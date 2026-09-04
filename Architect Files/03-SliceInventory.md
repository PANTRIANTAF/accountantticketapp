# Slice Inventory

The backend is one ASP.NET Core Minimal API project divided into vertical slices, with the
internal structure defined in [App/GeneralAppArchitecture.md](App/GeneralAppArchitecture.md).

This document is the **complete and closed list of slices**. Do not invent a new slice. If
a piece of functionality seems not to fit any slice below, that is a gap in this document —
flag it rather than creating a slice.

---

## 1. The slices

| Slice | Owns | Does not own |
|---|---|---|
| `Identity` | UserAccount. Authentication, invitations, password reset, session issuing, role assignment, and seeding the first Accountant Admin. | Employee records, Customer records. There is no Accountant entity to own — both Office roles are plain UserAccounts. |
| `Customers` | Customer (a business). The Customer record, contact details, suspension. | Employees. **Also not the composite onboarding operation** — see the note below the table. |
| `Employees` | Employee. Registration, editing, departure, and requesting that Identity create an account for one. **Plus the composite Customer-onboarding operation** — see the note below the table. | UserAccount itself. |
| `TicketTypes` | TicketType, its versions, FieldDescriptor. Authoring, versioning, activation, and serving the field schema to the UI. | Ticket data. |
| `Tickets` | Ticket, TicketRevision, FieldValue, FieldVerification, TicketMessage. The lifecycle state machine, field validation against a type version, and the correction round. | Field descriptor definitions, file bytes, notification delivery. |
| `Documents` | Document. Upload, storage, and authorized download. No virus scanning. | Which ticket a document is allowed to be read through — that authorization comes from `Tickets`. |
| `Notifications` | Notification. The event catalogue, creation, read state, and email delivery. | Deciding when a domain event happened — callers tell it. |
| `Audit` | AuditEntry. Append and query. | Anything else. It is write-only from every other slice's perspective. |

### Customer onboarding is one operation, and it lives in `Employees` — LOCKED

[02-AuthorizationMatrix.md](02-AuthorizationMatrix.md) section 3 is normative: *"Creating a
Customer includes registering and inviting its first Customer Admin, in one operation — a Customer
with no way to log in is useless."*

The `Customers` slice **cannot** do that. It needs an Employee record and a UserAccount plus an
invitation, and `Employees → Customers` already exists, so `Customers → Employees` would be a
cycle — forbidden by dependency rule 1.

So the composite operation lives in **`Employees`**, which already depends on `Customers`,
`Identity`, and `Notifications` — every slice the operation needs, with no new edge and no new
architectural concept:

1. `Customers` owns `/api/customers/create`, which creates **only** the Customer row. It is a
   building block, it is `AccountantAdmin`-only, and it is correct on its own.
2. `Employees` owns the composite endpoint, which calls `ICustomerApi` to create the Customer,
   creates the first Employee, and asks `Identity` to invite them — **in one request-scoped
   transaction**, so a failure at any step leaves no Customer behind.
3. The endpoint is `AccountantAdmin`-only, because step 1 is (section 3 of the matrix reserves
   creating a Customer to `AA`). It does not become an `AccountantUser` power by being wrapped.

The cost is accepted and is worth stating so nobody "fixes" it: an endpoint that brings a Customer
into existence is registered by a slice that does not own Customers. That is surprising when read
cold, and the route name must make it obvious rather than hiding it. It is preferred over three
separate calls from the SPA, which would lose atomicity and can leave exactly the state the matrix
calls "useless".

## 2. Dependency graph

An arrow means "may call, through the callee's `ExternalInterface`".

```
                    ┌──────────┐
                    │  Audit   │◄──────── every slice
                    └──────────┘

                 ┌───────────────┐
                 │ Notifications │◄─── Tickets, Employees, Identity
                 └───────────────┘

  ┌──────────┐        ┌───────────┐        ┌─────────────┐
  │ Customers│◄───────│ Employees │───────►│  Identity   │
  └──────────┘        └───────────┘        └─────────────┘
       ▲                    ▲                     ▲
       │                    │                     │
       └────────────┬───────┴─────────────────────┘
                    │
              ┌───────────┐        ┌─────────────┐
              │  Tickets  │───────►│ TicketTypes │
              └───────────┘        └─────────────┘
                    │
                    ▼
              ┌───────────┐
              │ Documents │
              └───────────┘
```

**The table below is authoritative, not the diagram.** The diagram predates two added edges
(`Tickets → Identity` and `Identity → Customers`) and does not show them. Read the table.

| Slice | May depend on |
|---|---|
| `Audit` | nothing |
| `Notifications` | `Audit` |
| `Customers` | `Audit` |
| `Identity` | `Customers`, `Notifications`, `Audit` |
| `TicketTypes` | `Audit` |
| `Documents` | `Audit` |
| `Employees` | `Customers`, `Identity`, `Notifications`, `Audit` |
| `Tickets` | `Employees`, `Customers`, `TicketTypes`, `Documents`, `Notifications`, `Identity`, `Audit` |

Two edges in that table were **added** after the original graph was drawn. Both are recorded here
because a reader comparing the table with the diagram above will notice them, and because each
exists to satisfy a specific normative rule.

`Tickets → Identity` was added when the behavioural decisions were locked. Section 9.8 of
[01-DomainModel.md](01-DomainModel.md) requires the pickup queue to return Tickets whose
Assignee's UserAccount is not `Active`, and account status is owned by `Identity`. The edge is
acyclic — `Identity` reaches only `Customers`, `Notifications`, and `Audit`, none of which reaches
`Tickets`. `Tickets` uses it for **account status and Accountant display names only**, through
`Identity`'s `ExternalInterface`; it never reads a UserAccount row.

`Identity → Customers` was added for the login check. [02-AuthorizationMatrix.md](02-AuthorizationMatrix.md)
section 11 requires that a Customer-side actor may log in only when **their Customer is also
`Active`**, and that suspending a Customer immediately blocks login for every Customer Admin and
Employee belonging to it. That check runs in `Identity`'s login handler, and Customer status is
owned by `Customers`. The edge is acyclic — `Customers` depends on `Audit` alone, which reaches
nothing. `Identity` uses it for **login-time Customer status only**, through
`ICustomerApi.IsActiveAsync`; it never reads the `customers` table and never caches the answer.

> **Do not denormalise a Customer's status onto `user_accounts`.** A cached copy is a second
> source of truth that goes stale at exactly the moment it matters — the moment a Customer is
> suspended. The status is read at login, every time.

## 3. Dependency rules

1. **The graph is acyclic and the table above is exhaustive.** A dependency not listed is
   forbidden. If you believe you need one, that is a design problem to raise, not to
   implement.
2. **All cross-slice calls go through the callee's `ExternalInterface`.** A slice never
   references another slice's `Core` entities, `Application` handlers, or `Infrastructure`.
   Never.
3. **No slice reaches upward.** `Customers` must never call `Employees`, and `TicketTypes`
   must never call `Tickets`, even though the reverse dependencies exist.
4. **An `ExternalInterface` returns its own small contract types**, not the slice's
   internal entities. `Employees` exposes something like an employee summary — identifier,
   name, owning Customer, whether they have an account — not the `Employee` entity.
5. **`Audit` is fire-and-forget from the caller's point of view.** No slice's business
   logic branches on what `Audit` returns.
6. **When two slices both need a value object, it goes in `Shared/ValueObjects/`**, defined
   once — never copied into each slice. See
   [App/GeneralAppArchitecture.md](App/GeneralAppArchitecture.md) section 4 for what may and
   may not live in `Shared/`.
7. **A slice may define an interface for another slice to implement — an *inverted* dependency.**
   This is how a lower slice obtains data from a higher one without an edge that would create a
   cycle. The interface lives in the **defining** slice's `ExternalInterfaces/`, and the
   implementing slice registers the implementation in its own `{Slice}Registration.cs`.

   The one instance in v1: `Notifications` needs a recipient's email address, but it may depend
   only on `Audit`, and `Identity → Notifications` already exists so the reverse edge would be a
   cycle. So `Notifications` defines `IRecipientDirectory` and **`Identity` implements it**. The
   reference direction is still `Identity → Notifications`, exactly as the table permits.

   Rules: the defining slice never references the implementation; the contract returns the
   defining slice's own small types, never the implementer's entities; and an unregistered
   implementation must fail at **startup**, not on first use. Do not invent a second inverted
   interface without raising it — the pattern is easy to abuse into a hidden cycle.

## 4. Where cross-cutting behaviour lives

These do not belong to any slice and must not be duplicated inside them. Their concrete
placement is specified in [App/GeneralAppArchitecture.md](App/GeneralAppArchitecture.md); this
list exists so a builder recognizes them as shared rather than reimplementing each one
per slice.

- Authentication, and the resolution of the current caller into role plus scope
- The **Customer scope filter** — a single shared mechanism, applied consistently.
  Re-implementing scope filtering per slice is the most likely way this application leaks
  data between Customers.
- Authorization policy evaluation against [02-AuthorizationMatrix.md](02-AuthorizationMatrix.md)
- Request validation and the uniform error response shape
- Exception handling
- Pagination, filtering, and sorting contracts
- Logging and correlation identifiers
- Date, time, and timezone handling
- Transaction boundaries per request

## 5. Locked platform decisions

These are settled. See [App/GeneralAppArchitecture.md](App/GeneralAppArchitecture.md) for details.

- **Runtime:** .NET 10
- **Database:** PostgreSQL 16, via `Npgsql.EntityFrameworkCore.PostgreSQL`
- **ORM:** Entity Framework Core, for querying and mapping only. **EF Core migrations are
  not used.** `Microsoft.EntityFrameworkCore.InMemory` must not be referenced by the API
  project — it is a test-only dependency.
- **Cross-slice calls:** Interface-based (`ICustomerApi`, etc.). Each slice defines its public
  contract as interfaces; other slices inject and call. Implementation stays in the slice.
- **Migrations:** SQL scripts in `Slices/{Slice}/Infrastructure/Migrations/`, named with a
  datetime prefix for execution order, e.g. `20260828_001_CreateCustomersTable.sql`, applied
  at startup by a shared runner that records applied scripts in a `schema_versions` table.
- **Shared code:** `Shared` folder at the root of the API project.
- **Slice registration:** each slice contains **one `{Slice}Registration.cs` at its root**
  exposing `Add{Slice}Slice(IServiceCollection, IConfiguration)`. It registers that slice's
  DbContext, every handler, and its `ExternalInterface` implementation — and nothing it does
  not own. `Program.cs` calls it once per slice and never names a handler or DbContext type
  itself. See [App/GeneralAppArchitecture.md](App/GeneralAppArchitecture.md) section 7.
- **DbContext:** One per slice. Separate contexts enforce architectural boundaries and prevent
  accidental cross-slice entity loading.
- **API route shape:** `/api/{domain}/{action}`, e.g. `/api/customers/create`, `/api/tickets/pickup`.
- **Transaction boundaries:** Per-request, per-slice. Cross-slice transactions are not supported
  (see [App/GeneralAppArchitecture.md](App/GeneralAppArchitecture.md) section 5).
- **Document production:** Accountants upload pre-made documents only. No generation, templates,
  or WYSIWYG editing in the app. Documents slice stores and serves files.

## 6. Behavioural decisions — all resolved

This section used to list open questions. **There are none left.** All ten behavioural
questions are decided and LOCKED in [01-DomainModel.md](01-DomainModel.md) section 9, which is
the authoritative text. Read the decision there; do not re-decide it in a slice plan, which
loses to document 1.

The table below exists only so a builder knows which slice has to *implement* each decision.

| 01-DomainModel §9 decision | Implemented by |
|---|---|
| 9.1 — `Closed` is never reopened; continuation is a new Ticket with `PrecededByTicketId` | `Tickets` |
| 9.2 — retention indefinite, nothing hard-deleted, Document soft delete is the only one | `Tickets`, `Documents` |
| 9.3 — a Draft is invisible to its Subject Employee | `Tickets` |
| 9.4 — an Accountant may never edit a Customer-supplied Field Value | `Tickets` |
| 9.5 — an invited Employee gains read on Tickets where they are Subject, no backfill | `Employees`, `Tickets` |
| 9.6 — a `Departed` Employee's Tickets stay visible to their Customer Admin | `Employees` |
| 9.7 — optimistic concurrency on the `tickets` row, `409` on mismatch | `Tickets` |
| 9.8 — stranded Tickets appear in the shared pickup queue; taking one is an audited reassignment | `Identity`, `Tickets` |
| 9.9 — any Accountant may reassign any Ticket, including to themselves | `Tickets` |
| 9.10 — no document generation | `Documents` (nothing to build) |

Two decisions cross a slice boundary and are the most likely to be built inconsistently:

- **9.8** requires `Tickets` to learn an Assignee's account status from `Identity`. That edge did
  not exist before this decision and was **added** to the table in section 2 — see the note
  under it. `Tickets` must not query a UserAccount table directly; it goes through `Identity`'s
  `ExternalInterface`.
- **9.5** requires that nothing is backfilled when an Employee gains an account. If a plan
  contains an `UPDATE` that stamps a new UserAccount onto existing Tickets, it is wrong: the
  Ticket points at the **Employee**, which is independent of the UserAccount.

The first-Accountant-Admin seeding method is **no longer open**: configuration binding, from
environment variables in production and `appsettings.json` locally. See
[App/GeneralAppArchitecture.md](App/GeneralAppArchitecture.md) section 9.
