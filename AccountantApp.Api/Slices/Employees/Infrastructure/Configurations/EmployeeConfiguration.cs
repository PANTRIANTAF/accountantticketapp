using AccountantApp.Api.Slices.Employees.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Employees.Infrastructure.Configurations;

/// <summary>
/// Entities are PascalCase, columns are snake_case, and there is no automatic conversion configured
/// anywhere in this application. Every property needs an explicit HasColumnName, or the first query
/// that touches it fails with 42703: column e.FamilyName does not exist -- at runtime, on one code
/// path, not at startup.
/// </summary>
public sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("employees");
        builder.HasKey(employee => employee.Id);

        builder.Property(employee => employee.Id).HasColumnName("id");
        builder.Property(employee => employee.CustomerId).HasColumnName("customer_id").IsRequired();
        builder.Property(employee => employee.GivenName)
            .HasColumnName("given_name").HasMaxLength(100).IsRequired();
        builder.Property(employee => employee.FamilyName)
            .HasColumnName("family_name").HasMaxLength(100).IsRequired();

        // Nullable, and therefore no IsRequired(): 01-DomainModel.md section 2 says the work email
        // "may be absent for an accountless Employee".
        builder.Property(employee => employee.WorkEmail)
            .HasColumnName("work_email").HasMaxLength(320);
        builder.Property(employee => employee.NormalizedWorkEmail)
            .HasColumnName("normalized_work_email").HasMaxLength(320);

        builder.Property(employee => employee.UserAccountId).HasColumnName("user_account_id");

        builder.Property(employee => employee.TaxIdentificationNumber)
            .HasColumnName("tax_identification_number").HasMaxLength(50);
        builder.Property(employee => employee.SocialSecurityNumber)
            .HasColumnName("social_security_number").HasMaxLength(50);
        builder.Property(employee => employee.JobTitle)
            .HasColumnName("job_title").HasMaxLength(200);
        builder.Property(employee => employee.ContactPhone)
            .HasColumnName("contact_phone").HasMaxLength(50);

        builder.Property(employee => employee.EmploymentStartDate)
            .HasColumnName("employment_start_date").HasColumnType("date").IsRequired();
        builder.Property(employee => employee.EmploymentEndDate)
            .HasColumnName("employment_end_date").HasColumnType("date");

        builder.Property(employee => employee.Status)
            .HasColumnName("status").HasMaxLength(20).IsRequired();

        builder.Property(employee => employee.CreatedAt).HasColumnName("created_at");
        builder.Property(employee => employee.UpdatedAt).HasColumnName("updated_at");
        builder.Property(employee => employee.DepartedAt).HasColumnName("departed_at");

        // Computed properties, not columns. Without these EF tries to map HasAccount and IsActive
        // to has_account and is_active, which do not exist.
        builder.Ignore(employee => employee.HasAccount);
        builder.Ignore(employee => employee.IsActive);

        // Declared so EF's model matches the database. The SQL script is what CREATEs them; these
        // exist for model-consistency checks, not for migration generation.
        builder.HasIndex(employee => new
        {
            employee.CustomerId, employee.FamilyName, employee.GivenName, employee.Id
        }).HasDatabaseName("idx_employees_customer_name");

        builder.HasIndex(employee => new { employee.CustomerId, employee.NormalizedWorkEmail })
            .IsUnique()
            .HasFilter("normalized_work_email IS NOT NULL")
            .HasDatabaseName("uq_employees_customer_email");

        builder.HasIndex(employee => employee.UserAccountId)
            .IsUnique()
            .HasFilter("user_account_id IS NOT NULL")
            .HasDatabaseName("uq_employees_user_account");
    }
}
