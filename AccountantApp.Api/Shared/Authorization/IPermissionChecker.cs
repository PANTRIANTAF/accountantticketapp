using AccountantApp.Api.Shared.Auth;

namespace AccountantApp.Api.Shared.Authorization;

public interface IPermissionChecker
{
    // Throws AppException(403) if denied. Audits every denial before throwing.
    Task RequireAsync(CurrentUser user, string action, object? scope = null, CancellationToken ct = default);
}
