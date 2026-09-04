using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// Restores a suspended Employee's access. AA, AU, and CA for their own Customer.
/// </summary>
public sealed class ReactivateEmployeeAccountHandler
{
    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IIdentityApi _identity;

    public ReactivateEmployeeAccountHandler(
        EmployeesDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IIdentityApi identity)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _identity = identity;
    }

    public async Task<MarkedResultDto> Handle(
        EmployeeIdRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ReactivateEmployeeAccount", ct: ct);

        await using var scope = await _transaction.BeginAsync(_db, ct);
        var employee = await EmployeeQueries.RequireScopedAsync(_db, request.EmployeeId, user, ct);

        if (employee.UserAccountId is not { } accountId)
            throw new AppException("This employee has no account to reactivate.", 422);

        // The rule most likely to be omitted, because it is a cross-check between two pieces of state in
        // two slices. A Departed Employee's suspension is a CONSEQUENCE of their departure, so lifting it
        // here would restore access to somebody who has left while their record still says they are gone.
        //
        // This stays a 422 even though /reinstate now exists. Reinstatement reactivates the account itself,
        // as one operation on one consistent state; this endpoint would produce Departed + Active access,
        // which is the pair nothing else in the slice can produce and nothing downstream expects.
        if (employee.Status == EmployeeStatus.Departed)
            throw new AppException(
                "A departed employee's account cannot be reactivated. Reinstate them if the departure was "
                + "recorded by mistake, or register them again if they have returned.",
                422);

        // No self check is needed: a caller cannot have suspended themselves, so they cannot be reactivating
        // themselves. No invariant guard either -- reactivation cannot reduce the Customer Admin count.
        //
        // This does not reset a password or clear a lockout. A returning person who has forgotten their
        // password uses the reset flow.
        await _identity.ReactivateAccountAsync(accountId, ct);

        await _transaction.CommitAsync(ct);
        return new MarkedResultDto { Success = true };
    }
}
