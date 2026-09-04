using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Notifications;
using AccountantApp.Api.Slices.Notifications.Application.Handlers;
using AccountantApp.Api.Slices.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AccountantApp.Tests.Notifications;

// Both bugs this file pins were invisible to the compiler and to every other test in the suite,
// because nothing resolved a NotificationsDbContext outside a live request. They are registration
// mistakes, so they are tested at the registration, without a database: inspecting which connection
// a context was handed does not require opening it.
public sealed class NotificationsRegistrationTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Database=accountant_app;Username=postgres;Password=postgres";

    // The slice read ConnectionStrings:DefaultConnection. Every other reader in the solution --
    // Program.cs and RequestConnection -- uses ConnectionStrings:Default, which is the only key the
    // appsettings files define. GetConnectionString returned null, so UseNpgsql(null) threw on first
    // use and every notification endpoint answered 500 while the drainer died on its first tick.
    [Fact]
    public async Task The_slice_reads_the_connection_string_key_the_rest_of_the_solution_writes()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        Assert.False(string.IsNullOrEmpty(db.Database.GetDbConnection().ConnectionString));
    }

    // NotificationApi.NotifyAsync calls IRequestTransaction.EnlistAsync so a notification commits or
    // rolls back with the ticket change that raised it. EnlistAsync hands this context the caller's
    // DbTransaction, and a transaction can only be handed to the connection that opened it -- so a
    // context on its own connection cannot enlist, and the atomicity guarantee silently became a
    // throw. Identity to the scope's RequestConnection is the thing that makes enlisting possible.
    [Fact]
    public async Task The_context_shares_the_requests_connection_so_it_can_enlist()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var requestConnection = scope.ServiceProvider.GetRequiredService<RequestConnection>();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        Assert.Same(requestConnection.Connection, db.Database.GetDbConnection());
    }

    [Fact]
    public async Task Separate_scopes_get_separate_connections_so_the_drainer_is_isolated()
    {
        await using var provider = BuildProvider();
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.GetDbConnection(),
            second.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.GetDbConnection());
    }

    // Not one of the four handlers was registered. Minimal APIs bind a complex endpoint parameter
    // from DI only when IServiceProviderIsService says it is a service, and otherwise infer it as
    // the request body -- which throws when the endpoint data sources are enumerated to build the
    // routing matcher. All data sources build together, so this took down every route in the
    // application, including the three slices that had been working. This asserts the exact
    // predicate the binder uses, not merely that resolution happens to succeed.
    [Theory]
    [InlineData(typeof(ListMyNotificationsHandler))]
    [InlineData(typeof(GetUnreadCountHandler))]
    [InlineData(typeof(MarkNotificationsReadHandler))]
    [InlineData(typeof(MarkAllNotificationsReadHandler))]
    public async Task Every_endpoint_handler_is_a_service_the_minimal_api_binder_can_see(Type handlerType)
    {
        await using var provider = BuildProvider();

        var isService = provider.GetRequiredService<IServiceProviderIsService>();

        Assert.True(
            isService.IsService(handlerType),
            $"{handlerType.Name} is not registered, so its endpoint parameter would be inferred as the request body.");
    }

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                // Left off so the test does not start the drainer's BackgroundService.
                ["Notifications:Email:Enabled"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<RequestConnection>();
        services.AddScoped<IRequestTransaction, RequestTransaction>();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNotificationsSlice(configuration);
        return services.BuildServiceProvider();
    }
}
