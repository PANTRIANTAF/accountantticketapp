using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Documents.Application;
using AccountantApp.Api.Slices.Documents.Core;
using AccountantApp.Api.Slices.Documents.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Documents.ExternalInterfaces;

/// <summary>
/// The only class in the application that may touch document_contents.
///
/// It injects two things and no more. In particular it does NOT inject IAuditApi (Tickets writes all
/// three document audit codes), IPermissionChecker (it checks no permissions), or CurrentUser (it has no
/// caller to be scoped to -- see IDocumentApi).
/// </summary>
public sealed class DocumentApi : IDocumentApi
{
    private readonly DocumentsDbContext _db;
    private readonly IRequestTransaction _transaction;

    public DocumentApi(DocumentsDbContext db, IRequestTransaction transaction)
    {
        _db = db;
        _transaction = transaction;
    }

    public async Task<DocumentSummary> StoreAsync(
        StoreDocumentRequest request, CancellationToken ct = default)
    {
        // Caller bugs, and they throw rather than returning a 4xx: a Tickets handler that passes
        // Guid.Empty as the Customer -- which is what user.CustomerId!.Value degrades to for an
        // Accountant if anybody ever makes it non-nullable -- has made a mistake it could have avoided,
        // and a row with an empty tenant id is worse than a 500. "Fails loudly, not a null row."
        if (request.TicketId == Guid.Empty)
            throw new InvalidOperationException("TicketId is required.");
        if (request.CustomerId == Guid.Empty)
            throw new InvalidOperationException(
                "CustomerId is required, and it must be the TICKET'S Customer -- never user.CustomerId, "
                + "which is null for an Accountant.");
        if (request.UploadedByUserAccountId == Guid.Empty)
            throw new InvalidOperationException("UploadedByUserAccountId is required.");
        if (!DocumentOrigin.All.Contains(request.Origin))
            throw new InvalidOperationException(
                $"Unknown Origin '{request.Origin}'. It is derived from the uploader's role by Tickets "
                + "and is never client-supplied. ck_documents_origin is the backstop.");

        // The body is read once, into one buffer, and everything after this works over that buffer. A
        // stream consumed by the sniffer and then handed to the writer stores zero bytes, and
        // ck_documents_size is what would catch it -- which is a constraint violation reported as a 500
        // for what is really a two-line ordering mistake.
        //
        // The read itself refuses to buffer more than the cap.
        var content = await UploadValidation.ReadWithinLimitAsync(request.Content, ct);

        // VALIDATE BEFORE THE ROW IS WRITTEN AND BEFORE THE BYTES ARE STORED. One transaction, and the
        // 422 happens first -- there is no window in which an unvalidated type exists in the table.
        var validated = UploadValidation.Validate(content, request.OriginalFileName);

        // Enlist, never begin and never commit. The caller owns the transaction, so the metadata row,
        // the bytes, the ticket change and the audit entry commit together or not at all.
        await _transaction.EnlistAsync(_db, ct);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            CustomerId = request.CustomerId,
            TicketId = request.TicketId,
            Origin = request.Origin,
            OriginalFileName = validated.OriginalFileName,
            ContentType = validated.ContentType,
            SizeBytes = validated.SizeBytes,
            ContentHash = validated.ContentHash,
            UploadedByUserAccountId = request.UploadedByUserAccountId,
            UploadedAt = DateTimeOffset.UtcNow
        };

        _db.Documents.Add(document);

        // Both tables, in ONE SaveChangesAsync. Two saves would give a moment in which the metadata row
        // exists and the bytes do not, which is only invisible because the caller's transaction covers
        // it -- and it would stop being invisible the day somebody called this outside one.
        _db.DocumentContents.Add(new DocumentContent
        {
            DocumentId = document.Id,
            Content = content
        });

        await _db.SaveChangesAsync(ct);

        return ToSummary(document);
    }

    public async Task<bool> SoftDeleteAsync(
        Guid documentId, Guid deletedByUserAccountId, CancellationToken ct = default)
    {
        if (deletedByUserAccountId == Guid.Empty)
            throw new InvalidOperationException(
                "DeletedByUserAccountId is required. ck_documents_deletion rejects a deleted_at without "
                + "a deleter, and a row that cannot answer 'who deleted it' fails the domain model's "
                + "section 6.");

        await _transaction.EnlistAsync(_db, ct);

        // Tracked, not AsNoTracking: this one is a write. The global filter applies, so an
        // already-deleted document is simply not found -- which returns false, becomes a 404 from
        // Tickets, and is the right answer for a document the caller can no longer see. Not a 422, and
        // not an idempotent 200. No IgnoreQueryFilters is needed or permitted.
        var document = await _db.Documents.FirstOrDefaultAsync(item => item.Id == documentId, ct);
        if (document is null)
            return false;

        // Both columns, together. ck_documents_deletion rejects either one alone.
        document.DeletedAt = DateTimeOffset.UtcNow;
        document.DeletedByUserAccountId = deletedByUserAccountId;

        // document_contents is NOT touched. A handler that sets deleted_at and then nulls the bytes to
        // save space has hard-deleted the document while leaving a row that claims otherwise -- and
        // retention here is indefinite, so the bytes stay forever.
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<DocumentSummary?> FindAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == documentId, ct);

        return document is null ? null : ToSummary(document);
    }

    public async Task<IReadOnlyList<DocumentSummary>> ListByTicketAsync(
        Guid ticketId, CancellationToken ct = default)
    {
        // Oldest first, matching idx_documents_ticket's (ticket_id, uploaded_at). The Id tiebreak makes
        // the order total rather than merely correct: two uploads sharing a timestamp would otherwise
        // come back in whatever order the plan produced, and the index still serves the leading columns.
        //
        // No scope filter, no bytes, no pagination, and no error for an unknown ticket.
        var documents = await _db.Documents.AsNoTracking()
            .Where(item => item.TicketId == ticketId)
            .OrderBy(item => item.UploadedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(ct);

        return documents.Select(ToSummary).ToList();
    }

    public async Task<DocumentContentResult?> OpenAsync(
        Guid documentId, CancellationToken ct = default)
    {
        // TWO QUERIES, IN THIS ORDER, AND THE ORDER IS THE MECHANISM. The Document is found through the
        // FILTERED query first; only then are the bytes read by id.
        //
        // document_contents has no deleted_at column and therefore no query filter (there is nothing to
        // filter on), so a query that starts from it -- or a join in that direction -- serves the bytes
        // of a document its owner was told was gone. That is a one-line mistake, it produces no error,
        // and nothing but a test for exactly this case catches it.
        var document = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == documentId, ct);
        if (document is null)
            return null;

        var content = await _db.DocumentContents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.DocumentId == documentId, ct);

        // The intra-slice foreign key makes this unreachable through any supported path, so if it does
        // happen the metadata and the bytes have drifted apart in the database itself.
        if (content is null)
            throw new AppException(
                $"Document {documentId} has a metadata row but no stored content.", 500);

        // THE ONE PLACE A 500 IS RIGHT. Corrupted bytes are a server fault, not a client one, and
        // serving silently corrupted tax data is worse than failing: the recipient cannot tell.
        //
        // .Trim() on the stored hash is deliberate. content_hash is CHAR(64), which PostgreSQL pads,
        // and a comparison against an unpadded 64-character string would then fail for every single
        // document -- turning an integrity check into an outage.
        if (!string.Equals(
                UploadValidation.ComputeHash(content.Content),
                document.ContentHash.Trim(),
                StringComparison.OrdinalIgnoreCase))
            throw new AppException(
                $"Stored content for document {documentId} does not match its recorded hash.", 500);

        // The whole array, in memory. At 25 MB that is acceptable at one-Office scale; streaming
        // straight out of BYTEA would need a data reader this slice does not otherwise use.
        return new DocumentContentResult(ToSummary(document), content.Content);
    }

    // The entity never leaves this class. A caller holding a tracked Document could mutate it, and
    // DocumentSummary carries no bytes, which is what makes the metadata reads cheap by construction.
    private static DocumentSummary ToSummary(Document document) => new(
        document.Id,
        document.TicketId,
        document.CustomerId,
        document.Origin,
        document.OriginalFileName,
        document.ContentType,
        document.SizeBytes,
        document.UploadedByUserAccountId,
        document.UploadedAt);
}
