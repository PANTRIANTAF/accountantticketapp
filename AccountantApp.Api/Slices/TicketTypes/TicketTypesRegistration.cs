using AccountantApp.Api.Slices.TicketTypes.Application.Handlers;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using AccountantApp.Api.Slices.TicketTypes.Infrastructure;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.TicketTypes;

public static class TicketTypesRegistration
{
    public static IServiceCollection AddTicketTypesSlice(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<TicketTypesDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<RequestConnection>().Connection));

        services.AddSingleton<IActionCatalogue, TicketTypesActionCatalogue>();
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