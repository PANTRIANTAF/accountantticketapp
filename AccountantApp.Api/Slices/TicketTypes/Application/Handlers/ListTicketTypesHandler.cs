using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Pagination;
using TicketTypeApplicationDtos = AccountantApp.Api.Slices.TicketTypes.Application.Dtos;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using AccountantApp.Api.Slices.TicketTypes.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.TicketTypes.Application.Handlers;

public class ListTicketTypesHandler
{
    private readonly TicketTypesDbContext _db;
    private readonly IPermissionChecker _permissions;

    public ListTicketTypesHandler(TicketTypesDbContext db, IPermissionChecker permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public async Task<PaginatedResponse<TicketTypeListItemDto>> Handle(
        TicketTypeApplicationDtos.ListTicketTypesRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ListTicketTypes", ct: ct);
        var (pageNumber, pageSize) = PaginatedQuery.Normalize(req.PageNumber, req.PageSize);
        var query = _db.TicketTypes.AsNoTracking();

        if (TicketTypeMapper.IsCustomerSide(user.Role))
        {
            query = query.Where(t => t.IsActive);
            if (user.Role == UserRole.Employee)
                query = query.Where(t => t.AllowEmployeeToOpen);
        }
        else if (req.ActiveOnly.HasValue)
        {
            query = query.Where(t => t.IsActive == req.ActiveOnly.Value);
        }

        var totalCount = await query.CountAsync(ct);
        var items = await query.OrderBy(t => t.DisplayName).ThenBy(t => t.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .Select(t => new TicketTypeListItemDto
            {
                Id = t.Id,
                Code = t.Code,
                DisplayName = t.DisplayName,
                Category = t.Category,
                IsActive = t.IsActive,
                CurrentVersionNumber = t.VersionNumber
            }).ToListAsync(ct);

        return new PaginatedResponse<TicketTypeListItemDto>
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Items = items
        };
    }
}