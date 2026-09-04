using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// <c>/api/tickets/get</c>. THE SHAPE OF THIS RESPONSE DEPENDS ON THE CALLER'S ROLE (plan §4.3 rule 5),
/// and the narrowing is not cosmetic -- it is the disclosure control for accountant-only fields:
///
///   - <see cref="Fields"/> comes from <c>ITicketTypesApi.GetVersionByIdAsync(versionId, user.Role, ct)</c>,
///     which already strips descriptors with <c>IsVisibleToCustomer = false</c> for a Customer-side
///     caller. Every field VALUE is then filtered to the keys that survived, so the two halves cannot
///     disagree -- there is one audience decision, made in `TicketTypes`, and this slice follows it.
///   - <see cref="Messages"/> is filtered by <c>TicketVisibility.WhereMessageVisible</c>, so an
///     InternalNote never reaches a Customer-side caller.
///
/// Documents are NOT here. <c>/api/documents/list</c> is a separate endpoint with its own action, and
/// embedding the list would mean a read of a ticket implies a read of its attachments' metadata under a
/// different permission.
/// </summary>
public class TicketDetailDto
{
    public Guid Id { get; set; }

    public string Reference { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Priority { get; set; } = string.Empty;

    public DateOnly? DueDate { get; set; }

    public Guid CustomerId { get; set; }

    public string? CustomerName { get; set; }

    public Guid TicketTypeId { get; set; }

    /// <summary>
    /// The VERSION the ticket was opened against -- not the type's current version. A later edit to the
    /// type must not change what an already-open ticket asked for (§9.4), and this is the id that makes
    /// that true.
    /// </summary>
    public Guid TicketTypeVersionId { get; set; }

    public string TicketTypeName { get; set; } = string.Empty;

    public int TicketTypeVersionNumber { get; set; }

    public Guid SubjectEmployeeId { get; set; }

    public string? SubjectName { get; set; }

    public Guid CreatorUserAccountId { get; set; }

    public string? CreatorName { get; set; }

    public Guid? AssigneeUserAccountId { get; set; }

    public string? AssigneeName { get; set; }

    /// <summary>
    /// Set when this ticket continues a Closed one. There is no reopen (§9.1); a continuation is a new
    /// ticket pointing back at its predecessor.
    /// </summary>
    public Guid? PrecededByTicketId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public int Version { get; set; }

    /// <summary>
    /// Straight from <c>TicketTransitions.AllowedTargetsFrom(Status)</c> -- the closed table's row for
    /// this status, not a client-side guess. It reflects the STATUS only; the caller's role and the
    /// per-transition conditions are still enforced by the handlers, so a target listed here can still
    /// be refused with 403 or 422.
    /// </summary>
    public List<string> AllowedTransitions { get; set; } = [];

    /// <summary>True only for Draft and AwaitingInformation (<c>Ticket.FieldsEditable</c>).</summary>
    public bool FieldsEditable { get; set; }

    /// <summary>Role-shaped descriptors, in <c>DisplayOrder</c>.</summary>
    public List<FieldDescriptorDetailDto> Fields { get; set; } = [];

    /// <summary>
    /// Newest first. Append-only: a revision, once written, is never modified, so this is a history and
    /// not a working copy. Unpaginated (§13 item 4).
    /// </summary>
    public List<TicketRevisionDto> Revisions { get; set; } = [];

    /// <summary>The current revision's id, or null on a ticket that has none yet.</summary>
    public Guid? CurrentRevisionId { get; set; }

    /// <summary>Oldest first, visibility-filtered. Unpaginated (§13 item 4).</summary>
    public List<TicketMessageDto> Messages { get; set; } = [];
}

/// <summary>One append-only revision and the values submitted with it.</summary>
public class TicketRevisionDto
{
    public Guid Id { get; set; }

    /// <summary>1-based and gap-free per ticket.</summary>
    public int SequenceNumber { get; set; }

    public Guid SubmittedByUserAccountId { get; set; }

    public string? SubmittedByName { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }

    public string? Note { get; set; }

    public bool IsCurrent { get; set; }

    public List<TicketFieldValueDto> FieldValues { get; set; } = [];
}

/// <summary>
/// One field's answer in one revision.
///
/// The carriers are separate nullable properties rather than one <c>object Value</c> because the column
/// they come from is what makes a Date a Date and a Number a <c>NUMERIC(18,4)</c>; collapsing them here
/// would put the type back in the client's hands. Exactly one is non-null, which
/// <c>ck_field_values_one_carrier</c> enforces in the database.
/// </summary>
public class TicketFieldValueDto
{
    public Guid Id { get; set; }

    public string FieldKey { get; set; } = string.Empty;

    /// <summary>From the descriptor, for rendering a value whose descriptor the client did not keep.</summary>
    public string Label { get; set; } = string.Empty;

    public string DataType { get; set; } = string.Empty;

    public string? Text { get; set; }

    public decimal? Number { get; set; }

    public DateOnly? Date { get; set; }

    /// <summary>The upper bound of a DateRange. Never set on a plain Date.</summary>
    public DateOnly? DateTo { get; set; }

    public bool? Boolean { get; set; }

    public Guid? DocumentId { get; set; }

    /// <summary>
    /// MultipleChoice only, parsed out of the JSON array stored in <c>value_text</c>. A single-choice
    /// answer uses <see cref="Text"/>.
    /// </summary>
    public List<string>? Choices { get; set; }

    /// <summary>
    /// True when the value was copied forward from the previous revision rather than answered again
    /// (§4.6). A server observation; a client never sets it.
    /// </summary>
    public bool IsCarriedForward { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Append-only, oldest first: a rejection followed by an acceptance leaves BOTH rows, which is what
    /// makes the verification history auditable.
    /// </summary>
    public List<FieldVerificationDto> Verifications { get; set; } = [];

    /// <summary>The last verification's outcome, or null if never verified. This is the effective state.</summary>
    public string? LatestOutcome { get; set; }
}

public class FieldVerificationDto
{
    public Guid Id { get; set; }

    /// <summary>Accepted or Rejected.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Required on a rejection, and always null on an acceptance (§4.7 rule 3).</summary>
    public string? RejectionReason { get; set; }

    public Guid VerifiedByUserAccountId { get; set; }

    public string? VerifiedByName { get; set; }

    public DateTimeOffset VerifiedAt { get; set; }
}

public class TicketMessageDto
{
    public Guid Id { get; set; }

    /// <summary>
    /// CustomerMessage, AccountantResponse, InternalNote or SystemEvent. Derived from the author's role
    /// at post time, never from the request body (§4.10 rule 1).
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Null exactly for a SystemEvent. The system is not a user, and inventing an author for it would
    /// make "who changed this status" unanswerable (§4.10 rule 5).
    /// </summary>
    public Guid? AuthorUserAccountId { get; set; }

    public string? AuthorName { get; set; }

    public string Body { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Ids only. The metadata comes from <c>/api/documents/list</c>, which has its own action, so an
    /// attachment cannot be enumerated through the conversation under the ticket-read permission.
    /// </summary>
    public List<Guid> AttachedDocumentIds { get; set; } = [];
}
