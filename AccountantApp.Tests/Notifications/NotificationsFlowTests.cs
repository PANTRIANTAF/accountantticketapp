using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Audit.Infrastructure;
using AccountantApp.Api.Slices.Notifications;
using AccountantApp.Api.Slices.Notifications.Application.Dtos;
using AccountantApp.Api.Slices.Notifications.Application.Handlers;
using AccountantApp.Api.Slices.Notifications.Core;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.Infrastructure;
using AccountantApp.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccountantApp.Tests.Notifications;

public sealed class NotificationsFlowTests
{
    // The denial branch passed the IActionCatalogue permission name "MarkOwnNotificationRead" to
    // IAuditApi as an audit action. AuditApi rejects anything outside AuditActions.All, so any
    // request naming an id the caller does not own threw InvalidOperationException -- a 500 the
    // client could trigger at will, after the other notifications had already been saved. The bug
    // was reachable only on the partial-match path, which nothing exercised.
    [Fact]
    public async Task Marking_ids_the_caller_does_not_own_audits_a_denial_instead_of_throwing()
    {
        await using var db = CreateDb();
        await using var auditDb = CreateAuditDb();
        var user = User("recipient-1");

        var mine = await AddNotification(db, "recipient-1");
        var theirs = await AddNotification(db, "recipient-2");

        var audit = CreateAuditApi(auditDb);
        var handler = new MarkNotificationsReadHandler(db, Permissions(audit), new NoOpRequestTransaction(), audit);

        var result = await handler.Handle(
            new MarkReadRequestDto { NotificationIds = [mine, theirs] }, user, CancellationToken.None);

        Assert.Equal(1, result.MarkedCount);

        var entry = await auditDb.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.PermissionDenied, entry.Action);
        Assert.Equal(AuditTargets.Notification, entry.TargetKind);
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        // The action written has to be in the catalogue the audit reader filters on, or the entry
        // could never have been inserted in the first place.
        Assert.Contains(entry.Action, AuditActions.All);
    }

    [Fact]
    public async Task Another_recipients_notification_is_not_marked_read()
    {
        await using var db = CreateDb();
        await using var auditDb = CreateAuditDb();
        var theirs = await AddNotification(db, "recipient-2");

        var audit = CreateAuditApi(auditDb);
        var handler = new MarkNotificationsReadHandler(db, Permissions(audit), new NoOpRequestTransaction(), audit);

        var result = await handler.Handle(
            new MarkReadRequestDto { NotificationIds = [theirs] }, User("recipient-1"), CancellationToken.None);

        Assert.Equal(0, result.MarkedCount);
        Assert.False((await db.Notifications.SingleAsync(n => n.Id == theirs)).IsRead);
    }

    // EmailStatus used to be fetched with one query per row inside the projection loop. Batching it
    // into a dictionary is only correct if each row still gets its own status back, so this pins the
    // pairing rather than the query count.
    [Fact]
    public async Task Each_row_gets_its_own_email_status_and_null_when_none_was_queued()
    {
        await using var db = CreateDb();
        await using var auditDb = CreateAuditDb();

        var sent = await AddNotification(db, "recipient-1");
        var pending = await AddNotification(db, "recipient-1");
        var neverEmailed = await AddNotification(db, "recipient-1");

        db.Outbox.AddRange(
            NewOutboxEntry(sent, OutboxStatus.Sent),
            NewOutboxEntry(pending, OutboxStatus.Pending));
        await db.SaveChangesAsync();

        var handler = new ListMyNotificationsHandler(db, Permissions(CreateAuditApi(auditDb)));

        var page = await handler.Handle(
            new ListMyNotificationsRequestDto(), User("recipient-1"), CancellationToken.None);

        Assert.Equal(3, page.TotalCount);
        var byId = page.Items.ToDictionary(item => item.Id);
        Assert.Equal(OutboxStatus.Sent, byId[sent].EmailStatus);
        Assert.Equal(OutboxStatus.Pending, byId[pending].EmailStatus);
        Assert.Null(byId[neverEmailed].EmailStatus);
    }

    [Fact]
    public async Task Only_the_callers_own_notifications_are_listed()
    {
        await using var db = CreateDb();
        await using var auditDb = CreateAuditDb();
        await AddNotification(db, "recipient-1");
        await AddNotification(db, "recipient-2");

        var handler = new ListMyNotificationsHandler(db, Permissions(CreateAuditApi(auditDb)));

        var page = await handler.Handle(
            new ListMyNotificationsRequestDto(), User("recipient-1"), CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
        Assert.All(page.Items, item => Assert.Equal("recipient-1", db.Notifications.Single(n => n.Id == item.Id).RecipientUserId));
    }

    [Fact]
    public async Task The_unread_count_ignores_other_recipients_and_read_rows()
    {
        await using var db = CreateDb();
        await using var auditDb = CreateAuditDb();
        await AddNotification(db, "recipient-1");
        await AddNotification(db, "recipient-1", isRead: true);
        await AddNotification(db, "recipient-2");

        var handler = new GetUnreadCountHandler(db, Permissions(CreateAuditApi(auditDb)));

        var response = await handler.Handle(User("recipient-1"), CancellationToken.None);

        Assert.Equal(1, response.UnreadCount);
    }

    // Re-marking your own already-read notification is an ordinary duplicate click. It used to be
    // indistinguishable from reaching for a stranger's notification, because the denial compared
    // rows-changed against ids-asked-for, so it manufactured a Denied security event against an
    // innocent user -- and after that, "Denied" no longer meant anything an investigator could use.
    [Fact]
    public async Task Re_marking_an_already_read_notification_is_a_clean_no_op_not_a_denial()
    {
        await using var db = CreateDb();
        await using var auditDb = CreateAuditDb();
        var mine = await AddNotification(db, "recipient-1", isRead: true);

        var audit = CreateAuditApi(auditDb);
        var handler = new MarkNotificationsReadHandler(db, Permissions(audit), new NoOpRequestTransaction(), audit);

        var result = await handler.Handle(
            new MarkReadRequestDto { NotificationIds = [mine] }, User("recipient-1"), CancellationToken.None);

        Assert.Equal(0, result.MarkedCount);
        Assert.Empty(await auditDb.AuditEntries.ToListAsync());
    }

    // ArgumentException is not AppException, so AppExceptionMiddleware turned a client-supplied
    // empty array into a 500 rather than a 4xx.
    [Fact]
    public async Task An_empty_id_list_is_a_client_error_not_a_server_error()
    {
        await using var db = CreateDb();
        await using var auditDb = CreateAuditDb();
        var audit = CreateAuditApi(auditDb);
        var handler = new MarkNotificationsReadHandler(db, Permissions(audit), new NoOpRequestTransaction(), audit);

        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new MarkReadRequestDto { NotificationIds = [] }, User("recipient-1"), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    // Without a cap, a request body of 100,000 ids became one enormous IN list.
    [Fact]
    public async Task An_oversized_batch_is_rejected_before_it_reaches_the_query()
    {
        await using var db = CreateDb();
        await using var auditDb = CreateAuditDb();
        var audit = CreateAuditApi(auditDb);
        var handler = new MarkNotificationsReadHandler(db, Permissions(audit), new NoOpRequestTransaction(), audit);

        var tooMany = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToList();

        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new MarkReadRequestDto { NotificationIds = tooMany }, User("recipient-1"), CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task Every_event_kind_the_slice_emails_is_a_known_event_kind()
    {
        await Task.CompletedTask;
        Assert.NotEmpty(NotificationEvents.All);
        Assert.All(NotificationEvents.Emailed, kind => Assert.Contains(kind, NotificationEvents.All));
    }

    private static NotificationsDbContext CreateDb() => new(
        new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AuditDbContext CreateAuditDb() => new(
        new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // The real AuditApi, not a double: the bug this file exists for was AuditApi rejecting the
    // action name, so a double that accepted any string would have reported the code as correct.
    private static AuditApi CreateAuditApi(AuditDbContext auditDb)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new CurrentUser("recipient-1", UserRole.Employee));
        return new AuditApi(
            auditDb,
            new NoOpRequestTransaction(),
            new HttpContextAccessor(),
            services.BuildServiceProvider(),
            NullLogger<AuditApi>.Instance);
    }

    private static PermissionChecker Permissions(IAuditApi audit) => new(
        [new NotificationsActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);

    private static CurrentUser User(string id) => new(id, UserRole.Employee, Guid.NewGuid());

    private static async Task<Guid> AddNotification(
        NotificationsDbContext db, string recipientUserId, bool isRead = false)
    {
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            RecipientUserId = recipientUserId,
            EventKind = NotificationEvents.All.First(),
            Title = "Title",
            Body = "Body",
            IsRead = isRead,
            ReadAt = isRead ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();
        return notification.Id;
    }

    private static OutboxEntry NewOutboxEntry(Guid notificationId, string status) => new()
    {
        Id = Guid.NewGuid(),
        NotificationId = notificationId,
        ResolvedEmail = "someone@example.com",
        Status = status,
        NextAttemptAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
