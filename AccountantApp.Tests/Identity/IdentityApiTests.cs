using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Tests.Identity;

/// <summary>
/// The cross-slice surface. Its rules are deliberately DIFFERENT from the HTTP handlers': these methods
/// enlist rather than begin, never commit, never check permissions, and assert a target state instead of
/// rejecting a no-op. Each of those differences is a place where copying handler behaviour across would
/// break a caller.
/// </summary>
public class IdentityApiTests
{
    private static (IdentityApi Api, RecordingAuditApi Audit, CountingRequestTransaction Transaction,
        RecordingNotificationApi Notifications) Build(IdentityDbContext db)
    {
        var audit = new RecordingAuditApi();
        var transaction = new CountingRequestTransaction();
        var notifications = new RecordingNotificationApi();
        var api = new IdentityApi(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Links, notifications, transaction, audit);
        return (api, audit, transaction, notifications);
    }

    private static InviteEmployeeAccount ValidInvite(Guid? customerId = null, Guid? employeeId = null) =>
        new(employeeId ?? Guid.NewGuid(), customerId ?? Guid.NewGuid(),
            "employee@customer.example.com", "Emma Employee", UserRole.Employee);

    // --- Writes never commit ---

    [Fact]
    public async Task No_write_on_this_surface_ever_commits()
    {
        await using var db = IdentityTestHarness.NewDb();
        var target = IdentityTestHarness.NewAccount(email: "target@example.com");
        var admin = IdentityTestHarness.NewAccount(email: "admin@example.com", role: UserRole.AccountantAdmin);
        var employee = IdentityTestHarness.NewAccount(
            email: "employee@example.com", role: UserRole.Employee,
            customerId: Guid.NewGuid(), employeeId: Guid.NewGuid());
        db.UserAccounts.AddRange(target, admin, employee);
        await db.SaveChangesAsync();

        var (api, _, transaction, _) = Build(db);

        await api.InviteEmployeeAccountAsync(ValidInvite(), default);
        await api.SuspendAccountAsync(target.Id, default);
        await api.ReactivateAccountAsync(target.Id, default);
        await api.SetCustomerSideRoleAsync(employee.Id, UserRole.CustomerAdmin, default);

        // The caller owns the transaction. Committing here would let this slice's write survive a failure
        // in the calling slice's next step -- an Employee row that rolls back leaving behind a login
        // account for a person who was never created.
        Assert.Equal(0, transaction.Commits);
    }

    // --- InviteEmployeeAccountAsync ---

    [Fact]
    public async Task Inviting_an_employee_creates_an_invited_account_scoped_to_their_customer()
    {
        await using var db = IdentityTestHarness.NewDb();
        var (api, audit, _, notifications) = Build(db);
        var customerId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();

        var accountId = await api.InviteEmployeeAccountAsync(
            ValidInvite(customerId, employeeId), default);

        var created = await db.UserAccounts.SingleAsync();
        Assert.Equal(accountId, created.Id);
        Assert.Equal(AccountStatus.Invited, created.Status);
        Assert.Null(created.PasswordHash);

        // Both ids are mandatory here, unlike an Accountant where both are null. customer_id is what every
        // Customer-side scope check compares against; employee_id is what links the login to the person.
        Assert.Equal(customerId, created.CustomerId);
        Assert.Equal(employeeId, created.EmployeeId);

        // EmployeeInvited, not Invited. Both must be in the outbox drainer's invitation allow-list -- an
        // invitee is not Active, so the suspended-recipient skip would otherwise swallow the mail. If only
        // Invited is allow-listed, Accountants get invited and Employees silently never do, which is far
        // harder to spot than a total failure because it works for whoever is testing it.
        Assert.Equal(NotificationEvents.EmployeeInvited,
            Assert.Single(notifications.Requests).EventKind);

        Assert.Single(audit.WithAction(AuditActions.AccountInvited));
    }

    [Fact]
    public async Task Inviting_an_employee_with_an_accountant_role_throws_rather_than_returning_a_status()
    {
        foreach (var role in new[] { UserRole.AccountantAdmin, UserRole.AccountantUser })
        {
            await using var db = IdentityTestHarness.NewDb();
            var (api, _, _, _) = Build(db);

            // InvalidOperationException, not AppException. A Customer-side caller passing an Accountant
            // role is a bug in that slice, not something a user typed -- a 422 would be shown to an end
            // user who can do nothing about it, and would be swallowed by the same handler that reports
            // genuine validation failures.
            await Assert.ThrowsAsync<InvalidOperationException>(() => api.InviteEmployeeAccountAsync(
                ValidInvite() with { Role = role }, default));

            Assert.Equal(0, await db.UserAccounts.CountAsync());
        }
    }

    [Fact]
    public async Task Both_scope_ids_are_mandatory_and_empty_is_not_a_value()
    {
        await using var db = IdentityTestHarness.NewDb();
        var (api, _, _, _) = Build(db);

        // Guid.Empty is the default for an uninitialised field, so it is exactly what arrives when a caller
        // forgets to set one. It parses and it stores, so without these guards the result is an account
        // scoped to a Customer that does not exist -- and every scope check quietly matches nothing.
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.InviteEmployeeAccountAsync(
            ValidInvite() with { CustomerId = Guid.Empty }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.InviteEmployeeAccountAsync(
            ValidInvite() with { EmployeeId = Guid.Empty }, default));
    }

    [Fact]
    public async Task A_duplicate_email_or_a_second_account_for_one_employee_is_a_409()
    {
        await using var db = IdentityTestHarness.NewDb();
        var (api, _, _, _) = Build(db);
        var employeeId = Guid.NewGuid();

        await api.InviteEmployeeAccountAsync(ValidInvite(employeeId: employeeId), default);

        // AppException 409, not InvalidOperationException: a colleague already having an account IS
        // something a user can act on, unlike the guards above.
        var duplicateEmail = await Assert.ThrowsAsync<AppException>(() => api.InviteEmployeeAccountAsync(
            ValidInvite() with { LoginEmail = "EMPLOYEE@customer.example.com" }, default));
        Assert.Equal(409, duplicateEmail.StatusCode);

        var duplicateEmployee = await Assert.ThrowsAsync<AppException>(() =>
            api.InviteEmployeeAccountAsync(
                ValidInvite(employeeId: employeeId) with { LoginEmail = "different@example.com" }, default));
        Assert.Equal(409, duplicateEmployee.StatusCode);

        // Checked in code as well as by uq_user_accounts_employee, so the caller gets a 409 rather than a
        // unique-violation 500.
        Assert.Equal(1, await db.UserAccounts.CountAsync());
    }

    // --- Idempotent suspend and reactivate ---

    [Fact]
    public async Task Suspend_and_reactivate_are_idempotent_here_unlike_the_http_handlers()
    {
        await using var db = IdentityTestHarness.NewDb();
        var target = IdentityTestHarness.NewAccount(email: "target@example.com");
        db.UserAccounts.Add(target);
        db.UserAccounts.Add(IdentityTestHarness.NewAccount(
            email: "admin@example.com", role: UserRole.AccountantAdmin));
        await db.SaveChangesAsync();

        var (api, audit, _, _) = Build(db);

        await api.SuspendAccountAsync(target.Id, default);
        await api.SuspendAccountAsync(target.Id, default);

        // Deliberately NOT the handlers' 422. These methods assert a target state for a caller that is
        // offboarding somebody: a departure whose account was already suspended for an unrelated reason
        // must still be recordable, and a 422 there would abort the whole offboarding transaction.
        Assert.Equal(AccountStatus.Suspended, target.Status);
        Assert.Single(audit.WithAction(AuditActions.AccountSuspended));   // audited once, not twice

        await api.ReactivateAccountAsync(target.Id, default);
        await api.ReactivateAccountAsync(target.Id, default);
        Assert.Equal(AccountStatus.Active, target.Status);
        Assert.Single(audit.WithAction(AuditActions.AccountReactivated));
    }

    [Fact]
    public async Task An_unknown_id_is_a_no_op_on_both_state_assertions()
    {
        await using var db = IdentityTestHarness.NewDb();
        var (api, audit, _, _) = Build(db);

        // No exception. A caller cleaning up after a deleted Employee should not have to distinguish
        // "already gone" from "never existed" -- both mean the account is not active, which is the
        // requested state.
        await api.SuspendAccountAsync(Guid.NewGuid(), default);
        await api.ReactivateAccountAsync(Guid.NewGuid(), default);

        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Reactivating_an_invited_account_through_this_surface_is_also_a_no_op()
    {
        await using var db = IdentityTestHarness.NewDb();
        var invited = IdentityTestHarness.NewAccount(
            email: "invited@example.com", status: AccountStatus.Invited, password: null);
        db.UserAccounts.Add(invited);
        await db.SaveChangesAsync();

        var (api, _, _, _) = Build(db);
        await api.ReactivateAccountAsync(invited.Id, default);

        // The `!= Suspended` check covers Invited too, which is what stops a null-password account being
        // flipped to Active. ck_user_accounts_status would reject the row -- and if it did not, the result
        // is an Active account that can never log in and that the reset flow will not help, because from
        // the outside it looks completely normal.
        Assert.Equal(AccountStatus.Invited, invited.Status);
        Assert.Null(invited.PasswordHash);
    }

    [Fact]
    public async Task Suspending_the_last_active_admin_is_blocked_even_through_this_back_door()
    {
        await using var db = IdentityTestHarness.NewDb();
        var onlyAdmin = IdentityTestHarness.NewAccount(
            email: "admin@example.com", role: UserRole.AccountantAdmin);
        db.UserAccounts.Add(onlyAdmin);
        await db.SaveChangesAsync();

        var (api, _, transaction, _) = Build(db);

        // Employees only ever means to touch Customer-side accounts, but the parameter is a bare Guid.
        // A wrong one that happens to be the last Admin must not get through a door the HTTP endpoint has
        // locked -- so the invariant lives in AccountInvariants and is called from both.
        await Assert.ThrowsAsync<AppException>(() => api.SuspendAccountAsync(onlyAdmin.Id, default));
        Assert.Equal(0, transaction.Commits);
    }

    // --- SetCustomerSideRoleAsync ---

    [Fact]
    public async Task Setting_a_customer_side_role_records_the_transition()
    {
        await using var db = IdentityTestHarness.NewDb();
        var employee = IdentityTestHarness.NewAccount(
            email: "employee@example.com", role: UserRole.Employee,
            customerId: Guid.NewGuid(), employeeId: Guid.NewGuid());
        db.UserAccounts.Add(employee);
        await db.SaveChangesAsync();

        var (api, audit, _, _) = Build(db);
        await api.SetCustomerSideRoleAsync(employee.Id, UserRole.CustomerAdmin, default);

        Assert.Equal(UserRole.CustomerAdmin, employee.Role);

        // Reuses EmployeeEdited -- the vocabulary has no CustomerSideRoleChanged, and the Before/After
        // snapshots carry the role, so the transition is fully recorded.
        var entry = Assert.Single(audit.WithAction(AuditActions.EmployeeEdited));
        Assert.NotNull(entry.Before);
        Assert.NotNull(entry.After);
    }

    [Fact]
    public async Task An_accountant_account_cannot_be_reached_through_the_customer_side_role_setter()
    {
        await using var db = IdentityTestHarness.NewDb();
        var accountant = IdentityTestHarness.NewAccount(
            email: "accountant@example.com", role: UserRole.AccountantAdmin);
        db.UserAccounts.Add(accountant);
        await db.SaveChangesAsync();

        var (api, _, _, _) = Build(db);

        // The target guard, separate from the role guard, because passing the wrong id is a different bug
        // from passing the wrong role. Without it a Customer-side actor could demote an Accountant Admin --
        // and if that were the last one, the Office would be unadministrable from a Customer's action.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.SetCustomerSideRoleAsync(accountant.Id, UserRole.Employee, default));

        Assert.Equal(UserRole.AccountantAdmin, accountant.Role);
    }

    [Fact]
    public async Task An_accountant_role_is_refused_by_the_customer_side_role_setter()
    {
        await using var db = IdentityTestHarness.NewDb();
        var employee = IdentityTestHarness.NewAccount(
            email: "employee@example.com", role: UserRole.Employee,
            customerId: Guid.NewGuid(), employeeId: Guid.NewGuid());
        db.UserAccounts.Add(employee);
        await db.SaveChangesAsync();

        var (api, _, _, _) = Build(db);

        // The role guard runs BEFORE the database is touched, so this throws even though the target is a
        // perfectly valid Customer-side account.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.SetCustomerSideRoleAsync(employee.Id, UserRole.AccountantAdmin, default));

        Assert.Equal(UserRole.Employee, employee.Role);
    }

    // --- Reads ---

    [Fact]
    public async Task IsActive_is_false_for_unknown_suspended_and_invited_accounts()
    {
        await using var db = IdentityTestHarness.NewDb();
        var active = IdentityTestHarness.NewAccount(email: "active@example.com");
        var suspended = IdentityTestHarness.NewAccount(
            email: "suspended@example.com", status: AccountStatus.Suspended);
        var invited = IdentityTestHarness.NewAccount(
            email: "invited@example.com", status: AccountStatus.Invited, password: null);
        db.UserAccounts.AddRange(active, suspended, invited);
        await db.SaveChangesAsync();

        var (api, _, _, _) = Build(db);

        Assert.True(await api.IsActiveAsync(active.Id, default));
        Assert.False(await api.IsActiveAsync(suspended.Id, default));

        // Invited is NOT active. Someone who has never set a password cannot pick up a ticket, so offering
        // them as an assignee creates work that is stranded the moment it is assigned.
        Assert.False(await api.IsActiveAsync(invited.Id, default));

        // Unknown id is false, not an exception: fail closed, matching ICustomerApi.IsActiveAsync.
        Assert.False(await api.IsActiveAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task FindMany_deduplicates_and_refuses_an_oversized_batch()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(email: "one@example.com");
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        var (api, _, _, _) = Build(db);

        Assert.Empty(await api.FindManyAsync([], default));

        // A page of tickets all assigned to the same person sends that id many times.
        var found = await api.FindManyAsync([account.Id, account.Id, account.Id], default);
        Assert.Single(found);

        // Throws rather than truncating. A silently short dictionary gives the caller rows whose assignee
        // renders as blank, which reads as missing data rather than as a batch that was too big.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.FindManyAsync(Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray(), default));
    }

    [Fact]
    public async Task ListAccountants_hides_inactive_accounts_by_default_and_excludes_customer_side_ones()
    {
        await using var db = IdentityTestHarness.NewDb();
        db.UserAccounts.AddRange(
            IdentityTestHarness.NewAccount(email: "active@example.com"),
            IdentityTestHarness.NewAccount(email: "suspended@example.com", status: AccountStatus.Suspended),
            IdentityTestHarness.NewAccount(
                email: "employee@example.com", role: UserRole.Employee,
                customerId: Guid.NewGuid(), employeeId: Guid.NewGuid()));
        await db.SaveChangesAsync();

        var (api, _, _, _) = Build(db);

        // Defaults to true because the main caller is an assignee picker. Note this is the OPPOSITE default
        // from ListAccountantsHandler, which shows every status -- an Admin cannot reactivate somebody the
        // list hides, whereas an assignee picker must not offer somebody who cannot work.
        var activeOnly = await api.ListAccountantsAsync(ct: default);
        Assert.Equal("active@example.com", Assert.Single(activeOnly).LoginEmail);

        var everyone = await api.ListAccountantsAsync(activeOnly: false, default);
        Assert.Equal(2, everyone.Count);
        Assert.DoesNotContain(everyone, item => item.LoginEmail == "employee@example.com");
    }

    [Fact]
    public async Task FindByEmployee_resolves_the_one_account_linked_to_a_person()
    {
        await using var db = IdentityTestHarness.NewDb();
        var employeeId = Guid.NewGuid();
        db.UserAccounts.Add(IdentityTestHarness.NewAccount(
            email: "employee@example.com", role: UserRole.Employee,
            customerId: Guid.NewGuid(), employeeId: employeeId));
        await db.SaveChangesAsync();

        var (api, _, _, _) = Build(db);

        var found = await api.FindByEmployeeAsync(employeeId, default);
        Assert.NotNull(found);
        Assert.True(found.IsActive);

        Assert.Null(await api.FindByEmployeeAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public void The_summary_exposes_is_active_as_a_derived_flag_not_a_stored_one()
    {
        // Callers ask "can this person act", and every one of them re-deriving that from a status string
        // is how one of them ends up treating Invited as usable.
        Assert.True(new AccountSummary(
            Guid.NewGuid(), "A", "a@example.com", UserRole.AccountantUser, AccountStatus.Active).IsActive);

        foreach (var status in new[] { AccountStatus.Suspended, AccountStatus.Invited })
            Assert.False(new AccountSummary(
                Guid.NewGuid(), "A", "a@example.com", UserRole.AccountantUser, status).IsActive);
    }
}
