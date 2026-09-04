using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Shared.Migrations;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers;
using AccountantApp.Api.Slices.Customers.Application.Dtos;
using AccountantApp.Api.Slices.Customers.Application.Handlers;
using AccountantApp.Api.Slices.Customers.Core;
using AccountantApp.Api.Slices.Customers.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace AccountantApp.Tests.Customers;

public sealed class CustomersSchemaTests
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
    private const string ExpectedScriptKey =
        "Customers/Infrastructure/Migrations/20260830_001_CreateCustomersSchema.sql";

    [SkippableFact]
    public async Task Migration_mapping_search_and_transaction_work_against_real_postgres()
    {
        Skip.IfNot(await PostgresIsReachable(),
            "No PostgreSQL at localhost:5432. The Customers schema and transaction rollback are unverified.");

        var database = $"accountant_app_customers_test_{Guid.NewGuid():N}";
        await ExecuteOnAdmin($"CREATE DATABASE \"{database}\"");
        var connectionString = AdminConnectionString.Replace("Database=postgres", $"Database={database}");

        try
        {
            await SqlMigrationRunner.RunAsync(connectionString, AppContext.BaseDirectory);
            Assert.Equal(ExpectedScriptKey, await QueryScalar<string>(connectionString,
                $"SELECT script_name FROM schema_versions WHERE script_name = '{ExpectedScriptKey}'"));
            Assert.Equal("pg_trgm", await QueryScalar<string>(connectionString,
                "SELECT extname FROM pg_extension WHERE extname = 'pg_trgm'"));

            var options = new DbContextOptionsBuilder<CustomersDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            var audit = new NoOpAuditApi();
            var permissions = Permissions(audit);
            var user = new CurrentUser("admin", UserRole.AccountantAdmin);
            Guid customerId;

            await using (var db = new CustomersDbContext(options))
            {
                var created = await new CreateCustomerHandler(
                    db, permissions, new RequestTransaction(), audit).Handle(
                    ValidRequest("EL123456789"), user, CancellationToken.None);
                customerId = created.Id;
                Assert.Equal(new DateOnly(2026, 1, 15), created.OnboardedOn);
            }

            await using (var db = new CustomersDbContext(options))
            {
                var read = await new GetCustomerHandler(db, permissions).Handle(
                    new GetCustomerRequestDto { CustomerId = customerId }, user, CancellationToken.None);
                Assert.Equal("Acme Ltd", read.LegalName);
                Assert.Equal(new DateOnly(2026, 1, 15), read.OnboardedOn);

                var search = await new ListCustomersHandler(db, permissions).Handle(
                    new ListCustomersRequestDto { Search = "cme" }, user, CancellationToken.None);
                Assert.Equal(customerId, Assert.Single(search.Items).Id);
            }

            await using (var db = new CustomersDbContext(options))
            {
                var original = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.FromHours(2));
                var customer = CustomerEntity("EL-OFFSET", original);
                db.Customers.Add(customer);
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                var roundTripped = await db.Customers.SingleAsync(item => item.Id == customer.Id);
                Assert.Equal(original.UtcDateTime, roundTripped.CreatedAt.UtcDateTime);
            }

            var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(
                connectionString,
                "INSERT INTO customers (legal_name, tax_number, address_line1, address_city, " +
                "address_postal_code, address_country, contact_email, contact_phone, onboarded_on) " +
                "VALUES ('Duplicate', 'EL123456789', '1 Main', 'Athens', '10001', 'GR', " +
                "'duplicate@example.com', '+302100000000', DATE '2026-01-15')"));
            Assert.Equal("23505", duplicate.SqlState);

            await using (var db = new CustomersDbContext(options))
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new CreateCustomerHandler(
                        db, Permissions(new ThrowingAuditApi()), new RequestTransaction(), new ThrowingAuditApi())
                        .Handle(ValidRequest("EL-ROLLBACK"), user, CancellationToken.None));
                Assert.Equal("Audit store unavailable.", exception.Message);
            }

            await using (var db = new CustomersDbContext(options))
                Assert.False(await db.Customers.AnyAsync(item => item.TaxNumber == "EL-ROLLBACK"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnAdmin($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    private static PermissionChecker Permissions(IAuditApi audit) => new(
        [new CustomersActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);

    private static CreateCustomerRequestDto ValidRequest(string taxNumber) => new()
    {
        LegalName = "Acme Ltd",
        TaxNumber = taxNumber,
        AddressLine1 = "1 Main Street",
        AddressCity = "Athens",
        AddressPostalCode = "10001",
        AddressCountry = "GR",
        ContactEmail = "info@acme.example",
        ContactPhone = "+302100000000",
        OnboardedOn = new DateOnly(2026, 1, 15)
    };

    private static Customer CustomerEntity(string taxNumber, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        LegalName = "Offset Customer",
        TaxNumber = taxNumber,
        AddressLine1 = "1 Main Street",
        AddressCity = "Athens",
        AddressPostalCode = "10001",
        AddressCountry = "GR",
        ContactEmail = "offset@example.com",
        ContactPhone = "+302100000000",
        Status = CustomerStatus.Active,
        OnboardedOn = new DateOnly(2026, 1, 15),
        CreatedAt = createdAt,
        UpdatedAt = createdAt
    };

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

    private sealed class ThrowingAuditApi : IAuditApi
    {
        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Audit store unavailable.");

        public Task LogUnauthenticatedAsync(
            string actorIdentifier, AuditEntry entry, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Audit store unavailable.");
    }
}