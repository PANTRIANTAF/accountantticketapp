using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Slices.TicketTypes.Application;
using AccountantApp.Api.Slices.TicketTypes.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

public class TicketTypesApi : ITicketTypesApi
{
    private readonly TicketTypesDbContext _db;

    public TicketTypesApi(TicketTypesDbContext db)
    {
        _db = db;
    }

    public async Task<TicketTypeDetailDto?> GetTicketTypeAsync(
        Guid ticketTypeId, UserRole callerRole, CancellationToken ct)
    {
        var type = await LoadCurrentVersion(ticketTypeId, ct);
        if (type is null || !TicketTypeMapper.IsDiscoverableBy(type, callerRole))
            return null;
        return TicketTypeMapper.ToDetail(type, TicketTypeMapper.CurrentVersionOf(type), callerRole);
    }

    // Audience check only, never the IsActive discovery check: this is how Tickets renders the
    // version a historical ticket was opened against, and deactivating a type must not blank out
    // the tickets already filed under it (correction note TicketTypes T-4). Tickets reaches
    // ticket types through this interface rather than through /api/ticket-types/version, so this
    // is the path the note was actually about.
    public async Task<TicketTypeDetailDto?> GetTicketTypeVersionAsync(
        Guid ticketTypeId, int versionNumber, UserRole callerRole, CancellationToken ct)
    {
        var type = await LoadVersion(ticketTypeId, versionNumber, ct);
        if (type is null || !TicketTypeMapper.IsInAudienceOf(type, callerRole))
            return null;
        var version = type.Versions.FirstOrDefault(v => v.VersionNumber == versionNumber);
        return version is null ? null : TicketTypeMapper.ToDetail(type, version, callerRole);
    }

    // The same read as GetTicketTypeVersionAsync, keyed on the version's own id, because that is
    // what tickets.ticket_type_version_id stores (Tickets §6.1 problem 1). It therefore applies
    // the same audience check and the same TicketTypeMapper.ToDetail projection — a second
    // projection here would be a second copy of the IsVisibleToCustomer strip, and only one of
    // the two would get corrected next time (correction note TicketTypes T-12).
    public async Task<TicketTypeDetailDto?> GetVersionByIdAsync(
        Guid ticketTypeVersionId, UserRole callerRole, CancellationToken ct)
    {
        var type = await LoadVersionById(ticketTypeVersionId, ct);
        if (type is null || !TicketTypeMapper.IsInAudienceOf(type, callerRole))
            return null;
        var version = type.Versions.FirstOrDefault(v => v.Id == ticketTypeVersionId);
        return version is null ? null : TicketTypeMapper.ToDetail(type, version, callerRole);
    }

    public Task<List<TicketTypeListItemDto>> ListAvailableTypesAsync(UserRole callerRole, CancellationToken ct)
    {
        var query = _db.TicketTypes.AsNoTracking().Where(t => t.IsActive);
        if (callerRole == UserRole.Employee)
            query = query.Where(t => t.AllowEmployeeToOpen);

        return query.OrderBy(t => t.DisplayName).ThenBy(t => t.Id)
            .Select(t => new TicketTypeListItemDto
            {
                Id = t.Id,
                Code = t.Code,
                DisplayName = t.DisplayName,
                Category = t.Category,
                IsActive = t.IsActive,
                CurrentVersionNumber = t.VersionNumber
            }).ToListAsync(ct);
    }

    // All three loads are filtered includes: an unfiltered Include pulls every version of the type
    // and every field descriptor of every version, then throws all but one away in memory
    // (correction note TicketTypes T-9). A type edited weekly for a year is 52 versions of rows
    // read to answer a one-version question.
    private Task<Core.TicketType?> LoadCurrentVersion(Guid ticketTypeId, CancellationToken ct) =>
        _db.TicketTypes.AsNoTracking()
            .Include(t => t.Versions.OrderByDescending(v => v.VersionNumber).Take(1))
            .ThenInclude(v => v.FieldDescriptors)
            .FirstOrDefaultAsync(t => t.Id == ticketTypeId, ct);

    private Task<Core.TicketType?> LoadVersion(Guid ticketTypeId, int versionNumber, CancellationToken ct) =>
        _db.TicketTypes.AsNoTracking()
            .Include(t => t.Versions.Where(v => v.VersionNumber == versionNumber))
            .ThenInclude(v => v.FieldDescriptors)
            .FirstOrDefaultAsync(t => t.Id == ticketTypeId, ct);

    // Filtered the same way, but the type is found through the version rather than the other way
    // round: the caller has only the version id. The parent type is still needed, because the
    // audience check and half of TicketTypeDetailDto (Code, DisplayName, IsActive, ...) live on it.
    private Task<Core.TicketType?> LoadVersionById(Guid ticketTypeVersionId, CancellationToken ct) =>
        _db.TicketTypes.AsNoTracking()
            .Include(t => t.Versions.Where(v => v.Id == ticketTypeVersionId))
            .ThenInclude(v => v.FieldDescriptors)
            .FirstOrDefaultAsync(t => t.Versions.Any(v => v.Id == ticketTypeVersionId), ct);
}