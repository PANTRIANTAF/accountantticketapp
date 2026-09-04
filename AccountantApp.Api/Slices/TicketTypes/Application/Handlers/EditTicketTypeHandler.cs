using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.TicketTypes.Application.Dtos;
using AccountantApp.Api.Slices.TicketTypes.Core;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using AccountantApp.Api.Slices.TicketTypes.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccountantApp.Api.Slices.TicketTypes.Application.Handlers;

public class EditTicketTypeHandler
{
    private const string ConcurrentEditMessage =
        "This ticket type was edited by someone else. Reload and try again.";
    private readonly TicketTypesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _auditApi;

    public EditTicketTypeHandler(
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

    public async Task<TicketTypeDetailDto> Handle(EditTicketTypeRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "EditTicketType", ct: ct);
        TicketTypeMapper.NormalizeTicketType(req);
        // Code is immutable, so only the editable strings are re-checked.
        TicketTypeMapper.ValidateTicketType(string.Empty, req.DisplayName, req.Category);
        TicketTypeMapper.ValidateDescription(req.Description);
        TicketTypeMapper.ValidateFields(req.Fields);

        await using var transactionScope = await _transaction.BeginAsync(_db, ct);
        var type = await _db.TicketTypes.Include(t => t.Versions)
            .FirstOrDefaultAsync(t => t.Id == req.TicketTypeId, ct);
        if (type is null)
            throw new AppException("Ticket type not found.", 404);

        var next = type.Versions.Max(v => v.VersionNumber) + 1;
        var now = DateTime.UtcNow;
        var version = new TicketTypeVersion { VersionNumber = next, CreatedAt = now };
        foreach (var field in req.Fields.OrderBy(f => f.DisplayOrder))
            version.FieldDescriptors.Add(TicketTypeMapper.ToEntity(field, now));
        type.Versions.Add(version);

        type.DisplayName = req.DisplayName;
        type.Description = req.Description;
        type.Category = req.Category;
        type.AllowEmployeeToOpen = req.AllowEmployeeToOpen;
        type.AllowSubjectOtherThanCreator = req.AllowSubjectOtherThanCreator;
        type.VersionNumber = next;
        type.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new AppException(ConcurrentEditMessage, 409);
        }

        await _auditApi.LogAsync(new AuditEntry(
            AuditActions.TicketTypeVersionCreated,
            AuditTargets.TicketType,
            type.Id.ToString(),
            After: new { type.Code, VersionNumber = next }), ct);
        await _transaction.CommitAsync(ct);
        return TicketTypeMapper.ToDetail(type, version, user.Role);
    }
}