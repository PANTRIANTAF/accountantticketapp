# Employees Screens — UI Implementation Plan

This is an executable step-by-step plan for the **Employees** slice of the React SPA. Follow it in order. Do not
add features and do not make architectural decisions. Where something is unclear, **flag it** (§16), never guess.

**Build position.** This plan runs after `UI/Plans/00-Foundation/IMPLEMENTATION_PLAN.md` and is independent of the
other slice plans. It creates files under `frontend/src/slices/employees/` and edits exactly two files outside it:
`frontend/src/routes.tsx`, whose job is to import every slice's screens (`GeneralUIArchitecture.md` §1.4 rule E),
and the Employees rows of `shared/permissions/can.ts`, which the Foundation plan leaves as a declared hole. It
says what order files are created in, what each step contains, and how each step is verified. It does not restate
`Screens/EmployeesScreens.md`; that document says what the screens are, and is cited by section throughout.

**Documents that govern this document, in precedence order.**

| # | Document | What it fixes for this plan |
|---|---|---|
| 1 | `00-Glossary.md` | Binding vocabulary in UI copy. Never "Client", never bare "Admin", never "User" as a label |
| 2 | `01-DomainModel.md` §2 | Why `Employee` and `UserAccount` are separate — the root of the two status vocabularies (§8.1) |
| 3 | `02-AuthorizationMatrix.md` §1, §4, §11, §12 | Normative role matrix. §1: out-of-scope is `404`, not `403`. §4: who may do what to an Employee |
| 4 | `03-SliceInventory.md` §1, §3 | Why `/api/customers/onboard` is registered by `Employees` (§13); the cross-slice import rule |
| 5 | `04-Infrastructure.md` §1–3 | `frontend/` at the repo root, one origin, no CORS, no base-URL variable |
| 6 | `App/GeneralAppArchitecture.md` §8 | Route shape, ids in the body, pagination envelope, `ProblemDetails` |
| 7 | `UI/GeneralUIArchitecture.md` | Governing UI document. §1.2 tree, §2 client, §3 queries, §4 routing, §6.1 `can()`, §7 errors, §8 MUI, §9 forms, §10 wire formats |
| 8 | `UI/LoginArchitecture.md` §3, §8 | The forced-password-change gate; the role enum |
| 9 | `UI/Screens/EmployeesScreens.md` | The screen specification. Normative for this plan |
| 10 | `UI/Plans/00-Foundation/IMPLEMENTATION_PLAN.md` | Phase 0, the shared kernel. Cited, never re-specified |
| — | `UI/BACKEND_CHANGES_REQUIRED.md` | **Not normative.** A punch-list. Cited by item number only |

Where any of 1–9 disagrees with this plan, **it wins and this plan is wrong**. Stop and flag it.

---

## 0. Phase 0 is a prerequisite and is not in this plan

### 0.1 What the Foundation plan must have delivered first

Do not start Phase 1 until every item below exists and `npm run dev` serves the shell.

| Needed by this plan | From `GeneralUIArchitecture.md` |
|---|---|
| `shared/api/` — `http.ts`, `ApiError.ts`, `problemDetails.ts`, `paginated.ts`, `queryClient.ts` | §2.1, §2.2, §3.3, §3.4 |
| `shared/auth/` — `SessionProvider.tsx`, `useSession.ts`, `RequireSession.tsx`, `RequireRole.tsx` | §4.3 |
| `shared/permissions/actions.ts` + `can.ts` — the file and the function, Employees rows **absent** | §6.1 |
| `shared/components/` — `AppShell`, `PageHeader`, `PaginatedTable`, `ConfirmDialog`, `StatusChip`, `ErrorBanner`, `EmptyState`, `LoadingRegion`, `NotFoundPage`, `AccessDeniedPage` | §8.3 |
| `shared/hooks/usePaginatedQuery.ts`; `shared/format/dates.ts`, `enums.ts` | §3.2 rule G, §10.1, §10.2 |
| `routes.tsx` with the three Employees rows unwired; `theme.ts`, `main.tsx`, `App.tsx`, `vite.config.ts` | §4.1, §8.1, §11.1 |

### 0.2 What this plan may not create

**A. Nothing under `frontend/src/shared/`**, except the thirteen `can.ts` rows of Phase 1 and their `ActionName`
entries. If a step appears to need a new shared component, formatter or hook, that is a gap in the Foundation plan
or in §8.3 — put it in §16 and stop. Do not build a private copy under `slices/employees/components/`; that is the
same defect with a longer import path.

**B. No dependency** outside the locked list in §1.5. This slice needs nothing beyond it.

**C. No `fetch` call.** `shared/api/http.ts` is the only module allowed one (§2.1). A `fetch` under
`slices/employees/` is a defect whether or not it works.

**D. No backend file is modified** — not `EmployeesActionCatalogue.cs`, not an endpoint, not a DTO. A backend
change is requested in §16, never performed here.

Before Phase 2, run `GeneralUIArchitecture.md` §11.3's five checks against a real database; they are not optional
and not this plan's to restate. Then run the Phase 1 gate.

---

## 1. Build order — twelve phases

| Phase | Deliverable | Gate |
|---|---|---|
| 0 | The shared kernel | Foundation plan (§0.1) |
| 1 | `can.ts` — thirteen Employees rows, verified against the catalogue | **Opens Phase 11** |
| 2 | `types.ts` — three read shapes, nine request shapes, `MarkedResult` | — |
| 3 | `api.ts` — thirteen `post` wrappers | — |
| 4 | `queries.ts` — one list hook, two detail hooks, one hook per write | — |
| 5 | `schemas.ts` — the Zod schemas | — |
| 6 | `EmployeeListScreen` + `EmployeeFieldset` + `RegisterEmployeeDialog`; wire `/employees` | — |
| 7 | `EmployeeDetailScreen` + `EmployeeStatusPair`; wire `/employees/:employeeId` | — |
| 8 | `EditEmployeeDialog`, `InviteEmployeeDialog` | — |
| 9 | `SetRoleDialog`, `DepartEmployeeDialog` | — |
| 10 | `ProfileScreen`; wire `/profile` | — |
| 11 | `ReinstateEmployeeDialog`, `ChangeLoginEmailDialog` | **Blocked until §2.2 passes** |

### 1.1 Why this order

Types before the client, the client before the hooks, the hooks before any screen: each layer is diffable against
exactly one thing — `types.ts` against `EmployeeReadDtos.cs` and `EmployeeWriteDtos.cs`, `api.ts` against
`EmployeesEndpoints.cs` — and neither diff is possible if a screen is being written at the same time. The list
precedes the detail because the detail is reached from it and because the list is the only screen that proves the
`pageSize` clamping rule. Phase 11 is last because of punch-list item 26 (§2): a numbered phase with a gate, not a
footnote, because building those two buttons in the wrong order costs an afternoon chasing a `403` that is not a
bug in this code.

---

## 2. Phase 1 — the permission gate that item 26 left behind

### 2.1 What to verify, at `file:line`, before writing a row

`IPermissionChecker` is **fail-closed on an unrecognised action name**. `Shared/Authorization/PermissionChecker.cs`:41 is

```
var allowed = _actions.TryGetValue(action, out var roles) && roles.Contains(user.Role);
```

An action absent from the composed dictionary fails the first clause, so `allowed` is `false` **for every role,
including `AccountantAdmin`**, and :63 throws `AppException($"Permission denied for action '{action}'.", 403)`.
There is no default-allow branch, and a missing key is undetectable at startup — the constructor rejects only a
zero-role or duplicated entry. Worse, :49-55 writes `AuditActions.PermissionDenied` / `AuditOutcome.Denied` with
`After: new { Action = action }` **before** throwing, so every attempt records a false denial against somebody who
was entitled to the operation, in the one log an investigator must trust.

Two handlers depend on names that were, until 2026-09-02, in no catalogue at all:

| Handler | `file:line` | Action literal required |
|---|---|---|
| `ReinstateEmployeeHandler` | `Slices/Employees/Application/Handlers/ReinstateEmployeeHandler.cs`:59 | `"ReinstateEmployee"` |
| `ChangeEmployeeLoginEmailHandler` | `Slices/Employees/Application/Handlers/ChangeEmployeeLoginEmailHandler.cs`:57 | `"ChangeEmployeeLoginEmail"` |

Both routes **are** registered — `EmployeesEndpoints.cs`:151 and :167 — so a `403` from either is not evidence
that the route is absent. Never conclude a route does not exist from a `403`.

> **Read this before assuming the bug is live.** In the working tree as of 2026-09-02 the catalogue **does**
> declare both: `Slices/Employees/EmployeesActionCatalogue.cs`:53 (`ReinstateEmployee` → `AccountantAdmin`,
> `AccountantUser`, `CustomerAdmin`) and :60 (`ChangeEmployeeLoginEmail` → `AccountantAdmin`, `AccountantUser`).
> Punch-list **item 26 is marked RESOLVED**, and `GeneralUIArchitecture.md` §6.1 — which outranks the screen spec —
> lists both rows as live. `Screens/EmployeesScreens.md` §8's callout, its files-checklist line and its success
> criterion 16 still say eleven rows and still describe the bug as open; **that text is stale**. Criterion 16
> resolves itself — *"the count is not the criterion, the exact match is"* — and the exact match today is
> **thirteen**. Recorded in §16. `Slices/Employees/` is untracked in git: check the working tree, never `git ls-tree`.

### 2.2 The gate

**File:** `frontend/src/shared/permissions/can.ts`

**Gate condition, checked by reading the source, not by running the app:** `EmployeesActionCatalogue.cs` contains
keys `"ReinstateEmployee"` **and** `"ChangeEmployeeLoginEmail"`, and each of the thirteen
`RequireAsync(user, "…")` literals under `Slices/Employees/Application/Handlers/` resolves to a key in that file.

- **Passes** → write all thirteen rows (§2.3); Phase 11 is unblocked.
- **Fails** → write **eleven**, omitting those two; **Phase 11 does not start**; raise item 26 as reopened. Do not
  add a row for an action the catalogue lacks: `can()` returning `true` against a guaranteed `403` is the bug §6.2
  rule B names, and the button it draws audits a false denial on every click. Do not ship the buttons behind a
  hardcoded `true` either. They are then built **last, in the same commit that adds the two catalogue entries** —
  a backend change, requested in §16, not performed here.

### 2.3 The thirteen rows

Verified name-by-name against `EmployeesActionCatalogue.cs`:22-64 and the thirteen handler literals. Roles per
`02-AuthorizationMatrix.md` §4.

| Action | AA | AU | CA | EMP | Catalogue line |
|---|:--:|:--:|:--:|:--:|---|
| `OnboardCustomer` | yes | — | — | — | 22 |
| `RegisterEmployee` | yes | yes | yes | — | 26 |
| `ListEmployees` | yes | yes | yes | — | 28 |
| `ViewEmployee` | yes | yes | yes | yes | 33 |
| `UpdateEmployee` | yes | yes | yes | — | 36 |
| `UpdateOwnContact` | — | — | yes | yes | 41 |
| `InviteEmployee` | yes | yes | yes | — | 43 |
| `SetEmployeeRole` | yes | yes | yes | — | 45 |
| `DepartEmployee` | yes | yes | yes | — | 47 |
| `ReinstateEmployee` | yes | yes | yes | — | 53 |
| `ChangeEmployeeLoginEmail` | yes | yes | **—** | **—** | 60 |
| `SuspendEmployeeAccount` | yes | yes | yes | — | 61 |
| `ReactivateEmployeeAccount` | yes | yes | yes | — | 63 |

**The asymmetry in rows 10 and 11 is deliberate; do not tidy it into matching.** `02-AuthorizationMatrix.md` §4
gives both halves: a Customer Admin who can enter a departure must be able to correct one, and *changing a login
email is reserved to the Office, and nobody may change their own*. `can(role,'ChangeEmployeeLoginEmail')` is
`false` for a `CustomerAdmin` **by design** — not a survivor of the item-26 bug — and "fixing" it hands a Customer
Admin the one Employee power the matrix withholds. `OnboardCustomer` is an **Employees** action (line 22) though
its route sits under `/api/customers/` and its screen in the Customers slice; its row belongs here (§13).

### 2.4 Four ways this step goes wrong

1. **A row the catalogue lacks.** Draws a button that `403`s for everyone and writes a false `PermissionDenied`
   entry per click — item 26 re-created by hand.
2. **A missing row for an action the catalogue has.** Hides an entitled button. Annoying, and far safer than item
   1 — which is why `can()` denies an unknown action.
3. **Giving `CustomerAdmin` `ChangeEmployeeLoginEmail` "for symmetry".** See §2.3.
4. **Treating `can()` as an answer about a row.** It answers *who may call*, never *which record* (§6.2 rule D).
   Row scoping is `CustomerScope` in the handler and reaches the UI as a `404`.

### What this step does NOT do, and why

It does not fetch a permission table — no endpoint exists, and a fetched table would still be wrong in exactly the
row-level cases that matter. It does not cache a `can()` result (the function is pure over the session role) and
adds no `try`/`catch` to absorb a `403`.

---

## 3. Phase 2 — `types.ts`

**File:** `frontend/src/slices/employees/types.ts`

Hand-written interfaces, camelCase, each commented with the C# file it mirrors (§2.5). Three read shapes from
`Slices/Employees/Application/Dtos/EmployeeReadDtos.cs`:

- `EmployeeSummary` — `id`, `givenName`, `familyName`, `jobTitle?`, `status`, `hasAccount`, `role: UserRole | null` (:18-34).
- `EmployeeDetail` — those plus `customerId`, `workEmail?`, `contactPhone?`, `taxIdentificationNumber?`,
  `socialSecurityNumber?`, `employmentStartDate`, `employmentEndDate?`, `createdAt`, `accountStatus: string | null` (:41-63).
- `EmployeeSelf` — `id`, `customerId`, `givenName`, `familyName`, `jobTitle?`, `workEmail?`, `contactPhone?`,
  `employmentStartDate`, `notice?` (:70-88).

`MarkedResult` is `{ success: boolean }` (:94) and carries no state at all. Request shapes mirror
`EmployeeWriteDtos.cs` and `EmployeeReadDtos.cs`:99-124: `ListEmployeesRequest`, `EmployeeIdRequest`,
`RegisterEmployeeRequest`, `UpdateEmployeeRequest`, `UpdateOwnContactRequest`, `InviteEmployeeRequest`,
`SetEmployeeRoleRequest`, `DepartEmployeeRequest`, `ChangeEmployeeLoginEmailRequest`, plus
`OnboardCustomerRequest`/`OnboardCustomerResponse` (§13).

**A. Three interfaces. No union and no optional-everything superset.** `EmployeeDetail | EmployeeSelf` forces a
narrowing check at every field access, and that check is a field sniff wearing a type annotation (§2.3 rule C).

**B. `status` is a string; `role` is an integer.** `status` is `'Active' | 'Departed'` — `Core/Employee.cs`:61-62
declares exactly those two. `accountStatus` is `'Invited' | 'Active' | 'Suspended' | null`. `role` is `UserRole`
from `shared/format/enums.ts`, `0`–`3`, **nullable** (§14 rule C).

**C. Both employment dates are `string`**, `"YYYY-MM-DD"` — C# `DateOnly`, no timezone; `createdAt` is a string
from a `DateTimeOffset`, with an offset (§10.2). Never construct a `Date` from a `DateOnly` and format it locally;
it shifts a day west of UTC.

No Zod schema here — that is Phase 5, and a validation schema is not a wire type. No generated client and no code
generator: there is no OpenAPI document, and producing one is punch-list item 9, not this plan's job.

---

## 4. Phase 3 — `api.ts`

**File:** `frontend/src/slices/employees/api.ts`

One exported function per endpoint, named for the endpoint. No React, no hooks, no TanStack Query, so the file
reads against `EmployeesEndpoints.cs` line by line (§2.5).

### 4.1 The thirteen wrappers, enumerated from the registrations

Read off the `MapPost` calls, **not** off the screens that consume them. That is how two endpoints were missed once
already.

| Function | Route | Registration | Request | Response |
|---|---|---|---|---|
| `registerEmployee` | `/api/employees/register` | :21 | `RegisterEmployeeRequest` | `EmployeeDetail` |
| `listEmployees` | `/api/employees/list` | :36 | `ListEmployeesRequest` | `PaginatedResponse<EmployeeSummary>` |
| `getEmployee` | `/api/employees/get` | :50 | `{ employeeId }` | `EmployeeDetail` **or** `EmployeeSelf` |
| `updateEmployee` | `/api/employees/update` | :69 | `UpdateEmployeeRequest` | `EmployeeDetail` |
| `updateOwnContact` | `/api/employees/update-own-contact` | :86 | `{ workEmail, contactPhone }` | `EmployeeSelf` |
| `inviteEmployee` | `/api/employees/invite` | :103 | `InviteEmployeeRequest` | `EmployeeDetail` |
| `setEmployeeRole` | `/api/employees/set-role` | :119 | `SetEmployeeRoleRequest` | `MarkedResult` |
| `departEmployee` | `/api/employees/depart` | :135 | `DepartEmployeeRequest` | `MarkedResult` |
| `reinstateEmployee` | `/api/employees/reinstate` | :151 | `{ employeeId }` | `MarkedResult` |
| `changeEmployeeLoginEmail` | `/api/employees/change-login-email` | :167 | `{ employeeId, loginEmail }` | `MarkedResult` |
| `suspendEmployeeAccount` | `/api/employees/suspend-account` | :184 | `{ employeeId }` | `MarkedResult` |
| `reactivateEmployeeAccount` | `/api/employees/reactivate-account` | :198 | `{ employeeId }` | `MarkedResult` |
| `onboardCustomer` | `/api/customers/onboard` | :227 | `OnboardCustomerRequest` | `OnboardCustomerResponse` |

**Every one is `post`. There is no `get` in this file and no query string.**

```ts
import { post } from '../../shared/api/http';
import type { PaginatedResponse } from '../../shared/api/paginated';
import type { EmployeeDetail, EmployeeSelf, EmployeeSummary } from './types';

/** POST read: EmployeesEndpoints.cs:36. The filter object is too large for a query string. */
export const listEmployees = (
  body: ListEmployeesRequest,
): Promise<PaginatedResponse<EmployeeSummary>> => post('/api/employees/list', body);

/** Two response shapes. Callers pick the type from the session role, never by sniffing. */
export const getEmployee = (employeeId: string): Promise<EmployeeDetail | EmployeeSelf> =>
  post('/api/employees/get', { employeeId });

/** No id parameter, deliberately. See EmployeesScreens.md section 7.5 rule A. */
export const updateOwnContact = (
  body: { workEmail: string | null; contactPhone: string | null },
): Promise<EmployeeSelf> => post('/api/employees/update-own-contact', body);
```

### 4.2 Five ways this step goes wrong

1. **"Correcting" `/list` or `/get` to `GET`.** Both are `POST` reads (§2.3 rule C); the result is a `405` with
   nothing in the body explaining it. The `list` suffix does not predict the verb.
2. **Giving `updateOwnContact` an `employeeId`.** Its absence *is* the security control —
   `EmployeeWriteDtos.cs`:46-60: *"an EmployeeId here, however carefully checked, turns every future edit of the
   handler into an opportunity to check it wrongly."* No call site can pass what does not exist.
3. **Putting an id in a path.** No route parameter exists anywhere in this API (§2.3 rule D). The SPA route carries
   the id; the API never does.
4. **Reusing `updateEmployee` for "edit my own details".** Different endpoint, permission, audit meaning and a much
   wider field list.
5. **A base URL or an `import.meta.env` lookup.** Every path is a relative string starting `/api/`. There is no API
   base-URL environment variable, ever (§2.3 rule A).

### What this step does NOT do, and why

No caching, invalidation or error handling: a non-2xx throws `ApiError` from `http.ts`, and callers never see
`{ data, error }` (§2.3 rule E). No retry — nothing here is idempotent and there is no idempotency key, so a
retried `register` creates a second Employee (§3.4).

---

## 5. Phase 4 — `queries.ts`

**File:** `frontend/src/slices/employees/queries.ts`

Hooks named `useXxx`. Screens import hooks; **screens never import `api.ts`** (§3.2 rule A).

| Hook | Key or invalidation |
|---|---|
| `useEmployeeList(filters)` | `['employees','list',{ customerId, status, hasAccount, searchTerm, pageNumber, pageSize }]` |
| `useEmployeeDetail(id)` | `['employees','detail',id]`, `enabled: role !== UserRole.Employee` |
| `useOwnEmployeeRecord(id)` | `['employees','self',id]` |
| `useRegisterEmployee`, `useUpdateEmployee`, `useInviteEmployee` | `setQueryData(['employees','detail',dto.id], dto)`; invalidate `['employees','list']` |
| `useUpdateOwnContact` | `setQueryData(['employees','self',dto.id], dto)`; invalidate `['employees','list']` |
| `useSetEmployeeRole`, `useDepartEmployee`, `useReinstateEmployee`, `useChangeLoginEmail`, `useSuspendAccount`, `useReactivateAccount` | invalidate `['employees','detail',employeeId]` **and** `['employees','list']` |

**A. Discriminate on the session role, before the call — mandatory.** Two hooks, two return types, two cache keys
(`EmployeesScreens.md` §2.3). Never `'status' in response`, never `response.status !== undefined`, never a Zod union
discriminated on optionality. All three work today and all three break *silently* the first time a field moves, by
sending a full record down the narrow branch. `status` is the worst possible sniffing key: it collides with
`ApiError.status`, with the HTTP status and with `accountStatus`, so the bug reads as correct code.

**B. The six `MarkedResult` mutations cannot seed the cache.** Invalidating both keys is the only way a screen
learns the new state, and it is a real second round trip. Do not paper over it with a guessed `accountStatus`:
§3.2 rule E bans optimistic updates, and the client could not guess correctly anyway — `suspend-account` changes a
value the *list* DTO never returns.

**C. `useEmployeeList` is built on `usePaginatedQuery`** (§3.2 rule G) and nothing else. Render the pager from
`response.pageSize`, never from the value sent: `pageSize` is clamped to 50, not rejected. Every filter appears in
the key — omit `searchTerm` and two searches share one entry, so the table shows the previous query's rows under
the new query's pager.

**D. `enabled` expresses a data dependency, never a permission.** `useEmployeeDetail` is disabled for the
`Employee` role because that role receives a *different type*, not because it is forbidden. Page-level permission
is `RequireRole` in the route table (§3.2 rule B).

**E. No `refetchInterval` in this file.** The unread-notification count is the only polling query in the
application (§3.2 rule H) — nothing here polls, including to observe the eight-hour role lag of §10 rule B.

---

## 6. Phase 5 — `schemas.ts`

**File:** `frontend/src/slices/employees/schemas.ts`

One Zod schema per form, through `zodResolver`, `mode: 'onBlur'` (§9.1, §9.3 rule A). Mirror
`Slices/Employees/Application/EmployeeValidation.cs` **exactly**: a client limit stricter than the server's blocks
legitimate input, a looser one produces the unattachable banner of §14 rule H.

| Field | Rule | Source |
|---|---|---|
| `givenName`, `familyName` | required, trimmed, ≤100 | `EmployeeValidation.cs`:32-33 |
| `jobTitle` | optional, ≤200 | :34 |
| `workEmail` | optional, ≤320, **must contain `@`** | :150-156 |
| `contactPhone`, `taxIdentificationNumber`, `socialSecurityNumber` | optional, ≤50 | :36-40 |
| `employmentStartDate` | required, ≤ today + 1 year | :138-148 (`MaximumStartDateYearsAhead = 1`) |
| `customerId` (register) | required, non-empty GUID | :30-31 |
| `searchTerm` | ≤200 | `ListEmployeesHandler.cs`:70-72 |
| `employmentEndDate` (depart) | required, **not before** `employmentStartDate`, no upper bound | `DepartEmployeeHandler.cs`:65-72 |
| `loginEmail` | required; the address is normalised by Identity server-side | `EmployeeWriteDtos.cs`:98-103 |

**A. Containing `@` is the *whole* email rule on the server** — `OptionalEmail` does one `Contains('@')` check and
nothing else. Do not add a stricter regex: the user cannot discover which rule is imaginary (§9.2).

**B. Trim before submitting, and send `null`, not `''`.** The server trims too, but its length checks run on what
it receives, and a C# `string?` treats `""` and `null` differently — `""` can pass a nullability check while
failing a format one (§9.3 rules E and F).

**C. The one-year start-date ceiling is invented and self-flagged in the C# source**
(`EmployeeValidation.cs`:22-26). Mirror it because the server enforces it, and expect it to change (§16).

**D. Submit is disabled only while the mutation is pending**, never because the form is invalid (§9.3 rule B).
Never reset a form on error; reset on success only.

---

## 7. Phase 6 — the list screen, the shared fieldset, and registration

**File:** `frontend/src/slices/employees/screens/EmployeeListScreen.tsx`
**File:** `frontend/src/slices/employees/components/EmployeeFieldset.tsx`
**File:** `frontend/src/slices/employees/components/RegisterEmployeeDialog.tsx`

Layout, columns and states: `EmployeesScreens.md` §4.1 and §4.4. Affordances: §4.3. Registration: §6.2. Build from
those tables; do not restate them. `EmployeeFieldset` holds the nine fields shared by the register and edit
dialogs and is created here, in the phase that first needs it, so Phase 8 composes rather than copies.

**A. There is no `/employees/new` route.** Registration is a dialog. `GeneralUIArchitecture.md` §4.1 is normative
and has no such row; inventing one contradicts this plan's governing document and leaves the route ungated.

**B. The Customer filter is drawn for the Accountant roles only, by a role check, not by `can()`.**
`ListEmployeesHandler.cs`:47-53 answers `403 "You may only list employees at your own customer."` when a Customer
Admin names another Customer. Drawing the control and then not sending it is the same lie in the other direction.

**C. Do not default the status filter to `Active`.** `EmployeesEndpoints.cs`:44 and `ListEmployeesHandler.cs`:58-61:
no filter returns both. A default that hides `Departed` rows makes a Customer Admin think the record is gone, and
nothing ever deletes an Employee (`02-AuthorizationMatrix.md` §4: *"Delete an Employee record — Nobody."*).

**D. Registration creates an accountless Employee** — no login, no email (`EmployeesEndpoints.cs`:29). The title
and the submit button say *Register*, never *Add user*, *Invite* or *Create account*. Do not merge registration and
invitation behind a "send invitation" checkbox: two endpoints, two permissions, two audit meanings, and no
transaction spanning them.

**E. Wire `/employees`** with `RequireRole` for AA, AU, CA. An `Employee` gets `AccessDeniedPage`, not a redirect
(§4.3 rule A).

### 7.1 Four ways this step goes wrong

1. **Filtering rows in the browser.** `.filter(e => e.customerId === session.customerId)` is not a safeguard; it
   conceals a server-side leak and breaks the pager, because `totalCount` counts the rows it discards.
   `EmployeeSummary` has no `customerId` at all — the shape of the API telling you not to.
2. **Rendering `role` raw, or testing it for truthiness.** `AccountantAdmin` is `0`, which is falsy (§14 rule C).
   `null` renders *Not invited* — never "Employee", which shows every accountless person as holding a role they do
   not have.
3. **Labelling the `hasAccount` column "Active".** An account existing is not an account anybody can sign in with,
   and `EmployeeSummary` has no `accountStatus` to say which.
4. **An undebounced search box.** Every keystroke is a new query key, so one POST per character. Debounce 300 ms,
   cap at 200 characters, and reset `pageNumber` to 1 on every filter change (§4.5 rules E and F) — otherwise a
   narrowed filter leaves the pager past the end and the user sees the over-run empty state instead of their rows.
   Say in the helper text that the search also matches work email, a column the table cannot show.

---

## 8. Phase 7 — the detail screen and the two status vocabularies

**File:** `frontend/src/slices/employees/screens/EmployeeDetailScreen.tsx`
**File:** `frontend/src/slices/employees/components/EmployeeStatusPair.tsx`

Layout: `EmployeesScreens.md` §5.1. Affordances: §5.4. Data: §5.2 — the employer name is a separate query against
`slices/customers/api.ts` keyed on `detail.customerId`, the one legitimate cross-slice import in the application
(§1.4 rules C and D), so a `404` on the Customer suppresses the name rather than blanking the page.

### 8.1 Two vocabularies, one screen — `EmployeeStatusPair`

Two independent statuses meet here. Both are strings, they belong to two different entities (`01-DomainModel.md`
§2) owned by two different slices, and **they share the value `"Active"`**.

| | Field | Values | Owner | Changed by |
|---|---|---|---|---|
| Employment | `status` | `"Active"`, `"Departed"` | `employees`; `Core/Employee.cs`:61-62 | `depart`, `reinstate` |
| Access | `accountStatus` | `null`, `"Invited"`, `"Active"`, `"Suspended"` | Identity, via `IIdentityApi` | `invite`, `suspend-account`, `reactivate-account`, and `depart` as a side effect |

**A. Two `StatusChip`s, each behind a visible prefix label** — `Employment: Active`, `Access: Suspended`.
`EmployeeStatusPair` renders the labels and the two chips and nothing else, so no screen can render one chip
without its label. Two bare chips reading "Active" and "Suspended" side by side are unreadable, and a single merged
chip is worse: it destroys the distinction the whole Actions menu depends on.

**B. An `Active` Employee with a `Suspended` account is a normal, expected state** — access revoked, still
employed. Render what arrived; never infer one status from the other, in either direction. `Departed` with `Active`
access does not occur today because `DepartEmployeeHandler` suspends in the same transaction — do not assert that
in the UI and do not derive the Access chip from `status`.

**C. `accountStatus === null` renders `Access: Not invited`** — not "Inactive", not an empty chip. It means no
account exists, a different fact from a suspended one.

**D. One colour map, inside the shared `StatusChip`**, so `Suspended` is never green on one screen and red on
another. The word is always shown; colour is never the only carrier (§8.4). The map is shared across all four
status vocabularies deliberately — but sharing a map does not make every word valid for every entity.

### 8.2 The `EMP` caller sees a different screen, and it is not a subset toggle

For an `Employee` the screen renders `EmployeeSelf` fields only — name, job title, work email, phone, start date —
with **no chips, no Identification card, no Actions menu**. None of those fields exists in the response, so an
"empty" chip is a rendered `undefined`. Render the Identification card only when the field is present; `can()` is
not enough here (§5.4).

That card holds two personal identifying numbers stored in plain text. Mask each behind a per-field *Show* toggle,
per mount, never persisted. **This is not a security control** — the values are in the response and in the network
tab. It stops a tax number being on screen during a screen-share; never present it as a control in review.

**Wire `/employees/:employeeId`** with `RequireRole` for all four roles. A colleague's id returns `404` from
`GetEmployeeHandler.cs`:70 by design: that handler adds a second `UserAccountId == accountId` filter for the
`Employee` role precisely so a colleague's tax number cannot be read by guessing an id. Render "Not found", never
"forbidden" (§14 rule B).

---

## 9. Phase 8 — edit and invite, and the two email affordances

**File:** `frontend/src/slices/employees/components/EditEmployeeDialog.tsx`
**File:** `frontend/src/slices/employees/components/InviteEmployeeDialog.tsx`

### 9.1 Work email and login email are two different operations

Verified in the endpoint descriptions. `EmployeesEndpoints.cs`:76-79 on `/update`: *"Changing the work email does
NOT change the address this person signs in with. The login email lives on their account — use
/api/employees/change-login-email, which is Accountant-only."* :174-177 on `/change-login-email`: *"Changes the
address the Employee SIGNS IN WITH… Leaves the work email, the password and any live session alone."*
`ChangeEmployeeLoginEmailHandler.cs`:101-104 confirms the Employee row is untouched.

**Two distinct affordances, never one field and never one dialog:**

| Affordance | Where | Changes | Gate |
|---|---|---|---|
| **Work email** field | `EditEmployeeDialog`; `/profile` when §11 unblocks | `employees.work_email` — contact information | `can(role,'UpdateEmployee')` / `UpdateOwnContact` |
| **Change login email** menu entry | Actions menu → `ChangeLoginEmailDialog` (Phase 11) | the account's sign-in address, in Identity | `can(role,'ChangeEmployeeLoginEmail')` |

**A. The distinction is stated in the form before any field is touched, and the copy differs by role.** Required in
substance by `EmployeesScreens.md` §5.5 rule A: an Accountant is pointed at *Change login email* in the Actions
menu; a Customer Admin or Employee is told that only the accounting office can change a login email, and to
contact them. Telling a Customer Admin to use an action they are refused is the same dead end in a new place.
Without this copy a Customer Admin "fixes" a colleague's login here, believes it done, and the colleague keeps
failing to sign in while nobody can find the cause.

**B. Who may change a login email, and whose.** Verified against `02-AuthorizationMatrix.md` §4 (*Change an
Employee's login email* — AA: Yes, any; AU: Yes, any; CA: **No**; EMP: **No**), `EmployeesActionCatalogue.cs`:60,
and `ChangeEmployeeLoginEmailHandler.cs`:22-25.

| Whose login email | Who may change it | Mechanism |
|---|---|---|
| An Employee's or a Customer Admin's | **Either Accountant role** | `POST /api/employees/change-login-email` |
| Your own — at any privilege level, an `AccountantAdmin`'s included | **Nobody** | none exists |
| An Accountant's | **Nobody** | none exists |

**Nobody may change their own.** There is no self-service path and no endpoint to build one against; punch-list
item 10 records the gap and states that the Accountant-only endpoint *"is not a precedent for adding one."* So this
affordance never appears on `/profile`, in any role. This is the item the punch-list previously got wrong — item 10
was amended on 2026-09-02 — so read the amended table, not a remembered version of it.

**C. The invite dialog carries the same sentence inverted.** There the address supplied *becomes* the permanent
login, and `InviteEmployeeHandler` writes it back to `WorkEmail` as well (`EmployeeWriteDtos.cs`:70-76). It is the
only moment in the person's life when that address is chosen. Say so.

### 9.2 Edit: pre-fill everything, submit everything

`UpdateEmployeeRequestDto` (`EmployeeWriteDtos.cs`:24-44) is a **full replacement**: *"omitting WorkEmail clears
it."* A form submitting only what was touched sends `null` for the rest and **silently erases the tax
identification number, the social-security number, the phone and the work email** — with a `200`, no warning and no
undo. If the detail query has not resolved, the dialog does not open. No inline-cell edit and no bulk edit, ever.

**A. A `Departed` Employee's record is still editable, deliberately** — `UpdateEmployeeHandler`: *"Correcting a
misspelled name or a wrong tax number after somebody has left is ordinary work."* Do not disable *Edit* there.

**B. `409 "An employee with this work email already exists at this customer."`** is the per-Customer uniqueness
constraint (`RegisterEmployeeHandler.cs`:28-29). Render it as a form banner with a reload affordance. It is **not**
a lost-update warning: there is no concurrency control in this backend (§9.4), `EmployeeDetail` carries no version
and no `updatedAt`, and the version-number mitigation prescribed for ticket types has **no counterpart here**. Do
not synthesise one from `createdAt`.

**C. `422 "Employment start date cannot be after the recorded employment end date."`**
(`UpdateEmployeeHandler.cs`:60-61) is reachable only on a `Departed` record. Mirror it in Zod when the loaded
detail has an `employmentEndDate`, so it never arrives as an unattachable banner.

### 9.3 Invite has no reverse

There is no un-invite and no delete-account endpoint. Once invited, the only lever is *Suspend access*, so
`ConfirmDialog` is mandatory and must name the two surprising consequences: an email goes out immediately to the
address shown, and that address becomes the person's permanent login (§8.6).

**A. A raw invitation token must never reach the browser.** `EmployeesEndpoints.cs`:111-112: *"The token is never
returned in the response — it goes to the invitee's mailbox and nowhere else."* There is nothing to put in a URL, a
log or an analytics call, and nothing to display. If a token ever appears in a response, stop and flag it.

**B. The response must not reveal whether an email already exists.** `409 "That email address is already in use."`
(`InviteEmployeeHandler.cs`:22) deliberately does not say where. Render it verbatim; do not embellish it with "at
another Customer", which the client does not know and must not imply.

**C. Hide the action, per the server's own `422`s:** `422 "A departed employee cannot be invited."` — hide when
`status === "Departed"`; `422 "No email address on file for this employee."` — block when `workEmail` is absent;
`409 "This employee already has an account."` — hide when `hasAccount === true`. Banners are the backstop, not the
design. Do not build *Resend invitation*: whether re-inviting an already-`Invited` person is supported is
unspecified (§16).

---

## 10. Phase 9 — role change and departure

**File:** `frontend/src/slices/employees/components/SetRoleDialog.tsx`
**File:** `frontend/src/slices/employees/components/DepartEmployeeDialog.tsx`

Copy, menu grouping and colour: `EmployeesScreens.md` §8.1–8.4. Build from those; the rules below are the ones a
builder gets wrong.

**A. `role` is sent as an integer** — `CustomerAdmin` is `2`, `Employee` is `3`. Use the `UserRole` const from
`shared/format/enums.ts`, never a hand-typed literal and never a string (a string is a `400`). Offer exactly two
options; do not build the select by filtering the four-role enum, or an added member becomes
`422 "An Employee's role must be CustomerAdmin or Employee."` (`EmployeeValidation.cs`:110-114) that nobody can
explain. Disable the option matching the target's current role (`SetEmployeeRoleHandler.cs`:67-68).

**B. A role change is not immediate, and the operator must be told.** Claims are minted at login, so a demoted
Customer Admin keeps administrative powers until the cookie expires — up to eight hours. The required demotion copy
(§8.3 rule C) ends with the actionable sentence: suspend their access as well if the change must take effect now.
Do not promise immediacy and do not try to fix the lag with a poll.

**C. *Suspend access* and *Mark departed* must be visibly different.** Different menu groups (*Access* vs
*Employment*, below a `Divider`), different icons, and only *Mark departed* is `color="error"` — the only red
button on the screen. Never a shared "Change status" submenu and never a single toggle. `suspend-account` revokes
access without ending employment; `depart` does both, in one transaction, and is reversible only as a *correction*.

**D. The departure dialog names the consequence and does not call it undoable.** It records the departure **and**
suspends the account in one step; `reinstate` exists to correct a mistake, not to re-hire. Do not soften the copy
into "you can always undo this". An end date is required, may be in the future, and the record flips to `Departed`
immediately regardless — do not imply it is scheduled.

**E. The at-least-one-active-Customer-Admin invariant is a `422`, not a `403`.** `EmployeeInvariants.cs`:102-103:
`422 "This Customer must always have at least one active Customer Admin."` guards demoting, departing and
suspending. Never render "permission denied" for it: the caller has the role; the data's state forbids the
operation. Leave the dialog **open**, render the `title` verbatim, and add one line — promote another Employee to
Customer Admin first, then try again. Never predict the invariant client-side: the page is one of many,
`EmployeeSummary` has no `accountStatus`, and the guard has an accepted concurrency window, so a button greyed out
on a wrong guess is worse than a `422` — the user cannot even attempt the operation to learn why.

**F. Self-action is a separate `422` from a separate guard.** `EmployeeInvariants.cs`:126: `422 "You cannot change
your own role or account status."` Hide *Change role*, *Suspend access* and *Mark departed* on the caller's own
record — but the client cannot reliably identify its own record (§15 item 2), so keep the banner as the backstop.

**G. Hide, do not disable, per the server's `422`s:** *Change role* when `hasAccount === false`
(`SetEmployeeRoleHandler.cs`:54-56); *Suspend access* / *Restore access* when `hasAccount === false`
(`SuspendEmployeeAccountHandler.cs`:48-49, `ReactivateEmployeeAccountHandler.cs`:44-45); *Restore access* when
`status === "Departed"` (`ReactivateEmployeeAccountHandler.cs`:54-58) — offer *Reinstate* instead, which restores
the account itself, so the two are never both needed.

**H. *Restore access* promises nothing about signing in.** It does not reset a password and does not clear a
lockout (`EmployeesEndpoints.cs`:206-207). Use the success copy in §8.5 rule B.

---

## 11. Phase 10 — `/profile`

**File:** `frontend/src/slices/employees/screens/ProfileScreen.tsx`

It lives in this slice because the only API call it makes belongs to this slice. Layout: §7.1. Regions and roles:
§7.2, §7.3.

> **The contact-details form cannot be built yet, and building it destroys data.** `SessionDto` is
> `(userId, displayName, role, customerId, mustChangePassword)`; `userId` is a **UserAccount** id, and
> `POST /api/employees/get` with it returns `404`. `ListEmployeesHandler` excludes the `Employee` role, and
> `/api/customers/own` returns the company. So a Customer-side caller has **no path to their own `employeeId`** and
> the form cannot be pre-filled. `UpdateOwnContactRequestDto` is a full replacement of its two fields, so an
> unfilled submit sends `{ workEmail: null, contactPhone: null }` and **erases both, with a `200` and a cheerful
> snackbar**. Punch-list item 12.

**A. Until item 12 lands, *My contact details* renders read-only, with no fields and no submit button**, above a
short notice that contact details are changed by asking a Customer Admin. §7.4 specifies the form for when the read
gap closes; do not build it now. A form that cannot be pre-filled must not be offered.

**B. No login-email affordance on this screen, in any role** (§9.1 rule B).

**C. The Accountant roles get no contact region at all.** `UpdateOwnContact` excludes them
(`EmployeesActionCatalogue.cs`:41) because an Accountant has no Employee record, so a clean `403` beats a confusing
`404`. `can()` returns `false`; the region is **hidden**, not disabled (§6.2 rule C).

**D. Render the login-email notice from static copy, never from `response.notice`.**
`UpdateOwnContactHandler.cs`:30-33 sets `Notice` on every successful **write** and `EmployeeMapper.ToSelfExpression`
never sets it on a read, so a screen keyed on its presence shows the warning *after* the mistake. Show static helper
text always, and surface `response.notice` verbatim in the success snackbar once the form exists.

**E. `404 "You do not have an employee record."`** (`UpdateOwnContactHandler.cs`:71) is a data fault the user cannot
fix. Render the `title` and stop; do not redirect and do not offer *Register*. **Wire `/profile`** for all four
roles.

---

## 12. Phase 11 — reinstate and change login email

**Do not begin this phase until the gate in §2.2 has passed.** These are the last two buttons in the slice for that
reason.

**File:** `frontend/src/slices/employees/components/ReinstateEmployeeDialog.tsx`
**File:** `frontend/src/slices/employees/components/ChangeLoginEmailDialog.tsx`

Required copy: `EmployeesScreens.md` §8.1 (reinstate) and §8.7 (change login email). Both are `ConfirmDialog`s and
both are mandatory.

**A. *Reinstate* is a correction, not a re-hire, and the copy must carry that.** `02-AuthorizationMatrix.md` §4 and
`ReinstateEmployeeHandler.cs`:24-27: somebody who genuinely left and came back is registered again as a **new**
record. Nothing can enforce the distinction — the audit entry only records which one the caller chose — so the copy
is the whole control. Visible **only** when `status === "Departed"`, in the *Employment* group next to *Mark
departed*, and **not red**: it is a repair, and `error` is reserved for the destructive direction.

**B. Reinstate restores the account itself, so *Restore access* is not a second step.**
`ReinstateEmployeeHandler.cs`:97-98 calls `ReactivateAccountAsync`, which returns the account to the state it can be
used in — `Active` for somebody who had a password, **`Invited`** for somebody still an unaccepted invitee when they
were departed. So do **not** write success copy claiming they can sign in; the `Access:` chip after invalidation is
the truth.

**C. Reinstate errors, rendered verbatim, dialog left open:** `422 "This employee has not departed."` (:67) — a
stale row; invalidate. `422 "This customer is not active."` (:74) — a suspended Customer gains nobody, and only an
Accountant can lift that.

**D. *Change login email* is gated in the menu, not in the dialog** — `can(role,'ChangeEmployeeLoginEmail')`. A
`CustomerAdmin` must not see a disabled item: a greyed-out entry invites a support request for a power deliberately
withheld. They see the §9.1 rule A helper text instead.

**E. Never pre-fill the field with the work email.** They are different addresses that are usually equal, so
pre-filling the wrong one turns a change into a silent revert. `EmployeeDetail` carries no login email; leave the
field empty.

**F. Change-login-email errors, rendered verbatim:** `422 "This employee has no account, so there is no sign-in
address to change. Invite them first."` (`ChangeEmployeeLoginEmailHandler.cs`:66-68) — hide the action when
`hasAccount === false`; `422 "This employee has departed."` (:74) — hide when `status === "Departed"` and offer
*Reinstate* first; `409` on a duplicate address, whose message deliberately does not say which account holds it.

**G. Invalidate the detail query and the list on success.** The work email did not change, so a UI that only
re-reads `workEmail` shows nothing happening and the operator runs the operation again.

---

## 13. `/api/customers/onboard` — what this plan owns and what it does not

The route is registered by **this** slice, in another slice's namespace, **deliberately and LOCKED**.
`EmployeesEndpoints.cs`:214-224 gives the reason: this slice owns two of the operation's three steps — the first
Employee and their invitation — and therefore owns the transaction that makes all three atomic. Creating the first
Customer Admin needs edges to `Identity` and to this slice's own tables that `Customers` does not have, and giving
`Customers` those edges would be a dependency cycle (`03-SliceInventory.md` §1). Splitting it in two would let a
Customer exist with nobody able to log into it. **Do not "tidy" it into the Customers slice, in either direction.**

Owned by this plan: `onboardCustomer` in `slices/employees/api.ts` (§4.1 row 13), because `api.ts` mirrors the
endpoint file, and `slices/customers/` may import it under §1.4 rule C — `api.ts` and `types.ts` only;
`OnboardCustomerRequest`/`Response` in `types.ts`, mirroring `EmployeeWriteDtos.cs`:139-173, whose body is
**nested** — `{ customer: {...}, firstAdmin: {...} }`, with `firstAdmin.workEmail` **required** — so that a
flattened body cannot bind both objects to their defaults and return `422 "Legal name is required."` for a form
that plainly had one; and the `OnboardCustomer` row in `can.ts`, an **Employees** catalogue action
(`EmployeesActionCatalogue.cs`:22), `AccountantAdmin` only.

**Not owned by this plan: the screen.** `EmployeesScreens.md` §0 and §1 note 6 place it outside this slice — *"must
not be duplicated here: two specs for one form is how the two field lists drift"* — and `CustomersScreens.md` §5
specifies it at `frontend/src/slices/customers/screens/OnboardCustomerScreen.tsx`, on the `/customers/new` route
that `GeneralUIArchitecture.md` §4.1 assigns. Its mutation hook therefore lives in `slices/customers/queries.ts`:
§1.4 rule C permits importing another slice's `api.ts` and forbids importing its `queries.ts`.

> **Drift.** This plan was commissioned on the understanding that the onboarding *screen* belongs here. Two screen
> specs, both outranking this plan, place it in the Customers slice; following them is not a choice this plan may
> make differently. Recorded in §16 so somebody decides, rather than two plans each building half a form.
>
> **Resolved 2026-09-02.** The specs win: the screen is Customers', the wire types and the `can()` row are this
> plan's. `Plans/Customers/IMPLEMENTATION_PLAN.md` §7 builds the screen and imports `onboardCustomer` from here, so
> the two halves meet at exactly one seam. Nothing below needs changing; this note stays so the next reader does not
> re-open it. (The miscitation that used to be flagged here — `CustomersScreens.md` pointing at *"§1 note 12"* when
> the note is **number 6** — was corrected in that document the same day.)

---

## 14. Cross-cutting rules for every file in this slice

**A. `can()` gates affordances, never data.** Never rely on the React app to hide anything. Out-of-scope records
must be **absent from the API response**, not merely unrendered (`02-AuthorizationMatrix.md` §12). If a screen here
is filtering rows or fields for security, the server has already leaked them and the UI is concealing a live bug.

**B. Out-of-scope rows return `404`, not `403`.** Never render "forbidden", "denied" or "no permission" for a
`404`. "Not found" is the only honest wording, and it is honest in both cases. A Customer Admin must never learn of
another Customer's employees from an error message.

**C. `role` is an integer and `0` is falsy; `status` fields are strings.** No `JsonStringEnumConverter` is
registered, so C# enums serialise as integers while properties already declared `string` serialise as strings — two
conventions in one payload, with nothing in the JSON marking the difference (punch-list item 4). `if (row.role)` is
`false` for `AccountantAdmin`, and so is `role || fallback`. Compare against a named constant and render through
`format/enums.ts`. `role` is additionally **nullable**, so distinguish `null` (not invited) from `0` explicitly.

**D. No token in `localStorage`, and nothing to store.** The session is the `aa_session` HttpOnly cookie;
JavaScript cannot read it. Every call is `credentials: 'same-origin'` — never `'omit'`, which drops the cookie, and
never `'include'`, which declares a cross-origin request in an application whose CORS is never configured and never
will be. **No API base-URL environment variable, ever**: no `VITE_API_URL`, no `import.meta.env` lookup that could
become one, no `http://` literal in any path. One build artefact must be correct everywhere.

**E. A `401` from any call means the session is gone.** Clear it and redirect to `/login` once. Never retry it,
never toast it, never render it inside a form.

**F. Every server message is in `title`.** `detail` is populated by exactly one response in this API — the
forced-password-change `403` — so reading `detail` yields `undefined` everywhere else. Render `title` verbatim; the
wording is written for the user. Show the `traceId` on `500` and nowhere else.

**G. A `422` is a form-level banner and never highlights a field.** The whole body is
`{ status, title, traceId }` — no `errors{}`, no code (punch-list item 5). A red outline on a guessed field is worse
than none. When a `422` names a rule the client could have checked, that is a **client** defect: add the Zod rule.

**H. Never offer delete, archive, hide, remove, export, import, a bulk action, or a client-side cross-Customer
search.** None has an endpoint. `Departed` is terminal in practice and the record is kept forever.

---

## 15. Known constraints

1. **Nothing in this plan has ever been run.** There is no `frontend/` directory and no local PostgreSQL on the
   authoring machine. Every route, field, limit and status code above was read from C# source; none was observed in
   a response.
2. **A Customer-side caller cannot obtain their own `employeeId`**, so `/profile`'s contact form is read-only with
   no submit button (item 12; §11).
3. **An Accountant's cross-Customer list cannot show the employer.** `EmployeeSummary` carries neither `customerId`
   nor a customer name (item 13), which is why §4.3 blocks the unfiltered list behind a "pick a Customer first"
   empty state. The list also searches work email, a column it cannot display.
4. **`/api/employees/get` returns one of two shapes and declares one** (item 6). Handled by discriminating on the
   session role (§5 rule A), never by sniffing.
5. **No optimistic concurrency anywhere.** Two Admins editing one Employee both get `200` and the second write wins
   silently; there is no version field to mitigate with.
6. **A role change lags up to eight hours** (§10 rule B), and the SPA cannot be served by the API yet — the three
   hosting lines are missing from `Program.cs` (item 1), so `npm run dev` only.

---

## 16. Questions to flag if unclear

Flag these; do not answer them by inventing a behaviour.

- [ ] **Is *Resend invitation* supported** for somebody already `Invited`? Nothing documents the behaviour beyond
      the `409`. No button until it is answered (§9.3 rule C).
- [ ] **Is the one-year future `EmploymentStartDate` ceiling real?** `EmployeeValidation.cs`:22-26 flags it as
      invented. Mirrored only because the server enforces it.
- [ ] **Should a suspended Customer's Employees see a suspension banner**, and should the eight-hour role-change lag
      be surfaced to the target rather than only to the operator? Neither is specified.

### Six questions this list carried until 2026-09-02, and how each closed

Kept, rather than deleted, because a builder who read an earlier copy of this plan will look for them. Do not
re-open any of these; each was checked against source, not reasoned about.

| Was | Closed by |
|---|---|
| `EmployeesScreens.md` describes item 26 as open, and counts eleven Employees actions | The screen spec now says thirteen in its §8 callout, its files-checklist line and criteria 3 and 16. The catalogue has both entries at `EmployeesActionCatalogue.cs`:53 and :60, so **verify those two lines still exist before writing their `can()` rows** — `Slices/Employees/` is untracked in git and the fix is uncommitted, so nothing about it can be recovered from history |
| Who owns the onboarding screen | The screen specs win; see §13. Screen and mutation hook are Customers', wire types and `can()` row are this plan's |
| Criterion 3 said "none of the eleven" against a checklist of thirteen | Corrected to thirteen in the screen spec |
| §11 flagged §2.3 rule C as saying "four are POST" | `GeneralUIArchitecture.md` §2.3 rule C says five and lists five. The flag is marked `RESOLVED` in place |
| `CustomersScreens.md` cited "EmployeesScreens.md §1 note 12" | Corrected to note 6 in that document, both occurrences |
| `UI/Plans/00-Foundation/IMPLEMENTATION_PLAN.md` did not exist | It exists. Confirmed: it leaves the Employees rows out of `can.ts` for §2 to add |

---

## Files checklist

Under `frontend/src/slices/employees/`:

- [ ] `types.ts` — three read shapes, `MarkedResult`, nine request shapes, onboarding request/response (§3)
- [ ] `api.ts` — the **thirteen** `post` wrappers of §4.1, **no** `get`, `updateOwnContact` with no id
- [ ] `queries.ts` — `useEmployeeList`, `useEmployeeDetail`, `useOwnEmployeeRecord`, one hook per write (§5)
- [ ] `schemas.ts` — the Zod schemas of §6
- [ ] `screens/EmployeeListScreen.tsx` (§7)
- [ ] `screens/EmployeeDetailScreen.tsx` (§8)
- [ ] `screens/ProfileScreen.tsx` (§11)
- [ ] `components/EmployeeFieldset.tsx` — the nine shared fields (§7)
- [ ] `components/RegisterEmployeeDialog.tsx` (§7)
- [ ] `components/EditEmployeeDialog.tsx` (§9.2)
- [ ] `components/InviteEmployeeDialog.tsx` (§9.3)
- [ ] `components/SetRoleDialog.tsx` (§10)
- [ ] `components/DepartEmployeeDialog.tsx` (§10)
- [ ] `components/EmployeeStatusPair.tsx` — the two labelled `StatusChip`s (§8.1)
- [ ] `components/ReinstateEmployeeDialog.tsx` (§12, gated)
- [ ] `components/ChangeLoginEmailDialog.tsx` (§12, gated)

The only two permitted edits outside the slice folder:

- [ ] `frontend/src/routes.tsx` — the rows `/employees`, `/employees/:employeeId`, `/profile`, wired with
      `RequireRole`; **no** `/employees/new`, **no** `/employees/:employeeId/edit`
- [ ] `frontend/src/shared/permissions/can.ts` + `actions.ts` — the **thirteen** rows of §2.3, verified against
      `EmployeesActionCatalogue.cs`. Eleven if the §2.2 gate fails

---

## Success criteria

Each is verified by running the app, not by reading the code.

1. The Employees rows in `can.ts` match `EmployeesActionCatalogue.cs` exactly — same action names, same role sets,
   **no extras on either side**. Thirteen today; the count is not the criterion, the exact match is.
2. As an `AccountantAdmin`, *Reinstate* on a `Departed` employee and *Change login email* on an employee with an
   account both return `200`, not `403`, and neither writes a `PermissionDenied` row into the audit log.
3. *Change login email* is absent — not disabled — for a `CustomerAdmin` and an `Employee` at every entry point, and
   absent from `/profile` in all four roles.
4. `api.ts` issues thirteen distinct `POST`s and no `GET`, and none has been "corrected" to `GET`.
5. `slices/employees/` contains no expression testing a field's presence to decide which DTO arrived — no
   `'status' in`, no `?.status !== undefined`, no union discriminated on optionality.
6. An `Employee` opening their own `/employees/:employeeId` sees a complete screen with no blank field, no empty
   chip and no `undefined`; the same URL with a colleague's id says **"Not found"**, never "forbidden".
7. A detail screen for an employed person with revoked access shows `Employment: Active` **and**
   `Access: Suspended`, both labelled, and a never-invited person shows `Access: Not invited`.
8. Editing one field and saving leaves every other field byte-identical in a subsequent `POST /api/employees/get`.
9. Both edit forms and the invite dialog show the work-email-versus-login-email copy before any interaction, and the
   wording differs between an Accountant and a Customer Admin.
10. `updateOwnContact` has no id parameter, no call site passes one, and `/profile` offers no way to submit empty
    contact details in any role.
11. Demoting, departing or suspending the last active Customer Admin renders the server's `422` verbatim, leaves the
    dialog open and names promotion as the way forward; no button was pre-emptively disabled to guess at the
    invariant.
12. The *Change role* select offers exactly two options, sends an **integer**, and disables the option matching the
    target's current role.
13. With no status filter the list contains both `Active` and `Departed` rows, and a `pageSize: 999` request renders
    a pager consistent with the `50` the server returned.
14. *Suspend access* and *Mark departed* are distinguishable at a glance, only *Mark departed* is red, and *Restore
    access* is replaced by *Reinstate* on a `Departed` row.
15. No screen renders a raw role integer, a status word outside its own entity's vocabulary, the word "Client",
    "User" as a role label, or "Admin" unqualified; none offers delete, archive, export, import or a bulk action;
    and no invitation token appears in a URL, a console log or any request the SPA makes.
16. Letting the session expire and reloading `/employees` redirects to `/login` once, with no retry storm and no
    toast.
