using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Tickets.Application;

/// <summary>
/// Entities to response DTOs, and the two "required visible fields" gates the state machine's
/// conditions depend on.
///
/// The gates are here rather than in <see cref="FieldValueValidation"/>, and that is a duplication
/// chosen on purpose after trying the alternative. <c>FieldValueValidation.Validate</c> answers a
/// different question -- "may this caller write these values, and are they well formed" -- and its two
/// halves are DISJOINT (§6.3): an Accountant-only value present in the revision is a wrong-half 403 for
/// a Customer-side caller, so the validator cannot be re-run over a stored revision to ask whether the
/// required fields are answered. Excluding the other half's values first does not work either, because a
/// Customer field whose visibility is conditional on an Accountant-only field would then evaluate as
/// hidden. Its <c>IsConditionallyHidden</c> is also private. So the conditional-visibility rule is
/// evaluated once more here, over stored <see cref="FieldValue"/> rows instead of submitted ones, and
/// the two implementations must be changed together -- see §13 of the report.
/// </summary>
public static class TicketMapper
{
    /// <summary>
    /// The list-row projection, applied in SQL. An <see cref="Expression"/> and not a method taking a
    /// <see cref="Ticket"/>, because a method would force the whole entity -- and its revisions and
    /// conversation -- to be materialised for every row of a fifty-row page.
    ///
    /// The four display-name properties are left null here and filled by the handler from the three
    /// batch contracts (§4.3 rule 3). A name resolved inside this projection would be a cross-slice
    /// query per row, which is the 150-extra-queries case that rule exists to prevent.
    /// </summary>
    public static readonly Expression<Func<Ticket, TicketListItemDto>> ListItem =
        ticket => new TicketListItemDto
        {
            Id = ticket.Id,
            Reference = ticket.Reference,
            Title = ticket.Title,
            Status = ticket.Status,
            Priority = ticket.Priority,
            DueDate = ticket.DueDate,
            CustomerId = ticket.CustomerId,
            TicketTypeId = ticket.TicketTypeId,
            SubjectEmployeeId = ticket.SubjectEmployeeId,
            CreatorUserAccountId = ticket.CreatorUserAccountId,
            AssigneeUserAccountId = ticket.AssigneeUserAccountId,
            CreatedAt = ticket.CreatedAt,
            LastActivityAt = ticket.LastActivityAt,
            ClosedAt = ticket.ClosedAt,
            Version = ticket.Version,
        };

    /// <summary>What every mutation returns. The Version here is the one AFTER the write.</summary>
    public static TicketStateDto ToState(Ticket ticket) => new()
    {
        Id = ticket.Id,
        Reference = ticket.Reference,
        Status = ticket.Status,
        Priority = ticket.Priority,
        DueDate = ticket.DueDate,
        AssigneeUserAccountId = ticket.AssigneeUserAccountId,
        ClosedAt = ticket.ClosedAt,
        LastActivityAt = ticket.LastActivityAt,
        Version = ticket.Version,
    };

    /// <summary>
    /// The full ticket, shaped for the caller's audience.
    /// </summary>
    /// <param name="version">
    /// The frozen version, ALREADY STRIPPED for the caller's role by
    /// <c>ITicketTypesApi.GetVersionByIdAsync(ticket.TicketTypeVersionId, user.Role, ct)</c>. That
    /// stripping is what makes rule 5 of §4.3 hold: the descriptor list a Customer-side caller receives
    /// contains no Accountant-only field, and every field VALUE below is filtered to the keys that
    /// survived it. One audience decision, made in <c>TicketTypes</c>, applied to both halves -- rather
    /// than a second copy of "which fields may this role see" in this slice that could disagree with it.
    /// </param>
    /// <param name="ticket">
    /// With <c>Revisions</c> (their <c>FieldValues</c> and those values' <c>Verifications</c>) and
    /// <c>Messages</c> loaded. Nothing here queries.
    /// </param>
    public static TicketDetailDto ToDetail(Ticket ticket, TicketTypeDetailDto version, CurrentUser user)
    {
        var descriptors = version.Fields ?? [];
        var byKey = descriptors.ToDictionary(field => field.Key, StringComparer.Ordinal);

        var detail = new TicketDetailDto
        {
            Id = ticket.Id,
            Reference = ticket.Reference,
            Title = ticket.Title,
            Status = ticket.Status,
            Priority = ticket.Priority,
            DueDate = ticket.DueDate,
            CustomerId = ticket.CustomerId,
            TicketTypeId = ticket.TicketTypeId,
            TicketTypeVersionId = ticket.TicketTypeVersionId,
            TicketTypeName = version.DisplayName,
            TicketTypeVersionNumber = version.VersionNumber,
            SubjectEmployeeId = ticket.SubjectEmployeeId,
            CreatorUserAccountId = ticket.CreatorUserAccountId,
            AssigneeUserAccountId = ticket.AssigneeUserAccountId,
            PrecededByTicketId = ticket.PrecededByTicketId,
            CreatedAt = ticket.CreatedAt,
            LastActivityAt = ticket.LastActivityAt,
            ClosedAt = ticket.ClosedAt,
            Version = ticket.Version,
            AllowedTransitions = [.. TicketTransitions.AllowedTargetsFrom(ticket.Status)],
            FieldsEditable = ticket.FieldsEditable,
            Fields = [.. descriptors.OrderBy(field => field.DisplayOrder)],
            CurrentRevisionId = ticket.CurrentRevisionId,
        };

        foreach (var revision in ticket.Revisions.OrderByDescending(row => row.SequenceNumber))
            detail.Revisions.Add(new TicketRevisionDto
            {
                Id = revision.Id,
                SequenceNumber = revision.SequenceNumber,
                SubmittedByUserAccountId = revision.SubmittedByUserAccountId,
                SubmittedAt = revision.SubmittedAt,
                Note = revision.Note,
                IsCurrent = ticket.CurrentRevisionId == revision.Id,

                // The audience filter on values: a key absent from the (already stripped) descriptor set
                // is a field this caller is not entitled to know exists, so its value is absent too --
                // not nulled, absent (§4.3 rule 5).
                FieldValues =
                [
                    .. revision.FieldValues
                        .Where(value => byKey.ContainsKey(value.FieldKey))
                        .OrderBy(value => byKey[value.FieldKey].DisplayOrder)
                        .Select(value => ToFieldValue(value, byKey[value.FieldKey]))
                ],
            });

        // Layer 4, through the one shared allow-list rather than a local "kind != InternalNote".
        foreach (var message in ticket.Messages.WhereMessageVisible(user).OrderBy(row => row.CreatedAt))
            detail.Messages.Add(new TicketMessageDto
            {
                Id = message.Id,
                Kind = message.Kind,
                AuthorUserAccountId = message.AuthorUserAccountId,
                Body = message.Body,
                CreatedAt = message.CreatedAt,
                AttachedDocumentIds =
                    [.. message.AttachedDocuments.Select(link => link.DocumentId)],
            });

        return detail;
    }

    public static TicketFieldValueDto ToFieldValue(FieldValue value, FieldDescriptorDetailDto field)
    {
        var latest = LatestVerification(value);

        return new TicketFieldValueDto
        {
            Id = value.Id,
            FieldKey = value.FieldKey,
            Label = field.Label,
            DataType = field.DataType,

            // A MultipleChoice answer is a JSON array in value_text (§1.4.1). It is returned as a list
            // rather than as the raw JSON so no client has to know that storage detail; Text is left
            // null for that type so a client cannot read the array as a single answer.
            Text = field.DataType == FieldDataTypes.MultipleChoice ? null : value.ValueText,
            Choices = field.DataType == FieldDataTypes.MultipleChoice
                ? ParseChoices(value.ValueText)
                : null,
            Number = value.ValueNumber,
            Date = value.ValueDate,
            DateTo = value.ValueDateTo,
            Boolean = value.ValueBoolean,
            DocumentId = value.ValueDocumentId,
            IsCarriedForward = value.IsCarriedForward,
            CreatedAt = value.CreatedAt,
            Verifications =
            [
                .. value.Verifications
                    .OrderBy(row => row.VerifiedAt)
                    .Select(row => new FieldVerificationDto
                    {
                        Id = row.Id,
                        Outcome = row.Outcome,
                        RejectionReason = row.RejectionReason,
                        VerifiedByUserAccountId = row.VerifiedByUserAccountId,
                        VerifiedAt = row.VerifiedAt,
                    })
            ],
            LatestOutcome = latest?.Outcome,
        };
    }

    /// <summary>
    /// The effective verification state of a value. Verifications are append-only (§1.5), so a rejection
    /// followed by an acceptance leaves two rows and the LAST one is what counts. Ordered by
    /// <c>VerifiedAt</c>, not by insertion: a carried-forward acceptance keeps the ORIGINAL timestamp
    /// (§4.5 rule 4), which is deliberately older than the row it was written beside.
    /// </summary>
    public static FieldVerification? LatestVerification(FieldValue value) =>
        value.Verifications.OrderBy(row => row.VerifiedAt).LastOrDefault();

    /// <summary>
    /// The keys of required visible fields that have no usable answer in <paramref name="values"/>.
    /// Empty means the ticket may be submitted; anything else is the 422's field list.
    ///
    /// "Required visible" is a CONJUNCTION of three things (§6.4): <c>IsRequired</c>,
    /// <c>IsVisibleToCustomer</c>, and not hidden by conditional visibility. Drop the third and
    /// submission becomes impossible for every ticket type with a conditional field, naming a field the
    /// user cannot see. Drop the second and a Customer-side caller is asked for a field that is the
    /// Accountant's to fill (§4.2 rule 3).
    /// </summary>
    /// <param name="version">
    /// The COMPLETE frozen version, not one stripped for a Customer audience. The conditional-visibility
    /// evaluation below has to be able to see a controlling field that is Accountant-only, and a stripped
    /// list would make the field it controls look hidden.
    /// </param>
    public static IReadOnlyList<string> UnansweredRequiredVisibleFields(
        TicketTypeDetailDto version, IReadOnlyCollection<FieldValue> values)
    {
        var descriptors = version.Fields ?? [];
        var byKey = ValuesByKey(values);
        var missing = new List<string>();

        foreach (var field in descriptors)
        {
            if (!field.IsRequired || !field.IsVisibleToCustomer)
                continue;

            if (IsConditionallyHidden(field, descriptors, byKey))
                continue;

            if (!byKey.TryGetValue(field.Key, out var value) || IsEmpty(field.DataType, value))
                missing.Add(field.Key);
        }

        return missing;
    }

    /// <summary>
    /// The keys of required visible fields whose latest verification is missing or is a rejection.
    ///
    /// This is the gate on <c>InReview → Answered</c> and, separately, on <c>Answered → Closed</c> (§4.9
    /// rules 1 and 2). It is checked at BOTH, not only at Answered, because <c>Answered → InReview →
    /// Answered</c> can happen in between and a field can be rejected in that window.
    /// </summary>
    public static IReadOnlyList<string> UnverifiedRequiredVisibleFields(
        TicketTypeDetailDto version, IReadOnlyCollection<FieldValue> values)
    {
        var descriptors = version.Fields ?? [];
        var byKey = ValuesByKey(values);
        var outstanding = new List<string>();

        foreach (var field in descriptors)
        {
            if (!field.IsRequired || !field.IsVisibleToCustomer)
                continue;

            if (IsConditionallyHidden(field, descriptors, byKey))
                continue;

            // A required visible field with no value at all is outstanding here too. It cannot be
            // verified, so it can never satisfy this gate, and reporting it as "unverified" is more
            // useful than omitting it and leaving the close mysteriously blocked.
            if (!byKey.TryGetValue(field.Key, out var value))
            {
                outstanding.Add(field.Key);
                continue;
            }

            if (LatestVerification(value) is not { } latest || !latest.IsAccepted)
                outstanding.Add(field.Key);
        }

        return outstanding;
    }

    /// <summary>
    /// Last row wins on a duplicate key. <c>uq_field_values_revision_key</c> makes duplicates impossible
    /// within one revision, so this only matters if a caller passes values from two revisions -- which is
    /// itself a bug, and one that a <c>ToDictionary</c> throw would surface as a 500.
    /// </summary>
    private static Dictionary<string, FieldValue> ValuesByKey(IReadOnlyCollection<FieldValue> values)
    {
        var byKey = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        foreach (var value in values)
            byKey[value.FieldKey] = value;

        return byKey;
    }

    /// <summary>
    /// §6.4, evaluated over STORED values. Same three rules as the validator's private counterpart: one
    /// level deep, a condition on an unknown key is treated as hidden (fail closed -- the alternative
    /// requires a field whose applicability nothing can establish), and a MultipleChoice controller
    /// never matches.
    /// </summary>
    private static bool IsConditionallyHidden(
        FieldDescriptorDetailDto field,
        IReadOnlyList<FieldDescriptorDetailDto> descriptors,
        IReadOnlyDictionary<string, FieldValue> byKey)
    {
        var condition = field.ConditionalVisibility;
        if (condition is null || string.IsNullOrWhiteSpace(condition.FieldKey))
            return false;

        var controller = descriptors.FirstOrDefault(
            candidate => string.Equals(candidate.Key, condition.FieldKey, StringComparison.Ordinal));

        if (controller is null)
            return true;

        if (!byKey.TryGetValue(condition.FieldKey, out var controllingValue))
            return true;

        var actual = ComparableText(controller.DataType, controllingValue);
        return !string.Equals(actual, condition.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stored controlling value as a string. Invariant culture throughout: a condition on "1.5" must
    /// not depend on the server's locale deciding that means fifteen.
    /// </summary>
    private static string? ComparableText(string dataType, FieldValue value) => dataType switch
    {
        FieldDataTypes.SingleLineText or FieldDataTypes.MultiLineText or FieldDataTypes.SingleChoice =>
            value.ValueText,
        FieldDataTypes.WholeNumber or FieldDataTypes.DecimalNumber or FieldDataTypes.MoneyAmount =>
            value.ValueNumber?.ToString(CultureInfo.InvariantCulture),
        FieldDataTypes.Date or FieldDataTypes.DateRange =>
            value.ValueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        FieldDataTypes.YesNo => value.ValueBoolean?.ToString().ToLowerInvariant(),
        FieldDataTypes.FileUpload => value.ValueDocumentId?.ToString(),
        _ => null,
    };

    /// <summary>
    /// A stored row that carries no answer. A DateRange with one end is INCOMPLETE, not present -- the
    /// same reading the validator uses, so a value it accepted as present cannot read as missing here.
    /// </summary>
    private static bool IsEmpty(string dataType, FieldValue value) => dataType switch
    {
        FieldDataTypes.SingleLineText or FieldDataTypes.MultiLineText or FieldDataTypes.SingleChoice =>
            string.IsNullOrWhiteSpace(value.ValueText),
        FieldDataTypes.MultipleChoice => ParseChoices(value.ValueText) is not { Count: > 0 },
        FieldDataTypes.WholeNumber or FieldDataTypes.DecimalNumber or FieldDataTypes.MoneyAmount =>
            value.ValueNumber is null,
        FieldDataTypes.Date => value.ValueDate is null,
        FieldDataTypes.DateRange => value.ValueDate is null || value.ValueDateTo is null,
        FieldDataTypes.YesNo => value.ValueBoolean is null,
        FieldDataTypes.FileUpload => value.ValueDocumentId is null,
        _ => true,
    };

    /// <summary>
    /// The MultipleChoice carrier. Returns null rather than throwing on text that is not a JSON array:
    /// a malformed stored value must not turn a ticket read into a 500, and every write path serialises
    /// it with <c>JsonSerializer</c> so the only way to get one is a row written outside the
    /// application.
    /// </summary>
    public static List<string>? ParseChoices(string? valueText)
    {
        if (string.IsNullOrWhiteSpace(valueText))
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<string>>(valueText);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
