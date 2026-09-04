namespace AccountantApp.Api.Slices.TicketTypes.Application.Dtos;

public class ListTicketTypesRequestDto
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 15;
    public bool? ActiveOnly { get; set; }
}