using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

/// <summary>
/// Lists the Office. Both Accountant roles may call it; they get DIFFERENT SHAPES.
///
/// An AccountantAdmin gets AccountantDetailDto -- email, status, last login. An AccountantUser gets
/// AccountantSummaryDto -- name and id only, because the reason they can see this list at all is that
/// assigning a ticket requires knowing who exists, and that needs a name, not a login history.
/// </summary>
public sealed class ListAccountantsHandler
{
    private readonly IdentityDbContext _db;
    private readonly IPermissionChecker _permissions;

    public ListAccountantsHandler(IdentityDbContext db, IPermissionChecker permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    /// <summary>
    /// Returns <see cref="object"/>, which is deliberate and is the only workable option here.
    ///
    /// System.Text.Json serialises the RUNTIME type, so an AccountantUser's response body contains
    /// exactly the two summary fields -- there is no nulled-out `loginEmail` key sitting in the JSON for
    /// anyone to notice. A declared return type of PaginatedResponse&lt;AccountantDetailDto&gt; would
    /// serialise the summary rows through the detail contract and put the field back.
    ///
    /// The route declares .Produces&lt;PaginatedResponse&lt;AccountantDetailDto&gt;&gt;(200) so OpenAPI
    /// documents the richer shape. Do not "fix" this signature to match: a single DTO with nullable
    /// fields, a wrapper record, and two separate endpoints were all considered and rejected -- the
    /// first two put the leakable field back in the type, and the third gives the front end two URLs to
    /// choose between using a rule it should not have to know.
    /// </summary>
    public async Task<object> Handle(
        ListAccountantsRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ListAccountants", ct: ct);

        var (pageNumber, pageSize) = PaginatedQuery.Normalize(request.PageNumber, request.PageSize);

        // Accountants only: the two Accountant roles, which is exactly the rows where customer_id is
        // null. Filtering on the role rather than on CustomerId keeps the intent readable and matches
        // idx_user_accounts_accountants.
        //
        // No status filter. A Suspended Accountant must still appear -- an Admin cannot reactivate
        // somebody the list does not show, and the status field is there precisely so they can see it.
        var query = _db.UserAccounts
            .AsNoTracking()
            .Where(account => account.Role == UserRole.AccountantAdmin
                              || account.Role == UserRole.AccountantUser);

        var totalCount = await query.CountAsync(ct);

        // Order before Skip. An unordered Skip/Take is not deterministic in PostgreSQL, so rows can
        // appear on two pages or on none -- and it usually looks fine on a small table, which is how it
        // reaches production.
        var accounts = await query
            .OrderBy(account => account.DisplayName)
            .ThenBy(account => account.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        if (user.Role == UserRole.AccountantAdmin)
            return new PaginatedResponse<AccountantDetailDto>
            {
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalCount = totalCount,
                TotalPages = totalPages,
                Items = accounts.Select(IdentityMapper.ToDetailDto).ToList()
            };

        // The catalogue allows only the two Accountant roles through, so this branch is AccountantUser.
        return new PaginatedResponse<AccountantSummaryDto>
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            Items = accounts.Select(IdentityMapper.ToSummaryDto).ToList()
        };
    }
}
