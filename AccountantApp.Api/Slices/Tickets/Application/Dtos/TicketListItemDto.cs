namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// One row of <c>/api/tickets/list</c> and <c>/api/tickets/pickup-queue</c>.
///
/// Two kinds of property live here and the difference matters. The ids and scalars come straight out of
/// the EF projection in <c>TicketMapper.ListItem</c>; the four display names do NOT -- they are filled
/// afterwards from <c>IEmployeeApi.FindManyAsync</c>, <c>ICustomerApi.FindManyAsync</c> and
/// <c>IIdentityApi.FindManyAsync</c>, one batched call each for the whole page rather than a lookup per
/// row (plan §4.3 rule 7 / §9 table). A name that stays null is an id the batch did not answer, not an
/// error: the batch contracts cap at 500 ids and simply omit what they cannot find.
/// </summary>
public class TicketListItemDto
{
    public Guid Id { get; set; }

    public string Reference { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Priority { get; set; } = string.Empty;

    public DateOnly? DueDate { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>Filled post-projection. Only ever populated for an Accountant's cross-Customer list.</summary>
    public string? CustomerName { get; set; }

    public Guid TicketTypeId { get; set; }

    public Guid SubjectEmployeeId { get; set; }

    /// <summary>Filled post-projection from <c>EmployeeSummary.FullName</c>.</summary>
    public string? SubjectName { get; set; }

    public Guid CreatorUserAccountId { get; set; }

    /// <summary>Filled post-projection.</summary>
    public string? CreatorName { get; set; }

    /// <summary>Null is the whole point of the pickup queue: nobody has it.</summary>
    public Guid? AssigneeUserAccountId { get; set; }

    /// <summary>Filled post-projection.</summary>
    public string? AssigneeName { get; set; }

    /// <summary>
    /// True when the row is in the pickup queue because its Assignee is not an Active account rather
    /// than because it has none -- the "stranded" half of the queue (plan §4.5 condition 2). Always
    /// false on <c>/api/tickets/list</c>.
    /// </summary>
    public bool IsStranded { get; set; }

    // There is deliberately no MessageCount. A count of ALL messages tells a Customer-side reader how
    // many internal notes exist about them, which is the disclosure visibility layer 4 exists to
    // prevent; a role-dependent count inside an EF projection is a security filter expressed as a
    // conditional the provider may or may not translate. Neither is worth a badge on a list row.

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The default sort key, descending, tie-broken by id (plan §4.3 rule 6).</summary>
    public DateTimeOffset LastActivityAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// Carried on a LIST row so a client can act on it without a round trip to <c>/get</c> first. Every
    /// mutation requires it and answers 409 when it is stale.
    /// </summary>
    public int Version { get; set; }
}
