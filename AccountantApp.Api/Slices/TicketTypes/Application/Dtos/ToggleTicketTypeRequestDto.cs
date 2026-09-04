namespace AccountantApp.Api.Slices.TicketTypes.Application.Dtos;

public class ToggleTicketTypeRequestDto
{
    public Guid TicketTypeId { get; set; }
    public bool NewIsActive { get; set; }
}