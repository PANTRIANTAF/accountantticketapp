using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Infrastructure;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.11, and the first of the four handlers §0.3 makes this slice responsible for.
///
/// <c>IDocumentApi</c> AUTHORIZES NOTHING -- no CurrentUser, no scope filter, and it will return the
/// bytes of any live document in the system given only its id. The security of every document rests on
/// the six steps of §0.3 being performed HERE, on all four handlers. On this one:
///
///   1. the permission check below;
///   2. + 3. + 4. <c>TicketAccess.LoadVisibleAsync</c>, whose four visibility layers ARE steps 2, 3 and
///      4 -- Customer scope, the Employee Creator-or-Subject narrowing, and the Draft-is-private-to-its-
///      Creator rule -- and whose miss is a 404;
///   5. not applicable to an upload: there is no caller-supplied document id to pair with the wrong
///      ticket. The document is created here, with the ticket's own id;
///   6. the <c>DocumentUploaded</c> entry, inside the transaction.
///
/// STATUS: NO QUALIFIER EXCEPT TERMINAL (rule 5, §13 item 5(a), decided). A Customer-side actor may
/// upload to any ticket they can see in any non-terminal status, INCLUDING <c>InReview</c>, and the
/// field-editability rule does not constrain it. An Accountant mid-review routinely needs one more
/// document, and the alternative bounces the ticket through <c>AwaitingInformation</c> purely to accept a
/// file. An upload is additive and audited; a field edit rewrites the thing under review. The two rules
/// differing is correct -- do not smooth it away.
///
/// The ticket row is NOT written: no <c>Touch</c>, so the caller's <c>Version</c> survives the upload and
/// a form that uploads three files in a row does not need to re-read the ticket between them. The version
/// is still CHECKED (§8 rule 7) so an upload against a ticket that has moved on is a 409.
/// </summary>
public class UploadDocumentHandler
{
    // The two Origin values were duplicated here as private consts, because DocumentOrigin lived in
    // Documents/Core and dependency rule 2 forbids this slice from reading another slice's Core. The
    // constants have since been moved to Documents/ExternalInterfaces (2026-09-02), which this slice MAY
    // read, so the duplicates are gone and DocumentOrigin.CustomerUpload / .AccountantResponse are used
    // directly below. StoreDocumentRequest validates Origin against DocumentOrigin.All with an Ordinal
    // comparer and throws on a miss, so there must be exactly one definition of these strings.

    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly IIdentityApi _identity;
    private readonly IDocumentApi _documents;

    public UploadDocumentHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit,
        IEmployeeApi employees,
        IIdentityApi identity,
        IDocumentApi documents)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
        _employees = employees;
        _identity = identity;
        _documents = documents;
    }

    public async Task<TicketDocumentDto> Handle(
        UploadDocumentRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "UploadDocument", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);

        // The ONLY status restriction on this path (rule 5). Rule 2 requires the terminal guard on upload
        // and delete while download and list pass through it.
        TicketAccess.RequireNotTerminal(ticket);

        // Derived from the ROLE, never from the body -- there is no Origin property on the request DTO to
        // read even if somebody wanted to. A Customer able to set it would mark their own upload as an
        // Accountant response and change what the ticket appears to say.
        var origin = TicketVisibility.IsAccountant(user)
            ? DocumentOrigin.AccountantResponse
            : DocumentOrigin.CustomerUpload;

        await using var scope = await _transaction.BeginAsync(_db, ct);

        // Inside the transaction, and IDocumentApi enlists rather than committing: the metadata row, the
        // bytes and the audit entry are one atomic unit. Because the bytes are in PostgreSQL and not on a
        // volume, that atomicity is real -- there is no orphaned-file cleanup job in this system because
        // there can be no orphaned file. Validation (allow-list, size cap, file name) happens in there and
        // throws AppException(422), which rolls the scope back on the way out.
        var stored = await _documents.StoreAsync(
            new StoreDocumentRequest(
                ticket.Id,

                // The TICKET'S Customer. user.CustomerId is null for an Accountant, which would make the
                // row either a NOT NULL violation or -- worse -- silently attributed to the wrong party.
                ticket.CustomerId,
                origin,
                req.FileName,
                req.DeclaredContentType,
                req.Content,
                callerAccountId), ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.DocumentUploaded,
            AuditTargets.Document,
            stored.Id.ToString(),
            ticket.CustomerId,
            After: new
            {
                TicketId = ticket.Id,
                stored.OriginalFileName,

                // The SNIFFED type, which is what was stored -- not req.DeclaredContentType, which is
                // whatever the client claimed. An audit entry recording the claim rather than the finding
                // would be evidence of the wrong fact.
                stored.ContentType,
                stored.SizeBytes,
                stored.Origin,
            }), ct);

        await _transaction.CommitAsync(ct);

        var uploader = await _identity.FindAsync(callerAccountId, ct);

        return ToDto(stored, uploader?.DisplayName);
    }

    /// <summary>
    /// One <c>DocumentSummary</c> as the slice's own DTO. Shared with
    /// <see cref="ListTicketDocumentsHandler"/> so the upload response and a list row cannot drift.
    ///
    /// <c>CustomerId</c> is deliberately dropped: a Customer-side caller already knows theirs, and it is
    /// not a fact about the document that a ticket screen needs.
    /// </summary>
    internal static TicketDocumentDto ToDto(DocumentSummary document, string? uploadedByName) => new()
    {
        Id = document.Id,
        TicketId = document.TicketId,
        FileName = document.OriginalFileName,
        ContentType = document.ContentType,
        SizeBytes = document.SizeBytes,
        Origin = document.Origin,
        UploadedByUserAccountId = document.UploadedByUserAccountId,
        UploadedByName = uploadedByName,
        UploadedAt = document.UploadedAt,
    };
}
