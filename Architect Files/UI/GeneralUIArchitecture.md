# General UI Architecture

This is the frontend counterpart to [App/GeneralAppArchitecture.md](../App/GeneralAppArchitecture.md).
It defines the structure every screen sits inside: where the code lives, how it talks to the API,
how it routes, how it renders errors, and which library does which job. Read it once, in full,
before writing any screen. Every per-screen spec under [Screens/](Screens/) assumes it and cites
it by section.

The React app is built **after** the API, against an API that already exists and will not be
reshaped to suit it. That asymmetry is the single most important thing to understand: where the
API's shape is inconvenient, the UI absorbs the inconvenience and the inconvenience is recorded
in [BACKEND_CHANGES_REQUIRED.md](BACKEND_CHANGES_REQUIRED.md). It is never fixed by guessing at
a different contract.

**Documents that govern this one, in precedence order.** Where any of them disagrees with this
document, **it wins and this document is wrong** — fix this document, do not code around it.

- [../README.md](../README.md) — *Locked platform decisions*, *Conflict precedence*
- [../00-Glossary.md](../00-Glossary.md) — the vocabulary, which is binding **in UI copy**
- [../01-DomainModel.md](../01-DomainModel.md) — entities and the ticket lifecycle
- [../02-AuthorizationMatrix.md](../02-AuthorizationMatrix.md) §1–12 — who may do what; normative
- [../04-Infrastructure.md](../04-Infrastructure.md) §1–3 — where the SPA is served from, and the dev loop
- [../App/GeneralAppArchitecture.md](../App/GeneralAppArchitecture.md) §8 — route shape, pagination, error contract

A screen spec under `Screens/` loses to this document.

---

## 0. Prerequisites — read before writing any code

### 0.1 What exists in the backend today, and what does not

**All eight** slices in [../03-SliceInventory.md](../03-SliceInventory.md) are built and wired in
`AccountantApp.Api/Program.cs` — `AddDocumentsSlice` at `:59` and `AddTicketsSlice` at `:65` are
the last two. None is specification only. What **six** of the eight have, and two do not, is a
**UI plan and a screen document**; that is a fact about this folder, not about the backend, and it
is the only reason two rows below say no screen can be built.

| Slice | State | HTTP surface | UI consequence |
|---|---|---|---|
| `Audit` | Built | `/api/audit/*`, 3 routes | Audit reader is buildable |
| `Notifications` | Built | `/api/notifications/*`, 4 routes | Notification centre is buildable |
| `Customers` | Built | `/api/customers/*`, 8 routes | Customer screens are buildable |
| `TicketTypes` | Built | `/api/ticket-types/*`, 6 routes | Type editor and the form renderer are buildable |
| `Identity` | Built | `/api/auth/*` 7, `/api/accountants/*` 6 | Login and accountant management are buildable |
| `Employees` | Built | `/api/employees/*` 12, plus `/api/customers/onboard` | Employee screens and onboarding are buildable |
| `Documents` | Built | none **of its own** — by design it never has any; `Tickets` registers its four routes | No upload, no download, anywhere |
| `Tickets` | Built | `/api/tickets/*`, 18 routes, **plus** `/api/documents/*`, 4 routes | **No ticket screen can be built** |

Two things follow, and both are load-bearing:

- **Tickets is the app's reason to exist and it has no UI yet.** The screens
  [../README.md](../README.md) calls "ticket inbox/queue", "ticket detail with assignment",
  "my tickets" and "new ticket" cannot be specified in this pass — not for want of endpoints, which
  exist and are routed, but because there is no `Screens/TicketsScreens.md` to specify them from,
  and a screen invented here would be a guess at behaviour rather than a transcription of it. They
  are listed as blocked in [README.md](README.md) and are not in this pass.
- **`TicketTypes` is still worth building fully now**, because it serves the *field schema* that
  the future ticket form will render. The dynamic form renderer specified in
  [Screens/TicketTypesScreens.md](Screens/TicketTypesScreens.md) is built now, in
  `shared/dynamicForm/`, and `Tickets` consumes it later.

### 0.2 The decisions already locked elsewhere — twelve of them

None of these is open. If you find yourself designing one, you are re-deciding something settled,
and the answer is in the *Where* column.

| # | Decision | Where | Why the UI may not revisit it |
|---|---|---|---|
| 1 | React SPA in TypeScript, source in `frontend/` at repo root | [../README.md](../README.md); [../04-Infrastructure.md](../04-Infrastructure.md) §3 | The production Dockerfile hard-codes `COPY frontend/package*.json` and `COPY --from=ui /ui/dist ./wwwroot`. Renaming the folder breaks the image build. |
| 2 | The SPA ships **inside the `app` container**, served from `wwwroot` | [../04-Infrastructure.md](../04-Infrastructure.md) §1 | Same origin is what lets `SameSite=Strict` work with no exceptions. |
| 3 | **CORS is never configured, in any environment** | [../README.md](../README.md); [../04-Infrastructure.md](../04-Infrastructure.md) §2 | The dev server proxies `/api`, so the browser sees one origin in dev too. A CORS header appearing anywhere means something else is already wrong. |
| 4 | **No API base-URL environment variable** | [../04-Infrastructure.md](../04-Infrastructure.md) §2 | *"A base-URL variable is how the same build ends up pointing at the wrong instance."* One build artifact must be correct everywhere. |
| 5 | Session is the `aa_session` cookie: `HttpOnly`, `Secure`, `SameSite=Strict`, 8h sliding | [../README.md](../README.md); `Slices/Identity/IdentityRegistration.cs` | `HttpOnly` means JavaScript **cannot read it**. There is nothing for the SPA to store, attach, or refresh. |
| 6 | **No JWT, no bearer token, nothing in `localStorage`** | [../README.md](../README.md) | There is no signing key in the system. A token store is not an optimisation here, it is a vulnerability with no counterpart on the server. |
| 7 | Every API route is under `/api`; everything else is the SPA | [../README.md](../README.md) | This is the whole reason the UI-in-API-container choice is cheap to reverse. |
| 8 | Route segments are lowercase **kebab-case**; ids go in the **body**, never the path | [../App/GeneralAppArchitecture.md](../App/GeneralAppArchitecture.md) §8 | `/api/tickettypes/list`'s doubled `t` is an invisible typo that reads as a missing row. |
| 9 | Errors are RFC7807 `ProblemDetails`; **`200` never carries an error** | [../App/GeneralAppArchitecture.md](../App/GeneralAppArchitecture.md) §8 | One parser handles every failure in the app. |
| 10 | Out-of-scope resources return **`404`, not `403`** | [../README.md](../README.md) | *"A `403` confirms the row exists."* |
| 11 | Authorization is **fail-closed and server-side** | [../02-AuthorizationMatrix.md](../02-AuthorizationMatrix.md) | See §6. The client's permission table decides which buttons to draw, and nothing else. |
| 12 | Pagination is `{ pageNumber, pageSize, totalCount, totalPages, items }`; default 15, max 50, **clamped not rejected** | [../App/GeneralAppArchitecture.md](../App/GeneralAppArchitecture.md) §8 | One table component serves every list in the app. |

### 0.3 Vocabulary is binding in UI copy

[../00-Glossary.md](../00-Glossary.md) is normative *"in code, in identifiers, and in UI copy"*.
The bans matter most where a UI writer's instinct fights them:

| Never write | Write instead | Why |
|---|---|---|
| "Client", "Clients" | **Customer** | Banned outright. "Client" is reserved for the React app in an HTTP sense. |
| "Admin" alone | **Accountant Admin** or **Customer Admin** | The word is ambiguous across two different roles with different powers. |
| "Firm", "Business", "Company" as a label | **Customer** | Same entity, one name. |
| "User" as a label | **Employee**, **Accountant**, or the person's name | "User" spans four roles and tells the reader nothing. |

A Customer is always a **company**, never a natural person
([../README.md](../README.md), *Customers are businesses*). Never label a Customer field
"First name".

---

## 1. Project layout

### 1.1 Where the SPA lives

`frontend/`, a sibling of `AccountantApp.Api/` at the repository root. This is not a preference —
[../04-Infrastructure.md](../04-Infrastructure.md) §3's Dockerfile names the path. There is no
`.csproj` for the SPA and no MSBuild integration: the Docker build runs `npm ci && npm run build`
in a `node:20-alpine` stage and copies `dist` into `wwwroot`.

### 1.2 The folder tree

Create exactly this. Deviating means a screen spec's `**File:**` paths do not resolve.

```
frontend/
  package.json
  tsconfig.json
  vite.config.ts
  index.html
  src/
    main.tsx                    # createRoot, providers, nothing else
    App.tsx                     # <RouterProvider>
    routes.tsx                  # the single route table (§4.1)
    theme.ts                    # the ONE MUI theme (§8.1)
    shared/
      api/
        http.ts                 # the only module in the app that calls fetch (§2.1)
        ApiError.ts             # the one error type every call throws (§2.2)
        problemDetails.ts       # tolerant ProblemDetails parsing (§2.2)
        paginated.ts            # PaginatedResponse<T> + PageRequest (§3.3)
        queryClient.ts          # TanStack Query configuration (§3.4)
      auth/
        SessionProvider.tsx     # bootstraps GET /api/auth/me
        useSession.ts
        RequireSession.tsx      # gate: authenticated
        RequireRole.tsx         # gate: role in a set
      permissions/
        actions.ts              # the action-name union, mirroring the server catalogues
        can.ts                  # can(role, action) -> boolean (§6.1)
      components/
        AppShell.tsx  PageHeader.tsx  PaginatedTable.tsx  ConfirmDialog.tsx
        StatusChip.tsx  ErrorBanner.tsx  EmptyState.tsx  LoadingRegion.tsx
        NotFoundPage.tsx  AccessDeniedPage.tsx
      dynamicForm/
        DynamicForm.tsx         # renders a FieldDescriptor[] (Screens/TicketTypesScreens.md)
        fieldRegistry.tsx       # dataType -> control, the 11-row switch
        buildZodSchema.ts       # FieldValidation -> Zod, at runtime
        visibility.ts           # conditionalVisibility evaluation
      hooks/
        usePaginatedQuery.ts
      format/
        dates.ts  money.ts  enums.ts
    slices/
      identity/       api.ts  types.ts  queries.ts  screens/  components/
      customers/      api.ts  types.ts  queries.ts  screens/  components/
      employees/      api.ts  types.ts  queries.ts  screens/  components/
      ticketTypes/    api.ts  types.ts  queries.ts  screens/  components/
      notifications/  api.ts  types.ts  queries.ts  screens/  components/
      audit/          api.ts  types.ts  queries.ts  screens/  components/
```

Folder names under `slices/` are `camelCase` renderings of the backend slice names
(`TicketTypes` → `ticketTypes`). One backend slice, one frontend folder, always.

### 1.3 Why the UI mirrors the backend's vertical slices

Because the whole specification is organised so that a builder reads **one** slice document and
touches **one** folder ([../README.md](../README.md):17). If the UI were grouped by technical
kind — `pages/`, `components/`, `hooks/`, `services/` — then implementing
[Screens/CustomersScreens.md](Screens/CustomersScreens.md) would mean editing four folders and
reviewing it would mean reading four folders, and nothing in the tree would tell you which of the
sixty components in `components/` belonged to Customers.

The second reason is that it makes a **cross-slice dependency visible**, exactly as
`ExternalInterfaces/` does on the server. An `import` reaching from `slices/employees/` into
`slices/customers/` is a line in a diff that a reviewer can see and question. The same coupling
expressed as two files sitting side by side in a flat `components/` folder is invisible.

### 1.4 What may import what

**A.** `shared/` may import from `shared/`. It may **never** import from `slices/`. A shared
component that knows about a slice is not shared; it has been misfiled, and it will drag that
slice into the bundle of every screen that uses it.

**B.** A slice may import freely from `shared/` and from itself.

**C.** A slice may import **only `types.ts` and `api.ts`** from another slice. Not its screens,
not its components, not its `queries.ts`. This is the client-side shape of the server's rule that
cross-slice calls go through `ExternalInterfaces` and nothing else
([../03-SliceInventory.md](../03-SliceInventory.md) §3).

**D.** There is one legitimate use of rule C today: `employees` needs the Customer name to render
an employee's employer, and `customers/types.ts` has it. If you find yourself needing a second
one, check whether the thing you are reaching for should have been in `shared/` all along.

**E.** `routes.tsx` is the one file that imports every slice's screens. That is its job.

### 1.5 Toolchain and the locked dependency list

| Concern | Package | Why this one |
|---|---|---|
| Build | `vite` | Already assumed by [../04-Infrastructure.md](../04-Infrastructure.md) §2's `vite.config.ts` proxy and §3's `npm run build` emitting `dist` |
| UI framework | `react`, `react-dom` | Locked in [../README.md](../README.md) |
| Language | `typescript` | Locked |
| Components | `@mui/material`, `@emotion/react`, `@emotion/styled`, `@mui/icons-material` | Locked. See §8 |
| Dates in inputs | `@mui/x-date-pickers`, `date-fns` | The form renderer needs `Date` and `DateRange` controls that respect `earliestDate`/`latestDate` |
| Server state | `@tanstack/react-query` | Locked. Every list in this API is server-paginated; hand-rolled caching gets invalidation wrong |
| Routing | `react-router-dom` | Locked |
| Forms | `react-hook-form`, `zod`, `@hookform/resolvers` | See §9.1 — the server returns no field-level errors, so client validation carries the whole burden |

**Adding a dependency.** Anything not in this table needs a line in §12 saying what it does and
what it replaced. Two things are banned outright: any HTTP client other than `fetch` behind
`shared/api/http.ts` (§2 exists precisely so there is one place cookies and errors are handled),
and any state-management library for **server** data (`redux`, `zustand`, `mobx`). React state
plus TanStack Query covers everything in this app; a third store is a third copy of the session.

`@mui/x-data-grid` is banned. See §8.2 for the reason.

---

## 2. The API client — write this once, use it everywhere

### 2.1 `http.ts` — the single request function

**File:** `frontend/src/shared/api/http.ts`

This is the **only** module in the application permitted to call `fetch`. Everything else calls
`get`/`post`. A `fetch` anywhere under `slices/` is a defect regardless of whether it works.

```ts
import { ApiError } from './ApiError';
import { parseProblemDetails } from './problemDetails';

// No base URL, and no environment variable that could become one. Every path is relative to the
// SPA's own origin, in every environment. In development the Vite proxy forwards /api to the API;
// in production the SPA and the API are the same origin. See 04-Infrastructure.md section 2.
async function request<T>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
  if (!path.startsWith('/api/')) {
    // A path that does not start with /api/ hits MapFallbackToFile and returns index.html with a
    // 200. Without this guard the caller gets HTML where it expected JSON and the parse failure
    // surfaces far away from the mistake.
    throw new Error(`API path must start with "/api/": ${path}`);
  }

  const response = await fetch(path, {
    method,
    // 'same-origin' is the default, but it is stated because 'omit' silently drops the session
    // cookie and 'include' implies a CORS request this app never makes.
    credentials: 'same-origin',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (!response.ok) {
    throw await parseProblemDetails(response);
  }

  // 204 never occurs in this API -- every mutation returns a body -- but a zero-length 200 would
  // make response.json() throw, so it is handled rather than assumed away.
  if (response.status === 204 || response.headers.get('content-length') === '0') {
    return undefined as T;
  }

  return (await response.json()) as T;
}

export const get = <T>(path: string): Promise<T> => request<T>('GET', path);
export const post = <T>(path: string, body?: unknown): Promise<T> => request<T>('POST', path, body);
export { ApiError };
```

`get` takes no second argument. Query strings are built by the caller in the slice's `api.ts`,
with `URLSearchParams`, so that the parameter names live next to the endpoint they belong to.

### 2.2 `ApiError` and tolerant `ProblemDetails` parsing

**File:** `frontend/src/shared/api/ApiError.ts`

```ts
export class ApiError extends Error {
  constructor(
    readonly status: number,
    /** The human-readable message. The API puts it in `title`, not `detail`. See rule F. */
    readonly title: string,
    /** Populated by exactly one response in the whole API: the must-change-password 403. */
    readonly detail: string | undefined,
    readonly traceId: string | undefined,
  ) {
    super(title);
    this.name = 'ApiError';
  }

  /** True for the forced-password-change gate. See LoginArchitecture.md section 3. */
  get isPasswordChangeRequired(): boolean {
    return this.status === 403 && this.detail !== undefined;
  }
}
```

**File:** `frontend/src/shared/api/problemDetails.ts`

```ts
import { ApiError } from './ApiError';

const fallbackTitle: Record<number, string> = {
  401: 'Your session has ended. Sign in again.',
  403: 'You do not have permission to do that.',
  404: 'Not found.',
  429: 'Too many attempts. Wait a moment and try again.',
  500: 'Something went wrong. Try again.',
  502: 'The server is unavailable. Try again shortly.',
  503: 'The server is unavailable. Try again shortly.',
};

export async function parseProblemDetails(response: Response): Promise<ApiError> {
  // The body is NOT always JSON. Caddy answers a rate-limited request itself, and a proxy error
  // is HTML. Calling response.json() unguarded turns "429 Too Many Requests" into an unhandled
  // SyntaxError, which reaches the user as a blank screen instead of "slow down".
  let title: string | undefined;
  let detail: string | undefined;
  let traceId: string | undefined;

  try {
    const body = (await response.json()) as {
      title?: string; detail?: string; traceId?: string;
    };
    title = body.title;
    detail = body.detail;
    traceId = body.traceId;
  } catch {
    // Left undefined on purpose; the fallback below covers it.
  }

  return new ApiError(
    response.status,
    title ?? fallbackTitle[response.status] ?? 'Something went wrong. Try again.',
    detail,
    traceId,
  );
}
```

> **The `Content-Type` is `application/json`, not `application/problem+json`.** The API
> serialises `ProblemDetails` with `WriteAsJsonAsync`
> (`Shared/Errors/AppExceptionMiddleware.cs`), which does not set the RFC7807 media type. Do not
> branch on `Content-Type` to decide whether a body is a problem document — branch on
> `response.ok`, and let the parse fail tolerantly.

### 2.3 Rules that apply to every API call

**A. Every path is a relative string beginning `/api/`.** No base URL, no `VITE_API_URL`, no
`import.meta.env` lookup that could become one. `04-Infrastructure.md` §2 forbids it by name:
*"A base-URL variable is how the same build ends up pointing at the wrong instance."*

**B. `credentials: 'same-origin'`.** Never `'omit'`, which drops the session cookie and turns
every authenticated call into a 401 that looks like an expired session. Never `'include'`, which
declares a cross-origin request in an application that has no CORS configuration and never will.

**C. Use the verb the backend actually uses, even when it looks wrong.** Most reads are `GET`,
but **exactly five of the reads this SPA calls are `POST`** — they take a filter or id object too
large or too structured for a query string. Five, not nine, is a fact about *this SPA*, not about
the API: `/api/tickets/list`, `/api/tickets/get`, `/api/tickets/pickup-queue` and
`/api/documents/list` are `POST` reads that exist and are routed
(`Slices/Tickets/TicketsEndpoints.cs:60`, `:68`, `:84`, `:312`), and they are absent from the
table below because no screen in this specification calls them, not because they are missing:

| POST read | Why |
|---|---|
| `/api/customers/list` | filter object |
| `/api/employees/list` | filter object |
| `/api/employees/get` | id in the body, like every other id in this API |
| `/api/notifications/list` | filter object |
| `/api/audit/search` | eight filters |

Changing one to `GET` produces a `405`. Do not "correct" them.

**The `list` suffix does not predict the verb**, and this is the trap: `/api/customers/list`
and `/api/employees/list` are `POST`, while `/api/ticket-types/list` and `/api/accountants/list`
are `GET` with `?pageNumber&pageSize`. Nor does `get`/`detail`: `/api/employees/get` is `POST`,
`/api/customers/detail` and `/api/ticket-types/detail` are `GET`. Read the verb off the endpoint
file for the route you are calling; do not infer it from the route's last segment.

**D. Ids travel in the body, never in the path.** There is no route parameter anywhere in this
API — `POST /api/employees/get` with `{ employeeId }`, not `GET /api/employees/{id}`. This is
locked in `App/GeneralAppArchitecture.md` §8. Note the asymmetry with §4: **SPA** routes *do* use
path parameters, because a URL a user can bookmark needs the id in it. `/employees/:employeeId`
in the browser becomes `{ employeeId }` in a POST body.

**E. A non-2xx response throws.** Callers never read `response.ok` and never receive a
`{ data, error }` pair. TanStack Query's `isError`/`error` is the single error channel.

**F. The human-readable message is `title`.** The whole error body is
`{ status, title, traceId }`. There is no `errors{}` dictionary, no error code, and `detail` is
populated by exactly one response in the entire API. Reading `detail` for the message yields
`undefined` on every failure except that one.

**G. Never assume the response body is JSON on a failure.** See §2.2.

**H. `401` from any call means the session is gone.** Clear the session and redirect to `/login`.
Never retry it, never show it as a toast, never render it inside a form. The backend overrides
`OnRedirectToLogin` to return a bare `401` rather than a `302`
(`Slices/Identity/IdentityRegistration.cs`), precisely so the SPA can treat it as data.

**I. A `403` carrying a `detail` is the forced-password-change gate, not a permission failure.**
Route to `/change-password`. It is a *state* the account is in, not an error the user made. See
[LoginArchitecture.md](LoginArchitecture.md) §3.

**J. `404` means "not found **or** not visible to you".** The backend deliberately returns `404`
for out-of-scope rows, because *"a `403` confirms the row exists"*
([../README.md](../README.md)). Never render the words "forbidden", "denied", or "no permission"
for a `404`. "Not found" is the only honest wording, and it is honest in both cases.

### 2.4 Six ways the API client goes wrong

1. **An absolute base URL.** `http://localhost:5131/api/...` works on the developer's machine,
   ships, and breaks in production where nothing is listening on 5131. The `startsWith('/api/')`
   guard in §2.1 does not catch this; code review is what catches it. Banned by rule A.
2. **`credentials: 'include'`.** Looks more thorough than `'same-origin'`, and drags in a CORS
   preflight against a server with no CORS configuration. The failure is an opaque network error
   with nothing in the response to read.
3. **Reading `detail` for the error message.** Every message is in `title`. This bug shows up as
   *every* error rendering as an empty string — except the password-change 403, which is the one
   response that would make you think you had it right.
4. **`await response.json()` on an error body.** Caddy's rate limiter returns its own non-JSON
   `429`. The unhandled `SyntaxError` replaces "Too many attempts" with a blank screen, on the
   login form, which is the worst possible place for it.
5. **Treating `404` as a bug.** It is the scoping mechanism. An Employee requesting a colleague's
   record gets `404` by design (`Slices/Employees/EmployeesEndpoints.cs:62-64`). Logging it as an
   error, retrying it, or reporting "something went wrong" all misread the contract.
6. **Assuming the server honoured your `pageSize`.** `PaginatedQuery.Normalize` **clamps to 50
   and does not reject** — ask for 200 and you get 50 with a `200 OK`. Always render from the
   `pageSize` in the *response*, never from the value you sent, or the pager computes the wrong
   number of pages and rows go missing with no error anywhere.

### 2.5 Per-slice `api.ts` modules

**File:** `frontend/src/slices/<slice>/api.ts`

One exported function per endpoint. Named for the endpoint, not for the screen that calls it —
the screen may change, the endpoint is the contract. Query strings are built here.

```ts
import { get, post } from '../../shared/api/http';
import type { PaginatedResponse } from '../../shared/api/paginated';
import type { TicketTypeDetail, TicketTypeListItem, CreateTicketTypeRequest } from './types';

export function listTicketTypes(
  params: { pageNumber: number; pageSize: number; activeOnly?: boolean },
): Promise<PaginatedResponse<TicketTypeListItem>> {
  const query = new URLSearchParams({
    pageNumber: String(params.pageNumber),
    pageSize: String(params.pageSize),
  });
  if (params.activeOnly !== undefined) query.set('activeOnly', String(params.activeOnly));
  return get(`/api/ticket-types/list?${query}`);
}

export const getTicketType = (ticketTypeId: string): Promise<TicketTypeDetail> =>
  get(`/api/ticket-types/detail?ticketTypeId=${encodeURIComponent(ticketTypeId)}`);

export const createTicketType = (body: CreateTicketTypeRequest): Promise<TicketTypeDetail> =>
  post('/api/ticket-types/create', body);
```

`api.ts` contains **no** React, no hooks, no TanStack Query. It is a plain typed wrapper over
HTTP, so it can be read against the C# endpoint file line by line. Hooks live in `queries.ts`
(§3).

**File:** `frontend/src/slices/<slice>/types.ts`

Hand-written interfaces mirroring the C# DTOs, with camelCase names. Each type carries a comment
naming the C# file it mirrors, so the next person can diff them:

```ts
/** Mirrors Slices/TicketTypes/ExternalInterfaces/TicketTypeDetailDto.cs */
export interface TicketTypeDetail { /* ... */ }
```

### 2.6 Why there is no generated client

There is no OpenAPI document. The API registers no `AddOpenApi()` and references no Swashbuckle
package, though its endpoints already carry accurate `.WithName`, `.WithTags` and `.Produces<T>`
metadata. Hand-written `types.ts` files are therefore the only option today, and they are the
largest source of silent drift in this codebase: a renamed C# property is a `undefined` in the
UI with no compiler error, because the JSON boundary is untyped.

Generating the client is item 9 in [BACKEND_CHANGES_REQUIRED.md](BACKEND_CHANGES_REQUIRED.md),
and item 6 there is the prerequisite: two routes declare a shape they do not always return, so
generating today would generate those two wrongly.
When it lands, `types.ts` is deleted in favour of generated types and `api.ts` stays — the
hand-written wrapper is what keeps rules A–D in one place, and a generated client would need them
re-applied anyway. **Do not** wait for it, and do not build a code generator as part of this work.

Two `.Produces<T>` declarations are already known to be wrong and must not be trusted:
`/api/accountants/list` declares `PaginatedResponse<AccountantDetailDto>` but returns the
*Summary* shape to an `AccountantUser`, and `/api/notifications/list` declares
`.Produces<object>`. Both are recorded in the punch-list.

---

## 3. Server state — TanStack Query

Every byte of data from the API is server state and belongs in TanStack Query. React state is for
things the server has never heard of: whether a dialog is open, what is typed in a field, which
tab is selected.

### 3.1 Query key convention

`[sliceName, resource, ...discriminators]`, always an array, always starting with the slice name.

| Query | Key |
|---|---|
| Ticket type list, page 2, active only | `['ticketTypes', 'list', { pageNumber: 2, pageSize: 15, activeOnly: true }]` |
| One ticket type | `['ticketTypes', 'detail', ticketTypeId]` |
| One historical version | `['ticketTypes', 'version', ticketTypeId, versionNumber]` |
| The session | `['identity', 'session']` |
| Unread count | `['notifications', 'unreadCount']` |

The slice prefix is what makes `invalidateQueries({ queryKey: ['ticketTypes'] })` a correct,
readable blast radius. Every filter that changes the response must appear in the key, or two
different filters share one cache entry and the screen shows the wrong rows.

### 3.2 Rules for every query and mutation

**A. One `queries.ts` per slice**, exporting hooks named `useXxx`. Screens import hooks; screens
never import `api.ts` directly. This keeps caching decisions out of JSX.

**B. Never disable a query to express "not allowed".** Permission gating decides what to
*render* (§6); it does not silently skip fetches. If a screen should not be reachable, gate the
route (§4.3). `enabled` is for genuine data dependencies — "do not fetch the detail until the id
is known".

**C. Every mutation states its invalidations explicitly.** No global `invalidateQueries()` with
no key. A create or edit invalidates its slice's list keys and seeds its detail key (rule D).

**D. Seed the cache from the mutation response; do not refetch.** Every mutating endpoint in
this API returns the full updated detail DTO — deliberately, so there is no second round trip:

```ts
const mutation = useMutation({
  mutationFn: editTicketType,
  onSuccess: (updated) => {
    queryClient.setQueryData(['ticketTypes', 'detail', updated.id], updated);
    queryClient.invalidateQueries({ queryKey: ['ticketTypes', 'list'] });
  },
});
```

Refetching the detail instead throws away a response you already have and introduces a window
where the screen shows stale data after a successful save.

**E. No optimistic updates.** Not anywhere, and not as an improvement later. See §9.4: the
backend has no concurrency token, so the client cannot know whether its optimistic guess matches
what was written. An optimistic update here is a UI that confidently displays a value the
database does not hold.

**F. `retry: false` for every 4xx.** See §3.4.

**G. Paginated lists use `usePaginatedQuery`** (§3.3) and nothing else, so the clamping trap
(§2.4 item 6) is handled in one place.

**H. The unread-count query is the only polling query in the app.** `refetchInterval` appears
exactly once, in `notifications/queries.ts`. Polling anything else is a change to this document,
not a local decision.

### 3.3 Pagination

**File:** `frontend/src/shared/api/paginated.ts`

```ts
/** Mirrors Shared/Pagination/PaginatedResponse.cs. Identical for every list in the API. */
export interface PaginatedResponse<T> {
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  items: T[];
}

/** Mirrors Shared/Pagination/PaginatedQuery.cs. */
export const DEFAULT_PAGE_SIZE = 15;
export const MAX_PAGE_SIZE = 50;
```

Three behaviours of the server contract that the UI must respect rather than defend against:

1. **`pageSize` is clamped, not rejected.** `Math.Clamp(pageSize, 1, 50)`. Never offer a page-size
   option above 50, and always render the pager from `response.pageSize`.
2. **`pageNumber` is clamped upward to 1**, and a page past the end returns
   `items: []` with a `200`, not a `404`. So "no rows" is ambiguous — it means *either* an empty
   result *or* an over-run page. `EmptyState` should offer "back to the first page" when
   `totalCount > 0 && items.length === 0`.
3. **Pages are 1-based.** MUI's `TablePagination` is 0-based. Convert in exactly one place —
   `PaginatedTable` (§8.3) — because an off-by-one here silently hides the first or last row of
   every list in the application.

### 3.4 Retry policy

**File:** `frontend/src/shared/api/queryClient.ts`

```ts
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // A 4xx is an answer, not a failure to get one. Retrying a 403 asks the server to deny you
      // three times and audits three denials -- PermissionChecker writes an audit row for every
      // one. Retrying a 401 delays the redirect to /login by several seconds for no gain.
      retry: (failureCount, error) =>
        error instanceof ApiError && error.status >= 500 && failureCount < 2,
      staleTime: 30_000,
      refetchOnWindowFocus: false,
    },
    mutations: { retry: false },
  },
});
```

Retrying a mutation is never right here: no endpoint in this API is idempotent, and there is no
idempotency key. A retried `POST /api/employees/register` creates a second Employee.

---

## 4. Routing

### 4.1 The route table

**File:** `frontend/src/routes.tsx`

`Roles` uses the abbreviations from [../02-AuthorizationMatrix.md](../02-AuthorizationMatrix.md):
AA = `AccountantAdmin`, AU = `AccountantUser`, CA = `CustomerAdmin`, EMP = `Employee`.

| Path | Screen | Shell | Roles |
|---|---|---|---|
| `/login` | `LoginScreen` | no | anonymous |
| `/forgot-password` | `ForgotPasswordScreen` | no | anonymous |
| `/reset-password` | `ResetPasswordScreen` | no | anonymous (token in query) |
| `/accept-invitation` | `AcceptInvitationScreen` | no | anonymous (token in query) |
| `/change-password` | `ChangePasswordScreen` | no | any authenticated |
| `/` | role redirect (§4.2) | yes | any |
| `/customers` | `CustomerListScreen` | yes | AA, AU |
| `/customers/new` | `OnboardCustomerScreen` | yes | AA |
| `/customers/:customerId` | `CustomerDetailScreen` | yes | AA, AU |
| `/my-customer` | `OwnCustomerScreen` | yes | CA, EMP |
| `/employees` | `EmployeeListScreen` | yes | AA, AU, CA |
| `/employees/:employeeId` | `EmployeeDetailScreen` | yes | AA, AU, CA, EMP |
| `/ticket-types` | `TicketTypeListScreen` | yes | AA, AU, CA, EMP |
| `/ticket-types/new` | `TicketTypeEditorScreen` | yes | AA, AU |
| `/ticket-types/:ticketTypeId` | `TicketTypeDetailScreen` | yes | AA, AU, CA, EMP |
| `/ticket-types/:ticketTypeId/edit` | `TicketTypeEditorScreen` | yes | AA, AU |
| `/accountants` | `AccountantListScreen` | yes | AA, AU |
| `/notifications` | `NotificationCentreScreen` | yes | AA, AU, CA, EMP |
| `/audit` | `AuditSearchScreen` | yes | AA |
| `/audit/:auditEntryId` | `AuditEntryScreen` | yes | AA |
| `/profile` | `ProfileScreen` | yes | AA, AU, CA, EMP |
| `*` | `NotFoundPage` | yes | any |

Paths are kebab-case, matching the API's convention, so `/ticket-types` and not `/tickettypes`
or `/ticketTypes`.

**SPA routes take path parameters. API routes do not.** `/employees/:employeeId` in the browser
becomes `POST /api/employees/get` with `{ employeeId }` in the body. This is not an
inconsistency to fix: a URL the user bookmarks needs the id in it, and an API that never puts ids
in paths never has a route-vs-body ambiguity. See §2.3 rule D.

### 4.2 Public routes, shell routes, and `/`

The five routes marked *shell: no* render standalone, centred, with no navigation. Someone at
`/login` has no session and therefore no role, so there is nothing to draw a nav bar from;
someone at `/change-password` has a session that the server will reject on every other route
(§2.3 rule I), so offering them navigation offers them ten links that all 403.

`/` renders no content of its own. It redirects by role:

| Role | Redirects to | Why |
|---|---|---|
| AA, AU | `/customers` | The Accountant's working list. (It becomes the ticket queue when the `Tickets` UI ships.) |
| CA | `/employees` | The Customer Admin's working list. |
| EMP | `/profile` | **An acknowledged placeholder, not a home screen.** [LoginArchitecture.md](LoginArchitecture.md) §2.6 and [../README.md](../README.md) both record this: an `Employee` has no dashboard until the `Tickets` UI ships, and `/profile` is the honest landing rather than one invented to be deleted. `/ticket-types` is reachable by an `Employee` (§4.1) but is a catalogue of forms they cannot submit yet, which reads as a broken home. Do not "improve" this to `/ticket-types` — the three documents agree on `/profile` and disagreeing with them silently is the trap. |

These three destinations are provisional and change when the `Tickets` UI ships. That is expected and
recorded in §12.

### 4.3 `RequireSession` and `RequireRole`

Two components, each doing one thing.

**File:** `frontend/src/shared/auth/RequireSession.tsx` — while the session is loading, render
`LoadingRegion`; if anonymous, `<Navigate to="/login" state={{ from: location }} replace />`; if
`mustChangePassword`, `<Navigate to="/change-password" replace />`; otherwise render children.

**File:** `frontend/src/shared/auth/RequireRole.tsx` — takes `roles: UserRole[]`; renders
`AccessDeniedPage` when the session's role is not in the set.

Three rules:

**A. `RequireRole` renders a denial page; it does not redirect.** A user who typed `/audit`
deserves to be told that page is not for them. A silent bounce to `/customers` reads as a broken
link, and they will try again.

**B. `RequireRole` is not a security boundary.** It is the same affordance logic as §6, applied
to a whole page. The server denies the underlying calls with `403` and audits every denial
regardless of what the router did. See §6.2.

**C. `RequireSession` wraps the shell once**, in the route table, not once per screen. A screen
that checks for a session itself is a screen that can be mounted without one.

### 4.4 The catch-all `404` route is mandatory

`MapFallbackToFile("index.html")` means **every** path outside `/api` returns the SPA with a
`200` ([../04-Infrastructure.md](../04-Infrastructure.md) §1). The server cannot tell
`/customers` from `/custmoers`; both load the app. If `routes.tsx` has no `*` route, a typo'd URL
renders a blank page with no error, in the browser and in the logs.

Two checks are named in `04-Infrastructure.md` §1 and both must be verified once the hosting lines
exist: `GET /customers/123` returns HTML, and `GET /api/nonexistent` returns a `404`
`ProblemDetails` — **not** `index.html`.

> **The three hosting lines are not in `Program.cs` yet.** `UseDefaultFiles()`,
> `UseStaticFiles()` and `MapFallbackToFile("index.html")` are specified as LOCKED in
> `04-Infrastructure.md` §1 and absent from the code, so `GET /` currently returns a
> `ProblemDetails` `404`. Until they are added the SPA runs only under `npm run dev`. This is
> item 1 in [BACKEND_CHANGES_REQUIRED.md](BACKEND_CHANGES_REQUIRED.md).

### 4.5 Deep-link routes the backend's emails depend on

**File:** `AccountantApp.Api/Slices/Identity/Application/TokenLinks.cs` builds two URLs and mails
them:

```csharp
public string AcceptInvitation(string rawToken) => $"{_baseUrl}/accept-invitation?token=...";
public string CompletePasswordReset(string rawToken) => $"{_baseUrl}/reset-password?token=...";
```

`/accept-invitation` and `/reset-password` are therefore **contract**, not UI choices. Renaming
either breaks every invitation and reset link already sitting in somebody's inbox. Note that the
SPA route `/reset-password` and the API route `/api/auth/complete-password-reset` have different
names on purpose; see [LoginArchitecture.md](LoginArchitecture.md) §4.3.

`_baseUrl` comes from the `App__BaseUrl` environment variable. If it is wrong, every emailed link
points at the wrong host — which is a deployment fault the UI cannot detect or work around.

---

## 5. The application shell

### 5.1 Layout

**File:** `frontend/src/shared/components/AppShell.tsx`

```
┌──────────────────────────────────────────────────────────────────────┐
│ AccountantApp        [ Customers  Employees  Ticket types  Audit ]   │  AppBar
│                                          [bell 3]  Jane Doe (AA) ▾   │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│   Customers                                          [ Add Customer ]│  PageHeader
│   ──────────────────────────────────────────────────────────────────  │
│                                                                      │
│   <Outlet />                                                         │  content
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
```

One horizontal `AppBar`, no sidebar. There are seven nav destinations at most and four for any
single role; a collapsible drawer for four links is machinery with nothing to manage. On small
screens the nav collapses into a `Menu` behind an icon button.

The account menu shows the display name and the role, and offers *Profile* and *Sign out*. The
role is shown because two people at the same Customer can see different buttons, and "why can she
suspend and I cannot" is otherwise unanswerable from the screen.

### 5.2 Navigation items by role

| Item | Route | AA | AU | CA | EMP |
|---|---|:--:|:--:|:--:|:--:|
| Customers | `/customers` | yes | yes | — | — |
| My Customer | `/my-customer` | — | — | yes | yes |
| Employees | `/employees` | yes | yes | yes | — |
| Ticket types | `/ticket-types` | yes | yes | yes | yes |
| Accountants | `/accountants` | yes | yes | — | — |
| Audit log | `/audit` | yes | — | — | — |
| Notifications | `/notifications` | yes | yes | yes | yes |

The nav is derived from this table and the session's role. It is **not** derived from `can()`:
a nav item maps to a page, not to an action, and several pages combine actions with different
role sets. `/employees` is visible to a `CustomerAdmin` who may list employees but may not
onboard a Customer.

An `Employee` gets three items. That is correct and not a bug to pad out — see §12.

### 5.3 What the shell does NOT do, and why

- **No global loading spinner.** A single top-level spinner means every navigation blanks the
  whole page, including the nav the user was about to click again. Loading is rendered inside the
  region that is loading, by `LoadingRegion` or by a `Skeleton` in the table body.
- **No global error toast for query failures.** An error belongs next to the thing that failed:
  a list error replaces the table, a form error sits above the submit button. A toast is
  dismissible, unlocatable, and gone before the user has read it. Toasts are for *successes*
  ("Customer suspended"), where there is nothing on screen to attach the message to.
- **No breadcrumbs.** The route hierarchy is two levels deep at most.
- **No client-side search across slices.** Every list is server-paginated; a global search box
  would need a server endpoint that does not exist.
- **No permission fetching.** See §6.3.
- **No session polling.** The shell does not check `/api/auth/me` on a timer. Expiry is detected
  the same way as any other failure: the next call returns `401` and §2.3 rule H fires. Polling
  to detect expiry earlier would *prevent* the expiry it was watching for, because the cookie
  slides on every request.

---

## 6. Permissions in the client

### 6.1 `can()` and the action table

**File:** `frontend/src/shared/permissions/can.ts`

This table mirrors the **six** `{Slice}ActionCatalogue.cs` files whose slices have a UI plan, which
are the server's authority for the screens this specification describes. There are **seven**
catalogues on disk and **57** names in them; the seventh is
`Slices/Tickets/TicketsActionCatalogue.cs` (22 names — the eighteen ticket actions plus the four
`Documents` actions it registers on that slice's behalf), and it is excluded on purpose because
there is no `Tickets` UI plan and no screen behind any of those names. The table is duplicated
here, by hand, and the duplication is deliberate — see §6.3.

```ts
/**
 * Mirrors the union of the SIX AccountantApp.Api/Slices/*ActionCatalogue.cs files whose slices
 * have a UI plan: Audit, Customers, Employees, Identity, Notifications, TicketTypes.
 *
 * Do not re-derive this from the glob. AccountantApp.Api/Slices/*ActionCatalogue.cs now resolves
 * to SEVEN files and 57 names; the seventh is Slices/Tickets/TicketsActionCatalogue.cs and its
 * 22 names must not appear here.
 *
 * Governed by 02-AuthorizationMatrix.md. When an action is added on the server, add it here in
 * the same commit; a missing row denies (see can()), so the UI hides a button the user is
 * entitled to -- annoying, and much safer than the reverse.
 */
const ACTIONS: Record<ActionName, UserRole[]> = { /* the table below */ };

export function can(role: UserRole | undefined, action: ActionName): boolean {
  if (role === undefined) return false;          // no session: nothing is permitted
  return ACTIONS[action]?.includes(role) ?? false; // unknown action: deny, matching the server
}
```

| Action | Slice | AA | AU | CA | EMP |
|---|---|:--:|:--:|:--:|:--:|
| `ReadAuditLog` | Audit | yes | — | — | — |
| `CreateCustomer` | Customers | yes | — | — | — |
| `SuspendCustomer` | Customers | yes | — | — | — |
| `ReactivateCustomer` | Customers | yes | — | — | — |
| `ListCustomers` | Customers | yes | yes | — | — |
| `EditCustomerLegal` | Customers | yes | yes | — | — |
| `EditCustomerContact` | Customers | yes | yes | yes | — |
| `ViewCustomer` | Customers | yes | yes | yes | — |
| `ViewOwnCustomer` | Customers | — | — | yes | yes |
| `OnboardCustomer` | Employees | yes | — | — | — |
| `RegisterEmployee` | Employees | yes | yes | yes | — |
| `ListEmployees` | Employees | yes | yes | yes | — |
| `ViewEmployee` | Employees | yes | yes | yes | yes |
| `UpdateEmployee` | Employees | yes | yes | yes | — |
| `UpdateOwnContact` | Employees | — | — | yes | yes |
| `InviteEmployee` | Employees | yes | yes | yes | — |
| `SetEmployeeRole` | Employees | yes | yes | yes | — |
| `DepartEmployee` | Employees | yes | yes | yes | — |
| `ReinstateEmployee` | Employees | yes | yes | yes | — |
| `ChangeEmployeeLoginEmail` | Employees | yes | yes | **—** | — |
| `SuspendEmployeeAccount` | Employees | yes | yes | yes | — |
| `ReactivateEmployeeAccount` | Employees | yes | yes | yes | — |
| `ListAccountants` | Identity | yes | yes | — | — |
| `InviteAccountant` | Identity | yes | — | — | — |
| `SuspendAccountant` | Identity | yes | — | — | — |
| `ReactivateAccountant` | Identity | yes | — | — | — |
| `PromoteAccountant` | Identity | yes | — | — | — |
| `DemoteAccountant` | Identity | yes | — | — | — |
| `ReadOwnNotifications` | Notifications | yes | yes | yes | yes |
| `MarkOwnNotificationRead` | Notifications | yes | yes | yes | yes |
| `CreateTicketType` | TicketTypes | yes | yes | — | — |
| `EditTicketType` | TicketTypes | yes | yes | — | — |
| `ToggleTicketType` | TicketTypes | yes | yes | — | — |
| `ReadTicketType` | TicketTypes | yes | yes | yes | yes |
| `ListTicketTypes` | TicketTypes | yes | yes | yes | yes |

Login, logout, `/api/auth/me` and change-password are deliberately **not** actions. Each is
available to every authenticated caller or to nobody, and `IdentityActionCatalogue.cs` says so:
*"An entry listing all four roles would imply a role decision where there is not one, and would
be a check that can only ever pass."*

**Thirty-five rows, and the last two were a gap that has now closed.** The table above is the exact
union of the six `{Slice}ActionCatalogue.cs` files whose slices have a UI plan — verified row by
row, and it matches: `Audit` (1), `Customers` (8), `Employees` (13), `Identity` (6),
`Notifications` (2), `TicketTypes` (5). **Thirty-five is the number to satisfy, and 35 rows against
seven catalogues on disk is the correct state, not a gap in this table.** A glob of
`Slices/*/*ActionCatalogue.cs` finds seven files and 57 names, and a builder who diffs this table
against that glob will conclude 22 names are missing. **They are not missing.** Do not add rows.

`ReinstateEmployee` and `ChangeEmployeeLoginEmail` were, until 2026-09-02, granted by
`02-AuthorizationMatrix.md` §4 and present in **no** catalogue, so their two registered endpoints —
`POST /api/employees/reinstate` and `POST /api/employees/change-login-email` — returned `403` to
every caller including an `AccountantAdmin`, because `PermissionChecker` is fail-closed on an
unrecognised action name. `EmployeesActionCatalogue.cs` now declares both, so the rows above are
live and punch-list item 26 is resolved.

**Note the one asymmetry in those two rows, and do not tidy it:** `ReinstateEmployee` includes
`CustomerAdmin` and `ChangeEmployeeLoginEmail` does not. Matrix §4 gives the reasons — a Customer
Admin who can enter a departure must be able to correct one, and *"changing a login email is
reserved to the Office, and nobody may change their own"*. `can(role,'ChangeEmployeeLoginEmail')`
is therefore `false` for a Customer Admin **by design**; it is not a missing row.

The catalogue's `CustomerAdmin` grant on reinstate is *"Yes, own Customer"*, and no catalogue entry
anywhere can express that scope. Row-level scoping stays in the handler and surfaces as a `404`
(rule A below). A `can()` of `true` never means "this particular record".

### 6.2 Rules

**A. `can()` decides affordances, never data.**
[../02-AuthorizationMatrix.md](../02-AuthorizationMatrix.md):311 —
*"Never rely on the React app to hide data. Internal Notes, Accountant-only fields, and
out-of-scope records must be **absent from the API response**, not merely unrendered."* If the
UI is filtering rows or fields for security, the server has already leaked them and the UI is
concealing a live bug.

**B. `can()` returning `true` followed by a `403` is a bug in this table, not on the server.**
The server is fail-closed and audits every denial. Fix the row; do not add a `try`/`catch` that
swallows the 403.

**C. Prefer hiding to disabling, with one exception.** A button a user can never enable is
noise. But [../02-AuthorizationMatrix.md](../02-AuthorizationMatrix.md):146 names a case that
must stay visible rather than greyed out; honour it where the matrix names it, and hide
otherwise.

**D. The table expresses *who may call*, not *which rows*.** Several actions are really "yes,
for their own Customer" — `EmployeesActionCatalogue.cs` says so in a comment. `can()` cannot
answer "may I edit *this* employee". Row-level scoping is the server's, enforced by
`CustomerScope`, and surfaces to the UI as a `404`.

**E. Never persist or cache a permission decision.** `can()` is a pure function of the session
role. Recompute it; it costs nothing.

### 6.3 Why the permission table is not fetched from the server

There is no endpoint that exposes it. `IActionCatalogue` is internal to the API, composed at
startup by `PermissionChecker`, and nothing maps it to a route. Adding one would be a small
change — and it is deliberately not requested, for two reasons.

First, the table would be **advisory anyway**. Rule D means the server's answer to "may I edit
this employee" depends on rows the client cannot see, so a fetched table would still be an
approximation and would still be wrong in the same cases. It would just feel authoritative while
being wrong, which is worse than a local table that is obviously local.

Second, a fetched table adds a request to the critical path of every page load to decide which
buttons to draw — and if it fails, the app must either render no buttons or render all of them.
Both are worse than a hardcoded table that is occasionally stale.

The mitigation is the discipline in §6.1's comment: the row is added in the same commit as the
server action, and the verification step in the plan diffs this table against the catalogue files.

---

## 7. Errors, loading and empty states

### 7.1 The error taxonomy

Every status this API produces, and the one correct treatment for each.

| Status | The server means | The user sees | Rendered as |
|---|---|---|---|
| `400` | Malformed body, unparseable query param, or an invalid/expired token | The `title` | Form banner, or page message on a token screen |
| `401` | No session, expired session, bad credentials, or a non-`Active` account | Nothing — they are moved | Redirect to `/login`. On the login form itself, a form banner |
| `403` **with** `detail` | Password change required | "You must change your password before continuing." | Redirect to `/change-password` |
| `403` without `detail` | Permission denied; the denial is audited | "You do not have permission to do that." | `AccessDeniedPage` for a page, banner for an action |
| `404` | Not found **or** out of scope | "Not found." | `NotFoundPage`, or an `EmptyState` inside a list |
| `409` | Duplicate code or email, or a concurrent edit | The `title`, plus "Reload and try again" | Form banner with a reload affordance |
| `422` | A business rule refused the request | The `title` | **Form-level** banner. See §7.3 |
| `429` | Caddy's rate limiter, on `/api/auth/*` | "Too many attempts. Wait a moment and try again." | Form banner. Body is **not** JSON (§2.2) |
| `500` | Unexpected; already logged server-side | Generic message plus the `traceId` | Page or form banner, `traceId` in small text |
| `502`/`503` | The `app` container is down or restarting | "The server is unavailable." | Page banner |

Show the `traceId` on `500` and nowhere else. It is the only handle support has on a server-side
log entry, and printing it on a `422` — where the `title` already says exactly what is wrong —
teaches users to ignore it.

### 7.2 Where each error is rendered

| Situation | Component | Placement |
|---|---|---|
| A list query failed | `ErrorBanner` | Replaces the table body; the page header and nav stay |
| A detail query failed with `404` | `NotFoundPage` | Replaces the content region |
| A detail query failed with `403` | `AccessDeniedPage` | Replaces the content region |
| A form submission failed | `ErrorBanner` | **Above the submit button**, inside the form, `role="alert"` |
| A row action failed | `ErrorBanner` | Above the table |
| Anything returned `401` | — | Redirect; nothing is rendered |
| A mutation succeeded | `Snackbar` | Bottom, auto-hide. Successes are the only toasts |

An error never replaces the form the user was filling in. Their input must survive the failure —
losing a half-built ticket type to a `422` about one field is the difference between a correction
and a re-entry.

### 7.3 Why a `422` cannot highlight a field, and what to do instead

The entire error body is `{ status, title, traceId }`. There is no `errors{}` dictionary and no
machine-readable code, so a `422` is **one sentence naming one broken rule** with nothing tying
it to an input. `"At least one field is required."` does not say *which* control to outline red,
and matching the sentence against field labels to guess is a heuristic that breaks the first time
somebody rewords a message on the server.

So:

1. **Validate completely on the client** (§9). A well-validated form should make server `422`s
   unreachable for anything the client can check. Reaching one means either a rule the client does
   not know or a genuine race.
2. **Render the `422` as a form-level banner**, verbatim from `title`. The server's wording is
   written for the user; do not paraphrase it.
3. **Do not guess at a field.** A red outline on the wrong control is worse than none.
4. When a `422` names a rule the client could have checked, that is a **client** defect. Add the
   Zod rule; do not treat the banner as the fix.

Field-level errors are item 5 in
[BACKEND_CHANGES_REQUIRED.md](BACKEND_CHANGES_REQUIRED.md).

### 7.4 Loading and empty states

- **Lists** render a `Skeleton` in the table body on first load, keeping the header and pager in
  place so the layout does not jump.
- **Refetches keep the old rows visible.** TanStack Query's `isFetching` with existing data means
  a subtle progress indicator, not a skeleton. Blanking a table the user is reading because the
  pager was clicked is a worse experience than a brief stale row.
- **Detail screens** render `LoadingRegion` in the content area.
- **Buttons** disable while their mutation is pending and show a small spinner. Never disable the
  whole form; the user may want to correct a field while the request is in flight.
- **Empty is not an error.** `items: []` with `totalCount: 0` renders `EmptyState` with the reason
  and, where the role permits it, the action that fixes it — "No customers yet" plus *Add
  Customer* for an `AccountantAdmin`, and the sentence alone for an `AccountantUser` who cannot
  create one.
- **`items: []` with `totalCount > 0`** means the page ran past the end (§3.3). Offer "back to
  the first page", not "no results".

---

## 8. MUI conventions

### 8.1 The theme

**File:** `frontend/src/theme.ts`

One `createTheme` call, one `ThemeProvider` in `main.tsx`, wrapped in `CssBaseline`. Every colour,
radius, spacing step and font size comes from the theme. A hex literal in a component is a
defect: this is an application whose look will be adjusted once, globally, by somebody who will
search the theme file and find nothing.

`sx` is for **layout local to one component** — a gap, a width, an alignment. It is not for
colour, typography, or anything that repeats. A `sx` prop appearing three times identically is a
component that should exist in `shared/components/`.

### 8.2 `Table`, not `DataGrid`

`@mui/x-data-grid` is banned for this application.

Every list in this API is **server-paginated** with a fixed envelope (§3.3). `DataGrid`'s default
model is client-side: it wants all the rows, then sorts and pages them in the browser. Driving it
server-side means opting out of that model with `paginationMode="server"`,
`sortingMode="server"`, `rowCount`, and a controlled `paginationModel` — and every one of those
is a place to get the 0-based/1-based conversion wrong, or to fire a second fetch on mount. The
failure is quiet: a list that is off by one page, or that double-fetches every navigation.
Server-side filtering and row grouping are Pro-licensed besides.

MUI's plain `Table` has none of that model. It renders the rows it is given. Wrap it once:

`PaginatedTable` takes a `PaginatedResponse<T>`, a column definition, and a page-change callback,
and owns the single 0-based/1-based conversion in the application (§3.3 item 3). Every list
screen uses it. No screen composes `Table` + `TablePagination` itself.

### 8.3 Shared components to build once

| Component | Responsibility | Notes |
|---|---|---|
| `AppShell` | AppBar, nav, account menu, `<Outlet />` | §5 |
| `PageHeader` | Title, optional subtitle, primary action slot | The action slot is where `can()` gates a button |
| `PaginatedTable` | `PaginatedResponse<T>` → table + pager | The only 1-based/0-based conversion (§3.3) |
| `ConfirmDialog` | Confirm an irreversible action | Mandatory for *depart* and *close*; see §8.3 note |
| `StatusChip` | `Active`/`Suspended`/`Invited`/`Departed` → a coloured `Chip` | One colour map, so `Suspended` is never green on one screen |
| `ErrorBanner` | An `ApiError` → an `Alert` | Owns the §7.1 taxonomy; screens pass the error and nothing else |
| `EmptyState` | Icon, sentence, optional action | Handles the `totalCount > 0` over-run case (§3.3) |
| `LoadingRegion` | Centred progress inside a region | Never full-page (§5.3) |
| `NotFoundPage` | "Not found" | Also the `*` route (§4.4) |
| `AccessDeniedPage` | "You do not have permission" | Rendered by `RequireRole` |

`ConfirmDialog` is required wherever the server says an operation is irreversible **or costly to
undo**. `POST /api/employees/depart` is the reference case: it suspends the account in the same
transaction, and as of 2026-09-02 it is reversible only as a *correction*, through
`/api/employees/reinstate` — somebody who genuinely left and came back is registered as a new
record instead. The dialog must name that consequence, not just ask "are you sure?", and must not
be softened into "you can always undo this" — see [Screens/EmployeesScreens.md](Screens/EmployeesScreens.md)
§8.1 for the required copy on both sides.

### 8.4 Accessibility floor

Not aspirational; these are the four things whose absence makes the app unusable rather than
imperfect.

1. **Every input has a real `<label>`.** MUI's `TextField label=` does this; a `placeholder`
   does not.
2. **Error banners are `role="alert"`** so a screen reader announces a failed submit. Silence
   after pressing *Save* is indistinguishable from a hung request.
3. **Focus moves to the banner on a failed submit**, and to the first heading on route change.
4. **Icon-only buttons carry an `aria-label`.** The notification bell and every row action.

Colour is never the only carrier of meaning. `StatusChip` shows the word as well as the colour.

---

## 9. Forms

### 9.1 React Hook Form + Zod, and why validation is not optional

One Zod schema per form, wired through `zodResolver`. This is not the usual "nice to have
validation" argument: **the server cannot tell the UI which field is wrong** (§7.3). Whatever the
client fails to check arrives as a sentence in a banner with no field attached. Client validation
is the only mechanism in this application that can put an error next to an input.

Schemas live beside the screen that uses them, in the slice folder, not in `shared/`. A schema
shared between two forms is usually two forms that should be one component.

### 9.2 Backend limits the client must mirror

Every one of these is enforced server-side and returns a `422` if violated. Mirror them exactly:
a client limit *stricter* than the server's blocks legitimate input, and one *looser* produces
the unhelpful banner §7.3 describes.

| Field | Limit | Source |
|---|---|---|
| Ticket type `code` | 1–100 chars, immutable after create | `TicketTypeMapper.cs` |
| `displayName` | 1–255 | `TicketTypeMapper.cs` |
| `category` | ≤100 | `TicketTypeMapper.cs` |
| `description` | ≤10,000 | `TicketTypeMapper.cs` |
| `helpText` | ≤10,000 — **declared but not enforced**, see below | `TicketTypeMapper.cs` |
| Field `key` | 1–100, **unique case-insensitively** within the type, **not trimmed** — see below | `TicketTypeMapper.cs` |
| Field `label` | 1–255 | `TicketTypeMapper.cs` |
| `groupName` | ≤100 | `TicketTypeMapper.cs` |
| `regexPattern` | ≤500, and **must compile** | `TicketTypeMapper.cs` |
| `allowedFileTypes` | ≤500 chars when joined | `TicketTypeMapper.cs` |
| `conditionalVisibility.value` | ≤500 | `TicketTypeMapper.cs` |
| Fields per type | **≥1** | `TicketTypeMapper.cs` |
| Choice options | **≥2** for choice types, **0** for non-choice types | `TicketTypeMapper.cs` |
| Numeric/date ranges | `min` must not exceed `max` | `TicketTypeMapper.cs` |
| `conditionalVisibility.fieldKey` | Must name **another** existing field, not itself | `TicketTypeMapper.cs` |
| Password | 12–128 chars; must differ from the login email **and** from the current password; no composition rules | `Slices/Identity` |
| `markRead` ids | Non-empty, **≤200** per call | `Slices/Notifications` |
| `pageSize` | 1–50, clamped | `Shared/Pagination/PaginatedQuery.cs` |

**No composition rules on passwords.** Do not add "one uppercase, one digit, one symbol". The
server does not require it, so a client that does rejects passwords the server would accept, and
the user has no way to discover which rule is imaginary.

**Two rows above are marked "declared but not enforced" and "not trimmed", and both are
deliberate exceptions to the mirror-exactly rule.** They are the only two places in this table
where the client is stricter than the server on purpose.

`TicketTypeMapper.cs`:94 declares `private const int HelpTextMaxLength = 10_000;` and **no
validator ever reads it**. `ValidateDescription` uses `DescriptionMaxLength`, the constant
declared immediately above it, and `ValidateFields` length-checks `label`, `groupName`,
`regexPattern`, both `conditionalVisibility` members and the joined `allowedFileTypes` — never
`helpText`. The column is `TEXT`, which PostgreSQL does not bound, and nothing in this system is
ever purged, so an unbounded `helpText` is stored forever. Mirror the 10,000 anyway. A client cap
on a limit the server forgot is the one case where being stricter costs nothing: no legitimate
help text approaches 10,000 characters, and the alternative is a field with no ceiling at all.
Recorded as punch-list item 24.

Field `key` is checked non-blank, ≤100 and unique — but `NormalizeFields` trims only `label` and
`groupName`, so `key` arrives unmodified. The uniqueness `HashSet` is `OrdinalIgnoreCase`:
case-insensitive, **whitespace-sensitive**. `"amount"` and `"amount "` are therefore two distinct
fields in one version, both accepted, indistinguishable in the editor's field list, and one of
them unreachable by any `conditionalVisibility.fieldKey` a human would type. **Trim every `key`
client-side before submit.** This is not cosmetic tidying — it is the only guard that exists.
Recorded as punch-list item 19, which also covers the second half of the problem: nothing
restricts a key's *characters* either, and React Hook Form treats `.` and `[` as path syntax.

### 9.3 Rules for every form

**A. `mode: 'onBlur'`.** Validating on every keystroke shows "must be at least 12 characters"
after the first character of a password. Validating only on submit hides a fixable mistake until
the round trip.

**B. Submit is disabled only while the mutation is pending.** Never disabled because the form is
invalid — a disabled button with no explanation is a dead end. Let submit run, let RHF show the
errors, and move focus to the first one.

**C. Server errors go in a form-level `ErrorBanner`** (§7.3), never mapped onto a field.

**D. Input survives failure.** Never reset a form on error. Reset on success only, and only if
the screen stays mounted.

**E. Trim before submitting.** The server's length checks run on what it receives; a trailing
space that pushes a 100-character code to 101 produces a `422` about a limit the user appears to
be within.

**F. Send `null`, not `''`, for an untouched optional field.** An empty string is a value; a C#
`string?` binding treats the two differently, and `""` can pass a nullability check while failing
a length or format one.

**G. A destructive submit is confirmed with `ConfirmDialog`** (§8.3), and the dialog names the
consequence.

### 9.4 The stale-form problem — there is no concurrency control

**No optimistic concurrency exists anywhere in the built backend.** No row-version column, no
`ETag`, no `If-Match`, no `DbUpdateConcurrencyException` handling. Every "conflict" the API
reports is a unique-constraint race, not a lost-update check.

The consequence for `POST /api/ticket-types/edit` is specific and bad. `Fields` is a **full
replacement** that mints a new version. Two Accountants open v3, each adds a different field,
and both save: the first write makes v4, the second makes v5 from the *stale* v3 and the first
Accountant's field vanishes. Both users get `200 OK`. The database is consistent. The work is
gone, and nothing anywhere reports it.

`409 "This ticket type was edited by someone else. Reload and try again."` only fires when the
two writes race for the same `(ticket_type_id, version_number)` index slot — genuinely
simultaneous saves. Sequential saves from stale forms slip through.

The only signal available to the client is `TicketTypeDetailDto`, which carries both
`versionNumber` (the version this response describes) and `currentVersionNumber` (the latest that
exists). So, **mandatory** for the type editor:

1. Record `currentVersionNumber` when the form loads.
2. Re-fetch the detail immediately before submitting.
3. If `currentVersionNumber` has moved, **do not submit**. Show a blocking banner: the type was
   changed by someone else, here is what changed, reload to continue.

This is a mitigation, not a fix — the window between step 2 and the write is still open. The
proper fix is a `version` column and a `409`, which is item 7 in
[BACKEND_CHANGES_REQUIRED.md](BACKEND_CHANGES_REQUIRED.md). Do not skip the mitigation on the
grounds that it is imperfect, and do not present it as making the problem go away.

**01-DomainModel.md** already specifies concurrency for the `tickets` row — *"An ordinary
`integer` column the handler increments"*, with the note that the SPA round-trips it. It is
built: `tickets.version` exists, every mutating ticket call carries `version`, and a stale
one gets a `409` that means reload. That pattern is the model for the fix here.

---

## 10. Dates, numbers, money and enums on the wire

### 10.1 Enums: `role` is a number, `status` is a string

The API registers **no** `JsonStringEnumConverter`. So C# `enum` properties serialise as
**integers**, while properties that are already `string` in C# serialise as strings. Two
conventions in one payload, and nothing in the JSON marks the difference.

```ts
/** Mirrors Shared/Auth/UserRole.cs. The ORDER IS THE CONTRACT: these are the wire values. */
export const UserRole = {
  AccountantAdmin: 0,
  AccountantUser: 1,
  CustomerAdmin: 2,
  Employee: 3,
} as const;
export type UserRole = (typeof UserRole)[keyof typeof UserRole];
```

| Value | Wire form | Example |
|---|---|---|
| `SessionDto.role`, `AccountantDetailDto.role` | **integer** `0`–`3` | `"role": 0` |
| `InviteAccountantRequestDto.role`, `SetEmployeeRoleRequestDto` role | **integer**, and must be **sent** as one | `{"role": 1}`. A string is a `400` |
| `Customer.status` | string | **`"Active" \| "Suspended"` only** — see below |
| `UserAccount.status` / `accountStatus` | string | `"Invited" \| "Active" \| "Suspended"` |
| Employee status | string | `"Active" \| "Departed"` |
| Audit `outcome` | string | `"Success" \| "Denied" \| "Failure"` |

**Four status vocabularies, no two of them the same, and `Invited` belongs to exactly one of
them.** A `Customer` is never `Invited`: `CustomerStatus` declares only `Active` and `Suspended`,
both insert paths write `Active`, `ListCustomersHandler` answers `422 "Unknown customer status."`
for anything else, and migration `20260901_002_AddCustomerStatusCheck.sql` adds
`CHECK (status IN ('Active', 'Suspended'))`. `Invited` is a **`UserAccount`** status — the person,
not the company.

So a Customer status filter must offer **two** options, and a `StatusChip` fed a Customer's status
must never be able to render `Invited`. Offering it produces a filter that returns a `422` on
selection, which reads as a server bug. The `StatusChip` colour map (§8.3) is shared across all
four vocabularies deliberately — one colour per word, so `Suspended` is never green on one screen
and red on another — but sharing the map does **not** mean every word is valid for every entity.

Three consequences:

1. **`role === 0` is `AccountantAdmin` and `0` is falsy.** `if (session.role)` is `false` for the
   most privileged role in the system. Never test a role for truthiness; compare it.
2. **Never render a raw role.** `format/enums.ts` maps the integer to the glossary label —
   `0 → "Accountant Admin"`. A screen showing "Role: 2" is a screen that leaked the wire format.
3. **Adding a role, or reordering `UserRole.cs`, silently repoints every stored value.** The enum
   order is the contract. This is the strongest argument for item 4 in the punch-list.

### 10.2 Dates

| C# type | Wire form | UI handling |
|---|---|---|
| `DateOnly` | `"2026-09-02"` | No timezone. Parse and render as a plain date; never construct a `Date` from it and format in local time, which shifts it a day west of UTC |
| `DateTime` (TicketTypes `createdAt`/`updatedAt`) | `"2026-09-02T14:33:12.4Z"` or with no suffix | **UTC, but the offset may be absent.** Treat a bare value as UTC |
| `DateTimeOffset` | `"2026-09-02T14:33:12.4+00:00"` | Has an offset; parse directly |

Render all timestamps in the **browser's local timezone**, with a consistent format from
`format/dates.ts`. One deployment serves one office, so a timezone label is noise — but a
timestamp that is silently eight hours out is a real support call, so the UTC-vs-local conversion
happens in exactly one module.

`MoneyAmount` fields arrive as JSON numbers from a C# `decimal`. Format with
`Intl.NumberFormat` for display and keep the raw number for arithmetic. Never store a formatted
string back into form state. There is no currency field in the schema; formatting is
locale-decimal, not currency-symbol, until one exists.

---

## 11. Build, the dev loop, and verification

### 11.1 `vite.config.ts`

**File:** `frontend/vite.config.ts`

```ts
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    // The proxy is what lets the browser see ONE origin in development, so the SameSite=Strict
    // session cookie works and no CORS configuration is needed anywhere. Removing it and calling
    // the API directly on its own port breaks the cookie, not just convenience.
    proxy: { '/api': 'http://localhost:5131' },
  },
  build: {
    // The Dockerfile copies /ui/dist into the API's wwwroot (04-Infrastructure.md section 3).
    outDir: 'dist',
  },
});
```

> **The port here is `5131`, not the `5000` in `04-Infrastructure.md` §2.** That document's
> example predates `AccountantApp.Api/Properties/launchSettings.json`, which binds
> `http://localhost:5131`. The proxy must match the port the API actually listens on. This drift
> is item 8 in [BACKEND_CHANGES_REQUIRED.md](BACKEND_CHANGES_REQUIRED.md); whichever side is
> changed, both must end up naming one port.

### 11.2 The dev loop

```
docker compose up -d db                       # Postgres only
dotnet run --project AccountantApp.Api        # http://localhost:5131
cd frontend && npm run dev                    # http://localhost:5173  <- use this one
```

Open **5173**, never 5131. The proxy is what makes the session cookie work (§11.1).

The seeded first Accountant Admin is `admin@accountantapp.local` with the password from
`Seeding:FirstAdminPassword`, and arrives with `mustChangePassword: true` — so the very first
thing a developer exercises is the forced-password-change gate
([LoginArchitecture.md](LoginArchitecture.md) §3). There is no way around it: `DevAuthHandler`
and its `X-Dev-Role` header have been **deleted**. Every request now needs a real login.

The cookie is `Secure`. Browsers treat `http://localhost` as a trustworthy origin, so this works
over plain HTTP in development. It will **not** work if the dev server is reached by LAN IP or a
`.local` hostname — the cookie is silently dropped and login appears to succeed and then 401.
Use `localhost`.

### 11.3 Five things to verify before writing a screen

Run these once, against a real database, and confirm each. Every one is a fact this document
rests on, and each is cheap to check and expensive to assume.

```bash
curl -i localhost:5131/api/auth/me
# -> 401 with a JSON ProblemDetails body. NOT a 302 to a login page.

curl -i localhost:5131/api/nonexistent
# -> 404 ProblemDetails. NOT index.html.

curl -i localhost:5131/
# -> 404 today. Proves the three hosting lines are still missing (section 4.4).

curl -i -c jar -X POST localhost:5131/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@accountantapp.local","password":"<seeded>"}'
# -> 200, Set-Cookie: aa_session. In the body: "role" is a NUMBER, "mustChangePassword" is true.

curl -i -b jar 'localhost:5131/api/ticket-types/list?pageNumber=1&pageSize=999'
# -> 200 with "pageSize": 50. Clamped, not rejected (section 2.4 item 6).
```

If any of the five behaves differently, **stop and flag it** — the document is wrong and needs
correcting, not working around.

---

## 12. Known constraints

1. **No ticket screens exist, and the app is about tickets.** Until the `Tickets` UI ships, the SPA is
   an administration console: customers, employees, ticket types, accountants, notifications,
   audit. The `/` redirects in §4.2 and the nav in §5.2 both change when it lands.
2. **An `Employee` has three nav items and almost nothing to do.** Correct for now — their
   screens are "my tickets" and "new ticket". Do not invent filler.
3. **No file upload anywhere.** Not for want of a backend. `Documents` **is** built and is
   registered at `Program.cs:59`, and by design it will never expose HTTP routes **of its own** —
   that distinction is the whole point and must not be flattened into "it has no routes". Four
   document routes DO exist, and `Tickets` registers them on `Documents`' behalf in
   `Slices/Tickets/TicketsEndpoints.cs:250` (`MapGroup("/api/documents")`): `/upload` at `:252`,
   `/list` at `:312`, `/download` at `:322`, `/delete` at `:356`. The reason the UI cannot use
   them is a **frontend** one: there is no `Tickets` UI plan, no `Screens/TicketsScreens.md` and
   therefore no client route and no ticket form anywhere in this specification, so there is no
   ticket id to attach an upload to and no submit path to attach it from. The form renderer's
   `FileUpload` control therefore renders **disabled**, with a note, rather than being omitted — a
   ticket type author can define a file field today and needs to see it.
4. **Lost updates on ticket-type edits are undetectable.** §9.4. Mitigated, not solved.
5. **A `422` can never highlight a field.** §7.3.
6. **The permission table is hand-duplicated from the server.** §6.3.
7. **`role` crosses the wire as an integer**, and the enum's declaration order is the contract.
   §10.1.
8. **The SPA cannot be served by the API yet.** §4.4. `npm run dev` only.
9. **No Dockerfile or `docker-compose.yml` exists in the repository**, though both are written
   out in full in `04-Infrastructure.md` §2–3. Production has never been built.
10. **Two `.Produces<T>` declarations are wrong**, so a generated client would be wrong in the
    same two places. §2.6.
11. **No screen in this specification has ever been rendered against a running backend.** The
    same caveat the slice plans carry, for the same reason: nothing here is verified.

---

## 13. Questions to flag if unclear

Flag these; do not answer them by inventing a behaviour.

- [ ] `/api/accountants/list` returns a different DTO shape to an `AccountantUser` than to an
      `AccountantAdmin`. Should the UI render two table shapes, or should the endpoint always
      return the narrower one and let the Admin fetch detail separately?
- [ ] Is there a version-history endpoint for ticket types? Only fetch-by-version-number exists.
      `Slices/TicketTypes/IMPLEMENTATION_PLAN.md` §11 already asks this and says *"the Accountant
      UI probably needs it"* — the list screen cannot show a version history without it.
- [ ] What polling interval is acceptable for the unread-notification count? §3.2 rule H requires
      one interval; nothing specifies the number.
- [ ] Should a suspended Customer's Employees be able to log in at all? The UI needs to know
      whether to show a suspension banner or a locked-out state.
- [ ] Is there a supported "resend invitation" operation? Neither `/api/accountants/invite` nor
      `/api/employees/invite` documents what happens when the target is already `Invited`.
- [ ] `SetEmployeeRoleRequestDto` documents that *"the target's existing session keeps the old
      role until it expires"* — up to 8 hours. Should the UI warn the operator that the change is
      not immediate?
- [ ] Which timezone should timestamps display in if the office is not in the browser's timezone?
      §10.2 assumes local.
- [ ] Does `App__BaseUrl` need to be surfaced anywhere in the UI? If it is misconfigured, emailed
      links break and nothing in the app reveals it.

---

## Files checklist

Configuration and shell:

- [ ] `frontend/package.json`, `tsconfig.json`, `index.html`
- [ ] `frontend/vite.config.ts` — proxy to the API's real port (§11.1)
- [ ] `frontend/src/main.tsx`, `App.tsx`, `routes.tsx`, `theme.ts`

`shared/api/`:

- [ ] `http.ts`, `ApiError.ts`, `problemDetails.ts`, `paginated.ts`, `queryClient.ts`

`shared/auth/`:

- [ ] `SessionProvider.tsx`, `useSession.ts`, `RequireSession.tsx`, `RequireRole.tsx`

`shared/permissions/`:

- [ ] `actions.ts`, `can.ts` — the table in §6.1, complete

`shared/components/`:

- [ ] `AppShell.tsx`, `PageHeader.tsx`, `PaginatedTable.tsx`, `ConfirmDialog.tsx`
- [ ] `StatusChip.tsx`, `ErrorBanner.tsx`, `EmptyState.tsx`, `LoadingRegion.tsx`
- [ ] `NotFoundPage.tsx`, `AccessDeniedPage.tsx`

`shared/` other:

- [ ] `dynamicForm/` — four files, specified in [Screens/TicketTypesScreens.md](Screens/TicketTypesScreens.md)
- [ ] `hooks/usePaginatedQuery.ts`
- [ ] `format/dates.ts`, `money.ts`, `enums.ts`

Per slice — `identity`, `customers`, `employees`, `ticketTypes`, `notifications`, `audit`:

- [ ] `api.ts`, `types.ts`, `queries.ts`, `screens/`, `components/`

---

## Success criteria

Each is verified by running the app, not by reading the code.

1. `npm run dev` serves the SPA on 5173, and a request to `/api/auth/me` from the browser reaches
   the API on its real port through the proxy.
2. There is no occurrence of `VITE_`, `import.meta.env`, or an `http://` literal in any API path
   in `frontend/src`.
3. `fetch` appears in exactly one file: `shared/api/http.ts`.
4. Signing in as the seeded admin lands on `/change-password`, and no other route is reachable
   until the password is changed.
5. After changing the password, `/` redirects by role per §4.2 and the nav shows exactly the
   items in §5.2 for that role.
6. Requesting a page size above 50 renders a pager consistent with the 50 the server returned,
   with no missing rows.
7. Navigating to `/custmoers` renders `NotFoundPage`, not a blank screen.
8. Navigating to `/audit` as an `AccountantUser` renders `AccessDeniedPage`, and the nav has no
   *Audit log* item for that role.
9. Requesting another Customer's employee by id renders "Not found" — never "forbidden".
10. Letting the session expire and then clicking any link redirects to `/login` once, with no
    retry storm and no toast.
11. A `422` from a deliberately invalid submission renders the server's `title` verbatim above the
    submit button, and every value the user typed is still in the form.
12. Stopping the API mid-session renders "The server is unavailable" rather than a blank screen or
    an unhandled `SyntaxError`.
13. The action table in §6.1 matches the union of the six `{Slice}ActionCatalogue.cs` files
    exactly — same action names, same role sets, no extras on either side.
14. No screen renders a raw role integer, a raw status string that is not a glossary term, or the
    word "Client".
