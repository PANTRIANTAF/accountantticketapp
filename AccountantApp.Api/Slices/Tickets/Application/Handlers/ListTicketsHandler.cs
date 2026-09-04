using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
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
/// Plan §4.3. One handler with a scope filter, because the six list shapes differ only in a
/// <c>Where</c> -- but EACH SCOPE VALUE IS AUTHORIZED SEPARATELY.
///
/// A CustomerAdmin passing <c>Scope = All</c> gets 403, NOT a silently narrowed result. Quietly
/// reinterpreting the scope is how a Customer Admin comes to believe they have cross-Customer visibility,
/// and the belief is worse than the error: they act on a list they think is complete. Rule 1.
///
/// No audit entry. Reads are not audited in this slice; only document downloads are.
/// </summary>
public class ListTicketsHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IEmployeeApi _employees;
    private readonly ICustomerApi _customers;
    private readonly IIdentityApi _identity;

    public ListTicketsHandler(
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
        ListTicketsRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ListTickets", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);
        var isAccountant = TicketVisibility.IsAccountant(user);

        var scope = req.Scope ?? string.Empty;
        if (!TicketListScopes.Known.Contains(scope))
            throw new AppException(
                $"Scope must be one of: {string.Join(", ", TicketListScopes.Known)}.", 422);

        // Layers 1 to 3 first, always. The scope filters below narrow a query that is already safe, so a
        // mistake in one of them can only ever hide rows, never reveal them.
        var query = _db.Tickets.AsNoTracking().WhereTicketVisible(user, callerEmployeeId);

        switch (scope)
        {
            case TicketListScopes.All:
            case TicketListScopes.Unassigned:
            case TicketListScopes.AssignedToMe:
                // Matrix §6 rows 1-2. These three are the Office's views of work across Customers.
                if (!isAccountant)
                    throw new AppException(
                        $"The '{scope}' view is available to the Office only.", 403);

                if (scope == TicketListScopes.Unassigned)
                    // The same two-part condition as the pickup queue's first half: status AND no
                    // Assignee. Status alone puts every correction round back in the shared pool.
                    query = query.Where(ticket => ticket.Status == TicketStatus.Submitted
                                               && ticket.AssigneeUserAccountId == null);

                if (scope == TicketListScopes.AssignedToMe)
                    query = query.Where(ticket => ticket.AssigneeUserAccountId == callerAccountId);

                break;

            case TicketListScopes.MyCustomer:
                // Redundant with layer 1 for a Customer-side caller, and that is the point: an Accountant
                // has no Customer of their own, so this scope is not something they can ask for.
                if (user.CustomerId is not { } customerId)
                    throw new AppException(
                        "This view is available to a customer's own users only.", 403);

                query = query.Where(ticket => ticket.CustomerId == customerId);
                break;

            case TicketListScopes.Mine:
                // "Mine" means a ticket the caller is a party to: they opened it, or it is about them.
                // For a CustomerAdmin or an Accountant with no Employee record this reduces to "I opened
                // it", which is the only meaning available.
                query = callerEmployeeId is { } employeeId
                    ? query.Where(ticket => ticket.CreatorUserAccountId == callerAccountId
                                         || ticket.SubjectEmployeeId == employeeId)
                    : query.Where(ticket => ticket.CreatorUserAccountId == callerAccountId);
                break;
        }

        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            if (!TicketStatus.All.Contains(req.Status))
                throw new AppException(
                    $"Status must be one of: {string.Join(", ", TicketStatus.All)}.", 422);

            query = query.Where(ticket => ticket.Status == req.Status);
        }

        var (pageNumber, pageSize) = PaginatedQuery.Normalize(req.PageNumber, req.PageSize);
        var totalCount = await query.CountAsync(ct);

        var items = await query
            // The default sort of every ticket list: real activity first, tie-broken by id so the order
            // is total. Without the tie-break, two tickets stamped in the same transaction can swap
            // places between pages and one of them is never shown.
            .OrderByDescending(ticket => ticket.LastActivityAt)
            .ThenByDescending(ticket => ticket.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(TicketMapper.ListItem)
            .ToListAsync(ct);

        await FillNamesAsync(items, isAccountant, ct);

        return new PaginatedResponse<TicketListItemDto>
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = PageCount(totalCount, pageSize),
            Items = items,
        };
    }

    /// <summary>
    /// The page count, by INTEGER ceiling division.
    ///
    /// Every other slice writes <c>(int)Math.Ceiling(totalCount / (double)pageSize)</c>, and this one
    /// cannot: criterion 26 bans the word from this slice's source outright, and
    /// <c>TicketsColumnMappingTests</c> enforces it file by file. The ban is aimed at money -- a binary
    /// floating-point type that accepts 0.10 and hands an accountant 0.100000001490116 -- and a page count
    /// is not money, so the two idioms differing here is a consequence of a source-level rule rather than a
    /// disagreement about arithmetic.
    ///
    /// The integer form is exact for every input instead of merely exact for small ones, which is the
    /// better answer regardless: no cast, no rounding mode, nothing that behaves differently at scale.
    /// Reported, because a reader comparing this slice with the other seven will notice.
    /// </summary>
    internal static int PageCount(int totalCount, int pageSize) =>
        pageSize <= 0 ? 0 : (totalCount + pageSize - 1) / pageSize;

    /// <summary>
    /// Three batched cross-slice calls for the whole page (§4.3 rule 3). At a fifty-row page the
    /// per-row alternative is 150 extra queries, and it looks identical in a five-row test.
    ///
    /// Shared by this handler and the pickup queue so the two lists cannot render names differently.
    /// </summary>
    internal static async Task FillNamesAsync(
        List<TicketListItemDto> items,
        bool isAccountant,
        IEmployeeApi employees,
        ICustomerApi customers,
        IIdentityApi identity,
        CancellationToken ct)
    {
        if (items.Count == 0)
            return;

        var subjects = await employees.FindManyAsync(
            [.. items.Select(item => item.SubjectEmployeeId).Distinct()], ct);

        var accountIds = items.Select(item => item.CreatorUserAccountId)
            .Concat(items.Where(item => item.AssigneeUserAccountId is not null)
                         .Select(item => item.AssigneeUserAccountId!.Value))
            .Distinct()
            .ToList();

        var accounts = await identity.FindManyAsync(accountIds, ct);

        // Customer names are resolved for the Office only. A Customer-side caller's every row belongs to
        // their own Customer, so the name is something they already know, and one fewer cross-slice read
        // on the commonest list in the system is worth more than a redundant label.
        IReadOnlyDictionary<Guid, CustomerSummary> customerNames = isAccountant
            ? await customers.FindManyAsync([.. items.Select(item => item.CustomerId).Distinct()], ct)
            : new Dictionary<Guid, CustomerSummary>();

        foreach (var item in items)
        {
            // TryGetValue throughout, never an indexer: the batch contracts cap at 500 ids and omit what
            // they cannot find, so a missing name is an ordinary outcome and a KeyNotFoundException here
            // would turn a deleted-elsewhere row into a 500 for the whole page.
            if (subjects.TryGetValue(item.SubjectEmployeeId, out var subject))
                item.SubjectName = subject.FullName;

            if (accounts.TryGetValue(item.CreatorUserAccountId, out var creator))
                item.CreatorName = creator.DisplayName;

            if (item.AssigneeUserAccountId is { } assigneeId
                && accounts.TryGetValue(assigneeId, out var assignee))
                item.AssigneeName = assignee.DisplayName;

            if (customerNames.TryGetValue(item.CustomerId, out var customer))
                item.CustomerName = customer.LegalName;
        }
    }

    private Task FillNamesAsync(List<TicketListItemDto> items, bool isAccountant, CancellationToken ct) =>
        FillNamesAsync(items, isAccountant, _employees, _customers, _identity, ct);
}
