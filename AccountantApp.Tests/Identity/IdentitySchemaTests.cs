using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Migrations;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Application.Handlers;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccountantApp.Tests.Identity;

/// <summary>
/// The only tests that touch the real migration. Everything else in this folder runs against the
/// in-memory provider, which does not enforce a single CHECK constraint, does not have partial indexes,
/// and cannot roll a transaction back -- so the constraints below are entirely unverified without this
/// file, and so is the commit-before-throw rule that the whole lockout mechanism depends on.
/// </summary>
public sealed class IdentitySchemaTests
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
    private const string ExpectedScriptKey =
        "Identity/Infrastructure/Migrations/20260901_001_CreateIdentitySchema.sql";
    private const string CorrectPassword = "correct-horse-battery";

    [SkippableFact]
    public async Task Migration_constraints_and_real_rollback_work_against_real_postgres()
    {
        Skip.IfNot(await PostgresIsReachable(),
            "No PostgreSQL at localhost:5432. The Identity schema, its CHECK constraints and the "
            + "commit-before-throw behaviour of the login failure path are unverified.");

        var database = $"accountant_app_identity_test_{Guid.NewGuid():N}";
        await ExecuteOnAdmin($"CREATE DATABASE \"{database}\"");
        var connectionString = AdminConnectionString.Replace("Database=postgres", $"Database={database}");

        try
        {
            await SqlMigrationRunner.RunAsync(connectionString, AppContext.BaseDirectory);

            // The tracked key uses forward slashes and is slice-relative. A backslash here on Windows
            // means the migration re-runs on Linux and fails on the already-existing table.
            Assert.Equal(ExpectedScriptKey, await QueryScalar<string>(connectionString,
                $"SELECT script_name FROM schema_versions WHERE script_name = '{ExpectedScriptKey}'"));

            await AssertScopeConstraint(connectionString);
            await AssertStatusConstraint(connectionString);
            await AssertTokenConstraints(connectionString);
            await AssertPartialIndexes(connectionString);
            await AssertColumnMapping(connectionString);
            await AssertFailedLoginIncrementSurvivesTheThrow(connectionString);
            await AssertLastAdminInvariantRollsBack(connectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnAdmin($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    /// <summary>
    /// ck_user_accounts_scope. An Accountant must have neither scope id; a Customer-side account must
    /// have both. A CustomerAdmin row with a NULL customer_id produces a cookie the factory rejects, so
    /// the symptom is a 401 on the request AFTER login and nothing at all wrong with the login.
    /// </summary>
    private static async Task AssertScopeConstraint(string connectionString)
    {
        var accountantWithScope = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
            connectionString,
            InsertAccount("scoped.accountant@example.com", "AccountantAdmin",
                employeeId: "gen_random_uuid()", customerId: "gen_random_uuid()")));
        Assert.Equal("23514", accountantWithScope.SqlState);
        Assert.Contains("ck_user_accounts_scope", accountantWithScope.Message);

        var employeeWithoutScope = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
            connectionString,
            InsertAccount("unscoped.employee@example.com", "Employee")));
        Assert.Equal("23514", employeeWithoutScope.SqlState);

        // Half-scoped is refused too -- customer_id present, employee_id missing. This is the shape a
        // partially written insert produces, and it is the one that looks most plausible in a table dump.
        var halfScoped = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
            connectionString,
            InsertAccount("half.scoped@example.com", "Employee", customerId: "gen_random_uuid()")));
        Assert.Equal("23514", halfScoped.SqlState);

        // Both valid shapes are accepted.
        await ExecuteOn(connectionString, InsertAccount("valid.accountant@example.com", "AccountantUser"));
        await ExecuteOn(connectionString, InsertAccount("valid.employee@example.com", "Employee",
            employeeId: "gen_random_uuid()", customerId: "gen_random_uuid()"));
    }

    private static async Task AssertStatusConstraint(string connectionString)
    {
        var badStatus = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
            connectionString,
            InsertAccount("bad.status@example.com", "AccountantUser", status: "'Deleted'")));
        Assert.Equal("23514", badStatus.SqlState);

        // Case matters. 'active' is not 'Active', and the code compares ordinally against the
        // PascalCase form -- a lowercase row would be invisible to every status check in the slice.
        var wrongCase = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
            connectionString,
            InsertAccount("wrong.case@example.com", "AccountantUser", status: "'active'")));
        Assert.Equal("23514", wrongCase.SqlState);

        // Duplicate normalized email. The unique constraint is the last line of defence behind the
        // handlers' explicit checks, and it is what makes two concurrent invitations to the same address
        // resolve to one account rather than two.
        await ExecuteOn(connectionString, InsertAccount("duplicate@example.com", "AccountantUser"));
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
            connectionString, InsertAccount("duplicate@example.com", "AccountantUser")));
        Assert.Equal("23505", duplicate.SqlState);
    }

    private static async Task AssertTokenConstraints(string connectionString)
    {
        var accountId = await QueryScalar<Guid>(connectionString,
            "SELECT id FROM user_accounts WHERE normalized_login_email = 'valid.accountant@example.com'");

        var badPurpose = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            InsertToken(accountId, "'EmailChange'", Hash('a'))));
        Assert.Equal("23514", badPurpose.SqlState);

        await ExecuteOn(connectionString, InsertToken(accountId, "'PasswordReset'", Hash('b')));

        // uq_user_account_tokens_hash. A duplicate hash is either a collision or -- far more likely -- a
        // bug that reused a raw token, and either must fail at insert rather than silently authorize.
        var duplicateHash = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            InsertToken(accountId, "'Invitation'", Hash('b'))));
        Assert.Equal("23505", duplicateHash.SqlState);

        // The foreign key is real here, because it is within the slice. A token for a nonexistent account
        // is unredeemable garbage that the cleanup job would have to reason about.
        var orphan = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            InsertToken(Guid.NewGuid(), "'Invitation'", Hash('c'))));
        Assert.Equal("23503", orphan.SqlState);

        // CHAR(64) is exactly the width of lowercase-hex SHA-256. Anything longer is refused rather than
        // silently truncated -- truncation would make two different tokens compare equal.
        var tooLong = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            InsertToken(accountId, "'Invitation'", new string('d', 65))));
        Assert.Equal("22001", tooLong.SqlState);
    }

    private static async Task AssertPartialIndexes(string connectionString)
    {
        // uq_user_accounts_employee is UNIQUE but PARTIAL. Both halves matter: unique, so one Employee
        // cannot have two accounts; partial on NOT NULL, because every Accountant row has a NULL
        // employee_id and a plain unique index would allow exactly one Accountant in the whole system.
        var employeeId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        await ExecuteOn(connectionString, InsertAccount("employee.one@example.com", "Employee",
            employeeId: $"'{employeeId}'", customerId: $"'{customerId}'"));

        var secondAccount = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            InsertAccount("employee.two@example.com", "Employee",
                employeeId: $"'{employeeId}'", customerId: $"'{customerId}'")));
        Assert.Equal("23505", secondAccount.SqlState);

        // Two Accountants both with NULL employee_id -- allowed, which is the partial half of the index.
        await ExecuteOn(connectionString, InsertAccount("accountant.a@example.com", "AccountantUser"));
        await ExecuteOn(connectionString, InsertAccount("accountant.b@example.com", "AccountantUser"));

        // The indexes the plan names, present with the predicates it specifies. A partial index that
        // silently became total still answers every query correctly and only shows up as a table that is
        // larger and slower than it should be, years later.
        var indexes = await QueryStrings(connectionString,
            "SELECT indexname FROM pg_indexes WHERE tablename IN ('user_accounts', 'user_account_tokens')");
        foreach (var expected in new[]
                 {
                     "idx_user_accounts_accountants", "idx_user_accounts_active_admins",
                     "uq_user_accounts_employee", "uq_user_account_tokens_hash",
                     "idx_user_account_tokens_outstanding"
                 })
            Assert.Contains(expected, indexes);

        var partial = await QueryStrings(connectionString,
            "SELECT indexname FROM pg_indexes WHERE tablename IN ('user_accounts', 'user_account_tokens') "
            + "AND indexdef LIKE '%WHERE%'");
        Assert.Contains("idx_user_accounts_active_admins", partial);
        Assert.Contains("uq_user_accounts_employee", partial);
        Assert.Contains("idx_user_account_tokens_outstanding", partial);

        // No second index on normalized_login_email. The UNIQUE constraint already provides one, and a
        // duplicate would double the write cost of the hottest table for no read benefit.
        var emailIndexes = await QueryScalar<long>(connectionString,
            "SELECT COUNT(*) FROM pg_indexes WHERE tablename = 'user_accounts' "
            + "AND indexdef LIKE '%normalized_login_email%'");
        Assert.Equal(1, emailIndexes);
    }

    /// <summary>
    /// Every property must map, and snake_case is NOT automatic in this codebase -- each column name is
    /// declared explicitly. A missed HasColumnName does not fail at startup; it fails on the first query
    /// that touches the column, as a 42703 from deep inside EF.
    /// </summary>
    private static async Task AssertColumnMapping(string connectionString)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        var accountId = Guid.NewGuid();
        var issued = new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.FromHours(2));

        await using (var db = new IdentityDbContext(options))
        {
            db.UserAccounts.Add(new UserAccount
            {
                Id = accountId,
                LoginEmail = "Mapping.Test@Example.COM",
                NormalizedLoginEmail = "mapping.test@example.com",
                PasswordHash = IdentityTestHarness.Passwords.Hash(CorrectPassword),
                DisplayName = "Mapping Test",
                Role = UserRole.AccountantAdmin,
                Status = AccountStatus.Active,
                MustChangePassword = true,
                EmailConfirmedAt = issued,
                FailedLoginCount = 3,
                LockoutExpiresAt = issued.AddMinutes(15),
                CreatedAt = issued,
                LastLoginAt = issued,
                LastPasswordChangeAt = issued
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new IdentityDbContext(options))
        {
            var read = await db.UserAccounts.SingleAsync(item => item.Id == accountId);

            // Every nullable timestamp round-trips, in UTC. TIMESTAMPTZ normalises the offset, so
            // comparing the raw DateTimeOffset would fail on a machine outside UTC+2 for no real reason.
            Assert.Equal(issued.UtcDateTime, read.CreatedAt.UtcDateTime);
            Assert.Equal(issued.UtcDateTime, read.EmailConfirmedAt!.Value.UtcDateTime);
            Assert.Equal(issued.UtcDateTime, read.LastLoginAt!.Value.UtcDateTime);
            Assert.Equal(issued.UtcDateTime, read.LastPasswordChangeAt!.Value.UtcDateTime);
            Assert.Equal(issued.AddMinutes(15).UtcDateTime, read.LockoutExpiresAt!.Value.UtcDateTime);

            Assert.Equal(3, read.FailedLoginCount);
            Assert.True(read.MustChangePassword);

            // Role is stored as TEXT, not as an integer. An int would make the CHECK constraint on role
            // impossible and a column dump unreadable.
            Assert.Equal(UserRole.AccountantAdmin, read.Role);
            Assert.Equal("Mapping.Test@Example.COM", read.LoginEmail);
        }

        var storedRole = await QueryScalar<string>(connectionString,
            $"SELECT role FROM user_accounts WHERE id = '{accountId}'");
        Assert.Equal("AccountantAdmin", storedRole);
    }

    /// <summary>
    /// The rule this whole slice's brute-force protection rests on, verified against a REAL transaction.
    /// The in-memory tests assert CommitAsync was called; only this one proves the increment actually
    /// survives, because only here does a missing commit really roll back.
    /// </summary>
    private static async Task AssertFailedLoginIncrementSurvivesTheThrow(string connectionString)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        var accountId = Guid.NewGuid();
        await using (var db = new IdentityDbContext(options))
        {
            db.UserAccounts.Add(new UserAccount
            {
                Id = accountId,
                LoginEmail = "lockout.test@example.com",
                NormalizedLoginEmail = "lockout.test@example.com",
                PasswordHash = IdentityTestHarness.Passwords.Hash(CorrectPassword),
                DisplayName = "Lockout Test",
                Role = UserRole.AccountantUser,
                Status = AccountStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Four wrong passwords, each through its own DbContext and its own REAL RequestTransaction --
        // four separate requests, as it would happen in production.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await using var db = new IdentityDbContext(options);
            var handler = new LoginHandler(
                db, IdentityTestHarness.Passwords, new StubCustomerApi(), new RequestTransaction(),
                new NoOpAuditApi(),
                new StubHttpContextAccessor { HttpContext = IdentityTestHarness.NewHttpContext() });

            await Assert.ThrowsAnyAsync<Exception>(() => handler.Handle(
                new LoginRequestDto { Email = "lockout.test@example.com", Password = "wrong-password" },
                CancellationToken.None));

            // Read it back from the database, not from the tracked entity. This is the assertion the
            // in-memory tests structurally cannot make: if the handler threw without committing, the count
            // here would still be 0 after every attempt, no account would ever lock, and every
            // status-code test in this folder would still be green.
            var persisted = await QueryScalar<int>(connectionString,
                $"SELECT failed_login_count FROM user_accounts WHERE id = '{accountId}'");
            Assert.Equal(attempt, persisted);
        }

        // The fifth failure locks the account and resets the counter to 0 -- left at 5, the first attempt
        // after the lockout expired would re-lock it immediately, forever.
        await using (var db = new IdentityDbContext(options))
        {
            var handler = new LoginHandler(
                db, IdentityTestHarness.Passwords, new StubCustomerApi(), new RequestTransaction(),
                new NoOpAuditApi(),
                new StubHttpContextAccessor { HttpContext = IdentityTestHarness.NewHttpContext() });

            await Assert.ThrowsAnyAsync<Exception>(() => handler.Handle(
                new LoginRequestDto { Email = "lockout.test@example.com", Password = "wrong-password" },
                CancellationToken.None));
        }

        Assert.Equal(0, await QueryScalar<int>(connectionString,
            $"SELECT failed_login_count FROM user_accounts WHERE id = '{accountId}'"));
        Assert.NotNull(await QueryScalar<DateTime?>(connectionString,
            $"SELECT lockout_expires_at FROM user_accounts WHERE id = '{accountId}'"));

        // And the correct password is now refused, because the lockout is checked before the password.
        await using (var db = new IdentityDbContext(options))
        {
            var handler = new LoginHandler(
                db, IdentityTestHarness.Passwords, new StubCustomerApi(), new RequestTransaction(),
                new NoOpAuditApi(),
                new StubHttpContextAccessor { HttpContext = IdentityTestHarness.NewHttpContext() });

            await Assert.ThrowsAnyAsync<Exception>(() => handler.Handle(
                new LoginRequestDto { Email = "lockout.test@example.com", Password = CorrectPassword },
                CancellationToken.None));
        }
    }

    /// <summary>
    /// The mirror image: a failure path that must NOT persist its write. The invariant check runs after
    /// SaveChangesAsync and before CommitAsync, so the suspension is really rolled back.
    /// </summary>
    private static async Task AssertLastAdminInvariantRollsBack(string connectionString)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        // Clear the Admins seeded by the constraint tests above, then create exactly one.
        await ExecuteOn(connectionString,
            "DELETE FROM user_account_tokens; DELETE FROM user_accounts WHERE role = 'AccountantAdmin'");

        var onlyAdminId = Guid.NewGuid();
        await using (var db = new IdentityDbContext(options))
        {
            db.UserAccounts.Add(new UserAccount
            {
                Id = onlyAdminId,
                LoginEmail = "sole.admin@example.com",
                NormalizedLoginEmail = "sole.admin@example.com",
                PasswordHash = IdentityTestHarness.Passwords.Hash(CorrectPassword),
                DisplayName = "Sole Admin",
                Role = UserRole.AccountantAdmin,
                Status = AccountStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var audit = new NoOpAuditApi();
        var permissions = new Api.Shared.Authorization.PermissionChecker(
            [new Api.Slices.Identity.IdentityActionCatalogue()], audit,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                Api.Shared.Authorization.PermissionChecker>.Instance);

        await using (var db = new IdentityDbContext(options))
        {
            var handler = new SuspendAccountantHandler(
                db, permissions, new RecordingNotificationApi(), new RequestTransaction(), audit);

            await Assert.ThrowsAnyAsync<Exception>(() => handler.Handle(
                new AccountIdRequestDto { UserAccountId = onlyAdminId },
                new CurrentUser(Guid.NewGuid().ToString(), UserRole.AccountantAdmin),
                CancellationToken.None));
        }

        // Still Active in the database. SaveChangesAsync wrote 'Suspended' inside the transaction and
        // DisposeAsync rolled it back because CommitAsync was never reached. Move the invariant check
        // before SaveChangesAsync and this row would read 'Suspended' -- the Office locked out by a call
        // that returned a 422 saying it had refused to do exactly that.
        Assert.Equal("Active", await QueryScalar<string>(connectionString,
            $"SELECT status FROM user_accounts WHERE id = '{onlyAdminId}'"));
    }

    // --- SQL helpers ---

    private static string Hash(char fill) => new(fill, 64);

    private static string InsertAccount(
        string email, string role,
        string? employeeId = null, string? customerId = null, string status = "'Active'") =>
        "INSERT INTO user_accounts (login_email, normalized_login_email, password_hash, display_name, "
        + "role, employee_id, customer_id, status) VALUES "
        + $"('{email}', '{email}', 'a-hash', 'Display Name', '{role}', "
        + $"{employeeId ?? "NULL"}, {customerId ?? "NULL"}, {status})";

    private static string InsertToken(Guid accountId, string purpose, string tokenHash) =>
        "INSERT INTO user_account_tokens (user_account_id, purpose, token_hash, expires_at) VALUES "
        + $"('{accountId}', {purpose}, '{tokenHash}', NOW() + INTERVAL '1 hour')";

    private static async Task<bool> PostgresIsReachable()
    {
        try
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync();
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    private static Task ExecuteOnAdmin(string sql) => ExecuteOn(AdminConnectionString, sql);

    private static async Task ExecuteOn(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> QueryScalar<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task<List<string>> QueryStrings(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }

    private sealed class NoOpAuditApi : IAuditApi
    {
        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task LogUnauthenticatedAsync(
            string actorIdentifier, AuditEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
