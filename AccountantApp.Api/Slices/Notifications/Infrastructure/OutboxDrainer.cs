using AccountantApp.Api.Slices.Notifications.Application;
using AccountantApp.Api.Slices.Notifications.Core;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AccountantApp.Api.Slices.Notifications.Infrastructure;

/// <summary>
/// Background service that drains pending outbox entries and sends emails.
///
/// Creates a fresh scope per iteration, which gives it its own RequestConnection and so its own
/// physical connection with no ambient transaction, and avoids accumulated DbContext state.
///
/// TOPOLOGY CONSTRAINT: this must run in exactly one replica. Entries are claimed with a plain
/// SELECT and the row is only marked after the send returns, with no FOR UPDATE SKIP LOCKED and no
/// lease column, so two instances polling the same table will both claim the same due entry and
/// send the same email twice. Scaling the API horizontally requires adding claim locking first.
/// See BLOCKERS_RESOLVED N-7.
/// </summary>
public sealed class OutboxDrainer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDrainer> _logger;
    private readonly OutboxDrainerOptions _options;

    // Backoff: 1m, 5m, 15m, 1h, 6h (attempt cap 6)
    private static readonly TimeSpan[] Backoff = [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6)
    ];

    public OutboxDrainer(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxDrainer> logger,
        OutboxDrainerOptions options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Notification outbox drainer started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainBatch(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox drain iteration failed.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Notification outbox drainer stopped.");
    }

    private async Task DrainBatch(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var directory = scope.ServiceProvider.GetRequiredService<IRecipientDirectory>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        var now = DateTimeOffset.UtcNow;

        // Join to notifications to get Title and Body (BLOCKERS N-6)
        var due = await db.Outbox.AsNoTracking()
            .Where(o => o.Status == OutboxStatus.Pending && o.NextAttemptAt <= now)
            .Join(db.Notifications.AsNoTracking(),
                  o => o.NotificationId,
                  n => n.Id,
                  (outbox, notification) => new { outbox, notification })
            .OrderBy(x => x.outbox.NextAttemptAt)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        // Process each entry individually (N-11: per-entry try/catch, per-entry save)
        foreach (var item in due)
        {
            try
            {
                await ProcessEntry(db, directory, sender, item.outbox, item.notification, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The entry has to be recorded as a failed attempt, not just logged. Logging alone
                // left attempt_count unchanged and next_attempt_at in the past, so the row stayed
                // Pending and was re-attempted every poll interval forever -- the unbounded-retry
                // self-DoS section 5.4 rule 5 forbids -- and could never reach Abandoned.
                //
                // The exception type is logged without its message, and last_error gets a fixed
                // string: a transport that echoes the request back in its error text would
                // otherwise put a single-use invitation or reset token into logs and into a table
                // nothing purges. See BLOCKERS_RESOLVED N-8.
                _logger.LogError(
                    "Outbox entry {EntryId} threw {ExceptionType}; recorded as a transient failure. " +
                    "Message suppressed because it can echo the email body.",
                    item.outbox.Id,
                    ex.GetType().FullName);

                try
                {
                    await RecordTransientFailure(db, item.outbox, "Unhandled exception while sending.", now, ct);
                }
                catch (Exception saveFailure)
                {
                    // Nothing further to do for this entry; the next iteration re-reads it as due.
                    _logger.LogError(
                        saveFailure, "Could not record the failure of outbox entry {EntryId}.", item.outbox.Id);
                }
            }
        }
    }

    private async Task RecordTransientFailure(
        NotificationsDbContext db,
        OutboxEntry entry,
        string error,
        DateTimeOffset now,
        CancellationToken ct)
    {
        entry.AttemptCount++;

        if (entry.AttemptCount >= _options.MaxAttempts)
        {
            entry.Status = OutboxStatus.Abandoned;
            entry.EmailBody = null; // Clear secret on abandoned (N-9)
        }
        else
        {
            entry.NextAttemptAt = now.Add(BackoffFor(entry.AttemptCount));
        }

        entry.LastError = TruncateError(error);
        db.Outbox.Update(entry);
        await db.SaveChangesAsync(ct);
    }

    private async Task ProcessEntry(
        NotificationsDbContext db,
        IRecipientDirectory directory,
        IEmailSender sender,
        OutboxEntry entry,
        Notification notification,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // 1. Resolve recipient
        var recipient = await directory.FindAsync(notification.RecipientUserId, ct);

        if (recipient is null)
        {
            entry.Status = OutboxStatus.Skipped;
            entry.LastError = "No such account";
            entry.EmailBody = null; // Clear secret (N-9)
            db.Outbox.Update(entry);
            await db.SaveChangesAsync(ct);
            return;
        }

        // Check if suspended (exception for invitations per plan §5.4 rule 4)
        var isInvitation = notification.EventKind is NotificationEvents.Invited
                                                    or NotificationEvents.EmployeeInvited;
        if (!recipient.IsActive && !isInvitation)
        {
            entry.Status = OutboxStatus.Skipped;
            entry.LastError = "Recipient is not active";
            entry.EmailBody = null; // Clear secret (N-9)
            db.Outbox.Update(entry);
            await db.SaveChangesAsync(ct);
            return;
        }

        // 2. Check email disabled
        if (!_options.Enabled)
        {
            entry.Status = OutboxStatus.Skipped;
            entry.LastError = "Email delivery disabled by configuration";
            entry.EmailBody = null; // Clear secret (N-9)
            db.Outbox.Update(entry);
            await db.SaveChangesAsync(ct);
            return;
        }

        // 3. Build message (use EmailBody if set, else Body from notification)
        var emailBody = entry.EmailBody ?? notification.Body;
        var message = new EmailMessage(
            To: recipient.Email,
            Subject: notification.Title,
            Body: emailBody);

        // 4. Send (BLOCKERS N-6: use both Title and Body)
        var result = await sender.SendAsync(message, ct);

        // 5. Handle outcome
        if (result.Outcome == EmailSendOutcome.Sent)
        {
            entry.Status = OutboxStatus.Sent;
            entry.SentAt = DateTimeOffset.UtcNow;
            entry.ResolvedEmail = recipient.Email;
            entry.EmailBody = null; // Clear secret on success (N-9, §5.4 rule 10)
        }
        else if (result.Outcome == EmailSendOutcome.PermanentFailure)
        {
            entry.Status = OutboxStatus.Abandoned;
            entry.LastError = TruncateError(result.Error);
            entry.EmailBody = null; // Clear secret on abandoned (N-9)
        }
        else // TransientFailure or exception
        {
            entry.AttemptCount++;

            if (entry.AttemptCount >= _options.MaxAttempts)
            {
                entry.Status = OutboxStatus.Abandoned;
                entry.LastError = TruncateError(result.Error);
                entry.EmailBody = null; // Clear secret on abandoned (N-9)
            }
            else
            {
                // Status stays Pending. Setting it to Failed took the row out of the claim query
                // above -- and out of the partial index behind it -- so it was never retried,
                // never reached Abandoned, and produced no operator signal: one greylisting (421)
                // silently lost the email forever. "Failed" is a terminal-looking name for a state
                // section 5.4 rule 4 defines as retryable, so the status does not change here at all.
                entry.LastError = TruncateError(result.Error);
                // Keep EmailBody for retry
                entry.NextAttemptAt = now.Add(BackoffFor(entry.AttemptCount));
            }
        }

        // Save per-entry (N-11: not batched)
        db.Outbox.Update(entry);
        await db.SaveChangesAsync(ct);
    }

    // Clamps rather than indexing directly. Backoff has five entries while MaxAttempts is
    // configurable, so MaxAttempts: 10 made attempt 6 throw IndexOutOfRangeException -- which the
    // catch in DrainBatch swallowed, pinning the row Pending and re-attempting it every tick.
    private static TimeSpan BackoffFor(int attemptCount) =>
        Backoff[Math.Clamp(attemptCount - 1, 0, Backoff.Length - 1)];

    private static string TruncateError(string? error)
    {
        if (string.IsNullOrEmpty(error))
            return string.Empty;

        const int maxLength = 1000;
        return error.Length > maxLength
            ? error[..maxLength]
            : error;
    }
}

public sealed class OutboxDrainerOptions
{
    public bool Enabled { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    // Named for the key in appsettings.json. This was PollIntervalMs, which bound to nothing:
    // Get<OutboxDrainerOptions>() found no PollIntervalMs key and left the 30_000 default, so the
    // interval happened to be 30s and editing PollIntervalSeconds in config did nothing.
    public int PollIntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 6;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(1, PollIntervalSeconds));
}
