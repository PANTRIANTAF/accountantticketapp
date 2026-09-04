using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Pagination;
// ExternalInterfaces ONLY -- dependency rule 2. This file used to import Documents.Application for the
// upload size cap and the download header names; both now live in Documents/ExternalInterfaces.
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Application.Handlers;
using Microsoft.AspNetCore.Mvc;

namespace AccountantApp.Api.Slices.Tickets;

/// <summary>
/// Plan §8. TWO route groups, and the second belongs to another slice's domain.
///
/// Everything is POST, including the reads (rule 4), every multi-word segment is kebab-case (rule 2,
/// LOCKED), and there are NO ROUTE PARAMETERS (rule 3) -- an identifier is not an action, so ids go in the
/// body. There is no DELETE endpoint and no /api/tickets/reopen (rule 5): matrix §7 gives both to Nobody,
/// and cancellation is a status.
///
/// NO try/catch ANYWHERE IN THIS FILE. Every handler throws <c>AppException</c>, and
/// <c>AppExceptionMiddleware</c> turns it into the ProblemDetails response with the right status code. A
/// catch here would produce a second, inconsistent error shape for one route.
/// </summary>
public static class TicketsEndpoints
{
    public static void MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        MapTicketRoutes(app);
        MapDocumentRoutes(app);
    }

    /// <summary>Plan §8.1. Eighteen routes, eighteen of the slice's twenty-two actions.</summary>
    private static void MapTicketRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets").WithTags("Tickets");

        group.MapPost("/create", async (CreateTicketRequestDto req, CreateTicketHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("CreateTicket")

            // 200 rather than 201 with a Location header. Every route in this application is a POST,
            // including the reads, so a Location pointing at /api/tickets/get -- which cannot be followed
            // with a GET -- would be a link nothing can dereference.
            .Produces<TicketDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);

        group.MapPost("/submit", async (SubmitTicketRequestDto req, SubmitTicketHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("SubmitTicket")
            .Produces<TicketStateDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/list", async (ListTicketsRequestDto req, ListTicketsHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("ListTickets")
            .Produces<PaginatedResponse<TicketListItemDto>>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(422);

        group.MapPost("/get", async (GetTicketRequestDto req, GetTicketHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("GetTicket")

            // §8 rule 8: this route returns DIFFERENT SHAPES BY ROLE and the declaration cannot say so.
            // A Customer-side caller never receives an Accountant-only field descriptor, an
            // Accountant-only field value, an InternalNote message, or CustomerName; an Accountant
            // receives all of them. It is ONE type with fewer members populated, not two types -- the
            // narrowing is done by TicketMapper.ToDetail from the caller's role (§4.3 rule 5), so an
            // OpenAPI consumer must treat every one of those members as optional.
            .Produces<TicketDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);

        group.MapPost("/pickup-queue", async (ListPickupQueueRequestDto req,
                ListPickupQueueHandler handler, CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("ListPickupQueue")
            .Produces<PaginatedResponse<TicketListItemDto>>()
            .Produces<ProblemDetails>(403);

        group.MapPost("/submit-revision", async (SubmitRevisionRequestDto req,
                SubmitRevisionHandler handler, CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("SubmitRevision")
            .Produces<RevisionSubmittedDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)

            // 409 twice over: a stale Version, and a lost race on uq_ticket_revisions_sequence between two
            // corrections submitted at the same moment (§4.5).
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/verify-field", async (VerifyFieldRequestDto req, VerifyFieldHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("VerifyField")
            .Produces<FieldVerifiedDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/set-priority", async (SetTicketPriorityRequestDto req, SetPriorityHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("SetTicketPriority")
            .Produces<TicketStateDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/set-due-date", async (SetTicketDueDateRequestDto req, SetDueDateHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("SetTicketDueDate")
            .Produces<TicketStateDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/pickup", async (PickupTicketRequestDto req, PickupTicketHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("PickupTicket")
            .Produces<TicketStateDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)

            // The one a client MUST handle gracefully: two Accountants taking the same queued ticket, where
            // the loser's 409 means "somebody else has it now", not "you did something wrong".
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/assign", async (AssignTicketRequestDto req, AssignTicketHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("AssignTicket")
            .Produces<TicketStateDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/request-information", async (RequestInformationRequestDto req,
                RequestInformationHandler handler, CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("RequestInformation")
            .Produces<TicketStateDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/answer", async (AnswerTicketRequestDto req, AnswerTicketHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("AnswerTicket")
            .Produces<TicketStateDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/close", async (CloseTicketRequestDto req, CloseTicketHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("CloseTicket")
            .Produces<TicketStateDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/return-to-review", async (ReturnToReviewRequestDto req,
                ReturnToReviewHandler handler, CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("ReturnTicketToReview")
            .Produces<TicketStateDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/post-message", async (PostMessageRequestDto req, PostMessageHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("PostMessage")
            .Produces<MessagePostedDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        // The SAME handler class as /post-message, through its other entry point. Two routes and two
        // ACTIONS: the second is Accountants-only in the catalogue, which is what denies it to a
        // Customer-side caller -- there is no role branch inside the handler (§4.10 rule 3). Merging these
        // two routes into one with a "kind" in the body would let a Customer post an internal note.
        group.MapPost("/post-internal-note", async (PostMessageRequestDto req, PostMessageHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.HandleInternalNote(req, user, ct)))
            .WithName("PostInternalNote")
            .Produces<MessagePostedDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/cancel", async (CancelTicketRequestDto req, CancelTicketHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("CancelTicket")
            .Produces<TicketStateDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);
    }

    /// <summary>
    /// Plan §8.2. THESE FOUR ROUTES BELONG TO THE DOCUMENTS DOMAIN AND ARE REGISTERED HERE ON PURPOSE --
    /// see the Documents plan §0.2, decided.
    ///
    /// A document inherits its access rules entirely from its ticket (matrix §8) and authorization must be
    /// re-checked at the moment of download. <c>Documents</c> may depend only on <c>Audit</c>, and
    /// <c>Tickets → Documents</c> already exists, so <c>Documents → Tickets</c> would be a CYCLE: the slice
    /// that owns the bytes cannot evaluate who may read them. Hence <c>IDocumentApi</c> authorizes nothing
    /// and these four routes live in the slice that can.
    ///
    /// DO NOT MOVE THEM INTO A DocumentsEndpoints.cs. It looks like tidying and it creates the cycle. There
    /// is deliberately no <c>MapDocumentEndpoints()</c> anywhere in the solution, which is why
    /// <c>Documents</c> contributes one line to <c>Program.cs</c> and this slice contributes two.
    /// </summary>
    private static void MapDocumentRoutes(IEndpointRouteBuilder app)
    {
        // Tagged "Documents" so the generated API surface groups these with the domain they belong to
        // rather than with the slice that hosts them.
        var group = app.MapGroup("/api/documents").WithTags("Documents");

        group.MapPost("/upload", async (
                IFormFile file,
                [FromForm] Guid ticketId,
                [FromForm] int version,
                UploadDocumentHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            {
                await using var content = file.OpenReadStream();

                return Results.Ok(await handler.Handle(new UploadDocumentRequestDto
                {
                    TicketId = ticketId,
                    Version = version,

                    // The client's own file name and its claimed type. Documents sanitises the first and
                    // SNIFFS the second from the leading bytes -- neither is trusted here, and neither is
                    // an Origin: that comes from the caller's role inside the handler.
                    FileName = file.FileName,
                    DeclaredContentType = file.ContentType,
                    Content = content,
                }, user, ct));
            })
            .WithName("UploadDocument")

            // THE ONE MULTIPART ENDPOINT IN THE SYSTEM (§8.2 rule 6), and the two limits below are the
            // reason this rule is an obligation on Tickets rather than a nicety. Documents enforces the cap
            // when it buffers the bytes, but RequestSizeLimit and MultipartBodyLengthLimit are
            // ENDPOINT-LEVEL knobs and Documents has no endpoints -- it physically cannot set them. Without
            // these, an oversized upload is still refused, but only AFTER ASP.NET has buffered the whole
            // body, which is the denial-of-service shape the limits exist to prevent.
            //
            // THE NUMBER COMES FROM Documents (amended 2026-09-02). DocumentLimits.MaxUploadSizeBytes is
            // the single declaration of this policy; writing 26_214_400 here again would be a second one
            // that nothing keeps in step, and the two would disagree only for uploads sized between them --
            // a quiet failure. The proxy-side third of "enforced at both the proxy and the application"
            // (04-Infrastructure.md §7) is DEFERRED: this repository has no Caddyfile and no deployment
            // layer.
            .WithMetadata(
                new RequestSizeLimitAttribute(DocumentLimits.MaxUploadSizeBytes),
                new RequestFormLimitsAttribute
                {
                    MultipartBodyLengthLimit = DocumentLimits.MaxUploadSizeBytes,
                })

            // Required, not optional: minimal-API form binding demands antiforgery validation, and this
            // application registers no antiforgery services and no UseAntiforgery middleware -- so without
            // this the route throws on every request. The CSRF defence for this endpoint is the auth
            // cookie's SameSite=Strict (IdentityRegistration), which a cross-site form post cannot carry.
            // Reported as a judgment call.
            .DisableAntiforgery()
            .Produces<TicketDocumentDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)

            // 422 covers a rejected content type, an empty body and an oversized file that got past the
            // limits above (a lying Content-Length).
            .Produces<ProblemDetails>(422);

        group.MapPost("/list", async (ListTicketDocumentsRequestDto req,
                ListTicketDocumentsHandler handler, CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("ListTicketDocuments")

            // Unpaginated on purpose: metadata only, bounded by the attachments of ONE ticket.
            .Produces<List<TicketDocumentDto>>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404);

        group.MapPost("/download", async (DownloadDocumentRequestDto req,
                DownloadDocumentHandler handler, HttpResponse response, CurrentUser user,
                CancellationToken ct) =>
            {
                // The handler has already COMMITTED the audit entry by the time it returns (§4.0 D), so
                // nothing below can lose the record of the disclosure -- not a client that disconnects
                // mid-stream, not a write error on the socket.
                var download = await handler.Handle(req, user, ct);

                // Both headers are written explicitly, and both values were decided by Documents'
                // DownloadShaping rather than here. nosniff, because without it a browser may disregard the
                // declared type and sniff the content -- which reintroduces exactly what the attachment
                // disposition prevents. The SPA and the API SHARE AN ORIGIN (01-DomainModel.md §6), so an
                // HTML or SVG file served inline would run script with the session cookie available.
                response.Headers[DownloadHeaders.ContentTypeOptionsHeaderName] =
                    download.ContentTypeOptions;

                // Set directly rather than through Results.File's fileDownloadName, which would re-encode
                // the name with its own rules and lose the RFC 5987 filename* form that makes a Greek file
                // name survive an HTTP header.
                response.Headers.ContentDisposition = download.ContentDisposition;

                return Results.File(download.Content, download.ContentType);
            })
            .WithName("DownloadDocument")

            // Not .Produces<DocumentDownloadDto>: that type never reaches the wire. The body is the bytes.
            .Produces(200, contentType: "application/octet-stream")
            .Produces<ProblemDetails>(403)

            // Includes the §0.3 step 5 case -- a real document id paired with the wrong ticket -- which is
            // deliberately indistinguishable from an id that does not exist.
            .Produces<ProblemDetails>(404);

        group.MapPost("/delete", async (DeleteDocumentRequestDto req, DeleteDocumentHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("DeleteDocument")

            // A SOFT delete, which is why this is a POST returning a body and not an HTTP DELETE returning
            // 204: nothing is removed, a row gains deleted_at, and the response says when.
            .Produces<DocumentDeletedDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);
    }
}
