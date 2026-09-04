using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

public sealed class ReactivateAccountantHandler
{
    private readonly IdentityDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public ReactivateAccountantHandler(
        IdentityDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
    }

    public async Task<AccountantDetailDto> Handle(
        AccountIdRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ReactivateAccountant", ct: ct);

        // No RequireNotSelf here, unlike suspend and demote. A suspended Admin cannot make this call --
        // they cannot log in -- so self-reactivation is already impossible, and a guard against it would
        // be dead code that reads like a live rule.
        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        var account = await AccountInvariants.LoadAccountantAsync(_db, request.UserAccountId, ct);

        // Only Suspended reactivates. An Invited account is NOT reactivatable: it has a null password
        // hash, so flipping it to Active produces a row that violates ck_user_accounts_status and, worse
        // if the check were absent, an Active account that can never authenticate and that no
        // invitation flow will ever finish. The fix for a stale invitation is a new invitation.
        if (account.Status != AccountStatus.Suspended)
            throw new AppException(
                account.Status == AccountStatus.Invited
                    ? "That account has not accepted its invitation yet, so it cannot be reactivated."
                    : "That account is already active.",
                422);

        var before = IdentityMapper.ToAuditSnapshot(account);
        account.Status = AccountStatus.Active;

        // Clear both. Someone suspended while locked out would otherwise come back Active and still
        // unable to log in, with nothing in the response to say why.
        account.FailedLoginCount = 0;
        account.LockoutExpiresAt = null;

        await _db.SaveChangesAsync(ct);

        // No RequireAnActiveAdminRemainsAsync. Reactivation only ever ADDS an Active account, so the
        // invariant cannot be broken by it -- calling it here would pass unconditionally.

        // No notification. The person cannot see in-app notifications while suspended and there is no
        // emailed event for reactivation; whoever asked for it will be told by the Admin who did it.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.AccountReactivated,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            null,
            Before: before,
            After: IdentityMapper.ToAuditSnapshot(account)), ct);

        await _transaction.CommitAsync(ct);
        return IdentityMapper.ToDetailDto(account);
    }
}
