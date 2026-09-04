namespace AccountantApp.Api.Slices.Notifications.Application.Dtos;

public sealed class NotificationDto
{
    public Guid Id { get; set; }
    public Guid? TicketId { get; set; }
    public string EventKind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Projected from the outbox row: Pending, Sent, Failed, Abandoned, Skipped, or null (not emailed).</summary>
    public string? EmailStatus { get; set; }
}
