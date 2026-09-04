# Audit Screens

Reading the audit log is one of **exactly four powers reserved to `AccountantAdmin`**
([../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §1, §10) — the others are creating a Customer, suspending or reactivating one, and managing Accountant accounts. The reason shapes the screen: the log records what **Accountant Users** did, so an `AccountantUser` who could read it would be reading the oversight of the people most able to cause harm (matrix §10). Do not relax it, do not add a narrower variant for `AccountantUser`, and do not invent a fifth role distinction (matrix §12 rule 7).

The log is **append-only and read-only forever**. Three routes exist and all three are reads. Matrix §10: *"Write to the audit log — **Nobody.** Written only by the application"*; *"Edit or delete an audit entry — **Nobody.** No API exists for this."* There is no create, edit, delete, purge, archive or export endpoint and by design there never will be. An *Export*, *Delete*, *Edit* or *Acknowledge* control here is not merely unimplementable — it misrepresents the guarantee the table exists to provide.

One unusual property, because it will look like a bug: **this screen shows the consequences of the reader's own denied actions.** Every permission denial writes an audit row before the `403` is thrown (`Shared/Authorization/PermissionChecker.cs`) and nothing exempts this slice, so a curious `AccountantUser` who types `/audit` writes a row into the log they were trying to read. That is a genuinely useful property, not an accident.

**Documents that govern this one, in precedence order.** Where any of them disagrees with this document, **they win and this document is wrong** — fix this document, do not code around it.

- [../../README.md](../../README.md) — *Locked platform decisions*, *Conflict precedence*
- [../../00-Glossary.md](../../00-Glossary.md) — *Audit Entry*: "never edited, never deleted"; "Accountant Admin" in full, never "Admin"
- [../../01-DomainModel.md](../../01-DomainModel.md) §8, §9.2 — what is audited; indefinite retention
- [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §1, §10, §12 — normative; §10 *is* this screen's permission spec
- [../../Slices/Audit/IMPLEMENTATION_PLAN.md](../../Slices/Audit/IMPLEMENTATION_PLAN.md) §1, §4.0 rule F, §6, §8, §12 — record shape and redaction
- [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §2.3, §3.1, §3.3, §4.3, §6.1, §7.1, §8.2, §8.4, §10.1, §10.2

---

## 0. Role coverage — one role, and what the other three see

| Role | `/audit` | Nav item (§5.2) | What happens |
|---|---|---|---|
| `AccountantAdmin` (AA) | Yes | *Audit log* | The whole log, every Customer, no scope filter |
| `AccountantUser` (AU) | No | **None** | `RequireRole` renders `AccessDeniedPage` (§4.3 rule A) |
| `CustomerAdmin` (CA) | No | **None** | Same |
| `Employee` (EMP) | No | **None** | Same |

`ReadAuditLog` is granted to `AccountantAdmin` alone (`AuditActionCatalogue.cs`, one row), so `can()` is `false` for the other three and §5.2 gives them **no nav item at all** — nothing to grey out, the entry is absent.

**A. A direct navigation gets a denial page, not a redirect** (§4.3 rule A). Someone who typed `/audit` deserves to be told the page is not for them; a silent bounce to `/customers` reads as a broken link and they try again — which, per rule B, writes a second audit row.

**B. The server denies and *audits* the denial regardless of what the router did** (§4.3 rule B, §6.2). All three handlers open with `RequireAsync(user, "ReadAuditLog")` and `PermissionChecker` writes before throwing. The row an `AccountantUser` generates by typing `/audit` is exactly `action = PermissionDenied`, `targetKind = None`, `targetId = ""`, `outcome = Denied`, `afterValue = {"Action":"ReadAuditLog"}`, with their own actor id, role, IP and user agent.

**C. Reading the log is *not* audited.** Both read handlers write nothing on success — *"a log that grew on every read would be a log nobody could read."* An Admin browsing leaves no trace; a denied non-Admin leaves one. Do not add a client-side "record that I looked" call.

---

## 1. Endpoints this slice consumes

Read against `AccountantApp.Api/Slices/Audit/AuditEndpoints.cs`. Three routes, all reads.

| Route | Verb | Request | Response | Roles | Notes |
|---|---|---|---|---|---|
| `/api/audit/search` | **POST** | `SearchAuditLogRequestDto` body — eight filters plus paging (§3.2) | `PaginatedResponse<AuditEntryDto>` | AA | `403`; `422` from five validations (§3.2) |
| `/api/audit/detail` | GET | `?auditEntryId=<guid>` | `AuditEntryDetailDto` | AA | `403`; `404` "Audit entry not found."; **`400`** if the GUID does not parse |
| `/api/audit/action-codes` | GET | none | `AuditActionsResponseDto` | AA | `403`. Three string lists (§5) |

**A. `/api/audit/search` is a `POST` that reads, and that is correct.** Eight optional filters plus paging are too many for a query string and date ranges in one invite encoding bugs; the endpoint file says so. **Do not "fix" it to a `GET`** — §2.3 rule C names it as one of the API's deliberate `POST` reads, and changing the verb produces a `405` with nothing in the body to explain it.

**B. The other two are `GET` with the id in the query string**, not the path and not a body. Build it with `URLSearchParams` in `api.ts` (§2.5).

**C. None of the three declares `.Produces<ProblemDetails>(401)` and all three can return it** — authentication comes from the `CurrentUser` parameter, whose factory throws `401`. Handle it per §2.3 rule H regardless of the missing metadata.

**D. There is no other audit surface in the API.** No per-entity history, no export, no full-text route, no `IAuditApi.QueryAsync` for another slice to wrap (plan §8 rule 5). If a screen needs something not in this table, it cannot be built.

---

## 2. Routes and screens

| SPA path | Screen | Roles |
|---|---|---|
| `/audit` | `AuditSearchScreen` | AA |
| `/audit/:auditEntryId` | `AuditEntryScreen` | AA |

Reproduced from §4.1, not invented. Both sit inside `RequireRole roles={[UserRole.AccountantAdmin]}`. `UserRole.AccountantAdmin` is `0` and `0` is falsy — compare, never test for truthiness (§10.1).

---

## 3. Screen: Audit search (`/audit`)

**File:** `frontend/src/slices/audit/screens/AuditSearchScreen.tsx`

### 3.1 Layout

```
  Audit log                                                        [ Refresh ]
  ────────────────────────────────────────────────────────────────────────────
  ▼ Filters  (3 active: Action=LoginFailed · Outcome=Denied · From 1 Sep 2026)
  ┌──────────────────────────────────────────────────────────────────────────┐
  │ Actor user id [________]  Action [LoginFailed ▾]  Outcome [Denied ▾]     │
  │ Target kind [Employee ▾]  Target id [____] (needs a target kind)         │
  │ Customer id [________]    From [01/09/2026 00:00]  To [02/09 23:59]      │
  │                                          [ Clear all ]     [ Search ]    │
  └──────────────────────────────────────────────────────────────────────────┘
  Occurred (exact)       Actor      Role            Action            Outcome
  02 Sep 2026 14:33:12   3f9a…c1    AccountantUser  PermissionDenied  (Denied)
  02 Sep 2026 14:31:02   3f9a…c1    AccountantUser  LoginSucceeded    (Success)
  ────────────────────────────────────────────────────────────────────────────
                                   Rows per page: 15   1-15 of 412,338   < >
```

Collapsed, the panel header still names the active filters. `PaginatedTable` (§8.2, §8.3) — never `Table` + `TablePagination` assembled here. A row is a real link to `/audit/:auditEntryId`, so middle-click and *Open in new tab* work: comparing two entries side by side is how this screen is used.

### 3.2 The filter panel

**File:** `frontend/src/slices/audit/components/AuditFilterPanel.tsx`

Every field of `SearchAuditLogRequestDto`. All optional, combined with **AND**; all absent means "the whole log, most recent page first".

| Field | Wire type | Control | Empty means |
|---|---|---|---|
| `actorUserId` | `string \| null` | `TextField` | Any actor. **Exact, case-sensitive equality** — a partial id matches nothing |
| `action` | `string \| null` | `Select` from `actionCodes.actions` (§5) | Any action. Off-catalogue is a **`422`** |
| `targetKind` | `string \| null` | `Select` from `actionCodes.targetKinds` (§5) | Any kind. Off-catalogue is a **`422`** |
| `targetId` | `string \| null` | `TextField`, **disabled until `targetKind` is set** | Any target of that kind. Sent alone it is a **`422`** |
| `customerId` | `string \| null` (GUID) | `TextField` | Any Customer **and** the entries that have none (§6 rule C) |
| `outcome` | `string \| null` | `Select` from `actionCodes.outcomes` (§5) | Any outcome. Off-catalogue is a **`422`** |
| `from` | `string \| null` (ISO) | `DateTimePicker` | No lower bound. **Inclusive** (`>=`) |
| `to` | `string \| null` (ISO) | `DateTimePicker` | No upper bound. **Inclusive** (`<=`) |
| `pageNumber` | `number` | the pager | Server default `1` |
| `pageSize` | `number` | the pager, max 50 | Server default `15`; clamped, not rejected (§3.3 item 1) |

**A. Every filter is part of the query key** (§3.1), or two filter sets share one cache entry and the table shows the wrong rows under the right heading — invisible on an audit tool, because the rows still look plausible.

**B. `from` after `to` must be caught client-side.** The server answers `422 "'From' must not be later than 'To'."` — one sentence with **no field attached** (§7.3), renderable only as a banner. Caught here it outlines the offending picker, which is the only mechanism that can:

```ts
export const auditFilterSchema = z.object({ /* the ten fields above */ })
  .refine((f) => !f.from || !f.to || new Date(f.from) <= new Date(f.to),
    { path: ['to'], message: 'The "to" date must not be earlier than the "from" date.' });
```

**C. The panel is collapsible and must name the active filters when collapsed.** A collapsed panel reading only *Filters* lets the reader take a filtered table for the whole log — a serious misreading for an audit tool, because they conclude "this never happened" from rows that were merely excluded. Render the count and each active filter as a removable `Chip` in the collapsed header.

**D. Draft filters are React state; applied filters are the query key.** Nothing fetches until *Search* is pressed. Keying off the draft fires a `POST` against the largest table in the database on every keystroke.

**E. Applied filters are mirrored into the `/audit` URL search params** (`useSearchParams`) and the key is derived from the URL. Otherwise *Back* from an entry re-runs an unfiltered search and the investigator loses their place in a four-hundred-thousand-row table; it also makes a search shareable, which is how one Admin hands an investigation to another.

**F. Applying or clearing any filter resets `pageNumber` to `1`**, or a new filter is applied while on page 7, the result has two pages, and the empty table (§3.3 item 2) reads as "nothing matched".

**G. Send `null`, not `''`, for an untouched field** (§9.3 rule F). The handler treats whitespace as absent so `''` happens to work — but `customerId` is a `Guid?`, and `""` is a **`400`** from model binding, not a `422`, and a `400` carries no sentence worth showing.

**H. Send timestamps as ISO strings with an explicit offset** (`toISOString()`). A bare local datetime is bound against the *server's* offset, silently shifting the window and dropping hours of evidence at the boundary.

### 3.3 Data and query keys

**File:** `frontend/src/slices/audit/queries.ts` — keys are `[sliceName, resource, ...discriminators]` (§3.1), exported as an `auditKeys` object so no screen builds an array literal.

| Query | Key |
|---|---|
| A search, one filter set and page | `['audit', 'search', { ...filters, pageNumber, pageSize }]` |
| One entry | `['audit', 'detail', auditEntryId]` |
| The filter catalogues | `['audit', 'actionCodes']` |

**A. `usePaginatedQuery` only** (§3.2 rule G), so the clamp trap is handled once (§2.4 item 6): `pageSize` is clamped to 50 with a `200` and no complaint. Render the pager from `response.pageSize`.

**B. No mutations, therefore no invalidations.** Nothing in the app writes to `['audit', …]`. *Refresh* is `refetch()` on the current key and nothing more.

**C. Never poll** (§3.2 rule H). An append-only log's cached page is never *wrong*, only missing rows newer than itself — which is what the visible *Refresh* is for.

**D. Keep `retry: false` for `4xx`** (§3.4). Retrying the `403` asks the server to deny you three times and writes **three** audit rows, all of them about you.

**E. This is the one table in the app likely to have a large `totalCount`** — retention is indefinite (`01-DomainModel.md` §9.2) and there is no purge (plan §12 item 2). So the over-run case of §3.3 item 2 (`items: []` with `totalCount > 0`) is likelier here than anywhere else: a filter narrows the result while the pager sits on page 200. Render "back to the first page", never "no results".

### 3.4 States

| State | Render |
|---|---|
| First load | `Skeleton` rows inside `PaginatedTable`; header, filters and pager stay put (§7.4) |
| Refetch with data | Keep the rows, subtle indicator. Never blank a table being read |
| `items: []`, `totalCount: 0` | `EmptyState` naming the active filters — §3.5 rule C |
| `items: []`, `totalCount > 0` | Over-ran the end: `EmptyState`, "Back to the first page" (§3.3 item 2) |
| `422` | `ErrorBanner` above the table, `title` verbatim, every filter value untouched (§7.3, §9.3 rule D) |
| `403` | `AccessDeniedPage` — reachable only if `RequireRole` and `can.ts` disagree, a client bug (§6.2 rule B) |
| `500` | `ErrorBanner` with the `traceId` in small text — the only status that shows it (§7.1) |
| `actionCodes` failed | The three `Select`s degrade to `TextField`s (§5 rule C) |

### 3.5 Rules

**A. No sorting controls.** The server orders `occurredAt DESC, id DESC` and accepts no sort parameter. A clickable header that sorts the 15 rows on screen is a lie about a table of hundreds of thousands, and it invites the conclusion that the top row is the newest of its kind.

**B. Render `totalCount` with thousands separators** via `Intl.NumberFormat` (§10.2). `412338` read as a page count is how a reader concludes the log is corrupt.

**C. An empty result must state what was filtered** — "No audit entries match these filters", the active filter list, and *Clear all*. A bare "No results" is the sentence the slice plan warns about: an investigator reads it as *this never happened*.

**D. Never fetch the detail of a row to fill a column.** `AuditEntryDto` omits `beforeValue`/`afterValue` deliberately — up to 8 KB each and the only personal data in the table, so *"a list endpoint that carried them would make every page of the audit log a bulk export of tax and payroll values."* Fifteen detail calls per page rebuilds that, one request at a time.

**E. `outcome` and `actorRole` are both `string`s** — §10.1's string-versus-integer asymmetry, biting twice on this screen. See §6 rules B and F.

---

## 4. Screen: Audit entry detail (`/audit/:auditEntryId`)

**File:** `frontend/src/slices/audit/screens/AuditEntryScreen.tsx`

```
  ‹ Back to audit log
  Audit entry                                                       (Denied)
  ────────────────────────────────────────────────────────────────────────────
  Occurred   02 Sep 2026 14:33:12   Actor  3f9a41c8-…-b1c1
  Action     PermissionDenied       Role at the time  AccountantUser
  Target     None  —                Customer  —        Source IP  10.0.3.7
  User agent Mozilla/5.0 (Windows NT 10.0; Win64; x64) …
  ────────────────────────────────────────────────────────────────────────────
  Before     (no before value — this entry records no change to existing data)
  After      { "Action": "ReadAuditLog" }
```

**A. Validate the path parameter as a GUID before fetching.** A malformed `auditEntryId` is a **`400`** from parameter binding whose body says nothing a reader can act on. Render `NotFoundPage` for anything that is not a GUID and never issue the request.

**B. `404` renders `NotFoundPage`** — not "forbidden", not "deleted". Entries are never deleted (plan §12 item 1), so a `404` means the id is wrong.

**C. `enabled` is for the id, not for permission** (§3.2 rule B): do not fetch until the parameter has parsed.

**D. *Back to audit log* uses router history, not a hardcoded `/audit`**, so the filters and page survive (§3.2 rule E).

**E. No next/previous entry navigation.** The API has no adjacency concept and the endpoint takes one id. Synthesising it from the last search page would be silently wrong the moment the reader arrived from a link.

---

## 5. Filter dropdowns come from the server, not from a constant

`GET /api/audit/action-codes` returns three ordinal-sorted lists: `actions` from `AuditActions.All` (**45** codes today, reflection-built so the count moves when a constant is added), `targetKinds` from `AuditTargets.All` (8), `outcomes` from `AuditOutcome.All` (3).

**A. Never hardcode any of the three in TypeScript**, even though `AuditActions` is a list of C# constants that would be trivial to copy. The server adds an action code **in the same commit as the feature that emits it**, so a hardcoded client list silently omits the newest codes — which are exactly the ones an Admin is investigating, because the newest feature is the one that just misbehaved. The omission has no symptom: the dropdown simply lacks the value, and the investigator concludes the action does not exist.

**B. Cache it hard.** The catalogue is compiled into the binary, so it changes only on deploy — and a deploy reloads the SPA anyway:

```ts
export const useAuditActionCodes = () => useQuery({
  queryKey: auditKeys.actionCodes, queryFn: getAuditActionCodes,
  staleTime: Infinity, gcTime: Infinity,   // Compile-time constants; a refetch can only return the
});                                        // same body, on a screen that is already query-heavy.
```

**C. If it fails, fall back to a free-text `TextField` per filter — never an empty `Select`.** An empty dropdown makes the whole search unusable: there is no way to filter by action, kind or outcome at all, and the failure reads as "there are no actions". A text field with helper text *"an unrecognised value is rejected"* keeps every search reachable by an Admin who knows the code.

**D. Render the search's `422`s verbatim** (§7.3 item 2) — one names this endpoint: *"Fetch /api/audit/action-codes for the catalogue."* Do not paraphrase a sentence that tells the reader exactly what to do.

> **The slice plan §6.3 specifies two lists; the shipped `AuditActionsResponseDto` returns three.** The code is a superset: `Outcomes` was added because the search `422`s an unrecognised outcome, so a client holding its own copy can `422` itself. This document depends on the third list. See §9.

---

## 6. Reading an audit entry — the field-by-field guide

`AuditEntryDto` (list) and `AuditEntryDetailDto` (detail) are **separate types**, not a base class and a subclass, deliberately: *"the separation that keeps the payload off the list endpoint would depend on nobody ever projecting the wrong type."* Mirror them as two interfaces in `frontend/src/slices/audit/types.ts`.

| Field | Wire type | Means | Render |
|---|---|---|---|
| `id` | `string` (GUID) | This entry's own id | Not a column — the row's link target |
| `actorUserId` | `string` (≤100) | **Who.** A `UserAccount` id — or, for a failed login, whatever identifier was attempted, which may match no row anywhere | Monospace, verbatim, middle-truncated in the table. **Never a name** — rule A |
| `actorRole` | `string` (≤30) | The role **at the time**. `"Unknown"` for unauthenticated writes | Glossary label from the *string* — rule B |
| `customerId` | `string \| null` | The Customer concerned; `null` when none was | `"—"`, never "All Customers" — rule C |
| `action` | `string` | The catalogue code, one of the 45 in `AuditActions` | Verbatim, monospace — rule D |
| `targetKind` | `string` | What kind of thing; `"None"` is a real value, not a gap | Verbatim; link only per rule E |
| `targetId` | `string` (≤100, may be `""`) | The target's id. Not unique across kinds | Monospace; `"—"` when empty |
| `outcome` | `string` | `"Success" \| "Denied" \| "Failure"` | `StatusChip` — rule F |
| `occurredAt` | `string`, carries an offset | When, UTC at write time | **Exact** timestamp — rule G |
| `sourceIp` | `string` (≤45, may be `""`) | The connection's remote address | Monospace; `"—"` when empty — rule H |
| `userAgent` | `string` (≤512, may be `""`) | The raw header, truncated at 512 | Verbatim, wrapped, **never parsed** — rule I |
| `beforeValue` | `string \| null` | **Detail only.** JSON text, already redacted | Rules J and K |
| `afterValue` | `string \| null` | **Detail only.** JSON text, already redacted | Rules J and K |

**A. There are no display names in this slice, anywhere.** Plan §8 rule 3: *"This slice never resolves an identifier to a name. The audit reader shows `actor_user_id`, not 'Maria Papadopoulou'"* — resolving one would make `Audit` depend on `Identity`, which it may not. The plan suggests the UI join client-side; **do not attempt it today**, because `/api/accountants/list` is paginated and Office-only and no endpoint maps a `UserAccount` id to a Customer-side person's name. Render the raw id. See §9.

**B. `actorRole` is a `string` here while `role` everywhere else in the API is an integer** — §10.1's asymmetry in one field. `AuditApi` stores `user.Role.ToString()`, so the value is `"AccountantAdmin"`, not `0`. Through `format/enums.ts`'s integer map it yields `undefined`; `Number(actorRole)` yields `NaN`. Map the *string* to its glossary label, and render an unrecognised value — including `"Unknown"` — verbatim rather than blank, because a role this UI does not know is itself information.

**C. `customerId: null` means "no Customer was involved", not "every Customer".** Inviting an Accountant, a failed login, a ticket-type edit: none belongs to a Customer. Rendering `null` as "All Customers" inverts the meaning of the most sensitive column on the screen.

**D. Render `action` as the code, not a prettified sentence.** "Permission denied" is friendlier, but the code is what the reader pastes back into the `action` filter and greps the source for — and the filter rejects the humanised form with a `422`.

**E. Link a target only where an SPA route exists**: `Customer` → `/customers/:id`, `Employee` → `/employees/:id`, `TicketType` → `/ticket-types/:id`. Never for `Ticket`, `Document`, `Notification` or `None` — those screens do not exist (§0.1, §12 item 1), so the link renders `NotFoundPage` and reads as a broken audit log.

**F. `StatusChip`, one colour map (§8.3), and `Denied` is not `Failure`.** Extend the shared component rather than writing a local chip; a local chip is how `Denied` becomes red here and grey elsewhere.

| `outcome` | Colour | Means |
|---|---|---|
| `Success` | `success` | The operation completed |
| `Denied` | `warning` | **Authorization refused it.** The system behaved correctly; somebody attempted what they may not do |
| `Failure` | `error` | **The operation errored.** It was permitted and went wrong |

Collapsing the two into one colour destroys the distinction the log exists to record: forty denials from one account in a minute is an intrusion attempt, forty failures is an outage, and they need opposite responses. The chip shows the **word** as well as the colour (§8.4).

**G. Audit entries are the one place in this app where *exact* time matters.** Format `occurredAt` in full — date, time and **seconds** — through `format/dates.ts` in the browser's local timezone (§10.2). **Never a relative "3 hours ago"**, not even as a secondary label: it is useless in an investigation, it changes on every re-render, and two entries forty minutes apart both read "about an hour ago". Seconds are not decoration — the sort is `occurredAt DESC, id DESC` precisely because one transaction writes several entries in the same second.

**H. Do not label `sourceIp` "the user's IP address".** Behind Caddy, `RemoteIpAddress` is the **proxy's** address unless forwarded headers are configured, which they are not (plan §13 question 3). *Source IP* is honest either way; the stronger claim is not.

**I. Never parse `userAgent` into "Chrome on Windows".** It is truncated at 512 characters, so the parse may run on a mutilated string — and a parsed guess replaces evidence with an inference in the one table whose entire value is that it holds what actually arrived.

**J. `beforeValue`/`afterValue` are `string`s containing JSON, not objects.** The columns are `jsonb` mapped to `string?`, so the body carries a quoted string: `"beforeValue": "{\"Name\":\"Acme\"}"`. `Object.keys(entry.beforeValue)` gives you the character indices of a string. Parse, pretty-print, and tolerate failure — an unparseable payload is still evidence:

```ts
export function parsePayload(raw: string | null): { pretty: string } | { raw: string } | null {
  if (raw === null) return null;   // No change recorded. Not an error, and not "{}".
  try { return { pretty: JSON.stringify(JSON.parse(raw), null, 2) }; } catch { return { raw }; }
}
```

`null` means *this entry records no change to existing data* — a create has no before, a read has neither (plan §12 item 5). Render that sentence; do not render `{}` and do not synthesise an empty object to make the two panels symmetrical.

**K. Redacted values arrive already redacted; render them as-is.** Redaction happens **at write time**, in `Slices/Audit/Application/Redaction.cs`: any property whose name *contains* `password`, `hash`, `salt`, `token`, `secret`, `apikey`, `sessionid` or `cookie`, at any nesting depth, was replaced with the literal `"[redacted]"` before insert. The column never held the secret. Therefore:

1. **Do not attempt to un-redact.** There is nothing to recover; the plaintext was never written.
2. **Do not hide, blank or filter out a `"[redacted]"` value.** It is *information*: it says this property changed and deliberately not to what. Redaction rule 3 exists for that — *"Knowing that a field changed without knowing to what is useful; not knowing it changed is not."* Blanking it destroys the information.
3. **Do not reformat it** into "hidden", "•••••" or an icon. It is the literal stored value, and an Admin comparing screen to database must see the same string.
4. **Two other payload shapes are real** and must render as explicit notes, never as an empty panel: `{"truncated": true, "length": <n>}` (over the 8 KB cap) and `{"unserialisable": true, "type": "<name>"}` (the server could not serialise it and logged the failure). Both mean "the payload is gone, the row is intact"; an empty panel says "no change was recorded", which is a different and false statement.
5. **Never add a client-side redaction pass.** §6.2 rule A: if something is on screen that should not be, the server leaked it and a UI filter conceals a live bug. It would also be wrong here — over-redaction is deliberate server-side (a property named `TokenCount` is redacted), so a client list is a second, divergent policy.

> **The plan's §2.1 table calls `AuditEntryDto` "the read model … with redaction applied"**, which reads as read-time redaction. The code never redacts on read; `AuditEntryDetailDto`'s comment says so: *"already redacted at write time … because the column must never have held a secret in the first place."* The behaviour is right and the plan's wording is stale. See §9.

---

## 7. What these screens must NOT do

**A. No export, no CSV, no *Print*, no *Download*, no *Copy all*.** No endpoint exists, and this is the rule most likely to be "helpfully" violated. Do **not** build a client-side CSV from the search response: it is **one page** of at most 50 rows out of a `totalCount` in the hundreds of thousands, so the file would be silently incomplete while looking authoritative — the worst possible property for an audit export, because nothing in the file reveals what is missing. Nor may you loop the pager to assemble one: hundreds of unbounded `POST`s against the largest table in the database, from a browser tab. Whether an export endpoint should exist is §9's first question.

**B. No delete, no edit, no annotate.** Matrix §10: *"Edit or delete an audit entry — **Nobody.**"* No route, no soft-delete flag, no `updated_at`, no purge job (plan §1, §12). Do not render a disabled *Delete* either — a greyed control implies the operation exists and is merely unavailable to you.

**C. No "resolve", "acknowledge", "flag", "assign" or "reviewed by" state.** An audit entry has no such concept — thirteen columns and none of them a status a reader can set. Held client-side it would survive no refresh and no second Admin would see it. This screen is a reader, not a case tracker.

**D. No free-text search across the whole log.** No endpoint. `actorUserId` and `targetId` are **exact-equality** filters, not `contains`, and there is no full-text route over the payloads — plan §13 question 1 keeps it open precisely because a GIN index on two `jsonb` columns of the largest table is not a decision to make in passing. A box that searched only the current page would tell an investigator "not found" about a log that contains the row.

**E. Do not correlate entries into a timeline beyond what the filters give you.** No grouping by "session", no "related entries", no chains inferred from adjacent timestamps. Nothing in the record links two rows — no correlation id, no request id, no `traceId` (§9) — so every such view is the UI's guess presented as the log's finding. The supported way to see a sequence is `actorUserId` plus a date range, which is honest because the reader chose it.

**F. Never filter or blank a field for security** (§6.2 rule A, matrix §12 rule 2), and never poll (§3.2 rule H).

**G. Never render this data on another slice's screen.** There is no per-entity audit endpoint, so a "recent activity" panel on a Customer or Employee screen cannot be built — and if it could, it would put `AccountantAdmin`-only data on a screen four roles can reach.

---

## 8. Behavioural cases

- [ ] As `AccountantAdmin`, `/audit` loads the most recent page, newest first, with no filters set.
- [ ] As `AccountantUser`, there is no *Audit log* nav item, `/audit` renders `AccessDeniedPage`, **and** a new `PermissionDenied` / `Denied` row for that account appears when an Admin searches. Same for `CustomerAdmin` and `Employee`.
- [ ] The three `Select`s are populated from `/api/audit/action-codes` — 45 actions, 8 kinds, 3 outcomes — with exactly **one** request for them per session.
- [ ] Blocking `/api/audit/action-codes` leaves all three filters usable as text fields; no dropdown renders empty.
- [ ] A `from` later than `to` is refused by the form, with the message on the *to* picker, and **no** request is sent. A `targetId` with no `targetKind` is impossible — the field is disabled.
- [ ] Collapsing the filter panel still shows which filters are active and their values.
- [ ] Clicking an entry, then *Back*, returns to the same page of the same filtered search.
- [ ] `/audit/not-a-guid` renders `NotFoundPage` with no network request; a well-formed unknown GUID renders `NotFoundPage` from the server's `404`.
- [ ] A `PermissionDenied` entry's detail shows `afterValue` as `{ "Action": "..." }`, target `None`, and the no-before-value sentence rather than `{}`.
- [ ] An entry whose payload held a `NewPasswordHash` property renders `"[redacted]"` visibly, and no code path removes or restyles it.
- [ ] Timestamps show date, time and seconds; nothing on either screen reads "ago".
- [ ] `Success`, `Denied` and `Failure` render three distinct colours and three distinct words.
- [ ] `pageSize=999` renders a pager consistent with the 50 the server returned, and narrowing a filter while on a high page number renders "back to the first page".
- [ ] Nothing on either screen offers export, print, delete, edit or acknowledge.

---

## 9. Questions to flag if unclear

Flag these; do not answer them by inventing a behaviour.

- [ ] **Is an export endpoint wanted?** An auditor will ask within a week, and §7 rule A forbids faking it client-side because a paginated export is silently incomplete. A server-side export — with its own action code, since exporting the log is itself worth recording — is the shape to consider.
- [ ] **Should the log be searchable by `traceId`?** A user reports a `500`, the `ErrorBanner` shows them a `traceId` (§7.1), and there is **no way to find the corresponding audit row**: the record has no correlation column and the search no such filter. This is the largest gap in the screen's investigative value.
- [ ] **Should the reader see names instead of ids?** Plan §8 rule 3 says the UI should join client-side, but no endpoint maps a `UserAccount` id to a display name for a Customer-side person (§6 rule A). Either an id-to-name endpoint is needed or the raw ids stand.
- [ ] **Does `sourceIp` record the proxy or the real client?** Plan §13 question 3 is open; until it is answered the column may be uniformly the Caddy container's address, which makes it worthless without saying so.
- [ ] **Should the plan's §2.1 wording be corrected?** It describes `AuditEntryDto` as redacted on read; redaction is at write time (§6 callout). Behaviour right, documentation stale.
- [ ] **`AuditActionsResponseDto` returns three lists where plan §6.3 specifies two** (§5 callout). Confirm `outcomes` stays; this document depends on it.
- [ ] **[../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §2.3 rule C says "four are `POST`" and then lists five**, `/api/audit/search` among them. The list is right, the count is wrong; correct the governing document, not this one.

---

## Files checklist

- [ ] `frontend/src/slices/audit/types.ts` — `AuditEntry`, `AuditEntryDetail`, `AuditActionCodes`, `AuditSearchRequest` (§6)
- [ ] `frontend/src/slices/audit/api.ts` — `searchAuditLog` (POST), `getAuditEntry`, `getAuditActionCodes`
- [ ] `frontend/src/slices/audit/queries.ts` — `auditKeys`, `useAuditSearch`, `useAuditEntry`, `useAuditActionCodes` (§3.3, §5)
- [ ] `frontend/src/slices/audit/screens/AuditSearchScreen.tsx` (§3) and `AuditEntryScreen.tsx` (§4)
- [ ] `frontend/src/slices/audit/screens/auditFilterSchema.ts` — ten fields plus the date-range `refine` (§3.2 rule B)
- [ ] `frontend/src/slices/audit/components/AuditFilterPanel.tsx` — collapsible, active-filter chips (§3.2 rule C)
- [ ] `frontend/src/slices/audit/components/AuditEntryTable.tsx` — columns per §3.1, rows link to the detail
- [ ] `frontend/src/slices/audit/components/AuditPayloadPanel.tsx` — `parsePayload`, the four payload shapes (§6 rules J, K)
- [ ] `frontend/src/shared/components/StatusChip.tsx` — **extend** with the three outcomes (§6 rule F)
- [ ] `frontend/src/shared/format/dates.ts` — the exact date-time-seconds formatter (§6 rule G)
- [ ] `frontend/src/shared/permissions/can.ts` — the one `Audit` row, `ReadAuditLog`, matching `AuditActionCatalogue.cs`

Not to be written: any export or download helper, any mutation, any `['audit', …]` invalidation, any client-side redaction, any relative-time formatter, any per-entity audit component in another slice.

---

## Success criteria

Each is verified by running the app, not by reading the code.

1. `/audit` as an `AccountantAdmin` lists entries newest first, with an exact timestamp including seconds and a `totalCount` carrying thousands separators.
2. *Audit log* appears in the nav for `AccountantAdmin` only, and `/audit` typed by each of the other three roles renders `AccessDeniedPage`.
3. After criterion 2, searching `action = PermissionDenied`, `outcome = Denied` shows one new row per attempt, with the attempting account's id and its role at the time.
4. The three dropdowns are populated from `/api/audit/action-codes`, requested exactly once however many searches are run; with that endpoint blocked, every filter is still usable and none is empty.
5. A `from` later than `to` produces a field-level error and no request; every other `422` in §1 is unreachable from the UI, and provoking one by hand leaves all filter values intact.
6. Collapsing the filter panel leaves the active filters visible, and clicking an entry then pressing *Back* restores the same filtered page.
7. A detail screen pretty-prints both payloads, renders `"[redacted]"` literally, and shows the explicit sentence — not `{}` — where a value is `null`.
8. `Denied` and `Failure` differ in colour **and** in words.
9. No relative timestamp appears anywhere under `slices/audit`.
10. No export, download, print, delete, edit, acknowledge or resolve control exists on either screen, enabled or disabled.
11. Narrowing a filter while on a high page number renders "back to the first page", and `/audit/<malformed>` issues no request at all.
12. The one `Audit` row in `can.ts` matches `AuditActionCatalogue.cs` exactly — `ReadAuditLog`, `AccountantAdmin` alone, nothing extra on either side.
13. No screen renders a `NaN`, an `undefined` where a role label belongs, a raw `[object Object]` from a payload string, or the word "Client".
