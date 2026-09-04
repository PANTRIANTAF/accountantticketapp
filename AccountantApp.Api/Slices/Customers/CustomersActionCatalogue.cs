using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;

namespace AccountantApp.Api.Slices.Customers;

internal sealed class CustomersActionCatalogue : IActionCatalogue
{
    public string SliceName => "Customers";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } =
        new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
        {
            ["CreateCustomer"] = [UserRole.AccountantAdmin],
            ["SuspendCustomer"] = [UserRole.AccountantAdmin],
            ["ReactivateCustomer"] = [UserRole.AccountantAdmin],
            ["ListCustomers"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["EditCustomerLegal"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["EditCustomerContact"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin],
            ["ViewCustomer"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin],
            ["ViewOwnCustomer"] = [UserRole.CustomerAdmin, UserRole.Employee]
        };
}