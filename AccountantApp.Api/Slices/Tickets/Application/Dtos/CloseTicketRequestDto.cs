namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Answered → Closed. Accountants only; the Customer side never closes.
///
/// Closing is final: there is no transition out of Closed (§9.1, LOCKED), no reopen endpoint and no
/// Reopened status. A continuation is a new ticket with <c>PrecededByTicketId</c>.
/// </summary>
public class CloseTicketRequestDto
{
    public Guid TicketId { get; set; }

    public int Version { get; set; }
}
