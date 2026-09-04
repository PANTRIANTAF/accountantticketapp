using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

/// <summary>
/// The one handler that runs before an identity exists, which is why its dependencies differ from
/// every other handler in the codebase.
///
/// It does NOT inject CurrentUser. CurrentUserFactory throws 401 when there is no authenticated
/// principal, so a CurrentUser parameter would make login fail with 401 before the body ever ran --
/// indistinguishable from a wrong password, and therefore very hard to diagnose.
///
/// It does NOT inject IPermissionChecker either. Logging in is not a permission; there is nobody to
/// check a permission for.
/// </summary>
public sealed class LoginHandler
{
    /// <summary>Consecutive failures that trigger a lockout.</summary>
    public const int MaximumFailedAttempts = 5;

    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// ONE message for every failure. Six different causes -- no such account, wrong password, still
    /// Invited, Suspended, locked out, owning Customer suspended -- and the response must be byte-for
    /// -byte identical for all of them, because any distinction is a way to ask whether an address has
    /// an account here. The audit log records which cause it actually was; the caller does not learn it.
    /// </summary>
    private const string FailureMessage = "Invalid email or password.";

    private readonly IdentityDbContext _db;
    private readonly IPasswordHashing _passwords;
    private readonly ICustomerApi _customers;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IHttpContextAccessor _httpContext;

    public LoginHandler(
        IdentityDbContext db,
        IPasswordHashing passwords,
        ICustomerApi customers,
        IRequestTransaction transaction,
        IAuditApi audit,
        IHttpContextAccessor httpContext)
    {
        _db = db;
        _passwords = passwords;
        _customers = customers;
        _transaction = transaction;
        _audit = audit;
        _httpContext = httpContext;
    }

    public async Task<SessionDto> Handle(LoginRequestDto request, CancellationToken ct)
    {
        var normalizedEmail = EmailNormalization.Normalize(request.Email);
        var password = request.Password ?? string.Empty;
        var now = DateTimeOffset.UtcNow;

        // The transaction opens before the read because the failure path WRITES -- it increments the
        // failure counter -- and that write has to be in the same transaction as the read that decided
        // to make it.
        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        var account = await _db.UserAccounts
            .FirstOrDefaultAsync(item => item.NormalizedLoginEmail == normalizedEmail, ct);

        // Verify unconditionally, including when there is no account, and BEFORE any branch on
        // account state. Verify(null, ...) does the full PBKDF2 work and returns Failed, so an
        // unknown address costs the same ~100ms as a known one. Skipping this call when `account` is
        // null is the account-enumeration bug this design exists to prevent, and it is invisible in
        // every functional test -- the status codes are identical either way.
        var verification = _passwords.Verify(account?.PasswordHash, password);

        if (account is null)
            throw await RecordFailureAsync(normalizedEmail, null, "NoSuchAccount", ct);

        // Lockout is checked BEFORE the password, and a locked-out attempt does NOT increment the
        // counter. Incrementing during a lockout lets an attacker who keeps trying extend the lockout
        // indefinitely -- the legitimate owner can then never get back in, so a brute-force defence
        // becomes a denial-of-service against the victim.
        if (account.IsLockedOut(now))
            throw await RecordFailureAsync(normalizedEmail, account, "LockedOut", ct);

        if (verification == PasswordVerification.Failed)
        {
            account.FailedLoginCount += 1;

            if (account.FailedLoginCount >= MaximumFailedAttempts)
            {
                account.LockoutExpiresAt = now.Add(LockoutDuration);

                // Reset to 0 as the lockout is applied. Leaving it at 5 means the next single failure
                // after the lockout expires re-locks the account immediately, and the account is
                // effectively locked forever.
                account.FailedLoginCount = 0;

                await _db.SaveChangesAsync(ct);
                await _audit.LogUnauthenticatedAsync(normalizedEmail, new AuditEntry(
                    AuditActions.AccountLockedOut,
                    AuditTargets.UserAccount,
                    account.Id.ToString(),
                    account.CustomerId,
                    AuditOutcome.Denied,
                    After: new { account.LockoutExpiresAt, LockoutMinutes = LockoutDuration.TotalMinutes }), ct);
            }

            throw await RecordFailureAsync(normalizedEmail, account, "BadPassword", ct);
        }

        // Status is checked after the password on purpose: an Invited or Suspended account with the
        // wrong password must be indistinguishable from an Active one with the wrong password.
        if (account.Status != AccountStatus.Active)
            throw await RecordFailureAsync(normalizedEmail, account, $"Status:{account.Status}", ct);

        // Read live, on every login, for the two Customer-side roles only. Not a column and not
        // cached: suspending a Customer must stop its people logging in on the very next attempt.
        // Accountants skip this entirely -- their CustomerId is null, and calling IsActiveAsync with
        // Guid.Empty would return false and lock the whole Office out.
        if (!account.IsAccountant)
        {
            if (account.CustomerId is not { } customerId)
                throw await RecordFailureAsync(normalizedEmail, account, "MissingCustomerId", ct);
            if (!await _customers.IsActiveAsync(customerId, ct))
                throw await RecordFailureAsync(normalizedEmail, account, "CustomerNotActive", ct);
        }

        // Rehash when the stored hash used an older format. This is the only moment the plaintext is
        // available, so an ignored SuccessRehashNeeded means the upgrade never happens for this
        // account -- not now, not ever.
        if (verification == PasswordVerification.SuccessRehashNeeded)
            account.PasswordHash = _passwords.Hash(password);

        account.FailedLoginCount = 0;
        account.LockoutExpiresAt = null;
        account.LastLoginAt = now;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.LoginSucceeded,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            account.CustomerId,
            After: new { account.Role, Rehashed = verification == PasswordVerification.SuccessRehashNeeded }), ct);

        // Commit BEFORE issuing the cookie. SignInAsync is not part of the transaction and cannot be
        // rolled back: if the commit failed afterwards, the caller would hold a valid cookie for a
        // login the database never recorded.
        await _transaction.CommitAsync(ct);

        var httpContext = _httpContext.HttpContext
            ?? throw new InvalidOperationException("Login requires an HTTP context.");
        await httpContext.SignInAsync(SessionClaims.Scheme, SessionClaims.Build(account));

        return SessionClaims.ToSessionDto(account);
    }

    /// <summary>
    /// Records the failure, COMMITS, and throws the one identical 401.
    ///
    /// The commit is the whole point. IRequestTransaction.DisposeAsync rolls back when CommitAsync was
    /// never called, so throwing directly out of the failure path discards the FailedLoginCount
    /// increment that was just written -- the counter never advances past 1, no account is ever locked
    /// out, and brute-force protection does not exist. Every status-code test still passes. This is the
    /// single most important line in this slice.
    ///
    /// RETURNS the exception rather than throwing it, so every call site reads
    /// `throw await RecordFailureAsync(...)`. That shape is not a stylistic choice: an
    /// `await FailAsync(...)` that threw internally would leave the compiler believing control can
    /// continue, so `account` would stay nullable for the rest of the method and the null-check above
    /// would appear not to narrow it. Do not "simplify" this by throwing inside.
    /// </summary>
    private async Task<AppException> RecordFailureAsync(
        string normalizedEmail,
        UserAccount? account,
        string reason,
        CancellationToken ct)
    {
        // LogUnauthenticatedAsync, never LogAsync: there is no CurrentUser here, and LogAsync would
        // resolve one and throw 401 from inside the audit call -- turning a clean 401 into a confusing
        // one raised from the wrong place.
        await _audit.LogUnauthenticatedAsync(normalizedEmail, new AuditEntry(
            AuditActions.LoginFailed,
            AuditTargets.UserAccount,
            account?.Id.ToString() ?? normalizedEmail,
            account?.CustomerId,
            AuditOutcome.Denied,
            // The reason goes in the audit log and ONLY in the audit log. It must never reach the
            // response body, a response header, or the exception message.
            After: new { Reason = reason }), ct);

        await _transaction.CommitAsync(ct);
        return new AppException(FailureMessage, 401);
    }
}
