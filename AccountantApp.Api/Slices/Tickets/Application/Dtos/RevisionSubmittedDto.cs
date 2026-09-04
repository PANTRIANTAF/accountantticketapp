namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// <c>/api/tickets/submit-revision</c>. Carries the new revision's identity as well as the ticket's new
/// state, because the client needs the id to render what it just wrote and the version to make its next
/// call.
/// </summary>
public class RevisionSubmittedDto
{
    public Guid TicketId { get; set; }

    public Guid RevisionId { get; set; }

    /// <summary>Gap-free per ticket, so this is also the count of revisions so far.</summary>
    public int SequenceNumber { get; set; }

    /// <summary>
    /// How many values were copied from the previous revision instead of being re-answered (§4.6). Zero
    /// on revision 1.
    /// </summary>
    public int CarriedForwardCount { get; set; }

    /// <summary>
    /// The ticket after the write. AwaitingInformation → InReview happens as part of submitting a
    /// revision, so the status here is often not the one the caller sent against.
    /// </summary>
    public TicketStateDto Ticket { get; set; } = new();
}
