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

public sealed class CreateCustomerHandler
{
    private const string DuplicateMessage = "A customer with this tax number already exists.";
    private readonly CustomersDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public CreateCustomerHandler(
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
        CreateCustomerRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "CreateCustomer", ct: ct);
        CustomerValidation.NormalizeAndValidate(request);

        if (await _db.Customers.AnyAsync(customer => customer.TaxNumber == request.TaxNumber, ct))
            throw new AppException(DuplicateMessage, 409);

        await using var transactionScope = await _transaction.BeginAsync(_db, ct);
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            LegalName = request.LegalName,
            TradingName = request.TradingName,
            TaxNumber = request.TaxNumber,
            TaxOffice = request.TaxOffice,
            AddressLine1 = request.AddressLine1,
            AddressLine2 = request.AddressLine2,
            AddressCity = request.AddressCity,
            AddressPostalCode = request.AddressPostalCode,
            AddressCountry = request.AddressCountry,
            ContactEmail = request.ContactEmail,
            ContactPhone = request.ContactPhone,
            Status = CustomerStatus.Active,
            OnboardedOn = request.OnboardedOn,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Customers.Add(customer);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new AppException(DuplicateMessage, 409);
        }

        await _audit.LogAsync(new AuditEntry(
            AuditActions.CustomerCreated,
            AuditTargets.Customer,
            customer.Id.ToString(),
            customer.Id,
            After: CustomerMapper.ToAuditSnapshot(customer)), ct);
        await _transaction.CommitAsync(ct);
        return CustomerMapper.ToDto(customer);
    }
}