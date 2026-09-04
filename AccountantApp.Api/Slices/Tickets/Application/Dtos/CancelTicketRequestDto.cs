namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Cancellation is the only removal in this system (matrix §7): a status, not a delete. The ticket, its
/// revisions, its messages and its documents all remain readable afterwards.
///
/// An Employee may cancel only their OWN ticket and only while it is Draft or Submitted (plan §4.12
/// rule 1) -- not once an Accountant is working on it.
/// </summary>
public class CancelTicketRequestDto
{
    public Guid TicketId { get; set; }

    public int Version { get; set; }

    /// <summary>Why. Recorded on the audit entry.</summary>
    public string? Reason { get; set; }
}
