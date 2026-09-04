using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

/// <summary>
/// Turns an Invited account into an Active one. This is the ONLY path that sets a first password, and
/// the only one that sets EmailConfirmedAt -- redeeming the token IS the proof the address works.
/// </summary>
public sealed class AcceptInvitationHandler
{
    private const string InvalidTokenMessage = "That invitation is invalid or has expired.";

    /// <summary>Same cap as the invite endpoint. A display name is a label, not a document.</summary>
    private const int DisplayNameMaximumLength = 200;

    private readonly IdentityDbContext _db;
    private readonly ITokenIssuing _tokens;
    private readonly IPasswordHashing _passwords;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public AcceptInvitationHandler(
        IdentityDbContext db,
        ITokenIssuing tokens,
        IPasswordHashing passwords,
        IRequestTransaction transaction,
        IAuditApi audit)
    {
        _db = db;
        _tokens = tokens;
        _passwords = passwords;
        _transaction = transaction;
        _audit = audit;
    }

    public async Task<MarkedResultDto> Handle(
        AcceptInvitationRequestDto request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            throw new AppException(InvalidTokenMessage, 400);

        var now = DateTimeOffset.UtcNow;
        var tokenHash = _tokens.HashToken(request.Token);

        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        // Purpose in the WHERE clause, as in the reset handler: a password-reset token must not be
        // redeemable as an invitation. It would activate the account and confirm the email address
        // without either having been proven by the invitation itself.
        var token = await _db.Tokens.FirstOrDefaultAsync(
            item => item.TokenHash == tokenHash && item.Purpose == TokenPurpose.Invitation, ct);

        if (token is null || !token.IsRedeemable(now))
            throw new AppException(InvalidTokenMessage, 400);

        var account = await _db.UserAccounts
            .FirstOrDefaultAsync(item => item.Id == token.UserAccountId, ct);

        if (account is null)
            throw new AppException(InvalidTokenMessage, 400);

        // The account must still be Invited. An Active account reaching this path means the invitation
        // was already accepted and this is a replayed link, or the account was invited, activated,
        // suspended and reactivated -- and in every one of those cases accepting again would reset a
        // working account's password using a link from an old email. Same opaque message.
        if (account.Status != AccountStatus.Invited)
            throw new AppException(InvalidTokenMessage, 400);

        PasswordPolicy.Validate(request.NewPassword, account.LoginEmail);

        // Absent means keep what the inviter typed. An empty or whitespace-only string is the same as
        // absent, NOT an instruction to blank the name -- a nameless account shows up as a blank space
        // in every assignment dropdown and message header in the system.
        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            var displayName = request.DisplayName.Trim();
            if (displayName.Length > DisplayNameMaximumLength)
                throw new AppException(
                    $"The display name must be at most {DisplayNameMaximumLength} characters long.", 422);
            account.DisplayName = displayName;
        }

        account.PasswordHash = _passwords.Hash(request.NewPassword);
        account.Status = AccountStatus.Active;

        // Redeeming a token that only ever existed in an email sent to that address IS the confirmation.
        // There is no separate confirm-your-email step, and adding one would ask the person to prove
        // twice, by the same means, something they have already proven.
        account.EmailConfirmedAt = now;
        account.LastPasswordChangeAt = now;

        // False, not true. The password was chosen by the person who owns the account, not assigned to
        // them, so there is nothing to force a change of. Leaving this true would send a user who has
        // just set their password straight to the change-password screen.
        account.MustChangePassword = false;

        token.ConsumedAt = now;

        await _db.SaveChangesAsync(ct);
        await _audit.LogUnauthenticatedAsync(account.NormalizedLoginEmail, new AuditEntry(
            AuditActions.InvitationAccepted,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            account.CustomerId,
            After: new { account.Status, account.EmailConfirmedAt, account.DisplayName }), ct);

        await _transaction.CommitAsync(ct);

        // Not signed in, for the same reason as CompletePasswordReset: the caller goes to the login page
        // and uses the password they just chose.
        return MarkedResultDto.Done;
    }
}
