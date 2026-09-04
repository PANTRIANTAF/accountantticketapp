using AccountantApp.Api.Shared.Authorization;

namespace AccountantApp.Api.Slices.Employees.Core;

/// <summary>
/// A person who works for a Customer. 01-DomainModel.md section 2 calls keeping this separate from
/// UserAccount "the single most important structural decision in this model, because it is what makes
/// on-behalf-of ticketing possible": an Employee who has never logged in, and may never log in, can
/// still be the Subject of a Ticket somebody else opens for them.
/// </summary>
public sealed class Employee : ICustomerScoped
{
    public Guid Id { get; set; }

    /// <summary>
    /// The tenant boundary. Immutable after creation -- there is no operation that writes it again.
    /// The same natural person working for two Customers is two independent records with no link
    /// between them, which is what keeps Customer isolation absolute.
    /// </summary>
    public Guid CustomerId { get; set; }

    public string GivenName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;

    public string? WorkEmail { get; set; }
    public string? NormalizedWorkEmail { get; set; }

    /// <summary>Null for an accountless Employee. Written only by the invitation handlers.</summary>
    public Guid? UserAccountId { get; set; }

    public string? TaxIdentificationNumber { get; set; }
    public string? SocialSecurityNumber { get; set; }
    public string? JobTitle { get; set; }
    public string? ContactPhone { get; set; }

    // DateOnly, mapping to DATE. DateTime here would re-introduce the timezone problem the DATE
    // column exists to avoid.
    public DateOnly EmploymentStartDate { get; set; }
    public DateOnly? EmploymentEndDate { get; set; }

    public string Status { get; set; } = EmployeeStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DepartedAt { get; set; }

    public bool HasAccount => UserAccountId is not null;
    public bool IsActive => Status == EmployeeStatus.Active;

    // There is deliberately no Role property. The Customer Admin role is not a separate entity: a
    // Customer Admin IS an Employee whose UserAccount has role CustomerAdmin, so the role lives on
    // the account, in Identity, and this slice changes it by asking. A column here would give the
    // system two answers to "is this person a Customer Admin", and they will disagree.
    //
    // There is also no navigation property to UserAccount or Customer. Both are other slices'
    // entities, and a navigation would require this context to map their tables.
}

public static class EmployeeStatus
{
    public const string Active = "Active";
    public const string Departed = "Departed";
}
