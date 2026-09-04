namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Opens a ticket, as a Draft or submitted immediately.
///
/// What this DTO deliberately does NOT carry (plan §4.1 rules 1 and 9, §4.7 rule 2):
/// <list type="bullet">
/// <item><c>CustomerId</c> — resolved, never trusted. For a Customer-side caller it is
/// <c>user.CustomerId</c>; for an Accountant it is the SUBJECT's Customer. Two sources for one value
/// is two chances to disagree.</item>
/// <item><c>Title</c> — derived from the type name and the Subject's name at creation.</item>
/// <item><c>Version</c> — there is nothing yet to conflict with.</item>
/// <item><c>Status</c>, <c>Reference</c>, <c>Priority</c>, <c>AssigneeUserAccountId</c> — none of them
/// is the caller's to choose.</item>
/// </list>
/// </summary>
public class CreateTicketRequestDto
{
    public Guid TicketTypeId { get; set; }

    /// <summary>
    /// The Employee the ticket is about. An Employee caller may only name themselves (§4.1 rule 5);
    /// a Subject at another Customer is 404 and a Departed Subject is 422.
    /// </summary>
    public Guid SubjectEmployeeId { get; set; }

    /// <summary>
    /// The Closed ticket this one continues, if any. There is no reopen (§9.1, LOCKED) — a continuation
    /// is a new ticket linked backwards. Copies no field values and grants no access to the predecessor.
    /// </summary>
    public Guid? PrecededByTicketId { get; set; }

    /// <summary>Free-text note recorded on revision 1.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// When true the Draft → Submitted transition runs in the same transaction, and every required
    /// visible field must therefore be answered. When false the ticket stays a Draft and required
    /// fields are not enforced yet.
    /// </summary>
    public bool SubmitImmediately { get; set; }

    public List<TicketFieldValueInputDto> FieldValues { get; set; } = [];
}
