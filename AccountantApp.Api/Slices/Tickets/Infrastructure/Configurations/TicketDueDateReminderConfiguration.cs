using AccountantApp.Api.Slices.Tickets.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Tickets.Infrastructure.Configurations;

/// <summary>
/// Plan section 9a.3. Same rules as the other six configurations: every property gets an explicit
/// HasColumnName, because no naming convention is configured anywhere in this application and a missed
/// one fails at runtime with 42703 on one code path rather than at startup.
/// </summary>
public sealed class TicketDueDateReminderConfiguration
    : IEntityTypeConfiguration<TicketDueDateReminder>
{
    public void Configure(EntityTypeBuilder<TicketDueDateReminder> builder)
    {
        builder.ToTable("ticket_due_date_reminders");

        // Composite key, matching PRIMARY KEY (ticket_id, due_date). This IS the idempotency guarantee:
        // a surrogate id with no unique constraint would let two passes -- or two replicas -- each
        // insert a marker for the same (ticket, due_date) and each send a reminder. Deliberately not
        // (ticket_id) alone: that is the boolean-shaped key section 9a.3 rejects, because it suppresses
        // the reminder forever once a ticket has been reminded at any date.
        builder.HasKey(reminder => new { reminder.TicketId, reminder.DueDate });

        builder.Property(reminder => reminder.TicketId).HasColumnName("ticket_id").IsRequired();

        // "date", exactly like tickets.due_date. The default mapping for DateOnly is already DATE, but
        // it is stated because the two columns are compared to each other and a silent divergence here
        // would make the marker miss.
        builder.Property(reminder => reminder.DueDate)
            .HasColumnName("due_date").HasColumnType("date").IsRequired();

        builder.Property(reminder => reminder.SentAt).HasColumnName("sent_at").IsRequired();

        // Intra-slice FK, configured WITHOUT navigation properties on either side. A
        // Ticket.DueDateReminders collection would be Included by somebody eventually, and this table
        // has no business appearing in any response; a TicketDueDateReminder.Ticket navigation would
        // let a reminder be used to load a ticket without going through TicketVisibility.
        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(reminder => reminder.TicketId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
