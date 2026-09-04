using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;

namespace AccountantApp.Api.Slices.TicketTypes;

internal sealed class TicketTypesActionCatalogue : IActionCatalogue
{
    public string SliceName => "TicketTypes";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } =
        new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
        {
            ["CreateTicketType"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["EditTicketType"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["ToggleTicketType"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["ReadTicketType"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],
            ["ListTicketTypes"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee]
        };
}