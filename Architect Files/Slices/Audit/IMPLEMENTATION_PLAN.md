# Audit Slice — Implementation Plan

Build this slice **first**, before every other slice. Seven slices call it, `IPermissionChecker`
calls it on every denial, and the transaction rule that makes it trustworthy changes how every
other slice registers its `DbContext`. Building it late means retrofitting all eight.

Read these first, in order. This plan is subordinate to all of them — where it disagrees with a
numbered document, the numbered document wins and this plan is wrong:

- [00-Glossary.md](../../00-Glossary.md)
- [01-DomainModel.md](../../01-DomainModel.md) — §8 defines AuditEntry and the minimum audited
  action set; §9.2 says nothing is ever deleted
- [02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) — §1 (every denial is audited),
  and the audit-log section (reading it is `AccountantAdmin` only)
- [03-SliceInventory.md](../../03-SliceInventory.md) — `Audit` depends on **nothing**
- [App/GeneralAppArchitecture.md](../../App/GeneralAppArchitecture.md) — §4 (action catalogue,
  scope filter), §5 (**the audit write shares the mutating slice's transaction**), §6, §7, §8

---

## 0. Prerequisites — read before writing any code

### 0.0 What already exists, and what is wrong with it

This slice is **not** greenfield. Five files exist from an earlier pass:

| File | State |
|---|---|
| `Slices/Audit/AuditRegistration.cs` | Exists. Registers the context and `IAuditApi`. Must change — connection overload, action catalogue. |
| `Slices/Audit/ExternalInterfaces/IAuditApi.cs` | Exists. The `AuditEntry` record has **5 fields**; `01-DomainModel.md` §8 requires **11**. Must be expanded. |
| `Slices/Audit/ExternalInterfaces/AuditApi.cs` | Exists. Writes immediately on its own connection. Must enlist in the caller's transaction. |
| `Slices/Audit/Infrastructure/AuditDbContext.cs` | Exists. Entity has 6 columns; needs 11. `OccurredAt` is a `DateTime` mapped to `TIMESTAMPTZ` — wrong, see 0.3. |
| `Slices/Audit/Infrastructure/Migrations/20260828_001_CreateAuditSchema.sql` | Exists. Creates a 6-column table. |

**Replace `20260828_001_CreateAuditSchema.sql` in place — do not add a `002` that alters it.**

That is normally forbidden. A migration script that has been applied to any database is
immutable, because the runner records it in `schema_versions` by slice-relative path and will
never re-run it, so an edited script leaves that database permanently out of step with the
repository. The exception applies here for one verifiable reason: **this script has never been
applied to any database.** No PostgreSQL instance has ever existed for this project. Confirm it
yourself before editing — if `schema_versions` exists anywhere and contains
`Audit/Infrastructure/Migrations/20260828_001_CreateAuditSchema.sql`, stop, and add
`20260830_002_ExpandAuditEntries.sql` instead.

There is no data to migrate either way. The table has never held a row.

### 0.1 How `CurrentUser` reaches a handler — and the field that is missing

Every handler takes `CurrentUser` as its second parameter. It is resolved once per request from
the authenticated principal and registered as a scoped service; see
[App/GeneralAppArchitecture.md](../../App/GeneralAppArchitecture.md) §7.

**`CurrentUser` must gain `CustomerId` before this slice is built:**

```csharp
public record CurrentUser(string Id, UserRole Role, Guid? CustomerId);
```

`CurrentUserFactory` currently reads the `NameIdentifier` and `Role` claims and **discards the
`customer_id` claim that `DevAuthHandler` already emits.** Fix it: read `customer_id`, and throw
`AppException(401)` when a `CustomerAdmin` or `Employee` principal has no `customer_id`. A
Customer-scoped role with a null scope is a broken principal, not a caller with wide access.

`Audit` needs it because `01-DomainModel.md` §8 requires *"Customer in scope, when applicable"*
on every entry, and the only place that value can come from is the caller.

### 0.2 The permission checker, and this slice's catalogue fragment

`IPermissionChecker` is fail-closed and asynchronous:

```csharp
public interface IPermissionChecker
{
    Task RequireAsync(CurrentUser user, string action, object? scope = null,
                      CancellationToken ct = default);
}
```

Three rules, restated because they are the ones a builder breaks:

1. **An unknown action name denies.** Never write a default branch that allows.
2. **Every denial is audited** before the exception is thrown — which is why the checker depends
   on this slice, and why this slice depends on nothing.
3. **It is `async` and callers `await` it.** A synchronous signature forces
   `.GetAwaiter().GetResult()` on a request thread, and if the audit write throws, the
   `NpgsqlException` replaces the `AppException(403)` — so during an audit outage a denied caller
   gets a `500` **and the denial is never recorded**.

`PermissionChecker` currently holds two hard-coded `HashSet<string>` fields. **Delete them.** The
catalogue is contributed per slice and composed at startup
([App/GeneralAppArchitecture.md](../../App/GeneralAppArchitecture.md) §4): each slice registers
an `IActionCatalogue`, duplicate action names are a startup failure, an absent action denies, and
an empty role array is a startup failure.

This slice's fragment is in §4.0 below.

### 0.3 Timestamps are `DateTimeOffset`, never `DateTime`

The existing entity has `public DateTime OccurredAt` mapped to a `TIMESTAMPTZ` column. That is a
defect, and it is the kind that produces wrong data rather than an error.

`TIMESTAMPTZ` stores an instant. Npgsql maps it to `DateTimeOffset`. Reading it into a `DateTime`
gives you a `DateTime` whose `Kind` depends on provider configuration, and writing a
`DateTime.UtcNow` with `Kind = Utc` happens to work — until someone passes a `DateTime.Now`,
which Npgsql then rejects or silently reinterprets depending on version. An audit log with
timestamps shifted by the server's UTC offset is worse than no audit log, because it looks fine.

**Every timestamp in this slice is `DateTimeOffset`, and every column is `TIMESTAMPTZ`.** Never
`TIMESTAMP` without the zone.

### 0.4 Pagination

Use the shared shapes in `Shared/Pagination/`. `PaginatedResponse<T>` already exists with
`PageNumber`, `PageSize`, `TotalCount`, `TotalPages`, `Items`.

For this slice specifically: **the audit log is the largest table in the system** and grows
forever (nothing is deleted, §9.2). An unpaginated `SELECT *` will eventually time out or
exhaust memory. Rules:

- Default page size **15**, maximum **50** — the system-wide numbers from
  `App/GeneralAppArchitecture.md` §8. This slice does **not** get a larger page because its table
  is larger; the opposite is true. A request for more is clamped, not rejected.
- `PageNumber` below 1 is clamped to 1.
- The default sort is `occurred_at DESC, id DESC`. The `id` tiebreaker is not decoration:
  `occurred_at` alone is not unique — a single transaction can write several entries with
  identical timestamps — and an unstable sort makes paging skip and repeat rows.

---

## 1. Database schema (SQL migration)

**File:** `Slices/Audit/Infrastructure/Migrations/20260828_001_CreateAuditSchema.sql`
(replacing the existing content — see 0.0)

### Table: audit_entries

```sql
CREATE TABLE audit_entries (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Acting UserAccount, and the role it held at the time. 01-DomainModel §8.
    actor_user_id   VARCHAR(100) NOT NULL,
    actor_role      VARCHAR(30)  NOT NULL,

    -- Customer in scope, when applicable. NULL for Accountant actions that are not
    -- about one Customer, and for authentication attempts by an unknown user.
    customer_id     UUID NULL,

    -- Action code from the fixed catalogue in ExternalInterfaces/AuditActions.cs.
    action          VARCHAR(100) NOT NULL,

    -- Target entity kind and identifier. Kind is a plain string, not a foreign key:
    -- this table must not reference other slices' tables. See section 8.
    target_kind     VARCHAR(50)  NOT NULL,
    target_id       VARCHAR(100) NOT NULL DEFAULT '',

    -- Outcome. Required because 01-DomainModel §8 mandates auditing "authentication
    -- attempts and outcomes" and "every permission-denied response" — an attempt with no
    -- recorded outcome does not satisfy that.
    -- CHECK, not a bare VARCHAR: the §6.1 search filters on this column and rejects an
    -- unrecognised value with a 422, so a row stored as 'success' is invisible to the only
    -- query written to find it — and this table has no UPDATE path to fix it later.
    outcome         VARCHAR(20)  NOT NULL
        CONSTRAINT ck_audit_entries_outcome CHECK (outcome IN ('Success', 'Denied', 'Failure')),

    -- Before and after values for changes to existing data. JSONB, not TEXT: the audit
    -- reader has to filter and display these, and JSONB gives PostgreSQL-side containment
    -- queries without parsing in the application. NULL for creates (no before) and for
    -- reads (no change).
    before_value    JSONB NULL,
    after_value     JSONB NULL,

    occurred_at     TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    source_ip       VARCHAR(45)  NOT NULL DEFAULT '',
    user_agent      VARCHAR(512) NOT NULL DEFAULT ''
);
```

Column notes a builder must not improvise on:

| Column | Note |
|---|---|
| `actor_user_id` | `VARCHAR(100)`, not `UUID`. `CurrentUser.Id` is a `string`, and authentication failures must be recordable against an identifier that may not correspond to any UserAccount row — a login attempt for an address that does not exist still has to be audited. |
| `actor_role` | The role **at the time of the action**, stored as text. Not a foreign key and never resolved live. The whole reason §8 asks for it is that a later promotion or demotion must not rewrite history. |
| `customer_id` | `UUID NULL`, no foreign key. See section 8. |
| `source_ip` | `VARCHAR(45)`, not `INET`. 45 characters is the longest IPv6 form including an IPv4-mapped suffix. `INET` is tempting but forces every reader and test to handle a Npgsql-specific type for a value that is only ever displayed. |
| `outcome` | `'Success'`, `'Denied'`, or `'Failure'`. Text, not an enum type — a new outcome must not require a DDL change. |
| `before_value` / `after_value` | JSONB. **Never store a raw entity here.** See §4.0 rule F on redaction. |

### Indexes

```sql
-- The audit reader's default view: most recent first. Matches the mandatory sort in 0.4.
CREATE INDEX idx_audit_entries_occurred_at ON audit_entries (occurred_at DESC, id DESC);

-- "What did this user do?" — the most common investigation.
CREATE INDEX idx_audit_entries_actor ON audit_entries (actor_user_id, occurred_at DESC);

-- "What happened to this record?" Composite, kind first: a target_id is only meaningful
-- alongside its kind, because ids are not unique across kinds.
CREATE INDEX idx_audit_entries_target ON audit_entries (target_kind, target_id, occurred_at DESC);

-- "Everything concerning this Customer." Partial, because most Accountant-level entries
-- have no Customer and would bloat the index for no benefit.
CREATE INDEX idx_audit_entries_customer ON audit_entries (customer_id, occurred_at DESC)
    WHERE customer_id IS NOT NULL;

-- Denials and auth failures are queried as a group during an incident.
CREATE INDEX idx_audit_entries_action ON audit_entries (action, occurred_at DESC);
```

**Do not add a unique constraint to anything in this table.** Two identical actions at the same
instant are legitimate — a retry, a double-click, two Accountants doing the same thing — and
the audit log's job is to record both, not to deduplicate them.

### There are no `UPDATE` or `DELETE` paths

`01-DomainModel.md` §8: *immutable, append-only, never updated, never deleted.*
`02-AuthorizationMatrix.md`: *Edit or delete an audit entry — **Nobody.** No API exists for
this.*

So: no `updated_at`, no `deleted_at`, no soft-delete flag, no version column, no archive table,
no retention purge. If you are tempted by a purge job because the table grows forever, re-read
§9.2 — there is no scheduler in the production topology and none is to be added.

Consider adding, as documentation rather than enforcement, a comment in the script:

```sql
COMMENT ON TABLE audit_entries IS
    'Append-only. No UPDATE or DELETE path exists in the application. See 01-DomainModel.md section 8.';
```

---

## 2. EF Core entities and DbContext

### 2.0 Column naming — mandatory

The SQL above creates `snake_case` columns. EF Core's default convention maps
`AuditRecord.ActorUserId` to a column named `ActorUserId`, which **does not exist**, and every
query fails with `column a.ActorUserId does not exist`.

**Map every single property with `HasColumnName`.** There are no exceptions, not even for `id`.
The in-memory provider ignores column names entirely, so a fully green in-memory test suite is
not evidence that any of this works. That is why §9.1 exists.

### 2.1 Naming: three types, deliberately different names

This is the one place in the slice where a builder reliably gets confused. Three distinct types
carry audit data, and they are not interchangeable:

| Type | Location | Purpose |
|---|---|---|
| `AuditEntry` | `ExternalInterfaces/IAuditApi.cs` | The **input contract**. What another slice passes to `LogAsync`. A `record`. Has no `Id`. |
| `AuditRecord` | `Core/AuditRecord.cs` | The **EF entity**. Maps to `audit_entries`. Never leaves this slice. |
| `AuditEntryDto` | `Application/Dtos/` | The **read model** returned by the query handlers, with redaction applied. |

Do not merge them. Returning `AuditRecord` from a handler leaks an EF entity across the slice
boundary; accepting `AuditRecord` in `LogAsync` forces every caller to reference this slice's
`Core`, which dependency rule 2 forbids outright.

### 2.2 Entity: `Core/AuditRecord.cs`

```csharp
namespace AccountantApp.Api.Slices.Audit.Core;

/// <summary>
/// One immutable audit entry. Rows are inserted and never updated or deleted.
/// Setters exist only so EF can materialise instances.
/// </summary>
public sealed class AuditRecord
{
    public Guid Id { get; set; }

    public string ActorUserId { get; set; } = string.Empty;
    public string ActorRole { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }

    public string Action { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;

    public string? BeforeValue { get; set; }
    public string? AfterValue { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public string SourceIp { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
}
```

`BeforeValue` and `AfterValue` are `string?` holding JSON text, mapped to `jsonb`. Do **not**
model them as `JsonDocument` or `object`: `JsonDocument` is `IDisposable` and EF change tracking
over a disposable is a leak, and `object` gives the mapper nothing to work with.

`AuditRecord` deliberately does **not** implement `ICustomerScoped`. It has a `CustomerId`, but
the audit log is not Customer-scoped data — only `AccountantAdmin` may read it at all, and an
Admin sees every Customer. Marking it `ICustomerScoped` would invite a
`WhereInCustomerScope` call that is a no-op for the only role that can get there, which reads
like protection while providing none.

### 2.3 DbContext: `Infrastructure/AuditDbContext.cs`

```csharp
public sealed class AuditDbContext : DbContext
{
    // Required. Without this constructor the context cannot be configured with a provider.
    public AuditDbContext(DbContextOptions<AuditDbContext> options) : base(options) { }

    public DbSet<AuditRecord> AuditEntries => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new AuditRecordConfiguration());
    }
}
```

Move the mapping out of `OnModelCreating` and into a configuration class (2.4). The existing file
inlines it, which works but diverges from every other slice.

`AuditDbContext` contains **one** entity and will never contain more. Nothing else belongs to
this slice.

### 2.4 Configuration: `Infrastructure/Configurations/AuditRecordConfiguration.cs`

```csharp
public sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> b)
    {
        b.ToTable("audit_entries");
        b.HasKey(e => e.Id);

        b.Property(e => e.Id).HasColumnName("id");
        b.Property(e => e.ActorUserId).HasColumnName("actor_user_id").HasMaxLength(100).IsRequired();
        b.Property(e => e.ActorRole).HasColumnName("actor_role").HasMaxLength(30).IsRequired();
        b.Property(e => e.CustomerId).HasColumnName("customer_id");
        b.Property(e => e.Action).HasColumnName("action").HasMaxLength(100).IsRequired();
        b.Property(e => e.TargetKind).HasColumnName("target_kind").HasMaxLength(50).IsRequired();
        b.Property(e => e.TargetId).HasColumnName("target_id").HasMaxLength(100).IsRequired();
        b.Property(e => e.Outcome).HasColumnName("outcome").HasMaxLength(20).IsRequired();
        b.Property(e => e.BeforeValue).HasColumnName("before_value").HasColumnType("jsonb");
        b.Property(e => e.AfterValue).HasColumnName("after_value").HasColumnType("jsonb");
        b.Property(e => e.OccurredAt).HasColumnName("occurred_at");
        b.Property(e => e.SourceIp).HasColumnName("source_ip").HasMaxLength(45).IsRequired();
        b.Property(e => e.UserAgent).HasColumnName("user_agent").HasMaxLength(512).IsRequired();
    }
}
```

`HasColumnType("jsonb")` is required. Without it EF maps a `string` to `text`, the DDL says
`jsonb`, and the insert fails with a type mismatch — or worse, succeeds against a column EF
believes is text and then breaks on any JSON operator.

---

## 3. The action code catalogue

**File:** `Slices/Audit/ExternalInterfaces/AuditActions.cs`

`01-DomainModel.md` §8 requires action codes from a **fixed catalogue**. The catalogue lives in
`ExternalInterfaces/`, not `Core/`, for a specific reason: every slice needs to name the actions
it audits, and dependency rule 2 forbids a slice from referencing another slice's `Core`. Putting
these constants in `Core` would make the rule unfollowable.

```csharp
namespace AccountantApp.Api.Slices.Audit.ExternalInterfaces;

/// <summary>
/// The fixed catalogue of audit action codes (01-DomainModel.md section 8).
/// Every value passed as AuditEntry.Action must be one of these constants.
/// Add a constant here rather than passing a literal from a slice.
/// </summary>
public static class AuditActions
{
    // --- Authentication (Identity) ---
    public const string LoginSucceeded          = "LoginSucceeded";
    public const string LoginFailed             = "LoginFailed";
    public const string LoggedOut               = "LoggedOut";
    public const string AccountLockedOut        = "AccountLockedOut";
    public const string PasswordResetRequested  = "PasswordResetRequested";
    public const string PasswordResetCompleted  = "PasswordResetCompleted";
    public const string PasswordChanged         = "PasswordChanged";

    // --- UserAccount lifecycle (Identity) ---
    public const string AccountInvited          = "AccountInvited";
    public const string InvitationAccepted      = "InvitationAccepted";
    public const string AccountSuspended        = "AccountSuspended";
    public const string AccountReactivated      = "AccountReactivated";

    // --- Accountant role changes (Identity). The highest-value entries in the log. ---
    public const string AccountantAccountCreated = "AccountantAccountCreated";
    public const string AccountantPromoted       = "AccountantPromoted";
    public const string AccountantDemoted        = "AccountantDemoted";

    // --- Customers ---
    public const string CustomerCreated         = "CustomerCreated";
    public const string CustomerUpdated         = "CustomerUpdated";
    public const string CustomerSuspended        = "CustomerSuspended";
    public const string CustomerReactivated      = "CustomerReactivated";

    // --- Employees ---
    public const string EmployeeRegistered      = "EmployeeRegistered";
    public const string EmployeeEdited          = "EmployeeEdited";
    public const string EmployeeDeparted        = "EmployeeDeparted";
    public const string EmployeeInvited         = "EmployeeInvited";

    // --- TicketTypes ---
    public const string TicketTypeCreated       = "TicketTypeCreated";
    public const string TicketTypeVersionCreated = "TicketTypeVersionCreated";
    public const string TicketTypeActivated     = "TicketTypeActivated";
    public const string TicketTypeDeactivated   = "TicketTypeDeactivated";

    // --- Tickets ---
    public const string TicketCreated           = "TicketCreated";
    public const string TicketStatusChanged     = "TicketStatusChanged";
    public const string TicketAssigned          = "TicketAssigned";
    public const string TicketReassigned        = "TicketReassigned";
    public const string TicketCancelled         = "TicketCancelled";
    public const string TicketClosed            = "TicketClosed";
    public const string RevisionSubmitted       = "RevisionSubmitted";
    public const string FieldVerified           = "FieldVerified";
    public const string FieldRejected           = "FieldRejected";
    public const string MessagePosted           = "MessagePosted";
    public const string PriorityChanged         = "PriorityChanged";
    public const string DueDateChanged          = "DueDateChanged";

    // --- Documents ---
    public const string DocumentUploaded        = "DocumentUploaded";
    public const string DocumentDownloaded      = "DocumentDownloaded";
    public const string DocumentSoftDeleted     = "DocumentSoftDeleted";

    // --- Cross-cutting ---
    public const string PermissionDenied        = "PermissionDenied";

    /// <summary>Every constant above. Used to reject an unknown code — see section 5.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        /* every constant declared above, listed explicitly */
    };
}
```

Rules:

1. **`All` is populated by reflection over the public constants**, not hand-copied. A
   hand-maintained duplicate list will drift, and the drift is silent: a code missing from `All`
   makes a legitimate audit call throw. Use
   `typeof(AuditActions).GetFields(BindingFlags.Public | BindingFlags.Static)` filtered to
   `IsLiteral && FieldType == typeof(string)`. This is the one place reflection is warranted —
   it is a one-time static initialiser, not per-request work, and it is not assembly scanning.
2. **A test asserts `All` is non-empty and contains a known constant.** A reflection filter that
   silently matches nothing turns rule 1 into "accept anything".
3. **`TargetKind` values are also fixed**, and belong in the same file as a nested
   `AuditTargets` class: `UserAccount`, `Customer`, `Employee`, `TicketType`, `Ticket`,
   `Document`, `Notification`, `None`.
4. **Do not add an action code speculatively.** Each of the above corresponds to an operation
   named in `01-DomainModel.md` §8 or in a slice plan. An unused code is harmless; a *pattern* of
   speculative codes makes the catalogue stop meaning anything.

### The minimum audited set is a requirement, not a suggestion

`01-DomainModel.md` §8 lists what must be audited. Restated as a checklist the other seven
plans are held to:

| Must be audited | Slice |
|---|---|
| Authentication attempts **and outcomes** | `Identity` |
| Employee registration and invitation | `Employees` |
| UserAccount status changes | `Identity` |
| **Accountant account creation and every promotion or demotion** | `Identity` |
| Ticket creation | `Tickets` |
| **Every** Ticket status transition | `Tickets` |
| **Every** assignment and reassignment | `Tickets` |
| Every field verification | `Tickets` |
| Every Document upload **and download** | `Documents` |
| Every Ticket Type version change | `TicketTypes` |
| **Every** permission-denied response | `Shared/Authorization` |

Accountant role changes matter most: promotion to `AccountantAdmin` grants the power to create
further Accountant accounts, so an unaudited promotion is an unauditable privilege-escalation
path.

---

## 4. DTOs

**Folder:** `Slices/Audit/Application/Dtos/`

### 4.1 `AuditEntryDto` — the read model

```csharp
public sealed class AuditEntryDto
{
    public Guid Id { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ActorRole { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string SourceIp { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
}
```

Note what is **absent**: `BeforeValue` and `AfterValue`. The list view does not carry them. They
are potentially large, they are the only place personal data appears in this table, and a list
endpoint that returns them makes every audit page a bulk export of tax and payroll values. They
appear only in the detail DTO.

### 4.2 `AuditEntryDetailDto`

`AuditEntryDto` plus:

```csharp
    public string? BeforeValue { get; set; }   // JSON text, redacted at write time
    public string? AfterValue { get; set; }
```

### 4.3 `SearchAuditLogRequestDto`

```csharp
public sealed class SearchAuditLogRequestDto
{
    public string? ActorUserId { get; set; }
    public string? Action { get; set; }
    public string? TargetKind { get; set; }
    public string? TargetId { get; set; }
    public Guid? CustomerId { get; set; }
    public string? Outcome { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 15;
}
```

Every filter is optional and they combine with `AND`. All null means "the whole log, most recent
page first".

### 4.4 `GetAuditEntryRequestDto`

One property: `public Guid AuditEntryId { get; set; }`.

### 4.5 Dto rules

- Suffix every DTO `...Dto`. Request DTOs are `...RequestDto`.
- **No DTO in this slice has a setter that a client can use to change stored data**, because
  nothing here is updatable. The request DTOs are query parameters only.
- DTOs are plain classes with public getters and setters, so the JSON serialiser can bind them.
  Do not use `record` with positional parameters for request DTOs — minimal-API model binding
  from a query string does not populate them.

---

## 5. ExternalInterface — `IAuditApi`

**Files:** `Slices/Audit/ExternalInterfaces/IAuditApi.cs`, `AuditApi.cs`

This is the only surface seven other slices touch. Getting it wrong is expensive to fix later.

### 5.1 The contract

```csharp
namespace AccountantApp.Api.Slices.Audit.ExternalInterfaces;

/// <summary>
/// One audit entry to be appended. Constructed by the calling slice.
/// Actor, role, IP and user agent are NOT set by the caller — AuditApi fills them from the
/// current request. A caller that could set its own actor could forge an entry.
/// </summary>
public sealed record AuditEntry(
    string Action,
    string TargetKind,
    string TargetId,
    Guid? CustomerId = null,
    string Outcome = AuditOutcome.Success,
    object? Before = null,
    object? After = null);

public static class AuditOutcome
{
    public const string Success = "Success";
    public const string Denied  = "Denied";
    public const string Failure = "Failure";

    // Kept in step with the CHECK on audit_entries.outcome. AppendAsync validates against this
    // the way it validates Action against AuditActions.All — see §5.2.
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Success, Denied, Failure };
}

public interface IAuditApi
{
    Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an entry for an actor who is not the current caller, or for no caller at all —
    /// a failed login, where there is no authenticated principal. Identity only.
    /// </summary>
    Task LogUnauthenticatedAsync(string actorIdentifier, AuditEntry entry,
                                 CancellationToken cancellationToken = default);
}
```

**The caller does not supply the actor.** This is the most important design point in the slice.
The existing `AuditEntry` has an `Actor` property, which means any slice can write an entry
attributed to anybody. `AuditApi` resolves the actor from the scoped `CurrentUser` and the IP and
user agent from `IHttpContextAccessor`. Remove `Actor` and `OccurredAt` from the contract — a
caller-supplied timestamp is a caller-supplied lie.

`LogUnauthenticatedAsync` exists because `01-DomainModel.md` §8 requires auditing failed
authentication attempts, and at that moment there is no `CurrentUser` to resolve — resolving one
throws `AppException(401)`. Without this method the single most security-relevant entry in the
catalogue cannot be written. It takes the attempted identifier (typically an email address) as
`actor_user_id` and records `actor_role` as `'Unknown'`.

**Neither member gets a default interface implementation.** Not `=> throw new
NotSupportedException()`, not `=> Task.CompletedTask`. It is tempting, because only `Identity`
calls `LogUnauthenticatedAsync` and a default body means the test doubles in every other slice do
not have to implement it — which is exactly the harm. A default body converts "this
implementation is incomplete" from a compile error the builder sees immediately into a runtime
failure inside an audit write, and §5.2.C makes an audit write failure roll back the operation
being audited. The compiler is the cheapest place to find a missing member; a test double that
needs the method can delegate to `LogAsync` in one line.

### 5.2 The implementation

```csharp
public sealed class AuditApi : IAuditApi
{
    private readonly AuditDbContext _db;
    private readonly IRequestTransaction _transaction;
    private readonly IHttpContextAccessor _http;
    private readonly IServiceProvider _services;   // CurrentUser resolved lazily — see below
    private readonly ILogger<AuditApi> _logger;

    public async Task LogAsync(AuditEntry entry, CancellationToken ct)
    {
        var user = _services.GetRequiredService<CurrentUser>();
        await AppendAsync(user.Id, user.Role.ToString(), entry, ct);
    }

    public Task LogUnauthenticatedAsync(string actorIdentifier, AuditEntry entry,
                                        CancellationToken ct) =>
        AppendAsync(actorIdentifier, "Unknown", entry, ct);

    private async Task AppendAsync(string actorId, string actorRole, AuditEntry entry,
                                   CancellationToken ct)
    {
        if (!AuditActions.All.Contains(entry.Action))
            throw new InvalidOperationException(
                $"'{entry.Action}' is not in the audit action catalogue. Add a constant to "
                + "AuditActions rather than passing a literal.");

        // Validate TargetKind and Outcome the same way, against AuditTargets.All and
        // AuditOutcome.All. All three are strings with a fixed set of legal values that the
        // §6.1 search filters on, and §6.1.2 returns 422 for an outcome it does not recognise
        // — so a row stored with "success" or "Rejected" is an audit entry that exists and that
        // the only query written to find it can never retrieve. On an append-only table there is
        // no UPDATE path to correct it afterwards. Both AuditActions and AuditTargets expose
        // `All`; give AuditOutcome one too, and keep it in step with the column's CHECK.

        // Join the mutating slice's transaction if one is open. No-op for a denial, which
        // has no transaction and must be committed on its own.
        await _transaction.EnlistAsync(_db, ct);

        var request = _http.HttpContext?.Request;
        _db.AuditEntries.Add(new AuditRecord
        {
            ActorUserId = Truncate(actorId, 100),
            ActorRole   = actorRole,
            CustomerId  = entry.CustomerId,
            Action      = entry.Action,
            TargetKind  = entry.TargetKind,
            TargetId    = Truncate(entry.TargetId, 100),
            Outcome     = entry.Outcome,
            BeforeValue = Redaction.ToJson(entry.Before),
            AfterValue  = Redaction.ToJson(entry.After),
            OccurredAt  = DateTimeOffset.UtcNow,
            SourceIp    = _http.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent   = Truncate(request?.Headers.UserAgent.ToString() ?? "", 512)
        });

        await _db.SaveChangesAsync(ct);
    }
}
```

Six rules for this method:

**A. `CurrentUser` is resolved lazily, from the provider, not injected.** Injecting `CurrentUser`
into `AuditApi`'s constructor makes it a hard dependency, and `PermissionChecker` →
`IAuditApi` → `CurrentUser` means an unauthenticated request throws `AppException(401)` while
*constructing the object that was supposed to record the failure*. Resolve it inside `LogAsync`,
where the caller has already established there is a principal.

**B. Truncate, do not let the database reject.** A user agent longer than 512 characters or an
identifier longer than 100 is entirely client-controlled. If it reaches PostgreSQL it raises
`22001 string_data_right_truncation`, which — under the transaction rule — **rolls back the
mutation**. So an attacker with a long `User-Agent` header could block every audited write in
the application. Truncate every client-supplied string against the length in §1 before insert.
This is the highest-severity trap in this slice.

**C. Enlist before adding, always.** `EnlistAsync` is idempotent and a no-op when no transaction
is open. Calling it in `AppendAsync` rather than leaving it to the caller is what makes the
guarantee hold without seven other slices remembering to cooperate.

**D. An unknown action code throws.** It is a programming error, caught by tests, and it fails
loudly at the point of the mistake. Silently writing an uncatalogued code produces an audit log
that cannot be filtered.

**E. `LogAsync` propagates its exceptions.** Do not wrap the body in `try/catch`. The whole point
of the transaction decision is that an audit failure fails the mutation. The **one** caller that
catches is `PermissionChecker`, which logs and still throws its `403` — see §0.2 rule 3.

**F. Redact before serialising `Before`/`After`.** `Shared/` is the wrong home for this because
it is audit-specific; put `Redaction` in `Slices/Audit/Application/`. Requirements:

1. Serialise with `System.Text.Json` to a JSON object, never `ToString()`.
2. **Drop any property whose name *contains* a denied term**, case-insensitively. The terms are
   `password`, `hash`, `salt`, `token`, `secret`, `apikey`, `sessionid`, `cookie` — eight terms,
   and deliberately not a list of spellings.

   `name.Contains(term, StringComparison.OrdinalIgnoreCase)`, never
   `deniedNames.Contains(name)`. Exact matching is the wrong test and fails on the properties this
   slice actually receives: an audit entry's property is named after what changed, so the
   qualified spelling is the common one. Exact matching redacts `PasswordHash` and lets
   `NewPasswordHash` through; it redacts `Token` and lets `AccessToken` and `RefreshTokenHash`
   through. Substring matching over-redacts instead — a property called `TokenCount` becomes
   `"[redacted]"` — and that is the right trade: over-redacting costs one diagnostic, while
   under-redacting writes a live credential into the one table in this system that nothing ever
   purges and that no `UPDATE` path can ever clean.

   Do not "improve" this by adding the spelling you just saw leak. Two terms — `passwordhash` and
   `invitationtoken` — were deleted from an earlier version of this list precisely because
   `password` and `token` already cover them, and a list of spellings invites the next reader to
   append rather than to trust the term.
3. Replace a dropped value with the literal string `"[redacted]"` rather than removing the key.
   Knowing that a field changed without knowing to what is useful; not knowing it changed is not.
4. **Cap the serialised length** at 8 KB per side. Beyond that, store
   `{"truncated": true, "length": <n>}`. An unbounded JSON column on the largest table in the
   system is how this table becomes the reason a restore takes a day.
5. `null` in gives `null` out — do not write `"null"`.
6. **Recurse into nested objects and arrays.** A denied property nested one level down is exactly
   as much of a credential as one at the root.
7. **Serialisation failure must not fail the audited operation.** `SerializeToNode` throws on a
   cyclic object graph, on an unsupported converter, and on exceeding the default depth limit.
   Wrap it in `try`/`catch (JsonException or NotSupportedException)`, store
   `{"unserialisable": true, "type": "<runtime type name>"}` in that column, and log the exception
   at error level so the bad caller is findable. Do not let it propagate: an audit write is a side
   effect of some other operation, so an exception here rejects a legitimate customer edit with a
   `500` because its audit payload happened to hold an awkward object — and the transaction design
   in §5.2.C guarantees the mutation rolls back with it. The row, with actor, action and target, is
   the part that matters; the payload is the part that can be lost.

   Note that rule 4 measures length by serialising the whole object first, so an enormous payload
   is fully materialised in memory before being discarded. Accepted: callers pass small anonymous
   records, and this path firing at all means a caller made a mistake.
8. A test asserts that an object with a `NewPasswordHash` property serialises without the hash
   value present anywhere in the output string. **Use a qualified spelling in the test, not
   `PasswordHash`** — a test written against the bare name passes under exact matching and so
   proves nothing about rule 2.

---

## 6. Handlers

**Folder:** `Slices/Audit/Application/Handlers/`

### 6.0 Rules that apply to every handler in this slice

Canonical signature, no mediator, one handler per operation:

```csharp
public async Task<TResponse> Handle(TRequest req, CurrentUser user, CancellationToken ct)
```

**A. Reading the audit log is `AccountantAdmin` only.** One of the four powers reserved to
`AccountantAdmin` in `02-AuthorizationMatrix.md`. Every handler here begins with
`await _permissions.RequireAsync(user, "<Action>", ct: ct);` and the catalogue fragment grants
these actions to `AccountantAdmin` alone.

The *reason* matters and should shape how carefully this is enforced: the audit log records what
Accountant Users did. An `AccountantUser` who can read it can see whether their own actions are
being reviewed, and can read every Customer's activity in one query. This is not a
tidiness rule.

**B. No handler in this slice writes.** There is no create, edit, or delete handler. Appending is
`IAuditApi.LogAsync`, which is not a handler and is not reachable over HTTP. Do not add an
`/api/audit/create` endpoint for testing.

**C. Read handlers open no transaction and audit nothing.** Reading the audit log is not itself
an audited action — `01-DomainModel.md` §8 does not list it, and recording every read of the log
inside the log produces a table that grows on read.

**D. Every query is paginated and clamped.** See 0.4. No handler returns an unbounded list.

**E. Queries are `AsNoTracking()`.** Nothing here is ever modified, so change tracking is pure
overhead on the largest table in the database.

**F. Filter in the database, never in memory.** `ToListAsync()` followed by a LINQ `Where` on a
table with millions of rows is the difference between a 20 ms response and an outage. Compose the
`IQueryable` and let PostgreSQL use the indexes from §1.

**G. Validate the date range.** `From` later than `To` is a client error: throw
`AppException("'From' must not be later than 'To'.", 422)`. Do not silently swap them, and do
not let it return an empty page — an empty result looks like "nothing happened", which in an
audit tool is a dangerous thing to say by accident.

### 6.1 `SearchAuditLogHandler`

**File:** `Application/Handlers/SearchAuditLogHandler.cs`
Dependencies: `AuditDbContext`, `IPermissionChecker`.
Request: `SearchAuditLogRequestDto` → Response: `PaginatedResponse<AuditEntryDto>`

```
await _permissions.RequireAsync(user, "ReadAuditLog", ct: ct)

validate From <= To                      → 422 if not
clamp PageNumber to >= 1
clamp PageSize   to 1..50 (default 15)

query = _db.AuditEntries.AsNoTracking()
if ActorUserId present  → query = query.Where(e => e.ActorUserId == req.ActorUserId)
if Action     present   → validate it is in AuditActions.All, else 422; then filter
if TargetKind present   → filter
if TargetId   present   → filter          (see rule below)
if CustomerId present   → filter
if Outcome    present   → validate against AuditOutcome, else 422; then filter
if From       present   → query.Where(e => e.OccurredAt >= req.From)
if To         present   → query.Where(e => e.OccurredAt <= req.To)

total = await query.CountAsync(ct)
items = await query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
                   .Skip((page - 1) * size).Take(size)
                   .Select(AuditMapper.ToDto)     // projection, not entity materialisation
                   .ToListAsync(ct)
```

Specific rules:

1. **`TargetId` without `TargetKind` is a `422`.** Identifiers are not unique across kinds, so
   filtering on `TargetId` alone can return entries about an unrelated entity that happens to
   share a GUID string. It also cannot use `idx_audit_entries_target`, whose leading column is
   `target_kind`, so it degrades to a full scan. Require both together.
2. **An unrecognised `Action` or `Outcome` value is `422`, not an empty page.** A typo'd filter
   returning zero rows tells an investigator "this never happened". Reject it so they retype it.
3. **`CountAsync` runs the filter twice** — once to count, once to page. That is correct and
   acceptable here; do not "optimise" it by materialising the whole result set to count in
   memory.
4. Project with `.Select(...)` **before** `ToListAsync`, so PostgreSQL never sends
   `before_value` and `after_value` for a list request. This is rule 4.1 enforced at the query
   level rather than the mapper level.

### 6.2 `GetAuditEntryHandler`

**File:** `Application/Handlers/GetAuditEntryHandler.cs`
Dependencies: `AuditDbContext`, `IPermissionChecker`.
Request: `GetAuditEntryRequestDto` → Response: `AuditEntryDetailDto`

```
await _permissions.RequireAsync(user, "ReadAuditLog", ct: ct)

var record = await _db.AuditEntries.AsNoTracking()
    .FirstOrDefaultAsync(e => e.Id == req.AuditEntryId, ct)
    ?? throw new AppException("Audit entry not found.", 404);

return AuditMapper.ToDetailDto(record);
```

This is the only endpoint that returns `before_value` and `after_value`, and it returns exactly
one entry. No scope filter is applied — see 2.2 for why an `AccountantAdmin`-only surface is not
Customer-scoped.

### 6.3 `ListAuditActionsHandler`

**File:** `Application/Handlers/ListAuditActionsHandler.cs`
Dependencies: `IPermissionChecker`. **No DbContext.**
Request: none → Response: `AuditActionsResponseDto` (two string lists: actions, target kinds)

Exists so the audit-log screen can populate its filter dropdowns from the catalogue rather than
hard-coding a copy of it in TypeScript — a copy that would drift the moment an action is added.

```
await _permissions.RequireAsync(user, "ReadAuditLog", ct: ct)
return new AuditActionsResponseDto {
    Actions = AuditActions.All.OrderBy(a => a, StringComparer.Ordinal).ToList(),
    TargetKinds = AuditTargets.All.OrderBy(t => t, StringComparer.Ordinal).ToList()
};
```

It still requires the permission. The catalogue is not secret, but an endpoint that enumerates
every auditable action in the system is a map of the application's privileged operations, and
there is no reason for anyone but an Admin to fetch it.

### 6.4 `AuditMapper`

**File:** `Application/AuditMapper.cs`

Static methods `ToDto(AuditRecord)` and `ToDetailDto(AuditRecord)`. `ToDto` must be usable
inside an EF `.Select(...)` projection, which means it has to be an expression-compatible
static method or an `Expression<Func<AuditRecord, AuditEntryDto>>` field. **Do not** write it as
a method with statements and then call it inside `Select` — EF will either fail to translate it
or silently evaluate it client-side after fetching every column, defeating rule 6.1.4.

Prefer an explicit expression:

```csharp
public static readonly Expression<Func<AuditRecord, AuditEntryDto>> ToDto = e =>
    new AuditEntryDto { Id = e.Id, ActorUserId = e.ActorUserId, /* ... */ };
```

---

## 7. Endpoints

**File:** `Slices/Audit/AuditEndpoints.cs`

Route shape is `/api/{domain}/{action}`, path segments lowercase and **kebab-case at every word
boundary** ([App/GeneralAppArchitecture.md](../../App/GeneralAppArchitecture.md) §8).
`audit` is one word so the group is `/api/audit`, but `action-codes` is two.

```csharp
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/audit");

        group.MapPost("/search", async (
            SearchAuditLogRequestDto req, SearchAuditLogHandler handler,
            CurrentUser user, CancellationToken ct) =>
                Results.Ok(await handler.Handle(req, user, ct)));

        group.MapGet("/detail", async (
            Guid auditEntryId, GetAuditEntryHandler handler,
            CurrentUser user, CancellationToken ct) =>
                Results.Ok(await handler.Handle(
                    new GetAuditEntryRequestDto { AuditEntryId = auditEntryId }, user, ct)));

        group.MapGet("/action-codes", async (
            ListAuditActionsHandler handler, CurrentUser user, CancellationToken ct) =>
                Results.Ok(await handler.Handle(user, ct)));
    }
}
```

| Route | Verb | Why |
|---|---|---|
| `/api/audit/search` | `POST` | Eight optional filters plus paging. A `GET` with that many query parameters is unwieldy, and date ranges in query strings invite encoding bugs. A `POST` that does not mutate is acceptable here and is the pragmatic choice; it must still open no transaction and audit nothing. |
| `/api/audit/detail` | `GET` | Single record by identifier. `?auditEntryId=<guid>`. |
| `/api/audit/action-codes` | `GET` | Static data. **Kebab-case** — `/api/audit/actioncodes` is exactly the doubled-consonant class of typo the rule exists to prevent. |

Rules:

- **No route parameters.** `/api/audit/detail?auditEntryId=...`, never `/api/audit/{id}`. The
  locked route shape is `{domain}/{action}` and an identifier is not an action.
- **Query and body parameter names stay camelCase.** Kebab-case is for path segments only —
  `auditEntryId`, not `audit-entry-id`.
- Handlers are injected per endpoint. Do not resolve them from `IServiceProvider` inside the
  lambda.
- `CurrentUser` is a parameter, resolved by DI from the scoped registration.
- Do not add `.RequireAuthorization()` with a policy name here. Authorization is
  `IPermissionChecker` inside the handler; two mechanisms means two places to get it wrong.

---

## 8. Cross-slice boundaries — what this table must not do

This slice depends on **nothing** ([03-SliceInventory.md](../../03-SliceInventory.md) §2). It is
the root of the dependency graph, and that has concrete consequences a builder will be tempted
to violate.

1. **No foreign keys out of `audit_entries`.** Not on `customer_id`, not on `actor_user_id`, not
   on `target_id`. A foreign key to `customers` would make `Audit` depend on `Customers`,
   inverting the graph, and would mean a migration ordering constraint between two slices whose
   scripts both start at `001`.
2. **`target_kind` is a string, not a discriminated reference.** Resist any scheme that resolves
   it to a table name and joins.
3. **This slice never resolves an identifier to a name.** The audit reader shows
   `actor_user_id`, not "Maria Papadopoulou". Enriching an entry with a display name would
   require calling `Identity`, which `Audit` may not do. If the UI needs names, the **UI**
   fetches them from the owning slice and joins client-side. Say this explicitly in the UI spec
   when it is written.
4. **`Audit` is fire-and-forget from the caller's point of view** in the sense that no slice
   branches on what `LogAsync` returns (dependency rule 5). That is not the same as ignoring
   failure: under the transaction rule an audit failure aborts the caller's mutation. "Do not
   branch on the result" and "do not swallow the exception" are both true.
5. **No slice reads `audit_entries` directly.** There is no `IAuditApi.QueryAsync`. If another
   slice ever appears to need audit history, that is a design question to raise.

---

## 9. Migrations — SQL scripts, not `dotnet ef`

- Scripts live in `Slices/Audit/Infrastructure/Migrations/`, named `YYYYMMDD_###_Description.sql`.
- **EF Core migrations are not used.** Never run `dotnet ef migrations add` or
  `dotnet ef database update` in this repository.
- The runner is `Shared/Migrations/SqlMigrationRunner.cs` and it runs at startup, before the
  middleware pipeline is built.
- **The tracking key is the slice-relative path with forward slashes**, stored in
  `schema_versions.script_name VARCHAR(500)` — never `Path.GetFileName`. Sequence numbers restart
  at `001` in every slice, so `Audit/.../20260828_001_...sql` and
  `Customers/.../20260828_001_...sql` have colliding filenames and a filename key silently skips
  the second one.
- This slice's key is exactly
  `Audit/Infrastructure/Migrations/20260828_001_CreateAuditSchema.sql`.
- `gen_random_uuid()` requires `pgcrypto` on PostgreSQL below 13. On 16 it is built in, so no
  `CREATE EXTENSION` is needed. Do not add one speculatively.

---

## 10. Service registration

### 10.1 `Slices/Audit/AuditRegistration.cs` — rewrite this file

```csharp
public static class AuditRegistration
{
    public static IServiceCollection AddAuditSlice(
        this IServiceCollection services, IConfiguration configuration)
    {
        // The (sp, o) overload, and the request's shared connection — NOT a connection
        // string. This is what lets the audit write join the mutating slice's transaction.
        services.AddDbContext<AuditDbContext>((sp, o) =>
            o.UseNpgsql(sp.GetRequiredService<RequestConnection>().Connection));

        services.AddScoped<IAuditApi, AuditApi>();
        services.AddSingleton<IActionCatalogue, AuditActionCatalogue>();

        services.AddScoped<SearchAuditLogHandler>();
        services.AddScoped<GetAuditEntryHandler>();
        services.AddScoped<ListAuditActionsHandler>();

        return services;
    }
}
```

### 10.2 `Slices/Audit/AuditActionCatalogue.cs`

```csharp
internal sealed class AuditActionCatalogue : IActionCatalogue
{
    public string SliceName => "Audit";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } =
        new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
        {
            // All three are the same reserved power: "Read the audit log".
            ["ReadAuditLog"] = [UserRole.AccountantAdmin]
        };
}
```

One action name covers all three handlers, because
`02-AuthorizationMatrix.md` grants one power, not three. Do not invent
`SearchAuditLog`/`GetAuditEntry`/`ListAuditActions` as separate actions — three names for one
power means three places to get the role list wrong.

`PermissionDenied` is an **audit action code**, not a permission action. It does not belong in
this dictionary. Confusing the two catalogues is an easy mistake: `AuditActions` is *what
happened*, `IActionCatalogue` is *who may do it*.

### 10.3 What `Program.cs` adds

Two lines, and it names no handler or DbContext type:

```csharp
builder.Services.AddAuditSlice(builder.Configuration);
// ...
app.MapAuditEndpoints();
```

`AddAuditSlice` must be called **before** anything that resolves `IPermissionChecker`, and
`RequestConnection` must be registered in the Shared block before any slice.

### 10.4 Registration traps

1. **`AddScoped<AuditDbContext>()` instead of `AddDbContext`** — registers the context with no
   provider configured. If both are present the later registration wins and silently discards the
   options. Never both.
2. **The `o =>` overload instead of `(sp, o) =>`** — compiles, works, and quietly breaks the
   transaction guarantee, because the context gets its own connection and the audit write commits
   independently of the mutation. This failure is invisible until you specifically test that a
   failing audit write rolls back a mutation (§11.1 case 5).
3. **`AddSingleton<IActionCatalogue, ...>` vs `AddScoped`** — must be singleton. The composition
   validates duplicates at startup, which requires the instances to exist at startup.
4. **Forgetting `AddHttpContextAccessor()`** — `AuditApi` needs it for IP and user agent. It is
   registered once in the Shared block of `Program.cs`, not per slice.
5. **Registering handlers in `Program.cs`** — forbidden. They go in this file. Assembly scanning
   is banned.

### 10.5 Startup smoke check — do this before writing tests

```bash
docker compose up -d db
dotnet build
dotnet run --project AccountantApp.Api
```

Then, with `DevAuth` enabled:

```bash
curl -i -H "X-Dev-Role: AccountantAdmin" http://localhost:5000/api/audit/action-codes
curl -i -H "X-Dev-Role: AccountantUser"  http://localhost:5000/api/audit/action-codes
```

Expect `200` then `403`. **A `401` from either proves nothing** except that `DevAuth` is not
switched on — check `IsDevelopment()` and `DevAuth:Enabled` in
`appsettings.Development.json`.

**Do not comment out the `SqlMigrationRunner.RunAsync` call to make startup succeed without a
database.** If it throws `Npgsql.NpgsqlException: Failed to connect`, start the database. A run
that skips migrations has verified nothing about the schema, which is the only thing that could
have gone wrong.

---

## 11. Tests

### 11.1 At least one test must run against real PostgreSQL — mandatory

`Microsoft.EntityFrameworkCore.InMemory` is **banned from the API project** and is a test-only
dependency. It ignores `HasColumnName`, `jsonb` column types, `NOT NULL`, `TIMESTAMPTZ`, and
string lengths — which is to say it ignores every single thing sections 1 and 2 exist to get
right. A green in-memory suite is not evidence that this slice works.

One test, against a real database, must cover:

1. **The migration applies.** `SqlMigrationRunner.RunAsync` succeeds against a scratch database.
2. **It is tracked by slice-relative path.** `schema_versions.script_name` equals
   `Audit/Infrastructure/Migrations/20260828_001_CreateAuditSchema.sql` — not the bare filename.
3. **An entry round-trips through `AuditApi` and `SearchAuditLogHandler`**, with a `Before`/`After`
   payload, so the `jsonb` mapping and every `HasColumnName` are exercised in both directions.
4. **`occurred_at` survives as an instant.** Write a known `DateTimeOffset` with a non-zero
   offset, read it back, assert the `UtcDateTime` matches. This is the test that catches the
   `DateTime`/`DateTimeOffset` defect in 0.3.
5. **A failing audit write rolls back the mutation.** The point of the whole transaction design,
   and the only test that catches trap 10.4.2. Open a transaction on a second context, insert a
   row, force the audit insert to fail (an action code that passes the catalogue check but a
   `target_kind` longer than 50 characters will do it — **before** truncation is applied to that
   field), and assert the first row is absent afterwards.
6. **An over-length `User-Agent` does not fail the write.** Rule 5.2.B. Set a 5,000-character
   user agent, assert the entry is stored with a 512-character value and the transaction commits.

Skip it **loudly** when no database is reachable — `Skip.IfNot(...)` with a message saying the
schema is unverified. Never let it pass silently.

### 11.2 Behavioural cases (in-memory is acceptable)

| Case | Expected |
|---|---|
| `AccountantAdmin` searches the log | `200`, paginated |
| `AccountantUser` searches the log | `403`, **and a `PermissionDenied` entry is written** |
| `CustomerAdmin` searches the log | `403` + audited denial |
| `Employee` searches the log | `403` + audited denial |
| `GetAuditEntry` with an unknown id | `404` |
| `From` later than `To` | `422` |
| `TargetId` supplied without `TargetKind` | `422` |
| Unrecognised `Action` filter value | `422`, not an empty page |
| Unrecognised `Outcome` filter value | `422` |
| `PageSize` of 5,000 | clamped to 50, `200 OK` |
| `PageSize` of 0 or negative | clamped to 1 |
| `PageNumber` of 0 or negative | clamped to 1 |
| Search results | ordered `occurred_at DESC, id DESC` |
| List response DTO | contains **no** `BeforeValue`/`AfterValue` |
| Detail response DTO | contains them |
| `LogAsync` with an uncatalogued action string | `InvalidOperationException` |
| `LogAsync` with a `Before` object having `NewPasswordHash`, `AccessToken` and `PasswordSalt` properties, one of them nested inside an array | none of the three values appears anywhere in the stored JSON |
| `LogAsync` with a `Before` object the serialiser cannot handle (a cyclic graph) | the row is written, `before_value` is `{"unserialisable": true, ...}`, and the audited mutation commits |
| `LogAsync` with `Outcome` set to `"success"` | `InvalidOperationException`, no row written |
| `LogAsync` with a 20 KB `After` object | stored as `{"truncated": true, ...}` |
| `LogUnauthenticatedAsync` | writes `actor_role` = `Unknown`, no `CurrentUser` resolved |
| `AuditActions.All` | non-empty, and contains `TicketCreated` |
| Two catalogues declaring one action | startup throws, naming both slices |
| A catalogue entry with an empty role array | startup throws |

---

## 12. Known constraints

1. **Nothing in this slice is ever updated or deleted, by anyone.**
   `02-AuthorizationMatrix.md`: *"Edit or delete an audit entry — **Nobody.** No API exists for
   this."* No update handler, no delete handler, no soft-delete flag, no purge, no "clear test
   data" path. Do not add one even where it looks harmless.
2. **The table grows without bound and that is the accepted design.** Retention is indefinite
   (`01-DomainModel.md` §9.2). The mitigations are the indexes in §1, the mandatory pagination in
   0.4, and the 8 KB cap on JSON payloads — not deletion.
3. **The actor is never caller-supplied.** Removing `Actor` from the `AuditEntry` contract is a
   breaking change to existing call sites in `PermissionChecker` and the `TicketTypes` handlers.
   Fix the call sites; do not keep the property "for compatibility".
4. **No display names anywhere.** §8 rule 3.
5. **`before`/`after` are only populated for changes to existing data.** A create has no before;
   a read has neither. Do not synthesise an empty object to make the shape uniform.
6. **This slice has no notion of a Customer boundary.** It is `AccountantAdmin`-only and an Admin
   sees everything. Do not apply `WhereInCustomerScope` here (see 2.2).
7. **Migrations are immutable once applied.** The in-place replacement in 0.0 is licensed only by
   the verified fact that no database has ever existed. That licence expires the first time the
   application runs against PostgreSQL.

---

## 13. Questions to flag rather than answer

If any of these comes up, stop and raise it. Do not invent a behaviour —
[README.md](../../README.md) is explicit that a gap should be flagged, not filled.

1. Whether the audit reader needs full-text search over `before`/`after`. Currently it does not,
   and adding a GIN index on two `jsonb` columns of the largest table is not a decision to make
   in passing.
2. Whether a failed login should be rate-limited *before* or *after* the audit write. Rate
   limiting on auth endpoints is mandatory (`README.md`), and an attacker who can cheaply write
   audit rows has a write amplification primitive. `Identity`'s plan owns this; flag it if that
   plan does not address it.
3. Whether `source_ip` should record the proxy's address or the `X-Forwarded-For` value. Behind
   Caddy, `RemoteIpAddress` is the proxy, which makes the field useless. This needs
   `ForwardedHeadersOptions` configured with a known-proxy allow-list — **do not** trust the
   header unconditionally, which would let a client forge its own source IP into the audit log.
   Flag it; it is an infrastructure decision, not an Audit one.
4. Any need for a second slice to read audit history (§8 rule 5).

---

## Files checklist

| File | Action |
|---|---|
| `Shared/Auth/CurrentUser.cs` | **Modify** — add `Guid? CustomerId` |
| `Shared/Auth/CurrentUserFactory.cs` | **Modify** — read `customer_id`; `401` if a Customer-scoped role lacks it |
| `Shared/Data/RequestConnection.cs` | **New** |
| `Shared/Data/IRequestTransaction.cs` | **New** |
| `Shared/Data/RequestTransaction.cs` | **New** |
| `Shared/Authorization/IActionCatalogue.cs` | **New** |
| `Shared/Authorization/PermissionChecker.cs` | **Modify** — delete the hard-coded sets, compose `IEnumerable<IActionCatalogue>`, validate at startup |
| `Slices/Audit/Core/AuditRecord.cs` | **New** |
| `Slices/Audit/Infrastructure/AuditDbContext.cs` | **Modify** — every column in §1, `DateTimeOffset`, config class |
| `Slices/Audit/Infrastructure/Configurations/AuditRecordConfiguration.cs` | **New** |
| `Slices/Audit/Infrastructure/Migrations/20260828_001_CreateAuditSchema.sql` | **Replace** — see 0.0 |
| `Slices/Audit/ExternalInterfaces/IAuditApi.cs` | **Modify** — expanded contract, no `Actor`, add `LogUnauthenticatedAsync` |
| `Slices/Audit/ExternalInterfaces/AuditApi.cs` | **Modify** — enlist, truncate, redact, resolve actor |
| `Slices/Audit/ExternalInterfaces/AuditActions.cs` | **New** — codes + `AuditTargets` |
| `Slices/Audit/Application/Redaction.cs` | **New** |
| `Slices/Audit/Application/AuditMapper.cs` | **New** |
| `Slices/Audit/Application/Dtos/*.cs` | **New** — 5 DTOs |
| `Slices/Audit/Application/Handlers/SearchAuditLogHandler.cs` | **New** |
| `Slices/Audit/Application/Handlers/GetAuditEntryHandler.cs` | **New** |
| `Slices/Audit/Application/Handlers/ListAuditActionsHandler.cs` | **New** |
| `Slices/Audit/AuditActionCatalogue.cs` | **New** |
| `Slices/Audit/AuditRegistration.cs` | **Modify** |
| `Slices/Audit/AuditEndpoints.cs` | **New** |
| `Program.cs` | **Modify** — `RequestConnection`, `MapAuditEndpoints` |
| `AccountantApp.Tests/Audit/AuditSchemaTests.cs` | **New** — the PostgreSQL test |
| `AccountantApp.Tests/Audit/AuditFlowTests.cs` | **New** — behavioural cases |

## Success criteria

1. `dotnet build` produces **0 errors and 0 warnings**.
2. `docker compose up -d db` then `dotnet run` starts without exception, applies the migration,
   and logs the `DevAuth` warning.
3. `schema_versions` contains the slice-relative path key, not the bare filename.
4. `audit_entries` has exactly the columns §1 defines — `id`, `actor_user_id`, `actor_role`,
   `customer_id`, `action`, `target_kind`, `target_id`, `outcome`, `before_value`, `after_value`,
   `occurred_at`, `source_ip`, `user_agent` — with `before_value` and `after_value` as `jsonb` and
   `occurred_at` as `TIMESTAMPTZ`. Named, not counted: a count drifts every time the table changes
   and this one already said 11 while §1 defined 13.
5. All five indexes from §1 exist, and there is **no** unique constraint on the table.
6. `GET /api/audit/action-codes` returns `200` for `AccountantAdmin` and `403` for
   `AccountantUser`, `CustomerAdmin`, and `Employee`.
7. Each of those three `403`s has written a `PermissionDenied` row.
8. A denial is recorded even when it happens outside any transaction.
9. **A forced audit failure rolls back the caller's mutation** — the transaction guarantee,
   demonstrated by a test.
10. A 5,000-character `User-Agent` does not fail an audited write.
11. A `Before` payload containing a password hash stores `"[redacted]"` and the hash appears
    nowhere in the column.
12. A 20 KB payload is stored truncated, not in full.
13. `LogAsync` with an uncatalogued action code throws rather than inserting.
14. The search endpoint is paginated, clamped at 50, and ordered `occurred_at DESC, id DESC`.
15. List responses omit `before_value`/`after_value`; the detail response includes them.
16. `PermissionChecker` contains **no hard-coded action names** — every action comes from a
    registered `IActionCatalogue`.
17. Startup fails loudly if two slices declare the same action name.
18. There is no code path in the repository that updates or deletes a row in `audit_entries`.
19. `dotnet test` passes, with the PostgreSQL test **executed, not skipped**.

---

# Correction Notes — review of 2026-09-01

Written after validating the working-tree implementation against this plan and documents 0–5.
**These are corrections to this plan and to the numbered documents, recorded so the next build
cycle does not repeat the same guesses.** Each finding says whether the fault is in the
IMPLEMENTATION, the SPEC, or both.

State at review: `dotnet build` = 0 errors. `dotnet test` = 27 passed, 0 failed, **2 skipped**.
See A-8 — the skips matter more than the passes.

## A-1 (BLOCKER, implementation) — the read side of the slice was never built

Sections 4, 6 and 7 and success criteria 6, 14 and 15 describe a read side that does not exist.
Absent from the tree: `AuditEndpoints.cs`, `Application/Handlers/`, `Application/Dtos/`,
`Application/AuditMapper.cs`. `Program.cs` has no `app.MapAuditEndpoints()`.

Consequence: `["ReadAuditLog"]` in `AuditActionCatalogue.cs` is dead — nothing in the repository
ever passes that action to `IPermissionChecker`. Reading the audit log is one of the four powers
reserved to `AccountantAdmin` by 02-AuthorizationMatrix §10, and it is currently unimplemented
and unenforceable.

Correction: build §4 DTOs, §6.1–6.4 handlers, §7 endpoints; register the three handlers in
`AuditRegistration`; add the `Map` line to `Program.cs`.

**RESOLVED 2026-09-01.** All of it built: five DTOs, `AuditMapper` (list projection as an
`Expression` so `before_value`/`after_value` are never selected), the three handlers, `AuditEndpoints`,
the three `AddScoped` registrations, and `app.MapAuditEndpoints()` — which also closes X-3.2.
`ReadAuditLog` now has callers. 29 tests in `AccountantApp.Tests/Audit/AuditReadTests.cs`.

Three deliberate deviations from §4/§6, each recorded because the next build cycle will otherwise
"fix" them back:

1. **`AuditActionsResponseDto` carries a third list, `Outcomes`.** §6.3 specifies two. The search
   returns `422` for an unrecognised outcome, so a client that hard-codes its own copy of the three
   values can `422` itself — the same drift argument that justifies the other two lists.
2. **`SearchAuditLogHandler` also validates `TargetKind` against `AuditTargets.All`** (§6.1 validates
   `Action` and `Outcome` only). Same reasoning as rule 2, same catalogue `AuditApi` validates
   against on write, and an unrecognised kind can never match a stored row — so accepting it can
   only ever produce the silent empty page rule 2 exists to prevent.
3. **`PageSize = 0` resolves to the default 15, not to 1.** §11.2's table says 1; §0.4 requires the
   shared `PaginatedQuery.Normalize`, which treats 0 as "unspecified". The shared helper wins — 0 is
   an unset field, and answering an unset field with a single row is a stranger reading of the
   request than answering it with a page. **Change §11.2's row, not the code.**

Also added: `AccountantApp.Tests/EndpointRoutingTests.cs`, which builds every slice's endpoints for
real and asserts each `RequestDelegate` constructs. That is the check whose absence let an
unregistered handler in `Notifications` take down every route in the application — a failure no
handler-level test can see, because a unit test constructs the handler itself. It is not specific to
this slice and guards all four.

## A-2 (BLOCKER, both) — the migration was rewritten in place after it had already shipped

§0.0 licensed replacing `20260828_001_CreateAuditSchema.sql` in place, conditioned on a fact the
plan cannot itself establish: *"this script has never been applied to any database."* §12.7 adds
that the licence "expires the first time the application runs against PostgreSQL."

The licence had already expired. `git show HEAD:` for that path is the **6-column** table
(`action, target_id, actor, details, occurred_at`), committed in `eb5c3b6`. The script was
nonetheless edited in place to the current **13-column** table, and no `002` exists.
`SqlMigrationRunner` scans *all* slices, so any dev database used during the TicketTypes work
already recorded `Audit/Infrastructure/Migrations/20260828_001_CreateAuditSchema.sql` in
`schema_versions` against the old shape. On such a database the runner skips the rewritten script
and every audit write then fails with `column a.actor_user_id does not exist` — inside the
caller's transaction, taking the caller's write down with it.

**Spec fault:** line 3 of this plan ordered "build this slice **first**", but the repo built
TicketTypes first, voiding the precondition. A plan must not gate a destructive instruction on an
ordering it does not control, and the fallback ("add a `002` instead") was given with no procedure
for reconciling an already-applied table.

Correction: check `schema_versions` on every existing database. If the old key is present, revert
`001` to its committed content and add `20260830_002_ExpandAuditEntries.sql` (`DROP TABLE
audit_entries` then recreate — it has never held a row worth keeping). Rewrite §0.0 to require
that check as a build step with recorded output, not as a hypothetical.

## A-3 (BLOCKER, spec) — document 4 contradicts document 5 on audit failure, and precedence selects the wrong one

The most damaging defect found, because it is in the locked numbered documents rather than a
slice plan.

- 04-Infrastructure §6, line 364: *"Audit log write failures — `Audit` is fire-and-forget from
  callers, so **a silent failure destroys the record with nothing else breaking.** This is the one
  metric that must page."*
- App/GeneralAppArchitecture §5, line 376: *"**The rule: a mutation and its audit entry commit
  together or not at all.** If the audit write fails, the mutation rolls back and the caller
  receives a `500`."*

These cannot both hold. Under doc 5 an audit failure is maximally loud; under doc 4 it is silent
and nothing else breaks. README says the lower-numbered document wins, so **doc 4 wins** — and
doc 4 mandates the opposite of the LOCKED rule in doc 5, of §5.2.E of this plan (*"`LogAsync`
propagates its exceptions. Do not wrap the body in `try/catch`"*), and of the implementation,
which follows doc 5 and is correct.

A builder applying the precedence rule literally would have wrapped `AppendAsync` in a swallowing
`try/catch`, producing a system where committed mutations are silently unaudited — exactly what
01-DomainModel §8 exists to prevent.

03-SliceInventory rule 5 uses the same phrase — *"`Audit` is fire-and-forget from the caller's
point of view"* — but qualifies it as *"No slice's business logic branches on what Audit
returns"*, which **is** compatible with commit-together. Only 04 §6 is genuinely contradictory.

Correction, in 04-Infrastructure §6: replace with *"Audit log write failures — an audit failure
aborts the caller's mutation and returns `500` (App §5), so this surfaces as a spike in `500`s
rather than as missing rows. This is the one metric that must page."* Remove the words
"fire-and-forget" and "silent failure" from doc 4 entirely. In 03 rule 5, keep the rule but add
*"Fire-and-forget means no caller branches on the result. It does not mean failures are swallowed
— see App §5."*

## A-4 (MAJOR, both) — the commit-together guarantee has no detector, and is already violated

§5.2.C claims enlisting inside `AppendAsync` "is what makes the guarantee hold without seven other
slices remembering to cooperate." It does not. `RequestTransaction.EnlistAsync` returns
immediately when `_transaction is null`, so a caller that never opened one silently downgrades to
two independent commits, and nothing in `Audit` can tell.

Confirmed by grep: only the five `Customers` handlers call `BeginAsync`/`CommitAsync`. All three
mutating TicketTypes handlers call `SaveChangesAsync` then `LogAsync` with no transaction. A
failing audit write there leaves a committed, unaudited mutation.

Correction: fix the three TicketTypes handlers (recorded in that slice's plan as T-1). In this
plan, add to §5.2 that `AppendAsync` must detect `EnlistAsync` finding no open transaction when
`Outcome != Denied`, and log a warning — throwing in Development. §10.4.2 already admits the
defect is "invisible until you specifically test that a failing audit write rolls back a
mutation"; an invisible guarantee with no detector and no test (A-8) is not a guarantee.

## A-5 (MAJOR, implementation) — `PermissionChecker` still hard-codes TicketTypes' action names

§0.2 said *"`PermissionChecker` currently holds two hard-coded `HashSet<string>` fields. **Delete
them.**"* Success criterion 16 requires no hard-coded action names. App §4 is LOCKED on it.

The `HashSet`s are gone, but `Shared/Authorization/PermissionChecker.cs:71` now carries a nested
`LegacyTicketTypesCatalogue` duplicating all five TicketTypes actions byte-for-byte from
`TicketTypesActionCatalogue.cs`, injected as the *only* catalogue by the 2-argument constructor.
DI selects the 3-argument constructor, so production is currently correct — but this is a live
trap with two edges:

1. Slice-specific action names in `Shared/` violates 03-SliceInventory rule 6 and App §4's
   *Never in Shared: business logic of any slice*.
2. Anything constructing `new PermissionChecker(auditApi, logger)` gets a checker in which
   `ReadAuditLog`, `CreateCustomer` and `SuspendCustomer` **do not exist** — so an
   `AccountantAdmin` is denied the audit log *and the denial is written to the audit log as though
   it were legitimate*. This is not hypothetical: all ten `PermissionChecker` constructions in
   `TicketTypesFlowTests.cs` use that overload.

Correction: delete `LegacyTicketTypesCatalogue` and the 2-argument constructor. Keep the
duplicate-action guard, the empty-roles guard, and the eager
`GetRequiredService<IPermissionChecker>()` in `Program.cs` — those three are right.

## A-6 (MAJOR, both) — scope denials are never audited and have no action code

02-AuthorizationMatrix §1 defines **two** denial kinds — `403` for role, **`404` for
out-of-scope** — and says *"Every denial writes an Audit Entry"* (restated as §12.5). Only role
denials are audited, inside `RequireAsync`. `CustomerScope.WhereInCustomerScope` filters rows away
and the handler throws a bare `AppException(..., 404)` with no audit call. `AuditActions` has no
`ScopeDenied` code and `AuditTargets` has no convention for a record the caller may not see.

**Spec fault:** §3's checklist narrows doc 2's rule to *"Every permission-denied response —
`Shared/Authorization`"*, which reads as role denials only. The scope denial has no owner, no code
and no hook.

Correction: decide explicitly whether a `404` scope denial is audited. If yes, add the action code
and put the hook at the `404`-throwing site — `CustomerScope` returns an `IQueryable` and cannot
know a row was filtered, so it is the wrong place.

## A-7 (MAJOR, both) — `source_ip` will record Caddy's address in production

04-Infrastructure §3 already decided this and supplied the code: *"Because the API sits behind a
proxy, it must trust the forwarded headers **or every audit entry records the proxy's address
instead of the caller's**… In `Program.cs`, **before any other middleware**:
`app.UseForwardedHeaders(...)`."*

`Program.cs` has no `UseForwardedHeaders` call anywhere (verified by grep); the first middleware is
`AppExceptionMiddleware`. `AuditApi` reads `Connection.RemoteIpAddress`, so in the locked
three-container topology every audit row gets the proxy IP and the `source_ip` column is useless.

**Spec fault:** §13.3 tells the builder to *flag* the proxy question rather than implement it,
contradicting doc 4 which had already answered it — and doc 4 leaves `KnownNetworks` as a comment,
so there was nothing concrete to copy.

Correction: add `UseForwardedHeaders` as the first middleware with an explicit
`KnownNetworks`/`KnownProxies` allow-list for the compose network. Never unconditional header
trust, or a client forges its own audit source IP. Delete §13.3 and replace it with the concrete
configuration.

## A-8 (MAJOR, implementation) — no tests exist for this slice, and the mandatory one is absent

§11.1 is headed *"At least one test must run against real PostgreSQL — mandatory"* and enumerates
six cases, including *"A failing audit write rolls back the mutation… the only test that catches
trap 10.4.2"*. The files checklist requires `AccountantApp.Tests/Audit/AuditSchemaTests.cs` and
`AuditFlowTests.cs`. Neither exists. No test anywhere touches `audit_entries`, `AuditApi`,
`Redaction`, or catalogue composition.

Note the compounding failure: §11.1 case 5 is precisely the test that would have caught A-4.

Separately, **success criterion 19 currently fails.** It requires `dotnet test` to pass "with the
PostgreSQL test **executed, not skipped**". The two existing schema tests (`CustomersSchemaTests`,
`TicketTypesSchemaTests`) both **skip**, so the entire class of SQL-versus-EF-configuration drift
is unguarded while the suite reports green. Correction: make the Postgres tests fail — not skip —
when no database is reachable in CI.

## A-9 (MAJOR, spec) — redaction matches whole property names only, so most secrets pass through

§5.2.F.2 says *"Drop any property **whose name matches** a deny-list, case-insensitively."*
`Redaction.cs:34` implements `DeniedProperties.Contains(property.Key)` — exact match. So
`PasswordHash` is caught only because that literal happens to be in the list, while
**`NewPasswordHash`, `AccessToken`, `InviteToken`, `PasswordSalt` and `Api_Key` are written to
`after_value` in full.**

Success criterion 11 passes anyway, which is the problem: it tests the one spelling that is listed.

**Spec fault:** "matches" is genuinely ambiguous, and the list argues both ways — it contains both
`password` *and* `passwordhash` (only sensible under exact matching) alongside bare `hash` and
`token` (only sensible under substring matching).

Correction: specify substring matching (`key.Contains(term, OrdinalIgnoreCase)`), which lets the
list shrink to `password`, `hash`, `salt`, `token`, `secret`, `apikey`, `sessionid`, `cookie`.
Restate criterion 11 using a spelling *not* present in the list.

## A-10 (MINOR, both) — `Redaction.ToJson` can throw, and the throw rolls back a valid mutation

§5.2.F lists six requirements and is silent on serialisation failure.
`JsonSerializer.SerializeToNode(value)` at `Redaction.cs:20` is uncovered: a cyclic object graph
throws `JsonException`, and so does a graph deeper than the default `MaxDepth` of 64 — plausible
for a `Before`/`After` built from client-supplied ticket field values. Under the A-3
commit-together rule that exception rolls back an otherwise-valid mutation and returns `500`,
which README forbids for anything a client can trigger by sending a value.

Correction: wrap the body in `try/catch`, store `{"unserialisable":true}`, log. Add as rule 7 in
§5.2.F. Note also that the length check serialises the full object *before* measuring it, so an
oversized payload is fully materialised in memory before being discarded.

## A-11 (MINOR, implementation) — `LogUnauthenticatedAsync` is a default interface method that throws

§5.1 declares it an ordinary member and justifies it: *"Without this method the single most
security-relevant entry in the catalogue cannot be written."* `IAuditApi.cs:23-27` gives it a
default body of `throw new NotSupportedException()`. `AuditApi` does override it, so today is fine
— but any second implementation compiles without it and turns "audit the failed login" into a
`500` at the moment it matters most. Correction: remove the default body.

## A-12 (MINOR, both) — action-name drift, and `Outcome` is never validated

1. §3 lists `CustomerEdited`; `AuditActions` defines `CustomerUpdated`. The code is internally
   consistent with the Customers slice, so the **plan** is what should change — but §3 calls
   itself a fixed catalogue, so the drift needs recording either way.
2. `AppendAsync` validates `Action` against `AuditActions.All` and `TargetKind` against
   `AuditTargets.All` (the latter an unrequested extra), but **never validates or truncates
   `Outcome`**, and the SQL has no `CHECK` constraint. A caller passing `"success"` or
   `"Rejected"` stores a row that the §6.1.2 search filter — which returns `422` for an
   unrecognised outcome — can never retrieve. An audit entry that exists but is invisible to the
   only query written to find it.

Correction: reconcile the name in one direction; validate `entry.Outcome` against `AuditOutcome`
the way `Action` is validated, or add `CHECK (outcome IN ('Success','Denied','Failure'))`.

## A-13 (MINOR, spec) — success criterion 4 contradicts §1

Criterion 4 requires *"`audit_entries` has all 11 columns"*. The DDL in §1 defines **13**
(`id, actor_user_id, actor_role, customer_id, action, target_kind, target_id, outcome,
before_value, after_value, occurred_at, source_ip, user_agent`), and the implementation matches
§1. Correction: change criterion 4 to 13, and prefer naming the columns to counting them — a
count drifts every time the table changes.

---

## Spec gaps — what a builder had to guess

Ranked by how likely a wrong guess is to produce a security hole or a broken build.

1. **Audit failure semantics** (A-3). Two locked documents state opposite behaviours and the
   precedence rule selects the wrong one. The highest-value fix in this list.
2. **Scope denials** (A-6). Doc 2 says every denial is audited and defines out-of-scope `404` as
   a denial; no document says where such a denial is audited, with which code, or against which
   target.
3. **Nothing detects a caller that forgot `BeginAsync`** (A-4), while the docs assert that
   correctness does not depend on the calling slice remembering.
4. **A slice that forgets to register its `IActionCatalogue`.** App §4 mandates startup failure
   for duplicate names and empty role arrays; rule 7 of doc 3 mandates startup failure for an
   unregistered *inverted* implementation. Nothing covers a **missing** catalogue, which is
   structurally undetectable — there is no expected-slice list — and manifests as a blanket `403`
   for every action of that slice plus a stream of misleading `PermissionDenied` rows. Either
   require each `Add{Slice}Slice` to assert its own catalogue's presence, or state plainly that
   this case cannot fail at startup.
5. **Redaction on hostile input** (A-9, A-10): exact versus substring matching, and what to store
   when serialisation throws.
6. **Append-only is convention only.** Doc 1 §8 and doc 2 §10 say "never updated, never deleted",
   and the code satisfies that today. But no document asks for database-level enforcement
   (`REVOKE UPDATE, DELETE`, a rule, or a trigger), while `AuditRecord` has public setters and
   `AuditDbContext.AuditEntries` is a writable `DbSet`. One `Remove()` in a future slice would
   compile and pass review, and success criterion 18 ("there is no code path in the repository")
   becomes unverifiable by inspection as the repo grows.
7. **`actor_role = 'Unknown'`** for `LogUnauthenticatedAsync` is not a `UserRole` value. No
   document says how the reader's role filter or `AuditEntryDto` treat it. Nor is the plan's
   "Identity only" restriction on that method enforced anywhere — any slice can call it and
   attribute an entry to an arbitrary actor string, which is the forgery that the
   actor-is-never-caller-supplied rule (§5.1, §12.3) exists to close.
8. **Proxy ownership** (A-7). §13.3 defers to "an infrastructure decision" that doc 4 had already
   made, so no document owns it and the field ships knowingly wrong.

---

# Appendix — cross-cutting corrections (review of 2026-09-01)

**These findings do not belong to the Audit slice.** They are recorded here because they belong to no
single slice plan, and because this plan already carries the other cross-cutting corrections (A-3 on
document 4 versus document 5, A-5 on `Shared/Authorization`, A-7 on forwarded headers). The fixes
themselves land in the numbered documents and in `Program.cs`, not in `Slices/Audit/`.

Order matters: **X-1 is the correction with the widest blast radius in the whole review**, because
four unbuilt slices will read the same sentence.

## X-1 (BLOCKER, spec) — "Cross-slice transactions are not supported" is contradicted twice in its own document

03-SliceInventory §5, line 196: *"**Transaction boundaries:** Per-request, per-slice. Cross-slice
transactions are not supported (see App/GeneralAppArchitecture.md section 5)."*

Two rules in the same document require exactly that:

- 03-SliceInventory §5 rule 2, line 43: *"`Employees` owns the composite endpoint, which calls
  `ICustomerApi` to create the Customer, creates the first Employee, and asks `Identity` to invite
  them — **in one request-scoped transaction**, so a failure at any step leaves no Customer
  behind."* Three slices, one transaction.
- App/GeneralAppArchitecture §5, LOCKED: *"a mutation and its audit entry commit together or not at
  all"* — the mutating slice and `Audit`, one transaction.

So the blanket statement at line 196 is false as written, and it is false in the direction that
causes data loss: a builder who reads it concludes that no transaction spans a cross-slice call, and
therefore does not open one. That is precisely the defect recorded as **T-1** in the TicketTypes
plan (three mutating handlers with no transaction, so a failing audit write leaves a committed
unaudited mutation), and it is precisely what will happen to the `Employees` composite endpoint —
where the cost is a half-created Customer with no Employee and no invitation.

Correction, in 03-SliceInventory §5: *"**Transaction boundaries:** One transaction per request,
opened by the slice that owns the endpoint. A cross-slice **call** may participate in it — `Audit`
always does (App §5), and the `Employees` composite endpoint requires it (§5 rule 2). What is not
supported is a transaction spanning two **requests**, or two separate database connections."* Then
make App §5 state the same thing in the same words, since line 196 cites it as its authority.

## X-2 (MAJOR, spec) — the README index and status section are stale, so three of the four plans are invisible

README's document table stops at *"| 7 | Slices/TicketTypes/IMPLEMENTATION_PLAN.md |"*, and the
"Status of this documentation" section (lines 157-160) still says: *"**Not yet written, required
before v1 ship:** Per-slice functional specs: Identity, Customers, Employees, Tickets, Documents,
Notifications, Audit… `TicketTypes` has one already."*

**Five** of those named-as-missing plans exist and are large: `Slices/Audit/IMPLEMENTATION_PLAN.md`
(1231 lines before these notes), `Slices/Identity/IMPLEMENTATION_PLAN.md` (2092),
`Slices/Employees/IMPLEMENTATION_PLAN.md` (1736), `Slices/Customers/IMPLEMENTATION_PLAN.md` (1177),
and `Slices/Notifications/IMPLEMENTATION_PLAN.md` (1157). **None is in the index**, and none is
tracked by git — `git status` reports all five directories as untracked, so they are invisible to
history as well as to the index. Only `Tickets` and `Documents` remain genuinely unwritten;
`Identity` is in fact the largest plan in the repository.

This is not cosmetic. README is the only document that states the **precedence rule**, and it states
it by numbering the index — *"the lower-numbered document wins"*, and a plan under `Slices/` loses to
every numbered document. A plan that is absent from the index has no stated rank. A builder handed
the Notifications plan has, from README alone, no way to know it ranks below documents 0–5, which is
the fact that resolves N-5 (this slice may not overrule 02) and N-3.

Correction: add rows 8, 9 and 10 for the Audit, Customers and Notifications plans; move the three
from the "not yet written" list, leaving Identity, Employees, Tickets and Documents; update the
"~2,400 lines" figure or drop the count, since it now excludes four plans rather than one; and state
the precedence rule for `Slices/` in prose rather than relying on the numbering, so an unindexed plan
still has a rank.

## X-3 (MAJOR, implementation) — `Program.cs` is missing three things the numbered documents require

The composition root is otherwise clean and satisfies App §7 — it names no handler or DbContext type,
registers each slice through one `Add{Slice}Slice` call, and the eager
`GetRequiredService<IPermissionChecker>()` probe at lines 45-48 is a genuinely good addition that
turns a duplicate action name or an empty role array into a startup failure. Three required pieces
are absent:

1. **`UseForwardedHeaders`** — 04-Infrastructure §3 supplies the code and says it must be *"before
   any other middleware"*, or *"every audit entry records the proxy's address instead of the
   caller's."* The first middleware is `AppExceptionMiddleware` at line 57. Recorded in full as A-7
   above; noted here because the fix is in `Program.cs`, not in the Audit slice.
2. **`app.MapAuditEndpoints()`** — absent because the endpoints are absent (A-1). Listed here so the
   `Program.cs` change is not forgotten when the read side is built.
3. **The SPA fallback.** 04-Infrastructure §1 requires that *"everything under `/api` is the API"*
   and everything else serves the React app. `Program.cs` maps two endpoint groups and calls
   `app.Run()` — there is no `UseStaticFiles`, no `MapFallbackToFile("index.html")`. The
   `UseStatusCodePages` handler at lines 61-70 correctly returns `ProblemDetails` for an unmatched
   route, which satisfies the TicketTypes criterion 14 clause *"a mistyped route under `/api` returns
   a `ProblemDetails` `404` rather than… `index.html`"* — but it does so by returning
   `ProblemDetails` for **every** unmatched path, including `/customers`, so the SPA cannot deep-link.
   When the fallback is added, the `/api` prefix must be excluded from it explicitly, or criterion 14
   regresses.

Also worth deciding now rather than later: there is no rate limiting anywhere, and no request-body
size limit (recorded as T-11 in the TicketTypes plan, whose §4.0 F rule 7 defers to a limit that does
not exist). 04-Infrastructure does not set either on the Caddy side.

## X-4 (MINOR, implementation) — `appsettings.Development.json` ships in the build output, collapsing DevAuth's two independent guards into one

`Program.cs` lines 28-32 document the design: *"Development-only authentication, behind **two
independent guards**: the environment AND an explicit flag that exists only in
appsettings.Development.json."* `devAuthEnabled` is `IsDevelopment() && GetValue<bool>(ConfigKey)`,
and `DevAuth` appears only in `appsettings.Development.json` — so far, correct.

But `AccountantApp.Api.csproj` sets no `ExcludeFromPublish` or `CopyToOutputDirectory` condition, and
`bin/Debug/net10.0/appsettings.Development.json` is present in the output. The file travels with the
artifact.

The two guards are therefore not independent: ASP.NET only loads `appsettings.{Environment}.json`, so
a single mistake — `ASPNETCORE_ENVIRONMENT=Development` on the production host — flips **both** guards
at once, and the API begins trusting the `X-Dev-Role` header from the internet. The design's intent
was that an attacker or an operator error would have to defeat two unrelated things.

Correction: add `<Content Remove="appsettings.Development.json" />` plus an explicit
`CopyToOutputDirectory="Never"` for the published configuration (or a `Condition` on
`'$(Configuration)' == 'Debug'`), so the flag genuinely cannot exist in a production artifact. Record
it in 04-Infrastructure §2 alongside the container build, since it is a packaging rule, not a code
rule.

## X-5 (MINOR, implementation) — build output is committed to the repository

The working tree carries modifications to `AccountantApp.Api/bin/` and `AccountantApp.Api/obj/` —
`AccountantApp.Api.dll`, `.pdb`, `apphost.exe`, `*.cache`, `sourcelink.json`, and a copy of the Audit
migration under `bin/Debug/net10.0/Slices/…`. Two consequences worth fixing before more slices land:

1. Every build produces a dirty tree, so `git status` stops being usable as a review signal and the
   real source changes are buried. It is also why the migration rewrite recorded as A-2 above was
   easy to miss.
2. The committed `bin/` copy of `20260828_001_CreateAuditSchema.sql` is a **second source of truth**
   for a migration whose in-place rewrite is already the subject of A-2. `SqlMigrationRunner` reads
   from `ContentRootPath`, so which copy wins depends on how the app is started.

Correction: add `bin/` and `obj/` to `.gitignore` and `git rm -r --cached` them.

---

# Appendix — cross-cutting corrections (second review, 2026-09-01)

Findings from a review of the tree after A-1…A-13 and X-1…X-5 were partly applied. Every item below
is **fixed in code** unless it says otherwise; they are recorded because this plan is the spec that
gets re-executed, and an unrecorded fix is a fix the next build undoes.

## X-6 (BLOCKER, implementation) — a nested `BeginAsync` rolls back the request and the commit then silently no-ops

`RequestTransaction.BeginAsync` returned an **owning** scope on every call, including the second
one:

```csharp
if (_transaction is null)
    _transaction = await context.Database.BeginTransactionAsync(ct);
else
    await EnlistAsync(context, ct);          // nested call

return new TransactionScope(this);           // ← owns rollback, even when nested
```

`TransactionScope.DisposeAsync()` calls `RequestTransaction.DisposeAsync()`, which rolls back and
sets `_transaction = null`. So in this shape:

```csharp
await using var scope = await _transaction.BeginAsync(_db, ct);   // outer
await _otherSliceHandler.Handle(...);       // begins, enlists, returns, disposes → ROLLBACK
await _transaction.CommitAsync(ct);         // _transaction is null → returns silently
return Ok(result);                          // 200, nothing committed, no exception
```

the inner handler's `await using` rolls the whole request back the moment it returns, and the outer
`CommitAsync` finds `_transaction` already null and does nothing. The caller gets a `200` describing
work that does not exist, and nothing is logged.

This is the exact inverse of A-4, which covers a caller who *forgets* to begin. It was latent only
because no shipped handler calls another slice's handler yet — and `Employees`' `/api/customers/onboard`
is specified as three slices in one transaction, so it is the first thing to hit it.

**Rule: only the outermost `BeginAsync` owns the transaction.** A nested call enlists and returns a
no-op scope. Record this in the transaction contract wherever `IRequestTransaction` is specified:

```csharp
if (_transaction is not null)
{
    await EnlistAsync(context, ct);
    return NoopScope.Instance;   // disposal does nothing; the outermost scope owns rollback
}

_transaction = await context.Database.BeginTransactionAsync(ct);
return new TransactionScope(this);
```

## X-7 (MAJOR, implementation) — `RequestTransaction` had an `IsRelational()` escape hatch that turned the whole guarantee off

`BeginAsync` opened with `if (!context.Database.IsRelational()) return NoopScope.Instance;` and
`EnlistAsync` had the same test. It existed so the in-memory flow tests would run, and its effect is
that **the one class the cross-slice atomicity guarantee rests on has a branch that disables it
silently**, on a condition the tests are the only known trigger for. Any future misconfiguration
that produces a non-relational context gets no transaction and no warning.

Correction, applied: the check is deleted from the API project, and the test project owns a
`NoOpRequestTransaction : IRequestTransaction` that the in-memory flow tests inject instead. The
real-PostgreSQL tests keep the real implementation.

**The general rule this is an instance of: a production class must not carry a branch whose only
purpose is to make a test pass.** Substitute at the seam — that is what the interface is for.

## X-8 (MAJOR, implementation) — the migration runner ordered scripts by full filename, so the description decided the order

`SqlMigrationRunner` ordered by `Path.GetFileName(script.Path)`. On the same date,
`Customers/20260830_001_CreateCustomersSchema.sql` therefore ran before
`Notifications/20260830_001_CreateNotificationsSchema.sql` for no better reason than `C` sorting
before `N` — and renaming a script for clarity would reorder a deployment.

Correction, applied: order by the `YYYYMMDD_###` prefix only, then by the slice-relative key as a
deterministic tie-break. The prefix is the part of the filename that means "when"; the rest is a
comment.

## X-9 (MAJOR, implementation) — `appsettings.json` shipped a database password

`appsettings.json` carried
`Host=localhost;…;Username=postgres;Password=postgres`, directly against 04-Infrastructure §4
(*"`appsettings.json` holds non-secret defaults only"*) and §2 (*"No secret is written in the file"*).
That file is copied into the image, so anyone who can pull the image has the credential — and a
default of `postgres`/`postgres` present in configuration is the kind of thing that survives to
production because it works.

Correction, applied: `ConnectionStrings:Default` in `appsettings.json` is `""`, the development
value moved to `appsettings.Development.json`, and `Program.cs` now rejects blank as well as missing
(`GetConnectionString` returns `""`, not `null`, for an empty entry, so the original
`?? throw` never fired and the app started and failed on first query instead).

Note this compounds X-4: `appsettings.Development.json` is currently copied to the build output, so
until X-4 is fixed the development credential still reaches the artifact. X-4 remains open.

## X-10 (MAJOR, implementation) — `Program.cs` still has no `UseForwardedHeaders`, recorded as A-7 and X-3.1

Now fixed, with one correction to the code 04-Infrastructure §3 shows: on .NET 10,
`ForwardedHeadersOptions.KnownNetworks` is **obsolete** (`ASPDEPR005`) and takes the deprecated
`Microsoft.AspNetCore.HttpOverrides.IPNetwork`. The current member is `KnownIPNetworks`, taking
`System.Net.IPNetwork` — and with both namespaces imported, the bare name `IPNetwork` is a CS0104
ambiguity, so it must be qualified. **04-Infrastructure §3's example does not compile as written on
this platform and should be updated** (not done here — the numbered documents were out of scope for
this pass).

Two design points worth keeping:

1. The allow-list is configuration (`ForwardedHeaders:KnownNetworks`, `ForwardedHeaders:KnownProxies`),
   not a hard-coded subnet, because the compose network's subnet is a deployment fact.
2. **Both `KnownIPNetworks` and `KnownProxies` default to loopback and must be cleared first**, and
   there is deliberately no fallback that trusts the header when the allow-list is empty. An empty
   allow-list means forwarded headers are ignored and `source_ip` records the proxy — wrong, and
   logged as a warning at startup, but not forgeable. `X-Forwarded-For` is a request header like any
   other; honouring it from an unknown sender lets a client write its own address into the audit
   log, which is the one column an attacker most wants to control.
