using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Documents.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Documents;

/// <summary>
/// TWO registrations, and that is the whole file -- no handlers, no endpoints, no action catalogue.
///
/// This slice has NO HTTP endpoints. Its four routes (/api/documents/upload, /list, /download, /delete)
/// are registered by TICKETS, because a document's access rules come entirely from its ticket and must
/// be re-checked on every request, and Documents -> Tickets would be a cycle. So this is the one slice
/// that contributes ONE line to Program.cs rather than two:
///
///     builder.Services.AddDocumentsSlice(builder.Configuration);
///
/// There is deliberately no MapDocumentEndpoints(). Do not create an empty one to make Program.cs look
/// symmetric -- an extension method that maps nothing hides the one asymmetry a reader needs to know
/// about. App/GeneralAppArchitecture.md section 7 says so as well.
///
/// The three document action names belong to TicketsActionCatalogue and must not also appear in a
/// Documents catalogue: action names are globally unique, so a duplicate is a startup failure naming
/// both slices. They are not spelled out here either, because EndpointRoutingTests reads action names
/// out of the Slices source and this slice requires none.
/// </summary>
public static class DocumentsRegistration
{
    public static IServiceCollection AddDocumentsSlice(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // The (serviceProvider, options) overload with the SHARED RequestConnection. The plain
        // options => options.UseNpgsql(connectionString) overload compiles, passes every single-slice
        // test, and silently gives this slice its OWN connection -- at which point StoreAsync's
        // EnlistAsync joins nothing and an upload commits independently of the ticket change and the
        // audit entry it was supposed to be atomic with. THE BYTES THEN SURVIVE A ROLLED-BACK TICKET
        // OPERATION, which is the one failure mode the "bytes in PostgreSQL rather than on a volume"
        // decision exists to make impossible. Nothing fails visibly.
        //
        // Never AddScoped<DocumentsDbContext>() either: that bypasses the options pipeline and the
        // context gets no provider.
        services.AddDbContext<DocumentsDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<RequestConnection>().Connection));

        // Scoped, not singleton: it holds a scoped DbContext, and a singleton would capture one context
        // for the process lifetime and fail on every request after the first connection died.
        services.AddScoped<IDocumentApi, DocumentApi>();

        return services;
    }
}
