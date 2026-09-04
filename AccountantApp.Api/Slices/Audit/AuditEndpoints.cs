using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Audit.Application.Dtos;
using AccountantApp.Api.Slices.Audit.Application.Handlers;
using Microsoft.AspNetCore.Mvc;

namespace AccountantApp.Api.Slices.Audit;

public static class AuditEndpoints
{
    // No .RequireAuthorization(): authorization is IPermissionChecker inside the handler, and the
    // application registers no authorization middleware at all -- metadata without middleware makes
    // every route in the group throw. Two mechanisms would mean two places to get it wrong.
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit").WithTags("Audit");

        // POST for a read, deliberately: eight optional filters plus paging, and date ranges in a
        // query string invite encoding bugs. It opens no transaction and audits nothing.
        group.MapPost("/search", async (SearchAuditLogRequestDto req, SearchAuditLogHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("SearchAuditLog")
            .Produces<PaginatedResponse<AuditEntryDto>>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(422);

        group.MapGet("/detail", async (Guid auditEntryId, GetAuditEntryHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(
                new GetAuditEntryRequestDto { AuditEntryId = auditEntryId }, user, ct)))
            .WithName("GetAuditEntry")
            .Produces<AuditEntryDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404);

        // Kebab-case at the word boundary: /api/audit/actioncodes is the doubled-consonant typo the
        // route rule exists to prevent.
        group.MapGet("/action-codes", async (ListAuditActionsHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(user, ct)))
            .WithName("ListAuditActions")
            .Produces<AuditActionsResponseDto>()
            .Produces<ProblemDetails>(403);
    }
}
