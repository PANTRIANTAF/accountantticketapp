# Identity Screens — Accountant Management

The two Office roles differ in **exactly four powers**, all reserved to `AccountantAdmin`
([../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §1): creating a Customer, suspending or
reactivating a Customer, managing Accountant accounts, and reading the audit log. The third is this document, and
the only one of the four that is a whole screen; it reaches the UI as **six** endpoints under `/api/accountants/*` —
one list read (§4), one invite (§5) and four row actions (§6). The rest live in [CustomersScreens.md](CustomersScreens.md) and
[AuditScreens.md](AuditScreens.md). Do not invent a fifth difference and do not collapse any (matrix §12 rule 7).

An `AccountantUser` reaches the same screen and gets a **strictly narrower response body** — id and display name
only — because the reason they may see the list at all is that assigning a ticket requires knowing who exists. One
route serves two DTOs, and handling that is the hardest thing here: read §2 before writing code. "Accountant Admin"
is written in full throughout; "Admin" alone is banned ([../../00-Glossary.md](../../00-Glossary.md)).

**Scope boundary.** Session bootstrap (`GET /api/auth/me`), login, logout, forced password change, password reset
and invitation acceptance belong to [../LoginArchitecture.md](../LoginArchitecture.md). If you are writing a password
field in this file you are in the wrong file — with one exception, a link, in §7.

**Documents that govern this one, in precedence order.** Where any of them disagrees with this document, **they win
and this document is wrong** — fix this document, do not code around it.

- [../../README.md](../../README.md) — *Locked platform decisions*, *Conflict precedence*
- [../../00-Glossary.md](../../00-Glossary.md) — "Accountant Admin"; never "Admin", never "User" as a role
- [../../01-DomainModel.md](../../01-DomainModel.md) §2 — an Accountant is a `UserAccount` with no Employee link
- [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §1, §2, §11, §12 — normative; §2 *is* this screen's specification
- [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §2.3, §2.6, §3.1, §3.2, §6.1, §7.1, §8.3, §10.1 — a bare `§` below means this file
- [../LoginArchitecture.md](../LoginArchitecture.md) — everything under `/api/auth/*`

---

## 0. Role coverage, and what lives in LoginArchitecture.md instead

| Role | `/accountants` | Sees | May do |
|---|---|---|---|
| `AccountantAdmin` (AA) | Yes | Detail shape: name, login email, role, status, created, last sign-in | Invite, suspend, reactivate, promote, demote |
| `AccountantUser` (AU) | Yes | **Summary shape: id and display name only** | Nothing — there is nothing to act on |
| `CustomerAdmin`, `Employee` | No | — | `RequireRole` renders `AccessDeniedPage` (§4.3) |

`/profile` is reachable by all four roles (§7). Owned by [../LoginArchitecture.md](../LoginArchitecture.md) and never
duplicated here: `POST /api/auth/login`, `/logout`, `/request-password-reset`, `/complete-password-reset`,
`/accept-invitation`, `/change-password` (its §3 also owns the forced-change gate), and `GET /api/auth/me`. The
invitation this screen *sends* is completed on `/accept-invitation`, which is **contract**: `TokenLinks.cs` builds
that URL and mails it, so renaming it breaks every invitation already in somebody's inbox (§4.5).

---

## 1. Endpoints this slice consumes

Read against `Slices/Identity/IdentityEndpoints.cs`. **`/list` is a `GET` with query parameters; the other five are
`POST` with a JSON body.** Do not "correct" either (§2.3 rule C).

| Route | Verb | Request | Response | Roles | Notes |
|---|---|---|---|---|---|
| `/api/accountants/list` | **GET** | `?pageNumber&pageSize` (`int?`, omissible) | **Two shapes — §2** | AA, AU | `403` for CA/EMP. **No status filter**: `Suspended` and `Invited` rows always appear |
| `/api/accountants/invite` | POST | `{ email, displayName, role }` | `AccountantDetailDto`, status **`201`** | AA | `409` duplicate, `422` validation. `Location` names the *list*, not the new row |
| `/api/accountants/suspend` | POST | `{ userAccountId }` | `AccountantDetailDto` | AA | `404`; `422` from three causes (§6) |
| `/api/accountants/reactivate` | POST | `{ userAccountId }` | `AccountantDetailDto` | AA | `404`; `422` from two causes |
| `/api/accountants/promote` | POST | `{ userAccountId }` | `AccountantDetailDto` | AA | `404`; `422`. `AccountantUser` → `AccountantAdmin` only |
| `/api/accountants/demote` | POST | `{ userAccountId }` | `AccountantDetailDto` | AA | `404`; `422` from three causes. `AccountantAdmin` → `AccountantUser` only |

**A. The body key is `userAccountId`** — not `id`, not `accountantId`. `AccountIdRequestDto` has one property, so
`{ id: ... }` binds to `Guid.Empty` and returns `404 "Accountant not found."` for a row visibly on screen, which
reads as a stale list rather than a typo.

**B. `invite` returns `201`.** `http.ts` branches on `response.ok`, so do not treat a non-`200` 2xx as an error and
do not follow the `Location` header.

**C. None of the six declares `.Produces<ProblemDetails>(401)` and all six can return it** — authentication comes
from the `CurrentUser` parameter, whose factory throws. Handle `401` per §2.3 rule H regardless.

**D. There is no get-single and no update endpoint**, so do not create an `['identity','accountants','detail',id]`
key: nothing can populate it.

---

## 2. The two response shapes of `/api/accountants/list`

The most important section in this document.

The route declares `.Produces<PaginatedResponse<AccountantDetailDto>>()`. `ListAccountantsHandler` is declared
`Task<object>` and branches on the caller's role: an `AccountantAdmin` gets `PaginatedResponse<AccountantDetailDto>`;
an **`AccountantUser` gets `PaginatedResponse<AccountantSummaryDto>`**, whose absent fields are absent from the JSON
entirely, because `System.Text.Json` serialises the runtime type.

| Field | Detail (AA) | Summary (AU) | Wire type |
|---|:--:|:--:|---|
| `id` | yes | yes | `string` (GUID) |
| `displayName` | yes | yes | `string` |
| `loginEmail` | yes | **key absent** | `string` |
| `role` | yes | **key absent** | **`number`** `0`–`3` (§10.1) |
| `status` | yes | **key absent** | **`string`**: `"Invited" \| "Active" \| "Suspended"` |
| `createdAt` | yes | **key absent** | `string`, `DateTimeOffset`, carries an offset |
| `lastLoginAt` | yes | **key absent** | `string \| null` |

The envelope (`pageNumber`, `pageSize`, `totalCount`, `totalPages`, `items`) is identical in both. The split is
normative — matrix §2: *"Return names and identifiers only — not email addresses, login history, or status detail."*
`AccountantDtos.cs` says why it is two types rather than one with nulls: *"a type that has no LoginEmail property
cannot leak one, whereas a handler that must remember to null it out will one day forget."*

**File:** `frontend/src/slices/identity/types.ts` — `AccountantSummary` (`id`, `displayName`) and
`AccountantDetail extends AccountantSummary` with the five further fields typed exactly as the table above, each
commented with the C# record it mirrors. **File:** `frontend/src/slices/identity/api.ts` — `listAccountants` returns
`Promise<PaginatedResponse<AccountantDetail> | PaginatedResponse<AccountantSummary>>`; the union is deliberate,
because an un-narrowed caller cannot read `status` without a compile error.

**File:** `frontend/src/slices/identity/screens/AccountantListScreen.tsx`

```tsx
// Compare, never test for truthiness: AccountantAdmin is 0 and 0 is falsy.
const isAccountantAdmin = session.role === UserRole.AccountantAdmin;
const rows = page.data?.items ?? [];
return isAccountantAdmin
  ? <AccountantAdminTable rows={rows as AccountantDetail[]} page={page.data} />
  : <AccountantNameTable rows={rows as AccountantSummary[]} page={page.data} />;
```

**A. Branch on `session.role`.** The server's branch is `user.Role == UserRole.AccountantAdmin`; mirroring that exact
condition is the only discrimination that cannot drift.

**B. Never sniff for a field.** `if ('loginEmail' in row)` is wrong in a way that passes review: `lastLoginAt` is
legitimately `null` for anyone who has never signed in, and every optional field the API grows later shares that
property — **an optional field that happens to be null looks exactly like the narrow shape.** Sniffing gives the
Accountant Admin the name-only rendering for a never-signed-in colleague, on some rows and not others.

**C. Two components, not one full of `?.`.** A single table rendering `row.status ?? '—'` shows an `AccountantUser`
an em-dash column, telling them a field exists and is withheld; two tables mean the narrow view has **no** status
column. The narrow view is not a filtered wide view — if the UI is filtering for security, the server has already
leaked it (matrix §12 rule 2; §6.2 rule A).

> **The `.Produces<T>` on this route is actively misleading**, documenting the richer shape for both callers.
> `ListAccountantsHandler`: *"it does not change what is serialised, and it must not be used to infer the response
> shape for a non-Admin caller."* §2.6 names this route as one of two known-wrong declarations, and a generated
> client would be wrong in exactly this place — item 6 in
> [../BACKEND_CHANGES_REQUIRED.md](../BACKEND_CHANGES_REQUIRED.md). Until then the hand-written union is the
> contract. Open question in §10.

---

## 3. Routes and screens

| SPA path | Screen | Roles |
|---|---|---|
| `/accountants` | `AccountantListScreen` | AA, AU |
| `/profile` | `ProfileScreen` | AA, AU, CA, EMP |

Reproduced from §4.1, not invented: there is no `/accountants/new` and no `/accountants/:id`, which is the first
reason inviting is a dialog (§5).

---

## 4. Screen: Accountant list (`/accountants`)

**File:** `frontend/src/slices/identity/screens/AccountantListScreen.tsx`

### 4.1 Layout

```
  AccountantAdmin                                        [ Invite Accountant ]
  ────────────────────────────────────────────────────────────────────────────
  Name              Email               Role              Status     Last sign-in
  Grace Hopper      grace@office.local  Accountant User   (Invited)  —          [...]
  Jane Doe  (you)   jane@office.local   Accountant Admin  (Active)   2 Sep 2026 [...]
  ────────────────────────────────────────────────────────────────────────────
                                           Rows per page: 15    1-2 of 2    < >
```

For an `AccountantUser`: one `Name` column, no row menu, no *Invite* button, the same pager, and the subtitle
"Names only. Account details are managed by an Accountant Admin." That subtitle is mandatory — without it the narrow
screen reads as broken rather than scoped. `[...]` is a row `IconButton` opening a `Menu` of §6's four actions, each
with an `aria-label` (§8.4 item 4). Both tables are `PaginatedTable` (§8.2, §8.3) — never `Table` +
`TablePagination` assembled here.

### 4.2 Data and query keys

**File:** `frontend/src/slices/identity/queries.ts` — keys follow `[sliceName, resource, ...discriminators]` (§3.1),
so `accountantKeys.list` is `['identity', 'accountants', 'list', { pageNumber, pageSize }]`. The page parameters
MUST be in the key, or every page shares one entry and the table shows page 1's rows under page 3's pager. The
three-segment prefix `['identity', 'accountants', 'list']` is every mutation's blast radius. The session key
`['identity', 'session']` belongs to `shared/auth` and is only read here.

Wrap it in `usePaginatedQuery`, so the clamp trap is handled in one place (§2.4 item 6): `pageSize` is clamped to 50
server-side, silently, with a `200`, so render the pager from `response.pageSize`. And never
`enabled: isAccountantAdmin` — both roles may call this endpoint, and expressing a permission by disabling a query
(forbidden by §3.2 rule B) would show an `AccountantUser` an empty table instead of the names they are entitled to.

### 4.3 Affordances by role

`can()` decides which buttons to draw and nothing else (§6.1). The action names are the exact six strings in
`IdentityActionCatalogue.cs`.

| Affordance | Action | AA, other row | AA, **own row** | AU |
|---|---|:--:|:--:|:--:|
| *Invite Accountant* | `InviteAccountant` | shown | shown | hidden |
| *Suspend* | `SuspendAccountant` | when `status === 'Active'` | **hidden** | hidden |
| *Reactivate* | `ReactivateAccountant` | when `status === 'Suspended'` | n/a | hidden |
| *Promote to Accountant Admin* | `PromoteAccountant` | when `role === UserRole.AccountantUser` | n/a — already one | hidden |
| *Demote to Accountant User* | `DemoteAccountant` | when `role === UserRole.AccountantAdmin` | **hidden** | hidden |
| The row menu itself | — | shown | draw no button when it would be empty | hidden |

The own row is `row.id === session.userId`, compared case-insensitively because the server's guard is
(`RequireNotSelf`, `StringComparison.OrdinalIgnoreCase`) — a `"D"`-versus-`"N"` GUID format mismatch that silently
never matches is the bug that comment exists to prevent. Label it `Jane Doe (you)`: a hidden action with no
explanation reads as a bug, and the label is the explanation.

> **`role === 0` is `AccountantAdmin`, and `0` is falsy.** `if (row.role)` is `false` for **every** Accountant Admin
> in the table, so that test puts *Promote* on every Accountant Admin and *Demote* on nobody. Always compare:
> `row.role === UserRole.AccountantAdmin` (§10.1).

### 4.4 States

| State | Render |
|---|---|
| First load | `Skeleton` rows inside `PaginatedTable`; header and pager stay put (§7.4) |
| `items: []`, `totalCount: 0` | Cannot happen — an Active Accountant Admin always exists (§6). Treat as a data fault |
| `items: []`, `totalCount > 0` | Ran past the end: `EmptyState` with "Back to the first page" (§3.3 item 2) |
| Query `403` | `AccessDeniedPage` — reachable only if `RequireRole` and `can.ts` disagree, a client bug (§6.2 rule B) |
| Mutation failure | `ErrorBanner` **above the table**, `role="alert"`, focus moved to it (§7.2, §8.4) |

### 4.5 Rules

**A. Render nothing raw.** `role` through `format/enums.ts` → "Accountant Admin" / "Accountant User"; `status`
through `StatusChip`, which owns the one colour map so `Suspended` is never green here and amber elsewhere. A cell
reading `0` has leaked the wire format (§10.1 item 2); one reading "Admin" has broken the glossary. `status` is a
string and `role` is a number **in the same row**, with nothing in the JSON marking the difference — so never
`Number(row.status)` or `String(row.role)`.

**B. `lastLoginAt` is nullable** — render "—", not "Invalid Date". Both timestamps carry an offset (§10.2 row 3);
format through `format/dates.ts`, one module.

**C. `Suspended` and `Invited` rows are always listed.** The handler applies no status filter, on purpose: *"an Admin
cannot reactivate somebody the list does not show."* An "active only" toggle would remove the only route to
reactivation.

**D. No sorting controls and no search box.** The server orders by `displayName` then `id` and accepts neither
parameter; a header that sorts the current page only is a lie about a server-paginated list.

---

## 5. Screen: Invite Accountant (dialog)

**File:** `frontend/src/slices/identity/components/InviteAccountantDialog.tsx`

Three fields — email address, display name, role — with *Cancel*, *Invite*, and an `ErrorBanner` inside the dialog.
**Why a dialog and not a route**, in order of weight: §4.1's route table has no `/accountants/new` row and that table
is normative; the form fetches nothing, so there is no state worth a bookmarkable URL; and the `409` duplicate is
best read with the list still on screen, because the answer to it is usually "she is already there, three rows up".

The role picker has **two options and only two**. `InviteAccountantHandler` rejects `CustomerAdmin` and `Employee`
with `422 "An invited accountant must be an Accountant Admin or an Accountant User."` — this endpoint invites
**Accountants** only. Customer-side accounts come from `POST /api/employees/invite` and `POST /api/customers/onboard`,
the only paths that can supply the mandatory `employee_id` and `customer_id`, so a four-option picker would offer two
choices that can only ever fail.

**File:** `frontend/src/slices/identity/screens/inviteAccountantSchema.ts` — limits mirrored from
`InviteAccountantHandler.cs` and `EmailNormalization.cs`. Stricter blocks legitimate input; looser produces the
unattached banner §7.3 describes.

```ts
export const inviteAccountantSchema = z.object({
  email: z.string().trim()
    .min(1, 'An email address is required.')
    .max(320, 'The email address must be at most 320 characters long.')
    // EmailNormalization.Require: one '@', not at either end, then MailAddress parses it. An
    // over-strict regex rejects addresses the server accepts, and nothing tells the user which
    // rule is the imaginary one.
    .refine((v) => v.split('@').length === 2 && !v.startsWith('@') && !v.endsWith('@'),
      'That email address is not valid.'),
  displayName: z.string().trim()
    .min(1, 'A display name is required.')
    .max(200, 'The display name must be at most 200 characters long.'),
  // A NUMBER on the wire. AccountantAdmin = 0, AccountantUser = 1.
  role: z.union([z.literal(UserRole.AccountantAdmin), z.literal(UserRole.AccountantUser)]),
});
```

**A. Send `role` as a number.** No `JsonStringEnumConverter` is registered, so `{"role":"1"}` is a **`400`** from
model binding before any handler runs — a bare "malformed body" naming no field. MUI's `Select` hands you a
`string`; convert in `onChange` or register with `valueAsNumber` (§10.1).

**B. Trim before submitting** (§9.3 rule E): a trailing space pushing a 200-character display name to 201 produces a
`422` about a limit the user appears to be within. **Do not lowercase the email** — the server keeps `LoginEmail` as
typed and normalises separately for uniqueness, so lowercasing changes what is displayed and what is mailed.

| Status | Cause | Treatment |
|---|---|---|
| `201` | Created | Close, `Snackbar` "Invitation sent to <email>.", invalidate the list |
| `409` | `An account already exists for '<normalised email>'.` | `ErrorBanner` in the dialog, verbatim, every field kept |
| `422` | Any message above, or the role rejection | `ErrorBanner` in the dialog, verbatim, never mapped to a field (§7.3) |
| `403` | Caller is not an Accountant Admin | A `can.ts` bug — the button should not have existed (§6.2 rule B) |

The address in the `409` **is** disclosed deliberately: the caller can already list every account.

### 5.1 The invitation token is never returned

`InviteAccountantHandler` puts the raw token in the notification's `EmailBody` only — not in `AccountantDetailDto`,
not in the `201` body, not in the `Location` header, not in the in-app notification `Body`.

- **No "copy invitation link".** There is nothing to copy, and a URL built from the account id would carry no token,
  fail on `/accept-invitation` with a `400`, and look like a broken invitation system. Never display, log or store a
  token; the only one the SPA touches is the `?token=` on `/accept-invitation`, LoginArchitecture.md's business.
- **The host comes from `App__BaseUrl`** (`TokenLinks.cs`). Misconfigured, every invitation points at the wrong host,
  **the UI cannot detect it**, and the only symptom is an invitee saying the link does not work. Flag it (§10); do
  not build a check.
- **There is no resend.** A second invite to the same address is a `409` whatever the target's status, and
  `reactivate` refuses `Invited` accounts (§10).

---

## 6. The four operations reserved to Accountant Admin

Invite is §5; these are the four row actions, all `POST` with `{ userAccountId }`. Every message is quoted from the
handler and rendered **verbatim** from `title` — never paraphrased, never attached to a control (§7.3).

| Operation | Preconditions the server enforces | `422` messages | `404` |
|---|---|---|---|
| `suspend` | Not self; not already `Suspended`; an Active Accountant Admin must remain **after** the write | `You cannot change your own role or status.` / `That account is already suspended.` / `At least one active Accountant Admin must remain.` | `Accountant not found.` |
| `reactivate` | Must be `Suspended`; `Invited` is refused | `That account has not accepted its invitation yet, so it cannot be reactivated.` / `That account is already active.` | `Accountant not found.` |
| `promote` | Must not already be an Accountant Admin. Allowed on `Suspended` and `Invited` | `That account is already an Accountant Admin.` | `Accountant not found.` |
| `demote` | Not self; must be an Accountant Admin; an Active Accountant Admin must remain **after** the write | `You cannot change your own role or status.` / `That account is not an Accountant Admin.` / `At least one active Accountant Admin must remain.` | `Accountant not found.` |

**A. No Accountant Admin may suspend or demote themselves.** `AccountInvariants.RequireNotSelf` throws
`422 "You cannot change your own role or status."` before any database work. `reactivate` and `promote` carry no such
guard — a suspended Accountant Admin cannot make the call at all, and self-promotion is already answered by "already
an Accountant Admin" — so hide exactly **two** actions on the own row (§4.3), not four.

**B. Hiding is an affordance, not a guarantee.** Handle that `422` anyway, in the banner above the table: §6.2 rule B
says the client's table decides which buttons to draw and nothing else, and a stale list, a second tab, or a `can.ts`
edited without this file all put the request on the wire. A `catch` that swallows it is forbidden.

**C. `At least one active Accountant Admin must remain.` is not the operator's mistake.** The server counts Active
Accountant Admins **after** the write, inside the transaction, and rolls back. Render it verbatim plus "Promote
another Accountant to Accountant Admin first." **Never pre-empt it by counting Accountant Admins client-side** — you
hold one page of a paginated list, so the count is wrong as soon as there are more Accountants than fit on a page, and
it would disable a legal action with nobody more powerful able to re-enable it (matrix §12 rule 6).

**D. `ConfirmDialog` for `suspend` and `demote` only** (§8.3), naming the consequence rather than asking "are you
sure?" — "Ada Byron will be unable to sign in until reactivated"; "Ada Byron will lose the ability to invite,
suspend, promote and demote Accountants". The other two grant capability and are reversible, so they submit directly.

**E. A role change is not immediate for the target.** `PromoteAccountantHandler`: *"The promoted user's own cookie
still says AccountantUser... the new permission arrives when they next sign in."* Up to eight hours, so the
`Snackbar` must say so — otherwise the operator watches nothing happen and does it again.

**F. `promote` on an `Invited` or `Suspended` account succeeds, deliberately** — the role is what they will be when
they can act, and `Status` governs whether they can act. Do not hide *Promote* on non-Active rows.

**G. Seed the cache from the response; do not refetch** (§3.2 rule D). All four return the full `AccountantDetailDto`
and there is no detail key to seed (§1 rule D), so patch the row in each cached page, then invalidate:

```ts
onSuccess: (updated) => {
  queryClient.setQueriesData<PaginatedResponse<AccountantDetail>>(
    { queryKey: ['identity', 'accountants', 'list'] },
    (page) => page && { ...page, items: page.items.map((r) => (r.id === updated.id ? updated : r)) },
  );
  queryClient.invalidateQueries({ queryKey: ['identity', 'accountants', 'list'] });
}
```

**H. `invite` invalidates only; it never splices.** Rows are ordered by `displayName` then `id`, so a new Accountant
may belong on a page you are not viewing, and inserting locally leaves `items.length` inconsistent with `totalCount`,
which makes the pager wrong.

**I. No optimistic updates** (§3.2 rule E): `suspend` and `demote` can be rolled back *after* the write appeared to
succeed, so an optimistic row would suspend the account, unsuspend it, and only then show the `422`. And
`retry: false` on all five (§3.4) — nothing is idempotent, so a retried `invite` is a spurious `409`.

---

## 7. Screen: Profile (`/profile`)

**File:** `frontend/src/slices/identity/screens/ProfileScreen.tsx`

Reachable by all four roles. Everything comes from `['identity', 'session']`, already cached by `GET /api/auth/me`.
**This screen issues no request of its own.**

| Belongs here | Does **not** belong here |
|---|---|
| Display name, from `SessionDto.displayName` | A display-name form — no endpoint (below) |
| Role as a glossary label, `SessionDto.role` through `format/enums.ts` | A login-email form, or the email as a field: `SessionDto` carries no email — *"it is not an account-detail response"* |
| *Change password* — a `<Link to="/change-password">` | A password form: [../LoginArchitecture.md](../LoginArchitecture.md) §3 owns it, and two forms posting to one endpoint means two sets of validation drifting apart |
| *Sign out*, also in the account menu (§5.1) | Anything about another person (§8) |

> **No endpoint changes your own display name or your own login email.** Across the six `/api/auth/*` and six
> `/api/accountants/*` routes, the only things any of them writes about the caller are the password and — on
> `/api/auth/accept-invitation` only, once, before first sign-in — an optional `displayName`. That is the sole write
> path for a name, and it is unreachable from an authenticated session. An Accountant Admin cannot rename another
> Accountant either: `AccountIdRequestDto` has one field. A "Save profile" button would have **nothing to POST to**,
> so show the name as read-only text with "To change this, ask an Accountant Admin". Open question in §10.
>
> **Still true as of 2026-09-02, and the exception is somebody else's screen.** `POST /api/employees/change-login-email`
> now exists, but it takes an `employeeId` and is granted to the two Accountant roles only — an Accountant changes a
> **Customer-side** person's sign-in address from the Employee detail screen ([EmployeesScreens.md](EmployeesScreens.md)
> §8.7). Nobody changes their own, and no Accountant's login email can be changed at all. `/profile` therefore gains
> nothing: **do not add a login-email field here**, editable or otherwise.

`SessionDto.customerId` is `null` for both Accountant roles and set for CA and EMP; never render a raw GUID — for a
Customer-side caller, link to `/my-customer`.

---

## 8. What these screens must NOT do

**A. Never show a password-reset affordance for another person** — not on a row, not in the row menu, not in the
invite dialog. Matrix §11:

> | Reset another person's password directly | **Nobody.** Re-issue an invitation or trigger a reset email instead. |

"Nobody" includes Accountant Admin, and `ChangePasswordRequestDto` is built so the mistake cannot be made: *"Two
fields, and there is deliberately no target user... A userId here would be the vulnerability, so the field must not
exist: you cannot forget to validate a parameter you never accepted."* There is no request shape such a button could
send. Nor may any of the following appear:

- **An email-change affordance**, for another person or for yourself: no endpoint exists (§7), and a form with no
  target is a support call waiting to happen.
- **Delete an Accountant.** Matrix §2: *"Delete an Accountant account — **Nobody.** Suspension only."* There is no
  `DELETE` route and no soft-delete flag.
- **A copyable invitation link or token** (§5.1).
- **Fields filtered or blanked for security** (matrix §12 rule 2; §6.2 rule A). If a field is on screen that should
  not be, the fix is a server change and a punch-list entry, not a CSS rule.
- **Audit entries.** Every §6 operation writes one, but reading them is `AccountantAdmin`-only on `/audit` and there
  is no per-account audit endpoint, so a "recent changes" panel cannot be built.
- **A swallowed `403` or `422`, or a polling list.** The unread-notification count is the app's only polling query
  (§3.2 rule H).

---

## 9. Behavioural cases

- [ ] As `AccountantAdmin`, `/accountants` shows name, email, role, status, last sign-in and *Invite Accountant*.
- [ ] As `AccountantUser`, names only, no *Invite* button, no row menu, none of the withheld keys in the body; as
      `CustomerAdmin`, `AccessDeniedPage` and no nav item.
- [ ] The own row is labelled `(you)` and offers neither *Suspend* nor *Demote*.
- [ ] Every Accountant Admin row offers *Demote* and none offers *Promote* — the `role === 0` trap is absent.
- [ ] Inviting sends `{"role":1}` as a number and returns `201`; a duplicate address renders the `409` verbatim with
      every field kept.
- [ ] Suspending the last Active Accountant Admin renders that `422` and the row is **unchanged** after a refetch.
- [ ] Reactivating an `Invited` account renders the invitation-not-accepted `422`; promoting one succeeds, shows an
      `Invited` chip beside "Accountant Admin", and the `Snackbar` mentions the next sign-in.
- [ ] Posting `suspend` with a `CustomerAdmin`'s id renders "Not found", never "forbidden".
- [ ] `pageSize=999` renders a pager consistent with the 50 the server returned.
- [ ] `/profile` makes no API request, links to `/change-password`, and offers no name or email edit.

---

## 10. Questions to flag if unclear

Flag these; do not answer them by inventing a behaviour.

- [ ] **Should `/api/accountants/list` always return the narrow shape?** One route serves two DTOs by role and
      `.Produces<T>` documents only the richer one (§2); two tables in the UI is the cost. Always returning
      `AccountantSummaryDto`, with a separate detail endpoint for the Accountant Admin, would make the declaration
      honest and a generated client correct — item 6 in
      [../BACKEND_CHANGES_REQUIRED.md](../BACKEND_CHANGES_REQUIRED.md), also open in §13.
- [ ] **PARTLY ANSWERED 2026-09-02 — is a display name or login email ever meant to be changeable after invitation?**
      The **login email** is, for a Customer-side person, by either Accountant role, through
      `POST /api/employees/change-login-email` ([EmployeesScreens.md](EmployeesScreens.md) §8.7 and
      [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §4). Still open for: **your own** login email
      (nobody), an **Accountant's** login email (nobody), and the **display name** (nobody, anywhere). If "yes
      eventually" for those, `/profile` and the row menu both need a design — and note the answer above was
      deliberately *not* self-service, so it is not the precedent for making one.
- [ ] **Is there a supported "resend invitation"?** A second invite is a `409` regardless of status and `reactivate`
      refuses `Invited` accounts, so a lost invitation has no recovery path (§5.1).
- [ ] **Should the operator be warned that a role change is not immediate?** §6 rule E requires a sentence in the
      `Snackbar`; confirm the wording.
- [ ] **Should `App__BaseUrl` be surfaced anywhere?** If it is wrong, every invitation this screen sends is broken
      and nothing in the UI reveals it.
- [ ] **Should a demoted Accountant Admin's open session be terminated?** There is no server-side session store, so
      today it is not — the cookie keeps the old role for up to eight hours.

---

## Files checklist

- [ ] `frontend/src/slices/identity/types.ts` — `AccountantSummary`, `AccountantDetail`, `InviteAccountantRequest` (§2)
- [ ] `frontend/src/slices/identity/api.ts` — six functions; `listAccountants` returns the union
- [ ] `frontend/src/slices/identity/queries.ts` — `accountantKeys`, `useAccountantList`, five mutations
- [ ] `frontend/src/slices/identity/screens/AccountantListScreen.tsx` — the role branch (§2)
- [ ] `frontend/src/slices/identity/screens/ProfileScreen.tsx` (§7), `screens/inviteAccountantSchema.ts` (§5)
- [ ] `frontend/src/slices/identity/components/AccountantAdminTable.tsx` — six columns plus the row menu
- [ ] `frontend/src/slices/identity/components/AccountantNameTable.tsx` — one column, no actions
- [ ] `frontend/src/slices/identity/components/InviteAccountantDialog.tsx` (§5), `AccountantRowMenu.tsx` (§4.3)
- [ ] `frontend/src/shared/format/enums.ts` — the `UserRole` → glossary-label map, if absent
- [ ] `frontend/src/shared/permissions/can.ts` — the six `Identity` rows, matching `IdentityActionCatalogue.cs`
- [ ] Nothing else: no password form, no token display, no Accountant detail route, no `detail` query key

---

## Success criteria

Each is verified by running the app, not by reading the code.

1. As an `AccountantAdmin`, `/accountants` lists every Accountant — `Suspended` and `Invited` included — with the role
   as a glossary label and the status as a `StatusChip`.
2. As an `AccountantUser` it renders one column, and that response carries no `loginEmail`, `role`, `status`,
   `createdAt` or `lastLoginAt` key on any row.
3. No code path inspects a row for the presence of a field, and neither view renders a placeholder for a withheld one.
4. The own row is labelled `(you)` and offers neither *Suspend* nor *Demote*; every Accountant Admin row offers
   *Demote* and none offers *Promote*.
5. The invite dialog has two role options and sends `role` as a JSON number.
6. Each distinct `4xx` message in §5 and §6 can be provoked and appears verbatim in an `ErrorBanner` — in the dialog
   for invite, above the table for the four row actions.
7. Provoking the last-Active-Accountant-Admin `422` leaves the row unchanged after a refetch.
8. Every successful row action updates its row from the mutation response with no second `GET` in the network tab,
   then invalidates the list once.
9. A promotion's success message states the change takes effect at the target's next sign-in.
10. Nothing in the app offers to reset another person's password, change any Accountant's email or display name,
    delete an Accountant, or copy an invitation link.
11. `/profile` issues no network request and its *Change password* control navigates to `/change-password`.
12. The six `Identity` rows in `can.ts` match `IdentityActionCatalogue.cs` exactly — same names, same role sets.
