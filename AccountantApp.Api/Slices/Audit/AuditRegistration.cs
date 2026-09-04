using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Audit.Application.Handlers;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Audit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Audit;

public static class AuditRegistration
{
    public static IServiceCollection AddAuditSlice(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<AuditDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<RequestConnection>().Connection));
        services.AddScoped<IAuditApi, AuditApi>();
        services.AddSingleton<IActionCatalogue, AuditActionCatalogue>();

        // Minimal APIs bind a complex endpoint parameter from DI only when IServiceProviderIsService
        // reports it as a service, and otherwise infer it as the request body -- which throws while
        // routing builds its matcher, taking every other slice's routes down with it. Registering
        // the handlers is what makes the endpoint signatures in AuditEndpoints bind.
        services.AddScoped<SearchAuditLogHandler>();
        services.AddScoped<GetAuditEntryHandler>();
        services.AddScoped<ListAuditActionsHandler>();

        return services;
    }
}