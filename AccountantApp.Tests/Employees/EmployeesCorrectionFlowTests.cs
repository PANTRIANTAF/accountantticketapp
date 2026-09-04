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
/// The two corrective operations -- plan sections 4.7a (reinstate) and 4.10a (change login email).
///
/// Both exist because a state this slice could reach had no in-app way out. A departure entered against the
/// wrong row froze the record; a person whose mailbox stopped working could not be given a new sign-in
/// address. They are grouped here rather than filed under the account flow because what they have in common
/// is that they UNDO or REPAIR, and the rules that matter are about not over-reaching while doing so.
/// </summary>
public sealed class EmployeesCorrectionFlowTests
{
    // --- 4.7a reinstate ---

    [Fact]
    public async Task Reinstating_clears_all_three_departure_fields_together()
    {
        await using var db = NewDb();
        var (audit, customers, identity, employee) = await Departed(db);

        var result = await Reinstate(db, customers, identity, audit).Handle(
            new EmployeeIdRequestDto { EmployeeId = employee.Id }, Accountant(), CancellationToken.None);

        Assert.True(result.Success);
        var stored = await db.Employees.AsNoTracking().SingleAsync();

        // All three, and the test asserts all three for a reason: ck_employees_departure requires an Active
        // row to have neither an employment_end_date NOR a departed_at, so clearing the status and one date
        // is a constraint violation that surfaces as a 500 against real Postgres. The in-memory provider
        // enforces no CHECK constraint, so this assertion is the only thing standing in for it here.
        Assert.Equal(EmployeeStatus.Active, stored.Status);
        Assert.Null(stored.EmploymentEndDate);
        Assert.Null(stored.DepartedAt);
    }

    [Fact]
    public async Task Reinstating_reactivates_the_account_in_the_same_operation()
    {
        await using var db = NewDb();
        var (audit, customers, identity, employee) = await Departed(db);
        var accountId = employee.UserAccountId!.Value;
        Assert.Equal("Suspended", identity.Account(accountId).Status);

        await Reinstate(db, customers, identity, audit).Handle(
            new EmployeeIdRequestDto { EmployeeId = employee.Id }, Accountant(), CancellationToken.None);

        // Not a separate call the caller has to remember. Reinstating and then leaving the login suspended
        // is the half-finished state /reactivate-account refuses to produce from the other direction.
        Assert.Equal(accountId, Assert.Single(identity.ReactivateCalls));
        Assert.Equal("Active", identity.Account(accountId).Status);
    }

    [Fact]
    public async Task Reinstating_a_never_accepted_invitee_returns_them_to_invited_not_active()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var customers = new FakeCustomerApi();
        var customerId = customers.AddActive();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(customerId, status: EmployeeStatus.Departed);

        // Invited, so the account has no password hash -- somebody who was departed before they ever signed
        // in. Seeded Invited and then suspended by the departure, exactly as the depart handler leaves it.
        employee.UserAccountId = identity.Seed(employee.Id, UserRole.Employee, "Invited");
        await identity.SuspendAccountAsync(employee.UserAccountId.Value);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        await Reinstate(db, customers, identity, audit).Handle(
            new EmployeeIdRequestDto { EmployeeId = employee.Id }, Accountant(), CancellationToken.None);

        // Invited, NOT Active -- Identity plan section 9.1 rule 14. An Active account with no password hash
        // passes every status check and fails every login, and it cannot be re-invited either, so the person
        // is locked out with nothing in the data looking wrong. This handler does not special-case it; it
        // gets the behaviour for free by calling ReactivateAccountAsync instead of flipping a status itself,
        // which is the thing to preserve if this ever changes.
        Assert.Equal("Invited", identity.Account(employee.UserAccountId.Value).Status);
    }

    [Fact]
    public async Task Reinstating_an_accountless_employee_touches_identity_not_at_all()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var customers = new FakeCustomerApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(customers.AddActive(), status: EmployeeStatus.Departed);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await Reinstate(db, customers, identity, audit).Handle(
            new EmployeeIdRequestDto { EmployeeId = employee.Id }, Accountant(), CancellationToken.None);

        // Registration creates no account, so a departed Employee may well have none. Reactivating a null
        // account id is how this becomes a 500 on the most ordinary case in the slice.
        Assert.True(result.Success);
        Assert.Empty(identity.ReactivateCalls);
        Assert.Equal(EmployeeStatus.Active, (await db.Employees.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Reinstating_an_employee_who_has_not_departed_is_422()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var customers = new FakeCustomerApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(customers.AddActive(), status: EmployeeStatus.Active);
        employee.UserAccountId = identity.Seed(employee.Id);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            Reinstate(db, customers, identity, audit).Handle(
                new EmployeeIdRequestDto { EmployeeId = employee.Id },
                Accountant(), CancellationToken.None));

        // 422 and not 409: the caller has the right to do this, the row is simply not in a reversible state.
        // An "Active employee reinstated" is a caller who picked the wrong row, and reactivating their
        // account as a side effect of that mistake is what this refusal prevents.
        Assert.Equal(422, exception.StatusCode);
        Assert.Equal("This employee has not departed.", exception.Message);
        Assert.Empty(identity.ReactivateCalls);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Reinstating_into_a_suspended_customer_is_422_and_changes_nothing()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var customers = new FakeCustomerApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(customers.AddSuspended(), status: EmployeeStatus.Departed);
        employee.UserAccountId = identity.Seed(employee.Id, UserRole.Employee, "Suspended");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            Reinstate(db, customers, identity, audit).Handle(
                new EmployeeIdRequestDto { EmployeeId = employee.Id },
                Accountant(), CancellationToken.None));

        // The same rule registration enforces, and for the same reason: a suspended Customer already blocks
        // every login there, so this would produce an Active Employee who still cannot get in -- and the
        // Office would have to remember that the reason is two levels up.
        Assert.Equal(422, exception.StatusCode);
        Assert.Equal("This customer is not active.", exception.Message);
        Assert.Empty(identity.ReactivateCalls);
        Assert.Equal(EmployeeStatus.Departed, (await db.Employees.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Reinstating_audits_reinstated_and_not_edited()
    {
        await using var db = NewDb();
        var (audit, customers, identity, employee) = await Departed(db);

        await Reinstate(db, customers, identity, audit).Handle(
            new EmployeeIdRequestDto { EmployeeId = employee.Id }, Accountant(), CancellationToken.None);

        // Its own action, not EmployeeEdited. Somebody auditing "was this departure real?" searches for the
        // reversal by name, and a reinstatement filed as a generic edit is one they will never find. The
        // before/after snapshots are what makes the entry answer the question rather than just record it.
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.EmployeeReinstated, entry.Action);
        Assert.Equal(AuditTargets.Employee, entry.TargetKind);
        Assert.Equal(employee.Id.ToString(), entry.TargetId);
        Assert.Equal(employee.CustomerId, entry.CustomerId);
        Assert.NotNull(entry.Before);
        Assert.NotNull(entry.After);
    }

    [Fact]
    public void Neither_correction_can_notify_anybody_because_neither_takes_the_contract()
    {
        // There is no EmployeeReinstated and no EmployeeLoginEmailChanged notification kind, deliberately: a
        // correction to a record is not news about the staff list, and telling a Customer's Admins that
        // somebody "came back" when what happened is that a mistyped departure was undone describes an event
        // that never occurred. Announcing a login-email change to the Admins is worse -- it broadcasts the
        // new sign-in address of a colleague's account to everybody who can read notifications.
        //
        // Asserted against the CONSTRUCTOR rather than against a recording double, because that is where the
        // rule actually lives: a handler that cannot reach INotificationApi cannot regress into using it, and
        // adding the parameter is the change this test is here to make somebody justify.
        foreach (var type in new[]
                 {
                     typeof(ReinstateEmployeeHandler), typeof(ChangeEmployeeLoginEmailHandler)
                 })
            Assert.DoesNotContain(
                type.GetConstructors()[0].GetParameters(),
                parameter => parameter.ParameterType.Name.Contains(
                    "Notification", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_customer_admin_may_reinstate_their_own_customers_employee()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var customers = new FakeCustomerApi();
        var customerId = customers.AddActive();
        var identity = new FakeIdentityApi();

        var admin = EmployeeEntity(customerId, "Admina", "Boss", "admina@acme.example");
        admin.UserAccountId = identity.Seed(
            admin.Id, UserRole.CustomerAdmin, "Active", "admina@acme.example");
        var departed = EmployeeEntity(
            customerId, "Gone", "Person", "gone@acme.example", status: EmployeeStatus.Departed);
        db.Employees.AddRange(admin, departed);
        await db.SaveChangesAsync();

        // Granted to exactly whoever may enter a departure. Narrowing it to Accountants would mean a
        // Customer Admin can create a state they cannot undo, which is how a mistake becomes a phone call.
        var result = await Reinstate(db, customers, identity, audit).Handle(
            new EmployeeIdRequestDto { EmployeeId = departed.Id },
            SessionFor(admin, UserRole.CustomerAdmin), CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task A_customer_admin_may_not_reinstate_another_customers_employee()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var customers = new FakeCustomerApi();
        var identity = new FakeIdentityApi();

        var admin = EmployeeEntity(customers.AddActive(), "Admina", "Boss", "admina@acme.example");
        admin.UserAccountId = identity.Seed(
            admin.Id, UserRole.CustomerAdmin, "Active", "admina@acme.example");
        var theirs = EmployeeEntity(
            customers.AddActive(), "Gone", "Person", "gone@other.example",
            status: EmployeeStatus.Departed);
        db.Employees.AddRange(admin, theirs);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            Reinstate(db, customers, identity, audit).Handle(
                new EmployeeIdRequestDto { EmployeeId = theirs.Id },
                SessionFor(admin, UserRole.CustomerAdmin), CancellationToken.None));

        // 404, not 403: the scope filter runs before the status check, so a caller learns nothing about
        // whether the id exists at all.
        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task A_plain_employee_may_not_reinstate_anybody()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var customers = new FakeCustomerApi();
        var customerId = customers.AddActive();
        var identity = new FakeIdentityApi();

        var caller = EmployeeEntity(customerId);
        caller.UserAccountId = identity.Seed(caller.Id);
        var departed = EmployeeEntity(
            customerId, "Gone", "Person", "gone@acme.example", status: EmployeeStatus.Departed);
        db.Employees.AddRange(caller, departed);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            Reinstate(db, customers, identity, audit).Handle(
                new EmployeeIdRequestDto { EmployeeId = departed.Id },
                SessionFor(caller, UserRole.Employee), CancellationToken.None));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal(EmployeeStatus.Departed, (await db.Employees.AsNoTracking()
            .SingleAsync(employee => employee.Id == departed.Id)).Status);
    }

    // --- 4.10a change login email ---

    [Fact]
    public async Task Changing_a_login_email_moves_the_account_and_leaves_the_employee_row_alone()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        employee.UserAccountId = identity.Seed(
            employee.Id, UserRole.Employee, "Active", "maria.old@acme.example");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await ChangeLoginEmail(db, identity, audit).Handle(
            new ChangeEmployeeLoginEmailRequestDto
            {
                EmployeeId = employee.Id,
                LoginEmail = "maria.new@acme.example"
            }, Accountant(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("maria.new@acme.example", identity.Account(employee.UserAccountId!.Value).LoginEmail);

        // The Employee row is untouched. WorkEmail is CONTACT information and this call named only the
        // sign-in address; rewriting a field the caller did not mention is the kind of helpfulness that
        // loses data -- and it would also silently move NormalizedWorkEmail, which the per-Customer unique
        // index is built on.
        var stored = await db.Employees.AsNoTracking().SingleAsync();
        Assert.Equal("maria@acme.example", stored.WorkEmail);
        Assert.Equal("MARIA@ACME.EXAMPLE", stored.NormalizedWorkEmail);
        Assert.Equal(employee.UserAccountId, stored.UserAccountId);
    }

    [Fact]
    public async Task Changing_a_login_email_leaves_the_account_status_alone()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());

        // An invitee who has not accepted yet. Their account keeps its status and its (absent) password:
        // moving the address must not quietly activate them, and must not quietly re-issue an invitation
        // either -- the token from the first one still works and still points at this account.
        employee.UserAccountId = identity.Seed(
            employee.Id, UserRole.Employee, "Invited", "typo@acme.example");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        await ChangeLoginEmail(db, identity, audit).Handle(
            new ChangeEmployeeLoginEmailRequestDto
            {
                EmployeeId = employee.Id,
                LoginEmail = "correct@acme.example"
            }, Accountant(), CancellationToken.None);

        var account = identity.Account(employee.UserAccountId!.Value);
        Assert.Equal("Invited", account.Status);
        Assert.Equal("correct@acme.example", account.LoginEmail);
        Assert.Empty(identity.ReactivateCalls);
        Assert.Empty(identity.SuspendCalls);
    }

    [Fact]
    public async Task Changing_the_login_email_of_an_accountless_employee_is_422()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            ChangeLoginEmail(db, identity, audit).Handle(
                new ChangeEmployeeLoginEmailRequestDto
                {
                    EmployeeId = employee.Id,
                    LoginEmail = "somebody@acme.example"
                }, Accountant(), CancellationToken.None));

        // 422, not 404: the Employee exists and the caller may see them. There is simply no account whose
        // address could be changed, and the message names the fix rather than making the caller infer it
        // from a status code.
        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(
            "This employee has no account, so there is no sign-in address to change. Invite them first.",
            exception.Message);
        Assert.Empty(identity.LoginEmailCalls);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Changing_a_departed_employees_login_email_is_422()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid(), status: EmployeeStatus.Departed);
        employee.UserAccountId = identity.Seed(
            employee.Id, UserRole.Employee, "Suspended", "gone@acme.example");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            ChangeLoginEmail(db, identity, audit).Handle(
                new ChangeEmployeeLoginEmailRequestDto
                {
                    EmployeeId = employee.Id,
                    LoginEmail = "gone.new@acme.example"
                }, Accountant(), CancellationToken.None));

        // Matches /reactivate-account: their account is suspended, so a new sign-in address changes nothing
        // anybody could use. It also keeps the address of a departed person's account free for reuse rather
        // than moving it around on their behalf.
        Assert.Equal(422, exception.StatusCode);
        Assert.Equal("This employee has departed.", exception.Message);
        Assert.Equal("gone@acme.example", identity.Account(employee.UserAccountId!.Value).LoginEmail);
    }

    [Fact]
    public async Task An_address_that_is_already_a_login_anywhere_is_409()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();

        // The taken address belongs to a DIFFERENT Customer's Employee. normalized_login_email is unique
        // system-wide, so this is a 409 even though nothing about the two Employees overlaps -- and the
        // message must not confirm whose it is.
        var theirs = EmployeeEntity(Guid.NewGuid(), "Taken", "Person", "taken@other.example");
        theirs.UserAccountId = identity.Seed(
            theirs.Id, UserRole.Employee, "Active", "taken@other.example");

        var employee = EmployeeEntity(Guid.NewGuid());
        employee.UserAccountId = identity.Seed(
            employee.Id, UserRole.Employee, "Active", "maria.old@acme.example");
        db.Employees.AddRange(theirs, employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            ChangeLoginEmail(db, identity, audit).Handle(
                new ChangeEmployeeLoginEmailRequestDto
                {
                    EmployeeId = employee.Id,
                    LoginEmail = "TAKEN@other.example"
                }, Accountant(), CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
        Assert.DoesNotContain("Taken", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Person", exception.Message, StringComparison.Ordinal);

        // Nothing moved, and no audit entry claims it did -- the audit write is after the Identity call for
        // exactly this reason.
        Assert.Equal("maria.old@acme.example", identity.Account(employee.UserAccountId!.Value).LoginEmail);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Changing_a_login_email_audits_against_the_employee_with_both_addresses()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        employee.UserAccountId = identity.Seed(
            employee.Id, UserRole.Employee, "Active", "maria.old@acme.example");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        await ChangeLoginEmail(db, identity, audit).Handle(
            new ChangeEmployeeLoginEmailRequestDto
            {
                EmployeeId = employee.Id,
                LoginEmail = "maria.new@acme.example"
            }, Accountant(), CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.LoginEmailChanged, entry.Action);

        // Targets the EMPLOYEE, not the account. Identity writes its own entry against the UserAccount;
        // somebody investigating "what happened to this person" searches by Employee id, and an entry only
        // findable by account id is an entry they will not find.
        Assert.Equal(AuditTargets.Employee, entry.TargetKind);
        Assert.Equal(employee.Id.ToString(), entry.TargetId);
        Assert.Equal(employee.CustomerId, entry.CustomerId);

        // Both addresses in full. A login email is not a personal identifying number, and which address it
        // was and which it became is the entire point of the entry -- a redacted one answers nothing.
        var payload = System.Text.Json.JsonSerializer.Serialize(new { entry.Before, entry.After });
        Assert.Contains("maria.old@acme.example", payload, StringComparison.Ordinal);
        Assert.Contains("maria.new@acme.example", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_customer_admin_may_not_change_anybodys_login_email()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();

        var admin = EmployeeEntity(customerId, "Admina", "Boss", "admina@acme.example");
        admin.UserAccountId = identity.Seed(
            admin.Id, UserRole.CustomerAdmin, "Active", "admina@acme.example");
        var colleague = EmployeeEntity(customerId, "Maria", "P", "maria@acme.example");
        colleague.UserAccountId = identity.Seed(
            colleague.Id, UserRole.Employee, "Active", "maria@acme.example");
        db.Employees.AddRange(admin, colleague);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            ChangeLoginEmail(db, identity, audit).Handle(
                new ChangeEmployeeLoginEmailRequestDto
                {
                    EmployeeId = colleague.Id,
                    LoginEmail = "attacker@elsewhere.example"
                }, SessionFor(admin, UserRole.CustomerAdmin), CancellationToken.None));

        // 403 from the catalogue, and the ONE row in this slice where a Customer Admin is refused something
        // their own Employee's record. Whoever can change a sign-in address can move an account to a mailbox
        // they control, so it stays outside the Customer entirely: doing it to a colleague is account
        // takeover, and doing it to themselves is the same thing with fewer steps.
        Assert.Equal(403, exception.StatusCode);
        Assert.Empty(identity.LoginEmailCalls);
        Assert.Equal("maria@acme.example", identity.Account(colleague.UserAccountId!.Value).LoginEmail);
    }

    [Fact]
    public async Task A_plain_employee_may_not_change_their_own_login_email()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        employee.UserAccountId = identity.Seed(
            employee.Id, UserRole.Employee, "Active", "maria@acme.example");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            ChangeLoginEmail(db, identity, audit).Handle(
                new ChangeEmployeeLoginEmailRequestDto
                {
                    EmployeeId = employee.Id,
                    LoginEmail = "personal@gmail.example"
                }, SessionFor(employee, UserRole.Employee), CancellationToken.None));

        // Self-service is the case this endpoint is NOT. Anybody who briefly holds a session -- an unlocked
        // laptop is enough -- could otherwise move the account to an address they keep after the session
        // ends. /update-own-contact is the endpoint for acting on yourself, and the work email is what it
        // reaches.
        Assert.Equal(403, exception.StatusCode);
        Assert.Empty(identity.LoginEmailCalls);
    }

    // --- helpers ---

    /// <summary>
    /// A departed Employee with a suspended account at an Active Customer, saved -- the state both
    /// reinstatement tests start from, produced the way departure produces it rather than asserted into
    /// place, so a change to what departure leaves behind shows up here.
    /// </summary>
    private static async Task<(TestAuditApi Audit, FakeCustomerApi Customers, FakeIdentityApi Identity,
            Employee Employee)>
        Departed(EmployeesDbContext db)
    {
        var audit = new TestAuditApi();
        var customers = new FakeCustomerApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(customers.AddActive(), status: EmployeeStatus.Departed);
        employee.UserAccountId = identity.Seed(employee.Id, UserRole.Employee, "Active");
        await identity.SuspendAccountAsync(employee.UserAccountId.Value);
        identity.SuspendCalls.Clear();
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (audit, customers, identity, employee);
    }

    private static ReinstateEmployeeHandler Reinstate(
        EmployeesDbContext db, FakeCustomerApi customers, FakeIdentityApi identity, TestAuditApi audit) =>
        new(db, Permissions(audit), new NoOpRequestTransaction(), customers, identity, audit);

    private static ChangeEmployeeLoginEmailHandler ChangeLoginEmail(
        EmployeesDbContext db, FakeIdentityApi identity, TestAuditApi audit) =>
        new(db, Permissions(audit), new NoOpRequestTransaction(), identity, audit);
}
