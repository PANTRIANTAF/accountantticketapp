using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.8. <c>Submitted → InReview</c> WITH self-assignment, as ONE atomic operation.
///
/// 01-DomainModel.md §3 is explicit: "Moving a Ticket from Submitted to InReview MUST set an Assignee in
/// the same operation. A request that would leave it null is rejected. The two are one atomic action, not
/// a status change followed by an optional assignment." <c>ck_tickets_assignee</c> is the backstop and
/// <c>TicketTransitions.Apply</c> refuses the transition without an assignee.
///
/// TAKING A STRANDED TICKET (§9.8, the pickup queue's condition 2) comes through here only while it is
/// still <c>Submitted</c>. A stranded ticket in <c>InReview</c>, <c>AwaitingInformation</c> or
/// <c>Answered</c> has no legal transition to itself, so it is taken through the assign endpoint with the
/// taker as the target -- which produces exactly the same <c>TicketReassigned</c> entry naming both
/// accounts. Reported, because the plan describes taking a stranded ticket without saying which of the
/// two endpoints does it.
/// </summary>
public class PickupTicketHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly IIdentityApi _identity;
    private readonly INotificationApi _notifications;

    public PickupTicketHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit,
        IEmployeeApi employees,
        IIdentityApi identity,
        INotificationApi notifications)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
        _employees = employees;
        _identity = identity;
        _notifications = notifications;
    }

    public async Task<TicketStateDto> Handle(
        PickupTicketRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "PickupTicket", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);

        // The caller's own account, for the SystemEvent's wording. Unreachable in practice -- they are
        // authenticated -- but the alternative is a message reading "Assigned to" with nothing after it.
        var self = await _identity.FindAsync(callerAccountId, ct)
                   ?? throw new AppException("Your user account could not be resolved.", 403);

        var previousAssignee = ticket.AssigneeUserAccountId;

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var now = DateTimeOffset.UtcNow;
        var fromStatus = ticket.Status;

        // Status AND assignee in one call. A pickup of a ticket in any other status is 422 from the closed
        // table, including a second pickup of one already in InReview -- there is no (InReview, InReview)
        // row and there must not be one.
        var statusEvent = TicketTransitions.Apply(ticket, TicketStatus.InReview, callerAccountId, now);
        _db.TicketMessages.Add(statusEvent);
        ticket.Messages.Add(statusEvent);

        // Rule 6: the assignment gets its own SystemEvent. The status message says the ticket moved; it
        // does not say into whose hands, and that is the fact the conversation needs to record.
        var assignmentEvent = AssignTicketHandler.AssignmentSystemEvent(
            ticket, previousAssignee, self, now);
        _db.TicketMessages.Add(assignmentEvent);
        ticket.Messages.Add(assignmentEvent);

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.TicketStatusChanged,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            Before: new { Status = fromStatus, AssigneeUserAccountId = previousAssignee },
            After: new { ticket.Status, ticket.AssigneeUserAccountId }), ct);

        // Rule 4, the one that is easy to get wrong: the code follows the PRIOR STATE, not the endpoint.
        // Taking a ticket whose Assignee was a different, suspended user is a REASSIGNMENT and is recorded
        // as one, naming both accounts -- "it is not recorded as a plain pickup" (§9.8 rule 3).
        await AssignTicketHandler.LogAssignmentAsync(
            _audit, ticket, previousAssignee, callerAccountId, ct);

        // Rule 7: the Customer side learns their ticket is being worked on. In-app only -- TicketPickedUp
        // is not one of the emailed kinds, because it needs no action from them.
        await TicketAccess.NotifyCustomerSideAsync(
            _notifications,
            _employees,
            ticket,
            NotificationEvents.TicketPickedUp,
            $"{ticket.Reference} is being reviewed",
            $"{self.DisplayName} has started work on {ticket.Title}.",
            ct);

        await _transaction.CommitAsync(ct);

        return TicketMapper.ToState(ticket);
    }
}
