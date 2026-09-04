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
/// Plan §4.8. Sets or changes the Assignee WITHOUT necessarily moving the status.
///
/// ANY ACCOUNTANT MAY REASSIGN ANY TICKET, including to themselves and including away from an
/// AccountantAdmin. §9.9 is LOCKED on this: "there is no seniority in assignment", and restricting
/// reassignment to Admins would create a fifth Admin-only power against a locked list of exactly four.
/// Attribution is preserved by the audit log, not by withholding the operation.
///
/// THE AUDIT CODE DEPENDS ON THE PRIOR STATE, NOT ON THE ENDPOINT (rule 4). No prior Assignee is
/// <c>TicketAssigned</c>; a different prior Assignee is <c>TicketReassigned</c>, naming BOTH accounts.
/// Hardcoding one code loses the only record that work was taken from somebody.
///
/// It also does NOT restrict anything to the Assignee (rule 8). There is no "only the Assignee may..."
/// check anywhere in this slice.
/// </summary>
public class AssignTicketHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly IIdentityApi _identity;
    private readonly INotificationApi _notifications;

    public AssignTicketHandler(
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
        AssignTicketRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "AssignTicket", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);

        // §9.9 says any NON-TERMINAL status. Assigning a Closed or Cancelled ticket would also violate
        // ck_tickets_assignee, which requires null in Cancelled.
        TicketAccess.RequireNotTerminal(ticket);

        // A Draft has no Assignee and no Accountant can even see one (visibility layer 3), so this is
        // unreachable in practice -- but ck_tickets_assignee forbids it outright, and a 422 explains it
        // where a constraint violation would be a 500.
        if (ticket.Status == TicketStatus.Draft)
            throw new AppException("A draft has no assignee.", 422);

        var target = await RequireActiveAccountantAsync(req.AssigneeUserAccountId, ct);

        var previousAssignee = ticket.AssigneeUserAccountId;

        if (previousAssignee == target.Id)
            // Already theirs. Not an error, and not an audit entry either.
            return TicketMapper.ToState(ticket);

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var now = DateTimeOffset.UtcNow;

        // The status is untouched, so this does NOT go through TicketTransitions.Apply -- there is no
        // transition to validate. The token still moves, because the tickets row is written.
        ticket.AssigneeUserAccountId = target.Id;
        TicketConcurrency.Touch(ticket, now);

        var systemEvent = AssignmentSystemEvent(ticket, previousAssignee, target, now);
        _db.TicketMessages.Add(systemEvent);
        ticket.Messages.Add(systemEvent);

        await _db.SaveChangesAsync(ct);

        await LogAssignmentAsync(_audit, ticket, previousAssignee, target.Id, ct);

        await NotifyAssigneeAsync(_notifications, ticket, target, callerAccountId, ct);

        await _transaction.CommitAsync(ct);

        return TicketMapper.ToState(ticket);
    }

    /// <summary>
    /// Rule 3: the target must resolve LIVE to an Active Accountant of either role.
    ///
    /// Read live, deliberately: a suspended Accountant's EXISTING assignments are retained (§9.8 rule 4),
    /// but they must not become a valid target for a new one -- otherwise the pickup queue surfaces the
    /// ticket as stranded and somebody assigns it straight back into the same hole.
    ///
    /// A Customer-side target is 422, not 403 (rule 3). The caller is entitled to assign; the account
    /// they named is simply not an eligible assignee, which is a fact about the request body.
    /// </summary>
    private async Task<AccountSummary> RequireActiveAccountantAsync(Guid accountId, CancellationToken ct)
    {
        var account = await _identity.FindAsync(accountId, ct)
                      ?? throw new AppException("That user account was not found.", 422);

        if (account.Role is not (UserRole.AccountantAdmin or UserRole.AccountantUser))
            throw new AppException("A ticket can only be assigned to a member of the Office.", 422);

        if (!account.IsActive)
            throw new AppException(
                "That account is not active and cannot be assigned new work.", 422);

        return account;
    }

    /// <summary>
    /// Rule 6: a SystemEvent for every assignment and reassignment. Shared with the pickup path so the
    /// two cannot word the same fact differently.
    ///
    /// A reassignment names both sides, because "Reassigned to Y" read six months later does not answer
    /// the question anybody is asking. Null author -- the application wrote it, not a person.
    /// </summary>
    internal static TicketMessage AssignmentSystemEvent(
        Ticket ticket, Guid? previousAssignee, AccountSummary target, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            AuthorUserAccountId = null,
            Kind = TicketMessageKind.SystemEvent,
            Body = previousAssignee is null
                ? $"Assigned to {target.DisplayName}"
                : $"Reassigned to {target.DisplayName}",
            CreatedAt = now,
        };

    /// <summary>
    /// Rule 4, and it is the reason this is a shared method rather than a line in each handler: the code
    /// is chosen by the PRIOR STATE, so the pickup endpoint and the assign endpoint must both be capable
    /// of writing either one.
    /// </summary>
    internal static Task LogAssignmentAsync(
        IAuditApi audit,
        Ticket ticket,
        Guid? previousAssignee,
        Guid newAssignee,
        CancellationToken ct) =>
        audit.LogAsync(new AuditEntry(
            previousAssignee is null ? AuditActions.TicketAssigned : AuditActions.TicketReassigned,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            Before: new { AssigneeUserAccountId = previousAssignee },
            After: new { AssigneeUserAccountId = newAssignee, ticket.Status }), ct);

    /// <summary>
    /// Rule 7: <c>TicketAssignedToYou</c>, and only when the new Assignee is not the caller. Telling an
    /// Accountant they assigned something to themselves is noise, and noise is how a notification list
    /// stops being read.
    /// </summary>
    internal static Task NotifyAssigneeAsync(
        INotificationApi notifications,
        Ticket ticket,
        AccountSummary target,
        Guid callerAccountId,
        CancellationToken ct) =>
        target.Id == callerAccountId
            ? Task.CompletedTask
            : notifications.NotifyAsync(new NotificationRequest(
                target.Id.ToString(),
                NotificationEvents.TicketAssignedToYou,
                $"{ticket.Reference} was assigned to you",
                $"{ticket.Title} is now yours.",
                ticket.Id), ct);
}
