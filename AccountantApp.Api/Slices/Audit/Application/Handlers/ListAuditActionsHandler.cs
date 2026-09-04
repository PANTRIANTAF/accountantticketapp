using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Slices.Audit.Application.Dtos;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Audit.Application.Handlers;

/// <summary>
/// The filter catalogues for the audit screen. No DbContext: everything it returns is a compile-time
/// constant.
/// </summary>
/// <remarks>
/// It still requires ReadAuditLog. The catalogue is not secret, but an endpoint that enumerates
/// every auditable operation in the system is a map of the application's privileged surface, and
/// there is no reason for anyone but an Admin to fetch it.
/// </remarks>
public class ListAuditActionsHandler
{
    private readonly IPermissionChecker _permissions;

    public ListAuditActionsHandler(IPermissionChecker permissions) => _permissions = permissions;

    public async Task<AuditActionsResponseDto> Handle(CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ReadAuditLog", ct: ct);

        return new AuditActionsResponseDto
        {
            Actions = AuditActions.All.OrderBy(action => action, StringComparer.Ordinal).ToList(),
            TargetKinds = AuditTargets.All.OrderBy(kind => kind, StringComparer.Ordinal).ToList(),
            // Beyond the plan's two lists. The search rejects an unrecognised Outcome with a 422,
            // so a client that hard-codes its own copy of these three values is a client that can
            // 422 itself; served from the same catalogue AuditApi validates against on write.
            Outcomes = AuditOutcome.All.OrderBy(outcome => outcome, StringComparer.Ordinal).ToList()
        };
    }
}
