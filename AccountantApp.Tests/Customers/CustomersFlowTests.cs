using System.Text.Json;
using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Tests.TestDoubles;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers.Application.Dtos;
using AccountantApp.Api.Slices.Customers.Application.Handlers;
using AccountantApp.Api.Slices.Customers.Core;
using AccountantApp.Api.Slices.Customers.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers;
using AccountantApp.Api.Slices.Customers.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccountantApp.Tests.Customers;

public sealed class CustomersFlowTests
{
    [Theory]
    [InlineData(UserRole.AccountantUser)]
    [InlineData(UserRole.CustomerAdmin)]
    [InlineData(UserRole.Employee)]
    public async Task Only_accountant_admin_can_create_a_customer(UserRole role)
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var handler = new CreateCustomerHandler(db, Permissions(audit), new NoOpRequestTransaction(), audit);

        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            ValidCreateRequest(), User(role), CancellationToken.None));

        Assert.Equal(403, exception.StatusCode);
        Assert.Empty(db.Customers);
        Assert.Equal(AuditActions.PermissionDenied, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task Accountant_admin_create_trims_values_and_sets_active_status()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var handler = new CreateCustomerHandler(db, Permissions(audit), new NoOpRequestTransaction(), audit);
        var request = ValidCreateRequest();
        request.LegalName = "  Acme Ltd  ";

        var result = await handler.Handle(request, User(UserRole.AccountantAdmin), CancellationToken.None);

        Assert.Equal("Acme Ltd", result.LegalName);
        Assert.Equal(CustomerStatus.Active, result.Status);
        Assert.Equal(AuditActions.CustomerCreated, Assert.Single(audit.Entries).Action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_legal_name_is_rejected(string legalName)
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var request = ValidCreateRequest();
        request.LegalName = legalName;

        var exception = await Assert.ThrowsAsync<AppException>(() => new CreateCustomerHandler(
            db, Permissions(audit), new NoOpRequestTransaction(), audit).Handle(
                request, User(UserRole.AccountantAdmin), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Empty(db.Customers);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Customer_side_callers_receive_404_for_another_customer()
    {
        await using var db = CreateDb();
        var ownCustomer = CustomerEntity("OWN");
        var otherCustomer = CustomerEntity("OTHER");
        db.Customers.AddRange(ownCustomer, otherCustomer);
        await db.SaveChangesAsync();
        var user = new CurrentUser("customer-user", UserRole.CustomerAdmin, ownCustomer.Id);

        var exception = await Assert.ThrowsAsync<AppException>(() => new GetCustomerHandler(
            db, Permissions(new TestAuditApi())).Handle(
                new GetCustomerRequestDto { CustomerId = otherCustomer.Id }, user, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task Employee_uses_limited_projection_and_cannot_reach_full_detail()
    {
        await using var db = CreateDb();
        var customer = CustomerEntity("SAFE");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var user = new CurrentUser("employee", UserRole.Employee, customer.Id);
        var permissions = Permissions(new TestAuditApi());

        var self = await new GetOwnCustomerHandler(db, permissions).Handle(user, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<AppException>(() => new GetCustomerHandler(db, permissions).Handle(
            new GetCustomerRequestDto { CustomerId = customer.Id }, user, CancellationToken.None));

        Assert.Equal(customer.Id, self.Id);
        Assert.Null(typeof(CustomerSelfDto).GetProperty("TaxNumber"));
        Assert.Equal(403, exception.StatusCode);
    }

    [Fact]
    public async Task Contact_update_audits_distinct_before_and_after_values()
    {
        await using var db = CreateDb();
        var customer = CustomerEntity("UPDATE");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var audit = new TestAuditApi();
        var request = new UpdateCustomerContactRequestDto
        {
            CustomerId = customer.Id,
            AddressLine1 = "2 New Street",
            AddressCity = "Athens",
            AddressPostalCode = "10002",
            AddressCountry = "GR",
            ContactEmail = "new@example.com",
            ContactPhone = "+302100000001"
        };

        await new UpdateCustomerContactHandler(db, Permissions(audit), new NoOpRequestTransaction(), audit)
            .Handle(request, User(UserRole.AccountantUser), CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        var before = JsonSerializer.Serialize(entry.Before);
        var after = JsonSerializer.Serialize(entry.After);
        Assert.Contains("1 Main Street", before);
        Assert.Contains("2 New Street", after);
        Assert.DoesNotContain("2 New Street", before);
    }

    [Fact]
    public async Task Suspend_and_reactivate_enforce_straight_line_transitions()
    {
        await using var db = CreateDb();
        var customer = CustomerEntity("STATUS");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var audit = new TestAuditApi();
        var permissions = Permissions(audit);
        var request = new SetCustomerStatusRequestDto { CustomerId = customer.Id, Reason = "Requested" };

        var suspended = await new SuspendCustomerHandler(db, permissions, new NoOpRequestTransaction(), audit)
            .Handle(request, User(UserRole.AccountantAdmin), CancellationToken.None);
        var duplicate = await Assert.ThrowsAsync<AppException>(() => new SuspendCustomerHandler(
            db, permissions, new NoOpRequestTransaction(), audit).Handle(
                request, User(UserRole.AccountantAdmin), CancellationToken.None));
        var reactivated = await new ReactivateCustomerHandler(db, permissions, new NoOpRequestTransaction(), audit)
            .Handle(request, User(UserRole.AccountantAdmin), CancellationToken.None);

        Assert.Equal(CustomerStatus.Suspended, suspended.Status);
        Assert.Equal(422, duplicate.StatusCode);
        Assert.Equal(CustomerStatus.Active, reactivated.Status);
        Assert.Equal(2, audit.Entries.Count);
    }

    [Fact]
    public async Task Customer_api_is_fail_closed_and_caps_bulk_lookups()
    {
        await using var db = CreateDb();
        var active = CustomerEntity("ACTIVE");
        var suspended = CustomerEntity("SUSPENDED");
        suspended.Status = CustomerStatus.Suspended;
        db.Customers.AddRange(active, suspended);
        await db.SaveChangesAsync();
        var api = new CustomerApi(db, new NoOpRequestTransaction(), new TestAuditApi());

        Assert.True(await api.IsActiveAsync(active.Id));
        Assert.False(await api.IsActiveAsync(suspended.Id));
        Assert.False(await api.IsActiveAsync(Guid.NewGuid()));
        Assert.Null(await api.FindAsync(Guid.NewGuid()));
        var found = await api.FindManyAsync([active.Id, suspended.Id, Guid.NewGuid()]);
        Assert.Equal(2, found.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.FindManyAsync(Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList()));
    }

    private static CustomersDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CustomersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PermissionChecker Permissions(IAuditApi audit) => new(
        [new CustomersActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);

    private static CurrentUser User(UserRole role) => new(
        "user-1", role, role is UserRole.CustomerAdmin or UserRole.Employee ? Guid.NewGuid() : null);

    private static CreateCustomerRequestDto ValidCreateRequest() => new()
    {
        LegalName = "Acme Ltd",
        TaxNumber = $"TAX-{Guid.NewGuid():N}",
        AddressLine1 = "1 Main Street",
        AddressCity = "Athens",
        AddressPostalCode = "10001",
        AddressCountry = "GR",
        ContactEmail = "info@acme.example",
        ContactPhone = "+302100000000",
        OnboardedOn = new DateOnly(2026, 1, 15)
    };

    private static Customer CustomerEntity(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        LegalName = $"Customer {suffix}",
        TaxNumber = $"TAX-{suffix}-{Guid.NewGuid():N}",
        AddressLine1 = "1 Main Street",
        AddressCity = "Athens",
        AddressPostalCode = "10001",
        AddressCountry = "GR",
        ContactEmail = $"{suffix.ToLowerInvariant()}@example.com",
        ContactPhone = "+302100000000",
        Status = CustomerStatus.Active,
        OnboardedOn = new DateOnly(2026, 1, 15),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class TestAuditApi : IAuditApi
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task LogUnauthenticatedAsync(
            string actorIdentifier, AuditEntry entry, CancellationToken cancellationToken = default) =>
            LogAsync(entry, cancellationToken);
    }
}