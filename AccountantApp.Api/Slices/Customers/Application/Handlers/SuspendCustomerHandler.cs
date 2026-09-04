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

public sealed class SuspendCustomerHandler
{
    private readonly CustomersDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public SuspendCustomerHandler(
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
        SetCustomerStatusRequestDto request,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "SuspendCustomer", ct: ct);
        request.Reason = CustomerValidation.NormalizeReason(request.Reason);

        // The transaction opens before the read, so the already-suspended guard below and the write
        // that depends on it see one consistent snapshot. Reading first let two concurrent /suspend
        // calls both observe Active, both pass the guard, and both write and audit -- two
        // CustomerSuspended entries for one transition, which is exactly the incoherent audit
        // record App section 4.6 rule 2 exists to prevent. See correction note Customers C-12.
        await using var transactionScope = await _transaction.BeginAsync(_db, ct);

        var customer = await _db.Customers.FirstOrDefaultAsync(item => item.Id == request.CustomerId, ct)
            ?? throw new AppException("Customer not found.", 404);
        if (customer.Status == CustomerStatus.Suspended)
            throw new AppException("This customer is already suspended.", 422);

        var before = CustomerMapper.ToAuditSnapshot(customer);
        customer.Status = CustomerStatus.Suspended;
        customer.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(new AuditEntry(
            AuditActions.CustomerSuspended,
            AuditTargets.Customer,
            customer.Id.ToString(),
            customer.Id,
            Before: before,
            After: new
            {
                customer.Status,
                request.Reason,
                Snapshot = CustomerMapper.ToAuditSnapshot(customer)
            }), ct);
        await _transaction.CommitAsync(ct);
        return CustomerMapper.ToDto(customer);
    }
}