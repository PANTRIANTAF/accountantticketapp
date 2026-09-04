using AccountantApp.Api.Slices.Tickets.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Tickets.Infrastructure.Configurations;

public sealed class FieldVerificationConfiguration : IEntityTypeConfiguration<FieldVerification>
{
    public void Configure(EntityTypeBuilder<FieldVerification> builder)
    {
        builder.ToTable("field_verifications");
        builder.HasKey(verification => verification.Id);

        builder.Property(verification => verification.Id).HasColumnName("id");
        builder.Property(verification => verification.FieldValueId)
            .HasColumnName("field_value_id").IsRequired();
        builder.Property(verification => verification.Outcome)
            .HasColumnName("outcome").HasMaxLength(20).IsRequired();
        builder.Property(verification => verification.RejectionReason)
            .HasColumnName("rejection_reason").HasMaxLength(2000);
        builder.Property(verification => verification.VerifiedByUserAccountId)
            .HasColumnName("verified_by_user_account_id").IsRequired();
        builder.Property(verification => verification.VerifiedAt).HasColumnName("verified_at");

        builder.Ignore(verification => verification.IsAccepted);
        builder.Ignore(verification => verification.IsRejected);

        builder.HasIndex(verification => new { verification.FieldValueId, verification.VerifiedAt })
            .HasDatabaseName("idx_field_verifications_value");
    }
}
