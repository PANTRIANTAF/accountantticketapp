using AccountantApp.Api.Shared.Auth;

namespace AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

public interface ITicketTypesApi
{
    /// <summary>Current version and fields, stripped for the caller's role. Null if not found.</summary>
    Task<TicketTypeDetailDto?> GetTicketTypeAsync(Guid ticketTypeId, UserRole callerRole, CancellationToken ct);

    /// <summary>A specific version by type + number, stripped for the caller's role. Null if not found.</summary>
    Task<TicketTypeDetailDto?> GetTicketTypeVersionAsync(
        Guid ticketTypeId, int versionNumber, UserRole callerRole, CancellationToken ct);

    /// <summary>
    /// A specific version by its own id, stripped for the caller's role. Null if not found.
    /// A ticket stores <c>ticket_type_version_id</c> — a Guid — so this is the only accessor that
    /// lets it resolve its frozen descriptor set. Without it the ticket would have to carry a
    /// version number as well, i.e. two references to one thing, which
    /// Slices/Tickets/IMPLEMENTATION_PLAN.md §6.1 forbids.
    /// </summary>
    Task<TicketTypeDetailDto?> GetVersionByIdAsync(
        Guid ticketTypeVersionId, UserRole callerRole, CancellationToken ct);

    /// <summary>Types the caller's role may open. Never includes inactive types for Customer-side roles.</summary>
    Task<List<TicketTypeListItemDto>> ListAvailableTypesAsync(UserRole callerRole, CancellationToken ct);
}
