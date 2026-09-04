namespace AccountantApp.Api.Slices.Tickets.Core;

/// <summary>
/// The join between a conversation message and the documents rendered with it.
///
/// It exists because a document is attached to a TICKET in the Documents schema (its ticket_id column)
/// but to a MESSAGE in the conversation, and both are true: the document belongs to the ticket for
/// authorization, and to a message for rendering. documents.ticket_id remains the authorization
/// anchor, so section 0.3 step 5 -- verify doc.TicketId == ticket.Id -- still has to run even when a
/// row here says the document is on this message.
///
/// Composite primary key (TicketMessageId, DocumentId). No surrogate id, and no FK on DocumentId:
/// documents is another slice's table.
/// </summary>
public sealed class TicketMessageDocument
{
    public Guid TicketMessageId { get; set; }
    public Guid DocumentId { get; set; }
}
