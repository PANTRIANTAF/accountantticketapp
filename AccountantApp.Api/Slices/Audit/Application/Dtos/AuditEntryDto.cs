namespace AccountantApp.Api.Slices.Audit.Application.Dtos;

/// <summary>
/// One audit entry as the list view returns it.
/// </summary>
/// <remarks>
/// Note what is absent: <c>BeforeValue</c> and <c>AfterValue</c>. They are the only place personal
/// data appears in this table, they are up to 8 KB each, and a list endpoint that carried them
/// would make every page of the audit log a bulk export of tax and payroll values. They belong to
/// <see cref="AuditEntryDetailDto"/>, which returns exactly one entry.
///
/// This is deliberately a separate type rather than a base class of the detail DTO. If the detail
/// DTO inherited from this one then this one would also *be* a detail DTO, and the separation that
/// keeps the payload off the list endpoint would depend on nobody ever projecting the wrong type.
/// </remarks>
public sealed class AuditEntryDto
{
    public Guid Id { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ActorRole { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string SourceIp { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
}
