using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AccountantApp.Api.Slices.Tickets.Infrastructure;

/// <summary>
/// The second (and, by 01-DomainModel.md section 9.2, last permitted) IHostedService in the system.
/// Plan section 9a; authorized by section 13 item 8. Modelled on
/// Slices/Notifications/Infrastructure/OutboxDrainer.cs, which solved most of these problems first.
///
/// It raises one <c>DueDateApproaching</c> notification per ticket whose due date is within
/// LeadTimeDays, to that ticket's Assignee, at most once per (ticket, due date).
///
/// WHAT IT DELIBERATELY DOES NOT DO, and each of these is a rule rather than an omission:
///
/// 1. NO IPermissionChecker AND NO CurrentUser (section 9a.2 rule 3). There is no actor outside a
///    request, and PermissionChecker.RequireAsync takes a CurrentUser. Manufacturing a fake one to
///    reuse a handler would invent an actor, and every audit entry and system message that handler
///    writes would then name it. So this reads TicketsDbContext directly and calls INotificationApi
///    directly, and calls no handler at all.
///
/// 2. NO AUDIT ENTRY (section 9a.2 rule 4, section 12 constraint 14). Every other write in this slice
///    audits (section 4.0 F); this is the single exception, and it is the exception for the same reason
///    as point 1: an AuditEntry names the actor who did the thing, and there is no actor here.
///    FLAGGED, NOT HIDDEN -- if reminders need an audit trail, that needs a system-actor concept which
///    does not exist today, and adding one is a change to the Audit slice, not a line in this file.
///
/// 3. NO DELETES and NO UPDATE TO tickets (section 1.9, section 9.7). The tickets query is
///    AsNoTracking and nothing in this file mutates a Ticket. Writing a "reminded" flag onto the
///    tickets row would bump its optimistic-concurrency token and hand a spurious 409 to a user who
///    changed nothing -- see TicketDueDateReminder for the full reasoning.
///
/// 4. NO EMAIL. DueDateApproaching is absent from NotificationEvents.Emailed on purpose (section 9a.2
///    rule 10, Notifications plan section 3 rule 5), so NotificationApi accepts the kind and writes no
///    outbox row. That is the intended behaviour, NOT a misconfiguration to be "fixed" by adding the
///    kind to Emailed.
///
/// TOPOLOGY CONSTRAINT: run this in exactly one replica (section 9a.2 rule 11). Unlike the
/// OutboxDrainer, a second replica would not actually double-send -- the marker and the notification
/// are written in one transaction behind a (ticket_id, due_date) primary key, so the loser of the race
/// gets 23505 and rolls its notification back -- but every instance would still scan the whole
/// candidate set and lose the race, and nothing here claims work. Scaling the API horizontally needs
/// claim locking first, exactly as it does for the drainer.
/// </summary>
public sealed class DueDateScanner : BackgroundService
{
    /// <summary>
    /// THE one documented time-zone constant, per section 9a.2 rule 7. The Office is in Greece
    /// (04-Infrastructure.md deploys a single-Office instance; every address fixture in the system is
    /// Athens), so "near a due date" is a question about the Greek calendar day.
    ///
    /// This is NOT UTC and it is NOT the host's local zone, and both alternatives are wrong in a way
    /// that is invisible here: a container in UTC computes "today" as the previous day for the three
    /// hours after Athens midnight in summer, so a reminder fires a day early -- and a developer whose
    /// machine is already in this zone can never reproduce it. TimeProvider.GetLocalNow() is the same
    /// bug with a nicer name, which is why it is not used below.
    ///
    /// The IANA id is the primary spelling. .NET maps IANA ids on Windows through ICU, so it normally
    /// resolves everywhere; the Windows id is a fallback for a host with no ICU data, because a
    /// TimeZoneNotFoundException in a static initialiser would take the whole application down at
    /// startup for a reason nobody would guess from the stack trace.
    /// </summary>
    private const string OfficeTimeZoneIanaId = "Europe/Athens";
    private const string OfficeTimeZoneWindowsId = "GTB Standard Time";

    private static readonly TimeZoneInfo OfficeTimeZone = ResolveOfficeTimeZone();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DueDateScanner> _logger;
    private readonly DueDateScannerOptions _options;

    public DueDateScanner(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<DueDateScanner> logger,
        DueDateScannerOptions options)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Due-date scanner started. Lead time {LeadTimeDays} day(s), one pass every {Interval}.",
            _options.LeadTimeDays,
            _options.ScanInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Section 9a.2 rule 9, the outer half: a BackgroundService whose ExecuteAsync throws
                // ends the service, and under the default BackgroundServiceExceptionBehavior it stops
                // the HOST -- so an unhandled exception in a reminder loop takes the API down. The
                // per-ticket half is in ScanOnceAsync.
                _logger.LogError(ex, "Due-date scan pass failed.");
            }

            try
            {
                await Task.Delay(_options.ScanInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Due-date scanner stopped.");
    }

    /// <summary>
    /// Section 9a.2 rule 2: A FRESH DI SCOPE PER PASS, via IServiceScopeFactory.
    ///
    /// This is not tidiness. TicketsDbContext is scoped onto the shared RequestConnection, so a scope
    /// per pass gives the scanner its own physical connection with no ambient transaction, and drops
    /// the change tracker rather than accumulating every ticket it has ever seen. NotificationApi
    /// resolved from the SAME scope shares that connection, which is what lets the marker and the
    /// notification commit together. The comment at the top of NotificationsRegistration explains why
    /// a context on its own connection cannot be handed somebody else's DbTransaction.
    /// </summary>
    private async Task ScanAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<TicketsDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationApi>();
        var transaction = scope.ServiceProvider.GetRequiredService<IRequestTransaction>();

        var notified = await ScanOnceAsync(db, notifications, transaction, ct);

        if (notified > 0)
            _logger.LogInformation("Due-date scan raised {Count} reminder(s).", notified);
    }

    /// <summary>
    /// One pass, with its collaborators handed in. Returns how many reminders were raised.
    ///
    /// Internal and dependency-free on purpose: this is the whole behaviour of the scanner, and it is
    /// reachable from a test with an in-memory TicketsDbContext, a recording INotificationApi and a
    /// fake TimeProvider without a host, a DI container or a database (section 9a.4). Everything above
    /// it is a loop and a scope.
    ///
    /// EVERY QUERY HERE IS LINQ, deliberately. The in-memory provider cannot execute raw SQL, so the
    /// obvious NOT EXISTS anti-join written as FromSqlRaw would make the one part of this file worth
    /// testing untestable on this machine. It is expressed as a correlated <c>Any()</c> over
    /// DueDateReminders instead, which the in-memory provider does evaluate and which Npgsql translates
    /// to the same NOT EXISTS.
    /// </summary>
    internal async Task<int> ScanOnceAsync(
        TicketsDbContext db,
        INotificationApi notifications,
        IRequestTransaction transaction,
        CancellationToken ct)
    {
        var today = TodayInOfficeZone(_timeProvider);
        var horizon = today.AddDays(_options.LeadTimeDays);

        // THE BOUNDARY, DECIDED (section 9a.4): a ticket due EXACTLY today + LeadTimeDays IS reminded.
        // "Three days' notice" that first arrives with two days left is not three days' notice, and the
        // inclusive reading is the one a person means by "remind me three days before". Asserted in
        // DueDateScannerTests so the decision cannot drift.
        //
        // The lower end is deliberately open: an ALREADY OVERDUE ticket is included, and gets exactly
        // one reminder for that due date, because a scanner that only looks forward never mentions the
        // date it just missed. That is a judgment call -- the notification kind is named
        // DueDateApproaching -- and BatchSize below is what keeps a historic backlog of overdue tickets
        // from making the first pass unbounded.
        //
        // Draft needs no clause of its own: ck_tickets_assignee forbids an Assignee in Draft, so the
        // assignee filter already excludes every draft. Answered is NOT excluded (rule 6) -- it is
        // waiting on the Customer and its deadline still matters.
        //
        // THE ANTI-JOIN IS PART OF THE QUERY, NOT A FILTER OVER THE RESULTS, and the difference is a
        // silent failure rather than a style preference. BatchSize caps this query, so filtering
        // already-reminded tickets out AFTERWARDS means every marker written eats one of the pass's
        // slots forever: the ordering is due date ascending and the lower bound is open, so the oldest
        // overdue tickets sort to the front, keep their markers, and are re-fetched on every pass. Once
        // more than BatchSize open tickets are past due, the batch is entirely already-reminded rows and
        // NO FURTHER REMINDER IS EVER SENT -- with no error, no log line and a scan that reports zero
        // work because there was none to do. Excluding them here means BatchSize bounds the tickets that
        // still need a reminder, which is what it was meant to bound.
        //
        // Matching on (TicketId, DueDate) rather than TicketId alone is what preserves section 9a.3:
        // a moved due date has no marker for its NEW date, so it re-arms.
        var candidates = await db.Tickets.AsNoTracking()
            .Where(ticket => ticket.DueDate != null
                          && ticket.DueDate <= horizon
                          && ticket.AssigneeUserAccountId != null
                          && ticket.Status != TicketStatus.Closed
                          && ticket.Status != TicketStatus.Cancelled
                          && !db.DueDateReminders.Any(reminder =>
                                 reminder.TicketId == ticket.Id
                              && reminder.DueDate == ticket.DueDate))
            .OrderBy(ticket => ticket.DueDate)
            .ThenBy(ticket => ticket.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        var notifiedCount = 0;

        foreach (var ticket in candidates)
        {
            try
            {
                await RemindAsync(db, notifications, transaction, ticket, ct);
                notifiedCount++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Section 9a.2 rule 9, the per-ticket half: catch, log, CONTINUE. One ticket with a
                // stale assignee account, an over-long title or a lost connection must not stop the
                // rest of the pass -- otherwise the first bad row silently suppresses every reminder
                // behind it, and the ordering above means that is the most urgent one.
                //
                // The ticket reference is logged rather than the notification body: the body is
                // derived from the ticket title, which carries an Employee's name.
                _logger.LogError(
                    ex,
                    "Due-date reminder for ticket {TicketReference} failed; continuing with the rest "
                    + "of the pass. No marker was committed, so the next pass retries it.",
                    ticket.Reference);
            }
        }

        return notifiedCount;
    }

    /// <summary>
    /// One reminder: the marker and the notification, in ONE transaction.
    ///
    /// Order and atomicity both matter. The marker is INSERTed first and the notification raised
    /// second, inside a transaction the scope owns, so:
    ///
    ///   - a failure raising the notification rolls the marker back, and the next pass retries -- a
    ///     marker committed without its notification would suppress that reminder forever;
    ///   - a second scanner racing this one blocks on pk_ticket_due_date_reminders, gets 23505, and
    ///     rolls back its own notification, so the reminder is sent once.
    ///
    /// NotificationApi enlists in this transaction through IRequestTransaction (its NotifyAsync calls
    /// EnlistAsync), which works here only because both contexts came out of the same scope and so sit
    /// on the same RequestConnection.
    /// </summary>
    private async Task RemindAsync(
        TicketsDbContext db,
        INotificationApi notifications,
        IRequestTransaction transaction,
        Ticket ticket,
        CancellationToken ct)
    {
        var dueDate = ticket.DueDate!.Value;

        await using var scope = await transaction.BeginAsync(db, ct);

        db.DueDateReminders.Add(new TicketDueDateReminder
        {
            TicketId = ticket.Id,
            DueDate = dueDate,
            SentAt = _timeProvider.GetUtcNow(),
        });

        await db.SaveChangesAsync(ct);

        // Section 9a.2 rule 5: THE ASSIGNEE, and nobody else. An approaching due date on an unassigned
        // ticket is a queue problem, and section 4.4's pickup queue is already where that surfaces --
        // broadcasting to every Accountant is worse than silence. Unassigned tickets never reach here
        // at all: the candidate query filters them out. Section 12 constraint 13.
        await notifications.NotifyAsync(
            new NotificationRequest(
                ticket.AssigneeUserAccountId!.Value.ToString(),
                NotificationEvents.DueDateApproaching,
                $"{ticket.Reference} is due on {dueDate:yyyy-MM-dd}",
                $"{ticket.Title} is due on {dueDate:yyyy-MM-dd}.",
                ticket.Id),
            ct);

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Today, as the Office reckons it. Section 9a.2 rule 7.
    ///
    /// GetUtcNow() converted through OfficeTimeZone, never GetLocalNow() and never .UtcDateTime.Date:
    /// due_date is a DATE, so "is this within the lead time" is a question about calendar days in one
    /// specific place, and asking it of the host's clock gives a different answer depending on where
    /// the container runs.
    ///
    /// Internal so DueDateScannerTests can pin it directly with a fake TimeProvider -- the bug this
    /// exists to prevent is invisible on a machine already in this zone, which is exactly why it needs
    /// a test rather than a reading.
    /// </summary>
    internal static DateOnly TodayInOfficeZone(TimeProvider timeProvider) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), OfficeTimeZone).DateTime);

    private static TimeZoneInfo ResolveOfficeTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(OfficeTimeZoneIanaId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(OfficeTimeZoneWindowsId);
        }
    }
}

/// <summary>
/// Bound from configuration section "Tickets:DueDateScanner" in TicketsRegistration. Shaped like
/// OutboxDrainerOptions: a class with settable properties, because Get&lt;T&gt;() binds by property
/// name, and each property NAMED FOR ITS CONFIGURATION KEY -- the drainer's PollIntervalMs bound to
/// nothing, silently kept its default, and made editing appsettings.json a no-op.
/// </summary>
public sealed class DueDateScannerOptions
{
    /// <summary>
    /// Section 9a.2 rule 1: OFF BY DEFAULT, and the default is what matters. TicketsRegistration calls
    /// AddHostedService only when this is true, which is what keeps a background loop out of the test
    /// host (EndpointRoutingTests builds the whole application) and out of a developer's F5 without
    /// anybody having to remember it.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Section 9a.2 rule 8. How many days before the due date to remind. Default 3.</summary>
    public int LeadTimeDays { get; set; } = 3;

    /// <summary>
    /// Section 9a.2 rule 8: ONE PASS PER DAY, not one per minute. A reminder is a calendar-day
    /// judgment, and the marker table makes a second pass on the same day a no-op anyway, so a short
    /// interval buys nothing and costs a full scan.
    /// </summary>
    public int ScanIntervalHours { get; set; } = 24;

    /// <summary>
    /// The cap on one pass. It exists because the candidate query has no lower bound on due_date, so
    /// the first pass over an established database sees every overdue ticket ever. Ordered by due date
    /// ascending, so a backlog larger than this drains oldest-first over several passes and nothing is
    /// lost -- each ticket keeps its marker once reminded.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Clamped at one hour, so a zero or negative configuration value is not a spin loop.</summary>
    public TimeSpan ScanInterval => TimeSpan.FromHours(Math.Max(1, ScanIntervalHours));
}
