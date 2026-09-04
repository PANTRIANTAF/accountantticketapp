# Notifications Slice — Implementation Status

**Status:** builds clean, 12 tests pass, **never run against a database.**

The previous version of this file claimed "✅ Ready for testing" and that all of phases 1–7 were
complete. That was wrong, and wrong in a way worth recording: it was written from what the code was
*meant* to do, immediately after `dotnet build` succeeded, with nothing executed. An audit then found
that the slice could not serve a single request — and that it broke the three previously working
slices too. A green build says nothing about a wiring mistake.

Do not trust a status claim in this file that is not backed by a test named in it.

---

## Defects found after the first pass, and fixed

Ordered by how badly they broke things.

| # | Defect | Effect |
|---|--------|--------|
| 1 | The four endpoint handlers were never registered in DI | Minimal APIs inferred each `handler` parameter as the **request body**, which throws when routing builds its matcher. All endpoint data sources build together, so **every route in the application 500'd**, including TicketTypes and Customers |
| 2 | `.RequireAuthorization()` on the group, with no authorization middleware in the app | `EndpointMiddleware` throws — every `/api/notifications/*` route 500'd, independently of #1 |
| 3 | `GetConnectionString("DefaultConnection")`; the key is `Default` everywhere else | Resolved to `null`, so the context had no connection string and failed on first use; drainer died every tick |
| 4 | Context registered on its own connection, not `RequestConnection` | `EnlistAsync` cannot hand a transaction to a different connection, so cross-slice atomicity was a silent no-op at best and a throw at worst |
| 5 | Denial path passed the *permission* name `MarkOwnNotificationRead` to `IAuditApi` | Not in `AuditActions.All`, so `AuditApi` threw → client-triggerable 500, after the marked rows had already been committed |
| 6 | Transient send failures set `status = 'Failed'` | The claim query and its partial index only match `Pending`, so a `Failed` row was never retried and never abandoned — one greylisting lost the email permanently |
| 7 | Per-entry `catch` only logged | `attempt_count` unchanged and `next_attempt_at` in the past, so the row was re-attempted **every poll forever** and could never abandon |
| 8 | `RecipientDirectoryStub` returned `null` | The drainer treats an unresolvable recipient as undeliverable: every invitation and password-reset link was marked `Skipped` and its `email_body` **destroyed**, silently |
| 9 | Over-length `Title`/`EmailBody` threw | Rolled back the caller's business transaction with a 500; a 200-char title is reachable from a 255-char TicketType `DisplayName` |
| 10 | Empty `notificationIds` threw `ArgumentException`; no batch cap | 500 instead of 422; 100,000 ids went into one `IN` list |
| 11 | Denial compared rows-changed against ids-asked-for | Re-marking your own already-read notification manufactured a `Denied` audit entry against an innocent user |
| 12 | `EmailStatus` fetched with a query per row | 16 round trips for a default page, growing with `PageSize` |
| 13 | `PollIntervalMs` vs the config key `PollIntervalSeconds` | Bound to nothing; the interval was the hardcoded default and config edits did nothing |
| 14 | `Backoff[AttemptCount - 1]` against a fixed 5-element array | `MaxAttempts: 10` threw `IndexOutOfRangeException`, swallowed by #7 |
| 15 | Exception messages written to logs and `last_error` verbatim | A transport echoing the request would put a single-use token into logs and a never-purged column |

Fixes for #1, #3 and #4 are pinned by `AccountantApp.Tests/Notifications/NotificationsRegistrationTests.cs`;
#5, #10, #11 and #12 by `NotificationsFlowTests.cs`. Each was confirmed to fail against the
un-fixed code, not just to pass against the fixed code.

---

## What is verified

- `dotnet build -warnaserror`: 0 warnings, 0 errors.
- 12 Notifications tests pass (55 across the solution, 0 failures).
- Schema ↔ EF mapping checked column by column: all 9 `notifications` and all 10
  `notification_outbox` columns have explicit `HasColumnName`, with lengths, nullability and
  `DateTimeOffset` ↔ `TIMESTAMPTZ` matching the DDL. Verified **by reading**, not by execution.
- `NotificationEvents.All` reflection returns exactly the 16 kinds; `Emailed` ⊆ `All`.
- Migration is picked up by the csproj glob and keyed correctly by `SqlMigrationRunner`.
- No `DELETE`/`Remove` anywhere in the slice; no HTTP endpoint creates a notification.

## What is NOT verified

- **Nothing has ever touched a real database.** Docker is not running, 5432 is closed, and there is
  no `docker-compose.yml` in the repo. Every CHECK constraint, index, partial index and column-name
  mapping in this slice is unexecuted. The plan's own success criteria require the PostgreSQL test
  to be *run, not skipped*; by that standard this slice is unverified, as are Audit, Customers and
  TicketTypes.
- The drainer has never completed an iteration. Its retry, abandon, backoff and secret-clearing
  paths are argued from reading only.
- `MarkAllNotificationsReadHandler` now uses `ExecuteUpdateAsync`, which the InMemory provider does
  not support, so it has **no test at all** until there is a PostgreSQL one.

---

## Deliberately left undone

- **Real `IEmailSender`.** `LoggingEmailSender` is a stub. `Notifications:Email:Enabled` is `false`
  in every environment including Development, and `Program.cs` now refuses to start if it is `true`
  while `IRecipientDirectory` is still the stub.
- **`FromAddress`/`FromName`** are bound and read by nothing: `EmailMessage` has no `From`. A real
  sender has to add it.
- **Outbox accumulation while mail is off.** Entries queue as `Pending` with plaintext bodies and
  nothing drains them. Preferred over the alternative, which destroyed them — but the retention
  question is open and belongs with the real sender.
- **Single-replica topology constraint.** Documented in `OutboxDrainer`'s summary rather than
  enforced; there is no claim locking, so two replicas would double-send.
- **Identity integration.** The stub now throws instead of answering, so this is fail-fast rather
  than silent.
