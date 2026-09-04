using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

/// <summary>
/// What the front end calls on load to decide whether to show the application or the login screen.
///
/// Reads the DATABASE, not the claims. Rebuilding this DTO from the cookie would be one line shorter
/// and would return a display name and a must-change-password flag that were true up to eight hours
/// ago. The flag is the one that matters: a user who changed their password in another tab would be
/// sent back to the change-password screen forever.
/// </summary>
public sealed class GetCurrentSessionHandler
{
    private readonly IdentityDbContext _db;

    public GetCurrentSessionHandler(IdentityDbContext db)
    {
        _db = db;
    }

    public async Task<SessionDto> Handle(CurrentUser user, CancellationToken ct)
    {
        // No permission check and no audit entry. Every authenticated user may ask who they are, and
        // the front end calls this on every page load -- auditing it would bury every real event under
        // thousands of rows saying nothing happened.
        if (!Guid.TryParse(user.Id, out var accountId))
            throw new AppException("Not authenticated.", 401);

        var account = await _db.UserAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == accountId, ct);

        // A valid cookie for an account that no longer exists, or was suspended since login, is 401 --
        // not 404 and not a stale 200. The cookie outlives the account state it was issued from, and
        // this is the check that closes that window. Without it a suspended user keeps working until
        // their cookie expires.
        if (account is null || account.Status != AccountStatus.Active)
            throw new AppException("Not authenticated.", 401);

        return SessionClaims.ToSessionDto(account);
    }
}
