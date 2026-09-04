namespace AccountantApp.Api.Slices.Audit.Application.Dtos;

/// <summary>
/// One audit entry with its before/after payload. Returned only by the detail endpoint, one entry
/// at a time. The payloads are JSON text, already redacted at write time by
/// <c>Application/Redaction.cs</c> — this slice does not redact on read, because the column must
/// never have held a secret in the first place.
/// </summary>
public sealed class AuditEntryDetailDto
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

    public string? BeforeValue { get; set; }
    public string? AfterValue { get; set; }
}
