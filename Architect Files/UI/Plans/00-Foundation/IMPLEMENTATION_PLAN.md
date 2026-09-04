# Foundation — UI Implementation Plan

This is **Phase 0** of the front end. No other UI plan can start until it is finished: every slice plan assumes `http.ts` exists, assumes a session is in context, assumes `PaginatedTable` owns the 1-based/0-based conversion, and assumes `can()` answers questions about the caller's role. This plan builds exactly that surface and stops.

It is a **build plan, not a second copy of the specification.** It says which file to create, in what order, what belongs in it, what must not be in it yet, and how a future builder proves the step worked. Facts are **cited by section** — `GeneralUIArchitecture.md` §2.3 rule B — not restated, because a restatement is a second thing to keep in sync. Open the cited section; do not implement from this plan's summary of it.

**Nothing in this front end has ever been run.** There is no `frontend/` directory, no Dockerfile, and no local PostgreSQL on the authoring machine. Every "verify" below is an instruction to a future builder who has a database. No step has been observed to work.

**Where this plan and a governing document disagree, the document wins.** Record such a disagreement; never resolve it by inventing a third behaviour (`UI/README.md` §*Conflict precedence*).

## Documents that govern this document, in precedence order

| # | Document | Sections this plan leans on |
|---|---|---|
| 1 | `../../../04-Infrastructure.md` | §1 the three hosting lines; §2 the dev table and vite proxy; §3 the Dockerfile's `frontend/` path |
| 2 | `../../../02-AuthorizationMatrix.md` | §4 the two employee grants; :146 hide-vs-disable; :296 never rely on the client to hide data; §11 no administrative password reset |
| 3 | `../../../00-Glossary.md` | Role labels; the banned word "Client" |
| 4 | `../../App/GeneralAppArchitecture.md` | §8 ids in the body, `{domain}/{action}` routes |
| 5 | `../../GeneralUIArchitecture.md` | §1.2 tree; §1.4 imports; §1.5 dependencies; §2.1–2.4 client; §3.2–3.4 queries; §4.1–4.5 routing; §5 shell; §6.1–6.2 `can()`; §7 errors; §8 MUI; §9 forms; §10 wire; §11.1 vite |
| 6 | `../../LoginArchitecture.md` | §0.1 cookie; §1.1–1.3 bootstrap; §2 login; §3 forced change; §4 reset; §5 invitation; §6 logout; §7 expiry; §8 roles |
| 7 | `../../Screens/*.md` | Not used here. Each belongs to its own slice plan |
| 8 | This plan | Loses to every row above |

`../../BACKEND_CHANGES_REQUIRED.md` is deliberately **not** in that list. It is a list of requests and overrides nothing. It is cited only to name a known defect a builder would otherwise mistake for their own.

---

## 0. Prerequisites and build position

### 0.1 What exists on the server

Full inventory: `GeneralUIArchitecture.md` §0.1. Three facts shape this phase.

1. **`Identity` is built and wired.** All thirteen routes — **seven** under `/api/auth/*` and **six** under `/api/accountants/*` — are registered (`Slices/Identity/IdentityEndpoints.cs:28-160`), so the auth screens have a real backend. §8.1 and §8.2 below list them individually, and `Plans/Identity/IMPLEMENTATION_PLAN.md` §0.2 counts them the same way. If you were given a count of eight, it is short by five.
2. **The API cannot serve the SPA** (§0.4 item 1). `npm run dev` is the only way to load the app, in this phase and every later one.
3. **`Tickets` and `Documents` have no UI plan, no screen document and no client route** — so an `Employee` has no home screen. Both backend slices are built, registered (`Program.cs:65` and `:59`) and routed: `Program.cs:157` maps two groups, `/api/tickets/*` and `/api/documents/*`. The gap is entirely on this side of the wire, and there is no `UI/Screens/TicketsScreens.md` to transcribe. A known constraint (§12 items 1–2), not a gap to fill with invented widgets.

### 0.2 What must be running before step 1

```
docker compose up -d db                       # Postgres 16. No compose file exists yet -- 0.4 item 2.
dotnet run --project AccountantApp.Api        # binds http://localhost:5131
cd frontend && npm run dev                    # http://localhost:5173  <- open THIS one
```

Four things stop the API before it serves a request. None is a UI fault; all four read as one.

- **`ConnectionStrings:Default`** is blank in `appsettings.json`; development sets `Host=localhost;Port=5432;Database=accountant_app;Username=postgres;Password=postgres`.
- **`DataProtection:KeyPath`** must be non-blank **and writable** or startup fails — the comment in `appsettings.json` says so. Development sets `.dataprotection-keys`.
- **`Seeding:FirstAdminEmail` and `Seeding:FirstAdminPassword`** must **both** be set or `Shared/Seeding/DatabaseSeeder.cs:58-60` throws. Development uses `admin@accountantapp.local` / `development-admin-password`.
- **Use `localhost`, never a LAN IP or a `.local` host.** The cookie is `Secure` (`LoginArchitecture.md` §0.1); browsers trust `http://localhost` and nothing else, and the cookie is dropped silently — login appears to succeed, then the next call 401s.

There is no `X-Dev-Role` shortcut: `DevAuthHandler` has been deleted (§11.2). Every request needs a real login, which is why the auth screens are in Phase 0 rather than in the `Identity` slice plan.

### 0.3 The nine steps, and why the order is load-bearing

| Step | Builds | Needs | Because |
|---|---|---|---|
| 1 | `frontend/` scaffolding | — | Nothing compiles |
| 2 | `shared/api/` | 1 | No tsconfig, no proxy, so no request can be made at all |
| 3 | `shared/format/`, `shared/hooks/` | 2 | `usePaginatedQuery` wraps a call that must exist |
| 4 | `shared/auth/` | 2, 3 | `SessionProvider` calls `get` and types `role` against `UserRole` |
| 5 | `shared/permissions/` | 3 | `can()` takes the `UserRole` from `format/enums.ts` |
| 6 | `theme.ts`, `shared/components/` | 2, 4, 5 | `AppShell` reads the session; `ErrorBanner` narrows an `ApiError` |
| 7 | `routes.tsx`, `App.tsx`, `main.tsx` | 2, 4, 5, 6 | The table wraps screens in the guards and renders the shell |
| 8 | The auth screens | 7 | A screen with no route cannot be opened, so it cannot be verified |
| 9 | Phase exit gate | 1–8 | — |

Do not reorder, and do not start all the files at once. Each dependency is a specific failure.

**A. `http.ts` before `SessionProvider`.** Written first, the provider has nothing to call, so it grows its own `fetch` — and that is the call made on **every** page load. It then owns its own `credentials` value, error shape and 401 behaviour. Symptom: everything works except that `credentials` was `'omit'` by accident, the cookie is never sent, `/api/auth/me` 401s forever, and the login form is the only screen that renders.

**B. `SessionProvider` before `RequireSession`.** The guard switches on **three** session states (`LoginArchitecture.md` §1.2). Written without the provider it has two, because `loading` is the one nobody thinks of. Symptom: a flash of the login form on every hard refresh — invisible on a fast local machine, first seen on somebody else's laptop months later.

**C. `RequireSession` before `routes.tsx`.** The guard wraps the shell **once**, in the route table (§4.3 rule C). Table first means the routes have no guard to sit in, so each screen checks for a session itself — and every screen can then be mounted without one.

**D. `format/enums.ts` before anything touching a role.** `UserRole.AccountantAdmin` is `0` and `0` is falsy (`LoginArchitecture.md` §8 rule A). `if (session.role)` written before the constants exist hides the most privileged role in the system and reads as a permission bug for an afternoon.

**E. `can()` before `AppShell` and every screen.** The nav derives from §5.2's table, **not** from `can()` — but every later *button* is gated by it, and a table that is absent when the first button is written means the button ships ungated and is copied nine times.

### 0.4 The four punch-list items that touch this phase

Each was checked against the working tree. Do **not** edit `BACKEND_CHANGES_REQUIRED.md`; report.

> **Item 1 — the three hosting lines are still missing. This blocks deployment, not development.** `AccountantApp.Api/Program.cs` is 159 lines and contains no `UseDefaultFiles()`, no `UseStaticFiles()` and no `MapFallbackToFile("index.html")`; verified by reading it in full. **Blocked:** the production image, `GET /` serving `index.html`, and every deep link not loaded through the dev server. **Not blocked:** anything in this plan — vite serves `index.html` for unknown paths itself, so the SPA's catch-all route is testable today. Two consequences: `curl -i localhost:5131/` returns a `ProblemDetails` `404` and that is currently *correct*; and `http.ts`'s `startsWith('/api/')` guard defends against a failure — a non-`/api` path returning `index.html` with a `200` — that cannot occur until item 1 lands. Write the guard anyway.

> **Item 2 — no `Dockerfile`, `docker-compose.yml` or `Caddyfile`.** Out of scope: this plan creates and edits no infrastructure file. Named because it is a **prerequisite of the first deployment** alongside item 1, and because two things a builder reaches for do not exist. `docker compose up -d db` has no compose file, so PostgreSQL must be started another way. And the `429` handling this plan requires comes from Caddy's rate limiter (`04-Infrastructure.md` §3, `route /api/auth/*`, 10 per minute), which is not running locally — so a `429` **cannot be produced in development** and its handling is verifiable only by inspection until item 2 lands.

> **Item 3 is "`frontend/` does not exist", which step 1 fixes — but the claim it is sometimes remembered for is stale.** The earlier draft's claim that **no Customer-side user could be created** is retracted in that document's own "Not on this list, and why" (:974-988), and the retraction is correct: `app.MapPost("/api/customers/onboard", …)` is live at `Slices/Employees/EmployeesEndpoints.cs:227`, routed from `:14` and `:225`, registered by `Program.cs:153` whose comment reads *"Registers /api/employees/* AND /api/customers/onboard"*; handler at `Slices/Employees/Application/Handlers/OnboardCustomerHandler.cs:20`. Every role can be created and logged in as today, so step 8's invitation screen has three real token producers (`LoginArchitecture.md` §5.2). The stale claim survives because `Slices/Employees/` is **untracked in git** — a survey of committed files does not see it. Check the working tree.

> **Item 8 — the dev port is ambiguous, and this plan resolves it to 5131.** `AccountantApp.Api/Properties/launchSettings.json` binds `http://localhost:5131` on the `http` profile and `https://localhost:7152;http://localhost:5131` on the `https` profile. `04-Infrastructure.md` §2 shows `proxy: { '/api': 'http://localhost:5000' }`. `GeneralUIArchitecture.md` §11.1 specifies **5131** and carries the same note. Step 1 targets 5131. This is the one place where shipped code beats a numbered document, and the reason is mechanical rather than editorial: a proxy pointed at a dead port fails every call. Whichever side is eventually changed, both must end up naming one port.

One more, not on the punch-list, that will cost a builder a morning:

> **`App:BaseUrl` in `appsettings.Development.json` is `https://localhost:5173`, and the dev server serves plain `http` on 5173.** `Slices/Identity/Application/TokenLinks.cs` prefixes every invitation and reset link with it, so a dev link points at a scheme nothing listens on. And `Notifications:Email:Enabled` is `false`, so `Slices/Notifications/NotificationsRegistration.cs:56` never registers the drainer and no email is delivered locally. §8.9 has the consequence — it decides how the reset and invitation screens can be verified at all.

### 0.5 Rules that hold in every step

Not new. These are the invariants this phase installs in one place.

**A. Never rely on the React app to hide data.** `can()` gates affordances only (`02-AuthorizationMatrix.md`:311, §6.2 rule A). A UI filtering rows or fields for security is concealing a live server bug.

**B. Out-of-scope rows return `404`, not `403`.** Never render "forbidden", "denied" or "no permission" for a `404` (§2.3 rule J). "Not found" is honest in both cases.

**C. No token in `localStorage`, `sessionStorage`, or a variable.** The session is the `aa_session` HttpOnly cookie and the client cannot read it (`LoginArchitecture.md` §0.1–0.2, §1.3). There is no `Authorization` header anywhere in this application.

**D. `credentials: 'same-origin'`.** Never `'omit'`, which drops the cookie; never `'include'`, which declares a cross-origin request in an app with no CORS configuration (§2.3 rule B).

**E. No API base-URL environment variable, ever.** Every path is relative and starts `/api/`. No `VITE_API_URL`, no `import.meta.env` lookup that could become one (§2.3 rule A).

**F. CORS is never configured, in any environment.** One origin in development because of the proxy; one in production because the SPA ships inside `wwwroot`.

**G. A raw invitation or reset token must never reach browser history, a log, or an analytics call.** Read it once, hold it in component state, `replace` the URL (`LoginArchitecture.md` §4.2 rule E).

**H. Invent no dependency and no shared component.** The kernel is §1.2 and the dependency list is §1.5. Anything missing goes in §9.2 as a question, never into the code as fact.

---

## 1. Scaffolding `frontend/`

Five files at the root of `frontend/`. No `src/` file is created in this step.

### 1.1 `package.json`

**File:** `frontend/package.json`

The locked dependency list from `GeneralUIArchitecture.md` §1.5 **and nothing else**. Runtime, exactly these thirteen: `react`, `react-dom`, `@mui/material`, `@emotion/react`, `@emotion/styled`, `@mui/icons-material`, `@mui/x-date-pickers`, `date-fns`, `@tanstack/react-query`, `react-router-dom`, `react-hook-form`, `zod`, `@hookform/resolvers`. Dev: `vite`, `@vitejs/plugin-react`, `typescript`, `@types/react`, `@types/react-dom`. `@vitejs/plugin-react` is not an addition — it is imported by the `vite.config.ts` §11.1 prescribes verbatim; `typescript` and the two `@types` are what §1.5's "TypeScript, strict" requires to exist.

**Do not hand-write version numbers.** Create the file with `name`, `private: true`, `type: "module"` and the scripts, then `npm install <names>` and `npm install -D <names>` and let npm write the ranges. A version a builder typed is a version nobody verified.

| Script | Command | Why |
|---|---|---|
| `dev` | `vite` | The only way to load the app this phase (§0.4 item 1) |
| `build` | `tsc --noEmit && vite build` | The Dockerfile runs `npm run build` and expects `dist`. Type-check first, so an error fails the build instead of shipping |
| `preview` | `vite preview` | Confirms `dist` is loadable |

No test, lint, format or CI script. None is specified anywhere in `UI/`; adding one is a dependency decision (§0.5 rule H).

### 1.2 `tsconfig.json`

**File:** `frontend/tsconfig.json`

| Option | Value | Consequence if wrong |
|---|---|---|
| `strict` | `true` | Without it `customerId` is `string`, not `string \| null` — and it **is** `null` for both Accountant roles |
| `noUncheckedIndexedAccess` | `true` | `ACTIONS[action]` types as defined, and §6.1's `?? false` looks like dead code somebody deletes |
| `jsx` | `react-jsx` | No `import React` |
| `moduleResolution` | `bundler` | Matches vite |
| `target` / `lib` | `ES2022`, DOM + DOM.Iterable | `URLSearchParams`, `Intl.NumberFormat` |
| `noEmit` | `true` | vite emits; `tsc` only checks |

**No path aliases.** Every `**File:**` path in every screen spec is relative; an alias makes those paths stop matching the imports.

### 1.3 `vite.config.ts`

**File:** `frontend/vite.config.ts`

Copy the block in `GeneralUIArchitecture.md` §11.1 **exactly, including its comments**: `plugins: [react()]`, `server.proxy` = `{ '/api': 'http://localhost:5131' }`, `build.outDir` = `'dist'`. **The port is 5131, not the 5000 in `04-Infrastructure.md` §2** — see §0.4 item 8 for the verification and the punch-list reference.

The proxy makes the browser see **one origin** in development. Not convenience: the cookie is `SameSite=Strict`, so a page on 5173 calling 5131 directly would not send it. Removing the proxy breaks authentication and would need CORS, which rule F forbids. Do not add `server.port` — 5173 is vite's default and both §11.2 and `App:BaseUrl` name it.

### 1.4 `index.html`

**File:** `frontend/index.html`

`<html lang="en">`, a `<title>`, `<div id="root"></div>`, `<script type="module" src="/src/main.tsx"></script>`. Nothing else — no CDN font or stylesheet, no analytics snippet, no inline script reading configuration. A CDN reference is a second origin in an app whose whole security model is one origin, and an analytics snippet on a page that can hold a reset token in its URL breaks rule G before any application code exists.

### 1.5 `.gitignore`

**File:** `frontend/.gitignore`

`node_modules/`, `dist/`, `*.local`, `.vite/`. §1.2's tree does not list this file; it is the only addition in this plan. §1.2's stated reason for exactness is that a screen spec's `**File:**` paths must resolve, and a `.gitignore` resolves nothing and breaks nothing. Committing `node_modules/` does break things.

### 1.6 How this step is verified

1. `npm install` completes and writes a lockfile.
2. `npm run dev` prints a URL on 5173 and that URL returns the `index.html` shell.
3. `npx tsc --noEmit` exits zero against an empty `src/`.
4. With the API up, `curl -i localhost:5173/api/auth/me` returns the API's `401` `ProblemDetails` **through the proxy** — not a vite 404. The most valuable check in step 1: if it fails, nothing after it can work.

### 1.7 Five ways this step goes wrong

1. **The proxy targets 5000**, copied from `04-Infrastructure.md` §2 without reading the drift note. Every call fails at the network layer, so there is no response body to read — it looks like the API is down, not like a config error.
2. **`strict` off, or relaxed at the first `null` error.** The nullable fields here are exactly the meaningful ones: `customerId` is `null` for Accountants, `detail` for all but one response.
3. **Invented pinned versions.** A range npm chose is verified; a number a builder typed is not.
4. **Extra dependencies** — another date library, an HTTP client, a second icon set. All banned by §1.5; the `fetch`-only rule and the `@mui/x-data-grid` ban are absolute.
5. **`frontend/` in the wrong place.** The Dockerfile hard-codes `COPY frontend/package*.json`. A nested location works in development and fails only in the image nobody has built.

### What this step does NOT do, and why

- **No `src/` file, not even a placeholder `main.tsx`.** A placeholder gets committed, then edited, then becomes the real `main.tsx` without ever having been written against §7.
- **No test harness.** No framework is specified in `UI/`; adding one is rule H.
- **No `.env` of any kind.** Rule E — there is nothing for it to hold.
- **No `Program.cs` or Dockerfile change.** Items 1 and 2 are named and left alone. This plan writes no C#.

---

## 2. `src/shared/api/` — the API client

Five files, in dependency order. The most consequential step in the phase: every later file calls it, so one mistake here is forty bugs that look unrelated.

### 2.1 `ApiError.ts`

**File:** `frontend/src/shared/api/ApiError.ts`

Copy the class in §2.2 exactly, comments included: `status`, `title`, `detail`, `traceId`, `super(title)`, `name = 'ApiError'`, and the `isPasswordChangeRequired` getter.

The whole error body is `{ status, title, traceId }`. No `errors{}` dictionary, no machine-readable code, and `detail` is populated by **exactly one** response in the API — the must-change-password `403` (`Shared/Auth/MustChangePasswordMiddleware.cs`; §2.3 rule F). Do not add an `errors` field "for later"; a field that is always `undefined` is one somebody branches on.

### 2.2 `problemDetails.ts`

**File:** `frontend/src/shared/api/problemDetails.ts`

Copy `parseProblemDetails` and the seven-entry `fallbackTitle` map from §2.2 exactly, including the `try`/`catch` around `response.json()` and the empty `catch` with its comment. That `catch` is not padding: Caddy answers a rate-limited request itself and its body is not JSON; a proxy error is HTML. An unguarded parse turns `429` into an unhandled `SyntaxError`, and it surfaces on the **login form**.

> **The content type is `application/json`, not `application/problem+json`.** The API serialises with `WriteAsJsonAsync` (`Shared/Errors/AppExceptionMiddleware.cs`), which does not set the RFC7807 media type. Never branch on `Content-Type` to decide whether a body is a problem document — branch on `response.ok`. Punch-list item 14.

### 2.3 `http.ts`

**File:** `frontend/src/shared/api/http.ts`

The **only** module permitted to call `fetch`. A `fetch` under `slices/` is a defect regardless of whether it works. Copy `request`/`get`/`post` from §2.1 exactly, then add these two interceptors and nothing else.

`LoginArchitecture.md` §3.2, verbatim — the sentence is verified against `MustChangePasswordMiddleware.cs`, which sets `Detail = "You must change your password before continuing."`:

```ts
export const MUST_CHANGE_PASSWORD_DETAIL = 'You must change your password before continuing.';

export function isMustChangePassword(error: unknown): boolean {
  return error instanceof ApiError
    && error.status === 403
    && (error.detail ?? '').includes(MUST_CHANGE_PASSWORD_DETAIL);
}
```

It must appear **once**, as this constant. §3.1 marks the whole mechanism fragile by construction and asks for a machine-readable `code` (punch-list item 5); one constant is what makes that a one-line change.

`http.ts` cannot navigate — it is not a component, and it must not import the router or touch `window.location`, because a hard navigation discards the router state `LoginArchitecture.md` §2.3 needs for return-to-intended-route. So it exposes handler slots:

```
# module scope
SESSION_BOOTSTRAP_PATH = '/api/auth/me'
let onUnauthorized, onPasswordChangeRequired          # null until registered
export registerUnauthorizedHandler(fn)               # called once, by SessionProvider
export registerPasswordChangeRequiredHandler(fn)     # called once, by SessionProvider

# inside request(), after parseProblemDetails produced `error`
if error.status == 401 and path != SESSION_BOOTSTRAP_PATH: onUnauthorized?.()
if isMustChangePassword(error):                          onPasswordChangeRequired?.()
throw error                                          # ALWAYS -- the caller's channel is unchanged
```

**A. The bootstrap exemption is by path, in one place, with a comment.** A `401` from `/api/auth/me` is the **normal answer for an anonymous visitor**, not an error (`LoginArchitecture.md` §1.1). Without the exemption the handler redirects to `/login`, which mounts, calls `/api/auth/me`, 401s, redirects — an infinite loop that pins the CPU.

**B. A callback, not a navigation.** `SessionProvider` supplies it; its job is to move the session to `anonymous`, and the *redirect* is rendered declaratively by `RequireSession`. That is what makes `LoginArchitecture.md` §7 rule E true for free: four panels losing their queries at once produce one state transition and one navigation, not four.

**C. The error is still thrown.** §2.3 rule E is unchanged: a non-2xx throws, callers never read `response.ok`, and TanStack Query's `isError` is the single error channel.

**D. `isMustChangePassword` routes; it never toasts.** It is a state the account is in, not a failed action (§2.3 rule I). The handler invalidates the session query; `RequireSession` navigates.

### 2.4 `paginated.ts`

**File:** `frontend/src/shared/api/paginated.ts`

Copy `PaginatedResponse<T>`, `DEFAULT_PAGE_SIZE = 15` and `MAX_PAGE_SIZE = 50` from §3.3. Both are verified against `Shared/Pagination/PaginatedQuery.cs:7-12`, which also shows `Math.Max(pageNumber, 1)` and `Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize)`.

The envelope `{ pageNumber, pageSize, totalCount, totalPages, items }` is **identical for every list in the API**; there is no per-slice variant to add later. No screen imports `MAX_PAGE_SIZE` to validate input, because the server clamps rather than rejects (punch-list item 17) — the constant exists so a page-size selector never *offers* more than 50.

### 2.5 `queryClient.ts`

**File:** `frontend/src/shared/api/queryClient.ts`

Copy the construction from §3.4 exactly: the predicate `error instanceof ApiError && error.status >= 500 && failureCount < 2`, `staleTime: 30_000`, `refetchOnWindowFocus: false`, `mutations: { retry: false }`.

Not a style choice. Retrying a `403` asks the server to deny you three times and `PermissionChecker` writes an **audit row per denial** — three false `PermissionDenied` entries for one click. And no endpoint is idempotent and there is no idempotency key, so a retried `POST /api/employees/register` creates a second Employee.

> **`GeneralUIArchitecture.md` cites this file as "§3.5" twice** — §1.2's tree comment and §3.2 rule F — but the retry policy is **§3.4** and no §3.5 exists. A cross-reference defect in the governing document, not a second file to create.

### 2.6 How this step is verified

The five checks in §11.3, with the API and dev server up:

1. `curl -i localhost:5131/api/auth/me` → `401` with a **JSON** body, not a `302`. The backend overrides `OnRedirectToLogin` (`Slices/Identity/IdentityRegistration.cs:93`) so this is data.
2. `curl -i localhost:5131/api/nonexistent` → `404` `ProblemDetails`, not `index.html`.
3. `curl -i localhost:5131/` → `404` today. This **confirms** item 1 is open.
4. `POST /api/auth/login` with the seeded admin → `200`, `Set-Cookie: aa_session`, and in the body `"role"` is a **number** and `"mustChangePassword"` is `true`.
5. `GET /api/ticket-types/list?pageNumber=1&pageSize=999` → `200` with `"pageSize": 50`.

If any behaves differently, **stop and flag it** — a governing document is wrong and needs correcting, not working around (§11.3).

### 2.7 Six ways this step goes wrong

1. **An absolute base URL.** Works locally, ships, breaks in production where nothing listens on 5131. The `startsWith('/api/')` guard does not catch it; only review does.
2. **`credentials: 'include'`**, because it looks more thorough. It drags in a CORS preflight against a server with no CORS configuration; the failure is an opaque network error with nothing to read.
3. **Reading `detail` for the message.** Every message is in `title`. Symptom: *every* error renders as an empty string — except the password-change `403`, the one that makes you think it works.
4. **Unguarded `response.json()` on an error body.** §2.2.
5. **Treating `404` as a bug.** It is the scoping mechanism (rule B). Logging, retrying or reporting "something went wrong" all misread the contract.
6. **Redirecting from inside `http.ts`.** `window.location.assign('/login')` reloads the document, discarding the router state that holds the path the user wanted — and it fires once per failed call, so a screen with four queries does it four times.

### What this step does NOT do, and why

- **No per-slice `api.ts`.** Those belong to the slice plans (§2.5). The one exception is `slices/identity/`, built in step 8 because Phase 0 owns the auth screens.
- **No interceptor that swallows an error.** Rule C.
- **No tuning of the §3.4 defaults.** A change there is a change to the governing document.
- **No generated client and no generator.** There is no OpenAPI document (§2.6); item 9 asks for one and item 6 must land first, because two routes declare a shape they do not always return. Do not wait for it and do not build it.

---

## 3. `src/shared/format/` and `src/shared/hooks/`

Deliberately early: steps 4 and 5 both type a `role`, and there must be one declaration of what a role is.

### 3.1 `format/enums.ts`

**File:** `frontend/src/shared/format/enums.ts`

Copy `UserRole` and `ROLE_LABELS` from `LoginArchitecture.md` §8 exactly; the identical block in §10.1 agrees.

```ts
export const UserRole = {
  AccountantAdmin: 0,
  AccountantUser: 1,
  CustomerAdmin: 2,
  Employee: 3,
} as const;
export type UserRole = (typeof UserRole)[keyof typeof UserRole];
```

Verified against `Shared/Auth/UserRole.cs`. **The declaration order is the wire contract** — no `JsonStringEnumConverter` is registered, so every C# enum serialises as its integer (item 4).

**A. `AccountantAdmin` is `0`, and `0` is falsy.** `if (session.role)`, `role || fallback` and `role ? label : 'unknown'` are all wrong for the most privileged role. Compare with `===` against a named constant, always.

**B. Never send a role as a string.** `InviteAccountantRequestDto.Role` and `SetEmployeeRoleRequestDto` bind an enum; `"AccountantUser"` is a `400` from model binding, before any handler runs, so with no useful message.

**C. Never render the raw number.** `ROLE_LABELS` is the only source of role text and its labels come from `00-Glossary.md`. The bare word "Admin" is banned — ambiguous between `AccountantAdmin` and `CustomerAdmin`. The labels are not the C# names: `AccountantAdmin` displays as "Accountant Admin".

Also here: the four status vocabularies as separate string unions — `CustomerStatus` (`'Active' | 'Suspended'` **only**), `AccountStatus` (`'Invited' | 'Active' | 'Suspended'`), employee status (`'Active' | 'Departed'`), audit outcome (`'Success' | 'Denied' | 'Failure'`). No two are the same, and **`Invited` belongs to exactly one** — it is a `UserAccount` status, the person, not the company (§10.1). A Customer is never `Invited`: `ListCustomersHandler` answers `422 "Unknown customer status."` and migration `20260901_002_AddCustomerStatusCheck.sql` adds a `CHECK`.

### 3.2 `format/dates.ts`

**File:** `frontend/src/shared/format/dates.ts`

The only module converting between wire timestamps and displayed text. Three wire shapes (§10.2):

| C# type | Wire | Handling |
|---|---|---|
| `DateOnly` | `"2026-09-02"` | Plain date, no timezone. **Never** build a `Date` and format in local time — that shifts it a day west of UTC |
| `DateTime` | `"…T14:33:12.4Z"` **or no suffix** | UTC, offset may be absent. Treat a bare value as UTC |
| `DateTimeOffset` | `"…+00:00"` | Has an offset; parse directly |

Export `formatDate`, `formatDateTime` (browser local, one consistent format) and a `parseUtc` that appends `Z` to a bare `DateTime`. No timezone label — one deployment serves one office — but the conversion happens here and nowhere else, because a timestamp silently eight hours out is a real support call.

### 3.3 `format/money.ts`

**File:** `frontend/src/shared/format/money.ts`

`MoneyAmount` arrives as a JSON number from a C# `decimal`. Format with `Intl.NumberFormat`; keep the raw number for arithmetic; never store a formatted string back into form state. There is **no currency field in the schema**, so formatting is locale-decimal, not currency-symbol, until one exists. Nothing in Phase 0 renders money; the file exists so the first slice that needs it does not invent a second one.

### 3.4 `hooks/usePaginatedQuery.ts`

**File:** `frontend/src/shared/hooks/usePaginatedQuery.ts`

One hook wrapping `useQuery` for a `PaginatedResponse<T>`. Every paginated list in every slice uses it and nothing else (§3.2 rule G), so the clamping trap is handled once.

```
usePaginatedQuery({ queryKey, queryFn, pageNumber, pageSize })
  # pageSize defaults to DEFAULT_PAGE_SIZE and is never sent above MAX_PAGE_SIZE
  # the caller supplies queryKey -- the hook does NOT build keys
  # returns the query result unchanged; it does NOT reshape items
  # exposes isOverrunPage = data.totalCount > 0 && data.items.length === 0
```

Three server behaviours it respects rather than defends against (§3.3):

1. **`pageSize` is clamped, not rejected.** Render the pager from `response.pageSize`, never from the value sent, or the pager computes the wrong page count and rows go missing with no error.
2. **A page past the end returns `items: []` with a `200`**, not a `404` — hence `isOverrunPage`, which `EmptyState` turns into "back to the first page" rather than "no results".
3. **Pages are 1-based; MUI's `TablePagination` is 0-based.** The conversion is **not** here. It is in `PaginatedTable`, in exactly one place.

The hook does not own the page number; the screen holds it in React state and passes it in.

### 3.5 Three ways this step goes wrong

1. **A second `UserRole` declaration** in `slices/identity/types.ts`, because that is where `SessionDto` lives. Two declarations drift, and the one that drifts is the one nobody imports.
2. **`new Date("2026-09-02").toLocaleDateString()`** for a `DateOnly`. West of UTC it renders 1 September — and it is correct on an authoring machine east of UTC, which is how it ships.
3. **`usePaginatedQuery` building query keys** "so callers do not have to". Then two filters share one cache entry and a screen shows another filter's rows (§3.1).

### What this step does NOT do, and why

- **No status colour helper.** The colour map belongs to `StatusChip`; sharing one map is the point.
- **`usePaginatedQuery` renders nothing.** It is a hook; the table is step 6.

---

## 4. `src/shared/auth/` — the session

Four files. Every guard depends on the session having three states, not two.

### 4.1 `SessionProvider.tsx`

**File:** `frontend/src/shared/auth/SessionProvider.tsx`

Bootstraps `GET /api/auth/me` and puts the result in context. The DTO, verified field by field against `Slices/Identity/Application/Dtos/AuthDtos.cs` — `SessionDto(string UserId, string DisplayName, UserRole Role, Guid? CustomerId, bool MustChangePassword)`:

| Field | Wire type | Note |
|---|---|---|
| `userId` | `string` | A Guid rendered as a string, not a `Guid` on the wire |
| `displayName` | `string` | Never blank; `AcceptInvitationHandler` refuses to clear it |
| `role` | `number` | `UserRole` from §3.1. `0` is `AccountantAdmin` |
| `customerId` | `string \| null` | `null` for both Accountant roles; non-null for `CustomerAdmin` and `Employee` |
| `mustChangePassword` | `boolean` | Check **before** routing anywhere |

There is **no `loginEmail`** — a real gap with a real consequence in §8.4 (punch-list item 11).

The query, from `LoginArchitecture.md` §1.2:

```tsx
const { data, isPending, error } = useQuery({
  queryKey: ['identity', 'session'],
  queryFn: getSession,
  retry: false,          // a 401 is an answer, not a transient failure
  staleTime: Infinity,   // the session does not change without a mutation that seeds it
});
```

| State | Condition | What the router renders |
|---|---|---|
| `loading` | the bootstrap has not settled | A full-page loader. **No route decision is taken** |
| `anonymous` | it settled with a `401` | Public routes only; anything else redirects to `/login` |
| `authenticated` | it settled with a `SessionDto` | The shell. If `mustChangePassword`, see §8.4 |

Register the two `http.ts` handlers here, once, in an effect:

```
registerUnauthorizedHandler(() => {
    queryClient.clear()      # LoginArchitecture.md section 6 rule A -- not just the session key
    setExpired(true)         # so /login shows one "your session ended" message
})
registerPasswordChangeRequiredHandler(() => {
    queryClient.invalidateQueries({ queryKey: ['identity','session'] })
    # the refreshed session carries mustChangePassword: true; RequireSession does the rest
})
```

`expired` is provider state, not storage: read once by the login screen and cleared, per §7 rule B — one message on the login screen, not a toast on a page that is about to unmount.

> **§1.4 rule A says `shared/` may never import from `slices/`, and `LoginArchitecture.md` §1.1 puts `getSession` in `slices/identity/api.ts` while §1.2 puts `SessionProvider` in `shared/auth/`.** Those cannot both hold. Precedence resolves it: §1.4 rule A wins. So **`SessionProvider.tsx` declares `SessionDto` and calls `get<SessionDto>('/api/auth/me')` from `shared/api/http.ts` directly** — which is what §1.2's own comment on the file, *"bootstraps GET /api/auth/me"*, describes — and `slices/identity/types.ts` re-exports the type rather than redeclaring it. Do **not** create a second `getSession`; two declarations of the session shape is the drift §1.4 exists to prevent. Recorded in §9.2.

**A. `retry: false` on this query specifically**, though the global policy already refuses 4xx. Retrying a `401` means three round trips before the login form appears.

**B. `staleTime: Infinity`.** The session changes in four places, all mutations that seed the cache: login, logout, change-password, and the 401 handler.

**C. Render a loader and nothing else while `isPending`** — not the shell with empty navigation, not the login form. Both are visibly wrong for the half-second they are on screen.

**D. Session mutations write into this key**, they do not invalidate it (§3.2 rule D) — with one exception, change-password, which returns `MarkedResultDto`. See §8.2.

**E. Nothing about the session goes into `localStorage`** (rule C). §1.3 gives three reasons: the cookie is the authority and the client cannot see it; a `role` in storage is user-editable and makes the client draw buttons that `403`, indistinguishable from a real permission-table bug; and a demotion already persists up to eight hours server-side, so caching makes the staleness unbounded. The cost of not caching is one round trip on cold load. That is the correct price.

### 4.2 `useSession.ts`

**File:** `frontend/src/shared/auth/useSession.ts`

One hook reading the context, returning a union discriminated on `status` (`'loading' | 'anonymous' | 'authenticated'`) so TypeScript refuses to let a caller read `session.role` in the `loading` branch. Throws outside the provider — that is a routing mistake and should say so loudly. **No component calls `useQuery(['identity','session'])` itself.**

### 4.3 `RequireSession.tsx`

**File:** `frontend/src/shared/auth/RequireSession.tsx`

Four branches, in this order (§4.3):

```
if status == 'loading'        -> <LoadingRegion />
if status == 'anonymous'      -> <Navigate to="/login" state={{ from: location }} replace />
if session.mustChangePassword -> <Navigate to="/change-password" replace />
otherwise                     -> children
```

The order matters: `mustChangePassword` first reads `session` in the `loading` branch, and `anonymous` before `loading` is the login-form flash.

`state={{ from: location }}` is the return-to mechanism, constrained by `LoginArchitecture.md` §2.3: **A.** location state, never a query parameter — a `?returnTo=` is an open redirect the moment it may hold an absolute URL. **B.** only redirect to a path starting with a single `/` and not `//` — `//evil.example.com` is protocol-relative and a browser treats it as another origin; validate it even though it came from your own router. **C.** if the stored path is a route the new role may not see, fall back to the landing route, or a `CustomerAdmin` bounced off `/audit` is shown access-denied as the first thing after a successful login. **D.** clear the stored path once used, or it redirects the next login too and surfaces weeks later as "logging in sends me to a random page". And §4.3 rule C: **this wraps the shell once**, in `routes.tsx`, not once per screen.

### 4.4 `RequireRole.tsx`

**File:** `frontend/src/shared/auth/RequireRole.tsx`

Takes `roles: UserRole[]`; renders `AccessDeniedPage` when the session's role is not in the set.

**A. It renders a denial page; it does not redirect.** A user who typed `/audit` deserves to be told the page is not for them; a silent bounce reads as a broken link and they try again.

**B. It is not a security boundary** — the same affordance logic as `can()`, applied to a page. The server denies the underlying calls with `403` and audits every denial regardless (§6.2 rule B).

**C. Compare with `roles.includes(session.role)`.** Never `indexOf(...) > 0` — `AccountantAdmin` is index `0` in most of these arrays, and `0` is falsy in every truthiness test a builder reaches for.

### 4.5 How this step is verified

1. Cold load, no cookie: loader, then the login form. **No** redirect loop, console clean.
2. Cold load with a valid cookie, hard refresh on a deep link: loader, then the page — **no** flash of the login form.
3. Deleting `aa_session` in devtools and clicking anything: exactly **one** redirect to `/login`.
4. `localStorage` and `sessionStorage` empty after login; no `Authorization` header anywhere.

### 4.6 Four ways this step goes wrong

1. **Two states instead of three.** Every guard sees "no session" during the first round trip and redirects an authenticated user to `/login`, who bounces back. Invisible on a fast machine.
2. **The bootstrap `401` is not exempted**, so the global handler fires on it and the app loops between `/login` and itself. The CPU pins and the page never renders.
3. **`if (session.role)`** anywhere in these four files. `AccountantAdmin` becomes "no role".
4. **A second `useQuery(['identity','session'])`** in a component that "just needs the display name". Same cache entry, so it works — until it is given a different `staleTime` and the two disagree about whether the user is logged in.

### What this step does NOT do, and why

- **No session polling.** The shell does not check `/api/auth/me` on a timer (§5.3): the cookie slides on every request, so polling to detect expiry would *prevent* the expiry it watched for.
- **No expiry countdown.** It cannot be built correctly against a sliding `HttpOnly` cookie (`LoginArchitecture.md` §7 rule D); a timer-based warning fires during active use.
- **No login form.** Step 8. This step ends with a session in context and nothing to log in with.

---

## 5. `src/shared/permissions/`

Two files, and the whole step is transcription. There is nothing to design.

### 5.1 `actions.ts`

**File:** `frontend/src/shared/permissions/actions.ts`

The `ActionName` union: the **35** action names in §6.1's table, in the order they appear there.

**Copy §6.1 exactly and add nothing.** Not a name from a screen spec, not one you find in a handler, not `UploadDocument`, not a plural that reads better. The reason is mechanical: `PermissionChecker` is **fail-closed on an unrecognised action name** and `can()`'s `?? false` mirrors it. A name here that no catalogue declares makes the UI draw a button that `403`s for everybody and writes a false `PermissionDenied` audit row on every click.

Re-verified against the six catalogues whose slices have a UI plan: `Slices/Audit/AuditActionCatalogue.cs:13` (1), `Customers/CustomersActionCatalogue.cs:13-22` (8), `Employees/EmployeesActionCatalogue.cs:22-63` (13), `Identity/IdentityActionCatalogue.cs:24-29` (6), `Notifications/NotificationsActionCatalogue.cs:13-14` (2), `TicketTypes/TicketTypesActionCatalogue.cs:13-18` (5). **35 catalogue entries, 35 names required by those six slices' handlers, 35 rows in §6.1. They match.**

There is a **seventh** catalogue on disk, and it is not one of the six above: `Slices/Tickets/TicketsActionCatalogue.cs:33-83` (22) — the eighteen ticket actions plus the four `Documents` actions that file registers on that slice's behalf. So a glob of `Slices/*/*ActionCatalogue.cs` finds **seven files and 57 names**, and a builder who diffs `actions.ts` against that glob will conclude 22 names are missing. **They are not missing.** §6.1 is the union of the six, on purpose: there is no `Tickets` UI plan, this phase builds no `Tickets` screen, and `UploadDocument` — the very name the paragraph above tells you not to add — is one of that file's four `Documents` entries. The number to satisfy here is **35**.

> **Punch-list item 26 is resolved and §6.1 is complete as printed.** It is struck through and marked **RESOLVED 2026-09-02** at `BACKEND_CHANGES_REQUIRED.md:527`; `EmployeesActionCatalogue.cs:53` declares `ReinstateEmployee` and `:60` declares `ChangeEmployeeLoginEmail`. If you were told that two action names living in shipped handlers are deliberately absent from §6.1, that is stale — none is. The instruction is unchanged and unconditional: **copy §6.1 exactly and add nothing.**

Login, logout, `/api/auth/me` and change-password are deliberately **not** actions and must not be added. `IdentityActionCatalogue.cs`: *"An entry listing all four roles would imply a role decision where there is not one, and would be a check that can only ever pass."*

### 5.2 `can.ts`

**File:** `frontend/src/shared/permissions/can.ts`

`ACTIONS: Record<ActionName, UserRole[]>` — all 35 rows of §6.1 transcribed column by column — and the function, copied exactly:

```ts
export function can(role: UserRole | undefined, action: ActionName): boolean {
  if (role === undefined) return false;          // no session: nothing is permitted
  return ACTIONS[action]?.includes(role) ?? false; // unknown action: deny, matching the server
}
```

Two rows are the ones a builder tidies, and both must be left alone (§6.1): `ReinstateEmployee` includes `CustomerAdmin`; `ChangeEmployeeLoginEmail` does **not** and is `[AccountantAdmin, AccountantUser]` (`EmployeesActionCatalogue.cs:60`). `02-AuthorizationMatrix.md` §4 gives the reasons — a Customer Admin who can enter a departure must be able to correct one, and *"changing a login email is reserved to the Office, and nobody may change their own"*. `can(CustomerAdmin, 'ChangeEmployeeLoginEmail') === false` **by design**.

Five rules from §6.2 that every later plan relies on:

**A. `can()` decides affordances, never data** (§0.5 rule A).

**B. `can()` returning `true` followed by a `403` is a bug in this table, not on the server.** The server is fail-closed and audits every denial. Fix the row; do not add a `catch` that swallows it.

**C. Prefer hiding to disabling, with one exception.** A button a user can never enable is noise — but `02-AuthorizationMatrix.md`:146 names a case that must stay visible rather than greyed out. Honour it where the matrix names it; hide otherwise.

**D. The table says *who may call*, not *which rows*.** Several grants are really "yes, for their own Customer", which no catalogue entry can express. Row-level scoping is the server's, enforced by `CustomerScope`, and it surfaces as a `404`. A `can()` of `true` never means "this record".

**E. Never persist or cache a permission decision.** `can()` is a pure function of the role.

### 5.3 How this step is verified

By reading the six C# catalogues named in §5.1 against `can.ts`, one row at a time — the six, not all seven on disk; `TicketsActionCatalogue.cs` is not one of them and its 22 names must not appear. It is the only check available: **there is no endpoint that exposes the catalogue**, and per §6.3 one is deliberately not requested.

### What this step does NOT do, and why

- **No fetch of the permission table.** §6.3 — `IActionCatalogue` is internal, and a fetched table would still be advisory because of rule D.
- **No `usePermissions` hook and no `<Can>` component.** Neither is in §1.2. `can(session.role, 'CreateCustomer')` at the call site is one line and greppable.
- **No route-level action map.** Routes are gated by *role*, in §4.1's table, not by action.

---

## 6. `src/theme.ts` and `src/shared/components/`

Ten components, all listed in §1.2 and §8.3. **No eleventh is invented in this phase.**

> **If you were given a list of eight for this step, it is short by two.** §1.2's tree and §8.3's table both list ten, and the two usually dropped are the two **required by step 4**: `LoadingRegion.tsx`, rendered by `RequireSession` while the session loads, and `AccessDeniedPage.tsx`, rendered by `RequireRole`. §1.2 outranks any summary of it. Build all ten.

### 6.1 `theme.ts`

**File:** `frontend/src/theme.ts`

One `createTheme` call; one `ThemeProvider`, in `main.tsx`, wrapped in `CssBaseline` (§8.1). Every colour, radius, spacing step and font size comes from here. **A hex literal in a component is a defect**: the look will be adjusted once, globally, by somebody who will search the theme file and find nothing. `sx` is for layout local to one component — a gap, a width, an alignment — never for colour or typography. An identical `sx` in three places is a component that should exist in `shared/components/`.

### 6.2 The ten components

| **File:** `frontend/src/shared/components/…` | Responsibility | The one thing to get right |
|---|---|---|
| `AppShell.tsx` | AppBar, nav, account menu, `<Outlet />` | Nav derives from §5.2's table and the role, **not** from `can()` |
| `PageHeader.tsx` | Title, subtitle, primary action slot | The slot is where `can()` gates a button; the header does not gate it |
| `PaginatedTable.tsx` | `PaginatedResponse<T>` + columns + page callback | The **only** 1-based/0-based conversion in the app |
| `ConfirmDialog.tsx` | Confirm an irreversible action | Names the consequence, not "are you sure?" |
| `StatusChip.tsx` | Status word → coloured `Chip` | One colour map across all four vocabularies; the word is always shown |
| `ErrorBanner.tsx` | An `ApiError` → an `Alert` | Owns the §7.1 taxonomy; screens pass the error and nothing else |
| `EmptyState.tsx` | Icon, sentence, optional action | Handles the `totalCount > 0` over-run case |
| `LoadingRegion.tsx` | Centred progress inside a region | Never full-page |
| `NotFoundPage.tsx` | "Not found" | Also the `*` route |
| `AccessDeniedPage.tsx` | "You do not have permission" | Rendered by `RequireRole` only |

**`AppShell`** — the §5.1 layout: one horizontal `AppBar`, **no sidebar** (seven destinations at most, four for any role; a drawer for four links is machinery with nothing to manage). On small screens the nav collapses into a `Menu`. The account menu shows the display name **and the role**, and offers *Profile* and *Sign out* — the role is shown because two people at the same Customer see different buttons, and "why can she suspend and I cannot" is otherwise unanswerable from the screen. Transcribe §5.2's seven rows exactly. An `Employee` gets three items; correct, and not a gap to pad (§12 item 2). In Phase 0 the destinations that do not exist resolve to the `*` route.

**`PaginatedTable`** — owns the single `pageNumber - 1` / `page + 1` conversion (§3.3 item 3, §8.2). `rowsPerPage` comes from `response.pageSize`, never from the value sent. **No screen composes `Table` + `TablePagination` itself**, in any slice, ever. `@mui/x-data-grid` is banned; §8.2 gives four reasons and the shortest is that every list here is server-paginated with a fixed envelope.

**`ErrorBanner`** — the one implementation of §7.1's ten-row taxonomy. `role="alert"`, so a screen reader announces a failed submit; silence after *Save* is indistinguishable from a hung request. Show the `traceId` on `500` **and nowhere else** — printing it on a `422`, where `title` already says what is wrong, teaches users to ignore it. Never render "forbidden" for a `404` (rule B).

**`StatusChip`** — one colour per word, so `Suspended` is never green on one screen and red on another. Sharing the map does **not** make every word valid for every entity: fed a Customer's status it must never be able to render `Invited` (§3.1).

**`ConfirmDialog`** — required wherever the server says an operation is irreversible or costly to undo. Nothing in Phase 0 uses it; it exists so the first slice that needs it does not invent a local dialog. The reference case and its required copy are in `Screens/EmployeesScreens.md` §8.1, and the dialog **must not** be softened into "you can always undo this".

**`NotFoundPage`** — also the mandatory `*` route (§4.4). Once item 1 lands, `MapFallbackToFile("index.html")` means every non-`/api` path returns the SPA with a `200`, and the server cannot tell `/customers` from `/custmoers`. Without a `*` route a typo renders a blank page with no error in the browser and none in the logs.

**Loading and empty states** (§7.4, §5.3), which set the pattern for every later screen: no global spinner and no global error toast; loading renders inside the region that is loading; refetches keep the old rows visible; buttons disable while their mutation is pending but never the whole form; `items: []` with `totalCount: 0` is an `EmptyState`, not an error; `items: []` with `totalCount > 0` offers "back to the first page". Toasts are for **successes** only, where there is nothing on screen to attach the message to.

**The accessibility floor** (§8.4), on all ten: a real `<label>` on every input (MUI's `TextField label=` does this; a `placeholder` does not); `role="alert"` on error banners; focus to the banner on a failed submit and to the first heading on route change; an `aria-label` on every icon-only button. Colour is never the only carrier of meaning.

### 6.3 How this step is verified

Phase 0 has no list screen, so `PaginatedTable` cannot be exercised end to end until the first slice plan runs. Verifiable now: the shell renders the correct nav for the logged-in role, `NotFoundPage` renders for a nonsense URL, `AccessDeniedPage` renders when `RequireRole` refuses, and `LoadingRegion` is visible for the bootstrap query on a throttled connection.

### 6.4 Four ways this step goes wrong

1. **An eleventh component** — a `Card` wrapper, a `Page` layout, a `FormRow`. Each is a decision this phase is not authorised to make (rule H), and each becomes load-bearing before review.
2. **The nav derived from `can()`.** A nav item maps to a *page*, not an action, and several pages combine actions with different role sets — `/employees` is visible to a `CustomerAdmin` who may list employees but may not onboard a Customer (§5.2).
3. **The 0-based conversion done in a screen as well.** Two conversions is an off-by-one that hides the first or last row of a list.
4. **A global spinner or a global error toast**, because both are one line. §5.3 rules both out: a top-level spinner blanks the nav the user was about to click again, and a toast is dismissible, unlocatable, and gone before it has been read.

### What this step does NOT do, and why

- **`src/shared/dynamicForm/` is NOT part of Phase 0.** §1.2 lists four files — `DynamicForm.tsx`, `fieldRegistry.tsx`, `buildZodSchema.ts`, `visibility.ts` — all specified in `Screens/TicketTypesScreens.md` and **owned by the TicketTypes UI plan**. Do not create the folder, do not stub the files, do not add an empty `visibility.ts` "so the import resolves". Nothing in Phase 0 renders a `FieldDescriptor[]`; the contract lives in a document this plan does not govern; and a stub written from the folder tree alone will be wrong in the eleven-row `dataType` switch and the runtime Zod builder — the two hardest things in the front end.
- **No slice component.** `shared/` may never import from `slices/` (§1.4 rule A); a shared component that knows about a slice has been misfiled.

---

## 7. `src/routes.tsx`, `src/App.tsx`, `src/main.tsx`

### 7.1 `routes.tsx`

**File:** `frontend/src/routes.tsx`

Transcribe **all 21 rows** of §4.1's table, including routes whose screens do not exist yet. A route pointing at a screen a later plan will write is a `TODO` a reviewer can see; a missing route is a 404 nobody can explain. For Phase 0, point every not-yet-built screen at `NotFoundPage` with a one-line comment naming the plan that owns it. Do **not** create empty files in `slices/*/screens/`.

```
/login  /forgot-password  /reset-password  /accept-invitation   public, no shell
/change-password                                                RequireSession, no shell
                          # everything below: RequireSession -> AppShell -> RequireRole per row
/                         role redirect
/customers ... /profile   per section 4.1
*                         NotFoundPage, inside the shell
```

**A. The five *shell: no* routes render standalone and centred** (§4.2). Someone at `/login` has no role to draw a nav bar from; someone at `/change-password` has a session the server rejects on every other route, so navigation would offer ten links that all `403`.

**B. `RequireSession` wraps the shell once** (§4.3 rule C); `RequireRole` wraps the rows that name a role subset. **C.** Paths are kebab-case — `/ticket-types`, never `/tickettypes` or `/ticketTypes`.

**D. SPA routes take path parameters; API routes never do.** `/employees/:employeeId` becomes `POST /api/employees/get` with `{ employeeId }` (§2.3 rule D). Not an inconsistency to fix: a URL a user bookmarks needs the id in it, and an API that never puts ids in paths never has a route-vs-body ambiguity.

**E. `/accept-invitation` and `/reset-password` are contract, not UI choices.** `Slices/Identity/Application/TokenLinks.cs` builds `{baseUrl}/accept-invitation?token=…` and `{baseUrl}/reset-password?token=…` and mails them. Renaming either — or the `token` parameter — breaks every link already in an inbox, and invitation links are live for **7 days** (`Slices/Identity/Core/UserAccountToken.cs:35`). Do not "align" `/reset-password` with its endpoint name `complete-password-reset`; `LoginArchitecture.md` §4.3 explains why they differ on purpose.

**F. The `*` catch-all is mandatory** (§4.4) and renders **inside** the shell, so a user who mistypes a URL still has navigation to get back with.

> **Where an `Employee` lands: `/profile`.** **G.** Build `/` → `/profile` for `EMP`, `/customers` for `AA`/`AU`, `/employees` for `CA`. Read the destinations off `GeneralUIArchitecture.md` §4.2's table and nothing else.
>
> This row was contested until 2026-09-02: §4.2 then sent `EMP` to `/ticket-types` (*"The only list an Employee can act on today"*) while `LoginArchitecture.md` §2.2 and §2.6 argued for `/profile`. §4.2 has since been corrected to `/profile`, so all three documents — plus `../../../README.md` — now agree, and §4.2's row says in terms: *"Do not 'improve' this to `/ticket-types`."* The reason is worth carrying, because a builder will be tempted: `/ticket-types` **is** reachable by an `Employee` (§4.1), but it is a catalogue of forms they cannot submit until the `Tickets` UI ships, so landing there reads as a broken home rather than an empty one.
>
> The underlying question — what an `Employee` should actually see before the `Tickets` UI ships — is still open in every one of those documents. This plan does not answer it. All three destinations are provisional and change when the `Tickets` UI ships (§12 item 1).

### 7.2 `App.tsx`

**File:** `frontend/src/App.tsx`

`<RouterProvider>` and nothing else (§1.2). No providers here — they are in `main.tsx`, so the router is created once and the provider tree is visible in one file.

### 7.3 `main.tsx`

**File:** `frontend/src/main.tsx`

`createRoot`, the provider stack, `<App />`. No side effects, no configuration reading, no `console.log`. The order is not arbitrary:

```
QueryClientProvider  value={queryClient}
  ThemeProvider      theme={theme}
    CssBaseline
      SessionProvider
        App
```

`SessionProvider` sits **inside** `QueryClientProvider` because its bootstrap is a `useQuery` and its handlers call `queryClient.clear()`, and **outside** the router because `RequireSession` reads the session on every route.

### 7.4 How this step is verified

1. `/custmoers` renders `NotFoundPage`, not a blank screen.
2. With no cookie, opening `/customers` directly lands on `/login`, once.
3. `/` with a session redirects by role per §4.2 and the nav shows exactly §5.2's items.
4. `/audit` as an `AccountantUser` renders `AccessDeniedPage`, with no *Audit log* nav item.

Checks 3 and 4 need a login, so they are re-run at the end of step 8.

### 7.5 Four ways this step goes wrong

1. **No `*` route.** A typo'd URL renders blank, with nothing in the console and nothing in the log.
2. **`RequireSession` per screen** instead of once around the shell. The next screen added has no guard and nobody notices.
3. **The `/` redirect written as `if (session.role)`** or a `switch` whose `default` catches `0`. `AccountantAdmin` lands wherever the fallback points.
4. **Provider order inverted** — `SessionProvider` outside `QueryClientProvider`. The failure is a "No QueryClient set" throw on first render, which reads as a broken installation.

### What this step does NOT do, and why

- **No lazy loading or route-level code splitting.** Not specified in `UI/`, and it changes what a `Suspense` boundary means for the three-state session. If the bundle later needs splitting, that is a change to `GeneralUIArchitecture.md`.
- **No screen file for any other slice.** Everything but the five auth screens is a `NotFoundPage` placeholder with a comment.

---

## 8. The auth screens

Six flows, five screens, one mutation with no screen. All in `slices/identity/` — the one slice folder Phase 0 creates, because without a login no other screen is reachable and `DevAuthHandler` is gone. Every route, field name, status code and message below was read out of the C# source.

### 8.1 `types.ts` and `api.ts`

**File:** `frontend/src/slices/identity/types.ts`

Re-export `SessionDto` from `shared/auth/SessionProvider` (§4.1) and declare the five request DTOs, verified against `Slices/Identity/Application/Dtos/AuthDtos.cs`:

| Interface | C# record | Fields |
|---|---|---|
| `LoginRequest` | `LoginRequestDto` | `email`, `password` |
| `ChangePasswordRequest` | `ChangePasswordRequestDto` | `currentPassword`, `newPassword` |
| `RequestPasswordResetRequest` | `RequestPasswordResetRequestDto` | `email` |
| `CompletePasswordResetRequest` | `CompletePasswordResetRequestDto` | `token`, `newPassword` |
| `AcceptInvitationRequest` | `AcceptInvitationRequestDto` | `token`, `newPassword`, `displayName?` |

Plus `MarkedResult` for `MarkedResultDto(bool Success)` — what logout and change-password return. `displayName` is the only optional field. Send it **only when the user typed something**; never `""` (§9.3 rule F).

**File:** `frontend/src/slices/identity/api.ts`

One function per endpoint, named for the endpoint. Verified against `IdentityEndpoints.cs`:

| Function | Route | Verb | Line | Declared |
|---|---|---|---|---|
| `login` | `/api/auth/login` | POST | :32 | `SessionDto`, 401, 422 |
| `requestPasswordReset` | `/api/auth/request-password-reset` | POST | :42 | 200 **only** |
| `completePasswordReset` | `/api/auth/complete-password-reset` | POST | :50 | 400, 422 |
| `acceptInvitation` | `/api/auth/accept-invitation` | POST | :59 | 400, 422 |
| `logout` | `/api/auth/logout` | POST | :69 | 401 |
| `changeOwnPassword` | `/api/auth/change-password` | POST | :83 | 401, 422 |

`/api/auth/me` is the **one GET** in the group (`:76`) and is called by `SessionProvider`, not here. `api.ts` contains no React, no hooks and no TanStack Query, so it can be read against the C# file line by line (§2.5).

### 8.2 `queries.ts`

**File:** `frontend/src/slices/identity/queries.ts`

Three mutations, three different cache behaviours. Getting one wrong looks like a server bug.

| Mutation | Returns | Cache action | Why |
|---|---|---|---|
| `useLogin` | `SessionDto` | **Seed** `['identity','session']` | §3.2 rule D — invalidating throws away a response you already have |
| `useChangeOwnPassword` | `MarkedResultDto` | **Invalidate** `['identity','session']` | It is **not** a `SessionDto`. The handler re-issues the cookie with the flag cleared, so the next `/api/auth/me` returns `false`. Skipping this leaves the stale `true` and the user is returned to this screen forever, having already succeeded |
| `useLogout` | `MarkedResultDto` | `queryClient.clear()` — the **whole** cache | Otherwise the next user at the same browser sees the previous user's lists flash on screen. On a shared office machine that is a real disclosure |

### 8.3 `LoginScreen`

**File:** `frontend/src/slices/identity/screens/LoginScreen.tsx`

Route `/login`, public, no shell. Two fields (`LoginArchitecture.md` §2.1): `email` — `TextField type="email"`, `autoComplete="username"`, `z.string().min(1).email()`; `password` — `type="password"`, `autoComplete="current-password"`, `z.string().min(1)`. **Do not apply the password policy to this form.** The policy governs *choosing* a password; a client-side `min(12)` here locks out a user whose existing password predates the rule, with a message that makes no sense to them.

**Errors are deliberately opaque and must stay that way.** `LoginHandler` returns one `401` with one message — `"Invalid email or password."` (`Handlers/LoginHandler.cs:38`) — for **six** causes: no such account, wrong password, still `Invited`, `Suspended`, locked out, and owning Customer suspended. The handler requires the response to be *byte-for-byte identical*, because any distinction answers *does this address have an account here*.

**A. Render `error.title` verbatim.** No appended guess, no "your account may be suspended", no wording that varies by attempt. Every embellishment is a channel.

**B. No lockout message and no client counter.** Lockout is 5 failures then 15 minutes (`LoginHandler.cs:28,30`); the response is the same `401` with the same sentence, and the client does not know whether the account exists, let alone its counter.

**C. Do not rate-limit the form.** The handler deliberately does not extend a lockout on repeated attempts, because that turns brute-force protection into a denial of service against the victim.

**D. A `422` here is a malformed request, not a credential failure** — the body failed model binding. Render it as a form banner; it indicates a client bug.

**E. A `429` comes from Caddy, carries no account information, and is safe to be specific about.** Its body may be HTML or empty, which is what §2.2's tolerant parse is for. It cannot be produced in development (§0.4 item 2).

On success, check `mustChangePassword` **before** routing anywhere, then route by role — with a valid return-to path taking precedence (§4.3): `AccountantAdmin` (`0`) and `AccountantUser` (`1`) → `/customers`; `CustomerAdmin` (`2`) → `/employees`; `Employee` (`3`) → `/profile`, per §7.1 rule G. The screen also renders the one-time "your session ended" message when `SessionProvider` reports `expired` (§4.1), and a link to `/forgot-password`.

**Seven things it must not do** (§2.5): check whether an email exists before submitting; offer a role picker; offer "remember me" (expiry is 8 hours sliding, fixed at `IdentityRegistration.cs:86-87` — the checkbox would do nothing); count attempts or disable after N failures; show a strength meter; pre-fill the email from `localStorage`; link to a register page. **There is none** — accounts come only from `/api/accountants/invite`, `/api/employees/invite`, or `/api/customers/onboard`.

### 8.4 `ChangePasswordScreen`

**File:** `frontend/src/slices/identity/screens/ChangePasswordScreen.tsx`

Route `/change-password`, authenticated, **no shell**. `POST /api/auth/change-password` with `{ currentPassword, newPassword }`.

The gate that breaks every other screen if missed. An authenticated user whose `must_change_password` claim is `"true"` gets **403 on every route** except exactly three (`Shared/Auth/MustChangePasswordMiddleware.cs`, read in full): `/api/auth/change-password`, `/api/auth/logout`, `/api/auth/me`. The body carries `Detail = "You must change your password before continuing."` and `Extensions["traceId"]`, and it is the **one response in the entire API that populates `detail`**. The match lives in `http.ts` as a single constant (§2.3).

**A. Route on it, do not toast it.** A state the account is in, not a failed action.

**B. Check `session.mustChangePassword` too.** The bootstrap returns the flag, so the app can route here before making a request that would `403`; the interceptor covers the flag being set by another session mid-flight. Both paths must exist — either alone leaves a hole.

**C. Renders outside the shell.** Navigation while every destination `403`s is a menu of dead links.

**D. No "skip for now".** There is nothing to skip to. **Logout is the only other permitted action and the screen must show it** — the middleware allows `/logout` for exactly this reason, and omitting the button is what makes the gate feel broken, because the user's only escape is clearing cookies by hand.

The Zod schema, verified against `Slices/Identity/Application/PasswordPolicy.cs` (read in full) and `Handlers/ChangeOwnPasswordHandler.cs`:

| Rule | Value | Server | Source |
|---|---|---|---|
| Required | non-empty | 422 | `PasswordPolicy.cs` |
| Minimum length | **12** | 422 | `PasswordPolicy.cs:11` |
| Maximum length | **128** | 422 | `PasswordPolicy.cs:17` |
| Not equal to the login email | case-insensitive, **trimmed** | 422 | `PasswordPolicy.cs:37-38` |
| Different from the current password | exact, case-sensitive | 422 | `ChangeOwnPasswordHandler.cs:92` |

> **`LoginArchitecture.md` §3.4 attributes all five rules to `PasswordPolicy.Validate`. The fifth is not there.** `PasswordPolicy.cs` enforces required, min 12, max 128 and not-equal-login-email, and nothing else. *"The new password must be different from the current one."* is a `422` raised at `Handlers/ChangeOwnPasswordHandler.cs:92`, after the policy call. What the client mirrors is unchanged — all five are enforced, all five are `422` — but a builder who opens `PasswordPolicy.cs` looking for the fifth will not find it and must not conclude it does not exist.

**There are deliberately no composition rules** — no required uppercase, digit or symbol, following NIST SP 800-63B, and `PasswordPolicy.cs:19-23` says so in a comment. **Do not add them** because they look more secure: a client rule the server does not enforce rejects passwords the server would have accepted, and the user cannot discover which rule is imaginary.

Two ordering facts worth mirroring. The handler validates the **new** password *before* verifying the current one (`ChangeOwnPasswordHandler.cs:65`, deliberately), so a 6-character new password is reported as such rather than producing a `401` the user reads as "I got my old password wrong". And **a wrong current password is `401`, not `403`** (`:88`) — a failed credential check, which does not increment the lockout counter and cannot lock the account, so do not warn that it might. That `401` must **not** trip the global handler into logging the user out: it arrives as a mutation error on this screen and is rendered as a form banner.

`ChangePasswordRequestDto` has **no target user field**, deliberately, so the endpoint cannot be pointed at another account even by mistake. `02-AuthorizationMatrix.md` §11: resetting another person's password directly is permitted to **nobody**. There is no administrative password reset to build, for any role.

**The client cannot check the email rule.** `SessionDto` carries no `loginEmail` (§4.1), so that comparison happens server-side only and surfaces as a `422` banner. Punch-list item 11; §9.2.

Who arrives here: **the seeded first Accountant Admin**, because `Shared/Seeding/DatabaseSeeder.cs:93` sets `MustChangePassword = true` — the seeded password came from an environment variable visible in `docker inspect`, in shell history and in the compose file. That is the first login anyone performs against a new deployment, so this is the first screen a builder exercises. **Nobody else today**: `AcceptInvitationHandler` and `CompletePasswordResetHandler` both set the flag to `false`, because the person chose the password themselves.

### 8.5 `ForgotPasswordScreen`

**File:** `frontend/src/slices/identity/screens/ForgotPasswordScreen.tsx`

Route `/forgot-password`, public, no shell, reachable from `/login`. `POST /api/auth/request-password-reset` with `{ email }`.

**This endpoint returns 200 unconditionally.** `IdentityEndpoints.cs:42` declares `.Produces<MarkedResultDto>()` and nothing else — no 404, no 422 — because an unknown address must get the same answer as a known one. The handler does not even validate the format: a `422` for a malformed address and a `200` for a well-formed unknown one is the same oracle, just quieter.

**A. Show the neutral confirmation, always.** "If that address has an account, a reset link is on its way." Not "check your inbox" (implies an account exists), not "we could not find that address" (impossible — the server never says so).

**B. Do the format check client-side anyway** — `z.string().email()` — so a typo is caught before the user waits for an email that will never arrive. Not a security control.

**C. Replace the form with the confirmation on success.** A live form invites repeated submissions, and each one invalidates the previous token: a user who clicks twice and opens the first email gets "that link is invalid or has expired" for a link that was valid a minute ago.

**D. The reset token lives 1 hour** — `Slices/Identity/Core/UserAccountToken.cs:36`, `PasswordResetLifetime = TimeSpan.FromHours(1)`. State the window in the confirmation copy.

### 8.6 `ResetPasswordScreen`

**File:** `frontend/src/slices/identity/screens/ResetPasswordScreen.tsx`

Route `/reset-password?token=…`, public, no shell. Reads `token` from the query string, collects a new password, calls `POST /api/auth/complete-password-reset` with `{ token, newPassword }`.

**A. Every failure is one `400` with one message** — `"That link is invalid or has expired."` (`Handlers/CompletePasswordResetHandler.cs:16`) — covering no such token, wrong purpose, already consumed, expired, and *account suspended between the request and the click*. Render it verbatim; do not try to distinguish expiry from consumption, because the server will not tell you, on purpose.

**B. A missing or empty `token` is a `400` too.** Detect it client-side and render the same message with no round trip, rather than submitting an empty token.

**C. Completing a reset does NOT sign the user in.** The handler's comment is explicit: a leaked reset link must not grant a live session in one step. Redirect to `/login` with a success message. **Do not** call `/api/auth/me` hoping for a session — there is none, and the `401` trips the global handler.

**D. The reset clears the lockout** as well as the password. Worth knowing when a user reports "I reset my password and still cannot get in" — that symptom is not this flow.

**E. The token must never reach history, a log, or an analytics call** (rule G). Read it once, hold it in component state, `replace` the URL. It is a single-use credential in a query parameter, already the weakest link in the flow.

Same password schema as §8.4, minus the current-password field and minus the differ-from-current rule, which cannot apply — the user is not authenticated and has nothing to compare against.

### 8.7 `AcceptInvitationScreen`

**File:** `frontend/src/slices/identity/screens/AcceptInvitationScreen.tsx`

Route `/accept-invitation?token=…`, public, no shell. `POST /api/auth/accept-invitation` with `{ token, newPassword, displayName? }`.

Structurally identical to §8.6 — one opaque `400` for every failure (`"That invitation is invalid or has expired."`, `Handlers/AcceptInvitationHandler.cs:17`), no session on success, redirect to `/login`. Three differences:

**A. The token lives 7 days**, not one hour (`Core/UserAccountToken.cs:35`). An invitation waits on a human; a reset answers something the person just did.

**B. `displayName` is optional and absent means "keep what the inviter typed".** An empty or whitespace-only string is treated as absent, **not** as an instruction to blank the name. Cap it at **200** — `AcceptInvitationHandler.cs:20`, `DisplayNameMaximumLength = 200`, `422` at `:84-86`. **This differs from the 255 used for most display names elsewhere in the API.** Send the field only when the user typed something.

**C. The account must still be `Invited`.** A replayed link, or one invited-activated-suspended-reactivated, gets the same opaque `400`. **Do not offer a "resend" button** — there is no anonymous resend endpoint and the person cannot log in to ask for one. The correct path is a fresh invitation from an Accountant Admin.

Redeeming the token **is** the email confirmation; a separate confirm step would ask the person to prove the same thing twice by the same means. The screen is role-agnostic and **must not guess who it serves** — it cannot: the token is opaque and the caller is anonymous. All three producers (`/api/accountants/invite`, `/api/employees/invite`, `/api/customers/onboard`) land the invitee here with the same token purpose (§5.2, and §0.4 item 3 for the verification that all three exist).

### 8.8 Logout

**File:** `frontend/src/slices/identity/queries.ts` (no screen of its own)

`POST /api/auth/logout`, no body, returns `{ success: true }`. Rendered as a menu item in `AppShell`'s account menu and as a button on `/change-password`. There is **no sessions table** — the cookie *is* the session — so `SignOutAsync` only queues a `Set-Cookie` that clears it; nothing can fail halfway and leave a session alive on the server.

**A. Clear the entire cache on success**, not just the session key (§8.2). **B. Navigate to `/login` with `replace`**, so the back button does not return to a route that now `401`s. **C. Logging out twice is a `200` both times**; it is idempotent, so do not guard the button against a double click with an error. **D. If logout itself fails, clear and redirect anyway** — the user asked to leave, and the cookie will be rejected on its own schedule regardless. **E. Logout is permitted while `mustChangePassword` is set**, so the button must be on `/change-password`.

### 8.9 How this step is verified — and the one thing that cannot be

Work through the 23 behavioural cases in `LoginArchitecture.md` §9. The eight that matter most now:

1. Cold load, no cookie: `401`, the login form, **no** redirect loop.
2. Hard refresh on an authenticated deep link: loader, then the page, **no** flash of the login form.
3. Wrong password and unknown email produce the same response and the same rendering.
4. Six consecutive wrong passwords: still the same message, no lockout notice, form still submittable.
5. First login as the seeded admin: `mustChangePassword` is `true`, the app lands on `/change-password` **without** the shell, and a Logout button is present.
6. In that state, navigating to `/customers` by URL: the `403`'s `detail` is matched and the app returns to `/change-password` with **no** error toast.
7. A 6-character new password: `422`, form banner, and the message is about the **new** password.
8. A successful change: the session is invalidated, the flag becomes `false`, the shell appears, and the user is not asked again.

> **The reset and invitation screens cannot be verified end to end in development as configured, and the reason is not the UI's.** `appsettings.Development.json` sets `Notifications:Email:Enabled = false`, so `NotificationsRegistration.cs:56` never registers `OutboxDrainer` and no email is sent. Because the drainer never runs, the outbox row is **not** marked `Skipped` and its `email_body` is **not** cleared — the `if (!_options.Enabled)` branch at `Infrastructure/OutboxDrainer.cs:202` is unreachable when the drainer is not hosted at all. So the raw link survives in the database and a builder with `psql` can read it: `SELECT email_body FROM notification_outbox ORDER BY created_at DESC LIMIT 1;` (table and column from `Infrastructure/Migrations/20260830_001_CreateNotificationsSchema.sql:23,35`). Two cautions: that value is a **live single-use credential**, so do not paste it anywhere that is logged; and `App:BaseUrl` is `https://localhost:5173` while the dev server serves plain `http`, so the scheme must be corrected by hand. If either is unacceptable in your environment, these two screens are verified **by inspection only** and must be reported as such. Do not write that they were verified.

### 8.10 Five ways this step goes wrong

1. **The login error is embellished.** "Your account may be suspended — contact your accountant" turns an opaque `401` into an enumeration oracle, and it takes one well-meaning commit.
2. **Change-password seeds the session instead of invalidating it.** The endpoint returns `MarkedResultDto`, so the seed writes `{ success: true }` over the session and the app no longer knows who is logged in. The variant that skips the invalidation entirely traps a user who has already succeeded.
3. **The reset token stays in the URL**, reaching history, the referrer of any outbound link, and every analytics call the page makes. Rule G.
4. **`displayName: ""` is sent** on an untouched field. The handler treats blank as absent, so nothing breaks today — which is why the habit survives to a field where it does (§9.3 rule F).
5. **The wrong-current-password `401` logs the user out.** The global handler fires and the user is bounced to `/login` for a typo. Mutation errors on this screen are rendered, not acted on, and this is the one place the two behaviours collide.

### What this step does NOT do, and why

- **No `/profile` screen.** Owned by the `Identity` UI plan (`Screens/IdentityScreens.md` §7), and specified read-only partly because of punch-list item 12.
- **No accountant management.** `/api/accountants/*` is six endpoints and a list screen — the `Identity` plan's work.
- **No administrative password reset.** There is none to build, for any role (§8.4).
- **No self-service display-name or login-email edit.** No endpoint exists (item 10).
- **No "resend invitation".** No endpoint exists, and it is an open question in §13.

---

## 9. Phase exit gate

### 9.1 Files checklist

Everything Phase 0 creates. If a file is not on this list, this plan does not authorise creating it.

- [ ] `frontend/package.json` — §1.5 dependencies, three scripts, no hand-typed versions
- [ ] `frontend/tsconfig.json` — `strict`, `noUncheckedIndexedAccess`, no path aliases
- [ ] `frontend/vite.config.ts` — copied from §11.1, proxy to **5131**
- [ ] `frontend/index.html` — no CDN link, no analytics
- [ ] `frontend/.gitignore`
- [ ] `frontend/src/shared/api/ApiError.ts`, `problemDetails.ts`, `paginated.ts`, `queryClient.ts`
- [ ] `frontend/src/shared/api/http.ts` — the only `fetch`; the bootstrap 401 exemption; `MUST_CHANGE_PASSWORD_DETAIL`
- [ ] `frontend/src/shared/format/enums.ts` — `UserRole`, `ROLE_LABELS`, the four status unions
- [ ] `frontend/src/shared/format/dates.ts`, `money.ts`
- [ ] `frontend/src/shared/hooks/usePaginatedQuery.ts`
- [ ] `frontend/src/shared/auth/SessionProvider.tsx` — three states; registers the two `http.ts` handlers
- [ ] `frontend/src/shared/auth/useSession.ts`, `RequireSession.tsx`, `RequireRole.tsx`
- [ ] `frontend/src/shared/permissions/actions.ts` — 35 names, copied from §6.1, nothing added
- [ ] `frontend/src/shared/permissions/can.ts` — 35 rows with their role sets
- [ ] `frontend/src/shared/components/` — all ten: `AppShell`, `PageHeader`, `PaginatedTable`, `ConfirmDialog`, `StatusChip`, `ErrorBanner`, `EmptyState`, `LoadingRegion`, `NotFoundPage`, `AccessDeniedPage`
- [ ] `frontend/src/theme.ts`
- [ ] `frontend/src/routes.tsx` — all 21 rows of §4.1, `*` included
- [ ] `frontend/src/App.tsx`, `frontend/src/main.tsx`
- [ ] `frontend/src/slices/identity/types.ts`, `api.ts`, `queries.ts`
- [ ] `frontend/src/slices/identity/screens/LoginScreen.tsx`, `ChangePasswordScreen.tsx`, `ForgotPasswordScreen.tsx`, `ResetPasswordScreen.tsx`, `AcceptInvitationScreen.tsx`

**Not created by this plan**, each with an owner:

- [ ] `shared/dynamicForm/` — four files, owned by the **TicketTypes** UI plan. Do not stub it
- [ ] `slices/customers/`, `employees/`, `ticketTypes/`, `notifications/`, `audit/` — one plan each
- [ ] `slices/identity/screens/ProfileScreen.tsx`, `AccountantListScreen.tsx` — the Identity plan
- [ ] `Dockerfile`, `docker-compose.yml`, `Caddyfile` — punch-list item 2
- [ ] Any change to `AccountantApp.Api/`, including the three lines of item 1

### 9.2 Questions to flag if unclear

Flag these; do not answer them by inventing a behaviour.

- [ ] **What should an `Employee` actually see on landing?** Not the same question as *which route* — that is settled at `/profile` (§7.1 rule G; the two documents that disagreed were reconciled on 2026-09-02). This one is open: `/profile` is a placeholder chosen because an `Employee` has no dashboard until the `Tickets` UI ships, and it stays a placeholder until somebody designs the real one.
- [ ] **Which document owns `getSession`?** §1.4 rule A forbids `shared/` importing from `slices/`; `LoginArchitecture.md` §1.1 puts `getSession` in `slices/identity/api.ts` while §1.2 puts `SessionProvider` in `shared/auth/`. This plan resolves it in §4.1. Confirm or correct.
- [ ] **Which port is authoritative, 5131 or 5000?** Item 8 and §0.4. This plan uses 5131 because that is what the API binds, but a numbered document says otherwise.
- [ ] **Should the `mustChangePassword` `403` carry a machine-readable code?** Matching on an English sentence is fragile by construction (§3.1). Punch-list item 5.
- [ ] **Should `SessionDto` carry `loginEmail`?** Without it the change-password form cannot check the not-equal-login-email rule client-side, and every violation costs a round trip and a banner. Item 11.
- [ ] **Should `/api/auth/me` be exempt from Caddy's `/api/auth/*` rate limit?** Ten events per minute, and several tabs bootstrapping at once can plausibly reach it. The failure mode is the whole app refusing to start.
- [ ] **Is `App:BaseUrl = https://localhost:5173` intended in development?** The dev server serves plain `http` on 5173, so every emailed dev link has a scheme nothing listens on. Related: item 16, `App:BaseUrl` has no verification path at all.
- [ ] **How should a builder obtain a token in development?** §8.9 reads it out of `notification_outbox.email_body`, which works only because the drainer is unregistered when email is disabled. Is a documented dev path wanted instead?
- [ ] **Is `Documents` spec-only?** **No — this question is answered, and the answer is no.** `Slices/Documents/` on disk holds `Core/Document.cs`, `Core/DocumentContent.cs`, a `DocumentsDbContext`, two EF configurations, migration `20260903_001_CreateDocumentsSchema.sql`, `DocumentsRegistration.cs`, `Application/UploadValidation.cs` and seven files under `ExternalInterfaces/` including `IDocumentApi.cs` and `DocumentApi.cs`. It **is** registered in `Program.cs:59`, and four routes exist: `Slices/Tickets/TicketsEndpoints.cs:250` opens `MapGroup("/api/documents")` with `/upload` (`:252`), `/list` (`:312`), `/download` (`:322`) and `/delete` (`:356`). `Documents` has no endpoints **of its own** — `Tickets` registers and authorizes all four on its behalf, deliberately, because the reverse dependency would be a cycle — and that is a different statement from "no route exists". If you were told the slice is unbuilt or unregistered, that is stale. **Nothing in this phase changes either way:** there is no `Tickets` UI plan and no `UI/Screens/TicketsScreens.md`, so this phase adds no document screen and no client route to one.
- [ ] **Is a session-expiry warning wanted at all?** `LoginArchitecture.md` §7 rule D argues it cannot be built correctly against a sliding `HttpOnly` cookie. Confirm that none is expected.

### 9.3 Success criteria

Each is verified by running the app, not by reading the code. Phase 0 is finished when all twelve hold; no slice plan starts before then.

1. `npm run dev` serves the SPA on 5173, and a request to `/api/auth/me` from the browser reaches the API on **5131** through the proxy.
2. `fetch` appears in exactly one file — `src/shared/api/http.ts` — and there is no occurrence of `VITE_`, `import.meta.env`, or an `http://` literal in any API path under `src/`.
3. A visitor with no cookie sees the login form within one round trip, with **no** redirect loop and an empty browser console.
4. A hard refresh on an authenticated deep link returns to that link with **no** visible flash of the login form.
5. Signing in as the seeded admin lands on `/change-password`, with no shell and with a Logout button, and **no** other route is reachable until the password is changed — including by typing a URL.
6. After changing it, `/` redirects by role per §4.2, the nav shows exactly §5.2's items for that role, and the user is never asked again.
7. `/custmoers` renders `NotFoundPage` — not a blank screen — and `/audit` as an `AccountantUser` renders `AccessDeniedPage` with no *Audit log* nav item for that role.
8. A deliberately invalid change-password submission renders the server's `title` verbatim above the submit button, and every value the user typed is still in the form.
9. Deleting `aa_session` in devtools and then clicking anything produces exactly **one** redirect to `/login`, one message about the session ending, no retry storm and no toast.
10. Stopping the API mid-session renders "The server is unavailable" rather than a blank screen or an unhandled `SyntaxError`.
11. `localStorage` and `sessionStorage` are empty after a successful login, no request carries an `Authorization` header, and no role integer or raw status string is rendered anywhere.
12. `can.ts` matches **§6.1's table** exactly — its 35 names, same role sets, no extras on either side — it contains **none** of the 22 names in `Slices/Tickets/TicketsActionCatalogue.cs`, and `shared/dynamicForm/` does not exist. Two concrete checks, and run both: the `ACTIONS` keys diff clean against §6.1's 35 rows, in order and in both directions; and `grep -E 'Ticket|Document' can.ts` returns nothing but the five `TicketTypes` rows — `CreateTicketType`, `EditTicketType`, `ToggleTicketType`, `ReadTicketType`, `ListTicketTypes`. **Do not verify this by globbing `Slices/*/*ActionCatalogue.cs`: that finds seven files, not six, and 57 names, not 35.** The seventh is `Slices/Tickets/TicketsActionCatalogue.cs`, which landed after §6.1 was written and registers the eighteen ticket actions plus the four `Documents` actions on that slice's behalf. §6.1 — the union of the other six — is the right target, and the only one: no `Tickets` UI plan exists, this phase builds no `Tickets` screen, and a name with no screen behind it is a row nobody will ever check. A `CreateTicket` or an `UploadDocument` in `can.ts` is exactly the failure this criterion exists to catch, and it is quiet: `actions.ts` is the union type, so the extra name type-checks, `can()` answers `true` for it, and the first slice plan that reaches for it draws a live button against a screen and a client route that do not exist. The endpoints behind both names **do** exist — `Slices/Tickets/` registers them — which is exactly what makes the extra row quiet rather than loud. Seven catalogues on disk and 35 rows in §6.1 is the correct state; it is not a gap in §6.1.

Two things that are **not** failures and must not be treated as exit blockers:

- **`curl -i localhost:5131/` returns a `404`.** Punch-list item 1 is still open; the dev loop does not need it and Phase 0 does not fix it.
- **The reset and invitation screens have no end-to-end email path** (§8.9). Report them as verified by inspection, or by a token read out of the database, and say which.
