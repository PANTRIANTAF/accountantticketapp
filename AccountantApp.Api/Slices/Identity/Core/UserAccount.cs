using AccountantApp.Api.Shared.Auth;

namespace AccountantApp.Api.Slices.Identity.Core;

/// <summary>
/// The only entity in the system that can authenticate. There is deliberately no Accountant table:
/// an Accountant is a UserAccount whose role is AccountantAdmin or AccountantUser, carrying its own
/// display name and contact email, with no Employee link (01-DomainModel.md section 2).
/// </summary>
public sealed class UserAccount
{
    public Guid Id { get; set; }

    /// <summary>The address as the person typed it. For display, and for addressing mail.</summary>
    public string LoginEmail { get; set; } = string.Empty;

    /// <summary>Lowercased and trimmed. The unique constraint and every lookup use this.</summary>
    public string NormalizedLoginEmail { get; set; } = string.Empty;

    /// <summary>Null while the account is Invited. Nullability here is load-bearing.</summary>
    public string? PasswordHash { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The shared enum from Shared/Auth/UserRole.cs, never a copy. It is the one type every slice's
    /// authorization depends on, and a second definition would be the worst duplication here.
    /// </summary>
    public UserRole Role { get; set; }

    /// <summary>Null for the two Accountant roles. No foreign key: Employees is another slice.</summary>
    public Guid? EmployeeId { get; set; }

    /// <summary>
    /// Null for the two Accountant roles. This looks like denormalisation and is not: an Employee's
    /// owning Customer is immutable, so a copy cannot go stale, and Identity may not call Employees
    /// because Employees -> Identity already exists. Passed in at creation time by whoever knows it.
    /// Customer *status* is the opposite case and is deliberately NOT a column -- it is read live
    /// through ICustomerApi.IsActiveAsync on every login.
    /// </summary>
    public Guid? CustomerId { get; set; }

    public string Status { get; set; } = AccountStatus.Invited;

    public bool MustChangePassword { get; set; }

    /// <summary>
    /// Set when the invitation is accepted. A nullable timestamp rather than a boolean, because
    /// *when* it happened is worth having and a boolean cannot be asked that.
    /// </summary>
    public DateTimeOffset? EmailConfirmedAt { get; set; }

    /// <summary>Consecutive failures. Reset to 0 on success AND when a lockout is applied.</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>
    /// Null means not locked out. A past timestamp also means not locked out: compare it to now
    /// rather than clearing it eagerly.
    /// </summary>
    public DateTimeOffset? LockoutExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? LastPasswordChangeAt { get; set; }

    public bool IsAccountant =>
        Role is UserRole.AccountantAdmin or UserRole.AccountantUser;

    /// <summary>
    /// Takes <paramref name="now"/> rather than reading the clock, because a method that reads the
    /// clock cannot be tested without waiting fifteen minutes.
    /// </summary>
    public bool IsLockedOut(DateTimeOffset now) =>
        LockoutExpiresAt is { } until && until > now;
}

public static class AccountStatus
{
    public const string Invited = "Invited";
    public const string Active = "Active";
    public const string Suspended = "Suspended";
}
