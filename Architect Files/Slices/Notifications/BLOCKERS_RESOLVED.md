# Notifications Plan — Blockers Resolved

Four contradictions in the plan that must be decided before implementation:

---

## N-1: Authorization — 404 vs Audited Denial on Bulk Operations

**Contradiction:**
- §7.0 C: "Out of scope is 404, never 403."
- §7.3 `MarkNotificationsReadHandler`: must handle multiple IDs; some may not belong to the caller.

**Resolution:**

**Decision:** Bulk operations (`NotifyManyAsync`, `MarkNotificationsReadHandler`) return `200` with aggregate success; per-id scope violations are not reported individually.

**Implementation:**
1. `MarkNotificationsReadHandler` filters by `user.Id` before loading. IDs not found after filtering are silently dropped.
2. Returns `200 OK` with `MarkedCount` = number of rows actually updated (zero if all were out-of-scope).
3. Write a **single audited denial** at the operation level (not per-id) when the request is made by comparing request count to result count. If `req.NotificationIds.Count > result.MarkedCount`, log one `MarkOwnNotificationRead` denial.
4. This treats the caller's attempt to touch another user's notification as a scope violation of the operation itself, not a per-id confirmation that the ID exists.

**Rationale:** A read-only operation that returns 404 confirms an ID exists. A read operation that returns 200-but-zero is indistinguishable from "you tried to read nothing". The denial audit happens at the boundary (the permission check), and the query filter does the hard scoping.

**Test:** Mark 5 IDs (2 yours, 3 another user's) → returns `MarkedCount = 2`, one `MarkOwnNotificationRead` denial audit entry.

---

## N-6: Drainer — Query Missing Notification Data

**Contradiction:**
- §5.4 rule 10: "Send `entry.EmailBody ?? notification.Body`"
- Pseudocode (lines 613-618): selects from `db.Outbox` only; no `notification` in scope.
- §2.4: "No navigation property from `OutboxEntry` to `Notification` is required."

**Resolution:**

**Decision:** The drainer query MUST join to `Notifications` and project both `Title` and `Body`.

**Implementation:**

```csharp
// OutboxDrainer.cs :: ExecuteAsync pseudocode
var due = await _db.Outbox
    .AsNoTracking()
    .Where(o => o.Status == OutboxStatus.Pending && o.NextAttemptAt <= now)
    .Join(_db.Notifications.AsNoTracking(),
          o => o.NotificationId,
          n => n.Id,
          (outbox, notification) => new { outbox, notification })
    .OrderBy(x => x.outbox.NextAttemptAt)
    .Take(BatchSize)
    .ToListAsync(ct);

foreach (var item in due)
{
    var entry = item.outbox;
    var notification = item.notification;
    
    // Now both are in scope:
    var emailBody = entry.EmailBody ?? notification.Body;
    var subject = notification.Title;
    // ... process(entry, notification)
}
```

**Navigation property decision:** Do NOT add a navigation property. The join is explicit and safer — a future coder cannot accidentally lazy-load 10,000 notifications. Keep `OutboxEntry` as a standalone entity.

**Rationale:** The design's whole point is that `email_body` is a secret and `notification.Body` is safe to log/display. The coalesce (`?? notification.Body`) requires both in scope.

---

## N-7: Duplicate Drainer — Locking Contradiction

**Contradiction:**
- Rule 6: "Do not send inside a transaction that spans the send. Save the status update after the send returns."  
  → Commit before send
- Rule 7: "Use `SELECT ... FOR UPDATE SKIP LOCKED` when claiming the batch."  
  → Lock prevents duplicates

**Problem:** `FOR UPDATE` locks only until the transaction commits. Commit before send (rule 6) → rows unlock during send → second instance can re-claim them → duplicate emails.

**Resolution:**

**Decision:** Keep rule 6 (commit before send). Drop rule 7's locking guarantee. Add a topology constraint instead.

**Implementation:**

Replace rule 7 with:

> **Single-replica topology.** The application deploys to one `app` container only. A second instance would re-claim rows mid-send and send duplicates. If scaling the `app` service is ever considered, add a `claimed_until` timestamp to `OutboxEntry` and swap the claim transaction to set it (do not commit the send inside the claim transaction). For v1, scale horizontally via `api` replicas and `Notifications` drainer replicas via separate deployments, never the same container.

**Why not add a claimed state now?**
- It's speculative (topology is locked at one replica).
- Adding it later is a one-line migration + two code changes.
- The current design is correct for the stated topology.

**Test:** (No test needed; topology is an ops constraint, not tested in code.)

---

## N-8: Logging — Body Contradiction

**Contradiction:**
- §5.2: "Log the body at `Information` **only** in Development."
- §5.4 rule 10: "Send `entry.EmailBody ?? notification.Body`, and **never log either**."

**Problem:** The `EmailBody` is a secret token. It must never appear in logs, even dev logs. But `notification.Body` (redacted, in-app text) is safe to log for debug.

**Resolution:**

**Decision:** Never log `EmailBody`. Log `notification.Body` only in development and only for non-emailed kinds.

**Implementation:**

```csharp
// LoggingEmailSender.cs
public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct)
{
    // Only log body for non-secret contexts. The body is the in-app text; 
    // the secret (if any) is in EmailBody on the outbox and is never logged.
    if (_env.IsDevelopment())
    {
        _logger.LogInformation("EMAIL (not sent — no transport) to {To}: {Subject}",
                               message.To, message.Subject);
        // Never log message.Body here; it may have come from EmailBody.
        // The drainer is responsible for not passing secrets to EmailMessage.
    }
    return Task.FromResult(new EmailSendResult(EmailSendOutcome.Sent));
}
```

**Drainer rule (§5.4 rule 10) restated:**

> **Send the redacted body, never log it.** The message is built from `entry.EmailBody ?? notification.Body`. If `entry.EmailBody` is set, it is a secret (single-use token) and must not reach any log. If null, `notification.Body` is safe (redacted in-app text). The sender (`IEmailSender`) must not log the message body under any circumstances — only the metadata (To, Subject). Truncate exception messages before logging; if an exception echoes the message body, replace it with a fixed string.

**Test:** 
1. Invitation with `EmailBody` set → not logged.
2. Ticket update with null `EmailBody` → only in dev, and only metadata.
3. Exception that echoes message body → truncated, no secret in logs.

---

## Summary Table

| Gap | Decision | Notes |
|---|---|---|
| **N-1** | Bulk operations return 200 with aggregate count; one audited denial per operation | Per-id failures are silent; scope is enforced by the query filter |
| **N-6** | Drainer query joins to `Notifications`; no navigation property | Explicit join, safer than lazy loading |
| **N-7** | Keep rule 6 (commit before send); drop locking guarantee | Single-replica topology constraint instead |
| **N-8** | Never log `EmailBody`; log `notification.Body` in dev only for debug | Redacted body is safe; secret is never logged |

---

## Implementation Checklist

Use these decisions to resolve remaining gaps:

- [ ] **Gap 1** (IRecipientDirectory startup check): Add a check in `NotificationsRegistration.cs` that attempts to resolve `IRecipientDirectory` and throws if unavailable. Run this during `AddNotificationsSlice()` before the services are built, or add a hosted service that validates on startup and logs an error if missing.

- [ ] **Gap 3** (ID/timestamp generation): Add `ValueGeneratedOnAdd()` to `Notification.Id` and `Notification.CreatedAt` in `NotificationConfiguration`. Use `DateTimeOffset.UtcNow` inline when creating outbox entries, or inject `SystemClock` (per App §4).

- [ ] **Gap 6** (NotifyManyAsync semantics): Defined as: collapse duplicate `(RecipientUserId, EventKind, TicketId)` triples **after** filtering out self-notifications (rule E) **and** validating non-empty recipientUserId (rule 4.3). Return count of rows created. Invalid requests throw `InvalidOperationException` and fail the whole batch atomically (enlisting in caller's transaction).

- [ ] **Gap 13** (AccountSuspended): Mark it as "write-only; not emailed; audit events that produce it must clarify that the user will not read it in-app." Or drop it — the plan does not require it.

---

## Next Steps

With these resolved, the remaining gaps (2, 4, 5, 7, 8, 9, 10, 11, 12, 14, 15) are implementation details, not blockers. Proceed with building.
