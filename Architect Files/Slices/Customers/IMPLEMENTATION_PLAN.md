# Customers Slice — Implementation Plan

Build this **third**, after `Audit` and `Notifications`. Three slices call it: `Employees`,
`Tickets`, and `Identity` — the last for the login-time Customer status check, which is why
`Identity → Customers` is in the dependency table.

Read these first. This plan is subordinate to all of them — where it disagrees with a numbered
document, the numbered document wins and this plan is wrong:

- [00-Glossary.md](../../00-Glossary.md)
- [01-DomainModel.md](../../01-DomainModel.md) — §2 defines Customer; §1 rule 3 (**no
  `OfficeId`, ever**); §9.2 (nothing is deleted)
- [02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) — §3 is the whole authorization
  spec for this slice; §1 (the four AA-only powers, two of which are here); §11 (suspension
  blocks login)
- [03-SliceInventory.md](../../03-SliceInventory.md) — §2 (`Customers` depends on `Audit` only)
- [App/GeneralAppArchitecture.md](../../App/GeneralAppArchitecture.md) — §4, §5, §6, §7, §8
- [Slices/Audit/IMPLEMENTATION_PLAN.md](../Audit/IMPLEMENTATION_PLAN.md) — build that first

---

## 0. Prerequisites — read before writing any code

### 0.1 What must already exist

| File | Where | Why |
|---|---|---|
| `Shared/Data/RequestConnection.cs` | `Shared/` | Every DbContext registers against the request's shared connection, so the audit write joins the mutation's transaction. |
| `Shared/Data/IRequestTransaction.cs` | `Shared/` | Every mutating handler here wraps its work. |
| `Shared/Auth/CurrentUser.cs` | `Shared/` | Must already carry `Guid? CustomerId` — this slice's scope check is built on it. |
| `Shared/Authorization/IActionCatalogue.cs` | `Shared/` | This slice registers a fragment with **six** actions. |
| `Shared/Scope/CustomerScope.cs` | `Shared/` | `ICustomerScoped` and `WhereInCustomerScope`. |
| `Slices/Audit/ExternalInterfaces/IAuditApi.cs` | `Audit` | This slice's only permitted dependency. |
| `Shared/Auth/DevAuthHandler.cs` | `Shared/` | Without it nothing sets `HttpContext.User`, so **every endpoint returns `401`**. Double-gated on `IsDevelopment()` and `DevAuth:Enabled`; `X-Dev-Role`, `X-Dev-User-Id`, `X-Dev-Customer-Id`. |

### 0.2 This slice is where the Customer scope mechanism gets its first real workout

`Customer` is the **root of the tenant boundary**. Every other Customer-side entity in the
system reaches exactly one Customer (`01-DomainModel.md` §1 rule 1), and this slice owns the row
they all point at.

That has an easily-missed consequence: **`Customer` does not implement `ICustomerScoped` by
carrying a `CustomerId` — its own `Id` *is* the `CustomerId`.** So the scope check here compares
`customer.Id` against `user.CustomerId`, not `customer.CustomerId` against it. If you implement
`ICustomerScoped` on `Customer` with `public Guid CustomerId => Id;` that is acceptable and makes
`WhereInCustomerScope` work unchanged — but write the expression-bodied property, not a mapped
column. **A `customer_id` column on the `customers` table is a mistake** and will be a
self-referencing duplicate of the primary key.

### 0.3 There is no `OfficeId`, and this is the slice most likely to grow one

`01-DomainModel.md` §1 rule 3: the Office is the deployment and has no row anywhere. When you are
writing a table called `customers`, adding an `office_id` to it feels natural and almost every
multi-tenant instinct will suggest it. **Do not.** One deployment serves one Office. A column
"for future multi-tenancy" is a different application, and it would silently become a second
scope dimension that no authorization code checks.

### 0.4 The permission checker — fail-closed

```csharp
Task RequireAsync(CurrentUser user, string action, object? scope = null,
                  CancellationToken ct = default);
```

1. **An unknown action name denies.** Never a default branch that allows.
2. **Every denial is audited** before the exception is thrown.
3. **It is `async` and callers `await` it.** A synchronous signature blocks a request thread on a
   database round-trip and lets an audit-write exception replace the `AppException(403)`, so a
   denied caller gets a `500` and the denial is never recorded.

This slice's catalogue fragment is in §11.2. **Two of the four AA-only powers in the whole system
live in this slice** — create a Customer, and suspend/reactivate one. Getting the role lists right
here is not a detail; it is half of the reason `AccountantAdmin` exists as a distinct role.

### 0.5 Pagination

Use `Shared/Pagination/`. Default `PageSize` **15**, maximum **50**
(`App/GeneralAppArchitecture.md` §8 — these are the system-wide numbers; do not pick different
ones for this slice). Clamp rather than reject: a `PageSize` of 5,000 becomes 50, a `PageNumber`
below 1 becomes 1.

Default sort `legal_name ASC, id ASC` — the `id` tiebreaker matters because two Customers may
legitimately share a trading name, and an unstable sort makes paging skip and repeat rows.

---

## 1. Database schema (SQL migration)

**File:** `Slices/Customers/Infrastructure/Migrations/20260830_001_CreateCustomersSchema.sql`

```sql
CREATE TABLE customers (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Identity of the business. Legal name is the registered name; trading name is what
    -- people actually call it. Both are shown; legal_name is what sorts and what appears
    -- on anything official.
    legal_name          VARCHAR(300) NOT NULL,
    trading_name        VARCHAR(300) NULL,

    -- Tax identity. UNIQUE, because two Customers with the same VAT number is a
    -- double-onboarding mistake, not a legitimate state.
    tax_number          VARCHAR(50)  NOT NULL,
    tax_office          VARCHAR(200) NULL,

    -- Registered address, held as discrete columns rather than one free-text blob so it can
    -- be rendered and corrected field by field.
    address_line1       VARCHAR(200) NOT NULL,
    address_line2       VARCHAR(200) NULL,
    address_city        VARCHAR(100) NOT NULL,
    address_postal_code VARCHAR(20)  NOT NULL,
    address_country     VARCHAR(100) NOT NULL,

    contact_email       VARCHAR(320) NOT NULL,
    contact_phone       VARCHAR(40)  NOT NULL,

    -- 'Active' | 'Suspended'. Text, not a PostgreSQL enum: a new status must not need DDL.
    status              VARCHAR(20)  NOT NULL DEFAULT 'Active',

    -- The business fact of when this Customer became a client. Distinct from created_at,
    -- which is when the row was written. They differ when a Customer is back-filled.
    onboarded_on        DATE         NOT NULL,

    created_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT uq_customers_tax_number UNIQUE (tax_number)
);
```

| Column | Note |
|---|---|
| `legal_name` | `NOT NULL`. A Customer is always a business with a registered name (`01-DomainModel.md` §2 — "always a company, never a natural person"). |
| `trading_name` | Nullable. Many businesses trade under their legal name and have no second one. Do **not** default it to `legal_name` in the database; do that in the read model if the UI wants a single display string. |
| `tax_number` | `UNIQUE`. See §7.1 rule 3 for why the violation must surface as a `409` and not a `500`. |
| `tax_office` | Nullable. Jurisdiction-dependent; some have no such concept. |
| `address_*` | Discrete columns. `line2` nullable, the rest required. |
| `contact_email` | `VARCHAR(320)`: 64-character local part + `@` + 255-character domain, the RFC maximum. **Not `UNIQUE`** — this is the business's contact address, not a login identifier, and two Customers sharing a bookkeeper's address is normal. |
| `contact_phone` | `VARCHAR(40)`, free text. Do not attempt to normalise or validate a phone number beyond a length and a non-empty check; international formats defeat every regex anyone writes. |
| `status` | Only `'Active'` and `'Suspended'` exist. **There is no `'Deleted'`.** |
| `onboarded_on` | `DATE`, not `TIMESTAMPTZ`. It is a calendar fact with no meaningful time of day, and storing it as an instant introduces a timezone question that has no answer. This is the **one** date column in the slice that is not `TIMESTAMPTZ`. |
| `updated_at` | Set by the handler on every write. Not a trigger — this codebase hand-writes its DDL and a hidden trigger is invisible to a reader of the C# code. |

### Indexes

```sql
-- The Accountant's customer list: sorted by name, optionally filtered by status.
CREATE INDEX idx_customers_legal_name ON customers (legal_name, id);

-- Name search. Trigram, because the search must match mid-string ("Ltd" in "Acme Ltd"),
-- which a B-tree on legal_name cannot serve.
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE INDEX idx_customers_name_trgm ON customers USING gin (legal_name gin_trgm_ops);
```

> `CREATE EXTENSION` requires elevated privileges. If the deployment's database user cannot
> create extensions, **stop and raise it** rather than dropping the index — the alternative is a
> sequential scan on every keystroke of the customer picker, and the fix (a `citext`/`lower()`
> prefix index that only matches from the start of the name) changes the search *behaviour*,
> which is a product decision and not yours to make. Flagged as §14 question 4.

`uq_customers_tax_number` already provides the index for the duplicate check; do not add a second
one on `tax_number`.

**No index on `status`.** A one-Office deployment has tens to low hundreds of Customers, and an
index on a two-valued column that is `'Active'` for nearly every row is never chosen by the
planner. Do not add one speculatively.

### No deletes

`02-AuthorizationMatrix.md` §3: *"Delete a Customer — **Nobody.** Customers are never deleted."*
There is no `deleted_at`, no soft delete, no `DELETE` statement, and no `Remove()` call anywhere
in this slice. `Document` is the only entity in the system with a soft delete
(`01-DomainModel.md` §9.2); `Customer` is emphatically not a second one. Offboarding is
`status = 'Suspended'`.

---

## 2. EF Core entities and DbContext

### 2.0 Column naming — mandatory

The SQL above creates `snake_case` columns; EF's convention produces `PascalCase`. **They do not
match**, and every query fails with `column c.LegalName does not exist`. Map every property with
`HasColumnName`, without exception. The in-memory provider ignores column names entirely, which
is why §12.1 exists.

Every timestamp is `DateTimeOffset` against `TIMESTAMPTZ`. The single exception is `OnboardedOn`,
which is `DateOnly` against `DATE`. Never `DateTime` for either.

### 2.1 `Core/Customer.cs`

```csharp
public sealed class Customer : ICustomerScoped
{
    public Guid Id { get; set; }

    public string LegalName { get; set; } = string.Empty;
    public string? TradingName { get; set; }

    public string TaxNumber { get; set; } = string.Empty;
    public string? TaxOffice { get; set; }

    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string AddressCity { get; set; } = string.Empty;
    public string AddressPostalCode { get; set; } = string.Empty;
    public string AddressCountry { get; set; } = string.Empty;

    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;

    public string Status { get; set; } = CustomerStatus.Active;
    public DateOnly OnboardedOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // ICustomerScoped. The Customer IS the scope root — its own Id is the CustomerId.
    // Expression-bodied and NOT mapped: see 0.2. There is no customer_id column.
    public Guid CustomerId => Id;
}

public static class CustomerStatus
{
    public const string Active    = "Active";
    public const string Suspended = "Suspended";
}
```

`CustomerId => Id` **must be marked `[NotMapped]` or excluded via `builder.Ignore(...)`.** EF will
otherwise try to map it to a `CustomerId` column that does not exist, and the model fails to build
at startup — which is the good failure, but only if you recognise the message.

### 2.2 DbContext: `Infrastructure/CustomersDbContext.cs`

```csharp
public sealed class CustomersDbContext : DbContext
{
    // Required. Without this constructor the context cannot be configured with a provider.
    public CustomersDbContext(DbContextOptions<CustomersDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new CustomerConfiguration());
    }
}
```

One entity. No global query filter on this context — read §2.3, which is the single most
dangerous decision in the slice.

### 2.3 Why there is **no** global query filter for Customer scope — read this before disagreeing

It is tempting to add `HasQueryFilter(c => c.Id == currentUserCustomerId)` so a Customer-side
caller can never see another Customer. **Do not.** Three reasons, each sufficient:

1. **Accountants read across all Customers** (`01-DomainModel.md` §1 rule 2). A global filter
   would have to be conditional on the caller's role, which means resolving `CurrentUser` inside
   `OnModelCreating` — and the model is built **once per context type per application**, not per
   request. The first request's caller would be baked into the model for the process lifetime.
   This does not throw. It silently serves one tenant's data to everybody.
2. **`ListCustomers` is AA/AU-only anyway** (`02-AuthorizationMatrix.md` §3), so the listing path
   has no Customer-side caller to filter.
3. **`ICustomerApi` is called by other slices on behalf of Accountants**, and a filter would
   break those lookups in a way whose cause is three slices away from its symptom.

The mechanism instead is explicit: `WhereInCustomerScope(user)` on every read that a Customer-side
role can reach, per `App/GeneralAppArchitecture.md`. It is one call and it is visible in the
handler. The mandatory cross-Customer-`404` test in §12.2 is what proves it was not forgotten.

### 2.4 Configuration

`Infrastructure/Configurations/CustomerConfiguration.cs`. Every property gets `HasColumnName` and,
for strings, `HasMaxLength` matching §1 exactly. The lengths are not decoration — §7.0 rule E
validates against them, and a mismatch means the validation passes and the insert fails with a
`22001` that, under the transaction rule, rolls back the audit entry too.

`OnboardedOn` needs `.HasColumnType("date")`. `CustomerId` needs `builder.Ignore(c => c.CustomerId)`.

---

## 3. DTOs

**Folder:** `Slices/Customers/Application/Dtos/`

| DTO | Shape |
|---|---|
| `CustomerDto` | `Id`, `LegalName`, `TradingName`, `TaxNumber`, `TaxOffice`, address fields, `ContactEmail`, `ContactPhone`, `Status`, `OnboardedOn`, `CreatedAt`, `UpdatedAt` |
| `CustomerSummaryDto` | `Id`, `LegalName`, `TradingName`, `Status` — the list row |
| `CustomerSelfDto` | `Id`, `LegalName`, `TradingName`, address fields, `ContactEmail`, `ContactPhone`, `Status` — **no `TaxNumber`, no `TaxOffice`, no timestamps** |
| `CreateCustomerRequestDto` | every writable field plus `OnboardedOn` |
| `UpdateCustomerContactRequestDto` | `CustomerId`, address fields, `ContactEmail`, `ContactPhone` |
| `UpdateCustomerLegalRequestDto` | `CustomerId`, `LegalName`, `TradingName`, `TaxNumber`, `TaxOffice` |
| `ListCustomersRequestDto` | `Status` (string?), `Search` (string?), `PageNumber`, `PageSize` |
| `GetCustomerRequestDto` | `CustomerId` |
| `SetCustomerStatusRequestDto` | `CustomerId`, `Reason` (string?, max 500) |

### 3.1 `CustomerSelfDto` exists because of one cell in the matrix

`02-AuthorizationMatrix.md` §3: *"View own Customer's details | Yes | Yes | Yes | **Yes,
read-only, limited fields**"*. That "limited fields" is normative and it is the only place the
matrix asks for a *narrower projection* rather than a different permission.

**The limitation must be a different DTO, not a nulled-out field on `CustomerDto`.** Returning
`CustomerDto` with `TaxNumber = null` for an Employee is how the field comes back the next time
someone adds a mapping line, and `02-AuthorizationMatrix.md` §12 rule 2 is explicit: out-of-scope
data must be **absent from the API response**, not merely blanked. A type that has no such
property cannot leak it.

Which fields are withheld from an `Employee`, and why: `TaxNumber` and `TaxOffice` are the
employer's tax identity, which an individual employee has no business need for and which is
directly useful for impersonating the business. Timestamps are withheld as ordinary
minimisation, not because they are sensitive.

> A `CustomerAdmin` gets the **full** `CustomerDto` for their own Customer — the matrix gives
> them "full visibility within it" (§1) and lets them edit contact details, which requires seeing
> them. Only `Employee` is narrowed. Do not narrow both because they are both "Customer-side".

### 3.2 Request DTO shape

Request DTOs are plain classes with public getters and setters, not positional `record`s —
minimal-API binding from a query string does not populate positional records. Response DTOs may
be `record`s.

---

## 4. Handlers

**Folder:** `Slices/Customers/Application/Handlers/`

### 4.0 Rules that apply to every handler in this slice

Canonical signature, no mediator, one handler per operation:

```csharp
public async Task<TResponse> Handle(TRequest req, CurrentUser user, CancellationToken ct)
```

**A. Every mutating handler wraps its work in `IRequestTransaction.BeginAsync`** and commits at
the end. The audit write enlists itself, so the entry and the mutation commit together or not at
all.

**B. Every mutating handler writes an audit entry** with `Before` and `After` for an update. A
create has `After` only; a status change has both, and both must include the `Status` field or the
entry records nothing useful.

**C. Read handlers open no transaction and audit nothing.** Reading a Customer is not in the
audited action set (`01-DomainModel.md` §8) and auditing every list would swamp the log.

**D. Out of scope is `404`, never `403`.** `02-AuthorizationMatrix.md` §1 and §12 rule 3. A `403`
on another Customer's id confirms it exists.

**E. Validate every string against the `HasMaxLength` in §2.4**, and return `422` on violation —
never let the database raise `22001`. Trim leading and trailing whitespace on every string before
validating, or `"  "` passes a required-field check.

**F. `AsNoTracking()` on reads.** Update handlers need tracking; nothing else does.

**G. No handler in this slice calls another slice.** `Customers` depends on `Audit` only.

### 4.1 `CreateCustomerHandler` — **AccountantAdmin only**

Request `CreateCustomerRequestDto` → `CustomerDto`

```
await _permissions.RequireAsync(user, "CreateCustomer", ct: ct)   // AA only

validate:
  LegalName          required, trimmed, 1..300          → 422
  TradingName        optional, <= 300                   → 422
  TaxNumber          required, trimmed, 1..50           → 422
  TaxOffice          optional, <= 200
  AddressLine1/City/PostalCode/Country  required        → 422
  ContactEmail       required, <= 320, contains '@'     → 422
  ContactPhone       required, <= 40                    → 422
  OnboardedOn        required; not more than 1 day in the future → 422

if await _db.Customers.AnyAsync(c => c.TaxNumber == req.TaxNumber, ct)
    → AppException("A customer with this tax number already exists.", 409)

await using var tx = await _transaction.BeginAsync(_db, ct)
entity = new Customer { ... Status = CustomerStatus.Active,
                        CreatedAt = now, UpdatedAt = now }
_db.Customers.Add(entity)
await _db.SaveChangesAsync(ct)

await _audit.LogAsync(new AuditEntry(
    AuditActions.CustomerCreated, AuditTargets.Customer, entity.Id.ToString(),
    CustomerId: entity.Id, After: <the new values>), ct)

await tx.CommitAsync(ct)
return CustomerMapper.ToDto(entity)
```

Rules:

1. **AA only.** This is AA-only power 1 of 4 (`02-AuthorizationMatrix.md` §1). An
   `AccountantUser` gets `403`. Do not "helpfully" allow it because an AU can edit everything
   else about a Customer — bringing a Customer into existence is precisely the line the four
   powers draw.
2. **The pre-check on `tax_number` is a courtesy, not the guarantee.** Two concurrent creates both
   pass the `AnyAsync` and one hits the unique constraint. **Both paths must produce the same
   `409`** — catch `DbUpdateException` wrapping a `PostgresException` with `SqlState == "23505"`
   and throw `AppException(..., 409)`. `App/GeneralAppArchitecture.md` §8 is absolute: anything a
   client can trigger by sending a request is a `4xx`. Do not solve the race with a lock or a
   retry loop.
3. **`Status` is always `Active` on create.** There is no way to create a suspended Customer, and
   `CreateCustomerRequestDto` must not have a `Status` field. A create endpoint that accepts a
   status is how a Customer gets created in a state no audit entry explains.
4. **`OnboardedOn` is supplied by the caller, not defaulted to today.** A Customer being
   back-filled was onboarded before the app existed. Reject a date more than one day in the
   future — "one day" rather than "today" because the caller's timezone may legitimately be ahead
   of the server's.
5. **This handler creates a Customer and nothing else.** It does **not** create the first Customer
   Admin, even though `02-AuthorizationMatrix.md` §3 says creating a Customer includes that "in one
   operation". That composite operation is **LOCKED to the `Employees` slice**
   ([03-SliceInventory.md](../../03-SliceInventory.md) §1): this slice may depend only on `Audit`,
   so `Customers → Employees` would be a cycle, while `Employees` already depends on `Customers`,
   `Identity`, and `Notifications`.

   So this handler is a **building block**, and it must stay one:

   - It is a public endpoint in its own right (`/api/customers/create`, `AA`-only). An
     `AccountantAdmin` back-filling a Customer that already has accounts uses it directly.
   - `Employees`' composite handler calls it **through `ICustomerApi`**, inside that request's
     transaction, so a failure creating the first admin leaves no Customer behind. This is why the
     mutation is wrapped in `IRequestTransaction.BeginAsync` rather than relying on
     `SaveChangesAsync` alone.
   - Do **not** add a dependency edge from this slice to `Employees` or `Identity`, and do not add
     an optional `FirstAdmin` block to `CreateCustomerRequestDto`. Either one recreates the cycle
     the decision exists to avoid.

   > `ICustomerApi` as specified in §5 is **read-only** and has no create method. Whether
   > `Employees` calls this handler directly (both are registered in the same container) or
   > `ICustomerApi` gains a `CreateAsync` is a decision for the **`Employees` plan** — it is that
   > slice's call site, and a write method on a read contract is a change to this slice's public
   > surface. Flag it there; do not add the method here pre-emptively.

### 4.2 `ListCustomersHandler` — **Accountants only**

Request `ListCustomersRequestDto` → `PaginatedResponse<CustomerSummaryDto>`

```
await _permissions.RequireAsync(user, "ListCustomers", ct: ct)   // AA + AU only

clamp page/size (default 15, max 50)
if req.Status is not null and not in { Active, Suspended } → 422

query = _db.Customers.AsNoTracking()
if req.Status is not null      → query = query.Where(c => c.Status == req.Status)
if req.Search is not null      → normalise, then
    query = query.Where(c => EF.Functions.ILike(c.LegalName, $"%{term}%")
                          || EF.Functions.ILike(c.TradingName!, $"%{term}%"))

total = await query.CountAsync(ct)
items = await query.OrderBy(c => c.LegalName).ThenBy(c => c.Id)
                   .Skip(...).Take(...)
                   .Select(CustomerMapper.ToSummaryExpression)
                   .ToListAsync(ct)
```

Rules:

1. **The endpoint rejects the role; it does not return an empty list.**
   `02-AuthorizationMatrix.md` §12 rule 4 is explicit — a cross-Customer listing endpoint must
   **reject** a Customer-side role, not filter it to nothing. So there is no
   `WhereInCustomerScope` here: a `CustomerAdmin` never reaches the query at all. Returning an
   empty page instead would be a quiet invitation to later "fix" it by adding a scope filter, at
   which point the endpoint becomes a cross-Customer listing that happens to be filtered — one
   refactor away from a leak.
2. **An unrecognised `Status` filter is `422`, not an empty page.** A typo'd filter returning zero
   rows tells the caller "there are no suspended customers", which is a different and false
   statement.
3. **`ILike`, not `ToLower().Contains()`.** `ToLower()` on both sides defeats the trigram index
   and is not correct for every locale. `EF.Functions.ILike` translates to PostgreSQL `ILIKE`,
   which the `gin_trgm_ops` index in §1 serves.
4. **Escape `%` and `_` in the search term** before interpolating. A search for `%` otherwise
   matches every row, and a user typing an underscore gets silently wrong results. This is not an
   injection risk — EF parameterises the value — but it is a correctness bug.
5. **`TradingName` is nullable**, so the `ILike` on it must not throw on `null`. PostgreSQL
   returns `NULL` (falsy) for `NULL ILIKE ...`, so the `||` behaves correctly — but the C# null
   forgiveness operator is required to satisfy the compiler and its presence should be commented,
   or someone will "fix" it into a client-side evaluation.
6. **Cap the search term at 200 characters** → `422`. An unbounded `%term%` against a trigram
   index degrades badly.

### 4.3 `GetCustomerHandler` — all four roles, three different projections

Request `GetCustomerRequestDto` → `object` is **wrong**; see rule 2.

```
await _permissions.RequireAsync(user, "ViewCustomer", ct: ct)   // all four roles

var query = _db.Customers.AsNoTracking().Where(c => c.Id == req.CustomerId)
                         .WhereInCustomerScope(user)

var entity = await query.FirstOrDefaultAsync(ct)
if (entity is null) throw new AppException("Customer not found.", 404)
```

Rules:

1. **`WhereInCustomerScope(user)` is applied even though this is a single-record read by
   identifier.** `App/GeneralAppArchitecture.md` is explicit that the extension is called on
   single-record reads too. An Accountant passes through it unchanged; a Customer-side caller is
   filtered to their own row, so an out-of-scope id yields `null` and therefore `404` — with no
   second lookup and no `403`. Do not "optimise" the filter away because the id is already known.
2. **Two endpoints, not one polymorphic response.** An `Employee` needs `CustomerSelfDto`;
   everyone else needs `CustomerDto`. Returning `object` or a union type makes the SPA guess, and
   makes it impossible to state a contract. Split it:
   - `GetCustomerHandler` → `CustomerDto`, permitted to `AA`, `AU`, `CA`
   - `GetOwnCustomerHandler` → `CustomerSelfDto`, permitted to `CA`, `Employee`, taking **no
     parameters at all** and reading `user.CustomerId`
3. **`GetOwnCustomerHandler` accepts no `CustomerId`.** The Customer is `user.CustomerId`, taken
   from the principal. A parameter you never accept cannot be used to read another Customer.
   Throw `AppException(401)` if `user.CustomerId` is null — that means an Accountant called a
   Customer-side endpoint, or the claim was dropped.
4. A `CustomerAdmin` may use either endpoint; the full DTO is theirs by right (§3.1).

### 4.4 `UpdateCustomerContactHandler` — `AA`, `AU`, and `CA` for their own

Request `UpdateCustomerContactRequestDto` → `CustomerDto`

```
await _permissions.RequireAsync(user, "EditCustomerContact", ct: ct)   // AA, AU, CA

var entity = await _db.Customers
    .Where(c => c.Id == req.CustomerId).WhereInCustomerScope(user)
    .FirstOrDefaultAsync(ct)
if (entity is null) throw new AppException("Customer not found.", 404)

validate the address, email and phone exactly as in 4.1

await using var tx = await _transaction.BeginAsync(_db, ct)
var before = CustomerMapper.ToAuditSnapshot(entity)   // BEFORE mutating
entity.AddressLine1 = ...; ... ; entity.UpdatedAt = now
await _db.SaveChangesAsync(ct)
await _audit.LogAsync(new AuditEntry(AuditActions.CustomerUpdated, AuditTargets.Customer,
    entity.Id.ToString(), CustomerId: entity.Id,
    Before: before, After: CustomerMapper.ToAuditSnapshot(entity)), ct)
await tx.CommitAsync(ct)
```

Rules:

1. **Capture `Before` from the tracked entity *before* assigning any property.** A snapshot taken
   after mutation records the new values twice and the audit entry becomes worthless while looking
   fine. Materialise it into a new object — do **not** hold a reference to the entity and expect
   it to still show old values.
2. **`WhereInCustomerScope` again**, so a `CustomerAdmin` editing another Customer's contact
   details gets `404`.
3. **This handler must not touch `LegalName`, `TradingName`, `TaxNumber`, or `TaxOffice`**, and
   `UpdateCustomerContactRequestDto` must not contain them. A `CustomerAdmin` may reach this
   handler and the matrix forbids them editing legal name or tax number — if the DTO carries the
   field, the only thing standing between a `CustomerAdmin` and their VAT number is a handler
   remembering not to assign it. Omit the fields from the type.
4. **A suspended Customer's contact details may still be edited by an Accountant.** Suspension
   blocks the Customer's *people* from logging in (`02-AuthorizationMatrix.md` §11); it does not
   freeze the record. A `CustomerAdmin` of a suspended Customer cannot reach this handler anyway,
   because they cannot log in.

### 4.5 `UpdateCustomerLegalHandler` — Accountants only

Request `UpdateCustomerLegalRequestDto` → `CustomerDto`

Same shape as 4.4, with three differences:

1. Action is `"EditCustomerLegal"`, permitted to `AA` and `AU` **only**
   (`02-AuthorizationMatrix.md` §3: *"Edit Customer legal name or tax number | Yes | Yes | No |
   No"*). Note this is **not** an AA-only power — an `AccountantUser` may do it. Do not add it to
   the four.
2. **Changing `TaxNumber` re-runs the uniqueness check**, excluding the row being edited
   (`c.Id != req.CustomerId`), and must handle the `23505` race the same way as 4.1 — same `409`,
   same message.
3. No `WhereInCustomerScope` is required for correctness because only Accountants reach it, but
   **call it anyway**. It is a no-op for Accountants, and its presence means the handler stays
   correct if the role list ever widens. Consistency here is cheaper than remembering.

### 4.6 `SetCustomerStatusHandler` — **AccountantAdmin only**

One handler for both directions, or two? **Two: `SuspendCustomerHandler` and
`ReactivateCustomerHandler`.** A single handler taking a target status invites a request that sets
the status to an arbitrary string, and it makes the two audit action codes conditional. Two
handlers, two endpoints, two action codes, each with a straight-line body.

```
// SuspendCustomerHandler
await _permissions.RequireAsync(user, "SuspendCustomer", ct: ct)   // AA only

var entity = await _db.Customers.FirstOrDefaultAsync(c => c.Id == req.CustomerId, ct)
if (entity is null) throw new AppException("Customer not found.", 404)
if (entity.Status == CustomerStatus.Suspended)
    throw new AppException("This customer is already suspended.", 422)

await using var tx = await _transaction.BeginAsync(_db, ct)
var before = ToAuditSnapshot(entity)
entity.Status = CustomerStatus.Suspended
entity.UpdatedAt = now
await _db.SaveChangesAsync(ct)
await _audit.LogAsync(new AuditEntry(AuditActions.CustomerSuspended, AuditTargets.Customer,
    entity.Id.ToString(), CustomerId: entity.Id,
    Before: before, After: ToAuditSnapshot(entity)), ct)
await tx.CommitAsync(ct)
```

Rules:

1. **AA only.** AA-only power 2 of 4.
2. **Already-in-that-state is `422`, not a silent success.** An idempotent no-op here hides a
   mistake: the operator believes they suspended a Customer at 14:02 and the audit log has no
   entry for it. Reactivation is symmetric.
3. **Suspension changes the `customers` row and nothing else.** It does not suspend UserAccounts,
   does not cancel Tickets, does not touch Employees. `02-AuthorizationMatrix.md` §11 is that
   suspension *blocks login* — a check performed at login time against the current status, not a
   cascade of writes. If you find yourself writing an `UPDATE user_accounts` here, stop: this
   slice cannot reach that table and the model does not want it to.
4. **`Reason` is captured in the audit entry, not in a column.** There is no `suspension_reason`
   on `customers`. The audit log is where the *why* of a status change lives, and a column would
   hold only the most recent one while the log holds all of them.
5. **Reactivating a Customer does not reactivate its users.** Their accounts have their own
   status, owned by `Identity`. A Customer reactivated whose Customer Admin is still `Suspended`
   still cannot log in, and that is correct — flag it in the UI, do not fix it here.

### 4.7 `CustomerMapper`

`Application/CustomerMapper.cs`. Three members:

- `ToDto(Customer)` — an ordinary method, called on a materialised entity
- `ToSummaryExpression` — an `Expression<Func<Customer, CustomerSummaryDto>>`
- `ToSelfDto(Customer)` — ordinary method

**`ToSummaryExpression` must be an `Expression`, not a static method with a body.** Used inside
`.Select(...)`, a statement-bodied method is either untranslatable or silently evaluated
client-side after fetching every column of every row — which defeats the entire point of a
summary projection and is invisible until a Customer has a large record.

`ToAuditSnapshot(Customer)` returns an anonymous or small record carrying the mutable fields only.
It must **not** include `CreatedAt` (never changes) and must **not** be the entity itself
(reference semantics, see 4.4 rule 1).

---

## 5. The `ICustomerApi` contract

**Files:** `Slices/Customers/ExternalInterfaces/ICustomerApi.cs`, `CustomerApi.cs`

Three slices call this: `Employees`, `Tickets`, and `Identity`. The `Identity` caller is the newest
and the least obvious — it needs `IsActiveAsync` at login time, because
`02-AuthorizationMatrix.md` §11 requires a Customer-side actor's Customer to be `Active` before
they may authenticate. `Identity → Customers` is a permitted edge for exactly that
([03-SliceInventory.md](../../03-SliceInventory.md) §2) and for nothing else.

```csharp
public sealed record CustomerSummary(
    Guid Id,
    string LegalName,
    string? TradingName,
    string Status)
{
    public bool IsActive => Status == "Active";
}

public interface ICustomerApi
{
    /// <summary>Null when no such Customer exists. Does NOT apply Customer scope — the
    /// caller is responsible for its own scope check.</summary>
    Task<CustomerSummary?> FindAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>True only when the Customer exists AND is Active. Callers use this to
    /// refuse work for a suspended Customer.</summary>
    Task<bool> IsActiveAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>Bulk lookup for list rendering. Missing ids are simply absent from the
    /// result — it is not an error to ask about one that does not exist.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> customerIds, CancellationToken ct = default);
}
```

Rules:

1. **It returns `CustomerSummary`, never the `Customer` entity.** Dependency rule 4. A caller
   holding a tracked `Customer` could mutate it and save through another slice's context.
2. **It exposes no tax number and no address.** A calling slice needs a Customer's name for
   display and its status for a decision. Nothing in `Employees` or `Tickets` needs the VAT
   number, and an `ExternalInterface` that carries it makes every consumer a potential leak path
   for a field that `CustomerSelfDto` deliberately withholds.
3. **`FindManyAsync` exists so callers do not loop.** `Tickets` rendering a 100-row list needs
   100 Customer names; a per-row `FindAsync` is 100 queries. Provide the bulk method and say in
   the `Tickets` plan to use it. Cap the input at 500 ids and throw
   `InvalidOperationException` above that — a caller passing an unbounded id list is a bug in the
   caller.
4. **It applies no scope filter and says so in the summary comment.** The caller knows its own
   scope rules; a hidden filter here would make an Accountant-initiated lookup from another slice
   fail for reasons invisible at the call site. This is the opposite of the handler rule in §4.0 D,
   and the difference is deliberate: handlers serve a request, `ICustomerApi` serves another
   slice's already-authorized logic.
5. **It writes no audit entries.** A lookup is not an audited action, and auditing it would record
   an entry for every row of every ticket list.
6. **It opens no transaction**, and it must tolerate being called inside the caller's transaction —
   which it will be, because it shares the request connection.
7. `IsActiveAsync` is a separate method rather than `FindAsync(...)?.IsActive`, so a caller cannot
   accidentally treat "not found" as "not active" *or* as "active" — the two-valued return forces
   the question. `FindAsync` returning `null` and `IsActiveAsync` returning `false` are different
   facts and callers that need to distinguish them use the former.

   This matters most for the `Identity` caller: `IsActiveAsync` is what stands between a suspended
   Customer and its people logging in. A `bool?` here, or a `FindAsync(...)?.IsActive ?? true`
   written at the call site, turns "I could not find that Customer" into "let them in". The method
   returns `false` for both "suspended" and "no such Customer", which is the fail-closed answer.

8. **It caches nothing.** No `IMemoryCache`, no static dictionary, no per-request memo. The status
   is read from the row on every call, because `02-AuthorizationMatrix.md` §11 requires suspension
   to block login **immediately**. A cache with any TTL is a window in which a suspended Customer
   can still authenticate.

---

## 6. Cross-slice boundaries

`Customers` depends on **`Audit` only** ([03-SliceInventory.md](../../03-SliceInventory.md) §2).

1. **This slice never calls `Employees`.** `Employees → Customers` exists, so the reverse is a
   cycle. Dependency rule 3: no slice reaches upward.
2. **This slice never calls `Identity`.** Not to create the first Customer Admin — that composite
   operation belongs to `Employees` (§4.1 rule 5) — and not to suspend accounts when a Customer is
   suspended (§4.6 rule 3). The edge runs the other way: `Identity → Customers`, for the login
   check. So a call from here would be a cycle.
3. **This slice never calls `Notifications`.** No Customer lifecycle event notifies anybody in
   v1 — the people who would be notified are the ones being locked out. If a "your account's
   business has been suspended" email is wanted, that is a new requirement; flag it.
4. **No foreign key points *out* of `customers`.** Nothing here references another slice's table.
   Foreign keys pointing *in* — `employees.customer_id`, `tickets.customer_id` — are declared by
   those slices' migrations, which run after this one. That ordering is why this slice's migration
   date prefix must sort before theirs.
5. **This slice does not know how many Employees or Tickets a Customer has.** A count on the
   Customer list row would require reading another slice's table. If the UI wants counts, they
   come from `Employees` and `Tickets` in separate calls, joined client-side.

---

## 7. Migrations

- Scripts in `Slices/Customers/Infrastructure/Migrations/`, named `YYYYMMDD_###_Description.sql`.
- **EF Core migrations are not used.** Never run `dotnet ef migrations add` or
  `dotnet ef database update`.
- **The tracking key is the slice-relative path with forward slashes**, in
  `schema_versions.script_name VARCHAR(500)` — never `Path.GetFileName`. Sequence numbers restart
  at `001` per slice, so bare filenames collide across slices and the second one is silently
  skipped.
- This slice's key is exactly
  `Customers/Infrastructure/Migrations/20260830_001_CreateCustomersSchema.sql`.
- **This script must run before `Employees` and `Tickets`**, whose tables carry a foreign key to
  `customers`. The runner orders by the datetime prefix, so keep this slice's prefix at or before
  theirs. If the runner orders by full path instead, `Customers` sorts before `Employees` and
  `Tickets` alphabetically anyway — but do not rely on that accident; verify which ordering the
  runner actually uses and say so.

---

## 8. Endpoints

**File:** `Slices/Customers/CustomersEndpoints.cs`

Route shape `/api/{domain}/{action}`, path segments lowercase and **kebab-case at every word
boundary**.

```csharp
var group = app.MapGroup("/api/customers");

group.MapPost("/create",        ...);
group.MapPost("/list",          ...);
group.MapGet ("/detail",        ...);   // ?customerId=
group.MapGet ("/own",           ...);   // no parameters
group.MapPost("/update-contact",...);
group.MapPost("/update-legal",  ...);
group.MapPost("/suspend",       ...);
group.MapPost("/reactivate",    ...);
```

| Route | Verb | Roles | Note |
|---|---|---|---|
| `/api/customers/create` | `POST` | **AA** | Mutating. |
| `/api/customers/list` | `POST` | AA, AU | `POST` for the filter body. Non-mutating: no transaction, no audit. **Rejects Customer-side roles outright.** |
| `/api/customers/detail` | `GET` | AA, AU, CA | `?customerId=` |
| `/api/customers/own` | `GET` | CA, EMP | **No parameters.** Reads `user.CustomerId`. |
| `/api/customers/update-contact` | `POST` | AA, AU, CA | **Kebab-case.** `/updatecontact` is unreadable and `/update_contact` is the wrong convention. |
| `/api/customers/update-legal` | `POST` | AA, AU | Kebab-case. |
| `/api/customers/suspend` | `POST` | **AA** | Mutating. |
| `/api/customers/reactivate` | `POST` | **AA** | Mutating. |

- **No route parameters.** Never `/api/customers/{id}`; the locked shape is `{domain}/{action}`
  and an identifier is not an action. It goes in the query string or the body.
- **Query and body parameter names stay camelCase** (`?customerId=`). Kebab-case is for path
  segments only.
- **There is no `/api/customers/delete`.** Not disabled, not returning `405` — absent.
- Handlers are injected per endpoint; do not resolve them from `IServiceProvider` in the lambda.
- No `.RequireAuthorization()` policy names. Authorization is `IPermissionChecker` in the handler.
- `/api/customers/own` and `/api/customers/detail` are separate routes rather than one route that
  branches on role, because they return **different types** (§4.3 rule 2).

---

## 9. Audit action codes

These belong in `Slices/Audit/ExternalInterfaces/AuditActions.cs`, grouped under `Customers`.
They are listed here so the `Audit` slice's catalogue can be completed:

| Constant | Written by |
|---|---|
| `CustomerCreated` | `CreateCustomerHandler` |
| `CustomerUpdated` | `UpdateCustomerContactHandler`, `UpdateCustomerLegalHandler` |
| `CustomerSuspended` | `SuspendCustomerHandler` |
| `CustomerReactivated` | `ReactivateCustomerHandler` |

`AuditTargets.Customer` is the target kind for all four.

**`CustomerUpdated` is shared by both update handlers**, and the `Before`/`After` payload is what
distinguishes a contact edit from a legal-name edit. Two separate codes would be defensible; one
is chosen because an investigator searching "what changed about this Customer" wants one query.
If you split them, add both to `AuditActions` — an uncatalogued code throws (`Audit` plan §5.2
rule D).

---

## 10. Service registration

### 10.1 `Slices/Customers/CustomersRegistration.cs`

```csharp
public static IServiceCollection AddCustomersSlice(
    this IServiceCollection services, IConfiguration configuration)
{
    // (sp, o) — NOT o =>. The context must use the request's shared connection so the
    // audit write joins this slice's transaction. See 10.2 trap 2.
    services.AddDbContext<CustomersDbContext>((sp, o) =>
        o.UseNpgsql(sp.GetRequiredService<RequestConnection>().Connection));

    services.AddScoped<ICustomerApi, CustomerApi>();
    services.AddSingleton<IActionCatalogue, CustomersActionCatalogue>();

    services.AddScoped<CreateCustomerHandler>();
    services.AddScoped<ListCustomersHandler>();
    services.AddScoped<GetCustomerHandler>();
    services.AddScoped<GetOwnCustomerHandler>();
    services.AddScoped<UpdateCustomerContactHandler>();
    services.AddScoped<UpdateCustomerLegalHandler>();
    services.AddScoped<SuspendCustomerHandler>();
    services.AddScoped<ReactivateCustomerHandler>();

    return services;
}
```

### 10.2 `Slices/Customers/CustomersActionCatalogue.cs`

```csharp
internal sealed class CustomersActionCatalogue : IActionCatalogue
{
    public string SliceName => "Customers";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } =
        new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
        {
            // AA-only power 1 of 4.
            ["CreateCustomer"]      = [UserRole.AccountantAdmin],

            // AA-only power 2 of 4. Both directions.
            ["SuspendCustomer"]     = [UserRole.AccountantAdmin],
            ["ReactivateCustomer"]  = [UserRole.AccountantAdmin],

            // Cross-Customer listing: Accountants only. The endpoint REJECTS a
            // Customer-side role — it does not return an empty page.
            ["ListCustomers"]       = [UserRole.AccountantAdmin, UserRole.AccountantUser],

            // Editing legal name / tax number is NOT an AA-only power. An AccountantUser
            // may do it; a CustomerAdmin may not.
            ["EditCustomerLegal"]   = [UserRole.AccountantAdmin, UserRole.AccountantUser],

            // Contact details are routine work: both Accountants, plus a CustomerAdmin for
            // their own Customer. The "own" part is the scope check, not the role list.
            ["EditCustomerContact"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                                       UserRole.CustomerAdmin],

            // Everyone may view a Customer. WHICH Customer, and which FIELDS, are decided
            // by WhereInCustomerScope and by the DTO — not here.
            ["ViewCustomer"]        = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                                       UserRole.CustomerAdmin,   UserRole.Employee]
        };
}
```

**The role list is only half of each decision.** `ViewCustomer` grants all four roles, and the
tenant boundary is held entirely by `WhereInCustomerScope` in the handler. A reader who sees four
roles and concludes the scope check is redundant has inverted the design. `02-AuthorizationMatrix.md`
§1: *"Passing the role check alone is never sufficient."*

**No empty role array.** An action mapped to `[]` is a startup failure, not a
deny-everyone shorthand. If nobody may do a thing, there is no action and no endpoint — which is
why `DeleteCustomer` does not appear above.

### 10.3 What `Program.cs` adds

```csharp
builder.Services.AddCustomersSlice(builder.Configuration);
// ...
app.MapCustomersEndpoints();
```

Two lines, naming no handler or DbContext type.

### 10.4 Registration traps

1. **`AddScoped<CustomersDbContext>()` instead of `AddDbContext`** — registers the context with
   no provider. If both are present the later wins and silently discards the options.
2. **The `o =>` overload instead of `(sp, o) =>`** — compiles and works, but the context gets its
   own connection, so the audit entry commits independently of the mutation. A create that then
   fails leaves an audit entry for a Customer that does not exist, and a create that succeeds
   with a failing audit write leaves a Customer nobody can attribute. Invisible until you
   specifically test that a failing audit write rolls back the mutation (§12.1 case 6).
3. **Forgetting `builder.Ignore(c => c.CustomerId)`** — EF tries to map the `ICustomerScoped`
   property to a nonexistent column and the model fails to build at startup.
4. **Registering handlers in `Program.cs`** — forbidden. Assembly scanning is banned.
5. **Registering `ICustomerApi` as a singleton** — it holds a scoped DbContext. Scoped.

### 10.5 Startup smoke check — before writing tests

```bash
docker compose up -d db
dotnet build
dotnet run --project AccountantApp.Api
```

```bash
# AA can create
curl -i -X POST -H "X-Dev-Role: AccountantAdmin" -H "Content-Type: application/json" \
  -d '{"legalName":"Acme Ltd","taxNumber":"EL123456789","addressLine1":"1 Main St",
       "addressCity":"Athens","addressPostalCode":"10001","addressCountry":"GR",
       "contactEmail":"info@acme.example","contactPhone":"+302100000000",
       "onboardedOn":"2026-01-15"}' \
  http://localhost:5000/api/customers/create

# AU cannot — expect 403, not 500 and not 200
curl -i -X POST -H "X-Dev-Role: AccountantUser" -H "Content-Type: application/json" \
  -d '{...}' http://localhost:5000/api/customers/create

# CA cannot list — expect 403
curl -i -X POST -H "X-Dev-Role: CustomerAdmin" -H "X-Dev-Customer-Id: <guid>" \
  -H "Content-Type: application/json" -d '{}' http://localhost:5000/api/customers/list
```

**A `401` from any of these proves nothing** except that `DevAuth` is off — check
`IsDevelopment()` and `DevAuth:Enabled` in `appsettings.Development.json`.

**Do not comment out `SqlMigrationRunner.RunAsync` to make startup succeed without a database.**
If it throws `Failed to connect`, start the database. A run that skips migrations has verified
nothing about the schema, which is the only thing that could have gone wrong.

---

## 11. Tests

### 11.1 At least one test must run against real PostgreSQL — mandatory

`Microsoft.EntityFrameworkCore.InMemory` is **banned from the API project**; it is a test-only
dependency. It ignores `HasColumnName`, `NOT NULL`, `TIMESTAMPTZ`, `DATE`, string lengths, unique
constraints, and `ILIKE` — every single thing §1 and §2 exist to get right. A green in-memory
suite is not evidence this slice works. In particular, **the `409`-on-duplicate-tax-number path
cannot be tested in memory at all**, because there is no unique constraint to violate.

One test against a real database must cover:

1. **The migration applies** — `SqlMigrationRunner.RunAsync` succeeds on a scratch database, and
   the `pg_trgm` extension is created (or the test fails loudly, per §1).
2. **Tracked by slice-relative path** — `schema_versions.script_name` equals
   `Customers/Infrastructure/Migrations/20260830_001_CreateCustomersSchema.sql`, not the bare
   filename.
3. **A Customer round-trips** through `CreateCustomerHandler` and `GetCustomerHandler`, exercising
   every `HasColumnName` in both directions.
4. **`OnboardedOn` round-trips as a `DateOnly`** with no timezone shift. Write `2026-01-15`, read
   back `2026-01-15` — not the 14th. This is the failure a `DateTime`/`DATE` mix-up produces, and
   it produces wrong data rather than an error.
5. **`CreatedAt` survives as an instant** — write a `DateTimeOffset` with a non-zero offset, read
   it back, assert `UtcDateTime` matches.
6. **A failing audit write rolls back the Customer create.** Force `IAuditApi` to throw, call
   `CreateCustomerHandler`, assert the `customers` table is empty. This is the only test that
   catches trap 10.4.2.
7. **A duplicate `tax_number` returns `409`, not `500`** — including when the pre-check is bypassed
   by inserting the conflicting row directly, which is the only way to exercise the `23505` catch.
8. **`ILike` search matches mid-string** — a Customer named `Acme Ltd` is found by searching
   `cme`, which proves the query reached PostgreSQL rather than being evaluated client-side.

Skip it **loudly** when no database is reachable — `Skip.IfNot(...)` with a message saying the
schema is unverified. Never let it pass silently.

### 11.2 Behavioural cases (in-memory acceptable)

| Case | Expected |
|---|---|
| `AA` creates a Customer | `200`, status `Active` |
| `AU` creates a Customer | **`403`** |
| `CA` creates a Customer | `403` |
| `EMP` creates a Customer | `403` |
| Create with a blank `LegalName` | `422` |
| Create with `LegalName` of 301 characters | `422` |
| Create with whitespace-only `LegalName` | `422` (trimmed first) |
| Create with `OnboardedOn` a year in the future | `422` |
| Create with an `OnboardedOn` of yesterday | `200` |
| Create with a duplicate `tax_number` | `409` |
| `CreateCustomerRequestDto` has a `Status` property | **compile-time absent** — assert by inspection |
| `AA`/`AU` list Customers | `200` |
| `CA` lists Customers | **`403`, not an empty page** |
| `EMP` lists Customers | `403` |
| List with `Status = "Deleted"` | `422` |
| List with a 300-character search term | `422` |
| List with a search term of `%` | matches literally, not everything |
| Paging | ordered `legal_name ASC, id ASC`; `PageSize` 5,000 clamps to 50; `PageNumber` 0 clamps to 1 |
| `AA` gets any Customer's detail | `200`, full `CustomerDto` |
| **`CA` gets another Customer's detail by id** | **`404`, not `403`** |
| **`EMP` gets another Customer's detail by id** | **`404`** |
| `CA` gets `/own` | `200`, full `CustomerDto` |
| `EMP` gets `/own` | `200`, `CustomerSelfDto` **with no `TaxNumber` field present** |
| `AA` calls `/own` | `401` (no `CustomerId` claim) |
| `CA` edits own contact details | `200`, `UpdatedAt` advanced |
| **`CA` edits another Customer's contact details** | **`404`** |
| `EMP` edits contact details | `403` |
| `CA` edits legal name | `403` |
| `AU` edits legal name | `200` |
| `UpdateCustomerContactRequestDto` has a `TaxNumber` property | **compile-time absent** |
| Audit entry for an update | `Before` holds the **old** values, `After` the new |
| `AA` suspends an `Active` Customer | `200`, status `Suspended`, audit entry written |
| `AA` suspends an already-`Suspended` Customer | **`422`** |
| `AU` suspends a Customer | `403` |
| `AA` reactivates a `Suspended` Customer | `200` |
| `AA` reactivates an `Active` Customer | `422` |
| Suspending a Customer | leaves every Employee and UserAccount untouched |
| `ICustomerApi.FindAsync` for an unknown id | `null`, no exception |
| `ICustomerApi.IsActiveAsync` for an unknown id | `false` |
| `ICustomerApi.IsActiveAsync` for a suspended Customer | `false` |
| `ICustomerApi.FindManyAsync` with 3 ids, 1 unknown | 2 entries, no exception |
| `ICustomerApi.FindManyAsync` with 501 ids | `InvalidOperationException` |
| `ICustomerApi.IsActiveAsync`, Customer suspended **after** an earlier call returned `true` | `false` on the next call — proves nothing is cached |
| `CustomerSummary` type | has no `TaxNumber` and no address — assert by inspection |
| Two slices declaring `"ViewCustomer"` | startup throws, naming both slices |

The four rows in bold are the tenant-boundary tests. `App/GeneralAppArchitecture.md` requires a
cross-Customer-`404` test per slice; these are this slice's, and they are mandatory rather than
nice to have.

---

## 12. Known constraints

1. **Customers are never deleted.** No endpoint, no soft delete, no `deleted_at`, no `DELETE`.
   Suspension is the only offboarding mechanism (`02-AuthorizationMatrix.md` §3,
   `01-DomainModel.md` §2 and §9.2).
2. **There is no `OfficeId` anywhere.** §0.3.
3. **Create and suspend/reactivate are `AccountantAdmin` only** — two of exactly four AA-only
   powers. Do not widen them, and do not add a fifth power to this slice.
4. **Editing legal name and tax number is not AA-only.** An `AccountantUser` may do it. This
   asymmetry is intentional and is in the matrix.
5. **An `Employee` sees a narrowed projection**, enforced by a distinct DTO type rather than a
   nulled field (§3.1).
6. **There is no global query filter on this context** (§2.3). Scope is explicit and per-handler.
7. **Suspension writes one row.** It does not cascade to UserAccounts, Employees, or Tickets. The
   login block is a check `Identity` performs at login time via `ICustomerApi.IsActiveAsync`
   (§4.6 rule 3).
8. **This slice creates only the Customer**, not its first Customer Admin. The composite operation
   is locked to `Employees` (§4.1 rule 5).
9. **`ICustomerApi` carries no tax number and no address**, applies no scope filter, and caches
   nothing (§5).
10. **No notifications are sent** for any Customer lifecycle event in v1 (§6 rule 3).

---

## 13. Questions to flag rather than answer

Stop and raise these. Do not invent a behaviour — [README.md](../../README.md) is explicit that a
gap should be flagged, not filled.

> Two questions that were open when this plan was first drafted are now **resolved and locked in
> the numbered documents**. They are recorded here only so a reader who remembers them does not
> re-open them:
>
> - **The composite Customer-onboarding operation lives in `Employees`**, not here. This slice
>   builds a Customer-only create as a building block. See
>   [03-SliceInventory.md](../../03-SliceInventory.md) §1 and §4.1 rule 5 below.
> - **`Identity → Customers` is now a permitted edge**, used for login-time Customer status only
>   via `ICustomerApi.IsActiveAsync`. See
>   [03-SliceInventory.md](../../03-SliceInventory.md) §2. Do not denormalise status onto
>   `user_accounts`.

### 1. Should a suspended Customer's people be told?

§4.6 rule 3 and §6 rule 3 say nothing is notified. That is the conservative reading, but a
Customer Admin who suddenly cannot log in and receives no explanation will call the Office. A
"your access has been suspended" email is arguably useful and arguably worse. Not decided; raise
it.

### 4. `CREATE EXTENSION pg_trgm` may not be permitted

§1 needs it for mid-string name search. If the deployment's database user lacks the privilege, the
fallback changes search *behaviour* from "contains" to "starts with", which is a product decision.
Raise it rather than silently degrading the index.

### 5. Is a Customer's contact email ever used to send mail?

`contact_email` is deliberately not unique and not a login identifier. Nothing in v1 sends mail to
it — `Notifications` mails a *person's* address via `IRecipientDirectory`. If the Office expects
correspondence to reach the business address, that is a new requirement and it interacts with the
undecided email transport (`04-Infrastructure.md` §5a).

---

## Files checklist

| File | Action |
|---|---|
| `Slices/Customers/Core/Customer.cs` | New (incl. `CustomerStatus`) |
| `Slices/Customers/Infrastructure/CustomersDbContext.cs` | New |
| `Slices/Customers/Infrastructure/Configurations/CustomerConfiguration.cs` | New |
| `Slices/Customers/Infrastructure/Migrations/20260830_001_CreateCustomersSchema.sql` | New |
| `Slices/Customers/ExternalInterfaces/ICustomerApi.cs` | New (incl. `CustomerSummary`) |
| `Slices/Customers/ExternalInterfaces/CustomerApi.cs` | New |
| `Slices/Customers/Application/CustomerMapper.cs` | New |
| `Slices/Customers/Application/Dtos/*.cs` | New — 9 DTOs |
| `Slices/Customers/Application/Handlers/CreateCustomerHandler.cs` | New |
| `Slices/Customers/Application/Handlers/ListCustomersHandler.cs` | New |
| `Slices/Customers/Application/Handlers/GetCustomerHandler.cs` | New |
| `Slices/Customers/Application/Handlers/GetOwnCustomerHandler.cs` | New |
| `Slices/Customers/Application/Handlers/UpdateCustomerContactHandler.cs` | New |
| `Slices/Customers/Application/Handlers/UpdateCustomerLegalHandler.cs` | New |
| `Slices/Customers/Application/Handlers/SuspendCustomerHandler.cs` | New |
| `Slices/Customers/Application/Handlers/ReactivateCustomerHandler.cs` | New |
| `Slices/Customers/CustomersActionCatalogue.cs` | New |
| `Slices/Customers/CustomersRegistration.cs` | New |
| `Slices/Customers/CustomersEndpoints.cs` | New |
| `Slices/Audit/ExternalInterfaces/AuditActions.cs` | Modify — add the four codes in §9 |
| `Program.cs` | Modify — two lines |
| `AccountantApp.Tests/Customers/CustomersSchemaTests.cs` | New — PostgreSQL test |
| `AccountantApp.Tests/Customers/CustomersAuthorizationTests.cs` | New — role and scope cases |
| `AccountantApp.Tests/Customers/CustomerApiTests.cs` | New — `ICustomerApi` cases |

## Success criteria

1. `dotnet build` produces **0 errors and 0 warnings**.
2. `docker compose up -d db` then `dotnet run` starts, applies the migration, and logs the
   `DevAuth` warning.
3. `schema_versions` holds the slice-relative path key, not the bare filename.
4. The `customers` table has the columns in §1, `onboarded_on` is `DATE`, every other date column
   is `TIMESTAMPTZ`, and there is **no `office_id`** and **no `customer_id`**.
5. `uq_customers_tax_number` exists; both indexes in §1 exist.
6. There is no `deleted_at` column, no delete endpoint, and no `DELETE` or `Remove()` in the slice.
7. `AccountantAdmin` can create, suspend, and reactivate a Customer; **`AccountantUser` gets `403`
   on all three**.
8. `AccountantUser` **can** edit a legal name and a tax number.
9. `CustomerAdmin` can edit their own Customer's contact details and gets **`404`** for another
   Customer's.
10. `Employee` receives `CustomerSelfDto` from `/own`, and that type **has no `TaxNumber`
    property** — not a null one.
11. `/api/customers/list` returns **`403`** for `CustomerAdmin` and `Employee`, not an empty page.
12. `/api/customers/own` takes no parameters and returns `401` for an Accountant.
13. A duplicate tax number returns **`409`**, both via the pre-check and via the raw constraint.
14. **A failing audit write rolls back the Customer create** — demonstrated by a test.
15. An update's audit entry has `Before` holding the pre-change values.
16. Suspending an already-suspended Customer returns `422`.
17. Suspending a Customer changes exactly one row and touches no UserAccount, Employee, or Ticket.
18. `ICustomerApi` exposes no tax number and no address, caps `FindManyAsync` at 500 ids, writes no
    audit entries, and **caches nothing** — a suspension is visible to `IsActiveAsync` on the very
    next call.
19. Startup fails if this slice declares an action name another slice already declared.
20. Startup fails if `CustomerId` is not ignored in the EF configuration.
21. `dotnet test` passes, with the PostgreSQL test **executed, not skipped**.

---

# Correction Notes — review of 2026-09-01

Written after validating the working-tree implementation against this plan and documents 0–5.
**These are corrections to this plan and to the numbered documents, recorded so the next build
cycle does not repeat the same guesses.** Each finding says whether the fault is in the
IMPLEMENTATION, the SPEC, or both.

State at review: `dotnet build` = 0 errors, 0 warnings. `dotnet test` = 27 passed, 0 failed,
**2 skipped** — and both skips are the real-PostgreSQL schema tests.

A large amount of this slice is correct and was checked rather than assumed: all eight routes are
`/api/customers/{action}` in lowercase kebab-case; the catalogue's role lists match
02-AuthorizationMatrix §3 cell for cell; `/api/customers/create` writes only the `customers` row;
the SQL migration and `CustomerConfiguration` agree column-for-column including
`uq_customers_tax_number`; there is no soft-delete flag and no hard-delete path; `ICustomerApi`
exposes only its own `CustomerSummary` and `IsActiveAsync` is a live uncached `AnyAsync`; the slice
references only `Shared` and `Slices.Audit.ExternalInterfaces`. The findings below are the
exceptions.

## C-1 (BLOCKER, implementation) — the LOCKED shared scope filter is silently shadowed by a slice-local copy

App/GeneralAppArchitecture §4, LOCKED: *"03-SliceInventory §4 names a per-slice reimplementation
of scope filtering as the most likely way this application leaks data between Customers. **So it is
written once, in `Shared/Authorization/CustomerScope.cs`.**"*

`Core/Customer.cs:33-41` declares `public static class CustomerRootScope` with
`WhereInCustomerScope(this IQueryable<Customer>, CurrentUser)` — a verbatim reimplementation of the
shared filter. Because it is **non-generic** while the shared one is
`WhereInCustomerScope<T>(this IQueryable<T>, CurrentUser) where T : ICustomerScoped`, C#
better-function-member resolution prefers the non-generic overload, so **all four** call sites
(`GetCustomerHandler.cs:28`, `GetOwnCustomerHandler.cs:29`, `UpdateCustomerContactHandler.cs:40`,
`UpdateCustomerLegalHandler.cs:42`) bind to the slice-local copy. The build is clean, there is no
ambiguity warning, and the call sites are textually identical either way — the binding is
invisible at the point of use.

The two implementations are behaviourally identical today, so nothing leaks *now*. That is what
makes it a blocker rather than a nit: the locked invariant — fix the filter once and every slice is
fixed — is already broken in the first slice that owns a Customer-scoped entity, and no test can
detect it.

**This is also a spec fault, and the more important half.** The shared filter is *structurally
unusable* for this entity. `Customer` satisfies `ICustomerScoped` with the computed
`public Guid CustomerId => Id;` (`Customer.cs:24`), and `CustomerConfiguration.cs:29` does
`builder.Ignore(customer => customer.CustomerId)`. An EF-ignored computed property with no backing
column **cannot be translated**, so `_db.Customers.WhereInCustomerScope(user)` through the shared
generic would throw *"The LINQ expression could not be translated"* against PostgreSQL. The builder
did not reimplement out of carelessness; the spec left no other option. App §4 defines
`ICustomerScoped` as `Guid CustomerId { get; }` and never says how the aggregate root — whose
primary key *is* the Customer id — satisfies it.

Correction, in the spec: add to App §4 a sanctioned root-entity overload living in
`Shared/Authorization/CustomerScope.cs` alongside the generic one, e.g.
`WhereIsCustomer(this IQueryable<Customer>, CurrentUser)`, or require an EF-mapped shadow column.
Correction, in the code: delete `CustomerRootScope` (lines 33-41) and move that overload into
`Shared/Authorization/CustomerScope.cs`. Consider a test that fails if any type outside
`Shared/Authorization` declares a member named `WhereInCustomerScope`.

## C-2 (MAJOR, both) — a role denial on `/api/customers/detail` is returned as 404, and is not audited

02-AuthorizationMatrix §1: *"when the caller lacks the role, respond `403`. When the record is
outside the caller's scope, respond **`404`, not `403`**."* §12 rule 3 restates it. §3 grants
Employee only *"View own Customer's details … Yes, read-only, limited fields"* — an Employee is not
permitted on the full-detail endpoint at all.

`GetCustomerHandler.cs:24` calls `RequireAsync(user, "ViewCustomer")`, and the catalogue maps
`ViewCustomer` to all four roles, so an Employee passes the role check. The Employee is then
excluded by a **query predicate** — `.Where(_ => user.Role != UserRole.Employee)` at line 27 —
which yields `null` and therefore `AppException(..., 404)`. So a role failure is rendered as a
scope failure, and because `RequireAsync` never denied, **no `PermissionDenied` audit row is
written**, violating §1's "Every denial writes an Audit Entry". `CustomersFlowTests.cs` asserts the
404, so the wrong behaviour is locked in by a passing test.

**Spec fault, and it is the cause:** §4.3 rule 2 gives `/detail` the role list `[AA, AU, CA]` and
`/own` the list `[CA, Employee]`, while §10.2 defines only **one** action name, `ViewCustomer`,
with all four roles. One catalogue entry cannot express two different lists, and the
`.Where(_ => …)` hack is the direct consequence.

Correction: split into `ViewCustomer` = `[AA, AU, CA]` for `/detail` and `ViewOwnCustomer` =
`[CA, Employee]` for `/own`. Delete `GetCustomerHandler.cs:27` and let `RequireAsync` produce the
audited 403. Update §10.2 to list both action names.

## C-3 (MAJOR, implementation) — pagination uses 25/200 instead of the system-wide 15/50

App/GeneralAppArchitecture §8: *"**Default `pageSize`: 15. Maximum: 50.** These two numbers are
system-wide. Every paginated endpoint in every slice uses them; **a slice does not pick its own.**"*
The same section explains why: *"The maximum is 50 rather than a large number on purpose. It is the
ceiling on how much data one request can extract, so it is a **security control** as much as a
performance one."* §0.5 of this plan repeats 15/50.

`ListCustomersHandler.cs:31` is `Math.Clamp(request.PageSize <= 0 ? 25 : request.PageSize, 1, 200)`
and `ListCustomersRequestDto.cs:8` defaults to 25. A caller can extract 200 rows per request — 4×
the sanctioned ceiling on an internet-facing app. The clamp-rather-than-reject behaviour is
correct; only the numbers are wrong.

The identical wrong pair appears in `ListTicketTypesHandler.cs:26`, `ListTicketTypesRequestDto.cs:6`
and `TicketTypesEndpoints.cs:49`, so this is copied repo-wide drift, not a one-off. Root cause is
C-8.

Correction: 15 default, clamp `(1, 50)`, DTO default 15 — but preferably via the shared helper in
C-8, so the numbers exist in one place.

## C-4 (MAJOR, implementation) — the tests never exercise the shipped action catalogue

Both test files declare their own `private sealed class CustomersCatalogue : IActionCatalogue`
(`CustomersFlowTests.cs:225`) and a third copy named `Catalogue` (`CustomersSchemaTests.cs:177`)
restating the role lists, and hand *those* to `PermissionChecker`. `CustomersActionCatalogue` —
the class actually registered in `CustomersRegistration.cs` and actually used in production — is
never instantiated by any test.

Every "403" assertion therefore proves only that the test's own private table is self-consistent.
Adding `CustomerAdmin` to `CreateCustomer` in the production catalogue leaves the suite green.
The same pattern affects TicketTypes, where all ten `PermissionChecker` constructions use the
legacy overload.

Correction: delete the three private catalogues and inject `new CustomersActionCatalogue()`. Add
one test asserting the production fragment's action→roles map equals the 02-AuthorizationMatrix §3
table literally.

## C-5 (MAJOR, implementation) — mandatory acceptance rows from §11.2 are absent

§11.2 lists roughly forty behavioural rows, four of them **bold mandatory tenant-boundary tests**;
App §4 rule 5 requires that *"every slice owning a Customer-scoped entity has a
cross-Customer-404 test."* `CustomersFlowTests.cs` has eight tests.

Present and genuinely asserted: AU/CA/EMP create → 403 with a `PermissionDenied` row; AA create
trims and forces `Active`; blank legal name → 422; CA reading another Customer via `/detail` → 404;
Employee `/own` → 200 and `/detail` → 404; contact update produces distinct Before/After; suspend →
re-suspend 422 → reactivate; `ICustomerApi` fail-closed on unknown id plus the 500-id cap.

Missing, each an explicit plan row: **CA `update-contact` against another Customer's id → 404**
(the bold mandatory *write*-path boundary test — only the read path is covered); CA `list` → 403;
AU `update-legal` → 200; CA `update-legal` → 403; AA calling `/own` → 401; 301-character legal name
→ 422; `OnboardedOn` two days in the future → 422; a search term containing a literal `%` treated
as a literal (the `ILike` escaping is untested); `pageSize` clamping; and `IsActiveAsync` returning
`false` on the call immediately after a suspension — the "do not cache" guarantee of
02-AuthorizationMatrix §11.

Correction: add the ten cases. The write-path cross-Customer 404 and the
`IsActiveAsync`-after-suspension case are the two that protect real invariants.

## C-6 (MAJOR, implementation) — the duplicate-tax-number → 409 path is never exercised through the handler

§11.1 case 7 and success criterion 13 require a duplicate tax number to surface as **409**, not
500 — i.e. that the `catch (DbUpdateException … PostgresException { SqlState: "23505" })` in
`CreateCustomerHandler` and `UpdateCustomerLegalHandler` actually fires.

`CustomersSchemaTests.cs:82` issues a raw `INSERT` on a second connection and asserts a bare
`PostgresException` with `SqlState == "23505"`. That proves PostgreSQL enforces the constraint. It
proves nothing about the application. Neither handler's 409 translation is covered anywhere — and
the in-memory suite cannot cover it, because the InMemory provider has no unique constraints, so
this `[SkippableFact]` is the only place it *could* be covered. Note that it is currently skipped
in any case (see C-9).

Correction: in the Postgres test, call `CreateCustomerHandler.Handle` twice with the same tax
number and assert `AppException.StatusCode == 409`; repeat for `UpdateCustomerLegalHandler`.

## C-7 (MINOR, implementation) — the rollback test bypasses `RequestConnection`, so it cannot catch the trap it exists for

§10.4.2 names the trap: registering `CustomersDbContext` with a bare connection string instead of
the per-request `RequestConnection` puts the audit write on a different connection and the "audit
fails ⇒ the customer row is not created" guarantee evaporates. App §6 requires the `(sp, o)`
`AddDbContext` overload.

`CustomersSchemaTests.cs:90` constructs the context with `UseNpgsql(connectionString)` directly
rather than resolving `RequestConnection`. It does show that `IRequestTransaction` rolls back when
the audit throws, but it would keep passing even if `CustomersRegistration` regressed to a bare
connection string — the actual failure mode. The registration itself is correct today.

Correction: build the context through a `ServiceProvider` that has `RequestConnection` registered,
exactly as `Program.cs` does, so the assertion is sensitive to the registration.

## C-8 (MINOR, implementation) — `Shared/Pagination` is missing the `PaginatedQuery` half of its contract

App §4 lists the shared pagination contract as a `PaginatedQuery` / `PaginatedResponse<T>` pair.
Only `PaginatedResponse<T>` exists. There is no shared request record and no shared clamp helper,
so every slice re-derives the default and maximum by hand — and both slices that have done so got
the numbers wrong. This is the root cause of C-3, not a style nit.

Correction: add `PaginatedQuery` with `Page`/`PageSize`, `const int DefaultPageSize = 15`,
`const int MaxPageSize = 50`, and a `Normalise()` that clamps; have `ListCustomersHandler` and
`ListTicketTypesHandler` call it. App §4 should show the type in code, the way it shows
`CustomerScope` and `RequestConnection` — stating the numbers only in prose is what allowed the
drift.

## C-9 (MINOR, implementation) — the slice's only real-database coverage is skipped

`CustomersSchemaTests.Migration_mapping_search_and_transaction_work_against_real_postgres` is a
`[SkippableFact]` and **skips** in the current run. Everything that only a real database can prove
— the migration applying, column mapping, `ILike` search, the unique constraint, the transaction
rollback — is therefore unverified while `dotnet test` reports green. Correction: make it fail
rather than skip when no database is reachable in CI, and say so in §11.1.

## C-10 (MINOR, both) — a malformed request body becomes a 500 in Development

App §8 / 02 §12: if a client can trigger it by sending a value, it is a `4xx`, never a `500`.

`AppExceptionMiddleware.cs:31` catches `AppException`, aborted `OperationCanceledException`, and
then `Exception` → 500. Minimal APIs throw `BadHttpRequestException` (which carries
`StatusCode = 400`) for unparseable bodies and missing required query parameters whenever
`RouteHandlerOptions.ThrowOnBadRequest` is true — and that defaults to **true in Development**. So
`POST /api/customers/create` with `{"onboardedOn":"2026-13-45"}`, or `GET /api/customers/detail`
with no `customerId`, returns 500 locally and 400 in Production. Development is exactly where the
§10.5 smoke checks run, so the wrong code is the one a builder sees.

Correction: add `catch (BadHttpRequestException exception)` before the catch-all, writing
`exception.StatusCode`. Add one test posting malformed JSON. The spec gap behind it: no document
says which component owns model-binding failures.

## C-11 (MINOR, spec) — `/api/customers/own` contradicts the plan's own acceptance table

§4.3 rule 2 says `GetOwnCustomerHandler` returns `CustomerSelfDto`, permitted to `CA` and
`Employee`. §3.1 and the §11.2 acceptance row say *"`CA` gets `/own` | `200`, full `CustomerDto`"*.

`GetOwnCustomerHandler.cs:32` follows §4.3: both `CustomerAdmin` and `Employee` get the reduced
`CustomerSelfDto`. No rule in doc 2 is violated — a CA is entitled to its own tax number and can
still get it from `/detail` — so nothing is over-exposed; the acceptance table is simply
unsatisfiable as written. The implementation picked the internally consistent branch.

Correction: decide in the plan. Simplest is `/own` always returns `CustomerSelfDto` and CAs use
`/detail` for the full record; then fix §3.1 and the §11.2 row. The alternative makes `/own` branch
on role, which is worse.

## C-12 (MINOR, implementation) — the entity is read outside the request transaction in both update handlers

App §6: the mutating slice opens the transaction and the audit write enlists in it, so "the row and
its audit entry commit or roll back together."

In `UpdateCustomerContactHandler.cs:38` and `UpdateCustomerLegalHandler.cs:40` the tracked read —
and, in the legal handler, the `AnyAsync` uniqueness pre-check at line 47 — happens **before**
`BeginAsync` at line 45/51. The write and the audit are correctly inside one transaction, so the
stated guarantee holds; but the read-modify-write is not atomic, so two concurrent `update-legal`
calls can both pass the pre-check. The unique constraint plus the 23505 → 409 catch prevents
corruption, so the impact is a lost update on non-unique columns rather than an integrity break.

Correction: move `BeginAsync` above the read in both handlers. The plan never states where
`BeginAsync` goes relative to the read — see gap 8.

---

## Spec gaps — what a builder had to guess

1. **One action name, two endpoints, two role lists.** §10.2 defines a single `ViewCustomer`, but
   §4.3 gives `/detail` `[AA,AU,CA]` and `/own` `[CA,EMP]`. The catalogue model cannot express
   this and the plan never says to split the action. Direct cause of C-2 and the most consequential
   gap in this document.
2. **`ICustomerScoped` cannot be satisfied by a Customer-scoped aggregate root** (C-1). App §4
   defines the marker as `Guid CustomerId { get; }`, but for `Customer` the Customer id *is* the
   primary key, so the property is computed, EF-ignored, and untranslatable. The spec gives no
   sanctioned way to scope the `Customer` entity itself, and thereby forces the per-slice
   reimplementation it names as the top leak vector.
3. **Action count contradiction.** §0.4 says the slice's catalogue has "six" actions; §10.2 lists
   seven. The count cannot be used as a checksum.
4. **`/own` DTO for a `CustomerAdmin`** (C-11). §4.3 rule 2 and §3.1 / §11.2 are unresolvable as
   written.
5. **401 vs 403 for a scope-less caller on `/own`.** §4.3 rule 3 prescribes 401 when
   `user.CustomerId` is null, but an AA/AU hitting `/own` *is* authenticated — 403 or 422 would be
   conventional. The reason for 401 is never given and 02 §1's status-code table does not cover
   "authenticated but has no Customer scope".
6. **Whether `UpdatedAt` belongs in the audit snapshot.** `CustomerMapper.ToAuditSnapshot` excludes
   `CreatedAt` but includes `UpdatedAt`, so Before/After differ on every update even when no
   business field changed — making the §11.2 "Before and After are distinct" row trivially
   satisfiable. No document says what a snapshot should contain.
7. **Model-binding failures are unowned** (C-10). Nothing says which component turns a malformed
   body or a missing required query parameter into a `4xx`, and no acceptance row tests it.
8. **Transaction boundary relative to the read** (C-12). App §6 says the audit write must be inside
   the mutating slice's transaction but never says whether the read-for-update belongs inside it,
   so read-modify-write atomicity was left to chance.
9. **`Reason` length.** `SetCustomerStatusRequestDto.Reason`'s 500-character limit appears only in
   the DTO table, not in §5's validation rules, and the plan never says whether an over-long reason
   is 422 or silently truncated. The implementation validates at 500 — a reasonable guess, not a
   specified one.
10. **Which endpoint a `CustomerAdmin` should call.** With both `/detail` and `/own` open to a CA
    and returning different shapes, no document says which is canonical.
11. **`CREATE EXTENSION pg_trgm` privileges.** §1 mandates the trigram index inside a slice
    migration, but nothing addresses that `CREATE EXTENSION` needs elevated rights, so the first
    deployment against a least-privilege database role fails at startup with no guidance.

---

# Correction Notes — second review, 2026-09-01

Findings from a review of the tree after C-1…C-12 were applied. All are fixed in code.

## C-13 (MAJOR, both) — `customers.status` has no `CHECK`, on a column with exactly two legal values

`20260830_001_CreateCustomersSchema.sql` declares `status VARCHAR(20) NOT NULL DEFAULT 'Active'`.
`CustomerStatus` defines exactly `Active` and `Suspended`, `ListCustomersHandler` rejects anything
else with a `422`, and the suspend/reactivate handlers are the only writers — so the constraint is
not the primary defence. It is what makes a future handler's bug, or a hand-run `UPDATE` during
support, fail loudly instead of leaving a row that no status filter matches and no reader can
explain.

Correction, applied as **`20260901_002_AddCustomerStatusCheck.sql`**, a new script:

```sql
ALTER TABLE customers
    ADD CONSTRAINT ck_customers_status CHECK (status IN ('Active', 'Suspended'));
```

**Not an edit to `001`.** A-2 in the `Audit` plan is a whole finding about exactly that mistake:
`001` is recorded in `schema_versions` on every environment where it has already run, so editing it
changes nothing there and silently produces two databases with different schemas. Migrations are
append-only, and the narrow exception the `Audit` plan §0.0 granted itself required *proving* the
script had never been applied anywhere.

Every enumerated string column in this system should get the same treatment. `audit_entries.outcome`
was the other one; see A-12.

## C-14 (MAJOR, implementation) — a dead copy of the action catalogue survived the C-4 fix, already divergent from the real one

C-4 was fixed correctly: `CustomersFlowTests.Permissions()` now builds a `PermissionChecker` from
`new CustomersActionCatalogue()`, the shipped fragment. But the private `CustomersCatalogue` class
the tests used to use was left in the file, and it had **already drifted** — its `ViewCustomer`
granted `Employee`, which the shipped catalogue does not, and it was missing `ViewOwnCustomer`
entirely.

Unreachable code is not harmless when it is a *plausible-looking copy of an authorization table*.
The next reader comparing the two has no way to know which is authoritative, and the obvious repair
— "the test disagrees with the code, make the code match the test" — grants `Employee` read access
to every Customer in the office.

Correction, applied: deleted. **A test's fix is not complete until the thing it replaced is gone.**

## C-15 (MINOR, implementation) — `ListCustomersHandler` is the one read path with no scope filter

`GetCustomerHandler` and `GetOwnCustomerHandler` call `WhereMatchesCustomerScope(user)`;
`ListCustomersHandler` queries `_db.Customers.AsNoTracking()` unfiltered. It is not a live
vulnerability — the catalogue restricts `ListCustomers` to the two Accountant roles — but it means
the permission check is the *only* thing standing between a Customer-side caller and a list of every
Customer in the office, and the LOCKED rule in `App/GeneralAppArchitecture.md` §4 is that the scope
filter is applied on every Customer-scoped read, not on the reads that currently need it.

The filter is a no-op for Accountant roles by construction, so applying it costs nothing and means
one catalogue edit cannot turn into a full customer-list disclosure.

Correction, applied: `_db.Customers.AsNoTracking().WhereMatchesCustomerScope(user)`.
