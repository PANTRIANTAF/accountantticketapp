namespace AccountantApp.Api.Slices.Notifications.Core;

public sealed class Notification
{
    public Guid Id { get; set; }
    public string RecipientUserId { get; set; } = string.Empty;
    public Guid? TicketId { get; set; }
    public string EventKind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
