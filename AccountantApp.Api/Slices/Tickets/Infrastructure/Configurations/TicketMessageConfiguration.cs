using AccountantApp.Api.Slices.Tickets.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Tickets.Infrastructure.Configurations;

public sealed class TicketMessageConfiguration : IEntityTypeConfiguration<TicketMessage>
{
    public void Configure(EntityTypeBuilder<TicketMessage> builder)
    {
        builder.ToTable("ticket_messages");
        builder.HasKey(message => message.Id);

        builder.Property(message => message.Id).HasColumnName("id");
        builder.Property(message => message.TicketId).HasColumnName("ticket_id").IsRequired();

        // Nullable, and therefore no IsRequired(): a SystemEvent has no human author.
        builder.Property(message => message.AuthorUserAccountId)
            .HasColumnName("author_user_account_id");

        builder.Property(message => message.Kind)
            .HasColumnName("kind").HasMaxLength(30).IsRequired();
        builder.Property(message => message.Body).HasColumnName("body").IsRequired();
        builder.Property(message => message.CreatedAt).HasColumnName("created_at");

        builder.HasMany(message => message.AttachedDocuments)
            .WithOne()
            .HasForeignKey(link => link.TicketMessageId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(message => new { message.TicketId, message.CreatedAt })
            .HasDatabaseName("idx_ticket_messages_ticket");
    }
}
