using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Tickets.Application.Handlers;
using AccountantApp.Api.Slices.Tickets.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AccountantApp.Api.Slices.Tickets;

/// <summary>
/// Plan §7.1. The DbContext, the action catalogue, the reference allocator, and twenty-one handlers.
///
/// THIS SLICE IS REGISTERED LAST (§7.3 rule 5), after all seven others. It is the only slice that depends
/// on every one of them, and a missing <c>AddDocumentsSlice</c> shows up here as an unresolvable
/// <c>IDocumentApi</c> on the first upload rather than at startup.
///
/// What is deliberately NOT here (§7.1):
///
///   - <c>IDocumentApi</c>. It belongs to <c>DocumentsRegistration</c>, and a second registration would
///     shadow it with a context THIS slice controls -- so the bytes would be written through a connection
///     the ticket transaction does not own.
///   - <c>ITicketApi</c>, or any other <c>ExternalInterfaces</c> contract. Nothing depends on this slice
///     (§0.2), and an unused contract invites the <c>Documents -> Tickets</c> cycle §0.3 exists to prevent.
///
/// The due-date scanner of §9a IS here now, at the bottom, and it needs NO line in <c>Program.cs</c>:
/// it registers itself from this method, and only when <c>Tickets:DueDateScanner:Enabled</c> is true.
/// </summary>
public static class TicketsRegistration
{
    public static IServiceCollection AddTicketsSlice(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // §7.3 rule 1: the (serviceProvider, options) overload with the SHARED RequestConnection. The plain
        // options => options.UseNpgsql(connectionString) overload compiles, passes every in-memory test,
        // and silently gives this slice its own connection -- at which point IRequestTransaction's scope
        // covers the tickets rows and NOTHING ELSE. IDocumentApi.StoreAsync, IAuditApi.LogAsync and
        // INotificationApi then commit independently: an upload's bytes survive a rolled-back ticket
        // operation, and a status change commits without its audit entry. Nothing fails visibly.
        //
        // Never AddScoped<TicketsDbContext>() (rule 2): that bypasses the options pipeline entirely and the
        // context is constructed with no provider configured.
        services.AddDbContext<TicketsDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<RequestConnection>().Connection));

        // §7.3 rule 4: as IActionCatalogue, NOT as the concrete type. PermissionChecker resolves
        // IEnumerable<IActionCatalogue>, so a concrete registration is invisible to it, every action in
        // this slice is absent from the composed set, and EVERY ENDPOINT HERE RETURNS 403 -- with no
        // startup error and no failing unit test of the catalogue itself.
        services.AddSingleton<IActionCatalogue, TicketsActionCatalogue>();

        // Scoped, not transient and not singleton: it holds the scoped TicketsDbContext, because the
        // reference must be allocated on the SAME context and inside the SAME transaction as the ticket
        // insert -- otherwise a rolled-back creation still consumes a number and the sequence gains a hole
        // (§1.7).
        //
        // Registered AS THE INTERFACE, because CreateTicketHandler depends on the interface so that it can
        // be tested at all (see ITicketReferenceAllocator). A concrete-only registration here would leave
        // the handler unresolvable -- and by the minimal-API binding trap in TicketsEndpoints, an
        // unresolvable handler does not fail on /api/tickets/create alone, it fails when the
        // RequestDelegate is built and takes down EVERY route in the application.
        services.AddScoped<ITicketReferenceAllocator, TicketReferenceAllocator>();

        // §7.3 rule 6: handlers are AddTransient. Twenty-one classes for twenty-two actions --
        // PostMessageHandler serves both PostMessage and PostInternalNote through two entry points on one
        // class, because the two differ only in the message kind and the catalogue is what denies the
        // second to a Customer-side caller (§4.10 rule 3).
        services.AddTransient<CreateTicketHandler>();
        services.AddTransient<SubmitTicketHandler>();
        services.AddTransient<ListTicketsHandler>();
        services.AddTransient<GetTicketHandler>();
        services.AddTransient<ListPickupQueueHandler>();
        services.AddTransient<SubmitRevisionHandler>();
        services.AddTransient<VerifyFieldHandler>();
        services.AddTransient<SetPriorityHandler>();
        services.AddTransient<SetDueDateHandler>();
        services.AddTransient<PickupTicketHandler>();
        services.AddTransient<AssignTicketHandler>();
        services.AddTransient<AnswerTicketHandler>();
        services.AddTransient<CloseTicketHandler>();
        services.AddTransient<RequestInformationHandler>();
        services.AddTransient<ReturnToReviewHandler>();
        services.AddTransient<PostMessageHandler>();
        services.AddTransient<CancelTicketHandler>();

        // The four document handlers, registered HERE because this slice owns their authorization (§0.3).
        services.AddTransient<UploadDocumentHandler>();
        services.AddTransient<ListTicketDocumentsHandler>();
        services.AddTransient<DownloadDocumentHandler>();
        services.AddTransient<DeleteDocumentHandler>();

        // The due-date scanner (§9a), registered EXACTLY like the OutboxDrainer and for the same reason.
        //
        // §9a.2 rule 1: config-gated, off by default. Get<DueDateScannerOptions>() returns null when the
        // section is absent, so a configuration file that says nothing about this leaves Enabled false and
        // nothing is registered at all -- no options singleton, no TimeProvider, no hosted service.
        //
        // THE GATE IS LOAD-BEARING, not a convenience. EndpointRoutingTests builds the whole application
        // through this method; a scanner that called AddHostedService unconditionally would start a
        // background loop inside the test run, on a connection to a database that is not there, and would
        // do the same on every developer's F5. The same is true of `dotnet run` against an empty database.
        var scannerConfig = configuration.GetSection("Tickets:DueDateScanner").Get<DueDateScannerOptions>()
                         ?? new DueDateScannerOptions();

        if (scannerConfig.Enabled)
        {
            // TryAdd, and only inside the gate: nothing else in the application registers TimeProvider
            // today (§9a.2 rule 7 notes there is no clock abstraction here yet), so this is where it
            // enters the container -- but it must not be this slice's private choice if a later Shared
            // registration adds one, and it must not appear in the container at all when the scanner is
            // off, or the gate would stop being the only difference.
            services.TryAddSingleton(TimeProvider.System);

            services.AddSingleton(scannerConfig);
            services.AddSingleton<DueDateScanner>();
            services.AddHostedService(serviceProvider =>
                serviceProvider.GetRequiredService<DueDateScanner>());
        }

        return services;
    }
}
