using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Employees.Application.Handlers;

/// <summary>
/// Changes the address an Employee signs in with. The Office does this on request -- typically after
/// somebody's surname changes and their old mailbox stops working.
///
/// WHY IT LIVES HERE AND NOT IN IDENTITY. The account is Identity's data and Identity does the write, but
/// the authorization question is "may this caller act on this Employee", which needs the Customer scope
/// only this slice has. Identity would have to be told the scope to check it, and a contract that takes
/// a CurrentUser is a contract whose caller can lie about it.
///
/// WHY ACCOUNTANTS ONLY. Self-service would let anybody who briefly holds a session move the account to
/// an address they control, and a Customer Admin doing it for a colleague is the same thing one step
/// removed -- with the added problem that the colleague is the one who then cannot log in. Routing it
/// through the Office means a human outside the Customer is in the loop and the audit entry names them.
///
/// It does NOT touch the work email, the password, or any live session. The person keeps what they know
/// and stays signed in; the next login uses the new address.
/// </summary>
public sealed class ChangeEmployeeLoginEmailHandler
{
    private readonly EmployeesDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IIdentityApi _identity;
    private readonly IAuditApi _audit;

    public ChangeEmployeeLoginEmailHandler(
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
        ChangeEmployeeLoginEmailRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ChangeEmployeeLoginEmail", ct: ct);

        await using var scope = await _transaction.BeginAsync(_db, ct);
        var employee = await EmployeeQueries.RequireScopedAsync(_db, request.EmployeeId, user, ct);

        // 422, not 404: the Employee exists and the caller may see them. There is simply no account whose
        // address could be changed, and the fix is /api/employees/invite -- which is what the message says
        // rather than making the caller guess from a status code.
        if (employee.UserAccountId is not { } accountId)
            throw new AppException(
                "This employee has no account, so there is no sign-in address to change. " +
                "Invite them first.", 422);

        // Refused for a departed Employee, matching /reactivate-account: their account is suspended, so a
        // new sign-in address would change nothing anybody could use. Reinstate them first if the
        // departure was a mistake.
        if (employee.Status == EmployeeStatus.Departed)
            throw new AppException("This employee has departed.", 422);

        var accountBefore = await _identity.FindAsync(accountId, ct);

        // Identity validates the address, rejects a duplicate with a 409, and audits the account change.
        // Inside this transaction, so a failure after this point leaves the old address in place.
        await _identity.ChangeLoginEmailAsync(accountId, request.LoginEmail, ct);

        var accountAfter = await _identity.FindAsync(accountId, ct);

        // A second entry, in this slice, targeting the EMPLOYEE. Identity's entry targets the account, and
        // somebody investigating "what happened to this person" searches by Employee id -- an entry only
        // findable by account id is an entry they will not find. Two entries for one user action is
        // correct: two things happened, in two slices.
        //
        // A login email is not a personal identifying number, so both addresses are recorded in full. That
        // is the whole point of the entry: which address it was, and which it became.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.LoginEmailChanged,
            AuditTargets.Employee,
            employee.Id.ToString(),
            employee.CustomerId,
            Before: new { LoginEmail = accountBefore?.LoginEmail },
            After: new { LoginEmail = accountAfter?.LoginEmail }), ct);

        await _transaction.CommitAsync(ct);

        // The Employee row is untouched -- deliberately. WorkEmail is contact information and this call
        // named only the sign-in address; rewriting a field the caller did not mention would be the kind
        // of helpfulness that loses data.
        return new MarkedResultDto { Success = true };
    }
}
