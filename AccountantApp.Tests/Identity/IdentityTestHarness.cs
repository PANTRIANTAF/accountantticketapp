using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AccountantApp.Tests.Identity;

internal static class IdentityTestHarness
{
    public static IdentityDbContext NewDb() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public static IPasswordHashing Passwords { get; } = new PasswordHashing();

    public static ITokenIssuing Tokens { get; } = new TokenIssuing();

    public static TokenLinks Links { get; } = new(
        Options.Create(new IdentityLinkOptions { BaseUrl = "https://app.test" }));

    /// <summary>
    /// An HttpContext that can actually complete SignInAsync/SignOutAsync. Without the authentication
    /// services in RequestServices those calls throw, so a success-path login test cannot run at all.
    /// </summary>
    public static DefaultHttpContext NewHttpContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();

        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    public static UserAccount NewAccount(
        string email = "alice@example.com",
        UserRole role = UserRole.AccountantUser,
        string status = AccountStatus.Active,
        string? password = "correct-horse-battery",
        Guid? customerId = null,
        Guid? employeeId = null,
        bool mustChangePassword = false)
    {
        return new UserAccount
        {
            Id = Guid.NewGuid(),
            LoginEmail = email,
            NormalizedLoginEmail = EmailNormalization.Normalize(email),
            PasswordHash = password is null ? null : Passwords.Hash(password),
            DisplayName = "Alice Example",
            Role = role,
            Status = status,
            CustomerId = customerId,
            EmployeeId = employeeId,
            MustChangePassword = mustChangePassword,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Records both the entries and whether each arrived through the authenticated or unauthenticated
/// method, because "the login failure path must use LogUnauthenticatedAsync" is one of the rules under
/// test -- LogAsync there would resolve a CurrentUser that does not exist.
/// </summary>
internal sealed class RecordingAuditApi : IAuditApi
{
    public List<AuditEntry> Entries { get; } = [];
    public List<string?> Actors { get; } = [];

    public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add(entry);
        Actors.Add(null);
        return Task.CompletedTask;
    }

    public Task LogUnauthenticatedAsync(
        string actorIdentifier, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add(entry);
        Actors.Add(actorIdentifier);
        return Task.CompletedTask;
    }

    public IEnumerable<AuditEntry> WithAction(string action) =>
        Entries.Where(entry => entry.Action == action);
}

/// <summary>
/// Counts commits and rollbacks. This is what makes the commit-before-throw rule testable: the failure
/// path writes a FailedLoginCount increment, and if it throws without committing, the real
/// RequestTransaction rolls it back and brute-force protection silently does not exist while every
/// status-code assertion still passes.
/// </summary>
internal sealed class CountingRequestTransaction : IRequestTransaction
{
    public int Commits { get; private set; }
    public int Disposals { get; private set; }

    /// <summary>True when the scope was disposed without a commit -- i.e. the work was rolled back.</summary>
    public bool RolledBack => Disposals > 0 && Commits == 0;

    public Task<IAsyncDisposable> BeginAsync(DbContext context, CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable>(new Scope(this));

    public Task EnlistAsync(DbContext context, CancellationToken ct) => Task.CompletedTask;

    public Task CommitAsync(CancellationToken ct)
    {
        Commits++;
        return Task.CompletedTask;
    }

    private sealed class Scope(CountingRequestTransaction owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owner.Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class StubCustomerApi : ICustomerApi
{
    public bool ActiveResult { get; set; } = true;
    public List<Guid> IsActiveCalls { get; } = [];

    public Task<CustomerSummary?> FindAsync(Guid customerId, CancellationToken ct = default) =>
        Task.FromResult<CustomerSummary?>(new CustomerSummary(customerId, "Acme", null, "Active"));

    public Task<bool> IsActiveAsync(Guid customerId, CancellationToken ct = default)
    {
        IsActiveCalls.Add(customerId);
        return Task.FromResult(ActiveResult);
    }

    public Task<IReadOnlyDictionary<Guid, CustomerSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> customerIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, CustomerSummary>>(
            new Dictionary<Guid, CustomerSummary>());

    public List<CreateCustomer> Created { get; } = [];

    /// <summary>
    /// Set to make the third-party creation fail, for the onboarding-rollback tests.
    /// </summary>
    public Exception? CreateFailure { get; set; }

    public Task<Guid> CreateAsync(CreateCustomer request, CancellationToken ct = default)
    {
        if (CreateFailure is not null)
            return Task.FromException<Guid>(CreateFailure);
        Created.Add(request);
        return Task.FromResult(Guid.NewGuid());
    }
}

internal sealed class RecordingNotificationApi : INotificationApi
{
    public List<NotificationRequest> Requests { get; } = [];

    public int NotifyManyCallCount { get; private set; }

    public Task<Guid> NotifyAsync(NotificationRequest request, CancellationToken ct = default)
    {
        RequireCataloguedKind(request);
        Requests.Add(request);
        return Task.FromResult(Guid.NewGuid());
    }

    public Task<int> NotifyManyAsync(
        IReadOnlyCollection<NotificationRequest> requests, CancellationToken ct = default)
    {
        foreach (var request in requests)
            RequireCataloguedKind(request);

        NotifyManyCallCount++;
        Requests.AddRange(requests);
        return Task.FromResult(requests.Count);
    }

    public IEnumerable<NotificationRequest> OfKind(string eventKind) =>
        Requests.Where(request => request.EventKind == eventKind);

    /// <summary>
    /// Mirrors the real NotificationApi's runtime check against the reflected catalogue, for the same reason
    /// TestAuditApi mirrors AuditApi's: without it, a handler naming a kind that is not in
    /// NotificationEvents passes every unit test and throws on the first real request.
    ///
    /// It does NOT implement rule E, the self-exclusion -- the double has no current user. A test that needs
    /// "the acting Admin is not notified" belongs in the Notifications slice's own tests, against the real
    /// implementation.
    /// </summary>
    private static void RequireCataloguedKind(NotificationRequest request) =>
        Assert.Contains(request.EventKind, NotificationEvents.All);
}

internal sealed class StubHttpContextAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }
}
