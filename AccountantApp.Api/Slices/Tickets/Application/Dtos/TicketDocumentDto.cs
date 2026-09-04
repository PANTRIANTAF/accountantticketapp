namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// One row of <c>/api/documents/list</c>, and what <c>/api/documents/upload</c> returns.
///
/// Metadata only. There is no Content property here for the same reason there is none on
/// <c>DocumentSummary</c>: a list of a ticket's ten attachments must not be able to become a list of
/// their contents. Bytes come from <c>/api/documents/download</c>, one at a time, each audited.
/// </summary>
public class TicketDocumentDto
{
    public Guid Id { get; set; }

    public Guid TicketId { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// The SNIFFED type, not what the uploader declared. Documents decides this from the leading bytes.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>CustomerUpload or AccountantResponse, derived from the uploader's role.</summary>
    public string Origin { get; set; } = string.Empty;

    public Guid UploadedByUserAccountId { get; set; }

    public string? UploadedByName { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>
/// <c>/api/documents/delete</c>. A soft delete: the row and the bytes both remain, with
/// <c>deleted_at</c> set, and the global query filter is what makes the document unreachable
/// afterwards. There is no undelete.
/// </summary>
public class DocumentDeletedDto
{
    public Guid DocumentId { get; set; }

    public Guid TicketId { get; set; }

    public DateTimeOffset DeletedAt { get; set; }
}

/// <summary>
/// The handler's answer for <c>/api/documents/download</c>: the bytes plus the three header values
/// <c>DownloadShaping</c> chose. The endpoint writes them and returns a file result -- it does not
/// decide any of them, because "attachment" and "nosniff" are properties of serving bytes and belong in
/// one place.
///
/// Not a JSON response type. It never reaches the wire in this shape.
/// </summary>
public class DocumentDownloadDto
{
    public Guid DocumentId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    /// <summary>Always begins "attachment;". Never "inline".</summary>
    public string ContentDisposition { get; set; } = string.Empty;

    public string ContentTypeOptions { get; set; } = string.Empty;

    public byte[] Content { get; set; } = [];
}
