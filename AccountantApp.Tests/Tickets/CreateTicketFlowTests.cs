using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// <c>CreateTicketHandler</c>, plan §4.1 — the handler that had no test at all until
/// <c>ITicketReferenceAllocator</c> gave it a seam.
///
/// It is worth its own file because it is the only place six of a ticket's values are ever decided:
/// Customer, Type, Type version, Creator, Subject and Preceded-by (01-DomainModel.md §3). There is no
/// update handler that accepts any of them, so a mistake here is permanent — the ticket has to be
/// cancelled and reopened. Every rule below is one of those, or the transaction that keeps them
/// consistent.
///
/// WHAT THESE TESTS CANNOT SEE:
///
///   - the reference's ATOMICITY. <c>SequentialReferenceAllocator</c> counts; the shipped allocator's
///     guarantee is <c>ON CONFLICT DO UPDATE … RETURNING</c> holding a row lock, and success criterion 4
///     (fifty concurrent creations, fifty distinct references) lives in <c>TicketsSchemaTests</c> and
///     SKIPS here. The format is real — the double calls
///     <c>TicketReferenceAllocator.Format</c> — but the concurrency is not.
///   - the two-<c>SaveChanges</c> rollback of rule 6. <c>NoOpRequestTransaction</c> makes the transaction
///     a no-op, so "a failure leaves neither the ticket nor the revision" is unverified, and criterion 5
///     covers it against real PostgreSQL only.
///   - <c>uq_ticket_revisions_sequence</c> and <c>ck_tickets_assignee</c>, the database backstops behind
///     rules 6 and 10.
/// </summary>
public sealed class CreateTicketFlowTests
{
    private static CreateTicketRequestDto Request(
        TicketTypeDetailDto type,
        Guid subjectEmployeeId,
        bool submitImmediately = false,
        Guid? precededBy = null,
        string? note = null,
        params TicketFieldValueInputDto[] fieldValues) =>
        new()
        {
            TicketTypeId = type.Id,
            SubjectEmployeeId = subjectEmployeeId,
            SubmitImmediately = submitImmediately,
            PrecededByTicketId = precededBy,
            Note = note,
            FieldValues = [.. fieldValues],
        };

    private static TicketFieldValueInputDto Text(string key, string value) =>
        new() { FieldKey = key, Text = value };

    /// <summary>
    /// <c>CurrentUser.Id</c> is a STRING — the claim as it arrives — while every column in this slice is a
    /// <c>Guid</c>. Parsing here rather than comparing the string keeps the tests below asserting against
    /// the same type the database stores.
    /// </summary>
    private static Guid AccountId(CurrentUser user) => Guid.Parse(user.Id);

    /// <summary>
    /// The happy path, end to end, which is what did not exist before: a Draft with revision 1, a
    /// reference in the shipped format, the derived title, no Assignee, and one audit entry.
    /// </summary>
    [Fact]
    public async Task An_employee_opens_a_draft_about_themselves()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (user, employee) = world.NewCustomerSide(customerId);

        // AllowEmployeeToOpen must be set for an EMPLOYEE caller: the type is otherwise outside their
        // audience entirely and GetTicketTypeAsync answers null, which is a 422 before any of this runs.
        var type = TicketsTestHarness.TypeWith(TicketsTestHarness.Field("iban", "SingleLineText"));
        type.AllowEmployeeToOpen = true;
        world.TicketTypes.Add(type);

        var detail = await world.CreateTicket().Handle(
            Request(type, employee.Id, fieldValues: Text("iban", "GR1601101250000000012300695")),
            user,
            CancellationToken.None);

        var ticket = await world.Db.Tickets.SingleAsync();

        Assert.Equal(TicketStatus.Draft, ticket.Status);

        // Rule 10: a Draft has no Assignee. ck_tickets_assignee is the backstop and it is not reachable
        // here, so the handler's own behaviour is what this asserts.
        Assert.Null(ticket.AssigneeUserAccountId);
        Assert.Equal(TicketPriority.Normal, ticket.Priority);

        // Rule 1: the Customer is RESOLVED. Nothing in the request carries it.
        Assert.Equal(customerId, ticket.CustomerId);

        // The Creator is the caller's ACCOUNT id, not their Employee id. Both are Guids, so nothing
        // would complain if these were swapped, and every visibility check keyed on the Creator would
        // then be comparing the wrong pair.
        Assert.Equal(AccountId(user), ticket.CreatorUserAccountId);
        Assert.Equal(employee.Id, ticket.SubjectEmployeeId);
        Assert.NotEqual(ticket.CreatorUserAccountId, ticket.SubjectEmployeeId);

        // The VERSION's Guid on one column and the TYPE's on the other, which is the mix-up that
        // produces a ticket whose descriptors can never be resolved.
        Assert.Equal(type.Id, ticket.TicketTypeId);
        Assert.Equal(type.VersionId, ticket.TicketTypeVersionId);
        Assert.NotEqual(ticket.TicketTypeId, ticket.TicketTypeVersionId);

        // Rule 9: derived from the type name and the Subject, so lists read without opening each ticket.
        Assert.Equal($"{type.DisplayName} — {employee.FullName}", ticket.Title);

        Assert.Equal(1, ticket.Version);
        Assert.Null(ticket.PrecededByTicketId);

        // Rule 6: current_revision_id is set, which needs the second SaveChanges because the two tables
        // reference each other. A handler that skipped it leaves a ticket whose field values are
        // unreachable through every read path.
        var revision = await world.Db.TicketRevisions.Include(r => r.FieldValues).SingleAsync();
        Assert.Equal(revision.Id, ticket.CurrentRevisionId);
        Assert.Equal(1, revision.SequenceNumber);
        Assert.Equal(AccountId(user), revision.SubmittedByUserAccountId);
        Assert.Equal("GR1601101250000000012300695", revision.FieldValues.Single().ValueText);

        // The reference: the shipped format, allocated for the year the handler resolved, once.
        Assert.Equal(TicketReferenceAllocator.Format(DateTimeOffset.UtcNow.Year, 1), ticket.Reference);
        Assert.Equal(DateTimeOffset.UtcNow.Year, Assert.Single(world.References.RequestedYears));

        var audit = Assert.Single(world.Audit.WithAction(AuditActions.TicketCreated));
        Assert.Equal(ticket.Id.ToString(), audit.TargetId);
        Assert.Equal(ticket.CustomerId, audit.CustomerId);

        // A Draft is nobody's work yet, so nothing is raised. The Office hears about it on submission.
        Assert.Empty(world.Notifications.Sent);

        Assert.Equal(ticket.Id, detail.Id);
        Assert.Equal(employee.FullName, detail.SubjectName);
    }

    /// <summary>
    /// The reference is allocated once per ticket and never reused, and it is stamped even on a Draft —
    /// which is the point of allocating inside the creation transaction rather than at submission.
    /// </summary>
    [Fact]
    public async Task Each_ticket_gets_its_own_reference()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (user, employee) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);
        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith());

        await world.CreateTicket().Handle(Request(type, employee.Id), user, CancellationToken.None);
        await world.CreateTicket().Handle(Request(type, employee.Id), user, CancellationToken.None);

        var references = await world.Db.Tickets.Select(ticket => ticket.Reference).ToListAsync();

        Assert.Equal(2, references.Count);
        Assert.Equal(2, references.Distinct().Count());
        Assert.All(references, reference => Assert.StartsWith("TKT-", reference));
    }

    /// <summary>
    /// Rule 1 from the other side: an ACCOUNTANT's ticket lands under the SUBJECT's Customer, because
    /// the Employee already determines it. An Accountant has no <c>CurrentUser.CustomerId</c> at all, so
    /// a handler that read the caller's would file every Office-opened ticket nowhere.
    /// </summary>
    [Fact]
    public async Task An_accountant_gets_the_customer_from_the_subject()
    {
        var world = new TicketsWorld();
        var accountant = world.NewAccountant();
        var subjectCustomerId = Guid.NewGuid();
        var subject = world.Employees.Add(subjectCustomerId);
        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith());

        Assert.Null(accountant.CustomerId);

        await world.CreateTicket().Handle(
            Request(type, subject.Id), accountant, CancellationToken.None);

        var ticket = await world.Db.Tickets.SingleAsync();
        Assert.Equal(subjectCustomerId, ticket.CustomerId);
        Assert.Equal(AccountId(accountant), ticket.CreatorUserAccountId);
    }

    /// <summary>
    /// Rule 3: a Subject at ANOTHER Customer is 404, not 403. A 403 confirms the Employee exists, which
    /// is exactly what a caller probing ids must not learn — and the id is a Guid, so the only way to
    /// discover one is a response that distinguishes "not yours" from "not there".
    /// </summary>
    [Fact]
    public async Task A_subject_at_another_customer_is_404_and_not_403()
    {
        var world = new TicketsWorld();
        var (user, _) = world.NewCustomerSide(Guid.NewGuid(), UserRole.CustomerAdmin);
        var stranger = world.Employees.Add(Guid.NewGuid());
        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith());

        var refused = await Assert.ThrowsAsync<AppException>(() => world.CreateTicket().Handle(
            Request(type, stranger.Id), user, CancellationToken.None));

        Assert.Equal(404, refused.StatusCode);
        Assert.Empty(world.Db.Tickets);

        // Indistinguishable from an id that exists nowhere, which is the requirement.
        var missing = await Assert.ThrowsAsync<AppException>(() => world.CreateTicket().Handle(
            Request(type, Guid.NewGuid()), user, CancellationToken.None));

        Assert.Equal(404, missing.StatusCode);
        Assert.Equal(refused.Message, missing.Message);
    }

    /// <summary>
    /// Rule 5: an Employee opens tickets about THEMSELVES. Not a colleague at the same Customer — so
    /// this cannot be caught by the Customer check above, and it is 403 rather than 404 because the
    /// caller may legitimately see this colleague through the Employees slice.
    /// </summary>
    [Fact]
    public async Task An_employee_may_not_open_a_ticket_about_a_colleague()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (user, _) = world.NewCustomerSide(customerId);
        var colleague = world.Employees.Add(customerId);
        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith());

        var denied = await Assert.ThrowsAsync<AppException>(() => world.CreateTicket().Handle(
            Request(type, colleague.Id), user, CancellationToken.None));

        Assert.Equal(403, denied.StatusCode);
        Assert.Empty(world.Db.Tickets);
    }

    /// <summary>
    /// A CustomerAdmin, by contrast, opens tickets about anyone at their own Customer. Rule 5 names the
    /// Employee role specifically, and asserting only the refusal above would leave a handler that
    /// refused everybody Customer-side looking correct.
    /// </summary>
    [Fact]
    public async Task A_customer_admin_may_open_a_ticket_about_an_employee()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (admin, _) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);
        var employee = world.Employees.Add(customerId);
        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith());

        await world.CreateTicket().Handle(
            Request(type, employee.Id), admin, CancellationToken.None);

        var ticket = await world.Db.Tickets.SingleAsync();
        Assert.Equal(employee.Id, ticket.SubjectEmployeeId);
        Assert.Equal(AccountId(admin), ticket.CreatorUserAccountId);
    }

    /// <summary>
    /// Rule 4 / §9.6 rule 3: a Departed Employee may not be the Subject of a NEW ticket. 422, not 404 —
    /// the caller is entitled to see this Employee, the state is the problem — and existing tickets about
    /// them are untouched, which is why this check appears on no read or update path.
    /// </summary>
    [Fact]
    public async Task A_departed_employee_cannot_be_the_subject_of_a_new_ticket()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (admin, _) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);
        var departed = world.Employees.Add(customerId, status: "Departed");
        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith());

        var refused = await Assert.ThrowsAsync<AppException>(() => world.CreateTicket().Handle(
            Request(type, departed.Id), admin, CancellationToken.None));

        Assert.Equal(422, refused.StatusCode);
        Assert.Empty(world.Db.Tickets);
    }

    /// <summary>
    /// An inactive type is 422 and no reference is consumed. <c>GetTicketTypeAsync</c> answers null for
    /// inactive, unknown and out-of-audience alike, and all three must land here rather than storing a
    /// ticket whose descriptors cannot be resolved.
    /// </summary>
    [Fact]
    public async Task An_inactive_ticket_type_is_refused()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (admin, employee) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var type = TicketsTestHarness.TypeWith();
        type.IsActive = false;
        world.TicketTypes.Add(type);

        var refused = await Assert.ThrowsAsync<AppException>(() => world.CreateTicket().Handle(
            Request(type, employee.Id), admin, CancellationToken.None));

        Assert.Equal(422, refused.StatusCode);
        Assert.Empty(world.Db.Tickets);

        // The allocation happens after every check that can refuse, so nothing was consumed. Gaps in the
        // sequence are correct and required (§12 constraint 5), but a gap per REFUSED request would make
        // them the norm rather than the exception.
        Assert.Empty(world.References.RequestedYears);
    }

    /// <summary>
    /// The <c>AllowEmployeeToOpen</c> gate, which the plan's §4.1 pseudo-code does not mention and which
    /// exists nowhere else in the system — left unenforced the property would mean nothing at all. It is
    /// the Employee role only: a CustomerAdmin at the same Customer opens the same type.
    /// </summary>
    [Fact]
    public async Task A_type_an_employee_may_not_open_is_refused_to_an_employee_and_allowed_to_their_admin()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();

        var type = TicketsTestHarness.TypeWith();
        type.AllowEmployeeToOpen = false;
        world.TicketTypes.Add(type);

        var (employeeUser, employee) = world.NewCustomerSide(customerId);

        var denied = await Assert.ThrowsAsync<AppException>(() => world.CreateTicket().Handle(
            Request(type, employee.Id), employeeUser, CancellationToken.None));

        // 422, not the 403 the handler writes for this case: the type is outside an Employee's audience,
        // so GetTicketTypeAsync returns null and the request never reaches the AllowEmployeeToOpen check.
        // The gate is unreachable for the Employee role, which is worth pinning — it is the only reading
        // of the property's name, and the audience strip already covers it.
        Assert.Equal(422, denied.StatusCode);

        var (admin, _) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        await world.CreateTicket().Handle(
            Request(type, employee.Id), admin, CancellationToken.None);

        Assert.Equal(1, await world.Db.Tickets.CountAsync());
    }

    /// <summary>
    /// §9.1: there is no reopen. A continuation is a NEW ticket pointing back at a CLOSED one, and the
    /// predecessor must be closed and must belong to the same Customer.
    /// </summary>
    [Fact]
    public async Task A_continuation_links_back_to_a_closed_ticket_of_the_same_customer()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (admin, employee) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);
        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith());

        var openPredecessor = world.NewTicket(
            customerId, AccountId(admin), employee.Id, TicketStatus.InReview, world.SeedAccount());

        var stillOpen = await Assert.ThrowsAsync<AppException>(() => world.CreateTicket().Handle(
            Request(type, employee.Id, precededBy: openPredecessor.Id), admin, CancellationToken.None));

        Assert.Equal(422, stillOpen.StatusCode);

        openPredecessor.Status = TicketStatus.Closed;
        await world.Db.SaveChangesAsync();

        await world.CreateTicket().Handle(
            Request(type, employee.Id, precededBy: openPredecessor.Id), admin, CancellationToken.None);

        var continuation = await world.Db.Tickets
            .SingleAsync(ticket => ticket.Id != openPredecessor.Id);

        Assert.Equal(openPredecessor.Id, continuation.PrecededByTicketId);

        // A new ticket, not a revived one: the predecessor stays Closed and keeps its own reference.
        Assert.Equal(TicketStatus.Closed, openPredecessor.Status);
        Assert.NotEqual(openPredecessor.Reference, continuation.Reference);
    }

    /// <summary>
    /// A predecessor the caller cannot see is 404 — the same answer as one that does not exist. It goes
    /// through <c>TicketAccess.LoadVisibleAsync</c> rather than a bare lookup, so this is the visibility
    /// rule and not a Customer comparison, and it is what stops the field being used to confirm that some
    /// other Customer has a ticket with a given id.
    /// </summary>
    [Fact]
    public async Task A_predecessor_the_caller_cannot_see_is_404()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (admin, employee) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);
        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith());

        var otherCustomerId = Guid.NewGuid();
        var foreignEmployee = world.Employees.Add(otherCustomerId);
        var foreign = world.NewTicket(
            otherCustomerId, world.SeedAccount(), foreignEmployee.Id, TicketStatus.Closed);

        var refused = await Assert.ThrowsAsync<AppException>(() => world.CreateTicket().Handle(
            Request(type, employee.Id, precededBy: foreign.Id), admin, CancellationToken.None));

        Assert.Equal(404, refused.StatusCode);
    }

    /// <summary>
    /// A Draft does NOT enforce required fields; submitting immediately does. Two assertions about one
    /// flag, because a handler that passed <c>enforceRequired: true</c> always would make Drafts useless
    /// and a handler that passed false always would let an unanswered ticket reach the Office.
    /// </summary>
    [Fact]
    public async Task Required_fields_are_enforced_only_when_submitting_immediately()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (admin, employee) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith(
            TicketsTestHarness.Field("iban", "SingleLineText", isRequired: true)));

        // Draft: saved with the required field unanswered.
        await world.CreateTicket().Handle(
            Request(type, employee.Id), admin, CancellationToken.None);

        Assert.Equal(1, await world.Db.Tickets.CountAsync());

        var refused = await Assert.ThrowsAsync<AppException>(() => world.CreateTicket().Handle(
            Request(type, employee.Id, submitImmediately: true), admin, CancellationToken.None));

        Assert.Equal(422, refused.StatusCode);
        Assert.Contains("iban", refused.Message);
    }

    /// <summary>
    /// Create-and-submit in one request: the ticket is Submitted, the status change is audited
    /// SEPARATELY from the creation, and the whole Office is notified — not one Accountant, because
    /// nobody owns it yet.
    ///
    /// The two audit entries matter. Folding them into one would lose the Draft → Submitted transition
    /// from the trail, and the trail is what §4.0 F exists for.
    /// </summary>
    [Fact]
    public async Task Creating_and_submitting_notifies_the_whole_office_and_audits_both_steps()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (admin, employee) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith(
            TicketsTestHarness.Field("iban", "SingleLineText", isRequired: true)));

        var firstAccountant = world.SeedAccount();
        var secondAccountant = world.SeedAccount();

        await world.CreateTicket().Handle(
            Request(type, employee.Id, submitImmediately: true,
                fieldValues: Text("iban", "GR1601101250000000012300695")),
            admin,
            CancellationToken.None);

        var ticket = await world.Db.Tickets.SingleAsync();
        Assert.Equal(TicketStatus.Submitted, ticket.Status);

        // Still unassigned: submission puts it in the pickup queue, it does not hand it to anybody.
        Assert.Null(ticket.AssigneeUserAccountId);

        // The transition wrote its system message through the one shared table.
        var systemMessage = await world.Db.TicketMessages.SingleAsync();
        Assert.Equal(ticket.Id, systemMessage.TicketId);

        Assert.Single(world.Audit.WithAction(AuditActions.TicketCreated));
        var statusChange = Assert.Single(world.Audit.WithAction(AuditActions.TicketStatusChanged));
        Assert.Equal(ticket.Id.ToString(), statusChange.TargetId);

        var recipients = world.Notifications.Sent
            .Select(sent => sent.RecipientUserId)
            .ToList();

        Assert.Contains(firstAccountant.ToString(), recipients);
        Assert.Contains(secondAccountant.ToString(), recipients);
        Assert.All(world.Notifications.Sent, sent =>
        {
            Assert.Equal(NotificationEvents.TicketSubmitted, sent.EventKind);

            // In-app only. An email per submission would be unusable, and NotificationApi would refuse
            // a body for a kind absent from NotificationEvents.Emailed.
            Assert.Null(sent.EmailBody);
        });

        // The Customer-side caller who opened it is not notified of their own submission.
        Assert.DoesNotContain(admin.Id, recipients);
    }

    /// <summary>
    /// §6.3 rule 2, reached through creation: an Accountant-only field supplied by a Customer-side caller
    /// is 403, not 422. The distinction is the reason <c>rulesVersion</c> is re-read at the
    /// <c>DescriptorAudienceForRules</c> audience — against the stripped descriptor set the same input
    /// reads as an unknown key and produces a 422.
    /// </summary>
    [Fact]
    public async Task A_customer_supplying_an_accountant_only_field_is_403()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (admin, employee) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);

        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith(
            TicketsTestHarness.Field("iban", "SingleLineText"),
            TicketsTestHarness.Field("internal_ref", "SingleLineText", isVisibleToCustomer: false)));

        var denied = await Assert.ThrowsAsync<AppException>(() => world.CreateTicket().Handle(
            Request(type, employee.Id, fieldValues: Text("internal_ref", "X-1")),
            admin,
            CancellationToken.None));

        Assert.Equal(403, denied.StatusCode);
        Assert.Empty(world.Db.Tickets);
    }

    /// <summary>
    /// A Customer-side session with no Customer attached is 403, and it is 403 rather than an
    /// <c>InvalidOperationException</c> on a <c>Nullable.Value</c> — the difference between a refusal and a
    /// 500. Rule 1 resolves the Customer from <c>user.CustomerId</c> for a Customer-side caller, and a
    /// token that reaches here without one is a broken account, not a server fault.
    ///
    /// Note what is NOT asserted: a role denied <c>CreateTicket</c> by the catalogue. There is none — all
    /// four roles may open a ticket (§7.2), so the catalogue denies nobody here, and a test claiming
    /// otherwise would be asserting a grant that does not exist.
    /// </summary>
    [Fact]
    public async Task A_customer_side_caller_with_no_customer_attached_is_403()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (user, employee) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);
        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith());

        var detached = user with { CustomerId = null };

        var denied = await Assert.ThrowsAsync<AppException>(() => world.CreateTicket().Handle(
            Request(type, employee.Id), detached, CancellationToken.None));

        Assert.Equal(403, denied.StatusCode);
        Assert.Empty(world.Db.Tickets);

        // Refused before the allocation, so the sequence gains no hole.
        Assert.Empty(world.References.RequestedYears);
    }

    /// <summary>
    /// Fail-closed on the identity itself: an <c>Id</c> claim that is not a Guid is 401, not a 500 and not
    /// a <c>Guid.Empty</c> creator. <c>Guid.Empty</c> is the dangerous outcome — it is a legal value for
    /// the column, so the ticket would store successfully with a creator that matches every other
    /// malformed session's, and visibility layer 3 keys on exactly that column.
    /// </summary>
    [Fact]
    public async Task A_malformed_account_id_is_401_and_stores_nothing()
    {
        var world = new TicketsWorld();
        var customerId = Guid.NewGuid();
        var (user, employee) = world.NewCustomerSide(customerId, UserRole.CustomerAdmin);
        var type = world.TicketTypes.Add(TicketsTestHarness.TypeWith());

        var malformed = user with { Id = "not-a-guid" };

        var refused = await Assert.ThrowsAsync<AppException>(() => world.CreateTicket().Handle(
            Request(type, employee.Id), malformed, CancellationToken.None));

        Assert.Equal(401, refused.StatusCode);
        Assert.Empty(world.Db.Tickets);
        Assert.Empty(world.References.RequestedYears);
    }
}
