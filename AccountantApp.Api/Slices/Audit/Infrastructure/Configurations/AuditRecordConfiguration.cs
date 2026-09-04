using AccountantApp.Api.Slices.Audit.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Audit.Infrastructure.Configurations;

public sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        builder.ToTable("audit_entries");
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).HasColumnName("id");
        builder.Property(entry => entry.ActorUserId).HasColumnName("actor_user_id").HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.ActorRole).HasColumnName("actor_role").HasMaxLength(30).IsRequired();
        builder.Property(entry => entry.CustomerId).HasColumnName("customer_id");
        builder.Property(entry => entry.Action).HasColumnName("action").HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.TargetKind).HasColumnName("target_kind").HasMaxLength(50).IsRequired();
        builder.Property(entry => entry.TargetId).HasColumnName("target_id").HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.Outcome).HasColumnName("outcome").HasMaxLength(20).IsRequired();
        builder.Property(entry => entry.BeforeValue).HasColumnName("before_value").HasColumnType("jsonb");
        builder.Property(entry => entry.AfterValue).HasColumnName("after_value").HasColumnType("jsonb");
        builder.Property(entry => entry.OccurredAt).HasColumnName("occurred_at");
        builder.Property(entry => entry.SourceIp).HasColumnName("source_ip").HasMaxLength(45).IsRequired();
        builder.Property(entry => entry.UserAgent).HasColumnName("user_agent").HasMaxLength(512).IsRequired();
    }
}