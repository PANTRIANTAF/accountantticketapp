using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// AA, AU, and CA. NOT the Employee role: the matrix gives them "own record only", and a list of one is
/// still a list endpoint they may not call.
/// </summary>
public sealed class ListEmployeesHandler
{
    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IIdentityApi _identity;

    public ListEmployeesHandler(
        EmployeesDbContext db,
        IPermissionChecker permissions,
        IIdentityApi identity)
    {
        _db = db;
        _permissions = permissions;
        _identity = identity;
    }

    public async Task<PaginatedResponse<EmployeeSummaryDto>> Handle(
        ListEmployeesRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ListEmployees", ct: ct);

        var (pageNumber, pageSize) = PaginatedQuery.Normalize(request.PageNumber, request.PageSize);
        var status = EmployeeValidation.NormalizeStatusFilter(request.Status);

        // Always, for every role. For a Customer Admin this reduces the query to their own Customer no
        // matter what the request said.
        var query = _db.Employees.AsNoTracking().WhereInCustomerScope(user);

        if (request.CustomerId is { } requestedCustomerId)
        {
            // A 403 rather than a silently-reinterpreted filter. A filter that quietly means something
            // else for one role is how a Customer Admin comes to believe they have cross-Customer
            // visibility.
            if (user.Role is UserRole.CustomerAdmin or UserRole.Employee
                && requestedCustomerId != user.CustomerId!.Value)
                throw new AppException("You may only list employees at your own customer.", 403);

            query = query.Where(employee => employee.CustomerId == requestedCustomerId);
        }

        // No status filter returns BOTH Active and Departed. Departed Employees stay visible forever, and
        // a default that hides them makes a Customer Admin think the record is gone.
        if (status is not null)
            query = query.Where(employee => employee.Status == status);

        if (request.HasAccount is { } hasAccount)
            query = hasAccount
                ? query.Where(employee => employee.UserAccountId != null)
                : query.Where(employee => employee.UserAccountId == null);

        var search = request.SearchTerm?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            if (search.Length > 200)
                throw new AppException("Search must be at most 200 characters.", 422);

            // ILIKE against the stored columns, not .ToLower() inside Where -- the latter is unindexable,
            // and the trigram indexes in the migration exist to serve exactly this predicate.
            var pattern = EmployeeQueries.LikePattern(search);
            query = query.Where(employee =>
                EF.Functions.ILike(employee.GivenName, pattern, "\\") ||
                EF.Functions.ILike(employee.FamilyName, pattern, "\\") ||
                EF.Functions.ILike(employee.WorkEmail!, pattern, "\\"));
        }

        var totalCount = await query.CountAsync(ct);

        // Matches idx_employees_customer_name. The id tiebreaker is mandatory: two Employees at one
        // Customer sharing both names is ordinary, and an unstable sort makes paging skip and repeat rows.
        var page = query
            .OrderBy(employee => employee.FamilyName)
            .ThenBy(employee => employee.GivenName)
            .ThenBy(employee => employee.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize);

        var items = await page.Select(EmployeeMapper.ToSummaryExpression).ToListAsync(ct);
        await FillRolesAsync(page, items, ct);

        return new PaginatedResponse<EmployeeSummaryDto>
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Items = items
        };
    }

    /// <summary>
    /// ONE bulk call, after the page is materialised. A FindAsync per row is an N+1: at the maximum page
    /// size of 50 that is 51 queries for one request. The 500-id cap on FindManyAsync is ten times the
    /// largest page, so it cannot be hit from here.
    /// </summary>
    private async Task FillRolesAsync(
        IQueryable<Core.Employee> page,
        List<EmployeeSummaryDto> items,
        CancellationToken ct)
    {
        // A second, narrow read of the same page rather than one wider projection, because the summary DTO
        // has no UserAccountId property to carry the id on (it is an account id, and nothing outside this
        // method needs one) and EF cannot invoke the mapper expression inside a larger projection. Both
        // reads hit idx_employees_customer_name, so the extra round trip is an index scan, not a table scan.
        var pairs = await page
            .Where(employee => employee.UserAccountId != null)
            .Select(employee => new { employee.Id, AccountId = employee.UserAccountId!.Value })
            .ToListAsync(ct);

        if (pairs.Count == 0)
            return;

        var accounts = await _identity.FindManyAsync(
            pairs.Select(pair => pair.AccountId).ToList(), ct);

        var byEmployee = pairs.ToDictionary(pair => pair.Id, pair => pair.AccountId);
        foreach (var item in items)
        {
            // Role stays null for an accountless Employee, and the SPA renders that as "not invited".
            if (byEmployee.TryGetValue(item.Id, out var accountId)
                && accounts.TryGetValue(accountId, out var account))
                item.Role = account.Role;
        }
    }
}
