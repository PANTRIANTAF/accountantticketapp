using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.6. An Accountant's judgement on ONE field value. Accountants only -- matrix §7 gives CA and
/// EMP "No", and the catalogue is what enforces that.
///
/// IT NEVER WRITES A FIELD VALUE. §9.4 is LOCKED: "There is no handler, no endpoint, and no code path by
/// which an Accountant's identity ends up attached to a Customer-supplied FieldValue." Rejecting with a
/// reason is the only thing an Accountant may do to a Customer's answer, and this handler adds a
/// verification row and nothing else.
///
/// IT APPENDS. A re-verification is a NEW row (§1.5); the latest by <c>VerifiedAt</c> is what counts, so
/// an accept-after-reject leaves both, and the history of a disputed field survives.
///
/// It does NOT transition the ticket. §4.6 rule 6: rejecting several fields and then moving to Awaiting
/// Information ONCE is the intended sequence -- a transition per rejection sends the Customer side one
/// notification per field.
/// </summary>
public class VerifyFieldHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly ITicketTypesApi _ticketTypes;
    private readonly INotificationApi _notifications;

    public VerifyFieldHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit,
        IEmployeeApi employees,
        ITicketTypesApi ticketTypes,
        INotificationApi notifications)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
        _employees = employees;
        _ticketTypes = ticketTypes;
        _notifications = notifications;
    }

    public async Task<FieldVerifiedDto> Handle(
        VerifyFieldRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "VerifyField", ct: ct);

        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);
        TicketAccess.RequireNotTerminal(ticket);

        // Rule 1: the outcome is one of two words. Validated here so the CHECK constraint is a backstop
        // and not the error message.
        if (!VerificationOutcome.All.Contains(req.Outcome))
            throw new AppException(
                $"Outcome must be one of: {string.Join(", ", VerificationOutcome.All)}.", 422);

        var isRejection = req.Outcome == VerificationOutcome.Rejected;
        var reason = req.RejectionReason?.Trim();

        // ck_field_verifications_reason: required when rejected, FORBIDDEN when accepted, and the
        // whitespace-only case counts as missing. Both halves are answered here with a real message --
        // reaching the constraint would produce a 500 for what is a bad request (rule 1).
        if (isRejection && string.IsNullOrWhiteSpace(reason))
            throw new AppException(
                "A rejection needs a reason. It is shown to the customer exactly as written.", 422);

        if (!isRejection && !string.IsNullOrWhiteSpace(reason))
            // Not silently dropped: a reason the Accountant typed and nobody will ever read is worse than
            // being told it does not belong on an acceptance.
            throw new AppException(
                "A reason belongs on a rejection only. Reject the field, or accept it without a reason.",
                422);

        var value = await LoadCurrentRevisionValueAsync(ticket, req.FieldValueId, ct);

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var now = DateTimeOffset.UtcNow;

        var verification = new FieldVerification
        {
            Id = Guid.NewGuid(),
            FieldValueId = value.Id,
            Outcome = req.Outcome,
            RejectionReason = isRejection ? reason : null,
            VerifiedByUserAccountId = callerAccountId,
            VerifiedAt = now,
        };

        _db.FieldVerifications.Add(verification);

        // JUDGMENT CALL, reported: a verification Touches the ticket. It is not a status change, so
        // §4.7 rule 3 does not name it -- but it IS activity on the ticket, the pickup queue and every
        // list sort on LastActivityAt, and this handler already takes a Version and enforces it. A
        // verification that left the token alone would let a stale client keep verifying against a
        // revision that had since been superseded.
        TicketConcurrency.Touch(ticket, now);

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            isRejection ? AuditActions.FieldRejected : AuditActions.FieldVerified,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            After: new
            {
                FieldValueId = value.Id,
                value.FieldKey,
                Outcome = req.Outcome,
                VerificationId = verification.Id,

                // The reason IS in the audit entry. It is shown to the Customer side verbatim, so it is
                // already a user-facing string, and "why was this rejected" is the first question asked
                // of the log six months later.
                RejectionReason = verification.RejectionReason,
            }), ct);

        if (isRejection)
            await NotifyRejectionAsync(ticket, value, reason!, ct);

        await _transaction.CommitAsync(ct);

        return new FieldVerifiedDto
        {
            TicketId = ticket.Id,
            FieldValueId = value.Id,
            VerificationId = verification.Id,
            Outcome = verification.Outcome,
            Ticket = TicketMapper.ToState(ticket),
        };
    }

    /// <summary>
    /// The value, and only if it is in THIS ticket's CURRENT revision.
    ///
    /// Two separate checks wearing one query. The <c>TicketId</c> half is §0.3 step 5's IDOR in yet
    /// another disguise: the caller supplied the ticket id and the value id independently, so a value
    /// from another Customer's ticket must not become verifiable just because this Accountant may read
    /// SOME ticket -- and for an Accountant, who is unscoped, the ticket check alone always passes.
    ///
    /// The CURRENT-revision half is rule 4: verifying a superseded revision's value is meaningless -- the
    /// answer it judged has already been replaced -- and it is 422, not 404, because the value exists and
    /// the caller may see it.
    /// </summary>
    private async Task<FieldValue> LoadCurrentRevisionValueAsync(
        Ticket ticket, Guid fieldValueId, CancellationToken ct)
    {
        var match = await _db.FieldValues
            .Where(value => value.Id == fieldValueId)
            .Select(value => new
            {
                Value = value,
                TicketId = _db.TicketRevisions
                    .Where(revision => revision.Id == value.TicketRevisionId)
                    .Select(revision => (Guid?)revision.TicketId)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        if (match is null || match.TicketId != ticket.Id)
            throw new AppException("Field value not found on this ticket.", 404);

        if (ticket.CurrentRevisionId is not { } currentRevisionId
            || match.Value.TicketRevisionId != currentRevisionId)
            throw new AppException(
                "That answer belongs to an earlier revision and can no longer be verified.", 422);

        return match.Value;
    }

    /// <summary>
    /// <c>FieldRejected</c> is one of the seven EMAILED kinds: the Customer side cannot act on a
    /// rejection they never saw, and the ticket sits in Awaiting Information until they do.
    ///
    /// The field's LABEL is used only when the field is customer-visible. An Accountant-only descriptor's
    /// label must not travel to the Customer side in a notification -- §4.3 rule 5 makes it absent from
    /// every response they receive, and a notification body is a response by another route.
    /// </summary>
    private async Task NotifyRejectionAsync(
        Ticket ticket, FieldValue value, string reason, CancellationToken ct)
    {
        var version = await _ticketTypes.GetVersionByIdAsync(
            ticket.TicketTypeVersionId, TicketAccess.DescriptorAudienceForRules, ct);

        var descriptor = (version?.Fields ?? []).FirstOrDefault(
            field => string.Equals(field.Key, value.FieldKey, StringComparison.Ordinal));

        var label = descriptor is { IsVisibleToCustomer: true } ? descriptor.Label : "One of your answers";

        await TicketAccess.NotifyCustomerSideAsync(
            _notifications,
            _employees,
            ticket,
            NotificationEvents.FieldRejected,
            $"{ticket.Reference}: {label} needs attention",
            $"{label} was not accepted. {reason}",
            ct);
    }
}
