using AccountantApp.Api.Shared.Authorization;

namespace AccountantApp.Api.Slices.Tickets.Core;

/// <summary>
/// The request itself. 01-DomainModel.md section 4.
///
/// It implements ICustomerScoped, so .WhereInCustomerScope(user) is available -- but that filter is
/// only the OUTERMOST of four visibility layers, and no read of a ticket may use it alone. Every read
/// goes through TicketVisibility.WhereTicketVisible(user, callerEmployeeId), which stacks Creator-or-
/// Subject and Draft privacy on top of it.
///
/// There are deliberately NO navigation properties to another slice's entities -- not Customer, not
/// Employee, not TicketTypeVersion, not Document. Dependency rule 2, and a navigation would require
/// TicketsDbContext to map another slice's table.
/// </summary>
public sealed class Ticket : ICustomerScoped
{
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable, unique, never reused and never changed. TKT-{year}-{000000}. Allocated by
    /// TicketReferenceAllocator inside the creation transaction, including for a Draft -- it is how
    /// the Creator refers to it.
    /// </summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>The tenant boundary. Immutable after creation.</summary>
    public Guid CustomerId { get; set; }

    public Guid TicketTypeId { get; set; }

    /// <summary>
    /// The specific version, frozen at creation. Stored as well as TicketTypeId so a later version of
    /// the type does not change what an existing ticket asked for. Resolve its descriptors with
    /// ITicketTypesApi.GetVersionByIdAsync -- that accessor exists for exactly this column.
    /// </summary>
    public Guid TicketTypeVersionId { get; set; }

    public Guid CreatorUserAccountId { get; set; }

    /// <summary>The Employee the ticket is about. May be accountless. Immutable after creation.</summary>
    public Guid SubjectEmployeeId { get; set; }

    public string Status { get; set; } = TicketStatus.Draft;

    /// <summary>
    /// Null in Draft and Cancelled, required in InReview/AwaitingInformation/Answered/Closed, and
    /// OPTIONAL in Submitted -- AwaitingInformation -> Submitted retains it. See ck_tickets_assignee.
    /// </summary>
    public Guid? AssigneeUserAccountId { get; set; }

    public string Priority { get; set; } = TicketPriority.Normal;

    /// <summary>DateOnly, mapping to DATE. A statutory deadline falls on a day, not at an instant.</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>
    /// Derived from the Ticket Type name plus the Subject at creation, so lists are readable without
    /// opening each ticket. Never supplied by a client; no request DTO has a Title property.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Nullable, and with no foreign key, ONLY because this table and ticket_revisions reference each
    /// other: the ticket is inserted, then revision 1, then this is set in a second SaveChangesAsync
    /// inside the same transaction.
    /// </summary>
    public Guid? CurrentRevisionId { get; set; }

    /// <summary>
    /// 01-DomainModel.md section 9.1: a Closed ticket is never reopened, so a continuation is a new
    /// ticket carrying this link. Set at creation only. It copies no data and grants no access to the
    /// predecessor.
    /// </summary>
    public Guid? PrecededByTicketId { get; set; }

    /// <summary>
    /// The optimistic-concurrency token, section 9.7. Hand-maintained: every write to this row calls
    /// TicketConcurrency.Touch. Not xmin.
    /// </summary>
    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    // Intra-slice navigations. These are fine and useful -- but do not Include them on the list
    // path, where a page of 50 tickets would drag every revision, value and message with it.
    public List<TicketRevision> Revisions { get; set; } = [];
    public List<TicketMessage> Messages { get; set; } = [];

    public bool IsTerminal => Status is TicketStatus.Closed or TicketStatus.Cancelled;

    public bool IsOpen => !IsTerminal;

    /// <summary>
    /// 01-DomainModel.md section 5: "Field values are editable only in Draft and
    /// AwaitingInformation. In every other status the current revision is frozen." Every handler that
    /// touches a field value consults this, never its own status list.
    /// </summary>
    public bool FieldsEditable => Status is TicketStatus.Draft or TicketStatus.AwaitingInformation;

    // There is no Reopened status, no ReopenedAt, and no reopen path of any kind. Section 9.1,
    // LOCKED. A continuation is a new ticket with PrecededByTicketId.
}

public static class TicketStatus
{
    public const string Draft               = "Draft";
    public const string Submitted           = "Submitted";
    public const string InReview            = "InReview";
    public const string AwaitingInformation = "AwaitingInformation";
    public const string Answered            = "Answered";
    public const string Closed              = "Closed";
    public const string Cancelled           = "Cancelled";

    /// <summary>Not Closed, not Cancelled. Used by the pickup queue (section 9.8 condition 2).</summary>
    public static readonly IReadOnlySet<string> Open = new HashSet<string>(StringComparer.Ordinal)
        { Submitted, InReview, AwaitingInformation, Answered };

    /// <summary>Every legal value of the status column, matching ck_tickets_status exactly.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Draft, Submitted, InReview, AwaitingInformation, Answered, Closed, Cancelled };
}

public static class TicketPriority
{
    public const string Normal = "Normal";
    public const string High   = "High";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Normal, High };
}
