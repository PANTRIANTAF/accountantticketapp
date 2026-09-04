using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

namespace AccountantApp.Api.Slices.TicketTypes.Application.Dtos;

public class CreateTicketTypeRequestDto
{
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool AllowEmployeeToOpen { get; set; } = true;
    public bool AllowSubjectOtherThanCreator { get; set; } = true;
    public List<CreateFieldDescriptorDto> Fields { get; set; } = new();
}

public class CreateFieldDescriptorDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public bool IsRequired { get; set; } = true;
    public bool IsVisibleToCustomer { get; set; } = true;
    public List<ChoiceOptionDto>? ChoiceOptions { get; set; }
    public FieldValidationDto? Validation { get; set; }
    public ConditionalVisibilityDto? ConditionalVisibility { get; set; }
}

// ChoiceOptionDto, FieldValidationDto and ConditionalVisibilityDto used to be declared here.
// FieldDescriptorDetailDto exposes all three, which makes them contract types, so they now live
// in ExternalInterfaces/TicketTypeDetailDto.cs and this request shape imports them.
