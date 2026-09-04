using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Migrations;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.TicketTypes;
using AccountantApp.Api.Slices.TicketTypes.Application.Dtos;
using AccountantApp.Api.Slices.TicketTypes.Application.Handlers;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using AccountantApp.Api.Slices.TicketTypes.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AccountantApp.Tests.TicketTypes;

/// <summary>
/// The only test in this slice that touches real PostgreSQL. Everything the migration script
/// and the EF configurations exist to get right is invisible to the in-memory provider: it
/// ignores HasColumnName, unique constraints, NOT NULL, and TIMESTAMPTZ. A green in-memory
/// suite is therefore not evidence that the schema works.
///
/// Skipped, loudly, when no database is reachable — never silently passed.
/// </summary>
public class TicketTypesSchemaTests
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    private const string ExpectedScriptKey =
        "TicketTypes/Infrastructure/Migrations/20260829_001_CreateTicketTypesSchema.sql";

    [SkippableFact]
    public async Task Migration_applies_and_a_ticket_type_round_trips_through_real_postgres()
    {
        // Reported as skipped, with the reason, rather than passing silently: a green run
        // without a database has verified nothing about the schema.
        Skip.IfNot(await PostgresIsReachable(),
            "No PostgreSQL at localhost:5432 — the migration script has NOT been applied to a "
            + "database and the schema is unverified. Run `docker compose up -d db`.");

        var database = $"accountant_app_test_{Guid.NewGuid():N}";
        await ExecuteOnAdmin($"CREATE DATABASE \"{database}\"");
        var connectionString = AdminConnectionString.Replace("Database=postgres", $"Database={database}");

        try
        {
            // 1. The migration script is valid SQL that actually applies.
            await SqlMigrationRunner.RunAsync(connectionString, AppContext.BaseDirectory);

            // 2. It is tracked by slice-relative path, not by bare filename.
            var recorded = await QueryScalar<string>(connectionString,
                "SELECT script_name FROM schema_versions ORDER BY script_name LIMIT 1");
            Assert.Equal(ExpectedScriptKey, recorded);

            // 3. Write and read through the handlers. This is what catches a missing
            //    HasColumnName: EF's INSERT and SELECT must both agree with the DDL.
            var options = new DbContextOptionsBuilder<TicketTypesDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            var audit = new NoOpAuditApi();
            var permissions = new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);
            var user = new CurrentUser("user-1", UserRole.AccountantAdmin);

            Guid createdId;
            await using (var db = new TicketTypesDbContext(options))
            {
                var created = await new CreateTicketTypeHandler(db, permissions, new RequestTransaction(), audit).Handle(
                    new CreateTicketTypeRequestDto
                    {
                        Code = "PAYROLL_CERTIFICATE",
                        DisplayName = "Payroll Certificate",
                        Description = "Payroll certificate request",
                        Category = "Payroll",
                        Fields =
                        [
                            new CreateFieldDescriptorDto
                            {
                                Key = "employee_name",
                                Label = "Employee Name",
                                DataType = "SingleLineText",
                                DisplayOrder = 1,
                                Validation = new FieldValidationDto { RegexPattern = "^[A-Z]+$" }
                            }
                        ]
                    },
                    user, CancellationToken.None);
                createdId = created.Id;
            }

            await using (var db = new TicketTypesDbContext(options))
            {
                var read = await new GetTicketTypeHandler(db, permissions).Handle(
                    new GetTicketTypeRequestDto { TicketTypeId = createdId }, user, CancellationToken.None);

                Assert.Equal("PAYROLL_CERTIFICATE", read.Code);
                Assert.Equal(1, read.VersionNumber);
                var field = Assert.Single(read.Fields);
                Assert.Equal("employee_name", field.Key);
                Assert.Equal("Employee Name", field.Label);
                Assert.Equal("^[A-Z]+$", field.Validation.RegexPattern);
            }

            // 4. Uniqueness on code is case-insensitive at the database level. Inserted with
            //    raw SQL on purpose: the handler's own pre-check would mask a missing index.
            var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
                connectionString,
                "INSERT INTO ticket_types (code, display_name, category) " +
                "VALUES ('payroll_certificate', 'Lower Case Clash', 'Payroll')"));
            Assert.Equal("23505", duplicate.SqlState);   // unique_violation
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnAdmin($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

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

    private sealed class NoOpAuditApi : IAuditApi
    {
        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task LogUnauthenticatedAsync(
            string actorIdentifier, AuditEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
