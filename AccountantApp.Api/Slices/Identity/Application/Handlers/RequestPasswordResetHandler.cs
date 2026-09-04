using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

/// <summary>
/// Unauthenticated, like login. Injects no CurrentUser and no IPermissionChecker for the same reason.
/// </summary>
public sealed class RequestPasswordResetHandler
{
    private readonly IdentityDbContext _db;
    private readonly ITokenIssuing _tokens;
    private readonly TokenLinks _links;
    private readonly INotificationApi _notifications;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public RequestPasswordResetHandler(
        IdentityDbContext db,
        ITokenIssuing tokens,
        TokenLinks links,
        INotificationApi notifications,
        IRequestTransaction transaction,
        IAuditApi audit)
    {
        _db = db;
        _tokens = tokens;
        _links = links;
        _notifications = notifications;
        _transaction = transaction;
        _audit = audit;
    }

    public async Task<MarkedResultDto> Handle(
        RequestPasswordResetRequestDto request,
        CancellationToken ct)
    {
        var normalizedEmail = EmailNormalization.Normalize(request.Email);
        var now = DateTimeOffset.UtcNow;

        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        var account = await _db.UserAccounts
            .FirstOrDefaultAsync(item => item.NormalizedLoginEmail == normalizedEmail, ct);

        // ALWAYS 200 with the same body, whether or not the address has an account and whether or not
        // that account can be reset. This endpoint is unauthenticated and accepts an arbitrary email,
        // so a 404 for an unknown address turns it into a free tool for testing which of a company's
        // addresses are registered here.
        //
        // Do not "improve the user experience" by returning "no account found". The front end shows a
        // neutral "if that address has an account, check your email" message, and that is the whole
        // design.
        //
        // Also do NOT validate the address format with EmailNormalization.Require here: a 422 for a
        // malformed address and a 200 for a well-formed unknown one is the same oracle, just quieter.
        if (account is null || account.Status != AccountStatus.Active)
        {
            await _audit.LogUnauthenticatedAsync(normalizedEmail, new AuditEntry(
                AuditActions.PasswordResetRequested,
                AuditTargets.UserAccount,
                account?.Id.ToString() ?? normalizedEmail,
                account?.CustomerId,
                AuditOutcome.Denied,
                After: new { Reason = account is null ? "NoSuchAccount" : $"Status:{account.Status}" }), ct);

            // Commit so the audit entry survives, then return -- do not throw. A thrown 404 would be
            // the leak; a rolled-back audit entry would mean a targeted enumeration sweep leaves no
            // trace at all, which is exactly the attack this log is for.
            await _transaction.CommitAsync(ct);
            return MarkedResultDto.Done;
        }

        // Invalidate every outstanding reset token for this account before issuing a new one. Without
        // this, asking three times leaves three working tokens for an hour each, so the window an
        // attacker can hunt in is as wide as the user is impatient.
        //
        // Invitation tokens are deliberately untouched: a pending invitation is a different flow, and
        // consuming it here would leave a half-onboarded account nobody can finish setting up.
        var outstanding = await _db.Tokens
            .Where(token => token.UserAccountId == account.Id
                            && token.Purpose == TokenPurpose.PasswordReset
                            && token.ConsumedAt == null)
            .ToListAsync(ct);
        foreach (var token in outstanding)
            token.ConsumedAt = now;

        var rawToken = _tokens.GenerateRawToken();
        _db.Tokens.Add(new UserAccountToken
        {
            Id = Guid.NewGuid(),
            UserAccountId = account.Id,
            Purpose = TokenPurpose.PasswordReset,
            // Only the hash. The raw token below goes into the email body and is then unreachable.
            TokenHash = _tokens.HashToken(rawToken),
            ExpiresAt = now.Add(TokenPurpose.PasswordResetLifetime),
            CreatedAt = now
        });

        await _db.SaveChangesAsync(ct);

        // The raw token appears in EmailBody ONLY. Body is what is stored on the notification row and
        // rendered in the application, and a stored raw token defeats the entire hash-only design --
        // anyone who can read the notifications table can reset anyone's password.
        await _notifications.NotifyAsync(new NotificationRequest(
            account.Id.ToString(),
            NotificationEvents.PasswordResetRequested,
            "Password reset requested",
            "A password reset was requested for your account. Check your email for the link.",
            EmailBody: $"A password reset was requested for your account.\n\n"
                       + $"Use this link within {TokenPurpose.PasswordResetLifetime.TotalMinutes:0} minutes:\n"
                       + $"{_links.CompletePasswordReset(rawToken)}\n\n"
                       + "If you did not request this, you can ignore this message; your password has not changed."), ct);

        await _audit.LogUnauthenticatedAsync(normalizedEmail, new AuditEntry(
            AuditActions.PasswordResetRequested,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            account.CustomerId,
            // Never the raw token and never its hash. "A reset was requested for this account at this
            // time" is the whole audit-worthy fact.
            After: new { InvalidatedOutstanding = outstanding.Count }), ct);

        await _transaction.CommitAsync(ct);
        return MarkedResultDto.Done;
    }
}
