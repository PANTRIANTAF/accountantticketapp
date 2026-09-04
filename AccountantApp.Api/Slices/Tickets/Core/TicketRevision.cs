namespace AccountantApp.Api.Slices.Tickets.Core;

/// <summary>
/// An immutable snapshot of ALL Field Values for a Ticket at one moment. 01-DomainModel.md section 3.
///
/// APPEND-ONLY: "A revision, once written, is never modified and never deleted. To see what an
/// Employee originally claimed, you read revision 1." A correction round appends revision 2 with a
/// row for EVERY descriptor -- new or carried forward -- because a partial revision cannot be read as
/// a snapshot.
///
/// No Version property. Section 9.7 puts optimistic concurrency on the tickets row alone; an
/// append-only table has nothing to conflict on. Two concurrent corrections are serialised by
/// uq_ticket_revisions_sequence instead (23505, mapped to 409).
/// </summary>
public sealed class TicketRevision
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }

    /// <summary>Starts at 1. Revision 1 is created together with the Ticket.</summary>
    public int SequenceNumber { get; set; }

    public Guid SubmittedByUserAccountId { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>Optional note explaining what changed, written by the submitter.</summary>
    public string? Note { get; set; }

    public List<FieldValue> FieldValues { get; set; } = [];
}
