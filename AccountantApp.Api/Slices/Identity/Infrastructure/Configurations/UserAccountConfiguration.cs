using AccountantApp.Api.Slices.Identity.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Identity.Infrastructure.Configurations;

// Entities are PascalCase, columns are snake_case, and there is no automatic conversion configured.
// Every property needs an explicit HasColumnName: a missing one produces
// "42703: column u.DisplayName does not exist" at runtime, only on the path that touches it.
public sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.ToTable("user_accounts");
        builder.HasKey(account => account.Id);

        builder.Property(account => account.Id).HasColumnName("id");
        builder.Property(account => account.LoginEmail)
            .HasColumnName("login_email").HasMaxLength(320).IsRequired();
        builder.Property(account => account.NormalizedLoginEmail)
            .HasColumnName("normalized_login_email").HasMaxLength(320).IsRequired();

        // No IsRequired(): an Invited account has no credential, matching the nullable column.
        builder.Property(account => account.PasswordHash)
            .HasColumnName("password_hash").HasMaxLength(500);

        builder.Property(account => account.DisplayName)
            .HasColumnName("display_name").HasMaxLength(200).IsRequired();

        // HasConversion<string>() so the column stays readable text rather than an ordinal.
        builder.Property(account => account.Role)
            .HasColumnName("role").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(account => account.EmployeeId).HasColumnName("employee_id");
        builder.Property(account => account.CustomerId).HasColumnName("customer_id");

        builder.Property(account => account.Status)
            .HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(account => account.MustChangePassword)
            .HasColumnName("must_change_password").IsRequired();
        builder.Property(account => account.EmailConfirmedAt).HasColumnName("email_confirmed_at");
        builder.Property(account => account.FailedLoginCount)
            .HasColumnName("failed_login_count").IsRequired();
        builder.Property(account => account.LockoutExpiresAt).HasColumnName("lockout_expires_at");
        builder.Property(account => account.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(account => account.LastLoginAt).HasColumnName("last_login_at");
        builder.Property(account => account.LastPasswordChangeAt)
            .HasColumnName("last_password_change_at");

        // Computed properties, not columns.
        builder.Ignore(account => account.IsAccountant);

        builder.HasIndex(account => account.NormalizedLoginEmail)
            .IsUnique().HasDatabaseName("uq_user_accounts_normalized_email");
    }
}
