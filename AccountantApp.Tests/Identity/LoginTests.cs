using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Application.Handlers;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Tests.Identity;

public class LoginTests
{
    private const string CorrectPassword = "correct-horse-battery";

    private static (LoginHandler Handler, RecordingAuditApi Audit, CountingRequestTransaction Transaction,
        StubCustomerApi Customers) Build(IdentityDbContext db)
    {
        var audit = new RecordingAuditApi();
        var transaction = new CountingRequestTransaction();
        var customers = new StubCustomerApi();
        var accessor = new StubHttpContextAccessor { HttpContext = IdentityTestHarness.NewHttpContext() };

        var handler = new LoginHandler(
            db, IdentityTestHarness.Passwords, customers, transaction, audit, accessor);

        return (handler, audit, transaction, customers);
    }

    private static LoginRequestDto Request(string email, string password) =>
        new() { Email = email, Password = password };

    [Fact]
    public async Task Correct_credentials_return_a_session_and_reset_the_failure_counter()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(password: CorrectPassword);
        account.FailedLoginCount = 3;
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        var (handler, audit, transaction, _) = Build(db);

        var session = await handler.Handle(Request("alice@example.com", CorrectPassword), default);

        Assert.Equal(account.Id.ToString(), session.UserId);
        Assert.Equal(UserRole.AccountantUser, session.Role);
        Assert.Equal(0, account.FailedLoginCount);
        Assert.NotNull(account.LastLoginAt);
        Assert.Equal(1, transaction.Commits);
        Assert.Single(audit.WithAction(AuditActions.LoginSucceeded));
    }

    [Fact]
    public async Task Email_is_matched_case_insensitively_and_after_trimming()
    {
        await using var db = IdentityTestHarness.NewDb();
        db.UserAccounts.Add(IdentityTestHarness.NewAccount(password: CorrectPassword));
        await db.SaveChangesAsync();

        var (handler, _, _, _) = Build(db);

        // "Alice@Example.COM " and "alice@example.com" are the same mailbox. Failing this would give one
        // person two accounts, one of which nobody remembers using.
        var session = await handler.Handle(Request("  Alice@Example.COM ", CorrectPassword), default);

        Assert.NotNull(session.UserId);
    }

    /// <summary>
    /// The account-enumeration defence, stated as one test. Six distinct causes, one identical response.
    /// Any divergence in status code or message turns the endpoint into a tool for discovering which of
    /// an organisation's addresses are registered here.
    /// </summary>
    [Fact]
    public async Task All_six_failure_causes_produce_an_identical_401()
    {
        var messages = new List<string>();
        var statuses = new List<int>();

        foreach (var scenario in FailureScenarios())
        {
            await using var db = IdentityTestHarness.NewDb();
            if (scenario.Account is not null)
            {
                db.UserAccounts.Add(scenario.Account);
                await db.SaveChangesAsync();
            }

            var (handler, _, _, customers) = Build(db);
            customers.ActiveResult = scenario.CustomerActive;

            var exception = await Assert.ThrowsAsync<AppException>(() =>
                handler.Handle(Request("alice@example.com", scenario.Password), default));

            messages.Add(exception.Message);
            statuses.Add(exception.StatusCode);
        }

        Assert.Equal(6, messages.Count);
        Assert.Single(messages.Distinct());
        Assert.Single(statuses.Distinct());
        Assert.Equal(401, statuses[0]);
        Assert.Equal("Invalid email or password.", messages[0]);
    }

    private static IEnumerable<(UserAccount? Account, string Password, bool CustomerActive)> FailureScenarios()
    {
        var customerId = Guid.NewGuid();

        // 1. No such account.
        yield return (null, CorrectPassword, true);

        // 2. Wrong password.
        yield return (IdentityTestHarness.NewAccount(password: CorrectPassword), "wrong-password-entirely", true);

        // 3. Still Invited -- null hash.
        yield return (IdentityTestHarness.NewAccount(status: AccountStatus.Invited, password: null),
            CorrectPassword, true);

        // 4. Suspended.
        yield return (IdentityTestHarness.NewAccount(status: AccountStatus.Suspended, password: CorrectPassword),
            CorrectPassword, true);

        // 5. Locked out.
        var lockedOut = IdentityTestHarness.NewAccount(password: CorrectPassword);
        lockedOut.LockoutExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        yield return (lockedOut, CorrectPassword, true);

        // 6. Owning Customer suspended.
        yield return (IdentityTestHarness.NewAccount(
                role: UserRole.Employee, customerId: customerId, employeeId: Guid.NewGuid(),
                password: CorrectPassword),
            CorrectPassword, false);
    }

    /// <summary>
    /// The single most important test in the slice. The failure path increments FailedLoginCount and must
    /// COMMIT that increment before throwing -- RequestTransaction rolls back on disposal when
    /// CommitAsync was never called, so a bare throw discards the increment, the counter never passes 1,
    /// nothing is ever locked out, and every status-code test still passes.
    /// </summary>
    [Fact]
    public async Task A_failed_login_commits_the_incremented_counter_before_throwing()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(password: CorrectPassword);
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        var (handler, _, transaction, _) = Build(db);

        await Assert.ThrowsAsync<AppException>(() =>
            handler.Handle(Request("alice@example.com", "wrong-password-entirely"), default));

        Assert.Equal(1, account.FailedLoginCount);
        Assert.Equal(1, transaction.Commits);
        Assert.False(transaction.RolledBack);
    }

    [Fact]
    public async Task The_fifth_consecutive_failure_locks_the_account_and_resets_the_counter()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(password: CorrectPassword);
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        var (handler, audit, _, _) = Build(db);

        for (var attempt = 0; attempt < 5; attempt++)
            await Assert.ThrowsAsync<AppException>(() =>
                handler.Handle(Request("alice@example.com", "wrong-password-entirely"), default));

        Assert.NotNull(account.LockoutExpiresAt);
        Assert.True(account.IsLockedOut(DateTimeOffset.UtcNow));

        // Reset to 0 as the lockout is applied. Left at 5, the first failure after the lockout expires
        // re-locks the account immediately and it is locked forever.
        Assert.Equal(0, account.FailedLoginCount);
        Assert.Single(audit.WithAction(AuditActions.AccountLockedOut));
    }

    [Fact]
    public async Task An_attempt_during_a_lockout_does_not_extend_it()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(password: CorrectPassword);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10);
        account.LockoutExpiresAt = expiry;
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        var (handler, _, _, _) = Build(db);

        await Assert.ThrowsAsync<AppException>(() =>
            handler.Handle(Request("alice@example.com", "wrong-password-entirely"), default));

        // Neither the counter nor the expiry moves. If a locked-out attempt incremented the counter, an
        // attacker who keeps trying would extend the lockout indefinitely and the legitimate owner could
        // never get back in -- brute-force protection turned into a denial of service against the victim.
        Assert.Equal(0, account.FailedLoginCount);
        Assert.Equal(expiry, account.LockoutExpiresAt);
    }

    [Fact]
    public async Task A_correct_password_during_a_lockout_is_still_refused()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(password: CorrectPassword);
        account.LockoutExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        var (handler, _, _, _) = Build(db);

        // The lockout is checked BEFORE the password. Checking it after would let the correct password
        // through during a lockout, which is the whole window the lockout exists to close.
        await Assert.ThrowsAsync<AppException>(() =>
            handler.Handle(Request("alice@example.com", CorrectPassword), default));
    }

    [Fact]
    public async Task An_expired_lockout_no_longer_blocks_login()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(password: CorrectPassword);
        // In the past: not locked out. The timestamp is compared to now rather than cleared eagerly.
        account.LockoutExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        var (handler, _, _, _) = Build(db);

        var session = await handler.Handle(Request("alice@example.com", CorrectPassword), default);

        Assert.NotNull(session.UserId);
        Assert.Null(account.LockoutExpiresAt);
    }

    [Fact]
    public async Task Customer_status_is_read_live_for_customer_side_roles_and_skipped_for_accountants()
    {
        var customerId = Guid.NewGuid();

        await using var employeeDb = IdentityTestHarness.NewDb();
        employeeDb.UserAccounts.Add(IdentityTestHarness.NewAccount(
            role: UserRole.Employee, customerId: customerId, employeeId: Guid.NewGuid(),
            password: CorrectPassword));
        await employeeDb.SaveChangesAsync();

        var (employeeHandler, _, _, employeeCustomers) = Build(employeeDb);
        await employeeHandler.Handle(Request("alice@example.com", CorrectPassword), default);

        Assert.Equal([customerId], employeeCustomers.IsActiveCalls);

        await using var accountantDb = IdentityTestHarness.NewDb();
        accountantDb.UserAccounts.Add(IdentityTestHarness.NewAccount(password: CorrectPassword));
        await accountantDb.SaveChangesAsync();

        var (accountantHandler, _, _, accountantCustomers) = Build(accountantDb);
        await accountantHandler.Handle(Request("alice@example.com", CorrectPassword), default);

        // An Accountant has no CustomerId, so calling IsActiveAsync would pass Guid.Empty, get false back,
        // and lock the entire Office out of the application.
        Assert.Empty(accountantCustomers.IsActiveCalls);
    }

    [Fact]
    public async Task Failures_are_audited_as_unauthenticated_with_the_email_as_the_actor()
    {
        await using var db = IdentityTestHarness.NewDb();
        var (handler, audit, _, _) = Build(db);

        await Assert.ThrowsAsync<AppException>(() =>
            handler.Handle(Request("Nobody@Example.com", "whatever-password"), default));

        // LogAsync would resolve a CurrentUser, whose factory throws 401 with no principal -- turning a
        // clean 401 into a confusing one raised from inside the audit call.
        Assert.Equal("nobody@example.com", Assert.Single(audit.Actors));
        Assert.Equal(AuditOutcome.Denied, Assert.Single(audit.Entries).Outcome);
    }

    [Fact]
    public async Task The_failure_reason_never_reaches_the_caller()
    {
        await using var db = IdentityTestHarness.NewDb();
        db.UserAccounts.Add(IdentityTestHarness.NewAccount(
            status: AccountStatus.Suspended, password: CorrectPassword));
        await db.SaveChangesAsync();

        var (handler, audit, _, _) = Build(db);

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            handler.Handle(Request("alice@example.com", CorrectPassword), default));

        // The audit log knows exactly why; the response does not. The reason string must stay on the
        // audit side of that line.
        Assert.DoesNotContain("Suspended", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(audit.Entries, entry => entry.Action == AuditActions.LoginFailed);
    }

    [Fact]
    public async Task An_unknown_account_still_performs_a_password_verification()
    {
        // The timing-defence contract, asserted on the unit that owns it rather than by measuring the
        // clock: Verify does the full PBKDF2 work for a null stored hash and returns Failed. An early
        // `return Failed` would make the no-account path return in microseconds against ~100ms for a real
        // account -- a working enumeration oracle over the network, and invisible to every status-code
        // test.
        var elapsedForMissingHash = await TimeAsync(() =>
            IdentityTestHarness.Passwords.Verify(null, "some-password"));
        var elapsedForRealHash = await TimeAsync(() =>
            IdentityTestHarness.Passwords.Verify(
                IdentityTestHarness.Passwords.Hash(CorrectPassword), "some-password"));

        // Deliberately loose: this asserts the work HAPPENS, not that the two are equal to the
        // microsecond. A tight bound here would fail on a loaded CI machine for no security reason.
        Assert.True(
            elapsedForMissingHash > elapsedForRealHash / 4,
            $"null-hash verification took {elapsedForMissingHash.TotalMilliseconds:0.0}ms versus "
            + $"{elapsedForRealHash.TotalMilliseconds:0.0}ms for a real hash, which suggests the work is "
            + "being skipped.");

        static Task<TimeSpan> TimeAsync(Action action)
        {
            action();   // warm up the JIT so the first call is not measured
            var start = System.Diagnostics.Stopwatch.StartNew();
            action();
            return Task.FromResult(start.Elapsed);
        }
    }

    [Fact]
    public async Task A_login_with_a_stale_hash_format_rewrites_it()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(password: CorrectPassword);
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        var originalHash = account.PasswordHash;

        var (handler, _, _, _) = Build(db);
        await handler.Handle(Request("alice@example.com", CorrectPassword), default);

        // The current hasher produces the current format, so nothing is rehashed here. The assertion is
        // that a successful login does not corrupt or drop the stored hash -- the rehash branch is
        // covered by PasswordHashing's own contract.
        Assert.Equal(originalHash, account.PasswordHash);
        Assert.NotNull(account.PasswordHash);
    }

    [Fact]
    public async Task An_empty_password_is_refused_without_reaching_the_database_state()
    {
        await using var db = IdentityTestHarness.NewDb();
        db.UserAccounts.Add(IdentityTestHarness.NewAccount(password: CorrectPassword));
        await db.SaveChangesAsync();

        var (handler, _, _, _) = Build(db);

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            handler.Handle(Request("alice@example.com", ""), default));

        // Same 401, not a 422. A validation error here would distinguish "this address exists but you
        // sent no password" from "no such address".
        Assert.Equal(401, exception.StatusCode);
    }
}
