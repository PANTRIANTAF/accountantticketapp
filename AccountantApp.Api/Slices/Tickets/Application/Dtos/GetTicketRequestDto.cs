namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>Reads one ticket. A ticket the caller may not see is 404, never 403 (§0.4).</summary>
public class GetTicketRequestDto
{
    public Guid TicketId { get; set; }
}
