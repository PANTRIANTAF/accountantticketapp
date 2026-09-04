namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Downloads one document's bytes.
///
/// It carries BOTH ids on purpose. The ticket is what authorization is decided on, and
/// <c>doc.TicketId == ticket.Id</c> is then verified independently (plan §0.3 step 5) -- the textbook
/// IDOR that every "my own ticket downloads fine" test passes without.
/// </summary>
public class DownloadDocumentRequestDto
{
    public Guid TicketId { get; set; }

    public Guid DocumentId { get; set; }
}
