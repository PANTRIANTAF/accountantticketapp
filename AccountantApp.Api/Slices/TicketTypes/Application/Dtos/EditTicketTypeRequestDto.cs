namespace AccountantApp.Api.Slices.TicketTypes.Application.Dtos;

public class EditTicketTypeRequestDto
{
    public Guid TicketTypeId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool AllowEmployeeToOpen { get; set; }
    public bool AllowSubjectOtherThanCreator { get; set; }
    public List<CreateFieldDescriptorDto> Fields { get; set; } = new();
}
