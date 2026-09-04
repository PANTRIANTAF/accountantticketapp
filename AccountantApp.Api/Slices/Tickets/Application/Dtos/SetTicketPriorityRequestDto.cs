namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Sets Normal or High. Accountants only (matrix §7).
///
/// A separate DTO and a separate handler from the due date, because the two audit differently and a
/// combined shape with two nullable properties cannot tell "not supplied" from "clear it"
/// (plan §4.7 rule 1).
/// </summary>
public class SetTicketPriorityRequestDto
{
    public Guid TicketId { get; set; }

    public int Version { get; set; }

    /// <summary>Normal or High. Anything else is 422.</summary>
    public string Priority { get; set; } = string.Empty;
}
