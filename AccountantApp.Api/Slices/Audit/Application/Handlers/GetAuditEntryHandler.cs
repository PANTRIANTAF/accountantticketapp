using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.Application.Dtos;
using AccountantApp.Api.Slices.Audit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Audit.Application.Handlers;

/// <summary>
/// One audit entry, and the only surface that returns its before/after payload.
/// </summary>
/// <remarks>
/// No Customer scope filter is applied, and that is correct rather than an omission: the audit log
/// is AccountantAdmin-only and an Admin sees every Customer. A scope filter here would be a no-op
/// for the only role that can reach the method, which reads like protection while providing none.
/// </remarks>
public class GetAuditEntryHandler
{
    private readonly AuditDbContext _db;
    private readonly IPermissionChecker _permissions;

    public GetAuditEntryHandler(AuditDbContext db, IPermissionChecker permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public async Task<AuditEntryDetailDto> Handle(
        GetAuditEntryRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ReadAuditLog", ct: ct);

        var record = await _db.AuditEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == req.AuditEntryId, ct)
            ?? throw new AppException("Audit entry not found.", 404);

        return AuditMapper.ToDetailDto(record);
    }
}
