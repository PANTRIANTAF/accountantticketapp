using AccountantApp.Api.Slices.Documents.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Documents.Infrastructure.Configurations;

/// <summary>
/// The bytes. There is deliberately NO query filter here, because the table has no deleted_at column
/// to filter on -- and that absence is exactly why DocumentApi.OpenAsync must find the Document
/// through the FILTERED query first and only then read the bytes by id. Plan section 2.4 rule 3.
/// </summary>
public sealed class DocumentContentConfiguration : IEntityTypeConfiguration<DocumentContent>
{
    public void Configure(EntityTypeBuilder<DocumentContent> builder)
    {
        builder.ToTable("document_contents");

        // The document id IS the key. One row of bytes per document, no surrogate.
        builder.HasKey(content => content.DocumentId);

        builder.Property(content => content.DocumentId).HasColumnName("document_id");
        builder.Property(content => content.Content)
            .HasColumnName("content").HasColumnType("bytea").IsRequired();
    }
}
