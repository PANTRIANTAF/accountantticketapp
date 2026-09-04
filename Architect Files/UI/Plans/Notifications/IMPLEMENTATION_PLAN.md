# Notifications Screens — UI Implementation Plan

An executable step-by-step plan for one slice of the SPA: the notification centre at `/notifications`, the
unread bell in the AppBar, *Mark as read* and *Mark all as read*. It assumes **Phase 0 is already built**
(§0.1) and that you have read `../../GeneralUIArchitecture.md` in full and
`../../Screens/NotificationsScreens.md` once; it restates neither. That spec says *what* the screens are —
this says which files you create in what order, what goes in each, and how a future builder checks it.
Build it after Phase 0, in any order relative to the other slices. Where something is unclear, **flag it
(§11); never invent.**

**Documents that govern this document, in precedence order.** Where any disagrees with this one, **it wins
and this one is wrong** — fix this document, do not code around it.

| # | Document | Sections that bind this plan |
|---|---|---|
| 1 | `../../../README.md` | *Locked platform decisions*, *Conflict precedence* |
| 2 | `../../../00-Glossary.md` | Banned terms; binding in UI copy |
| 3 | `../../../01-DomainModel.md` | §7, §9.2 — Notification; nothing is ever deleted |
| 4 | `../../../02-AuthorizationMatrix.md` | §9 — nobody reads another actor's notifications |
| 5 | `../../../04-Infrastructure.md` | §1–3 — hosting, one origin, the dev loop |
| 6 | `../../../App/GeneralAppArchitecture.md` | §8 — route shape, pagination, error contract |
| 7 | `../../GeneralUIArchitecture.md` | §1.2, §1.4 A/C/E, §2.1–2.5, §3.1–3.4, §5.1/§5.3, §6, §7.1–7.4, §8.2–8.4, §10.2 |
| 8 | `../../LoginArchitecture.md` | §1.1–1.2 — the three session states this slice reads |
| 9 | `../../Screens/NotificationsScreens.md` | **The screen spec. Normative for every rule below** |
| 10 | `../../BACKEND_CHANGES_REQUIRED.md` | Non-normative; cited by item number only |
| 11 | This plan | Loses to all of the above |

---

## 0. Prerequisites and build position

### 0.1 Phase 0 is a prerequisite, not a step in this plan

Everything under `frontend/src/shared/` and the three root files come from
`../00-Foundation/IMPLEMENTATION_PLAN.md`. **Do not start §1 until they exist and the app runs.** If one is
missing, build Phase 0; never create a local substitute in this slice.

| Needed here | From Phase 0 | Used for |
|---|---|---|
| `shared/api/http.ts` (`get`, `post`), `ApiError.ts`, `problemDetails.ts` | §2.1–2.2 | Every call in §2. The only `fetch` in the app |
| `shared/api/paginated.ts`, `queryClient.ts` | §3.3–3.4 | `PaginatedResponse<T>`; `retry`, `staleTime`, no focus refetch |
| `shared/auth/useSession.ts` | `LoginArchitecture.md` §1.2 | The badge's `enabled` gate |
| `shared/components/AppShell.tsx` | §5.1 | Hosts the bell. **See §0.2** |
| `PaginatedTable` (for its 1-based/0-based conversion), `PageHeader`, `ErrorBanner`, `EmptyState`, `LoadingRegion`, `ConfirmDialog` | §8.2–8.3 | §6, §7 |
| `shared/format/dates.ts`; `routes.tsx` | §10.2; §4.1 | `createdAt`; registers `/notifications`, passes the slot |

**This plan creates nothing under `frontend/src/shared/`** — no component, hook or formatter. If a step
below appears to need one, that is a Phase 0 gap and belongs in §11.

### 0.2 The one shared file this plan touches, and the extent of the touch

The bell mounts *into* `AppShell`, which this slice does not own, and §1.4 rule A forbids `shared/`
importing from `slices/`. The mechanism is a slot, not an import.

**A.** `AppShell.tsx` gains **one optional prop**, `notificationSlot?: ReactNode`, rendered at §5.1's
`[bell 3]` position. Nothing else in that file changes — no import, no state, no query, no role check — and
if Phase 0 already declared it, this plan changes it not at all.

**B.** `routes.tsx` passes `<AppShell notificationSlot={<UnreadBadge />} />`: it already imports every
slice's screens (§1.4 rule E), so the coupling lands in the file built to hold it, and with the prop
omitted the shell draws no bell, leaving Phase 0 runnable alone.

**C.** Do not import the badge into the shell instead: the app's only possible rule-A violation, and it
drags this slice's `api.ts`, `types.ts` and `queries.ts` into the entry chunk of every screen including
`/login`, which has no shell. Criterion 3 greps for it.

### 0.3 What was verified in the C# source

Read out of the code, not the screen spec. Re-verify any row you depend on.

| Fact | Source |
|---|---|
| `POST /api/notifications/list` — a **POST read** with a body | `Slices/Notifications/NotificationsEndpoints.cs:19` |
| `GET .../unread-count`, no query string; `POST .../mark-read`; `POST .../mark-all-read` with **no body parameter** | `NotificationsEndpoints.cs:23`, `:27`, `:31`, `:65-72` |
| `list` declares `.Produces<object>(200)`; the group has **no `.WithTags`** and no `.RequireAuthorization()` | `NotificationsEndpoints.cs:11-21` |
| Request `{ unreadOnly=false, pageNumber=1, pageSize=15 }`, a required parameter | `ListMyNotificationsRequestDto.cs:5-7`; endpoint `:37` |
| Row `{ id, ticketId?, eventKind, title, body, isRead, readAt?, createdAt, emailStatus? }`; envelope `PaginatedResponse<NotificationDto>`, ordered `createdAt` desc then `id` desc | `NotificationDto.cs:5-15`; `ListMyNotificationsHandler.cs:44-45`, `:79-86` |
| `mark-read` body `{ notificationIds: Guid[] }`; **non-empty** → `422 "NotificationIds cannot be empty."` | `MarkReadRequestDto.cs:5`; `MarkNotificationsReadHandler.cs:43-44` |
| **≤ 200**, checked *after* `Distinct()` → `422 "No more than 200 notifications can be marked in one request."` | `MarkNotificationsReadHandler.cs:46-51` |
| `{ markedCount }` from both mark endpoints, counting only rows not already read | `MarkReadResponseDto.cs:5`; `MarkNotificationsReadHandler.cs:62-69`; `MarkAllNotificationsReadHandler.cs:39-49` |
| An unowned id is filtered out, audited `PermissionDenied`/`Denied`, and still answers `200` | `MarkNotificationsReadHandler.cs:58-92` |
| `{ unreadCount }`, scoped to `CurrentUser.Id` like every route here | `UnreadCountResponseDto.cs:5`; `GetUnreadCountHandler.cs:26-30` |
| Both permissions granted to all four roles, and only these two | `NotificationsActionCatalogue.cs:13-14` |
| `aa_session`: `HttpOnly`, `Secure`, `SameSite=Strict`, **8h sliding** | `Slices/Identity/IdentityRegistration.cs:67-87` |
| **No SignalR, no `MapHub`, no `text/event-stream`, no websocket** anywhere in the API | Whole-project search returns nothing |

> **`.Produces<object>` and the missing `.WithTags` are both real, and neither changes this build.** Seen at
> `NotificationsEndpoints.cs:21` and `:11-17`; every other group tags itself (`CustomersEndpoints.cs:13`,
> `AuditEndpoints.cs:16`, four more). Punch-list **item 15** is the tags, **item 9** the annotation. They
> constrain a future *generated* client, not this hand-written one: type `listNotifications` from
> `ListMyNotificationsHandler`'s return type, never from the annotation. Do not fix either from the UI
> side, and do not skip the endpoint because its metadata is wrong.

### 0.4 Drift between the screen spec and the code

> **`NotificationEvents.cs` defines eighteen event kinds; `NotificationsScreens.md` §5 says sixteen and
> tables sixteen.** The two missing are `EmployeeRegistered` and `EmployeeDeparted`
> (`Slices/Notifications/ExternalInterfaces/NotificationEvents.cs:36-37`), and they are the ones that
> matter: both have live producers (`Employees/Application/Handlers/RegisterEmployeeHandler.cs:128`,
> `DepartEmployeeHandler.cs:116`), both go to the Customer's own Admins — people who *can* sign in and read
> them — and both are in-app only, absent from `NotificationEvents.Emailed` (`:48-52`). That spec's
> preamble therefore miscounts twice: twelve of eighteen kinds are ticket-related, not eleven of sixteen,
> and **all eighteen kinds have producers**, not five and no longer only six. The twelve ticket-related
> kinds acquired theirs when the `Tickets` slice landed. Counted by grepping
> `NotificationEvents\.` across `AccountantApp.Api/Slices/` and keeping only the sites that *name a
> kind while creating a notification* — which discards `NotificationApi.cs:111`, `:151` and `:154`
> (dispatch and validation), `OutboxDrainer.cs:189-190` (email rendering) and the comment at
> `DueDateScanner.cs:38` — that is **21 producer call sites, 15 of them under
> `Slices/Tickets/**`**, and every one of the eighteen kinds is named by at least one. Three kinds
> have two producers each: `TicketSubmitted` (`CreateTicketHandler.cs:276`,
> `SubmitTicketHandler.cs:139`), `CorrectionSubmitted` (`SubmitRevisionHandler.cs:417`,
> `SubmitTicketHandler.cs:159`) and `TicketCancelled` (`CancelTicketHandler.cs:171`, `:184`).
>
> **In code you do nothing.** §5's table is normative and adding rows is an edit to that document this plan
> may not make. The two fall through §5 rule D's unmapped path and render with their raw `eventKind` as the
> label — readable, counted, never hidden, which is what that rule is for. It is also ugly in front of a
> real Customer Admin today, so it is the **first** item in §11, to decide before this slice ships.

---

## 1. Step 1 — `types.ts`

**File:** `frontend/src/slices/notifications/types.ts`

Three interfaces, `camelCase`, each commented with the C# DTO it mirrors (§2.5). `Guid` becomes `string`.

```ts
/** Mirrors Slices/Notifications/Application/Dtos/NotificationDto.cs */
export interface Notification {
  id: string;
  ticketId: string | null;    // present on ticket kinds; NEVER rendered (section 6 C)
  eventKind: string;          // raw string, not a union: NotificationEvents.cs will grow
  title: string;
  body: string;
  isRead: boolean;
  readAt: string | null;      // never rendered
  createdAt: string;          // DateTimeOffset: has an offset, parses directly (GeneralUI 10.2)
  emailStatus: string | null; // Pending|Sent|Failed|Abandoned|Skipped|null; never rendered
}

export interface MarkReadResult { markedCount: number }      // MarkReadResponseDto, BOTH mark endpoints
export interface UnreadCountResult { unreadCount: number }   // UnreadCountResponseDto
```

**A. `eventKind` stays `string`.** A union makes an unknown kind a type error at the one boundary with no
type checking, and invites an exhaustive `switch` that throws on the nineteenth kind. §4 handles it as data.

### What this step does NOT do, and why

No enum for `eventKind` — the server sends a string here, unlike `role`, an integer (§10.1). No
request-side interfaces: both request bodies are inline literals in `api.ts`, beside their route.

---

## 2. Step 2 — `api.ts`

**File:** `frontend/src/slices/notifications/api.ts`

Four functions. No React, no hooks, no TanStack Query — a typed wrapper readable line by line against
`NotificationsEndpoints.cs`.

```ts
/** POST, not GET: a POST read with a filter body (NotificationsEndpoints.cs:19; GeneralUI 2.3 C).
 *  Typed from ListMyNotificationsHandler, NOT from .Produces<object> (section 0.3). */
export const listNotifications = (
  body: { unreadOnly: boolean; pageNumber: number; pageSize: number },
): Promise<PaginatedResponse<Notification>> => post('/api/notifications/list', body);

/** GET, and no query string at all (NotificationsEndpoints.cs:23). */
export const getUnreadCount = (): Promise<UnreadCountResult> => get('/api/notifications/unread-count');

/** No second argument: the endpoint declares no body parameter (NotificationsEndpoints.cs:65-72). */
export const markAllNotificationsRead = (): Promise<MarkReadResult> =>
  post('/api/notifications/mark-all-read');
```

`markNotificationsRead` is the fourth, written exactly as `NotificationsScreens.md` §6 rule C gives it:
`MARK_READ_MAX_IDS = 200`, a throw on an empty array, a throw above the cap, then
`post('/api/notifications/mark-read', { notificationIds })`.

**A. The verbs are asymmetric on purpose.** §2.3 rule C names `/api/notifications/list` among the five POST
reads; the `list` suffix predicts nothing — `/api/ticket-types/list` next door is a `GET`. Changing either
verb yields a `405` with nothing in the body to explain it. Send all three keys of the `list` body every
call: the DTO is a required parameter, so an absent body is a `400`, not defaults.

### 2.1 Two bounds on `mark-read`, and what the client does at each

**A. An empty selection is a client-side guard, never a server round trip.**
`MarkNotificationsReadHandler.cs:43-44` answers `422 "NotificationIds cannot be empty."` — a banner naming
a C# property, for a no-op nobody asked for. The `api.ts` throw is the whole fix; above it, the screen does
not offer the action with nothing selected, and a row's button always carries exactly one id.

**B. Over the cap: assert, do not chunk.** `MarkNotificationsReadHandler.cs:50-51` refuses more than 200
ids *after* `Distinct()`; the client asserts on the raw array, so it is marginally stricter than the server
— correct, because a guard that fires only on input the server would also reject is never exercised. **Do
not chunk into batches of 200:** with `pageSize` clamped to 50 the cap is unreachable today, so chunking is
dead code, and a silent multi-request loop turns one audited operation into several. If a selection ever
could exceed 200 the throw fires in development, in the file that owns the bound, rather than as a `422`
for one heavy user in production.

---

## 3. Step 3 — `queries.ts`

**File:** `frontend/src/slices/notifications/queries.ts`

Four hooks — `useNotifications`, `useUnreadCount`, `useMarkRead`, `useMarkAllRead`. Screens import hooks,
never `api.ts` (§3.2 rule A). Keys, per §3.1 and `NotificationsScreens.md` §4.2:

| Query | Key |
|---|---|
| The list | `['notifications', 'list', { unreadOnly, pageNumber, pageSize }]` |
| The badge count | `['notifications', 'unreadCount']` |

`unreadOnly` **must** be in the key: it changes the response, and two filters sharing one cache entry is how
a screen shows the wrong rows. It is React state, not a URL parameter.

### 3.1 `useUnreadCount` — the only polling query in the application

Write it exactly as `NotificationsScreens.md` §3.1 gives it. Four options, all load-bearing:

| Option | Value | Why |
|---|---|---|
| `refetchInterval` | **`60_000` — 60 seconds** | Decided by screen spec §3.1; nothing upstream sets it (§11) |
| `refetchIntervalInBackground` | `false` (the default, stated) | §3.2 rule A, and §3.2 A below |
| `enabled` | authenticated only | Screen spec §3.2 rule D. Anonymous polling is a `401` a minute |
| `staleTime` | `30_000` | Half the interval, so a mount inside the window adds no request |

`refetchInterval` appears in **exactly one file in the application** and this is it (§3.2 rule H); a second
is a change to that document, not a local decision. Criterion 2 greps for it.

**Why polling and not websockets.** There is no alternative, and this is verified rather than assumed: the
API has no SignalR package, no `MapHub`, no route emitting `text/event-stream` and no websocket handling of
any kind (§0.3, last row), so a client opening a socket connects to nothing and retries forever. Screen
spec §7 items 1–4 close the same door on `EventSource`, service workers and the browser `Notification` API.

### 3.2 Two ways a wrong interval does damage

**A. Polling in a hidden tab renews a sliding session forever.** `aa_session` is `ExpireTimeSpan = 8h` with
`SlidingExpiration = true` (`IdentityRegistration.cs:86-87`), so every request resets the clock. A
backgrounded tab polling each minute renews the cookie ~480 times a workday and the 8-hour expiry **never
fires**: an unattended machine in a shared office stays signed in overnight, indefinitely, and the only
control the system has over abandoned sessions is disabled by a badge nobody is looking at. Hence
`refetchIntervalInBackground: false`, and hence §5.3 forbidding a session poll for the same reason.

**B. Needless load.** 60 seconds is ~480 requests per user per workday, each a `SELECT COUNT(*)`
(`GetUnreadCountHandler.cs:26-28`); at ten seconds it is ~2,900, for a number no more useful because
nothing a user does with it is urgent. Both failure modes are invisible in development, where nobody leaves
a tab open for eight hours.

### 3.3 The two mutations, and what this step does not do

`useMutation` with `retry: false` inherited from Phase 0's `queryClient` — nothing here is idempotent and
`mark-read` writes an audit row, so a retry can manufacture a second one. Their `onSuccess` is the whole of
§8. **No `onMutate` and no optimistic update anywhere** (§3.2 rule E; screen spec §6 rule G). **No
`refetchInterval` on the list**, which would repaginate under the reader and lose their place (screen spec
§7 item 10). **No `can()` call:** both actions are granted to all four roles
(`NotificationsActionCatalogue.cs:13-14`), so the check could only ever pass. **No `enabled: false` meaning
"not allowed"** (§3.2 rule B); the session gate is the one `enabled` here and it is a data dependency.

---

## 4. Step 4 — `eventKinds.ts`

**File:** `frontend/src/slices/notifications/eventKinds.ts`

Two exports: the label map transcribed from `NotificationsScreens.md` §5's table, and `destinationFor`. One
file, so adding a kind is one edit.

**A. Most kinds map to no destination, and today every kind does.** **Twelve** ticket kinds have no
**client route** to link to — not because the backend slice is unbuilt (it is built, registered at
`Program.cs:65` and routed at `:157`, and all twelve kinds have live producers; §0.4) but because
there is no `Tickets` UI plan, no `Screens/TicketsScreens.md` and therefore no route in `routes.tsx`
and no screen to navigate into. The **six** non-ticket kinds are linkless by design, their tokens
living only in the email. Twelve plus six is the eighteen of §0.4, and the two sections must agree.
`destinationFor` therefore returns `null` for every input. Keep it a function, as §5
rule C writes it: when a `Tickets` **UI** ships this is a one-line change in one file, and the route comes from
`Screens/TicketsScreens.md`, never from a guess here.

**B. A linkless notification is a non-interactive row.** No anchor, no `Link`, no `onClick`, no pointer
cursor, no disabled-looking link. Ticket kinds carry one muted line under the body: **"Not available yet —
ticket screens do not exist"**. Inventing `/tickets/:id` ships twelve dead links that render `NotFoundPage`
and read as a broken application; the note reads as an unfinished one, which is true.

**C. An unmapped `eventKind` renders readably and never disappears.** The enum will grow and this map will
lag it by at least a commit; §0.4 shows it already does.

```
# NotificationRow, label resolution
label = EVENT_LABELS[n.eventKind] ?? n.eventKind   # raw kind, unstyled, obviously untranslated
# then the server's title and body, which are always present and never null
# no link, no crash, no "undefined", and NO filtered-out row
```

No exhaustive `switch`, no `throw new Error('unknown kind')`, no `assertNever`, and **never filter the row
out**. A hidden notification is worse than an ugly one: the badge says 3, the list shows 2, the user can
neither find nor clear the third, the badge sticks at 1 forever, and the only recovery is `mark-all-read`,
which is irreversible. Render the ugly row.

---

## 5. Step 5 — the unread badge, and mounting it

**File:** `frontend/src/slices/notifications/components/UnreadBadge.tsx`

An MUI `IconButton` wrapping a `Badge` around a bell icon, calling `useUnreadCount()` and navigating to
`/notifications`. It reads the count and nothing else — it never lists, never marks, never renders an error.
Then wire it per §0.2.

Implement screen spec §3.2 rules A–H in full. The three most often lost: render `data.unreadCount` and never
a cached page's length (a page holds 50, a user may have 63); `invisible` at zero, because a permanent grey
"0" trains everyone to stop looking; and an `aria-label` carrying the count, because on an icon-only button
the badge is the entire information content. There is deliberately **no `aria-live`** — a polite region on
a value repolled every 60 seconds interrupts a screen-reader user on a schedule.

### 5.1 Four ways the badge goes wrong

1. **Importing `UnreadBadge` into `AppShell.tsx`.** The app's only §1.4 rule A violation, and it pulls this
   slice into `/login`'s chunk. Use the slot (§0.2); criterion 3 catches it.
2. **Polling while anonymous.** The group has no `.RequireAuthorization()` and `CurrentUserFactory` answers
   `401` (`NotificationsEndpoints.cs:11-16`), so an anonymous poll is not a `200` with zero — it is a `401`
   every 60 seconds, each firing §2.3 rule H's redirect to the page already on screen.
3. **`refetchIntervalInBackground: true`** to "keep the badge fresh": it keeps the *session* fresh, forever.
4. **A banner or toast on a failed poll.** It fails on a 60-second timer and §5.3 forbids a global error
   toast. The bell renders with no badge and no error; the failure surfaces where it is locatable, as an
   `ErrorBanner` on `/notifications`.

---

## 6. Step 6 — `NotificationRow.tsx`

**File:** `frontend/src/slices/notifications/components/NotificationRow.tsx`

One `ListItem`: unread dot **and** bolder title (colour is never the only carrier of meaning, §8.4), the §4
label, the server's `title` and `body`, the timestamp, and — when unread — a *Mark as read* button carrying
that row's single id and no `ConfirmDialog`. Implement screen spec §4.4 rules A–F; three carry consequences
beyond tidiness.

**A. `title` and `body` render verbatim**, never paraphrased, prefixed or templated over. The producing
handler wrote them for this reader; §4's label is a category, not a replacement for the title.

**B. `body` is server-supplied text, rendered as text.** Never `dangerouslySetInnerHTML`, never a markdown
renderer, never `innerHTML`. Producers write `\n`, not markup, so use `whiteSpace: 'pre-line'`, truncated to
two lines with an expand affordance (the column is `TEXT` with no ceiling, `Core/Notification.cs:10`). Every
producer today is a server-side literal; the moment this system gains user-authored ticket comments a body
is attacker-influencable, and this rule is all that stands between that and stored XSS.

**C. `readAt`, `emailStatus` and `ticketId` are never rendered.** The dot already says what `readAt` says;
`emailStatus` is operator telemetry a recipient can only worry about; a raw `ticketId` GUID is a value with
no destination. `createdAt` goes through `shared/format/dates.ts` — it parses directly, but it still goes
through the one module, because that is where a timezone bug gets fixed once instead of six times.

---

## 7. Step 7 — `NotificationCentreScreen.tsx`

**File:** `frontend/src/slices/notifications/screens/NotificationCentreScreen.tsx`

`PageHeader` with *Mark all as read*, an "Unread only" checkbox, a `List` of `NotificationRow`, and a pager.
Registered at `/notifications` for all four roles (§4.1). Implement screen spec §4.1–4.4 and §6; the points
that bite:

**A. The heading carries the unread count** — "Notifications" over "3 unread" — from the same
`['notifications', 'unreadCount']` cache the badge reads: no second request, no second number to drift.
Focus lands there on route change (§8.4 item 3), which is where the count is spoken on arrival.

**B. A `List`, not `PaginatedTable`**; as a table it is three columns with one at 80% width and a header
labelling nothing. The pager is still `TablePagination` and **imports `PaginatedTable`'s 1-based/0-based
conversion** rather than re-deriving it (§3.3 item 3): a second copy is how the first or last row goes
missing with no error anywhere. Render it from `response.pageSize`, never the value sent —
`PaginatedQuery.Normalize` clamps to 50 and returns `200` (§2.4 item 6; punch-list item 17).

**C. Ordering is the server's** — `createdAt` desc then `id` desc (`ListMyNotificationsHandler.cs:44-45`),
not unread-first and never re-sorted client-side: the server paginates, so sorting reorders 15 rows out of
63 and produces an order that changes per page.

**D. *Mark all as read* is gated by `ConfirmDialog` naming the consequence.** There is no `mark-unread`
endpoint anywhere in the API and `is_read` is the row's only mutable field, so unread state once cleared
cannot be restored by any operation this application offers. The copy says exactly that — *"This cannot be
undone; there is no way to mark a notification unread again"* — not "are you sure?".

**E. Build the id array from the current render**, never from a `getQueryData` read of another key and never
from a discarded page. `MarkNotificationsReadHandler.cs:85-92` audits `PermissionDenied`/`Denied` when fewer
rows come back than ids were asked for **and still returns `200`** (punch-list item 18), so a stale id
manufactures a security event against an innocent user with no visible symptom in the UI.

**F. Snackbar copy comes from `markedCount`:** `1` → "1 notification marked read"; more → "12 notifications
marked read"; `0` → **"Nothing to mark read."** Zero is reachable with no error, because the handler counts
only rows not already read (`MarkNotificationsReadHandler.cs:62-69`): a double click or a second tab returns
`200` with `markedCount: 0`, and "0 notifications marked read" reads as a failure.

### 7.1 The state table

Implement every row of screen spec §4.3. Two are easy to miss: the **over-run page**
(`totalCount > 0 && items.length === 0`) is genuinely reachable, since marking read with *Unread only* on
shrinks the result under the cursor — `EmptyState` with *Back to the first page*, not "no results"; and
**`isFetching` with existing data keeps the rows** with subtle progress, because a skeleton on refetch
blanks a list being read (§7.4). There is **no `403` and no `404` state here**: every role may call every
endpoint, and an unowned id is filtered out with a `200`. A `403` means the catalogue changed and §6.1's
table is stale — fix the table, do not catch the error.

### What this step does NOT do, and why

Per screen spec §7, and none of these is a gap this plan fills later: no websockets, no `EventSource`, no
service worker, no browser `Notification` prompt, no sound, no count in `document.title`, no preferences or
per-kind mute, no delete/archive/dismiss, no client-side search or date filter, no polling of the list, and
**no cross-user view for any role, including an Accountant Admin support screen** —
`02-AuthorizationMatrix.md` §9 is absolute and no endpoint exists to build one on.

---

## 8. Cache reconciliation — the subtle part

Marking read changes two things the client holds: `isRead` on rows in the **list** query, and the integer in
the **unread-count** query. Both must move, and the badge must not flash a stale value after the list has
visibly updated.

**A. Invalidate both keys, by name, in `onSuccess`** — §3.2 rule C, screen spec §6 rule G, both mutations:

```ts
onSuccess: ({ markedCount }) => {
  queryClient.invalidateQueries({ queryKey: ['notifications', 'list'] });
  queryClient.invalidateQueries({ queryKey: ['notifications', 'unreadCount'] });
  showSnackbar(markedReadMessage(markedCount));
}
```

The list because `isRead` changed on rows it holds; the count because the badge derives from the server, not
from this response.

**B. No `setQueryData`.** §3.2 rule D's seed-from-the-response pattern needs a response that *is* the new
state, and `{ markedCount }` is a tally, not a row: this is the one slice where rule D does not apply. **Do
not invalidate `['notifications']` alone** either — it works and it is worse, a broader blast radius than
the two keys that changed, and a reader cannot tell which caches the author believed were affected.

**C. There is no flash of a stale badge, because nothing is guessed.** Both queries refetch from the
invalidation and land within a round trip of each other, and `staleTime: 30_000` keeps the poll from adding
a third request in the same window. The badge lags the click by one round trip and **that lag is accepted**:
§3.2 rule E bans optimistic updates, and here the guess is wrong in a common case — decrementing by
`notificationIds.length` overcounts whenever a row was already read in another tab, leaving the badge
*lower* than the truth until the next poll, on the one number a user might act on. With `unreadOnly` on, a
marked row vanishes one round trip later; a fade-out that removes it sooner is an optimistic update in
costume.

### 8.1 Three ways the reconciliation goes wrong

1. **Invalidating only the list.** Rows update, the bell keeps its old number until the next poll: up to 60
   seconds of a visibly wrong badge beside a visibly correct list, reported as "marking read does nothing".
2. **Invalidating only the count.** The badge drops, the row keeps its dot, and a second click returns
   `markedCount: 0` — "Nothing to mark read." on a row that still looks unread.
3. **Decrementing optimistically, then invalidating.** The badge jumps to a guess and corrects to a
   different number a moment later: two wrong values instead of one stale one, and the correction reads as
   a bug of its own.

---

## 9. Security invariants for this slice

**A.** Never rely on the React app to hide data. Every response here is already scoped to `CurrentUser.Id`
by the handler (`ListMyNotificationsHandler.cs:34`, `GetUnreadCountHandler.cs:27`,
`MarkNotificationsReadHandler.cs:59`) and no endpoint accepts a recipient; a UI filtering rows for security
is a UI concealing a server leak.

**B.** Out-of-scope means **`404`, not `403`**, and a `404` is never rendered as "forbidden" or "no
permission". No route here returns `404` today, but the rule binds the moment a notification links
anywhere: when the `Tickets` UI ships and a reader follows a link to a resource they can no longer see, the answer
is `404` and the only honest copy is "Not found." A `403` would confirm the row exists.

**C.** `body` is text, never HTML (§6 rule B). **D.** No token in `localStorage` and nothing to store: the
session is the `aa_session` HttpOnly cookie (`IdentityRegistration.cs:67-68`), unreadable from JavaScript,
and every call goes through `http.ts` with `credentials: 'same-origin'`. **E.** No API base-URL environment
variable, ever — every path is a relative string beginning `/api/`, with no `VITE_`, no `import.meta.env`
and no `http://` literal; CORS is never configured, in any environment. **F.** Do not weaken §7 rule E to
save a request: it is the difference between a clean audit log and false `Denied` entries in the one log an
investigator is supposed to trust.

---

## 10. Files checklist

Created by this plan, in this order:

- [ ] `frontend/src/slices/notifications/types.ts` — §1
- [ ] `frontend/src/slices/notifications/api.ts` — §2, four functions, both guards
- [ ] `frontend/src/slices/notifications/queries.ts` — §3, the one `refetchInterval` in the app
- [ ] `frontend/src/slices/notifications/eventKinds.ts` — §4, §5's table plus `destinationFor`
- [ ] `frontend/src/slices/notifications/components/UnreadBadge.tsx` — §5
- [ ] `frontend/src/slices/notifications/components/NotificationRow.tsx` — §6
- [ ] `frontend/src/slices/notifications/screens/NotificationCentreScreen.tsx` — §7

Touched, not owned — both belong to Phase 0, and nothing else under `frontend/src/shared/` is created,
renamed or moved by this plan (§0.1):

- [ ] `frontend/src/shared/components/AppShell.tsx` — add `notificationSlot?: ReactNode` and render it.
      **No import from `slices/`.** No change at all if Phase 0 already declared it
- [ ] `frontend/src/routes.tsx` — pass `notificationSlot={<UnreadBadge />}`; confirm `/notifications` is
      registered for all four roles

---

## 11. Questions to flag if unclear

- [ ] **`EmployeeRegistered` and `EmployeeDeparted` are missing from screen spec §5** (§0.4). Both have live
      producers and reach Customer Admins who can sign in, so both render today with a raw `eventKind`
      label. Two category labels are needed in that document; this plan may not invent them. **Decide
      before this slice ships.**
- [ ] **Is 60 seconds the right interval?** Screen spec §3.1 decides it and `GeneralUIArchitecture.md` §13
      asks the same question. Confirm or replace it there, once — not here.
- [ ] **What route do the ticket kinds link to?** `Screens/TicketsScreens.md` does not exist;
      `destinationFor` returns `null` until it does and names one.
- [ ] **Does Phase 0's `useSession()` distinguish `loading` from `anonymous`?** `LoginArchitecture.md` §1.2
      specifies three states but no document fixes the hook's return shape; the badge must be disabled in
      both non-authenticated ones.
- [ ] **Should `AccountSuspended` and the two invitation kinds be in-app notifications at all?** All three
      go to accounts that cannot sign in, so they accumulate unread and become the first thing a reactivated
      or newly-onboarded person sees.
- [ ] **Should `emailStatus` be visible anywhere?** `Failed`/`Abandoned` on an invitation is operationally
      important and invisible to every human today, but an operator surface needs a cross-user read, which
      `02-AuthorizationMatrix.md` §9 bans.
- [ ] **Is a "mark this page read" action wanted**, between one row and all rows? It is the only thing that
      would make the 200-id cap reachable, and the only reason to revisit §2.1 rule B.

---

## 12. Success criteria

Each is verified by running the app, not by reading the code. Nothing in this plan has ever been run: there
is no `frontend/` directory, and this machine has no local PostgreSQL, so no route, bound or status code
above has been observed in a response.

1. Signing in draws the bell for all four roles, clicking it lands on `/notifications`, and with nothing
   unread there is no badge — not a `0`.
2. `grep -rn "refetchInterval" frontend/src` returns exactly one file: this slice's `queries.ts`.
3. `grep -rn "slices/" frontend/src/shared` returns nothing, and `AppShell` renders correctly with
   `notificationSlot` omitted.
4. `POST /api/auth/request-password-reset` for the signed-in account raises the badge to `1` within one
   interval with no manual refresh; backgrounding the tab five minutes produces **zero** `unread-count`
   requests; the count is fresh within one interval of refocusing.
5. Signing out produces no further `unread-count` requests and no `401` on a timer.
6. `/notifications` lists newest first, read and unread interleaved, and `pageSize: 200` renders a pager
   consistent with the `50` the server returned, with no rows missing.
7. Toggling *Unread only* issues a new request rather than reusing the other filter's rows.
8. Marking one row read shows a snackbar built from the server's `markedCount`, and the badge changes only
   after the refetch — never instantly, and never to a value it then corrects.
9. Marking a row a second tab already marked shows "Nothing to mark read." and no error.
10. *Mark all as read* is gated by a `ConfirmDialog` stating the action cannot be undone, then reports the
    server's count and empties the badge.
11. With *Unread only* on and the cursor on page 2, marking page 2 read renders the over-run `EmptyState`
    with *Back to the first page*, not "no results".
12. A row inserted directly in the database with `event_kind = 'SomethingNobodyWrote'` renders its raw kind
    plus title and body, and is counted by the badge.
13. A `TicketSubmitted` row with a random `ticket_id` renders with no link and the "ticket screens do not
    exist" note, no click reaches `NotFoundPage`, and no `ticketId`, `readAt` or `emailStatus` value appears
    anywhere in the rendered DOM.
14. A `body` containing `<script>alert(1)</script>` renders those characters as visible text and executes
    nothing.
15. Stopping the API leaves the bell drawn with no badge and no toast, while `/notifications` renders "The
    server is unavailable."
16. Every string in this slice passes the vocabulary check: no "Client", no "Firm", no bare "Admin".
