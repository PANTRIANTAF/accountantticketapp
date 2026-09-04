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
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.9. <c>Answered → Closed</c>, Accountants only: "Only an Accountant may close a Ticket. The
/// Customer side never closes."
///
/// THE CLOSING RULE IS CHECKED AGAIN HERE (rule 2), even though <c>Answered</c> already required it. §3
/// states it separately and more strictly: "a Ticket cannot move to Closed while any required, visible
/// FieldValue in the current revision is unverified or rejected." Between the answer and the close the
/// ticket can have gone <c>Answered → InReview → Answered</c>, and a field can have been rejected in that
/// window -- so the check at the earlier gate proves nothing about this moment.
///
/// CLOSED IS TERMINAL AND THERE IS NO WAY BACK. §9.1 is LOCKED: no reopen endpoint, no Reopened status, no
/// ReopenedAt, and no (Closed, anything) row in the transition table. A continuation is a NEW ticket
/// carrying <c>PrecededByTicketId</c>. <c>closed_at</c> is set by <c>TicketTransitions.Apply</c>, exactly
/// when the status becomes Closed, which is what <c>ck_tickets_closed</c> demands.
/// </summary>
public class CloseTicketHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly ITicketTypesApi _ticketTypes;
    private readonly INotificationApi _notifications;

    public CloseTicketHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit,
        IEmployeeApi employees,
        ITicketTypesApi ticketTypes,
        INotificationApi notifications)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
        _employees = employees;
        _ticketTypes = ticketTypes;
        _notifications = notifications;
    }

    public async Task<TicketStateDto> Handle(
        CloseTicketRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "CloseTicket", ct: ct);

        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);

        var rulesVersion = await _ticketTypes.GetVersionByIdAsync(
                               ticket.TicketTypeVersionId, TicketAccess.DescriptorAudienceForRules, ct)
                           ?? throw new AppException(
                               "This ticket's type version could not be resolved.", 422);

        var values = await TicketAccess.CurrentValuesAsync(_db, ticket, ct);

        var outstanding = TicketMapper.UnverifiedRequiredVisibleFields(rulesVersion, values);
        if (outstanding.Count > 0)
            throw new AppException(
                "These answers must be accepted before the ticket can be closed: "
                + $"{string.Join(", ", outstanding)}.", 422);

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var now = DateTimeOffset.UtcNow;
        var fromStatus = ticket.Status;

        // Closed REQUIRES an assignee (ck_tickets_assignee), so null here retains the one it already has
        // -- a ticket cannot reach Answered without one.
        var systemEvent = TicketTransitions.Apply(ticket, TicketStatus.Closed, null, now);
        _db.TicketMessages.Add(systemEvent);
        ticket.Messages.Add(systemEvent);

        await _db.SaveChangesAsync(ct);

        // TWO audit entries, and rule 8 asks for both: the generic status change keeps the transition
        // history uniform, and TicketClosed is the code a retention or reporting query looks for. One
        // without the other means either the timeline has a gap or "when did we close this" needs a scan
        // of every status change.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.TicketStatusChanged,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            Before: new { Status = fromStatus },
            After: new { ticket.Status, ticket.ClosedAt }), ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.TicketClosed,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            After: new { ticket.ClosedAt, ticket.AssigneeUserAccountId }), ct);

        // TicketClosed is EMAILED. It is the end of the matter for the Customer side, and their documents
        // stay downloadable afterwards (§4.11 rule 2) -- the ticket is read-only, not gone.
        await TicketAccess.NotifyCustomerSideAsync(
            _notifications,
            _employees,
            ticket,
            NotificationEvents.TicketClosed,
            $"{ticket.Reference} has been closed",
            $"{ticket.Title} is complete. You can still read it and download its documents.",
            ct);

        await _transaction.CommitAsync(ct);

        return TicketMapper.ToState(ticket);
    }
}
