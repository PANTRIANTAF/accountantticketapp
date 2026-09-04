namespace AccountantApp.Api.Slices.TicketTypes.Core;

public class FieldDescriptor
{
    public Guid Id { get; set; }
    public Guid TicketTypeVersionId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public bool IsRequired { get; set; } = true;
    public bool IsVisibleToCustomer { get; set; } = true;
    public string ChoiceOptions { get; set; } = "[]";
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public DateOnly? EarliestDate { get; set; }
    public DateOnly? LatestDate { get; set; }
    public string RegexPattern { get; set; } = string.Empty;
    public string AllowedFileTypes { get; set; } = string.Empty;
    public long? MaxFileSizeBytes { get; set; }
    public string ConditionalVisibilityFieldKey { get; set; } = string.Empty;
    public string ConditionalVisibilityValue { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public TicketTypeVersion TicketTypeVersion { get; set; } = default!;
}
