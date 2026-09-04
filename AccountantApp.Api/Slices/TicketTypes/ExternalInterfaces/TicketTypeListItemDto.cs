namespace AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

// Contract type: returned by ITicketTypesApi.ListAvailableTypesAsync as well as by
// /api/ticket-types/list. See the note on TicketTypeDetailDto for why it is not in
// Application/Dtos/.
public class TicketTypeListItemDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int CurrentVersionNumber { get; set; }
}
