namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// InReview → Answered. Accountants only. Permitted only when no required visible field of the current
/// revision is unverified or rejected (plan §4.9 rule 1).
/// </summary>
public class AnswerTicketRequestDto
{
    public Guid TicketId { get; set; }

    public int Version { get; set; }

    /// <summary>The answer itself, posted as an AccountantResponse message if supplied.</summary>
    public string? Message { get; set; }
}
