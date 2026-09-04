namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// The correction round. Fields the caller does not supply are carried forward from the previous
/// revision, so this DTO is a delta of intent, not a snapshot -- the SNAPSHOT is what gets written
/// (plan §4.5 rule 2).
/// </summary>
public class SubmitRevisionRequestDto
{
    public Guid TicketId { get; set; }

    public int Version { get; set; }

    /// <summary>What changed and why. Stored on the new revision, shown to the other side.</summary>
    public string? Note { get; set; }

    public List<TicketFieldValueInputDto> FieldValues { get; set; } = [];
}
