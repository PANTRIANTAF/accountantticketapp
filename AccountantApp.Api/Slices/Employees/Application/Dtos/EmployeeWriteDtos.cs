using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Slices.Customers.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Employees.Application.Dtos;

/// <summary>
/// Registers a person as an Employee. Creates NO account and sends NO email -- inviting is a separate,
/// later, optional operation. A CustomerId is present because an Accountant registers on behalf of a
/// Customer; a Customer Admin supplying anything other than their own Customer is a 403.
/// </summary>
public sealed class RegisterEmployeeRequestDto
{
    public Guid CustomerId { get; set; }
    public string GivenName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string? WorkEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? TaxIdentificationNumber { get; set; }
    public string? SocialSecurityNumber { get; set; }
    public DateOnly EmploymentStartDate { get; set; }
}

/// <summary>
/// Edits an Employee record on behalf of someone else -- Accountants and the owning Customer's Admins.
/// Does not change the role and does not touch the account: those are separate endpoints, because they
/// have different permissions and different audit meanings.
///
/// Every field is replaced with what is sent, including the nullable ones: omitting WorkEmail clears it.
/// A partial-update shape would need a way to distinguish "absent" from "null", and the one it would
/// reach for -- treating null as absent -- makes clearing an email impossible.
/// </summary>
public sealed class UpdateEmployeeRequestDto
{
    public Guid EmployeeId { get; set; }
    public string GivenName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string? WorkEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? TaxIdentificationNumber { get; set; }
    public string? SocialSecurityNumber { get; set; }
    public DateOnly EmploymentStartDate { get; set; }
}

/// <summary>
/// What a person may change about themselves. There is deliberately NO EmployeeId: the handler resolves
/// the record from the session, so this endpoint is structurally incapable of editing a colleague.
/// That absence IS the security control -- an EmployeeId here, however carefully checked, turns every
/// future edit of the handler into an opportunity to check it wrongly.
///
/// The field list is also the control on what may change: no start date, no job title, no personal
/// identifying numbers, no name. A person cannot promote themselves, cannot backdate their employment,
/// and cannot alter the numbers the Office files taxes with.
/// </summary>
public sealed class UpdateOwnContactRequestDto
{
    public string? WorkEmail { get; set; }
    public string? ContactPhone { get; set; }
}

/// <summary>
/// Invites an existing Employee to get a login. This is the second half of "registering and inviting
/// are two separate operations".
/// </summary>
public sealed class InviteEmployeeRequestDto
{
    public Guid EmployeeId { get; set; }

    /// <summary>
    /// Optional override of the address the invitation goes to. Null falls back to the Employee's
    /// WorkEmail, and an Employee with neither is a 422 -- you cannot invite somebody without an
    /// address. Whatever is used is then written back to WorkEmail, so the record always shows the
    /// address that actually received the invitation rather than one the inviter picked silently.
    /// </summary>
    public string? LoginEmail { get; set; }

    /// <summary>
    /// CustomerAdmin or Employee. An Accountant role here is rejected -- an Employee of a Customer is
    /// not staff of the accounting office, and Identity enforces this too.
    /// </summary>
    public UserRole Role { get; set; } = UserRole.Employee;
}

/// <summary>
/// Changes the address an Employee SIGNS IN WITH -- not their work email, which /update and
/// /update-own-contact change. The two are separate fields with separate consequences, and this endpoint
/// deliberately leaves the work email alone: a person whose surname changed may want both updated, and
/// two explicit calls are better than one endpoint that quietly rewrites a field the caller did not name.
///
/// Reserved to the two Accountant roles. A Customer Admin cannot change a colleague's login address and
/// nobody can change their own: both are account-takeover routes, one of them one step removed.
/// </summary>
public sealed class ChangeEmployeeLoginEmailRequestDto
{
    public Guid EmployeeId { get; set; }

    /// <summary>
    /// The new sign-in address. Validated and normalized by Identity's own helper, so an address this
    /// endpoint accepts is one login can match -- a second, laxer validator here would let an address in
    /// through a door that authentication could never open.
    /// </summary>
    public string LoginEmail { get; set; } = string.Empty;
}

public sealed class SetEmployeeRoleRequestDto
{
    public Guid EmployeeId { get; set; }

    /// <summary>CustomerAdmin or Employee only.</summary>
    public UserRole Role { get; set; }
}

/// <summary>
/// Marks an Employee as having left.
///
/// Reversible, but only as a CORRECTION: /api/employees/reinstate undoes a departure entered by
/// mistake. Somebody who genuinely left and later returns is registered again as a new record, because
/// their two periods of employment are separate facts and clearing the first one's end date corrupts
/// both. Nothing can enforce that distinction -- the audit entry records which one the caller made.
/// </summary>
public sealed class DepartEmployeeRequestDto
{
    public Guid EmployeeId { get; set; }

    /// <summary>
    /// Must not precede the employment start date. May be in the future -- a notice period is normal --
    /// and the record is marked Departed immediately regardless, because the alternative is a scheduled
    /// job this application does not have.
    /// </summary>
    public DateOnly EmploymentEndDate { get; set; }
}

/// <summary>
/// The composite operation: a new Customer, its first Employee, and that Employee's CustomerAdmin
/// invitation, in one transaction. Registered from EmployeesEndpoints because this slice owns the
/// second and third steps and therefore the transaction; Customers owns only the first.
/// </summary>
public sealed class OnboardCustomerRequestDto
{
    /// <summary>
    /// The Customers slice's own creation shape, reused rather than re-listed. A parallel list of twelve
    /// fields here would drift from that one the first time a Customer field is added.
    /// </summary>
    public CreateCustomer Customer { get; set; } = new();

    /// <summary>
    /// The first Employee. No CustomerId -- the Customer does not exist yet, so the only correct value
    /// is the one this handler is about to generate. A CustomerId field here could only ever be wrong.
    /// A work email IS required, unlike plain registration: this person is about to be invited, and the
    /// invitation goes to their work email.
    /// </summary>
    public OnboardFirstAdminDto FirstAdmin { get; set; } = new();
}

public sealed class OnboardFirstAdminDto
{
    public string GivenName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string WorkEmail { get; set; } = string.Empty;
    public string? ContactPhone { get; set; }
    public string? TaxIdentificationNumber { get; set; }
    public string? SocialSecurityNumber { get; set; }
    public DateOnly EmploymentStartDate { get; set; }
}

public sealed class OnboardCustomerResponseDto
{
    public Guid CustomerId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid UserAccountId { get; set; }
}
