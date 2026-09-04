using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Pagination;

namespace AccountantApp.Api.Slices.Employees.Application.Dtos;

// Three read shapes, because matrix section 4's "View an Employee record" row has three different
// answers: Accountants and the owning Customer Admin see everything, the Employee sees their own
// contact details, and the list row shows neither the personal identifying numbers nor the email.
//
// Three types rather than one with nulled-out fields, for the reason the Identity slice separates
// AccountantSummaryDto from AccountantDetailDto: a type that has no SocialSecurityNumber property
// cannot serialise one. A handler that must remember to null it out will one day not, and the reviewer
// of that diff sees a field being SET, which looks correct.

/// <summary>
/// One row of /api/employees/list. No personal identifying numbers, no email, no phone.
/// </summary>
public sealed class EmployeeSummaryDto
{
    public Guid Id { get; set; }
    public string GivenName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool HasAccount { get; set; }

    /// <summary>
    /// Not a column -- the role lives on the account, in Identity, and is filled in from one bulk
    /// IIdentityApi call after the page is materialised. Null for an accountless Employee, and the SPA
    /// renders that as "not invited". Do NOT default it to Employee: that would show every accountless
    /// person as holding a role they do not have.
    /// </summary>
    public UserRole? Role { get; set; }
}

/// <summary>
/// What Accountants and the owning Customer's Admins see. Carries the personal identifying numbers:
/// the Office needs them to do accounting work and the employer supplied them, so both may read them.
/// An Employee never receives this type, including for themselves.
/// </summary>
public sealed class EmployeeDetailDto
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string GivenName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool HasAccount { get; set; }
    public string? WorkEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? TaxIdentificationNumber { get; set; }
    public string? SocialSecurityNumber { get; set; }
    public DateOnly EmploymentStartDate { get; set; }
    public DateOnly? EmploymentEndDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>From Identity. Null for an accountless Employee.</summary>
    public UserRole? Role { get; set; }

    /// <summary>From Identity: Invited, Active, or Suspended. Null for an accountless Employee.</summary>
    public string? AccountStatus { get; set; }
}

/// <summary>
/// What an Employee gets for their own record. No tax identification number, no social-security
/// number, no Status, no UserAccountId -- not because they are secret from the person themselves, but
/// because this endpoint has no reason to return them and a narrower type cannot leak them.
/// </summary>
public sealed class EmployeeSelfDto
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string GivenName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string? WorkEmail { get; set; }
    public string? ContactPhone { get; set; }
    public DateOnly EmploymentStartDate { get; set; }

    /// <summary>
    /// Set by the endpoints that change a work email, because a person editing their own address will
    /// otherwise assume they have just changed how they log in. They have not: the login email lives on
    /// their account, in Identity, and only an Accountant can change it -- through
    /// /api/employees/change-login-email, which this person has no access to.
    /// </summary>
    public string? Notice { get; set; }
}

/// <summary>
/// The response of the operations that change state elsewhere and have nothing of their own to return:
/// set-role, depart, reinstate, suspend-account, reactivate-account, change-login-email.
/// </summary>
public sealed class MarkedResultDto
{
    public bool Success { get; set; }
}

public sealed class ListEmployeesRequestDto
{
    /// <summary>
    /// A filter for Accountants. A Customer Admin naming a Customer other than their own is a 403, not
    /// a silently reinterpreted filter -- a filter that quietly means something else for one role is how
    /// a Customer Admin comes to believe they have cross-Customer visibility.
    /// </summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Active or Departed. Null returns both -- Departed Employees stay visible.</summary>
    public string? Status { get; set; }

    public bool? HasAccount { get; set; }

    /// <summary>Matches given name, family name, and work email, case-insensitively.</summary>
    public string? SearchTerm { get; set; }

    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = PaginatedQuery.DefaultPageSize;
}

/// <summary>Used by view, invite, suspend-account, reactivate-account, and reinstate.</summary>
public sealed class EmployeeIdRequestDto
{
    public Guid EmployeeId { get; set; }
}
