using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Tickets.Application;
using AccountantApp.Api.Slices.Tickets.Core;
using static AccountantApp.Tests.Tickets.TicketsTestHarness;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// The state machine, plan section 5. The table is CLOSED, so the tests come in three parts: every
/// permitted pair is permitted, a representative sample of the forbidden ones is refused, and nothing
/// leaves Closed or Cancelled.
///
/// The forbidden sample is generated rather than listed: every (from, to) pair of the seven statuses
/// that is not in the table must be a 422. Listing a handful by hand is what lets a stray extra row
/// into the table unnoticed -- the sample would have to happen to name that exact pair.
/// </summary>
public sealed class TicketTransitionsTests
{
    private static readonly string[] AllStatuses =
    [
        TicketStatus.Draft, TicketStatus.Submitted, TicketStatus.InReview,
        TicketStatus.AwaitingInformation, TicketStatus.Answered, TicketStatus.Closed,
        TicketStatus.Cancelled,
    ];

    [Fact]
    public void The_table_has_exactly_the_eleven_transitions_the_domain_model_lists()
    {
        Assert.Equal(11, TicketTransitions.AllowedTransitions.Count);

        var expected = new[]
        {
            (TicketStatus.Draft, TicketStatus.Submitted),
            (TicketStatus.Draft, TicketStatus.Cancelled),
            (TicketStatus.Submitted, TicketStatus.InReview),
            (TicketStatus.Submitted, TicketStatus.Cancelled),
            (TicketStatus.InReview, TicketStatus.AwaitingInformation),
            (TicketStatus.InReview, TicketStatus.Answered),
            (TicketStatus.InReview, TicketStatus.Cancelled),
            (TicketStatus.AwaitingInformation, TicketStatus.Submitted),
            (TicketStatus.AwaitingInformation, TicketStatus.Cancelled),
            (TicketStatus.Answered, TicketStatus.Closed),
            (TicketStatus.Answered, TicketStatus.InReview),
        };

        foreach (var (from, to) in expected)
            Assert.True(TicketTransitions.IsAllowed(from, to), $"{from} -> {to} should be allowed");
    }

    [Fact]
    public void Every_permitted_pair_applies_and_writes_a_system_event()
    {
        foreach (var (from, to) in TicketTransitions.AllowedTransitions)
        {
            // An Assignee is present wherever the source status may carry one, so the pair under test is
            // the only thing that can fail.
            var existingAssignee = from is TicketStatus.Draft ? (Guid?)null : Guid.NewGuid();
            var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), from, existingAssignee);

            // Submitted -> InReview is the one pair that REQUIRES a new Assignee to be supplied.
            var newAssignee = (from, to) == (TicketStatus.Submitted, TicketStatus.InReview)
                ? Guid.NewGuid()
                : (Guid?)null;

            var systemEvent = TicketTransitions.Apply(ticket, to, newAssignee, Now);

            Assert.Equal(to, ticket.Status);
            Assert.Equal(TicketMessageKind.SystemEvent, systemEvent.Kind);

            // Section 5 rule 7 and the domain model: written by the application, not by a person. An
            // author here would make it look like something the actor typed.
            Assert.Null(systemEvent.AuthorUserAccountId);
            Assert.Equal(ticket.Id, systemEvent.TicketId);
            Assert.False(string.IsNullOrWhiteSpace(systemEvent.Body));
        }
    }

    [Fact]
    public void Every_pair_outside_the_table_is_422()
    {
        foreach (var from in AllStatuses)
        foreach (var to in AllStatuses)
        {
            if (TicketTransitions.IsAllowed(from, to))
                continue;

            var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), from,
                from is TicketStatus.Draft or TicketStatus.Cancelled ? null : Guid.NewGuid());

            var exception = Assert.Throws<AppException>(
                () => TicketTransitions.Apply(ticket, to, Guid.NewGuid(), Now));

            // 422, not 500 and not a silent no-op. A no-op would return 200 while changing nothing, and
            // the client would show the status it asked for.
            Assert.Equal(422, exception.StatusCode);

            // And it left the entity alone -- a rejected transition must not have half applied.
            Assert.Equal(from, ticket.Status);
            Assert.Equal(1, ticket.Version);
        }
    }

    /// <summary>
    /// The invariant, stated directly rather than inferred from the sweep above: both terminal statuses
    /// are equally terminal. A Closed ticket is never reopened (section 9.1, LOCKED); a continuation is
    /// a NEW ticket carrying PrecededByTicketId.
    /// </summary>
    [Fact]
    public void Nothing_leaves_Closed_or_Cancelled()
    {
        Assert.Empty(TicketTransitions.AllowedTargetsFrom(TicketStatus.Closed));
        Assert.Empty(TicketTransitions.AllowedTargetsFrom(TicketStatus.Cancelled));

        Assert.DoesNotContain(TicketTransitions.AllowedTransitions,
            pair => pair.From is TicketStatus.Closed or TicketStatus.Cancelled);
    }

    /// <summary>
    /// These two look alike and are opposite. (Answered, InReview) is an Accountant deciding, before
    /// closing, that the answer was not finished; (Closed, InReview) is the reopen section 9.1 forbids.
    /// </summary>
    [Fact]
    public void Answered_to_InReview_is_allowed_and_Closed_to_InReview_is_not()
    {
        Assert.True(TicketTransitions.IsAllowed(TicketStatus.Answered, TicketStatus.InReview));
        Assert.False(TicketTransitions.IsAllowed(TicketStatus.Closed, TicketStatus.InReview));
    }

    [Fact]
    public void Submitted_to_InReview_without_an_assignee_is_422()
    {
        var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), TicketStatus.Submitted);

        var exception = Assert.Throws<AppException>(
            () => TicketTransitions.Apply(ticket, TicketStatus.InReview, null, Now));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(TicketStatus.Submitted, ticket.Status);
        Assert.Null(ticket.AssigneeUserAccountId);
    }

    /// <summary>
    /// THE SECTION 5 TRAP. A correction round goes AwaitingInformation -> Submitted and the ticket must
    /// stay with its Assignee -- it does not go back into the pickup pool. This is also what
    /// ck_tickets_assignee's third branch exists to permit.
    /// </summary>
    [Fact]
    public void AwaitingInformation_to_Submitted_retains_the_assignee()
    {
        var assignee = Guid.NewGuid();
        var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TicketStatus.AwaitingInformation, assignee);

        TicketTransitions.Apply(ticket, TicketStatus.Submitted, null, Now);

        Assert.Equal(TicketStatus.Submitted, ticket.Status);
        Assert.Equal(assignee, ticket.AssigneeUserAccountId);
    }

    [Fact]
    public void InReview_to_AwaitingInformation_retains_the_assignee()
    {
        var assignee = Guid.NewGuid();
        var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TicketStatus.InReview, assignee);

        TicketTransitions.Apply(ticket, TicketStatus.AwaitingInformation, null, Now);

        Assert.Equal(assignee, ticket.AssigneeUserAccountId);
    }

    /// <summary>
    /// ck_tickets_assignee requires NULL in Cancelled, so cancelling clears it. Done in Apply rather
    /// than in the cancel handler, because there are four cancel paths and each could forget.
    /// </summary>
    [Fact]
    public void Cancelling_clears_the_assignee_and_sets_no_closed_at()
    {
        var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TicketStatus.InReview, Guid.NewGuid());

        TicketTransitions.Apply(ticket, TicketStatus.Cancelled, null, Now);

        Assert.Null(ticket.AssigneeUserAccountId);

        // Cancelled is terminal but it is NOT closed, and ck_tickets_closed forbids a closed_at on
        // anything but Closed. The two mean different things in every report.
        Assert.Null(ticket.ClosedAt);
    }

    [Fact]
    public void Closing_sets_closed_at_and_keeps_the_assignee()
    {
        var assignee = Guid.NewGuid();
        var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TicketStatus.Answered, assignee);

        TicketTransitions.Apply(ticket, TicketStatus.Closed, null, Now);

        Assert.Equal(Now, ticket.ClosedAt);
        Assert.Equal(assignee, ticket.AssigneeUserAccountId);
    }

    /// <summary>
    /// Every transition Touches the row. Without this a second writer's stale version still matches and
    /// two concurrent transitions both succeed, one silently overwriting the other.
    /// </summary>
    [Fact]
    public void Every_applied_transition_touches_the_version_and_activity()
    {
        var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), TicketStatus.Draft);
        var later = Now.AddHours(3);

        TicketTransitions.Apply(ticket, TicketStatus.Submitted, null, later);

        Assert.Equal(2, ticket.Version);
        Assert.Equal(later, ticket.LastActivityAt);
    }

    /// <summary>
    /// The body is generated, human-readable and STABLE -- it is what a Customer reads in the
    /// conversation. Asserted literally, because a "nicer" wording later silently makes every historical
    /// message inconsistent with every new one.
    /// </summary>
    [Fact]
    public void The_system_event_body_spells_the_status_out_for_a_human()
    {
        var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TicketStatus.InReview, Guid.NewGuid());

        var systemEvent = TicketTransitions.Apply(
            ticket, TicketStatus.AwaitingInformation, null, Now);

        Assert.Equal("Status changed to Awaiting Information", systemEvent.Body);
        Assert.Equal("In Review", TicketTransitions.DisplayName(TicketStatus.InReview));
    }
}
