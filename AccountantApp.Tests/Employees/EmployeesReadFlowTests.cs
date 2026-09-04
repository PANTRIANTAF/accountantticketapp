using System.Text.Json;
using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Application.Handlers;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static AccountantApp.Tests.Employees.EmployeesTestHarness;

namespace AccountantApp.Tests.Employees;

/// <summary>
/// The two read endpoints -- plan sections 4.3 and 4.4. This is where the highest-consequence defect in
/// the slice lives: an Employee reading a colleague's social-security number.
/// </summary>
public sealed class EmployeesReadFlowTests
{
    // --- 4.4 get ---

    // §11.3 test 1. THE test for this slice.
    //
    // WhereInCustomerScope narrows an Employee-role caller to their CUSTOMER, which is every colleague
    // they work with. The second filter -- UserAccountId == the caller's account -- is what narrows it to
    // themselves. The scope test everybody writes uses a DIFFERENT Customer's Employee, and that one
    // passes with the second filter missing. This one does not.
    [Fact]
    public async Task An_employee_reading_a_colleague_at_their_own_customer_gets_404()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();

        var self = EmployeeEntity(customerId, "Maria", "Papadopoulou", "maria@acme.example");
        self.UserAccountId = identity.Seed(self.Id, UserRole.Employee);
        var colleague = EmployeeEntity(customerId, "Kostas", "Dimitriou", "kostas@acme.example");
        db.Employees.AddRange(self, colleague);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Get(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = colleague.Id },
            SessionFor(self, UserRole.Employee), CancellationToken.None));

        // 404, never 403 -- a 403 would confirm the colleague exists.
        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task An_employee_reads_their_own_record_as_the_self_shape()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var self = EmployeeEntity(customerId);
        self.UserAccountId = identity.Seed(self.Id, UserRole.Employee);
        db.Employees.Add(self);
        await db.SaveChangesAsync();

        var result = await Get(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = self.Id },
            SessionFor(self, UserRole.Employee), CancellationToken.None);

        var dto = Assert.IsType<EmployeeSelfDto>(result);
        Assert.Equal(self.Id, dto.Id);
        Assert.Equal("Maria", dto.GivenName);

        // The serialised response, not just the static type: a narrower type is only a real control if the
        // keys are genuinely absent from the wire format.
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("ocialSecurityNumber", json, StringComparison.Ordinal);
        Assert.DoesNotContain("axIdentificationNumber", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SSN-987654", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TIN-123456", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_account_id_on_the_session_fails_closed()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var self = EmployeeEntity(customerId);
        self.UserAccountId = identity.Seed(self.Id, UserRole.Employee);
        db.Employees.Add(self);
        await db.SaveChangesAsync();

        // Not a Guid. AccountIdOf yields Guid.Empty, which matches no row -- so the read 404s rather than
        // falling through to every colleague.
        var user = new CurrentUser("not-a-guid", UserRole.Employee, customerId);

        var exception = await Assert.ThrowsAsync<AppException>(() => Get(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = self.Id }, user, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task A_customer_admin_reads_their_own_customers_employee_in_full()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var target = EmployeeEntity(customerId);
        target.UserAccountId = identity.Seed(target.Id, UserRole.Employee, "Suspended");
        db.Employees.Add(target);
        await db.SaveChangesAsync();
        var user = new CurrentUser(Guid.NewGuid().ToString(), UserRole.CustomerAdmin, customerId);

        var result = await Get(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = target.Id }, user, CancellationToken.None);

        var dto = Assert.IsType<EmployeeDetailDto>(result);
        // Both numbers ARE returned. The Office needs them and the employer supplied them; withholding them
        // from the employer would make the record useless for the work it exists to support.
        Assert.Equal("TIN-123456", dto.TaxIdentificationNumber);
        Assert.Equal("SSN-987654", dto.SocialSecurityNumber);
        // Role and AccountStatus are not columns -- they come from Identity.
        Assert.Equal(UserRole.Employee, dto.Role);
        Assert.Equal("Suspended", dto.AccountStatus);
    }

    [Fact]
    public async Task A_customer_admin_reading_another_customers_employee_gets_404_not_403()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var target = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(target);
        await db.SaveChangesAsync();
        var user = new CurrentUser(Guid.NewGuid().ToString(), UserRole.CustomerAdmin, Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<AppException>(() => Get(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = target.Id }, user, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
    }

    [Theory]
    [InlineData(UserRole.AccountantAdmin)]
    [InlineData(UserRole.AccountantUser)]
    public async Task An_accountant_reads_any_employee_in_full(UserRole role)
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var target = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(target);
        await db.SaveChangesAsync();

        var result = await Get(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = target.Id }, Accountant(role), CancellationToken.None);

        var dto = Assert.IsType<EmployeeDetailDto>(result);
        // Accountless: Role and AccountStatus stay null rather than defaulting to a role nobody holds.
        Assert.False(dto.HasAccount);
        Assert.Null(dto.Role);
        Assert.Null(dto.AccountStatus);
    }

    [Fact]
    public async Task Reading_a_departed_employee_still_works()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var departed = EmployeeEntity(Guid.NewGuid(), status: EmployeeStatus.Departed);
        db.Employees.Add(departed);
        await db.SaveChangesAsync();

        var result = await Get(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = departed.Id }, Accountant(), CancellationToken.None);

        // Departed records are retained and stay visible forever. Hiding them makes a Customer Admin think
        // the record is gone.
        Assert.Equal(EmployeeStatus.Departed, Assert.IsType<EmployeeDetailDto>(result).Status);
    }

    // --- 4.3 list ---

    [Fact]
    public async Task A_customer_admin_sees_only_their_own_customers_employees()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var own = Guid.NewGuid();
        var other = Guid.NewGuid();
        db.Employees.AddRange(
            EmployeeEntity(own, "Mine", "One", "one@own.example"),
            EmployeeEntity(own, "Mine", "Two", "two@own.example"),
            EmployeeEntity(other, "Theirs", "Three", "three@other.example"));
        await db.SaveChangesAsync();
        var user = new CurrentUser(Guid.NewGuid().ToString(), UserRole.CustomerAdmin, own);

        var page = await List(db, identity).Handle(
            new ListEmployeesRequestDto(), user, CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, item => Assert.Equal("Mine", item.GivenName));
    }

    [Fact]
    public async Task A_customer_admin_naming_another_customer_gets_403_not_an_empty_page()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var own = Guid.NewGuid();
        var user = new CurrentUser(Guid.NewGuid().ToString(), UserRole.CustomerAdmin, own);

        var exception = await Assert.ThrowsAsync<AppException>(() => List(db, identity).Handle(
            new ListEmployeesRequestDto { CustomerId = Guid.NewGuid() }, user, CancellationToken.None));

        // An empty page would be a filter that quietly means something else for one role, which is how a
        // Customer Admin comes to believe they have cross-Customer visibility.
        Assert.Equal(403, exception.StatusCode);
    }

    [Fact]
    public async Task The_employee_role_may_not_list_at_all()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var user = new CurrentUser(Guid.NewGuid().ToString(), UserRole.Employee, Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<AppException>(() => new ListEmployeesHandler(
                db, Permissions(audit), new FakeIdentityApi())
            .Handle(new ListEmployeesRequestDto(), user, CancellationToken.None));

        // The matrix gives them "own record only", and a list of one is still a list endpoint.
        Assert.Equal(403, exception.StatusCode);
        Assert.Equal(Api.Slices.Audit.ExternalInterfaces.AuditActions.PermissionDenied,
            Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task The_default_list_includes_departed_employees()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        db.Employees.AddRange(
            EmployeeEntity(customerId, "Active", "Person", "a@acme.example"),
            EmployeeEntity(customerId, "Gone", "Person", "g@acme.example",
                status: EmployeeStatus.Departed));
        await db.SaveChangesAsync();

        var all = await List(db, identity).Handle(
            new ListEmployeesRequestDto(), Accountant(), CancellationToken.None);
        var activeOnly = await List(db, identity).Handle(
            new ListEmployeesRequestDto { Status = EmployeeStatus.Active },
            Accountant(), CancellationToken.None);

        // No filter means BOTH. A default that hid Departed rows would make a Customer Admin think the
        // record had been deleted -- and nothing in this slice deletes.
        Assert.Equal(2, all.TotalCount);
        Assert.Equal(1, activeOnly.TotalCount);
    }

    [Fact]
    public async Task An_unknown_status_filter_is_422()
    {
        await using var db = NewDb();

        var exception = await Assert.ThrowsAsync<AppException>(() => List(db, new FakeIdentityApi())
            .Handle(new ListEmployeesRequestDto { Status = "Deleted" },
                Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task The_has_account_filter_separates_invited_from_accountless()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var accounted = EmployeeEntity(customerId, "With", "Account", "w@acme.example");
        accounted.UserAccountId = identity.Seed(accounted.Id);
        db.Employees.AddRange(accounted, EmployeeEntity(customerId, "No", "Account", "n@acme.example"));
        await db.SaveChangesAsync();

        var withAccount = await List(db, identity).Handle(
            new ListEmployeesRequestDto { HasAccount = true }, Accountant(), CancellationToken.None);
        var without = await List(db, identity).Handle(
            new ListEmployeesRequestDto { HasAccount = false }, Accountant(), CancellationToken.None);

        Assert.Equal(accounted.Id, Assert.Single(withAccount.Items).Id);
        Assert.Equal("No", Assert.Single(without.Items).GivenName);
    }

    [Fact]
    public async Task An_oversized_page_size_is_clamped_rather_than_rejected()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        for (var index = 0; index < 60; index++)
            db.Employees.Add(EmployeeEntity(
                customerId, $"Given{index:D2}", $"Family{index:D2}", $"person{index:D2}@acme.example"));
        await db.SaveChangesAsync();

        var page = await List(db, identity).Handle(
            new ListEmployeesRequestDto { PageSize = 5000 }, Accountant(), CancellationToken.None);

        // Clamped to MaxPageSize, not a 422. A caller asking for too much gets the most they may have.
        Assert.Equal(50, page.Items.Count);
        Assert.Equal(50, page.PageSize);
        Assert.Equal(60, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
    }

    [Fact]
    public async Task Paging_is_stable_across_pages_when_two_employees_share_both_names()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        for (var index = 0; index < 6; index++)
            db.Employees.Add(EmployeeEntity(customerId, "Same", "Name", $"same{index}@acme.example"));
        await db.SaveChangesAsync();
        var handler = List(db, identity);

        var first = await handler.Handle(
            new ListEmployeesRequestDto { PageSize = 3, PageNumber = 1 },
            Accountant(), CancellationToken.None);
        var second = await handler.Handle(
            new ListEmployeesRequestDto { PageSize = 3, PageNumber = 2 },
            Accountant(), CancellationToken.None);

        // The id tiebreaker. Without it the sort is unstable, and paging silently skips and repeats rows --
        // six identical names is not a contrived case at one Customer.
        var ids = first.Items.Concat(second.Items).Select(item => item.Id).ToList();
        Assert.Equal(6, ids.Distinct().Count());
    }

    [Fact]
    public async Task Roles_are_resolved_with_one_bulk_identity_call_not_one_per_row()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        for (var index = 0; index < 12; index++)
        {
            var employee = EmployeeEntity(
                customerId, $"Given{index:D2}", $"Family{index:D2}", $"person{index:D2}@acme.example");
            employee.UserAccountId = identity.Seed(
                employee.Id, index == 0 ? UserRole.CustomerAdmin : UserRole.Employee);
            db.Employees.Add(employee);
        }

        db.Employees.Add(EmployeeEntity(customerId, "Zeta", "Zulu", "zeta@acme.example"));
        await db.SaveChangesAsync();

        var page = await List(db, identity).Handle(
            new ListEmployeesRequestDto { PageSize = 50 }, Accountant(), CancellationToken.None);

        // One call. At the maximum page size of 50 a FindAsync per row would be 51 queries for one request.
        Assert.Equal(1, identity.FindManyCallCount);
        Assert.Equal(UserRole.CustomerAdmin, page.Items.Single(item => item.GivenName == "Given00").Role);
        Assert.Equal(11, page.Items.Count(item => item.Role == UserRole.Employee));

        // The accountless row carries a NULL role, not "Employee". Defaulting it would show somebody who
        // has never been invited as holding a role -- the SPA renders null as "not invited".
        var accountless = page.Items.Single(item => item.GivenName == "Zeta");
        Assert.False(accountless.HasAccount);
        Assert.Null(accountless.Role);
    }

    [Fact]
    public async Task A_page_of_only_accountless_employees_asks_identity_nothing()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        db.Employees.Add(EmployeeEntity(Guid.NewGuid()));
        await db.SaveChangesAsync();

        await List(db, identity).Handle(
            new ListEmployeesRequestDto(), Accountant(), CancellationToken.None);

        Assert.Equal(0, identity.FindManyCallCount);
    }

    [Fact]
    public void The_summary_row_carries_no_contact_details_or_identifying_numbers()
    {
        // The list row is the widest-audience read in the slice. Anything on it is visible to every
        // Customer Admin for every colleague, so the field list is the control.
        foreach (var forbidden in new[]
                 {
                     "SocialSecurityNumber", "TaxIdentificationNumber", "WorkEmail", "ContactPhone",
                     "EmploymentStartDate", "EmploymentEndDate", "UserAccountId"
                 })
            Assert.Null(typeof(EmployeeSummaryDto).GetProperty(forbidden));
    }

    // --- helpers ---

    private static GetEmployeeHandler Get(EmployeesDbContext db, FakeIdentityApi identity) =>
        new(db, Permissions(new TestAuditApi()), identity);

    private static ListEmployeesHandler List(EmployeesDbContext db, FakeIdentityApi identity) =>
        new(db, Permissions(new TestAuditApi()), identity);
}
