namespace AccountantApp.Api.Slices.TicketTypes.Application.Dtos;

public class GetTicketTypeVersionRequestDto
{
    public Guid TicketTypeId { get; set; }
    public int VersionNumber { get; set; }
}