namespace AccountantApp.Api.Slices.Audit.Core;

public sealed class AuditRecord
{
    public Guid Id { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ActorRole { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? BeforeValue { get; set; }
    public string? AfterValue { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string SourceIp { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
}