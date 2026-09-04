using AccountantApp.Api.Slices.Customers.Core;
using AccountantApp.Api.Slices.Customers.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Customers.Infrastructure;

public sealed class CustomersDbContext : DbContext
{
    public CustomersDbContext(DbContextOptions<CustomersDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new CustomerConfiguration());
    }
}