namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// The six list shapes of plan §0.6, expressed as one filter rather than six endpoints -- they differ
/// only in a Where.
///
/// Each scope is authorized SEPARATELY (§4.3 rule 1): a CustomerAdmin passing
/// <see cref="TicketListScopes.All"/> receives 403, never a silently narrowed result. A scope that
/// quietly means something else for one role is how a Customer Admin comes to believe they have
/// cross-Customer visibility.
/// </summary>
public class ListTicketsRequestDto
{
    /// <summary>One of <see cref="TicketListScopes"/>. Required; an unknown value is 422.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Optional status filter. An unknown status is 422.</summary>
    public string? Status { get; set; }

    public int PageNumber { get; set; } = 1;

    public int PageSize { get; set; } = Shared.Pagination.PaginatedQuery.DefaultPageSize;
}

/// <summary>
/// The scope vocabulary. Strings rather than an enum because an unrecognised enum member binds to
/// whichever value happens to be 0 -- which here would be the widest scope in the list.
/// </summary>
public static class TicketListScopes
{
    /// <summary>Every ticket in the system. Accountants only (matrix §6 row 1).</summary>
    public const string All = "All";

    /// <summary>Submitted with no Assignee. Accountants only (matrix §6 row 2).</summary>
    public const string Unassigned = "Unassigned";

    /// <summary>Assigned to the caller. Accountants only -- assignment is an Accountant concept.</summary>
    public const string AssignedToMe = "AssignedToMe";

    /// <summary>Every ticket of the caller's own Customer. Customer-scoped roles only.</summary>
    public const string MyCustomer = "MyCustomer";

    /// <summary>Tickets the caller created or is the Subject of. Every role.</summary>
    public const string Mine = "Mine";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
        { All, Unassigned, AssignedToMe, MyCustomer, Mine };
}
