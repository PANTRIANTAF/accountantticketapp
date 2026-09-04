using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Shared.Seeding;

/// <summary>
/// Creates the very first AccountantAdmin, because without one nobody can invite anybody and the
/// application is unusable from a fresh database.
///
/// Runs at startup, AFTER migrations, inside a scope created in Program.cs. There is no request, so it
/// opens its own transaction.
/// </summary>
public static class DatabaseSeeder
{
    /// <summary>Actor identifier in the audit log. There is no user to attribute this to.</summary>
    private const string SeedActor = "seed";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var db = services.GetRequiredService<IdentityDbContext>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSeeder");

        // The condition is "no AccountantAdmin exists", NOT "the table is empty". A database holding
        // Employee accounts and no Admin is exactly the unrecoverable state the last-Admin invariant
        // guards against, and seeding is the only way out of it -- a table-empty check would decline to
        // help precisely then.
        //
        // Any status counts here, not just Active. A Suspended Admin can be reactivated by... nobody, but
        // seeding a second Admin alongside one that already exists is the wrong fix and would happen on
        // every restart.
        var adminExists = await db.UserAccounts
            .AnyAsync(account => account.Role == UserRole.AccountantAdmin, ct);
        if (adminExists)
        {
            // Idempotent, and deliberately silent about the configured password. Re-seeding would reset
            // the first Admin's password back to the environment variable on every deploy, silently
            // undoing the change they were forced to make.
            logger.LogInformation("An Accountant Admin already exists; skipping seed.");
            return;
        }

        var email = configuration["Seeding:FirstAdminEmail"];
        var password = configuration["Seeding:FirstAdminPassword"];

        // Fail startup. There is deliberately NO fallback to a built-in default password: a default admin
        // password is the single most exploited misconfiguration in self-hosted software, and one that
        // works on first run is one nobody ever discovers is there.
        //
        // No interactive prompt and no sentinel file either -- there is no terminal in the container.
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "No Accountant Admin exists and Seeding:FirstAdminEmail / Seeding:FirstAdminPassword are "
                + "not both set. Set ACCOUNTANT_ADMIN_EMAIL and ACCOUNTANT_ADMIN_PASSWORD (as "
                + "Seeding__FirstAdminEmail and Seeding__FirstAdminPassword) and start again.");

        var loginEmail = EmailNormalization.Require(email);

        // Validate BEFORE hashing and before inserting. A seeded password the policy would reject can
        // never be changed to something acceptable, because the change-password flow applies the same
        // policy -- and the failure would surface only at first login, as a 422 the person cannot act on.
        PasswordPolicy.Validate(password, loginEmail);

        var passwords = services.GetRequiredService<IPasswordHashing>();
        var transaction = services.GetRequiredService<IRequestTransaction>();
        var audit = services.GetRequiredService<IAuditApi>();

        await using var transactionScope = await transaction.BeginAsync(db, ct);

        var now = DateTimeOffset.UtcNow;
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            LoginEmail = loginEmail,
            NormalizedLoginEmail = EmailNormalization.Normalize(loginEmail),
            PasswordHash = passwords.Hash(password),
            DisplayName = loginEmail,
            Role = UserRole.AccountantAdmin,

            // Active, not Invited. There is nobody to send an invitation to yet, and a first run has no
            // mail transport necessarily configured -- an Invited first Admin could never accept.
            Status = AccountStatus.Active,

            // TRUE, and this is the reason the forced-password-change middleware exists. The seeded
            // password came from an environment variable, which is visible in `docker inspect`, in shell
            // history, and in the compose file -- so it must not remain usable for anything but the one
            // action of replacing itself.
            MustChangePassword = true,

            // Null: nothing has proven the mailbox works. The invitation flow is what normally sets this,
            // and this account skipped it.
            EmailConfirmedAt = null,

            EmployeeId = null,
            CustomerId = null,
            CreatedAt = now
        };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync(ct);

        // LogUnauthenticatedAsync, not LogAsync: there is no CurrentUser in a startup scope, and LogAsync
        // resolves one -- which would throw 401 from inside startup and stop the application booting.
        await audit.LogUnauthenticatedAsync(SeedActor, new AuditEntry(
            AuditActions.AccountantAccountCreated,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            // No Before, and no password in After.
            After: new { account.LoginEmail, account.Role, account.Status, account.MustChangePassword }), ct);

        await transaction.CommitAsync(ct);

        logger.LogWarning(
            "Seeded the first Accountant Admin ({Email}). It must change its password before it can do "
            + "anything else.", account.LoginEmail);
    }
}
