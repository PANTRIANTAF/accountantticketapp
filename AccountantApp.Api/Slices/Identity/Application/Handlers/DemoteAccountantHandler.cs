using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Infrastructure;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

/// <summary>AccountantAdmin to AccountantUser. The one role change that can break the Office.</summary>
public sealed class DemoteAccountantHandler
{
    private readonly IdentityDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public DemoteAccountantHandler(
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
        await _permissions.RequireAsync(user, "DemoteAccountant", ct: ct);

        // Self-demotion is refused outright, before the invariant check has a say. The last Admin
        // demoting themselves is caught either way, but an Admin demoting themselves while a second
        // Admin exists would pass the invariant and silently strip their own access mid-session -- with
        // a cookie that still says AccountantAdmin, so the next few requests succeed and then stop.
        AccountInvariants.RequireNotSelf(request.UserAccountId, user);

        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        var account = await AccountInvariants.LoadAccountantAsync(_db, request.UserAccountId, ct);

        if (account.Role != UserRole.AccountantAdmin)
            throw new AppException("That account is not an Accountant Admin.", 422);

        var before = IdentityMapper.ToAuditSnapshot(account);
        account.Role = UserRole.AccountantUser;

        await _db.SaveChangesAsync(ct);

        // AFTER the save, INSIDE the transaction -- the same rule as suspend, for the same reason.
        // Demoting the last Active Admin leaves nobody who can invite, suspend, or promote, and no role
        // that can undo it. Before the save, the count would still see this account as an Admin and pass.
        await AccountInvariants.RequireAnActiveAdminRemainsAsync(_db, ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.AccountantDemoted,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            null,
            Before: before,
            After: IdentityMapper.ToAuditSnapshot(account)), ct);

        await _transaction.CommitAsync(ct);
        return IdentityMapper.ToDetailDto(account);
    }
}
