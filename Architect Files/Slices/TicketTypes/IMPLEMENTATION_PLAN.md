# TicketTypes Slice — Implementation Plan

This is an executable step-by-step plan. Follow it exactly. Do not add features, skip steps,
or make architectural decisions. If something is unclear, flag it rather than guess.

**Precedence:** if this document contradicts `00-Glossary.md`, `01-DomainModel.md`,
`02-AuthorizationMatrix.md`, or `03-SliceInventory.md`, those win. Stop and flag it.

---

## 0. Prerequisites — read before writing any code

This slice **cannot be built in isolation**. The following must exist first. If any is
missing, build it as part of this task, in this order, before starting section 1.

| Prerequisite | Where it lives | Why |
|---|---|---|
| `Shared/Auth/UserRole.cs` | `Shared/` | The four-role enum. Shared, **not** owned by `Identity` — `TicketTypes` may not depend on `Identity` (see `03-SliceInventory.md` §2). |
| `Shared/Auth/CurrentUser.cs` | `Shared/` | The resolved caller. See §0.1 — how it reaches a handler is prescribed, not free choice. |
| `Shared/Authorization/IPermissionChecker.cs` | `Shared/` | See §0.2. Fail-closed. |
| `Shared/Errors/AppException.cs` | `Shared/` | Carries an HTTP status code. |
| `Shared/Pagination/PaginatedResponse.cs` | `Shared/` | See §0.3. Do **not** define this inside a slice. |
| `Slices/Audit/ExternalInterfaces/IAuditApi.cs` + `AuditApi.cs` | `Slices/Audit/` | `IAuditApi` belongs to the **Audit slice**, not `Shared/`. `Shared/` must never contain a slice's contract (`App/GeneralAppArchitecture.md` §4, "Never in Shared"). A registered implementation must exist or the application will not start. |
| `Slices/Audit/AuditRegistration.cs` | `Slices/Audit/` | Every slice registers its own elements. `Audit` must expose `AddAuditSlice(IServiceCollection, IConfiguration)` binding `IAuditApi` → `AuditApi`; `TicketTypes` never registers another slice's types. See §7.1. |
| `Shared/Migrations/SqlMigrationRunner.cs` | `Shared/` | See §8. There are no EF Core migrations in this project. |
| `Shared/Auth/DevAuthHandler.cs` | `Shared/` | Until `Identity` exists nothing sets `HttpContext.User`, so **every endpoint returns `401`** and none of the success criteria below can be checked. The development-only test principal is specified in `App/GeneralAppArchitecture.md` §9 — two guards (`IsDevelopment()` **and** `DevAuth:Enabled`), role chosen per request by the `X-Dev-Role` header, deleted when real login ships. |

### 0.1 How `CurrentUser` reaches a handler — prescribed

`CurrentUser` is **not** a minimal-API endpoint parameter. ASP.NET Core cannot infer it: it
is neither a route value nor a registered service, so it is inferred as a body parameter,
which fails on `GET` and collides with the request DTO on `POST`. Every endpoint would
return `500 Failure to infer one or more parameters`.

Register it as a scoped service resolved from `HttpContext`:

```csharp
// Program.cs
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>(sp =>
{
    var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext
        ?? throw new InvalidOperationException("No HttpContext.");
    return CurrentUserFactory.FromPrincipal(http.User);   // Shared/Auth/CurrentUserFactory.cs
});
```

`CurrentUserFactory.FromPrincipal` reads the caller's identifier and role claims and throws
`AppException(401)` if the caller is not authenticated. Once registered this way, `CurrentUser`
binds as a service and the endpoint signatures in §6 work unchanged.

### 0.2 The permission checker — fail-closed

```csharp
// Shared/Authorization/IPermissionChecker.cs
public interface IPermissionChecker
{
    // Throws AppException(403) if denied. Audits every denial before throwing.
    Task RequireAsync(CurrentUser user, string action, object? scope = null,
                      CancellationToken ct = default);
}
```

Three rules the implementation must obey:

1. **An unknown action name denies.** Never `default => allow-everyone`. A typo in an action
   string must lock everyone out, not let everyone in.
2. **Every denial is written to the audit log** before the exception is thrown
   (`02-AuthorizationMatrix.md`).
3. **The method is `async` and callers `await` it.** It is not `void Require(...)`. Auditing a
   denial is a database write; a synchronous signature forces
   `LogAsync(...).GetAwaiter().GetResult()`, which blocks a thread-pool thread and, if the
   audit write throws, replaces the `AppException(403)` with an `NpgsqlException` — the caller
   gets `500` and the denial is never recorded. If the audit write fails, log it and still
   throw the `403`. See `App/GeneralAppArchitecture.md` §4.

Ticket Types are not Customer-scoped, so `scope` is always `null` in this slice. Pass `ct`
positionally as `ct: ct` so the `scope` default is not accidentally filled with the token.

### 0.3 Pagination

```csharp
// Shared/Pagination/PaginatedResponse.cs
public class PaginatedResponse<T>
{
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }          // required by App/GeneralAppArchitecture.md §8
    public List<T> Items { get; set; } = new();
}
```

Default `PageSize` is **15**, maximum **50** (`App/GeneralAppArchitecture.md` §8). Clamp,
do not reject.

> **The shipped code does not yet match this.** These numbers were changed system-wide from
> 25/200 to 15/50 after the `TicketTypes` slice was built. Three files still carry the old
> literals and must be updated:
> `Slices/TicketTypes/Application/Dtos/ListTicketTypesRequestDto.cs` (`= 25`),
> `Slices/TicketTypes/Application/Handlers/ListTicketTypesHandler.cs` (`? 25 :` and the
> `Math.Clamp(..., 1, 200)`), and
> `Slices/TicketTypes/TicketTypesEndpoints.cs` (`pageSize ?? 25`).
> This plan is the spec; where the two disagree, change the code.

---

## 1. Database schema (SQL migration)

**File:** `Slices/TicketTypes/Infrastructure/Migrations/20260829_001_CreateTicketTypesSchema.sql`

This exact path. Migrations live inside the owning slice — not in a project-root
`Infrastructure/Migrations/` folder.

### Table: ticket_types

```sql
CREATE TABLE ticket_types (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code VARCHAR(100) NOT NULL,                  -- e.g., "PAYROLL_CERTIFICATE"; immutable
                                                 -- uniqueness is case-insensitive; see the index below
    display_name VARCHAR(255) NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    category VARCHAR(100) NOT NULL,              -- for grouping in the UI
    allow_employee_to_open BOOLEAN NOT NULL DEFAULT true,
    allow_subject_other_than_creator BOOLEAN NOT NULL DEFAULT true,
    is_active BOOLEAN NOT NULL DEFAULT true,
    version_number INTEGER NOT NULL DEFAULT 1,   -- MUST equal MAX(ticket_type_versions.version_number)
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX idx_ticket_types_code_lower ON ticket_types (LOWER(code));
CREATE INDEX idx_ticket_types_active ON ticket_types(is_active);
```

**Do not write `code VARCHAR(100) NOT NULL UNIQUE`.** A plain `UNIQUE` constraint is
case-**sensitive**, so PostgreSQL would accept `payroll_certificate` alongside
`PAYROLL_CERTIFICATE` while the create handler (§4.1) rejects the second as a duplicate with
`409` because it compares codes case-insensitively. The database and the handler must agree on
what "already exists" means, or two callers racing on the same code in different casing both
pass the handler's pre-check and both insert. The functional unique index on `LOWER(code)` is
the constraint; there is no second index on `code` itself.

### Table: ticket_type_versions

Immutable snapshots. Rows are inserted, never updated or deleted.

```sql
CREATE TABLE ticket_type_versions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_type_id UUID NOT NULL REFERENCES ticket_types(id),
    version_number INTEGER NOT NULL,             -- 1, 2, 3, ... per type
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE(ticket_type_id, version_number)
);

CREATE INDEX idx_ticket_type_versions_type_id ON ticket_type_versions(ticket_type_id);
```

### Table: field_descriptors

One row per field per version.

```sql
CREATE TABLE field_descriptors (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_type_version_id UUID NOT NULL REFERENCES ticket_type_versions(id),
    key VARCHAR(100) NOT NULL,                   -- e.g., "employee_name", "salary_amount"
    label VARCHAR(255) NOT NULL,
    help_text TEXT NOT NULL DEFAULT '',
    data_type VARCHAR(50) NOT NULL,              -- see enum below
    display_order INTEGER NOT NULL,
    group_name VARCHAR(100) NOT NULL DEFAULT '', -- optional section heading
    is_required BOOLEAN NOT NULL DEFAULT true,
    is_visible_to_customer BOOLEAN NOT NULL DEFAULT true,  -- false = Accountant-only

    -- Choice options, for data_type IN ('SingleChoice', 'MultipleChoice')
    choice_options TEXT NOT NULL DEFAULT '[]',   -- JSON array of {label, value}

    -- Validation rules
    min_length INTEGER,
    max_length INTEGER,
    min_value NUMERIC(18,4),
    max_value NUMERIC(18,4),
    earliest_date DATE,
    latest_date DATE,
    regex_pattern VARCHAR(500) NOT NULL DEFAULT '',
    allowed_file_types VARCHAR(500) NOT NULL DEFAULT '',   -- comma-separated, "pdf,jpg,png"
    max_file_size_bytes BIGINT,

    -- Conditional visibility: "only show if field X equals value Y"
    conditional_visibility_field_key VARCHAR(100) NOT NULL DEFAULT '',
    conditional_visibility_value VARCHAR(500) NOT NULL DEFAULT '',

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- A field key must be unique within one version, or FieldValue resolution
    -- in the Tickets slice is ambiguous.
    UNIQUE(ticket_type_version_id, key)
);

CREATE INDEX idx_field_descriptors_version_id ON field_descriptors(ticket_type_version_id);
```

No index on `key` alone — it is not queried without a version.

**`NUMERIC(18,4)`, not bare `DECIMAL`.** Bare `DECIMAL` in PostgreSQL is unbounded precision
and maps unpredictably to `decimal`.

**`TIMESTAMPTZ`, not `TIMESTAMP`.** All timestamps are written as UTC. Bare `TIMESTAMP` loses
the offset and Npgsql will reject a `DateTime` with `Kind == Utc` written to it.

**Data type enum (as strings in the `data_type` column):**
`SingleLineText`, `MultiLineText`, `WholeNumber`, `DecimalNumber`, `MoneyAmount`, `Date`,
`DateRange`, `YesNo`, `SingleChoice`, `MultipleChoice`, `FileUpload`

Put these eleven strings in one place — `Slices/TicketTypes/ExternalInterfaces/FieldDataTypes.cs`, a
static class with a `public const string` per type **and** a `public static readonly
IReadOnlySet<string> All` built *from those constants*. Handlers validate against `All` (§4.0);
everything else references `FieldDataTypes.MoneyAmount` and never the bare string.

> **AMENDED.** This said `Core/FieldDataTypes.cs` with only the `HashSet<string>`, and both halves of
> that were wrong for the same reason: `DataType` crosses the slice boundary on
> `FieldDescriptorDetailDto`, so every consumer needs this vocabulary to interpret it, and the Tickets
> slice switches on it eleven ways. In `Core` it was unreachable — dependency rule 2 — and with no named
> constants there was nothing to reach for anyway, so the first Tickets implementation declared its own
> eleven literals that nothing kept in sync. Contract vocabulary belongs beside the contract, and `All`
> is derived from the constants so a twelfth type cannot be added to one and forgotten in the other.
>
> Use `StringComparer.Ordinal`, not `OrdinalIgnoreCase`: these are stored values matched against a
> case-sensitive `CHECK` constraint, so accepting `"yesno"` here writes a row the database rejects — a
> `500` where the caller should have had a `422`.

---

## 2. EF Core entities and DbContext

### 2.0 Column naming — mandatory

The SQL schema in §1 uses `snake_case`. EF Core's default convention maps
`TicketType.DisplayName` to a column named `DisplayName`. **These do not match**, and every
query fails against PostgreSQL with `column t.DisplayName does not exist`.

An in-memory provider hides this completely. Do not rely on tests passing to conclude the
mapping is right.

Fix it once, in `OnModelCreating`, by mapping every property explicitly with
`HasColumnName("snake_case")` in the `IEntityTypeConfiguration` classes. Every property of
every entity. No exceptions.

### 2.1 Entity files

Non-nullable `string` properties are initialised to `string.Empty`; nullable value types stay
`?`. This matches the `NOT NULL DEFAULT ''` columns in §1 and avoids CS8618.

**File:** `Slices/TicketTypes/Core/TicketType.cs`

```csharp
namespace AccountantApp.Api.Slices.TicketTypes.Core;

public class TicketType
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;          // never changes
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool AllowEmployeeToOpen { get; set; } = true;
    public bool AllowSubjectOtherThanCreator { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int VersionNumber { get; set; } = 1;               // current version
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<TicketTypeVersion> Versions { get; set; } = new List<TicketTypeVersion>();
}
```

**File:** `Slices/TicketTypes/Core/TicketTypeVersion.cs`

```csharp
namespace AccountantApp.Api.Slices.TicketTypes.Core;

public class TicketTypeVersion
{
    public Guid Id { get; set; }
    public Guid TicketTypeId { get; set; }
    public int VersionNumber { get; set; }
    public DateTime CreatedAt { get; set; }

    public TicketType TicketType { get; set; } = default!;
    public ICollection<FieldDescriptor> FieldDescriptors { get; set; } = new List<FieldDescriptor>();
}
```

**File:** `Slices/TicketTypes/Core/FieldDescriptor.cs`

```csharp
namespace AccountantApp.Api.Slices.TicketTypes.Core;

public class FieldDescriptor
{
    public Guid Id { get; set; }
    public Guid TicketTypeVersionId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;      // see FieldDataTypes
    public int DisplayOrder { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public bool IsRequired { get; set; } = true;
    public bool IsVisibleToCustomer { get; set; } = true;
    public string ChoiceOptions { get; set; } = "[]";         // JSON
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public DateOnly? EarliestDate { get; set; }
    public DateOnly? LatestDate { get; set; }
    public string RegexPattern { get; set; } = string.Empty;
    public string AllowedFileTypes { get; set; } = string.Empty;   // "pdf,jpg,png"
    public long? MaxFileSizeBytes { get; set; }
    public string ConditionalVisibilityFieldKey { get; set; } = string.Empty;
    public string ConditionalVisibilityValue { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public TicketTypeVersion TicketTypeVersion { get; set; } = default!;
}
```

`EarliestDate`/`LatestDate` are `DateOnly?`, matching the `DATE` columns. `DateTime?` against
a `DATE` column silently discards a time component the caller may have believed was stored.

### 2.2 DbContext

**File:** `Slices/TicketTypes/Infrastructure/TicketTypesDbContext.cs`

```csharp
namespace AccountantApp.Api.Slices.TicketTypes.Infrastructure;

using Microsoft.EntityFrameworkCore;
using AccountantApp.Api.Slices.TicketTypes.Core;
using AccountantApp.Api.Slices.TicketTypes.Infrastructure.Configurations;

public class TicketTypesDbContext : DbContext
{
    public DbSet<TicketType> TicketTypes => Set<TicketType>();
    public DbSet<TicketTypeVersion> TicketTypeVersions => Set<TicketTypeVersion>();
    public DbSet<FieldDescriptor> FieldDescriptors => Set<FieldDescriptor>();

    public TicketTypesDbContext(DbContextOptions<TicketTypesDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new TicketTypeConfiguration());
        builder.ApplyConfiguration(new TicketTypeVersionConfiguration());
        builder.ApplyConfiguration(new FieldDescriptorConfiguration());
    }
}
```

### 2.3 EF configurations

Each configuration must, for every property: set `HasColumnName`, and mirror the SQL
constraints (`HasMaxLength`, `IsRequired`, `HasPrecision(18, 4)` for the two `NUMERIC`
columns). Indexes and unique constraints mirror §1 exactly — including
`HasIndex(f => new { f.TicketTypeVersionId, f.Key }).IsUnique()`.

**File:** `Slices/TicketTypes/Infrastructure/Configurations/TicketTypeConfiguration.cs`

```csharp
public class TicketTypeConfiguration : IEntityTypeConfiguration<TicketType>
{
    public void Configure(EntityTypeBuilder<TicketType> builder)
    {
        builder.ToTable("ticket_types");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
        builder.Property(t => t.DisplayName).HasColumnName("display_name").HasMaxLength(255).IsRequired();
        builder.Property(t => t.Description).HasColumnName("description").IsRequired();
        builder.Property(t => t.Category).HasColumnName("category").HasMaxLength(100).IsRequired();
        builder.Property(t => t.AllowEmployeeToOpen).HasColumnName("allow_employee_to_open");
        builder.Property(t => t.AllowSubjectOtherThanCreator).HasColumnName("allow_subject_other_than_creator");
        builder.Property(t => t.IsActive).HasColumnName("is_active");
        builder.Property(t => t.VersionNumber).HasColumnName("version_number");
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(t => t.Code).IsUnique();
        builder.HasIndex(t => t.IsActive);
    }
}
```

Write `TicketTypeVersionConfiguration` and `FieldDescriptorConfiguration` the same way —
every property gets a `HasColumnName`. `FieldDescriptorConfiguration` additionally needs:

```csharp
builder.Property(f => f.MinValue).HasColumnName("min_value").HasPrecision(18, 4);
builder.Property(f => f.MaxValue).HasColumnName("max_value").HasPrecision(18, 4);
builder.HasIndex(f => new { f.TicketTypeVersionId, f.Key }).IsUnique();
```

---

## 3. DTOs

Request DTOs live in `Slices/TicketTypes/Application/Dtos/`, one concern per file; the two RESPONSE
shapes live in `ExternalInterfaces/` (§5). **Do not declare a request DTO inside a handler file.**

**File:** `Application/Dtos/CreateTicketTypeRequestDto.cs`

```csharp
namespace AccountantApp.Api.Slices.TicketTypes.Application.Dtos;

public class CreateTicketTypeRequestDto
{
    public string Code { get; set; } = string.Empty;           // never changes
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool AllowEmployeeToOpen { get; set; } = true;
    public bool AllowSubjectOtherThanCreator { get; set; } = true;
    public List<CreateFieldDescriptorDto> Fields { get; set; } = new();
}

public class CreateFieldDescriptorDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;       // must be in FieldDataTypes.All
    public int DisplayOrder { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public bool IsRequired { get; set; } = true;
    public bool IsVisibleToCustomer { get; set; } = true;
    public List<ChoiceOptionDto>? ChoiceOptions { get; set; }  // only for choice types
    public FieldValidationDto? Validation { get; set; }
    public ConditionalVisibilityDto? ConditionalVisibility { get; set; }
}

public class ChoiceOptionDto            // shape only — declared in ExternalInterfaces/ (§5)
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class FieldValidationDto        // shape only — declared in ExternalInterfaces/ (§5)
{
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public DateOnly? EarliestDate { get; set; }
    public DateOnly? LatestDate { get; set; }
    public string RegexPattern { get; set; } = string.Empty;
    public List<string> AllowedFileTypes { get; set; } = new();   // e.g., ["pdf", "jpg"]
    public long? MaxFileSizeBytes { get; set; }
}

public class ConditionalVisibilityDto   // shape only — declared in ExternalInterfaces/ (§5)
{
    public string FieldKey { get; set; } = string.Empty;       // only show if this field...
    public string Value { get; set; } = string.Empty;          // ...equals this value
}
```

**File:** `Application/Dtos/EditTicketTypeRequestDto.cs`

```csharp
public class EditTicketTypeRequestDto
{
    public Guid TicketTypeId { get; set; }
    public string DisplayName { get; set; } = string.Empty;    // Code is immutable
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool AllowEmployeeToOpen { get; set; }
    public bool AllowSubjectOtherThanCreator { get; set; }
    public List<CreateFieldDescriptorDto> Fields { get; set; } = new();  // complete list
}
```

**File:** `Application/Dtos/ToggleTicketTypeRequestDto.cs`

```csharp
public class ToggleTicketTypeRequestDto
{
    public Guid TicketTypeId { get; set; }
    public bool NewIsActive { get; set; }
}
```

**File:** `Application/Dtos/GetTicketTypeRequestDto.cs`

```csharp
public class GetTicketTypeRequestDto
{
    public Guid TicketTypeId { get; set; }
}
```

**File:** `Application/Dtos/GetTicketTypeVersionRequestDto.cs`

```csharp
public class GetTicketTypeVersionRequestDto
{
    public Guid TicketTypeId { get; set; }
    public int VersionNumber { get; set; }
}
```

**File:** `Application/Dtos/ListTicketTypesRequestDto.cs`

```csharp
public class ListTicketTypesRequestDto
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 15;
    public bool? ActiveOnly { get; set; }   // null = role default; see §4.5
}
```

**File:** `ExternalInterfaces/TicketTypeDetailDto.cs`

```csharp
public class TicketTypeDetailDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool AllowEmployeeToOpen { get; set; }
    public bool AllowSubjectOtherThanCreator { get; set; }
    public bool IsActive { get; set; }
    public Guid VersionId { get; set; }             // the VERSION row's own id -- see below
    public int CurrentVersionNumber { get; set; }   // the type's current version
    public int VersionNumber { get; set; }          // the version these Fields came from
    public List<FieldDescriptorDetailDto> Fields { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class FieldDescriptorDetailDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public bool IsVisibleToCustomer { get; set; }
    public List<ChoiceOptionDto> ChoiceOptions { get; set; } = new();
    public FieldValidationDto Validation { get; set; } = new();
    public ConditionalVisibilityDto? ConditionalVisibility { get; set; }
}
```

`CurrentVersionNumber` and `VersionNumber` are separate on purpose. When
`GetTicketTypeVersionHandler` returns v1 of a type now on v3, the client needs both: the
fields are v1's, and it must be able to tell the type has since moved on.

> **AMENDED — `VersionId` added.** Without it this contract is not round-trippable, and that is a
> concrete break, not a tidiness point. A ticket stores `tickets.ticket_type_version_id`, a **Guid**, so
> that a later edit to the type cannot change what an already-open ticket asked for. Creation resolves
> the active version with `GetTicketTypeAsync` and must persist *which* version it got; every later read
> resolves it back with `GetVersionByIdAsync`, which takes that same Guid. Exposing only `Id` (the
> TYPE's id) and `VersionNumber` left the consuming slice able to see which version it was handed and
> unable to name it — the only ways out being to reach into this slice's `Infrastructure` to look the id
> up, which dependency rule 2 forbids, or to store the version *number* and re-resolve by
> (type, number) on every read, a second resolution path that can disagree with the first.
>
> `ToDetail` sets `VersionId = version.Id`, the VERSION row's id — **not** `type.Id`. Getting that wrong
> compiles and stores a Guid that `GetVersionByIdAsync` never finds, so
> `TicketTypesFlowTests.The_active_version_read_names_the_version_it_projected_so_a_ticket_can_store_it`
> asserts it is neither `Guid.Empty` nor the type's id, and that the round trip returns the same fields.

**File:** `ExternalInterfaces/TicketTypeListItemDto.cs`

```csharp
public class TicketTypeListItemDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int CurrentVersionNumber { get; set; }
}
```

---

## 4. Handlers

### 4.0 Rules that apply to every handler in this slice

Write these once and reuse them. Getting them wrong in one handler is a defect even if the
other five are right.

**A. Constructor injection only.** Handlers are resolved from DI (see §7). Endpoints must
never call `new CreateTicketTypeHandler(...)` — that bypasses DI and makes the registrations
dead code.

**B. Signature.** Every handler has exactly one public method:

```csharp
public async Task<TResponse> Handle(TRequest req, CurrentUser user, CancellationToken ct)
```

Pass `ct` to every `await`. Do not omit it.

**C. Authorization is not optional on reads.** Every handler starts with a
`await _permissions.RequireAsync(user, "<Action>", ct: ct)` call, including the three read
handlers. It is awaited, never called as `void`, and never blocked on with `.GetResult()`
(`App/GeneralAppArchitecture.md` §4, rule 3). Action
names: `CreateTicketType`, `EditTicketType`, `ToggleTicketType`, `ReadTicketType`,
`ListTicketTypes`.

**D. Accountant-only field stripping — mandatory, server-side.**
`02-AuthorizationMatrix.md` §5: *"Accountant-only Field Descriptors are stripped from
responses to Customer-side callers, on the server."*

Write one shared private mapper used by every read path:

```csharp
// Slices/TicketTypes/Application/TicketTypeMapper.cs
internal static class TicketTypeMapper
{
    internal static bool IsCustomerSide(UserRole role) =>
        role is UserRole.CustomerAdmin or UserRole.Employee;

    internal static TicketTypeDetailDto ToDetail(
        TicketType type, TicketTypeVersion version, UserRole callerRole)
    {
        var fields = version.FieldDescriptors.AsEnumerable();
        if (IsCustomerSide(callerRole))
            fields = fields.Where(f => f.IsVisibleToCustomer);   // <-- the stripping
        ...
    }
}
```

Omitting the `Where` leaks the existence, label, and help text of Accountant-only fields to
every Employee. Never filter this in the React client instead.

**E. Customer-side visibility filter on reads.** `02-AuthorizationMatrix.md` §5: for a
Customer-side caller, *"'Filtered' means: only `Active` types, and only types whose audience
permits the caller's role"*, and a type outside their audience *"is not returned by the API at
all"*. So for `CustomerAdmin` and `Employee`, `GetTicketTypeHandler` and
`GetTicketTypeVersionHandler` must throw `AppException(404)` — not 403 — when the type is
`IsActive == false`, and additionally when `AllowEmployeeToOpen == false` and the caller is
an `Employee`. A 403 would confirm the type exists.

**F. Field-set validation.** Both write handlers validate the incoming `Fields` list before
touching the database, and throw `AppException(422)` on the first failure:

1. `Fields` is not empty.
2. Every `Key` is non-empty, ≤ 100 chars, and **unique within the request**
   (case-insensitive). Duplicates would violate `UNIQUE(ticket_type_version_id, key)` and
   surface as an opaque 500.
3. Every `DataType` is in `FieldDataTypes.All`. Without this check `"Banana"` is persisted
   and the Tickets slice fails later, far from the cause.
4. `SingleChoice` and `MultipleChoice` fields have at least two `ChoiceOptions`; all other
   types have none.
5. If `ConditionalVisibility` is set, `ConditionalVisibility.FieldKey` **must name another
   field in the same request**, and must not be the field's own key. A dangling reference
   produces a field that can never be shown.
6. `MinLength <= MaxLength`, `MinValue <= MaxValue`, `EarliestDate <= LatestDate` where both
   are present.
7. **Every string is within its column's `VARCHAR` limit.** Checking `Key` alone is not
   enough — a caller who pastes a 400-character label gets a `500`, because PostgreSQL raises
   `22001: value too long for type character varying(255)`, EF wraps it in
   `DbUpdateException`, and nothing in this slice catches it. Every limit below comes straight
   from the migration in §1; if the two ever disagree, §1 wins.

   | Field | Limit | Field | Limit |
   |---|---|---|---|
   | `TicketType.Code` | 100 | `FieldDescriptor.Key` | 100 |
   | `TicketType.DisplayName` | 255 | `FieldDescriptor.Label` | 255 |
   | `TicketType.Category` | 100 | `FieldDescriptor.GroupName` | 100 |
   | `TicketType.Description` | unlimited (`TEXT`) | `FieldDescriptor.HelpText` | unlimited (`TEXT`) |
   | | | `FieldDescriptor.RegexPattern` | 500 |
   | | | `FieldDescriptor.AllowedFileTypes` | 500 |
   | | | `FieldDescriptor.ConditionalVisibilityFieldKey` | 100 |
   | | | `FieldDescriptor.ConditionalVisibilityValue` | 500 |

   The `TEXT` columns have no length limit in PostgreSQL, so they need no check here — but
   they are still not unbounded input; cap them at the request-body size limit rather than
   per-field.

8. **`RegexPattern`, if non-empty, must compile.** Do it at validation time:

   ```csharp
   try { _ = new Regex(field.RegexPattern); }
   catch (ArgumentException)
   { throw new AppException(422, $"Field '{field.Key}' has an invalid regular expression."); }
   ```

   A pattern is client-supplied code that the **Tickets** slice will later execute against
   ticket values. Store one that has never been compiled and the failure appears in a
   different slice, on a different request, weeks later, as a `500` nobody can trace back to
   this create call. Compile it here, where the caller is still on the line to be told.

   Do not pass `RegexOptions.Compiled` and do not evaluate the pattern against any input at
   this point — you are checking that it parses, nothing more.

**G. Timestamps.** Take `DateTime.UtcNow` **once** at the top of the handler into a local and
reuse it, so a type and its version and fields share one creation instant. Always `UtcNow`,
never `Now`.

**H. One `SaveChangesAsync` per handler.** Build the whole object graph using navigation
properties, then save once. EF assigns the foreign keys. Multiple sequential saves leave a
type with no version if the second save fails.

**I. Audit after the save succeeds**, never before. `IAuditApi` is fire-and-forget
(`03-SliceInventory.md` §3.5) — do not branch on its result, and do not let an audit failure
roll back the write.

### 4.1 CreateTicketTypeHandler

**File:** `Application/Handlers/CreateTicketTypeHandler.cs`

Dependencies: `TicketTypesDbContext`, `IPermissionChecker`, `IAuditApi`.

```
Handle(CreateTicketTypeRequestDto req, CurrentUser user, CancellationToken ct):
  await permissions.RequireAsync(user, "CreateTicketType", ct: ct)        # 403 for CA / EMP

  if req.Code is blank: throw AppException(422, "Ticket type code is required.")
  ValidateFields(req.Fields)                            # §4.0 F

  # Codes are compared case-insensitively; "payroll" and "PAYROLL" are the same code.
  if await db.TicketTypes.AnyAsync(t => t.Code.ToLower() == req.Code.ToLower(), ct):
      throw AppException(409, "A Ticket Type with this code already exists")
      # 409, not 422: this is a uniqueness conflict (App/GeneralAppArchitecture.md §8)

  now = DateTime.UtcNow

  version = new TicketTypeVersion { VersionNumber = 1, CreatedAt = now }
  foreach field in req.Fields.OrderBy(f => f.DisplayOrder):
      version.FieldDescriptors.Add(MapToEntity(field, now))

  type = new TicketType {
      Code = req.Code, DisplayName = req.DisplayName, Description = req.Description,
      Category = req.Category, AllowEmployeeToOpen = req.AllowEmployeeToOpen,
      AllowSubjectOtherThanCreator = req.AllowSubjectOtherThanCreator,
      IsActive = true, VersionNumber = 1, CreatedAt = now, UpdatedAt = now
  }
  type.Versions.Add(version)                            # EF fills TicketTypeId / version ids

  db.TicketTypes.Add(type)
  await db.SaveChangesAsync(ct)                          # single save — §4.0 H

  await auditApi.LogAsync(new AuditEntry(
      Action: "CreateTicketType", TargetId: type.Id.ToString(),
      Actor: user.Id, Details: $"Created ticket type {type.Code} v1",
      OccurredAt: now), ct)

  return TicketTypeMapper.ToDetail(type, version, user.Role)
```

`MapToEntity` serialises `ChoiceOptions` with `System.Text.Json` (`"[]"` when absent) and
joins `AllowedFileTypes` with commas (`""` when absent). It is shared with §4.2 — write it
once in `TicketTypeMapper`.

Do **not** build the response from the request DTO. Map from the persisted entities, so the
response reflects what was actually stored and goes through the stripping in §4.0 D.

### 4.2 EditTicketTypeHandler

**File:** `Application/Handlers/EditTicketTypeHandler.cs`

Dependencies: `TicketTypesDbContext`, `IPermissionChecker`, `IAuditApi`.

This handler owns the versioning invariant. It is the highest-risk one in the slice.

```
Handle(EditTicketTypeRequestDto req, CurrentUser user, CancellationToken ct):
  await permissions.RequireAsync(user, "EditTicketType", ct: ct)
  ValidateFields(req.Fields)

  type = await db.TicketTypes.Include(t => t.Versions)
            .FirstOrDefaultAsync(t => t.Id == req.TicketTypeId, ct)
  if type is null: throw AppException(404, "Ticket type not found.")

  # Derive the next version from the versions table, NOT from type.VersionNumber.
  # If the two ever drift, trusting the denormalised counter collides with
  # UNIQUE(ticket_type_id, version_number) and surfaces as a 500.
  next = type.Versions.Max(v => v.VersionNumber) + 1

  now = DateTime.UtcNow

  newVersion = new TicketTypeVersion { VersionNumber = next, CreatedAt = now }
  foreach field in req.Fields.OrderBy(f => f.DisplayOrder):
      newVersion.FieldDescriptors.Add(MapToEntity(field, now))
  type.Versions.Add(newVersion)

  # Mutate only the type header. Never touch an existing version or its descriptors.
  type.DisplayName = req.DisplayName
  type.Description = req.Description
  type.Category = req.Category
  type.AllowEmployeeToOpen = req.AllowEmployeeToOpen
  type.AllowSubjectOtherThanCreator = req.AllowSubjectOtherThanCreator
  type.VersionNumber = next
  type.UpdatedAt = now                                   # must be maintained on every edit

  await db.SaveChangesAsync(ct)

  await auditApi.LogAsync(new AuditEntry(
      Action: "EditTicketType", TargetId: type.Id.ToString(),
      Actor: user.Id, Details: $"Updated ticket type {type.Code} to v{next}",
      OccurredAt: now), ct)

  return TicketTypeMapper.ToDetail(type, newVersion, user.Role)
```

**Key rule:** editing never mutates an old version. `Code` is never assigned here.

**Concurrent edits of the same type — do not solve, but do not return a `500` either.** Two
Accountants editing the same type at once both compute the same `next` version number, and one
`INSERT` loses on the unique constraint.

`01-DomainModel.md` §9.7 does **not** cover this. It settles optimistic concurrency for the
`tickets` row only, and explicitly says not to put a version column on other tables. So:

1. **Do not add a version column, a row lock, or a retry loop to `ticket_types`.** That is a
   design decision to raise, not to implement.
2. **Do catch the collision.** A losing `INSERT` raises `DbUpdateException` wrapping a
   `PostgresException` with `SqlState == "23505"`. Catch it and throw
   `AppException("This ticket type was edited by someone else. Reload and try again.", 409)`.
   Letting it escape produces a `ProblemDetails` `500`, and the locked rule is that anything a
   client can trigger by sending a request is a `4xx` — a `500` here would be a defect, not an
   accepted limitation.
3. The unhandled part is only that the loser has to redo their edit. That is acceptable in v1;
   an opaque `500` is not.

### 4.3 ToggleTicketTypeHandler

**File:** `Application/Handlers/ToggleTicketTypeHandler.cs`

Dependencies: `TicketTypesDbContext`, `IPermissionChecker`, `IAuditApi`.

```
Handle(ToggleTicketTypeRequestDto req, CurrentUser user, CancellationToken ct):
  await permissions.RequireAsync(user, "ToggleTicketType", ct: ct)

  type = await db.TicketTypes
            .Include(t => t.Versions).ThenInclude(v => v.FieldDescriptors)
            .FirstOrDefaultAsync(t => t.Id == req.TicketTypeId, ct)
  if type is null: throw AppException(404, "Ticket type not found.")

  if type.IsActive == req.NewIsActive:
      # Idempotent: return current state, write no audit entry for a no-op.
      return TicketTypeMapper.ToDetail(type, CurrentVersionOf(type), user.Role)

  now = DateTime.UtcNow
  type.IsActive = req.NewIsActive
  type.UpdatedAt = now
  await db.SaveChangesAsync(ct)

  await auditApi.LogAsync(new AuditEntry(
      Action: "ToggleTicketType", TargetId: type.Id.ToString(), Actor: user.Id,
      Details: $"Set IsActive to {req.NewIsActive} on {type.Code}",
      OccurredAt: now), ct)

  return TicketTypeMapper.ToDetail(type, CurrentVersionOf(type), user.Role)
```

`CurrentVersionOf(type)` = `type.Versions.OrderByDescending(v => v.VersionNumber).First()`.
Put it in `TicketTypeMapper`. Deactivating never deletes anything and never affects existing
Tickets that reference this type.

### 4.4 GetTicketTypeHandler

**File:** `Application/Handlers/GetTicketTypeHandler.cs`

Dependencies: `TicketTypesDbContext`, `IPermissionChecker`.

```
Handle(GetTicketTypeRequestDto req, CurrentUser user, CancellationToken ct):
  await permissions.RequireAsync(user, "ReadTicketType", ct: ct)

  type = await db.TicketTypes.AsNoTracking()
            .Include(t => t.Versions).ThenInclude(v => v.FieldDescriptors)
            .FirstOrDefaultAsync(t => t.Id == req.TicketTypeId, ct)
  if type is null: throw AppException(404, "Ticket type not found.")

  ApplyCustomerSideVisibility(type, user)                # §4.0 E — throws 404, not 403

  version = CurrentVersionOf(type)
  if version is null: throw AppException(500, "Ticket type has no version.")
  # 500, not 404: a type with no version is a broken invariant, not a missing resource.

  return TicketTypeMapper.ToDetail(type, version, user.Role)   # strips per §4.0 D
```

### 4.5 ListTicketTypesHandler

**File:** `Application/Handlers/ListTicketTypesHandler.cs`

Dependencies: `TicketTypesDbContext`, `IPermissionChecker`.

```
Handle(ListTicketTypesRequestDto req, CurrentUser user, CancellationToken ct):
  await permissions.RequireAsync(user, "ListTicketTypes", ct: ct)

  pageNumber = max(1, req.PageNumber)
  pageSize   = clamp(req.PageSize <= 0 ? 15 : req.PageSize, 1, 50)

  query = db.TicketTypes.AsNoTracking()

  if IsCustomerSide(user.Role):
      # Customer-side callers never see inactive types. ActiveOnly is ignored for them.
      query = query.Where(t => t.IsActive)
      if user.Role == UserRole.Employee:
          query = query.Where(t => t.AllowEmployeeToOpen)
  else:
      # Accountants see Active AND Inactive by default. Only narrow if asked.
      if req.ActiveOnly == true:  query = query.Where(t => t.IsActive)
      if req.ActiveOnly == false: query = query.Where(t => !t.IsActive)

  totalCount = await query.CountAsync(ct)
  items = await query.OrderBy(t => t.DisplayName).ThenBy(t => t.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .Select(t => new TicketTypeListItemDto { ... })
            .ToListAsync(ct)

  return new PaginatedResponse<TicketTypeListItemDto> {
      PageNumber = pageNumber, PageSize = pageSize, TotalCount = totalCount,
      TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
      Items = items
  }
```

Two traps:

- **Defaulting `ActiveOnly` to `true` for everyone is wrong.** An Accountant would never be
  able to find a type they just deactivated in order to reactivate it, and there would be no
  way to reach one.
- `OrderBy(DisplayName)` alone is not a stable sort. Two types with the same display name can
  appear on both page 1 and page 2, or on neither. Always add `.ThenBy(t => t.Id)`.

### 4.6 GetTicketTypeVersionHandler

**File:** `Application/Handlers/GetTicketTypeVersionHandler.cs`

Dependencies: `TicketTypesDbContext`, `IPermissionChecker`.

```
Handle(GetTicketTypeVersionRequestDto req, CurrentUser user, CancellationToken ct):
  await permissions.RequireAsync(user, "ReadTicketType", ct: ct)

  type = await db.TicketTypes.AsNoTracking()
            .Include(t => t.Versions).ThenInclude(v => v.FieldDescriptors)
            .FirstOrDefaultAsync(t => t.Id == req.TicketTypeId, ct)
  if type is null: throw AppException(404, "Ticket type not found.")

  ApplyCustomerSideVisibility(type, user)                # §4.0 E

  version = type.Versions.FirstOrDefault(v => v.VersionNumber == req.VersionNumber)
  if version is null: throw AppException(404, "Ticket type version not found.")

  return TicketTypeMapper.ToDetail(type, version, user.Role)
```

**This is critical for the Tickets slice:** a Ticket stores a reference to a specific
`TicketTypeVersion`. When rendering that Ticket, this handler returns the exact schema that
was in effect when the Ticket was created — which is why versions are immutable and why an
old version must remain readable after the type is deactivated.

---

## 5. ExternalInterface

**File:** `Slices/TicketTypes/ExternalInterfaces/ITicketTypesApi.cs`

```csharp
using AccountantApp.Api.Shared.Auth;

namespace AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;

public interface ITicketTypesApi
{
    /// <summary>Current version and fields, stripped for the caller's role. Null if not found.</summary>
    Task<TicketTypeDetailDto?> GetTicketTypeAsync(Guid ticketTypeId, UserRole callerRole, CancellationToken ct);

    /// <summary>A specific version by type + number, stripped for the caller's role. Null if not found.</summary>
    Task<TicketTypeDetailDto?> GetTicketTypeVersionAsync(Guid ticketTypeId, int versionNumber, UserRole callerRole, CancellationToken ct);

    /// <summary>A specific version by its OWN id. Null if not found. See the note below.</summary>
    Task<TicketTypeDetailDto?> GetVersionByIdAsync(Guid ticketTypeVersionId, UserRole callerRole, CancellationToken ct);

    /// <summary>Types the caller's role may open. Never includes inactive types for Customer-side roles.</summary>
    Task<List<TicketTypeListItemDto>> ListAvailableTypesAsync(UserRole callerRole, CancellationToken ct);
}
```

> **Amended 2026-09-02, and the previous version of this block was wrong in two ways** that the
> `Tickets` plan (§6.1, §13 item 1) caught before that slice was built.
>
> 1. **It had `using AccountantApp.Api.Slices.TicketTypes.Application.Dtos;`** — which made every
>    consuming slice violate dependency rule 2 simply by calling the contract. `TicketTypeDetailDto`,
>    `TicketTypeListItemDto` and the three types they expose transitively (`ChoiceOptionDto`,
>    `FieldValidationDto`, `ConditionalVisibilityDto`) now live in **`ExternalInterfaces/`**. See
>    `App/GeneralAppArchitecture.md` §3, whose directory table has been given a note stating the rule
>    generally. The `*RequestDto` types stayed in `Application/Dtos/`.
> 2. **It had no way to fetch a version by its own id.** `tickets.ticket_type_version_id` is a `Guid`,
>    so `GetTicketTypeVersionAsync`, which takes a version *number*, cannot resolve a ticket's frozen
>    descriptor set. `GetVersionByIdAsync` looks redundant beside it until you notice that — do not
>    "simplify" it away, and do not solve it by storing a version number on the ticket as well, which
>    gives one thing two references. It reuses the same `TicketTypeMapper` audience-filtering path as
>    the by-number accessor; a second projection is how the two would drift.
>
> Also note the `using` now sits **above** the `namespace`, matching every other file in the codebase.

Three things the previous version of this interface got wrong:

1. **`callerRole` is required on the two reads.** Without it the implementation cannot strip
   Accountant-only descriptors, so the cross-slice path leaks exactly what the HTTP path is
   required to hide. Every method that returns field descriptors takes the caller's role.
2. **Return `TicketTypeDetailDto?`, not `TicketTypeDetailDto`.** The implementation must use
   `FirstOrDefaultAsync` and return `null`. `FirstAsync` throws a raw
   `InvalidOperationException`, which the caller cannot distinguish from a bug and which
   surfaces as a 500 instead of a 404.
3. `UserRole` is a `Shared/Auth` type, not an `Identity` type. `TicketTypes` may depend only
   on `Audit` (`03-SliceInventory.md` §2) — taking a role parameter does not create a
   dependency on `Identity`, but importing anything from `Slices/Identity/` would.

**File:** `Slices/TicketTypes/ExternalInterfaces/TicketTypesApi.cs`

Inject `TicketTypesDbContext`. Reuse `TicketTypeMapper` (§4.0 D) — do not write a second
mapper here, or the two will drift and only one will strip.

---

## 6. Endpoints

**File:** `Slices/TicketTypes/TicketTypesEndpoints.cs`

Routes under `/api/ticket-types` — **hyphenated, not `tickettypes`.** The slice name is two
words, and multi-word path segments are kebab-case (`App/GeneralAppArchitecture.md` §8,
"Multi-word segments are kebab-case"). Concatenating them collides the two `t`s at the seam,
and `ticketypes` is a typo nobody sees while the resulting `404` looks like a missing row.

| Route | Handler | Authorization | Success |
|---|---|---|---|
| `POST /api/ticket-types/create` | `CreateTicketTypeHandler` | AccountantAdmin or AccountantUser | `201` |
| `POST /api/ticket-types/edit` | `EditTicketTypeHandler` | AccountantAdmin or AccountantUser | `200` |
| `POST /api/ticket-types/toggle` | `ToggleTicketTypeHandler` | AccountantAdmin or AccountantUser | `200` |
| `GET /api/ticket-types/list` | `ListTicketTypesHandler` | All roles, filtered | `200` |
| `GET /api/ticket-types/detail` | `GetTicketTypeHandler` | All roles, filtered | `200` |
| `GET /api/ticket-types/version` | `GetTicketTypeVersionHandler` | All roles, filtered | `200` |

Endpoint rules:

- **Take the handler as a parameter**, injected by DI. Never `new` it.
- `CurrentUser` is a registered scoped service (§0.1), so it binds from services.
- Authorization is the handler's job, not the endpoint's. Do not duplicate the check.
- `create` returns `Results.Created($"/api/ticket-types/detail?ticketTypeId={result.Id}", result)`.
- Do not write a `try/catch` per endpoint. `AppException` → `ProblemDetails` is a single
  shared exception-handling middleware (`03-SliceInventory.md` §4), which must also map the
  status code from `AppException.StatusCode` rather than hardcoding 403.

```csharp
public static void MapTicketTypesEndpoints(this IEndpointRouteBuilder app)
{
    var g = app.MapGroup("/api/ticket-types").WithTags("TicketTypes");

    g.MapPost("/create", async (
            CreateTicketTypeRequestDto req,
            CreateTicketTypeHandler handler,
            CurrentUser user,
            CancellationToken ct) =>
        {
            var result = await handler.Handle(req, user, ct);
            return Results.Created($"/api/ticket-types/detail?ticketTypeId={result.Id}", result);
        })
        .WithName("CreateTicketType")
        .Produces<TicketTypeDetailDto>(201)
        .Produces<ProblemDetails>(403)
        .Produces<ProblemDetails>(409)
        .Produces<ProblemDetails>(422);

    g.MapGet("/list", async (
            int? pageNumber, int? pageSize, bool? activeOnly,
            ListTicketTypesHandler handler,
            CurrentUser user,
            CancellationToken ct) =>
        Results.Ok(await handler.Handle(new ListTicketTypesRequestDto
        {
            PageNumber = pageNumber ?? 1,
            PageSize = pageSize ?? 15,
            ActiveOnly = activeOnly
        }, user, ct)))
        .WithName("ListTicketTypes")
        .Produces<PaginatedResponse<TicketTypeListItemDto>>(200);

    // edit, toggle, detail, version follow the same shape.
}
```

Query parameters are `int?`/`bool?`, not `int`/`bool`. A non-nullable `int pageNumber` makes
the parameter **required**, so `GET /api/ticket-types/list` with no query string returns 400
instead of the first page.

---

## 7. Service registration

### 7.1 `Slices/TicketTypes/TicketTypesRegistration.cs` — write this file

Everything this slice owns is registered here, not in `Program.cs`
(`App/GeneralAppArchitecture.md` §7, *Slice registration*). Create the file exactly:

```csharp
namespace Slices.TicketTypes;

public static class TicketTypesRegistration
{
    public static IServiceCollection AddTicketTypesSlice(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddDbContext<TicketTypesDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Default")));

        services.AddTransient<CreateTicketTypeHandler>();
        services.AddTransient<EditTicketTypeHandler>();
        services.AddTransient<ToggleTicketTypeHandler>();
        services.AddTransient<GetTicketTypeHandler>();
        services.AddTransient<ListTicketTypesHandler>();
        services.AddTransient<GetTicketTypeVersionHandler>();

        services.AddScoped<ITicketTypesApi, TicketTypesApi>();

        return services;
    }
}
```

All six handlers from §4 and the `ITicketTypesApi` from §5 appear in that list. If you add a
seventh handler later, it gets a line here in the same commit.

**Do not register `IAuditApi`, `CurrentUser`, or `IPermissionChecker` in this file.** `Audit`
owns its own registration; the other two belong to `Shared/` and are `Program.cs`'s job.

### 7.2 What `Program.cs` adds

Two lines for this slice, plus the prerequisites from §0 and the `Audit` slice:

```csharp
// Shared — once for the whole app (§0.1, §0.2)
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>(/* see §0.1 */);
builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();

// Audit — TicketTypes will not start without it (trap 3 below)
builder.Services.AddAuditSlice(builder.Configuration);

// This slice
builder.Services.AddTicketTypesSlice(builder.Configuration);

var app = builder.Build();
// ... middleware ...
app.MapTicketTypesEndpoints();
```

If you find yourself typing `AddTransient<CreateTicketTypeHandler>()` or
`AddDbContext<TicketTypesDbContext>` into `Program.cs`, that line belongs in §7.1 instead.

### 7.3 Five registration traps

1. **`AddDbContext<TicketTypesDbContext>(o => o.UseNpgsql(cs))` — not
   `AddScoped<TicketTypesDbContext>()`.** The context's only constructor takes
   `DbContextOptions<TicketTypesDbContext>`; a bare `AddScoped` supplies no provider. Do not
   write both — the later registration wins and silently discards the configured options.
2. **The provider is Npgsql.** `Microsoft.EntityFrameworkCore.InMemory` must not appear in
   `AccountantApp.Api.csproj`. PostgreSQL is a locked decision, and the in-memory provider
   hides the entire class of column-mapping and constraint bugs described in §2.0. Reference
   `Npgsql.EntityFrameworkCore.PostgreSQL`. In-memory belongs only in the test project.
3. **`IAuditApi` must have a registered implementation.** Every write handler injects it. With
   `ValidateOnBuild` (the default in Development) an unregistered `IAuditApi` makes
   `builder.Build()` throw and **the application will not start at all** — no endpoint is
   reachable. If the `Audit` slice is not built yet, build it first; do not leave the
   interface unregistered.
4. **Register every handler — you `new` none of them.** After §6, handlers are only ever
   resolved from DI, so a missing `AddTransient` is a startup failure rather than dead code —
   which is what you want. Cross-check the list in §7.1 against §4.1–4.6 line by line.
5. **`AddTicketTypesSlice` must actually be called.** A registration file that nothing invokes
   compiles cleanly and registers nothing. Confirm the call is in `Program.cs` before the
   smoke check below.

### Startup smoke check — do this before writing tests

```
docker compose up -d db          # the runner needs a database; see §8
dotnet run
curl -i "http://localhost:5000/api/ticket-types/list" -H "X-Dev-Role: AccountantAdmin"
```

A build that succeeds proves nothing about whether the app runs. `dotnet build` cannot catch
an unregistered service, an unbindable endpoint parameter, or a column-name mismatch. If the
process exits during `Build()`, or any endpoint returns
`500 Failure to infer one or more parameters`, stop and fix it before continuing.

**The header is required, and a `401` is a failed smoke check.** `Identity` is not built yet,
so nothing sets `HttpContext.User` and `CurrentUserFactory.FromPrincipal` throws
`AppException(401)` for every caller. `401` means the request stopped in the `CurrentUser`
factory and **never reached the endpoint, the handler, the permission checker, or the
database** — it is indistinguishable from a completely unimplemented slice. Enable the
development-only principal described in `App/GeneralAppArchitecture.md` §9 ("The
development-only test principal") and pass `X-Dev-Role`; expect `200` with an empty list.

Then repeat the check as each role and confirm the matrix, which is the part no unit test that
constructs a handler directly can confirm:

```
curl -i .../api/ticket-types/list   -H "X-Dev-Role: Employee" -H "X-Dev-Customer-Id: <guid>"
curl -i -X POST .../api/ticket-types/create -H "X-Dev-Role: Employee" ... # expect 403
```

If the process exits at `SqlMigrationRunner` with
`Npgsql.NpgsqlException: Failed to connect to [::1]:5432`, the database is not running. Start
it. Do **not** comment out the migration call to get the app to boot: the schema would then
never be applied, every handler would fail on its first query, and the smoke check would be
measuring nothing.

---

## 8. Migrations — SQL scripts, not `dotnet ef`

**There are no EF Core migrations in this project.** Never run `dotnet ef migrations add` or
`dotnet ef database update`. The locked strategy (`03-SliceInventory.md` §5,
`App/GeneralAppArchitecture.md` §6) is raw SQL scripts per slice, applied by a runner.

`Shared/Migrations/SqlMigrationRunner.cs`, invoked from `Program.cs` after `app.Build()`:

1. Ensure the tracking table exists:
   ```sql
   CREATE TABLE IF NOT EXISTS schema_versions (
       script_name VARCHAR(500) PRIMARY KEY,
       applied_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
   );
   ```
2. Enumerate `Slices/**/Infrastructure/Migrations/*.sql`.
3. Sort by **filename**, with the slice name as tiebreaker, so dates order changes across
   slices deterministically.
4. Skip any script whose **slice-relative path** is already present in `schema_versions`.
5. Execute each remaining script and insert that same path into `schema_versions` **inside one
   transaction per script**, so a failure leaves neither a half-applied script nor a false
   tracking row.

**The tracking key is the slice-relative path, not the bare filename.** Store
`TicketTypes/Infrastructure/Migrations/20260829_001_CreateTicketTypesSchema.sql`, with forward
slashes so a script applied on a Windows dev machine is not re-applied in the Linux container.

Do **not** write `var name = Path.GetFileName(script);`. Sequence numbers restart at `001` in
every slice, so the moment a second slice ships a `20260829_001_*.sql` with the same
description — or `20260829_002_AddIndex.sql`, which two slices will pick independently — the
runner records the first, sees the name already present, and skips the second **silently**.
The result is a missing table, no error at startup, and a `42P01: relation does not exist` on
the first query into whichever slice lost the race. Widen the column to `VARCHAR(500)` for the
same reason: `VARCHAR(255)` fits a filename but truncates a path, and a truncated key
collides.

Scripts must be shipped with the assembly — mark them `<EmbeddedResource>` or
`CopyToOutputDirectory`, or the runner finds nothing in a published container.

Migrations are append-only. No rollback scripts. A mistake is fixed by a new script.

---

## 9. Tests

Write these in `AccountantApp.Tests/TicketTypes/`. The in-memory EF provider is acceptable
**here only**. Note what it cannot prove: it ignores `HasColumnName`, unique constraints, and
`NOT NULL`, so a green suite says nothing about §2.0.

### 9.1 At least one test must run against real PostgreSQL — mandatory

An all-in-memory suite for this slice is **not** an acceptable deliverable, however many cases
it covers. Everything §1 and §2.0 exist to get right is invisible to the in-memory provider:
a missing `HasColumnName` (EF would query `"DisplayName"` against a `display_name` column),
the case-insensitive unique index on `code`, `NOT NULL`, `TIMESTAMPTZ` versus `TIMESTAMP`.
A suite that is green on all of those is not evidence; it is the absence of evidence.

Write `AccountantApp.Tests/TicketTypes/TicketTypesSchemaTests.cs` with **one** test that:

1. Connects to a real PostgreSQL 16 (`docker compose up db`, or Testcontainers if the test
   project already uses it — do not add the dependency just for this).
2. Runs `SqlMigrationRunner` against a scratch database, proving §1 is valid SQL that
   actually applies.
3. Creates one type with one field through `CreateTicketTypeHandler`, reads it back through
   `GetTicketTypeHandler`, and asserts the round-trip. This is what catches a column-name
   mismatch — the write and the read must agree with the DDL, not merely with each other.
4. Inserts a second type whose `Code` differs from the first **only in case** with raw SQL,
   bypassing the handler, and asserts PostgreSQL rejects it. This is the only way to verify
   the `LOWER(code)` index; the handler's own pre-check would mask it.

Skip it with a clear message — not a silent pass, and not a failure — when no database is
reachable, so a developer without Docker running is told what was not verified:

```csharp
Skip.If(!await PostgresAvailable(), "No PostgreSQL at localhost:5432; schema not verified.");
```

Report it plainly if you could not run this test. "The build succeeds and 14 tests pass" while
this one never executed means the schema has never been applied to a database, and it is the
schema that everything else rests on.

### 9.2 Behavioural cases (in-memory is acceptable)

- [ ] Create a type with 5 fields; v1 exists with 5 descriptors
- [ ] Edit the type (change 2 fields); v2 exists **and v1's descriptors are byte-identical to before**
- [ ] `GetTicketTypeVersion(v1)` returns the original fields; `(v2)` returns the edited ones; the two differ
- [ ] List as Accountant: returns Active **and** Inactive types
- [ ] List as Employee: only Active types with `AllowEmployeeToOpen == true`
- [ ] Toggle inactive: the type disappears from an Employee's list but is still readable by version number
- [ ] Conditional visibility round-trips; a rule naming a non-existent field is rejected with 422
- [ ] A `SingleChoice` field with 3 options round-trips through JSON
- [ ] Duplicate `Code` (including differing only in case) → 409
- [ ] Duplicate field `Key` within one request → 422
- [ ] Unknown `DataType` → 422
- [ ] **A field with `IsVisibleToCustomer == false` is absent from the response to an Employee and to a CustomerAdmin, and present for both Accountant roles** — on `detail`, on `version`, through `ITicketTypesApi.GetTicketTypeVersionAsync`, **and through `ITicketTypesApi.GetVersionByIdAsync`** (the fourth path, added 2026-09-02 — §5). The by-id and by-number strips must produce the **same** field set for the same version; assert that equality rather than the two independently, or a future second projection can drift one without failing anything
- [ ] Create/edit/toggle as `CustomerAdmin` → 403; as `Employee` → 403
- [ ] Every write emits exactly one audit entry; a permission denial also emits one
- [ ] `detail` on a deactivated type as an Employee → 404 (not 403, not 200)

---

## 10. Known constraints

1. **`TicketType.Code` is immutable.** Once created it never changes.

2. **Nothing in this slice is ever deleted — by anyone.**
   `02-AuthorizationMatrix.md` §5: *"Delete a Ticket Type or a version | **Nobody.** Existing
   tickets depend on them."* There is no delete endpoint, no soft-delete flag, and no
   "delete if unused" path. A type created by mistake is deactivated with `toggle`; a correct
   one is created alongside it. Do not add a delete operation even if it looks harmless
   because no Ticket references the type yet.

3. **Versions are never edited.** A wrong descriptor in v1 is fixed by creating v2.

4. **Backward compatibility.** A Ticket created against v1 must render correctly forever,
   after v2 and v3 exist and after the type is deactivated. This is why reads by version
   number ignore `IsActive` for Accountants.

5. **No approval workflow.** An Accountant User can change the catalogue immediately and it
   applies to every Customer. By design (`02-AuthorizationMatrix.md` §5) — do not add an
   approval step or restrict authoring to Accountant Admin.

6. **`ChoiceOptions` is a JSON string column.** Serialize on write, deserialize on read.
   Treat a malformed value as an empty list rather than throwing — it would make a type
   permanently unreadable.

---

## 11. Questions to flag if unclear

- [ ] Should a version-history endpoint exist, listing all versions of a type with their
      dates? Currently no — only fetch-by-version-number. The Accountant UI probably needs it.
- [ ] Should `TicketType` record who created it and who last edited it? Currently no; the
      information exists only in the audit log.
- [ ] Should default Ticket Types be seeded on first startup? Currently no.
- [ ] Concurrent edits to one type (§4.2) — unresolved, `03-SliceInventory.md` §6 question 4.

---

## Files checklist

Prerequisites (§0) — build first if missing:

- [ ] `Shared/Auth/UserRole.cs`, `Shared/Auth/CurrentUser.cs`, `Shared/Auth/CurrentUserFactory.cs`
- [ ] `Shared/Authorization/IPermissionChecker.cs` + `PermissionChecker.cs` (fail-closed)
- [ ] `Shared/Errors/AppException.cs` + exception-handling middleware
- [ ] `Shared/Pagination/PaginatedResponse.cs`
- [ ] `Shared/Migrations/SqlMigrationRunner.cs` — keyed on the **slice-relative path** (§8)
- [ ] `Shared/Auth/DevAuthHandler.cs` + `DevAuth:Enabled` in `appsettings.Development.json` only
- [ ] `Slices/Audit/ExternalInterfaces/IAuditApi.cs` + `AuditApi.cs` (**not** in `Shared/`)

This slice:

- [ ] `Slices/TicketTypes/Infrastructure/Migrations/20260829_001_CreateTicketTypesSchema.sql`
- [ ] `Core/TicketType.cs`, `Core/TicketTypeVersion.cs`, `Core/FieldDescriptor.cs`
- [ ] `Infrastructure/TicketTypesDbContext.cs`
- [ ] `Infrastructure/Configurations/` — three configurations, every property with `HasColumnName`
- [ ] `Application/Dtos/` — 6 files, the REQUEST shapes only (§3). The two response shapes moved to
      `ExternalInterfaces/`; see the entry below and §3.
- [ ] `Application/TicketTypeMapper.cs` (the single stripping mapper)
- [ ] `Application/Handlers/` — 6 handlers
- [ ] `ExternalInterfaces/ITicketTypesApi.cs` + `TicketTypesApi.cs`
- [ ] `ExternalInterfaces/TicketTypeDetailDto.cs` + `TicketTypeListItemDto.cs` — the contract RESPONSE
      shapes, in `ExternalInterfaces/` and not `Application/Dtos/`, because `ITicketTypesApi` returns
      them across the slice boundary. `Application/Dtos/` keeps the REQUEST shapes only.
- [ ] `ExternalInterfaces/FieldDataTypes.cs` — the eleven data type constants plus `All` (§1)
- [ ] `Shared/Validation/UserSuppliedRegex.cs` — the one regex match timeout, shared with `Tickets`
      (correction note T-16). Not owned by this slice; created by whichever slice needs it first.
- [ ] `TicketTypesEndpoints.cs`
- [ ] `TicketTypesRegistration.cs` — DbContext with Npgsql, 6 handlers, `ITicketTypesApi` (§7.1)
- [ ] `Slices/Audit/AuditRegistration.cs` — exposes `AddAuditSlice`, binds `IAuditApi` (§0)
- [ ] `Program.cs` — Shared services, `AddAuditSlice()`, `AddTicketTypesSlice()`, `MapTicketTypesEndpoints()`, and **no handler or DbContext type named directly** (§7.2)
- [ ] `AccountantApp.Api.csproj` — `Npgsql.EntityFrameworkCore.PostgreSQL`, **no** `.InMemory`
- [ ] Startup smoke check passes (§7) — as `AccountantAdmin`, `CustomerAdmin`, and `Employee`, with no `401`
- [ ] `AccountantApp.Tests/TicketTypes/TicketTypesSchemaTests.cs` — the PostgreSQL-backed test (§9.1)
- [ ] Tests in §9.2 pass

---

## Success criteria

Each is verified by running the app, not by reading the code.

**Every check below is made with an authenticated caller** — the `X-Dev-Role` header of §0 and
`App/GeneralAppArchitecture.md` §9. A `401` means the request never reached the slice, so it
satisfies nothing here; do not record a criterion as met on a `401`.

1. `dotnet run` starts and stays up with the migration runner **enabled**, and
   `GET /api/ticket-types/list` returns `200` with an empty page.
2. `POST /create` returns `201` and stores the type with version 1.
3. `POST /edit` returns `200`, creates version 2, and leaves version 1's descriptors unchanged.
4. `GET /version?ticketTypeId=X&versionNumber=1` returns the original fields; `versionNumber=2` returns the edited ones.
5. `GET /list` returns a `PaginatedResponse` with a correct `TotalPages`; Accountants see inactive types, Employees do not.
6. All validation rules (lengths, ranges, regex, file types, choice options) round-trip intact.
7. Conditional visibility rules round-trip; dangling references are rejected at write time.
8. **A field with `IsVisibleToCustomer == false` never appears in any response to a
   `CustomerAdmin` or `Employee`, through any endpoint or through `ITicketTypesApi`.**
9. Every write emits exactly one audit entry; every permission denial emits one.
10. `CustomerAdmin` and `Employee` receive `403` on create/edit/toggle, and `404` on `detail`
    for a deactivated type.
11. The schema in the running PostgreSQL database matches §1, applied by the SQL runner, with
    a row in `schema_versions` holding the **slice-relative path**, not a bare filename.
    Two codes differing only in case cannot both be inserted, even by raw SQL (§9.1).
12. `Program.cs` contains **no** `TicketTypes` type name other than `AddTicketTypesSlice()` and
    `MapTicketTypesEndpoints()`. Everything else the slice needs is registered inside
    `TicketTypesRegistration.cs`.
13. An over-length `DisplayName`, `Label`, or `GroupName`, and an unparseable `RegexPattern`,
    each return `422` — never `500` (§4.0 rule F).
14. An exception the code does not anticipate returns a `ProblemDetails` `500` with a
    `traceId` and no stack trace, and a mistyped route under `/api` returns a
    `ProblemDetails` `404` rather than an empty body or `index.html`
    (`App/GeneralAppArchitecture.md` §8).

---

# Correction Notes — review of 2026-09-01

Written after validating the working-tree implementation against this plan and documents 0–5.
**These are corrections to this plan and to the numbered documents, recorded so the next build
cycle does not repeat the same guesses.** Each finding says whether the fault is in the
IMPLEMENTATION, the SPEC, or both.

State at review: `dotnet build` = 0 errors, 0 warnings. `dotnet test` = 27 passed, 0 failed,
**2 skipped** — one of the skips is this slice's mandatory PostgreSQL test (T-10).

Much of this slice is right, and was checked rather than assumed. §4.0 D — the one rule whose
failure leaks Accountant-only field labels to every Employee — is implemented correctly and in
exactly one place (`TicketTypeMapper.ToDetail`), and every read path goes through it. §4.0 E returns
`404` rather than `403` for a Customer-side caller on an inactive or out-of-audience type. §4.0 F is
implemented in full, all eight sub-rules including the `VARCHAR` table and the `Regex` compile
check, and `TicketTypeMapper` cites the migration as the source of each limit. §4.0 G takes one
`DateTime.UtcNow` per handler, §4.0 H saves once, editing never mutates an old version, `Code` is
never reassigned on edit, and the action catalogue matches 02-AuthorizationMatrix §5 cell for cell
(create/edit/toggle `[AA, AU]`, read/list all four). `Program.cs` names only
`AddTicketTypesSlice()` and `MapTicketTypesEndpoints()`, satisfying success criterion 12. The
findings below are the exceptions.

## T-1 (BLOCKER, both) — no mutating handler opens a transaction, and this plan is what told it not to

App/GeneralAppArchitecture §5, LOCKED: *"**The rule: a mutation and its audit entry commit together
or not at all.** If the audit write fails, the mutation rolls back and the caller receives a
`500`."*

§4.0 rule I of this plan says the opposite: *"`IAuditApi` is fire-and-forget
(`03-SliceInventory.md` §3.5) — do not branch on its result, **and do not let an audit failure roll
back the write**."*

The implementation followed this plan. `CreateTicketTypeHandler.cs:57-62`,
`EditTicketTypeHandler.cs:51-56` and `ToggleTicketTypeHandler.cs:39-44` all do
`await _db.SaveChangesAsync(ct)` and *then* `await _auditApi.LogAsync(...)`, with no
`IRequestTransaction` injected anywhere in the slice — verified by grep: `BeginAsync` appears only
in the five `Customers` handlers. `RequestTransaction.EnlistAsync` returns immediately when no
transaction is open, so `AuditApi` cannot detect the omission and downgrades silently to a second
independent commit. A failing audit write therefore leaves a **committed, unaudited mutation** —
precisely what 01-DomainModel §8 exists to prevent, and what App §5 is LOCKED to prevent.

Per README precedence a per-slice plan under `Slices/` loses to every numbered document, so rule I
is simply wrong. It is wrong because it inherited the ambiguity recorded as A-3 in the Audit plan:
04-Infrastructure §6 line 364 calls Audit *"fire-and-forget from callers, so a silent failure
destroys the record with nothing else breaking"*, and 03-SliceInventory rule 5 uses the same phrase.
Rule I is the third document in the repo to read "fire-and-forget" as "swallow failures". **Fixing
04 §6 and 03 rule 5 (see Audit A-3) is the correction that prevents this recurring in Tickets,
Documents and Employees.**

Correction, in this plan: replace §4.0 rule I with — *"Open `IRequestTransaction.BeginAsync` before
the read, `SaveChangesAsync`, then `LogAsync`, then `CommitAsync`. Fire-and-forget means no handler
**branches on** the audit result; it does not mean an audit failure is swallowed. If `LogAsync`
throws, the transaction is not committed and the caller gets a `500` (App §5)."* Correction, in the
code: inject `IRequestTransaction` into the three mutating handlers and wrap read-through-audit in
one transaction, as the `Customers` handlers already do.

## T-2 (BLOCKER, implementation) — the mandated 23505 → 409 catch was never written, so a concurrent edit returns 500

§4.2 rule 2 is explicit and gives the code: *"A losing `INSERT` raises `DbUpdateException` wrapping
a `PostgresException` with `SqlState == "23505"`. Catch it and throw `AppException("This ticket type
was edited by someone else. Reload and try again.", 409)`. Letting it escape produces a
`ProblemDetails` `500`, and the locked rule is that anything a client can trigger by sending a
request is a `4xx` — **a `500` here would be a defect, not an accepted limitation**."*

`EditTicketTypeHandler.cs` contains no `try`, no `catch`, and no reference to `DbUpdateException` or
`PostgresException`. Two Accountants editing one type concurrently both compute the same `next` from
`type.Versions.Max(...)` at line 36; the loser's insert violates
`UNIQUE(ticket_type_id, version_number)` and returns an opaque 500. The plan anticipated this
exactly and the mitigation was skipped.

The same gap exists on the create path, and §1 (lines 140-143) already names the race: *"two callers
racing on the same code in different casing both pass the handler's pre-check and both insert."*
`CreateTicketTypeHandler.cs:33` does the `AnyAsync` pre-check and line 57 saves with no catch, so
the loser of that race also gets a 500 where §4.1 promises 409. **Note this is currently masked:
because the functional unique index is only verified by the skipped PostgreSQL test (T-10), the
in-memory suite cannot reach either path.**

Correction: add the `catch (DbUpdateException exception) when (exception.InnerException is
PostgresException { SqlState: "23505" })` block to **both** write handlers — 409 in each case — and
extend §4.1 to require it on create, not only §4.2 on edit.

## T-3 (BLOCKER, implementation) — no test exercises the shipped action catalogue, and the one they do use is missing three actions

All eleven `PermissionChecker` constructions in this slice's tests — `TicketTypesFlowTests.cs` lines
26, 120, 185, 224, 238, 264, 281, 308, 342, 368, 386, 403 and `TicketTypesSchemaTests.cs:60` — use
the two-argument overload `new PermissionChecker(audit, NullLogger<PermissionChecker>.Instance)`.
That overload injects `LegacyTicketTypesCatalogue`, the copy of this slice's action names that still
lives inside `Shared/Authorization/PermissionChecker.cs:71` (recorded as A-5 in the Audit plan).

Two consequences. First, `TicketTypesActionCatalogue` — the class `TicketTypesRegistration.cs:19`
actually registers and production actually uses — is **never instantiated by any test**. Every
`403` assertion in this slice proves only that the duplicate table in `Shared/` is self-consistent;
adding `CustomerAdmin` to `CreateTicketType` in the real catalogue leaves the suite green. Success
criterion 9's *"every permission denial emits one [audit entry]"* is recorded as met against the
wrong table.

Second, that legacy catalogue contains **only** this slice's five actions, so under it
`ReadAuditLog`, `CreateCustomer` and `SuspendCustomer` do not exist — and an unrecognised action
DENIES. Any test or future caller reaching for the two-argument constructor gets a checker that
denies an `AccountantAdmin` the audit log *and writes the denial to the audit log as if it were
legitimate*.

Correction: delete `LegacyTicketTypesCatalogue` and the two-argument constructor (Audit A-5), then
fix all eleven call sites to pass `new TicketTypesActionCatalogue()`. Add to §9 the rule that a test
may not define its own `IActionCatalogue` — the production fragment is the thing under test — plus
one test asserting its action→roles map equals the 02-AuthorizationMatrix §5 table literally.

## T-4 (MAJOR, spec) — the plan requires deactivated versions to stay readable and also requires them to 404

Two rules of this plan cannot both hold for a Customer-side caller.

- §4.0 E: for `CustomerAdmin` and `Employee`, `GetTicketTypeHandler` **and
  `GetTicketTypeVersionHandler`** must throw `AppException(404)` when the type is
  `IsActive == false`.
- §4.6: *"a Ticket stores a reference to a specific `TicketTypeVersion`… which is why versions are
  immutable and **why an old version must remain readable after the type is deactivated**."*
- §9.2: *"Toggle inactive: the type disappears from an Employee's list **but is still readable by
  version number**"* — while success criterion 10 restricts the Customer-side `404` to `detail`
  only, saying nothing about `version`.

The implementation follows E: `GetTicketTypeVersionHandler.cs:29` calls
`ApplyCustomerSideVisibility`, which throws 404 on `!type.IsActive` for Customer-side roles. So a
`CustomerAdmin` viewing their own historical ticket cannot fetch the schema that ticket was created
under, once the type is deactivated. That is a functional break in the **Tickets** slice, which
§4.6 identifies as this handler's only real consumer — and Tickets is not yet built, so it will be
discovered as a rendering bug there rather than as a defect here.

02-AuthorizationMatrix §5 does not settle it: *"'Filtered' means: only `Active` types, and only
types whose audience permits the caller's role"* is written about *"List Ticket Types available to
open"* — the discovery path — whereas reading the version of a type you already have a ticket for is
not discovery.

Correction: state in §4.0 E that the `IsActive` check applies to the **discovery** paths (`list`,
`detail`) and **not** to `version`, which must remain readable by version number to any role
entitled to read the type at all; the audience check (`AllowEmployeeToOpen`) still applies to
`version`. Then make §9.2's row and criterion 10 say which endpoints they cover, and add a case for
a Customer-side `version` read of a deactivated type returning 200.

## T-5 (MAJOR, spec) — every audit call in this plan is against an `AuditEntry` that no longer exists

§4.1, §4.2 and §4.3 all specify the call as:

```
await auditApi.LogAsync(new AuditEntry(
    Action: "CreateTicketType", TargetId: type.Id.ToString(),
    Actor: user.Id, Details: $"Created ticket type {type.Code} v1",
    OccurredAt: now), ct)
```

The shipped record (`Slices/Audit/ExternalInterfaces/IAuditApi.cs:3-10`) is
`AuditEntry(string Action, string TargetKind, string TargetId, Guid? CustomerId, string Outcome,
object? Before, object? After)`. There is no `Actor`, no `Details` and no `OccurredAt`, and
`TargetKind` is required. Transcribed literally, this plan **does not compile**, and its `Actor:
user.Id` would violate the Audit plan's §5.1/§12.3 rule that the actor is never caller-supplied.

The implementation uses the real shape and is correct. But the action *names* drifted with it: the
plan says `"CreateTicketType"`, `"EditTicketType"`, `"ToggleTicketType"`, while the code writes
`AuditActions.TicketTypeCreated`, `TicketTypeVersionCreated`, and
`TicketTypeActivated`/`TicketTypeDeactivated` (a distinction the plan does not have at all).
`AuditApi.AppendAsync` validates `Action` against `AuditActions.All`, so the plan's spellings would
be **rejected at runtime** even if they compiled.

Correction: rewrite the three audit calls in this plan against the real record, using
`AuditTargets.TicketType`, `Before`/`After` snapshots instead of a `Details` string, no `Actor`, and
the four `AuditActions` constants the code actually uses. Note in §4.3 that activate and deactivate
are distinct action names.

## T-6 (MAJOR, implementation) — the 25/200 pagination drift recorded in §0.3 is still unfixed

§0.3 already carries the correction as a blockquote: *"**The shipped code does not yet match this.**
These numbers were changed system-wide from 25/200 to 15/50… Three files still carry the old
literals."* This note records that as of this review **none of the three has been changed**:

- `Application/Dtos/ListTicketTypesRequestDto.cs` — still `= 25`
- `Application/Handlers/ListTicketTypesHandler.cs:26` — still
  `Math.Clamp(req.PageSize <= 0 ? 25 : req.PageSize, 1, 200)`
- `TicketTypesEndpoints.cs:49` — still `pageSize ?? 25`

App §8 states why the ceiling matters: it *"is the ceiling on how much data one request can extract,
so it is a **security control** as much as a performance one."* 200 is four times it. The identical
pair also survives in `ListCustomersHandler.cs:31` (recorded as C-3 there), so this is repo-wide
drift with a common root: `Shared/Pagination/` ships only `PaginatedResponse<T>` and no
`PaginatedQuery`, so every slice re-types the two numbers by hand (Customers C-8).

Correction: fix the three files, and prefer routing them through a shared
`PaginatedQuery.Normalise()` with `DefaultPageSize = 15` / `MaxPageSize = 50` so the literals exist
once. Add a §9.2 case asserting `pageSize = 5000` comes back as 50.

## T-7 (MAJOR, both) — `Code` is never trimmed, which defeats the case-insensitive uniqueness guarantee of §1

§1 goes to some length to make uniqueness case-insensitive, because *"the database and the handler
must agree on what 'already exists' means."* Whitespace breaks that agreement just as casing would.

`CreateTicketTypeHandler.cs:43` assigns `Code = req.Code` verbatim. Line 28 only rejects
`IsNullOrWhiteSpace`. So `"payroll"` and `" payroll"` both pass the `AnyAsync` pre-check at line 33
and both satisfy the functional index on `LOWER(code)` — two ticket types with the same code as far
as any human or any downstream `Code` lookup is concerned. The same applies to `DisplayName` and
`Category`. The `Customers` slice trims its string inputs; this slice does not, and neither
behaviour is specified.

Correction: trim `Code`, `DisplayName` and `Category` before validating and before the uniqueness
check, and add to §4.0 F a rule 0 — *"every string in the request is `Trim()`med first; the trimmed
value is what is validated, compared and stored."* Add a §9.2 case for `" payroll"` colliding with
an existing `"PAYROLL"` → 409.

## T-8 (MINOR, both) — a ticket type with a blank display name is accepted

§4.0 F validates lengths, ranges, regex and choice options, but never requires a non-blank
`DisplayName` or `Category`, and §4.1 requires non-blank only for `Code`. The implementation matches:
`ValidateTicketType` calls `RequireLength` three times, which returns without complaint for `""`.
So `POST /create` with `{"code":"X","displayName":"","category":""}` returns 201, and the type then
sorts first in every `list` response (`OrderBy(t => t.DisplayName)`) with nothing to click on.

Related, and worth fixing at the same time: `EditTicketTypeHandler.cs:29` calls
`TicketTypeMapper.ValidateTicketType(string.Empty, req.DisplayName, req.Category)`, passing a dummy
value for the immutable `Code` and relying on `RequireLength` ignoring it. It works, but the
signature invites a future caller to pass `req.Code` and silently re-validate a field that cannot
change.

Correction: add non-blank checks for `DisplayName` and `Category` to §4.0 F, and split
`ValidateTicketType` so the edit path does not pass a placeholder.

## T-9 (MINOR, implementation) — the version read loads every version and every descriptor to return one

`GetTicketTypeVersionHandler.cs:24-25` and `ToggleTicketTypeHandler.cs:27` both do
`.Include(t => t.Versions).ThenInclude(v => v.FieldDescriptors)` and then select a single version in
memory. Cost grows linearly with the number of times the type has ever been edited, and §4.6 marks
this handler as the hot path for rendering every historical ticket in the Tickets slice. The plan's
own pseudocode has the same shape, so this is a plan defect the code inherited.

Correction: filter the include to the requested version (`.Include(t => t.Versions.Where(v => v.VersionNumber == req.VersionNumber))`)
and update the §4.6 pseudocode. `ToggleTicketTypeHandler` does not need `FieldDescriptors` at all
unless it is returning them — and it is, via `ToDetail`, so there only the current version is needed.

## T-10 (MINOR, implementation) — the mandatory PostgreSQL test skips, so success criterion 11 is unmet

§9.1 is headed *"At least one test must run against real PostgreSQL — mandatory"*, and names what
only a real database can prove: *"the case-insensitive unique index on `code`, `NOT NULL`,
`TIMESTAMPTZ` versus `TIMESTAMP`."* `TicketTypesSchemaTests.Migration_applies_and_a_ticket_type_round_trips_through_real_postgres`
is a `[SkippableFact]` and **skipped** in this run.

So success criterion 11 — *"Two codes differing only in case cannot both be inserted, even by raw
SQL"* — is currently unverified, and with it the whole class of SQL-versus-EF-configuration drift.
§9.1 also notes what the in-memory suite *cannot* prove: *"it ignores `HasColumnName`, unique
constraints, and…"* — which is exactly why the skip matters. Compounding: T-2's 409 paths can only
be tested here.

Correction: make the Postgres tests **fail** rather than skip when no database is reachable in CI,
and say so in §9.1.

## T-11 (MINOR, spec) — the `TEXT` columns are unbounded and the limit they defer to does not exist

§4.0 F rule 7 handles the `VARCHAR` columns properly, then says of `Description` and `HelpText`:
*"The `TEXT` columns have no length limit in PostgreSQL, so they need no check here — but they are
still not unbounded input; **cap them at the request-body size limit** rather than per-field."*

No request-body size limit is configured anywhere. `Program.cs` sets no
`MaxRequestBodySize`, no form or JSON limits, and 04-Infrastructure does not set one on the Caddy
side either — so the effective cap is Kestrel's 30 MB default, per field, unaudited. A single
`POST /create` can store tens of megabytes of `HelpText` per field descriptor, permanently, since
versions are immutable and nothing is ever deleted.

Correction: either name a concrete per-request limit in 04-Infrastructure and reference it here, or
give `Description` and `HelpText` explicit limits in §4.0 F's table like every other string.

---

## Spec gaps — what a builder had to guess

Ranked by how likely a wrong guess is to produce a security hole, data loss, or a broken build.

1. **Audit-failure semantics** (T-1). This plan's §4.0 rule I states the opposite of the LOCKED rule
   in App §5, having read "fire-and-forget" in 04-Infrastructure §6 and 03-SliceInventory rule 5 as
   licence to swallow failures. It is the third document to make that reading, and the reason three
   handlers ship with no transaction. Fixing the phrase in doc 4 (Audit A-3) is the single
   highest-value correction available, because Tickets, Documents and Employees will all read the
   same sentence.
2. **Whether a deactivated type's old versions stay readable to Customer-side callers** (T-4). §4.0
   E, §4.6, §9.2 and criterion 10 give three different answers, and the consequence lands in an
   unbuilt slice.
3. **The `AuditEntry` contract is not synchronised between the Audit plan and this one** (T-5).
   Every audit call here is written against a record shape and an action-name set that the shipped
   `IAuditApi` rejects. Any plan that names `AuditEntry` fields needs to be regenerated whenever the
   Audit slice's `ExternalInterfaces` change, and no document says who owns that.
4. **Whether tests may substitute their own `IActionCatalogue`** (T-3). Nothing forbids it, so this
   slice's entire authorization suite validates a table that only exists in `Shared/` and is missing
   three of the system's actions.
5. **String normalisation is unspecified** (T-7, T-8). No document says whether request strings are
   trimmed, whether trimming happens before or after validation, or whether blank is distinct from
   absent — while §1 depends on handler and database agreeing on what "already exists" means. The
   `Customers` slice trims and this one does not, from the same silence.
6. **Redundant state change: idempotent 200 or 422?** §4.3 specifies a silent 200 with no audit entry
   for a no-op toggle. The `Customers` slice returns 422 for re-suspending an already-suspended
   Customer, and its acceptance table locks that in. Two slices, two answers to one question, neither
   derived from a numbered document. Pick one system-wide and say so in App §8.
7. **Request-body size is unowned** (T-11). §4.0 F defers to a "request-body size limit" that no
   document sets, in neither the app nor the proxy.
8. **Where `BeginAsync` sits relative to the read.** App §6 requires the audit write to be inside the
   mutating slice's transaction but never says whether the read-for-update belongs inside it too.
   `EditTicketTypeHandler` computes `next` from a read that, once T-1 is fixed, must be inside the
   transaction or T-2's collision window stays open. Recorded as gap 8 in the Customers plan as well.

---

# Correction Notes — second review, 2026-09-01

Findings from a review of the tree after T-1…T-11 were partly applied. All are fixed in code.

## T-12 (BLOCKER, both) — T-4 and T-9 were fixed in the handlers and left live in `ExternalInterfaces/`, which is the path they were about

This is the most important item in this round, and the pattern matters more than the two instances.

`GetTicketTypeVersionHandler` got the T-4 fix (a deactivated type's version stays readable) and
`ToggleTicketTypeHandler` got the T-9 fix (do not load every version to return one).
`ExternalInterfaces/TicketTypesApi.cs` got neither. Both live methods were in
`GetTicketTypeVersionAsync`:

```csharp
var type = await LoadType(ticketTypeId, ct);
if (type is null || !IsVisible(type.IsActive, type.AllowEmployeeToOpen, callerRole))
    return null;                       // T-4: deactivating a type blanks every historical ticket
```

and `LoadType` was the unfiltered `Include(t => t.Versions).ThenInclude(v => v.FieldDescriptors)`
that T-9 named.

**In both cases the missed path is the one the finding was about.** `Tickets` reads ticket types
through `ITicketTypesApi`, not through `/api/ticket-types/version` — T-4's own text says so:
*"this handler's only real consumer."* So the correction was applied everywhere except where it
mattered, and the slice's tests all passed, because no test touched `TicketTypesApi`.

Two rules follow:

1. **A correction to a visibility or loading rule is not applied until `ExternalInterfaces/` is
   checked.** `Application/Handlers/` serves the HTTP caller; `ExternalInterfaces/` serves the other
   seven slices. They are different code paths with the same rules, and the cross-slice one is
   usually the one with the consequence.
2. **A rule that exists in two places will drift, so put it in one.** The cause here was that the
   handler expressed the rule as `ApplyCustomerSideVisibility` / `ApplyCustomerSideAudience` while
   `TicketTypesApi` had its own private `IsVisible(bool, bool, UserRole)` saying nearly the same
   thing. Fixed by making `TicketTypeMapper` own two predicates —
   `IsDiscoverableBy(type, role)` and `IsInAudienceOf(type, role)` — with the throwing helpers
   defined in terms of them and `TicketTypesApi` calling them directly. The handlers throw `404`,
   the API returns `null`, and the *rule* now exists once.

The filtered loads that replace `LoadType`:

```csharp
// current version
.Include(t => t.Versions.OrderByDescending(v => v.VersionNumber).Take(1))
    .ThenInclude(v => v.FieldDescriptors)

// one specific version
.Include(t => t.Versions.Where(v => v.VersionNumber == versionNumber))
    .ThenInclude(v => v.FieldDescriptors)
```

Also add tests against `TicketTypesApi` itself, not only against the handlers: that a deactivated
type's version is still served to an `Employee`, that `GetTicketTypeAsync` returns `null` for the
same type and the same caller, and that an `AllowEmployeeToOpen = false` type's version is hidden
from an `Employee` and visible to a `CustomerAdmin`.

## T-13 (MAJOR, both) — `/edit` trimmed only two of the fields `/create` trims, so the same input stored differently on each route

T-7 required every request string trimmed before validation, and the fix landed as
`TicketTypeMapper.NormalizeTicketType(CreateTicketTypeRequestDto)`. `EditTicketTypeHandler` never
called it — it trimmed `DisplayName` and `Category` inline at the call site and left every field
`Label` and `GroupName` untrimmed. So `"  Payroll  "` as a field label became `"Payroll"` through
`/create` and stayed `"  Payroll  "` through `/edit`.

Correction, applied: a second `NormalizeTicketType(EditTicketTypeRequestDto)` overload sharing one
private `NormalizeFields`, called from the handler. **Normalisation belongs in the mapper with the
validation it feeds, never inline in a handler** — inline is how one route ends up normalising a
different set of fields than another, and both DTOs already share `CreateFieldDescriptorDto`.

## T-14 (MINOR, implementation) — `CurrentVersionOf` threw `AppException(…, 500)`, putting internal state in a response body

```csharp
?? throw new AppException("Ticket type has no version.", 500);
```

`AppException`'s message is written into the `ProblemDetails` body by design — that is what makes it
the right type for a `422`. A type with no version rows is a broken invariant, not something a
caller did, and its diagnosis is not the caller's business. The LOCKED rule in the README is that
anything a client can trigger by sending a value is a `4xx`; the converse is that a `500` should
carry no detail.

Correction, applied: `throw new InvalidOperationException($"Ticket type {type.Id} has no version
rows.")`. `AppExceptionMiddleware`'s `catch (Exception)` turns it into a bare `ProblemDetails` `500`
and logs the detail server-side. **`AppException` with a 5xx status is always wrong.**

## T-15 (MINOR, implementation) — the endpoint re-hard-coded the page-size default that T-6 centralised

`TicketTypesEndpoints.cs` had `PageSize = pageSize ?? 15`. The `15` is correct today and
`PaginatedQuery.DefaultPageSize` exists precisely so that changing it is one edit. A literal in an
endpoint is how the 25/200 drift recorded in T-6 and C-3 happened in the first place.

Correction, applied: `pageSize ?? PaginatedQuery.DefaultPageSize`.

## T-16 (MINOR, both) — a stored `RegexPattern` was compiled without a match timeout

`ValidateRegexCompiles` did `_ = new Regex(pattern)`. Compiling without a timeout is harmless in
itself — nothing is matched here — but it left no shared constant for the slice that *does* match,
and `Tickets` §12 rule 3 requires a timeout there. A pattern is authored by an Accountant and run
against a value supplied by a Customer over the internet, so catastrophic backtracking is a
request-side denial of service.

Correction, applied: `Shared.Validation.UserSuppliedRegex.MatchTimeout` (100 ms), used here and by
`Tickets` so both halves of the rule use one number. Do not substitute
`RegexOptions.NonBacktracking` — it rejects backreferences and lookaround at construction, so a
pattern this slice accepted would throw in `Tickets`.

> **AMENDED.** The constant was first placed on `TicketTypeMapper` as an `internal` field, and the
> `Tickets` plan told that slice to reference
> `TicketTypes.Application.TicketTypeMapper.RegexMatchTimeout`. That compiled — one assembly — but it
> is a dependency rule 2 violation, and it pinned this mapper's shape in place on behalf of a slice
> with no visible relationship to it. It now lives in `Shared/Validation/UserSuppliedRegex.cs`. Not in
> `ExternalInterfaces/` either: a limit on evaluating untrusted patterns is not part of the
> `TicketTypes` contract.
