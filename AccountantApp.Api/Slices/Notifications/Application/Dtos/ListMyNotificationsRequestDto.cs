namespace AccountantApp.Api.Slices.Notifications.Application.Dtos;

public sealed class ListMyNotificationsRequestDto
{
    public bool UnreadOnly { get; set; } = false;
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 15;
}
