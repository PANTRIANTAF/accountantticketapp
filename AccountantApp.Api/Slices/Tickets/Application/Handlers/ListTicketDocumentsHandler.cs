using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Infrastructure;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.11. A ticket's live documents, metadata only.
///
/// NO TERMINAL GUARD (rule 2). Matrix §8 makes reading a Closed ticket's documents "a stated
/// requirement", and a blanket "no operations on a terminal ticket" check applied to all four document
/// handlers is precisely how that requirement gets broken. It belongs on upload and delete only.
///
/// NO VERSION either: the request DTO has none, because nothing is written and there is nothing for a
/// stale version to collide with. §8 rule 7's "every MUTATING route carries Version" is the rule, and this
/// route mutates nothing.
///
/// SOFT-DELETED DOCUMENTS ARE ABSENT STRUCTURALLY (rule 7), through the global query filter inside
/// <c>Documents</c> -- not by a WHERE clause here that somebody has to remember. There is no
/// <c>IgnoreQueryFilters</c> path to this list and none is to be added.
///
/// NO AUDIT ENTRY, and that is a GAP rather than a decision: §4.11 rule 8 names three codes --
/// <c>DocumentUploaded</c>, <c>DocumentDownloaded</c>, <c>DocumentSoftDeleted</c> -- and
/// <c>AuditActions</c> has no fourth for a LIST. Inventing a string here would be an audit code no
/// migration's CHECK constraint knows about, which fails at the database rather than in review. Listing
/// metadata is also the one document operation that discloses no content. Reported.
/// </summary>
public class ListTicketDocumentsHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IEmployeeApi _employees;
    private readonly IIdentityApi _identity;
    private readonly IDocumentApi _documents;

    public ListTicketDocumentsHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IEmployeeApi employees,
        IIdentityApi identity,
        IDocumentApi documents)
    {
        _db = db;
        _permissions = permissions;
        _employees = employees;
        _identity = identity;
        _documents = documents;
    }

    public async Task<List<TicketDocumentDto>> Handle(
        ListTicketDocumentsRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ListTicketDocuments", ct: ct);

        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        // Steps 2, 3 and 4 of §0.3 in one call. Step 5 does not arise: the caller supplies no document id,
        // and every row returned comes from THIS ticket's own list.
        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);

        var documents = await _documents.ListByTicketAsync(ticket.Id, ct);
        if (documents.Count == 0)
            return [];

        // One batch for every distinct uploader. FindManyAsync throws above 500 ids (§9's batch rule) and a
        // ticket's uploaders are a handful of people, so no chunking is needed here -- the id list is
        // distinct uploaders, not documents.
        var uploaderIds = documents
            .Select(document => document.UploadedByUserAccountId)
            .Distinct()
            .ToList();

        var uploaders = await _identity.FindManyAsync(uploaderIds, ct);

        return
        [
            .. documents.Select(document => UploadDocumentHandler.ToDto(
                document,
                uploaders.TryGetValue(document.UploadedByUserAccountId, out var uploader)
                    ? uploader.DisplayName
                    : null))
        ];
    }
}
