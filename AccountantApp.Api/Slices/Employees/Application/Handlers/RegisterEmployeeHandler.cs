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
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// Creates an ACCOUNTLESS Employee. No account, no invitation, no email of any kind -- inviting is a
/// separate, later, optional operation (see InviteEmployeeHandler). A Customer Admin may register
/// somebody and never invite them.
///
/// This is the shape that makes on-behalf-of ticketing work: a person who has never logged in can be the
/// Subject of a Ticket their Customer Admin opens for them. Merging registration and invitation breaks
/// the domain model's most important structural decision.
/// </summary>
public sealed class RegisterEmployeeHandler
{
    internal const string DuplicateMessage =
        "An employee with this work email already exists at this customer.";

    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly ICustomerApi _customers;
    private readonly IIdentityApi _identity;
    private readonly INotificationApi _notifications;
    private readonly IAuditApi _audit;

    public RegisterEmployeeHandler(
        EmployeesDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        ICustomerApi customers,
        IIdentityApi identity,
        INotificationApi notifications,
        IAuditApi audit)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _customers = customers;
        _identity = identity;
        _notifications = notifications;
        _audit = audit;
    }

    public async Task<EmployeeDetailDto> Handle(
        RegisterEmployeeRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "RegisterEmployee", ct: ct);
        EmployeeValidation.NormalizeAndValidate(request);

        var customerId = await ResolveCustomerAsync(request.CustomerId, user, ct);

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var normalizedEmail = EmployeeValidation.Normalize(request.WorkEmail);
        if (normalizedEmail is not null && await _db.Employees.AnyAsync(
                employee => employee.CustomerId == customerId
                         && employee.NormalizedWorkEmail == normalizedEmail, ct))
            throw new AppException(DuplicateMessage, 409);

        var now = DateTimeOffset.UtcNow;
        var employee = new Employee
        {
            CustomerId = customerId,
            GivenName = request.GivenName,
            FamilyName = request.FamilyName,
            JobTitle = request.JobTitle,
            WorkEmail = request.WorkEmail,
            NormalizedWorkEmail = normalizedEmail,
            ContactPhone = request.ContactPhone,
            TaxIdentificationNumber = request.TaxIdentificationNumber,
            SocialSecurityNumber = request.SocialSecurityNumber,
            EmploymentStartDate = request.EmploymentStartDate,

            // Accountless by construction. EmploymentEndDate and DepartedAt stay null:
            // ck_employees_departure forbids an end date on an Active row, and departure is its own
            // endpoint.
            UserAccountId = null,
            Status = EmployeeStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Employees.Add(employee);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            // The pre-check above gives the good message; this is the guarantee. Two Admins registering
            // the same person at the same moment would otherwise produce a 500.
            throw new AppException(DuplicateMessage, 409);
        }

        // No personal identifying numbers in the payload -- only whether they are present. A tax
        // identification number written into an audit row is retained forever in a table nobody purges.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.EmployeeRegistered,
            AuditTargets.Employee,
            employee.Id.ToString(),
            employee.CustomerId,
            After: EmployeeMapper.ToAuditSnapshot(employee)), ct);

        // The Customer's own Admins are told their staff list grew -- in-app only, and not the person just
        // registered, who has no account. Inside the transaction: a registration that rolls back must not
        // leave a notification claiming it happened.
        //
        // No name in the title, only in the body: notification titles are what a list shows, and a list of
        // "Maria Papadopoulou registered" rows is a staff list rebuilt in the wrong place.
        await EmployeeNotifications.NotifyCustomerAdminsAsync(
            _db, _identity, _notifications, employee.CustomerId,
            NotificationEvents.EmployeeRegistered,
            "A new employee was registered",
            $"{employee.GivenName} {employee.FamilyName} has been added to your employee list. "
            + "They cannot sign in until they are invited.", ct);

        await _transaction.CommitAsync(ct);

        // Role and AccountStatus stay null: this Employee has no account, and substituting "Employee"
        // would show them as holding a role they do not have.
        return EmployeeMapper.ToDetailDto(employee);
    }

    private async Task<Guid> ResolveCustomerAsync(
        Guid requestedCustomerId,
        CurrentUser user,
        CancellationToken ct)
    {
        if (user.Role is UserRole.CustomerAdmin or UserRole.Employee)
        {
            // One of the few places a 403 is right rather than a 404: the caller supplied a Customer id,
            // and Customer ids are not secret to a Customer Admin who knows their own. No row is being
            // hidden.
            if (requestedCustomerId != user.CustomerId!.Value)
                throw new AppException("You may only register employees at your own customer.", 403);

            // Their own Customer must still be Active -- a suspended Customer cannot gain Employees, and
            // a Customer Admin is not exempt from that.
            if (!await _customers.IsActiveAsync(requestedCustomerId, ct))
                throw new AppException("This customer is not active.", 422);

            return requestedCustomerId;
        }

        // Asked live, never cached, and never FindAsync(...)?.IsActive ?? true -- the ?? true turns
        // "no such Customer" into "go ahead".
        if (!await _customers.IsActiveAsync(requestedCustomerId, ct))
            throw new AppException("Unknown or inactive customer.", 422);

        return requestedCustomerId;
    }
}
