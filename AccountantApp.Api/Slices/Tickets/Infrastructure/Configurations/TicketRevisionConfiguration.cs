using AccountantApp.Api.Slices.Tickets.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Tickets.Infrastructure.Configurations;

public sealed class TicketRevisionConfiguration : IEntityTypeConfiguration<TicketRevision>
{
    public void Configure(EntityTypeBuilder<TicketRevision> builder)
    {
        builder.ToTable("ticket_revisions");
        builder.HasKey(revision => revision.Id);

        builder.Property(revision => revision.Id).HasColumnName("id");
        builder.Property(revision => revision.TicketId).HasColumnName("ticket_id").IsRequired();
        builder.Property(revision => revision.SequenceNumber)
            .HasColumnName("sequence_number").IsRequired();
        builder.Property(revision => revision.SubmittedByUserAccountId)
            .HasColumnName("submitted_by_user_account_id").IsRequired();
        builder.Property(revision => revision.SubmittedAt).HasColumnName("submitted_at");
        builder.Property(revision => revision.Note).HasColumnName("note").HasMaxLength(2000);

        builder.HasMany(revision => revision.FieldValues)
            .WithOne()
            .HasForeignKey(value => value.TicketRevisionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Two concurrent corrections cannot both become revision 2. One of them gets 23505, which the
        // handler maps to 409 -- not a 500.
        builder.HasIndex(revision => new { revision.TicketId, revision.SequenceNumber })
            .IsUnique()
            .HasDatabaseName("uq_ticket_revisions_sequence");
    }
}
