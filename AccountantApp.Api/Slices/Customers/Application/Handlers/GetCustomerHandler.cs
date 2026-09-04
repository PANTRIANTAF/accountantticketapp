using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Customers.Application.Dtos;
using AccountantApp.Api.Slices.Customers.Core;
using AccountantApp.Api.Slices.Customers.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Customers.Application.Handlers;

public sealed class GetCustomerHandler
{
    private readonly CustomersDbContext _db;
    private readonly IPermissionChecker _permissions;

    public GetCustomerHandler(CustomersDbContext db, IPermissionChecker permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public async Task<CustomerDto> Handle(GetCustomerRequestDto request, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "ViewCustomer", ct: ct);
        var customer = await _db.Customers.AsNoTracking()
            .Where(item => item.Id == request.CustomerId)
            .WhereMatchesCustomerScope(user)
            .FirstOrDefaultAsync(ct)
            ?? throw new AppException("Customer not found.", 404);
        return CustomerMapper.ToDto(customer);
    }
}