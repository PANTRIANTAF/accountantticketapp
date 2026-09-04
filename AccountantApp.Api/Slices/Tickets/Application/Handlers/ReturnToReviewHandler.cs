using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.9 rule 3. <c>Answered → InReview</c>, Accountants only.
///
/// THIS IS NOT A REOPEN. <c>(Answered, InReview)</c> IS in the closed table and <c>(Closed, InReview)</c>
/// is NOT, and the two look alike and are opposite. This one is an Accountant deciding, BEFORE closing,
/// that the answer was not finished -- the ticket never left the Office's hands. The other is the reopen
/// §9.1 forbids outright. "Do not make them consistent" (<c>TicketTransitions</c>).
///
/// The Customer side is NOT notified. There is no notification kind for it in §4.0 G, and that is right:
/// nothing is being asked of them, and "your answer was withdrawn, we are looking at it again" is anxiety
/// with no action attached. The SystemEvent records it in the conversation, which they can read.
///
/// The reason, if given, is an INTERNAL NOTE (§4.10 rule 3's channel): the Office second-guessing its own
/// answer is the Office's business. It is written here rather than through the internal-note endpoint
/// because it belongs to this one transaction -- but note that the caller is an Accountant by the
/// catalogue's own restriction on this action, so no Customer-side actor can reach this code path.
/// </summary>
public class ReturnToReviewHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;

    public ReturnToReviewHandler(
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
        ReturnToReviewRequestDto req, CurrentUser user, CancellationToken ct)
    {
        // The action name is "ReturnTicketToReview" while the class is ReturnToReviewHandler -- the plan
        // names them differently in §7.2 and §4.9, and the catalogue's spelling is the one that has to
        // match this literal byte for byte.
        await _permissions.RequireAsync(user, "ReturnTicketToReview", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);

        var reason = req.Reason?.Trim();

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var now = DateTimeOffset.UtcNow;
        var fromStatus = ticket.Status;

        // null retains the Assignee, and InReview requires one -- the ticket goes back to the person who
        // answered it, not into the pickup pool.
        var systemEvent = TicketTransitions.Apply(ticket, TicketStatus.InReview, null, now);
        _db.TicketMessages.Add(systemEvent);
        ticket.Messages.Add(systemEvent);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            var note = new TicketMessage
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                AuthorUserAccountId = callerAccountId,
                Kind = TicketMessageKind.InternalNote,
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
            Before: new { Status = fromStatus },

            // No ClosedAt on either side. ck_tickets_closed ties it to the Closed status and this ticket
            // was never Closed -- it was Answered -- so there is nothing here to lose or to restore.
            After: new { ticket.Status, ticket.AssigneeUserAccountId }), ct);

        await _transaction.CommitAsync(ct);

        return TicketMapper.ToState(ticket);
    }
}
