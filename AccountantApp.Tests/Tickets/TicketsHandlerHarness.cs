using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Documents.Infrastructure;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets;
using AccountantApp.Api.Slices.Tickets.Application.Handlers;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using AccountantApp.Tests.Documents;
using AccountantApp.Tests.Employees;
using AccountantApp.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// The handler-level fixtures: one in-memory <c>TicketsDbContext</c>, the REAL
/// <c>PermissionChecker</c> over the REAL <c>TicketsActionCatalogue</c>, the REAL <c>DocumentApi</c>
/// over an in-memory <c>DocumentsDbContext</c>, and doubles for the four remaining cross-slice
/// contracts.
///
/// The permission checker is real on purpose. Every one of these handlers opens with
/// <c>RequireAsync</c>, and a permissive double would make the whole "who may call" half of the
/// authorization matrix untested -- including the two rows (internal notes, the pickup queue) where the
/// catalogue is the ONLY thing that denies the operation, because the handlers deliberately carry no
/// role branch.
///
/// <c>IDocumentApi</c> is real for the same reason: the document tests are about a soft-deleted row
/// being absent from a list and a foreign document id producing a 404, and a hand-written double would
/// be asserting my own idea of those rules rather than the shipped ones.
///
/// <c>CreateTicketHandler</c> IS now here. It used to be the one exclusion: it depended on the concrete
/// <c>TicketReferenceAllocator</c>, which is sealed, non-virtual and implemented as one raw
/// <c>INSERT … ON CONFLICT … RETURNING</c> that the in-memory provider cannot execute — so the handler
/// that decides all six of a ticket's permanent values was verified by reading only. It now depends on
/// <c>ITicketReferenceAllocator</c>, and <c>SequentialReferenceAllocator</c> below stands in.
///
/// WHAT THAT STILL DOES NOT COVER, and the seam must not be mistaken for covering it: the atomicity of
/// the real statement. Fifty concurrent creations producing fifty distinct references (success criterion
/// 4) is a claim about <c>ON CONFLICT DO UPDATE … RETURNING</c> holding a row lock, it is unverifiable
/// without PostgreSQL, and it lives in <c>TicketsSchemaTests</c> — which SKIPS on this machine.
/// </summary>
internal sealed class TicketsWorld
{
    public TicketsWorld(IRequestTransaction? transaction = null)
    {
        Db = TicketsTestHarness.NewDb();
        DocumentsDb = DocumentsTestHarness.NewDb();
        Transaction = transaction ?? new NoOpRequestTransaction();
        Documents = DocumentsTestHarness.NewApi(DocumentsDb, Transaction);
        Permissions = new PermissionChecker(
            [new TicketsActionCatalogue()], Audit, NullLogger<PermissionChecker>.Instance);
    }

    public TicketsDbContext Db { get; }
    public DocumentsDbContext DocumentsDb { get; }
    public IRequestTransaction Transaction { get; }
    public DocumentApi Documents { get; }
    public PermissionChecker Permissions { get; }

    public TestAuditApi Audit { get; } = new();
    public FakeTicketEmployeeApi Employees { get; } = new();
    public FakeIdentityApi Identity { get; } = new();
    public FakeCustomerApi Customers { get; } = new();
    public FakeTicketTypesApi TicketTypes { get; } = new();
    public FakeNotificationApi Notifications { get; } = new();

    public SequentialReferenceAllocator References { get; } = new();

    // --- Handlers. One factory each, so a constructor change is one edit rather than forty. ---

    public CreateTicketHandler CreateTicket() => new(
        Db, Permissions, Transaction, Audit, Employees, TicketTypes, Identity, Notifications, References);

    public GetTicketHandler GetTicket() =>
        new(Db, Permissions, Employees, Customers, Identity, TicketTypes);

    public ListTicketsHandler ListTickets() =>
        new(Db, Permissions, Employees, Customers, Identity);

    public ListPickupQueueHandler ListPickupQueue() =>
        new(Db, Permissions, Employees, Customers, Identity);

    public SubmitTicketHandler SubmitTicket() => new(
        Db, Permissions, Transaction, Audit, Employees, TicketTypes, Identity, Notifications);

    public SubmitRevisionHandler SubmitRevision() => new(
        Db, Permissions, Transaction, Audit, Employees, TicketTypes, Documents, Notifications);

    public VerifyFieldHandler VerifyField() => new(
        Db, Permissions, Transaction, Audit, Employees, TicketTypes, Notifications);

    public PickupTicketHandler PickupTicket() => new(
        Db, Permissions, Transaction, Audit, Employees, Identity, Notifications);

    public AssignTicketHandler AssignTicket() => new(
        Db, Permissions, Transaction, Audit, Employees, Identity, Notifications);

    public RequestInformationHandler RequestInformation() =>
        new(Db, Permissions, Transaction, Audit, Employees, Notifications);

    public AnswerTicketHandler AnswerTicket() => new(
        Db, Permissions, Transaction, Audit, Employees, TicketTypes, Notifications);

    public CloseTicketHandler CloseTicket() => new(
        Db, Permissions, Transaction, Audit, Employees, TicketTypes, Notifications);

    public ReturnToReviewHandler ReturnToReview() =>
        new(Db, Permissions, Transaction, Audit, Employees);

    public SetPriorityHandler SetPriority() =>
        new(Db, Permissions, Transaction, Audit, Employees);

    public SetDueDateHandler SetDueDate() =>
        new(Db, Permissions, Transaction, Audit, Employees);

    public PostMessageHandler PostMessage() => new(
        Db, Permissions, Transaction, Audit, Employees, Documents, Notifications);

    public CancelTicketHandler CancelTicket() =>
        new(Db, Permissions, Transaction, Audit, Employees, Notifications);

    public UploadDocumentHandler UploadDocument() => new(
        Db, Permissions, Transaction, Audit, Employees, Identity, Documents);

    public ListTicketDocumentsHandler ListTicketDocuments() =>
        new(Db, Permissions, Employees, Identity, Documents);

    public DownloadDocumentHandler DownloadDocument() =>
        new(Db, Permissions, Transaction, Audit, Employees, Documents);

    public DeleteDocumentHandler DeleteDocument() =>
        new(Db, Permissions, Transaction, Audit, Employees, Documents);

    // --- Fixtures ---

    /// <summary>
    /// An Accountant session backed by a REAL account row in the Identity double, because several
    /// handlers resolve the caller's own <c>AccountSummary</c> for a system message and 403 without it.
    /// </summary>
    public CurrentUser NewAccountant(
        UserRole role = UserRole.AccountantAdmin, string status = "Active")
    {
        var accountId = SeedAccount(role, status);
        return TicketsTestHarness.Accountant(accountId, role);
    }

    /// <summary>
    /// An account with no Employee record and no session -- an Accountant who can be ASSIGNED work.
    /// Seeded through the Employees harness's Identity double, whose Seed takes an Employee id it does
    /// not need here, so a throwaway one is passed.
    /// </summary>
    public Guid SeedAccount(UserRole role = UserRole.AccountantUser, string status = "Active") =>
        Identity.Seed(Guid.NewGuid(), role, status, $"{Guid.NewGuid():N}@office.example");

    /// <summary>
    /// A Customer-side session whose <c>CurrentUser.Id</c> is an ACCOUNT id and whose Employee record
    /// resolves from that account id -- the only shape in which visibility layer 2 can be tested
    /// honestly. Handing over a CurrentUser carrying the EMPLOYEE id would test the bug rather than the
    /// rule.
    /// </summary>
    public (CurrentUser User, EmployeeSummary Employee) NewCustomerSide(
        Guid customerId,
        UserRole role = UserRole.Employee,
        string status = "Active",
        string given = "Maria",
        string family = "Papadopoulou")
    {
        var accountId = SeedAccount(role);
        var employee = Employees.Add(customerId, accountId, status, given, family);
        return (TicketsTestHarness.CustomerSide(accountId, role, customerId), employee);
    }

    /// <summary>
    /// A ticket row, plus the ticket type version its <c>ticket_type_version_id</c> points at, both
    /// saved. The version matters more than it looks: seven handlers resolve it through
    /// <c>ITicketTypesApi</c> and 422 when it does not resolve, so a fixture that skipped it would fail
    /// for a reason unrelated to the rule under test.
    /// </summary>
    public Ticket NewTicket(
        Guid customerId,
        Guid creatorAccountId,
        Guid subjectEmployeeId,
        string status = TicketStatus.Draft,
        Guid? assignee = null,
        TicketTypeDetailDto? type = null,
        string? reference = null)
    {
        var descriptor = TicketTypes.Add(type ?? TicketsTestHarness.TypeWith());

        var ticket = TicketsTestHarness.NewTicket(
            customerId, creatorAccountId, subjectEmployeeId, status, assignee, reference);

        ticket.TicketTypeId = descriptor.Id;
        ticket.TicketTypeVersionId = descriptor.VersionId;

        Db.Tickets.Add(ticket);
        Db.SaveChanges();
        return ticket;
    }

    /// <summary>
    /// A revision with its field values, made current. Sequence numbers are assigned from what is
    /// already stored, so a fixture cannot accidentally create two revision 1s -- the one thing
    /// <c>uq_ticket_revisions_sequence</c> would catch in PostgreSQL and the in-memory provider would
    /// not.
    /// </summary>
    public TicketRevision AddRevision(
        Ticket ticket,
        Guid submittedByAccountId,
        params FieldValue[] values)
    {
        var sequence = Db.TicketRevisions.Count(revision => revision.TicketId == ticket.Id) + 1;

        var revision = new TicketRevision
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            SequenceNumber = sequence,
            SubmittedByUserAccountId = submittedByAccountId,
            SubmittedAt = TicketsTestHarness.Now,
        };

        foreach (var value in values)
        {
            value.TicketRevisionId = revision.Id;
            revision.FieldValues.Add(value);
        }

        Db.TicketRevisions.Add(revision);
        ticket.CurrentRevisionId = revision.Id;
        Db.SaveChanges();
        return revision;
    }

    public static FieldValue Value(
        string fieldKey,
        string? text = null,
        decimal? number = null,
        DateOnly? date = null,
        Guid? documentId = null,
        bool isCarriedForward = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            FieldKey = fieldKey,
            ValueText = text,
            ValueNumber = number,
            ValueDate = date,
            ValueDocumentId = documentId,
            IsCarriedForward = isCarriedForward,
            CreatedAt = TicketsTestHarness.Now,
        };

    public FieldVerification Accept(
        FieldValue value, Guid verifierAccountId, DateTimeOffset verifiedAt)
    {
        var verification = new FieldVerification
        {
            Id = Guid.NewGuid(),
            FieldValueId = value.Id,
            Outcome = VerificationOutcome.Accepted,
            VerifiedByUserAccountId = verifierAccountId,
            VerifiedAt = verifiedAt,
        };

        Db.FieldVerifications.Add(verification);
        Db.SaveChanges();
        return verification;
    }

    public FieldVerification Reject(
        FieldValue value, Guid verifierAccountId, DateTimeOffset verifiedAt, string reason = "Illegible")
    {
        var verification = new FieldVerification
        {
            Id = Guid.NewGuid(),
            FieldValueId = value.Id,
            Outcome = VerificationOutcome.Rejected,
            RejectionReason = reason,
            VerifiedByUserAccountId = verifierAccountId,
            VerifiedAt = verifiedAt,
        };

        Db.FieldVerifications.Add(verification);
        Db.SaveChanges();
        return verification;
    }

    /// <summary>
    /// A stored document on a ticket, through the real <c>DocumentApi</c>, so the row carries a sniffed
    /// content type and a sanitised file name exactly as a live upload would.
    /// </summary>
    public async Task<DocumentSummary> StoreDocumentAsync(
        Ticket ticket,
        Guid uploaderAccountId,
        string fileName = "payslip.pdf",
        string origin = "CustomerUpload") =>
        await Documents.StoreAsync(new StoreDocumentRequest(
            ticket.Id,
            ticket.CustomerId,
            origin,
            fileName,
            "application/pdf",
            new MemoryStream(DocumentsTestHarness.Pdf()),
            uploaderAccountId));
}

/// <summary>
/// Hands out references in order, through the SHIPPED formatter.
///
/// It calls <c>TicketReferenceAllocator.Format</c> rather than writing <c>$"TKT-{year}-{n:D6}"</c> again,
/// so a test asserting the shape of a reference is asserting the production formatter and not this
/// double's idea of it. Six-digit padding is the part a client formats back, and a double that padded to
/// five would make a test of the format pass while the shipped format differed.
///
/// It does NOT emulate the counter table: no per-year restart, no persistence, no locking. It counts, and
/// counting is enough for "the handler stamps whatever it is given, once per ticket, inside the
/// transaction". The concurrency guarantee is <c>ON CONFLICT</c>'s and is tested only against real
/// PostgreSQL.
/// </summary>
internal sealed class SequentialReferenceAllocator : ITicketReferenceAllocator
{
    private int _sequence;

    public List<int> RequestedYears { get; } = [];

    public Task<string> AllocateAsync(int year, CancellationToken ct)
    {
        RequestedYears.Add(year);
        return Task.FromResult(TicketReferenceAllocator.Format(year, ++_sequence));
    }
}

/// <summary>
/// The Employee directory, standing in for the Employees slice. It applies NO scope filter, exactly as
/// the real contract documents -- a double that filtered by Customer would hide the case where a Tickets
/// handler passes an id it has not authorized, which is the failure the contract warns about.
/// </summary>
internal sealed class FakeTicketEmployeeApi : IEmployeeApi
{
    private readonly Dictionary<Guid, EmployeeSummary> _byId = [];

    public int FindManyCallCount { get; private set; }

    public EmployeeSummary Add(
        Guid customerId,
        Guid? userAccountId = null,
        string status = "Active",
        string given = "Maria",
        string family = "Papadopoulou")
    {
        var summary = new EmployeeSummary(
            Guid.NewGuid(), customerId, given, family, status,
            userAccountId is not null, userAccountId);

        _byId[summary.Id] = summary;
        return summary;
    }

    public Task<EmployeeSummary?> FindAsync(Guid employeeId, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(employeeId, out var summary) ? summary : null);

    public Task<IReadOnlyDictionary<Guid, EmployeeSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> employeeIds, CancellationToken ct = default)
    {
        if (employeeIds.Count > 500)
            throw new InvalidOperationException("At most 500 employee ids may be requested.");

        FindManyCallCount++;
        return Task.FromResult<IReadOnlyDictionary<Guid, EmployeeSummary>>(
            employeeIds.Distinct()
                .Where(_byId.ContainsKey)
                .ToDictionary(id => id, id => _byId[id]));
    }

    // Fail closed: an unknown id is false, never true and never a throw.
    public Task<bool> IsActiveAsync(Guid employeeId, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(employeeId, out var summary) && summary.IsActive);

    public Task<EmployeeSummary?> FindByAccountAsync(
        Guid userAccountId, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(
            summary => summary.UserAccountId == userAccountId));

    public Task<PaginatedResponse<EmployeeSummary>> ListActiveByCustomerAsync(
        Guid customerId,
        int pageNumber = 1,
        int pageSize = PaginatedQuery.DefaultPageSize,
        CancellationToken ct = default)
    {
        var rows = _byId.Values
            .Where(summary => summary.CustomerId == customerId && summary.IsActive)
            .ToList();

        return Task.FromResult(new PaginatedResponse<EmployeeSummary>
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = rows.Count,
            TotalPages = 1,
            Items = rows,
        });
    }
}

/// <summary>
/// The ticket type descriptors, standing in for the TicketTypes slice.
///
/// IT PERFORMS THE ROLE STRIP ITSELF, and that is the whole reason it is not a trivial dictionary: the
/// real contract removes every <c>IsVisibleToCustomer = false</c> descriptor for a Customer-side role and
/// returns NULL for a type outside the caller's audience. Two of this slice's rules exist only because of
/// those behaviours -- <c>TicketAccess.DescriptorAudienceForRules</c> and the
/// <c>AllowEmployeeToOpen</c> fallback in <c>ResolveResponseVersionAsync</c> -- so a permissive double
/// would leave both untested and both are load-bearing.
/// </summary>
internal sealed class FakeTicketTypesApi : ITicketTypesApi
{
    private readonly List<TicketTypeDetailDto> _versions = [];

    public TicketTypeDetailDto Add(TicketTypeDetailDto detail)
    {
        if (detail.VersionId == Guid.Empty)
            detail.VersionId = Guid.NewGuid();

        _versions.Add(detail);
        return detail;
    }

    public Task<TicketTypeDetailDto?> GetTicketTypeAsync(
        Guid ticketTypeId, UserRole callerRole, CancellationToken ct) =>
        Task.FromResult(Strip(
            _versions.FirstOrDefault(version => version.Id == ticketTypeId && version.IsActive),
            callerRole));

    public Task<TicketTypeDetailDto?> GetTicketTypeVersionAsync(
        Guid ticketTypeId, int versionNumber, UserRole callerRole, CancellationToken ct) =>
        Task.FromResult(Strip(
            _versions.FirstOrDefault(version => version.Id == ticketTypeId
                                             && version.VersionNumber == versionNumber),
            callerRole));

    public Task<TicketTypeDetailDto?> GetVersionByIdAsync(
        Guid ticketTypeVersionId, UserRole callerRole, CancellationToken ct) =>
        Task.FromResult(Strip(
            _versions.FirstOrDefault(version => version.VersionId == ticketTypeVersionId), callerRole));

    public Task<List<TicketTypeListItemDto>> ListAvailableTypesAsync(
        UserRole callerRole, CancellationToken ct) =>
        Task.FromResult<List<TicketTypeListItemDto>>([]);

    /// <summary>
    /// A COPY with the Customer-side view applied, never the stored object: a strip that mutated the
    /// fixture would leak into the next call in the same test and make an Accountant read look narrowed.
    /// </summary>
    private static TicketTypeDetailDto? Strip(TicketTypeDetailDto? detail, UserRole callerRole)
    {
        if (detail is null)
            return null;

        if (callerRole is UserRole.AccountantAdmin or UserRole.AccountantUser)
            return detail;

        // The audience gate: a type an Employee may not open is not readable by an Employee at all.
        if (callerRole == UserRole.Employee && !detail.AllowEmployeeToOpen)
            return null;

        return new TicketTypeDetailDto
        {
            Id = detail.Id,
            VersionId = detail.VersionId,
            Code = detail.Code,
            DisplayName = detail.DisplayName,
            Description = detail.Description,
            Category = detail.Category,
            AllowEmployeeToOpen = detail.AllowEmployeeToOpen,
            AllowSubjectOtherThanCreator = detail.AllowSubjectOtherThanCreator,
            IsActive = detail.IsActive,
            CurrentVersionNumber = detail.CurrentVersionNumber,
            VersionNumber = detail.VersionNumber,
            Fields = [.. detail.Fields.Where(field => field.IsVisibleToCustomer)],
            CreatedAt = detail.CreatedAt,
            UpdatedAt = detail.UpdatedAt,
        };
    }
}

/// <summary>
/// Records what was raised. It collapses duplicate recipients per call, like the real
/// <c>NotifyManyAsync</c>, so "the Creator and the Subject are the same person" produces one
/// notification here as well -- otherwise a test asserting a count would pass against a handler that
/// notified somebody twice.
/// </summary>
internal sealed class FakeNotificationApi : INotificationApi
{
    public List<NotificationRequest> Sent { get; } = [];

    public Task<Guid> NotifyAsync(NotificationRequest request, CancellationToken ct = default)
    {
        Sent.Add(request);
        return Task.FromResult(Guid.NewGuid());
    }

    public Task<int> NotifyManyAsync(
        IReadOnlyCollection<NotificationRequest> requests, CancellationToken ct = default)
    {
        var distinct = requests
            .GroupBy(request => (request.RecipientUserId, request.EventKind))
            .Select(group => group.First())
            .ToList();

        Sent.AddRange(distinct);
        return Task.FromResult(distinct.Count);
    }

    public IEnumerable<NotificationRequest> For(Guid recipientAccountId) =>
        Sent.Where(request => request.RecipientUserId == recipientAccountId.ToString());
}
