using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Slices.Tickets.Infrastructure;

/// <summary>
/// Maps exactly the seven entities this slice owns. A DbSet&lt;Document&gt; or
/// DbSet&lt;TicketTypeVersion&gt; here would mean two slices own one table and their migrations would
/// fight.
///
/// SEVEN, not six: ticket_due_date_reminders (plan section 9a.3) was added by a second, later
/// migration for the due-date scanner. It is the one table here that no request path ever reads --
/// only DueDateScanner touches it -- and it is mapped rather than written with raw SQL precisely so
/// the scanner can be tested on the in-memory provider.
///
/// ticket_reference_counters has no DbSet on purpose: it is written only by
/// TicketReferenceAllocator's single atomic upsert, and an entity for it would invite somebody to
/// read-then-increment it through the change tracker, which is the lost-update race that produces two
/// tickets with one reference.
///
/// THERE ARE NO GLOBAL QUERY FILTERS, and each temptation fails for its own reason:
///
/// 1. No Customer-scope filter. Accountants are unscoped, so the filter would need the caller's role,
///    which means an EF filter reading a scoped service. An explicit .WhereTicketVisible(user, ...)
///    is greppable; a missing global filter is nothing to see.
/// 2. No Draft-excluding filter. The Creator must see their own drafts, and the create, submit and
///    cancel handlers must be able to find their own targets. Draft privacy is layer 3 of the
///    visibility extension, where it can see who the caller is.
/// 3. No Cancelled-excluding filter. A cancelled ticket stays readable (section 1.9); the list
///    endpoints offer a status filter instead.
/// 4. No InternalNote-excluding filter on TicketMessage. This is the tempting one -- matrix section 6
///    requires the exclusion be "enforced on the server by filtering" -- but a global filter cannot
///    see the caller's role, so it would hide internal notes from Accountants too, i.e. from the only
///    people they exist for. The read query does it, with TicketMessageKind.CustomerVisible.
/// </summary>
public sealed class TicketsDbContext : DbContext
{
    // Required. Without this constructor the context cannot be configured with a provider, and
    // AddScoped<TicketsDbContext>() -- which is forbidden -- is what somebody reaches for instead.
    public TicketsDbContext(DbContextOptions<TicketsDbContext> options) : base(options) { }

    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketRevision> TicketRevisions => Set<TicketRevision>();
    public DbSet<FieldValue> FieldValues => Set<FieldValue>();
    public DbSet<FieldVerification> FieldVerifications => Set<FieldVerification>();
    public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();
    public DbSet<TicketMessageDocument> TicketMessageDocuments => Set<TicketMessageDocument>();

    /// <summary>
    /// The due-date scanner's sent-markers (section 9a.3). Written by DueDateScanner and by nothing
    /// else; no handler, endpoint or DTO refers to it.
    /// </summary>
    public DbSet<TicketDueDateReminder> DueDateReminders => Set<TicketDueDateReminder>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new TicketConfiguration());
        builder.ApplyConfiguration(new TicketRevisionConfiguration());
        builder.ApplyConfiguration(new FieldValueConfiguration());
        builder.ApplyConfiguration(new FieldVerificationConfiguration());
        builder.ApplyConfiguration(new TicketMessageConfiguration());
        builder.ApplyConfiguration(new TicketMessageDocumentConfiguration());
        builder.ApplyConfiguration(new TicketDueDateReminderConfiguration());
    }
}
