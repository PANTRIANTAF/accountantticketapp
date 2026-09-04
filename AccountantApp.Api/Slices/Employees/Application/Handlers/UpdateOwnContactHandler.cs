using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// The only endpoint an Employee may write through, and the only one whose request carries no target.
/// CustomerAdmin and Employee only -- an Accountant has no Employee record, so the catalogue excludes them
/// to produce a clean 403 rather than a confusing 404.
///
/// Exactly two editable fields: ContactPhone and WorkEmail. Not the name, not the job title, not the dates,
/// and above all not the personal identifying numbers. "Contact details" means how to reach them.
/// </summary>
public sealed class UpdateOwnContactHandler
{
    /// <summary>
    /// Returned on every successful call, not only when the email changed. A person who edits their work
    /// email will otherwise assume they have just changed how they log in -- and for a self-service
    /// endpoint that confusion is worse than for an administrative one. The login email lives on their
    /// account, in Identity, and only an Accountant can change it -- so the notice says who to ask rather
    /// than leaving the person to discover there is no self-service route.
    /// </summary>
    internal const string LoginEmailNotice =
        "Your work email is contact information only. It is not the address you sign in with, " +
        "and changing it here does not change how you log in. Ask the accounting office if you " +
        "need the address you sign in with changed.";

    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public UpdateOwnContactHandler(
        EmployeesDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
    }

    public async Task<EmployeeSelfDto> Handle(
        UpdateOwnContactRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "UpdateOwnContact", ct: ct);
        EmployeeValidation.NormalizeAndValidate(request);

        var accountId = EmployeeInvariants.AccountIdOf(user);

        await using var scope = await _transaction.BeginAsync(_db, ct);

        // The target is the session, never the request. WhereInCustomerScope is applied as well -- belt
        // and braces, and free, since the unique index on user_account_id already makes the account id
        // sufficient on its own.
        var employee = await _db.Employees
            .Where(candidate => candidate.UserAccountId == accountId)
            .WhereInCustomerScope(user)
            .FirstOrDefaultAsync(ct)
            ?? throw new AppException("You do not have an employee record.", 404);

        // No explicit Departed check. A Departed Employee's account is Suspended by the departure handler,
        // so they cannot log in and cannot reach this endpoint; a check here would be unreachable code
        // asserting something another slice already guarantees.

        var normalizedEmail = EmployeeValidation.Normalize(request.WorkEmail);
        if (normalizedEmail is not null
            && normalizedEmail != employee.NormalizedWorkEmail
            && await _db.Employees.AnyAsync(
                other => other.CustomerId == employee.CustomerId
                      && other.Id != employee.Id
                      && other.NormalizedWorkEmail == normalizedEmail, ct))
            throw new AppException(RegisterEmployeeHandler.DuplicateMessage, 409);

        var before = EmployeeMapper.ToAuditSnapshot(employee);
        employee.WorkEmail = request.WorkEmail;
        employee.NormalizedWorkEmail = normalizedEmail;
        employee.ContactPhone = request.ContactPhone;
        employee.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new AppException(RegisterEmployeeHandler.DuplicateMessage, 409);
        }

        // The actor is the Employee themselves; AuditApi resolves that from CurrentUser.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.EmployeeEdited,
            AuditTargets.Employee,
            employee.Id.ToString(),
            employee.CustomerId,
            Before: before,
            After: EmployeeMapper.ToAuditSnapshot(employee)), ct);

        await _transaction.CommitAsync(ct);

        var self = EmployeeMapper.ToSelfDto(employee);
        self.Notice = LoginEmailNotice;
        return self;
    }
}
