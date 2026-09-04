using AccountantApp.Api.Slices.Employees.Core;
using AccountantApp.Api.Slices.Employees.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Employees.Infrastructure;

/// <summary>
/// Maps exactly one entity. A DbSet&lt;Customer&gt; or DbSet&lt;UserAccount&gt; appearing here would
/// mean two slices own one table, and their migrations would fight.
///
/// There is deliberately no global query filter. Customer scope is applied explicitly with
/// .WhereInCustomerScope(user) at every call site, because a global filter would (a) need to know the
/// caller's role, which means a filter reading a scoped service, and (b) HIDE the second, tighter
/// filter this slice needs -- an Employee sees their own record only, not their Customer's, and a
/// Customer-level filter makes that case look handled when it is not.
///
/// Nor is there a filter excluding Departed Employees: they stay visible to their Customer Admin
/// forever, and the departure handler has to be able to find its own target.
/// </summary>
public sealed class EmployeesDbContext : DbContext
{
    public EmployeesDbContext(DbContextOptions<EmployeesDbContext> options) : base(options) { }

    public DbSet<Employee> Employees => Set<Employee>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new EmployeeConfiguration());
    }
}
