using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.TicketTypes.Application.Dtos;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using AccountantApp.Api.Slices.TicketTypes.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.TicketTypes.Application.Handlers;

public class ToggleTicketTypeHandler
{
    private readonly TicketTypesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _auditApi;

    public ToggleTicketTypeHandler(
        TicketTypesDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi auditApi)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _auditApi = auditApi;
    }

    public async Task<TicketTypeDetailDto> Handle(ToggleTicketTypeRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ToggleTicketType", ct: ct);
        // Only the version rows are loaded here; the current version's descriptors are
        // fetched separately below rather than via ThenInclude over every version (T-9).
        var type = await _db.TicketTypes.Include(t => t.Versions)
            .FirstOrDefaultAsync(t => t.Id == req.TicketTypeId, ct);
        if (type is null)
            throw new AppException("Ticket type not found.", 404);

        var version = TicketTypeMapper.CurrentVersionOf(type);
        await _db.Entry(version).Collection(v => v.FieldDescriptors).LoadAsync(ct);
        if (type.IsActive == req.NewIsActive)
            return TicketTypeMapper.ToDetail(type, version, user.Role);

        await using var transactionScope = await _transaction.BeginAsync(_db, ct);
        var now = DateTime.UtcNow;
        type.IsActive = req.NewIsActive;
        type.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        await _auditApi.LogAsync(new AuditEntry(
            req.NewIsActive ? AuditActions.TicketTypeActivated : AuditActions.TicketTypeDeactivated,
            AuditTargets.TicketType,
            type.Id.ToString(),
            After: new { type.Code, type.IsActive }), ct);
        await _transaction.CommitAsync(ct);
        return TicketTypeMapper.ToDetail(type, version, user.Role);
    }
}