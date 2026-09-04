using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers.Application;
using AccountantApp.Api.Slices.Customers.Application.Dtos;
using AccountantApp.Api.Slices.Customers.Core;
using AccountantApp.Api.Slices.Customers.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccountantApp.Api.Slices.Customers.ExternalInterfaces;

public sealed class CustomerApi : ICustomerApi
{
    private const string DuplicateMessage = "A customer with this tax number already exists.";

    private readonly CustomersDbContext _db;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;

    public CustomerApi(CustomersDbContext db, IRequestTransaction transaction, IAuditApi audit)
    {
        _db = db;
        _transaction = transaction;
        _audit = audit;
    }

    public Task<CustomerSummary?> FindAsync(Guid customerId, CancellationToken ct = default) =>
        _db.Customers.AsNoTracking()
            .Where(customer => customer.Id == customerId)
            .Select(customer => new CustomerSummary(
                customer.Id, customer.LegalName, customer.TradingName, customer.Status))
            .FirstOrDefaultAsync(ct);

    public Task<bool> IsActiveAsync(Guid customerId, CancellationToken ct = default) =>
        _db.Customers.AsNoTracking()
            .AnyAsync(customer => customer.Id == customerId && customer.Status == CustomerStatus.Active, ct);

    public async Task<IReadOnlyDictionary<Guid, CustomerSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> customerIds,
        CancellationToken ct = default)
    {
        if (customerIds.Count > 500)
            throw new InvalidOperationException("At most 500 customer ids may be requested.");

        var ids = customerIds.Distinct().ToList();
        return await _db.Customers.AsNoTracking()
            .Where(customer => ids.Contains(customer.Id))
            .Select(customer => new CustomerSummary(
                customer.Id, customer.LegalName, customer.TradingName, customer.Status))
            .ToDictionaryAsync(customer => customer.Id, ct);
    }

    public async Task<Guid> CreateAsync(CreateCustomer request, CancellationToken ct = default)
    {
        // Enlist, never Begin, and never Commit. Employees' onboarding operation spans three slices on
        // one connection; opening a transaction here would let this Customer survive a failure in the
        // step after it, which is precisely the state matrix section 3 forbids -- a Customer nobody can
        // log into.
        await _transaction.EnlistAsync(_db, ct);

        // The same validation the HTTP endpoint runs, on the same type, so "what a valid Customer is"
        // has one implementation. Mapping to the request DTO rather than duplicating the rules is the
        // point of this method taking a parallel shape at all.
        var validated = new CreateCustomerRequestDto
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
            OnboardedOn = request.OnboardedOn
        };
        CustomerValidation.NormalizeAndValidate(validated);

        if (await _db.Customers.AnyAsync(customer => customer.TaxNumber == validated.TaxNumber, ct))
            throw new AppException(DuplicateMessage, 409);

        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            LegalName = validated.LegalName,
            TradingName = validated.TradingName,
            TaxNumber = validated.TaxNumber,
            TaxOffice = validated.TaxOffice,
            AddressLine1 = validated.AddressLine1,
            AddressLine2 = validated.AddressLine2,
            AddressCity = validated.AddressCity,
            AddressPostalCode = validated.AddressPostalCode,
            AddressCountry = validated.AddressCountry,
            ContactEmail = validated.ContactEmail,
            ContactPhone = validated.ContactPhone,
            Status = CustomerStatus.Active,
            OnboardedOn = validated.OnboardedOn,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Customers.Add(customer);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            // The pre-check above gives the good message; this is the guarantee. Two Accountants
            // onboarding the same company at once would otherwise produce a 500.
            throw new AppException(DuplicateMessage, 409);
        }

        // This slice audits the row it created. The caller separately audits its own EmployeeRegistered
        // and EmployeeInvited -- three entries for one user action, because three things happened in
        // three slices.
        await _audit.LogAsync(new AuditEntry(
            AuditActions.CustomerCreated,
            AuditTargets.Customer,
            customer.Id.ToString(),
            customer.Id,
            After: CustomerMapper.ToAuditSnapshot(customer)), ct);

        return customer.Id;
    }
}