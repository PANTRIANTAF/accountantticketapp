using AccountantApp.Api.Shared.Pagination;

namespace AccountantApp.Api.Slices.Audit.Application.Dtos;

/// <summary>
/// Filters for the audit search. Every one is optional and they combine with AND; all null means
/// "the whole log, most recent page first".
///
/// A plain class with settable properties, not a positional record: minimal-API binding from a
/// request body needs a parameterless constructor and writable properties.
/// </summary>
public sealed class SearchAuditLogRequestDto
{
    public string? ActorUserId { get; set; }
    public string? Action { get; set; }
    public string? TargetKind { get; set; }
    public string? TargetId { get; set; }
    public Guid? CustomerId { get; set; }
    public string? Outcome { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = PaginatedQuery.DefaultPageSize;
}
