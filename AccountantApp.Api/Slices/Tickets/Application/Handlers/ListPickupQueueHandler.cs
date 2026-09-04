using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Customers.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.4 -- the query the Office lives in, and §9.8 makes it the one most likely to be built wrong.
///
/// TWO CONDITIONS, and NEITHER of them is "status equals Submitted":
///
///   1. <c>Submitted</c> AND no Assignee. The second half is not optional: AwaitingInformation ->
///      Submitted RETAINS the Assignee, so status alone puts every correction round back into the shared
///      pool while the Accountant who asked the question is still on it (rule 2).
///   2. Any OPEN status AND the Assignee's account is not Active. This is the ONLY thing in the system
///      that surfaces work stranded by a suspension -- nothing happens automatically on suspension
///      (§9.8 rule 4), so a stranded ticket that is not in this queue is invisible forever.
///
/// The union happens in SQL, as one <c>IQueryable</c> with an <c>||</c>, because rule 5 requires both
/// halves to be paginated TOGETHER. Two <c>ToListAsync</c> calls concatenated in memory give the wrong
/// page size and the wrong sort, and the bug only appears once the queue is longer than one page.
///
/// A read: no transaction, no audit, no write. Taking a ticket from here is <c>PickupTicketHandler</c>'s
/// job, and taking one surfaced by condition 2 is an audited REASSIGNMENT (rule 7).
/// </summary>
public class ListPickupQueueHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IEmployeeApi _employees;
    private readonly ICustomerApi _customers;
    private readonly IIdentityApi _identity;

    public ListPickupQueueHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IEmployeeApi employees,
        ICustomerApi customers,
        IIdentityApi identity)
    {
        _db = db;
        _permissions = permissions;
        _employees = employees;
        _customers = customers;
        _identity = identity;
    }

    public async Task<PaginatedResponse<TicketListItemDto>> Handle(
        ListPickupQueueRequestDto req, CurrentUser user, CancellationToken ct)
    {
        // The catalogue restricts this to AA and AU, so there is no role branch below. A Customer-side
        // caller never reaches the query at all.
        await _permissions.RequireAsync(user, "ListPickupQueue", ct: ct);

        // An array, not TicketStatus.Open itself: Enumerable.Contains over a materialised collection is
        // what the Npgsql provider translates to `= ANY (...)`. An instance Contains on the
        // IReadOnlySet is not guaranteed to translate, and an untranslatable predicate on THIS query
        // would silently evaluate client-side over every open ticket in the database.
        var openStatuses = TicketStatus.Open.ToArray();

        // Layers 1 to 3 even here. For an Accountant layer 1 is a no-op and layer 3 excludes Drafts,
        // which no open status includes anyway -- but the queue is not the place to start a habit of
        // querying tickets without the visibility filter.
        var visible = _db.Tickets.AsNoTracking().WhereTicketVisible(user, callerEmployeeId: null);

        // Condition 2, part 1: which accounts currently hold open work. ONE bulk call to Identity per
        // request (§12 note 2, accepted), not one per ticket.
        var assigneeIds = await visible
            .Where(ticket => openStatuses.Contains(ticket.Status)
                          && ticket.AssigneeUserAccountId != null)
            .Select(ticket => ticket.AssigneeUserAccountId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var strandedAssignees = await StrandedAssigneesAsync(assigneeIds, ct);

        var query = visible.Where(ticket =>
            (ticket.Status == TicketStatus.Submitted && ticket.AssigneeUserAccountId == null)
            || (openStatuses.Contains(ticket.Status)
                && ticket.AssigneeUserAccountId != null
                && strandedAssignees.Contains(ticket.AssigneeUserAccountId.Value)));

        var (pageNumber, pageSize) = PaginatedQuery.Normalize(req.PageNumber, req.PageSize);
        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(ticket => ticket.LastActivityAt)
            .ThenByDescending(ticket => ticket.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(TicketMapper.ListItem)
            .ToListAsync(ct);

        await ListTicketsHandler.FillNamesAsync(
            items, isAccountant: true, _employees, _customers, _identity, ct);

        // The flag the Office needs to tell the two halves apart. Without it a stranded ticket looks
        // like an ordinary assigned one that has wandered into the queue, and an Accountant who takes it
        // cannot tell they are performing a reassignment.
        foreach (var item in items)
            item.IsStranded = item.AssigneeUserAccountId is { } assignee
                              && strandedAssignees.Contains(assignee);

        return new PaginatedResponse<TicketListItemDto>
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount,
            // Shared with the ticket list so the two pagers cannot round differently, and integer-only
            // because this slice's source may not contain the word the other seven slices use here.
            TotalPages = ListTicketsHandler.PageCount(totalCount, pageSize),
            Items = items,
        };
    }

    /// <summary>
    /// The assignees whose accounts are not Active. Rule 4: AN UNKNOWN ACCOUNT COUNTS AS NOT ACTIVE --
    /// <c>!TryGetValue || !IsActive</c>. Failing toward surfacing the work is the whole point; a ticket
    /// assigned to an account that no longer resolves is exactly the stranded case, and treating it as
    /// healthy hides it from everyone.
    ///
    /// Chunked at 500 because <c>FindManyAsync</c> THROWS above that cap, and an office of more than 500
    /// accountants holding open work must not turn this queue into a 500.
    /// </summary>
    private async Task<List<Guid>> StrandedAssigneesAsync(
        List<Guid> assigneeIds, CancellationToken ct)
    {
        var stranded = new List<Guid>();

        foreach (var chunk in assigneeIds.Chunk(500))
        {
            var accounts = await _identity.FindManyAsync(chunk, ct);

            stranded.AddRange(chunk.Where(id =>
                !accounts.TryGetValue(id, out var account) || !account.IsActive));
        }

        return stranded;
    }
}
