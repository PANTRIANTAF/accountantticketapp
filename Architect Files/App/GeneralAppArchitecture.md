# Backend Architecture

## 1. Technology stack

- **Runtime:** .NET 10
- **Web Framework:** ASP.NET Core Minimal APIs
- **Database:** PostgreSQL 16, via the `Npgsql.EntityFrameworkCore.PostgreSQL` provider
- **ORM:** Entity Framework Core — for querying and mapping only. **EF Core migrations are
  not used**; see section 6.
- **Language:** C#, `<Nullable>enable</Nullable>`

`Microsoft.EntityFrameworkCore.InMemory` must not be referenced by `AccountantApp.Api`. It
belongs to the test project alone. The in-memory provider ignores column names, unique
constraints, and `NOT NULL`, so it will report success on a schema that cannot work against
PostgreSQL.

## 2. Project structure

One `AccountantApp.Api` project, organized into vertical slices. Do not create separate
project-per-slice; slices are directories, not projects.

```
AccountantApp.Api/
├── Shared/
│   ├── Auth/
│   ├── Authorization/
│   ├── Errors/
│   ├── Migrations/
│   ├── Pagination/
│   ├── ValueObjects/
│   └── ... (see section 4)
│
├── Slices/
│   ├── Identity/
│   │   ├── Core/                     (EF entities)
│   │   ├── Application/
│   │   │   ├── Handlers/
│   │   │   └── Dtos/
│   │   ├── Infrastructure/
│   │   │   ├── Migrations/           (SQL scripts)
│   │   │   └── IdentityDbContext.cs   (EF context for this slice)
│   │   ├── ExternalInterfaces/
│   │   │   ├── IIdentityApi.cs       (public contract)
│   │   │   └── IdentityApi.cs        (implementation)
│   │   ├── IdentityEndpoints.cs      (routes)
│   │   └── IdentityRegistration.cs   (DI — see section 7)
│   │
│   ├── Customers/
│   │   ├── Core/
│   │   ├── Application/
│   │   ├── Infrastructure/
│   │   │   ├── Migrations/
│   │   │   └── CustomersDbContext.cs
│   │   ├── ExternalInterfaces/
│   │   │   ├── ICustomerApi.cs
│   │   │   └── CustomerApi.cs
│   │   ├── CustomerEndpoints.cs
│   │   └── CustomersRegistration.cs
│   │
│   └── ... (Employees, TicketTypes, Tickets, Documents, Notifications, Audit)
│
├── Program.cs                        (Shared services, middleware, one Add*Slice + one Map* per slice)
└── appsettings.json

```

## 3. Directory conventions per slice

Every slice contains these if applicable to its domain; do not add directories that are
unused.

| Directory | Purpose |
|---|---|
| `Core/` | EF entity classes. One file per entity. Strongly typed. No Dtos here. |
| `Application/Handlers/` | Handler classes, one per handler. Name: `<Operation>Handler.cs`. |
| `Application/Dtos/` | Request and response DTOs for this slice's **own** HTTP surface. Separate files for Request/Response/List/Detail shapes. Name: `<Entity><Shape>Dto.cs`, e.g. `CustomerCreateRequestDto.cs`. **A response type another slice receives through a contract does not live here** — see the `ExternalInterfaces/` row and the note below the table. |
| `Application/Validators/` | FluentValidation validators for Dtos. |
| `Infrastructure/Migrations/` | SQL scripts only, one per schema change. Named `YYYYMMDD_###_Description.sql`. No EF migrations. |
| `Infrastructure/` | DbContext for this slice (if not in a shared context). Repositories, external service clients. |
| `ExternalInterfaces/` | Public contract interfaces (`I*Api.cs`), their implementations (`*Api.cs`), **and the types those contracts return** (`*Summary`, and response DTOs a caller slice receives). Used by other slices. |
| (no directory) | `{Slice}Endpoints.cs` at slice root: minimal API route registration. |
| (no directory) | `{Slice}Registration.cs` at slice root: **dependency-injection registration for everything the slice owns. Mandatory — every slice has exactly one.** See section 7. |

> **Where a contract's response type lives, and why the two rows above had to be sharpened.**
> Dependency rule 2 (section 5) forbids a slice referencing another slice's `Application`. A contract
> method returning a type from `Application/Dtos/` therefore forces every caller into a violation just
> by using it — the interface is public, but its return type is not reachable legally. That is not a
> hypothetical: `ITicketTypesApi` shipped that way and was corrected on 2026-09-02, which is what
> prompted this note.
>
> The rule, stated once:
>
> - **A type a contract returns lives in `ExternalInterfaces/`.** `TicketTypeDetailDto`,
>   `EmployeeSummary`, `DocumentSummary`, `AccountSummary`, `CustomerSummary`.
> - **A type only this slice's own HTTP surface uses lives in `Application/Dtos/`.** Every
>   `*RequestDto` belongs here; request DTOs are HTTP input and no other slice ever sees one.
> - **One type may legitimately serve both** an endpoint response and a contract return. When it does,
>   it lives in `ExternalInterfaces/` and the endpoint returns it from there. **Do not duplicate it
>   into two parallel shapes** to satisfy the folder layout — two shapes that must agree and are
>   checked by nothing is a worse outcome than a response type in a slightly surprising folder.
> - A request DTO may reference a contract type (e.g. reusing a shared option shape). That direction
>   is safe. The reverse — a contract type reaching into `Application/` — is the violation.

## 4. Shared code: what belongs in `Shared/`

Shared code is code that multiple slices need and no slice owns. It lives in `Shared/` at
the API project root. A builder must not put slice-specific code here.

### Required in Shared

- **Auth/** — resolving the current caller from the **session cookie** into role + scope, the
  `CurrentUser` record, and claim extraction. There are no bearer tokens; see section 9.
- **Errors/** — `AppException` base class carrying an HTTP status code, error codes, and the
  `ProblemDetails` formatting used by the exception-handling middleware.
- **Authorization/** — the permission checker that evaluates
  [02-AuthorizationMatrix.md](../02-AuthorizationMatrix.md). Exactly one interface:

  ```csharp
  public interface IPermissionChecker
  {
      // Throws AppException(403) if denied. Audits every denial before throwing.
      Task RequireAsync(CurrentUser user, string action, object? scope = null,
                        CancellationToken ct = default);
  }
  ```

  Three rules the implementation must obey:

  1. **An unknown action name denies.** Never write `default => allow-everyone`. A typo in an
     action string must lock everyone out rather than let everyone in.
  2. **Every denial is audited** before the exception is thrown
     ([02-AuthorizationMatrix.md](../02-AuthorizationMatrix.md)). This is why the checker is an
     injected service and not a method on `CurrentUser` — a plain record cannot reach `IAuditApi`.
  3. **The method is `async`, and callers `await` it.** It is not `void Require(...)`. Auditing
     a denial is a database write, and a synchronous signature forces
     `LogAsync(...).GetAwaiter().GetResult()` on a request thread. That blocks a thread-pool
     thread for a round-trip under load, and — worse — if the audit write throws, the
     `NpgsqlException` replaces the `AppException(403)`, so during an audit outage a denied
     caller receives `500` and **the denial is never recorded**. Auditing failures are the one
     thing that must not vanish ([04-Infrastructure.md](../04-Infrastructure.md) section 6).

  If the audit write fails, log it and still throw the `403`. Never let an audit failure turn a
  denial into a success or into a `500`.
- **Migrations/** — the SQL script runner described in section 6.
- **Authorization/** also holds the **action catalogue composition** and the **Customer scope
  filter**, both specified in the two locked subsections below.
- **ValueObjects/** — things two slices both need. Examples: `CustomerId`, `EmployeeId`,
  `TicketId` (if used as values rather than ints). Do **not** duplicate these per-slice.
- **Pagination/** — request/response shapes: `PaginatedQuery`, `PaginatedResponse<T>`.

### Optional in Shared, if used

- **Middleware/** — correlation IDs, request logging, exception handling
- **DateTime/** — timezone utilities, `SystemClock` abstraction
- **Localization/** — if the app supports multiple languages
- **Validation/** — base validators, custom FluentValidation rules used across slices

### Never in Shared

- Business logic of any slice
- Handlers or handlers' dependencies
- EF entities or DbContexts
- Slice-specific Dtos
- Infrastructure of any kind

### The action catalogue is contributed per slice and composed at startup — LOCKED

`IPermissionChecker` answers "may this role perform this action". The mapping from action name
to permitted roles is **not** a hard-coded set inside `PermissionChecker`. Each slice
contributes its own fragment, and the checker composes them.

Why not one central table: a single file listing every action in the system would be edited by
all eight slices, and a slice's permissions would live somewhere other than the slice. The
fragment approach keeps a slice self-describing, consistent with the locked rule that a slice
registers everything it owns and nothing it does not.

```csharp
namespace AccountantApp.Api.Shared.Authorization;

/// <summary>
/// One slice's contribution to the action catalogue. Registered by {Slice}Registration.cs.
/// </summary>
public interface IActionCatalogue
{
    /// <summary>Slice name, for error messages when two slices claim one action.</summary>
    string SliceName { get; }

    /// <summary>Action name to the roles permitted to perform it. Never empty-valued.</summary>
    IReadOnlyDictionary<string, UserRole[]> Actions { get; }
}
```

Rules the composition must obey:

1. **Registered as `IEnumerable<IActionCatalogue>`.** Each slice calls
   `services.AddSingleton<IActionCatalogue, {Slice}ActionCatalogue>()` in its
   `{Slice}Registration.cs`. `PermissionChecker` injects the enumerable. No assembly scanning.
2. **A duplicate action name is a startup failure, not a merge.** If two slices declare the same
   action, throw `InvalidOperationException` naming both slices and the action. Silently letting
   one win means the effective permission depends on registration order in `Program.cs`.
   Validate this **eagerly at startup**, not on first request — a permission bug must not wait
   for traffic to appear.
3. **An action absent from every catalogue denies.** This is rule 1 of `IPermissionChecker`
   restated: composition must not introduce an "unknown means allow" path.
4. **An empty role array is a startup failure.** `["SomeAction"] = []` denies everyone, which is
   almost always a typo rather than an intent. If an action really is permitted to nobody, it
   should have no handler at all — see the many "**Nobody.**" rows in
   [02-AuthorizationMatrix.md](../02-AuthorizationMatrix.md).
5. **Action names are unique across the whole system, not per slice.** Prefix with the entity
   where it helps: `CreateTicketType`, not `Create`.
6. The catalogue answers the **role** check only. The **scope** check is separate and is never
   expressible as a role list — see the next subsection.

### The Customer scope filter is one shared mechanism — LOCKED

[02-AuthorizationMatrix.md](../02-AuthorizationMatrix.md) §1: every decision is the conjunction
of a role check **and** a scope check, and passing the role check alone is never sufficient.
[03-SliceInventory.md](../03-SliceInventory.md) §4 names a per-slice reimplementation of scope
filtering as *the most likely way this application leaks data between Customers*. So it is
written once, in `Shared/Authorization/CustomerScope.cs`.

**`CurrentUser` carries the scope.** It is not `(Id, Role)`:

```csharp
public record CurrentUser(string Id, UserRole Role, Guid? CustomerId);
```

| Role | `CustomerId` |
|---|---|
| `AccountantAdmin`, `AccountantUser` | `null` — they are not scoped to a Customer. |
| `CustomerAdmin`, `Employee` | **Required.** A Customer-scoped role with a null `CustomerId` is not a caller with wide access; it is a broken principal. `CurrentUserFactory` must throw `AppException(401)` rather than build one. |

`CurrentUserFactory` reads the `customer_id` claim. `DevAuthHandler` already emits it and
already fails a Customer-scoped role that omits `X-Dev-Customer-Id`; the factory must stop
discarding it.

The filter itself is an explicit, called extension over a marker interface:

```csharp
public interface ICustomerScoped
{
    Guid CustomerId { get; }
}

public static class CustomerScope
{
    /// <summary>
    /// Restricts a query to what this caller may see at Customer granularity.
    /// Accountants see everything; Customer-scoped roles see their own Customer only.
    /// </summary>
    public static IQueryable<T> WhereInCustomerScope<T>(this IQueryable<T> query, CurrentUser user)
        where T : ICustomerScoped =>
        user.Role is UserRole.AccountantAdmin or UserRole.AccountantUser
            ? query
            : query.Where(e => e.CustomerId == user.CustomerId!.Value);
}
```

Rules:

1. **Every query over a Customer-scoped entity calls it.** Including single-record reads by
   identifier — 02 §1 is explicit that the scope check applies to reads by id, so
   `FirstOrDefaultAsync(x => x.Id == id)` without the scope call is a cross-Customer leak that
   works whenever the caller guesses an id.
2. **Out of scope is `404`, never `403`.** Because the filter removes the row, the natural result
   is "not found" — which is exactly the required response. Do not add a second lookup that
   distinguishes "exists but not yours" and returns `403`; that reintroduces the leak the
   `404` rule exists to close.
3. **It is chosen over an EF global query filter deliberately.** A global filter cannot express
   Employee scope, which is not "their Customer" but *Tickets where they are Creator or
   Subject* — a narrower rule that needs the Ticket's own columns. Two mechanisms with different
   granularity would be worse than one explicit one.
4. **Employee scope is narrower and is applied in addition.** `WhereInCustomerScope` is
   necessary but not sufficient for an `Employee`; the `Tickets` slice applies the
   Creator-or-Subject restriction on top. Never treat an Employee as having Customer-wide
   visibility.
5. **Every slice owning a Customer-scoped entity has a test** asserting that a caller from
   Customer A reading a Customer B record by identifier receives `404`. This is the test that
   catches a forgotten call, and it is not optional.

## 5. Data access strategy: Entity Framework and DbContext

### DbContext ownership: One per slice — LOCKED

Each slice owns one `DbContext` subclass in its `Infrastructure/` folder. It contains
**only** the entities of that slice, never cross-slice references.

**Why per-slice:** EF DbContexts are not unit-of-work boundaries the way you might expect.
Each context tracks its own change set. Per-slice contexts make it explicit that a handler
in one slice does not accidentally load entities from another slice's context. This enforces
the architectural boundary and prevents the most common violation of vertical slicing.

Example:

```csharp
// Slices/Customers/Infrastructure/CustomersDbContext.cs
public class CustomersDbContext : DbContext
{
    public DbSet<Customer> Customers => Set<Customer>();

    // Required. Without this constructor the context cannot be configured with a provider.
    public CustomersDbContext(DbContextOptions<CustomersDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new CustomerConfiguration());
    }
}
```

Register it with `AddDbContext`, which supplies the options and the provider. This line lives
in the slice's own `{Slice}Registration.cs` (section 7), never in `Program.cs`:

```csharp
services.AddDbContext<CustomersDbContext>(o => o.UseNpgsql(connectionString));
```

> **Amended below.** The shape above is correct in outline, but the real registration uses the
> `(sp, o)` overload and passes the request's **shared connection** instead of a connection
> string, so that the audit entry can commit in the same transaction as the mutation. See *The
> audit write shares the mutating slice's transaction* later in this section. Use that form; it
> supersedes this line.

**Never `services.AddScoped<CustomersDbContext>()`**, and never both. A bare
`AddScoped` registers the context with no provider configured, and if both are present the
later registration wins and silently discards the configured options.

Another slice **never** references `CustomersDbContext`.

### Column naming: entities are PascalCase, columns are snake_case

The SQL scripts in section 6 create `snake_case` columns. EF Core's default convention maps
`Customer.LegalName` to a column called `LegalName`. **These do not match**, and every query
fails against PostgreSQL with `column c.LegalName does not exist`.

Map every property explicitly with `HasColumnName("legal_name")` in the entity's
`IEntityTypeConfiguration`. Every property of every entity, no exceptions. This failure is
invisible under the in-memory provider — a green test suite proves nothing about it.

### Cross-slice data: go through ExternalInterfaces

When Tickets needs Customer data, it calls `ICustomerApi.GetCustomer()`, not
`CustomersDbContext`. The `CustomerApi` implementation in the Customers slice handles the
query and returns a Dto.

```csharp
// Slices/Tickets/Application/Handlers/SubmitTicketHandler.cs
public class SubmitTicketHandler
{
    private readonly ICustomerApi _customerApi;
    private readonly TicketsDbContext _db;

    public async Task<TicketSubmittedDto> Handle(
        SubmitTicketRequestDto req, CurrentUser user, CancellationToken ct)
    {
        // Ask the question, do not fetch the state and re-derive the answer. IsActiveAsync
        // returns false for both "suspended" and "no such Customer", which is the
        // fail-closed answer to "may work be opened for this Customer".
        if (!await _customerApi.IsActiveAsync(req.CustomerId, ct))
            throw new AppException("This customer is suspended.", 422);

        var ticket = new Ticket { /* ... */ };
        _db.Tickets.Add(ticket);
        await _db.SaveChangesAsync(ct);
        return new TicketSubmittedDto { /* ... */ };
    }
}
```

Two things the example is careful about, both of which an earlier draft got wrong:

- **It does not name `CustomerStatus`.** That enum lives in `Slices/Customers/Core/`, and
  dependency rule 2 forbids a slice from referencing another slice's `Core` — so
  `customer.Status == CustomerStatus.Suspended` in the `Tickets` slice does not just smell wrong, it
  is the violation the rule names. An `ExternalInterface` returns its own contract types, and any
  status comparison a caller needs is exposed as a method or a computed property on those types.
- **The Tickets slice never holds a reference to `CustomersDbContext`.**

### Transaction boundaries: per-request

A single HTTP request is typically one transaction. If a handler in Tickets needs to call
CustomerApi and then write its own changes, that is **two separate transactions** — one in
Customers (inside the ICustomerApi call), one in Tickets (the handler's `SaveChangesAsync`).

This is deliberate. Cross-slice transactions are hard to reason about and often create
hidden dependencies. If atomicity across slices is truly required (e.g., "if I can't write
the ticket, don't write the Customer record"), express it explicitly as a compensating
action on failure, not as a shared transaction.

### The audit write shares the mutating slice's transaction — LOCKED

There is exactly **one** exception to "no cross-slice transactions", and it exists because
`01-DomainModel.md` §8 requires that no committed change is ever unaudited.

**The rule: a mutation and its audit entry commit together or not at all.** If the audit write
fails, the mutation rolls back and the caller receives a `500`. An audited action that quietly
succeeded without its audit row is not acceptable, and neither is an audit row for a mutation
that never committed.

This requires that `AuditDbContext` and the mutating slice's context be on the **same database
connection and the same transaction**. Three pieces make that work.

**1. One connection per request, in `Shared/Data/`:**

```csharp
namespace AccountantApp.Api.Shared.Data;

/// <summary>
/// One NpgsqlConnection per HTTP request, shared by every DbContext in that request.
/// Scoped. Disposing it closes the connection.
/// </summary>
public sealed class RequestConnection : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; }

    public RequestConnection(IConfiguration configuration) =>
        Connection = new NpgsqlConnection(configuration.GetConnectionString("Default"));

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
```

**2. Every slice registers its context against that connection.** This *amends* the locked
registration line earlier in this section — the provider is still Npgsql, but the overload takes
the shared connection rather than a connection string:

```csharp
// In {Slice}Registration.cs. Note the (sp, o) overload — the plain o => overload cannot
// reach the service provider and so cannot get the shared connection.
services.AddDbContext<CustomersDbContext>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<RequestConnection>().Connection));
```

`services.AddScoped<CustomersDbContext>()` is still forbidden, for the same reason as before.

**3. `Shared/Data/IRequestTransaction`** owns the transaction and the enlistment:

```csharp
public interface IRequestTransaction
{
    /// <summary>Begins the request's transaction and enlists the given context. Idempotent.</summary>
    Task<IAsyncDisposable> BeginAsync(DbContext context, CancellationToken ct);

    /// <summary>Enlists a further context in the already-open transaction. No-op if none is open.</summary>
    Task EnlistAsync(DbContext context, CancellationToken ct);

    Task CommitAsync(CancellationToken ct);
}
```

Rules:

1. **A mutating handler wraps its work.** `BeginAsync(_db, ct)` first, `SaveChangesAsync`, the
   `IAuditApi` call, then `CommitAsync`. Disposal without a commit rolls back.
2. **`AuditApi` enlists itself.** It calls `EnlistAsync(_auditDb, ct)` before writing, so a
   handler cannot forget to include the audit entry in the transaction. This is the whole point:
   correctness does not depend on the calling slice remembering.
3. **A denial has no transaction, and that is correct.** `PermissionChecker` runs before any
   handler work, so `EnlistAsync` finds nothing open and the audit row is written and committed
   on its own. That is exactly what the denial rule in section 4 requires — the denial is
   recorded even though the request fails.
4. **Read-only handlers open no transaction.** They call neither `BeginAsync` nor `IAuditApi`,
   with one exception: a document **download** is audited (`01-DomainModel.md` §6), so it
   behaves like a mutation even though it changes nothing.
5. **This does not license general cross-slice transactions.** `Audit` is the only slice that
   joins another slice's transaction. A handler must never call another domain slice's
   `ExternalInterface` inside its own transaction and expect that call to roll back.
6. **Do not use `TransactionScope` or `System.Transactions`.** Two Npgsql connections in one
   ambient scope escalate to a distributed transaction requiring two-phase commit. Sharing one
   connection is the mechanism; ambient enlistment is not.

## 6. Migration strategy: SQL scripts, per-slice

Each slice owns its database schema changes. Migrations live in the slice's `Infrastructure/Migrations/` folder as **raw SQL scripts**.

### Naming and execution order

File name format: `YYYYMMDD_###_ShortDescription.sql`

Examples:
```
Slices/Customers/Infrastructure/Migrations/
├── 20260828_001_CreateCustomersTable.sql
├── 20260828_002_AddSuspensionToCustomers.sql
├── 20260829_003_CreateIndexOnCustomerTaxNumber.sql

Slices/Employees/Infrastructure/Migrations/
├── 20260828_001_CreateEmployeeTable.sql
├── 20260828_002_AddEmployeeStatusField.sql
└── 20260829_003_AddUniqueConstraintOnEmail.sql
```

**Execution order:** scripts are executed **in alphabetical order by filename**. Dates
ensure no collision; sequence numbers (after the date) order changes within a single date.

The builder runs migrations **manually on startup** (not automatically in `DbContext`), from
`Shared/Migrations/SqlMigrationRunner.cs`:

1. Scan `Slices/**/Infrastructure/Migrations/` for all `.sql` files.
2. Sort by filename, then by slice name as tiebreaker.
3. Track which have been run in a `schema_versions` table.
4. Execute any new ones in order, each in its own transaction.

### The tracking key is the slice-relative path, never the bare filename — LOCKED

The `schema_versions` primary key is the script's path **relative to `Slices/`**, e.g.
`Customers/Infrastructure/Migrations/20260828_001_CreateCustomersTable.sql`. Do not key on
`Path.GetFileName(script)`.

Look at the two examples above: `Slices/Customers/.../20260828_001_CreateCustomersTable.sql`
and `Slices/Employees/.../20260828_001_CreateEmployeeTable.sql` share neither name — but
`20260828_002_AddSuspensionToCustomers.sql` and `20260828_002_AddEmployeeStatusField.sql`
sit at the same date and sequence number, and nothing in the naming rule stops two slices
from choosing the same description too. Slices are developed independently and their sequence
numbers restart at `001` each, so filename collisions across slices are expected, not
unlikely. Keyed on the bare filename, the first slice's script runs, records the name, and the
second slice's script is silently treated as already applied — a missing table that surfaces
only at the first query. The column is wide enough for a path: `script_name VARCHAR(500)
PRIMARY KEY`, not `VARCHAR(255)`.

Store the path with forward slashes so a script applied on a Windows dev machine is not
re-applied inside the Linux container.

### Per-script structure

Each script is complete and idempotent where possible:

```sql
-- Slices/Customers/Infrastructure/Migrations/20260828_001_CreateCustomersTable.sql

CREATE TABLE customers (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    legal_name VARCHAR(255) NOT NULL,
    trading_name VARCHAR(255),
    tax_number VARCHAR(50),
    status VARCHAR(20) NOT NULL DEFAULT 'Active',
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_customers_status ON customers(status);
```

No rollback scripts. Migrations are append-only; if a mistake is made, fix it in a new
migration.

## 7. Handler pattern: minimal API handlers

Every operation is a handler. No "service layer" sitting between handlers and the database.
A handler owns its own transactions.

### Handler structure

```csharp
// Slices/Customers/Application/Handlers/CreateCustomerHandler.cs

public class CreateCustomerHandler
{
    private readonly CustomersDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _auditApi;

    // Four dependencies, and note which ones are ABSENT: no IIdentityApi, no
    // INotificationApi. `Customers` may depend on `Audit` and nothing else
    // (03-SliceInventory.md §2). A constructor here that asks for another domain
    // slice's ExternalInterface is an architecture violation that compiles.
    public CreateCustomerHandler(
        CustomersDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi auditApi)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _auditApi = auditApi;
    }

    public async Task<CustomerCreatedDto> Handle(
        CreateCustomerRequestDto req,
        CurrentUser user,
        CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "CreateCustomer", ct: ct);

        // Validate every string against the VARCHAR(n) in the migration BEFORE saving.
        // See "Input length and format validation is 400/422, never 500" in section 8.

        await using var tx = await _transaction.BeginAsync(_db, ct);

        var customer = new Customer
        {
            LegalName = req.LegalName,
            TaxNumber = req.TaxNumber,
            Status = CustomerStatus.Active,
        };

        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);

        // AuditApi enlists itself in the transaction opened above, so this entry and the
        // customer row commit together or not at all. See "The audit write shares the
        // mutating slice's transaction" in section 5.
        await _auditApi.LogAsync(new AuditEntry(
            Action: AuditActions.CustomerCreated,
            TargetKind: AuditTargets.Customer,
            TargetId: customer.Id.ToString(),
            CustomerId: customer.Id,
            After: new { customer.LegalName, customer.TaxNumber, customer.Status }), ct);

        await tx.CommitAsync(ct);

        return new CustomerCreatedDto { Id = customer.Id };
    }
}
```

**This handler creates a Customer and nothing else.** An earlier draft of this document showed it
also creating the first Customer Admin, by injecting `IIdentityApi`. That was wrong twice over and
is corrected here rather than deleted, because it is the exact mistake a builder will make:

1. **It violated the dependency table.** `Customers` may depend on `Audit` only. `Employees →
   Customers` already exists, so `Customers → Employees` is a cycle, and `Identity → Customers` was
   later added for the login check — making `Customers → Identity` a cycle too.
2. **`02-AuthorizationMatrix.md` §3 still requires the two to happen in one operation.** That
   requirement is satisfied — the composite operation lives in the **`Employees`** slice, which
   already depends on `Customers`, `Identity`, and `Notifications`. See
   [03-SliceInventory.md](../03-SliceInventory.md) §1.

The general rule the corrected example teaches: **a handler's constructor is the slice's dependency
declaration.** Before adding a parameter, check the caller's row in
[03-SliceInventory.md](../03-SliceInventory.md) §2. Nothing in the compiler or the DI container will
stop you — every `I*Api` is registered in the same container, so an illegal dependency resolves
happily at runtime.

### Handler registration and endpoints

Handlers are injected into minimal API endpoints in `*Endpoints.cs`:

```csharp
// Slices/Customers/CustomerEndpoints.cs

namespace Slices.Customers;

public static class CustomerEndpoints
{
    public static void MapCustomerEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/customers")
            .WithTags("Customers");
        
        g.MapPost("/create", CreateCustomer)
            .WithName("CreateCustomer")
            .Produces<CustomerCreatedDto>(201)
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(409);
        
        g.MapGet("/detail", GetCustomer)
            .WithName("GetCustomer")
            .Produces<CustomerDetailDto>(200)
            .Produces<ProblemDetails>(404);
    }
    
    private static async Task<IResult> CreateCustomer(
        CreateCustomerRequest req,
        CreateCustomerHandler handler,
        CurrentUser user,
        CancellationToken ct)
    {
        var result = await handler.Handle(req, user, ct);
        return Results.Created($"/api/customers/detail?id={result.Id}", result);
    }
    
    private static async Task<IResult> GetCustomer(
        Guid id,
        GetCustomerHandler handler,
        CurrentUser user,
        CancellationToken ct)
    {
        return Results.Ok(await handler.Handle(id, user, ct));
    }
}
```

**No `try/catch` in an endpoint.** Exception handling is cross-cutting
([03-SliceInventory.md](../03-SliceInventory.md) section 4): one middleware translates
`AppException` into `ProblemDetails`, taking the status code from `AppException.StatusCode`.
Catching per endpoint duplicates that logic and, as written above, hardcodes `403` onto every
failure — so a duplicate-name conflict is reported as a permission denial.

### How `CurrentUser` reaches an endpoint

`CurrentUser` must be registered as a scoped **service**. It is not a route value and not a
body, so if it is left unregistered ASP.NET Core infers it as a body parameter: illegal on a
`GET`, and a second body parameter on a `POST`. Every affected endpoint then fails at request
time with `500 Failure to infer one or more parameters` — the routes build fine and the app
starts, so only running a request reveals it.

```csharp
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>(sp =>
{
    var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext
        ?? throw new InvalidOperationException("No HttpContext.");
    return CurrentUserFactory.FromPrincipal(http.User);   // throws AppException(401) if anonymous
});
```

### Slice registration: one `{Slice}Registration.cs` per slice — LOCKED

**Every slice owns a file that registers everything in it.** `Program.cs` never names a
handler, a DbContext, or an `I{Domain}Api` implementation — it calls one method per slice.

The file and method are named from the slice name exactly as
[03-SliceInventory.md](../03-SliceInventory.md) section 1 spells it — so
`TicketTypesRegistration.cs` / `AddTicketTypesSlice`, `CustomersRegistration.cs` /
`AddCustomersSlice`. Do not singularise, and do not invent `Module`, `Bootstrap`, or
`DependencyInjection` as alternatives.

`Slices/Customers/CustomersRegistration.cs`:

```csharp
namespace Slices.Customers;

public static class CustomersRegistration
{
    public static IServiceCollection AddCustomersSlice(
        this IServiceCollection services,
        IConfiguration config)
    {
        // 1. This slice's DbContext. AddDbContext, never AddScoped. See section 5.
        services.AddDbContext<CustomersDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Default")));

        // 2. Every handler in this slice. One line each, no exceptions.
        services.AddTransient<CreateCustomerHandler>();
        services.AddTransient<GetCustomerHandler>();
        services.AddTransient<ListCustomersHandler>();
        services.AddTransient<SuspendCustomerHandler>();

        // 3. This slice's ExternalInterfaces implementation. Register it even if no
        //    other slice calls it yet — see the rule below.
        services.AddScoped<ICustomerApi, CustomerApi>();

        // 4. Slice-internal services and validators, if any.
        services.AddValidatorsFromAssemblyContaining<CreateCustomerRequestValidator>();

        return services;
    }
}
```

Rules:

1. **It registers only what the slice owns.** Never register a type from `Shared/` here, and
   never a type from another slice — not even an interface you consume. `Program.cs` owns
   `Shared/`; the *callee's* registration file owns `I{Domain}Api`.
2. **It returns `IServiceCollection`** so calls chain, and it takes `IConfiguration` because
   it needs the connection string. Do not read `Environment.GetEnvironmentVariable` here.
3. **Registering `I{Domain}Api` is mandatory, not conditional on a caller existing.** Every
   slice may call `Audit`, so `AuditApi` must be registered from the first commit. A handler
   that injects an unregistered `IAuditApi` makes the whole app fail at `Build()` with
   `Unable to resolve service for type 'IAuditApi'` — the process exits before serving a
   single request, and no amount of building or unit-testing reveals it.
4. **Adding a handler means adding a line here, in the same commit.** A handler that exists
   but is not registered compiles, and the endpoint that injects it returns `500` on the first
   call. This is the single most common way a slice ships broken.
5. **Do not "register handlers automatically" by assembly scanning.** An explicit list is the
   inventory of the slice; scanning hides the omission this file exists to prevent.
6. **Endpoints are not registered here.** Routes extend `WebApplication`, not
   `IServiceCollection`, and are mapped after `Build()` — they stay in
   `{Slice}Endpoints.cs`. So each slice contributes exactly two lines to `Program.cs` — except
   `Documents`, which contributes one, for the reason given after the example below.

### `Program.cs` in full

`Program.cs` has three parts and nothing else: `Shared/` services, one `Add*Slice` per slice,
then middleware and one `Map*Endpoints` per slice.

```csharp
var builder = WebApplication.CreateBuilder(args);

// ── Shared services (owned by Program.cs, not by any slice) ──────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>(/* see above */);
builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
builder.Services.AddSingleton<SqlMigrationRunner>();
builder.Services.AddProblemDetails();

// ── Slices: one line each, in dependency order (see 03-SliceInventory.md §2) ──
builder.Services.AddAuditSlice(builder.Configuration);
builder.Services.AddNotificationsSlice(builder.Configuration);
builder.Services.AddIdentitySlice(builder.Configuration);
builder.Services.AddCustomersSlice(builder.Configuration);
builder.Services.AddEmployeesSlice(builder.Configuration);
builder.Services.AddTicketTypesSlice(builder.Configuration);
builder.Services.AddDocumentsSlice(builder.Configuration);
builder.Services.AddTicketsSlice(builder.Configuration);

var app = builder.Build();

// ── Middleware ───────────────────────────────────────────────────────────
app.UseForwardedHeaders(/* see 04-Infrastructure.md §3 */);
app.UseExceptionHandler();          // AppException → ProblemDetails
app.UseAuthentication();
app.UseAuthorization();

// ── Slice routes: one line each ──────────────────────────────────────────
app.MapAuditEndpoints();
app.MapNotificationEndpoints();
app.MapIdentityEndpoints();
app.MapCustomerEndpoints();
app.MapEmployeeEndpoints();
app.MapTicketTypesEndpoints();
// NO app.MapDocumentEndpoints() -- Documents has no endpoints. Tickets registers
// /api/documents/* instead. This is the one slice contributing ONE line, not two.
app.MapTicketEndpoints();       // /api/tickets/* AND /api/documents/*

// ── SPA fallback: must be last (see 04-Infrastructure.md §1) ─────────────
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
```

Registration order between slices does not matter to the DI container — it resolves lazily —
but keep the list in dependency order anyway, because it makes an illegal dependency visible
as a slice appearing above the one it needs.

**`Program.cs` gets two lines per slice, with exactly one exception.** `Documents` gets one:
it has no HTTP endpoints at all. A document's access rules come entirely from its ticket and
must be re-checked at the moment of download, but `Documents` may not depend on `Tickets`
(that edge is a cycle), so `Tickets` registers `/api/documents/upload`, `/list`, `/download`,
and `/delete` and performs every authorization check on them.

> Do not "fix" this by creating an empty `MapDocumentEndpoints()`. An extension method that
> maps nothing is worse than its absence: it makes the file look symmetric and hides the one
> asymmetry a reader needs to know about. See
> [Slices/Documents/IMPLEMENTATION_PLAN.md](../Slices/Documents/IMPLEMENTATION_PLAN.md) §0.2
> and [Slices/Tickets/IMPLEMENTATION_PLAN.md](../Slices/Tickets/IMPLEMENTATION_PLAN.md) §0.3.

**If `Program.cs` mentions a handler type, a `DbContext`, or a `*Api` class, that line is in
the wrong file.** Move it into the owning slice's registration file.

### Handler interface pattern

Each handler's signature follows the same pattern:

```csharp
public async Task<TResponse> Handle(TRequest req, CurrentUser user, CancellationToken ct)
```

where:
- `TRequest` is a Dto (or a value, e.g. `Guid id`, if there is no complex input)
- `TResponse` is the output Dto
- `CurrentUser` is always included — the handler must know who is calling
- `CancellationToken` allows graceful cancellation

## 8. API conventions

### Route shape

All routes follow the pattern: `/api/{domain}/{action}`

Examples:
- `POST /api/customers/create` — create a Customer
- `GET /api/customers/list` — list all Customers (paginated)
- `GET /api/customers/detail?id=...` — retrieve one Customer
- `POST /api/customers/update` — update a Customer
- `POST /api/tickets/submit` — submit a Ticket
- `POST /api/tickets/pickup` — pick up a Ticket (assign to self, move to InReview)
- `POST /api/tickets/reassign` — assign to another Accountant
- `GET /api/employees/list` — list Employees (filtered by query params)

This route style is **explicit and intent-driven**, not REST-style with HTTP verbs alone.
Each endpoint name matches its handler name: `create` → `CreateCustomerHandler`, `pickup` →
`PickupTicketHandler`.

### Multi-word segments are kebab-case — LOCKED

Every example above is a single word, which says nothing about a slice like `TicketTypes`. The
rule: **lowercase, and hyphenate at each word boundary.**

| Slice / handler | Correct segment | Wrong |
|---|---|---|
| `TicketTypes` | `ticket-types` | `tickettypes`, `ticketTypes`, `TicketTypes`, `ticket_types` |
| `GetTicketTypeVersion` | `version` | `getTicketTypeVersion` |
| a version-history action | `version-history` | `versionhistory`, `versionHistory` |

So the routes for this slice are `/api/ticket-types/create`, `/api/ticket-types/list`, and so
on — never `/api/tickettypes/...`.

**Why hyphens rather than just concatenating:** `tickettypes` puts a doubled `t` at the seam,
and a doubled letter at a word boundary is one of the easiest typos to make and the hardest to
spot. Drop one and you get `ticketypes`, which is wrong and looks fine — in a URL bar, in a
`curl` line, in a React API client, in a route string nobody re-reads. The failure is a `404`
from routing, indistinguishable from a resource that does not exist, so the search starts on
the server instead of on the path. `ticket-types` has no doubled letter and puts a visible
mark where the words meet, so the eye can check it.

Apply the same rule to query parameter names (`ticketTypeId` stays camelCase — that is a JSON
property, not a path segment) and nowhere else: **hyphens are for path segments only.**

**Query parameters for filtering and pagination:**

```
GET /api/tickets/list?customerId=...&status=InReview&pageNumber=1&pageSize=15&sortBy=createdAt&sortOrder=desc
```

Never nest resources in the URL (`/api/customers/1/employees` is forbidden). All filtering
is query params.

**Response codes:**
- `200` — success, payload in body
- `201` — created, payload in body
- `400` — validation error (client mistake)
- `401` — not authenticated
- `403` — authenticated but not authorized (role denial)
- `404` — resource not found (out of scope counts as 404)
- `409` — conflict (e.g., email already exists)
- `422` — unprocessable (semantic error, e.g., "can't create a second Accountant Admin")
- `500` — unexpected error

### Error responses

All errors return JSON in this shape:

```json
{
  "type": "https://yourapi.com/errors/validation-error",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "traceId": "...",
  "errors": {
    "email": ["Email is required", "Email is not valid"],
    "name": ["Name must be between 1 and 100 characters"]
  }
}
```

Lifted from ASP.NET Core's default `ProblemDetails`. Always use HTTP status codes correctly:
- `400` — validation error (client mistake)
- `401` — not authenticated
- `403` — authenticated but not authorized (role or scope denial)
- `404` — resource not found (out of scope counts as 404, not 403)
- `409` — conflict (e.g., email already exists)
- `422` — unprocessable (semantic error, e.g., "can't create a second Accountant Admin")
- `500` — unexpected server error

Never return `200` with an error message in the body.

### The exception middleware catches everything — LOCKED

`Shared/Errors/AppExceptionMiddleware.cs` has **two** catch blocks, in this order:

```csharp
try
{
    await next(context);
}
catch (AppException ex)
{
    // Expected, deliberate failure. Status and message come from the exception.
    await WriteProblemDetails(context, ex.StatusCode, ex.Message, ex.ErrorCode);
}
catch (Exception ex)
{
    // Unexpected. Log the full exception; tell the client nothing about it.
    logger.LogError(ex, "Unhandled exception for {Method} {Path}",
                    context.Request.Method, context.Request.Path);
    await WriteProblemDetails(context, 500, "An unexpected error occurred.", "internal_error");
}
```

**A middleware that catches only `AppException` is a bug**, not a simplification. Everything
the code does not anticipate — a `NullReferenceException`, a `RegexParseException` from a
user-supplied pattern, an `NpgsqlException` when the database drops, a `DbUpdateException`
from a unique-index violation — falls through to the ASP.NET Core default handler, which
returns an HTML error page in Development and a bare, bodyless `500` in Production. Either
way the client gets something that is not `ProblemDetails`, so the SPA's error parser fails
on the one response it most needs to read, and in Development a stack trace naming internal
types is served to the internet.

Two rules for the second block:

1. **Log the exception, with the request method and path.** It is the only record that the
   failure happened.
2. **Never put the exception message, type name, or stack trace in the response.** Not even
   in Development — the same code path serves production. The `traceId` is how a client
   report is correlated to the log entry.

Do not add a `catch (OperationCanceledException)` that returns `500`: a cancelled request is
the client disconnecting. Let it pass through, or return `499`/nothing; a disconnected client
is not a server error and must not page anyone.

### Unmatched `/api` routes return `ProblemDetails`, not an empty body

`04-Infrastructure.md` §1 requires that everything under `/api` is the API and everything
else is the SPA. A request to a mistyped API route must therefore produce a JSON `404`, not
the SPA's `index.html` and not a zero-length body. Add `app.UseStatusCodePages()` — or an
explicit `ProblemDetails`-writing fallback for `/api/{**rest}` — so that a `404` or `405`
produced by routing rather than by a handler still has the body shape documented above.

### Input length and format validation is `400`/`422`, never `500`

Every string field written to the database has a `VARCHAR(n)` limit in the migration script.
Validate that limit **before** `SaveChangesAsync`. Otherwise PostgreSQL raises
`22001: value too long for type character varying(n)`, EF wraps it in `DbUpdateException`,
and the caller gets a `500` for what is plainly a client mistake — a bug report about a
server fault every time a user pastes a long label.

Same rule for anything the server will later interpret: a user-supplied regular expression
must be compiled once at validation time inside a `try`/`catch (ArgumentException)` and
rejected with `422` if it does not parse. Never store a pattern that has never been compiled;
the failure would otherwise surface much later, inside a different slice, when a ticket is
validated against the stored field descriptor.

The rule in one line: **if a client can trigger it by sending a value, it is a `4xx`.** A
`500` means the server is broken, and every `500` in the log should be worth investigating.

### Pagination

For endpoints that return lists, support these query parameters:

```
GET /api/tickets?pageNumber=1&pageSize=15&sortBy=createdAt&sortOrder=desc
```

Response shape:

```json
{
  "pageNumber": 1,
  "pageSize": 15,
  "totalCount": 62,
  "totalPages": 5,
  "items": [...]
}
```

**Default `pageSize`: 15. Maximum: 50.** These two numbers are system-wide. Every paginated
endpoint in every slice uses them; a slice does not pick its own. A `pageSize` above the maximum
is **clamped, not rejected** — a caller asking for 5,000 rows gets 50 and a `200`, not a `400`.
A `pageSize` of zero or negative is clamped to 1, and a `pageNumber` below 1 is clamped to 1.

Sorting is optional; specify which fields are sortable per endpoint. Every paginated query needs
a **stable** sort — a unique tiebreaker column such as `id` after the sort key. Without it,
paging over rows with equal sort values silently skips and repeats records.

> The maximum is 50 rather than a large number on purpose. It is the ceiling on how much data
> one request can extract, so it is a security control as much as a performance one, and it
> bounds the worst case of an N+1 in any handler that has one. Do not raise it for a single
> endpoint that "needs more" — that endpoint needs a narrower filter instead.

### Dto naming

- Request: `<Entity><Action>RequestDto` (e.g., `CustomerCreateRequestDto`, `TicketSubmitRequestDto`)
- Response (single): `<Entity>DetailDto` (e.g., `CustomerDetailDto`)
- Response (list item): `<Entity>ListItemDto` (e.g., `CustomerListItemDto`)
- Update: `<Entity>UpdateRequestDto` (e.g., `CustomerUpdateRequestDto`)

Do not use `Command`, `Query`, or `Result` suffixes — they are CQS noise. The handler name
and method signature are clear enough.

### Versioning

API versioning is **not** in the URL by default. If you need to version, do it with a
request header (e.g., `api-version: 1.0`) and handle multiple versions in the handler if
needed. For v1, avoid versioning — design the schema well the first time.

## 9. Configuration, seeding, and local development

### appsettings.json

Non-secret local-development defaults only. Secrets come from the environment
([04-Infrastructure.md](../04-Infrastructure.md) section 4), and
`appsettings.Production.json` does not exist.

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Database=accountant_app;Username=postgres;Password=postgres"
  },
  "OfficeSettings": {
    "Name": "Your Office Name",
    "TaxNumber": "XX-XXXXX-XX",
    "Address": "123 Main St",
    "Email": "office@example.com"
  },
  "Seeding": {
    "FirstAdminEmail": "admin@example.com",
    "FirstAdminPassword": "ChangeMe123!"
  }
}
```

**There is no `JwtOptions` section and no signing key**, because there are no JWTs. Sessions
are an `HttpOnly` `Secure` `SameSite=Strict` cookie (README, locked). Do not add a bearer
token, an `Authorization` header, or anything in `localStorage`. Cookie authentication instead
needs persisted data-protection keys, or every restart signs every user out — see
[04-Infrastructure.md](../04-Infrastructure.md) section 4.

### Seeding on startup

In `Program.cs`, after `app.Build()`:

```csharp
using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedAsync();
}

app.Run();
```

The `DatabaseSeeder` (in `Shared/Seeding/DatabaseSeeder.cs`):

1. Runs migrations (scan and execute SQL scripts)
2. Creates the first `AccountantAdmin` account **if none exists**
3. Seeds any reference data (e.g., default Ticket Types)

**How the first Accountant Admin's credentials are supplied — DECIDED.** Standard
`IConfiguration` binding of the `Seeding` section, which gives both behaviours from one code
path and no branching on environment:

- **Production:** the environment variables `ACCOUNTANT_ADMIN_EMAIL` and
  `ACCOUNTANT_ADMIN_PASSWORD`, set by `docker-compose.yml`
  ([04-Infrastructure.md](../04-Infrastructure.md) section 4). Map them in `Program.cs`, or
  set `Seeding__FirstAdminEmail` / `Seeding__FirstAdminPassword` directly — pick one and use
  it consistently.
- **Local development:** the `Seeding` block of `appsettings.json` shown above.

Do **not** implement an interactive prompt (there is no terminal in the container) or a
sentinel credentials file. If neither source supplies a value and no Admin exists, **fail
startup with a clear message** — do not fall back to a built-in default password.

**Password reset on first login:** After seeding, the first Admin logs in with the seeded
password. The app must force a password change before they can do anything else. This is
handled in the Identity slice's login flow.

### The development-only test principal — required, and hard-gated

Slices are built one at a time, and `Identity` is not first. Until it exists nothing sets
`HttpContext.User`, so `CurrentUserFactory.FromPrincipal` throws `AppException(401)` and
**every endpoint of every slice returns `401`** — including the smoke check below and every
authorization, scoping, and field-stripping rule the slice was built to satisfy. Without a
way to present a role, none of it is verifiable by anything except a unit test that
constructs the handler directly, which is exactly the kind of test that passes on broken
wiring.

So the API supports one development-only authenticated principal. It is an authentication
bypass, so it carries **two independent guards** and both must pass:

```csharp
// Program.cs — after the CurrentUser registration, before app.Build()
var devAuthEnabled = builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue<bool>("DevAuth:Enabled");

if (devAuthEnabled)
{
    builder.Services.AddAuthentication("DevAuth")
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>("DevAuth", null);
}
```

- **Guard 1 — `IsDevelopment()`.** `ASPNETCORE_ENVIRONMENT` is `Production` in the container
  ([04-Infrastructure.md](../04-Infrastructure.md) section 2), so the scheme is not even
  registered there.
- **Guard 2 — `DevAuth:Enabled`.** Lives in `appsettings.Development.json` only. It is
  **absent from `appsettings.json`**, so its default is `false`; a machine that somehow runs
  with `ASPNETCORE_ENVIRONMENT=Development` still gets no bypass unless someone opted in
  explicitly in a file that is never deployed.

One guard is not enough: a missing environment variable turns the first into the wrong answer,
and an `appsettings.json` flag can be copied into a deployed image. Two unrelated conditions
means a single mistake cannot open it.

`Shared/Auth/DevAuthHandler.cs` reads the role and identity from **request headers** so a
`curl` line can choose a caller:

| Header | Meaning | Default if absent |
|---|---|---|
| `X-Dev-Role` | One of the four `UserRole` values. An unrecognised value **fails authentication** — never fall back to a role. | fail (`401`) |
| `X-Dev-User-Id` | The caller's user id (GUID). | a fixed, documented GUID |
| `X-Dev-Customer-Id` | The scoping Customer id, for `CustomerAdmin` and `Employee`. | none — and a `CustomerAdmin`/`Employee` without one **fails authentication**, because a Customer-scoped role with no scope would silently read everything |

Three rules:

1. **It sets a principal; it does not grant permission.** Requests still go through
   `IPermissionChecker`, so `403` and `404` behaviour is exercised for real. A dev principal
   that skipped authorization would verify nothing.
2. **It is deleted, not disabled, once `Identity` ships.** Delete `DevAuthHandler.cs`, the
   registration block, and the `DevAuth` config key in the same commit that adds real cookie
   login. Leaving dead bypass code in the tree is how it comes back.
3. **Log a warning at startup when it is active**, e.g.
   `"DevAuth is ENABLED — authentication is bypassed. Never in production."` A bypass that
   runs silently is a bypass someone forgets.

### Running locally

```bash
# Prerequisites
# - .NET 10 SDK
# - Postgres running via `docker compose up db` (see 04-Infrastructure.md section 2)

dotnet run --project AccountantApp.Api

# The API runs on http://localhost:5000, SQL migrations execute on startup, and the
# first Accountant Admin account is created if none exists.
```

**A successful build is not a working application.** `dotnet build` cannot catch an
unregistered service, an unbindable endpoint parameter, or a column-name mismatch — all three
are startup or first-request failures. After any change to `Program.cs` or an endpoint file,
run the app and call one endpoint:

```bash
curl -i http://localhost:5000/api/customers/list \
  -H "X-Dev-Role: AccountantAdmin"
```

The expected result is `200`, or a `403`/`404` that the authorization matrix predicts. A
**`401` means the request never reached the handler** and the check proved nothing: either
`DevAuth` is not enabled or the header is missing. Do not accept `401` as "the endpoint is
wired up".

If the process exits during `Build()` (`Some services are not able to be constructed`) or the
call returns `500 Failure to infer one or more parameters`, fix it before writing tests. Tests
that construct handlers directly will pass regardless.

## 10. Behavioural decisions — all resolved

**There are no open questions.** All ten behavioural decisions are LOCKED in
[01-DomainModel.md](../01-DomainModel.md) section 9, which is the authoritative text, and
[03-SliceInventory.md](../03-SliceInventory.md) section 6 maps each one to the slice that implements
it. Nothing in this document is open either; every decision here is settled.

If you find a behaviour that none of the ten cover, that is a **new** gap: flag it, do not invent
it. A per-slice plan under `Slices/` loses to every document above it, so a plan that contradicts
one of these decisions is wrong, not new.
