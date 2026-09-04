using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Tests.Documents;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// Plan §11.2's pickup-queue group and the assignment rules that hang off it -- "the highest-risk query"
/// in the slice, because it is the only thing in the system that surfaces work stranded by a suspension
/// and a ticket missing from it is invisible forever.
/// </summary>
public class TicketsPickupQueueFlowTests
{
    /// <summary>
    /// One Customer, one Creator, one Subject: the queue is about status and assignee, and varying
    /// anything else per case would make a failure ambiguous.
    /// </summary>
    private sealed class Queue
    {
        public Queue()
        {
            World = new TicketsWorld();
            CustomerId = World.Customers.AddActive();
            (Creator, Subject) = World.NewCustomerSide(CustomerId, UserRole.CustomerAdmin);
            Office = World.NewAccountant(UserRole.AccountantUser);
        }

        public TicketsWorld World { get; }
        public Guid CustomerId { get; }
        public CurrentUser Creator { get; }
        public Api.Slices.Employees.ExternalInterfaces.EmployeeSummary Subject { get; }
        public CurrentUser Office { get; }

        public Ticket Add(string status, Guid? assignee = null) => World.NewTicket(
            CustomerId, Guid.Parse(Creator.Id), Subject.Id, status, assignee);

        public Task<Api.Shared.Pagination.PaginatedResponse<TicketListItemDto>> Read(
            int pageSize = 15) =>
            World.ListPickupQueue().Handle(
                new ListPickupQueueRequestDto { PageSize = pageSize }, Office, default);
    }

    [Fact]
    public async Task A_Submitted_ticket_with_no_assignee_is_in_the_queue()
    {
        var queue = new Queue();
        var ticket = queue.Add(TicketStatus.Submitted);

        var page = await queue.Read();

        var row = Assert.Single(page.Items);
        Assert.Equal(ticket.Id, row.Id);
        Assert.False(row.IsStranded);
    }

    /// <summary>
    /// §11.3 test 3, and the §5 trap. <c>AwaitingInformation → Submitted</c> RETAINS the Assignee, so a
    /// ticket that has been through a correction round is <c>Submitted</c> WITH an assignee -- and a
    /// queue filtered on status alone puts it back into the shared pool while the Accountant who asked
    /// the question is still working on it.
    /// </summary>
    [Fact]
    public async Task A_Submitted_ticket_that_still_has_its_assignee_is_not_in_the_queue()
    {
        var queue = new Queue();
        var assignee = queue.World.SeedAccount();

        queue.Add(TicketStatus.Submitted, assignee);

        var page = await queue.Read();

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task An_InReview_ticket_with_an_active_assignee_is_not_in_the_queue()
    {
        var queue = new Queue();
        queue.Add(TicketStatus.InReview, queue.World.SeedAccount());

        Assert.Empty((await queue.Read()).Items);
    }

    [Theory]
    [InlineData(TicketStatus.InReview)]
    [InlineData(TicketStatus.AwaitingInformation)]
    [InlineData(TicketStatus.Answered)]
    public async Task An_open_ticket_whose_assignee_is_suspended_is_in_the_queue(string status)
    {
        var queue = new Queue();
        var suspended = queue.World.SeedAccount(status: "Suspended");
        var ticket = queue.Add(status, suspended);

        var row = Assert.Single((await queue.Read()).Items);

        Assert.Equal(ticket.Id, row.Id);

        // The flag the Office needs to tell the two halves apart: without it a stranded ticket looks like
        // an ordinary assigned one that has wandered in, and the taker cannot tell it is a reassignment.
        Assert.True(row.IsStranded);
    }

    [Theory]
    [InlineData(TicketStatus.Closed)]
    [InlineData(TicketStatus.Cancelled)]
    public async Task A_terminal_ticket_is_never_in_the_queue(string status)
    {
        var queue = new Queue();

        // Cancelled cannot really hold an assignee (ck_tickets_assignee), but the fixture is written the
        // hostile way on purpose: if the status filter were dropped, this row would surface.
        queue.Add(status, queue.World.SeedAccount(status: "Suspended"));

        Assert.Empty((await queue.Read()).Items);
    }

    /// <summary>
    /// §9.8 rule 4: AN UNKNOWN ACCOUNT COUNTS AS NOT ACTIVE. A ticket assigned to an account that no
    /// longer resolves is exactly the stranded case, and treating it as healthy hides it from everyone.
    /// </summary>
    [Fact]
    public async Task A_ticket_whose_assignee_no_longer_resolves_is_in_the_queue()
    {
        var queue = new Queue();
        var ticket = queue.Add(TicketStatus.InReview, Guid.NewGuid());

        var row = Assert.Single((await queue.Read()).Items);

        Assert.Equal(ticket.Id, row.Id);
        Assert.True(row.IsStranded);
    }

    /// <summary>
    /// A read: no transaction, no audit, no write. Asserted through a recording transaction rather than
    /// by inspection, because "the queue handler opens no transaction" is a §11.2 row.
    /// </summary>
    [Fact]
    public async Task The_queue_handler_opens_no_transaction_and_writes_nothing()
    {
        var recorder = new RecordingRequestTransaction();
        var world = new TicketsWorld(recorder);
        var customerId = world.Customers.AddActive();
        var (creator, subject) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(creator.Id), subject.Id, TicketStatus.Submitted);

        var office = world.NewAccountant(UserRole.AccountantUser);

        await world.ListPickupQueue().Handle(new ListPickupQueueRequestDto(), office, default);

        Assert.Equal(0, recorder.BeginCount);
        Assert.Equal(0, recorder.CommitCount);
        Assert.Empty(world.Audit.Entries);
        Assert.Equal(1, world.Db.Tickets.Single().Version);
        Assert.Equal(ticket.LastActivityAt, world.Db.Tickets.Single().LastActivityAt);
    }

    /// <summary>
    /// Both halves of the union are paginated TOGETHER (rule 5), the page size is capped at
    /// <c>MaxPageSize</c>, and the whole page costs ONE Identity call -- at a fifty-row page the per-row
    /// alternative is fifty extra cross-slice reads and it looks identical in a five-row test.
    /// </summary>
    [Fact]
    public async Task The_queue_caps_the_page_size_and_resolves_names_in_one_call()
    {
        var queue = new Queue();

        for (var index = 0; index < 60; index++)
            queue.Add(TicketStatus.Submitted);

        var page = await queue.Read(pageSize: 5000);

        Assert.Equal(50, page.Items.Count);
        Assert.Equal(60, page.TotalCount);
        Assert.Equal(2, page.TotalPages);

        // None of the 60 has an assignee, so the only Identity read is the batched name resolution.
        Assert.Equal(1, queue.World.Identity.FindManyCallCount);
        Assert.Equal(1, queue.World.Employees.FindManyCallCount);
    }

    // --- Taking work out of the queue ---

    [Fact]
    public async Task A_plain_pickup_is_audited_as_an_assignment_and_writes_two_system_events()
    {
        var queue = new Queue();
        var ticket = queue.Add(TicketStatus.Submitted);

        var state = await queue.World.PickupTicket().Handle(
            new PickupTicketRequestDto { TicketId = ticket.Id, Version = ticket.Version },
            queue.Office,
            default);

        Assert.Equal(TicketStatus.InReview, state.Status);
        Assert.Equal(Guid.Parse(queue.Office.Id), state.AssigneeUserAccountId);
        Assert.Equal(2, state.Version);

        Assert.Single(queue.World.Audit.WithAction(AuditActions.TicketAssigned));
        Assert.Empty(queue.World.Audit.WithAction(AuditActions.TicketReassigned));
        Assert.Single(queue.World.Audit.WithAction(AuditActions.TicketStatusChanged));

        // The status message and the assignment message. Both SystemEvents, both with a NULL author --
        // the application wrote them, not a person, and ck_ticket_messages_author requires exactly that.
        var events = queue.World.Db.TicketMessages.ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, message => Assert.Equal(TicketMessageKind.SystemEvent, message.Kind));
        Assert.All(events, message => Assert.Null(message.AuthorUserAccountId));

        // And it is out of the queue.
        Assert.Empty((await queue.Read()).Items);
    }

    /// <summary>
    /// §11.3 test 4. An <c>AccountantUser</c> -- not an Admin, §9.8 -- takes a ticket stranded by a
    /// suspension, and the audit entry must be <c>TicketReassigned</c> NAMING THE PREVIOUS ASSIGNEE.
    /// Asserting only that the pickup succeeded passes against a hardcoded <c>TicketAssigned</c>, which
    /// destroys the only record that work was taken out of somebody's hands.
    /// </summary>
    [Fact]
    public async Task Taking_a_stranded_ticket_is_audited_as_a_reassignment_naming_the_previous_assignee()
    {
        var queue = new Queue();
        var suspended = queue.World.SeedAccount(status: "Suspended");
        var ticket = queue.Add(TicketStatus.Submitted, suspended);

        var state = await queue.World.PickupTicket().Handle(
            new PickupTicketRequestDto { TicketId = ticket.Id, Version = ticket.Version },
            queue.Office,
            default);

        Assert.Equal(Guid.Parse(queue.Office.Id), state.AssigneeUserAccountId);

        var entry = Assert.Single(queue.World.Audit.WithAction(AuditActions.TicketReassigned));
        Assert.Empty(queue.World.Audit.WithAction(AuditActions.TicketAssigned));

        // The previous Assignee is in the entry's Before, and the taker in its After. Read through the
        // anonymous types the handler builds, which is the shape the Audit slice serialises.
        Assert.Equal(suspended, ReadGuid(entry.Before, "AssigneeUserAccountId"));
        Assert.Equal(Guid.Parse(queue.Office.Id), ReadGuid(entry.After, "AssigneeUserAccountId"));

        // The conversation says so too, and says it as a REASSIGNMENT.
        Assert.Contains(
            queue.World.Db.TicketMessages,
            message => message.Kind == TicketMessageKind.SystemEvent
                    && message.Body.StartsWith("Reassigned to"));
    }

    /// <summary>
    /// Suspending an Accountant changes no ticket. Nothing happens automatically on suspension (§9.8 rule
    /// 4) -- the queue is the whole mechanism -- so the row keeps its status, its Assignee and its
    /// concurrency token until somebody takes it.
    /// </summary>
    [Fact]
    public async Task Suspending_an_accountant_changes_no_ticket()
    {
        var queue = new Queue();
        var assignee = queue.World.SeedAccount();
        var ticket = queue.Add(TicketStatus.InReview, assignee);

        await queue.World.Identity.SuspendAccountAsync(assignee);

        var stored = queue.World.Db.Tickets.Single();
        Assert.Equal(TicketStatus.InReview, stored.Status);
        Assert.Equal(assignee, stored.AssigneeUserAccountId);
        Assert.Equal(ticket.Version, stored.Version);

        // It is only the queue that has changed its mind about the ticket.
        Assert.Single((await queue.Read()).Items);
    }

    [Fact]
    public async Task A_second_pickup_of_a_ticket_already_InReview_is_422()
    {
        var queue = new Queue();
        var ticket = queue.Add(TicketStatus.InReview, queue.World.SeedAccount());

        var rejected = await Assert.ThrowsAsync<AppException>(() => queue.World.PickupTicket()
            .Handle(
                new PickupTicketRequestDto { TicketId = ticket.Id, Version = ticket.Version },
                queue.Office,
                default));

        // There is no (InReview, InReview) row in the closed table and there must not be one.
        Assert.Equal(422, rejected.StatusCode);
    }

    [Fact]
    public async Task A_pickup_with_a_stale_version_is_409()
    {
        var queue = new Queue();
        var ticket = queue.Add(TicketStatus.Submitted);

        var rejected = await Assert.ThrowsAsync<AppException>(() => queue.World.PickupTicket()
            .Handle(
                new PickupTicketRequestDto { TicketId = ticket.Id, Version = ticket.Version - 1 },
                queue.Office,
                default));

        Assert.Equal(409, rejected.StatusCode);
        Assert.Empty(queue.World.Db.TicketMessages);
    }

    [Fact]
    public async Task Assigning_to_a_suspended_accountant_or_to_a_customer_side_account_is_422()
    {
        var queue = new Queue();
        var ticket = queue.Add(TicketStatus.InReview, queue.World.SeedAccount());

        var suspended = await Assert.ThrowsAsync<AppException>(() => queue.World.AssignTicket()
            .Handle(
                new AssignTicketRequestDto
                {
                    TicketId = ticket.Id,
                    Version = ticket.Version,
                    AssigneeUserAccountId = queue.World.SeedAccount(status: "Suspended"),
                },
                queue.Office,
                default));

        Assert.Equal(422, suspended.StatusCode);

        // A Customer-side target is 422 and not 403: the caller is entitled to assign, the account they
        // named is simply not an eligible assignee.
        var customerSide = await Assert.ThrowsAsync<AppException>(() => queue.World.AssignTicket()
            .Handle(
                new AssignTicketRequestDto
                {
                    TicketId = ticket.Id,
                    Version = ticket.Version,
                    AssigneeUserAccountId = Guid.Parse(queue.Creator.Id),
                },
                queue.Office,
                default));

        Assert.Equal(422, customerSide.StatusCode);
        Assert.Empty(queue.World.Audit.WithAction(AuditActions.TicketReassigned));
    }

    /// <summary>
    /// §9.9: reassignment is NOT an Admin-only power, and restricting it to one would create a fifth
    /// Admin power against the locked list of four. An <c>AccountantUser</c> reassigns an
    /// <c>AccountantAdmin</c>'s ticket, and the entry names both sides.
    /// </summary>
    [Fact]
    public async Task An_AccountantUser_reassigns_an_AccountantAdmins_ticket_and_both_are_named()
    {
        var queue = new Queue();
        var admin = queue.World.SeedAccount(UserRole.AccountantAdmin);
        var target = queue.World.SeedAccount(UserRole.AccountantUser);
        var ticket = queue.Add(TicketStatus.InReview, admin);

        var state = await queue.World.AssignTicket().Handle(
            new AssignTicketRequestDto
            {
                TicketId = ticket.Id, Version = ticket.Version, AssigneeUserAccountId = target,
            },
            queue.Office,
            default);

        Assert.Equal(target, state.AssigneeUserAccountId);
        Assert.Equal(TicketStatus.InReview, state.Status);

        var entry = Assert.Single(queue.World.Audit.WithAction(AuditActions.TicketReassigned));
        Assert.Equal(admin, ReadGuid(entry.Before, "AssigneeUserAccountId"));
        Assert.Equal(target, ReadGuid(entry.After, "AssigneeUserAccountId"));

        // The new Assignee is told; the caller is not told they assigned something away.
        Assert.Single(queue.World.Notifications.For(target));
        Assert.Empty(queue.World.Notifications.For(Guid.Parse(queue.Office.Id)));
    }

    [Fact]
    public async Task Assigning_a_ticket_to_the_account_that_already_holds_it_writes_nothing()
    {
        var queue = new Queue();
        var assignee = queue.World.SeedAccount();
        var ticket = queue.Add(TicketStatus.InReview, assignee);

        var state = await queue.World.AssignTicket().Handle(
            new AssignTicketRequestDto
            {
                TicketId = ticket.Id, Version = ticket.Version, AssigneeUserAccountId = assignee,
            },
            queue.Office,
            default);

        Assert.Equal(ticket.Version, state.Version);
        Assert.Empty(queue.World.Audit.Entries);
        Assert.Empty(queue.World.Db.TicketMessages);
        Assert.Empty(queue.World.Notifications.Sent);
    }

    /// <summary>
    /// The audit entries are anonymous types, so a test that wants one of their members has to reflect.
    /// Done in one helper rather than inline, because getting it wrong silently returns null and an
    /// <c>Assert.Equal(expected, null)</c> failure reads like a handler bug.
    /// </summary>
    private static Guid? ReadGuid(object? payload, string propertyName)
    {
        Assert.NotNull(payload);

        var property = payload.GetType().GetProperty(propertyName);
        Assert.NotNull(property);

        return (Guid?)property.GetValue(payload);
    }
}
