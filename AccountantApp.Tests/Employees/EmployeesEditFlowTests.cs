using System.Text.Json;
using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Application.Handlers;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using static AccountantApp.Tests.Employees.EmployeesTestHarness;

namespace AccountantApp.Tests.Employees;

/// <summary>
/// The two write endpoints that change an Employee's own fields -- plan sections 4.9 and 4.10. The second
/// is the only endpoint in the slice an Employee may write through, and the only one whose request carries
/// no target at all.
/// </summary>
public sealed class EmployeesEditFlowTests
{
    // --- 4.9 update ---

    [Fact]
    public async Task An_accountant_may_correct_a_departed_employees_record()
    {
        await using var db = NewDb();
        var employee = EmployeeEntity(Guid.NewGuid(), status: EmployeeStatus.Departed);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await Update(db).Handle(
            Request(employee.Id, family: "Papadopoulos"), Accountant(), CancellationToken.None);

        // Correcting a misspelled name or a wrong tax number after somebody has left is ordinary work, and
        // the record is retained forever. Refusing it would leave a permanent error in the file.
        Assert.Equal("Papadopoulos", result.FamilyName);
        Assert.Equal(EmployeeStatus.Departed, result.Status);
    }

    [Fact]
    public async Task A_start_date_after_a_recorded_end_date_is_422()
    {
        await using var db = NewDb();
        var employee = EmployeeEntity(Guid.NewGuid(), status: EmployeeStatus.Departed);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var request = Request(employee.Id);
        // The seeded end date is 2026-06-30.
        request.EmploymentStartDate = new DateOnly(2026, 7, 15);

        var exception = await Assert.ThrowsAsync<AppException>(() => Update(db).Handle(
            request, Accountant(), CancellationToken.None));

        // ck_employees_dates would reject this too; pre-checking gives a message that says what is wrong
        // rather than a constraint name.
        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task Update_clears_a_nullable_field_that_is_omitted()
    {
        await using var db = NewDb();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var request = Request(employee.Id);
        request.WorkEmail = null;
        request.ContactPhone = null;

        await Update(db).Handle(request, Accountant(), CancellationToken.None);

        // Every field is replaced with what was sent. A partial-update shape would need a way to
        // distinguish "absent" from "null", and the one it would reach for -- treating null as absent --
        // makes clearing an email impossible.
        var stored = await db.Employees.AsNoTracking().SingleAsync();
        Assert.Null(stored.WorkEmail);
        Assert.Null(stored.NormalizedWorkEmail);
        Assert.Null(stored.ContactPhone);
    }

    [Fact]
    public async Task Update_to_a_colleagues_work_email_is_409()
    {
        await using var db = NewDb();
        var customerId = Guid.NewGuid();
        var target = EmployeeEntity(customerId, "Maria", "Papadopoulou", "maria@acme.example");
        db.Employees.AddRange(target, EmployeeEntity(customerId, "Kostas", "Dimitriou", "kostas@acme.example"));
        await db.SaveChangesAsync();
        var request = Request(target.Id);
        request.WorkEmail = "KOSTAS@ACME.EXAMPLE";

        var exception = await Assert.ThrowsAsync<AppException>(() => Update(db).Handle(
            request, Accountant(), CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task Re_saving_an_employees_own_unchanged_email_is_not_a_conflict()
    {
        await using var db = NewDb();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        // The duplicate check must exclude the row being edited. Without the `other.Id != employee.Id`
        // clause every update that touched anything else would 409 on the record's own address.
        var result = await Update(db).Handle(
            Request(employee.Id, family: "Papadopoulos"), Accountant(), CancellationToken.None);

        Assert.Equal("maria@acme.example", result.WorkEmail);
    }

    [Fact]
    public async Task Update_audit_names_the_changed_sensitive_fields_and_carries_neither_value()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var request = Request(employee.Id);
        request.TaxIdentificationNumber = "TIN-NEW-VALUE";
        request.SocialSecurityNumber = "SSN-987654";   // unchanged

        await new UpdateEmployeeHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), new FakeIdentityApi(), audit)
            .Handle(request, Accountant(), CancellationToken.None);

        var entry = Assert.Single(audit.WithAction(AuditActions.EmployeeEdited));
        var before = JsonSerializer.Serialize(entry.Before);
        var after = JsonSerializer.Serialize(entry.After);

        // The NAMES of the fields that changed, never the values -- Redaction covers neither field name, so
        // a value here would sit in the audit table forever. Only the tax number moved, so the social
        // security number must not be listed: "everything sensitive" would make the list useless for
        // answering "who changed somebody's tax number".
        Assert.Contains(
            """
            "ChangedSensitiveFields":["TaxIdentificationNumber"]
            """, after, StringComparison.Ordinal);
        Assert.DoesNotContain("SSN-987654", after, StringComparison.Ordinal);
        Assert.DoesNotContain("SSN-987654", before, StringComparison.Ordinal);
        Assert.DoesNotContain("TIN-NEW-VALUE", after, StringComparison.Ordinal);
        Assert.DoesNotContain("TIN-123456", before, StringComparison.Ordinal);

        // Before and After are genuinely different, which is what makes the entry worth keeping.
        Assert.Contains("Papadopoulou", before, StringComparison.Ordinal);
        Assert.Contains("Papadopoulos", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_customer_admin_updating_another_customers_employee_gets_404()
    {
        await using var db = NewDb();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var user = new CurrentUser(Guid.NewGuid().ToString(), UserRole.CustomerAdmin, Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<AppException>(() => Update(db).Handle(
            Request(employee.Id), user, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public void The_update_request_cannot_reach_the_fields_other_endpoints_own()
    {
        // A property that exists is a property somebody binds. CustomerId is immutable, UserAccountId is the
        // invite endpoint's, and Status, EmploymentEndDate and DepartedAt are the departure endpoint's.
        foreach (var forbidden in new[]
                 {
                     "CustomerId", "Status", "EmploymentEndDate", "DepartedAt", "UserAccountId", "Role"
                 })
            Assert.Null(typeof(UpdateEmployeeRequestDto).GetProperty(forbidden));
    }

    // --- 4.10 update-own-contact ---

    [Fact]
    public async Task An_employee_updates_only_their_own_phone_and_email()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var self = EmployeeEntity(customerId);
        self.UserAccountId = identity.Seed(self.Id, UserRole.Employee);
        db.Employees.Add(self);
        await db.SaveChangesAsync();

        var result = await new UpdateOwnContactHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), audit)
            .Handle(new UpdateOwnContactRequestDto
            {
                WorkEmail = "maria.new@acme.example",
                ContactPhone = "+302109999999"
            }, SessionFor(self, UserRole.Employee), CancellationToken.None);

        var stored = await db.Employees.AsNoTracking().SingleAsync();
        Assert.Equal("maria.new@acme.example", stored.WorkEmail);
        Assert.Equal("MARIA.NEW@ACME.EXAMPLE", stored.NormalizedWorkEmail);
        Assert.Equal("+302109999999", stored.ContactPhone);

        // Nothing else moved. The field list is the control on what may change: a person cannot promote
        // themselves, backdate their employment, or alter the numbers the Office files taxes with.
        Assert.Equal("Maria", stored.GivenName);
        Assert.Equal("Papadopoulou", stored.FamilyName);
        Assert.Equal("Bookkeeper", stored.JobTitle);
        Assert.Equal("SSN-987654", stored.SocialSecurityNumber);
        Assert.Equal("TIN-123456", stored.TaxIdentificationNumber);
        Assert.Equal(new DateOnly(2026, 1, 5), stored.EmploymentStartDate);
        Assert.Equal(EmployeeStatus.Active, stored.Status);

        // The notice is on every success, not only when the email changed: a person editing their work
        // email will otherwise assume they have just changed how they log in. They have not.
        Assert.Equal(UpdateOwnContactHandler.LoginEmailNotice, result.Notice);
        Assert.Equal(AuditActions.EmployeeEdited, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task A_customer_admin_may_also_update_their_own_contact_details()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var self = EmployeeEntity(customerId, "Ada", "Admin", "ada@acme.example");
        self.UserAccountId = identity.Seed(self.Id, UserRole.CustomerAdmin);
        db.Employees.Add(self);
        await db.SaveChangesAsync();

        var result = await OwnContact(db).Handle(
            new UpdateOwnContactRequestDto { ContactPhone = "+302108888888" },
            SessionFor(self, UserRole.CustomerAdmin), CancellationToken.None);

        Assert.Equal(self.Id, result.Id);
    }

    [Fact]
    public async Task The_target_is_the_session_and_a_colleagues_record_is_untouched()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var self = EmployeeEntity(customerId, "Maria", "Papadopoulou", "maria@acme.example");
        self.UserAccountId = identity.Seed(self.Id, UserRole.Employee);
        var colleague = EmployeeEntity(customerId, "Kostas", "Dimitriou", "kostas@acme.example");
        db.Employees.AddRange(self, colleague);
        await db.SaveChangesAsync();

        await OwnContact(db).Handle(
            new UpdateOwnContactRequestDto { ContactPhone = "+302107777777" },
            SessionFor(self, UserRole.Employee), CancellationToken.None);

        var storedColleague = await db.Employees.AsNoTracking().SingleAsync(
            employee => employee.Id == colleague.Id);
        Assert.Equal("+302100000000", storedColleague.ContactPhone);
    }

    [Fact]
    public async Task An_employee_with_no_record_gets_404()
    {
        await using var db = NewDb();
        var customerId = Guid.NewGuid();
        db.Employees.Add(EmployeeEntity(customerId));
        await db.SaveChangesAsync();
        // A session whose account id belongs to no Employee row at this Customer.
        var user = new CurrentUser(Guid.NewGuid().ToString(), UserRole.Employee, customerId);

        var exception = await Assert.ThrowsAsync<AppException>(() => OwnContact(db).Handle(
            new UpdateOwnContactRequestDto { ContactPhone = "+302106666666" },
            user, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
    }

    [Theory]
    [InlineData(UserRole.AccountantAdmin)]
    [InlineData(UserRole.AccountantUser)]
    public async Task An_accountant_is_refused_by_the_catalogue_not_by_a_missing_row(UserRole role)
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();

        var exception = await Assert.ThrowsAsync<AppException>(() => new UpdateOwnContactHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), audit)
            .Handle(new UpdateOwnContactRequestDto { ContactPhone = "+302105555555" },
                Accountant(role), CancellationToken.None));

        // 403 and not the 404 the handler would otherwise produce. An Accountant has no Employee record at
        // all, so the catalogue excludes them and the answer says so plainly.
        Assert.Equal(403, exception.StatusCode);
        Assert.Equal(AuditActions.PermissionDenied, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task Taking_a_colleagues_email_through_the_self_endpoint_is_409()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var self = EmployeeEntity(customerId, "Maria", "Papadopoulou", "maria@acme.example");
        self.UserAccountId = identity.Seed(self.Id, UserRole.Employee);
        db.Employees.AddRange(self, EmployeeEntity(customerId, "Kostas", "Dimitriou", "kostas@acme.example"));
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => OwnContact(db).Handle(
            new UpdateOwnContactRequestDto { WorkEmail = "kostas@acme.example" },
            SessionFor(self, UserRole.Employee), CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public void The_self_update_request_has_no_employee_id_and_no_other_writable_field()
    {
        // THE structural control of section 4.10. The handler resolves the record from the session, so this
        // endpoint is incapable of editing a colleague. An EmployeeId here, however carefully checked, turns
        // every future edit of the handler into an opportunity to check it wrongly.
        Assert.Null(typeof(UpdateOwnContactRequestDto).GetProperty("EmployeeId"));

        foreach (var forbidden in new[]
                 {
                     "GivenName", "FamilyName", "JobTitle", "Role", "Status", "CustomerId",
                     "EmploymentStartDate", "TaxIdentificationNumber", "SocialSecurityNumber"
                 })
            Assert.Null(typeof(UpdateOwnContactRequestDto).GetProperty(forbidden));

        // Exactly two writable fields.
        Assert.Equal(
            ["ContactPhone", "WorkEmail"],
            typeof(UpdateOwnContactRequestDto).GetProperties()
                .Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void The_self_read_shape_carries_neither_identifying_number()
    {
        foreach (var forbidden in new[]
                 {
                     "SocialSecurityNumber", "TaxIdentificationNumber", "Status", "UserAccountId",
                     "EmploymentEndDate"
                 })
            Assert.Null(typeof(EmployeeSelfDto).GetProperty(forbidden));
    }

    // --- helpers ---

    private static UpdateEmployeeHandler Update(EmployeesDbContext db)
    {
        var audit = new TestAuditApi();
        return new UpdateEmployeeHandler(
            db, Permissions(audit), new NoOpRequestTransaction(), new FakeIdentityApi(), audit);
    }

    private static UpdateOwnContactHandler OwnContact(EmployeesDbContext db)
    {
        var audit = new TestAuditApi();
        return new UpdateOwnContactHandler(db, Permissions(audit), new NoOpRequestTransaction(), audit);
    }

    /// <summary>
    /// A full replacement of every editable field, matching the entity the harness seeds except for the
    /// family name -- so an assertion on "what changed" is about the field the test changed.
    /// </summary>
    private static UpdateEmployeeRequestDto Request(Guid employeeId, string family = "Papadopoulos") => new()
    {
        EmployeeId = employeeId,
        GivenName = "Maria",
        FamilyName = family,
        JobTitle = "Bookkeeper",
        WorkEmail = "maria@acme.example",
        ContactPhone = "+302100000000",
        TaxIdentificationNumber = "TIN-123456",
        SocialSecurityNumber = "SSN-987654",
        EmploymentStartDate = new DateOnly(2026, 1, 5)
    };
}
