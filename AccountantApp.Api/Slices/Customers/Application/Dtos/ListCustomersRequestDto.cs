namespace AccountantApp.Api.Slices.Customers.Application.Dtos;

public sealed class ListCustomersRequestDto
{
    public string? Status { get; set; }
    public string? Search { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 15;
}