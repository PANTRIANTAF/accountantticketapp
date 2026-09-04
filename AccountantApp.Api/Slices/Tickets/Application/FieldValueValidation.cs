using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Shared.Validation;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Tickets.Application;

/// <summary>
/// Validates submitted field values against another slice's descriptors, and turns them into
/// <see cref="FieldValue"/> rows. Section 6.
///
/// THE SPLIT: TicketTypes DEFINES the rules; Tickets APPLIES them to a submitted value. Neither half
/// works alone, and this is the applying half. It must not drift into defining -- no rule here that
/// isn't in the descriptor.
///
/// It imports TicketTypes.ExternalInterfaces ONLY. Never TicketTypes.Application.Dtos (request DTOs,
/// and under Application) and never TicketTypes.Core. Dependency rule 2.
///
/// Every failure is a 422 naming the field key, except the two authorization failures in section 6.3,
/// which are 403. A validation error the user cannot locate is not a validation error.
/// </summary>
public static class FieldValueValidation
{
    /// <summary>
    /// One submitted answer, in typed form. Deliberately defined HERE and not taken as a request DTO:
    /// this is the shape the validator needs, several request DTOs will map onto it, and a request DTO
    /// in the signature would drag transport concerns (string parsing, JSON binding) into the rules.
    ///
    /// Exactly one carrier should be populated for a given data type -- see <see cref="Validate"/>. A
    /// second populated carrier is not an error here; the validator reads only the one the descriptor's
    /// DataType selects, so a stray value is dropped rather than smuggled into a column it does not
    /// belong in.
    /// </summary>
    public sealed record SubmittedFieldValue(
        string FieldKey,
        string? Text = null,
        decimal? Number = null,
        DateOnly? Date = null,
        DateOnly? DateTo = null,
        bool? Boolean = null,
        Guid? DocumentId = null,
        IReadOnlyList<string>? Choices = null,
        bool IsCarriedForward = false);

    // The data type names come from TicketTypes.ExternalInterfaces.FieldDataTypes -- ONE definition,
    // shared. This file used to declare its own nested `DataTypes` class with the same eleven string
    // literals, because the names then lived in TicketTypes.Core where dependency rule 2 puts them out
    // of reach. Eleven duplicated literals with nothing keeping them in sync is precisely the drift
    // that rule exists to prevent, so the constants moved to the contract folder instead. The default
    // arm of each switch below still throws on an unrecognised type, so a twelfth type added to
    // FieldDataTypes fails loudly here rather than being stored unvalidated.

    /// <summary>
    /// Validates <paramref name="submitted"/> against <paramref name="version"/>'s descriptors and
    /// returns the FieldValue rows to persist, in descriptor order.
    ///
    /// Nothing is written to a context here and no id is assigned to a revision: the caller creates the
    /// TicketRevision, then assigns TicketRevisionId on each returned row. Keeping this pure is what
    /// lets the whole of section 6 be tested without a database.
    /// </summary>
    /// <param name="version">
    /// The FROZEN version the ticket stores, resolved with
    /// ITicketTypesApi.GetVersionByIdAsync(ticket.TicketTypeVersionId, ...). Never the type's current
    /// version: a later version must not change what an existing ticket asked for, and validating a
    /// correction against a newer descriptor set can reject a value that was valid when it was given.
    /// </param>
    /// <param name="callerRole">
    /// Decides which HALF of the fields the caller may write, per section 6.3. The two halves are
    /// disjoint: the Customer side writes the IsVisibleToCustomer fields, an Accountant writes the
    /// others. There is no field both may write.
    /// </param>
    /// <param name="enforceRequired">
    /// False for saving a Draft, true for a submission. A Draft is a work in progress; requiring every
    /// field to save one makes the Draft status pointless. Section 6.4 then narrows "required" further:
    /// a conditionally hidden field is not required even when this is true.
    /// </param>
    /// <param name="documents">
    /// For FileUpload fields. MUST contain only documents already verified to belong to THIS ticket --
    /// the caller does that check (section 4.5 rule 6, the same IDOR as section 0.3 step 5). A document
    /// id absent from this dictionary is rejected, so passing an unfiltered lookup here would defeat
    /// the check entirely.
    /// </param>
    public static IReadOnlyList<FieldValue> Validate(
        IReadOnlyCollection<SubmittedFieldValue> submitted,
        TicketTypeDetailDto version,
        UserRole callerRole,
        bool enforceRequired,
        DateTimeOffset now,
        IReadOnlyDictionary<Guid, DocumentSummary>? documents = null)
    {
        var descriptors = version.Fields ?? [];
        var isAccountant = callerRole is UserRole.AccountantAdmin or UserRole.AccountantUser;

        // A duplicate key would violate uq_field_values_revision_key at SaveChanges, i.e. a 500 for
        // what is a bad request. Rejected here, named.
        var duplicate = submitted.GroupBy(value => value.FieldKey, StringComparer.Ordinal)
                                 .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new AppException(
                $"Field '{duplicate.Key}' was supplied more than once. A revision holds one answer "
                + "per field.", 422);

        var byKey = submitted.ToDictionary(value => value.FieldKey, StringComparer.Ordinal);

        // Rule 2 of section 6.2: a value for a field_key not in the version's descriptors is 422, NOT
        // silently dropped. Dropping it makes a typo'd key look like a missing required field three
        // screens away, and the user's answer is simply gone.
        var known = descriptors.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        var unknown = submitted.FirstOrDefault(value => !known.Contains(value.FieldKey));
        if (unknown is not null)
            throw new AppException(
                $"Field '{unknown.FieldKey}' is not part of this ticket type.", 422);

        var results = new List<FieldValue>();

        foreach (var field in descriptors)
        {
            var hasValue = byKey.TryGetValue(field.Key, out var value);

            // SECTION 6.3, and it looks like a contradiction until you see the two sets are disjoint.
            // "An Accountant may edit Accountant-only fields" and "an Accountant may not edit a Field
            // Value" are both true, about different fields. The split is by IsVisibleToCustomer, the
            // shipped descriptor property; there is no IsAccountantOnly flag and adding one would give
            // the system two answers.
            var callerMayWrite = isAccountant ? !field.IsVisibleToCustomer : field.IsVisibleToCustomer;

            if (hasValue && !callerMayWrite)
                // 403, not 422: the value may be perfectly valid, the caller simply may not supply it.
                // There must be NO code path by which an Accountant's identity attaches to a
                // Customer-supplied FieldValue (section 9.4), and this is that path being closed.
                throw new AppException(
                    isAccountant
                        ? $"Field '{field.Key}' is a Customer field. An Accountant may verify it but "
                          + "never supply its value."
                        : $"Field '{field.Key}' does not exist on this ticket type.",
                    403);

            // Note the asymmetric wording above. Telling a Customer-side caller that an Accountant-only
            // field exists leaks the descriptor they must never see (section 4.3 rule 5: absent from
            // every response they receive -- not nulled, absent). The status is still 403 because that
            // is what section 6.3 rule 2 specifies.

            var isHidden = IsConditionallyHidden(field, descriptors, byKey);

            if (isHidden)
            {
                // Section 6.4 rule 2: a value supplied for a hidden field is 422 and is NOT written.
                // Stored-but-ignored leaves a value that silently reappears if the condition later
                // flips, answering a question the user was never shown.
                if (hasValue)
                    throw new AppException(
                        $"Field '{field.Key}' is not applicable given the other answers and cannot be "
                        + "supplied.", 422);

                continue;
            }

            if (!hasValue || IsEmpty(field.DataType, value!))
            {
                // SECTION 6.4. "Required visible fields" is a conjunction, and a required field that is
                // conditionally hidden IS NOT REQUIRED -- the hidden branch above has already skipped
                // those. Miss that and submission becomes impossible for any ticket type using a
                // conditional field, with a 422 naming a field the user cannot see and cannot fill.
                //
                // callerMayWrite carries the other half: Accountant-only fields are never required for
                // a Customer-side submission (section 4.2 rule 3), and Customer fields are never
                // required of an Accountant.
                if (enforceRequired && field.IsRequired && callerMayWrite)
                    throw new AppException($"Field '{field.Key}' is required.", 422);

                continue;
            }

            results.Add(Build(field, value!, now, documents));
        }

        return results;
    }

    /// <summary>
    /// Section 6.4. Hidden when the descriptor names a controlling field and that field's value in THIS
    /// submission does not equal the expected one.
    ///
    /// Evaluated against the values in the revision being submitted, not the previous one (rule 1): the
    /// answer that switches a conditional field on is usually given in the same request.
    ///
    /// One level deep only (rule 3), because the shipped descriptor carries a single key and value. Do
    /// not build a condition tree here -- a chain (field C visible only when B is visible and set) is a
    /// TicketTypes change to raise, not something to infer.
    /// </summary>
    private static bool IsConditionallyHidden(
        FieldDescriptorDetailDto field,
        IReadOnlyList<FieldDescriptorDetailDto> descriptors,
        IReadOnlyDictionary<string, SubmittedFieldValue> byKey)
    {
        var condition = field.ConditionalVisibility;
        if (condition is null || string.IsNullOrWhiteSpace(condition.FieldKey))
            return false;

        var controller = descriptors.FirstOrDefault(
            candidate => string.Equals(candidate.Key, condition.FieldKey, StringComparison.Ordinal));

        // A condition pointing at a key that is not in this version is treated as HIDDEN, not as
        // visible. Fail closed: the alternative shows -- and can require -- a field whose applicability
        // nothing can establish.
        if (controller is null)
            return true;

        if (!byKey.TryGetValue(condition.FieldKey, out var controllingValue))
            return true;

        var actual = ComparableText(controller.DataType, controllingValue);
        return !string.Equals(actual, condition.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The controlling value as a string, for comparison with ConditionalVisibilityValue -- which is
    /// stored as a string whatever the controlled field's type. Invariant culture throughout: a
    /// condition on "1.5" must not depend on the server's locale deciding that means fifteen.
    /// </summary>
    private static string? ComparableText(string dataType, SubmittedFieldValue value) => dataType switch
    {
        FieldDataTypes.SingleLineText or FieldDataTypes.MultiLineText or FieldDataTypes.SingleChoice => value.Text,
        FieldDataTypes.WholeNumber or FieldDataTypes.DecimalNumber or FieldDataTypes.MoneyAmount =>
            value.Number?.ToString(CultureInfo.InvariantCulture),
        FieldDataTypes.Date or FieldDataTypes.DateRange => value.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        // Lower-cased, matching JSON and the way a descriptor author writes it in the UI.
        FieldDataTypes.YesNo => value.Boolean?.ToString().ToLowerInvariant(),
        FieldDataTypes.FileUpload => value.DocumentId?.ToString(),

        // A MultipleChoice field is not usable as a condition source: "equals one string" has no
        // meaning for a set, and guessing (any? all? the JSON text?) would give three defensible
        // answers. Treated as never matching, so the dependent field stays hidden.
        FieldDataTypes.MultipleChoice => null,
        _ => null,
    };

    private static bool IsEmpty(string dataType, SubmittedFieldValue value) => dataType switch
    {
        FieldDataTypes.SingleLineText or FieldDataTypes.MultiLineText or FieldDataTypes.SingleChoice =>
            string.IsNullOrWhiteSpace(value.Text),
        FieldDataTypes.WholeNumber or FieldDataTypes.DecimalNumber or FieldDataTypes.MoneyAmount => value.Number is null,

        // A DateRange with only one end is INCOMPLETE, not present. Reported as required-missing rather
        // than as a range error, which is what the user actually has to fix.
        FieldDataTypes.Date => value.Date is null,
        FieldDataTypes.DateRange => value.Date is null && value.DateTo is null,
        FieldDataTypes.YesNo => value.Boolean is null,
        FieldDataTypes.MultipleChoice => value.Choices is null || value.Choices.Count == 0,
        FieldDataTypes.FileUpload => value.DocumentId is null,
        _ => true,
    };

    private static FieldValue Build(
        FieldDescriptorDetailDto field,
        SubmittedFieldValue value,
        DateTimeOffset now,
        IReadOnlyDictionary<Guid, DocumentSummary>? documents)
    {
        var validation = field.Validation;

        var row = new FieldValue
        {
            Id = Guid.NewGuid(),
            FieldKey = field.Key,
            IsCarriedForward = value.IsCarriedForward,
            CreatedAt = now,
        };

        // The value goes in the column its declared type selects -- section 1.4.1. One TEXT column
        // would not preserve the type, it would defer the question to every reader, and the readers
        // would disagree: one parses "1.500" as fifteen hundred and another as one and a half.
        switch (field.DataType)
        {
            case FieldDataTypes.SingleLineText:
            case FieldDataTypes.MultiLineText:
                row.ValueText = ValidateText(field, validation, value.Text!);
                break;

            case FieldDataTypes.SingleChoice:
                row.ValueText = ValidateChoice(field, value.Text!);
                break;

            case FieldDataTypes.MultipleChoice:
                // The one place a stored value is not atomic: a JSON array in value_text. Every element
                // is validated against ChoiceOptions BEFORE it is serialised, because once it is one
                // string nothing downstream will re-check the parts.
                foreach (var choice in value.Choices!)
                    ValidateChoice(field, choice);

                row.ValueText = JsonSerializer.Serialize(value.Choices);
                break;

            case FieldDataTypes.WholeNumber:
                // Rejected rather than rounded. Rounding invents an answer the user did not give.
                if (decimal.Truncate(value.Number!.Value) != value.Number.Value)
                    throw new AppException($"Field '{field.Key}' must be a whole number.", 422);

                row.ValueNumber = ValidateNumber(field, validation, value.Number.Value);
                break;

            case FieldDataTypes.DecimalNumber:
            case FieldDataTypes.MoneyAmount:
                // decimal, mapped to NUMERIC(18,4) -- never float/double/real. MoneyAmount is money, a
                // binary float cannot represent 0.10, and this is an accounting application: a rounding
                // artefact in a tax figure is the worst class of bug this codebase can produce.
                row.ValueNumber = ValidateNumber(field, validation, value.Number!.Value);
                break;

            case FieldDataTypes.Date:
                row.ValueDate = ValidateDate(field, validation, value.Date!.Value);
                break;

            case FieldDataTypes.DateRange:
                // Section 6.2 rule 5. There is no CHECK constraint for this (section 1.4 rule 3), so
                // this is the ONLY guard: both ends present, and the end not before the start.
                if (value.Date is null || value.DateTo is null)
                    throw new AppException(
                        $"Field '{field.Key}' needs both a start and an end date.", 422);

                if (value.DateTo.Value < value.Date.Value)
                    throw new AppException(
                        $"Field '{field.Key}': the end date cannot be before the start date.", 422);

                row.ValueDate = ValidateDate(field, validation, value.Date.Value);
                row.ValueDateTo = ValidateDate(field, validation, value.DateTo.Value);
                break;

            case FieldDataTypes.YesNo:
                row.ValueBoolean = value.Boolean!.Value;
                break;

            case FieldDataTypes.FileUpload:
                row.ValueDocumentId = ValidateDocument(field, validation, value.DocumentId!.Value, documents);
                break;

            default:
                // Not an AppException: there is no status code that helps here and no input the user
                // could change. A descriptor whose DataType this slice does not understand cannot be
                // validated, and accepting the value unvalidated is worse than failing -- it stores an
                // answer nothing has ever checked. It is a bug in TicketTypes or a twelfth data type
                // added without updating this switch.
                throw new InvalidOperationException(
                    $"Field '{field.Key}' declares unsupported data type '{field.DataType}'.");
        }

        return row;
    }

    private static string ValidateText(
        FieldDescriptorDetailDto field, FieldValidationDto? validation, string text)
    {
        if (validation is not null)
        {
            if (validation.MinLength is { } min && text.Length < min)
                throw new AppException(
                    $"Field '{field.Key}' must be at least {min} characters.", 422);

            if (validation.MaxLength is { } max && text.Length > max)
                throw new AppException(
                    $"Field '{field.Key}' must be at most {max} characters.", 422);

            ValidatePattern(field, validation.RegexPattern, text);
        }

        return text;
    }

    private static void ValidatePattern(
        FieldDescriptorDetailDto field, string? pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern))
            return;

        try
        {
            // The pattern is authored by an Accountant; the text it runs against arrives from a
            // Customer over the internet. A catastrophically backtracking pattern is therefore a denial
            // of service against the whole worker process, not just this request. The timeout is the
            // mechanism.
            //
            // The timeout is the same constant TicketTypes compiles stored patterns with, so one
            // pattern cannot be accepted under one budget and run under another -- two timeouts for one
            // pattern is a bug waiting for a pattern that sits between them (section 6.2 rule 3). It
            // lives in Shared/Validation because both slices need it; see that file for why it is not
            // borrowed from TicketTypes.Application any more, and for why NonBacktracking is not a
            // substitute.
            var regex = new Regex(pattern, RegexOptions.None, UserSuppliedRegex.MatchTimeout);

            if (!regex.IsMatch(text))
                throw new AppException($"Field '{field.Key}' is not in the expected format.", 422);
        }
        catch (RegexMatchTimeoutException)
        {
            // 422 naming the field, never a 500 and never a hung request. The value is rejected because
            // it could not be checked, which is the fail-closed answer.
            throw new AppException(
                $"Field '{field.Key}' could not be validated against its pattern in time. Shorten the "
                + "value or contact the Office.", 422);
        }
        catch (ArgumentException)
        {
            // A stored pattern that no longer compiles is a TicketTypes data problem, not a bad request
            // -- TicketTypeMapper compiles every pattern when the descriptor is authored, so reaching
            // here means the row was written some other way.
            throw new InvalidOperationException(
                $"Field '{field.Key}' has a stored regex pattern that does not compile.");
        }
    }

    private static string ValidateChoice(FieldDescriptorDetailDto field, string chosen)
    {
        var options = field.ChoiceOptions ?? [];
        if (!options.Any(option => string.Equals(option.Value, chosen, StringComparison.Ordinal)))
            throw new AppException(
                $"Field '{field.Key}': '{chosen}' is not one of the available options.", 422);

        return chosen;
    }

    private static decimal ValidateNumber(
        FieldDescriptorDetailDto field, FieldValidationDto? validation, decimal number)
    {
        if (validation is not null)
        {
            if (validation.MinValue is { } min && number < min)
                throw new AppException($"Field '{field.Key}' must be at least {min}.", 422);

            if (validation.MaxValue is { } max && number > max)
                throw new AppException($"Field '{field.Key}' must be at most {max}.", 422);
        }

        return number;
    }

    private static DateOnly ValidateDate(
        FieldDescriptorDetailDto field, FieldValidationDto? validation, DateOnly date)
    {
        if (validation is not null)
        {
            if (validation.EarliestDate is { } earliest && date < earliest)
                throw new AppException(
                    $"Field '{field.Key}' cannot be earlier than {earliest:yyyy-MM-dd}.", 422);

            if (validation.LatestDate is { } latest && date > latest)
                throw new AppException(
                    $"Field '{field.Key}' cannot be later than {latest:yyyy-MM-dd}.", 422);
        }

        return date;
    }

    private static Guid ValidateDocument(
        FieldDescriptorDetailDto field,
        FieldValidationDto? validation,
        Guid documentId,
        IReadOnlyDictionary<Guid, DocumentSummary>? documents)
    {
        // Absent from the dictionary means "not a document of this ticket", because the caller is
        // required to have filtered it to this ticket's documents. Rejected, and deliberately with the
        // same message whether the document does not exist or belongs to another ticket -- otherwise
        // the difference between the two answers enumerates other Customers' document ids.
        if (documents is null || !documents.TryGetValue(documentId, out var document))
            throw new AppException(
                $"Field '{field.Key}': the attached document was not found on this ticket.", 422);

        if (validation is null)
            return documentId;

        if (validation.MaxFileSizeBytes is { } maxBytes && document.SizeBytes > maxBytes)
            throw new AppException(
                $"Field '{field.Key}': the attached file exceeds the maximum size of {maxBytes} bytes.",
                422);

        var allowed = validation.AllowedFileTypes ?? [];
        if (allowed.Count > 0)
        {
            // AllowedFileTypes holds EXTENSIONS, not MIME types -- TicketTypes stores them as
            // "pdf,jpg,png". Compared case-insensitively and with any leading dot tolerated on either
            // side, because a descriptor author writes both forms and neither is wrong.
            var extension = Path.GetExtension(document.OriginalFileName).TrimStart('.');
            var matches = allowed.Any(entry => string.Equals(
                entry.TrimStart('.'), extension, StringComparison.OrdinalIgnoreCase));

            if (!matches)
                throw new AppException(
                    $"Field '{field.Key}': only {string.Join(", ", allowed)} files are accepted.", 422);
        }

        return documentId;
    }
}
