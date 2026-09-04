using System.Text.Json;
using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Audit.Infrastructure;
using AccountantApp.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccountantApp.Tests.Audit;

public sealed class AuditApiTests
{
    // Redaction matched property names exactly, which is the wrong test: the properties an audit
    // entry carries are named after what changed, so the qualified spelling is the common one.
    // "PasswordHash" was on the deny-list and "NewPasswordHash" was not, so a password change
    // wrote the hash into a table nothing ever purges.
    [Fact]
    public async Task Credential_properties_are_redacted_however_they_are_qualified()
    {
        await using var db = CreateDb();
        var api = CreateApi(db);

        await api.LogAsync(new AuditEntry(
            AuditActions.PasswordChanged,
            AuditTargets.UserAccount,
            "user-1",
            After: new
            {
                NewPasswordHash = "$2a$12$secret",
                AccessToken = "abc",
                RefreshTokenHash = "def",
                PasswordSalt = "ghi",
                Email = "someone@example.com"
            }), CancellationToken.None);

        var after = await SingleAfterValue(db);
        Assert.DoesNotContain("$2a$12$secret", after, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", after, StringComparison.Ordinal);
        Assert.DoesNotContain("def", after, StringComparison.Ordinal);
        Assert.DoesNotContain("ghi", after, StringComparison.Ordinal);
        Assert.Contains("someone@example.com", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redaction_reaches_nested_objects_and_arrays()
    {
        await using var db = CreateDb();
        var api = CreateApi(db);

        await api.LogAsync(new AuditEntry(
            AuditActions.AccountInvited,
            AuditTargets.UserAccount,
            "user-1",
            After: new { Invitations = new[] { new { Email = "a@example.com", InvitationToken = "tok-1" } } }),
            CancellationToken.None);

        var after = await SingleAfterValue(db);
        Assert.DoesNotContain("tok-1", after, StringComparison.Ordinal);
        Assert.Contains("a@example.com", after, StringComparison.Ordinal);
    }

    // An audit write is a side effect of some other operation. A payload the serialiser cannot
    // handle must not fail the operation being audited, and the row must still say what happened.
    [Fact]
    public async Task An_unserialisable_payload_still_writes_a_row()
    {
        await using var db = CreateDb();
        var api = CreateApi(db);
        var cyclic = new SelfReferencing();
        cyclic.Self = cyclic;

        await api.LogAsync(new AuditEntry(
            AuditActions.CustomerUpdated, AuditTargets.Customer, "customer-1", After: cyclic),
            CancellationToken.None);

        var record = await db.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.CustomerUpdated, record.Action);
        Assert.Contains("unserialisable", record.AfterValue!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("Denied ")]
    [InlineData("Rejected")]
    public async Task An_outcome_outside_the_catalogue_is_rejected(string outcome)
    {
        await using var db = CreateDb();
        var api = CreateApi(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.LogAsync(
            new AuditEntry(AuditActions.CustomerUpdated, AuditTargets.Customer, "customer-1", Outcome: outcome),
            CancellationToken.None));
        Assert.Empty(await db.AuditEntries.ToListAsync());
    }

    [Theory]
    [InlineData(AuditOutcome.Success)]
    [InlineData(AuditOutcome.Denied)]
    [InlineData(AuditOutcome.Failure)]
    public async Task Every_catalogued_outcome_is_accepted(string outcome)
    {
        await using var db = CreateDb();
        var api = CreateApi(db);

        await api.LogAsync(
            new AuditEntry(AuditActions.CustomerUpdated, AuditTargets.Customer, "customer-1", Outcome: outcome),
            CancellationToken.None);

        Assert.Equal(outcome, (await db.AuditEntries.SingleAsync()).Outcome);
    }

    private static AuditDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // AuditApi resolves CurrentUser from the container rather than taking it as a constructor
    // parameter, so the test has to supply a container holding one.
    private static AuditApi CreateApi(AuditDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new CurrentUser("accountant", UserRole.AccountantAdmin));
        return new AuditApi(
            db,
            new NoOpRequestTransaction(),
            new HttpContextAccessor(),
            services.BuildServiceProvider(),
            NullLogger<AuditApi>.Instance);
    }

    private static async Task<string> SingleAfterValue(AuditDbContext db)
    {
        var record = await db.AuditEntries.SingleAsync();
        // Parsed, not just string-matched, so a redaction that produced malformed JSON would fail
        // here rather than silently satisfying a Contains assertion.
        using var _ = JsonDocument.Parse(record.AfterValue!);
        return record.AfterValue!;
    }

    private sealed class SelfReferencing
    {
        public SelfReferencing? Self { get; set; }
    }
}
