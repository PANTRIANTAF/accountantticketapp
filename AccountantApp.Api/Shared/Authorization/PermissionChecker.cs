using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;

namespace AccountantApp.Api.Shared.Authorization;

public sealed class PermissionChecker : IPermissionChecker
{
    private readonly IReadOnlyDictionary<string, UserRole[]> _actions;
    private readonly IAuditApi _auditApi;
    private readonly ILogger<PermissionChecker> _logger;

    public PermissionChecker(
        IEnumerable<IActionCatalogue> catalogues,
        IAuditApi auditApi,
        ILogger<PermissionChecker> logger)
    {
        var actions = new Dictionary<string, (string Slice, UserRole[] Roles)>(StringComparer.Ordinal);
        foreach (var catalogue in catalogues)
        {
            foreach (var (action, roles) in catalogue.Actions)
            {
                if (roles.Length == 0)
                    throw new InvalidOperationException(
                        $"Action '{action}' in slice '{catalogue.SliceName}' has no permitted roles.");
                if (actions.TryGetValue(action, out var existing))
                    throw new InvalidOperationException(
                        $"Action '{action}' is declared by both '{existing.Slice}' and '{catalogue.SliceName}'.");
                actions.Add(action, (catalogue.SliceName, roles));
            }
        }

        _actions = actions.ToDictionary(pair => pair.Key, pair => pair.Value.Roles, StringComparer.Ordinal);
        _auditApi = auditApi;
        _logger = logger;
    }

    public async Task RequireAsync(
        CurrentUser user, string action, object? scope = null, CancellationToken ct = default)
    {
        var allowed = _actions.TryGetValue(action, out var roles) && roles.Contains(user.Role);

        if (allowed)
            return;

        // Audit the denial before throwing. An audit failure must not turn the 403 into a 500.
        try
        {
            await _auditApi.LogAsync(new AuditEntry(
                AuditActions.PermissionDenied,
                AuditTargets.None,
                string.Empty,
                user.CustomerId,
                AuditOutcome.Denied,
                After: new { Action = action }), ct);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception,
                "Failed to audit permission denial of {Action} for {Actor}.", action, user.Id);
        }

        throw new AppException($"Permission denied for action '{action}'.", 403);
    }
}
