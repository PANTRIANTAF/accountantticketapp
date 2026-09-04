# Identity Screens — UI Implementation Plan

An ordered build plan for the **accountant-management half** of the Identity slice: the accountant list, the five mutations that act on it, and the read-only profile screen. Build the steps in order. Where something is unclear, flag it in §12 — do not invent a behaviour.

**Build position.** This plan runs **after** `UI/Plans/00-Foundation/IMPLEMENTATION_PLAN.md` and after nothing else. Phase 0 builds all of `frontend/src/shared/`, the route table, and every `/api/auth/*` screen. This plan writes inside `frontend/src/slices/identity/` and adds two rows to `frontend/src/routes.tsx`. It touches nothing else, depends on no other slice, and creates **nothing** under `frontend/src/shared/`.

**Documents that govern this document, in precedence order**

| # | Document | Sections that bind this plan |
|---|---|---|
| 1 | `00-Glossary.md` … `04-Infrastructure.md` | 00 — "Accountant Admin", never "Admin" or "User" alone. 02 §1, §2, §11, §12. 04 §1–3 — same origin, no base-URL variable, no CORS |
| 2 | `App/GeneralAppArchitecture.md` | §8 — kebab-case routes, ids in the body, `ProblemDetails`, the pagination envelope |
| 3 | `UI/GeneralUIArchitecture.md` | §1.2 tree, §1.4 imports, §1.5 dependencies, §2 client, §3 Query, §4 routing, §5 shell, §6 `can()`, §7 errors, §8 MUI, §9 forms, §10 wire formats |
| 4 | `UI/LoginArchitecture.md` | §1 bootstrap, §3 forced change, §4–5 reset and invitation, §6 logout, §8 role enum |
| 5 | `UI/Screens/IdentityScreens.md` | All of it. It is the *what*; this is the *in what order* |
| 6 | `UI/Plans/00-Foundation/IMPLEMENTATION_PLAN.md` | The shared kernel and the `/api/auth/*` screens |
| 7 | This plan | Loses to all six above |

`UI/BACKEND_CHANGES_REQUIRED.md` is **not** normative. Item numbers are permanent but the bands are non-contiguous, so every citation below names its band. A bare `§` means `GeneralUIArchitecture.md`, matching `IdentityScreens.md`'s convention; `IdentityScreens §n` is the screen spec; Matrix is `02-AuthorizationMatrix.md`. C# citations are `file:line` against the tree of 2026-09-02.

---

## 0. Prerequisites

### 0.1 The shared kernel belongs to Phase 0 and may not be recreated here

Phase 0 builds all of `frontend/src/shared/`. This plan consumes it. If any of it is missing, stop and build Phase 0 — do **not** write a local substitute, because a second `can()` or a second `fetch` wrapper is the one defect no step below can detect.

Consumed: `api/http.ts` (§2.1 — the app's only `fetch`), `api/ApiError.ts` and `problemDetails.ts` (§2.2), `api/paginated.ts` (§3.3), `api/queryClient.ts` (§3.4), `auth/SessionProvider.tsx` + `useSession.ts` + `RequireSession.tsx` + `RequireRole.tsx` (§4.3), `permissions/can.ts` (§6.1), `hooks/usePaginatedQuery.ts`, `format/enums.ts` and `format/dates.ts` (§10), and from §8.3 `PaginatedTable`, `PageHeader`, `ConfirmDialog`, `StatusChip`, `ErrorBanner`, `EmptyState`, `LoadingRegion`, `AccessDeniedPage`.

> **`IdentityScreens.md`'s files checklist lists two `shared/` files** — `shared/format/enums.ts` and `shared/permissions/can.ts` — as Identity deliverables. `GeneralUIArchitecture.md` §1.2 and §1.4 rule A place both in Phase 0, and §3 outranks §5. Resolved the only way that cannot produce two copies: **Phase 0 creates them, step 0.3 verifies them.**

### 0.2 Login, change-password, reset and invitation acceptance are Phase 0's, not this plan's

`IdentityEndpoints.cs` registers **two `MapGroup` prefixes** from one file — `/api/auth` at line 28 and `/api/accountants` at line 94 — because authentication and Office administration are one slice's two jobs. The frontend split is by **prefix, not folder**, and it must be stated explicitly or a builder reading the folder name writes the login screen a second time.

| Routes | Owner | Screens |
|---|---|---|
| `GET /api/auth/me` and `POST /login`, `/logout`, `/change-password`, `/request-password-reset`, `/complete-password-reset`, `/accept-invitation` — **seven** | Phase 0, per `LoginArchitecture.md` | `LoginScreen`, `ChangePasswordScreen`, `ForgotPasswordScreen`, `ResetPasswordScreen`, `AcceptInvitationScreen` |
| `GET /api/accountants/list` and `POST /invite`, `/suspend`, `/reactivate`, `/promote`, `/demote` — **six** | **This plan** | `AccountantListScreen`, `ProfileScreen`, four components |

**A. If you are writing a password field, you are in the wrong plan.** The one exception is a link, not a field: `ProfileScreen`'s `<Link to="/change-password">` (step 7).

**B. The invitation this plan sends is completed on a Phase 0 screen.** `/accept-invitation` is contract, not choice — `Application/TokenLinks.cs` builds that URL and mails it. Step 6 sends; it never builds, shows, copies or logs the link.

**C. `/profile` is in scope** even though it renders `/api/auth/me` data: it is a screen inside the shell, Phase 0 builds none of those, and `IdentityScreens §7` specifies it. It issues no request of its own.

**D. Steps 1–3 extend three existing files; they never overwrite them.** `LoginArchitecture.md`'s checklist already puts `slices/identity/types.ts`, `api.ts` and `queries.ts` in Phase 0 — `SessionProvider` cannot bootstrap without `getSession`, which lives in this slice's `api.ts`. A `Write` that replaces `api.ts` deletes `getSession` and `login`, and the symptom is the whole app failing to start, nowhere near the accountant list. Read each file, append the named exports, leave the rest alone.

### 0.3 Verify these five facts before step 1

- [ ] `shared/format/enums.ts` exports `UserRole` as `AccountantAdmin: 0`, `AccountantUser: 1`, `CustomerAdmin: 2`, `Employee: 3` plus `ROLE_LABELS`. The order is the contract — `Shared/Auth/UserRole.cs:3-9` declares no explicit values.
- [ ] `shared/permissions/can.ts` holds exactly six `Identity` rows matching `IdentityActionCatalogue.cs:24-29`: `ListAccountants` → AA + AU; `InviteAccountant`, `SuspendAccountant`, `ReactivateAccountant`, `PromoteAccountant`, `DemoteAccountant` → AA only.
- [ ] `shared/api/paginated.ts` exports `PaginatedResponse<T>` with `pageNumber`, `pageSize`, `totalCount`, `totalPages`, `items`, and `DEFAULT_PAGE_SIZE = 15` / `MAX_PAGE_SIZE = 50` from `Shared/Pagination/PaginatedQuery.cs:7-12`, which **clamps**.
- [ ] `StatusChip` accepts `'Invited' | 'Active' | 'Suspended'` — the `UserAccount` vocabulary (`Core/UserAccount.cs:77-82`), which is **not** the Customer or Employee vocabulary.
- [ ] `useSession()` exposes `userId: string` and `role: UserRole`. `Application/SessionClaims.cs:51-56` builds `SessionDto` from `account.Id.ToString()`, so `userId` is a string, never a `Guid`.

---

## 1. The backend surface, verified against source

### 1.1 The six routes

| Route | Verb | Request | Response | Roles | Source |
|---|---|---|---|---|---|
| `/api/accountants/list` | **GET** | `?pageNumber&pageSize`, both `int?`, both omissible | **two shapes — §1.2** | AA, AU | `IdentityEndpoints.cs:108-121` |
| `/api/accountants/invite` | POST | `{ email, displayName, role }` | `AccountantDetailDto`, **`201`** | AA | `:96-106` |
| `/api/accountants/suspend` | POST | `{ userAccountId }` | `AccountantDetailDto` | AA | `:125-134` |
| `/api/accountants/reactivate` | POST | `{ userAccountId }` | `AccountantDetailDto` | AA | `:136-143` |
| `/api/accountants/promote` | POST | `{ userAccountId }` | `AccountantDetailDto` | AA | `:145-152` |
| `/api/accountants/demote` | POST | `{ userAccountId }` | `AccountantDetailDto` | AA | `:154-161` |

**A. `list` is a GET; the other five are POST.** Do not "correct" either (§2.3 rule C). The `list` suffix predicts the verb nowhere in this API.

**B. The body key is `userAccountId`.** `AccountIdRequestDto` has that one property (`Dtos/AccountantDtos.cs:49-52`), so `{ id }` binds `Guid.Empty` and returns `404 "Accountant not found."` for a row visibly on screen.

**C. `invite` returns `201` with `Location: /api/accountants/list`** (`:100`) — the list, not the new row. `http.ts` branches on `response.ok`, so `201` is success. Never follow the header; there is no detail route to follow it to.

**D. None of the six declares `.Produces<ProblemDetails>(401)`, and all six can return it** — authentication comes from the `CurrentUser` parameter, whose factory throws. Handle `401` per §2.3 rule H regardless.

**E. There is no get-single and no update route.** So there is no `['identity','accountants','detail',id]` key anywhere in step 3: nothing could populate it.

**F. Build the query string from numbers only.** `pageNumber`/`pageSize` are `int?`, defaulted in the lambda (`:112-113`) before `Normalize` runs; a non-numeric value is a bare model-binding `400`.

### 1.2 One route, two DTO shapes, one misleading `.Produces<>`

The most important fact in this plan, **verified in the handler**, not inferred from the spec:

- `ListAccountantsHandler.cs:43` — `public async Task<object> Handle(...)`.
- `ListAccountantsHandler.cs:77-85` — `if (user.Role == UserRole.AccountantAdmin)` returns `PaginatedResponse<AccountantDetailDto>`.
- `ListAccountantsHandler.cs:88-95` — the fall-through returns `PaginatedResponse<AccountantSummaryDto>`; its comment: *"The catalogue allows only the two Accountant roles through, so this branch is AccountantUser."*
- `IdentityEndpoints.cs:120` declares `.Produces<PaginatedResponse<AccountantDetailDto>>()` for **both** callers, and `:116-119` warns it *"must not be used to infer the response shape for a non-Admin caller."*

`System.Text.Json` serialises the runtime type, so for an `AccountantUser` the other five keys are **absent from the JSON** — not null, not empty: absent.

| Field | Detail (AA) | Summary (AU) | Wire type | Source |
|---|:--:|:--:|---|---|
| `id` | yes | yes | `string` (GUID) | `AccountantDtos.cs:34`, `:30` |
| `displayName` | yes | yes | `string` | `:35`, `:30` |
| `loginEmail` | yes | **absent** | `string` | `:36` |
| `role` | yes | **absent** | **`number`** `0`–`3` | `:37` |
| `status` | yes | **absent** | **`string`** `Invited\|Active\|Suspended` | `:38` |
| `createdAt` | yes | **absent** | `string`, carries an offset | `:39` |
| `lastLoginAt` | yes | **absent** | `string \| null` | `:40` |

The envelope is identical in both. The narrowing is normative — Matrix §2: *"Return names and identifiers only — not email addresses, login history, or status detail."* It is two types rather than one with nulls on purpose (`AccountantDtos.cs:26-28`): *"a type that has no LoginEmail property cannot leak one, whereas a handler that must remember to null it out will one day forget."*

**How the client handles both — three mandatory rules.**

**A. `api.ts` returns a union**, not the wide type with optionals. An un-narrowed caller reading `.status` must fail to compile; that compile error is the mechanism (step 2).

**B. Narrow on `session.role`, never on field presence** (step 4). The server's discriminator is `user.Role == UserRole.AccountantAdmin` (`:77`); mirroring that exact condition is the only test that cannot drift. `if ('loginEmail' in row)` passes review and is wrong — `lastLoginAt` is legitimately `null` for anyone who has never signed in, so an optional field that happens to be null looks exactly like the narrow shape.

**C. Two table components, not one full of `?.`** (step 5). A single table rendering `row.status ?? '—'` tells an `AccountantUser` that a field exists and is withheld. The narrow view is not a filtered wide view (§6.2 rule A, Matrix §12 rule 2): if the UI is filtering for security, the server has already leaked it.

Recorded as punch-list item **6**, band ***Degrading***, and open in `IdentityScreens §10`. Until it is answered the hand-written union is the contract — and do not generate a client from the `.Produces<>` (punch-list item **9**, ***Degrading***).

### 1.3 No Admin may act against their own account

**Verified:** `Application/AccountInvariants.cs:78-91`, `RequireNotSelf(Guid targetId, CurrentUser user)`, throws `AppException("You cannot change your own role or status.", 422)`, comparing `targetId.ToString()` with the caller's claim under **`StringComparison.OrdinalIgnoreCase`** (`:89`) — deliberately, to defeat a GUID format mismatch that would otherwise silently never match. It is called from exactly two handlers, **before any database work**:

| Handler | Self guard | Line |
|---|---|---|
| `SuspendAccountantHandler` | `RequireNotSelf` | `:45` |
| `DemoteAccountantHandler` | `RequireNotSelf` | `:42` |
| `ReactivateAccountantHandler` | **none, deliberately** | `:38-40` — a suspended Admin cannot make the call at all |
| `PromoteAccountantHandler` | **none, deliberately** | `:38-39` — self-promotion is answered by "already an Accountant Admin" |

**The mirroring affordance rule.** Where `row.id.toLowerCase() === session.userId.toLowerCase()`, hide exactly **two** actions — *Suspend* and *Demote* — and no others. Hiding four invents a guard the server does not have. Label the row `(you)` (`IdentityScreens §4.3`): a hidden action with no explanation reads as a bug.

**Hiding is an affordance, never a guarantee.** Handle the `422` anyway, in the banner above the table — a stale list, a second tab, or a `can.ts` edited without this plan all put the request on the wire. Swallowing it in a `catch` is forbidden (§6.2 rule B).

**Consequence worth stating, because it looks like a missing feature:** an Accountant Admin viewing this list is necessarily `Active` and `AccountantAdmin`, so on their own row all four actions evaluate to hidden. **Draw no row-menu button at all** rather than an empty menu.

---

## 2. Step 1 — `types.ts`

**File:** `frontend/src/slices/identity/types.ts`

Append to what Phase 0 wrote (§0.2 rule D). Each interface names the C# record it mirrors, so the two can be diffed (§2.5). `UserRole` is imported from `shared/format/enums.ts` and never redeclared.

```ts
/** Mirrors AccountantSummaryDto — Application/Dtos/AccountantDtos.cs:30 */
export interface AccountantSummary {
  id: string;          // C# Guid; lowercase hyphenated string on the wire
  displayName: string;
}

/** Mirrors AccountantDetailDto — AccountantDtos.cs:33-40. AccountantAdmin only. */
export interface AccountantDetail extends AccountantSummary {
  loginEmail: string;
  role: UserRole;              // NUMBER on the wire. 0 = AccountantAdmin, and 0 is falsy
  status: AccountantStatus;    // STRING on the wire
  createdAt: string;           // DateTimeOffset, carries an offset
  lastLoginAt: string | null;  // null for anyone who has never signed in
}

/** UserAccount.Status — Core/UserAccount.cs:77-82. NOT the Customer vocabulary. */
export type AccountantStatus = 'Invited' | 'Active' | 'Suspended';

/** Mirrors InviteAccountantRequestDto — AccountantDtos.cs:6-18 */
export interface InviteAccountantRequest {
  email: string;
  displayName: string;
  role: UserRole;              // sent as a number; a string is a 400 from model binding
}

/** Mirrors AccountIdRequestDto — AccountantDtos.cs:49-52. The key is userAccountId. */
export interface AccountIdRequest { userAccountId: string }
```

### 2.1 Four ways this step goes wrong

1. **One interface with five optional fields.** `loginEmail?: string` compiles, reads as tolerant, and destroys the compile error §1.2 rule A exists to produce. Two interfaces, with `AccountantDetail extends AccountantSummary`.
2. **Typing `role` as a string union.** No `JsonStringEnumConverter` is registered anywhere (punch-list item **4**, ***Degrading***), so `role` is `0`–`3` while `status` in the adjacent field is a string — two conventions in one row, with nothing in the JSON marking the difference.
3. **Reusing another slice's status vocabulary.** `Customer.status` is `Active | Suspended` and a Customer is never `Invited`; Employee status is `Active | Departed`. `AccountantStatus` is its own three-word type (§10.1).
4. **Overwriting the file.** `SessionDto` and the auth request types are already in it.

### What this step does NOT do, and why

No `AccountantListItem` alias, no response wrapper, no re-export of `PaginatedResponse`. There is no detail DTO distinct from the list row: the four row actions return the same `AccountantDetailDto` the list returns (`Application/IdentityMapper.cs:14-21`), which is exactly what makes step 3's cache patch possible.

---

## 3. Step 2 — `api.ts`

**File:** `frontend/src/slices/identity/api.ts`

Append six functions. No React, no hooks, no Query — a plain typed wrapper readable against `IdentityEndpoints.cs` line by line (§2.5).

```ts
export function listAccountants(params: { pageNumber: number; pageSize: number }):
  Promise<PaginatedResponse<AccountantDetail> | PaginatedResponse<AccountantSummary>> {
  const query = new URLSearchParams({
    pageNumber: String(params.pageNumber),
    pageSize: String(params.pageSize),
  });
  return get(`/api/accountants/list?${query}`);
}

export const inviteAccountant = (body: InviteAccountantRequest): Promise<AccountantDetail> =>
  post('/api/accountants/invite', body);

// suspend, reactivate, promote, demote: identical shape, one line each.
export const suspendAccountant = (userAccountId: string): Promise<AccountantDetail> =>
  post('/api/accountants/suspend', { userAccountId });
```

**A. The union return type is the point of the signature.** Do not widen it to the detail type "because the Admin case is the interesting one", and do not add a generic that lets the caller choose.

**B. Each mutation takes a bare `string` and builds `{ userAccountId }` here**, so the key `AccountantDtos.cs:51` requires is written once, beside the endpoint, rather than four times at four call sites.

**C. Four one-line functions, not one `accountantAction(action, id)` helper.** A helper that interpolates the route hides four operations with four distinct precondition sets behind one name, and the `422` messages in §5.3 stop being traceable to a call site.

**D. No headers, no `credentials`, no base URL, no `import.meta.env`.** `http.ts` owns all of it (§2.3 rules A–B). There is no `VITE_API_URL` and never will be, and CORS is never configured (04-Infrastructure §2).

### 3.1 Three ways this step goes wrong

1. **POST for `/list`, or GET for a mutation** — either is a `405`. `list` is `MapGet` at `IdentityEndpoints.cs:108`.
2. **Sending `pageSize` above 50 and trusting it.** `PaginatedQuery.Normalize` clamps to `[1,50]` and returns `200` (punch-list item **17**, ***Drift***). This file may send what it is given; steps 3 and 4 render from `response.pageSize`.
3. **String concatenation instead of `URLSearchParams`.** Harmless here, but §2.5 fixes the idiom so every slice's `api.ts` reads alike.

### What this step does NOT do, and why

No `getAccountant`, `updateAccountant`, `deleteAccountant` or `resendInvitation` — none exists (§1.1 rule E). Matrix §2: *"Delete an Accountant account — **Nobody.** Suspension only."* A second invite to the same address is a `409` whatever the target's status (`InviteAccountantHandler.cs:83-86`), so there is no resend to wrap.

---

## 4. Step 3 — `queries.ts`

**File:** `frontend/src/slices/identity/queries.ts`

Append one key factory, one query hook, five mutation hooks. Screens import hooks and never `api.ts` (§3.2 rule A).

```ts
export const accountantKeys = {
  all: ['identity', 'accountants'] as const,
  list: (pageNumber: number, pageSize: number) =>
    ['identity', 'accountants', 'list', { pageNumber, pageSize }] as const,
};
```

### 4.1 The list hook

`useAccountantList({ pageNumber, pageSize })` wraps `shared/hooks/usePaginatedQuery.ts` (§3.3) so the clamp is handled once for the whole app, and returns step 2's union unchanged — narrowing is the screen's job.

- **The page parameters MUST be in the key**, or every page shares one cache entry and page 1's rows appear under page 3's pager (§3.1).
- **Never `enabled: isAccountantAdmin`.** Both Accountant roles may call this route, and expressing a permission by disabling a query is forbidden (§3.2 rule B) — it would show an `AccountantUser` an empty table instead of the names Matrix §2 entitles them to.
- **No `refetchInterval`.** The unread-notification count is the app's only polling query (§3.2 rule H).

### 4.2 The five mutation hooks

`useInviteAccountant`, `useSuspendAccountant`, `useReactivateAccountant`, `usePromoteAccountant`, `useDemoteAccountant`.

**A. The four row actions patch the row in every cached page, then invalidate.** All four return the full `AccountantDetailDto`, so refetching discards a response you already hold (§3.2 rule D). There is no detail key to seed (§1.1 rule E), so the seed is a prefix patch:

```ts
onSuccess: (updated) => {
  queryClient.setQueriesData<PaginatedResponse<AccountantDetail>>(
    { queryKey: accountantKeys.all },
    (page) => page && { ...page, items: page.items.map((r) => (r.id === updated.id ? updated : r)) },
  );
  queryClient.invalidateQueries({ queryKey: accountantKeys.all });
}
```

**B. `invite` invalidates only; it never splices.** Rows are ordered by `displayName` then `id` (`ListAccountantsHandler.cs:69-70`), so a new Accountant may belong on a page you are not viewing, and a local insert leaves `items.length` inconsistent with `totalCount`, which makes the pager wrong.

**C. No optimistic updates on any of the five** (§3.2 rule E), and here for a specific reason: `suspend` and `demote` run `RequireAnActiveAdminRemainsAsync` **after** `SaveChangesAsync`, inside the transaction (`SuspendAccountantHandler.cs:70`, `DemoteAccountantHandler.cs:59`, `AccountInvariants.cs:30-47`), so a refused write is rolled back after appearing to succeed. An optimistic row would suspend, unsuspend, then show the `422`.

**D. `retry: false`, inherited from `queryClient.ts`.** Nothing here is idempotent and there is no idempotency key, so a retried `invite` is a spurious `409`.

**E. Invalidate `accountantKeys.all`, never the whole cache** (§3.2 rule C). That three-segment prefix is every mutation's correct blast radius.

### 4.3 Four ways this step goes wrong

1. **Seeding a detail key** — it has no fetcher, will hold a row forever, and no screen reads it.
2. **`setQueryData` on one key** instead of `setQueriesData` on the prefix, which leaves the other visited pages stale.
3. **Typing the patch callback as the union.** Only an Admin can call these four, so only Admin pages are ever patched; type it `PaginatedResponse<AccountantDetail>`.
4. **Touching `['identity','session']`.** That key is `shared/auth`'s. Demoting somebody does not change *your* session, and nothing here re-fetches `/api/auth/me`.

---

## 5. Step 4 — `AccountantListScreen.tsx`

**File:** `frontend/src/slices/identity/screens/AccountantListScreen.tsx`

Route `/accountants`, inside the shell, AA and AU (§4.1). It owns three things and delegates the rest: the role branch, the page state, and the mutation error banner.

```tsx
// Compare, never test truthiness: AccountantAdmin is 0 and 0 is falsy.
const isAccountantAdmin = session.role === UserRole.AccountantAdmin;
```

### 5.1 Structure

1. `PageHeader`, title "Accountants". The action slot holds *Invite Accountant* only when `can(session.role, 'InviteAccountant')`. For an `AccountantUser` the slot is empty and the subtitle is **mandatory**: "Names only. Account details are managed by an Accountant Admin." Without it the narrow screen reads as broken rather than scoped (`IdentityScreens §4.1`).
2. `ErrorBanner` for the row-action error, above the table, `role="alert"`, focus moved to it on failure (§7.2, §8.4).
3. The branch: `AccountantAdminTable` or `AccountantNameTable`, each given the page envelope.
4. `InviteAccountantDialog`, mounted for an Accountant Admin only, open state in React state.
5. `Snackbar` for successes — the only toasts in this application (§5.3).

### 5.2 States

| State | Render |
|---|---|
| First load | `Skeleton` rows inside `PaginatedTable`; header and pager stay put (§7.4) |
| Refetch with data | Keep the rows, show a subtle progress indicator. Never blank a table being read |
| `items: []`, `totalCount: 0` | **Cannot happen** — Matrix §2 guarantees an Active Accountant Admin always exists. Treat as a data fault, not a state to design |
| `items: []`, `totalCount > 0` | The page ran past the end: `EmptyState` with "Back to the first page" (§3.3) |
| Query `403` | `AccessDeniedPage`. Reachable only if `RequireRole` and `can.ts` disagree — a client bug (§6.2 rule B) |
| Mutation failure | `ErrorBanner` above the table, verbatim from `error.title` |

### 5.3 The `4xx` map for the five mutations

Render **verbatim from `title`**, never paraphrased and never attached to a control (§7.3; punch-list item **5**, ***Degrading***, since `ProblemDetails` carries no field map). Every message is quoted from its handler.

| Status | Message | Source |
|---|---|---|
| `404` | `Accountant not found.` | `AccountInvariants.cs:71` |
| `422` | `You cannot change your own role or status.` | `AccountInvariants.cs:90` |
| `422` | `That account is already suspended.` | `SuspendAccountantHandler.cs:52` |
| `422` | `That account has not accepted its invitation yet, so it cannot be reactivated.` | `ReactivateAccountantHandler.cs:52` |
| `422` | `That account is already active.` | `ReactivateAccountantHandler.cs:53` |
| `422` | `That account is already an Accountant Admin.` | `PromoteAccountantHandler.cs:45` |
| `422` | `That account is not an Accountant Admin.` | `DemoteAccountantHandler.cs:49` |
| `422` | `At least one active Accountant Admin must remain.` | `AccountInvariants.cs:46` |
| `409` | `An account already exists for '<normalised email>'.` | `InviteAccountantHandler.cs:86` — renders inside the dialog |

**A. A `404` here means "not found **or** not visible to you".** `AccountInvariants.cs:58-71` returns it for an id belonging to a `CustomerAdmin`, because the role filter is part of the lookup rather than a check afterwards. Never render "forbidden", "denied" or "no permission" for a `404` (§2.3 rule J).

**B. `At least one active Accountant Admin must remain.` is not the operator's mistake.** The server counts Active Admins *after* the write, inside the transaction, and rolls back. Add "Promote another Accountant to Accountant Admin first." — and **never pre-empt it by counting Admins client-side**: you hold one page of a paginated list, so the count is wrong as soon as there are more Accountants than fit on a page, and a wrong count disables a legal action with nobody more powerful able to re-enable it (Matrix §12 rule 6 — Accountant Admin is the ceiling).

**C. No `traceId` on a `404`, `409` or `422`.** It is for `500` only (§7.1); printing it where `title` already says what is wrong teaches users to ignore it.

### 5.4 Four ways this step goes wrong

1. **`if (session.role)` or `row.role ? … : …`.** `AccountantAdmin` is `0`, which is falsy, so the branch takes the `AccountantUser` path for the most privileged role in the system and the Admin sees a one-column table. Always `===` a named constant (§10.1).
2. **Rendering the pager from the requested `pageSize`.** Ask for 999, get 50 with a `200`; every page boundary is then wrong with no error anywhere.
3. **Sorting or filtering client-side.** The server orders by `displayName` then `id` and accepts neither a sort nor a search parameter. A header that sorts one page is a lie about a server-paginated list.
4. **Adding an "active only" toggle.** `ListAccountantsHandler.cs:58-61` applies **no status filter**, on purpose — *"an Admin cannot reactivate somebody the list does not show."* A toggle would remove the only route to reactivation.

### What this step does NOT do, and why

No search box, sort, status filter, bulk selection, or per-row history. All five operations write audit entries, but reading the log is Accountant-Admin-only on `/audit` and there is no per-account audit endpoint, so a per-row history cannot be built (`IdentityScreens §8`).

---

## 6. Step 5 — the two tables and the row menu

Three components, one job each; all three receive data and callbacks and none of them fetches.

**File:** `frontend/src/slices/identity/components/AccountantAdminTable.tsx`

Columns: Name, Email, Role, Status, Last sign-in, plus the menu. Rendered through `shared/components/PaginatedTable.tsx` — never `Table` + `TablePagination` assembled here (§8.2), and `@mui/x-data-grid` is banned.

**A. Render nothing raw.** `role` through `ROLE_LABELS` → "Accountant Admin" / "Accountant User"; `status` through `StatusChip`, which owns the one colour map. A cell reading `0` has leaked the wire format; one reading "Admin" has broken the glossary. `status` is a string and `role` is a number **in the same row** — never `Number(row.status)`, never `String(row.role)`.

**B. `lastLoginAt` is nullable** — render an em dash, not "Invalid Date". Both timestamps carry an offset; format through `shared/format/dates.ts` (§10.2).

**C. Append `(you)` to the own row's name**, matched case-insensitively (§1.3).

**File:** `frontend/src/slices/identity/components/AccountantNameTable.tsx`

**One** column: Name. No menu column, no status column, no em-dash placeholders, same pager. It takes `PaginatedResponse<AccountantSummary>` and cannot be handed a detail row.

**File:** `frontend/src/slices/identity/components/AccountantRowMenu.tsx`

An `IconButton` opening a `Menu`, every item carrying an `aria-label` (§8.4). Its entire logic is this table, and every condition is verified against a handler:

| Item | Action | Shown when | Server guard |
|---|---|---|---|
| *Suspend* | `SuspendAccountant` | `can(...)` and not own row and `status === 'Active'` | `SuspendAccountantHandler.cs:45,51` |
| *Reactivate* | `ReactivateAccountant` | `can(...)` and `status === 'Suspended'` | `ReactivateAccountantHandler.cs:49-54` |
| *Promote to Accountant Admin* | `PromoteAccountant` | `can(...)` and `role === UserRole.AccountantUser` | `PromoteAccountantHandler.cs:44-45` |
| *Demote to Accountant User* | `DemoteAccountant` | `can(...)` and not own row and `role === UserRole.AccountantAdmin` | `DemoteAccountantHandler.cs:42,48-49` |
| the button | — | at least one item is shown | — |

**D. Prefer hiding to disabling** (§6.2 rule C). Nothing in Matrix §1–2 or §11 names a case on this screen that must stay visible-but-greyed.

**E. `ConfirmDialog` for *Suspend* and *Demote* only** (§8.3), naming the **consequence**, not "are you sure?": "Ada Byron will be unable to sign in until reactivated"; "Ada Byron will lose the ability to invite, suspend, promote and demote Accountants". *Reactivate* and *Promote* grant capability and are reversible, so they submit directly.

**F. *Promote* stays visible on an `Invited` or `Suspended` row.** `PromoteAccountantHandler.cs:47-49` allows it deliberately — *"The role is what they will be when they can act; it is not itself permission to act, which is what Status governs."*

**G. A promotion or demotion is not immediate for the target.** `PromoteAccountantHandler.cs:57-60`: the role is a claim taken at login, so the new permission arrives at their next sign-in — up to eight hours. The success `Snackbar` must say so, or the operator watches nothing happen and does it again.

### 6.1 Five ways this step goes wrong

1. **`if (row.role)` to choose between *Promote* and *Demote*.** `AccountantAdmin` is `0`, so the test is `false` for **every** Accountant Admin: *Promote* appears on every Admin and *Demote* on nobody. The likeliest bug in this plan.
2. **Hiding four actions on the own row instead of two** — the server guards only `suspend` and `demote` (§1.3).
3. **Comparing ids with a case-sensitive `===`.** Mirror `AccountInvariants.cs:89`.
4. **Rendering an empty `Menu`** instead of no button (§1.3).
5. **Showing *Suspend* on an `Invited` row.** See the drift note in §10 item 2 — the `status === 'Active'` condition is load-bearing.

### What this step does NOT do, and why

No *Reset password*, *Change email*, *Rename*, *Delete* or *Resend invitation*. Matrix §11: resetting another person's password directly is permitted to **nobody**, and `ChangePasswordRequestDto` (`Dtos/AuthDtos.cs:32-36`) has no target-user field precisely so the mistake cannot be made — *"you cannot forget to validate a parameter you never accepted."* There is no request shape those buttons could send.

---

## 7. Step 6 — the invite dialog and its schema

**File:** `frontend/src/slices/identity/screens/inviteAccountantSchema.ts`

The path is `screens/` because `IdentityScreens.md`'s checklist puts it there and that spec outranks this plan. Three fields, limits mirrored exactly:

| Field | Client rule | Server source |
|---|---|---|
| `email` | trim, required, ≤ **320** | `EmailNormalization.cs:13,33-37` |
| `email` | exactly one `@`, not at either end | `EmailNormalization.cs:38-40` |
| `displayName` | trim, required, ≤ **200** | `InviteAccountantHandler.cs:19,65-70` |
| `role` | `AccountantAdmin` or `AccountantUser`, as a **number** | `InviteAccountantHandler.cs:58-60` |

**A. Do not write an email regex.** `EmailNormalization.cs:23-27` says why: one `@` plus `MailAddress` parsing, *"deliberately not a regular expression"*. A stricter client pattern rejects addresses the server accepts, and nothing tells the user which rule is imaginary (§9.2).

**B. Do not lowercase the email.** The server keeps `LoginEmail` as typed (`InviteAccountantHandler.cs:91`) and normalises separately for uniqueness (`:92`); lowercasing changes what is displayed and mailed.

**C. 200, not 255.** Most display names elsewhere in this API are capped at 255. Mirroring 255 here produces a `422` about a limit the user appears to be within.

**File:** `frontend/src/slices/identity/components/InviteAccountantDialog.tsx`

`mode: 'onBlur'`; submit disabled only while the mutation is pending, never because the form is invalid; `ErrorBanner` above the submit button inside the dialog; never reset on error — input must survive failure (§9.3 rules A–D).

**D. A dialog, not a route.** §4.1's route table has no `/accountants/new` and that table is normative; the form fetches nothing, so no state is worth a URL; and the `409` is best read with the list on screen, because the answer to it is usually "she is already there, three rows up".

**E. Two role options and only two.** `InviteAccountantHandler.cs:58-60` rejects `CustomerAdmin` and `Employee` with `422 "An invited accountant must be an Accountant Admin or an Accountant User."` Customer-side accounts come only from `/api/employees/invite` and `/api/customers/onboard`, the only paths that can supply the mandatory `employee_id`/`customer_id`, so a four-option picker offers two choices that can only fail.

**F. Send `role` as a JSON number.** MUI's `Select` hands you a `string`; convert in `onChange`. `{"role":"1"}` is a **`400`** from model binding before any handler runs, so the banner names no field (LoginArchitecture §8).

**G. On `201`:** close the dialog, `Snackbar` "Invitation sent to &lt;email&gt;.", invalidate the list. Do not follow `Location` (§1.1 rule C).

### 7.1 The invitation token never reaches the browser

`InviteAccountantHandler.cs:134-142` puts the raw token in the notification's `EmailBody` **only** — not in `AccountantDetailDto`, not in the `201` body, not in the `Location` header, not in the in-app notification body. A raw invitation or reset token must never reach browser history, a log, or an analytics call.

- **No "copy invitation link" affordance.** There is nothing to copy; a URL built from the account id would carry no token, fail on `/accept-invitation`, and look like a broken invitation system.
- **The link's host comes from `App__BaseUrl`** (`TokenLinks.cs`). Misconfigured, every invitation points at the wrong host and **the UI cannot detect it**. Flag it (§12); build no check. Punch-list item **16**, ***Drift***.
- **There is no resend, and no recovery for a lost invitation** (§12).

### 7.2 Three ways this step goes wrong

1. **Treating `201` as an error** because it is not `200`.
2. **Mapping the `409` or a `422` onto the email field.** A red outline on a guessed control is worse than none.
3. **Adding an "is this address taken" pre-check.** No endpoint exists, and one would be the enumeration oracle the whole auth flow is built to prevent. The `409` *does* disclose the address deliberately — the caller can already list every account (`InviteAccountantHandler.cs:79-82`) — and that does not license a check on an unauthenticated path. Login and reset responses are deliberately opaque: never add an email-existence check to either.

---

## 8. Step 7 — `ProfileScreen.tsx`

**File:** `frontend/src/slices/identity/screens/ProfileScreen.tsx`

Route `/profile`, inside the shell, all four roles (§4.1). **It issues no request of its own** — everything comes from `['identity','session']`, already cached by `GET /api/auth/me`.

Renders: `displayName` as read-only text; the role through `ROLE_LABELS`; a `<Link to="/change-password">`; *Sign out*, the same mutation the account menu uses (§5.1); and for a Customer-side caller only, a link to `/my-customer` (`SessionDto.customerId` is non-null for CA and EMP — never render the raw GUID).

**A. No display-name form and no login-email field.** There is nothing to POST to. Across all thirteen routes, the only things any of them writes about the caller are the password and — on `/accept-invitation` only, once, before first sign-in — an optional `displayName` (`AuthDtos.cs:54-58`). `SessionDto` carries no email at all (`AuthDtos.cs:20-25`), so there is not even a value to prefill. Show "To change this, ask an Accountant Admin". Punch-list items **10** and **11**, both ***Degrading***.

**B. `POST /api/employees/change-login-email` is not a counter-example.** It takes an `employeeId`, is granted to the two Accountant roles only, and belongs on the Employee detail screen. Nobody changes their own, and no Accountant's login email can be changed at all.

**C. No password form here.** `LoginArchitecture.md` §3 owns it; two forms posting to one endpoint means two sets of validation drifting apart.

### What this step does NOT do, and why

No contact-details region. `EmployeesScreens.md` §7 specifies that region, and specifies it read-only with no submit button, because `POST /api/employees/update-own-contact` is a full replacement and an un-prefillable form wipes the user's own phone and work email on a `200` (punch-list item **12**, ***Degrading***). Either way it is not this plan's region.

---

## 9. Step 8 — route and navigation wiring

**File:** `frontend/src/routes.tsx`

Phase 0 created this file and owns its shape. Add only the two rows §4.1 already specifies, both inside the shell and inside `RequireSession`:

| Path | Screen | Guard |
|---|---|---|
| `/accountants` | `AccountantListScreen` | `RequireRole roles={[AccountantAdmin, AccountantUser]}` |
| `/profile` | `ProfileScreen` | `RequireSession` only — all four roles |

**A. `RequireRole` renders `AccessDeniedPage`; it does not redirect** (§4.3 rule A), so a `CustomerAdmin` who typed `/accountants` is told the page is not for them.

**B. `RequireRole` is not a security boundary** (§4.3 rule B). Never rely on the React app to hide data: the server denies the underlying calls with `403` and audits every denial regardless of what the router did.

**C. The *Accountants* nav item derives from §5.2's table** (AA and AU), **not** from `can()` — a nav item maps to a page, not to an action. `AppShell` is Phase 0's file; a missing row there is a Phase 0 defect to report, not a local fix.

**D. No `/accountants/:id`, no `/accountants/new`.** §4.1's table has neither, and no get-single endpoint could populate the first.

**E. This slice imports from `shared/` and from itself only** (§1.4 rules B–C) — never from another slice, and nothing under `shared/` imports from here. No token in `localStorage` or `sessionStorage`, ever: the session is the `aa_session` HttpOnly cookie and `http.ts` sends `credentials: 'same-origin'`, so this slice has nothing to store or attach. A `403` carrying a `detail` is the forced-password-change gate, handled centrally by Phase 0 (§2.3 rule I); a `401` from any call here means the session is gone and Phase 0's interceptor redirects once — no screen retries, toasts or inlines either.

---

## 10. Spec-versus-code drift found while writing this plan

Recorded rather than smoothed over, per `UI/README.md` §*Conflict precedence*. None of these changes a step above; each is something a builder would otherwise "fix".

> **1. `IdentityScreens.md`'s files checklist claims two `shared/` files.** Resolved in §0.1: Phase 0 creates `shared/format/enums.ts` and `shared/permissions/can.ts`, this plan verifies them. `GeneralUIArchitecture.md` §1.2 outranks a screen spec.

> **2. Suspending an `Invited` Accountant succeeds, and the specs' affordance rule is what prevents it.** `SuspendAccountantHandler.cs:51-52` refuses only an already-`Suspended` account, so an `Invited` row moves to `Suspended` with a `200`. *Reactivate* then passes its guard (`ReactivateAccountantHandler.cs:49`) and writes `Status = Active` while `PasswordHash` is still `null`; `AcceptInvitationHandler.cs:73` now refuses the original invitation link because the status is no longer `Invited`. The account is recoverable only through forgot-password, which does accept it (`RequestPasswordResetHandler.cs:62`, `CompletePasswordResetHandler.cs:72`, both requiring `Active`). `IdentityScreens §4.3`'s `status === 'Active'` condition on *Suspend* is therefore load-bearing for a reason that spec does not state. Keep it. This is a backend gap and is not currently on the punch-list.

> **3. `ReactivateAccountantHandler.cs:45-48` names a constraint that does not do what it says.** The comment claims flipping an `Invited` account to `Active` *"produces a row that violates ck_user_accounts_status"*. `Infrastructure/Migrations/20260901_001_CreateIdentitySchema.sql:54` is `CHECK (status IN ('Invited', 'Active', 'Suspended'))` — a vocabulary check only, with no reference to `password_hash`. The handler's own `422` is the real guard. No UI consequence; recorded so nobody relies on the database to catch item 2.

> **4. `IdentityScreens.md`'s opening paragraph says "five endpoints under `/api/accountants/*`".** Its own §1 lists **six**, and `IdentityEndpoints.cs:92-162` registers six: five mutations plus `list`, the only one an `AccountantUser` may call. Build six.

---

## 11. Known constraints

1. **Two response shapes from one route** — item **6**, ***Degrading***. Two table components is the cost.
2. **`role` is an integer on the wire and `0` is falsy** — item **4**, ***Degrading***.
3. **A `422` can never highlight a field** — item **5**, ***Degrading***. Form-level and table-level banners only.
4. **No self-service profile edit exists** — items **10** and **11**, ***Degrading***.
5. **No resend invitation and no recovery for a lost one** (§7.1).
6. **A role change takes effect at the target's next sign-in**, up to eight hours later; there is no server-side session store, so a demoted Admin keeps the old role in their cookie.
7. **`can.ts` is hand-duplicated from the server** (§6.3); its six Identity rows are verified in §0.3 and nowhere else.
8. **Nothing here has been run.** There is no `frontend/` directory, the SPA-hosting lines are absent from `Program.cs` (item **1**, ***Blocking***), and this machine has no local PostgreSQL. Every criterion below is for a future builder to verify.

---

## 12. Questions to flag if unclear

- [ ] **Does Phase 0 create `slices/identity/api.ts`, `types.ts` and `queries.ts`?** §0.2 rule D assumes so because `SessionProvider` needs `getSession`. If Phase 0 instead puts session code under `shared/auth/`, steps 1–3 create these three files rather than extending them.
- [ ] **Should `/api/accountants/list` always return the narrow shape**, with a separate detail endpoint for the Admin? Item **6**, ***Degrading***; open in `IdentityScreens §10`.
- [ ] **Is a display name ever meant to be changeable after invitation?** Nobody can change anybody's, anywhere. Confirm that is intended for v1 rather than a missing endpoint.
- [ ] **Is there a supported "resend invitation"?** A second invite is a `409` regardless of status and `reactivate` refuses an `Invited` account, so a lost invitation has no recovery path.
- [ ] **Confirm the wording of the "takes effect at next sign-in" `Snackbar`** (§6 rule G requires one; nothing specifies the text).
- [ ] **Should a demoted Accountant Admin's open session be terminated?** Today it is not.
- [ ] **Should *Suspend* be hidden on an `Invited` row, or refused by the server?** §10 item 2.
- [ ] **Should a misconfigured `App__BaseUrl` be surfaced anywhere?** If it is wrong every invitation is broken and nothing in the UI reveals it. Item **16**, ***Drift***.

---

## Files checklist

Phase 0 must already have produced these — verify, never create (§0.1, §0.3):

- [ ] `frontend/src/shared/api/` — `http.ts`, `ApiError.ts`, `problemDetails.ts`, `paginated.ts`, `queryClient.ts`
- [ ] `frontend/src/shared/auth/` — `SessionProvider.tsx`, `useSession.ts`, `RequireSession.tsx`, `RequireRole.tsx`
- [ ] `frontend/src/shared/permissions/can.ts`, `shared/hooks/usePaginatedQuery.ts`, `shared/format/enums.ts`, `shared/format/dates.ts`
- [ ] `frontend/src/shared/components/` — `PaginatedTable`, `PageHeader`, `ConfirmDialog`, `StatusChip`, `ErrorBanner`, `EmptyState`, `LoadingRegion`, `AccessDeniedPage`
- [ ] `frontend/src/slices/identity/` — `types.ts`, `api.ts`, `queries.ts` and the five `/api/auth/*` screens

This plan, in build order:

- [ ] `frontend/src/slices/identity/types.ts` — **extended**: `AccountantSummary`, `AccountantDetail`, `AccountantStatus`, `InviteAccountantRequest`, `AccountIdRequest` (step 1)
- [ ] `frontend/src/slices/identity/api.ts` — **extended**: six functions, `listAccountants` returning the union (step 2)
- [ ] `frontend/src/slices/identity/queries.ts` — **extended**: `accountantKeys`, `useAccountantList`, five mutation hooks (step 3)
- [ ] `frontend/src/slices/identity/screens/AccountantListScreen.tsx` — the role branch (step 4)
- [ ] `frontend/src/slices/identity/components/AccountantAdminTable.tsx` — five columns plus the menu (step 5)
- [ ] `frontend/src/slices/identity/components/AccountantNameTable.tsx` — one column, no actions (step 5)
- [ ] `frontend/src/slices/identity/components/AccountantRowMenu.tsx` — the four affordance conditions (step 5)
- [ ] `frontend/src/slices/identity/screens/inviteAccountantSchema.ts` (step 6)
- [ ] `frontend/src/slices/identity/components/InviteAccountantDialog.tsx` (step 6)
- [ ] `frontend/src/slices/identity/screens/ProfileScreen.tsx` (step 7)
- [ ] `frontend/src/routes.tsx` — **extended** with two rows (step 8)
- [ ] Nothing else: no file under `frontend/src/shared/`, no password form, no token display, no Accountant detail route, no `detail` query key

---

## Success criteria

Each is verified by running the app, not by reading the code. None has been observed. The dev loop is `docker compose up -d db`, `dotnet run --project AccountantApp.Api`, then `npm run dev` in `frontend/`, with the browser on **5173** and never on the API's port (§11).

1. As an `AccountantAdmin`, `/accountants` lists every Accountant — `Suspended` and `Invited` included — with the role as a glossary label and the status as a `StatusChip`.
2. As an `AccountantUser`, the screen renders one column and the mandatory subtitle, and the response in the network tab carries **no** `loginEmail`, `role`, `status`, `createdAt` or `lastLoginAt` key on any row.
3. No code path inspects a row for the presence of a field, and neither view renders a placeholder for a withheld one.
4. As a `CustomerAdmin`, `/accountants` renders `AccessDeniedPage` and the nav has no *Accountants* item.
5. The own row is labelled `(you)`, offers neither *Suspend* nor *Demote*, and — no other action being available — shows no row-menu button at all.
6. Every Accountant Admin row offers *Demote* and none offers *Promote*: the `role === 0` trap is absent.
7. The invite dialog has exactly two role options, and the request body shows `"role"` as a JSON **number**.
8. Inviting returns `201`, the `Snackbar` names the address, and the list refetches once.
9. A duplicate address renders the `409` verbatim inside the dialog with every field still filled in.
10. Each `4xx` message in §5.3 can be provoked and appears verbatim — in the dialog for invite, above the table for the four row actions — with no `traceId` shown.
11. Provoking `At least one active Accountant Admin must remain.` leaves the row unchanged after a refetch, and the banner adds the "promote another Accountant first" sentence.
12. Every successful row action updates its row from the mutation response with **no second `GET`** in the network tab, then invalidates the list once.
13. A promotion's success message states that the change takes effect at the target's next sign-in.
14. Posting `suspend` with a `CustomerAdmin`'s id renders "Not found", never "forbidden".
15. `pageSize=999` renders a pager consistent with the `50` in the response envelope, with no missing rows.
16. `/profile` issues **no** network request, its *Change password* control navigates to `/change-password`, and it offers no name or email edit.
17. Nothing in the app offers to reset another person's password, change any Accountant's email or display name, delete an Accountant, or copy an invitation link.
18. `localStorage` and `sessionStorage` are empty throughout, no request carries an `Authorization` header, and `fetch` appears in exactly one file: `shared/api/http.ts`.
19. The six `Identity` rows in `can.ts` match `IdentityActionCatalogue.cs:24-29` exactly — no extras on either side.
20. No screen renders a raw role integer, a raw GUID, a status word outside `Invited`/`Active`/`Suspended`, or the words "Client" or "Admin" alone.
