using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Infrastructure;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.7. Sets or clears the due date, Accountants only.
///
/// A DATE IN THE PAST IS ALLOWED (rule 5). An Accountant recording an already-missed statutory deadline
/// is ordinary bookkeeping, and a future-date guard would make the system unable to describe the
/// situation it most needs to track. There is deliberately no such guard here.
///
/// Null CLEARS the date. That is the whole reason this is not merged with priority: one combined request
/// shape cannot distinguish "leave it alone" from "remove it".
/// </summary>
public class SetDueDateHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;

    public SetDueDateHandler(
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
        SetTicketDueDateRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "SetTicketDueDate", ct: ct);

        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);
        TicketAccess.RequireNotTerminal(ticket);

        var before = ticket.DueDate;

        if (before == req.DueDate)
            return TicketMapper.ToState(ticket);

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var now = DateTimeOffset.UtcNow;

        ticket.DueDate = req.DueDate;
        TicketConcurrency.Touch(ticket, now);

        await _db.SaveChangesAsync(ct);

        // Both values are in the entry, including nulls: "the due date was removed" is the fact an audit
        // reader needs, and it is indistinguishable from "was never set" if only the new value is stored.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.DueDateChanged,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            Before: new { DueDate = before },
            After: new { ticket.DueDate }), ct);

        await _transaction.CommitAsync(ct);

        return TicketMapper.ToState(ticket);
    }
}
