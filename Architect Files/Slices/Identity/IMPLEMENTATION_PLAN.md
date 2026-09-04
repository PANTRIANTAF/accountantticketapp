# Identity Slice — Implementation Plan

Build this **fourth**, after `Audit`, `Notifications`, and `Customers`, and before `Employees`,
`Documents`, and `Tickets`.

It is the largest slice in the system, and it is the only one that changes how *every other slice*
behaves — because until it ships, nothing sets `HttpContext.User` and every endpoint runs on the
development bypass. **The commit that finishes this slice deletes `DevAuthHandler.cs`.** See §15;
that section is not optional cleanup, it is part of the slice.

Documents that govern this slice, in precedence order. Where any of them disagrees with this plan,
**they win and this plan is wrong** — fix the plan, do not code around it.

- [02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §2 (Accountant accounts), §11
  (authentication and self-service), §12 (rules a builder must not violate)
- [01-DomainModel.md](../../01-DomainModel.md) §2 (UserAccount, and "Accountants — deliberately
  not a separate entity")
- [03-SliceInventory.md](../../03-SliceInventory.md) §1, §2 (the `Identity → Customers` edge), §3
  rule 7 (the inverted dependency this slice implements)
- [App/GeneralAppArchitecture.md](../../App/GeneralAppArchitecture.md) §9 (configuration, seeding,
  and the `DevAuth` deletion rule)
- [04-Infrastructure.md](../../04-Infrastructure.md) §4 (no JWT signing key; data-protection keys)

---

## 0. Prerequisites — read before writing any code

### 0.1 What this slice owns, and the two things it does not

It owns **`UserAccount`** — credentials, role, status, and the ability to log in. It is the only
entity in the system that can authenticate.

Two absences matter more than anything it owns:

1. **There is no `Accountant` table, and you must not create one.** `01-DomainModel.md` §2: *"An
   Accountant is simply a UserAccount whose role is `AccountantAdmin` or `AccountantUser`, carrying
   its own display name and contact email, with no Employee link."* A separate table would
   duplicate what `user_accounts` already holds and immediately drift from it. When this plan says
   "an Accountant account", it means a row in `user_accounts` with one of those two roles.
2. **It does not own `Employee` records, and it cannot read them.** `Employees → Identity` exists,
   so the reverse edge would be a cycle. This has one non-obvious consequence that shapes the
   schema — see §1, "Why `customer_id` is a column here".

### 0.2 This slice *produces* `CurrentUser` — it does not merely consume it

Every other slice receives `CurrentUser` as an injected scoped service and trusts it. This slice
is where the trust comes from. `Shared/Auth/CurrentUserFactory.FromPrincipal` reads three claims:

| Claim | Read as | Notes |
|---|---|---|
| `ClaimTypes.NameIdentifier` (or `sub`) | `CurrentUser.Id` | The `user_accounts.id`, as a string |
| `ClaimTypes.Role` (or `role`) | `CurrentUser.Role` | Must parse to a `UserRole`; anything else is `401` |
| `customer_id` | `CurrentUser.CustomerId` | Must be a GUID. **Mandatory for `CustomerAdmin` and `Employee`** — the factory throws `AppException(401)` without it |

**So the login handler must write all three, and a `CustomerAdmin` or `Employee` session without a
`customer_id` claim is not a degraded session — it is a broken one that fails on the next request
with a `401` and no useful message.** Read `Shared/Auth/CurrentUserFactory.cs` before writing
§7.1; it is 25 lines and it is the contract you are implementing against.

Do not modify `CurrentUserFactory` to relax any of those checks. If a claim is missing, the bug is
in the sign-in path, which is in this slice.

### 0.3 The permission checker — fail-closed, and mostly not used here

Handlers take `IPermissionChecker` and call
`await _permissions.RequireAsync(user, "ActionName", ct: ct)` as the first statement. An action
absent from the composed catalogue **denies**; a role not listed **denies**; every denial is
audited before the `403`.

This slice is the exception to the usual shape. It has **thirteen handlers**. **Four of them are
unauthenticated** and therefore have no `CurrentUser` to check, and three more are authenticated but
have no catalogue action:

| Handler | Authorization |
|---|---|
| `LoginHandler`, `RequestPasswordResetHandler`, `CompletePasswordResetHandler`, `AcceptInvitationHandler` | **None.** No `CurrentUser` exists. Authorization is the token or the password itself. |
| `LogoutHandler`, `GetCurrentSessionHandler`, `ChangeOwnPasswordHandler` | Authenticated, but **no catalogue action** — every role may act on their own session (`02` §11). |
| `ListAccountantsHandler` | `ListAccountants` — both Accountant roles |
| `InviteAccountantHandler`, `SuspendAccountantHandler`, `ReactivateAccountantHandler`, `PromoteAccountantHandler`, `DemoteAccountantHandler` | `AccountantAdmin` only — matrix §2 |

> **An unauthenticated handler must not inject `CurrentUser`.** It is registered as a scoped
> service whose factory calls `FromPrincipal`, which throws `AppException(401)` when no principal
> exists. Injecting it into `LoginHandler` makes login return `401` **before the handler body
> runs**, on every request, forever. This is the single most likely way to build this slice so that
> it can never work, and the failure looks exactly like "wrong password".
>
> The same rule applies to auditing: use `IAuditApi.LogUnauthenticatedAsync(actorIdentifier, ...)`
> from those four handlers. `LogAsync` resolves `CurrentUser` from the service provider and will
> throw for the same reason. `AuditApi` implements both; note that `IAuditApi`'s *default* body for
> `LogUnauthenticatedAsync` throws `NotSupportedException`, so a test double must override it.

### 0.4 Pagination

Use `Shared/Pagination/`. Default `PageSize` **15**, maximum **50**
(`App/GeneralAppArchitecture.md` §8 — these are the system-wide numbers; do not pick different
ones for this slice). A `PageSize` of 5,000 clamps to 50, a `PageNumber` below 1 clamps to 1.

Only one endpoint here is paginated: `/api/accountants/list`. Default sort
`display_name ASC, id ASC` — the `id` tiebreaker matters because two Accountants may share a
display name, and an unstable sort makes paging skip and repeat rows.

### 0.5 The six decisions locked for this slice

None of these are in the numbered documents, because none of them had been made. They are decided
now and they are **LOCKED**. Do not re-litigate them in code.

| # | Decision |
|---|---|
| 1 | **Password hashing:** `Microsoft.AspNetCore.Identity.PasswordHasher<T>`, that type **only**. See §3.1. |
| 2 | **Invitation and reset tokens:** a separate `user_account_tokens` table storing only a SHA-256 hash. See §1 and §3.2. |
| 3 | **Lockout:** 5 consecutive failures → 15-minute lockout, counter resets on success. See §7.1. |
| 4 | **Session:** an 8-hour cookie with sliding renewal past the halfway point. See §4. |
| 5 | **Forced password change:** a `must_change_password` column, carried as a claim, enforced by a shared middleware. See §5. |
| 6 | **Email confirmation:** accepting the invitation *is* the confirmation. There is no separate confirm-email flow. See §7.7. |

### 0.6 What this slice deletes

Three things leave the tree in the commit that finishes this slice. §15 is the checklist; it is
listed here so you know from the start that they are temporary:

- `Shared/Auth/DevAuthHandler.cs`, its registration block in `Program.cs`, and the `DevAuth` key
  in `appsettings.Development.json`
- The `RecipientDirectoryStub` registered by `NotificationsRegistration.cs`, **and** the startup
  guard in `Program.cs` that throws when `Notifications:Email:Enabled` is true while the stub is
  still in place. Once this slice registers the real directory that guard can never fire, and it is
  the thing currently keeping email switched off. See §15.
- Nothing else. In particular the `Seeding` configuration section **stays** — it is how the first
  Admin is created on a fresh database, forever, not just during development.

---

## 1. Database schema (SQL migration)

**File:** `Slices/Identity/Infrastructure/Migrations/20260901_001_CreateIdentitySchema.sql`

One script, two tables, `user_accounts` first because `user_account_tokens` references it.

### Table: user_accounts

```sql
CREATE TABLE user_accounts (
    id                        UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The login identifier. Two columns on purpose: see the notes below.
    login_email               VARCHAR(320) NOT NULL,
    normalized_login_email    VARCHAR(320) NOT NULL,

    -- NULL while the account is Invited. It is not "" and it is not a placeholder hash.
    password_hash             VARCHAR(500) NULL,

    display_name              VARCHAR(200) NOT NULL,

    -- One of the four UserRole values, as text.
    role                      VARCHAR(20)  NOT NULL,

    -- The Employee this account belongs to, and that Employee's Customer. Both NULL for the
    -- two Accountant roles. Neither is a foreign key: they point into another slice.
    employee_id               UUID NULL,
    customer_id               UUID NULL,

    -- 'Invited' | 'Active' | 'Suspended'
    status                    VARCHAR(20)  NOT NULL DEFAULT 'Invited',

    must_change_password      BOOLEAN      NOT NULL DEFAULT FALSE,
    email_confirmed_at        TIMESTAMPTZ  NULL,

    failed_login_count        INTEGER      NOT NULL DEFAULT 0,
    lockout_expires_at        TIMESTAMPTZ  NULL,

    created_at                TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_login_at             TIMESTAMPTZ  NULL,
    last_password_change_at   TIMESTAMPTZ  NULL,

    CONSTRAINT uq_user_accounts_normalized_email UNIQUE (normalized_login_email),

    -- An Accountant has no Employee and no Customer; a Customer-side account has both.
    -- The database enforces this because a session minted from a row with a NULL customer_id
    -- and a CustomerAdmin role is unusable, and the failure surfaces one request later.
    CONSTRAINT ck_user_accounts_scope CHECK (
        (role IN ('AccountantAdmin', 'AccountantUser')
             AND employee_id IS NULL AND customer_id IS NULL)
        OR
        (role IN ('CustomerAdmin', 'Employee')
             AND employee_id IS NOT NULL AND customer_id IS NOT NULL)
    )
);
```

| Column | Note |
|---|---|
| `login_email` | The address **as the person typed it**, for display and for addressing mail. |
| `normalized_login_email` | Lowercased and trimmed. **The unique constraint is on this, not on `login_email`.** See below. |
| `password_hash` | `VARCHAR(500)`. `PasswordHasher<T>`'s v3 format is a base64 string around 90 characters; 500 leaves room for a future format change without a migration. **Nullable is load-bearing** — an `Invited` account has no credential, and a placeholder value would be a password somebody could eventually guess. |
| `role` | Text, not a PostgreSQL enum. A new role must not need DDL. Validated by the `CHECK` and by EF's conversion, not by a database enum type. |
| `employee_id`, `customer_id` | **No foreign key.** `employees` and `customers` belong to other slices, and a cross-slice FK makes the two schemas one schema. |
| `status` | `'Invited'`, `'Active'`, `'Suspended'`. Default `'Invited'`, because every account except the seeded first Admin starts there. |
| `must_change_password` | Default `FALSE`. Set `TRUE` by seeding (§14) and by nothing else in v1. |
| `email_confirmed_at` | Set when the invitation is accepted (§7.7). Nullable timestamp rather than a boolean, because *when* it happened is worth having and a boolean cannot be asked that. |
| `failed_login_count` | Consecutive failures. Reset to 0 on any success **and** when a lockout is applied. |
| `lockout_expires_at` | Null means not locked out. A past timestamp also means not locked out — do not clear it eagerly; compare it to `NOW()`. |

### Why the email is stored twice

`Alice@Example.COM` and `alice@example.com` are the same mailbox for every practical purpose, and
a system that lets both exist as separate logins has two accounts for one person, one of which
will be the one nobody remembers using.

- **Uniqueness and lookup use `normalized_login_email`**, produced by
  `email.Trim().ToLowerInvariant()`. It is what the unique index covers and what `LoginHandler`
  queries.
- **`login_email` is what you display and what you mail.** Some mail systems do treat the local
  part as case-sensitive, so mangling the stored address is not free.

Do **not** implement this by calling `.ToLower()` inside a `Where` clause on `login_email`. That
is unindexable, and `App/GeneralAppArchitecture.md` §8 already forbids the pattern elsewhere in
this codebase. Normalize on write, query the normalized column.

Also do **not** validate the address with a regular expression. Accept it if it contains exactly
one `@`, has something on both sides, and is 320 characters or fewer. `System.Net.Mail.MailAddress`
parsing is acceptable. An over-clever pattern rejects legitimate addresses, and the invitation
email is the real validator — an address that cannot receive the invitation never becomes an
account.

### Why `customer_id` is a column here — read this before removing it

It looks like denormalisation, and it looks like the exact mistake
[03-SliceInventory.md](../../03-SliceInventory.md) §2 forbids for Customer *status*. It is not.

The login handler must put a `customer_id` claim into the session for a `CustomerAdmin` or
`Employee`, or `CurrentUserFactory` rejects every subsequent request with a `401` (§0.2). The
account links to an **Employee**, and the Employee belongs to a Customer — but `Identity` may not
call `Employees`, because `Employees → Identity` already exists and the reverse edge is a cycle.
So `Identity` has exactly three options, and two are wrong:

1. **A second inverted interface** (`Identity` defines an employee locator, `Employees` implements
   it). §3 rule 7 says *"Do not invent a second inverted interface without raising it — the pattern
   is easy to abuse into a hidden cycle."* This would be that abuse, for a value that never
   changes.
2. **Look up the Employee anyway**, which is a dependency-rule-1 violation.
3. **Receive the `customer_id` at account-creation time and store it.** `Employees` already calls
   `IIdentityApi` to create the account (§9.1); it passes both the Employee id and the Customer id
   because it knows both. This is what we do.

The distinction from the forbidden case is **mutability**, and it is the whole argument:

> An Employee's owning Customer is **immutable**. `01-DomainModel.md` §2: *"an Employee record
> belongs to exactly one Customer... If the same natural person works for two Customers of the
> Office, that is two independent Employee records."* There is no operation anywhere in this
> system that moves an Employee between Customers. A copy of an immutable fact cannot go stale.
>
> A Customer's **status** is the opposite: it changes precisely when it matters, and a cached copy
> is wrong at exactly the moment suspension is supposed to bite. That is why status is **not** a
> column here and is read live through `ICustomerApi.IsActiveAsync` on every login.

Both rules live in the same table and they point in opposite directions. Get them the wrong way
round and you have either a login that cannot be scoped or a suspension that does not work.

### Table: user_account_tokens

```sql
CREATE TABLE user_account_tokens (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Same slice, so a real foreign key is correct here.
    user_account_id  UUID NOT NULL REFERENCES user_accounts(id),

    -- 'Invitation' | 'PasswordReset'
    purpose          VARCHAR(30) NOT NULL,

    -- SHA-256 of the raw token, lowercase hex. The raw token is NEVER stored.
    token_hash       CHAR(64)    NOT NULL,

    expires_at       TIMESTAMPTZ NOT NULL,
    consumed_at      TIMESTAMPTZ NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

| Column | Note |
|---|---|
| `token_hash` | `CHAR(64)`, fixed-width because a hex SHA-256 is always 64 characters. Unique — see the index below. |
| `expires_at` | Absolute, computed at issue time. **Invitations: 7 days. Password resets: 1 hour.** The two differ because an invitation is an onboarding step that waits on a human, and a reset is a response to something the person just did. |
| `consumed_at` | Null means unused. Set on successful redemption; **the row is never deleted**, so "this token was used, at this time" stays answerable. |

**Only the hash is stored.** The raw token exists in exactly two places: the HTTP response of
nothing at all, and the body of one email. This is the point of the design — a person with read
access to the database cannot mint a session or take over an account. It is also why the
`Notifications` slice grew a `notification_outbox.email_body` column that is blanked after
sending; see [that plan](../Notifications/IMPLEMENTATION_PLAN.md) §1, "Why `email_body` exists",
and §7.5 below. **If you find yourself storing the raw token anywhere, stop — you have undone the
whole mechanism, and nothing will fail a test to tell you.**

### Indexes

```sql
-- The login lookup. This is the hottest query in the application.
-- The UNIQUE constraint on normalized_login_email already provides the index; do not add
-- a second one on the same column.

-- Listing Accountants: matrix §2 permits both Accountant roles to read this list.
-- Partial, because the two Accountant roles are a handful of rows in a table that grows
-- with every Employee of every Customer.
CREATE INDEX idx_user_accounts_accountants ON user_accounts (display_name, id)
    WHERE role IN ('AccountantAdmin', 'AccountantUser');

-- The at-least-one-Active-Admin guard (§8) counts this on every suspend and demote.
CREATE INDEX idx_user_accounts_active_admins ON user_accounts (id)
    WHERE role = 'AccountantAdmin' AND status = 'Active';

-- Employees asks "does this Employee have an account?" through IIdentityApi.
CREATE UNIQUE INDEX uq_user_accounts_employee ON user_accounts (employee_id)
    WHERE employee_id IS NOT NULL;

-- Token redemption looks up BY HASH, never by user. Unique so a hash collision — or, far more
-- likely, a bug that reuses a token — fails loudly at insert instead of silently authorizing.
CREATE UNIQUE INDEX uq_user_account_tokens_hash ON user_account_tokens (token_hash);

-- Invalidating a user's outstanding tokens of one purpose. Partial: consumed rows accumulate
-- forever and must not be scanned.
CREATE INDEX idx_user_account_tokens_outstanding
    ON user_account_tokens (user_account_id, purpose)
    WHERE consumed_at IS NULL;
```

`uq_user_accounts_employee` is a **unique** partial index, and that is a domain rule the database
should hold: one Employee has at most one UserAccount. Two accounts for one Employee means two
sessions with the same scope and different roles, and the second one is invisible in every UI.

### No deletes

`01-DomainModel.md` §2: *"A UserAccount is never deleted."* §9.2: nothing in this system is
hard-deleted and `Document` is the only entity with a soft delete. So:

- There is no `DELETE` statement anywhere in this slice, no `deleted_at`, and no delete endpoint.
  Matrix §2 spells it out: *"Delete an Accountant account — **Nobody.** Suspension only."*
- Consumed and expired tokens are **not** purged. They are the evidence that a reset happened.

---

## 2. EF Core entities and DbContext

### 2.0 Column naming — mandatory

Entities are PascalCase, columns are snake_case, and **there is no automatic conversion
configured**. Every single property needs an explicit `HasColumnName`. A missing one produces a
`42703: column u.DisplayName does not exist` at runtime, not at startup, and only on the code path
that touches it. `App/GeneralAppArchitecture.md` §5.

### 2.1 `Core/UserAccount.cs`

```csharp
public sealed class UserAccount
{
    public Guid Id { get; set; }
    public string LoginEmail { get; set; } = string.Empty;
    public string NormalizedLoginEmail { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? CustomerId { get; set; }
    public string Status { get; set; } = AccountStatus.Invited;
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? EmailConfirmedAt { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? LastPasswordChangeAt { get; set; }

    public bool IsAccountant =>
        Role is UserRole.AccountantAdmin or UserRole.AccountantUser;

    public bool IsLockedOut(DateTimeOffset now) =>
        LockoutExpiresAt is { } until && until > now;
}

public static class AccountStatus
{
    public const string Invited   = "Invited";
    public const string Active    = "Active";
    public const string Suspended = "Suspended";
}
```

Two notes:

- **`UserRole` is the shared enum** from `Shared/Auth/UserRole.cs`, not a copy. It is the one type
  every slice's authorization depends on, and a second definition would be the worst possible
  duplication in this codebase. Map it with `.HasConversion<string>()` so the column stays
  readable text.
- **`IsLockedOut` takes `now` as a parameter** rather than reading `DateTimeOffset.UtcNow`
  internally. A method that reads the clock cannot be tested without waiting 15 minutes.

### 2.2 `Core/UserAccountToken.cs`

```csharp
public sealed class UserAccountToken
{
    public Guid Id { get; set; }
    public Guid UserAccountId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsRedeemable(DateTimeOffset now) =>
        ConsumedAt is null && ExpiresAt > now;
}

public static class TokenPurpose
{
    public const string Invitation    = "Invitation";
    public const string PasswordReset = "PasswordReset";
}
```

`IsRedeemable` exists so that "unconsumed **and** unexpired" is written once. Two separate checks
at four call sites is four chances to forget the second one, and forgetting it means an expired
token still works.

### 2.3 `Infrastructure/IdentityDbContext.cs`

```csharp
public sealed class IdentityDbContext : DbContext
{
    public IdentityDbContext(DbContextOptions<IdentityDbContext> options) : base(options) { }

    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<UserAccountToken> Tokens => Set<UserAccountToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new UserAccountConfiguration());
        modelBuilder.ApplyConfiguration(new UserAccountTokenConfiguration());
    }
}
```

Rules, each of which is a way this breaks:

1. **The `DbContextOptions<IdentityDbContext>` constructor is required.** Registration uses
   `AddDbContext<IdentityDbContext>`, which supplies exactly that; a parameterless constructor
   fails at resolution with a message that does not mention the constructor.
2. **Never `AddScoped<IdentityDbContext>()`.** See §12.
3. **Do not add a navigation property from `UserAccount` to its tokens.** Nothing needs to load an
   account with its token history, and a collection navigation invites `Include(u => u.Tokens)` on
   the login path, which would pull every reset token the person has ever had into memory to check
   a password.
4. **The name `IdentityDbContext` also exists in
   `Microsoft.AspNetCore.Identity.EntityFrameworkCore`.** That package must **not** be referenced
   (§3.1), so there is no clash — but if you ever see a "the type exists in both" error, the fix is
   to remove the package reference, not to rename our class.

### 2.4 EF configurations

`Infrastructure/Configurations/UserAccountConfiguration.cs` and
`UserAccountTokenConfiguration.cs`. Both are ordinary `IEntityTypeConfiguration<T>`
implementations. Every property gets `HasColumnName`, `HasMaxLength` matching the DDL exactly, and
`IsRequired()` where the column is `NOT NULL`.

Three specifics:

- `builder.Property(u => u.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(20)`
- `builder.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(500)` — and
  **no `IsRequired()`**, matching the nullable column.
- `builder.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsFixedLength()`

**There is no global query filter in this slice.** Not for status, not for scope. The reasons are
the same three given in [the Customers plan](../Customers/IMPLEMENTATION_PLAN.md) §2.3, and one
more that is specific here: a filter excluding `Suspended` accounts would make the suspend and
reactivate handlers unable to find their own target, and would make the login handler unable to
tell "no such account" from "suspended" — which it must, to audit correctly.

---

## 3. Password hashing, tokens, and the password policy

### 3.1 `Application/PasswordHashing.cs` — the one dependency this slice adds

**Decision 1, LOCKED.** Add a package reference to **`Microsoft.AspNetCore.Identity`** and use
**`PasswordHasher<TUser>`** from it, and nothing else from it.

```csharp
// Slices/Identity/Application/PasswordHashing.cs
public interface IPasswordHashing
{
    string Hash(string password);

    /// <summary>
    /// Verifies, and reports whether the stored hash used an older format and should be
    /// rewritten. Returns Failed for a null or empty stored hash — never true.
    /// </summary>
    PasswordVerification Verify(string? storedHash, string password);
}

public enum PasswordVerification { Failed, Success, SuccessRehashNeeded }
```

Implemented over `new PasswordHasher<UserAccount>()`, which gives PBKDF2-HMAC-SHA512 with 210,000
iterations and a self-describing versioned format.

Rules:

1. **Reference `Microsoft.AspNetCore.Identity`, not
   `Microsoft.AspNetCore.Identity.EntityFrameworkCore`.** The latter brings `IdentityDbContext`,
   `IdentityUser`, and an EF-migrations-shaped model that would fight one-DbContext-per-slice, raw
   SQL migrations, and snake_case columns simultaneously. We want one class from one package.
2. **Do not use `UserManager`, `SignInManager`, `IdentityOptions`, or `AddIdentity()`.** They own
   the whole account lifecycle — lockout, token generation, claims, stores — and this slice already
   specifies all of it differently. Half-adopting ASP.NET Core Identity produces two lockout
   implementations that disagree.
3. **`Verify` returning `SuccessRehashNeeded` must actually rehash.** `PasswordHasher` returns it
   when the stored hash used fewer iterations or an older format; the login handler then writes the
   new hash. Ignoring it means the upgrade never happens and the return value is decoration.
4. **`Verify(null, password)` returns `Failed` — but only *after* doing the same work a real
   verification would.** An `Invited` account has a null hash, and so does "no such account" in
   §7.1. Passing null into the underlying hasher throws, and a `500` on the login path both leaks
   that the account exists in that state and violates §8 of the architecture doc. **An early
   `return Failed` is equally wrong**, for a reason that is invisible locally: it makes the null
   case return in microseconds while a real account costs ~100 ms of PBKDF2, and that difference is
   measurable over the network. It is the enumeration oracle §7.1 rule 1 exists to close, and
   §7.1 cannot close it on its own — the defence has to live here, in the only place that knows how
   long a hash takes.

   So the implementation is, precisely:

   ```csharp
   // Computed ONCE in the constructor — this type is a singleton (rule 6).
   private readonly string _dummyHash = new PasswordHasher<UserAccount>()
       .HashPassword(null!, "timing-defence-dummy-password");

   public PasswordVerification Verify(string? storedHash, string password)
   {
       if (string.IsNullOrEmpty(storedHash))
       {
           _hasher.VerifyHashedPassword(null!, _dummyHash, password);  // work, then discard
           return PasswordVerification.Failed;
       }
       // … the real comparison …
   }
   ```

   **Do not "optimise" the discarded call away**, and do not compute `_dummyHash` per request —
   hashing it on every login doubles the cost of the hottest path in the application. There is a
   test for the timing property; it is checking security, not performance.
5. **Never log a password, a hash, or any prefix of either**, at any level, including `Debug`.
6. Register as a **singleton**. `PasswordHasher<T>` is stateless and thread-safe, and constructing
   one per request is pointless allocation on the hottest path.

### 3.2 `Application/TokenIssuing.cs`

```csharp
public interface ITokenIssuing
{
    /// <summary>Returns the raw token — the ONLY time it exists. Persist nothing but the hash.</summary>
    string GenerateRawToken();

    /// <summary>Lowercase hex SHA-256. Deterministic: the same input always gives the same output.</summary>
    string HashToken(string rawToken);
}
```

Rules:

1. **`RandomNumberGenerator.GetBytes(32)`**, then `Base64Url` encoding — 32 bytes of CSPRNG output,
   43 URL-safe characters. **Never `System.Random`, never `Guid.NewGuid()`.** A `Guid` is not a
   secret: v4 has 122 bits but no guarantee of cryptographic generation, and a builder who reaches
   for `Guid` here has reached for the wrong tool for the right-looking reason.
2. **`Base64Url`, not plain Base64.** The token goes in a URL query string. `+`, `/`, and `=`
   survive some URL handling and not others, and the failure is an occasional invalid token that
   nobody can reproduce.
3. **The hash is plain SHA-256 with no salt and no work factor, and that is correct.** This is not
   a password. It is 256 bits of uniform random, so there is no dictionary to attack and nothing
   for a salt to defend against; and lookup must be a single indexed equality on `token_hash`,
   which a per-row salt makes impossible.
4. **Look tokens up by hash.** `WHERE token_hash = @hash`, using the unique index. Do not load
   candidate tokens and compare in memory.
5. **Constant-time comparison is not needed here** and you should not add one, because the
   comparison happens inside PostgreSQL's index lookup, not in your code. Do not let this rule
   tempt you into loading rows so you can compare them "safely".

### 3.3 The password policy

One place: `Application/PasswordPolicy.cs`, called by every handler that accepts a new password
(§7.4, §7.6, §7.7).

- **Minimum 12 characters. Maximum 128.**
- **No composition rules** — no required uppercase, digit, or symbol. This follows NIST SP 800-63B:
  composition rules push people toward `Password1!` and add far less entropy than length. Do not
  add them because they look more secure.
- **Reject a password equal to the login email**, case-insensitively.
- Violations are **`422`**, with a message naming the actual rule that failed. Never `500` — this
  is a client-supplied value, and `App/GeneralAppArchitecture.md` §8 is explicit that a
  client-triggerable value is always a `4xx`.

The maximum of 128 is not cosmetic: PBKDF2 hashes whatever it is given, and an endpoint that
accepts a 10 MB password is an endpoint that burns CPU on request. Enforce it **before** hashing.

---

## 4. Cookie authentication

`04-Infrastructure.md` §4 and `App/GeneralAppArchitecture.md` §9 are both categorical:
**there are no JWTs, no bearer tokens, and nothing in `localStorage`.** Sessions are a cookie.

### 4.1 The scheme

Configured in `IdentityRegistration.cs` (§12), not scattered in `Program.cs`:

```csharp
services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name        = "aa_session";
        options.Cookie.HttpOnly    = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite    = SameSiteMode.Strict;
        options.Cookie.Path        = "/";

        options.ExpireTimeSpan     = TimeSpan.FromHours(8);   // decision 4
        options.SlidingExpiration  = true;                    // renews past the halfway point

        // The SPA must receive a status code, never a redirect to a login page.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
```

Five rules:

1. **`SecurePolicy.Always`, not `SameAsRequest`.** The app is served over HTTPS through Caddy
   (`04-Infrastructure.md` §3) and the API sees plain HTTP behind the proxy, so `SameAsRequest`
   silently drops the `Secure` flag in production — the one environment where it matters.
   `UseForwardedHeaders` must be configured (`04-Infrastructure.md` §3) for the proxy scheme to be
   visible at all; verify it is, because the same misconfiguration also makes every audit entry
   record Caddy's IP instead of the caller's.
2. **`SameSite=Strict`, not `Lax`.** The SPA is served from the same origin as the API
   (`04-Infrastructure.md` §1), so `Strict` costs nothing. The one thing it breaks is following a
   link from an email straight into an authenticated page — and invitation and reset links land on
   *unauthenticated* endpoints, so it does not affect them.
3. **The two `OnRedirectTo…` overrides are mandatory.** Without them an expired session gets a
   `302` to `/Account/Login`, which does not exist, and the SPA's `fetch` sees a `200` with HTML.
   The symptom is "my app randomly shows the index page inside a JSON parse error".
4. **The cookie carries no secret and no role decision.** It carries the claims in §4.2 and is
   signed by data protection. Authorization still runs `IPermissionChecker` on every request; a
   valid cookie proves identity, never permission.
5. **Never set an explicit `Cookie.Domain`.** Leaving it unset scopes the cookie to the exact host,
   which is what one-domain deployment wants; setting it widens the cookie to subdomains.

### 4.2 The claims written at sign-in

`LoginHandler` builds a `ClaimsPrincipal` with exactly these:

| Claim | Value | Why |
|---|---|---|
| `ClaimTypes.NameIdentifier` | `account.Id.ToString()` | Becomes `CurrentUser.Id` |
| `ClaimTypes.Role` | `account.Role.ToString()` | Becomes `CurrentUser.Role` |
| `customer_id` | `account.CustomerId`, **only when non-null** | Becomes `CurrentUser.CustomerId`. Mandatory for the two Customer-side roles (§0.2) |
| `display_name` | `account.DisplayName` | So `/api/auth/me` needs no database read |
| `must_change_password` | `"true"` / `"false"` | Read by the middleware in §5 |

Rules:

1. **Do not add a claim for anything a handler can look up and that can change.** A claim is a
   snapshot taken at login and valid for up to 8 hours. Customer status in particular must never
   become a claim — it is read live (§7.1).
2. **Omit `customer_id` entirely for Accountants; do not write an empty string.**
   `CurrentUserFactory` treats a present-but-unparseable value as a `401`, so `""` would lock out
   every Accountant. Read the factory and match it.
3. **`must_change_password` is the one mutable value that is a claim**, and it is safe in the one
   direction that matters: the flag only ever goes from `true` to `false`, and the handler that
   clears it (§7.4) re-issues the cookie in the same request. A stale `true` costs one extra
   password-change prompt; a stale `false` would be the bug, and it cannot happen.

### 4.3 Data protection keys

The cookie is protected by ASP.NET Core data protection. By default the keys are generated per
process and kept in memory or in a user profile that does not exist in a container.

**Consequence if you skip this: every deploy and every container restart signs out every user**,
and it will be reported as "the app logs me out randomly". `04-Infrastructure.md` §4 requires the
keys be persisted to a mounted volume or the database.

```csharp
services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
    .SetApplicationName("AccountantApp");
```

- `keyPath` comes from configuration (`DataProtection:KeyPath`), mapped to a Docker volume.
- **`SetApplicationName` is not optional.** It is part of the key-derivation purpose; changing or
  omitting it invalidates every existing cookie.
- **Fail startup if the path is missing or not writable**, rather than falling back to ephemeral
  keys. The fallback works perfectly in testing and quietly fails in production, which is the worst
  available combination.

### 4.4 What must not appear anywhere in this slice

A checklist, because each of these will be suggested by muscle memory or by an example found
online, and each is forbidden by a locked decision:

- `AddJwtBearer`, `JwtSecurityTokenHandler`, `SymmetricSecurityKey`, a `JwtOptions` section, or a
  signing key in configuration — `04-Infrastructure.md` §4
- An `Authorization: Bearer` header, or any refresh-token concept
- A token in a response body, in `localStorage`, or in `sessionStorage`
- A `sessions` table. Cookie authentication is self-contained; a server-side session table would
  need its own expiry sweep and would be a second source of truth about who is logged in
- `AddIdentity`, `AddIdentityCore`, `UserManager`, `SignInManager` — §3.1 rule 2

**There is no "revoke a session" operation in v1, and that is a real, accepted limitation.**
Suspending an account stops the *next* login and every permission check that reads status, but an
already-issued cookie stays cryptographically valid until it expires. Record it as a known
constraint (§17); do not solve it by inventing a session table.

---

## 5. The forced-password-change middleware

**Decision 5, LOCKED.** `App/GeneralAppArchitecture.md` §9: *"the first Admin logs in with the
seeded password. The app must force a password change before they can do anything else."*

**File:** `Shared/Auth/MustChangePasswordMiddleware.cs` — in `Shared/`, not in this slice, because
it applies to every endpoint of every slice.

```
if the request has no authenticated principal        → next()
if the must_change_password claim is not "true"      → next()
if the request path is one of the three exemptions   → next()
otherwise → 403 with ProblemDetails and
            extensions["code"] = "password_change_required"
```

The three exemptions, and only these three:

| Path | Why |
|---|---|
| `/api/auth/change-password` | The way out. Blocking it is a deadlock with no recovery except editing the database. |
| `/api/auth/logout` | Somebody must be able to leave. |
| `/api/auth/me` | The SPA needs to read `mustChangePassword` to know where to send the user. |

Rules:

1. **It is a deny-by-default allowlist, and that is the whole point.** A new endpoint added in a
   later slice is blocked automatically, because nobody has to remember to guard it. An
   implementation that instead names the paths to *block* is wrong, and will be wrong differently
   every time a slice ships.
2. **Match paths exactly and case-insensitively**, with `StringComparison.OrdinalIgnoreCase` on
   the full path. Not `StartsWith` — `/api/auth/change-password-and-do-something-else` would pass
   a prefix check.
3. **Register it after `UseAuthentication()` and before endpoint execution.** Before
   authentication there is no principal, so the first condition always short-circuits and the
   middleware does nothing at all — which looks exactly like it working.
4. **`403`, not `401`.** The caller is authenticated; they are not permitted. A `401` would make
   the SPA's interceptor log them out, so the user bounces between login and lockout forever.
5. **The machine-readable `code` extension is required.** The SPA must distinguish this `403` from
   a role denial, and it must not do so by matching on the message text.
6. **The middleware does not read the database.** It reads the claim (§4.2). A database read on
   every request of every endpoint to check a flag that is `false` for every account but one is a
   cost paid forever for a condition that occurs once per installation.

---

## 6. DTOs

`Application/Dtos/`. Records for responses, classes with settable properties for requests bound
from a body.

| DTO | Fields |
|---|---|
| `LoginRequestDto` | `Email`, `Password` |
| `SessionDto` | `UserId`, `DisplayName`, `Role`, `CustomerId` (nullable), `MustChangePassword` |
| `ChangePasswordRequestDto` | `CurrentPassword`, `NewPassword` |
| `RequestPasswordResetRequestDto` | `Email` |
| `CompletePasswordResetRequestDto` | `Token`, `NewPassword` |
| `AcceptInvitationRequestDto` | `Token`, `NewPassword`, `DisplayName` (optional override) |
| `InviteAccountantRequestDto` | `Email`, `DisplayName`, `Role` (must be an Accountant role) |
| `AccountantSummaryDto` | `Id`, `DisplayName` — **and nothing else**, see below |
| `AccountantDetailDto` | `Id`, `DisplayName`, `LoginEmail`, `Role`, `Status`, `CreatedAt`, `LastLoginAt` |
| `ListAccountantsRequestDto` | `PageNumber`, `PageSize` (`= 15`) |
| `AccountIdRequestDto` | `UserAccountId` — used by suspend, reactivate, promote, demote |
| `MarkedResultDto` | `Success` — for operations with nothing to return |

Rules:

1. **`SessionDto` carries no email and no token.** It answers "who am I and where must I go
   next"; it is not an account-detail response.
2. **`AccountantSummaryDto` has two fields, and that is a normative requirement, not minimalism.**
   Matrix §2: *"An Accountant User can see the list of Accountants, because assigning a ticket
   requires knowing who exists... Return names and identifiers only — not email addresses, login
   history, or status detail."* This is why there are **two** DTOs. The limitation must be a
   different type, not a nulled-out field on the detail DTO — a type that has no `LoginEmail`
   property cannot leak one, whereas a handler that must remember to null it out will one day
   forget. `AccountantDetailDto` is returned to `AccountantAdmin` only.
3. **No DTO in this slice has a `PasswordHash`, `TokenHash`, `Status` reason, or
   `FailedLoginCount` field.** Nothing outside this slice has any use for them.
4. **`InviteAccountantRequestDto.Role` is a string bound to `UserRole`, and a value of
   `CustomerAdmin` or `Employee` is a `422`**, not a silent coercion. This endpoint creates
   Accountant accounts; Customer-side accounts are created through `IIdentityApi` by `Employees`.
5. **No request DTO in this slice has a `UserId` field except `AccountIdRequestDto`**, which is
   used only by the four `AccountantAdmin`-only administration endpoints. In particular
   `ChangePasswordRequestDto` has **no** target user: matrix §11 says *"Reset another person's
   password directly — **Nobody.**"* A `userId` on the change-password DTO is the vulnerability, so
   the field must not exist — you cannot forget to validate a parameter you never accepted.

---

## 7. Handlers

`Application/Handlers/`, one file each, registered `AddTransient`.

### 7.0 Rules that apply to every handler in this slice

**A. The canonical signature**, except for the four unauthenticated handlers which take no
`CurrentUser`:

```csharp
public async Task<TResponse> Handle(TRequest req, CurrentUser user, CancellationToken ct)
```

**B. Authorization first, before any database read.** `RequireAsync` throws `403` and audits. A
handler that reads first and checks later has already leaked whether the row exists via its
timing, and will eventually leak it via an error message.

**C. One transaction per handler that writes.** `await using var scope = await
_transaction.BeginAsync(_db, ct);` … `await _transaction.CommitAsync(ct);`. The audit entry
enlists itself, so it commits or rolls back with the business change
(`App/GeneralAppArchitecture.md` §5).

> **D. The login-failure path must commit before it throws — read this twice.**
> `RequestTransaction.DisposeAsync` **rolls back** when `CommitAsync` was not called. So a handler
> that increments `failed_login_count` and then throws `AppException(401)` discards the increment.
> The counter is permanently zero, the lockout never triggers, and **the brute-force protection
> that decision 3 exists to provide does not exist** — while every test that checks "wrong password
> returns 401" still passes.
>
> Every handler in this slice that records a failure must `await _transaction.CommitAsync(ct)` and
> **then** throw. This applies to §7.1 and §7.4. It is the single most important paragraph in this
> plan.

**E. Never reveal whether an account exists.** Login, password reset, and invitation acceptance all
return the same response for "no such account" as for the real failure. §7.1 rule 3 and §7.5 rule 1
give the specifics. Matrix §1's reasoning about `404`-not-`403` is the same principle: a
distinguishable response is an enumeration oracle, and here the enumerable set is every email
address of every person the Office works with.

**F. Audit every outcome, including the failures.** Unlike other slices, the *failed* operations
here are the interesting ones. `AuditActions` already contains every code this slice needs — do not
add one:

| Handler | Action code | Outcome |
|---|---|---|
| `LoginHandler` | `LoginSucceeded` / `LoginFailed` | `Success` / `Failure` |
| `LoginHandler`, on lockout | `AccountLockedOut` | `Failure` |
| `LogoutHandler` | `LoggedOut` | `Success` |
| `ChangeOwnPasswordHandler` | `PasswordChanged` | `Success` |
| `RequestPasswordResetHandler` | `PasswordResetRequested` | `Success` |
| `CompletePasswordResetHandler` | `PasswordResetCompleted` | `Success` |
| `AcceptInvitationHandler` | `InvitationAccepted` | `Success` |
| `InviteAccountantHandler` | `AccountantAccountCreated`, then `AccountInvited` | `Success` |
| `SuspendAccountantHandler` | `AccountSuspended` | `Success` |
| `ReactivateAccountantHandler` | `AccountReactivated` | `Success` |
| `PromoteAccountantHandler` | `AccountantPromoted` | `Success` |
| `DemoteAccountantHandler` | `AccountantDemoted` | `Success` |

**G. No audit entry ever carries a password, a raw token, or a hash.** `Before`/`After` on an
account change record status and role only. `Audit`'s redaction helper exists, but do not rely on
it to catch a field you should not have passed.

**H. `CustomerScope` is not used in this slice.** `UserAccount` does **not** implement
`ICustomerScoped`, and `WhereInCustomerScope` is never called. Nothing here is listed to a
Customer-side caller: the only list endpoint is Accountant-only, and every other read is
"my own session". Adding the interface would invite a scoped query that returns *other people's
accounts within the same Customer*, which no endpoint in this slice is permitted to do.

### 7.1 `LoginHandler` — the most important handler in the application

**Injects:** `IdentityDbContext`, `IPasswordHashing`, `ICustomerApi`, `IRequestTransaction`,
`IAuditApi`, `IHttpContextAccessor`. **Not** `CurrentUser` (§0.3). **Not** `IPermissionChecker` —
there is no role to check yet.

`LoginRequestDto` → `SessionDto`.

The order of operations is not negotiable:

```
now         = DateTimeOffset.UtcNow
normalized  = req.Email.Trim().ToLowerInvariant()

begin transaction

account = await _db.UserAccounts
    .FirstOrDefaultAsync(u => u.NormalizedLoginEmail == normalized, ct)

# 1. Always verify a password, even when there is no account.
verification = _hashing.Verify(account?.PasswordHash, req.Password)

# 2. Locked out? Reject without touching the counter.
if account is not null and account.IsLockedOut(now):
    → fail("LoginFailed", account.Id)

# 3. Wrong password, or no such account.
if verification == Failed:
    if account is not null:
        account.FailedLoginCount += 1
        if account.FailedLoginCount >= 5:
            account.LockoutExpiresAt = now.AddMinutes(15)
            account.FailedLoginCount = 0
            audit AccountLockedOut
        await _db.SaveChangesAsync(ct)
    → fail("LoginFailed", account?.Id)

# 4. Right password, but the account may not log in.
if account.Status != Active:
    → fail("LoginFailed", account.Id)

# 5. Customer-side roles: the Customer must also be Active. Read live, every time.
if account.Role is CustomerAdmin or Employee:
    if not await _customers.IsActiveAsync(account.CustomerId!.Value, ct):
        → fail("LoginFailed", account.Id)

# 6. Success.
if verification == SuccessRehashNeeded:
    account.PasswordHash = _hashing.Hash(req.Password)
account.FailedLoginCount = 0
account.LockoutExpiresAt = null
account.LastLoginAt      = now
await _db.SaveChangesAsync(ct)

audit LoginSucceeded
await _transaction.CommitAsync(ct)                  # commit BEFORE issuing the cookie

await httpContext.SignInAsync(scheme, principal)    # claims per §4.2
return session dto

# fail(action, accountId):
#   audit via LogUnauthenticatedAsync(normalized, ...)
#   await _transaction.CommitAsync(ct)               # rule D — or the counter is lost
#   throw new AppException("Invalid email or password.", 401)
```

Rules:

1. **Step 1 runs the hash verification even when no account was found**, against `null`, which
   `IPasswordHashing.Verify` handles by returning `Failed` (§3.1 rule 4) — *after* doing comparable
   work. Without this, "no such account" returns in microseconds while a real account takes ~100 ms
   of PBKDF2, and the difference is measurable over the network. That timing gap is a working
   account-enumeration oracle against every email address the Office holds.

   > If `Verify(null, …)` returns early without hashing, the mitigation does not work. Hash the
   > supplied password against a fixed dummy hash generated once at startup, then discard the
   > result. Do not "optimise" this away; there is a test for it, and the test is checking a
   > security property, not performance.

2. **The lockout check precedes the password check** (step 2), and a locked-out account's counter is
   **not** incremented. Otherwise an attacker holds an account locked indefinitely by continuing
   to guess, which turns brute-force protection into a denial-of-service tool against a named
   person.

3. **Every failure returns the identical response**: `401` with the message
   `"Invalid email or password."` — for a nonexistent account, a wrong password, an `Invited`
   account, a `Suspended` account, a locked-out account, and a suspended Customer. Six distinct
   causes, one response.

   The cost is real and accepted: a locked-out user is not told they are locked out and will
   retry, and support resolves it by reading the audit log. That is the correct trade. Distinct
   messages let an attacker map which addresses are accounts, which are Accountants, and which
   Customers are suspended, without ever guessing a password. **The audit log records which cause
   it was — that is where the distinction belongs.**

4. **Step 5 calls `ICustomerApi.IsActiveAsync` on every login, and caches nothing.** Matrix §11
   requires suspension to block login *immediately*. `IsActiveAsync` returns `false` for both
   "suspended" and "no such Customer", which is the fail-closed answer
   ([Customers plan](../Customers/IMPLEMENTATION_PLAN.md) §5 rule 7). Do not write
   `FindAsync(...)?.IsActive ?? true` — the `?? true` turns "I could not find that Customer" into
   "let them in".

   Note the converse, which is correct and will look like a bug: a `Suspended` **account** at an
   `Active` Customer cannot log in, and an `Active` account at a `Suspended` Customer cannot log
   in, and reactivating the Customer does not reactivate the account (matrix §11).

5. **Accountants skip step 5 entirely.** They have no `CustomerId`, and matrix §11 says
   *"Accountants of both roles are unaffected"* by Customer suspension. Calling `IsActiveAsync`
   with `Guid.Empty` for an Accountant would lock the entire Office out the first time it ran.

6. **Commit before `SignInAsync`.** A cookie issued for a transaction that then fails to commit is
   a session for a login that did not happen — including a `LastLoginAt` that was rolled back, and
   in the seeding case a `MustChangePassword` state that is now inconsistent with the claim.

7. **Audit failures with `LogUnauthenticatedAsync`**, passing the normalized email as the actor
   identifier. `LogAsync` resolves `CurrentUser` and throws `401` (§0.3), which would replace the
   intended `401` with an identical-looking one that recorded nothing.

8. **`FailedLoginCount` resets to 0 when the lockout is applied**, not when it expires. The lockout
   timestamp is the state; the counter starts the next window. Leaving the counter at 5 means the
   very next failure after expiry re-locks immediately.

### 7.2 `LogoutHandler`

**Injects:** `IRequestTransaction`, `IAuditApi`, `IHttpContextAccessor`. Takes `CurrentUser`.

`await httpContext.SignOutAsync(scheme)`, audit `LoggedOut`, commit.

1. **No `RequireAsync`.** Every role may end their own session.
2. **Idempotent.** Called without a valid cookie it returns `200`, having done nothing. A `401` on
   logout leaves the SPA unable to clear a session it cannot use — the worst possible state.
3. **It does not invalidate the cookie server-side, because it cannot** (§4.4). It clears the
   cookie in the browser. A copy captured earlier stays valid until it expires; that is the known
   constraint in §17.

### 7.3 `GetCurrentSessionHandler`

**Injects:** `IHttpContextAccessor`, and nothing else — no DbContext, no `IPermissionChecker`, no
`IAuditApi`. Takes `CurrentUser`, returns `SessionDto`. Exempt from the §5 middleware.

1. **No `RequireAsync`, and no database read.** Everything in `SessionDto` is already a claim
   (§4.2). This endpoint is called on every page load of the SPA; a database round trip for data
   the cookie already carries is a query per navigation, forever.

   > `CurrentUser` is `record CurrentUser(string Id, UserRole Role, Guid? CustomerId = null)` — it
   > carries **three** of `SessionDto`'s five fields. `DisplayName` and `MustChangePassword` are
   > claims but not properties of `CurrentUser`, so read them off
   > `IHttpContextAccessor.HttpContext.User` with `FindFirstValue("display_name")` and
   > `FindFirstValue("must_change_password")`. **Do not add a database read to get them, and do not
   > widen `CurrentUser`** — every other slice depends on that record's shape, and adding a field
   > that only this slice can populate makes it lie everywhere else.
2. **`401` when unauthenticated** — which happens automatically, because `CurrentUser`'s factory
   throws it. Do not catch it to return an anonymous session; the SPA needs the status code.

### 7.4 `ChangeOwnPasswordHandler`

**Injects:** `IdentityDbContext`, `IPasswordHashing`, `IRequestTransaction`, `IAuditApi`,
`IHttpContextAccessor`. Takes `CurrentUser`.

1. **No `RequireAsync`** — matrix §11 grants this to all four roles.
2. **The target is always `user.Id`.** There is no parameter for whose password to change (§6
   rule 5).
3. **Verify `CurrentPassword` first.** A session hijacker who can change the password without
   knowing the old one owns the account permanently. This check is the reason the endpoint takes
   two fields.
4. **A wrong `CurrentPassword` is `401`, and it increments `FailedLoginCount`** exactly as a failed
   login does, with the same 5-attempt lockout — otherwise this endpoint is an unthrottled password
   oracle for anyone holding a stolen cookie. **Commit before throwing** (rule D).
5. **Validate the new password against `PasswordPolicy`** (§3.3) and reject a new password equal to
   the current one (`422`).
6. On success: write the new hash, set `LastPasswordChangeAt`, **set `MustChangePassword = false`**,
   reset `FailedLoginCount` and `LockoutExpiresAt`, audit `PasswordChanged`, commit.
7. **Consume every outstanding `PasswordReset` token for this account** —
   `UPDATE … SET consumed_at = NOW() WHERE user_account_id = @id AND purpose = 'PasswordReset' AND
   consumed_at IS NULL`. Someone who has just proved they know the current password has no use for
   an old reset link, and an attacker who triggered a reset and then obtained the session should
   not keep a second way in.
8. **Re-issue the cookie with `SignInAsync` after the change**, because the
   `must_change_password` claim has changed. Skip this and the forced-change middleware keeps
   blocking the user for up to 8 hours after they have complied — which is a total lockout of the
   first Admin on a fresh installation, i.e. the very first thing anyone will try.

### 7.5 `RequestPasswordResetHandler`

**Injects:** `IdentityDbContext`, `ITokenIssuing`, `INotificationApi`, `IRequestTransaction`,
`IAuditApi`, `IConfiguration`. **Unauthenticated** — no `CurrentUser`.

1. **Always return `200` with an empty body, whatever happens.** No such account, a `Suspended`
   account, an `Invited` account — all `200`. Anything else makes this endpoint a free account
   enumerator, and unlike login it needs no password guess at all. This is the most easily
   overlooked instance of rule E and the most valuable one to an attacker.
2. **Only an `Active` account actually gets a token and an email.** For any other status, do the
   audit and return. Do not send "your account is suspended" mail — that confirms the address is
   registered.
3. **Consume outstanding `PasswordReset` tokens for the account before issuing a new one.** Two
   live reset links doubles the exposure for no benefit, and "the newest link works" is what people
   expect.
4. Issue: `raw = _tokens.GenerateRawToken()`, store a row with `HashToken(raw)`,
   `purpose = 'PasswordReset'`, `expires_at = now + 1 hour`.
5. **Send through `INotificationApi` with `EmailBody` set** — event kind
   `NotificationEvents.PasswordResetRequested`:

   ```csharp
   await _notifications.NotifyAsync(new NotificationRequest(
       RecipientUserId: account.Id.ToString(),
       EventKind:       NotificationEvents.PasswordResetRequested,
       Title:           "Password reset requested",
       Body:            "A password reset link was emailed to you.",   // stored, redacted
       EmailBody:       $"…{baseUrl}/reset-password?token={raw}…"),      // emailed, then blanked
       ct);
   ```

   > **Use named arguments, as above.** The real record is
   > `NotificationRequest(string RecipientUserId, string EventKind, string Title, string Body,
   > Guid? TicketId = null, string? EmailBody = null)` — there is a `TicketId` parameter
   > **between `Body` and `EmailBody`** that nothing in this slice sets. Passing the arguments
   > positionally puts the reset link in `TicketId` and fails to compile, or worse, if the shape ever
   > shifts, puts it somewhere that does compile. This applies equally to §7.8 point 5.

   **The raw token goes in `EmailBody` and nowhere else.** `Body` is persisted in `notifications`
   forever and must never contain it — see
   [the Notifications plan](../Notifications/IMPLEMENTATION_PLAN.md) §1, "Why `email_body`
   exists". Putting the link in `Body` compiles, works end to end in testing, and silently defeats
   the reason `user_account_tokens` stores only a hash.

6. **`baseUrl` comes from configuration (`App:BaseUrl`) and startup fails if it is missing.** Never
   build the link from the request's `Host` header — that is attacker-controlled, and a host-header
   injection here mails a working reset token to a domain of the attacker's choosing.
7. **Audit `PasswordResetRequested` via `LogUnauthenticatedAsync`**, with the normalized email as
   the actor. Audit it even when no account was found: a burst of requests for addresses that do
   not exist is exactly what someone probing for accounts looks like, and the audit log is where
   that becomes visible.

### 7.6 `CompletePasswordResetHandler`

**Unauthenticated.** `CompletePasswordResetRequestDto` → `MarkedResultDto`.

```
hash = _tokens.HashToken(req.Token)
token = await _db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct)

if token is null or not token.IsRedeemable(now) or token.Purpose != PasswordReset:
    → 400 "This link is invalid or has expired."

account = await _db.UserAccounts.FirstAsync(u => u.Id == token.UserAccountId, ct)
if account.Status != Active:
    → 400, same message

validate new password (§3.3)
account.PasswordHash          = _hashing.Hash(req.NewPassword)
account.LastPasswordChangeAt  = now
account.MustChangePassword    = false
account.FailedLoginCount      = 0
account.LockoutExpiresAt      = null          # a reset clears a lockout
token.ConsumedAt              = now
audit PasswordResetCompleted (LogUnauthenticatedAsync, actor = account.Id)
commit
```

1. **One message for every rejection**: unknown token, expired token, already-consumed token, wrong
   purpose, non-`Active` account. `400`, not `404` — a `404` invites a client to distinguish
   "no such token" from "expired", which is exactly the distinction not to make.
2. **`purpose` is checked even though the hash is unique.** An invitation token must not be
   redeemable as a password reset: invitation tokens live 7 days rather than 1 hour, and accepting
   an invitation is also what confirms the email address (§7.7). Cross-purpose redemption is the
   classic token bug and the check is one comparison.
3. **Set `ConsumedAt` in the same transaction as the password write.** Separate transactions leave a
   window where the password changed and the token is still live.
4. **A successful reset clears the lockout.** Someone who proved control of the mailbox should not
   have to wait out a lockout caused by the attacker who prompted the reset.
5. **Do not sign the user in.** Redirect them to log in with the new password. Auto-login here
   means the reset link alone is a session, so a link forwarded, logged by a mail gateway, or left
   in a browser history is a full account takeover with no password needed.
6. **`MustChangePassword = false`.** Otherwise a first Admin who resets rather than changes their
   seeded password stays permanently blocked by the §5 middleware.

### 7.7 `AcceptInvitationHandler`

**Unauthenticated.** `AcceptInvitationRequestDto` → `MarkedResultDto`.

Structurally §7.6, with four differences:

1. **`purpose` must be `Invitation`**, and the account's status must be **`Invited`**, not
   `Active`. Re-accepting an already-accepted invitation is the same single `400`.
2. **It sets `Status = Active`**, which is the transition that gives the account a usable
   credential for the first time.
3. **It sets `EmailConfirmedAt = now`.** Decision 6: accepting the invitation *is* the email
   confirmation, because the token was delivered only to that address, so redeeming it proves
   control of the mailbox. There is no separate confirm-email endpoint, no second email, and no
   state in which an `Active` account has an unconfirmed address. Do not add one.
4. **`DisplayName` may be overridden**, since the inviter typed a placeholder and the person
   knows their own name. Validate 1–200 characters; ignore it when absent rather than blanking the
   existing value.

Also: **`MustChangePassword` is `false`** on an accepted invitation. The person just chose the
password themselves — forcing an immediate second change is the kind of detail that makes people
write it on a note.

Audit `InvitationAccepted` with `LogUnauthenticatedAsync`.

### 7.8 `InviteAccountantHandler`

`RequireAsync(user, "InviteAccountant")` — **`AccountantAdmin` only** (matrix §2).
`InviteAccountantRequestDto` → `AccountantDetailDto`.

1. **`req.Role` must be `AccountantAdmin` or `AccountantUser`.** Any other value is `422`. This
   endpoint cannot create a Customer-side account — those come from `Employees` through
   `IIdentityApi` (§9.1), which is the only path that can supply the mandatory `employee_id` and
   `customer_id`.
2. **`employee_id` and `customer_id` are `NULL`.** An Accountant has no Employee record
   (`01-DomainModel.md` §2), and the `ck_user_accounts_scope` constraint enforces it.
3. **Pre-check the normalized email for uniqueness and return `409`** — *and also* catch
   `DbUpdateException` wrapping a `PostgresException` with `SqlState == "23505"` and map it to the
   same `409`. The pre-check is a courtesy that produces a good message; the constraint is the
   guarantee. Two Admins inviting the same address concurrently will otherwise produce a `500`.
4. **The account is created `Invited` with `password_hash = NULL`.** Never generate a temporary
   password and never email one. A password in an inbox is a password in an inbox forever.
5. **Issue an `Invitation` token, 7-day expiry**, and notify with
   `NotificationEvents.Invited` — `EmailBody` carrying `{baseUrl}/accept-invitation?token={raw}`,
   `Body` carrying the redacted text. Identical rules to §7.5 point 5, for the same reason.
6. **Two audit entries, in order: `AccountantAccountCreated` then `AccountInvited`.** They are
   distinct facts — the row came into existence, and an invitation went out — and re-inviting later
   writes only the second.
7. **Everything is one transaction**: the account row, the token row, the notification, the outbox
   row, and both audit entries. A committed account with no invitation is a login identifier
   reserved for somebody who was never told, and it blocks the address from being invited again
   without an Admin noticing why.
8. **Returns `AccountantDetailDto`, never the raw token.** The inviter does not need the link, and
   an Admin who can read it can impersonate the invitee before they ever log in.

### 7.9 `ListAccountantsHandler`

`RequireAsync(user, "ListAccountants")` — **both** Accountant roles (matrix §2: an Accountant User
must see who exists in order to assign a ticket).

Returns `PaginatedResponse<AccountantSummaryDto>` **or** `PaginatedResponse<AccountantDetailDto>`,
depending on the caller's role.

**The signature is `Task<object>`, and that is LOCKED.** It is the one handler in the codebase that
does not return a concrete DTO, so here is why, and what not to do instead:

```csharp
public async Task<object> Handle(
    ListAccountantsRequestDto req, CurrentUser user, CancellationToken ct)
```

`System.Text.Json` serialises the **runtime** type of an `object`-declared value, so an
`AccountantUser` receives a body with exactly two keys and there is no code path on which a
`LoginEmail` could be written — which is the §6 rule 2 guarantee, preserved. The route declares
`.Produces<PaginatedResponse<AccountantDetailDto>>(200)` as the documented superset shape, with a
comment saying an `AccountantUser` gets the summary shape.

Rejected alternatives, each of which a builder will be tempted by:

- **A single DTO with nullable detail fields.** Forbidden by §6 rule 2 — the whole point is that the
  restricted shape is a type that *cannot* carry an email.
- **A wrapper record holding both paginated responses, one of them null.** Serialises a `details:
  null` key to an `AccountantUser`, which advertises the existence of the shape they were denied.
- **Two endpoints, one per role.** The role decision must be made server-side from the cookie, not
  by which URL the client chose to call.

1. **`AccountantUser` gets `AccountantSummaryDto` — id and display name only.** `AccountantAdmin`
   gets `AccountantDetailDto`. Matrix §2 requires the restriction; §6 rule 2 explains why it is a
   different type rather than a nulled field.

   > Implement this as **two projections behind one `if` on the role**, each selecting only its own
   > columns. Do not project the detail DTO and then strip fields: the wide row travels through the
   > application, and every future maintainer sees a `LoginEmail` in scope with nothing but a
   > comment stopping them from serialising it.

2. **Reject Customer-side roles at the role check**, which the catalogue does by listing only the
   two Accountant roles. It must not return an empty page — matrix §12 rule 4: *"Never expose a
   cross-Customer listing endpoint to a Customer-side role, not even one returning an empty
   list."* This list is every Accountant in the Office, which is precisely cross-Customer data.
3. **Filter `WHERE role IN ('AccountantAdmin','AccountantUser')`**, matching
   `idx_user_accounts_accountants`. Without the filter this endpoint lists every Employee account
   of every Customer to any Accountant User, which is a different endpoint than the one the matrix
   authorized.
4. **Include `Suspended` and `Invited` Accountants**, and expose status to the Admin only. An Admin
   needs to see a suspended colleague in order to reactivate them; an `AccountantUser` gets neither
   the status nor a filtered list, because "who can I assign a ticket to" is answered by
   `IIdentityApi` (§9.1), not by this endpoint.
5. Order `display_name ASC, id ASC`. Paginate per §0.4.

### 7.10 `SuspendAccountantHandler`

`RequireAsync(user, "SuspendAccountant")` — `AccountantAdmin` only.
`AccountIdRequestDto` → `MarkedResultDto`.

1. **The target must be an Accountant.** A `CustomerAdmin` or `Employee` account is `404` — not
   `403`. Customer-side account suspension belongs to `Employees` (matrix §4), and a `403` here
   would confirm the account exists to an Admin using the wrong endpoint. Route the caller to the
   right slice by not answering.
2. **Self-suspension is `422`.** `01-DomainModel.md` §2: *"An Accountant Admin cannot suspend,
   demote, or delete their own account."* Compare `req.UserAccountId.ToString()` with `user.Id`.
3. **The at-least-one-`Active`-`AccountantAdmin` guard runs here** — §8.
4. **Already `Suspended` is `422`, not a silent success.** A no-op `200` tells an Admin the
   operation had an effect when it had none, and it writes a misleading audit entry.
5. **Suspension writes one row.** It does not touch tickets, notifications, assignments, or
   sessions. An already-issued cookie stays valid until it expires (§4.4, §17).
6. Notify the suspended account with `NotificationEvents.AccountSuspended` — in-app only; that kind
   is not in `Emailed`.
7. Audit `AccountSuspended` with `Before`/`After` carrying the status only.

### 7.11 `ReactivateAccountantHandler`

The inverse, `AccountantAdmin` only. Deliberately a **separate handler**, not a
`SetStatus(status)` handler with a parameter.

1. **The target must be `Suspended`.** Reactivating an `Active` account is `422`; reactivating an
   `Invited` account is `422` with a message saying to re-invite instead — `Invited → Active`
   without a password would produce an `Active` account with a null hash that can never log in and
   never be invited again.
2. **Reactivation does not reset the password, clear a lockout, or re-send anything.** It restores
   status. A returning Accountant who has forgotten their password uses the reset flow.
3. **No self-check is needed** — an Admin cannot have suspended themselves (§7.10 rule 2), so they
   cannot be reactivating themselves.
4. Audit `AccountReactivated`.

> Two handlers rather than one parameterised handler, for the same reason as in the `Customers`
> plan: the guards differ. Suspension needs the last-Admin check and the self-check; reactivation
> needs neither, and needs a rule about `Invited` that suspension does not have. A single handler
> with an `if (suspending)` inside it is where one of those four guards eventually goes missing.

### 7.12 `PromoteAccountantHandler`

`RequireAsync(user, "PromoteAccountant")` — `AccountantAdmin` only. Target must currently be
`AccountantUser`; anything else is `422`. Sets `role = 'AccountantAdmin'`. Audit
`AccountantPromoted` with `Before`/`After` roles.

1. **A Customer-side target is `404`**, as in §7.10 rule 1. Matrix §4 is explicit that Employee
   role changes are restricted to `CustomerAdmin` and `Employee` and that *"a request setting a
   role to either Accountant role is rejected outright"* — the mirror of that rule is that this
   endpoint never touches a Customer-side account.
2. **Promotion does not require the target to be `Active`.** Promoting an `Invited` Accountant is
   legitimate: the invitation was sent, the role was wrong, and re-inviting would mean a second
   email. It does not satisfy the at-least-one-**`Active`**-Admin rule, though — see §8.
3. **No claims are re-issued, and the target's current session keeps the old role for up to 8
   hours.** Accept it, record it in §17. It fails safe on promotion (they gain nothing until next
   login) and unsafe on demotion — which is why §7.13 has an extra rule.

### 7.13 `DemoteAccountantHandler`

`RequireAsync(user, "DemoteAccountant")` — `AccountantAdmin` only. Target must currently be
`AccountantAdmin`. Sets `role = 'AccountantUser'`. Audit `AccountantDemoted`.

1. **Self-demotion is `422`** — `01-DomainModel.md` §2, the same rule as self-suspension.
2. **The at-least-one-`Active`-`AccountantAdmin` guard runs here too** (§8). Demoting the last
   Active Admin is the other way to reach zero, and it is easier to overlook than suspension
   because it does not look destructive.
3. **A demoted Admin's live session retains `AccountantAdmin` for up to 8 hours** (§7.12 rule 3),
   and unlike promotion this direction fails **unsafe** — a demoted person keeps Admin powers until
   their cookie expires. Record it in §17 as a known constraint. **Do not fix it by having
   `IPermissionChecker` read the database on every call**; that puts a query on every authorized
   action in the system to close a window that ends by itself. If it must be closed, the fix is
   session revocation, which is out of scope for v1 and is a decision to raise, not to implement.

---

## 8. The two invariant guards

Both are stated in `01-DomainModel.md` §2 and matrix §2. Both are easy to write in a way that
looks right and is not. Put them in **one file**, `Application/AccountInvariants.cs`, called by
§7.10, §7.12, and §7.13 — not copied into each handler.

### 8.1 At least one `Active` `AccountantAdmin` must always exist

**The order is LOCKED: mutate → `SaveChangesAsync` → count → throw on zero → the transaction rolls
back.** Not the other way round, for the reason in rule 1.

```csharp
// Called INSIDE the handler's transaction, AFTER SaveChangesAsync has written the change.
public static async Task RequireAnActiveAdminRemainsAsync(
    IdentityDbContext db, CancellationToken ct)
{
    var activeAdmins = await db.UserAccounts.CountAsync(
        u => u.Role == UserRole.AccountantAdmin && u.Status == AccountStatus.Active, ct);

    if (activeAdmins == 0)
        throw new AppException("At least one active Accountant Admin must remain.", 422);
}
```

1. **Count *after* `SaveChangesAsync`, never before.** `CountAsync` is a database query, not a
   change-tracker query: it sees what has been written to the connection, and before
   `SaveChangesAsync` the pending mutation is not there. **A count taken before the save therefore
   always finds the very Admin it is about to remove, and the guard never fires** — it looks
   correct, it compiles, and the invariant it exists to protect is simply absent.

   Counting after the save works because the handler is inside a transaction on a single shared
   connection (§7.0 C), so the query sees the slice's own uncommitted write. Throwing then propagates
   out of the handler, `CommitAsync` is never reached, and `RequestTransaction.DisposeAsync` rolls
   the change back — which is why the row must be **unchanged** afterwards (§16.2, success
   criterion 20). Do not catch this exception to "clean up"; the rollback is the cleanup.
2. **The condition is `Active` **and** `AccountantAdmin`.** Counting Admins of any status passes
   when the only remaining Admin is `Suspended` or `Invited` — nobody can log in, and the only role
   that can fix it is the one that no longer exists. That is the unrecoverable state this guard
   exists for.
3. **`422`, not `403`.** The caller has the role; the operation is refused because of the state of
   the data. A `403` would suggest re-authenticating as somebody more powerful, and there is
   nobody more powerful — matrix §12 rule 6: *"Accountant Admin is the ceiling."*
4. **It runs inside the handler's transaction**, so a rejection rolls the change back.
5. **A concurrency note that matters:** two Admins suspending each other simultaneously can both
   pass a count taken in separate transactions and commit to zero. Under PostgreSQL's default
   `READ COMMITTED` this is a real interleaving. Mitigate by taking the count with
   `FOR UPDATE`-equivalent locking on the Admin rows, or accept it and record it in §17 — but do
   not leave it unmentioned, because "we locked ourselves out of the app" has no in-app recovery.

### 8.2 No self-action on one's own role or status

```csharp
if (string.Equals(target.Id.ToString(), user.Id, StringComparison.Ordinal))
    throw new AppException("You cannot change your own role or status.", 422);
```

Applies to **suspend and demote**. It does **not** apply to changing one's own password, which is
explicitly permitted by matrix §11.

1. **Compare `user.Id` — the claim — with the target id.** Do not re-derive the caller's identity
   from the database; the claim *is* the authenticated identity, and a second lookup is another
   chance to compare the wrong two things.
2. **It is a string comparison** because `CurrentUser.Id` is a `string`. Parse the target to a
   `Guid` and back, or compare `ToString()` on both, but do it in exactly one place — this file.
   The bug where a `Guid.ToString("D")` is compared to a `Guid.ToString("N")` and never matches
   turns this guard off silently.

---

## 9. ExternalInterfaces

### 9.1 `IIdentityApi` — what other slices may ask

**Files:** `Slices/Identity/ExternalInterfaces/IIdentityApi.cs`, `IdentityApi.cs`

Two slices call this: **`Employees`** (to create and manage the accounts of its people) and
**`Tickets`** (for `01-DomainModel.md` §9.8 — the pickup queue must show Tickets whose Assignee's
account is not `Active`, and for Accountant display names).

```csharp
public sealed record AccountSummary(
    Guid Id,
    string DisplayName,
    string LoginEmail,
    UserRole Role,
    string Status)
{
    public bool IsActive => Status == "Active";
}

public interface IIdentityApi
{
    // --- Reads, for Tickets and Employees ---

    /// <summary>Null when no such account exists.</summary>
    Task<AccountSummary?> FindAsync(Guid userAccountId, CancellationToken ct = default);

    /// <summary>Bulk lookup for list rendering. Missing ids are simply absent.
    /// Capped at 500 ids.</summary>
    Task<IReadOnlyDictionary<Guid, AccountSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> userAccountIds, CancellationToken ct = default);

    /// <summary>True only when the account exists AND is Active. This is what Tickets §9.8
    /// asks to decide whether an assignment is stranded.</summary>
    Task<bool> IsActiveAsync(Guid userAccountId, CancellationToken ct = default);

    /// <summary>The account belonging to an Employee, or null for an accountless one.</summary>
    Task<AccountSummary?> FindByEmployeeAsync(Guid employeeId, CancellationToken ct = default);

    /// <summary>Every Accountant of either role, for the assignee picker. Names and ids only.</summary>
    Task<IReadOnlyList<AccountSummary>> ListAccountantsAsync(
        bool activeOnly = true, CancellationToken ct = default);

    // --- Writes, for Employees only ---

    /// <summary>Creates an Invited account for an Employee, issues an invitation token, and
    /// queues the invitation email — all in the caller's transaction. Returns the new id.</summary>
    Task<Guid> InviteEmployeeAccountAsync(InviteEmployeeAccount request,
                                          CancellationToken ct = default);

    Task SuspendAccountAsync(Guid userAccountId, CancellationToken ct = default);

    /// <summary>Lifts a suspension. Restores the account to `Invited` when it has no password
    /// hash, `Active` when it has one — see rule 14.</summary>
    Task ReactivateAccountAsync(Guid userAccountId, CancellationToken ct = default);

    /// <summary>Sets a Customer-side account's role. Throws for an Accountant role or an
    /// Accountant target — see rule 5.</summary>
    Task SetCustomerSideRoleAsync(Guid userAccountId, UserRole role,
                                  CancellationToken ct = default);

    /// <summary>Moves a Customer-side account to a new login address. `409` when the address is
    /// taken; throws for an Accountant target — see rule 15.</summary>
    Task ChangeLoginEmailAsync(Guid userAccountId, string loginEmail,
                               CancellationToken ct = default);
}

public sealed record InviteEmployeeAccount(
    Guid EmployeeId,
    Guid CustomerId,
    string LoginEmail,
    string DisplayName,
    UserRole Role);          // CustomerAdmin or Employee only
```

Rules:

1. **It returns `AccountSummary`, never the `UserAccount` entity** (dependency rule 4). A caller
   holding a tracked `UserAccount` could mutate it and save it through another slice's context.
2. **`AccountSummary` has no `PasswordHash`, no lockout state, no failed-login count, and no
   timestamps.** Nothing outside this slice has a use for them, and an `ExternalInterface` that
   carries a hash makes every consumer a leak path.
3. **`InviteEmployeeAccount` requires both `EmployeeId` and `CustomerId`**, and both are
   non-nullable. This is the mechanism from §1, "Why `customer_id` is a column here": `Employees`
   knows both, `Identity` cannot look either up, and the `ck_user_accounts_scope` constraint
   rejects the row if either is missing. **Do not add an overload that omits `CustomerId`**; it
   would compile, insert nothing, and fail with a check-constraint violation at a call site that
   cannot see why.
4. **`InviteEmployeeAccount.Role` must be `CustomerAdmin` or `Employee`.** An Accountant role
   throws `InvalidOperationException` — a programming error in the calling slice, not a user error.
   Matrix §4: *"No Customer-side actor can create or modify an Accountant account."* This method is
   how that rule could be circumvented, so the guard lives here as well as in `Employees`.
5. **`SetCustomerSideRoleAsync` throws when the target is an Accountant account, and when the
   requested role is an Accountant role.** Both directions. Matrix §4 requires the request be
   *"rejected outright, not silently ignored"*. Two guards because the two mistakes are different:
   the first is `Employees` passing the wrong id, the second is `Employees` passing the wrong role.
6. **The write methods enlist in the caller's transaction** — `IRequestTransaction.EnlistAsync`, as
   `AuditApi` does. They do **not** open or commit a transaction of their own. `Employees`'
   composite onboarding operation ([03-SliceInventory.md](../../03-SliceInventory.md) §1) depends
   on this: a failure after the account is created must leave no account behind.
7. **The write methods do not check permissions.** The calling handler has already called
   `RequireAsync` with its own action name, and matrix §4 grants Employee-account operations to
   `AccountantAdmin`, `AccountantUser`, and a `CustomerAdmin` within their own Customer — a
   permission rule that belongs to `Employees`, which knows the scope. `Identity` enforces
   *structural* invariants here (rules 4 and 5), never role rules.
8. **The write methods do audit.** `AccountInvited`, `AccountSuspended`, `AccountReactivated` —
   because the account is this slice's data and the audit entry's `TargetKind` is `UserAccount`.
   The caller separately audits its own `EmployeeInvited`. Two entries for one user action is
   correct here: two things happened, in two slices.
9. **`FindManyAsync` exists so callers do not loop.** `Tickets` rendering a page of tickets needs
   the Assignee's name for each row; a per-row `FindAsync` is a query per row. Cap at 500 ids and
   throw `InvalidOperationException` above it.
10. **`ListAccountantsAsync` is not `ListAccountantsHandler`.** The handler serves an authorized
    HTTP request and returns a paginated, role-shaped DTO; this returns an unpaginated list for
    another slice's assignee picker. They must not share a return type, or the endpoint's
    field-stripping rule (§7.9 rule 1) becomes something `Tickets` can accidentally bypass.
11. **It caches nothing.** `IsActiveAsync` is what `Tickets` uses to decide an assignment is
    stranded, and status changes are exactly the event a cache would hide.
12. **`InviteEmployeeAccountAsync` queues `NotificationEvents.EmployeeInvited`, not `Invited`.**
    `InviteAccountantHandler` (§7.8) uses `Invited`. Two kinds for what looks like one operation,
    because the two audiences get different copy — an Accountant is joining the Office, an Employee
    is joining their employer's portal — and the recipient sets differ.

    > Both kinds must be in the `OutboxDrainer`'s invitation allow-list
    > ([the Notifications plan](../Notifications/IMPLEMENTATION_PLAN.md) §5.4 rule 4). An invitee is
    > not `Active` yet, so the suspended-recipient skip would otherwise swallow the email, and
    > handling only `Invited` means **Accountants get invited and Employees silently never do.**
    > That asymmetry is harder to spot than a total failure, because the feature demonstrably works
    > when the person testing it is an Accountant.

    **Confirmed 2026-09-02**, against [the Employees plan](../Employees/IMPLEMENTATION_PLAN.md) §13
    item 9: the kind is `EmployeeInvited` and the recipient is the **Employee being invited**, not
    their Customer Admin. An Admin who wants to know the invitation went out reads the Employee's
    account status; the invitation email itself is addressed to exactly one person, and copying an
    Admin on it would put a token-bearing link in a second mailbox.

13. **`SuspendAccountAsync` is idempotent: suspending an already-suspended account is a no-op, not
    an error.** This differs deliberately from `SuspendAccountantHandler`, the HTTP endpoint, which
    returns `422` for the same input.
14. **`ReactivateAccountAsync` restores `Invited`, not `Active`, when `PasswordHash is null`.**
    Suspension flattens `Invited` and `Active` into one `Suspended` status, so on the way back the
    two are indistinguishable *by status* — the password hash is the only surviving evidence of
    which one it was. Restoring an unaccepted invitee to `Active` produces an account that passes
    every status check and fails every login, because `Verify(null, …)` cannot succeed and no
    invitation flow looks at `Active` accounts. Nothing detects it: `ck_user_accounts_status`
    constrains the enum only and never ties password to status, so the row is legal and the person
    is simply locked out with no error anyone can see.

    > This is reachable through `/api/employees/reactivate-account` on its own, and now also
    > through `/api/employees/reinstate`, which reactivates automatically. Both paths hit this
    > method, which is why the fix belongs here and not in either caller.

    Note the asymmetry with `ReactivateAccountantHandler`, the HTTP endpoint, which returns `422`
    for an `Invited` account and tells the caller to re-invite (§16.2). That is right for a human
    at a screen and wrong for a cross-slice call inside somebody else's transaction: `Employees`
    reinstating an Employee must not fail because that Employee never accepted their invitation.
15. **`ChangeLoginEmailAsync` exists, and it throws for an Accountant target.** Matrix §4 grants
    changing an Employee's login email to either Accountant role and to nobody else — not to a
    Customer Admin, and not to the account's owner. Accountant accounts change through §7's own
    endpoints if they ever do, so an Accountant id here is a programming error in the calling
    slice, exactly as in rules 4 and 5.

    It normalizes through `EmailNormalization`, returns early when the address is ordinally
    unchanged, and raises `409` when another account already holds the normalized form — the same
    system-wide uniqueness `uq_user_accounts_login_email` enforces, checked first so the caller
    gets a message instead of a `23505`.

    **It writes `LoginEmail` and `NormalizedLoginEmail` and nothing else.** Not the password, not
    `EmailConfirmedAt`, not the status, and no session invalidation — there is no session
    revocation in this slice (constraint 1), so the person's live cookie keeps working under the
    new address until it expires. It audits `LoginEmailChanged` against `UserAccount`; the calling
    handler audits its own entry against `Employee`, as in rule 8.

    > The reason is that the two callers ask different questions. A human clicking *Suspend* on an
    > already-suspended account has made a mistake and should be told. `Employees`' departure
    > handler is asserting an end state — [01-DomainModel.md](../../01-DomainModel.md) §9.6 rule 2
    > makes departure *suspend the account* — and a departing Employee whose account was already
    > suspended for an unrelated reason is an ordinary case, not an error. Making the contract
    > throw there means a departure that cannot be recorded.
    >
    > So: the **handler** validates current state, the **contract** asserts target state. Do not
    > implement the contract by calling the handler, and do not "unify" them.

### 9.2 `IRecipientDirectory` — the inverted dependency this slice implements

**File:** `Slices/Identity/ExternalInterfaces/RecipientDirectory.cs`

`Notifications` defines `IRecipientDirectory` and **`Identity` implements it**
([03-SliceInventory.md](../../03-SliceInventory.md) §3 rule 7). This is the one inverted dependency
in v1. The reference direction stays `Identity → Notifications`, exactly as the dependency table
permits.

```csharp
public sealed class RecipientDirectory : IRecipientDirectory
{
    public async Task<Recipient?> FindAsync(string userAccountId, CancellationToken ct)
    {
        if (!Guid.TryParse(userAccountId, out var id))
            return null;

        return await _db.UserAccounts
            .Where(u => u.Id == id)
            .Select(u => new Recipient(
                u.Id.ToString(), u.LoginEmail, u.DisplayName,
                u.Status == AccountStatus.Active))
            .FirstOrDefaultAsync(ct);
    }
}
```

Rules:

1. **Register it in `IdentityRegistration.cs`**, and **delete `RecipientDirectoryStub`** from
   `NotificationsRegistration.cs` in the same commit. The stub is registered with `TryAddScoped`
   so the real one wins regardless of `Program.cs` ordering.

   > The stub is called `RecipientDirectoryStub`, not `NullRecipientDirectory`, and it **throws
   > `InvalidOperationException`** rather than returning `null` — deliberately. Returning `null` was
   > tried and was worse than useless: the drainer treats an unresolvable recipient as permanently
   > undeliverable, so it marked every entry `Skipped` and nulled `email_body`, destroying every
   > invitation and reset link with no error anywhere and the notification row still claiming the
   > mail had been handled. Read the comment on the class before deleting it; it is the reason the
   > `Invited` trap in rule 3 below is survivable at all.
   >
   > **Also delete the paired startup guard in `Program.cs`** — the block that throws when
   > `Notifications:Email:Enabled` is true while the resolved `IRecipientDirectory` is still
   > `RecipientDirectoryStub`. Once this slice registers the real one that check can never fire, and
   > it is what is currently forcing email to stay off. It is on the §15 checklist.
2. **An unparseable id returns `null`, not an exception.** The drainer treats `null` as `Skipped`
   with `"No such account"` — the right outcome. Throwing would break the drain loop for every
   subsequent row in the batch.
3. **`IsActive` is `Status == Active`**, so `Invited` and `Suspended` both come back inactive and
   the drainer skips them. That is correct for `Suspended`. **For `Invited` it is not** — see rule
   4, and this is the trap.

   > **The invitation email must reach an `Invited` account.** That is the entire point of an
   > invitation, and it is the one case where mailing an inactive account is required. If the
   > drainer skips inactive recipients unconditionally, **no invitation is ever delivered and
   > nobody but the seeded Admin can ever log in** — with no error, just a `Skipped` row.
   >
   > Resolve it in the drainer, not here: skip an inactive recipient **unless the notification's
   > event kind is `Invited`**. Add this as a rule to
   > [the Notifications plan](../Notifications/IMPLEMENTATION_PLAN.md) §5.4 rule 4 when you build
   > it, and add a test for it. It is flagged in §18.

4. **This class does **not** run inside a request.** The drainer calls it from a background scope
   with its own connection (Notifications §5.4 rule 2), so it must not depend on
   `RequestConnection`, `IHttpContextAccessor`, `CurrentUser`, or `IRequestTransaction`. Its
   `IdentityDbContext` comes from the drainer's scope. **This is the one place in this slice where
   the request-scoped connection is unavailable**, and injecting anything request-scoped here
   produces a `NullReferenceException` in a background loop at 3 a.m.
5. **It returns `Recipient`, a `Notifications` type**, and that is correct: the defining slice owns
   the contract type (rule 7's "the contract returns the defining slice's own small types").
6. **It reads, never writes, and never audits.** Resolving an address is not an audited action.

### 9.3 What no `ExternalInterface` in this slice may expose

- The password hash, or any part of it
- `FailedLoginCount`, `LockoutExpiresAt`, or whether an account is currently locked out
- Any token, raw or hashed
- `MustChangePassword`
- A method that authenticates. There is no `IIdentityApi.VerifyPasswordAsync`, and no other slice
  may ever have a reason for one. If one appears, something is being built in the wrong slice.

---

## 10. Cross-slice boundaries

`Identity` may depend on **`Customers`, `Notifications`, `Audit`** — and nothing else
([03-SliceInventory.md](../../03-SliceInventory.md) §2).

| It calls | For | Not for |
|---|---|---|
| `ICustomerApi.IsActiveAsync` | The login-time Customer status check, §7.1 step 5 | Anything else. It never reads the `customers` table and never caches the answer. |
| `INotificationApi.NotifyAsync` | Invitations, password resets, account suspension | Anything an Accountant does to another Accountant's role — a promotion is not news that needs an email. |
| `IAuditApi` | Every outcome in §7.0 F | — |

It is called by **`Employees`** and **`Tickets`**, through `IIdentityApi`, and by
**`Notifications`** through the inverted `IRecipientDirectory`.

Four boundary rules:

1. **`Identity` never references `Employees` or `Tickets`.** Both depend on it. If a handler here
   needs an Employee's Customer, the answer is §1, "Why `customer_id` is a column here" — the value
   was passed in at creation time, not looked up.
2. **`Identity` never names `CustomerStatus`** or any other type from another slice's `Core`
   (dependency rule 2). It calls `IsActiveAsync` and gets a `bool`.
   `App/GeneralAppArchitecture.md` §5 has a worked example of exactly this mistake.
3. **`Identity` does not create Employee records.** The composite Customer-onboarding operation
   lives in `Employees` ([03-SliceInventory.md](../../03-SliceInventory.md) §1) and calls into
   here, not the other way round.
4. **A second inverted interface is not permitted without raising it** (§3 rule 7). This slice
   already implements the one that exists. If you find yourself wanting `Identity` to define an
   interface for `Employees` to implement, stop and flag it — §1's `customer_id` decision exists
   precisely so that you do not need to.

---

## 11. Endpoints

`IdentityEndpoints.cs` at the slice root. Two route groups, both owned by this slice.

### 11.1 `/api/auth/*` — self-service

**This prefix is not a stylistic choice.** `04-Infrastructure.md` §3: Caddy rate-limits
`/api/auth/*` at 10 events per minute per remote host, and the README calls that *mandatory, not
hardening*. **An unauthenticated credential-accepting endpoint placed anywhere else is
unthrottled.** That is the whole reason login, password reset, and invitation acceptance live here.

| Method | Route | Handler | Auth |
|---|---|---|---|
| `POST` | `/api/auth/login` | `LoginHandler` | none |
| `POST` | `/api/auth/logout` | `LogoutHandler` | cookie |
| `GET` | `/api/auth/me` | `GetCurrentSessionHandler` | cookie |
| `POST` | `/api/auth/change-password` | `ChangeOwnPasswordHandler` | cookie |
| `POST` | `/api/auth/request-password-reset` | `RequestPasswordResetHandler` | none |
| `POST` | `/api/auth/reset-password` | `CompletePasswordResetHandler` | none |
| `POST` | `/api/auth/accept-invitation` | `AcceptInvitationHandler` | none |

### 11.2 `/api/accountants/*` — administration

| Method | Route | Handler | Roles |
|---|---|---|---|
| `POST` | `/api/accountants/list` | `ListAccountantsHandler` | AA, AU |
| `POST` | `/api/accountants/invite` | `InviteAccountantHandler` | AA |
| `POST` | `/api/accountants/suspend` | `SuspendAccountantHandler` | AA |
| `POST` | `/api/accountants/reactivate` | `ReactivateAccountantHandler` | AA |
| `POST` | `/api/accountants/promote` | `PromoteAccountantHandler` | AA |
| `POST` | `/api/accountants/demote` | `DemoteAccountantHandler` | AA |

Rules:

1. **Multi-word segments are kebab-case** — `change-password`, `request-password-reset`,
   `reset-password`, `accept-invitation`. Not `changepassword`, and not `resetPassword`.
   `App/GeneralAppArchitecture.md` §8 makes this a LOCKED rule, and the stated reason applies
   directly here: a doubled letter across a word boundary is easy to typo and the mistake is
   invisible in review.
2. **No route parameters, anywhere.** Not `/api/accountants/{id}/suspend`. Ids go in the body via
   `AccountIdRequestDto`. `App/GeneralAppArchitecture.md` §8.
3. **`/api/auth/me` is the only `GET`** in this slice, and it is a `GET` because it is a pure read
   with no body. Everything else is a `POST`, including `list` — a request DTO in the body, not a
   query string.
4. **There is no `DELETE` endpoint in this slice.** Matrix §2: *"Delete an Accountant account —
   **Nobody.** Suspension only."*
5. **There is no endpoint that creates a Customer-side account.** `Employees` owns those; they
   arrive through `IIdentityApi`.
6. **There is no endpoint that resets another person's password.** Matrix §11:
   *"Reset another person's password directly — **Nobody.** Re-issue an invitation or trigger a
   reset email instead."* An Admin who wants to help a locked-out colleague re-invites them or
   tells them to use the reset flow.
7. **`.Produces<T>(200)` and `.ProducesProblem(...)` on every route**, so the generated OpenAPI
   document is usable by the SPA.
8. **`/api/auth/login` must not be marked `[AllowAnonymous]` by accident on the whole group.**
   Apply anonymous access per-endpoint, not to the group, or `logout`, `me`, and
   `change-password` become unauthenticated too — and `me` returning an anonymous `200` instead of
   a `401` is a bug the SPA will paper over.

---

## 12. Service registration

### 12.1 `Slices/Identity/IdentityRegistration.cs`

```csharp
public static IServiceCollection AddIdentitySlice(
    this IServiceCollection services, IConfiguration configuration)
{
    // The SHARED request connection overload. See rule 1.
    services.AddDbContext<IdentityDbContext>((serviceProvider, options) =>
        options.UseNpgsql(serviceProvider.GetRequiredService<RequestConnection>().Connection));

    services.AddSingleton<IActionCatalogue, IdentityActionCatalogue>();
    services.AddSingleton<IPasswordHashing, PasswordHashing>();
    services.AddSingleton<ITokenIssuing, TokenIssuing>();

    services.AddTransient<LoginHandler>();
    // … the other thirteen handlers …

    services.AddScoped<IIdentityApi, IdentityApi>();
    services.AddScoped<IRecipientDirectory, RecipientDirectory>();   // §9.2

    // Cookie authentication and data protection — §4
    services.AddAuthentication(/* … */).AddCookie(/* … */);
    services.AddDataProtection(/* … */);

    return services;
}
```

### 12.2 What `Program.cs` adds

Exactly two lines for the slice, plus three that belong to this slice's arrival:

```csharp
builder.Services.AddIdentitySlice(builder.Configuration);   // 1
// …
app.UseAuthentication();                                    // 2 — now unconditional
app.UseMiddleware<MustChangePasswordMiddleware>();          // 3 — AFTER authentication
app.MapIdentityEndpoints();                                 // 4
```

**There is no `app.UseAuthorization()`, and that is deliberate.** It is absent from `Program.cs`
today and it stays absent. Authorization in this codebase is not endpoint metadata — it is
`IPermissionChecker.RequireAsync` called as the first statement of a handler (§0.3), and
authentication is enforced by `CurrentUser`'s factory throwing `401` when there is no principal
(§0.2). An endpoint that takes `CurrentUser` is therefore authenticated by construction, and one
that does not — the four unauthenticated handlers — is anonymous by construction.

> **The consequence for §11 rule 8: do not use `.RequireAuthorization()` or `[AllowAnonymous]` at
> all.** Without the authorization middleware in the pipeline they are inert, so
> `.RequireAuthorization()` would be a guard that silently does nothing — worse than no guard,
> because it reads like protection. Rule 8's concern about accidentally marking the whole group
> anonymous cannot arise if nothing is marked either way. If you ever do add `UseAuthorization()`,
> it goes between `UseAuthentication()` and the §5 middleware, and every route in every slice needs
> auditing at that point — which is a change to raise, not to make here.

And **removes** the `devAuthEnabled` variable, the conditional `AddAuthentication("DevAuth")`
block, the startup warning, and the conditional `app.UseAuthentication()` — see §15.

### 12.3 Registration traps

1. **`AddDbContext` must use the `(serviceProvider, options)` overload and `RequestConnection`.**
   The plain `options => options.UseNpgsql(connectionString)` overload compiles, works in every
   single-slice test, and silently gives this slice its **own** connection — at which point the
   audit entry written by a login is in a different transaction from the login itself, and the
   `App/GeneralAppArchitecture.md` §5 guarantee that they commit together is gone. Nothing fails.
2. **Never `AddScoped<IdentityDbContext>()`.** It bypasses the options pipeline and the context
   gets no provider at all.
3. **`IPasswordHashing` and `ITokenIssuing` are singletons; everything touching the DbContext is
   scoped or transient.** A singleton that captured `IdentityDbContext` would hold one context for
   the process lifetime — the same bug as Notifications §5.4 rule 1, and it would break every login
   after the first connection died.
4. **`IRecipientDirectory` is `AddScoped`, not `AddSingleton`.** The drainer resolves it inside a
   per-iteration scope; a singleton would capture a context.
5. **Register the action catalogue as `IActionCatalogue`, not as the concrete type.**
   `PermissionChecker` takes `IEnumerable<IActionCatalogue>`; a concrete registration is simply
   never seen, every action in it is absent, and every one of this slice's authorized endpoints
   returns `403`. The composition also fails at startup on a duplicate action name and on an empty
   role array — both are what you want.
6. **`PermissionChecker` has exactly one constructor**,
   `(IEnumerable<IActionCatalogue>, IAuditApi, ILogger<PermissionChecker>)`. Earlier drafts of this
   plan warned about a legacy single-argument constructor carrying an inline
   `LegacyTicketTypesCatalogue` — **both are already gone from the tree; do not go looking for
   them.** What still applies: the composition validates at startup and fails on a duplicate action
   name across two slices and on an empty role array. Both are the designed behaviour, and a
   duplicate-name failure naming two slices is a confusing first symptom rather than a bug.

### 12.4 `IdentityActionCatalogue.cs`

```csharp
public sealed class IdentityActionCatalogue : IActionCatalogue
{
    public string SliceName => "Identity";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } = new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
    {
        ["ListAccountants"]      = [UserRole.AccountantAdmin, UserRole.AccountantUser],
        ["InviteAccountant"]     = [UserRole.AccountantAdmin],
        ["SuspendAccountant"]    = [UserRole.AccountantAdmin],
        ["ReactivateAccountant"] = [UserRole.AccountantAdmin],
        ["PromoteAccountant"]    = [UserRole.AccountantAdmin],
        ["DemoteAccountant"]     = [UserRole.AccountantAdmin],
    };
}
```

**Six actions, and no more.** There is deliberately no action for login, logout, `me`, or
change-password: those are available to every authenticated caller or to none, and a catalogue
entry listing all four roles would imply a role decision where there is not one. `ListAccountants`
is the only entry with two roles, and the *field-level* difference between them lives in the
handler (§7.9 rule 1) because the catalogue can express "who may call", not "what they see".

### 12.5 Startup smoke check — before writing any test

```bash
# 1. Seeded Admin can log in, and is told to change the password.
curl -sc jar.txt -X POST localhost:5000/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@example.com","password":"ChangeMe123!"}'
#    expect 200 and "mustChangePassword": true

# 2. Everything else is blocked until they do.
curl -sb jar.txt -X POST localhost:5000/api/accountants/list -d '{}'
#    expect 403 with "code": "password_change_required"     ← §5

# 3. Change it, then the same call works.
curl -sb jar.txt -c jar.txt -X POST localhost:5000/api/auth/change-password \
  -H 'Content-Type: application/json' \
  -d '{"currentPassword":"ChangeMe123!","newPassword":"a-much-longer-passphrase"}'
curl -sb jar.txt -X POST localhost:5000/api/accountants/list -d '{}'
#    expect 200

# 4. Six wrong passwords lock the account.
#    expect 401 each time, and still 401 with the CORRECT password on the sixth.
```

Step 4 is the one to actually run, because it is the one that fails when rule D in §7.0 was
overlooked — and it fails in a way that no unit test constructing the handler directly will catch.

---

## 13. Migrations — SQL scripts, not `dotnet ef`

**File:** `Slices/Identity/Infrastructure/Migrations/20260901_001_CreateIdentitySchema.sql`

- `YYYYMMDD_###_Description.sql`. The sequence number restarts at `001` **per slice**, which is
  why the runner tracks the **slice-relative path with forward slashes**, never
  `Path.GetFileName` (`App/GeneralAppArchitecture.md` §6 — LOCKED). This slice's `..._001_...` and
  `Audit`'s `..._001_...` are different rows in `schema_versions`.
- **Never `dotnet ef migrations add`.** EF migrations are not used in this project. If a
  `Migrations/` folder with C# files appears, delete it.
- One script for this slice, containing both tables, every index, and both `CONSTRAINT`s. Ordering
  within the file: `user_accounts` before `user_account_tokens`.
- **No rollback script.** Migrations are append-only; a mistake is fixed by a new script.
- Set the file's build action so it is copied to the output directory, or the runner finds nothing
  at startup and every query fails with `42P01: relation "user_accounts" does not exist`.

---

## 14. Seeding the first `AccountantAdmin`

`App/GeneralAppArchitecture.md` §9 is normative and the decision is made: **`IConfiguration`
binding of the `Seeding` section**, from environment variables in production
(`ACCOUNTANT_ADMIN_EMAIL` / `ACCOUNTANT_ADMIN_PASSWORD`) and `appsettings.json` locally.

**`Shared/Seeding/DatabaseSeeder.cs` does not exist.** Neither does the `Shared/Seeding/` directory,
and there is no `Seeding` section in either `appsettings` file — an earlier draft of this plan said
the seeder "already exists in outline" and that was wrong. **You are writing it from scratch**, along
with its `Program.cs` wiring, and that is a component in its own right rather than the edit the files
checklist used to imply. What it must do:

```
if no row in user_accounts has role = 'AccountantAdmin':
    read Seeding:FirstAdminEmail and Seeding:FirstAdminPassword
    if either is missing or blank → THROW, failing startup with a clear message
    validate the password against PasswordPolicy (§3.3)
    insert:
        role                     = AccountantAdmin
        status                   = Active           ← not Invited
        password_hash            = hash of the configured password
        must_change_password     = TRUE             ← the reason §5 exists
        email_confirmed_at       = NULL
        employee_id, customer_id = NULL
    audit AccountantAccountCreated via LogUnauthenticatedAsync(actor = "seed")
```

Rules:

1. **The condition is "no `AccountantAdmin` exists", not "the table is empty".** A database with
   Employee accounts and no Admin is exactly the unrecoverable state §8.1 guards against, and
   seeding is the only way out of it.
2. **`Status = Active`, not `Invited`.** There is nobody to send an invitation to yet, and no
   mail transport is necessarily configured on a first run.
3. **`MustChangePassword = TRUE`.** The seeded password came from an environment variable that is
   visible in `docker inspect`, in shell history, and in the compose file. `App/GeneralAppArchitecture.md`
   §9: *"The app must force a password change before they can do anything else."*
4. **Fail startup when the configuration is absent.** Explicitly: *"do not fall back to a built-in
   default password."* A default admin password is the single most exploited misconfiguration in
   self-hosted software.
5. **No interactive prompt and no sentinel file.** There is no terminal in the container.
6. **Idempotent.** Restarting must not create a second Admin, and must not reset the first one's
   password back to the configured value — which would silently undo step 3 on every deploy.
7. **The seeder runs after migrations**, inside the scope created in `Program.cs`
   (`App/GeneralAppArchitecture.md` §9). It needs its own transaction; there is no request.
8. **Validate the configured password against the policy**, and fail startup if it is too short.
   A seeded password that the policy would reject can never be *changed* to something acceptable
   through a flow that also rejects it — and the failure appears only at first login.

---

## 15. Deleting `DevAuth` — the checklist

`App/GeneralAppArchitecture.md` §9 rule 2: *"It is **deleted, not disabled**, once `Identity`
ships. Delete `DevAuthHandler.cs`, the registration block, and the `DevAuth` config key in the
same commit that adds real cookie login. **Leaving dead bypass code in the tree is how it comes
back.**"*

This is part of the slice. Tick every line:

- [ ] Delete `AccountantApp.Api/Shared/Auth/DevAuthHandler.cs`
- [ ] Delete the `var devAuthEnabled = …` variable in `Program.cs`
- [ ] Delete the `if (devAuthEnabled) { builder.Services.AddAuthentication("DevAuth")… }` block
- [ ] Delete the `if (devAuthEnabled) { app.Logger.LogWarning(…) }` block
- [ ] Replace the `if (devAuthEnabled) { app.UseAuthentication(); }` block with an
      **unconditional** `app.UseAuthentication();`
- [ ] Delete the `DevAuth` section from `appsettings.Development.json`
- [ ] Delete the `Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions` and
      `DevAuthHandler` `using` directives left behind in `Program.cs`
- [ ] Delete `RecipientDirectoryStub.cs`, its `TryAddScoped` line in `NotificationsRegistration.cs`,
      **and** the `Notifications:Email:Enabled` + `is RecipientDirectoryStub` startup guard in
      `Program.cs` (§9.2 rule 1)
- [ ] Search the whole solution for `X-Dev-Role`, `X-Dev-User-Id`, `X-Dev-Customer-Id`, and
      `DevAuth` — including test helpers, `.http` files, and shell scripts. **Every hit must go.**

> **This is smaller than it looks. Do not go hunting for work that is not there.**
>
> An earlier draft of this plan claimed that rewriting tests to log in over HTTP was "the largest
> single piece of work in the slice" and that `Audit`, `TicketTypes`, `Notifications`, and
> `Customers` "all have tests that use the bypass." **That was wrong.** There are **zero** hits for
> `X-Dev-*` or `DevAuth` anywhere in `AccountantApp.Tests`. Every existing test constructs
> `new CurrentUser(id, role, customerId)` directly and calls the handler in-process; none of them go
> through the HTTP pipeline, so none of them authenticate at all.
>
> `DevAuth` lives in exactly **three** files: `Shared/Auth/DevAuthHandler.cs`, `Program.cs`, and
> `appsettings.Development.json`. Deleting it does not break a single existing test.
>
> **So do not convert the existing in-process tests to cookie logins.** They are testing handlers,
> which is the right altitude for them, and rewriting them would be days of churn that removes
> coverage rather than adding it. The HTTP-pipeline-with-a-cookie-jar coverage this slice needs is
> **new** — it is the §16.1 real-PostgreSQL test and the §12.5 smoke sequence, and it is additive.

> **Do not keep DevAuth "just for tests".** A test-only authentication bypass is compiled into the
> shipped assembly and is gated by configuration that a deployment can set. The whole point of the
> two-guard design was that it was temporary.

---

## 16. Tests

### 16.1 At least one test must run against real PostgreSQL — mandatory

`Microsoft.EntityFrameworkCore.InMemory` is banned from the API project and permitted only in the
test project ([03-SliceInventory.md](../../03-SliceInventory.md) §5). In-memory cannot see any of
this slice's most important behaviour:

- The `uq_user_accounts_normalized_email` unique constraint — so the `409` path in §7.8 rule 3 is
  untestable
- The `ck_user_accounts_scope` check constraint
- The **partial** and **unique-partial** indexes in §1
- `PostgresException` `SqlState == "23505"`, which the duplicate-email handling catches by value

So: a real-PostgreSQL test that covers, at minimum, inviting a duplicate email (`409`), inserting
a `CustomerAdmin` with a null `customer_id` (constraint violation), and one full
login → change-password → authorized-call sequence through the HTTP pipeline with a cookie jar.

> Docker is currently not starting on this machine, so no PostgreSQL exists and **no part of this
> schema has ever been applied**. Every SQL statement in §1 and §13 is unverified. When Docker
> works, run the migration first and fix the script before trusting any of this plan's DDL.

### 16.2 Behavioural cases

| Case | Expected |
|---|---|
| Correct credentials, `Active` account, `Active` Customer | `200`, cookie set, `LastLoginAt` advanced |
| Correct credentials, `Active` account, **`Suspended` Customer** | `401`, identical message |
| Correct credentials, `Suspended` account | `401`, identical message |
| Correct credentials, `Invited` account (null hash) | `401`, identical message, **no `500`** |
| No such email | `401`, identical message |
| Wrong password | `401`; `FailedLoginCount` is **1 in the database afterwards** |
| Five wrong passwords | `LockoutExpiresAt` set, `FailedLoginCount` back to 0, `AccountLockedOut` audited |
| Sixth attempt with the **correct** password while locked out | `401`, and `FailedLoginCount` still 0 |
| Login after the lockout expires, correct password | `200`, lockout cleared |
| An Accountant logging in | `IsActiveAsync` is **never called** |
| Timing: login for a nonexistent email vs. a real one | comparable durations (§7.1 rule 1) |
| `CustomerAdmin` login | the cookie carries a `customer_id` claim; a follow-up request succeeds |
| Accountant login | the cookie carries **no** `customer_id` claim; a follow-up request succeeds |
| Seeded Admin's first login | `mustChangePassword: true` |
| Seeded Admin calls any other endpoint before changing it | `403`, `code = "password_change_required"` |
| Seeded Admin calls `/api/auth/me`, `/logout`, `/change-password` | `200` — the three exemptions |
| After changing the password | the same endpoint returns `200`; the new cookie's claim is `false` |
| `change-password` with the wrong current password | `401`, and `FailedLoginCount` incremented **in the database** |
| `change-password` with a new password of 11 characters | `422` naming the length rule |
| `change-password` with the new password equal to the current one | `422` |
| `change-password` succeeds | outstanding `PasswordReset` tokens are consumed |
| `request-password-reset` for an unknown email | `200`, empty body, no token row, **audited** |
| `request-password-reset` for a `Suspended` account | `200`, empty body, **no token row and no email** |
| `request-password-reset` twice | the first token is `consumed_at`-stamped; only the second works |
| The reset notification | `notifications.body` has **no token**; `notification_outbox.email_body` has one |
| `reset-password` with a valid token | `200`; hash changed; token consumed; lockout cleared; **no cookie issued** |
| `reset-password` with the same token twice | `400`, identical message |
| `reset-password` with an expired token | `400`, identical message |
| `reset-password` with an **invitation** token | `400` — cross-purpose redemption refused |
| `accept-invitation` with a valid token | `Status = Active`, `EmailConfirmedAt` set, `MustChangePassword = false` |
| `accept-invitation` twice | `400` |
| `invite` a duplicate email | `409`, not `500` |
| `invite` with `Role = CustomerAdmin` | `422` |
| `invite` response | contains **no token** |
| `invite` where the notification write fails | **no account row committed** (one transaction) |
| `AccountantUser` calls `/api/accountants/list` | `200`, and **no `loginEmail` key in the JSON** |
| `AccountantAdmin` calls the same | `200` with `loginEmail` and `status` present |
| `CustomerAdmin` calls it | `403`, **not** an empty page |
| The list | contains no Employee or CustomerAdmin accounts |
| `suspend` the only `Active` `AccountantAdmin` | `422`, and the row is **unchanged after rollback** |
| `demote` the only `Active` `AccountantAdmin` | `422` |
| `suspend` yourself | `422` |
| `demote` yourself | `422` |
| `suspend` an already-`Suspended` account | `422`, not `200` |
| `suspend` a `CustomerAdmin` account through `/api/accountants/suspend` | `404`, not `403` |
| `reactivate` an `Invited` account | `422` telling the caller to re-invite |
| `promote` an `Invited` `AccountantUser` | `200` — allowed |
| `IIdentityApi.InviteEmployeeAccountAsync` with an Accountant role | `InvalidOperationException` |
| `IIdentityApi.SetCustomerSideRoleAsync` targeting an Accountant | throws |
| `IIdentityApi.SetCustomerSideRoleAsync` with an Accountant role | throws |
| `IIdentityApi.ReactivateAccountAsync` on a suspended account **with** a password hash | `Active` |
| `IIdentityApi.ReactivateAccountAsync` on a suspended account with **no** hash | `Invited`, **not** `Active` (rule 14) |
| `IIdentityApi.ChangeLoginEmailAsync` to a free address | `LoginEmail` and `NormalizedLoginEmail` both updated; hash, status and `EmailConfirmedAt` unchanged |
| `IIdentityApi.ChangeLoginEmailAsync` to an address another account holds | `AppException` `409`, not `DbUpdateException` |
| `IIdentityApi.ChangeLoginEmailAsync` to the same address | no-op, no audit entry |
| `IIdentityApi.ChangeLoginEmailAsync` targeting an Accountant | throws |
| Logging in after `ChangeLoginEmailAsync` | the **new** address works, the old one `401`s |
| `IIdentityApi.FindManyAsync` with 501 ids | `InvalidOperationException` |
| `AccountSummary` | has no `PasswordHash`, lockout, or token property — assert by reflection |
| `RecipientDirectory.FindAsync("not-a-guid")` | `null`, no exception |
| Every denial in this slice | writes an Audit entry |
| Solution-wide search for `X-Dev-Role` and `DevAuth` | **zero hits** (§15) |

### 16.3 The test that is easy to write wrongly

The lockout tests must assert against the **database**, not the response. Every one of the six
attempts returns `401` whether or not the counter was persisted, so a test that checks only status
codes passes against the broken implementation described in §7.0 rule D. Read
`failed_login_count` and `lockout_expires_at` back from PostgreSQL, in a **new** context or scope,
after the request has completed.

---

## 17. Known constraints

1. **A cookie outlives a status change, for up to 8 hours.** Suspending an account, demoting an
   Admin, or suspending a Customer stops the *next* login and every check that reads status — but
   an already-issued cookie stays cryptographically valid. There is no session revocation in v1
   (§4.4). The demotion direction fails **unsafe** (§7.13 rule 3). Do not fix this with a
   per-request database read in `IPermissionChecker`; if it must be fixed, that is a design
   decision to raise.
2. **Suspending a Customer does not invalidate its people's live sessions**, for the same reason.
   Matrix §11's "immediately" is satisfied for *login*, which is what it specifies.
3. **A locked-out user is not told they are locked out** (§7.1 rule 3). Accepted, in exchange for
   not having an enumeration oracle. Support reads the audit log.
4. **The at-least-one-Admin guard has a concurrency window** under `READ COMMITTED` (§8.1 rule 5).
   Two Admins acting simultaneously can reach zero. There is no in-app recovery from zero Active
   Admins other than re-seeding.
5. **`Identity` cannot resolve an Employee's Customer** and depends on `Employees` passing it in
   (§1). A bug in `Employees` that passes the wrong `CustomerId` gives that account the wrong
   tenant scope, and nothing in this slice can detect it. The `Employees` plan must carry a test.
6. **No email is delivered until a transport is chosen.** `04-Infrastructure.md` §5a: the
   registered `IEmailSender` is a logging stub. So on a real deployment today, **invitations and
   password resets do not arrive** — the seeded Admin is the only person who can log in, and the
   only way to onboard anyone is to read the link out of the application log. This is the highest
   priority external decision blocking the slice.
7. **`AccountantAdmin` is the ceiling** (matrix §12 rule 6). There is no break-glass account and no
   support bypass. That is deliberate, and it is why constraints 4 and 6 matter.
8. **No password history and no expiry.** A user may change their password back to a previous one,
   and passwords never expire. Both follow NIST SP 800-63B; neither is an oversight.
9. **No multi-factor authentication.** Out of scope for v1 (`04-Infrastructure.md` §8). The schema
   does not anticipate it; adding it later means a migration.
10. **Two accounts for one person at two Customers** (`01-DomainModel.md` §2). Two Employee records
    means two login emails, and the same address cannot be used for both because
    `normalized_login_email` is unique system-wide. Accepted; it keeps Customer isolation absolute.

---

## 18. Questions to flag rather than answer

Do not resolve these by guessing. Each one changes behaviour visible to a user.

1. **RESOLVED — the `Invited`-recipient trap in the drainer.** `IRecipientDirectory` reports an
   `Invited` account as inactive, and the drainer skipped inactive recipients — so **no invitation
   email would ever have been sent, silently.** [The Notifications
   plan](../Notifications/IMPLEMENTATION_PLAN.md) §5.4 rule 4 has been amended with the condition
   and the two required tests, and it covers **both** `Invited` and `EmployeeInvited` (§9.1 rule
   12). Nothing is open here; verify the amended rule is implemented when `Notifications` is
   built. It was the highest-severity cross-plan issue in the set.
2. **Which email transport?** `04-Infrastructure.md` §5a: "Twilio" is ambiguous — Twilio's own API
   is SMS, its email product is SendGrid, and they need different libraries and secrets. Until this
   is answered, constraint 6 stands and nobody can be onboarded on a real deployment.
3. **What is `App:BaseUrl` in each environment?** §7.5 rule 6 forbids deriving it from the `Host`
   header. It must be configured, and a wrong value produces invitation links that 404 — or worse,
   links to somebody else's host.
4. **Should `/api/accountants/*` be rate-limited too?** Only `/api/auth/*` is
   (`04-Infrastructure.md` §3). These endpoints require an authenticated `AccountantAdmin`, so the
   exposure is low — but `invite` sends email, and an authenticated Admin could be used to send a
   lot of it. Ask before adding a second Caddy zone.
5. **DECIDED — departure suspends the account.** [01-DomainModel.md](../../01-DomainModel.md) §9.6
   rule 2 answers this: marking an Employee `Departed` suspends their `UserAccount`. `Employees`
   implements it, calling `IIdentityApi.SuspendAccountAsync` inside its own transaction — which is
   why that method is idempotent while the HTTP endpoint returns `422` (§9.1 rule 13). Nothing is
   open; this slice's only obligation is the no-op semantics.
6. **Is a display-name change a self-service operation?** Matrix §11 lists password operations
   only, and §4 gives an Employee "own contact details only" on their *Employee* record — which is
   a different entity from the account carrying `display_name`. There is currently **no way to
   change a `display_name` after invitation acceptance.** That is probably a gap; confirm before
   adding an endpoint the matrix does not authorize.

   > The neighbouring question — how a **login email** gets changed — was answered on 2026-09-02:
   > matrix §4 now carries a "Change an Employee's login email" row granted to Accountants only,
   > `Employees` owns `POST /api/employees/change-login-email`, and this slice does the write
   > through `ChangeLoginEmailAsync` (§9.1 rule 15). That answer does **not** extend to
   > `display_name`, and it is not the precedent for making one self-service: the login email was
   > deliberately kept *away* from the account's owner, so "an Accountant can change X" says
   > nothing about whether the owner can change Y.
7. **How does an Admin recover a colleague locked out by lockout rather than by a forgotten
   password?** Matrix §11 forbids resetting another person's password directly. Today the answer is
   "wait 15 minutes", which is fine — but there is no *clear-lockout* operation, and an Admin will
   ask for one. It would be a new row in matrix §2 and therefore a normative change.

---

## Files checklist

| File | Action |
|---|---|
| `Slices/Identity/Infrastructure/Migrations/20260901_001_CreateIdentitySchema.sql` | New |
| `Slices/Identity/Core/UserAccount.cs` | New (incl. `AccountStatus`) |
| `Slices/Identity/Core/UserAccountToken.cs` | New (incl. `TokenPurpose`) |
| `Slices/Identity/Infrastructure/IdentityDbContext.cs` | New |
| `Slices/Identity/Infrastructure/Configurations/UserAccountConfiguration.cs` | New |
| `Slices/Identity/Infrastructure/Configurations/UserAccountTokenConfiguration.cs` | New |
| `Slices/Identity/Application/PasswordHashing.cs` | New (incl. `IPasswordHashing`) |
| `Slices/Identity/Application/TokenIssuing.cs` | New (incl. `ITokenIssuing`) |
| `Slices/Identity/Application/PasswordPolicy.cs` | New |
| `Slices/Identity/Application/AccountInvariants.cs` | New |
| `Slices/Identity/Application/Dtos/` — twelve DTOs per §6 | New |
| `Slices/Identity/Application/Handlers/LoginHandler.cs` | New |
| `Slices/Identity/Application/Handlers/LogoutHandler.cs` | New |
| `Slices/Identity/Application/Handlers/GetCurrentSessionHandler.cs` | New |
| `Slices/Identity/Application/Handlers/ChangeOwnPasswordHandler.cs` | New |
| `Slices/Identity/Application/Handlers/RequestPasswordResetHandler.cs` | New |
| `Slices/Identity/Application/Handlers/CompletePasswordResetHandler.cs` | New |
| `Slices/Identity/Application/Handlers/AcceptInvitationHandler.cs` | New |
| `Slices/Identity/Application/Handlers/InviteAccountantHandler.cs` | New |
| `Slices/Identity/Application/Handlers/ListAccountantsHandler.cs` | New |
| `Slices/Identity/Application/Handlers/SuspendAccountantHandler.cs` | New |
| `Slices/Identity/Application/Handlers/ReactivateAccountantHandler.cs` | New |
| `Slices/Identity/Application/Handlers/PromoteAccountantHandler.cs` | New |
| `Slices/Identity/Application/Handlers/DemoteAccountantHandler.cs` | New |
| `Slices/Identity/ExternalInterfaces/IIdentityApi.cs` | New (incl. `AccountSummary`, `InviteEmployeeAccount`) |
| `Slices/Identity/ExternalInterfaces/IdentityApi.cs` | New |
| `Slices/Identity/ExternalInterfaces/RecipientDirectory.cs` | New |
| `Slices/Identity/IdentityActionCatalogue.cs` | New |
| `Slices/Identity/IdentityRegistration.cs` | New |
| `Slices/Identity/IdentityEndpoints.cs` | New |
| `Shared/Auth/MustChangePasswordMiddleware.cs` | New |
| `Shared/Seeding/DatabaseSeeder.cs` | **New** — §14 (does not exist today) |
| `Shared/Auth/DevAuthHandler.cs` | **DELETE** — §15 |
| `Program.cs` | Edit — add four lines, remove the `DevAuth` blocks and the `RecipientDirectoryStub` startup guard, make `UseAuthentication` unconditional, run the seeder after migrations |
| `appsettings.json` | Edit — add `App:BaseUrl`, `DataProtection:KeyPath`, and `Seeding` |
| `appsettings.Development.json` | Edit — **remove** the `DevAuth` section |
| `AccountantApp.Api.csproj` | Edit — add `Microsoft.AspNetCore.Identity` |
| `Slices/Notifications/ExternalInterfaces/RecipientDirectoryStub.cs` | **DELETE** — §9.2 |
| `Slices/Notifications/NotificationsRegistration.cs` | Edit — **remove** the `RecipientDirectoryStub` registration |
| `AccountantApp.Tests/Identity/` | **New** — §16. Existing tests in other slices need **no** change (§15) |

---

## Success criteria

1. The migration applies to a fresh PostgreSQL database, and both `CHECK` constraints and all five
   indexes exist.
2. A `CustomerAdmin` row with a null `customer_id` is rejected by the database, not by a handler.
3. The seeded first `AccountantAdmin` is created from configuration, is `Active`, and has
   `must_change_password = TRUE`. Startup **fails** when the configuration is absent.
4. Startup fails when `DataProtection:KeyPath` is missing or unwritable — it never falls back to
   ephemeral keys.
5. `POST /api/auth/login` with correct credentials returns `200` and sets an `HttpOnly`, `Secure`,
   `SameSite=Strict` cookie named `aa_session`.
6. A `CustomerAdmin`'s cookie carries a `customer_id` claim; an Accountant's carries none; both can
   make a subsequent authorized request.
7. All six login failure causes return an identical `401` body, and the audit log distinguishes
   them.
8. Five wrong passwords set `lockout_expires_at` **in the database**, and the correct password is
   still refused while it is in the future.
9. A wrong password on `/api/auth/change-password` also increments the counter in the database.
10. Login for an unknown email takes comparable time to login for a known one.
11. `ICustomerApi.IsActiveAsync` is called on every Customer-side login and never for an
    Accountant, and its result is never cached.
12. Before the seeded Admin changes their password, every endpoint except the three exemptions
    returns `403` with `code = "password_change_required"`; afterwards they all work, without a
    re-login.
13. `POST /api/auth/request-password-reset` returns an identical `200` for a real, a suspended, and
    a nonexistent address, and only the real one produces a token row.
14. No raw token is present in `notifications.body`, in any response body, in any log line, or in
    any audit entry. The only raw token in the system is in `notification_outbox.email_body`, and
    it is `NULL` once sent.
15. A token cannot be redeemed twice, after expiry, or for the wrong purpose.
16. `reset-password` does not issue a cookie.
17. Accepting an invitation sets `Status = Active` and `EmailConfirmedAt`, and leaves
    `MustChangePassword` false.
18. Inviting a duplicate email returns `409`, from both the pre-check and the constraint.
19. `/api/accountants/list` returns JSON with **no `loginEmail` key** for an `AccountantUser`, and
    `403` for a `CustomerAdmin`.
20. Suspending or demoting the last `Active` `AccountantAdmin` returns `422` and leaves the row
    unchanged.
21. Self-suspension and self-demotion return `422`.
22. `/api/accountants/suspend` on a Customer-side account returns `404`, not `403`.
23. `IIdentityApi` write methods enlist in the caller's transaction and never commit.
24. `IIdentityApi` rejects an Accountant role on `InviteEmployeeAccountAsync` and both mistakes on
    `SetCustomerSideRoleAsync`, and throws for an Accountant target on `ChangeLoginEmailAsync`.
24a. `ReactivateAccountAsync` restores a hashless account to `Invited` and a hashed one to `Active`
    (§9.1 rule 14). A test that only asserts "not `Suspended`" does not cover this.
24b. `ChangeLoginEmailAsync` changes the address the account logs in with, returns `409` for a taken
    address, and leaves the password hash, status, and `EmailConfirmedAt` untouched.
25. No type reachable through an `ExternalInterface` exposes a hash, a token, or lockout state.
26. `IRecipientDirectory` is registered by this slice, `NullRecipientDirectory` is gone, and
    resolving the interface at startup succeeds.
27. Every route uses kebab-case for multi-word segments and no route has a route parameter.
28. `DevAuthHandler.cs` no longer exists; `app.UseAuthentication()` is unconditional; a
    solution-wide search for `DevAuth` and `X-Dev-Role` returns zero hits.
29. The existing tests in `Audit`, `TicketTypes`, `Notifications`, and `Customers` still pass
    **unmodified** — they construct `CurrentUser` in-process and never authenticated via `DevAuth`,
    so deleting it must not touch them (§15). New HTTP-pipeline coverage is additive.
30. Every operation in the §7.0 F table writes an Audit entry, including all failures, and no entry
    contains a password, hash, or token.
