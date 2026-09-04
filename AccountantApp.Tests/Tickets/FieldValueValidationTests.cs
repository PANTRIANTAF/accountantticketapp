using System.Text.Json;
using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using static AccountantApp.Api.Slices.Tickets.Application.FieldValueValidation;
using static AccountantApp.Tests.Tickets.TicketsTestHarness;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// Field value validation, plan section 6. TicketTypes DEFINES the rules; this is the half that APPLIES
/// them, so these tests are the only place the applying half is checked at all.
/// </summary>
public sealed class FieldValueValidationTests
{
    private static readonly UserRole Customer = UserRole.CustomerAdmin;
    private static readonly UserRole Accountant = UserRole.AccountantAdmin;

    // --- Section 6.2: the descriptor rules ---

    [Fact]
    public void A_required_visible_field_that_is_missing_is_422_naming_the_field()
    {
        var type = TypeWith(Field("vat_number", FieldDataTypes.SingleLineText, isRequired: true));

        var exception = Assert.Throws<AppException>(
            () => Validate([], type, Customer, enforceRequired: true, Now));

        Assert.Equal(422, exception.StatusCode);

        // Naming the field is the point: a validation error the user cannot locate is not a validation
        // error.
        Assert.Contains("vat_number", exception.Message);
    }

    /// <summary>
    /// enforceRequired is false when saving a Draft. Requiring every field to save a work in progress
    /// makes the Draft status pointless.
    /// </summary>
    [Fact]
    public void A_required_field_may_be_missing_while_the_ticket_is_still_a_draft()
    {
        var type = TypeWith(Field("vat_number", FieldDataTypes.SingleLineText, isRequired: true));

        Assert.Empty(Validate([], type, Customer, enforceRequired: false, Now));
    }

    /// <summary>
    /// Section 6.2 rule 2. Silently dropping it makes a typo'd key look like a missing required field
    /// three screens away, and the user's answer is simply gone.
    /// </summary>
    [Fact]
    public void A_value_for_an_unknown_field_key_is_422()
    {
        var type = TypeWith(Field("known", FieldDataTypes.SingleLineText));

        var exception = Assert.Throws<AppException>(() => Validate(
            [new SubmittedFieldValue("mystery", Text: "x")], type, Customer, true, Now));

        Assert.Equal(422, exception.StatusCode);
        Assert.Contains("mystery", exception.Message);
    }

    /// <summary>uq_field_values_revision_key, caught before it becomes a 23505 at SaveChanges.</summary>
    [Fact]
    public void The_same_field_supplied_twice_is_422()
    {
        var type = TypeWith(Field("note", FieldDataTypes.SingleLineText));

        var exception = Assert.Throws<AppException>(() => Validate(
            [new SubmittedFieldValue("note", Text: "a"), new SubmittedFieldValue("note", Text: "b")],
            type, Customer, true, Now));

        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public void Text_length_bounds_are_enforced()
    {
        var type = TypeWith(Field("code", FieldDataTypes.SingleLineText,
            validation: new FieldValidationDto { MinLength = 3, MaxLength = 5 }));

        Assert.Equal(422, Throws(type, Customer, new SubmittedFieldValue("code", Text: "ab")).StatusCode);
        Assert.Equal(422, Throws(type, Customer, new SubmittedFieldValue("code", Text: "abcdef")).StatusCode);

        var accepted = Validate(
            [new SubmittedFieldValue("code", Text: "abcd")], type, Customer, true, Now);
        Assert.Equal("abcd", Assert.Single(accepted).ValueText);
    }

    [Fact]
    public void Numeric_bounds_are_enforced_and_a_whole_number_is_not_rounded()
    {
        var type = TypeWith(
            Field("count", FieldDataTypes.WholeNumber,
                validation: new FieldValidationDto { MinValue = 1, MaxValue = 10 }));

        Assert.Equal(422, Throws(type, Customer, new SubmittedFieldValue("count", Number: 0)).StatusCode);
        Assert.Equal(422, Throws(type, Customer, new SubmittedFieldValue("count", Number: 11)).StatusCode);

        // Rejected rather than rounded: rounding invents an answer the user did not give.
        Assert.Equal(422, Throws(type, Customer, new SubmittedFieldValue("count", Number: 2.5m)).StatusCode);

        Assert.Equal(3m, Assert.Single(
            Validate([new SubmittedFieldValue("count", Number: 3)], type, Customer, true, Now)).ValueNumber);
    }

    /// <summary>
    /// MoneyAmount is money and lands in value_number, which is NUMERIC(18,4) -- never a binary float,
    /// which cannot represent 0.10. This test can only prove the C# side keeps it exact; the column type
    /// itself needs real PostgreSQL, and the schema test covers it there.
    /// </summary>
    [Fact]
    public void A_money_amount_keeps_its_exact_decimal_value()
    {
        var type = TypeWith(Field("amount", FieldDataTypes.MoneyAmount));

        var row = Assert.Single(Validate(
            [new SubmittedFieldValue("amount", Number: 0.10m)], type, Customer, true, Now));

        Assert.Equal(0.10m, row.ValueNumber);
        Assert.Null(row.ValueText);
    }

    [Fact]
    public void Date_bounds_are_enforced()
    {
        var type = TypeWith(Field("on", FieldDataTypes.Date,
            validation: new FieldValidationDto
            {
                EarliestDate = new DateOnly(2026, 1, 1),
                LatestDate = new DateOnly(2026, 12, 31),
            }));

        Assert.Equal(422,
            Throws(type, Customer, new SubmittedFieldValue("on", Date: new DateOnly(2025, 12, 31)))
                .StatusCode);
        Assert.Equal(422,
            Throws(type, Customer, new SubmittedFieldValue("on", Date: new DateOnly(2027, 1, 1)))
                .StatusCode);

        var row = Assert.Single(Validate(
            [new SubmittedFieldValue("on", Date: new DateOnly(2026, 6, 1))], type, Customer, true, Now));
        Assert.Equal(new DateOnly(2026, 6, 1), row.ValueDate);
    }

    /// <summary>
    /// Section 6.2 rule 5. There is no CHECK constraint for this, so this is the only guard in the whole
    /// system.
    /// </summary>
    [Fact]
    public void A_date_range_needs_both_ends_and_must_not_run_backwards()
    {
        var type = TypeWith(Field("period", FieldDataTypes.DateRange, isRequired: true));

        Assert.Equal(422, Throws(type, Customer,
            new SubmittedFieldValue("period", Date: new DateOnly(2026, 3, 1))).StatusCode);

        Assert.Equal(422, Throws(type, Customer, new SubmittedFieldValue("period",
            Date: new DateOnly(2026, 3, 1), DateTo: new DateOnly(2026, 2, 1))).StatusCode);

        // Equal ends are fine: a one-day period is a period.
        var row = Assert.Single(Validate([new SubmittedFieldValue("period",
            Date: new DateOnly(2026, 3, 1), DateTo: new DateOnly(2026, 3, 1))], type, Customer, true, Now));
        Assert.Equal(row.ValueDate, row.ValueDateTo);
    }

    [Fact]
    public void A_single_choice_must_be_one_of_the_options()
    {
        var type = TypeWith(Field("shift", FieldDataTypes.SingleChoice,
            choices: [Option("Day"), Option("Night")]));

        Assert.Equal(422, Throws(type, Customer, new SubmittedFieldValue("shift", Text: "Evening"))
            .StatusCode);

        Assert.Equal("Night", Assert.Single(
            Validate([new SubmittedFieldValue("shift", Text: "Night")], type, Customer, true, Now))
            .ValueText);
    }

    /// <summary>
    /// MultipleChoice is the one place a stored value is not atomic: a JSON array in value_text. Every
    /// element is validated BEFORE serialisation, because once it is one string nothing downstream will
    /// re-check the parts.
    /// </summary>
    [Fact]
    public void Every_element_of_a_multiple_choice_is_validated_and_the_result_is_a_json_array()
    {
        var type = TypeWith(Field("days", FieldDataTypes.MultipleChoice,
            choices: [Option("Mon"), Option("Tue"), Option("Wed")]));

        // The FIRST element is valid and the second is not, which is the case a naive
        // "is the first one known" check passes.
        Assert.Equal(422, Throws(type, Customer,
            new SubmittedFieldValue("days", Choices: ["Mon", "Sun"])).StatusCode);

        var row = Assert.Single(Validate(
            [new SubmittedFieldValue("days", Choices: ["Mon", "Wed"])], type, Customer, true, Now));

        Assert.Equal(
            new List<string> { "Mon", "Wed" },
            JsonSerializer.Deserialize<List<string>>(row.ValueText!));
    }

    [Fact]
    public void A_yes_no_field_lands_in_value_boolean()
    {
        var type = TypeWith(Field("agreed", FieldDataTypes.YesNo));

        var row = Assert.Single(Validate(
            [new SubmittedFieldValue("agreed", Boolean: false)], type, Customer, true, Now));

        // False is a VALUE, not an absence. A required YesNo answered "no" must not read as missing.
        Assert.False(row.ValueBoolean);
        Assert.Null(row.ValueText);
    }

    [Fact]
    public void A_required_yes_no_answered_no_satisfies_the_requirement()
    {
        var type = TypeWith(Field("agreed", FieldDataTypes.YesNo, isRequired: true));

        Assert.Single(Validate(
            [new SubmittedFieldValue("agreed", Boolean: false)], type, Customer, true, Now));
    }

    // --- Section 6.2 rule 3: the regex timeout ---

    /// <summary>
    /// The pattern is authored by an Accountant; the value it runs against arrives from a Customer over
    /// the internet, so a catastrophically backtracking pattern is a denial of service against the whole
    /// worker process. It must come back as a 422 naming the field, NOT as a 500 and not as a hung
    /// request.
    ///
    /// The pattern below is the classic exponential case; the value is long enough that matching it
    /// cannot finish inside the shared 100 ms budget.
    /// </summary>
    [Fact]
    public void A_catastrophically_backtracking_pattern_times_out_as_a_422()
    {
        var type = TypeWith(Field("pattern_field", FieldDataTypes.SingleLineText,
            validation: new FieldValidationDto { RegexPattern = "^(a+)+$" }));

        var exception = Throws(type, Customer,
            new SubmittedFieldValue("pattern_field", Text: new string('a', 40) + "!"));

        Assert.Equal(422, exception.StatusCode);
        Assert.Contains("pattern_field", exception.Message);
    }

    [Fact]
    public void A_value_that_does_not_match_its_pattern_is_422_and_one_that_does_is_accepted()
    {
        var type = TypeWith(Field("vat", FieldDataTypes.SingleLineText,
            validation: new FieldValidationDto { RegexPattern = "^EL[0-9]{9}$" }));

        Assert.Equal(422, Throws(type, Customer, new SubmittedFieldValue("vat", Text: "GR123")).StatusCode);

        Assert.Equal("EL123456789", Assert.Single(
            Validate([new SubmittedFieldValue("vat", Text: "EL123456789")], type, Customer, true, Now))
            .ValueText);
    }

    // --- Section 6.3: the Accountant-only split ---

    /// <summary>
    /// Section 6.3 rule 1 and section 9.4. There must be NO code path by which an Accountant's identity
    /// attaches to a Customer-supplied FieldValue, and 403 rather than 422 because the value may be
    /// perfectly valid -- the caller simply may not supply it.
    /// </summary>
    [Fact]
    public void An_Accountant_supplying_a_customer_visible_field_is_403()
    {
        var type = TypeWith(Field("vat", FieldDataTypes.SingleLineText, isVisibleToCustomer: true));

        var exception = Throws(type, Accountant, new SubmittedFieldValue("vat", Text: "EL1"));

        Assert.Equal(403, exception.StatusCode);
    }

    [Fact]
    public void A_customer_side_caller_supplying_an_accountant_only_field_is_403()
    {
        var type = TypeWith(Field("internal_ref", FieldDataTypes.SingleLineText, isVisibleToCustomer: false));

        var exception = Throws(type, Customer, new SubmittedFieldValue("internal_ref", Text: "X"));

        Assert.Equal(403, exception.StatusCode);
    }

    /// <summary>
    /// The two halves are DISJOINT, which is why "an Accountant may edit Accountant-only fields" and "an
    /// Accountant may not edit a Field Value" do not conflict. An Accountant writing an Accountant-only
    /// field succeeds.
    /// </summary>
    [Fact]
    public void An_Accountant_may_write_an_accountant_only_field()
    {
        var type = TypeWith(Field("internal_ref", FieldDataTypes.SingleLineText, isVisibleToCustomer: false));

        var row = Assert.Single(Validate(
            [new SubmittedFieldValue("internal_ref", Text: "REF-9")], type, Accountant, true, Now));

        Assert.Equal("REF-9", row.ValueText);
    }

    /// <summary>
    /// Section 6.3 rule 3 / section 4.2 rule 3. An Accountant-only field is NEVER required for a
    /// Customer-side submission -- otherwise no Customer could ever submit a ticket whose type has one.
    /// </summary>
    [Fact]
    public void An_accountant_only_field_is_never_required_of_a_customer_side_submission()
    {
        var type = TypeWith(
            Field("vat", FieldDataTypes.SingleLineText, isRequired: true),
            Field("internal_ref", FieldDataTypes.SingleLineText, isRequired: true,
                isVisibleToCustomer: false));

        var rows = Validate(
            [new SubmittedFieldValue("vat", Text: "EL1")], type, Customer, enforceRequired: true, Now);

        Assert.Equal("vat", Assert.Single(rows).FieldKey);
    }

    /// <summary>And symmetrically: a Customer field is not required of an Accountant.</summary>
    [Fact]
    public void A_customer_visible_field_is_never_required_of_an_Accountant()
    {
        var type = TypeWith(Field("vat", FieldDataTypes.SingleLineText, isRequired: true));

        Assert.Empty(Validate([], type, Accountant, enforceRequired: true, Now));
    }

    // --- Section 6.4: "required visible" means two things at once ---

    /// <summary>
    /// THE BUG SECTION 6.4 EXISTS TO PREVENT. A required field hidden by conditional visibility IS NOT
    /// REQUIRED. Miss this and submission is impossible for any ticket type using a conditional field,
    /// with a 422 naming a field the user cannot see and cannot fill -- reported as "the app is broken"
    /// rather than as a validation error.
    /// </summary>
    [Fact]
    public void A_required_field_hidden_by_conditional_visibility_is_not_required()
    {
        var type = ConditionalType();

        var rows = Validate(
            [new SubmittedFieldValue("has_children", Boolean: false)],
            type, Customer, enforceRequired: true, Now);

        Assert.Equal("has_children", Assert.Single(rows).FieldKey);
    }

    /// <summary>
    /// And when the condition IS met the field becomes required again. Without this half, the test above
    /// passes against an implementation that simply never enforces the requirement.
    /// </summary>
    [Fact]
    public void The_same_field_is_required_once_the_condition_is_met()
    {
        var type = ConditionalType();

        var exception = Assert.Throws<AppException>(() => Validate(
            [new SubmittedFieldValue("has_children", Boolean: true)],
            type, Customer, enforceRequired: true, Now));

        Assert.Equal(422, exception.StatusCode);
        Assert.Contains("child_count", exception.Message);
    }

    /// <summary>
    /// Section 6.4 rule 2. Stored-but-ignored leaves a value that silently reappears if the condition
    /// later flips, answering a question the user was never shown.
    /// </summary>
    [Fact]
    public void A_value_supplied_for_a_conditionally_hidden_field_is_422()
    {
        var type = ConditionalType();

        var exception = Assert.Throws<AppException>(() => Validate(
            [
                new SubmittedFieldValue("has_children", Boolean: false),
                new SubmittedFieldValue("child_count", Number: 2),
            ],
            type, Customer, enforceRequired: true, Now));

        Assert.Equal(422, exception.StatusCode);
        Assert.Contains("child_count", exception.Message);
    }

    /// <summary>
    /// Section 6.4 rule 1: the condition is evaluated against the values in the revision BEING SUBMITTED.
    /// A controlling field absent from this submission leaves the dependent field hidden -- fail closed,
    /// so it is neither required nor writable.
    /// </summary>
    [Fact]
    public void A_condition_whose_controlling_field_is_absent_leaves_the_field_hidden()
    {
        var type = ConditionalType();

        Assert.Empty(Validate([], type, Customer, enforceRequired: true, Now));
    }

    /// <summary>
    /// A condition pointing at a key that is not in this version is treated as hidden, not as visible.
    /// The alternative would show -- and could require -- a field whose applicability nothing can
    /// establish.
    /// </summary>
    [Fact]
    public void A_condition_on_a_nonexistent_field_key_leaves_the_field_hidden()
    {
        var type = TypeWith(Field("orphan", FieldDataTypes.SingleLineText, isRequired: true,
            conditional: new ConditionalVisibilityDto { FieldKey = "not_here", Value = "true" }));

        Assert.Empty(Validate([], type, Customer, enforceRequired: true, Now));
    }

    // --- FileUpload ---

    /// <summary>
    /// The dictionary contains only documents the caller has already verified to belong to THIS ticket, so
    /// a document from another ticket is simply absent -- and rejected. Same message either way: the
    /// difference between "does not exist" and "belongs to another ticket" would enumerate other
    /// Customers' document ids.
    /// </summary>
    [Fact]
    public void A_file_upload_naming_a_document_not_on_this_ticket_is_422()
    {
        var type = TypeWith(Field("payslip", FieldDataTypes.FileUpload));

        var exception = Assert.Throws<AppException>(() => Validate(
            [new SubmittedFieldValue("payslip", DocumentId: Guid.NewGuid())],
            type, Customer, true, Now, documents: new Dictionary<Guid, DocumentSummary>()));

        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public void File_size_and_extension_limits_are_enforced_against_the_document_summary()
    {
        var type = TypeWith(Field("payslip", FieldDataTypes.FileUpload,
            validation: new FieldValidationDto
            {
                MaxFileSizeBytes = 1000,
                AllowedFileTypes = ["pdf", "jpg"],
            }));

        var tooBig = Document("payslip.pdf", 2000);
        Assert.Equal(422, Assert.Throws<AppException>(() => Validate(
            [new SubmittedFieldValue("payslip", DocumentId: tooBig.Id)],
            type, Customer, true, Now, Lookup(tooBig))).StatusCode);

        var wrongType = Document("payslip.exe", 100);
        Assert.Equal(422, Assert.Throws<AppException>(() => Validate(
            [new SubmittedFieldValue("payslip", DocumentId: wrongType.Id)],
            type, Customer, true, Now, Lookup(wrongType))).StatusCode);

        // Upper case and a dotted entry both match: a descriptor author writes both forms and neither is
        // wrong.
        var accepted = Document("PAYSLIP.PDF", 100);
        var row = Assert.Single(Validate(
            [new SubmittedFieldValue("payslip", DocumentId: accepted.Id)],
            type, Customer, true, Now, Lookup(accepted)));

        Assert.Equal(accepted.Id, row.ValueDocumentId);
    }

    // --- Carried-forward values ---

    /// <summary>
    /// 01-DomainModel.md section 3: a revision records whether each value was carried forward unchanged
    /// or newly entered. The validator preserves the flag rather than deciding it -- only the correction
    /// handler knows what the previous revision held.
    /// </summary>
    [Fact]
    public void The_carried_forward_flag_is_preserved()
    {
        var type = TypeWith(
            Field("a", FieldDataTypes.SingleLineText),
            Field("b", FieldDataTypes.SingleLineText));

        var rows = Validate(
            [
                new SubmittedFieldValue("a", Text: "old", IsCarriedForward: true),
                new SubmittedFieldValue("b", Text: "new"),
            ],
            type, Customer, true, Now);

        Assert.True(rows.Single(row => row.FieldKey == "a").IsCarriedForward);
        Assert.False(rows.Single(row => row.FieldKey == "b").IsCarriedForward);
    }

    // --- helpers ---

    private static AppException Throws(
        TicketTypeDetailDto type, UserRole role, SubmittedFieldValue value) =>
        Assert.Throws<AppException>(() => Validate([value], type, role, true, Now));

    private static ChoiceOptionDto Option(string value) => new() { Label = value, Value = value };

    /// <summary>child_count is required, but only when has_children is true.</summary>
    private static TicketTypeDetailDto ConditionalType() => TypeWith(
        Field("has_children", FieldDataTypes.YesNo),
        Field("child_count", FieldDataTypes.WholeNumber, isRequired: true,
            conditional: new ConditionalVisibilityDto { FieldKey = "has_children", Value = "true" }));

    private static DocumentSummary Document(string fileName, long sizeBytes) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "CustomerUpload", fileName,
        "application/octet-stream", sizeBytes, Guid.NewGuid(), Now);

    private static Dictionary<Guid, DocumentSummary> Lookup(DocumentSummary document) =>
        new() { [document.Id] = document };
}
