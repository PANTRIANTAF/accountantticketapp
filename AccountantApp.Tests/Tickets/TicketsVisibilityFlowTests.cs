using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// Plan §11.2, the visibility and conversation groups, exercised THROUGH THE HANDLERS.
///
/// <c>TicketVisibilityTests</c> already covers the query builder in isolation. These tests exist because
/// the rule the plan states is about the RESPONSE: a miss must be a 404 with the same body as a
/// nonexistent id, and that is a property of the handler, not of the <c>IQueryable</c>. A handler that
/// composed the filter correctly and then threw 403 on the miss would pass every helper test.
/// </summary>
public class TicketsVisibilityFlowTests
{
    private static FieldDescriptorDetailDto CustomerField(string key = "grossPay") =>
        TicketsTestHarness.Field(key, FieldDataTypes.MoneyAmount);

    private static TicketTypeDetailDto EmployeeOpenableType(params FieldDescriptorDetailDto[] fields)
    {
        var type = TicketsTestHarness.TypeWith(fields);
        type.AllowEmployeeToOpen = true;
        return type;
    }

    /// <summary>
    /// §11.3 test 1, the one the plan says everybody writes as the Employee case and thereby misses.
    ///
    /// Layer 3 sits OUTSIDE the Employee branch, so an AccountantAdmin -- who is exempt from layers 1
    /// and 2 -- must still get 404 on a Draft somebody else created. The creator's own 200 in the same
    /// test is what proves the 404 came from layer 3 rather than from a fixture that was never visible
    /// to anyone.
    /// </summary>
    [Fact]
    public async Task An_AccountantAdmin_gets_404_on_a_customers_Draft_and_its_creator_does_not()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (creator, subject) = world.NewCustomerSide(customerId);

        var draft = world.NewTicket(
            customerId, Guid.Parse(creator.Id), subject.Id, TicketStatus.Draft,
            type: EmployeeOpenableType(CustomerField()));

        var accountant = world.NewAccountant();

        var denied = await Assert.ThrowsAsync<AppException>(() => world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = draft.Id }, accountant, default));

        Assert.Equal(404, denied.StatusCode);

        // The same id, the same handler, the creator's session: 200. Without this half the test would
        // also pass against a filter that hid the ticket from everybody.
        var mine = await world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = draft.Id }, creator, default);

        Assert.Equal(draft.Id, mine.Id);
        Assert.Equal(TicketStatus.Draft, mine.Status);
    }

    [Fact]
    public async Task An_AccountantUsers_ticket_list_contains_no_Draft_of_any_customer()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (creator, subject) = world.NewCustomerSide(customerId);

        world.NewTicket(customerId, Guid.Parse(creator.Id), subject.Id, TicketStatus.Draft);
        var submitted = world.NewTicket(
            customerId, Guid.Parse(creator.Id), subject.Id, TicketStatus.Submitted);

        var accountant = world.NewAccountant(UserRole.AccountantUser);

        var page = await world.ListTickets().Handle(
            new ListTicketsRequestDto { Scope = TicketListScopes.All }, accountant, default);

        Assert.Equal(1, page.TotalCount);
        var row = Assert.Single(page.Items);
        Assert.Equal(submitted.Id, row.Id);

        // CustomerName is populated for an Accountant-side reader, through the BATCH read. This was
        // silently always null: FakeCustomerApi.FindManyAsync returned an empty dictionary while its own
        // FindAsync resolved the same id, and an absent entry reads as "no name" rather than as an error.
        Assert.Equal("Acme", row.CustomerName);
    }

    [Fact]
    public async Task A_CustomerAdmin_reading_another_customers_ticket_gets_404_not_403()
    {
        var world = new TicketsWorld();
        var mine = world.Customers.AddActive();
        var theirs = world.Customers.AddActive();

        var (stranger, strangerEmployee) = world.NewCustomerSide(theirs);
        var ticket = world.NewTicket(
            theirs, Guid.Parse(stranger.Id), strangerEmployee.Id, TicketStatus.Submitted);

        var (admin, _) = world.NewCustomerSide(mine, UserRole.CustomerAdmin);

        var denied = await Assert.ThrowsAsync<AppException>(() => world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = ticket.Id }, admin, default));

        // 403 here would confirm the ticket exists, which is exactly the enumeration oracle §3.1 forbids.
        Assert.Equal(404, denied.StatusCode);
    }

    [Fact]
    public async Task A_CustomerAdmin_gets_404_on_an_Employees_Draft_at_their_own_customer()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (employee, employeeRecord) = world.NewCustomerSide(customerId);

        var draft = world.NewTicket(
            customerId, Guid.Parse(employee.Id), employeeRecord.Id, TicketStatus.Draft);

        var (admin, _) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var denied = await Assert.ThrowsAsync<AppException>(() => world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = draft.Id }, admin, default));

        Assert.Equal(404, denied.StatusCode);
    }

    [Fact]
    public async Task An_Employee_gets_404_on_a_colleagues_ticket_at_their_own_customer()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();

        var (colleague, colleagueRecord) = world.NewCustomerSide(customerId, given: "Nikos");
        var ticket = world.NewTicket(
            customerId, Guid.Parse(colleague.Id), colleagueRecord.Id, TicketStatus.Submitted);

        var (caller, _) = world.NewCustomerSide(customerId, given: "Eleni");

        var denied = await Assert.ThrowsAsync<AppException>(() => world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = ticket.Id }, caller, default));

        // Layer 2: same Customer, so layer 1 passed. Neither Creator nor Subject.
        Assert.Equal(404, denied.StatusCode);
    }

    [Fact]
    public async Task An_Employee_reads_a_non_Draft_ticket_where_they_are_the_Subject_but_not_the_Creator()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();

        var (subjectUser, subjectRecord) = world.NewCustomerSide(customerId, given: "Eleni");
        var (admin, _) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin, given: "Kostas");

        var ticket = world.NewTicket(
            customerId, Guid.Parse(admin.Id), subjectRecord.Id, TicketStatus.InReview,
            type: EmployeeOpenableType(CustomerField()));

        var detail = await world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = ticket.Id }, subjectUser, default);

        Assert.Equal(ticket.Id, detail.Id);
    }

    /// <summary>§9.3, LOCKED: the Subject does not see a Draft about them. Only its Creator does.</summary>
    [Fact]
    public async Task An_Employee_gets_404_on_a_Draft_where_they_are_the_Subject()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();

        var (subjectUser, subjectRecord) = world.NewCustomerSide(customerId, given: "Eleni");
        var (admin, _) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin, given: "Kostas");

        var draft = world.NewTicket(
            customerId, Guid.Parse(admin.Id), subjectRecord.Id, TicketStatus.Draft,
            type: EmployeeOpenableType(CustomerField()));

        var denied = await Assert.ThrowsAsync<AppException>(() => world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = draft.Id }, subjectUser, default));

        Assert.Equal(404, denied.StatusCode);
    }

    /// <summary>§9.6 rule 1: a departure is not a retraction. The history stays readable, permanently.</summary>
    [Fact]
    public async Task A_CustomerAdmin_still_reads_a_departed_Employees_old_tickets()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();

        var departed = world.Employees.Add(
            customerId, userAccountId: null, status: "Departed", given: "Giorgos");

        var (admin, _) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(admin.Id), departed.Id, TicketStatus.Closed);

        var detail = await world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = ticket.Id }, admin, default);

        Assert.Equal(ticket.Id, detail.Id);
        Assert.Equal("Giorgos Papadopoulou", detail.SubjectName);
    }

    /// <summary>
    /// An Employee-role account with no Employee record. The plan permits "empty result or 401, never an
    /// unfiltered query"; the shipped choice is an empty result, so the list is empty and the direct read
    /// is a 404 -- and CRUCIALLY the ticket used here is one that would be visible with the filter
    /// dropped, so a regression to "no filter" fails this test rather than passing it.
    /// </summary>
    [Fact]
    public async Task An_Employee_with_no_Employee_record_sees_nothing()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();

        var (colleague, colleagueRecord) = world.NewCustomerSide(customerId);
        var ticket = world.NewTicket(
            customerId, Guid.Parse(colleague.Id), colleagueRecord.Id, TicketStatus.Submitted);

        // A session with no Employee row behind it at all.
        var orphan = TicketsTestHarness.CustomerSide(
            world.SeedAccount(UserRole.Employee), UserRole.Employee, customerId);

        var page = await world.ListTickets().Handle(
            new ListTicketsRequestDto { Scope = TicketListScopes.MyCustomer }, orphan, default);

        Assert.Empty(page.Items);

        var denied = await Assert.ThrowsAsync<AppException>(() => world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = ticket.Id }, orphan, default));

        Assert.Equal(404, denied.StatusCode);
    }

    /// <summary>
    /// §4.3 rule 1: an Office-only scope asked for by a Customer-side caller is a 403, NOT a silently
    /// narrowed result. Quietly reinterpreting it would let a Customer Admin believe they had
    /// cross-Customer visibility and act on a list they think is complete.
    /// </summary>
    [Fact]
    public async Task A_CustomerAdmin_asking_for_the_All_scope_is_refused_rather_than_narrowed()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (admin, _) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var denied = await Assert.ThrowsAsync<AppException>(() => world.ListTickets()
            .Handle(new ListTicketsRequestDto { Scope = TicketListScopes.All }, admin, default));

        Assert.Equal(403, denied.StatusCode);
    }

    [Fact]
    public async Task An_unknown_scope_is_422()
    {
        var world = new TicketsWorld();
        var accountant = world.NewAccountant();

        var rejected = await Assert.ThrowsAsync<AppException>(() => world.ListTickets()
            .Handle(new ListTicketsRequestDto { Scope = "Everything" }, accountant, default));

        Assert.Equal(422, rejected.StatusCode);
    }

    /// <summary>
    /// The pickup queue is denied by the CATALOGUE, and the denial is audited. Both halves matter: the
    /// handler carries no role branch, so the catalogue entry is the entire defence, and a denial that
    /// wrote no audit entry would leave a probing session invisible.
    /// </summary>
    [Fact]
    public async Task A_CustomerAdmin_is_denied_the_pickup_queue_and_the_denial_is_audited()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (admin, _) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var denied = await Assert.ThrowsAsync<AppException>(() => world.ListPickupQueue()
            .Handle(new ListPickupQueueRequestDto(), admin, default));

        Assert.Equal(403, denied.StatusCode);
        Assert.Single(world.Audit.WithAction(AuditActions.PermissionDenied));
    }

    /// <summary>
    /// Layer 4. The note is ABSENT from the JSON, not flagged: a message the Customer side must not read
    /// is not something the React app is trusted to hide.
    /// </summary>
    [Fact]
    public async Task Internal_notes_are_absent_for_the_customer_side_and_present_for_the_Office()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (creator, subject) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(creator.Id), subject.Id, TicketStatus.InReview);

        var accountant = world.NewAccountant(UserRole.AccountantUser);

        world.Db.TicketMessages.AddRange(
            new TicketMessage
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                AuthorUserAccountId = Guid.Parse(creator.Id),
                Kind = TicketMessageKind.CustomerMessage,
                Body = "Here are the payslips.",
                CreatedAt = TicketsTestHarness.Now,
            },
            new TicketMessage
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                AuthorUserAccountId = Guid.Parse(accountant.Id),
                Kind = TicketMessageKind.InternalNote,
                Body = "Third time they have sent the wrong month.",
                CreatedAt = TicketsTestHarness.Now,
            });

        await world.Db.SaveChangesAsync();

        var asAdmin = await world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = ticket.Id }, creator, default);

        Assert.Equal(TicketMessageKind.CustomerMessage, Assert.Single(asAdmin.Messages).Kind);
        Assert.DoesNotContain(asAdmin.Messages, message => message.Body.Contains("wrong month"));

        var asOffice = await world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = ticket.Id }, accountant, default);

        Assert.Equal(2, asOffice.Messages.Count);
        Assert.Contains(asOffice.Messages, message => message.Kind == TicketMessageKind.InternalNote);
    }

    [Fact]
    public async Task A_CustomerAdmin_posting_an_internal_note_is_denied_by_the_catalogue()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (admin, subject) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(admin.Id), subject.Id, TicketStatus.InReview);

        var denied = await Assert.ThrowsAsync<AppException>(() => world.PostMessage()
            .HandleInternalNote(
                new PostMessageRequestDto
                {
                    TicketId = ticket.Id,
                    Version = ticket.Version,
                    Body = "Not for their eyes.",
                },
                admin,
                default));

        Assert.Equal(403, denied.StatusCode);

        // Denied BEFORE the write: no message row, and the ticket's token did not move.
        Assert.Empty(world.Db.TicketMessages);
        Assert.Equal(1, world.Db.Tickets.Single().Version);
    }

    /// <summary>
    /// The message kind comes from the ROLE. There is no <c>kind</c> on the request DTO to ignore --
    /// which is the strongest form of "ignored", and this test pins it by asserting the property does not
    /// exist.
    /// </summary>
    [Fact]
    public async Task The_message_kind_is_derived_from_the_role_and_cannot_be_supplied()
    {
        Assert.Null(typeof(PostMessageRequestDto).GetProperty("Kind"));

        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (creator, subject) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(creator.Id), subject.Id, TicketStatus.InReview);

        var fromCustomer = await world.PostMessage().Handle(
            new PostMessageRequestDto
            {
                TicketId = ticket.Id, Version = ticket.Version, Body = "Sent this morning.",
            },
            creator,
            default);

        Assert.Equal(TicketMessageKind.CustomerMessage, fromCustomer.Kind);

        var accountant = world.NewAccountant(UserRole.AccountantUser);

        var fromOffice = await world.PostMessage().Handle(
            new PostMessageRequestDto
            {
                TicketId = ticket.Id, Version = fromCustomer.Ticket.Version, Body = "Received, thanks.",
            },
            accountant,
            default);

        Assert.Equal(TicketMessageKind.AccountantResponse, fromOffice.Kind);
    }

    [Fact]
    public async Task An_internal_note_notifies_nobody_on_the_customer_side()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (creator, subject) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(creator.Id), subject.Id, TicketStatus.InReview,
            assignee: world.SeedAccount());

        var accountant = world.NewAccountant(UserRole.AccountantUser);

        await world.PostMessage().HandleInternalNote(
            new PostMessageRequestDto
            {
                TicketId = ticket.Id, Version = ticket.Version, Body = "Chase the payroll file.",
            },
            accountant,
            default);

        Assert.Empty(world.Notifications.For(Guid.Parse(creator.Id)));
    }

    /// <summary>
    /// The <c>AllowEmployeeToOpen = false</c> fallback in <c>TicketAccess.ResolveResponseVersionAsync</c>.
    /// A Customer Admin opens a ticket ABOUT an Employee under a type that Employee may not open; the
    /// Employee is the Subject, so visibility layer 2 grants the read, and without the fallback they
    /// would receive a ticket with no field labels and no values -- or a 422 on their own ticket.
    /// </summary>
    [Fact]
    public async Task A_Subject_reads_a_ticket_of_a_type_they_may_not_open()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();

        var (subjectUser, subjectRecord) = world.NewCustomerSide(customerId, given: "Eleni");
        var (admin, _) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin, given: "Kostas");

        // AllowEmployeeToOpen stays false: GetVersionByIdAsync(..., Employee, ...) returns null.
        var type = TicketsTestHarness.TypeWith(CustomerField("bonus"));

        var ticket = world.NewTicket(
            customerId, Guid.Parse(admin.Id), subjectRecord.Id, TicketStatus.InReview, type: type);

        world.AddRevision(
            ticket, Guid.Parse(admin.Id), TicketsWorld.Value("bonus", number: 1200.50m));

        var detail = await world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = ticket.Id }, subjectUser, default);

        Assert.Equal("bonus", Assert.Single(detail.Fields).Key);
        Assert.Equal(1200.50m, Assert.Single(Assert.Single(detail.Revisions).FieldValues).Number);
    }
}
