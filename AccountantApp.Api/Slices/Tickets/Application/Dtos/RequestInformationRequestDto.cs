namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// InReview → AwaitingInformation. Accountants only, and the Assignee is RETAINED.
///
/// The transition's condition is "at least one field rejected, or a question posted" (plan §4.9 rule 4),
/// so either <see cref="Question"/> carries the question or the current revision already holds a
/// rejection. Neither is 422 -- a ticket returned with no reason is a ticket nobody can act on.
/// </summary>
public class RequestInformationRequestDto
{
    public Guid TicketId { get; set; }

    public int Version { get; set; }

    /// <summary>
    /// Posted as an AccountantResponse message if supplied. The Kind is derived from the role, never
    /// from the body (§4.10 rule 1).
    /// </summary>
    public string? Question { get; set; }
}
