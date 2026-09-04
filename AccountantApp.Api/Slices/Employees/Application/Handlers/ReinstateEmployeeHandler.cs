using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// Reverses a departure: the Employee returns to Active and their account is reactivated in the same
/// transaction.
///
/// This exists because Departed was terminal and that was wrong in practice. A departure entered by
/// mistake -- the wrong person picked from a list -- had no in-app recovery at all: the record was
/// frozen, the login suspended, and the only fix was an edit straight against the database. The cost of
/// the operation existing is that a real departure can be undone by anybody who can enter one, which the
/// audit trail is what makes acceptable.
///
/// It is NOT a general re-hire operation. Somebody who left, worked elsewhere for two years, and came
/// back is still a NEW Employee record -- their two periods of employment are separate facts, and this
/// handler would silently merge them by clearing the end date of the first. The distinction is the
/// caller's to make and nothing here can enforce it; the audit entry records which one happened.
/// </summary>
public sealed class ReinstateEmployeeHandler
{
    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly ICustomerApi _customers;
    private readonly IIdentityApi _identity;
    private readonly IAuditApi _audit;

    public ReinstateEmployeeHandler(
        EmployeesDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        ICustomerApi customers,
        IIdentityApi identity,
        IAuditApi audit)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _customers = customers;
        _identity = identity;
        _audit = audit;
    }

    public async Task<MarkedResultDto> Handle(
        EmployeeIdRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ReinstateEmployee", ct: ct);

        await using var scope = await _transaction.BeginAsync(_db, ct);
        var employee = await EmployeeQueries.RequireScopedAsync(_db, request.EmployeeId, user, ct);

        // 422 and not 409: the caller has the right to do this, the record is simply not in a state that
        // can be reversed. An Active Employee being "reinstated" is a caller who has the wrong row.
        if (employee.Status != EmployeeStatus.Departed)
            throw new AppException("This employee has not departed.", 422);

        // A suspended Customer cannot gain an active Employee, the same rule registration enforces. The
        // Customer's own suspension already blocks every login there, so reinstating somebody into it
        // would produce an Active Employee who still cannot get in -- and the Office would have to
        // remember that the reason is two levels up.
        if (!await _customers.IsActiveAsync(employee.CustomerId, ct))
            throw new AppException("This customer is not active.", 422);

        var before = EmployeeMapper.ToAuditSnapshot(employee);

        // All three fields, in this order relative to the status: ck_employees_departure requires an
        // Active row to have neither a departed_at NOR an employment_end_date, so clearing one and not
        // the other turns this into a constraint violation surfacing as a 500.
        employee.Status = EmployeeStatus.Active;
        employee.EmploymentEndDate = null;
        employee.DepartedAt = null;
        employee.UpdatedAt = DateTimeOffset.UtcNow;

        // No last-Active-Customer-Admin guard here, unlike depart, set-role and suspend: this operation
        // can only ever ADD an active Admin. A guard that cannot fire is a guard whose absence is the
        // clearer statement.
        //
        // No RequireNotSelf either, and it is not an oversight: departure suspends the account, so a
        // departed person cannot authenticate and therefore cannot be the caller reinstating themselves.
        //
        // ReactivateAccountAsync is idempotent and restores the account to the state it can be used in --
        // Active for somebody who had a password, Invited for somebody who was still an unaccepted
        // invitee when they were departed. That second case is why this is not a plain flip to Active:
        // an Active account with no password hash can neither log in nor be re-invited.
        if (employee.UserAccountId is { } accountId)
            await _identity.ReactivateAccountAsync(accountId, ct);

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.EmployeeReinstated,
            AuditTargets.Employee,
            employee.Id.ToString(),
            employee.CustomerId,
            Before: before,
            After: EmployeeMapper.ToAuditSnapshot(employee)), ct);

        await _transaction.CommitAsync(ct);

        // Nothing is done about their Tickets, because nothing was done to them on departure either. They
        // stayed attached and stayed visible; now their Subject can be picked for new ones again, which
        // IEmployeeApi.IsActiveAsync answers live in Tickets without this handler telling it anything.
        return new MarkedResultDto { Success = true };
    }
}
