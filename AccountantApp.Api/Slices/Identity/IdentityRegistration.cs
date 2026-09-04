using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Identity.Application;
using AccountantApp.Api.Slices.Identity.Application.Handlers;
using AccountantApp.Api.Slices.Identity.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity;

public static class IdentityRegistration
{
    public static IServiceCollection AddIdentitySlice(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<IdentityDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<RequestConnection>().Connection));

        services.AddSingleton<IActionCatalogue, IdentityActionCatalogue>();

        // Singletons: both are stateless and thread-safe, and PasswordHashing computes its timing-defence
        // dummy hash once in its constructor -- which only pays off if there is one instance.
        services.AddSingleton<IPasswordHashing, PasswordHashing>();
        services.AddSingleton<ITokenIssuing, TokenIssuing>();

        services.Configure<IdentityLinkOptions>(options =>
            options.BaseUrl = configuration["App:BaseUrl"] ?? string.Empty);
        services.AddSingleton<TokenLinks>();

        services.AddTransient<LoginHandler>();
        services.AddTransient<LogoutHandler>();
        services.AddTransient<GetCurrentSessionHandler>();
        services.AddTransient<ChangeOwnPasswordHandler>();
        services.AddTransient<RequestPasswordResetHandler>();
        services.AddTransient<CompletePasswordResetHandler>();
        services.AddTransient<AcceptInvitationHandler>();
        services.AddTransient<InviteAccountantHandler>();
        services.AddTransient<ListAccountantsHandler>();
        services.AddTransient<SuspendAccountantHandler>();
        services.AddTransient<ReactivateAccountantHandler>();
        services.AddTransient<PromoteAccountantHandler>();
        services.AddTransient<DemoteAccountantHandler>();

        services.AddScoped<IIdentityApi, IdentityApi>();

        // The inverted dependency: Notifications declares IRecipientDirectory, this slice satisfies it.
        // This registration REPLACES the stub Notifications registered as a placeholder -- last
        // registration of a service type wins for a single resolution, and AddIdentitySlice must therefore
        // run AFTER AddNotificationsSlice in Program.cs.
        services.AddScoped<IRecipientDirectory, RecipientDirectory>();

        AddCookieAuthentication(services);
        AddDataProtection(services, configuration);

        return services;
    }

    private static void AddCookieAuthentication(IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "aa_session";
                options.Cookie.HttpOnly = true;

                // Always, NOT SameAsRequest. Caddy terminates TLS and the API sees plain HTTP behind it,
                // so SameAsRequest silently drops the Secure flag in production -- the one environment
                // where it matters. This depends on UseForwardedHeaders being configured for the proxy's
                // scheme to be visible at all.
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

                // Strict, not Lax. The SPA is served from the same origin as the API, so Strict costs
                // nothing. The one thing it breaks -- following an email link straight into an
                // authenticated page -- does not apply, because invitation and reset links land on
                // unauthenticated endpoints.
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";

                // Never set Cookie.Domain. Leaving it unset scopes the cookie to the exact host, which is
                // what a one-domain deployment wants; setting it widens the cookie to every subdomain.

                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;

                // Both overrides are MANDATORY. Without them an expired session gets a 302 to
                // /Account/Login, which does not exist, and the SPA's fetch sees a 200 with an HTML body.
                // The symptom reported is "the app randomly shows the index page inside a JSON parse
                // error", which points nowhere near here.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
    }

    private static void AddDataProtection(IServiceCollection services, IConfiguration configuration)
    {
        // By default the keys are generated per process and kept in memory, or in a user profile that does
        // not exist inside a container. Skipping this signs out every user on every deploy and every
        // container restart -- and it gets reported as "the app logs me out randomly".
        var keyPath = configuration["DataProtection:KeyPath"];
        if (string.IsNullOrWhiteSpace(keyPath))
            throw new InvalidOperationException(
                "DataProtection:KeyPath is required. Point it at a mounted volume so cookie signing keys "
                + "survive a restart.");

        // Fail startup rather than falling back to ephemeral keys. The fallback works perfectly in
        // testing and quietly fails in production, which is the worst available combination -- so create
        // the directory if it is missing, and refuse to start if it cannot be written.
        var keyDirectory = new DirectoryInfo(keyPath);
        try
        {
            keyDirectory.Create();
            var probe = Path.Combine(keyDirectory.FullName, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"DataProtection:KeyPath '{keyPath}' is not writable. Cookie signing keys cannot be "
                + "persisted, and every restart would sign out every user.", exception);
        }

        services.AddDataProtection()
            .PersistKeysToFileSystem(keyDirectory)
            // Not optional: the application name is part of the key-derivation purpose, so changing or
            // omitting it invalidates every cookie that has already been issued.
            .SetApplicationName("AccountantApp");
    }
}
