using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Customers.Application.Dtos;
using AccountantApp.Api.Slices.Customers.Core;
using AccountantApp.Api.Slices.Customers.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Customers.Application.Handlers;

public sealed class ListCustomersHandler
{
    private readonly CustomersDbContext _db;
    private readonly IPermissionChecker _permissions;

    public ListCustomersHandler(CustomersDbContext db, IPermissionChecker permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public async Task<PaginatedResponse<CustomerSummaryDto>> Handle(
        ListCustomersRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ListCustomers", ct: ct);

        var (pageNumber, pageSize) = PaginatedQuery.Normalize(request.PageNumber, request.PageSize);
        var status = request.Status?.Trim();
        if (status is not null && status is not (CustomerStatus.Active or CustomerStatus.Suspended))
            throw new AppException("Unknown customer status.", 422);

        // The catalogue restricts ListCustomers to the two Accountant roles, so today the scope
        // filter is a no-op. It is here because "the permission check is the only thing keeping
        // a Customer Admin from reading every Customer in the office" is one catalogue edit away
        // from being a full customer-list disclosure — and the row-level filter would still hold.
        var query = _db.Customers.AsNoTracking().WhereMatchesCustomerScope(user);
        if (status is not null)
            query = query.Where(customer => customer.Status == status);

        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            if (search.Length > 200)
                throw new AppException("Search must be at most 200 characters.", 422);
            var pattern = $"%{EscapeLikePattern(search)}%";
            query = query.Where(customer =>
                EF.Functions.ILike(customer.LegalName, pattern, "\\") ||
                EF.Functions.ILike(customer.TradingName!, pattern, "\\"));
        }

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .OrderBy(customer => customer.LegalName)
            .ThenBy(customer => customer.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(CustomerMapper.ToSummaryExpression)
            .ToListAsync(ct);

        return new PaginatedResponse<CustomerSummaryDto>
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Items = items
        };
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}