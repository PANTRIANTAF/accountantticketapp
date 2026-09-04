# Frontend Specification — Index

This folder is the specification for the AccountantApp's React single-page application. Like
the rest of `Architect Files/`, it is written to be **read and implemented by an AI coding
agent**, not skimmed by a human. It is prescriptive on purpose: every rule states the concrete
failure that follows from breaking it, and where a decision looks arbitrary the reason is given
next to it.

The backend it targets is real. All eight slices are built and wired in `Program.cs`; the six this
folder specifies screens for are `Audit`, `Notifications`, `Customers`, `TicketTypes`, `Identity`
and `Employees` — and every route, DTO field, status code and validation limit quoted in this
folder was read out of that code rather than inferred from a plan. Where the code and a numbered
document disagree, the disagreement is named and recorded in
[BACKEND_CHANGES_REQUIRED.md](BACKEND_CHANGES_REQUIRED.md) instead of being smoothed over.

## Reading order

Read 1 and 2 in full before writing any code. Then read **one** screen spec — the one for the
slice you are building — and not the others. Then read that slice's **build plan** (10), which
tells you what order to create its files in; the screen spec says *what* the screens are, the plan
says *when each file gets written and how you check it*.

| # | Document | What it defines |
|---|---|---|
| 1 | [GeneralUIArchitecture.md](GeneralUIArchitecture.md) | The governing document. Project layout, the API client, TanStack Query rules, routing and guards, the shell, client-side permissions, the error taxonomy, MUI conventions, forms, wire formats, the dev loop. Everything else cites it. |
| 2 | [LoginArchitecture.md](LoginArchitecture.md) | Session bootstrap, login, the forced-password-change gate, password reset, invitation acceptance, logout, mid-session expiry, the role enum. |
| 3 | [Screens/IdentityScreens.md](Screens/IdentityScreens.md) | Accountant management and the read-only profile screen. |
| 4 | [Screens/CustomersScreens.md](Screens/CustomersScreens.md) | Customer list, detail, create, edit, suspend, and "my Customer". |
| 5 | [Screens/EmployeesScreens.md](Screens/EmployeesScreens.md) | Employee list and detail, registration, invitation, role changes, departure, account suspension, and customer onboarding. |
| 6 | [Screens/TicketTypesScreens.md](Screens/TicketTypesScreens.md) | Ticket Type list, detail, version history, the field-descriptor editor, **and the dynamic form renderer contract**. The largest of the six. |
| 7 | [Screens/NotificationsScreens.md](Screens/NotificationsScreens.md) | Notification centre and the unread badge. The only polling in the application. |
| 8 | [Screens/AuditScreens.md](Screens/AuditScreens.md) | Audit search and entry detail. `AccountantAdmin` only. |
| 9 | [BACKEND_CHANGES_REQUIRED.md](BACKEND_CHANGES_REQUIRED.md) | The punch-list of backend work the UI depends on, ranked by consequence. Read it once, early. Thirty-three items; three of them block deployment, and **item 3 makes every `CustomerAdmin` and `Employee` screen in this folder untestable today**. If you do not know that before you build them it will cost you an afternoon. |
| 10 | [Plans/README.md](Plans/README.md) | The index to the seven **build plans** under `Plans/` — Phase 0 plus one per slice that has a screen spec. Ordered steps, not a second copy of the specs above: which file to create, in what order, and how each step is verified. Read the index, then your slice's plan. |

### The screen specs are grouped by slice, not by role

`../README.md` states the brief grouped by role (*"Accountant: customer list, ticket
inbox…"*). This folder groups by **slice**, one file per specified slice, matching
`Slices/<Slice>/IMPLEMENTATION_PLAN.md` exactly.

The reason is the spec's own premise: read one plan for the slice you are building
(`../README.md` row 8). Per-role files would duplicate the DTO tables and permission matrices
that a list screen shares with its detail screen, and a screen seen by two roles — customer
detail, employee detail — would have no single home.

So that neither reader loses their way, **every screen spec opens with a role coverage table**
mapping the role-grouped brief onto slice-grouped files.

## Conflict precedence

This folder sits **below** the numbered documents. In order, highest first:

1. `../00-Glossary.md` … `../04-Infrastructure.md`
2. `../App/GeneralAppArchitecture.md` — backend conventions the client consumes
3. `UI/GeneralUIArchitecture.md`
4. `UI/LoginArchitecture.md`
5. `UI/Screens/*.md`
6. `UI/Plans/*/IMPLEMENTATION_PLAN.md` — the build plans, at the bottom of the whole hierarchy

A screen spec loses to `GeneralUIArchitecture.md`, and a build plan loses to its screen spec — where
the two disagree, the plan is the one that is wrong. Both lose to documents 0–4.
`BACKEND_CHANGES_REQUIRED.md` is not normative at all — it is a list of requests, and nothing
in it overrides anything.

**Do not resolve a contradiction by inventing a third behaviour — flag it**
(`../README.md` §*Conflict precedence*). That is how these documents get corrected.

One place where the shipped code loses too: where the code contradicts a numbered document
(the dev port, the error content type), the specification follows the **document** and records
the drift in the punch-list. It does not silently follow the code.

## The locked frontend stack

Settled. Do not revisit, and do not add a dependency outside this list without flagging it
first (`GeneralUIArchitecture.md` §1.5).

| Concern | Choice |
|---|---|
| Build tool | **Vite** |
| Language | **TypeScript**, strict |
| Components | **MUI (Material UI)**, plus `@mui/x-date-pickers` and `date-fns` |
| Server state | **TanStack Query** |
| Routing | **React Router** |
| Forms | **React Hook Form** with **Zod** |

**Banned:** `@mui/x-data-grid` (every list in this API is server-paginated; see
`GeneralUIArchitecture.md` §8.2), any state-management library for server data, and any HTTP
client other than `fetch` behind the single `http.ts` module.

Three hosting facts that constrain everything and are locked in `../04-Infrastructure.md`:

- The SPA source lives in **`frontend/`** at the repository root, and the production Dockerfile
  hard-codes that path.
- Production is **three containers**; the built SPA ships inside `app`, served from its
  `wwwroot`. Same origin, so **CORS is never configured, in any environment**.
- **There is no API base-URL environment variable.** The SPA always calls `/api/...` relative to
  its own origin. `VITE_API_URL` and anything like it are forbidden — a base-URL variable is how
  one build ends up pointing at a different instance.

## Files in this folder that are *not* specification

| File | Status |
|---|---|
| `TicketArchitecture` (no extension) | The original author's working notes on the ticket UX — 15 lines, **non-normative**. It is the only recorded intent for the ticket screens, which are still unwritten, so it is kept for the same reason `App/GeneralAppArchitecture` (no extension) was kept. Do not implement from it and do not link to it as though it were a spec. |

The two 0-byte placeholders that used to sit here — `GeneralUIArchitexture.txt` (note the
misspelling) and `LoginArchitecture.txt` — have been **deleted**, replaced by the
correctly-spelled `.md` files above. Leaving an empty misspelled file beside the real one is
exactly the trap `../README.md` warned about.

## Not written yet

Three slices have no screen specs, and in each case the blocker is a frontend one — a missing
screen document, or an undecided screen — not a missing backend.

| Missing spec | Blocked by |
|---|---|
| Ticket inbox / pickup queue, ticket detail with assignment, my tickets, new ticket | **There is no `Screens/TicketsScreens.md`.** That is the blocker, and it is a frontend one: every plan in `Plans/` is a transcription of a screen document, so with no screen document there is nothing to transcribe and no client route to add. The backend slice is **built and routed** — `Program.cs:65` registers it, `Program.cs:157` maps it, and `Slices/Tickets/TicketsEndpoints.cs:35` opens `/api/tickets/*` with 18 routes — so **do not record "`Tickets` is unbuilt" or "no endpoints exist" as the reason; that claim is stale.** It is still last in the backend build order and still depends on every other slice. `TicketArchitecture` records the original intent and is non-normative. |
| Document upload and download | **There is no `Screens/DocumentsScreens.md` and no `Screens/TicketsScreens.md`**, and the second is the harder blocker: a document is reached through its ticket, so document screens cannot be specified before `Tickets` either. The slice is **built** (`Program.cs:59`) and its routes exist. `Documents` has **no endpoints of its own** — that is by design, not absence: `Tickets` registers *and authorizes* `/api/documents/*` on its behalf (`Slices/Tickets/TicketsEndpoints.cs:250`, four routes — `/upload` `:252`, `/list` `:312`, `/download` `:322`, `/delete` `:356`), because the reverse dependency would be a cycle. **"Unbuilt", and "no endpoints at all", are stale — the distinction is "none of its own", not "no routes".** |
| A real Employee home screen | Depends on the `Tickets` screens, which is a documentation gap, not a backend one. `LoginArchitecture.md` §2.6 lands an `Employee` on `/profile` as an acknowledged placeholder rather than inventing a dashboard that would have to be deleted. |

Two consequences that leak into the specs that *are* written, and are flagged in place rather
than hidden:

- `TicketTypesScreens.md` specifies the `FileUpload` field type as rendering **disabled with an
  explanatory note** — the upload route exists (`TicketsEndpoints.cs:252`), no screen owns it.
- `NotificationsScreens.md` maps most notification event kinds to **no destination link**,
  because the tickets they refer to have no screens.

## Status of this documentation

**Written is not the same as verified.** Two things to know before building:

- **No screen in this folder has ever been rendered against a running backend.** There is no
  `frontend/` directory yet, there is no Dockerfile, and this authoring machine has no local
  PostgreSQL, so the API has never been started against a real database either. Every route
  table, DTO field list and status code here was read from source; none was observed in a
  response.
- **Each document ends with a "questions to flag if unclear" section, and those are real.**
  They are not rhetorical. Several are decisions somebody has to make — whether the
  must-change-password 403 gets a machine-readable code, whether a self-service profile edit
  exists at all, what an `Employee` should see before the `Tickets` screens exist. Read that
  section for your slice before writing code, not after.

A builder hitting a gap should **flag it, not invent a behaviour**. If implementing a screen
from these documents requires a decision the documents do not contain, that gap is a defect in
the specification — record it rather than patching it silently.
