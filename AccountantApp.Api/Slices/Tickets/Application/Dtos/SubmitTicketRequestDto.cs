namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Draft → Submitted, or AwaitingInformation → Submitted. Two operations wearing one name, and they
/// differ in the one way that matters: the second one KEEPS its Assignee (plan §4.2 rule 1).
/// </summary>
public class SubmitTicketRequestDto
{
    public Guid TicketId { get; set; }

    /// <summary>The version the caller last read. A stale one is 409 (§3.2 rule 1).</summary>
    public int Version { get; set; }
}
