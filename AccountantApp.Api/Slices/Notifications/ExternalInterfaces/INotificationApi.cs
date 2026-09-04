namespace AccountantApp.Api.Slices.Notifications.ExternalInterfaces;

public sealed record NotificationRequest(
    string RecipientUserId,
    string EventKind,
    string Title,
    string Body,
    Guid? TicketId = null,

    /// <summary>
    /// Set this ONLY when the email must say something the stored notification must not — in
    /// practice, when it carries a single-use token link. When set, <paramref name="Body"/> is
    /// what gets stored and shown in the app, and this is what gets emailed. When null, the
    /// body is used for both.
    /// </summary>
    string? EmailBody = null);

public interface INotificationApi
{
    /// <summary>
    /// Creates one notification and, when the kind is emailed, its outbox row — both inside
    /// the caller's transaction. Returns the notification id.
    /// </summary>
    Task<Guid> NotifyAsync(NotificationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Creates many in one call. Use this rather than a loop of NotifyAsync: one round trip,
    /// one SaveChanges, and duplicate recipients are collapsed.
    /// </summary>
    Task<int> NotifyManyAsync(IReadOnlyCollection<NotificationRequest> requests,
                              CancellationToken ct = default);
}
