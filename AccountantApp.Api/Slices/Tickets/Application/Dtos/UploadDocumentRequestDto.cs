namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// The one multipart request in the system, mapped off the form by
/// <c>TicketsEndpoints</c> before it reaches the handler.
///
/// No <c>Origin</c> property: the origin is derived from the caller's role and ignored if supplied
/// (plan §0.3, §4.11 rule 4). No <c>CustomerId</c> either -- it is the TICKET's Customer, which is also
/// why <c>user.CustomerId</c> (null for an Accountant) is never the source.
/// </summary>
public class UploadDocumentRequestDto
{
    public Guid TicketId { get; set; }

    /// <summary>
    /// Checked with <c>RequireVersion</c> so an upload against a ticket the caller has not re-read is a
    /// 409 rather than a surprise (§8 rule 7). The tickets row itself is not written by an upload.
    /// </summary>
    public int Version { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// What the client claims the type is. Documents sniffs the leading bytes and decides for itself;
    /// this is passed through for the record only.
    /// </summary>
    public string DeclaredContentType { get; set; } = string.Empty;

    /// <summary>
    /// The body. Documents reads it with its own cap so an oversized upload is refused before the whole
    /// thing is in memory.
    /// </summary>
    public Stream Content { get; set; } = Stream.Null;
}
