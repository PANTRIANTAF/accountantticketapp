using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Customers.Application.Dtos;

namespace AccountantApp.Api.Slices.Customers.Application;

internal static class CustomerValidation
{
    internal static void NormalizeAndValidate(CreateCustomerRequestDto request)
    {
        request.LegalName = Required(request.LegalName, 300, "Legal name");
        request.TradingName = Optional(request.TradingName, 300, "Trading name");
        request.TaxNumber = Required(request.TaxNumber, 50, "Tax number");
        request.TaxOffice = Optional(request.TaxOffice, 200, "Tax office");
        NormalizeContact(request);
        if (request.OnboardedOn == default)
            throw Invalid("Onboarded date is required.");
        if (request.OnboardedOn > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
            throw Invalid("Onboarded date cannot be more than one day in the future.");
    }

    internal static void NormalizeAndValidate(UpdateCustomerContactRequestDto request) =>
        NormalizeContact(request);

    internal static void NormalizeAndValidate(UpdateCustomerLegalRequestDto request)
    {
        request.LegalName = Required(request.LegalName, 300, "Legal name");
        request.TradingName = Optional(request.TradingName, 300, "Trading name");
        request.TaxNumber = Required(request.TaxNumber, 50, "Tax number");
        request.TaxOffice = Optional(request.TaxOffice, 200, "Tax office");
    }

    internal static string? NormalizeReason(string? reason) => Optional(reason, 500, "Reason");

    private static void NormalizeContact(CreateCustomerRequestDto request)
    {
        request.AddressLine1 = Required(request.AddressLine1, 200, "Address line 1");
        request.AddressLine2 = Optional(request.AddressLine2, 200, "Address line 2");
        request.AddressCity = Required(request.AddressCity, 100, "Address city");
        request.AddressPostalCode = Required(request.AddressPostalCode, 20, "Address postal code");
        request.AddressCountry = Required(request.AddressCountry, 100, "Address country");
        request.ContactEmail = Email(request.ContactEmail);
        request.ContactPhone = Required(request.ContactPhone, 40, "Contact phone");
    }

    private static void NormalizeContact(UpdateCustomerContactRequestDto request)
    {
        request.AddressLine1 = Required(request.AddressLine1, 200, "Address line 1");
        request.AddressLine2 = Optional(request.AddressLine2, 200, "Address line 2");
        request.AddressCity = Required(request.AddressCity, 100, "Address city");
        request.AddressPostalCode = Required(request.AddressPostalCode, 20, "Address postal code");
        request.AddressCountry = Required(request.AddressCountry, 100, "Address country");
        request.ContactEmail = Email(request.ContactEmail);
        request.ContactPhone = Required(request.ContactPhone, 40, "Contact phone");
    }

    private static string Email(string value)
    {
        var email = Required(value, 320, "Contact email");
        if (!email.Contains('@', StringComparison.Ordinal))
            throw Invalid("Contact email must contain '@'.");
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