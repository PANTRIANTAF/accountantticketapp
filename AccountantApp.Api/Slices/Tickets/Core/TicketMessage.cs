namespace AccountantApp.Api.Slices.Tickets.Core;

/// <summary>
/// The conversation on a Ticket. One entity covers all four kinds. 01-DomainModel.md section 3.
///
/// APPEND-ONLY: "Messages are append-only. They are not edited or deleted." No EditedAt, no DeletedAt,
/// no update handler, no delete handler. No Version property (9.7).
/// </summary>
public sealed class TicketMessage
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }

    /// <summary>
    /// Null for a SystemEvent, which the application writes rather than a person, and non-null for
    /// every other kind. ck_ticket_messages_author enforces both halves.
    /// </summary>
    public Guid? AuthorUserAccountId { get; set; }

    /// <summary>
    /// Derived from the caller's ROLE, never taken from the request body: if it came from the body a
    /// Customer could post something that renders as an Accountant response.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public List<TicketMessageDocument> AttachedDocuments { get; set; } = [];
}

public static class TicketMessageKind
{
    public const string CustomerMessage    = "CustomerMessage";
    public const string AccountantResponse = "AccountantResponse";
    public const string InternalNote       = "InternalNote";
    public const string SystemEvent        = "SystemEvent";

    /// <summary>
    /// Kinds a Customer-side caller may see. InternalNote is absent BY DESIGN.
    ///
    /// This is an ALLOW-LIST, not All.Except(InternalNote). A fifth kind added later is then invisible
    /// to the Customer side until somebody deliberately adds it, which is the safe default; a
    /// block-list makes the new kind visible immediately, and matrix section 6 makes internal notes
    /// "the Office's private channel". Deny by default.
    /// </summary>
    public static readonly IReadOnlySet<string> CustomerVisible = new HashSet<string>(StringComparer.Ordinal)
        { CustomerMessage, AccountantResponse, SystemEvent };

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { CustomerMessage, AccountantResponse, InternalNote, SystemEvent };
}
