using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Tickets.Core;

namespace AccountantApp.Api.Slices.Tickets.Application;

/// <summary>
/// The four visibility layers of section 3.1. THIS IS THE MOST SECURITY-CRITICAL FILE IN THE SLICE.
///
/// | Layer | What it does                                          | Applies to             |
/// |-------|-------------------------------------------------------|------------------------|
/// | 1     | Customer boundary; Accountants pass through           | CustomerAdmin, Employee|
/// | 2     | Creator-or-Subject                                    | Employee only          |
/// | 3     | A Draft is visible ONLY to its Creator                | ALL FOUR ROLES         |
/// | 4     | InternalNote messages stripped from the conversation  | CustomerAdmin, Employee|
///
/// EVERY read of a ticket goes through <see cref="WhereTicketVisible"/>, without exception. A handler
/// that writes .WhereInCustomerScope(user) on its own has silently skipped layers 2 and 3.
///
/// LAYER 3 IS OUTSIDE THE Employee BRANCH, AND THAT IS THE WHOLE POINT. Moving it inside the `if` is
/// the single most likely error in this file and it compiles, passes every Employee test, and exposes
/// every Customer's half-finished drafts -- which contain payroll data -- to the entire Office.
/// Matrix section 6: "No Accountant ever sees drafts." A test asserts an AccountantAdmin gets 404 on
/// another person's Draft.
///
/// A miss is 404, never 403: an out-of-scope ticket must not be distinguishable from a nonexistent
/// one, or the id space itself becomes an enumeration oracle. That is why this is a query filter and
/// not a post-load permission check -- a filtered query cannot accidentally return the row and then
/// forget to throw.
/// </summary>
public static class TicketVisibility
{
    /// <summary>
    /// Stacks layers 1 to 3 onto a ticket query.
    /// </summary>
    /// <param name="callerEmployeeId">
    /// The caller's Employee id, resolved BEFORE the query by
    /// IEmployeeApi.FindByAccountAsync(callerAccountId). It is not on CurrentUser -- that carries the
    /// account id, the role and the CustomerId, and nothing else -- so this cannot be a pure one-step
    /// extension. The two-step shape a handler must use:
    /// <code>
    /// Guid? callerEmployeeId = user.Role == UserRole.Employee
    ///     ? (await _employees.FindByAccountAsync(callerAccountId, ct))?.Id
    ///     : null;
    /// var query = _db.Tickets.WhereTicketVisible(user, callerEmployeeId);
    /// </code>
    /// Pass null for any role other than Employee; it is ignored there.
    /// </param>
    public static IQueryable<Ticket> WhereTicketVisible(
        this IQueryable<Ticket> query, CurrentUser user, Guid? callerEmployeeId)
    {
        // Layer 1: the Customer boundary. Accountants pass through; Customer-side roles are pinned to
        // their own Customer. The one shared implementation in Shared/Authorization -- never a local
        // copy of the same Where clause.
        query = query.WhereInCustomerScope(user);

        // user.Id is parsed to a Guid ONCE, out here. A .ToString() or Guid.Parse inside the lambda
        // either fails to translate to SQL or is evaluated per row against a text cast, defeating the
        // index; worse, a "D"-vs-"N" format mismatch compares two strings that are never equal and
        // silently matches nothing, which reads as "the user has no tickets" rather than as a bug.
        var callerAccountId = ParseAccountId(user.Id);
        if (callerAccountId is null)
            // A caller whose id is not a Guid cannot be the Creator or the Subject of anything, and an
            // unparseable id must not mean "no filter". Fail closed.
            return query.Where(_ => false);

        // Layer 3: a Draft is visible ONLY to its Creator, IN EVERY ROLE. Section 9.3, matrix section
        // 6. Deliberately placed before the role branch so that no future edit inside the branch can
        // move it: an AccountantAdmin passes layer 1 and is exempt from layer 2, and must still get a
        // 404 on somebody else's Draft.
        query = query.Where(ticket => ticket.Status != TicketStatus.Draft
                                   || ticket.CreatorUserAccountId == callerAccountId.Value);

        // Layer 2: an Employee sees only tickets they are party to -- Creator or Subject.
        //
        // CustomerAdmin gets NO layer-2 filter, deliberately. Matrix section 6 gives them "all of
        // them" within their Customer, and the note beneath the table is emphatic that the Customer
        // Admin's full visibility within their Customer is a deliberate, accepted decision, including
        // tickets containing payroll and personal tax data. Do not add confidentiality flags or narrow
        // this without an explicit instruction.
        if (user.Role == UserRole.Employee)
        {
            if (callerEmployeeId is null)
                // ASSUMPTION, section 13 item 6 (open in the plan; the plan says "pick one and state
                // it"): an Employee-role account with no Employee record is a BROKEN state, not a
                // permissive one, so it yields an empty result rather than an unfiltered query. Chosen
                // over throwing 401 because this is a query builder used by read and write paths
                // alike, and an empty result gives a uniform 404 on every one of them -- the same
                // answer an out-of-scope id gets. Whoever owns the handlers may prefer a 401 at the
                // resolve step; that belongs there, not here, and this stays fail-closed either way.
                return query.Where(_ => false);

            query = query.Where(ticket => ticket.CreatorUserAccountId == callerAccountId.Value
                                       || ticket.SubjectEmployeeId == callerEmployeeId.Value);
        }

        return query;
    }

    /// <summary>
    /// Layer 4. Strips InternalNote messages for Customer-side callers.
    ///
    /// Matrix section 6 requires the exclusion be "enforced on the server by filtering, not by the
    /// React app choosing not to display them", so every conversation read composes this. It is not a
    /// global query filter on TicketMessage, because such a filter cannot see the caller's role and
    /// would therefore hide internal notes from Accountants too -- from the only people they exist
    /// for. See TicketsDbContext.
    ///
    /// Note the direction: it filters by TicketMessageKind.CustomerVisible, an ALLOW-LIST, not by
    /// "kind != InternalNote". A fifth message kind added later is then invisible to the Customer side
    /// until somebody deliberately adds it. Deny by default.
    /// </summary>
    public static IQueryable<TicketMessage> WhereMessageVisible(
        this IQueryable<TicketMessage> query, CurrentUser user)
    {
        if (IsAccountant(user))
            return query;

        // Copied to an array so the call binds to Enumerable.Contains, which EF translates to
        // `kind IN (...)`. IReadOnlySet<T>.Contains is an instance method EF does not recognise, and
        // an untranslatable predicate on a security filter is the worst possible failure mode: it
        // either throws at runtime or, on a client-evaluating provider, silently stops filtering. The
        // array is derived from the one set, so the allow-list still has a single definition.
        var visibleKinds = TicketMessageKind.CustomerVisible.ToArray();
        return query.Where(message => visibleKinds.Contains(message.Kind));
    }

    /// <summary>
    /// The in-memory counterpart of <see cref="WhereMessageVisible"/>, for a conversation already
    /// materialised as part of a loaded ticket. Same allow-list, so the two cannot drift.
    /// </summary>
    public static IEnumerable<TicketMessage> WhereMessageVisible(
        this IEnumerable<TicketMessage> messages, CurrentUser user) =>
        IsAccountant(user)
            ? messages
            : messages.Where(message => TicketMessageKind.CustomerVisible.Contains(message.Kind));

    /// <summary>
    /// Both Accountant roles, never one of them. Internal notes are "the Office's private channel, not
    /// the Admin's" (matrix section 6), so an AccountantUser sees them exactly as an AccountantAdmin
    /// does.
    /// </summary>
    public static bool IsAccountant(CurrentUser user) =>
        user.Role is UserRole.AccountantAdmin or UserRole.AccountantUser;

    /// <summary>
    /// Returns null rather than throwing, so callers stay fail-closed instead of turning a malformed
    /// token into a 500.
    /// </summary>
    public static Guid? ParseAccountId(string id) =>
        Guid.TryParse(id, out var parsed) ? parsed : null;

    /// <summary>
    /// The account id of the caller, for the write paths that must record who acted. Throws, because a
    /// write that cannot identify its actor must not proceed -- an audit entry with an unattributed
    /// actor is worse than a failed request (01-DomainModel.md section 6).
    /// </summary>
    public static Guid RequireAccountId(CurrentUser user) =>
        ParseAccountId(user.Id)
        ?? throw new AppException("The authenticated user id is not valid.", 401);
}
