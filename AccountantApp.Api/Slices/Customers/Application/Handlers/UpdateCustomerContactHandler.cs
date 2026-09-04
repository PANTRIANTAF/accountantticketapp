using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers.Application.Dtos;
using AccountantApp.Api.Slices.Customers.Core;
using AccountantApp.Api.Slices.Customers.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Customers.Application.Handlers;

public sealed class UpdateCustomerContactHandler
{
    private readonly CustomersDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public UpdateCustomerContactHandler(
        CustomersDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
    }

    public async Task<CustomerDto> Handle(
        UpdateCustomerContactRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "EditCustomerContact", ct: ct);
        await using var transactionScope = await _transaction.BeginAsync(_db, ct);
        var customer = await _db.Customers
            .Where(item => item.Id == request.CustomerId)
            .WhereMatchesCustomerScope(user)
            .FirstOrDefaultAsync(ct)
            ?? throw new AppException("Customer not found.", 404);
        CustomerValidation.NormalizeAndValidate(request);

        var before = CustomerMapper.ToAuditSnapshot(customer);
        customer.AddressLine1 = request.AddressLine1;
        customer.AddressLine2 = request.AddressLine2;
        customer.AddressCity = request.AddressCity;
        customer.AddressPostalCode = request.AddressPostalCode;
        customer.AddressCountry = request.AddressCountry;
        customer.ContactEmail = request.ContactEmail;
        customer.ContactPhone = request.ContactPhone;
        customer.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditEntry(
            AuditActions.CustomerUpdated,
            AuditTargets.Customer,
            customer.Id.ToString(),
            customer.Id,
            Before: before,
            After: CustomerMapper.ToAuditSnapshot(customer)), ct);
        await _transaction.CommitAsync(ct);
        return CustomerMapper.ToDto(customer);
    }
}