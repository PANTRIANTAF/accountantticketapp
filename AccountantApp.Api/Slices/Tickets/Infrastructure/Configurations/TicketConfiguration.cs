using AccountantApp.Api.Slices.Tickets.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Tickets.Infrastructure.Configurations;

/// <summary>
/// Entities are PascalCase, columns are snake_case, and no automatic conversion is configured anywhere
/// in this application. Every property needs an explicit HasColumnName, or the first query that
/// touches it fails at runtime with 42703: column t.CustomerId does not exist -- on one code path,
/// not at startup. With six entities and roughly ninety columns this is the slice where one gets
/// missed, which is why TicketsColumnMappingTests asserts it by reflection.
/// </summary>
public sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets");
        builder.HasKey(ticket => ticket.Id);

        builder.Property(ticket => ticket.Id).HasColumnName("id");
        builder.Property(ticket => ticket.Reference)
            .HasColumnName("reference").HasMaxLength(20).IsRequired();
        builder.Property(ticket => ticket.CustomerId).HasColumnName("customer_id").IsRequired();
        builder.Property(ticket => ticket.TicketTypeId).HasColumnName("ticket_type_id").IsRequired();
        builder.Property(ticket => ticket.TicketTypeVersionId)
            .HasColumnName("ticket_type_version_id").IsRequired();
        builder.Property(ticket => ticket.CreatorUserAccountId)
            .HasColumnName("creator_user_account_id").IsRequired();
        builder.Property(ticket => ticket.SubjectEmployeeId)
            .HasColumnName("subject_employee_id").IsRequired();

        builder.Property(ticket => ticket.Status)
            .HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(ticket => ticket.AssigneeUserAccountId)
            .HasColumnName("assignee_user_account_id");

        builder.Property(ticket => ticket.Priority)
            .HasColumnName("priority").HasMaxLength(10).IsRequired();
        builder.Property(ticket => ticket.DueDate)
            .HasColumnName("due_date").HasColumnType("date");

        builder.Property(ticket => ticket.Title)
            .HasColumnName("title").HasMaxLength(300).IsRequired();

        builder.Property(ticket => ticket.CurrentRevisionId).HasColumnName("current_revision_id");
        builder.Property(ticket => ticket.PrecededByTicketId).HasColumnName("preceded_by_ticket_id");

        builder.Property(ticket => ticket.Version).HasColumnName("version").IsRequired();

        builder.Property(ticket => ticket.CreatedAt).HasColumnName("created_at");
        builder.Property(ticket => ticket.LastActivityAt).HasColumnName("last_activity_at");
        builder.Property(ticket => ticket.ClosedAt).HasColumnName("closed_at");

        // Computed properties, not columns. Without these EF maps them to is_terminal, is_open and
        // fields_editable, none of which exist.
        builder.Ignore(ticket => ticket.IsTerminal);
        builder.Ignore(ticket => ticket.IsOpen);
        builder.Ignore(ticket => ticket.FieldsEditable);

        // CurrentRevisionId gets NO relationship configured, deliberately. tickets and
        // ticket_revisions reference each other, so an FK here would make the two-insert creation
        // sequence impossible. The database has no constraint on it either (plan section 1.3).

        // The one FK on this table, and it is self-referential and therefore intra-slice. Configured
        // without navigation properties on either side: a Ticket.PrecededBy navigation would invite a
        // handler to load and read a predecessor the caller may not see, and section 1.2 requires the
        // predecessor be resolved through .WhereTicketVisible(user) so the miss is a natural 404.
        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(ticket => ticket.PrecededByTicketId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(ticket => ticket.Revisions)
            .WithOne()
            .HasForeignKey(revision => revision.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(ticket => ticket.Messages)
            .WithOne()
            .HasForeignKey(message => message.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        // Declared so EF's model matches the database. The SQL script is what CREATEs them; these
        // exist for model-consistency checks, not for migration generation.
        builder.HasIndex(ticket => ticket.Reference)
            .IsUnique()
            .HasDatabaseName("uq_tickets_reference");

        builder.HasIndex(ticket => ticket.LastActivityAt)
            .HasFilter("status = 'Submitted' AND assignee_user_account_id IS NULL")
            .HasDatabaseName("idx_tickets_pickup");

        builder.HasIndex(ticket => new { ticket.AssigneeUserAccountId, ticket.LastActivityAt })
            .HasFilter("status IN ('Submitted','InReview','AwaitingInformation','Answered')")
            .HasDatabaseName("idx_tickets_assignee_open");

        builder.HasIndex(ticket => new { ticket.CustomerId, ticket.LastActivityAt, ticket.Id })
            .HasDatabaseName("idx_tickets_customer_activity");

        builder.HasIndex(ticket => new { ticket.CreatorUserAccountId, ticket.LastActivityAt })
            .HasDatabaseName("idx_tickets_creator");

        builder.HasIndex(ticket => new { ticket.SubjectEmployeeId, ticket.LastActivityAt })
            .HasDatabaseName("idx_tickets_subject");
    }
}
