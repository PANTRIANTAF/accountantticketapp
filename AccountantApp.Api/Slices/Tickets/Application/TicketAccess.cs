using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Tickets.Application;

/// <summary>
/// The first three steps of §4.0 B, in one place: resolve the caller's Employee id, load the ticket
/// through <see cref="TicketVisibility.WhereTicketVisible"/>, 404 on a miss.
///
/// Seventeen handlers open with that sequence. Written out at each call site it is five lines that look
/// like boilerplate, which is exactly the problem: the one handler that resolves no Employee id and
/// passes <c>null</c> for an Employee caller silently loses visibility layer 2 and returns another
/// Employee's ticket. Here it cannot be forgotten, only mis-called.
///
/// It deliberately does NOT wrap the rest of §4.0 B. <c>RequireVersion</c>, the per-handler
/// authorization qualifiers and the transition check all differ per handler, and a helper that took a
/// version and a status list would end up with a boolean parameter per handler.
/// </summary>
public static class TicketAccess
{
    /// <summary>
    /// The role this slice passes to <c>ITicketTypesApi</c> when it needs the COMPLETE descriptor set to
    /// evaluate a server-side RULE rather than to build a response.
    ///
    /// It exists because the contract has no unstripped read: every method takes a <c>callerRole</c> and
    /// removes Accountant-only descriptors for a Customer-side one. Two rules need the full set:
    ///
    ///   - <see cref="FieldValueValidation.Validate"/>, whose wrong-half branch is what turns an
    ///     Accountant-only key supplied by a Customer-side caller into the 403 §6.3 rule 2 requires.
    ///     Against a stripped set the same input is an unknown key and a 422.
    ///   - <see cref="TicketMapper.UnansweredRequiredVisibleFields"/>, which must be able to see a
    ///     controlling field that is Accountant-only. Against a stripped set the Customer field it
    ///     controls evaluates as hidden, so a required field silently stops being required.
    ///
    /// NOTHING resolved through this constant reaches a response. The descriptors and values a caller
    /// receives are always resolved a second time with their OWN role, so the audience decision is still
    /// made once, in <c>TicketTypes</c>. Named and commented in one place so that a future reader finds
    /// the reason rather than an unexplained hardcoded role -- and so the whole workaround disappears at
    /// one call site if <c>TicketTypes</c> ever exposes an explicit "for rules" read. Reported.
    /// </summary>
    public const UserRole DescriptorAudienceForRules = UserRole.AccountantAdmin;

    /// <summary>
    /// The caller's Employee id, or null. Only an Employee-role caller has one that matters: visibility
    /// layer 2 is the Employee branch, and every other role passes null.
    ///
    /// A CustomerAdmin who also has an Employee record deliberately gets null. Their reach is "all
    /// tickets of their own Customer" (matrix §6) and resolving their Employee id here would narrow it
    /// to Creator-or-Subject -- a filter the matrix does not ask for.
    /// </summary>
    public static async Task<Guid?> ResolveCallerEmployeeIdAsync(
        IEmployeeApi employees, CurrentUser user, CancellationToken ct)
    {
        if (user.Role != UserRole.Employee)
            return null;

        var callerAccountId = TicketVisibility.ParseAccountId(user.Id);
        if (callerAccountId is null)
            return null;

        var employee = await employees.FindByAccountAsync(callerAccountId.Value, ct);
        return employee?.Id;
    }

    /// <summary>
    /// One visible ticket, TRACKED, or 404.
    ///
    /// 404 and never 403, for every reason a miss can happen -- out of Customer scope, not a party to
    /// it, somebody else's Draft, or simply nonexistent. All four answer identically, so the id space
    /// cannot be probed (§3.1).
    /// </summary>
    public static async Task<Ticket> LoadVisibleAsync(
        TicketsDbContext db,
        CurrentUser user,
        Guid? callerEmployeeId,
        Guid ticketId,
        CancellationToken ct)
    {
        var ticket = await db.Tickets
            .WhereTicketVisible(user, callerEmployeeId)
            .FirstOrDefaultAsync(candidate => candidate.Id == ticketId, ct);

        return ticket ?? throw new AppException("Ticket not found.", 404);
    }

    /// <summary>
    /// The frozen version SHAPED FOR THE CALLER, for a response.
    ///
    /// The fallback in the middle is not defensive padding, it is a collision between two rules. A ticket
    /// type with <c>AllowEmployeeToOpen = false</c> is out of the Employee audience, so
    /// <c>GetVersionByIdAsync(..., UserRole.Employee, ...)</c> returns NULL for it -- while visibility
    /// layer 2 still grants that Employee read access to a ticket of that type on which they are the
    /// Subject, which is precisely what such a type is for: the Customer Admin opens it about somebody.
    /// Without the fallback that Employee can see the ticket exists and gets no field labels and no
    /// values, or a 422 on their own ticket.
    ///
    /// It retries as <c>CustomerAdmin</c> rather than stripping the descriptors here. That keeps ONE copy
    /// of the <c>IsVisibleToCustomer</c> strip -- <c>TicketTypes</c> still applies it, because
    /// CustomerAdmin is a Customer-side role -- and bypasses only the <c>AllowEmployeeToOpen</c> gate,
    /// which governs who may OPEN a type, not who may read a ticket already opened under it. A local
    /// strip would be the second copy that correction note T-12 exists to prevent. Reported.
    /// </summary>
    public static async Task<TicketTypeDetailDto?> ResolveResponseVersionAsync(
        ITicketTypesApi ticketTypes, Guid ticketTypeVersionId, CurrentUser user, CancellationToken ct)
    {
        var version = await ticketTypes.GetVersionByIdAsync(ticketTypeVersionId, user.Role, ct);
        if (version is null && user.Role == UserRole.Employee)
            version = await ticketTypes.GetVersionByIdAsync(
                ticketTypeVersionId, UserRole.CustomerAdmin, ct);

        return version;
    }

    /// <summary>
    /// The CURRENT revision's field values, with their verifications. Empty when the ticket somehow has
    /// no current revision.
    ///
    /// Read through the FieldValues set rather than by loading the ticket's whole revision graph: every
    /// gate in the state machine asks about the CURRENT revision only, and a ticket in its tenth
    /// correction round carries nine revisions' worth of rows that no gate reads. Tracked, because
    /// <c>VerifyFieldHandler</c> and <c>SubmitRevisionHandler</c> attach new rows to what this returns.
    /// </summary>
    public static async Task<List<FieldValue>> CurrentValuesAsync(
        TicketsDbContext db, Ticket ticket, CancellationToken ct)
    {
        if (ticket.CurrentRevisionId is not { } revisionId)
            return [];

        return await db.FieldValues
            .Include(value => value.Verifications)
            .Where(value => value.TicketRevisionId == revisionId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Refuses an operation on a Closed or Cancelled ticket. 422: the request is well formed, the ticket
    /// is simply over.
    ///
    /// NOT called on the download or list-documents path. Matrix §8 makes downloading from a Closed
    /// ticket "a stated requirement", and a blanket terminal guard applied to all four document
    /// handlers is the way that requirement gets broken (§4.11 rule 2).
    /// </summary>
    public static void RequireNotTerminal(Ticket ticket)
    {
        if (ticket.IsTerminal)
            throw new AppException(
                $"This ticket is {TicketTransitions.DisplayName(ticket.Status).ToLowerInvariant()} and "
                + "can no longer be changed.", 422);
    }

    /// <summary>
    /// The Customer-side recipients of a notification about <paramref name="ticket"/>: the Creator, plus
    /// the Subject's account when the Subject has one and it is not already the Creator.
    ///
    /// §4.0 H, from 01-DomainModel.md §7: an accountless Employee has no UserAccount and therefore
    /// receives no notifications, and when a ticket's Subject is accountless the notifications go to the
    /// Creator. That has to be handled HERE rather than by letting a null recipient reach
    /// <c>INotificationApi</c>, which would either throw or write an orphaned row nobody can ever read.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> CustomerSideRecipientsAsync(
        IEmployeeApi employees, Ticket ticket, CancellationToken ct)
    {
        var recipients = new List<Guid> { ticket.CreatorUserAccountId };

        var subject = await employees.FindAsync(ticket.SubjectEmployeeId, ct);
        if (subject?.UserAccountId is { } subjectAccountId
            && subjectAccountId != ticket.CreatorUserAccountId)
            recipients.Add(subjectAccountId);

        return recipients;
    }

    /// <summary>
    /// One notification per Customer-side recipient, in the caller's transaction.
    ///
    /// <c>EmailBody</c> is left null on purpose. <c>NotificationApi</c> throws if it is set on a kind that
    /// is not emailed, and for a kind that IS emailed the outbox falls back to <c>Body</c> -- so null is
    /// correct for every kind this slice raises. It exists for the single-use-token case (an invitation
    /// link), which no ticket event has.
    /// </summary>
    public static async Task NotifyCustomerSideAsync(
        INotificationApi notifications,
        IEmployeeApi employees,
        Ticket ticket,
        string eventKind,
        string title,
        string body,
        CancellationToken ct)
    {
        var recipients = await CustomerSideRecipientsAsync(employees, ticket, ct);

        await notifications.NotifyManyAsync(
        [
            .. recipients.Select(recipient => new NotificationRequest(
                recipient.ToString(), eventKind, title, body, ticket.Id))
        ], ct);
    }
}
