using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Infrastructure;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

/// <summary>AccountantUser to AccountantAdmin.</summary>
public sealed class PromoteAccountantHandler
{
    private readonly IdentityDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public PromoteAccountantHandler(
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
        await _permissions.RequireAsync(user, "PromoteAccountant", ct: ct);

        // No RequireNotSelf. The caller is already an Admin -- promoting themselves is caught by the
        // already-an-Admin guard below, which gives a clearer answer than a self-action rule would.
        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        var account = await AccountInvariants.LoadAccountantAsync(_db, request.UserAccountId, ct);

        if (account.Role == UserRole.AccountantAdmin)
            throw new AppException("That account is already an Accountant Admin.", 422);

        // Deliberately allowed for a Suspended or Invited account. The role is what they will be when
        // they can act; it is not itself permission to act, which is what Status governs. Blocking this
        // would mean an invited Admin has to be invited as a User and promoted after their first login.
        var before = IdentityMapper.ToAuditSnapshot(account);
        account.Role = UserRole.AccountantAdmin;

        await _db.SaveChangesAsync(ct);

        // No invariant check: promotion can only increase the number of Admins.
        //
        // The promoted user's own cookie still says AccountantUser, and that is accepted. The role is a
        // claim taken at login, so the new permission arrives when they next sign in -- there is no
        // mechanism to rewrite another person's cookie, and inventing one would mean tracking sessions
        // server-side, which this design does not do.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.AccountantPromoted,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            null,
            Before: before,
            After: IdentityMapper.ToAuditSnapshot(account)), ct);

        await _transaction.CommitAsync(ct);
        return IdentityMapper.ToDetailDto(account);
    }
}
