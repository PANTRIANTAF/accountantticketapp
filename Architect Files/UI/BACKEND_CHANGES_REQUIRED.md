# Backend Changes the UI Requires

A punch-list, not a wish-list. Every item below was found by writing the UI specification
against the shipped backend and hitting something that does not exist, contradicts a locked
decision, or forces the client into a workaround that will have to be unpicked later.

Each item states: **what the UI needs**, **what exists today**, **the file to change**, and
**the UI workaround until it lands**. The workaround matters as much as the fix — a builder
reading a screen spec needs to know that a strange-looking instruction is a workaround for a
numbered item here, not an arbitrary choice.

Items are ordered by consequence, not by effort:

| Band | Meaning |
|---|---|
| **Blocking** | The UI cannot be built, run, or deployed at all until this is done. |
| **Degrading** | The UI can be built, but carries a workaround that a builder will otherwise "fix" back into a bug. |
| **Drift** | The code and documents 0–4 disagree. Nothing breaks, but the next reader is misled. |

**Nothing in this file overrides documents 0–4.** Where the code and a numbered document
disagree, the UI specification follows the numbered document and records the drift here — it
does not silently follow the code (`README.md` §*Conflict precedence*).

**Item numbers are permanent identifiers, not positions.** The screen specs cite them ("a
workaround for item 12"), so renumbering an item silently invalidates every citation to it. New
items are therefore appended with the next free number and filed under the right band, which
means **a band is no longer a contiguous range** — `Blocking` holds 1–3, `Degrading` holds 4–13,
19–23 and 26–31, `Drift` holds 14–18, 24–25 and 32–33. Read the band heading, not the number. Never
renumber; if an item is resolved, mark it resolved and leave the number.

Items 27–33 were added on 2026-09-02 while writing the seven UI implementation plans under
`UI/Plans/`. That is where they came from and it is worth noting why: each was found by checking a
claim in a screen spec against the handler that implements it, one route at a time. None was
visible from reading either document alone.

**Thirty-three items, one of them resolved** (item 26, on 2026-09-02 — see its entry for what to
verify before trusting that). Verified against the working tree on 2 September 2026. Six slices are
built and wired in `Program.cs`: `Audit`, `Notifications`, `Customers`, `TicketTypes`, `Identity`,
`Employees`. `Documents` and `Tickets` are not.

---

## Blocking

### 1. The three SPA-hosting lines are missing from `Program.cs`

**What the UI needs:** the `app` container to serve `index.html` at `/` and to return
`index.html` for any non-`/api` path, so client-side routing works after a hard refresh on a
deep link.

**What exists today:** nothing. `Program.cs` maps six `Map{Slice}Endpoints()` calls and stops.
`GET /` returns a `ProblemDetails` 404. `GET /customers` returns a `ProblemDetails` 404. The
SPA cannot be served by the API at all.

This is not a new decision — `04-Infrastructure.md` §1 specifies it as **LOCKED** and gives the
exact three lines:

```csharp
app.UseDefaultFiles();      // "/" → index.html
app.UseStaticFiles();       // wwwroot/, populated by the React build
app.MapFallbackToFile("index.html");   // client-side routing: /tickets/42 → index.html
```

**File to change:** `AccountantApp.Api/Program.cs`.

**Two constraints on where they go**, both from `04-Infrastructure.md` §1:

- `MapFallbackToFile` must be registered **last**, after every `MapXxxEndpoints()` call, or it
  swallows API routes.
- It must never return `index.html` for a path under `/api`. An unknown API route is a `404`
  `ProblemDetails`, not an HTML page — because `await response.json()` on an HTML body throws a
  syntax error from a request that returned `200`, and the reported symptom points nowhere near
  the router. See `LoginArchitecture.md` §0.3.

**Verify both after the change:** `GET /customers` returns HTML; `GET /api/nonexistent` returns
a 404 `ProblemDetails`.

**UI workaround until it lands:** development works regardless, because the vite dev server
serves the SPA and proxies `/api` (item 8). So this blocks **deployment only** — which means it
will be discovered late, by whoever first builds the image, unless it is done now.

### 2. There is no `Dockerfile`, no `docker-compose.yml`, and no `Caddyfile`

**What the UI needs:** the multi-stage build that compiles `frontend/` and copies `dist` into
the API's `wwwroot`. Without it there is no artefact that contains the SPA.

**What exists today:** none of them. The repository root holds `AccountantApp.Api`,
`AccountantApp.Tests`, `AccountantApp.slnx` and `Architect Files`. No `.env.example` either.

All three files are **already fully written** in `04-Infrastructure.md` §2–3 and can be copied
out verbatim. This is transcription, not design. Note that the Dockerfile as written hard-codes
the SPA's location:

```dockerfile
COPY frontend/package*.json ./
RUN npm run build                      # emits /ui/dist
COPY --from=ui /ui/dist ./wwwroot
```

so `frontend/` at the repository root is not a suggestion in `GeneralUIArchitecture.md` §1.1 —
it is what the (unwritten) Dockerfile requires.

**Files to create:** `Dockerfile`, `docker-compose.yml`, `Caddyfile`, `.env.example`.

**UI workaround until it lands:** none needed for development. Blocks deployment.

### 3. `frontend/` does not exist

Stated for completeness, since it is the UI's own first task rather than a backend change. The
directory is absent, so `npm` has nothing to install and the Dockerfile's `ui` stage would fail
on `COPY frontend/package*.json`. Scaffold it per `GeneralUIArchitecture.md` §1.2.

---

## Degrading

### 4. No `JsonStringEnumConverter`, so `role` is an integer while `status` is a string

**What the UI needs:** one convention for enums on the wire.

**What exists today:** no `JsonStringEnumConverter` is registered anywhere, so every C# enum
serialises as its integer value. `UserRole` therefore crosses the wire as `0`–`3`. Meanwhile
`AccountStatus`, `CustomerStatus`, `EmployeeStatus` and `AuditOutcome` arrive as **strings**,
because those DTO properties are declared as `string`, not as enums. There is no rule to
learn — it has to be checked field by field, and two conventions appear in a single row of the
accountant list.

**The concrete cost:** `AccountantAdmin` is `0`, which is falsy in JavaScript. `if (row.role)`
is `false` for the most privileged role in the system; so is `role || fallback`. This is
documented as a mandatory trap in `GeneralUIArchitecture.md` §10.1 and
`LoginArchitecture.md` §8 rule A, and it will still cost somebody an afternoon.

**File to change:** `AccountantApp.Api/Program.cs`, in the JSON options.

**Why it is not simply "a fix":** it is a **breaking change to every response and every request
carrying a role**. The client's `UserRole` map, `ROLE_LABELS`, `can()`, `RequireRole`,
`InviteAccountantRequestDto.Role` and `SetEmployeeRoleRequestDto.Role` all change at once, so
the API change and the SPA change must deploy together. Do not land it mid-build.

**UI workaround:** the integer map in `frontend/src/shared/format/enums.ts`, plus the rule that
role comparisons are always `===` against a named constant.

### 5. `ProblemDetails` carries no field-level errors and no error code

**What the UI needs:** to attach a server validation failure to the input that caused it, and
to branch on a failure programmatically rather than by reading English.

**What exists today:** `Shared/Errors/AppExceptionMiddleware.cs` writes exactly three things:

```csharp
new ProblemDetails
{
    Status = statusCode,
    Title = title,
    Extensions = { ["traceId"] = context.TraceIdentifier }
}
```

No `errors` dictionary, no `code`, no `detail`. The human-readable message is in `title`.

**Two consequences:**

- **Every 422 is a form-level banner.** The UI cannot highlight the offending field, because
  the response does not say which one it was. This is why
  `GeneralUIArchitecture.md` §9 makes client-side validation *complete* rather than
  best-effort: the client is the only layer that can point at an input.
- **The forced-password-change 403 is matched on a sentence.** It is the one response in the
  API that populates `detail`, and `MustChangePasswordMiddleware` states that the front end
  matches on the string to decide to show the change-password screen. A reworded message
  silently breaks the gate. See `LoginArchitecture.md` §3.1.

**File to change:** `Shared/Errors/AppExceptionMiddleware.cs`, plus `AppException` to carry an
optional code and an optional field name.

**Minimum useful version:** a stable `code` extension. A full field map is a larger change,
because handlers throw `AppException` with a sentence and no field reference; a `code` is
additive and unblocks the one place that currently string-matches.

**UI workaround:** form-level banners everywhere, and one exported constant for the
must-change-password sentence, matched by substring in exactly one function.

### 6. Two endpoints return two different shapes and declare only one

**What the UI needs:** a response whose shape does not depend on who asked.

**What exists today:** the endpoint declares
`.Produces<PaginatedResponse<AccountantDetailDto>>()` but the handler returns `object`, and an
`AccountantUser` actually receives `PaginatedResponse<AccountantSummaryDto>` — a narrower row
with no `loginEmail` key present at all. The endpoint's own comment says the declaration *"must
not be used to infer the response shape for a non-Admin caller"*.

This is a deliberate narrowing and the narrowing is correct — an `AccountantUser` has no
business reading colleagues' login addresses. The problem is that it is expressed as an
undeclared runtime shape change rather than as two endpoints or one lowest-common-denominator
shape.

**`POST /api/employees/get` has the same defect**, and worse documentation. It declares
`.Produces<EmployeeDetailDto>()`, the handler returns `Task<object>`, and a caller with the
`Employee` role receives `EmployeeSelfDto`. The endpoint's `.WithDescription` lists the narrowing
as *"without the status, the account link, the employment end date, or either personal
identifying number"* — but `EmployeeSelfDto` **also** omits `createdAt`, `role` and
`accountStatus`, and **adds** `notice`. A client that trusted the description would be wrong
about four fields.

**Files to change:** `Slices/Identity/IdentityEndpoints.cs` + `ListAccountantsHandler.cs`;
`Slices/Employees/EmployeesEndpoints.cs` + `GetEmployeeHandler.cs`.

**Two candidate fixes, and the question is which:** always return the narrow shape and add a
separate detail endpoint for the privileged caller; or keep both shapes and declare the union
honestly. Flagged as an open question in `Screens/IdentityScreens.md` and
`Screens/EmployeesScreens.md` rather than decided here.

**UI workaround:** discriminate on the **session role**, not on field presence. Field-presence
sniffing is fragile — an optional field that happens to be `null` looks exactly like the narrow
shape. At minimum, correct the `.WithDescription` on `/api/employees/get`: a description that is
wrong about four fields is worse than none, because it will be trusted.

### 7. No optimistic concurrency on `ticket_types`, so edits lose updates silently

**What the UI needs:** to be told that the record changed under it.

**What exists today:** nothing. `01-DomainModel.md` §9 locks optimistic concurrency on the
`tickets` row with a hand-maintained `version` column and a `409` on mismatch — but `Tickets`
is unbuilt, and no other table has it. `/api/ticket-types/edit` replaces the whole field list
and mints a new version. Two people editing the same Ticket Type from stale forms produce two
versions, and the first person's work is gone with no error, no warning, and nothing in the
response that differs from a clean edit.

**File to change:** `Slices/TicketTypes` — a `version` column on `ticket_types`, echoed in the
edit DTO, with a `409` on mismatch.

**UI workaround:** before submitting an edit, re-fetch the type and compare
`currentVersionNumber` to the value the form was loaded with; refuse and offer a reload if it
moved. This is specified as **mandatory** in `Screens/TicketTypesScreens.md` and
`GeneralUIArchitecture.md` §9.4, and it is explicitly **imperfect** — the window between the
check and the write is still open. It narrows the race; it does not close it. Only a server-side
version can.

### 8. The development API port is ambiguous

**What the UI needs:** one port for the vite proxy target.

**What exists today:** two answers.

| Source | Port |
|---|---|
| `04-Infrastructure.md` §2 (`proxy: { '/api': 'http://localhost:5000' }`) | 5000 |
| `AccountantApp.Api/Properties/launchSettings.json` | 5131 (http), 7152 (https) |

`GeneralUIArchitecture.md` §11.1 specifies **5131**, to match what `dotnet run` actually binds.
That is a deliberate departure from a numbered document and it is recorded here because
`README.md`'s precedence rule says the numbered document wins — so either the document or
`launchSettings.json` must change, and the UI config must then follow.

**Files to change:** one of `04-Infrastructure.md` §2 or
`AccountantApp.Api/Properties/launchSettings.json`.

**Symptom if unresolved:** every API call in development fails with a proxy `ECONNREFUSED`,
which surfaces in the browser as a network error rather than as a configuration mistake.

### 9. There is no OpenAPI document

**What the UI needs:** generated request and response types, instead of hand-written
`types.ts` files that drift the moment a DTO gains a field.

**What exists today:** no `AddOpenApi()`, no Swashbuckle, no `/openapi/v1.json`. The
`.Produces<T>()` annotations are already present and mostly accurate, which is the expensive
part — adding the package and two lines is the cheap part.

**File to change:** `AccountantApp.Api/Program.cs` and `AccountantApp.Api.csproj`.

**Two things to fix first, or the generated client is wrong in a way nobody notices:**

- `/api/accountants/list` (item 6) declares the richer of two shapes.
- `/api/notifications/list` declares `.Produces<object>(200)`, which generates as `unknown`.

**UI workaround:** hand-written `types.ts` per slice, cross-checked against
`Slices/*/Application/Dtos/*.cs` once. `GeneralUIArchitecture.md` §2.6 records what to delete
when OpenAPI lands.

### 10. No endpoint changes your **own** display name or your **own** login email

**What the UI needs:** a self-service profile edit, or a clear statement that there is none.

**What exists today:** `/api/auth/me` is read-only. `ChangePasswordRequestDto` is the only
self-service write in the entire API, and it has no target user field, deliberately, because
`02-AuthorizationMatrix.md` §11 permits resetting another person's password to **nobody**.

The gaps this leaves:

- No user can change their own display name. `AcceptInvitationHandler` is the only path that
  ever sets it after invitation.
- **No user can change their own login email**, at any privilege level, including an Accountant
  Admin changing their own.

**Login email — updated 2026-09-02.** This section previously said no endpoint in the application
changed a login email for anybody. That is no longer true: `POST /api/employees/change-login-email`
exists, takes `{ employeeId, loginEmail }`, and is granted to `AccountantAdmin` and
`AccountantUser` only — see `02-AuthorizationMatrix.md` §4, row *"Change an Employee's login
email"*. A Customer Admin is refused it, and so is the account's owner, deliberately: whoever can
move an account to a new address can move it to a mailbox they control.

So the accurate statement for the UI is **narrower than "there is no such thing"**:

| Whose login email | Who can change it | How |
|---|---|---|
| An Employee's or Customer Admin's | Either Accountant role | `POST /api/employees/change-login-email` |
| Your own | Nobody | — |
| An Accountant's | Nobody | — |

Whether *self-service* profile editing is intended for v1 is still a genuine question, not a defect
claim — recorded as an open question in `Screens/IdentityScreens.md` §7 and `LoginArchitecture.md`
§10, and in [the Identity plan](../Slices/Identity/IMPLEMENTATION_PLAN.md) §18 item 6. The
Accountant-only endpoint above is **not** a precedent for adding one; it was granted to Accountants
precisely because it was judged unsafe in the owner's own hands.

**UI workaround:** the `/profile` screen is read-only apart from the change-password link, and
must **not** present a display-name field or an editable login-email field. A form with nothing to
POST to is worse than no form: a builder will invent an endpoint for it. The
change-login-email form belongs on the Employee **detail** screen, visible to Accountants only —
see `Screens/EmployeesScreens.md`.

### 11. `SessionDto` does not carry the login email

**What the UI needs:** to validate "the new password must not equal the login email"
client-side, which `PasswordPolicy.Validate` enforces server-side.

**What exists today:** `SessionDto` is
`(userId, displayName, role, customerId, mustChangePassword)`. The login email is not in it, so
the change-password form cannot perform that one check locally and must let the server refuse.

**File to change:** `Slices/Identity/Application/Dtos/AuthDtos.cs` and `SessionClaims.cs`.

**Judgement:** minor. One extra field, additive, non-breaking. It removes one round trip and
one 422 that the user experiences as a form-level banner with no field highlighted (item 5).

**UI workaround:** accept the server 422 and render it as a form banner.

### 12. A Customer-side user cannot read their own Employee record, and the only write they have erases fields

The most consequential item on this list after the three blockers, because it is the one that
**destroys data on a `200`**.

**What the UI needs:** for a `CustomerAdmin` or `Employee` to load their own contact details
before editing them.

**What exists today:** no path to their own `employeeId`.

- `SessionDto` is `(userId, displayName, role, customerId, mustChangePassword)`. `userId` is a
  **UserAccount** id, not an Employee id, and the two are different entities in different slices.
- `POST /api/employees/get` takes an `employeeId` the caller does not have.
- `ListEmployeesHandler` excludes callers with the `Employee` role, so they cannot find themselves
  in a list either.
- `GET /api/customers/own` returns `CustomerSelfDto` — the company, not the person.

**And `POST /api/employees/update-own-contact` is a full replacement.**
`UpdateOwnContactHandler` assigns both editable fields unconditionally:

```csharp
employee.WorkEmail = request.WorkEmail;
employee.NormalizedWorkEmail = normalizedEmail;
employee.ContactPhone = request.ContactPhone;
```

So a form that submits without having been pre-filled — which is the only kind of form that can
be built today — sends `{ workEmail: null, contactPhone: null }` and **wipes both fields, with a
`200` and a success notice**. There is no partial-update semantic, no `409`, and nothing in the
response that distinguishes an intentional clear from an accidental one.

Note the endpoint resolves its target from the session (`UserAccountId == accountId`), so it
needs no id to *write*. The asymmetry is the whole problem: a write with no read.

**Files to change:** either add `employeeId` to `SessionDto`
(`Slices/Identity/Application/Dtos/AuthDtos.cs` — but that couples Identity to Employees), or add
`POST /api/employees/get-own` taking no body, mirroring `/api/customers/own`. The second is the
cleaner shape and matches an endpoint that already exists next door.

**UI workaround:** `Screens/EmployeesScreens.md` §7 specifies the contact region on `/profile` as
**read-only with no submit button** until this lands. That is deliberately worse than useless —
a form that cannot be pre-filled must not be offered, because the first person to open it and
click Save loses their own phone number and work email.

### 13. `EmployeeSummaryDto` carries no customer identity

**What the UI needs:** for an Accountant looking at employees across Customers to see who each
person works for.

**What exists today:** `EmployeeSummaryDto` has neither `customerId` nor a customer name, so a
cross-Customer employee list is a list of names with no employer column. The only way to get it
per row is one `POST /api/employees/get` per employee — fifteen extra requests per page, which is
not a workaround, it is a different bug.

**File to change:** `Slices/Employees/Application/Dtos/EmployeeDtos.cs` — add `customerId` and
`customerName` to the summary. The handler already has both in scope.

**UI workaround:** `Screens/EmployeesScreens.md` §4.3 blocks the unfiltered Accountant list behind
a "pick a Customer first" empty state, so the employer is implied by the filter rather than shown
per row. It works, and it makes a genuinely useful screen — "all employees, everywhere" —
unbuildable.

---

### 19. A field `key` is neither trimmed nor constrained to safe characters

**What the UI needs.** A ticket-type field `key` that is stable, comparable, and usable as a form
control name.

**What exists today.** `ValidateFields` requires each `key` to be non-blank, ≤100 characters, and
unique — with a `HashSet<string>(StringComparer.OrdinalIgnoreCase)`. `NormalizeFields` trims
`Label` and `GroupName` and **not** `Key`. Two consequences, both live:

- **Whitespace.** Uniqueness is case-insensitive and whitespace-*sensitive*, so `"amount"` and
  `"amount "` are two accepted, distinct fields in one version. They are indistinguishable in any
  UI that renders the key, and the second is unreachable by any `conditionalVisibility.fieldKey`
  a human would type — that check uses `keys.Contains`, which is also whitespace-sensitive.
- **Character set.** Nothing restricts the characters. `"a.b"` and `"a[0]"` are legal keys. React
  Hook Form parses `.` and `[` as **path syntax**, so registering a control named `a.b` creates a
  nested object rather than a flat field, silently reshaping both form state and the submitted
  payload. Nothing errors; the value simply arrives under the wrong name.

Correction note `TicketTypes` T-7 introduced trimming precisely to stop a leading space defeating
a uniqueness check, and T-13 extended it to `label` and `groupName`. It did not reach `key`, which
is the one field where the consequence is structural rather than cosmetic.

**File to change.** `Slices/TicketTypes/Application/TicketTypeMapper.cs` — add
`field.Key = field.Key.Trim();` to `NormalizeFields`, and add a character-set check to
`ValidateFields` (`^[A-Za-z][A-Za-z0-9_]*$` is sufficient and excludes both metacharacters).
Note that adding the pattern is a **breaking change for any type already stored** with a key that
fails it, so it needs a read of the existing rows first.

**UI workaround.** Two, both mandatory and both specified in `Screens/TicketTypesScreens.md`. The
editor trims every `key` before submit — currently the only guard that exists. The renderer does
**not** impose a client-side character pattern, because `GeneralUIArchitecture.md` §9.2 forbids a
client limit stricter than the server's on a field the server accepts; instead §6.7 rule C
registers controls under generated index aliases (`f0`, `f1`, …) and keeps an alias→key map,
building the payload from the map. That is why the renderer looks indirect where a direct
`register(field.key)` would read better.

---

### 20. `activeOnly=false` means "inactive only", not "include inactive"

**What the UI needs.** A ticket-type list that an Accountant can view as active-only, or in full.

**What exists today.** `ListTicketTypesHandler`:34-36 applies
`query.Where(t => t.IsActive == req.ActiveOnly.Value)` when the parameter has a value. So
`activeOnly=true` returns active types, `activeOnly=false` returns **only deactivated** types, and
omitting the parameter returns both. The parameter is a three-state filter wearing a boolean's
name.

The natural UI for a parameter called `activeOnly` is a checkbox, and a checkbox has two states.
Bound directly, unchecking it shows an Accountant nothing but deactivated types — which looks
exactly like a list that failed to load, or like every type having been deactivated.

**File to change.** `Slices/TicketTypes/Application/Dtos/ListTicketTypesRequestDto.cs` and its
handler. Either rename the parameter to `isActive` — which is what it does — or change the
handler so `false` means "no filter". Renaming is the honest fix and costs nothing today, because
no client consumes it yet.

**UI workaround.** `Screens/TicketTypesScreens.md` §1.1 mandates a **three-state** control (*All*
/ *Active* / *Inactive*) whose *All* option **omits the parameter entirely** rather than sending
`false`. A builder who simplifies that to a checkbox reintroduces the bug.

---

### 21. There is no way to list a ticket type's versions

**What the UI needs.** Version history for a ticket type: which versions exist, and when each was
created.

**What exists today.** `GET /api/ticket-types/version` fetches **one** version by number. There is
no endpoint that lists them. `TicketTypeDetailDto` carries `currentVersionNumber`, so a client can
infer that versions `1..currentVersionNumber` exist — and nothing else about them.

**File to change.** `Slices/TicketTypes/TicketTypesEndpoints.cs` plus a handler — a
`GET /api/ticket-types/versions` returning `{ versionNumber, createdAt, createdByUserId }` per
version. The rows exist; only the projection is missing.

**UI workaround.** `Screens/TicketTypesScreens.md` §4 steps through versions by number, bounded by
`1..currentVersionNumber`, fetching one at a time. The history list therefore shows **version
numbers with no dates** — a builder will be tempted to display "created" dates it does not have.
It must not invent them.

---

### 22. The audit log cannot be searched by `traceId`

**What the UI needs.** To turn a user's support report into the audit rows for that request.

**What exists today.** Nothing connects the two. `AppExceptionMiddleware` puts a `traceId` in
every `ProblemDetails`, and `GeneralUIArchitecture.md` §7.1 mandates that the `ErrorBanner`
display it for exactly that purpose — but `audit_entries` has no trace column, `AuditRecord` has
no such property, and `AuditSearchRequestDto` has no such filter. The identifier the UI shows the
user is the one identifier the audit log cannot be queried by.

This is the largest single gap in the audit screen's investigative value: a user reports "it said
error, trace `0HN7…`", and there is no way to find the corresponding row.

**File to change.** `Slices/Audit/Core/AuditRecord.cs`, the schema migration, `AuditApi.WriteAsync`
(read `Activity.Current?.Id` alongside the other ambient values it already captures from
`IHttpContextAccessor`), and `AuditSearchRequestDto`.

**UI workaround.** None. `Screens/AuditScreens.md` §9 records it as an open question rather than
specifying a filter that cannot be built.

---

### 23. No endpoint maps a user id to a display name

**What the UI needs.** To render "Suspended by Maria Papadopoulou" instead of
"Suspended by `a3f1c2e8-…`".

**What exists today.** Audit rows store `ActorUserId`, and `Slices/Audit/IMPLEMENTATION_PLAN.md`
§8 rule 3 instructs the UI to join ids to names client-side. That is not implementable.
`/api/accountants/list` is paginated **and** Office-only, so it cannot resolve a Customer-side
actor and cannot be used as a lookup table; `/api/employees/get` needs a customer scope the audit
row does not carry; and there is no batch id→name endpoint anywhere.

**File to change.** A small `POST /api/accountants/resolve-names` (or a shared one) taking ids and
returning `{ userId, displayName }`, readable by whoever may read the log. It must return nothing
for an id the caller may not see, and **must not** distinguish "no such user" from "not visible to
you" — otherwise it becomes an enumeration oracle for the very ids the 404 rule protects.

**UI workaround.** `Screens/AuditScreens.md` renders the **raw id**, monospaced, with no attempt at
a lookup. It looks unfinished because it is, and a builder must not paper over it by fetching
`/api/accountants/list` and hoping the actor is on page one.

Related: item 13 (`EmployeeSummaryDto` carries no customer identity) and item 11 (`SessionDto` has
no login email) are the same shape of problem — the API returns ids where the UI must show people.

---

### 26. ~~Two registered endpoints are unreachable by every role, because their action names are not in any catalogue~~ — **RESOLVED 2026-09-02**

**Fixed in the working tree.** `EmployeesActionCatalogue.cs` now declares both entries, at lines 53
and 60, with exactly the role lists this item specified. Verified by reading the file, not inferred.
The analysis below is kept because the *mechanism* is worth understanding — a `RequireAsync` string
with no catalogue entry is a dead route that boots cleanly and audits every attempt as a denial. The
test that links handler literals to catalogue keys, which this item asked for, is **now written** and
is described in place of that request below.

**This was a backend bug, not a specification gap, and it was the highest-consequence item on this
list after the three Blocking ones.**

`Slices/Employees/EmployeesEndpoints.cs` registered two routes that no caller could reach:

| Route | Action name the handler requires | In `EmployeesActionCatalogue.cs`? |
|---|---|---|
| `POST /api/employees/reinstate` | `"ReinstateEmployee"` | **No** |
| `POST /api/employees/change-login-email` | `"ChangeEmployeeLoginEmail"` | **No** |

`ReinstateEmployeeHandler`:59 calls `RequireAsync(user, "ReinstateEmployee", ct: ct)` and
`ChangeEmployeeLoginEmailHandler`:57 calls `RequireAsync(user, "ChangeEmployeeLoginEmail", ct: ct)`.
`EmployeesActionCatalogue.cs` declares eleven actions and neither of those is among them.

`PermissionChecker.RequireAsync`:41 is
`_actions.TryGetValue(action, out var roles) && roles.Contains(user.Role)`. An action name absent
from the composed dictionary fails the first clause, so `allowed` is `false` **for every role,
including `AccountantAdmin`**, and the method throws
`AppException("Permission denied for action '…'.", 403)`. There is no default-allow branch — the
fail-closed rule in `02-AuthorizationMatrix.md` is correctly implemented, and it is what makes
these two routes dead rather than merely permissive.

Two things make this hard to notice and worth fixing promptly:

- **The failure is silent at startup.** The constructor validates that no action has zero roles and
  that no two catalogues declare the same action, so a *duplicate* or an *empty* entry throws at
  boot. A **missing** entry cannot be detected there, because the constructor sees catalogues and
  never sees the handlers' string literals. Nothing links the two sides at compile time.
- **Every attempt writes a false audit entry.** Before throwing, `RequireAsync` logs
  `AuditActions.PermissionDenied` / `AuditOutcome.Denied` with `After: new { Action = action }`. So
  an `AccountantAdmin` correcting a mistaken departure is recorded as having attempted something
  they were not entitled to, in the one log an investigator is supposed to trust. The rows are
  already distinguishable — the action string is one nothing grants — but only to someone who knows
  to look.

**This is the one item on this list where the code contradicts a normative document.**
`02-AuthorizationMatrix.md` §4 grants both actions explicitly, with the roles already decided:

| Matrix §4 row | AA | AU | CA | EMP |
|---|---|---|---|---|
| Reinstate a `Departed` Employee (line 109) | Yes, any | Yes, any | Yes, own Customer | No |
| Change an Employee's login email (line 110) | Yes, any | Yes, any | **No** | **No** |

The matrix wins over the code (`README.md` §*Conflict precedence*), so this is not a design
question — the grants exist and the catalogue simply does not implement them.

**File changed.** `Slices/Employees/EmployeesActionCatalogue.cs`, which now contains:

```csharp
["ReinstateEmployee"] = [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin],
["ChangeEmployeeLoginEmail"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
```

`CustomerAdmin` is present on the first and absent from the second, and the asymmetry is
deliberate on both counts. The matrix's §4 notes give the reasons: *"Reinstatement is a
correction, not a re-hire"* — the person who entered the mistaken departure should be able to undo
it — and *"Changing a login email is reserved to the Office, and nobody may change their own"*,
because whoever can move an account to a new address can move it to a mailbox they control. Do not
"tidy" the two lists into matching.

Note that `CustomerAdmin`'s grant on reinstate is *"Yes, own Customer"*. The catalogue cannot
express that scope — no entry in it can — and `EmployeesActionCatalogue.cs` already says so in a
comment about its other rows. Row-level scoping stays where it is, in `CustomerScope` in the
handler, and surfaces as a `404`. Adding the role to the catalogue is correct and is not the whole
of the rule.

**The test that catches the next one now exists** — `EndpointRoutingTests.cs`,
`Every_action_name_a_handler_requires_exists_in_some_catalogue`. A startup check was considered and
rejected: it cannot be done by reflection, because the action is a string literal in a method body
and nothing in the metadata records it. The test scans `Slices/**/*.cs` for
`RequireAsync(user, "…"`, composes every `IActionCatalogue` in the assembly by reflection, and
asserts each scanned name resolves. Two details make it worth more than the obvious version:

- It **fails when the scan matches nothing**, so a refactor that changes the call shape breaks the
  test instead of quietly turning it into an assertion about an empty set.
- It **fails on a non-literal second argument** — a constant, a `nameof`, an interpolation — because
  the point of the literal is that this test can read it.

A companion, `Every_catalogued_action_is_required_by_some_handler`, asserts the reverse: a catalogue
entry no handler asks for is a granted permission for an operation that does not exist, which is what
somebody auditing who-may-do-what would read it as. Both directions pass, at 35 names each.

**No UI workaround is needed any more.** Both actions are now rows in `GeneralUIArchitecture.md`
§6.1's `can()` table, and `Screens/EmployeesScreens.md` specifies the affordances: *Reinstate* in
§8.1 and *Change login email* in §8.7, with the required dialog copy for each.

One thing to keep straight when reading that table: `ChangeEmployeeLoginEmail` grants `AA` and `AU`
only, so `can()` is `false` for a `CustomerAdmin` **by design** — matrix §4, *"Changing a login email
is reserved to the Office, and nobody may change their own."* That `false` is not a survivor of this
bug, and "fixing" it would hand a Customer Admin the one Employee power the matrix withholds. The
two role lists are deliberately different; do not tidy them into matching.

---

### 27. Suspending an `Invited` account, then reactivating it, produces an `Active` Accountant with no password — and it satisfies the last-Admin invariant

Found on 2026-09-02 while writing the UI implementation plans. Four handlers are each individually
reasonable and the composition is not.

| Step | Handler | What it allows |
|---|---|---|
| Suspend an `Invited` account | `SuspendAccountantHandler.cs:51-52` | Refuses only an account that is **already `Suspended`**. `Invited` passes. |
| Reactivate it | `ReactivateAccountantHandler.cs:49-55` | Requires `Status == Suspended` — which it now is. Sets `Status = Active` and **does not touch `PasswordHash`**, still `null`. |
| Accept the invitation | `AcceptInvitationHandler.cs:73` | Requires `Status == Invited`. Now `400`, with the opaque invalid-token message. The emailed link is dead. |
| Log in | `LoginHandler` | Fails: there is no hash to verify. |

So the account is `Active`, unusable, and its invitation cannot be completed. That much is merely
bad. The consequence that makes it worth ranking is the next one:

```csharp
// AccountInvariants.cs:37-40
var activeAdmins = await db.UserAccounts.CountAsync(
    account => account.Role == UserRole.AccountantAdmin
               && account.Status == AccountStatus.Active,
    ct);
```

**The invariant counts `Active`, with no condition on `PasswordHash`.** A passwordless `Active`
Admin produced by the sequence above therefore *counts as* the Active Admin that must always
remain — so the last Admin who can actually sign in becomes suspendable, and the guard passes.
That is precisely the state the comment at `AccountInvariants.cs:34-36` says the invariant exists
to prevent: *"nobody can log in, and the only role that can fix it is the one that no longer
exists."* Recovery is `forgot-password` against the passwordless account, which works but which
nobody would think to try.

**The fix is one guard, and there is a choice of where.** Refusing to suspend a non-`Active`
account in `SuspendAccountantHandler` is the narrower change and matches the `422` vocabulary
already there. Adding `&& account.PasswordHash != null` to the invariant is the deeper one and
would also catch any future path that mints a passwordless `Active` row. Both are cheap; the
second is the one that stops the class of bug rather than this instance.

> **A comment in the code asserts this is already impossible, and it is not.**
> `ReactivateAccountantHandler.cs:45-48` reads *"flipping it to Active produces a row that violates
> `ck_user_accounts_status`"*. That constraint is
> `CHECK (status IN ('Invited', 'Active', 'Suspended'))`
> (`20260901_001_CreateIdentitySchema.sql:54`) — a **vocabulary** check. It has no opinion on
> `PasswordHash` and rejects nothing here. The comment is wrong, and it is the reason the gap
> survived review: a reader checking whether the database prevents this will believe it does.
> Fix the comment in the same commit as the guard, or the next reader repeats the mistake.

**Nothing in the UI can prevent this** — it is two legitimate button presses in a legitimate order.
`Screens/IdentityScreens.md` §4.3 gates *Suspend* on `status === 'Active'`, which is what keeps the
SPA from being the thing that triggers it, and that gate is therefore **load-bearing rather than
cosmetic**. It is not a fix: the endpoint remains callable directly.

---

### 28. `/create` and `/edit` default the same two visibility flags to opposite values

`CreateTicketTypeRequestDto.cs:11-12` initialises both flags to `true`:

```csharp
public bool AllowEmployeeToOpen { get; set; } = true;
public bool AllowSubjectOtherThanCreator { get; set; } = true;
```

`EditTicketTypeRequestDto.cs:9-10` declares the same two properties with **no initialiser**, so
they default to `false`. The two DTOs sit in the same folder and are read side by side.

The consequence is specific to `/edit` being a full replacement (item 7): a client that omits
either flag from an edit payload does not "leave it unchanged", it sets it to `false` and mints a
new version with an Employee's ability to open that ticket type silently revoked. There is no
validation error, no `422`, and no diff shown to the operator — the response is a `200` with a new
`versionNumber`.

**For the UI this is handled and must stay handled:** `Screens/TicketTypesScreens.md` requires the
editor to send both flags explicitly, always, read from the loaded detail. That is why the rule is
phrased as *always send every field* rather than *send what changed*.

**The fix** is to make `/edit`'s defaults match `/create`'s, or better, to make both non-nullable
inputs the handler validates as present. Matching the defaults alone leaves the trap for the next
client — an omitted flag would then silently mean `true`, which is wrong in the other direction.

---

### 29. `null` for a trimmed string field is a `500`, not a `4xx`

`TicketTypeMapper.NormalizeTicketType` trims unconditionally:

```csharp
// TicketTypeMapper.cs:112-114 (the /edit overload; /create trims the same at :101-104)
req.DisplayName = req.DisplayName.Trim();
req.Category = req.Category.Trim();
NormalizeFields(req.Fields);          // -> field.Label.Trim(), field.GroupName.Trim()
```

The DTO initialisers are `= string.Empty`, but a JSON body containing an explicit
`"displayName": null` overwrites the initialiser with `null`, and `.Trim()` then throws
`NullReferenceException`. `AppExceptionMiddleware`'s `catch (Exception)` turns it into a
`ProblemDetails` `500`. The same applies to `Category`, and to every field's `Label` and
`GroupName`.

This contradicts a locked decision, so it is a defect rather than a preference —
`../README.md`, *Locked platform decisions*: *"Anything a client can trigger by sending a value —
an over-length string, an unparseable regex — is a `4xx`, never a `500`."* An explicit `null` is a
value a client can send.

**For the UI:** the Zod schemas never produce `null` for these fields, so a correct client never
triggers it. It is recorded because a `500` from a form submission will otherwise be investigated
as a client bug, and because the fix is a null-coalesce per field rather than anything structural.

---

### 30. `PermissionChecker` puts an internal action name into `title`, which is the field the UI renders

```csharp
// PermissionChecker.cs:63
throw new AppException($"Permission denied for action '{action}'.", 403);
```

`AppException`'s message becomes `ProblemDetails.title`, and `title` is the one field
`GeneralUIArchitecture.md` §2.3 rule F designates as the human-readable message. So the string a
user sees on a denied action is `Permission denied for action 'EditTicketType'.` — a C# catalogue
key, in a dialog, in an application whose UI copy is otherwise governed by `00-Glossary.md`.

It is not a security problem: the action names are already inferable from the UI's own affordances,
and `02-AuthorizationMatrix.md` is the public contract. It is a copy problem, and it is the only
place in the API where an internal identifier is rendered to an end user by design rather than by
accident.

**The UI cannot fix this by rewriting the message**, because §7.1 forbids matching on English prose
to decide what a response meant (item 5).

Where the leak actually surfaces is narrower than it first looks, and worth knowing precisely.
`GeneralUIArchitecture.md` §7.2 sends a **detail query**'s `403` to `AccessDeniedPage`, which renders
its own copy and never shows `title` — so the common case is already clean. But a `403` from a **row
action** or a **form submission** goes to `ErrorBanner`, which renders `title` verbatim. Those are
exactly the denials a mis-specified `can()` row produces (§6.2 rule B), so the string a builder is
most likely to see while getting permissions wrong is also the ugliest one.

**The fix** is to give `AppException` a caller-facing message and log the action name instead:
`"You do not have permission to do that."` in `title`, `{Action}` in the structured log and in the
audit entry, which already records it (`After: new { Action = action }`).

---

### 31. `EmployeeInvited` has no producer, and the two employee notifications that do fire carry no id to link to

Two separate findings in one place, both about `NotificationEvents`.

**`EmployeeInvited` is declared and never raised.** Grepping `NotificationEvents.` across every
slice returns exactly five call sites: `Invited` (`InviteAccountantHandler.cs:136`),
`PasswordResetRequested` (`RequestPasswordResetHandler.cs:112`), `AccountSuspended`
(`SuspendAccountantHandler.cs:74`), `EmployeeRegistered` (`RegisterEmployeeHandler.cs:128`) and
`EmployeeDeparted` (`DepartEmployeeHandler.cs:116`). `EmployeeInvited` is not among them, although
`InviteEmployeeHandler` is the obvious place for it and an invited Employee is exactly the person an
email is being sent to. Either the constant is dead and should say so, or the handler is missing a
notification. This is a decision, not a cleanup.

**`EmployeeRegistered` and `EmployeeDeparted` carry the person's name and not their id.** Both
producers write the name into the body and pass no `ticketId` (the only id `NotificationDto` has a
slot for). They are also, as of the item-3 correction, **the only notifications a signed-in user
receives in volume** — both go to the Customer's own Admins, who can sign in, unlike the recipients
of `Invited` and `AccountSuspended`.

So the notification centre's first real content is two kinds that cannot be given a destination
link, because there is no id to build `/employees/:employeeId` from. `Screens/NotificationsScreens.md`
§5 rule E forbids resolving the name to an id by searching the employee list — two employees may
share a name and a wrong link is worse than none. **The fix is for the producer to carry the
employee id**, which needs a payload field that is not `ticketId`; that is a schema change and is
why this is recorded rather than worked around.

---

## Drift

These change nothing about how the UI is built. They are recorded so the next reader is not
misled, and so nobody "fixes" the UI to match a document that is itself stale.

### 14. Error responses are `application/json`, not `application/problem+json`

`AppExceptionMiddleware` uses `WriteAsJsonAsync`, which sets `application/json`. RFC 7807
specifies `application/problem+json`. Nothing in the client depends on the content type — the
API client branches on `response.ok` and the status code — so this is cosmetic. It becomes
non-cosmetic if any tooling ever content-negotiates.

**File to change:** `Shared/Errors/AppExceptionMiddleware.cs`.

### 15. `/api/notifications/*` has no `.WithTags`

Every other route group tags itself (`"Auth"`, `"Accountants"`, `"Employees"`, and so on).
`NotificationsEndpoints.cs` calls `app.MapGroup("/api/notifications")` with no `.WithTags`, so
those four routes would land untagged in any generated client (item 9) and produce a
differently-named module from the other five slices.

**File to change:** `Slices/Notifications/NotificationsEndpoints.cs`.

### 16. `App:BaseUrl` has no verification path

`TokenLinks` prefixes every invitation and reset link with `App:BaseUrl`. If the value is wrong,
every emailed link points at the wrong host, and **nothing in the SPA can detect it** — the SPA
never sees the link it was reached by. `IdentityLinkOptions`' own comment notes that the failure
*"does not break anything until the first invitation email goes out with a link to nowhere, and
by then the token has been consumed by nobody and the person is stuck."*

This is a deployment check, not a UI feature. Worth surfacing on a health endpoint so a smoke
test can assert it.

### 17. `pageSize` is clamped, not rejected

`PaginatedQuery.Normalize` clamps `pageSize` to `[1, 50]` and substitutes the default of `15`
for anything `<= 0`; `pageNumber` is raised to `1`. This is correct and deliberate — it is
recorded here only because it is a trap for the client, and it is the sixth entry in
`GeneralUIArchitecture.md` §2.4.

**The client must render pagination from the `pageSize` in the response envelope, never from the
value it asked for.** A request for `pageSize=100` returns 50 rows with `pageSize: 50`, and a
client that trusted its own request computes every page boundary wrong from then on — with no
error anywhere to explain it.

No change required. Do not "fix" it into a 422; a clamp is friendlier and the envelope already
reports the truth.

### 18. `mark-read` returns `200` while auditing a `Denied` outcome

`MarkNotificationsReadHandler` filters the requested ids down to the ones the caller owns, and if
any were dropped it writes an audit entry with `AuditActions.PermissionDenied` and
`AuditOutcome.Denied` — then commits the partial update and returns `200` with `markedCount`.

Both halves are defensible on their own. Returning `404` for another recipient's notification id
would confirm the row exists, which the out-of-scope-is-404 rule exists to avoid, and returning
`403` for a batch that was mostly legitimate would discard work the caller was entitled to. The
audit entry is the record that somebody reached outside their own scope. But the combination means
**a successful-looking response can silently record a security event against the caller**, and
neither the caller nor the client can tell.

No change required, and no workaround available client-side. It is recorded because it drives a
rule that would otherwise look paranoid: `Screens/NotificationsScreens.md` §6 rule E forbids
sending ids from any source other than the current render, because a stale cached page manufactures
an audited denial with no visible symptom. A builder who "optimises" that by marking ids from the
query cache is generating false entries in the one log an investigator is supposed to trust.

---

### 24. `HelpTextMaxLength` is declared and never used

`TicketTypeMapper.cs`:94 declares `private const int HelpTextMaxLength = 10_000;`. No validator
reads it. `ValidateDescription` uses `DescriptionMaxLength` — declared on the line immediately
above, with the shared comment explaining why both exist — and `ValidateFields` length-checks
`label`, `groupName`, `regexPattern`, both `conditionalVisibility` members and the joined
`allowedFileTypes`, never `helpText`.

The column is `TEXT`, which PostgreSQL does not bound, and correction note `TicketTypes` T-11
states the intent explicitly: *"unbounded input on a table nothing ever purges is still a
mistake — cap it explicitly."* The constant is the cap; the call was never added.

**File to change.** `Slices/TicketTypes/Application/TicketTypeMapper.cs` — one
`RequireLength(field.HelpText, HelpTextMaxLength, $"HelpText of field '{field.Key}'")` inside the
`ValidateFields` loop. It cannot reject anything already stored, because nothing has been stored.

Filed as drift rather than degrading because the UI is unaffected: `GeneralUIArchitecture.md` §9.2
mirrors the 10,000 client-side regardless, on the reasoning that a client cap on a limit the
server forgot costs nothing when no legitimate value comes near it. This is one of only two rows
in that table where the client is deliberately stricter than the server; the other is item 19.

---

### 25. Two statements in the `Audit` implementation plan describe behaviour the code does not have

Both are stale plan text, and in both cases **the code is right**. Recorded so nobody edits
working code to match the plan.

**`Slices/Audit/IMPLEMENTATION_PLAN.md` §2.1 implies redaction happens on read.** It describes
`AuditEntryDto` as the read model *"with redaction applied"*. Redaction is applied at **write**
time: `AuditApi.WriteAsync` calls `Redaction.ToJson(entry.Before, _logger)` on the way into
`_db.AuditEntries.Add(...)`, so the stored column is already redacted and the read path does not
redact at all. `AuditEntryDetailDto`'s own doc comment says so. Write-time is the correct choice —
it means an unredacted secret never reaches the table, where read-time redaction would leave it
sitting in a row nothing ever purges, protected only by the projection.

**§6.3 specifies two lists from the action-codes endpoint; the shipped DTO returns three.**
`AuditActionsResponseDto` carries `Actions`, `TargetKinds` **and** `Outcomes`. The code is a
superset and is the better behaviour: the search endpoint `422`s an unrecognised outcome, so a
client holding its own hardcoded copy of the outcome vocabulary would eventually `422` itself
against a server that had moved on. `Screens/AuditScreens.md` §5 depends on the third list. Fix the
plan, not the DTO.

**One live deployment consequence sits next to these, and is not drift.** `AuditRecord.SourceIp` is
`_http.HttpContext?.Connection.RemoteIpAddress`. `Program.cs`:82-104 does configure
`UseForwardedHeaders`, but `KnownProxies`/`KnownIPNetworks` are read from configuration and, when
both are empty, the app **logs a warning and continues** rather than failing. So in production
behind Caddy, `sourceIp` is the real client address only if
`ForwardedHeaders__KnownNetworks__0` is set to the compose network subnet — and the compose file
and `.env.example` that would set it do not exist yet (item 2). Until that is verified,
`Screens/AuditScreens.md` deliberately labels the column `sourceIp` and **not** "the user's IP
address", because a column that is uniformly the proxy's address while captioned as the user's is
worse than no column.

---

### 32. Three endpoints omit a status code they can actually return

Found while cross-checking each screen spec's error handling against the `.Produces` declarations.

| Route | Returns | Declared? | Where |
|---|---|---|---|
| `POST /api/ticket-types/edit` | `409` on a duplicate display name | **No** — declares `403`, `404`, `422` only | `EditTicketTypeHandler.cs:72` vs `TicketTypesEndpoints.cs:31-35` |
| `GET /api/customers/own` | `403` for an Accountant | **No** — declares `401`, `404` | `GetOwnCustomerHandler.cs:24` vs `CustomersEndpoints.cs:53-61` |
| All three `/api/audit/*` routes | `401` when anonymous | **No** | `AuditEndpoints.cs:16-44` |

The `/api/customers/own` case is the instructive one, because the declaration is not merely
incomplete — it is misleading about *which* code fires. `RequireAsync(user, "ViewOwnCustomer")` runs
**before** the `CustomerId`-is-null check, and the action is granted to `CustomerAdmin` and
`Employee` only, so an Accountant is refused with `403` and the declared `401` on the following line
is unreachable for them. A client written from the declaration would route an Accountant through its
session-expired path and sign them out.

This costs the UI nothing today — `Screens/CustomersScreens.md` §6.4 rule A now states the real
behaviour, and no screen routes an Accountant to `/my-customer` anyway. It matters for item 9: a
generated client would inherit all three gaps, and the `401`/`403` one would generate a wrong
control flow rather than just a missing union member.

---

### 33. Two Customer handlers omit the scope filter, and are safe only because of who may call them

`SuspendCustomerHandler.cs:47` and `ReactivateCustomerHandler.cs:45` load the Customer by primary
key without `WhereMatchesCustomerScope(user)`, which every other read in the slice applies.

They are not exploitable today: both actions are `AccountantAdmin`-only, and an Accountant's scope
is every Customer, so the filter would be a no-op. The concern is structural — the safety currently
rests on the catalogue entry rather than on the query, so widening either action to a Customer-side
role (which `02-AuthorizationMatrix.md` does not do, but a future change might) turns a
one-line permission edit into a cross-tenant read. Every sibling handler in the slice is written the
other way, which is what makes these two look like oversights rather than decisions.

Adding the filter costs nothing and removes the coupling. **Nothing in the UI depends on this**; it is
here because "safe because of the caller, not the query" is the shape of defect that survives until
the caller changes.

---

## Not on this list, and why

Two things a reader may expect to find here.

**Customer-side screens are not blocked.** An earlier draft of this punch-list claimed there
was no way to create a `CustomerAdmin` or an `Employee`, and therefore that every Customer-side
screen was unreachable and untestable. **That was wrong.** The `Employees` slice is built and
wired (`Program.cs` lines 52 and 153) and exposes `POST /api/employees/invite` and
`POST /api/customers/onboard`. `/api/accountants/invite` does reject `CustomerAdmin` and
`Employee` with a 422 — but that endpoint invites Accountants, and the other two exist for
exactly this purpose. Every role in the system can be created and logged in as today.

The reason the earlier draft got it wrong is worth knowing: `Slices/Employees/` is **untracked
in git**. A survey of committed files does not see it. Check the working tree, not `git
ls-tree`.

**Ticket, Document and Ticket-detail screens are not on this list.** Both slices are built and routed
(`Program.cs`:59, :65, :157). There is no `Screens/TicketsScreens.md` and no `DocumentsScreens.md` to transcribe
into a UI plan, so their screens are out of scope for this pass by decision, not by defect. See `UI/README.md`.

---

## Ranking, for whoever is prioritising

1. **Items 1, 2, 3** — the SPA cannot be deployed at all. Item 1 is three lines.
2. **Item 27** — the only item that can reach a state where **nobody can administer the instance**.
   It defeats the one invariant written specifically to prevent that, the code carries a comment
   asserting the database prevents it when the database does not, and the fix is a single guard. No
   UI change can mitigate it. Rank it first among the non-deployment items for that reason, not for
   its likelihood — it needs two deliberate button presses in an unusual order.
3. **Item 28** — two initialisers, and until they match, an edit that omits a flag **silently
   revokes a role's access** and mints a version recording it as intended. Cheap, and the kind of
   loss nobody reports because it looks like a decision somebody made.
4. **Item 12** — the only item here that **destroys data on a `200`**, and it costs one small
   endpoint (`/api/employees/get-own`) to close. It also unblocks the `/profile` screen for the two
   Customer-side roles, which is currently specified as read-only for this reason alone.
5. **Item 33** — two missing `.WhereMatchesCustomerScope()` calls. Nothing exploits it today
   because no route reaches those handlers with a scoped caller, so it is a **latent** hole rather
   than an open one; it is ranked this high only because the fix is two lines and the next person
   to add a Customer-side suspend route will not think to add them.
6. **Item 8** — one line, and it blocks every API call in development.
7. **Item 5 (`code` extension only)** — removes the one place the client matches on English prose.
8. **Item 4** — cheap in the API, but it must ship in the same deploy as the SPA change.
9. **Item 7** — loses user data too, but silently and rarely: Ticket Types are edited by few people.
   Ranked below item 12 because item 12's loss is a single misclick by any Employee.
10. **Item 19** — cheap, and it is the only item that can silently reshape a submitted payload.
    The trim is one line and should go in regardless; the character-set check needs a look at
    existing rows first, so it can follow separately.
11. **Item 30** — one string. The message it leaks is an internal action code, not data, but it
    reaches an `ErrorBanner` verbatim, which makes it the only place the UI shows the user a word
    from the permission catalogue. Fix it before anyone screenshots it.
12. **Item 20** — one word, and it is the difference between a working filter and a list that
    looks broken. Free today: nothing consumes the parameter yet.
13. **Item 29** — the mapper drops a field the detail screen needs. It is a small change, but it
    is a *response-shape* change, so it must land before the screen is written rather than after.
14. **Items 6, 9, 10, 11, 13, 21, 22, 23, 31** — real, and none of them blocks a screen. Items 22
    and 23 are what make the audit screen investigative rather than merely complete; item 21 is
    the difference between version history and a version *picker*. Item 31 is the only one of this
    group that needs a decision rather than code: either the producer carries the employee id or
    the notification stays unlinked, and the UI spec has already assumed the latter.
15. **Items 14–18, 24, 25, 32** — record and move on. Items 18, 25 and 32 need no behaviour change
    at all; 18 justifies a rule that would otherwise look paranoid, 25 exists to stop somebody
    "fixing" correct code to match a stale plan, and 32 is a `.Produces` annotation that only
    matters once an OpenAPI document is generated (item 10). Item 25's `ForwardedHeaders` note is
    the exception — that one is real, and it ships with item 2.

**Item 26 is resolved** and is deliberately absent from this ranking. It was ranked second here
until 2026-09-02. Do not re-add it; see the item itself for what to verify before trusting that,
because the fix is in the working tree and `Slices/Employees/` is untracked.
