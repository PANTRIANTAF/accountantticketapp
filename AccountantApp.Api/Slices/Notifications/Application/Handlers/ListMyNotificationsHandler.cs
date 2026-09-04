using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Notifications.Application.Dtos;
using AccountantApp.Api.Slices.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;
using AccountantApp.Api.Shared.Errors;

namespace AccountantApp.Api.Slices.Notifications.Application.Handlers;

public sealed class ListMyNotificationsHandler
{
    private readonly NotificationsDbContext _db;
    private readonly IPermissionChecker _permissions;

    public ListMyNotificationsHandler(
        NotificationsDbContext db,
        IPermissionChecker permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public async Task<PaginatedResponse<NotificationDto>> Handle(
        ListMyNotificationsRequestDto req,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ReadOwnNotifications", ct: ct);

        var (pageNumber, pageSize) = PaginatedQuery.Normalize(req.PageNumber, req.PageSize);

        var query = _db.Notifications.AsNoTracking()
            .Where(n => n.RecipientUserId == user.Id);

        if (req.UnreadOnly)
        {
            query = query.Where(n => !n.IsRead);
        }

        var totalCount = await query.CountAsync(ct);

        var notifications = await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        // EmailStatus for the whole page in one round trip, over idx_outbox_notification. This was
        // a query per row inside the loop below, so a default page cost 16 round trips instead of
        // two and the cost grew with PageSize.
        var pageIds = notifications.Select(n => n.Id).ToList();

        var emailStatuses = await _db.Outbox.AsNoTracking()
            .Where(o => pageIds.Contains(o.NotificationId))
            .Select(o => new { o.NotificationId, o.Status })
            .ToDictionaryAsync(o => o.NotificationId, o => o.Status, ct);

        var dtos = new List<NotificationDto>(notifications.Count);
        foreach (var n in notifications)
        {
            dtos.Add(new NotificationDto
            {
                Id = n.Id,
                TicketId = n.TicketId,
                EventKind = n.EventKind,
                Title = n.Title,
                Body = n.Body,
                IsRead = n.IsRead,
                ReadAt = n.ReadAt,
                CreatedAt = n.CreatedAt,
                // Absent for a notification whose event kind is not emailed, and after the drainer
                // clears a sent entry; null means "no email was ever queued for this".
                EmailStatus = emailStatuses.GetValueOrDefault(n.Id)
            });
        }

        return new PaginatedResponse<NotificationDto>
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Items = dtos
        };
    }
}
