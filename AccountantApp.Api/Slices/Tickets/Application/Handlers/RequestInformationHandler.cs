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
/// Plan §4.9. <c>InReview → AwaitingInformation</c>, Accountants only.
///
/// THE TRANSITION HAS A CONDITION: at least one rejected field, OR a posted question (rule 4). A ticket
/// sent back with neither is a ticket the Customer side cannot act on -- they are told more information is
/// needed and nothing says what. So this handler either finds a rejection in the current revision or takes
/// the question in the request, and refuses with a 422 when it has neither.
///
/// THE ASSIGNEE IS RETAINED. <c>TicketTransitions.Apply(..., null, ...)</c> retains it, and
/// <c>AwaitingInformation</c> is one of the statuses <c>ck_tickets_assignee</c> REQUIRES one for: the
/// Accountant who asked the question still owns the ticket, and the answer must come back to them
/// (§4.2 rule 1).
/// </summary>
public class RequestInformationHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly INotificationApi _notifications;

    public RequestInformationHandler(
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
        RequestInformationRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "RequestInformation", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);

        var question = req.Question?.Trim();
        var hasQuestion = !string.IsNullOrWhiteSpace(question);

        if (!hasQuestion)
        {
            // The other half of the condition. A rejection already carries its own reason, shown verbatim
            // to the Customer side, so the ticket is actionable without a covering message -- which is
            // exactly why §4.6 rule 6 wants several fields rejected and then ONE transition.
            var values = await TicketAccess.CurrentValuesAsync(_db, ticket, ct);

            var rejected = values.Any(
                value => TicketMapper.LatestVerification(value) is { IsRejected: true });

            if (!rejected)
                throw new AppException(
                    "Reject at least one answer, or include a question, before asking for more "
                    + "information.", 422);
        }

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var now = DateTimeOffset.UtcNow;
        var fromStatus = ticket.Status;

        // null RETAINS the Assignee. Also the point at which an illegal from-status (anything but
        // InReview) becomes a 422 from the closed table.
        var systemEvent = TicketTransitions.Apply(ticket, TicketStatus.AwaitingInformation, null, now);
        _db.TicketMessages.Add(systemEvent);
        ticket.Messages.Add(systemEvent);

        if (hasQuestion)
        {
            // Kind derived from the role, never from the body (§4.10 rule 1). This handler is Accountants
            // only, so the question is an AccountantResponse -- visible to the Customer side, which is the
            // whole purpose of asking it.
            var message = new TicketMessage
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                AuthorUserAccountId = callerAccountId,
                Kind = TicketMessageKind.AccountantResponse,
                Body = question!,
                CreatedAt = now,
            };

            _db.TicketMessages.Add(message);
            ticket.Messages.Add(message);
        }

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.TicketStatusChanged,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            Before: new { Status = fromStatus },
            After: new { ticket.Status, ticket.AssigneeUserAccountId, QuestionPosted = hasQuestion }), ct);

        // InformationRequested is EMAILED (§4.0 G). The ticket now waits on the Customer side, and a
        // ticket waiting on somebody who was never told is the case the email list exists for.
        await TicketAccess.NotifyCustomerSideAsync(
            _notifications,
            _employees,
            ticket,
            NotificationEvents.InformationRequested,
            $"{ticket.Reference}: more information needed",
            hasQuestion
                ? question!
                : $"Some of the answers on {ticket.Title} need correcting. Open the ticket to see which.",
            ct);

        await _transaction.CommitAsync(ct);

        return TicketMapper.ToState(ticket);
    }
}
