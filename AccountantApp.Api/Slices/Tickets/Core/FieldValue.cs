namespace AccountantApp.Api.Slices.Tickets.Core;

/// <summary>
/// One answer within one revision. 01-DomainModel.md section 3.
///
/// The value is held in TYPED columns rather than one string, because the domain model requires it be
/// stored "in a form that preserves the declared data type". Which column a given field uses is decided
/// by its descriptor's DataType, which lives in TicketTypes -- so neither this entity nor the database
/// can enforce that a WholeNumber landed in ValueNumber rather than ValueText. FieldValueValidation is
/// the only guard for THAT, which is why its default arm throws instead of storing an unvalidated value.
///
/// The database does enforce the weaker half, resolved from plan section 13 item 4: at most one of the
/// five primary carriers is populated (ck_field_values_one_carrier), and a DateRange's end implies its
/// start (ck_field_values_date_range). So a row can be the wrong column, but never TWO columns -- which
/// is the failure where every reader picks a different answer. Populate exactly one carrier per value,
/// plus ValueDateTo for a DateRange.
///
/// No Version property: append-only, section 9.7.
/// </summary>
public sealed class FieldValue
{
    public Guid Id { get; set; }
    public Guid TicketRevisionId { get; set; }

    /// <summary>
    /// The FieldDescriptor key this answers. A string, not a foreign key: descriptors are another
    /// slice's rows, and the key is what survives a version change.
    /// </summary>
    public string FieldKey { get; set; } = string.Empty;

    /// <summary>
    /// SingleLineText, MultiLineText, SingleChoice -- and MultipleChoice, which holds a JSON array of
    /// the chosen option values. MultipleChoice is the one place a value is not atomic.
    /// </summary>
    public string? ValueText { get; set; }

    /// <summary>
    /// WholeNumber, DecimalNumber, MoneyAmount. decimal, mapping to NUMERIC(18,4) -- never double or
    /// float. A binary float cannot represent 0.10 and this is an accounting application.
    /// </summary>
    public decimal? ValueNumber { get; set; }

    /// <summary>Date, and the FROM end of a DateRange.</summary>
    public DateOnly? ValueDate { get; set; }

    /// <summary>The TO end of a DateRange. There is no CHECK enforcing to &gt;= from; the validator is.</summary>
    public DateOnly? ValueDateTo { get; set; }

    /// <summary>YesNo.</summary>
    public bool? ValueBoolean { get; set; }

    /// <summary>
    /// FileUpload. No foreign key -- documents is another slice's table -- and the document MUST be
    /// verified to belong to this ticket before it is accepted, which is section 0.3 step 5's IDOR in
    /// a different disguise.
    /// </summary>
    public Guid? ValueDocumentId { get; set; }

    /// <summary>
    /// Whether this value was carried forward unchanged from the previous revision or newly entered
    /// in this one. Not cosmetic: it is what tells the Accountant which fields need attention.
    /// </summary>
    public bool IsCarriedForward { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<FieldVerification> Verifications { get; set; } = [];
}
