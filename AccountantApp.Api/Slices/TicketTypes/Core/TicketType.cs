namespace AccountantApp.Api.Slices.TicketTypes.Core;

public class TicketType
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool AllowEmployeeToOpen { get; set; } = true;
    public bool AllowSubjectOtherThanCreator { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int VersionNumber { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<TicketTypeVersion> Versions { get; set; } = new List<TicketTypeVersion>();
}
