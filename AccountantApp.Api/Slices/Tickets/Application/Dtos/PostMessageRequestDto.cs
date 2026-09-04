namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Posts one message to a ticket's conversation. Used by both <c>/post-message</c> and
/// <c>/post-internal-note</c>.
///
/// There is no <c>Kind</c> property, and that is the point (plan §4.10 rule 1): the kind is derived from
/// the caller's role -- an Accountant produces an AccountantResponse, a Customer-side actor produces a
/// CustomerMessage -- and an internal note is a SEPARATE ACTION so the catalogue denies a Customer-side
/// caller rather than a handler branching on a body field. If the kind came from the body, a Customer
/// could post something that renders as an Accountant response.
/// </summary>
public class PostMessageRequestDto
{
    public Guid TicketId { get; set; }

    public int Version { get; set; }

    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Documents to attach. Every one must ALREADY belong to this ticket (§4.10 rule 7) -- the same IDOR
    /// check as §0.3 step 5.
    /// </summary>
    public List<Guid> AttachedDocumentIds { get; set; } = [];
}
