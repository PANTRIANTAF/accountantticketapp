using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Slices.Notifications.Core;
using AccountantApp.Api.Slices.Notifications.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AccountantApp.Api.Slices.Notifications.ExternalInterfaces;

internal sealed class NotificationApi : INotificationApi
{
    // Column widths from 20260830_001_CreateNotificationsSchema.sql.
    private const int TitleMaxLength = 200;
    private const int BodyMaxLength = 2000;
    private const int EmailBodyMaxLength = 4000;

    private readonly NotificationsDbContext _db;
    private readonly IRequestTransaction _transaction;
    private readonly IServiceProvider _serviceProvider;

    public NotificationApi(
        NotificationsDbContext db,
        IRequestTransaction transaction,
        IServiceProvider serviceProvider)
    {
        _db = db;
        _transaction = transaction;
        _serviceProvider = serviceProvider;
    }

    public async Task<Guid> NotifyAsync(NotificationRequest request, CancellationToken ct)
    {
        ValidateRequest(request);
        await _transaction.EnlistAsync(_db, ct);

        // Rule E applies to both write paths. It used to be here only in NotifyManyAsync, so
        // NotifyAsync(self) created a row and NotifyManyAsync([self]) did not -- the same event
        // behaving two ways depending on which overload the caller reached for.
        if (request.RecipientUserId == CurrentUserId())
            return Guid.Empty;

        var notification = Enqueue(request);

        await _db.SaveChangesAsync(ct);
        return notification.Id;
    }

    public async Task<int> NotifyManyAsync(IReadOnlyCollection<NotificationRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0)
            return 0;

        foreach (var req in requests)
            ValidateRequest(req);

        await _transaction.EnlistAsync(_db, ct);

        var currentUserId = CurrentUserId();

        // Collapse duplicate (RecipientUserId, EventKind, TicketId) after filtering self-notifications
        var filtered = requests
            .Where(r => currentUserId is null || r.RecipientUserId != currentUserId)
            .ToList();

        var uniqueKey = new HashSet<(string, string, Guid?)>();
        var deduped = new List<NotificationRequest>();

        foreach (var req in filtered)
        {
            var key = (req.RecipientUserId, req.EventKind, req.TicketId);
            if (uniqueKey.Add(key))
            {
                deduped.Add(req);
            }
        }

        int created = 0;

        foreach (var request in deduped)
        {
            Enqueue(request);
            created++;
        }

        if (created > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return created;
    }

    // Adds the notification, and an outbox row when the kind is emailed. Shared by both write paths
    // so they cannot drift apart in what they persist.
    private Notification Enqueue(NotificationRequest request)
    {
        var now = DateTimeOffset.UtcNow;

        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            RecipientUserId = request.RecipientUserId,
            EventKind = request.EventKind,
            Title = TruncateRequired(request.Title, TitleMaxLength),
            Body = TruncateRequired(request.Body, BodyMaxLength),
            TicketId = request.TicketId,
            CreatedAt = now
        };

        _db.Notifications.Add(notification);

        if (NotificationEvents.Emailed.Contains(request.EventKind))
        {
            _db.Outbox.Add(new OutboxEntry
            {
                Id = Guid.NewGuid(),
                NotificationId = notification.Id,
                EmailBody = Truncate(request.EmailBody, EmailBodyMaxLength),
                Status = OutboxStatus.Pending,
                AttemptCount = 0,
                NextAttemptAt = now,
                CreatedAt = now
            });
        }

        return notification;
    }

    // CurrentUser is absent on the login and password-reset paths, where CurrentUserFactory throws
    // rather than returning null. Swallowed on purpose: an unauthenticated event still has to
    // deliver, it just has nobody to compare the recipient against.
    private string? CurrentUserId()
    {
        try
        {
            return _serviceProvider.GetService<CurrentUser>()?.Id;
        }
        catch
        {
            return null;
        }
    }

    private static void ValidateRequest(NotificationRequest request)
    {
        // These three are caller bugs -- a wrong constant or an unset field -- so they throw. The
        // length limits below are not: they are data, and a caller passing a long title is not
        // making a mistake it could have avoided.
        if (string.IsNullOrWhiteSpace(request.RecipientUserId))
            throw new InvalidOperationException("RecipientUserId cannot be null, empty, or whitespace.");

        if (!NotificationEvents.All.Contains(request.EventKind))
            throw new InvalidOperationException($"Unknown EventKind: {request.EventKind}");

        if (request.EmailBody is not null && !NotificationEvents.Emailed.Contains(request.EventKind))
            throw new InvalidOperationException($"EmailBody is set but EventKind '{request.EventKind}' is not emailed.");
    }

    // Title and EmailBody used to throw when they exceeded the column width. This runs inside the
    // caller's transaction, so a 201-character title rolled back the ticket transition that raised
    // the notification and answered 500 -- and a 200-character title is reachable from a
    // TicketType DisplayName, which the TicketTypes slice allows 255 characters. Truncating loses
    // the tail of a title; throwing loses the whole business operation. See Correction Note N-1.
    private static string TruncateRequired(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null : TruncateRequired(value, maxLength);
}
