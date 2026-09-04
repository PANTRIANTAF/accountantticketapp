using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Audit;
using AccountantApp.Api.Slices.Audit.Application.Dtos;
using AccountantApp.Api.Slices.Audit.Application.Handlers;
using AccountantApp.Api.Slices.Audit.Core;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Audit.Infrastructure;
using AccountantApp.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccountantApp.Tests.Audit;

// The read side of the Audit slice: three handlers whose whole job is to expose a table only
// AccountantAdmin may see. Until these existed, "ReadAuditLog" sat in AuditActionCatalogue with no
// caller anywhere in the repository -- one of the four powers reserved to AccountantAdmin was
// catalogued and unenforceable, because there was nothing to enforce it on.
public sealed class AuditReadTests
{
    // ---- Authorization: one power, three handlers, and every denial recorded ----

    [Fact]
    public async Task An_accountant_admin_can_search_the_log()
    {
        await using var db = CreateDb();
        await AddEntry(db, AuditActions.CustomerCreated);
        var admin = Admin();

        var page = await Search(db, admin).Handle(new SearchAuditLogRequestDto(), admin, CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Items);
    }

    [Theory]
    [InlineData(UserRole.AccountantUser)]
    [InlineData(UserRole.CustomerAdmin)]
    [InlineData(UserRole.Employee)]
    public async Task Every_other_role_is_denied_the_search_and_the_denial_is_recorded(UserRole role)
    {
        await using var db = CreateDb();
        await AddEntry(db, AuditActions.CustomerCreated);
        var user = User(role);

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            Search(db, user).Handle(new SearchAuditLogRequestDto(), user, CancellationToken.None));

        Assert.Equal(403, exception.StatusCode);

        // The denial lands in the table the caller was trying to read. An AccountantUser probing the
        // audit log leaves a trace in it, which is the entire point of reserving the power.
        var denial = await db.AuditEntries.SingleAsync(e => e.Action == AuditActions.PermissionDenied);
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Equal(role.ToString(), denial.ActorRole);
        Assert.Equal(user.Id, denial.ActorUserId);
    }

    [Theory]
    [InlineData(UserRole.AccountantUser)]
    [InlineData(UserRole.CustomerAdmin)]
    [InlineData(UserRole.Employee)]
    public async Task Every_other_role_is_denied_the_detail_and_the_action_codes(UserRole role)
    {
        await using var db = CreateDb();
        var id = await AddEntry(db, AuditActions.CustomerCreated);
        var user = User(role);

        var onDetail = await Assert.ThrowsAsync<AppException>(() => Detail(db, user).Handle(
            new GetAuditEntryRequestDto { AuditEntryId = id }, user, CancellationToken.None));
        var onCodes = await Assert.ThrowsAsync<AppException>(() =>
            Codes(db, user).Handle(user, CancellationToken.None));

        Assert.Equal(403, onDetail.StatusCode);
        Assert.Equal(403, onCodes.StatusCode);
    }

    // All three handlers guard the same action name, because 02-AuthorizationMatrix grants one
    // power. Three names for one power would be three places to get the role list wrong.
    [Fact]
    public void All_three_read_handlers_are_governed_by_the_single_ReadAuditLog_action()
    {
        var catalogue = new AuditActionCatalogue();

        Assert.Equal(new[] { "ReadAuditLog" }, catalogue.Actions.Keys);
        Assert.Equal(new[] { UserRole.AccountantAdmin }, catalogue.Actions["ReadAuditLog"]);
    }

    // ---- Reading must not mutate ----

    // 01-DomainModel section 8: the log is append-only, and reading it is not itself an audited
    // action. A read that wrote an entry would make the table grow on every page view, and the first
    // thing an investigator did would bury what they came to look at.
    [Fact]
    public async Task Reading_the_log_writes_nothing_to_it()
    {
        await using var db = CreateDb();
        var id = await AddEntry(db, AuditActions.CustomerCreated);
        var admin = Admin();

        await Search(db, admin).Handle(new SearchAuditLogRequestDto(), admin, CancellationToken.None);
        await Detail(db, admin).Handle(
            new GetAuditEntryRequestDto { AuditEntryId = id }, admin, CancellationToken.None);
        await Codes(db, admin).Handle(admin, CancellationToken.None);

        Assert.Equal(1, await db.AuditEntries.CountAsync());
    }

    // ---- Rejected filters: 422, never a silent empty page ----

    [Fact]
    public async Task A_reversed_date_range_is_rejected()
    {
        await using var db = CreateDb();
        var admin = Admin();
        var now = DateTimeOffset.UtcNow;

        var exception = await Assert.ThrowsAsync<AppException>(() => Search(db, admin).Handle(
            new SearchAuditLogRequestDto { From = now, To = now.AddDays(-1) }, admin, CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    // Ids are not unique across kinds, so a TargetId on its own can return entries about an unrelated
    // entity that happens to share a GUID string -- and it cannot use the composite index whose
    // leading column is target_kind, so it scans the largest table in the database.
    [Fact]
    public async Task A_target_id_without_its_kind_is_rejected()
    {
        await using var db = CreateDb();
        var admin = Admin();

        var exception = await Assert.ThrowsAsync<AppException>(() => Search(db, admin).Handle(
            new SearchAuditLogRequestDto { TargetId = Guid.NewGuid().ToString() },
            admin, CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    // A mistyped filter that returns zero rows tells an investigator "this never happened". That is
    // the one answer an audit tool must never give by accident, so each of these is a 422 that makes
    // them retype it rather than an empty page that reads like a finding.
    [Theory]
    [InlineData("CustomerDeleted", null, null)]     // plausible, but not in the catalogue
    [InlineData(null, "Invoice", null)]             // not a known target kind
    [InlineData(null, null, "success")]             // right value, wrong case
    [InlineData(null, null, "Rejected")]            // not an outcome at all
    public async Task An_unrecognised_filter_value_is_rejected_rather_than_returning_an_empty_page(
        string? action, string? targetKind, string? outcome)
    {
        await using var db = CreateDb();
        await AddEntry(db, AuditActions.CustomerCreated);
        var admin = Admin();

        var exception = await Assert.ThrowsAsync<AppException>(() => Search(db, admin).Handle(
            new SearchAuditLogRequestDto { Action = action, TargetKind = targetKind, Outcome = outcome },
            admin, CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task Recognised_filter_values_are_accepted_so_the_guard_is_not_rejecting_everything()
    {
        await using var db = CreateDb();
        await AddEntry(db, AuditActions.CustomerCreated, targetKind: AuditTargets.Customer);
        await AddEntry(db, AuditActions.TicketCreated, targetKind: AuditTargets.Ticket);
        var admin = Admin();

        var page = await Search(db, admin).Handle(new SearchAuditLogRequestDto
        {
            Action = AuditActions.CustomerCreated,
            TargetKind = AuditTargets.Customer,
            Outcome = AuditOutcome.Success
        }, admin, CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(AuditActions.CustomerCreated, page.Items[0].Action);
    }

    // ---- Filters combine ----

    [Fact]
    public async Task Filters_combine_with_and()
    {
        await using var db = CreateDb();
        var customer = Guid.NewGuid();
        await AddEntry(db, AuditActions.CustomerSuspended, actor: "admin-1", customerId: customer);
        await AddEntry(db, AuditActions.CustomerSuspended, actor: "admin-2", customerId: customer);
        await AddEntry(db, AuditActions.CustomerSuspended, actor: "admin-1", customerId: Guid.NewGuid());
        var admin = Admin();

        var page = await Search(db, admin).Handle(new SearchAuditLogRequestDto
        {
            ActorUserId = "admin-1",
            CustomerId = customer
        }, admin, CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task The_date_range_is_inclusive_at_both_ends()
    {
        await using var db = CreateDb();
        var noon = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        await AddEntry(db, AuditActions.TicketCreated, occurredAt: noon.AddDays(-1));
        await AddEntry(db, AuditActions.TicketCreated, occurredAt: noon);
        await AddEntry(db, AuditActions.TicketCreated, occurredAt: noon.AddDays(1));
        var admin = Admin();

        var page = await Search(db, admin).Handle(
            new SearchAuditLogRequestDto { From = noon.AddDays(-1), To = noon },
            admin, CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
    }

    // ---- Paging is clamped, and stable ----

    [Fact]
    public async Task An_absurd_page_size_is_clamped_rather_than_rejected()
    {
        await using var db = CreateDb();
        for (var i = 0; i < 3; i++)
            await AddEntry(db, AuditActions.TicketCreated);
        var admin = Admin();

        var page = await Search(db, admin).Handle(
            new SearchAuditLogRequestDto { PageSize = 5000 }, admin, CancellationToken.None);

        Assert.Equal(PaginatedQuery.MaxPageSize, page.PageSize);
        Assert.Equal(3, page.TotalCount);
    }

    // The plan's case table says a PageSize of 0 clamps to 1; the shared PaginatedQuery.Normalize --
    // which section 0.4 requires this slice to use -- treats it as unspecified and applies the
    // default. The shared helper wins: 0 means "the client did not say", and answering that with a
    // single row is a stranger reading of the request than answering it with a page.
    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task An_unset_or_negative_page_size_falls_back_to_the_shared_default(int pageSize)
    {
        await using var db = CreateDb();
        var admin = Admin();

        var page = await Search(db, admin).Handle(
            new SearchAuditLogRequestDto { PageSize = pageSize }, admin, CancellationToken.None);

        Assert.Equal(PaginatedQuery.DefaultPageSize, page.PageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task A_page_number_below_one_is_clamped_to_the_first_page(int pageNumber)
    {
        await using var db = CreateDb();
        await AddEntry(db, AuditActions.TicketCreated);
        var admin = Admin();

        var page = await Search(db, admin).Handle(
            new SearchAuditLogRequestDto { PageNumber = pageNumber }, admin, CancellationToken.None);

        Assert.Equal(1, page.PageNumber);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task The_newest_entry_comes_first()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        await AddEntry(db, AuditActions.TicketCreated, occurredAt: now.AddMinutes(-10));
        var newest = await AddEntry(db, AuditActions.TicketClosed, occurredAt: now);
        await AddEntry(db, AuditActions.TicketAssigned, occurredAt: now.AddMinutes(-5));
        var admin = Admin();

        var page = await Search(db, admin).Handle(
            new SearchAuditLogRequestDto(), admin, CancellationToken.None);

        Assert.Equal(newest, page.Items[0].Id);
    }

    // occurred_at is not unique -- one transaction can write several entries on the same instant --
    // so the sort carries an Id tiebreaker. What is asserted is that paging sees every row exactly
    // once, rather than a particular Guid order: the order is the mechanism, stable paging is the
    // property, and an unstable sort skips and repeats rows.
    [Fact]
    public async Task Paging_through_entries_sharing_one_timestamp_repeats_and_skips_nothing()
    {
        await using var db = CreateDb();
        var sameInstant = DateTimeOffset.UtcNow;
        var written = new List<Guid>();
        for (var i = 0; i < 5; i++)
            written.Add(await AddEntry(db, AuditActions.TicketCreated, occurredAt: sameInstant));
        var admin = Admin();

        var seen = new List<Guid>();
        for (var pageNumber = 1; pageNumber <= 5; pageNumber++)
        {
            var page = await Search(db, admin).Handle(
                new SearchAuditLogRequestDto { PageNumber = pageNumber, PageSize = 1 },
                admin, CancellationToken.None);
            seen.AddRange(page.Items.Select(item => item.Id));
        }

        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(written.OrderBy(id => id), seen.OrderBy(id => id));
    }

    // ---- The payload appears in exactly one place ----

    // before_value and after_value are the only place personal data appears in this table and are up
    // to 8 KB each. A list endpoint carrying them would turn every page of the audit log into a bulk
    // export of tax and payroll values, so the list DTO has no property to put them in -- asserted by
    // reflection, because the guarantee is the shape of the type rather than the content of one
    // response.
    [Fact]
    public void The_list_dto_has_nowhere_to_put_a_before_or_after_payload()
    {
        var propertyNames = typeof(AuditEntryDto).GetProperties().Select(property => property.Name).ToList();

        Assert.DoesNotContain("BeforeValue", propertyNames);
        Assert.DoesNotContain("AfterValue", propertyNames);

        // Nor by inheritance: if the detail DTO derived from the list DTO, then the list DTO would
        // also be a detail DTO, and the separation would rest on nobody projecting the wrong type.
        Assert.Equal(typeof(object), typeof(AuditEntryDto).BaseType);
        Assert.Equal(typeof(object), typeof(AuditEntryDetailDto).BaseType);
    }

    [Fact]
    public async Task The_detail_endpoint_returns_the_payload()
    {
        await using var db = CreateDb();
        var id = await AddEntry(db, AuditActions.CustomerUpdated,
            before: """{"LegalName":"Old"}""", after: """{"LegalName":"New"}""");
        var admin = Admin();

        var detail = await Detail(db, admin).Handle(
            new GetAuditEntryRequestDto { AuditEntryId = id }, admin, CancellationToken.None);

        Assert.Equal("""{"LegalName":"Old"}""", detail.BeforeValue);
        Assert.Equal("""{"LegalName":"New"}""", detail.AfterValue);
    }

    [Fact]
    public async Task An_unknown_entry_id_is_a_404()
    {
        await using var db = CreateDb();
        var admin = Admin();

        var exception = await Assert.ThrowsAsync<AppException>(() => Detail(db, admin).Handle(
            new GetAuditEntryRequestDto { AuditEntryId = Guid.NewGuid() }, admin, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
    }

    // ---- The filter catalogues ----

    // The screen's dropdowns come from here so there is no second copy to drift. Every value the
    // search accepts has to be offered, or a client can 422 itself with its own dropdown.
    [Fact]
    public async Task The_action_codes_endpoint_offers_exactly_what_the_search_accepts()
    {
        await using var db = CreateDb();
        var admin = Admin();

        var response = await Codes(db, admin).Handle(admin, CancellationToken.None);

        Assert.Equal(AuditActions.All.OrderBy(action => action, StringComparer.Ordinal), response.Actions);
        Assert.Equal(AuditTargets.All.OrderBy(kind => kind, StringComparer.Ordinal), response.TargetKinds);
        Assert.Equal(AuditOutcome.All.OrderBy(outcome => outcome, StringComparer.Ordinal), response.Outcomes);
        Assert.Contains(AuditActions.PermissionDenied, response.Actions);
    }

    // ---- Fixtures ----

    private static AuditDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SearchAuditLogHandler Search(AuditDbContext db, CurrentUser user) =>
        new(db, Permissions(db, user));

    private static GetAuditEntryHandler Detail(AuditDbContext db, CurrentUser user) =>
        new(db, Permissions(db, user));

    private static ListAuditActionsHandler Codes(AuditDbContext db, CurrentUser user) =>
        new(Permissions(db, user));

    // The real PermissionChecker over the real AuditActionCatalogue, writing through the real
    // AuditApi into the same context under test. A double would let a denial "succeed" without ever
    // proving the entry can be stored, and the denial row is the part that matters.
    private static PermissionChecker Permissions(AuditDbContext db, CurrentUser user) => new(
        [new AuditActionCatalogue()], AuditApiFor(db, user), NullLogger<PermissionChecker>.Instance);

    // AuditApi resolves the actor from the scoped CurrentUser rather than trusting the caller to
    // supply it, so the provider has to hand back the same principal the handler is given.
    private static AuditApi AuditApiFor(AuditDbContext db, CurrentUser user)
    {
        var services = new ServiceCollection();
        services.AddSingleton(user);
        return new AuditApi(
            db,
            new NoOpRequestTransaction(),
            new HttpContextAccessor(),
            services.BuildServiceProvider(),
            NullLogger<AuditApi>.Instance);
    }

    private static CurrentUser Admin() => new("admin-1", UserRole.AccountantAdmin);

    // CustomerAdmin and Employee are Customer-scoped roles: CurrentUserFactory rejects such a
    // principal without a customer_id claim, so giving them one keeps the fixture honest.
    private static CurrentUser User(UserRole role) =>
        role is UserRole.CustomerAdmin or UserRole.Employee
            ? new CurrentUser($"user-{role}", role, Guid.NewGuid())
            : new CurrentUser($"user-{role}", role);

    private static async Task<Guid> AddEntry(
        AuditDbContext db,
        string action,
        string actor = "admin-1",
        string targetKind = AuditTargets.None,
        Guid? customerId = null,
        DateTimeOffset? occurredAt = null,
        string? before = null,
        string? after = null)
    {
        var record = new AuditRecord
        {
            Id = Guid.NewGuid(),
            ActorUserId = actor,
            ActorRole = UserRole.AccountantAdmin.ToString(),
            CustomerId = customerId,
            Action = action,
            TargetKind = targetKind,
            TargetId = string.Empty,
            Outcome = AuditOutcome.Success,
            BeforeValue = before,
            AfterValue = after,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            SourceIp = "127.0.0.1",
            UserAgent = "tests"
        };
        db.AuditEntries.Add(record);
        await db.SaveChangesAsync();
        return record.Id;
    }
}
