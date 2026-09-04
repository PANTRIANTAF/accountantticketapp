namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Lists a ticket's documents. Permitted on a Closed ticket (plan §4.11 rule 2) and never returns a
/// soft-deleted document -- the Documents query filter removes those, which is also why a deleted
/// document is 404 on download and not 403.
/// </summary>
public class ListTicketDocumentsRequestDto
{
    public Guid TicketId { get; set; }
}
