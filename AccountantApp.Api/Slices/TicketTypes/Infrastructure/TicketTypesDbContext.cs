using AccountantApp.Api.Slices.TicketTypes.Core;
using AccountantApp.Api.Slices.TicketTypes.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.TicketTypes.Infrastructure;

public class TicketTypesDbContext : DbContext
{
    public DbSet<TicketType> TicketTypes => Set<TicketType>();
    public DbSet<TicketTypeVersion> TicketTypeVersions => Set<TicketTypeVersion>();
    public DbSet<FieldDescriptor> FieldDescriptors => Set<FieldDescriptor>();

    public TicketTypesDbContext(DbContextOptions<TicketTypesDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new TicketTypeConfiguration());
        builder.ApplyConfiguration(new TicketTypeVersionConfiguration());
        builder.ApplyConfiguration(new FieldDescriptorConfiguration());
    }
}
