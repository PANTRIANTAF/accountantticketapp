namespace AccountantApp.Api.Slices.Tickets.Core;

/// <summary>
/// The due-date scanner's idempotency marker. Plan section 9a.3.
///
/// It records that a <c>DueDateApproaching</c> reminder was sent for a ticket FOR A PARTICULAR DUE
/// DATE. That key is the whole design:
///
///   - A plain boolean (or a <c>last_reminded_at</c>) would suppress the reminder forever, so moving a
///     due date out and back, or forward by a month, would never remind anybody again.
///   - Keying it to <c>(ticket_id, due_date)</c> means changing <c>tickets.due_date</c> RE-ARMS the
///     reminder automatically, with nothing to reset and no second code path.
///
/// WHY THIS IS NOT A COLUMN ON tickets, which is where it obviously belongs: section 9.7 gives
/// tickets a hand-maintained optimistic-concurrency token, and every write to that row bumps it via
/// TicketConcurrency.Touch. A background UPDATE to a reminder column would therefore raise the version
/// of a row a user may have open, and the next Save they attempt answers 409 for a change nobody made
/// -- a conflict manufactured by a service they cannot see. A separate table touches nothing the
/// request path holds.
///
/// Section 1.9 applies here as everywhere else in this slice: append-only. A re-armed reminder is a
/// NEW row for the new due date; the row for the old due date stays, and is the record that the old
/// date was in fact reminded.
///
/// This entity is deliberately NOT ICustomerScoped and carries no navigation property. It is never
/// read on a request path, so there is no visibility question to answer, and a navigation would let
/// somebody load a ticket through it without going through TicketVisibility.
/// </summary>
public sealed class TicketDueDateReminder
{
    /// <summary>Half of the composite primary key. Intra-slice FK to tickets(id).</summary>
    public Guid TicketId { get; set; }

    /// <summary>
    /// The other half. The due date the reminder was sent FOR -- not the date it was sent ON, which is
    /// SentAt. DateOnly, mapping to DATE, exactly like tickets.due_date.
    /// </summary>
    public DateOnly DueDate { get; set; }

    /// <summary>
    /// When the reminder went out, as an instant. TIMESTAMPTZ rather than DATE because this one IS a
    /// moment in time rather than a calendar day, and it exists for diagnosis ("why did this fire at
    /// 03:00?") rather than for any decision the scanner makes.
    /// </summary>
    public DateTimeOffset SentAt { get; set; }
}
