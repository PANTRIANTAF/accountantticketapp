using AccountantApp.Api.Slices.Notifications.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Notifications.Infrastructure.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(n => n.RecipientUserId)
            .HasColumnName("recipient_user_id")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(n => n.TicketId)
            .HasColumnName("ticket_id");

        builder.Property(n => n.EventKind)
            .HasColumnName("event_kind")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(n => n.Title)
            .HasColumnName("title")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(n => n.Body)
            .HasColumnName("body")
            .HasMaxLength(2000)
            .IsRequired();

        builder.Property(n => n.IsRead)
            .HasColumnName("is_read");

        builder.Property(n => n.ReadAt)
            .HasColumnName("read_at");

        builder.Property(n => n.CreatedAt)
            .HasColumnName("created_at")
            .ValueGeneratedOnAdd();
    }
}
