using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Application.Handlers;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Tests.Identity;
using AccountantApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using static AccountantApp.Tests.Employees.EmployeesTestHarness;

namespace AccountantApp.Tests.Employees;

/// <summary>
/// Registration and the composite onboarding operation -- plan sections 4.1 and 4.2.
///
/// The structural rule both of these exist to protect: registering an Employee and giving them a login
/// are two separate operations. Registration creates no account and sends no email, which is what makes
/// on-behalf-of ticketing possible for somebody who has never logged in.
/// </summary>
public sealed class EmployeesRegistrationFlowTests
{
    // --- 4.2 register ---

    [Fact]
    public async Task Register_creates_an_accountless_employee_and_touches_identity_not_at_all()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var customers = new FakeCustomerApi();
        var customerId = customers.AddActive();

        var result = await new RegisterEmployeeHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), customers, identity,
                new RecordingNotificationApi(), audit)
            .Handle(Request(customerId), Accountant(), CancellationToken.None);

        var stored = await db.Employees.SingleAsync();
        Assert.Null(stored.UserAccountId);
        Assert.Equal(EmployeeStatus.Active, stored.Status);
        Assert.Null(stored.EmploymentEndDate);
        Assert.Null(stored.DepartedAt);

        // No account row and no invitation. A registration that quietly invited would send an email to
        // somebody the Customer Admin had not decided to give access to.
        Assert.Null(await identity.FindByEmployeeAsync(stored.Id));

        // Role and AccountStatus null, NOT "Employee": showing a role they do not hold is how a Customer
        // Admin comes to believe somebody can log in.
        Assert.False(result.HasAccount);
        Assert.Null(result.Role);
        Assert.Null(result.AccountStatus);
        Assert.Equal(AuditActions.EmployeeRegistered, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task Register_normalizes_the_work_email_for_the_unique_index()
    {
        await using var db = NewDb();
        var customers = new FakeCustomerApi();
        var customerId = customers.AddActive();
        var request = Request(customerId);
        request.WorkEmail = "  Nikos.Petrou@Acme.Example  ";

        await Register(db, customers).Handle(request, Accountant(), CancellationToken.None);

        var stored = await db.Employees.SingleAsync();
        // The display form keeps its casing; the normalized form is what the unique index compares, and it
        // has to be written at insert time because a b-tree cannot normalize during a lookup.
        Assert.Equal("Nikos.Petrou@Acme.Example", stored.WorkEmail);
        Assert.Equal("NIKOS.PETROU@ACME.EXAMPLE", stored.NormalizedWorkEmail);
    }

    [Fact]
    public async Task Register_at_a_suspended_customer_is_rejected()
    {
        await using var db = NewDb();
        var customers = new FakeCustomerApi();
        var suspended = customers.AddSuspended();

        var exception = await Assert.ThrowsAsync<AppException>(() => Register(db, customers)
            .Handle(Request(suspended), Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Empty(db.Employees);
    }

    [Fact]
    public async Task Register_at_an_unknown_customer_is_422_not_500()
    {
        await using var db = NewDb();
        var customers = new FakeCustomerApi();

        var exception = await Assert.ThrowsAsync<AppException>(() => Register(db, customers)
            .Handle(Request(Guid.NewGuid()), Accountant(), CancellationToken.None));

        // The failure mode this rules out is `FindAsync(...)?.IsActive ?? true`, which turns "no such
        // Customer" into "go ahead" and produces an Employee attached to a Customer that does not exist.
        Assert.Equal(422, exception.StatusCode);
        Assert.Empty(db.Employees);
    }

    [Fact]
    public async Task Customer_admin_registering_at_another_customer_is_403()
    {
        await using var db = NewDb();
        var customers = new FakeCustomerApi();
        var own = customers.AddActive();
        var other = customers.AddActive();
        var user = new CurrentUser(Guid.NewGuid().ToString(), UserRole.CustomerAdmin, own);

        var exception = await Assert.ThrowsAsync<AppException>(() => Register(db, customers)
            .Handle(Request(other), user, CancellationToken.None));

        // 403 and not 404: the caller supplied a Customer id, and no row is being hidden from them.
        Assert.Equal(403, exception.StatusCode);
        Assert.Empty(db.Employees);
    }

    [Fact]
    public async Task Customer_admin_at_a_suspended_customer_is_not_exempt()
    {
        await using var db = NewDb();
        var customers = new FakeCustomerApi();
        var own = customers.AddSuspended();
        var user = new CurrentUser(Guid.NewGuid().ToString(), UserRole.CustomerAdmin, own);

        var exception = await Assert.ThrowsAsync<AppException>(() => Register(db, customers)
            .Handle(Request(own), user, CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task Duplicate_work_email_at_the_same_customer_is_409_not_500()
    {
        await using var db = NewDb();
        var customers = new FakeCustomerApi();
        var customerId = customers.AddActive();
        var handler = Register(db, customers);

        await handler.Handle(Request(customerId), Accountant(), CancellationToken.None);

        var second = Request(customerId);
        // Different casing and surrounding space: the same address as far as the unique index is concerned.
        second.WorkEmail = " NIKOS@acme.example ";
        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            second, Accountant(), CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal(1, await db.Employees.CountAsync());
    }

    [Fact]
    public async Task The_same_work_email_at_two_different_customers_is_allowed()
    {
        await using var db = NewDb();
        var customers = new FakeCustomerApi();
        var first = customers.AddActive();
        var second = customers.AddActive();
        var handler = Register(db, customers);

        await handler.Handle(Request(first), Accountant(), CancellationToken.None);
        await handler.Handle(Request(second), Accountant(), CancellationToken.None);

        // Work email is unique PER CUSTOMER, not globally. A global rule would make registering an
        // Employee fail because an unrelated Customer has that address on file, and the error could not
        // explain why without leaking another Customer's data.
        Assert.Equal(2, await db.Employees.CountAsync());
    }

    [Fact]
    public async Task Register_audit_payload_carries_neither_personal_identifying_number()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var customers = new FakeCustomerApi();
        var customerId = customers.AddActive();
        var request = Request(customerId);
        request.TaxIdentificationNumber = "TIN-SECRET-1";
        request.SocialSecurityNumber = "SSN-SECRET-2";

        await new RegisterEmployeeHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), customers,
                new FakeIdentityApi(), new RecordingNotificationApi(), audit)
            .Handle(request, Accountant(), CancellationToken.None);

        // Redaction.ToJson redacts by substring on password/hash/salt/token/secret/apikey/sessionid/
        // cookie. Neither of these field names matches, so a value put in the payload would be retained
        // forever in a table nobody purges -- the payload carries booleans instead.
        var payload = System.Text.Json.JsonSerializer.Serialize(
            Assert.Single(audit.Entries).After);
        Assert.DoesNotContain("TIN-SECRET-1", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("SSN-SECRET-2", payload, StringComparison.Ordinal);
        Assert.Contains("HasTaxIdentificationNumber", payload, StringComparison.Ordinal);
        Assert.Contains("HasSocialSecurityNumber", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registering_notifies_the_customers_own_active_admins_once()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var notifications = new RecordingNotificationApi();
        var customers = new FakeCustomerApi();
        var customerId = customers.AddActive();

        // Two Admins, a plain Employee with an account, and a suspended Admin. Only the first two rows are
        // recipients, and getting that set wrong is the failure this whole helper exists to prevent once
        // rather than twice.
        var first = Admin(db, identity, customerId, "Admina");
        var second = Admin(db, identity, customerId, "Adminb");
        var plain = EmployeeEntity(customerId, "Plain", "Person", "plain@acme.example");
        plain.UserAccountId = identity.Seed(plain.Id, UserRole.Employee, "Active", "plain@acme.example");
        var suspended = EmployeeEntity(customerId, "Susie", "Boss", "susie@acme.example");
        suspended.UserAccountId = identity.Seed(
            suspended.Id, UserRole.CustomerAdmin, "Suspended", "susie@acme.example");
        db.Employees.AddRange(plain, suspended);
        await db.SaveChangesAsync();

        await new RegisterEmployeeHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), customers, identity, notifications,
                audit)
            .Handle(Request(customerId), Accountant(), CancellationToken.None);

        // One NotifyManyAsync for both, not a loop of NotifyAsync.
        Assert.Equal(1, notifications.NotifyManyCallCount);

        var sent = notifications.OfKind(NotificationEvents.EmployeeRegistered).ToList();
        // Both sides ordered: account ids are random Guids, so an unordered comparison passes or fails on
        // which Guid happened to sort first.
        Assert.Equal(
            new[] { first.UserAccountId!.Value.ToString(), second.UserAccountId!.Value.ToString() }.Order(),
            sent.Select(request => request.RecipientUserId).Order());

        // The person just registered is not a recipient and could not be one -- registration creates no
        // account, so there is nowhere for it to go. Nor are Accountants: they already see every Customer's
        // Employees, and notifying the whole Office on every registration is how a notification list becomes
        // something people stop reading.
        Assert.All(sent, request =>
        {
            Assert.Equal("A new employee was registered", request.Title);
            Assert.Contains("Nikos Petrou", request.Body, StringComparison.Ordinal);

            // The body says so explicitly, because the alternative is a Customer Admin who believes the new
            // colleague can sign in and waits for them to.
            Assert.Contains("cannot sign in until they are invited", request.Body, StringComparison.Ordinal);

            // In-app only: no EmailBody, and the kind is deliberately absent from Emailed. An Admin who
            // registers six people in an afternoon does not want six emails about their own afternoon.
            Assert.Null(request.EmailBody);
        });

        Assert.Contains(NotificationEvents.EmployeeRegistered, NotificationEvents.All);
        Assert.DoesNotContain(NotificationEvents.EmployeeRegistered, NotificationEvents.Emailed);
    }

    [Fact]
    public async Task Registering_at_a_customer_with_no_admin_account_notifies_nobody()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var notifications = new RecordingNotificationApi();
        var customers = new FakeCustomerApi();
        var customerId = customers.AddActive();

        // The very first Employee of a brand-new Customer, which is the most common registration there is.
        // No recipients means no call at all rather than a call with an empty list.
        await new RegisterEmployeeHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), customers, new FakeIdentityApi(),
                notifications, audit)
            .Handle(Request(customerId), Accountant(), CancellationToken.None);

        Assert.Equal(0, notifications.NotifyManyCallCount);
        Assert.Empty(notifications.Requests);
    }

    [Fact]
    public async Task A_failed_registration_notifies_nobody()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var notifications = new RecordingNotificationApi();
        var customers = new FakeCustomerApi();
        var customerId = customers.AddActive();
        Admin(db, identity, customerId, "Admina");
        await db.SaveChangesAsync();

        var request = Request(customerId);
        request.GivenName = "   ";

        await Assert.ThrowsAsync<AppException>(() => new RegisterEmployeeHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), customers, identity, notifications,
                audit)
            .Handle(request, Accountant(), CancellationToken.None));

        // The notification is raised inside the transaction, after the row is written. A registration that
        // rolls back must not leave behind a notification claiming a colleague was added -- and with a
        // NoOpRequestTransaction there is no rollback here at all, so the ordering is what protects it.
        Assert.Empty(notifications.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_given_name_is_422_and_writes_nothing(string givenName)
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var customers = new FakeCustomerApi();
        var request = Request(customers.AddActive());
        request.GivenName = givenName;

        var exception = await Assert.ThrowsAsync<AppException>(() => new RegisterEmployeeHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), customers,
                new FakeIdentityApi(), new RecordingNotificationApi(), audit)
            .Handle(request, Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Empty(db.Employees);
        // Validation is not a denial. Only the permission checker writes an entry for a refusal.
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_missing_employment_start_date_is_422()
    {
        await using var db = NewDb();
        var customers = new FakeCustomerApi();
        var request = Request(customers.AddActive());
        request.EmploymentStartDate = default;

        var exception = await Assert.ThrowsAsync<AppException>(() => Register(db, customers)
            .Handle(request, Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    // --- 4.1 onboarding ---

    [Fact]
    public async Task Accountant_admin_onboards_a_customer_its_first_employee_and_a_customer_admin_account()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var customers = new FakeCustomerApi();

        var result = await new OnboardCustomerHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), customers, identity, audit)
            .Handle(OnboardRequest(), Accountant(UserRole.AccountantAdmin), CancellationToken.None);

        Assert.Equal(customers.NextCreatedId, result.CustomerId);
        Assert.Single(customers.Created);

        var employee = await db.Employees.SingleAsync();
        Assert.Equal(result.EmployeeId, employee.Id);
        // No CustomerId came in on the request, so the only possible correct value is the one step 1
        // generated. A CustomerId field on the request DTO could only ever have been wrong.
        Assert.Equal(result.CustomerId, employee.CustomerId);
        Assert.Equal(result.UserAccountId, employee.UserAccountId);

        // CustomerAdmin, not Employee. A first person created as a plain Employee gives a Customer that
        // violates its own at-least-one-active-Customer-Admin invariant from the moment it exists, and the
        // set-role guard would then block every attempt to climb out.
        var account = identity.Account(result.UserAccountId);
        Assert.Equal(UserRole.CustomerAdmin, account.Role);
        Assert.Equal("Invited", account.Status);

        // Two entries from this slice. Customers wrote CustomerCreated inside CreateAsync and Identity
        // wrote AccountInvited inside InviteEmployeeAccountAsync -- three things happened in three slices.
        Assert.Single(audit.WithAction(AuditActions.EmployeeRegistered));
        Assert.Single(audit.WithAction(AuditActions.EmployeeInvited));
    }

    [Theory]
    [InlineData(UserRole.AccountantUser)]
    [InlineData(UserRole.CustomerAdmin)]
    [InlineData(UserRole.Employee)]
    public async Task Only_accountant_admin_may_onboard(UserRole role)
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var customers = new FakeCustomerApi();
        var user = new CurrentUser(
            Guid.NewGuid().ToString(), role,
            role is UserRole.CustomerAdmin or UserRole.Employee ? Guid.NewGuid() : null);

        var exception = await Assert.ThrowsAsync<AppException>(() => new OnboardCustomerHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), customers,
                new FakeIdentityApi(), audit)
            .Handle(OnboardRequest(), user, CancellationToken.None));

        Assert.Equal(403, exception.StatusCode);
        // The refusal happens before step 1, so no Customer is created either.
        Assert.Empty(customers.Created);
        Assert.Empty(db.Employees);
        Assert.Equal(AuditActions.PermissionDenied, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task Onboarding_without_a_first_admin_work_email_is_422_before_the_customer_is_created()
    {
        await using var db = NewDb();
        var customers = new FakeCustomerApi();
        var request = OnboardRequest();
        request.FirstAdmin.WorkEmail = "   ";

        var exception = await Assert.ThrowsAsync<AppException>(() => Onboard(db, customers)
            .Handle(request, Accountant(UserRole.AccountantAdmin), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        // Validated up front. A rollback would also have covered this, but "safe by construction" stops
        // being true the first time somebody moves a call outside the transaction.
        Assert.Empty(customers.Created);
    }

    [Fact]
    public async Task Onboarding_where_the_invitation_step_fails_commits_nothing()
    {
        await using var db = NewDb();
        var customers = new FakeCustomerApi();
        var identity = new FakeIdentityApi
        {
            // The address is already a login somewhere, possibly at another Customer.
            InviteFailure = new AppException("An account with that email address already exists.", 409)
        };
        var transaction = new CountingRequestTransaction();
        var audit = new TestAuditApi();

        var exception = await Assert.ThrowsAsync<AppException>(() => new OnboardCustomerHandler(
                db, Permissions(audit), transaction, customers, identity, audit)
            .Handle(OnboardRequest(), Accountant(UserRole.AccountantAdmin), CancellationToken.None));

        // A 409, never a 500: the value came from the client. And the message must not say where the
        // address is already in use, because that would be another Customer's data.
        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("That email address is already in use.", exception.Message);

        // The transaction scope was disposed without a commit. This is a PROXY for the real assertion --
        // the in-memory provider has no transaction, so the Customer and Employee rows written before the
        // failure are still there in this test's store. The real rollback is asserted in
        // EmployeesSchemaTests, which queries the database in a new context after the failure.
        Assert.True(transaction.RolledBack);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void The_onboarding_response_contains_no_token()
    {
        // The invitation link goes to the invitee's mailbox and nowhere else. A token in this response
        // would let whoever onboarded the Customer take over its first administrator's account.
        var suspicious = typeof(OnboardCustomerResponseDto).GetProperties()
            .Where(property => property.Name.Contains("token", StringComparison.OrdinalIgnoreCase)
                            || property.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
                            || property.Name.Contains("link", StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(suspicious);
    }

    [Fact]
    public void The_onboarding_request_has_no_customer_id_on_its_first_admin_block()
    {
        // The Customer does not exist when the request is written, so the only correct value is the one
        // the handler is about to generate. A property here could only ever be wrong.
        Assert.Null(typeof(OnboardFirstAdminDto).GetProperty("CustomerId"));
    }

    // --- helpers ---

    /// <summary>
    /// An Employee with an Active CustomerAdmin account, added to the context but not saved -- the caller
    /// saves once. Returned so a test can read its account id back as an expected recipient.
    /// </summary>
    private static Employee Admin(
        Api.Slices.Employees.Infrastructure.EmployeesDbContext db,
        FakeIdentityApi identity,
        Guid customerId,
        string given)
    {
        var email = $"{given.ToLowerInvariant()}@acme.example";
        var employee = EmployeeEntity(customerId, given, "Boss", email);
        employee.UserAccountId = identity.Seed(employee.Id, UserRole.CustomerAdmin, "Active", email);
        db.Employees.Add(employee);
        return employee;
    }

    private static RegisterEmployeeHandler Register(
        Api.Slices.Employees.Infrastructure.EmployeesDbContext db, FakeCustomerApi customers)
    {
        var audit = new TestAuditApi();
        return new RegisterEmployeeHandler(
            db, Permissions(audit), new NoOpRequestTransaction(), customers,
            new FakeIdentityApi(), new RecordingNotificationApi(), audit);
    }

    private static OnboardCustomerHandler Onboard(
        Api.Slices.Employees.Infrastructure.EmployeesDbContext db, FakeCustomerApi customers)
    {
        var audit = new TestAuditApi();
        return new OnboardCustomerHandler(
            db, Permissions(audit), new NoOpRequestTransaction(), customers, new FakeIdentityApi(), audit);
    }

    private static RegisterEmployeeRequestDto Request(Guid customerId) => new()
    {
        CustomerId = customerId,
        GivenName = "Nikos",
        FamilyName = "Petrou",
        JobTitle = "Bookkeeper",
        WorkEmail = "nikos@acme.example",
        ContactPhone = "+302100000000",
        EmploymentStartDate = new DateOnly(2026, 2, 1)
    };

    private static OnboardCustomerRequestDto OnboardRequest() => new()
    {
        Customer = new CreateCustomer
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
        },
        FirstAdmin = new OnboardFirstAdminDto
        {
            GivenName = "Ada",
            FamilyName = "Admin",
            JobTitle = "Owner",
            WorkEmail = "ada@acme.example",
            EmploymentStartDate = new DateOnly(2026, 1, 15)
        }
    };
}
