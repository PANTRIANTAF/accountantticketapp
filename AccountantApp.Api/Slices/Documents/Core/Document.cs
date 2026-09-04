using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Documents.Core;

/// <summary>
/// The metadata half of a document. The bytes are a separate entity
/// (<see cref="DocumentContent"/>), reached by one deliberate method.
///
/// There is NO byte[] Content property here, and adding one -- even lazy-loaded -- reintroduces the
/// accidental read this slice's two-table schema exists to prevent: EF materialises every mapped
/// column, so a query written to list ten file names would read 250 MB of bytes. Plan section 1.1.
///
/// There is no TicketStatus (mutable, and Tickets evaluates it live -- plan section 1.2), no ScanState
/// and no IsQuarantined (there is no virus scanning, and that is a decision rather than an omission --
/// 01-DomainModel.md section 6), and no navigation to Ticket or Customer, which are other slices'
/// entities and whose tables this context must not map.
///
/// CustomerId is here as defence in depth. Tickets already constrains the Customer by loading the
/// ticket in scope, but if that check is ever wrong, ICustomerScoped is a second independent barrier
/// between a caller and another Customer's payroll data.
/// </summary>
public sealed class Document : ICustomerScoped
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid TicketId { get; set; }

    public string Origin { get; set; } = DocumentOrigin.CustomerUpload;

    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ContentHash { get; set; } = string.Empty;

    public Guid UploadedByUserAccountId { get; set; }
    public DateTimeOffset UploadedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserAccountId { get; set; }

    public bool IsDeleted => DeletedAt is not null;
}

// DocumentOrigin used to live here. It is now in ExternalInterfaces/DocumentOrigin.cs, because the one
// caller obliged to supply an Origin is another slice, which may not read this folder. Do not move it
// back.
