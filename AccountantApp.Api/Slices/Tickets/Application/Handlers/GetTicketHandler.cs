using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Customers.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Infrastructure;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.3 rules 4-8. The ticket, its revisions with their field values and verifications, and the
/// conversation.
///
/// TWO THINGS ARE FILTERED BY ROLE, both server-side:
///
///   1. The descriptors and therefore the field VALUES. The version is resolved with the caller's own
///      role, so <c>TicketTypes</c> strips Accountant-only descriptors, and <c>TicketMapper.ToDetail</c>
///      keeps only values whose key survived. Two projections behind one decision, made once, in the
///      slice that owns the descriptors.
///   2. The conversation, through the <c>CustomerVisible</c> allow-list. Matrix §6: internal notes must
///      be excluded by SERVER-SIDE FILTERING, not by the React app choosing not to display them.
///
/// A ticket the caller may not see is 404 from the visibility filter, never 403.
/// </summary>
public class GetTicketHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IEmployeeApi _employees;
    private readonly ICustomerApi _customers;
    private readonly IIdentityApi _identity;
    private readonly ITicketTypesApi _ticketTypes;

    public GetTicketHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IEmployeeApi employees,
        ICustomerApi customers,
        IIdentityApi identity,
        ITicketTypesApi ticketTypes)
    {
        _db = db;
        _permissions = permissions;
        _employees = employees;
        _customers = customers;
        _identity = identity;
        _ticketTypes = ticketTypes;
    }

    public async Task<TicketDetailDto> Handle(
        GetTicketRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ViewTicket", ct: ct);

        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        // AsNoTracking with the whole graph: this is a read, and tracking a ticket's every revision and
        // message costs identity-resolution work for entities nothing will modify.
        var ticket = await _db.Tickets
            .AsNoTracking()
            .WhereTicketVisible(user, callerEmployeeId)
            .Include(candidate => candidate.Revisions)
                .ThenInclude(revision => revision.FieldValues)
                    .ThenInclude(value => value.Verifications)
            .Include(candidate => candidate.Messages)
                .ThenInclude(message => message.AttachedDocuments)
            .FirstOrDefaultAsync(candidate => candidate.Id == req.TicketId, ct);

        if (ticket is null)
            throw new AppException("Ticket not found.", 404);

        var version = await TicketAccess.ResolveResponseVersionAsync(
                          _ticketTypes, ticket.TicketTypeVersionId, user, ct)
                      ?? throw new AppException(
                          "This ticket's type version could not be resolved.", 422);

        var detail = TicketMapper.ToDetail(ticket, version, user);

        var subject = await _employees.FindAsync(ticket.SubjectEmployeeId, ct);
        detail.SubjectName = subject?.FullName;

        var accountIds = new List<Guid> { ticket.CreatorUserAccountId };
        if (ticket.AssigneeUserAccountId is { } assigneeId)
            accountIds.Add(assigneeId);

        // Every author of a visible message, plus every verifier, in the same batch. One call, not one
        // per row: a ticket with forty messages would otherwise be forty cross-slice reads.
        accountIds.AddRange(detail.Messages
            .Where(message => message.AuthorUserAccountId is not null)
            .Select(message => message.AuthorUserAccountId!.Value));

        accountIds.AddRange(detail.Revisions.Select(revision => revision.SubmittedByUserAccountId));
        accountIds.AddRange(detail.Revisions
            .SelectMany(revision => revision.FieldValues)
            .SelectMany(value => value.Verifications)
            .Select(verification => verification.VerifiedByUserAccountId));

        var accounts = await FindManyChunkedAsync(accountIds, ct);

        if (accounts.TryGetValue(ticket.CreatorUserAccountId, out var creator))
            detail.CreatorName = creator.DisplayName;

        if (ticket.AssigneeUserAccountId is { } assignee
            && accounts.TryGetValue(assignee, out var assigneeAccount))
            detail.AssigneeName = assigneeAccount.DisplayName;

        foreach (var message in detail.Messages)
            if (message.AuthorUserAccountId is { } authorId
                && accounts.TryGetValue(authorId, out var author))
                message.AuthorName = author.DisplayName;

        foreach (var revision in detail.Revisions)
        {
            if (accounts.TryGetValue(revision.SubmittedByUserAccountId, out var submitter))
                revision.SubmittedByName = submitter.DisplayName;

            foreach (var verification in revision.FieldValues.SelectMany(value => value.Verifications))
                if (accounts.TryGetValue(verification.VerifiedByUserAccountId, out var verifier))
                    verification.VerifiedByName = verifier.DisplayName;
        }

        // Only for the Office. A Customer-side caller is reading their own Customer's ticket by
        // construction, so the name is one they already have.
        if (TicketVisibility.IsAccountant(user))
        {
            var customer = await _customers.FindAsync(ticket.CustomerId, ct);
            detail.CustomerName = customer?.LegalName;
        }

        return detail;
    }

    /// <summary>
    /// <c>IIdentityApi.FindManyAsync</c> is capped at 500 ids and THROWS above it. A long-running ticket
    /// with hundreds of messages, revisions and verifications can exceed that from ordinary use, so the
    /// list is chunked rather than trusted to stay small. An InvalidOperationException here would be a
    /// 500 on a read the caller is entitled to.
    /// </summary>
    private async Task<Dictionary<Guid, AccountSummary>> FindManyChunkedAsync(
        IEnumerable<Guid> accountIds, CancellationToken ct)
    {
        var resolved = new Dictionary<Guid, AccountSummary>();

        foreach (var chunk in accountIds.Distinct().Chunk(500))
        {
            var batch = await _identity.FindManyAsync(chunk, ct);
            foreach (var pair in batch)
                resolved[pair.Key] = pair.Value;
        }

        return resolved;
    }
}
