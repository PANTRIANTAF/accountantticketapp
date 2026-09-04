namespace AccountantApp.Api.Slices.Notifications.Application.Dtos;

public sealed class MarkReadRequestDto
{
    public List<Guid> NotificationIds { get; set; } = [];
}
