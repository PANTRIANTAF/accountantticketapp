# Login and Session Architecture — Frontend

This is the first thing to build in the React application and the last thing anybody thinks
about again, which is why it has its own document. Everything else in the SPA assumes a
resolved session: the shell cannot draw its navigation without a role, `RequireRole` cannot
decide anything without one, and `can()` returns nothing useful. Get this wrong and every
other screen fails in a way that points nowhere near here.

The backend half is **already built** — `Slices/Identity` is complete and wired in
`Program.cs`. Nothing in this document is a proposal. Every route, status code, message and
lifetime below was read out of the shipped code, and where the code and a specification
disagree, that disagreement is called out rather than smoothed over.

The authentication design is unusually opinionated and several of its decisions look like
bugs from the client side until you know why they were made. A caller who "fixes" one of them
reintroduces an account-enumeration oracle or a lockout denial-of-service. Read §0 and §2.4
before writing the login form.

**Documents that govern this document, in precedence order:**

| Document | What it settles for this file |
|---|---|
| `README.md` — *Locked platform decisions* | Cookie session, no JWT, no bearer token, nothing in `localStorage`. Four roles. At least one Active Accountant Admin always exists. |
| `02-AuthorizationMatrix.md` §11 | Resetting another person's password directly is permitted to **nobody**. |
| `04-Infrastructure.md` §1–2 | Same origin in every environment. CORS is never configured. No API base-URL variable. |
| `App/GeneralAppArchitecture.md` §8–9 | All routes under `/api`, kebab-case, ids in the body, no PUT/PATCH/DELETE. |
| `UI/GeneralUIArchitecture.md` | The API client (§2), TanStack Query rules (§3), routing and guards (§4), the error taxonomy (§7), forms (§9), wire formats (§10). |

Where any of them disagrees with this document, **they win and this document is wrong**. Do
not resolve a contradiction by inventing a third behaviour — flag it (`README.md` §*Conflict
precedence*).

---

## 0. Prerequisites — what the backend actually does

### 0.1 The cookie

Read from `Slices/Identity/IdentityRegistration.cs`:

| Property | Value | Why the client cannot work around it |
|---|---|---|
| Name | `aa_session` | Never referenced in client code. It is `HttpOnly`; JavaScript cannot read it. |
| `HttpOnly` | `true` | `document.cookie` will not show it. A debugging session spent looking for it is a wasted afternoon. |
| `Secure` | `Always` (not `SameAsRequest`) | Over plain `http://localhost` the browser **still stores it** for localhost specifically; on any other plain-HTTP origin it will not, and login will appear to succeed and then every subsequent call 401s. |
| `SameSite` | `Strict` | Works only because the SPA and the API share an origin. This is the reason `04-Infrastructure.md` puts the SPA inside the `app` container. |
| `Path` | `/` | Sent with SPA routes as well as `/api` routes. |
| `Domain` | deliberately unset | Host-scoped. Do not expect the cookie on a different subdomain. |
| Lifetime | 8 hours, **sliding** | Every authenticated request extends it. A user who leaves the app open with a polling query never expires — see `NotificationsScreens.md` §3.1. |

### 0.2 Why there is no token, no `localStorage`, and no refresh flow

- There is **no JWT and no bearer token anywhere in this application**, and therefore no
  signing key, no `Authorization` header, and no place a token could be stored
  (`README.md`, locked; `04-Infrastructure.md` §4). If you find an older draft mentioning
  `JwtOptions` or a `SecretKey`, it is stale.
- There is **no refresh endpoint**, because there is nothing to refresh. Sliding expiry is
  handled by the cookie middleware re-issuing `Set-Cookie` on its own. A client-side refresh
  timer would be a timer that calls nothing.
- **Nothing about the session is written to `localStorage` or `sessionStorage`** — see §1.3.

The whole of the client's authentication code is therefore: send requests with
`credentials: 'same-origin'` (`GeneralUIArchitecture.md` §2.3 rule B) and handle 401
(rule H). There is no header to attach and no token to manage. Any code that looks like token
management is code that should not exist.

### 0.3 A 401 is JSON, never a 302

ASP.NET Core's cookie handler redirects an unauthenticated request to `/Account/Login` by
default. `IdentityRegistration.cs` overrides both events:

```
OnRedirectToLogin        -> 401, empty body
OnRedirectToAccessDenied -> 403, empty body
```

The comment in that file names the symptom if the override is ever removed: *"the app
randomly shows the index page inside a JSON parse error"*. Once
`MapFallbackToFile("index.html")` is registered (`BACKEND_CHANGES_REQUIRED.md` item 1), a 302
to a non-existent page returns the SPA's own HTML with a `200`, and `await response.json()`
throws a syntax error from a request that looks like it succeeded.

Two consequences for the client:

- **A 401 body may be empty.** These two overrides write a status code and nothing else, so
  `parseProblemDetails` must tolerate a zero-length body and still produce an `ApiError` with
  `status: 401` (`GeneralUIArchitecture.md` §2.3 rule G). A `title` is not guaranteed on a
  401, unlike every 401 thrown by a handler.
- **Never follow a redirect for authentication.** There are none. If a response to an `/api`
  call is ever a redirect, something is wrong with the hosting configuration, not with the
  session.

---

## 1. Session bootstrap

### 1.1 `GET /api/auth/me` on application start

**File:** `frontend/src/slices/identity/api.ts`

```ts
export async function getSession(): Promise<SessionDto> {
  return http.get<SessionDto>('/api/auth/me');
}
```

`/api/auth/me` is the one **GET** in `/api/auth` and the one endpoint the front end calls on
every page load. It is authenticated by construction: the handler takes `CurrentUser`, and
`CurrentUserFactory` throws 401 when there is no principal. There is no anonymous variant and
no "is anyone logged in" endpoint that returns `false`.

**A 401 from `/api/auth/me` is the normal answer for an anonymous visitor. It is not an
error.** This is the single most important line in this section. Treating it as an error
produces one of two failure modes, both of which get reported as "the app is broken on first
load":

- An error boundary catches it and the user sees a crash screen instead of the login page.
- The global 401 handler (`GeneralUIArchitecture.md` §2.3 rule H) redirects to `/login`,
  which mounts, calls `/api/auth/me` again, 401s again, and redirects to `/login` — an
  infinite loop that pins the CPU.

So the bootstrap query is the **one exception** to the global 401 redirect. Exempt it
explicitly by URL, in one place, and comment why.

### 1.2 `SessionProvider` and its three states

**File:** `frontend/src/shared/auth/SessionProvider.tsx`

Three states, not two. Collapsing `loading` into `anonymous` is the second-most-common bug in
this file: for the duration of the first round trip every guard sees "no session" and
redirects an authenticated user to `/login`, who then bounces back once the query resolves.
The user sees a flash of the login form on every hard refresh.

| State | Condition | What the router renders |
|---|---|---|
| `loading` | the bootstrap query has not settled | A full-page loader. **No route decision is taken.** |
| `anonymous` | the query settled with a 401 | Public routes only; anything else redirects to `/login`. |
| `authenticated` | the query settled with a `SessionDto` | The shell. If `mustChangePassword` is true, see §3. |

```tsx
const { data, isPending, error } = useQuery({
  queryKey: ['identity', 'session'],
  queryFn: getSession,
  retry: false,          // a 401 is an answer, not a transient failure
  staleTime: Infinity,   // the session does not change without a mutation that seeds it
});
```

**A. `retry: false` on this query specifically.** The default retry policy
(`GeneralUIArchitecture.md` §3.4) already refuses to retry a 4xx, but state it here too:
retrying a 401 three times means three round trips before the login form appears, and the
user watches a spinner for no reason.

**B. `staleTime: Infinity`.** The session changes in exactly four places, all of them
mutations that can seed the cache directly: login, logout, change-password, and the 401
handler. Refetching it on window focus makes the app call `/api/auth/me` every time the user
alt-tabs.

**C. The provider renders nothing but a loader while `isPending`.** Not the shell with empty
navigation, not the login form. Both are visibly wrong for the half-second they are on
screen.

**D. Mutations that change the session write it into this cache key**, they do not invalidate
it (`GeneralUIArchitecture.md` §3.2 rule D). Login returns the full `SessionDto`; invalidating
would throw it away and immediately fetch the same object again.

### 1.3 Why the session is not cached in `localStorage`

It is tempting: read the last known session synchronously, render the shell immediately, and
reconcile in the background. Do not.

- **The cookie is the authority and the client cannot see it.** A cached `SessionDto` says
  nothing about whether the cookie is still valid. After eight hours away, the app renders a
  full authenticated shell for a user with no session, and every panel on the page fills with
  401 errors at once.
- **Role escalation looks like it works.** A `role` in `localStorage` is user-editable. It
  changes nothing on the server — every endpoint checks `IPermissionChecker` — but it does
  make the client draw buttons that 403, which is indistinguishable from a real bug in the
  permission table (`GeneralUIArchitecture.md` §6.2).
- **A demotion or suspension persists in the cache.** `set-role` and `demote` deliberately
  leave the target's existing session on the old role until it expires, so the server's
  answer is already up to eight hours stale. Caching it in the browser on top of that makes
  the staleness unbounded.

The cost of not caching is one round trip on cold load. That is the correct price.

---

## 2. Login

### 2.1 The form, the request, the response

**File:** `frontend/src/slices/identity/screens/LoginScreen.tsx`

Route `/login`, public, no shell. Two fields and a submit button.

| Field | Control | Zod |
|---|---|---|
| `email` | `TextField type="email"`, `autoComplete="username"` | `z.string().min(1).email()` |
| `password` | `TextField type="password"`, `autoComplete="current-password"` | `z.string().min(1)` |

`POST /api/auth/login` with `{ email, password }`. **Do not apply the password policy from
§3.4 to the login form.** The policy governs *choosing* a password; a user whose existing
password predates a rule must still be able to log in with it, and a client-side `min(12)`
here silently locks them out of their own account with a validation message that makes no
sense to them.

Success returns `SessionDto` and the `Set-Cookie` header. The DTO, from
`Slices/Identity/Application/Dtos/AuthDtos.cs`:

| Field | Wire type | Notes |
|---|---|---|
| `userId` | `string` | A Guid rendered as a string. Not a `Guid` type on the wire. |
| `displayName` | `string` | Never blank — `AcceptInvitationHandler` refuses to clear it. |
| `role` | **`number`** | See §8. `0` is `AccountantAdmin`. |
| `customerId` | `string \| null` | `null` for both Accountant roles, non-null for `CustomerAdmin` and `Employee`. |
| `mustChangePassword` | `boolean` | If true, see §3. Check this **before** routing anywhere. |

### 2.2 Post-login routing by role

`role` is the only thing that decides the landing route. There is no "last visited page"
memory and no user-configurable home.

| `role` | Value | Landing route | Why |
|---|---|---|---|
| `AccountantAdmin` | `0` | `/customers` | The customer list is the Office's main working surface. |
| `AccountantUser` | `1` | `/customers` | Same. The two Accountant roles differ in four powers, none of which is a different home screen. |
| `CustomerAdmin` | `2` | `/employees` | Their own Customer's people. |
| `Employee` | `3` | `/profile` | Honest placeholder: an Employee's real home is *my tickets*, and `Tickets` has no screens. See §2.6. |

**File:** `frontend/src/routes.tsx` — this table is also the body of the `/` route, which is a
role-dependent redirect rather than a page (`GeneralUIArchitecture.md` §4.2).

### 2.3 Return-to-intended-route

A user who is sent to `/login` by the 401 handler while trying to open `/audit/abc-123`
should land back on `/audit/abc-123`, not on `/customers`.

**A. Store the intended path in the router's location state, not in a query parameter.** A
`?returnTo=` parameter is an open redirect if it is ever allowed to hold an absolute URL, and
sanitising it correctly is more work than avoiding it.

**B. Only ever redirect to a path that starts with a single `/` and does not start with
`//`.** `//evil.example.com` is a protocol-relative URL and a browser treats it as a
different origin. Validate this even though the value came from your own router — it costs one
line.

**C. If the stored path is a route the new session's role may not see, fall back to the §2.2
landing route.** Otherwise a `CustomerAdmin` who was bounced off `/audit` logs in and is shown
the access-denied page as the first thing after a successful login.

**D. Clear the stored path once it has been used.** A path left in state redirects the next
login too, which surfaces weeks later as "logging in sends me to a random page".

### 2.4 Login errors are deliberately opaque, and must stay that way

`LoginHandler` returns **one 401 with one message — `"Invalid email or password."` — for six
distinct causes**: no such account, wrong password, account still `Invited`, account
`Suspended`, account locked out, and the owning Customer suspended. The handler's own comment
requires the response to be *"byte-for-byte identical for all of them"*, because any
distinction answers the question *does this address have an account here*.

The real reason is recorded in the audit log and only in the audit log. An `AccountantAdmin`
can read it at `/audit` (`AuditScreens.md`); the person at the keyboard cannot.

**Rules:**

**A. Render `error.title` verbatim.** Do not append a guess, do not add "your account may be
suspended — contact your accountant", do not vary the wording by attempt number. Every
embellishment is a channel that leaks which branch was taken.

**B. There is no separate lockout message and the client must not compute one.** Lockout is
**5 consecutive failures**, then **15 minutes** (`LoginHandler.MaximumFailedAttempts`,
`LockoutDuration`). The response for a locked-out attempt is the same 401 with the same
sentence. A client-side "3 attempts remaining" counter is wrong in both directions: the
counter is server-side and per-account, and the client does not know whether the account
exists, let alone its counter.

**C. A locked-out attempt does not increment the counter, and the client must not rate-limit
its own form.** The handler deliberately avoids extending a lockout on repeated attempts,
because that turns brute-force protection into a denial-of-service against the victim. A
client-side cooldown reintroduces exactly that, in the one place the backend went out of its
way to avoid it.

**D. A 422 from `/api/auth/login` is a malformed request, not a credential failure.** It
means the body failed model binding. Render it as a form-level banner
(`GeneralUIArchitecture.md` §7.1) — it indicates a bug in the client, not a user mistake.

**E. Rate limiting lives in Caddy** (`04-Infrastructure.md` §3, `route /api/auth/*`, 10
events per minute per host). Its response is **not** `ProblemDetails` and may be HTML or
empty with a `429`. The API client must survive parsing it (§0.3, and
`GeneralUIArchitecture.md` §2.3 rule G), and the login screen should render a distinct
"too many attempts, wait a minute" message for a `429` — that status comes from the proxy and
carries no account information, so it is safe to be specific about.

### 2.5 What the login screen must NOT do

| Do not | Because |
|---|---|
| Check whether an email exists before submitting | There is no endpoint for it, and building one would be the enumeration oracle the whole flow is designed to prevent. |
| Offer a role picker or an "I am a…" selector | The role comes from the account. A picker implies it is a choice and invites a support ticket when the "wrong" one fails. |
| Offer "remember me" | Expiry is 8 hours sliding, fixed in `IdentityRegistration.cs`. There is no persistent-cookie option to toggle, so the checkbox would do nothing. |
| Count attempts or disable the form after N failures | §2.4 rule C. |
| Show a password-strength meter | This is not where a password is chosen. |
| Pre-fill the email from `localStorage` | Not a security boundary, but it is the one field the browser's own autofill already handles correctly, and a stale value competing with autofill is worse than neither. |
| Link to a "register" or "sign up" page | There is none. Accounts are created only by invitation (`/api/accountants/invite`, `/api/employees/invite`) or by onboarding (`/api/customers/onboard`). |

### 2.6 The Employee landing route is a known compromise

There is no `Tickets` UI plan, so an `Employee` logging in has almost nothing to see:
their own record (`EmployeesScreens.md`), their own contact details, notifications, and the
Ticket Types their Customer may use. Sending them to `/profile` is honest but bleak.

Do not invent a dashboard to fill the space. An empty state that names what is coming is
better than a screen of fabricated widgets that have to be deleted when the `Tickets` UI
ships. Recorded in §10.

---

## 3. Forced password change — the gate that breaks every other screen if missed

### 3.1 What the middleware does

**File read:** `Shared/Auth/MustChangePasswordMiddleware.cs`, registered in `Program.cs:129`
immediately after `UseAuthentication()`.

An authenticated user whose `must_change_password` claim is `"true"` receives **403 on every
route** except exactly three:

```
/api/auth/change-password
/api/auth/logout
/api/auth/me
```

The 403 body is a `ProblemDetails` with a **stable, distinguishable `detail`**:

```
"You must change your password before continuing."
```

`detail` is the only field that distinguishes this 403 from a permission denial, and the
middleware's own comment says the front end is expected to match on it. This is the **one
response in the entire API that populates `detail`** (`GeneralUIArchitecture.md` §2.3 rule F).

> **Fragile by construction.** The client matching on an English sentence is a string
> comparison against a message that a well-meaning edit could reword. It is what the backend
> offers today. `BACKEND_CHANGES_REQUIRED.md` asks for a machine-readable extension —
> `"code": "must_change_password"` — and until that lands, the match must be a substring test
> against a single exported constant, never an inline literal repeated at three call sites.

### 3.2 The client handling

**File:** `frontend/src/shared/api/http.ts` — handled centrally, not per screen. There are
roughly forty endpoints; forty call sites cannot all remember.

```ts
export const MUST_CHANGE_PASSWORD_DETAIL = 'You must change your password before continuing.';

export function isMustChangePassword(error: unknown): boolean {
  return error instanceof ApiError
    && error.status === 403
    && (error.detail ?? '').includes(MUST_CHANGE_PASSWORD_DETAIL);
}
```

**A. Route on it, do not toast it.** It is a state the account is in, not a failure of the
action the user attempted (`GeneralUIArchitecture.md` §2.3 rule I). Navigate to
`/change-password`; show no error.

**B. Belt and braces: check `session.mustChangePassword` too.** The bootstrap query already
returns the flag, so the app can route to `/change-password` before making a single request
that would 403. The interceptor exists for the case where the flag was set by another session
mid-flight. Both paths must exist; either alone leaves a hole.

**C. `/change-password` renders outside the shell.** Drawing the navigation while every
navigable destination 403s produces a menu of dead links.

**D. Do not offer a "skip for now" or "remind me later".** There is nothing to skip to.
Logout is the only other permitted action, and the screen must show it — the middleware
explicitly allows `/logout` for exactly this reason, and omitting the button is the bug that
makes the gate feel broken, because the user's only escape is clearing cookies by hand.

### 3.3 The form

**File:** `frontend/src/slices/identity/screens/ChangePasswordScreen.tsx`

`POST /api/auth/change-password` with `{ currentPassword, newPassword }`. Returns
`{ success: true }` — **not** a `SessionDto`, so the session cache must be **invalidated**
here rather than seeded. The handler re-issues the cookie with the flag cleared, so the next
`/api/auth/me` returns `mustChangePassword: false`; skipping the invalidation leaves the stale
`true` in the client and the user is sent back to this screen forever, having already
succeeded.

`ChangePasswordRequestDto` has **no target user field**, deliberately, so this endpoint
cannot be pointed at another account even by mistake. `02-AuthorizationMatrix.md` §11:
resetting another person's password directly is permitted to **nobody**. There is no
administrative password reset to build, for any role.

**§3.4 The password policy, mirrored into Zod.** The first four rules are `PasswordPolicy.Validate`
(`Slices/Identity/Application/PasswordPolicy.cs:24`). The fifth is **not** — it lives in the handler,
at `ChangeOwnPasswordHandler.cs:92`, and that placement is not an accident: `PasswordPolicy` is given
a password and a login email and has no idea what the current password is, so only the
change-password path can enforce it.

| Rule | Value | Server status | Enforced in |
|---|---|---|---|
| Required | non-empty | 422 | `PasswordPolicy.Validate` |
| Minimum length | **12** | 422 | `PasswordPolicy.Validate` |
| Maximum length | **128** | 422 | `PasswordPolicy.Validate` |
| Must not equal the login email | case-insensitive, trimmed | 422 | `PasswordPolicy.Validate` |
| Must differ from the current password | exact, case-sensitive | 422 | `ChangeOwnPasswordHandler.cs:92` |

**The fifth rule therefore applies to change-password only, not to reset or invitation acceptance.**
Neither of those flows has a current password to compare against — the user is proving control of a
mailbox, not of an old secret. Mirroring the rule into the reset schema would reject a user who
legitimately reuses the password they could not remember well enough to sign in with, and there is no
server rule behind the rejection. Put it in the change-password schema and nowhere else.

**There are deliberately no composition rules** — no required uppercase, digit or symbol,
following NIST SP 800-63B. Do not add them to the Zod schema because they look more secure.
A client rule the server does not enforce rejects passwords the server would have accepted,
which is a validation bug that only the client can be blamed for.

Two ordering facts worth mirroring, because they change which message the user sees:

- The handler validates the **new** password *before* verifying the current one. A user who
  typed a 6-character new password is told that, rather than getting a 401 they will read as
  "I got my old password wrong".
- A wrong current password is **401, not 403** — it is a failed credential check, exactly like
  login. It does **not** increment the lockout counter and cannot lock the account, so do not
  warn the user that it might.

Client-side, validate the full policy including the email comparison (the session already
carries what is needed for the length and difference checks; the login email is **not** in
`SessionDto` — see §10). Every server 422 here can only ever be a form-level banner, because
`ProblemDetails` carries no field map (`GeneralUIArchitecture.md` §7.3).

### 3.5 Who arrives in this state

- **The seeded first Accountant Admin.** `Shared/Seeding/DatabaseSeeder.cs` sets
  `MustChangePassword = true`, because the seeded password came from an environment variable
  that is visible in `docker inspect`, in shell history, and in the compose file. This is the
  very first login anyone ever performs against a new deployment, so it is also the first
  thing this screen must handle correctly.
- **Nobody else, today.** `AcceptInvitationHandler` and `CompletePasswordResetHandler` both
  set the flag to `false` — the person chose the password themselves, so there is nothing to
  force a change of.

---

## 4. Password reset

Two screens, two endpoints, and one naming trap.

### 4.1 `/forgot-password`

**File:** `frontend/src/slices/identity/screens/ForgotPasswordScreen.tsx`

`POST /api/auth/request-password-reset` with `{ email }`. Public, no shell, reachable from a
link on `/login`.

**This endpoint returns 200 unconditionally.** `.Produces<MarkedResultDto>()` is the only
declaration on it, and the endpoint comment says so explicitly: no 404 and no 422 can be
returned, because an unknown address gets the same 200 as a known one. The handler does not
even validate the address format, on the grounds that a 422 for a malformed address and a 200
for a well-formed unknown one is the same oracle, just quieter.

**A. Show the neutral confirmation, always.** "If that address has an account, a reset link
is on its way." Not "check your inbox" (implies an account exists), not "we could not find
that address" (impossible — the server never says so).

**B. Do the format check client-side anyway.** `z.string().email()`, purely so a typo is
caught before the user waits for an email that will never arrive. This is not a security
control; the server ignores the format.

**C. Replace the form with the confirmation on success.** Leaving the form live invites
repeated submissions, and each one invalidates the previous token — a user who clicks twice
and then opens the first email gets "that link is invalid or has expired" for a link that was
valid one minute ago.

**D. The reset token lives 1 hour** (`TokenPurpose.PasswordResetLifetime`). State the window
in the confirmation copy; the email says it too.

### 4.2 `/reset-password?token=…`

**File:** `frontend/src/slices/identity/screens/ResetPasswordScreen.tsx`

Reads `token` from the query string, collects a new password, and calls
`POST /api/auth/complete-password-reset` with `{ token, newPassword }`.

**A. Every failure is one 400 with one message** — `"That link is invalid or has expired."` —
covering no such token, wrong purpose, already consumed, expired, and *account suspended
between the request and the click*. Render it verbatim. Do not try to distinguish expiry from
consumption; the server will not tell you, on purpose.

**B. A missing or empty `token` parameter is a 400 too.** Detect it client-side and render the
same message without a round trip, rather than submitting an empty token.

**C. Completing a reset does NOT sign the user in.** The handler's comment is explicit: a
leaked reset link must not grant a live session in one step. On success, redirect to `/login`
with a success message. Do not call `/api/auth/me` hoping for a session — there is none, and
the 401 will trip the global handler.

**D. The reset clears the lockout** as well as the password. Worth knowing when a user reports
"I reset my password and still cannot get in" — that symptom is not this flow.

**E. The token must never reach the SPA's history, a log, or an analytics call.** Read it
once, hold it in component state, and `replace` the URL to drop the query string. It is a
single-use credential in a query parameter, which is already the weakest link.

### 4.3 The SPA route and the API route have different names, on purpose

| Emailed link | SPA route | API endpoint |
|---|---|---|
| `{App:BaseUrl}/reset-password?token=…` | `/reset-password` | `POST /api/auth/complete-password-reset` |
| `{App:BaseUrl}/accept-invitation?token=…` | `/accept-invitation` | `POST /api/auth/accept-invitation` |

The link text is built by `Slices/Identity/Application/TokenLinks.cs`, which exists precisely
so the query-parameter name cannot drift between the email and the page that reads it — its
comment notes that a mismatch there produces *"invalid token" for every user, with a token
that is perfectly valid*.

**Do not "align" the reset pair.** Renaming the SPA route to `/complete-password-reset`
breaks every reset link already sitting in a mailbox, and those links are live for an hour.
Renaming the API endpoint breaks nothing in the emails but contradicts
`App/GeneralAppArchitecture.md` §8's `{domain}/{action}` shape. The two names are different
because one is a page a human lands on and the other is a verb the page performs. That is
correct, not sloppy.

Also note: `TokenLinks` prefixes both with `App:BaseUrl`. If that setting is wrong, every
emailed link points at the wrong host and **nothing in the UI can detect it** — the SPA never
sees the link. It is a deployment check, recorded in `BACKEND_CHANGES_REQUIRED.md`.

---

## 5. Invitation

### 5.1 `/accept-invitation?token=…`

**File:** `frontend/src/slices/identity/screens/AcceptInvitationScreen.tsx`

`POST /api/auth/accept-invitation` with `{ token, newPassword, displayName? }`.

Structurally the same as §4.2 — public, no shell, token from the query string, one opaque 400
for every failure (`"That invitation is invalid or has expired."`), no session on success,
redirect to `/login`. Three differences:

**A. The token lives 7 days**, not one hour (`TokenPurpose.InvitationLifetime`). Invitations
wait on a human; a reset answers something the person just did.

**B. `displayName` is optional and absent means "keep what the inviter typed".** An empty or
whitespace-only string is treated as absent, **not** as an instruction to blank the name.
Cap it at **200** characters (`AcceptInvitationHandler.DisplayNameMaximumLength`) — note that
this differs from the 255 used for most display names elsewhere in the API. Send the field
only when the user actually typed something; do not send `""`.

**C. The account must still be `Invited`.** A replayed link, or an account that was invited,
activated, suspended and reactivated, gets the same opaque 400. Do not offer a "resend" button
on this screen — there is no anonymous resend endpoint, and the person cannot log in to ask
for one. The correct path is for an Accountant Admin to invite them again.

Redeeming the token **is** the email confirmation. There is no separate confirm-your-email
step and adding one would ask the person to prove the same thing twice by the same means.

### 5.2 Who can be invited

Both Customer-side and Office-side invitations exist, and the earlier draft of this spec was
wrong about that.

| Endpoint | Creates | Caller |
|---|---|---|
| `POST /api/accountants/invite` | `AccountantAdmin` or `AccountantUser` only — the other two roles are a **422** | `AccountantAdmin` |
| `POST /api/employees/invite` | An existing Employee's login, as `Employee` or `CustomerAdmin` | Accountants and the owning Customer's Admins |
| `POST /api/customers/onboard` | A Customer, its first Employee, and that Employee's `CustomerAdmin` invitation, in one transaction | `AccountantAdmin` |

All three land the invitee on the same `/accept-invitation` page with the same token purpose,
so this screen is role-agnostic and must not try to guess who it is serving. It cannot: the
token is opaque and the caller is anonymous.

---

## 6. Logout

**File:** `frontend/src/slices/identity/queries.ts`

`POST /api/auth/logout`, no body, returns `{ success: true }`.

There is **no sessions table** — the cookie *is* the session — so `SignOutAsync` only queues a
`Set-Cookie` that clears it. Nothing here can fail halfway and leave a session alive on the
server.

**A. Clear the entire query cache on success**, not just the session key:
`queryClient.clear()`. Leaving customer lists, employee records and audit entries in memory
means the next user at the same browser sees the previous user's data flash on screen before
their own requests resolve. On a shared office machine that is a real disclosure.

**B. Navigate to `/login` after clearing**, and use `replace` so the back button does not
return to an authenticated route that will now 401.

**C. Logging out twice is a 200 both times.** It is idempotent. Do not guard the button
against a double click with an error.

**D. If the logout call itself fails, clear and redirect anyway.** The user asked to leave. A
failed logout that leaves them looking at an authenticated shell is worse than a cookie that
outlives its client state — and the cookie will be rejected on its own schedule regardless.

**E. Logout is permitted while `mustChangePassword` is set** (§3.1). The button must be
present on `/change-password`.

---

## 7. Session expiry mid-session

The 8-hour sliding window means expiry happens to an idle tab, not an active one. The user
comes back from lunch, clicks something, and gets a 401.

Handled once, in the API client (`GeneralUIArchitecture.md` §2.3 rule H):

**A. On any 401 that is not the bootstrap query: clear the cache, set the session state to
`anonymous`, and redirect to `/login`.** Store the current path per §2.3 first.

**B. Show one message, on the login screen, saying the session ended.** Not a toast on the
page they were leaving — that page is about to unmount and the toast goes with it.

**C. Never retry a 401.** The retry policy already excludes 4xx; the point here is that a 401
is not a transient network failure even though it can look like one when several queries fail
at once.

**D. Do not attempt to detect expiry in advance.** There is no way to: the cookie is
`HttpOnly` and the expiry slides on every request, so any client-side countdown is wrong the
moment the user does anything. A "your session will expire in 2 minutes" warning built on a
timer will fire during active use.

**E. Concurrent 401s must produce one redirect.** A dashboard with four panels loses all four
queries at once. Guard the redirect so it runs once per transition to `anonymous`, or the
router receives four navigations and the login screen mounts four times.

---

## 8. The role enum on the wire

**`role` is a number. `status` is a string.** In the same application, sometimes in adjacent
fields of the same response.

No `JsonStringEnumConverter` is registered, so every C# enum serialises as its integer value.
`AccountStatus` and `CustomerStatus` reach the client as strings only because they are
declared as strings in their DTOs. There is no rule to learn here — it has to be checked per
field.

**File:** `frontend/src/shared/format/enums.ts`

```ts
export const UserRole = {
  AccountantAdmin: 0,
  AccountantUser: 1,
  CustomerAdmin: 2,
  Employee: 3,
} as const;

export type UserRole = (typeof UserRole)[keyof typeof UserRole];

export const ROLE_LABELS: Record<UserRole, string> = {
  [UserRole.AccountantAdmin]: 'Accountant Admin',
  [UserRole.AccountantUser]: 'Accountant User',
  [UserRole.CustomerAdmin]: 'Customer Admin',
  [UserRole.Employee]: 'Employee',
};
```

**A. `AccountantAdmin` is `0`, which is falsy.** `if (session.role)` is `false` for the most
privileged role in the system. So is `role || defaultRole`, and so is
`role ? label : 'unknown'`. Every check must be `===` against a named constant. This one trap
will cost somebody an afternoon; it is the reason the constants above are mandatory rather
than suggested.

**B. Never send a role as a string.** `InviteAccountantRequestDto.Role` and
`SetEmployeeRoleRequestDto` both bind an enum, and `"AccountantUser"` in the JSON is a **400**
from model binding — before any handler runs, so with no useful message.

**C. Never render the raw number.** `ROLE_LABELS` is the only source of user-facing role text,
and the labels must match `00-Glossary.md`. In particular the bare word "Admin" is ambiguous
between `AccountantAdmin` and `CustomerAdmin` and is banned in UI copy.

**D. The labels are not the C# names.** `AccountantAdmin` displays as "Accountant Admin".
Do not `String(role)` anything.

Fixing this asymmetry server-side is a **breaking change** that the client must land in the
same deploy. Recorded in `BACKEND_CHANGES_REQUIRED.md`.

---

## 9. Behavioural cases

Every one of these is reachable from the login screen with a keyboard, and every one has
been got wrong at least once in an application of this shape.

- [ ] Cold load, no cookie: `/api/auth/me` 401s, the login form renders, and **no** redirect
      loop occurs.
- [ ] Cold load, valid cookie, hard refresh on `/audit/abc-123`: the loader shows, then the
      audit detail page — **no** flash of the login form.
- [ ] Correct credentials: the shell renders, the nav matches the role, and the landing route
      matches §2.2.
- [ ] Wrong password: one 401, the message `"Invalid email or password."`, the form stays
      filled except for the password, focus returns to the password field.
- [ ] Unknown email: byte-for-byte the same response and the same rendering as wrong password.
- [ ] Six consecutive wrong passwords: still the same message. No "you are locked out", no
      client-side cooldown, and the form remains submittable.
- [ ] Eleven login attempts inside a minute (Caddy's limit): a `429` with a possibly non-JSON
      body does not crash the client, and shows a distinct "too many attempts" message.
- [ ] First login as the seeded Accountant Admin: `mustChangePassword` is `true`, the app
      lands on `/change-password` **without** the shell, and a Logout button is present.
- [ ] While in that state, navigating to `/customers` by URL: the 403's `detail` is matched
      and the app returns to `/change-password`, showing no error toast.
- [ ] Change password with a 6-character new password: 422, form-level banner, and the message
      is about the new password — not about the current one.
- [ ] Change password with the wrong current password: 401, rendered as a form banner, and the
      user is **not** logged out by the global 401 handler.
- [ ] Change password successfully: the session cache is invalidated, `mustChangePassword`
      becomes `false`, the shell appears, and the user is not asked again.
- [ ] Forgot password with an unknown address: 200, neutral confirmation, form replaced.
- [ ] Forgot password twice, then open the first email: 400 with the opaque message; nothing
      in the UI claims the link was already used.
- [ ] `/reset-password` with no `token` parameter: the opaque message, no request sent.
- [ ] Reset completes: redirect to `/login`, no session, and the token is gone from the URL
      and from history.
- [ ] Accept an invitation with a blank display name: succeeds, and the name set by the
      inviter is preserved.
- [ ] Accept the same invitation twice: the second is a 400 with the same message.
- [ ] Logout: cache cleared, `/login` via `replace`, and the back button does not restore an
      authenticated screen.
- [ ] Logout twice: 200 both times, no error.
- [ ] A dashboard with four queries when the cookie is deleted from devtools: exactly one
      redirect to `/login`, not four.
- [ ] `session.role === 0` renders "Accountant Admin" everywhere, and no code path treats it
      as missing.

---

## 10. Questions to flag if unclear

Flag these; do not answer them by inventing a behaviour (`README.md` §*Conflict precedence*).

- [ ] **The `mustChangePassword` 403 is matched on an English sentence.** Should the middleware
      add a machine-readable `code` extension to `ProblemDetails` so the client stops matching
      on prose? The client fix is one line either way; the question is whether the contract
      changes.
- [ ] **`SessionDto` does not include the login email.** The change-password form must check
      "new password must not equal the login email" client-side to avoid a pointless round
      trip, and it cannot. Add `loginEmail` to `SessionDto`, or accept the server 422?
- [ ] **No endpoint changes your own display name or your own login email.** `/api/auth/me` is
      read-only and `ChangePasswordRequestDto` is the only self-service write. Is that
      intended for v1, or is a self-profile edit missing? See `IdentityScreens.md` §7.
      Still true as of 2026-09-02: `POST /api/employees/change-login-email` changes **somebody
      else's** — an Accountant acting on a Customer-side person — and is deliberately not
      self-service. Nothing here changes.
- [ ] **The Employee landing route is a placeholder** (§2.6). Where should an Employee land
      before the `Tickets` UI ships?
- [ ] **The dev vite proxy target is ambiguous.** `04-Infrastructure.md` §2 says `5000`;
      `Properties/launchSettings.json` binds `5131`. `GeneralUIArchitecture.md` §11.1 specifies
      `5131` to match the running code. Which one is authoritative?
- [ ] **`App:BaseUrl` has no verification path.** A wrong value produces emails with links to
      nowhere and nothing in the SPA can detect it. Should the app expose it on a health
      endpoint so a deployment check can assert it?
- [ ] **Is a session-expiry warning wanted at all?** §7 rule D argues it cannot be built
      correctly against a sliding `HttpOnly` cookie. Confirm that no warning is expected.
- [ ] **Should `/api/auth/me` be exempted from rate limiting?** It sits under
      `/api/auth/*`, which Caddy limits to 10 events per minute. A user who reloads
      repeatedly, or several tabs bootstrapping at once, can plausibly hit that — and the
      failure mode is the whole app refusing to start.

---

## Files checklist

- [ ] `frontend/src/shared/auth/SessionProvider.tsx`
- [ ] `frontend/src/shared/auth/useSession.ts`
- [ ] `frontend/src/shared/auth/RequireSession.tsx`
- [ ] `frontend/src/shared/auth/RequireRole.tsx`
- [ ] `frontend/src/shared/format/enums.ts` — `UserRole`, `ROLE_LABELS`
- [ ] `frontend/src/slices/identity/types.ts` — `SessionDto` and the five auth request DTOs
- [ ] `frontend/src/slices/identity/api.ts` — the seven `/api/auth/*` calls
- [ ] `frontend/src/slices/identity/queries.ts` — session query, login/logout/change-password mutations
- [ ] `frontend/src/slices/identity/screens/LoginScreen.tsx`
- [ ] `frontend/src/slices/identity/screens/ChangePasswordScreen.tsx`
- [ ] `frontend/src/slices/identity/screens/ForgotPasswordScreen.tsx`
- [ ] `frontend/src/slices/identity/screens/ResetPasswordScreen.tsx`
- [ ] `frontend/src/slices/identity/screens/AcceptInvitationScreen.tsx`
- [ ] `frontend/src/shared/api/http.ts` — the 401 and `mustChangePassword` 403 interceptors

---

## Success criteria

Each is verified by running the app, not by reading the code.

1. A visitor with no cookie sees the login form within one round trip, and the browser console
   is empty of unhandled errors.
2. A hard refresh on any authenticated deep link returns to that link, with no visible flash
   of the login form.
3. All six login failure causes are indistinguishable in the network tab: same status, same
   body, same rendering.
4. The seeded Accountant Admin's first login lands on `/change-password`, cannot navigate
   anywhere else, and can log out.
5. Changing that password grants immediate access with no second prompt and no reload.
6. `/forgot-password` behaves identically for a known and an unknown address, confirmed by
   comparing both responses byte for byte.
7. A reset link works once, and a second use of the same link is refused with the same message
   as an expired one.
8. An invitation link sets a password and a display name, and lands the user on `/login` with
   no session.
9. Deleting the cookie in devtools and clicking anything produces exactly one redirect to
   `/login` and one message about the session ending.
10. Logging out and logging in as a different user shows none of the first user's data at any
    point, including for a single frame.
11. Every role label rendered anywhere in the app comes from `ROLE_LABELS`; searching the built
    bundle for the string `"AccountantAdmin"` finds only the constant definition.
12. No request in the network tab carries an `Authorization` header, and `localStorage` and
    `sessionStorage` are empty after a successful login.
