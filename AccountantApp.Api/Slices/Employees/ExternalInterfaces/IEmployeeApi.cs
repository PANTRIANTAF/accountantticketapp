using AccountantApp.Api.Shared.Pagination;

namespace AccountantApp.Api.Slices.Employees.ExternalInterfaces;

/// <summary>
/// What other slices see of an Employee. Never the entity itself: a caller holding a tracked Employee could
/// mutate it and save it through another slice's context, which is the exact coupling one-DbContext-per-slice
/// exists to prevent.
///
/// No tax identification number, no social-security number, no work email, no phone, no employment dates.
/// Nothing in Tickets needs them, and an ExternalInterface that carries a social-security number makes every
/// consumer a disclosure path -- the same restriction CustomerSummary has for tax numbers and AccountSummary
/// has for password hashes.
///
/// CustomerId IS here, deliberately: Tickets needs it to enforce its own Customer scope on a Ticket's
/// Subject, and it is not sensitive.
/// </summary>
public sealed record EmployeeSummary(
    Guid Id,
    Guid CustomerId,
    string GivenName,
    string FamilyName,
    string Status,
    bool HasAccount,
    Guid? UserAccountId)
{
    public bool IsActive => Status == "Active";
    public string FullName => $"{GivenName} {FamilyName}";
}

/// <summary>
/// One slice calls this: Tickets.
///
/// IT APPLIES NO SCOPE FILTER, AND THE CALLER MUST. This is the opposite of the rule inside the slice, where
/// every read goes through .WhereInCustomerScope(user). The reason is that this contract is called on behalf
/// of every role including Accountants, so a filter here would either break Accountant reads or silently
/// depend on a CurrentUser this contract does not take. A Tickets handler that passes an id it has not
/// authorized is a cross-Customer read, and nothing on this interface will stop it. ICustomerApi makes the
/// same choice for the same reason.
///
/// It is READ-ONLY, and it stays that way. There is no RegisterAsync, no DepartAsync, and no write method of
/// any kind -- unlike ICustomerApi, which gained one because a real call site needed it. A write method here
/// would be a way for Tickets to change Employee records, which no row in the authorization matrix
/// authorizes. Do not add one pre-emptively.
///
/// It caches nothing and writes no audit entries. These are reads, and IsActiveAsync is how a departure takes
/// effect in Tickets -- a status change is precisely the event a cache would hide.
/// </summary>
public interface IEmployeeApi
{
    /// <summary>Null when no such Employee exists. Applies NO scope filter -- the caller authorizes.</summary>
    Task<EmployeeSummary?> FindAsync(Guid employeeId, CancellationToken ct = default);

    /// <summary>
    /// Bulk lookup for list rendering, so a caller does not run a query per row. Missing ids are simply
    /// absent from the dictionary. Capped at 500 ids; above that it throws InvalidOperationException, matching
    /// ICustomerApi and IIdentityApi.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, EmployeeSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> employeeIds, CancellationToken ct = default);

    /// <summary>
    /// True only when the Employee exists AND is Active. This is what Tickets asks to refuse a new Ticket for
    /// a departed Subject. An unknown id is false -- never true, never a throw. Fail closed: a "?? true"
    /// anywhere in that chain lets one through.
    /// </summary>
    Task<bool> IsActiveAsync(Guid employeeId, CancellationToken ct = default);

    /// <summary>
    /// The Employee belonging to an account, or null. This is how Tickets resolves "which Employee is the
    /// caller" to compute Subject-based read access.
    /// </summary>
    Task<EmployeeSummary?> FindByAccountAsync(Guid userAccountId, CancellationToken ct = default);

    /// <summary>
    /// Active Employees of one Customer, a page at a time, for another slice's Subject picker.
    ///
    /// Deliberately NOT the same return type as ListEmployeesHandler, and deliberately not that handler:
    /// the handler serves an authorized HTTP request and returns a role-shaped DTO whose field
    /// restrictions would otherwise become something Tickets could bypass by calling this instead.
    ///
    /// PAGED, and page size is capped by PaginatedQuery.MaxPageSize like every other list in the system.
    /// It was unpaginated originally, on the reasoning that a silent cap makes a Subject un-pickable with
    /// no error at all. Paging is the answer to that rather than a version of it: TotalCount tells the
    /// caller how many there are, so a picker can show a count, page, or search instead of quietly
    /// rendering the first 50 as though they were all of them.
    ///
    /// A caller that wants everybody must loop until it has TotalCount rows. A caller that renders one
    /// page and offers no way to reach page 2 has reintroduced the silent cap.
    /// </summary>
    Task<PaginatedResponse<EmployeeSummary>> ListActiveByCustomerAsync(
        Guid customerId,
        int pageNumber = 1,
        int pageSize = PaginatedQuery.DefaultPageSize,
        CancellationToken ct = default);
}
