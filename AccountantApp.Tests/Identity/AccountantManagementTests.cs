using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity;
using AccountantApp.Api.Slices.Identity.Application;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Application.Handlers;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccountantApp.Tests.Identity;

public class AccountantManagementTests
{
    /// <summary>
    /// The real PermissionChecker over the real IdentityActionCatalogue, not a stub that always allows.
    /// A stub would pass even if an action name in the catalogue were misspelled -- and a misspelled name
    /// is not a compile error, it is an action that no role can ever perform.
    /// </summary>
    private static IPermissionChecker Permissions(IAuditApi audit) =>
        new PermissionChecker([new IdentityActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);

    private static CurrentUser Admin(Guid? id = null) =>
        new((id ?? Guid.NewGuid()).ToString(), UserRole.AccountantAdmin);

    private static CurrentUser Staff() => new(Guid.NewGuid().ToString(), UserRole.AccountantUser);

    private static UserAccount AddAdmin(IdentityDbContext db, string email = "admin@example.com") =>
        Add(db, IdentityTestHarness.NewAccount(email: email, role: UserRole.AccountantAdmin));

    private static UserAccount Add(IdentityDbContext db, UserAccount account)
    {
        db.UserAccounts.Add(account);
        return account;
    }

    // --- Suspend ---

    [Fact]
    public async Task An_admin_cannot_suspend_themselves()
    {
        await using var db = IdentityTestHarness.NewDb();
        var self = AddAdmin(db);
        AddAdmin(db, "other@example.com");   // a second Admin, so the invariant would NOT catch this
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new SuspendAccountantHandler(
            db, Permissions(audit), new RecordingNotificationApi(), new CountingRequestTransaction(), audit);

        // With a second Admin present the last-Admin invariant passes happily, so self-suspension is only
        // stopped by the explicit RequireNotSelf check. Remove it and an Admin can end their own session
        // mid-request and get a 200 back for doing it.
        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new AccountIdRequestDto { UserAccountId = self.Id }, Admin(self.Id), default));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(AccountStatus.Active, self.Status);
    }

    [Fact]
    public async Task Suspending_the_last_active_admin_is_refused_and_rolled_back()
    {
        await using var db = IdentityTestHarness.NewDb();
        var onlyOtherAdmin = AddAdmin(db, "target@example.com");
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var transaction = new CountingRequestTransaction();
        var handler = new SuspendAccountantHandler(
            db, Permissions(audit), new RecordingNotificationApi(), transaction, audit);

        // The caller is an Admin who is NOT in this database (their own row is irrelevant to the count);
        // the target is the only Active Admin row. Suspending it leaves an Office nobody can administer,
        // and no role in the system can undo that.
        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new AccountIdRequestDto { UserAccountId = onlyOtherAdmin.Id }, Admin(), default));

        Assert.Equal(422, exception.StatusCode);

        // No commit reached, so the real RequestTransaction rolls the suspension back. The in-memory
        // entity still shows Suspended because SaveChangesAsync ran -- the rollback is the transaction's
        // job, and that it was never committed is the assertion that matters.
        Assert.Equal(0, transaction.Commits);
        Assert.True(transaction.RolledBack);
    }

    [Fact]
    public async Task Suspending_succeeds_when_another_active_admin_remains()
    {
        await using var db = IdentityTestHarness.NewDb();
        var target = AddAdmin(db, "target@example.com");
        AddAdmin(db, "survivor@example.com");
        var lockedOut = target;
        lockedOut.FailedLoginCount = 3;
        lockedOut.LockoutExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var transaction = new CountingRequestTransaction();
        var notifications = new RecordingNotificationApi();
        var handler = new SuspendAccountantHandler(db, Permissions(audit), notifications, transaction, audit);

        var result = await handler.Handle(
            new AccountIdRequestDto { UserAccountId = target.Id }, Admin(), default);

        Assert.Equal(AccountStatus.Suspended, result.Status);
        Assert.Equal(1, transaction.Commits);

        // Suspended already blocks login, so the lockout timestamp is state that has outlived its reason.
        Assert.Null(target.LockoutExpiresAt);
        Assert.Equal(0, target.FailedLoginCount);

        Assert.Equal(NotificationEvents.AccountSuspended, Assert.Single(notifications.Requests).EventKind);
        Assert.Single(audit.WithAction(AuditActions.AccountSuspended));
    }

    [Fact]
    public async Task Suspending_an_already_suspended_account_is_422()
    {
        await using var db = IdentityTestHarness.NewDb();
        var target = Add(db, IdentityTestHarness.NewAccount(
            email: "target@example.com", status: AccountStatus.Suspended));
        AddAdmin(db, "survivor@example.com");
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new SuspendAccountantHandler(
            db, Permissions(audit), new RecordingNotificationApi(), new CountingRequestTransaction(), audit);

        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new AccountIdRequestDto { UserAccountId = target.Id }, Admin(), default));

        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task A_customer_side_account_is_a_404_from_the_accountant_endpoints()
    {
        await using var db = IdentityTestHarness.NewDb();
        var employee = Add(db, IdentityTestHarness.NewAccount(
            email: "employee@customer.example.com", role: UserRole.Employee,
            customerId: Guid.NewGuid(), employeeId: Guid.NewGuid()));
        AddAdmin(db, "survivor@example.com");
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new SuspendAccountantHandler(
            db, Permissions(audit), new RecordingNotificationApi(), new CountingRequestTransaction(), audit);

        // The role filter is in the LOOKUP, so an Employee id is indistinguishable from a nonexistent one.
        // A 403 here would confirm to an Accountant Admin that some specific id belongs to a Customer-side
        // account; more importantly, these endpoints simply do not govern those accounts.
        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new AccountIdRequestDto { UserAccountId = employee.Id }, Admin(), default));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal(AccountStatus.Active, employee.Status);
    }

    // --- Reactivate ---

    [Fact]
    public async Task Reactivating_a_suspended_account_restores_it()
    {
        await using var db = IdentityTestHarness.NewDb();
        var target = Add(db, IdentityTestHarness.NewAccount(
            email: "target@example.com", status: AccountStatus.Suspended));
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var transaction = new CountingRequestTransaction();
        var handler = new ReactivateAccountantHandler(db, Permissions(audit), transaction, audit);

        var result = await handler.Handle(
            new AccountIdRequestDto { UserAccountId = target.Id }, Admin(), default);

        Assert.Equal(AccountStatus.Active, result.Status);
        Assert.Equal(1, transaction.Commits);
        Assert.Single(audit.WithAction(AuditActions.AccountReactivated));
    }

    [Fact]
    public async Task An_invited_account_cannot_be_reactivated_into_existence()
    {
        await using var db = IdentityTestHarness.NewDb();
        var invited = Add(db, IdentityTestHarness.NewAccount(
            email: "invited@example.com", status: AccountStatus.Invited, password: null));
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new ReactivateAccountantHandler(
            db, Permissions(audit), new CountingRequestTransaction(), audit);

        // Invited means password_hash IS NULL. Flipping the status to Active would either violate
        // ck_user_accounts_status or produce an Active account with no password -- one that can never
        // authenticate and that the reset flow will refuse to help, because it looks fine from outside.
        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new AccountIdRequestDto { UserAccountId = invited.Id }, Admin(), default));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(AccountStatus.Invited, invited.Status);

        // And the message says to re-send the invitation rather than repeating the generic "not
        // suspended" -- this is the one case where the fix is a different action, so saying so is worth
        // the extra branch.
        Assert.Contains("invit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reactivating_an_active_account_is_422()
    {
        await using var db = IdentityTestHarness.NewDb();
        var target = Add(db, IdentityTestHarness.NewAccount(email: "target@example.com"));
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new ReactivateAccountantHandler(
            db, Permissions(audit), new CountingRequestTransaction(), audit);

        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new AccountIdRequestDto { UserAccountId = target.Id }, Admin(), default));

        Assert.Equal(422, exception.StatusCode);
    }

    // --- Promote and demote ---

    [Fact]
    public async Task Promotion_makes_an_accountant_user_an_admin()
    {
        await using var db = IdentityTestHarness.NewDb();
        var target = Add(db, IdentityTestHarness.NewAccount(email: "target@example.com"));
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new PromoteAccountantHandler(
            db, Permissions(audit), new CountingRequestTransaction(), audit);

        var result = await handler.Handle(
            new AccountIdRequestDto { UserAccountId = target.Id }, Admin(), default);

        Assert.Equal(UserRole.AccountantAdmin, result.Role);
        Assert.Single(audit.WithAction(AuditActions.AccountantPromoted));
    }

    [Fact]
    public async Task A_suspended_account_can_still_be_promoted()
    {
        await using var db = IdentityTestHarness.NewDb();
        var target = Add(db, IdentityTestHarness.NewAccount(
            email: "target@example.com", status: AccountStatus.Suspended));
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new PromoteAccountantHandler(
            db, Permissions(audit), new CountingRequestTransaction(), audit);

        // Deliberately allowed. Role and status are independent axes: a role is what someone may do once
        // they can act at all, and Suspended already stops them acting. Coupling them would mean an Admin
        // has to reactivate someone before they can fix their role, which is the wrong order for
        // onboarding somebody who is not starting until Monday.
        var result = await handler.Handle(
            new AccountIdRequestDto { UserAccountId = target.Id }, Admin(), default);

        Assert.Equal(UserRole.AccountantAdmin, result.Role);
        Assert.Equal(AccountStatus.Suspended, result.Status);
    }

    [Fact]
    public async Task Promoting_an_existing_admin_is_422()
    {
        await using var db = IdentityTestHarness.NewDb();
        var target = AddAdmin(db, "target@example.com");
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new PromoteAccountantHandler(
            db, Permissions(audit), new CountingRequestTransaction(), audit);

        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new AccountIdRequestDto { UserAccountId = target.Id }, Admin(), default));

        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_demote_themselves()
    {
        await using var db = IdentityTestHarness.NewDb();
        var self = AddAdmin(db);
        AddAdmin(db, "other@example.com");   // again, a second Admin, so the invariant would not fire
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new DemoteAccountantHandler(
            db, Permissions(audit), new CountingRequestTransaction(), audit);

        // RequireNotSelf runs before anything else. With a second Admin present the last-Admin invariant
        // is satisfied, so without this check the call would succeed and silently strip the caller's own
        // access -- while their cookie still says AccountantAdmin, so the UI keeps offering buttons that
        // now 403.
        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new AccountIdRequestDto { UserAccountId = self.Id }, Admin(self.Id), default));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(UserRole.AccountantAdmin, self.Role);
    }

    [Fact]
    public async Task Demoting_the_last_active_admin_is_refused_and_never_committed()
    {
        await using var db = IdentityTestHarness.NewDb();
        var onlyAdmin = AddAdmin(db, "target@example.com");
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var transaction = new CountingRequestTransaction();
        var handler = new DemoteAccountantHandler(db, Permissions(audit), transaction, audit);

        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new AccountIdRequestDto { UserAccountId = onlyAdmin.Id }, Admin(), default));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(0, transaction.Commits);
        Assert.True(transaction.RolledBack);
    }

    [Fact]
    public async Task Demoting_succeeds_when_another_active_admin_remains()
    {
        await using var db = IdentityTestHarness.NewDb();
        var target = AddAdmin(db, "target@example.com");
        AddAdmin(db, "survivor@example.com");
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var transaction = new CountingRequestTransaction();
        var handler = new DemoteAccountantHandler(db, Permissions(audit), transaction, audit);

        var result = await handler.Handle(
            new AccountIdRequestDto { UserAccountId = target.Id }, Admin(), default);

        Assert.Equal(UserRole.AccountantUser, result.Role);
        Assert.Equal(1, transaction.Commits);
    }

    [Fact]
    public async Task A_suspended_admin_does_not_count_towards_the_last_admin_invariant()
    {
        await using var db = IdentityTestHarness.NewDb();
        var target = AddAdmin(db, "target@example.com");
        Add(db, IdentityTestHarness.NewAccount(
            email: "suspended-admin@example.com", role: UserRole.AccountantAdmin,
            status: AccountStatus.Suspended));
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new DemoteAccountantHandler(
            db, Permissions(audit), new CountingRequestTransaction(), audit);

        // The invariant counts ACTIVE Admins. A suspended one cannot log in, so counting it would let the
        // last usable Admin be demoted while the check reports everything is fine.
        await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new AccountIdRequestDto { UserAccountId = target.Id }, Admin(), default));
    }

    // --- Permissions ---

    [Fact]
    public async Task An_accountant_user_cannot_reach_any_of_the_four_management_actions()
    {
        var staff = Staff();

        foreach (var action in new[]
                 { "SuspendAccountant", "ReactivateAccountant", "PromoteAccountant", "DemoteAccountant" })
        {
            await using var db = IdentityTestHarness.NewDb();
            var audit = new RecordingAuditApi();
            var permissions = Permissions(audit);

            var exception = await Assert.ThrowsAsync<AppException>(() =>
                permissions.RequireAsync(staff, action, ct: default));

            Assert.Equal(403, exception.StatusCode);
            Assert.Single(audit.WithAction(AuditActions.PermissionDenied));
        }
    }

    [Fact]
    public async Task Customer_side_roles_cannot_reach_the_accountant_list()
    {
        var audit = new RecordingAuditApi();
        var permissions = Permissions(audit);

        foreach (var role in new[] { UserRole.CustomerAdmin, UserRole.Employee })
        {
            var user = new CurrentUser(Guid.NewGuid().ToString(), role, Guid.NewGuid());

            // Who works at the accounting firm is not a Customer's business, and the list is the one
            // Identity read an Accountant User is allowed -- so it is the one most likely to be
            // over-granted by accident.
            var exception = await Assert.ThrowsAsync<AppException>(() =>
                permissions.RequireAsync(user, "ListAccountants", ct: default));

            Assert.Equal(403, exception.StatusCode);
        }
    }

    [Fact]
    public void The_catalogue_declares_exactly_the_six_expected_actions()
    {
        var catalogue = new IdentityActionCatalogue();

        // Pinned as a set. A new action added here without a matching RequireAsync call is an endpoint
        // nobody guards; an action renamed here without renaming the call site is an action nobody can
        // perform, and neither shows up as a compile error.
        Assert.Equal(
            [
                "DemoteAccountant", "InviteAccountant", "ListAccountants",
                "PromoteAccountant", "ReactivateAccountant", "SuspendAccountant"
            ],
            catalogue.Actions.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());

        // Only an Admin may change the shape of the Office. ListAccountants is the sole exception.
        foreach (var (action, roles) in catalogue.Actions)
        {
            Assert.NotEmpty(roles);
            if (action != "ListAccountants")
                Assert.Equal([UserRole.AccountantAdmin], roles);
        }
    }

    // --- Invite ---

    [Fact]
    public async Task Inviting_an_accountant_creates_a_passwordless_invited_account_and_a_token()
    {
        await using var db = IdentityTestHarness.NewDb();
        var audit = new RecordingAuditApi();
        var notifications = new RecordingNotificationApi();
        var transaction = new CountingRequestTransaction();
        var handler = new InviteAccountantHandler(
            db, Permissions(audit), IdentityTestHarness.Tokens, IdentityTestHarness.Links,
            notifications, transaction, audit);

        var result = await handler.Handle(new InviteAccountantRequestDto
        {
            Email = "  New.Person@Example.COM ",
            DisplayName = "New Person",
            Role = UserRole.AccountantUser
        }, Admin(), default);

        var created = await db.UserAccounts.SingleAsync();

        // Invited, with no password. The invitee sets their own -- an Admin who types a temporary password
        // knows that password, and the audit log cannot tell later actions by the invitee from actions by
        // the Admin who invited them.
        Assert.Equal(AccountStatus.Invited, created.Status);
        Assert.Null(created.PasswordHash);
        Assert.False(created.MustChangePassword);

        // Both scope ids null: an Accountant sees every Customer, so a customer_id here would silently
        // scope them to one.
        Assert.Null(created.CustomerId);
        Assert.Null(created.EmployeeId);

        // Normalized for lookup, original preserved for display and delivery.
        Assert.Equal("new.person@example.com", created.NormalizedLoginEmail);
        Assert.Equal("New.Person@Example.COM", created.LoginEmail);

        var token = await db.Tokens.SingleAsync();
        Assert.Equal(TokenPurpose.Invitation, token.Purpose);
        Assert.True(token.ExpiresAt > DateTimeOffset.UtcNow.AddDays(6));

        var notification = Assert.Single(notifications.Requests);
        Assert.Equal(NotificationEvents.Invited, notification.EventKind);
        Assert.Contains("https://app.test/accept-invitation?token=", notification.EmailBody);

        Assert.Equal(1, transaction.Commits);
        Assert.Single(audit.WithAction(AuditActions.AccountantAccountCreated));
        Assert.Equal(result.Id, created.Id);
    }

    [Fact]
    public async Task Inviting_a_customer_side_role_through_this_endpoint_is_422()
    {
        foreach (var role in new[] { UserRole.CustomerAdmin, UserRole.Employee })
        {
            await using var db = IdentityTestHarness.NewDb();
            var audit = new RecordingAuditApi();
            var handler = new InviteAccountantHandler(
                db, Permissions(audit), IdentityTestHarness.Tokens, IdentityTestHarness.Links,
                new RecordingNotificationApi(), new CountingRequestTransaction(), audit);

            // 422, not a silent coercion to AccountantUser. Customer-side accounts need a customer_id and
            // an employee_id that this endpoint has no way to supply; creating one without them yields an
            // account whose scope checks all compare against null.
            var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
                new InviteAccountantRequestDto
                {
                    Email = "someone@example.com", DisplayName = "Someone", Role = role
                }, Admin(), default));

            Assert.Equal(422, exception.StatusCode);
            Assert.Equal(0, await db.UserAccounts.CountAsync());
        }
    }

    [Fact]
    public async Task A_duplicate_email_is_409_and_here_the_address_is_disclosed()
    {
        await using var db = IdentityTestHarness.NewDb();
        Add(db, IdentityTestHarness.NewAccount(email: "taken@example.com"));
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new InviteAccountantHandler(
            db, Permissions(audit), IdentityTestHarness.Tokens, IdentityTestHarness.Links,
            new RecordingNotificationApi(), new CountingRequestTransaction(), audit);

        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new InviteAccountantRequestDto
            {
                Email = "TAKEN@example.com", DisplayName = "Someone", Role = UserRole.AccountantUser
            }, Admin(), default));

        Assert.Equal(409, exception.StatusCode);

        // Unlike login and password reset, naming the address is correct here: the caller is an
        // AccountantAdmin who can already list every account with its email. Hiding it would only make a
        // genuine typo unfixable, and the enumeration it would prevent is already permitted.
        Assert.Contains("taken@example.com", exception.Message, StringComparison.OrdinalIgnoreCase);

        // Matched on the NORMALIZED column, so case is not a way around the uniqueness rule.
        Assert.Equal(1, await db.UserAccounts.CountAsync());
    }

    // --- List ---

    [Fact]
    public async Task An_admin_sees_full_detail_and_an_accountant_user_sees_only_names()
    {
        await using var db = IdentityTestHarness.NewDb();
        Add(db, IdentityTestHarness.NewAccount(email: "zoe@example.com"));
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new ListAccountantsHandler(db, Permissions(audit));
        var request = new ListAccountantsRequestDto();

        var adminResult = Assert.IsType<PaginatedResponse<AccountantDetailDto>>(
            await handler.Handle(request, Admin(), default));
        Assert.Equal("zoe@example.com", Assert.Single(adminResult.Items).LoginEmail);

        // A DIFFERENT TYPE, not a nulled-out field. The handler returns object, so System.Text.Json
        // serialises the runtime type -- an AccountantUser's response has exactly two keys and no
        // loginEmail at all. A shared DTO with a null email would still ship the property name, and one
        // handler that forgot to null it would leak every address in the firm.
        var staffResult = Assert.IsType<PaginatedResponse<AccountantSummaryDto>>(
            await handler.Handle(request, Staff(), default));
        Assert.Equal("Alice Example", Assert.Single(staffResult.Items).DisplayName);

        Assert.DoesNotContain(typeof(AccountantSummaryDto).GetProperties(),
            property => property.Name.Contains("Email", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_list_includes_suspended_and_invited_accounts()
    {
        await using var db = IdentityTestHarness.NewDb();
        Add(db, IdentityTestHarness.NewAccount(email: "active@example.com"));
        Add(db, IdentityTestHarness.NewAccount(email: "suspended@example.com", status: AccountStatus.Suspended));
        Add(db, IdentityTestHarness.NewAccount(
            email: "invited@example.com", status: AccountStatus.Invited, password: null));
        // A Customer-side account, which must NOT appear -- this is the accountant list.
        Add(db, IdentityTestHarness.NewAccount(
            email: "employee@customer.example.com", role: UserRole.Employee,
            customerId: Guid.NewGuid(), employeeId: Guid.NewGuid()));
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new ListAccountantsHandler(db, Permissions(audit));

        var result = Assert.IsType<PaginatedResponse<AccountantDetailDto>>(
            await handler.Handle(new ListAccountantsRequestDto(), Admin(), default));

        // No status filter. An Admin cannot reactivate somebody the list refuses to show them, and
        // "where did that person go" is the bug report that follows from hiding suspended rows.
        Assert.Equal(3, result.TotalCount);
        Assert.DoesNotContain(result.Items, item => item.LoginEmail.Contains("customer.example.com"));
    }

    [Fact]
    public async Task The_list_is_ordered_before_it_is_paged()
    {
        await using var db = IdentityTestHarness.NewDb();
        foreach (var name in new[] { "Carol", "alice", "Bob" })
            Add(db, new UserAccount
            {
                Id = Guid.NewGuid(),
                LoginEmail = $"{name}@example.com",
                NormalizedLoginEmail = $"{name.ToLowerInvariant()}@example.com",
                PasswordHash = IdentityTestHarness.Passwords.Hash("whatever-password"),
                DisplayName = name,
                Role = UserRole.AccountantUser,
                Status = AccountStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new ListAccountantsHandler(db, Permissions(audit));

        var firstPage = Assert.IsType<PaginatedResponse<AccountantDetailDto>>(
            await handler.Handle(new ListAccountantsRequestDto { PageSize = 2 }, Admin(), default));
        var secondPage = Assert.IsType<PaginatedResponse<AccountantDetailDto>>(
            await handler.Handle(
                new ListAccountantsRequestDto { PageNumber = 2, PageSize = 2 }, Admin(), default));

        // ORDER BY before OFFSET, or the same row can appear on two pages and another on none -- and it
        // happens intermittently, which reads as data loss rather than a missing sort.
        Assert.Equal(["alice", "Bob"], firstPage.Items.Select(item => item.DisplayName).ToArray());
        Assert.Equal(["Carol"], secondPage.Items.Select(item => item.DisplayName).ToArray());
        Assert.Equal(3, firstPage.TotalCount);
    }
}
