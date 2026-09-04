namespace AccountantApp.Api.Slices.Notifications.Core;

public sealed class OutboxEntry
{
    public Guid Id { get; set; }
    public Guid NotificationId { get; set; }
    public string? ResolvedEmail { get; set; }

    /// <summary>Secret-bearing email body. Null means "email the notification's body".
    /// Set to null again on successful send — see the drainer rules.</summary>
    public string? EmailBody { get; set; }

    public string Status { get; set; } = OutboxStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

public static class OutboxStatus
{
    public const string Pending   = "Pending";
    public const string Sent      = "Sent";
    public const string Failed    = "Failed";     // transient; will be retried
    public const string Abandoned = "Abandoned";  // attempt cap reached; never retried
    public const string Skipped   = "Skipped";    // no address, or email disabled
}
