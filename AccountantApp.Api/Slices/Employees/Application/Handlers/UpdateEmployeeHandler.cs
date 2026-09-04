using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// Edits an Employee record on somebody else's behalf. AA, AU, and CA for their own Customer.
///
/// Not editable by this endpoint, and therefore absent from its DTO: CustomerId (immutable), UserAccountId
/// (the invite endpoint's), Status, EmploymentEndDate, DepartedAt (the departure endpoint's). A property
/// that exists is a property somebody binds.
/// </summary>
public sealed class UpdateEmployeeHandler
{
    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IIdentityApi _identity;
    private readonly IAuditApi _audit;

    public UpdateEmployeeHandler(
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
        UpdateEmployeeRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "UpdateEmployee", ct: ct);
        EmployeeValidation.NormalizeAndValidate(request);

        await using var scope = await _transaction.BeginAsync(_db, ct);
        var employee = await EmployeeQueries.RequireScopedAsync(_db, request.EmployeeId, user, ct);

        // A Departed Employee's record is still editable on purpose. Correcting a misspelled name or a
        // wrong tax number after somebody has left is ordinary work, and the record is retained forever.

        // ck_employees_dates would reject this anyway; pre-checking gives a message that says what is
        // wrong instead of a constraint name.
        if (employee.EmploymentEndDate is { } endDate && request.EmploymentStartDate > endDate)
            throw new AppException(
                "Employment start date cannot be after the recorded employment end date.", 422);

        var normalizedEmail = EmployeeValidation.Normalize(request.WorkEmail);
        if (normalizedEmail is not null
            && normalizedEmail != employee.NormalizedWorkEmail
            && await _db.Employees.AnyAsync(
                other => other.CustomerId == employee.CustomerId
                      && other.Id != employee.Id
                      && other.NormalizedWorkEmail == normalizedEmail, ct))
            throw new AppException(RegisterEmployeeHandler.DuplicateMessage, 409);

        var before = EmployeeMapper.ToAuditSnapshot(employee);

        // Which sensitive fields changed, computed before the assignment because afterwards the old values
        // are gone. The NAMES go into the audit row; the values never do.
        var changedSensitiveFields = new List<string>();
        if (employee.TaxIdentificationNumber != request.TaxIdentificationNumber)
            changedSensitiveFields.Add(nameof(request.TaxIdentificationNumber));
        if (employee.SocialSecurityNumber != request.SocialSecurityNumber)
            changedSensitiveFields.Add(nameof(request.SocialSecurityNumber));

        employee.GivenName = request.GivenName;
        employee.FamilyName = request.FamilyName;
        employee.JobTitle = request.JobTitle;
        employee.WorkEmail = request.WorkEmail;
        employee.NormalizedWorkEmail = normalizedEmail;
        employee.ContactPhone = request.ContactPhone;
        employee.TaxIdentificationNumber = request.TaxIdentificationNumber;
        employee.SocialSecurityNumber = request.SocialSecurityNumber;
        employee.EmploymentStartDate = request.EmploymentStartDate;
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

        await _audit.LogAsync(new AuditEntry(
            AuditActions.EmployeeEdited,
            AuditTargets.Employee,
            employee.Id.ToString(),
            employee.CustomerId,
            Before: before,
            After: EmployeeMapper.ToAuditSnapshot(employee, changedSensitiveFields)), ct);

        await _transaction.CommitAsync(ct);

        var detail = EmployeeMapper.ToDetailDto(employee);
        if (employee.UserAccountId is not null)
        {
            var account = await _identity.FindAsync(employee.UserAccountId.Value, ct);
            detail.Role = account?.Role;
            detail.AccountStatus = account?.Status;
        }

        return detail;
    }
}
