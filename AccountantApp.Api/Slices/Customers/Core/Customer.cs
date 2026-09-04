using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;

namespace AccountantApp.Api.Slices.Customers.Core;

public sealed class Customer : ICustomerScoped, ICustomerRoot
{
    public Guid Id { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string? TradingName { get; set; }
    public string TaxNumber { get; set; } = string.Empty;
    public string? TaxOffice { get; set; }
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string AddressCity { get; set; } = string.Empty;
    public string AddressPostalCode { get; set; } = string.Empty;
    public string AddressCountry { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string Status { get; set; } = CustomerStatus.Active;
    public DateOnly OnboardedOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid CustomerId => Id;
}

public static class CustomerStatus
{
    public const string Active = "Active";
    public const string Suspended = "Suspended";
}
