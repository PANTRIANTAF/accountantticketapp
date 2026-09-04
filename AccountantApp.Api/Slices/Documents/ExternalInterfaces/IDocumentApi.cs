namespace AccountantApp.Api.Slices.Documents.ExternalInterfaces;

/// <summary>
/// What another slice sees of a document. Never the entity itself, and never the bytes: a caller
/// holding a tracked Document could mutate it and save it through another context, which is the exact
/// coupling one-DbContext-per-slice exists to prevent.
///
/// There is deliberately NO Content property. Bytes come back from OpenAsync alone, so a list of a
/// ticket's ten attachments cannot accidentally be a list of their contents.
///
/// CustomerId and TicketId ARE here, and they are the reason this record exists in this shape: they
/// are what let Tickets assert that the document it just loaded belongs to the ticket it authorized.
/// Without them the check in section 0.3 step 5 -- the one whose absence is a textbook IDOR -- could
/// not be written at all.
/// </summary>
public sealed record DocumentSummary(
    Guid Id,
    Guid TicketId,
    Guid CustomerId,
    string Origin,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    Guid UploadedByUserAccountId,
    DateTimeOffset UploadedAt);

/// <summary>Metadata plus the bytes. Returned only by OpenAsync.</summary>
public sealed record DocumentContentResult(DocumentSummary Document, byte[] Content);

/// <summary>
/// What Tickets hands over for an upload.
///
/// DeclaredContentType is the client's Content-Type header. It is carried here so the contract is
/// honest about what the caller has, and it is DELIBERATELY NEVER USED to decide anything: the stored
/// content type is sniffed from the leading bytes. It exists for logging and for the day somebody wants
/// to compare what was claimed with what was found.
///
/// CustomerId must be the TICKET'S Customer, taken from the ticket Tickets has already loaded -- never
/// user.CustomerId, which is null for an Accountant and would make the row either a NOT NULL violation
/// or, worse, silently wrong.
///
/// Origin must be one of the two DocumentOrigin constants, derived by Tickets from the uploader's ROLE.
/// It must never come from a request body: a Customer who could set it would mark their own upload as an
/// Accountant response.
/// </summary>
public sealed record StoreDocumentRequest(
    Guid TicketId,
    Guid CustomerId,
    string Origin,
    string OriginalFileName,
    string DeclaredContentType,
    Stream Content,
    Guid UploadedByUserAccountId);

/// <summary>
/// This slice's ENTIRE public surface. One slice calls it: Tickets.
///
/// IT PERFORMS NO AUTHORIZATION. NONE. It has no CurrentUser parameter, it applies no Customer scope
/// filter, and it will hand any caller the bytes of any live document in the system given only its id.
///
/// That is deliberate, and there is no safe alternative given the dependency graph. A document's access
/// rules come entirely from its ticket and must be re-checked at the moment of download, but
/// 03-SliceInventory.md section 2 permits Documents -> Audit and nothing else, and Tickets -> Documents
/// already exists -- so Documents -> Tickets would be a cycle. This slice therefore CANNOT evaluate a
/// ticket's access rules, and a contract that pretended to authorize would be more dangerous than one
/// that visibly does not.
///
/// Before ANY call here, Tickets must:
///   1. require the matching permission through IPermissionChecker -- the upload, download or delete
///      document action, whose names belong to TicketsActionCatalogue and are deliberately not written
///      as literals here: EndpointRoutingTests reads action names out of the source, so a name that
///      appears in this slice at all would be reported as required by a slice that has no handlers;
///   2. load the TICKET with .WhereInCustomerScope(user) -- not found is a 404;
///   3. for an Employee, additionally require Creator or Subject; for a CustomerAdmin, own Customer;
///   4. for a Draft ticket, require the caller to be the Creator -- no Accountant ever sees a draft;
///   5. VERIFY doc.TicketId == ticket.Id, or 404. This is the step that gets skipped, because the
///      ticket check passed and the document was found so both halves look verified. They are not: the
///      caller supplied both ids independently, and pairing a ticket you may read with a document from
///      one you may not serves the bytes. Every test of "a document on my own ticket downloads" passes
///      without it;
///   6. audit the operation.
///
/// NO OTHER SLICE MAY EVER BE GIVEN THIS DEPENDENCY, for exactly that reason.
///
/// It writes no audit entries. All three codes -- DocumentUploaded, DocumentDownloaded and
/// DocumentSoftDeleted -- are written by Tickets, which knows the ticket and the actor's relationship to
/// it; this slice does not inject IAuditApi at all, despite Documents -> Audit being its one permitted
/// edge. Do not add an audit call to make the edge look used: two entries for one upload is worse than
/// an unused edge.
/// </summary>
public interface IDocumentApi
{
    // ── WRITES. Enlist in the caller's transaction; never commit. ──

    /// <summary>
    /// Validates (the allow-list, the size cap, the file name), stores the metadata row and the bytes,
    /// and returns the new summary. Throws AppException(422) on a rejected type, a rejected size, or an
    /// empty body, and InvalidOperationException on a caller bug such as a missing CustomerId or an
    /// Origin that is not one of the two constants.
    ///
    /// DOES NOT AUTHORIZE ANYTHING. The caller must have already run every step above.
    ///
    /// It ENLISTS in the caller's transaction and NEVER COMMITS, so the metadata row, the bytes, the
    /// ticket change that prompted the upload and the audit entry are one atomic unit -- and because the
    /// bytes are in PostgreSQL rather than on a volume, that atomicity is real. There is no
    /// orphaned-file cleanup job in this system because there can be no orphaned file.
    /// </summary>
    Task<DocumentSummary> StoreAsync(StoreDocumentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Sets deleted_at and deleted_by. Returns false when no live document with that id exists, so the
    /// caller maps it to a 404 with its own message -- including for an already-deleted document, which
    /// the query filter simply does not find. Never removes a row and never touches the bytes.
    ///
    /// DOES NOT AUTHORIZE ANYTHING. In particular it does not enforce the matrix rule that Accountants
    /// may delete any document on a ticket they can see while Customer-side actors may delete only their
    /// OWN uploads and only before the ticket reaches InReview -- both halves need the ticket's status
    /// and the uploader's identity, so Tickets evaluates them.
    ///
    /// It enlists in the caller's transaction and never commits. There is no UndeleteAsync and no
    /// HardDeleteAsync, not even marked internal.
    /// </summary>
    Task<bool> SoftDeleteAsync(Guid documentId, Guid deletedByUserAccountId,
                               CancellationToken ct = default);

    // ── READS. No transaction. ──

    /// <summary>Metadata only, no bytes. Null when not found or soft-deleted.</summary>
    Task<DocumentSummary?> FindAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// A ticket's live documents, oldest first. An unknown ticket id returns an EMPTY LIST rather than
    /// an error: the caller has already established the ticket exists, and a throw here would turn its
    /// 404 into a 500. Soft-deleted documents are absent structurally, through the global query filter,
    /// rather than by a WHERE clause somebody has to remember.
    ///
    /// Unpaginated. A ticket with 500 attachments returns 500 rows -- of metadata only, so bounded and
    /// small.
    /// </summary>
    Task<IReadOnlyList<DocumentSummary>> ListByTicketAsync(Guid ticketId,
                                                           CancellationToken ct = default);

    /// <summary>
    /// Metadata AND bytes. Null when not found or soft-deleted, which the caller turns into a 404 --
    /// NEVER a 403, which would confirm the document exists.
    ///
    /// This is the ONLY method that reads document_contents.
    ///
    /// DOES NOT AUTHORIZE ANYTHING. The caller must have already authorized the ticket AND must verify
    /// Document.TicketId against it. Given a bare id this method returns another Customer's payroll data
    /// without complaint, and a test asserts that it does, by design.
    /// </summary>
    Task<DocumentContentResult?> OpenAsync(Guid documentId, CancellationToken ct = default);
}
