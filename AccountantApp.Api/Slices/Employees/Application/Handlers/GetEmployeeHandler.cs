using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// All four roles, two return shapes. Accountants and the owning Customer Admin get EmployeeDetailDto;
/// the Employee role gets EmployeeSelfDto for their own record and a 404 for anybody else's.
/// </summary>
public sealed class GetEmployeeHandler
{
    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IIdentityApi _identity;

    public GetEmployeeHandler(
        EmployeesDbContext db,
        IPermissionChecker permissions,
        IIdentityApi identity)
    {
        _db = db;
        _permissions = permissions;
        _identity = identity;
    }

    /// <summary>
    /// Returns EmployeeDetailDto or EmployeeSelfDto. The static type is object because the shape is a
    /// function of the caller's role; the endpoint documents the detail shape and notes the narrowing.
    /// </summary>
    public async Task<object> Handle(
        EmployeeIdRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ViewEmployee", ct: ct);

        // AsNoTracking: it is a read.
        var query = _db.Employees.AsNoTracking()
            .Where(employee => employee.Id == request.EmployeeId)
            .WhereInCustomerScope(user);

        if (user.Role == UserRole.Employee)
        {
            // WhereInCustomerScope narrows an Employee to their CUSTOMER, which is every colleague they
            // work with. Without this second filter any Employee can read every colleague's tax
            // identification number and social-security number by guessing an id -- and the scope test
            // everyone writes (a DIFFERENT Customer's Employee returns 404) still passes. This is the
            // highest-consequence defect available in this slice and it is invisible to the obvious test.
            //
            // The id is parsed once, outside the query. A .ToString() inside the query either fails to
            // translate or becomes a cast that defeats the index, and a "D"-format Guid compared against
            // an "N"-format string silently 404s a person's own record.
            var accountId = EmployeeInvariants.AccountIdOf(user);

            // Two separate projections behind one if, each selecting only its own columns. Do NOT project
            // the detail DTO and strip fields: the social-security number would then travel through the
            // application in a variable a future maintainer can serialise, with nothing but a comment
            // stopping them.
            return await query
                .Where(employee => employee.UserAccountId == accountId)
                .Select(EmployeeMapper.ToSelfExpression)
                .FirstOrDefaultAsync(ct)
                // An out-of-scope or non-self id is a 404 produced by the query finding nothing, never a
                // 403 -- a 403 would confirm the row exists.
                ?? throw new AppException("Employee not found.", 404);
        }

        var detail = await query
            .Select(EmployeeMapper.ToDetailExpression)
            .FirstOrDefaultAsync(ct)
            ?? throw new AppException("Employee not found.", 404);

        await FillAccountAsync(detail, request.EmployeeId, ct);
        return detail;
    }

    /// <summary>
    /// Role and AccountStatus are not columns; they come from Identity. Both stay null for an accountless
    /// Employee, and the SPA renders that as "not invited".
    /// </summary>
    private async Task FillAccountAsync(EmployeeDetailDto detail, Guid employeeId, CancellationToken ct)
    {
        if (!detail.HasAccount)
            return;

        var account = await _identity.FindByEmployeeAsync(employeeId, ct);
        if (account is null)
            return;

        detail.Role = account.Role;
        detail.AccountStatus = account.Status;
    }
}
