using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Employees.Application;

/// <summary>
/// Tells a Customer's own Admins that their staff list changed. One file, called by registration and
/// departure, because the recipient rule -- "the Active Customer Admins of this Customer" -- is the part
/// that is easy to get subtly wrong and must not exist in two copies.
///
/// WHO IS NOT A RECIPIENT, and why:
///
/// - Accountants. They already see every Customer's Employees and did not ask to hear about each change;
///   notifying the whole Office on every registration is how a notification list becomes something people
///   stop reading.
/// - The person the event is about. A registration notification to somebody who has no account yet has
///   nowhere to go, and a departure notification to the departed is cruel and useless in equal measure.
/// - The caller. Notifications' own rule E drops a notification whose recipient is the current user, so
///   this is NOT filtered here: a Customer Admin registering somebody does not get told about their own
///   action, and duplicating that rule in this slice would mean two places to change it and one of them
///   left behind.
/// </summary>
internal static class EmployeeNotifications
{
    /// <summary>Matches EmployeeInvariants: batch up to the contract's cap, never refuse above it.</summary>
    private const int AccountLookupBatchSize = 500;

    internal static async Task NotifyCustomerAdminsAsync(
        EmployeesDbContext db,
        IIdentityApi identity,
        INotificationApi notifications,
        Guid customerId,
        string eventKind,
        string title,
        string body,
        CancellationToken ct)
    {
        // The Employee half of "Active Customer Admin" is this slice's; the role and the account status are
        // Identity's. Joining the two tables would make the two schemas one schema, which is why this is
        // two queries rather than the one it looks like it should be.
        var accountIds = await db.Employees
            .AsNoTracking()
            .Where(employee => employee.CustomerId == customerId
                            && employee.Status == EmployeeStatus.Active
                            && employee.UserAccountId != null)
            .Select(employee => employee.UserAccountId!.Value)
            .ToListAsync(ct);

        if (accountIds.Count == 0)
            return;

        var recipients = new List<NotificationRequest>();
        for (var offset = 0; offset < accountIds.Count; offset += AccountLookupBatchSize)
        {
            var batch = accountIds.Skip(offset).Take(AccountLookupBatchSize).ToList();
            foreach (var account in (await identity.FindManyAsync(batch, ct)).Values)
            {
                if (account.Role != UserRole.CustomerAdmin)
                    continue;

                // Invited counts, Suspended does not. An Admin who has not accepted their invitation yet
                // will read this the day they do; a suspended one is somebody whose access was
                // deliberately revoked, and queueing notifications for them is queueing work nobody will
                // ever collect.
                if (account.Status == "Suspended")
                    continue;

                recipients.Add(new NotificationRequest(
                    account.Id.ToString(), eventKind, title, body));
            }
        }

        if (recipients.Count == 0)
            return;

        // NotifyManyAsync, not a loop of NotifyAsync: one round trip and one SaveChanges, inside the
        // caller's transaction. A Customer with four Admins would otherwise be four of each.
        await notifications.NotifyManyAsync(recipients, ct);
    }
}
