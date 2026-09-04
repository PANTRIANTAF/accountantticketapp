using AccountantApp.Api.Slices.TicketTypes.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.TicketTypes.Infrastructure.Configurations;

public class FieldDescriptorConfiguration : IEntityTypeConfiguration<FieldDescriptor>
{
    public void Configure(EntityTypeBuilder<FieldDescriptor> builder)
    {
        builder.ToTable("field_descriptors");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Id).HasColumnName("id");
        builder.Property(f => f.TicketTypeVersionId).HasColumnName("ticket_type_version_id");
        builder.Property(f => f.Key).HasColumnName("key").HasMaxLength(100).IsRequired();
        builder.Property(f => f.Label).HasColumnName("label").HasMaxLength(255).IsRequired();
        builder.Property(f => f.HelpText).HasColumnName("help_text").IsRequired();
        builder.Property(f => f.DataType).HasColumnName("data_type").HasMaxLength(50).IsRequired();
        builder.Property(f => f.DisplayOrder).HasColumnName("display_order");
        builder.Property(f => f.GroupName).HasColumnName("group_name").HasMaxLength(100).IsRequired();
        builder.Property(f => f.IsRequired).HasColumnName("is_required");
        builder.Property(f => f.IsVisibleToCustomer).HasColumnName("is_visible_to_customer");
        builder.Property(f => f.ChoiceOptions).HasColumnName("choice_options").IsRequired();
        builder.Property(f => f.MinLength).HasColumnName("min_length");
        builder.Property(f => f.MaxLength).HasColumnName("max_length");
        builder.Property(f => f.MinValue).HasColumnName("min_value").HasPrecision(18, 4);
        builder.Property(f => f.MaxValue).HasColumnName("max_value").HasPrecision(18, 4);
        builder.Property(f => f.EarliestDate).HasColumnName("earliest_date");
        builder.Property(f => f.LatestDate).HasColumnName("latest_date");
        builder.Property(f => f.RegexPattern).HasColumnName("regex_pattern").HasMaxLength(500).IsRequired();
        builder.Property(f => f.AllowedFileTypes).HasColumnName("allowed_file_types").HasMaxLength(500).IsRequired();
        builder.Property(f => f.MaxFileSizeBytes).HasColumnName("max_file_size_bytes");
        builder.Property(f => f.ConditionalVisibilityFieldKey).HasColumnName("conditional_visibility_field_key").HasMaxLength(100).IsRequired();
        builder.Property(f => f.ConditionalVisibilityValue).HasColumnName("conditional_visibility_value").HasMaxLength(500).IsRequired();
        builder.Property(f => f.CreatedAt).HasColumnName("created_at");

        builder.HasOne(f => f.TicketTypeVersion)
            .WithMany(v => v.FieldDescriptors)
            .HasForeignKey(f => f.TicketTypeVersionId);

        builder.HasIndex(f => f.TicketTypeVersionId);
        builder.HasIndex(f => new { f.TicketTypeVersionId, f.Key }).IsUnique();
    }
}
