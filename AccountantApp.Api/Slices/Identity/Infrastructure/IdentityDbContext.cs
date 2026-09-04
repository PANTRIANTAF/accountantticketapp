using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Identity.Infrastructure;

/// <summary>
/// Note the name also exists in Microsoft.AspNetCore.Identity.EntityFrameworkCore. That package is
/// deliberately NOT referenced (plan section 3.1), so there is no clash -- but if a "the type exists
/// in both" error ever appears, the fix is to remove the package reference, not to rename this class.
/// </summary>
public sealed class IdentityDbContext : DbContext
{
    // Required: registration uses AddDbContext<IdentityDbContext>, which supplies exactly this.
    // A parameterless constructor fails at resolution with a message that never mentions it.
    public IdentityDbContext(DbContextOptions<IdentityDbContext> options) : base(options) { }

    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<UserAccountToken> Tokens => Set<UserAccountToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new UserAccountConfiguration());
        builder.ApplyConfiguration(new UserAccountTokenConfiguration());
    }

    // There is deliberately no navigation from UserAccount to its tokens. Nothing needs an account
    // with its token history, and a collection navigation invites Include(u => u.Tokens) on the
    // login path -- pulling every reset token the person has ever had into memory to check a password.

    // There is also no global query filter, for status or for scope. One excluding Suspended
    // accounts would make suspend and reactivate unable to find their own target, and would make
    // LoginHandler unable to tell "no such account" from "suspended" -- which it must, to audit
    // correctly even though it reports both identically.
}
