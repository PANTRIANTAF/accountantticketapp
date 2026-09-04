namespace AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

/// <summary>
/// The eleven field data types, as named constants plus the set of all of them.
///
/// WHY THIS IS IN ExternalInterfaces/ AND NOT Core/. It used to be
/// <c>TicketTypes.Core.FieldDataTypes</c>, exposing only <see cref="All"/> and no named constants. That
/// left the Tickets slice — which has to switch on <c>FieldDescriptorDetailDto.DataType</c> to decide
/// how to parse and store a submitted value — with two bad options: reach into another slice's
/// <c>Core</c>, which dependency rule 2 forbids, or declare its own eleven string constants, which is
/// what it did. Eleven duplicated literals that nothing keeps in sync is exactly the drift the rule
/// exists to prevent: adding a twelfth type to one list and not the other produces a type that
/// validates as a descriptor and then falls through a switch somewhere else.
///
/// The data type is part of the TicketTypes CONTRACT — <see cref="FieldDescriptorDetailDto.DataType"/>
/// is a string returned across the slice boundary, so every consumer needs the vocabulary to interpret
/// it. Contract vocabulary belongs beside the contract. Same reasoning as the DTOs in this folder
/// (App/GeneralAppArchitecture.md §3).
///
/// These are the stored string values. They are persisted in <c>field_descriptors.data_type</c> and
/// named in the <c>ck_*</c> CHECK constraint, so RENAMING ONE IS A MIGRATION, not a rename refactor.
/// That is also why they are strings and not a C# enum: the set is validated at the boundary and in the
/// database, and an enum would silently coerce an unknown stored value to whichever member happens to
/// be 0.
/// </summary>
public static class FieldDataTypes
{
    public const string SingleLineText = "SingleLineText";
    public const string MultiLineText = "MultiLineText";
    public const string WholeNumber = "WholeNumber";
    public const string DecimalNumber = "DecimalNumber";
    public const string MoneyAmount = "MoneyAmount";
    public const string Date = "Date";
    public const string DateRange = "DateRange";
    public const string YesNo = "YesNo";
    public const string SingleChoice = "SingleChoice";
    public const string MultipleChoice = "MultipleChoice";
    public const string FileUpload = "FileUpload";

    /// <summary>
    /// All eleven, for validating an authored descriptor's <c>DataType</c>.
    ///
    /// Built FROM the constants above rather than listing the strings a second time, so a twelfth type
    /// cannot be added to one and forgotten in the other. Ordinal comparison, not
    /// <c>OrdinalIgnoreCase</c>: these are stored values matched against a database CHECK constraint
    /// that is itself case-sensitive, so accepting <c>"yesno"</c> here would write a row the constraint
    /// rejects — a 500 instead of the 422 the caller should have got.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        SingleLineText,
        MultiLineText,
        WholeNumber,
        DecimalNumber,
        MoneyAmount,
        Date,
        DateRange,
        YesNo,
        SingleChoice,
        MultipleChoice,
        FileUpload
    };
}
