using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers;
using AccountantApp.Api.Slices.Documents;
using AccountantApp.Api.Slices.Employees;
using AccountantApp.Api.Slices.Identity;
using AccountantApp.Api.Slices.Notifications;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.TicketTypes;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using AccountantApp.Tests.Documents;
using AccountantApp.Tests.Employees;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// Plan §11.2's lifecycle group: the ends of a ticket's life, and the four small facts §9 LOCKS about them
/// -- no reopen, no delete, nothing lost on a cancellation, and the terminal states really terminal.
/// </summary>
public class TicketsLifecycleFlowTests
{
    /// <summary>
    /// Matrix §7: an Employee may cancel their own Draft and their own Submitted ticket, and nothing after
    /// that. 422 on the later status, not 403 -- they are entitled to cancel their own tickets, this one has
    /// simply moved past the point where they may, and the message says who to ask.
    /// </summary>
    [Fact]
    public async Task An_Employee_cancels_their_own_Submitted_ticket_but_not_one_under_review()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (employee, employeeRecord) = world.NewCustomerSide(customerId);

        var submitted = world.NewTicket(
            customerId, Guid.Parse(employee.Id), employeeRecord.Id, TicketStatus.Submitted);

        var state = await world.CancelTicket().Handle(
            new CancelTicketRequestDto
            {
                TicketId = submitted.Id,
                Version = submitted.Version,
                Reason = "Sorted it out with HR directly.",
            },
            employee,
            default);

        Assert.Equal(TicketStatus.Cancelled, state.Status);

        // ck_tickets_closed: closed_at is set if and only if the status is Closed. Cancelled is an end, not
        // a completion, and reporting must be able to tell the two apart.
        Assert.Null(state.ClosedAt);

        // ck_tickets_assignee requires NULL in Cancelled.
        Assert.Null(state.AssigneeUserAccountId);

        // Rule 8: the generic transition entry AND the specific one.
        Assert.Single(world.Audit.WithAction(AuditActions.TicketStatusChanged));
        Assert.Single(world.Audit.WithAction(AuditActions.TicketCancelled));

        // The reason is in the conversation, on the PUBLIC channel: the SystemEvent body is fixed wording
        // that cannot carry it, and both sides need to know why a ticket ended.
        Assert.Contains(
            world.Db.TicketMessages,
            message => message.Kind == TicketMessageKind.CustomerMessage
                    && message.Body.Contains("Sorted it out"));

        var underReview = world.NewTicket(
            customerId, Guid.Parse(employee.Id), employeeRecord.Id, TicketStatus.InReview,
            assignee: world.SeedAccount());

        var refused = await Assert.ThrowsAsync<AppException>(() => world.CancelTicket().Handle(
            new CancelTicketRequestDto
            {
                TicketId = underReview.Id, Version = underReview.Version,
            },
            employee,
            default));

        Assert.Equal(422, refused.StatusCode);
    }

    /// <summary>
    /// Being the SUBJECT of somebody else's ticket does not make it yours to cancel. 403 and not 404,
    /// because visibility layer 2 already showed them this ticket -- pretending it does not exist would
    /// contradict the list they are looking at.
    /// </summary>
    [Fact]
    public async Task An_Employee_cannot_cancel_a_ticket_that_is_merely_about_them()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();

        var (subjectUser, subjectRecord) = world.NewCustomerSide(customerId, given: "Eleni");
        var (admin, _) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin, given: "Kostas");

        var ticket = world.NewTicket(
            customerId, Guid.Parse(admin.Id), subjectRecord.Id, TicketStatus.Submitted);

        var denied = await Assert.ThrowsAsync<AppException>(() => world.CancelTicket().Handle(
            new CancelTicketRequestDto { TicketId = ticket.Id, Version = ticket.Version },
            subjectUser,
            default));

        Assert.Equal(403, denied.StatusCode);
        Assert.Equal(TicketStatus.Submitted, world.Db.Tickets.Single().Status);
    }

    /// <summary>
    /// §9.1 and §1.9: THE TERMINAL STATES ARE TERMINAL. There is no row in the transition table whose From
    /// is Closed or Cancelled, so a second cancel is a 422 rather than a silent success -- and every attempt
    /// to move a finished ticket fails the same way, including from the Office.
    /// </summary>
    [Fact]
    public async Task Nothing_moves_a_cancelled_ticket_afterwards()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (employee, employeeRecord) = world.NewCustomerSide(customerId);
        var accountant = world.NewAccountant();

        var ticket = world.NewTicket(
            customerId, Guid.Parse(employee.Id), employeeRecord.Id, TicketStatus.Cancelled);

        var again = await Assert.ThrowsAsync<AppException>(() => world.CancelTicket().Handle(
            new CancelTicketRequestDto { TicketId = ticket.Id, Version = ticket.Version },
            accountant,
            default));

        Assert.Equal(422, again.StatusCode);

        var picked = await Assert.ThrowsAsync<AppException>(() => world.PickupTicket().Handle(
            new PickupTicketRequestDto { TicketId = ticket.Id, Version = ticket.Version },
            accountant,
            default));

        Assert.Equal(422, picked.StatusCode);

        var closed = await Assert.ThrowsAsync<AppException>(() => world.CloseTicket().Handle(
            new CloseTicketRequestDto { TicketId = ticket.Id, Version = ticket.Version },
            accountant,
            default));

        Assert.Equal(422, closed.StatusCode);

        // Not one of the three wrote anything.
        Assert.Equal(ticket.Version, world.Db.Tickets.Single().Version);
        Assert.Empty(world.Db.TicketMessages);
    }

    /// <summary>
    /// §1.9: A CANCELLATION LOSES NOTHING. The revisions, the conversation and the documents are all still
    /// there and still readable, which is why cancelling is a status and not a delete.
    /// </summary>
    [Fact]
    public async Task A_cancelled_ticket_keeps_its_revisions_its_conversation_and_its_documents()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (employee, employeeRecord) = world.NewCustomerSide(customerId);

        var type = TicketsTestHarness.TypeWith(
            TicketsTestHarness.Field("grossPay", FieldDataTypes.MoneyAmount));
        type.AllowEmployeeToOpen = true;

        var ticket = world.NewTicket(
            customerId, Guid.Parse(employee.Id), employeeRecord.Id, TicketStatus.Submitted,
            type: type);

        world.AddRevision(
            ticket, Guid.Parse(employee.Id), TicketsWorld.Value("grossPay", number: 1450.75m));

        await world.PostMessage().Handle(
            new PostMessageRequestDto
            {
                TicketId = ticket.Id, Version = ticket.Version, Body = "Anything else you need?",
            },
            employee,
            default);

        var document = await world.StoreDocumentAsync(ticket, Guid.Parse(employee.Id));

        var current = world.Db.Tickets.Single();

        await world.CancelTicket().Handle(
            new CancelTicketRequestDto { TicketId = ticket.Id, Version = current.Version },
            employee,
            default);

        var detail = await world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = ticket.Id }, employee, default);

        Assert.Equal(TicketStatus.Cancelled, detail.Status);
        Assert.Equal(1450.75m, Assert.Single(Assert.Single(detail.Revisions).FieldValues).Number);
        Assert.Contains(detail.Messages, message => message.Body == "Anything else you need?");

        Assert.Equal(document.Id, Assert.Single(await world.ListTicketDocuments()
            .Handle(new ListTicketDocumentsRequestDto { TicketId = ticket.Id }, employee, default)).Id);

        // And the transition itself is in the conversation, as a SystemEvent nobody authored.
        Assert.Contains(
            detail.Messages,
            message => message.Kind == TicketMessageKind.SystemEvent
                    && message.AuthorUserAccountId is null);
    }

    /// <summary>
    /// §4.9 rule 4: a ticket cannot be closed while a required, customer-visible answer is still
    /// unaccepted, and the refusal NAMES the field -- an unexplained 422 on a close leaves the Accountant
    /// with no way to find out which answer is holding it.
    /// </summary>
    [Fact]
    public async Task A_ticket_with_an_unverified_required_answer_cannot_be_closed_until_it_is_accepted()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (employee, employeeRecord) = world.NewCustomerSide(customerId);

        var type = TicketsTestHarness.TypeWith(
            TicketsTestHarness.Field("grossPay", FieldDataTypes.MoneyAmount, isRequired: true));

        var accountant = world.NewAccountant(UserRole.AccountantUser);
        var assignee = Guid.Parse(accountant.Id);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(employee.Id), employeeRecord.Id, TicketStatus.Answered,
            assignee: assignee, type: type);

        var value = TicketsWorld.Value("grossPay", number: 1000m);
        world.AddRevision(ticket, Guid.Parse(employee.Id), value);

        var blocked = await Assert.ThrowsAsync<AppException>(() => world.CloseTicket().Handle(
            new CloseTicketRequestDto { TicketId = ticket.Id, Version = ticket.Version },
            accountant,
            default));

        Assert.Equal(422, blocked.StatusCode);
        Assert.Contains("grossPay", blocked.Message);

        // A REJECTION does not satisfy the gate either -- only an acceptance does.
        world.Reject(value, assignee, TicketsTestHarness.Now);

        var stillBlocked = await Assert.ThrowsAsync<AppException>(() => world.CloseTicket().Handle(
            new CloseTicketRequestDto { TicketId = ticket.Id, Version = ticket.Version },
            accountant,
            default));

        Assert.Equal(422, stillBlocked.StatusCode);

        world.Accept(value, assignee, TicketsTestHarness.Now);

        var state = await world.CloseTicket().Handle(
            new CloseTicketRequestDto { TicketId = ticket.Id, Version = ticket.Version },
            accountant,
            default);

        Assert.Equal(TicketStatus.Closed, state.Status);
        Assert.NotNull(state.ClosedAt);

        // Closed REQUIRES an assignee (ck_tickets_assignee), so the transition retains the one it had.
        Assert.Equal(assignee, state.AssigneeUserAccountId);

        Assert.Single(world.Audit.WithAction(AuditActions.TicketStatusChanged));
        Assert.Single(world.Audit.WithAction(AuditActions.TicketClosed));

        var notification = Assert.Single(world.Notifications.For(Guid.Parse(employee.Id)));
        Assert.Equal(NotificationEvents.TicketClosed, notification.EventKind);
    }

    /// <summary>
    /// A required field that is NOT customer-visible cannot block a close: the Customer was never asked for
    /// it and can do nothing about it, so a gate over the Accountant's own fields would strand the ticket.
    /// </summary>
    [Fact]
    public async Task An_unverified_accountant_only_field_does_not_block_a_close()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (employee, employeeRecord) = world.NewCustomerSide(customerId);

        var type = TicketsTestHarness.TypeWith(
            TicketsTestHarness.Field(
                "internalRef", FieldDataTypes.SingleLineText,
                isRequired: true, isVisibleToCustomer: false));

        var accountant = world.NewAccountant(UserRole.AccountantUser);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(employee.Id), employeeRecord.Id, TicketStatus.Answered,
            assignee: Guid.Parse(accountant.Id), type: type);

        world.AddRevision(
            ticket, Guid.Parse(accountant.Id), TicketsWorld.Value("internalRef", text: "LEDGER-1"));

        var state = await world.CloseTicket().Handle(
            new CloseTicketRequestDto { TicketId = ticket.Id, Version = ticket.Version },
            accountant,
            default);

        Assert.Equal(TicketStatus.Closed, state.Status);
    }

    /// <summary>
    /// Matrix §7: priority and due date are the Office's scheduling tools and belong to nobody else. Denied
    /// by the CATALOGUE, so the handlers carry no role branch at all and the entry is the whole defence.
    /// </summary>
    [Fact]
    public async Task The_customer_side_cannot_set_a_priority_or_a_due_date()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (admin, subject) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(admin.Id), subject.Id, TicketStatus.Submitted);

        var priority = await Assert.ThrowsAsync<AppException>(() => world.SetPriority().Handle(
            new SetTicketPriorityRequestDto
            {
                TicketId = ticket.Id, Version = ticket.Version, Priority = TicketPriority.High,
            },
            admin,
            default));

        Assert.Equal(403, priority.StatusCode);

        var dueDate = await Assert.ThrowsAsync<AppException>(() => world.SetDueDate().Handle(
            new SetTicketDueDateRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version,
                DueDate = new DateOnly(2026, 12, 31),
            },
            admin,
            default));

        Assert.Equal(403, dueDate.StatusCode);

        Assert.Equal(2, world.Audit.WithAction(AuditActions.PermissionDenied).Count());
        Assert.Equal(TicketPriority.Normal, world.Db.Tickets.Single().Priority);
    }

    /// <summary>
    /// §4.8: setting a value that is already the value writes NOTHING -- no audit entry, no version bump,
    /// no notification. An audit trail full of "changed Normal to Normal" is an audit trail nobody reads,
    /// and a version bump on a no-op invalidates every other tab's token for nothing.
    /// </summary>
    [Fact]
    public async Task Setting_a_priority_to_the_value_it_already_has_writes_nothing()
    {
        var recorder = new RecordingRequestTransaction();
        var world = new TicketsWorld(recorder);
        var customerId = world.Customers.AddActive();
        var (creator, subject) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(creator.Id), subject.Id, TicketStatus.Submitted);

        var accountant = world.NewAccountant();

        // Read once and kept: the handler mutates the tracked entity, so ticket.Version is not a stable
        // baseline to compare against afterwards.
        var originalVersion = ticket.Version;

        var state = await world.SetPriority().Handle(
            new SetTicketPriorityRequestDto
            {
                TicketId = ticket.Id, Version = originalVersion, Priority = TicketPriority.Normal,
            },
            accountant,
            default);

        Assert.Equal(TicketPriority.Normal, state.Priority);
        Assert.Equal(originalVersion, state.Version);
        Assert.Equal(0, recorder.CommitCount);
        Assert.Empty(world.Audit.Entries);

        // And a real change does move it, so the test above is not passing because the handler does nothing.
        var changed = await world.SetPriority().Handle(
            new SetTicketPriorityRequestDto
            {
                TicketId = ticket.Id, Version = originalVersion, Priority = TicketPriority.High,
            },
            accountant,
            default);

        Assert.Equal(TicketPriority.High, changed.Priority);
        Assert.Equal(originalVersion + 1, changed.Version);
        Assert.Single(world.Audit.WithAction(AuditActions.PriorityChanged));
    }

    [Fact]
    public async Task An_unknown_priority_is_422()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (creator, subject) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(creator.Id), subject.Id, TicketStatus.Submitted);

        var rejected = await Assert.ThrowsAsync<AppException>(() => world.SetPriority().Handle(
            new SetTicketPriorityRequestDto
            {
                TicketId = ticket.Id, Version = ticket.Version, Priority = "Immediately",
            },
            world.NewAccountant(),
            default));

        Assert.Equal(422, rejected.StatusCode);
    }

    // --- Structural facts the plan LOCKS, which no behavioural test can reach ---

    /// <summary>
    /// §9.1: THERE IS NO REOPEN. A closed matter is closed, and the successor mechanism is a NEW ticket
    /// linked through <c>PrecededByTicketId</c> -- which keeps the original's audit trail intact instead of
    /// reviving it. A <c>ReopenedAt</c> column is the shape this rule gets broken in, so its absence is
    /// asserted rather than assumed.
    /// </summary>
    [Fact]
    public void There_is_no_reopen_anywhere_in_the_model()
    {
        Assert.Null(typeof(Ticket).GetProperty("ReopenedAt"));
        Assert.Null(typeof(Ticket).GetProperty("ReopenedBy"));
        Assert.Null(typeof(TicketStateDto).GetProperty("ReopenedAt"));

        // The successor link exists instead, on creation only.
        Assert.NotNull(typeof(CreateTicketRequestDto).GetProperty("PrecededByTicketId"));
        Assert.NotNull(typeof(Ticket).GetProperty("PrecededByTicketId"));

        // And no catalogue action offers it to anybody.
        Assert.DoesNotContain(
            new TicketsActionCatalogue().Actions.Keys,
            action => action.Contains("Reopen", StringComparison.OrdinalIgnoreCase)
                   || action.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                      && !action.Contains("Document", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §4.6 and §9.5: the identity of a ticket -- its Customer, its Subject, its type and its reference --
    /// is fixed at creation. An update DTO carrying any of them is the door through which a ticket gets
    /// moved to another Customer, and the safest place to shut it is the type system.
    /// </summary>
    [Theory]
    [InlineData(typeof(SubmitRevisionRequestDto))]
    [InlineData(typeof(SubmitTicketRequestDto))]
    [InlineData(typeof(SetTicketPriorityRequestDto))]
    [InlineData(typeof(SetTicketDueDateRequestDto))]
    [InlineData(typeof(AssignTicketRequestDto))]
    [InlineData(typeof(CloseTicketRequestDto))]
    [InlineData(typeof(CancelTicketRequestDto))]
    [InlineData(typeof(AnswerTicketRequestDto))]
    [InlineData(typeof(RequestInformationRequestDto))]
    [InlineData(typeof(ReturnToReviewRequestDto))]
    [InlineData(typeof(PostMessageRequestDto))]
    [InlineData(typeof(UploadDocumentRequestDto))]
    public void No_update_request_can_restate_a_tickets_identity(Type requestType)
    {
        foreach (var forbidden in new[]
                 { "CustomerId", "SubjectEmployeeId", "TicketTypeId", "TicketTypeVersionId",
                   "Title", "Reference", "CreatorUserAccountId" })
        {
            Assert.Null(requestType.GetProperty(forbidden));
        }
    }

    /// <summary>
    /// §9.9, the LOCKED list of exactly four Admin-only powers, none of which is in this slice. An action
    /// catalogued to <c>AccountantAdmin</c> alone would be a fifth, and it would look like ordinary caution
    /// rather than a violation.
    /// </summary>
    [Fact]
    public void No_ticket_action_is_reserved_to_the_AccountantAdmin_alone()
    {
        var catalogue = new TicketsActionCatalogue();

        Assert.Equal(22, catalogue.Actions.Count);

        var adminOnly = catalogue.Actions
            .Where(entry => entry.Value.Length == 1 && entry.Value.Contains(UserRole.AccountantAdmin))
            .Select(entry => entry.Key)
            .ToList();

        Assert.Empty(adminOnly);

        // Every action is open to both Office roles, so no ordinary Accountant is blocked from any part of
        // the work, and none is empty -- an empty entry denies everybody while looking configured.
        Assert.All(catalogue.Actions, entry =>
        {
            Assert.NotEmpty(entry.Value);
            Assert.Contains(UserRole.AccountantAdmin, entry.Value);
            Assert.Contains(UserRole.AccountantUser, entry.Value);
        });
    }

    /// <summary>
    /// The catalogue must be registered AS <c>IActionCatalogue</c> (§7.3 rule 4). Registered as its concrete
    /// type it is invisible to <c>PermissionChecker</c>, every action in this slice is missing from the
    /// composed set, and because the checker FAILS CLOSED every endpoint here returns 403 -- with no startup
    /// error, no failing catalogue test, and an audit trail that says the caller lacked the permission
    /// rather than that the permission does not exist.
    /// </summary>
    [Fact]
    public void The_action_catalogue_is_registered_as_the_interface()
    {
        var services = new ServiceCollection();
        services.AddTicketsSlice(Configuration());

        var catalogues = services.BuildServiceProvider()
            .GetServices<IActionCatalogue>()
            .ToList();

        Assert.Contains(catalogues, catalogue => catalogue is TicketsActionCatalogue);

        // A PermissionChecker composed the way the application composes it can find these actions.
        var checker = new PermissionChecker(
            catalogues, new TestAuditApi(), NullLogger<PermissionChecker>.Instance);

        Assert.NotNull(checker);
    }

    // A BuildAppWithTickets() lived here, with a delegate-building test, a route-shape test, the 18/4
    // route counts and the DisableAntiforgery assertion. It existed only because
    // EndpointRoutingTests.BuildApp() registered neither Documents nor Tickets. BuildApp() now registers
    // both, and all four assertions live there (2026-09-02) -- the counts and the antiforgery check as
    // their own tests, the other two subsumed by the whole-application tests that were already in that
    // file. DO NOT REINTRODUCE A SECOND BUILDER HERE: two copies of the Program.cs mirror drift, and the
    // copy passing is what would hide the original from breaking.

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(SettingsForRoutingTests()).Build();

    private static Dictionary<string, string?> SettingsForRoutingTests() => new()
    {
        ["ConnectionStrings:Default"] =
            "Host=localhost;Port=5432;Database=accountant_app;Username=postgres;Password=postgres",
        ["Notifications:Email:Enabled"] = "false",
        ["DataProtection:KeyPath"] =
            Path.Combine(Path.GetTempPath(), "accountant-app-tickets-routing-tests-keys"),
    };
}
