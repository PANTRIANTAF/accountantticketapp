namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// One answer supplied by a caller, in the typed shape FieldValueValidation expects.
///
/// One input shape for all eleven data types, with one nullable carrier each, because the caller does
/// not know which column the value lands in -- the descriptor does. The handler maps this onto
/// <see cref="Application.FieldValueValidation.SubmittedFieldValue"/> and lets the validator decide
/// which carrier is legal for the declared type; supplying the wrong one is a 422, never a stored row.
///
/// NOTE WHAT IS ABSENT: <c>IsCarriedForward</c>. Whether a value was carried forward from the previous
/// revision is a server observation, not a client claim -- it is what tells the Accountant which fields
/// need attention (plan §4.5 rule 3), so a client that could set it could hide a changed answer behind
/// a "nothing to see here" flag. SubmitRevisionHandler sets it.
/// </summary>
public class TicketFieldValueInputDto
{
    public string FieldKey { get; set; } = string.Empty;

    /// <summary>SingleLineText, MultiLineText, SingleChoice.</summary>
    public string? Text { get; set; }

    /// <summary>WholeNumber, DecimalNumber, MoneyAmount. decimal, never double -- this is accounting.</summary>
    public decimal? Number { get; set; }

    /// <summary>Date, and the FROM end of a DateRange.</summary>
    public DateOnly? Date { get; set; }

    /// <summary>The TO end of a DateRange.</summary>
    public DateOnly? DateTo { get; set; }

    /// <summary>YesNo.</summary>
    public bool? Boolean { get; set; }

    /// <summary>FileUpload. The document must already belong to this ticket (plan §0.3 step 5).</summary>
    public Guid? DocumentId { get; set; }

    /// <summary>MultipleChoice. Serialised to a JSON array in one column by the validator.</summary>
    public List<string>? Choices { get; set; }

    /// <summary>
    /// The transport-to-rules mapping, in one place so that no handler can map it slightly differently.
    /// <c>IsCarriedForward</c> is hardcoded false here: this overload only ever converts values a CLIENT
    /// supplied, and a client-supplied value is by definition not carried forward.
    /// </summary>
    public static IReadOnlyList<FieldValueValidation.SubmittedFieldValue> ToSubmitted(
        IEnumerable<TicketFieldValueInputDto> inputs) =>
    [
        .. inputs.Select(input => new FieldValueValidation.SubmittedFieldValue(
            input.FieldKey,
            input.Text,
            input.Number,
            input.Date,
            input.DateTo,
            input.Boolean,
            input.DocumentId,
            input.Choices,
            IsCarriedForward: false))
    ];
}
