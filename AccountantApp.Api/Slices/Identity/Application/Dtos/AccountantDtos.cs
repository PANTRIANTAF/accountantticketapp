using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Pagination;

namespace AccountantApp.Api.Slices.Identity.Application.Dtos;

public sealed class InviteAccountantRequestDto
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Must be AccountantAdmin or AccountantUser. CustomerAdmin or Employee is a 422, not a silent
    /// coercion: this endpoint creates Accountant accounts, and Customer-side accounts are created
    /// through IIdentityApi by Employees, the only path that can supply the mandatory employee_id
    /// and customer_id.
    /// </summary>
    public UserRole Role { get; set; }
}

/// <summary>
/// Two fields, and that is a normative requirement rather than minimalism. Matrix section 2: an
/// Accountant User can see the list of Accountants because assigning a ticket requires knowing who
/// exists -- "Return names and identifiers only, not email addresses, login history, or status
/// detail."
///
/// This is why there are TWO DTOs. The limitation must be a different type, not a nulled-out field on
/// the detail DTO: a type that has no LoginEmail property cannot leak one, whereas a handler that
/// must remember to null it out will one day forget.
/// </summary>
public sealed record AccountantSummaryDto(Guid Id, string DisplayName);

/// <summary>Returned to AccountantAdmin only.</summary>
public sealed record AccountantDetailDto(
    Guid Id,
    string DisplayName,
    string LoginEmail,
    UserRole Role,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

public sealed class ListAccountantsRequestDto
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = PaginatedQuery.DefaultPageSize;
}

/// <summary>Used by suspend, reactivate, promote, and demote -- the four AccountantAdmin-only endpoints.</summary>
public sealed class AccountIdRequestDto
{
    public Guid UserAccountId { get; set; }
}
