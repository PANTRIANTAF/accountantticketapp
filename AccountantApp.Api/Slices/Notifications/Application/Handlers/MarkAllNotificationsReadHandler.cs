using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Notifications.Application.Dtos;
using AccountantApp.Api.Slices.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Notifications.Application.Handlers;

public sealed class MarkAllNotificationsReadHandler
{
    private readonly NotificationsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;

    public MarkAllNotificationsReadHandler(
        NotificationsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
    }

    public async Task<MarkReadResponseDto> Handle(CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "MarkOwnNotificationRead", ct: ct);

        // Owns the transaction rather than only enlisting: EnlistAsync is a no-op when the caller has
        // no ambient transaction, which is always the case on this endpoint.
        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        // One UPDATE, not a read-then-mutate. Materialising every unread row cost a heavy user
        // thousands of tracked entities to set two columns, and ExecuteUpdateAsync returns the
        // affected count directly. It bypasses the change tracker, so it must run inside the
        // transaction above rather than alongside a SaveChangesAsync.
        var readAt = DateTimeOffset.UtcNow;
        var markedCount = await _db.Notifications
            .Where(n => n.RecipientUserId == user.Id && !n.IsRead)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(n => n.IsRead, true)
                    .SetProperty(n => n.ReadAt, readAt),
                ct);

        await _transaction.CommitAsync(ct);

        return new MarkReadResponseDto { MarkedCount = markedCount };
    }
}
