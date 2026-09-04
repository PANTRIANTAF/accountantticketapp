using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Slices.Notifications.Application.Dtos;
using AccountantApp.Api.Slices.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Notifications.Application.Handlers;

public sealed class GetUnreadCountHandler
{
    private readonly NotificationsDbContext _db;
    private readonly IPermissionChecker _permissions;

    public GetUnreadCountHandler(
        NotificationsDbContext db,
        IPermissionChecker permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public async Task<UnreadCountResponseDto> Handle(CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ReadOwnNotifications", ct: ct);

        var unreadCount = await _db.Notifications.AsNoTracking()
            .Where(n => n.RecipientUserId == user.Id && !n.IsRead)
            .CountAsync(ct);

        return new UnreadCountResponseDto { UnreadCount = unreadCount };
    }
}
