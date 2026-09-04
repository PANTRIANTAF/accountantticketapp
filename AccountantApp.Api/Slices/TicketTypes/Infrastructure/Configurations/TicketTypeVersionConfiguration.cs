using AccountantApp.Api.Slices.TicketTypes.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.TicketTypes.Infrastructure.Configurations;

public class TicketTypeVersionConfiguration : IEntityTypeConfiguration<TicketTypeVersion>
{
    public void Configure(EntityTypeBuilder<TicketTypeVersion> builder)
    {
        builder.ToTable("ticket_type_versions");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Id).HasColumnName("id");
        builder.Property(v => v.TicketTypeId).HasColumnName("ticket_type_id").IsRequired();
        builder.Property(v => v.VersionNumber).HasColumnName("version_number").IsRequired();
        builder.Property(v => v.CreatedAt).HasColumnName("created_at");

        builder.HasOne(v => v.TicketType)
            .WithMany(t => t.Versions)
            .HasForeignKey(v => v.TicketTypeId);

        builder.HasIndex(v => new { v.TicketTypeId, v.VersionNumber }).IsUnique();
    }
}
