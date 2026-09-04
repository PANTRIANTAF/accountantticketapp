using AccountantApp.Api.Slices.TicketTypes.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.TicketTypes.Infrastructure.Configurations;

public class TicketTypeConfiguration : IEntityTypeConfiguration<TicketType>
{
    public void Configure(EntityTypeBuilder<TicketType> builder)
    {
        builder.ToTable("ticket_types");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
        builder.Property(t => t.DisplayName).HasColumnName("display_name").HasMaxLength(255).IsRequired();
        builder.Property(t => t.Description).HasColumnName("description").IsRequired();
        builder.Property(t => t.Category).HasColumnName("category").HasMaxLength(100).IsRequired();
        builder.Property(t => t.AllowEmployeeToOpen).HasColumnName("allow_employee_to_open");
        builder.Property(t => t.AllowSubjectOtherThanCreator).HasColumnName("allow_subject_other_than_creator");
        builder.Property(t => t.IsActive).HasColumnName("is_active");
        builder.Property(t => t.VersionNumber).HasColumnName("version_number");
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(t => t.Code).IsUnique();
        builder.HasIndex(t => t.IsActive);
    }
}
