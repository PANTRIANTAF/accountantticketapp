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
/// Plan §4.1. Creates a Draft, or creates and submits in one request.
///
/// Six of this slice's immutable-after-creation values are decided here and nowhere else: Customer,
/// Type, Type version, Creator, Subject and Preceded-by (01-DomainModel.md §3). There is no update
/// handler that accepts any of them -- if one is wrong the ticket is cancelled and a new one opened --
/// so a mistake made here is permanent, which is why every one of them is resolved rather than trusted.
/// </summary>
public class CreateTicketHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly ITicketTypesApi _ticketTypes;
    private readonly IIdentityApi _identity;
    private readonly INotificationApi _notifications;
    private readonly ITicketReferenceAllocator _references;

    public CreateTicketHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit,
        IEmployeeApi employees,
        ITicketTypesApi ticketTypes,
        IIdentityApi identity,
        INotificationApi notifications,
        ITicketReferenceAllocator references)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
        _employees = employees;
        _ticketTypes = ticketTypes;
        _identity = identity;
        _notifications = notifications;
        _references = references;
    }

    public async Task<TicketDetailDto> Handle(
        CreateTicketRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "CreateTicket", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);
        var isAccountant = TicketVisibility.IsAccountant(user);

        // 1. The Subject, and the Customer THROUGH the Subject.
        var subject = await _employees.FindAsync(req.SubjectEmployeeId, ct)
                      ?? throw new AppException("Employee not found.", 404);

        // Rule 1: the Customer is resolved, never supplied. For a Customer-side caller it is their own;
        // for an Accountant it is the Subject's, because the Employee already determines it. Two sources
        // for one value is two chances to disagree, and the disagreement would put a ticket under the
        // wrong Customer permanently.
        var resolvedCustomerId = isAccountant
            ? subject.CustomerId
            : user.CustomerId ?? throw new AppException(
                "This account is not attached to a Customer.", 403);

        // Rule 3: a Subject at another Customer is 404, not 403 -- a 403 confirms the Employee exists.
        if (subject.CustomerId != resolvedCustomerId)
            throw new AppException("Employee not found.", 404);

        // Rule 4: §9.6 rule 3. A Departed Employee may not be the Subject of a NEW ticket. Existing
        // tickets are untouched, and this check appears on no read or update path.
        if (!subject.IsActive)
            throw new AppException(
                "This employee has departed and cannot be the subject of a new ticket.", 422);

        // Rule 5: an Employee opens tickets about themselves only -- not for a colleague, not for a
        // subordinate. The comparison is Employee id to Employee id; subject.UserAccountId == user.Id is
        // also true in the common case and compares the wrong pair the moment the Subject is accountless.
        if (user.Role == UserRole.Employee && subject.Id != callerEmployeeId)
            throw new AppException(
                "An employee may only open a ticket about themselves.", 403);

        // 3. The Type's CURRENT ACTIVE version, frozen onto the ticket. An inactive, unknown or
        //    out-of-audience type is 422 -- GetTicketTypeAsync answers null for all three.
        var version = await _ticketTypes.GetTicketTypeAsync(req.TicketTypeId, user.Role, ct)
                      ?? throw new AppException(
                          "This ticket type is not available.", 422);

        // JUDGMENT CALL, reported: AllowEmployeeToOpen is a shipped property of the type and this is the
        // only place it could ever be read. The plan's §4.1 pseudo-code does not mention it. Left
        // unenforced it means nothing anywhere in the system, so it is enforced here -- and only for the
        // Employee role, since that is the only reading its name supports.
        if (user.Role == UserRole.Employee && !version.AllowEmployeeToOpen)
            throw new AppException(
                "This ticket type may only be opened on your behalf by your administrator or the "
                + "Office.", 403);

        // 4. The predecessor link. §9.1: a continuation of a Closed ticket is a NEW ticket pointing back
        //    at it, because there is no reopen.
        if (req.PrecededByTicketId is { } predecessorId)
        {
            var predecessor = await TicketAccess.LoadVisibleAsync(
                _db, user, callerEmployeeId, predecessorId, ct);

            if (predecessor.CustomerId != resolvedCustomerId)
                throw new AppException(
                    "The preceding ticket belongs to another customer.", 422);

            if (predecessor.Status != TicketStatus.Closed)
                throw new AppException(
                    "Only a closed ticket can be continued by a new one.", 422);
        }

        // The COMPLETE descriptor set for the rules. GetTicketTypeAsync has already stripped
        // Accountant-only descriptors for a Customer-side caller, and FieldValueValidation needs to SEE
        // them: its wrong-half branch is what turns an Accountant-only key supplied by a Customer into
        // the 403 §6.3 rule 2 specifies, and against a stripped set the same input reads as an unknown
        // key and a 422. See the report -- ITicketTypesApi has no unstripped read.
        var rulesVersion = isAccountant
            ? version
            : await _ticketTypes.GetVersionByIdAsync(
                  version.VersionId, TicketAccess.DescriptorAudienceForRules, ct)
              ?? throw new AppException("This ticket type is not available.", 422);

        var submitted = TicketFieldValueInputDto.ToSubmitted(req.FieldValues);

        var now = DateTimeOffset.UtcNow;

        // enforceRequired follows the intent: a Draft is a work in progress, so requiring every field to
        // save one would make the status pointless. A direct submission is held to the full standard.
        //
        // documents: null, deliberately and unavoidably. A document is uploaded AGAINST an existing
        // ticket, so at creation there is no document that can belong to this ticket and any FileUpload
        // value is rejected by the validator with "the attached document was not found on this ticket".
        // A required FileUpload field therefore makes SubmitImmediately impossible -- reported.
        var fieldValues = FieldValueValidation.Validate(
            submitted, rulesVersion, user.Role, enforceRequired: req.SubmitImmediately, now);

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var reference = await _references.AllocateAsync(now.Year, ct);

        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            Reference = reference,
            CustomerId = resolvedCustomerId,
            TicketTypeId = version.Id,

            // The VERSION's Guid, not the type's. Getting these two the wrong way round produces a
            // ticket whose descriptors can never be resolved, and both are Guids so nothing complains.
            TicketTypeVersionId = version.VersionId,
            CreatorUserAccountId = callerAccountId,
            SubjectEmployeeId = subject.Id,
            Status = TicketStatus.Draft,

            // Rule 10: a Draft has no Assignee, and ck_tickets_assignee is the backstop.
            AssigneeUserAccountId = null,
            Priority = TicketPriority.Normal,

            // Rule 9: derived from the type name and the Subject so lists read without opening each
            // ticket. Not recomputed if the Employee is later renamed (§13 item 7).
            Title = $"{version.DisplayName} — {subject.FullName}",
            Version = 1,
            CreatedAt = now,
            LastActivityAt = now,
            PrecededByTicketId = req.PrecededByTicketId,
        };

        _db.Tickets.Add(ticket);
        await _db.SaveChangesAsync(ct);

        var revision = new TicketRevision
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            SequenceNumber = 1,
            SubmittedByUserAccountId = callerAccountId,
            SubmittedAt = now,
            Note = req.Note,
        };

        foreach (var value in fieldValues)
        {
            value.TicketRevisionId = revision.Id;
            revision.FieldValues.Add(value);
        }

        _db.TicketRevisions.Add(revision);
        ticket.Revisions.Add(revision);

        // Rule 6: current_revision_id in a SECOND SaveChanges, because the two tables reference each
        // other (§1.3). Both writes are in this transaction, so a failure leaves neither.
        ticket.CurrentRevisionId = revision.Id;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.TicketCreated,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            After: new
            {
                ticket.Reference,
                ticket.TicketTypeId,
                ticket.TicketTypeVersionId,
                ticket.SubjectEmployeeId,
                ticket.Status,
                RevisionId = revision.Id,
            }), ct);

        if (req.SubmitImmediately)
            await SubmitAsync(ticket, revision, rulesVersion, now, ct);

        await _transaction.CommitAsync(ct);

        var detail = TicketMapper.ToDetail(ticket, version, user);
        detail.SubjectName = subject.FullName;
        return detail;
    }

    /// <summary>
    /// The Draft → Submitted half of §4.2, inlined here rather than by calling SubmitTicketHandler.
    ///
    /// Calling that handler would re-run its own <c>RequireAsync("SubmitTicket")</c> against a ticket
    /// that does not exist outside this transaction yet, and would audit the creation as though it were a
    /// separate request. The transition itself still goes through the one shared table.
    /// </summary>
    private async Task SubmitAsync(
        Ticket ticket,
        TicketRevision revision,
        TicketTypeDetailDto rulesVersion,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var unanswered = TicketMapper.UnansweredRequiredVisibleFields(rulesVersion, revision.FieldValues);
        if (unanswered.Count > 0)
            throw new AppException(
                $"These required fields still need an answer: {string.Join(", ", unanswered)}.", 422);

        var systemEvent = TicketTransitions.Apply(ticket, TicketStatus.Submitted, null, now);
        _db.TicketMessages.Add(systemEvent);
        ticket.Messages.Add(systemEvent);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.TicketStatusChanged,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            Before: new { Status = TicketStatus.Draft },
            After: new { ticket.Status }), ct);

        // The Office is notified, not one Accountant: nobody owns this ticket yet, which is the whole
        // point of the unassigned pool. In-app only -- an email per submission would be unusable.
        var office = await _identity.ListAccountantsAsync(activeOnly: true, ct);
        if (office.Count > 0)
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
}
