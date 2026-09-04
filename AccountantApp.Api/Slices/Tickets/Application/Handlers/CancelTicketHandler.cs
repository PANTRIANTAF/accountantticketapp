using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.12. All four roles, with the narrowest reach in the matrix.
///
/// CANCELLATION IS THE ONLY REMOVAL IN THIS SYSTEM, and it removes nothing: it is a STATUS. The ticket,
/// its revisions, its messages and its documents all remain readable afterwards (§1.9). There is no delete
/// endpoint for a ticket anywhere in this slice, and <c>Cancelled</c> is absolutely terminal -- no
/// transition out, no un-cancel.
///
/// THE EMPLOYEE RESTRICTION IS TWO CONDITIONS (rule 1): their own ticket AND a status in
/// {Draft, Submitted}. An Employee cannot cancel a ticket an Accountant has already started work on --
/// the Office's time has been spent on it by then, and the record of that work is not theirs to end.
///
/// THE ASSIGNEE IS CLEARED, by <c>TicketTransitions.Apply</c>, because <c>ck_tickets_assignee</c> requires
/// null in Cancelled. That looks like it contradicts §9.8's "assignments are never silently
/// redistributed" and does not: nothing is redistributed, the ticket is over.
/// </summary>
public class CancelTicketHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly INotificationApi _notifications;

    public CancelTicketHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit,
        IEmployeeApi employees,
        INotificationApi notifications)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
        _employees = employees;
        _notifications = notifications;
    }

    public async Task<TicketStateDto> Handle(
        CancelTicketRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "CancelTicket", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);
        var isAccountant = TicketVisibility.IsAccountant(user);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);

        if (user.Role == UserRole.Employee)
        {
            // Condition one: their own ticket. Visibility layer 2 lets an Employee SEE a ticket where they
            // are the Creator or the Subject, and being the Subject of somebody else's ticket does not make
            // it theirs to cancel -- matrix §7 says "own drafts and own Submitted tickets".
            if (ticket.CreatorUserAccountId != callerAccountId)
                throw new AppException(
                    "Only the person who opened this ticket can cancel it.", 403);

            // Condition two: not yet in the Office's hands. 422 rather than 403 -- the caller is entitled
            // to cancel their own tickets, this one has simply moved past the point where they may.
            if (ticket.Status is not (TicketStatus.Draft or TicketStatus.Submitted))
                throw new AppException(
                    "This ticket is already being worked on. Ask the Office to cancel it.", 422);
        }

        // Everything else the table decides: Answered -> Cancelled is not in it, and a second cancel of an
        // already Cancelled ticket is a 422 rather than a silent success.
        var previousAssignee = ticket.AssigneeUserAccountId;

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var now = DateTimeOffset.UtcNow;
        var fromStatus = ticket.Status;

        var systemEvent = TicketTransitions.Apply(ticket, TicketStatus.Cancelled, null, now);
        _db.TicketMessages.Add(systemEvent);
        ticket.Messages.Add(systemEvent);

        var reason = req.Reason?.Trim();
        if (!string.IsNullOrWhiteSpace(reason))
        {
            // The reason goes into the conversation as an ordinary message from whoever cancelled, on the
            // public channel: both sides need to know why a ticket ended, and the SystemEvent body is
            // fixed wording that cannot carry it.
            var note = new TicketMessage
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                AuthorUserAccountId = callerAccountId,
                Kind = isAccountant
                    ? TicketMessageKind.AccountantResponse
                    : TicketMessageKind.CustomerMessage,
                Body = reason,
                CreatedAt = now,
            };

            _db.TicketMessages.Add(note);
            ticket.Messages.Add(note);
        }

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.TicketStatusChanged,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            Before: new { Status = fromStatus, AssigneeUserAccountId = previousAssignee },
            After: new { ticket.Status, ticket.AssigneeUserAccountId }), ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.TicketCancelled,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            Before: new { Status = fromStatus, AssigneeUserAccountId = previousAssignee },

            // The reason is in the entry because "who ended this and why" is the question asked of a
            // cancelled ticket, and unlike an internal note this text was posted on the public channel.
            After: new { ticket.Status, Reason = reason }), ct);

        await NotifyOtherSideAsync(ticket, isAccountant, previousAssignee, reason, ct);

        await _transaction.CommitAsync(ct);

        return TicketMapper.ToState(ticket);
    }

    /// <summary>
    /// Rule 5: the OTHER side is told, whichever side that is.
    ///
    /// An Accountant cancelling tells the Customer side. A Customer-side actor cancelling tells the
    /// Assignee -- and nobody at all when there is none, because an unassigned ticket was in the shared
    /// pickup queue and simply leaves it. Telling the whole Office that a draft nobody had opened was
    /// cancelled is the kind of notification that trains people to ignore the list.
    /// </summary>
    private async Task NotifyOtherSideAsync(
        Ticket ticket,
        bool isAccountant,
        Guid? previousAssignee,
        string? reason,
        CancellationToken ct)
    {
        var body = string.IsNullOrWhiteSpace(reason)
            ? $"{ticket.Title} has been cancelled."
            : $"{ticket.Title} has been cancelled. {reason}";

        if (isAccountant)
        {
            await TicketAccess.NotifyCustomerSideAsync(
                _notifications,
                _employees,
                ticket,
                NotificationEvents.TicketCancelled,
                $"{ticket.Reference} was cancelled",
                body,
                ct);

            return;
        }

        if (previousAssignee is not { } assignee)
            return;

        await _notifications.NotifyAsync(new NotificationRequest(
            assignee.ToString(),
            NotificationEvents.TicketCancelled,
            $"{ticket.Reference} was cancelled",
            body,
            ticket.Id), ct);
    }
}
