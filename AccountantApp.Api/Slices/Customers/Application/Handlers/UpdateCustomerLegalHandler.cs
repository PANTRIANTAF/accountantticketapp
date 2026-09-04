using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers.Application.Dtos;
using AccountantApp.Api.Slices.Customers.Core;
using AccountantApp.Api.Slices.Customers.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccountantApp.Api.Slices.Customers.Application.Handlers;

public sealed class UpdateCustomerLegalHandler
{
    private const string DuplicateMessage = "A customer with this tax number already exists.";
    private readonly CustomersDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public UpdateCustomerLegalHandler(
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
        UpdateCustomerLegalRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "EditCustomerLegal", ct: ct);
        await using var transactionScope = await _transaction.BeginAsync(_db, ct);
        var customer = await _db.Customers
            .Where(item => item.Id == request.CustomerId)
            .WhereMatchesCustomerScope(user)
            .FirstOrDefaultAsync(ct)
            ?? throw new AppException("Customer not found.", 404);
        CustomerValidation.NormalizeAndValidate(request);

        if (await _db.Customers.AnyAsync(
            item => item.Id != customer.Id && item.TaxNumber == request.TaxNumber, ct))
            throw new AppException(DuplicateMessage, 409);

        var before = CustomerMapper.ToAuditSnapshot(customer);
        customer.LegalName = request.LegalName;
        customer.TradingName = request.TradingName;
        customer.TaxNumber = request.TaxNumber;
        customer.TaxOffice = request.TaxOffice;
        customer.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new AppException(DuplicateMessage, 409);
        }

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