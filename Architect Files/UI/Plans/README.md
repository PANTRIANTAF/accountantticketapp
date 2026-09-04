# UI Implementation Plans — Index

Seven build plans, one per phase or slice, for the React SPA specified in
[../GeneralUIArchitecture.md](../GeneralUIArchitecture.md), [../LoginArchitecture.md](../LoginArchitecture.md)
and [../Screens/](../Screens/). They are ordered instructions: which file to create, in what order,
what belongs in it, what must **not** be in it yet, and how a builder proves the step worked.

**A plan is not a second copy of the specification.** The screen specs say *what* each screen is;
these say *in what order which files get created and how each step is verified*. Facts are **cited by
section** — `GeneralUIArchitecture.md` §2.3 rule B — not restated, because a restatement is a second
thing to keep in sync and the copy is the one that goes stale. Open the cited section; do not
implement from a plan's summary of it.

## Reading order

Read [00-Foundation](00-Foundation/IMPLEMENTATION_PLAN.md) in full first. Then read **one** slice
plan — the one you are building — and not the others. That is the same premise the rest of
`Architect Files/` is built on ([../../README.md](../../README.md) *Reading order*).

| # | Plan | Lines | Builds |
|---|---|---|---|
| 0 | [00-Foundation/IMPLEMENTATION_PLAN.md](00-Foundation/IMPLEMENTATION_PLAN.md) | 981 | `frontend/` itself, the API client, TanStack Query setup, the session, `can()`, the theme, the ten shared components, the router, and all five `/api/auth/*` screens |
| 1 | [Identity/IMPLEMENTATION_PLAN.md](Identity/IMPLEMENTATION_PLAN.md) | 582 | The accountant list, its five mutations, and `/profile` |
| 2 | [Customers/IMPLEMENTATION_PLAN.md](Customers/IMPLEMENTATION_PLAN.md) | 946 | Customer list, detail, create, the two gated edit dialogs, suspend/reactivate, `/my-customer` |
| 3 | [TicketTypes/IMPLEMENTATION_PLAN.md](TicketTypes/IMPLEMENTATION_PLAN.md) | 1,391 | Type list, detail, version history, the field-descriptor editor, **and `shared/dynamicForm/`**. The largest, by a wide margin |
| 4 | [Employees/IMPLEMENTATION_PLAN.md](Employees/IMPLEMENTATION_PLAN.md) | 881 | Employee list and detail, registration, invitation, role change, departure, account suspension, reinstate, login-email change |
| 5 | [Notifications/IMPLEMENTATION_PLAN.md](Notifications/IMPLEMENTATION_PLAN.md) | 559 | The notification centre, the unread bell, mark-read and mark-all-read |
| 6 | [Audit/IMPLEMENTATION_PLAN.md](Audit/IMPLEMENTATION_PLAN.md) | 471 | Audit search and entry detail, `AccountantAdmin` only. Smallest by file count, largest by negative space |

The numbering is a **reading order, not a build order** past phase 0 — see below.

## Build order

Phase 0 is a hard prerequisite of all six slice plans: every one of them assumes `http.ts` exists,
assumes a session is in context, assumes `PaginatedTable` owns the 1-based/0-based page conversion,
and assumes `can()` answers questions about the caller's role. Nothing below compiles without it.

```
                        00-Foundation
                             |
   +---------+---------+-----+-----+---------+---------+
   |         |         |           |         |         |
Identity  Customers  TicketTypes  Employees  Notifications  Audit
             |                       |
             +------ api.ts ---------+          (the only slice-to-slice edge)
```

**The six slice plans are mutually independent and may be built in any order**, with one exception,
below. Each writes inside its own `frontend/src/slices/<slice>/` folder and edits `routes.tsx` and
its own rows of `can.ts` — nothing else. `03-SliceInventory.md`'s dependency rule is mirrored in the
client by `GeneralUIArchitecture.md` §1.4: a slice folder may import `shared/`, its own folder, and
another slice's `types.ts` and `api.ts` — never another slice's `screens/`, `components/` or
`queries.ts`.

### The two seams where a plan touches something it does not own

Both are deliberate, both are argued in place, and both are the kind of thing a builder "tidies" into
a circular import if the reason is not written down.

**A. `Customers` imports `Employees`' `api.ts`.** `/customers/new` calls
`POST /api/customers/onboard`, which the **`Employees`** slice registers, in another slice's
namespace, deliberately and LOCKED (`EmployeesEndpoints.cs:214-224`): that slice owns two of the
operation's three steps, so it owns the transaction that makes all three atomic. Ownership splits at
exactly one seam — the `Employees` plan §13 owns the wrapper, the request/response types and the
`can()` row; the `Customers` plan §7 owns the screen and the mutation hook. Building
`/customers/new` before `slices/employees/api.ts` exists is the one real ordering constraint in the
graph, and `Customers` §0.2 rule C resolves it either way.

**B. `Notifications` mounts the bell into `AppShell`.** `shared/` may not import from `slices/`
(§1.4 rule A), so the badge cannot be imported into `AppShell.tsx`. Instead `AppShell` gains one
optional prop, `notificationSlot?: ReactNode`, and `routes.tsx` — which already imports every
slice's screens — passes `<UnreadBadge />` into it. The coupling lands in the file built to hold it.

### What no slice plan may create

If a slice plan appears to need one of these, it has misread its dependency: the file is Phase 0's,
and a private copy is a second thing to fix when the shared one changes.

`shared/api/*` · `shared/auth/*` · `shared/permissions/can.ts` and `actions.ts` (a slice plan adds
**rows**, never the module) · `shared/hooks/usePaginatedQuery.ts` · `shared/format/*` ·
`shared/components/*` — `AppShell`, `PaginatedTable`, `ConfirmDialog`, `PageHeader`, `StatusChip`,
`ErrorBanner`, `EmptyState`, `LoadingRegion`, `NotFoundPage`, `AccessDeniedPage` · `theme.ts` ·
`routes.tsx` itself (a slice plan adds **rows**) · every screen under `/api/auth/*`.

One exception, and it is not `shared/` by accident: **`shared/dynamicForm/` belongs to the
`TicketTypes` plan**, not to Phase 0. It lives in `shared/` because the as-yet-unplanned `Tickets`
UI is its real consumer — a ticket form is a rendered ticket type — and it is specified by `TicketTypes`
because that is the slice that defines the field descriptors it renders.

## Conflict precedence

These plans sit at the **bottom** of the whole hierarchy. Highest first:

1. `../../00-Glossary.md` … `../../04-Infrastructure.md`
2. `../../App/GeneralAppArchitecture.md`
3. `../GeneralUIArchitecture.md`
4. `../LoginArchitecture.md`
5. `../Screens/*.md`
6. **This folder** — loses to all of the above

Where a plan disagrees with a document above it, **the document wins and the plan is wrong**: fix the
plan, do not code around it. Where a plan disagrees with the shipped code, read
[../BACKEND_CHANGES_REQUIRED.md](../BACKEND_CHANGES_REQUIRED.md) before deciding which is stale — it
is non-normative and cited by item number only, and it is where a code-versus-document disagreement
gets recorded rather than smoothed over.

**Do not resolve a contradiction by inventing a third behaviour — flag it.** Every plan ends with a
*Questions to flag if unclear* section, and those questions are real, not rhetorical. Read your
slice's before writing code, not after.

## What is not planned, and why

| Missing plan | Blocked by |
|---|---|
| `Tickets` — inbox, pickup queue, ticket detail with assignment, my tickets, new ticket | **There is no `../Screens/TicketsScreens.md`.** That is the blocker, and it is a frontend one: every plan in this folder is a transcription of a screen document, so with no screen document there is nothing to transcribe and no client route to add. The backend slice is **built and routed** — `Program.cs:65` registers it and `Program.cs:157` maps `/api/tickets/*` — so do not record "no endpoints" as the reason. It is last in the backend build order and depends on every other slice. `../TicketArchitecture` records the original intent and is non-normative |
| `Documents` — upload and download | **There is no `../Screens/DocumentsScreens.md` and no `../Screens/TicketsScreens.md`**, and the second is the harder blocker: a document is reached through its ticket, so document screens cannot be planned before `Tickets` either. The routes themselves exist — `Documents` is the only slice with no endpoints **of its own**, because `Tickets` registers `/api/documents/*` on its behalf (`Slices/Tickets/TicketsEndpoints.cs:250`, four routes). `TicketTypes` §6 therefore renders the `FileUpload` field type disabled with an explanatory note |

There is no separate plan for the login screens: they are Phase 0 §8, because the router and the
session provider cannot be verified without them.

## Status

**Nothing in these plans has ever been run.** There is no `frontend/` directory, there is no
Dockerfile, and the authoring machine has no local PostgreSQL, so the API has never been started
against a real database either. Every "verify" step is an instruction to a future builder who has a
database. No step has been observed to work, and no line of the code these plans describe exists.

Two consequences worth knowing before you start:

- **Three punch-list items block deployment, none blocks development.** Items 1, 2 and 3 —
  `Program.cs` is missing the three SPA-hosting lines, there is no Dockerfile or compose file, and
  there is no way to create a Customer-side user. The third is the one that bites: **every
  `CustomerAdmin` and `Employee` screen in these plans is unreachable and untestable today.**
- **`Slices/Employees/` is untracked in git.** Its permission catalogue carries an uncommitted fix
  (punch-list item 26), nothing about it can be recovered from history, and the `Employees` plan §2
  is a gate that checks two specific lines still exist before writing their `can()` rows. Do not
  skip that gate on the grounds that the item is marked resolved.
