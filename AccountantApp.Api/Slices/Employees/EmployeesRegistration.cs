using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Employees.Application.Handlers;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Employees;

public static class EmployeesRegistration
{
    public static IServiceCollection AddEmployeesSlice(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // The (serviceProvider, options) overload with the SHARED RequestConnection. The plain
        // options => options.UseNpgsql(connectionString) overload compiles, passes every single-slice test,
        // and silently gives this slice its OWN connection -- at which point the composite onboarding
        // operation is three transactions instead of one and a failure at step 3 leaves a Customer behind
        // with nothing failing anywhere. That is the most damaging mistake available in this file, because it
        // defeats the entire reason this slice owns /api/customers/onboard.
        //
        // Never AddScoped<EmployeesDbContext>() either: that bypasses the options pipeline and the context
        // gets no provider.
        services.AddDbContext<EmployeesDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<RequestConnection>().Connection));

        // As IActionCatalogue, not the concrete type. PermissionChecker takes IEnumerable<IActionCatalogue>;
        // a concrete registration is never seen, every action in it is absent, and every endpoint in this
        // slice returns 403.
        services.AddSingleton<IActionCatalogue, EmployeesActionCatalogue>();

        // Scoped, not singleton: it holds a scoped DbContext, and a singleton would capture one context for
        // the process lifetime and fail on every request after the first connection died.
        services.AddScoped<IEmployeeApi, EmployeeApi>();

        services.AddScoped<OnboardCustomerHandler>();
        services.AddScoped<RegisterEmployeeHandler>();
        services.AddScoped<ListEmployeesHandler>();
        services.AddScoped<GetEmployeeHandler>();
        services.AddScoped<UpdateEmployeeHandler>();
        services.AddScoped<UpdateOwnContactHandler>();
        services.AddScoped<InviteEmployeeHandler>();
        services.AddScoped<SetEmployeeRoleHandler>();
        services.AddScoped<DepartEmployeeHandler>();
        services.AddScoped<ReinstateEmployeeHandler>();
        services.AddScoped<ChangeEmployeeLoginEmailHandler>();
        services.AddScoped<SuspendEmployeeAccountHandler>();
        services.AddScoped<ReactivateEmployeeAccountHandler>();

        return services;
    }
}
