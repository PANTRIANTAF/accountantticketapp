# Employee Screens

This slice has the most role-dependent UI in the application. Four roles reach it through the same endpoints, and the endpoints answer them differently: `POST /api/employees/list` is a `403` for an `Employee` and a cross-Customer table for an Accountant, and `POST /api/employees/get` returns **one of two different DTO shapes** with nothing in the JSON to mark which arrived. The wrong branch here renders `undefined` where a person's employment status should be, with no error anywhere.

Second: almost every permission in [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4 reads *"Yes, own Customer"* or *"Own record only"*, and **the client cannot evaluate either qualifier**. `can()` answers "may an `Employee` view an employee record" — yes — and cannot answer "may this Employee view *that* record". `EmployeesActionCatalogue.cs` says so itself: the catalogue *"expresses WHICH ROLES MAY CALL, and nothing else"*, and the qualifiers *"are enforced by the scope filter and the self checks inside each handler"*. Every screen below follows from that: draw the affordance from the role, let the server decide the row, render `404` as "Not found".

Third: three irreversible or near-irreversible operations sit one menu item apart — *Mark departed*, *Suspend access*, *Invite* — and one cannot be undone through the API at all. §8 exists because picking the wrong one is the most expensive mistake a Customer Admin can make.

**Precedence.** Documents 0–4 win, then [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md), then this file. **Where any of them disagrees with this document, they win and this document is wrong** — fix this document, do not code around it.

- [../../README.md](../../README.md) — *Locked platform decisions*, *Conflict precedence*
- [../../00-Glossary.md](../../00-Glossary.md) — banned terms, binding in UI copy
- [../../01-DomainModel.md](../../01-DomainModel.md) §2 — why `Employee` and `UserAccount` are separate
- [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4, §11, §12 — normative role matrix
- [../../04-Infrastructure.md](../../04-Infrastructure.md) §1–3 — hosting and the dev loop
- [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) — governing UI document; cited by section below, never restated
- [../../Slices/Employees/IMPLEMENTATION_PLAN.md](../../Slices/Employees/IMPLEMENTATION_PLAN.md) §0.3, §12, §13 — backend invariants and flagged items

---

## 0. Role coverage

Where each Employee-related entry in [../../README.md](../../README.md)'s role-grouped brief lands.

| README brief | Roles | Covered by | Note |
|---|---|---|---|
| "Customer Admin: employee list" | CA | §4 | The same screen serves AA and AU, with a Customer filter |
| "Customer Admin: employee tickets" | CA | **Not here.** Blocked on `Tickets` (§0.1) | — |
| "Shared: profile/settings" | all four | §7 | Partially blocked; see the callout in §7.2 |
| (not in the brief) Employee detail | all four | §5 | EMP reaches their own record only |
| (not in the brief) Register an Employee | AA, AU, CA | §6 | A dialog, not a route |
| (not in the brief) Onboard a Customer | AA | **Not here.** [CustomersScreens.md](CustomersScreens.md) | §1 note 6 |

---

## 1. Endpoints this slice consumes

AA = `AccountantAdmin`, AU = `AccountantUser`, CA = `CustomerAdmin`, EMP = `Employee`. The Roles column is *who may call*; every row except `update-own-contact` is additionally scoped server-side.

| Route | Verb | Request DTO | Response DTO | Roles | Notes |
|---|---|---|---|---|---|
| `/api/employees/register` | POST | `RegisterEmployeeRequestDto` | `EmployeeDetailDto` | AA, AU, CA | Creates **no** account, sends **no** email |
| `/api/employees/list` | POST | `ListEmployeesRequestDto` | `PaginatedResponse<EmployeeSummaryDto>` | AA, AU, CA | A **POST read** — note 1 |
| `/api/employees/get` | POST | `EmployeeIdRequestDto` | `EmployeeDetailDto` **or** `EmployeeSelfDto` | all four | A **POST read**, two shapes — §2 |
| `/api/employees/update` | POST | `UpdateEmployeeRequestDto` | `EmployeeDetailDto` | AA, AU, CA | Full replacement — §5.5 rule C |
| `/api/employees/update-own-contact` | POST | `UpdateOwnContactRequestDto` | `EmployeeSelfDto` | CA, EMP | **No employee id** — §7.5 rule A |
| `/api/employees/invite` | POST | `InviteEmployeeRequestDto` | `EmployeeDetailDto` | AA, AU, CA | `role` is an **integer** |
| `/api/employees/set-role` | POST | `SetEmployeeRoleRequestDto` | `MarkedResultDto` | AA, AU, CA | `role` integer; not immediate — §8.3 |
| `/api/employees/depart` | POST | `DepartEmployeeRequestDto` | `MarkedResultDto` | AA, AU, CA | Reversible **only as a correction** — §8.1 |
| `/api/employees/reinstate` | POST | `EmployeeIdRequestDto` | `MarkedResultDto` | AA, AU, CA | Undoes a departure **and** reactivates the account — §8.1 |
| `/api/employees/change-login-email` | POST | `ChangeEmployeeLoginEmailRequestDto` | `MarkedResultDto` | **AA, AU only** | Not the work email; not self-service — §8.7 |
| `/api/employees/suspend-account` | POST | `EmployeeIdRequestDto` | `MarkedResultDto` | AA, AU, CA | Reversible — §8.2 |
| `/api/employees/reactivate-account` | POST | `EmployeeIdRequestDto` | `MarkedResultDto` | AA, AU, CA | Refused for a departed Employee — §8.5 |
| `/api/customers/onboard` | POST | `OnboardCustomerRequestDto` | `OnboardCustomerResponseDto` | AA | Registered here, specified elsewhere — note 6 |

1. **`/api/employees/list` and `/api/employees/get` are `POST` reads. Do not "fix" them to `GET`.** §2.3 rule C names both: the filter object is too large for a query string, and changing the verb produces a `405` with nothing in the body explaining it. `api.ts` calls `post` for both and names the functions `listEmployees` / `getEmployee`, so nobody reading the wrapper concludes the verb was a typo.
2. Every id travels in the request **body**; the SPA route carries it in the path (§2.3 rule D).
3. `/api/employees/update` replaces every field including the nullable ones — `UpdateEmployeeRequestDto`: *"omitting WorkEmail clears it"*.
4. `MarkedResultDto` is `{ success: boolean }` and carries no state, so §3.2 rule D's seed-from-the-response pattern does **not** apply to the six operations returning it — `set-role`, `depart`, `reinstate`, `change-login-email`, `suspend-account`, `reactivate-account`. They invalidate instead (§4.2 rule B).
5. Every mutation can return `403`; every one naming an employee can return `404`. The `404` is the scoping mechanism, not a bug (§2.4 item 5).
6. `/api/customers/onboard` is registered from `EmployeesEndpoints.cs` on purpose and LOCKED — *"Do not 'tidy' this into the Customers slice"* — but it **creates a Customer**, and its screen is `/customers/new`. It is specified in [CustomersScreens.md](CustomersScreens.md) and **must not be duplicated here**: two specs for one form is how the two field lists drift. `slices/employees/api.ts` still owns the wrapper function, because `api.ts` mirrors the endpoint file, and `slices/customers/` imports it under §1.4 rule C.

---

## 2. The three response shapes, and how the UI tells them apart

The most important section in this document.

There are three read DTOs because [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4's *"View an Employee record"* row has three different answers. `EmployeeReadDtos.cs` explains why they are three types rather than one with nulled-out fields: *"a type that has no SocialSecurityNumber property cannot serialise one."*

### 2.1 Which caller gets which shape

| Endpoint | Caller | Shape | Otherwise |
|---|---|---|---|
| `/api/employees/list` | AA, AU, CA | `EmployeeSummaryDto` page | EMP: `403` — `ListEmployees` excludes them |
| `/api/employees/get` | AA, AU | `EmployeeDetailDto`, any Customer | — |
| `/api/employees/get` | CA | `EmployeeDetailDto`, own Customer | Another Customer: `404` |
| `/api/employees/get` | EMP | `EmployeeSelfDto`, **own record only** | **Anybody else's, including a colleague at their own Customer: `404`** |
| `/api/employees/update-own-contact` | CA, EMP | `EmployeeSelfDto` | AA, AU: `403` |

Internalise the colleague case. `GetEmployeeHandler` applies `WhereInCustomerScope` **and then** a second `UserAccountId == accountId` filter for the `Employee` role, commented: *"Without this second filter any Employee can read every colleague's tax identification number and social-security number by guessing an id."* An Employee has exactly one readable employee record — their own. Everything else in this slice is a `404` to them.

### 2.2 The exact field difference

Mirrors `Slices/Employees/Application/Dtos/EmployeeReadDtos.cs`.

| Field | `EmployeeSummaryDto` | `EmployeeDetailDto` | `EmployeeSelfDto` |
|---|:--:|:--:|:--:|
| `id` | yes | yes | yes |
| `customerId` | — | yes | yes |
| `givenName`, `familyName`, `jobTitle` | yes | yes | yes |
| `workEmail`, `contactPhone` | — | yes | yes |
| `employmentStartDate` | — | yes | yes |
| `status` (`"Active"`\|`"Departed"`) | yes | yes | **absent** |
| `hasAccount` | yes | yes | **absent** |
| `role` (integer, nullable) | yes | yes | **absent** |
| `accountStatus` (string, nullable) | — | yes | **absent** |
| `taxIdentificationNumber`, `socialSecurityNumber` | — | yes | **absent** |
| `employmentEndDate` | — | yes | **absent** |
| `createdAt` | — | yes | **absent** |
| `notice` | — | — | yes, **and only from `update-own-contact`** |

Three consequences:

- **`notice` is `null` on a read.** `EmployeeMapper.ToSelfExpression` never sets it; only `UpdateOwnContactHandler` does. A profile screen that renders the login-email warning when `notice` is present shows it *after* saving and not *before*, which is exactly backwards. Render the warning from static copy (§7.5 rule B).
- **`EmployeeSummaryDto` has no email**, yet `searchTerm` matches work email server-side. The list searches a column it cannot display. Say so in the search field's helper text, or a Customer Admin will report the search as broken when it returns a row whose visible fields do not contain what they typed.
- **`EmployeeSummaryDto` has no `accountStatus`.** `hasAccount: true` means an account exists, not that anybody can sign in with it. Never label that column "Active".

### 2.3 Discriminate on the session role, not on field presence

**Mandatory.** `queries.ts` decides the shape **before** the call, from `useSession().role`, and exposes two hooks:

```ts
/** Mirrors EmployeeReadDtos.cs. Two shapes, one endpoint -- see EmployeesScreens.md section 2. */
export function useEmployeeDetail(employeeId: string) {
  const { role } = useSession();
  return useQuery<EmployeeDetail>({
    queryKey: ['employees', 'detail', employeeId],
    queryFn: () => getEmployee(employeeId) as Promise<EmployeeDetail>,
    enabled: role !== UserRole.Employee,
  });
}

export function useOwnEmployeeRecord(employeeId: string) {
  return useQuery<EmployeeSelf>({
    queryKey: ['employees', 'self', employeeId],
    queryFn: () => getEmployee(employeeId) as Promise<EmployeeSelf>,
  });
}
```

**A. Never sniff fields to decide the shape.** `'status' in response`, `response.status !== undefined`, and a zod union discriminating on optionality all work today and all break the first time a field is added to `EmployeeSelfDto` or made nullable in `EmployeeDetailDto` — and they break *silently*, by sending a full record down the narrow branch. `status` is a particularly bad sniffing key: it collides with `ApiError.status`, with the HTTP status, and with `accountStatus`, so the bug reads as correct code.

**B. Two shapes, two cache keys** — `['employees','detail',id]` and `['employees','self',id]` (§3.1). One key holding either shape means a role change hands a component the other type.

**C. Two `types.ts` interfaces. No union, no optional-everything superset.** An `EmployeeDetail | EmployeeSelf` union forces a narrowing check at every field access, and that check is rule A's field sniff wearing a type annotation.

> **`.Produces<EmployeeDetailDto>()` on `/api/employees/get` is therefore incomplete.** The endpoint also returns `EmployeeSelfDto` — `GetEmployeeHandler.Handle` is declared `Task<object>` for that reason — and the metadata declares only one of the two. Its `.WithDescription` does document the narrowing, and is itself slightly wrong: it names the status, the account link, the end date and the two identifying numbers, but `EmployeeSelfDto` also omits `createdAt`, `role` and `accountStatus`, and adds `notice`. A generated client would be wrong here, exactly as §2.6 warns for `/api/accountants/list`. Recorded in `BACKEND_CHANGES_REQUIRED.md` and §11.

---

## 3. Routes and screens

From §4.1, which is normative. This table adds no route to it.

| SPA path | Screen component | Roles |
|---|---|---|
| `/employees` | `EmployeeListScreen` | AA, AU, CA |
| `/employees/:employeeId` | `EmployeeDetailScreen` | AA, AU, CA, EMP |
| `/profile` | `ProfileScreen` | AA, AU, CA, EMP |

`RequireRole` wraps each with exactly those roles and renders `AccessDeniedPage`, not a redirect (§4.3 rule A). There is deliberately **no** `/employees/new` and **no** `/employees/:employeeId/edit`; both are dialogs (§6.1).

---

## 4. Screen: Employee list (`/employees`)

**File:** `frontend/src/slices/employees/screens/EmployeeListScreen.tsx`

### 4.1 Layout

```
  Employees                                            [ Register Employee ]
  ─────────────────────────────────────────────────────────────────────────
  [ Search name or work email ] [ Customer v ]* [ Status v ] [ Account v ]
                                 * Accountant roles only
  ┌──────────────────┬────────────┬────────────┬────────────┬─────────────┐
  │ Name             │ Job title  │ Employment │ Role       │ Access      │
  ├──────────────────┼────────────┼────────────┼────────────┼─────────────┤
  │ Doe, Jane        │ Payroll    │ [Active]   │ Cust. Adm. │ Has account │
  │ Roe, Richard     │ --         │ [Departed] │ Not invited│ Not invited │
  └──────────────────┴────────────┴────────────┴────────────┴─────────────┘
                                          Rows per page: 15 v  1-2 of 2  <>
```

Sorted by family name, then given name, then id — server-side, fixed by `ListEmployeesHandler` to match `idx_employees_customer_name`. Offer no column sorting: `ListEmployeesRequestDto` has no sort parameter, and a client-side sort would reorder one page of fifteen out of a hundred.

### 4.2 Data and query keys

**File:** `frontend/src/slices/employees/queries.ts`

| Query / mutation | Key or invalidation |
|---|---|
| The list | `['employees','list',{ customerId, status, hasAccount, searchTerm, pageNumber, pageSize }]` |
| `registerEmployee`, `updateEmployee`, `inviteEmployee` | `setQueryData(['employees','detail', dto.id], dto)`; invalidate `['employees','list']` |
| `setEmployeeRole`, `departEmployee`, `suspendAccount`, `reactivateAccount` | invalidate `['employees','detail', employeeId]` **and** `['employees','list']` |
| `updateOwnContact` | `setQueryData(['employees','self', dto.id], dto)`; invalidate `['employees','list']` |

**A. Every filter appears in the key.** Omitting `searchTerm` makes two searches share one cache entry, and the table then shows the previous query's rows under the new query's pager.

**B. The four `MarkedResultDto` mutations cannot seed the cache.** Invalidating both keys is the only way the screen learns the new state, and it is a real second round trip. Do not paper over it with a guessed `accountStatus`: §3.2 rule E bans optimistic updates outright, and here the client could not guess correctly anyway — `suspend-account` changes a value the list DTO never returns.

**C. `usePaginatedQuery` only** (§3.2 rule G). `pageSize` is clamped to 50, not rejected (§2.4 item 6).

### 4.3 Affordances by role

| Affordance | AA | AU | CA | Gate |
|---|:--:|:--:|:--:|---|
| The screen | yes | yes | yes | `RequireRole`; EMP gets `AccessDeniedPage` |
| *Register Employee* | yes | yes | yes | `can(role,'RegisterEmployee')` |
| Customer filter | yes | yes | **no** | Role check, not `can()` — see below |
| Status / Account filters, search | yes | yes | yes | none |
| Row menu *View* | yes | yes | yes | always |
| Row menu *Edit*, *Invite*, *Change role*, *Suspend access*, *Restore access*, *Mark departed* | yes | yes | yes | `can()` plus §8's hide rules |

The Customer filter is drawn for the Accountant roles only. `ListEmployeesHandler` returns `403 "You may only list employees at your own customer."` when a `CustomerAdmin` names another Customer, deliberately — `ListEmployeesRequestDto`: *"a filter that quietly means something else for one role is how a Customer Admin comes to believe they have cross-Customer visibility."* Drawing the control for a CA and then not sending it is the same lie in the other direction. Hide it.

> **An Accountant's cross-Customer list cannot show the employer.** `EmployeeSummaryDto` carries neither `customerId` nor a customer name, so with no Customer filter an `AccountantUser` sees a page of names belonging to unidentified Customers. `EmployeeDetailDto` does carry `customerId`, so the *detail* screen can resolve a name via `slices/customers/api.ts` (§1.4 rule D); the list cannot. Do not work around it by fetching each row's detail — that is fifteen extra POSTs per page. Until `customerId` is added to the summary DTO, an Accountant arriving with no Customer chosen gets an `EmptyState` asking them to pick one, and the table renders only once `customerId` is set. Recorded in `BACKEND_CHANGES_REQUIRED.md` and §11.

### 4.4 States

| State | Render |
|---|---|
| First load | `Skeleton` rows; header, filters and pager stay put (§7.4) |
| Refetch with data | Keep the rows, subtle progress indicator (§7.4) |
| `totalCount === 0`, no filters | `EmptyState` "No employees yet" + *Register Employee* where `can()` allows |
| `totalCount === 0`, filters set | `EmptyState` "No employees match these filters" + *Clear filters* |
| `items: []` with `totalCount > 0` | Over-run page — `EmptyState` + *Back to the first page* (§3.3 item 2) |
| Query failed | `ErrorBanner` replacing the table body (§7.2) |
| Accountant, no Customer chosen | The callout in §4.3 |

### 4.5 Rules

**A. Never filter rows in the browser** — see §9 rule A.

**B. `role` is a nullable integer.** `null` renders "Not invited", **never** "Employee": `EmployeeSummaryDto.Role` says *"Do NOT default it to Employee: that would show every accountless person as holding a role they do not have."* And `AccountantAdmin` is `0`, which is falsy, so never test `role` for truthiness (§10.1 consequence 1). Render through `format/enums.ts`, never raw (§10.1 consequence 2).

**C. The status filter's only values are `"Active"` and `"Departed"`** — anything else is `422 "Unknown employee status."`. Send `null` or omit for "both", never `''` (§9.3 rule F).

**D. Departed Employees are visible by default and that is correct.** The endpoint: *"Returns both Active and Departed Employees unless a status filter is supplied."* Do not default the filter to `Active` — `ListEmployeesHandler` notes that a default which hides them *"makes a Customer Admin think the record is gone"*, and nothing ever deletes an Employee ([../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4: *"Delete an Employee record — Nobody."*).

**E. Debounce `searchTerm` by 300 ms and cap it at 200 characters** — the handler returns `422 "Search must be at most 200 characters."`, and every keystroke is a new query key, so an undebounced box is one POST per character.

**F. Reset `pageNumber` to 1 whenever a filter changes**, or a narrowed filter leaves the pager on page 4 of a 1-page result and the user sees the over-run empty state instead of their rows.

---

## 5. Screen: Employee detail (`/employees/:employeeId`)

**File:** `frontend/src/slices/employees/screens/EmployeeDetailScreen.tsx`

### 5.1 Layout

```
  Jane Doe · Acme Ltd                                    [ Actions v ]
  ────────────────────────────────────────────────────────────────────
  Employment: [Active]        Access: [Suspended]

  Contact                          Employment
    Work email  jane@acme.example    Started         2024-03-01
    Phone       +30 210 0000000      Ended           --
                                     Role            Customer Admin
  Identification  (AA, AU, CA)       Record created  2024-02-28
    Tax id      ... [Show]
    Social sec. ... [Show]
```

For an `EMP` caller the screen renders the `EmployeeSelfDto` fields only — name, job title, work email, phone, start date — with **no chips, no Identification card, no Actions menu**. None of those fields exists in the response, and an empty chip is a rendered `undefined`.

### 5.2 Data

`useEmployeeDetail(employeeId)` for AA/AU/CA, `useOwnEmployeeRecord(employeeId)` for EMP (§2.3). The employer name is a separate query against `slices/customers/api.ts` keyed on `detail.customerId` — the one legitimate cross-slice import in the application (§1.4 rule D) — so a `404` on the Customer suppresses the name rather than blanking the page.

### 5.3 The two statuses

Two independent statuses live here. Both are strings, they come from two different slices, and **they share the value `"Active"`**.

| | Field | Values | Owner | Changed by |
|---|---|---|---|---|
| Employment | `status` | `"Active"`, `"Departed"` | `employees` table | `/api/employees/depart` only |
| Access | `accountStatus` | `null`, `"Invited"`, `"Active"`, `"Suspended"` | Identity, via `IIdentityApi` | `invite`, `suspend-account`, `reactivate-account`, and `depart` as a side effect |

**A. Two `StatusChip`s, each with a visible prefix label** — `Employment: Active`, `Access: Suspended`. Two bare chips reading "Active" and "Suspended" side by side are unreadable, and a single merged chip is worse: a Departed Employee's account is Suspended, so merging loses the distinction all of §8 depends on.

**B. One colour map, inside `StatusChip`** (§8.3). `Suspended` must never be green on one screen and red on another, and colour is never the only carrier — the word is always shown (§8.4).

**C. `accountStatus === null` renders `Access: Not invited`** — not "Inactive", not an empty chip. It means `hasAccount` is `false`: no account exists, which is a different fact from a suspended one.

**D. Render what arrived; do not infer one status from the other.** An `Active` Employee may have a `Suspended` account (access revoked, still employed). `Departed` with `Active` access does not occur today because `DepartEmployeeHandler` suspends in the same transaction, but do not assert that in the UI.

### 5.4 Affordances by role

| Affordance | AA | AU | CA | EMP | Gate |
|---|:--:|:--:|:--:|:--:|---|
| Contact, Employment cards | yes | yes | yes | own only | — |
| Identification card | yes | yes | yes | **absent from the response** | render only if the field exists — `can()` is not enough |
| *Edit details* | yes | yes | yes | no | `can(role,'UpdateEmployee')` |
| *Edit my contact details* | no | no | yes | yes | `can(role,'UpdateOwnContact')`, own record only |
| Actions menu (§8) | yes | yes | yes | no | per action |

The Identification card holds two personal identifying numbers stored in plain text ([../../Slices/Employees/IMPLEMENTATION_PLAN.md](../../Slices/Employees/IMPLEMENTATION_PLAN.md) §12 constraint 9). Mask both behind a per-field *Show* toggle, per mount, never persisted. This is **not** a security control — the values are in the response and the network tab — it stops a tax number being on screen during a screen-share, which is the realistic exposure. Never present it as a control in review.

### 5.5 Rules

**File:** `frontend/src/slices/employees/components/EditEmployeeDialog.tsx`

**A. The work email is not the login email, and the form must say so, in the form.** Helper text under the field, matching the endpoint's own description in substance: *"Changing the work email does NOT change the address this person signs in with. The login email lives on their account — use /api/employees/change-login-email, which is Accountant-only."* Without it a Customer Admin will "fix" a colleague's login here, believe it done, and the colleague will keep failing to sign in with the new address while nobody can locate the problem. Required copy, **and it differs by role** — telling a Customer Admin to use an action they are refused is the same dead end in a new place:

> **Accountants:** Work email is contact information. It is **not** the address this person signs in with. To change how they log in, use *Change login email* in the Actions menu.
>
> **Customer Admins and Employees:** Work email is contact information. It is **not** the address this person signs in with, and changing it here does not change how they log in. Only the accounting office can change a login email — contact them.

**B. The same sentence goes on the *Invite* dialog's `loginEmail` field, inverted.** There the address supplied **does** become the permanent login, and `InviteEmployeeHandler` writes it back to `WorkEmail` as well. It is the only moment in the person's life when that address is chosen. Say so.

**C. Pre-fill from the loaded `EmployeeDetailDto` and submit every field.** `/api/employees/update` replaces all of them, so a form submitting only what was touched sends `null` for the rest and **silently erases the tax identification number, the social-security number, the phone and the work email** — with a `200`, no warning and no undo. If the detail query has not resolved, the dialog does not open. For the same reason, offer no inline-cell or bulk edit.

**D. A Departed Employee's record is still editable, deliberately** — `UpdateEmployeeHandler`: *"Correcting a misspelled name or a wrong tax number after somebody has left is ordinary work."* Do not disable *Edit* on a `Departed` row.

**E. Zod, mirrored from `EmployeeValidation.cs`.**

| Field | Rule | Server message |
|---|---|---|
| `givenName` | required, trimmed, ≤100 | `"Given name is required."` / `"Given name must be at most 100 characters."` |
| `familyName` | required, trimmed, ≤100 | `"Family name is required."` / `"…at most 100 characters."` |
| `jobTitle` | optional, ≤200 | `"Job title must be at most 200 characters."` |
| `workEmail` | optional, ≤320, **must contain `@`** | `"Work email must contain '@'."` |
| `contactPhone` | optional, ≤50 | `"Contact phone must be at most 50 characters."` |
| `taxIdentificationNumber` | optional, ≤50 | `"Tax identification number must be at most 50 characters."` |
| `socialSecurityNumber` | optional, ≤50 | `"Social security number must be at most 50 characters."` |
| `employmentStartDate` | required, ≤ today + 1 year | `"Employment start date cannot be more than 1 year(s) in the future."` |
| `customerId` (register only) | required, non-empty GUID | `"Customer is required."` |

Containing `@` is the **whole** email rule on the server. Do not add a stricter regex: a client rule the server does not have rejects an address the server would accept, and the user cannot discover which rule is imaginary (§9.2). Trim before submitting (§9.3 rule E) and send `null`, not `''` (§9.3 rule F). Dates are `"YYYY-MM-DD"` strings with no timezone (§10.2). The one-year ceiling is an **invented, self-flagged** threshold ([../../Slices/Employees/IMPLEMENTATION_PLAN.md](../../Slices/Employees/IMPLEMENTATION_PLAN.md) §13 item 8); mirror it and expect it to change.

**F. `409` from an edit is the per-Customer work-email uniqueness:** `"An employee with this work email already exists at this customer."` Render it as a form banner with a *Reload and try again* affordance (§7.1). It is **not** a lost-update warning — there is no concurrency control in this backend (§9.4), so two Admins editing one Employee both get `200` and the second write wins silently. The version-number mitigation §9.4 prescribes for ticket types has **no counterpart here**: `EmployeeDetailDto` carries no version and no `updatedAt`. Do not synthesise one from `createdAt`.

**G. `422 "Employment start date cannot be after the recorded employment end date."`** is reachable only on a `Departed` record. Mirror it in Zod when the loaded detail has an `employmentEndDate`, so it never arrives as an unattachable banner (§7.3 item 4).

---

## 6. Screen: Register Employee — a dialog

**File:** `frontend/src/slices/employees/components/RegisterEmployeeDialog.tsx`

### 6.1 Why a dialog and not a route

§4.1's route table is normative and contains `/customers/new` but **no** `/employees/new`. A screen spec loses to that document, so inventing the route here would put this file in conflict with its governing document — and the route would then be missing from `routes.tsx` and from the shell's role gating. A dialog needs neither.

It is also right on its merits: nine flat fields, no steps, always opened from `/employees` with the Customer context already on screen, and nothing worth deep-linking to. Compare `/customers/new`, which an Accountant Admin may be sent a link to, and `/ticket-types/new`, which has an unbounded field list worth bookmarking mid-build. If a bookmarkable registration form is later wanted, that is a change to §4.1, not a local decision.

### 6.2 Behaviour

| Concern | Specification |
|---|---|
| Opened from | *Register Employee* in `PageHeader`, gated by `can(role,'RegisterEmployee')` |
| `customerId` | AA/AU: a required Customer picker from `slices/customers/api.ts`. CA: **not rendered**, filled from `session.customerId` |
| Schema | §5.5 rule E, plus `customerId` |
| On success | `Snackbar` "Employee registered"; seed `['employees','detail', created.id]`; invalidate the list; close |
| Then what | Offer *Invite* in the snackbar's action slot — see rule B |
| On failure | `ErrorBanner` above the submit button, `role="alert"`, focus moved to it (§8.4). The form is **never** reset on error (§9.3 rule D) |

**A. This creates an accountless Employee — no account, no email.** The endpoint: *"Creates an accountless Employee. No login is created and no email is sent."* The title and submit button must not imply otherwise: *Register*, never *Add user*, never *Invite*, never *Create account*. [../../01-DomainModel.md](../../01-DomainModel.md) §2 calls the Employee/`UserAccount` separation the most important structural decision in the model — an accountless Employee can still be the Subject of a Ticket their Customer Admin opens.

**B. Do not merge registration and invitation into one form with a "send invitation" checkbox.** [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4: *"Registering and inviting are two separate operations… A Customer Admin may do the first without ever doing the second."* Two endpoints, two audit meanings, two permissions. A checkbox would make the SPA chain two POSTs with no transaction, so a failed invite leaves a registered Employee behind an error message that looks like nothing happened.

**C. `workEmail` is optional here and required by `/api/customers/onboard`.** Do not copy the onboarding form's validation across: `OnboardFirstAdminDto` requires it because that operation always invites. A registered Employee with no email is legitimate, and the consequence — `422 "No email address on file for this employee."` — belongs on the Invite dialog.

**D. `403 "You may only register employees at your own customer."` is a scope failure, not a role failure** — one of the few `403`s in this API that is not a bug in `can()` (§6.2 rule B). Render the `title` verbatim.

**E. `422 "This customer is not active."` / `"Unknown or inactive customer."`** A suspended Customer cannot gain Employees. Offer `Active` Customers only in the picker, so this is the race case and not the normal one.

**F. Never retry a failed register.** Nothing here is idempotent and there is no idempotency key; a retry creates a second Employee (§3.4).

---

## 7. Screen: Profile / my own contact details (`/profile`)

**File:** `frontend/src/slices/employees/screens/ProfileScreen.tsx`

It lives in this slice because the only API call it makes belongs to this slice. The session arrives from `shared/auth/useSession`, which anyone may import (§1.4 rule B), so there is no cross-slice import to justify.

### 7.1 Layout

```
  My profile
  ─────────────────────────────────────────────
  Account
    Name      Jane Doe        (session)
    Role      Customer Admin  (session, via format/enums.ts)
    Customer  Acme Ltd        (customers/api.ts -- CA and EMP only)
                                   [ Change password ]

  My contact details                 CA and EMP only
    Work email  jane@acme.example
    Phone       +30 210 0000000
    (i) Work email is contact information only. It is not the
        address you sign in with.
```

### 7.2 Data

| Region | Source | Roles |
|---|---|---|
| Account | `useSession()` → `['identity','session']` | all four |
| Customer name | `/api/customers/own` via `slices/customers/api.ts` | CA, EMP |
| Change password | Link to `/change-password` | all four |
| My contact details | `/api/employees/update-own-contact` | CA, EMP |

> **No endpoint reads your own Employee record without already knowing its id, and the session does not carry one.** `SessionDto` is `(UserId, DisplayName, Role, CustomerId, MustChangePassword)`; `UserId` is a **UserAccount** id, and `POST /api/employees/get` with it returns `404`. An `Employee` cannot obtain their own `employeeId` at all — `ListEmployees` excludes their role and `/api/customers/own` returns `CustomerSelfDto`. So the contact-details form **cannot be pre-filled**.
>
> **Do not work around this by submitting a blank form.** `UpdateOwnContactRequestDto` is a full replacement of its two fields, so posting an untouched form sends `{ workEmail: null, contactPhone: null }` and **erases both**, with a `200` and a cheerful snackbar. That is the single most destructive thing this specification could accidentally prescribe.
>
> Therefore, until the backend adds `employeeId` to `SessionDto` or a `POST /api/employees/get-own` taking no id: the *My contact details* region renders **read-only, with no fields and no submit button**, above a short notice that contact details are changed by asking a Customer Admin. Both go in `BACKEND_CHANGES_REQUIRED.md`; `employeeId` on `SessionDto` is the smaller change and unblocks §7.4 as written. Recorded in §11.

### 7.3 Affordances by role

| Affordance | AA | AU | CA | EMP |
|---|:--:|:--:|:--:|:--:|
| Account region, *Change password* | yes | yes | yes | yes |
| Customer name | — | — | yes | yes |
| *My contact details* | **no** | **no** | blocked (§7.2) | blocked (§7.2) |

The Accountant roles get no contact region at all. `UpdateOwnContact` excludes them and `EmployeesActionCatalogue.cs` says why: *"an Accountant has no Employee record at all, so a clean 403 here beats a confusing 404 from the handler."* `can()` returns `false`, and the region is hidden, not disabled (§6.2 rule C).

### 7.4 The form, for when §7.2's read gap closes

Two fields, `workEmail` and `contactPhone`, validated per §5.5 rule E. Not the name, not the job title, not the dates, not the identifying numbers — `UpdateOwnContactRequestDto`: *"A person cannot promote themselves, cannot backdate their employment, and cannot alter the numbers the Office files taxes with."* Somebody needing their name corrected asks a Customer Admin ([../../Slices/Employees/IMPLEMENTATION_PLAN.md](../../Slices/Employees/IMPLEMENTATION_PLAN.md) §12 constraint 7); say that on the screen so the absence reads as a rule and not a missing feature. On success, seed `['employees','self', response.id]` — this response is the only place a Customer-side caller learns their own employee id — and invalidate the list, because a Customer Admin's own row appears in it.

### 7.5 Rules

**A. Never add an `employeeId` to `update-own-contact`.** Its absence *is* the security control. `UpdateOwnContactRequestDto`: *"an EmployeeId here, however carefully checked, turns every future edit of the handler into an opportunity to check it wrongly."* The endpoint is structurally incapable of editing a colleague — not "checked", incapable — and that is worth more than reusing the `/update` wrapper. Concretely: `api.ts` exposes `updateOwnContact(body: { workEmail, contactPhone })` with **no** id parameter, so no call site can supply one. A Customer Admin editing a colleague uses `/api/employees/update` from `/employees/:employeeId`, which is scoped, audited differently, and needs `UpdateEmployee`.

**B. Render the login-email notice from static copy, not from `response.notice`.** The server sets `notice` on every successful write (`UpdateOwnContactHandler.LoginEmailNotice`) and never on a read (§2.2), so a screen keyed on its presence shows the warning after the mistake. Show static helper text at all times, and additionally surface `response.notice` verbatim in the success snackbar — the server's wording is written for the user.

**C. `404 "You do not have an employee record."`** is a Customer-side session whose account has no Employee row: a data fault the user cannot fix. Render the `title` and stop. Do not redirect and do not offer *Register*.

**D. `409` here is the same work-email collision as §5.5 rule F**, and the message names another Employee's address without naming them. Do not embellish it with "someone else at your company has this address" — you do not know that from the response.

---

## 8. The irreversible, near-irreversible, and correctable operations

> **Two of the actions below were unreachable by every role until 2026-09-02, and the fix is in the
> working tree but not committed.** `reinstate` and `change-login-email` are registered endpoints
> whose handlers call `RequireAsync(user, "ReinstateEmployee")` and
> `RequireAsync(user, "ChangeEmployeeLoginEmail")`. Both action names were absent from
> `EmployeesActionCatalogue.cs`, and `PermissionChecker` is fail-closed on an unrecognised name, so
> both routes returned `403` to every caller including an `AccountantAdmin` and every attempt wrote a
> `PermissionDenied` audit entry against a person who was entitled to the action.
> **`EmployeesActionCatalogue.cs` now declares both**, at lines 53 and 60, with the role lists
> [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4 grants. This was item 26 in
> [../BACKEND_CHANGES_REQUIRED.md](../BACKEND_CHANGES_REQUIRED.md), now marked resolved there.
>
> **What that means for this file.** The specifications in §8.1 and §8.7 stand, and both buttons are
> now buildable. But **verify the two catalogue entries before you write their `can()` rows** rather
> than trusting this paragraph: `Slices/Employees/` is untracked in git, so the fix has no commit
> behind it and a lost working tree silently restores the bug. A `can()` of `true` against a
> guaranteed `403` is the exact bug [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §6.2
> rule B names, and it is the reason the check is cheap and the failure is not. If either entry is
> missing when you look, build these two menu entries last and in the same commit that restores it.
> Do not ship them gated on a hardcoded `true`. See also success criterion 16.

| Action | Endpoint | Reversible? | Reversed by | `ConfirmDialog`? |
|---|---|---|---|---|
| Mark departed | `depart` | **Only as a correction** | *Reinstate* — see §8.1 | **Mandatory** |
| Reinstate | `reinstate` | Yes | *Mark departed* again | **Mandatory** |
| Suspend access | `suspend-account` | Yes | *Restore access* | **Mandatory** |
| Restore access | `reactivate-account` | Yes | *Suspend access* | No |
| Change role | `set-role` | Yes | Change it back | **Mandatory** |
| Change login email | `change-login-email` | Yes | Change it back | **Mandatory** |
| Invite | `invite` | **No un-invite exists** | Only *Suspend access* | **Mandatory** |

### 8.1 *Mark departed* is reversible only as a correction, and both dialogs must say which

**Updated 2026-09-02.** This section previously said departure was irreversible and that the dialog had to promise so. `POST /api/employees/reinstate` now exists, so that copy is wrong — but the fix is **not** to soften the dialog into "you can always undo this". The distinction the whole feature rests on:

| | *Reinstate* | Register again |
|---|---|---|
| What happened | The departure was entered against the wrong record, or with the wrong facts | The person genuinely left, and later came back |
| What the UI does | `POST /api/employees/reinstate` | The §3 registration form, from scratch |
| Result | The same record returns to `Active`; its account is reactivated automatically | A second, separate Employee record |
| Their old tickets | Still on this record, as before | Stay on the old record |

Both are one click apart and the server cannot tell them apart — the audit entry records which one the caller chose. **So the copy carries the distinction, on both dialogs.** Required copy in substance:

> **Mark Jane Doe as departed?**
> This records that she has left **and** suspends her access immediately, in one step.
> If you enter this against the wrong person you can correct it with *Reinstate*. That is for fixing a mistake — if she genuinely leaves and later returns, register her again as a new Employee. Her tickets stay on this record either way.
> An end date is required. It may be in the future, and the record is marked Departed straight away either way.
> [ Cancel ] [ Mark departed ]

> **Reinstate Jane Doe?**
> Use this only to correct a departure that should not have been recorded. She returns to Active and her access is restored in the same step — you do not also need *Restore access*.
> **If she left and has now come back, do not use this.** Register her again as a new Employee, so the two periods of employment stay separate.
> [ Cancel ] [ Reinstate ]

**Reinstate is in the *Employment* menu group, next to *Mark departed*, and is not red** — it is a repair, and §8.2's colour rule reserves `error` for the destructive direction. It appears **only** when `status === "Departed"`, and is available to both Accountant roles and to a Customer Admin in their own Customer ([../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4).

Its errors: `422 "This employee has not departed."` — a stale row; render the banner and invalidate. `422 "This customer is not active."` — a suspended Customer gains nobody, and only an Accountant can lift that.

**Reinstate reactivates the account automatically**, which means the account's status may go to `Invited` rather than `Active` — that happens when the person was invited but never accepted before they were departed ([../../Slices/Identity/IMPLEMENTATION_PLAN.md](../../Slices/Identity/IMPLEMENTATION_PLAN.md) §9.1 rule 14). So do **not** write success copy claiming she can sign in; the `Access:` chip after invalidation is the truth.

**A. The end date is part of the dialog, required, and validated.** `employmentEndDate` must be present — `422 "Employment end date is required."` — and not before `employmentStartDate` — `422 "Employment end date cannot be before the employment start date."`. Mirror both in Zod against the loaded detail. There is no upper bound: `DepartEmployeeRequestDto` says a future date is normal for a notice period. Do **not** imply the departure is scheduled; the record flips to `Departed` on submit.

**B. The confirm button is `color="error"` and is the only red button on the screen.**

**C. `422 "This employee has already departed."`** comes from a stale row: render the banner and invalidate, do not treat it as a failure to report.

### 8.2 *Suspend access* must look visibly different from *Mark departed*

`suspend-account` *"revokes access without ending employment"* and does **not** mark anybody departed; `depart` does both. They are one menu item apart, one is reversible and one is not, and the wrong choice cannot be taken back.

| | *Suspend access* | *Mark departed* |
|---|---|---|
| Menu group | *Access* | *Employment*, below a `Divider` |
| Icon | Lock | Person-off |
| Colour | default | `error` |
| Dialog title | "Suspend Jane Doe's access?" | "Mark Jane Doe as departed?" |
| Dialog body | "She stays employed. You can restore access at any time." | The §8.1 copy |
| Position | Never adjacent to *Mark departed* | Last item in the menu |

Never render them as two options in one "Change status" submenu, and never as a single toggle.

### 8.3 *Change role* is not immediate, and the operator must be told

**A. `role` is sent as an integer** — `CustomerAdmin` is `2`, `Employee` is `3` (§10.1: the enum's declaration order is the contract, and a string is a `400`). Use the `UserRole` const object, never a hand-typed literal.

**B. Only those two values may be offered.** Either Accountant role is `422 "An Employee's role must be CustomerAdmin or Employee."`, rejected outright per [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4. The select therefore has exactly two hard-coded options; do not build it by filtering the four-role enum, or an added enum member becomes a `422` the user cannot explain.

**C. The change is not immediate.** `SetEmployeeRoleHandler`: *"the target's live session keeps the old role for up to 8 hours, because claims are minted at login. Demotion therefore fails UNSAFE — a demoted Customer Admin keeps administrative powers until their cookie expires."* Required copy on the demotion path:

> This takes effect the next time she signs in. If she is signed in now she keeps Customer Admin powers until her session expires — up to 8 hours. Suspend her access as well if the change must be immediate.

The last sentence is the actionable part. Do not promise immediacy, and do not attempt to "fix" the lag with a poll (§9 rule G).

**D. Other `422`s, rendered verbatim:** `"This employee has no account. Invite them before setting a role."` — so hide *Change role* when `hasAccount === false`; `"This employee already has that role."` — a no-op is refused, so disable the option matching the current role; `"This employee's account could not be found."`

### 8.4 The at-least-one-active-Customer-Admin invariant

`EmployeeInvariants.RequireAnotherActiveCustomerAdminAsync` guards **three** operations — demoting, departing, suspending — and rejects any that would leave a Customer with no `CustomerAdmin` whose account is `Active` and whose Employee record is `Active`:

```
422  "This Customer must always have at least one active Customer Admin."
```

**A. It is a `422`, not a `403`, and the copy must respect that.** The handler: *"the caller has the role, the data's state forbids the operation. A 403 would suggest re-authenticating as somebody more powerful, which would not help."* So never render "permission denied" for this. Render the `title` verbatim in the dialog's `ErrorBanner`, leave the dialog **open**, and add one line of guidance: *promote another Employee to Customer Admin first, then try again.* That is the only recovery a Customer Admin has; otherwise, per [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4, *"only an Accountant can resolve such a situation."*

**B. The UI must not try to predict it.** Counting `role === UserRole.CustomerAdmin` rows on the current page and disabling the action is wrong three ways: the page is one of many, `EmployeeSummaryDto` has no `accountStatus` so the client cannot tell an active Customer Admin from a suspended one, and the guard has an accepted concurrency window ([../../Slices/Employees/IMPLEMENTATION_PLAN.md](../../Slices/Employees/IMPLEMENTATION_PLAN.md) §12 constraint 2). A button greyed out on a wrong guess is worse than a `422`, because the user cannot even attempt the operation to learn why.

**C. There is no longer a "too many accounts" `422` from this guard.** This rule previously required rendering `"This Customer has too many accounts for the Customer Admin check to run. Contact the accounting office."` above 500 accounted Employees. **That message was removed on 2026-09-02** — the guard now looks accounts up in batches of 500 instead of refusing above it, so a Customer of any size is handled ([../../Slices/Employees/IMPLEMENTATION_PLAN.md](../../Slices/Employees/IMPLEMENTATION_PLAN.md) §13 item 5). Do not build a client-side branch for it and do not add the string to any error map; it will never arrive.

**D. Self-action is a fourth `422`, from a different guard:** `"You cannot change your own role or account status."` A Customer Admin may not demote, depart or suspend themselves — [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4, and `SuspendEmployeeAccountHandler`: *"This is what stops a Customer locking itself out."* Hide those three actions when the record shown is the caller's own; but the client cannot reliably identify its own record (§7.2), so keep the banner as the backstop.

### 8.5 *Restore access* — what the UI must not promise

**A. Refused for a departed Employee:** `422 "A departed employee's account cannot be reactivated. Reinstate them if the departure was recorded by mistake, or register them again if they have returned."` The handler explains that a Departed Employee's suspension is a *consequence* of their departure, so lifting it here would leave `Departed` employment with `Active` access — a pair nothing else produces. **Hide the action entirely when `status === "Departed"`** and offer *Reinstate* (§8.1) in its place; the banner is the backstop, not the design. *Reinstate* restores the account itself, so the two are never both needed.

**B. It does not reset a password and does not clear a lockout.** So the success copy must not say "she can sign in again". Use:

> Access restored. If she cannot sign in, she can reset her own password from the sign-in page — restoring access does not reset a password or clear a lockout.

**C. `422 "This employee has no account to reactivate."` / `"…to suspend."`** — a `422`, not a `404`: the Employee exists, there is nothing to act on. Hide both actions when `hasAccount === false`.

### 8.6 *Invite* has no reverse

There is no un-invite and no delete-account endpoint ([../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4: *"Delete an Employee record — Nobody."*). Once invited, the only lever is *Suspend access*. `ConfirmDialog` is therefore required, and must name the two surprising consequences: an email goes out immediately to the address shown, and that address becomes the person's permanent login.

Errors: `422 "A departed employee cannot be invited."` — hide the action when `status === "Departed"`; `409 "This employee already has an account."`; `422 "No email address on file for this employee."`; and `409 "That email address is already in use."`, a **system-wide** login-email collision whose message deliberately *"must not say where"* — do not embellish it with "at another Customer". Whether re-inviting an already-`Invited` person is supported is unspecified (§13), so do not build a *Resend invitation* button; §11 flags it.

### 8.7 *Change login email* is Accountant-only, and that is the point

**Added 2026-09-02.** `POST /api/employees/change-login-email` takes `{ employeeId, loginEmail }` and moves the address the person signs in with. [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4 grants it to `AccountantAdmin` and `AccountantUser` and to **nobody else** — not a Customer Admin, not the account's own owner:

> Whoever can move an account to a new address can move it to a mailbox they control; a Customer Admin doing it to a colleague is account takeover one step removed, and the colleague is the one who then cannot log in.

**A. Gate it on the Accountant roles, in the menu, not in the dialog.** `can(role,'ChangeEmployeeLoginEmail')`. A Customer Admin must not see a disabled item — a greyed-out *Change login email* invites a support request for a power that is deliberately withheld. They see the §5.5 rule A helper text instead, which tells them to contact the office.

**B. It is not on `/profile`, ever.** Nobody changes their own, including an Accountant Admin (§7, and [../BACKEND_CHANGES_REQUIRED.md](../BACKEND_CHANGES_REQUIRED.md) §10).

**C. `ConfirmDialog` is mandatory and must name the two consequences.** Required copy in substance:

> **Change the address Jane Doe signs in with?**
> She will sign in as `jane.new@example.com` from now on. The old address stops working.
> This does **not** change her password, and it does **not** change her work email — those are separate. If she is signed in right now, her session keeps working until it expires, up to 8 hours.
> [ Cancel ] [ Change login email ]

**D. Pre-fill the field with the current login email if the response carries it, and otherwise leave it empty** — `EmployeeDetailDto` may not expose it (§2.3). Never pre-fill it with the **work** email: they are different addresses that are usually equal, and pre-filling the wrong one turns a change into a silent revert.

**E. Errors, rendered verbatim, dialog left open:** `422 "This employee has no account, so there is no sign-in address to change. Invite them first."` — hide the action when `hasAccount === false`; `422 "This employee has departed."` — hide it when `status === "Departed"`, and offer *Reinstate* (§8.1) as the first step; `409` on a duplicate address, whose message deliberately does not say which account holds it (§8.6) — do not embellish it.

**F. Invalidate the detail query and the list on success.** The work email did not change, so a UI that only re-reads `workEmail` shows nothing happening and the operator will run it again.

---

## 9. What these screens must NOT do

**A. Never filter employees to the caller's Customer in the browser.** `CustomerScope` does it server-side on every query a Customer-side caller can reach ([../../Slices/Employees/IMPLEMENTATION_PLAN.md](../../Slices/Employees/IMPLEMENTATION_PLAN.md) §0.3). [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §12: *"Never rely on the React app to hide data. Internal Notes, Accountant-only fields, and out-of-scope records must be **absent from the API response**, not merely unrendered."* A `.filter(e => e.customerId === session.customerId)` here is not a safeguard; it is **evidence of a server-side leak being concealed**, and it would break the pager too, since `totalCount` counts rows the filter throws away. `EmployeeSummaryDto` has no `customerId` to filter on, which is the shape of the API telling you not to.

**B. Never use `can()` to decide whether *this* employee may be edited.** §6.2 rule D: *"The table expresses who may call, not which rows… `can()` cannot answer 'may I edit this employee'."* `can()` draws the button; the server decides whether the row exists.

**C. Never render "forbidden", "denied" or "no permission" for a `404`** (§2.3 rule J). An Employee requesting a colleague's record gets a `404` **by design**, and "Not found" is the only honest wording — honest whether the row is absent or invisible, where a `403` would confirm the row exists.

**D. Never strip a field for security.** If `socialSecurityNumber` arrives, the caller was entitled to it. §5.4's masking is ergonomics, not a control.

**E. Never build a cross-Customer client-side search, an export, or a bulk action.** None has an endpoint. There is no employee import either ([../../Slices/Employees/IMPLEMENTATION_PLAN.md](../../Slices/Employees/IMPLEMENTATION_PLAN.md) §12 constraint 10): onboarding 200 Employees is 200 calls, and a CSV dropzone firing 200 POSTs with no transaction is worse than not having one.

**F. Never offer *Delete*, in any form** — including "archive", "hide" or "remove". Nobody may delete an Employee; `Departed` is terminal and the record is kept forever.

**G. Never poll** — not the list, not a detail, not to detect the 8-hour role lag. §3.2 rule H allows exactly one polling query in the application, and it is the notification unread count.

**H. Never add an id to `update-own-contact`, and never reuse `/api/employees/update` to implement "edit my own details"** (§7.5 rule A).

**I. Never duplicate the onboarding form here** (§1 note 6).

---

## 10. Behavioural cases

Each is a manual check against a running app with a real database.

- [ ] An `Employee` opening `/employees` sees `AccessDeniedPage`, and the shell shows no *Employees* nav item for that role.
- [ ] An `Employee` at `/employees/:id` for **their own** id sees name, job title, work email, phone and start date — and no status chips, no Identification card, no Actions menu.
- [ ] An `Employee` at `/employees/:id` for a **colleague at their own Customer** sees "Not found", never "forbidden".
- [ ] A `CustomerAdmin` at `/employees/:id` for another Customer's employee sees "Not found".
- [ ] A `CustomerAdmin` sees no Customer filter on `/employees`; an `AccountantUser` does.
- [ ] With no status filter the list contains both `Active` and `Departed` rows.
- [ ] A never-invited Employee's row shows *Not invited* in both Role and Access — never "Employee", never a blank chip.
- [ ] A detail screen for an employed person with revoked access shows `Employment: Active` **and** `Access: Suspended`, both labelled.
- [ ] Editing an employee, changing only the phone, leaves the tax identification number, social-security number and work email unchanged in a subsequent `get`.
- [ ] The edit form shows the work-email-is-not-the-login-email notice before any field is touched.
- [ ] Requesting `pageSize: 999` renders a pager consistent with the 50 the server returned.
- [ ] A 201-character search term is blocked client-side, not by a `422`.
- [ ] Demoting the only active Customer Admin renders the invariant `422` verbatim, with the dialog still open and the promote-first guidance present.
- [ ] A `CustomerAdmin` sees no *Change role*, *Suspend access* or *Mark departed* on their own row; forcing it renders `"You cannot change your own role or account status."`
- [ ] The *Mark departed* dialog names the account suspension and the fact that undoing it is a **correction**, requires an end date, and rejects a date before the start date without a round trip. It must **not** call departure irreversible — `/api/employees/reinstate` exists (§11).
- [ ] *Reinstate* appears only on a `Departed` row, and its copy frames the action as correcting a mistaken departure, not as re-hiring.
- [ ] *Change login email* is absent for a `CustomerAdmin` and an `Employee` at every entry point, and a duplicate address renders the `409` verbatim.
- [ ] *Suspend access* and *Mark departed* are in different menu groups with different icons, and only the second is red.
- [ ] Demoting somebody who is signed in shows the 8-hour warning, and their session genuinely keeps the old role until it expires.
- [ ] *Restore access* is absent for a `Departed` employee, and its success copy promises neither a password reset nor a cleared lockout.
- [ ] *Invite* on an employee with no work email is blocked client-side or renders `"No email address on file for this employee."`
- [ ] `/profile` as an `AccountantAdmin` shows no contact-details region at all.
- [ ] `/profile` as an `Employee` shows **no submit button** for contact details while §7.2's gap is open.
- [ ] Letting the session expire and reloading `/employees` redirects to `/login` once, with no retry storm and no toast.
- [ ] `frontend/src/slices/employees/api.ts` contains thirteen `post(` calls and no `get(` — the twelve `/api/employees` routes plus `onboardCustomer`.

---

## 11. Questions to flag if unclear

Flag these; do not answer them by inventing a behaviour. Items 1–3 block screens in this document.

- [ ] **How does a `CustomerAdmin` or `Employee` obtain their own `employeeId`?** `SessionDto` carries a UserAccount id and nothing maps it to an Employee id for a Customer-side caller. `/profile`'s form cannot be pre-filled without it, and submitting it unfilled erases both fields (§7.2). Add `employeeId` to `SessionDto`, or add `POST /api/employees/get-own` taking no id.
- [ ] **`EmployeeSummaryDto` has no `customerId` and no customer name**, so an Accountant's cross-Customer list cannot show anyone's employer (§4.3). Add `customerId` to the summary, or have the endpoint refuse an unfiltered cross-Customer list?
- [ ] **`.Produces<EmployeeDetailDto>()` on `/api/employees/get` declares one of two shapes**, and its description under-lists what `EmployeeSelfDto` omits (§2.3 callout). Declare both, or move the narrow shape to its own route?
- [x] **ANSWERED 2026-09-02 — `Departed` is not terminal.** `POST /api/employees/reinstate` takes `{ employeeId }`, returns `MarkedResult`, and is granted to both Accountant roles **and** to a Customer Admin within their own Customer ([../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4). It clears the end date and the departure timestamp and **reactivates the account automatically** — so the *Restore access* action is not a second step. It is a **correction, not a re-hire**: somebody who genuinely left and came back is registered again as a new record. The UI must say that, or it becomes the re-hire button. `422` when the Employee has not departed, `422` when the Customer is not active. Screens: a *Reinstate* entry in the §8 Actions menu, visible only on a `Departed` row, not red, and copy that names the correction framing rather than promising a rehire path. The §8 *Mark departed* dialog must **stop** calling departure irreversible — checklist line for that is above and now wrong.
- [x] **ANSWERED 2026-09-02 — an Accountant changes it.** `POST /api/employees/change-login-email` takes `{ employeeId, loginEmail }`, returns `MarkedResult`, and is granted to `AccountantAdmin` and `AccountantUser` **only** — not a Customer Admin, not the person themselves ([../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4). `409` when another account holds the address; `422` when the Employee has no account yet ("invite them first") or has departed. It touches neither the password nor the work email, and the person's live session keeps working for up to 8 hours. Screens: a *Change login email* entry in the §8 Actions menu gated on the Accountant roles, and the §5.5 rule A copy now branches by role. A Customer Admin still gets "contact the accounting office" — which is now a real route to a real endpoint rather than a dead end.
- [ ] **Is *Resend invitation* supported** for somebody already `Invited`? Nothing documents the behaviour beyond the `409`.
- [ ] **Should the 8-hour role-change lag be surfaced anywhere but the dialog?** §8.3 warns the operator; nothing warns the target.
- [ ] **Is the one-year future `EmploymentStartDate` ceiling real?** `EmployeeValidation.cs` flags it as invented (§13 item 8); §5.5 rule E mirrors it only because the server enforces it.
- [ ] **Should a suspended Customer's Employees see a suspension banner** on these screens? A Customer Admin can see the Customer's status via `/api/customers/own`.
- [x] **RESOLVED 2026-09-02 — the governing document's count was a slip and is fixed.** §2.3 rule C said *"four are POST"* and then listed five routes; it now says *"exactly five"*. Both `/api/employees/list` and `/api/employees/get` are in it.

---

## Files checklist

- [ ] `frontend/src/slices/employees/types.ts` — `EmployeeSummary`, `EmployeeDetail`, `EmployeeSelf`, `MarkedResult` and the request types, each commented with the C# file it mirrors (§2.2)
- [ ] `frontend/src/slices/employees/api.ts` — the twelve `/api/employees` functions (including `reinstate` and `changeLoginEmail`) plus `onboardCustomer`, all `post`; `updateOwnContact` takes no id (§7.5 rule A)
- [ ] `frontend/src/slices/employees/queries.ts` — `useEmployeeList`, `useEmployeeDetail`, `useOwnEmployeeRecord`, one mutation hook per write, each stating its invalidations (§4.2)
- [ ] `frontend/src/slices/employees/schemas.ts` — the Zod schemas of §5.5 rule E
- [ ] `frontend/src/slices/employees/screens/EmployeeListScreen.tsx` (§4)
- [ ] `frontend/src/slices/employees/screens/EmployeeDetailScreen.tsx` (§5)
- [ ] `frontend/src/slices/employees/screens/ProfileScreen.tsx` (§7)
- [ ] `frontend/src/slices/employees/components/EmployeeFieldset.tsx` — the nine shared fields, used by the register and edit dialogs
- [ ] `frontend/src/slices/employees/components/RegisterEmployeeDialog.tsx` (§6)
- [ ] `frontend/src/slices/employees/components/EditEmployeeDialog.tsx` (§5.5)
- [ ] `frontend/src/slices/employees/components/InviteEmployeeDialog.tsx` (§8.6)
- [ ] `frontend/src/slices/employees/components/SetRoleDialog.tsx` (§8.3)
- [ ] `frontend/src/slices/employees/components/DepartEmployeeDialog.tsx` (§8.1)
- [ ] `frontend/src/slices/employees/components/EmployeeStatusPair.tsx` — the two labelled `StatusChip`s of §5.3
- [ ] `frontend/src/routes.tsx` — the three rows of §3, wired with `RequireRole`; **no** `/employees/new`
- [ ] `frontend/src/shared/permissions/can.ts` — the thirteen Employees rows, verified against `EmployeesActionCatalogue.cs` **by reading the file, not by trusting this checklist**. Thirteen only if the catalogue still declares `ReinstateEmployee` and `ChangeEmployeeLoginEmail`; eleven if it does not (§8 callout)

---

## Success criteria

Each is verified by running the app, not by reading the code.

1. An `Employee` signing in and opening their own `/employees/:employeeId` sees a complete screen with no blank field, no empty chip and no `undefined`; the same URL with a colleague's id says "Not found".
2. `frontend/src/slices/employees/` contains no expression testing a field's presence to decide which DTO arrived — no `'status' in`, no `?.status !== undefined`, no union discriminated on optionality. The shape is chosen from the session role, before the call.
3. Every call in `api.ts` uses `post`, and none of the thirteen has been "corrected" to `GET`.
4. Editing one field and saving leaves every other field byte-identical in a subsequent `POST /api/employees/get`.
5. Both edit forms and the invite dialog show the work-email-versus-login-email copy before any interaction.
6. `updateOwnContact` has no id parameter, and no call site passes one.
7. `/profile` offers no way to submit empty contact details, in any role.
8. Demoting, departing or suspending the last active Customer Admin renders the server's `422` verbatim, leaves the dialog open, and names promotion as the way forward; no button was pre-emptively disabled to guess at the invariant.
9. *Suspend access* and *Mark departed* are distinguishable at a glance, and only *Mark departed* is confirmed with irreversibility copy naming the simultaneous account suspension.
10. The *Change role* select offers exactly two options, sends an integer, and disables the option matching the target's current role.
11. Demoting a signed-in Customer Admin shows the 8-hour warning, and the target's session demonstrably keeps the old role afterwards.
12. *Restore access* is absent for a `Departed` employee, and its success message promises neither a password reset nor a cleared lockout.
13. No screen in this slice offers delete, archive, export, import or a bulk action.
14. No screen renders a raw role integer, the word "Client", "User" as a role label, or "Admin" unqualified.
15. Two status chips are never rendered without their `Employment:` / `Access:` labels, and `Suspended` has one colour across the application.
16. The Employees rows in `can.ts` match `EmployeesActionCatalogue.cs` exactly — same action names, same role sets, **no extras on either side**. That is **thirteen** rows against the catalogue as it stands today; the count is not the criterion, the exact match is. Read the catalogue and count, rather than taking thirteen from this line: `ReinstateEmployee` and `ChangeEmployeeLoginEmail` were absent until 2026-09-02 and the fix is uncommitted (§8 callout), so if they have gone again the answer is eleven and those two buttons come out of the menu. A `can()` row the catalogue does not have produces a button that `403`s for everyone, which is the failure this criterion exists to catch.
