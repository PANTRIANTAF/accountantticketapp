namespace AccountantApp.Api.Slices.Customers.Application.Dtos;

public sealed class CustomerSummaryDto
{
    public Guid Id { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string? TradingName { get; set; }
    public string Status { get; set; } = string.Empty;
}