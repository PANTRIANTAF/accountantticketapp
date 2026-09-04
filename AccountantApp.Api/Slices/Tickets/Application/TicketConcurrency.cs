using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Tickets.Core;

namespace AccountantApp.Api.Slices.Tickets.Application;

/// <summary>
/// Optimistic concurrency on the tickets row. Section 9.7, LOCKED. Two methods, used by every handler
/// that writes that row, and reimplemented nowhere.
///
/// The token is a hand-maintained integer column, NOT UseXminAsConcurrencyToken(). Section 9.7: "an
/// opaque provider-specific token that the SPA has to round-trip does not belong in the contract." If
/// `xmin` ever appears in a configuration in this slice, it is wrong. The trade is deliberate: an
/// integer the client can read, echo and display costs an explicit Touch on every write, and forgetting
/// that Touch is the failure this file's comments exist to prevent.
///
/// Do NOT apply RequireVersion to the append-only tables. Posting a message or adding a field
/// verification does not conflict with a concurrent one -- those writes interleave correctly and a
/// version check there would reject two people typing at once for no reason. But when the SAME handler
/// also writes the ticket row (rejecting a field AND moving to AwaitingInformation, for instance), the
/// check applies to that part.
/// </summary>
public static class TicketConcurrency
{
    /// <summary>
    /// Rejects a write built on a stale read.
    ///
    /// 409, not 500 and not 422 (section 9.7). It is not a server fault and it is not a malformed
    /// request -- the request was valid when the client composed it. The client re-reads and retries,
    /// and only a 409 tells it that retrying unchanged is pointless while retrying after a reload is
    /// not.
    ///
    /// Call this AFTER loading through WhereTicketVisible and BEFORE any other work (section 4.0 B), so
    /// a conflict costs nothing and cannot half-apply. Checking it late means the audit entry, the
    /// notification or the SystemEvent may already exist for a change that never lands.
    /// </summary>
    public static void RequireVersion(Ticket ticket, int expectedVersion)
    {
        if (ticket.Version != expectedVersion)
            throw new AppException(
                "This ticket was changed by someone else. Reload and try again.", 409);
    }

    /// <summary>
    /// Advances the token and stamps activity. Call on EVERY write to the tickets row -- status
    /// transition, pickup, assignment, reassignment, priority, due date, title-affecting edit.
    ///
    /// A handler that modifies the row without calling this leaves the version unchanged, so the next
    /// writer's stale version still matches and two concurrent writers both succeed with one silently
    /// overwriting the other. That is the exact bug the column exists to prevent, and it leaves no
    /// trace.
    ///
    /// LastActivityAt moves with it because the two are the same event: something happened to this
    /// ticket. Keeping them in one method is why idx_tickets_customer_activity can be trusted to order
    /// a Customer's list by real activity.
    /// </summary>
    /// <param name="now">
    /// From the application clock, passed in -- never DateTimeOffset.UtcNow read inside here. One
    /// operation that touches the row twice must stamp one instant, and a testable clock is the only
    /// way a test can assert ordering.
    /// </param>
    public static void Touch(Ticket ticket, DateTimeOffset now)
    {
        ticket.Version += 1;
        ticket.LastActivityAt = now;
    }
}
