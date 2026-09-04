# Notification Screens

This is the only feature all four roles use identically. `NotificationsActionCatalogue.cs` grants
`ReadOwnNotifications` and `MarkOwnNotificationRead` to all four and nothing else, and
[../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §9 closes the other direction
absolutely — *"Read another actor's notifications: **Nobody, including Accountant Admins**"*. No
endpoint accepts a recipient, so there is no row-scoping question, no two-DTO branch, and no
role-dependent column set anywhere below. It is also the only place in the application that
**polls**: [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §3.2 rule H puts
`refetchInterval` in exactly one file, and that file is in this slice.

Two awkward facts shape everything here. **Eleven of the eighteen event kinds in
`NotificationEvents.cs` are about Tickets, and Tickets has no screens** (§0.1 of the governing
doc) — those notifications arrive carrying a `ticketId` this SPA has nowhere to send the user, so
they render as text with **no link** (§5). And **exactly five of the eighteen have a producer in the
built backend** — grep `NotificationEvents.` across the slices and you get five call sites:
`Invited` (`InviteAccountantHandler.cs:136`), `PasswordResetRequested`
(`RequestPasswordResetHandler.cs:112`), `AccountSuspended` (`SuspendAccountantHandler.cs:74`),
`EmployeeRegistered` (`RegisterEmployeeHandler.cs:128`) and `EmployeeDeparted`
(`DepartEmployeeHandler.cs:116`). Note what is *not* there: **`EmployeeInvited` has no producer**,
despite being the obvious counterpart to `Invited` and despite appearing in §5's table.

Two of those five go to accounts that cannot sign in to read them — `Invited` has no password yet,
and `AccountSuspended` belongs to an account login now rejects with `401`. So **three kinds reach a
reader**: `PasswordResetRequested` for anyone, and `EmployeeRegistered` and `EmployeeDeparted` for a
Customer Admin. For an **Accountant** the centre is therefore empty today and §4.3's empty state is
where this screen will spend most of its life. For a Customer Admin at a Customer that is hiring it
is not empty at all: every registration and departure lands here (§5 rule E). Build the empty state
properly *and* the populated list properly; neither role is the only one that matters.

**Documents that govern this one, in precedence order.** Where any of them disagrees with this
document, **they win and this document is wrong** — fix this document, do not code around it.

- [../../README.md](../../README.md) — *Locked platform decisions*, *Conflict precedence*
- [../../00-Glossary.md](../../00-Glossary.md) — banned terms; binding in UI copy
- [../../01-DomainModel.md](../../01-DomainModel.md) §7, §9.2 — Notification; nothing is deleted
- [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §9 — normative, and short
- [../../04-Infrastructure.md](../../04-Infrastructure.md) §1–3, §5a — hosting, dev loop, outbox
- [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) — the governing UI document; cited
  below at §1.4 A/C/E, §2.3 C, §2.4, §3.1, §3.2 D/E/H, §3.3, §5.1, §5.3, §7.1, §7.2, §7.4, §8.3,
  §8.4, §9.2, §10.2. Its rules are cited, never restated
- [../../Slices/Notifications/IMPLEMENTATION_PLAN.md](../../Slices/Notifications/IMPLEMENTATION_PLAN.md)
  — §3 the event catalogue, §13 constraints, §14 open questions

---

## 0. Role coverage

| README brief | Role | Covered by | Notes |
|---|---|---|---|
| "Shared: notification centre" | AA, AU, CA, EMP | §4 | One screen, one shape, no role branches |
| (not in the brief) Unread badge | AA, AU, CA, EMP | §3 | Not a route; lives in the AppBar (§5.1) |
| (not in the brief) Preferences / mute | — | **Nowhere.** No endpoint | §7 item 7 |
| (not in the brief) Email delivery status | — | **Not rendered** | §4.4 rule F |

No `can()` call appears on this screen. Both actions are granted to all four roles, so the check
could only ever pass — the reasoning `IdentityActionCatalogue.cs` gives for omitting login. The two
rows in §6.1's table exist so it matches the server catalogue, not to be called from here.

---

## 1. Endpoints this slice consumes

AA = `AccountantAdmin`, AU = `AccountantUser`, CA = `CustomerAdmin`, EMP = `Employee`. Every row is
scoped to `CurrentUser.Id` by the handler; none accepts a recipient.

| Route | Verb | Request | Response | Roles | Notes |
|---|---|---|---|---|---|
| `/api/notifications/list` | **POST** | `{ unreadOnly, pageNumber, pageSize }` | `PaginatedResponse<NotificationDto>` | AA, AU, CA, EMP | A **POST read** (note 1). `.Produces<object>` — see callout |
| `/api/notifications/unread-count` | **GET** | none | `{ unreadCount }` | AA, AU, CA, EMP | No query string. The polling query, §3.1 |
| `/api/notifications/mark-read` | POST | `{ notificationIds }` | `{ markedCount }` | AA, AU, CA, EMP | Non-empty, ≤ 200. §6 |
| `/api/notifications/mark-all-read` | POST | **no body** | `{ markedCount }` | AA, AU, CA, EMP | Irreversible. §6 rule F |

1. **`list` is a POST read; `unread-count` is a GET. Do not "correct" either.** §2.3 rule C names
   the list route by name. Changing either verb produces a `405` with nothing in the body to explain
   it. `api.ts` uses `post` for one and `get` for the other, and the asymmetry is deliberate.
2. **`mark-all-read` takes no body.** Call `post('/api/notifications/mark-all-read')` with no second
   argument. Sending `{}` works today but asserts a contract the endpoint does not have.
3. `NotificationDto` is `{ id, ticketId?, eventKind, title, body, isRead, readAt?, createdAt,
   emailStatus? }`. `createdAt`/`readAt` are `DateTimeOffset`, so §10.2's third row applies.
4. `pageSize` is **clamped to 50, not rejected** (`PaginatedQuery.Normalize`). Render the pager from
   `response.pageSize`, per §2.4 item 6.
5. The group carries **no `.RequireAuthorization()`** — deliberate, and the endpoint file explains
   why. `CurrentUserFactory` throws `401`, so an anonymous `unread-count` is a `401` and not a `200`
   with zero. That is why §3.2 rule D exists.

> **`/api/notifications/list` declares `.Produces<object>(200)` and actually returns
> `PaginatedResponse<NotificationDto>`** — `ListMyNotificationsHandler` builds the envelope. The
> declaration is wrong, not the handler; already item 10 of the governing doc's §12. Type
> `listNotifications` from the handler, never from `.Produces`. Repeated in §9.

---

## 2. Routes and screens

| SPA path | Screen | Roles |
|---|---|---|
| `/notifications` | `NotificationCentreScreen` | AA, AU, CA, EMP |

One route, no detail route: a notification has no field a row does not already show, and there is no
by-id endpoint to serve one. The badge is not a route — it is mounted in the AppBar on every shell
route, which is exactly why §3.2 rule D matters.

---

## 3. The unread badge in the AppBar

**File:** `frontend/src/slices/notifications/components/UnreadBadge.tsx`

The bell sits where [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §5.1's diagram draws
`[bell 3]`, left of the account menu, and navigates to `/notifications`.

> **Mounting it is a dependency decision, and the answer is a slot, not an import.** `AppShell.tsx`
> is in `shared/components/`, and §1.4 rule A forbids `shared/` importing `slices/` — *"it will drag
> that slice into the bundle of every screen that uses it."* Importing `UnreadBadge` there would be
> the only rule-A violation in the app and would pull this slice's `api.ts`, `types.ts` and
> `queries.ts` into the entry chunk of every screen, including `/login`, which draws no shell.
> **Do this instead:** `AppShell` takes `notificationSlot?: ReactNode` and renders it in the AppBar;
> `routes.tsx` — the one file whose job is already importing every slice's screens (§1.4 rule E) —
> passes `<AppShell notificationSlot={<UnreadBadge />} />`. One prop, no new exception, and the
> coupling sits in the file meant to hold it. Do not implement the import and apologise in a comment.

### 3.1 The one polling query in the application

**File:** `frontend/src/slices/notifications/queries.ts`

```ts
/** The ONLY polling query in this application (GeneralUIArchitecture.md 3.2 rule H). A
 *  refetchInterval anywhere else is a change to that document, not a local decision. */
export function useUnreadCount() {
  const { session } = useSession();
  return useQuery({
    queryKey: ['notifications', 'unreadCount'],
    queryFn: getUnreadCount,
    // 60s is a decision THIS document makes; nothing upstream specifies it. See section 9.
    refetchInterval: 60_000,
    // Left at its default (false) DELIBERATELY. Do not set it true. aa_session is 8h SLIDING, so a
    // backgrounded tab polling every minute renews it forever and the expiry never fires -- an
    // unattended machine stays signed in overnight. It also spends ~480 requests a workday
    // redrawing a number nobody is looking at.
    refetchIntervalInBackground: false,
    // No session means CurrentUserFactory answers 401 (section 1 note 5), so polling anonymously is
    // a 401 every 60s, each one firing section 2.3 rule H's bounce to /login.
    enabled: session !== undefined,
    staleTime: 30_000,
  });
}
```

### 3.2 Rules

**A. Render `data.unreadCount`, never a cached page's length.** A page holds at most 50 rows, so a
user with 63 unread would see "50", and the number would change as they paged.

**B. `unreadCount === 0` renders the bell with no badge.** MUI's `Badge` shows the zero by default;
use `invisible`. A permanent grey "0" beside a bell trains everyone to stop looking at it.

**C. A failed poll renders the bell with no badge and no error.** §5.3 forbids a global error toast,
and this query fails on a 60-second timer — one banner a minute for a number the user never asked
for. The failure becomes visible where it is locatable: `/notifications` shows an `ErrorBanner`.

**D. Disabled with no session** (rule in §3.1's code). Otherwise the badge polls `/login` and each
`401` fires a redirect to the page already on screen.

**E. `refetchIntervalInBackground` stays `false`.** Both consequences — a session that never
expires, and 480 pointless requests a day — are invisible in development, where nobody leaves a tab
open for eight hours.

**F. The bell is icon-only, so its `aria-label` carries the count** (§8.4 item 4):
`count > 0 ? \`Notifications, ${count} unread\` : 'Notifications, none unread'`. Without it a
screen-reader user hears "Notifications, button" and the badge — the entire information content — is
a visual-only signal.

**G. The count is deliberately NOT announced when it changes.** No `aria-live`. A polite live region
on a value that repolls every 60 seconds interrupts a screen-reader user mid-sentence, on a
schedule, with a number they cannot act on from where they are standing. It is announced on focus
(rule F) and again in the page heading on arrival (§4.4 rule A). This errs towards being quiet,
which is recoverable; the alternative talks over its user once a minute forever.

**H. No other query gets a `refetchInterval`.** Grep must return one file — success criterion 3.

---

## 4. Screen: Notification centre (`/notifications`)

### 4.1 Layout

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Notifications                                    [ Mark all as read ]   │ PageHeader
│  3 unread                                                                │
│  ──────────────────────────────────────────────────────────────────────  │
│  [x] Unread only                                                        │ filter
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │ ●  Password reset  Password reset requested       2 Sep, 14:33      │  │ unread
│  │    A password reset was requested… [ Mark as read ]                 │  │
│  ├────────────────────────────────────────────────────────────────────┤  │
│  │    Ticket submitted  A new ticket was submitted.  31 Aug, 16:40     │  │ no link
│  │    Not available yet — ticket screens do not exist                  │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                      Rows per page: 15   1–3 of 3  < >   │ pager
└──────────────────────────────────────────────────────────────────────────┘
```

A `List`, not `PaginatedTable`. Every other list here is tabular because its rows are records with
columns; a notification is a kind label, a title, a sentence and a timestamp, one of which wraps —
as a table it is three columns with one at 80% width and a header labelling nothing. The pager is
still `TablePagination`, and it **imports `PaginatedTable`'s 1-based/0-based conversion** rather than
re-deriving it, because §3.3 item 3 requires that conversion to exist in exactly one place.

The unread marker is a filled dot **and** a bolder title: colour is never the only carrier of
meaning (§8.4).

### 4.2 Data and query keys

Per §3.1 of the governing document.

| Query | Key |
|---|---|
| The list | `['notifications', 'list', { unreadOnly, pageNumber, pageSize }]` |
| The badge count | `['notifications', 'unreadCount']` |

`unreadOnly` **must** be in the key: it changes the response, and two filters sharing one cache entry
is how a screen shows the wrong rows. It is React state, not a URL parameter — a bookmarkable
`?unreadOnly=true` is a saved link to a list that empties itself as the user reads.

**Ordering is `createdAt` desc, then `id` desc** (`ListMyNotificationsHandler`), **not**
unread-first. Do not re-sort client-side: the server paginates, so sorting reorders 15 rows out of
63 and produces an order that changes per page.

### 4.3 States

| State | Condition | Render |
|---|---|---|
| First load | `isLoading` | `Skeleton` rows, header and pager in place (§7.4) |
| Refetch | `isFetching && data` | Keep the rows, subtle progress — never a skeleton (§7.4) |
| Empty | `totalCount === 0 && !unreadOnly` | `EmptyState` "No notifications yet." No action — nothing a user does creates one |
| Empty, filtered | `totalCount === 0 && unreadOnly` | `EmptyState` "Nothing unread." Action: *Show all* |
| Over-run page | `totalCount > 0 && items.length === 0` | `EmptyState` with *Back to the first page* (§3.3 item 2). Reachable here: marking read while filtered shrinks the result under the cursor |
| Query failed | `isError` | `ErrorBanner` replaces the list; header and pager stay (§7.2) |
| `401` | any call | Redirect to `/login`; nothing rendered (§2.3 rule H) |
| Mark succeeded | `onSuccess` | `Snackbar` from `markedCount` (§6 rule D) |
| Mark failed | mutation `isError` | `ErrorBanner` above the list (§7.2, row-action row) |

There is no `403` and no `404` state on this screen: every role may call every endpoint, and an
unowned id is filtered out with a `200` rather than refused (§6 rule E).

### 4.4 Rules

**A. The heading carries the unread count** — "Notifications" over "3 unread", from the same
`unreadCount` cache the badge uses. Focus moves there on route change (§8.4 item 3), so the count is
spoken once, on arrival — the announcement §3.2 rule G declines to make on a timer.

**B. Render the server's `title` and `body` verbatim.** The producing handler wrote them
(`"Password reset requested"` / `"A password reset was requested for your account. Check your email
for the link."`), they are stored on the row and never edited (plan §13 item 4). Do not paraphrase,
prefix or template over them. §5's table supplies a **category label** for the kind, not a
replacement for the title.

**C. `body` is plain text — render it as text.** Never `dangerouslySetInnerHTML`. Producers write
`\n`, not markup; use `whiteSpace: 'pre-line'`. A notification body is attacker-influencable input
in any system that eventually gains user-authored ticket comments, and this one will.

**D. Truncate `body` to two lines with an expand affordance.** It is `TEXT` with no ceiling in the
DTO; one 4,000-character body pushes every other row off the screen.

**E. `createdAt` goes through `shared/format/dates.ts`**, browser-local, per §10.2. It carries an
offset so it parses directly — but it still goes through the one module, because that is where a
timezone bug gets fixed once instead of in six screens.

**F. Do not render `readAt`, `emailStatus`, or `ticketId`.** `readAt` tells the reader what the dot
already tells them. `emailStatus` (`Pending`/`Sent`/`Failed`/`Abandoned`/`Skipped`/`null`) is
operator telemetry the recipient can only worry about — and the notifications whose mail matters
most go to people who cannot sign in to see it (§9 item 5). A raw `ticketId` GUID is a value with no
destination (§5).

---

## 5. Event kinds — copy and destination

`NotificationEvents.cs` defines eighteen kinds. This table is the whole mapping and lives in code as
`frontend/src/slices/notifications/eventKinds.ts`, so adding a kind edits one file. The **copy**
column is the *category label* rendered as the row's kind chip; it is not the row's text (§4.4 rule
B owns that). **Every destination is `none` today** — that is the state of the application, not a
gap in this table.

| Event kind | User-facing copy | Destination link | Available today? |
|---|---|---|---|
| `Invited` | Invitation | none — accepting happens via the emailed `/accept-invitation` link | No: recipient cannot sign in |
| `EmployeeInvited` | Invitation | none — as above | **No producer at all** — see the preamble |
| `PasswordResetRequested` | Password reset | none — the token is in the email only; the stored body deliberately holds none | **Yes** |
| `EmployeeRegistered` | Employee registered | **none** — see rule E | **Yes** — Customer Admins |
| `EmployeeDeparted` | Employee departed | **none** — see rule E | **Yes** — Customer Admins |
| `AccountSuspended` | Account suspended | none — a suspended account cannot sign in | No: unreadable by design |
| `TicketPickedUp` | Ticket picked up | **none** — Tickets has no screens | No |
| `InformationRequested` | Information requested | **none** | No |
| `FieldRejected` | Field rejected | **none** | No |
| `TicketAnswered` | Ticket answered | **none** | No |
| `TicketClosed` | Ticket closed | **none** | No |
| `TicketCancelled` | Ticket cancelled | **none** | No |
| `AccountantResponded` | Accountant responded | **none** | No |
| `TicketSubmitted` | Ticket submitted | **none** | No |
| `CorrectionSubmitted` | Correction submitted | **none** | No |
| `CustomerReplied` | Customer replied | **none** | No |
| `TicketAssignedToYou` | Assigned to you | **none** | No |
| `DueDateApproaching` | Due date approaching | **none** | No producer at all |

**A. The eleven ticket kinds render with no link, and the row says so** — one muted line under the
body: "Not available yet — ticket screens do not exist". Do not invent `/tickets/:id`, do not draw a
disabled link, do not hide the row. Inventing the route ships eleven dead links that render
`NotFoundPage` and read as a broken application; the note reads as an unfinished one, which is true.

**B. `DueDateApproaching` has no producer, and is not therefore unreachable.** Plan §3 rule 5: the
constant exists, nothing emits it, building a scheduler for it is forbidden. It is in the table so
the row renders if it ever arrives.

**C. One place holds the future link.**

```ts
/** Returns null for every kind today. When Tickets ships, the ticket-bearing kinds return the route
 *  that Screens/TicketsScreens.md names -- read it from that document, do not guess a path here.
 *  Keeping the function (rather than inlining null) makes that a one-line change in one file. */
export function destinationFor(n: Notification): string | null {
  return null;
}
```

**D. An unrecognised kind renders, visibly plain, and never disappears.** If `eventKind` is not in
the map: show the raw kind string as the label — unstyled and obviously untranslated — then the
server's `title` and `body`, which are always present. No link, no crash, no `undefined`, and **no
filtered-out row**. A hidden notification is worse than an ugly one: the badge said 3, the list shows
2, and the user can neither find the third nor clear it, so the badge sticks at 1 forever — and the
only recovery is `mark-all-read`, which is irreversible. Render the ugly row.

**E. `EmployeeRegistered` and `EmployeeDeparted` are the two kinds a signed-in user actually
receives in volume, and neither gets a link.** Both are produced inside the registration and
departure transactions (`RegisterEmployeeHandler.cs:128`, `DepartEmployeeHandler.cs:116`) and both
are addressed to the Customer's own Admins — who *can* sign in, unlike the recipients of `Invited`,
`EmployeeInvited` and `AccountSuspended`. So these are the first notifications this screen will show
a real reader, and the empty-state assumption in the preamble is weaker than it looks for a Customer
Admin at a growing Customer.

They still get **no destination link**, and that is a decision rather than a gap: the obvious target
is `/employees/:employeeId`, but the notification body deliberately carries the person's name and
**not** their id (read the producer — the id is not in the payload), so there is nothing to build a
route from without a second lookup. Do not add one by searching the employee list for a name match;
two employees may share a name, and a wrong link is worse than none. If a link is wanted later, the
producer must carry the id — that is a backend change, recorded in
[../BACKEND_CHANGES_REQUIRED.md](../BACKEND_CHANGES_REQUIRED.md), not a client workaround.

---

## 6. Marking read

**A. `mark-read` sends `{ notificationIds }` — non-empty, at most 200.**
`MarkNotificationsReadHandler` holds `private const int MaxIdsPerRequest = 200` and two `422`s with
these exact messages:

| Condition | Status | Server message |
|---|---|---|
| null or empty array | `422` | `NotificationIds cannot be empty.` |
| more than 200 ids after `Distinct()` | `422` | `No more than 200 notifications can be marked in one request.` |

**B. Never send an empty array.** Guard in `api.ts`, before the request: an empty array is a `422`
for a no-op the user did not ask for, rendered as a banner naming a C# DTO property — the least
useful message in the application. This is §9.2's `markRead ids` row, and the client is the only
place it can be stopped before the round trip.

**C. Assert the 200 cap client-side; do not chunk.** With `pageSize` clamped to 50, a "mark this page
read" action sends at most 50 ids, so the cap is unreachable today and chunking would be dead code.
Assert it anyway, so a future bulk action fails loudly here rather than quietly at the server:

```ts
const MARK_READ_MAX_IDS = 200; // MarkNotificationsReadHandler.MaxIdsPerRequest

export function markNotificationsRead(notificationIds: string[]): Promise<MarkReadResult> {
  // Fails HERE, in development, on the first run -- not as a 422 naming a C# property for one
  // unlucky heavy user in production.
  if (notificationIds.length === 0) throw new Error('markNotificationsRead: no ids');
  if (notificationIds.length > MARK_READ_MAX_IDS)
    throw new Error(`markNotificationsRead: ${notificationIds.length} ids exceeds ${MARK_READ_MAX_IDS}`);
  return post('/api/notifications/mark-read', { notificationIds });
}
```

**D. Both mutations return `{ markedCount }` — put it in the snackbar.** Successes are the only
toasts (§7.2, last row). `1` → "1 notification marked read"; more → "12 notifications marked read";
`0` → **"Nothing to mark read."** Zero is reachable with no error at all: the handler counts only
rows that were not already read, so a double click, or a second tab that got there first, returns
`200` with `markedCount: 0`, and "0 notifications marked read" reads as a failure.

**E. Only ever send ids from the rows currently rendered.** The handler filters by
`RecipientUserId == user.Id` and, when fewer rows come back than ids were asked for, writes an
**audited `PermissionDenied` / `Denied`** entry against the caller — and still returns `200`. A stale
id from a discarded cache page therefore manufactures a security event against an innocent user and
shows nothing wrong in the UI. Build the array from the current render, never from a
`getQueryData` read of another key.

**F. `mark-all-read` is irreversible and needs a `ConfirmDialog`.** There is no `mark-unread`
endpoint anywhere in the API and `is_read` is the only mutable field on the row (plan §13 item 4), so
unread state once cleared cannot be restored by any operation the application offers. That is
precisely what §8.3 reserves `ConfirmDialog` for, and the dialog must name the consequence — "This
cannot be undone; there is no way to mark a notification unread again" — not ask "are you sure?". A
single row's *Mark as read* gets no dialog: one visible row, and the same rule would put a modal in
front of every click.

**G. No optimistic updates. The badge lags by one round trip, and that is accepted.** §3.2 rule E
bans them outright and §9.4 gives the reason: no concurrency token exists anywhere in this backend,
so the client cannot know its guess matches what was written. Here the guess is wrong in a common
case — decrementing by `notificationIds.length` overcounts whenever a row was already read in
another tab, leaving the badge *lower* than the truth until the next poll, on the one number a user
might act on. So marking read invalidates both keys and waits:

```ts
const markRead = useMutation({
  mutationFn: markNotificationsRead,
  onSuccess: ({ markedCount }) => {
    // Both keys, always. The list because isRead changed on rows it holds; the count because the
    // badge derives from the server, not from this response. No setQueryData: rule D's
    // seed-from-the-response pattern needs a response that IS the new state, and { markedCount }
    // is a tally, not a row.
    queryClient.invalidateQueries({ queryKey: ['notifications', 'list'] });
    queryClient.invalidateQueries({ queryKey: ['notifications', 'unreadCount'] });
    showSnackbar(markedReadMessage(markedCount));
  },
});
```

Invalidating `['notifications']` alone also works and is *worse*: a broader blast radius than the two
keys that changed, and a reader cannot tell which caches the author believed were affected.

**H. With `unreadOnly` on, a row marked read vanishes one round trip later.** That is the filter
working, not a glitch — the clicked row stays on screen for a moment. Do not paper over it with a
fade-out that removes the row before the server confirms; that is an optimistic update in costume.

---

## 7. What these screens must NOT do

1. **No websockets.** No SignalR hub and no `MapHub` exists in the API; the client would connect to
   nothing and retry forever.
2. **No `EventSource`.** No route emits `text/event-stream`.
3. **No service worker.** It needs something to push to it (item 1), and it caches the SPA shell —
   a stale worker serves last week's `index.html` after a deploy, silently.
4. **No browser `Notification` API, no `requestPermission()`.** A prompt on first load, before the
   user has done anything, is hostile — and it is one-shot: *Block* can never be re-asked.
5. **No sound.** This is a back-office tool used all day in a shared room, not a chat client.
6. **No unread count in `document.title`.** A second badge to keep in sync, flickering every poll.
7. **No preferences, mute, or per-kind opt-out.** No endpoint, no column; `NotificationEvents.Emailed`
   is a compile-time set (plan §3 rule 4). A settings screen that persists nothing is worse than none.
8. **No delete, archive or dismiss.** Nothing in this system is deleted (plan §13 item 3) and no
   endpoint exists. *Mark as read* is the only disposal.
9. **No client-side search or date filter.** `unreadOnly` is the only filter the DTO accepts;
   anything else filters one page of a server-paginated list and silently misses rows.
10. **No polling of the list** (§3.2 rule H). A list repaginating under the reader every minute
    loses their place.
11. **No cross-user view, for any role, including an Accountant Admin support screen.**
    [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §9 is absolute and no
    endpoint exists to build one on.

---

## 8. Behavioural cases

- [ ] Signing in draws the bell; with nothing unread there is **no badge**, not a `0`.
- [ ] `POST /api/auth/request-password-reset` for your own signed-in email (it does not change the
      password) produces one notification, and the badge reaches `1` within the poll interval.
- [ ] Backgrounding the tab for five minutes fires **no** `unread-count` requests; it refreshes on
      refocus.
- [ ] Signing out stops polling entirely — no `401`s on a timer in the network panel.
- [ ] `/notifications` lists newest first, read and unread interleaved.
- [ ] Toggling *Unread only* refetches rather than reusing the other filter's rows.
- [ ] Marking one row read drops the badge **after** the refetch, never before.
- [ ] Marking a row a second tab already marked shows "Nothing to mark read." — not an error, and
      not "0 notifications marked read".
- [ ] *Mark all as read* confirms the irreversibility before any request, then reports
      "12 notifications marked read" and empties the badge.
- [ ] *Mark all as read* with *Unread only* on renders "Nothing unread." with *Show all*.
- [ ] With `unreadOnly` on and the cursor on page 2, marking page 2 read renders the over-run
      `EmptyState` with *Back to the first page*, not "no results".
- [ ] A row with `event_kind = 'SomethingNobodyWrote'`, inserted directly in the database, renders
      its raw kind plus title and body and is counted by the badge.
- [ ] A row with `event_kind = 'TicketSubmitted'` and a random `ticket_id` renders with no link and
      the "ticket screens do not exist" note.
- [ ] Requesting `pageSize: 200` renders a pager consistent with the 50 the server returned.
- [ ] Stopping the API leaves the bell drawn with no badge and no toast; `/notifications` says
      "The server is unavailable."

---

## 9. Questions to flag if unclear

- [ ] **Is 60 seconds the right poll interval?** This document decides it; nothing upstream
      specifies it, and the governing doc's §13 asks the same question. 60s means up to a minute of
      staleness and roughly 480 requests per user per workday. Confirm or replace it here, once.
- [ ] **What route do the eleven ticket kinds link to?** `TicketsScreens.md` does not exist;
      `destinationFor` returns `null` until it does and names one (§5 rule C).
- [ ] **`/api/notifications/list` declares `.Produces<object>`.** Already item 10 of the governing
      doc's §12; it must be fixed on the server before any generated client is trusted here.
- [ ] **Should `AccountSuspended` and the two invitation kinds be in-app notifications at all?** All
      three go to accounts that cannot sign in, so they accumulate unread and become the first thing
      a reactivated or newly-onboarded person sees. Intended, or email-only?
- [ ] **Should `emailStatus` be visible anywhere?** `Failed`/`Abandoned` on an invitation is
      operationally important and currently invisible to every human; plan §14 item 5 asks the same
      from the server side. An operator surface needs a cross-user read, which §9 of the matrix bans.
- [ ] **Is a "mark this page read" action wanted**, between one row and all rows? It is the only
      thing that would make §6 rule C's cap reachable.
- [ ] **Volume.** Plan §14 items 2–4 ask whether every Accountant gets a notification per submitted
      ticket. If yes, a list with no filter beyond `unreadOnly` is not enough of a screen, and this
      document needs revising before `Tickets` ships.

---

## Files checklist

- [ ] `frontend/src/slices/notifications/types.ts` — `Notification`, `MarkReadResult`,
      `UnreadCountResult`, each commented with the C# DTO it mirrors
- [ ] `frontend/src/slices/notifications/api.ts` — four functions; the `post`/`get` split of §1
      note 1 and the guards of §6 rule C
- [ ] `frontend/src/slices/notifications/queries.ts` — `useNotifications`, `useUnreadCount` (the one
      `refetchInterval` in the app), `useMarkRead`, `useMarkAllRead`
- [ ] `frontend/src/slices/notifications/eventKinds.ts` — the §5 table plus `destinationFor`
- [ ] `frontend/src/slices/notifications/components/UnreadBadge.tsx`
- [ ] `frontend/src/slices/notifications/components/NotificationRow.tsx`
- [ ] `frontend/src/slices/notifications/screens/NotificationCentreScreen.tsx`
- [ ] `frontend/src/shared/components/AppShell.tsx` — add `notificationSlot?: ReactNode`. No import
      from `slices/`
- [ ] `frontend/src/routes.tsx` — pass `notificationSlot={<UnreadBadge />}`; register
      `/notifications` for all four roles

---

## Success criteria

Each is verified by running the app, not by reading the code.

1. The bell appears for all four roles and clicking it lands on `/notifications`.
2. With nothing unread there is no badge — not a `0`.
3. `grep -rn "refetchInterval" frontend/src` returns exactly one file: this slice's `queries.ts`.
4. `grep -rn "slices/" frontend/src/shared` returns nothing.
5. Requesting a password reset for the signed-in account raises the badge to `1` within one poll
   interval, with no manual refresh.
6. Backgrounding the tab five minutes produces zero `unread-count` requests, and the count is fresh
   within one interval of refocusing.
7. Signing out produces no further `unread-count` requests and no `401` on a timer.
8. `/notifications` lists newest first, interleaved, and the pager reports the server's `pageSize`
   after a request for 200.
9. Marking one row read shows a snackbar built from the server's `markedCount`, and the badge changes
   only after the refetch — never instantly.
10. Marking an already-read row shows "Nothing to mark read." and no error.
11. *Mark all as read* is gated by a `ConfirmDialog` stating the action cannot be undone.
12. A row with an unknown `event_kind` renders visibly plain with its raw kind, title and body, and
    is included in the unread count.
13. A `TicketSubmitted` row renders with no link and the "ticket screens do not exist" note; no click
    on this screen reaches `NotFoundPage`.
14. No `ticketId`, `readAt` or `emailStatus` value appears in the rendered DOM.
15. Stopping the API leaves the bell drawn with no badge and no toast; `/notifications` renders
    "The server is unavailable."
16. Every string in this slice passes the vocabulary check: no "Client", no "Firm", no bare "Admin".
