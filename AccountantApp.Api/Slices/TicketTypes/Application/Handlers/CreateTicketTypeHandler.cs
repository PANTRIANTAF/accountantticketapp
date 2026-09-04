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

public class CreateTicketTypeHandler
{
    private const string DuplicateMessage = "A Ticket Type with this code already exists";
    private readonly TicketTypesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _auditApi;

    public CreateTicketTypeHandler(
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

    public async Task<TicketTypeDetailDto> Handle(CreateTicketTypeRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "CreateTicketType", ct: ct);
        if (string.IsNullOrWhiteSpace(req.Code))
            throw new AppException("Ticket type code is required.", 422);

        TicketTypeMapper.NormalizeTicketType(req);
        TicketTypeMapper.ValidateTicketType(req.Code, req.DisplayName, req.Category);
        TicketTypeMapper.ValidateDescription(req.Description);
        TicketTypeMapper.ValidateFields(req.Fields);
        if (await _db.TicketTypes.AnyAsync(t => t.Code.ToLower() == req.Code.ToLower(), ct))
            throw new AppException(DuplicateMessage, 409);

        await using var transactionScope = await _transaction.BeginAsync(_db, ct);
        var now = DateTime.UtcNow;
        var version = new TicketTypeVersion { VersionNumber = 1, CreatedAt = now };
        foreach (var field in req.Fields.OrderBy(f => f.DisplayOrder))
            version.FieldDescriptors.Add(TicketTypeMapper.ToEntity(field, now));

        var type = new TicketType
        {
            Code = req.Code,
            DisplayName = req.DisplayName,
            Description = req.Description,
            Category = req.Category,
            AllowEmployeeToOpen = req.AllowEmployeeToOpen,
            AllowSubjectOtherThanCreator = req.AllowSubjectOtherThanCreator,
            IsActive = true,
            VersionNumber = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        type.Versions.Add(version);

        _db.TicketTypes.Add(type);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new AppException(DuplicateMessage, 409);
        }

        await _auditApi.LogAsync(new AuditEntry(
            AuditActions.TicketTypeCreated,
            AuditTargets.TicketType,
            type.Id.ToString(),
            After: new { type.Code, VersionNumber = 1 }), ct);
        await _transaction.CommitAsync(ct);
        return TicketTypeMapper.ToDetail(type, version, user.Role);
    }
}