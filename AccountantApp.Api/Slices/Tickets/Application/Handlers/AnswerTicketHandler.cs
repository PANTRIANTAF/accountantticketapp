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
/// Plan §4.9. <c>InReview → Answered</c>, Accountants only. Matrix §7 gives CA and EMP "No" on both this
/// and the close.
///
/// THE GATE (rule 1): no required visible field of the current revision may be unverified or rejected.
/// It is the transition table's own condition, and it is checked again at close (rule 2) because
/// <c>Answered → InReview → Answered</c> can happen in between and a field can be rejected in that window.
/// </summary>
public class AnswerTicketHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly ITicketTypesApi _ticketTypes;
    private readonly INotificationApi _notifications;

    public AnswerTicketHandler(
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
        AnswerTicketRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "AnswerTicket", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
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
                "These answers still need to be accepted or rejected before the ticket can be answered: "
                + $"{string.Join(", ", outstanding)}.", 422);

        var message = req.Message?.Trim();

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var now = DateTimeOffset.UtcNow;
        var fromStatus = ticket.Status;

        var systemEvent = TicketTransitions.Apply(ticket, TicketStatus.Answered, null, now);
        _db.TicketMessages.Add(systemEvent);
        ticket.Messages.Add(systemEvent);

        if (!string.IsNullOrWhiteSpace(message))
        {
            var response = new TicketMessage
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                AuthorUserAccountId = callerAccountId,
                Kind = TicketMessageKind.AccountantResponse,
                Body = message,
                CreatedAt = now,
            };

            _db.TicketMessages.Add(response);
            ticket.Messages.Add(response);
        }

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.TicketStatusChanged,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            Before: new { Status = fromStatus },
            After: new { ticket.Status, ticket.AssigneeUserAccountId }), ct);

        // TicketAnswered is EMAILED. The answer is the thing the Customer side has been waiting for, and
        // it is the one event where not being told promptly defeats the point of the ticket.
        await TicketAccess.NotifyCustomerSideAsync(
            _notifications,
            _employees,
            ticket,
            NotificationEvents.TicketAnswered,
            $"{ticket.Reference} has been answered",
            string.IsNullOrWhiteSpace(message)
                ? $"{ticket.Title} has been answered by the Office."
                : message,
            ct);

        await _transaction.CommitAsync(ct);

        return TicketMapper.ToState(ticket);
    }
}
