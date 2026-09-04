using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.TicketTypes.Application.Dtos;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using AccountantApp.Api.Slices.TicketTypes.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.TicketTypes.Application.Handlers;

public class GetTicketTypeVersionHandler
{
    private readonly TicketTypesDbContext _db;
    private readonly IPermissionChecker _permissions;

    public GetTicketTypeVersionHandler(TicketTypesDbContext db, IPermissionChecker permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public async Task<TicketTypeDetailDto> Handle(GetTicketTypeVersionRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ReadTicketType", ct: ct);
        var type = await _db.TicketTypes.AsNoTracking()
            .Include(t => t.Versions.Where(v => v.VersionNumber == req.VersionNumber))
            .ThenInclude(v => v.FieldDescriptors)
            .FirstOrDefaultAsync(t => t.Id == req.TicketTypeId, ct);
        if (type is null)
            throw new AppException("Ticket type not found.", 404);

        // A historical version must stay readable after the type is deactivated (T-4); only
        // the audience rule (who may open this type at all) still applies here.
        TicketTypeMapper.ApplyCustomerSideAudience(type, user);
        var version = type.Versions.FirstOrDefault(v => v.VersionNumber == req.VersionNumber);
        if (version is null)
            throw new AppException("Ticket type version not found.", 404);
        return TicketTypeMapper.ToDetail(type, version, user.Role);
    }
}