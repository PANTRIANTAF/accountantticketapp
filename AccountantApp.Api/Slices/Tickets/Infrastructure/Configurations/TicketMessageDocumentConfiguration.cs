using AccountantApp.Api.Slices.Tickets.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Tickets.Infrastructure.Configurations;

public sealed class TicketMessageDocumentConfiguration : IEntityTypeConfiguration<TicketMessageDocument>
{
    public void Configure(EntityTypeBuilder<TicketMessageDocument> builder)
    {
        builder.ToTable("ticket_message_documents");

        // Composite key, matching PRIMARY KEY (ticket_message_id, document_id). A surrogate id would
        // let the same document be linked to the same message twice.
        builder.HasKey(link => new { link.TicketMessageId, link.DocumentId });

        builder.Property(link => link.TicketMessageId)
            .HasColumnName("ticket_message_id").IsRequired();

        // No FK: documents is another slice's table. And a row here is NOT an authorization fact --
        // documents.ticket_id is the anchor, so section 0.3 step 5 still runs.
        builder.Property(link => link.DocumentId).HasColumnName("document_id").IsRequired();
    }
}
