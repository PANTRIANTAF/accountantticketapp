using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;

namespace AccountantApp.Api.Slices.Employees;

/// <summary>
/// The catalogue expresses WHICH ROLES MAY CALL, and nothing else. Almost every row in this slice's matrix
/// section is really "yes, for their own Customer" or "own record only", and those qualifiers cannot be
/// written here -- they are enforced by the scope filter and the self checks inside each handler. A handler
/// whose only authorization is RequireAsync is a handler that lets a Customer Admin edit another Customer's
/// Employees.
/// </summary>
internal sealed class EmployeesActionCatalogue : IActionCatalogue
{
    public string SliceName => "Employees";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } =
        new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
        {
            // The only AccountantAdmin-only entry in this slice, because creating a Customer is an
            // AccountantAdmin power and this operation creates one.
            ["OnboardCustomer"] = [UserRole.AccountantAdmin],

            // Every other entry includes AccountantUser: the matrix gives AU everything AA has in this
            // domain, and none of the four powers reserved to AA is an Employee operation.
            ["RegisterEmployee"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin],
            ["ListEmployees"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin],

            // The only entry with all four roles. The scoping difference between them lives in
            // GetEmployeeHandler, because the catalogue can express who may call, not which rows.
            ["ViewEmployee"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],

            ["UpdateEmployee"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin],

            // Excludes both Accountant roles on purpose: an Accountant has no Employee record at all, so a
            // clean 403 here beats a confusing 404 from the handler.
            ["UpdateOwnContact"] = [UserRole.CustomerAdmin, UserRole.Employee],

            ["InviteEmployee"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin],
            ["SetEmployeeRole"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin],
            ["DepartEmployee"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin],

            // Reversing a departure is granted to exactly whoever may enter one. Narrowing it to
            // Accountants would mean a Customer Admin can create a state they cannot undo, which is how a
            // mistake becomes a support request.
            ["ReinstateEmployee"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin],

            // The second Accountant-only entry, and the only one that is not about creating a Customer.
            // Whoever can change a login address can move an account to a mailbox they control, so this
            // stays outside the Customer: a Customer Admin doing it to a colleague is account takeover,
            // and doing it to themselves is the same thing with fewer steps.
            ["ChangeEmployeeLoginEmail"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["SuspendEmployeeAccount"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin],
            ["ReactivateEmployeeAccount"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin]
        };
}
