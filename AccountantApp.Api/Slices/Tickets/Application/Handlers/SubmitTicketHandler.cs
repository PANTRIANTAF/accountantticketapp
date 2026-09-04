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
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.2. TWO OPERATIONS WEARING ONE NAME, and they differ in the one way that matters:
///
/// | From                 | Assignee                                  | Notifies             |
/// |----------------------|-------------------------------------------|----------------------|
/// | Draft                | stays null -- into the unassigned pool     | TicketSubmitted      |
/// | AwaitingInformation  | RETAINED -- not back in the pool          | CorrectionSubmitted  |
///
/// Clearing the Assignee on the second path is the bug that sends every correction back to the shared
/// queue and loses the person who asked the question. <c>TicketTransitions.Apply(..., null, ...)</c>
/// retains it; there is no code here that assigns or clears.
/// </summary>
public class SubmitTicketHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly ITicketTypesApi _ticketTypes;
    private readonly IIdentityApi _identity;
    private readonly INotificationApi _notifications;

    public SubmitTicketHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit,
        IEmployeeApi employees,
        ITicketTypesApi ticketTypes,
        IIdentityApi identity,
        INotificationApi notifications)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
        _employees = employees;
        _ticketTypes = ticketTypes;
        _identity = identity;
        _notifications = notifications;
    }

    public async Task<TicketStateDto> Handle(
        SubmitTicketRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "SubmitTicket", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);

        // Matrix §7's "Submit a ticket" row: AA/AU Creator only; CustomerAdmin Creator OR any ticket of
        // their own Customer; Employee Creator only. So the Creator test applies to three of the four
        // roles, and the Admin's exemption is the one relaxation -- which layer 3 then makes unreachable
        // for another person's Draft, since no role but the Creator can see one. In practice the
        // exemption bites on AwaitingInformation. §13 item 2 -- reported, not resolved here.
        if (user.Role != UserRole.CustomerAdmin && ticket.CreatorUserAccountId != callerAccountId)
            throw new AppException("Only the person who opened this ticket can submit it.", 403);

        var fromStatus = ticket.Status;
        if (fromStatus is not (TicketStatus.Draft or TicketStatus.AwaitingInformation))
            throw new AppException(
                $"A ticket in status '{TicketTransitions.DisplayName(fromStatus)}' cannot be submitted.",
                422);

        // The COMPLETE descriptor set: the gate has to see an Accountant-only field that controls the
        // visibility of a Customer field, or the field it controls silently stops being required.
        var rulesVersion = await _ticketTypes.GetVersionByIdAsync(
                               ticket.TicketTypeVersionId, TicketAccess.DescriptorAudienceForRules, ct)
                           ?? throw new AppException(
                               "This ticket's type version could not be resolved.", 422);

        var currentValues = await TicketAccess.CurrentValuesAsync(_db, ticket, ct);

        // The transition table's condition, in full: "all required VISIBLE fields valid", where visible
        // means IsVisibleToCustomer AND not conditionally hidden (§6.4). Accountant-only fields are never
        // required for a submission -- they are the Accountant's to fill (§4.2 rule 3).
        var unanswered = TicketMapper.UnansweredRequiredVisibleFields(rulesVersion, currentValues);
        if (unanswered.Count > 0)
            throw new AppException(
                $"These required fields still need an answer: {string.Join(", ", unanswered)}.", 422);

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var now = DateTimeOffset.UtcNow;

        // null retains the Assignee. On the Draft path there is none; on the AwaitingInformation path
        // that retention is the whole rule.
        var systemEvent = TicketTransitions.Apply(ticket, TicketStatus.Submitted, null, now);
        _db.TicketMessages.Add(systemEvent);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.TicketStatusChanged,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            Before: new { Status = fromStatus },
            After: new { ticket.Status, ticket.AssigneeUserAccountId }), ct);

        if (fromStatus == TicketStatus.Draft)
            await NotifyOfficeAsync(ticket, ct);
        else
            await NotifyAssigneeAsync(ticket, ct);

        await _transaction.CommitAsync(ct);

        return TicketMapper.ToState(ticket);
    }

    private async Task NotifyOfficeAsync(Ticket ticket, CancellationToken ct)
    {
        var office = await _identity.ListAccountantsAsync(activeOnly: true, ct);
        if (office.Count == 0)
            return;

        await _notifications.NotifyManyAsync(
        [
            .. office.Select(accountant => new NotificationRequest(
                accountant.Id.ToString(),
                NotificationEvents.TicketSubmitted,
                $"New ticket {ticket.Reference}",
                $"{ticket.Title} was submitted and is waiting to be picked up.",
                ticket.Id))
        ], ct);
    }

    /// <summary>
    /// The correction path notifies the ASSIGNEE -- the person who asked the question -- and nobody else.
    /// A null Assignee here would mean the retention rule was broken upstream; there is simply nobody to
    /// tell, and inventing the Office as a fallback would put the ticket back in front of the pool it
    /// never left.
    /// </summary>
    private async Task NotifyAssigneeAsync(Ticket ticket, CancellationToken ct)
    {
        if (ticket.AssigneeUserAccountId is not { } assignee)
            return;

        await _notifications.NotifyAsync(new NotificationRequest(
            assignee.ToString(),
            NotificationEvents.CorrectionSubmitted,
            $"Correction on {ticket.Reference}",
            $"{ticket.Title} has been resubmitted with the information you asked for.",
            ticket.Id), ct);
    }
}
