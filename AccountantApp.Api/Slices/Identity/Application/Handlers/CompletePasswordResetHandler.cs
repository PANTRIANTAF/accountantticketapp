using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

/// <summary>
/// Redeems a password-reset token. Unauthenticated: the whole point is that the caller cannot log in.
/// </summary>
public sealed class CompletePasswordResetHandler
{
    private const string InvalidTokenMessage = "That link is invalid or has expired.";

    private readonly IdentityDbContext _db;
    private readonly ITokenIssuing _tokens;
    private readonly IPasswordHashing _passwords;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public CompletePasswordResetHandler(
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
        CompletePasswordResetRequestDto request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            throw new AppException(InvalidTokenMessage, 400);

        var now = DateTimeOffset.UtcNow;

        // Hash the supplied token and look up BY HASH. Never load candidate rows to compare in memory,
        // and never store or log the raw value.
        var tokenHash = _tokens.HashToken(request.Token);

        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        var token = await _db.Tokens.FirstOrDefaultAsync(
            item => item.TokenHash == tokenHash && item.Purpose == TokenPurpose.PasswordReset, ct);

        // ONE message for all of: no such token, wrong purpose, already consumed, expired. Telling the
        // caller "this token has expired" versus "no such token" confirms that a token existed, which
        // confirms an account exists at whatever address they guessed.
        //
        // The purpose is part of the WHERE clause, not a later check: an invitation token must not be
        // redeemable here. Both live in one table with one unique hash index, so without this filter a
        // valid invitation token would complete a password reset -- skipping the email-confirmation
        // step the invitation flow exists to perform.
        if (token is null || !token.IsRedeemable(now))
            throw new AppException(InvalidTokenMessage, 400);

        var account = await _db.UserAccounts
            .FirstOrDefaultAsync(item => item.Id == token.UserAccountId, ct);

        // Suspended between requesting the reset and clicking the link. Same opaque message: a reset
        // must not be the way to discover that an account was disabled.
        if (account is null || account.Status != AccountStatus.Active)
            throw new AppException(InvalidTokenMessage, 400);

        PasswordPolicy.Validate(request.NewPassword, account.LoginEmail);

        account.PasswordHash = _passwords.Hash(request.NewPassword);
        account.LastPasswordChangeAt = now;

        // The person just chose this password themselves, so do not make them change it again.
        account.MustChangePassword = false;

        // Clear the lockout. Someone who was locked out by an attacker guessing at their account has
        // now proven control of the mailbox, and leaving the lockout in place means the reset appears to
        // succeed and the next login still fails for fifteen minutes with no explanation.
        account.FailedLoginCount = 0;
        account.LockoutExpiresAt = null;

        // Consume the token. Single-use is enforced by this write, not by deleting the row: the
        // consumed row is the evidence the reset happened, and the unique hash index still blocks reuse.
        token.ConsumedAt = now;

        await _db.SaveChangesAsync(ct);

        // LogUnauthenticatedAsync: the reset completes without a session, so there is no CurrentUser to
        // resolve. The actor identifier is the account's own email -- the mailbox holder is who acted.
        await _audit.LogUnauthenticatedAsync(account.NormalizedLoginEmail, new AuditEntry(
            AuditActions.PasswordResetCompleted,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            account.CustomerId,
            After: new { account.LastPasswordChangeAt, LockoutCleared = true }), ct);

        await _transaction.CommitAsync(ct);

        // Deliberately NOT signed in. Completing a reset does not create a session -- the caller is
        // sent to the login page to use the password they just chose. Signing them in here would mean a
        // leaked reset link grants a live session in one step, with nothing else needed.
        return MarkedResultDto.Done;
    }
}
