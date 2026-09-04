using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Audit;
using AccountantApp.Api.Slices.Customers;
using AccountantApp.Api.Slices.Documents;
using AccountantApp.Api.Slices.Employees;
using AccountantApp.Api.Slices.Identity;
using AccountantApp.Api.Slices.Notifications;
using AccountantApp.Api.Slices.Tickets;
using AccountantApp.Api.Slices.TicketTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AccountantApp.Tests;

// This file exists because of a bug that took the whole application down and that every other test
// in the suite was blind to.
//
// Minimal APIs bind a complex endpoint parameter from DI only when IServiceProviderIsService reports
// it as a service; otherwise the parameter is inferred as the request body. That inference fails at
// the moment the endpoint's RequestDelegate is built -- which happens when routing enumerates the
// endpoint data sources, and all data sources are enumerated together. So a single unregistered
// handler in one slice makes every route in the application throw, including slices that were
// working. The compiler cannot see it, and no handler-level unit test can either, because a unit
// test constructs the handler itself.
//
// The fix is to build the endpoints for real. Enumerating DataSources forces every RequestDelegate
// to be constructed, exactly as the first request to the application would.
public sealed class EndpointRoutingTests
{
    [Fact]
    public void Every_endpoint_in_every_slice_builds_its_request_delegate()
    {
        var app = BuildApp();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .ToList();

        Assert.NotEmpty(endpoints);
        // Every endpoint carries a delegate, which is what could not be constructed when a handler
        // was missing from DI.
        Assert.All(endpoints, endpoint => Assert.NotNull(Assert.IsType<RouteEndpoint>(endpoint).RequestDelegate));
    }

    // The route shape is locked: /api/{domain}/{action}, lowercase, kebab-case at every word
    // boundary, and no route parameters -- an identifier is not an action, so it goes in the query
    // string. A drifting route is a silent break of a client that has already shipped.
    [Fact]
    public void Every_route_matches_the_locked_shape()
    {
        var patterns = Routes(BuildApp());

        Assert.All(patterns, pattern =>
        {
            Assert.StartsWith("/api/", pattern, StringComparison.Ordinal);
            Assert.DoesNotContain('{', pattern);
            Assert.Equal(pattern.ToLowerInvariant(), pattern);
            // api / {domain} / {action}
            Assert.Equal(3, pattern.Split('/', StringSplitOptions.RemoveEmptyEntries).Length);
        });
    }

    [Fact]
    public void The_audit_slice_exposes_its_three_read_routes()
    {
        var patterns = Routes(BuildApp());

        Assert.Contains("/api/audit/search", patterns);
        Assert.Contains("/api/audit/detail", patterns);
        Assert.Contains("/api/audit/action-codes", patterns);
    }

    /// <summary>
    /// The Tickets slice's route surface, counted. Moved here from TicketsLifecycleFlowTests on
    /// 2026-09-02: that file carried its own copy of BuildApp() because this one did not register
    /// Documents or Tickets, and two builders that must mirror Program.cs drift -- the copy would keep
    /// passing while this one, the file whose whole purpose is to catch a missing registration, stayed
    /// blind to the slice with the most registrations.
    ///
    /// The counts are the point. The three tests above would all pass if MapDocumentRoutes were dropped
    /// entirely, because every route they DO see is still well formed.
    /// </summary>
    [Fact]
    public void The_tickets_slice_exposes_eighteen_ticket_routes_and_the_four_document_routes()
    {
        var patterns = Routes(BuildApp());

        Assert.Equal(18, patterns.Count(pattern =>
            pattern.StartsWith("/api/tickets/", StringComparison.Ordinal)));

        // Registered by Tickets, not by Documents, and a cycle if they were not -- a document's access
        // rules come from its ticket and must be re-checked at download. Documents contributes no routes.
        Assert.Equal(4, patterns.Count(pattern =>
            pattern.StartsWith("/api/documents/", StringComparison.Ordinal)));

        // Matrix §7 gives both of these to Nobody. Cancellation is a status, not a deletion.
        Assert.DoesNotContain("/api/tickets/reopen", patterns);
        Assert.DoesNotContain("/api/tickets/delete", patterns);
    }

    /// <summary>
    /// The upload route needs <c>DisableAntiforgery</c>, and this test is what stops the line being
    /// "tidied" away. It is the only multipart endpoint in the application, minimal-API form binding
    /// requires antiforgery validation by default, and this application registers no antiforgery services
    /// and no <c>UseAntiforgery</c> middleware -- so without it the route throws on every request. That
    /// failure is at REQUEST time, not startup, which is why it needs asserting rather than trusting.
    ///
    /// What makes disabling it safe is the auth cookie's SameSite=Strict, which a cross-site form post
    /// cannot carry. Remove that and this line becomes a real CSRF hole.
    /// </summary>
    [Fact]
    public void The_multipart_upload_route_disables_antiforgery()
    {
        var upload = ((IEndpointRouteBuilder)BuildApp()).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/api/documents/upload");

        Assert.Contains(
            upload.Metadata,
            metadata => metadata is Microsoft.AspNetCore.Antiforgery.IAntiforgeryMetadata
            {
                RequiresValidation: false,
            });
    }

    // No slice may carry authorization metadata, because the application registers no authorization
    // services or middleware at all -- authorization is IPermissionChecker inside the handler. An
    // endpoint with an authorize attribute and no middleware to satisfy it makes EndpointMiddleware
    // throw on every request to that route.
    [Fact]
    public void No_endpoint_carries_authorization_metadata_the_application_cannot_satisfy()
    {
        var app = BuildApp();

        var offenders = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .Where(endpoint => endpoint.Metadata.OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Any())
            .Select(endpoint => endpoint.DisplayName)
            .ToList();

        Assert.Empty(offenders);
    }

    // The companion to the routing bug above, and the same shape of blind spot.
    //
    // PermissionChecker.RequireAsync is `_actions.TryGetValue(action, out var roles) && roles.Contains(role)`,
    // which FAILS CLOSED: an action name that is not in the composed catalogue is denied to every role,
    // including AccountantAdmin, and the denial is logged as a PermissionDenied audit entry -- so the trail
    // says the caller lacked the permission rather than that the permission does not exist. Its constructor
    // detects duplicate and empty entries at boot, but it cannot detect a MISSING one, because it never sees
    // the string literals in the handlers.
    //
    // The compiler cannot see it either: the action is a string. A handler unit test cannot see it, because
    // those build a PermissionChecker from the catalogue the handler is being tested against. So a typo, or a
    // new handler whose catalogue entry was forgotten, ships as a 403 for everybody -- which reads exactly
    // like a deliberate authorization decision.
    //
    // Scanning the source is the only place the two sides meet. It is coarse on purpose.
    [Fact]
    public void Every_action_name_a_handler_requires_exists_in_some_catalogue()
    {
        var catalogued = Catalogued();
        var required = RequiredActions();

        // The regex is load-bearing, so a scan that matched nothing must fail rather than pass vacuously --
        // that is how this test quietly stops testing anything after a refactor renames the method.
        Assert.NotEmpty(required);

        var missing = required
            .Where(action => !catalogued.Contains(action.Action))
            .Select(action => $"{action.Action} (required by {action.File})")
            .ToList();

        Assert.Empty(missing);
    }

    // The other direction. A catalogue entry no handler asks for is dead configuration that reads as a
    // granted permission -- somebody auditing who may do what believes the operation exists, and a UI built
    // from can() renders a control for it.
    [Fact]
    public void Every_catalogued_action_is_required_by_some_handler()
    {
        var required = RequiredActions().Select(action => action.Action).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(required);
        Assert.Empty(Catalogued().Where(action => !required.Contains(action)).Order());
    }

    /// <summary>
    /// Every action name in every IActionCatalogue in the Api assembly, composed the way PermissionChecker
    /// composes them -- by reflection over the implementations rather than a hand-written list, so a new
    /// slice's catalogue is covered the moment it exists.
    /// </summary>
    private static HashSet<string> Catalogued() =>
        typeof(IActionCatalogue).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface && typeof(IActionCatalogue).IsAssignableFrom(type))
            .Select(type => (IActionCatalogue)Activator.CreateInstance(type)!)
            .SelectMany(catalogue => catalogue.Actions.Keys)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every action name passed to RequireAsync as a literal, read out of the Api project's source. Every
    /// call site in the application is a single line of the form RequireAsync(user, "Name", ct: ct); the only
    /// multi-line one is the declaration in PermissionChecker itself, which this pattern cannot match because
    /// its second argument is not a literal.
    /// </summary>
    private static List<(string Action, string File)> RequiredActions()
    {
        // Captures the second argument WHATEVER it is, rather than only a quoted one, so a call this test
        // cannot check is a failure instead of an omission.
        var pattern = new System.Text.RegularExpressions.Regex(
            @"RequireAsync\(\s*\w+\s*,\s*([^,)]+)");

        var found = Directory
            .EnumerateFiles(Path.Combine(ApiSourceRoot(), "Slices"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => pattern.Matches(StripComments(File.ReadAllText(file)))
                .Select(match => (
                    Argument: match.Groups[1].Value.Trim(),
                    File: Path.GetFileName(file))))
            .Distinct()
            .ToList();

        // A constant, a nameof, or an interpolated string would defeat the scan silently. There are none, and
        // there should be none: a literal here is what lets this test read the name at all.
        Assert.Empty(found
            .Where(match => !(match.Argument.StartsWith('"') && match.Argument.EndsWith('"')))
            .Select(match => $"{match.File}: {match.Argument}"));

        return found.Select(match => (match.Argument.Trim('"'), match.File)).ToList();
    }

    // StripComments guards the two tests above, so it gets its own. Without these, the stripper could
    // quietly start removing real code and both action-name tests would go green by scanning less.
    [Fact]
    public void The_action_scan_ignores_a_RequireAsync_written_in_a_comment()
    {
        var source = """
            // RequireAsync(user, "CommentedLineAction")
            /* RequireAsync(user, "CommentedBlockAction") */
            /// <summary>Calls RequireAsync(user, "CommentedDocAction") first.</summary>
            public async Task Handle() => await _permissions.RequireAsync(user, "RealAction", ct: ct);
            """;

        var actions = ActionArgumentsIn(source);

        Assert.Equal(["\"RealAction\""], actions);
    }

    [Fact]
    public void The_action_scan_does_not_lose_a_call_after_a_string_containing_a_slash_pair()
    {
        // A blind cut at "//" would swallow the rest of this line and miss the call entirely -- which is
        // the failure mode that matters, because it makes a missing action invisible instead of noisy.
        var source =
            """
            var url = "https://example.test/x"; await _permissions.RequireAsync(user, "AfterUrl", ct: ct);
            """;

        Assert.Equal(["\"AfterUrl\""], ActionArgumentsIn(source));
    }

    [Fact]
    public void The_action_scan_reads_through_a_raw_string_literal()
    {
        // TicketReferenceAllocator holds the one raw string literal in the Slices tree. A stripper that
        // mishandled the """ fence would treat everything after it as a string and stop scanning.
        var source =
            "private const string Sql = \"\"\"\n  SELECT 1; -- not a comment marker\n  \"\"\";\n"
            + "await _permissions.RequireAsync(user, \"AfterRawString\", ct: ct);";

        Assert.Equal(["\"AfterRawString\""], ActionArgumentsIn(source));
    }

    private static List<string> ActionArgumentsIn(string source) =>
        new System.Text.RegularExpressions.Regex(@"RequireAsync\(\s*\w+\s*,\s*([^,)]+)")
            .Matches(StripComments(source))
            .Select(match => match.Groups[1].Value.Trim())
            .ToList();

    /// <summary>
    /// Source with comments removed, so that prose ABOUT a RequireAsync call is not read as one.
    ///
    /// This is not a nicety. Without it, a doc comment explaining which actions a slice's routes require
    /// is indistinguishable from the calls themselves, so the scan reports an action no catalogue
    /// contains and this test fails -- which is exactly what happened while Documents was being built.
    /// The pressure that creates is the wrong way round: it makes a passing suite depend on documentation
    /// staying vague, and the natural fix ("stop naming the actions in comments") makes the codebase
    /// worse to satisfy a test. So the test does the work instead.
    ///
    /// Note the failure was at least in the safe direction -- a comment can only ADD to the required set,
    /// never mask a missing action -- so this made the test noisy rather than dishonest.
    ///
    /// String and char literals are tracked rather than blindly cutting at "//", because a literal
    /// containing a slash pair (a URL, a regex) would otherwise swallow the rest of its line and could
    /// hide a real call. Raw ("""), verbatim (@"") and regular strings are all handled.
    /// </summary>
    private static string StripComments(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        var index = 0;

        while (index < source.Length)
        {
            // Line comment: drop to the newline, but keep the newline so line-oriented regexes still work.
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] != '\n')
                    index++;
                continue;
            }

            // Block comment.
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/'))
                    index++;
                index = Math.Min(index + 2, source.Length);
                continue;
            }

            // Raw string literal: opened by N >= 3 quotes, closed by the first run of N quotes.
            if (source[index] == '"' && index + 2 < source.Length
                && source[index + 1] == '"' && source[index + 2] == '"')
            {
                var fenceLength = 0;
                while (index + fenceLength < source.Length && source[index + fenceLength] == '"')
                    fenceLength++;

                var fence = new string('"', fenceLength);
                output.Append(fence);
                index += fenceLength;

                var close = source.IndexOf(fence, index, StringComparison.Ordinal);
                if (close < 0)
                {
                    output.Append(source, index, source.Length - index);
                    return output.ToString();
                }

                output.Append(source, index, close - index).Append(fence);
                index = close + fenceLength;
                continue;
            }

            // Verbatim string: "" is an escaped quote; backslash is not an escape.
            if (source[index] == '@' && index + 1 < source.Length && source[index + 1] == '"')
            {
                output.Append('@').Append('"');
                index += 2;
                while (index < source.Length)
                {
                    if (source[index] == '"')
                    {
                        if (index + 1 < source.Length && source[index + 1] == '"')
                        {
                            output.Append("\"\"");
                            index += 2;
                            continue;
                        }

                        output.Append('"');
                        index++;
                        break;
                    }

                    output.Append(source[index]);
                    index++;
                }

                continue;
            }

            // Regular string or char literal.
            if (source[index] is '"' or '\'')
            {
                var quote = source[index];
                output.Append(quote);
                index++;
                while (index < source.Length && source[index] != quote)
                {
                    if (source[index] == '\\' && index + 1 < source.Length)
                    {
                        output.Append(source[index]);
                        index++;
                    }

                    output.Append(source[index]);
                    index++;
                }

                if (index < source.Length)
                {
                    output.Append(quote);
                    index++;
                }

                continue;
            }

            output.Append(source[index]);
            index++;
        }

        return output.ToString();
    }

    /// <summary>
    /// The Api project directory, found by walking up from the test binaries. A path relative to the test
    /// assembly rather than to the working directory, because the working directory differs between
    /// `dotnet test`, the IDE runner, and CI.
    /// </summary>
    private static string ApiSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "AccountantApp.Api");
            if (Directory.Exists(Path.Combine(candidate, "Slices")))
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find AccountantApp.Api above {AppContext.BaseDirectory}.");
    }

    private static List<string> Routes(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.Trim('/'))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // The one route in the application registered by a slice that does not own the thing it creates.
    // It is asserted here because "tidying" it into CustomersEndpoints -- which would create a
    // dependency cycle -- would otherwise leave every test still green.
    [Fact]
    public void Customer_onboarding_is_registered_by_the_employees_slice()
    {
        var app = BuildApp();

        var onboarding = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/api/customers/onboard");

        Assert.Contains("Employees", onboarding.Metadata
            .OfType<Microsoft.AspNetCore.Http.Metadata.ITagsMetadata>()
            .SelectMany(tags => tags.Tags));
    }

    // Mirrors Program.cs: the same Shared block and the same six slice registrations, minus the
    // startup work that needs a database. Nothing here connects -- RequestConnection is lazy and
    // building endpoints resolves no scoped service.
    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] =
                "Host=localhost;Port=5432;Database=accountant_app;Username=postgres;Password=postgres",
            // Off, so no BackgroundService is registered by the Notifications slice.
            ["Notifications:Email:Enabled"] = "false",
            // Required by the Identity slice, which refuses to register without it rather than
            // silently keeping cookie signing keys in memory. Nothing is written: no key is created
            // until a cookie is signed, and this test signs none.
            ["DataProtection:KeyPath"] =
                Path.Combine(Path.GetTempPath(), "accountant-app-routing-tests-keys")
        });

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
        builder.Services.AddIdentitySlice(builder.Configuration);
        builder.Services.AddEmployeesSlice(builder.Configuration);
        builder.Services.AddDocumentsSlice(builder.Configuration);

        // LAST, mirroring Program.cs: Tickets is the only slice that depends on all seven others. It is
        // also the slice with the most handlers to register, which makes it the likeliest source of the
        // missing-registration bug this whole file exists for.
        builder.Services.AddTicketsSlice(builder.Configuration);

        var app = builder.Build();
        app.MapAuditEndpoints();
        app.MapTicketTypesEndpoints();
        app.MapCustomersEndpoints();
        app.MapNotificationsEndpoints();
        app.MapIdentityEndpoints();
        app.MapEmployeesEndpoints();
        // Registers /api/tickets/* AND /api/documents/*. There is deliberately no MapDocumentsEndpoints:
        // Documents has no HTTP surface, and AddDocumentsSlice above contributes no routes at all.
        app.MapTicketEndpoints();
        return app;
    }
}
