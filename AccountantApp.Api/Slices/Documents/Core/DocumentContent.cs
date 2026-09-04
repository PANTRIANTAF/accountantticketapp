namespace AccountantApp.Api.Slices.Documents.Core;

/// <summary>
/// The bytes, in their own table. BYTEA, not a large object: BYTEA is transactional, comes back with
/// an ordinary query, and is in pg_dump without extra flags (04-Infrastructure.md section 7).
///
/// It has no ICustomerScoped, no audit columns and no soft-delete flag, because it is addressed only
/// by DocumentId and is only ever reached AFTER the corresponding Document has been found through the
/// filtered query. IT MUST NOT BE REACHABLE BY ANY OTHER PATH: this table has no deleted_at column and
/// therefore no query filter, so a query that starts here -- or a join in this direction -- serves the
/// bytes of a document a user was told was gone. Plan sections 2.2 and 5.2 rule 2.
/// </summary>
public sealed class DocumentContent
{
    public Guid DocumentId { get; set; }
    public byte[] Content { get; set; } = [];
}
