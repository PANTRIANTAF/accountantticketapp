using System.Security.Claims;
using AccountantApp.Api.Shared.Errors;

namespace AccountantApp.Api.Shared.Auth;

public static class CurrentUserFactory
{
    public static CurrentUser FromPrincipal(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        var roleValue = principal.FindFirstValue(ClaimTypes.Role) ?? principal.FindFirstValue("role");
        if (string.IsNullOrWhiteSpace(id) || !Enum.TryParse<UserRole>(roleValue, true, out var role))
            throw new AppException("Authentication required.", 401);

        var customerIdValue = principal.FindFirstValue("customer_id");
        Guid? customerId = null;
        if (!string.IsNullOrWhiteSpace(customerIdValue))
        {
            if (!Guid.TryParse(customerIdValue, out var parsedCustomerId))
                throw new AppException("Authentication required.", 401);
            customerId = parsedCustomerId;
        }

        if (role is UserRole.CustomerAdmin or UserRole.Employee && customerId is null)
            throw new AppException("Authentication required.", 401);

        return new CurrentUser(id, role, customerId);
    }
}