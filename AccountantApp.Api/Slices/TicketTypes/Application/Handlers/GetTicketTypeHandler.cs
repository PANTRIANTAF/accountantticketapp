using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.TicketTypes.Application.Dtos;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using AccountantApp.Api.Slices.TicketTypes.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.TicketTypes.Application.Handlers;

public class GetTicketTypeHandler
{
    private readonly TicketTypesDbContext _db;
    private readonly IPermissionChecker _permissions;

    public GetTicketTypeHandler(TicketTypesDbContext db, IPermissionChecker permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public async Task<TicketTypeDetailDto> Handle(GetTicketTypeRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ReadTicketType", ct: ct);
        var type = await _db.TicketTypes.AsNoTracking().Include(t => t.Versions)
            .ThenInclude(v => v.FieldDescriptors).FirstOrDefaultAsync(t => t.Id == req.TicketTypeId, ct);
        if (type is null)
            throw new AppException("Ticket type not found.", 404);

        TicketTypeMapper.ApplyCustomerSideVisibility(type, user);
        return TicketTypeMapper.ToDetail(type, TicketTypeMapper.CurrentVersionOf(type), user.Role);
    }
}