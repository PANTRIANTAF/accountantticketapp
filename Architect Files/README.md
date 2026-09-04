# Architecture Documentation — Index

This folder is the specification for the AccountantApp. It is written to be read and
implemented by an AI coding agent. Read the documents in the order listed below.

## Reading order

| # | Document | What it defines |
|---|---|---|
| 0 | [00-Glossary.md](00-Glossary.md) | Every term used in these docs. Read first; the word "admin" is ambiguous and this file disambiguates it. |
| 1 | [01-DomainModel.md](01-DomainModel.md) | The actors, the entities, their relationships, and the ticket lifecycle. |
| 2 | [02-AuthorizationMatrix.md](02-AuthorizationMatrix.md) | Who is allowed to do what. Normative. |
| 3 | [03-SliceInventory.md](03-SliceInventory.md) | Which vertical slices exist and which may depend on which. |
| 4 | [04-Infrastructure.md](04-Infrastructure.md) | Containers, topology, hosting, storage, secrets. |
| 5 | [App/GeneralAppArchitecture.md](App/GeneralAppArchitecture.md) | Backend structural conventions. |
| 6 | [UI/README.md](UI/README.md) | Frontend architecture and per-screen specifications. Start at `UI/README.md`; read `UI/GeneralUIArchitecture.md` and `UI/LoginArchitecture.md` in full, then **only** the one screen spec for the slice you are building. The `Tickets`, `Documents` and Employee-home screens are still unwritten — their slices are unbuilt. |
| 7 | [UI/Plans/README.md](UI/Plans/README.md) | The frontend **build** plans — Phase 0 (the `frontend/` scaffold, the API client, the session, the shared components, the login screens) plus one per built slice. Read Phase 0 in full, then **only** your slice's plan. These sequence the specs in row 6; they do not restate them, and they lose to them wherever the two disagree. |
| 8 | [Slices/](Slices/) | One `IMPLEMENTATION_PLAN.md` per backend slice — eight of them, all written. Read **only** the one for the slice you are building; they are ~1,200–2,100 lines each and reading all eight is not useful. See the build order below. |

### Slice build order

Build in this order. It is the dependency order from
[03-SliceInventory.md](03-SliceInventory.md) §2 — every slice's dependencies ship before it.

| # | Slice | Plan | Depends on |
|---|---|---|---|
| 1 | `Audit` | [Slices/Audit](Slices/Audit/IMPLEMENTATION_PLAN.md) | nothing |
| 2 | `Notifications` | [Slices/Notifications](Slices/Notifications/IMPLEMENTATION_PLAN.md) | `Audit` |
| 3 | `Customers` | [Slices/Customers](Slices/Customers/IMPLEMENTATION_PLAN.md) | `Audit` |
| 4 | `TicketTypes` | [Slices/TicketTypes](Slices/TicketTypes/IMPLEMENTATION_PLAN.md) | `Audit` |
| 5 | `Identity` | [Slices/Identity](Slices/Identity/IMPLEMENTATION_PLAN.md) | `Customers`, `Notifications`, `Audit` |
| 6 | `Employees` | [Slices/Employees](Slices/Employees/IMPLEMENTATION_PLAN.md) | `Customers`, `Identity`, `Notifications`, `Audit` |
| 7 | `Documents` | [Slices/Documents](Slices/Documents/IMPLEMENTATION_PLAN.md) | `Audit` |
| 8 | `Tickets` | [Slices/Tickets](Slices/Tickets/IMPLEMENTATION_PLAN.md) | every other slice |

Two of these plans register endpoints in **another slice's** route namespace, and both are
deliberate. Do not "tidy" either one; each is explained in the plan that owns it.

- `Employees` registers `/api/customers/onboard` — creating the first Customer Admin needs
  edges that `Customers` does not have, and giving it those edges would be a dependency cycle.
- `Tickets` registers `/api/documents/*` — a document's access rules come entirely from its
  ticket, and `Documents` may not depend on `Tickets`. **`Documents` has no endpoints at all**,
  and is the only slice in the system without them.

### Files in this folder that are *not* specification

Two extensionless files predate the numbered documents and are the original author's
working notes. They are **non-normative** and no document should link to them:

| File | Superseded by |
|---|---|
| `App/GeneralAppArchitecture` (no extension) | [App/GeneralAppArchitecture.md](App/GeneralAppArchitecture.md) |
| `UI/TicketArchitecture` (no extension) | [UI/Screens/](UI/Screens/) for the six built slices. It remains the only recorded intent for the **ticket** screens, which are still unwritten. |

`UI/GeneralUIArchitexture.txt` (note the misspelling) and `UI/LoginArchitecture.txt` were
0-byte placeholders and have been **deleted**, replaced by
[UI/GeneralUIArchitecture.md](UI/GeneralUIArchitecture.md) and
[UI/LoginArchitecture.md](UI/LoginArchitecture.md). Leaving an empty misspelled file beside the
real one is exactly the trap this section warns about.

## How this spec is used

1. **First implementation:** A code-generating model (e.g., Haiku) reads these docs and
   builds an initial implementation.
2. **Review and feedback:** You review the implementation, correct it, and note what the
   docs should have said more clearly.
3. **Iterative refinement:** Each cycle, the docs improve. Gaps become visible only when
   a builder tries to use them. This is intentional.

This means these docs are **executable and will be tested**. Ambiguities will surface.

## Conflict precedence

If two documents disagree, the **lower-numbered document wins**. If a document in
`App/` or `UI/` contradicts documents 0–4, documents 0–4 win. A per-slice implementation
plan under `Slices/` loses to every numbered document. Do not resolve a contradiction by
inventing a third behaviour — flag it.

## Locked platform decisions

These are settled. Do not revisit them.

- **Backend:** .NET 10, ASP.NET Core Minimal API, Entity Framework Core with PostgreSQL 16
  (`Npgsql.EntityFrameworkCore.PostgreSQL`), vertical-slice architecture as described in
  [App/GeneralAppArchitecture.md](App/GeneralAppArchitecture.md).
- **Migrations:** SQL scripts per-slice with datetime-prefixed filenames, executed in order
  by a custom runner. **EF Core migrations are not used** — `dotnet ef migrations add` and
  `dotnet ef database update` are never run in this repository.
- **`Microsoft.EntityFrameworkCore.InMemory` is banned from the API project.** Tests only.
  It ignores column names and constraints, so it passes on schemas PostgreSQL rejects.
- **Cross-slice calls:** Interface-based. Each slice defines public contract interfaces;
  other slices inject and call.
- **Slice self-registration:** every slice has one `{Slice}Registration.cs` exposing
  `Add{Slice}Slice(...)` that registers its own DbContext, handlers, and `ExternalInterface`
  implementation. `Program.cs` gets two lines per slice — `Add{Slice}Slice()` and
  `Map{Slice}Endpoints()` — and names no handler or DbContext type directly. **One exception:**
  `Documents` has no endpoints and contributes one line; `Tickets` registers `/api/documents/*`.
- **Frontend:** React single-page application in TypeScript, separate source project,
  consuming the API over HTTP. The stack is **Vite, TypeScript, MUI (Material UI), TanStack
  Query, React Router, React Hook Form + Zod**; `@mui/x-data-grid` is banned because every list
  in this API is server-paginated. Source lives in **`frontend/`** at the repository root, which
  the production Dockerfile hard-codes. **There is no API base-URL environment variable** — the
  SPA always calls `/api/...` relative to its own origin, in every environment. See
  [UI/README.md](UI/README.md).
- **Hosting:** Docker Compose on one host. **Production is exactly three containers:**
  `caddy` (TLS, the only container publishing a port), `app` (the API *and* the built SPA
  served from its `wwwroot`), and `db` (PostgreSQL, on an internal-only network). There is
  no separate UI container and no nginx. Development runs the SPA separately for hot
  reload — React dev server + API on the host, Postgres in a container. See
  [04-Infrastructure.md](04-Infrastructure.md).
- **The app is internet-facing.** Only the proxy publishes a port. Rate limiting on auth
  endpoints and no-user-enumeration behaviour are mandatory, not hardening extras.
- **Document bytes live in PostgreSQL**, behind the `Documents` slice's storage interface.
- **No virus scanning.** Deliberate. Uploads are defended by a content-type allow-list
  and a size cap. Do not add a scan state.
- **All API routes live under `/api`.** Non-negotiable, from the first commit; everything
  not under `/api` is the SPA. This is what keeps the UI-in-API-container decision cheap
  to reverse. Path segments are lowercase and **kebab-case at every word boundary** —
  `/api/ticket-types/list`, never `/api/tickettypes/list`, whose doubled `t` is an
  invisible typo waiting to happen.
- **Session auth is an `HttpOnly` `Secure` `SameSite=Strict` cookie.** Never a token in
  `localStorage`. There are **no JWTs and no bearer tokens** anywhere, and therefore no
  signing key to configure. CORS is disabled in production.
- **Development-only authentication is header-driven and double-gated.** Until the `Identity`
  slice ships, a `DevAuth` scheme lets `X-Dev-Role` choose the caller — active only when
  `IsDevelopment()` **and** `DevAuth:Enabled` (a key that exists in
  `appsettings.Development.json` alone) are both true. It sets a principal; it never skips
  `IPermissionChecker`. It is deleted in the commit that adds real cookie login. See
  [App/GeneralAppArchitecture.md](App/GeneralAppArchitecture.md) section 9.
- **Every unhandled exception becomes a `ProblemDetails` `500`.** The exception middleware has
  a `catch (Exception)` block, not only `catch (AppException)`. Anything a client can trigger
  by sending a value — an over-length string, an unparseable regex — is a `4xx`, never a `500`.
- **Migrations are tracked by slice-relative path**, never by bare filename: sequence numbers
  restart at `001` in every slice, so filenames collide and a filename key silently skips the
  second slice's script.
- **Authorization is fail-closed.** One injected `IPermissionChecker` in
  `Shared/Authorization`; an unrecognised action name **denies**, and every denial is
  audited. Never write a default branch that allows. See
  [02-AuthorizationMatrix.md](02-AuthorizationMatrix.md).
- **Out-of-scope resources return `404`, not `403`.** A `403` confirms the row exists.
- **Deployment model:** one deployment serves exactly **one accounting Office**, run by
  exactly **one Accountant**. The Office is the operator of the instance, not a tenant
  inside it. The tenant boundary is the **Customer**.
- **Customers are businesses.** A Customer is always a company, never a natural person.
  Each Customer has one or more Employees.
- **Four roles:** `AccountantAdmin`, `AccountantUser`, `CustomerAdmin`, `Employee`. The two
  Office roles are collectively called *Accountants* and differ in **exactly four powers**,
  all reserved to `AccountantAdmin`: create a Customer, suspend a Customer, manage Accountant
  accounts, and read the audit log. Everything else — including serving tickets and authoring
  Ticket Types — is open to both.
- **At least one `Active` Accountant Admin always exists.** The first is seeded; further
  Accountant accounts are invited by an existing Admin. No Admin can suspend or demote
  themselves.
- **Ticket assignment is required on pickup.** Moving a ticket to `InReview` must set an
  Assignee in the same operation. Assignment records accountability, not exclusivity — any
  Accountant may act on any ticket.
- **Customer Admin visibility:** a Customer Admin has full visibility of all tickets
  belonging to their own Customer, including field values.
- **Ticket data history:** ticket field values are stored as **immutable revisions**.
  Nothing is ever overwritten.

## Status of this documentation

**Platform decisions are locked** in documents 0–4 and
[App/GeneralAppArchitecture.md](App/GeneralAppArchitecture.md): route shape
(`{domain}/{action}`), DbContext strategy (one per slice), migrations (SQL with datetime
prefix, no EF migrations), cross-slice calls (interface-based), container topology (three
containers), session auth (cookie, no JWT), and document handling (upload only, no
generation, bytes in PostgreSQL).

**Behavioural decisions are also locked now.** The nine open questions that used to sit in
[01-DomainModel.md](01-DomainModel.md) §9 have all been decided; §9 is the authoritative text
and [03-SliceInventory.md](03-SliceInventory.md) §6 maps each decision to the slice that
implements it. The headline outcomes, because they overturn earlier drafts:

- **A `Closed` Ticket is never reopened.** No reopen endpoint, no window, no `Closed → InReview`
  transition. A continuation is a new Ticket carrying a `PrecededByTicketId`.
- **Retention is indefinite and nothing is hard-deleted.** No purge job, no scheduler.
  `Document` has the only soft-delete flag in the system, enforced with a global query filter.
- **Optimistic concurrency on the `tickets` row**, a hand-maintained `version` column, `409` on
  mismatch. Append-only tables get no version column.
- **Stranded Tickets surface in the shared pickup queue**, so the pickup query has two
  conditions and neither is "status equals `Submitted`" alone.

**All eight per-slice implementation plans are now written.** Every slice in
[03-SliceInventory.md](03-SliceInventory.md) has one, at uniform depth: schema, entities, DTOs,
handlers with rules, cross-slice boundaries, registration, endpoints, migrations, tests, known
constraints, and a "questions to flag rather than answer" section. See the build order above.

**Locked is not the same as complete, and written is not the same as verified.** Two things to
know before building:

- **No part of any schema has ever been applied to a real PostgreSQL database.** Docker has not
  started on the authoring machine, so every `CREATE TABLE`, `CHECK`, and index in all eight
  plans is unverified. Apply each slice's migration first and fix the script before trusting its
  DDL.
- **Each plan ends with a "questions to flag rather than answer" section, and those are real.**
  They are not rhetorical. Several are build blockers — the `Tickets` plan's first item is a
  change `TicketTypes` needs before `Tickets` can be built at all. Read that section before
  writing code for a slice, not after.

A builder hitting a gap should **flag it, not invent a behaviour** — that is how these docs get
corrected.

**The frontend specification is now written**, in [UI/](UI/): the two cross-cutting documents
(`GeneralUIArchitecture.md`, `LoginArchitecture.md`), one screen spec per built slice under
`UI/Screens/`, seven **build plans** under [UI/Plans/](UI/Plans/README.md) — Phase 0 plus one per
built slice — and `UI/BACKEND_CHANGES_REQUIRED.md`, a ranked punch-list of the backend work the UI
depends on. Three items in that punch-list block deployment, and the first of them is three lines in
`Program.cs`. Read it early.

The split between the two UI layers matters and is not a filing convention: a screen spec says *what*
a screen is, a build plan says *in what order its files get created and how each step is verified*.
Plans cite the specs by section rather than restating them, because the restatement is the copy that
goes stale.

**Writing the punch-list and the plans found thirty-three backend items**, of which seven were found
only by the plans — each by checking one claim in a screen spec against the handler that implements
it, one route at a time. None was visible from reading either document alone. Two are worth naming
here:

- **Item 27 is the only one that can reach a state where nobody can administer the instance.**
  Suspending an `Invited` Accountant Admin is allowed, and reactivating it is then allowed too,
  producing an `Active` account with a null `PasswordHash` — an account that can never sign in. The
  last-Admin invariant counts only `Status == Active` (`AccountInvariants.cs:37-40`), so that
  unusable account satisfies it, and the real last Admin who *can* sign in becomes suspendable. A
  comment in the code asserts the database prevents this; the constraint it names is a vocabulary
  check, not a state-machine one. The fix is a single guard.
- **Item 28 silently revokes access on a successful edit.** `CreateTicketTypeRequestDto` defaults its
  two access flags to `true`; `EditTicketTypeRequestDto` declares the same fields with no
  initialiser. An edit that omits a flag therefore turns it off and mints a version recording that as
  intended.

One item found this way is already fixed. Item 26 — `POST /api/employees/reinstate` and
`/change-login-email` requiring action names absent from `EmployeesActionCatalogue.cs`, so that
`IPermissionChecker`'s fail-closed path returned `403` to every caller including an
`AccountantAdmin` — was **resolved on 2026-09-02**. Two caveats: the fix is uncommitted, and
`Slices/Employees/` is untracked in git, so nothing about it can be recovered from history.

Worth noting how item 26 stayed hidden, because the same gap can recur: nothing links a handler's
`RequireAsync(user, "…")` string literal to the catalogue at compile time, and the
`PermissionChecker` constructor's startup validation catches a *duplicated* or *empty* action but
cannot catch a *missing* one. A test that extracts those literals and asserts each resolves would
have caught both, and it has not been written.

The screen specs are grouped by **slice**, not by role, to preserve the read-one-plan premise.
Each one opens with a role coverage table mapping the role-grouped brief below onto
slice-grouped files.

**Not yet written, required before v1 ship:**

- Per-screen specs for the three unbuilt slices, each blocked by the absence of its API:
  - `Tickets` — ticket inbox/pickup queue, ticket detail with assignment, my tickets, new
    ticket. `UI/TicketArchitecture` records the original intent.
  - `Documents` — upload and download. Blocked twice over: the slice has no endpoints at all,
    and the ones it needs are registered by `Tickets`.
  - A real home screen for the `Employee` role. `UI/LoginArchitecture.md` §2.6 lands them on
    `/profile` as an acknowledged placeholder rather than inventing a dashboard.
- Build plans for those same three unbuilt slices. `UI/Plans/` covers the six built slices only;
  a plan for a slice with no endpoints would be invention, not sequencing.
- **No screen in `UI/` has ever been rendered against a running backend, and no file any plan
  describes exists.** There is no `frontend/` directory, no Dockerfile, and no local PostgreSQL on
  the authoring machine, so the UI specification carries the same "written is not verified" caveat as
  the eight slice plans — every "verify" step in `UI/Plans/` is an instruction to a future builder
  who has a database.

**Total lines of specification:** ~31,300, in five groups:

| Group | Lines | Read |
|---|---:|---|
| The six cross-cutting documents (0–4 plus `App/GeneralAppArchitecture.md`) | ~3,100 | All of them, first |
| The eight slice implementation plans under `Slices/` | ~15,100 | **One** — the slice you are building |
| The frontend specification under `UI/` | ~6,900 | `UI/README.md`, the two cross-cutting docs, then **one** screen spec |
| The seven UI build plans under `UI/Plans/` | ~5,900 | `UI/Plans/README.md`, then Phase 0, then **one** slice plan |
| This file | ~280 | Now |

This is executable spec, not summary — which is why the reading instructions above say *one* slice
plan, *one* screen spec and *one* UI plan. A builder who reads all eight backend plans, all nine UI
documents and all seven UI plans has read some 28,000 lines to write one slice, and will have
forgotten the parts that mattered.
