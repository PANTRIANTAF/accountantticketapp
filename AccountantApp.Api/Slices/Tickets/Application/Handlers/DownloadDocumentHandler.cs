using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Infrastructure;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.11. The bytes, one document at a time, every one of them audited.
///
/// NO TERMINAL GUARD (rule 2). Matrix §8: downloading from a <c>Closed</c> ticket "is a stated
/// requirement" -- the ticket is read-only, not gone, and the close notification tells the Customer side
/// exactly that. <c>Cancelled</c> behaves the same way: §1.9 keeps everything, and a cancelled ticket's
/// attachments are often the only copy the Customer has.
///
/// STEP 5 OF §0.3 IS THE WHOLE POINT OF THIS HANDLER. Both ids come from the caller, INDEPENDENTLY. The
/// ticket check passing and the document being found makes both halves LOOK verified; they are not. Pair a
/// ticket you may read with a document id from a ticket you may not and, without the one line below, the
/// bytes are served. It is a textbook IDOR, and every test of the shape "a document on my own ticket
/// downloads fine" passes without it.
///
/// THE AUDIT ENTRY IS COMMITTED BEFORE THE BYTES LEAVE (rule 3, §4.0 D). The transaction closes in this
/// handler and the endpoint only then writes the response, so a client that disconnects mid-stream cannot
/// take the record of the disclosure with it. Streaming first and auditing afterwards produces exactly the
/// gap an audit log exists to close -- and reversing the order looks harmless because the happy path is
/// identical.
/// </summary>
public class DownloadDocumentHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly IDocumentApi _documents;

    public DownloadDocumentHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit,
        IEmployeeApi employees,
        IDocumentApi documents)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
        _employees = employees;
        _documents = documents;
    }

    public async Task<DocumentDownloadDto> Handle(
        DownloadDocumentRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "DownloadDocument", ct: ct);

        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);

        // Null for "does not exist" AND for "soft-deleted" -- the query filter simply does not find it --
        // and both are 404 (rule 7). Never 403, which would confirm the id names something real.
        var opened = await _documents.OpenAsync(req.DocumentId, ct)
                     ?? throw new AppException("Document not found.", 404);

        // §0.3 STEP 5. Do not remove it, do not fold it into the query above, and do not "simplify" it to
        // an assertion: it is the only thing standing between a valid session and another Customer's
        // payroll data.
        if (opened.Document.TicketId != ticket.Id)
            // The SAME message as a missing document. A different one distinguishes "exists elsewhere"
            // from "does not exist" and turns the id space into an enumerable directory.
            throw new AppException("Document not found.", 404);

        // A transaction for a read, deliberately: the audit entry is a WRITE, and it must be durable
        // before the caller has the bytes.
        await using var scope = await _transaction.BeginAsync(_db, ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.DocumentDownloaded,
            AuditTargets.Document,
            opened.Document.Id.ToString(),
            ticket.CustomerId,
            After: new
            {
                TicketId = ticket.Id,
                opened.Document.OriginalFileName,
                opened.Document.ContentType,
                opened.Document.SizeBytes,

                // Recorded because "who read this Customer's payroll file after the ticket closed" is a
                // question this log has to answer, and the ticket's status at the moment of the read is not
                // recoverable from the tickets row later.
                TicketStatus = ticket.Status,
            }), ct);

        // COMMITTED HERE, before the return. The endpoint writes the headers and the body afterwards.
        await _transaction.CommitAsync(ct);

        // The three header values come from Documents, which owns them (its DownloadShaping): always
        // "attachment", never "inline", plus nosniff, plus the RFC 5987 encoding that makes a Greek file
        // name survive an HTTP header. The endpoint writes them and decides none of them.
        var headers = DownloadShaping.For(opened.Document);

        return new DocumentDownloadDto
        {
            DocumentId = opened.Document.Id,
            FileName = opened.Document.OriginalFileName,

            // The SNIFFED type stored at upload, not anything the downloader asked for.
            ContentType = headers.ContentType,
            ContentDisposition = headers.ContentDisposition,
            ContentTypeOptions = headers.ContentTypeOptions,
            Content = opened.Content,
        };
    }
}
