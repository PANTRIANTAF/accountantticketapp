using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Audit.Application.Dtos;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Audit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Audit.Application.Handlers;

/// <summary>
/// Paged search over the audit log. Opens no transaction and writes no audit entry: reading the
/// log is not itself an audited action, and a log that grew on every read would be a log nobody
/// could read.
/// </summary>
public class SearchAuditLogHandler
{
    private readonly AuditDbContext _db;
    private readonly IPermissionChecker _permissions;

    public SearchAuditLogHandler(AuditDbContext db, IPermissionChecker permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public async Task<PaginatedResponse<AuditEntryDto>> Handle(
        SearchAuditLogRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ReadAuditLog", ct: ct);

        Validate(req);

        var (pageNumber, pageSize) = PaginatedQuery.Normalize(req.PageNumber, req.PageSize);

        // Composed against the IQueryable so PostgreSQL filters using the indexes on the largest
        // table in the system. Never ToListAsync() and then Where().
        var query = _db.AuditEntries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(req.ActorUserId))
            query = query.Where(e => e.ActorUserId == req.ActorUserId);
        if (!string.IsNullOrWhiteSpace(req.Action))
            query = query.Where(e => e.Action == req.Action);
        if (!string.IsNullOrWhiteSpace(req.TargetKind))
            query = query.Where(e => e.TargetKind == req.TargetKind);
        if (!string.IsNullOrWhiteSpace(req.TargetId))
            query = query.Where(e => e.TargetId == req.TargetId);
        if (req.CustomerId.HasValue)
            query = query.Where(e => e.CustomerId == req.CustomerId.Value);
        if (!string.IsNullOrWhiteSpace(req.Outcome))
            query = query.Where(e => e.Outcome == req.Outcome);
        if (req.From.HasValue)
            query = query.Where(e => e.OccurredAt >= req.From.Value);
        if (req.To.HasValue)
            query = query.Where(e => e.OccurredAt <= req.To.Value);

        // Two passes over the filter, deliberately. Materialising the whole result set to count it
        // in memory is the alternative, and on this table that is the outage.
        var totalCount = await query.CountAsync(ct);

        // The Id tiebreaker is not decoration: one transaction can write several entries with the
        // same occurred_at, and an unstable sort makes paging skip and repeat rows.
        var items = await query
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .Select(AuditMapper.ToDto)
            .ToListAsync(ct);

        return new PaginatedResponse<AuditEntryDto>
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Items = items
        };
    }

    // Every rejection here is 422 rather than an empty page. An investigator who mistypes a filter
    // and gets zero rows reads that as "this never happened", which is the one thing an audit tool
    // must not say by accident.
    private static void Validate(SearchAuditLogRequestDto req)
    {
        if (req.From.HasValue && req.To.HasValue && req.From > req.To)
            throw new AppException("'From' must not be later than 'To'.", 422);

        // A target id is only meaningful alongside its kind -- ids are not unique across kinds, so
        // filtering on the id alone can return entries about an unrelated entity that happens to
        // share a GUID string. It also cannot use idx_audit_entries_target, whose leading column is
        // target_kind, so it degrades to a full scan of the largest table in the database.
        if (!string.IsNullOrWhiteSpace(req.TargetId) && string.IsNullOrWhiteSpace(req.TargetKind))
            throw new AppException("'TargetId' requires 'TargetKind'.", 422);

        if (!string.IsNullOrWhiteSpace(req.Action) && !AuditActions.All.Contains(req.Action))
            throw new AppException(
                $"'{req.Action}' is not a known audit action. Fetch /api/audit/action-codes for the catalogue.",
                422);

        // Not required by the plan, which validates Action and Outcome only. Applied for the same
        // reason and with the same catalogue AuditApi validates against on write: an unrecognised
        // kind cannot match a stored row, so accepting it can only ever produce a silent empty page.
        if (!string.IsNullOrWhiteSpace(req.TargetKind) && !AuditTargets.All.Contains(req.TargetKind))
            throw new AppException(
                $"'{req.TargetKind}' is not a known audit target kind.", 422);

        if (!string.IsNullOrWhiteSpace(req.Outcome) && !AuditOutcome.All.Contains(req.Outcome))
            throw new AppException(
                $"'{req.Outcome}' is not a known outcome. Expected one of: {string.Join(", ", AuditOutcome.All)}.",
                422);
    }
}
