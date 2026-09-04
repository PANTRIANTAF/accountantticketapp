using AccountantApp.Api.Slices.Tickets.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Tickets.Infrastructure.Configurations;

public sealed class FieldValueConfiguration : IEntityTypeConfiguration<FieldValue>
{
    public void Configure(EntityTypeBuilder<FieldValue> builder)
    {
        builder.ToTable("field_values");
        builder.HasKey(value => value.Id);

        builder.Property(value => value.Id).HasColumnName("id");
        builder.Property(value => value.TicketRevisionId)
            .HasColumnName("ticket_revision_id").IsRequired();
        builder.Property(value => value.FieldKey)
            .HasColumnName("field_key").HasMaxLength(100).IsRequired();

        builder.Property(value => value.ValueText).HasColumnName("value_text");

        // HasPrecision(18, 4), matching NUMERIC(18,4). Not double, not float: MoneyAmount is money,
        // and a binary float cannot represent 0.10.
        builder.Property(value => value.ValueNumber)
            .HasColumnName("value_number").HasPrecision(18, 4);

        builder.Property(value => value.ValueDate).HasColumnName("value_date").HasColumnType("date");
        builder.Property(value => value.ValueDateTo)
            .HasColumnName("value_date_to").HasColumnType("date");
        builder.Property(value => value.ValueBoolean).HasColumnName("value_boolean");
        builder.Property(value => value.ValueDocumentId).HasColumnName("value_document_id");

        builder.Property(value => value.IsCarriedForward)
            .HasColumnName("is_carried_forward").IsRequired();
        builder.Property(value => value.CreatedAt).HasColumnName("created_at");

        builder.HasMany(value => value.Verifications)
            .WithOne()
            .HasForeignKey(verification => verification.FieldValueId)
            .OnDelete(DeleteBehavior.Restrict);

        // One answer per field within one revision. Without it a correction that writes a second row
        // for the same key produces two answers and every read picks whichever comes back first.
        builder.HasIndex(value => new { value.TicketRevisionId, value.FieldKey })
            .IsUnique()
            .HasDatabaseName("uq_field_values_revision_key");
    }
}
