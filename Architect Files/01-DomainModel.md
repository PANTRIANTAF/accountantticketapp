# Domain Model

Terms are as defined in [00-Glossary.md](00-Glossary.md). Permissions are in
[02-AuthorizationMatrix.md](02-AuthorizationMatrix.md) and are not repeated here.

This document describes **what each entity is for and what it holds**. It does not
prescribe table names, column types, indexes, or ORM configuration.

---

## 1. The shape of the world

```
Office  (the deployment — NOT an entity, NOT a table, NOT a foreign key)
 │
 ├── Accountant Admins ──────── UserAccount (role: AccountantAdmin)  ← at least one
 ├── Accountant Users ───────── UserAccount (role: AccountantUser)   ← zero or more
 │        └── is the Assignee of ──► Ticket
 │
 └── Customer (a business — tenant boundary)              ← many
      │
      └── Employee ─────────── UserAccount (role: CustomerAdmin | Employee)   [optional]
           │                    ← many per Customer; at least one is a CustomerAdmin
           │
           └── is the Subject of ──► Ticket
                                      │
                                      ├── TicketType  ──► FieldDescriptor[]
                                      ├── TicketRevision[]  ──► FieldValue[]
                                      │                          └── FieldVerification
                                      ├── TicketMessage[]
                                      └── Document[]
```

Cardinality, stated plainly because it constrains every schema decision below:

- **One Office.** It is the deployment. It has no row anywhere.
- **Many Accountants**, of two kinds: at least one `AccountantAdmin`, plus any number of
  `AccountantUser`. Both are plain UserAccounts; there is still no `Accountant` table.
- **Many Customers per Office**, each a business.
- **Many Employees per Customer**, at least one of whom is a Customer Admin.
- **Many Tickets per Customer**, each with exactly one Employee as its Subject.

Three rules that govern everything:

1. **Every Customer-side entity is reachable to exactly one Customer.** Tickets,
   Employees, Documents, Notifications. Any query made by a Customer-side actor must be
   filtered to their Customer. There are no cross-Customer reads for Customer-side
   actors, ever.
2. **Accountants are not scoped to a Customer.** Both Office roles read across all of them.
3. **Nothing is scoped by Office.** Since there is one Office and it is the deployment,
   an `OfficeId` column anywhere is a mistake. Do not add one "for future
   multi-tenancy" — that is a different application.

---

## 2. Identity entities

### UserAccount

Credentials and role. The only entity that can log in.

- Login identifier (email address; unique across the whole system)
- Credential material (password hash and whatever the hashing scheme needs)
- Display name of the person, for showing authorship on tickets and messages
- Role: exactly one of `AccountantAdmin`, `AccountantUser`, `CustomerAdmin`, `Employee`
- Link to an Employee record. Set for `CustomerAdmin` and `Employee` accounts, and
  **absent for both Accountant roles** — an Accountant works for the Office, not for a
  Customer, and therefore has no Employee record.
- Status: `Invited`, `Active`, `Suspended`
- Email confirmation state
- Timestamps for creation, last successful login, last password change
- Failed-login counter and lockout expiry

A UserAccount in `Invited` status has no usable credential yet — it exists so the
invitation can be tracked and so the login identifier is reserved. It cannot
authenticate. It becomes `Active` when the person completes the invitation and sets a
password.

`Suspended` accounts cannot authenticate but are never deleted, because their audit
history and authored tickets must remain attributable.

**A UserAccount is never deleted.** Deactivation is a status change.

### Accountants — deliberately not a separate entity

**There is still no `Accountant` table.** An Accountant is simply a UserAccount whose role
is `AccountantAdmin` or `AccountantUser`, carrying its own display name and contact email,
with no Employee link. Adding an `Accountant` entity would duplicate what UserAccount
already holds.

Rules a builder must honour:

- **At least one `Active` `AccountantAdmin` must exist at all times.** Any operation that
  would leave zero — suspending the last one, or demoting it to `AccountantUser` — is
  rejected. This mirrors the identical rule for Customer Admins.
- The **first** `AccountantAdmin` is created by **seeding**, because no one exists yet with
  the authority to create it. Every subsequent Accountant account, of either role, is
  created by an existing `AccountantAdmin` through the normal invitation flow.
- An `AccountantAdmin` may promote an `AccountantUser` to `AccountantAdmin`, and may demote
  another `AccountantAdmin`, subject to the at-least-one rule.
- **An Accountant Admin cannot suspend, demote, or delete their own account.** Self-action
  on one's own role or status is rejected, exactly as for Customer Admins. This is what
  prevents an accidental total lockout.
- Accountant accounts are never deleted. Suspension is the only offboarding mechanism,
  because their verifications, responses, and audit entries must remain attributable.

### Why `AccountantUser` exists

The two Office roles differ in **four powers only**, all reserved to `AccountantAdmin`:

1. Create a Customer
2. Suspend or reactivate a Customer
3. Create, invite, suspend, promote, or demote Accountant accounts
4. Read the audit log

Everything else is identical. In particular an `AccountantUser` **can** do all of the
following, and a builder must not restrict them: pick up, verify, respond to, and close
tickets for any Customer; register and invite Employees at any Customer; edit a Customer's
contact details; and author and version Ticket Types.

That last one is deliberate and worth noting because it is surprising: **Ticket Type
authoring is not admin-only.** An Accountant User can change the ticket form catalogue,
and those changes apply to every Customer. It was chosen this way because form maintenance
is routine accounting work rather than administration.

### Employee

A person who works for a Customer. **This entity exists independently of any
UserAccount** — that separation is the single most important structural decision in
this model, because it is what makes on-behalf-of ticketing possible.

- Owning Customer
- Given name, family name
- Work email (used as the login identifier if and when they are invited; may be absent
  for an accountless Employee)
- Personal identifying numbers the Office needs for accounting work (tax identification
  number, social-security number, and similar)
- Job title
- Employment start date, employment end date (end date absent while still employed)
- Status: `Active`, `Departed`
- Optional link to a UserAccount — absent for an accountless Employee

**Scope decision:** an Employee record belongs to exactly one Customer. If the same
natural person works for two Customers of the Office, that is two independent Employee
records with no link between them. This is deliberate — it keeps Customer isolation
absolute. The cost is that such a person needs two logins; that is accepted.

The Customer Admin role is not a separate entity. A Customer Admin **is** an Employee
whose UserAccount has role `CustomerAdmin`. This means a Customer Admin can also be the
Subject of their own tickets, which is required.

### Customer

A **business** that is a client of the Office — always a company, never a natural
person. A Customer has many Employees and at all times at least one Customer Admin.

- Legal name, trading name
- Tax identification / VAT number, and the tax office it belongs to
- Registered address, contact phone, contact email
- Status: `Active`, `Suspended`. A suspended Customer's actors cannot log in and no new
  tickets may be opened for it; existing tickets remain readable by Accountants.
- Date the Customer was onboarded

**Customers are never deleted.** Accounting records have legal retention periods.
Suspension is the only offboarding mechanism.

---

## 3. Ticket Type entities

Ticket Types are the app's configuration surface. They are authored by Accountants and
are **global** — every Customer sees the same catalogue.

### TicketType

- Code (stable, machine-readable, never changes — e.g. `PAYROLL_CERTIFICATE`)
- Display name and description shown in the type picker
- Category, for grouping in the picker
- Whether an Employee may open this type directly, or only a Customer Admin may. Some
  requests are employer business, not employee business.
- Whether a Subject other than the Creator is allowed
- Active flag — an inactive type cannot be chosen for new tickets but existing tickets
  of that type keep working
- Version number

### TicketType versioning — required behaviour

A Ticket must render and validate against **the Field Descriptors that were in effect
when it was created**, forever, even after the Ticket Type has changed.

Therefore: a TicketType has a sequence of **versions**, each version carrying its own
complete set of Field Descriptors. A Ticket stores a reference to the specific
*version* it was created against, not just to the type. Editing a type's fields creates
a new version; it never mutates an existing one.

Getting this wrong means old tickets render with the wrong fields or crash. It is not
optional.

### FieldDescriptor

One input on one version of a Ticket Type.

- Key (stable within the type version; this is what a FieldValue points at)
- Label and help text shown to the person filling the form
- Display order, and optional group/section heading
- Data type — one of: single-line text, multi-line text, whole number, decimal number,
  money amount, date, date range, yes/no, single choice from a list, multiple choice
  from a list, file upload
- Choice options, when the data type is a choice
- Required or optional
- Validation rules appropriate to the data type: minimum and maximum length, minimum
  and maximum value, earliest and latest date, regular expression pattern, allowed file
  types, maximum file size
- Conditional visibility: an optional rule of the form "only show and only require this
  field when field *X* has value *Y*". A field hidden by its condition is treated as
  not required.
- Whether the field is visible to the Customer side at all, or is a field only
  an Accountant fills in during processing

Validation rules declared here are enforced **on the server**. The React app enforces
them too, for feedback, but the server is the authority and never trusts the client.

---

## 4. Ticket entities

### Ticket

The request itself.

- Ticket Reference — human-readable, unique, generated on creation, never reused and
  never changed. Format `TKT-{year}-{zero-padded sequence}`, e.g. `TKT-2026-000417`.
- Owning Customer
- Ticket Type, and the specific Ticket Type **version**
- Creator — the UserAccount that submitted it
- Subject — the Employee the ticket is about
- Status — see the lifecycle below
- **Assignee** — the Accountant responsible for the work. Absent while the Ticket is
  `Draft`, `Submitted`, or `Cancelled`; **required** in `InReview`, `AwaitingInformation`,
  `Answered`, and `Closed`. Either Accountant role may be an Assignee.
- Priority: `Normal`, `High`. Set by an Accountant only.
- Due date — optional, set by an Accountant
- Title — derived from the Ticket Type name plus the Subject, so lists are readable
  without opening each ticket
- Timestamps: created, last activity, closed
- Reference to the current TicketRevision
- **Version** — an integer, starting at 1, incremented on every write to the Ticket row.
  This is the optimistic-concurrency token; see section 9.7.
- **Preceded-by Ticket** — optional. Set at creation only, when this Ticket continues a
  matter whose Ticket is already `Closed`. See section 9.1; a `Closed` Ticket is never
  reopened, so this link is how a chain is continued.

A Ticket's Customer, Type, Type version, Creator, Subject, and Preceded-by Ticket are
**immutable after creation**. If any of them is wrong, the Ticket is cancelled and a new one is
opened. The Assignee, by contrast, is expected to change over a Ticket's life.

### Assignment rules

Assignment is **required on pickup**. There is no such thing as a Ticket being worked on
without exactly one named Accountant accountable for it.

- Moving a Ticket from `Submitted` to `InReview` **must** set an Assignee in the same
  operation. A request that would leave it null is rejected. The two are one atomic
  action, not a status change followed by an optional assignment.
- The normal path is **self-assignment**: an Accountant picks the Ticket up and becomes
  its Assignee. Any Accountant may also assign a Ticket to a different Accountant.
- **Reassignment** is permitted in any non-terminal status and by any Accountant, for
  absence and workload. The target must be an `Active` Accountant of either role.
- An Assignee stays set through `AwaitingInformation` — waiting on the Customer does not
  release the Ticket back to the pool, because the person who asked the question is the
  person who should read the answer.
- A `Closed` Ticket is **never reopened**, so there is no reopen-time assignment rule. A
  continuation is a new Ticket linked by Preceded-by, with its own assignment history. See
  section 9.1.
- When an Accountant is suspended, their open Tickets **keep pointing at them**. Their
  assignments are not silently redistributed and nothing moves automatically. What surfaces
  them is the shared pickup queue, which returns any open Ticket whose Assignee is not
  `Active`; taking one is audited as a reassignment naming the previous Assignee. See
  section 9.8.
- Assignment and reassignment each write a `SystemEvent` message and an Audit Entry, and an
  Audit Entry for a reassignment names **both** the previous and the new Assignee.

Assignment does **not** restrict permissions. Any Accountant may read and act on any
Ticket regardless of who holds it; the Assignee records accountability, not exclusivity.
Concurrent action by two Accountants is handled by optimistic concurrency on the Ticket row —
see section 9.7.

### TicketRevision

An immutable snapshot of all Field Values for a Ticket at one moment.

- The Ticket it belongs to
- Sequence number, starting at 1
- The actor who submitted it, and when
- Optional note explaining what changed, written by the submitter

Revision 1 is created together with the Ticket. Every correction round appends a new
revision. **A revision, once written, is never modified and never deleted.** To see
what an Employee originally claimed, you read revision 1.

### FieldValue

One answer within one revision.

- The TicketRevision it belongs to
- The Field Descriptor key it answers
- The value — stored in a form that preserves the declared data type; a file-upload
  field's value is a reference to a Document
- Whether this value was carried forward unchanged from the previous revision or was
  newly entered in this revision

### FieldVerification

An Accountant's judgement on one Field Value.

- The FieldValue being judged
- Outcome: `Accepted` or `Rejected`
- Rejection reason — required when rejected, and shown verbatim to the Customer side so
  they know what to fix
- The Accountant who judged it, and when

A FieldValue with no FieldVerification is unverified. Verifications attach to a
FieldValue in a specific revision, so the verification history of a corrected field is
fully preserved.

**Closing rule:** a Ticket cannot move to `Closed` while any required, visible
FieldValue in the current revision is unverified or rejected.

### TicketMessage

The conversation on a Ticket. One entity covers all message kinds, distinguished by a
kind field.

- The Ticket
- Author UserAccount
- Kind: `CustomerMessage`, `AccountantResponse`, `InternalNote`, `SystemEvent`
- Body text
- Attached Documents
- Created timestamp

`SystemEvent` messages are written by the application, not a person, to render status
changes inline in the conversation ("Status changed to Awaiting Information").

`InternalNote` messages are visible **only** to Accountants. This must be enforced on
the server by filtering, not by the React app choosing not to display them.

Messages are append-only. They are not edited or deleted.

---

## 5. Ticket lifecycle

### Statuses

| Status | Meaning |
|---|---|
| `Draft` | Being filled in by its Creator. Not visible to any Accountant. No Assignee. |
| `Submitted` | Sent to the Office, in the shared pool. **Not yet assigned.** |
| `InReview` | Its Assignee is working on it. |
| `AwaitingInformation` | One or more fields were rejected, or the Assignee asked a question. The Customer side must act. Assignee retained. |
| `Answered` | The Assignee has delivered the response and/or documents, and considers the work done. Awaiting close. |
| `Closed` | Terminal, permanently. Work finished. Read-only, documents still downloadable. Never reopened — a continuation is a new Ticket (section 9.1). |
| `Cancelled` | Terminal. Abandoned or opened in error. No work was delivered. |

### Transitions

| From | To | Triggered by | Conditions |
|---|---|---|---|
| — | `Draft` | Creator | |
| — | `Submitted` | Creator | All required visible fields valid |
| `Draft` | `Submitted` | Creator | All required visible fields valid |
| `Draft` | `Cancelled` | Creator | |
| `Submitted` | `InReview` | Accountant | **Must set an Assignee in the same operation** |
| `Submitted` | `Cancelled` | Creator, Customer Admin, Accountant | |
| `InReview` | `AwaitingInformation` | Accountant | At least one field rejected, or a question posted. Assignee retained. |
| `InReview` | `Answered` | Accountant | No rejected or unverified required visible fields |
| `InReview` | `Cancelled` | Accountant | |
| `AwaitingInformation` | `Submitted` | Creator, Customer Admin | A new revision was submitted, or a reply posted. **Assignee retained — does not return to the pool.** |
| `AwaitingInformation` | `Cancelled` | Customer Admin, Accountant | |
| `Answered` | `Closed` | Accountant | |
| `Answered` | `InReview` | Accountant | Reopening before close, e.g. the response was wrong |

**The table is complete and closed. There is no row whose `From` is `Closed` or `Cancelled`.**

Rules that follow from the table:

- **Only an Accountant may close a Ticket.** The Customer side never closes.
- No transition out of `Cancelled`. It is absolutely terminal.
- **No transition out of `Closed` either.** `Closed` is exactly as terminal as `Cancelled`.
  There is no reopen endpoint and no configured window — a continuation is a new Ticket
  carrying a Preceded-by link (section 9.1). Do not add a `Closed → InReview` row, a
  `Reopened` status, or a `ReopenedAt` timestamp.
- Field values are editable only in `Draft` and `AwaitingInformation`. In every other
  status the current revision is frozen.
- Every transition writes a `SystemEvent` TicketMessage and an Audit Entry.
- **`AwaitingInformation` → `Submitted` is the one place the status name misleads.** The
  Ticket returns to `Submitted` but keeps its Assignee, so it is *not* back in the
  unassigned pool. Any "needs pickup" list must therefore filter on `Submitted` **with no
  Assignee**, not on `Submitted` alone. Filtering on status alone is the most likely bug
  in this state machine.
- The same pickup list must **also** return open Tickets whose Assignee is not `Active`, which
  is the only thing that surfaces work stranded by a suspension (section 9.8). So the pickup
  query has two conditions, and neither of them is "status equals `Submitted`" on its own.

### The correction round, end to end

1. Ticket is `InReview`. Accountant rejects field `iban` with reason "IBAN has 26
   characters, expected 27".
2. Accountant moves the Ticket to `AwaitingInformation`. A Notification goes to the
   Creator and, if different, to the Subject if the Subject has an account.
3. The Customer side opens the Ticket and sees the rejection reason against that field.
4. They submit a correction. This creates **revision 2**: unchanged fields are carried
   forward and flagged as carried-forward, the corrected field holds the new value.
   Revision 1 is untouched and remains readable.
5. Ticket returns to `Submitted`. The corrected field is unverified in revision 2.
   Fields accepted in revision 1 and carried forward unchanged **retain their accepted
   state** — do not force an Accountant to re-verify what did not change.
6. Accountant verifies the corrected field and proceeds.

---

## 6. Document entities

### Document

- Owning Customer, and the Ticket it belongs to
- Origin: `CustomerUpload` or `AccountantResponse`
  - `CustomerUpload`: evidence or supporting files uploaded by the Customer side
  - `AccountantResponse`: documents attached by an Accountant as part of their response
    (always pre-made files, **never generated by the app**)
- Original file name, content type, size in bytes
- A reference to where the bytes are stored — reached only through the `Documents`
  slice's storage interface. The rest of the system must not depend on the mechanism.
  See [04-Infrastructure.md](04-Infrastructure.md) section 7.
- Uploader UserAccount and upload timestamp
- Soft-delete flag, with who deleted it and when. **Documents are never hard-deleted** —
  retention is indefinite, so the row and its bytes are kept permanently and the flag only
  hides it. `Document` is the **only** entity in this system with a soft delete; see section
  9.2 for who may set it and for the mandatory global query filter that keeps a forgotten
  `WHERE` clause from serving a deleted file.

**There is no document generation, no templates, no WYSIWYG editor.** An Accountant
serving a ticket attaches pre-made PDFs, certificates, or other documents. The app
stores and serves them; it does not produce them. This dramatically simplifies the
Documents slice.

**There is no virus scanning, and no scan state.** This is a deliberate decision, not an
omission — do not add a `ScanState` field, a quarantine status, or a "pending scan"
condition on download. Uploads are defended by content-type allow-listing and a maximum
size only, per the rules below.

Because there is no scanner, upload hygiene carries the whole defence and must be
enforced **on the server**, never on the declared content type alone:

- An **allow-list** of accepted types, not a block-list. PDFs, common image formats, and
  office documents as needed — nothing else.
- The type is validated against the file's actual content, not the client-supplied
  `Content-Type` header or the file extension.
- A maximum size, enforced at both the proxy and the application.
- Downloads are always served as an attachment with an explicit content type, **never
  inline**, so a malicious upload cannot execute in the browser against this app's own
  origin. This matters more than usual here: the SPA and the API share an origin, so an
  HTML or SVG file rendered inline would run as same-origin script.
- The stored filename is never used as a filesystem path, and is sanitised before being
  echoed back in a download header.

Every download is recorded as an Audit Entry. This is a requirement, not a nicety —
these files contain personal tax and payroll data.

---

## 7. Notification

- Recipient UserAccount
- The Ticket it concerns
- Event kind, from a fixed catalogue defined in the Notifications slice spec
- Title and body
- Read flag and read timestamp
- Created timestamp
- Email delivery state, when the event kind is also emailed

An accountless Employee has no UserAccount and therefore receives no notifications.
When a Ticket's Subject is accountless, notifications about it go to the Creator. This
is a real consequence of the accountless model and must be handled explicitly rather
than producing an orphaned notification.

---

## 8. AuditEntry

Immutable. Append-only. Never updated, never deleted.

- Acting UserAccount, and the role it held at the time
- Customer in scope, when applicable
- Action code, from a fixed catalogue
- Target entity kind and identifier
- Before and after values for changes to existing data
- Timestamp, source IP address, user agent

Minimum set of audited actions: authentication attempts and outcomes, Employee
registration and invitation, UserAccount status changes, **Accountant account creation and
every role promotion or demotion**, Ticket creation, every Ticket status transition, **every
assignment and reassignment**, every field verification, every Document upload and download,
every Ticket Type version change, and every permission-denied response.

Accountant role changes matter most of all here. Promotion to `AccountantAdmin` grants the
power to create further Accountant accounts, so an unaudited promotion is an unauditable
escalation path.

---

## 9. Resolved behavioural decisions

**All ten are now LOCKED.** This section previously listed nine open questions; they were
decided in full before the per-slice implementation plans were written. Nothing here is open,
and nothing here may be re-litigated by a slice plan — remember that a plan under `Slices/`
loses to this document, so a plan that contradicts one of these decisions is wrong, not new.

If you find a behaviour that none of these ten cover, that is a **new** gap: flag it, do not
invent it.

---

### 9.1 A `Closed` Ticket is never reopened — LOCKED

`Closed` is terminal. There is no reopen endpoint, no reopen window, and no transition out of
`Closed` in the table in section *Transitions*.

When a Customer needs to revisit a closed matter they **create a new Ticket** that references
the old one, through a nullable `PrecededByTicketId` on `Ticket`:

| Rule | Detail |
|---|---|
| Column | `preceded_by_ticket_id UUID NULL REFERENCES tickets(id)` |
| Who may set it | Whoever may create the Ticket, at creation only. It is immutable thereafter, like Customer and Type. |
| Validation | The referenced Ticket must exist, must belong to **the same Customer**, and must be `Closed`. Any other value is `422`. A reference to a Ticket the caller cannot see is `404`, not `403` — the usual scope rule. |
| Effect on authorization | None. The new Ticket's visibility is computed from its own Customer and Subject. Linking does **not** grant access to the predecessor. |
| Effect on data | None is copied. Field values are not carried forward; the new Ticket starts empty at its Type's current active version. |

Why this rather than a duration: a reopen window forces a position on which revision becomes
current again, what happens if the TicketType version changed while the Ticket was closed, and
whether a suspended Assignee is restored — three problems that the link approach does not have.
The cost is that a conversation spanning a reopen is split across two Tickets, which is
acceptable because the link makes the chain navigable.

**Do not** add a `Reopened` status, a `ReopenedAt` timestamp, or a `Closed → InReview`
transition.

### 9.2 Retention is indefinite. Nothing is hard-deleted, and only Document has a soft delete — LOCKED

**Retention is indefinite.** No row in this database is ever removed by the application.

1. **Nothing is hard-deleted, ever.** Not a Ticket, TicketRevision, FieldValue,
   FieldVerification, TicketMessage, Document, AuditEntry, Customer, Employee, or UserAccount.
   No slice issues a SQL `DELETE`, and no handler calls `Remove()` on a tracked entity.
2. **There is no background retention job and no scheduled purge.** No `IHostedService`, no cron
   entry, and no container whose purpose is to delete data.

   > Precisely: what is forbidden is background work that **removes** data. Two `IHostedService`
   > implementations are permitted, both inside the existing `app` container, both adding no
   > container, and neither deleting a domain row:
   >
   > 1. The `Notifications` email outbox drainer.
   > 2. The `Tickets` due-date scanner, which produces `DueDateApproaching`
   >    (`Slices/Tickets/IMPLEMENTATION_PLAN.md` §13 item 8, **authorized**). It lives in `Tickets`
   >    because `tickets.due_date` is that slice's column and `Tickets → Notifications` is a
   >    permitted edge, where the reverse would be a cycle.
   >
   > **The rule that matters is the one about deletion, not the one about counting.** A third hosted
   > service that reads and notifies needs an explicit authorization like this one; a hosted service
   > that removes data does not get one. Do not read either of these as precedent for a purge job.
3. **A data-deletion request is handled out of band** by the Office operator, against the
   database, as a deliberate manual act. The application's stated position is that accounting
   records carry a legal retention minimum that overrides an in-app deletion request, so the
   app does not offer an endpoint it would have to refuse.
4. The entities with a lifecycle end — Customer, Employee, UserAccount — end by **status**
   (`Suspended`, `Departed`), never by removal.

#### The one exception: Document soft delete

`Document` alone carries a soft-delete flag, exactly as the Document entity in section 6
specifies: **who deleted it and when**. This is deliberate and narrow, and the permissions are
already fixed by [02-AuthorizationMatrix.md](02-AuthorizationMatrix.md):

| Who | May soft-delete |
|---|---|
| `AccountantAdmin`, `AccountantUser` | Any Document on a Ticket they can see. |
| `CustomerAdmin`, `Employee` | Their **own** uploads only, and only while the Ticket has not yet reached `InReview`. |

- The bytes are **not** removed. A soft-deleted Document keeps its row and its stored content.
- There is no undelete endpoint, and no hard-delete endpoint that finishes the job later.
- Soft-deleting a Document writes an Audit Entry, like every other document operation.
- No other entity in the system gets a soft-delete flag. If a second one seems to need it,
  that is a new gap — flag it.

#### How the filter is not forgotten — mandatory

A soft-delete column's real cost is that every query must exclude the deleted rows, and
forgetting once serves a file a user was told was gone. Discipline is not the mechanism. The
`Documents` slice must make the exclusion **structural**:

1. The `Document` entity's EF configuration declares a **global query filter** —
   `HasQueryFilter(d => d.DeletedAt == null)` — so the default for every LINQ query in the slice
   is already correct and a handler that forgets the clause still behaves.
2. Any query that deliberately needs deleted rows opts in explicitly with
   `IgnoreQueryFilters()`. At the time of writing **no handler needs this**, so a use of
   `IgnoreQueryFilters()` should be treated as a mistake until a spec says otherwise.
3. **The download path re-checks `DeletedAt` at download time**, not only at link-issue time.
   This is the same rule as the existing one that document access is re-authorized on download
   rather than trusted from a previously issued URL — a link handed out before the delete must
   stop working after it.
4. A test asserts that a soft-deleted Document is absent from the ticket's document list **and**
   that downloading it returns `404` — not `403`, which would confirm it exists.

### 9.3 A Draft is invisible to its Subject Employee — LOCKED

A Draft Ticket created by a Customer Admin on behalf of an Employee is **not** visible to that
Employee before submission, even though they are the Subject.

Drafts are private to their Creator. This is the same rule as
[02-AuthorizationMatrix.md](02-AuthorizationMatrix.md) already states — no Accountant ever sees
a Draft either — extended to the one case the matrix did not name. The Subject link starts
granting visibility at `Submitted`.

Concretely: any query that returns Tickets to a caller who is not the Creator must exclude
`Draft`. A Subject-scoped query is not an exception to that.

### 9.4 An Accountant may never edit a Field Value — LOCKED

An Accountant rejects a field with a reason and waits for a correction. They do not overwrite
what the Customer claimed. There is no handler, no endpoint, and no code path by which an
Accountant's identity ends up attached to a Customer-supplied FieldValue.

The reason is attribution: the whole point of a FieldValue plus a FieldVerification is that the
record says *this party claimed this, and that party accepted or rejected it*. An Accountant
edit collapses both sides into one and makes the audit trail a lie.

**Do not confuse this with the rule it looks like.** Two different things, both true:

| Field kind | Who may write it |
|---|---|
| A normal field on the Type | The Customer side only (Employee, or Customer Admin on their behalf). An Accountant may only verify. |
| A FieldDescriptor marked Accountant-only | The Accountant only. The Customer side never sees it, let alone writes it. |

So "an Accountant may edit Accountant-only fields" and "an Accountant may not edit a Field
Value" do not conflict: they are about disjoint sets of fields.

### 9.5 An invited Employee gains read access to Tickets where they are the Subject — LOCKED

When an accountless Employee is later invited and their UserAccount is created, they
immediately gain read access to every non-Draft Ticket where they are the Subject, including
ones created before they had an account.

No data migration, no backfill, no linking step. The `SubjectEmployeeId` link already exists on
those Tickets; access is computed from it at query time. If you find yourself writing an
`UPDATE` that stamps the new account onto old Tickets, the model has been misunderstood — the
Ticket points at the **Employee**, which is independent of the UserAccount.

Draft Tickets remain invisible to them, per 9.3.

### 9.6 A `Departed` Employee's Tickets stay visible to their Customer Admin — LOCKED

Departure changes the Employee's status and nothing else. Their Customer Admin keeps full
visibility of all their Tickets, permanently — retention is indefinite (9.2), so "for the
retention period" means forever.

1. **No Ticket is hidden, closed, reassigned, or deleted because its Subject departed.**
2. A `Departed` Employee's own UserAccount, if they had one, is `Suspended` — so they lose
   their own access. The Customer Admin's access is unaffected.
3. A `Departed` Employee may not be the Subject of a **new** Ticket. Existing ones are
   untouched.

### 9.7 Optimistic concurrency on the Ticket row, `409` on conflict — LOCKED

Assignment is accountability, not a lock, so two Accountants can act on one Ticket at the same
time. The mutable Ticket row is protected with an explicit version:

| Rule | Detail |
|---|---|
| Column | `version INTEGER NOT NULL DEFAULT 1` on `tickets` |
| Incremented | By the handler, on **every** write to the `tickets` row — status transition, assignment, reassignment. |
| Sent by the client | Every mutating Ticket request carries the `Version` it read. |
| On mismatch | `AppException(409)`. The client re-reads and retries. A `409` is not a `500` and not a `422`. |
| Hand-maintained | An ordinary `integer` column the handler increments, **not** `UseXminAsConcurrencyToken()`. This codebase hand-writes its DDL; an opaque provider-specific token that the SPA has to round-trip does not belong in the contract. |

**Only the `tickets` row needs this.** TicketRevision, FieldValue, FieldVerification and
TicketMessage are append-only: concurrent writes to them interleave, they never overwrite, so
there is nothing to conflict on. Do not put a version column on an append-only table.

### 9.8 Stranded Tickets appear in the shared pickup queue, and taking one is an audited reassignment — LOCKED

A suspended Accountant keeps their assignments — they are never silently redistributed. What
makes them visible is the ordinary "needs pickup" queue, extended:

The pickup queue returns a Ticket when **either**

1. its status is `Submitted` **and** it has no Assignee, **or**
2. it is in any open status **and** its Assignee's UserAccount is not `Active`.

> Filtering on status alone is the most likely bug in this state machine, and this rule is the
> second half of that trap. `AwaitingInformation → Submitted` retains the Assignee, so
> condition 1 must test the Assignee too. Condition 2 requires asking `Identity` for account
> status — a dependency the table in
> [03-SliceInventory.md](03-SliceInventory.md) already permits.

Any Accountant — not only an `AccountantAdmin` — may take a Ticket surfaced by condition 2.
That is deliberate: work stranded by a suspension should not wait for an Admin. It is reconciled
with "not silently redistributed" by making it **not silent**:

3. Taking a Ticket whose Assignee is a different, inactive user is audited as a
   **reassignment**, and the audit entry names the previous Assignee as well as the new one.
   It is not recorded as a plain pickup.
4. Nothing happens automatically. Suspension does not clear an Assignee, does not change a
   status, and does not reassign anything.

### 9.9 Any Accountant may reassign any Ticket, including to themselves — LOCKED

Confirmed as originally permitted. An Accountant User may take a Ticket from another Accountant
without asking, and may assign one to a colleague.

This follows from two locked rules that are already in [README.md](README.md): assignment
records accountability rather than exclusivity, and any Accountant may act on any Ticket.
Restricting reassignment to `AccountantAdmin` would create a **fifth** Admin-only power and
contradict the locked "exactly four powers" list, so it is not an option.

Every reassignment writes an audit entry naming **both** the previous and the new Assignee.
Attribution is preserved by the audit log, not by withholding the operation.

### 9.10 Document generation — LOCKED: NO GENERATION

Accountants upload pre-made documents as responses. The app stores and serves them; it does not
produce PDFs, certificates, or any other documents. No templates, no WYSIWYG editor.
