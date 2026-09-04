namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// <c>/api/tickets/verify-field</c>.
///
/// A verification NEVER changes the ticket's status (plan §4.7 rule 5) -- rejecting a field does not
/// move the ticket to AwaitingInformation, because a reviewer rejects several fields and then returns
/// the ticket once. The status in <see cref="Ticket"/> is therefore the status the caller already had.
/// The VERSION is not: verifying touches the ticket row, so the caller must use the version returned
/// here for the transition that follows.
/// </summary>
public class FieldVerifiedDto
{
    public Guid TicketId { get; set; }

    public Guid FieldValueId { get; set; }

    /// <summary>The new append-only verification row. The previous ones are still there.</summary>
    public Guid VerificationId { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public TicketStateDto Ticket { get; set; } = new();
}
