using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.Application.Handlers;

public sealed class SuspendAccountantHandler
{
    private readonly IdentityDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly INotificationApi _notifications;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public SuspendAccountantHandler(
        IdentityDbContext db,
        IPermissionChecker permissions,
        INotificationApi notifications,
        IRequestTransaction transaction,
        IAuditApi audit)
    {
        _db = db;
        _permissions = permissions;
        _notifications = notifications;
        _transaction = transaction;
        _audit = audit;
    }

    public async Task<AccountantDetailDto> Handle(
        AccountIdRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "SuspendAccountant", ct: ct);

        // Before any database work: an Admin must not suspend themselves. Cheap, and it makes the
        // most common way to lock the Office out impossible.
        AccountInvariants.RequireNotSelf(request.UserAccountId, user);

        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        var account = await AccountInvariants.LoadAccountantAsync(_db, request.UserAccountId, ct);

        if (account.Status == AccountStatus.Suspended)
            throw new AppException("That account is already suspended.", 422);

        var before = IdentityMapper.ToAuditSnapshot(account);
        account.Status = AccountStatus.Suspended;

        // Clear the lockout as the account is suspended. Suspended already blocks login, so a lingering
        // lockout timestamp is stale state that outlives the reason for it and confuses the next person
        // reading the row.
        account.FailedLoginCount = 0;
        account.LockoutExpiresAt = null;

        await _db.SaveChangesAsync(ct);

        // AFTER SaveChangesAsync, INSIDE the transaction. Suspending the last Active Admin leaves an
        // Office nobody can administer, and no role exists that could undo it. The count must run after
        // the write so it sees this change; before the write it would always find the very Admin it is
        // about to remove and pass. Throwing here reaches no CommitAsync, so DisposeAsync rolls the
        // suspension back.
        await AccountInvariants.RequireAnActiveAdminRemainsAsync(_db, ct);

        await _notifications.NotifyAsync(new NotificationRequest(
            account.Id.ToString(),
            NotificationEvents.AccountSuspended,
            "Your account has been suspended",
            "Your account has been suspended. Contact your administrator if you believe this is a mistake."), ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.AccountSuspended,
            AuditTargets.UserAccount,
            account.Id.ToString(),
            null,
            Before: before,
            After: IdentityMapper.ToAuditSnapshot(account)), ct);

        await _transaction.CommitAsync(ct);
        return IdentityMapper.ToDetailDto(account);
    }
}
