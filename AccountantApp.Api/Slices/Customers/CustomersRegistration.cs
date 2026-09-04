using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Customers.Application.Handlers;
using AccountantApp.Api.Slices.Customers.ExternalInterfaces;
using AccountantApp.Api.Slices.Customers.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Customers;

public static class CustomersRegistration
{
    public static IServiceCollection AddCustomersSlice(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<CustomersDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<RequestConnection>().Connection));

        services.AddScoped<ICustomerApi, CustomerApi>();
        services.AddSingleton<IActionCatalogue, CustomersActionCatalogue>();
        services.AddScoped<CreateCustomerHandler>();
        services.AddScoped<ListCustomersHandler>();
        services.AddScoped<GetCustomerHandler>();
        services.AddScoped<GetOwnCustomerHandler>();
        services.AddScoped<UpdateCustomerContactHandler>();
        services.AddScoped<UpdateCustomerLegalHandler>();
        services.AddScoped<SuspendCustomerHandler>();
        services.AddScoped<ReactivateCustomerHandler>();
        return services;
    }
}