using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.Application;

/// <summary>
/// Both guards live here rather than being copied into each handler. Both are easy to write in a way
/// that looks right and is not.
/// </summary>
public static class AccountInvariants
{
    /// <summary>
    /// At least one Active AccountantAdmin must always exist.
    ///
    /// MUST be called INSIDE the handler's transaction and AFTER SaveChangesAsync has written the
    /// change. CountAsync is a database query, not a change-tracker query: it sees what has been
    /// written to the connection, so before SaveChangesAsync the pending mutation is not there and
    /// the count always finds the very Admin it is about to remove. That version compiles, looks
    /// correct, and the invariant is simply absent.
    ///
    /// Counting after the save works because the handler holds a transaction on a single shared
    /// connection, so this query sees the slice's own uncommitted write. Throwing then propagates
    /// out of the handler, CommitAsync is never reached, and RequestTransaction.DisposeAsync rolls
    /// the change back -- which is why the row must be unchanged afterwards. Do not catch this to
    /// "clean up": the rollback is the cleanup.
    /// </summary>
    public static async Task RequireAnActiveAdminRemainsAsync(
        IdentityDbContext db,
        CancellationToken ct)
    {
        // The condition is Active AND AccountantAdmin. Counting Admins of any status passes when the
        // only remaining Admin is Suspended or Invited -- nobody can log in, and the only role that
        // can fix it is the one that no longer exists. That is the unrecoverable state this exists for.
        var activeAdmins = await db.UserAccounts.CountAsync(
            account => account.Role == UserRole.AccountantAdmin
                       && account.Status == AccountStatus.Active,
            ct);

        // 422, not 403: the caller has the role, and the operation is refused because of the state of
        // the data. A 403 would suggest re-authenticating as somebody more powerful, and there is
        // nobody more powerful -- Accountant Admin is the ceiling.
        if (activeAdmins == 0)
            throw new AppException("At least one active Accountant Admin must remain.", 422);
    }

    /// <summary>
    /// Loads an Accountant by id, or 404.
    ///
    /// The role filter is part of the LOOKUP, not a check performed afterwards. The four
    /// AccountantAdmin-only endpoints administer the Office; pointing one at a CustomerAdmin's id must
    /// be a 404, not a successful suspension of a client's user by an endpoint that has no notion of
    /// Customer scope and would never think to apply it. Shared by all four, so the filter cannot be
    /// present in three of them and quietly missing from the fourth.
    /// </summary>
    public static async Task<UserAccount> LoadAccountantAsync(
        IdentityDbContext db,
        Guid userAccountId,
        CancellationToken ct)
    {
        // Tracked, not AsNoTracking: every caller mutates what comes back.
        return await db.UserAccounts.FirstOrDefaultAsync(
                   account => account.Id == userAccountId
                              && (account.Role == UserRole.AccountantAdmin
                                  || account.Role == UserRole.AccountantUser), ct)
               // Same message for "no such id" and "that id is a Customer-side account". An Admin can
               // list every Accountant, so this discloses nothing they cannot already see, and it avoids
               // turning the endpoint into a way to confirm that some id belongs to a client's user.
               ?? throw new AppException("Accountant not found.", 404);
    }

    /// <summary>
    /// No self-action on one's own role or status. Applies to suspend and demote. Does NOT apply to
    /// changing one's own password, which is explicitly permitted.
    /// </summary>
    public static void RequireNotSelf(Guid targetId, CurrentUser user)
    {
        // Compare the claim with the target id. Do not re-derive the caller's identity from the
        // database: the claim IS the authenticated identity, and a second lookup is another chance to
        // compare the wrong two things.
        //
        // Both sides go through Guid.ToString() in this one place, so the bug where a "D"-formatted
        // Guid is compared to an "N"-formatted one and never matches -- silently turning the guard
        // off -- cannot happen.
        var callerId = Guid.TryParse(user.Id, out var parsed) ? parsed.ToString() : user.Id;

        if (string.Equals(targetId.ToString(), callerId, StringComparison.OrdinalIgnoreCase))
            throw new AppException("You cannot change your own role or status.", 422);
    }
}
