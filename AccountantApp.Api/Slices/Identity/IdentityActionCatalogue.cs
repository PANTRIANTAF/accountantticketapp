using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;

namespace AccountantApp.Api.Slices.Identity;

/// <summary>
/// Six actions, and no more.
///
/// There is deliberately no action for login, logout, `me`, or change-password: each is available to
/// every authenticated caller, or to nobody. An entry listing all four roles would imply a role
/// decision where there is not one, and would be a check that can only ever pass.
///
/// ListAccountants is the only entry with two roles. The field-level difference between what an Admin
/// and a User see lives in the handler, not here -- the catalogue can express "who may call", not
/// "what they see".
/// </summary>
public sealed class IdentityActionCatalogue : IActionCatalogue
{
    public string SliceName => "Identity";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } =
        new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
        {
            ["ListAccountants"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["InviteAccountant"] = [UserRole.AccountantAdmin],
            ["SuspendAccountant"] = [UserRole.AccountantAdmin],
            ["ReactivateAccountant"] = [UserRole.AccountantAdmin],
            ["PromoteAccountant"] = [UserRole.AccountantAdmin],
            ["DemoteAccountant"] = [UserRole.AccountantAdmin],
        };
}
