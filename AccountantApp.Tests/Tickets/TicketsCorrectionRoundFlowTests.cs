using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// Plan §11.2's correction-round group, at handler level.
///
/// <c>FieldValueValidationTests</c> already covers the validator; what cannot be tested there is the part
/// the plan calls the subtlest requirement in the slice: THE REQUEST IS A DELTA AND WHAT IS WRITTEN IS A
/// SNAPSHOT, with an accepted value keeping its acceptance under the ORIGINAL verifier's name and a
/// rejection deliberately not carried.
/// </summary>
public class TicketsCorrectionRoundFlowTests
{
    private static readonly DateTimeOffset FirstVerifiedAt =
        new(2026, 8, 20, 9, 30, 0, TimeSpan.Zero);

    private static TicketTypeDetailDto ThreeCustomerFields()
    {
        var type = TicketsTestHarness.TypeWith(
            TicketsTestHarness.Field("grossPay", FieldDataTypes.MoneyAmount, isRequired: true),
            TicketsTestHarness.Field("notes", FieldDataTypes.SingleLineText),
            TicketsTestHarness.Field("phone", FieldDataTypes.SingleLineText));

        type.AllowEmployeeToOpen = true;
        return type;
    }

    /// <summary>
    /// A ticket in <c>AwaitingInformation</c> whose revision 1 holds one ACCEPTED value, one REJECTED
    /// value and one unverified value, then one correction that resubmits only the third.
    ///
    /// Built once and asserted from several tests, because the interesting facts are about different
    /// tables and one giant test would report only the first of them.
    /// </summary>
    private sealed class Round
    {
        public Round()
        {
            World = new TicketsWorld();
            CustomerId = World.Customers.AddActive();
            (Customer, Subject) = World.NewCustomerSide(CustomerId);
            Verifier = World.SeedAccount();

            Ticket = World.NewTicket(
                CustomerId, Guid.Parse(Customer.Id), Subject.Id,
                TicketStatus.AwaitingInformation, assignee: Verifier, type: ThreeCustomerFields());

            GrossPay = TicketsWorld.Value("grossPay", number: 1000.10m);
            Notes = TicketsWorld.Value("notes", text: "see attached");
            Phone = TicketsWorld.Value("phone", text: "2100000000");

            RevisionOne = World.AddRevision(
                Ticket, Guid.Parse(Customer.Id), GrossPay, Notes, Phone);

            World.Accept(GrossPay, Verifier, FirstVerifiedAt);
            World.Reject(Notes, Verifier, FirstVerifiedAt, "The attachment was blank.");
        }

        public TicketsWorld World { get; }
        public Guid CustomerId { get; }
        public CurrentUser Customer { get; }
        public Api.Slices.Employees.ExternalInterfaces.EmployeeSummary Subject { get; }
        public Guid Verifier { get; }
        public Ticket Ticket { get; }
        public FieldValue GrossPay { get; }
        public FieldValue Notes { get; }
        public FieldValue Phone { get; }
        public TicketRevision RevisionOne { get; }

        public Task<RevisionSubmittedDto> Correct() =>
            World.SubmitRevision().Handle(
                new SubmitRevisionRequestDto
                {
                    TicketId = Ticket.Id,
                    Version = Ticket.Version,
                    Note = "Corrected the number you asked about.",
                    FieldValues = [new TicketFieldValueInputDto
                    {
                        FieldKey = "phone", Text = "2109999999",
                    }],
                },
                Customer,
                default);

        public TicketRevision RevisionTwo() => World.Db.TicketRevisions
            .AsNoTracking()
            .Include(revision => revision.FieldValues)
                .ThenInclude(value => value.Verifications)
            .Single(revision => revision.SequenceNumber == 2);
    }

    /// <summary>
    /// Rule 2: revision 2 has a row for EVERY descriptor, and the ones nobody touched are flagged as
    /// carried forward. A partial revision cannot be read as a snapshot, and "what did they originally
    /// claim" stops being answerable.
    /// </summary>
    [Fact]
    public async Task Revision_two_holds_every_descriptor_and_flags_what_was_carried_forward()
    {
        var round = new Round();

        var result = await round.Correct();

        Assert.Equal(2, result.SequenceNumber);
        Assert.Equal(2, result.CarriedForwardCount);

        var revisionTwo = round.RevisionTwo();
        Assert.Equal(3, revisionTwo.FieldValues.Count);

        var byKey = revisionTwo.FieldValues.ToDictionary(value => value.FieldKey);
        Assert.True(byKey["grossPay"].IsCarriedForward);
        Assert.True(byKey["notes"].IsCarriedForward);
        Assert.False(byKey["phone"].IsCarriedForward);

        Assert.Equal("2109999999", byKey["phone"].ValueText);

        // NUMERIC, not a binary float: the carried money value is the same decimal, not something within
        // a rounding error of it.
        Assert.Equal(1000.10m, byKey["grossPay"].ValueNumber);

        // New rows, not the old ones moved: the previous revision owns its rows forever.
        Assert.DoesNotContain(
            revisionTwo.FieldValues.Select(value => value.Id),
            id => id == round.GrossPay.Id || id == round.Notes.Id || id == round.Phone.Id);

        Assert.Single(round.World.Audit.WithAction(AuditActions.RevisionSubmitted));
    }

    /// <summary>
    /// Rule 1: THE PREVIOUS REVISION IS NEVER TOUCHED. Same ids, same values, same verification rows --
    /// nothing here issues an UPDATE against <c>ticket_revisions</c> or an existing <c>field_values</c>
    /// row.
    /// </summary>
    [Fact]
    public async Task Revision_one_is_unchanged_by_the_correction()
    {
        var round = new Round();

        await round.Correct();

        var revisionOne = round.World.Db.TicketRevisions
            .AsNoTracking()
            .Include(revision => revision.FieldValues)
                .ThenInclude(value => value.Verifications)
            .Single(revision => revision.Id == round.RevisionOne.Id);

        Assert.Equal(1, revisionOne.SequenceNumber);
        Assert.Equal(3, revisionOne.FieldValues.Count);

        var byKey = revisionOne.FieldValues.ToDictionary(value => value.FieldKey);
        Assert.Equal(round.GrossPay.Id, byKey["grossPay"].Id);
        Assert.Equal(1000.10m, byKey["grossPay"].ValueNumber);
        Assert.Equal("see attached", byKey["notes"].ValueText);
        Assert.Equal("2100000000", byKey["phone"].ValueText);
        Assert.All(byKey.Values, value => Assert.False(value.IsCarriedForward));

        // Its acceptance and its rejection both stay exactly where they were.
        Assert.Single(byKey["grossPay"].Verifications);
        Assert.Single(byKey["notes"].Verifications);
    }

    /// <summary>
    /// §11.3 test 2, the requirement the whole handler exists to get right, and the one that must be
    /// asserted against the <c>field_verifications</c> ROWS rather than against the API response.
    ///
    /// A test that only checked "the field still shows as accepted" would pass against a handler that
    /// re-accepted the value under the CORRECTOR's identity -- a false audit record, and worse than no
    /// record at all.
    /// </summary>
    [Fact]
    public async Task An_accepted_value_carried_forward_keeps_the_original_verifier_and_timestamp()
    {
        var round = new Round();

        await round.Correct();

        var carried = round.RevisionTwo().FieldValues.Single(value => value.FieldKey == "grossPay");

        var acceptance = Assert.Single(carried.Verifications);
        Assert.Equal(VerificationOutcome.Accepted, acceptance.Outcome);

        // THE ORIGINAL verifier, not the customer who submitted the correction.
        Assert.Equal(round.Verifier, acceptance.VerifiedByUserAccountId);
        Assert.NotEqual(Guid.Parse(round.Customer.Id), acceptance.VerifiedByUserAccountId);

        // THE ORIGINAL timestamp, not "now".
        Assert.Equal(FirstVerifiedAt, acceptance.VerifiedAt);

        // A new row pointing at the new value -- verifications belong to a FieldValue in a SPECIFIC
        // revision, so the acceptance had to be copied rather than moved.
        Assert.Equal(carried.Id, acceptance.FieldValueId);
        Assert.Null(acceptance.RejectionReason);
    }

    /// <summary>
    /// Rule 5: a REJECTION IS NOT CARRIED FORWARD. An unchanged rejected field arrives unverified so the
    /// Office can accept it or reject it again; copying the rejection would leave the ticket permanently
    /// unclosable with no action available to anybody.
    /// </summary>
    [Fact]
    public async Task A_rejected_value_carried_forward_arrives_unverified()
    {
        var round = new Round();

        await round.Correct();

        var carried = round.RevisionTwo().FieldValues.Single(value => value.FieldKey == "notes");

        Assert.True(carried.IsCarriedForward);
        Assert.Empty(carried.Verifications);
    }

    /// <summary>
    /// §4.2 rule 1: <c>AwaitingInformation → Submitted</c> RETAINS the Assignee -- the person who asked
    /// the question keeps the ticket and it does not return to the pickup pool -- and they, and only they,
    /// are told.
    /// </summary>
    [Fact]
    public async Task A_correction_resubmits_the_ticket_to_the_same_assignee()
    {
        var round = new Round();

        var result = await round.Correct();

        Assert.Equal(TicketStatus.Submitted, result.Ticket.Status);
        Assert.Equal(round.Verifier, result.Ticket.AssigneeUserAccountId);
        Assert.Equal(2, result.Ticket.Version);

        var notification = Assert.Single(round.World.Notifications.For(round.Verifier));
        Assert.Equal(NotificationEvents.CorrectionSubmitted, notification.EventKind);

        // The Customer side is not notified of their own correction.
        Assert.Empty(round.World.Notifications.For(Guid.Parse(round.Customer.Id)));
    }

    [Fact]
    public async Task A_correction_while_the_ticket_is_InReview_is_422()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.InReview,
            assignee: world.SeedAccount(), type: ThreeCustomerFields());

        world.AddRevision(ticket, Guid.Parse(customer.Id), TicketsWorld.Value("phone", text: "210"));

        var rejected = await Assert.ThrowsAsync<AppException>(() => world.SubmitRevision().Handle(
            new SubmitRevisionRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version,
                FieldValues = [new TicketFieldValueInputDto { FieldKey = "phone", Text = "211" }],
            },
            customer,
            default));

        Assert.Equal(422, rejected.StatusCode);
        Assert.Single(world.Db.TicketRevisions);
    }

    /// <summary>
    /// §9.4, LOCKED: there is no code path by which an Accountant's identity ends up attached to a
    /// Customer-supplied FieldValue. The 403 is asserted together with the ABSENCE of any new row, because
    /// a handler that wrote the revision and then threw would satisfy the status code alone.
    /// </summary>
    [Fact]
    public async Task An_Accountant_correcting_a_customer_visible_field_is_403_and_writes_nothing()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (_, subject) = world.NewCustomerSide(customerId);
        var accountant = world.NewAccountant();

        // Created BY the Accountant, so visibility layer 3 does not hide their own draft from them.
        var ticket = world.NewTicket(
            customerId, Guid.Parse(accountant.Id), subject.Id, TicketStatus.Draft,
            type: ThreeCustomerFields());

        world.AddRevision(
            ticket, Guid.Parse(accountant.Id), TicketsWorld.Value("phone", text: "210"));

        var denied = await Assert.ThrowsAsync<AppException>(() => world.SubmitRevision().Handle(
            new SubmitRevisionRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version,
                FieldValues = [new TicketFieldValueInputDto { FieldKey = "phone", Text = "211" }],
            },
            accountant,
            default));

        Assert.Equal(403, denied.StatusCode);
        Assert.Single(world.Db.TicketRevisions);
        Assert.DoesNotContain(
            world.Db.FieldValues.ToList(), value => value.ValueText == "211");
    }

    /// <summary>
    /// §6.3, the rule that looks like a contradiction: the two halves are DISJOINT, so an Accountant may
    /// write an Accountant-only field on the same ticket they may not touch a Customer field on -- and the
    /// Customer half is copied across unvalidated, because it was validated when it was written against
    /// these same frozen descriptors.
    /// </summary>
    [Fact]
    public async Task An_Accountant_writes_an_accountant_only_field_and_the_customer_half_is_carried()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);
        var accountant = world.NewAccountant();

        var type = TicketsTestHarness.TypeWith(
            TicketsTestHarness.Field("grossPay", FieldDataTypes.MoneyAmount),
            TicketsTestHarness.Field(
                "internalRef", FieldDataTypes.SingleLineText, isVisibleToCustomer: false));

        var ticket = world.NewTicket(
            customerId, Guid.Parse(accountant.Id), subject.Id, TicketStatus.Draft, type: type);

        world.AddRevision(
            ticket, Guid.Parse(accountant.Id), TicketsWorld.Value("grossPay", number: 900m));

        var result = await world.SubmitRevision().Handle(
            new SubmitRevisionRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version,
                FieldValues = [new TicketFieldValueInputDto
                {
                    FieldKey = "internalRef", Text = "LEDGER-88",
                }],
            },
            accountant,
            default);

        var revisionTwo = world.Db.TicketRevisions
            .AsNoTracking()
            .Include(revision => revision.FieldValues)
            .Single(revision => revision.Id == result.RevisionId);

        var byKey = revisionTwo.FieldValues.ToDictionary(value => value.FieldKey);
        Assert.Equal("LEDGER-88", byKey["internalRef"].ValueText);
        Assert.False(byKey["internalRef"].IsCarriedForward);
        Assert.Equal(900m, byKey["grossPay"].ValueNumber);
        Assert.True(byKey["grossPay"].IsCarriedForward);

        // A Draft stays a Draft -- the handler appends rather than editing in place (§13 item 3) -- and
        // the token still moves because the tickets row was written.
        Assert.Equal(TicketStatus.Draft, result.Ticket.Status);
        Assert.Equal(2, result.Ticket.Version);

        // The customer-side session cannot even see this draft (layer 3), which is why the "is the
        // Accountant-only value hidden from them" half lives in its own test below on a ticket they own.
        Assert.NotEqual(Guid.Parse(accountant.Id), Guid.Parse(customer.Id));
    }

    /// <summary>
    /// §4.3 rule 5: a value whose DESCRIPTOR was stripped for this audience is ABSENT from the response --
    /// not nulled, absent. Nulling it would tell the Customer side that a field they may not see exists
    /// and is unanswered, and a UI built on that would render an empty row asking them to fill it in.
    /// </summary>
    [Fact]
    public async Task An_accountant_only_value_is_absent_from_a_customer_side_response()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var type = TicketsTestHarness.TypeWith(
            TicketsTestHarness.Field("grossPay", FieldDataTypes.MoneyAmount),
            TicketsTestHarness.Field(
                "internalRef", FieldDataTypes.SingleLineText, isVisibleToCustomer: false));

        type.AllowEmployeeToOpen = true;

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.InReview, type: type);

        world.AddRevision(
            ticket,
            Guid.Parse(customer.Id),
            TicketsWorld.Value("grossPay", number: 900m),
            TicketsWorld.Value("internalRef", text: "LEDGER-88"));

        var asCustomer = await world.GetTicket()
            .Handle(new GetTicketRequestDto { TicketId = ticket.Id }, customer, default);

        Assert.Equal("grossPay", Assert.Single(asCustomer.Fields).Key);
        Assert.DoesNotContain(asCustomer.Fields, field => field.Key == "internalRef");
        Assert.DoesNotContain(
            asCustomer.Revisions.SelectMany(revision => revision.FieldValues),
            value => value.FieldKey == "internalRef");

        // The row is in the database; it is the response that omits it.
        Assert.Contains(world.Db.FieldValues.ToList(), value => value.FieldKey == "internalRef");

        // And the Office sees both.
        var asOffice = await world.GetTicket().Handle(
            new GetTicketRequestDto { TicketId = ticket.Id }, world.NewAccountant(), default);

        Assert.Equal(2, asOffice.Fields.Count);
        Assert.Contains(
            asOffice.Revisions.SelectMany(revision => revision.FieldValues),
            value => value.FieldKey == "internalRef");
    }

    [Fact]
    public async Task A_customer_side_caller_writing_an_accountant_only_field_is_403()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var type = TicketsTestHarness.TypeWith(
            TicketsTestHarness.Field(
                "internalRef", FieldDataTypes.SingleLineText, isVisibleToCustomer: false));
        type.AllowEmployeeToOpen = true;

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.Draft, type: type);

        var denied = await Assert.ThrowsAsync<AppException>(() => world.SubmitRevision().Handle(
            new SubmitRevisionRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version,
                FieldValues = [new TicketFieldValueInputDto
                {
                    FieldKey = "internalRef", Text = "guessing",
                }],
            },
            customer,
            default));

        // 403 and not 422: the key exists, and telling them it does not would be a lie the validator
        // deliberately does not tell (§6.3 rule 2).
        Assert.Equal(403, denied.StatusCode);
    }

    [Fact]
    public async Task A_value_for_an_unknown_field_key_is_422_rather_than_silently_dropped()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.Draft,
            type: ThreeCustomerFields());

        var rejected = await Assert.ThrowsAsync<AppException>(() => world.SubmitRevision().Handle(
            new SubmitRevisionRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version,
                FieldValues = [new TicketFieldValueInputDto { FieldKey = "notAField", Text = "x" }],
            },
            customer,
            default));

        Assert.Equal(422, rejected.StatusCode);
    }

    /// <summary>
    /// The required gate is evaluated over the COMPLETED SNAPSHOT and only when the revision SUBMITS. A
    /// correction that does not restate every answer must not 422 -- and one that leaves a required
    /// visible field genuinely empty must, naming it.
    /// </summary>
    [Fact]
    public async Task A_correction_that_resubmits_with_a_required_field_still_empty_is_422_naming_it()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.AwaitingInformation,
            assignee: world.SeedAccount(), type: ThreeCustomerFields());

        // Revision 1 never held grossPay, and the correction does not supply it either.
        world.AddRevision(ticket, Guid.Parse(customer.Id), TicketsWorld.Value("phone", text: "210"));

        var rejected = await Assert.ThrowsAsync<AppException>(() => world.SubmitRevision().Handle(
            new SubmitRevisionRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version,
                FieldValues = [new TicketFieldValueInputDto { FieldKey = "phone", Text = "211" }],
            },
            customer,
            default));

        Assert.Equal(422, rejected.StatusCode);
        Assert.Contains("grossPay", rejected.Message);
        Assert.Single(world.Db.TicketRevisions);
    }

    /// <summary>
    /// §6.4: a required field HIDDEN by conditional visibility is not required, and the condition is
    /// evaluated against the completed snapshot -- so a controlling answer given in revision 1 and not
    /// restated still counts.
    /// </summary>
    [Fact]
    public async Task A_required_field_hidden_by_its_condition_does_not_block_the_resubmission()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var type = TicketsTestHarness.TypeWith(
            TicketsTestHarness.Field("hasBonus", FieldDataTypes.YesNo),
            TicketsTestHarness.Field(
                "bonusReason", FieldDataTypes.SingleLineText, isRequired: true,
                conditional: new ConditionalVisibilityDto { FieldKey = "hasBonus", Value = "true" }),
            TicketsTestHarness.Field("phone", FieldDataTypes.SingleLineText));

        type.AllowEmployeeToOpen = true;

        var ticket = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.AwaitingInformation,
            assignee: world.SeedAccount(), type: type);

        var hasBonus = TicketsWorld.Value("hasBonus");
        hasBonus.ValueBoolean = false;
        world.AddRevision(ticket, Guid.Parse(customer.Id), hasBonus);

        var result = await world.SubmitRevision().Handle(
            new SubmitRevisionRequestDto
            {
                TicketId = ticket.Id,
                Version = ticket.Version,
                FieldValues = [new TicketFieldValueInputDto { FieldKey = "phone", Text = "210" }],
            },
            customer,
            default);

        Assert.Equal(TicketStatus.Submitted, result.Ticket.Status);
    }

    /// <summary>
    /// §0.3 step 5 for the second time: a <c>FileUpload</c> value naming a document that belongs to
    /// ANOTHER ticket. The validator is handed only this ticket's live documents, so the id is simply not
    /// there and the answer is a 422 -- without the handler writing a comparison of its own.
    /// </summary>
    [Fact]
    public async Task A_file_upload_value_naming_another_tickets_document_is_refused()
    {
        var world = new TicketsWorld();
        var customerId = world.Customers.AddActive();
        var (customer, subject) = world.NewCustomerSide(customerId);

        var type = TicketsTestHarness.TypeWith(
            TicketsTestHarness.Field("payslip", FieldDataTypes.FileUpload));
        type.AllowEmployeeToOpen = true;

        var mine = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.Draft, type: type);

        var theirs = world.NewTicket(
            customerId, Guid.Parse(customer.Id), subject.Id, TicketStatus.Draft, type: type);

        var foreignDocument = await world.StoreDocumentAsync(theirs, Guid.Parse(customer.Id));

        var rejected = await Assert.ThrowsAsync<AppException>(() => world.SubmitRevision().Handle(
            new SubmitRevisionRequestDto
            {
                TicketId = mine.Id,
                Version = mine.Version,
                FieldValues = [new TicketFieldValueInputDto
                {
                    FieldKey = "payslip", DocumentId = foreignDocument.Id,
                }],
            },
            customer,
            default));

        Assert.Equal(422, rejected.StatusCode);
    }
}
