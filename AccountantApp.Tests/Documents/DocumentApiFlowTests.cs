using System.Text;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Documents.Application;
using AccountantApp.Api.Slices.Documents.Core;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;
using static AccountantApp.Tests.Documents.DocumentsTestHarness;

namespace AccountantApp.Tests.Documents;

/// <summary>
/// IDocumentApi's behaviour, against the in-memory provider.
///
/// What this file CANNOT show, and what DocumentsSchemaTests exists for: there is no BYTEA here, no CHECK
/// constraint, no foreign key, no partial index, and NO REAL TRANSACTION -- so the single most damaging
/// registration mistake in this slice (its own connection instead of the request's, at which point
/// EnlistAsync joins nothing and the bytes survive a rolled-back ticket operation) is invisible to
/// everything below. The closest this file gets is asserting that EnlistAsync was CALLED and CommitAsync
/// never was.
/// </summary>
public class DocumentApiFlowTests
{
    [Fact]
    public async Task StoreAsync_writes_both_tables_in_one_save_and_returns_the_summary()
    {
        await using var db = NewDb();
        var ticketId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var uploaderId = Guid.NewGuid();

        var summary = await NewApi(db).StoreAsync(Upload(
            Pdf(), "Statement.pdf",
            ticketId, customerId,
            DocumentOrigin.AccountantResponse,
            uploadedBy: uploaderId));

        Assert.Equal(ticketId, summary.TicketId);
        Assert.Equal(customerId, summary.CustomerId);
        Assert.Equal(DocumentOrigin.AccountantResponse, summary.Origin);
        Assert.Equal("Statement.pdf", summary.OriginalFileName);
        Assert.Equal("application/pdf", summary.ContentType);
        Assert.Equal(Pdf().Length, summary.SizeBytes);
        Assert.Equal(uploaderId, summary.UploadedByUserAccountId);
        Assert.NotEqual(default, summary.UploadedAt);

        // BOTH tables. A metadata row with no bytes downloads as a 500, and bytes with no metadata are
        // unreachable forever -- and neither shows up in a test that checks only the return value.
        Assert.Equal(1, await db.Documents.CountAsync());
        var content = await db.DocumentContents.SingleAsync();
        Assert.Equal(summary.Id, content.DocumentId);
        Assert.Equal(Pdf(), content.Content);
    }

    [Fact]
    public async Task StoreAsync_enlists_in_the_callers_transaction_and_never_commits()
    {
        await using var db = NewDb();
        var transaction = new RecordingRequestTransaction();

        await NewApi(db, transaction).StoreAsync(Upload(Pdf(), "a.pdf"));

        // Enlisted: the metadata row, the bytes, the ticket change and the audit entry are one atomic
        // unit, and that only holds if this context joined the transaction the caller opened.
        Assert.Equal(1, transaction.EnlistCount);
        // Never began one of its own, and NEVER COMMITTED. A write method that commits has ended the
        // caller's transaction early -- the ticket change that prompted the upload could then still fail
        // and roll back nothing.
        Assert.Equal(0, transaction.BeginCount);
        Assert.Equal(0, transaction.CommitCount);
    }

    [Fact]
    public async Task SoftDeleteAsync_enlists_in_the_callers_transaction_and_never_commits()
    {
        await using var db = NewDb();
        var transaction = new RecordingRequestTransaction();
        var api = NewApi(db, transaction);
        var stored = await api.StoreAsync(Upload(Pdf(), "a.pdf"));

        await api.SoftDeleteAsync(stored.Id, Guid.NewGuid());

        Assert.Equal(2, transaction.EnlistCount);
        Assert.Equal(0, transaction.CommitCount);
    }

    [Fact]
    public async Task A_multi_megabyte_file_round_trips_byte_identically()
    {
        await using var db = NewDb();
        var payload = Concat(Pdf(), Filler(3 * 1024 * 1024));
        var api = NewApi(db);

        var stored = await api.StoreAsync(Upload(payload, "big.pdf"));
        var opened = await api.OpenAsync(stored.Id);

        Assert.NotNull(opened);
        // Byte-identical, not merely the same length. And the hash recorded at upload is the hash of
        // these bytes, which is what makes the integrity check in OpenAsync meaningful at all.
        Assert.Equal(payload, opened!.Content);
        Assert.Equal(UploadValidation.ComputeHash(payload),
            (await db.Documents.AsNoTracking().SingleAsync()).ContentHash);
    }

    [Fact]
    public async Task A_txt_with_a_bom_round_trips_with_the_bom_still_in_it()
    {
        await using var db = NewDb();
        var withBom = Concat([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes("ποσό\n"));
        var api = NewApi(db);

        var stored = await api.StoreAsync(Upload(withBom, "bom.txt"));
        var opened = await api.OpenAsync(stored.Id);

        Assert.Equal("text/plain", stored.ContentType);
        // Skipped for validation, NEVER stripped from storage. A validator that normalised the bytes it
        // was checking would break the byte-identical round trip section 1 requires.
        Assert.Equal(withBom, opened!.Content);
        Assert.Equal(0xEF, opened.Content[0]);
    }

    [Fact]
    public async Task Two_documents_with_identical_content_are_stored_independently()
    {
        await using var db = NewDb();
        var api = NewApi(db);
        var payload = Pdf();

        var first = await api.StoreAsync(Upload(payload, "same.pdf"));
        var second = await api.StoreAsync(Upload(payload, "same.pdf"));

        // No deduplication, deliberately: the same PDF legitimately appears on two tickets at two
        // Customers, and one row's bytes serving two documents would make soft-deleting either one
        // either break the other or do nothing. content_hash is indexed but NOT unique.
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await db.DocumentContents.CountAsync());

        // Soft-deleting one leaves the other downloadable.
        Assert.True(await api.SoftDeleteAsync(first.Id, Guid.NewGuid()));
        Assert.Null(await api.OpenAsync(first.Id));
        Assert.NotNull(await api.OpenAsync(second.Id));
        Assert.Equal(payload, (await api.OpenAsync(second.Id))!.Content);
    }

    [Fact]
    public async Task ListByTicketAsync_returns_a_tickets_documents_oldest_first_and_no_bytes()
    {
        await using var db = NewDb();
        var api = NewApi(db);
        var ticketId = Guid.NewGuid();

        var first = await api.StoreAsync(Upload(Pdf(), "first.pdf", ticketId));
        var second = await api.StoreAsync(Upload(Png(), "second.png", ticketId));
        // Another ticket's document, which must not appear.
        await api.StoreAsync(Upload(Pdf(), "other.pdf"));

        var listed = await api.ListByTicketAsync(ticketId);

        Assert.Equal(new[] { first.Id, second.Id }, listed.Select(item => item.Id).ToArray());
        // Metadata only. DocumentSummary has no Content property, which is what makes this bounded.
        Assert.Equal("image/png", listed[1].ContentType);
    }

    [Fact]
    public async Task ListByTicketAsync_for_an_unknown_ticket_is_an_empty_list_not_an_error()
    {
        await using var db = NewDb();

        // The caller has already established that the ticket exists. A throw here would turn its 404
        // into a 500.
        Assert.Empty(await NewApi(db).ListByTicketAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_soft_deleted_document_disappears_from_every_read_but_keeps_its_bytes()
    {
        await using var db = NewDb();
        var api = NewApi(db);
        var ticketId = Guid.NewGuid();
        var deleterId = Guid.NewGuid();
        var stored = await api.StoreAsync(Upload(Pdf(), "gone.pdf", ticketId));

        Assert.True(await api.SoftDeleteAsync(stored.Id, deleterId));

        // Absent from the list, null from FindAsync, null from OpenAsync. All three come from the global
        // query filter rather than from three WHERE clauses somebody has to remember, which is the whole
        // point of declaring it on the entity: Tickets turns the null into a 404 -- never a 403, which
        // would confirm the document exists.
        Assert.Empty(await api.ListByTicketAsync(ticketId));
        Assert.Null(await api.FindAsync(stored.Id));
        Assert.Null(await api.OpenAsync(stored.Id));

        // AND THE BYTES ARE STILL THERE. This is the assertion that distinguishes a soft delete from a
        // hard delete wearing a flag: a hard delete produces exactly the same three results above and
        // passes every test that only looks at the API. Retention is indefinite, so the row and its
        // content are kept permanently.
        var row = await db.Documents.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.NotNull(row.DeletedAt);
        Assert.Equal(deleterId, row.DeletedByUserAccountId);
        Assert.True(row.IsDeleted);

        var content = await db.DocumentContents.AsNoTracking().SingleAsync();
        Assert.Equal(Pdf(), content.Content);
        Assert.NotEmpty(content.Content);
    }

    [Fact]
    public async Task A_link_issued_before_a_soft_delete_stops_working_after_it()
    {
        await using var db = NewDb();
        var api = NewApi(db);
        var stored = await api.StoreAsync(Upload(Pdf(), "link.pdf"));

        // The id was handed out and the bytes were readable.
        Assert.NotNull(await api.OpenAsync(stored.Id));

        await api.SoftDeleteAsync(stored.Id, Guid.NewGuid());

        // Re-checked at DOWNLOAD time, not at link-issue time. The same id, and now nothing.
        Assert.Null(await api.OpenAsync(stored.Id));
    }

    [Fact]
    public async Task Soft_deleting_an_already_deleted_document_returns_false()
    {
        await using var db = NewDb();
        var api = NewApi(db);
        var stored = await api.StoreAsync(Upload(Pdf(), "twice.pdf"));

        Assert.True(await api.SoftDeleteAsync(stored.Id, Guid.NewGuid()));

        // false -> 404 from Tickets. Not a 422, and not an idempotent 200: the filtered query does not
        // find it, and 404 is the correct answer for a document the caller can no longer see.
        Assert.False(await api.SoftDeleteAsync(stored.Id, Guid.NewGuid()));
        Assert.False(await api.SoftDeleteAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task Soft_deleting_never_touches_the_bytes_or_removes_a_row()
    {
        await using var db = NewDb();
        var api = NewApi(db);
        var stored = await api.StoreAsync(Upload(Pdf(), "kept.pdf"));

        await api.SoftDeleteAsync(stored.Id, Guid.NewGuid());

        // Row counts unchanged. A handler that set deleted_at and then nulled the bytes to save space
        // would have hard-deleted the document while leaving a row that claims otherwise.
        Assert.Equal(1, await db.Documents.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, await db.DocumentContents.CountAsync());
    }

    [Fact]
    public async Task SoftDeleteAsync_refuses_to_record_a_deletion_without_a_deleter()
    {
        await using var db = NewDb();
        var api = NewApi(db);
        var stored = await api.StoreAsync(Upload(Pdf(), "who.pdf"));

        // ck_documents_deletion rejects a deleted_at with no deleter in the database; this refuses to
        // reach it. A row that cannot answer "who deleted it" fails what the domain model requires of
        // the soft delete, and the bytes are gone from view with nobody accountable.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.SoftDeleteAsync(stored.Id, Guid.Empty));
    }

    [Fact]
    public async Task StoreAsync_rejects_an_origin_that_is_not_one_of_the_two_constants()
    {
        await using var db = NewDb();

        // The contract validates it. Origin is derived from the uploader's ROLE by Tickets and is never
        // client-supplied: a Customer who could set it would mark their own upload as an Accountant
        // response and change what the ticket appears to say.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewApi(db).StoreAsync(Upload(Pdf(), "a.pdf", origin: "Whatever")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewApi(db).StoreAsync(Upload(Pdf(), "a.pdf", origin: "customerupload")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewApi(db).StoreAsync(Upload(Pdf(), "a.pdf", origin: "")));

        Assert.Equal(0, await db.Documents.CountAsync());
    }

    [Fact]
    public async Task StoreAsync_fails_loudly_when_the_caller_passes_an_empty_customer_id()
    {
        await using var db = NewDb();

        // The shape of the mistake: Tickets passing user.CustomerId, which is NULL for an Accountant.
        // A row with an empty tenant id would be invisible to every scope filter and silently wrong,
        // which is worse than a 500.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewApi(db).StoreAsync(Upload(Pdf(), "a.pdf", customerId: Guid.Empty)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewApi(db).StoreAsync(Upload(Pdf(), "a.pdf", ticketId: Guid.Empty)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewApi(db).StoreAsync(Upload(Pdf(), "a.pdf", uploadedBy: Guid.Empty)));

        Assert.Equal(0, await db.Documents.CountAsync());
    }

    [Fact]
    public async Task A_rejected_upload_writes_no_row_and_no_bytes()
    {
        await using var db = NewDb();

        // Validation happens BEFORE the row is written and before the bytes are stored, so there is no
        // window in which an unvalidated type exists in the table.
        var exception = await Assert.ThrowsAsync<AppException>(
            () => NewApi(db).StoreAsync(Upload(Html(), "invoice.pdf")));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(0, await db.Documents.CountAsync());
        Assert.Equal(0, await db.DocumentContents.CountAsync());
    }

    [Fact]
    public async Task StoreAsync_sanitises_the_stored_file_name()
    {
        await using var db = NewDb();

        var stored = await NewApi(db).StoreAsync(Upload(Pdf(), "../../etc/passwd.pdf"));

        // The stored name is never used as a filesystem path.
        Assert.Equal("passwd.pdf", stored.OriginalFileName);
        Assert.DoesNotContain("/", stored.OriginalFileName);
        Assert.DoesNotContain("..", stored.OriginalFileName);
    }

    [Fact]
    public async Task StoreAsync_stores_the_sniffed_type_not_the_declared_one()
    {
        await using var db = NewDb();

        var stored = await NewApi(db).StoreAsync(Upload(
            Png(), "logo.png", declaredContentType: "application/pdf"));

        // The declared header said PDF. What is stored -- and therefore what the download will set -- is
        // what the leading bytes said.
        Assert.Equal("image/png", stored.ContentType);
    }

    [Fact]
    public async Task OpenAsync_throws_500_when_the_stored_bytes_do_not_match_the_recorded_hash()
    {
        await using var db = NewDb();
        var api = NewApi(db);
        var stored = await api.StoreAsync(Upload(Pdf(), "corrupt.pdf"));

        // Corrupt the bytes behind the API's back, which is what a failing disk or a bad restore looks
        // like from here.
        var content = await db.DocumentContents.SingleAsync();
        content.Content = Concat(Pdf(), [0x00]);
        await db.SaveChangesAsync();

        // THE ONE PLACE A 500 IS RIGHT. Corrupted bytes are a server fault, and serving silently
        // corrupted tax data is worse than failing -- the recipient cannot tell.
        var exception = await Assert.ThrowsAsync<AppException>(() => api.OpenAsync(stored.Id));
        Assert.Equal(500, exception.StatusCode);
    }

    [Fact]
    public async Task FindAsync_and_OpenAsync_return_null_for_an_unknown_id()
    {
        await using var db = NewDb();

        Assert.Null(await NewApi(db).FindAsync(Guid.NewGuid()));
        Assert.Null(await NewApi(db).OpenAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// PLAN SECTION 11.3 TEST 3. THIS IS NOT A BUG REPORT.
    ///
    /// IDocumentApi performs no authorization at all: no CurrentUser parameter, no Customer scope filter,
    /// and it will hand any caller the bytes of any live document in the system given only its id. It is
    /// built that way because this slice CANNOT evaluate a ticket's access rules -- 03-SliceInventory.md
    /// section 2 permits Documents -> Audit and nothing else, and Tickets -> Documents already exists, so
    /// Documents -> Tickets would be a cycle. A contract that PRETENDED to authorize would be worse than
    /// one that visibly does not, because the pretence is what a caller would rely on.
    ///
    /// The security boundary therefore lives in Tickets, on every request, in all six steps of section
    /// 0.3. This test exists so that if somebody later adds a filter INSIDE DocumentApi, it fails and
    /// forces the conversation about where that boundary lives -- instead of the boundary quietly moving
    /// to a place that cannot enforce it while Tickets keeps its now-redundant-looking checks and then
    /// loses them in a tidy-up.
    /// </summary>
    [Fact]
    public async Task OpenAsync_AppliesNoAuthorization_ByDesign()
    {
        await using var db = NewDb();
        var api = NewApi(db);

        var victimCustomerId = Guid.NewGuid();
        var victimsDocument = await api.StoreAsync(Upload(
            Pdf(), "payroll.pdf", customerId: victimCustomerId));

        // No user, no scope, no ticket: one Guid, and another Customer's payroll data comes back.
        var opened = await api.OpenAsync(victimsDocument.Id);

        Assert.NotNull(opened);
        Assert.Equal(Pdf(), opened!.Content);
        Assert.Equal(victimCustomerId, opened.Document.CustomerId);

        // The same for the metadata reads, and for the write paths.
        Assert.NotNull(await api.FindAsync(victimsDocument.Id));
        Assert.Single(await api.ListByTicketAsync(victimsDocument.TicketId));
        Assert.True(await api.SoftDeleteAsync(victimsDocument.Id, Guid.NewGuid()));
    }

    /// <summary>
    /// The other half of section 0.3 step 5, from this side: OpenAsync returns CustomerId and TicketId
    /// PRECISELY so that the caller can compare them with the ticket it authorized.
    ///
    /// The IDOR itself -- a ticket the caller may read paired with a document from a ticket they may not
    /// -- is a Tickets test, because Tickets owns the check and the route. What is assertable here is
    /// that the two fields a caller needs to make that comparison are present and correct; if they were
    /// absent from DocumentSummary the check could not be written at all.
    /// </summary>
    [Fact]
    public async Task A_summary_carries_the_ticket_and_customer_a_caller_must_compare_against()
    {
        await using var db = NewDb();
        var api = NewApi(db);
        var readableTicket = Guid.NewGuid();
        var unreadableTicket = Guid.NewGuid();
        var otherCustomer = Guid.NewGuid();

        var foreignDocument = await api.StoreAsync(Upload(
            Pdf(), "not-yours.pdf", unreadableTicket, otherCustomer));

        var opened = await api.OpenAsync(foreignDocument.Id);

        // This is the comparison Tickets must make, and it is the step that gets skipped because the
        // ticket check passed and the document was found, so both halves look verified.
        Assert.NotEqual(readableTicket, opened!.Document.TicketId);
        Assert.Equal(unreadableTicket, opened.Document.TicketId);
        Assert.Equal(otherCustomer, opened.Document.CustomerId);
    }

    private static byte[] Filler(int length)
    {
        var filler = new byte[length];
        for (var index = 0; index < length; index++)
            filler[index] = (byte)('a' + index % 26);
        return filler;
    }
}
