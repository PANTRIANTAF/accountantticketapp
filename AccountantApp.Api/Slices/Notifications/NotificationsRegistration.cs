using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Notifications.Application;
using AccountantApp.Api.Slices.Notifications.Application.Handlers;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AccountantApp.Api.Slices.Notifications;

public static class NotificationsRegistration
{
    public static IServiceCollection AddNotificationsSlice(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Scoped onto RequestConnection, exactly like every other slice. This is not optional:
        // NotificationApi.NotifyAsync calls IRequestTransaction.EnlistAsync so a notification
        // commits or rolls back with the ticket change that caused it, and EnlistAsync hands this
        // context the caller's DbTransaction. A transaction can only be handed to the connection
        // that opened it, so a context on its own connection throws there instead of enlisting.
        // The drainer is unaffected: it opens a fresh scope per iteration, which yields a fresh
        // RequestConnection with its own physical connection and no ambient transaction.
        services.AddDbContext<NotificationsDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<RequestConnection>().Connection));

        services.AddScoped<INotificationApi, NotificationApi>();
        services.AddScoped<IEmailSender, LoggingEmailSender>();

        // Minimal APIs bind a complex endpoint parameter from DI only when
        // IServiceProviderIsService says it is a service; otherwise it is inferred as the request
        // body. These four were missing, so every route in NotificationsEndpoints had a handler
        // inferred as a body parameter -- and because all endpoint data sources are built together
        // when routing builds its matcher, that took down every other slice's routes with it.
        services.AddScoped<ListMyNotificationsHandler>();
        services.AddScoped<GetUnreadCountHandler>();
        services.AddScoped<MarkNotificationsReadHandler>();
        services.AddScoped<MarkAllNotificationsReadHandler>();

        // IRecipientDirectory is NOT registered here. This slice defines the interface and Identity
        // implements it -- the one inverted dependency in the system, because Identity already depends on
        // this slice to queue invitation and reset emails, and calling IIdentityApi from here would close
        // the cycle. In a single assembly that does not fail to compile; it becomes a constructor graph
        // that StackOverflows on the first request.
        //
        // The stub that used to sit here is gone. Do not reintroduce it "so the slice can be tested in
        // isolation": a stub that fails every address lookup lets the drainer mark each entry Skipped and
        // clear email_body, destroying invitation and reset links that cannot be regenerated.

        // Drainer, registered only if enabled
        var emailConfig = configuration.GetSection("Notifications:Email").Get<OutboxDrainerOptions>()
                       ?? new OutboxDrainerOptions();

        if (emailConfig.Enabled)
        {
            services.AddSingleton(emailConfig);
            services.AddSingleton<OutboxDrainer>();
            services.AddHostedService(sp => sp.GetRequiredService<OutboxDrainer>());
        }

        // Action catalogue fragment
        services.AddNotificationsActions();

        return services;
    }

    private static void AddNotificationsActions(this IServiceCollection services)
    {
        services.AddScoped<IActionCatalogue, NotificationsActionCatalogue>();
    }
}
