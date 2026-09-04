using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using Microsoft.AspNetCore.Authentication;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

/// <summary>
/// Deletes the cookie. There is no sessions table to delete a row from, which is the direct
/// consequence of the cookie being the session -- so there is also nothing here that can fail
/// halfway and leave a session alive on the server.
/// </summary>
public sealed class LogoutHandler
{
    private readonly IAuditApi _audit;
    private readonly IHttpContextAccessor _httpContext;

    public LogoutHandler(IAuditApi audit, IHttpContextAccessor httpContext)
    {
        _audit = audit;
        _httpContext = httpContext;
    }

    public async Task<MarkedResultDto> Handle(CurrentUser user, CancellationToken ct)
    {
        // No permission check: anyone authenticated may end their own session, and there is no
        // variant of this operation that acts on somebody else.
        //
        // No transaction either. The audit entry is the only write, and IAuditApi handles its own
        // persistence; wrapping one audit call in a transaction adds a way to fail and nothing else.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.LoggedOut,
            AuditTargets.UserAccount,
            user.Id,
            user.CustomerId), ct);

        var httpContext = _httpContext.HttpContext
            ?? throw new InvalidOperationException("Logout requires an HTTP context.");

        // Audit first, sign out second. SignOutAsync only queues a Set-Cookie header, so ordering
        // does not strictly matter here -- but auditing before mutating is the rule everywhere else
        // in this codebase, and a logout that clears the cookie and then fails to record it is the
        // one ordering that loses information.
        await httpContext.SignOutAsync(SessionClaims.Scheme);

        // 200 with a body, not 204. Logging out twice is a 200 both times: it is idempotent, and
        // there is no state to conflict with.
        return MarkedResultDto.Done;
    }
}
