using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Tickets.Core;

namespace AccountantApp.Api.Slices.Tickets.Application;

/// <summary>
/// The ticket state machine. Section 5, transcribing 01-DomainModel.md section 5's transition table
/// EXACTLY.
///
/// ONE FILE, ONE TABLE. Nine handler paths change a ticket's status; each of them calls
/// <see cref="Apply"/>. A handler that sets ticket.Status itself has bypassed the table, the Touch and
/// the SystemEvent at once, and nothing about it looks wrong at the call site.
/// </summary>
public static class TicketTransitions
{
    /// <summary>
    /// THE TABLE IS COMPLETE AND CLOSED. Any pair not listed here is illegal.
    ///
    /// THERE IS NO ROW WHOSE From IS Closed OR Cancelled, and adding one violates section 9.1 and
    /// section 5. Both terminal statuses are equally terminal: a Closed ticket is never reopened, and a
    /// continuation is a NEW ticket carrying PrecededByTicketId. There is no Reopened status.
    ///
    /// (Answered, InReview) IS in the table and (Closed, InReview) IS NOT. These two look alike and are
    /// opposite. The first is an Accountant deciding, before closing, that the answer was not finished
    /// -- the ticket never left the Office's hands. The second is the reopen that section 9.1 forbids.
    /// Do not "make them consistent".
    /// </summary>
    private static readonly (string From, string To)[] Allowed =
    [
        (TicketStatus.Draft,               TicketStatus.Submitted),
        (TicketStatus.Draft,               TicketStatus.Cancelled),
        (TicketStatus.Submitted,           TicketStatus.InReview),            // MUST set an Assignee in the same operation
        (TicketStatus.Submitted,           TicketStatus.Cancelled),
        (TicketStatus.InReview,            TicketStatus.AwaitingInformation), // Assignee retained
        (TicketStatus.InReview,            TicketStatus.Answered),
        (TicketStatus.InReview,            TicketStatus.Cancelled),
        (TicketStatus.AwaitingInformation, TicketStatus.Submitted),           // Assignee RETAINED -- not back in the pool
        (TicketStatus.AwaitingInformation, TicketStatus.Cancelled),
        (TicketStatus.Answered,            TicketStatus.Closed),
        (TicketStatus.Answered,            TicketStatus.InReview),            // reopening BEFORE close -- not a section 9.1 reopen
    ];

    /// <summary>Exposed so a test can enumerate the closed table rather than restate it.</summary>
    public static IReadOnlyList<(string From, string To)> AllowedTransitions => Allowed;

    /// <summary>
    /// Statuses that require an Assignee, mirroring ck_tickets_assignee's first branch. Submitted is
    /// deliberately absent: it MAY have one, because AwaitingInformation -> Submitted retains it.
    /// </summary>
    private static readonly IReadOnlySet<string> RequiresAssignee = new HashSet<string>(StringComparer.Ordinal)
        { TicketStatus.InReview, TicketStatus.AwaitingInformation, TicketStatus.Answered, TicketStatus.Closed };

    /// <summary>
    /// Statuses that must have no Assignee, mirroring ck_tickets_assignee's second branch. Cancelling
    /// therefore CLEARS the Assignee -- see <see cref="Apply"/>.
    /// </summary>
    private static readonly IReadOnlySet<string> ForbidsAssignee = new HashSet<string>(StringComparer.Ordinal)
        { TicketStatus.Draft, TicketStatus.Cancelled };

    /// <summary>
    /// The human-readable status names used in the SystemEvent body. An explicit map, not a
    /// PascalCase-splitting regex: section 5 rule 7 requires the body be STABLE, and a regex changes
    /// every existing message's wording the day somebody renames a status or "improves" the splitting.
    /// These strings are part of what a Customer reads in the conversation.
    /// </summary>
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.Ordinal)
    {
        [TicketStatus.Draft]               = "Draft",
        [TicketStatus.Submitted]           = "Submitted",
        [TicketStatus.InReview]            = "In Review",
        [TicketStatus.AwaitingInformation] = "Awaiting Information",
        [TicketStatus.Answered]            = "Answered",
        [TicketStatus.Closed]              = "Closed",
        [TicketStatus.Cancelled]           = "Cancelled",
    };

    public static bool IsAllowed(string fromStatus, string toStatus)
    {
        foreach (var (from, to) in Allowed)
            if (string.Equals(from, fromStatus, StringComparison.Ordinal)
                && string.Equals(to, toStatus, StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>
    /// The statuses reachable from <paramref name="fromStatus"/>. Empty for Closed and Cancelled, and
    /// that emptiness is the invariant -- not an oversight to be filled in later.
    /// </summary>
    public static IReadOnlyList<string> AllowedTargetsFrom(string fromStatus) =>
        Allowed.Where(pair => string.Equals(pair.From, fromStatus, StringComparison.Ordinal))
               .Select(pair => pair.To)
               .ToList();

    /// <summary>The wording used in the SystemEvent body, for a handler that needs to echo it.</summary>
    public static string DisplayName(string status) =>
        DisplayNames.TryGetValue(status, out var name) ? name : status;

    /// <summary>
    /// Applies a status transition and returns the SystemEvent TicketMessage that must be written with
    /// it.
    ///
    /// Section 4.0 E says every transition does four things together, in one transaction: validate,
    /// write the status and Touch, write a SystemEvent TicketMessage, and write the Audit entry plus
    /// any Notification. This method owns the first three. THE FOURTH STAYS WITH THE HANDLER, because
    /// only the handler knows which audit code and which notification kind the operation carries
    /// (section 4.0 F and G map them per operation), and because AuditApi and NotificationApi are
    /// injected services that a static helper would have to be handed for no gain.
    ///
    /// The returned message is NOT added to the context here -- the caller does
    /// `ticket.Messages.Add(systemEvent)` or `_db.TicketMessages.Add(systemEvent)`. Returning it rather
    /// than swallowing it is deliberate: an ignored return value is visible in review, whereas a
    /// SystemEvent silently not written is invisible until somebody audits a six-month-old
    /// conversation. Four things spread across nine handlers is thirty-six chances to forget one, and
    /// the one that gets forgotten is the SystemEvent, because nothing breaks without it.
    /// </summary>
    /// <param name="newAssignee">
    /// The Assignee to set. Pass null to RETAIN the current one -- which is what
    /// AwaitingInformation -> Submitted requires (section 4.2 rule 1: the ticket does not go back into
    /// the pickup pool). Required, and rejected if null, for Submitted -> InReview.
    /// </param>
    /// <param name="now">From the application clock, so one operation stamps one instant.</param>
    /// <exception cref="AppException">422 if the pair is not in the table, or the Assignee rules fail.</exception>
    public static TicketMessage Apply(
        Ticket ticket, string toStatus, Guid? newAssignee, DateTimeOffset now)
    {
        var fromStatus = ticket.Status;

        // 1. Validate against the closed table. 422, not 500 and not a silent no-op: the request named
        //    a transition that does not exist, which is a bad request, and a no-op would return 200
        //    while changing nothing -- the client then shows the new status it asked for.
        if (!IsAllowed(fromStatus, toStatus))
            throw new AppException(
                $"A ticket in status '{DisplayName(fromStatus)}' cannot move to "
                + $"'{DisplayName(toStatus)}'.", 422);

        // The Assignee, resolved before anything is written so a rejection leaves the entity untouched.
        var assignee = newAssignee ?? ticket.AssigneeUserAccountId;

        // Cancelling clears the Assignee: ck_tickets_assignee requires NULL in Cancelled. Done here
        // rather than in the cancel handler so the constraint cannot be violated from any of the four
        // cancel paths.
        if (ForbidsAssignee.Contains(toStatus))
            assignee = null;

        if (RequiresAssignee.Contains(toStatus) && assignee is null)
            // Submitted -> InReview is the case that matters: picking up or assigning a ticket IS the
            // act of taking responsibility for it, so a null here means the operation was half
            // specified. Section 5 rule 4.
            throw new AppException(
                $"Moving a ticket to '{DisplayName(toStatus)}' requires an assignee.", 422);

        // 2. Write the status, the Assignee and Touch.
        ticket.Status = toStatus;
        ticket.AssigneeUserAccountId = assignee;

        // ck_tickets_closed: closed_at is set if and only if the status is Closed. Cancelled does NOT
        // get one -- it is terminal but it is not closed, and the two mean different things in every
        // report.
        ticket.ClosedAt = toStatus == TicketStatus.Closed ? now : null;

        // Never inline ticket.Version += 1 here. One implementation of the token, in TicketConcurrency.
        TicketConcurrency.Touch(ticket, now);

        // 3. The SystemEvent. Null author: it is written by the application, not by a person, which is
        //    what ck_ticket_messages_author's first branch encodes. Attributing it to the actor would
        //    make it look like something they typed.
        return new TicketMessage
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            AuthorUserAccountId = null,
            Kind = TicketMessageKind.SystemEvent,
            Body = $"Status changed to {DisplayName(toStatus)}",
            CreatedAt = now,
        };
    }
}
