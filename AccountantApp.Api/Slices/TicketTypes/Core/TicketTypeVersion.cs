namespace AccountantApp.Api.Slices.TicketTypes.Core;

public class TicketTypeVersion
{
    public Guid Id { get; set; }
    public Guid TicketTypeId { get; set; }
    public int VersionNumber { get; set; }
    public DateTime CreatedAt { get; set; }

    public TicketType TicketType { get; set; } = default!;
    public ICollection<FieldDescriptor> FieldDescriptors { get; set; } = new List<FieldDescriptor>();
}
