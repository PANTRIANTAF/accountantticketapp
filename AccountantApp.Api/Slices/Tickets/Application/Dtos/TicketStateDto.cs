namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// What every ticket MUTATION returns: the fields a mutation can have changed, and nothing else.
///
/// It exists so that the caller of a transition gets the new <see cref="Version"/> back. Without it the
/// client's next call carries the version it sent, which is now one behind, and every second operation
/// is a spurious 409 -- the "optimistic concurrency makes the UI unusable" bug that is actually a
/// missing response field (plan §3.2).
///
/// It is deliberately NOT the detail shape. A transition does not re-read the revisions, the field
/// values or the conversation, so returning a detail DTO would either mean a second set of queries per
/// mutation or a detail DTO with half its properties empty.
/// </summary>
public class TicketStateDto
{
    public Guid Id { get; set; }

    public string Reference { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Priority { get; set; } = string.Empty;

    public DateOnly? DueDate { get; set; }

    public Guid? AssigneeUserAccountId { get; set; }

    /// <summary>Non-null exactly when <see cref="Status"/> is Closed.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>The version AFTER the write. Send this on the next mutation, not the one you sent.</summary>
    public int Version { get; set; }
}
