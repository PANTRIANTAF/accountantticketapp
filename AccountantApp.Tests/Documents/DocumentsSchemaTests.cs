using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Migrations;
using AccountantApp.Api.Slices.Documents.Application;
using AccountantApp.Api.Slices.Documents.Core;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Documents.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccountantApp.Tests.Documents;

/// <summary>
/// The only tests that touch the real Documents migration, and the only ones that can.
///
/// Everything else in this folder runs against the in-memory provider, which has NO BYTEA, enforces no
/// CHECK constraint, has no foreign key, no partial index, and -- the one that matters most -- no real
/// transaction. So without this file the following are entirely unverified: the DDL, all three CHECK
/// constraints, the intra-slice foreign key, all three partial indexes, the byte-identical
/// multi-megabyte round trip through BYTEA, and whether a rolled-back ticket operation really leaves no
/// bytes behind.
///
/// That last one is plan section 11.3 test 1, and it asserts BY QUERYING THE DATABASE ON A SEPARATE
/// CONNECTION after the scope is disposed. A test that checked only the thrown exception would pass just
/// as happily against a DocumentApi registered with its own connection -- the exact bug where the bytes
/// commit independently of the ticket change they were supposed to be atomic with, and survive its
/// rollback. Nothing else in the suite can see it.
/// </summary>
public sealed class DocumentsSchemaTests
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
    private const string ExpectedScriptKey =
        "Documents/Infrastructure/Migrations/20260903_001_CreateDocumentsSchema.sql";

    [SkippableFact]
    public async Task Migration_constraints_indexes_bytea_round_trip_and_real_rollback_work_against_real_postgres()
    {
        Skip.IfNot(await PostgresIsReachable(),
            "No PostgreSQL at localhost:5432. The Documents schema, its three CHECK constraints, the "
            + "intra-slice foreign key, all three partial indexes, the byte-identical multi-megabyte "
            + "BYTEA round trip, and the rollback that must leave no bytes behind are all unverified.");

        var database = $"accountant_app_documents_test_{Guid.NewGuid():N}";
        await ExecuteOnAdmin($"CREATE DATABASE \"{database}\"");
        var connectionString = AdminConnectionString.Replace("Database=postgres", $"Database={database}");

        try
        {
            await SqlMigrationRunner.RunAsync(connectionString, AppContext.BaseDirectory);

            // Slice-relative and forward-slashed. A backslash here on Windows means the migration re-runs
            // on Linux and fails on the already-existing table.
            Assert.Equal(ExpectedScriptKey, await QueryScalar<string>(connectionString,
                $"SELECT script_name FROM schema_versions WHERE script_name = '{ExpectedScriptKey}'"));

            await AssertTablesAndForeignKey(connectionString);
            await AssertSizeConstraint(connectionString);
            await AssertOriginConstraint(connectionString);
            await AssertDeletionConstraint(connectionString);
            await AssertHashIsNotUnique(connectionString);
            await AssertIndexes(connectionString);
            await AssertColumnMapping(connectionString);
            await AssertMultiMegabyteRoundTripIsByteIdentical(connectionString);
            await AssertRollbackLeavesNoRowAndNoBytes(connectionString);
            await AssertSoftDeleteKeepsTheRowAndTheBytes(connectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnAdmin($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    private static async Task AssertTablesAndForeignKey(string connectionString)
    {
        var tables = await QueryStrings(connectionString,
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public'");
        Assert.Contains("documents", tables);
        Assert.Contains("document_contents", tables);

        // BYTEA, not TEXT and not OID. TEXT would base64 the content into a third more storage and make
        // the round trip lossy for anything that is not valid UTF-8, which is most of the allow-list.
        Assert.Equal("bytea", await QueryScalar<string>(connectionString,
            "SELECT data_type FROM information_schema.columns "
            + "WHERE table_name = 'document_contents' AND column_name = 'content'"));

        // CHAR(64), which is what makes IsFixedLength() on the model necessary.
        Assert.Equal("character", await QueryScalar<string>(connectionString,
            "SELECT data_type FROM information_schema.columns "
            + "WHERE table_name = 'documents' AND column_name = 'content_hash'"));

        // The one foreign key in the schema, and it is intra-slice. Bytes with no metadata row are
        // unreachable forever; a metadata row with no bytes downloads as a 500.
        var orphan = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            "INSERT INTO document_contents (document_id, content) VALUES "
            + "(gen_random_uuid(), decode('00', 'hex'))"));
        Assert.Equal("23503", orphan.SqlState);

        // And there is no cross-slice foreign key: customer_id and ticket_id reference nothing, because
        // a FK out of the slice would make two schemas one schema.
        var foreignKeys = await QueryStrings(connectionString,
            "SELECT conname FROM pg_constraint WHERE contype = 'f' "
            + "AND conrelid IN ('documents'::regclass, 'document_contents'::regclass)");
        Assert.Single(foreignKeys);

        // No cascade on it either. A cascade is harmless while nothing deletes, and it advertises an
        // operation that must not exist.
        Assert.Equal("a", await QueryScalar<string>(connectionString,
            "SELECT confdeltype::text FROM pg_constraint WHERE contype = 'f' "
            + "AND conrelid = 'document_contents'::regclass"));
    }

    /// <summary>
    /// ck_documents_size. The cap that cannot be bypassed: the application checks it before buffering and
    /// the endpoint will check it again, but only this one holds if either is ever changed alone.
    /// </summary>
    private static async Task AssertSizeConstraint(string connectionString)
    {
        // Zero bytes. A client bug, and the row it produces downloads as nothing at all.
        var empty = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOn(connectionString, Insert(sizeBytes: 0)));
        Assert.Equal("23514", empty.SqlState);
        Assert.Contains("ck_documents_size", empty.Message);

        // One byte over 25 MiB.
        var oversized = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOn(connectionString, Insert(sizeBytes: 26_214_401)));
        Assert.Equal("23514", oversized.SqlState);
        Assert.Contains("ck_documents_size", oversized.Message);

        // Negative, which is what an int overflow or a bad cast produces.
        var negative = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOn(connectionString, Insert(sizeBytes: -1)));
        Assert.Equal("23514", negative.SqlState);

        // Exactly at the cap is accepted -- 26214400, the same number as UploadValidation's constant. An
        // off-by-one here rejects a file the application accepted, as a 500 from a constraint violation.
        Assert.Equal(DocumentLimits.MaxUploadSizeBytes, 26_214_400);
        await ExecuteOn(connectionString, Insert(sizeBytes: DocumentLimits.MaxUploadSizeBytes));
        await ExecuteOn(connectionString, Insert(sizeBytes: 1));
    }

    private static async Task AssertOriginConstraint(string connectionString)
    {
        // Origin is derived from the uploader's role. A third value, or the wrong case, would be
        // invisible to every ordinal comparison in the code while reading as a valid row in a dump.
        foreach (var origin in new[] { "Whatever", "customerupload", "CUSTOMERUPLOAD", "" })
        {
            var rejected = await Assert.ThrowsAsync<PostgresException>(
                () => ExecuteOn(connectionString, Insert(origin: origin)));
            Assert.Equal("23514", rejected.SqlState);
            Assert.Contains("ck_documents_origin", rejected.Message);
        }

        await ExecuteOn(connectionString, Insert(origin: DocumentOrigin.CustomerUpload));
        await ExecuteOn(connectionString, Insert(origin: DocumentOrigin.AccountantResponse));
    }

    /// <summary>
    /// ck_documents_deletion. The two soft-delete columns are set together or not at all: a row with a
    /// deleted_at and no deleter cannot answer "who deleted it", and the bytes are hidden with nobody
    /// accountable.
    /// </summary>
    private static async Task AssertDeletionConstraint(string connectionString)
    {
        var instantOnly = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOn(connectionString, Insert(deletedAt: "NOW()")));
        Assert.Equal("23514", instantOnly.SqlState);
        Assert.Contains("ck_documents_deletion", instantOnly.Message);

        var deleterOnly = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOn(connectionString, Insert(deletedBy: "gen_random_uuid()")));
        Assert.Equal("23514", deleterOnly.SqlState);
        Assert.Contains("ck_documents_deletion", deleterOnly.Message);

        // Both, and neither: the two legal shapes.
        await ExecuteOn(connectionString,
            Insert(deletedAt: "NOW()", deletedBy: "gen_random_uuid()"));
        await ExecuteOn(connectionString, Insert());
    }

    private static async Task AssertHashIsNotUnique(string connectionString)
    {
        var ticketId = Guid.NewGuid();
        var hash = new string('b', 64);

        // The same content on the same ticket, twice. Section 1.3: making this index unique would mean
        // one row's bytes serving two documents, and soft-deleting either would break or ignore the other.
        await ExecuteOn(connectionString, Insert(ticketId: ticketId, contentHash: hash));
        await ExecuteOn(connectionString, Insert(ticketId: ticketId, contentHash: hash));

        Assert.Equal(2, await QueryScalar<long>(connectionString,
            $"SELECT COUNT(*) FROM documents WHERE ticket_id = '{ticketId}'"));
    }

    private static async Task AssertIndexes(string connectionString)
    {
        var indexes = await QueryStrings(connectionString,
            "SELECT indexname FROM pg_indexes WHERE tablename = 'documents'");

        foreach (var expected in new[]
                 { "idx_documents_ticket", "idx_documents_customer", "idx_documents_ticket_hash" })
            Assert.Contains(expected, indexes);

        // The two partial ones. A partial index that silently became total still answers every query
        // correctly -- it shows up only as a table larger and slower than it should be, years later --
        // and idx_documents_ticket's predicate has to mirror the global query filter exactly or the
        // slice's main query stops being able to use it.
        var partial = await QueryStrings(connectionString,
            "SELECT indexname FROM pg_indexes WHERE tablename = 'documents' "
            + "AND indexdef LIKE '%WHERE (deleted_at IS NULL)%'");
        Assert.Contains("idx_documents_ticket", partial);
        Assert.Contains("idx_documents_customer", partial);

        // And the hash index is NOT unique and NOT partial: the duplicate-reporting query asks about all
        // of a ticket's documents.
        var unique = await QueryStrings(connectionString,
            "SELECT indexname FROM pg_indexes WHERE tablename = 'documents' "
            + "AND indexdef LIKE 'CREATE UNIQUE%'");
        Assert.DoesNotContain("idx_documents_ticket_hash", unique);
        Assert.DoesNotContain("idx_documents_ticket_hash", partial);
    }

    /// <summary>
    /// Every property must map. snake_case is NOT automatic in this codebase -- each column name is
    /// declared explicitly, and a missed HasColumnName does not fail at startup. It fails on the first
    /// query that touches the column, as a 42703 from deep inside EF.
    /// </summary>
    private static async Task AssertColumnMapping(string connectionString)
    {
        var options = Options(connectionString);
        var documentId = Guid.NewGuid();
        var recorded = new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.FromHours(2));
        var hash = UploadValidation.ComputeHash("mapping"u8.ToArray());

        await using (var db = new DocumentsDbContext(options))
        {
            db.Documents.Add(new Document
            {
                Id = documentId,
                CustomerId = Guid.NewGuid(),
                TicketId = Guid.NewGuid(),
                Origin = DocumentOrigin.AccountantResponse,
                OriginalFileName = "Βεβαίωση Αποδοχών.pdf",
                ContentType = "application/pdf",
                SizeBytes = 7,
                ContentHash = hash,
                UploadedByUserAccountId = Guid.NewGuid(),
                UploadedAt = recorded
            });
            db.DocumentContents.Add(new DocumentContent
            {
                DocumentId = documentId,
                Content = "mapping"u8.ToArray()
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new DocumentsDbContext(options))
        {
            var read = await db.Documents.SingleAsync(item => item.Id == documentId);

            // TIMESTAMPTZ normalises the offset, so compare in UTC.
            Assert.Equal(recorded.UtcDateTime, read.UploadedAt.UtcDateTime);
            // A Greek file name survives the column unchanged; VARCHAR(255) is characters, not bytes.
            Assert.Equal("Βεβαίωση Αποδοχών.pdf", read.OriginalFileName);
            Assert.Equal(DocumentOrigin.AccountantResponse, read.Origin);
            Assert.Null(read.DeletedAt);
            Assert.False(read.IsDeleted);

            // CHAR(64) is BLANK-PADDED by PostgreSQL, and a 64-character hash is exactly 64 characters,
            // so this comparison is the one that proves the width is right. A CHAR(65) would come back
            // with a trailing space and every integrity check would fail for every document.
            Assert.Equal(hash, read.ContentHash.Trim());
            Assert.Equal(64, read.ContentHash.Trim().Length);
        }

        // The one computed property is not a column.
        Assert.Equal(0, await QueryScalar<long>(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns "
            + "WHERE table_name = 'documents' AND column_name IN ('is_deleted', 'IsDeleted')"));
    }

    /// <summary>
    /// A multi-megabyte file through BYTEA and back, byte for byte. The in-memory provider hands back the
    /// same array instance it was given, so it cannot fail this test however the column is configured --
    /// which means the column is unverified everywhere else.
    /// </summary>
    private static async Task AssertMultiMegabyteRoundTripIsByteIdentical(string connectionString)
    {
        var payload = MultiMegabytePdf(3 * 1024 * 1024);
        Guid documentId;

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            var transaction = new RequestTransaction();

            await using var db = new DocumentsDbContext(Options(connection));
            var api = new DocumentApi(db, transaction);

            await using (await transaction.BeginAsync(db, CancellationToken.None))
            {
                documentId = (await api.StoreAsync(Request(payload, "big.pdf"))).Id;
                // The CALLER commits. The slice never does.
                await transaction.CommitAsync(CancellationToken.None);
            }
        }

        // A fresh connection and a fresh context, so nothing is served out of EF's change tracker.
        await using (var db = new DocumentsDbContext(Options(connectionString)))
        {
            var opened = await new DocumentApi(db, new RequestTransaction()).OpenAsync(documentId);

            Assert.NotNull(opened);
            // Byte-identical, not merely the same length -- and the hash check inside OpenAsync has
            // already agreed, which is what makes it an integrity check rather than decoration.
            Assert.Equal(payload, opened!.Content);
            Assert.Equal(payload.Length, opened.Document.SizeBytes);
            Assert.Equal("application/pdf", opened.Document.ContentType);
        }

        // And the database agrees about the length, so nothing was truncated on the way in.
        Assert.Equal(payload.Length, await QueryScalar<int>(connectionString,
            $"SELECT octet_length(content) FROM document_contents WHERE document_id = '{documentId}'"));
    }

    /// <summary>
    /// PLAN SECTION 11.3 TEST 1. A rolled-back ticket operation must leave NEITHER a metadata row NOR any
    /// bytes.
    ///
    /// The assertion queries a SEPARATE CONNECTION after the scope is disposed. It does not assert on the
    /// exception: the exception is thrown whether or not the rollback reached the bytes, so the obvious
    /// version of this test passes against a DocumentApi that was given its own connection -- at which
    /// point EnlistAsync joins nothing, the upload commits on its own, and 25 MB of a document belonging
    /// to a ticket that does not exist sits in the table forever. That is the one failure mode the
    /// "bytes in PostgreSQL rather than on a volume" decision exists to make impossible.
    /// </summary>
    private static async Task AssertRollbackLeavesNoRowAndNoBytes(string connectionString)
    {
        var ticketId = Guid.NewGuid();

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            var transaction = new RequestTransaction();

            await using var db = new DocumentsDbContext(Options(connection));
            var api = new DocumentApi(db, transaction);

            // What a Tickets handler does: begin, store, then fail before committing. Disposal rolls back.
            await using (await transaction.BeginAsync(db, CancellationToken.None))
            {
                var stored = await api.StoreAsync(Request(
                    DocumentsTestHarness.Pdf(), "doomed.pdf", ticketId));

                // Inside the transaction it is there, so the rollback below is genuinely undoing work
                // rather than the store having quietly failed.
                Assert.NotNull(await api.FindAsync(stored.Id));

                // NO CommitAsync. The ticket update that prompted this upload failed.
            }
        }

        // THE ASSERTION, on a separate connection. Both tables.
        Assert.Equal(0, await QueryScalar<long>(connectionString,
            $"SELECT COUNT(*) FROM documents WHERE ticket_id = '{ticketId}'"));
        Assert.Equal(0, await QueryScalar<long>(connectionString,
            "SELECT COUNT(*) FROM document_contents dc "
            + "WHERE NOT EXISTS (SELECT 1 FROM documents d WHERE d.id = dc.document_id)"));
    }

    /// <summary>
    /// PLAN SECTION 11.3 TEST 2, against the real schema. A soft delete must leave the row AND the bytes:
    /// a hard delete produces exactly the same API behaviour -- absent from the list, null from FindAsync,
    /// null from OpenAsync -- and passes every test that only looks through IDocumentApi.
    /// </summary>
    private static async Task AssertSoftDeleteKeepsTheRowAndTheBytes(string connectionString)
    {
        var ticketId = Guid.NewGuid();
        var deleterId = Guid.NewGuid();
        var payload = DocumentsTestHarness.Pdf(4096);
        Guid documentId;

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            var transaction = new RequestTransaction();
            await using var db = new DocumentsDbContext(Options(connection));
            var api = new DocumentApi(db, transaction);

            await using (await transaction.BeginAsync(db, CancellationToken.None))
            {
                documentId = (await api.StoreAsync(Request(payload, "kept.pdf", ticketId))).Id;
                await transaction.CommitAsync(CancellationToken.None);
            }
        }

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            var transaction = new RequestTransaction();
            await using var db = new DocumentsDbContext(Options(connection));
            var api = new DocumentApi(db, transaction);

            await using (await transaction.BeginAsync(db, CancellationToken.None))
            {
                Assert.True(await api.SoftDeleteAsync(documentId, deleterId));
                await transaction.CommitAsync(CancellationToken.None);
            }
        }

        // Invisible through the API, in a fresh context, because of the global query filter.
        await using (var db = new DocumentsDbContext(Options(connectionString)))
        {
            var api = new DocumentApi(db, new RequestTransaction());

            Assert.Null(await api.FindAsync(documentId));
            Assert.Null(await api.OpenAsync(documentId));
            Assert.Empty(await api.ListByTicketAsync(ticketId));

            // A second delete is false, which the caller turns into a 404 -- the filter simply does not
            // find it.
            Assert.False(await api.SoftDeleteAsync(documentId, deleterId));
        }

        // AND STILL IN THE DATABASE, bytes included. Retention is indefinite, so this row and its content
        // are kept permanently; the difference between a soft and a hard delete is visible ONLY here.
        Assert.Equal(1, await QueryScalar<long>(connectionString,
            $"SELECT COUNT(*) FROM documents WHERE id = '{documentId}' AND deleted_at IS NOT NULL"));
        Assert.Equal(deleterId.ToString(), (await QueryScalar<Guid>(connectionString,
            $"SELECT deleted_by_user_account_id FROM documents WHERE id = '{documentId}'")).ToString());
        Assert.Equal(payload.Length, await QueryScalar<int>(connectionString,
            $"SELECT octet_length(content) FROM document_contents WHERE document_id = '{documentId}'"));
    }

    // --- Helpers ---

    private static DbContextOptions<DocumentsDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<DocumentsDbContext>().UseNpgsql(connectionString).Options;

    private static DbContextOptions<DocumentsDbContext> Options(NpgsqlConnection connection) =>
        new DbContextOptionsBuilder<DocumentsDbContext>().UseNpgsql(connection).Options;

    private static StoreDocumentRequest Request(byte[] content, string fileName, Guid? ticketId = null) =>
        new(
            ticketId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            DocumentOrigin.CustomerUpload,
            fileName,
            "application/octet-stream",
            new MemoryStream(content),
            Guid.NewGuid());

    /// <summary>
    /// A PDF whose body is pseudo-random rather than zeros, so a truncation, a base64 round trip or a
    /// text-mode conversion cannot pass by accident: the hash of a run of zeros is the hash of a shorter
    /// run of zeros only if the length is also wrong, and that is far too easy a test to pass.
    /// </summary>
    private static byte[] MultiMegabytePdf(int length)
    {
        var content = DocumentsTestHarness.Pdf();
        var payload = new byte[length];
        content.CopyTo(payload, 0);

        var random = new Random(20260903);
        random.NextBytes(payload.AsSpan(content.Length));
        return payload;
    }

    private static string Insert(
        Guid? ticketId = null,
        long sizeBytes = 1024,
        string origin = "CustomerUpload",
        string? contentHash = null,
        string deletedAt = "NULL",
        string deletedBy = "NULL") =>
        "INSERT INTO documents (customer_id, ticket_id, origin, original_file_name, content_type, "
        + "size_bytes, content_hash, uploaded_by_user_account_id, deleted_at, "
        + "deleted_by_user_account_id) VALUES ("
        + $"gen_random_uuid(), '{ticketId ?? Guid.NewGuid()}', '{origin}', 'file.pdf', "
        + $"'application/pdf', {sizeBytes}, '{contentHash ?? new string('a', 64)}', "
        + $"gen_random_uuid(), {deletedAt}, {deletedBy})";

    private static async Task<bool> PostgresIsReachable()
    {
        try
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync();
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    private static Task ExecuteOnAdmin(string sql) => ExecuteOn(AdminConnectionString, sql);

    private static async Task ExecuteOn(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> QueryScalar<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task<List<string>> QueryStrings(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }
}
