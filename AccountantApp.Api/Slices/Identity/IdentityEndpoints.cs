using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Application.Handlers;
using Microsoft.AspNetCore.Mvc;

namespace AccountantApp.Api.Slices.Identity;

public static class IdentityEndpoints
{
    public static void MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        MapAuthEndpoints(app);
        MapAccountantEndpoints(app);
    }

    /// <summary>
    /// Two groups, not one, and NOT because of authorization -- there is no .RequireAuthorization()
    /// anywhere here. Authentication is enforced by taking a CurrentUser parameter, whose factory throws
    /// 401 when there is no principal; the four anonymous endpoints simply do not take one.
    ///
    /// The pipeline has no authorization middleware, so .RequireAuthorization() and [AllowAnonymous]
    /// would both be inert. Do not add them: a guard that silently does nothing is worse than no guard,
    /// because it reads like protection.
    /// </summary>
    private static void MapAuthEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        // --- Anonymous by construction: no CurrentUser parameter ---

        group.MapPost("/login", async (LoginRequestDto req, LoginHandler handler,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, ct)))
            .WithName("Login")
            .Produces<SessionDto>()
            // One 401 for every failure cause. There is no 403 and no 404 here on purpose: a distinct
            // status per cause is an account-enumeration oracle.
            .Produces<ProblemDetails>(401)
            .Produces<ProblemDetails>(422);

        group.MapPost("/request-password-reset", async (RequestPasswordResetRequestDto req,
                RequestPasswordResetHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, ct)))
            .WithName("RequestPasswordReset")
            // 200 ONLY. No 404 and no 422 are declared because none can be returned: an unknown address
            // gets the same 200 as a known one.
            .Produces<MarkedResultDto>();

        group.MapPost("/complete-password-reset", async (CompletePasswordResetRequestDto req,
                CompletePasswordResetHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, ct)))
            .WithName("CompletePasswordReset")
            .Produces<MarkedResultDto>()
            // 400 for an invalid, expired, consumed or wrong-purpose token -- all one message.
            .Produces<ProblemDetails>(400)
            .Produces<ProblemDetails>(422);

        group.MapPost("/accept-invitation", async (AcceptInvitationRequestDto req,
                AcceptInvitationHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, ct)))
            .WithName("AcceptInvitation")
            .Produces<MarkedResultDto>()
            .Produces<ProblemDetails>(400)
            .Produces<ProblemDetails>(422);

        // --- Authenticated by construction: they take CurrentUser ---

        group.MapPost("/logout", async (LogoutHandler handler, CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(user, ct)))
            .WithName("Logout")
            .Produces<MarkedResultDto>()
            .Produces<ProblemDetails>(401);

        // GET, and the one endpoint the front end calls on every page load.
        group.MapGet("/me", async (GetCurrentSessionHandler handler, CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(user, ct)))
            .WithName("GetCurrentSession")
            .Produces<SessionDto>()
            .Produces<ProblemDetails>(401);

        group.MapPost("/change-password", async (ChangePasswordRequestDto req,
                ChangeOwnPasswordHandler handler, CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("ChangeOwnPassword")
            .Produces<MarkedResultDto>()
            .Produces<ProblemDetails>(401)
            .Produces<ProblemDetails>(422);
    }

    private static void MapAccountantEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accountants").WithTags("Accountants");

        group.MapPost("/invite", async (InviteAccountantRequestDto req,
                InviteAccountantHandler handler, CurrentUser user, CancellationToken ct) =>
            {
                var result = await handler.Handle(req, user, ct);
                return Results.Created($"/api/accountants/list", result);
            })
            .WithName("InviteAccountant")
            .Produces<AccountantDetailDto>(201)
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapGet("/list", async (int? pageNumber, int? pageSize,
                ListAccountantsHandler handler, CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(new ListAccountantsRequestDto
            {
                PageNumber = pageNumber ?? 1,
                PageSize = pageSize ?? PaginatedQuery.DefaultPageSize
            }, user, ct)))
            .WithName("ListAccountants")
            // Documents the RICHER of the two shapes. The handler returns object, so an AccountantUser
            // actually receives PaginatedResponse<AccountantSummaryDto> -- two fields per row, with no
            // loginEmail key present at all. The declaration here is for OpenAPI; it does not change what
            // is serialised, and it must not be used to infer the response shape for a non-Admin caller.
            .Produces<PaginatedResponse<AccountantDetailDto>>()
            .Produces<ProblemDetails>(403);

        // Four AccountantAdmin-only mutations, all POST with the id in the body. Not DELETE and not PUT:
        // an account is never deleted, and these are named transitions rather than replacements.
        group.MapPost("/suspend", async (AccountIdRequestDto req, SuspendAccountantHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("SuspendAccountant")
            .Produces<AccountantDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            // 422 covers three distinct refusals: already suspended, acting on oneself, and removing the
            // last active Admin.
            .Produces<ProblemDetails>(422);

        group.MapPost("/reactivate", async (AccountIdRequestDto req, ReactivateAccountantHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("ReactivateAccountant")
            .Produces<AccountantDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);

        group.MapPost("/promote", async (AccountIdRequestDto req, PromoteAccountantHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("PromoteAccountant")
            .Produces<AccountantDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);

        group.MapPost("/demote", async (AccountIdRequestDto req, DemoteAccountantHandler handler,
                CurrentUser user, CancellationToken ct) =>
            Results.Ok(await handler.Handle(req, user, ct)))
            .WithName("DemoteAccountant")
            .Produces<AccountantDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);
    }
}
