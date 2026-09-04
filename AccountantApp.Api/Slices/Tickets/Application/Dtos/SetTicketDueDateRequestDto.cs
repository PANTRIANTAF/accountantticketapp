namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Sets or clears the due date. Accountants only (matrix §7).
///
/// A past date is ALLOWED (plan §4.7 rule 5): an Accountant recording an already-missed statutory
/// deadline is ordinary. Do not add a future-date guard.
/// </summary>
public class SetTicketDueDateRequestDto
{
    public Guid TicketId { get; set; }

    public int Version { get; set; }

    /// <summary>Null clears the due date. That is why this endpoint is separate from priority.</summary>
    public DateOnly? DueDate { get; set; }
}
