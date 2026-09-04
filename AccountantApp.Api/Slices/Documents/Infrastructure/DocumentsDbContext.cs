using AccountantApp.Api.Slices.Documents.Core;
using AccountantApp.Api.Slices.Documents.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Documents.Infrastructure;

/// <summary>
/// Maps exactly two entities, both owned by this slice. A DbSet&lt;Ticket&gt; appearing here would mean
/// two slices own one table and their migrations would fight -- and it would also be the cycle this
/// whole slice is shaped around avoiding (plan section 0.2).
///
/// The DbContextOptions&lt;DocumentsDbContext&gt; constructor is required: the registration uses the
/// (serviceProvider, options) AddDbContext overload so that this context shares the request's single
/// connection. Never AddScoped&lt;DocumentsDbContext&gt;(), which bypasses the options pipeline and
/// leaves the context with no provider.
/// </summary>
public sealed class DocumentsDbContext : DbContext
{
    public DocumentsDbContext(DbContextOptions<DocumentsDbContext> options) : base(options) { }

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentContent> DocumentContents => Set<DocumentContent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new DocumentConfiguration());
        modelBuilder.ApplyConfiguration(new DocumentContentConfiguration());
    }
}
