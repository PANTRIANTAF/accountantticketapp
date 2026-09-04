# Ticket Type Screens, and the Dynamic Form Renderer

This slice has two audiences and they want opposite things. **Accountants author** Ticket Types: they invent field keys, pick data types, write validation rules, and mint a new immutable version every time they save. **All four roles read** them: a Ticket Type is the schema a ticket form is generated from, and [../../00-Glossary.md](../../00-Glossary.md) defines it as *"the template that defines what a Ticket of that kind contains"*. The authoring half is three screens and a large form. The reading half is one component, and it is the most reused component in the application.

The reading half has no consumer in this specification yet. `Tickets` is built, registered and routed; what it lacks is a UI plan and a screen document ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §0.1), so nothing in this pass renders a ticket. But `UI/TicketArchitecture` describes exactly what the renderer is for: *"When a user opens a ticket he choose from a dropdown what type of ticket it is. On selection a call to the app is triggered to fetch a json with the fields of the ticket. Then the view is rendered according to this fields."* That json is `TicketTypeDetailDto.Fields`. The renderer that turns it into controls is therefore built **now**, against the endpoint that already serves it, and it lives in `frontend/src/shared/dynamicForm/` — not in `slices/ticketTypes/` — because `shared/` may never import from `slices/` ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §1.4 rule A) and `slices/tickets/` will need it. A renderer written inside this slice's folder is a renderer that `Tickets` has to either import illegally or copy.

The third thing to absorb before writing any of it: **the server already filters, and the renderer must not.** `TicketTypeMapper.ToDetail` strips Accountant-only fields for Customer-side callers, `IsDiscoverableBy` hides deactivated types from them with a `404`, and `IsInAudienceOf` hides types an Employee may not open. All three are server-side, tested, and load-bearing. §6.8 exists because re-implementing any of them in the client is the one change to this specification that would make the application *less* safe while looking more careful.

**Documents that govern this one, in precedence order.** Where any of them disagrees with this document, **they win and this document is wrong** — fix this document, do not code around it.

- [../../README.md](../../README.md) — *Locked platform decisions*, *Conflict precedence*, and the out-of-scope-is-`404` rule
- [../../00-Glossary.md](../../00-Glossary.md) — *Ticket Type*, *Field Descriptor*, *Field Value*; binding in UI copy
- [../../01-DomainModel.md](../../01-DomainModel.md) §9.7 — concurrency, and why `ticket_types` has no version column
- [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §1, §5, §11, §12 — normative
- [../../04-Infrastructure.md](../../04-Infrastructure.md) §1–3 — hosting and the dev loop
- [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) — the governing UI document. §2.3 rules C and J, §3.1, §3.2, §7.3, §8.2, §8.4, §9.2, §9.3, §9.4, §10.1, §10.2, §12 are cited throughout and **not restated**. **§9.4 is central to §5 of this document.**
- [../../Slices/TicketTypes/IMPLEMENTATION_PLAN.md](../../Slices/TicketTypes/IMPLEMENTATION_PLAN.md) — the backend plan, including its correction notes T-4, T-7, T-11 and T-13

---

## 0. Role coverage, and what the server already does for you

[../../README.md](../../README.md)'s *Not yet written* list has **no ticket-type entry**. Its closest brief is *"Employee: new ticket"*, which is blocked. So the screens below are not in the brief; they exist because the type catalogue has to be authorable and because the renderer that unblocks *new ticket* is specified here.

| Audience | Role | Screen | Notes |
|---|---|---|---|
| Author | AA, AU | §5 editor | Identical for both. Authoring is deliberately **not** Admin-only ([../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §5) |
| Author | AA, AU | §3 list, §4 detail | Sees inactive types; sees Accountant-only fields; sees the *Edit* and *Deactivate* actions |
| Reader | CA, EMP | §3 list, §4 detail | Sees only active, in-audience types, with Accountant-only fields already absent |
| Reader | all four | §6 renderer | Consumed by `Tickets` later; exercised in this pass by §4's read-only preview |

### 0.1 Five filters the server applies, so the client does not

Read these before §6.8, which forbids duplicating them.

| Filter | Where | Effect on the client |
|---|---|---|
| Accountant-only fields stripped for CA/EMP | `TicketTypeMapper.ToDetail`, `fields.Where(f => f.IsVisibleToCustomer)` | `Fields` is already the caller's complete list. There is nothing left to filter |
| Deactivated type hidden from CA/EMP on **discovery** | `IsDiscoverableBy` via `ApplyCustomerSideVisibility`, used by `/detail` | `404`, not an empty response, not a flag |
| `AllowEmployeeToOpen == false` hidden from EMP | `IsInAudienceOf`, on `/detail` **and** `/version` | `404` |
| Deactivated type's old versions **stay readable** | `/version` applies only `IsInAudienceOf` (correction note T-4) | `/version` succeeds where `/detail` returns `404`. See §7.3 |
| Inactive types excluded from CA/EMP lists; `activeOnly` ignored for them | `ListTicketTypesHandler` | Never send `activeOnly` for a Customer-side caller; it does nothing |

The permission rows are already in [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §6.1 and match `TicketTypesActionCatalogue.cs` cell for cell: `CreateTicketType`, `EditTicketType`, `ToggleTicketType` are `[AA, AU]`; `ReadTicketType` and `ListTicketTypes` are all four. Do not add a row and do not re-derive them.

---

## 1. Endpoints this slice consumes

Roles use the abbreviations from [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md): AA = `AccountantAdmin`, AU = `AccountantUser`, CA = `CustomerAdmin`, EMP = `Employee`. Read from `Slices/TicketTypes/TicketTypesEndpoints.cs`; nothing below is inferred.

| Route | Verb | Request | Response | Roles | Notes |
|---|---|---|---|---|---|
| `/api/ticket-types/create` | POST | `CreateTicketTypeRequestDto` (body) | `TicketTypeDetailDto`, **`201`** | AA, AU | `Location:` header is set. Note 1 |
| `/api/ticket-types/edit` | POST | `EditTicketTypeRequestDto` (body) | `TicketTypeDetailDto`, `200` | AA, AU | `Fields` is a **full replacement** that mints a version. Note 2 |
| `/api/ticket-types/toggle` | POST | `ToggleTicketTypeRequestDto` (body) | `TicketTypeDetailDto`, `200` | AA, AU | Field is `newIsActive`. Idempotent. Note 3 |
| `/api/ticket-types/list` | **GET** | query `pageNumber?`, `pageSize?`, `activeOnly?` | `PaginatedResponse<TicketTypeListItemDto>` | AA, AU, CA, EMP | All three optional. Note 4 |
| `/api/ticket-types/detail` | **GET** | query `ticketTypeId` (**required**) | `TicketTypeDetailDto` | AA, AU, CA, EMP | Always the current version. Note 5 |
| `/api/ticket-types/version` | **GET** | query `ticketTypeId`, `versionNumber` (**both required**) | `TicketTypeDetailDto` | AA, AU, CA, EMP | Skips the `IsActive` check. §7.3 |

**Notes.**

1. `create` returns `201` with `Location: /api/ticket-types/detail?ticketTypeId=<id>`. **Do not follow the header.** The body is the full `TicketTypeDetailDto`; fetching the location is a second round trip for data you already hold, and it re-reads through `ToDetail` a version you just received. Seed the cache from the body ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §3.2 rule D).
2. `EditTicketTypeRequestDto` has **no `Code` property.** Sending one is silently ignored by the JSON binder — no `400`, no warning. See §5.2.
3. `toggle` with `newIsActive` equal to the current state returns `200`, writes no audit entry, and changes nothing. Harmless, but it means a successful response is not evidence that anything moved; render from the returned `isActive`, never from what you sent.
4. **These are `GET`s with query parameters, and the three parameters are nullable server-side** (`int? pageNumber, int? pageSize, bool? activeOnly`). Omitting them yields page 1 of 15. [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §2.3 rule C's list of `POST` reads does **not** include any route in this slice — do not "make it consistent" with `/api/employees/list`; a `POST` here returns `405`.
5. `detail` and `version` take **non-nullable** `Guid` / `int` query parameters, unlike `list`. A missing or unparseable `ticketTypeId` is a `400` from the model binder before any handler runs, so its `title` is framework wording, not the slice's. Guard in `api.ts`: never call `getTicketType` with an empty string from a route param that has not resolved yet — use TanStack Query's `enabled` for that ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §3.2 rule B).
6. `pageSize` is **clamped to 50, not rejected** ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §2.4 item 6). Render the pager from `response.pageSize`.

### 1.1 `activeOnly` is a three-state filter and `false` does not mean "all"

`ListTicketTypesHandler` reads it as `query.Where(t => t.IsActive == req.ActiveOnly.Value)` and only when `HasValue`. So:

| Sent | Accountant sees | Customer-side sees |
|---|---|---|
| omitted | active **and** inactive | active only (forced) |
| `true` | active only | active only |
| `false` | **inactive only** | active only |

The filter control on §3 must therefore be a three-option toggle — *All* / *Active* / *Inactive* — where *All* **omits the parameter entirely**. A two-state checkbox labelled "Active only" that sends `false` when unticked shows an Accountant nothing but deactivated types, which reads as "the catalogue is empty" on a screen whose empty state also says that.

### 1.2 Query keys

Per [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §3.1, exactly these:

| Query | Key |
|---|---|
| List | `['ticketTypes', 'list', { pageNumber, pageSize, activeOnly }]` |
| Detail (current version) | `['ticketTypes', 'detail', ticketTypeId]` |
| One version | `['ticketTypes', 'version', ticketTypeId, versionNumber]` |

`activeOnly` appears in the key even when `undefined`, so the three states of §1.1 cannot share a cache entry. A create, edit or toggle seeds `['ticketTypes','detail', id]` from the response body and invalidates `['ticketTypes','list']`. **A version key is never invalidated by anything** — `ticket_type_versions` rows are immutable by design ([../../Slices/TicketTypes/IMPLEMENTATION_PLAN.md](../../Slices/TicketTypes/IMPLEMENTATION_PLAN.md) §10 item 3), so a version query may use an unbounded `staleTime`. Invalidating it instead is a refetch that can only ever return the identical bytes.

---

## 2. Routes and screens

| SPA path | Screen | Roles |
|---|---|---|
| `/ticket-types` | `TicketTypeListScreen` | AA, AU, CA, EMP |
| `/ticket-types/new` | `TicketTypeEditorScreen` (create mode) | AA, AU |
| `/ticket-types/:ticketTypeId` | `TicketTypeDetailScreen` | AA, AU, CA, EMP |
| `/ticket-types/:ticketTypeId?version=N` | `TicketTypeDetailScreen` (historical) | AA, AU, CA, EMP |
| `/ticket-types/:ticketTypeId/edit` | `TicketTypeEditorScreen` (edit mode) | AA, AU |

These are the four rows already in [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §4.1, unchanged. The historical view is a **query parameter on the detail route, not a fifth route**, because a version is a lens on one resource rather than a resource of its own, and because `?version=` survives a bookmark while keeping one screen component and one `RequireRole` wrapper. `/ticket-types/new` must be declared **before** `/ticket-types/:ticketTypeId` in `routes.tsx`; declared after, `new` matches the parameterised route, `ticketTypeId` becomes the literal string `"new"`, and the detail query fires a `400` that reads like a broken link.

---

## 3. Screen: Ticket type list (`/ticket-types`)

**File:** `frontend/src/slices/ticketTypes/screens/TicketTypeListScreen.tsx`

The one list in the application every role can reach. `PaginatedTable` ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §8.2), never a hand-rolled `Table` + `TablePagination`.

### 3.1 Columns

`TicketTypeListItemDto` has six properties and no others. Do not display a field it does not carry.

| Column | Source | Notes |
|---|---|---|
| Display name | `displayName` | The row link, to `/ticket-types/:id` |
| Code | `code` | Monospaced. Immutable, so it is the stable human handle |
| Category | `category` | Plain text. **Not** a grouping — see rule B |
| Status | `isActive` | `StatusChip`. See rule C |
| Version | `currentVersionNumber` | Rendered as `v3`, not `3` |

**A. There is no `description`, `createdAt`, `updatedAt` or field count on the list DTO.** Adding a column for any of them means an N+1 of `/detail` calls behind a table. Put them on §4.

**B. Do not group or sort by `category` in the client.** The server orders by `DisplayName, Id` and pages *after* ordering, so a client-side regroup of one page produces category headings that appear and disappear as the user pages — and a category whose members straddle a page boundary renders as two separate sections with the same name. Category is a text column until a server-side grouping or filter parameter exists (§10).

**C. `isActive` is a `bool`, not one of the glossary status strings.** `StatusChip` handles `Active`/`Suspended`/`Invited`/`Departed` ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §8.3). Map `true → "Active"` and `false → "Inactive"` and pass the word; do not invent a `Suspended` chip for a Ticket Type, because "suspended" is a Customer and account state in [../../00-Glossary.md](../../00-Glossary.md) and reusing it here means two different things wear one colour.

### 3.2 Affordances

| Affordance | Gate | Placement |
|---|---|---|
| *New ticket type* | `can(role, 'CreateTicketType')` | `PageHeader` action slot |
| Row menu: *Edit* | `can(role, 'EditTicketType')` | Row overflow menu |
| Row menu: *Deactivate* / *Reactivate* | `can(role, 'ToggleTicketType')` | Row overflow menu, below a divider |
| *All* / *Active* / *Inactive* filter | Accountant roles only | Above the table |

The filter is hidden for CA and EMP rather than disabled: the server ignores the parameter for them (§1.1), so a visible control that demonstrably does nothing is worse than no control ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §6.2 rule C).

Empty states, per [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §7.4: "No ticket types yet" plus *New ticket type* for an Accountant; the sentence alone for CA and EMP, who cannot create one. And when `totalCount > 0 && items.length === 0`, the over-run message with *back to the first page* — not "no results".

### 3.3 The deactivate confirmation

**File:** `frontend/src/slices/ticketTypes/components/ToggleTicketTypeDialog.tsx`

`ConfirmDialog` ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §8.3). Deactivation is reversible, so it is not styled as destructive — but it is invisible from the other side of the Customer boundary, and the dialog must say all four of these or an Accountant will not predict what happens:

1. **Customer Admins and Employees stop seeing the type entirely.** Not greyed out, not "closed for new tickets" — absent. `/detail` answers them `404` ([../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §5: *"is **not** returned by the API at all… not greyed out in the UI"*).
2. **Existing tickets are unaffected** and still render, because `/version` deliberately keeps working (§7.3).
3. **Nothing is deleted.** Reactivating restores exactly the previous state; the type keeps its version number and its history.
4. **Accountants can still see and edit it.** It stays in an Accountant's *All* and *Inactive* lists, which is the only way back — a type that vanished from every list could never be reactivated.

Reactivation needs no confirmation: it only makes something visible again. Both call `toggle` with `newIsActive` and seed the detail cache from the response.

---

## 4. Screen: Ticket type detail (`/ticket-types/:ticketTypeId`)

**File:** `frontend/src/slices/ticketTypes/screens/TicketTypeDetailScreen.tsx`

Reads `['ticketTypes','detail',id]` when there is no `?version=`, and `['ticketTypes','version',id,n]` when there is. Both return the same `TicketTypeDetailDto`, so one render path serves both. `404` renders `NotFoundPage` ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §7.2), with the wording "Not found" and never "forbidden" — for a Customer-side caller on a deactivated type, `404` is the *designed* answer, not a fault (§2.3 rule J).

### 4.1 Regions, in order

1. **Header** — `displayName`, `code`, `StatusChip`, `v{currentVersionNumber}`, and the *Edit* / *Deactivate* actions gated by `can()`.
2. **Version banner** — only when `versionNumber !== currentVersionNumber`. §7.2.
3. **Summary** — `description`, `category`, `createdAt`, `updatedAt`. Timestamps through `format/dates.ts`; these are `DateTime` values that may arrive **with no offset suffix** and are UTC regardless ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §10.2).
4. **Behaviour flags** — `allowEmployeeToOpen` and `allowSubjectOtherThanCreator`, as labelled yes/no rows with one sentence each. `allowSubjectOtherThanCreator` has **no consumer: no handler anywhere reads it**; show it anyway and say so, because an Accountant setting it needs to see that it was stored.
5. **Fields table** — one row per `Fields` entry: key, label, data type, group, `displayOrder`, required, visible-to-Customer, and a rendered summary of `validation` and `conditionalVisibility`.
6. **Form preview** — `<DynamicForm mode="preview" fields={detail.fields} />`. The only place the renderer is exercised before a `Tickets` UI exists, and therefore the only way §6 gets tested at all in this pass. It submits nowhere; a preview has no ticket, and this specification has no ticket-submit path.

### 4.2 Rules

**A. Show `isVisibleToCustomer` as a badge on the fields table, for Accountants.** It is the author's control and the author needs to see its effect. It is **never** a filter here — the array an Accountant receives already contains every field, and the array a Customer-side caller receives already contains none of the hidden ones. See §6.8.

**B. Do not label the badge "hidden".** Write *"Accountant only"*, matching [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §5's *"Accountant-only Field Descriptors"*. "Hidden" invites the reading that the value is hidden from Customers but collected from them, which is the opposite of what happens.

**C. A CA or EMP viewing this screen must not be told that fields were stripped.** No "3 fields not shown" line, no count that disagrees with the rows. The count comes from `detail.fields.length`, which is already the caller's truth. Deriving a total from anywhere else would require a number the API does not send, and inventing one leaks the existence of what was stripped.

**D. The preview is read-only-ish, not disabled.** `mode="preview"` renders live, focusable controls with no submit button, because a disabled form cannot demonstrate that a `conditionalVisibility` rule works — and that is the single thing an author most needs to check before saving. Nothing typed into it is persisted or read back.

**E. Version stepping is by number, and that is the whole of it.** There is no version-history endpoint (§10 item 1). Offer *Previous version* / *Next version* buttons bounded by `1` and `currentVersionNumber`, both derivable from the response you already have. Do not fabricate a history list by looping `/version` from `1` to `currentVersionNumber` — that is N requests to build a list the server could return in one, and on a type edited fifty times it is fifty round trips on page load.

---

## 5. Screen: Ticket type editor (`/ticket-types/new` and `/:ticketTypeId/edit`)

**File:** `frontend/src/slices/ticketTypes/screens/TicketTypeEditorScreen.tsx` **File:** `frontend/src/slices/ticketTypes/components/FieldDescriptorEditor.tsx` **File:** `frontend/src/slices/ticketTypes/components/ChoiceOptionsEditor.tsx` **File:** `frontend/src/slices/ticketTypes/components/ValidationRulesEditor.tsx` **File:** `frontend/src/slices/ticketTypes/schemas.ts`

One component, two modes, chosen from `useParams().ticketTypeId`. React Hook Form with `useFieldArray` for `fields`, `zodResolver`, `mode: 'onBlur'` ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §9.3 rule A).

### 5.1 The three things that make this form dangerous

1. **`Fields` is a full replacement that mints a new version.** `EditTicketTypeHandler` builds `newVersion.FieldDescriptors` from `req.Fields` and nothing else. Submit four of five fields and the fifth is gone from v-next — with a `200 OK` and no warning anywhere. So the editor **must load the current version's complete `fields` array into form state and submit all of it**, every time. Never build the payload from RHF's `dirtyFields`, never omit a row the user did not touch, and never lazy-load field rows behind an accordion that has not been opened.
2. **Every save is a new version, including a save that changed nothing.** There is no no-op path on `/edit`. Pressing *Save* twice produces v4 and v5 with identical descriptors. Disable *Save* while the mutation is pending (§9.3 rule B) and, in edit mode only, keep it enabled otherwise — but the success snackbar must name the version: *"Saved as version 4."* Silent success on an operation that increments a counter is how a catalogue reaches v30 by accident.
3. **There is no concurrency control.** §5.6.

### 5.2 `code` is immutable and absent from the edit DTO

| Mode | Control | Why |
|---|---|---|
| Create | editable `TextField`, required, ≤100 | The only chance to set it |
| Edit | **read-only** text, rendered as a labelled value with a lock icon and the note *"A ticket type's code never changes."* | `EditTicketTypeRequestDto` has no `Code` property |

Render it read-only rather than disabled-but-present-in-the-payload, and **do not include `code` in the edit form's Zod schema or its submitted object.** A `TextField` bound to a value the server throws away is a control that accepts an edit, reports success, and shows the old value again after the cache is seeded from the response — the user concludes the save silently failed. The server will not help: an unknown JSON property is ignored by `System.Text.Json`'s default binding, so there is no `400` to catch.

### 5.3 The type-level form

| Control | DTO property | Rules |
|---|---|---|
| Code | `code` (create only) | Required, ≤100, trimmed. `409` on duplicate — case-**insensitive** server-side |
| Display name | `displayName` | **Required, non-blank**, ≤255, trimmed |
| Category | `category` | **Required, non-blank**, ≤100, trimmed |
| Description | `description` | Optional, ≤10,000, trimmed. Multiline |
| Allow Employee to open | `allowEmployeeToOpen` | `Switch`. Note under it: turning it off hides the type from every Employee's list **and** returns `404` on their reads |
| Allow subject other than creator | `allowSubjectOtherThanCreator` | `Switch`. Note: stored, but no handler reads it, so no effect |

`displayName` and `category` are non-blank because `TicketTypeMapper.RequireNonBlank` rejects `""` with a `422`. `code` is checked separately in `CreateTicketTypeHandler` with `IsNullOrWhiteSpace`. `description` has a real 10,000 limit (`ValidateDescription`).

### 5.4 The field rows

Each row edits one `CreateFieldDescriptorDto`. Rows are `useFieldArray` entries.

| Control | Property | Rules |
|---|---|---|
| Key | `key` | Required non-blank, ≤100, **unique case-insensitively** within the form, **trimmed by the client** (§5.5 rule C) |
| Label | `label` | ≤255, trimmed |
| Help text | `helpText` | ≤10,000 client-side. See the callout in §5.5 |
| Data type | `dataType` | `Select` over the eleven strings in `FieldDataTypes.cs`. Never a free-text box |
| Display order | `displayOrder` | Integer. Renumbered densely on reorder (rule E) |
| Group name | `groupName` | ≤100, trimmed. Blank means the leading unnamed group (§6.6) |
| Required | `isRequired` | `Switch`, defaults `true`, matching the DTO |
| Accountant only | `isVisibleToCustomer` | `Switch`, defaults `true`. Inverted label: *"Accountant only"* is `isVisibleToCustomer === false`. Show the raw property name in a tooltip so the code and the copy are reconcilable |
| Choice options | `choiceOptions` | Present **only** for `SingleChoice`/`MultipleChoice`; ≥2 rows; each row is `{label, value}` |
| Validation | `validation` | Only the members the data type can use (§6.4) |
| Shown only when | `conditionalVisibility` | Two controls: a field `Select` and a value control (rule D) |

### 5.5 Rules for the editor

**A. At least one field, or the server returns `"At least one field is required."`** Block submit in Zod; do not let a user compose a nine-field type, delete them all by mistake, and learn from a banner.

**B. Choice options: exactly `≥2` for `SingleChoice` and `MultipleChoice`, exactly `0` for everything else.** Both directions are enforced by `ValidateFields` and both return `422`. The second direction is the one that bites: **changing a field's `dataType` away from a choice type must clear `choiceOptions`**, or the row still carries the two options it had and the save fails with `"Non-choice field 'x' cannot have choice options."` — a message that names a field the user just fixed. Symmetrically, changing *to* a choice type must seed two blank option rows, and changing the data type must clear every `validation` member the new type cannot use (§6.4), because `minValue` left behind on a `SingleLineText` field is stored, is meaningless, and will be applied by a future renderer that trusts it.

**C. Trim `key`, `helpText` and every choice option in the client.** Cite [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §9.3 rule E, and then the specific reason below.

> **The server trims `label` and `groupName` and nothing else on a field.** `TicketTypeMapper.NormalizeFields` trims exactly those two properties; `NormalizeTicketType` additionally trims `code`, `displayName` and `category`. **`key` is not trimmed.** `ValidateFields` rejects a whitespace-only key via `IsNullOrWhiteSpace`, but `" key "` passes, and the uniqueness `HashSet` is `OrdinalIgnoreCase` — case-insensitive and whitespace-**sensitive**. So `"key"` and `"key "` are two distinct fields in one version, both stored, both rendered, indistinguishable on screen, and ambiguous to whatever resolves a Field Value by key in `Tickets`. Correction note T-13 fixed exactly this class of bug for `label` and `groupName` and did not extend to `key`. Flagged in §10 item 3; until it is fixed the client trimming `key` is the only thing preventing it.

**D. `conditionalVisibility` needs two controls, and the value one is not a text box.** The field `Select` offers **only the other fields in the form** — `ValidateFields` rejects a self-reference and a dangling reference with `422`, so a free-text field key is a guaranteed round trip for a typo the client could have prevented. The value control depends on the *referenced* field's data type, because the comparison is a string equality against a coerced value (§6.5):

| Referenced field's `dataType` | Value control |
|---|---|
| `YesNo` | `Select` with exactly two options, values `"true"` and `"false"` |
| `SingleChoice`, `MultipleChoice` | `Select` over the referenced field's option **`value`s** (never its labels) |
| any other | `TextField`, ≤500 |

A free-text value box here is the highest-yield authoring mistake in the slice: an author types `Yes` against a `YesNo` field, the server accepts it — it validates the *reference*, never the *value* — and the dependent field never appears for anybody, forever, with no error on any screen.

**E. Reordering renumbers `displayOrder` densely from 0.** Move-up/move-down buttons, or drag if you like, but on every change rewrite every row's `displayOrder` to its array index. Array position is not persisted anywhere; `displayOrder` is the only ordering the server stores, and `ToDetail` re-sorts by it on the way out. A reorder that leaves the old numbers renders in the old order after a reload while showing the new order until then.

**F. The Zod schema mirrors `TicketTypeMapper.cs` exactly** — no stricter, no looser ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §9.2).

| Rule | Limit | Constant in `TicketTypeMapper.cs` |
|---|---|---|
| `code` | non-blank, ≤100 | `CodeMaxLength` |
| `displayName` | non-blank, ≤255 | `DisplayNameMaxLength` |
| `category` | non-blank, ≤100 | `CategoryMaxLength` |
| `description` | ≤10,000 | `DescriptionMaxLength` |
| field `key` | non-blank, ≤100, unique case-insensitively | `FieldKeyMaxLength` |
| field `label` | ≤255 | `FieldLabelMaxLength` |
| `groupName` | ≤100 | `GroupNameMaxLength` |
| `validation.regexPattern` | ≤500, and must compile | `RegexPatternMaxLength` |
| `validation.allowedFileTypes` | ≤500 **joined with commas** | `AllowedFileTypesMaxLength` |
| `conditionalVisibility.value` | ≤500 | `ConditionalValueMaxLength` |
| `conditionalVisibility.fieldKey` | ≤100 | `FieldKeyMaxLength` |
| `fields` | length ≥1 | `ValidateFields` |
| `choiceOptions` | ≥2 for choice types, 0 otherwise | `ValidateFields` |
| ranges | `minLength ≤ maxLength`, `minValue ≤ maxValue`, `earliestDate ≤ latestDate` | `ValidateFields` |

`allowedFileTypes` is validated on the **joined** string, so cap `values.join(',').length` at 500 and not each entry — a client that checks each entry accepts sixty short extensions and gets a `422` naming a limit every individual value is inside.

> **`helpText` has no server-side length check, despite [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §9.2 listing it at ≤10,000.** `TicketTypeMapper` declares `HelpTextMaxLength = 10_000` and **never uses it**; `ValidateFields` length-checks `label`, `groupName`, `regexPattern`, the conditional-visibility pair and the joined `allowedFileTypes`, and not `helpText`. The column is `TEXT`, so an over-long value does not `500` — it is simply stored, permanently, on a table nothing ever purges (correction note T-11 is about exactly this). Mirror the 10,000 anyway: it is the documented intent, the constant exists, and a client cap is currently the only cap. Flagged in §10 item 2.

**G. `regexPattern` must compile in the browser before submit**, with the same `try`/`catch` shape the renderer uses (§6.4 rule 4). The server compiles it with `new Regex(...)` in .NET; a pattern that compiles there may throw in JS. Validating client-side turns "saved, then unusable in every ticket form" into an inline error next to the box.

**H. A `422` is a form banner, verbatim, above *Save*** — never mapped to a field ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §7.3). The messages do name the field key (`"Duplicate field key 'x'."`), and matching that string to highlight a row is exactly the heuristic §7.3 forbids. If you reach one of these messages at all, the corresponding Zod rule in rule F is missing; add it.

**I. A `409` means the code is taken (create) or somebody else saved first (edit).** Two different sentences from the server, both rendered verbatim, both with a *Reload* affordance. Never reset the form (§9.3 rule D) — the user's twelve field rows must survive.

### 5.6 The mandatory stale check (§9.4)

[../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §9.4 is not optional here, and this is the screen it was written for. Implement it exactly:

```ts
// TicketTypeEditorScreen.tsx, edit mode only.
// Recorded when the form is first populated, and NOT updated on re-render.
const loadedVersionRef = useRef<number | null>(null);

async function onSubmit(values: EditTicketTypeFormValues) {
  // Step 2 of section 9.4: re-read immediately before writing. fetchQuery, not
  // invalidateQueries, because we need the value here and now, not a background refresh.
  const latest = await queryClient.fetchQuery({
    queryKey: ['ticketTypes', 'detail', ticketTypeId],
    queryFn: () => getTicketType(ticketTypeId),
  });

  if (latest.currentVersionNumber !== loadedVersionRef.current) {
    // Step 3: do NOT submit. Submitting would replace their version's fields with ours.
    setStaleConflict(latest);
    return;
  }

  await editMutation.mutateAsync(toEditRequest(values));
}
```

The blocking banner must state the version numbers (*"You are editing version 3. Version 5 now exists."*), summarise what moved — `displayName`, `category`, the two flags, and the field-key set added and removed, all computable by diffing `latest.fields` against the loaded array — and offer two buttons: *Reload and discard my changes* and *Keep editing* (which leaves submit blocked). It must not offer *Save anyway*: there is no merge, and the entire content of the losing save is the other person's work.

**State plainly, in the banner's own copy and in a code comment, that this is a mitigation with an open race and not a fix.** Between the `fetchQuery` and the `POST` another Accountant can still save, and both callers still receive `200`. The proper fix is a row-version column and a `409`, which is **item 7 in [../BACKEND_CHANGES_REQUIRED.md](../BACKEND_CHANGES_REQUIRED.md)**. Do not skip the mitigation because it is imperfect, and do not present it to a user as making the problem go away.

**The check does not apply to create** (there is nothing to be stale against) and **does apply to the editor only** — `toggle` writes no version and cannot lose a field.

### 5.7 The editor must refuse to load from a historical version

If the editor is reached with a detail whose `versionNumber !== currentVersionNumber` — by a `?version=` on the URL, by a stale cache entry, or by an *Edit* link on §4's historical view — render a blocking banner and **no submit button**, offering only *Edit the current version*.

The failure otherwise is silent and total: `/edit` replaces the field set with whatever is in the form, so saving a form populated from v1 while v5 exists creates v6 containing v1's fields. Four versions of work are reverted, the response is `200`, and the only trace is a version number that went up. See §7.2.

---

## 6. The dynamic form renderer — the contract

### 6.1 Where it lives, and why it is in `shared/` and not in this slice

**File:** `frontend/src/shared/dynamicForm/DynamicForm.tsx` **File:** `frontend/src/shared/dynamicForm/fieldRegistry.tsx` **File:** `frontend/src/shared/dynamicForm/buildZodSchema.ts` **File:** `frontend/src/shared/dynamicForm/visibility.ts`

Four files, already listed in [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §1.2 and §1.4. `slices/tickets/` is its real consumer and does not exist yet; `slices/ticketTypes/` is its only consumer today, through §4's preview. Putting it in `slices/ticketTypes/` would force `slices/tickets/` to import a screen component from another slice, which §1.4 rule C forbids — a slice may import only `types.ts` and `api.ts` from another slice.

The consequence to accept now: **the renderer's props may not mention a ticket, a ticket type, a role, or a session.** It takes `FieldDescriptor[]`, a mode, and a value object. If a prop named `ticketId` or `role` appears in its signature, the file is in the wrong folder — and see §6.8 for why `role` in particular is a defect and not just a layering slip.

### 6.2 The `FieldDescriptor` contract

**File:** `frontend/src/shared/dynamicForm/types.ts`

Mirrors `FieldDescriptorDetailDto` in `Slices/TicketTypes/ExternalInterfaces/TicketTypeDetailDto.cs`, property for property, camelCased. This type lives in `shared/`, not in `slices/ticketTypes/types.ts`, and `slices/ticketTypes/` re-exports it — a `shared/` component cannot depend on a slice's types (§1.4 rule A).

```ts
/** Mirrors Slices/TicketTypes/ExternalInterfaces/TicketTypeDetailDto.cs -> FieldDescriptorDetailDto. */
export interface FieldDescriptor {
  key: string;
  label: string;
  /** '' when absent. Never null: the C# property is a non-nullable string. */
  helpText: string;
  /** One of the eleven strings in ExternalInterfaces/FieldDataTypes.cs:28-38. Treated as unknown otherwise (6.3). */
  dataType: string;
  displayOrder: number;
  /** '' means "no group" -- the leading unnamed group (6.6). */
  groupName: string;
  isRequired: boolean;
  /**
   * The AUTHOR'S setting, echoed back. It is NOT a render instruction: the server has already
   * removed the fields a Customer-side caller may not see. See section 6.8 -- filtering on this
   * in the client is a defect.
   */
  isVisibleToCustomer: boolean;
  /** [] for every non-choice type. >= 2 entries for SingleChoice / MultipleChoice. */
  choiceOptions: ChoiceOption[];
  /** ALWAYS present -- the C# property is `= new()`, never null. Members are individually absent. */
  validation: FieldValidation;
  /** null when the author set no rule. Never a blank-fieldKey object. */
  conditionalVisibility: ConditionalVisibility | null;
}

export interface ChoiceOption { label: string; value: string; }

/** Mirrors FieldValidationDto. Every member is optional; '' and [] mean "no rule". */
export interface FieldValidation {
  minLength?: number | null;
  maxLength?: number | null;
  /** C# decimal -> JSON number. See GeneralUIArchitecture section 10.2. */
  minValue?: number | null;
  maxValue?: number | null;
  /** C# DateOnly -> "2026-09-02". No timezone. Never build a Date and format it locally. */
  earliestDate?: string | null;
  latestDate?: string | null;
  /** '' means no rule. A .NET-authored pattern that must be compiled in JS (6.4 rule 4). */
  regexPattern: string;
  /** [] means no rule. Split from a comma-separated column, already trimmed server-side. */
  allowedFileTypes: string[];
  maxFileSizeBytes?: number | null;
}

export interface ConditionalVisibility { fieldKey: string; value: string; }

export type DynamicFormMode = 'input' | 'preview' | 'read';

export interface DynamicFormProps {
  fields: FieldDescriptor[];
  mode: DynamicFormMode;
  /** Keyed by FieldDescriptor.key. Absent key = no value. */
  values?: Record<string, unknown>;
  /** Omitted in 'preview' and 'read'. Receives ONLY visible fields' values (6.5 trap 1). */
  onSubmit?: (values: Record<string, unknown>) => void;
  // Deliberately absent: role, session, ticketId, ticketTypeId, isAccountant. See 6.8.
}
```

**`validation` is always an object.** `FieldValidationDto Validation { get; set; } = new()`, so `validation` never arrives `null` but `validation.minLength` routinely arrives `null`. Testing `if (field.validation)` is always true and proves nothing. Test each member.

**`''` and `[]` mean "no rule", not "a rule matching nothing".** `regexPattern: ''` compiled to `new RegExp('')` matches every string, which is harmless; `allowedFileTypes: []` treated as a whitelist rejects every file. Check for emptiness before building either rule.

### 6.3 `dataType` → control

**File:** `frontend/src/shared/dynamicForm/fieldRegistry.tsx`

The eleven strings in `Slices/TicketTypes/ExternalInterfaces/FieldDataTypes.cs:28-38`, in that file's order. One `Record<string, FieldRenderer>` lookup, not a chain of `if`s — a registry can be enumerated in a test against `FieldDataTypes.All` (`:49-62`), and a chain cannot.

| `dataType` | MUI control | Value in form state | Notes |
|---|---|---|---|
| `SingleLineText` | `TextField` | `string` | `''` → `null` on submit (§6.7 rule F) |
| `MultiLineText` | `TextField multiline minRows={4}` | `string` | Do not auto-grow past ~12 rows |
| `WholeNumber` | `TextField type="number"` , `inputMode="numeric"`, `step={1}` | `number \| null` | Register with `valueAsNumber`; reject a non-integer in Zod, not by masking keystrokes |
| `DecimalNumber` | `TextField type="number"` , `step="any"` | `number \| null` | |
| `MoneyAmount` | `TextField type="number"` , `step="0.01"`, right-aligned | `number \| null` | **No currency symbol.** There is no currency in the schema ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §10.2) |
| `Date` | `DatePicker` (`@mui/x-date-pickers`) | `string` `"yyyy-MM-dd"` | Submit the plain date string. Never `toISOString()`, which shifts the day west of UTC |
| `DateRange` | **two** `DatePicker`s, *From* and *To* | `{ from: string \| null; to: string \| null }` | MUI's `DateRangePicker` is in the **Pro** package and is not a locked dependency (§1.5). Two pickers, one fieldset, one label |
| `YesNo` | `RadioGroup` with *Yes* and *No* | `boolean \| null` | **Not a `Checkbox`.** A checkbox cannot represent "not answered", so an optional `YesNo` would submit `false` for a question nobody read |
| `SingleChoice` | `TextField select` | `string` (the option's **`value`**) | `≥2` options guaranteed by the server. Render `label`, submit `value` |
| `MultipleChoice` | `FormGroup` of `Checkbox`es | `string[]` | Never a native multi-`select`; unusable on touch. `[]` → `null` on submit |
| `FileUpload` | **disabled** placeholder | always `null` | §6.9 |

**An unrecognised `dataType` renders a visible error placeholder and nothing else.** The placeholder is an `Alert severity="error"` naming the field label, the field key, and the unrecognised value verbatim: *"Field 'salary_amount' has an unsupported data type 'Currency' and cannot be shown."* It occupies the field's position in the layout and it contributes **no** Zod rule and **no** value.

Do **not** skip the field silently, and do not fall back to `SingleLineText`. Silently skipping a field that is `isRequired` produces a Zod schema whose required key can never be satisfied, so *Submit* fails with an error attached to a control that is not on the screen — a form that cannot be submitted with nothing anywhere indicating why. Falling back to a text input is worse: it collects a string where a number or a date was specified, and the wrongness surfaces in whatever consumes the Field Value, long after the person who typed it has gone. This can happen without a deployment mismatch, incidentally: `ValidateFields` only checks `FieldDataTypes.All`, so a data type added on the server ships to a browser holding an older bundle.

### 6.4 `validation` → Zod

**File:** `frontend/src/shared/dynamicForm/buildZodSchema.ts`

`buildZodSchema(fields: FieldDescriptor[], visibleKeys: Set<string>): ZodObject` — it takes the visible set because a hidden field contributes no rule (§6.5 trap 1). Every member of `FieldValidationDto`, and `isRequired`:

| Member | Applies to | Zod | Failure if omitted or wrong |
|---|---|---|---|
| `isRequired` | all | strings `.min(1)`; numbers/booleans/dates `.refine(v => v !== null && v !== undefined)`; `MultipleChoice` `.min(1)` on the array | A `.nonempty()` on a `boolean` is a type error; a `required` prop alone is decorative and RHF submits `null` |
| `minLength` | text | `.min(n)` | |
| `maxLength` | text | `.max(n)` | |
| `minValue` | `WholeNumber`, `DecimalNumber`, `MoneyAmount` | `.gte(n)` | Arrives as a JSON **number** from a C# `decimal`; never compare it as a string |
| `maxValue` | same | `.lte(n)` | |
| `earliestDate` | `Date`, `DateRange` | `.refine(v => v >= earliestDate)` on the `"yyyy-MM-dd"` **string** | ISO date strings compare correctly lexicographically. Parsing to `Date` re-introduces the timezone shift for no benefit |
| `latestDate` | same | `.refine(v => v <= latestDate)` | For `DateRange`, apply to both ends **and** add `from <= to`, which the server does not check per-value |
| `regexPattern` | text | `.regex(compiled)`, compiled per rule 4 below | |
| `allowedFileTypes` | `FileUpload` | none today (§6.9) | Surface as help text so the author sees it took effect |
| `maxFileSizeBytes` | `FileUpload` | none today (§6.9) | Same, formatted through `format/money.ts`-style `Intl.NumberFormat` |

Four rules for building it:

1. **`.optional()` last, and transform `''` before validating.** `z.string().max(255).optional()` is right; `.optional().max(255)` does not typecheck. And an *optional* text field with a `minLength` fails on `''` unless you `z.preprocess(v => (v === '' ? undefined : v), …)` first — otherwise leaving a field alone trips a rule that was only ever meant to apply to an answer. This is the same rule as [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §9.3 rule F: `null`, not `''`.
2. **Skip a rule whose member is `null`, `undefined`, `''` or `[]`.** See §6.2.
3. **A rule that does not apply to the data type is ignored, not an error.** `minValue` on a `SingleLineText` is legal server-side — `ValidateFields` never cross-checks a validation member against the data type — so it will occur. Ignore it silently in the renderer; the *editor* is where it is prevented (§5.5 rule B).
4. **`regexPattern` is a string compiled at runtime, and an invalid one must not crash the form.**

   ```ts
   // The server proved this pattern compiles in .NET (TicketTypeMapper.ValidateRegexCompiles).
   // It did NOT prove it compiles in JavaScript. The dialects differ: .NET accepts inline
   // options (?i), conditionals (?(cond)a|b), atomic groups (?>...), balancing groups,
   // \Z and \z anchors, (?#comments) and \p{IsGreek}. Every one of those throws SyntaxError
   // in `new RegExp`. An uncaught throw here happens during render, so it takes out the whole
   // form -- every field, including the ones with no pattern -- and the user sees a blank
   // region with the failure only in the console.
   function compilePattern(pattern: string, key: string): RegExp | undefined {
     if (pattern === '') return undefined;
     try {
       return new RegExp(pattern);
     } catch {
       // Drop the rule, keep the field usable, and make the fault visible to somebody who can
       // fix it. Never fail closed here: a field whose pattern cannot be compiled would
       // otherwise reject every value with no message that names a cause.
       console.warn(`Field "${key}": regexPattern is not a valid JavaScript regular expression.`);
       return undefined;
     }
   }
   ```

   Compile **once**, in `buildZodSchema`, memoised on the field array — never inside a `.refine` that runs per keystroke. Do not add the `u` flag (it makes previously valid patterns throw) and do not add `g` (a stateful `lastIndex` makes `.test` alternate between true and false on identical input).

### 6.5 `conditionalVisibility` — evaluation rules

**File:** `frontend/src/shared/dynamicForm/visibility.ts`

`{ fieldKey, value }` means: **show this field only while the field named `fieldKey` currently holds a value equal to `value`.** `value` is a string, always — it comes from a `VARCHAR(500)` column — so every comparison needs a defined coercion of the controller's live value.

| Controller's `dataType` | Coercion | Comparison |
|---|---|---|
| `SingleLineText`, `MultiLineText` | the string as-is | `===`, case-sensitive, after trimming both sides |
| `WholeNumber`, `DecimalNumber`, `MoneyAmount` | `Number(value)` on the **rule** side | Numeric `===` when both sides parse to a finite number; otherwise never equal. **Not `String(n)`** — `String(1.50)` is `"1.5"`, so a rule written `"1.50"` would never match a value the user entered as `1.50` |
| `YesNo` | `"true"` / `"false"`, lower-case | `===`. `null` (unanswered) never matches |
| `Date` | the `"yyyy-MM-dd"` string | `===` |
| `SingleChoice` | the selected option's **`value`** | `===`. Never compare against the label |
| `MultipleChoice` | the `string[]` | **`array.includes(value)`** — not `===`, not `join()` |
| `DateRange` | not comparable | Unevaluable: the dependent field is **shown**. Rule 3 below |
| `FileUpload` | not comparable | Unevaluable: shown. Rule 3 |

Rules:

**1. Self-reference needs no client defence.** `ValidateFields` rejects `fieldKey === own key` and a `fieldKey` naming no field in the request, both with `422`, so neither can be stored. Do not write a guard for them; a guard for an impossible state is untested code that hides the real bug if the server check ever regresses.

**2. Chains are real and cycles are possible, because the server validates neither.** `A` shows `B`, `B` shows `C` is legal and normal. And a cycle — `A` names `B`, `B` names `A` — passes `ValidateFields` intact, because it only checks that each `fieldKey` exists and differs from its own. So:

```
computeVisible(fields, values):
  visible = every field with conditionalVisibility === null
  repeat up to min(fields.length, 32) times:
    for each field with a rule:
      controller = fields[rule.fieldKey]
      # A field whose controller is itself hidden is hidden: a rule on an invisible
      # question can never be satisfied by an answer nobody was asked for.
      shown = visible.has(controller.key) && matches(rule, coerce(controller, values))
      update visible
    if nothing changed this pass: stop
  # Fixed point reached, or the cap hit -- see rule 3.
```

Cap the iteration; an uncapped fixed-point loop over a cycle is an infinite render. Detect cycles once, structurally, in the same module: build the `key -> fieldKey` edge list and find any strongly-connected component of size > 1.

**3. When a rule cannot be evaluated — a cycle, an unevaluable controller type, an unknown `dataType` on the controller — render every field involved rather than none.** An unexpectedly visible field is a cosmetic fault the user can see, describe and report. An unexpectedly hidden one is a question nobody was asked, an empty Field Value on a ticket, and no evidence anywhere that something was withheld. Log a `console.warn` naming the keys in the cycle, and in `mode="preview"` (§4.1) show an inline `Alert severity="warning"` — that preview is the only place an author can discover they have built one.

**4. TRAP — a hidden field's value must not be submitted, and a hidden field's validation must not run. This is the worst bug available in this renderer.** Numbered, because it will be introduced by anybody who builds the schema once and the visibility separately:

> A field with `isRequired: true` that is currently hidden contributes a required Zod key. RHF's resolver fails, `handleSubmit` never calls `onSubmit`, and the error is attached to a control that is **not rendered**. So *Submit* does nothing at all: no request, no banner, no field outlined red, nothing in the console. The user presses it repeatedly and the form is permanently unsubmittable. It is unreportable, because from the user's side the button is simply broken.

The fix is structural, not defensive: `buildZodSchema(fields, visibleKeys)` takes the visible set and **omits hidden fields from the schema entirely**, and the schema is recomputed (memoised on `[fields, visibleKeys]`) whenever visibility changes. Symmetrically, the submitted object is built from the visible set only — a hidden field's key is **absent**, not `null`. Present-and-null and absent are different answers: the first says "asked, not answered", the second says "not asked", and only the second is true.

**5. A field that becomes visible again keeps whatever was typed into it before.** Do not clear values on hide. A user who ticks *Other*, types a reason, unticks and re-ticks by accident has not asked to lose the sentence — and clearing on hide plus omitting on submit means a mis-click destroys data with no undo. Keep it in form state and let §6.5 rule 4 decide what leaves.

### 6.6 `groupName` and `displayOrder` — layout

**A. Fields with `groupName === ''` form the leading unnamed group**, rendered with no heading and no card, before every named group. Blank is the DTO default and most types will have nothing else, so an unnamed group must not render an empty `<h3>` or a "General" heading nobody wrote.

**B. Named groups follow, ordered by the minimum `displayOrder` among their members**, tie-broken by `groupName` case-insensitively. Using the author's existing ordering number means section order is controlled by the same control that orders fields, with nothing new to learn; ordering groups alphabetically instead puts *Bank details* before *Personal details* whatever the author intended, and there is no `groupOrder` column to appeal to.

**C. Within a group, sort by `displayOrder`, then by `key`.** `displayOrder` is an `INTEGER` with no uniqueness constraint anywhere — the editor renumbers densely (§5.5 rule E) but nothing stops an older type, or a hand-written API call, from having five fields all at `0`. **A collision resolved only by `displayOrder` leaves the order to whatever the array happened to hold**, and that order can differ between a fresh fetch and a cache read, so the form visibly reshuffles between renders and a screenshot cannot be reproduced. `key` is unique per version (`UNIQUE(ticket_type_version_id, key)`), so `displayOrder` then `key` is total and deterministic. Compare keys with `localeCompare(b.key, undefined, { sensitivity: 'base' })`, then ordinal, so two keys differing only in case do not tie.

**D. `groupName` is compared exactly.** `"Bank Details"` and `"bank details"` are two groups. The server trims but does not case-fold it, so that is the truth; folding them in the client would merge two groups an author can see are separate in the editor, and the merge would be invisible there.

**E. A group with no visible members renders nothing** — no heading, no divider, no empty card. A heading with nothing under it reads as a failed load, and every field in a group can legitimately be conditional.

**F. The renderer never mutates the array it is given.** `fields.sort(...)` sorts in place, and the array it is handed is the one inside the TanStack Query cache entry — so sorting it reorders what every other component reading `['ticketTypes','detail',id]` sees, including §4's fields table, without any state change to explain it. Derive with `[...fields].sort(...)` inside a `useMemo`.

### 6.7 Rules that apply to every rendered field

**A. Every control has a real `<label>`**, from `label`, via MUI's `label=` prop ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §8.4 item 1). A `placeholder` is not a label. For `DateRange` and `MultipleChoice`, which are several inputs, the group label is a `FormLabel` inside a `FormControl` with the inputs as its content — otherwise a screen reader announces two unlabelled date boxes.

**B. `helpText` goes in `helperText`, and an error replaces it.** `helperText={error?.message ?? (field.helpText || undefined)}` with `error={Boolean(error)}`. MUI has one slot; rendering `helpText` unconditionally means a validation message either never appears or appears somewhere else, and a validation message the user cannot find is the same as none.

**C. The RHF field name is an alias, not the `key`.** React Hook Form parses `.` and `[` in a name as a path, and the server accepts **any** non-blank string of ≤100 characters as a `key` — it checks length, blankness and uniqueness and nothing about the character set. A key `salary.amount` therefore becomes nested state `{ salary: { amount } }` and submits under the wrong shape. So `DynamicForm` builds an `alias -> key` map once (`f0`, `f1`, … by index), registers the aliases, and translates back when assembling the submitted object. Errors are looked up by alias. Do **not** solve this by restricting keys in the editor's Zod schema: a client limit stricter than the server's blocks input the server accepts, which [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §9.2 forbids by name. Flagged in §10 item 4.

**D. `isRequired` drives both the asterisk and the Zod rule.** MUI's `required` prop only draws the asterisk; it adds no validation that RHF's `handleSubmit` respects. One without the other is either a form that accepts a blank required answer or one that rejects it with no visual cue that it was mandatory.

**E. `mode="read"` renders values as text, not as disabled inputs.** A disabled `TextField` is low-contrast, unselectable and unreadable at length, and a page of them looks broken rather than informational. `mode="read"` is what the future ticket detail screen uses, so it must be built now even though nothing calls it — it is three lines per renderer and retrofitting it later means touching all eleven.

**F. An untouched optional field submits `null`, not `''`, `[]` or `NaN`** ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §9.3 rule F). `TextField type="number"` yields `''` when cleared, which `Number('')` turns into `0` — a zero the user never typed, indistinguishable from one they did.

**G. Numbers stay numbers in form state.** Register with `valueAsNumber` and keep the raw number; never store a formatted string back into state ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §10.2). Format only for `mode="read"`.

**H. The renderer holds no server state and issues no requests.** No `useQuery`, no import from `shared/api/`, no `fetch`. It is a pure function of its props. `SingleChoice` options come from `choiceOptions` on the descriptor it was handed, never from a lookup endpoint — there is no such endpoint, and adding one would put a network call inside a component that renders once per field.

### 6.8 What the renderer must NOT do, and why

**This is the most important negative-space section in the UI specification.** Everything below is already done, server-side, in `TicketTypeMapper`. Doing it again in the client makes the application less safe, not more.

| The renderer must NOT | Already done by | Why a client copy is worse than nothing |
|---|---|---|
| Filter on `isVisibleToCustomer` | `ToDetail`: `if (IsCustomerSide(callerRole)) fields = fields.Where(f => f.IsVisibleToCustomer)` | See below |
| Hide or grey a deactivated type | `IsDiscoverableBy` → `404` on `/detail` | The type is already absent; a client check has nothing to act on except a `200` that should not have arrived |
| Check `allowEmployeeToOpen` | `IsInAudienceOf` → `404` | Same |
| Take a `role` or read `useSession` | — | `shared/` may not import a slice, and a renderer that knows the role is a renderer somebody will add a filter to |
| Re-check any authorization rule | `PermissionChecker`, fail-closed, audited | `can()` decides affordances, never data ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §6.2 rule A) |
| "Repair" a dangling `conditionalVisibility` by dropping the field | `ValidateFields` → `422` | A field silently removed by client-side repair is a question nobody was asked (§6.5 rule 3) |
| Deduplicate `key`s | `ValidateFields` + `UNIQUE(ticket_type_version_id, key)` | Duplicates cannot be stored; deduping hides it if they ever are |

The reason, stated once and applying to every row: [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) is explicit —

> *"Never rely on the React app to hide data. Internal Notes, Accountant-only fields, and out-of-scope records must be **absent from the API response**, not merely unrendered."*

and [../../README.md](../../README.md) locks the mechanism: an out-of-scope resource is a `404`, because *"a `403` confirms the row exists"*.

**A client-side duplicate of a server-side security filter is not defence in depth here; it is a mute button on the alarm.** If `ToDetail`'s `Where` is ever removed — by a refactor, a new read path, a second mapper, exactly as correction note T-12 describes happening in `ExternalInterfaces/TicketTypesApi.cs` — then with the client filter in place an Employee's browser *receives* every Accountant-only field key, label and help text over the wire and quietly declines to draw them. The leak is complete: it is in the response body, in the browser's network tab, in the TanStack Query cache, and in any error report that serialises it. Nothing is broken on screen, so nobody reports anything, and the regression survives indefinitely. Without the client filter the same regression is a screen full of fields that visibly should not be there — noticed in minutes, by the first Employee who looks.

**`isVisibleToCustomer` is still meaningful — to an Accountant author.** It is a `Switch` in the editor (§5.4) and a badge on the detail screen (§4.2 rule A). Both are Accountant-facing, both are about the author's *intent*, and neither is a render-time filter. That is the whole of its use in the client. Concretely: `isVisibleToCustomer` may appear in `frontend/src/slices/ticketTypes/`, and must not appear anywhere in `frontend/src/shared/dynamicForm/` except in the `FieldDescriptor` interface and its comment.

Two more, for completeness:

- **Never swallow a `404`.** `try`/`catch` around a detail query that renders an empty form instead of `NotFoundPage` converts the scoping mechanism into a blank screen ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §2.4 item 5).
- **Never render "forbidden", "denied" or "no permission" for a `404`** (§2.3 rule J). For a Customer-side caller on a deactivated type, "Not found" is the only honest wording — and it is honest in both cases, which is the point.

### 6.9 `FileUpload` — disabled until a `Tickets` UI ships

`Documents` is built and registered (`Program.cs:59`) and *by design never exposes HTTP routes of its own*; `Tickets` already owns `/api/documents/*` — `/upload`, `/list`, `/download` and `/delete` all exist today, at `Slices/Tickets/TicketsEndpoints.cs:250-356` ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §0.1, §12 item 3). So the endpoints are not what is missing. What is missing is on this side of the wire: no `Tickets` UI plan, no `Screens/TicketsScreens.md`, no client route and no ticket form, so no ticket-submit path and **no id to reference** — and `/api/documents/upload` takes a `ticketId`.

`FileUpload` therefore renders a **disabled** control — an outlined region with the field label, a disabled *Choose file* button, and the sentence *"File uploads are not available yet."* — and:

- it contributes **no** Zod rule, not even `isRequired`. A required-but-impossible field would make every ticket of that type unsubmittable the moment a `Tickets` **UI** ships;
- it submits `null` always;
- `allowedFileTypes` and `maxFileSizeBytes` are shown as help text, so an author can confirm the rules were stored, formatted (`"PDF, JPG, PNG, up to 5 MB"`) rather than raw;
- it is **not omitted**. §12 item 3 is explicit: *"a ticket type author can define a file field today and needs to see it."* Omitting it makes a field the author created invisible, and they will add it a second time.

---

## 7. Version numbers: `versionNumber` vs `currentVersionNumber`

### 7.1 The two properties

| Property | Means | Source |
|---|---|---|
| `versionNumber` | The version **these `fields` came from** | `version.VersionNumber` |
| `currentVersionNumber` | The **latest version that exists** for this type | `type.VersionNumber` |

| Endpoint | `versionNumber` | `currentVersionNumber` |
|---|---|---|
| `/detail` | always the current one (`CurrentVersionOf`) | the same number |
| `/version` | the one you asked for | the latest, which may be higher |
| `/create` | `1` | `1` |
| `/edit` | the version just minted | the same number |
| `/toggle` | the current one — **toggle mints no version** | the same number |

`TicketTypeListItemDto` carries only `currentVersionNumber`; there is no field set on the list DTO for a `versionNumber` to describe.

### 7.2 The banner is mandatory when the two differ

When `versionNumber !== currentVersionNumber`, §4 renders an `Alert severity="info"` above everything else: **"This is version 3 of 5. It is not the current version."** with a link to `/ticket-types/:id` (no `?version=`) reading *View the current version*, and — for a caller with `can(role, 'EditTicketType')` — **no *Edit* button on the historical view at all**, only a link to edit the current one (§5.7).

The failure without it is silent data loss. `/edit` replaces the field set wholesale from whatever the form holds. An Accountant who reaches v1 by stepping back through versions, spots a typo, and presses *Edit* gets a form full of v1's fields; saving mints v6 containing v1's fields, reverting four versions of work. Every response is `200`, no audit entry says "reverted", and the only visible trace is a version counter that went up by one — which is what a successful edit looks like.

### 7.3 `/version` deliberately ignores `IsActive`, and `/detail` does not

`GetTicketTypeVersionHandler` calls `ApplyCustomerSideAudience` (audience only); `GetTicketTypeHandler` calls `ApplyCustomerSideVisibility` (audience **and** `IsActive`). This is correction note T-4, applied on purpose, and `TicketTypeMapper`'s own comment says why: *"the version-by-number read must stay reachable for a historical ticket even after the type is deactivated."* A Customer Admin looking at a ticket they raised last year must still be able to render its form after the type has been retired.

Three consequences for the UI:

**A. `/version` can succeed where `/detail` returns `404`, for the same type and the same caller.** That is not an inconsistency to normalise. Never use `/version` to test whether a type exists or is usable, and never fall back from a `404` on `/detail` to a `/version` call to "get something to show" — that is precisely the discovery the `404` refused.

**B. `Tickets` reads schemas through `/version` (in fact through `ITicketTypesApi`), not through `/detail`.** So the renderer must be usable from a descriptor array whose type is deactivated, with no "this type is retired" interstitial of its own. Whether to show a retirement notice is the *ticket* screen's decision, made once a `Tickets` UI exists.

**C. `versionNumber` is not bounded by anything the client can see except `currentVersionNumber`.** Version numbers start at 1 and increment by 1 per edit — `EditTicketTypeHandler` derives `next` from `Max(v.VersionNumber) + 1` — so `1..currentVersionNumber` is a complete, gapless range and stepping is safe. Do not assume it stays gapless if a future migration ever backfills; render a `404` from `/version` as "That version does not exist" rather than crashing the stepper.

---

## 8. What these screens must NOT do

1. **No delete, anywhere, for a type or a version.** [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §5: *"Delete a Ticket Type or a version | **Nobody.** Existing tickets depend on them."* There is no endpoint, no soft-delete flag, and no "delete if unused". Deactivation is the whole of the retirement story. A *Delete* button that calls nothing is a support ticket; one that calls `toggle` is a lie about what happened.
2. **No version-history list.** No endpoint returns one (§10 item 1). Step by number (§4.2 rule E); do not loop `/version` to fake it.
3. **No client-side filtering of fields or types by role.** §6.8.
4. **No client-side search, sort or filter of the list beyond `activeOnly`.** `/list` accepts three query parameters and no search term or sort key. Sorting one page of a server-paginated list sorts fifteen rows out of two hundred and presents the result as if it were the whole ordering.
5. **No optimistic updates** ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §3.2 rule E). There is no concurrency token, so an optimistic edit is a confident display of a version number that may not exist.
6. **No "duplicate this type" action.** It would need a new `code` and a full `create` payload assembled client-side, and nothing specifies what the copy's code should be. Not in this pass.
7. **No import, export, or bulk activate/deactivate.** One `toggle` per type, confirmed.
8. **No approval workflow, and no restricting authoring to `AccountantAdmin`.** [../../02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §5 forbids both by name: *"Do not restrict it to Accountant Admin, and do not add an approval step."*
9. **No form that submits a rendered ticket.** There is no ticket endpoint. §4's preview submits nowhere and must not grow a *Submit* button.
10. **No `activeOnly=false` meaning "everything".** §1.1.
11. **No page-size option above 50** ([../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §3.3).

---

## 9. Behavioural cases

Each is checked in a browser against a running API. A passing type-check proves none of them.

- [ ] `/ticket-types` as an `Employee` shows only active types with `allowEmployeeToOpen`, and the *All/Active/Inactive* filter is absent.
- [ ] The same screen as an `AccountantUser` shows inactive types with *All* selected, and *All* sends **no** `activeOnly` parameter (verified in the network tab).
- [ ] Selecting *Inactive* sends `activeOnly=false` and returns only deactivated types.
- [ ] `pageSize=999` renders a pager consistent with the 50 the server returned, with no missing rows.
- [ ] Creating a type with 5 fields returns `201`, and the detail screen shows `v1` with 5 rows without a second fetch.
- [ ] Editing that type and saving shows *"Saved as version 2."*, and `?version=1` still shows the original 5 fields unchanged.
- [ ] The editor in edit mode renders `code` read-only, and `code` is absent from the request body.
- [ ] Deleting a field row and saving mints a version **without** it — confirming the full-replacement semantics of §5.1 item 1 are understood, not worked around.
- [ ] Opening the editor in two tabs, saving in the first, then saving in the second: the second is **blocked** with the version-conflict banner and **no request is sent**.
- [ ] `?version=1` on a type at v3 shows the "version 3 of 5"-style banner and offers no *Edit*.
- [ ] Switching a field from `SingleChoice` to `SingleLineText` clears its options, and the save succeeds instead of returning `"Non-choice field … cannot have choice options."`
- [ ] A choice field with one option is blocked client-side, not by a `422`.
- [ ] A field key of 101 characters, and a duplicate key differing only in case, are both blocked client-side.
- [ ] A field key entered as `"  key  "` is submitted trimmed (§5.5 rule C).
- [ ] A `conditionalVisibility` value control for a `YesNo` controller offers exactly *Yes* and *No*, and sends `"true"` / `"false"`.
- [ ] In the §4 preview, answering the controller field shows and hides the dependent field live.
- [ ] A **required** field that is currently hidden does **not** block submit, and its key is **absent** from the submitted object — not `null` (§6.5 trap 4).
- [ ] A three-deep chain A→B→C resolves in one interaction, with no intermediate frame showing C without B.
- [ ] A cycle built through the API by hand renders **all** the cycle's fields with a warning, and does not hang the tab.
- [ ] Five fields all at `displayOrder: 0` render in the same order on a hard reload as on a cache read.
- [ ] A type with two named groups renders the ungrouped fields first, with no heading above them.
- [ ] A group whose every member is hidden renders no heading.
- [ ] A `dataType` of `"Currency"` injected via the API renders a red placeholder naming the field and the type, and the rest of the form still submits.
- [ ] A `regexPattern` of `(?i)abc` (valid .NET, invalid JS) does **not** blank the form; the field renders with no pattern rule and a console warning.
- [ ] A `FileUpload` field renders disabled, shows its allowed types and size, and does not block submit.
- [ ] Deactivating a type: it disappears from an `Employee`'s list, `/detail` gives them "Not found", and `?version=1` still renders for them.
- [ ] Reactivating restores it with the same version number and the same fields.
- [ ] `grep -rn "isVisibleToCustomer" frontend/src/shared/` matches only the `FieldDescriptor` interface and its comment.
- [ ] `grep -rn "role\|useSession" frontend/src/shared/dynamicForm/` finds nothing.

---

## 10. Questions to flag if unclear

Flag these; do not answer them by inventing a behaviour.

- [ ] **There is no version-history endpoint** — only fetch-by-version-number. Already asked in [../../Slices/TicketTypes/IMPLEMENTATION_PLAN.md](../../Slices/TicketTypes/IMPLEMENTATION_PLAN.md) §11 (*"The Accountant UI probably needs it"*) and in [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §13. So §4 **cannot list versions with their dates**; in the meantime it steps through them by number, bounded by `1` and `currentVersionNumber`, and shows no dates for versions other than the one loaded. Should `GET /api/ticket-types/versions?ticketTypeId=` exist, returning `{ versionNumber, createdAt }[]`?
- [ ] **`helpText` has a declared limit that is never enforced** (see the callout in §5.5). Should `ValidateFields` length-check it, or should [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §9.2 stop listing it as a server limit? The client currently caps it at 10,000 on the strength of an unused constant.
- [ ] **A field `key` is not trimmed server-side** (see the callout in §5.5 rule C), so `"key"` and `"key "` are two distinct fields in one version. Should `NormalizeFields` trim `key` and `helpText`, as correction note T-13 did for `label` and `groupName`?
- [ ] **Is any character set required of a field `key`?** The server enforces only non-blank, ≤100 and case-insensitive uniqueness, so `"a.b"`, `"a[0]"` and a key of pure emoji are all valid. §6.7 rule C works around the RHF path-syntax hazard with aliases, but a key is also a JSON property name in the future Field Value payload. Should the server require `^[A-Za-z0-9_.-]{1,100}$`? The client must **not** impose it unilaterally (§9.2).
- [ ] **`ChoiceOptionDto.Label` and `.Value` have no length limit anywhere.** They are serialised into a `TEXT` column as JSON, so nothing bounds them. No client cap is specified, because an invented one would reject input the server accepts.
- [ ] **`validation` members are not cross-checked against `dataType`.** `minValue` on a `SingleLineText` field is stored. §6.4 rule 3 ignores them at render time and §5.5 rule B clears them in the editor, but a type authored through the API keeps them. Should `ValidateFields` reject the combination?
- [ ] **`allowSubjectOtherThanCreator` has no consumer.** It is authored, stored and displayed, and the built `Tickets` slice never reads it: `CreateTicketHandler.cs:93-95` restricts an Employee to a ticket about themselves unconditionally, and `:107` reads `AllowEmployeeToOpen` but never this flag. Confirm the intended semantics before a screen explains it to a user.
- [ ] **Should `conditionalVisibility` support any operator other than equality?** One string, one `===`. "Greater than", "is not empty" and "is one of" are all natural requests against a numeric or choice field and none is expressible. A change to `ConditionalVisibilityDto` and its two columns, not a UI decision.
- [ ] **Should `/list` gain a `search` or `category` parameter?** §3.1 rule B and §8 item 4 both refuse to fake it client-side; a catalogue of two hundred types is unnavigable at 15 a page.
- [ ] **Minor, in the governing document:** [../BACKEND_CHANGES_REQUIRED.md](../BACKEND_CHANGES_REQUIRED.md) is cited by [../GeneralUIArchitecture.md](../GeneralUIArchitecture.md) §2.6, §4.4, §7.3, §9.4, §11.1 and by §5.6 of this document, and **the file does not exist in the repository yet.** Items 1–8 are referenced by number from four documents; it needs writing before those numbers drift.

---

## Files checklist

`shared/dynamicForm/` — built now, consumed by `Tickets` later:

- [ ] `frontend/src/shared/dynamicForm/types.ts` — `FieldDescriptor` and friends, commented with the C# file they mirror (§6.2)
- [ ] `frontend/src/shared/dynamicForm/DynamicForm.tsx` — grouping, ordering, the alias map, the three modes; **no** `role` prop (§6.1, §6.6, §6.7 rule C)
- [ ] `frontend/src/shared/dynamicForm/fieldRegistry.tsx` — eleven entries plus the unknown-type placeholder (§6.3)
- [ ] `frontend/src/shared/dynamicForm/buildZodSchema.ts` — takes `visibleKeys`; memoised pattern compilation (§6.4)
- [ ] `frontend/src/shared/dynamicForm/visibility.ts` — coercion table, capped fixed point, cycle detection (§6.5)

`slices/ticketTypes/`:

- [ ] `frontend/src/slices/ticketTypes/types.ts` — `TicketTypeListItem`, `TicketTypeDetail`, the three request types; re-exports `FieldDescriptor` from `shared/dynamicForm/types`
- [ ] `frontend/src/slices/ticketTypes/api.ts` — six functions: `createTicketType`, `editTicketType`, `toggleTicketType` (`post`), `listTicketTypes`, `getTicketType`, `getTicketTypeVersion` (`get`, with `URLSearchParams`)
- [ ] `frontend/src/slices/ticketTypes/queries.ts` — `useTicketTypeList`, `useTicketTypeDetail`, `useTicketTypeVersion`, and three mutation hooks, each naming its invalidations (§1.2)
- [ ] `frontend/src/slices/ticketTypes/schemas.ts` — the create and edit Zod schemas of §5.5 rule F. **No `code` in the edit schema** (§5.2)
- [ ] `frontend/src/slices/ticketTypes/screens/TicketTypeListScreen.tsx` (§3)
- [ ] `frontend/src/slices/ticketTypes/screens/TicketTypeDetailScreen.tsx` (§4)
- [ ] `frontend/src/slices/ticketTypes/screens/TicketTypeEditorScreen.tsx` (§5)
- [ ] `frontend/src/slices/ticketTypes/components/FieldDescriptorEditor.tsx` (§5.4)
- [ ] `frontend/src/slices/ticketTypes/components/ChoiceOptionsEditor.tsx` (§5.5 rule B)
- [ ] `frontend/src/slices/ticketTypes/components/ValidationRulesEditor.tsx` (§5.4)
- [ ] `frontend/src/slices/ticketTypes/components/ConditionalVisibilityEditor.tsx` (§5.5 rule D)
- [ ] `frontend/src/slices/ticketTypes/components/ToggleTicketTypeDialog.tsx` (§3.3)
- [ ] `frontend/src/slices/ticketTypes/components/VersionBanner.tsx` (§7.2)
- [ ] `frontend/src/routes.tsx` — the four rows in §2, `/ticket-types/new` declared **before** `/ticket-types/:ticketTypeId`
- [ ] `frontend/src/shared/permissions/can.ts` — the five TicketTypes rows, verified against `TicketTypesActionCatalogue.cs`

---

## Success criteria

Each is verified by running the app, not by reading the code.

1. All four roles can open `/ticket-types` and see a populated table; only AA and AU see *New ticket type*, *Edit*, and *Deactivate*.
2. Creating a type with one field of every one of the eleven data types succeeds, and its detail preview renders eleven controls with no placeholder, no `undefined` label, and no console error.
3. Every value in `Slices/TicketTypes/ExternalInterfaces/FieldDataTypes.cs:28-38` has an entry in `fieldRegistry.tsx`, and the registry has no entry that is not in that file.
4. A `dataType` the bundle does not know renders a red placeholder naming the field and the type, and the rest of the form still validates and submits.
5. A required field that is hidden by `conditionalVisibility` never blocks submit, and its key is absent from the submitted payload — not present with `null`.
6. A cyclic `conditionalVisibility` graph, built through the API, renders every field involved and does not hang the browser.
7. Five fields sharing one `displayOrder` render in the same order on a hard reload as on a cache read, and on every subsequent render.
8. Saving the editor in edit mode after another tab has saved is **blocked before any request is sent**, and the banner names both version numbers and says that it is a mitigation, not a guarantee.
9. `code` cannot be changed from the editor, and no request body from `/ticket-types/:id/edit` contains a `code` property.
10. Deleting a field row and saving demonstrably removes it from the new version, and the previous version still contains it under `?version=`.
11. Deactivating a type makes it `404` on `/detail` for an `Employee` while `?version=1` still renders for the same `Employee` in the same session.
12. No screen anywhere offers delete, duplicate, import, export, a bulk action, or a version list.
13. `frontend/src/shared/dynamicForm/` contains no reference to `role`, `useSession`, `shared/api/`, `fetch`, or any path under `slices/`; and `isVisibleToCustomer` appears there only in the `FieldDescriptor` interface.
14. No screen renders a raw `isActive` boolean, a raw role integer, a raw `dataType` string outside the editor's `Select`, or the word "Client".
15. The five TicketTypes rows in `can.ts` match `TicketTypesActionCatalogue.cs` exactly — same action names, same role sets, no extras on either side.
