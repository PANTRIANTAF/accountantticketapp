using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Seeding;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application;
using AccountantApp.Api.Slices.Identity.Application.Handlers;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AccountantApp.Tests.Identity;

public class DatabaseSeederTests
{
    /// <summary>
    /// A provider holding just what SeedAsync resolves. Built by hand rather than through
    /// AddIdentitySlice, because the real registration wires a Postgres connection that is not available
    /// here -- and the seeder's contract is about configuration and idempotence, not about Npgsql.
    /// </summary>
    private static (IServiceProvider Services, IdentityDbContext Db, RecordingAuditApi Audit,
        CountingRequestTransaction Transaction) BuildProvider(
        string? email, string? password, IdentityDbContext? existingDb = null)
    {
        var db = existingDb ?? IdentityTestHarness.NewDb();
        var audit = new RecordingAuditApi();
        var transaction = new CountingRequestTransaction();

        var settings = new Dictionary<string, string?>();
        if (email is not null) settings["Seeding:FirstAdminEmail"] = email;
        if (password is not null) settings["Seeding:FirstAdminPassword"] = password;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton(db);
        services.AddSingleton(IdentityTestHarness.Passwords);
        services.AddSingleton<IRequestTransaction>(transaction);
        services.AddSingleton<IAuditApi>(audit);

        return (services.BuildServiceProvider(), db, audit, transaction);
    }

    [Fact]
    public async Task A_fresh_database_gets_one_active_admin_that_must_change_its_password()
    {
        var (services, db, audit, transaction) =
            BuildProvider("first.admin@example.com", "a-seeded-admin-password");

        await DatabaseSeeder.SeedAsync(services);

        var admin = await db.UserAccounts.SingleAsync();
        Assert.Equal(UserRole.AccountantAdmin, admin.Role);

        // Active, not Invited. There is no mail transport necessarily configured on a first run and nobody
        // to send to, so an Invited first Admin could never accept and the application would be permanently
        // unusable.
        Assert.Equal(AccountStatus.Active, admin.Status);

        // TRUE, and this is the whole reason MustChangePasswordMiddleware exists. The seeded password came
        // from an environment variable -- visible in `docker inspect`, in shell history, and in the compose
        // file -- so it must not remain usable for anything except replacing itself.
        Assert.True(admin.MustChangePassword);

        // Null: nothing has proven this mailbox works. The invitation flow normally sets it, and this
        // account skipped that flow by construction.
        Assert.Null(admin.EmailConfirmedAt);

        Assert.Null(admin.CustomerId);
        Assert.Null(admin.EmployeeId);
        Assert.Equal(1, transaction.Commits);
    }

    [Fact]
    public async Task Missing_configuration_fails_startup_rather_than_using_a_default_password()
    {
        foreach (var (email, password) in new[]
                 {
                     ((string?)null, (string?)null),
                     ("admin@example.com", null),
                     (null, "a-seeded-admin-password"),
                     ("admin@example.com", "   "),
                     ("   ", "a-seeded-admin-password")
                 })
        {
            var (services, db, _, _) = BuildProvider(email, password);

            // There is deliberately NO built-in default. A default admin password is the most exploited
            // misconfiguration in self-hosted software, and one that works on a first run is one nobody
            // ever discovers is there. Blank counts as missing because an unset environment variable
            // arrives as "" through the configuration binder, not as null.
            await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseSeeder.SeedAsync(services));

            Assert.Equal(0, await db.UserAccounts.CountAsync());
        }
    }

    [Fact]
    public async Task A_seeded_password_the_policy_would_reject_fails_before_anything_is_written()
    {
        var (services, db, _, transaction) = BuildProvider("first.admin@example.com", "short");

        // Validated BEFORE hashing and inserting. A seeded password the policy rejects could never be
        // changed to something acceptable -- the change-password flow applies the same policy -- so the
        // account would be permanently stuck, and the failure would surface only at first login as a 422
        // nobody can act on.
        await Assert.ThrowsAnyAsync<Exception>(() => DatabaseSeeder.SeedAsync(services));

        Assert.Equal(0, await db.UserAccounts.CountAsync());
        Assert.Equal(0, transaction.Commits);
    }

    [Fact]
    public async Task Seeding_twice_does_not_reset_the_existing_admins_password()
    {
        await using var db = IdentityTestHarness.NewDb();

        var (firstRun, _, _, _) = BuildProvider("first.admin@example.com", "a-seeded-admin-password", db);
        await DatabaseSeeder.SeedAsync(firstRun);

        var admin = await db.UserAccounts.SingleAsync();
        // The Admin does what they were forced to do: changes the password away from the seeded one.
        admin.PasswordHash = IdentityTestHarness.Passwords.Hash("the-password-they-actually-chose");
        admin.MustChangePassword = false;
        await db.SaveChangesAsync();
        var chosenHash = admin.PasswordHash;

        // A redeploy runs startup again with the same environment variables still set.
        var (secondRun, _, secondAudit, secondTransaction) =
            BuildProvider("first.admin@example.com", "a-seeded-admin-password", db);
        await DatabaseSeeder.SeedAsync(secondRun);

        // Re-seeding would silently undo the change they were required to make, restoring a password that
        // is visible in the deployment configuration -- on every single deploy.
        Assert.Equal(chosenHash, admin.PasswordHash);
        Assert.False(admin.MustChangePassword);
        Assert.Equal(1, await db.UserAccounts.CountAsync());
        Assert.Empty(secondAudit.Entries);
        Assert.Equal(0, secondTransaction.Commits);
    }

    [Fact]
    public async Task A_suspended_admin_still_counts_so_a_second_one_is_not_created()
    {
        await using var db = IdentityTestHarness.NewDb();
        db.UserAccounts.Add(IdentityTestHarness.NewAccount(
            email: "existing.admin@example.com", role: UserRole.AccountantAdmin,
            status: AccountStatus.Suspended));
        await db.SaveChangesAsync();

        var (services, _, _, _) = BuildProvider("first.admin@example.com", "a-seeded-admin-password", db);
        await DatabaseSeeder.SeedAsync(services);

        // Any status counts. Otherwise every restart would add another Admin alongside the suspended one,
        // and the fix for a suspended sole Admin is not "quietly grow a second account on each reboot".
        Assert.Equal(1, await db.UserAccounts.CountAsync());
    }

    [Fact]
    public async Task A_database_with_employees_but_no_admin_is_still_seeded()
    {
        await using var db = IdentityTestHarness.NewDb();
        db.UserAccounts.Add(IdentityTestHarness.NewAccount(
            email: "employee@customer.example.com", role: UserRole.Employee,
            customerId: Guid.NewGuid(), employeeId: Guid.NewGuid()));
        db.UserAccounts.Add(IdentityTestHarness.NewAccount(
            email: "staff@example.com", role: UserRole.AccountantUser));
        await db.SaveChangesAsync();

        var (services, _, _, _) = BuildProvider("first.admin@example.com", "a-seeded-admin-password", db);
        await DatabaseSeeder.SeedAsync(services);

        // The condition is "no AccountantAdmin exists", NOT "the table is empty". A database with accounts
        // but no Admin is exactly the unrecoverable state the last-Admin invariant exists to prevent, and
        // seeding is the only way out of it -- a table-empty check would decline to help precisely then.
        Assert.Equal(1, await db.UserAccounts.CountAsync(
            account => account.Role == UserRole.AccountantAdmin));
    }

    [Fact]
    public async Task The_seed_is_audited_as_unauthenticated_and_records_no_password()
    {
        var (services, _, audit, _) =
            BuildProvider("First.Admin@Example.COM", "a-seeded-admin-password");

        await DatabaseSeeder.SeedAsync(services);

        // LogUnauthenticatedAsync, not LogAsync. There is no CurrentUser in a startup scope, and LogAsync
        // resolves one -- which throws 401 from inside startup and stops the application booting, with a
        // stack trace that points at the audit slice rather than at the seeder.
        Assert.Equal("seed", Assert.Single(audit.Actors));

        var entry = Assert.Single(audit.WithAction(AuditActions.AccountantAccountCreated));
        Assert.Null(entry.Before);

        // The seeded password must not reach the audit log. It is the one credential in the system that a
        // human typed into a config file, and audit rows are read by more people than the database is.
        var after = System.Text.Json.JsonSerializer.Serialize(entry.After);
        Assert.DoesNotContain("a-seeded-admin-password", after);
        Assert.DoesNotContain("passwordHash", after, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_seeded_email_is_stored_normalized_for_lookup_and_original_for_display()
    {
        var (services, db, _, _) = BuildProvider("  First.Admin@Example.COM ", "a-seeded-admin-password");

        await DatabaseSeeder.SeedAsync(services);

        var admin = await db.UserAccounts.SingleAsync();

        // Trimmed on the way in, then normalized for the unique index. If the seeded address were stored
        // un-normalized, the first login -- which looks up by the normalized column -- would not find it,
        // and the only account in the database would be unreachable.
        Assert.Equal("First.Admin@Example.COM", admin.LoginEmail);
        Assert.Equal("first.admin@example.com", admin.NormalizedLoginEmail);
    }

    [Fact]
    public async Task The_seeded_admin_can_actually_log_in_with_the_configured_password()
    {
        await using var db = IdentityTestHarness.NewDb();
        var (services, _, _, _) = BuildProvider("first.admin@example.com", "a-seeded-admin-password", db);
        await DatabaseSeeder.SeedAsync(services);

        // The end-to-end assertion that matters: seeding is worthless if the resulting row cannot
        // authenticate. This covers the hash format, the normalized-email lookup, and the Active status all
        // at once -- three separate ways to produce an account that looks correct in the table and cannot
        // be used.
        var handler = new LoginHandler(
            db, IdentityTestHarness.Passwords, new StubCustomerApi(), new CountingRequestTransaction(),
            new RecordingAuditApi(),
            new StubHttpContextAccessor { HttpContext = IdentityTestHarness.NewHttpContext() });

        var session = await handler.Handle(new Api.Slices.Identity.Application.Dtos.LoginRequestDto
        {
            Email = "FIRST.ADMIN@example.com",
            Password = "a-seeded-admin-password"
        }, default);

        Assert.Equal(UserRole.AccountantAdmin, session.Role);

        // And the session carries the flag, so the middleware confines them to change-password.
        Assert.True(session.MustChangePassword);
    }
}
