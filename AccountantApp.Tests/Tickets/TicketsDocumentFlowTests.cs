using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Tests.Documents;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// Plan §11.2's document group. <c>IDocumentApi</c> AUTHORIZES NOTHING -- it will return the bytes of any
/// live document in the system given only its id -- so the security of every document in this application
/// is these four handlers performing the six steps of §0.3, and nothing else.
/// </summary>
public class TicketsDocumentFlowTests
{
    /// <summary>
    /// §11.3 test 5, and the only test in the slice that can fail an IDOR.
    ///
    /// The pairing is what matters: A TICKET THE CALLER MAY READ WITH A DOCUMENT THEY MAY NOT. Both ids
    /// arrive from the caller independently, so "the ticket check passed and the document was found" makes
    /// both halves look verified when neither has been tied to the other. Every test of the shape "a
    /// document on my own ticket downloads fine" passes against a handler with no step-5 check at all.
    ///
    /// The victim downloading their own document in the same test is what proves the 404 came from the
    /// pairing rather than from a document that was unreadable to everybody.
    /// </summary>
    [Fact]
    public async Task A_document_from_another_customers_ticket_is_404_even_on_a_ticket_the_caller_may_read()
    {
        var world = new TicketsWorld();

        var victimCustomer = world.Customers.AddActive();
        var (victim, victimEmployee) = world.NewCustomerSide(victimCustomer);
        var victimTicket = world.NewTicket(
            victimCustomer, Guid.Parse(victim.Id), victimEmployee.Id, TicketStatus.Submitted);

        var theirDocument = await world.StoreDocumentAsync(
            victimTicket, Guid.Parse(victim.Id), "payroll-january.pdf");

        var attackerCustomer = world.Customers.AddActive();
        var (attacker, attackerEmployee) = world.NewCustomerSide(attackerCustomer);
        var ownTicket = world.NewTicket(
            attackerCustomer, Guid.Parse(attacker.Id), attackerEmployee.Id, TicketStatus.Submitted);

        var denied = await Assert.ThrowsAsync<AppException>(() => world.DownloadDocument().Handle(
            new DownloadDocumentRequestDto
            {
                TicketId = ownTicket.Id, DocumentId = theirDocument.Id,
            },
            attacker,
            default));

        Assert.Equal(404, denied.StatusCode);

        // The same message as a missing id: "exists elsewhere" must not be distinguishable from "does not
        // exist", or the document id space becomes an enumerable directory.
        Assert.Equal("Document not found.", denied.Message);

        // Nothing was disclosed, so nothing is recorded as disclosed.
        Assert.Empty(world.Audit.WithAction(AuditActions.DocumentDownloaded));

        // The document is perfectly readable by the person entitled to it.
        var allowed = await world.DownloadDocument().Handle(
            new DownloadDocumentRequestDto
            {
                TicketId = victimTicket.Id, DocumentId = theirDocument.Id,
            },
            victim,
            default);

        Assert.Equal("payroll-january.pdf", allowed.FileName);
        Assert.NotEmpty(allowed.Content);
    }

    /// <summary>
    /// The same attack inside ONE Customer, which is the version that survives a Customer-scope-only fix:
    /// layer 2 hides a colleague's ticket from an Employee, so the document on it must be equally out of
    /// reach even though both people work for the same Customer.
    /// </summary>
    [Fact]
    public async Task An_Employee_cannot_reach_a_colleagues_document_through_their_own_ticket()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();

        var (colleague, colleagueEmployee) = world.NewCustomerSide(customerId, given: "Nikos");
        var colleagueTicket = world.NewTicket(
            customerId, Guid.Parse(colleague.Id), colleagueEmployee.Id, TicketStatus.Submitted);

        var document = await world.StoreDocumentAsync(colleagueTicket, Guid.Parse(colleague.Id));

        var (caller, callerEmployee) = world.NewCustomerSide(customerId, given: "Eleni");
        var ownTicket = world.NewTicket(
            customerId, Guid.Parse(caller.Id), callerEmployee.Id, TicketStatus.Submitted);

        // Paired with their own ticket: step 5 refuses it.
        var pairedWithOwn = await Assert.ThrowsAsync<AppException>(() => world.DownloadDocument()
            .Handle(
                new DownloadDocumentRequestDto
                {
                    TicketId = ownTicket.Id, DocumentId = document.Id,
                },
                caller,
                default));

        Assert.Equal(404, pairedWithOwn.StatusCode);

        // Paired with the ticket it really belongs to: visibility refuses it, at the same status code.
        var pairedWithTheirs = await Assert.ThrowsAsync<AppException>(() => world.DownloadDocument()
            .Handle(
                new DownloadDocumentRequestDto
                {
                    TicketId = colleagueTicket.Id, DocumentId = document.Id,
                },
                caller,
                default));

        Assert.Equal(404, pairedWithTheirs.StatusCode);
    }

    /// <summary>
    /// §4.11 rule 2, and the asymmetry the plan is explicit about: A CLOSED TICKET'S DOCUMENTS STAY
    /// READABLE. The ticket is read-only, not gone, and for a cancelled ticket the attachment is often the
    /// only copy the Customer has.
    /// </summary>
    [Theory]
    [InlineData(TicketStatus.Closed)]
    [InlineData(TicketStatus.Cancelled)]
    public async Task A_terminal_tickets_documents_can_still_be_listed_and_downloaded(string status)
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, status);

        var document = await world.StoreDocumentAsync(ticket, Guid.Parse(customer.Id));

        var listed = await world.ListTicketDocuments()
            .Handle(new ListTicketDocumentsRequestDto { TicketId = ticket.Id }, customer, default);

        Assert.Equal(document.Id, Assert.Single(listed).Id);

        var downloaded = await world.DownloadDocument().Handle(
            new DownloadDocumentRequestDto { TicketId = ticket.Id, DocumentId = document.Id },
            customer,
            default);

        Assert.NotEmpty(downloaded.Content);

        // Read-only means read-only in the other direction too: nothing may be added to a closed record.
        var refused = await Assert.ThrowsAsync<AppException>(() => world.UploadDocument().Handle(
            new UploadDocumentRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version,
                FileName = "late-addition.pdf",
                DeclaredContentType = "application/pdf",
                Content = new MemoryStream(DocumentsTestHarness.Pdf()),
            },
            customer,
            default));

        Assert.Equal(422, refused.StatusCode);
    }

    /// <summary>
    /// §4.11 rule 3 and §4.0 D: EVERY DOWNLOAD IS AUDITED, AND THE ENTRY IS COMMITTED BEFORE THE BYTES
    /// LEAVE. Streaming first and auditing afterwards is identical on the happy path and loses exactly the
    /// record an audit log exists to keep when the client disconnects mid-stream.
    /// </summary>
    [Fact]
    public async Task Every_download_is_audited_and_the_entry_is_committed_before_the_bytes_are_returned()
    {
        var recorder = new RecordingRequestTransaction();
        var world = new TicketsWorld(recorder);
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.Closed);

        var document = await world.StoreDocumentAsync(ticket, Guid.Parse(customer.Id));

        var beforeDownloads = recorder.CommitCount;

        await world.DownloadDocument().Handle(
            new DownloadDocumentRequestDto { TicketId = ticket.Id, DocumentId = document.Id },
            customer,
            default);

        await world.DownloadDocument().Handle(
            new DownloadDocumentRequestDto { TicketId = ticket.Id, DocumentId = document.Id },
            customer,
            default);

        // Twice, not once: a second read of the same document by the same person is a second disclosure.
        Assert.Equal(2, world.Audit.WithAction(AuditActions.DocumentDownloaded).Count());
        Assert.Equal(beforeDownloads + 2, recorder.CommitCount);

        var entry = world.Audit.WithAction(AuditActions.DocumentDownloaded).First();
        Assert.Equal(AuditTargets.Document, entry.TargetKind);
        Assert.Equal(document.Id.ToString(), entry.TargetId);
        Assert.Equal(customerId, entry.CustomerId);

        // The status at the moment of the read: "who read this after the ticket closed" is a question the
        // log has to answer, and the tickets row cannot answer it later.
        Assert.Equal(TicketStatus.Closed, ReadString(entry.After, "TicketStatus"));

        // A read of the ticket, but never a write of it.
        Assert.Equal(ticket.Version, world.Db.Tickets.Single().Version);
    }

    /// <summary>
    /// The origin comes from the ROLE. There is no <c>Origin</c> on the request DTO to read, which this
    /// test pins by asserting the property does not exist: a Customer able to set it would mark their own
    /// upload as an Accountant response and change what the ticket record appears to say.
    /// </summary>
    [Fact]
    public async Task The_upload_origin_is_derived_from_the_role_and_cannot_be_supplied()
    {
        Assert.Null(typeof(UploadDocumentRequestDto).GetProperty("Origin"));

        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.InReview,
            assignee: world.SeedAccount());

        var fromCustomer = await world.UploadDocument().Handle(
            new UploadDocumentRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version,
                FileName = "payslip.pdf",
                DeclaredContentType = "application/pdf",
                Content = new MemoryStream(DocumentsTestHarness.Pdf()),
            },
            customer,
            default);

        Assert.Equal("CustomerUpload", fromCustomer.Origin);
        Assert.Equal(ticket.Id, fromCustomer.TicketId);
        Assert.Equal(Guid.Parse(customer.Id), fromCustomer.UploadedByUserAccountId);

        // §4.11: an upload does NOT touch the ticket, so a form uploading three files in a row does not
        // have to re-read the ticket between them.
        Assert.Equal(ticket.Version, world.Db.Tickets.Single().Version);

        var accountant = world.NewAccountant(UserRole.AccountantUser);

        var fromOffice = await world.UploadDocument().Handle(
            new UploadDocumentRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version,
                FileName = "calculation.pdf",
                DeclaredContentType = "application/pdf",
                Content = new MemoryStream(DocumentsTestHarness.Pdf()),
            },
            accountant,
            default);

        Assert.Equal("AccountantResponse", fromOffice.Origin);
        Assert.Equal(2, world.Audit.WithAction(AuditActions.DocumentUploaded).Count());
    }

    /// <summary>
    /// §8 rule 7: the version is CHECKED on the document write paths even though they do not move it, so
    /// an upload against a ticket that has since been closed or cancelled underneath the caller is a 409
    /// rather than a write onto a view of the ticket that no longer exists.
    /// </summary>
    [Fact]
    public async Task An_upload_with_a_stale_version_is_409_and_stores_nothing()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.Draft);

        var rejected = await Assert.ThrowsAsync<AppException>(() => world.UploadDocument().Handle(
            new UploadDocumentRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version - 1,
                FileName = "payslip.pdf",
                DeclaredContentType = "application/pdf",
                Content = new MemoryStream(DocumentsTestHarness.Pdf()),
            },
            customer,
            default));

        Assert.Equal(409, rejected.StatusCode);
        Assert.Empty(world.DocumentsDb.Documents);
        Assert.Empty(world.Audit.WithAction(AuditActions.DocumentUploaded));
    }

    /// <summary>
    /// A rejected upload leaves NOTHING behind -- no metadata row, no bytes, no audit entry. The bytes live
    /// in PostgreSQL rather than on a volume precisely so that this is a transaction rather than a cleanup
    /// job, and there is no orphaned-file sweeper in this system because there can be no orphaned file.
    /// </summary>
    [Fact]
    public async Task An_upload_whose_content_fails_validation_leaves_no_row_and_no_bytes()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.Draft);

        // A PDF extension over HTML bytes: Documents sniffs the content and refuses it.
        var rejected = await Assert.ThrowsAsync<AppException>(() => world.UploadDocument().Handle(
            new UploadDocumentRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version,
                FileName = "invoice.pdf",
                DeclaredContentType = "application/pdf",
                Content = new MemoryStream(DocumentsTestHarness.Html()),
            },
            customer,
            default));

        Assert.Equal(422, rejected.StatusCode);
        Assert.Empty(world.DocumentsDb.Documents);
        Assert.Empty(world.DocumentsDb.DocumentContents);
        Assert.Empty(world.Audit.Entries);
    }

    // --- Deleting ---

    /// <summary>
    /// §4.11 rule 6, first half: a Customer-side actor may remove their OWN upload while the ticket is
    /// still theirs. A soft delete, so the row and the bytes both stay and it is the query filter that
    /// makes the document unreachable -- nothing in this system is hard-deleted (§9.2).
    /// </summary>
    [Fact]
    public async Task A_customer_side_uploader_removes_their_own_upload_from_a_draft()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.Draft);

        var document = await world.StoreDocumentAsync(ticket, Guid.Parse(customer.Id));

        var result = await world.DeleteDocument().Handle(
            new DeleteDocumentRequestDto
            {
                TicketId = ticket.Id, DocumentId = document.Id, Version = ticket.Version,
            },
            customer,
            default);

        Assert.Equal(document.Id, result.DocumentId);

        var entry = Assert.Single(world.Audit.WithAction(AuditActions.DocumentSoftDeleted));

        // The Before is the only remaining machine-readable record of what the document was: from now on
        // the row sits behind a query filter and no read path lifts it.
        Assert.Equal("payslip.pdf", ReadString(entry.Before, "OriginalFileName"));

        // Gone from the list and 404 on download, while the row and the bytes are both still there.
        Assert.Empty(await world.ListTicketDocuments()
            .Handle(new ListTicketDocumentsRequestDto { TicketId = ticket.Id }, customer, default));

        var denied = await Assert.ThrowsAsync<AppException>(() => world.DownloadDocument().Handle(
            new DownloadDocumentRequestDto { TicketId = ticket.Id, DocumentId = document.Id },
            customer,
            default));

        Assert.Equal(404, denied.StatusCode);
        Assert.Single(world.DocumentsDb.Documents.IgnoreQueryFilters());

        // A second delete of the same id is a 404, not a silent success: the caller's view is stale.
        var again = await Assert.ThrowsAsync<AppException>(() => world.DeleteDocument().Handle(
            new DeleteDocumentRequestDto
            {
                TicketId = ticket.Id, DocumentId = document.Id, Version = ticket.Version,
            },
            customer,
            default));

        Assert.Equal(404, again.StatusCode);
    }

    /// <summary>
    /// Rule 6, second half. "HAS NOT YET REACHED <c>InReview</c>" is not "is not <c>InReview</c>": once the
    /// Office has picked the ticket up, the Customer side cannot quietly remove a document the review is
    /// based on. 422 rather than 403 because this is a fact about the ticket that was different an hour
    /// ago, not a permanent fact about the caller.
    /// </summary>
    [Fact]
    public async Task A_customer_side_uploader_cannot_remove_their_upload_once_the_office_has_the_ticket()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.InReview,
            assignee: world.SeedAccount());

        var document = await world.StoreDocumentAsync(ticket, Guid.Parse(customer.Id));

        var refused = await Assert.ThrowsAsync<AppException>(() => world.DeleteDocument().Handle(
            new DeleteDocumentRequestDto
            {
                TicketId = ticket.Id, DocumentId = document.Id, Version = ticket.Version,
            },
            customer,
            default));

        Assert.Equal(422, refused.StatusCode);
        Assert.Empty(world.Audit.WithAction(AuditActions.DocumentSoftDeleted));

        // Still there, and still downloadable.
        Assert.Single(await world.ListTicketDocuments()
            .Handle(new ListTicketDocumentsRequestDto { TicketId = ticket.Id }, customer, default));
    }

    /// <summary>
    /// Rule 6 again: OWN upload. A Customer Admin may see the whole Customer's tickets, which is not the
    /// same as being allowed to remove what somebody else attached. 403 -- a permanent fact about the
    /// caller -- and not 404, because they can already see the document in the ticket's own list.
    /// </summary>
    [Fact]
    public async Task A_CustomerAdmin_cannot_remove_an_upload_they_did_not_make()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();

        var (admin, adminEmployee) = world.NewCustomerSide(
            customerId, UserRole.CustomerAdmin, given: "Kostas");

        // The Admin is the Creator, so they can see the Draft; the upload is somebody else's.
        var ticket = world.NewTicket(
            customerId, Guid.Parse(admin.Id), adminEmployee.Id, TicketStatus.Draft);

        var otherAccount = world.SeedAccount(UserRole.Employee);
        var document = await world.StoreDocumentAsync(ticket, otherAccount);

        var denied = await Assert.ThrowsAsync<AppException>(() => world.DeleteDocument().Handle(
            new DeleteDocumentRequestDto
            {
                TicketId = ticket.Id, DocumentId = document.Id, Version = ticket.Version,
            },
            admin,
            default));

        Assert.Equal(403, denied.StatusCode);
        Assert.Empty(world.Audit.WithAction(AuditActions.DocumentSoftDeleted));
    }

    /// <summary>
    /// Rule 6, the Accountant half: any document on a ticket they can see, whoever uploaded it and whatever
    /// the status -- short of terminal. This is the escape hatch the 422 above points the Customer at.
    /// </summary>
    [Fact]
    public async Task An_Accountant_removes_a_customers_upload_on_a_ticket_under_review()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.InReview,
            assignee: world.SeedAccount());

        var document = await world.StoreDocumentAsync(ticket, Guid.Parse(customer.Id));

        var accountant = world.NewAccountant(UserRole.AccountantUser);

        var result = await world.DeleteDocument().Handle(
            new DeleteDocumentRequestDto
            {
                TicketId = ticket.Id, DocumentId = document.Id, Version = ticket.Version,
            },
            accountant,
            default);

        Assert.Equal(document.Id, result.DocumentId);
        Assert.Single(world.Audit.WithAction(AuditActions.DocumentSoftDeleted));
    }

    /// <summary>
    /// Rule 2: a terminal ticket refuses a DELETE while it permits a download. The asymmetry is the point
    /// -- nothing may be removed from the record after the matter is over.
    /// </summary>
    [Fact]
    public async Task Not_even_an_Accountant_removes_a_document_from_a_closed_ticket()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.Closed);

        var document = await world.StoreDocumentAsync(ticket, Guid.Parse(customer.Id));

        var refused = await Assert.ThrowsAsync<AppException>(() => world.DeleteDocument().Handle(
            new DeleteDocumentRequestDto
            {
                TicketId = ticket.Id, DocumentId = document.Id, Version = ticket.Version,
            },
            world.NewAccountant(),
            default));

        Assert.Equal(422, refused.StatusCode);
    }

    /// <summary>§0.3 step 5 on the WRITE path: the same pairing check, or a valid session soft-deletes
    /// another Customer's document by naming a ticket of their own.</summary>
    [Fact]
    public async Task A_delete_naming_another_tickets_document_is_404()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var mine = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.Draft);

        var theirCustomer = world.Customers.AddActive();
        var (they, theirEmployee) = world.NewCustomerSide(theirCustomer);
        var theirTicket = world.NewTicket(
            theirCustomer, Guid.Parse(they.Id), theirEmployee.Id, TicketStatus.Draft);

        var theirDocument = await world.StoreDocumentAsync(theirTicket, Guid.Parse(they.Id));

        var denied = await Assert.ThrowsAsync<AppException>(() => world.DeleteDocument().Handle(
            new DeleteDocumentRequestDto
            {
                TicketId = mine.Id, DocumentId = theirDocument.Id, Version = mine.Version,
            },
            customer,
            default));

        Assert.Equal(404, denied.StatusCode);

        // Untouched: still live, still on its own ticket.
        Assert.Single(await world.ListTicketDocuments()
            .Handle(new ListTicketDocumentsRequestDto { TicketId = theirTicket.Id }, they, default));
    }

    /// <summary>
    /// The audit entries are anonymous types, so reading a member of one means reflecting. In a helper
    /// rather than inline: getting the name wrong silently yields null, and the resulting failure reads
    /// like a handler bug.
    /// </summary>
    private static string? ReadString(object? payload, string propertyName)
    {
        Assert.NotNull(payload);

        var property = payload.GetType().GetProperty(propertyName);
        Assert.NotNull(property);

        return (string?)property.GetValue(payload);
    }
}
