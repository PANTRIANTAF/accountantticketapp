using System.Text.Json;
using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Application.Handlers;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Tests.Identity;
using AccountantApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using static AccountantApp.Tests.Employees.EmployeesTestHarness;

namespace AccountantApp.Tests.Employees;

/// <summary>
/// The operations that reach into Identity -- invite, set-role, depart, suspend-account,
/// reactivate-account, reinstate, change-login-email (plan sections 4.5 to 4.10a) -- and the
/// at-least-one-active-Customer-Admin invariant of section 5 that three of them share.
/// </summary>
public sealed class EmployeesAccountFlowTests
{
    // --- 4.5 invite ---

    [Fact]
    public async Task Inviting_an_accountless_employee_writes_the_account_id_onto_the_row()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await new InviteEmployeeHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), identity, audit)
            .Handle(new InviteEmployeeRequestDto { EmployeeId = employee.Id },
                Accountant(), CancellationToken.None);

        // The link back from the Employee row. Without it the account exists but nobody can find it, and the
        // Employee can be invited again -- reserving the address twice and failing on a unique constraint
        // with a message that makes no sense.
        var stored = await db.Employees.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.UserAccountId);

        var account = identity.Account(stored.UserAccountId!.Value);
        // Defaults to Employee, not CustomerAdmin: an invitation is not a promotion.
        Assert.Equal(UserRole.Employee, account.Role);
        Assert.Equal("maria@acme.example", account.LoginEmail);
        Assert.Equal("Maria Papadopoulou", account.DisplayName);

        Assert.Equal(UserRole.Employee, result.Role);
        Assert.Equal("Invited", result.AccountStatus);
        Assert.Equal(AuditActions.EmployeeInvited, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task An_override_login_email_is_written_back_onto_the_record()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        await Invite(db, identity).Handle(
            new InviteEmployeeRequestDto { EmployeeId = employee.Id, LoginEmail = "Maria.New@Acme.Example" },
            Accountant(), CancellationToken.None);

        // The record must show the address that actually received the invitation, not one the inviter
        // supplied silently while the row kept the old value.
        var stored = await db.Employees.AsNoTracking().SingleAsync();
        Assert.Equal("Maria.New@Acme.Example", stored.WorkEmail);
        Assert.Equal("MARIA.NEW@ACME.EXAMPLE", stored.NormalizedWorkEmail);
    }

    [Fact]
    public async Task Inviting_an_already_accounted_employee_is_409()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        employee.UserAccountId = identity.Seed(employee.Id);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Invite(db, identity).Handle(
            new InviteEmployeeRequestDto { EmployeeId = employee.Id },
            Accountant(), CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task Inviting_a_departed_employee_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid(), status: EmployeeStatus.Departed);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Invite(db, identity).Handle(
            new InviteEmployeeRequestDto { EmployeeId = employee.Id },
            Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    [Theory]
    [InlineData(UserRole.AccountantAdmin)]
    [InlineData(UserRole.AccountantUser)]
    public async Task Inviting_an_employee_into_an_accountant_role_is_422(UserRole role)
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Invite(db, identity).Handle(
            new InviteEmployeeRequestDto { EmployeeId = employee.Id, Role = role },
            Accountant(), CancellationToken.None));

        // A 422 for the user, before Identity's own InvalidOperationException for the programmer. Both
        // guards stay: an Employee of a Customer is not staff of the accounting office.
        Assert.Equal(422, exception.StatusCode);
        Assert.Null((await db.Employees.AsNoTracking().SingleAsync()).UserAccountId);
    }

    [Fact]
    public async Task An_email_already_a_login_at_another_customer_is_409_and_names_nobody()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var otherCustomerEmployeeId = Guid.NewGuid();
        identity.Seed(otherCustomerEmployeeId, UserRole.Employee, "Active", "shared@family.example");

        // Work email is unique per Customer, so this row is perfectly legal -- the collision only exists
        // in Identity, where login emails are globally unique.
        var employee = EmployeeEntity(Guid.NewGuid(), workEmail: "shared@family.example");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Invite(db, identity).Handle(
            new InviteEmployeeRequestDto { EmployeeId = employee.Id },
            Accountant(), CancellationToken.None));

        // A 409, not a 500 -- the value came from the client. And the message must not reveal WHERE the
        // address is already in use, because that is another Customer's data.
        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("That email address is already in use.", exception.Message);
        Assert.DoesNotContain(otherCustomerEmployeeId.ToString(), exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inviting_with_no_email_on_file_and_none_supplied_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid(), workEmail: null);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Invite(db, identity).Handle(
            new InviteEmployeeRequestDto { EmployeeId = employee.Id },
            Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task When_the_account_creation_fails_no_account_id_is_written()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi
        {
            InviteFailure = new AppException("An account with that email address already exists.", 409)
        };
        var transaction = new Identity.CountingRequestTransaction();
        var audit = new TestAuditApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<AppException>(() => new InviteEmployeeHandler(
                db, Permissions(audit), transaction, identity, audit)
            .Handle(new InviteEmployeeRequestDto { EmployeeId = employee.Id },
                Accountant(), CancellationToken.None));

        // Read back from the store rather than the tracked entity, so this asserts what a later request
        // would see: an Employee who is still invitable, rather than one pointing at an account that was
        // never created.
        db.ChangeTracker.Clear();
        Assert.Null((await db.Employees.AsNoTracking().SingleAsync()).UserAccountId);
        Assert.True(transaction.RolledBack);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_customer_admin_may_not_invite_another_customers_employee()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var user = new CurrentUser(Guid.NewGuid().ToString(), UserRole.CustomerAdmin, Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<AppException>(() => Invite(db, identity).Handle(
            new InviteEmployeeRequestDto { EmployeeId = employee.Id }, user, CancellationToken.None));

        // 404 from the scope filter, not 403: the row is being hidden, so confirming it exists would leak.
        Assert.Equal(404, exception.StatusCode);
    }

    // --- 4.6 set-role ---

    [Fact]
    public async Task An_accountant_may_demote_a_customer_admin_when_another_active_one_remains()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var target = Admin(db, identity, customerId, "Ada", "Admin");
        Admin(db, identity, customerId, "Bob", "Backup");
        await db.SaveChangesAsync();

        var result = await new SetEmployeeRoleHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), identity, audit)
            .Handle(new SetEmployeeRoleRequestDto { EmployeeId = target.Id, Role = UserRole.Employee },
                Accountant(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UserRole.Employee, identity.Account(target.UserAccountId!.Value).Role);

        // Both role names in the entry. Without them the log records that a role changed but not to what,
        // which makes it useless for the one question it will be asked: who made this person an
        // administrator.
        var entry = Assert.Single(audit.WithAction(AuditActions.EmployeeRoleChanged));
        Assert.Contains("CustomerAdmin", JsonSerializer.Serialize(entry.Before), StringComparison.Ordinal);
        Assert.Contains("Employee", JsonSerializer.Serialize(entry.After), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Demoting_the_last_active_customer_admin_is_422_and_changes_nothing()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var sole = Admin(db, identity, customerId, "Ada", "Admin");
        // A plain Employee and a SUSPENDED Customer Admin, neither of which counts. Counting Admins of any
        // status would pass here, leaving a Customer whose only administrator cannot log in.
        var suspendedAdmin = EmployeeEntity(customerId, "Sam", "Suspended", "sam@acme.example");
        suspendedAdmin.UserAccountId = identity.Seed(
            suspendedAdmin.Id, UserRole.CustomerAdmin, "Suspended", "sam@acme.example");
        db.Employees.AddRange(suspendedAdmin, EmployeeEntity(customerId, "Eve", "Employee", "eve@acme.example"));
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => SetRole(db, identity).Handle(
            new SetEmployeeRoleRequestDto { EmployeeId = sole.Id, Role = UserRole.Employee },
            Accountant(), CancellationToken.None));

        // 422, not 403: the caller has the role, the data's state forbids the operation. A 403 would suggest
        // re-authenticating as somebody more powerful, which would not help.
        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(UserRole.CustomerAdmin, identity.Account(sole.UserAccountId!.Value).Role);
    }

    [Fact]
    public async Task A_departed_customer_admin_does_not_count_towards_the_invariant()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var sole = Admin(db, identity, customerId, "Ada", "Admin");

        // An Active ACCOUNT on a Departed Employee record. The guard reads the Employee half from this
        // slice and the role from Identity, and both have to be Active for the person to count.
        var departedAdmin = EmployeeEntity(
            customerId, "Gone", "Admin", "gone@acme.example", status: EmployeeStatus.Departed);
        departedAdmin.UserAccountId = identity.Seed(
            departedAdmin.Id, UserRole.CustomerAdmin, "Active", "gone@acme.example");
        db.Employees.Add(departedAdmin);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => SetRole(db, identity).Handle(
            new SetEmployeeRoleRequestDto { EmployeeId = sole.Id, Role = UserRole.Employee },
            Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task The_invariant_batches_past_the_lookup_cap_instead_of_refusing()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();

        // 502 accounted Active Employees at one Customer -- past IIdentityApi.FindManyAsync's cap of 500 by
        // two, which is enough to force a second batch without making the test slow. A Customer this size is
        // ordinary; the original guard asked for all of them in one call and got an
        // InvalidOperationException, which surfaced as a 500 on every departure, demotion and suspension at
        // that Customer.
        var first = Admin(db, identity, customerId, "Ada", "Admin");
        var second = Admin(db, identity, customerId, "Bob", "Admin");
        for (var index = 0; index < 500; index++)
        {
            var email = $"person{index:D3}@acme.example";
            var employee = EmployeeEntity(customerId, $"Given{index:D3}", "Person", email);
            employee.UserAccountId = identity.Seed(employee.Id, UserRole.Employee, "Active", email);
            db.Employees.Add(employee);
        }

        await db.SaveChangesAsync();

        // Succeeds, because the other Admin is found. WHICH batch they turn up in is not something the guard
        // may depend on -- account ids come back in whatever order the query yields -- so it reads every
        // batch, which is the only way the count is exact at any Customer size.
        await SetRole(db, identity).Handle(
            new SetEmployeeRoleRequestDto { EmployeeId = first.Id, Role = UserRole.Employee },
            Accountant(), CancellationToken.None);

        Assert.Equal(UserRole.Employee, identity.Account(first.UserAccountId!.Value).Role);
        Assert.Equal(UserRole.CustomerAdmin, identity.Account(second.UserAccountId!.Value).Role);

        // Two calls, not one and not 502. One call would have thrown; a call per Employee would be 502 round
        // trips, which is the other way to get this wrong.
        Assert.Equal(2, identity.FindManyCallCount);
    }

    [Fact]
    public async Task The_invariant_still_fires_when_the_only_other_admin_is_beyond_the_first_batch()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var sole = Admin(db, identity, customerId, "Ada", "Admin");

        // 501 plain Employees, so the last one sits alone in the second batch and there is no other Admin
        // anywhere. A guard that stopped at the first batch would still be reading only Employees here and
        // would correctly refuse -- so the assertion that matters is the one below it: every batch was read
        // before the refusal, which is what makes the count exact rather than luckily right.
        for (var index = 0; index < 501; index++)
        {
            var email = $"person{index:D3}@acme.example";
            var employee = EmployeeEntity(customerId, $"Given{index:D3}", "Person", email);
            employee.UserAccountId = identity.Seed(employee.Id, UserRole.Employee, "Active", email);
            db.Employees.Add(employee);
        }

        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => SetRole(db, identity).Handle(
            new SetEmployeeRoleRequestDto { EmployeeId = sole.Id, Role = UserRole.Employee },
            Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(
            "This Customer must always have at least one active Customer Admin.", exception.Message);
        Assert.Equal(2, identity.FindManyCallCount);
        Assert.Equal(UserRole.CustomerAdmin, identity.Account(sole.UserAccountId!.Value).Role);
    }

    [Fact]
    public async Task Departing_at_a_customer_past_the_lookup_cap_succeeds()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var notifications = new RecordingNotificationApi();
        var customerId = Guid.NewGuid();
        Admin(db, identity, customerId, "Ada", "Admin");

        // The same size of Customer, through the other caller of the guard AND through the notification
        // helper, which batches to the same cap for the same reason. Both loops run over the same 501
        // accounts in one request, so a cap-related throw in either one fails this test.
        for (var index = 0; index < 500; index++)
        {
            var email = $"person{index:D3}@acme.example";
            var employee = EmployeeEntity(customerId, $"Given{index:D3}", "Person", email);
            employee.UserAccountId = identity.Seed(employee.Id, UserRole.Employee, "Active", email);
            db.Employees.Add(employee);
        }

        var leaver = EmployeeEntity(customerId, "Gone", "Person", "gone@acme.example");
        leaver.UserAccountId = identity.Seed(leaver.Id, UserRole.Employee, "Active", "gone@acme.example");
        db.Employees.Add(leaver);
        await db.SaveChangesAsync();

        var result = await Depart(db, identity, notifications).Handle(
            new DepartEmployeeRequestDto
            {
                EmployeeId = leaver.Id,
                EmploymentEndDate = new DateOnly(2026, 8, 31)
            }, Accountant(), CancellationToken.None);

        Assert.True(result.Success);

        // And the notification still reaches the one Admin. A helper that gave up above the cap would send
        // nothing at all, silently, at exactly the Customers with the most people to tell.
        Assert.Single(notifications.OfKind(NotificationEvents.EmployeeDeparted));
    }

    // §11.3 test 3. A hand-made CurrentUser whose Id happens to be the EMPLOYEE id tests the bug rather
    // than the rule: user.Id is an ACCOUNT id, and comparing it to employee.Id never matches, so the guard
    // silently never fires while looking entirely correct in review. SessionFor builds the session from the
    // row's UserAccountId, which is what CurrentUserFactory really puts there.
    [Fact]
    public async Task A_customer_admin_cannot_demote_themselves()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var self = Admin(db, identity, customerId, "Ada", "Admin");
        // A second Active Admin exists, so the invariant guard would NOT reject this. Only the self check
        // can, which is what makes this a test of the self check.
        Admin(db, identity, customerId, "Bob", "Backup");
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => SetRole(db, identity).Handle(
            new SetEmployeeRoleRequestDto { EmployeeId = self.Id, Role = UserRole.Employee },
            SessionFor(self, UserRole.CustomerAdmin), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal("You cannot change your own role or account status.", exception.Message);
        Assert.Equal(UserRole.CustomerAdmin, identity.Account(self.UserAccountId!.Value).Role);
    }

    [Fact]
    public async Task Setting_an_accountant_role_through_set_role_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var target = Admin(db, identity, Guid.NewGuid(), "Ada", "Admin");
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => SetRole(db, identity).Handle(
            new SetEmployeeRoleRequestDto { EmployeeId = target.Id, Role = UserRole.AccountantUser },
            Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Empty(identity.RoleCalls);
    }

    [Fact]
    public async Task Setting_a_role_on_an_accountless_employee_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => SetRole(db, identity).Handle(
            new SetEmployeeRoleRequestDto { EmployeeId = employee.Id, Role = UserRole.CustomerAdmin },
            Accountant(), CancellationToken.None));

        // 422 and not 404: the Employee exists, there is just no account to hold a role.
        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task Setting_the_role_already_held_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var target = Admin(db, identity, Guid.NewGuid(), "Ada", "Admin");
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => SetRole(db, identity).Handle(
            new SetEmployeeRoleRequestDto { EmployeeId = target.Id, Role = UserRole.CustomerAdmin },
            Accountant(), CancellationToken.None));

        // A no-op success tells the caller something happened and writes a misleading audit entry.
        Assert.Equal(422, exception.StatusCode);
        Assert.Empty(identity.RoleCalls);
    }

    [Fact]
    public async Task Promoting_a_plain_employee_needs_no_invariant_check()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        // No Customer Admin at all at this Customer -- a broken state an Accountant is supposed to be able
        // to fix. If the guard did not short-circuit on a target that is not an Active CustomerAdmin, this
        // promotion would be refused and the Customer would be frozen rather than merely broken.
        var employee = EmployeeEntity(customerId, "Eve", "Employee", "eve@acme.example");
        employee.UserAccountId = identity.Seed(employee.Id, UserRole.Employee, "Active", "eve@acme.example");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await SetRole(db, identity).Handle(
            new SetEmployeeRoleRequestDto { EmployeeId = employee.Id, Role = UserRole.CustomerAdmin },
            Accountant(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UserRole.CustomerAdmin, identity.Account(employee.UserAccountId!.Value).Role);
    }

    // --- 4.7 depart ---

    [Fact]
    public async Task Departing_an_accounted_employee_marks_the_row_and_suspends_the_account()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        employee.UserAccountId = identity.Seed(employee.Id);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await new DepartEmployeeHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), identity,
                new RecordingNotificationApi(), audit)
            .Handle(new DepartEmployeeRequestDto
            {
                EmployeeId = employee.Id,
                EmploymentEndDate = new DateOnly(2026, 8, 31)
            }, Accountant(), CancellationToken.None);

        Assert.True(result.Success);
        var stored = await db.Employees.AsNoTracking().SingleAsync();
        Assert.Equal(EmployeeStatus.Departed, stored.Status);
        Assert.Equal(new DateOnly(2026, 8, 31), stored.EmploymentEndDate);
        // Both, and they answer different questions: when the employment ended, and when that was recorded.
        Assert.NotNull(stored.DepartedAt);

        // An active login for somebody who has left the company is the exact hole this closes.
        Assert.Equal(employee.UserAccountId!.Value, Assert.Single(identity.SuspendCalls));
        Assert.Equal("Suspended", identity.Account(employee.UserAccountId!.Value).Status);
        Assert.Equal(AuditActions.EmployeeDeparted, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task Departing_an_accountless_employee_calls_identity_not_at_all()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        await Depart(db, identity).Handle(
            new DepartEmployeeRequestDto
            {
                EmployeeId = employee.Id,
                EmploymentEndDate = new DateOnly(2026, 8, 31)
            }, Accountant(), CancellationToken.None);

        Assert.Equal(EmployeeStatus.Departed, (await db.Employees.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(identity.SuspendCalls);
    }

    [Fact]
    public async Task Departing_an_employee_whose_account_is_already_suspended_succeeds()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        employee.UserAccountId = identity.Seed(employee.Id, UserRole.Employee, "Suspended");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await Depart(db, identity).Handle(
            new DepartEmployeeRequestDto
            {
                EmployeeId = employee.Id,
                EmploymentEndDate = new DateOnly(2026, 8, 31)
            }, Accountant(), CancellationToken.None);

        // SuspendAccountAsync is idempotent by contract, and that is what makes this safe: departing
        // somebody whose access had already been revoked for an unrelated reason must not fail.
        Assert.True(result.Success);
        Assert.Equal(EmployeeStatus.Departed, (await db.Employees.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task A_future_end_date_still_marks_the_record_departed_immediately()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        await Depart(db, identity).Handle(
            new DepartEmployeeRequestDto
            {
                EmployeeId = employee.Id,
                // A notice period is ordinary.
                EmploymentEndDate = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(2)
            }, Accountant(), CancellationToken.None);

        // Immediately, because the alternative is a scheduled job this application does not have.
        Assert.Equal(EmployeeStatus.Departed, (await db.Employees.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Departing_the_last_active_customer_admin_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var sole = Admin(db, identity, customerId, "Ada", "Admin");
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Depart(db, identity).Handle(
            new DepartEmployeeRequestDto
            {
                EmployeeId = sole.Id,
                EmploymentEndDate = new DateOnly(2026, 8, 31)
            }, Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(EmployeeStatus.Active, (await db.Employees.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(identity.SuspendCalls);
    }

    [Fact]
    public async Task A_customer_admin_cannot_depart_themselves()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var self = Admin(db, identity, customerId, "Ada", "Admin");
        Admin(db, identity, customerId, "Bob", "Backup");
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Depart(db, identity).Handle(
            new DepartEmployeeRequestDto
            {
                EmployeeId = self.Id,
                EmploymentEndDate = new DateOnly(2026, 8, 31)
            }, SessionFor(self, UserRole.CustomerAdmin), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task Departing_an_already_departed_employee_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid(), status: EmployeeStatus.Departed);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Depart(db, identity).Handle(
            new DepartEmployeeRequestDto
            {
                EmployeeId = employee.Id,
                EmploymentEndDate = new DateOnly(2026, 9, 30)
            }, Accountant(), CancellationToken.None));

        // Departed is terminal, and re-departing would silently overwrite the recorded end date.
        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task An_end_date_before_the_start_date_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Depart(db, identity).Handle(
            new DepartEmployeeRequestDto
            {
                EmployeeId = employee.Id,
                // The seeded start date is 2026-01-05.
                EmploymentEndDate = new DateOnly(2025, 12, 31)
            }, Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(EmployeeStatus.Active, (await db.Employees.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task A_missing_end_date_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Depart(db, identity).Handle(
            new DepartEmployeeRequestDto { EmployeeId = employee.Id },
            Accountant(), CancellationToken.None));

        // default(DateOnly) is 0001-01-01, which would pass a naive "not before the start date" check only
        // by being absurd. It is refused by name instead.
        Assert.Equal(422, exception.StatusCode);
    }

    // --- the departure notification ---

    [Fact]
    public async Task Departing_notifies_the_customers_own_active_admins_once()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var notifications = new RecordingNotificationApi();
        var customerId = Guid.NewGuid();

        var first = Admin(db, identity, customerId, "Admina", "Boss");
        var second = Admin(db, identity, customerId, "Adminb", "Boss");
        var leaver = EmployeeEntity(customerId, "Gone", "Person", "gone@acme.example");
        leaver.UserAccountId = identity.Seed(leaver.Id, UserRole.Employee, "Active", "gone@acme.example");
        db.Employees.Add(leaver);
        await db.SaveChangesAsync();

        await Depart(db, identity, notifications).Handle(
            new DepartEmployeeRequestDto
            {
                EmployeeId = leaver.Id,
                EmploymentEndDate = new DateOnly(2026, 8, 31)
            }, Accountant(), CancellationToken.None);

        // ONE NotifyManyAsync for both Admins, not a loop of NotifyAsync: a Customer with four Admins would
        // otherwise be four round trips and four SaveChanges inside one transaction.
        Assert.Equal(1, notifications.NotifyManyCallCount);

        var sent = notifications.OfKind(NotificationEvents.EmployeeDeparted).ToList();
        Assert.Equal(2, sent.Count);
        // Both sides ordered: account ids are random Guids, so an unordered comparison passes or fails on
        // which Guid happened to sort first.
        Assert.Equal(
            new[] { first.UserAccountId!.Value.ToString(), second.UserAccountId!.Value.ToString() }.Order(),
            sent.Select(request => request.RecipientUserId).Order());

        // The departing person is NOT a recipient. Their status is written before the helper runs, so the
        // query that finds Admins cannot see them -- which is why the ORDER of those two steps matters here
        // and not in registration.
        Assert.DoesNotContain(leaver.UserAccountId!.Value.ToString(),
            sent.Select(request => request.RecipientUserId));

        // The end date is in the body because a future one is ordinary: "departing on the 30th" and
        // "departed" are different facts for an Admin deciding whether to reassign work today. The name is in
        // the body and not the title, because titles are what a list shows and a list of "X departed" rows is
        // a staff list rebuilt in the wrong place.
        Assert.All(sent, request =>
        {
            Assert.Equal("An employee has departed", request.Title);
            Assert.Contains("Gone Person", request.Body, StringComparison.Ordinal);
            Assert.Contains("2026-08-31", request.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_departure_notification_is_in_app_only()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var notifications = new RecordingNotificationApi();
        var customerId = Guid.NewGuid();
        Admin(db, identity, customerId, "Admina", "Boss");
        var leaver = EmployeeEntity(customerId, "Gone", "Person", "gone@acme.example");
        db.Employees.Add(leaver);
        await db.SaveChangesAsync();

        await Depart(db, identity, notifications).Handle(
            new DepartEmployeeRequestDto
            {
                EmployeeId = leaver.Id,
                EmploymentEndDate = new DateOnly(2026, 8, 31)
            }, Accountant(), CancellationToken.None);

        // In the catalogue, deliberately NOT in Emailed. Nothing here is time-critical and nothing carries a
        // token, and an Admin who departs six people in an afternoon does not want six emails about their own
        // afternoon. NotificationApi writes an outbox row only for an Emailed kind, so this is what keeps the
        // outbox for the things that must leave the building.
        Assert.Contains(NotificationEvents.EmployeeDeparted, NotificationEvents.All);
        Assert.DoesNotContain(NotificationEvents.EmployeeDeparted, NotificationEvents.Emailed);

        // And no EmailBody, which NotificationApi rejects outright for a non-Emailed kind.
        Assert.All(notifications.Requests, request => Assert.Null(request.EmailBody));
    }

    [Fact]
    public async Task Departing_at_a_customer_with_no_admin_account_notifies_nobody()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var notifications = new RecordingNotificationApi();
        var customerId = Guid.NewGuid();

        // A plain Employee with an account, and a suspended Admin. Neither is a recipient: the first has the
        // wrong role, and queueing notifications for the second is queueing work nobody will ever collect.
        var plain = EmployeeEntity(customerId, "Plain", "Person", "plain@acme.example");
        plain.UserAccountId = identity.Seed(plain.Id, UserRole.Employee, "Active", "plain@acme.example");
        var suspended = EmployeeEntity(customerId, "Susie", "Boss", "susie@acme.example");
        suspended.UserAccountId = identity.Seed(
            suspended.Id, UserRole.CustomerAdmin, "Suspended", "susie@acme.example");
        var leaver = EmployeeEntity(customerId, "Gone", "Person", "gone@acme.example");
        db.Employees.AddRange(plain, suspended, leaver);
        await db.SaveChangesAsync();

        await Depart(db, identity, notifications).Handle(
            new DepartEmployeeRequestDto
            {
                EmployeeId = leaver.Id,
                EmploymentEndDate = new DateOnly(2026, 8, 31)
            }, Accountant(), CancellationToken.None);

        // No recipients means no call at all, not a call with an empty list -- NotifyManyAsync with nothing in
        // it is a round trip and a SaveChanges for no reason, on the most common shape of Customer there is.
        Assert.Equal(0, notifications.NotifyManyCallCount);
        Assert.Empty(notifications.Requests);
    }

    [Fact]
    public async Task An_invited_admin_still_receives_the_departure_notification()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var notifications = new RecordingNotificationApi();
        var customerId = Guid.NewGuid();

        var invited = EmployeeEntity(customerId, "Admina", "Boss", "admina@acme.example");
        invited.UserAccountId = identity.Seed(
            invited.Id, UserRole.CustomerAdmin, "Invited", "admina@acme.example");
        var leaver = EmployeeEntity(customerId, "Gone", "Person", "gone@acme.example");
        db.Employees.AddRange(invited, leaver);
        await db.SaveChangesAsync();

        await Depart(db, identity, notifications).Handle(
            new DepartEmployeeRequestDto
            {
                EmployeeId = leaver.Id,
                EmploymentEndDate = new DateOnly(2026, 8, 31)
            }, Accountant(), CancellationToken.None);

        // Invited counts and Suspended does not, and the difference is not arbitrary: an Admin who has not
        // accepted their invitation yet will read this the day they do, whereas a suspended one is somebody
        // whose access was deliberately revoked.
        Assert.Equal(
            invited.UserAccountId!.Value.ToString(),
            Assert.Single(notifications.Requests).RecipientUserId);
    }

    // --- 4.8 suspend and reactivate ---

    [Fact]
    public async Task Suspending_an_account_leaves_the_employee_active()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        employee.UserAccountId = identity.Seed(employee.Id);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await new SuspendEmployeeAccountHandler(
                db, Permissions(new TestAuditApi()), new NoOpRequestTransaction(), identity)
            .Handle(new EmployeeIdRequestDto { EmployeeId = employee.Id },
                Accountant(), CancellationToken.None);

        Assert.True(result.Success);
        // Suspension is temporary and reversible; departure is neither. Suspending must not mark anybody
        // Departed, even though departing suspends.
        var stored = await db.Employees.AsNoTracking().SingleAsync();
        Assert.Equal(EmployeeStatus.Active, stored.Status);
        Assert.Null(stored.DepartedAt);
        Assert.Equal("Suspended", identity.Account(employee.UserAccountId!.Value).Status);
    }

    [Fact]
    public async Task Suspending_writes_no_audit_entry_of_its_own()
    {
        await using var db = NewDb();
        var audit = new TestAuditApi();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        employee.UserAccountId = identity.Seed(employee.Id);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        await new SuspendEmployeeAccountHandler(
                db, Permissions(audit), new NoOpRequestTransaction(), identity)
            .Handle(new EmployeeIdRequestDto { EmployeeId = employee.Id },
                Accountant(), CancellationToken.None);

        // Identity audits the suspension, because the account is its data. No employees row changed here, so
        // there is nothing of this slice's to record and a second entry would be one event logged twice.
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Suspending_the_last_active_customer_admin_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var sole = Admin(db, identity, customerId, "Ada", "Admin");
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Suspend(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = sole.Id }, Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Empty(identity.SuspendCalls);
    }

    [Fact]
    public async Task Suspending_an_accountless_employee_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Suspend(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = employee.Id }, Accountant(), CancellationToken.None));

        // 422, not 404: the Employee exists, there is just nothing to suspend.
        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task A_customer_admin_cannot_suspend_their_own_account()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var customerId = Guid.NewGuid();
        var self = Admin(db, identity, customerId, "Ada", "Admin");
        Admin(db, identity, customerId, "Bob", "Backup");
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Suspend(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = self.Id },
            SessionFor(self, UserRole.CustomerAdmin), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Empty(identity.SuspendCalls);
    }

    [Fact]
    public async Task Reactivating_an_active_employees_suspended_account_succeeds()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        employee.UserAccountId = identity.Seed(employee.Id, UserRole.Employee, "Suspended");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await Reactivate(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = employee.Id }, Accountant(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Active", identity.Account(employee.UserAccountId!.Value).Status);
    }

    [Fact]
    public async Task Reactivating_a_departed_employees_account_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid(), status: EmployeeStatus.Departed);
        employee.UserAccountId = identity.Seed(employee.Id, UserRole.Employee, "Suspended");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Reactivate(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = employee.Id }, Accountant(), CancellationToken.None));

        // The rule most likely to be omitted, because it is a cross-check between two pieces of state in two
        // slices. A Departed Employee's suspension is a CONSEQUENCE of their departure, so lifting it alone
        // would produce Departed employment with Active access -- the one pair nothing else in the slice can
        // produce and nothing downstream expects. It stays a 422 even though /reinstate now exists:
        // reinstatement reactivates the account itself, as one operation on one consistent state.
        Assert.Equal(422, exception.StatusCode);
        Assert.Empty(identity.ReactivateCalls);
    }

    [Fact]
    public async Task Reactivating_an_accountless_employee_is_422()
    {
        await using var db = NewDb();
        var identity = new FakeIdentityApi();
        var employee = EmployeeEntity(Guid.NewGuid());
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppException>(() => Reactivate(db, identity).Handle(
            new EmployeeIdRequestDto { EmployeeId = employee.Id }, Accountant(), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    // --- helpers ---

    /// <summary>
    /// An Employee with an Active CustomerAdmin account, added to the context but not saved -- the caller
    /// saves once. Returned so a test can build a session from it or read its account back.
    /// </summary>
    private static Employee Admin(
        EmployeesDbContext db, FakeIdentityApi identity, Guid customerId, string given, string family)
    {
        var employee = EmployeeEntity(
            customerId, given, family, $"{given.ToLowerInvariant()}@acme.example");
        employee.UserAccountId = identity.Seed(
            employee.Id, UserRole.CustomerAdmin, "Active", $"{given.ToLowerInvariant()}@acme.example");
        db.Employees.Add(employee);
        return employee;
    }

    private static InviteEmployeeHandler Invite(EmployeesDbContext db, FakeIdentityApi identity)
    {
        var audit = new TestAuditApi();
        return new InviteEmployeeHandler(
            db, Permissions(audit), new NoOpRequestTransaction(), identity, audit);
    }

    private static SetEmployeeRoleHandler SetRole(EmployeesDbContext db, FakeIdentityApi identity)
    {
        var audit = new TestAuditApi();
        return new SetEmployeeRoleHandler(
            db, Permissions(audit), new NoOpRequestTransaction(), identity, audit);
    }

    private static DepartEmployeeHandler Depart(
        EmployeesDbContext db, FakeIdentityApi identity, RecordingNotificationApi? notifications = null)
    {
        var audit = new TestAuditApi();
        return new DepartEmployeeHandler(
            db, Permissions(audit), new NoOpRequestTransaction(), identity,
            notifications ?? new RecordingNotificationApi(), audit);
    }

    private static SuspendEmployeeAccountHandler Suspend(
        EmployeesDbContext db, FakeIdentityApi identity) =>
        new(db, Permissions(new TestAuditApi()), new NoOpRequestTransaction(), identity);

    private static ReactivateEmployeeAccountHandler Reactivate(
        EmployeesDbContext db, FakeIdentityApi identity) =>
        new(db, Permissions(new TestAuditApi()), new NoOpRequestTransaction(), identity);
}
