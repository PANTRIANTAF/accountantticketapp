using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccountantApp.Tests.Employees;

internal static class EmployeesTestHarness
{
    public static EmployeesDbContext NewDb() =>
        new(new DbContextOptionsBuilder<EmployeesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public static PermissionChecker Permissions(IAuditApi audit) => new(
        [new EmployeesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);

    /// <summary>
    /// An Accountant session. No CustomerId, because an Accountant is not Customer-scoped, and the id is
    /// an account id -- the same thing CurrentUserFactory puts there.
    /// </summary>
    public static CurrentUser Accountant(UserRole role = UserRole.AccountantAdmin) =>
        new(Guid.NewGuid().ToString(), role);

    /// <summary>
    /// A Customer-side session built from the account id of a real Employee row, which is the only way
    /// the self checks and the second scope filter can be exercised honestly. Handing a CurrentUser whose
    /// Id happens to be the EMPLOYEE id would test the bug (comparing an account id to an Employee id)
    /// rather than the rule.
    /// </summary>
    public static CurrentUser SessionFor(Employee employee, UserRole role) =>
        new(employee.UserAccountId!.Value.ToString(), role, employee.CustomerId);

    public static Employee EmployeeEntity(
        Guid customerId,
        string given = "Maria",
        string family = "Papadopoulou",
        string? workEmail = "maria@acme.example",
        Guid? userAccountId = null,
        string status = EmployeeStatus.Active)
    {
        var now = DateTimeOffset.UtcNow;
        return new Employee
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            GivenName = given,
            FamilyName = family,
            JobTitle = "Bookkeeper",
            WorkEmail = workEmail,
            NormalizedWorkEmail = workEmail?.ToUpperInvariant(),
            ContactPhone = "+302100000000",
            TaxIdentificationNumber = "TIN-123456",
            SocialSecurityNumber = "SSN-987654",
            EmploymentStartDate = new DateOnly(2026, 1, 5),
            EmploymentEndDate = status == EmployeeStatus.Departed ? new DateOnly(2026, 6, 30) : null,
            DepartedAt = status == EmployeeStatus.Departed ? now : null,
            UserAccountId = userAccountId,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}

internal sealed class TestAuditApi : IAuditApi
{
    public List<AuditEntry> Entries { get; } = [];

    public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        // Mirrors the real AuditApi's validation, which is a RUNTIME check against the reflected constant
        // set. Without it here, a handler naming an action that does not exist passes every unit test and
        // throws on the first real request.
        Assert.Contains(entry.Action, AuditActions.All);
        Assert.Contains(entry.TargetKind, AuditTargets.All);
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task LogUnauthenticatedAsync(
        string actorIdentifier, AuditEntry entry, CancellationToken cancellationToken = default) =>
        LogAsync(entry, cancellationToken);

    public IEnumerable<AuditEntry> WithAction(string action) =>
        Entries.Where(entry => entry.Action == action);
}

/// <summary>
/// An in-memory account store standing in for the Identity slice. It enforces the same STRUCTURAL rules
/// the real IIdentityApi does -- throwing on an Accountant role, on a duplicate login email, and treating
/// suspend/reactivate as idempotent -- because those are the behaviours this slice's handlers are built
/// against, and a permissive double would make several of the tests below vacuous.
/// </summary>
internal sealed class FakeIdentityApi : IIdentityApi
{
    private readonly Dictionary<Guid, AccountSummary> _accounts = new();
    private readonly Dictionary<Guid, Guid> _byEmployee = new();

    /// <summary>
    /// Accounts with no password hash -- an invitation that was never accepted. AccountSummary deliberately
    /// carries no hash (Identity plan section 9.1 rule 2), so the double tracks the fact separately. It has
    /// to track it at all because ReactivateAccountAsync's behaviour depends on it: a hashless account comes
    /// back as Invited, not Active (rule 14).
    /// </summary>
    private readonly HashSet<Guid> _hashless = [];

    public List<Guid> SuspendCalls { get; } = [];
    public List<Guid> ReactivateCalls { get; } = [];
    public List<(Guid AccountId, UserRole Role)> RoleCalls { get; } = [];
    public List<(Guid AccountId, string LoginEmail)> LoginEmailCalls { get; } = [];
    public int FindManyCallCount { get; private set; }

    /// <summary>Set to make InviteEmployeeAccountAsync fail, for the rollback tests.</summary>
    public Exception? InviteFailure { get; set; }

    public Guid Seed(
        Guid employeeId,
        UserRole role = UserRole.Employee,
        string status = "Active",
        string email = "seeded@acme.example")
    {
        var id = Guid.NewGuid();
        _accounts[id] = new AccountSummary(id, "Seeded Person", email, role, status);
        _byEmployee[employeeId] = id;

        // An Invited account has been sent a link and has set no password yet. Every other seeded status
        // implies somebody got in at least once.
        if (status == "Invited")
            _hashless.Add(id);

        return id;
    }

    public AccountSummary Account(Guid accountId) => _accounts[accountId];

    public Task<AccountSummary?> FindAsync(Guid userAccountId, CancellationToken ct = default) =>
        Task.FromResult(_accounts.TryGetValue(userAccountId, out var account) ? account : null);

    public Task<IReadOnlyDictionary<Guid, AccountSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> userAccountIds, CancellationToken ct = default)
    {
        if (userAccountIds.Count > 500)
            throw new InvalidOperationException("At most 500 account ids may be requested.");

        FindManyCallCount++;
        return Task.FromResult<IReadOnlyDictionary<Guid, AccountSummary>>(
            userAccountIds.Distinct()
                .Where(_accounts.ContainsKey)
                .ToDictionary(id => id, id => _accounts[id]));
    }

    public Task<bool> IsActiveAsync(Guid userAccountId, CancellationToken ct = default) =>
        Task.FromResult(_accounts.TryGetValue(userAccountId, out var account) && account.IsActive);

    public Task<AccountSummary?> FindByEmployeeAsync(Guid employeeId, CancellationToken ct = default) =>
        Task.FromResult(_byEmployee.TryGetValue(employeeId, out var accountId)
            ? _accounts[accountId]
            : null);

    /// <summary>
    /// The seeded Accountant accounts, filtered by role and — when asked — by status.
    ///
    /// THIS USED TO RETURN AN EMPTY LIST unconditionally, and an empty list is not a neutral stub here.
    /// Every handler that notifies the Office guards with <c>if (office.Count > 0)</c>, so a permanently
    /// empty directory made that branch dead in every test that ran through it: "the whole Office is
    /// notified on submission" was asserted nowhere and could not fail. A handler that notified nobody,
    /// or one Accountant, or the Customer instead, passed identically.
    ///
    /// <paramref name="activeOnly"/> is honoured rather than ignored, because a Suspended Accountant
    /// receiving work notifications is the difference the parameter exists to express.
    /// </summary>
    public Task<IReadOnlyList<AccountSummary>> ListAccountantsAsync(
        bool activeOnly = true, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AccountSummary>>(
        [
            .. _accounts.Values
                .Where(account => account.Role
                    is UserRole.AccountantAdmin or UserRole.AccountantUser)
                .Where(account => !activeOnly || account.IsActive)
        ]);

    public Task<Guid> InviteEmployeeAccountAsync(
        InviteEmployeeAccount request, CancellationToken ct = default)
    {
        if (InviteFailure is not null)
            return Task.FromException<Guid>(InviteFailure);

        if (request.Role is UserRole.AccountantAdmin or UserRole.AccountantUser)
            throw new InvalidOperationException("An Employee account cannot hold an Accountant role.");
        if (request.CustomerId == Guid.Empty || request.EmployeeId == Guid.Empty)
            throw new InvalidOperationException("Both ids are required.");

        // Globally unique, across every Customer -- which is what makes the "already a login at ANOTHER
        // Customer" case a 409 rather than a success.
        if (_accounts.Values.Any(account =>
                string.Equals(account.LoginEmail, request.LoginEmail, StringComparison.OrdinalIgnoreCase)))
            throw new AppException("An account with that email address already exists.", 409);

        var id = Guid.NewGuid();
        _accounts[id] = new AccountSummary(
            id, request.DisplayName, request.LoginEmail, request.Role, "Invited");
        _byEmployee[request.EmployeeId] = id;
        _hashless.Add(id);
        return Task.FromResult(id);
    }

    /// <summary>Marks an account as having accepted its invitation, so it has a password hash.</summary>
    public void AcceptInvitation(Guid accountId)
    {
        _hashless.Remove(accountId);
        if (_accounts.TryGetValue(accountId, out var account))
            _accounts[accountId] = account with { Status = "Active" };
    }

    // Idempotent by contract: suspending an already-suspended account is a no-op, not an error, or a
    // departure could not be recorded for somebody whose access was already revoked.
    public Task SuspendAccountAsync(Guid userAccountId, CancellationToken ct = default)
    {
        SuspendCalls.Add(userAccountId);
        if (_accounts.TryGetValue(userAccountId, out var account))
            _accounts[userAccountId] = account with { Status = "Suspended" };
        return Task.CompletedTask;
    }

    // Restores Invited rather than Active when the account never set a password (Identity plan section 9.1
    // rule 14). Getting this wrong in the double would hide the real defect it was written for: a
    // never-accepted invitee reinstated as Active passes every status check and fails every login.
    public Task ReactivateAccountAsync(Guid userAccountId, CancellationToken ct = default)
    {
        ReactivateCalls.Add(userAccountId);
        if (_accounts.TryGetValue(userAccountId, out var account))
            _accounts[userAccountId] = account with
            {
                Status = _hashless.Contains(userAccountId) ? "Invited" : "Active"
            };
        return Task.CompletedTask;
    }

    public Task SetCustomerSideRoleAsync(
        Guid userAccountId, UserRole role, CancellationToken ct = default)
    {
        if (role is UserRole.AccountantAdmin or UserRole.AccountantUser)
            throw new InvalidOperationException("Not a Customer-side role.");

        RoleCalls.Add((userAccountId, role));
        if (_accounts.TryGetValue(userAccountId, out var account))
            _accounts[userAccountId] = account with { Role = role };
        return Task.CompletedTask;
    }

    /// <summary>
    /// The structural rules the real implementation enforces, and no others: unknown account is a 404, an
    /// Accountant target throws, an unchanged address is a no-op, and a duplicate is a 409 -- system-wide,
    /// like the invitation path above, because normalized_login_email is unique across every Customer.
    ///
    /// It writes the login email and NOTHING else. A double that also touched the status or the hash would
    /// make the handler's "it changes only the address" tests pass for the wrong reason.
    /// </summary>
    public Task ChangeLoginEmailAsync(
        Guid userAccountId, string loginEmail, CancellationToken ct = default)
    {
        if (!_accounts.TryGetValue(userAccountId, out var account))
            throw new AppException("Account not found.", 404);

        if (account.Role is UserRole.AccountantAdmin or UserRole.AccountantUser)
            throw new InvalidOperationException(
                "An Accountant's login email is not changed through this method.");

        if (string.IsNullOrWhiteSpace(loginEmail))
            throw new AppException("Login email is required.", 422);

        var trimmed = loginEmail.Trim();
        if (string.Equals(account.LoginEmail, trimmed, StringComparison.Ordinal))
            return Task.CompletedTask;

        if (_accounts.Values.Any(other =>
                other.Id != userAccountId
                && string.Equals(other.LoginEmail, trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new AppException("An account with that email address already exists.", 409);

        LoginEmailCalls.Add((userAccountId, trimmed));
        _accounts[userAccountId] = account with { LoginEmail = trimmed };
        return Task.CompletedTask;
    }
}

internal sealed class FakeCustomerApi : ICustomerApi
{
    private readonly Dictionary<Guid, string> _statuses = [];

    public List<CreateCustomer> Created { get; } = [];
    public List<Guid> IsActiveCalls { get; } = [];

    /// <summary>The id CreateAsync will return, so a test can assert the Employee row points at it.</summary>
    public Guid NextCreatedId { get; set; } = Guid.NewGuid();

    public Guid AddActive() => Add("Active");

    /// <summary>
    /// A Customer that EXISTS and is not Active, which is a different case from an unknown id even
    /// though both are a 422 -- an implementation that answered IsActiveAsync from FindAsync is not
    /// null would pass the unknown-id test and let a suspended Customer gain Employees.
    /// </summary>
    public Guid AddSuspended() => Add("Suspended");

    private Guid Add(string status)
    {
        var id = Guid.NewGuid();
        _statuses[id] = status;
        return id;
    }

    public Task<CustomerSummary?> FindAsync(Guid customerId, CancellationToken ct = default) =>
        Task.FromResult<CustomerSummary?>(_statuses.TryGetValue(customerId, out var status)
            ? new CustomerSummary(customerId, "Acme", null, status)
            : null);

    public Task<bool> IsActiveAsync(Guid customerId, CancellationToken ct = default)
    {
        IsActiveCalls.Add(customerId);
        return Task.FromResult(_statuses.TryGetValue(customerId, out var status) && status == "Active");
    }

    /// <summary>
    /// The batch read, answering CONSISTENTLY with <see cref="FindAsync"/> — a seeded id resolves, an
    /// unseeded one is simply absent from the dictionary.
    ///
    /// IT USED TO RETURN AN EMPTY DICTIONARY unconditionally, which made the two methods of this one
    /// double disagree about the same id: `FindAsync` resolved it and `FindManyAsync` did not. Callers
    /// that batch-resolve a display name treat "absent" as "no name", so the name silently never appeared
    /// and no assertion about it could fail. A double that contradicts itself tests the caller's
    /// null-handling and nothing else.
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, CustomerSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> customerIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, CustomerSummary>>(
            customerIds.Distinct()
                .Where(_statuses.ContainsKey)
                .ToDictionary(id => id, id => new CustomerSummary(id, "Acme", null, _statuses[id])));

    public Task<Guid> CreateAsync(CreateCustomer request, CancellationToken ct = default)
    {
        Created.Add(request);
        _statuses[NextCreatedId] = "Active";
        return Task.FromResult(NextCreatedId);
    }
}
