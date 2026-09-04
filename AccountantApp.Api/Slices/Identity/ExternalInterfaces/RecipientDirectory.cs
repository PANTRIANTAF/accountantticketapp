using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.ExternalInterfaces;

/// <summary>
/// The one INVERTED interface in the system, and the direction is deliberate.
///
/// Notifications DEFINES IRecipientDirectory; Identity IMPLEMENTS it. The obvious arrangement --
/// Notifications calling IIdentityApi -- would make Notifications depend on Identity, and Identity
/// already depends on Notifications to queue invitation and reset emails. That is a cycle, and in a
/// single assembly a cycle does not fail to compile: it just becomes a constructor graph that
/// StackOverflows on the first request.
///
/// So the arrow is flipped. Notifications knows only "something can tell me an address for a user id",
/// which is the least it can know and still send mail.
/// </summary>
internal sealed class RecipientDirectory : IRecipientDirectory
{
    private readonly IdentityDbContext _db;

    public RecipientDirectory(IdentityDbContext db)
    {
        _db = db;
    }

    public async Task<Recipient?> FindAsync(string userAccountId, CancellationToken ct)
    {
        // The id arrives as a string because that is what a notification row stores. An unparseable
        // value is null, not an exception: the drainer runs in the background over rows it did not
        // validate, and one malformed id must not stop the queue.
        if (!Guid.TryParse(userAccountId, out var accountId))
            return null;

        var account = await _db.UserAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == accountId, ct);

        if (account is null)
            return null;

        return new Recipient(
            account.Id.ToString(),
            // LoginEmail, as the person typed it -- not NormalizedLoginEmail. Some mail systems treat the
            // local part as case-sensitive, and the normalized column exists for lookup, not for delivery.
            account.LoginEmail,
            account.DisplayName,

            // Invited counts as NOT active, and that is what makes invitation emails a special case rather
            // than something this method can decide. The drainer skips inactive recipients so a suspended
            // person stops receiving mail -- but an invitee is Invited by definition, so the invitation
            // itself would be skipped. The drainer's invitation allow-list handles that; do not "fix" it
            // by reporting Invited as active here, which would silently resume mail to suspended accounts
            // too.
            account.Status == AccountStatus.Active);
    }
}
