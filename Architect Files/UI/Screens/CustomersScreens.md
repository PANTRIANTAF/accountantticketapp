# Customer Screens

Four roles reach this slice through two disjoint doors. The two Accountant roles work a cross-Customer list at `/customers` and drill into `/customers/:customerId`; the two Customer-side roles have no list at all and see exactly one Customer — their own — at `/my-customer`, through a different endpoint returning a deliberately narrower DTO. No screen here is openable by both an `AccountantUser` and an `Employee`. That is why `/api/customers/detail` and `/api/customers/own` both exist and must not be collapsed into one call.

The second thing to absorb is that **the Customer *is* the tenant boundary** ([../../00-Glossary.md](../../00-Glossary.md), *Customer*), so this is the one slice where the row a caller may see and the row defining their scope are the same row — `CustomerScope.WhereMatchesCustomerScope` filters on the primary key, not a foreign key, and there is a separate `ICustomerRoot` interface for exactly that. The consequence for the UI is narrow and absolute: the client never filters Customers (§7 rule A).

The third is that **a Customer is always a company, never a natural person** ([../../README.md](../../README.md)). This slice has no "First name" field and no "Client" in any copy. The one natural person on these screens is the first Customer Admin on the onboarding form, and that person is an **Employee** — a different entity, in a different slice, with its own labels (§5.4).

**Documents that govern this one, in precedence order.** Where any of them disagrees with this document, **they win and this document is wrong** — fix this document, do not code around it.

- [../../README.md](../../README.md) — *Locked platform decisions*, *Conflict precedence*
- [../../00-Glossary.md](../../00-Glossary.md) — banned terms; binding in UI copy
- [../../01-DomainModel.md](../../01-DomainModel.md) §1–2 — the tenant boundary, and which entity owns `Invited`
- [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §1, §3, §11, §12 — normative
- [../../03-SliceInventory.md](../../03-SliceInventory.md) §1 — why onboarding lives in `Employees`
- [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) — the governing UI document; cited, never restated
- [EmployeesScreens.md](EmployeesScreens.md) §1 note 6 — the other half of the onboarding split

---

## 0. Role coverage

| README brief | Role | Covered by | Notes |
|---|---|---|---|
| "Accountant: customer list" | AA, AU | §3 | One screen, two affordance sets |
| "Accountant: create a Customer" | AA | §5 | Route `/customers/new`, endpoint `/api/customers/onboard` |
| "Accountant: suspend/reactivate" | AA | §4.5–4.6 | Both confirmed with `ConfirmDialog` |
| "Customer Admin: our company details" | CA | §6 | Contact editable, legal not |
| "Employee: our company details" | EMP | §6 | Same screen, read-only, no buttons |
| (not in the brief) Customer detail | AA, AU | §4 | Two separately gated edit forms |
| "Accountant: ticket queue" | AA, AU | **Not here.** Blocked on `Tickets` | §0.1 of the governing document |

---

## 1. Endpoints this slice consumes

AA = `AccountantAdmin`, AU = `AccountantUser`, CA = `CustomerAdmin`, EMP = `Employee`. Roles is *who may call*, from `CustomersActionCatalogue.cs` and `EmployeesActionCatalogue.cs`; every row naming a `customerId` is additionally scoped server-side.

| Route | Verb | Request DTO | Response DTO | Roles | Notes |
|---|---|---|---|---|---|
| `/api/customers/list` | POST | `ListCustomersRequestDto` | `PaginatedResponse<CustomerSummaryDto>` | AA, AU | A **POST read**. Note 1 |
| `/api/customers/detail` | GET | `?customerId=<guid>` | `CustomerDto` | AA, AU, CA | Query param. Note 2 |
| `/api/customers/own` | GET | none | `CustomerSelfDto` | CA, EMP | **No parameter at all.** Note 3 |
| `/api/customers/update-contact` | POST | `UpdateCustomerContactRequestDto` | `CustomerDto` | AA, AU, CA | Full replacement. Note 5 |
| `/api/customers/update-legal` | POST | `UpdateCustomerLegalRequestDto` | `CustomerDto` | AA, AU | Full replacement; `409` on tax number |
| `/api/customers/suspend` | POST | `SetCustomerStatusRequestDto` | `CustomerDto` | **AA only** | §4.5 |
| `/api/customers/reactivate` | POST | `SetCustomerStatusRequestDto` | `CustomerDto` | **AA only** | §4.6 |
| `/api/customers/onboard` | POST | `OnboardCustomerRequestDto` | `OnboardCustomerResponseDto` | **AA only** | Registered in `EmployeesEndpoints.cs`. §5 |
| `/api/customers/create` | POST | `CreateCustomerRequestDto` | `CustomerDto` (**201**) | **AA only** | **No screen calls this.** Note 4 |

1. **`/api/customers/list` is a `POST` read. Do not "fix" it to `GET`** — [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §2.3 rule C names it. `GET` matches no route and returns `405`, surfacing as an unexplained banner on the Accountant's landing screen. `pageNumber` and `pageSize` are body fields here, not query parameters.
2. **`detail` is a `GET` with `?customerId=`**, the one departure from §2.3 rule D in this slice. Build it with `URLSearchParams` in `api.ts`; a concatenated undefined id sends `customerId=undefined` and returns a `400` about parameter binding that reads nothing like "the id was missing".
3. **`own` takes no argument** — it reads `CurrentUser.CustomerId` from the session cookie. Never add a `customerId` to it: the id is not the client's to choose, and a parameter is the first step to a client that thinks it is.
4. **Nothing in the SPA calls `/api/customers/create`.** It creates a Customer with no Employee and no account — [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §3 calls that *"useless"* (*"a Customer with no way to log in"*) and it violates the at-least-one-active-Customer-Admin invariant from the moment it exists. It is a building block for `ICustomerApi.CreateAsync` and nothing else. Do not even write a wrapper: an unused wrapper is an invitation to the two-step form §5.2 forbids.
5. **Both update endpoints are full replacements**, not patches — all seven contact fields or all four legal fields, unconditionally. Submitting a form pre-filled from a stale read silently reverts whatever changed in between, and there is no concurrency token to detect it (§9.4).
6. Every route can return `403`; every route naming a `customerId` can return `404`, which is the scoping mechanism and not a bug (§2.4 item 5; §7 rule B here).

---

## 2. Routes and screens

From [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §4.1, which is normative. This table adds no route to it.

| SPA path | Screen component | Roles |
|---|---|---|
| `/customers` | `CustomerListScreen` | AA, AU |
| `/customers/new` | `OnboardCustomerScreen` | **AA** |
| `/customers/:customerId` | `CustomerDetailScreen` | AA, AU |
| `/my-customer` | `OwnCustomerScreen` | CA, EMP |

`RequireRole` wraps each with exactly those roles and renders `AccessDeniedPage`, not a redirect (§4.3 rule A). There is deliberately **no** `/customers/:customerId/edit`: both edit forms are dialogs on the detail screen (§4.3), because each touches four to seven fields of a record already on screen.

`react-router-dom` ranks static `/customers/new` above `/customers/:customerId` regardless of declaration order, so no ordering workaround is needed. If `customerId === 'new'` ever reaches the detail query the route table is wrong — fix the table, do not add a guard, which hides the fault behind a `?customerId=new` `400`.

**File:** `frontend/src/slices/customers/api.ts` — eight functions, one per row of §1 except `create`.
**File:** `frontend/src/slices/customers/types.ts` — `CustomerSummary`, `Customer`, `CustomerSelf` and the request types, each commented with the C# file it mirrors (§2.5).

`onboardCustomer` lives in `frontend/src/slices/employees/api.ts`, because `api.ts` mirrors the endpoint file and `/api/customers/onboard` is registered from `EmployeesEndpoints.cs`. Import it under §1.4 rule C — `api.ts` and `types.ts` only. See [EmployeesScreens.md](EmployeesScreens.md) §1 note 6 and **do not duplicate the wrapper**, or the two field lists drift the first time a Customer field is added.

---

## 3. Screen: Customer list (`/customers`)

**File:** `frontend/src/slices/customers/screens/CustomerListScreen.tsx`

### 3.1 Layout

```
  Customers                                     [ Add Customer ]*
  ─────────────────────────────────────────────────────────────────
  [ Search legal or trading name ] [ Status ▾ ]  * AccountantAdmin
  ┌─────────────────────────┬──────────────┬─────────────┐
  │ Legal name              │ Trading name │ Status      │
  │ Acme Manufacturing S.A. │ Acme         │ [Active]    │
  │ Beta Holdings Ltd       │ —            │ [Suspended] │
  └─────────────────────────┴──────────────┴─────────────┘
                          Rows per page: 15 ▾  1-2 of 2  <>
```

Three columns, because `CustomerSummaryDto` has exactly three renderable fields plus `id`. **Add no column** for contact email, city, employee count, ticket count or onboarded date: none is in the summary DTO, and resolving one per row is fifteen extra requests per page. Sorted by `legalName` then `id`, server-side; `ListCustomersRequestDto` has no sort parameter, so offering column sort would reorder the fifteen rows on screen out of a hundred and look like corrupt data.

### 3.2 Data and query keys

**File:** `frontend/src/slices/customers/queries.ts`

| Query / mutation | Key or invalidation |
|---|---|
| The list | `['customers', 'list', { status, search, pageNumber, pageSize }]` |
| `updateCustomerContact`, `updateCustomerLegal`, `suspendCustomer`, `reactivateCustomer` | `setQueryData(['customers','detail', updated.id], updated)`; invalidate `['customers','list']` |
| `onboardCustomer` | invalidate `['customers','list']`; **cannot seed** — §5.3 |

**A. Every filter appears in the key** (§3.1). Omit `search` and two different searches share one cache entry, so the table shows the previous query's rows under the new query's pager.

**B. All four mutations return the full `CustomerDto`, so all four seed** (§3.2 rule D). Refetching instead discards a response already in hand and opens a window where the screen shows the old status after a successful suspend.

**C. `usePaginatedQuery` only** (§3.2 rule G). `PaginatedQuery.Normalize` clamps `pageSize` to 50 rather than rejecting it, so render the pager from `response.pageSize` (§2.4 item 6).

### 3.3 Affordances by role

| Affordance | AA | AU | Gate |
|---|:--:|:--:|---|
| The screen at all | yes | yes | `RequireRole`; CA and EMP get `AccessDeniedPage` |
| *Add Customer* | yes | **no** | `can(role, 'OnboardCustomer')` — **not** `CreateCustomer` |
| Search, status filter | yes | yes | none |
| Row click → detail | yes | yes | always |
| Suspend / reactivate | **no** | **no** | Detail screen only — see below |

Gate the button on `OnboardCustomer`, the action the endpoint the screen actually calls checks. Gating it on `CreateCustomer` gives the same answer today — both are `[AccountantAdmin]` — and becomes a lie the moment either changes independently.

Status changes are **not** row actions. A row menu offering *Suspend* two pixels from *Open* is how an entire company's staff loses its logins by mis-click, and a list row does not carry the context §4.5's dialog copy needs.

### 3.4 States

| State | Render |
|---|---|
| First load | `Skeleton` rows; header, filters and pager stay put (§7.4) |
| Refetch with data | Keep the rows, subtle progress indicator (§7.4) |
| `totalCount === 0`, no filters | `EmptyState` "No customers yet" plus *Add Customer* for AA; the sentence alone for AU |
| `totalCount === 0`, filters set | `EmptyState` "No customers match these filters" plus *Clear filters* |
| `items: []`, `totalCount > 0` | Over-run page — `EmptyState` with *Back to the first page* (§3.3 item 2) |
| Query failed | `ErrorBanner` replacing the table body (§7.2) |

### 3.5 Rules

**A. The status filter's values are exactly `"Active"` and `"Suspended"`.** `ListCustomersHandler` returns `422 "Unknown customer status."` for anything else. Send `null` or omit the key for "both", never `""` (§9.3 rule F).

> **`Invited` is not a Customer status, and the governing document says it is.** [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §10.1 lists `Customer.status` as `"Invited" | "Active" | "Suspended"`. The code has two: `Customers.Core.CustomerStatus` declares `Active` and `Suspended` only, `CreateCustomerHandler` and `CustomerApi.CreateAsync` both insert `Active`, and migration `20260901_002_AddCustomerStatusCheck.sql` adds `CHECK (status IN ('Active','Suspended'))`. `Invited` is a **UserAccount** status ([../../01-DomainModel.md](../../01-DomainModel.md) §2) — which is why a newly onboarded Customer is `Active` while its first Customer Admin is `Invited`. Never offer `Invited` in this filter (`422`) and never render an `Invited` chip for a Customer. Added to §9.

**B. `status` is a string; `role` is an integer** (§10.1). `StatusChip` takes the string through unchanged. Nothing in this slice sends or renders a role at all — the `CustomerAdmin` role the onboarding handler assigns never crosses the wire (§5.1).

**C. Debounce `search` by 300ms and cap it at 200 characters** — the handler returns `422 "Search must be at most 200 characters."`, and every keystroke is a new query key, so an undebounced box is one POST per character.

**D. Say what the search searches.** `ILIKE` over `legalName` **or** `tradingName` and nothing else — not tax number, not city, not contact email. Label the box "Search legal or trading name". A box labelled "Search" that silently ignores a pasted tax number reads as missing data. `%` and `_` are escaped server-side, so do not strip them client-side.

**E. Reset `pageNumber` to 1 whenever a filter changes**, or a narrowed filter leaves the pager on page 4 of a 1-page result and the user gets the over-run empty state instead of their rows.

---

## 4. Screen: Customer detail (`/customers/:customerId`)

**File:** `frontend/src/slices/customers/screens/CustomerDetailScreen.tsx`

### 4.1 Layout

```
  Acme Manufacturing S.A.  [Active]            [ Actions ▾ ]*
  ────────────────────────────────────────────────────────────
  ┌── Legal ─────────────────┐  ┌── Contact ────────────────┐
  │ Legal name   Acme Manu…  │  │ Address 12 Mill Road      │
  │ Trading name Acme        │  │         Athens 10431      │
  │ Tax number   EL123456789 │  │         Greece            │
  │ Tax office   A' Athinon  │  │ Email   ops@acme.example  │
  │            [ Edit legal ]│  │ Phone   +30 210 0000000   │
  └──────────────────────────┘  │       [ Edit contact ]    │
  ┌── Record ────────────────┐  └───────────────────────────┘
  │ Onboarded on 2026-03-14  │
  │ Created      14 Mar 2026 │   [ View employees → ]
  │ Updated      01 Sep 2026 │   * Suspend/Reactivate: AA only
  └──────────────────────────┘
```

### 4.2 Data and query keys

One query, `['customers', 'detail', customerId]`, from `GET /api/customers/detail?customerId=`. Use `enabled` only while `customerId` is undefined mid-navigation — never to express "not allowed" (§3.2 rule B).

### 4.3 Two separately gated edit forms, with no overlap

`CustomersActionCatalogue.cs` grants `EditCustomerContact` to **AA, AU and CA** and `EditCustomerLegal` to **AA and AU only**. That is not a rounding of one permission; it is [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §3's distinction between routine work and changing who the company legally is. So: two dialogs, two Zod schemas, two mutations, and no shared "edit customer" form. Merging them produces a form a `CustomerAdmin` can open and never submit, because `update-legal` `403`s for them.

The split is exact. A field in the wrong dialog is either silently reverted — it is absent from the DTO that dialog posts, and both endpoints are full replacements — or a `403`.

| `UpdateCustomerLegalRequestDto` | `UpdateCustomerContactRequestDto` | Read-only, no endpoint |
|---|---|---|
| `customerId`, `legalName`, `tradingName`, `taxNumber`, `taxOffice` | `customerId`, `addressLine1`, `addressLine2`, `addressCity`, `addressPostalCode`, `addressCountry`, `contactEmail`, `contactPhone` | `status` (§4.5–4.6 only), `onboardedOn`, `createdAt`, `updatedAt` |

`onboardedOn` is settable at creation and by nothing afterwards. Render it; never put it in an input.

**File:** `frontend/src/slices/customers/components/EditCustomerLegalDialog.tsx`
**File:** `frontend/src/slices/customers/components/EditCustomerContactDialog.tsx`

Both follow §9.3: `mode: 'onBlur'`, submit disabled only while pending, server errors in a form-level `ErrorBanner` above the submit button, input surviving failure, values trimmed, `null` and not `''` for an untouched optional field.

### 4.4 Affordances by role

| Affordance | AA | AU | Gate |
|---|:--:|:--:|---|
| The screen at all | yes | yes | `RequireRole` |
| *Edit contact* | yes | yes | `can(role, 'EditCustomerContact')` |
| *Edit legal* | yes | **yes** | `can(role, 'EditCustomerLegal')` |
| *Actions → Suspend* | yes | **no** | `can(role, 'SuspendCustomer')` **and** `status === 'Active'` |
| *Actions → Reactivate* | yes | **no** | `can(role, 'ReactivateCustomer')` **and** `status === 'Suspended'` |
| *View employees →* | yes | yes | always; a `<Link>`, not an import |

*View employees* navigates to `/employees` with the Customer filter preset from `customerId` ([EmployeesScreens.md](EmployeesScreens.md) §4.3). A cross-slice link is routing, not a dependency — do not import an Employees screen or hook here (§1.4 rule C).

The whole *Actions* menu is **absent** for an `AccountantUser`, not rendered disabled: a menu whose only two items can never be enabled is noise (§6.2 rule C).

### 4.5 *Suspend* — `AccountantAdmin` only, and the dialog names the consequence

`ConfirmDialog` is mandatory (§8.3), and it must state what suspension does, because from the operator's seat it looks like a chip changing colour and is in fact a lockout of everybody at that company. Four sentences, each a fact read out of the code:

1. **Every Customer Admin and Employee at this Customer will be unable to sign in, from their next attempt.** `LoginHandler` calls `ICustomerApi.IsActiveAsync` live on every login for the two Customer-side roles and refuses with the same generic `401` it gives a wrong password ([../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §11).
2. **Accountants are unaffected** — their `CustomerId` is null and the check is skipped.
3. **Anyone already signed in keeps working until their session expires**, up to 8 hours. Nothing re-checks Customer status on cookie replay, and `SuspendCustomerHandler` changes exactly one row in `customers` and touches no `UserAccount`. Suspension is not a session revocation; the dialog must not imply it is.
4. **Reactivating later does not restore individually suspended accounts.** [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §11 calls this *"correct and will look like a bug"* — those accounts have their own status, owned by `Identity`.

*Reason* is optional, `≤500` chars (`CustomerValidation.NormalizeReason`), and goes **to the audit log only**, into the `After` payload of the `CustomerSuspended` entry. It is not on the Customer row, not in `CustomerDto`, and not visible on any screen. Label it "Reason (recorded in the audit log)" — a label implying the Customer will see it is false, and one implying it appears here is worse, because the operator will look for it.

`422 "This customer is already suspended."` is reachable from a stale tab. Render it verbatim in the dialog banner, keep the dialog open, and invalidate the detail query so the chip corrects itself (§7.3).

### 4.6 *Reactivate* — the mirror, with one honest omission

Same AA-only gate, same `ConfirmDialog` requirement, same `SetCustomerStatusRequestDto` `{ customerId, reason? }`, same `CustomerDto` response, `422 "This customer is already active."` when it has not moved.

The success copy must **not** promise anybody can now sign in, for the reason in §4.5 item 4. "Customer reactivated" and nothing more: a Customer Admin whose own account is `Suspended` still cannot.

### 4.7 States

`LoadingRegion` while loading; `NotFoundPage` on `404` — **never** "forbidden" (§7 rule B); `AccessDeniedPage` on `403`; a form banner with a reload affordance on `409` from *Edit legal*; a verbatim form banner with input intact on any `422`; a `Snackbar` on success, the only toasts in the app (§5.3, §7.1–7.2).

---

## 5. Screen: Onboard Customer (`/customers/new`)

**File:** `frontend/src/slices/customers/screens/OnboardCustomerScreen.tsx`

### 5.1 One endpoint, one transaction, one submit

`POST /api/customers/onboard` creates the Customer, its first Employee, and that Employee's `CustomerAdmin` invitation **in one transaction across three slices**, `AccountantAdmin` only. `OnboardCustomerHandler`: *"A failure at ANY step must leave nothing behind."*

The response is `{ customerId, employeeId, userAccountId }` and **no token** — the invitation link is emailed to the invitee and reaches the SPA nowhere, so build no "copy invitation link" affordance. The role is `CustomerAdmin`, chosen by the handler and **not** a request field: add no role select. Creating the first person as a plain `Employee` would put the Customer in violation of its own at-least-one-active-Customer-Admin invariant from the moment it exists, and the set-role guard would then block every attempt to climb out.

### 5.2 Why this is one form and not a wizard that calls two endpoints

The instinct here is *step 1: create the Customer, step 2: add the first Customer Admin*, because that is how the two halves read. It is banned. Splitting the submit across `/api/customers/create` and then `/api/employees/register` + `/api/employees/invite` puts a network boundary inside an operation [../../03-SliceInventory.md](../../03-SliceInventory.md) §1 makes atomic on purpose — *"Customer onboarding is one operation, and it lives in Employees"*. A `422` on the work email, a `409` on the login address, or a closed laptop then leaves a Customer row with no Employee and no account: nobody can sign in, and no screen can finish the job, because there is no "resume onboarding" endpoint. `EmployeesEndpoints.cs` says *"splitting it into two calls would let a Customer exist with nobody able to log into it"*, and `CustomerApi.CreateAsync` deliberately enlists in the caller's transaction rather than opening one — so the atomicity is a property of calling `/onboard` and of nothing else.

A **visual** stepper that collects both blocks and submits **once** is fine. A stepper whose *Next* button issues a request is the banned design. There are twenty fields; two headed sections on one page is enough.

### 5.3 Data

`onboardCustomer` → invalidate `['customers','list']`, then `navigate('/customers/' + customerId)`.

**It cannot seed the detail key**: the response is three ids, not a `CustomerDto`, so this is the one mutation in the slice that genuinely needs a second round trip, and the detail screen fetches for itself. Do not assemble a `Customer` from the form values — the server trimmed and normalised them (`CustomerValidation`), so the cache entry would differ from the database, and §3.2 rule E bans optimistic writes outright.

### 5.4 The form — two blocks, and only one is about a company

```
  Add Customer
  ──────────────────────────────────────────────────────
  Company                                (the Customer)
    Legal name*     [      ]  Trading name  [      ]
    Tax number*     [      ]  Tax office    [      ]
    Address line 1* [      ]  Address line 2[      ]
    City*  [   ]  Postal code* [  ]  Country* [   ]
    Contact email*  [      ]  Contact phone*[      ]
    Onboarded on*   [ 2026-09-02 ]

  First Customer Admin           (an Employee — a person)
    Given name*     [      ]  Family name*  [      ]
    Job title       [      ]
    Work email*     [      ]  Contact phone [      ]
    Tax identification [   ]  Social security [  ]
    Employment start* [ 2026-09-02 ]
                    [ Cancel ]  [ Create Customer ]
```

**The copy rule, stated once and binding on every label above.** The upper block describes a **company**: "Legal name", "Trading name" — never "First name", never "Company name" (the entity is the **Customer**), never "Client", which [../../00-Glossary.md](../../00-Glossary.md) bans outright. The lower block describes a **natural person**, so "Given name" / "Family name" are correct *there and only there*, because those fields belong to `OnboardFirstAdminDto` — an **Employee**, a different entity in a different slice. The section heading is what stops a reader carrying person-shaped labels upward. Never write "Admin" unqualified in it; write **Customer Admin**.

The body is **nested**: `{ "customer": {...}, "firstAdmin": {...} }`, mirroring `CreateCustomer` and `OnboardFirstAdminDto`. A flattened body binds both objects to their defaults and returns `422 "Legal name is required."` for a form that plainly had one.

### 5.5 Zod limits, mirrored from the handlers

Every limit is enforced server-side and returns `422`. Mirroring is not optional: `ProblemDetails` carries no field-level errors (§7.3), so whatever the client misses arrives as one sentence with nothing tying it to an input. A stricter client limit blocks legitimate input; a looser one produces exactly that unattributable banner. Sources: `Customers/Application/CustomerValidation.cs`, `Employees/Application/EmployeeValidation.cs`.

| Block | Field | Rule |
|---|---|---|
| Customer | `legalName` / `tradingName` | required ≤300 / optional ≤300 |
| Customer | `taxNumber` / `taxOffice` | required ≤50 / optional ≤200. `409 "A customer with this tax number already exists."` |
| Customer | `addressLine1` / `addressLine2` | required ≤200 / optional ≤200 |
| Customer | `addressCity` / `addressPostalCode` / `addressCountry` | required ≤100 / ≤20 / ≤100 |
| Customer | `contactEmail` / `contactPhone` | required ≤320, **must contain `@`** and nothing more / required ≤40 |
| Customer | `onboardedOn` | required, **at most one day in the future** |
| First admin | `givenName` / `familyName` / `jobTitle` | required ≤100 / required ≤100 / optional ≤200 |
| First admin | `workEmail` / `contactPhone` | **required** here, ≤320, must contain `@` / optional ≤50 |
| First admin | `taxIdentificationNumber` / `socialSecurityNumber` | optional ≤50 each |
| First admin | `employmentStartDate` | required, at most **1 year** in the future |
| Status change | `reason` | optional ≤500 |
| List | `search` | ≤200 |

```ts
// frontend/src/slices/customers/schemas.ts — mirrors CustomerValidation.cs.
// The email rule is deliberately weak: the server checks only for '@', so z.string().email()
// would reject addresses the API accepts and the user could not discover which rule was ours.
const optional = (max: number) =>
  z.string().trim().max(max).optional().transform((v) => (v ? v : null)); // 9.3 rule F: null, not ''

export const customerBlockSchema = z.object({
  legalName: z.string().trim().min(1, 'Legal name is required.').max(300),
  tradingName: optional(300),
  taxNumber: z.string().trim().min(1, 'Tax number is required.').max(50),
  taxOffice: optional(200),
  contactEmail: z.string().trim().min(1).max(320)
    .includes('@', { message: "Contact email must contain '@'." }),
  // DateOnly on the wire: "2026-09-02", no timezone. Compare as a plain string, never via
  // new Date(), which shifts the boundary a day west of UTC (GeneralUIArchitecture 10.2).
  onboardedOn: z.string().date().refine((v) => v <= isoPlusDays(1),
    'Onboarded date cannot be more than one day in the future.'),
  // ...the five address fields and contactPhone, at the lengths tabled above
});
```

**Validate both blocks in one pass.** `OnboardCustomerHandler` validates `FirstAdmin` **before** delegating the Customer half to `CustomerApi.CreateAsync`, so a request wrong in both places returns only the first-admin `422`. A client validating sequentially reproduces that: fix the work email, submit, receive a second `422` about the legal name. One `zodResolver` over the whole nested object shows both sets at once.

### 5.6 The two conflicts mean different things

| `title` | Cause | The form's response |
|---|---|---|
| `A customer with this tax number already exists.` | The **Customer** already exists | Banner; suggest searching `/customers` for it. Nothing was written |
| `That email address is already in use.` | The **login email** is taken, possibly at another Customer | Banner on the same form. Nothing was written |

Both are `409` with no error code, so the UI cannot branch on them programmatically — render `title` verbatim (§2.3 rule F) and attach neither to a field. The second is deliberately vague: `OnboardCustomerHandler` rewrites Identity's `409` precisely so the response *"must not reveal"* which Customer holds the address. Do not "improve" it by naming the Customer; the client does not know it and must not.

---

## 6. Screen: My Customer (`/my-customer`)

**File:** `frontend/src/slices/customers/screens/OwnCustomerScreen.tsx`

```
  Acme Manufacturing S.A.   [Active]
  ─────────────────────────────────────────
  Trading name  Acme
  Address       12 Mill Road
                Athens 10431, Greece
  Email         ops@acme.example
  Phone         +30 210 0000000
              [ Edit contact ]*  * CustomerAdmin only
```

### 6.1 Data and query keys

| Query / mutation | Key or invalidation |
|---|---|
| Own Customer | `['customers', 'own']` — no discriminator; there is only ever one |
| `updateCustomerContact` from here | **invalidate** `['customers','own']`; do not seed it |

The key takes no id deliberately: an id would imply a screen that could show a different Customer, and this endpoint cannot.

**Do not seed `['customers','own']` from the update response.** `update-contact` returns the wide `CustomerDto`; this key holds the narrow `CustomerSelfDto`. Writing one into the other puts `taxNumber`, `taxOffice`, `onboardedOn`, `createdAt` and `updatedAt` into a cache entry typed `CustomerSelf`, and the next component rendering "everything in `own`" starts showing fields this screen is specified not to show. Invalidate instead; it is one extra `GET` on a rarely visited screen.

### 6.2 `CustomerSelfDto` is narrower on the server

`CustomerMapper.ToSelfDto` omits five fields `ToDto` includes: `taxNumber`, `taxOffice`, `onboardedOn`, `createdAt`, `updatedAt`. They are **absent from the response**, which is what [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md):311 demands — *"must be absent from the API response, not merely unrendered"*. There is nothing here for the UI to hide.

> **A `CustomerAdmin` can nevertheless read the wide DTO.** `ViewCustomer` includes `CustomerAdmin` and `GetCustomerHandler` applies `WhereMatchesCustomerScope`, so `GET /api/customers/detail?customerId=<their own>` returns the full `CustomerDto`, tax number included, to a `CustomerAdmin`. That is consistent with [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §3 (*"View own Customer's details: CA Yes"*) and the Glossary's *"Sees everything belonging to their own Customer"*, and it is **not** a leak. It does mean `CustomerSelfDto`'s narrowing is about the *Employee* role, not the Customer side generally. The UI still routes CA here, because `/customers/:id` is `RequireRole`d to the two Accountant roles and no Customer-side screen links to it.

### 6.3 Affordances by role

| Affordance | CA | EMP | Gate |
|---|:--:|:--:|---|
| The screen at all | yes | yes | `RequireRole`; AA and AU get `AccessDeniedPage` |
| *Edit contact* | yes | **no** | `can(role, 'EditCustomerContact')` |
| Legal name, trading name, tax number, tax office | **no** | **no** | `EditCustomerLegal` excludes both roles; three of the four are not even in the DTO |
| Suspend / reactivate | **no** | **no** | AA only; no Customer-side screen mentions either |

An `Employee` gets a read-only card with no buttons. That is the whole screen for them and it is correct; do not invent filler (§12 item 2). The *Edit contact* dialog is **the same component** as §4.3's, posting the same DTO with `customerId` from `own.id` — `CustomerScope` restricts the write to the caller's own row regardless, so there is no second endpoint and no CA-specific form.

### 6.4 Rules

**A. An Accountant reaching this endpoint gets `403`, not `401`.** `GetOwnCustomerHandler` calls `RequireAsync(user, "ViewOwnCustomer")` **before** it reads `CurrentUser.CustomerId`, and that action is granted to `CustomerAdmin` and `Employee` only (`CustomersActionCatalogue.cs:22`). So the permission check refuses an Accountant first and the `AppException("Authentication required.", 401)` on the next line is unreachable for them — it can fire only for a Customer-side caller whose `CustomerId` is somehow null, which is a defensive branch, not a route the router can produce. `CustomersEndpoints.cs` declares `401` and `404` and **not** the `403` that is actually reachable; do not take the declaration as the contract. Either way this is unreachable through the router (§2) and must not be special-cased: §2.3 rule H applies unchanged, and a `403` here is handled as a `403` everywhere else is.

**B. `status` is rendered but will normally read `Active`.** A suspended Customer's people cannot sign in at all (§4.5 item 1), so nobody who can load this screen sees `"Suspended"` on it — except inside the up-to-8-hour window of a session predating the suspension. Render the chip anyway; that window is exactly when a user needs the explanation. Whether it should carry a banner too is in §9.

---

## 7. What these screens must NOT do

**A. Never filter Customers by scope in the browser.** `CustomerScope.WhereMatchesCustomerScope` does it server-side on every read in this slice. [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md):311 — *"Never rely on the React app to hide data. Internal Notes, Accountant-only fields, and out-of-scope records must be **absent from the API response**, not merely unrendered."* A `.filter(c => c.id === session.customerId)` over `/api/customers/list` is therefore not a safeguard: it is **evidence of a server-side leak being concealed**, and it also breaks the pager, because `totalCount` counts the rows it discards. `ListCustomersHandler` keeps its scope filter even though the catalogue makes it a no-op today, and says why — one catalogue edit separates a `CustomerAdmin` from a full customer-list disclosure, and the row-level filter *"would still hold"*. A client-side copy of it holds nothing.

**B. Never render "forbidden", "denied" or "no permission" for a `404`** (§2.3 rule J). Out-of-scope Customers return `404 "Customer not found."` **by design**, because *"a `403` confirms the row exists"*. "Not found" is the only honest wording and it is honest in both cases.

**C. Never call `/api/customers/create` from the SPA.** §1 note 4. Onboarding is the only creation path a screen may use.

**D. Never offer *Delete*, *Archive*, *Remove* or *Merge*.** [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §3: *"Delete a Customer — **Nobody.** Customers are never deleted."* Suspension is the only removal and it is reversible.

**E. Never write "Client", "Clients", "Firm", or "Admin" unqualified — anywhere.** Titles, nav, buttons, dialogs, snackbars, empty states, `aria-label`s, the browser tab title. "Client" and "Firm" are banned outright; "Admin" alone is ambiguous between two roles with different powers ([../../00-Glossary.md](../../00-Glossary.md)). Write **Customer**, **Office**, **Customer Admin**, **Accountant Admin**.

**F. Never label a Customer field with a person's field name.** No "First name", no "Last name", no "Date of birth". A Customer is a company. §5.4.

**G. Never add client-side sort, export, bulk suspend, or Customer import.** None has an endpoint, and a CSV import firing N un-transacted `/onboard` calls is worse than not having one.

**H. Never poll** — not the list, not the detail, not `own`. §3.2 rule H allows exactly one polling query in the application and it is the notification unread count.

**I. Never put `customerId` in an API path, and never in `/api/customers/own`.** §1 note 3 and §2.3 rule D: the SPA route carries the id so the URL is bookmarkable; the API takes it as a query parameter or a body field.

---

## 8. Behavioural cases

Each is a manual check against a running app with a real database.

- [ ] A `CustomerAdmin` at `/customers` sees `AccessDeniedPage`, and the shell shows *My Customer* instead of *Customers* for that role.
- [ ] An `AccountantUser` sees no *Add Customer* button and, at `/customers/:id`, both edit buttons but **no** *Actions* menu at all.
- [ ] `/customers/:id` for a non-existent GUID renders "Not found", never "forbidden".
- [ ] The status filter offers exactly *Active* and *Suspended* — no *Invited*.
- [ ] Searching a Customer's tax number returns nothing, and the box's label already said so. A 201-character term is blocked client-side, not by a `422`.
- [ ] `pageSize: 999` renders a pager consistent with the 50 the server returned, no rows missing.
- [ ] Editing only the contact phone leaves `taxNumber` and `taxOffice` byte-identical in a subsequent `GET /api/customers/detail`.
- [ ] *Edit legal* with another Customer's tax number renders `"A customer with this tax number already exists."` verbatim, every typed value intact.
- [ ] The *Suspend* dialog names all four consequences in §4.5, and its *Reason* label says the reason goes to the audit log.
- [ ] Suspending an already-suspended Customer from a stale tab renders `"This customer is already suspended."` verbatim, and the chip then corrects itself.
- [ ] After suspending: that Customer Admin cannot sign in, an `AccountantUser` still can, and a Customer-side session opened *before* the suspension still loads `/my-customer`.
- [ ] The *Reactivate* success message promises nobody they can sign in.
- [ ] `/customers/new` with both a bad work email and a blank legal name shows **both** errors before any request is sent; with `onboardedOn` two days ahead it is blocked client-side, not by a `422`.
- [ ] A successful `/customers/new` lands on `/customers/:customerId` showing `Active`, the list contains the row without a manual refresh, and no role selector or invitation link appeared anywhere in the flow.
- [ ] `/my-customer` as an `Employee` shows no buttons, and the response body carries no `taxNumber` — check the network tab, not the screen.
- [ ] `grep -rn "customers/create" frontend/src` finds nothing, and `grep -rniE "\bclients?\b|\bfirm\b" frontend/src/slices/customers` finds nothing outside a comment about HTTP.

---

## 9. Questions to flag if unclear

Flag these; do not answer them by inventing a behaviour.

- [ ] **`Invited` is listed as a Customer status in [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §10.1 and does not exist in the code** (§3.5 rule A) — two values, with a `CHECK` constraint enforcing it. Which is wrong, the document or a missing status? If a Customer is meant to have an `Invited` state, nothing creates one and `StatusChip` needs a third colour.
- [ ] **What does suspension do to the Customer's Tickets?** Still open, and not because `Tickets` is unbuilt — it is built and routed — but because no handler addresses it. `SuspendCustomerHandler.cs:45-69` writes the one `customers` row and one audit entry and touches no ticket; the `Tickets` slice reads Customer status nowhere — `CreateTicketHandler` does not even inject `ICustomerApi`, and `GetTicketHandler.cs:130-131` and `ListTicketsHandler.cs:190-210` use it only for `LegalName` — so `01-DomainModel.md:160-161`'s *"no new tickets may be opened for it"* is unenforced. Are open tickets frozen, closed, or served as normal? §4.5's dialog says nothing about tickets because the code says nothing about tickets.
- [ ] **Should an existing session survive its Customer's suspension?** It does today, for up to 8 hours (§4.5 item 3). If not, `Identity` needs an `OnValidatePrincipal` check — a backend change, not a UI decision. [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §13 asks the neighbouring question.
- [ ] **Should `/my-customer` show a suspension banner** in that window (§6.4 rule B), and should the Employee screens? [EmployeesScreens.md](EmployeesScreens.md) §11 asks the same from the other side; one answer should serve both.
- [ ] **Is `onboardedOn` really immutable?** No endpoint changes it (§4.3) and a mis-typed date has no in-app correction. Add it to `UpdateCustomerLegalRequestDto`, or is a support-only `UPDATE` the intended answer?
- [ ] **Is tax-number uniqueness meant to be case- and whitespace-sensitive?** `CreateCustomerHandler` and `UpdateCustomerLegalHandler` compare with `==` after a `Trim()` only, unlike `ticket_types.code`, which is unique on `LOWER(code)`. So `el123456789` and `EL123456789` are two Customers today, and the UI cannot fix it.
- [ ] **Is there any view of the suspension reason?** Audit-only today (§4.5), and only an `AccountantAdmin` may read the audit log ([../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §10). An `AccountantUser` seeing a `Suspended` chip has no way to learn why.
- [ ] **Should the detail screen show an Employee or Customer Admin count?** `CustomerDto` carries neither and no endpoint returns one, so the at-least-one-active-Customer-Admin invariant is invisible from every Customer screen. Adding a count is a backend change.

---

## Files checklist

- [ ] `slices/customers/types.ts` — `CustomerSummary`, `Customer`, `CustomerSelf` and the request types, each commented with the C# file it mirrors; `status` typed `'Active' | 'Suspended'`, **not** including `'Invited'` (§3.5 rule A)
- [ ] `slices/customers/api.ts` — `listCustomers` (POST), `getCustomer` (GET + `URLSearchParams`), `getOwnCustomer` (GET, no argument), `updateCustomerContact`, `updateCustomerLegal`, `suspendCustomer`, `reactivateCustomer`. **No** `createCustomer` (§1 note 4)
- [ ] `slices/customers/queries.ts` — `useCustomerList`, `useCustomer`, `useOwnCustomer`, one mutation hook per write, each stating its invalidations (§3.2, §6.1)
- [ ] `slices/customers/schemas.ts` — `customerBlockSchema`, `firstAdminBlockSchema`, `legalSchema`, `contactSchema` (§5.5)
- [ ] `slices/customers/screens/` — `CustomerListScreen.tsx` (§3), `CustomerDetailScreen.tsx` (§4), `OnboardCustomerScreen.tsx` (§5), `OwnCustomerScreen.tsx` (§6)
- [ ] `slices/customers/components/` — `EditCustomerLegalDialog.tsx` (§4.3), `EditCustomerContactDialog.tsx` (used by §4.3 **and** §6.3), `SuspendCustomerDialog.tsx` (§4.5, the four consequence sentences and *Reason*), `ReactivateCustomerDialog.tsx` (§4.6)
- [ ] `frontend/src/routes.tsx` — the four rows in §2 wired with `RequireRole`; **no** `/customers/:customerId/edit`
- [ ] `frontend/src/shared/permissions/can.ts` — the eight Customers rows verified against `CustomersActionCatalogue.cs`, plus `OnboardCustomer` from `EmployeesActionCatalogue.cs`

---

## Success criteria

Each is verified by running the app, not by reading the code.

1. `/customers` and `/customers/:customerId` are reachable by both Accountant roles and neither Customer-side role; `/my-customer` is the exact reverse. No role opens both.
2. `slices/customers/api.ts` uses `post` for `list` and `get` for `detail` and `own`, neither has been "corrected" to the other verb, and `getOwnCustomer` takes no parameter.
3. There is no `createCustomer` anywhere in `frontend/src`, and no screen reaches `/api/customers/create`.
4. `/customers/new` issues exactly **one** request on submit, and a deliberately failing submit leaves no Customer behind — verified by searching `/customers` afterwards.
5. The onboarding form shows errors from both the Company and First Customer Admin blocks in a single submit attempt.
6. An `AccountantUser` sees no suspend or reactivate affordance anywhere, in any menu, in any state.
7. A `CustomerAdmin` at `/my-customer` can edit contact details and has no path — button, link or URL — to legal name, trading name, tax number or tax office.
8. The `/api/customers/own` response carries no `taxNumber`, `taxOffice`, `onboardedOn`, `createdAt` or `updatedAt`, for both CA and EMP.
9. Editing one field and saving leaves every other field byte-identical in a subsequent `GET /api/customers/detail`.
10. The suspend dialog states the login lockout, the Accountant exemption, the surviving session and the non-restoration of individual accounts — and the reactivate success copy promises none of it.
11. Suspending a Customer blocks its Customer Admin's next sign-in and disturbs no Accountant's.
12. No client-side `.filter(...)` or conditional row render exists in `slices/customers/` for the purpose of hiding a Customer.
13. Every `422` and `409` renders the server's `title` verbatim above the submit button with all typed input intact, and no `traceId` is shown for either.
14. No screen renders an `Invited` chip for a Customer, a raw status string that is not a Glossary term, the word "Client", the word "Firm", or "Admin" unqualified.
15. The only "Given name" and "Family name" in the slice are under the *First Customer Admin* heading of `/customers/new`; no Customer field carries a natural person's label.
16. The eight Customers rows in `can.ts` match `CustomersActionCatalogue.cs` exactly — same action names, same role sets, no extras on either side.
