using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Shared.Migrations;
using AccountantApp.Api.Shared.Seeding;
using AccountantApp.Api.Slices.Audit;
using AccountantApp.Api.Slices.Customers;
using AccountantApp.Api.Slices.Documents;
using AccountantApp.Api.Slices.Employees;
using AccountantApp.Api.Slices.Identity;
using AccountantApp.Api.Slices.Notifications;
using AccountantApp.Api.Slices.Tickets;
using AccountantApp.Api.Slices.TicketTypes;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Default");
// Blank, not just missing: appsettings.json ships an empty Default so that no credential is
// baked into the image, and an unset ConnectionStrings__Default therefore arrives as "" rather
// than null. Without this check the app starts and fails on the first query instead.
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "ConnectionStrings:Default is required. Set the ConnectionStrings__Default environment variable.");

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<RequestConnection>();
builder.Services.AddScoped<IRequestTransaction, RequestTransaction>();
builder.Services.AddScoped<CurrentUser>(services =>
{
    var httpContext = services.GetRequiredService<IHttpContextAccessor>().HttpContext
        ?? throw new InvalidOperationException("No HttpContext.");
    return CurrentUserFactory.FromPrincipal(httpContext.User);
});
builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();

builder.Services.AddAuditSlice(builder.Configuration);
builder.Services.AddNotificationsSlice(builder.Configuration);
builder.Services.AddTicketTypesSlice(builder.Configuration);
builder.Services.AddCustomersSlice(builder.Configuration);
// AFTER Notifications, and the order is load-bearing: AddIdentitySlice registers the real
// IRecipientDirectory over the placeholder Notifications registers for itself, and the last
// registration of a service type is the one that resolves. Reversing these two lines gives the
// stub back, with nothing failing to say so.
builder.Services.AddIdentitySlice(builder.Configuration);
// AFTER Customers and Identity. DI does not care about order, but a missing AddCustomersSlice or
// AddIdentitySlice line surfaces here as an unresolvable ICustomerApi or IIdentityApi on the first
// request rather than at startup.
builder.Services.AddEmployeesSlice(builder.Configuration);
// Documents contributes exactly ONE line -- there is no app.MapDocumentEndpoints() below, and an empty
// one must not be created to make the two lists look symmetrical. The slice has no HTTP surface at all:
// a document's access rules come entirely from its ticket and are re-checked at the moment of download,
// but Documents may not depend on Tickets (that edge is a cycle), so Tickets registers /api/documents/*
// and performs every authorization check on those four routes. Slices/Documents/IMPLEMENTATION_PLAN.md
// §0.2, App/GeneralAppArchitecture.md §7.
builder.Services.AddDocumentsSlice(builder.Configuration);
// LAST of the eight, and the only one that must be. Tickets depends on all seven others -- TicketTypes,
// Customers, Employees, Identity, Notifications, Documents and Audit -- so this is the line where a
// missing registration above becomes a failure. It also contributes the LAST TWO catalogue entries the
// permission composer sees; the scope check below resolves IPermissionChecker, which is what turns a
// duplicated action name in TicketsActionCatalogue into a startup failure rather than a silent grant.
builder.Services.AddTicketsSlice(builder.Configuration);

var app = builder.Build();

// await using, not using. RequestTransaction implements IAsyncDisposable ONLY, and a synchronous
// `using` on a scope that has resolved one throws "type only implements IAsyncDisposable" at dispose --
// after the block body has already succeeded, so the message points at the scope and not at the service
// that caused it.
await using (var validationScope = app.Services.CreateAsyncScope())
{
    _ = validationScope.ServiceProvider.GetRequiredService<IPermissionChecker>();
}

await SqlMigrationRunner.RunAsync(connectionString, app.Environment.ContentRootPath);

// AFTER migrations -- the seeder queries user_accounts, which the Identity migration creates. Its own
// scope, because there is no request: RequestConnection and the DbContexts are scoped, and resolving
// them from the root provider would keep one connection alive for the life of the process.
await using (var seedScope = app.Services.CreateAsyncScope())
{
    await DatabaseSeeder.SeedAsync(seedScope.ServiceProvider);
}

// First middleware, before anything that reads the caller's address (04-Infrastructure.md
// section 3). Caddy terminates TLS, so without this every audit row records the proxy's address
// as the source IP and every request looks like plain HTTP to the app.
//
// The proxy list is an explicit allow-list and there is deliberately no fallback that trusts the
// header unconditionally: X-Forwarded-For is a request header like any other, so honouring it
// from an unknown sender lets a client write its own source IP into the audit log — the one
// column an attacker most wants to control. Empty allow-list means no forwarded header is
// honoured and the real socket address is used, which is wrong but not forgeable.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
// KnownIPNetworks, not the KnownNetworks that 04-Infrastructure.md section 3 shows: that
// property is obsolete on .NET 10 (ASPDEPR005) and takes the deprecated
// Microsoft.AspNetCore.HttpOverrides.IPNetwork rather than System.Net.IPNetwork.
// Both default to loopback, so both need clearing before the allow-list is built.
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
foreach (var network in app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    forwardedHeaders.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
foreach (var proxy in app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    forwardedHeaders.KnownProxies.Add(IPAddress.Parse(proxy));
if (forwardedHeaders.KnownIPNetworks.Count == 0 && forwardedHeaders.KnownProxies.Count == 0)
{
    app.Logger.LogWarning(
        "No ForwardedHeaders:KnownNetworks or ForwardedHeaders:KnownProxies configured. " +
        "X-Forwarded-For will be ignored and audit rows will record the proxy's address. " +
        "Behind Caddy, set ForwardedHeaders__KnownNetworks__0 to the compose network subnet.");
}

app.UseForwardedHeaders(forwardedHeaders);
app.UseMiddleware<AppExceptionMiddleware>();

// A 404 or 405 produced by routing rather than by a handler still has to be ProblemDetails,
// not an empty body: everything under /api is the API (04-Infrastructure.md section 1).
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    await response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = response.StatusCode,
        Title = ReasonPhrases.GetReasonPhrase(response.StatusCode),
        Extensions = { ["traceId"] = context.HttpContext.TraceIdentifier }
    });
});

// Unconditional now. There is deliberately NO app.UseAuthorization(): authorization in this codebase
// is IPermissionChecker.RequireAsync called as the first statement of a handler, and authentication is
// enforced by CurrentUser's factory throwing 401 when there is no principal. An endpoint that takes a
// CurrentUser is authenticated by construction; one that does not is anonymous by construction.
app.UseAuthentication();

// AFTER UseAuthentication -- it reads context.User, which does not exist before the cookie has been
// decoded. Placed earlier it sees an unauthenticated principal on every request and lets everything
// through, so a user who must change their password is never stopped.
app.UseMiddleware<MustChangePasswordMiddleware>();

app.MapAuditEndpoints();
app.MapTicketTypesEndpoints();
app.MapCustomersEndpoints();
app.MapNotificationsEndpoints();
app.MapIdentityEndpoints();
// Registers /api/employees/* AND /api/customers/onboard -- the latter deliberately, see
// EmployeesEndpoints.MapOnboardingRoute.
app.MapEmployeesEndpoints();
// Singular "Ticket", and it registers TWO route groups: /api/tickets/* and /api/documents/*. The second
// is not a mistake and must not be moved into a MapDocumentsEndpoints() -- see the AddDocumentsSlice
// comment above. This is the only MapPost in the application that accepts multipart.
app.MapTicketEndpoints();

// ---------------------------------------------------------------------------------------------
// The SPA. 04-Infrastructure.md section 1: the built React application ships inside this container
// and is served from here, so there is one origin, no CORS anywhere, and SameSite=Strict works with
// no exceptions.
//
// LAST, after every MapXxxEndpoints() call above. Registered earlier, the fallback swallows API
// routes.
//
// In development wwwroot is empty -- the SPA is the Vite dev server on port 5173, which proxies /api
// here -- so these three lines do nothing locally and everything in a container. That asymmetry is
// why they are easy to leave out and hard to notice missing: the deployed image serves a blank page
// with a 404 for index.html and no error anywhere.
// ---------------------------------------------------------------------------------------------
app.UseDefaultFiles();   // "/" -> index.html
app.UseStaticFiles();    // wwwroot/, populated from the ui build stage by the Dockerfile

// TWO fallbacks, and the /api one is not optional. MapFallbackToFile matches {*path:nonfile}, which
// includes /api/nonexistent -- so with only the file fallback an unknown API route answers 200 with
// an HTML body, and the SPA's fetch reports a JSON parse error that points nowhere near the cause.
// 04-Infrastructure.md section 1 states the requirement directly: "an unknown API route is a 404, not
// an HTML page."
//
// Route matching picks the more specific pattern, so anything under /api lands here, and the
// UseStatusCodePages handler above turns this bare 404 into ProblemDetails -- everything under /api is
// the API, including its failures.
app.MapFallback("/api/{**path}", (HttpContext context) =>
{
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    return Task.CompletedTask;
});

// Client-side routing: /customers/<id> is a route in the browser's router, not on the server, so the
// server must return the application and let it read the URL. This is also what makes the `*`
// catch-all row in the SPA's route table load-bearing -- with a 200 for every non-/api path, the
// server cannot tell /customers from /custmoers.
app.MapFallbackToFile("index.html");

app.Run();
