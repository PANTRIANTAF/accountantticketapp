using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Customers.Application.Dtos;
using AccountantApp.Api.Slices.Customers.Core;
using AccountantApp.Api.Slices.Customers.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Customers.Application.Handlers;

public sealed class GetOwnCustomerHandler
{
    private readonly CustomersDbContext _db;
    private readonly IPermissionChecker _permissions;

    public GetOwnCustomerHandler(CustomersDbContext db, IPermissionChecker permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public async Task<CustomerSelfDto> Handle(CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ViewOwnCustomer", ct: ct);
        var customerId = user.CustomerId
            ?? throw new AppException("Authentication required.", 401);
        var customer = await _db.Customers.AsNoTracking()
            .Where(item => item.Id == customerId)
            .WhereMatchesCustomerScope(user)
            .FirstOrDefaultAsync(ct)
            ?? throw new AppException("Customer not found.", 404);
        return CustomerMapper.ToSelfDto(customer);
    }
}