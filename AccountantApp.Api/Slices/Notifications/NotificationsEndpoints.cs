using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Slices.Notifications.Application.Dtos;
using AccountantApp.Api.Slices.Notifications.Application.Handlers;

namespace AccountantApp.Api.Slices.Notifications;

public static class NotificationsEndpoints
{
    public static void MapNotificationsEndpoints(this WebApplication app)
    {
        // No .RequireAuthorization(): the app registers authentication but never AddAuthorization(),
        // so WebApplication does not auto-insert UseAuthorization() and Program.cs does not add it.
        // Authorization metadata with no middleware to service it makes EndpointMiddleware throw,
        // which turned every route in this group into a 500. Authentication is enforced by
        // CurrentUserFactory (401 when there is no principal) and authorization by IPermissionChecker
        // inside each handler, exactly as the other three slices do it.
        var group = app.MapGroup("/api/notifications");

        group.MapPost("/list", ListNotifications)
            .WithName("ListNotifications")
            .Produces<object>(StatusCodes.Status200OK);

        group.MapGet("/unread-count", GetUnreadCount)
            .WithName("GetUnreadCount")
            .Produces<UnreadCountResponseDto>(StatusCodes.Status200OK);

        group.MapPost("/mark-read", MarkNotificationsRead)
            .WithName("MarkNotificationsRead")
            .Produces<MarkReadResponseDto>(StatusCodes.Status200OK);

        group.MapPost("/mark-all-read", MarkAllNotificationsRead)
            .WithName("MarkAllNotificationsRead")
            .Produces<MarkReadResponseDto>(StatusCodes.Status200OK);
    }

    private static async Task<IResult> ListNotifications(
        ListMyNotificationsRequestDto req,
        CurrentUser user,
        ListMyNotificationsHandler handler,
        CancellationToken ct)
    {
        var result = await handler.Handle(req, user, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetUnreadCount(
        CurrentUser user,
        GetUnreadCountHandler handler,
        CancellationToken ct)
    {
        var result = await handler.Handle(user, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> MarkNotificationsRead(
        MarkReadRequestDto req,
        CurrentUser user,
        MarkNotificationsReadHandler handler,
        CancellationToken ct)
    {
        var result = await handler.Handle(req, user, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> MarkAllNotificationsRead(
        CurrentUser user,
        MarkAllNotificationsReadHandler handler,
        CancellationToken ct)
    {
        var result = await handler.Handle(user, ct);
        return Results.Ok(result);
    }
}
