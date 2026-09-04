using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.5 -- the correction round, and the subtlest handler in the slice.
///
/// THE REQUEST IS A DELTA; WHAT IS WRITTEN IS A SNAPSHOT. Every field of the type version gets a row in
/// the new revision, either newly supplied or carried forward, because a revision IS "an immutable
/// snapshot of ALL field values at one moment" (§4.5 rule 2). A partial revision cannot be read as a
/// snapshot and the question "what did they originally claim" stops being answerable.
///
/// THE PART THAT IS EASY TO SKIP, and which nothing will report as a bug: an accepted value carried
/// forward keeps its acceptance (§4.5 rule 4). Verifications attach to a FieldValue in a SPECIFIC
/// revision, and the new revision has NEW FieldValue rows, so the acceptance must be copied as a new
/// FieldVerification pointing at the new row -- PRESERVING THE ORIGINAL VERIFIER AND TIMESTAMP, because
/// the record must say who accepted it and when, not that the correction re-accepted it. Skip it and the
/// Office re-verifies every field on every round; that gets reported as the app being tedious, never as
/// a bug.
///
/// A REJECTION IS NOT CARRIED FORWARD (rule 5). An unchanged rejected field arrives unverified, so the
/// Accountant can accept it or reject it again. Copying the rejection would make the ticket permanently
/// unclosable with no action available to anyone.
///
/// THE PREVIOUS REVISION IS NEVER TOUCHED (rule 1). Nothing here issues an UPDATE against
/// ticket_revisions or against an existing field_values row.
/// </summary>
public class SubmitRevisionHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly ITicketTypesApi _ticketTypes;
    private readonly IDocumentApi _documents;
    private readonly INotificationApi _notifications;

    public SubmitRevisionHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit,
        IEmployeeApi employees,
        ITicketTypesApi ticketTypes,
        IDocumentApi documents,
        INotificationApi notifications)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
        _employees = employees;
        _ticketTypes = ticketTypes;
        _documents = documents;
        _notifications = notifications;
    }

    public async Task<RevisionSubmittedDto> Handle(
        SubmitRevisionRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "SubmitRevision", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);

        // Matrix §7 gives AA/AU any ticket, CA their own Customer's, and EMP where Creator or Subject --
        // which is exactly what the visibility filter above already enforced. No further role test: a
        // second, differently-worded copy of the matrix is how the two come to disagree.

        // §5 and Ticket.FieldsEditable: Draft and AwaitingInformation only. Consulted as the property
        // rather than as a local status list, so this handler cannot drift from the other four places
        // that ask the same question.
        if (!ticket.FieldsEditable)
            throw new AppException(
                $"The answers on a ticket in status '{TicketTransitions.DisplayName(ticket.Status)}' are "
                + "frozen and cannot be revised.", 422);

        var rulesVersion = await _ticketTypes.GetVersionByIdAsync(
                               ticket.TicketTypeVersionId, TicketAccess.DescriptorAudienceForRules, ct)
                           ?? throw new AppException(
                               "This ticket's type version could not be resolved.", 422);

        var descriptors = rulesVersion.Fields ?? [];
        var isAccountant = TicketVisibility.IsAccountant(user);

        // §4.5 rule 6 / §0.3 step 5: the IDOR again. The dictionary handed to the validator contains ONLY
        // this ticket's live documents, so a FileUpload value naming a document from another ticket is
        // rejected without this handler writing a comparison of its own. Passing an unfiltered lookup
        // would defeat the check entirely.
        var documents = (await _documents.ListByTicketAsync(ticket.Id, ct))
            .ToDictionary(document => document.Id);

        var previous = await PreviousValuesAsync(ticket, ct);

        // ── The merge, and why it is shaped like this ────────────────────────────────────────────────
        //
        // The caller's own values plus the values being carried forward IN THE CALLER'S HALF are handed
        // to the validator TOGETHER, as one submission. That is not tidiness: conditional visibility is
        // evaluated against the values in the submission (§6.4 rule 1), so a dependent field whose
        // controlling answer was given in revision 1 and not resubmitted would otherwise evaluate as
        // HIDDEN -- and a value supplied for a hidden field is a 422. The correction round would fail
        // with "not applicable given the other answers" on a field the user can plainly see.
        //
        // Only the caller's half, because the two halves are DISJOINT (§6.3): merging in an
        // Accountant-only value that already exists would hit the validator's wrong-half branch and
        // return 403 to a Customer-side caller for a field they never touched. The other half is copied
        // straight across below, unvalidated -- it was validated when it was written, against these same
        // frozen descriptors.
        var submitted = TicketFieldValueInputDto.ToSubmitted(req.FieldValues).ToList();
        var offered = submitted.Select(value => value.FieldKey).ToHashSet(StringComparer.Ordinal);

        foreach (var field in descriptors)
        {
            if (!CallerMayWrite(field, isAccountant) || offered.Contains(field.Key))
                continue;

            if (!previous.TryGetValue(field.Key, out var carried))
                continue;

            // A carried FileUpload is deliberately NOT re-offered to the validator. Its document may
            // since have been soft-deleted -- a permitted operation -- and re-validating would then
            // reject a value nobody touched, making the ticket impossible to correct. It is copied
            // across instead, with the consequence that a FileUpload field cannot act as a conditional
            // controller across a correction round. Reported.
            if (field.DataType == FieldDataTypes.FileUpload)
                continue;

            submitted.Add(ToSubmitted(field.Key, carried));
            offered.Add(field.Key);
        }

        var now = DateTimeOffset.UtcNow;

        // enforceRequired is FALSE here even when this revision submits the ticket, and the reason is not
        // laxity: the validator's required check asks "is this key in THIS SUBMISSION", and in a
        // correction round a required field answered in revision 1 is legitimately absent from the delta.
        // Passing true would 422 on every correction that did not restate every answer. The gate is run
        // below, against the completed snapshot, which is the set the rule is actually about.
        var rows = FieldValueValidation.Validate(
            submitted, rulesVersion, user.Role, enforceRequired: false, now, documents).ToList();

        // The other half, plus carried FileUploads: copied, never revalidated, never mutated in place --
        // a NEW row with a new id, because the old one belongs to the old revision forever (rule 1).
        foreach (var field in descriptors)
        {
            if (offered.Contains(field.Key))
                continue;

            if (previous.TryGetValue(field.Key, out var carried))
                rows.Add(CopyForward(carried, now));
        }

        // Descriptor order, so a revision reads the way the form does.
        var order = descriptors.Select((field, index) => (field.Key, index))
            .ToDictionary(pair => pair.Key, pair => pair.index, StringComparer.Ordinal);

        rows = [.. rows.OrderBy(row => order.TryGetValue(row.FieldKey, out var index) ? index : int.MaxValue)];

        var submits = ticket.Status == TicketStatus.AwaitingInformation;

        if (submits)
        {
            // The transition table's condition on AwaitingInformation -> Submitted, evaluated over the
            // COMPLETED snapshot rather than over the delta.
            var unanswered = TicketMapper.UnansweredRequiredVisibleFields(rulesVersion, rows);
            if (unanswered.Count > 0)
                throw new AppException(
                    $"These required fields still need an answer: {string.Join(", ", unanswered)}.", 422);
        }

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var revision = new TicketRevision
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,

            // Max + 1, read inside the transaction. Two concurrent corrections cannot both become
            // revision N: one of them violates uq_ticket_revisions_sequence and is mapped to 409 below.
            SequenceNumber = await NextSequenceNumberAsync(ticket, ct),
            SubmittedByUserAccountId = callerAccountId,
            SubmittedAt = now,
            Note = req.Note,
        };

        foreach (var row in rows)
        {
            row.TicketRevisionId = revision.Id;
            revision.FieldValues.Add(row);
        }

        _db.TicketRevisions.Add(revision);
        ticket.Revisions.Add(revision);

        var carriedAcceptances = CarryVerificationsForward(rows, previous);
        _db.FieldVerifications.AddRange(carriedAcceptances);

        var previousRevisionId = ticket.CurrentRevisionId;
        ticket.CurrentRevisionId = revision.Id;

        TicketMessage? systemEvent = null;
        var fromStatus = ticket.Status;

        if (submits)
        {
            // null retains the Assignee: the person who asked the question keeps the ticket, and it does
            // NOT return to the pickup pool (§4.2 rule 1).
            systemEvent = TicketTransitions.Apply(ticket, TicketStatus.Submitted, null, now);
            _db.TicketMessages.Add(systemEvent);
            ticket.Messages.Add(systemEvent);
        }
        else
        {
            // The Draft path writes no transition -- a draft stays a draft until it is submitted -- but
            // it still writes the tickets row (current_revision_id), so the concurrency token must move.
            // §13 item 3, decided: this handler ALWAYS APPENDS, in Draft as well. Editing revision 1 in
            // place would mean deleting field_values rows, and §1.9 permits no delete of any kind.
            TicketConcurrency.Touch(ticket, now);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            // uq_ticket_revisions_sequence: somebody else's correction got this sequence number first.
            // 409 and never a 500 -- it is the same lost-update the version check exists for, caught one
            // layer down (§4.5 rule 8).
            throw new AppException(
                "This ticket was corrected by someone else while you were working. Reload and try "
                + "again.", 409);
        }

        await _audit.LogAsync(new AuditEntry(
            AuditActions.RevisionSubmitted,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            Before: new { Status = fromStatus, RevisionId = previousRevisionId },
            After: new
            {
                RevisionId = revision.Id,
                revision.SequenceNumber,
                ticket.Status,
                CarriedForwardCount = rows.Count(row => row.IsCarriedForward),
                CarriedAcceptances = carriedAcceptances.Count,
            }), ct);

        if (submits && systemEvent is not null)
            await NotifyAssigneeAsync(ticket, ct);

        await _transaction.CommitAsync(ct);

        return new RevisionSubmittedDto
        {
            TicketId = ticket.Id,
            RevisionId = revision.Id,
            SequenceNumber = revision.SequenceNumber,
            CarriedForwardCount = rows.Count(row => row.IsCarriedForward),
            Ticket = TicketMapper.ToState(ticket),
        };
    }

    /// <summary>
    /// §6.3's split, in the one form this slice may express it: by <c>IsVisibleToCustomer</c>. There is no
    /// <c>IsAccountantOnly</c> flag and adding one would give the system two answers.
    /// </summary>
    private static bool CallerMayWrite(FieldDescriptorDetailDto field, bool isAccountant) =>
        isAccountant ? !field.IsVisibleToCustomer : field.IsVisibleToCustomer;

    /// <summary>
    /// The current revision's values with their verifications, keyed by field key. Read AsNoTracking:
    /// nothing here may modify them, and a tracked graph is one careless property set away from an UPDATE
    /// on a revision that rule 1 declares immutable.
    /// </summary>
    private async Task<Dictionary<string, FieldValue>> PreviousValuesAsync(
        Ticket ticket, CancellationToken ct)
    {
        var byKey = new Dictionary<string, FieldValue>(StringComparer.Ordinal);

        if (ticket.CurrentRevisionId is not { } revisionId)
            return byKey;

        var values = await _db.FieldValues
            .AsNoTracking()
            .Include(value => value.Verifications)
            .Where(value => value.TicketRevisionId == revisionId)
            .ToListAsync(ct);

        foreach (var value in values)
            byKey[value.FieldKey] = value;

        return byKey;
    }

    private async Task<int> NextSequenceNumberAsync(Ticket ticket, CancellationToken ct)
    {
        var highest = await _db.TicketRevisions
            .Where(revision => revision.TicketId == ticket.Id)
            .MaxAsync(revision => (int?)revision.SequenceNumber, ct);

        return (highest ?? 0) + 1;
    }

    /// <summary>
    /// A previous row as a submitted value. Every carrier is passed and the validator reads only the one
    /// the descriptor's DataType selects, so this needs no type switch of its own -- one more place that
    /// maps eleven types to five columns is one more place that can disagree with §1.4.1.
    /// </summary>
    private static FieldValueValidation.SubmittedFieldValue ToSubmitted(string key, FieldValue row) =>
        new(key,
            row.ValueText,
            row.ValueNumber,
            row.ValueDate,
            row.ValueDateTo,
            row.ValueBoolean,
            row.ValueDocumentId,

            // A MultipleChoice answer is a JSON array in value_text; every other type ignores this.
            TicketMapper.ParseChoices(row.ValueText),
            IsCarriedForward: true);

    /// <summary>
    /// A NEW row holding the old value. <c>CreatedAt</c> is <paramref name="now"/> -- the row was created
    /// now, in this revision; the fact that its VALUE is older is what <c>IsCarriedForward</c> records.
    /// Verifications are deliberately not copied here; that is <see cref="CarryVerificationsForward"/>'s
    /// job, and only for acceptances.
    /// </summary>
    private static FieldValue CopyForward(FieldValue previous, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        FieldKey = previous.FieldKey,
        ValueText = previous.ValueText,
        ValueNumber = previous.ValueNumber,
        ValueDate = previous.ValueDate,
        ValueDateTo = previous.ValueDateTo,
        ValueBoolean = previous.ValueBoolean,
        ValueDocumentId = previous.ValueDocumentId,
        IsCarriedForward = true,
        CreatedAt = now,
    };

    /// <summary>
    /// §4.5 rule 4, the requirement the whole handler exists to get right. For every row carried forward
    /// whose previous value was ACCEPTED, a new FieldVerification pointing at the NEW row, keeping the
    /// ORIGINAL verifier and the ORIGINAL timestamp.
    ///
    /// Rule 5: a REJECTION is not copied. An unchanged rejected field comes back unverified so the
    /// Accountant can accept it or reject it again; copying the rejection would leave the ticket
    /// unclosable with no action available.
    /// </summary>
    private static List<FieldVerification> CarryVerificationsForward(
        IEnumerable<FieldValue> rows, IReadOnlyDictionary<string, FieldValue> previous)
    {
        var carried = new List<FieldVerification>();

        foreach (var row in rows)
        {
            if (!row.IsCarriedForward)
                continue;

            if (!previous.TryGetValue(row.FieldKey, out var old))
                continue;

            if (TicketMapper.LatestVerification(old) is not { } latest || !latest.IsAccepted)
                continue;

            var acceptance = new FieldVerification
            {
                Id = Guid.NewGuid(),
                FieldValueId = row.Id,
                Outcome = VerificationOutcome.Accepted,

                // Null, and ck_field_verifications_reason requires exactly that for an acceptance.
                RejectionReason = null,
                VerifiedByUserAccountId = latest.VerifiedByUserAccountId,
                VerifiedAt = latest.VerifiedAt,
            };

            carried.Add(acceptance);
            row.Verifications.Add(acceptance);
        }

        return carried;
    }

    /// <summary>
    /// The Assignee, and nobody else: they asked the question, and this is the answer. The Office at
    /// large is not told, because the ticket never went back into the pool.
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
