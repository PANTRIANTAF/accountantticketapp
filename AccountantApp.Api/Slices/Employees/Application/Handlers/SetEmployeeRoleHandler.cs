using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// Promotion to CustomerAdmin and demotion back to Employee. AA, AU, and CA for their own Customer.
///
/// No column in this slice holds the role -- there is no Employee.Role, because a Customer Admin IS an
/// Employee whose account has role CustomerAdmin. The change is delegated to Identity; the only thing this
/// handler writes to employees is UpdatedAt.
/// </summary>
public sealed class SetEmployeeRoleHandler
{
    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IIdentityApi _identity;
    private readonly IAuditApi _audit;

    public SetEmployeeRoleHandler(
        EmployeesDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IIdentityApi identity,
        IAuditApi audit)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _identity = identity;
        _audit = audit;
    }

    public async Task<MarkedResultDto> Handle(
        SetEmployeeRoleRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "SetEmployeeRole", ct: ct);

        // An Accountant role here is rejected outright with a 422, not silently ignored.
        EmployeeValidation.NormalizeAndValidate(request);

        await using var scope = await _transaction.BeginAsync(_db, ct);
        var employee = await EmployeeQueries.RequireScopedAsync(_db, request.EmployeeId, user, ct);

        if (employee.UserAccountId is not { } accountId)
            throw new AppException(
                "This employee has no account. Invite them before setting a role.", 422);

        // Compares against employee.UserAccountId, not employee.Id. The asymmetry is correct: an
        // Accountant is never themselves an Employee, so this only ever fires for a Customer Admin -- who
        // may not remove their own CustomerAdmin role, because that is how a Customer locks itself out.
        EmployeeInvariants.RequireNotSelf(employee, user);

        var account = await _identity.FindAsync(accountId, ct)
            ?? throw new AppException("This employee's account could not be found.", 422);

        // A no-op success tells the caller something happened and writes a misleading audit entry.
        if (account.Role == request.Role)
            throw new AppException("This employee already has that role.", 422);

        // Demotion is one of the three ways to reach zero active Customer Admins. Inside the transaction,
        // so a rejection rolls back the Identity call too.
        if (account.Role == UserRole.CustomerAdmin)
            await EmployeeInvariants.RequireAnotherActiveCustomerAdminAsync(
                _db, _identity, employee.CustomerId, accountId, ct);

        await _identity.SetCustomerSideRoleAsync(accountId, request.Role, ct);

        // Consistent with DepartEmployeeHandler: UpdatedAt is written exactly when this slice also writes
        // an audit entry about the employee, which is the pair of operations that change what the record
        // means. The suspend and reactivate handlers write neither, because no employees row changes there.
        employee.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Before and After carry the two role names. Without them the log records that a role changed but
        // not to what, which makes it useless for the one question it will actually be asked: who made
        // this person an administrator.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.EmployeeRoleChanged,
            AuditTargets.Employee,
            employee.Id.ToString(),
            employee.CustomerId,
            Before: new { Role = account.Role.ToString() },
            After: new { Role = request.Role.ToString() }), ct);

        await _transaction.CommitAsync(ct);

        // KNOWN, and not fixable here: the target's live session keeps the old role for up to 8 hours,
        // because claims are minted at login. Demotion therefore fails UNSAFE -- a demoted Customer Admin
        // keeps administrative powers until their cookie expires. Do not "fix" it with a per-request
        // database read inside IPermissionChecker.
        return new MarkedResultDto { Success = true };
    }
}
