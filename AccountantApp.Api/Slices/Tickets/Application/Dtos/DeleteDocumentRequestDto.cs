namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Soft-deletes one document. Nothing in this system is hard-deleted (§9.2).
///
/// Two halves to the permission (plan §4.11 rule 6): an Accountant may delete any document on a ticket
/// they can see; a Customer-side actor may delete only their OWN upload, and only while the ticket is
/// Draft or Submitted-with-no-Assignee.
/// </summary>
public class DeleteDocumentRequestDto
{
    public Guid TicketId { get; set; }

    public Guid DocumentId { get; set; }

    public int Version { get; set; }
}
