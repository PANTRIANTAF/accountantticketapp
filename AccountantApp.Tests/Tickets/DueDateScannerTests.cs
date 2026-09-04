using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;
using AccountantApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// The due-date scanner, plan section 9a.4. Every case that section lists, and the four rules whose
/// violation would be invisible at runtime.
///
/// It is testable without a database and without waiting a day only because rules 1, 2, 7 and 8 were
/// honoured: <c>ScanOnceAsync</c> takes its three collaborators as arguments, the clock is a
/// <c>TimeProvider</c>, the lead time is configuration, and every query is LINQ rather than raw SQL.
///
/// WHAT THESE TESTS CANNOT SEE, because the in-memory provider has no PostgreSQL underneath it:
///
///   - the marker and the notification committing or rolling back TOGETHER. These tests use
///     <c>NoOpRequestTransaction</c>, so <c>RemindAsync</c>'s transaction is a no-op and a reminder that
///     fails after the marker is written keeps its marker here where it would roll it back in
///     production. That is the guarantee <c>pk_ticket_due_date_reminders</c> and the transaction exist
///     for, and it is unverified on this machine.
///   - <c>pk_ticket_due_date_reminders</c> itself and its foreign key, which is why
///     <c>TicketsSchemaTests</c> asserts them -- and skips.
///   - the fresh-DI-scope-per-pass of rule 2. <c>ScanOnceAsync</c> is called directly here; the scope
///     is opened by <c>ScanAsync</c>, which needs a container and a connection.
/// </summary>
public sealed class DueDateScannerTests
{
    // 22:30 UTC on 2 September. In Athens that is 01:30 on 3 SEPTEMBER (EEST, UTC+3), so the Office
    // calendar day and the UTC calendar day disagree at this instant -- which is the entire point of
    // choosing it. Every date below is expressed relative to the OFFICE day, 2026-09-03.
    private static readonly DateTimeOffset UtcNow =
        new(2026, 9, 2, 22, 30, 0, TimeSpan.Zero);

    private static readonly DateOnly OfficeToday = new(2026, 9, 3);

    // ---------------------------------------------------------------------------------------------
    // Rule 7: the Office's calendar day, not the host's.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Section 9a.2 rule 7, pinned. The host here is UTC -- explicitly NOT the Office's zone, which is
    /// what makes this test able to fail; on a developer's machine already in Europe/Athens every
    /// implementation passes, including the wrong one.
    ///
    /// At 22:30 UTC on 2 September the Office is already into 3 September. An implementation that reads
    /// <c>GetUtcNow().UtcDateTime.Date</c>, or <c>GetLocalNow()</c> on a UTC container, computes 2
    /// September, so its whole horizon is a day early and every reminder fires a day early.
    /// </summary>
    [Fact]
    public void Today_is_the_office_calendar_day_not_the_hosts()
    {
        var clock = new FakeTimeProvider(UtcNow);

        // The assumption this test rests on, asserted rather than assumed: the host is not the Office.
        Assert.Equal(TimeZoneInfo.Utc, clock.LocalTimeZone);
        Assert.NotEqual(OfficeToday, DateOnly.FromDateTime(UtcNow.UtcDateTime));

        Assert.Equal(OfficeToday, DueDateScanner.TodayInOfficeZone(clock));
    }

    /// <summary>
    /// The same bug, seen from the outside: with a three-day lead time the horizon is 6 September in the
    /// Office and 5 September in UTC, so this ticket is the one a UTC-based scanner misses entirely on
    /// this pass and reminds a day late on the next.
    /// </summary>
    [Fact]
    public async Task A_ticket_at_the_office_horizon_but_beyond_the_utc_one_is_reminded()
    {
        var world = new ScannerWorld(UtcNow, leadTimeDays: 3);
        var assignee = Guid.NewGuid();

        // 6 September: today (Office) + 3. In UTC "today" is the 2nd, whose horizon is the 5th.
        var ticket = world.SeedTicket(OfficeToday.AddDays(3), assignee);

        Assert.Equal(1, await world.ScanAsync());
        Assert.Equal(ticket.Id, world.Notifications.Sent.Single().TicketId!.Value);
    }

    // ---------------------------------------------------------------------------------------------
    // Section 9a.4, case by case.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_ticket_inside_the_lead_time_is_reminded_once()
    {
        var world = new ScannerWorld(UtcNow);
        var assignee = Guid.NewGuid();
        var ticket = world.SeedTicket(OfficeToday.AddDays(1), assignee);

        Assert.Equal(1, await world.ScanAsync());

        var sent = world.Notifications.Sent.Single();
        Assert.Equal(assignee.ToString(), sent.RecipientUserId);
        Assert.Equal(NotificationEvents.DueDateApproaching, sent.EventKind);
        Assert.Equal(ticket.Id, sent.TicketId!.Value);
        Assert.Contains(ticket.Reference, sent.Title);

        // In-app only (rule 10). EmailBody must be null, and NotificationApi would throw if it were not,
        // because DueDateApproaching is deliberately absent from NotificationEvents.Emailed.
        Assert.Null(sent.EmailBody);

        var marker = await world.Db.DueDateReminders.SingleAsync();
        Assert.Equal(ticket.Id, marker.TicketId);
        Assert.Equal(OfficeToday.AddDays(1), marker.DueDate);
        Assert.Equal(UtcNow, marker.SentAt);
    }

    [Fact]
    public async Task The_same_ticket_and_due_date_is_not_reminded_twice()
    {
        var world = new ScannerWorld(UtcNow);
        world.SeedTicket(OfficeToday.AddDays(1), Guid.NewGuid());

        Assert.Equal(1, await world.ScanAsync());
        Assert.Equal(0, await world.ScanAsync());
        Assert.Equal(0, await world.ScanAsync());

        Assert.Single(world.Notifications.Sent);
        Assert.Equal(1, await world.Db.DueDateReminders.CountAsync());
    }

    /// <summary>
    /// Section 9a.3, the reason the marker is keyed to the due date at all. Moving the deadline re-arms
    /// the reminder with no reset step. A boolean marker -- or a <c>reminded_at</c> column on tickets --
    /// passes the test above and fails this one, silently, forever.
    /// </summary>
    [Fact]
    public async Task Moving_the_due_date_re_arms_the_reminder()
    {
        var world = new ScannerWorld(UtcNow);
        var ticket = world.SeedTicket(OfficeToday.AddDays(1), Guid.NewGuid());

        Assert.Equal(1, await world.ScanAsync());

        ticket.DueDate = OfficeToday.AddDays(2);
        await world.Db.SaveChangesAsync();

        Assert.Equal(1, await world.ScanAsync());

        Assert.Equal(2, world.Notifications.Sent.Count);

        // Two markers, not one updated in place: section 1.9, append-only.
        var dueDates = await world.Db.DueDateReminders
            .Select(reminder => reminder.DueDate).OrderBy(date => date).ToListAsync();
        Assert.Equal(
            new List<DateOnly> { OfficeToday.AddDays(1), OfficeToday.AddDays(2) },
            dueDates);
    }

    /// <summary>
    /// Section 9a.2 rule 5 and section 12 constraint 13. A reminder needs somebody to remind, and
    /// broadcasting an approaching deadline to every Accountant is worse than silence -- section 4.4's
    /// pickup queue is where an unassigned ticket surfaces.
    /// </summary>
    [Fact]
    public async Task An_unassigned_ticket_is_skipped()
    {
        var world = new ScannerWorld(UtcNow);
        world.SeedTicket(OfficeToday.AddDays(1), assignee: null, status: TicketStatus.Submitted);

        Assert.Equal(0, await world.ScanAsync());
        Assert.Empty(world.Notifications.Sent);
        Assert.Empty(await world.Db.DueDateReminders.ToListAsync());
    }

    /// <summary>
    /// Section 9a.2 rule 6, both halves. Closed and Cancelled are terminal and their due dates are
    /// history. Answered is NOT terminal: it is waiting on the Customer, and the deadline the Office is
    /// accountable for has not moved.
    /// </summary>
    [Theory]
    [InlineData(TicketStatus.Closed, false)]
    [InlineData(TicketStatus.Cancelled, false)]
    [InlineData(TicketStatus.Answered, true)]
    [InlineData(TicketStatus.Submitted, true)]
    [InlineData(TicketStatus.InReview, true)]
    [InlineData(TicketStatus.AwaitingInformation, true)]
    public async Task Only_non_terminal_tickets_are_reminded(string status, bool expectReminder)
    {
        var world = new ScannerWorld(UtcNow);
        world.SeedTicket(OfficeToday.AddDays(1), Guid.NewGuid(), status);

        Assert.Equal(expectReminder ? 1 : 0, await world.ScanAsync());
        Assert.Equal(expectReminder ? 1 : 0, world.Notifications.Sent.Count);
    }

    /// <summary>
    /// The recipient is the Assignee and nobody else -- not the Creator, not the Subject's account, not
    /// every Accountant. Seeded so that all three ids are distinct, which is the only arrangement in
    /// which "notifies the Assignee" and "notifies somebody related to the ticket" can be told apart.
    /// </summary>
    [Fact]
    public async Task Only_the_assignee_is_notified()
    {
        var world = new ScannerWorld(UtcNow);
        var assignee = Guid.NewGuid();
        world.SeedTicket(OfficeToday.AddDays(1), assignee);

        await world.ScanAsync();

        var recipients = world.Notifications.Sent
            .Select(request => request.RecipientUserId).ToList();

        Assert.Equal(new List<string> { assignee.ToString() }, recipients);
        Assert.DoesNotContain(world.CreatorAccountId.ToString(), recipients);
        Assert.DoesNotContain(world.SubjectEmployeeId.ToString(), recipients);
    }

    /// <summary>
    /// THE BOUNDARY, DECIDED ONE WAY AND ASSERTED (section 9a.4).
    ///
    /// A ticket due exactly <c>today + LeadTimeDays</c> IS reminded: the comparison is
    /// <c>due_date &lt;= today + LeadTimeDays</c>, inclusive. "Three days' notice" that first arrives
    /// with two days left is not three days' notice. The day after the horizon is not reminded, which is
    /// the half that pins the decision -- an off-by-one in either direction changes exactly one of these
    /// two assertions.
    /// </summary>
    [Theory]
    [InlineData(-1, true)]   // yesterday: already overdue, and reminded once
    [InlineData(0, true)]    // today
    [InlineData(2, true)]    // inside
    [InlineData(3, true)]    // EXACTLY the boundary -- inclusive
    [InlineData(4, false)]   // one day beyond
    [InlineData(30, false)]
    public async Task The_lead_time_boundary_is_inclusive(int daysFromToday, bool expectReminder)
    {
        var world = new ScannerWorld(UtcNow, leadTimeDays: 3);
        world.SeedTicket(OfficeToday.AddDays(daysFromToday), Guid.NewGuid());

        Assert.Equal(expectReminder ? 1 : 0, await world.ScanAsync());
    }

    /// <summary>
    /// BatchSize BOUNDS THE TICKETS STILL NEEDING A REMINDER, not the tickets examined -- so an
    /// already-reminded ticket does not occupy a slot in every later pass.
    ///
    /// This is the failure mode the anti-join in <c>ScanOnceAsync</c>'s query exists to prevent, and it is
    /// the one bug in this file that no other test here can see: with the exclusion applied to the query's
    /// RESULTS instead, every test above still passes, because none of them seeds more tickets than
    /// BatchSize. In production the lower bound on due date is open and the ordering is due date
    /// ascending, so the oldest overdue tickets sort to the front of every pass and keep their markers
    /// forever. Once more than BatchSize open tickets are past due, the batch is entirely already-reminded
    /// rows and the scanner sends NOTHING, for any ticket, ever again -- with no error and no log line,
    /// reporting zero reminders because it genuinely found no work.
    ///
    /// BatchSize is 2 here rather than 500 so the condition costs three tickets instead of five hundred
    /// and one.
    /// </summary>
    [Fact]
    public async Task An_already_reminded_ticket_does_not_consume_a_batch_slot_on_later_passes()
    {
        var world = new ScannerWorld(UtcNow, batchSize: 2);

        // Two tickets due EARLIER than the third, so they sort ahead of it and fill the batch.
        var first = world.SeedTicket(OfficeToday, Guid.NewGuid());
        var second = world.SeedTicket(OfficeToday.AddDays(1), Guid.NewGuid());
        var third = world.SeedTicket(OfficeToday.AddDays(2), Guid.NewGuid());

        // Pass one: the batch cap means only the two earliest are reminded.
        Assert.Equal(2, await world.ScanAsync());
        Assert.Equal(
            new List<Guid> { first.Id, second.Id },
            world.Notifications.Sent.Select(sent => sent.TicketId!.Value).ToList());

        // Pass two: the first two are excluded by the query, so the third is reached rather than
        // starved. Filtering after the Take would return zero here -- and zero on every pass after it.
        Assert.Equal(1, await world.ScanAsync());
        Assert.Equal(third.Id, world.Notifications.Sent.Last().TicketId!.Value);

        Assert.Equal(3, await world.Db.DueDateReminders.CountAsync());

        // And nothing is sent twice once the backlog has drained.
        Assert.Equal(0, await world.ScanAsync());
        Assert.Equal(3, world.Notifications.Sent.Count);
    }

    /// <summary>
    /// Section 9a.2 rule 9, the per-ticket half. One bad ticket must not stop the pass.
    ///
    /// The ordering in <c>ScanOnceAsync</c> is by due date ascending, so the throwing ticket is given the
    /// EARLIER due date -- it is processed first, and a scanner that let the exception escape the loop
    /// would leave the second ticket unreminded with nothing in the response to show it.
    ///
    /// A throwing <c>INotificationApi</c> is the realistic shape of this: an assignee whose account row
    /// has gone, a title that trips a validation rule, a connection lost mid-pass.
    /// </summary>
    [Fact]
    public async Task One_throwing_ticket_does_not_prevent_the_next_from_being_notified()
    {
        var world = new ScannerWorld(UtcNow);

        var doomedAssignee = Guid.NewGuid();
        var healthyAssignee = Guid.NewGuid();

        var doomed = world.SeedTicket(OfficeToday.AddDays(1), doomedAssignee);
        var healthy = world.SeedTicket(OfficeToday.AddDays(2), healthyAssignee);

        world.Notifications.ThrowFor.Add(doomed.Id);

        Assert.Equal(1, await world.ScanAsync());

        var sent = world.Notifications.Sent.Single();
        Assert.Equal(healthyAssignee.ToString(), sent.RecipientUserId);
        Assert.Equal(healthy.Id, sent.TicketId!.Value);
    }

    // ---------------------------------------------------------------------------------------------
    // Rules 1, 3, 4 and 10: the ones whose violation is invisible at runtime.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Section 9a.2 rule 1. The default is what keeps the hosted service out of the test host --
    /// EndpointRoutingTests builds the whole application -- and out of a developer's F5.
    /// </summary>
    [Fact]
    public void The_options_default_to_off_with_a_three_day_lead_time_and_one_pass_a_day()
    {
        var options = new DueDateScannerOptions();

        Assert.False(options.Enabled);
        Assert.Equal(3, options.LeadTimeDays);
        Assert.Equal(TimeSpan.FromHours(24), options.ScanInterval);

        // A zero or negative interval in configuration is a spin loop, not an opt-out.
        Assert.Equal(TimeSpan.FromHours(1),
            new DueDateScannerOptions { ScanIntervalHours = 0 }.ScanInterval);
    }

    /// <summary>
    /// The gate itself, asserted on the service collection: with the section absent -- which is the state
    /// EndpointRoutingTests builds in -- <c>AddTicketsSlice</c> registers NO hosted service at all.
    /// </summary>
    [Fact]
    public void No_hosted_service_is_registered_when_the_scanner_is_not_configured()
    {
        Assert.Empty(HostedServicesIn(new Dictionary<string, string?>()));
        Assert.Empty(HostedServicesIn(new Dictionary<string, string?>
        {
            ["Tickets:DueDateScanner:Enabled"] = "false",
        }));

        // TimeProvider enters the container only through the gate, so its absence is a second, independent
        // witness that nothing was registered.
        Assert.DoesNotContain(
            Registered(new Dictionary<string, string?>()),
            descriptor => descriptor.ServiceType == typeof(TimeProvider));
    }

    [Fact]
    public void The_hosted_service_is_registered_and_bound_when_enabled()
    {
        var services = Registered(new Dictionary<string, string?>
        {
            ["Tickets:DueDateScanner:Enabled"] = "true",
            ["Tickets:DueDateScanner:ScanIntervalHours"] = "6",
        });

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(TimeProvider));

        var options = (DueDateScannerOptions)services
            .Single(descriptor => descriptor.ServiceType == typeof(DueDateScannerOptions))
            .ImplementationInstance!;

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromHours(6), options.ScanInterval);

        // Not set in configuration above, so the DEFAULT must survive binding. The drainer's
        // PollIntervalMs is the cautionary tale: a property whose name does not match its key binds to
        // nothing and silently keeps its default while the configured value does nothing at all.
        Assert.Equal(3, options.LeadTimeDays);
    }

    /// <summary>
    /// Section 9a.2 rules 3 and 4, asserted structurally because there is no runtime symptom.
    ///
    /// The scanner takes no <c>IPermissionChecker</c>, no <c>CurrentUser</c> and no <c>IAuditApi</c>.
    /// There is no actor outside a request: a scanner that manufactured a <c>CurrentUser</c> to reuse a
    /// handler would invent one, and every audit entry that handler writes would name it. The missing
    /// audit entry is the one write in this slice with no trail, and it is a FLAGGED gap (section 12
    /// constraint 14) -- closing it needs a system-actor concept in Audit that does not exist.
    /// </summary>
    [Fact]
    public void The_scanner_depends_on_no_actor_and_writes_no_audit_entry()
    {
        var dependencies = typeof(DueDateScanner)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.DoesNotContain(typeof(IPermissionChecker), dependencies);
        Assert.DoesNotContain(typeof(PermissionChecker), dependencies);
        Assert.DoesNotContain(typeof(CurrentUser), dependencies);
        Assert.DoesNotContain(typeof(IAuditApi), dependencies);

        // And nothing is reached for round the back, through a field or a captured service provider.
        var fieldTypes = typeof(DueDateScanner)
            .GetFields(System.Reflection.BindingFlags.Instance
                     | System.Reflection.BindingFlags.NonPublic
                     | System.Reflection.BindingFlags.Public)
            .Select(field => field.FieldType)
            .ToList();

        Assert.DoesNotContain(typeof(IAuditApi), fieldTypes);
        Assert.DoesNotContain(typeof(CurrentUser), fieldTypes);
        Assert.DoesNotContain(typeof(IPermissionChecker), fieldTypes);
    }

    /// <summary>
    /// Section 9a.2 rule 10. <c>DueDateApproaching</c> is a valid kind and is NOT emailed, and both
    /// halves matter: <c>NotificationApi.ValidateRequest</c> throws on an unknown kind, so the first
    /// keeps the scanner working at all, and the second is the decision that this is an in-app reminder.
    /// A well-meaning addition to <c>Emailed</c> would start sending a daily email per approaching
    /// deadline, and it would look like a fix.
    /// </summary>
    [Fact]
    public void DueDateApproaching_is_a_valid_kind_and_is_not_emailed()
    {
        Assert.Contains(NotificationEvents.DueDateApproaching, NotificationEvents.All);
        Assert.DoesNotContain(NotificationEvents.DueDateApproaching, NotificationEvents.Emailed);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static List<ServiceDescriptor> Registered(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(settings)
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Database=never_connected",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddTicketsSlice(configuration);
        return [.. services];
    }

    private static List<ServiceDescriptor> HostedServicesIn(Dictionary<string, string?> settings) =>
        Registered(settings)
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToList();

    /// <summary>
    /// One in-memory <c>TicketsDbContext</c>, one fake clock, one recording <c>INotificationApi</c> and
    /// the real <c>DueDateScanner</c>. <c>ScanOnceAsync</c> is called directly, so the scope factory is
    /// never used -- and the double throws rather than returning something, so a future change that
    /// starts opening a scope from inside the pass fails here instead of quietly working.
    /// </summary>
    private sealed class ScannerWorld
    {
        private readonly DueDateScanner _scanner;
        private readonly Guid _customerId = Guid.NewGuid();

        public ScannerWorld(DateTimeOffset utcNow, int leadTimeDays = 3, int batchSize = 500)
        {
            Db = TicketsTestHarness.NewDb();
            Clock = new FakeTimeProvider(utcNow);

            _scanner = new DueDateScanner(
                new UnusableScopeFactory(),
                Clock,
                NullLogger<DueDateScanner>.Instance,
                new DueDateScannerOptions
                {
                    Enabled = true,
                    LeadTimeDays = leadTimeDays,
                    BatchSize = batchSize,
                });
        }

        public TicketsDbContext Db { get; }
        public FakeTimeProvider Clock { get; }
        public RecordingNotificationApi Notifications { get; } = new();

        /// <summary>
        /// Distinct from the Assignee and from each other, so "notifies the Assignee" cannot be confused
        /// with "notifies somebody attached to the ticket".
        /// </summary>
        public Guid CreatorAccountId { get; } = Guid.NewGuid();

        public Guid SubjectEmployeeId { get; } = Guid.NewGuid();

        public Task<int> ScanAsync() =>
            _scanner.ScanOnceAsync(Db, Notifications, new NoOpRequestTransaction(), default);

        public Ticket SeedTicket(
            DateOnly dueDate, Guid? assignee, string status = TicketStatus.InReview)
        {
            var ticket = TicketsTestHarness.NewTicket(
                _customerId, CreatorAccountId, SubjectEmployeeId, status, assignee);

            ticket.DueDate = dueDate;

            Db.Tickets.Add(ticket);
            Db.SaveChanges();
            return ticket;
        }
    }

    /// <summary>
    /// A fixed clock. Written here rather than taken from Microsoft.Extensions.TimeProvider.Testing so
    /// that the test project gains no package reference for four lines.
    ///
    /// LocalTimeZone is UTC on purpose: it stands for the container the application actually runs in,
    /// and the scanner must ignore it. A fake that returned the Office zone here would let a
    /// <c>GetLocalNow()</c> implementation pass every test in this file.
    /// </summary>
    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>
    /// Records what was raised, and throws for nominated tickets so rule 9 can be exercised. It does
    /// NOT collapse duplicates: this scanner sends at most one notification per ticket per pass, so a
    /// de-duplicating double would hide a scanner that notified the same Assignee twice.
    /// </summary>
    private sealed class RecordingNotificationApi : INotificationApi
    {
        public List<NotificationRequest> Sent { get; } = [];

        public HashSet<Guid> ThrowFor { get; } = [];

        public Task<Guid> NotifyAsync(NotificationRequest request, CancellationToken ct = default)
        {
            if (request.TicketId is { } ticketId && ThrowFor.Contains(ticketId))
                throw new InvalidOperationException("Recipient account has gone.");

            Sent.Add(request);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<int> NotifyManyAsync(
            IReadOnlyCollection<NotificationRequest> requests, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "The scanner notifies one Assignee per ticket and must not batch across tickets: a "
                + "batch would make one bad recipient lose the whole pass, which is what rule 9 forbids.");
    }

    private sealed class UnusableScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException(
                "ScanOnceAsync must not open a DI scope. The scope is per PASS (rule 2), opened by "
                + "ScanAsync, which is what keeps the pass itself testable without a container.");
    }
}
