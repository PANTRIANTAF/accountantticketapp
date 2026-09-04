using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Employees.Application;

/// <summary>
/// One file, called by all three operations that can leave a Customer with no active Customer Admin:
/// demoting one (set-role), departing one, and suspending one's account. Copying the guard into three
/// handlers is how two of them stay correct and the third drifts.
/// </summary>
internal static class EmployeeInvariants
{
    /// <summary>
    /// The cap IIdentityApi.FindManyAsync enforces. This guard BATCHES up to it rather than refusing
    /// above it: a Customer larger than one batch must still be able to depart somebody, and the
    /// alternative -- a 422 telling them to phone the accounting office -- froze the operation entirely.
    ///
    /// Batching is safe here in a way silent truncation would not be, because every batch is counted.
    /// Do not "optimise" this by stopping at the first batch that contains another active Admin unless
    /// you also keep the exclusion check below, which needs the TARGET's own row.
    /// </summary>
    private const int AccountLookupBatchSize = 500;

    /// <summary>
    /// Rejects an operation that would leave <paramref name="customerId"/> with no Active CustomerAdmin
    /// whose Employee record is also Active. <paramref name="excludedAccountId"/> is the account about
    /// to be demoted, suspended, or departed.
    ///
    /// APPROACH -- read this before changing it. This guard EXCLUDES the target from a count taken
    /// BEFORE the change. Identity's equivalent guard does the opposite: it mutates first and counts
    /// after, seeing its own pending state. The difference is forced, not stylistic -- the mutation here
    /// happens in another slice through IIdentityApi, so there is no locally tracked entity whose
    /// pending state a count could observe. A builder who has read both plans and mixes the two gets a
    /// guard that counts the target as still qualifying and therefore always passes.
    ///
    /// It must be called INSIDE the handler's transaction, so a rejection rolls back the IIdentityApi
    /// call too.
    ///
    /// It applies to Accountant callers as well. An AccountantUser demoting a Customer's last Admin
    /// creates the same hole; matrix section 4's "only an Accountant can resolve such a situation"
    /// describes the recovery path, not an exemption.
    ///
    /// CONCURRENCY -- known and accepted. Two callers demoting two different Customer Admins in separate
    /// transactions can both pass a count taken before either commits, and reach zero. Under READ
    /// COMMITTED this is a real interleaving, and it is worse here than in Identity because the count
    /// reads user_accounts, a table this transaction never locks. Unlike the Accountant case it IS
    /// recoverable, by an Accountant, which is why it is recorded rather than solved with a lock.
    /// </summary>
    internal static async Task RequireAnotherActiveCustomerAdminAsync(
        EmployeesDbContext db,
        IIdentityApi identity,
        Guid customerId,
        Guid excludedAccountId,
        CancellationToken ct)
    {
        // Every accounted, Active Employee of this Customer. The Employee half of "Active Customer
        // Admin" is this slice's; the role and the account status are Identity's, and joining the two
        // tables would make the two schemas one schema.
        var accountIds = await db.Employees
            .AsNoTracking()
            .Where(employee => employee.CustomerId == customerId
                            && employee.Status == EmployeeStatus.Active
                            && employee.UserAccountId != null)
            .Select(employee => employee.UserAccountId!.Value)
            .ToListAsync(ct);

        // Batched, not capped. Every account is looked at, so the count is exact at any Customer size;
        // the only cost is one extra round trip per 500 accounted Employees. Silently taking the first
        // batch would let the count reach zero undetected, which is the unrecoverable state this whole
        // guard exists to prevent -- so the loop must cover all of them or none of this is sound.
        var accounts = new Dictionary<Guid, AccountSummary>(accountIds.Count);
        for (var offset = 0; offset < accountIds.Count; offset += AccountLookupBatchSize)
        {
            var batch = accountIds.Skip(offset).Take(AccountLookupBatchSize).ToList();
            foreach (var pair in await identity.FindManyAsync(batch, ct))
                accounts[pair.Key] = pair.Value;
        }

        // An operation that cannot reduce the count cannot be the one that leaves zero. Without this, a
        // Customer already sitting at zero active Customer Admins -- which an Accountant is supposed to be
        // able to fix -- could not depart or suspend a plain Employee either, and the guard would have
        // turned one broken Customer into a frozen one.
        if (!accounts.TryGetValue(excludedAccountId, out var target)
            || target.Role != UserRole.CustomerAdmin
            || !target.IsActive)
            return;

        // All three conditions. Counting CustomerAdmins of any status passes when the only one left is
        // Suspended -- nobody can log in, and only an Accountant can dig the Customer out.
        var remaining = accounts.Values.Count(account =>
            account.Role == UserRole.CustomerAdmin
            && account.IsActive
            && account.Id != excludedAccountId);

        // 422, not 403: the caller has the role, the data's state forbids the operation. A 403 would
        // suggest re-authenticating as somebody more powerful, which would not help.
        if (remaining == 0)
            throw new AppException(
                "This Customer must always have at least one active Customer Admin.", 422);
    }

    /// <summary>
    /// Blocks a Customer Admin from acting on their own role or account status. Applies to set-role,
    /// depart, and suspend-account -- NOT to update-own-contact, which is the endpoint for acting on
    /// yourself and is explicitly permitted.
    ///
    /// It compares the caller's id against employee.UserAccountId, NOT employee.Id. user.Id is an
    /// ACCOUNT id; compared to an Employee id it never matches, so the guard silently never fires -- and
    /// it looks entirely correct in review. That is the single most likely way to build this check so
    /// that it does nothing.
    ///
    /// One place, one format: Guid.ToString() on both sides, here only. A "D"-format string compared to
    /// an "N"-format one never matches, with the same silent result.
    ///
    /// It needs no role check. An Accountant has no Employee record, so it can only ever fire for a
    /// Customer-side caller.
    /// </summary>
    internal static void RequireNotSelf(Employee employee, CurrentUser user)
    {
        if (employee.UserAccountId is { } accountId
            && string.Equals(accountId.ToString(), user.Id, StringComparison.Ordinal))
            throw new AppException("You cannot change your own role or account status.", 422);
    }

    /// <summary>
    /// The caller's account id as a Guid, for the second filter an Employee-role read needs.
    ///
    /// A malformed user.Id yields Guid.Empty, which matches no row, so the read fails CLOSED with a 404
    /// rather than opening up to every colleague. Parsed once, here, so no LINQ query ever contains
    /// .ToString() -- Npgsql either fails to translate that or translates it to a cast that defeats the
    /// index, and a "D"-format Guid compared against an "N"-format string silently 404s a person's own
    /// record.
    /// </summary>
    internal static Guid AccountIdOf(CurrentUser user) =>
        Guid.TryParse(user.Id, out var parsed) ? parsed : Guid.Empty;
}
