using System.Text.Json;
using System.Text.RegularExpressions;
using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Shared.Validation;
using AccountantApp.Api.Slices.TicketTypes.Application.Dtos;
using AccountantApp.Api.Slices.TicketTypes.Core;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

namespace AccountantApp.Api.Slices.TicketTypes.Application;

internal static class TicketTypeMapper
{
    internal static bool IsCustomerSide(UserRole role) =>
        role is UserRole.CustomerAdmin or UserRole.Employee;

    // The two Customer-side gates, as predicates, so the HTTP handlers (which throw 404) and
    // ITicketTypesApi (which returns null) cannot disagree about what is visible. They did
    // disagree: correction note T-4 was applied to GetTicketTypeVersionHandler and missed in
    // TicketTypesApi, because each path carried its own copy of the rule.

    // May this caller see that the type exists at all? Customer-side callers cannot discover a
    // deactivated type.
    internal static bool IsDiscoverableBy(TicketType type, UserRole role) =>
        !IsCustomerSide(role) || type.IsActive && IsInAudienceOf(type, role);

    // Is this caller in the type's audience? Independent of IsActive: the version-by-number read
    // must stay reachable for a historical ticket even after the type is deactivated (correction
    // note TicketTypes T-4), so that read applies this check and never the discovery check.
    internal static bool IsInAudienceOf(TicketType type, UserRole role) =>
        !IsCustomerSide(role) || role != UserRole.Employee || type.AllowEmployeeToOpen;

    internal static void ApplyCustomerSideVisibility(TicketType type, CurrentUser user)
    {
        if (!IsDiscoverableBy(type, user.Role))
            throw new AppException("Ticket type not found.", 404);
    }

    internal static void ApplyCustomerSideAudience(TicketType type, CurrentUser user)
    {
        if (!IsInAudienceOf(type, user.Role))
            throw new AppException("Ticket type not found.", 404);
    }

    // Not an AppException: a type with no version rows is a broken invariant, not something a
    // caller did. AppException's message is written into the response body, and this one names
    // internal state. The middleware's catch-all turns this into a bare ProblemDetails 500.
    internal static TicketTypeVersion CurrentVersionOf(TicketType type) =>
        type.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault()
        ?? throw new InvalidOperationException($"Ticket type {type.Id} has no version rows.");

    internal static FieldDescriptor ToEntity(CreateFieldDescriptorDto field, DateTime now) => new()
    {
        Key = field.Key,
        Label = field.Label,
        HelpText = field.HelpText,
        DataType = field.DataType,
        DisplayOrder = field.DisplayOrder,
        GroupName = field.GroupName,
        IsRequired = field.IsRequired,
        IsVisibleToCustomer = field.IsVisibleToCustomer,
        ChoiceOptions = JsonSerializer.Serialize(field.ChoiceOptions ?? []),
        MinLength = field.Validation?.MinLength,
        MaxLength = field.Validation?.MaxLength,
        MinValue = field.Validation?.MinValue,
        MaxValue = field.Validation?.MaxValue,
        EarliestDate = field.Validation?.EarliestDate,
        LatestDate = field.Validation?.LatestDate,
        RegexPattern = field.Validation?.RegexPattern ?? string.Empty,
        AllowedFileTypes = string.Join(',', field.Validation?.AllowedFileTypes ?? []),
        MaxFileSizeBytes = field.Validation?.MaxFileSizeBytes,
        ConditionalVisibilityFieldKey = field.ConditionalVisibility?.FieldKey ?? string.Empty,
        ConditionalVisibilityValue = field.ConditionalVisibility?.Value ?? string.Empty,
        CreatedAt = now
    };

    // Every limit here comes from the VARCHAR(n) widths in
    // Infrastructure/Migrations/20260829_001_CreateTicketTypesSchema.sql. Checked before the
    // save, because PostgreSQL would otherwise raise 22001 and EF would surface a
    // DbUpdateException as a 500 for what is a client mistake.
    private const int CodeMaxLength = 100;
    private const int DisplayNameMaxLength = 255;
    private const int CategoryMaxLength = 100;
    private const int FieldKeyMaxLength = 100;
    private const int FieldLabelMaxLength = 255;
    private const int GroupNameMaxLength = 100;
    private const int RegexPatternMaxLength = 500;
    private const int AllowedFileTypesMaxLength = 500;
    private const int ConditionalValueMaxLength = 500;
    // TEXT columns have no PostgreSQL length limit, but unbounded input on a table nothing
    // ever purges is still a mistake — cap it explicitly rather than deferring to an unset
    // request-body limit elsewhere (see correction note TicketTypes T-11).
    private const int DescriptionMaxLength = 10_000;
    private const int HelpTextMaxLength = 10_000;

    // Every request string is trimmed before it is validated or stored, so a leading or
    // trailing space cannot defeat a blank check or a case-insensitive uniqueness index
    // (see correction note TicketTypes T-7).
    internal static void NormalizeTicketType(CreateTicketTypeRequestDto req)
    {
        req.Code = req.Code.Trim();
        req.DisplayName = req.DisplayName.Trim();
        req.Category = req.Category.Trim();
        NormalizeFields(req.Fields);
    }

    // Edit gets its own overload rather than trimming inline at the call site: an edit that
    // trimmed only DisplayName and Category left field labels and group names untrimmed, so the
    // same value stored through /create and through /edit came out different.
    internal static void NormalizeTicketType(EditTicketTypeRequestDto req)
    {
        req.DisplayName = req.DisplayName.Trim();
        req.Category = req.Category.Trim();
        NormalizeFields(req.Fields);
    }

    private static void NormalizeFields(IEnumerable<CreateFieldDescriptorDto> fields)
    {
        foreach (var field in fields)
        {
            field.Label = field.Label.Trim();
            field.GroupName = field.GroupName.Trim();
        }
    }

    internal static void ValidateTicketType(string code, string displayName, string category)
    {
        RequireLength(code, CodeMaxLength, "Code");
        RequireNonBlank(displayName, "DisplayName");
        RequireLength(displayName, DisplayNameMaxLength, "DisplayName");
        RequireNonBlank(category, "Category");
        RequireLength(category, CategoryMaxLength, "Category");
    }

    internal static void ValidateDescription(string? description) =>
        RequireLength(description, DescriptionMaxLength, "Description");

    private static void RequireNonBlank(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new AppException($"{name} is required.", 422);
    }

    private static void RequireLength(string? value, int max, string name)
    {
        if (value is not null && value.Length > max)
            throw new AppException($"{name} must be at most {max} characters.", 422);
    }

    internal static void ValidateFields(IReadOnlyCollection<CreateFieldDescriptorDto> fields)
    {
        if (fields.Count == 0)
            throw new AppException("At least one field is required.", 422);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key) || field.Key.Length > FieldKeyMaxLength)
                throw new AppException("Every field key is required and must be at most 100 characters.", 422);
            if (!keys.Add(field.Key))
                throw new AppException($"Duplicate field key '{field.Key}'.", 422);
            if (!FieldDataTypes.All.Contains(field.DataType))
                throw new AppException($"Unknown field data type '{field.DataType}'.", 422);

            RequireLength(field.Label, FieldLabelMaxLength, $"Label of field '{field.Key}'");
            RequireLength(field.GroupName, GroupNameMaxLength, $"GroupName of field '{field.Key}'");
            RequireLength(field.Validation?.RegexPattern, RegexPatternMaxLength,
                $"RegexPattern of field '{field.Key}'");
            RequireLength(field.ConditionalVisibility?.Value, ConditionalValueMaxLength,
                $"ConditionalVisibility value of field '{field.Key}'");
            RequireLength(field.ConditionalVisibility?.FieldKey, FieldKeyMaxLength,
                $"ConditionalVisibility field key of field '{field.Key}'");
            // AllowedFileTypes is persisted as one comma-separated string, so the joined
            // length is what has to fit, not each entry.
            RequireLength(string.Join(',', field.Validation?.AllowedFileTypes ?? []),
                AllowedFileTypesMaxLength, $"AllowedFileTypes of field '{field.Key}'");

            ValidateRegexCompiles(field);

            var isChoice = field.DataType is "SingleChoice" or "MultipleChoice";
            if (isChoice && (field.ChoiceOptions?.Count ?? 0) < 2)
                throw new AppException($"Choice field '{field.Key}' requires at least two options.", 422);
            if (!isChoice && field.ChoiceOptions is { Count: > 0 })
                throw new AppException($"Non-choice field '{field.Key}' cannot have choice options.", 422);

            var validation = field.Validation;
            if (validation?.MinLength > validation?.MaxLength ||
                validation?.MinValue > validation?.MaxValue ||
                validation?.EarliestDate > validation?.LatestDate)
                throw new AppException($"Invalid validation range for field '{field.Key}'.", 422);
        }

        foreach (var field in fields.Where(f => f.ConditionalVisibility is not null))
        {
            var dependency = field.ConditionalVisibility!.FieldKey;
            if (string.Equals(dependency, field.Key, StringComparison.OrdinalIgnoreCase) || !keys.Contains(dependency))
                throw new AppException($"Invalid conditional visibility reference for field '{field.Key}'.", 422);
        }
    }

    // The match timeout moved to Shared/Validation/UserSuppliedRegex.cs. It used to be an internal
    // field here, which Tickets reached as TicketTypes.Application.TicketTypeMapper.RegexMatchTimeout
    // -- legal to the compiler, one assembly, but a dependency rule 2 violation that pinned this
    // mapper's shape in place. Both slices must use the SAME budget (see that file), so it is now one
    // shared constant instead of one slice borrowing another's private detail.

    // A caller-supplied pattern is code the Tickets slice will later run against ticket
    // values. Compile it here, where the caller can still be told it is wrong; an
    // uncompilable pattern stored now becomes an untraceable 500 in another slice later.
    private static void ValidateRegexCompiles(CreateFieldDescriptorDto field)
    {
        var pattern = field.Validation?.RegexPattern;
        if (string.IsNullOrEmpty(pattern))
            return;

        try
        {
            _ = new Regex(pattern, RegexOptions.None, UserSuppliedRegex.MatchTimeout);
        }
        catch (ArgumentException)
        {
            throw new AppException($"Field '{field.Key}' has an invalid regular expression.", 422);
        }
    }

    internal static TicketTypeDetailDto ToDetail(TicketType type, TicketTypeVersion version, UserRole callerRole)
    {
        var fields = version.FieldDescriptors.AsEnumerable();
        if (IsCustomerSide(callerRole))
            fields = fields.Where(f => f.IsVisibleToCustomer);

        return new TicketTypeDetailDto
        {
            Id = type.Id,
            // The version's OWN id, not type.Id. This is what a ticket stores so that editing the type
            // later cannot change what an already-open ticket asked for; see TicketTypeDetailDto.
            VersionId = version.Id,
            Code = type.Code,
            DisplayName = type.DisplayName,
            Description = type.Description,
            Category = type.Category,
            AllowEmployeeToOpen = type.AllowEmployeeToOpen,
            AllowSubjectOtherThanCreator = type.AllowSubjectOtherThanCreator,
            IsActive = type.IsActive,
            CurrentVersionNumber = type.VersionNumber,
            VersionNumber = version.VersionNumber,
            CreatedAt = type.CreatedAt,
            UpdatedAt = type.UpdatedAt,
            Fields = fields.OrderBy(f => f.DisplayOrder).Select(ToFieldDetail).ToList()
        };
    }

    private static FieldDescriptorDetailDto ToFieldDetail(FieldDescriptor field) => new()
    {
        Key = field.Key,
        Label = field.Label,
        HelpText = field.HelpText,
        DataType = field.DataType,
        DisplayOrder = field.DisplayOrder,
        GroupName = field.GroupName,
        IsRequired = field.IsRequired,
        IsVisibleToCustomer = field.IsVisibleToCustomer,
        ChoiceOptions = DeserializeChoices(field.ChoiceOptions),
        Validation = new FieldValidationDto
        {
            MinLength = field.MinLength,
            MaxLength = field.MaxLength,
            MinValue = field.MinValue,
            MaxValue = field.MaxValue,
            EarliestDate = field.EarliestDate,
            LatestDate = field.LatestDate,
            RegexPattern = field.RegexPattern,
            AllowedFileTypes = string.IsNullOrWhiteSpace(field.AllowedFileTypes)
                ? []
                : field.AllowedFileTypes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
            MaxFileSizeBytes = field.MaxFileSizeBytes
        },
        ConditionalVisibility = string.IsNullOrWhiteSpace(field.ConditionalVisibilityFieldKey)
            ? null
            : new ConditionalVisibilityDto
            {
                FieldKey = field.ConditionalVisibilityFieldKey,
                Value = field.ConditionalVisibilityValue
            }
    };

    private static List<ChoiceOptionDto> DeserializeChoices(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ChoiceOptionDto>>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}