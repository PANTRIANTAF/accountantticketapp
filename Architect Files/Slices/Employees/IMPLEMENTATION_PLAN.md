# Employees Slice — Implementation Plan

Build this **fifth**, after `Audit`, `Notifications`, `Customers`, and `Identity`, and before
`Documents` and `Tickets`.

It depends on more slices than anything except `Tickets`, and it owns the one endpoint in the whole
application that is registered by a slice that does not own the thing it creates — the composite
Customer-onboarding operation (§4.1). That is deliberate and LOCKED; §0.6 explains it so nobody
"fixes" it.

Documents that govern this slice, in precedence order. Where any of them disagrees with this plan,
**they win and this plan is wrong** — fix the plan, do not code around it.

- [02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §3 (Customers — the composite
  operation), §4 (Employees, and its five constraints), §12 (rules a builder must not violate)
- [01-DomainModel.md](../../01-DomainModel.md) §2 (Employee, Customer, and "The Customer Admin role
  is not a separate entity"), §9.5, §9.6
- [03-SliceInventory.md](../../03-SliceInventory.md) §1 ("Customer onboarding is one operation, and
  it lives in `Employees` — LOCKED"), §2, §3 rules 1–4
- [App/GeneralAppArchitecture.md](../../App/GeneralAppArchitecture.md) §5 (transactions), §8
  (routes, pagination)

---

## 0. Prerequisites — read before writing any code

### 0.1 What this slice owns, and the four things it does not

It owns **`Employee`** — a person who works for a Customer. `01-DomainModel.md` §2 calls the
separation of `Employee` from `UserAccount` *"the single most important structural decision in this
model, because it is what makes on-behalf-of ticketing possible."*

Four things it does **not** own, each of which a builder will reach for:

1. **`UserAccount`.** It does not create, hash, invite, suspend, or role-change an account
   directly. Every account operation goes through `IIdentityApi`
   ([the Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §9.1). There is no
   `employees` table column holding a password, a token, or a status of an account.
2. **`Customer`.** It reads and creates Customers through `ICustomerApi`, never by touching the
   `customers` table.
3. **The Customer Admin *role*.** `01-DomainModel.md` §2: *"The Customer Admin role is not a
   separate entity. A Customer Admin **is** an Employee whose UserAccount has role
   `CustomerAdmin`."* So the role lives on the account, in `Identity`, and this slice changes it by
   asking. There is no `is_customer_admin` column, and adding one is the most likely wrong turn in
   this slice.
4. **Which Tickets an Employee can see.** `Tickets` computes that from the Subject link (§9.5).
   This slice does not know what a Ticket is.

### 0.2 `CurrentUser` and the four roles that reach this slice

Unlike `Identity`, this slice only consumes `CurrentUser`. All four roles reach it, and each sees a
different slice of the same table:

| Role | Reach |
|---|---|
| `AccountantAdmin` | Every Employee of every Customer, plus the composite onboarding endpoint |
| `AccountantUser` | Every Employee of every Customer — **everything except onboarding** |
| `CustomerAdmin` | Every Employee of **their own** Customer |
| `Employee` | **Their own record only**, and only their own contact details are editable |

The `Employee` row is the tightest scope in the system: not "their Customer", but "themselves".
`WhereInCustomerScope` gets them to their Customer; a **second** filter gets them to their own row
(§4.4 rule 2). Forgetting the second one exposes every colleague's tax identification number and
social-security number to every Employee, which is the highest-consequence mistake available in
this slice.

### 0.3 `CustomerScope` — this is the slice where it does the most work

`Employee` implements `ICustomerScoped`, and every query that a Customer-side caller can reach
goes through `.WhereInCustomerScope(user)`
([03-SliceInventory.md](../../03-SliceInventory.md) §4):

```csharp
public sealed class Employee : ICustomerScoped
{
    public Guid CustomerId { get; set; }
    // …
}
```

Rules:

1. **Accountants pass through unfiltered.** The extension returns the query unchanged for both
   Office roles, because `01-DomainModel.md` §1 rule 2 says they are not scoped.
2. **A `CustomerAdmin` or `Employee` whose `CustomerId` is null never reaches a handler** —
   `CurrentUserFactory` throws `401` first (Identity §0.2). Do not write a null check that falls
   back to an unfiltered query; that is a cross-tenant read waiting for a bug upstream.
3. **An out-of-scope id yields `404`, naturally**, because the filtered query finds nothing. Do
   **not** load the row and then compare `CustomerId` to `user.CustomerId` and return `403` — the
   `403` confirms the row exists, and matrix §1 requires `404`.
4. **The mandatory cross-Customer test exists for this slice too**, and here it needs three cases,
   not one: a `CustomerAdmin` reading another Customer's Employee, an `Employee` reading a
   *colleague's* record at their own Customer, and an `Employee` editing a colleague's contact
   details. The second and third are the ones the scope extension alone does not stop.

### 0.4 The permission checker — fail-closed

Handlers take `IPermissionChecker` and call
`await _permissions.RequireAsync(user, "ActionName", ct: ct)` as the **first statement**. An action
absent from the composed catalogue denies; a role not listed denies; every denial is audited before
the `403`.

Every handler in this slice has a catalogue action — there are no unauthenticated endpoints here.
But **the catalogue only expresses "which roles may call"**, and this slice's matrix rows are
mostly "yes, *own Customer*" or "own record only". Those qualifiers are enforced in the handler by
the scope filter and the self checks; the catalogue cannot express them. A handler whose only
authorization is `RequireAsync` is a handler that lets a `CustomerAdmin` edit another Customer's
Employees.

### 0.5 Pagination

Use `Shared/Pagination/`. Default `PageSize` **15**, maximum **50**
(`App/GeneralAppArchitecture.md` §8 — these are the system-wide numbers; do not pick your own).
`PageSize` above the maximum is **clamped to 50 with a `200`**, not rejected; a `PageNumber` below
1 clamps to 1.

One paginated endpoint: `/api/employees/list`. Default sort
`family_name ASC, given_name ASC, id ASC`. The `id` tiebreaker is mandatory — two Employees at one
Customer sharing a surname and given name is entirely ordinary, and an unstable sort makes paging
skip and repeat rows.

### 0.6 The composite onboarding endpoint lives here — LOCKED, and it looks wrong

`02-AuthorizationMatrix.md` §3 is normative: *"Creating a Customer includes registering and
inviting its first Customer Admin, in one operation — a Customer with no way to log in is
useless."*

[03-SliceInventory.md](../../03-SliceInventory.md) §1 locks the location. The chain, restated
because it is the thing most likely to be "corrected":

- `Customers` may depend only on `Audit`. It cannot create an Employee or an account.
- `Employees → Customers` already exists, so `Customers → Employees` would be a **cycle**,
  forbidden by dependency rule 1.
- `Employees` already depends on `Customers`, `Identity`, and `Notifications` — every slice the
  operation needs, with **no new edge and no new architectural concept**.

So `POST /api/customers/onboard` is registered by **`Employees`**. Two consequences:

1. **It is `AccountantAdmin`-only**, because creating a Customer is (matrix §3). Wrapping the
   operation does not make it an `AccountantUser` power. `03-SliceInventory.md` §1 says this
   explicitly.
2. **The route name must make the surprise visible rather than hide it.** `/api/customers/onboard`
   sits next to `Customers`' own `/api/customers/create` in the URL space and in the OpenAPI
   document, which is what a caller expects. The *file* it is registered from is the surprise, and
   §11 rule 2 requires a comment saying so at the registration site.

Do **not** "fix" this by adding a `FirstAdmin` block to `CreateCustomerRequestDto`, by giving
`Customers` a dependency on this slice, or by having the SPA make three calls. The third loses
atomicity and can leave exactly the state the matrix forbids: a Customer nobody can log into.

### 0.7 The five decisions locked for this slice

| # | Decision |
|---|---|
| 1 | ~~**`Departed` is terminal.** There is no un-depart operation.~~ **Superseded 2026-09-02** — `Departed` is reversible **as a correction only**, through `/api/employees/reinstate`. A genuine re-hire is still a new record. See §4.7, §4.7a and the decision record (§13 item 3). |
| 2 | **Registering and inviting are two separate endpoints**, never one. Matrix §4. See §4.2 and §4.5. |
| 3 | **Departure suspends the Employee's account**, automatically, in the same transaction. `01-DomainModel.md` §9.6 rule 2. See §4.7. |
| 4 | **`ICustomerApi` gains a `CreateAsync`** — resolving the question the `Customers` plan deferred here. See §4.1 and §10. |
| 5 | **Personal identifying numbers are stored, and are visible to Accountants and to the owning Customer's Admins**, and to the Employee themselves. See §1 and §3.1. |

---

## 1. Database schema (SQL migration)

**File:** `Slices/Employees/Infrastructure/Migrations/20260902_001_CreateEmployeesSchema.sql`

One table.

```sql
CREATE TABLE employees (
    id                       UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The tenant boundary. No foreign key: customers belongs to another slice.
    customer_id              UUID NOT NULL,

    given_name               VARCHAR(100) NOT NULL,
    family_name              VARCHAR(100) NOT NULL,

    -- The login identifier IF and WHEN they are invited. NULL for an accountless Employee
    -- who has no email on file. NOT unique here — see the notes below.
    work_email               VARCHAR(320) NULL,
    normalized_work_email    VARCHAR(320) NULL,

    -- The account, once one exists. NULL for an accountless Employee.
    -- No foreign key: user_accounts belongs to another slice.
    user_account_id          UUID NULL,

    -- Personal identifying numbers the Office needs. Sensitive; see §3.1.
    tax_identification_number VARCHAR(50) NULL,
    social_security_number    VARCHAR(50) NULL,

    job_title                VARCHAR(200) NULL,

    employment_start_date    DATE NOT NULL,
    employment_end_date      DATE NULL,

    -- 'Active' | 'Departed'
    status                   VARCHAR(20) NOT NULL DEFAULT 'Active',

    contact_phone            VARCHAR(50) NULL,

    created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    departed_at              TIMESTAMPTZ NULL,

    -- An employment end date is set exactly when the person has departed, and never before.
    CONSTRAINT ck_employees_departure CHECK (
        (status = 'Active'   AND departed_at IS NULL)
        OR
        (status = 'Departed' AND departed_at IS NOT NULL)
    ),

    -- An end date, when present, cannot precede the start date.
    CONSTRAINT ck_employees_dates CHECK (
        employment_end_date IS NULL OR employment_end_date >= employment_start_date
    ),

    -- The two email columns are populated together or not at all.
    CONSTRAINT ck_employees_email_pair CHECK (
        (work_email IS NULL AND normalized_work_email IS NULL)
        OR
        (work_email IS NOT NULL AND normalized_work_email IS NOT NULL)
    )
);
```

| Column | Note |
|---|---|
| `customer_id` | The tenant boundary and the `ICustomerScoped` property. **Immutable after creation** — see below. |
| `work_email` / `normalized_work_email` | Stored as typed and lowercased-and-trimmed, for the same reason as `user_accounts` ([Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §1, "Why the email is stored twice"). Nullable because `01-DomainModel.md` §2 says it *"may be absent for an accountless Employee"*. |
| `user_account_id` | Set when the Employee is invited. **Not** a foreign key — `Identity` owns that table. |
| `tax_identification_number`, `social_security_number` | `VARCHAR`, never numeric: leading zeros are significant, formats vary, and nothing arithmetic is ever done with them. Nullable because the Office may not have them yet at registration. |
| `employment_start_date`, `employment_end_date` | `DATE`, not `TIMESTAMPTZ`. Employment starts on a day, not at an instant, and a timezone-shifted timestamp turns a start date into the previous day for half the world. |
| `status` | `'Active'` or `'Departed'`. Terminal — decision 1. |
| `departed_at` | `TIMESTAMPTZ`, the audit-relevant instant, separate from the `DATE` the employment ended. Both exist because they answer different questions. |
| `updated_at` | Written by the application on every edit, not by a trigger. `App/GeneralAppArchitecture.md` uses no database triggers, and a trigger writing a column EF also tracks produces silent divergence. |

### Why `customer_id` is immutable, and why there is no `UPDATE` that changes it

`01-DomainModel.md` §2: *"an Employee record belongs to exactly one Customer. If the same natural
person works for two Customers of the Office, that is two independent Employee records with no
link between them. This is deliberate — it keeps Customer isolation absolute."*

So there is **no move-an-Employee operation**, no endpoint that accepts a `CustomerId`, and no
handler that writes `employee.CustomerId` after creation. This is not a missing feature.

It is also the load-bearing fact behind a decision in another slice: `Identity` stores
`customer_id` on `user_accounts`, supplied by this slice at invitation time, **because the value can
never go stale** ([Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §1, "Why `customer_id` is a
column here"). If you add an operation that moves an Employee between Customers, you silently
break the tenant scope of their session. Do not add one.

### Why `work_email` is *not* globally unique

`user_accounts.normalized_login_email` **is** unique system-wide (Identity §1). This column is not,
and the difference is intentional:

- An accountless Employee's `work_email` is a note on a record, not a credential. Two Customers may
  each have an Employee with a shared family address on file, and neither can log in.
- **Uniqueness is enforced at invitation time**, by `Identity`, when the address becomes a login
  identifier. §4.5 rule 4 pre-checks it and maps the constraint violation to `409`.
- Within one Customer, though, two Employees with the same work email is a data-entry error rather
  than a legitimate state, so a **per-Customer** unique index is correct. See the indexes below.

> Do not add a global unique index on `normalized_work_email`. It would make registering an
> accountless Employee fail because an *unrelated Customer* has that address on file, and the error
> message could not say why without leaking another Customer's data.

### Indexes

```sql
-- The list endpoint, in its sort order, scoped to a Customer. Covers the common query.
CREATE INDEX idx_employees_customer_name
    ON employees (customer_id, family_name, given_name, id);

-- Two Employees at ONE Customer must not share a work email. Partial, because NULL is common
-- and NULLs are not comparable anyway — the WHERE makes the intent explicit.
CREATE UNIQUE INDEX uq_employees_customer_email
    ON employees (customer_id, normalized_work_email)
    WHERE normalized_work_email IS NOT NULL;

-- Identity asks "which Employee owns this account?" and Tickets resolves the reverse.
-- Unique: one account belongs to at most one Employee.
CREATE UNIQUE INDEX uq_employees_user_account
    ON employees (user_account_id)
    WHERE user_account_id IS NOT NULL;

-- The at-least-one-Active-CustomerAdmin guard (§8.1) and the "who can be a Ticket Subject"
-- query both filter on Active within a Customer.
CREATE INDEX idx_employees_customer_active
    ON employees (customer_id)
    WHERE status = 'Active';
```

`uq_employees_user_account` is a domain rule the database should hold: one account, one Employee.
Two Employee rows pointing at one account means two Customer scopes for one session, and whichever
one a query happens to find first wins.

### No deletes

Matrix §4: *"Delete an Employee record — **Nobody.**"* `01-DomainModel.md` §9.2 retains everything
indefinitely.

- No `DELETE` statement, no `deleted_at`, no delete endpoint, no soft-delete flag. `Document` is
  the only entity in the system with a soft delete, and this is not it.
- **`Departed` is not a delete.** A departed Employee stays in the table, stays visible to their
  Customer Admin forever (§9.6), and stays the Subject of every Ticket they were ever the Subject
  of.

---

## 2. EF Core entities and DbContext

### 2.0 Column naming — mandatory

Entities are PascalCase, columns are snake_case, and **there is no automatic conversion
configured**. Every property needs an explicit `HasColumnName`. A missing one produces
`42703: column e.FamilyName does not exist` at runtime, on one code path, not at startup.
`App/GeneralAppArchitecture.md` §5.

### 2.1 `Core/Employee.cs`

```csharp
public sealed class Employee : ICustomerScoped
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }

    public string GivenName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;

    public string? WorkEmail { get; set; }
    public string? NormalizedWorkEmail { get; set; }

    public Guid? UserAccountId { get; set; }

    public string? TaxIdentificationNumber { get; set; }
    public string? SocialSecurityNumber { get; set; }
    public string? JobTitle { get; set; }
    public string? ContactPhone { get; set; }

    public DateOnly EmploymentStartDate { get; set; }
    public DateOnly? EmploymentEndDate { get; set; }

    public string Status { get; set; } = EmployeeStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DepartedAt { get; set; }

    public bool HasAccount => UserAccountId is not null;
    public bool IsActive => Status == EmployeeStatus.Active;
}

public static class EmployeeStatus
{
    public const string Active   = "Active";
    public const string Departed = "Departed";
}
```

Three notes:

- **`DateOnly` for the two employment dates**, mapping to `DATE`. Npgsql maps `DateOnly` to `date`
  natively. Using `DateTime` here re-introduces the timezone problem the `DATE` column exists to
  avoid.
- **There is no `Role` property.** The role lives on the account (§0.1 point 3). A caller who needs
  it asks `IIdentityApi`. Adding `Employee.Role` gives the system two answers to "is this person a
  Customer Admin", and they will disagree.
- **There is no navigation property to `UserAccount` or to `Customer`.** Both are other slices'
  entities, and a navigation would require this context to map their tables — dependency rule 3.

### 2.2 `Infrastructure/EmployeesDbContext.cs`

```csharp
public sealed class EmployeesDbContext : DbContext
{
    public EmployeesDbContext(DbContextOptions<EmployeesDbContext> options) : base(options) { }

    public DbSet<Employee> Employees => Set<Employee>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfiguration(new EmployeeConfiguration());
}
```

1. **The `DbContextOptions<EmployeesDbContext>` constructor is required.** §10 explains why.
2. **Never `AddScoped<EmployeesDbContext>()`.**
3. **It maps exactly one entity.** If a `DbSet<Customer>` or `DbSet<UserAccount>` appears here,
   two slices now own one table and their migrations will fight.

### 2.3 Why there is **no** global query filter for Customer scope

The same three reasons as the `Customers` plan §2.3, plus two specific to here:

1. `ICustomerApi`-style external reads and the composite onboarding operation act on behalf of
   Accountants, who are unscoped. A global filter would need to know the caller's role, which
   means a filter reading a scoped service — an EF anti-pattern that produces a captured
   `CurrentUser`.
2. **A global filter would hide the second, tighter filter this slice needs.** An `Employee` sees
   *their own record only*, not their Customer's. A Customer-level global filter would make the
   `Employee` case look handled when it is not, which is exactly the failure in §0.2.
3. Explicit `.WhereInCustomerScope(user)` at the call site is visible in review and greppable.
   A missing one is a diff; a missing global filter is nothing.

Also: **no global filter excluding `Departed`.** Matrix §4 lets a Customer Admin list and view
departed Employees, §9.6 requires their Tickets stay visible, and the departure handler must be
able to find its own target. Filter on status explicitly, where the rule actually applies (§4.3
rule 3).

### 2.4 Configuration

`Infrastructure/Configurations/EmployeeConfiguration.cs`. Every property gets `HasColumnName`,
`HasMaxLength` matching the DDL exactly, and `IsRequired()` where the column is `NOT NULL`.

Specifics:

- `builder.Property(e => e.EmploymentStartDate).HasColumnName("employment_start_date").HasColumnType("date")`
- `builder.Property(e => e.Status).HasColumnName("status").HasMaxLength(20).IsRequired()`
- The two email columns are nullable and get **no** `IsRequired()`.
- `builder.HasIndex(e => new { e.CustomerId, e.NormalizedWorkEmail }).IsUnique().HasFilter("normalized_work_email IS NOT NULL")`
  — declared so EF's model matches the database. The **SQL script** is what creates it (§13); this
  is for model-consistency checks, not for migration generation.

---

## 3. DTOs

`Application/Dtos/`. Records for responses, classes with settable properties for request bodies.

| DTO | Fields |
|---|---|
| `EmployeeSummaryDto` | `Id`, `GivenName`, `FamilyName`, `JobTitle`, `Status`, `HasAccount`, `Role` (nullable) |
| `EmployeeDetailDto` | Everything in the summary, plus `WorkEmail`, `ContactPhone`, `TaxIdentificationNumber`, `SocialSecurityNumber`, `EmploymentStartDate`, `EmploymentEndDate`, `CustomerId`, `AccountStatus` (nullable), `CreatedAt` |
| `EmployeeSelfDto` | `Id`, `GivenName`, `FamilyName`, `JobTitle`, `WorkEmail`, `ContactPhone`, `EmploymentStartDate`, `CustomerId` |
| `RegisterEmployeeRequestDto` | `CustomerId`, `GivenName`, `FamilyName`, `WorkEmail?`, `ContactPhone?`, `JobTitle?`, `TaxIdentificationNumber?`, `SocialSecurityNumber?`, `EmploymentStartDate` |
| `ListEmployeesRequestDto` | `CustomerId?`, `Status?`, `HasAccount?`, `SearchTerm?`, `PageNumber`, `PageSize` (`= 15`) |
| `EmployeeIdRequestDto` | `EmployeeId` — used by view, depart, invite, suspend, reactivate |
| `UpdateEmployeeRequestDto` | `EmployeeId`, plus every editable field |
| `UpdateOwnContactRequestDto` | `ContactPhone?`, `WorkEmail?` — **and no `EmployeeId`** |
| `InviteEmployeeRequestDto` | `EmployeeId`, `LoginEmail?`, `Role` |
| `SetEmployeeRoleRequestDto` | `EmployeeId`, `Role` |
| `DepartEmployeeRequestDto` | `EmployeeId`, `EmploymentEndDate` |
| `OnboardCustomerRequestDto` | A `Customer` block plus a `FirstAdmin` block — see §4.1 |
| `OnboardCustomerResponseDto` | `CustomerId`, `EmployeeId`, `UserAccountId` |
| `MarkedResultDto` | `Success` |

### 3.1 Three read DTOs, because the matrix has three different answers

Matrix §4 row "View an Employee record": `AA` and `AU` any, `CA` own Customer, `EMP` own record
only. Three audiences, three types.

- **`EmployeeSelfDto`** — what an `Employee` gets for their own record. **No tax identification
  number, no social-security number, no `Status`, no `UserAccountId`.** Not because they are secret
  from the person themselves, but because this endpoint has no reason to return them and a
  narrower type cannot leak them.
- **`EmployeeDetailDto`** — Accountants and the owning `CustomerAdmin`. Carries the personal
  identifying numbers. Decision 5: the Office needs them to do accounting work, and the employer
  supplied them, so both may see them. **An `Employee` never receives this type for anyone,
  including themselves.**
- **`EmployeeSummaryDto`** — the list row. No personal identifying numbers, no email.

> **This is why there are three DTOs rather than one with nulled-out fields.** A type that has no
> `SocialSecurityNumber` property cannot serialise one. A handler that must remember to null it out
> will, one day, not — and the reviewer of that diff sees a field being *set*, which looks correct.
> The same argument, and the same shape, as `AccountantSummaryDto` vs `AccountantDetailDto` in
> [the Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §6 rule 2.

### 3.2 `UpdateOwnContactRequestDto` has no `EmployeeId`, and that is the security control

Matrix §4: an `Employee` may edit *"own contact details only"*. The endpoint that lets them do it
takes **no target**. The target is `CurrentUser`'s Employee record, resolved by the handler.

**You cannot forget to validate a parameter you never accepted.** If this DTO had an `EmployeeId`,
the whole protection would rest on one `if` in one handler, and that `if` is exactly what gets
refactored away. Same reasoning as `ChangePasswordRequestDto` in Identity §6 rule 5.

### 3.3 `EmployeeSummaryDto.Role` and `EmployeeDetailDto.AccountStatus` come from `Identity`

Both are nullable, both are `null` for an accountless Employee, and **neither is a column**
(§0.1 point 3). They are filled from `IIdentityApi`. In the list handler that means **one bulk
call**, not one per row — see §4.3 rule 5.

---

## 4. Handlers

`Application/Handlers/`, one file each, registered `AddTransient`.

### 4.0 Rules that apply to every handler in this slice

**A. The canonical signature:**

```csharp
public async Task<TResponse> Handle(TRequest req, CurrentUser user, CancellationToken ct)
```

**B. Authorization first**, before any database read. Then the scope filter. Then the self checks.
Then the invariant guards. In that order, every time.

**C. One transaction per writing handler.** `await using var scope = await
_transaction.BeginAsync(_db, ct);` … `await _transaction.CommitAsync(ct);`. `AuditApi` enlists
itself, so the audit entry commits or rolls back with the business change
(`App/GeneralAppArchitecture.md` §5).

> **D. Calls into `IIdentityApi` and `ICustomerApi` join *this* transaction.** They call
> `IRequestTransaction.EnlistAsync`, they do not open or commit their own
> ([Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §9.1 rule 6). This is what makes §4.1
> atomic. **Verify it when you build this slice** — if either one commits internally, the
> composite onboarding operation can leave a Customer with no Employee and no account, which is
> precisely the state matrix §3 forbids, and it will only happen when the third step fails.

**E. Every scoped read goes through `.WhereInCustomerScope(user)`**, and every read of a *single*
Employee by an `Employee` role adds `.Where(e => e.UserAccountId == parsedUserId)`. §0.2.

**F. Audit every write.** `AuditActions` already contains four codes for this slice:

| Handler | Action code | Target |
|---|---|---|
| `RegisterEmployeeHandler` | `EmployeeRegistered` | `Employee` |
| `UpdateEmployeeHandler`, `UpdateOwnContactHandler` | `EmployeeEdited` | `Employee` |
| `DepartEmployeeHandler` | `EmployeeDeparted` | `Employee` |
| `InviteEmployeeHandler` | `EmployeeInvited` | `Employee` |
| `OnboardCustomerHandler` | `CustomerCreated`, then `EmployeeRegistered`, then `EmployeeInvited` | mixed |

> **There is no `AuditActions` code for an Employee role change, and `AuditApi` throws on an
> unknown code** ([Audit plan](../Audit/IMPLEMENTATION_PLAN.md) §D). So §4.6 cannot be built until
> a code exists. Add `EmployeeRoleChanged` to
> `Slices/Audit/ExternalInterfaces/AuditActions.cs` — the catalogue is `Audit`'s to extend, and
> `AuditActions.All` is reflected over the constants, so one line is the whole change. **Flagged
> in §18.** Do not work around it by reusing `EmployeeEdited`: a role change and a phone-number
> change must be distinguishable in the audit log, or the log cannot answer "who made this person
> an administrator".

**G. Account-level operations are audited by `Identity`, not here.** Suspending an Employee's
account writes `AccountSuspended` from inside `IIdentityApi` (Identity §9.1 rule 8). This slice
does **not** also write one. Two entries for one action is correct only when two slices changed
their own data — as in §4.5, where an Employee row *and* an account are both written.

**H. No handler in this slice writes a password, hashes anything, or generates a token.** All of
it is `Identity`'s. If `PasswordHasher` or `RandomNumberGenerator` appears in this slice, something
is being built in the wrong place.

### 4.1 `OnboardCustomerHandler` — **AccountantAdmin only**

The composite operation. `OnboardCustomerRequestDto` → `OnboardCustomerResponseDto`.

`RequireAsync(user, "OnboardCustomer")` — `AccountantAdmin` only (§0.6 point 1).

```
begin transaction

# 1. The Customer. Delegated — this slice never touches the customers table.
customerId = await _customers.CreateAsync(req.Customer, ct)

# 2. The first Employee, at that Customer.
employee = new Employee {
    CustomerId          = customerId,
    GivenName           = req.FirstAdmin.GivenName,
    FamilyName          = req.FirstAdmin.FamilyName,
    WorkEmail           = req.FirstAdmin.WorkEmail,
    NormalizedWorkEmail = normalize(req.FirstAdmin.WorkEmail),
    EmploymentStartDate = req.FirstAdmin.EmploymentStartDate,
    Status              = Active,
}
_db.Employees.Add(employee)
await _db.SaveChangesAsync(ct)

# 3. The account and the invitation. Delegated — Identity owns accounts.
userAccountId = await _identity.InviteEmployeeAccountAsync(new InviteEmployeeAccount(
    EmployeeId: employee.Id,
    CustomerId: customerId,              # ← mandatory; see below
    LoginEmail: req.FirstAdmin.WorkEmail,
    DisplayName: $"{given} {family}",
    Role:       UserRole.CustomerAdmin), ct)   # ← CustomerAdmin, not Employee

employee.UserAccountId = userAccountId
await _db.SaveChangesAsync(ct)

audit CustomerCreated, EmployeeRegistered, EmployeeInvited
await _transaction.CommitAsync(ct)
```

Rules:

1. **`ICustomerApi` gains `Task<Guid> CreateAsync(CreateCustomer request, CancellationToken ct)`** —
   decision 4, resolving the question [the Customers plan](../Customers/IMPLEMENTATION_PLAN.md)
   §4.1 explicitly deferred to this plan. Adding a write method to that contract is preferred over
   this slice resolving `CreateCustomerHandler` from the container directly, because:
   - The `ExternalInterfaces` folder is a slice's declared public surface (dependency rule 4). A
     cross-slice call that goes around it is invisible to anyone reading either slice.
   - A handler is not a contract. `CreateCustomerHandler.Handle` takes `CurrentUser` and calls
     `RequireAsync` itself, so calling it from here would run the permission check **twice**, under
     two different action names, and audit the Customer creation as if it were a direct request.
   - `CreateAsync` can require exactly what this call site has and nothing more, and it enlists in
     this transaction (rule D) rather than managing its own.

   **`ICustomerApi.CreateAsync` must not check permissions** — this handler already did — and
   **must** audit `CustomerCreated`, because the Customer row is `Customers`' data. Add this to the
   `Customers` plan §5 when you build it.

2. **`Role` is `CustomerAdmin`, not `Employee`.** The whole point is that the Customer has somebody
   who can administer it. Matrix §3: *"a Customer with no way to log in is useless"* — and
   `01-DomainModel.md` §1: *"at least one is a CustomerAdmin"*. Creating the first person as a
   plain `Employee` produces a Customer that violates its own invariant from the moment it exists,
   and §8.1 will then block every subsequent role change out of the hole.

3. **`CustomerId` must be passed to `InviteEmployeeAccountAsync`.** It is non-nullable on
   `InviteEmployeeAccount` for exactly this reason ([Identity plan](../Identity/IMPLEMENTATION_PLAN.md)
   §9.1 rule 3): `Identity` cannot look it up, and the `ck_user_accounts_scope` check constraint
   rejects the row without it.

4. **`WorkEmail` is required on the `FirstAdmin` block**, unlike on `RegisterEmployeeRequestDto`.
   You cannot invite somebody without an address, and this operation always invites. Absent → `422`.

5. **One transaction, three slices, and a failure at any step leaves nothing behind.** This is the
   entire justification for the slice placement (§0.6) and for `RequestConnection` existing at all.
   Verify it with a test that makes step 3 fail and then asserts **the `customers` table is
   empty** — not that the response was a `500`.

6. **It returns all three ids.** The SPA needs the Customer id to navigate and the Employee id to
   show the invitation state.

7. **It returns no token.** The invitation link goes to the invitee's mailbox and nowhere else
   ([Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §7.8 rule 8).

8. **Validate the whole request before step 1.** Both blocks, fully. A `422` discovered after the
   Customer row was inserted is a rollback that works — but a `422` discovered after the
   *invitation email was queued* is an outbox row for an account that no longer exists. The outbox
   row is in the same transaction and rolls back too, so this is safe by construction; validate up
   front anyway, because "safe by construction" stops being true the first time somebody moves the
   notification call outside the transaction.

### 4.2 `RegisterEmployeeHandler` — `AA`, `AU`, `CA` for their own Customer

`RequireAsync(user, "RegisterEmployee")` — all three. `RegisterEmployeeRequestDto` →
`EmployeeDetailDto`.

1. **The resulting Employee is accountless.** `UserAccountId` is null, and no account is created,
   no invitation is sent, and no email goes anywhere. Matrix §4: *"Registering and inviting are two
   separate operations. The first creates an accountless Employee; the second gives them a login. A
   Customer Admin may do the first without ever doing the second."* Decision 2.

   > This is the shape that makes on-behalf-of ticketing work: an Employee who has never logged in
   > can be the Subject of a Ticket a Customer Admin opens for them. Merging the two operations
   > breaks the model's most important structural decision (§0.1).

2. **`req.CustomerId` is validated against the caller's scope, not trusted.**
   - `AccountantAdmin` / `AccountantUser`: any Customer. Verify it exists and is `Active` via
     `ICustomerApi.IsActiveAsync`; a suspended or unknown Customer is `422`.
   - `CustomerAdmin`: **must equal `user.CustomerId`**, else `403`. This is one of the few places a
     `403` is right rather than a `404` — the caller supplied a Customer id, and Customer ids are
     not secret to a `CustomerAdmin` who knows their own. There is no row being hidden.
   - `Employee`: denied by the catalogue.
3. **A suspended Customer cannot gain Employees.** `IsActiveAsync` is called live, never cached
   ([Customers plan](../Customers/IMPLEMENTATION_PLAN.md) §5). Do not write
   `FindAsync(...)?.IsActive ?? true` — the `?? true` turns "no such Customer" into "go ahead".
4. **Pre-check the per-Customer email uniqueness and return `409`**, *and* catch
   `DbUpdateException` wrapping `PostgresException` with `SqlState == "23505"` and map it to the
   same `409`. The pre-check gives a good message; the constraint is the guarantee. Two Admins
   registering the same person concurrently otherwise produces a `500`.
5. **`EmploymentStartDate` may be in the past or the future**; both are legitimate. Reject a date
   more than, say, a year in the future as a `422` typo guard — but **flag the threshold** rather
   than inventing a business rule (§18).
6. **`EmploymentEndDate` is not accepted by this endpoint.** A registration creates an `Active`
   Employee, and `ck_employees_departure` forbids an end date without `Departed`. Departure is
   §4.7.
7. Audit `EmployeeRegistered`, target `Employee`, `After` carrying names and Customer — **not** the
   personal identifying numbers. Rule G in Identity §7.0 has the general form of this; here it
   matters because a tax identification number in an audit row is a tax identification number
   retained forever in a table nobody purges.

### 4.3 `ListEmployeesHandler` — all roles except `Employee`

`RequireAsync(user, "ListEmployees")` — `AA`, `AU`, `CA`. **Not `Employee`**: matrix §4 gives them
"own record only", and a list of one is still a list endpoint they may not call.

Returns `PaginatedResponse<EmployeeSummaryDto>`.

1. **`.WhereInCustomerScope(user)` always.** For a `CustomerAdmin` this reduces the query to their
   own Customer regardless of what `req.CustomerId` says.
2. **`req.CustomerId` is a *filter* for Accountants and is ignored for a `CustomerAdmin`** — or,
   better, is a `403` when it names a different Customer, so a mistake is visible rather than
   silently reinterpreted. Pick the `403`; a filter that quietly means something else for one role
   is how a `CustomerAdmin` comes to believe they have cross-Customer visibility.
3. **`Status` and `HasAccount` are optional filters, and the default returns everything** —
   including `Departed` Employees. §9.6 requires them to stay visible, and a default that hides
   them makes a Customer Admin think the record is gone. If the SPA wants an active-only default,
   it passes the filter.
4. **`SearchTerm` matches given name, family name, and work email**, case-insensitively.
   Implement it with a normalized-column comparison or a `LIKE` on a normalized expression — **not
   `.ToLower()` on the stored column inside `Where`**, which is unindexable
   (`App/GeneralAppArchitecture.md` §8, and the same rule the `Customers` plan applies to legal
   name).
5. **Resolve roles and account statuses with ONE bulk call.**
   `IIdentityApi.FindManyAsync(accountIds)` after the page has been materialised, then map. A
   `FindAsync` per row is an N+1: at the maximum page size of 50 that is 51 queries for one
   request. The bulk method exists specifically for this call site
   ([Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §9.1 rule 9), and its 500-id cap is far
   above the 50-row page.
6. **`Role` and `AccountStatus` are null for accountless Employees**, and the SPA must render that
   as "not invited". Do not substitute `"Employee"` as a default — it would show every accountless
   person as having a role they do not have.
7. Order `family_name ASC, given_name ASC, id ASC`, matching `idx_employees_customer_name`.
   Paginate per §0.5.
8. **No cross-Customer listing is ever exposed to a Customer-side role**, not even one returning an
   empty list. Matrix §12 rule 4.

### 4.4 `GetEmployeeHandler` — all four roles, three projections

`RequireAsync(user, "ViewEmployee")` — all four roles. `EmployeeIdRequestDto` →
`EmployeeDetailDto` **or** `EmployeeSelfDto`, by role.

1. **Projection by role**, per §3.1:
   - `AccountantAdmin`, `AccountantUser`, `CustomerAdmin` → `EmployeeDetailDto`
   - `Employee` → `EmployeeSelfDto`

   **Two separate projections behind one `if`, each selecting only its own columns.** Do not
   project the detail DTO and strip fields: the social-security number then travels through the
   application in a variable that a future maintainer can serialise, with nothing but a comment
   stopping them.

2. **For the `Employee` role, the scope filter is not enough.**

   ```csharp
   var query = _db.Employees.AsNoTracking().WhereInCustomerScope(user);
   if (user.Role == UserRole.Employee)
       query = query.Where(e => e.UserAccountId.ToString() == user.Id);   // see rule 3
   ```

   > `WhereInCustomerScope` narrows an `Employee` to **their Customer**, which is every colleague
   > they work with. Without the second filter, any Employee can read every colleague's tax
   > identification number and social-security number by guessing an id — and the scope test
   > everyone writes (a *different* Customer's Employee returns `404`) **passes**. This is the
   > highest-consequence defect available in this slice, and it is invisible to the obvious test.
   > §16.2 has the case that catches it.

3. **Compare `user.Id` (a `string`) to `UserAccountId` (a `Guid?`) in exactly one place.** Parse
   `user.Id` to a `Guid` once, at the top of the handler, and compare `Guid` to `Guid?`. Do not
   translate `.ToString()` inside the LINQ query — Npgsql will either fail to translate it or
   translate it to a cast that defeats the index. A `Guid.ToString("D")` compared against a
   `"N"`-format string never matches and silently returns `404` for a person's own record.
4. **An out-of-scope or non-self id is `404`**, produced by the query finding nothing. Never
   `403`.
5. **`AsNoTracking()`** — it is a read.
6. **No audit entry.** Reads are not audited in this system except in `Documents`
   (`DocumentDownloaded`). Do not add one here; every list render would write rows.

### 4.5 `InviteEmployeeHandler` — `AA`, `AU`, `CA` for their own Customer

`RequireAsync(user, "InviteEmployee")`. `InviteEmployeeRequestDto` → `EmployeeDetailDto`.

The second half of decision 2. This is where an accountless Employee gains a login.

```
authorize
employee = scoped lookup by req.EmployeeId, else 404

if employee.Status != Active                      → 422 "A departed Employee cannot be invited."
if employee.UserAccountId is not null             → 409 "This Employee already has an account."
if req.Role is AccountantAdmin or AccountantUser  → 422  (rule 3)

loginEmail = req.LoginEmail ?? employee.WorkEmail
if loginEmail is null                             → 422 "No email address on file."

begin transaction
userAccountId = await _identity.InviteEmployeeAccountAsync(new InviteEmployeeAccount(
    EmployeeId: employee.Id,
    CustomerId: employee.CustomerId,
    LoginEmail: loginEmail,
    DisplayName: $"{employee.GivenName} {employee.FamilyName}",
    Role:       req.Role), ct)

employee.UserAccountId       = userAccountId
employee.WorkEmail           = loginEmail          # keep the record consistent
employee.NormalizedWorkEmail = normalize(loginEmail)
employee.UpdatedAt           = now
await _db.SaveChangesAsync(ct)

audit EmployeeInvited (target Employee)
commit
```

Rules:

1. **The invitation, the account, and the `user_account_id` write are one transaction.** A
   committed account with no link back from the Employee row produces an account nobody can find
   and an Employee who can be invited again — reserving the address twice and failing on the unique
   constraint with a message that makes no sense.
2. **`req.Role` must be `CustomerAdmin` or `Employee`.** Anything else is `422`. Matrix §4: *"a
   request setting a role to either Accountant role is **rejected outright**, not silently
   ignored."* `IIdentityApi` guards this too (Identity §9.1 rule 4) — **both guards stay**. This one
   produces a `422` for the user; that one throws for the programmer. They protect against
   different mistakes.
3. **A `CustomerAdmin` may invite somebody as `CustomerAdmin`.** Matrix §4 permits it explicitly:
   *"A Customer Admin can promote another Employee to `CustomerAdmin`."* Do not restrict this to
   Accountants.
4. **`Identity` enforces the system-wide login-email uniqueness, and its `409` must surface as a
   `409`.** Catch it and rethrow with a message naming the address. Do not let it become a `500` —
   `App/GeneralAppArchitecture.md` §8: a client-triggerable value is always a `4xx`. The address
   may already be a login at **another Customer**, so the message must say "that email address is
   already in use" and **must not** say where.
5. **Writing `employee.WorkEmail` from `loginEmail` can violate the per-Customer unique index.**
   Map `23505` to a `409` here too.
6. **Two audit entries, in two slices, and that is correct** (rule G): `Identity` writes
   `AccountInvited` against `UserAccount`, this slice writes `EmployeeInvited` against `Employee`.
   Two things happened.
7. **The notification is `Identity`'s to send.** This handler does not call `INotificationApi`.
   `InviteEmployeeAccountAsync` queues the invitation email, with the token in `EmailBody`
   ([Notifications plan](../Notifications/IMPLEMENTATION_PLAN.md) §1). Sending a second
   notification from here means the invitee gets two emails, one of which has no link.
8. **Nothing is backfilled.** `01-DomainModel.md` §9.5 — LOCKED: the new account *immediately*
   gains read access to every non-`Draft` Ticket where the Employee is the Subject, computed at
   query time from the existing `SubjectEmployeeId`. **If you write an `UPDATE` that stamps the new
   account id onto old Tickets, the model has been misunderstood.** There is no migration step
   here, and this handler must not touch the `tickets` table — it cannot, since it has no
   dependency on `Tickets` and adding one would be a cycle.

### 4.6 `SetEmployeeRoleHandler` — `AA`, `AU`, `CA` for their own Customer

`RequireAsync(user, "SetEmployeeRole")`. `SetEmployeeRoleRequestDto` → `MarkedResultDto`.

Promotion to `CustomerAdmin` and demotion back to `Employee`.

1. **`req.Role` must be `CustomerAdmin` or `Employee`.** An Accountant role is `422`, rejected
   outright. Matrix §4, and §4.5 rule 2.
2. **The Employee must have an account.** No account means no role; `422` telling the caller to
   invite them first.
3. **Self-action is `422`.** Matrix §4: *"A Customer Admin cannot act on their own account's status
   or role — they cannot suspend themselves or remove their own `CustomerAdmin` role."* Compare
   `employee.UserAccountId` with `user.Id` (§8.2).

   Note the asymmetry, which is correct: an **Accountant** may change any Customer-side role
   including that of an Employee at a Customer they are not part of, because they are never
   themselves an Employee. The self check only ever fires for a `CustomerAdmin`.
4. **The at-least-one-`Active`-`CustomerAdmin`-per-Customer guard runs here** — §8.1. Demoting the
   last one is one of the two ways to reach zero.
5. **Delegate the actual change:** `IIdentityApi.SetCustomerSideRoleAsync(accountId, role, ct)`. No
   column in this slice changes — there is no `Employee.Role` (§2.1). The only thing this handler
   writes to `employees` is `UpdatedAt`, and arguably not even that; pick one and be consistent
   with §4.7.
6. **Already-in-that-role is `422`, not a silent `200`.** A no-op success tells the caller something
   happened and writes a misleading audit entry.
7. **Audit `EmployeeRoleChanged`, with `Before` and `After` carrying the two role names.** That
   constant does not exist yet — rule F, and §18 item 1. Without the before/after the log records
   that a role changed but not to what, which makes it useless for the one question it will be
   asked.
8. **The target's live session keeps the old role for up to 8 hours.** Claims are minted at login
   ([Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §17 constraint 1). Demotion therefore fails
   **unsafe**: a demoted Customer Admin keeps administrative powers until their cookie expires.
   Record it in §17. **Do not fix it with a per-request database read in `IPermissionChecker`.**

### 4.7 `DepartEmployeeHandler` — `AA`, `AU`, `CA` for their own Customer

`RequireAsync(user, "DepartEmployee")`. `DepartEmployeeRequestDto` → `MarkedResultDto`.

```
authorize; scoped lookup, else 404
if employee.Status == Departed                    → 422
if self (employee.UserAccountId == user.Id)       → 422   (§8.2)
if req.EmploymentEndDate < employee.EmploymentStartDate → 422

# The guard: departing the last Active Customer Admin reaches zero the same way demoting does.
run the at-least-one-Active-CustomerAdmin guard   (§8.1)

begin transaction
employee.Status            = Departed
employee.EmploymentEndDate = req.EmploymentEndDate
employee.DepartedAt        = now
employee.UpdatedAt         = now

# Decision 3 — 01-DomainModel.md §9.6 rule 2.
if employee.UserAccountId is not null:
    await _identity.SuspendAccountAsync(employee.UserAccountId.Value, ct)

await _db.SaveChangesAsync(ct)
audit EmployeeDeparted
commit
```

Rules:

1. **Departure suspends the account, automatically, in the same transaction.** Decision 3.
   `01-DomainModel.md` §9.6 rule 2: *"A `Departed` Employee's own UserAccount, if they had one, is
   `Suspended` — so they lose their own access."* The matrix lists account suspension as a separate
   operation, and it remains one (§4.8) — but departure implies it, because an active login for
   somebody who has left the company is the exact hole this rule closes. Two operations, one of
   which triggers the other.

   > The reverse is **not** true: suspending an account does not mark the Employee `Departed`.
   > Suspension is temporary and freely reversible; departure is reversible only as a correction
   > (rule 3), and reversing it reactivates the account in the same step.

2. **An already-suspended account is not an error here.** `SuspendAccountAsync` on a suspended
   account must be a no-op rather than a throw, or departing somebody whose access was already
   revoked fails. **Flag this to the `Identity` plan**: its §7.10 rule 4 makes an already-suspended
   *endpoint* call a `422`, which is right for a direct request and wrong for this internal call.
   The `IIdentityApi` method needs the no-op semantics, and the two must not share an
   implementation that only has one behaviour. §18 item 2.

3. **~~`Departed` is terminal — there is no un-depart endpoint.~~ SUPERSEDED 2026-09-02.** The
   original rule read: *matrix §4 has a row for marking an Employee `Departed` and no row for
   reversing it, and matrix §12 forbids inventing permissions the matrix does not grant.* That
   reasoning was correct, and the answer was to change the matrix — which has now happened. Matrix
   §4 carries a **"Reinstate a `Departed` Employee"** row, granted to both Accountant roles and to a
   Customer Admin within their own Customer, and `ReinstateEmployeeHandler` implements it (§4.7a).

   What did **not** change: if somebody genuinely returns to the company, that is a **new Employee
   record** — consistent with §2's rule that the same person at two Customers is two records. Their
   old Tickets stay attached to the old record and stay visible (§9.6). Reinstatement is for a
   departure entered against the wrong record or with the wrong facts, which was the cost this rule
   used to accept and §13 item 3 flagged. Nothing in the server can tell the two apart; the audit
   entry records which one the caller made.

4. **Nothing else changes.** `01-DomainModel.md` §9.6 rule 1: *"No Ticket is hidden, closed,
   reassigned, or deleted because its Subject departed."* This handler does not touch `tickets` —
   it cannot, and must not gain the ability. Their Customer Admin keeps full visibility
   permanently.

5. **A `Departed` Employee may not be the Subject of a new Ticket** (§9.6 rule 3), and that is
   enforced in `Tickets`, via `IEmployeeApi.IsActiveAsync` (§9). Not here. This handler has no way
   to prevent a future ticket and should not try.

6. **`EmploymentEndDate` is required by this endpoint** and may be in the past or the future. It is
   the only place the column is ever written, and `ck_employees_departure` ties it to the status.

7. Audit `EmployeeDeparted`, `Before`/`After` carrying status and end date. `Identity` separately
   audits `AccountSuspended`. Two slices, two entries (rule G).

### 4.7a `ReinstateEmployeeHandler` — `AA`, `AU`, `CA` for their own Customer

**Added 2026-09-02**, resolving §13 item 3. `RequireAsync(user, "ReinstateEmployee")`.
`EmployeeIdRequestDto` → `MarkedResultDto`. Matrix §4, row *"Reinstate a `Departed` Employee"*.

Granted to exactly whoever may enter a departure, including a Customer Admin. The reasoning is in
the matrix: a Customer Admin who can create a state they cannot undo turns every mistake into a
support request, and the Office ends up doing data entry.

1. Scoped lookup, `404` if out of scope. Ordinary `EmployeeQueries.RequireScopedAsync`.
2. **`422` when the Employee has not departed** — *"This employee has not departed."* A no-op is
   refused rather than accepted, for the same reason §4.6 refuses a role change to the current role.
3. **`422` when the Customer is not `Active`.** A suspended Customer gains nobody, and a Customer
   Admin is not exempt (§4.2's rule, same wording).
4. **Clear all three departure fields together:** `Status = Active`, `EmploymentEndDate = null`,
   `DepartedAt = null`. `ck_employees_departure` requires exactly this — an `Active` row with either
   field still set fails the constraint, so a partial reinstatement is a `500`, not a bad row.
5. **Reactivate the account automatically**, when there is one, via
   `IIdentityApi.ReactivateAccountAsync` in the same transaction. This is the user's decision on
   §13 item 3 and it is what makes the operation a real undo: leaving the account suspended would
   produce `Active` employment with no access and a second manual step through §4.8, and §4.8
   *refuses* to run on a `Departed` record — so the caller would have to get the order exactly right
   to recover at all.

   > This is the path that surfaced the `ReactivateAccountAsync` defect described in the decision
   > record: a never-accepted invitee was restored to `Active` with no password hash, which fails
   > every login silently. Fixed in `Identity` ([its plan](../Identity/IMPLEMENTATION_PLAN.md) §9.1
   > rule 14), not here — this handler must not special-case it.

6. **No at-least-one-`Active`-`CustomerAdmin` guard.** The invariant is a floor; reinstating can
   only raise the count. A guard here would be dead code that looks load-bearing.
7. **No `RequireNotSelf`.** A `Departed` Employee's account is suspended, so they cannot
   authenticate to call this — the caller is never the subject. Do not add the check for symmetry
   with §4.7; a guard that cannot fire is a guard nobody can test.
8. **No email-conflict check.** `uq_employees_customer_email` covers **every** status including
   `Departed`, so nobody could have taken the address while they were away. This is a property of
   the index being unconditional on status (§1) — if that ever changes, this rule breaks silently.
9. **No notification.** There is no `EmployeeReinstated` notification kind, deliberately — see
   [the Notifications plan](../Notifications/IMPLEMENTATION_PLAN.md) §3 rule 6. Telling every Admin
   that a departure they may never have seen has been undone is noise.
10. Audit **`EmployeeReinstated`**, a new action, `Before`/`After` carrying the full snapshot.
    **Not `EmployeeEdited`** and not a second `EmployeeDeparted`: this is the entry that
    distinguishes a correction from a re-hire, and it is the only record of which one the caller
    meant. Reusing an existing action name makes the distinction unsearchable. `Identity`
    separately audits `AccountReactivated` (rule G).

### 4.8 `SuspendEmployeeAccountHandler` and `ReactivateEmployeeAccountHandler`

`RequireAsync(user, "SuspendEmployeeAccount")` / `"ReactivateEmployeeAccount"` — `AA`, `AU`, and
`CA` for their own Customer. `EmployeeIdRequestDto` → `MarkedResultDto`.

**Two handlers, not one with a status parameter**, for the same reason as the equivalent pair in
[the Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §7.11: the guards differ, and a single
handler with an `if (suspending)` inside it is where one of them eventually goes missing.

Suspend:

1. Scoped lookup, `404` if out of scope.
2. **No account is `422`**, not `404` — the Employee exists, there is just nothing to suspend.
3. **Self-suspension is `422`** (§8.2). Matrix §4's first constraint.
4. **The at-least-one-`Active`-`CustomerAdmin` guard runs** (§8.1). Suspending the last Customer
   Admin's account is the third way to reach zero, alongside demoting and departing.
5. Delegate to `IIdentityApi.SuspendAccountAsync`. **`Identity` audits it**; this slice does not
   (rule G) — no `employees` row changed.
6. **Does not mark the Employee `Departed`** (§4.7 rule 1, reverse direction).
7. `Identity` sends the `AccountSuspended` notification (in-app only — that kind is not in
   `Emailed`). This handler does not call `INotificationApi`.

Reactivate:

1. Same scope and no-account rules.
2. **No self check needed** — a caller cannot have suspended themselves (rule 3), so they cannot be
   reactivating themselves.
3. **No invariant guard** — reactivation cannot reduce the Customer Admin count.
4. **`Departed` Employees cannot have their account reactivated.** `422`, with a message saying so.
   §9.6 rule 2 makes the suspension a *consequence* of departure; reactivating the account while
   the Employee is `Departed` restores access to somebody who has left. **This is the rule most
   likely to be omitted**, because it is a cross-check between two different pieces of state.

   **This stays a `422` now that §4.7a exists.** The original justification was that departure was
   terminal so there was no path back to a consistent state; the surviving one is narrower and still
   decisive — this endpoint would produce `Departed` employment with `Active` access, a pair nothing
   else in the slice can produce and nothing downstream expects. `/reinstate` reactivates the
   account itself, as one operation on one consistent state, so the correct answer to *"reactivate a
   departed person"* is *"reinstate them"*. The message says so: *"A departed employee's account
   cannot be reactivated. Reinstate them if the departure was recorded by mistake, or register them
   again if they have returned."*
5. **Reactivation does not reset a password or clear a lockout** ([Identity plan](../Identity/IMPLEMENTATION_PLAN.md)
   §7.11 rule 2). A returning person who has forgotten their password uses the reset flow.

### 4.9 `UpdateEmployeeHandler` — `AA`, `AU`, `CA` for their own Customer

`RequireAsync(user, "UpdateEmployee")`. `UpdateEmployeeRequestDto` → `EmployeeDetailDto`.

1. **Editable:** given name, family name, work email, contact phone, job title, tax identification
   number, social-security number, employment start date.
2. **Not editable, ever:** `CustomerId` (immutable, §1), `UserAccountId` (set by §4.5 only),
   `Status` (§4.7 only), `EmploymentEndDate` (§4.7 only), `DepartedAt`. **The DTO must not have
   properties for them.** A property that exists is a property somebody binds.
3. **A `Departed` Employee's record is still editable.** Correcting a misspelled name or a wrong
   tax number after departure is ordinary work, and the record is retained forever. Do not block
   it.
4. **Changing the work email does not change the login email.** They are separate columns in
   separate slices, and this endpoint touches only `employees`. **State this in the response or
   the API description**, because it will otherwise be reported as a bug: an Admin edits the work
   email, the person's login still uses the old address, and nothing said so. **RESOLVED since
   this plan was written** (§13 item 4): a login email *is* now changeable, by an Accountant only,
   through `/api/employees/change-login-email` — so say which endpoint to use rather than that it
   cannot be done.
5. **A work-email change is subject to the per-Customer unique index.** Map `23505` to `409`.
6. **`EmploymentStartDate` cannot be moved past an existing `EmploymentEndDate`** —
   `ck_employees_dates` will reject it, so pre-check and return `422` with a real message.
7. Audit `EmployeeEdited`, with `Before`/`After` listing **which fields changed, not their
   values**, for the two personal identifying numbers. §4.2 rule 7: a tax identification number in
   an audit row is retained forever in a table nobody purges. Names, titles, and phone numbers
   may carry values.
8. `UpdatedAt = now`.

### 4.10 `UpdateOwnContactHandler` — `Employee` and `CustomerAdmin`, own record only

`RequireAsync(user, "UpdateOwnContact")`. `UpdateOwnContactRequestDto` → `EmployeeSelfDto`.

Matrix §4: an `Employee` may edit *"own contact details only"*.

1. **The DTO has no target** (§3.2). The Employee row is found by
   `UserAccountId == parse(user.Id)`, with `.WhereInCustomerScope(user)` also applied — belt and
   braces, and free.
2. **Exactly two editable fields: `ContactPhone` and `WorkEmail`.** Not the name, not the job
   title, not the dates, and above all not the personal identifying numbers. "Contact details"
   means how to reach them.
3. **No Employee record for the caller is `404`** — which happens for an Accountant, who has no
   Employee record at all (§0.1). The catalogue should therefore list only `CustomerAdmin` and
   `Employee` for this action, so an Accountant gets a clean `403` instead of a confusing `404`.
4. **A `Departed` Employee cannot use this endpoint** — their account is `Suspended`, so they
   cannot log in. No explicit check is needed, and adding one is harmless; note which you chose.
5. **A work-email change here is subject to the same unique index and the same `409`**, and to the
   same caveat as §4.9 rule 4: it does not change their login email. For a self-service endpoint
   that caveat is more confusing, not less — the person will assume they just changed how they log
   in. Surface it in the response message, and now that a login email *can* be changed (§13 item 4,
   resolved), the message must also say who to ask: the accounting office, because this person is
   not authorized to change it and never will be.
6. Audit `EmployeeEdited`, actor being the Employee themselves.

### 4.10a `ChangeEmployeeLoginEmailHandler` — **`AA` and `AU` only**

**Added 2026-09-02**, resolving §13 item 4. `RequireAsync(user, "ChangeEmployeeLoginEmail")`.
`ChangeEmployeeLoginEmailRequestDto` → `MarkedResultDto`. Matrix §4, row *"Change an Employee's
login email"* — the **one** row in that section a Customer Admin is refused.

**Why it is Accountant-only.** Whoever can move an account to a new address can move it to a
mailbox they control. A Customer Admin doing it to a colleague is account takeover one step removed,
and the colleague is the person who then cannot log in. Routing it through the Office puts a human
outside the Customer in the loop and names them in the audit entry. **Nobody may change their own**,
including an Accountant Admin — see [the matrix](../../02-AuthorizationMatrix.md) §4 and
[the Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §18 item 6.

**Why it lives in this slice and not `Identity`.** The account is `Identity`'s data and `Identity`
does the write — but the authorization question is *"may this caller act on this Employee"*, which
needs the Customer scope only this slice has. `Identity` would have to be handed the scope to check
it, and a contract taking a `CurrentUser` is a contract whose caller can lie about it.

1. Scoped lookup, `404` if out of scope. The catalogue lists only the two Accountant roles, so a
   Customer Admin gets `403` before any lookup happens.
2. **`422` when the Employee has no account** — *"This employee has no account, so there is no
   sign-in address to change. Invite them first."* Not `404`: the Employee exists and the caller may
   see them; there is simply nothing whose address could change. The message names the fix rather
   than leaving the caller to infer it from a status code.
3. **`422` when the Employee has departed** — *"This employee has departed."* Matching §4.8's
   reactivate rule: their account is suspended, so a new sign-in address changes nothing anybody
   could use. Reinstate first (§4.7a).
4. **Delegate the write to `IIdentityApi.ChangeLoginEmailAsync`**, inside this handler's
   transaction. `Identity` normalizes the address, refuses a duplicate with `409` (system-wide
   uniqueness, and the message deliberately does not say **where** the collision is — §4.5's rule),
   throws for an Accountant target, and audits `LoginEmailChanged` against `UserAccount`.
5. **The `employees` row is not touched.** `WorkEmail` is contact information and this call named
   only the sign-in address. Rewriting a field the caller did not mention is the kind of
   helpfulness that loses data — and the two addresses are legitimately different for anybody whose
   contact address is a shared mailbox. Changing the work email is §4.9.
6. **It touches neither the password nor any session.** The person keeps what they know, and their
   live cookie stays valid under the new address for up to 8 hours (there is no session revocation —
   [Identity plan](../Identity/IMPLEMENTATION_PLAN.md) constraint 1). Do not add a forced password
   reset: it turns a clerical fix into a lockout.
7. Audit **`LoginEmailChanged`** a second time in this slice, targeting **`AuditTargets.Employee`**
   with the Employee's id. `Identity`'s entry targets the account; somebody investigating *"what
   happened to this person"* searches by Employee id, and an entry findable only by account id is an
   entry they will not find. Two entries for one action is correct (rule G) — two things happened,
   in two slices.

   > **Both addresses are recorded in full.** A login email is not a personal identifying number, so
   > §4.2 rule 7's which-fields-not-values rule does not apply. Which address it was and which it
   > became is the entire content of the entry.

8. **No notification.** There is no catalogue kind for it. Emailing the *new* address is useless
   for detecting a mistake and emailing the *old* one requires copy this plan has not specified —
   flag it if wanted rather than inventing a kind (`NotificationEvents.All` rejects one anyway).

### 4.11 `EmployeeMapper`

One static class with the three projections, so the field lists exist once:

- `ToSummaryExpression` — `Expression<Func<Employee, EmployeeSummaryDto>>`, with `Role` and
  `AccountStatus` left null and filled after the bulk `IIdentityApi` call (§4.3 rule 5)
- `ToDetailExpression` — includes the personal identifying numbers
- `ToSelfExpression` — excludes them

**They must be `Expression<...>`, not `Func<...>`.** A `Func` forces client-side evaluation: EF
fetches every column of every row and projects in memory, so the "narrower" self projection reads
the social-security number out of the database anyway. The whole point of §3.1 is that the
sensitive columns are never selected.

---

## 5. The at-least-one-`Active`-`CustomerAdmin` invariant

Matrix §4, second constraint: ***"A Customer must always retain at least one `Active` Customer
Admin. Any operation leaving zero is rejected. Only an Accountant can resolve such a
situation."***

`01-DomainModel.md` §2 calls this the mirror of the identical Accountant Admin rule.

**Three operations can reach zero**, and it is easy to guard one or two and miss the third:

| Operation | How it reaches zero |
|---|---|
| §4.6 `SetEmployeeRole` — demote to `Employee` | The role goes away |
| §4.7 `DepartEmployee` | The Employee leaves and their account is suspended |
| §4.8 `SuspendEmployeeAccount` | The account stops being `Active` |

Put the guard in **one file**, `Application/EmployeeInvariants.cs`, called by all three. Do not
copy it.

### 5.1 The guard, and why it is harder here than in `Identity`

`Identity`'s equivalent guard counts rows in its own table
([Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §8.1). **This one cannot**, because the two
halves of "Active Customer Admin" live in two slices:

- The **role** `CustomerAdmin` is on `user_accounts`, owned by `Identity`
- The **`Active` account status** is on `user_accounts`, owned by `Identity`
- The **Customer** the person belongs to is on `employees`, owned by this slice

So the count is a join across a slice boundary, which is exactly what dependency rule 3 forbids.
The resolution:

```csharp
// 1. This slice: every Employee of the Customer that has an account.
var accountIds = await db.Employees
    .Where(e => e.CustomerId == customerId
             && e.Status == EmployeeStatus.Active
             && e.UserAccountId != null)
    .Select(e => e.UserAccountId!.Value)
    .ToListAsync(ct);

// 2. Identity: which of those are Active CustomerAdmins. One bulk call.
var accounts = await identity.FindManyAsync(accountIds, ct);
var remaining = accounts.Values.Count(a =>
    a.Role == UserRole.CustomerAdmin && a.IsActive && a.Id != excludedAccountId);

if (remaining == 0)
    throw new AppException("This Customer must always have at least one active Customer Admin.", 422);
```

Rules:

1. **`FindManyAsync`, not a loop.** A Customer with 200 Employees would otherwise make 200 queries
   on every demotion. The 500-id cap (Identity §9.1 rule 9) is a real ceiling — **a Customer with
   more than 500 accounted Employees breaks this guard with an `InvalidOperationException`**. Flag
   it (§18 item 5); do not silently take the first 500, which would let the count reach zero
   undetected.
2. **`excludedAccountId` is the account being demoted, suspended, or departed**, and it is excluded
   from the count rather than the guard being run after the change. This is the opposite choice to
   Identity §8.1, which counts after mutating — and the difference is forced: the change here
   happens in *another slice*, through `IIdentityApi`, so there is no locally tracked entity whose
   pending state a count could see. **Write down which approach this file uses**, because the two
   plans differ and a builder reading both will otherwise mix them and get a guard that always
   passes.
3. **The condition is `CustomerAdmin` **and** `Active` account **and** `Active` Employee.** All
   three. Counting `CustomerAdmin`s of any status passes when the only one left is `Suspended` or
   `Departed` — nobody can log in, and matrix §4 says only an Accountant can resolve it, which
   means a support call. That unrecoverable state is what this guard exists for.
4. **`422`, not `403`.** The caller has the role; the data's state forbids the operation. A `403`
   would suggest re-authenticating as somebody more powerful.
5. **It runs inside the handler's transaction**, so a rejection rolls back everything including the
   `IIdentityApi` call.
6. **It applies to Accountant callers too.** An `AccountantUser` demoting a Customer's last Admin
   creates the same hole. The guard is about the data, not the caller — matrix §4's *"Only an
   Accountant can resolve such a situation"* describes the **recovery** path, not an exemption from
   the rule.
7. **Concurrency:** two callers demoting two different Customer Admins simultaneously can both
   pass a count taken in separate transactions and commit to zero. Under `READ COMMITTED` this is a
   real interleaving, and it is **worse here than in `Identity`** because the count reads a table
   this transaction does not lock. Accept it and record it in §17, or take a row lock — but do not
   leave it unmentioned. Unlike the Accountant case, this one **is** recoverable, by an Accountant.

### 5.2 The self check

```csharp
if (employee.UserAccountId is { } accountId
    && string.Equals(accountId.ToString(), user.Id, StringComparison.Ordinal))
    throw new AppException("You cannot change your own role or account status.", 422);
```

Matrix §4, first constraint: *"A Customer Admin cannot act on their own account's status or role —
they cannot suspend themselves or remove their own `CustomerAdmin` role. This prevents a Customer
locking itself out."*

1. **Applies to §4.6 (role), §4.7 (departure), and §4.8 (suspend).** Not to §4.10, which is the
   endpoint for acting on yourself and is explicitly permitted.
2. **It compares against `employee.UserAccountId`, not `employee.Id`.** The caller's `user.Id` is
   an *account* id. Comparing it to an Employee id never matches, so the guard silently never
   fires — and it looks completely correct in review. This is the single most likely way to build
   this check so that it does nothing.
3. **One place, one format.** `Guid.ToString()` on both sides, in this file only. A `"D"`-format
   string compared to an `"N"`-format one never matches, with the same silent result as rule 2.
4. **It fires only for Customer-side callers**, since an Accountant has no Employee record and
   therefore never matches. No role check is needed inside the guard.

---

## 6. Cross-slice boundaries

`Employees` may depend on **`Customers`, `Identity`, `Notifications`, `Audit`**
([03-SliceInventory.md](../../03-SliceInventory.md) §2) — four of the seven, more than any slice
except `Tickets`.

| It calls | For | Not for |
|---|---|---|
| `ICustomerApi.CreateAsync` | §4.1 step 1 — decision 4 | Anything else. It never inserts into `customers`. |
| `ICustomerApi.IsActiveAsync` | Refusing to register an Employee at a suspended Customer (§4.2) | Deciding whether somebody may log in — that is `Identity`'s check |
| `ICustomerApi.FindAsync` / `FindManyAsync` | Customer names on an Accountant's cross-Customer list | Reading a tax number or address — `CustomerSummary` has neither |
| `IIdentityApi.InviteEmployeeAccountAsync` | §4.1, §4.5 | — |
| `IIdentityApi.SetCustomerSideRoleAsync` | §4.6 | Setting an Accountant role — it throws |
| `IIdentityApi.SuspendAccountAsync` / `ReactivateAccountAsync` | §4.7, §4.8 | — |
| `IIdentityApi.FindManyAsync` | §4.3 rule 5, §5.1 rule 1 | Reading a hash or lockout state — `AccountSummary` has neither |
| `IAuditApi` | Every write in §4.0 F | — |
| `INotificationApi` | **Nothing, in v1** — see below | — |

It is called by **`Tickets`**, through `IEmployeeApi` (§9).

Five boundary rules:

1. **`Employees` never references `Tickets` or `Documents`.** Both depend on it (or on nothing).
   §4.5 rule 8 and §4.7 rule 4 are the two places a builder will want to, and both are explicitly
   forbidden by the domain rules themselves.
2. **`Employees` never names another slice's `Core` types** — not `Customer`, not `UserAccount`, not
   `CustomerStatus`. It uses `CustomerSummary`, `AccountSummary`, and `bool`. Dependency rule 2,
   and `App/GeneralAppArchitecture.md` §5 has a worked example of exactly this mistake.
3. **`Employees` has a `Notifications` dependency it does not currently use.** Every notification
   this slice's operations produce is sent by `Identity`, from inside `IIdentityApi` (§4.5 rule 7,
   §4.8 rule 7). The edge exists in the dependency table and the registration may import it, but
   **do not add a `NotifyAsync` call to make the dependency look used.** Two invitation emails is
   worse than an unused edge. If a genuinely Employee-owned notification appears later — "your
   record was updated", say — this is where it goes.
4. **`Employees` does not implement any inverted interface.** The one inverted dependency in v1 is
   `IRecipientDirectory`, defined by `Notifications` and implemented by `Identity`
   ([03-SliceInventory.md](../../03-SliceInventory.md) §3 rule 7). If you find yourself wanting
   `Employees` to define an interface for `Tickets` to implement, stop and flag it.
5. **`Employees` does not read `user_accounts` and cannot.** Everything about an account comes
   through `IIdentityApi`, including in §5.1 where a join would be far simpler. That simplicity is
   the trap: a join makes the two schemas one schema, and `Identity`'s migration then cannot change
   without breaking this slice's queries.

---

## 7. Migrations — SQL scripts, not `dotnet ef`

**File:** `Slices/Employees/Infrastructure/Migrations/20260902_001_CreateEmployeesSchema.sql`

- `YYYYMMDD_###_Description.sql`. The sequence number restarts at `001` **per slice**, which is why
  the runner tracks the **slice-relative path with forward slashes**, never `Path.GetFileName`
  (`App/GeneralAppArchitecture.md` §6 — LOCKED). This slice's `..._001_...` and `Audit`'s are
  different rows in `schema_versions`.
- **Never `dotnet ef migrations add`.** If a `Migrations/` folder with C# files appears, delete it.
- One script: the table, all three `CHECK` constraints, all four indexes.
- **No rollback script.** Append-only; a mistake is fixed by a new script.
- Set the build action so the file is copied to the output directory, or the runner finds nothing
  and every query fails with `42P01: relation "employees" does not exist`.

---

## 8. Endpoints

`EmployeesEndpoints.cs` at the slice root. **Two route groups**, and the second one is the
surprise.

### 8.1 `/api/employees/*`

| Method | Route | Handler | Roles |
|---|---|---|---|
| `POST` | `/api/employees/register` | `RegisterEmployeeHandler` | AA, AU, CA |
| `POST` | `/api/employees/list` | `ListEmployeesHandler` | AA, AU, CA |
| `POST` | `/api/employees/get` | `GetEmployeeHandler` | AA, AU, CA, EMP |
| `POST` | `/api/employees/update` | `UpdateEmployeeHandler` | AA, AU, CA |
| `POST` | `/api/employees/update-own-contact` | `UpdateOwnContactHandler` | CA, EMP |
| `POST` | `/api/employees/invite` | `InviteEmployeeHandler` | AA, AU, CA |
| `POST` | `/api/employees/set-role` | `SetEmployeeRoleHandler` | AA, AU, CA |
| `POST` | `/api/employees/depart` | `DepartEmployeeHandler` | AA, AU, CA |
| `POST` | `/api/employees/reinstate` | `ReinstateEmployeeHandler` | AA, AU, CA |
| `POST` | `/api/employees/change-login-email` | `ChangeEmployeeLoginEmailHandler` | **AA, AU only** |
| `POST` | `/api/employees/suspend-account` | `SuspendEmployeeAccountHandler` | AA, AU, CA |
| `POST` | `/api/employees/reactivate-account` | `ReactivateEmployeeAccountHandler` | AA, AU, CA |

The last two rows added 2026-09-02 (§13 items 3 and 4). `change-login-email` is the only route in
this group a Customer Admin cannot call.

### 8.2 `/api/customers/onboard` — registered here, on purpose

| Method | Route | Handler | Roles |
|---|---|---|---|
| `POST` | `/api/customers/onboard` | `OnboardCustomerHandler` | **AA only** |

Rules:

1. **Multi-word segments are kebab-case** — `update-own-contact`, `set-role`, `suspend-account`,
   `reactivate-account`. `App/GeneralAppArchitecture.md` §8, LOCKED. The stated reason applies
   directly to `suspend-account`: a doubled letter across a word boundary is easy to typo and
   invisible in review.
2. **A comment at the `/api/customers/onboard` registration site** naming
   [03-SliceInventory.md](../../03-SliceInventory.md) §1 and saying in one line why a
   `/api/customers/*` route is registered from `EmployeesEndpoints.cs`. §0.6 point 2. Without it
   the next person to touch either slice "tidies" it into `Customers` and creates the cycle.
3. **No route parameters, anywhere.** Not `/api/employees/{id}/depart`. Ids go in the body.
   `App/GeneralAppArchitecture.md` §8.
4. **Everything is a `POST`, including the reads.** Request DTOs go in the body, not a query
   string, consistently with every other slice.
5. **There is no `DELETE` endpoint.** Matrix §4: *"Delete an Employee record — **Nobody.**"*
6. **There IS an un-depart endpoint** — `/api/employees/reinstate`, added when §13 item 3 was
   resolved. It is a correction, not a re-hire: matrix §4 now carries the row. There is still **no
   move-between-Customers endpoint** (§1), and that one is not open to reconsideration — a move
   would make `user_accounts.customer_id` go stale.
7. **There IS an endpoint that changes a login email** — `/api/employees/change-login-email`, added
   when §13 item 4 was resolved. Accountant roles only; matrix §4 carries the row.
8. **`.Produces<T>(200)` and `.ProducesProblem(...)` on every route**, so the generated OpenAPI
   document is usable by the SPA. `/api/employees/get` returns two different shapes by role —
   document the union or document the detail shape and note the narrowing; do not silently declare
   one.

---

## 9. The `IEmployeeApi` contract

**Files:** `Slices/Employees/ExternalInterfaces/IEmployeeApi.cs`, `EmployeeApi.cs`

One slice calls this: **`Tickets`**. [03-SliceInventory.md](../../03-SliceInventory.md) §3 rule 3
describes the shape: *"`Employees` exposes something like an employee summary — identifier, name,
owning Customer, whether they have an account — not the `Employee` entity."*

```csharp
public sealed record EmployeeSummary(
    Guid Id,
    Guid CustomerId,
    string GivenName,
    string FamilyName,
    string Status,
    bool HasAccount,
    Guid? UserAccountId)
{
    public bool IsActive => Status == "Active";
    public string FullName => $"{GivenName} {FamilyName}";
}

public interface IEmployeeApi
{
    /// <summary>Null when no such Employee exists. Applies NO scope filter — the caller
    /// authorizes. See rule 4.</summary>
    Task<EmployeeSummary?> FindAsync(Guid employeeId, CancellationToken ct = default);

    /// <summary>Bulk lookup for list rendering. Missing ids are absent. Capped at 500.</summary>
    Task<IReadOnlyDictionary<Guid, EmployeeSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> employeeIds, CancellationToken ct = default);

    /// <summary>True only when the Employee exists AND is Active. This is what Tickets asks
    /// to enforce 01-DomainModel.md §9.6 rule 3.</summary>
    Task<bool> IsActiveAsync(Guid employeeId, CancellationToken ct = default);

    /// <summary>The Employee belonging to an account, or null. This is how Tickets resolves
    /// "which Employee is the caller" to compute Subject-based read access.</summary>
    Task<EmployeeSummary?> FindByAccountAsync(Guid userAccountId, CancellationToken ct = default);

    /// <summary>Active Employees of one Customer, for a Subject picker. Unpaginated;
    /// see rule 6.</summary>
    Task<IReadOnlyList<EmployeeSummary>> ListActiveByCustomerAsync(
        Guid customerId, CancellationToken ct = default);
}
```

Rules:

1. **It returns `EmployeeSummary`, never the `Employee` entity** (dependency rule 4). A caller
   holding a tracked entity could mutate it and save it through another slice's context.
2. **`EmployeeSummary` carries no tax identification number, no social-security number, no work
   email, no phone, and no employment dates.** Nothing in `Tickets` needs them, and an
   `ExternalInterface` that carries a social-security number makes every consumer a disclosure
   path. This is the same restriction `CustomerSummary` has for tax numbers
   ([Customers plan](../Customers/IMPLEMENTATION_PLAN.md) §5) and `AccountSummary` has for hashes.
3. **`CustomerId` **is** on the summary, and that is deliberate**: `Tickets` needs it to enforce its
   own Customer scope on a Ticket's Subject, and it is not sensitive.
4. **It applies no scope filter, and the caller must.** `IEmployeeApi` is called on behalf of every
   role including Accountants, so a filter here would either break Accountant reads or silently
   depend on a `CurrentUser` this contract does not take. **Document this loudly at the interface**,
   because it is the opposite of the rule inside the slice (§4.0 E) and a `Tickets` handler that
   forgets is a cross-Customer read. `ICustomerApi` makes the same choice for the same reason.
5. **`IsActiveAsync` returns `false` for an unknown id**, never throws and never `true`.
   Fail-closed: `Tickets` uses it to refuse a new Ticket for a departed Subject, and a `?? true`
   anywhere in that chain lets one through.
6. **`ListActiveByCustomerAsync` is unpaginated and is not `ListEmployeesHandler`.** The handler
   serves an authorized HTTP request and returns a paginated, role-shaped DTO; this returns a list
   for another slice's picker. **They must not share a return type**, or the handler's field
   restrictions (§3.1) become something `Tickets` can bypass by calling the other one.

   Its unboundedness is a real risk: a Customer with 5,000 Employees returns 5,000 rows. **Flag
   it** (§18 item 6) rather than silently capping, because a silent cap makes a Subject
   un-pickable with no error.
7. **`FindManyAsync` caps at 500 ids** and throws `InvalidOperationException` above it, matching
   `ICustomerApi` and `IIdentityApi`. It exists so `Tickets` does not loop when rendering a page.
8. **It caches nothing.** `IsActiveAsync` is how a departure takes effect in `Tickets`, and a
   status change is precisely the event a cache would hide.
9. **It is read-only.** There is no `RegisterAsync`, no `DepartAsync`, and no write method of any
   kind — unlike `ICustomerApi`, which gains one in §4.1. The difference is that a real call site
   needed that one; nothing needs a write here. **Do not add one pre-emptively**; a write method on
   this contract is a way for `Tickets` to change Employee records, which no rule in the matrix
   authorizes.
10. **It writes no audit entries.** These are reads.

---

## 10. Service registration

### 10.1 `Slices/Employees/EmployeesRegistration.cs`

```csharp
public static IServiceCollection AddEmployeesSlice(
    this IServiceCollection services, IConfiguration configuration)
{
    // The SHARED request connection overload. See 10.4 rule 1.
    services.AddDbContext<EmployeesDbContext>((serviceProvider, options) =>
        options.UseNpgsql(serviceProvider.GetRequiredService<RequestConnection>().Connection));

    services.AddSingleton<IActionCatalogue, EmployeesActionCatalogue>();

    services.AddTransient<OnboardCustomerHandler>();
    // … the other ten handlers …

    services.AddScoped<IEmployeeApi, EmployeeApi>();

    return services;
}
```

### 10.2 `Slices/Employees/EmployeesActionCatalogue.cs`

```csharp
public sealed class EmployeesActionCatalogue : IActionCatalogue
{
    public string SliceName => "Employees";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } = new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
    {
        // The composite operation — AccountantAdmin only, because creating a Customer is.
        ["OnboardCustomer"] = [UserRole.AccountantAdmin],

        ["RegisterEmployee"]          = [AA, AU, CustomerAdmin],
        ["ListEmployees"]             = [AA, AU, CustomerAdmin],
        ["ViewEmployee"]              = [AA, AU, CustomerAdmin, UserRole.Employee],
        ["UpdateEmployee"]            = [AA, AU, CustomerAdmin],
        ["UpdateOwnContact"]          = [CustomerAdmin, UserRole.Employee],
        ["InviteEmployee"]            = [AA, AU, CustomerAdmin],
        ["SetEmployeeRole"]           = [AA, AU, CustomerAdmin],
        ["DepartEmployee"]            = [AA, AU, CustomerAdmin],
        ["SuspendEmployeeAccount"]    = [AA, AU, CustomerAdmin],
        ["ReactivateEmployeeAccount"] = [AA, AU, CustomerAdmin],
    };
}
```

Rules:

1. **`OnboardCustomer` is `AccountantAdmin` only**, and it is the only entry that is. §0.6 point 1.
   Every other entry includes `AccountantUser`, because matrix §4 gives `AU` everything `AA` has in
   this domain — `01-DomainModel.md` §2 lists the four powers reserved to `AA`, and none of them is
   an Employee operation.
2. **`UpdateOwnContact` excludes both Accountant roles** — §4.10 rule 3. An Accountant has no
   Employee record, so a clean `403` beats a confusing `404`.
3. **`ViewEmployee` is the only entry with all four roles**, and the *scoping* difference between
   them lives in the handler (§4.4 rule 2), because the catalogue can express "who may call", not
   "which rows".
4. **Action names are globally unique.** `ViewEmployee` and `ListEmployees`, not `View` and `List`.
   Two catalogues declaring one name is a startup failure naming both slices, which is the designed
   behaviour but a confusing first symptom.
5. **No empty role arrays** — the composer fails startup on one, deliberately.

### 10.3 What `Program.cs` adds

Exactly two lines:

```csharp
builder.Services.AddEmployeesSlice(builder.Configuration);
// …
app.MapEmployeesEndpoints();
```

Assembly scanning is banned; `Program.cs` contributes two lines per slice and nothing more.

### 10.4 Registration traps

1. **`AddDbContext` must use the `(serviceProvider, options)` overload and `RequestConnection`.**
   The plain `options => options.UseNpgsql(connectionString)` overload compiles, passes every
   single-slice test, and silently gives this slice its **own connection** — at which point the
   composite onboarding operation is three transactions, not one, and a failure at step 3 leaves a
   Customer behind. **Nothing fails, and the test that would catch it is §16.1's.** This is the
   most damaging registration mistake available in this slice, because it defeats the entire reason
   the slice owns that endpoint.
2. **Never `AddScoped<EmployeesDbContext>()`.** It bypasses the options pipeline and the context
   gets no provider.
3. **`IEmployeeApi` is `AddScoped`, not `AddSingleton`** — it holds a scoped DbContext. A singleton
   would capture one context for the process lifetime and fail on every request after the first
   connection died.
4. **Register the catalogue as `IActionCatalogue`**, not the concrete type. `PermissionChecker`
   takes `IEnumerable<IActionCatalogue>`; a concrete registration is never seen, every action in it
   is absent, and **every endpoint in this slice returns `403`**.
5. **Handlers are `AddTransient`.** They hold no state between requests.
6. **This slice must be registered *after* `Customers`, `Identity`, and `Notifications` in
   `Program.cs`** — not because DI cares about order, but because a missing
   `AddCustomersSlice`/`AddIdentitySlice` line surfaces here as an unresolvable `ICustomerApi` or
   `IIdentityApi` at the first request rather than at startup. Consider a startup assertion that
   both resolve.

### 10.5 Startup smoke check — before writing any test

```bash
# 1. Onboard a Customer end to end. One call, three slices.
curl -sb jar.txt -X POST localhost:5000/api/customers/onboard \
  -H 'Content-Type: application/json' \
  -d '{"customer":{"legalName":"Acme SA","taxNumber":"123456789"},
       "firstAdmin":{"givenName":"Maria","familyName":"P","workEmail":"maria@acme.example",
                     "employmentStartDate":"2026-09-01"}}'
#    expect 200 with customerId, employeeId, userAccountId

# 2. The Customer, the Employee, and the account all exist.
curl -sb jar.txt -X POST localhost:5000/api/employees/list -d '{}'

# 3. Now make it fail. Repeat step 1 with the SAME workEmail.
#    expect 409 — and then assert the SECOND customer row does NOT exist:
psql -c "select count(*) from customers where legal_name = 'Acme SA';"
#    expect 1, not 2
```

Step 3 is the one to actually run. It is the only check that proves the shared-connection
transaction works, and it fails when trap 1 was overlooked.

---

## 11. Tests

### 11.1 At least one test must run against real PostgreSQL — mandatory

`Microsoft.EntityFrameworkCore.InMemory` is banned from the API project and permitted only in the
test project ([03-SliceInventory.md](../../03-SliceInventory.md) §5). In-memory cannot see this
slice's most important behaviour:

- All three `CHECK` constraints
- The two **unique partial** indexes, so the `409` paths are untestable
- `PostgresException` with `SqlState == "23505"`, which the duplicate handling catches by value
- **The cross-slice transaction**, which is the whole point of §4.1 — in-memory has no real
  transaction and no shared connection, so a rollback test passes vacuously

So: a real-PostgreSQL test covering, at minimum, a failed onboard leaving **no** Customer row, a
duplicate work email within one Customer (`409`), the same work email at two *different* Customers
(succeeds), and inserting a `Departed` Employee with a null `departed_at` (constraint violation).

> Docker is currently not starting on this machine, so no PostgreSQL exists and **no part of this
> schema has ever been applied**. Every SQL statement in §1 and §7 is unverified. When Docker
> works, apply the migration first and fix the script before trusting any of this plan's DDL.

### 11.2 Behavioural cases

| Case | Expected |
|---|---|
| `AA` onboards a Customer | `200`; Customer, Employee, and `CustomerAdmin` account all exist |
| The onboarded first admin's role | `CustomerAdmin`, **not** `Employee` |
| `AU` calls `/api/customers/onboard` | `403` |
| `CA` calls it | `403` |
| Onboard where the invitation step fails | `409`/`500` **and the `customers` table is unchanged** |
| Onboard with no `firstAdmin.workEmail` | `422` |
| Onboard response | contains no token |
| `register` an accountless Employee | `200`; `userAccountId` is null; **no email sent, no account row** |
| `register` at a suspended Customer | `422` |
| `register` at an unknown Customer | `422`, not `500` |
| `CA` registers at another Customer | `403` |
| `register` a duplicate work email at the same Customer | `409`, not `500` |
| `register` the same work email at a **different** Customer | `200` — not globally unique |
| `list` as `CA` | only their own Customer's Employees |
| `list` as `CA` passing another `customerId` | `403`, **not** an empty page |
| `list` as `EMP` | `403` |
| `list` default | includes `Departed` Employees |
| `list` with 60 Employees and `pageSize: 5000` | 50 rows, `200` |
| `list` role/status resolution | **one** `IIdentityApi` call, not one per row |
| `list` of accountless Employees | `role` and `accountStatus` are null, not `"Employee"` |
| **`get` as `EMP` for a colleague at their own Customer** | **`404`** — the §0.2 defect |
| `get` as `EMP` for their own record | `200`, `EmployeeSelfDto` |
| `get` as `EMP` — response JSON | **no `socialSecurityNumber` key, no `taxIdentificationNumber` key** |
| `get` as `CA` for their own Customer's Employee | `200`, `EmployeeDetailDto` with both numbers |
| `get` as `CA` for another Customer's Employee | `404`, not `403` |
| `get` as `AA` for any Employee | `200`, detail |
| `invite` an accountless Employee | `200`; account created; `userAccountId` written on the row |
| `invite` an already-accounted Employee | `409` |
| `invite` a `Departed` Employee | `422` |
| `invite` with `role: "AccountantAdmin"` | `422` |
| `invite` with an email already a login **at another Customer** | `409`, and the message names no other Customer |
| `invite` with no email on file and none supplied | `422` |
| `invite` where the account creation fails | **no `user_account_id` written** |
| After `invite`, the Employee's pre-existing Tickets | readable by the new account, **with no `UPDATE` to `tickets`** |
| `set-role` to `AccountantUser` | `422` |
| `set-role` on an accountless Employee | `422` |
| `CA` demotes themselves | `422` |
| `AA` demotes a `CA` at a Customer with two Active Admins | `200` |
| `AA` demotes the **last** Active `CA` | `422`, and the role is unchanged after rollback |
| `set-role` to the role already held | `422` |
| `set-role` audit entry | contains both the before and after role |
| `depart` an Employee with an account | `Status = Departed`, `departed_at` set, **account `Suspended`** |
| `depart` an accountless Employee | `200`, no account call |
| `depart` the last Active `CA` | `422` |
| `CA` departs themselves | `422` |
| `depart` an already-`Departed` Employee | `422` |
| `depart` with an end date before the start date | `422` |
| After `depart`, the Employee's Tickets | still visible to their `CA`; none closed, hidden, or reassigned |
| `depart` an Employee whose account is **already suspended** | `200`, not `422` — §4.7 rule 2 |
| `suspend-account` the last Active `CA` | `422` |
| `suspend-account` on an accountless Employee | `422` |
| `suspend-account` does not change `employees.status` | `Active` still |
| `depart` at a Customer with **501 accounted Employees** | `200` — the guard and the notification helper both batch, §5.1 |
| `depart` notification recipients | the Customer's `Active`/`Invited` `CustomerAdmin`s only — **not** the leaver, **not** the `Suspended` Admin, **not** Accountants |
| `depart` notification call count | **one** `NotifyManyAsync`, not one `NotifyAsync` per Admin |
| `depart` at a Customer with no `CustomerAdmin` account | `200`, and **zero** notification calls — not a call with an empty list |
| `register` notification | same recipient rule; body says they cannot sign in until invited |
| A `register` that throws | **no notification** — raised inside the transaction, after the row |
| `EmployeeRegistered` / `EmployeeDeparted` | in `NotificationEvents.All`, **not** in `Emailed`; `EmailBody` null |
| `reinstate` a `Departed` Employee | `200`; `Status`, `employment_end_date` **and** `departed_at` all cleared |
| `reinstate` reactivates the account | one `ReactivateAccountAsync` call; no separate step for the caller |
| `reinstate` somebody who never accepted their invitation | account comes back **`Invited`**, not `Active` — Identity §9.1 rule 14 |
| `reinstate` an accountless Employee | `200`, no account call |
| `reinstate` an `Active` Employee | `422` "This employee has not departed."; nothing changed |
| `reinstate` at a suspended Customer | `422` "This customer is not active."; nothing changed |
| `reinstate` audit entry | `EmployeeReinstated`, **not** `EmployeeEdited`, with both snapshots |
| `reinstate` as `CA` for their own Customer | `200` — granted to whoever may depart |
| `reinstate` as `CA` for another Customer | `404`, not `403` |
| `reinstate` as `EMP` | `403` |
| `reinstate` notification | **none** — no `EmployeeReinstated` kind exists, and the handler takes no `INotificationApi` |
| `reactivate-account` on a `Departed` Employee | `422` — §4.8 rule 4, still, now that `/reinstate` exists |
| `reactivate-account` on an `Active` Employee's suspended account | `200` |
| `change-login-email` happy path | account address moved; **`employees.work_email` and `normalized_work_email` unchanged** |
| `change-login-email` on an `Invited` account | address moved; status still `Invited`; no suspend/reactivate call |
| `change-login-email` on an accountless Employee | `422` naming `/invite` as the fix |
| `change-login-email` on a `Departed` Employee | `422`; the old address is still on the account |
| `change-login-email` to an address that is a login **at another Customer** | `409`, message names nobody, **no audit entry** |
| `change-login-email` audit entry | `LoginEmailChanged`, targets the **`Employee`**, carries both addresses in full |
| `change-login-email` as `CA` | `403` — the one row where a `CA` is refused on their own Employee |
| `change-login-email` as `EMP` on their own record | `403` — this is not self-service |
| `update` a `Departed` Employee's name | `200` — editing after departure is allowed |
| `update` request DTO | has no `customerId`, `status`, `employmentEndDate`, or `userAccountId` property |
| `update` audit entry | names the changed sensitive fields, **carries neither number's value** |
| `update-own-contact` as `EMP` | `200`; only phone and email changed |
| `update-own-contact` request DTO | has **no** `employeeId` property |
| `update-own-contact` as `AA` | `403` from the catalogue, not `404` |
| `update-own-contact` attempting a name change | the field does not exist; ignored |
| `IEmployeeApi.IsActiveAsync` for an unknown id | `false` |
| `IEmployeeApi.IsActiveAsync` for a `Departed` Employee | `false` |
| `IEmployeeApi.IsActiveAsync` after a departure that an earlier call reported `true` | `false` — proves nothing is cached |
| `IEmployeeApi.FindManyAsync` with 501 ids | `InvalidOperationException` |
| `IEmployeeApi.ListActiveByCustomerAsync` with 60 Active, 1 `Departed`, 1 elsewhere | `TotalCount` 60, `TotalPages` 3 at `pageSize` 25 |
| the same, page 3 | 10 rows, continuing where page 2 stopped — no row on two pages |
| the same with `pageNumber: 0, pageSize: 10000` | normalized to page 1 and `MaxPageSize`; **no throw** — the caller is another slice |
| `EmployeeSummary` type | has no SSN, tax number, email, phone, or dates — assert by reflection |
| `IEmployeeApi.FindAsync` | returns a row regardless of caller scope — the caller authorizes |
| Every denial in this slice | writes an Audit entry |
| Every action name any handler passes to `RequireAsync` | resolves in some `IActionCatalogue` — see §11.3 item 4 |

### 11.3 The four tests that are easy to write wrongly

1. **The `Employee`-reads-a-colleague test.** The obvious scope test uses a *different* Customer's
   Employee, and it **passes even when §4.4 rule 2 is missing**. The test that catches the real
   defect uses a colleague at the **same** Customer. Write that one.
2. **The onboard-rollback test must query the database**, in a new scope, after the request
   completed — not assert on the response status. A `500` is returned whether or not the rollback
   worked.
3. **The self-check tests must exercise a real `CustomerAdmin` session**, because §5.2 rule 2's
   failure mode (comparing an account id to an Employee id) makes the guard silently never fire.
   Constructing the handler with a hand-made `CurrentUser` whose `Id` happens to be the Employee id
   tests the bug rather than the rule.
4. **The action-name test has to read the handler source.** §0.4's fail-closed checker denies an
   uncatalogued action to *every* role and logs it as `PermissionDenied`, so a forgotten catalogue
   entry ships as a `403` that reads like a deliberate decision. Nothing else can catch it: the
   action is a string, so the compiler cannot; and a handler unit test builds its
   `PermissionChecker` from the catalogue it is testing against, so it agrees with itself. The test
   that works scans `Slices/**/*.cs` for `RequireAsync(user, "…"` and asserts each name resolves in
   some catalogue — **and asserts the scan matched something**, or it passes vacuously the moment a
   refactor changes the call shape. Assert the reverse direction too: a catalogue entry no handler
   asks for is a granted permission for an operation that does not exist.

   Lives in `EndpointRoutingTests.cs` rather than in this slice's folder, because it covers every
   slice and there is no reason for six copies of it.

---

## 12. Known constraints

1. **A role change or account suspension does not affect the target's live session** for up to 8
   hours ([Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §17 constraint 1). Demotion and
   suspension therefore fail **unsafe**. There is no session revocation in v1. Do not fix it with a
   per-request database read in `IPermissionChecker`; if it must be fixed, that is a design
   decision to raise.
2. **The at-least-one-`Active`-`CustomerAdmin` guard has a concurrency window** (§5.1 rule 7),
   wider than `Identity`'s because the count reads a table this transaction does not lock. Unlike
   the Accountant case this **is** recoverable — matrix §4: *"Only an Accountant can resolve such a
   situation"* — but it is a support call.
3. ~~**The guard breaks at 500 accounted Employees per Customer**~~ — **resolved** (§13 item 5). It
   batches the `IIdentityApi` lookup in 500s instead of refusing above it, so the count is exact at
   any Customer size. The cost is one extra round trip per 500 accounted Employees.
4. ~~**`Departed` is terminal**~~ — **resolved** (§13 item 3). `/api/employees/reinstate` undoes a
   departure entered in error. A genuine return to the company is still a new Employee record.
5. ~~**`ListActiveByCustomerAsync` is unpaginated**~~ — **resolved** (§13 item 5). It is paged, capped
   by `PaginatedQuery.MaxPageSize`, and returns `TotalCount` so a caller can tell there is more. A
   caller that renders page 1 and offers no way to page has reintroduced the problem.
6. **An Employee's work email and their login email can diverge** (§4.9 rule 4). Editing one does
   not change the other. A login email can now be changed (§13 item 4) but only by an Accountant, so
   an Employee or Customer Admin who edits a work email still has to be told what they did not
   change — which is what the `Notice` on `EmployeeSelfDto` is for.
7. **An Employee cannot change their own name or job title.** Matrix §4 gives them "own contact
   details only", and this slice implements exactly that. A misspelled name needs a Customer Admin.
8. **The same natural person at two Customers is two unrelated records with two logins**
   (`01-DomainModel.md` §2). Accepted; it is what keeps Customer isolation absolute. They cannot
   share a login email, because `user_accounts.normalized_login_email` is unique system-wide.
9. **Personal identifying numbers are stored in plain text**, protected by row-level authorization
   only — not encrypted at rest beyond whatever the volume provides, and not tokenised. Decision 5
   accepts this for v1; §18 item 7 raises it, because it is the kind of decision that is very
   expensive to revisit after the table has data.
10. **No Employee import.** Onboarding a Customer with 200 Employees is 200 calls to
    `/api/employees/register`. Out of scope for v1.

---

## 13. Questions to flag rather than answer

Do not resolve these by guessing. Each changes behaviour a user or another slice sees.

**All nine are now answered — see the decision record at the end of this file (2026-09-02).** The
original text of each is kept below unedited, because the reasoning that made it a question is what
justifies the answer. Where an answer changed the build, the item says so.

1. **`AuditActions` has no code for an Employee role change**, and `AuditApi` throws on an unknown
   code. §4.6 cannot be built until `EmployeeRoleChanged` is added to
   `Slices/Audit/ExternalInterfaces/AuditActions.cs`. One line; `All` is reflected over the
   constants. Raise it, add it, and amend the `Audit` plan's code list. **Do not reuse
   `EmployeeEdited`** — the audit log must be able to answer "who made this person an
   administrator".
2. **RESOLVED — `IIdentityApi.SuspendAccountAsync` is idempotent** for an already-suspended account
   (§4.7 rule 2), while `Identity`'s *endpoint* correctly returns `422` for the same case (Identity
   §7.10 rule 4). [The Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §9.1 rule 13 now states
   this, with the reason: the handler validates current state, the contract asserts target state.
   The two must not share one implementation. Nothing is open; verify it when `Identity` is built.
3. **Is `Departed` really terminal?** Decision 1 says yes, from matrix §4 having no reverse row and
   matrix §12 forbidding invented permissions. But a mis-entered departure has no in-app recovery
   (§12 constraint 4), and re-hiring is not rare. If an un-depart operation is wanted it is a
   **normative change to matrix §4**, not a plan decision. Confirm before building either way.
4. **How does an Employee's login email get changed?** Matrix §4 authorizes editing an Employee
   record and matrix §11 authorizes password operations; **neither authorizes changing a login
   identifier.** Today the answer is "it cannot be changed", which will not survive first contact
   with a person who changes their surname. It would be a new row in matrix §4 and a new
   `IIdentityApi` method.
5. **What is the real ceiling on Employees per Customer?** §5.1 rule 1's 500-id cap becomes a hard
   failure above it, and §9 rule 6's unpaginated list becomes a large response. Both are fine at
   the expected scale and both need a number to design against.
6. **Should the Customer Admin be notified when an Employee is registered or departs?** No
   `NotificationEvents` kind exists for either, and §6 rule 3 says not to invent notification calls
   to make the dependency look used. If these events should notify, the kinds must be added to
   `NotificationEvents` — which is a change to the `Notifications` plan's fixed catalogue.
7. **Should the personal identifying numbers be encrypted at rest, or stored at all?**
   `01-DomainModel.md` §2 says the Office needs them, and decision 5 stores them in plain text with
   row-level authorization only. That is defensible and it is also a decision about
   legally-sensitive personal data that a builder should not be making alone. Raise it **before the
   table has production data**, because retrofitting encryption to a populated column is a
   migration plus a key-management design.
8. **Is a future `EmploymentStartDate` legitimate, and how far?** §4.2 rule 5 invents a one-year
   typo guard, which is a business rule this plan has no authority to set. Ask for the real answer,
   or drop the guard.
9. **`NotificationEvents` has both `Invited` and `EmployeeInvited`.** `Identity`'s
   `InviteEmployeeAccountAsync` must pick one. The consistent reading is `EmployeeInvited` for an
   Employee and `Invited` for an Accountant, and both are in `Emailed` — but the `Identity` plan
   §9.1 does not say, and the `Notifications` plan's `EmployeeInvited` sits under an "Employees"
   heading without stating its recipient. Confirm which kind and which recipient, then amend the
   `Identity` plan.

---

## Files checklist

| File | Action |
|---|---|
| `Slices/Employees/Infrastructure/Migrations/20260902_001_CreateEmployeesSchema.sql` | New |
| `Slices/Employees/Core/Employee.cs` | New (incl. `EmployeeStatus`) |
| `Slices/Employees/Infrastructure/EmployeesDbContext.cs` | New |
| `Slices/Employees/Infrastructure/Configurations/EmployeeConfiguration.cs` | New |
| `Slices/Employees/Application/EmployeeInvariants.cs` | New — §5 |
| `Slices/Employees/Application/EmployeeMapper.cs` | New — §4.11 |
| `Slices/Employees/Application/Dtos/` — fourteen DTOs per §3 | New |
| `Slices/Employees/Application/Handlers/OnboardCustomerHandler.cs` | New |
| `Slices/Employees/Application/Handlers/RegisterEmployeeHandler.cs` | New |
| `Slices/Employees/Application/Handlers/ListEmployeesHandler.cs` | New |
| `Slices/Employees/Application/Handlers/GetEmployeeHandler.cs` | New |
| `Slices/Employees/Application/Handlers/UpdateEmployeeHandler.cs` | New |
| `Slices/Employees/Application/Handlers/UpdateOwnContactHandler.cs` | New |
| `Slices/Employees/Application/Handlers/InviteEmployeeHandler.cs` | New |
| `Slices/Employees/Application/Handlers/SetEmployeeRoleHandler.cs` | New |
| `Slices/Employees/Application/Handlers/DepartEmployeeHandler.cs` | New |
| `Slices/Employees/Application/Handlers/SuspendEmployeeAccountHandler.cs` | New |
| `Slices/Employees/Application/Handlers/ReactivateEmployeeAccountHandler.cs` | New |
| `Slices/Employees/ExternalInterfaces/IEmployeeApi.cs` | New (incl. `EmployeeSummary`) |
| `Slices/Employees/ExternalInterfaces/EmployeeApi.cs` | New |
| `Slices/Employees/EmployeesActionCatalogue.cs` | New |
| `Slices/Employees/EmployeesRegistration.cs` | New |
| `Slices/Employees/EmployeesEndpoints.cs` | New |
| `Slices/Customers/ExternalInterfaces/ICustomerApi.cs` | **Edit** — add `CreateAsync`, §4.1 rule 1 |
| `Slices/Customers/ExternalInterfaces/CustomerApi.cs` | **Edit** — implement it, enlisting, auditing `CustomerCreated` |
| `Slices/Audit/ExternalInterfaces/AuditActions.cs` | **Edit** — add `EmployeeRoleChanged`, §18 item 1 |
| `Program.cs` | Edit — two lines |
| `AccountantApp.Tests/Employees/` | New — §11 |

---

## Success criteria

1. The migration applies to a fresh PostgreSQL database, and all three `CHECK` constraints and all
   four indexes exist.
2. A `Departed` Employee with a null `departed_at` is rejected by the database, not by a handler.
3. `POST /api/customers/onboard` creates a Customer, an Employee, and a `CustomerAdmin` account in
   **one transaction**, and returns all three ids and no token.
4. When any step of onboarding fails, **the `customers` table is unchanged** — verified by querying
   it, not by reading a status code.
5. Onboarding is `AccountantAdmin`-only; `AccountantUser` and `CustomerAdmin` both get `403`.
6. The first admin created by onboarding has role `CustomerAdmin`.
7. `POST /api/employees/register` creates an Employee with a null `user_account_id`, sends no
   email, and creates no account row.
8. Registering at a suspended or unknown Customer returns `422`; `IsActiveAsync` is called live.
9. The same work email is accepted at two different Customers and rejected as `409` within one.
10. An `Employee` role reading a **colleague at their own Customer** gets `404`.
11. An `Employee` role's own record response contains **no `taxIdentificationNumber` and no
    `socialSecurityNumber` key**, and those columns are never selected from the database.
12. A `CustomerAdmin` reading another Customer's Employee gets `404`, never `403`.
13. A `CustomerAdmin` passing another Customer's id to `list` gets `403`, not an empty page.
14. `list` resolves roles and account statuses in **one** `IIdentityApi` call for the whole page,
    and returns null role for accountless Employees.
15. `list` includes `Departed` Employees by default and clamps `pageSize` to 50.
16. `invite` creates the account, writes `user_account_id`, and both happen or neither does.
17. `invite` rejects an Accountant role with `422` and a duplicate login email with `409` whose
    message names no other Customer.
18. Inviting an Employee writes **no** `UPDATE` to `tickets`, and their pre-existing non-`Draft`
    Tickets become readable to the new account immediately.
19. Demoting, departing, or suspending the last `Active` `CustomerAdmin` of a Customer returns
    `422` and leaves the data unchanged — all three paths.
20. A `CustomerAdmin` cannot demote, depart, or suspend themselves; all three return `422`.
21. The self check compares the caller's id against `employee.UserAccountId`, not `employee.Id`,
    and a test proves it fires.
22. Departure sets `Departed`, sets `departed_at`, and **suspends the account in the same
    transaction**; an already-suspended account is not an error.
23. Departure closes, hides, reassigns, and deletes nothing, and touches no other slice's table.
24. Reactivating the account of a `Departed` Employee returns `422`.
25. `update`'s request DTO has no `customerId`, `status`, `employmentEndDate`, or `userAccountId`
    property, and a `Departed` Employee's record is still editable.
26. `update-own-contact`'s request DTO has **no** `employeeId` property, and edits exactly two
    fields.
27. No audit entry in this slice carries a tax identification number or a social-security number
    value.
28. Account-level audit entries are written by `Identity`, not duplicated here; `EmployeeInvited`
    and `AccountInvited` both exist for one invitation.
29. `EmployeeSummary` exposes no personal identifying number, email, phone, or employment date —
    asserted by reflection.
30. `IEmployeeApi.IsActiveAsync` returns `false` for unknown and `Departed` ids, caches nothing,
    and `FindManyAsync` caps at 500.
31. Every route uses kebab-case for multi-word segments, no route has a route parameter, and
    `/api/customers/onboard` carries a comment naming `03-SliceInventory.md` §1.
32. Every write in the §4.0 F table writes an Audit entry; every denial writes one too.

---

## Implementation record — 2026-09-02

Built as specified. Every file in the checklist above exists; the solution compiles with
`-warnaserror` and no warnings. `AccountantApp.Tests/Employees/` holds six files and **110 passing
tests plus 1 skipped**; the whole suite is 300 passed / 0 failed / 4 skipped.

**Updated after the §13 answers.** `AccountantApp.Tests/Employees/` now holds **seven** files — the
new `EmployeesCorrectionFlowTests.cs` covers §4.7a and §4.10a — and the whole suite is **332 passed /
0 failed / 4 skipped**. The 4 skips are unchanged: they are the four slices' real-PostgreSQL
`[SkippableFact]`s, all of which are still **unverified rather than verified** (below). What the new
tests add, beyond the §11.2 rows:

- Reinstatement, including the never-accepted-invitee case that comes back `Invited` rather than
  `Active` — the defect Identity §9.1 rule 14 exists for. The handler earns that behaviour by
  calling `ReactivateAccountAsync` instead of flipping a status itself, which is the thing to
  preserve if it is ever rewritten.
- The login-email change, including that the `employees` row is untouched and that a `CustomerAdmin`
  and an `Employee` are both `403` — the one row in this slice where a `CustomerAdmin` is refused on
  their own Employee.
- Both notifications: recipient set, one `NotifyManyAsync` rather than a loop, zero calls when there
  is no recipient, `Invited` Admins in and `Suspended` Admins out, and neither kind in `Emailed`.
- The batched account lookup, at 501 and 502 accounted Employees, through both callers — the guard
  and the notification helper — asserting **two** `FindManyAsync` calls, not one and not 502.
- The paged `IEmployeeApi.ListActiveByCustomerAsync`, including that a hostile `pageNumber: 0,
  pageSize: 10000` normalizes rather than throwing.
- §11.3 item 4's action-name scan, in `EndpointRoutingTests.cs`. Both directions pass today: the 35
  names the handlers require and the 35 the catalogues grant are the same set.

### Not verified on this machine

There is **no local PostgreSQL**, so `EmployeesSchemaTests` skipped in full. Everything it covers is
therefore **unverified, not verified**:

- The migration has never been applied to any database.
- All three `CHECK` constraints (`ck_employees_departure`, `ck_employees_dates`,
  `ck_employees_email_pair`).
- Both unique partial indexes, `idx_employees_customer_active`, and the two trigram indexes.
- The `23505` → `409` conversion, which is unreachable without a real unique index.
- The `EF.Functions.ILike` search branch of §4.3, which the in-memory provider cannot translate at
  all — it is dead code in every other test.
- **Success criteria 1, 2 and 4, and §11.3 test 2** — the cross-slice onboarding rollback. The test
  is written and asserts by querying `customers`, `employees`, `user_accounts` and
  `user_account_tokens` on a fresh connection after the failure, per §11.3's instruction not to
  assert on the status code. It has never run.

The other two of §11.3's three easy-to-write-wrongly tests **do** run and pass:
`An_employee_reading_a_colleague_at_their_own_customer_gets_404` (a colleague at the **same**
Customer, not a different one), and the three self-check tests, which build `CurrentUser.Id` from
the row's `UserAccountId` so that §5.2 rule 2's failure mode — comparing an account id to an
Employee id — makes them fail rather than silently pass.

Where an in-memory test can only prove the transaction scope was disposed without a commit
(onboard-rollback, invite-failure), the assertion is `transaction.RolledBack` and the test comment
says in so many words that it is a **proxy**, with the real assertion deferred to the schema tests.

### Deviations from this plan

1. **`ck_employees_departure` is stricter than §1.** The `Active` branch gained
   `AND employment_end_date IS NULL`. As written in §1 an `Active` row could carry an
   `employment_end_date`, which is the "leaving next month" row that no departure ever processes —
   the person keeps their login indefinitely. §4.2 already refuses to write one; the constraint now
   holds it too.
2. **Two trigram indexes beyond §1's four**, plus `CREATE EXTENSION IF NOT EXISTS pg_trgm`:
   `idx_employees_name_trgm` on `(given_name, family_name)` and `idx_employees_email_trgm` on
   `normalized_work_email`. §4.3 specifies `ILIKE '%term%'`, which no b-tree can serve, so without
   these the search is a sequential scan that degrades with the table and never reports an error.
   Same approach the Customers slice takes for `legal_name`. Success criterion 1 says "all four
   indexes"; there are six.
3. **`EmployeeSelfDto` has a `Notice` property**, not in §3. `update-own-contact` sets it on every
   success to say that the work email is contact information and is not the address the person signs
   in with. Without it a self-service edit of a work email reads as a login change. Since §13 item 4
   was answered it also names who to ask, because the change is possible now — just not by them.
4. **`ICustomerApi.CreateCustomer` is a class with settable properties**, not a positional record.
   `OnboardCustomerRequestDto.Customer` binds it straight from the request body, and a positional
   record cannot be model-bound that way.
5. **`EmployeeValidation` duplicates Identity's email normalization** rather than calling
   `EmailNormalization`. That type is internal to Identity, and a cross-slice reference to another
   slice's internal helper is the coupling `ExternalInterfaces` exists to prevent. The two
   implementations must agree, and a comment in `EmployeeValidation` says so.
6. **§5's guard short-circuits.** `RequireAnotherActiveCustomerAdminAsync` returns immediately when
   the excluded account is not currently an `Active` `CustomerAdmin`, so promoting a plain Employee
   at a Customer with zero Admins is possible. It also uses the pre-change exclusion approach rather
   than Identity's count-after-mutate. It no longer caps the Identity lookup at 500 — see the
   decision record for §13 item 5.

---

## Decision record — the §13 questions, answered 2026-09-02

All nine are closed. Four changed the build; the changes are described here and are **normative** —
they amend `02-AuthorizationMatrix.md` §4 and the `Notifications` catalogue, which the owner of this
project authorized explicitly rather than a builder inferring them.

| # | Decision |
|---|---|
| 1 | `EmployeeRoleChanged` added to `AuditActions`. `EmployeeEdited` is not reused. |
| 2 | Already resolved in Identity's code — `SuspendAccountAsync` is idempotent, the endpoint is not. |
| 3 | **Departure is reversible as a correction.** New operation, new matrix row. |
| 4 | **A login email can be changed, by an Accountant only.** New operation, new matrix row. |
| 5 | **A Customer may exceed 500 Employees.** The guard batches; the contract is paged. |
| 6 | **Notify the Customer's own Admins** on registration and on departure, in-app only. |
| 7 | **Plain text stands**, with row-level authorization only. Accepted risk, not an open question. |
| 8 | **The one-year future-start-date guard stands** and is now a real rule, not a guess. |
| 9 | **`EmployeeInvited`, to the invitee.** Confirmed; the `Identity` plan §9.1 now says so. |

### 3 — reinstatement (`/api/employees/reinstate`)

`ReinstateEmployeeHandler`. Requires the Employee to be `Departed` (`422` otherwise) and their
Customer to be `Active` (`422`), then clears `EmploymentEndDate` and `DepartedAt`, sets `Active`, and
calls `IIdentityApi.ReactivateAccountAsync` in the same transaction. Audits `EmployeeReinstated` — a
new `AuditActions` constant, not a reused `EmployeeEdited`, because "whose departure was undone, by
whom" is exactly what this log will be asked. Granted to `AA`, `AU`, `CA` — whoever may enter a
departure may undo one.

No last-Active-Admin guard: the operation can only add an Admin. No self check: departure suspends
the account, so a departed person cannot be the caller.

**It forced a fix in Identity.** `ReactivateAccountAsync` set `Active` unconditionally. Suspension
flattens `Invited` and `Active` into one status, so departing a never-accepted invitee and then
reinstating them produced an `Active` account with **no password hash** — one that fails every login
(`Verify(null, …)` returns `Failed`) and is invisible to every invitation flow, because invitees are
not `Active`. It now restores to `Invited` when `PasswordHash is null`. This was already reachable
through `/api/employees/reactivate-account` before reinstatement existed.

### 4 — login email (`/api/employees/change-login-email`)

`ChangeEmployeeLoginEmailHandler` plus a new `IIdentityApi.ChangeLoginEmailAsync`. `AA` and `AU`
only: whoever can move an account to a new address can move it to a mailbox they control, so a
Customer Admin is refused and **nobody may change their own**. `422` for an accountless or departed
Employee, `409` for an address another account holds. Audits `LoginEmailChanged` **twice**, once in
each slice — Identity's entry targets the account, this slice's targets the Employee, because
somebody investigating a person searches by Employee id.

It does not touch the work email, the password, `EmailConfirmedAt`, or any live session. Forcing
re-confirmation would lock out the person whose address was corrected *because* they could not
receive mail at the old one — the case the operation exists for.

`UpdateOwnContactHandler.LoginEmailNotice` now names the accounting office instead of saying the
change is impossible.

### 5 — scale

Two changes, both in this slice. `EmployeeInvariants` **batches** the `IIdentityApi.FindManyAsync`
lookup in 500s rather than throwing above 500, so the last-Active-Admin count is exact at any
Customer size; the old `422` froze the operation entirely for a large Customer. And
`IEmployeeApi.ListActiveByCustomerAsync` is **paged** — `PaginatedResponse<EmployeeSummary>`, page
size capped by `PaginatedQuery.MaxPageSize`, ordered `FamilyName, GivenName, Id` so a page boundary
cannot show one colleague twice and another never.

`TotalCount` is what makes paging different from the silent cap the original text warned about: a
picker can show a count or a search box instead of rendering 50 rows as though they were all of them.
**A caller that renders page 1 with no way to reach page 2 has reintroduced the problem.**

### 6 — notifications

Two kinds added to `NotificationEvents`: `EmployeeRegistered` and `EmployeeDeparted`. Both go to the
Customer's own `CustomerAdmin` accounts and **neither is in `Emailed`** — an Admin who registers six
people in an afternoon does not want six emails about their own afternoon, and neither event carries
a token or anything time-critical.

`EmployeeNotifications.NotifyCustomerAdminsAsync` holds the recipient rule in one place, called by
both handlers inside their transaction. Recipients exclude Accountants (they see every Customer
already), the subject of the event, and suspended Admins; `Invited` Admins are included, and will
read it when they accept. The **caller** is not filtered here — Notifications' own rule E already
drops a notification addressed to the current user, and a second copy of that rule in this slice
would be one more place to forget.

`DepartEmployeeHandler` sends **after** writing the status, so the helper's own query sees the
departing person as `Departed` and cannot address the notification to them.

### 7 — identifying numbers stay in plain text

Decision 5 stands: `tax_identification_number` and `social_security_number` are `VARCHAR`, protected
by row-level authorization and by the fact that no DTO or `ExternalInterface` carries them. This is
now an **accepted risk, recorded deliberately** — not an unanswered question. The cost of revisiting
it rises the moment the table holds production data, because retrofitting encryption to a populated
column is a migration plus a key-management design.

### 8 — future start dates

`MaximumStartDateYearsAhead = 1` stands, unchanged. It is now the project's rule rather than §4.2
rule 5's guess.
