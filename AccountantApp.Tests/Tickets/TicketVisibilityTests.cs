using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Slices.Tickets.Application;
using AccountantApp.Api.Slices.Tickets.Core;
using Microsoft.EntityFrameworkCore;
using static AccountantApp.Tests.Tickets.TicketsTestHarness;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// The four visibility layers, plan section 3.1. These are behavioural and need no real PostgreSQL: the
/// layers are LINQ predicates, and the in-memory provider translates them exactly as Npgsql would.
///
/// PLAN SECTION 11.3 TEST 1 IS THE FIRST TEST BELOW. Everyone writes the Employee draft test, which
/// passes whether or not layer 3 sits outside the role branch. Only the Accountant case catches section
/// 3.1 rule 2 -- and that failure exposes every Customer's half-finished drafts, containing payroll
/// data, to the whole Office.
/// </summary>
public sealed class TicketVisibilityTests
{
    private readonly Guid _customerA = Guid.NewGuid();
    private readonly Guid _customerB = Guid.NewGuid();

    private readonly Guid _creatorAccount = Guid.NewGuid();
    private readonly Guid _creatorEmployee = Guid.NewGuid();
    private readonly Guid _colleagueAccount = Guid.NewGuid();
    private readonly Guid _colleagueEmployee = Guid.NewGuid();
    private readonly Guid _subjectEmployee = Guid.NewGuid();
    private readonly Guid _subjectAccount = Guid.NewGuid();

    /// <summary>
    /// PLAN SECTION 11.3 TEST 1 / SECTION 3.1 RULE 2. Layer 3 is outside the Employee branch, so it
    /// constrains Accountants too. Matrix section 6: "No Accountant ever sees drafts."
    /// </summary>
    [Fact]
    public async Task An_Accountant_cannot_see_a_Customers_draft()
    {
        await using var db = NewDb();
        var draft = NewTicket(_customerA, _creatorAccount, _subjectEmployee);
        db.Tickets.Add(draft);
        await db.SaveChangesAsync();

        foreach (var role in new[] { UserRole.AccountantAdmin, UserRole.AccountantUser })
        {
            var visible = await db.Tickets
                .WhereTicketVisible(Accountant(Guid.NewGuid(), role), null)
                .ToListAsync();

            Assert.Empty(visible);
        }
    }

    /// <summary>
    /// The other side of layer 3: an Accountant sees everything that is not a Draft, at any Customer,
    /// because layer 1 lets them through. If this failed, the Office could not work at all.
    /// </summary>
    [Fact]
    public async Task An_Accountant_sees_non_draft_tickets_at_every_customer()
    {
        await using var db = NewDb();
        db.Tickets.AddRange(
            NewTicket(_customerA, _creatorAccount, _subjectEmployee, TicketStatus.Submitted),
            NewTicket(_customerB, _colleagueAccount, _colleagueEmployee, TicketStatus.Answered,
                Guid.NewGuid()));
        await db.SaveChangesAsync();

        var visible = await db.Tickets
            .WhereTicketVisible(Accountant(Guid.NewGuid()), null)
            .ToListAsync();

        Assert.Equal(2, visible.Count);
    }

    /// <summary>
    /// An Accountant DOES see their own draft, if such a thing exists -- layer 3 is "only its Creator",
    /// not "no Accountant, ever". Written to pin the rule as stated rather than as a blanket ban, which
    /// would be a different rule that happens to pass the test above.
    /// </summary>
    [Fact]
    public async Task Layer_three_is_creator_privacy_not_a_blanket_draft_ban()
    {
        await using var db = NewDb();
        var accountantAccount = Guid.NewGuid();
        db.Tickets.Add(NewTicket(_customerA, accountantAccount, _subjectEmployee));
        await db.SaveChangesAsync();

        var visible = await db.Tickets
            .WhereTicketVisible(Accountant(accountantAccount), null)
            .ToListAsync();

        Assert.Single(visible);
    }

    [Fact]
    public async Task A_CustomerAdmin_cannot_see_another_customers_ticket()
    {
        await using var db = NewDb();
        db.Tickets.Add(NewTicket(_customerB, _colleagueAccount, _colleagueEmployee,
            TicketStatus.Submitted));
        await db.SaveChangesAsync();

        // Filtered out, which is what makes the handler return 404 rather than 403. An out-of-scope
        // ticket must not be distinguishable from a nonexistent one.
        var visible = await db.Tickets
            .WhereTicketVisible(CustomerSide(Guid.NewGuid(), UserRole.CustomerAdmin, _customerA), null)
            .ToListAsync();

        Assert.Empty(visible);
    }

    /// <summary>
    /// Layer 3 again, this time for the role that has the widest reach inside a Customer. A Customer
    /// Admin sees everything at their Customer EXCEPT somebody else's Draft.
    /// </summary>
    [Fact]
    public async Task A_CustomerAdmin_cannot_see_an_employees_draft_at_their_own_customer()
    {
        await using var db = NewDb();
        db.Tickets.AddRange(
            NewTicket(_customerA, _creatorAccount, _creatorEmployee),
            NewTicket(_customerA, _creatorAccount, _creatorEmployee, TicketStatus.Submitted));
        await db.SaveChangesAsync();

        var visible = await db.Tickets
            .WhereTicketVisible(CustomerSide(Guid.NewGuid(), UserRole.CustomerAdmin, _customerA), null)
            .ToListAsync();

        Assert.Equal(TicketStatus.Submitted, Assert.Single(visible).Status);
    }

    /// <summary>
    /// CustomerAdmin gets NO layer-2 filter, deliberately -- matrix section 6 gives them "all of them"
    /// within their Customer, including tickets containing payroll and personal tax data. A DELIBERATE,
    /// ACCEPTED DECISION; this test exists so that narrowing it fails loudly rather than looking like a
    /// security improvement.
    /// </summary>
    [Fact]
    public async Task A_CustomerAdmin_sees_every_non_draft_ticket_at_their_customer()
    {
        await using var db = NewDb();
        db.Tickets.AddRange(
            NewTicket(_customerA, _creatorAccount, _creatorEmployee, TicketStatus.Submitted),
            NewTicket(_customerA, _colleagueAccount, _colleagueEmployee, TicketStatus.Answered,
                Guid.NewGuid()),
            NewTicket(_customerA, _colleagueAccount, _subjectEmployee, TicketStatus.Closed,
                Guid.NewGuid()),
            NewTicket(_customerA, _colleagueAccount, _subjectEmployee, TicketStatus.Cancelled));
        await db.SaveChangesAsync();

        var visible = await db.Tickets
            .WhereTicketVisible(CustomerSide(Guid.NewGuid(), UserRole.CustomerAdmin, _customerA), null)
            .ToListAsync();

        // Four, including the Cancelled one: a cancelled ticket stays readable (section 1.9), which is
        // why there is no Cancelled-excluding global query filter.
        Assert.Equal(4, visible.Count);
    }

    /// <summary>
    /// Layer 2. An Employee sees only the tickets they are party to, so a colleague's ticket at their own
    /// Customer is invisible even though layer 1 passes it.
    /// </summary>
    [Fact]
    public async Task An_Employee_cannot_see_a_colleagues_ticket()
    {
        await using var db = NewDb();
        db.Tickets.Add(NewTicket(_customerA, _colleagueAccount, _colleagueEmployee,
            TicketStatus.Submitted));
        await db.SaveChangesAsync();

        var visible = await db.Tickets
            .WhereTicketVisible(
                CustomerSide(_creatorAccount, UserRole.Employee, _customerA), _creatorEmployee)
            .ToListAsync();

        Assert.Empty(visible);
    }

    /// <summary>
    /// Creator OR Subject, not Creator only. A Customer Admin opening a ticket on an Employee's behalf is
    /// the normal case, and the Employee must be able to read it.
    /// </summary>
    [Fact]
    public async Task An_Employee_sees_a_non_draft_ticket_where_they_are_only_the_subject()
    {
        await using var db = NewDb();
        db.Tickets.Add(NewTicket(_customerA, _colleagueAccount, _subjectEmployee,
            TicketStatus.Submitted));
        await db.SaveChangesAsync();

        var visible = await db.Tickets
            .WhereTicketVisible(
                CustomerSide(_subjectAccount, UserRole.Employee, _customerA), _subjectEmployee)
            .ToListAsync();

        Assert.Single(visible);
    }

    /// <summary>
    /// Section 9.3, LOCKED. Being the Subject does not make somebody else's Draft visible. Layers 2 and
    /// 3 are independent, and passing layer 2 is not a pass on layer 3.
    /// </summary>
    [Fact]
    public async Task An_Employee_cannot_see_a_draft_where_they_are_the_subject()
    {
        await using var db = NewDb();
        db.Tickets.Add(NewTicket(_customerA, _colleagueAccount, _subjectEmployee));
        await db.SaveChangesAsync();

        var visible = await db.Tickets
            .WhereTicketVisible(
                CustomerSide(_subjectAccount, UserRole.Employee, _customerA), _subjectEmployee)
            .ToListAsync();

        Assert.Empty(visible);
    }

    [Fact]
    public async Task An_Employee_sees_their_own_draft()
    {
        await using var db = NewDb();
        db.Tickets.Add(NewTicket(_customerA, _creatorAccount, _creatorEmployee));
        await db.SaveChangesAsync();

        var visible = await db.Tickets
            .WhereTicketVisible(
                CustomerSide(_creatorAccount, UserRole.Employee, _customerA), _creatorEmployee)
            .ToListAsync();

        Assert.Single(visible);
    }

    /// <summary>
    /// Section 3.1 rule 3 and section 13 item 6. An Employee-role account with no Employee record is a
    /// BROKEN state, not a permissive one. THE ASSUMPTION IMPLEMENTED HERE IS "empty result"; the plan
    /// leaves the choice between that and a 401 open, and requires only that it is never an unfiltered
    /// query. The assertion is written against the guarantee, not the choice: nothing is returned, not
    /// even a ticket the account created.
    /// </summary>
    [Fact]
    public async Task An_Employee_with_no_employee_record_sees_nothing()
    {
        await using var db = NewDb();
        db.Tickets.AddRange(
            NewTicket(_customerA, _creatorAccount, _creatorEmployee, TicketStatus.Submitted),
            NewTicket(_customerA, _colleagueAccount, _colleagueEmployee, TicketStatus.Submitted));
        await db.SaveChangesAsync();

        var visible = await db.Tickets
            .WhereTicketVisible(
                CustomerSide(_creatorAccount, UserRole.Employee, _customerA), callerEmployeeId: null)
            .ToListAsync();

        Assert.Empty(visible);
    }

    /// <summary>
    /// A caller whose Id is not a Guid must not fall through to an unfiltered query. This is the "D" vs
    /// "N" format trap's neighbour: the failure mode of a parse that silently produces nothing is a
    /// missing filter, and a missing filter here is a tenant breach.
    /// </summary>
    [Fact]
    public async Task An_unparseable_account_id_sees_nothing()
    {
        await using var db = NewDb();
        db.Tickets.Add(NewTicket(_customerA, _creatorAccount, _creatorEmployee,
            TicketStatus.Submitted));
        await db.SaveChangesAsync();

        var visible = await db.Tickets
            .WhereTicketVisible(new CurrentUser("not-a-guid", UserRole.AccountantAdmin), null)
            .ToListAsync();

        Assert.Empty(visible);
    }

    // --- Layer 4: internal notes ---

    /// <summary>
    /// Matrix section 6 requires the exclusion be enforced ON THE SERVER by filtering, not by the React
    /// app choosing not to display them.
    /// </summary>
    [Fact]
    public async Task Internal_notes_are_stripped_for_customer_side_callers_and_kept_for_accountants()
    {
        await using var db = NewDb();
        var ticket = NewTicket(_customerA, _creatorAccount, _creatorEmployee, TicketStatus.InReview,
            Guid.NewGuid());
        db.Tickets.Add(ticket);
        db.TicketMessages.AddRange(
            Message(ticket.Id, TicketMessageKind.CustomerMessage),
            Message(ticket.Id, TicketMessageKind.AccountantResponse),
            Message(ticket.Id, TicketMessageKind.SystemEvent),
            Message(ticket.Id, TicketMessageKind.InternalNote));
        await db.SaveChangesAsync();

        foreach (var user in new[]
                 {
                     CustomerSide(_creatorAccount, UserRole.CustomerAdmin, _customerA),
                     CustomerSide(_creatorAccount, UserRole.Employee, _customerA),
                 })
        {
            var visible = await db.TicketMessages.WhereMessageVisible(user).ToListAsync();

            Assert.Equal(3, visible.Count);
            Assert.DoesNotContain(visible, message => message.Kind == TicketMessageKind.InternalNote);
        }

        foreach (var role in new[] { UserRole.AccountantAdmin, UserRole.AccountantUser })
        {
            // Both Accountant roles, never one of them: internal notes are the Office's private channel,
            // not the Admin's.
            var visible = await db.TicketMessages
                .WhereMessageVisible(Accountant(Guid.NewGuid(), role))
                .ToListAsync();

            Assert.Equal(4, visible.Count);
        }
    }

    /// <summary>
    /// The allow-list, not a block-list. A fifth kind added later must be invisible to the Customer side
    /// until somebody deliberately adds it -- a block-list would expose it the day it appears.
    /// </summary>
    [Fact]
    public void CustomerVisible_is_an_allow_list_of_exactly_three_kinds()
    {
        Assert.Equal(3, TicketMessageKind.CustomerVisible.Count);
        Assert.Contains(TicketMessageKind.CustomerMessage, TicketMessageKind.CustomerVisible);
        Assert.Contains(TicketMessageKind.AccountantResponse, TicketMessageKind.CustomerVisible);
        Assert.Contains(TicketMessageKind.SystemEvent, TicketMessageKind.CustomerVisible);
        Assert.DoesNotContain(TicketMessageKind.InternalNote, TicketMessageKind.CustomerVisible);
    }

    /// <summary>
    /// The in-memory overload of layer 4 must agree with the queryable one. Two filters for one rule is
    /// how the rule ends up applied in one place and not the other.
    /// </summary>
    [Fact]
    public void The_enumerable_overload_of_layer_four_matches_the_queryable_one()
    {
        var ticketId = Guid.NewGuid();
        var messages = new List<TicketMessage>
        {
            Message(ticketId, TicketMessageKind.CustomerMessage),
            Message(ticketId, TicketMessageKind.InternalNote),
        };

        var forCustomer = messages
            .WhereMessageVisible(CustomerSide(_creatorAccount, UserRole.Employee, _customerA))
            .ToList();
        Assert.Equal(TicketMessageKind.CustomerMessage, Assert.Single(forCustomer).Kind);

        Assert.Equal(2, messages.WhereMessageVisible(Accountant(Guid.NewGuid())).Count());
    }

    private static TicketMessage Message(Guid ticketId, string kind) => new()
    {
        Id = Guid.NewGuid(),
        TicketId = ticketId,
        AuthorUserAccountId = kind == TicketMessageKind.SystemEvent ? null : Guid.NewGuid(),
        Kind = kind,
        Body = kind,
        CreatedAt = Now,
    };
}
