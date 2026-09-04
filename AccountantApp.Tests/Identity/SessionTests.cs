using System.Security.Claims;
using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Application.Handlers;
using AccountantApp.Api.Slices.Identity.Core;
using Microsoft.AspNetCore.Http;

namespace AccountantApp.Tests.Identity;

public class SessionTests
{
    private const string CurrentPassword = "correct-horse-battery";
    private const string NewPassword = "a-brand-new-password";

    private static CurrentUser AsUser(UserAccount account) =>
        new(account.Id.ToString(), account.Role, account.CustomerId);

    // --- Claims round-trip ---

    /// <summary>
    /// SessionClaims.Build writes the cookie; CurrentUserFactory.FromPrincipal reads it back on every
    /// subsequent request. They are the two halves of one contract with a serialisation boundary in
    /// between, so a mismatch is invisible at compile time and shows up as a 401 loop after login.
    /// </summary>
    [Theory]
    [InlineData(UserRole.AccountantAdmin, false)]
    [InlineData(UserRole.AccountantUser, false)]
    [InlineData(UserRole.CustomerAdmin, true)]
    [InlineData(UserRole.Employee, true)]
    public void Every_role_survives_the_round_trip_through_the_cookie(UserRole role, bool customerScoped)
    {
        var account = IdentityTestHarness.NewAccount(
            role: role,
            customerId: customerScoped ? Guid.NewGuid() : null,
            employeeId: customerScoped ? Guid.NewGuid() : null);

        var user = CurrentUserFactory.FromPrincipal(SessionClaims.Build(account));

        Assert.Equal(account.Id.ToString(), user.Id);
        Assert.Equal(role, user.Role);
        Assert.Equal(account.CustomerId, user.CustomerId);
    }

    [Fact]
    public void An_accountant_gets_no_customer_id_claim_at_all()
    {
        var principal = SessionClaims.Build(IdentityTestHarness.NewAccount(role: UserRole.AccountantUser));

        // Absent, not empty-string and not Guid.Empty. An Accountant sees every Customer, so any value
        // here would silently scope them to one -- and Guid.Empty parses, so the factory would accept it
        // and every scope check would compare against a Customer that does not exist.
        Assert.Null(principal.FindFirst(SessionClaims.CustomerId));
        Assert.Null(CurrentUserFactory.FromPrincipal(principal).CustomerId);
    }

    [Fact]
    public void A_customer_side_cookie_without_a_customer_id_is_rejected_as_unauthenticated()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, UserRole.Employee.ToString())
            ],
            SessionClaims.Scheme);

        // Fail closed. Without a customer_id every scope check on a Customer-side request compares against
        // null, which matches nothing -- or, worse, is treated as "unscoped" and matches everything.
        var exception = Assert.Throws<AppException>(() =>
            CurrentUserFactory.FromPrincipal(new ClaimsPrincipal(identity)));

        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public void The_must_change_password_claim_is_written_as_a_parseable_string()
    {
        var flagged = SessionClaims.Build(IdentityTestHarness.NewAccount(mustChangePassword: true));
        var clear = SessionClaims.Build(IdentityTestHarness.NewAccount(mustChangePassword: false));

        // The middleware compares to the literal "true". Writing bool.ToString() would give "True" and an
        // ordinal comparison would silently never match -- the middleware would allow everything and the
        // forced password change would not exist.
        Assert.Equal("true", flagged.FindFirst(SessionClaims.MustChangePassword)?.Value);
        Assert.Equal("false", clear.FindFirst(SessionClaims.MustChangePassword)?.Value);
    }

    [Fact]
    public void A_principal_with_no_claims_is_a_401_rather_than_an_anonymous_user()
    {
        // This is what makes an endpoint that takes a CurrentUser authenticated by construction. There is
        // no app.UseAuthorization() in this application, so if the factory returned a blank CurrentUser
        // instead of throwing, every endpoint would silently accept anonymous callers.
        var exception = Assert.Throws<AppException>(() =>
            CurrentUserFactory.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity())));

        Assert.Equal(401, exception.StatusCode);
    }

    // --- GET /me ---

    [Fact]
    public async Task Me_reads_the_flag_from_the_database_not_from_the_stale_cookie()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(mustChangePassword: false);
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        // A cookie issued when the flag WAS set, e.g. an Admin reset this password after the user logged in.
        var staleUser = AsUser(account);

        var session = await new GetCurrentSessionHandler(db).Handle(staleUser, default);

        // The database is the truth. Returning the claim would leave a user who has already changed their
        // password stuck on the change-password screen until their cookie expired, with the endpoint that
        // is supposed to release them reporting they are still trapped.
        Assert.False(session.MustChangePassword);
        Assert.Equal(account.DisplayName, session.DisplayName);
    }

    [Fact]
    public async Task Me_is_401_for_a_valid_cookie_whose_account_was_suspended_or_deleted()
    {
        await using var suspendedDb = IdentityTestHarness.NewDb();
        var suspended = IdentityTestHarness.NewAccount(status: AccountStatus.Suspended);
        suspendedDb.UserAccounts.Add(suspended);
        await suspendedDb.SaveChangesAsync();

        // The cookie outlives the account state it was minted from. Without this check a suspended user
        // keeps working normally until it expires -- up to eight hours after being suspended.
        var suspendedFailure = await Assert.ThrowsAsync<AppException>(() =>
            new GetCurrentSessionHandler(suspendedDb).Handle(AsUser(suspended), default));
        Assert.Equal(401, suspendedFailure.StatusCode);

        await using var emptyDb = IdentityTestHarness.NewDb();
        var missingFailure = await Assert.ThrowsAsync<AppException>(() =>
            new GetCurrentSessionHandler(emptyDb).Handle(
                new CurrentUser(Guid.NewGuid().ToString(), UserRole.AccountantUser), default));

        // 401, not 404. The subject of this request is the caller, and "you do not exist" is an
        // authentication failure, not a missing resource.
        Assert.Equal(401, missingFailure.StatusCode);
    }

    // --- Change own password ---

    [Fact]
    public async Task Changing_your_password_clears_the_flag_and_reissues_the_cookie()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(password: CurrentPassword, mustChangePassword: true);
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var transaction = new CountingRequestTransaction();
        var httpContext = IdentityTestHarness.NewHttpContext();
        var handler = new ChangeOwnPasswordHandler(
            db, IdentityTestHarness.Passwords, transaction, audit,
            new StubHttpContextAccessor { HttpContext = httpContext });

        await handler.Handle(new ChangePasswordRequestDto
        {
            CurrentPassword = CurrentPassword,
            NewPassword = NewPassword
        }, AsUser(account), default);

        Assert.Equal(PasswordVerification.Success,
            IdentityTestHarness.Passwords.Verify(account.PasswordHash, NewPassword));
        Assert.False(account.MustChangePassword);
        Assert.Equal(1, transaction.Commits);
        Assert.Single(audit.WithAction(AuditActions.PasswordChanged));

        // The cookie carries must_change_password. Clearing the column without re-issuing it leaves the
        // claim saying "true" for the rest of the session, so the middleware keeps returning 403 to a user
        // who has just done exactly what it asked.
        Assert.NotNull(httpContext.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task A_wrong_current_password_is_401_and_does_not_change_anything()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(password: CurrentPassword);
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        var originalHash = account.PasswordHash;

        var audit = new RecordingAuditApi();
        var transaction = new CountingRequestTransaction();
        var handler = new ChangeOwnPasswordHandler(
            db, IdentityTestHarness.Passwords, transaction, audit,
            new StubHttpContextAccessor { HttpContext = IdentityTestHarness.NewHttpContext() });

        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new ChangePasswordRequestDto
            {
                CurrentPassword = "not-the-current-password",
                NewPassword = NewPassword
            }, AsUser(account), default));

        Assert.Equal(401, exception.StatusCode);
        Assert.Equal(originalHash, account.PasswordHash);

        // The denial is audited and COMMITTED. Someone probing a borrowed, unlocked session for the
        // password is exactly what this row records, and throwing without committing discards it.
        Assert.Equal(1, transaction.Commits);
        Assert.Contains(audit.Entries,
            entry => entry.Action == AuditActions.PasswordChanged && entry.Outcome == AuditOutcome.Denied);

        // No lockout increment: this is an authenticated caller, and letting a hostile page drive the
        // logged-in user's own account into a lockout is a denial of service, not a defence.
        Assert.Equal(0, account.FailedLoginCount);
        Assert.Null(account.LockoutExpiresAt);
    }

    [Fact]
    public async Task The_new_password_is_validated_before_the_current_one_is_checked()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(password: CurrentPassword);
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new ChangeOwnPasswordHandler(
            db, IdentityTestHarness.Passwords, new CountingRequestTransaction(), audit,
            new StubHttpContextAccessor { HttpContext = IdentityTestHarness.NewHttpContext() });

        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new ChangePasswordRequestDto
            {
                CurrentPassword = CurrentPassword,
                NewPassword = "short"
            }, AsUser(account), default));

        // 422 for the policy failure. Ordered this way so the person who typed both fields correctly-ish
        // gets told which field is wrong; checking the current password first would report "wrong
        // password" to someone whose real mistake was a too-short new one.
        Assert.Equal(422, exception.StatusCode);
        Assert.Empty(audit.WithAction(AuditActions.PasswordChanged));
    }

    [Fact]
    public async Task Nobody_can_change_somebody_elses_password_through_this_endpoint()
    {
        // Structural, so it is asserted on the shape of the request rather than by trying an attack: the
        // DTO has no target-user field, and the handler takes the account id from CurrentUser. There is no
        // parameter to tamper with, which is why no authorization check is needed here at all.
        Assert.DoesNotContain(typeof(ChangePasswordRequestDto).GetProperties(),
            property => property.Name.Contains("UserId", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Account", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Email", StringComparison.OrdinalIgnoreCase));

        await Task.CompletedTask;
    }

    // --- Logout ---

    [Fact]
    public async Task Logout_audits_then_clears_the_cookie_and_is_idempotent()
    {
        var audit = new RecordingAuditApi();
        var httpContext = IdentityTestHarness.NewHttpContext();
        var handler = new LogoutHandler(audit, new StubHttpContextAccessor { HttpContext = httpContext });
        var user = new CurrentUser(Guid.NewGuid().ToString(), UserRole.AccountantUser);

        var first = await handler.Handle(user, default);
        var second = await handler.Handle(user, default);

        // 200 both times. There is no session row to delete, so there is no state to conflict with -- and
        // a second logout returning 409 would break every client that calls it on a page it has already
        // left.
        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(2, audit.WithAction(AuditActions.LoggedOut).Count());
    }

    // --- The must-change-password gate ---

    private static async Task<int> InvokeMiddlewareAsync(
        string path, bool authenticated, bool mustChangePassword)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        if (authenticated)
        {
            var account = IdentityTestHarness.NewAccount(mustChangePassword: mustChangePassword);
            context.User = SessionClaims.Build(account);
        }

        var reachedTheHandler = false;
        var middleware = new MustChangePasswordMiddleware(_ =>
        {
            reachedTheHandler = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        return reachedTheHandler ? StatusCodes.Status200OK : context.Response.StatusCode;
    }

    [Theory]
    [InlineData("/api/auth/change-password")]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/me")]
    public async Task A_flagged_user_may_still_reach_the_three_allowed_paths(string path)
    {
        Assert.Equal(StatusCodes.Status200OK,
            await InvokeMiddlewareAsync(path, authenticated: true, mustChangePassword: true));
    }

    [Theory]
    [InlineData("/api/accountants/list")]
    [InlineData("/api/tickettypes/list")]
    [InlineData("/api/customers/list")]
    [InlineData("/api/auth/change-password/../../accountants/list")]
    public async Task A_flagged_user_is_403_everywhere_else(string path)
    {
        // Including the traversal-shaped path: the comparison is a whole-string equality on the already
        // normalised Request.Path, not StartsWith. A StartsWith("/api/auth/change-password") check would
        // let anything with that prefix through.
        Assert.Equal(StatusCodes.Status403Forbidden,
            await InvokeMiddlewareAsync(path, authenticated: true, mustChangePassword: true));
    }

    [Fact]
    public async Task An_unflagged_user_and_an_anonymous_caller_both_pass_through()
    {
        Assert.Equal(StatusCodes.Status200OK,
            await InvokeMiddlewareAsync("/api/accountants/list", true, mustChangePassword: false));

        // Anonymous must pass, or login itself becomes unreachable and the application cannot be entered
        // at all. This middleware is not an authentication gate.
        Assert.Equal(StatusCodes.Status200OK,
            await InvokeMiddlewareAsync("/api/auth/login", authenticated: false, mustChangePassword: false));
    }

    [Fact]
    public async Task The_allowed_paths_are_matched_case_insensitively()
    {
        // ASP.NET routing is case-insensitive, so /API/Auth/Logout reaches the logout handler. An ordinal
        // comparison here would 403 it -- the user's one escape route works or not depending on how they
        // typed the URL.
        Assert.Equal(StatusCodes.Status200OK,
            await InvokeMiddlewareAsync("/API/Auth/Logout", authenticated: true, mustChangePassword: true));
    }
}
