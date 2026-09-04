using AccountantApp.Api.Slices.Documents.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Documents.Infrastructure.Configurations;

/// <summary>
/// Entities are PascalCase, columns are snake_case, and there is no automatic conversion configured
/// anywhere in this application. Every property needs an explicit HasColumnName, or the first query
/// that touches it fails with 42703: column d.TicketId does not exist -- at runtime, on one code path,
/// not at startup.
/// </summary>
public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("documents");
        builder.HasKey(document => document.Id);

        // MANDATORY, and a normative requirement rather than a recommendation. 01-DomainModel.md
        // section 9.2: "A soft-delete column's real cost is that every query must exclude the deleted
        // rows, and forgetting once serves a file a user was told was gone. Discipline is not the
        // mechanism."
        //
        // Declared here, on the entity, so the DEFAULT for every LINQ query in this slice is already
        // correct and a query that forgets the clause still behaves. It also gives the
        // re-check-at-download-time rule for free: a link handed out before a delete stops working
        // after it.
        //
        // IgnoreQueryFilters() must appear NOWHERE in this slice. Section 9.2 rule 2 says no handler
        // needs it, so a use of it is a mistake until a spec says otherwise -- treat one as a
        // review-blocking finding, not a local convenience. The soft-delete WRITE does not need it
        // either: it loads a live document, which the filter permits, and deleting an
        // already-deleted document is correctly a 404 because the filter does not find it.
        builder.HasQueryFilter(document => document.DeletedAt == null);

        builder.Property(document => document.Id).HasColumnName("id");
        builder.Property(document => document.CustomerId).HasColumnName("customer_id").IsRequired();
        builder.Property(document => document.TicketId).HasColumnName("ticket_id").IsRequired();

        builder.Property(document => document.Origin)
            .HasColumnName("origin").HasMaxLength(30).IsRequired();

        builder.Property(document => document.OriginalFileName)
            .HasColumnName("original_file_name").HasMaxLength(255).IsRequired();

        // The SNIFFED type, never the client-declared Content-Type header. Plan section 3.4 rule 3.
        builder.Property(document => document.ContentType)
            .HasColumnName("content_type").HasMaxLength(100).IsRequired();

        // long, mapping to BIGINT. An int overflows at 2 GB, which the 25 MB cap makes unreachable
        // today and which costs nothing to get right.
        builder.Property(document => document.SizeBytes).HasColumnName("size_bytes").IsRequired();

        // CHAR(64), so IsFixedLength(). Without it EF and PostgreSQL disagree about padding and an
        // equality comparison against a 64-character hex string silently never matches -- which turns
        // the duplicate-detection query into one that finds nothing, with no error anywhere.
        builder.Property(document => document.ContentHash)
            .HasColumnName("content_hash").HasMaxLength(64).IsFixedLength().IsRequired();

        builder.Property(document => document.UploadedByUserAccountId)
            .HasColumnName("uploaded_by_user_account_id").IsRequired();
        builder.Property(document => document.UploadedAt).HasColumnName("uploaded_at").IsRequired();

        builder.Property(document => document.DeletedAt).HasColumnName("deleted_at");
        builder.Property(document => document.DeletedByUserAccountId)
            .HasColumnName("deleted_by_user_account_id");

        // A computed property, not a column. Without this EF tries to map IsDeleted to is_deleted,
        // which does not exist.
        builder.Ignore(document => document.IsDeleted);

        // Declared so EF's model matches the database. The SQL script is what CREATEs them; these
        // exist for model-consistency checks, not for migration generation. The filters repeat the
        // partial-index predicates from the migration exactly, and idx_documents_ticket's mirrors the
        // global query filter above -- change them together or not at all.
        builder.HasIndex(document => new { document.TicketId, document.UploadedAt })
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("idx_documents_ticket");

        builder.HasIndex(document => document.CustomerId)
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("idx_documents_customer");

        // NOT unique, deliberately. Plan section 1.3: the same file legitimately appears on two
        // tickets, and deduplicating would make one row's bytes serve two documents.
        builder.HasIndex(document => new { document.TicketId, document.ContentHash })
            .HasDatabaseName("idx_documents_ticket_hash");
    }
}
