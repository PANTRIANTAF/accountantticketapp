using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;

namespace AccountantApp.Api.Slices.Notifications;

internal sealed class NotificationsActionCatalogue : IActionCatalogue
{
    public string SliceName => "Notifications";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } =
        new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
        {
            ["ReadOwnNotifications"] = [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin, UserRole.Employee],
            ["MarkOwnNotificationRead"] = [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin, UserRole.Employee]
        };
}
