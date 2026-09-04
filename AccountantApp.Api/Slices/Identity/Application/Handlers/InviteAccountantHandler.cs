using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

/// <summary>
/// Creates an Invited Accountant account and emails the invitation. AccountantAdmin only.
/// </summary>
public sealed class InviteAccountantHandler
{
    private const int DisplayNameMaximumLength = 200;

    private readonly IdentityDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly ITokenIssuing _tokens;
    private readonly TokenLinks _links;
    private readonly INotificationApi _notifications;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public InviteAccountantHandler(
        IdentityDbContext db,
        IPermissionChecker permissions,
        ITokenIssuing tokens,
        TokenLinks links,
        INotificationApi notifications,
        IRequestTransaction transaction,
        IAuditApi audit)
    {
        _db = db;
        _permissions = permissions;
        _tokens = tokens;
        _links = links;
        _notifications = notifications;
        _transaction = transaction;
        _audit = audit;
    }

    public async Task<AccountantDetailDto> Handle(
        InviteAccountantRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "InviteAccountant", ct: ct);

        // Accountant roles only. A CustomerAdmin or Employee here is a 422, not a silent coercion to
        // AccountantUser: Customer-side accounts need an employee_id and a customer_id that this
        // endpoint has no way to supply, so the row it would create violates ck_user_accounts_scope and
        // fails as a 500 instead.
        if (request.Role is not (UserRole.AccountantAdmin or UserRole.AccountantUser))
            throw new AppException(
                "An invited accountant must be an Accountant Admin or an Accountant User.", 422);

        var loginEmail = EmailNormalization.Require(request.Email);
        var normalizedEmail = EmailNormalization.Normalize(loginEmail);

        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (displayName.Length == 0)
            throw new AppException("A display name is required.", 422);
        if (displayName.Length > DisplayNameMaximumLength)
            throw new AppException(
                $"The display name must be at most {DisplayNameMaximumLength} characters long.", 422);

        var now = DateTimeOffset.UtcNow;

        // Transaction before the duplicate check, so the check and the insert see one snapshot. Two
        // concurrent invitations to the same address would otherwise both pass the check and the second
        // insert would hit uq_user_accounts_normalized_email as a 500 rather than a 409.
        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        // 409, and here the address IS disclosed -- unlike every unauthenticated path in this slice.
        // The caller is an authenticated Accountant Admin who can already list every account, so there
        // is nothing to leak, and an opaque error would leave them unable to tell "already invited"
        // from "something broke".
        var existing = await _db.UserAccounts
            .AnyAsync(item => item.NormalizedLoginEmail == normalizedEmail, ct);
        if (existing)
            throw new AppException($"An account already exists for '{normalizedEmail}'.", 409);

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            LoginEmail = loginEmail,
            NormalizedLoginEmail = normalizedEmail,

            // Null, not a hash of a random string. Null is what makes the account unable to
            // authenticate, and it is what ck_user_accounts_status ties to the Invited status. A
            // throwaway hash would mean the account is one lucky guess away from being usable and would
            // leave the Invited state describing something that is no longer true.
            PasswordHash = null,

            DisplayName = displayName,
            Role = request.Role,

            // Both null for Accountants, and that is enforced by ck_user_accounts_scope. Do not write
            // Guid.Empty: it satisfies "not null" and refers to a Customer that does not exist.
            EmployeeId = null,
            CustomerId = null,

            Status = AccountStatus.Invited,

            // False. The invitation flow has the person choose their own password, so there is nothing
            // to force a change of afterwards -- see AcceptInvitationHandler.
            MustChangePassword = false,

            EmailConfirmedAt = null,
            CreatedAt = now
        };
        _db.UserAccounts.Add(account);

        var rawToken = _tokens.GenerateRawToken();
        _db.Tokens.Add(new UserAccountToken
        {
            Id = Guid.NewGuid(),
            UserAccountId = account.Id,
            Purpose = TokenPurpose.Invitation,
            TokenHash = _tokens.HashToken(rawToken),
            ExpiresAt = now.Add(TokenPurpose.InvitationLifetime),
            CreatedAt = now
        });

        await _db.SaveChangesAsync(ct);

        // Raw token in EmailBody only; Body is stored and rendered in the app. Same rule as the reset
        // flow, and it matters more here because an invitation token lives for seven days.
        await _notifications.NotifyAsync(new NotificationRequest(
            account.Id.ToString(),
            NotificationEvents.Invited,
            "You have been invited",
            "An account has been created for you. Check your email to set your password.",
            EmailBody: $"Hello {account.DisplayName},\n\n"
                       + "An account has been created for you. Use this link to set your password:\n"
                       + $"{_links.AcceptInvitation(rawToken)}\n\n"
                       + $"The link is valid for {TokenPurpose.InvitationLifetime.TotalDays:0} days."), ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.AccountantAccountCreated,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            // Null: an Accountant belongs to no Customer, and inventing one here would file the
            // creation of an Office account under a client's history.
            null,
            // No Before -- the account did not exist. And no token, hashed or otherwise.
            After: new { account.LoginEmail, account.DisplayName, account.Role, account.Status }), ct);

        await _transaction.CommitAsync(ct);
        return IdentityMapper.ToDetailDto(account);
    }
}
