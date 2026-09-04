using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static AccountantApp.Tests.Employees.EmployeesTestHarness;

namespace AccountantApp.Tests.Employees;

/// <summary>
/// What leaves this slice -- IEmployeeApi -- and the catalogue that decides who may call it at all.
/// Plan sections 6, 7 and the reflection rows of section 11.2.
/// </summary>
public sealed class EmployeesContractTests
{
    // --- IEmployeeApi ---

    [Fact]
    public async Task IsActiveAsync_is_false_for_an_unknown_employee()
    {
        await using var db = NewDb();

        // Never true, never a throw. This is what Tickets asks before accepting a Ticket for a Subject, and
        // a "?? true" anywhere in the chain would let a Ticket be raised for somebody who does not exist.
        Assert.False(await new EmployeeApi(db).IsActiveAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task IsActiveAsync_is_false_for_a_departed_employee()
    {
        await using var db = NewDb();
        var departed = EmployeeEntity(Guid.NewGuid(), status: EmployeeStatus.Departed);
        db.Employees.Add(departed);
        await db.SaveChangesAsync();

        Assert.False(await new EmployeeApi(db).IsActiveAsync(departed.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsActiveAsync_reflects_a_departure_that_happened_after_an_earlier_true_answer()
    {
        await using var db = NewDb();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var api = new EmployeeApi(db);

        Assert.True(await api.IsActiveAsync(employee.Id, CancellationToken.None));

        employee.Status = EmployeeStatus.Departed;
        employee.EmploymentEndDate = new DateOnly(2026, 6, 30);
        employee.DepartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Nothing is cached, on the same instance. A status change is precisely the event a cache would
        // hide, and the consequence is Tickets accepting work for somebody who has left.
        Assert.False(await api.IsActiveAsync(employee.Id, CancellationToken.None));
    }

    [Fact]
    public async Task FindAsync_answers_regardless_of_who_is_asking()
    {
        await using var db = NewDb();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        // The contract takes no CurrentUser and applies no scope filter, deliberately -- it is called on
        // behalf of every role including Accountants. THE CALLER AUTHORIZES. A Tickets handler that passes
        // an id it has not checked has made a cross-Customer read, and nothing here will stop it.
        var summary = await new EmployeeApi(db).FindAsync(employee.Id, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal(employee.CustomerId, summary.CustomerId);
        Assert.Equal("Maria Papadopoulou", summary.FullName);
        Assert.True(summary.IsActive);
    }

    [Fact]
    public async Task FindAsync_is_null_for_an_unknown_employee()
    {
        await using var db = NewDb();

        Assert.Null(await new EmployeeApi(db).FindAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task FindManyAsync_above_the_cap_throws_rather_than_running_the_query()
    {
        await using var db = NewDb();
        var ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList();

        // 501, one over. The cap matches ICustomerApi and IIdentityApi: an unbounded IN list is a query a
        // caller can make arbitrarily expensive by looping, and a silent truncation would return a
        // dictionary missing rows the caller believes it asked for.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EmployeeApi(db).FindManyAsync(ids, CancellationToken.None));

        Assert.Contains("500", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindManyAsync_at_the_cap_is_allowed_and_omits_unknown_ids()
    {
        await using var db = NewDb();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var ids = Enumerable.Range(0, 499).Select(_ => Guid.NewGuid()).Append(employee.Id).ToList();

        var found = await new EmployeeApi(db).FindManyAsync(ids, CancellationToken.None);

        // Exactly 500 is fine, and the 499 unknown ids are simply absent -- not null entries the caller has
        // to filter, and not an error, because a list-rendering caller cannot know which rows still exist.
        Assert.Equal(employee.Id, Assert.Single(found).Key);
    }

    [Fact]
    public async Task FindManyAsync_tolerates_the_same_id_twice()
    {
        await using var db = NewDb();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        // A caller rendering a list of Tickets whose Subject is the same person twice is the normal case.
        // Without the Distinct the dictionary build throws on a duplicate key.
        var found = await new EmployeeApi(db).FindManyAsync(
            [employee.Id, employee.Id], CancellationToken.None);

        Assert.Single(found);
    }

    [Fact]
    public async Task FindByAccountAsync_resolves_the_employee_behind_a_session()
    {
        await using var db = NewDb();
        var accountId = Guid.NewGuid();
        var employee = EmployeeEntity(Guid.NewGuid(), userAccountId: accountId);
        db.Employees.AddRange(employee, EmployeeEntity(Guid.NewGuid(), "Other", "Person", "o@acme.example"));
        await db.SaveChangesAsync();

        var summary = await new EmployeeApi(db).FindByAccountAsync(accountId, CancellationToken.None);

        // How Tickets answers "which Employee is the caller" for Subject-based read access.
        Assert.Equal(employee.Id, summary?.Id);
        Assert.Null(await new EmployeeApi(db).FindByAccountAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ListActiveByCustomerAsync_pages_one_customers_active_employees()
    {
        await using var db = NewDb();
        var customerId = Guid.NewGuid();
        for (var index = 0; index < 60; index++)
            db.Employees.Add(EmployeeEntity(
                customerId, $"Given{index:D2}", $"Family{index:D2}", $"person{index:D2}@acme.example"));
        db.Employees.Add(EmployeeEntity(customerId, "Gone", "Person", "g@acme.example",
            status: EmployeeStatus.Departed));
        db.Employees.Add(EmployeeEntity(Guid.NewGuid(), "Theirs", "Person", "t@other.example"));
        await db.SaveChangesAsync();

        var page = await new EmployeeApi(db).ListActiveByCustomerAsync(
            customerId, 1, 25, CancellationToken.None);

        // TotalCount counts the Active rows of THIS Customer -- 60 -- not the 25 on the page and not the 62
        // in the table. It is the number a Subject picker needs to know it is not showing everybody, which
        // is what replaced the original unpaginated contract: a silent cap makes a Subject un-pickable with
        // no error at all, and a count is how the caller avoids becoming that cap.
        Assert.Equal(60, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(25, page.Items.Count);
        Assert.All(page.Items, summary => Assert.Equal(customerId, summary.CustomerId));
        Assert.All(page.Items, summary => Assert.Equal(EmployeeStatus.Active, summary.Status));
        Assert.Equal("Family00", page.Items[0].FamilyName);

        // The last page is short, and its first row continues where page 2 stopped -- no row on two pages
        // and none on neither, which is what the ThenBy(Id) tiebreak is for.
        var last = await new EmployeeApi(db).ListActiveByCustomerAsync(
            customerId, 3, 25, CancellationToken.None);
        Assert.Equal(10, last.Items.Count);
        Assert.Equal("Family50", last.Items[0].FamilyName);
    }

    [Fact]
    public async Task ListActiveByCustomerAsync_normalizes_a_hostile_page_request()
    {
        await using var db = NewDb();
        var customerId = Guid.NewGuid();
        for (var index = 0; index < 60; index++)
            db.Employees.Add(EmployeeEntity(
                customerId, $"Given{index:D2}", $"Family{index:D2}", $"person{index:D2}@acme.example"));
        await db.SaveChangesAsync();

        // Page 0 would be a negative OFFSET and 10,000 rows would be the whole table in one response. The
        // contract normalizes rather than throwing, because the caller is another slice and a 500 from a
        // Subject picker is worse than a first page.
        var page = await new EmployeeApi(db).ListActiveByCustomerAsync(
            customerId, 0, 10_000, CancellationToken.None);

        Assert.Equal(1, page.PageNumber);
        Assert.Equal(PaginatedQuery.MaxPageSize, page.PageSize);
        Assert.Equal(PaginatedQuery.MaxPageSize, page.Items.Count);
        Assert.Equal(60, page.TotalCount);
    }

    [Fact]
    public void The_external_summary_carries_nothing_sensitive()
    {
        // An ExternalInterface that carries a social-security number makes every consumer a disclosure
        // path, forever, and consumers are added by people who never read this slice.
        foreach (var forbidden in new[]
                 {
                     "TaxIdentificationNumber", "SocialSecurityNumber", "WorkEmail", "NormalizedWorkEmail",
                     "ContactPhone", "EmploymentStartDate", "EmploymentEndDate", "DepartedAt", "JobTitle"
                 })
            Assert.Null(typeof(EmployeeSummary).GetProperty(forbidden));

        // CustomerId IS present, deliberately: Tickets needs it to scope a Ticket's Subject, and it is not
        // sensitive.
        Assert.NotNull(typeof(EmployeeSummary).GetProperty("CustomerId"));
    }

    [Fact]
    public void The_contract_stays_read_only()
    {
        // No RegisterAsync, no DepartAsync, no write method of any kind. A write here would be a way for
        // another slice to change Employee records, which no row in the authorization matrix authorizes.
        var writeShaped = typeof(IEmployeeApi).GetMethods()
            .Where(method => !method.Name.StartsWith("Find", StringComparison.Ordinal)
                          && !method.Name.StartsWith("List", StringComparison.Ordinal)
                          && !method.Name.StartsWith("Is", StringComparison.Ordinal))
            .Select(method => method.Name)
            .ToList();

        Assert.Empty(writeShaped);
    }

    // --- the catalogue ---

    [Fact]
    public async Task Every_action_refuses_every_role_the_matrix_does_not_grant()
    {
        var catalogue = new EmployeesActionCatalogue();
        var allRoles = Enum.GetValues<UserRole>();

        foreach (var (action, permitted) in catalogue.Actions)
        foreach (var role in allRoles.Except(permitted))
        {
            var audit = new TestAuditApi();
            var user = new CurrentUser(
                Guid.NewGuid().ToString(), role,
                role is UserRole.CustomerAdmin or UserRole.Employee ? Guid.NewGuid() : null);

            var exception = await Assert.ThrowsAsync<AppException>(
                () => Permissions(audit).RequireAsync(user, action, ct: CancellationToken.None));

            Assert.Equal(403, exception.StatusCode);

            // Every refusal is recorded. A denial nobody can see afterwards is a denial nobody investigates.
            var entry = Assert.Single(audit.Entries);
            Assert.Equal(AuditActions.PermissionDenied, entry.Action);
            Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        }
    }

    [Fact]
    public async Task Every_action_admits_every_role_the_matrix_grants()
    {
        var catalogue = new EmployeesActionCatalogue();

        foreach (var (action, permitted) in catalogue.Actions)
        foreach (var role in permitted)
        {
            var audit = new TestAuditApi();
            var user = new CurrentUser(
                Guid.NewGuid().ToString(), role,
                role is UserRole.CustomerAdmin or UserRole.Employee ? Guid.NewGuid() : null);

            await Permissions(audit).RequireAsync(user, action, ct: CancellationToken.None);

            // A permitted call is not an auditable event by itself; the handler decides what to record.
            Assert.Empty(audit.Entries);
        }
    }

    [Fact]
    public async Task An_action_name_the_catalogue_does_not_know_is_refused()
    {
        var audit = new TestAuditApi();

        var exception = await Assert.ThrowsAsync<AppException>(() => Permissions(audit).RequireAsync(
            Accountant(), "DeleteEmployee", ct: CancellationToken.None));

        // Fails CLOSED. A typo in an action name must not become a handler that authorizes everybody, and
        // "DeleteEmployee" is a good example: nothing in this slice deletes.
        Assert.Equal(403, exception.StatusCode);
        Assert.Equal(AuditActions.PermissionDenied, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public void Only_onboarding_is_reserved_to_accountant_admin()
    {
        var catalogue = new EmployeesActionCatalogue();

        // AccountantUser gets everything AccountantAdmin has in this slice except the one operation that
        // creates a Customer, because creating a Customer is an AccountantAdmin power.
        var adminOnly = catalogue.Actions
            .Where(pair => pair.Value.Contains(UserRole.AccountantAdmin)
                        && !pair.Value.Contains(UserRole.AccountantUser))
            .Select(pair => pair.Key)
            .ToList();

        Assert.Equal(["OnboardCustomer"], adminOnly);
        Assert.Equal("Employees", catalogue.SliceName);
    }

    [Fact]
    public void The_employee_role_may_only_view_and_edit_its_own_contact_details()
    {
        var catalogue = new EmployeesActionCatalogue();

        var forEmployees = catalogue.Actions
            .Where(pair => pair.Value.Contains(UserRole.Employee))
            .Select(pair => pair.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // Two entries, and both are then narrowed to "own record only" inside the handlers -- the catalogue
        // can express who may call, not which rows. ListEmployees is absent: a list of one is still a list.
        Assert.Equal(["UpdateOwnContact", "ViewEmployee"], forEmployees);
    }
}
