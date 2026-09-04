using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// The second half of "registering and inviting are two separate operations": where an accountless
/// Employee gains a login. AA, AU, and CA for their own Customer -- a Customer Admin may invite somebody
/// as CustomerAdmin, which the matrix permits explicitly.
/// </summary>
public sealed class InviteEmployeeHandler
{
    private const string EmailInUseMessage = "That email address is already in use.";

    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IIdentityApi _identity;
    private readonly IAuditApi _audit;

    public InviteEmployeeHandler(
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

    public async Task<EmployeeDetailDto> Handle(
        InviteEmployeeRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "InviteEmployee", ct: ct);

        // Rejects an Accountant role with a 422. IIdentityApi guards the same thing by throwing; both
        // guards stay, because they protect against different mistakes -- this one tells a user their
        // request was wrong, that one tells a programmer their call site is.
        EmployeeValidation.NormalizeAndValidate(request);

        // The account, the invitation, and the user_account_id write are ONE transaction. A committed
        // account with no link back from the Employee row is an account nobody can find plus an Employee
        // who can be invited again -- reserving the address twice and failing on the unique constraint
        // with a message that makes no sense.
        await using var scope = await _transaction.BeginAsync(_db, ct);
        var employee = await EmployeeQueries.RequireScopedAsync(_db, request.EmployeeId, user, ct);

        if (employee.Status != EmployeeStatus.Active)
            throw new AppException("A departed employee cannot be invited.", 422);
        if (employee.UserAccountId is not null)
            throw new AppException("This employee already has an account.", 409);

        var loginEmail = request.LoginEmail ?? employee.WorkEmail;
        if (string.IsNullOrEmpty(loginEmail))
            throw new AppException("No email address on file for this employee.", 422);

        var normalizedEmail = EmployeeValidation.Normalize(loginEmail);
        if (normalizedEmail != employee.NormalizedWorkEmail
            && await _db.Employees.AnyAsync(
                other => other.CustomerId == employee.CustomerId
                      && other.Id != employee.Id
                      && other.NormalizedWorkEmail == normalizedEmail, ct))
            throw new AppException(RegisterEmployeeHandler.DuplicateMessage, 409);

        Guid userAccountId;
        try
        {
            userAccountId = await _identity.InviteEmployeeAccountAsync(new InviteEmployeeAccount(
                EmployeeId: employee.Id,
                // Mandatory and non-nullable: Identity cannot look it up, and ck_user_accounts_scope
                // rejects the row without it.
                CustomerId: employee.CustomerId,
                LoginEmail: loginEmail,
                DisplayName: $"{employee.GivenName} {employee.FamilyName}",
                Role: request.Role), ct);
        }
        catch (AppException exception) when (exception.StatusCode == 409)
        {
            // Identity enforces the system-wide login-email uniqueness. Its 409 must surface as a 409, not
            // a 500 -- a client-triggerable value is always a 4xx. The address may already be a login at
            // ANOTHER Customer, so the message must not say where.
            throw new AppException(EmailInUseMessage, 409);
        }

        employee.UserAccountId = userAccountId;

        // Keep the record consistent: the address that received the invitation is the address on file.
        employee.WorkEmail = loginEmail;
        employee.NormalizedWorkEmail = normalizedEmail;
        employee.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            // Writing WorkEmail from loginEmail can violate the per-Customer unique index, and so can the
            // unique index on user_account_id.
            throw new AppException(RegisterEmployeeHandler.DuplicateMessage, 409);
        }

        // Two audit entries in two slices, and that is correct: Identity writes AccountInvited against
        // UserAccount, this slice writes EmployeeInvited against Employee. Two things happened.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.EmployeeInvited,
            AuditTargets.Employee,
            employee.Id.ToString(),
            employee.CustomerId,
            After: EmployeeMapper.ToAuditSnapshot(employee)), ct);

        await _transaction.CommitAsync(ct);

        // No notification from here. InviteEmployeeAccountAsync already queued the invitation email with
        // the token in it; a second one would mean two emails, one of which has no link.
        //
        // And nothing is backfilled. The new account immediately gains read access to every non-Draft
        // Ticket where this Employee is the Subject, computed at query time from the existing
        // SubjectEmployeeId. An UPDATE stamping the new account id onto old Tickets would mean the model
        // had been misunderstood -- and this slice has no dependency on Tickets with which to try.
        var detail = EmployeeMapper.ToDetailDto(employee);
        detail.Role = request.Role;
        detail.AccountStatus = "Invited";
        return detail;
    }
}
