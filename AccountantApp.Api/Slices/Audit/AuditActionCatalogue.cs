using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;

namespace AccountantApp.Api.Slices.Audit;

internal sealed class AuditActionCatalogue : IActionCatalogue
{
    public string SliceName => "Audit";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } =
        new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
        {
            ["ReadAuditLog"] = [UserRole.AccountantAdmin]
        };
}