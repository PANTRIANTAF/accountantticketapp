# Tickets Slice — Implementation Plan

Build this **last**, after all seven other slices. It depends on every one of them, it is the only
slice that owns a state machine, and it registers another slice's HTTP routes (§0.3). Nothing here
can be built or tested in isolation.

It is also the only slice with **no `ExternalInterfaces` folder at all** — see §0.2.

Documents that govern this slice, in precedence order. Where any of them disagrees with this plan,
**they win and this plan is wrong** — fix the plan, do not code around it.

- [02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §6 (Tickets — read), §7 (Tickets —
  write), §8 (Documents), §12
- [01-DomainModel.md](../../01-DomainModel.md) §3 (Ticket, assignment rules, TicketRevision,
  FieldValue, FieldVerification, TicketMessage), §5 (the lifecycle, the transition table, and the
  correction round), §9.1, §9.3, §9.4, §9.5, §9.6, §9.7, §9.8, §9.9
- [03-SliceInventory.md](../../03-SliceInventory.md) §2, §3 rules 1–7
- [App/GeneralAppArchitecture.md](../../App/GeneralAppArchitecture.md) §5, §8
- [The Documents plan](../Documents/IMPLEMENTATION_PLAN.md) §0.2, §0.3, §5 — because this slice
  implements the authorization half of that contract

---

## 0. Prerequisites — read before writing any code

### 0.1 What this slice owns

Five entities and one join table: `Ticket`, `TicketRevision`, `FieldValue`, `FieldVerification`,
`TicketMessage`, and `ticket_message_documents`. Plus the reference-number counter (§1.7).

It owns **the state machine** — the closed transition table in
[01-DomainModel.md](../../01-DomainModel.md) §5 — and it owns every authorization rule that
mentions a ticket, including the ones about documents (§0.3).

What it does **not** own: the field *definitions* (`TicketTypes`), the document *bytes*
(`Documents`), the *people* (`Employees`, `Identity`), the *tenant* (`Customers`), and the
*notification delivery* (`Notifications`). It orchestrates all six.

### 0.2 This slice has **no `ExternalInterfaces` folder**, and that is a design signal

[03-SliceInventory.md](../../03-SliceInventory.md) §2: nothing depends on `Tickets`. It is the top
of the dependency graph, so it has no consumers, so it needs no public contract.

Consequences:

1. **Do not create `ITicketApi`.** There is no caller. An interface with no consumer is a promise
   about a boundary that nothing enforces, and the first thing that will happen is a lower slice
   being given a dependency on it — which is a cycle, because `Tickets` reaches everything.
2. **If you find yourself wanting a lower slice to read a Ticket, stop and flag it.** That is either
   a misplaced responsibility or a genuine need for an inverted interface, and dependency rule 7
   says an inverted interface must be raised, not invented. The one v1 inverted interface is
   `IRecipientDirectory`, and the second candidate was considered and rejected in
   [the Documents plan](../Documents/IMPLEMENTATION_PLAN.md) §0.2.
3. **Everything this slice exposes, it exposes over HTTP.** That is the whole surface.

### 0.3 This slice registers the `Documents` routes — decided, and it is load-bearing

[The Documents plan](../Documents/IMPLEMENTATION_PLAN.md) §0.2, decided: `Documents` has no
endpoints. `Tickets` registers `/api/documents/upload`, `/list`, `/download`, and `/delete`, does
all four authorizations, and writes all three document audit entries.

The reason, restated because it is the kind of thing that gets "tidied": a document inherits its
access rules entirely from its ticket (matrix §8) and authorization must be **re-checked at the
moment of download** — but `Documents` may depend only on `Audit`, and `Tickets → Documents`
already exists, so `Documents → Tickets` would be a cycle.

**The six steps every document handler must perform**, copied verbatim from
[the Documents plan](../Documents/IMPLEMENTATION_PLAN.md) §0.3 so the two cannot drift:

1. `RequireAsync(user, "UploadDocument" | "DownloadDocument" | "DeleteDocument")`.
2. Load the **ticket** with `.WhereInCustomerScope(user)`; not found → `404`.
3. For an `Employee` role, additionally require Creator **or** Subject (matrix §6). For a
   `CustomerAdmin`, own Customer is sufficient.
4. For a `Draft` ticket, require the caller to be the Creator — drafts are private to their Creator
   regardless of role, and **no Accountant ever sees a draft** (§9.3).
5. **Verify the document actually belongs to that ticket:** `if (doc.TicketId != ticket.Id) → 404`.

   > This is the step that gets skipped, because the ticket check passed and the document was found,
   > so both halves look verified. They are not: the caller supplied **both** ids independently.
   > Pair a ticket you may read with a document id from a ticket you may not, and without step 5 the
   > bytes are served. It is a textbook IDOR, and every test that checks "a document on my own
   > ticket downloads" passes.

6. Audit the operation.

And the two rules that are this slice's to get right:

- **`IDocumentApi` authorizes nothing.** It has no `CurrentUser`, applies no scope filter, and will
  hand any caller the bytes of any live document given only its id
  ([the Documents plan](../Documents/IMPLEMENTATION_PLAN.md) §5.0). The security of every document
  in the system rests on the six steps above.
- **`Origin` is derived from the caller's role, never from the request body** (that plan, §5.1 rule
  5). An Accountant uploading gives `AccountantResponse`; a Customer-side actor gives
  `CustomerUpload`. If it came from the body, a Customer could mark their own upload as an
  Accountant response and change what the ticket appears to say.

  > **Use `DocumentOrigin.AccountantResponse` / `.CustomerUpload`, not string literals** (amended
  > 2026-09-02). Those constants used to live in `Documents/Core`, which this slice may not read, so
  > the first build of `UploadDocumentHandler` duplicated the two literals as private consts and
  > reported it. They are now in `Documents/ExternalInterfaces/DocumentOrigin.cs` and must be used
  > directly: `StoreDocumentRequest` validates `Origin` against `DocumentOrigin.All` with an `Ordinal`
  > comparer and throws `InvalidOperationException` on a miss, so a typo in a duplicated literal is a
  > 500 on every upload from one side of the system.

### 0.4 `CustomerScope` — and the three tighter filters stacked on top of it

`Ticket` implements `ICustomerScoped`, so `.WhereInCustomerScope(user)` is the first filter on every
query. But matrix §6 is the most finely graded table in the whole application, and the scope filter
is only the outermost of **four** layers:

| Layer | What it does | Who it constrains |
|---|---|---|
| 1. `.WhereInCustomerScope(user)` | Accountants pass through; Customer-side roles limited to their Customer | `CustomerAdmin`, `Employee` |
| 2. Creator-or-Subject | Limits to tickets the person is party to | `Employee` only |
| 3. Draft privacy | A `Draft` is visible **only to its Creator**, in every role | all four roles |
| 4. Internal-note filtering | `InternalNote` messages are stripped from the conversation | `CustomerAdmin`, `Employee` |

> **Layer 3 is the one that catches people out, because it constrains Accountants too.** An
> `AccountantAdmin` — who passes layer 1 and is exempt from layer 2 — must still not see a Customer's
> `Draft`. Matrix §6: *"Drafts are private to their Creator regardless of role. **No Accountant ever
> sees drafts.**"* And §9.3 extends it to the one case the matrix did not name: a `Draft` created by
> a Customer Admin on behalf of an Employee is **not** visible to that Employee either, even though
> they are the Subject. The Subject link starts granting visibility at `Submitted`.

Build layers 1–3 as **one shared query extension in this slice**, `.WhereTicketVisible(user)`, and
call it from every read. A per-handler reimplementation of a four-layer rule is the most likely way
this application leaks a Customer's payroll data.

### 0.5 The permission checker

Every handler calls `await _permissions.RequireAsync(user, "ActionName", ct: ct)` as its **first
statement**. Absent action denies, unlisted role denies, every denial is audited before the `403`.

But this slice's matrix rows are almost entirely qualified — "own Customer", "where Creator or
Subject", "Creator only", "own drafts". **The catalogue expresses only which roles may call.** Every
qualifier is enforced in the handler by §0.4's layers and the per-handler checks. A handler whose
only authorization is `RequireAsync` is a handler that lets an `Employee` read a colleague's payroll
ticket.

### 0.6 Pagination

`Shared/Pagination/`. Default `PageSize` **15**, maximum **50**
(`App/GeneralAppArchitecture.md` §8 — system-wide; do not pick your own). Over the maximum is
**clamped with a `200`**, not rejected.

Every list endpoint in this slice is paginated — there are six (§8.1), more than any other slice.
Default sort `last_activity_at DESC, id DESC`. The `id` tiebreaker is mandatory: two tickets
touched in the same transaction share a `last_activity_at` to the microsecond, and an unstable sort
makes paging skip and repeat rows.

### 0.7 The decisions already locked elsewhere — nine of them

None of these is this plan's to revisit. [01-DomainModel.md](../../01-DomainModel.md) §9 is explicit:
*"Nothing here is open, and nothing here may be re-litigated by a slice plan — remember that a plan
under `Slices/` loses to this document, so a plan that contradicts one of these decisions is wrong,
not new."*

| # | Decision | Where it lands in this plan |
|---|---|---|
| 9.1 | **A `Closed` Ticket is never reopened.** No reopen endpoint, no `Reopened` status, no `ReopenedAt`, no `Closed → InReview` row. A continuation is a new Ticket with `PrecededByTicketId`. | §1.2, §4.2, §5 |
| 9.2 | **Nothing is hard-deleted.** No `DELETE`, no `Remove()`, no purge job. Cancellation is the only removal. | §1.8 |
| 9.3 | **A `Draft` is invisible to its Subject Employee.** | §0.4 layer 3 |
| 9.4 | **An Accountant may never edit a Field Value** — but may write Accountant-only fields. Disjoint sets. | §4.6, §6.3 |
| 9.5 | **An invited Employee gains Subject-based read access with no backfill.** | §0.4, §4.3 |
| 9.6 | **A `Departed` Employee's Tickets stay visible to their Customer Admin, permanently.** They may not be the Subject of a **new** Ticket. | §4.1, §4.3 |
| 9.7 | **Optimistic concurrency on the `tickets` row only**, hand-maintained `version INTEGER`, `409` on mismatch. | §1.1, §3.2, §5.1 |
| 9.8 | **Stranded Tickets appear in the pickup queue**; taking one is an audited **reassignment**. | §4.4, §4.8 |
| 9.9 | **Any Accountant may reassign any Ticket**, including to themselves. | §4.8 |

---

## 1. Database schema (SQL migration)

**File:** `Slices/Tickets/Infrastructure/Migrations/20260904_001_CreateTicketsSchema.sql`

Six tables. This is the largest schema in the system.

### 1.1 `tickets`

```sql
CREATE TABLE tickets (
    id                       UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Human-readable, unique, never reused, never changed. Format TKT-{year}-{000000}.
    reference                VARCHAR(20) NOT NULL,

    -- The tenant boundary (ICustomerScoped). Immutable. No FK: another slice.
    customer_id              UUID NOT NULL,

    -- Type AND the specific version. Both immutable. No FK: another slice.
    ticket_type_id           UUID NOT NULL,
    ticket_type_version_id   UUID NOT NULL,

    -- The UserAccount that created it. Immutable.
    creator_user_account_id  UUID NOT NULL,

    -- The Employee the ticket is about. Immutable. No FK: another slice.
    subject_employee_id      UUID NOT NULL,

    -- 'Draft'|'Submitted'|'InReview'|'AwaitingInformation'|'Answered'|'Closed'|'Cancelled'
    status                   VARCHAR(30) NOT NULL DEFAULT 'Draft',

    -- The Accountant responsible. NULL in Draft/Submitted/Cancelled, REQUIRED otherwise.
    assignee_user_account_id UUID NULL,

    -- 'Normal' | 'High'. Accountant-only.
    priority                 VARCHAR(10) NOT NULL DEFAULT 'Normal',
    due_date                 DATE NULL,

    -- Derived from the Type name plus the Subject, at creation, so lists read well.
    title                    VARCHAR(300) NOT NULL,

    -- The current revision. Nullable ONLY between the two inserts -- see 1.3.
    current_revision_id      UUID NULL,

    -- 01-DomainModel.md §9.1. Set at creation only, immutable thereafter.
    preceded_by_ticket_id    UUID NULL REFERENCES tickets(id),

    -- 01-DomainModel.md §9.7. Hand-incremented on EVERY write to this row.
    version                  INTEGER NOT NULL DEFAULT 1,

    created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_activity_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    closed_at                TIMESTAMPTZ NULL,

    CONSTRAINT uq_tickets_reference UNIQUE (reference),

    CONSTRAINT ck_tickets_status CHECK (status IN (
        'Draft','Submitted','InReview','AwaitingInformation','Answered','Closed','Cancelled')),

    CONSTRAINT ck_tickets_priority CHECK (priority IN ('Normal','High')),

    -- 01-DomainModel.md §3: the Assignee is ABSENT in Draft/Submitted/Cancelled and
    -- REQUIRED in InReview/AwaitingInformation/Answered/Closed.
    --
    -- ONE EXCEPTION, and it is the trap in §5 of the domain model:
    -- AwaitingInformation -> Submitted RETAINS the Assignee. So 'Submitted' can have one.
    CONSTRAINT ck_tickets_assignee CHECK (
        (status IN ('InReview','AwaitingInformation','Answered','Closed')
             AND assignee_user_account_id IS NOT NULL)
        OR
        (status IN ('Draft','Cancelled') AND assignee_user_account_id IS NULL)
        OR
        (status = 'Submitted')          -- may or may not have one. See above.
    ),

    CONSTRAINT ck_tickets_closed CHECK (
        (status = 'Closed' AND closed_at IS NOT NULL)
        OR
        (status <> 'Closed' AND closed_at IS NULL)
    ),

    CONSTRAINT ck_tickets_version CHECK (version >= 1)
);
```

> **`ck_tickets_assignee`'s third branch is the whole `AwaitingInformation → Submitted` trap
> expressed in SQL.** [01-DomainModel.md](../../01-DomainModel.md) §5: *"`AwaitingInformation` →
> `Submitted` is the one place the status name misleads. The Ticket returns to `Submitted` but keeps
> its Assignee, so it is **not** back in the unassigned pool."* A constraint written as
> `status = 'Submitted' AND assignee IS NULL` looks obviously right and rejects every correction
> round. Write the comment; the next person will try to "tighten" it.

`preceded_by_ticket_id` is the **only** foreign key on this table, and it is self-referential and
therefore intra-slice. Everything else points at another slice's table and gets no FK.

### 1.2 `preceded_by_ticket_id` — the validation belongs in the handler

The column has an FK, so the referenced ticket must exist. But §9.1 requires three things the FK
cannot express, and all three are the handler's:

| Rule | Enforcement |
|---|---|
| Must belong to **the same Customer** | Handler; `422` on mismatch |
| Must be `Closed` | Handler; `422` on any other status |
| A predecessor the caller **cannot see** is `404`, not `403` | Handler; resolve it through `.WhereTicketVisible(user)` so the miss is natural |
| Immutable after creation | No update path accepts it (§4.7 rule 2) |
| Grants **no** access to the predecessor | Nothing to build — just do not add an access rule |
| Copies **no** data | Nothing to build — the new Ticket starts empty at the Type's **current active** version |

That last row is worth a sentence, because "continuation" suggests carrying data forward: §9.1 says
*"None is copied. Field values are not carried forward; the new Ticket starts empty at its Type's
current active version."* The predecessor's Type version may be inactive by now, and the new ticket
does not inherit it.

### 1.3 `ticket_revisions`

```sql
CREATE TABLE ticket_revisions (
    id                        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_id                 UUID NOT NULL REFERENCES tickets(id),

    -- Starts at 1. Revision 1 is created together with the Ticket.
    sequence_number           INTEGER NOT NULL,

    submitted_by_user_account_id UUID NOT NULL,
    submitted_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Optional note explaining what changed, written by the submitter.
    note                      VARCHAR(2000) NULL,

    CONSTRAINT uq_ticket_revisions_sequence UNIQUE (ticket_id, sequence_number),
    CONSTRAINT ck_ticket_revisions_sequence CHECK (sequence_number >= 1)
);
```

**Append-only.** [01-DomainModel.md](../../01-DomainModel.md) §3: *"A revision, once written, is
never modified and never deleted. To see what an Employee originally claimed, you read revision
1."*

- **No `version` column.** §9.7: *"Only the `tickets` row needs this… Do not put a version column on
  an append-only table."* Same for `field_values`, `field_verifications`, and `ticket_messages`.
- `uq_ticket_revisions_sequence` is what makes two concurrent corrections impossible to interleave
  into a duplicate revision 2 — one of them gets `23505` and the handler maps it to `409` (§4.5 rule
  8).
- `tickets.current_revision_id` and `ticket_revisions.ticket_id` point at each other. That is a
  cycle in the FK graph, which is why `current_revision_id` is **nullable** and has **no** FK
  constraint: the ticket row is inserted first, then revision 1, then the ticket is updated. §4.1
  rule 6.

### 1.4 `field_values`

```sql
CREATE TABLE field_values (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_revision_id  UUID NOT NULL REFERENCES ticket_revisions(id),

    -- The FieldDescriptor key it answers. A string, not an FK: descriptors are
    -- another slice's rows, and the key is what survives a version change.
    field_key           VARCHAR(100) NOT NULL,

    -- The value, in a form that preserves the declared data type. See 1.4.1.
    value_text          TEXT NULL,
    value_number        NUMERIC(18,4) NULL,
    value_date          DATE NULL,
    value_date_to       DATE NULL,          -- DateRange only
    value_boolean       BOOLEAN NULL,
    value_document_id   UUID NULL,          -- FileUpload only. No FK: another slice.

    -- 01-DomainModel.md §3: whether this value was carried forward unchanged from the
    -- previous revision, or newly entered in this one.
    is_carried_forward  BOOLEAN NOT NULL DEFAULT FALSE,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT uq_field_values_revision_key UNIQUE (ticket_revision_id, field_key)
);
```

`uq_field_values_revision_key` is a real invariant: a revision holds **one answer per field**
([01-DomainModel.md](../../01-DomainModel.md) §3, *"One answer within one revision"*). Without it a
correction that writes a second row for the same key produces two answers and every read picks
whichever the query returns first.

#### 1.4.1 Why typed columns rather than one `TEXT`

[01-DomainModel.md](../../01-DomainModel.md) §3: *"stored in a form that **preserves the declared
data type**"*. One `TEXT` column does not preserve a type; it defers the question to every reader,
and the readers will disagree — one parses `"1.500"` as fifteen hundred and another as one and a
half.

The eleven data types in `TicketTypes.ExternalInterfaces.FieldDataTypes` map as:

> **AMENDED.** `FieldDataTypes` used to live in `TicketTypes.Core` and expose only a `HashSet<string>`
> named `All`, with no named constants. This slice has to switch on `DataType` eleven ways, and
> dependency rule 2 forbade it from importing that `Core` — so the first implementation declared its
> own eleven string literals, which nothing kept in sync with the real list. The type now lives in
> `TicketTypes.ExternalInterfaces` with a `public const string` per type and `All` built *from* those
> constants. Reference `FieldDataTypes.MoneyAmount` and never the bare string `"MoneyAmount"`.
>
> These are stored values: they are persisted in `field_descriptors.data_type` and named in a `CHECK`
> constraint, so renaming one is a migration, not a rename refactor. That is also why they are strings
> and not a C# enum.

| `DataType` | Column(s) |
|---|---|
| `SingleLineText`, `MultiLineText`, `SingleChoice` | `value_text` |
| `MultipleChoice` | `value_text`, holding a JSON array of the chosen keys |
| `WholeNumber`, `DecimalNumber`, `MoneyAmount` | `value_number` |
| `Date` | `value_date` |
| `DateRange` | `value_date` and `value_date_to` |
| `YesNo` | `value_boolean` |
| `FileUpload` | `value_document_id` |

Rules:

1. **`NUMERIC(18,4)`, never `float`/`double`/`real`.** `MoneyAmount` is money. A binary float cannot
   represent `0.10` and this is an accounting application; a rounding artefact in a tax figure is
   the worst class of bug this codebase can produce. Map it to `decimal` in C#.
2. **`MultipleChoice` stores a JSON array in `value_text`**, and it is the one place a value is not
   atomic. Validate every element against the descriptor's `ChoiceOptions` before writing.
3. **There is no `CHECK` constraint tying `DataType` to the populated column**, because this table
   does not know the data type — that lives in `TicketTypes`. The **handler** enforces it (§6.2),
   and §13 item 4 raises whether that is good enough.
4. **`value_document_id` has no FK** (`documents` is another slice) and **the document must be
   verified to belong to this ticket** before it is accepted as a field value — the same IDOR as
   §0.3 step 5, in a different disguise. §4.5 rule 6.

### 1.5 `field_verifications`

```sql
CREATE TABLE field_verifications (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Attaches to a FieldValue in a SPECIFIC revision, so the verification history
    -- of a corrected field is fully preserved. 01-DomainModel.md §3.
    field_value_id      UUID NOT NULL REFERENCES field_values(id),

    -- 'Accepted' | 'Rejected'
    outcome             VARCHAR(20) NOT NULL,

    -- Required when rejected. Shown VERBATIM to the Customer side.
    rejection_reason    VARCHAR(2000) NULL,

    verified_by_user_account_id UUID NOT NULL,
    verified_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_field_verifications_outcome CHECK (outcome IN ('Accepted','Rejected')),

    -- 01-DomainModel.md §3: "Rejection reason -- required when rejected".
    CONSTRAINT ck_field_verifications_reason CHECK (
        (outcome = 'Rejected' AND rejection_reason IS NOT NULL AND length(trim(rejection_reason)) > 0)
        OR
        (outcome = 'Accepted' AND rejection_reason IS NULL)
    )
);
```

**Append-only**, no `version`, no soft delete. A re-verification appends a new row; the latest by
`verified_at` (tie-broken by `id`) is current. Do **not** update an existing row — the verification
history is the point.

> `ck_field_verifications_reason` enforces a normative rule at the database level because a rejected
> field with no reason is useless to the Customer side, and the reason is *"shown verbatim"* so an
> empty string is as bad as a null. `length(trim(...)) > 0` catches the whitespace case that a
> `NOT NULL` alone does not.

### 1.6 `ticket_messages` and `ticket_message_documents`

```sql
CREATE TABLE ticket_messages (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_id           UUID NOT NULL REFERENCES tickets(id),

    -- NULL for SystemEvent: written by the application, not a person.
    author_user_account_id UUID NULL,

    -- 'CustomerMessage'|'AccountantResponse'|'InternalNote'|'SystemEvent'
    kind                VARCHAR(30) NOT NULL,

    body                TEXT NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_ticket_messages_kind CHECK (kind IN (
        'CustomerMessage','AccountantResponse','InternalNote','SystemEvent')),

    -- A SystemEvent has no human author; everything else must have one.
    CONSTRAINT ck_ticket_messages_author CHECK (
        (kind = 'SystemEvent' AND author_user_account_id IS NULL)
        OR
        (kind <> 'SystemEvent' AND author_user_account_id IS NOT NULL)
    )
);

-- 01-DomainModel.md §3: a TicketMessage has "Attached Documents".
CREATE TABLE ticket_message_documents (
    ticket_message_id   UUID NOT NULL REFERENCES ticket_messages(id),
    document_id         UUID NOT NULL,      -- No FK: another slice.
    PRIMARY KEY (ticket_message_id, document_id)
);
```

**Append-only.** [01-DomainModel.md](../../01-DomainModel.md) §3: *"Messages are append-only. They
are not edited or deleted."* No `edited_at`, no `deleted_at`, no update handler.

`ticket_message_documents` exists because a document is attached to a **ticket** in the `Documents`
schema (its `ticket_id` column) but to a **message** in the conversation. Both are true: the
document belongs to the ticket for authorization, and to a message for rendering. The join table is
the message link; `documents.ticket_id` remains the authorization anchor, and §0.3 step 5 still
checks it.

### 1.7 The ticket reference — `TKT-{year}-{000000}`

[01-DomainModel.md](../../01-DomainModel.md) §3: *"human-readable, unique, generated on creation,
**never reused and never changed**. Format `TKT-{year}-{zero-padded sequence}`, e.g.
`TKT-2026-000417`."*

The sequence restarts each year, which rules out a plain PostgreSQL `SEQUENCE`. Use a counter table
and one atomic upsert:

```sql
CREATE TABLE ticket_reference_counters (
    year           INTEGER PRIMARY KEY,
    last_sequence  INTEGER NOT NULL
);
```

```sql
-- Atomic: allocates and returns in one statement, under any concurrency.
INSERT INTO ticket_reference_counters (year, last_sequence)
VALUES (@year, 1)
ON CONFLICT (year) DO UPDATE
    SET last_sequence = ticket_reference_counters.last_sequence + 1
RETURNING last_sequence;
```

Rules:

1. **Use exactly this statement**, via `FromSqlRaw`/`ExecuteSqlRaw` with a scalar read. It is atomic
   under concurrency because the `ON CONFLICT DO UPDATE` takes a row lock and the `RETURNING` reads
   the value it just wrote.
2. **Do not read-then-increment.** `SELECT last_sequence` followed by `UPDATE` is a lost-update race
   that produces two tickets with one reference — and `uq_tickets_reference` then rejects the second
   with a `500` at the worst possible moment.
3. **Do not use `COUNT(*) FROM tickets WHERE ...`.** It is a race *and* it reuses numbers after a
   cancellation, and the reference must never be reused.
4. **Zero-pad to six digits**: `$"TKT-{year}-{seq:D6}"`. The example in the domain model is
   `TKT-2026-000417`, which is six.
5. **The allocation happens inside the creation transaction, and it rolls back with it.**
   `ticket_reference_counters` is an ordinary table, not a PostgreSQL `SEQUENCE`, and that difference
   is the whole behaviour: `nextval()` is deliberately non-transactional and would leave a gap, but an
   `UPDATE` to a row is undone by `ROLLBACK` like any other write. So a rolled-back creation **does**
   release the number and the next caller receives it.

   That is correct, and it does not weaken "a reference is never reused": the rolled-back ticket was
   never persisted, so no ticket ever bore that reference. The invariant is about *committed* tickets —
   no two of them share a reference, and a reference is never re-assigned away from a ticket that
   exists. Handing an unused number to the next caller satisfies both.

   > **AMENDED.** This rule previously said the opposite — that a rollback consumes the number and
   > leaves a gap. That was wrong about PostgreSQL, and it mattered: the acceptance criterion derived
   > from it asserted a gap, so an implementation that behaved *correctly* would have failed the
   > suite. §12 constraint 5 and the §11.4 test matrix row are corrected to match.

   One real consequence to design around: `ON CONFLICT DO UPDATE` takes a row lock on the counter row
   and PostgreSQL holds row locks until end of transaction, not until end of statement. Every
   concurrent creation for the same year therefore serialises on that one row for the remainder of
   each creation transaction. **Allocate as late as possible** — after validation, after the Ticket
   Type version has been resolved, immediately before the `INSERT INTO tickets` — so the lock is held
   for the shortest possible window. Correctness does not depend on this; throughput does.
6. **The year comes from the application clock, once**, and is passed in. Calling `NOW()` inside the
   statement and `DateTime.Now` in the C# string produces a mismatched reference on New Year's Eve.
7. **A `Draft` gets a reference immediately**, at creation. It is how the Creator refers to it.

### 1.8 Indexes

```sql
-- The pickup queue, condition 1: Submitted with NO assignee. 01-DomainModel.md §9.8.
CREATE INDEX idx_tickets_pickup
    ON tickets (last_activity_at)
    WHERE status = 'Submitted' AND assignee_user_account_id IS NULL;

-- The pickup queue, condition 2, and "assigned to me": by assignee, open statuses only.
CREATE INDEX idx_tickets_assignee_open
    ON tickets (assignee_user_account_id, last_activity_at)
    WHERE status IN ('Submitted','InReview','AwaitingInformation','Answered');

-- Every Customer-side list, in the default sort order.
CREATE INDEX idx_tickets_customer_activity
    ON tickets (customer_id, last_activity_at DESC, id DESC);

-- An Employee's own tickets: Creator or Subject. Two indexes, because it is an OR.
CREATE INDEX idx_tickets_creator  ON tickets (creator_user_account_id, last_activity_at DESC);
CREATE INDEX idx_tickets_subject  ON tickets (subject_employee_id, last_activity_at DESC);

-- Lookup by the human-readable reference (the search box).
-- Already covered by uq_tickets_reference; no second index.

CREATE INDEX idx_ticket_revisions_ticket  ON ticket_revisions (ticket_id, sequence_number);
CREATE INDEX idx_field_values_revision    ON field_values (ticket_revision_id);
CREATE INDEX idx_field_verifications_value ON field_verifications (field_value_id, verified_at);
CREATE INDEX idx_ticket_messages_ticket   ON ticket_messages (ticket_id, created_at);
```

Two notes:

- **`idx_tickets_creator` and `idx_tickets_subject` are two indexes for one `OR`.** PostgreSQL can
  combine them with a bitmap `OR`, but a query written as
  `WHERE creator = @x OR subject = @y` on a large table may still scan. If the plan is bad, rewrite
  it as a `UNION` of two indexed queries rather than adding a composite index that cannot serve an
  `OR`. Measure before optimising.
- **`idx_tickets_pickup`'s predicate must match §4.4's query exactly**, including the
  `assignee IS NULL`. A partial index whose predicate is narrower than the query is unusable, and
  the pickup queue is the hottest query the Office runs.

### 1.9 No deletes

Matrix §7: *"Delete a ticket — **Nobody.** Cancellation is the only removal."* §9.2: nothing is
hard-deleted, and **`Document` is the only entity with a soft delete** — so no table in this slice
gets a `deleted_at`.

- No `DELETE` statement, no `Remove()`, no soft-delete flag on any of the six tables.
- **`Cancelled` is a status, not a delete.** A cancelled ticket stays readable, stays in lists (with
  a filter available), and keeps its revisions, messages, and documents.
- No purge job, no `IHostedService` in this slice (§9.2 rule 2 — the one hosted service in the
  system is the `Notifications` email drainer).

---

## 2. EF Core entities and DbContext

### 2.0 Column naming — mandatory

Entities PascalCase, columns snake_case, **no automatic conversion configured**. Every property
needs an explicit `HasColumnName`, or one code path fails at runtime with
`42703: column t.CustomerId does not exist`. With six entities and roughly ninety columns this is
the slice where one will be missed; a startup model-validation test that reflects over every mapped
property and asserts a non-default column name is worth writing.

### 2.1 `Core/Ticket.cs`

```csharp
public sealed class Ticket : ICustomerScoped
{
    public Guid Id { get; set; }
    public string Reference { get; set; } = string.Empty;

    public Guid CustomerId { get; set; }
    public Guid TicketTypeId { get; set; }
    public Guid TicketTypeVersionId { get; set; }
    public Guid CreatorUserAccountId { get; set; }
    public Guid SubjectEmployeeId { get; set; }

    public string Status { get; set; } = TicketStatus.Draft;
    public Guid? AssigneeUserAccountId { get; set; }

    public string Priority { get; set; } = TicketPriority.Normal;
    public DateOnly? DueDate { get; set; }

    public string Title { get; set; } = string.Empty;
    public Guid? CurrentRevisionId { get; set; }
    public Guid? PrecededByTicketId { get; set; }

    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsTerminal => Status is TicketStatus.Closed or TicketStatus.Cancelled;
    public bool IsOpen => !IsTerminal;
    public bool FieldsEditable => Status is TicketStatus.Draft or TicketStatus.AwaitingInformation;
}

public static class TicketStatus
{
    public const string Draft               = "Draft";
    public const string Submitted            = "Submitted";
    public const string InReview             = "InReview";
    public const string AwaitingInformation  = "AwaitingInformation";
    public const string Answered             = "Answered";
    public const string Closed               = "Closed";
    public const string Cancelled            = "Cancelled";

    /// <summary>Not Closed, not Cancelled. Used by the pickup queue (§9.8 condition 2).</summary>
    public static readonly IReadOnlySet<string> Open = new HashSet<string>(StringComparer.Ordinal)
        { Submitted, InReview, AwaitingInformation, Answered };
}

public static class TicketPriority
{
    public const string Normal = "Normal";
    public const string High   = "High";
}
```

Notes:

- **`FieldsEditable` encodes the rule once.** [01-DomainModel.md](../../01-DomainModel.md) §5:
  *"Field values are editable only in `Draft` and `AwaitingInformation`. In every other status the
  current revision is frozen."* Every handler that touches a field value consults this property, not
  its own status list.
- **There is no `Reopened` status, no `ReopenedAt`** (§9.1, LOCKED).
- **No navigation properties to other slices' entities** — not `Customer`, not `Employee`, not
  `TicketTypeVersion`, not `Document`. Dependency rule 3.
- **Navigation properties *within* the slice are fine** and useful (`Ticket.Revisions`,
  `Revision.FieldValues`), but see §2.4 rule 3 about `Include` on the list path.

### 2.2 The other five entities

`TicketRevision`, `FieldValue`, `FieldVerification`, `TicketMessage`, `TicketMessageDocument` —
straightforward mappings of §1.3–§1.6, with constants classes:

```csharp
public static class VerificationOutcome
{
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
}

public static class TicketMessageKind
{
    public const string CustomerMessage    = "CustomerMessage";
    public const string AccountantResponse = "AccountantResponse";
    public const string InternalNote       = "InternalNote";
    public const string SystemEvent        = "SystemEvent";

    /// <summary>Kinds a Customer-side caller may see. InternalNote is absent BY DESIGN.</summary>
    public static readonly IReadOnlySet<string> CustomerVisible = new HashSet<string>(StringComparer.Ordinal)
        { CustomerMessage, AccountantResponse, SystemEvent };
}
```

> **`CustomerVisible` is an allow-list, not `All.Except(InternalNote)`.** A fifth message kind added
> later is then invisible to the Customer side until somebody deliberately adds it — which is the
> safe default. A block-list makes the new kind visible immediately, and matrix §6 makes internal
> notes *"the Office's private channel"*. Deny by default.

**None of the five gets a `version` column or property** (§9.7 — append-only tables have nothing to
conflict on).

### 2.3 `Infrastructure/TicketsDbContext.cs`

```csharp
public sealed class TicketsDbContext : DbContext
{
    public TicketsDbContext(DbContextOptions<TicketsDbContext> options) : base(options) { }

    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketRevision> TicketRevisions => Set<TicketRevision>();
    public DbSet<FieldValue> FieldValues => Set<FieldValue>();
    public DbSet<FieldVerification> FieldVerifications => Set<FieldVerification>();
    public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();
    public DbSet<TicketMessageDocument> TicketMessageDocuments => Set<TicketMessageDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TicketConfiguration());
        // … the other five …
    }
}
```

1. **The `DbContextOptions<TicketsDbContext>` constructor is required.** §7 rule 1.
2. **Never `AddScoped<TicketsDbContext>()`.**
3. It maps exactly the six entities this slice owns. A `DbSet<Document>` or
   `DbSet<TicketTypeVersion>` here means two slices own one table.

### 2.4 No global query filters — and why not, for each temptation

1. **No Customer-scope filter.** Accountants are unscoped, so a filter would need the caller's role,
   which means an EF filter reading a scoped service. Explicit `.WhereTicketVisible(user)` is
   greppable; a missing global filter is nothing. Same reasoning as every other slice.
2. **No `Draft`-excluding filter.** The Creator must see their own drafts, and the create/submit/
   cancel handlers must find their own targets. Layer 3 of §0.4 belongs in the visibility extension,
   where it can see who the caller is.
3. **No `Cancelled`-excluding filter.** A cancelled ticket stays readable (§1.9), and the list
   endpoints offer a status filter instead.
4. **No `InternalNote`-excluding filter on `TicketMessage`.** It is tempting — matrix §6 requires
   the exclusion *"enforced on the server by filtering, not by the React app choosing not to display
   them"* — but a global filter cannot see the caller's role, so it would hide internal notes from
   Accountants too, i.e. from the only people they exist for. §4.10 rule 2 does it in the query.

---

## 3. Shared query and concurrency helpers

Two files, because both rules are needed by nearly every handler and neither may be reimplemented.

### 3.1 `Application/TicketVisibility.cs`

```csharp
public static IQueryable<Ticket> WhereTicketVisible(this IQueryable<Ticket> query, CurrentUser user)
{
    // Layer 1: the Customer boundary. Accountants pass through.
    query = query.WhereInCustomerScope(user);

    // Layer 3: a Draft is visible ONLY to its Creator, in EVERY role. §9.3, matrix §6.
    var callerAccountId = ParseAccountId(user.Id);
    query = query.Where(t => t.Status != TicketStatus.Draft
                          || t.CreatorUserAccountId == callerAccountId);

    // Layer 2: an Employee sees only tickets where they are Creator or Subject.
    if (user.Role == UserRole.Employee)
    {
        var employeeId = ResolveCallerEmployeeId();   // see rule 3
        query = query.Where(t => t.CreatorUserAccountId == callerAccountId
                              || t.SubjectEmployeeId == employeeId);
    }

    return query;
}
```

Rules:

1. **Every read of a ticket goes through this**, without exception. A handler that writes
   `.WhereInCustomerScope(user)` alone has skipped layers 2 and 3.
2. **Layer 3 applies to Accountants.** It is outside the `if`. Matrix §6: *"No Accountant ever sees
   drafts."* Putting it inside the Employee branch is the single most likely error in this file, and
   it silently exposes every Customer's half-finished drafts — containing payroll data — to the whole
   Office. **A test asserts an `AccountantAdmin` gets `404` on a `Draft` (§11.2).**
3. **The caller's Employee id is not on `CurrentUser`.** `CurrentUser` carries `Id` (account),
   `Role`, and `CustomerId` — not an Employee id. So layer 2 needs
   `IEmployeeApi.FindByAccountAsync(callerAccountId)` **before** building the query, and the id is
   passed in. That makes this an `async` two-step, not a pure extension method:

   ```csharp
   // The real shape: resolve first, then filter.
   Guid? employeeId = user.Role == UserRole.Employee
       ? (await _employees.FindByAccountAsync(callerAccountId, ct))?.Id
       : null;
   var query = _db.Tickets.WhereTicketVisible(user, employeeId);
   ```

   **An `Employee` role with no Employee record is a broken state, not a permissive one** — return an
   empty result (or throw `401`), never an unfiltered query. Pick one and state it; §13 item 6.
4. **`user.Id` is parsed to a `Guid` once, outside the query.** A `.ToString()` inside LINQ either
   fails to translate or defeats the index, and a `"D"`-vs-`"N"` format mismatch silently matches
   nothing — the same trap as [the Employees plan](../Employees/IMPLEMENTATION_PLAN.md) §5.2 rule 3.
5. **`CustomerAdmin` gets no layer-2 filter, deliberately.** Matrix §6 gives them *"all of them"*
   within their Customer, and the note beneath the table is emphatic: *"The Customer Admin's full
   visibility within their Customer is a **deliberate, accepted decision**, including tickets
   containing payroll and personal tax data. Do not add confidentiality flags or narrow this without
   an explicit instruction."*

### 3.2 `Application/TicketConcurrency.cs`

§9.7, LOCKED. One helper, used by every handler that writes the `tickets` row:

```csharp
public static void RequireVersion(Ticket ticket, int expectedVersion)
{
    if (ticket.Version != expectedVersion)
        throw new AppException(
            "This ticket was changed by someone else. Reload and try again.", 409);
}

public static void Touch(Ticket ticket, DateTimeOffset now)
{
    ticket.Version += 1;
    ticket.LastActivityAt = now;
}
```

Rules:

1. **Every mutating ticket request carries the `Version` the client read**, in its DTO. §9.7.
2. **`409` on mismatch — not `500`, not `422`.** §9.7 says so explicitly. The client re-reads and
   retries.
3. **`Touch` is called on every write to the `tickets` row** — status transition, assignment,
   reassignment, priority, due date. If a handler modifies the ticket row without calling it, two
   concurrent writers both succeed and one silently overwrites the other.
4. **Hand-maintained `integer`, not `UseXminAsConcurrencyToken()`.** §9.7: *"an opaque
   provider-specific token that the SPA has to round-trip does not belong in the contract."* If
   `xmin` appears in a configuration, it is wrong.
5. **Do not put `RequireVersion` on the append-only tables.** Posting a message or adding a
   verification does not conflict — those writes interleave. But **if the same handler also writes
   the ticket row** (e.g. rejecting a field *and* moving to `AwaitingInformation`), the version check
   applies to that part.
6. **Check the version *after* loading and *before* any other work**, so a `409` costs nothing and
   cannot half-apply.

---

## 4. Handlers

`Application/Handlers/`, one file each, `AddTransient`. This is the largest handler set in the
system.

### 4.0 Rules that apply to every handler here

**A. The canonical signature:** `public async Task<TResponse> Handle(TRequest req, CurrentUser user,
CancellationToken ct)`.

**B. The order is fixed:** `RequireAsync` → resolve the caller's Employee id if needed → load
through `.WhereTicketVisible(user)` (`404` on miss) → `RequireVersion` → the per-handler
authorization qualifiers → the transition legality check → the work.

**C. One transaction per mutating handler.** `BeginAsync(_db, ct)` … `CommitAsync(ct)`. `AuditApi`
enlists itself. Disposal without a commit rolls back.

> **D. Reads open no transaction — except a document download**, which is audited and therefore
> behaves like a mutation (`App/GeneralAppArchitecture.md` §5 rule 4). And it must **commit before
> streaming the bytes**: once the body has started the status is sent, and
> `RequestTransaction.DisposeAsync()` rolls back, so a commit attempted after streaming discards the
> audit entry **while the caller receives the file**. A downloaded document with no audit row is
> exactly what [01-DomainModel.md](../../01-DomainModel.md) §6 exists to prevent.

**E. Every status transition does four things, together, in one transaction:**

1. Validates the transition against the closed table (§5)
2. Writes the new status and calls `Touch`
3. Writes a **`SystemEvent` `TicketMessage`** — [01-DomainModel.md](../../01-DomainModel.md) §5:
   *"Every transition writes a `SystemEvent` TicketMessage and an Audit Entry."*
4. Writes the **Audit** entry, and queues any **Notification**

> Put this in one shared helper, `TicketTransitions.Apply(...)`, and call it from all nine
> transition paths. Four things that must always happen together, spread across nine handlers, is
> thirty-six chances to forget one — and the one that gets forgotten is the `SystemEvent`, because
> nothing breaks without it.

**F. Audit codes — all of them already exist** in `Slices/Audit/ExternalInterfaces/AuditActions.cs`.
Verified against the shipped file; nothing needs adding, unlike `Employees`:

| Operation | Code |
|---|---|
| Create (draft or direct submit) | `TicketCreated` |
| Any status change | `TicketStatusChanged` |
| Pickup / assign | `TicketAssigned` |
| Reassign (incl. taking a stranded ticket) | `TicketReassigned` |
| Cancel | `TicketCancelled` |
| Close | `TicketClosed` |
| Correction revision | `RevisionSubmitted` |
| Field accepted | `FieldVerified` |
| Field rejected | `FieldRejected` |
| Message posted (any kind) | `MessagePosted` |
| Priority set | `PriorityChanged` |
| Due date set | `DueDateChanged` |
| Document upload / download / soft delete | `DocumentUploaded` / `DocumentDownloaded` / `DocumentSoftDeleted` |
| Every denial | `PermissionDenied`, by `PermissionChecker` |

`AuditApi` validates the code against `AuditActions.All` and **throws on an unknown one**, so a
typo'd string fails the whole operation at runtime. Use the constants.

**G. Notification kinds — all of them already exist** in `NotificationEvents`
([the Notifications plan](../Notifications/IMPLEMENTATION_PLAN.md)). Verified. The mapping:

| Event | Kind | Recipients | Emailed? |
|---|---|---|---|
| Submitted to the Office | `TicketSubmitted` | the Office | no |
| Picked up | `TicketPickedUp` | Customer side | no |
| → `AwaitingInformation` | `InformationRequested` | Creator, and Subject if they have an account | **yes** |
| A field rejected | `FieldRejected` | same | **yes** |
| → `Answered` | `TicketAnswered` | same | **yes** |
| → `Closed` | `TicketClosed` | same | **yes** |
| Cancelled | `TicketCancelled` | the other side | no |
| Accountant posted a message | `AccountantResponded` | Customer side | no |
| Correction submitted | `CorrectionSubmitted` | the Assignee | no |
| Customer replied | `CustomerReplied` | the Assignee | no |
| Assigned to somebody else | `TicketAssignedToYou` | the new Assignee | no |

**H. The accountless-Subject rule.** [01-DomainModel.md](../../01-DomainModel.md) §7: *"An
accountless Employee has no UserAccount and therefore receives no notifications. When a Ticket's
Subject is accountless, notifications about it go to the Creator. This is a real consequence of the
accountless model and **must be handled explicitly rather than producing an orphaned
notification**."*

So every Customer-side notification resolves recipients as: **the Creator, plus the Subject's
account if the Subject has one and it is not the Creator.** Never a `notification` row with a null
recipient.

**I. An `InternalNote` is never a notification trigger.** Posting one notifies nobody on the Customer
side — it is the Office's private channel (matrix §6). §4.10 rule 5.

**J. No handler in this slice writes to another slice's table.** Not `documents`, not `employees`,
not `ticket_type_versions`. Every cross-slice read and write goes through the callee's
`ExternalInterface`.

### 4.1 `CreateTicketHandler` — all four roles

`RequireAsync(user, "CreateTicket")`. Creates a `Draft`, or submits directly.

```
authorize

# 1. Resolve and validate the Subject.
subject = await _employees.FindAsync(req.SubjectEmployeeId, ct)     ?? 404
if subject.CustomerId != resolvedCustomerId                          → 404   (rule 3)
if !subject.IsActive                                                 → 422   (rule 4)

# 2. The on-behalf-of rule. Matrix §7.
if user.Role == Employee && subject is not the caller                → 403   (rule 5)

# 3. The Type version. GetTicketTypeAsync returns the type's CURRENT ACTIVE version, already
#    stripped for user.Role, and null if the type is inactive or not in the caller's audience --
#    which is exactly the 422. Store version.VersionId (the VERSION's Guid) in
#    tickets.ticket_type_version_id; NOT version.Id, which is the ticket TYPE's id.
version = await _ticketTypes.GetTicketTypeAsync(req.TicketTypeId, user.Role, ct) ?? 422

# 4. The predecessor link, if any. §9.1 / §1.2.
if req.PrecededByTicketId is not null:
    pred = load through .WhereTicketVisible(user)                    ?? 404
    if pred.CustomerId != resolvedCustomerId                          → 422
    if pred.Status != Closed                                          → 422

begin transaction
reference = allocate(year)                                           # §1.7
ticket = new Ticket { …, Status = Draft, Version = 1,
                      Title = $"{version.TypeName} — {subject.FullName}" }
_db.Tickets.Add(ticket); await _db.SaveChangesAsync(ct)

revision = new TicketRevision { TicketId = ticket.Id, SequenceNumber = 1, … }
write the field values supplied (§6)
ticket.CurrentRevisionId = revision.Id                               # §1.3
await _db.SaveChangesAsync(ct)

audit TicketCreated
if req.SubmitImmediately: apply the Draft → Submitted transition (§4.2)
commit
```

Rules:

1. **The Customer is resolved, never trusted.** For a Customer-side caller it is `user.CustomerId`.
   For an Accountant it is the **Subject's** Customer, read from `IEmployeeApi` — an Accountant
   opening a ticket on behalf of an Employee does not supply a Customer id, because the Employee
   already determines it. Two sources for one value is two chances to disagree.
2. **Customer, Type, Type version, Creator, Subject, and Preceded-by are immutable after creation**
   ([01-DomainModel.md](../../01-DomainModel.md) §3). No update handler accepts any of them (§4.7
   rule 2). *"If any of them is wrong, the Ticket is cancelled and a new one is opened."*
3. **A Subject at another Customer is `404`, not `403`.** The scope rule; a `403` confirms the
   Employee exists.
4. **A `Departed` Employee may not be the Subject of a new Ticket** — §9.6 rule 3, enforced by
   `IEmployeeApi.IsActiveAsync`/`subject.IsActive`. `422`. **Existing tickets are untouched**, and
   this check must not appear on any read or update path.
5. **An `Employee` cannot open a ticket on behalf of anyone, including a colleague.** Matrix §7, and
   the note beneath it states it twice. The check is `subject.Id == callerEmployeeId`, resolved from
   `IEmployeeApi.FindByAccountAsync` — not `subject.UserAccountId == user.Id`, which is also true but
   compares the wrong pair when the Employee has no account.
6. **`current_revision_id` is set in a second `SaveChangesAsync`**, because the two tables reference
   each other (§1.3). Both are in one transaction, so a failure leaves neither.
7. **The Type version is the *current active* one**, resolved at creation and then frozen on the
   ticket. A later version change does not affect an existing ticket — that is the entire reason
   `ticket_type_version_id` is stored rather than just `ticket_type_id`.
8. **An inactive or unknown Type is `422`.** A Customer-side caller must not be able to open a ticket
   on a deactivated type.
9. **`Title` is derived, not supplied.** [01-DomainModel.md](../../01-DomainModel.md) §3: *"derived
   from the Ticket Type name plus the Subject, so lists are readable without opening each ticket."*
   The DTO has no `Title` property. It is computed once at creation and not recomputed if the
   Employee is later renamed — worth a note in §13 (item 7).
10. **A `Draft` has no Assignee**, enforced by `ck_tickets_assignee`.
11. **Validate every field value against the descriptor** before writing (§6), and reject
    Accountant-only fields from a Customer-side caller (§6.3).

### 4.2 `SubmitTicketHandler` — all four roles, with different reach

`RequireAsync(user, "SubmitTicket")`. `Draft → Submitted`, or `AwaitingInformation → Submitted`.

Matrix §7 row "Submit a ticket": AA/AU **Creator only**; CA **Creator, or any ticket of own
Customer**; EMP **Creator only**.

1. **The two source statuses are different operations wearing one name**, and they differ in the one
   way that matters:

   | From | Assignee | New revision? |
   |---|---|---|
   | `Draft` | stays null — goes into the **unassigned pool** | no, revision 1 already exists |
   | `AwaitingInformation` | **retained** — does *not* return to the pool | usually yes (§4.5) |

   [01-DomainModel.md](../../01-DomainModel.md) §5: *"`AwaitingInformation` → `Submitted` … keeps its
   Assignee, so it is **not** back in the unassigned pool."* Clearing the Assignee here is the bug
   that sends every correction back to the queue and loses the person who asked the question.

2. **All required *visible* fields must be valid.** The transition table's condition is *"All
   required visible fields valid"*, and "visible" means two things at once (§6.4): visible to the
   Customer (`IsVisibleToCustomer`) **and** not hidden by conditional visibility
   (`ConditionalVisibilityFieldKey`/`Value`). A required field hidden by a condition is **not**
   required. Getting this wrong makes submission impossible for a whole ticket type, with a
   `422` naming a field the user cannot see.
3. **Accountant-only fields are never required for a Customer-side submission** — they are the
   Accountant's to fill (§9.4). Exclude them from the check.
4. **A `CustomerAdmin` may submit any ticket of their own Customer**, including one an Employee
   drafted. That is the one place the Creator restriction is relaxed, and it is in the matrix.
   But layer 3 of §0.4 means the Admin **cannot see** another person's `Draft` — so in practice this
   applies to `AwaitingInformation`. **Flag the tension** (§13 item 2): the matrix grants a
   submission right over a ticket the visibility rule hides.
5. Transition via §4.0 E. Notify `TicketSubmitted` to the Office on the `Draft` path,
   `CorrectionSubmitted` to the Assignee on the `AwaitingInformation` path.
6. **`RequireVersion` first.** Two people submitting the same draft is a `409` for one of them.

### 4.3 `ListTicketsHandler` and `GetTicketHandler`

`RequireAsync(user, "ListTickets")` / `"ViewTicket"` — all four roles.

Matrix §6 is the specification. `.WhereTicketVisible(user)` implements layers 1–3; these handlers
add the filters and the projections.

1. **Six list shapes, one handler with filters — or six handlers?** One handler with a filter DTO
   (`Scope: All | Unassigned | AssignedToMe | MyCustomer | Mine`), because they differ only in a
   `Where`. But **each scope value must be authorized separately**: `All` and `Unassigned` are
   Accountant-only (matrix §6 rows 1–2), and a `CustomerAdmin` passing `Scope = All` must get
   `403`, **not** a silently narrowed result. A scope that quietly means something else for one
   role is how a `CustomerAdmin` comes to believe they have cross-Customer visibility — the same
   rule as [the Employees plan](../Employees/IMPLEMENTATION_PLAN.md) §4.3 rule 2.
2. **`Draft` tickets appear only in the caller's own list**, guaranteed by layer 3 rather than by a
   filter.
3. **Resolve Customer names, Subject names, and Assignee names in bulk**, one call each to
   `ICustomerApi.FindManyAsync`, `IEmployeeApi.FindManyAsync`, and `IIdentityApi.FindManyAsync`,
   after the page is materialised. At a 50-row page a per-row lookup is 150 extra queries.
4. **`GetTicketHandler` returns the ticket, its current revision's field values with their
   verifications, and the conversation** — and the conversation is **filtered by role** (§4.10 rule
   2). Do not return internal notes and let the SPA hide them; matrix §6 requires server-side
   filtering.
5. **Accountant-only field values are stripped for a Customer-side caller.** §6.3: *"The Customer
   side never sees it, let alone writes it."* Two projections behind one `if`, each selecting only
   its own fields — not one wide projection with fields removed afterwards.
6. **A `Departed` Subject's tickets stay visible to their Customer Admin, permanently** (§9.6 rule
   1). There is no status filter on the Subject anywhere in a read path.
7. **An invited Employee sees their pre-existing non-`Draft` tickets immediately**, computed from
   `subject_employee_id` at query time (§9.5). **No backfill, no `UPDATE`.** If a migration or a
   handler stamps an account onto old tickets, the model has been misunderstood.
8. **No audit entry.** Reads are not audited here — only document downloads are (§4.0 D).

### 4.4 `ListPickupQueueHandler` — Accountants only

`RequireAsync(user, "ListPickupQueue")` — `AA`, `AU`. **This is the query the Office lives in, and
§9.8 makes it the one most likely to be built wrong.**

```csharp
// Condition 1: Submitted AND no Assignee.
//   NOT "status == Submitted" -- AwaitingInformation → Submitted retains the Assignee.
var unassigned = _db.Tickets.Where(t =>
    t.Status == TicketStatus.Submitted && t.AssigneeUserAccountId == null);

// Condition 2: any OPEN status AND the Assignee's account is not Active.
//   This is the ONLY thing that surfaces work stranded by a suspension.
var assigneeIds = await _db.Tickets
    .Where(t => TicketStatus.Open.Contains(t.Status) && t.AssigneeUserAccountId != null)
    .Select(t => t.AssigneeUserAccountId!.Value).Distinct().ToListAsync(ct);

var accounts  = await _identity.FindManyAsync(assigneeIds, ct);
var strandedAssignees = assigneeIds.Where(id =>
    !accounts.TryGetValue(id, out var a) || !a.IsActive).ToList();

var stranded = _db.Tickets.Where(t =>
    TicketStatus.Open.Contains(t.Status)
    && t.AssigneeUserAccountId != null
    && strandedAssignees.Contains(t.AssigneeUserAccountId.Value));

// The queue is the union.
```

Rules:

1. **Two conditions, and neither is "status equals `Submitted`" on its own.**
   [01-DomainModel.md](../../01-DomainModel.md) §5: *"Filtering on status alone is the most likely
   bug in this state machine."* §9.8 calls condition 2 *"the second half of that trap."*
2. **Condition 1 must test the Assignee.** Without `AssigneeUserAccountId == null`, every ticket in
   a correction round reappears in the shared pool while its Assignee is still on it.
3. **Condition 2 needs `Identity`.** That is what the `Tickets → Identity` edge exists for
   ([03-SliceInventory.md](../../03-SliceInventory.md) §2, stated explicitly: *"account status and
   Accountant display names only"*). It never reads the `user_accounts` table.
4. **An unknown account counts as not `Active`** — `!TryGetValue || !IsActive`. Fail toward
   surfacing the work; a ticket assigned to an account that no longer resolves is exactly the
   stranded case.
5. **Both queries are paginated together**, which means the union must happen in SQL, not in memory
   after two `ToListAsync` calls. Build one `IQueryable` with an `||`, or use `Union`, then paginate
   — otherwise the page size is wrong and the sort is wrong.
6. **Nothing happens automatically.** §9.8 rule 4: *"Suspension does not clear an Assignee, does not
   change a status, and does not reassign anything."* This handler is a **read**. It opens no
   transaction and writes nothing.
7. **Any Accountant may take a ticket surfaced by condition 2**, not only an `AccountantAdmin` — and
   taking one is an audited **reassignment** (§4.8 rule 4).

### 4.5 `SubmitRevisionHandler` — the correction round

`RequireAsync(user, "SubmitRevision")`. Matrix §7: AA/AU yes, CA own Customer, EMP where Creator or
Subject.

[01-DomainModel.md](../../01-DomainModel.md) §5's "correction round, end to end" is the
specification. Step 4 and step 5 are the hard part:

```
authorize; load through .WhereTicketVisible(user); RequireVersion
if !ticket.FieldsEditable                 → 422   (Draft or AwaitingInformation only)

begin transaction
prev = current revision (with its field values and their latest verifications)
next = new TicketRevision { SequenceNumber = prev.SequenceNumber + 1, Note = req.Note }

for each field in the Type version's descriptors:
    if req supplies a new value:
        write FieldValue { …, IsCarriedForward = false }
    else:
        write FieldValue { …copied from prev…, IsCarriedForward = true }
        # AND carry the verification forward -- see rule 4
        if prev value was Accepted:
            write FieldVerification { FieldValueId = new value's id,
                                      Outcome = Accepted,
                                      VerifiedBy = the ORIGINAL verifier,
                                      VerifiedAt = the ORIGINAL timestamp }

ticket.CurrentRevisionId = next.Id
apply AwaitingInformation → Submitted (§4.2), retaining the Assignee
audit RevisionSubmitted
notify CorrectionSubmitted to the Assignee
commit
```

Rules:

1. **The previous revision is never touched.** §3: *"A revision, once written, is never modified and
   never deleted."* No `UPDATE` on `ticket_revisions` or on the old `field_values`.
2. **Every field in the Type version gets a row in the new revision**, either new or carried
   forward. A revision is *"an immutable snapshot of **all** Field Values for a Ticket at one
   moment"* — a partial revision cannot be read as a snapshot, and the "what did they originally
   claim" question stops being answerable by reading one revision.
3. **`is_carried_forward` is set correctly, and it is not cosmetic.** It is what tells the Accountant
   which fields need attention.
4. **Fields accepted in the previous revision and carried forward unchanged retain their accepted
   state.** §5 step 5, verbatim: *"Fields accepted in revision 1 and carried forward unchanged
   **retain their accepted state** — do not force an Accountant to re-verify what did not
   change."*

   > Because verifications attach to a `FieldValue` **in a specific revision** (§3), and the new
   > revision has *new* `FieldValue` rows, this is not automatic — the acceptance must be copied
   > forward as a new `FieldVerification` row pointing at the new value. **Preserve the original
   > verifier and the original timestamp**, because the record must say who accepted it and when,
   > not that the correction re-accepted it.
   >
   > This is the subtlest requirement in the slice. Skip it and the Office is forced to re-verify
   > every field on every correction round, which nothing will report as a bug — it will be reported
   > as the app being tedious.

5. **A carried-forward value that was *rejected* does not carry the rejection forward** — if the
   Customer side did not change a rejected field, the new value is unverified and the Accountant
   rejects it again (with a reason) or accepts it. Do not copy a `Rejected` verification; that would
   make the ticket permanently unclosable with no action available.
6. **A `FileUpload` field's document must belong to this ticket.** Verify
   `doc.TicketId == ticket.Id` before accepting `value_document_id` — §0.3 step 5's IDOR in a
   different disguise (§1.4 rule 4).
7. **An Accountant submitting a revision may only change Accountant-only fields.** §9.4 and §6.3.
   An Accountant-supplied value for a Customer field is `403`, and there must be **no code path** by
   which an Accountant's identity attaches to a Customer-supplied `FieldValue`.
8. **A concurrent second correction is a `409`**, from `RequireVersion` or from
   `uq_ticket_revisions_sequence` (`23505`). Map both to `409`; never a `500`.
9. **Fields are editable only in `Draft` and `AwaitingInformation`** (§5, and `Ticket.FieldsEditable`).
   In `Draft` this handler edits revision 1 in place rather than appending — **decide which and state
   it**; appending a revision per keystroke-save is wrong, and mutating revision 1 after submission
   is wrong. §13 item 3.

### 4.6 `VerifyFieldHandler` — Accountants only

`RequireAsync(user, "VerifyField")` — `AA`, `AU`. Matrix §7: CA and EMP **No**.

1. **`Accepted` or `Rejected`, and a rejection requires a reason** —
   `ck_field_verifications_reason` is the backstop, but return a `422` with a real message rather
   than letting the constraint produce a `500`.
2. **The reason is shown verbatim to the Customer side** (§3), so it is a user-facing string.
   Do not put an internal code in it.
3. **It appends; it never updates.** A re-verification is a new row (§1.5).
4. **It verifies a `FieldValue` in a specific revision**, and only in the **current** revision.
   Verifying a superseded revision's value is meaningless and must be rejected — `422`.
5. **This handler never writes a `FieldValue`.** §9.4, LOCKED: *"There is no handler, no endpoint,
   and no code path by which an Accountant's identity ends up attached to a Customer-supplied
   FieldValue."* Rejecting is the only path.
6. **A rejection usually accompanies `InReview → AwaitingInformation`**, but they are separate
   operations: the transition table's condition is *"At least one field rejected, or a question
   posted"*. Allow rejecting several fields and then transitioning once, rather than transitioning
   per rejection — otherwise the Customer side gets one notification per field.
7. Audit `FieldVerified` or `FieldRejected`. Notify `FieldRejected` (emailed) on a rejection.

### 4.7 `SetPriorityHandler`, `SetDueDateHandler`, `UpdateTicketHandler`

`RequireAsync(user, "SetTicketPriority")` / `"SetTicketDueDate"` — **Accountants only**. Matrix §7:
*"Set priority or due date"* — CA and EMP **No**. [01-DomainModel.md](../../01-DomainModel.md) §3
says the same twice ("Set by an Accountant only", "optional, set by an Accountant").

1. **Two handlers, not one.** They audit differently (`PriorityChanged` vs `DueDateChanged`) and
   a combined handler with two nullable fields cannot distinguish "not supplied" from "clear it".
2. **There is no general `UpdateTicketHandler`, and there must not be.** Customer, Type, Type
   version, Creator, Subject, and Preceded-by are **immutable after creation** (§3). Title is
   derived. Status has its own transitions. Assignee has §4.8. That leaves priority and due date —
   which is why those are the only two. **A DTO with a `CustomerId` or `SubjectEmployeeId` property
   is a bug**, because a property that exists is a property somebody binds.
3. **`RequireVersion` and `Touch`** — both write the `tickets` row.
4. **Neither is permitted on a terminal ticket.** `Closed` and `Cancelled` are read-only; `422`.
5. **A due date in the past is allowed** — an Accountant recording an already-missed statutory
   deadline is ordinary. Do not add a future-date guard.

### 4.8 `PickupTicketHandler` and `AssignTicketHandler`

`RequireAsync(user, "PickupTicket")` / `"AssignTicket"` — **Accountants only** (matrix §7, all
three assignment rows).

**Pickup** is `Submitted → InReview` with self-assignment. **Assign** sets or changes the Assignee
without necessarily moving the status.

1. **Pickup sets the status and the Assignee in one atomic operation.**
   [01-DomainModel.md](../../01-DomainModel.md) §3: *"Moving a Ticket from `Submitted` to `InReview`
   **must** set an Assignee in the same operation. A request that would leave it null is rejected.
   The two are one atomic action, not a status change followed by an optional assignment."*
   `ck_tickets_assignee` is the backstop.
2. **Any Accountant may reassign any Ticket, including to themselves, in any non-terminal status**
   — §9.9, LOCKED, and §3's assignment rules. *"An Accountant User may reassign a Ticket away from
   an Accountant Admin — there is no seniority in assignment."* Do **not** restrict reassignment to
   `AccountantAdmin`: §9.9 says that would create a fifth Admin-only power and contradict the locked
   "exactly four powers" list.
3. **The target must be an `Active` Accountant of either role.** Verified through
   `IIdentityApi.FindAsync` — role in {`AccountantAdmin`, `AccountantUser`} and `IsActive`. A
   Customer-side target is `422`, not `403`. **Read the status live**; a suspended Accountant must
   not be a valid assignment target even though their existing assignments are retained.
4. **Taking a ticket whose Assignee is a different, inactive user is audited as a REASSIGNMENT, not
   a pickup**, and the entry **names the previous Assignee as well as the new one.** §9.8 rule 3,
   verbatim: *"It is not recorded as a plain pickup."*

   > So the audit code depends on the prior state, not on which endpoint was called:
   > `AssigneeUserAccountId == null` → `TicketAssigned`; a different prior Assignee →
   > `TicketReassigned` with both ids in `Before`/`After`. A handler that hardcodes `TicketAssigned`
   > because it is "the pickup endpoint" loses the only record that work was taken from someone.

5. **Every reassignment writes an audit entry naming both the previous and the new Assignee**
   (§9.9). *"Attribution is preserved by the audit log, not by withholding the operation."*
6. **A `SystemEvent` message for every assignment and reassignment** (§3, and §4.0 E).
7. **Notify `TicketAssignedToYou`** when the new Assignee is not the caller. Notify
   `TicketPickedUp` to the Customer side on the `Submitted → InReview` transition.
8. **Assignment does not restrict permissions** (§3). Any Accountant may read and act on any ticket
   regardless of the Assignee. Do not add an "only the Assignee may…" check anywhere.
9. **An Assignee is retained through `AwaitingInformation`** (§3), and retained through
   `AwaitingInformation → Submitted` (§4.2 rule 1).

### 4.9 `AnswerTicketHandler`, `CloseTicketHandler`, `RequestInformationHandler`, `ReturnToReviewHandler`

All **Accountants only** (matrix §7: *"Change status to `Answered` / `Closed`"* — CA and EMP No;
*"Only an Accountant may close a Ticket. The Customer side never closes."*).

1. **`InReview → Answered` requires no rejected or unverified required visible fields.** The
   transition table's condition.
2. **`Answered → Closed`** — and the **closing rule** from §3, which is stricter and stated
   separately: *"a Ticket cannot move to `Closed` while any required, visible FieldValue in the
   current revision is unverified or rejected."* Check it at close even though `Answered` already
   required it, because `Answered → InReview → Answered` can happen in between.
3. **`Answered → InReview` exists** — *"Reopening before close, e.g. the response was wrong"*. It is
   in the transition table, it is **not** a reopen of a `Closed` ticket, and confusing the two
   produces either a missing legal transition or an illegal one. §5.
4. **`InReview → AwaitingInformation` requires at least one rejected field or a posted question**,
   and **retains the Assignee**.
5. **`closed_at` is set exactly when the status becomes `Closed`**, per `ck_tickets_closed`.
6. **There is no transition out of `Closed`.** §9.1, LOCKED. No reopen endpoint, no `Reopened`
   status, no `ReopenedAt`, no `Closed → InReview` row. A continuation is a new ticket with
   `PrecededByTicketId` (§4.1).
7. Notify `TicketAnswered` / `TicketClosed` / `InformationRequested` — all three **emailed**.
8. Audit `TicketStatusChanged`, plus `TicketClosed` on the close.

### 4.10 `PostMessageHandler`

`RequireAsync(user, "PostMessage")` — all four roles. `RequireAsync(user, "PostInternalNote")` —
Accountants only.

1. **`Kind` is derived from the caller's role, never supplied.** An Accountant posting gives
   `AccountantResponse`; a Customer-side actor gives `CustomerMessage`. If it came from the body, a
   Customer could post something that renders as an Accountant response.
2. **An `InternalNote` is filtered out server-side for Customer-side callers** — matrix §6: *"This
   must be enforced on the server by filtering, not by the React app choosing not to display
   them."* Use `TicketMessageKind.CustomerVisible` (§2.2), an allow-list.
3. **`InternalNote` is a separate action, Accountants only**, so the catalogue denies it rather than
   a handler branch. Matrix §6: *"Internal Notes are visible to **both** Accountant roles. They are
   the Office's private channel, not the Admin's."*
4. **`SystemEvent` is never posted by a person.** No endpoint produces one; only
   `TicketTransitions.Apply` does (§4.0 E), with a null author.
5. **Posting an `InternalNote` notifies nobody on the Customer side** (§4.0 I). Posting a
   `CustomerMessage` notifies the Assignee (`CustomerReplied`); posting an `AccountantResponse`
   notifies the Customer side (`AccountantResponded`).
6. **Messages are append-only** (§3). No edit handler, no delete handler, no `edited_at`.
7. **Attached documents go in `ticket_message_documents`**, and every attached document must already
   belong to this ticket — §0.3 step 5 again.
8. **A message on a terminal ticket is `422`.** `Closed` is read-only (§5), though its documents stay
   downloadable (§4.11 rule 2).
9. Audit `MessagePosted`.

### 4.11 The four document handlers

`UploadDocumentHandler`, `ListTicketDocumentsHandler`, `DownloadDocumentHandler`,
`DeleteDocumentHandler`. Registered here (§0.3), delegating to `IDocumentApi`.

1. **All six steps of §0.3, on every one of the four.** Especially step 5.
2. **Downloading from a `Closed` ticket is explicitly permitted** — matrix §8: *"it is a stated
   requirement."* A blanket "no operations on a terminal ticket" guard must not catch the download
   or list path. It **must** catch upload and delete.
3. **Every download is audited, and the audit commits before the bytes stream** (§4.0 D).
4. **`Origin` is derived from the caller's role** (§0.3).
5. **Upload follows the matrix, with no status qualifier — including `InReview`.** §13 item 5(a),
   **decided**. A Customer-side actor may upload to any ticket they can see, in any non-terminal
   status, and the field-editability rule does **not** constrain it. The reasoning: an Accountant
   mid-review routinely needs one more document, and the alternative forces a bounce through
   `AwaitingInformation` purely to accept a file. An upload is additive and audited, where a field
   edit rewrites the thing under review — so the two rules differing is correct, not an
   inconsistency to be smoothed away.

   **What still blocks an upload:** the terminal statuses. Rule 2 above requires the terminal guard
   to catch upload and delete while letting download and list through, so `Closed` and `Cancelled`
   refuse an upload. That is the only status restriction on this path; do not add another.
6. **Soft delete only, and the permission rule has two halves** (matrix §8): Accountants may
   soft-delete any document on a ticket they can see; a Customer-side actor may delete **their own
   uploads only, and only while the ticket has not yet reached `InReview`**. Both halves need data
   this slice has: the uploader from `DocumentSummary.UploadedByUserAccountId`, the status from the
   ticket row.

   > *"Has not yet reached `InReview`"* is not the same as *"is not `InReview`"*. A ticket in
   > `Answered` has reached it. The correct test is `status is Draft or Submitted` — and
   > `Submitted` after a correction round has already been in `InReview`, which the status alone
   > cannot tell you. **Flag it** (§13 item 5); the safest reading is `Draft` or (`Submitted` with
   > no Assignee), because an Assignee is the durable trace of having been picked up.

7. **A soft-deleted document is absent from the list and `404` on download** — never `403`. The
   global query filter in `Documents` gives this for free; do not add an `IgnoreQueryFilters` path.
8. Audit `DocumentUploaded` / `DocumentDownloaded` / `DocumentSoftDeleted`.

### 4.12 `CancelTicketHandler`

`RequireAsync(user, "CancelTicket")` — all four roles, with the narrowest reach in the matrix.

Matrix §7: AA/AU yes; CA *"Yes, own Customer"*; EMP *"Own drafts and own `Submitted` tickets"*.

1. **The `Employee` restriction is two conditions**: their own ticket (Creator), **and** status in
   {`Draft`, `Submitted`}. An Employee cannot cancel their own ticket once an Accountant is working
   on it.
2. **Cancellation is the only removal** (matrix §7). It is a status, not a delete: the ticket, its
   revisions, its messages, and its documents all remain (§1.9).
3. **`Cancelled` is absolutely terminal.** No transition out (§5). No un-cancel endpoint.
4. **The Assignee is cleared**, because `ck_tickets_assignee` requires null in `Cancelled`. That is
   the one place an Assignee is removed, and it is a consequence of the constraint, not a policy —
   note it, because it looks like it contradicts *"assignments are never silently redistributed"*
   (§9.8). It does not: the ticket is over.
5. Audit `TicketCancelled`. Notify `TicketCancelled` to the other side.

---

## 5. The state machine — one file, one closed table

`Application/TicketTransitions.cs`. [01-DomainModel.md](../../01-DomainModel.md) §5's transition
table, transcribed exactly, plus the four-part `Apply` from §4.0 E.

```csharp
// The table is COMPLETE and CLOSED. There is no row whose From is Closed or Cancelled.
private static readonly (string From, string To)[] Allowed =
[
    (Draft,               Submitted),
    (Draft,               Cancelled),
    (Submitted,           InReview),            // MUST set an Assignee in the same operation
    (Submitted,           Cancelled),
    (InReview,            AwaitingInformation), // Assignee retained
    (InReview,            Answered),
    (InReview,            Cancelled),
    (AwaitingInformation, Submitted),           // Assignee RETAINED -- not back in the pool
    (AwaitingInformation, Cancelled),
    (Answered,            Closed),
    (Answered,            InReview),            // reopening BEFORE close -- not a §9.1 reopen
];
```

Rules:

1. **Any pair not in the table is `422`.** Not `500`, not silently ignored.
2. **There is no row whose `From` is `Closed` or `Cancelled`**, and adding one violates §9.1 and §5.
   Both terminal statuses are equally terminal.
3. **`(Answered, InReview)` is in the table and `(Closed, InReview)` is not.** These look alike and
   are opposite. Comment it.
4. **`(Submitted, InReview)` cannot be applied without an Assignee** — the helper takes the new
   Assignee and rejects a null for this pair.
5. **`(AwaitingInformation, Submitted)` must not clear the Assignee.** §4.2 rule 1.
6. **`Apply` does all four things** (§4.0 E): validate, write status + `Touch`, write the
   `SystemEvent`, audit and notify. Nine call sites, one implementation.
7. **The `SystemEvent` body is generated, human-readable, and stable** — e.g. *"Status changed to
   Awaiting Information"* ([01-DomainModel.md](../../01-DomainModel.md) §3). It has a null author.

---

## 6. Field values — validation against another slice's descriptors

`Application/FieldValueValidation.cs`.

### 6.1 Where the descriptors come from

`ITicketTypesApi`, which the shipped code defines as:

```csharp
Task<TicketTypeDetailDto?> GetTicketTypeAsync(Guid ticketTypeId, UserRole callerRole, CancellationToken ct);
Task<TicketTypeDetailDto?> GetTicketTypeVersionAsync(Guid ticketTypeId, int versionNumber, UserRole callerRole, CancellationToken ct);
Task<List<TicketTypeListItemDto>> ListAvailableTypesAsync(UserRole callerRole, CancellationToken ct);
```

> **Both problems below were fixed on 2026-09-02, before this slice was built** (§13 item 1, §12
> constraint 12). The contract now reads:
>
> ```csharp
> Task<TicketTypeDetailDto?> GetTicketTypeAsync(Guid ticketTypeId, UserRole callerRole, CancellationToken ct);
> Task<TicketTypeDetailDto?> GetTicketTypeVersionAsync(Guid ticketTypeId, int versionNumber, UserRole callerRole, CancellationToken ct);
> Task<TicketTypeDetailDto?> GetVersionByIdAsync(Guid ticketTypeVersionId, UserRole callerRole, CancellationToken ct);
> Task<List<TicketTypeListItemDto>> ListAvailableTypesAsync(UserRole callerRole, CancellationToken ct);
> ```
>
> — and those types now live in `TicketTypes.ExternalInterfaces`, not `Application.Dtos`. The two
> problem statements are kept below because they are why the contract has the shape it has, and
> because reintroducing either is easy: `GetVersionByIdAsync` looks redundant next to
> `GetTicketTypeVersionAsync` until you notice what a ticket actually stores.

**Two problems with this contract, both of which must be raised before this slice is built:**

1. **There is no way to fetch a version by its id.** `tickets.ticket_type_version_id` stores a
   `Guid`, but `GetTicketTypeVersionAsync` takes a **version number**. So reading a ticket cannot
   resolve its own frozen descriptor set without also storing the version number, or without a new
   method. **`ITicketTypesApi` needs `GetVersionByIdAsync(Guid ticketTypeVersionId, UserRole, ct)`.**
   §13 item 1.
2. **It returns `TicketTypeDetailDto` from `TicketTypes.Application.Dtos`.** Dependency rule 2:
   *"A slice never references another slice's `Core` entities, `Application` handlers, or
   `Infrastructure`. Never."* `Application/Dtos` is under `Application`. So `Tickets` importing that
   namespace is a boundary violation in the **shipped** code, and the fix belongs in `TicketTypes`:
   move the contract types into its `ExternalInterfaces/`. §13 item 1.

Until both are resolved this slice cannot be built correctly. **Raise them; do not work around them
by storing a version number alongside the id, which would give the ticket two references to one
thing.**

### 6.2 What must be validated

Against each `FieldDescriptorDetailDto` on `version.Fields`:

> **AMENDED.** This said "each `FieldDescriptor` (the shipped shape has 24 properties)", describing a
> flat entity. What `ITicketTypesApi` actually returns is `FieldDescriptorDetailDto`, and the validation
> limits are **nested**: `field.Validation.MinLength`, `field.Validation.RegexPattern`,
> `field.Validation.AllowedFileTypes` and so on hang off a non-null `FieldValidationDto`, and
> `field.ConditionalVisibility` is a nullable `ConditionalVisibilityDto`. Read them through those two
> objects, not as properties of the field. `Validation` is never null — an unconstrained field has one
> with every property null — so `field.Validation.MaxLength is not null` is the "is there a limit"
> test, not a null check on `Validation` itself. `ConditionalVisibility` **is** null when the field is
> unconditional, and that is the check for §6.4.

| Descriptor property | Rule |
|---|---|
| `DataType` | The value goes in the matching column (§1.4.1) and parses as that type |
| `IsRequired` | A missing value is `422` — **but only if the field is visible**, see §6.4 |
| `MinLength`, `MaxLength` | Text length |
| `MinValue`, `MaxValue` | Numeric range |
| `EarliestDate`, `LatestDate` | Date range |
| `RegexPattern` | Text pattern — see rule 3 |
| `ChoiceOptions` | Every chosen key is in the list; `MultipleChoice` validates each element |
| `AllowedFileTypes`, `MaxFileSizeBytes` | `FileUpload` only, checked against the `DocumentSummary` |
| `IsVisibleToCustomer` | §6.3 |
| `ConditionalVisibilityFieldKey`/`Value` | §6.4 |

Rules:

1. **Validation happens in this slice, not in `TicketTypes`.** `TicketTypes` defines the rules;
   `Tickets` applies them to a submitted value. Neither half works alone.
2. **A value for a `field_key` not in the version's descriptors is `422`**, not silently dropped.
   Dropping it makes a typo'd key look like a missing required field, three screens away.
3. **`RegexPattern` is applied with a timeout — `new Regex(pattern, RegexOptions.None,
   UserSuppliedRegex.MatchTimeout)`,** from `Shared/Validation/UserSuppliedRegex.cs`. The pattern is
   authored by an Accountant, but the value it runs against comes from a Customer over the internet, so
   a catastrophically backtracking pattern is a denial of service against the whole process. Catch
   `RegexMatchTimeoutException` and return a `422` naming the field, never a `500` and never a hung
   request.

   Use that shared constant, never a local literal: `TicketTypes` compiles every stored pattern with
   the same timeout when the descriptor is authored, and two different timeouts for one pattern is a
   bug waiting for a pattern that sits between them — accepted under the generous budget, dying under
   the strict one, in the slice that did not author it.

   Do **not** substitute `RegexOptions.NonBacktracking`. It rejects backreferences and lookaround at
   construction, so a pattern `TicketTypes` legitimately accepted would throw `ArgumentException` here
   and turn a valid ticket type into a `500`.

   > **AMENDED.** This rule previously named
   > `TicketTypes.Application.TicketTypeMapper.RegexMatchTimeout`. That reference compiled — the field
   > was `internal` and there is one assembly — but it directed a violation of dependency rule 2: a
   > slice reaching into another slice's `Application`. The constant now lives in `Shared/Validation`,
   > which is where a limit both slices need belongs. It is not part of the `TicketTypes` contract, so
   > `TicketTypes.ExternalInterfaces` would have been the wrong home too.

   Do not reach for `RegexOptions.NonBacktracking` here. It is not a drop-in — it rejects
   backreferences and lookaround at construction time, so a pattern `TicketTypes` accepted would
   throw `ArgumentException` when `Tickets` tries to run it, turning a valid ticket type into a
   `500` in a different slice. The timeout is the mechanism; `NonBacktracking` is not a fallback
   for it.
4. **Every failure is a `422` with the field key in the message.** A validation error the user cannot
   locate is not a validation error.
5. **`DateRange` needs both ends and `value_date_to >= value_date`.** There is no `CHECK` for it
   (§1.4 rule 3), so the handler is the only guard.

### 6.3 Accountant-only fields — the rule that looks like a contradiction

§9.4, LOCKED, and it takes care to head off the confusion:

| Field kind | Who may write it |
|---|---|
| A normal field on the Type | **The Customer side only** (Employee, or Customer Admin on their behalf). An Accountant may only verify. |
| A descriptor with `IsVisibleToCustomer = false` | **The Accountant only.** The Customer side never sees it, let alone writes it. |

> *"So 'an Accountant may edit Accountant-only fields' and 'an Accountant may not edit a Field Value'
> do not conflict: they are about disjoint sets of fields."*

Four rules:

1. **An Accountant supplying a value for a Customer-visible field is `403`**, and there must be **no
   code path** by which an Accountant's identity attaches to a Customer-supplied `FieldValue` (§9.4).
2. **A Customer-side caller supplying a value for an Accountant-only field is `403`**, and the field
   is **absent from every response** they receive — not nulled, absent (§4.3 rule 5).
3. **Accountant-only fields are never required for a Customer-side submission** (§4.2 rule 3).
4. **The split is by `IsVisibleToCustomer`**, the shipped descriptor property. There is no separate
   `IsAccountantOnly` flag, and adding one gives the system two answers.

### 6.4 "Required visible fields" means two things at once

The transition condition is *"All required visible fields valid"*, and **visible** is the
conjunction of:

1. `IsVisibleToCustomer` — for a Customer-side submission (§6.3)
2. **Not hidden by conditional visibility** — `ConditionalVisibilityFieldKey` is set and the
   referenced field's current value does not equal `ConditionalVisibilityValue`

> **A required field that is conditionally hidden is not required.** Miss this and submission is
> impossible for any ticket type using a conditional field, with a `422` naming a field the user
> cannot see and cannot fill. It is the kind of bug that gets reported as "the app is broken" rather
> than as a validation error.

Rules:

1. **Evaluate conditional visibility against the values in the revision being submitted**, not the
   previous one.
2. **A hidden field's value is not written**, and a value supplied for a hidden field is `422` — not
   stored-but-ignored, which leaves a value that reappears if the condition later flips.
3. **Conditions are one level deep**, on the shipped descriptor shape (a single key and value). Do
   not build a condition tree. If a chain of conditions is needed — field C visible only when B is
   visible and set — that is a `TicketTypes` change to raise.

---

## 7. Service registration

### 7.1 `Slices/Tickets/TicketsRegistration.cs`

```csharp
public static IServiceCollection AddTicketsSlice(
    this IServiceCollection services, IConfiguration configuration)
{
    // The SHARED request connection overload. See 7.3 rule 1.
    services.AddDbContext<TicketsDbContext>((serviceProvider, options) =>
        options.UseNpgsql(serviceProvider.GetRequiredService<RequestConnection>().Connection));

    services.AddSingleton<IActionCatalogue, TicketsActionCatalogue>();

    services.AddTransient<CreateTicketHandler>();
    // … the other ~20 handlers, including the four document handlers …

    // NO IDocumentApi registration -- that is DocumentsRegistration's.
    // NO ITicketApi -- nothing depends on this slice (§0.2).

    return services;
}
```

### 7.2 `Slices/Tickets/TicketsActionCatalogue.cs`

```csharp
public sealed class TicketsActionCatalogue : IActionCatalogue
{
    public string SliceName => "Tickets";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } = new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
    {
        ["CreateTicket"]        = [AA, AU, CustomerAdmin, Employee],
        ["SubmitTicket"]        = [AA, AU, CustomerAdmin, Employee],
        ["ListTickets"]         = [AA, AU, CustomerAdmin, Employee],
        ["ViewTicket"]          = [AA, AU, CustomerAdmin, Employee],
        ["ListPickupQueue"]     = [AA, AU],
        ["SubmitRevision"]      = [AA, AU, CustomerAdmin, Employee],
        ["VerifyField"]         = [AA, AU],
        ["SetTicketPriority"]   = [AA, AU],
        ["SetTicketDueDate"]    = [AA, AU],
        ["PickupTicket"]        = [AA, AU],
        ["AssignTicket"]        = [AA, AU],
        ["AnswerTicket"]        = [AA, AU],
        ["CloseTicket"]         = [AA, AU],
        ["RequestInformation"]  = [AA, AU],
        ["ReturnTicketToReview"]= [AA, AU],
        ["PostMessage"]         = [AA, AU, CustomerAdmin, Employee],
        ["PostInternalNote"]    = [AA, AU],
        ["CancelTicket"]        = [AA, AU, CustomerAdmin, Employee],

        // Registered by THIS slice, per the Documents plan §0.2.
        ["UploadDocument"]      = [AA, AU, CustomerAdmin, Employee],
        ["ListTicketDocuments"] = [AA, AU, CustomerAdmin, Employee],
        ["DownloadDocument"]    = [AA, AU, CustomerAdmin, Employee],
        ["DeleteDocument"]      = [AA, AU, CustomerAdmin, Employee],
    };
}
```

Rules:

1. **Not one action here is `AccountantAdmin`-only.** Matrix §7's note: *"Serving tickets is fully
   open to both Accountant roles. Verifying, responding, assigning, and closing are all available to
   an Accountant User. This is the core of what the role exists for."* And §9.9: restricting
   reassignment to `AA` would create a fifth Admin-only power and contradict the locked
   "exactly four powers" list. **If any row here is `[AA]` alone, it is wrong.**
2. **`PostInternalNote` is a separate action from `PostMessage`**, so the catalogue denies rather
   than a handler branching (§4.10 rule 3).
3. **The four document actions live here and nowhere else.** A `DocumentsActionCatalogue` declaring
   them too is a startup failure naming both slices.
4. **Every Customer-side "yes, but only…" qualifier is missing from this table on purpose.** The
   catalogue says who may call; §0.4 and the per-handler checks say which rows. A reviewer reading
   only this file would conclude an `Employee` can cancel any ticket — §4.12 rule 1 is what stops
   them.
5. **Action names are globally unique.** `ViewTicket`, not `View`.
6. **No empty role arrays** — the composer fails startup on one, deliberately.

### 7.3 Registration traps

1. **`AddDbContext` must use the `(serviceProvider, options)` overload and `RequestConnection`.**
   The plain overload compiles and gives this slice its own connection — at which point every
   cross-slice call (`IDocumentApi.StoreAsync`, `IAuditApi`, `INotificationApi`) commits
   independently of the ticket change. **An upload's bytes then survive a rolled-back ticket
   operation, and a status change can commit without its audit entry.** Nothing fails visibly.
2. **Never `AddScoped<TicketsDbContext>()`.**
3. **Do not register `IDocumentApi` here.** It is `DocumentsRegistration`'s, and a second
   registration would shadow it with a context this slice controls.
4. **Register the catalogue as `IActionCatalogue`**, not the concrete type. A concrete registration
   is never seen by `PermissionChecker`, every action is absent, and **every endpoint in this slice
   returns `403`**.
5. **This slice must be registered last**, after all seven others, matching
   `App/GeneralAppArchitecture.md` §7's example. A missing `AddDocumentsSlice` surfaces here as an
   unresolvable `IDocumentApi` on first request rather than at startup; a startup assertion that all
   six injected contracts resolve is worth the twelve lines.
6. **Handlers are `AddTransient`.**

### 7.4 What `Program.cs` adds

```csharp
builder.Services.AddTicketsSlice(builder.Configuration);
// …
app.MapTicketEndpoints();
```

Two lines. Note that `Documents` contributes only **one** (no `MapDocumentEndpoints()`), and that
`App/GeneralAppArchitecture.md` §7's example still shows it —
[the Documents plan](../Documents/IMPLEMENTATION_PLAN.md) §13 item 1 raises the amendment.

---

## 8. Endpoints

`TicketsEndpoints.cs` at the slice root. **Two route groups**, and the second belongs to another
slice's domain.

### 8.1 `/api/tickets/*`

| Method | Route | Handler | Roles |
|---|---|---|---|
| `POST` | `/api/tickets/create` | `CreateTicketHandler` | all four |
| `POST` | `/api/tickets/submit` | `SubmitTicketHandler` | all four |
| `POST` | `/api/tickets/list` | `ListTicketsHandler` | all four |
| `POST` | `/api/tickets/get` | `GetTicketHandler` | all four |
| `POST` | `/api/tickets/pickup-queue` | `ListPickupQueueHandler` | AA, AU |
| `POST` | `/api/tickets/submit-revision` | `SubmitRevisionHandler` | all four |
| `POST` | `/api/tickets/verify-field` | `VerifyFieldHandler` | AA, AU |
| `POST` | `/api/tickets/set-priority` | `SetPriorityHandler` | AA, AU |
| `POST` | `/api/tickets/set-due-date` | `SetDueDateHandler` | AA, AU |
| `POST` | `/api/tickets/pickup` | `PickupTicketHandler` | AA, AU |
| `POST` | `/api/tickets/assign` | `AssignTicketHandler` | AA, AU |
| `POST` | `/api/tickets/request-information` | `RequestInformationHandler` | AA, AU |
| `POST` | `/api/tickets/answer` | `AnswerTicketHandler` | AA, AU |
| `POST` | `/api/tickets/close` | `CloseTicketHandler` | AA, AU |
| `POST` | `/api/tickets/return-to-review` | `ReturnToReviewHandler` | AA, AU |
| `POST` | `/api/tickets/post-message` | `PostMessageHandler` | all four |
| `POST` | `/api/tickets/post-internal-note` | `PostMessageHandler` | AA, AU |
| `POST` | `/api/tickets/cancel` | `CancelTicketHandler` | all four |

### 8.2 `/api/documents/*` — registered here, on purpose

| Method | Route | Handler | Roles |
|---|---|---|---|
| `POST` | `/api/documents/upload` | `UploadDocumentHandler` | all four |
| `POST` | `/api/documents/list` | `ListTicketDocumentsHandler` | all four |
| `POST` | `/api/documents/download` | `DownloadDocumentHandler` | all four |
| `POST` | `/api/documents/delete` | `DeleteDocumentHandler` | all four |

Rules:

1. **A comment at the `/api/documents/*` registration site** naming
   [the Documents plan](../Documents/IMPLEMENTATION_PLAN.md) §0.2 and saying in one line why these
   routes are registered from `TicketsEndpoints.cs`. Without it somebody will move them into
   `Documents` and create the cycle.
2. **Multi-word segments are kebab-case** — `pickup-queue`, `submit-revision`, `verify-field`,
   `set-priority`, `set-due-date`, `request-information`, `return-to-review`, `post-message`,
   `post-internal-note`. `App/GeneralAppArchitecture.md` §8, LOCKED.
3. **No route parameters.** Not `/api/tickets/{id}/close`. Ids go in the body.
4. **Everything is `POST`, including the reads.** Consistent with every other slice.
5. **There is no `DELETE` endpoint and no `/api/tickets/reopen`.** Matrix §7: delete — Nobody;
   reopen — Nobody. §9.1.
6. **`/api/documents/upload` is the one multipart endpoint in the system.** Explicit
   `RequestSizeLimit` and a matching `MultipartBodyLengthLimit`; the only place an `IFormFile`
   appears.

   > **Take the number from `Documents`, not from a literal here** (amended 2026-09-02). The limit is
   > declared once, as
   > `AccountantApp.Api.Slices.Documents.ExternalInterfaces.DocumentLimits.MaxUploadSizeBytes`, and the
   > Documents plan's criterion 30 requires it be declared exactly once. Writing `26_214_400` in this
   > file again would be a second declaration of the same policy that nothing keeps in step — and the
   > failure mode is quiet, because the two only disagree for uploads sized between the smaller and the
   > larger limit.
   >
   > **The class name changed later the same day.** This note first said
   > `Documents.Application.UploadValidation.MaxUploadSizeBytes`, which is where the constant was built
   > and which made following this rule a dependency-rule-2 violation: `Application` is exactly the
   > folder this slice may not read. The two limits now live in
   > `Documents/ExternalInterfaces/DocumentLimits.cs`; `UploadValidation` keeps `private` aliases so its
   > own rules read as before. If you find `UploadValidation.MaxUploadSizeBytes` referenced anywhere
   > outside `Documents`, it is stale.
   >
   > **This is a real obligation on `Tickets`, not a nicety.** `Documents` enforces the cap when it
   > buffers the bytes, but `RequestSizeLimit` and `MultipartBodyLengthLimit` are **endpoint-level**
   > knobs, and `Documents` has no endpoints — so it physically cannot set them. If this rule is
   > skipped, the cap still holds (the slice rejects the oversized upload) but only *after* ASP.NET has
   > buffered the whole body, which is the denial-of-service shape the limits exist to prevent. The
   > proxy-side third of this (`04-Infrastructure.md` §7's "enforced at both the proxy and the
   > application") is **deferred**: there is no `Caddyfile` and no deployment layer in this repository.
   > See the Documents plan §13 item 3.
7. **Every mutating route's DTO carries `Version`** (§3.2 rule 1). A DTO without it cannot be
   concurrency-checked, and the check is where the `409` comes from.
8. **`.Produces<T>(200)` and `.ProducesProblem(...)` on every route.** `/api/tickets/get` returns
   different shapes by role (§4.3 rule 5) — document the narrowing rather than silently declaring
   one.

---

## 9. Cross-slice boundaries

`Tickets` may depend on **all seven** other slices
([03-SliceInventory.md](../../03-SliceInventory.md) §2). Nothing depends on it (§0.2).

| It calls | For | Not for |
|---|---|---|
| `IEmployeeApi.FindAsync` / `FindManyAsync` | Subject validation, Subject names on lists | Reading an SSN or tax number — `EmployeeSummary` has neither |
| `IEmployeeApi.IsActiveAsync` | Refusing a `Departed` Subject on a **new** ticket (§9.6 rule 3) | Any read path, ever |
| `IEmployeeApi.FindByAccountAsync` | Resolving the caller's Employee id for layer 2 (§3.1 rule 3) | — |
| `IEmployeeApi.ListActiveByCustomerAsync` | The Subject picker | A paginated list — it is unpaginated |
| `ICustomerApi.FindManyAsync` | Customer names on an Accountant's cross-Customer list | Tax numbers or addresses |
| `IIdentityApi.FindManyAsync` | **Assignee account status for pickup condition 2**, and Accountant display names | Reading a hash, a token, or lockout state |
| `IIdentityApi.FindAsync` | Validating an assignment target is an `Active` Accountant | — |
| `ITicketTypesApi` | Descriptors for validation and rendering | Creating or changing a type |
| `IDocumentApi` | Storing, reading, listing, soft-deleting bytes | Authorization — it does none (§0.3) |
| `INotificationApi` | Every kind in §4.0 G | Sending an email directly — the outbox does that |
| `IAuditApi` | Every write in §4.0 F | — |

Six boundary rules:

1. **`Tickets` names no other slice's `Core` entities.** It uses `EmployeeSummary`,
   `CustomerSummary`, `AccountSummary`, `DocumentSummary`. **The one current exception is
   `ITicketTypesApi`, which returns `Application/Dtos` types** — a violation of dependency rule 2 in
   the shipped code, to be fixed in `TicketTypes` (§6.1 problem 2, §13 item 1).
2. **`03-SliceInventory.md` §2 constrains what two of these edges may be used for**, and both
   constraints are narrow: `Tickets → Identity` is for *"account status and Accountant display names
   only"* and *"never reads a UserAccount row"*. Respect the stated scope even though the edge would
   technically permit more.
3. **Nothing is cached.** `IsActiveAsync` on an Employee, account status on an Assignee, Customer
   status — all read live. A cache is what would hide a suspension, and surfacing suspensions is the
   entire point of §9.8 condition 2.
4. **`Tickets` implements no inverted interface** and defines none (§0.2).
5. **`Tickets` writes to no other slice's table**, and reaches every one of them through its
   `ExternalInterfaces` (§4.0 J).
6. **A cross-slice call inside this slice's transaction rolls back with it only because every
   contract enlists.** `App/GeneralAppArchitecture.md` §5 rule 5 warns that this *"does not license
   general cross-slice transactions"* — the guarantee holds because `IDocumentApi` and `IAuditApi`
   explicitly enlist, and it must be **verified**, not assumed. §7.3 trap 1.

---

## 9a. The due-date scanner — the second `IHostedService` in the system

Authorized by §13 item 8. **Build this last**, after every handler and endpoint in this slice is
green. It is the only part of the slice that runs with no request, no `CurrentUser` and no
`IPermissionChecker`, and it is far easier to reason about once the read paths exist.

**File:** `Slices/Tickets/Infrastructure/DueDateScanner.cs`. Model it on
`Slices/Notifications/Infrastructure/OutboxDrainer.cs`, which is the only other one and which already
solved most of these problems.

### 9a.1 It does not change §10's migration

Its idempotency state (§9a.3) lives in a **seventh table**, added by a **second, later migration**:
`Slices/Tickets/Infrastructure/Migrations/20260905_001_CreateDueDateReminders.sql`. §10's script stays
exactly as specified — six tables and the counter — and success criterion 1 stays true as written.
Migrations are append-only (§10), so a second file is the normal way to add a table, not a workaround.
**Do not fold this table into the first script**, and do not let it hold up the first script.

### 9a.2 Rules

1. **Config-gated, and off by default**, exactly like the drainer: bind
   `Tickets:DueDateScanner` to an options record whose `Enabled` defaults to `false`, and call
   `AddHostedService` only when it is true. This is what keeps it out of the test host and out of a
   developer's F5 without anyone having to remember it.
2. **A fresh DI scope per pass**, via `IServiceScopeFactory`. `TicketsDbContext` is scoped onto
   `RequestConnection`; a scope per pass gives the scanner its own physical connection with no ambient
   transaction, and avoids accumulated change-tracker state. The comment at the top of
   `NotificationsRegistration` explains why this is not optional.
3. **It calls no handler and injects no `IPermissionChecker`.** There is no `CurrentUser` outside a
   request, and `PermissionChecker.RequireAsync` takes one. A scanner that manufactured a fake
   `CurrentUser` to reuse a handler would be inventing an actor, and the audit trail would name it.
   Read through `TicketsDbContext` directly and call `INotificationApi`.
4. **It writes no audit entry.** Every other write in this slice audits (§4.0 F), and this is the
   exception, because an `AuditEntry` names the actor who did the thing and there is no actor here.
   **Flagged, not hidden:** if an audit trail of reminders is wanted, it needs a system-actor concept
   that does not exist today, and that is a change to `Audit`, not a line in this file.
5. **The recipient is the Assignee, and a ticket with no Assignee is skipped.** An approaching due
   date on an unassigned ticket is a queue problem, and §4.4's pickup queue is already the place it
   surfaces — a reminder needs somebody to remind. Do **not** broadcast to every Accountant.
   Constraint, not oversight; see §12 constraint 13.
6. **Only non-terminal tickets.** `Closed` and `Cancelled` are skipped. `Answered` is **not** skipped
   — it is waiting on the Customer and its due date still matters.
7. **`due_date` is a date, so compute "near" in the Office's time zone, not UTC.** Inject
   `TimeProvider` and resolve the zone from one documented constant; there is no clock abstraction in
   the codebase today, so `TimeProvider` is the choice that makes this testable at all. A reminder
   that fires a day early because the host is UTC is the exact bug this rule exists to prevent, and it
   is invisible on a machine already in the Office's zone.
8. **`LeadTimeDays` is configuration, defaulting to 3.** One pass per day, not one per minute; the
   interval belongs in the same options record.
9. **One bad ticket must not kill the loop.** Catch per ticket, log, continue. The scanner must never
   take the application down, and a `BackgroundService` whose `ExecuteAsync` throws does exactly that.
10. **`DueDateApproaching` stays out of `NotificationEvents.Emailed`** — in-app only. The Notifications
    plan §3 rule 5 carries the reasoning. `NotificationApi` will accept the kind and write no outbox
    row, which is the intended behaviour and not a misconfiguration to be "fixed".
11. **Single replica, same as the drainer.** The `OutboxDrainer` doc comment states this constraint for
    itself; state it here too, because this is now the second reason horizontal scaling needs work
    first.

    **AMENDED.** This rule previously justified itself with "two instances scanning the same table both
    send the same reminder." That is false as designed, and the false reason is worse than no reason,
    because somebody who checks it will conclude the constraint is imaginary and remove it. With
    `pk_ticket_due_date_reminders` on `(ticket_id, due_date)` and the marker written in the **same
    transaction** as the notification (rule 12), a second scanner blocks on that key, gets `23505` when
    the first commits, and **rolls its own notification back**. The reminder is sent once. The real cost
    of a second replica is that every instance scans the whole candidate set and loses the race — wasted
    work with no claim locking — which is the same thing that blocks scaling the drainer. Keep the
    single-replica constraint; state the honest reason.

12. **The marker and the notification go in ONE transaction, marker first.** Marker-then-notify inside
    `IRequestTransaction.BeginAsync` means a failure raising the notification rolls the marker back and
    the next pass retries. The other order double-sends whenever the marker write fails, and a marker
    committed without its notification suppresses that reminder **permanently** — the marker is the only
    record consulted.

13. **The already-reminded exclusion belongs IN the candidate query, and it must be matched on
    `(ticket_id, due_date)`.** If the pass is capped — and it must be, because rule 6 puts no lower
    bound on `due_date`, so the first pass over an established database sees every overdue ticket ever
    — then filtering already-reminded tickets out of the query's **results** makes every marker
    permanently consume one slot of every later pass. Ordering is due date ascending, so the oldest
    overdue tickets sort to the front and stay there; once more open tickets are past due than the batch
    size, the batch is entirely already-reminded rows and **the scanner silently stops reminding
    anybody, forever**, with no error and a pass that truthfully reports zero work. Write it as a
    correlated `!db.DueDateReminders.Any(...)` in the `Where`, before `Take`: the in-memory provider
    evaluates it, so this stays testable, and Npgsql translates it to `NOT EXISTS`.

### 9a.3 The reminder table, and why not a column on `tickets`

`ticket_due_date_reminders`: the ticket id, the `due_date` the reminder was sent **for**, and
`sent_at`. Keying the sent-marker to the due date means moving a due date correctly re-arms the
reminder, where a plain boolean would suppress it forever.

**Do not put this on the `tickets` row.** §9.7 gives `tickets` optimistic concurrency, so a background
`UPDATE` to a reminder column would bump the concurrency token of a row a user may be editing, and
produce a spurious conflict from a change nobody made. A separate table touches nothing the request
path is holding. No deletes (§1.9): a re-armed reminder is a new row or an upsert, never a removal.

### 9a.4 Tests

The scanner is testable without a real database and without waiting a day, provided rules 1, 2, 7 and
8 are honoured: inject a fake `TimeProvider`, an in-memory context, and a recording
`INotificationApi`. At minimum — fires once for a ticket inside the lead time; does **not** fire twice
for the same `(ticket, due_date)`; **does** fire again after the due date moves; skips an unassigned
ticket; skips `Closed` and `Cancelled`; does **not** skip `Answered`; notifies the Assignee and nobody
else; a ticket exactly on the lead-time boundary is decided one way and asserted; and one throwing
ticket does not prevent the next from being notified.

**Plus one the list above missed, added after it was found in review:** with the batch size set small
(2, not the default), seed three tickets and assert that a **second** pass reaches the third rather
than re-fetching the two already-reminded ones. This is the only test that can fail on rule 13, and
every other test in this section passes with rule 13 violated, because none of them seeds more tickets
than one batch.

**And note what these tests structurally cannot reach on a machine with no PostgreSQL:** with
`NoOpRequestTransaction`, rule 12's "both or neither" is unverified — in-memory, a reminder that throws
after its marker is written **keeps** the marker, the opposite of production. That is the guarantee the
ordering exists for, so it must be stated at the test class rather than assumed from a green run.

---

## 10. Migrations

**File:** `Slices/Tickets/Infrastructure/Migrations/20260904_001_CreateTicketsSchema.sql`

- `YYYYMMDD_###_Description.sql`; the sequence restarts at `001` **per slice**, which is why the
  runner tracks the **slice-relative path with forward slashes**, never `Path.GetFileName`
  (`App/GeneralAppArchitecture.md` §6 — LOCKED).
- **Never `dotnet ef migrations add`.** Delete any C# migration folder that appears.
- One script: six tables, the reference counter, every `CHECK`, the two intra-slice foreign keys,
  and all nine indexes. **Table order matters** — `tickets` before `ticket_revisions`, and
  `preceded_by_ticket_id`'s self-reference means `tickets` must be created before it can reference
  itself (it can, in one `CREATE TABLE`).
- **No rollback script.** Append-only.
- Set the build action so the file is copied to the output directory.

---

## 11. Tests

### 11.1 At least one test must run against real PostgreSQL — mandatory

`Microsoft.EntityFrameworkCore.InMemory` is banned from the API project, test-only, and for this
slice it cannot see:

- All twelve `CHECK` constraints — including `ck_tickets_assignee`, which encodes the
  `AwaitingInformation → Submitted` trap
- `uq_tickets_reference` and `uq_ticket_revisions_sequence`, so both `409` paths are untestable
- **`INSERT … ON CONFLICT DO UPDATE … RETURNING`**, so the reference allocator (§1.7) cannot be
  tested at all — and it is the one piece of raw SQL in the slice
- The two partial indexes — `idx_tickets_pickup` and `idx_tickets_assignee_open`, the only two of the
  nine carrying a `WHERE` predicate. Their point is that the predicate is *part of the index*: an
  in-memory provider has no index at all, so a query whose filter no longer matches the predicate
  still returns the right rows and nothing reveals that the index stopped being used.
- **Real transactions**, so §7.3 trap 1 — the most damaging registration mistake — is undetectable
- `NUMERIC(18,4)` semantics for `MoneyAmount`

So: a real-PostgreSQL test covering, at minimum, concurrent reference allocation producing no
duplicates, a rolled-back creation leaving no `tickets` **and** no `ticket_revisions` row, a
`Submitted` ticket **with** an Assignee being accepted by `ck_tickets_assignee`, and a rejected
verification with a whitespace-only reason being refused.

> Docker is currently not starting on this machine, so no PostgreSQL exists and **no part of any
> slice's schema has ever been applied**. Every SQL statement in §1 and §10 is unverified. Apply the
> migration first and fix the script before trusting any of this plan's DDL.

### 11.2 Behavioural cases

Grouped, because there are too many to read flat.

**Visibility — layers 1 to 4 (§0.4)**

| Case | Expected |
|---|---|
| **`AccountantAdmin` reads a Customer's `Draft`** | **`404`** — layer 3 applies to Accountants (§3.1 rule 2) |
| `AccountantUser` lists tickets | no `Draft` of any Customer appears |
| A `CustomerAdmin` reads another Customer's ticket | `404`, not `403` |
| A `CustomerAdmin` reads an Employee's `Draft` at their own Customer | `404` |
| An `Employee` reads a colleague's ticket at their own Customer | `404` — layer 2 |
| An `Employee` reads a ticket where they are the **Subject** but not Creator | `200`, if not `Draft` |
| **An `Employee` reads a `Draft` where they are the Subject** | **`404`** — §9.3, LOCKED |
| A `CustomerAdmin` reads a `Departed` Employee's old tickets | `200` — §9.6 rule 1, permanently |
| A newly invited Employee reads their pre-account non-`Draft` tickets | `200`, with **no `UPDATE`** to `tickets` — §9.5 |
| A `CustomerAdmin` reads a ticket containing payroll data | `200` — the deliberate accepted decision, matrix §6 |
| An `Employee` role with no Employee record | empty result or `401`, **never** an unfiltered query |

**Conversation and internal notes**

| Case | Expected |
|---|---|
| A `CustomerAdmin` reads a ticket with internal notes | notes **absent from the JSON**, not flagged |
| An `Employee` reads the conversation | internal notes absent |
| An `AccountantUser` reads the conversation | internal notes **present** — matrix §6 |
| A `CustomerAdmin` posts to `/post-internal-note` | `403` from the catalogue |
| A message `kind` supplied in the body | ignored; derived from the role |
| Posting an internal note | notifies nobody on the Customer side |
| Editing or deleting a message | no endpoint exists |

**The state machine (§5)**

| Case | Expected |
|---|---|
| `Closed → InReview` | `422` — no such transition, §9.1 |
| `Cancelled → anything` | `422` |
| `Answered → InReview` | `200` — it **is** in the table |
| `Submitted → InReview` with no Assignee | rejected, by the handler **and** by `ck_tickets_assignee` |
| `AwaitingInformation → Submitted` | **Assignee retained** |
| Every transition | writes a `SystemEvent` message **and** an Audit entry |
| A `SystemEvent` message | has a null author |
| Any transition not in the table | `422`, not `500` |
| `Closed` ticket | `closed_at` set; no `ReopenedAt` column exists |

**The pickup queue (§9.8) — the highest-risk query**

| Case | Expected |
|---|---|
| `Submitted` with no Assignee | **in** the queue |
| **`Submitted` after a correction round, Assignee retained** | **NOT in the queue** — the §5 trap |
| `InReview` with an `Active` Assignee | not in the queue |
| `InReview` with a **suspended** Assignee | **in** the queue — condition 2 |
| `AwaitingInformation` with a suspended Assignee | in the queue |
| `Closed`/`Cancelled` with a suspended Assignee | not in the queue — open statuses only |
| Assignee's account no longer resolves | in the queue — fail toward surfacing |
| An `AccountantUser` takes a stranded ticket | `200` — not Admin-only, §9.8 |
| That operation's audit entry | **`TicketReassigned`, naming the previous Assignee** — not `TicketAssigned` |
| Suspending an Accountant | changes no ticket's status or Assignee |
| The queue handler | opens no transaction, writes nothing |
| The queue with 60 matches and `pageSize: 5000` | 50 rows, one `Identity` call |

**The correction round (§4.5)**

| Case | Expected |
|---|---|
| Revision 1 after revision 2 exists | byte-for-byte unchanged |
| Revision 2 | has a row for **every** descriptor, new or carried forward |
| An unchanged field | `is_carried_forward = true` |
| **A field accepted in rev 1, carried forward unchanged** | **still `Accepted` in rev 2, with the ORIGINAL verifier and timestamp** |
| A field **rejected** in rev 1 and carried forward | **unverified** in rev 2 — no rejection copied |
| Two concurrent corrections | one `200`, one `409` — never a `500`, never two revision 2s |
| A correction in `InReview` | `422` — fields editable only in `Draft`/`AwaitingInformation` |
| An Accountant correcting a Customer-visible field | `403`, and no Accountant id on any Customer `FieldValue` |
| An Accountant writing an Accountant-only field | `200` — §9.4, disjoint sets |
| A Customer-side caller writing an Accountant-only field | `403` |
| A Customer-side response | Accountant-only fields **absent from the JSON** |
| A `FileUpload` value naming a document from **another** ticket | `422`/`404` — the IDOR |

**Field validation (§6)**

| Case | Expected |
|---|---|
| A required **visible** field missing | `422` naming the field |
| **A required field hidden by conditional visibility, missing** | **`200`** — not required, §6.4 |
| A value for a conditionally hidden field | `422` |
| A value for an unknown `field_key` | `422`, not silently dropped |
| `MoneyAmount` of `0.10` | round-trips exactly — `NUMERIC`, not `float` |
| A catastrophically backtracking `RegexPattern` | `422` on timeout, **not** a hung process |
| `DateRange` with `to` before `from` | `422` |
| `MultipleChoice` with an option not in `ChoiceOptions` | `422` |

**Creation, assignment, concurrency, cancellation**

| Case | Expected |
|---|---|
| An `Employee` opens a ticket for a colleague | `403` — matrix §7, no on-behalf-of |
| A ticket with a `Departed` Subject | `422` — §9.6 rule 3 |
| A ticket with a Subject at another Customer | `404` |
| The reference format | `TKT-2026-000417` — six digits |
| 50 concurrent creations | 50 distinct references, no duplicate, no `500` |
| A rolled-back creation | leaves no `tickets` row, and the number **is** released to the next caller — §1.7 rule 5 |
| `precededBy` a non-`Closed` ticket | `422` |
| `precededBy` another Customer's ticket | `422` |
| `precededBy` a ticket the caller cannot see | `404`, not `403` |
| A linked continuation | copies **no** field values; uses the Type's **current active** version |
| Linking | grants **no** access to the predecessor |
| Any mutating request with a stale `Version` | `409` — not `422`, not `500` |
| Two Accountants closing the same ticket | one `200`, one `409` |
| An `AccountantUser` reassigns an `AccountantAdmin`'s ticket | `200` — §9.9 |
| Assigning to a suspended Accountant | `422` |
| Assigning to a `CustomerAdmin` | `422` |
| Every reassignment's audit entry | names **both** the previous and the new Assignee |
| `Answered → Closed` with an unverified required visible field | `422` — the closing rule |
| An `Employee` cancels their own `InReview` ticket | `403` — own drafts and own `Submitted` only |
| An `Employee` cancels their own `Submitted` ticket | `200` |
| A cancelled ticket | still readable; revisions, messages, documents all present |
| A cancelled ticket's Assignee | null, per `ck_tickets_assignee` |
| A `CustomerAdmin` sets priority | `403` — Accountant-only |
| Any update DTO | has no `customerId`, `subjectEmployeeId`, `ticketTypeId`, or `title` property |

**Documents through this slice (§0.3, §4.11)**

| Case | Expected |
|---|---|
| **A readable ticket paired with a document from an unreadable ticket** | **`404`** — §0.3 step 5, the IDOR |
| Download from a `Closed` ticket | `200` — matrix §8, a stated requirement |
| Upload to a `Closed` ticket | `422` |
| Every download | writes `DocumentDownloaded` |
| A download whose audit commit fails | the bytes are **not** streamed |
| `origin` supplied in the body | ignored; derived from the role |
| An Accountant's upload | `origin = AccountantResponse` |
| A Customer-side actor soft-deletes their own upload on a `Draft` | `200` |
| A Customer-side actor soft-deletes their own upload after pickup | `403` |
| A Customer-side actor soft-deletes **someone else's** upload | `403` |
| An Accountant soft-deletes any document on a visible ticket | `200` |
| A soft-deleted document | absent from `/list`, `404` on download — never `403` |
| An `Employee` downloads from a colleague's ticket | `404` |

**Structural**

| Case | Expected |
|---|---|
| The slice's source, grepped for `DELETE`/`Remove(` | zero matches |
| The slice's source, grepped for `UseXminAsConcurrencyToken` | zero matches |
| `Slices/Tickets/ExternalInterfaces/` | does not exist |
| `TicketsActionCatalogue` | no entry is `[AccountantAdmin]` alone |
| Append-only entities | no `Version` property on any of the five |
| Every mapped property | has an explicit `HasColumnName` — assert by reflection |
| Every denial | writes a `PermissionDenied` Audit entry |

### 11.3 The six tests that are easy to write wrongly

1. **The Accountant-cannot-see-a-`Draft` test.** Everyone writes the *Employee* draft test, which
   passes whether or not layer 3 is outside the `if`. Only the Accountant case catches §3.1 rule 2 —
   and that failure exposes every Customer's drafts to the whole Office.
2. **The correction-round-retains-acceptance test must inspect the `field_verifications` rows**, not
   just the API response, and must assert the **original verifier and timestamp**. A test that only
   checks "the field shows as accepted" passes when the correction re-accepted it under the
   corrector's own identity, which is a false audit record.
3. **The pickup-queue test needs a `Submitted` ticket WITH an Assignee.** The obvious fixtures are
   `Submitted`-with-null and `InReview`-with-active, and both pass with a naive
   `status == 'Submitted'` filter. The ticket that has been through a correction round is the one
   that fails it.
4. **The stranded-ticket audit test must assert the code is `TicketReassigned` and that the previous
   Assignee is named.** Asserting only that the pickup succeeded passes with a hardcoded
   `TicketAssigned`, which destroys the only record that work was taken from someone.
5. **The document IDOR test needs a ticket the caller MAY read paired with a document they may
   NOT.** Two tickets the caller cannot see tests nothing; that is what layer 1 already stops.
6. **The transaction test must query the database in a new scope** after the request completed, and
   must check **both** `tickets` and `ticket_revisions`. Checking the response status passes either
   way, and this is the test for §7.3 trap 1.

---

## 12. Known constraints

1. **A role change or suspension does not affect a live session for up to 8 hours**
   ([the Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §17). A demoted `CustomerAdmin` keeps
   full ticket visibility across their Customer until their cookie expires. There is no session
   revocation in v1.
2. **The pickup queue asks `Identity` for account status on every call** (§4.4). It is one bulk
   query, and it is deliberately uncached — but it is on the Office's hottest path, and the number
   of distinct open Assignees bounds it.
3. **`IIdentityApi.FindManyAsync` and `IEmployeeApi.FindManyAsync` cap at 500 ids** and throw above
   it. A 50-row page needs at most 50, so the list paths are safe — but the pickup queue's
   *distinct open Assignees* is unbounded in principle. It will not exceed 500 Accountants at
   one-Office scale; note it and move on.
4. **`Title` is computed at creation and never recomputed** (§4.1 rule 9). Renaming an Employee or a
   Ticket Type leaves old titles showing the old name. Arguably correct — the title records what the
   ticket was about when it was opened — but it will be reported as a bug. §13 item 7.
5. **Every concurrent creation serialises on one counter row.** The `ON CONFLICT DO UPDATE` of §1.7
   holds a row lock on `ticket_reference_counters` for the whole remainder of the creation
   transaction, so creations for the same year queue behind each other. At one-Office scale this is
   invisible; it is noted because the fix — allocate as late as possible in the transaction (§1.7
   rule 5) — is a design decision, not a tuning knob to reach for later.

   > **AMENDED.** This constraint previously read "the reference sequence has gaps", asserting that a
   > rolled-back creation consumes its number. That is false on PostgreSQL for a counter held in a
   > *table*: the `UPDATE` rolls back with everything else. See the amendment note in §1.7 rule 5.
   > Gaps can still appear from a committed ticket being hard-deleted, but nothing in this slice hard-
   > deletes a ticket, so in practice the sequence is dense.
6. ~~**`DueDateApproaching` exists in `NotificationEvents` and nothing produces it.**~~ **RESOLVED —
   a second `IHostedService` is authorized and this slice owns it.** §13 item 8, decided.
   `01-DomainModel.md` §9.2 has been amended to permit exactly two hosted services and to state that
   the rule it protects is about *removing* data, not about counting services. The scanner lives here
   because `tickets.due_date` is this slice's column and `Tickets → Notifications` is a permitted
   edge, where the reverse would be a cycle. **It is built as a separate, final piece of work, after
   the handlers and endpoints are green** — its design is specified in §9a, and the reminders are
   in-app only (`DueDateApproaching` stays out of `NotificationEvents.Emailed`; see the Notifications
   plan §3 rule 5 for why).
7. **A `Closed` ticket's conversation is split from its continuation.** §9.1 accepts this: *"The cost
   is that a conversation spanning a reopen is split across two Tickets, which is acceptable because
   the link makes the chain navigable."*
8. **A `CustomerAdmin` sees every ticket of their Customer, including payroll and personal tax
   data.** Matrix §6, a *"deliberate, accepted decision"*. There are no confidentiality flags, and
   adding one needs an explicit instruction.
9. **Optimistic concurrency covers the `tickets` row only** (§9.7). Two Accountants can append
   verifications to the same field concurrently and both succeed; the later one wins as "current".
   Correct by design — append-only tables have nothing to conflict on — but it means a `409` is not
   raised for every kind of simultaneous action.
10. **No full-text search.** Finding a ticket by its content is not supported; the reference,
    the Customer, the Subject, the Type, and the status are the filters. Out of scope for v1.
11. **No bulk operations.** Closing 50 tickets is 50 requests, each with its own `Version`.
12. ~~**`ITicketTypesApi` cannot resolve a version by id**~~ **RESOLVED 2026-09-02, before this slice
    was built.** `ITicketTypesApi.GetVersionByIdAsync(Guid ticketTypeVersionId, UserRole, ct)` now
    exists and returns `TicketTypeDetailDto?`, reusing the same `TicketTypeMapper` audience-filtering
    path as the by-number accessor. Both halves of §13 item 1 are done: the contract response types
    (`TicketTypeDetailDto`, `TicketTypeListItemDto` and the three types they expose transitively) were
    moved out of `TicketTypes.Application.Dtos` into `TicketTypes.ExternalInterfaces`, so this slice
    imports `...TicketTypes.ExternalInterfaces` **only** and dependency rule 2 holds in shipped code.
    The request DTOs stayed in `Application/Dtos`, where they belong.
13. **The due-date scanner skips unassigned tickets** (§9a.2 rule 5). A ticket approaching its due date
    with nobody assigned generates no reminder, because a reminder needs a recipient and broadcasting
    to every Accountant is worse. §4.4's pickup queue is where that ticket surfaces instead. Accepted;
    revisit if unassigned tickets are observed going past their dates in practice.
14. **The due-date scanner writes no audit entry** (§9a.2 rule 4), because there is no actor to name.
    It is the one write path in this slice with no audit trail, and closing that gap needs a
    system-actor concept in `Audit` that does not exist.

---

## 13. Questions to flag rather than answer

1. ~~**`ITicketTypesApi` has two problems and both block this slice**~~ **RESOLVED 2026-09-02, in
   `TicketTypes`, before this slice was built.** Both fixes landed exactly where this item said they
   belonged: `GetVersionByIdAsync(Guid ticketTypeVersionId, UserRole, ct)` was added, and the contract
   response types were moved from `Application/Dtos` into `ExternalInterfaces/`. See §6.1 and §12
   constraint 12 for what shipped. **This slice must import `...TicketTypes.ExternalInterfaces` and
   never `...TicketTypes.Application.Dtos`** — the latter now holds request DTOs only, and referencing
   it would re-break dependency rule 2.
2. **Matrix §7 lets a `CustomerAdmin` submit "any ticket of own Customer", but §0.4 layer 3 hides
   another person's `Draft` from them** (§4.2 rule 4). So the granted right is unreachable for
   drafts. Either the visibility rule has an exception for a Customer Admin submitting, or the
   submission right applies only to `AwaitingInformation`. The second reading is consistent and is
   what this plan assumes — **confirm it**, because the first would be a change to a LOCKED rule
   (§9.3).
3. **In `Draft`, does editing a field mutate revision 1 or append a revision?** (§4.5 rule 9.)
   Appending one per save produces dozens of revisions before submission; mutating revision 1 after
   submission would violate immutability. The coherent answer is *mutate while `Draft`, append
   thereafter* — but §3 says *"A revision, once written, is never modified"* without carving out
   `Draft`, so this needs confirming rather than assuming.
4. ~~**Should `field_values` have a `CHECK` tying the populated column to the data type?**~~
   **RESOLVED — add the weaker constraint, which is the half that can be expressed.** (§1.4 rule 3.)

   The strong form is genuinely impossible here: `field_values` has no `data_type` column, because the
   type lives on the descriptor in `TicketTypes`, so the database cannot know that this row was
   supposed to be a `WholeNumber`. That part of the original answer was correct.

   But the invariant *"at most one of the five primary carriers is populated"* holds for **all eleven
   data types**, so it can be checked without knowing which type a row is. Two constraints are now in
   the migration:

   - `ck_field_values_one_carrier` — at most one of `value_text`, `value_number`, `value_date`,
     `value_boolean`, `value_document_id` is non-null. `value_date_to` is excluded from the count
     because it is the companion of `value_date`, not a carrier; that is what lets `DateRange` pass.
     `MultipleChoice` passes because it serialises to a JSON array in `value_text` — one column, even
     though the value is not atomic. All five null is permitted, because a `Draft` may hold a blank
     answer.
   - `ck_field_values_date_range` — `value_date_to` non-null implies `value_date` non-null. A range
     with an end and no start is not a range, and the carrier count above cannot see it.

   This catches the more damaging half of the bug class: a switch that falls through and writes two
   carriers, producing a row with two answers where every reader picks a different one. It does not
   catch a value in the *wrong* single column, and nothing in this schema can. `FieldValueValidation`
   remains the only guard for that, which is why its `default` arm throws rather than storing an
   unvalidated value.

   **Not verified locally** — there is no PostgreSQL on the build machine, so both constraints are
   unexercised. `TicketsSchemaTests` must assert each one rejects a violating row.
5. **Two document questions** (§4.11 rules 5 and 6).
   (a) ~~May a Customer upload during `InReview`?~~ **DECIDED: yes — uploads follow the matrix, with
   no status qualifier.** Only the terminal statuses refuse an upload. See §4.11 rule 5, which now
   carries the rule and the reasoning.
   (b) **Deletion: accepted as this plan proposed, and still worth a second look.** The test is
   `Draft` or (`Submitted` with no Assignee), because *"before `InReview`"* is a claim about history
   and an Assignee is the durable trace of having been picked up. This was **not** separately
   confirmed — it was adopted because the plan's reasoning stands on its own and the alternative
   (status alone) is demonstrably wrong for a correction round. Implement it, and raise it again if a
   Customer ever reports being unable to remove a file they just attached to a returned ticket.
6. **What happens when an `Employee`-role caller has no Employee record?** (§3.1 rule 3.) It should
   be impossible — an account with role `Employee` is created by
   `IIdentityApi.InviteEmployeeAccountAsync`, which requires an `EmployeeId`. But "impossible" needs
   a behaviour: empty result, or `401`. Pick one deliberately; a permissive fallback is a
   cross-Customer read.

   > **DECIDED 2026-09-02: empty result**, at `TicketVisibility.cs`'s Employee branch, commented there.
   > It is a broken state, not a permissive one, so the query returns no rows rather than an unfiltered
   > set — which gives a uniform `404` on read and write paths alike, the same answer an out-of-scope
   > id gets. `401` was the alternative and remains defensible; it belongs at the resolve step in the
   > handlers rather than in a query builder shared by every path. Either way it is fail-closed, and
   > the test asserts the GUARANTEE (no cross-Customer rows) rather than the choice, so switching is a
   > one-file change.
7. **Should `Title` be recomputed when the Type or the Subject is renamed?** (§12 constraint 4.)
   Storing it is what makes lists readable without six joins, and a stale title is arguably the
   honest record. It will still be reported as a bug.
8. ~~**`DueDateApproaching` has no producer.**~~ **DECIDED: a second hosted service is authorized, and
   this slice owns it.** See §9a for its design and §12 constraint 6. Three documents were amended to
   make the authorization real rather than implied: `01-DomainModel.md` §9.2 (now enumerates both
   permitted services and re-states the rule as being about *removing* data), the Notifications plan
   §3 rule 5 and §13 items 6-7 (producer now exists, still not built there, and the kind stays out of
   `Emailed`), and this plan. **Build it last**, after the handlers and endpoints are green — it is the
   one piece of this slice that runs with no `CurrentUser` and no request, and it is much easier to
   reason about once the read paths it depends on exist.
9. **Is there a maximum number of revisions, messages, or documents per ticket?** All three are
   unbounded and all three are returned unpaginated by `/api/tickets/get`. A ticket with 200
   messages returns 200. A number would let the detail response be sized, or paginated.
10. **An Accountant cannot open a ticket of a type that has any required customer-visible field, and
    the ticket they create is stranded rather than refused.** Found and verified while reviewing the
    built slice, 2026-09-02. Three rules that are each correct on their own compose into a dead end:

    - §6.3 makes the two halves **disjoint** — `callerMayWrite = isAccountant ? !IsVisibleToCustomer
      : IsVisibleToCustomer` — and supplying the other half is a **403**, deliberately, so that no
      Accountant's identity can ever attach to a Customer-supplied value (§9.4).
    - the submission gate counts **only** customer-visible required fields
      (`TicketMapper.UnansweredRequiredVisibleFields` skips `!IsVisibleToCustomer`), which is right for
      the Customer-side flows it was written for.
    - visibility layer 3 makes a `Draft` visible to its **Creator only, in every role** (§9.3).

    So `SubmitImmediately: true` is a `422` the Accountant can never satisfy, and
    `SubmitImmediately: false` produces a `Draft` that only the Accountant can see and only the
    Customer can complete. There is no transition out: `RequestInformation` needs `InReview`, which
    needs the submission that is blocked. The only exit is `Cancel`.

    **Nothing was changed in code for this** — every rule involved is locked, and the fix is a product
    decision, not a refactor. The options, none of them free: forbid Accountant creation of such a
    type at the point of creation (a clear `422` beats a stranded row); or exempt an
    Accountant-created ticket from the customer-half required check until the Customer first sees it;
    or make an Accountant's `Draft` visible to the Customer side, which contradicts §9.3 and is the
    worst of the three. Note this is NOT reachable for a Customer-side creator, and not reachable for
    an Accountant on a type whose required fields are all Accountant-only — so a deployment may never
    hit it, depending entirely on how types are configured.
11. **A required `FileUpload` field makes `SubmitImmediately: true` impossible for anybody**, and this
    one is inherent rather than a rule collision. A document is uploaded **against an existing
    ticket**, so at creation there is no document that can belong to a ticket that does not exist yet.
    `CreateTicketHandler` therefore passes `documents: null` to the validator, and
    `FieldValueValidation.ValidateDocument` rejects a null dictionary with the same 422 as a document
    belonging to another ticket — correctly, since the alternative is trusting a caller-supplied id.
    With `enforceRequired: true` the required upload can never be satisfied.

    The flow that works is Draft → upload → submit, which is what a user would do anyway. **No change
    is needed**; it is recorded because "create and submit in one call" is documented as available and
    silently is not for these types, and because the 422's message ("the attached document was not
    found on this ticket") reads like a bug rather than like the constraint it is. Worth a clearer
    message at the creation path if this is ever hit in practice.

---

## Files checklist

| File | Action |
|---|---|
| `Slices/Tickets/Infrastructure/Migrations/20260904_001_CreateTicketsSchema.sql` | New — six tables + the counter |
| `Slices/Tickets/Core/Ticket.cs` | New (incl. `TicketStatus`, `TicketPriority`) |
| `Slices/Tickets/Core/TicketRevision.cs` | New |
| `Slices/Tickets/Core/FieldValue.cs` | New |
| `Slices/Tickets/Core/FieldVerification.cs` | New (incl. `VerificationOutcome`) |
| `Slices/Tickets/Core/TicketMessage.cs` | New (incl. `TicketMessageKind`) |
| `Slices/Tickets/Core/TicketMessageDocument.cs` | New |
| `Slices/Tickets/Infrastructure/TicketsDbContext.cs` | New |
| `Slices/Tickets/Infrastructure/Configurations/` — six files | New |
| `Slices/Tickets/Infrastructure/TicketReferenceAllocator.cs` | New — §1.7, the one piece of raw SQL. Also declares `ITicketReferenceAllocator`, added in review: `CreateTicketHandler` depended on the concrete class, whose single statement the in-memory provider cannot execute, so the handler that decides all six permanent values had **no test at all**. Registered as the interface in `TicketsRegistration`. Exactly one production implementation — do not add a second |
| `Slices/Tickets/Application/TicketVisibility.cs` | New — §3.1, the four layers |
| `Slices/Tickets/Application/TicketConcurrency.cs` | New — §3.2 |
| `Slices/Tickets/Application/TicketTransitions.cs` | New — §5, the closed table |
| `Slices/Tickets/Application/FieldValueValidation.cs` | New — §6 |
| `Slices/Tickets/Application/TicketMapper.cs` | New — role-shaped projections, `Expression<>` not `Func<>` |
| `Slices/Tickets/Application/TicketAccess.cs` | New — the load-and-404 sequence §0.3 steps 2–4 require on all 22 actions. Not in the original list; the alternative was repeating it in 21 handlers |
| `Slices/Tickets/Application/Dtos/` — 26 files | New |
| `Slices/Tickets/Application/Handlers/` — **21 classes** serving 22 actions | New. "18 ticket handlers + 4 document handlers" was the original count and is wrong twice over: 17 of the ticket handlers are separate classes and `PostMessageHandler` serves BOTH `PostMessage` and `PostInternalNote` through two entry points (§4.10 rule 3 — one class, two catalogued actions, no role branch inside), so 17 + 4 = 21 classes for 22 actions |
| `Slices/Tickets/TicketsActionCatalogue.cs` | New — 22 actions |
| `Slices/Tickets/TicketsRegistration.cs` | New |
| `Slices/Tickets/TicketsEndpoints.cs` | New — two route groups, §8 |
| `Slices/Tickets/Infrastructure/DueDateScanner.cs` | New — §9a, incl. `DueDateScannerOptions` in the same file (mirroring `OutboxDrainer.cs`) |
| `Slices/Tickets/Core/TicketDueDateReminder.cs` | New — §9a.3, the seventh entity. Three columns, no navigations |
| `Slices/Tickets/Infrastructure/Configurations/TicketDueDateReminderConfiguration.cs` | New — makes the `Configurations/` row above **seven** files, not six |
| `Slices/Tickets/Infrastructure/Migrations/20260905_001_CreateDueDateReminders.sql` | New — §9a.1, the **second** migration. Not a change to the first one, and no `.csproj` change: the existing `Slices\**\Infrastructure\Migrations\*.sql` glob already copies it |
| `appsettings.json`, `appsettings.Development.json` | Edit — the `Tickets:DueDateScanner` section, `Enabled: false` in both. Not in the original list |
| `Slices/TicketTypes/ExternalInterfaces/ITicketTypesApi.cs` | ~~Edit~~ — **done 2026-09-02**, §13 item 1. Not one file: the DTO move touched 13 API files and 2 test files, and added `ExternalInterfaces/TicketTypeDetailDto.cs` and `TicketTypeListItemDto.cs` |
| `Program.cs` | Edit — two lines |
| **Not this slice:** `Slices/Tickets/ExternalInterfaces/` | Must not exist — §0.2 |
| `AccountantApp.Tests/Tickets/` | New — §11. Includes `CreateTicketFlowTests.cs` and `DueDateScannerTests.cs`, neither of which the original §11 list anticipated |
| `AccountantApp.Tests/Employees/EmployeesTestHarness.cs` | Edit — `FakeIdentityApi.ListAccountantsAsync` returned a hardcoded empty list. Not a harmless stub: every handler that notifies the Office guards on `office.Count > 0`, so that branch was **dead in every test**, and "the whole Office is notified on submission" could not fail |

---

## Success criteria

1. The migration applies to a fresh PostgreSQL database; six tables, the counter table, all **twelve**
   `CHECK` constraints, all **six** intra-slice foreign keys, the two `UNIQUE` constraints
   (`uq_tickets_reference`, `uq_ticket_revisions_sequence`) and all **nine** indexes exist.

   > **AMENDED.** This read "eight `CHECK` constraints, both intra-slice foreign keys". Both numbers
   > were wrong against the DDL this same document specifies in §1.1–§1.6 — it defines ten named
   > `ck_*` constraints and six `REFERENCES` clauses, the latter because every child table points at
   > its parent, not just two of them. A criterion that counts is only useful if the count is right;
   > a builder who trusts "both" writes a test asserting two and passes while four foreign keys go
   > unchecked. Count from the migration, not from this sentence, if they ever disagree again.
   >
   > Ten became twelve when §13 item 4 was resolved in favour of adding
   > `ck_field_values_one_carrier` and `ck_field_values_date_range`.
   >
   > **AMENDED again, for §9a.** "Six tables" is a claim about the **first script**, and this criterion
   > is checked against the database **after** `SqlMigrationRunner` has applied every script in the
   > slice — so once §9a.1's second migration exists, the database has **seven tables plus the
   > counter**, and a test asserting the seventh is absent fails necessarily. The original test did
   > assert exactly that, and it had to be flipped. The claim §9a.1 actually makes — that the *first*
   > script does not create `ticket_due_date_reminders` — is a statement about source text, so assert it
   > by reading the two scripts, which also works on a machine with no PostgreSQL. Read this criterion
   > as: seven tables plus the counter, of which the first script creates six plus the counter.
2. `ck_tickets_assignee` accepts a `Submitted` ticket **with** an Assignee, and rejects `InReview`
   without one.
3. A rejected verification with a null or whitespace-only reason is refused by the database.
4. 50 concurrent creations produce 50 distinct references of the form `TKT-2026-000417`, with no
   duplicate and no `500`.
5. A rolled-back creation leaves no `tickets` row and no `ticket_revisions` row, and does not reuse
   the reference number.
6. `.WhereTicketVisible(user)` is the only path to a ticket, and layer 3 is outside the
   role-specific branch: an `AccountantAdmin` gets `404` on a Customer's `Draft`.
7. An `Employee` who is the Subject of a `Draft` gets `404`; the same ticket at `Submitted` returns
   `200`.
8. An `Employee` gets `404` on a colleague's ticket; a `CustomerAdmin` sees all of their Customer's,
   including a `Departed` Employee's, permanently.
9. A newly invited Employee's pre-existing non-`Draft` tickets are readable with **no `UPDATE`** to
   `tickets`.
10. Internal notes are absent from the JSON for both Customer-side roles and present for both
    Accountant roles, filtered by an allow-list.
11. Message `kind` and document `origin` are both derived from the caller's role and ignored if
    supplied in the body.
12. Every transition in the closed table is permitted, every pair outside it is `422`, and there is
    no path out of `Closed` or `Cancelled`.
13. `Answered → InReview` succeeds; `Closed → InReview` is `422`.
14. `Submitted → InReview` without an Assignee is rejected by the handler and by the constraint.
15. `AwaitingInformation → Submitted` retains the Assignee, and the resulting ticket is **not** in
    the pickup queue.
16. Every transition writes a `SystemEvent` message with a null author **and** an Audit entry.
17. The pickup queue returns `Submitted`-with-no-Assignee and open-with-inactive-Assignee, in one
    paginated query, and nothing else.
18. Taking a ticket from a different inactive Assignee audits `TicketReassigned` naming both
    Assignees — not `TicketAssigned`.
19. An `AccountantUser` may take a stranded ticket and may reassign an `AccountantAdmin`'s ticket;
    no action in `TicketsActionCatalogue` is `AccountantAdmin`-only.
20. Suspending an Accountant changes no ticket's status or Assignee.
21. A correction appends a revision containing a row for **every** descriptor, leaves the previous
    revision byte-for-byte unchanged, and sets `is_carried_forward` correctly.
22. A field accepted in the previous revision and carried forward unchanged is still `Accepted`,
    **with the original verifier and timestamp**; a rejected one is carried forward **unverified**.
23. An Accountant cannot write a Customer-visible field value, and no Accountant id appears on any
    Customer-supplied `FieldValue`; an Accountant **can** write an Accountant-only field.
24. Accountant-only fields are absent from every Customer-side response and are never required for a
    Customer-side submission.
25. A required field hidden by conditional visibility is **not** required, and a value supplied for a
    hidden field is `422`.
26. `MoneyAmount` round-trips exactly through `NUMERIC(18,4)`; no `float` or `double` appears in the
    slice.
27. A catastrophic `RegexPattern` yields `422` on timeout, not a hung process.
28. Every mutating request carries `Version`; a stale one is `409`, never `422` or `500`; `Touch` is
    called on every write to the `tickets` row; `UseXminAsConcurrencyToken` appears nowhere.
29. An `Employee` cannot open a ticket for a colleague, and a `Departed` Employee cannot be the
    Subject of a new one while existing ones are untouched.
30. `precededBy` validates same-Customer and `Closed` with `422`, an unseeable predecessor with
    `404`, copies no field values, uses the Type's current active version, and grants no access to
    the predecessor.
31. An `Employee` may cancel their own `Draft` and `Submitted` tickets and nothing else; a cancelled
    ticket stays fully readable with a null Assignee.
32. There is no reopen endpoint, no `Reopened` status, no `ReopenedAt` column, no delete endpoint, no
    `DELETE` statement, and no `Remove()` call.
33. All six steps of §0.3 run on all four document handlers; a readable ticket paired with another
    ticket's document returns `404`.
34. Downloading from a `Closed` ticket succeeds; uploading to one is `422`; every download audits and
    commits **before** streaming.
35. A soft-deleted document is absent from the list and `404` on download, never `403`.
36. `Slices/Tickets/ExternalInterfaces/` does not exist, and no `ITicketApi` is defined.
37. The four document actions exist in `TicketsActionCatalogue` and in no other catalogue.
38. Every mapped property has an explicit `HasColumnName`, asserted by reflection; no append-only
    entity has a `Version` property.
39. Every denial writes a `PermissionDenied` Audit entry, and every write in §4.0 F writes its
    entry.
40. `ITicketTypesApi` has been amended per §13 item 1 before this slice is considered complete.
