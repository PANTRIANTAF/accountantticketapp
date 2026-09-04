# Audit Screens — UI Implementation Plan

The build plan for `frontend/src/slices/audit/`: two screens, three components, three plumbing modules, in the order a builder creates them. It is not a restatement of [Screens/AuditScreens.md](../../Screens/AuditScreens.md) — that says *what* the screens are and is cited by section throughout. This says which file is written when and how a future builder confirms it works.

Build position: **after Phase 0** (§0.1), independent of every other slice — `Audit` imports no other slice's `types.ts` or `api.ts`. It is the smallest slice plan by file count and the largest by negative space: §10 forbids more than the steps build, because the audit log is a reader over an append-only table and most affordances a builder reaches for cannot be served. Where something is unclear, flag it in *Questions to flag if unclear* rather than deciding it.

**Documents that govern this document, in precedence order.** Where any disagrees with this plan, **it wins and this plan is wrong** — fix this plan, do not code around it.

| # | Document | Sections that bind this plan |
|---|---|---|
| 1 | [../../../README.md](../../../README.md) | *Locked platform decisions*, *Conflict precedence*, out-of-scope-is-404 |
| 2 | [../../../00-Glossary.md](../../../00-Glossary.md) | *Audit Entry* — "never edited, never deleted"; "Accountant Admin" in full |
| 3 | [../../../01-DomainModel.md](../../../01-DomainModel.md) | §8 what is audited, §9.2 indefinite retention |
| 4 | [../../../02-AuthorizationMatrix.md](../../../02-AuthorizationMatrix.md) | §1, §10, §12 rules 2 and 7 |
| 5 | [../../../App/GeneralAppArchitecture.md](../../../App/GeneralAppArchitecture.md) | §8 route shape, pagination envelope, error contract |
| 6 | [../../GeneralUIArchitecture.md](../../GeneralUIArchitecture.md) | §1.2, §1.4, §1.5, §2.1–2.5, §3.1–3.5, §4.1, §4.3, §5.2, §6.1–6.3, §7.1–7.4, §8.2–8.4, §9.1, §9.3, §10.1, §10.2 |
| 7 | [../../LoginArchitecture.md](../../LoginArchitecture.md) | §8 — the role enum and the falsy-zero rule |
| 8 | [../../Screens/AuditScreens.md](../../Screens/AuditScreens.md) | All of it. This plan may not contradict a row of it |
| 9 | [../00-Foundation/IMPLEMENTATION_PLAN.md](../00-Foundation/IMPLEMENTATION_PLAN.md) | The shared kernel. Prerequisite, not part of this plan |

Non-normative, cited only by number: [../../BACKEND_CHANGES_REQUIRED.md](../../BACKEND_CHANGES_REQUIRED.md) (a punch-list of requests) and [../../../Slices/Audit/IMPLEMENTATION_PLAN.md](../../../Slices/Audit/IMPLEMENTATION_PLAN.md) (the backend plan, stale in one place — §11.1).

---

## 0. Prerequisites and the verified endpoint surface

### 0.1 Phase 0 is a prerequisite, and this plan creates nothing under `shared/`

The kernel — API client, query client, session provider, `RequireRole`, `PaginatedTable`, `usePaginatedQuery`, `StatusChip`, `ErrorBanner`, `EmptyState`, `LoadingRegion`, `NotFoundPage`, `AccessDeniedPage`, `format/dates.ts`, `can.ts` — is built by [../00-Foundation/IMPLEMENTATION_PLAN.md](../00-Foundation/IMPLEMENTATION_PLAN.md). None of it is re-specified here.

**This plan creates no file under `frontend/src/shared/` and none at `frontend/src/` root.** It *edits* four Phase-0 files at the insertion points in step 8, and touches nothing else outside `frontend/src/slices/audit/`. If one of those four is missing, Phase 0 is not done — **stop and build Phase 0**. A local `OutcomeChip`, a local date helper or a second `fetch` wrapper under `slices/audit/` is a defect even when it renders correctly, because it forks a decision Phase 0 owns (§1.4 rule A, §8.1).

### 0.2 The endpoint surface, read off the C# and not off the screen spec

`AuditEndpoints.cs:16-44`. Three routes, all reads, all gated on one action.

| Route | Verb | Request | Response | Declared |
|---|---|---|---|---|
| `/api/audit/search` | **POST** | `SearchAuditLogRequestDto` in the body | `PaginatedResponse<AuditEntryDto>` | `403`, `422` |
| `/api/audit/detail` | GET | `?auditEntryId=<guid>` | `AuditEntryDetailDto` | `403`, `404` |
| `/api/audit/action-codes` | GET | none | `AuditActionsResponseDto` | `403` |

All three handlers open with `RequireAsync(user, "ReadAuditLog", ct: ct)` (`SearchAuditLogHandler.cs:31`, `GetAuditEntryHandler.cs:32`, `ListAuditActionsHandler.cs:25`). `AuditActionCatalogue.cs:13` grants `ReadAuditLog` to `[UserRole.AccountantAdmin]` and nothing else; `PermissionChecker.cs:41` is `TryGetValue(...) && roles.Contains(user.Role)` — fail closed, no default-allow branch.

**`/api/audit/search` is a `POST` that reads, and that is correct** (`AuditEndpoints.cs:18-19`; §2.3 rule C names it as one of the API's deliberate `POST` reads). A `GET` returns `405` with nothing in the body to explain it. **No route declares `401` and all three can return it** — authentication throws in the `CurrentUser` factory, before the handler runs. Handle `401` per §2.3 rule H regardless of the missing metadata.

### 0.3 `AccountantAdmin` only — and both halves are required

Two independent things; building either alone is a defect.

1. **No nav item.** *Audit log* appears for `AccountantAdmin` and nobody else (§5.2).
2. **A denial on the URL.** Both routes sit inside `RequireRole roles={[UserRole.AccountantAdmin]}`, which renders `AccessDeniedPage` (§4.3 rule A); behind it the server returns a real `403`.

**Nav-item-only, no route guard.** A non-Admin who types, bookmarks or is sent the URL mounts the screen, fires `POST /api/audit/search`, takes a `403`, and gets either a permission banner or — worse — an empty table, which reads as *the audit log is empty*. Each attempt also writes a real `PermissionDenied` / `Denied` row against their own account (`PermissionChecker.cs:47-61`). **Route-guard-only, nav item for everyone.** Three of four roles see a link that always denies. It reads as a broken app, they click again, and every click writes another audit row — noise in the one table an investigator must trust.

**The nav item is an affordance, not a control.** The boundary is the server's `ReadAuditLog` grant; router and nav merely reflect it (§4.3 rule B, §6.2 rule A). Never rely on the React app to hide audit data: a row on screen that should not be there is a server leak, and a client-side filter conceals a live bug.

---

## 1. Rules that apply to every step

**A.** Every path is a relative string beginning `/api/`, built in `api.ts` and nowhere else. No base URL, no `import.meta.env`, no `VITE_` anything, ever (§2.3 rule A). `fetch` appears nowhere under `slices/audit/`.

**B.** The session is the `aa_session` HttpOnly cookie. Nothing is read from or written to `localStorage`/`sessionStorage` — not a token, not a remembered filter set. Requests carry `credentials: 'same-origin'`, already set by `shared/api/http.ts`. CORS is never configured.

**C.** Audit rows are the most sensitive data in the system. Nothing from either screen goes to an analytics call, a `console.log`, an error-reporting SDK, a clipboard helper, or any URL but the two SPA routes. The one exception is the applied *filters*, which go into the `/audit` query string on purpose (step 4 rule D) — filters, never rows.

**D.** A payload is untrusted server-stored text, rendered as a text child only. No `dangerouslySetInnerHTML`, no `eval`, no `new Function`, no markdown or HTML renderer (§7).

**E.** `404` means "not found", never "forbidden" (§2.3 rule J). This slice applies no Customer scope — `GetAuditEntryHandler.cs:14-17` says a scope filter here *"reads like protection while providing none"* — so a `404` means the id is wrong. Entries are never deleted, so it never means "deleted" either.

**F.** `retry: false` for every `4xx`, inherited from `shared/api/queryClient.ts` (§3.4). Retrying a `403` asks the server to deny you three times and writes **three** audit rows, all about you.

**G.** Never poll, and never mutate. `refetchInterval` appears once in the whole app, in `notifications/queries.ts` (§3.2 rule H) — an append-only log's cached page is never *wrong*, only missing newer rows, which is what a visible *Refresh* is for. Nothing writes to an `['audit', …]` key either, so there are no invalidations; a `useMutation` inside `slices/audit/` means you built something §10 forbids.

**H.** Compare roles, never test truthiness. `UserRole.AccountantAdmin` is `0`, so `if (session.role)` is `false` for the most privileged role in the system (§10.1; punch-list item 4).

**I.** Add no dependency (§1.5 is closed): no JSON viewer, no virtualised list, no CSV or file-saver package, no relative-time formatter.

---

## 2. Step 1 — the DTO mirrors and the request type

**File:** `frontend/src/slices/audit/types.ts`

Hand-written camelCase interfaces, each commented with the C# file it mirrors so the next reader can diff them (§2.5). Four exports.

### 2.1 `AuditEntry` and `AuditEntryDetail` are two interfaces, not a base and a subclass

`AuditEntryDto.cs:18-28` has eleven properties: `id`, `actorUserId`, `actorRole`, `customerId`, `action`, `targetKind`, `targetId`, `outcome`, `occurredAt`, `sourceIp`, `userAgent`. `AuditEntryDetailDto.cs:11-24` has the same eleven **plus** `beforeValue` and `afterValue`, both `string | null`. Mirror as two flat interfaces, not `interface Detail extends Entry`: the C# remark at `AuditEntryDto.cs:12-15` survives translation — if the detail type is a subtype of the list type then the list type also *is* a detail type, and the separation keeping up to 8 KB of payload off the list endpoint then depends on nobody ever widening a variable.

Nullability read off the C#, not guessed:

- `customerId` is `Guid?` → `string | null`. Every other string property is non-nullable in C# and initialised to `string.Empty`, so `targetId`, `sourceIp` and `userAgent` arrive as `""`, not `null`, when there is nothing to record. Type them `string` and render `"—"` for `""`.
- `occurredAt` is `DateTimeOffset` → an ISO string **carrying an offset** (§10.2): parse it directly, do not treat it as a bare UTC value. `beforeValue` / `afterValue` are `string?` mapped from `jsonb` (`AuditRecordConfiguration.cs:21-22`) — **JSON text, not objects** (§7).

### 2.2 `AuditSearchRequest` — the eight filters, from the request DTO

`SearchAuditLogRequestDto.cs:14-23`, not the screen spec's table. Eight optional filters combined with `AND` (`SearchAuditLogHandler.cs:41-56`) plus two paging fields.

| TS field | C# declaration | TS type | Verified in the handler |
|---|---|---|---|
| `actorUserId` | `string? ActorUserId` | `string \| null` | Exact, case-sensitive equality (`:42`). A partial id matches nothing |
| `action` | `string? Action` | `string \| null` | Must be in `AuditActions.All` or `422` (`:95`) |
| `targetKind` | `string? TargetKind` | `string \| null` | Must be in `AuditTargets.All` or `422` (`:103`) |
| `targetId` | `string? TargetId` | `string \| null` | `422` if sent without `targetKind` (`:92`) |
| `customerId` | **`Guid? CustomerId`** | `string \| null` | The only non-`string` filter. `""` is a **`400`** from binding |
| `outcome` | `string? Outcome` | `string \| null` | Must be in `AuditOutcome.All` or `422` (`:107`) |
| `from` | `DateTimeOffset? From` | `string \| null` | Inclusive `>=` (`:54`). Send `toISOString()` |
| `to` | `DateTimeOffset? To` | `string \| null` | Inclusive `<=` (`:56`). `422` if `from > to` (`:85`) |
| `pageNumber` | `int PageNumber = 1` | `number` | Clamped up to 1, never rejected |
| `pageSize` | `int PageSize = DefaultPageSize` | `number` | Default 15, clamped to 50, never rejected |

The seven string filters are tested with `IsNullOrWhiteSpace`, so `""` happens to behave as "absent". **`customerId` does not** — a `Guid?` bound from the body makes `""` a model-binding `400` whose body carries no sentence worth showing (§3.2 rule G). Send `null` for every untouched field and the asymmetry never arises.

### 2.3 `AuditActionCodes` — three lists, not two

`AuditActionsResponseDto.cs:9-11` declares `Actions`, `TargetKinds` **and** `Outcomes`, each a `List<string>`. Mirror all three as `string[]`. §4.3 says why the third exists.

---

## 3. Step 2 — the endpoint wrappers

**File:** `frontend/src/slices/audit/api.ts`

Three functions, named for the endpoint and not the screen (§2.5). No React, no hooks, no TanStack Query — this file exists to be read line by line against `AuditEndpoints.cs`.

```ts
import { get, post } from '../../shared/api/http';
import type { PaginatedResponse } from '../../shared/api/paginated';
import type { AuditEntry, AuditEntryDetail, AuditActionCodes, AuditSearchRequest } from './types';

export const searchAuditLog = (
  body: AuditSearchRequest,
): Promise<PaginatedResponse<AuditEntry>> => post('/api/audit/search', body);

export const getAuditEntry = (auditEntryId: string): Promise<AuditEntryDetail> =>
  get(`/api/audit/detail?${new URLSearchParams({ auditEntryId })}`);

export const getAuditActionCodes = (): Promise<AuditActionCodes> => get('/api/audit/action-codes');
```

### 3.1 Three ways this step goes wrong

1. **Sending the filters as a query string.** The route binds from the body (`AuditEndpoints.cs:20`). A `POST` with filters in the query string and an empty body binds every filter to `null` and returns the whole log with a `200` and no complaint — the search silently ignores everything the user set.
2. **Concatenating `/api/audit/detail` by hand.** Use `URLSearchParams`; a raw interpolation of a non-GUID produces a malformed query the server answers with a `400`.
3. **Adding a fourth function.** `AuditEndpoints.cs` maps exactly three routes, and `IAuditApi.cs` exposes only *write* members to other slices — there is no query member to wrap.

---

## 4. Step 3 — query keys and the three hooks

**File:** `frontend/src/slices/audit/queries.ts`

Keys are `[sliceName, resource, ...discriminators]` (§3.1), exported as one object so no screen builds an array literal.

```ts
export const auditKeys = {
  all: ['audit'] as const,
  search: (req: AuditSearchRequest) => ['audit', 'search', req] as const,
  detail: (auditEntryId: string) => ['audit', 'detail', auditEntryId] as const,
  actionCodes: ['audit', 'actionCodes'] as const,
};
```

### 4.1 `useAuditSearch` — through `usePaginatedQuery`, never `useQuery`

Paginated lists go through Phase 0's `usePaginatedQuery` and nothing else (§3.2 rule G), so clamping is handled in one place. **Every filter appears in the key**; omit one and two filter sets share a cache entry, showing the wrong rows under the right heading — invisible on an audit tool because the rows still look plausible (§3.2 rule A). Render the pager from `response.pageSize`, never the value you sent: `PaginatedQuery.Normalize` clamps to `[1, 50]` and substitutes 15 for anything `<= 0` (`SearchAuditLogHandler.cs:35`; punch-list item 17), so asking for 999 yields 50 with a `200`.

### 4.2 `useAuditEntry` — `enabled` is for the id, never for permission

`enabled: isGuid(auditEntryId)` is a genuine data dependency. Never use `enabled` to express "not allowed" (§3.2 rule B): gating is step 8's job, and a disabled query renders an empty screen where a denial should appear.

### 4.3 `useAuditActionCodes` — cached for the session, unlike everything else here

The catalogues are compile-time constants: `ListAuditActionsHandler.cs:29-34` projects `AuditActions.All`, `AuditTargets.All` and `AuditOutcome.All`, ordinal-sorted, with **no `DbContext` injected at all** (`:19-21`). The response can only change on a deploy, and a deploy reloads the SPA.

```ts
export const useAuditActionCodes = () => useQuery({
  queryKey: auditKeys.actionCodes,
  queryFn: getAuditActionCodes,
  staleTime: Infinity,   // Compile-time constants. A refetch can only return the same body.
  gcTime: Infinity,      // Kept for the session so an unmount does not re-request it.
});
```

This is the **only** query here that departs from the kernel's 30-second `staleTime`. Search results are the opposite case and keep the default: the log grows continuously, and a search cached forever tells an investigator the log stopped.

**Never hardcode any of the three lists** (§5 rule A), even though `AuditActions.cs` is a wall of constants trivial to copy. The server adds an action code in the same commit as the feature that emits it, so a copy silently lacks the newest codes — exactly the ones being investigated, because the newest feature is the one that just misbehaved. The omission has no symptom: the dropdown simply lacks the value.

> **The counts in the screen spec are approximate and nothing may depend on them.** `AuditActions.cs` declares **45** action constants in this working tree, not the "~44" in [../../Screens/AuditScreens.md](../../Screens/AuditScreens.md) §5 and §8; `AuditTargets` declares 8 (`AuditActions.cs:74-81`) and `AuditOutcome` 3 (`IAuditApi.cs:14-16`). The tilde does real work. Assert no count in a test, comment or helper text — `AuditActions.All` is built by reflection over the fields (`AuditActions.cs:65-69`), so a new constant changes the number with no other edit.

### 4.4 Four ways this step goes wrong

1. **`staleTime: Infinity` on the search.** The two policies are opposite and sit six lines apart. On the search, *Refresh* returns the cached page and the Admin concludes nothing new was written.
2. **A partial key** — `['audit', 'search', filters]` without the paging fields is the same bug as omitting a filter. Put the whole request object in the key.
3. **Fetching `action-codes` in the panel component.** It is a hook in `queries.ts` called once from the panel; a bare `useEffect` + `fetch` neither deduplicates nor obeys rule A.
4. **`refetchOnMount: 'always'` on the catalogues** — inert against `staleTime: Infinity` until someone sets it, then one extra request per navigation to an already query-heavy screen.

---

## 5. Step 4 — the filter schema, the panel, the table and the search screen

**File:** `frontend/src/slices/audit/screens/auditFilterSchema.ts`

**File:** `frontend/src/slices/audit/components/AuditFilterPanel.tsx`

**File:** `frontend/src/slices/audit/components/AuditEntryTable.tsx`

**File:** `frontend/src/slices/audit/screens/AuditSearchScreen.tsx`

Layout in [../../Screens/AuditScreens.md](../../Screens/AuditScreens.md) §3.1, control mapping in §3.2, column rendering in §6. The panel is collapsible with eight controls plus *Clear all* and *Search*; paging belongs to the pager, not the panel. The table is `PaginatedTable` fed a `PaginatedResponse<AuditEntry>` — never `Table` + `TablePagination` assembled here (§8.2), and `@mui/x-data-grid` is banned.

### 5.1 The schema mirrors two server `422`s exactly

Ten fields through `zodResolver` (§9.1). The server returns no field-level errors (punch-list item 5), so this schema is the only thing that can put a message next to a control. A rule stricter than the server's blocks legitimate input; looser produces the field-less banner of §7.3.

**A. `from` must not be later than `to`.** The server answers `422 "'From' must not be later than 'To'."` (`SearchAuditLogHandler.cs:85-86`) with no field attached. Caught here it outlines the picker:

```ts
.refine((f) => !f.from || !f.to || new Date(f.from) <= new Date(f.to),
  { path: ['to'], message: 'The "to" date must not be earlier than the "from" date.' });
```

**B. `customerId`, if present, must parse as a GUID** (`SearchAuditLogRequestDto.cs:18`) — a non-GUID is a binding `400`, not a `422`, and a `400` carries no sentence worth rendering.

The `targetId`-requires-`targetKind` rule (`:92-93`) is enforced structurally instead (rule B below), so the invalid state cannot be expressed. The three catalogue `422`s (`:95`, `:103`, `:107`) are unreachable while values come from server-populated `Select`s; where the catalogues failed and the controls degraded to text fields they *are* reachable, so render the server's sentence verbatim (§7.3). One of those sentences names the endpoint to fetch — do not paraphrase a message that tells the reader exactly what to do.

### 5.2 Rules for the panel, the table and the screen

**A.** Draft filters are React state; **applied** filters are the query key (§3.2 rule D). Nothing fetches until *Search*. Keying off the draft fires a `POST` against the largest table in the database on every keystroke.

**B.** `targetId` is disabled until `targetKind` is set, and clearing the kind clears the id.

**C.** Collapsed, the panel names every active filter — a count plus one removable `Chip` each (§3.2 rule C). A panel reading only *Filters* lets a reader take a filtered table for the whole log and conclude *this never happened* from rows that were merely excluded.

**D.** Applied filters are mirrored into the `/audit` query string with `useSearchParams`, and the query key derives from the URL (§3.2 rule E). Otherwise *Back* from an entry re-runs an unfiltered search and the investigator loses their place in a table with hundreds of thousands of rows. It also makes a search shareable, which is how one Admin hands an investigation to another.

**E.** The three `Select`s degrade to `TextField`s if `action-codes` failed, with helper text saying an unrecognised value is rejected (§5 rule C). Never render an empty `Select`: it makes search unusable and reads as *there are no actions*.

**F.** Applying or clearing any filter resets `pageNumber` to `1` (§3.2 rule F). **G.** Send `null`, not `''`, for untouched fields (§9.3 rule F), and send timestamps with an explicit offset via `toISOString()` (§3.2 rule H) — a bare local datetime is read against the *server's* offset and silently shifts the window, dropping evidence at the boundary.

**H.** A row is a real link to `/audit/:auditEntryId`, so middle-click and *Open in new tab* work: comparing two entries side by side is how this screen is used.

**I.** `actorRole` is a **`string`** here while `role` everywhere else in the API is an integer — `AuditApi.cs:34` stores `user.Role.ToString()`, and `LogUnauthenticatedAsync` stores the literal `"Unknown"` (`:41`). Through the integer map in `format/enums.ts` it yields `undefined`; `Number(actorRole)` yields `NaN`. Map the **string** to its glossary label and render an unrecognised value verbatim rather than blank — a role this UI does not know is itself information.

**J.** `customerId: null` means "no Customer was involved", never "every Customer". Inviting an Accountant, a failed login, a ticket-type edit: none belongs to a Customer. Render `"—"`. Rendering "All Customers" inverts the meaning of the most sensitive column on the screen.

**K.** Timestamps are exact to the second and never relative, via Phase 0's `format/dates.ts` in the browser's local timezone (§10.2, §6 rule G). The sort is `occurredAt DESC, id DESC` *because* one transaction writes several entries in the same second (`SearchAuditLogHandler.cs:62-65`), so seconds are load-bearing. "3 hours ago" is useless in an investigation and renders two entries forty minutes apart identically.

**L.** `totalCount` gets thousands separators via `Intl.NumberFormat` (§3.5 rule B) — `412338` read as a page count is how a reader concludes the log is corrupt — and *Refresh* is `refetch()` on the current key, not an invalidation, not a new key, not a poll.

**M.** Empty is not an error, and the two empty cases differ. `totalCount === 0` renders an `EmptyState` that **names the active filters** and offers *Clear all* — a bare "No results" is the sentence an investigator misreads as *this never happened*. `items.length === 0 && totalCount > 0` means the pager ran past the end (§3.3) and offers "Back to the first page". This slice hits the second case most easily: retention is indefinite (`01-DomainModel.md` §9.2), there is no purge, and narrowing a filter while on page 200 produces it immediately.

### What this step does NOT do, and why

- **No `traceId` field** (§11.2) — the most likely thing for a builder to add, because the obvious support workflow ends here and the field cannot be built.
- **No free-text "search everything" box.** `actorUserId` and `targetId` are exact-equality filters (`:42`, `:48`) and no full-text route exists. A box searching the current page would report "not found" about a log that contains the row.
- **No saved or named filter sets** — nowhere to store one, and §1 rule B forbids `localStorage`. The URL is the sharing mechanism (rule D).
- **No sort controls.** The server orders `occurredAt DESC, id DESC` (`:65`) and accepts no sort parameter.

---

## 6. Step 5 — the outcome chip rows

**File:** `frontend/src/shared/components/StatusChip.tsx` — **edited, not created.** Phase 0 owns it (§8.3). Add three rows to the one colour map; write no local chip.

`AuditOutcome` (`IAuditApi.cs:14-16`) is `Success | Denied | Failure`, enforced on write at `AuditApi.cs:57-58` and by `CHECK (outcome IN ('Success', 'Denied', 'Failure'))` in `20260901_002_ReshapeAuditEntries.sql`. It is a **fourth status vocabulary**, and none of its three words appears in any of the other three:

| Vocabulary | Values |
|---|---|
| `Customer.status` | `Active`, `Suspended` — **never `Invited`** |
| `UserAccount.status` / `accountStatus` | `Invited`, `Active`, `Suspended` |
| Employee status | `Active`, `Departed` |
| **Audit `outcome`** | `Success`, `Denied`, `Failure` |

`Denied` is `warning`: authorization refused the operation and **the system behaved correctly** — somebody attempted what they may not do. `Failure` is `error`: the operation was permitted and went wrong. Collapsing them destroys the distinction the log exists to record — forty denials from one account in a minute is an intrusion attempt, forty failures is an outage, and they need opposite responses. The chip shows the **word** as well as the colour (§8.4).

One shared colour map does not make every word valid for every entity. Never feed an audit `outcome` into a Customer status filter, and never offer `Invited` as a Customer status — it is a `UserAccount` status, the person and not the company, and offering it returns a `422` that reads as a server bug.

> **If Phase 0's `StatusChip` has no fallback for an unmapped word, that is a Phase 0 gap.** Flag it; do not add a local chip, which is how `Denied` becomes amber here and grey on the next screen.

---

## 7. Step 6 — rendering an arbitrary JSON payload

**File:** `frontend/src/slices/audit/components/AuditPayloadPanel.tsx`

`beforeValue`/`afterValue` are `string?` columns of type `jsonb` (`AuditRecordConfiguration.cs:21-22`), so the response carries a **quoted string**: `"beforeValue": "{\"Name\":\"Acme\"}"`. `Object.keys(entry.beforeValue)` gives you character indices. The shape is otherwise unknown — `AuditApi.cs:71-72` serialises whatever the calling slice passed.

```ts
export function parsePayload(raw: string | null): { pretty: string } | { raw: string } | null {
  if (raw === null) return null;   // No change recorded. Not an error, and not "{}".
  try { return { pretty: JSON.stringify(JSON.parse(raw), null, 2) }; } catch { return { raw }; }
}
```

**A.** Render the result as a **text child** of a `<pre>`-styled MUI `Box` with `whiteSpace: 'pre-wrap'` and `overflowWrap: 'anywhere'`, so one long value cannot force horizontal scroll on the page. React escapes text children and that escaping is the entire defence: a payload containing `<script>` renders as those nine characters. It holds only while the string is a *child* — the moment it becomes `dangerouslySetInnerHTML`, a `srcDoc`, a `style` value or a markdown renderer's argument, the escaping is gone (§1 rule D).

**B.** `null` means *this entry records no change to existing data* — a create has no before, a read has neither. Render that sentence. Do **not** render `{}` or synthesise an empty object to make the panels symmetrical: `{}` says "an empty change was recorded", a different and false statement.

**C.** Two things that look like `null` are not `null`. An empty or whitespace-only string fails `JSON.parse` and lands in the `{ raw }` branch, which is correct — "the column holds something this UI could not parse" and "the column is null" are different facts about the row. And the four-character JSON document `null` is a value: `Redaction.ToJson` returns the string `"null"` when serialisation produced a JSON null (`Redaction.cs:33`), it round-trips to the text `null`, and rendering the no-change sentence for it would claim the column was empty when it holds something. Let the `pretty` branch print it.

**D.** Size is bounded server-side, so no windowing and no lazy load: `Redaction.cs:8` caps a serialised payload at `8 * 1024` characters and replaces anything longer with `{"truncated":true,"length":<n>}` (`:36`). Two-space re-indentation can roughly double that — tens of kilobytes, well inside what a browser renders unaided. Constrain the panel with `maxHeight` and `overflow: 'auto'` so a large payload cannot push the metadata off screen, and **never truncate the text yourself**: it is evidence, and a UI-side ellipsis is indistinguishable from the server's own truncation marker.

**E.** Two payload shapes are real and render as explicit notes, never as an empty panel: `{"truncated": true, "length": <n>}` and `{"unserialisable": true, "type": "<name>"}` (`Redaction.cs:36`, `:59-63`). Both mean *the payload is gone, the row is intact*; an empty panel would say *no change was recorded*, which is false. Keep the raw JSON visible beneath the note — the note renders two keys, it does not prove provenance, and a genuine payload could carry the same keys.

**F.** No collapsible JSON tree. It needs a dependency (§1 rule I) or a bespoke component, and a collapsed node hides evidence by default on the one screen whose value is showing what was stored.

---

## 8. Step 7 — the detail screen

**File:** `frontend/src/slices/audit/screens/AuditEntryScreen.tsx`

Layout in [../../Screens/AuditScreens.md](../../Screens/AuditScreens.md) §4: a metadata block, then the two payload panels from step 6.

**A.** Validate the path parameter as a GUID **before** fetching. `AuditEndpoints.cs:28` binds `Guid auditEntryId` from the query string, so a malformed value is a **`400`** from parameter binding whose body says nothing actionable. Render `NotFoundPage` and issue no request.

**B.** A `404` renders `NotFoundPage`. The server's message is `"Audit entry not found."` (`GetAuditEntryHandler.cs:36`) — not "forbidden", not "deleted" (§1 rule E).

**C.** *Back to audit log* uses router history, not a hardcoded `/audit`, so filters and page survive. There is no next/previous entry navigation: the endpoint takes one id and the API has no adjacency concept, so synthesising it from the last search page is silently wrong the moment the reader arrived by shared link — the neighbours would be the *sharer's* filtered neighbours.

**D.** Link a target only where an SPA route exists: `Customer` → `/customers/:id`, `Employee` → `/employees/:id`, `TicketType` → `/ticket-types/:id`. Never for `Ticket`, `Document`, `Notification` or `None` (`AuditActions.cs:74-81` lists all eight kinds) — those screens do not exist, so the link renders `NotFoundPage` and reads as a broken audit log.

**E.** Render `action` as the code, monospace, not a prettified sentence. "Permission denied" is friendlier, but `PermissionDenied` is what the reader pastes into the `action` filter and greps the source for, and the filter `422`s the humanised form (`SearchAuditLogHandler.cs:95`).

**F.** Truncated-at-write fields are rendered verbatim, with no ellipsis and no parsing (§11.4).

---

## 9. Step 8 — wiring, and verifying both halves of the gate

Four Phase-0 files are **edited**, one insertion point each. Nothing is created.

**File:** `frontend/src/routes.tsx` — the two rows from §4.1 verbatim, both wrapped in `RequireRole roles={[UserRole.AccountantAdmin]}`: `/audit` → `AuditSearchScreen`, `/audit/:auditEntryId` → `AuditEntryScreen`, both inside the shell.

**File:** `frontend/src/shared/permissions/actions.ts` and `can.ts` — one row, `ReadAuditLog`, `AccountantAdmin` alone, matching `AuditActionCatalogue.cs:13` exactly. `can()` returning `true` followed by a `403` is a bug in that table, never on the server (§6.2 rule B) — fix the row, do not wrap the call in a `try`/`catch`.

**File:** `frontend/src/shared/components/AppShell.tsx` — one nav item, *Audit log* → `/audit`, for `AccountantAdmin` only. The nav derives from the §5.2 role table, **not** from `can()`: a nav item maps to a page, and pages combine actions with different role sets. That this one aligns with `can('ReadAuditLog')` is a coincidence of a single-action slice, not a reason to rewire the nav.

**File:** `frontend/src/shared/components/StatusChip.tsx` — the three outcome rows of step 5.

### 9.1 Both halves, checked separately, as each of the three non-Admin roles

1. The nav shows **no** *Audit log* item — absent, not greyed out. There is nothing to grey: `can('ReadAuditLog')` is `false` and §5.2 gives the row no cell for that role.
2. Typing `/audit` renders `AccessDeniedPage` — a **denial page, not a redirect** (§4.3 rule A). Someone who typed the URL deserves to be told; a silent bounce to `/customers` reads as a broken link, so they try again and write a second audit row.
3. Then, as `AccountantAdmin`, search `action = PermissionDenied`, `outcome = Denied` and confirm one new row per attempt with the attempting account's id and its role at the time.

Check 3 will look like a bug and is not: **this screen shows the consequences of the reader's own denied actions.** `PermissionChecker.cs:47-61` writes `PermissionDenied` / `AuditTargets.None` / `""` / `Denied` with `After: new { Action = action }` before throwing the `403` at `:63`, and nothing exempts this slice — so a curious `AccountantUser` who types `/audit` writes a row into the log they were trying to read.

**Reading the log is not itself audited.** Both read handlers write nothing on success — *"a log that grew on every read would be a log nobody could read"* (`SearchAuditLogHandler.cs:13-15`). Do not add a client-side "record that I looked" call: there is no endpoint and it would invert the design.

---

## 10. What this slice does NOT build, and why

The audit log is **append-only and read-only forever**. `02-AuthorizationMatrix.md` §10: *"Write to the audit log — **Nobody.** Written only by the application"*; *"Edit or delete an audit entry — **Nobody.** No API exists for this."* `AuditEndpoints.cs` maps three routes and all three read. `20260901_002_ReshapeAuditEntries.sql` closes with `COMMENT ON TABLE audit_entries IS 'Append-only. No UPDATE or DELETE path exists in the application.'` There is no create, edit, delete, purge, archive or export endpoint and by design there never will be. Each item below is something a builder will reach for and none can be served.

1. **No export, CSV, *Print*, *Download* or *Copy all*** — the most likely rule to be "helpfully" violated. A client-side CSV from the search response is **one page of at most 50 rows** out of a `totalCount` in the hundreds of thousands: silently incomplete while looking authoritative, the worst possible property for an audit export, because nothing in the file reveals what is missing. Nor may you loop the pager: hundreds of unbounded `POST`s against the largest table in the database, from a browser tab. Whether a server-side export should exist is an open question below.
2. **No delete, edit or annotate — and no *disabled* one either.** A greyed-out *Delete* implies the operation exists and is merely unavailable to you, misrepresenting the guarantee the table provides.
3. **No "resolve", "acknowledge", "flag", "assign" or "reviewed by" state.** `AuditRecord.cs:5-17` is thirteen properties and none is a status a reader can set. Held client-side it would survive no refresh and no second Admin would ever see it. This screen is a reader, not a case tracker.
4. **No "unredact" or "show original"** (§11.1) — there is nothing to recover.
5. **No client-side redaction pass.** §6.2 rule A: something on screen that should not be there is a server leak, and a UI filter conceals it. Over-redaction is deliberate server-side — `Redaction.cs:14-16` notes that a property named `TokenCount` is redacted — so a client list would be a second, divergent policy.
6. **No correlation into a timeline** — no grouping by "session", no "related entries", no chains inferred from adjacent timestamps. Nothing links two rows: `AuditRecord.cs` has no correlation id, no request id, no trace id. Every such view is the UI's guess presented as the log's finding. The supported way to see a sequence is `actorUserId` plus a date range, honest because the reader chose it. Nor is there a **per-entity audit panel on another slice's screen**: no per-entity endpoint exists, and if it did it would put `AccountantAdmin`-only data on a screen four roles can reach.
7. **No relative timestamps anywhere**, not even as a secondary label (step 4 rule K), and **no polling and no mutation** (§1 rule G).

---

## 11. Four backend facts the UI absorbs rather than works around

### 11.1 Redaction happens at write time, so there is nothing to un-redact

`ExternalInterfaces/AuditApi.cs:71-72`: `BeforeValue = Redaction.ToJson(entry.Before, _logger)` and `AfterValue = Redaction.ToJson(entry.After, _logger)` are evaluated **inside the object initialiser passed to `_db.AuditEntries.Add(...)`**, before `SaveChangesAsync` at `:77`. `Redaction.cs:67-87` walks the serialised node and replaces any property whose name *contains* `password`, `hash`, `salt`, `token`, `secret`, `apikey`, `sessionid` or `cookie` — at any depth, case-insensitively — with the literal string `"[redacted]"`. Neither read handler redacts anything; `AuditEntryDetailDto.cs:5-7` says so in its own doc comment.

**The column never held the secret.** Three consequences:

1. **No "unredact", "show original" or "reveal" affordance, and nothing implying one exists** — no tooltip reading "hidden for security", no lock icon, no disabled eye button. The plaintext was never written; no privileged call returns it and no privilege would.
2. **Do not hide, blank or filter out a `"[redacted]"` value.** It is *information*: this property changed, and deliberately not to what. Blanking destroys it.
3. **Do not reformat it** into "hidden", "•••••" or an icon. It is the literal stored value, and an Admin comparing screen to database must see the same string. Redaction is not a rendering concern at all, which is why §10 item 5 forbids a client pass.

> **The backend plan's §2.1 describes read-time redaction and the code does not do that.** `Slices/Audit/IMPLEMENTATION_PLAN.md:257` calls `AuditEntryDto` *"the **read model** returned by the query handlers, with redaction applied"*, which reads as redaction on read. The behaviour is right and the plan's wording is stale. Recorded as punch-list **item 25**, also flagged in [../../Screens/AuditScreens.md](../../Screens/AuditScreens.md) §6 and §9. Do not edit working code to match a plan.

### 11.2 The log is not searchable by `traceId`, and the screen must not pretend otherwise

Verified three ways: `AuditRecord.cs:5-17` declares thirteen properties and none is a trace or correlation id; `SearchAuditLogRequestDto.cs:14-23` declares eight filters and none is one; a search for `trace` across `Slices/Audit/` matches nothing, and `20260901_002_ReshapeAuditEntries.sql` adds nine columns and no trace column.

Meanwhile `AppExceptionMiddleware` puts a `traceId` in every `ProblemDetails` and §7.1 **mandates** that `ErrorBanner` show it on a `500`. So the one identifier the UI hands the user is the one the audit log cannot be queried by, and the obvious support workflow — paste it into audit search — dead-ends. Punch-list **item 22**, whose *UI workaround* column reads "None".

What the search screen does instead:

1. **No `traceId` field and no free-text control that could be mistaken for one**, no helper text promising one, and no "coming soon" note. A filter that silently ignored the value would be worse than its absence.
2. **The substitute workflow is stated in the empty state, not in a new control**: identity and time are what a support conversation actually yields, so filter `actorUserId` where known, plus a `from`/`to` window bracketing the report, plus `outcome = Failure`. Honest, because the reader chose the window.
3. **State its limit.** A handler that failed *before* reaching its audit write leaves **no row at all** — auditing happens after the save succeeds — so the substitute search can legitimately return nothing for a real `500`. The empty state must therefore name what was filtered (step 4 rule M) and never read as "this never happened".

### 11.3 There is no id→display-name endpoint, so ids render as ids

Punch-list **item 23**. Rows store `ActorUserId`, and the backend plan §8 rule 3 tells the UI to join ids to names client-side. That is not implementable: `/api/accountants/list` is paginated **and** Office-only, so it cannot resolve a Customer-side actor and cannot serve as a lookup table; `/api/employees/get` needs a Customer scope the audit row does not carry; and no batch id→name endpoint exists anywhere.

So `actorUserId` and `targetId` render as **raw ids**, monospace, verbatim, middle-truncated in the table and full in the detail. It looks unfinished because it is. Do **not** paper over it by fetching `/api/accountants/list` and hoping the actor is on page one, and do not build a best-effort resolver that falls back to the id — a name appearing for some rows and not others reads as a data-quality problem in the log rather than a gap in the client.

**A constraint on the future resolver, so it is not designed wrongly later.** Whatever endpoint lands, it **must not distinguish "no such user" from "not visible to you"** — it returns nothing for both. That difference turns a name lookup into an **enumeration oracle** for exactly the ids the out-of-scope-is-404 rule protects: a caller who can tell them apart can probe id space and learn which accounts exist. Item 23 states this requirement; a UI rendering the two cases differently would defeat it from the client even if the server got it right, so render one indistinguishable fallback — the raw id — for both.

### 11.4 `sourceIp` is truncated to 45 characters at write time, `userAgent` to 512

`AuditApi.cs:74` writes `Truncate(..., 45)` and `AuditRecordConfiguration.cs:24` declares `source_ip VARCHAR(45)`. Forty-five is exactly the width of the longest legal textual IP address — an IPv4-mapped IPv6 literal such as `0000:0000:0000:0000:0000:ffff:255.255.255.255` — so truncation cannot mangle a well-formed address, and a 45-character value is a full address at the boundary rather than a clipped one. `AuditApi.cs:75` truncates `userAgent` to 512 (`AuditRecordConfiguration.cs:25`), and real user-agent headers **do** exceed that, so those values genuinely are clipped. `actorUserId` is truncated to 100, `actorRole` to 30, `targetId` to 100 (`:65`, `:66`, `:69`).

What the UI does with all five: **render verbatim, monospace, `"—"` when empty, nothing else.**

- **No ellipsis and no "(truncated)" label.** The UI cannot tell a clipped value from one that is exactly the limit, so either marker is a claim it cannot support — and on `sourceIp` it would assert data loss where none occurred.
- **Never parse `userAgent`** into "Chrome on Windows": the parse may run on a mutilated string, and a guess replaces evidence with an inference in the one table whose value is holding what actually arrived. Likewise **never re-format, reverse-resolve or geo-locate `sourceIp`**, and do not assume it parses as an address at all — `""` is a legal stored value.
- **Do not label the column "the user's IP address".** `Program.cs` configures `UseForwardedHeaders`, but with `KnownProxies`/`KnownIPNetworks` read from configuration and, when both are empty, it logs a warning and continues — and the compose file that would set them does not exist (punch-list items 2 and 25). Behind Caddy the value may uniformly be the proxy's address. *Source IP* is honest either way; the stronger claim is not.

---

## Files checklist

Created under `frontend/src/slices/audit/`, in build order:

- [ ] `types.ts` — `AuditEntry`, `AuditEntryDetail`, `AuditActionCodes`, `AuditSearchRequest` (§2)
- [ ] `api.ts` — `searchAuditLog` (POST), `getAuditEntry`, `getAuditActionCodes` (§3)
- [ ] `queries.ts` — `auditKeys` and the three hooks, incl. the `Infinity` catalogue policy (§4)
- [ ] `screens/auditFilterSchema.ts` — ten fields, the date-range `refine`, the GUID check (§5.1)
- [ ] `components/AuditFilterPanel.tsx` — collapsible, active-filter chips, degradable selects (§5.2)
- [ ] `components/AuditEntryTable.tsx` — columns per `AuditScreens.md` §3.1, rows link out (§5.2)
- [ ] `screens/AuditSearchScreen.tsx` — filters in the URL, `PaginatedTable`, two empty cases (§5.2)
- [ ] `components/AuditPayloadPanel.tsx` — `parsePayload` and the four payload shapes (§7)
- [ ] `screens/AuditEntryScreen.tsx` — GUID guard, metadata block, two payload panels (§8)

Edited, all owned by Phase 0 — if one is missing, Phase 0 is not done:

- [ ] `shared/components/StatusChip.tsx` — three outcome rows in the one colour map (§6)
- [ ] `shared/permissions/actions.ts` + `can.ts` — one row, `ReadAuditLog`, AA alone (§9)
- [ ] `shared/components/AppShell.tsx` — one nav item, AA only (§9)
- [ ] `routes.tsx` — two rows, both inside `RequireRole` (§9)

**Not to be written at all:** any export, download, print or clipboard helper; any mutation; any `['audit', …]` invalidation; any client-side redaction; any relative-time formatter; any id→name resolver; any `traceId` filter; any per-entity audit component in another slice; any file under `frontend/src/shared/` that does not already exist.

---

## Questions to flag if unclear

Flag these; do not answer them by inventing a behaviour.

- [ ] **Is a server-side export endpoint wanted?** An auditor will ask within a week, and §10 item 1 forbids faking it client-side. A server-side export with **its own action code** — exporting the log is itself worth recording — is the shape to consider.
- [ ] **Should the log be searchable by `traceId`?** §11.2, punch-list item 22. The largest single gap in this screen's investigative value.
- [ ] **Should the reader see names instead of ids?** §11.3, punch-list item 23, and the enumeration-oracle constraint any answer must satisfy.
- [ ] **Does `sourceIp` record the proxy or the real client?** §11.4; backend plan §13 question 3 is still open. Until answered the column may uniformly be the Caddy container's address.
- [ ] **Two possible Phase 0 gaps.** Does `StatusChip` accept an unmapped word (§6) — if it throws or renders blank that is a Phase 0 decision, not a licence for a local chip. And does `format/dates.ts` expose a seconds-precision formatter, which step 4 rule K requires?
- [ ] **`AuditActionsResponseDto` returns three lists where the backend plan §6.3 specifies two** (punch-list item 25). This plan depends on `outcomes` staying. Confirm it does.
- [ ] **Should the backend plan's §2.1 redaction wording be corrected?** §11.1 — behaviour right, documentation stale.
- [ ] **What does an Admin do with `actorRole: "Unknown"`?** Written by `LogUnauthenticatedAsync` (`AuditApi.cs:41`) and not a `UserRole`. Step 4 rule I renders it verbatim; whether it deserves its own filter option is a product question.

---

## Success criteria

Each is verified by running the app, not by reading the code. Nothing in this plan has ever been run: there is no `frontend/` directory, no Dockerfile, and no local PostgreSQL on the authoring machine, so no route, DTO field or status code here was observed in a response.

1. `/audit` as an `AccountantAdmin` lists entries newest first, with an exact timestamp including seconds and a `totalCount` carrying thousands separators.
2. *Audit log* appears in the nav for `AccountantAdmin` only, and `/audit` typed by each of the other three roles renders `AccessDeniedPage` — **both halves, checked separately** (§9.1). The one `Audit` row in `can.ts` matches `AuditActionCatalogue.cs` exactly: `ReadAuditLog`, `AccountantAdmin` alone, nothing extra on either side.
3. After criterion 2, searching `action = PermissionDenied`, `outcome = Denied` shows one new row per attempt, carrying the attempting account's id and its role at the time.
4. The three dropdowns populate from `/api/audit/action-codes` with **exactly one** request per session however many searches are run; with that endpoint blocked, all three filters remain usable as text fields and none renders empty.
5. A `from` later than `to` produces a field-level error on the *to* picker and **no** request; a `targetId` cannot be entered without a `targetKind`; provoking any other `422` by hand leaves every filter value intact and renders the server's `title` verbatim.
6. Collapsing the panel leaves the active filters and their values visible; clicking an entry then pressing *Back* restores the same page of the same filtered search.
7. `/audit/not-a-guid` renders `NotFoundPage` with **no network request**; a well-formed unknown GUID renders `NotFoundPage` from the server's `404`, and never the word "forbidden".
8. A detail screen pretty-prints both payloads, renders `"[redacted]"` literally and unstyled, and shows the explicit no-change sentence — not `{}` — where a value is `null`.
9. `Success`, `Denied` and `Failure` render three distinct colours **and** three distinct words, and the same `StatusChip` still renders `Active`, `Suspended`, `Invited` and `Departed` unchanged elsewhere.
10. Requesting `pageSize=999` renders a pager consistent with the 50 the server returned; narrowing a filter while on a high page renders "Back to the first page", not "no results".
11. No export, download, print, copy-all, delete, edit, acknowledge, resolve, unredact or show-original control exists on either screen, enabled or disabled; and no relative timestamp, no `fetch`, no `localStorage`, no `dangerouslySetInnerHTML` and no `import.meta.env` appears anywhere under `frontend/src/slices/audit/`.
12. No screen renders a `NaN`, an `undefined` where a role label belongs, an `[object Object]` from a payload string, a raw role integer, or the word "Client".
