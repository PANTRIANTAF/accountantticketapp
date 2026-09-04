# Customers Screens — UI Implementation Plan

This is an executable step-by-step plan for one slice of the React SPA: the Customer list at
`/customers`, the Customer detail at `/customers/:customerId`, the create form at `/customers/new`,
the two separately gated edit dialogs, suspend and reactivate, and the "my Customer" card at
`/my-customer`. Follow it in order. It assumes **Phase 0 is already built** (§0.1), and it assumes
you have read `../../GeneralUIArchitecture.md` in full and `../../Screens/CustomersScreens.md` once.
It does not restate either: `CustomersScreens.md` says *what* the screens are; this document says in
what order you create which files under `frontend/src/slices/customers/`, what goes in each, and how
a future builder checks it.

Build it after Phase 0. It has one ordering constraint on another slice — `/customers/new` calls an
endpoint the `Employees` slice registers (§0.2) — and nothing else in the application depends on
this slice. If something here is unclear, **flag it (§15); do not invent a behaviour.**

**Documents that govern this document, in precedence order.** Where any of them disagrees with this
one, **it wins and this document is wrong** — fix this document, do not code around it.

| # | Document | Sections that bind this plan |
|---|---|---|
| 1 | `../../../README.md` | *Locked platform decisions*, *Conflict precedence*, *Customers are businesses* |
| 2 | `../../../00-Glossary.md` | Banned terms and *Customer*; binding in UI copy |
| 3 | `../../../01-DomainModel.md` | §1–2 — the tenant boundary; which entity owns `Invited` |
| 4 | `../../../02-AuthorizationMatrix.md` | §1, §3, §11, §12 — normative on every gate below |
| 5 | `../../../03-SliceInventory.md` | §1 — why onboarding lives in `Employees` |
| 6 | `../../../04-Infrastructure.md` | §1–3 — hosting, one origin, the dev loop |
| 7 | `../../../App/GeneralAppArchitecture.md` | §8 — route shape, pagination, error contract |
| 8 | `../../GeneralUIArchitecture.md` | §1.2 tree, §1.4 A/C/E, §2.1–2.5, §3.1–3.4, §4.1/§4.3, §6, §7.1–7.4, §8.2–8.4, §9.2–9.4, §10.1–10.2 |
| 9 | `../../LoginArchitecture.md` | §1.2 session states, §8 the role enum |
| 10 | `../../Screens/CustomersScreens.md` | **The screen spec. Normative for every rule below.** |
| 11 | `../../BACKEND_CHANGES_REQUIRED.md` | Non-normative. Cited by item number only |
| 12 | This plan | Loses to all of the above |

---

## 0. Prerequisites and build position

### 0.1 Phase 0 is a prerequisite, not a step in this plan

Everything under `frontend/src/shared/` and the four root files come from
`../00-Foundation/IMPLEMENTATION_PLAN.md`. **Do not start section 1 until all of these exist and the
app runs.** If one is missing, stop and build Phase 0; do not create a local substitute inside this
slice.

| Needed here | Where it comes from | Used for |
|---|---|---|
| `shared/api/http.ts` — `get`, `post` | Phase 0 (`GeneralUIArchitecture.md` §2.1) | Every call in §2. The only `fetch` in the app |
| `shared/api/ApiError.ts`, `problemDetails.ts` | Phase 0 (§2.2) | `ErrorBanner` input; the `status` every rule below branches on |
| `shared/api/paginated.ts` — `PaginatedResponse<T>`, `DEFAULT_PAGE_SIZE`, `MAX_PAGE_SIZE` | Phase 0 (§3.3) | The list envelope in §3 |
| `shared/api/queryClient.ts` | Phase 0 (§3.4) | `retry` on 4xx, `staleTime`, `refetchOnWindowFocus: false` |
| `shared/auth/SessionProvider.tsx`, `useSession.ts` | Phase 0 (`LoginArchitecture.md` §1.2) | The role every `can()` call reads |
| `shared/auth/RequireRole.tsx` | Phase 0 (§4.3) | The four route gates in §11 |
| `shared/permissions/can.ts`, `actions.ts` | Phase 0 (§6.1) | Nine action names, verified in §11.2 |
| `shared/components/PaginatedTable.tsx` | Phase 0 (§8.2) | The one 1-based/0-based conversion, imported not copied |
| `shared/components/StatusChip.tsx` | Phase 0 (§8.3) | The Customer chip. **Read §12.2 before using it** |
| `PageHeader`, `ConfirmDialog`, `ErrorBanner`, `EmptyState`, `LoadingRegion`, `NotFoundPage`, `AccessDeniedPage` | Phase 0 (§8.3) | §5–§10 |
| `shared/hooks/usePaginatedQuery.ts` | Phase 0 (§3.3) | §3.1. Mandatory for the list (§3.2 rule G) |
| `shared/format/dates.ts` | Phase 0 (§10.2) | `onboardedOn`, `createdAt`, `updatedAt` |
| `routes.tsx`, `theme.ts` | Phase 0 (§4.1, §8.1) | §11 |

**This plan creates nothing under `frontend/src/shared/`.** Not a component, not a hook, not a
formatter, not a permission row. If a step below appears to need one, that is a Phase 0 gap and it
belongs in §15 — not in this plan as a fact, and not in this slice as a private copy.

`frontend/src/routes.tsx` is the one file outside this slice that this plan edits, and §11 states the
exact extent of the edit. That file already imports every slice's screens by design
(`GeneralUIArchitecture.md` §1.4 rule E).

### 0.2 Creating a Customer and onboarding its first Customer Admin are one operation in another slice

`POST /api/customers/onboard` is registered by **`EmployeesEndpoints.cs:227`**, deliberately, because
that slice owns steps 2 and 3 of the transaction and therefore owns the transaction
(`OnboardCustomerHandler.cs:16-28`; `03-SliceInventory.md` §1; `02-AuthorizationMatrix.md` §3). The
boundary, stated once so it is not built twice:

**A.** The wrapper `onboardCustomer` and the types `OnboardCustomerRequest` /
`OnboardCustomerResponse` belong to `frontend/src/slices/employees/api.ts` and
`.../employees/types.ts`, because `api.ts` mirrors the endpoint file
(`CustomersScreens.md` §2; `GeneralUIArchitecture.md` §2.5). **Do not write a second wrapper in
`customers/api.ts`.** Two wrappers over one endpoint means two field lists, and they drift the first
time a Customer field is added.

**B.** The *screen* `OnboardCustomerScreen.tsx` belongs to **this** slice, at `/customers/new`, per
the route table (`GeneralUIArchitecture.md` §4.1) and `CustomersScreens.md` §5. Importing
`employees/api.ts` and `employees/types.ts` from `customers/screens/` is permitted and is the second
legitimate use of §1.4 rule C. Importing anything else from `slices/employees/` — a screen, a
component, `queries.ts` — is not.

**C.** If the Employees slice has not been built yet, create in `slices/employees/` **exactly** the
two exports §7 needs and nothing else: no `queries.ts`, no screens, no components. When the Employees
plan runs it finds them present and changes them not at all. If they are already present, this plan
changes them not at all either. Do not resolve the ordering by copying the wrapper here.

**D.** Everything else about onboarding an Employee — invite, register, set role, depart, reinstate —
is the Employees plan's, including the *View employees* link's destination screen. §6 links to
`/employees` with a router `<Link>`; a link is routing, not a dependency.

### 0.3 What was verified in the C# source, with line numbers

Every row was read out of the code, not out of the screen spec. Re-verify any row you are about to
depend on. Do not "correct" a verb, a bound or a status code to match your expectations.

| Fact | Source |
|---|---|
| `POST /api/customers/list` — a **POST read** taking a body | `Slices/Customers/CustomersEndpoints.cs:30` |
| `GET /api/customers/detail` — `Guid customerId` from the **query string** | `CustomersEndpoints.cs:41-47` |
| `GET /api/customers/own` — **no parameter of any kind** | `CustomersEndpoints.cs:53-57` |
| `POST /api/customers/update-contact`, `/update-legal`, `/suspend`, `/reactivate` | `CustomersEndpoints.cs:63, 75, 88, 100` |
| `/update-legal` is the only one of the four declaring a `409` | `CustomersEndpoints.cs:85` |
| `POST /api/customers/create` returns **201** with a `Location` header. **No screen calls it** | `CustomersEndpoints.cs:15-28` |
| `POST /api/customers/onboard` is registered from **Employees**, returns **200** (`Results.Ok`) | `Slices/Employees/EmployeesEndpoints.cs:227-241` |
| Eight Customers actions and their exact role sets | `Slices/Customers/CustomersActionCatalogue.cs:13-22` |
| `OnboardCustomer` = `[AccountantAdmin]`, in the **Employees** catalogue | `Slices/Employees/EmployeesActionCatalogue.cs:22` |
| List body `{ status?, search?, pageNumber=1, pageSize=15 }`; row is four keys, nothing more | `ListCustomersRequestDto.cs:5-8`; `CustomerSummaryDto.cs:5-8`; `CustomerMapper.cs:9-16` |
| `CustomerDto` 16 keys; `CustomerSelfDto` omits `taxNumber`, `taxOffice`, `onboardedOn`, `createdAt`, `updatedAt` | `CustomerDto.cs:5-20`; `CustomerSelfDto.cs:5-16`; `CustomerMapper.cs:18-51` |
| Contact request = `customerId` + **seven**; legal = `customerId` + **four**; status = `{ customerId, reason? }` | `UpdateCustomerContactRequestDto.cs:5-12`; `UpdateCustomerLegalRequestDto.cs:5-9`; `SetCustomerStatusRequestDto.cs:5-6` |
| `CustomerStatus` declares **exactly two** values, and a `CHECK` constraint enforces them | `Core/Customer.cs:27-31`; `Migrations/20260901_002_AddCustomerStatusCheck.sql:10-11` |
| Status filter compared **after `Trim()` and case-sensitively**; anything else → `422 "Unknown customer status."` | `Application/Handlers/ListCustomersHandler.cs:31-33` |
| `search` > 200 → `422`; `ILIKE` over `legalName` **or** `tradingName`; `%`, `_`, `\` escaped server-side | `ListCustomersHandler.cs:46-52, 73-76` |
| Ordering is `legalName` then `id`; there is **no** sort parameter | `ListCustomersHandler.cs:56-57` |
| `pageSize` **clamped** to `[1,50]`, `pageNumber` raised to 1; never rejected | `Shared/Pagination/PaginatedQuery.cs:10-12` |
| Out-of-scope or unknown id → `404 "Customer not found."` on all six id-taking routes | `GetCustomerHandler.cs:29`; `GetOwnCustomerHandler.cs:31`; `UpdateCustomerContactHandler.cs:43`; `UpdateCustomerLegalHandler.cs:45`; `SuspendCustomerHandler.cs:48`; `ReactivateCustomerHandler.cs:46` |
| Scope filters on the **primary key**, and is a pass-through for both Accountant roles | `Shared/Authorization/CustomerScope.cs:37-41` |
| `422 "This customer is already suspended."` / `"…already active."` | `SuspendCustomerHandler.cs:49-50`; `ReactivateCustomerHandler.cs:47-48` |
| `409 "A customer with this tax number already exists."` — pre-check **and** a `23505` catch | `UpdateCustomerLegalHandler.cs:48-50, 62-65` |
| `409 "That email address is already in use."` — Identity's 409, rewritten so it names no Customer | `OnboardCustomerHandler.cs:116-121` |
| Onboard body is **nested** `{ customer, firstAdmin }`; response is **three ids and no token** | `EmployeeWriteDtos.cs:139-173`; `ICustomerApi.cs:18-32`; `OnboardCustomerHandler.cs:153-160` |
| The first person's role is `CustomerAdmin`, chosen by the handler, **not a request field**; `firstAdmin` is validated **before** the Customer half | `OnboardCustomerHandler.cs:61-68, 74, 104-114` |
| Every string is `Trim()`ed server-side; an empty optional becomes `null`; `reason` ≤ 500 | `CustomerValidation.cs:32, 64-82` |
| Field limits (transcribed in §4) | `CustomerValidation.cs:10-18, 34-43, 56-62`; `EmployeeValidation.cs:26, 86-104, 138-148` |
| Denial is fail-closed `403`, title `"Permission denied for action '<Name>'."`; error body is `{ status, title, traceId }` | `PermissionChecker.cs:41, 63`; `AppExceptionMiddleware.cs:53-58` |
| Tax number is unique on the **raw** value: `EL1` and `el1` are two Customers | `CustomerConfiguration.cs:30`; `UpdateCustomerLegalHandler.cs:48-49` |

### 0.4 Drift between the specs and the code — flagged, not smoothed over

> **`/api/customers/own` answers an Accountant with `403`, not the `401`
> `CustomersScreens.md` §6.4 rule A describes.** `GetOwnCustomerHandler.cs:24` calls
> `RequireAsync(user, "ViewOwnCustomer")` **before** the null-`CustomerId` check at `:25-26`, and
> `ViewOwnCustomer` is `[CustomerAdmin, Employee]` (`CustomersActionCatalogue.cs:22`). Both Accountant
> roles therefore fail the role check first and receive
> `403 "Permission denied for action 'ViewOwnCustomer'."`. The `401 "Authentication required."` is
> reachable only for a Customer-side session whose `customerId` is null.
> `CustomersEndpoints.cs:53-61` compounds it by declaring `.Produces(401)` and `.Produces(404)` and
> **not** the one denial that is actually reachable. **What you do about it in code: nothing.** The
> route is `RequireRole`d to CA and EMP (§11), so neither status is reachable through the router, and
> rule A's conclusion stands. Do not build a 401 branch here.

> **`CustomersScreens.md` §1 says "every row naming a `customerId` is additionally scoped
> server-side". Suspend and reactivate are not.** `SuspendCustomerHandler.cs:47` and
> `ReactivateCustomerHandler.cs:45` load the row with a bare `FirstOrDefaultAsync(item => item.Id ==
> request.CustomerId)` and no `WhereMatchesCustomerScope`. Harmless today — both actions are
> `[AccountantAdmin]` (`CustomersActionCatalogue.cs:14-15`) and the filter is a pass-through for
> Accountants anyway (`CustomerScope.cs:39-40`). **No UI consequence and no UI workaround**; recorded
> because the sentence is not universally true, and a catalogue edit widening either action would
> silently make it a cross-Customer write.

> **The `Invited` question in `CustomersScreens.md` §3.5 and §9 has already been answered upstream.**
> Both claim `GeneralUIArchitecture.md` §10.1 lists `Customer.status` as
> `"Invited" | "Active" | "Suspended"`. It no longer does: §10.1's table now reads
> `"Active" | "Suspended"` **only**. The code agrees (`Customer.cs:27-31`,
> `20260901_002_AddCustomerStatusCheck.sql:10-11`). No live conflict — but **the trap survives the
> resolution** and §12.2 carries it, because `Invited` is a real word here; it belongs to a
> `UserAccount`.

---

## 1. Step 1 — `types.ts`

**File:** `frontend/src/slices/customers/types.ts`

Hand-written interfaces mirroring the C# DTOs, `camelCase`, `Guid` → `string`, each commented with
the file it mirrors so the next reader can diff them (`GeneralUIArchitecture.md` §2.5). Eight
exports: `CustomerStatus`, `CustomerSummary`, `Customer`, `CustomerSelf`,
`ListCustomersRequest`, `UpdateCustomerContactRequest`, `UpdateCustomerLegalRequest`,
`SetCustomerStatusRequest`.

```ts
/** Mirrors Slices/Customers/Core/Customer.cs:27-31 -- exactly two values. NEVER add 'Invited';
 *  that is a UserAccount status (01-DomainModel.md section 2) and the database rejects it
 *  (20260901_002_AddCustomerStatusCheck.sql). */
export type CustomerStatus = 'Active' | 'Suspended';

/** Mirrors Application/Dtos/CustomerSummaryDto.cs -- four keys, and there is no fifth to add. */
export interface CustomerSummary {
  id: string;
  legalName: string;
  tradingName: string | null;
  status: CustomerStatus;
}

/** Mirrors Application/Dtos/CustomerSelfDto.cs. Narrower than Customer ON THE SERVER: five fields
 *  are absent from the response, not merely unrendered (02-AuthorizationMatrix.md:311). */
export interface CustomerSelf { /* id, legalName, tradingName, the five address fields,
                                  contactEmail, contactPhone, status -- eleven keys */ }
```

`Customer` carries all sixteen keys of `CustomerDto.cs:5-20`. `onboardedOn` is `string` (a `DateOnly`
on the wire: `"2026-09-02"`, no timezone); `createdAt` and `updatedAt` are `string` (a
`DateTimeOffset`, offset present, parses directly) — §10.2.

**A. `CustomerSelf` is a separate interface, not `Partial<Customer>` or `Omit<Customer, …>`.** A
derived type invites a component that accepts either and reads `taxNumber` off whichever it got.
Two names for two shapes is the whole point of §10's cache rule.

**B. No `role` anywhere in this file.** Nothing in this slice sends or renders a role: the
`CustomerAdmin` role the onboarding handler assigns is chosen server-side and never crosses the wire
(`OnboardCustomerHandler.cs:114`). The integer-vs-string trap of §10.1 still binds every `status`
field here — see §12.2 — but there is no integer in this slice to get wrong.

**C. No `CreateCustomerRequest`.** §2 rule A.

### What this step does NOT do, and why

- **No `taxNumber` on `CustomerSelf`.** It is not in the response. Adding an optional key to make one
  component serve both screens is how a "field this screen is specified not to show" becomes
  `undefined` rendered as a blank row rather than an absent one.
- **No `employeeCount`, `ticketCount`, `customerAdminCount`.** No endpoint returns any of them
  (§15). A field with no producer is a field somebody resolves per row with fifteen extra requests.
- **No union with the Employees slice's onboarding types.** They live there (§0.2 rule A).

---

## 2. Step 2 — `api.ts`

**File:** `frontend/src/slices/customers/api.ts`

**Seven** functions, no React, no hooks, no TanStack Query — a plain typed wrapper readable line by
line against `CustomersEndpoints.cs`.

```ts
import { get, post } from '../../shared/api/http';
import type { PaginatedResponse } from '../../shared/api/paginated';
import type { Customer, CustomerSelf, CustomerSummary /* …the request types */ } from './types';

/** POST, not GET. A POST read with a filter body: CustomersEndpoints.cs:30, and
 *  GeneralUIArchitecture.md section 2.3 rule C names this exact route. pageNumber and pageSize are
 *  BODY fields here, not query parameters. */
export const listCustomers = (
  body: ListCustomersRequest,
): Promise<PaginatedResponse<CustomerSummary>> => post('/api/customers/list', body);

/** GET with a query parameter -- the one departure from section 2.3 rule D in this slice
 *  (CustomersEndpoints.cs:41). URLSearchParams, never concatenation. */
export const getCustomer = (customerId: string): Promise<Customer> =>
  get(`/api/customers/detail?${new URLSearchParams({ customerId })}`);

/** GET, and NO argument. The id comes from the session cookie (GetOwnCustomerHandler.cs:25). */
export const getOwnCustomer = (): Promise<CustomerSelf> => get('/api/customers/own');
```

The remaining four are `updateCustomerContact`, `updateCustomerLegal`, `suspendCustomer` and
`reactivateCustomer` — each a one-line `post` returning `Promise<Customer>`, because all four return
the full `CustomerDto` (`CustomersEndpoints.cs:70, 82, 95, 105`).

**A. There is no `createCustomer`, and you may not write one.** `CustomersScreens.md` §1 note 4 and
§7 rule C: `POST /api/customers/create` makes a Customer with no Employee and no account, which
`02-AuthorizationMatrix.md` §3 calls useless and which violates the at-least-one-active-Customer-Admin
invariant from the moment it exists. It is a building block for `ICustomerApi.CreateAsync`
(`ICustomerApi.cs:61-76`) and nothing else. **Do not write even an unused wrapper** — an unused
wrapper is an invitation to the two-step wizard §7.2 forbids. Success criterion 3 greps for it.

**B. The verbs are asymmetric and the `list` suffix predicts nothing.** `/api/customers/list` is
`POST`; `/api/customers/detail` and `/api/customers/own` are `GET`. **Exactly five of the reads
this SPA calls are `POST`**, and `GeneralUIArchitecture.md` §2.3 rule C tables all five; two of
them are in this slice's neighbourhood. Nine POST reads exist on disk — the other four belong to
`Slices/Tickets/` and no screen in this specification calls them, which is why that rule's table
stops at five. Meanwhile `/api/ticket-types/list` and `/api/accountants/list` are `GET`
with a query string. Read the verb off the endpoint file for the route you are calling. "Correcting" a
POST read to a `GET` matches no route and returns **405** with nothing in the body to explain it — on
the Accountant's landing screen, which is the worst place for it.

**C. Always send the whole `list` body.** `ListCustomersRequestDto` is a required parameter
(`CustomersEndpoints.cs:31`), so an absent body is a `400` about a missing request body, not a `200`
with the DTO's defaults. Send all four keys every call.

**D. Omit `status` — or send `null` — for "both". Never `''`.** `ListCustomersHandler.cs:31-33`
trims and then compares case-sensitively against the two constants, so `""` and `"active"` are both
`422 "Unknown customer status."` (§9.3 rule F).

**E. Build the `detail` query string with `URLSearchParams`.** A concatenated `undefined` sends
`customerId=undefined`, which fails minimal-API model binding and surfaces through
`AppExceptionMiddleware.cs:31-36` as a `400` whose `title` names a C# parameter. That message is
correct and unusable; the guard is not rendering it better, it is not sending it.

---

## 3. Step 3 — `queries.ts`

**File:** `frontend/src/slices/customers/queries.ts`

Seven hooks: `useCustomerList`, `useCustomer`, `useOwnCustomer`, `useUpdateCustomerContact`,
`useUpdateCustomerLegal`, `useSuspendCustomer`, `useReactivateCustomer`. Screens import hooks and
**never** `api.ts` (§3.2 rule A).

### 3.1 Query keys, exactly

| Query | Key |
|---|---|
| The list | `['customers', 'list', { status, search, pageNumber, pageSize }]` |
| One Customer | `['customers', 'detail', customerId]` |
| Own Customer | `['customers', 'own']` |

**A. Every filter is in the key.** Omit `search` and two different searches share one cache entry, so
the table shows the previous query's rows under the new query's pager
(`CustomersScreens.md` §3.2 rule A).

**B. `['customers','own']` takes no discriminator, deliberately.** An id in that key would imply a
screen that could show a different Customer, and `/api/customers/own` cannot
(`CustomersScreens.md` §6.1).

**C. `useCustomerList` is built on `usePaginatedQuery`** and nothing else (§3.2 rule G), so the
clamping trap is handled in one place.

**D. `enabled` appears once**, on `useCustomer`, and only to express "the id is not known yet
mid-navigation". Never to express "not allowed" (§3.2 rule B). If a screen should be unreachable,
gate the route (§11).

### 3.2 Mutation invalidations, stated per hook

All four writes return the full `Customer`, so all four **seed** (§3.2 rule D):

```ts
onSuccess: (updated) => {
  queryClient.setQueryData(['customers', 'detail', updated.id], updated);
  queryClient.invalidateQueries({ queryKey: ['customers', 'list'] });
}
```

`useUpdateCustomerContact` adds one line and it is the subtle one:

```ts
  queryClient.invalidateQueries({ queryKey: ['customers', 'own'] });   // invalidate, NEVER seed
```

**A. Never seed `['customers','own']`.** `update-contact` returns the wide `Customer`; that key holds
the narrow `CustomerSelf`. Writing one into the other puts `taxNumber`, `taxOffice`, `onboardedOn`,
`createdAt` and `updatedAt` into a cache entry typed `CustomerSelf`, and the next component that
renders "everything in `own`" starts showing fields `/my-customer` is specified not to show — from the
cache, on a screen an `Employee` can open. Invalidate; it is one extra `GET` on a rarely visited
screen (`CustomersScreens.md` §6.1).

**B. One `useUpdateCustomerContact` serves both screens.** `CustomerScope` restricts the write to the
caller's own row regardless (`UpdateCustomerContactHandler.cs:39-43`), so there is no second endpoint,
no CA-specific hook, and no second dialog. The hook seeds `detail`, invalidates `list`, invalidates
`own`; on `/my-customer` the first two are no-ops in that browser and cost nothing.

**C. Suspend and reactivate do *not* invalidate `['customers','own']`.** Only an `AccountantAdmin` can
call them, and an Accountant cannot populate that key at all (§0.4, first blockquote). Invalidating it
would be a no-op that tells the next reader the author believed a cache existed that never does.

**D. No optimistic updates, anywhere in this slice.** §3.2 rule E, and here the guess is concretely
wrong: every string is trimmed and normalised server-side (`CustomerValidation.cs:64-82`), so a cache
entry assembled from form values differs from the row that was written.

**E. `retry: false` on every mutation**, inherited from Phase 0's `queryClient` (§3.4). No endpoint
here is idempotent, every write audits (`UpdateCustomerContactHandler.cs:56-62`), and a retried
`/suspend` after a timeout is a second audited transition for one operator action.

### What this step does NOT do, and why

- **No `refetchInterval`.** §3.2 rule H allows exactly one polling query in the application and it is
  the notification unread count. Not the list, not the detail, not `own`.
- **No `can()` call.** `queries.ts` fetches; §6.1 decides what to draw. A hook that hides a fetch
  behind a permission check is §3.2 rule B in disguise.
- **No `onError` that swallows a `403`.** §6.2 rule B: a `can()` of `true` followed by a `403` is a bug
  in Phase 0's table, and a `catch` is how it stays one.

---

## 4. Step 4 — `schemas.ts`

**File:** `frontend/src/slices/customers/schemas.ts`

Four exports: `customerBlockSchema`, `firstAdminBlockSchema`, `legalSchema`, `contactSchema`. Zod,
wired through `zodResolver`, `mode: 'onBlur'` (§9.3 rule A). Transcribe the limits from
`CustomersScreens.md` §5.5, which this plan has re-verified line by line against
`CustomerValidation.cs:10-18, 34-43, 56-62` and `EmployeeValidation.cs:86-104, 138-148`.

**A. Mirror the server exactly — neither stricter nor looser** (§9.2). Stricter blocks legitimate
input; looser produces a `422` banner with nothing tying it to an input, because the error body has no
`errors{}` dictionary (`AppExceptionMiddleware.cs:53-58`; punch-list item 5).

**B. The email rule is deliberately weak.** `CustomerValidation.cs:56-62` checks only for `'@'`.
`z.string().email()` rejects addresses the API accepts, and the user has no way to discover which rule
was ours.

**C. Trim before submitting, and send `null` for an untouched optional** (§9.3 rules E and F). The
server trims too, but its length check runs on what it *receives*: a trailing space that pushes a
300-character legal name to 301 is a `422` about a limit the user appears to be within.

**D. `onboardedOn` is a `DateOnly` string.** Compare as a string against today-plus-one-day; never via
`new Date()`, which shifts the boundary a day west of UTC (§10.2). The ceiling is **+1 day**
(`CustomerValidation.cs:17`); `employmentStartDate`'s is **+1 year**
(`EmployeeValidation.cs:26, 143`). Two different ceilings on two date fields in one form.

**E. `legalSchema` and `contactSchema` are separate schemas over disjoint field sets.** §8.1 says why,
and it is a permission boundary, not a convenience.

---

## 5. Step 5 — `CustomerListScreen.tsx`

**File:** `frontend/src/slices/customers/screens/CustomerListScreen.tsx`

`PageHeader` with an *Add Customer* action slot, a search box, a status select, a `PaginatedTable` of
three columns, and the pager. Implement `CustomersScreens.md` §3 in full: §3.1's three columns, §3.4's
six states, §3.5's five rules.

**A. Three columns — legal name, trading name, status — and no fourth.** `CustomerSummaryDto` has
four keys including `id` (`CustomerSummaryDto.cs:5-8`). Contact email, city, employee count and
onboarded date are not in it, and resolving one per row is fifteen extra requests per page.

**B. Render the pager from `response.pageSize`, never from the value sent.** `PaginatedQuery.cs:10-12`
clamps to 50 with a `200` (§2.4 item 6; punch-list item 17). Never offer a page-size option above 50.

**C. Debounce `search` by 300ms and cap the input at 200 characters.** Every keystroke is a new query
key, so an undebounced box is one `POST` per character, and character 201 is a `422`
(`ListCustomersHandler.cs:46-47`).

**D. Label the box "Search legal or trading name".** `ILIKE` covers those two columns and nothing else
(`ListCustomersHandler.cs:48-52`) — not tax number, not city, not contact email. A box labelled
"Search" that silently ignores a pasted tax number reads as missing data. Do not strip `%` or `_`;
they are escaped server-side (`ListCustomersHandler.cs:73-76`).

**E. The status select offers exactly two options plus "All"**, and *All* **omits the key**. §12.2.

**F. Reset `pageNumber` to 1 whenever a filter changes**, or a narrowed filter leaves the pager on
page 4 of a one-page result and the user gets the over-run empty state instead of their rows.

**G. No column sort, no export, no bulk action, no row-level suspend.** There is no sort parameter
(`ListCustomersHandler.cs:56-57`), so a clickable header would reorder the fifteen rows on screen out
of a hundred and look like corrupt data. And a row menu offering *Suspend* two pixels from *Open* is
how a whole company's staff loses its logins by mis-click (`CustomersScreens.md` §3.3).

### 5.1 Four ways this step goes wrong

1. **`GET`ting the list**, because the route ends in `list`. A `405`, on the landing screen for both
   Accountant roles (§2 rule B).
2. **Sending `status: ''` for "All".** A `422` on selection, which reads as a server bug
   (§2 rule D).
3. **`items.length === 0` rendered as "no results" unconditionally.** With `totalCount > 0` it is an
   over-run page and the fix is *Back to the first page* (§3.3 item 2). "No customers match these
   filters" on a Customer that exists is a report of missing data.
4. **A client-side `.filter(...)` over the rows.** §13 rule A. It is not a safeguard; it is evidence
   of a server-side leak being concealed, and it breaks the pager because `totalCount` counts the rows
   it discards.

---

## 6. Step 6 — `CustomerDetailScreen.tsx`

**File:** `frontend/src/slices/customers/screens/CustomerDetailScreen.tsx`

`useCustomer(customerId)`, then §4.1's three cards — Legal, Contact, Record — a `StatusChip` beside the
heading, an *Actions* menu, and a `<Link to="/employees">` carrying the Customer filter. The four
dialogs it opens are built in §8 and §9; until then, render the buttons and wire them to nothing rather
than inventing a placeholder form.

**A. Affordances are gated exactly as `CustomersScreens.md` §4.4 tables them**, and every row of that
table was re-verified against `CustomersActionCatalogue.cs:13-22`:

| Affordance | Gate | Catalogue line |
|---|---|---|
| *Edit contact* | `can(role, 'EditCustomerContact')` — AA, AU, **CA** | `:18-19` |
| *Edit legal* | `can(role, 'EditCustomerLegal')` — AA, AU **only** | `:17` |
| *Actions → Suspend* | `can(role, 'SuspendCustomer')` **and** `status === 'Active'` | `:14` |
| *Actions → Reactivate* | `can(role, 'ReactivateCustomer')` **and** `status === 'Suspended'` | `:15` |
| *View employees* | always; a `<Link>`, not an import | — |

**B. The whole *Actions* menu is absent for an `AccountantUser`**, not rendered disabled. A menu whose
only two items can never be enabled is noise (§6.2 rule C).

**C. A mismatch here produces a button the server 403s.** `PermissionChecker.cs:41` is fail-closed and
audits every denial before throwing (`:49-63`), so a wrong gate is not a cosmetic error — it is a
`403` the user cannot act on plus a `PermissionDenied` row against their name in the one log an
investigator is supposed to trust. If `can()` says `true` and the server says `403`, fix Phase 0's
table (§6.2 rule B); do not catch the error.

**D. `404` renders `NotFoundPage`.** Never "forbidden", "denied" or "no permission" (§13 rule B).

**E. `onboardedOn`, `createdAt` and `updatedAt` are rendered and never put in an input.** No endpoint
changes any of them (`UpdateCustomerLegalRequestDto.cs:5-9`, `UpdateCustomerContactRequestDto.cs:5-12`);
§15 asks whether `onboardedOn` is meant to be immutable.

**F. Three dates, two wire formats.** `onboardedOn` is a `DateOnly` — render it as a plain date, never
through `new Date()` (§10.2). `createdAt`/`updatedAt` are `DateTimeOffset` — they carry an offset and
parse directly, and still go through `shared/format/dates.ts`, because that is where a timezone bug
gets fixed once instead of in six screens.

### What this step does NOT do, and why

- **No `/customers/:customerId/edit` route.** Both edit forms are dialogs on this screen
  (`CustomersScreens.md` §2), because each touches four to seven fields of a record already on screen.
- **No *Delete*, *Archive*, *Remove* or *Merge*.** `02-AuthorizationMatrix.md` §3:
  *"Delete a Customer — **Nobody.** Customers are never deleted."*
- **No Employee or Customer Admin count.** `CustomerDto` carries neither (§15).
- **No suspension reason.** It is written to the audit log only (§9), and only an `AccountantAdmin` may
  read that log.

---

## 7. Step 7 — `OnboardCustomerScreen.tsx`

**File:** `frontend/src/slices/customers/screens/OnboardCustomerScreen.tsx`

Two headed blocks on **one** page, one submit, one request. `AccountantAdmin` only. Implement
`CustomersScreens.md` §5 in full: §5.4's field layout and copy rule, §5.5's limits, §5.6's two
conflicts.

**A. One `POST /api/customers/onboard`, and it is imported from `slices/employees/api.ts`** (§0.2).
The body is **nested** — `{ customer: {…}, firstAdmin: {…} }` (`EmployeeWriteDtos.cs:139-166`). A
flattened body binds both objects to their defaults and returns
`422 "Legal name is required."` for a form that plainly had one.

**B. A visual stepper is fine; a stepper whose *Next* issues a request is banned.** Splitting the
submit across `/api/customers/create` and then `/api/employees/register` + `/invite` puts a network
boundary inside an operation `03-SliceInventory.md` §1 makes atomic on purpose. A `422` on the work
email, a `409` on the login address, or a closed laptop then leaves a Customer with no Employee and no
account: nobody can sign in, and no screen can finish the job, because there is no resume-onboarding
endpoint (`OnboardCustomerHandler.cs:24-27`; `CustomersScreens.md` §5.2).

**C. Validate both blocks in one `zodResolver` pass.** `OnboardCustomerHandler.cs:61-68` validates
`firstAdmin` **before** delegating the Customer half at `:74`, so a request wrong in both places
returns only the first-admin `422`. A client that validated sequentially reproduces that: fix the work
email, submit, receive a second `422` about the legal name.

**D. No role select, and no "copy invitation link".** The role is `CustomerAdmin`, chosen by the
handler (`OnboardCustomerHandler.cs:104-114`), and the response is three ids with no token
(`:153-160`) because the invitation is emailed and reaches the SPA nowhere.

**E. Invalidate `['customers','list']`, then `navigate('/customers/' + customerId)`.** This is the one
mutation in the slice that **cannot seed** the detail key: the response is three ids, not a `Customer`.
The detail screen fetches for itself. Do not assemble a `Customer` from the form values — the server
normalised them (§3.2 rule D).

**F. Both `409`s render `title` verbatim, and neither attaches to a field.**
`"A customer with this tax number already exists."` means the **Customer** already exists — suggest
searching `/customers` for it. `"That email address is already in use."` means the **login email** is
taken, possibly at another Customer, and `OnboardCustomerHandler.cs:116-121` rewrites Identity's `409`
precisely so the response reveals nothing about which. **Do not "improve" it by naming the Customer;**
the client does not know it and must not. In both cases nothing was written.

### 7.1 Five ways this step goes wrong

1. **A flattened request body.** §7 rule A. A `422` about a field the user filled in.
2. **A two-call wizard.** §7 rule B. It leaves an unusable Customer behind on any failure between the
   calls.
3. **A role selector on the first admin.** Creating the first person as a plain `Employee` puts the
   Customer in violation of its own at-least-one-active-Customer-Admin invariant from the moment it
   exists, and the set-role guard then blocks every attempt to climb out
   (`OnboardCustomerHandler.cs:110-113`).
4. **Person-shaped labels in the upper block.** §12.1. The upper block is a company.
5. **Gating *Add Customer* on `CreateCustomer`.** §11.2.

---

## 8. Step 8 — the two edit dialogs

**File:** `frontend/src/slices/customers/components/EditCustomerLegalDialog.tsx`
**File:** `frontend/src/slices/customers/components/EditCustomerContactDialog.tsx`

Two dialogs, two Zod schemas, two mutations, no shared "edit customer" form. Mount both into §6.

### 8.1 The split is a permission boundary and it is exact

`CustomersActionCatalogue.cs:17` grants `EditCustomerLegal` to **AA and AU only**; `:18-19` grants
`EditCustomerContact` to **AA, AU and CA**. That is `02-AuthorizationMatrix.md` §3's distinction
between routine work and changing who the company legally is. Merging the dialogs produces a form a
`CustomerAdmin` can open and never submit, because `update-legal` `403`s for them
(`UpdateCustomerLegalHandler.cs:39`).

| Legal dialog — `UpdateCustomerLegalRequestDto.cs:5-9` | Contact dialog — `UpdateCustomerContactRequestDto.cs:5-12` | Neither: read-only |
|---|---|---|
| `customerId`, `legalName`, `tradingName`, `taxNumber`, `taxOffice` | `customerId`, `addressLine1`, `addressLine2`, `addressCity`, `addressPostalCode`, `addressCountry`, `contactEmail`, `contactPhone` | `status`, `onboardedOn`, `createdAt`, `updatedAt` |

**A. Both endpoints are full replacements, not patches.** `UpdateCustomerContactHandler.cs:47-53` and
`UpdateCustomerLegalHandler.cs:53-56` assign every field unconditionally. So a field in the wrong
dialog is either **silently reverted** — it is absent from the DTO that dialog posts — or a `403`. And
a form pre-filled from a stale read reverts whatever changed in between, with no concurrency token
anywhere in the built backend to detect it (§9.4; punch-list item 7 is the ticket-type case of the same
gap). Always open the dialog from the freshest detail the cache holds, and never keep a dialog's
initial values across a route change.

**B. Send all eight or all five keys, always**, including the unchanged ones. There is no partial
semantic to reach for.

**C. `null`, not `''`, for a cleared optional** (§9.3 rule F). `CustomerValidation.cs:74-82` maps an
empty optional to `null`; `''` is a value that can pass a nullability check and fail a length one.

**D. The status codes arrive in a fixed order and only one of them highlights nothing.** Both handlers
run `RequireAsync` → scope read → validate → (legal only) duplicate check, so a caller can see `403`,
then `404`, then `422`, then `409`, in that order. Render `422` and `409` as a form-level banner above
the submit button with every typed value intact (§7.3), and add a *Reload* affordance on the `409`.
Never map either onto a field: the body has no field reference to map from.

**E. `403` does not render the server's `title`.** `PermissionChecker.cs:63` writes
`"Permission denied for action 'EditCustomerLegal'."` — an internal action string. §7.1 fixes the copy
for a `403` without `detail` at *"You do not have permission to do that."* The verbatim-title rule
(§2.3 rule F) governs `400`, `409` and `422`, where the server's wording was written for the user.

**F. The contact dialog is used by two screens.** §6 and §10 mount the same component; it takes the
`customerId` and the seven current values as props and knows nothing about which screen it is on.

---

## 9. Step 9 — the suspend and reactivate dialogs

**File:** `frontend/src/slices/customers/components/SuspendCustomerDialog.tsx`
**File:** `frontend/src/slices/customers/components/ReactivateCustomerDialog.tsx`

`ConfirmDialog` is mandatory for both (§8.3), and the suspend dialog must **name the consequence**,
because from the operator's seat it looks like a chip changing colour and is in fact a lockout of
everybody at that company.

**A. Write all four sentences from `CustomersScreens.md` §4.5 verbatim in substance.** Each is a fact
read out of the code, and the fourth is the one nobody guesses:

1. Every Customer Admin and Employee at this Customer will be unable to sign in from their next
   attempt — `LoginHandler` calls `ICustomerApi.IsActiveAsync` live on every login for the two
   Customer-side roles (`ICustomerApi.cs:44-50`; `02-AuthorizationMatrix.md` §11).
2. Accountants are unaffected — their `CustomerId` is null and the check is skipped.
3. Anyone already signed in keeps working until their session expires, up to 8 hours. Nothing
   re-checks Customer status on cookie replay, and `SuspendCustomerHandler.cs:53` changes one row in
   `customers` and touches no `UserAccount`. **Suspension is not a session revocation and the dialog
   must not imply it is.**
4. Reactivating later does **not** restore individually suspended accounts.
   `02-AuthorizationMatrix.md` §11 calls this *"correct and will look like a bug"*.

**B. *Reason* is optional, `≤500`, and goes to the audit log only.** `CustomerValidation.cs:32`
normalises it; `SuspendCustomerHandler.cs:56-67` writes it into the `After` payload of the
`CustomerSuspended` entry. It is not on the Customer row, not in `CustomerDto`, and not visible on any
screen. Label it **"Reason (recorded in the audit log)"** — a label implying the Customer will see it
is false, and one implying it appears on this screen is worse, because the operator will look for it.

**C. `422 "This customer is already suspended."` is reachable from a stale tab**
(`SuspendCustomerHandler.cs:49-50`). Render it verbatim in the dialog banner, **keep the dialog open**,
and invalidate the detail query so the chip corrects itself. Same for
`"This customer is already active."` (`ReactivateCustomerHandler.cs:47-48`).

**D. The reactivate success copy promises nobody a login.** "Customer reactivated" and nothing more —
rule A item 4 is why. A `Snackbar` on success; successes are the only toasts in the app (§5.3).

**E. Do not offer a reason field on reactivate and a required one on suspend, or vice versa.** The DTO
is the same for both (`SetCustomerStatusRequestDto.cs:5-6`) and the field is optional in both.

---

## 10. Step 10 — `OwnCustomerScreen.tsx`

**File:** `frontend/src/slices/customers/screens/OwnCustomerScreen.tsx`

`useOwnCustomer()` — no argument — then one card: legal name and chip in the heading, trading name,
address, email, phone, and an *Edit contact* button for a `CustomerAdmin` only. It is built last
because it mounts §8's contact dialog.

**A. `Employee` gets a read-only card with no buttons.** That is the whole screen for them and it is
correct; do not invent filler (`CustomersScreens.md` §6.3). `can(role,'EditCustomerContact')` is `false` for
`Employee` (`CustomersActionCatalogue.cs:18-19`).

**B. Neither role has any path — button, link or URL — to legal name, trading name, tax number or tax
office.** Three of those four are not even in the response (`CustomerSelfDto.cs:5-16`;
`CustomerMapper.cs:38-51`), which is what `02-AuthorizationMatrix.md`:311 demands: *"absent from the
API response, not merely unrendered."* There is nothing here for the UI to hide.

**C. A `CustomerAdmin` can nevertheless read the wide DTO, and that is not a leak.** `ViewCustomer`
includes `CustomerAdmin` (`CustomersActionCatalogue.cs:20-21`) and `GetCustomerHandler.cs:27` applies
the scope filter, so `GET /api/customers/detail?customerId=<their own>` returns the full `Customer`,
tax number included. `CustomerSelfDto`'s narrowing is about the **`Employee`** role, not the
Customer side generally. The UI still routes CA here, because `/customers/:customerId` is
`RequireRole`d to the two Accountant roles and no Customer-side screen links to it.

**D. Render the chip, even though it will normally read `Active`.** A suspended Customer's people
cannot sign in at all, so the only reader who ever sees `"Suspended"` here is inside the up-to-8-hour
window of a session predating the suspension — which is exactly when a user needs the explanation.
Whether it should carry a banner too is §15.

**E. No `401` special case.** §0.4, first blockquote: the reachable denial for an Accountant is a
`403`, both are unreachable through the router, and §2.3 rule H applies unchanged.

---

## 11. Step 11 — route wiring and the `can.ts` verification

### 11.1 `routes.tsx` — four rows, and no fifth

**File:** `frontend/src/routes.tsx` (Phase 0's; this plan adds rows and nothing else)

| Path | Screen | `RequireRole` |
|---|---|---|
| `/customers` | `CustomerListScreen` | AA, AU |
| `/customers/new` | `OnboardCustomerScreen` | **AA** |
| `/customers/:customerId` | `CustomerDetailScreen` | AA, AU |
| `/my-customer` | `OwnCustomerScreen` | CA, EMP |

Exactly the rows in `GeneralUIArchitecture.md` §4.1, which is normative and to which this plan adds
nothing. **No `/customers/:customerId/edit`** (§6). `RequireRole` renders `AccessDeniedPage`; it does
not redirect (§4.3 rule A).

`react-router-dom` ranks static `/customers/new` above `/customers/:customerId` regardless of
declaration order, so no ordering workaround is needed. If `customerId === 'new'` ever reaches the
detail query, the route table is wrong — fix the table, do not add a guard, which hides the fault
behind a `?customerId=new` `400` (`CustomersScreens.md` §2).

`RequireRole` is **not a security boundary** (§4.3 rule B). The server denies the underlying calls and
audits every denial regardless of what the router did.

### 11.2 Verify the nine action names before you gate anything

Phase 0 wrote `can.ts`. This plan **verifies** the rows it depends on and creates none. Diff them
against the source, by hand, in this order:

- [ ] The eight Customers rows against `CustomersActionCatalogue.cs:13-22` — same names, same role
      sets, no extras on either side: `CreateCustomer` AA; `SuspendCustomer` AA; `ReactivateCustomer`
      AA; `ListCustomers` AA+AU; `EditCustomerLegal` AA+AU; `EditCustomerContact` AA+AU+CA;
      `ViewCustomer` AA+AU+CA; `ViewOwnCustomer` CA+EMP.
- [ ] `OnboardCustomer` against **`EmployeesActionCatalogue.cs:22`** — AA only. It is not in the
      Customers catalogue, because the endpoint is not in the Customers endpoint file.

**A. Gate *Add Customer* on `OnboardCustomer`, not `CreateCustomer`.** Both are `[AccountantAdmin]`
today, so the wrong one gives the right answer — and becomes a lie the moment either changes
independently. Gate on the action the endpoint the button actually calls checks
(`OnboardCustomerHandler.cs:59`).

**B. A missing row denies.** `can()` returns `false` for an unknown action (§6.1), matching the
server's fail-closed checker. So a typo hides a button the user is entitled to: annoying, and much
safer than the reverse.

**C. `can()` expresses *who may call*, never *which rows*.** `ViewCustomer` is `true` for a
`CustomerAdmin` and answers nothing about *this* Customer; row-level scoping is
`CustomerScope.cs:37-41`'s and surfaces as a `404` (§6.2 rule D).

---

## 12. Copy and vocabulary — binding, not stylistic

### 12.1 The word "Client" is banned, and a Customer is never a person

`00-Glossary.md` is normative *"in code, in identifiers, and in UI copy"*. In this slice:

**A. Never write "Client", "Clients", "Firm", or "Admin" unqualified — anywhere.** Titles, nav,
buttons, dialogs, snackbars, empty states, `aria-label`s, the browser tab title. Write **Customer**,
**Office**, **Customer Admin**, **Accountant Admin**. "Client" is reserved for the React app in an
HTTP sense and appears in this slice only inside a comment about HTTP, if at all.

**B. A Customer is always a company, never a natural person.** No "First name", no "Last name", no
"Date of birth", and no person-shaped placeholder text in any Customer field — not
`placeholder="e.g. John Papadopoulos"` on a contact email, not "their address" for the company
address. The fields are `legalName` and `tradingName`.

**C. The one natural person in this slice is the first Customer Admin on `/customers/new`**, and
"Given name" / "Family name" are correct **there and only there**, because those fields belong to
`OnboardFirstAdminDto` (`EmployeeWriteDtos.cs:156-166`) — an **Employee**, a different entity in a
different slice. The section heading *First Customer Admin* is what stops a reader carrying
person-shaped labels upward into the Company block. Never write "Admin" unqualified in it.

**D. `placeholder` is never a label** (§8.4 item 1). MUI's `TextField label=` renders a real
`<label>`; a placeholder disappears on focus and is invisible to a screen reader.

### 12.2 Three status vocabularies, one `StatusChip`

`StatusChip` is Phase 0's and its colour map is shared across every vocabulary on purpose — one colour
per word, so `Suspended` is never green on one screen and red on another. **Sharing the map does not
mean every word is valid for every entity.**

| Entity | Vocabulary | Source |
|---|---|---|
| `Customer` | `Active` \| `Suspended` | `Customers/Core/Customer.cs:27-31`, plus a `CHECK` constraint |
| `UserAccount` (`accountStatus`) | `Invited` \| `Active` \| `Suspended` | Identity; the **person**, not the company |
| `Employee` | `Active` \| `Departed` | Employees |

So, in this slice:

**A. The status filter sends `Active` or `Suspended` as those exact strings, or omits the key**;
`ListCustomersHandler.cs:31-33` answers `422` to `""`, `"active"` and `"Invited"` alike (§2 rule D).

**B. `Invited` must be unreachable for a Customer.** A newly onboarded Customer is `Active` while its
first Customer Admin is `Invited` (`Slices/Customers/Core/Customer.cs:20` for the Customer's `Active`
default; `OnboardCustomerHandler.cs:104-114` for the invite call that mints the account) — the company and the person,
two rows, two vocabularies. Never render an `Invited` chip on a Customer screen.

**C. `Departed` belongs to an Employee and never appears here either.**

**D. `status` is a string; `role` is an integer, and `0` is falsy.** No `JsonStringEnumConverter` is
registered anywhere, so C# enums cross the wire as integers while these `string` properties cross as
strings (§10.1; punch-list item 4). `AccountantAdmin` is `0`, so `if (session.role)` is `false` for
the most privileged role in the system, and so is `role || fallback`. **Every role check in this slice
is `===` against a named constant from `shared/format/enums.ts`** — which in practice means every
`can()` call, since `can()` takes the role. Nothing in this slice sends or renders a role, so there is
no `ROLE_LABELS` call here; the trap is in the comparisons.

---

## 13. Security invariants for this slice

**A. Never filter Customers by scope in the browser.** `CustomerScope.cs:37-41` does it server-side on
every read here, on the **primary key** — the Customer *is* the tenant boundary.
`02-AuthorizationMatrix.md`:311 — *"Never rely on the React app to hide data … must be **absent from
the API response**, not merely unrendered."* So a `.filter(c => c.id === session.customerId)` is not a
safeguard: it is **evidence of a server-side leak being concealed**, and it breaks the pager because
`totalCount` counts the rows it discards.

**B. Out-of-scope rows return `404`, not `403`, and `404` is never rendered as "forbidden".** Every
route naming a `customerId` answers `404 "Customer not found."` for a row the caller may not see
(§0.3), because *"a `403` confirms the row exists"*. Never render "forbidden", "denied" or "no
permission" for a `404`. **"Not found." is the only honest wording and it is honest in both cases** —
and it is the only thing standing between a `CustomerAdmin` and the discovery that another Customer
exists.

**C. `can()` gates affordances only.** Never a query, never a field, never a row (§6.2 rule A).

**D. No token in `localStorage`, and nothing to store.** The session is the `aa_session` HttpOnly
cookie; JavaScript cannot read it. Every call goes through `http.ts` with
`credentials: 'same-origin'` — never `'omit'`, which drops the cookie, and never `'include'`, which
declares a cross-origin request in an application that has no CORS configuration and never will.

**E. No API base-URL environment variable, ever.** Every path in `api.ts` is a relative string
beginning `/api/`. No `VITE_`, no `import.meta.env`, no `http://` literal. **CORS is never
configured, in any environment.**

**F. Never put `customerId` in an API path, and never add one to `/api/customers/own`.** The SPA route
carries the id so the URL is bookmarkable; the API takes it as a query parameter or a body field
(§2.3 rule D). A parameter on `/own` is the first step to a client that thinks the id is its to choose.

**G. Never call `/api/customers/create` from the SPA** (§2 rule A).

---

## 14. Files checklist

Created by this plan, in this order:

- [ ] `frontend/src/slices/customers/types.ts` — §1
- [ ] `frontend/src/slices/customers/api.ts` — §2, seven functions, **no `createCustomer`**
- [ ] `frontend/src/slices/customers/queries.ts` — §3, seven hooks, invalidations stated per hook
- [ ] `frontend/src/slices/customers/schemas.ts` — §4, four schemas
- [ ] `frontend/src/slices/customers/screens/CustomerListScreen.tsx` — §5
- [ ] `frontend/src/slices/customers/screens/CustomerDetailScreen.tsx` — §6
- [ ] `frontend/src/slices/customers/screens/OnboardCustomerScreen.tsx` — §7
- [ ] `frontend/src/slices/customers/components/EditCustomerLegalDialog.tsx` — §8
- [ ] `frontend/src/slices/customers/components/EditCustomerContactDialog.tsx` — §8, used by §6 **and** §10
- [ ] `frontend/src/slices/customers/components/SuspendCustomerDialog.tsx` — §9, the four sentences
- [ ] `frontend/src/slices/customers/components/ReactivateCustomerDialog.tsx` — §9
- [ ] `frontend/src/slices/customers/screens/OwnCustomerScreen.tsx` — §10

Touched, not owned:

- [ ] `frontend/src/routes.tsx` — the four rows in §11.1 wired with `RequireRole`; no fifth row
- [ ] `frontend/src/slices/employees/api.ts` and `types.ts` — **only** if the Employees slice does not
      exist yet, and then only the `onboardCustomer` wrapper and its two types (§0.2 rule C)

Verified, not created:

- [ ] `frontend/src/shared/permissions/can.ts` — the nine rows in §11.2

Nothing under `frontend/src/shared/` is created, renamed or moved by this plan (§0.1).

---

## 15. Questions to flag if unclear

Flag these; do not answer them by inventing a behaviour. None of them changes a file in §14 — the
checklist is complete as printed.

> This list opened with *"does `../00-Foundation/IMPLEMENTATION_PLAN.md` exist yet?"* until
> 2026-09-02. It does, and it is this plan's Phase 0. The rule that question protected still holds
> and is not negotiable: **do not start §1 by building a private copy of `http.ts` or `StatusChip`.**

- [ ] **Is the Employees slice built before this one?** §0.2 rule C resolves the ordering either way,
      but the build order for the six slices is not fixed anywhere, and `/customers/new` cannot be
      exercised without `slices/employees/api.ts`.
- [ ] **Should a suspended Customer's `/my-customer` show a suspension banner** in the up-to-8-hour
      window (§10 rule D)? `Screens/EmployeesScreens.md` §11 asks the same from the other side; one
      answer should serve both.
- [ ] **Should an existing session survive its Customer's suspension?** It does today (§9 rule A item
      3). If not, `Identity` needs an `OnValidatePrincipal` check — a backend change, not a UI
      decision.
- [ ] **Is `onboardedOn` really immutable?** No endpoint changes it and a mis-typed date has no in-app
      correction. Add it to `UpdateCustomerLegalRequestDto`, or is a support-only `UPDATE` the intended
      answer?
- [ ] **Is tax-number uniqueness meant to be case- and whitespace-sensitive?**
      `UpdateCustomerLegalHandler.cs:48-49` compares with `==` after a `Trim()` only, unlike
      `ticket_types.code`, which is unique on `LOWER(code)`. So `el123456789` and `EL123456789` are two
      Customers today and the UI cannot fix it.
- [ ] **Is there any view of the suspension reason?** Audit-only (§9 rule B), and only an
      `AccountantAdmin` may read the audit log. An `AccountantUser` seeing a `Suspended` chip has no
      way to learn why.
- [ ] **Should the detail screen show an Employee or Customer Admin count?** `CustomerDto` carries
      neither, so the at-least-one-active-Customer-Admin invariant is invisible from every Customer
      screen. Adding a count is a backend change.
- [ ] **Should `/api/customers/own` declare the `403` it can actually return, and should
      `CustomersScreens.md` §6.4 rule A be corrected?** §0.4, first blockquote. No UI consequence, but
      the endpoint's `.Produces` list is wrong about its own denials.

---

## 16. Success criteria

Each is verified by running the app, not by reading the code. **Nothing in this plan has ever been
run:** there is no `frontend/` directory, and this machine has no local PostgreSQL, so no route, bound
or status code below has been observed in a response.

1. `/customers` and `/customers/:customerId` are reachable by both Accountant roles and neither
   Customer-side role; `/my-customer` is the exact reverse. **No role opens both.**
2. `api.ts` uses `post` for `list` and `get` for `detail` and `own`, neither has been "corrected" to
   the other verb, and `getOwnCustomer` takes no parameter.
3. `grep -rn "customers/create\|createCustomer" frontend/src` finds nothing.
4. `/customers/new` issues exactly **one** request on submit, and a deliberately failing submit leaves
   no Customer behind — verified by searching `/customers` afterwards.
5. `/customers/new` with both a bad work email and a blank legal name shows **both** errors before any
   request is sent; with `onboardedOn` two days ahead it is blocked client-side, not by a `422`.
6. An `AccountantUser` sees no *Add Customer* button and, at `/customers/:id`, both edit buttons but
   **no** *Actions* menu at all — in any state, in any menu.
7. A `CustomerAdmin` at `/my-customer` can edit contact details and has no path — button, link or
   URL — to legal name, trading name, tax number or tax office; an `Employee` there sees no buttons.
8. The `/api/customers/own` response carries no `taxNumber`, `taxOffice`, `onboardedOn`, `createdAt`
   or `updatedAt`, for both CA and EMP — checked in the network tab, not on the screen.
9. Editing one field and saving leaves every other field byte-identical in a subsequent
   `GET /api/customers/detail`.
10. The status filter offers exactly *Active* and *Suspended* plus an *All* option that sends no
    `status` key at all, and selecting any of the three returns rows rather than a `422`.
11. Searching a Customer's tax number returns nothing and the box's label already said so; a
    201-character term is blocked client-side; `pageSize: 999` renders a pager consistent with the
    `50` the server returned, with no rows missing.
12. `/customers/:id` for a non-existent GUID renders "Not found", never "forbidden".
13. The *Suspend* dialog states the login lockout, the Accountant exemption, the surviving session and
    the non-restoration of individual accounts, and its *Reason* label says the reason goes to the
    audit log — and the *Reactivate* success copy promises none of it.
14. Suspending an already-suspended Customer from a stale tab renders
    `"This customer is already suspended."` verbatim, the dialog stays open, and the chip then
    corrects itself.
15. After suspending: that Customer Admin cannot sign in, an `AccountantUser` still can, and a
    Customer-side session opened *before* the suspension still loads `/my-customer`.
16. *Edit legal* with another Customer's tax number renders
    `"A customer with this tax number already exists."` verbatim with every typed value intact, and
    no `traceId` is shown.
17. A `403` renders "You do not have permission to do that." — **not** the server's
    `"Permission denied for action '…'."` title.
18. Saving contact details from `/my-customer` and then reopening the screen shows the new values, and
    no request to `/api/customers/detail` was made from that screen.
19. `grep -rn "refetchInterval\|\.filter(" frontend/src/slices/customers` finds no polling and no
    client-side row hiding.
20. No screen renders an `Invited` chip for a Customer, a raw role integer, the word "Client", the
    word "Firm", or "Admin" unqualified; the only "Given name" and "Family name" in the slice are
    under the *First Customer Admin* heading of `/customers/new`.
21. The nine action names in §11.2 match `CustomersActionCatalogue.cs` and
    `EmployeesActionCatalogue.cs:22` exactly — same names, same role sets, no extras on either side.
