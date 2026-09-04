namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Answered → InReview. Accountants only.
///
/// This is reopening BEFORE close -- "the response was wrong" -- and it IS in the closed transition
/// table (plan §4.9 rule 3). It is not a §9.1 reopen of a Closed ticket, and confusing the two produces
/// either a missing legal transition or an illegal one.
/// </summary>
public class ReturnToReviewRequestDto
{
    public Guid TicketId { get; set; }

    public int Version { get; set; }

    /// <summary>Why the answer is being revisited. Posted as an internal note if supplied.</summary>
    public string? Reason { get; set; }
}
