using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

/// <summary>
/// Changing one's OWN password. There is no endpoint for changing anybody else's -- Matrix section 11:
/// "Reset another person's password directly -- Nobody." The request DTO has no target user field, so
/// this handler cannot be pointed at another account even by mistake.
///
/// Available to every role, including a user whose must_change_password flag is set: this is the one
/// endpoint the change-password middleware must let through, or a first-time user is trapped in a loop
/// where the only thing they are allowed to do is the thing they are blocked from doing.
/// </summary>
public sealed class ChangeOwnPasswordHandler
{
    private readonly IdentityDbContext _db;
    private readonly IPasswordHashing _passwords;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IHttpContextAccessor _httpContext;

    public ChangeOwnPasswordHandler(
        IdentityDbContext db,
        IPasswordHashing passwords,
        IRequestTransaction transaction,
        IAuditApi audit,
        IHttpContextAccessor httpContext)
    {
        _db = db;
        _passwords = passwords;
        _transaction = transaction;
        _audit = audit;
        _httpContext = httpContext;
    }

    public async Task<MarkedResultDto> Handle(
        ChangePasswordRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        // No IPermissionChecker call. "Change my own password" is not in the action catalogue, because
        // it is not a permission that could be denied to anybody -- every authenticated user has it,
        // unconditionally. Adding a catalogue entry granted to all four roles would be a check that can
        // only ever pass.
        if (!Guid.TryParse(user.Id, out var accountId))
            throw new AppException("Not authenticated.", 401);

        var now = DateTimeOffset.UtcNow;
        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        var account = await _db.UserAccounts.FirstOrDefaultAsync(item => item.Id == accountId, ct)
            ?? throw new AppException("Not authenticated.", 401);

        // Validate the new password BEFORE verifying the current one, so a caller who typed a
        // too-short new password is told that, rather than being sent away with a 401 they will read as
        // "I got my old password wrong".
        PasswordPolicy.Validate(request.NewPassword, account.LoginEmail);

        // The current password is required even though the caller is already authenticated. The cookie
        // proves the session was opened by this user at some point; it does not prove the person at the
        // keyboard right now is them. An unattended browser is the whole threat model here.
        //
        // 401, not 403: it is a failed credential check, exactly like login.
        if (_passwords.Verify(account.PasswordHash, request.CurrentPassword) == PasswordVerification.Failed)
        {
            // Deliberately no FailedLoginCount increment and no lockout. Locking out on this path lets
            // anyone with brief access to an unlocked screen lock the real owner out of their account,
            // and the attacker already has a valid session, so the lockout protects nothing.
            await _audit.LogAsync(new AuditEntry(
                AuditActions.PasswordChanged,
                AuditTargets.UserAccount,
                account.Id.ToString(),
                account.CustomerId,
                AuditOutcome.Denied,
                After: new { Reason = "WrongCurrentPassword" }), ct);

            // Commit, THEN throw -- the audit entry is the record that somebody tried, and throwing
            // first would roll it back. Same rule as the login failure path.
            await _transaction.CommitAsync(ct);
            throw new AppException("The current password is incorrect.", 401);
        }

        if (string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
            throw new AppException("The new password must be different from the current one.", 422);

        account.PasswordHash = _passwords.Hash(request.NewPassword);
        account.LastPasswordChangeAt = now;

        // Clearing this is the point of the whole first-login flow. Leaving it set means the user
        // changes their password and is immediately asked to change it again.
        account.MustChangePassword = false;

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditEntry(
            AuditActions.PasswordChanged,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            account.CustomerId,
            // Before/After are omitted on purpose. There is nothing here that can be recorded: a
            // password hash must never reach the audit log, not even truncated, and "the password
            // changed" is fully expressed by the action name plus the timestamp.
            After: new { account.LastPasswordChangeAt }), ct);
        await _transaction.CommitAsync(ct);

        // Re-issue the cookie so must_change_password is false in the claims from here on. Skipping
        // this leaves the stale `true` in the cookie for up to eight hours, and the middleware reads
        // the CLAIM -- so the user would be redirected to the change-password screen on every request
        // until they log out and back in, having already changed it successfully.
        var httpContext = _httpContext.HttpContext
            ?? throw new InvalidOperationException("Changing a password requires an HTTP context.");
        await httpContext.SignInAsync(SessionClaims.Scheme, SessionClaims.Build(account));

        return MarkedResultDto.Done;
    }
}
