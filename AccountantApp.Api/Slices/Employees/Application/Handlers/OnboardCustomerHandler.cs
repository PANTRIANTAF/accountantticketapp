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
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// The composite operation: a new Customer, its first Employee, and that Employee's CustomerAdmin
/// invitation, in ONE transaction across three slices. AccountantAdmin only, because creating a Customer is.
///
/// It lives in this slice, and its route is /api/customers/onboard, because this slice owns steps 2 and 3
/// and therefore owns the transaction. Customers owns only step 1 and may not depend on Employees or
/// Identity -- moving this handler there creates a dependency cycle.
///
/// A failure at ANY step must leave nothing behind. That is the entire justification for the slice
/// placement and for RequestConnection existing at all: every slice's DbContext shares one connection, so
/// ICustomerApi.CreateAsync and IIdentityApi.InviteEmployeeAccountAsync enlist in this transaction rather
/// than opening their own.
/// </summary>
public sealed class OnboardCustomerHandler
{
    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly ICustomerApi _customers;
    private readonly IIdentityApi _identity;
    private readonly IAuditApi _audit;

    public OnboardCustomerHandler(
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

    public async Task<OnboardCustomerResponseDto> Handle(
        OnboardCustomerRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "OnboardCustomer", ct: ct);

        // Validate the Employee half BEFORE step 1. A 422 discovered after the Customer row was inserted is
        // a rollback that works -- the outbox row for the invitation email is in this transaction too, so it
        // rolls back with everything else. Validate up front anyway, because "safe by construction" stops
        // being true the first time somebody moves the notification call outside the transaction.
        //
        // The Customer half is Customers' to validate, and CreateAsync does it with the same code path the
        // /api/customers/create endpoint uses.
        EmployeeValidation.NormalizeAndValidate(request.FirstAdmin);

        await using var scope = await _transaction.BeginAsync(_db, ct);

        // Step 1. Delegated -- this slice never touches the customers table. CreateAsync enlists, audits
        // CustomerCreated itself, and checks no permissions, because this handler already did.
        var customerId = await _customers.CreateAsync(request.Customer, ct);

        // Step 2. The first Employee, at that Customer. No CustomerId came in on the request: the Customer
        // did not exist yet, so the only correct value is the one just generated.
        var admin = request.FirstAdmin;
        var now = DateTimeOffset.UtcNow;
        var employee = new Employee
        {
            CustomerId = customerId,
            GivenName = admin.GivenName,
            FamilyName = admin.FamilyName,
            JobTitle = admin.JobTitle,
            WorkEmail = admin.WorkEmail,
            NormalizedWorkEmail = EmployeeValidation.Normalize(admin.WorkEmail),
            ContactPhone = admin.ContactPhone,
            TaxIdentificationNumber = admin.TaxIdentificationNumber,
            SocialSecurityNumber = admin.SocialSecurityNumber,
            EmploymentStartDate = admin.EmploymentStartDate,
            Status = EmployeeStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Employees.Add(employee);
        await _db.SaveChangesAsync(ct);

        // Step 3. The account and the invitation. Delegated -- Identity owns accounts.
        Guid userAccountId;
        try
        {
            userAccountId = await _identity.InviteEmployeeAccountAsync(new InviteEmployeeAccount(
                EmployeeId: employee.Id,
                // Mandatory: Identity cannot look it up, and ck_user_accounts_scope rejects the row without it.
                CustomerId: customerId,
                LoginEmail: admin.WorkEmail,
                DisplayName: $"{admin.GivenName} {admin.FamilyName}",
                // CustomerAdmin, not Employee. The whole point is that the new Customer has somebody who can
                // administer it. Creating the first person as a plain Employee produces a Customer that
                // violates its own at-least-one-active-Customer-Admin invariant from the moment it exists,
                // and the set-role guard would then block every attempt to climb out of the hole.
                Role: UserRole.CustomerAdmin), ct);
        }
        catch (AppException exception) when (exception.StatusCode == 409)
        {
            // The address is already a login somewhere -- possibly at another Customer, which the message must
            // not reveal. A 409, never a 500.
            throw new AppException("That email address is already in use.", 409);
        }

        employee.UserAccountId = userAccountId;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new AppException("That email address is already in use.", 409);
        }

        // Three audit entries for one user action, because three things happened in three slices. Customers
        // wrote CustomerCreated inside CreateAsync and Identity wrote AccountInvited inside
        // InviteEmployeeAccountAsync; these two are this slice's own.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.EmployeeRegistered,
            AuditTargets.Employee,
            employee.Id.ToString(),
            customerId,
            After: EmployeeMapper.ToAuditSnapshot(employee)), ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.EmployeeInvited,
            AuditTargets.Employee,
            employee.Id.ToString(),
            customerId,
            After: EmployeeMapper.ToAuditSnapshot(employee)), ct);

        await _transaction.CommitAsync(ct);

        // All three ids, and no token. The SPA needs the Customer id to navigate and the Employee id to show
        // the invitation state; the invitation link goes to the invitee's mailbox and nowhere else.
        return new OnboardCustomerResponseDto
        {
            CustomerId = customerId,
            EmployeeId = employee.Id,
            UserAccountId = userAccountId
        };
    }
}
