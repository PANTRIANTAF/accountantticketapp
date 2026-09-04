namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Sets or changes the Assignee without necessarily moving the status.
///
/// Any Accountant may reassign any ticket, including to themselves and including away from an
/// AccountantAdmin -- there is no seniority in assignment (plan §9.9, LOCKED). Attribution is preserved
/// by the audit log, not by withholding the operation.
/// </summary>
public class AssignTicketRequestDto
{
    public Guid TicketId { get; set; }

    public int Version { get; set; }

    /// <summary>
    /// The target's USER ACCOUNT id. It must resolve LIVE to an Active Accountant of either role
    /// (§4.8 rule 3); a suspended Accountant or a Customer-side account is 422, not 403.
    /// </summary>
    public Guid AssigneeUserAccountId { get; set; }
}
