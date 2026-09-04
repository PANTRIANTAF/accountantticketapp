using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.11 rule 6. A SOFT delete: <c>deleted_at</c> and <c>deleted_by</c> are set, the row stays, the
/// bytes stay, and the global query filter is what makes the document unreachable afterwards. Nothing in
/// this system is hard-deleted (§9.2), there is no undelete, and <c>IDocumentApi</c> exposes no
/// <c>HardDeleteAsync</c> even as an internal method.
///
/// THE PERMISSION HAS TWO HALVES (matrix §8), and both need data only this slice has -- the uploader from
/// <c>DocumentSummary.UploadedByUserAccountId</c>, the status from the ticket row:
///
///   - an ACCOUNTANT may delete any document on a ticket they can see;
///   - a CUSTOMER-SIDE actor may delete only their OWN upload, and only while the ticket has not yet
///     reached <c>InReview</c>.
///
/// "HAS NOT YET REACHED <c>InReview</c>" IS NOT "IS NOT <c>InReview</c>". A ticket in <c>Answered</c> has
/// reached it. And <c>Submitted</c> after a correction round has ALSO already been in <c>InReview</c>,
/// which the status alone cannot tell you -- so the test used here is the safest reading the plan gives:
/// <c>Draft</c>, or <c>Submitted</c> WITH NO ASSIGNEE, because an Assignee is the durable trace of having
/// been picked up. A ticket that went InReview -> AwaitingInformation -> Submitted keeps its Assignee
/// (§4.2 rule 1), so this correctly refuses the delete there. Flagged in §13 item 5 and reported.
///
/// TERMINAL TICKETS REFUSE A DELETE (rule 2) even though they permit a download. The asymmetry is the
/// point: a Closed ticket's documents stay readable, and nothing may be removed from the record after the
/// matter is over.
/// </summary>
public class DeleteDocumentHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly IDocumentApi _documents;

    public DeleteDocumentHandler(
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

    public async Task<DocumentDeletedDto> Handle(
        DeleteDocumentRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "DeleteDocument", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);

        TicketAccess.RequireNotTerminal(ticket);

        // Metadata only -- the bytes are never read on a delete path. Null covers both "no such document"
        // and "already soft-deleted", and both are 404 (rule 7): a second delete of the same id is a 404
        // rather than a silent success, because the caller's view of the ticket is out of date.
        var document = await _documents.FindAsync(req.DocumentId, ct)
                       ?? throw new AppException("Document not found.", 404);

        // §0.3 STEP 5, on the delete path too. Both ids came from the caller independently, and without
        // this a valid session can soft-delete another Customer's document by pairing its id with a ticket
        // of their own. The write paths need it as much as the download does.
        if (document.TicketId != ticket.Id)
            throw new AppException("Document not found.", 404);

        RequireDeletable(ticket, document, user, callerAccountId);

        await using var scope = await _transaction.BeginAsync(_db, ct);

        // Enlists in this transaction and never commits, so the delete and its audit entry are atomic.
        // False means no LIVE document with that id -- the same 404 as above, reachable if two callers race.
        var deleted = await _documents.SoftDeleteAsync(document.Id, callerAccountId, ct);
        if (!deleted)
            throw new AppException("Document not found.", 404);

        // The handler's own clock rather than the stored deleted_at. Documents sets that value with its own
        // clock and gives no way to read it back -- FindAsync now returns null for this id, by the very
        // query filter that makes the delete work -- so the two can differ by microseconds. The
        // alternative, adding a timestamp to the contract's return, is a change to another slice and is
        // reported rather than made.
        var deletedAt = DateTimeOffset.UtcNow;

        await _audit.LogAsync(new AuditEntry(
            AuditActions.DocumentSoftDeleted,
            AuditTargets.Document,
            document.Id.ToString(),
            ticket.CustomerId,

            // BOTH sides here. The Before is the only remaining machine-readable record of what the
            // document was, since the row is behind a query filter from now on and no read path lifts it.
            Before: new
            {
                TicketId = ticket.Id,
                document.OriginalFileName,
                document.ContentType,
                document.SizeBytes,
                document.Origin,
                document.UploadedByUserAccountId,
            },
            After: new { DeletedAt = deletedAt, TicketStatus = ticket.Status }), ct);

        await _transaction.CommitAsync(ct);

        // The ticket row is NOT touched, matching the upload path: a document is not the ticket, and the
        // Version was checked (§8 rule 7) so a delete against a stale view is a 409.
        return new DocumentDeletedDto
        {
            DocumentId = document.Id,
            TicketId = ticket.Id,
            DeletedAt = deletedAt,
        };
    }

    /// <summary>
    /// The two halves of rule 6. An Accountant passes on the strength of seeing the ticket at all; a
    /// Customer-side actor has to clear both conditions.
    ///
    /// The status half is 422 and the ownership half is 403, deliberately: "you may not delete other
    /// people's uploads" is a permanent fact about the caller, while "this ticket has moved on" is a fact
    /// about the ticket that was different an hour ago. Neither is a 404 -- the document's existence is
    /// already known to this caller, who can see it in the ticket's own document list.
    /// </summary>
    private static void RequireDeletable(
        Ticket ticket, DocumentSummary document, CurrentUser user, Guid callerAccountId)
    {
        if (TicketVisibility.IsAccountant(user))
            return;

        if (document.UploadedByUserAccountId != callerAccountId)
            throw new AppException("You can only remove documents you uploaded yourself.", 403);

        var stillTheirs = ticket.Status == TicketStatus.Draft
                          || (ticket.Status == TicketStatus.Submitted
                              && ticket.AssigneeUserAccountId is null);

        if (!stillTheirs)
            throw new AppException(
                "The Office has this ticket now, so its documents can no longer be removed. "
                + "Ask them to remove it.", 422);
    }
}
