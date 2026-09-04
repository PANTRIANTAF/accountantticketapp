using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// Marks an Employee as having left, and suspends their account in the same transaction.
///
/// Departure is reversible only as a CORRECTION, through ReinstateEmployeeHandler. That endpoint exists
/// because this one was originally terminal and the cost turned out to be real: a departure entered
/// against the wrong row could not be undone through the API at all.
///
/// Reinstating is not re-hiring. Somebody who returns to the company after genuinely leaving is a NEW
/// Employee record -- consistent with the rule that the same person at two Customers is two records. Their
/// old Tickets stay attached to the old record and stay visible either way.
/// </summary>
public sealed class DepartEmployeeHandler
{
    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IIdentityApi _identity;
    private readonly INotificationApi _notifications;
    private readonly IAuditApi _audit;

    public DepartEmployeeHandler(
        EmployeesDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IIdentityApi identity,
        INotificationApi notifications,
        IAuditApi audit)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _identity = identity;
        _notifications = notifications;
        _audit = audit;
    }

    public async Task<MarkedResultDto> Handle(
        DepartEmployeeRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "DepartEmployee", ct: ct);

        await using var scope = await _transaction.BeginAsync(_db, ct);
        var employee = await EmployeeQueries.RequireScopedAsync(_db, request.EmployeeId, user, ct);

        if (employee.Status == EmployeeStatus.Departed)
            throw new AppException("This employee has already departed.", 422);

        EmployeeInvariants.RequireNotSelf(employee, user);

        if (request.EmploymentEndDate == default)
            throw new AppException("Employment end date is required.", 422);

        // May be in the past or the future -- a notice period is ordinary -- but never before the start
        // date, which ck_employees_dates enforces too.
        if (request.EmploymentEndDate < employee.EmploymentStartDate)
            throw new AppException(
                "Employment end date cannot be before the employment start date.", 422);

        // Departure reaches zero active Customer Admins the same way demoting does.
        if (employee.UserAccountId is { } guardedAccountId)
            await EmployeeInvariants.RequireAnotherActiveCustomerAdminAsync(
                _db, _identity, employee.CustomerId, guardedAccountId, ct);

        var before = EmployeeMapper.ToAuditSnapshot(employee);
        var now = DateTimeOffset.UtcNow;

        // The record is marked Departed immediately even for a future end date, because the alternative is
        // a scheduled job this application does not have.
        employee.Status = EmployeeStatus.Departed;
        employee.EmploymentEndDate = request.EmploymentEndDate;
        employee.DepartedAt = now;
        employee.UpdatedAt = now;

        // Departure implies suspension: an active login for somebody who has left the company is the exact
        // hole this closes. Suspension remains a separate operation as well -- two operations, one of which
        // triggers the other. The reverse is NOT true: suspending an account does not mark anybody Departed.
        //
        // SuspendAccountAsync is idempotent by contract, which is what makes this safe: departing somebody
        // whose access had already been revoked for an unrelated reason must not fail.
        if (employee.UserAccountId is { } accountId)
            await _identity.SuspendAccountAsync(accountId, ct);

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.EmployeeDeparted,
            AuditTargets.Employee,
            employee.Id.ToString(),
            employee.CustomerId,
            Before: before,
            After: EmployeeMapper.ToAuditSnapshot(employee)), ct);

        // Sent AFTER the status is written, so the query inside the helper sees this person as Departed and
        // cannot address the notification to them -- which is the whole reason the order matters here and
        // not in registration.
        //
        // The end date is in the body because a future one is ordinary: "departing on the 30th" and
        // "departed" are different facts for an Admin deciding whether to reassign work today.
        await EmployeeNotifications.NotifyCustomerAdminsAsync(
            _db, _identity, _notifications, employee.CustomerId,
            NotificationEvents.EmployeeDeparted,
            "An employee has departed",
            $"{employee.GivenName} {employee.FamilyName} is recorded as having left on "
            + $"{employee.EmploymentEndDate:yyyy-MM-dd}. Their access has been revoked.", ct);

        await _transaction.CommitAsync(ct);

        // Nothing else changes. No Ticket is hidden, closed, reassigned, or deleted because its Subject
        // departed, and their Customer Admin keeps full visibility permanently. This handler does not touch
        // tickets -- it cannot, and must not gain the ability. That a Departed Employee may not be the
        // SUBJECT of a new Ticket is enforced in Tickets, through IEmployeeApi.IsActiveAsync, not here.
        return new MarkedResultDto { Success = true };
    }
}
