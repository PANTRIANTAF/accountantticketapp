using AccountantApp.Api.Slices.Identity.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Identity.Infrastructure.Configurations;

public sealed class UserAccountTokenConfiguration : IEntityTypeConfiguration<UserAccountToken>
{
    public void Configure(EntityTypeBuilder<UserAccountToken> builder)
    {
        builder.ToTable("user_account_tokens");
        builder.HasKey(token => token.Id);

        builder.Property(token => token.Id).HasColumnName("id");
        builder.Property(token => token.UserAccountId).HasColumnName("user_account_id").IsRequired();
        builder.Property(token => token.Purpose)
            .HasColumnName("purpose").HasMaxLength(30).IsRequired();

        // CHAR(64): a hex SHA-256 is always exactly 64 characters.
        builder.Property(token => token.TokenHash)
            .HasColumnName("token_hash").HasMaxLength(64).IsFixedLength().IsRequired();

        builder.Property(token => token.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(token => token.ConsumedAt).HasColumnName("consumed_at");
        builder.Property(token => token.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(token => token.TokenHash)
            .IsUnique().HasDatabaseName("uq_user_account_tokens_hash");
    }
}
