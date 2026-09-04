using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.Application.Dtos;
using AccountantApp.Api.Slices.Notifications.Core;
using AccountantApp.Api.Slices.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Notifications.Application.Handlers;

public sealed class MarkNotificationsReadHandler
{
    private const int MaxIdsPerRequest = 200;

    private readonly NotificationsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public MarkNotificationsReadHandler(
        NotificationsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
    }

    public async Task<MarkReadResponseDto> Handle(
        MarkReadRequestDto req,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "MarkOwnNotificationRead", ct: ct);

        // AppException, not ArgumentException: only AppException carries a status code through
        // AppExceptionMiddleware, so a client posting {"notificationIds": []} used to get a 500.
        if (req.NotificationIds is null || req.NotificationIds.Count == 0)
            throw new AppException("NotificationIds cannot be empty.", 422);

        var idsToMark = req.NotificationIds.Distinct().ToList();

        // Cap the batch before it reaches the query. Without this an unauthenticated-cheap request
        // body of 100,000 ids becomes a single enormous IN list.
        if (idsToMark.Count > MaxIdsPerRequest)
            throw new AppException($"No more than {MaxIdsPerRequest} notifications can be marked in one request.", 422);

        // Own the transaction rather than only enlisting: with no ambient transaction EnlistAsync
        // returns immediately, which left the update and the audit entry below independently
        // committable.
        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        var owned = await _db.Notifications
            .Where(n => n.RecipientUserId == user.Id && idsToMark.Contains(n.Id))
            .ToListAsync(ct);

        var newlyRead = owned.Where(n => !n.IsRead).ToList();
        foreach (var notification in newlyRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTimeOffset.UtcNow;
        }

        int markedCount = newlyRead.Count;

        // One audited denial per operation, not per id (BLOCKERS_RESOLVED N-1). The action must be
        // an AuditActions member, not the IActionCatalogue permission name checked above: AuditApi
        // rejects anything outside AuditActions.All, so passing "MarkOwnNotificationRead" here
        // threw and turned a partially-satisfiable request into a 500. A caller reaching for
        // another recipient's notification is a scope violation, which is PermissionDenied.
        //
        // Audited before SaveChangesAsync so both land in the caller's transaction and neither can
        // be committed without the other. The previous order saved first, so when the audit threw
        // the reads stayed marked while the client saw a 500.
        //
        // The comparison is on ownership, not on how many rows changed. Comparing marked-vs-asked
        // treated re-marking your own already-read notification as a scope violation, so an ordinary
        // duplicate click manufactured a Denied security event against an innocent user -- and
        // "Denied" stopped meaning anything an investigator could rely on.
        if (owned.Count < idsToMark.Count)
        {
            await _audit.LogAsync(new(
                Action: AuditActions.PermissionDenied,
                TargetKind: AuditTargets.Notification,
                TargetId: "batch",
                Outcome: AuditOutcome.Denied), ct);
        }

        await _db.SaveChangesAsync(ct);
        await _transaction.CommitAsync(ct);

        return new MarkReadResponseDto { MarkedCount = markedCount };
    }
}
