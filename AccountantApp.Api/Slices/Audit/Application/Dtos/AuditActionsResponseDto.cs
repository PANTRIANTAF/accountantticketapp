namespace AccountantApp.Api.Slices.Audit.Application.Dtos;

/// <summary>
/// The fixed catalogues, so the audit screen can populate its filter dropdowns from the server
/// rather than keeping a copy that drifts the moment an action code is added.
/// </summary>
public sealed class AuditActionsResponseDto
{
    public List<string> Actions { get; set; } = new();
    public List<string> TargetKinds { get; set; } = new();
    public List<string> Outcomes { get; set; } = new();
}
