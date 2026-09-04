using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.ExternalInterfaces;

internal sealed class IdentityApi : IIdentityApi
{
    /// <summary>
    /// Cap on FindManyAsync. Above it, throw: an uncapped IN clause with tens of thousands of ids is a
    /// query that will one day take the database down, and silently truncating the list would give the
    /// caller a dictionary that is quietly missing rows.
    /// </summary>
    private const int MaximumBatchSize = 500;

    private const int DisplayNameMaximumLength = 200;

    private readonly IdentityDbContext _db;
    private readonly ITokenIssuing _tokens;
    private readonly TokenLinks _links;
    private readonly INotificationApi _notifications;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public IdentityApi(
        IdentityDbContext db,
        ITokenIssuing tokens,
        TokenLinks links,
        INotificationApi notifications,
        IRequestTransaction transaction,
        IAuditApi audit)
    {
        _db = db;
        _tokens = tokens;
        _links = links;
        _notifications = notifications;
        _transaction = transaction;
        _audit = audit;
    }

    // --- Reads ---

    public async Task<AccountSummary?> FindAsync(Guid userAccountId, CancellationToken ct = default)
    {
        var account = await _db.UserAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == userAccountId, ct);

        // Null for an unknown id, not an exception. A caller resolving a name for a display row should
        // not have to handle an exception for a row that has been superseded.
        return account is null ? null : ToSummary(account);
    }

    public async Task<IReadOnlyDictionary<Guid, AccountSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> userAccountIds, CancellationToken ct = default)
    {
        if (userAccountIds.Count == 0)
            return new Dictionary<Guid, AccountSummary>();

        if (userAccountIds.Count > MaximumBatchSize)
            throw new InvalidOperationException(
                $"FindManyAsync accepts at most {MaximumBatchSize} ids; {userAccountIds.Count} were supplied.");

        // Distinct before the query: a page of tickets assigned to the same person sends the same id
        // many times, and the dictionary would key it once anyway.
        var ids = userAccountIds.Distinct().ToList();

        return await _db.UserAccounts
            .AsNoTracking()
            .Where(account => ids.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id, ToSummary, ct);
    }

    public async Task<bool> IsActiveAsync(Guid userAccountId, CancellationToken ct = default)
    {
        // A single EXISTS with the status in the predicate -- not a fetch followed by a check. Nothing is
        // cached here: this is what the pickup queue uses to decide an assignment is stranded, so a stale
        // true would hide the very thing the caller is asking about.
        //
        // An unknown id is false, not an error: fail closed, matching ICustomerApi.IsActiveAsync.
        return await _db.UserAccounts
            .AsNoTracking()
            .AnyAsync(account => account.Id == userAccountId
                                 && account.Status == AccountStatus.Active, ct);
    }

    public async Task<AccountSummary?> FindByEmployeeAsync(
        Guid employeeId, CancellationToken ct = default)
    {
        // Backed by uq_user_accounts_employee, which is unique -- so FirstOrDefault cannot be hiding a
        // second account for the same Employee.
        var account = await _db.UserAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.EmployeeId == employeeId, ct);

        return account is null ? null : ToSummary(account);
    }

    public async Task<IReadOnlyList<AccountSummary>> ListAccountantsAsync(
        bool activeOnly = true, CancellationToken ct = default)
    {
        var query = _db.UserAccounts
            .AsNoTracking()
            .Where(account => account.Role == UserRole.AccountantAdmin
                              || account.Role == UserRole.AccountantUser);

        // activeOnly defaults to true because the main caller is an assignee picker, and offering a
        // suspended Accountant as an assignee creates a Ticket that is stranded the moment it is
        // assigned. A caller that wants everyone -- an admin screen -- passes false explicitly.
        if (activeOnly)
            query = query.Where(account => account.Status == AccountStatus.Active);

        var accounts = await query.OrderBy(account => account.DisplayName).ToListAsync(ct);
        return accounts.Select(ToSummary).ToList();
    }

    // --- Writes ---

    public async Task<Guid> InviteEmployeeAccountAsync(
        InviteEmployeeAccount request, CancellationToken ct = default)
    {
        // Structural guard, and it lives here as well as in Employees because this method is how the
        // "no Customer-side actor creates an Accountant account" rule could be circumvented.
        // InvalidOperationException, not AppException: a caller passing an Accountant role here is a bug
        // in that slice, not something a user typed.
        if (request.Role is not (UserRole.CustomerAdmin or UserRole.Employee))
            throw new InvalidOperationException(
                $"An Employee account must be CustomerAdmin or Employee, not {request.Role}.");

        if (request.EmployeeId == Guid.Empty)
            throw new InvalidOperationException("An Employee account requires an EmployeeId.");
        if (request.CustomerId == Guid.Empty)
            throw new InvalidOperationException("An Employee account requires a CustomerId.");

        // Enlist, never Begin. The caller's onboarding operation spans several slices, and opening a
        // second transaction here would let the account survive a failure in the step after it.
        await _transaction.EnlistAsync(_db, ct);

        var loginEmail = EmailNormalization.Require(request.LoginEmail);
        var normalizedEmail = EmailNormalization.Normalize(loginEmail);

        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (displayName.Length == 0)
            throw new InvalidOperationException("An Employee account requires a display name.");
        if (displayName.Length > DisplayNameMaximumLength)
            displayName = displayName[..DisplayNameMaximumLength];

        if (await _db.UserAccounts.AnyAsync(item => item.NormalizedLoginEmail == normalizedEmail, ct))
            throw new Shared.Errors.AppException(
                "An account with that email address already exists.", 409);

        // One Employee has at most one account, held by uq_user_accounts_employee. Checked here too so
        // the caller gets a 409 rather than a unique-violation 500.
        if (await _db.UserAccounts.AnyAsync(item => item.EmployeeId == request.EmployeeId, ct))
            throw new Shared.Errors.AppException(
                "That employee already has an account.", 409);

        var now = DateTimeOffset.UtcNow;
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            LoginEmail = loginEmail,
            NormalizedLoginEmail = normalizedEmail,
            PasswordHash = null,
            DisplayName = displayName,
            Role = request.Role,
            EmployeeId = request.EmployeeId,
            CustomerId = request.CustomerId,
            Status = AccountStatus.Invited,
            MustChangePassword = false,
            CreatedAt = now
        };
        _db.UserAccounts.Add(account);

        var rawToken = _tokens.GenerateRawToken();
        _db.Tokens.Add(new UserAccountToken
        {
            Id = Guid.NewGuid(),
            UserAccountId = account.Id,
            Purpose = TokenPurpose.Invitation,
            TokenHash = _tokens.HashToken(rawToken),
            ExpiresAt = now.Add(TokenPurpose.InvitationLifetime),
            CreatedAt = now
        });

        await _db.SaveChangesAsync(ct);

        // EmployeeInvited, NOT Invited -- InviteAccountantHandler uses Invited. Two kinds for what looks
        // like one operation, because the audiences differ: an Accountant is joining the Office, an
        // Employee is joining their employer's portal.
        //
        // Both kinds must be in the outbox drainer's invitation allow-list. An invitee is not Active yet,
        // so the suspended-recipient skip would otherwise swallow the email -- and if only Invited is
        // allow-listed, Accountants get invited and Employees silently never do. That asymmetry is far
        // harder to notice than a total failure, because the feature works when the person testing it is
        // an Accountant.
        await _notifications.NotifyAsync(new NotificationRequest(
            account.Id.ToString(),
            NotificationEvents.EmployeeInvited,
            "You have been invited",
            "An account has been created for you. Check your email to set your password.",
            EmailBody: $"Hello {account.DisplayName},\n\n"
                       + "An account has been created for you. Use this link to set your password:\n"
                       + $"{_links.AcceptInvitation(rawToken)}\n\n"
                       + $"The link is valid for {TokenPurpose.InvitationLifetime.TotalDays:0} days."), ct);

        // This slice audits the account it created; the caller separately audits its own EmployeeInvited.
        // Two entries for one user action is correct -- two things happened, in two slices.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.AccountInvited,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            account.CustomerId,
            After: new { account.LoginEmail, account.DisplayName, account.Role, account.Status }), ct);

        // No CommitAsync. The caller commits.
        return account.Id;
    }

    public async Task SuspendAccountAsync(Guid userAccountId, CancellationToken ct = default)
    {
        await _transaction.EnlistAsync(_db, ct);

        var account = await _db.UserAccounts
            .FirstOrDefaultAsync(item => item.Id == userAccountId, ct);

        // Unknown id is a no-op, and already-suspended is a no-op. This method asserts a target state;
        // see the interface. A departure whose account was already suspended for an unrelated reason
        // must still be recordable.
        if (account is null || account.Status == AccountStatus.Suspended)
            return;

        var before = IdentityMapper.ToAuditSnapshot(account);
        account.Status = AccountStatus.Suspended;
        account.FailedLoginCount = 0;
        account.LockoutExpiresAt = null;

        await _db.SaveChangesAsync(ct);

        // The last-Active-Admin invariant is checked here too. This path is reached by Employees, which
        // deals with Customer-side accounts and would never knowingly touch an Accountant -- but the id
        // is just a Guid, and a wrong one that happens to be the last Admin must not get through a door
        // the HTTP endpoint has locked.
        await AccountInvariants.RequireAnActiveAdminRemainsAsync(_db, ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.AccountSuspended,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            account.CustomerId,
            Before: before,
            After: IdentityMapper.ToAuditSnapshot(account)), ct);
    }

    public async Task ReactivateAccountAsync(Guid userAccountId, CancellationToken ct = default)
    {
        await _transaction.EnlistAsync(_db, ct);

        var account = await _db.UserAccounts
            .FirstOrDefaultAsync(item => item.Id == userAccountId, ct);

        if (account is null || account.Status != AccountStatus.Suspended)
            return;

        var before = IdentityMapper.ToAuditSnapshot(account);

        // An account is restored to the state it can actually be used in, which is NOT always Active.
        //
        // Suspension flattens Invited and Active into one status, so by the time we get here the previous
        // state is gone and only the password tells us what it was. A person who was invited, never
        // accepted, and was then suspended -- which a departure does automatically -- has no password
        // hash. Flipping them to Active produces an account that passes every constraint and can never be
        // logged into: Verify(null, password) fails, so the answer is "invalid email or password" forever,
        // and no invitation flow will touch them again because Active accounts are not invitees.
        //
        // Returning them to Invited instead keeps them re-invitable. This matters most for the reinstate
        // path in Employees, where departing and un-departing a never-accepted invitee would otherwise
        // destroy their only route in.
        account.Status = account.PasswordHash is null
            ? AccountStatus.Invited
            : AccountStatus.Active;
        account.FailedLoginCount = 0;
        account.LockoutExpiresAt = null;

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditEntry(
            AuditActions.AccountReactivated,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            account.CustomerId,
            Before: before,
            After: IdentityMapper.ToAuditSnapshot(account)), ct);
    }

    public async Task ChangeLoginEmailAsync(
        Guid userAccountId, string loginEmail, CancellationToken ct = default)
    {
        await _transaction.EnlistAsync(_db, ct);

        var account = await _db.UserAccounts
            .FirstOrDefaultAsync(item => item.Id == userAccountId, ct)
            ?? throw new Shared.Errors.AppException("Account not found.", 404);

        // Same two-sided reasoning as SetCustomerSideRoleAsync: Employees is the only caller and deals
        // only in Customer-side accounts, so an Accountant target is a wrong id in that slice rather than
        // anything a user did. Accountants change their own contact details through Identity's own
        // endpoints.
        if (account.IsAccountant)
            throw new InvalidOperationException(
                "ChangeLoginEmailAsync must not be used on an Accountant account.");

        // Validated and normalized by the same helper every other write uses. A second, laxer validator
        // here would let an address in through this door that login could never match.
        var trimmed = EmailNormalization.Require(loginEmail);
        var normalized = EmailNormalization.Normalize(trimmed);

        // No-op when nothing changes, including a pure case change: writing LoginEmail alone would leave
        // NormalizedLoginEmail correct anyway, but the audit entry would claim a change that did not
        // happen. A case-only edit -- "Maria.P@acme.example" for "maria.p@acme.example" -- is still worth
        // storing, so it is compared on the display form.
        if (string.Equals(account.LoginEmail, trimmed, StringComparison.Ordinal))
            return;

        // Excludes this account, so re-saving somebody's own address is not a conflict. Without the
        // Id check a case-only correction would 409 against the row being corrected.
        if (await _db.UserAccounts.AnyAsync(
                item => item.NormalizedLoginEmail == normalized && item.Id != account.Id, ct))
            throw new Shared.Errors.AppException(
                "An account with that email address already exists.", 409);

        var before = IdentityMapper.ToAuditSnapshot(account);
        account.LoginEmail = trimmed;
        account.NormalizedLoginEmail = normalized;

        // PasswordHash, Status, EmailConfirmedAt and MustChangePassword are untouched on purpose. This
        // changes which address signs in, not whether the person is verified or what they know. Forcing a
        // re-confirmation here would lock out somebody whose address was corrected by the Office because
        // they could not receive mail at the old one -- the exact case this operation exists for.
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.LoginEmailChanged,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            account.CustomerId,
            Before: before,
            After: IdentityMapper.ToAuditSnapshot(account)), ct);
    }

    public async Task SetCustomerSideRoleAsync(
        Guid userAccountId, UserRole role, CancellationToken ct = default)
    {
        // Guard one: the requested role. Employees passing an Accountant role is a wrong-role bug.
        if (role is not (UserRole.CustomerAdmin or UserRole.Employee))
            throw new InvalidOperationException(
                $"SetCustomerSideRoleAsync accepts CustomerAdmin or Employee, not {role}.");

        await _transaction.EnlistAsync(_db, ct);

        var account = await _db.UserAccounts
            .FirstOrDefaultAsync(item => item.Id == userAccountId, ct)
            ?? throw new Shared.Errors.AppException("Account not found.", 404);

        // Guard two: the target. Employees passing the wrong id is a different bug from passing the
        // wrong role, which is why both checks exist rather than one. Demoting an Accountant through this
        // method would be a Customer-side actor modifying an Accountant account -- rejected outright, not
        // silently ignored.
        if (account.IsAccountant)
            throw new InvalidOperationException(
                "SetCustomerSideRoleAsync must not be used on an Accountant account.");

        if (account.Role == role)
            return;

        var before = IdentityMapper.ToAuditSnapshot(account);
        account.Role = role;
        await _db.SaveChangesAsync(ct);

        // Reuses EmployeeEdited: the audit vocabulary has no CustomerSideRoleChanged, and the Before/After
        // snapshots carry the role, so the transition is fully recorded. Do not invent a new action name
        // here -- AuditApi checks entry.Action against AuditActions.All and rejects anything not declared
        // there. Note that is a RUNTIME check on the logging call, not a startup one: an invented name
        // compiles, starts, and fails only when this branch is actually taken. Add the constant to
        // AuditActions first if a new name is genuinely needed.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.EmployeeEdited,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            account.CustomerId,
            Before: before,
            After: IdentityMapper.ToAuditSnapshot(account)), ct);
    }

    private static AccountSummary ToSummary(UserAccount account) => new(
        account.Id,
        account.DisplayName,
        account.LoginEmail,
        account.Role,
        account.Status);
}
