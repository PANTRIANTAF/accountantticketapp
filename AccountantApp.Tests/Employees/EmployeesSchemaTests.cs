using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Migrations;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers.Infrastructure;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Application.Handlers;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using AccountantApp.Tests.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static AccountantApp.Tests.Employees.EmployeesTestHarness;

namespace AccountantApp.Tests.Employees;

/// <summary>
/// The only tests that touch the real Employees migration. Everything else in this folder runs against the
/// in-memory provider, which enforces no CHECK constraint, has no partial or trigram indexes, cannot
/// translate EF.Functions.ILike, and cannot roll a transaction back.
///
/// So without this file the following are entirely unverified: all three CHECK constraints, both unique
/// partial indexes, the ILIKE search path, the 23505 conversion to a 409, and -- the one that matters most
/// -- whether a failed onboarding really leaves no Customer behind. Plan section 11.3 test 2 is here, and
/// it asserts by QUERYING THE DATABASE after the failure, because a 409 comes back whether or not the
/// rollback worked.
/// </summary>
public sealed class EmployeesSchemaTests
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
    private const string ExpectedScriptKey =
        "Employees/Infrastructure/Migrations/20260902_001_CreateEmployeesSchema.sql";

    [SkippableFact]
    public async Task Migration_constraints_indexes_search_and_real_rollback_work_against_real_postgres()
    {
        Skip.IfNot(await PostgresIsReachable(),
            "No PostgreSQL at localhost:5432. The Employees schema, its three CHECK constraints, both "
            + "unique partial indexes, the ILIKE search path and the cross-slice onboarding rollback are "
            + "unverified.");

        var database = $"accountant_app_employees_test_{Guid.NewGuid():N}";
        await ExecuteOnAdmin($"CREATE DATABASE \"{database}\"");
        var connectionString = AdminConnectionString.Replace("Database=postgres", $"Database={database}");

        try
        {
            await SqlMigrationRunner.RunAsync(connectionString, AppContext.BaseDirectory);

            // Slice-relative and forward-slashed. A backslash here on Windows means the migration re-runs
            // on Linux and fails on the already-existing table.
            Assert.Equal(ExpectedScriptKey, await QueryScalar<string>(connectionString,
                $"SELECT script_name FROM schema_versions WHERE script_name = '{ExpectedScriptKey}'"));

            await AssertDepartureConstraint(connectionString);
            await AssertDateConstraint(connectionString);
            await AssertEmailPairConstraint(connectionString);
            await AssertUniqueEmailPerCustomer(connectionString);
            await AssertUniqueUserAccount(connectionString);
            await AssertIndexes(connectionString);
            await AssertColumnMapping(connectionString);
            await AssertDuplicateEmailSurfacesAs409(connectionString);
            await AssertSearchUsesIlikeAndEscapesWildcards(connectionString);
            await AssertOnboardingRollsBackEverySlice(connectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnAdmin($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    /// <summary>
    /// ck_employees_departure. The state machine, in the schema: an end date and a departed_at exist
    /// exactly when the person has departed, and never before.
    /// </summary>
    private static async Task AssertDepartureConstraint(string connectionString)
    {
        // Departed with no departed_at. This is the row a departure handler that forgot the timestamp
        // writes, and it reads as a perfectly ordinary Departed Employee in a table dump -- the audit
        // trail simply has no record of when it happened.
        var departedWithoutInstant = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
            connectionString,
            Insert("departed.no.instant@example.com", status: "'Departed'",
                endDate: "'2026-06-30'")));
        Assert.Equal("23514", departedWithoutInstant.SqlState);
        Assert.Contains("ck_employees_departure", departedWithoutInstant.Message);

        // Active with a departed_at.
        var activeWithInstant = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
            connectionString,
            Insert("active.with.instant@example.com", departedAt: "NOW()")));
        Assert.Equal("23514", activeWithInstant.SqlState);

        // Active with an employment_end_date. Allowing this is how a "leaving next month" row gets
        // created that no departure ever processes -- the person keeps their login indefinitely.
        var activeWithEndDate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
            connectionString,
            Insert("active.with.end@example.com", endDate: "'2026-12-31'")));
        Assert.Equal("23514", activeWithEndDate.SqlState);

        // A third status value fails BOTH branches, which is why there is deliberately no separate
        // ck_employees_status. A second constraint expressing a rule this one already holds is a second
        // constraint that can disagree with it.
        var unknownStatus = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
            connectionString, Insert("unknown.status@example.com", status: "'Suspended'")));
        Assert.Equal("23514", unknownStatus.SqlState);
        Assert.Contains("ck_employees_departure", unknownStatus.Message);

        // Case matters: the code compares ordinally against the PascalCase form, so an 'active' row
        // would be invisible to every status check in the slice.
        var wrongCase = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
            connectionString, Insert("wrong.case@example.com", status: "'active'")));
        Assert.Equal("23514", wrongCase.SqlState);

        // Both legal shapes are accepted.
        await ExecuteOn(connectionString, Insert("valid.active@example.com"));
        await ExecuteOn(connectionString, Insert("valid.departed@example.com",
            status: "'Departed'", endDate: "'2026-06-30'", departedAt: "NOW()"));

        // Departed with no end date is allowed on purpose -- the two answer different questions, and a
        // record corrected years later may have the instant without anybody knowing the last working day.
        await ExecuteOn(connectionString, Insert("departed.no.end.date@example.com",
            status: "'Departed'", departedAt: "NOW()"));
    }

    private static async Task AssertDateConstraint(string connectionString)
    {
        var backwards = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
            connectionString,
            Insert("backwards.dates@example.com", status: "'Departed'",
                startDate: "'2026-03-01'", endDate: "'2026-02-01'", departedAt: "NOW()")));
        Assert.Equal("23514", backwards.SqlState);
        Assert.Contains("ck_employees_dates", backwards.Message);

        // Equal is fine: somebody can start and leave on the same day.
        await ExecuteOn(connectionString, Insert("one.day@example.com", status: "'Departed'",
            startDate: "'2026-03-01'", endDate: "'2026-03-01'", departedAt: "NOW()"));
    }

    private static async Task AssertEmailPairConstraint(string connectionString)
    {
        // A work_email with no normalized_work_email is invisible to every lookup in the slice, which
        // reads to a Customer Admin as "no such Employee" while the row is plainly there in the table.
        var halfWritten = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            "INSERT INTO employees (customer_id, given_name, family_name, work_email, "
            + "employment_start_date) VALUES "
            + "(gen_random_uuid(), 'Half', 'Written', 'half@example.com', '2026-01-05')"));
        Assert.Equal("23514", halfWritten.SqlState);
        Assert.Contains("ck_employees_email_pair", halfWritten.Message);

        // And the other way round.
        var normalizedOnly = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            "INSERT INTO employees (customer_id, given_name, family_name, normalized_work_email, "
            + "employment_start_date) VALUES "
            + "(gen_random_uuid(), 'Normalized', 'Only', 'NORM@EXAMPLE.COM', '2026-01-05')"));
        Assert.Equal("23514", normalizedOnly.SqlState);

        // Neither: an accountless Employee with no address on file, which is a normal record.
        await ExecuteOn(connectionString,
            "INSERT INTO employees (customer_id, given_name, family_name, employment_start_date) VALUES "
            + "(gen_random_uuid(), 'No', 'Email', '2026-01-05')");
    }

    /// <summary>
    /// uq_employees_customer_email. Unique PER CUSTOMER and partial on NOT NULL -- both halves matter.
    /// </summary>
    private static async Task AssertUniqueEmailPerCustomer(string connectionString)
    {
        var customerId = Guid.NewGuid();
        await ExecuteOn(connectionString,
            Insert("shared@example.com", customerId: $"'{customerId}'"));

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            Insert("shared@example.com", customerId: $"'{customerId}'", given: "'Second'")));
        Assert.Equal("23505", duplicate.SqlState);
        Assert.Contains("uq_employees_customer_email", duplicate.Message);

        // The SAME address at a DIFFERENT Customer is fine. A global index here would make registering an
        // Employee fail because an unrelated Customer has that address on file, and the error could not
        // say why without leaking another Customer's data.
        await ExecuteOn(connectionString,
            Insert("shared@example.com", customerId: $"'{Guid.NewGuid()}'"));

        // Many NULLs at one Customer -- the partial half. A total unique index would allow exactly one
        // Employee without a work email per Customer, and accountless Employees are the common case.
        for (var index = 0; index < 3; index++)
            await ExecuteOn(connectionString,
                "INSERT INTO employees (customer_id, given_name, family_name, employment_start_date) "
                + $"VALUES ('{customerId}', 'Accountless{index}', 'Person', '2026-01-05')");
    }

    /// <summary>
    /// uq_employees_user_account. Two Employee rows pointing at one account means two Customer scopes for
    /// one session, and whichever row a query finds first wins.
    /// </summary>
    private static async Task AssertUniqueUserAccount(string connectionString)
    {
        var accountId = Guid.NewGuid();
        await ExecuteOn(connectionString,
            Insert("account.one@example.com", accountId: $"'{accountId}'"));

        var second = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            Insert("account.two@example.com", accountId: $"'{accountId}'")));
        Assert.Equal("23505", second.SqlState);
        Assert.Contains("uq_employees_user_account", second.Message);

        // Two accountless rows -- the partial half again.
        await ExecuteOn(connectionString, Insert("accountless.a@example.com"));
        await ExecuteOn(connectionString, Insert("accountless.b@example.com"));
    }

    private static async Task AssertIndexes(string connectionString)
    {
        var indexes = await QueryStrings(connectionString,
            "SELECT indexname FROM pg_indexes WHERE tablename = 'employees'");
        foreach (var expected in new[]
                 {
                     "idx_employees_customer_name", "uq_employees_customer_email",
                     "uq_employees_user_account", "idx_employees_customer_active",
                     "idx_employees_name_trgm", "idx_employees_email_trgm"
                 })
            Assert.Contains(expected, indexes);

        // A partial index that silently became total still answers every query correctly. It shows up
        // only as a table that is larger and slower than it should be, years later.
        var partial = await QueryStrings(connectionString,
            "SELECT indexname FROM pg_indexes WHERE tablename = 'employees' "
            + "AND indexdef LIKE '%WHERE%'");
        Assert.Contains("uq_employees_customer_email", partial);
        Assert.Contains("uq_employees_user_account", partial);
        Assert.Contains("idx_employees_customer_active", partial);

        // GIN, not b-tree. A b-tree on these columns is created without complaint, answers the ILIKE
        // query correctly by sequential scan, and is simply never used -- the search gets slower in
        // proportion to the table and nothing ever reports an error.
        var gin = await QueryStrings(connectionString,
            "SELECT indexname FROM pg_indexes WHERE tablename = 'employees' "
            + "AND indexdef LIKE '%USING gin%'");
        Assert.Contains("idx_employees_name_trgm", gin);
        Assert.Contains("idx_employees_email_trgm", gin);

        // pg_trgm is what makes them possible at all.
        Assert.Equal("pg_trgm", await QueryScalar<string>(connectionString,
            "SELECT extname FROM pg_extension WHERE extname = 'pg_trgm'"));
    }

    /// <summary>
    /// Every property must map. snake_case is NOT automatic in this codebase -- each column name is
    /// declared explicitly, and a missed HasColumnName does not fail at startup. It fails on the first
    /// query that touches the column, as a 42703 from deep inside EF.
    /// </summary>
    private static async Task AssertColumnMapping(string connectionString)
    {
        var options = new DbContextOptionsBuilder<EmployeesDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        var employeeId = Guid.NewGuid();
        var recorded = new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.FromHours(2));

        await using (var db = new EmployeesDbContext(options))
        {
            db.Employees.Add(new Employee
            {
                Id = employeeId,
                CustomerId = Guid.NewGuid(),
                GivenName = "Mapping",
                FamilyName = "Test",
                JobTitle = "Bookkeeper",
                WorkEmail = "Mapping.Test@Example.COM",
                NormalizedWorkEmail = "MAPPING.TEST@EXAMPLE.COM",
                ContactPhone = "+302100000000",
                TaxIdentificationNumber = "TIN-000",
                SocialSecurityNumber = "SSN-000",
                UserAccountId = Guid.NewGuid(),
                EmploymentStartDate = new DateOnly(2026, 1, 5),
                EmploymentEndDate = new DateOnly(2026, 6, 30),
                DepartedAt = recorded,
                Status = EmployeeStatus.Departed,
                CreatedAt = recorded,
                UpdatedAt = recorded
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new EmployeesDbContext(options))
        {
            var read = await db.Employees.SingleAsync(item => item.Id == employeeId);

            // DATE round-trips as a DateOnly with no timezone shift. A TIMESTAMPTZ column here would turn
            // a start date into the previous day for half the world.
            Assert.Equal(new DateOnly(2026, 1, 5), read.EmploymentStartDate);
            Assert.Equal(new DateOnly(2026, 6, 30), read.EmploymentEndDate);

            // TIMESTAMPTZ normalises the offset, so compare in UTC -- the raw DateTimeOffset would differ
            // on a machine outside UTC+2 for no real reason.
            Assert.Equal(recorded.UtcDateTime, read.DepartedAt!.Value.UtcDateTime);
            Assert.Equal(recorded.UtcDateTime, read.CreatedAt.UtcDateTime);
            Assert.Equal(recorded.UtcDateTime, read.UpdatedAt.UtcDateTime);

            Assert.Equal("Mapping.Test@Example.COM", read.WorkEmail);
            Assert.Equal("MAPPING.TEST@EXAMPLE.COM", read.NormalizedWorkEmail);
            Assert.Equal("TIN-000", read.TaxIdentificationNumber);
            Assert.Equal("SSN-000", read.SocialSecurityNumber);
        }

        // Status is TEXT, not an integer. An int would make ck_employees_departure impossible to express
        // and a table dump unreadable.
        Assert.Equal("Departed", await QueryScalar<string>(connectionString,
            $"SELECT status FROM employees WHERE id = '{employeeId}'"));
    }

    /// <summary>
    /// The register handler end to end against real Postgres, then a duplicate. The in-memory tests prove
    /// the pre-check; only here does the index itself exist, so only here is the 23505 catch reachable at
    /// all -- and this is also the first time the DDL and the EF mapping are exercised together.
    /// </summary>
    private static async Task AssertDuplicateEmailSurfacesAs409(string connectionString)
    {
        var options = new DbContextOptionsBuilder<EmployeesDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var customers = new FakeCustomerApi();
        var customerId = customers.AddActive();

        var request = new RegisterEmployeeRequestDto
        {
            CustomerId = customerId,
            GivenName = "Nikos",
            FamilyName = "Petrou",
            JobTitle = "Bookkeeper",
            WorkEmail = "nikos.real@example.com",
            EmploymentStartDate = new DateOnly(2026, 2, 1)
        };

        await using (var db = new EmployeesDbContext(options))
        {
            var audit = new TestAuditApi();
            var result = await new RegisterEmployeeHandler(
                    db, Permissions(audit), new RequestTransaction(), customers,
                    new FakeIdentityApi(), new Identity.RecordingNotificationApi(), audit)
                .Handle(request, Accountant(), CancellationToken.None);

            Assert.False(result.HasAccount);
        }

        // Committed for real, in a fresh context. The in-memory suite cannot tell a commit from a
        // rollback, so this is the first evidence that a successful registration persists at all.
        await using (var db = new EmployeesDbContext(options))
            Assert.Equal(1, await db.Employees.CountAsync(
                employee => employee.NormalizedWorkEmail == "NIKOS.REAL@EXAMPLE.COM"));

        await using (var db = new EmployeesDbContext(options))
        {
            var audit = new TestAuditApi();
            var exception = await Assert.ThrowsAsync<Api.Shared.Errors.AppException>(() =>
                new RegisterEmployeeHandler(
                        db, Permissions(audit), new RequestTransaction(), customers,
                        new FakeIdentityApi(), new Identity.RecordingNotificationApi(), audit)
                    .Handle(request, Accountant(), CancellationToken.None));

            Assert.Equal(409, exception.StatusCode);
        }
    }

    /// <summary>
    /// EF.Functions.ILike cannot be translated by the in-memory provider at all, so the entire search
    /// branch of the list endpoint is dead code in every other test in this folder.
    /// </summary>
    private static async Task AssertSearchUsesIlikeAndEscapesWildcards(string connectionString)
    {
        var options = new DbContextOptionsBuilder<EmployeesDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var customerId = Guid.NewGuid();

        await ExecuteOn(connectionString,
            Insert("maria.search@example.com", customerId: $"'{customerId}'",
                given: "'Maria'", family: "'Papadopoulou'"));
        await ExecuteOn(connectionString,
            Insert("kostas.search@example.com", customerId: $"'{customerId}'",
                given: "'Kostas'", family: "'Dimitriou'"));
        // A literal per-cent sign in a name, which is what makes the escaping observable.
        await ExecuteOn(connectionString,
            Insert("odd.search@example.com", customerId: $"'{customerId}'",
                given: "'100%'", family: "'Certain'"));

        await using var db = new EmployeesDbContext(options);
        var handler = new ListEmployeesHandler(
            db, Permissions(new TestAuditApi()), new FakeIdentityApi());
        var user = new CurrentUser(Guid.NewGuid().ToString(), UserRole.CustomerAdmin, customerId);

        // Case-insensitive, and a substring rather than a prefix -- 'ADOPO' is in the middle of the
        // family name.
        var byFamily = await handler.Handle(
            new ListEmployeesRequestDto { SearchTerm = "ADOPO" }, user, CancellationToken.None);
        Assert.Equal("Maria", Assert.Single(byFamily.Items).GivenName);

        // The work email is searched too, and it is the NORMALIZED column that carries the value -- but
        // the predicate runs against work_email, so mixed case in the term must still match.
        var byEmail = await handler.Handle(
            new ListEmployeesRequestDto { SearchTerm = "KoStAs.SeArCh" }, user, CancellationToken.None);
        Assert.Equal("Kostas", Assert.Single(byEmail.Items).GivenName);

        // A bare '%' typed into a search box matches ONE row -- the one with a literal per-cent sign --
        // not all three. Unescaped it would silently return everything, which looks like the filter
        // being ignored rather than a bug.
        var wildcard = await handler.Handle(
            new ListEmployeesRequestDto { SearchTerm = "%" }, user, CancellationToken.None);
        Assert.Equal(1, wildcard.TotalCount);
        Assert.Equal("100%", Assert.Single(wildcard.Items).GivenName);

        // '_' likewise matches any single character unescaped, so this would otherwise return all three.
        var underscore = await handler.Handle(
            new ListEmployeesRequestDto { SearchTerm = "_" }, user, CancellationToken.None);
        Assert.Equal(0, underscore.TotalCount);

        // Scope still applies with a search term: an Employee at another Customer whose name matches.
        await ExecuteOn(connectionString,
            Insert("other.adopo@example.com", given: "'Maria'", family: "'Papadopoulou'"));
        var scoped = await handler.Handle(
            new ListEmployeesRequestDto { SearchTerm = "ADOPO" }, user, CancellationToken.None);
        Assert.Equal(1, scoped.TotalCount);
    }

    /// <summary>
    /// PLAN SECTION 11.3 TEST 2. The onboarding operation spans three slices on one connection, and a
    /// failure at step 3 must leave neither the Customer nor the Employee behind.
    ///
    /// The assertion QUERIES THE DATABASE in a new context after the request completed. It does not check
    /// the status code: a 409 comes back whether or not the rollback worked, so a status-code assertion
    /// passes just as happily against an implementation that left a Customer nobody can log into -- the
    /// exact state the platform matrix forbids.
    /// </summary>
    private static async Task AssertOnboardingRollsBackEverySlice(string connectionString)
    {
        const string takenEmail = "already.taken@example.com";
        var taxNumber = $"TAX-{Guid.NewGuid():N}";

        // The address is already a login, in this case an Accountant's -- so step 3 fails with a 409 after
        // steps 1 and 2 have already written their rows.
        await ExecuteOn(connectionString,
            "INSERT INTO user_accounts (login_email, normalized_login_email, password_hash, "
            + "display_name, role, status) VALUES "
            + $"('{takenEmail}', '{takenEmail}', 'a-hash', 'Existing Person', 'AccountantUser', 'Active')");

        // ONE connection for all three DbContexts. This is what makes the transaction span three slices,
        // and it is the whole justification for RequestConnection existing: with a connection each, the
        // Customer would commit independently and survive the failure.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var transaction = new RequestTransaction();
        var audit = new NoOpAuditApi();

        await using (var employeesDb = new EmployeesDbContext(
                         new DbContextOptionsBuilder<EmployeesDbContext>()
                             .UseNpgsql(connection).Options))
        await using (var customersDb = new CustomersDbContext(
                         new DbContextOptionsBuilder<CustomersDbContext>()
                             .UseNpgsql(connection).Options))
        await using (var identityDb = new IdentityDbContext(
                         new DbContextOptionsBuilder<IdentityDbContext>()
                             .UseNpgsql(connection).Options))
        {
            var handler = new OnboardCustomerHandler(
                employeesDb,
                Permissions(new TestAuditApi()),
                transaction,
                new CustomerApi(customersDb, transaction, audit),
                new IdentityApi(
                    identityDb, IdentityTestHarness.Tokens, IdentityTestHarness.Links,
                    new RecordingNotificationApi(), transaction, audit),
                audit);

            var exception = await Assert.ThrowsAsync<Api.Shared.Errors.AppException>(() => handler.Handle(
                new OnboardCustomerRequestDto
                {
                    Customer = new CreateCustomer
                    {
                        LegalName = "Rollback Ltd",
                        TaxNumber = taxNumber,
                        AddressLine1 = "1 Main Street",
                        AddressCity = "Athens",
                        AddressPostalCode = "10001",
                        AddressCountry = "GR",
                        ContactEmail = "info@rollback.example",
                        ContactPhone = "+302100000000",
                        OnboardedOn = new DateOnly(2026, 1, 15)
                    },
                    FirstAdmin = new OnboardFirstAdminDto
                    {
                        GivenName = "Ada",
                        FamilyName = "Admin",
                        JobTitle = "Owner",
                        WorkEmail = takenEmail,
                        EmploymentStartDate = new DateOnly(2026, 1, 15)
                    }
                },
                Accountant(UserRole.AccountantAdmin), CancellationToken.None));

            // Worth stating, but NOT the point of this test.
            Assert.Equal(409, exception.StatusCode);
        }

        // THE ASSERTION. A separate connection, after the scope above was disposed without a commit.
        Assert.Equal(0, await QueryScalar<long>(connectionString,
            $"SELECT COUNT(*) FROM customers WHERE tax_number = '{taxNumber}'"));
        Assert.Equal(0, await QueryScalar<long>(connectionString,
            $"SELECT COUNT(*) FROM employees WHERE normalized_work_email = '{takenEmail.ToUpperInvariant()}'"));

        // And the pre-existing account is untouched -- exactly one, still the Accountant.
        Assert.Equal(1, await QueryScalar<long>(connectionString,
            $"SELECT COUNT(*) FROM user_accounts WHERE normalized_login_email = '{takenEmail}'"));
        Assert.Equal("AccountantUser", await QueryScalar<string>(connectionString,
            $"SELECT role FROM user_accounts WHERE normalized_login_email = '{takenEmail}'"));

        // No invitation token either. A token surviving a rolled-back invitation is a credential for an
        // account that does not exist.
        Assert.Equal(0, await QueryScalar<long>(connectionString,
            "SELECT COUNT(*) FROM user_account_tokens WHERE purpose = 'Invitation'"));
    }

    // --- SQL helpers ---

    private static string Insert(
        string email,
        string customerId = "gen_random_uuid()",
        string given = "'Given'",
        string family = "'Family'",
        string status = "'Active'",
        string startDate = "'2026-01-05'",
        string? endDate = null,
        string? departedAt = null,
        string? accountId = null) =>
        "INSERT INTO employees (customer_id, given_name, family_name, work_email, "
        + "normalized_work_email, user_account_id, employment_start_date, employment_end_date, "
        + "departed_at, status) VALUES "
        + $"({customerId}, {given}, {family}, '{email}', '{email.ToUpperInvariant()}', "
        + $"{accountId ?? "NULL"}, {startDate}, {endDate ?? "NULL"}, {departedAt ?? "NULL"}, {status})";

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
