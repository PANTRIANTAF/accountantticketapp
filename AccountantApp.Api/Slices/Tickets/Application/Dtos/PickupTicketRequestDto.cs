namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Submitted → InReview with self-assignment, in one atomic operation (plan §4.8 rule 1). There is no
/// <c>AssigneeUserAccountId</c> property: a pickup assigns the CALLER. Assigning someone else is
/// <see cref="AssignTicketRequestDto"/>.
/// </summary>
public class PickupTicketRequestDto
{
    public Guid TicketId { get; set; }

    public int Version { get; set; }
}
