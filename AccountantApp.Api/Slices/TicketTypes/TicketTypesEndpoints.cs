using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.TicketTypes.Application.Dtos;
using AccountantApp.Api.Slices.TicketTypes.Application.Handlers;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using Microsoft.AspNetCore.Mvc;
using ExternalTicketTypeListItemDto = AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces.TicketTypeListItemDto;

namespace AccountantApp.Api.Slices.TicketTypes;

public static class TicketTypesEndpoints
{
    public static void MapTicketTypesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ticket-types").WithTags("TicketTypes");

        group.MapPost("/create", async (CreateTicketTypeRequestDto req, CreateTicketTypeHandler handler,
                CurrentUser user, CancellationToken ct) =>
            {
                var result = await handler.Handle(req, user, ct);
                return Results.Created($"/api/ticket-types/detail?ticketTypeId={result.Id}", result);
            })
            .WithName("CreateTicketType")
            .Produces<TicketTypeDetailDto>(201)
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/edit", async (EditTicketTypeRequestDto req, EditTicketTypeHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("EditTicketType")
            .Produces<TicketTypeDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);

        group.MapPost("/toggle", async (ToggleTicketTypeRequestDto req, ToggleTicketTypeHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("ToggleTicketType")
            .Produces<TicketTypeDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404);

        group.MapGet("/list", async (int? pageNumber, int? pageSize, bool? activeOnly,
                ListTicketTypesHandler handler, CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(new ListTicketTypesRequestDto
            {
                PageNumber = pageNumber ?? 1,
                PageSize = pageSize ?? PaginatedQuery.DefaultPageSize,
                ActiveOnly = activeOnly
            }, user, ct)))
            .WithName("ListTicketTypes")
            .Produces<PaginatedResponse<ExternalTicketTypeListItemDto>>();

        group.MapGet("/detail", async (Guid ticketTypeId, GetTicketTypeHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(new GetTicketTypeRequestDto { TicketTypeId = ticketTypeId }, user, ct)))
            .WithName("GetTicketType")
            .Produces<TicketTypeDetailDto>()
            .Produces<ProblemDetails>(404);

        group.MapGet("/version", async (Guid ticketTypeId, int versionNumber,
                GetTicketTypeVersionHandler handler, CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(new GetTicketTypeVersionRequestDto
            {
                TicketTypeId = ticketTypeId,
                VersionNumber = versionNumber
            }, user, ct)))
            .WithName("GetTicketTypeVersion")
            .Produces<TicketTypeDetailDto>()
            .Produces<ProblemDetails>(404);
    }
}