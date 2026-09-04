using System.Linq.Expressions;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Employees.ExternalInterfaces;

public sealed class EmployeeApi : IEmployeeApi
{
    private const int LookupCap = 500;

    // One projection for all five methods, so the summary's field list -- and therefore what leaves this
    // slice -- exists once. It selects no sensitive column, which is what makes the restriction structural
    // rather than a convention each method has to remember.
    private static readonly Expression<Func<Employee, EmployeeSummary>> ToSummary = employee =>
        new EmployeeSummary(
            employee.Id,
            employee.CustomerId,
            employee.GivenName,
            employee.FamilyName,
            employee.Status,
            employee.UserAccountId != null,
            employee.UserAccountId);

    private readonly EmployeesDbContext _db;

    public EmployeeApi(EmployeesDbContext db) => _db = db;

    public Task<EmployeeSummary?> FindAsync(Guid employeeId, CancellationToken ct = default) =>
        _db.Employees.AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Select(ToSummary)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, EmployeeSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> employeeIds,
        CancellationToken ct = default)
    {
        if (employeeIds.Count > LookupCap)
            throw new InvalidOperationException($"At most {LookupCap} employee ids may be requested.");

        var ids = employeeIds.Distinct().ToList();
        return await _db.Employees.AsNoTracking()
            .Where(employee => ids.Contains(employee.Id))
            .Select(ToSummary)
            .ToDictionaryAsync(summary => summary.Id, ct);
    }

    // Existence and Active status in one predicate. Not FindAsync(...)?.IsActive, because an unknown id must
    // be false rather than null-propagating into whatever the caller's ?? does with it.
    public Task<bool> IsActiveAsync(Guid employeeId, CancellationToken ct = default) =>
        _db.Employees.AsNoTracking()
            .AnyAsync(employee => employee.Id == employeeId
                               && employee.Status == EmployeeStatus.Active, ct);

    public Task<EmployeeSummary?> FindByAccountAsync(Guid userAccountId, CancellationToken ct = default) =>
        _db.Employees.AsNoTracking()
            .Where(employee => employee.UserAccountId == userAccountId)
            .Select(ToSummary)
            .FirstOrDefaultAsync(ct);

    public async Task<PaginatedResponse<EmployeeSummary>> ListActiveByCustomerAsync(
        Guid customerId,
        int pageNumber = 1,
        int pageSize = PaginatedQuery.DefaultPageSize,
        CancellationToken ct = default)
    {
        // Normalized, never trusted: a caller passing 0 or -3 gets page 1 rather than a query with a
        // negative OFFSET, and a caller asking for 10,000 rows gets MaxPageSize.
        var (page, size) = PaginatedQuery.Normalize(pageNumber, pageSize);

        var query = _db.Employees.AsNoTracking()
            .Where(employee => employee.CustomerId == customerId
                            && employee.Status == EmployeeStatus.Active);

        // Counted before paging, so TotalCount is the number of Active Employees and not the size of the
        // page. A picker that shows "50 of 50" when there are 4,000 is the silent cap wearing a number.
        var totalCount = await query.CountAsync(ct);

        // ThenBy(Id) is what makes paging correct rather than approximately correct: two colleagues with
        // the same name have no stable order without it, so one of them can appear on both page 1 and
        // page 2 while the other appears on neither.
        var items = await query
            .OrderBy(employee => employee.FamilyName)
            .ThenBy(employee => employee.GivenName)
            .ThenBy(employee => employee.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(ToSummary)
            .ToListAsync(ct);

        return new PaginatedResponse<EmployeeSummary>
        {
            PageNumber = page,
            PageSize = size,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)size),
            Items = items
        };
    }
}
