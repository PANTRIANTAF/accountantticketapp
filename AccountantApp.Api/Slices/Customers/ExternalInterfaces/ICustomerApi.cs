namespace AccountantApp.Api.Slices.Customers.ExternalInterfaces;

public sealed record CustomerSummary(Guid Id, string LegalName, string? TradingName, string Status)
{
    public bool IsActive => Status == "Active";
}

/// <summary>
/// What another slice must supply to create a Customer. Every field of CreateCustomerRequestDto, and
/// on purpose: this is the same operation, so a second field list here would drift from that one.
/// The mapping and the validation both live in <see cref="CustomerApi.CreateAsync"/>, so there is
/// still exactly one implementation of what a valid Customer is.
///
/// A class with settable properties rather than a positional record, because Employees binds an
/// instance of it straight out of the /api/customers/onboard request body -- the same convention every
/// request DTO in this codebase follows.
/// </summary>
public sealed class CreateCustomer
{
    public string LegalName { get; set; } = string.Empty;
    public string? TradingName { get; set; }
    public string TaxNumber { get; set; } = string.Empty;
    public string? TaxOffice { get; set; }
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string AddressCity { get; set; } = string.Empty;
    public string AddressPostalCode { get; set; } = string.Empty;
    public string AddressCountry { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public DateOnly OnboardedOn { get; set; }
}

public interface ICustomerApi
{
    /// <summary>
    /// Looks up one Customer by id. Does NOT apply Customer scope — the caller is responsible for
    /// its own scope check. Passing an id the caller has no right to see returns that Customer's
    /// summary, so a handler must have already established that the id is in scope.
    /// Returns null for an unknown id rather than throwing.
    /// </summary>
    Task<CustomerSummary?> FindAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>
    /// Whether the Customer is currently Active. Does NOT apply Customer scope. Answered from a
    /// live query with no caching of any kind, so a suspension takes effect on the next call —
    /// callers gate work on this and a stale true would let a suspended Customer keep acting.
    /// An unknown id is false, not an error: fail closed.
    /// </summary>
    Task<bool> IsActiveAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>
    /// Batch form of <see cref="FindAsync"/>, for callers resolving names for a page of rows
    /// without a query per row. Does NOT apply Customer scope. Ids not found are simply absent from
    /// the dictionary. The batch is capped, so a caller must not rely on every id being answered.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, CustomerSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> customerIds,
        CancellationToken ct = default);

    /// <summary>
    /// Creates an Active Customer and returns its id. The one write on this contract, and it exists
    /// for exactly one caller: Employees' composite onboarding operation, which creates a Customer,
    /// its first Employee, and that Employee's account in one transaction.
    ///
    /// It does NOT check permissions -- the calling handler has already called RequireAsync under its
    /// own action name, and running the check again here would audit the creation as if it were a
    /// direct request. It DOES audit CustomerCreated, because the row is this slice's data.
    ///
    /// It ENLISTS in the caller's transaction and never opens or commits one. That is what makes
    /// onboarding atomic: if the invitation step fails, this Customer must not survive.
    ///
    /// Throws AppException(409) for a duplicate tax number and AppException(422) for invalid input,
    /// so the caller does not have to re-validate what this method already checks.
    /// </summary>
    Task<Guid> CreateAsync(CreateCustomer request, CancellationToken ct = default);
}