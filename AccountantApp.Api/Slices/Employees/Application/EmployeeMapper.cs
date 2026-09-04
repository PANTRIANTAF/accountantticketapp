using System.Linq.Expressions;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Core;

namespace AccountantApp.Api.Slices.Employees.Application;

/// <summary>
/// The three field lists of section 3.1, each in exactly one place.
///
/// They are Expression&lt;Func&lt;...&gt;&gt; and not Func&lt;...&gt;: a Func forces client-side
/// evaluation, so EF fetches every column of every row and projects in memory -- and the "narrower"
/// self projection then reads the social-security number out of the database anyway. The whole point of
/// three DTOs is that the sensitive columns are never selected.
///
/// The compiled companions exist only for the handlers that have just written an entity and hold it in
/// memory already. They are .Compile() of the same expression rather than a second field list, because
/// two lists is how the detail shape and the just-created-response shape drift apart.
/// </summary>
internal static class EmployeeMapper
{
    internal static readonly Expression<Func<Employee, EmployeeSummaryDto>> ToSummaryExpression =
        employee => new EmployeeSummaryDto
        {
            Id = employee.Id,
            GivenName = employee.GivenName,
            FamilyName = employee.FamilyName,
            JobTitle = employee.JobTitle,
            Status = employee.Status,
            HasAccount = employee.UserAccountId != null

            // Role is deliberately absent: it is not a column. The list handler fills it from one bulk
            // IIdentityApi call after the page is materialised.
        };

    internal static readonly Expression<Func<Employee, EmployeeDetailDto>> ToDetailExpression =
        employee => new EmployeeDetailDto
        {
            Id = employee.Id,
            CustomerId = employee.CustomerId,
            GivenName = employee.GivenName,
            FamilyName = employee.FamilyName,
            JobTitle = employee.JobTitle,
            Status = employee.Status,
            HasAccount = employee.UserAccountId != null,
            WorkEmail = employee.WorkEmail,
            ContactPhone = employee.ContactPhone,
            TaxIdentificationNumber = employee.TaxIdentificationNumber,
            SocialSecurityNumber = employee.SocialSecurityNumber,
            EmploymentStartDate = employee.EmploymentStartDate,
            EmploymentEndDate = employee.EmploymentEndDate,
            CreatedAt = employee.CreatedAt
        };

    internal static readonly Expression<Func<Employee, EmployeeSelfDto>> ToSelfExpression =
        employee => new EmployeeSelfDto
        {
            Id = employee.Id,
            CustomerId = employee.CustomerId,
            GivenName = employee.GivenName,
            FamilyName = employee.FamilyName,
            JobTitle = employee.JobTitle,
            WorkEmail = employee.WorkEmail,
            ContactPhone = employee.ContactPhone,
            EmploymentStartDate = employee.EmploymentStartDate
        };

    private static readonly Func<Employee, EmployeeDetailDto> ToDetailFunc = ToDetailExpression.Compile();
    private static readonly Func<Employee, EmployeeSelfDto> ToSelfFunc = ToSelfExpression.Compile();

    internal static EmployeeDetailDto ToDetailDto(Employee employee) => ToDetailFunc(employee);

    internal static EmployeeSelfDto ToSelfDto(Employee employee) => ToSelfFunc(employee);

    /// <summary>
    /// What goes into an audit row. The two personal identifying numbers are represented as booleans and
    /// never as values, because Redaction only redacts by substring on password/hash/salt/token/secret/
    /// apikey/sessionid/cookie -- neither "TaxIdentificationNumber" nor "SocialSecurityNumber" matches
    /// any of them, so a value put here is a value retained forever in a table nobody purges.
    ///
    /// <paramref name="changedSensitiveFields"/> carries the NAMES of the sensitive fields an edit
    /// touched, satisfying section 4.9 rule 7's "which fields changed, not their values". Passing null
    /// gives the plain snapshot used by register and depart.
    /// </summary>
    internal static object ToAuditSnapshot(
        Employee employee,
        IReadOnlyList<string>? changedSensitiveFields = null) => new
    {
        employee.CustomerId,
        employee.GivenName,
        employee.FamilyName,
        employee.JobTitle,
        employee.WorkEmail,
        employee.ContactPhone,
        employee.Status,
        employee.EmploymentStartDate,
        employee.EmploymentEndDate,
        employee.DepartedAt,
        employee.HasAccount,
        HasTaxIdentificationNumber = employee.TaxIdentificationNumber is not null,
        HasSocialSecurityNumber = employee.SocialSecurityNumber is not null,
        ChangedSensitiveFields = changedSensitiveFields
    };
}
