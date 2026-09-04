using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// Revokes an Employee's access without ending their employment. AA, AU, and CA for their own Customer.
///
/// A separate handler from ReactivateEmployeeAccountHandler rather than one with a status parameter: the
/// guards differ, and a single handler with an if (suspending) inside it is where one of them eventually
/// goes missing.
/// </summary>
public sealed class SuspendEmployeeAccountHandler
{
    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IIdentityApi _identity;

    public SuspendEmployeeAccountHandler(
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
        await _permissions.RequireAsync(user, "SuspendEmployeeAccount", ct: ct);

        await using var scope = await _transaction.BeginAsync(_db, ct);
        var employee = await EmployeeQueries.RequireScopedAsync(_db, request.EmployeeId, user, ct);

        // 422, not 404: the Employee exists, there is just nothing to suspend.
        if (employee.UserAccountId is not { } accountId)
            throw new AppException("This employee has no account to suspend.", 422);

        // A Customer Admin may not suspend themselves. This is what stops a Customer locking itself out.
        EmployeeInvariants.RequireNotSelf(employee, user);

        // The third way to reach zero active Customer Admins, alongside demoting and departing.
        await EmployeeInvariants.RequireAnotherActiveCustomerAdminAsync(
            _db, _identity, employee.CustomerId, accountId, ct);

        // Identity audits this and sends the in-app AccountSuspended notification. This slice does neither:
        // no employees row changed, so there is nothing of ours to record, and a notification from here
        // would be a second message about one event.
        await _identity.SuspendAccountAsync(accountId, ct);

        // No UpdatedAt write, deliberately -- consistent with the rule that this slice stamps UpdatedAt
        // exactly when it also writes an audit entry about the employee. Nothing in employees changed here.
        //
        // And this does NOT mark the Employee Departed. Suspension is temporary and reversible; departure
        // is neither.
        await _transaction.CommitAsync(ct);
        return new MarkedResultDto { Success = true };
    }
}
