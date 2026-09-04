# Ticket Types Screens — UI Implementation Plan

This is an executable step-by-step plan for the **fourth** slice of the SPA and for one piece of
shared machinery that no other slice plan owns. Follow it in order. Do not add features, do not
skip steps, and do not make an architectural decision that is not written here. If something is
unclear, **flag it** in the section at the end rather than inventing a behaviour.

**Build position.** [`UI/Plans/00-Foundation/IMPLEMENTATION_PLAN.md`](../00-Foundation/IMPLEMENTATION_PLAN.md)
scaffolds `frontend/`, the API client, the session, the router, the shell and the ten shared
components. That plan is **phase 0 of this one** and is a hard prerequisite: nothing below
compiles without it. This plan then builds, in this order, the ticket-type list, the ticket-type
detail screen, **the shared dynamic form renderer**, the field-descriptor editor, and the
stale-form guard the editor cannot ship without.

The renderer is the reason this plan is larger than the screen count suggests. It is the most
reused component in the finished application and its only consumer today is one read-only
preview. It is built here because the endpoint that feeds it already exists, and it is built into
`frontend/src/shared/dynamicForm/` rather than into the slice folder because the as-yet-unplanned
`Tickets` UI is its real consumer, and `slices/tickets/` may import only `types.ts` and
`api.ts` from another slice
([`UI/GeneralUIArchitecture.md`](../../GeneralUIArchitecture.md) §1.4 rule C). A renderer written
inside `slices/ticketTypes/` is a renderer `Tickets` must either import illegally or copy.
Phase 0 deliberately does **not** create those four files; this plan owns all of them.

**Documents that govern this document, in precedence order**

| # | Document | Sections this plan leans on |
|---|---|---|
| 1 | [`../../../00-Glossary.md`](../../../00-Glossary.md) | *Ticket Type*, *Field Descriptor*, *Field Value* — binding in UI copy |
| 2 | [`../../../01-DomainModel.md`](../../../01-DomainModel.md) | §9.7 — concurrency, and why `ticket_types` has no version column |
| 3 | [`../../../02-AuthorizationMatrix.md`](../../../02-AuthorizationMatrix.md) | §1, §5, §11, §12 — normative; §5 forbids delete and forbids Admin-only authoring |
| 4 | [`../../../04-Infrastructure.md`](../../../04-Infrastructure.md) | §1–3 — hosting, the dev loop |
| 5 | [`../../../App/GeneralAppArchitecture.md`](../../../App/GeneralAppArchitecture.md) | §8 — route shape, pagination envelope, error contract |
| 6 | [`../../GeneralUIArchitecture.md`](../../GeneralUIArchitecture.md) | §1.2, §1.4, §1.5, §2, §3, §4, §6, §7, §8, §9.2, §9.3, §9.4, §10 |
| 7 | [`../../LoginArchitecture.md`](../../LoginArchitecture.md) | §3 — the forced-password-change gate |
| 8 | [`../../Screens/TicketTypesScreens.md`](../../Screens/TicketTypesScreens.md) | The screen and renderer contract. **Normative for this plan.** |
| 9 | [`../00-Foundation/IMPLEMENTATION_PLAN.md`](../00-Foundation/IMPLEMENTATION_PLAN.md) | Phase 0. Peer plan, not a governing document |
| — | [`../../BACKEND_CHANGES_REQUIRED.md`](../../BACKEND_CHANGES_REQUIRED.md) | **Non-normative.** Cited by item number only |

This plan loses to every one of rows 1–8. Where it appears to contradict one of them, the other
document wins and this one is wrong — fix this document, do not code around it.

---

## 0. Prerequisites — read before writing any code

### 0.1 Phase 0, and what this plan may assume exists

Do not start section 2 until every row below resolves. Each is built by
[`../00-Foundation/IMPLEMENTATION_PLAN.md`](../00-Foundation/IMPLEMENTATION_PLAN.md) and **none of
it is re-specified here.**

| Assumed to exist | Why this plan cannot proceed without it |
|---|---|
| `frontend/` scaffold, `vite.config.ts` with the `/api` proxy | Punch-list items 3 and 8. Without the proxy every call is `ECONNREFUSED` |
| `shared/api/http.ts`, `ApiError.ts`, `problemDetails.ts` | The only module allowed to call `fetch` (§2.1). This slice's `api.ts` is a wrapper over it |
| `shared/api/paginated.ts`, `queryClient.ts`, `hooks/usePaginatedQuery.ts` | The list screen renders from `response.pageSize`, never from the request (§2.4 item 6) |
| `shared/auth/` — session, `RequireSession`, `RequireRole` | Route gating for `/ticket-types/new` and `/edit` |
| `shared/permissions/can.ts` with the five TicketTypes rows | §6.1. Verified against `TicketTypesActionCatalogue.cs` in phase 9 |
| The ten `shared/components/` | `PaginatedTable`, `ConfirmDialog`, `StatusChip`, `ErrorBanner`, `EmptyState`, `LoadingRegion`, `NotFoundPage`, `PageHeader`, `AppShell`, `AccessDeniedPage` |
| `shared/format/dates.ts`, `money.ts`, `enums.ts` | `createdAt`/`updatedAt` rendering, and `MoneyAmount` display in `mode="read"` |
| `theme.ts`, `routes.tsx` | This plan adds four rows to `routes.tsx` and creates no new provider |

**Phase 0 does not create `shared/dynamicForm/`.** That is deliberate, and stated in
[`../../GeneralUIArchitecture.md`](../../GeneralUIArchitecture.md) §1.2, where the folder is
listed with a pointer to the screen spec rather than to a foundation section. Its four files plus
`types.ts` are created in section 6 of **this** plan and nowhere else.

**Dependencies are closed.** The locked list is
[`../../GeneralUIArchitecture.md`](../../GeneralUIArchitecture.md) §1.5. This plan adds nothing to
it: no drag-and-drop library, no JSON-schema-to-form library, no regex-safety package, no
`@mui/x-date-pickers-pro`. Anything that feels missing goes in section 13, never into a step.

### 0.2 Route casing — the one typo that costs an afternoon

Every route in this slice is **`/api/ticket-types/...`**, kebab-cased at every word boundary,
verified at `AccountantApp.Api/Slices/TicketTypes/TicketTypesEndpoints.cs:14`
(`app.MapGroup("/api/ticket-types")`). The SPA paths match: `/ticket-types`,
`/ticket-types/new`, `/ticket-types/:ticketTypeId`, `/ticket-types/:ticketTypeId/edit`.

`/api/tickettypes/list` is a `404` that reads like a missing endpoint, `/api/ticketTypes/list` is
a `404` that reads like a casing bug in the server, and neither is caught by TypeScript because
both are string literals. `GeneralUIArchitecture.md` §0.2 decision 8 names this specific doubled
`t` as *"an invisible typo that reads as a missing row"*. Write the six paths once, in
`api.ts`, and never spell one again anywhere else in the slice.

### 0.3 Six endpoint facts to confirm in the network tab before phase 3

Each is read out of the C# source below. Each is cheap to confirm and expensive to assume. A
future builder confirms them by running the app; nothing here has been observed in a response.

| # | Fact | Read from |
|---|---|---|
| 1 | `list`, `detail`, `version` are **`GET`**. A `POST` returns `405` | `TicketTypesEndpoints.cs:45, 56, 63` |
| 2 | `create`, `edit`, `toggle` are **`POST`** with the id in the body | `TicketTypesEndpoints.cs:16, 28, 37` |
| 3 | `create` returns **`201`** with `Location: /api/ticket-types/detail?ticketTypeId=<id>` | `TicketTypesEndpoints.cs:20` |
| 4 | `pageSize=999` returns `200` with `"pageSize": 50` | `Shared/Pagination/PaginatedQuery.cs:10-12` |
| 5 | `detail`/`version` take **non-nullable** `Guid`/`int`; a missing one is a `400` from the model binder with framework wording | `TicketTypesEndpoints.cs:56, 63`; `Shared/Errors/AppExceptionMiddleware.cs:31-36` |
| 6 | The list is ordered `DisplayName, Id` **server-side, before paging** | `ListTicketTypesHandler.cs:41` |

Fact 5 is why `api.ts` must never be called with an unresolved route parameter. Use TanStack
Query's `enabled` (§3.2 rule B), not a `try`/`catch`.

---

## 1. Build order — nine phases

Build in this order. The ordering is not stylistic: three of the arrows are load-bearing.

| Phase | Deliverable | Depends on | Why here |
|---|---|---|---|
| 0 | The shared kernel | — | [`../00-Foundation`](../00-Foundation/IMPLEMENTATION_PLAN.md). Prerequisite |
| 1 | `shared/dynamicForm/types.ts`, `slices/ticketTypes/types.ts`, `api.ts` | 0 | Types before hooks, hooks before screens |
| 2 | `queries.ts`, the four `routes.tsx` rows | 1 | |
| 3 | `TicketTypeListScreen` + `ToggleTicketTypeDialog` | 2 | The cheapest screen that proves the wiring |
| 4 | `TicketTypeDetailScreen` (no preview yet) + `VersionBanner` | 3 | Proves the detail DTO, including `fields` |
| 5 | **`shared/dynamicForm/`** — `visibility.ts`, `buildZodSchema.ts`, `fieldRegistry.tsx`, `DynamicForm.tsx` | 4 | See below |
| 6 | Wire `<DynamicForm mode="preview">` into the detail screen; version stepping | 5 | The only way phase 5 gets exercised at all |
| 7 | `TicketTypeEditorScreen` + the four editor components + `schemas.ts` | 6 | See below |
| 8 | The mandatory stale check and the historical-version refusal | 7 | Ships **with** the editor, never after it |
| 9 | The audit pass: greps, behavioural cases, success criteria | 8 | |

**Phase 5 sits after the read-only screens and before the editor, and both halves of that matter.**

*After* the read-only screens, because the renderer is then exercised against real server data —
a `fields` array produced by `TicketTypeMapper.ToDetail` and ordered by `DisplayOrder`
(`TicketTypeMapper.cs:249`) — rather than against an array a builder wrote to match their own
assumptions. A renderer built against hand-made fixtures agrees with the fixtures.

*Before* the editor, because the editor is the only thing that can author a `FieldDescriptor`, and
an author must not be able to compose a descriptor the renderer cannot draw. Building the editor
first produces a catalogue of types that look fine in the editor and render as red placeholders in
the preview, and by then the types are stored and immutable.

**Phase 8 is not a follow-up.** An editor shipped without the stale check silently destroys other
people's work on a `200 OK` (section 9). Do not merge phase 7 without phase 8.

---

## 2. Phase 1 — types and the API wrapper

### 2.1 The renderer's own types

**File:** `frontend/src/shared/dynamicForm/types.ts`

Transcribe `DynamicFormProps`, `FieldDescriptor`, `ChoiceOption`, `FieldValidation`,
`ConditionalVisibility` and `DynamicFormMode` **exactly** as written in
[`../../Screens/TicketTypesScreens.md`](../../Screens/TicketTypesScreens.md) §6.2, comments
included. Do not paraphrase the comments; three of them are the only record of a trap.

> **The C# path in that spec's mirror comment is `ExternalInterfaces/`, not `Application/Dtos/`.**
> `FieldDescriptorDetailDto` is at
> `AccountantApp.Api/Slices/TicketTypes/ExternalInterfaces/TicketTypeDetailDto.cs:46`, and its
> own file comment (lines 3–8) explains why it is in `ExternalInterfaces/` and not in
> `Application/Dtos/`: it is a contract type returned by `ITicketTypesApi` as well as by HTTP.
> An earlier draft of §6.2, and of `GeneralUIArchitecture.md` §2.5's example comment, named the
> `Application/Dtos/` path; both have since been corrected. **Write the
> `ExternalInterfaces/` path in the mirror comment**, so the next person can diff the two files.
> Flagged in section 11.

Property-by-property confirmation against `ExternalInterfaces/TicketTypeDetailDto.cs:46-87`:
`Validation` is `= new()` (line 57), so `validation` is **never** `null` while its members
routinely are; `ChoiceOptions` is `= new()` (line 56), so `choiceOptions` is `[]` and never
`null`; `ConditionalVisibility` is `ConditionalVisibilityDto?` (line 58) and *is* nullable;
`RegexPattern` (line 78) and `AllowedFileTypes` (line 79) are non-nullable with `''`/`[]`
defaults. `MinValue`/`MaxValue` are `decimal?` → JSON **numbers**; `EarliestDate`/`LatestDate`
are `DateOnly?` → `"2026-09-02"` strings; `MaxFileSizeBytes` is `long?`.

`FieldDescriptor` lives in `shared/`, not in the slice, and the slice **re-exports** it. A
`shared/` module may never import from `slices/` (§1.4 rule A).

### 2.2 The slice's types

**File:** `frontend/src/slices/ticketTypes/types.ts`

Six interfaces, each with a mirror comment naming its C# file:

| TypeScript | Mirrors | Notes |
|---|---|---|
| `TicketTypeListItem` | `ExternalInterfaces/TicketTypeListItemDto.cs:6-14` | **Six** properties: `id`, `code`, `displayName`, `category`, `isActive`, `currentVersionNumber`. Nothing else exists on it |
| `TicketTypeDetail` | `ExternalInterfaces/TicketTypeDetailDto.cs:9-44` | Fourteen properties. `versionId: string` — see below. `fields: FieldDescriptor[]` |
| `CreateTicketTypeRequest` | `Application/Dtos/CreateTicketTypeRequestDto.cs:5-14` | Seven properties, `code` included |
| `EditTicketTypeRequest` | `Application/Dtos/EditTicketTypeRequestDto.cs:3-12` | Six properties, **`code` absent** — see §8.2 |
| `ToggleTicketTypeRequest` | `Application/Dtos/ToggleTicketTypeRequestDto.cs:3-7` | `{ ticketTypeId, newIsActive }` |
| `CreateFieldDescriptor` | `Application/Dtos/CreateTicketTypeRequestDto.cs:16-29` | The **request**-side descriptor. Not the same type as `FieldDescriptor` |

**`versionId` is the fourteenth property, and the C# declares it second — immediately after `Id`
(`TicketTypeDetailDto.cs:30`). Mirror it in that position.** A C# `Guid` crosses the wire as a JSON
string, so the TypeScript type is `string`, not anything cleverer:

```ts
export interface TicketTypeDetail {
  id: string;
  versionId: string;      // Guid. TicketTypeDetailDto.cs:30, set by TicketTypeMapper.cs:237
  // ... the other twelve, in the C# file's order
}
```

It is in the interface because the DTO carries it and this mirror is property-for-property — not
because anything asked to see it. It is the id of the specific **version** this response projects,
and it is the value a ticket persists as `tickets.ticket_type_version_id` so that a later edit to
the type cannot change what an already-open ticket asked for. **No TicketTypes screen renders it**:
it is not a list column, not a detail row, not a form control, and the string `versionId` appears
nowhere in the screen spec. So there are two ways to get this wrong and a weaker reading of this
plan hits both. Dropping it because nothing displays it leaves the future `Tickets` slice with no
way to name the version it was handed — `id` and `versionNumber` alone cannot, and the DTO's own
comment (`TicketTypeDetailDto.cs:14-29`) spells out what the two workarounds cost. Putting it on
screen because it is present in the type
invents UI this plan does not specify. Mirror it; do not draw it.

`CreateFieldDescriptor` and `FieldDescriptor` differ and must stay two types. On the request side
`ChoiceOptions`, `Validation` and `ConditionalVisibility` are all nullable
(`CreateTicketTypeRequestDto.cs:26-28`); on the response side the first two are non-nullable.
Collapsing them into one type makes the editor's optional members look mandatory and the
renderer's mandatory members look optional.

Re-export the renderer's type so the rest of the slice has one import site:

```ts
export type { FieldDescriptor, ChoiceOption, FieldValidation } from '../../shared/dynamicForm/types';
```

**File:** `frontend/src/slices/ticketTypes/fieldDataTypes.ts`

The eleven strings from `Slices/TicketTypes/ExternalInterfaces/FieldDataTypes.cs:28-38`, **in that file's
order**, as a `readonly` tuple plus a derived union type. One list, imported by both the editor's
`Select` and the registry's completeness check. Two copies drift, and the drift shows up as a
data type an author can pick and the renderer cannot draw.

### 2.3 The API wrapper

**File:** `frontend/src/slices/ticketTypes/api.ts`

Six exported functions, one per endpoint, named for the endpoint and not for the screen. No
React, no hooks, no TanStack Query — this file exists so it can be read against
`TicketTypesEndpoints.cs` line by line (§2.5).

```ts
// Verbs are read off TicketTypesEndpoints.cs. GeneralUIArchitecture section 2.3 rule C's list of
// POST reads contains no route from this slice; a POST to /list here is a 405.
export function listTicketTypes(p: { pageNumber: number; pageSize: number; activeOnly?: boolean }):
  Promise<PaginatedResponse<TicketTypeListItem>>;                       // GET  /api/ticket-types/list
export function getTicketType(ticketTypeId: string): Promise<TicketTypeDetail>;
                                                                        // GET  /api/ticket-types/detail
export function getTicketTypeVersion(ticketTypeId: string, versionNumber: number):
  Promise<TicketTypeDetail>;                                            // GET  /api/ticket-types/version
export function createTicketType(body: CreateTicketTypeRequest): Promise<TicketTypeDetail>;
export function editTicketType(body: EditTicketTypeRequest): Promise<TicketTypeDetail>;
export function toggleTicketType(body: ToggleTicketTypeRequest): Promise<TicketTypeDetail>;
```

Four rules for the three `GET`s:

**A.** Build every query string with `URLSearchParams`, never template interpolation. A `code`
never reaches a query string here, but `ticketTypeId` does, and an unencoded value is a broken URL
the moment anything upstream hands you a malformed id.

**B.** Omit `activeOnly` from the string when it is `undefined`. Do **not** send `activeOnly=false`
to mean "all". Section 4.2 is the whole reason.

**C.** Never call `getTicketType('')`. Fact 5 of §0.3: the parameter is a non-nullable `Guid` and
an empty value is a `400` from the model binder, whose `title` is framework wording routed through
`AppExceptionMiddleware.cs:31-36` — so the user sees a sentence written by ASP.NET Core. Gate with
`enabled`.

**D.** `create` returns `201` and a `Location` header. `http.ts` returns the parsed body for any
`response.ok`, so `createTicketType` resolves with the full `TicketTypeDetail`. **Do not follow
the `Location` header**: it is a second round trip for data you already hold, and it re-reads
through `ToDetail` a version you were just given (screen spec §1 note 1).

### What this step does NOT do, and why

- **No generated client, and no code generator.** There is no OpenAPI document — punch-list item
  9 — and item 6 is its prerequisite. Hand-written `types.ts` is the only option today
  (§2.6). Do not build a generator as part of this work.
- **No shared `TicketTypeStatus` enum.** `isActive` is a `bool`
  (`TicketTypeListItemDto.cs:12`), not one of the four glossary status strings. Map it at the
  render site (`true → "Active"`, `false → "Inactive"`) and pass the word to `StatusChip`.
- **No `description`, `createdAt`, `updatedAt` or field count on `TicketTypeListItem`.** The list
  DTO carries six properties and no others. Adding a column for any of them means an N+1 of
  `/detail` calls behind a table (screen spec §3.1 rule A).

---

## 3. Phase 2 — query hooks and the route rows

**File:** `frontend/src/slices/ticketTypes/queries.ts`

Three query hooks and three mutation hooks. Screens import hooks; screens never import `api.ts`
(§3.2 rule A).

| Hook | Key | Notes |
|---|---|---|
| `useTicketTypeList(params)` | `['ticketTypes','list',{ pageNumber, pageSize, activeOnly }]` | Through `usePaginatedQuery` |
| `useTicketTypeDetail(id)` | `['ticketTypes','detail', id]` | `enabled: Boolean(id)` |
| `useTicketTypeVersion(id, n)` | `['ticketTypes','version', id, n]` | `staleTime: Infinity` |
| `useCreateTicketType()` | — | `setQueryData(detail)`, invalidate `['ticketTypes','list']` |
| `useEditTicketType()` | — | Same |
| `useToggleTicketType()` | — | Same |

Four rules:

**A. `activeOnly` appears in the list key even when `undefined`**, so the three states of §4.2
cannot share one cache entry (screen spec §1.2). Every filter that changes the response must be
in the key or the screen shows the wrong rows.

**B. A version key is never invalidated by anything.** `ticket_type_versions` rows are immutable
by design; `EditTicketTypeHandler` only ever *adds* a row (`EditTicketTypeHandler.cs:53-56`) and
nothing updates one. `staleTime: Infinity` is correct and an invalidation is a refetch that can
only return the identical bytes.

**C. Every mutation seeds the detail key from its own response and does not refetch** (§3.2 rule
D). All three mutating endpoints return the full `TicketTypeDetailDto`
(`TicketTypesEndpoints.cs:23, 32, 41`), deliberately, so there is no second round trip.

**D. No optimistic updates, in any of the three** (§3.2 rule E; screen spec §8 item 5). There is
no concurrency token, so an optimistic edit is a confident display of a version number that may
not exist. Section 9 is the reason.

**File:** `frontend/src/routes.tsx` — add the four rows already present in
[`../../GeneralUIArchitecture.md`](../../GeneralUIArchitecture.md) §4.1. Two constraints:

1. **`/ticket-types/new` is declared before `/ticket-types/:ticketTypeId`.** Declared after,
   `new` matches the parameterised route, `ticketTypeId` becomes the literal `"new"`, and the
   detail query fires a `400` that reads like a broken link (screen spec §2).
2. The historical view is **`?version=N` on the detail route**, not a fifth route. One screen
   component, one `RequireRole` wrapper, and a bookmarkable URL.

`/ticket-types` and `/ticket-types/:ticketTypeId` are open to all four roles;
`/ticket-types/new` and `/ticket-types/:ticketTypeId/edit` are wrapped in `RequireRole` for
`AccountantAdmin` and `AccountantUser`. `RequireRole` renders `AccessDeniedPage` and does not
redirect (§4.3 rule A), and it is **not** a security boundary (§4.3 rule B) — the server denies
the underlying calls with `403` and audits every denial regardless
(`Shared/Authorization/PermissionChecker.cs:46-63`).

---

## 4. Phase 3 — the ticket type list

**File:** `frontend/src/slices/ticketTypes/screens/TicketTypeListScreen.tsx`
**File:** `frontend/src/slices/ticketTypes/components/ToggleTicketTypeDialog.tsx`

Five columns and no sixth: see screen spec §3.1. `PaginatedTable` (§8.2), never a hand-rolled
`Table` + `TablePagination`. Affordances and their `can()` gates are the table in §3.2.

### 4.1 Render the pager from the response

`PaginatedQuery.Normalize` is
`(Math.Max(pageNumber, 1), Math.Clamp(pageSize <= 0 ? 15 : pageSize, 1, 50))`
(`Shared/Pagination/PaginatedQuery.cs:10-12`). It **clamps and does not reject**: ask for 200 and
you get 50 with a `200 OK`. Render every page boundary from `response.pageSize`, never from the
value sent. Offer no page-size option above 50.

`items: []` is ambiguous. With `totalCount === 0` it is an empty catalogue → `EmptyState`. With
`totalCount > 0` the page ran past the end → the over-run message with *back to the first page*,
not "no results" (§3.3 item 2).

### 4.2 `activeOnly=false` does not mean "include inactive" — verified in the handler

`ListTicketTypesHandler.cs:29-38`:

```
if (IsCustomerSide(user.Role))          # CustomerAdmin or Employee
    query = query.Where(t => t.IsActive)
    if (user.Role == Employee) query = query.Where(t => t.AllowEmployeeToOpen)
else if (req.ActiveOnly.HasValue)
    query = query.Where(t => t.IsActive == req.ActiveOnly.Value)
```

Two facts fall out of the shape of that `else if`, and both are in the code, not inferred:

- For a Customer-side caller, `activeOnly` is **never read at all** — it sits in the `else`
  branch. Sending it does nothing.
- For an Accountant, the predicate is an **equality**, not a relaxation. `true` → active only.
  `false` → **inactive only**. Omitted → both.

So the filter is a three-option control — *All* / *Active* / *Inactive* — where **All omits the
parameter entirely**. A two-state checkbox labelled "Active only" bound straight to the parameter
shows an Accountant nothing but deactivated types when unticked, which is indistinguishable on
screen from a catalogue that failed to load. This is punch-list item **20**, in the *Degrading*
band; the workaround is mandatory and a builder who "simplifies" it to a checkbox reintroduces
the bug.

The control is **hidden** for `CustomerAdmin` and `Employee`, not disabled: the server ignores
the parameter for them, so a visible control that demonstrably does nothing is worse than no
control (§6.2 rule C).

### 4.3 The deactivate confirmation

`ConfirmDialog` (§8.3). Deactivation is reversible, so it is not styled as destructive, but the
dialog must state all four consequences in screen spec §3.3 — Customer-side callers stop seeing
the type **entirely** (`/detail` answers them `404` via
`TicketTypeMapper.ApplyCustomerSideVisibility`, `TicketTypeMapper.cs:33-37`); existing tickets
still render because `/version` deliberately keeps working; nothing is deleted; Accountants keep
it in their *All* and *Inactive* lists, which is the only way back.

Reactivation needs no confirmation. Both paths call `toggle` with `newIsActive` and render from
the **returned** `isActive`, never from what was sent: `ToggleTicketTypeHandler.cs:44-45` returns
early with a `200`, no transaction and no audit entry when the requested state already holds, so
a success response is not evidence that anything moved.

### What this step does NOT do, and why

- **No client-side sort, search, group or category filter.** `/list` accepts three query
  parameters and no search term or sort key. The server orders by `DisplayName, Id` and pages
  *after* ordering (`ListTicketTypesHandler.cs:41`), so sorting one page sorts fifteen rows out of
  two hundred and presents the result as if it were the whole ordering. Punch-list item 21's
  sibling request — a `search` or `category` parameter — is section 13.
- **No `Suspended` chip for a ticket type.** "Suspended" is a Customer and account state in the
  glossary; reusing it here makes two different things wear one colour.
- **No delete, duplicate, import, export or bulk toggle.**
  [`../../../02-AuthorizationMatrix.md`](../../../02-AuthorizationMatrix.md) §5 grants delete to
  **nobody** and there is no endpoint. A *Delete* button that calls nothing is a support ticket;
  one that calls `toggle` is a lie about what happened.

---

## 5. Phase 4 — the detail screen, without the preview

**File:** `frontend/src/slices/ticketTypes/screens/TicketTypeDetailScreen.tsx`
**File:** `frontend/src/slices/ticketTypes/components/VersionBanner.tsx`

Six regions in the order given in screen spec §4.1. Build regions 1–5 now; region 6, the
`<DynamicForm mode="preview">`, lands in phase 6.

`?version=` absent → `useTicketTypeDetail`. Present → `useTicketTypeVersion`. Both return the
same `TicketTypeDetailDto`, so one render path serves both.

### 5.1 `versionNumber` vs `currentVersionNumber`

| Property | Means | Source |
|---|---|---|
| `versionNumber` | the version **these `fields` came from** | `TicketTypeMapper.cs:246` — `version.VersionNumber` |
| `currentVersionNumber` | the **latest version that exists** | `TicketTypeMapper.cs:245` — `type.VersionNumber` |

`/detail` passes `CurrentVersionOf(type)` (`GetTicketTypeHandler.cs:31`), so the two are always
equal there. `/version` passes the requested version (`GetTicketTypeVersionHandler.cs:35-38`), so
`currentVersionNumber` may be higher. `toggle` mints no version, so both are unchanged
(`ToggleTicketTypeHandler.cs:45, 58`). `TicketTypeListItemDto` carries only
`currentVersionNumber` — there is no field set on a list row for a `versionNumber` to describe.

**When the two differ, `VersionBanner` is mandatory**, above everything else, and the historical
view offers **no *Edit* button at all** — only a link to edit the current version. Screen spec
§7.2 gives the copy. The failure without it is silent and total: `/edit` replaces the field set
wholesale from whatever the form holds, so an Accountant who steps back to v1, spots a typo and
presses *Edit* mints v6 containing v1's fields. Four versions of work are reverted, the response
is `200`, and the only visible trace is a version counter that went up by one — which is what a
successful edit looks like.

### 5.2 Version stepping, because there is no version-list endpoint — verified

`TicketTypesEndpoints.cs:12-73` registers exactly six routes. There is no
`/api/ticket-types/versions`, and `GET /version` takes a single `versionNumber`
(`TicketTypesEndpoints.cs:63`). Confirmed by reading the whole file, not inferred from its
absence in the screen spec. This is punch-list item **21**, in the *Degrading* band, and it is
also open question 2 in `GeneralUIArchitecture.md` §13 and §11 of the backend slice plan.

So the screen offers *Previous version* / *Next version* bounded by `1` and
`currentVersionNumber`, both derivable from the response already in hand. Two prohibitions:

**A. Do not fabricate a history list by looping `/version` from `1` to `currentVersionNumber`.**
On a type edited fifty times that is fifty requests on page load, to build a list the server
could return in one.

**B. Do not display a created date for any version other than the one loaded.** `createdAt` on
the detail DTO is `type.CreatedAt` (`TicketTypeMapper.cs:247`) — the *type's* creation, not the
version's. `TicketTypeVersion.CreatedAt` exists in the database
(`20260829_001_CreateTicketTypesSchema.sql:26`) and is **not projected into any DTO**. A "created"
column on a version stepper is a number the API does not send, and item 21's workaround note
warns by name that a builder will be tempted to invent it.

The range `1..currentVersionNumber` is gapless: `EditTicketTypeHandler.cs:51` derives
`next = type.Versions.Max(v => v.VersionNumber) + 1`. Still render a `404` from `/version` as
"That version does not exist" rather than crashing the stepper — a future backfill migration is
not something the client can rule out (screen spec §7.3 rule C).

### 5.3 Two rules about `isVisibleToCustomer` on this screen

**A. Show it as a badge, for Accountants, reading *"Accountant only"***, matching
[`../../../02-AuthorizationMatrix.md`](../../../02-AuthorizationMatrix.md) §5's *"Accountant-only
Field Descriptors"*. Not "hidden" — "hidden" invites the reading that the value is concealed from
Customers but still collected from them, which is the opposite of what happens.

**B. It is never a filter here.** An Accountant's `fields` array already contains every field; a
Customer-side caller's already contains none of the hidden ones
(`TicketTypeMapper.cs:228-230`). And a Customer-side caller must **not** be told that fields were
stripped: no "3 fields not shown" line, no count that disagrees with the rows. The count is
`detail.fields.length`, which is already the caller's truth. Any other total would require a
number the API does not send, and inventing one leaks the existence of what was stripped.

### 5.4 `404` is the designed answer, and never says "forbidden"

Three server paths answer `404` for a row the caller may not see: `ApplyCustomerSideVisibility`
(`TicketTypeMapper.cs:33-37`, used by `/detail`), `ApplyCustomerSideAudience`
(`TicketTypeMapper.cs:39-43`, used by `/version`), and the plain not-found checks. All three
throw `AppException("Ticket type not found.", 404)`.

Render `NotFoundPage` with the wording "Not found", and **never** "forbidden", "denied" or "no
permission" (§2.3 rule J). *"A `403` confirms the row exists."* For a Customer Admin on a
deactivated type, `404` is not a fault — it is the mechanism.

And never `try`/`catch` a `404` into an empty screen. That converts the scoping mechanism into a
blank page (§2.4 item 5).

> **`/version` can succeed where `/detail` returns `404`, for the same type and the same
> caller.** `GetTicketTypeVersionHandler.cs:34` applies the audience check only;
> `GetTicketTypeHandler.cs:30` applies audience **and** `IsActive`. This is correction note T-4,
> applied on purpose, with the reason in `TicketTypeMapper.cs:27-29`. Never use `/version` to
> test whether a type exists, and never fall back from a `404` on `/detail` to a `/version` call
> to "get something to show" — that is precisely the discovery the `404` refused.

---

## 6. Phase 5 — the dynamic form renderer

**File:** `frontend/src/shared/dynamicForm/visibility.ts`
**File:** `frontend/src/shared/dynamicForm/buildZodSchema.ts`
**File:** `frontend/src/shared/dynamicForm/fieldRegistry.tsx`
**File:** `frontend/src/shared/dynamicForm/DynamicForm.tsx`

This plan owns these four files plus `types.ts` from §2.1. Nothing else in the specification
creates them.

Write them in the order listed. `visibility.ts` is a pure function of `(fields, values)` and
depends on nothing; `buildZodSchema.ts` consumes its output as a `Set<string>`; `fieldRegistry`
consumes neither and is pure JSX; `DynamicForm` composes all three. Building `DynamicForm` first
means writing three modules to fit a component instead of a component to fit three testable
modules.

### 6.1 Why it lives in `shared/` — state the reason in the file header

`slices/tickets/` is the renderer's real consumer and does not exist yet
(`GeneralUIArchitecture.md` §0.1). `slices/ticketTypes/` is its only consumer today, through
phase 6's preview. Putting it in `slices/ticketTypes/` would force `slices/tickets/` to import a
component from another slice, which §1.4 rule C forbids — a slice may import only `types.ts` and
`api.ts` from another slice. `shared/` may never import from `slices/` (rule A), so the dependency
cannot be made legal in the other direction either.

The consequence to accept now: **the renderer's props may not mention a ticket, a ticket type, a
role or a session.** It takes `FieldDescriptor[]`, a `mode`, a `values` object and an optional
`onSubmit`, and nothing else. A prop named `ticketId`, `ticketTypeId`, `role` or `isAccountant`
means the file is in the wrong folder — and for `role` in particular it means something worse;
see §6.6.

### 6.2 `visibility.ts`

Implement screen spec §6.5 exactly: the eight-row coercion table, the capped fixed-point loop,
and structural cycle detection.

Three points a builder gets wrong:

**A.** The comparison side is a `VARCHAR(500)` string
(`20260829_001_CreateTicketTypesSchema.sql:54`), always. For a numeric controller, coerce the
**rule** with `Number(value)` and compare numerically — never `String(n)`, because `String(1.50)`
is `"1.5"` and a rule written `"1.50"` would never match a value the user entered as `1.50`.

**B.** `MultipleChoice` uses `array.includes(value)`. Not `===`, not `join()`.

**C.** Cap the loop at `min(fields.length, 32)` passes and stop early when a pass changes
nothing. A cycle is storable: `TicketTypeMapper.cs:193-198` checks only that each `fieldKey`
differs from its own key and exists in the key set, so `A → B, B → A` passes validation intact.
An uncapped fixed-point loop over that is an infinite render.

**D. Self-reference and dangling references need no client defence.**
`TicketTypeMapper.cs:196` rejects both with a `422`, so neither can be stored. A guard for an
impossible state is untested code that hides the real bug if the server check ever regresses
(screen spec §6.5 rule 1).

**E. When a rule cannot be evaluated, render every field involved rather than none.** A cycle, an
unevaluable controller type (`DateRange`, `FileUpload`), or an unknown `dataType` on the
controller: show the fields, `console.warn` the keys, and in `mode="preview"` show an inline
`Alert severity="warning"` — the preview is the only place an author can discover they built one.
An unexpectedly visible field is cosmetic and reportable; an unexpectedly hidden one is a question
nobody was asked, with no evidence anywhere that something was withheld.

### 6.3 `buildZodSchema.ts`

`buildZodSchema(fields: FieldDescriptor[], visibleKeys: Set<string>): ZodObject`. The
`validation`-member-to-Zod mapping is the ten-row table in screen spec §6.4, plus `isRequired`;
implement it from there rather than from memory, and obey its four numbered rules.

Every limit in that table is enforced at `TicketTypeMapper.cs:150-199` and returns a `422`. Mirror
them exactly — a client limit stricter than the server's blocks legitimate input, a looser one
produces the unattributable banner of §7.3 (§9.2).

Two members contribute **no** rule today: `allowedFileTypes` and `maxFileSizeBytes`, because
`FileUpload` is disabled (§6.7). Surface both as help text so an author can confirm they were
stored.

### 6.4 `regexPattern` — user-supplied code executed in the browser

A `regexPattern` is authored by an Accountant, stored in `VARCHAR(500)`
(`20260829_001_CreateTicketTypesSchema.sql:50`), and then **compiled and run in the browsers of
Customer-side users** against values they typed. Two distinct hazards, and the server has
mitigated exactly one of each pair.

**A. It may not compile in JavaScript.** `TicketTypeMapper.ValidateRegexCompiles`
(`TicketTypeMapper.cs:210-224`) proves the pattern compiles in **.NET**. It proves nothing about
JS. .NET accepts inline options `(?i)`, conditionals, atomic groups, balancing groups, `\Z`,
`(?#comments)` and `\p{IsGreek}`; every one throws `SyntaxError` in `new RegExp`. An uncaught
throw happens during render and takes out the **whole** form — every field, including those with
no pattern — leaving a blank region and a console message. So: compile inside `try`/`catch`, once,
in `buildZodSchema`, memoised on the field array; on failure **drop the rule**, keep the field
usable, and `console.warn` naming the key. Never fail closed there — a field whose pattern cannot
compile would otherwise reject every value with no message naming a cause. Do not add the `u`
flag (it makes previously valid patterns throw) and do not add `g` (a stateful `lastIndex` makes
`.test` alternate between `true` and `false` on identical input). Screen spec §6.4 rule 4 has the
exact shape; write it.

**B. It may backtrack catastrophically, and JavaScript has no regex timeout.** The backend author
saw this: `Shared/Validation/UserSuppliedRegex.cs:41` declares
`public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);`, and the class
comment above it (`UserSuppliedRegex.cs:24-28`) names `(a+)+$` and calls it *"a request-side
denial of service on the whole worker process"*. Note the member is `MatchTimeout` and it is
`static readonly`, not a `const` named `RegexMatchTimeout`: that older name is gone, and grepping
`TicketTypeMapper.cs` for the timeout lands only on the note at `TicketTypeMapper.cs:201-205`
recording that it moved to `Shared/` because two slices need the same budget. There is
no `RegExp` equivalent in any browser. A pattern that hangs the thread hangs **the tab**, with no
error, no recovery and nothing in the console.

The plan does three concrete things about it, and claims nothing more:

1. **The pattern never runs on a keystroke.** Every form in the app is `mode: 'onBlur'` (§9.3
   rule A), so the resolver runs on blur and on submit. That bounds evaluations to roughly one per
   field visit instead of one per character. It does not make any single evaluation safe.
2. **The pattern is never applied to an unbounded input.** Export
   `const REGEX_INPUT_CEILING = 4096;` and apply the pattern through a `superRefine` that first
   checks length. A value longer than the ceiling **fails** with *"This value is too long to
   check against the required format."* — it does not silently skip the rule. Failing closed on
   length is safe; skipping would accept unvalidated input, and running is what hangs the tab.
   Catastrophic backtracking is exponential in input length, so a hard ceiling is the only bound
   available without a timeout.
3. **No "test your pattern" affordance in the editor.** It would run an author-supplied pattern
   against author-supplied input, in the author's tab, with no timeout. Out of scope for this
   pass; section 13.

> **The ceiling is a third deliberate exception to the mirror-exactly rule of §9.2**, alongside
> `helpText` (§8.6) and the `key` trim (§8.5). The server stores a value of any length in a `TEXT`
> column and would accept a 10,000-character answer that this client refuses to pattern-check.
> The exception is stated rather than hidden, and the proper fix — a server-side complexity or
> length bound on patterned fields, or running validation in a terminable Web Worker — is flagged
> in section 13. Do not remove the ceiling on the grounds that it is stricter than the server.

### 6.5 `fieldRegistry.tsx`

One `Record<string, FieldRenderer>` over the eleven strings in
`Slices/TicketTypes/ExternalInterfaces/FieldDataTypes.cs:28-38`, in that file's order. **A registry, not a chain
of `if`s** — a registry can be enumerated in a check against the eleven-string list from §2.2 and
a chain cannot.

Take the `dataType`-to-control mapping from the eleven-row table in screen spec §6.3 rather than
inventing controls. Four entries there are decisions, not preferences, and each states its
failure mode: `YesNo` is a `RadioGroup` and **not** a `Checkbox`, because a checkbox cannot
represent "not answered"; `DateRange` is **two** `DatePicker`s in one fieldset, because
`DateRangePicker` is in the MUI **Pro** package and is not a locked dependency (§1.5);
`MoneyAmount` carries **no** currency symbol, because there is no currency in the schema
(`min_value NUMERIC(18,4)` and nothing else); `MultipleChoice` is a `FormGroup` of `Checkbox`es
and never a native multi-`select`, which is unusable on touch.

**An unrecognised `dataType` renders a visible error placeholder and nothing else** — an
`Alert severity="error"` naming the field label, the key and the unrecognised value verbatim. It
occupies the field's position and contributes **no** Zod rule and **no** value. Do not skip it
silently and do not fall back to `SingleLineText`: a silently skipped `isRequired` field produces
a schema whose required key can never be satisfied, so *Submit* fails against a control that is
not on screen. A text fallback is worse — it collects a string where a number or date was
specified, and the wrongness surfaces in whatever consumes the Field Value.

This is reachable without a deployment mismatch: `TicketTypeMapper.cs:162` validates `DataType`
against `FieldDataTypes.All`, so a twelfth data type added on the server ships to a browser
holding an older bundle.

### 6.6 What the renderer must NOT do — the negative-space rule that matters most

**A. The renderer must not filter on `isVisibleToCustomer`.**

The server already does it. `TicketTypeMapper.cs:228-230`:

```
var fields = version.FieldDescriptors.AsEnumerable();
if (IsCustomerSide(callerRole))
    fields = fields.Where(f => f.IsVisibleToCustomer);
```

Every read path goes through `ToDetail` — `/detail`, `/version`, and both `ITicketTypesApi`
methods. So `fields` is **already the caller's complete list** and there is nothing left to
filter.

**The failure mode, stated as the rule's reason.** If that `Where` is ever removed — by a
refactor, by a second mapper, by a new read path, exactly as correction note T-12 records
happening in `ExternalInterfaces/TicketTypesApi.cs` — then *with* a client filter in place an
Employee's browser **receives** every Accountant-only field key, label and help text over the
wire and quietly declines to draw them. The leak is complete: it is in the response body, in the
network tab, in the TanStack Query cache, and in any error report that serialises it. Nothing
looks wrong on screen, so nobody reports anything, and the regression survives indefinitely.
*Without* the client filter the same regression is a screen full of fields that visibly should not
be there — noticed in minutes, by the first Employee who looks.

**A client-side duplicate of a server-side security filter is not defence in depth here. It is a
mute button on the alarm.**
[`../../../02-AuthorizationMatrix.md`](../../../02-AuthorizationMatrix.md):311 is explicit:
*"Never rely on the React app to hide data. Internal Notes, Accountant-only fields, and
out-of-scope records must be **absent from the API response**, not merely unrendered."*

Concretely: `isVisibleToCustomer` may appear in `frontend/src/slices/ticketTypes/` — it is a
`Switch` in the editor and a badge on the detail screen, both Accountant-facing and both about the
author's *intent*. It must not appear anywhere in `frontend/src/shared/dynamicForm/` except in the
`FieldDescriptor` interface and its comment. Phase 9 greps for exactly that.

**B. Six more prohibitions, each already done server-side.**

| Must NOT | Already done by | Why a client copy is worse than nothing |
|---|---|---|
| Hide or grey a deactivated type | `IsDiscoverableBy` → `404` (`TicketTypeMapper.cs:24-25`) | The type is already absent; a client check has nothing to act on except a `200` that should not have arrived |
| Check `allowEmployeeToOpen` | `IsInAudienceOf` → `404` (`TicketTypeMapper.cs:30-31`) | Same |
| Take a `role` prop or read `useSession` | — | `shared/` may not import a slice, and a renderer that knows the role is a renderer somebody will add a filter to |
| Re-check any authorization rule | `PermissionChecker`, fail-closed and audited (`PermissionChecker.cs:39-63`) | `can()` decides affordances, never data (§6.2 rule A) |
| "Repair" a dangling `conditionalVisibility` by dropping the field | `TicketTypeMapper.cs:196` → `422` | A field removed by client-side repair is a question nobody was asked |
| Deduplicate `key`s | `TicketTypeMapper.cs:160` + `UNIQUE(ticket_type_version_id, key)` | Duplicates cannot be stored; deduping hides it if they ever are |

**C. The renderer holds no server state and issues no requests.** No `useQuery`, no import from
`shared/api/`, no `fetch`. It is a pure function of its props. `SingleChoice` options come from
`choiceOptions` on the descriptor it was handed — there is no options endpoint, and adding one
would put a network call inside a component that renders once per field.

### 6.7 `FileUpload` renders disabled, with an explanatory note

`Documents` is built and registered (`Program.cs:59`) and *by design never exposes HTTP routes of its
own*; `Tickets` owns `/api/documents/*` (§0.1, §12 item 3), and all four routes exist today —
`/upload`, `/list`, `/download`, `/delete`, at `Slices/Tickets/TicketsEndpoints.cs:250-356`. The
upload endpoint and the download endpoint are therefore **not** what is missing. What is missing is
on this side of the wire: there is no `Tickets` UI plan, no `UI/Screens/TicketsScreens.md`, no
client route and no ticket form anywhere in this specification, so there is no ticket-submit path
and **no ticket id to reference** — and `/api/documents/upload` takes one.

So `FileUpload` renders an outlined region with the field label, a **disabled** *Choose file*
button, and the sentence *"File uploads are not available yet."* And:

- it contributes **no** Zod rule, **not even `isRequired`**. A required-but-impossible field
  would make every ticket of that type unsubmittable the moment a `Tickets` **UI** ships;
- it submits `null`, always;
- `allowedFileTypes` and `maxFileSizeBytes` appear as help text, formatted
  (`"PDF, JPG, PNG, up to 5 MB"`) and not raw, so an author can confirm the rules were stored;
- it is **not omitted**. §12 item 3 is explicit: *"a ticket type author can define a file field
  today and needs to see it."* Omitting it makes a field the author created invisible, and they
  will add it a second time.

### 6.8 Cross-cutting rules for `DynamicForm.tsx`

**A. `groupName` and `displayOrder` drive layout, per screen spec §6.6.** Fields with
`groupName === ''` form the leading unnamed group with **no heading and no card**; named groups
follow, ordered by the minimum `displayOrder` among their members, tie-broken by `groupName`
case-insensitively. Within a group sort by `displayOrder` **then by `key`** — `display_order` has
no uniqueness constraint (`20260829_001_CreateTicketTypesSchema.sql:39`) and
`ToDetail`'s `fields.OrderBy(f => f.DisplayOrder)` (`TicketTypeMapper.cs:249`) is a stable sort,
so ties keep whatever order the descriptor rows arrived in, which can differ between a fresh
fetch and a cache read. `key` is unique per version, so `displayOrder` then `key` is total and
deterministic. `groupName` is compared **exactly**: `"Bank Details"` and `"bank details"` are two
groups, because the server trims it and does not case-fold it
(`TicketTypeMapper.cs:122`). A group with no visible members renders **nothing**.

**B. Never mutate the array you were given.** `fields.sort(...)` sorts in place, and the array is
the one inside the TanStack Query cache entry — so sorting it silently reorders what the detail
screen's fields table sees, with no state change to explain it. Derive with
`[...fields].sort(...)` inside a `useMemo`.

**C. The RHF field name is an index alias, never the `key`.** React Hook Form parses `.` and `[`
in a name as path syntax, and the server accepts **any** non-blank string of ≤100 characters as a
`key`: `TicketTypeMapper.cs:158` checks blankness and length and nothing about the character set.
A key `salary.amount` therefore becomes nested state `{ salary: { amount } }` and submits under
the wrong shape, with nothing erroring. So `DynamicForm` builds an `alias → key` map once (`f0`,
`f1`, … by index), registers the aliases, looks errors up by alias, and translates back when
assembling the submitted object. Do **not** solve this by constraining keys in the renderer — a
client limit stricter than the server's is forbidden by §9.2. This is punch-list item **19**, in
the *Degrading* band, and this indirection is why the renderer looks roundabout where a direct
`register(field.key)` would read better.

**D. A hidden field contributes no rule and submits no key.** `buildZodSchema(fields,
visibleKeys)` **omits hidden fields from the schema entirely**, recomputed and memoised on
`[fields, visibleKeys]`, and the submitted object is built from the visible set only — a hidden
field's key is **absent**, not `null`. Present-and-null and absent are different answers: the
first says "asked, not answered", the second says "not asked", and only the second is true.

**E. A field that becomes visible again keeps whatever was typed into it.** Do not clear values
on hide. Clearing on hide plus omitting on submit means a mis-click destroys a sentence with no
undo.

**F. `helpText` goes in `helperText`, and an error replaces it.**
`helperText={error?.message ?? (field.helpText || undefined)}` with `error={Boolean(error)}`. MUI
has one slot; rendering `helpText` unconditionally means a validation message either never
appears or appears somewhere else, and a message the user cannot find is the same as none.

**G. `isRequired` drives both the asterisk and the Zod rule.** MUI's `required` prop only draws
the asterisk. One without the other is either a form that accepts a blank required answer or one
that rejects it with no visual cue that it was mandatory.

**H. `mode="read"` renders values as text, not as disabled inputs**, and is built now even though
nothing calls it. It is three lines per renderer; retrofitting it later means touching all eleven.

**I. An untouched optional field submits `null`, never `''`, `[]` or `NaN`** (§9.3 rule F).
`TextField type="number"` yields `''` when cleared, and `Number('')` is `0` — a zero the user
never typed and cannot be distinguished from one they did. Register numbers with `valueAsNumber`
and keep the raw number in state; format only for `mode="read"`.

**J. Every control has a real `<label>`** via MUI's `label=` prop (§8.4 item 1). For `DateRange`
and `MultipleChoice`, which are several inputs, the group label is a `FormLabel` inside a
`FormControl` — otherwise a screen reader announces two unlabelled date boxes.

### 6.9 Six ways the renderer goes wrong

1. **Building the schema once and visibility separately.** The worst bug available here. A hidden
   `isRequired` field contributes a required key, the resolver fails, `handleSubmit` never calls
   `onSubmit`, and the error attaches to a control that is not rendered. *Submit* does nothing at
   all: no request, no banner, no red outline, nothing in the console. The user presses it
   repeatedly. It is unreportable, because from their side the button is simply broken. Rule D is
   the structural fix.
2. **`if (field.validation)`.** Always `true` — `Validation` is `= new()`
   (`TicketTypeDetailDto.cs:57`). It proves nothing. Test each member.
3. **Treating `''` and `[]` as rules.** `new RegExp('')` matches every string, which is harmless;
   `allowedFileTypes: []` treated as a whitelist rejects every file. Check for emptiness first.
4. **Compiling the pattern inside a `.refine`.** It then recompiles on every validation pass,
   and an uncaught `SyntaxError` there blanks the form. §6.4.
5. **`fields.sort(...)`.** Mutates the query cache. Rule B.
6. **`register(field.key)`.** A key containing `.` or `[` reshapes the payload silently. Rule C.

### What this phase does NOT do, and why

- **No submit path.** There is no ticket endpoint. The preview submits nowhere and must not grow
  a *Submit* button (screen spec §8 item 9).
- **No `FileUpload` implementation.** §6.7.
- **No `role`, `session`, `ticketId` or `ticketTypeId` prop.** §6.1 and §6.6.
- **No "retired type" interstitial.** `Tickets` will read schemas through `ITicketTypesApi`, which
  applies the audience check only (`TicketTypesApi.cs:31-39`), so the renderer must be usable from
  a descriptor array whose type is deactivated. Whether to show a retirement notice is the
  *ticket* screen's decision, made once `Tickets` exists.

---

## 7. Phase 6 — wire the preview in

Add region 6 to `TicketTypeDetailScreen`: `<DynamicForm mode="preview" fields={detail.fields} />`.

This is the only place the renderer is exercised before `Tickets` exists, and therefore the only
way phase 5 gets tested at all in this pass. **The preview is read-only-*ish*, not disabled**:
`mode="preview"` renders live, focusable controls with no submit button, because a disabled form
cannot demonstrate that a `conditionalVisibility` rule works — and that is the single thing an
author most needs to check before saving. Nothing typed into it is persisted or read back.

Also land version stepping here (§5.2), now that both the banner and the preview exist, so an
author can step to v1 and see v1's form.

---

## 8. Phase 7 — the field-descriptor editor

**File:** `frontend/src/slices/ticketTypes/schemas.ts`
**File:** `frontend/src/slices/ticketTypes/screens/TicketTypeEditorScreen.tsx`
**File:** `frontend/src/slices/ticketTypes/components/FieldDescriptorEditor.tsx`
**File:** `frontend/src/slices/ticketTypes/components/ChoiceOptionsEditor.tsx`
**File:** `frontend/src/slices/ticketTypes/components/ValidationRulesEditor.tsx`
**File:** `frontend/src/slices/ticketTypes/components/ConditionalVisibilityEditor.tsx`

Write `schemas.ts` first, then the four components bottom-up, then the screen. One screen
component, two modes, chosen from `useParams().ticketTypeId`. React Hook Form with
`useFieldArray` for `fields`, `zodResolver`, `mode: 'onBlur'` (§9.3 rule A).

The type-level controls are the table in screen spec §5.3; the per-field controls are §5.4. The
Zod limits are the fifteen-row table in §5.5 rule F, every one of which is
`TicketTypeMapper.cs:126-199`. Do not copy those tables here; implement from them.

### 8.1 `Code` is immutable and absent from the edit DTO — verified in the handler

`EditTicketTypeRequestDto.cs:3-12` declares six properties and **no `Code`**. And
`EditTicketTypeHandler.cs:41` calls
`TicketTypeMapper.ValidateTicketType(string.Empty, req.DisplayName, req.Category)` under the
comment *"Code is immutable, so only the editable strings are re-checked"* — it passes an empty
string where the code would go, which `ValidateTicketType` length-checks and never blank-checks
(`TicketTypeMapper.cs:128`). Nothing in the edit path can write `Code`.

| Mode | Control |
|---|---|
| Create | editable `TextField`, required non-blank, ≤100, trimmed |
| Edit | **read-only** labelled value with a lock icon and the note *"A ticket type's code never changes."* |

**Do not include `code` in the edit form's Zod schema or its submitted object.** A `TextField`
bound to a value the server throws away is a control that accepts an edit, reports success, and
shows the old value again once the cache is seeded from the response — the user concludes the save
silently failed. The server will not help: an unknown JSON property is ignored by
`System.Text.Json`'s default binding, so there is no `400` to catch.

On create, `code` uniqueness is **case-insensitive**: `CreateTicketTypeHandler.cs:45` compares
`t.Code.ToLower() == req.Code.ToLower()`, and `idx_ticket_types_code_lower`
(`20260829_001_CreateTicketTypesSchema.sql:18`) is a unique index on `LOWER(code)` that catches
the race the pre-check cannot. Render the `409` verbatim:
`"A Ticket Type with this code already exists"` — note the absent full stop
(`CreateTicketTypeHandler.cs:17`); do not add one and do not paraphrase (§7.3 item 2).

### 8.2 `Fields` is a full replacement that mints a new version — verified in the handler

`EditTicketTypeHandler.cs:51-56` builds a **new** `TicketTypeVersion` numbered
`Max(v.VersionNumber) + 1` and populates `version.FieldDescriptors` from `req.Fields` and nothing
else. It never reads the previous version's descriptors.

Two consequences the editor must obey:

**A. Load the current version's complete `fields` array into form state and submit all of it,
every time.** Submit four of five fields and the fifth is gone from v-next, with a `200 OK` and no
warning anywhere. Never build the payload from RHF's `dirtyFields`, never omit a row the user did
not touch, and never lazy-load field rows behind an accordion that has not been opened.

**B. Every save is a new version, including one that changed nothing.** There is no no-op path:
`/edit` has no early return, unlike `toggle`. Pressing *Save* twice produces v4 and v5 with
identical descriptors. So the success snackbar must name the version — *"Saved as version 4."* —
because silent success on an operation that increments a counter is how a catalogue reaches v30 by
accident. Disable *Save* only while the mutation is pending (§9.3 rule B).

### 8.3 A choice field needs ≥2 options; a non-choice field must have none — verified

`TicketTypeMapper.cs:180-184`:

```
isChoice = field.DataType is "SingleChoice" or "MultipleChoice"
if (isChoice && (ChoiceOptions?.Count ?? 0) < 2)  -> 422 "Choice field 'x' requires at least two options."
if (!isChoice && ChoiceOptions is { Count: > 0 }) -> 422 "Non-choice field 'x' cannot have choice options."
```

Both directions, both `422`. The second is the one that bites, so make the data-type `Select` an
explicit state transition rather than a plain field:

- changing **away from** a choice type **clears `choiceOptions`** — otherwise the row still
  carries the two options it had and the save fails with a message naming a field the user just
  fixed;
- changing **to** a choice type seeds two blank option rows;
- changing the data type at all **clears every `validation` member the new type cannot use**.
  `minValue` left behind on a `SingleLineText` field is stored — `ValidateFields` never
  cross-checks a validation member against the data type — is meaningless, and will be applied by
  a future renderer that trusts it.

Block a one-option choice field in Zod. Block a zero-field type in Zod too:
`TicketTypeMapper.cs:152-153` returns `422 "At least one field is required."`, and a user who
composes a nine-field type, deletes them all by mistake and learns from a banner has lost the
work.

### 8.4 Trim `key` client-side — it is the only guard that exists

`TicketTypeMapper.NormalizeFields` (`TicketTypeMapper.cs:117-124`) trims exactly `Label` and
`GroupName`. `NormalizeTicketType` additionally trims `Code`, `DisplayName` and `Category`
(lines 101-103, 112-113). **`Key` is trimmed nowhere.**

`ValidateFields` rejects a whitespace-only key via `IsNullOrWhiteSpace`
(`TicketTypeMapper.cs:158`), but `" key "` passes, and the uniqueness set is
`new HashSet<string>(StringComparer.OrdinalIgnoreCase)` (line 155) — case-insensitive and
whitespace-**sensitive**. So `"key"` and `"key "` are two distinct fields in one version, both
accepted, both stored, indistinguishable in the editor's field list, and the second unreachable by
any `conditionalVisibility.fieldKey` a human would type, because that check is `keys.Contains`
(line 195) and is whitespace-sensitive too.

**Trim every `key` before submit, and enforce case-insensitive uniqueness in Zod across the whole
`fields` array.** This is not cosmetic tidying; it is the only thing preventing the bug. Trim
`helpText` and every choice-option `label` and `value` as well (§9.3 rule E).

Do **not** impose a character pattern on `key` client-side. Punch-list item **19**, in the
*Degrading* band, covers both halves — the missing trim and the missing character-set check — and
its workaround section is explicit that the client trims and does **not** constrain characters,
because §9.2 forbids a client limit stricter than the server's. The RHF path-syntax hazard is
handled by the alias map instead (§6.8 rule C).

### 8.5 `helpText` — a declared limit the server never enforces

`TicketTypeMapper.cs:94` declares `private const int HelpTextMaxLength = 10_000;`. **No validator
reads it.** Confirmed by grepping the whole slice and the test project: the constant appears once,
at its declaration, and nowhere else. `ValidateDescription` uses `DescriptionMaxLength`
(line 136), and `ValidateFields` length-checks `label`, `groupName`, `regexPattern`, both
`conditionalVisibility` members and the joined `allowedFileTypes` (lines 165-176) — never
`helpText`. The column is `help_text TEXT NOT NULL`
(`20260829_001_CreateTicketTypesSchema.sql:37`), which PostgreSQL does not bound, and nothing in
this system is ever purged.

**Cap it at 10,000 in Zod anyway.** This is a deliberate exception to the mirror-exactly rule of
§9.2 and is stated as such there: a client cap on a limit the server forgot costs nothing when no
legitimate help text approaches 10,000 characters, and the alternative is a field with no ceiling
at all. Punch-list item **24**, filed under *Drift* precisely because the UI is unaffected —
correction note T-11 records the intent (*"unbounded input on a table nothing ever purges is still
a mistake — cap it explicitly"*) and the call was never added.

### 8.6 `conditionalVisibility` needs two controls, and the value one is not a text box

`TicketTypeMapper.cs:193-198` rejects a self-reference and a dangling reference with `422`, so a
free-text field key is a guaranteed round trip for a typo the client could have prevented. The
field `Select` therefore offers **only the other rows currently in the form**.

The value control depends on the **referenced** field's data type, per the four-row table in
screen spec §5.5 rule D: `YesNo` → a `Select` of exactly `"true"`/`"false"`; a choice type → a
`Select` over the referenced field's option **`value`s**, never its labels; anything else → a
`TextField` capped at 500 (`ConditionalValueMaxLength`, `TicketTypeMapper.cs:89`).

A free-text value box here is the highest-yield authoring mistake in the slice: an author types
`Yes` against a `YesNo` field, the server accepts it — `ValidateFields` validates the *reference*,
never the *value* — and the dependent field never appears for anybody, forever, with no error on
any screen.

### 8.7 Reordering renumbers `displayOrder` densely from 0

Move-up/move-down buttons. On every change, rewrite every row's `displayOrder` to its array index.
Array position is not persisted anywhere: `display_order` is the only ordering the server stores,
`ToEntity` copies it verbatim (`TicketTypeMapper.cs:58`), and `ToDetail` re-sorts by it on the way
out (line 249). A reorder that leaves the old numbers renders in the new order until reload and in
the old order afterwards.

### 8.8 Two edit-DTO defaults that silently flip a flag

`EditTicketTypeRequestDto.cs:9-10` declares `AllowEmployeeToOpen` and
`AllowSubjectOtherThanCreator` with **no initialiser**, so both default to `false`.
`CreateTicketTypeRequestDto.cs:11-12` declares the same two properties with `= true`.

So an edit payload that **omits** either flag turns it **off**, while a create payload that omits
it leaves it **on**. Turning `allowEmployeeToOpen` off hides the type from every Employee's list
and returns `404` on their reads (`ListTicketTypesHandler.cs:32-33`;
`TicketTypeMapper.cs:30-31`) — a whole role loses a whole type, from a property nobody typed.

**Always send both booleans explicitly on both routes.** Never build the request object by
spreading only dirty fields, and never let a `Switch` bound to `undefined` reach `JSON.stringify`,
which drops the key.

### 8.9 Send `''`, not `null`, for the four non-nullable strings

`GeneralUIArchitecture.md` §9.3 rule F says *"Send `null`, not `''`, for an untouched optional
field."* **In this slice, four properties are an exception, and the failure is a `500`.**

`description`, `helpText`, `groupName` and `label` are all non-nullable `string` in C# with
`= string.Empty` defaults. Three of the four columns are `NOT NULL DEFAULT ''`
(`20260829_001_CreateTicketTypesSchema.sql:5, 37, 40`); `label` is `VARCHAR(255) NOT NULL` with
**no** default (`:36`), which changes nothing here, because the unconditional `.Trim()` below
throws long before any column default could have applied. Nullable reference types are not
enforced at runtime, so `System.Text.Json` will happily assign `null`, and then:

- `NormalizeFields` calls `field.Label.Trim()` and `field.GroupName.Trim()` unconditionally
  (`TicketTypeMapper.cs:121-122`), and `NormalizeTicketType` calls `.Trim()` on `DisplayName` and
  `Category` (lines 112-113). A `null` there is a `NullReferenceException`, caught by
  `AppExceptionMiddleware.cs:37-43` and returned as a bare `500 "An unexpected error occurred."`
- `description` and `helpText` survive normalisation and reach a `NOT NULL` column, which is a
  `DbUpdateException` and also a `500`.

So the Zod schema must default all four to `''` and the request builder must never emit `null` for
them. Rule F still holds for the genuinely nullable members —
`choiceOptions`, `validation`, `conditionalVisibility` and the seven **nullable** members of
`FieldValidationDto` are `?` on the request side (`CreateTicketTypeRequestDto.cs:26-28`;
`TicketTypeDetailDto.cs:72-77, 80`) and `null` is the correct way to say "no rule". The two
exceptions inside `FieldValidationDto` are `RegexPattern` (line 78) and `AllowedFileTypes`
(line 79), which are non-nullable with `''` and `[]` defaults exactly like the four above — §2.1
states this too, and the two sections must not disagree. Flagged in
section 13.

### 8.10 Seven ways the editor goes wrong

1. **Submitting only dirty fields.** §8.2 rule A. The untouched rows vanish from the new version.
2. **A `code` field in edit mode.** §8.1. Accepts an edit, reports success, shows the old value.
3. **Omitting the two booleans.** §8.8. A flag flips with nobody having touched it.
4. **Sending `null` for `description` or `helpText`.** §8.9. A `500` on a valid-looking form.
5. **Leaving `choiceOptions` behind after a data-type change.** §8.3. The `422` names a field the
   user just fixed.
6. **A free-text `conditionalVisibility` value box.** §8.6. Accepted, stored, and permanently
   unsatisfiable.
7. **Mapping a `422` onto a field.** The messages *do* name the field key
   (`"Duplicate field key 'x'."`, `TicketTypeMapper.cs:161`), and matching that string to
   highlight a row is exactly the heuristic §7.3 forbids. Punch-list item 5 is why there is no
   machine-readable alternative. Render the `title` verbatim in a form-level banner above *Save*,
   and if you reach one of these messages at all, **the corresponding Zod rule is missing — add
   it**; the banner is not the fix.

Never reset the form on error (§9.3 rule D). The user's twelve field rows must survive a `422`
and a `409`.

> **A `422` can precede a `404` on `/edit`.** `EditTicketTypeHandler.cs:38-49` runs
> `RequireAsync`, then normalise, then all three validators, and **only then** loads the type and
> throws `404` if it is missing. So editing a deleted-or-invisible type with an invalid payload
> returns `422`, not `404`. Do not treat a `422` as proof that the type exists.

---

## 9. Phase 8 — the mandatory stale check

### 9.1 There is no optimistic concurrency anywhere in the built backend

No row-version column, no `ETag`, no `If-Match`, no `DbUpdateConcurrencyException` handling.
Verified: `ticket_types` has eleven columns and none of them is a concurrency token
(`20260829_001_CreateTicketTypesSchema.sql:1-13`), and `EditTicketTypeHandler` reads the type,
mutates it and saves with no version predicate (`EditTicketTypeHandler.cs:46-68`).

The single `409` this slice can produce is a **unique-constraint race**, not a lost-update check:
`EditTicketTypeHandler.cs:70-73` catches `PostgresException { SqlState: "23505" }` and rethrows
`"This ticket type was edited by someone else. Reload and try again."` That only fires when two
writes contend for the same `(ticket_type_id, version_number)` index slot
(`20260829_001_CreateTicketTypesSchema.sql:27`) — genuinely simultaneous saves.

**Sequential saves from stale forms slip through, and the loss is undetectable.** Two Accountants
open v3. Each adds a different field. The first save mints v4. The second computes
`Max(VersionNumber) + 1 = 5` from a freshly loaded type and mints v5 **from the stale form's field
list**, so the first Accountant's field is not in v5. Both callers receive `200 OK`. The database
is consistent. The audit log records `TicketTypeVersionCreated` twice
(`EditTicketTypeHandler.cs:75-79`), which is exactly what two legitimate edits look like. The work
is gone and nothing anywhere reports it.

> **`/edit` can return a `409` and does not declare one.** `TicketTypesEndpoints.cs:31-35`
> declares `403`, `404` and `422` only, while `EditTicketTypeHandler.cs:72` throws `409`.
> `/create` declares `409` correctly (line 25). Harmless to the client, which branches on
> `response.status` and not on declared metadata, but it means a generated client (punch-list
> item 9) would not know the status exists. Flagged in section 11.

The only signal available is `TicketTypeDetailDto`, which carries **both** `versionNumber` and
`currentVersionNumber` (`TicketTypeDetailDto.cs:39-40`). Comparing `currentVersionNumber` before
submit is therefore the only available signal and is **mandatory** — `GeneralUIArchitecture.md`
§9.4 and screen spec §5.6 both require it, and this is the screen §9.4 was written for. The proper
fix is a `version` column and a `409` on mismatch: punch-list item **7**, in the *Degrading* band.

### 9.2 The implementation

Edit mode only. Create has nothing to be stale against, and `toggle` writes no version and cannot
lose a field.

```ts
// TicketTypeEditorScreen.tsx, edit mode only.
// Recorded when the form is FIRST populated, and never updated on re-render.
const loadedVersionRef = useRef<number | null>(null);

async function onSubmit(values: EditTicketTypeFormValues) {
  // Step 2 of GeneralUIArchitecture section 9.4: re-read immediately before writing.
  // fetchQuery, not invalidateQueries -- we need the value here and now, not a background refresh.
  const latest = await queryClient.fetchQuery({
    queryKey: ['ticketTypes', 'detail', ticketTypeId],
    queryFn: () => getTicketType(ticketTypeId),
  });

  if (latest.currentVersionNumber !== loadedVersionRef.current) {
    // Step 3: do NOT submit. Submitting would replace their version's fields with ours.
    // This narrows the race; it does not close it. See section 9.3 and punch-list item 7.
    setStaleConflict(latest);
    return;
  }

  await editMutation.mutateAsync(toEditRequest(values));
}
```

The blocking banner must:

1. **State both version numbers** — *"You are editing version 3. Version 5 now exists."*
2. **Summarise what moved** — `displayName`, `category`, the two flags, and the field-key set
   added and removed, all computable by diffing `latest.fields` against the loaded array.
3. **Offer exactly two buttons**: *Reload and discard my changes*, and *Keep editing* (which
   leaves submit blocked).
4. **Not offer *Save anyway*.** There is no merge, and the entire content of the losing save is
   the other person's work.
5. **Say, in its own copy and in a code comment, that this is a mitigation with an open race and
   not a fix.** Between the `fetchQuery` and the `POST` another Accountant can still save, and
   both callers still receive `200`. Do not skip the mitigation because it is imperfect, and do
   not present it to a user as making the problem go away.

### 9.3 The editor must refuse to load from a historical version

If the editor is reached with a detail whose `versionNumber !== currentVersionNumber` — via a
`?version=` on the URL, a stale cache entry, or an *Edit* link on the historical detail view —
render a blocking banner and **no submit button**, offering only *Edit the current version*.
Otherwise the mass-revert of §5.1 happens on a `200 OK`, from the editor rather than from a link.

---

## 10. Phase 9 — the audit pass

Four greps and one diff. A future builder runs each; none has been run.

```bash
grep -rn "isVisibleToCustomer" frontend/src/shared/          # only the FieldDescriptor interface + comment
grep -rn "role\|useSession" frontend/src/shared/dynamicForm/ # nothing
grep -rn "fetch(" frontend/src/slices/ticketTypes/           # nothing; fetch lives only in shared/api/http.ts
grep -rniE "tickettypes/|ticketTypes/list" frontend/src/slices/ticketTypes/api.ts   # nothing: kebab-case only
```

Then diff the five `TicketTypes` rows in `shared/permissions/can.ts` against
`Slices/TicketTypes/TicketTypesActionCatalogue.cs:13-19`. Verified for this plan:
`CreateTicketType`, `EditTicketType`, `ToggleTicketType` are `[AccountantAdmin, AccountantUser]`;
`ReadTicketType` and `ListTicketTypes` are all four roles. Same names, same role sets, no extras
on either side. Do not add a sixth row.

Then walk the twenty-nine behavioural cases in screen spec §9. They are checked in a browser
against a running API; a passing type-check proves none of them.

---

## 11. Spec-vs-code drift found while writing this plan

Recorded, not smoothed over. None of it changes a step above; each is a thing the next reader
would otherwise be misled by.

> **1. The mirror path for `FieldDescriptorDetailDto` moved, and two documents had to be corrected
> to follow it.** `Screens/TicketTypesScreens.md` §6.2 and `GeneralUIArchitecture.md` §2.5 once
> named `Slices/TicketTypes/Application/Dtos/TicketTypeDetailDto.cs`; both now name the right
> path. The type is at
> `Slices/TicketTypes/ExternalInterfaces/TicketTypeDetailDto.cs:46`, and that file's own comment
> (lines 3–8) explains why. Write the `ExternalInterfaces/` path in the mirror comments. §2.1.

> **2. `POST /api/ticket-types/edit` can return `409` and declares only `403`, `404`, `422`.**
> `TicketTypesEndpoints.cs:31-35` versus `EditTicketTypeHandler.cs:70-73`. `/create` declares its
> `409` (line 25). No client impact; a generated client would miss the status. §9.1.

> **3. The `403` body names an internal action string, which §7.1 says the user must not see.**
> `PermissionChecker.cs:63` throws
> `AppException($"Permission denied for action '{action}'.", 403)`, so `ApiError.title` is
> `"Permission denied for action 'EditTicketType'."` `GeneralUIArchitecture.md` §7.1 specifies the
> user-visible copy for a `403` without `detail` as *"You do not have permission to do that."*,
> and §2.2's `fallbackTitle[403]` carries exactly that sentence but is only reached when `title`
> is **absent**. §7.1 wins over the code, so `ErrorBanner` must render the fixed §7.1 sentence for
> a `403` with no `detail` rather than `error.title`. That is a **phase 0** obligation, in
> `shared/components/ErrorBanner.tsx`; this plan depends on it and does not implement it.

> **4. `EditTicketTypeRequestDto`'s two booleans default to `false`; `CreateTicketTypeRequestDto`'s
> default to `true`.** `EditTicketTypeRequestDto.cs:9-10` versus
> `CreateTicketTypeRequestDto.cs:11-12`. No document mentions the asymmetry. §8.8.

> **5. Four request strings are non-nullable in C# and `NOT NULL` in the schema, so `null`
> produces a `500`, not a `422`.** This is a real exception to `GeneralUIArchitecture.md` §9.3
> rule F that no document records. §8.9.

---

## 12. Known constraints

1. **Nothing in this plan has ever been run.** There is no `frontend/` directory (punch-list item
   3), the three SPA-hosting lines are absent from `Program.cs` (item 1), and this authoring
   machine has no local PostgreSQL, so the API has never been started against a real database.
   Every route, DTO field, limit and status code above was read from source; none was observed in
   a response.
2. **No delete and no version-history list.** §4.3 and §5.2.
3. **No file upload anywhere.** §6.7.
4. **Lost updates on ticket-type edits are undetectable.** Mitigated in §9, not solved. Item 7.
5. **A `422` can never highlight a field.** Item 5. Client validation is the only layer that can
   point at an input.
6. **`allowSubjectOtherThanCreator` has no consumer** until `Tickets` ships. Displayed anyway,
   with a note saying so, because an Accountant setting it needs to see that it was stored.
7. **`toggle` is idempotent and silent about it.** `ToggleTicketTypeHandler.cs:44-45`. Render from
   the returned `isActive`.
8. **The permission table is hand-duplicated from the server.** §6.3 of the governing document
   explains why it is not fetched; §10's diff is the mitigation.
9. **The dev API port is 5131**, per `launchSettings.json`, not the `5000` in
   `04-Infrastructure.md` §2. Item 8; one side must change and the proxy must follow.

---

## 13. Questions to flag if unclear

Flag these; do not answer them by inventing a behaviour.

- [ ] **Should `GET /api/ticket-types/versions` exist**, returning
      `{ versionNumber, createdAt, createdByUserId }[]`? Item 21. `ticket_type_versions.created_at`
      already exists (`20260829_001_CreateTicketTypesSchema.sql:26`) and is projected into no DTO,
      so the version stepper can show numbers and no dates. Asked already in the backend slice
      plan §11 and in `GeneralUIArchitecture.md` §13.
- [ ] **Is a `REGEX_INPUT_CEILING` acceptable, and is there a better mitigation?** §6.4. JavaScript
      has no `RegExp` timeout, while the server carries a 100 ms one
      (`UserSuppliedRegex.MatchTimeout`, `Shared/Validation/UserSuppliedRegex.cs:41`; it used to be
      on `TicketTypeMapper` and is not there any more). The alternatives are a server-side
      complexity bound on `regexPattern`, or running validation in a terminable Web Worker. Both
      are larger than this plan and neither is specified anywhere.
- [ ] **Should the editor offer a "test this pattern" box?** Deliberately omitted (§6.4 point 3) —
      it would run an author-supplied pattern against author-supplied input with no timeout.
- [ ] **Should `NormalizeFields` trim `Key` and `HelpText`**, as correction note T-13 did for
      `Label` and `GroupName`? Item 19. Until it does, the client trim in §8.4 is the only guard.
- [ ] **Is any character set required of a field `key`?** Item 19's second half. The client must
      **not** impose one unilaterally (§9.2), so the alias map of §6.8 rule C is a workaround for
      a decision nobody has made.
- [ ] **Should `ValidateFields` length-check `helpText`**, or should `GeneralUIArchitecture.md`
      §9.2 stop listing it as a server limit? Item 24. The client caps it at 10,000 on the
      strength of an unused constant.
- [ ] **Should `activeOnly` be renamed `isActive`**, or should `false` mean "no filter"? Item 20.
      Renaming is free today because nothing consumes the parameter yet.
- [ ] **Should the four non-nullable request strings become `string?`**, or should the client's
      `''` default be documented in `GeneralUIArchitecture.md` §9.3 rule F? §8.9. Today the client
      is the only thing standing between an omitted optional field and a `500`.
- [ ] **`min_value`/`max_value` are `NUMERIC(18,4)`** (`20260829_001_CreateTicketTypesSchema.sql:46-47`)
      and `ValidateFields` checks only `min <= max`. A `minValue` of `1e20` is a PostgreSQL numeric
      overflow and therefore a `500`. Should the mapper bound them? No client cap is specified,
      because an invented one would reject input the server accepts.
- [ ] **`ChoiceOptionDto.Label` and `.Value` have no length limit anywhere**
      (`TicketTypeDetailDto.cs:64-68`); they are serialised into a `TEXT` column as JSON. No client
      cap is specified, for the same reason.
- [ ] **Should `validation` members be cross-checked against `dataType`?** `minValue` on a
      `SingleLineText` field is stored. §6.3 ignores it at render time and §8.3 clears it in the
      editor, but a type authored directly through the API keeps it.
- [ ] **Should `conditionalVisibility` support any operator other than equality?** One string, one
      `===`. "Greater than", "is not empty" and "is one of" are all natural requests and none is
      expressible. A change to `ConditionalVisibilityDto` and its two columns, not a UI decision.
- [ ] **Should `/list` gain a `search` or `category` parameter?** A catalogue of two hundred types
      is unnavigable at fifteen a page, and §4.3 refuses to fake it client-side.
**Closed 2026-09-02:** this list also asked whether `UI/Plans/00-Foundation/IMPLEMENTATION_PLAN.md`
existed. It does — 981 lines, and it is this plan's phase 0. Nothing in the phase-0 prerequisite,
the files checklist or §11 item 3 changes as a result; the dependency was already stated correctly.

---

## Files checklist

Prerequisite — **not** created by this plan:

- [ ] `UI/Plans/00-Foundation/IMPLEMENTATION_PLAN.md` executed in full (§0.1)

`shared/dynamicForm/` — owned by this plan, consumed by `Tickets` later:

- [ ] `frontend/src/shared/dynamicForm/types.ts` — `FieldDescriptor` and friends, mirror comment naming `ExternalInterfaces/TicketTypeDetailDto.cs` (§2.1)
- [ ] `frontend/src/shared/dynamicForm/visibility.ts` — coercion table, capped fixed point, cycle detection (§6.2)
- [ ] `frontend/src/shared/dynamicForm/buildZodSchema.ts` — takes `visibleKeys`; memoised pattern compilation; `REGEX_INPUT_CEILING` (§6.3, §6.4)
- [ ] `frontend/src/shared/dynamicForm/fieldRegistry.tsx` — eleven entries plus the unknown-type placeholder (§6.5)
- [ ] `frontend/src/shared/dynamicForm/DynamicForm.tsx` — grouping, ordering, the alias map, the three modes; **no** `role` prop (§6.6, §6.8)

`slices/ticketTypes/`:

- [ ] `frontend/src/slices/ticketTypes/types.ts` — six interfaces, each with its C# mirror comment; re-exports `FieldDescriptor` (§2.2)
- [ ] `frontend/src/slices/ticketTypes/fieldDataTypes.ts` — the eleven strings, in `FieldDataTypes.cs` order (§2.2)
- [ ] `frontend/src/slices/ticketTypes/api.ts` — six functions, kebab-case paths, `URLSearchParams` (§2.3)
- [ ] `frontend/src/slices/ticketTypes/queries.ts` — three queries, three mutations, each naming its invalidations (§3)
- [ ] `frontend/src/slices/ticketTypes/schemas.ts` — create and edit Zod schemas. **No `code` in the edit schema** (§8.1)
- [ ] `frontend/src/slices/ticketTypes/screens/TicketTypeListScreen.tsx` (§4)
- [ ] `frontend/src/slices/ticketTypes/screens/TicketTypeDetailScreen.tsx` (§5, §7)
- [ ] `frontend/src/slices/ticketTypes/screens/TicketTypeEditorScreen.tsx` (§8, §9)
- [ ] `frontend/src/slices/ticketTypes/components/ToggleTicketTypeDialog.tsx` (§4.3)
- [ ] `frontend/src/slices/ticketTypes/components/VersionBanner.tsx` (§5.1)
- [ ] `frontend/src/slices/ticketTypes/components/FieldDescriptorEditor.tsx` (§8)
- [ ] `frontend/src/slices/ticketTypes/components/ChoiceOptionsEditor.tsx` (§8.3)
- [ ] `frontend/src/slices/ticketTypes/components/ValidationRulesEditor.tsx` (§8)
- [ ] `frontend/src/slices/ticketTypes/components/ConditionalVisibilityEditor.tsx` (§8.6)

Touched, not created:

- [ ] `frontend/src/routes.tsx` — the four rows, `/ticket-types/new` **before** `/ticket-types/:ticketTypeId` (§3)
- [ ] `frontend/src/shared/permissions/can.ts` — the five TicketTypes rows verified against the catalogue (§10)

---

## Success criteria

Each is verified by running the app, not by reading the code. None has been verified: there is no
`frontend/`, and this machine has no PostgreSQL.

1. All four roles open `/ticket-types` and see a populated table; only `AccountantAdmin` and
   `AccountantUser` see *New ticket type*, *Edit* and *Deactivate*.
2. As an `AccountantUser`, *All* sends **no** `activeOnly` parameter, *Inactive* sends
   `activeOnly=false` and returns only deactivated types — both confirmed in the network tab. As
   an `Employee` the filter is absent from the page.
3. `pageSize=999` renders a pager consistent with the `50` in the response, with no missing rows.
4. Creating a type with one field of each of the eleven data types returns `201`, and the detail
   preview renders eleven controls with no placeholder, no `undefined` label and no console error,
   without a second fetch.
5. Every value in `Slices/TicketTypes/ExternalInterfaces/FieldDataTypes.cs:49-62` has an entry in `fieldRegistry.tsx`
   and the registry has no entry that is not in that file.
6. A `dataType` of `"Currency"` injected through the API renders a red placeholder naming the
   field and the type, and the rest of the form still validates and submits.
7. A required field hidden by `conditionalVisibility` never blocks submit, and its key is
   **absent** from the submitted object — not present with `null`.
8. A three-deep chain A→B→C resolves in one interaction, and a cycle built through the API renders
   every field involved with a warning and does not hang the tab.
9. A `regexPattern` of `(?i)abc` — valid .NET, invalid JavaScript — does not blank the form; the
   field renders with no pattern rule and one console warning.
10. Five fields sharing `displayOrder: 0` render in the same order on a hard reload as on a cache
    read, and on every subsequent render.
11. A type with two named groups renders the ungrouped fields first with no heading above them,
    and a group whose every member is hidden renders no heading.
12. A `FileUpload` field renders disabled, shows its allowed types and size as help text, and does
    not block submit.
13. `code` cannot be changed from the editor, and no request body from `/ticket-types/:id/edit`
    contains a `code` property.
14. Deleting a field row and saving shows *"Saved as version N."*, demonstrably removes the field
    from the new version, and `?version=` on the previous number still contains it.
15. A field key of 101 characters, a duplicate key differing only in case, and a choice field with
    one option are each blocked client-side rather than by a `422`; a key entered as `"  key  "`
    is submitted trimmed.
16. Switching a field from `SingleChoice` to `SingleLineText` clears its options and the save
    succeeds instead of returning *"Non-choice field … cannot have choice options."*
17. A `conditionalVisibility` value control for a `YesNo` controller offers exactly *Yes* and *No*
    and sends `"true"` / `"false"`; answering the controller in the preview shows and hides the
    dependent field live.
18. Saving the editor in a second tab after the first tab has saved is **blocked before any
    request is sent**, and the banner names both version numbers, lists the field keys added and
    removed, offers no *Save anyway*, and says it is a mitigation and not a guarantee.
19. `?version=1` on a type at v5 shows the version banner, offers no *Edit*, and the editor
     reached by URL from a historical version renders no submit button.
20. Deactivating a type removes it from an `Employee`'s list, `/detail` gives them "Not found" —
    never "forbidden" — and `?version=1` still renders for the same `Employee` in the same
    session. Reactivating restores it with the same version number and the same fields.
21. No screen offers delete, duplicate, import, export, a bulk action, or a version list.
22. No screen renders a raw `isActive` boolean, a raw role integer, a raw `dataType` string
    outside the editor's `Select`, or the word "Client".
23. All four greps in §10 return what §10 says they return, and the five `can.ts` rows match
    `TicketTypesActionCatalogue.cs` exactly.
