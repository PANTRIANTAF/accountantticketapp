namespace AccountantApp.Api.Slices.TicketTypes.Application.Dtos;

public class TicketTypeListItemDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int CurrentVersionNumber { get; set; }
}
