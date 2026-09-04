namespace AccountantApp.Api.Slices.Customers.Application.Dtos;

public sealed class SetCustomerStatusRequestDto
{
    public Guid CustomerId { get; set; }
    public string? Reason { get; set; }
}