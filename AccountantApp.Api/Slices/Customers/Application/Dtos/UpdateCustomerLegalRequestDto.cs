namespace AccountantApp.Api.Slices.Customers.Application.Dtos;

public sealed class UpdateCustomerLegalRequestDto
{
    public Guid CustomerId { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string? TradingName { get; set; }
    public string TaxNumber { get; set; } = string.Empty;
    public string? TaxOffice { get; set; }
}