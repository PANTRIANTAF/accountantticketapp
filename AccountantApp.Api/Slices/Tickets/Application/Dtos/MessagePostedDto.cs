namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// <c>/api/tickets/post-message</c> and <c>/api/tickets/post-internal-note</c>.
///
/// <see cref="Kind"/> is echoed back because the caller does not choose it -- the server derived it from
/// the role -- so this is the only way the client learns whether what it posted is visible to the other
/// side.
/// </summary>
public class MessagePostedDto
{
    public Guid TicketId { get; set; }

    public Guid MessageId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public TicketStateDto Ticket { get; set; } = new();
}
