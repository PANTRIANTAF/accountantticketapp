# Notifications Slice — Build Plan

Build order and task breakdown. Read [BLOCKERS_RESOLVED.md](BLOCKERS_RESOLVED.md) first.

---

## Phase 1: Database & EF Setup (2–3 hours)

### Task 1.1: SQL Migration
**File:** `AccountantApp.Api/Slices/Notifications/Infrastructure/Migrations/20260830_001_CreateNotificationsSchema.sql`

From plan §1, create both tables with all constraints and indexes:
- `notifications` table with `notifications_pkey` (id)
- `notification_outbox` table with FK to notifications(id)
- 3 indexes: `idx_notifications_recipient`, `idx_notifications_unread` (partial), `idx_outbox_due` (partial)
- Copy exactly from plan §1 lines 83–190

**Success:** Migration applies cleanly to fresh PostgreSQL; both tables, constraints, and indexes exist.

### Task 1.2: EF Core Entities
**Files:**
- `AccountantApp.Api/Slices/Notifications/Core/Notification.cs`
- `AccountantApp.Api/Slices/Notifications/Core/OutboxEntry.cs`

Copy from plan §2.1–§2.2, lines 214–263. Add a `OutboxStatus` static class with the 5 constants.

**Critical:** Do NOT add navigation properties yet. Keep entities standalone.

**Success:** Entities match plan exactly; no Content property, no Status navigation.

### Task 1.3: EF Configurations (Full Code)
**Files:**
- `AccountantApp.Api/Slices/Notifications/Infrastructure/Configurations/NotificationConfiguration.cs`
- `AccountantApp.Api/Slices/Notifications/Infrastructure/Configurations/OutboxEntryConfiguration.cs`

**From plan §2.5, write out COMPLETE `IEntityTypeConfiguration` implementations** (the plan is prose-only; write the code):

**NotificationConfiguration:**
- `ToTable("notifications")`
- Every property gets `HasColumnName` (even `Id` → `"id"`)
- `HasColumnName("recipient_user_id")` + `HasMaxLength(100)` on `RecipientUserId`
- `HasColumnName("ticket_id")` on `TicketId` (nullable, no FK)
- `HasColumnName("event_kind")` + `HasMaxLength(100)` on `EventKind`
- `HasColumnName("title")` + `HasMaxLength(200)` on `Title`
- `HasColumnName("body")` + `HasMaxLength(2000)` on `Body`
- `HasColumnName("is_read")` on `IsRead`
- `HasColumnName("read_at")` on `ReadAt` (nullable)
- `HasColumnName("created_at")` on `CreatedAt` + `ValueGeneratedOnAdd()`
- `HasKey(n => n.Id)` implicit

**OutboxEntryConfiguration:**
- `ToTable("notification_outbox")`
- `HasColumnName("notification_id")` on `NotificationId` + `HasForeignKey` to notifications(id)
- `HasColumnName("resolved_email")` + `HasMaxLength(320)` on `ResolvedEmail` (nullable)
- `HasColumnName("email_body")` + `HasMaxLength(4000)` on `EmailBody` (nullable)
- `HasColumnName("status")` + `HasMaxLength(20)` on `Status`
- `HasColumnName("attempt_count")` on `AttemptCount`
- `HasColumnName("next_attempt_at")` on `NextAttemptAt`
- `HasColumnName("last_error")` + `HasMaxLength(1000)` on `LastError` (nullable)
- `HasColumnName("created_at")` on `CreatedAt` + `ValueGeneratedOnAdd()`
- `HasColumnName("sent_at")` on `SentAt` (nullable)

**Success:** Reflection checks pass (all properties mapped); no type mismatches on query.

### Task 1.4: DbContext
**File:** `AccountantApp.Api/Slices/Notifications/Infrastructure/NotificationsDbContext.cs`

Copy from plan §2.4, lines 288–304. Requires `DbContextOptions<NotificationsDbContext>` in ctor.

**Success:** Two `DbSet<T>` properties; `OnModelCreating` applies both configurations.

---

## Phase 2: Event Catalogue & API Contract (1–2 hours)

### Task 2.1: Event Catalogue
**File:** `AccountantApp.Api/Slices/Notifications/ExternalInterfaces/NotificationEvents.cs`

Copy from plan §3, lines 329–366. **Write the reflection code** for `All`:

```csharp
public static readonly IReadOnlySet<string> All = new(
    typeof(NotificationEvents)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(string) && f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
    , StringComparer.Ordinal);
```

**Success:** `All.Count > 0`; `All.Contains("Invited")` passes; `Emailed` is subset of `All`.

### Task 2.2: DTOs
**Files:** `AccountantApp.Api/Slices/Notifications/Application/Dtos/*.cs`

From plan §6, create (as plain classes, not records, for minimal-API binding):
- `NotificationDto` — no `RecipientUserId` field
- `ListMyNotificationsRequestDto` — `UnreadOnly`, `PageNumber`, `PageSize`
- `UnreadCountResponseDto` — `UnreadCount`
- `MarkReadRequestDto` — `NotificationIds` (List<Guid>)
- `MarkReadResponseDto` — `MarkedCount`

**Success:** No DTO has `RecipientUserId`; all are valid for minimal-API binding.

### Task 2.3: INotificationApi & IRecipientDirectory
**Files:**
- `AccountantApp.Api/Slices/Notifications/ExternalInterfaces/INotificationApi.cs`
- `AccountantApp.Api/Slices/Notifications/ExternalInterfaces/IRecipientDirectory.cs`

Copy contracts from plan §4.1 and §5.3, lines 394–424 and 574–582.

**Success:** Contracts match plan exactly; `IRecipientDirectory` has `Recipient` record and `FindAsync` method.

---

## Phase 3: Core Implementation (6–8 hours)

### Task 3.1: NotificationApi Implementation
**File:** `AccountantApp.Api/Slices/Notifications/ExternalInterfaces/NotificationApi.cs`

Implement from plan §4.2 rules A–H:

- A: `EnlistAsync(_db, ct)` before writing
- B: Unknown `EventKind` → `InvalidOperationException`
- C: Validate lengths; throw on title > 200, truncate body @ 2000 + "…"
- D: `NotifyManyAsync` collapses duplicate `(RecipientUserId, EventKind, TicketId)` triples
- E: Resolve `CurrentUser` lazily; skip self-notifications (log at debug)
- F: Enqueue outbox row only for emailed kinds; throw if `EmailBody` set on non-emailed
- F2: `EmailBody` > 4000 → `InvalidOperationException`
- G: Do NOT resolve recipient email here
- H: No audit

**Critical (from BLOCKERS_RESOLVED):** Gap 6 — collapse happens **after** self-drop and **after** validating non-empty recipient. Return count of rows created.

**Success:** `NotifyAsync` rejects unknown event kind; `NotifyManyAsync` collapses overlapping recipients; self-notifications are dropped.

### Task 3.2: IEmailSender & LoggingEmailSender
**Files:**
- `AccountantApp.Api/Slices/Notifications/Application/IEmailSender.cs`
- `AccountantApp.Api/Slices/Notifications/Infrastructure/OutboxDrainer.cs` (init)

From plan §5.1–§5.2, lines 514–549:

- Define `EmailMessage`, `EmailSendOutcome` enum, `EmailSendResult`
- Implement `LoggingEmailSender`: log only metadata in dev, no body (per BLOCKERS_RESOLVED N-8)
- Return `EmailSendResult` (no exception throwing)

**Success:** `LoggingEmailSender` logs only To/Subject; body never logged.

### Task 3.3: IRecipientDirectory Stub (Pre-Identity)
**File:** `AccountantApp.Api/Slices/Notifications/ExternalInterfaces/RecipientDirectoryStub.cs`

From plan §5.3 rule 4: implement stub, guarded by `TryAddScoped`:

```csharp
public class RecipientDirectoryStub : IRecipientDirectory
{
    public Task<Recipient?> FindAsync(string userAccountId, CancellationToken ct) => 
        Task.FromResult((Recipient?)null);
}
```

This is temporary; deleted when Identity is built.

**Success:** Registration uses `TryAddScoped`, so Identity's real impl wins on override.

---

## Phase 4: HTTP Endpoints & Handlers (4–6 hours)

### Task 4.1: Handlers (4 total)
**Files:** `AccountantApp.Api/Slices/Notifications/Application/Handlers/*.cs`

From plan §7, implement 4 handlers. All follow 7.0 rules A–F:

#### Handler 1: ListMyNotificationsHandler
- `Handle(ListMyNotificationsRequestDto req, CurrentUser user, CancellationToken ct)`
- `RequireAsync(user, "ReadOwnNotifications", ct)`
- Clamp page/size (default 15, max 50)
- Filter: `n.RecipientUserId == user.Id` + optional `!n.IsRead`
- Order: `created_at DESC, id DESC`
- Project to `NotificationDto` (join outbox for `EmailStatus`)
- Return `PaginatedResponse<NotificationDto>`

#### Handler 2: GetUnreadCountHandler
- `Handle(CurrentUser user, CancellationToken ct)` (no request DTO)
- `RequireAsync(user, "ReadOwnNotifications", ct)`
- Query: `COUNT(*)` where `recipient_user_id == user.Id AND is_read == false`
- Return `UnreadCountResponseDto`

#### Handler 3: MarkNotificationsReadHandler
- `Handle(MarkReadRequestDto req, CurrentUser user, CancellationToken ct)`
- `RequireAsync(user, "MarkOwnNotificationRead", ct)`
- Validate: `req.NotificationIds` not null/empty (→ 400)
- Load: `_db.Notifications.Where(n => n.RecipientUserId == user.Id && req.NotificationIds.Contains(n.Id))`
- Update: `IsRead = true, ReadAt = DateTimeOffset.UtcNow`
- **From BLOCKERS_RESOLVED N-1:** If `result.Count < req.Count`, write one audited denial of "MarkOwnNotificationRead"
- Return `MarkReadResponseDto { MarkedCount = result.Count }`

#### Handler 4: MarkAllNotificationsReadHandler
- `Handle(CurrentUser user, CancellationToken ct)` (no request DTO)
- `RequireAsync(user, "MarkOwnNotificationRead", ct)`
- Update all unread: `WHERE recipient_user_id == user.Id AND is_read == false`
- Return `MarkReadResponseDto { MarkedCount = updated.Count }`

**Success:** All handlers filter by `user.Id` first; no cross-user reads possible.

### Task 4.2: Endpoints
**File:** `AccountantApp.Api/Slices/Notifications/NotificationsEndpoints.cs`

Register 4 routes (minimal API):
- `POST /api/notifications/list` → `ListMyNotificationsHandler`
- `GET /api/notifications/unread-count` → `GetUnreadCountHandler`
- `POST /api/notifications/mark-read` → `MarkNotificationsReadHandler`
- `POST /api/notifications/mark-all-read` → `MarkAllNotificationsReadHandler`

All require `[Authorize]` (via minimal API requirements).

**Success:** Routes are discoverable; all require auth.

---

## Phase 5: Email Drainer (4–5 hours)

### Task 5.1: OutboxDrainer — BackgroundService
**File:** `AccountantApp.Api/Slices/Notifications/Infrastructure/OutboxDrainer.cs`

Implement from plan §5.4, with BLOCKERS_RESOLVED corrections:

**Constructor:**
- Inject `IServiceScopeFactory`, `IEmailSender` (not directly), `IRecipientDirectory` (not directly), `ILogger`, `IHostEnvironment`, `IConfiguration`

**ExecuteAsync loop:**
```
while (!stoppingToken.IsCancellationRequested):
  try:
    scope = _scopeFactory.CreateScope()
    db = scope.GetRequiredService<NotificationsDbContext>()
    
    due = await db.Outbox.AsNoTracking()
        .Where(o => o.Status == Pending && o.NextAttemptAt <= now)
        .Join(db.Notifications.AsNoTracking(),      // BLOCKERS N-6
              o => o.NotificationId,
              n => n.Id,
              (o, n) => new { o, n })
        .OrderBy(x => x.o.NextAttemptAt)
        .Take(BatchSize)
        .ToListAsync(ct)
    
    foreach (item in due):
      ProcessEntry(item.o, item.n)    // Process individually (N-11)
    
  catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested):
    break
  catch (Exception ex):
    _logger.LogError(ex, "Outbox drain iteration failed")  // Swallow
  
  await Task.Delay(PollInterval, ct)
```

**ProcessEntry(entry, notification):**
From plan §5.4 rule 4 (outcomes table, lines 649–659):

```
1. Resolve recipient email (FindAsync from IRecipientDirectory)
   - null → Skipped, "No such account"
   - IsActive == false && kind not in (Invited, EmployeeInvited) → Skipped
   - Otherwise, proceed

2. Check email disabled by configuration → Skipped

3. Build EmailMessage:
   - To: recipient.Email
   - Subject: notification.Title
   - Body: entry.EmailBody ?? notification.Body   (BLOCKERS N-6)
   
4. Call IEmailSender.SendAsync()

5. Handle outcome:
   - Sent → status = Sent, sent_at = now, resolved_email recorded, email_body = null
   - TransientFailure → attempt_count++, if < MaxAttempts: next_attempt_at += backoff, else: Abandoned
   - PermanentFailure → Abandoned immediately
   - Exception → treat as TransientFailure, truncate message to 1000 chars

6. Save per-entry (N-11: not batched)
```

**Backoff:** 1m, 5m, 15m, 1h, 6h, then Abandoned (attempt 6).

**Configuration:** Read from `Notifications:Email:*`:
- `Enabled` (bool)
- `FromAddress`, `FromName` (strings, not used in v1 LoggingEmailSender)
- `PollIntervalSeconds` (int, default 30)
- `BatchSize` (int, default 20)
- `MaxAttempts` (int, default 6)

**Connection isolation (BLOCKERS N-7):** Do NOT use `RequestConnection`. Create a separate options config:

```csharp
// In NotificationsRegistration.cs
services.AddDbContext<NotificationsDbContext>(
    (sp, options) => options.UseNpgsql(
        configuration.GetConnectionString("DefaultConnection")), // Direct, not RequestConnection
    ServiceLifetime.Transient);
```

**Success:** One bad row doesn't stop the batch; drainer continues on exception.

### Task 5.2: Drainer Registration
**File:** `AccountantApp.Api/Slices/Notifications/NotificationsRegistration.cs` (continuation)

Register the drainer:

```csharp
if (configuration.GetValue<bool>("Notifications:Email:Enabled"))
{
    services.AddSingleton<OutboxDrainer>();
    services.AddHostedService(sp => sp.GetRequiredService<OutboxDrainer>());
}
```

**Startup check for IRecipientDirectory (BLOCKERS gap 1):**

```csharp
// At end of AddNotificationsSlice, before returning:
using (var scope = services.BuildServiceProvider().CreateScope())
{
    var dir = scope.ServiceProvider.GetService<IRecipientDirectory>();
    if (dir is null || dir is RecipientDirectoryStub)
    {
        throw new InvalidOperationException(
            "IRecipientDirectory is not registered. " +
            "Until Identity is built, the stub is registered; after Identity is added, " +
            "it must register the real implementation. See Notifications plan §5.3 rule 3.");
    }
}
```

Actually, the stub exists so the check should be: if it's the stub, log a warning but allow (for pre-Identity). When Identity ships, it overrides via `TryAddScoped`.

**Success:** Drainer starts if enabled; startup fails clearly if IRecipientDirectory isn't configured.

---

## Phase 6: Service Registration & Program.cs (1 hour)

### Task 6.1: NotificationsRegistration.cs (Full)
**File:** `AccountantApp.Api/Slices/Notifications/NotificationsRegistration.cs`

```csharp
public static IServiceCollection AddNotificationsSlice(
    this IServiceCollection services, IConfiguration config)
{
    // DbContext with its own connection (not RequestConnection)
    services.AddDbContext<NotificationsDbContext>(
        (sp, options) => options.UseNpgsql(
            config.GetConnectionString("DefaultConnection")),
        ServiceLifetime.Transient);
    
    services.AddScoped<INotificationApi, NotificationApi>();
    services.AddScoped<IEmailSender, LoggingEmailSender>();
    services.TryAddScoped<IRecipientDirectory, RecipientDirectoryStub>();
    
    if (config.GetValue<bool>("Notifications:Email:Enabled"))
    {
        services.AddSingleton<OutboxDrainer>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxDrainer>());
    }
    
    // Action catalogue fragment
    services.AddNotificationsActions();
    
    return services;
}
```

### Task 6.2: Action Catalogue Fragment
**File:** `AccountantApp.Api/Slices/Notifications/NotificationsActionCatalogue.cs`

Two actions, all four roles:

```csharp
public sealed class NotificationsActionCatalogue : IActionCatalogue
{
    public IEnumerable<ActionDefinition> GetActions()
    {
        yield return new("ReadOwnNotifications", isPublic: true,
                        AllowedRoles: [AA, AU, CA, EMP]);
        yield return new("MarkOwnNotificationRead", isPublic: true,
                        AllowedRoles: [AA, AU, CA, EMP]);
    }
}
```

### Task 6.3: Program.cs Edit
Add ONE line after Audit registration:

```csharp
builder.Services.AddNotificationsSlice(builder.Configuration);
```

**Before:** `AddTicketsSlice` (if it exists) and any other slice.

---

## Phase 7: appsettings & Configuration (30 mins)

### Task 7.1: appsettings.json & appsettings.Development.json
Add configuration section:

```json
"Notifications": {
  "Email": {
    "Enabled": false,
    "FromAddress": "noreply@accountantapp.local",
    "FromName": "Accountant App",
    "PollIntervalSeconds": 30,
    "BatchSize": 20,
    "MaxAttempts": 6
  }
}
```

In Development, set `Enabled` to `true` if you want to test the drainer loop (it will log, not send).

---

## Phase 8: Tests (8–12 hours)

### Critical: PostgreSQL Integration Test Required
Per plan §12.1, at least one real-database test. Use Docker Compose or a test container.

From plan §12.2–§12.3, ~27 behavioral cases. Key ones:

- Notification round-trip (create, list, mark read)
- `NotifyAsync` rejects unknown event kind
- `NotifyManyAsync` collapses duplicates
- `NotifyAsync` rejects empty recipient
- Out-of-scope notification returns 404 (not 403)
- Drainer processes Pending entries
- Drainer backs off on transient failures
- Drainer abandons on permanent failure
- One bad row in a batch doesn't stop others (N-11)
- Invitation email is delivered even if IsActive == false
- Suspended account skips non-invitation mail
- Skipped/Abandoned rows clear `email_body` (N-9)
- `EmailBody` is never logged
- Bulk mark-read with mixed in/out-of-scope IDs returns MarkedCount (N-1)

**Success:** All test cases pass; PostgreSQL test runs (not skipped).

---

## Checklist

- [ ] SQL migration (1.1)
- [ ] Entities (1.2)
- [ ] EF Configurations full code (1.3)
- [ ] DbContext (1.4)
- [ ] Event Catalogue with reflection (2.1)
- [ ] DTOs (2.2)
- [ ] INotificationApi, IRecipientDirectory (2.3)
- [ ] NotificationApi implementation (3.1)
- [ ] IEmailSender, LoggingEmailSender, stub (3.2–3.3)
- [ ] 4 Handlers (4.1)
- [ ] Endpoints (4.2)
- [ ] OutboxDrainer (5.1–5.2)
- [ ] NotificationsRegistration (6.1–6.2)
- [ ] Action Catalogue (6.2)
- [ ] Program.cs edit (6.3)
- [ ] appsettings (7.1)
- [ ] Tests (8.0)

**Estimated time: 25–35 hours** for a developer familiar with the codebase.
