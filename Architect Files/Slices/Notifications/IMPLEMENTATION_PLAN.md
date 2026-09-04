# Notifications Slice — Implementation Plan

Build this **second**, after `Audit` and before every other slice. `Identity`, `Employees`, and
`Tickets` all call it, and `Identity` must implement an interface this slice defines.

Read these first. This plan is subordinate to all of them — where it disagrees with a numbered
document, the numbered document wins and this plan is wrong:

- [00-Glossary.md](../../00-Glossary.md)
- [01-DomainModel.md](../../01-DomainModel.md) — §7 defines Notification and the
  **accountless-Employee rule**; §9.2 (nothing is deleted)
- [02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) — **nobody reads another
  person's notifications**
- [03-SliceInventory.md](../../03-SliceInventory.md) — §2 (`Notifications` depends on `Audit`
  **only**), §3 rule 7 (**inverted dependencies**)
- [04-Infrastructure.md](../../04-Infrastructure.md) — §5a (**outbox LOCKED, transport
  UNDECIDED**)
- [App/GeneralAppArchitecture.md](../../App/GeneralAppArchitecture.md) — §4, §5, §6, §7, §8
- [Slices/Audit/IMPLEMENTATION_PLAN.md](../Audit/IMPLEMENTATION_PLAN.md) — build that first

---

## 0. Prerequisites — read before writing any code

### 0.1 What must already exist

| File | Where | Why |
|---|---|---|
| `Shared/Data/RequestConnection.cs` | `Shared/` | Every DbContext registers against the request's shared connection so the audit write can join the mutation's transaction. |
| `Shared/Data/IRequestTransaction.cs` | `Shared/` | The outbox row and the Notification row must commit together — see §5. |
| `Shared/Auth/CurrentUser.cs` | `Shared/` | Must already carry `Guid? CustomerId`. |
| `Shared/Authorization/IActionCatalogue.cs` | `Shared/` | This slice registers a fragment. |
| `Slices/Audit/ExternalInterfaces/IAuditApi.cs` | `Audit` | This slice's only permitted dependency. |
| `Shared/Auth/DevAuthHandler.cs` | `Shared/` | Without it nothing sets `HttpContext.User`, so **every endpoint returns `401`** and no success criterion below can be checked. Double-gated on `IsDevelopment()` **and** `DevAuth:Enabled`; role from `X-Dev-Role`. |

### 0.2 The permission checker — fail-closed

```csharp
Task RequireAsync(CurrentUser user, string action, object? scope = null,
                  CancellationToken ct = default);
```

1. **An unknown action name denies.** Never a default branch that allows.
2. **Every denial is audited** before the exception is thrown.
3. **It is `async` and callers `await` it.** A synchronous signature blocks a request thread on a
   database round-trip and, worse, lets an audit-write exception replace the `AppException(403)`
   so a denied caller gets a `500` and the denial is never recorded.

This slice's catalogue fragment is in §11.2.

### 0.3 The authorization rule for this slice is unusually simple, and unusually absolute

`02-AuthorizationMatrix.md`: **nobody reads another person's notifications.** Not an
`AccountantAdmin`, not a `CustomerAdmin` reading their Employee's, not for support purposes.

That has a concrete consequence that shapes every handler here: **there is no recipient parameter
on any read endpoint.** The recipient is always `CurrentUser.Id`, taken from the principal and
never from the request. A `recipientUserId` field on a request DTO is the vulnerability, so the
field must not exist — you cannot forget to validate a parameter you never accepted.

This is also why the `WhereInCustomerScope` filter is **not** used in this slice. Customer
scoping is too coarse: two Employees of the same Customer share a `CustomerId` and must not see
each other's notifications. Scoping here is per **user**, not per Customer.

### 0.4 Pagination

Use `Shared/Pagination/`. Default `PageSize` **15**, maximum **50**
(`App/GeneralAppArchitecture.md` §8 — these are the system-wide numbers; do not pick different
ones for this slice). A `PageSize` of 5,000 clamps to 50, and a `PageNumber` below 1 clamps
to 1. Default sort `created_at DESC, id DESC` — the `id` tiebreaker matters because a single
domain event can create several notifications in one transaction with identical timestamps, and
an unstable sort makes paging skip and repeat rows.

---

## 1. Database schema (SQL migration)

**File:** `Slices/Notifications/Infrastructure/Migrations/20260830_001_CreateNotificationsSchema.sql`

### Table: notifications

```sql
CREATE TABLE notifications (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The UserAccount that receives it. VARCHAR, not UUID, and NO foreign key:
    -- this slice may not depend on Identity. See section 8.
    recipient_user_id   VARCHAR(100) NOT NULL,

    -- The Ticket it concerns. Nullable: an invitation notification concerns no Ticket.
    -- No foreign key to tickets — Notifications must not depend on Tickets, and the
    -- dependency graph runs the other way.
    ticket_id           UUID NULL,

    -- Event kind, from the fixed catalogue in ExternalInterfaces/NotificationEvents.cs.
    event_kind          VARCHAR(100) NOT NULL,

    title               VARCHAR(200)  NOT NULL,
    body                VARCHAR(2000) NOT NULL,

    is_read             BOOLEAN     NOT NULL DEFAULT FALSE,
    read_at             TIMESTAMPTZ NULL,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

Note what is **not** here: the email delivery state. `01-DomainModel.md` §7 lists *"Email
delivery state, when the event kind is also emailed"* as part of Notification, but it lives on
the outbox row instead, and §2.3 explains why.

### Table: notification_outbox

```sql
CREATE TABLE notification_outbox (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The Notification this email is for. Same slice, so a real foreign key is correct here.
    notification_id     UUID NOT NULL REFERENCES notifications(id),

    -- Resolved at send time, not at enqueue time, so an address change is picked up.
    -- Recorded here once resolved, for diagnostics.
    resolved_email      VARCHAR(320) NULL,

    -- The email body, when it must differ from the notification body because it carries a
    -- secret. NULL means "use the notification's body". Blanked by the drainer on success.
    email_body          VARCHAR(4000) NULL,

    status              VARCHAR(20) NOT NULL DEFAULT 'Pending',
    attempt_count       INTEGER     NOT NULL DEFAULT 0,
    next_attempt_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_error          VARCHAR(1000) NULL,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    sent_at             TIMESTAMPTZ NULL
);
```

| Column | Note |
|---|---|
| `notification_id` | A real FK, because both tables belong to this slice. `UNIQUE` is **not** applied — see below. |
| `resolved_email` | `VARCHAR(320)`: 64-character local part + `@` + 255-character domain, the RFC maximum. Nullable because it is unknown until the drainer resolves it. |
| `email_body` | The **secret-bearing** variant of the body. See "Why this column exists" below. Nullable, and null is the normal case. |
| `status` | `'Pending'`, `'Sent'`, `'Failed'`, `'Abandoned'`, `'Skipped'`. Text, not a PostgreSQL enum — a new status must not need DDL. |
| `next_attempt_at` | Drives the backoff. Defaults to `NOW()` so the first attempt is immediate. |
| `last_error` | Capped at 1,000 characters. **Truncate in code before insert**; a provider exception message is not length-bounded, and an over-length insert raises `22001` which, under the transaction rule, would roll back the drainer's progress. |

### Why `email_body` exists — read this before removing it

`Identity` sends invitation and password-reset mail through this slice, and that mail must contain
a **single-use token in a link**. `user_account_tokens` deliberately stores only a SHA-256 hash of
that token, so that a reader of the database cannot mint a session
([Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §1). Putting the link in `notifications.body`
would defeat that entirely: `notifications` is never purged (`01-DomainModel.md` §9.2), so a live
token would sit in a permanently-retained table for its whole validity window, in plaintext.

So the two bodies are separated:

- `notifications.body` gets a **redacted** in-app body — *"A password reset link was emailed to
  you."* That is also the better in-app text: an invited user has no session yet and will never
  read it, and a reset link is useless to someone already logged in.
- `notification_outbox.email_body` gets the token-bearing text, and **the drainer sets it to
  `NULL` in the same update that sets `status = 'Sent'`**. The secret's lifetime is then bounded
  by delivery rather than by retention.

A caller that passes no `EmailBody` gets the current behaviour — the notification body is emailed
verbatim — and that is the case for every event kind except `Invited` and
`PasswordResetRequested`.

**No `UNIQUE` on `notification_id`.** It is tempting — one email per notification — but a retry
after an `Abandoned` row, or a future digest that re-sends, would then fail on a constraint
instead of doing something sensible. Enforce one-per-notification in the enqueue path, not the
schema.

### Indexes

```sql
-- The notification centre: this user's list, newest first. Every read endpoint uses it.
CREATE INDEX idx_notifications_recipient ON notifications (recipient_user_id, created_at DESC, id DESC);

-- The unread badge count. Partial: only unread rows are ever counted, and the unread set
-- stays small while the table grows forever.
CREATE INDEX idx_notifications_unread ON notifications (recipient_user_id)
    WHERE is_read = FALSE;

-- The drainer's only query. Partial on Pending: Sent rows accumulate forever and must not
-- be scanned. This index is what keeps the background loop cheap.
CREATE INDEX idx_outbox_due ON notification_outbox (next_attempt_at)
    WHERE status = 'Pending';
```

The partial index on `status = 'Pending'` is not an optimisation detail. Without it the drainer's
poll scans every row ever sent, every few seconds, forever.

### No deletes

Nothing here is deleted — `01-DomainModel.md` §9.2. Read notifications are **not** purged, sent
outbox rows are **not** purged, and there is no `deleted_at`. Marking as read is an `UPDATE` of
`is_read`/`read_at`, which is the only update in this slice.

---

## 2. EF Core entities and DbContext

### 2.0 Column naming — mandatory

The SQL above creates `snake_case` columns; EF's convention produces `PascalCase`. **They do not
match**, and every query fails with `column n.RecipientUserId does not exist`. Map every
property with `HasColumnName`, without exception. The in-memory provider ignores column names
entirely, which is why §12.1 exists.

Every timestamp is `DateTimeOffset` against `TIMESTAMPTZ`. Never `DateTime`.

### 2.1 `Core/Notification.cs`

```csharp
public sealed class Notification
{
    public Guid Id { get; set; }
    public string RecipientUserId { get; set; } = string.Empty;
    public Guid? TicketId { get; set; }
    public string EventKind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

`Notification` does **not** implement `ICustomerScoped`, and must not — see 0.3. It has no
`CustomerId` column at all, deliberately: adding one would invite `WhereInCustomerScope`, which
would let one Employee read a colleague's notifications.

### 2.2 `Core/OutboxEntry.cs`

```csharp
public sealed class OutboxEntry
{
    public Guid Id { get; set; }
    public Guid NotificationId { get; set; }
    public string? ResolvedEmail { get; set; }

    /// <summary>Secret-bearing email body. Null means "email the notification's body".
    /// Set to null again on successful send — see the drainer rules in §5.4.</summary>
    public string? EmailBody { get; set; }

    public string Status { get; set; } = OutboxStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

public static class OutboxStatus
{
    public const string Pending   = "Pending";
    public const string Sent      = "Sent";
    public const string Failed    = "Failed";     // transient; will be retried
    public const string Abandoned = "Abandoned";  // attempt cap reached; never retried
    public const string Skipped   = "Skipped";    // no address, or email disabled
}
```

`Failed` and `Abandoned` are different states and conflating them is the mistake to avoid.
`Failed` rows are picked up again after the backoff; `Abandoned` rows never are, and are what an
operator investigates.

### 2.3 Why delivery state lives on the outbox, not on Notification

`01-DomainModel.md` §7 lists email delivery state as a Notification field. Putting it on the
outbox row instead satisfies the requirement better, and the reasoning should be understood
rather than reversed:

- A Notification exists whether or not its kind is emailed. A delivery column on Notification is
  `NULL`/`NotApplicable` for most rows.
- Delivery has **attempts, timing, and an error** — three more columns that have nothing to do
  with the notification the user reads.
- The drainer updates delivery state repeatedly. Writing to `notifications` on every retry
  contends with the read path the notification centre uses.

The read model still exposes it: `NotificationDto.EmailStatus` is projected by joining the
outbox, so the API surface matches §7 even though the storage does not. If you find yourself
adding an `email_status` column to `notifications`, re-read this.

### 2.4 DbContext: `Infrastructure/NotificationsDbContext.cs`

```csharp
public sealed class NotificationsDbContext : DbContext
{
    // Required. Without this constructor the context cannot be configured with a provider.
    public NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : base(options) { }

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<OutboxEntry> Outbox => Set<OutboxEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new NotificationConfiguration());
        builder.ApplyConfiguration(new OutboxEntryConfiguration());
    }
}
```

Two entities, both owned by this slice. No navigation property from `OutboxEntry` to
`Notification` is required; if you add one, it must not be a lazy-loading proxy — the drainer
runs outside a request and a lazy load there opens a connection nobody disposes.

### 2.5 Configurations

`Infrastructure/Configurations/NotificationConfiguration.cs` and `OutboxEntryConfiguration.cs`.
Every property gets `HasColumnName` and, for strings, `HasMaxLength` matching §1 exactly. The
lengths are not decoration — §4.0 rule D validates against them, and a mismatch means the
validation passes and the insert fails.

---

## 3. The event catalogue

**File:** `Slices/Notifications/ExternalInterfaces/NotificationEvents.cs`

`01-DomainModel.md` §7: the event kind comes from *"a fixed catalogue defined in the
Notifications slice spec"*. This is that catalogue. It lives in `ExternalInterfaces/`, not
`Core/`, because every calling slice must be able to name an event and dependency rule 2 forbids
referencing another slice's `Core`.

```csharp
namespace AccountantApp.Api.Slices.Notifications.ExternalInterfaces;

public static class NotificationEvents
{
    // --- Identity ---
    public const string Invited              = "Invited";
    public const string PasswordResetRequested = "PasswordResetRequested";
    public const string AccountSuspended     = "AccountSuspended";

    // --- Tickets: to the Customer side ---
    public const string TicketPickedUp       = "TicketPickedUp";
    public const string InformationRequested = "InformationRequested";   // → AwaitingInformation
    public const string FieldRejected        = "FieldRejected";
    public const string TicketAnswered       = "TicketAnswered";
    public const string TicketClosed         = "TicketClosed";
    public const string TicketCancelled      = "TicketCancelled";
    public const string AccountantResponded  = "AccountantResponded";

    // --- Tickets: to the Office ---
    public const string TicketSubmitted      = "TicketSubmitted";
    public const string CorrectionSubmitted  = "CorrectionSubmitted";
    public const string CustomerReplied      = "CustomerReplied";
    public const string TicketAssignedToYou  = "TicketAssignedToYou";
    public const string DueDateApproaching   = "DueDateApproaching";

    // --- Employees ---
    public const string EmployeeInvited      = "EmployeeInvited";

    // In-app only, and deliberately so -- see §3 rule 6.
    public const string EmployeeRegistered   = "EmployeeRegistered";
    public const string EmployeeDeparted     = "EmployeeDeparted";

    public static readonly IReadOnlySet<string> All = /* reflection over the constants above */;

    /// <summary>Kinds that are also emailed. Everything else is in-app only.</summary>
    public static readonly IReadOnlySet<string> Emailed = new HashSet<string>(StringComparer.Ordinal)
    {
        Invited, PasswordResetRequested, InformationRequested, FieldRejected,
        TicketAnswered, TicketClosed, EmployeeInvited
    };
}
```

Rules:

1. **`All` is built by reflection over the public string constants**, not hand-copied. A
   hand-maintained duplicate drifts silently, and a kind missing from `All` makes a legitimate
   call throw.
2. **A test asserts `All` is non-empty and contains a known constant.** A reflection filter that
   matches nothing turns rule 1 into "accept anything".
3. **A test asserts `Emailed` is a subset of `All`.** A typo in the `Emailed` set would otherwise
   mean an event is silently never emailed.
4. **`Emailed` is deliberately small.** Only things a person must act on, or cannot discover any
   other way, generate mail. Every ticket status change arriving as an email trains recipients to
   ignore all of them, including the invitation.
5. **`DueDateApproaching` is produced by the `Tickets` due-date scanner** (amended 2026-09-02, from
   [the Tickets plan](../Tickets/IMPLEMENTATION_PLAN.md) §13 item 8, **authorized**). It was
   producerless until then. `01-DomainModel.md` §9.2 now permits two hosted services and enumerates
   both. **Still do not build a scheduler in this slice** — the scanner lives in `Tickets`, which owns
   `tickets.due_date`, and it calls `INotificationApi` like every other caller. This slice cannot host
   it without depending on `Tickets`, which is a cycle.

   **It stays out of `Emailed`, deliberately.** It goes to the Office, whose staff are in the
   application daily, and a reminder that mails every Accountant every morning about every ticket
   approaching its date is exactly the training-recipients-to-ignore-mail failure rule 4 above warns
   about. In-app is where a reminder about your own queue belongs. Revisit only with a stated
   instruction.
6. **`EmployeeRegistered` and `EmployeeDeparted` are in `All` and deliberately NOT in `Emailed`**
   (added 2026-09-02, from [the Employees plan](../Employees/IMPLEMENTATION_PLAN.md) §13 item 6).
   Both go to the Customer's own Customer Admins, who are the people whose staff list just changed.
   They stay in-app because neither is something the recipient must act on and both are things a
   Customer Admin often did themselves a second earlier — mailing those is rule 4's failure mode
   with a name. Do not move them into `Emailed` to "make sure the Admin sees it"; the Admin sees it
   in the notification list, which is what the list is for.

   > `EmployeeRegistered` fires from `RegisterEmployeeHandler` and `EmployeeDeparted` from
   > `DepartEmployeeHandler`, both inside the caller's transaction. Neither is addressed to the
   > Employee concerned: a newly registered one has no account to receive it, and a departed one's
   > account has just been suspended. Rule E's self-exclusion already keeps the acting Admin off
   > their own notification, so an Admin acting alone gets nothing — correct, and not a bug.

   **There is no `EmployeeReinstated` kind.** Reinstatement is a correction of a mistake, and
   telling every Admin that a departure they may not have seen has been undone is noise about an
   event that, done properly, never should have been visible. The audit entry is the record.

---

## 4. The `INotificationApi` contract

**Files:** `Slices/Notifications/ExternalInterfaces/INotificationApi.cs`, `NotificationApi.cs`

### 4.1 The contract

```csharp
public sealed record NotificationRequest(
    string RecipientUserId,
    string EventKind,
    string Title,
    string Body,
    Guid? TicketId = null,

    /// <summary>
    /// Set this ONLY when the email must say something the stored notification must not — in
    /// practice, when it carries a single-use token link. When set, <paramref name="Body"/> is
    /// what gets stored and shown in the app, and this is what gets emailed. When null, the
    /// body is used for both. See §1, "Why email_body exists".
    /// </summary>
    string? EmailBody = null);

public interface INotificationApi
{
    /// <summary>
    /// Creates one notification and, when the kind is emailed, its outbox row — both inside
    /// the caller's transaction. Returns the notification id.
    /// </summary>
    Task<Guid> NotifyAsync(NotificationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Creates many in one call. Use this rather than a loop of NotifyAsync: one round trip,
    /// one SaveChanges, and duplicate recipients are collapsed.
    /// </summary>
    Task<int> NotifyManyAsync(IReadOnlyCollection<NotificationRequest> requests,
                              CancellationToken ct = default);
}
```

### 4.2 Rules for the implementation

**A. Enlist in the caller's transaction.** Call `IRequestTransaction.EnlistAsync(_db, ct)` before
adding rows, exactly as `AuditApi` does. A notification for an event that then failed to commit
is a lie told to a user, and it is the harder direction to debug — the user sees a ticket state
that does not exist.

**B. An unknown `EventKind` throws `InvalidOperationException`.** Programming error, caught by
tests, failing at the point of the mistake. Silently accepting an uncatalogued kind produces
notifications the UI cannot render a label for.

**C. Validate lengths and truncate the body, reject the title.** `Title` over 200 characters is a
caller bug → `InvalidOperationException`. `Body` over 2,000 characters is **truncated** with an
ellipsis, because a body can legitimately embed a rejection reason typed by an Accountant, and
that is user input which must never produce a `500` — `App/GeneralAppArchitecture.md` §8: if a
client can trigger it by sending a value, it is a `4xx`, and here it should not even be that.

**D. `NotifyManyAsync` collapses duplicate `(RecipientUserId, EventKind, TicketId)` triples.**
The `Tickets` slice will legitimately compute overlapping recipient sets — the Creator may also
be the Subject, and a Customer Admin may be both. Sending one person the same notification twice
for one event is the most visible defect this slice can ship.

**E. Never notify the actor about their own action.** The caller passes recipients, but this
method is the last line of defence: the `Tickets` slice will get this wrong at least once. Resolve
`CurrentUser` lazily from the service provider — as `AuditApi` does, and for the same reason —
and drop any request whose `RecipientUserId` equals the current caller's `Id`. Log at debug when
dropping; do not throw.

> Resolve `CurrentUser` **lazily**, not by constructor injection. `INotificationApi` may be
> called from a path where no principal exists, and eagerly resolving `CurrentUser` throws
> `AppException(401)` while constructing the object that was supposed to send the notification.

**F. Enqueue an outbox row only when `NotificationEvents.Emailed` contains the kind.** One row
per notification, in the same `SaveChanges`. Copy `request.EmailBody` onto `OutboxEntry.EmailBody`
verbatim — do **not** truncate it and do **not** fall back to `Body` here; null already means
"use the body", and the drainer resolves that at send time.

> **If `EmailBody` is set on a kind that is not in `Emailed`, throw `InvalidOperationException`.**
> No outbox row would be created, so the email would never be sent, and the caller believes it
> delivered a token. Silence here means an invited user waits forever for mail that was never
> queued. Reject the combination at the boundary.

**F2. `EmailBody` over 4,000 characters throws `InvalidOperationException`.** Unlike `Body`, it is
**not truncated** — truncating a body that contains a link produces a broken link, which is worse
than a rejected call, and every `EmailBody` in the system is composed from a template by a slice,
not typed by a user. This is the mirror image of rule C and the difference is deliberate.

**G. Do not resolve the recipient's email address here.** That happens in the drainer, at send
time. Resolving at enqueue time freezes an address that may change before the mail goes out, and
it puts an `IRecipientDirectory` call — which reaches into `Identity` — inside the caller's
transaction.

**H. Audit nothing.** Creating a notification is not in the audited action set
(`01-DomainModel.md` §8) and would double the audit volume for no investigative value. The
domain event that caused it is already audited by the slice that raised it.

### 4.3 The accountless-Employee rule — the one domain rule this slice must not get wrong

`01-DomainModel.md` §7:

> An accountless Employee has no UserAccount and therefore receives no notifications. When a
> Ticket's Subject is accountless, notifications about it go to the **Creator**. This is a real
> consequence of the accountless model and must be handled explicitly rather than producing an
> orphaned notification.

**Where it is enforced: in `Tickets`, when building the recipient list — not here.** This slice
receives `RecipientUserId` values and cannot tell an accountless Employee from anything else; it
has no Employee concept and may not call `Employees`.

What this slice must do is make the failure impossible to ignore:

1. **Reject a null, empty, or whitespace `RecipientUserId`** with `InvalidOperationException`.
   That is the shape an accountless Employee takes when a caller forgets the rule — the
   `UserAccountId` is absent — and it must fail loudly at the boundary rather than inserting an
   orphaned row that no one will ever read.
2. **Do not "helpfully" skip it.** Silently dropping an empty recipient is how the rule gets
   forgotten: the ticket flow keeps working and nobody is ever told anything.
3. The `Tickets` and `Employees` plans carry the positive rule — substitute the Creator. Their
   tests assert it. This plan carries the guard.

---

## 5. Email delivery

Read [04-Infrastructure.md](../../04-Infrastructure.md) §5a first. The delivery model is
**LOCKED**; the transport is **UNDECIDED**.

### 5.1 `IEmailSender` — the seam

**File:** `Slices/Notifications/Application/IEmailSender.cs` (internal to the slice; no other
slice may reference it)

```csharp
public sealed record EmailMessage(string To, string Subject, string Body);

public enum EmailSendOutcome { Sent, TransientFailure, PermanentFailure }

public sealed record EmailSendResult(EmailSendOutcome Outcome, string? Error = null);

public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct);
}
```

**It returns a result rather than throwing**, and it distinguishes transient from permanent.
That distinction is the whole retry policy: a `421`/`450` greylisting or a socket timeout is
transient and must be retried; a `550 no such mailbox` is permanent and retrying it 10 times
achieves nothing but a worse sender reputation. A sender that throws for both forces the drainer
to parse exception messages to tell them apart.

### 5.2 The only implementation in v1: `LoggingEmailSender`

```csharp
internal sealed class LoggingEmailSender : IEmailSender
{
    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct)
    {
        _logger.LogInformation("EMAIL (not sent — no transport configured) to {To}: {Subject}",
                               message.To, message.Subject);
        return Task.FromResult(new EmailSendResult(EmailSendOutcome.Sent));
    }
}
```

**Do not add a provider SDK, an API key, or a secret.** The transport is undecided
(04-Infrastructure §5a). When you reach the point of needing real mail, **stop and ask**: the
operator said "Twilio", and Twilio's own API is SMS and verification while its email product is
SendGrid — different library, different secret, different failure modes. If the intended channel
is SMS rather than email, that is a larger change than a new `IEmailSender`: `01-DomainModel.md`
§7 specifies an *email* delivery state and no phone number is stored on a UserAccount.

Log the body at `Information` **only** in Development. A notification body can contain a
rejection reason referencing payroll data, and production logs are not the place for it. Gate it
on `IHostEnvironment.IsDevelopment()`.

### 5.3 `IRecipientDirectory` — the inverted dependency

**File:** `Slices/Notifications/ExternalInterfaces/IRecipientDirectory.cs`

This slice needs a recipient's email address. It may depend on `Audit` only, and
`Identity → Notifications` already exists, so an edge to `Identity` would be a cycle. The
resolution is dependency inversion, licensed by
[03-SliceInventory.md](../../03-SliceInventory.md) §3 rule 7: **this slice defines the interface,
`Identity` implements it.**

```csharp
namespace AccountantApp.Api.Slices.Notifications.ExternalInterfaces;

public sealed record Recipient(string UserAccountId, string Email, string DisplayName,
                              bool IsActive);

public interface IRecipientDirectory
{
    Task<Recipient?> FindAsync(string userAccountId, CancellationToken ct);
}
```

Rules:

1. **This slice never references `Identity`.** It injects `IRecipientDirectory` and nothing more.
   The reference direction is `Identity → Notifications`, matching the permitted table.
2. **`Identity` registers the implementation** in `IdentityRegistration.cs`.
3. **An unregistered implementation must fail at startup, not on first send.** Add a startup
   check that resolves `IRecipientDirectory` once and throws a clear
   `InvalidOperationException` naming the missing registration. A background loop discovering the
   gap at 3 a.m. logs an obscure DI error nobody reads.
4. **Until `Identity` exists, register a stub** in `NotificationsRegistration.cs` guarded by
   `TryAddScoped`, returning `null` for every lookup. `TryAdd` so that `Identity`'s real
   registration wins whenever it is present regardless of `Program.cs` ordering. Delete the stub
   in the commit that adds `Identity` — the same discipline as `DevAuthHandler`.
5. `FindAsync` returning `null` means "no such account". `IsActive == false` means suspended.
   The drainer treats them differently — see 5.4 rule 4.

### 5.4 `OutboxDrainer` — the one hosted service in the system

**File:** `Slices/Notifications/Infrastructure/OutboxDrainer.cs`, a `BackgroundService`.

```
loop until stopping:
    try:
        using scope = _scopeFactory.CreateScope()
        db     = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>()
        dir    = scope.ServiceProvider.GetRequiredService<IRecipientDirectory>()
        sender = scope.ServiceProvider.GetRequiredService<IEmailSender>()

        due = await db.Outbox
            .Where(o => o.Status == OutboxStatus.Pending && o.NextAttemptAt <= now)
            .OrderBy(o => o.NextAttemptAt)
            .Take(BatchSize)              // 20
            .ToListAsync(ct)

        foreach entry in due:
            process(entry)                // see below
        await db.SaveChangesAsync(ct)

    catch (OperationCanceledException) when (stopping):
        break                             // normal shutdown, not an error
    catch (Exception ex):
        _logger.LogError(ex, "Outbox drain iteration failed.")
        // swallow: the loop must survive. Never let one bad row stop all mail.

    await Task.Delay(PollInterval, ct)    // 30 seconds
```

Rules, each of which corresponds to a way this class breaks in production:

**1. Create a DI scope per iteration.** `NotificationsDbContext` is scoped; a `BackgroundService`
is a singleton. Injecting the context into the constructor captures one instance for the process
lifetime — it accumulates tracked entities forever and its connection eventually dies, after
which every send fails until restart. Inject `IServiceScopeFactory`.

**2. It must not use `RequestConnection`.** There is no request, so `IHttpContextAccessor` is
null and the request-scoped connection is meaningless. The drainer's context needs its **own**
connection. Register a second options configuration for this purpose, or resolve the connection
string directly — and say so explicitly in `NotificationsRegistration.cs`, because a builder who
reuses `RequestConnection` gets a `NullReferenceException` at startup with no obvious cause.

**3. Never throw out of `ExecuteAsync`.** An unhandled exception silently stops the service for
the process lifetime, with no email and no error after the first one. Catch broadly, log, and
continue.

**4. Per-entry outcomes:**

| Situation | Result |
|---|---|
| Recipient not found (`null`) | `Skipped`, `last_error = "No such account"`. Not `Failed` — retrying will never find them. |
| Recipient suspended (`IsActive == false`) | `Skipped` — **unless the event kind is an invitation.** See the boxed note below; this exception is load-bearing. |
| Email delivery disabled by configuration | `Skipped`. Not `Pending` — leaving them pending means a flood the moment mail is enabled. |
| `Sent` | `status = 'Sent'`, `sent_at = now`, `resolved_email` recorded, **`email_body` set to `NULL`** in the same update. |
| `TransientFailure` | `attempt_count++`; if under the cap, `status` stays `Pending` with `next_attempt_at` pushed out by the backoff; at the cap, `Abandoned`. |
| `PermanentFailure` | `Abandoned` immediately. Do not consume retries on a rejected address. |
| An exception from `SendAsync` | Treat as `TransientFailure`, and record the exception message **truncated to 1,000 characters**. |

> **The invitation exception, and why skipping it breaks the whole application.**
>
> An invited account has not accepted yet, so it is not `Active`, so `IsActive` is `false`. Apply
> the suspended-recipient rule literally and the invitation email is `Skipped` — which means
> **no invitation is ever delivered to anybody, and the only accounts that can log in are the
> seeded first Admin.** Nothing reports an error: the outbox row is `Skipped`, which reads as a
> deliberate decision, and the invited person simply never hears anything.
>
> This is the highest-severity failure in this plan, because it is silent, it is total, and the
> rule that causes it is correct for every other event kind.
>
> So the check is not `if (!recipient.IsActive) → Skipped`. It is:
>
> ```csharp
> // An invitee is NOT Active yet -- that is the entire point of an invitation.
> // Suppressing these makes the application unusable, silently.
> var isInvitation = entry.EventKind is NotificationEvents.Invited
>                                     or NotificationEvents.EmployeeInvited;
>
> if (!recipient.IsActive && !isInvitation)
>     return OutboxOutcome.Skipped("Recipient is not active");
> ```
>
> **Both kinds.** `Identity`'s `InviteAccountantHandler` uses `Invited`; its
> `InviteEmployeeAccountAsync` uses `EmployeeInvited`. Handling only the first means Employees
> are never invited while Accountants are — a partial failure that is harder to notice than a
> total one.
>
> `PasswordResetRequested` is **not** in the exception: a suspended user must not be able to
> regain access by resetting their password. Keep the allow-list to the two invitation kinds and
> add nothing to it without a reason written down here.
>
> **Two tests, and they must assert on the delivery, not on the enqueue.** (1) An invited,
> not-yet-active account receives its invitation email. (2) A **suspended** account whose event
> kind is `AccountSuspended` or `PasswordResetRequested` does not. A test that only checks the
> outbox row was created passes in every one of these cases, including the broken one.

**5. Backoff is capped exponential with a maximum attempt count.** `1m, 5m, 15m, 1h, 6h`, then
`Abandoned` at attempt 6. Never unbounded retries: an outbox that retries forever against a
misconfigured host is a self-inflicted denial of service on your own sending reputation.

**6. Do not send inside a transaction that spans the send.** Save the status update after the
send returns. If the process dies between the send and the save, the row is retried and one
duplicate email goes out — the correct trade, because the alternative (mark sent, then send) loses
mail silently. Say this in a comment; a later reader will otherwise "fix" it.

**7. Guard against two instances.** Production is one `app` container, so two drainers should not
exist — but a mistake in scaling would double every email. Use
`SELECT ... FOR UPDATE SKIP LOCKED` when claiming the batch, so a second instance takes different
rows instead of the same ones. This is a raw-SQL claim step; EF's `Take` alone gives no locking.

**8. It is registered once and only if enabled.** `Notifications:Email:Enabled` false means do not
register the service at all, rather than registering one that returns immediately — an unregistered
service cannot log a confusing warning every 30 seconds.

**9. Configuration** binds from `Notifications:Email`: `Enabled`, `FromAddress`, `FromName`,
`PollIntervalSeconds`, `BatchSize`, `MaxAttempts`. No secret yet; there is no transport.

**10. Send `entry.EmailBody ?? notification.Body`, and never log either.** The coalesce is the
whole mechanism from §1 — an `EmailMessage` built from `notification.Body` unconditionally silently
emails the redacted text, so an invited user receives *"An invitation was emailed to you"* and no
link, and nothing errors. Two further rules follow from `EmailBody` being a secret:

- **Clear it on `Sent`, and also on `Abandoned`.** `Sent` is in the table above; `Abandoned` needs
  it too, because an abandoned row is retained forever and would otherwise be the exact permanent
  plaintext-token row this design exists to prevent. `Failed`/`Pending` rows keep it — they will be
  retried and still need it.
- **It must never reach a log line or `last_error`.** Log the outbox row's `id`, never its content.
  A provider exception that echoes the message body must be truncated and, if it might contain the
  body, replaced with a fixed string. Logs are retained and are read by people who are not the
  recipient.

---

## 6. DTOs

**Folder:** `Slices/Notifications/Application/Dtos/`

| DTO | Shape |
|---|---|
| `NotificationDto` | `Id`, `TicketId`, `EventKind`, `Title`, `Body`, `IsRead`, `ReadAt`, `CreatedAt`, `EmailStatus` (string?, projected from the outbox — see 2.3) |
| `ListMyNotificationsRequestDto` | `UnreadOnly` (bool, default false), `PageNumber`, `PageSize` |
| `UnreadCountResponseDto` | `UnreadCount` (int) |
| `MarkReadRequestDto` | `NotificationIds` (`List<Guid>`) |
| `MarkReadResponseDto` | `MarkedCount` (int) |

**No DTO in this slice has a `RecipientUserId` field.** Not on a request, not on a response. See
0.3: the recipient is always the caller, and a field you never accept cannot be used to read
someone else's notifications. A response field would be redundant and would make the omission
look accidental.

Request DTOs are plain classes with public getters and setters, not positional `record`s —
minimal-API binding from a query string does not populate positional records.

---

## 7. Handlers

**Folder:** `Slices/Notifications/Application/Handlers/`

### 7.0 Rules that apply to every handler in this slice

Canonical signature, no mediator, one handler per operation:

```csharp
public async Task<TResponse> Handle(TRequest req, CurrentUser user, CancellationToken ct)
```

**A. Every handler filters by `user.Id` and by nothing else.** Not `CustomerId`, not a request
field. `.Where(n => n.RecipientUserId == user.Id)` is the first clause of every query in this
slice, including single-record operations.

**B. Every role has the same rights here.** All four roles may read and mark their own
notifications, and none may touch another's. There is no Admin override — an `AccountantAdmin`
reading a Customer Admin's notifications is exactly what `02-AuthorizationMatrix.md` forbids. So
the catalogue grants these actions to all four roles, and the *scope* check does the real work.
Do not conclude from "all four roles" that the permission check is pointless: it still audits
denials and still refuses an unknown action.

**C. Out of scope is `404`, never `403`.** A `403` on someone else's notification id confirms it
exists. Because the `user.Id` filter removes the row, the natural result is already "not found" —
do not add a second lookup to produce a better error message.

**D. No handler here writes an audit entry**, and read handlers open no transaction. See 4.2 H.

**E. Validate against the lengths in §1**, and clamp paging per 0.4.

**F. `AsNoTracking()` on reads.** `MarkAsRead` needs tracking; nothing else does.

### 7.1 `ListMyNotificationsHandler`

Request `ListMyNotificationsRequestDto` → `PaginatedResponse<NotificationDto>`

```
await _permissions.RequireAsync(user, "ReadOwnNotifications", ct: ct)
clamp page/size (default 15, max 50)

query = _db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == user.Id)
if req.UnreadOnly → query = query.Where(n => !n.IsRead)

total = await query.CountAsync(ct)
items = await query
    .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
    .Skip((page - 1) * size).Take(size)
    .Select(n => new NotificationDto { /* ... */
        EmailStatus = _db.Outbox.Where(o => o.NotificationId == n.Id)
                               .Select(o => o.Status).FirstOrDefault() })
    .ToListAsync(ct)
```

The correlated subquery for `EmailStatus` is fine at the maximum page size of 50 and keeps the
projection in one round trip. Do **not** replace it with a per-item lookup in a `foreach` after
`ToListAsync` — that is N+1, and at the maximum page size it is 51 queries.

### 7.2 `GetUnreadCountHandler`

Request: none → `UnreadCountResponseDto`

```
await _permissions.RequireAsync(user, "ReadOwnNotifications", ct: ct)
return new UnreadCountResponseDto {
    UnreadCount = await _db.Notifications
        .CountAsync(n => n.RecipientUserId == user.Id && !n.IsRead, ct) }
```

This is the badge on every screen, so the SPA will poll it. It must be a `COUNT` served by
`idx_notifications_unread` and must never materialise rows. Do not implement it as
`ListMyNotifications(unreadOnly).Count`.

### 7.3 `MarkNotificationsReadHandler`

Request `MarkReadRequestDto` → `MarkReadResponseDto`

```
await _permissions.RequireAsync(user, "MarkOwnNotificationsRead", ct: ct)

if req.NotificationIds is null or empty      → 422 "At least one notification id is required."
if req.NotificationIds.Count > 200           → 422 "At most 200 at a time."

var ids = req.NotificationIds.Distinct().ToList()
await using var tx = await _transaction.BeginAsync(_db, ct)

var rows = await _db.Notifications
    .Where(n => n.RecipientUserId == user.Id && ids.Contains(n.Id) && !n.IsRead)
    .ToListAsync(ct)

foreach r in rows: r.IsRead = true; r.ReadAt = DateTimeOffset.UtcNow
await _db.SaveChangesAsync(ct)
await tx.CommitAsync(ct)

return new MarkReadResponseDto { MarkedCount = rows.Count }
```

Rules:

1. **The `RecipientUserId == user.Id` clause is inside the same `Where` as `ids.Contains`.** Not a
   separate validation pass, not a check after loading. This is the single line that stops one
   user marking another's notifications read, and separating it is how it gets dropped in a
   refactor.
2. **Ids that are unknown, already read, or belong to someone else are silently skipped** and
   simply not counted. `MarkedCount` may legitimately be less than the number of ids sent, and
   the caller cannot distinguish which reason applied. That is deliberate: reporting "3 of 5 were
   not yours" is an enumeration oracle.
3. **A cap of 200 ids.** Unbounded `IN` lists produce enormous query plans and are a cheap
   denial-of-service vector. This 200 is **not** the pagination maximum (which is 50) and is not
   derived from it — it bounds one `IN` list, and a client may legitimately mark several pages'
   worth read in one call. Do not "align" the two numbers.
4. **`Distinct()` first**, so a duplicated id does not inflate the count.
5. Marking read is idempotent. Calling it twice yields `MarkedCount = 0` the second time, not an
   error.

### 7.4 `MarkAllNotificationsReadHandler`

Request: none → `MarkReadResponseDto`

```
await _permissions.RequireAsync(user, "MarkOwnNotificationsRead", ct: ct)
await using var tx = await _transaction.BeginAsync(_db, ct)

var marked = await _db.Notifications
    .Where(n => n.RecipientUserId == user.Id && !n.IsRead)
    .ExecuteUpdateAsync(s => s
        .SetProperty(n => n.IsRead, true)
        .SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), ct)

await tx.CommitAsync(ct)
return new MarkReadResponseDto { MarkedCount = marked }
```

`ExecuteUpdateAsync` is correct here and is the one place in this slice where a set-based update
beats loading entities: a user with 5,000 unread notifications would otherwise materialise all
5,000. Note that `ExecuteUpdateAsync` **bypasses the change tracker**, so it must not be mixed
with tracked modifications to the same rows in one handler — it does not here.

The `user.Id` filter is still the first clause. An `ExecuteUpdateAsync` without it marks the whole
table read.

### 7.5 `NotificationMapper`

`Application/NotificationMapper.cs`. As with `Audit`, if a mapping method is used inside an EF
`.Select(...)`, it must be an `Expression<Func<...>>` or written inline — a static method with
statements is either untranslatable or silently evaluated client-side after fetching every
column.

---

## 8. Cross-slice boundaries

`Notifications` depends on **`Audit` only** ([03-SliceInventory.md](../../03-SliceInventory.md)
§2), plus the inverted `IRecipientDirectory` it defines itself.

1. **No foreign key on `recipient_user_id`.** A FK to a UserAccount table would make this slice
   depend on `Identity` and invert the graph.
2. **No foreign key on `ticket_id`.** Same reason, and the graph runs `Tickets → Notifications`.
   A FK would also mean a migration ordering constraint between two slices whose scripts both
   start at `001`.
3. **This slice never resolves a Ticket reference, a Customer name, or an Employee name.** The
   `Title` and `Body` arrive fully rendered from the calling slice, which has the data. If a
   notification needs to say `TKT-2026-000417`, `Tickets` puts that string in the body.
4. **This slice decides nothing about when an event happened** — 03's own wording: *"Deciding when
   a domain event happened — callers tell it."* There is no logic here that infers a status
   change.
5. **`IEmailSender` and `LoggingEmailSender` are internal to the slice.** No other slice sends
   mail, and nothing outside `Notifications` may reference them.
6. **`IRecipientDirectory` is the only inverted interface in v1.** Do not add a second without
   raising it — the pattern is easy to abuse into a hidden cycle.

---

## 9. Migrations

- Scripts in `Slices/Notifications/Infrastructure/Migrations/`, named
  `YYYYMMDD_###_Description.sql`.
- **EF Core migrations are not used.** Never run `dotnet ef migrations add` or
  `dotnet ef database update`.
- **The tracking key is the slice-relative path with forward slashes**, in
  `schema_versions.script_name VARCHAR(500)` — never `Path.GetFileName`. Sequence numbers restart
  at `001` per slice, so bare filenames collide across slices and the second one is silently
  skipped.
- This slice's key is exactly
  `Notifications/Infrastructure/Migrations/20260830_001_CreateNotificationsSchema.sql`.
- Both tables go in **one** script. They are created together and the FK between them requires
  ordering within the file: `notifications` before `notification_outbox`.

---

## 10. Endpoints

**File:** `Slices/Notifications/NotificationsEndpoints.cs`

Route shape `/api/{domain}/{action}`, path segments lowercase and **kebab-case at every word
boundary**. `notifications` is one word; `unread-count` and `mark-read` are two.

```csharp
var group = app.MapGroup("/api/notifications");

group.MapPost("/list",         ...);   // filters + paging in the body
group.MapGet ("/unread-count", ...);
group.MapPost("/mark-read",    ...);
group.MapPost("/mark-all-read",...);
```

| Route | Verb | Note |
|---|---|---|
| `/api/notifications/list` | `POST` | `POST` for the filter body. Non-mutating: no transaction, no audit. |
| `/api/notifications/unread-count` | `GET` | **Kebab-case.** `/unreadcount` is the doubled-letter class of typo the rule exists to prevent. |
| `/api/notifications/mark-read` | `POST` | Mutating. |
| `/api/notifications/mark-all-read` | `POST` | Mutating. |

- **No route parameters.** Never `/api/notifications/{id}`; the locked shape is
  `{domain}/{action}` and an identifier is not an action.
- **Query and body parameter names stay camelCase.** Kebab-case is for path segments only.
- Handlers are injected per endpoint; do not resolve them from `IServiceProvider` in the lambda.
- No `.RequireAuthorization()` policy names. Authorization is `IPermissionChecker` in the handler.
- There is **no** endpoint that creates a notification. Creation is `INotificationApi`, called
  from another slice, not reachable over HTTP. Do not add `/api/notifications/create` for
  testing.

---

## 11. Service registration

### 11.1 `Slices/Notifications/NotificationsRegistration.cs`

```csharp
public static IServiceCollection AddNotificationsSlice(
    this IServiceCollection services, IConfiguration configuration)
{
    // Request-scoped context on the shared connection, so NotifyAsync can join the caller's
    // transaction.
    services.AddDbContext<NotificationsDbContext>((sp, o) =>
        o.UseNpgsql(sp.GetRequiredService<RequestConnection>().Connection));

    services.AddScoped<INotificationApi, NotificationApi>();
    services.AddSingleton<IActionCatalogue, NotificationsActionCatalogue>();

    services.AddScoped<ListMyNotificationsHandler>();
    services.AddScoped<GetUnreadCountHandler>();
    services.AddScoped<MarkNotificationsReadHandler>();
    services.AddScoped<MarkAllNotificationsReadHandler>();

    // Placeholder until Identity ships. TryAdd, so Identity's real registration wins
    // regardless of the order of the Add*Slice calls in Program.cs. DELETE this line in the
    // commit that adds Identity — same discipline as DevAuthHandler.
    services.TryAddScoped<IRecipientDirectory, NullRecipientDirectory>();

    var email = configuration.GetSection("Notifications:Email");
    if (email.GetValue<bool>("Enabled"))
    {
        services.AddScoped<IEmailSender, LoggingEmailSender>();
        services.AddHostedService<OutboxDrainer>();
    }

    return services;
}
```

### 11.2 `Slices/Notifications/NotificationsActionCatalogue.cs`

```csharp
internal sealed class NotificationsActionCatalogue : IActionCatalogue
{
    public string SliceName => "Notifications";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } =
        new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
        {
            // All four roles, because everyone has their own notifications. The scope
            // restriction — you only ever see your own — is enforced by the user.Id filter
            // in every handler, not by the role list. See 0.3.
            ["ReadOwnNotifications"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                                        UserRole.CustomerAdmin,  UserRole.Employee],
            ["MarkOwnNotificationsRead"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                                            UserRole.CustomerAdmin,  UserRole.Employee]
        };
}
```

The word **Own** in both action names is load-bearing. A future `ReadNotifications` without it
would read as a general capability and invite an Admin override that `02-AuthorizationMatrix.md`
forbids.

### 11.3 What `Program.cs` adds

```csharp
builder.Services.AddNotificationsSlice(builder.Configuration);
// ...
app.MapNotificationsEndpoints();
```

Two lines, naming no handler or DbContext type.

### 11.4 Registration traps

1. **`AddScoped<NotificationsDbContext>()` instead of `AddDbContext`** — registers the context
   with no provider. If both are present the later wins and silently discards the options.
2. **The `o =>` overload instead of `(sp, o) =>`** — compiles and works, but the context gets its
   own connection, so `NotifyAsync` no longer joins the caller's transaction and a notification
   survives a rolled-back event. Invisible until you test it (§12.1 case 5).
3. **Giving `OutboxDrainer` a constructor-injected `DbContext`** — a singleton capturing a scoped
   context. It works for minutes and then fails permanently. Inject `IServiceScopeFactory`.
4. **Letting the drainer's context use `RequestConnection`** — there is no request, so it throws
   at startup or produces a null connection. It needs its own.
5. **`AddScoped<IRecipientDirectory, ...>` instead of `TryAddScoped`** for the stub — then
   registration order in `Program.cs` decides whether real email lookup works. Use `TryAdd`.
6. **Registering handlers in `Program.cs`** — forbidden. Assembly scanning is banned.
7. **`AddHostedService<OutboxDrainer>()` unconditionally** — a disabled-email deployment then runs
   a loop that logs every 30 seconds forever.

### 11.5 Startup smoke check — before writing tests

```bash
docker compose up -d db
dotnet build
dotnet run --project AccountantApp.Api
```

```bash
curl -i -H "X-Dev-Role: AccountantAdmin" http://localhost:5000/api/notifications/unread-count
curl -i -H "X-Dev-Role: Employee" -H "X-Dev-Customer-Id: <guid>" \
     http://localhost:5000/api/notifications/unread-count
```

Both `200` with a count of `0`. **A `401` from either proves nothing** except that `DevAuth` is
off — check `IsDevelopment()` and `DevAuth:Enabled` in `appsettings.Development.json`.

**Do not comment out `SqlMigrationRunner.RunAsync` to make startup succeed without a database.**
If it throws `Failed to connect`, start the database. A run that skips migrations has verified
nothing about the schema, which is the only thing that could have gone wrong.

---

## 12. Tests

### 12.1 At least one test must run against real PostgreSQL — mandatory

`Microsoft.EntityFrameworkCore.InMemory` is **banned from the API project**; it is a test-only
dependency. It ignores `HasColumnName`, `NOT NULL`, `TIMESTAMPTZ`, string lengths, partial
indexes, and foreign keys — every single thing §1 and §2 exist to get right. A green in-memory
suite is not evidence this slice works.

One test against a real database must cover:

1. **The migration applies** — `SqlMigrationRunner.RunAsync` succeeds on a scratch database.
2. **Tracked by slice-relative path** — `schema_versions.script_name` equals
   `Notifications/Infrastructure/Migrations/20260830_001_CreateNotificationsSchema.sql`, not the
   bare filename.
3. **A notification round-trips** through `NotificationApi` and `ListMyNotificationsHandler`,
   exercising every `HasColumnName` in both directions.
4. **`created_at` survives as an instant** — write a `DateTimeOffset` with a non-zero offset, read
   it back, assert `UtcDateTime` matches. This catches a `DateTime`/`TIMESTAMPTZ` mix-up, which
   produces wrong data rather than an error.
5. **A rolled-back transaction leaves no notification.** Call `NotifyAsync` inside a transaction,
   roll back, assert the table is empty. This is the only test that catches trap 11.4.2, and it is
   the whole reason `NotifyAsync` enlists.
6. **An emailed kind creates exactly one outbox row; a non-emailed kind creates none.**
7. **The drainer sends a `Pending` row and marks it `Sent`**, with `resolved_email` populated,
   `email_body` cleared, and the sent body taken from `EmailBody` when one was set — using a fake
   `IRecipientDirectory` and a fake `IEmailSender`.
8. **A `PermanentFailure` goes straight to `Abandoned`** without consuming retries.
9. **A `TransientFailure` stays `Pending` with `next_attempt_at` in the future**, and reaches
   `Abandoned` at the attempt cap.

Skip it **loudly** when no database is reachable — `Skip.IfNot(...)` with a message saying the
schema is unverified. Never let it pass silently.

### 12.2 Behavioural cases (in-memory acceptable)

| Case | Expected |
|---|---|
| Each of the four roles lists own notifications | `200` |
| A user lists notifications; another user's rows exist | only own rows returned |
| `MarkRead` with another user's notification id | `MarkedCount = 0`, no exception, that row still unread |
| `MarkRead` with an unknown id | `MarkedCount = 0` |
| `MarkRead` with an empty list | `422` |
| `MarkRead` with 201 ids | `422` |
| `MarkRead` with the same id twice | `MarkedCount = 1` |
| `MarkRead` called twice on one id | `1` then `0` |
| `MarkAllRead` | marks only the caller's rows; another user's stay unread |
| `GetUnreadCount` after marking all read | `0` |
| `UnreadOnly = true` | read rows excluded |
| Paging | ordered `created_at DESC, id DESC`; `PageSize` 5,000 clamps to 50; `PageNumber` 0 clamps to 1 |
| `NotifyAsync` with an uncatalogued `EventKind` | `InvalidOperationException` |
| `NotifyAsync` with an empty `RecipientUserId` | `InvalidOperationException` (the accountless-Employee guard, 4.3) |
| `NotifyAsync` with a 3,000-character body | stored truncated to 2,000, no exception |
| `NotifyAsync` with a 300-character title | `InvalidOperationException` |
| `NotifyAsync` where the recipient **is** the current caller | no row created |
| `NotifyManyAsync` with a duplicated `(recipient, kind, ticket)` triple | one row, not two |
| `NotificationEvents.All` | non-empty, contains `TicketSubmitted` |
| `NotificationEvents.Emailed` | a strict subset of `All` |
| Drainer: recipient not found | `Skipped`, not `Failed` |
| Drainer: recipient suspended | `Skipped` |
| Drainer: `IEmailSender` throws | `Failed`/`Pending`, error truncated to 1,000 chars, loop survives |
| Drainer: one bad row in a batch of 20 | the other 19 still processed |
| No `IRecipientDirectory` registered | startup throws, naming the missing registration |
| `NotifyAsync` with an `EmailBody` set | `notifications.body` holds the redacted text; `notification_outbox.email_body` holds the token text; the two differ |
| `NotifyAsync` with an `EmailBody` on a non-`Emailed` kind | `InvalidOperationException` |
| `NotifyAsync` with a 5,000-character `EmailBody` | `InvalidOperationException` — rejected, **not** truncated |
| Drainer sends a row whose `EmailBody` is set | the `EmailMessage.Body` is the `EmailBody`, **not** the notification body |
| Drainer sends a row whose `EmailBody` is null | the `EmailMessage.Body` is the notification body |
| Drainer marks a row `Sent` | `email_body` is `NULL` afterwards |
| Drainer marks a row `Abandoned` | `email_body` is `NULL` afterwards |
| Drainer marks a row `Failed`/`Pending` | `email_body` is **retained**, so the retry still has a link |

---

## 13. Known constraints

1. **Nobody reads another person's notifications**, and there is no override for any role. This is
   the slice's defining rule.
2. **No request or response DTO carries a recipient id.** 0.3 and §6.
3. **Nothing is deleted.** No purge of read notifications, no purge of sent outbox rows, no
   `deleted_at`. Retention is indefinite (`01-DomainModel.md` §9.2). The mitigations are the
   partial indexes in §1 and pagination, not deletion.
4. **The only mutation is `is_read`/`read_at`.** A notification's title, body, or kind is never
   edited after creation.
5. **The email transport is not chosen** (04-Infrastructure §5a). `LoggingEmailSender` is the only
   implementation. Do not add an SDK, an API key, or a secret. **Stop and ask** which Twilio
   product is meant before writing a real sender.
6. ~~**`DueDateApproaching` has no producer.** §3 rule 5. Do not build a scheduler for it.~~
   **SUPERSEDED — a producer is now authorized, and it is not in this slice.** The `Tickets` slice
   owns a due-date scanner (`Slices/Tickets/IMPLEMENTATION_PLAN.md` §13 item 8) which calls
   `INotificationApi` like any other caller. **Nothing changes in this slice**: `DueDateApproaching`
   was already in `NotificationEvents`, and this slice still never infers a domain event (rule 8
   below). Do not build a scheduler *here* — that part of the original rule stands, and now for a
   sharper reason than "nobody produces it".
7. **The outbox drainer is one of exactly two `IHostedService` implementations in the system**, the
   other being the `Tickets` due-date scanner. Neither is precedent for a retention job or any other
   background work that **removes** data — `01-DomainModel.md` §9.2, which now enumerates both and
   states that a third read-and-notify service needs its own explicit authorization.
8. **This slice raises no domain decisions.** Callers tell it what happened; it never infers.
9. **The accountless-Employee substitution belongs to `Tickets` and `Employees`.** This slice only
   refuses an empty recipient (4.3).

---

## 14. Questions to flag rather than answer

Stop and raise these. Do not invent a behaviour — [README.md](../../README.md) is explicit that a
gap should be flagged, not filled.

1. **Which Twilio product**, or which provider at all. SMS and email are different libraries,
   secrets, and failure modes, and an SMS channel would need a phone number that no entity
   currently stores.
2. **Whether an Accountant wants a notification per submitted ticket.** In a busy office
   `TicketSubmitted` to every Accountant could be dozens a day, which is how people learn to
   ignore the notification centre. A digest would need scheduled work, which is forbidden. Raise
   it rather than choosing.
3. **Who receives `TicketSubmitted`** — every Accountant, or nobody because the pickup queue is
   the real mechanism. `Tickets` owns the recipient list; flag it if that plan does not say.
4. **Whether a `CustomerAdmin` should be notified about all their Customer's tickets.** They have
   full visibility, so this could be high volume for a large Customer.
5. **Whether `Abandoned` rows should raise an operational alert.** `04-Infrastructure.md` §6 lists
   what to monitor; abandoned mail is not currently on it, and silent permanent failure of
   invitation emails would be discovered only by a user complaining.

---

## Files checklist

| File | Action |
|---|---|
| `Slices/Notifications/Core/Notification.cs` | New |
| `Slices/Notifications/Core/OutboxEntry.cs` | New (incl. `OutboxStatus`) |
| `Slices/Notifications/Infrastructure/NotificationsDbContext.cs` | New |
| `Slices/Notifications/Infrastructure/Configurations/NotificationConfiguration.cs` | New |
| `Slices/Notifications/Infrastructure/Configurations/OutboxEntryConfiguration.cs` | New |
| `Slices/Notifications/Infrastructure/Migrations/20260830_001_CreateNotificationsSchema.sql` | New |
| `Slices/Notifications/Infrastructure/OutboxDrainer.cs` | New |
| `Slices/Notifications/ExternalInterfaces/INotificationApi.cs` | New |
| `Slices/Notifications/ExternalInterfaces/NotificationApi.cs` | New |
| `Slices/Notifications/ExternalInterfaces/NotificationEvents.cs` | New |
| `Slices/Notifications/ExternalInterfaces/IRecipientDirectory.cs` | New |
| `Slices/Notifications/Application/IEmailSender.cs` | New |
| `Slices/Notifications/Application/LoggingEmailSender.cs` | New |
| `Slices/Notifications/Application/NullRecipientDirectory.cs` | New — **delete when `Identity` ships** |
| `Slices/Notifications/Application/NotificationMapper.cs` | New |
| `Slices/Notifications/Application/Dtos/*.cs` | New — 5 DTOs |
| `Slices/Notifications/Application/Handlers/*.cs` | New — 4 handlers |
| `Slices/Notifications/NotificationsActionCatalogue.cs` | New |
| `Slices/Notifications/NotificationsRegistration.cs` | New |
| `Slices/Notifications/NotificationsEndpoints.cs` | New |
| `appsettings.json` / `appsettings.Development.json` | Modify — `Notifications:Email` section, no secret |
| `Program.cs` | Modify — two lines |
| `AccountantApp.Tests/Notifications/NotificationsSchemaTests.cs` | New — PostgreSQL test |
| `AccountantApp.Tests/Notifications/NotificationsFlowTests.cs` | New — behavioural cases |
| `AccountantApp.Tests/Notifications/OutboxDrainerTests.cs` | New — drainer outcomes |

## Success criteria

1. `dotnet build` produces **0 errors and 0 warnings**.
2. `docker compose up -d db` then `dotnet run` starts, applies the migration, and logs the
   `DevAuth` warning.
3. `schema_versions` holds the slice-relative path key, not the bare filename.
4. Both tables exist with the columns in §1; all timestamps are `TIMESTAMPTZ`.
5. All three indexes exist, including both **partial** ones.
6. `notifications` has **no** foreign key to any table outside this slice.
7. All four roles get `200` from `/api/notifications/unread-count`.
8. A user cannot see, count, or mark another user's notifications — by any endpoint, with any
   parameter. No DTO in the slice has a recipient field.
9. `MarkRead` with another user's id returns `MarkedCount = 0` and leaves that row unread.
10. **A rolled-back caller transaction leaves no notification row** — demonstrated by a test.
11. An emailed kind produces exactly one outbox row; a non-emailed kind produces none.
12. `NotifyAsync` with an empty `RecipientUserId` throws (the accountless guard).
13. `NotifyAsync` never notifies the current caller about their own action.
14. `NotifyManyAsync` collapses duplicate recipient/kind/ticket triples.
15. The drainer marks `Sent` on success, `Abandoned` on permanent failure, and backs off on
    transient failure up to the attempt cap.
16. The drainer survives an exception from `IEmailSender` and keeps processing later rows.
17. The drainer uses its own connection and a per-iteration DI scope — not `RequestConnection`,
    not a captured context.
18. Startup fails with a clear message if no `IRecipientDirectory` is registered.
19. Startup fails if this slice declares an action name another slice already declared.
20. No provider SDK, API key, or email secret has been added anywhere.
21. There is no HTTP endpoint that creates a notification.
22. There is no code path that deletes a row from either table.
23. `dotnet test` passes, with the PostgreSQL test **executed, not skipped**.

---

# Correction Notes — review of 2026-09-01

**This is a plan-only review.** There is no `AccountantApp.Api/Slices/Notifications/` directory —
`Slices/` contains only `Audit`, `Customers` and `TicketTypes`. So nothing below is an
implementation defect. Everything below is a defect in **this plan**, found by checking it against
documents 0–5 and against the three slices already built, and recorded before it gets built rather
than after.

Read this section before §0. Three of the findings (N-1, N-4, N-5) will produce silent data or mail
loss if the plan is transcribed as written.

Checked and **cleared** first, because both looked like contradictions and are not:

- **The hosted service is authorised.** README:144 says *"No purge job, no scheduler."* but
  01-DomainModel §9.2 carves it out: *"the `Notifications` slice does run one `IHostedService` — the
  email outbox drainer — inside the existing `app` container. That is the **only** hosted service in
  the system, it adds no container"*, and 04-Infrastructure §5a locks the model. Lower-numbered
  document wins, the drainer adds no fourth container and deletes nothing. No correction needed.
- **`Notifications` never references `Identity`.** §5.3 rule 1, §8.1–8.2 and the
  `Recipient`/`IRecipientDirectory` contract all comply with 03-SliceInventory §3 rule 7 — the
  reference direction is `Identity → Notifications`, and the types are this slice's own, defined in
  its `ExternalInterfaces/`. No step reads `Employees`, `Tickets` or `Customers`.
- **Read-state scoping is specified unusually well.** §0.3, §7.0 A and *"No DTO in this slice has a
  `RecipientUserId` field"* (§6) close the leak by construction. No version column, no `deleted_at`,
  no soft delete, per README:146–147. All four routes are correctly kebab-cased. §12.2's 27-row
  acceptance table is the strongest in any of the four plans.

## N-1 (BLOCKER) — an over-length `Title` becomes a 500, inside the caller's transaction

§4.2 C, line 438: *"`Title` over 200 characters is a caller bug → `InvalidOperationException`."*

App/GeneralAppArchitecture §8 is LOCKED against this: *"The rule in one line: **if a client can
trigger it by sending a value, it is a `4xx`.**"* An `InvalidOperationException` is not an
`AppException`, so the exception middleware's `catch (Exception)` turns it into a 500.

The title **is** client-triggerable. 01-DomainModel §4 specifies *"Title — derived from the Ticket
Type name plus the Subject"*, and both the Ticket Type display name and the Employee's name are
user-supplied — and note that `TicketTypes` currently accepts a 255-character `DisplayName`, so 200
is reachable without anyone trying. Because §4.2 A puts `NotifyAsync` inside the caller's
transaction, the throw does not merely fail the notification: **every ticket transition by a user
with a long name rolls back with a 500.**

The house answer already exists. The Audit plan's rule B calls letting a client-controlled string
reach PostgreSQL *"the highest-severity trap in this slice"* and truncates every one; this plan
truncates `Body` two lines earlier for exactly that reason.

Correction: truncate `Title` the way `Body` is truncated, or throw `AppException(..., 422)`. Same fix
for the `EmailBody` > 4,000 rejection in §4.2 F2.

## N-2 (BLOCKER) — the `IRecipientDirectory` startup check can never fire, and the failure it exists to prevent silently discards every email

§5.3 rule 3, line 590: *"**An unregistered implementation must fail at startup, not on first
send.** Add a startup check that resolves `IRecipientDirectory` once and throws a clear
`InvalidOperationException` naming the missing registration."*

§5.3 rule 4, line 594, and §9, line 968: *"**Until `Identity` exists, register a stub** in
`NotificationsRegistration.cs` guarded by `TryAddScoped`, returning `null` for every lookup"* /
`services.TryAddScoped<IRecipientDirectory, NullRecipientDirectory>();`

Because the slice registers a fallback unconditionally, `IRecipientDirectory` is **always**
resolvable, so the rule-3 check is dead code and the §12 test row at line 1118 (*"No
`IRecipientDirectory` registered | startup throws"*) cannot be written against the real registration
path. Success criterion 18 will be recorded as met on a check that cannot fail.

Then the failure mode the requirement exists to prevent returns in its worst form: if `Identity`
ever ships without registering the real implementation, the stub answers `null`, the drainer marks
every row `Skipped` with `"No such account"` (line 653), and **every invitation and every
password-reset email is silently discarded — no startup error, no failed row, no retry.**

Correction: pick one mechanism and delete the other. Either (a) drop the stub and let DI fail, or
(b) keep the stub for the pre-`Identity` window, have `NullRecipientDirectory` throw at construction
when `Notifications:Email:Enabled` is true, and replace rule 3's check with an explicit startup
assertion that the resolved implementation is **not** `NullRecipientDirectory`. Then say where that
assertion lives — see gap 1, because none of the obvious places work.

## N-3 (BLOCKER) — "email disabled" cannot produce `Skipped`, because nothing is running

Four lines apart, mutually exclusive:

- Line 655: *"| Email delivery disabled by configuration | `Skipped`. **Not `Pending`** — leaving
  them pending means a flood the moment mail is enabled. |"*
- Line 675, rule 8: *"**It is registered once and only if enabled.** `Notifications:Email:Enabled`
  false means **do not register the service at all**."*

If the drainer is not registered, it cannot mark anything `Skipped`. Meanwhile §4.2 F (line 459)
enqueues an outbox row purely on `NotificationEvents.Emailed.Contains(kind)`, with **no `Enabled`
check at all** — so with email disabled, rows accumulate as `Pending` forever and switching mail on
later produces precisely the flood the first rule forbids.

Correction: move the `Enabled` decision into the **enqueue** path — `NotificationApi` writes the row
with `status = 'Skipped'`, or writes no row, when mail is disabled — and delete the
`Skipped`-on-disabled row from the drainer's table. (Registering the drainer unconditionally and
letting it skip is the other consistent option, but it contradicts rule 8's stated reason.) State
which, and give `Notifications:Email:Enabled` a specified default — see gap 5.

## N-4 (BLOCKER) — the drainer's DbContext has no registration that gives it the "own connection" rule 2 requires

§5.4 rule 2, line 639: *"**It must not use `RequestConnection`.** There is no request… The drainer's
context needs its **own** connection. Register a second options configuration for this purpose, or
resolve the connection string directly."*

Line 954 is the only registration in the plan:
`services.AddDbContext<NotificationsDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<RequestConnection>().Connection));`
— and line 609 has the drainer do
`scope.ServiceProvider.GetRequiredService<NotificationsDbContext>()`, which resolves exactly that.
There is no second registration, and two `AddDbContext` calls for one context type are **not
composable**: the last-registered `DbContextOptions<NotificationsDbContext>` wins for both callers.

The trap description at line 1024 (§11.4.4) also states the wrong symptom: *"there is no request, so
it throws at startup or produces a null connection."* `RequestConnection` as defined in
App/GeneralAppArchitecture §5 takes only `IConfiguration`, so it constructs perfectly well outside a
request. The drainer will not throw — it will quietly open a connection scoped to a lifetime it
created and share it with nothing, which is a much harder bug to see than the one the builder is
told to look for.

Correction: specify the concrete mechanism and show the registration line. Either a separate
`NotificationsDrainerDbContext` type, or `AddDbContextFactory<NotificationsDbContext>` with a
connection-string-based options action used only by the drainer. Then rewrite §11.4.4's symptom.

## N-5 (MAJOR) — an out-of-scope notification id is neither 404 nor audited

Line 823: *"**Ids that are unknown, already read, or belong to someone else are silently skipped**
and simply not counted."* Line 747: *"**No handler here writes an audit entry**."*

02-AuthorizationMatrix §1: *"When the record is outside the caller's scope, respond **`404`, not
`403`**… Every denial writes an Audit Entry"*, restated as §12 rules 3 and 5. This plan's own §7.0 C
repeats *"Out of scope is `404`, never `403`"* — yet **no endpoint in the slice can ever return
404**: there is no single-notification read, and `mark-read` returns 200 with `MarkedCount = 0`.

The enumeration-oracle reasoning in §7.3 rule 2 is sound on its merits — returning 404 for a
foreign id does confirm the id exists. But a plan under `Slices/` may not silently overrule doc 2,
and README:48 is explicit: *"Do not resolve a contradiction by inventing a third behaviour — flag
it."* As written a builder will implement an unaudited scope denial believing §7.0 C is satisfied.

Correction: state the deviation and reconcile it. Either declare that a bulk operation reports
aggregate success and that per-id scope misses are recorded as one audited denial, or raise it as a
gap in 02 §9. Do not leave §7.0 C claiming a behaviour the slice cannot produce.

## N-6 (MAJOR) — the drainer needs `notification.Body` and never loads it

§5.4 rule 10, line 682: *"**Send `entry.EmailBody ?? notification.Body`**… an `EmailMessage` built
from `notification.Body` unconditionally silently emails the redacted text."* This coalesce is the
whole point of the `email_body` design.

But lines 613-618 select from `db.Outbox` only, and line 307 says *"No navigation property from
`OutboxEntry` to `Notification` is required."* A builder following the pseudocode literally has no
`notification` in scope where rule 10 applies. The same omission hides gap 2: `EmailMessage.Subject`
has no specified source, and the obvious source — `notification.Title` — is equally unloaded.

Correction: make the claim query a join or projection carrying the notification's `Body` **and**
`Title`, or mandate the navigation property. Then say that `Subject` is `notification.Title`.

## N-7 (MAJOR) — the duplicate-drainer guard does not guard anything, and contradicts rule 6

- Rule 7, line 670: *"**Guard against two instances.** Use `SELECT … FOR UPDATE SKIP LOCKED` when
  claiming the batch, so a second instance takes different rows."*
- Rule 6, line 665: *"**Do not send inside a transaction that spans the send.** Save the status
  update after the send returns."*

`FOR UPDATE` holds only until the claiming transaction commits. Commit before the send, as rule 6
requires, and the rows are unlocked *during* the send — so a second instance re-claims them and the
recipient gets two emails. A real claim needs the status flipped inside the claim transaction, but
`OutboxStatus` (lines 256-263) defines no `Claimed`/`InProgress` value, and lines 613-618 show a
plain EF `Take` with no locking at all, contradicting rule 7's own *"This is a raw-SQL claim step;
EF's `Take` alone gives no locking."*

Correction: add a claimed state (or a `claimed_until` timestamp) to the §1 schema **and** to
`OutboxStatus`, and show the claim statement. Or drop rule 7, rely on the locked single-replica
topology, and say that explicitly so nobody scales the `app` container believing it is safe.

## N-8 (MAJOR) — the plan both requires and forbids logging the email body

- §5.2, line 559: *"Log the body at `Information` **only** in Development."*
- §5.4 rule 10, line 682: *"Send `entry.EmailBody ?? notification.Body`, and **never log either**."*
  Line 691: *"**It must never reach a log line or `last_error`.**"*

The body in question can be the single-use token link that §1's entire `email_body` design exists to
keep out of retained storage. Development logs are the least protected place in the system.

Correction: delete the §5.2 allowance. Also fix the `LoggingEmailSender` sample at lines 543-548,
which logs unconditionally with no `IHostEnvironment` gate and no constructor for the `_logger` it
uses.

## N-9 (MAJOR) — `Skipped` rows retain the plaintext token forever

Rule 10, line 687: *"**Clear it on `Sent`, and also on `Abandoned`.** … `Failed`/`Pending` rows keep
it — they will be retried and still need it."*

`Skipped` appears in neither list, and `Skipped` rows are never retried. Every row produced by the
pre-`Identity` `NullRecipientDirectory` stub (N-2), every suspended recipient, and every
disabled-email row (N-3) is `Skipped` — so a live token sits in `email_body` in a table that line 196
states is never purged. That is verbatim the outcome line 154 says the design prevents: *"a live
token would sit in a permanently-retained table for its whole validity window, in plaintext."*

Correction: clear `email_body` on `Skipped` too. Add the row to the §12 test table at lines
1124-1126.

## N-10 (MAJOR) — §7.1's `EmailStatus` projection is the unindexed scan §1 says must never happen

Line 192: *"The partial index on `status = 'Pending'` is not an optimisation detail. Without it the
drainer's poll scans every row ever sent, every few seconds, forever."*

Line 769: `EmailStatus = _db.Outbox.Where(o => o.NotificationId == n.Id).Select(o => o.Status).FirstOrDefault()`,
defended at line 774 as *"fine at the maximum page size of 50."*

There is **no index on `notification_outbox(notification_id)`** — §1's index list (lines 177-190)
has three, and the partial `idx_outbox_due` does not serve this predicate. So every page of the
notification centre runs up to 50 sequential scans over a table that grows forever. Success criterion
5 (line 1211) locks in *"all three indexes"*, so a builder will not add a fourth.

Correction: add `CREATE INDEX idx_outbox_notification ON notification_outbox (notification_id);` to
§1 and update criterion 5 to four, or drop `EmailStatus` from the list projection.

## N-11 (MAJOR) — one bad row loses the whole batch's progress and re-sends its emails

Lines 619-628: `foreach entry in due: process(entry)`, then a **single**
`await db.SaveChangesAsync(ct)` after the loop, with `catch (Exception ex)` wrapping the whole
iteration. Rule 6 says *"Save the status update after the send returns."* The §12 row at line 1117
requires *"one bad row in a batch of 20 | the other 19 still processed."*

If anything escapes `process` on row 5, control jumps to the catch before `SaveChangesAsync`, so rows
1–4's `Sent` statuses are discarded — **and those four emails are sent again on the next tick**, and
again, until a batch completes cleanly.

Correction: save per entry, matching rule 6, and wrap `process(entry)` in its own try/catch so the
batch continues.

## N-12 (MINOR) — silent dropping is forbidden in §4.3 and mandated in §4.2 E

- Line 503: *"**Do not "helpfully" skip it.** Silently dropping an empty recipient is how the rule
  gets forgotten."*
- Lines 449-453: *"**Never notify the actor about their own action.** … drop any request whose
  `RecipientUserId` equals the current caller's `Id`. Log at debug when dropping; do not throw."*

No numbered document states a no-self-notify rule, and it has a concrete victim: a self-service
`PasswordResetRequested`, or any self-triggered account action, has the caller **as** the recipient —
so rule E silently discards the only email that matters. Rule E also sits awkwardly against this
plan's own line 878 (*"This slice decides nothing about when a domain event happened — callers tell
it"*) and 03-SliceInventory §1.

Correction: either exempt an explicit list (`Invited`, `PasswordResetRequested` always delivered) or
move the decision to the calling slices, consistent with §8.4.

## N-13 (MINOR) — `recipient_user_id VARCHAR(100)` is justified by a reason that does not hold

Lines 86-88: *"The UserAccount that receives it. VARCHAR, not UUID, and NO foreign key: this slice
may not depend on Identity."*

A `UUID` column with no foreign key creates no dependency whatsoever — **the FK is the dependency,
not the type.** Choosing `VARCHAR` makes `n.RecipientUserId == user.Id` (line 761) an exact string
comparison, so any format drift between what a calling slice writes and what the session claim
yields hides a user's entire notification list, with no error anywhere. See gap 4.

Correction: use `UUID` with no foreign key, keeping the stated independence and getting the
comparison checked by the database. If `VARCHAR` is kept for a reason not given here, give it, and
mandate a canonical format.

## N-14 (MINOR) — §12.1 and success criterion 23 disagree about skipping

Line 1087: *"Skip it **loudly** when no database is reachable."* Line 1232: *"`dotnet test` passes,
with the PostgreSQL test **executed, not skipped**."* Both PostgreSQL tests in the repo currently
skip, and criterion 19 of the Audit plan has the same conflict. Pick "fail, do not skip" and apply it
in all four plans.

## N-15 (MINOR) — §4.2 F2 and §7.2 use 422 where App §8 says 400

Lines 800-801: *"if `req.NotificationIds` is null or empty → 422… Count > 200 → 422"*.
App/GeneralAppArchitecture §8: *"`400` — validation error (client mistake)"*; *"`422` —
unprocessable (semantic error, e.g. "can't create a second Accountant Admin")"*. A missing required
list and an over-cap list are both shape validation → 400. Low impact on its own, but it will be
copied into `Tickets`.

## N-16 (MINOR) — §10 has no per-endpoint role column, unlike every other plan

§10's table is `Route | Verb | Note` (line 926). The Customers plan uses
`Route | Verb | Roles | Note` (lines 785-794) with the matrix roles per route. Here the
route→action-name mapping exists only inside handler pseudocode, so a builder wiring endpoints
cannot check §11.2 against 02-AuthorizationMatrix §9 without reverse-engineering it.

The mapping is in fact correct — all four routes resolve to two all-four-role actions, matching 02 §9's
*"List own notifications"* / *"Mark own notification read"* / *"Read another actor's notifications —
Nobody, including Accountant Admins"*. Correction: add the Roles column so that is checkable.

## N-17 (MINOR) — three success criteria are not assertable as written

- Line 1214: *"by any endpoint, with any parameter"* — a universal claim. The paired line 1216 is
  the real, assertable test.
- Line 1225: *"The drainer uses its own connection"* — unverifiable until N-4 is resolved.
- Line 1227: *"Startup fails … if no `IRecipientDirectory` is registered"* — unreachable per N-2, so
  it will be recorded as met without ever having been exercised.

Correction: replace each with a criterion that can fail.

---

## Gaps a builder must guess — ranked by blast radius

1. **Where the `IRecipientDirectory` startup check lives, and how it detects absence** (N-2).
   Every obvious location is closed: `Program.cs` is capped at *"Two lines, naming no handler or
   DbContext type"* (line 1014, README:68); `AddNotificationsSlice` returns `IServiceCollection`
   before `Build()`, so it can resolve nothing; the service is **scoped** and nothing injects it at
   construction time (the drainer uses `GetRequiredService` inside a loop), so `ValidateOnBuild` /
   `ValidateScopes` will not catch it either. The plan states the requirement and names no mechanism.
   A wrong guess is the silent total mail loss in N-2.
2. **`EmailMessage.Subject` has no specified source.** Line 520 defines
   `EmailMessage(string To, string Subject, string Body)`; nothing in §1, §4 or §5.4 says the subject
   is `notification.Title`, there is no `subject` column, and the drainer never loads the title
   (N-6). Separately, rule 9 configures `FromAddress` and `FromName`, but `EmailMessage` has no
   `From` field and `LoggingEmailSender` never reads configuration — the seam as defined cannot carry
   them.
3. **Who generates `id` and `created_at`.** The DDL uses `DEFAULT gen_random_uuid()` and
   `DEFAULT NOW()` (lines 84, 104, 116, 131, 132); the entities are plain `Guid Id` /
   `DateTimeOffset CreatedAt` (lines 219, 227); §2.5 requires only `HasColumnName` and
   `HasMaxLength` — **no `ValueGeneratedOnAdd()`** — and `NotificationApi`'s body is never shown. EF
   will therefore insert `Guid.Empty` (the second insert violates the primary key) and `0001-01-01`
   timestamps, silently breaking the `created_at DESC` ordering, the unread list, and §12.1 case 4.
   The Audit plan avoids half of this by shipping `OccurredAt = DateTimeOffset.UtcNow` in real code;
   this plan ships no equivalent. Also state whether this slice uses `DateTimeOffset.UtcNow` inline
   (lines 810, 847) or the `SystemClock` that App §4 offers and 03-SliceInventory §4 lists as
   cross-cutting.
4. **No canonical form for `RecipientUserId`** (N-13). `Identity`'s directory would return
   `u.Id.ToString()`; `CurrentUser.Id` comes from a claim; calling slices supply the value on write.
   Nothing mandates a format or a normalising comparison, and a mismatch is a total, silent read
   failure for the affected user.
5. **`Notifications:Email:Enabled` has no specified default and no specified `appsettings`
   content.** The §13 checklist says only *"Modify — `Notifications:Email` section, no secret"* (line
   1198). Combined with N-3, the difference between `true` and `false` is the difference between
   "mail drains" and "mail accumulates forever".
6. **`NotifyManyAsync` semantics** (line 422). What the returned `int` counts (requests accepted,
   rows created, rows surviving the self-drop); whether one invalid request rejects the whole batch or
   only itself, inside the caller's transaction; whether there is a batch-size cap; whether
   duplicate-collapse runs before or after the self-drop. Success criterion 14 asserts the collapse
   without pinning the count.
7. **What lazy `CurrentUser` resolution does when there is no principal** (lines 455-457). The plan
   says resolve lazily because *"`INotificationApi` may be called from a path where no principal
   exists"*, but not what happens then — `GetRequiredService<CurrentUser>()` as registered in App §7
   throws. Does rule E swallow it and deliver? The anonymous forgot-password path depends on the
   answer, and a 401 escaping `NotifyAsync` would abort an invitation.
8. **`PageSize <= 0`.** §0.4 (line 69) covers only "5,000 clamps to 50" and "PageNumber below 1
   clamps to 1". Absent or zero `PageSize` is the common case, and the other plans disagree: Audit
   line 1115 says *"clamped to 1"*, TicketTypes line 886 says
   `clamp(req.PageSize <= 0 ? 15 : req.PageSize, 1, 50)`. Uncapped is not the risk here — a silent
   page size of 1 is. Settle it in `Shared/Pagination`, which §0.1's prerequisites table also omits.
9. **`resolved_email` is not length-validated.** Line 146 rightly insists `last_error` be
   *"truncate[d] in code before insert"* because `22001` *"would roll back the drainer's progress"* —
   then stores an externally-supplied address into `VARCHAR(320)` (line 123) with no equivalent
   guard.
10. **RESOLVED 2026-09-02 — `Invited` vs `EmployeeInvited`.** Two catalogue kinds for one act,
    **both** in `Emailed`, with no statement of which slice raises which. 03-SliceInventory §1
    gives `Employees` *"requesting that Identity create an account for one"*, so both slices could
    plausibly notify and the invitee would get two emails.

    The answer, from [the Employees plan](../Employees/IMPLEMENTATION_PLAN.md) §13 item 9 and
    [the Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §9.1 rule 12: **`Identity` raises it,
    exactly once, from `InviteEmployeeAccountAsync`, addressed to the Employee being invited.**
    `Invited` is for an Accountant joining the Office; `EmployeeInvited` is for an Employee joining
    their employer's portal. `Employees` raises **no** notification when inviting — it audits
    `EmployeeInvited` as an *audit action*, which is a different catalogue with a colliding name.
    That collision is the trap here: seeing the string in the Employees handler does not mean a
    notification was sent from there.
11. **`NotificationEvents.All` is a comment.** Line 358:
    `public static readonly IReadOnlySet<string> All = /* reflection over the constants above */;`
    The one line gating every `NotifyAsync` call is left unwritten. Tests 2 and 3 catch a total
    failure but not a partial filter.
12. **The event catalogue's relationship to `IActionCatalogue` is never stated.** One central static
    class owned by `Notifications` is correct per 01-DomainModel §7 (*"a fixed catalogue defined in
    the Notifications slice spec"*) — but it is the **opposite** of the per-slice-fragment pattern App
    §4 mandates for actions and `Audit` established. A builder arriving from `Audit` will plausibly
    invent `INotificationEventCatalogue` fragments, and unlike the action catalogue nothing here has
    a duplicate-name startup check, so a shadowed kind would resolve by registration order. Even
    within the central class, two constants sharing a *value* collapse silently into `All`. Say that
    centrality is deliberate, why, and that no fragment interface is to be added.
13. **`AccountSuspended` is structurally undeliverable.** It is in the catalogue (line 337) but not in
    `Emailed`, and a suspended account cannot authenticate (01-DomainModel §2), so the in-app
    notification can never be read; if it were emailed, line 654 would `Skip` it for
    `IsActive == false`. Either drop the constant or state that it is intentionally write-only.
14. **`mark-all-read` has no row in 02-AuthorizationMatrix §9.** It is a reasonable reading of "Mark
    own notification read", but it is an inference. Say so, or raise it.
15. **`NotificationDto.EmailStatus` for a non-emailed kind.** `FirstOrDefault()` on a string yields
    `null` — is `null` the contract, or `"None"`? The SPA renders it.

---

## Deviations from the conventions the three built slices established

1. **No Roles column on the endpoint table** (N-16). Customers plan lines 785-794 has one.
2. **The EF configurations are prose-only.** The Audit plan (lines 334-346) ships the complete
   `IEntityTypeConfiguration` body — every `HasColumnName` / `HasMaxLength` / `IsRequired` line — and
   asserts *"Map every single property with `HasColumnName`. There are no exceptions, not even for
   `id`."* This plan describes both configurations in two sentences and ships no code. For a
   Haiku-class builder that is the difference between transcription and invention, and it is exactly
   where gap 3 (`ValueGeneratedOnAdd`) and the `HasMaxLength`-versus-`VARCHAR` agreement get decided.
   **Write both configurations out in full.**
3. **Over-length client-derived strings are thrown on, not truncated** (N-1), against the Audit plan's
   rule B.
4. **The `PageSize <= 0` clamp is omitted** (gap 8), although Audit line 1115 and TicketTypes line 886
   both state it.
5. **No-request handlers are not given a signature.** `GetUnreadCountHandler` and
   `MarkAllNotificationsReadHandler` are documented as *"Request: none"* (lines 781, 837) against the
   canonical `Handle(TRequest req, CurrentUser user, CancellationToken ct)` at line 729. The Audit
   plan resolves this concretely by showing `handler.Handle(user, ct)`; this plan leaves the
   two-argument form to be inferred.
