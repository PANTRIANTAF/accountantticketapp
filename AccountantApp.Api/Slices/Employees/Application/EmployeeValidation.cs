using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Core;

namespace AccountantApp.Api.Slices.Employees.Application;

/// <summary>
/// Normalizes in place and throws AppException(422). Same shape as CustomerValidation, deliberately:
/// one validation idiom per codebase.
///
/// Email normalization is duplicated from Identity's EmailNormalization on purpose. That type lives in
/// Identity.Application, which is not Identity's declared public surface -- reaching into it would break
/// dependency rule 4 (a slice may only touch another slice's ExternalInterfaces). The alternative,
/// promoting it to Shared, would make a change to how Identity compares login emails silently change how
/// this slice compares work emails, and the two are not the same column or the same uniqueness rule
/// (work email is unique per Customer, login email is globally unique).
/// </summary>
internal static class EmployeeValidation
{
    /// <summary>
    /// The typo guard on EmploymentStartDate. FLAGGED: this threshold is invented, not sourced from any
    /// governing document -- see the plan's section 13. A start date a year out is far more likely to be
    /// a mistyped year than a real future hire.
    /// </summary>
    internal const int MaximumStartDateYearsAhead = 1;

    internal static void NormalizeAndValidate(RegisterEmployeeRequestDto request)
    {
        if (request.CustomerId == Guid.Empty)
            throw Invalid("Customer is required.");
        request.GivenName = Required(request.GivenName, 100, "Given name");
        request.FamilyName = Required(request.FamilyName, 100, "Family name");
        request.JobTitle = Optional(request.JobTitle, 200, "Job title");
        request.WorkEmail = OptionalEmail(request.WorkEmail);
        request.ContactPhone = Optional(request.ContactPhone, 50, "Contact phone");
        request.TaxIdentificationNumber =
            Optional(request.TaxIdentificationNumber, 50, "Tax identification number");
        request.SocialSecurityNumber =
            Optional(request.SocialSecurityNumber, 50, "Social security number");
        request.EmploymentStartDate = StartDate(request.EmploymentStartDate);
    }

    internal static void NormalizeAndValidate(UpdateEmployeeRequestDto request)
    {
        if (request.EmployeeId == Guid.Empty)
            throw Invalid("Employee is required.");
        request.GivenName = Required(request.GivenName, 100, "Given name");
        request.FamilyName = Required(request.FamilyName, 100, "Family name");
        request.JobTitle = Optional(request.JobTitle, 200, "Job title");
        request.WorkEmail = OptionalEmail(request.WorkEmail);
        request.ContactPhone = Optional(request.ContactPhone, 50, "Contact phone");
        request.TaxIdentificationNumber =
            Optional(request.TaxIdentificationNumber, 50, "Tax identification number");
        request.SocialSecurityNumber =
            Optional(request.SocialSecurityNumber, 50, "Social security number");
        request.EmploymentStartDate = StartDate(request.EmploymentStartDate);
    }

    internal static void NormalizeAndValidate(UpdateOwnContactRequestDto request)
    {
        request.WorkEmail = OptionalEmail(request.WorkEmail);
        request.ContactPhone = Optional(request.ContactPhone, 50, "Contact phone");
    }

    internal static void NormalizeAndValidate(InviteEmployeeRequestDto request)
    {
        if (request.EmployeeId == Guid.Empty)
            throw Invalid("Employee is required.");
        request.LoginEmail = OptionalEmail(request.LoginEmail);
        RequireCustomerSideRole(request.Role);
    }

    internal static void NormalizeAndValidate(SetEmployeeRoleRequestDto request)
    {
        if (request.EmployeeId == Guid.Empty)
            throw Invalid("Employee is required.");
        RequireCustomerSideRole(request.Role);
    }

    /// <summary>
    /// Validates the whole onboarding request -- both blocks -- before the handler writes anything.
    /// The Customer block is Customers' to validate and is left to CreateAsync; this checks the
    /// Employee half, which the handler inserts itself.
    /// </summary>
    internal static void NormalizeAndValidate(OnboardFirstAdminDto request)
    {
        request.GivenName = Required(request.GivenName, 100, "Given name");
        request.FamilyName = Required(request.FamilyName, 100, "Family name");
        request.JobTitle = Optional(request.JobTitle, 200, "Job title");

        // Required here, unlike on registration: this operation always invites, and an invitation needs
        // somewhere to go. Absent is a 422 rather than a Customer created with nobody who can log in.
        request.WorkEmail = Required(request.WorkEmail, 320, "Work email");
        if (!request.WorkEmail.Contains('@', StringComparison.Ordinal))
            throw Invalid("Work email must contain '@'.");

        request.ContactPhone = Optional(request.ContactPhone, 50, "Contact phone");
        request.TaxIdentificationNumber =
            Optional(request.TaxIdentificationNumber, 50, "Tax identification number");
        request.SocialSecurityNumber =
            Optional(request.SocialSecurityNumber, 50, "Social security number");
        request.EmploymentStartDate = StartDate(request.EmploymentStartDate);
    }

    /// <summary>
    /// Both a role guard for the user (422) and a companion to IIdentityApi's own throw for the
    /// programmer. Both stay: they protect against different mistakes.
    /// </summary>
    internal static void RequireCustomerSideRole(UserRole role)
    {
        if (role is not (UserRole.CustomerAdmin or UserRole.Employee))
            throw Invalid("An Employee's role must be CustomerAdmin or Employee.");
    }

    internal static string? NormalizeStatusFilter(string? status)
    {
        var normalized = status?.Trim();
        if (string.IsNullOrEmpty(normalized))
            return null;
        if (normalized is not (EmployeeStatus.Active or EmployeeStatus.Departed))
            throw Invalid("Unknown employee status.");
        return normalized;
    }

    /// <summary>
    /// Upper-invariant, because the unique index on (customer_id, normalized_work_email) is a plain
    /// b-tree over the stored value -- the normalization has to happen before the insert, not in the
    /// query. Invariant culture, not the current one: Turkish-locale casing maps 'i' to 'İ', which would
    /// make the same address normalize differently depending on the server's locale.
    /// </summary>
    internal static string? Normalize(string? email)
    {
        var trimmed = email?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    private static DateOnly StartDate(DateOnly value)
    {
        if (value == default)
            throw Invalid("Employment start date is required.");

        var ceiling = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(MaximumStartDateYearsAhead);
        if (value > ceiling)
            throw Invalid(
                $"Employment start date cannot be more than {MaximumStartDateYearsAhead} year(s) in the future.");
        return value;
    }

    private static string? OptionalEmail(string? value)
    {
        var email = Optional(value, 320, "Work email");
        if (email is not null && !email.Contains('@', StringComparison.Ordinal))
            throw Invalid("Work email must contain '@'.");
        return email;
    }

    private static string Required(string? value, int maximumLength, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw Invalid($"{name} is required.");
        if (normalized.Length > maximumLength)
            throw Invalid($"{name} must be at most {maximumLength} characters.");
        return normalized;
    }

    private static string? Optional(string? value, int maximumLength, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
            return null;
        if (normalized.Length > maximumLength)
            throw Invalid($"{name} must be at most {maximumLength} characters.");
        return normalized;
    }

    private static AppException Invalid(string message) => new(message, 422);
}
