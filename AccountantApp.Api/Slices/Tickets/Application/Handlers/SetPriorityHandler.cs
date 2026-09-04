using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.7. Normal or High, Accountants only (matrix §7: CA and EMP "No" -- priority is the Office's
/// triage tool, not a customer's way of jumping the queue).
///
/// SEPARATE FROM THE DUE DATE ON PURPOSE (rule 1): the two audit under different codes, and one handler
/// with two nullable properties cannot tell "not supplied" from "clear it".
///
/// There is no general UpdateTicketHandler anywhere in this slice, and rule 2 says there must not be.
/// Customer, Type, Type version, Creator, Subject and Preceded-by are immutable after creation; Title is
/// derived; Status has the transition table; Assignee has §4.8. Priority and due date are what is left,
/// which is why these two handlers exist and no third one does.
/// </summary>
public class SetPriorityHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;

    public SetPriorityHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit,
        IEmployeeApi employees)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
        _employees = employees;
    }

    public async Task<TicketStateDto> Handle(
        SetTicketPriorityRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "SetTicketPriority", ct: ct);

        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);

        // Rule 4. Closed and Cancelled are read-only; 422, because the request is well formed and the
        // ticket is simply over.
        TicketAccess.RequireNotTerminal(ticket);

        if (!TicketPriority.All.Contains(req.Priority))
            throw new AppException(
                $"Priority must be one of: {string.Join(", ", TicketPriority.All)}.", 422);

        var before = ticket.Priority;

        // A no-op is not an error, but it must not be recorded as a change either: an audit log full of
        // "High -> High" entries is a log nobody reads. The version is still returned unchanged, so a
        // client that resends the same value stays in step.
        if (before == req.Priority)
            return TicketMapper.ToState(ticket);

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var now = DateTimeOffset.UtcNow;

        ticket.Priority = req.Priority;

        // Rule 3: this writes the tickets row, so the token moves. Never `ticket.Version += 1` here --
        // one implementation of the token, in TicketConcurrency.
        TicketConcurrency.Touch(ticket, now);

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.PriorityChanged,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            Before: new { Priority = before },
            After: new { ticket.Priority }), ct);

        await _transaction.CommitAsync(ct);

        return TicketMapper.ToState(ticket);
    }
}
