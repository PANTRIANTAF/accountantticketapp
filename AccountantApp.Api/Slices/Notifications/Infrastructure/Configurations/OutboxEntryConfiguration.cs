using AccountantApp.Api.Slices.Notifications.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Notifications.Infrastructure.Configurations;

public sealed class OutboxEntryConfiguration : IEntityTypeConfiguration<OutboxEntry>
{
    public void Configure(EntityTypeBuilder<OutboxEntry> builder)
    {
        builder.ToTable("notification_outbox");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(o => o.NotificationId)
            .HasColumnName("notification_id")
            .IsRequired();

        builder.HasOne<Notification>()
            .WithMany()
            .HasForeignKey(o => o.NotificationId);

        builder.Property(o => o.ResolvedEmail)
            .HasColumnName("resolved_email")
            .HasMaxLength(320);

        builder.Property(o => o.EmailBody)
            .HasColumnName("email_body")
            .HasMaxLength(4000);

        builder.Property(o => o.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(o => o.AttemptCount)
            .HasColumnName("attempt_count");

        builder.Property(o => o.NextAttemptAt)
            .HasColumnName("next_attempt_at");

        builder.Property(o => o.LastError)
            .HasColumnName("last_error")
            .HasMaxLength(1000);

        builder.Property(o => o.CreatedAt)
            .HasColumnName("created_at")
            .ValueGeneratedOnAdd();

        builder.Property(o => o.SentAt)
            .HasColumnName("sent_at");
    }
}
