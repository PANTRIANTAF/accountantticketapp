using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Employees.Application;

internal static class EmployeeQueries
{
    /// <summary>
    /// The tracked, scope-filtered lookup that every writing handler starts with. Out of scope yields
    /// 404 because the filtered query finds nothing -- never load the row and then compare CustomerId to
    /// return a 403, because a 403 confirms the row exists.
    ///
    /// Callable only by roles the catalogue admits, and every such role except Employee is excluded from
    /// the writing actions, so no second UserAccountId filter belongs here. The one handler an Employee
    /// can write through (update-own-contact) does not take a target at all and so does not use this.
    /// </summary>
    internal static async Task<Employee> RequireScopedAsync(
        EmployeesDbContext db,
        Guid employeeId,
        CurrentUser user,
        CancellationToken ct) =>
        await db.Employees
            .Where(employee => employee.Id == employeeId)
            .WhereInCustomerScope(user)
            .FirstOrDefaultAsync(ct)
        ?? throw new AppException("Employee not found.", 404);

    /// <summary>
    /// Turns a search term into a LIKE pattern. The escaping matters: an unescaped '%' typed into a
    /// search box matches everything, and an unescaped '_' matches any character.
    /// </summary>
    internal static string LikePattern(string value) =>
        $"%{value.Replace("\\", "\\\\", StringComparison.Ordinal)
                 .Replace("%", "\\%", StringComparison.Ordinal)
                 .Replace("_", "\\_", StringComparison.Ordinal)}%";
}
