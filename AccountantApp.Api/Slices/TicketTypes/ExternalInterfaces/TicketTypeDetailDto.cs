namespace AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

// The contract response shape, and therefore in ExternalInterfaces/ rather than
// Application/Dtos/: dependency rule 2 forbids another slice from referencing this slice's
// Application namespace, and ITicketTypesApi returns these types to Tickets
// (Slices/Tickets/IMPLEMENTATION_PLAN.md §6.1 problem 2, §13 item 1). One shape serves both the
// HTTP endpoints and the cross-slice contract — a second, parallel response type would drift and
// only one of the two would strip Accountant-only descriptors.
public class TicketTypeDetailDto
{
    /// <summary>The ticket TYPE's id. Stable across every version.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The id of the specific VERSION this DTO projects — the primary key of the
    /// <c>ticket_type_versions</c> row, not the type's id and not the version NUMBER.
    ///
    /// Without this the contract is not round-trippable, and that is not a theoretical complaint. A
    /// ticket stores <c>tickets.ticket_type_version_id</c>, a Guid, so that a later edit to the type
    /// cannot change what an already-open ticket asked for. Creation resolves the active version with
    /// <see cref="ITicketTypesApi.GetTicketTypeAsync"/> and has to persist *which* version it got;
    /// every later read resolves it back with
    /// <see cref="ITicketTypesApi.GetVersionByIdAsync(System.Guid, Shared.Auth.UserRole, System.Threading.CancellationToken)"/>,
    /// which takes that same Guid. With only <see cref="Id"/> and <see cref="VersionNumber"/> exposed,
    /// the consuming slice can see which version it was handed but cannot name it — and the only ways
    /// out are reaching into this slice's <c>Infrastructure</c> to look the id up, which dependency
    /// rule 2 forbids, or storing the version NUMBER and re-resolving by (type, number) on every read,
    /// which is a second lookup path that can disagree with the first.
    /// </summary>
    public Guid VersionId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool AllowEmployeeToOpen { get; set; }
    public bool AllowSubjectOtherThanCreator { get; set; }
    public bool IsActive { get; set; }
    public int CurrentVersionNumber { get; set; }
    public int VersionNumber { get; set; }
    public List<FieldDescriptorDetailDto> Fields { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class FieldDescriptorDetailDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public bool IsVisibleToCustomer { get; set; }
    public List<ChoiceOptionDto> ChoiceOptions { get; set; } = new();
    public FieldValidationDto Validation { get; set; } = new();
    public ConditionalVisibilityDto? ConditionalVisibility { get; set; }
}

// These three are exposed transitively by FieldDescriptorDetailDto, so they are contract types
// too and had to move with it. They are also reused by CreateFieldDescriptorDto on the request
// side; a request DTO depending on a contract type is fine, the reverse would not be.
public class ChoiceOptionDto
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class FieldValidationDto
{
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public DateOnly? EarliestDate { get; set; }
    public DateOnly? LatestDate { get; set; }
    public string RegexPattern { get; set; } = string.Empty;
    public List<string> AllowedFileTypes { get; set; } = new();
    public long? MaxFileSizeBytes { get; set; }
}

public class ConditionalVisibilityDto
{
    public string FieldKey { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
