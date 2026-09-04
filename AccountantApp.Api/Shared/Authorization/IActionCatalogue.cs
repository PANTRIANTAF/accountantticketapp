using AccountantApp.Api.Shared.Auth;

namespace AccountantApp.Api.Shared.Authorization;

public interface IActionCatalogue
{
    string SliceName { get; }
    IReadOnlyDictionary<string, UserRole[]> Actions { get; }
}