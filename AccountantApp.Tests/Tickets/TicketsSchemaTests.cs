using AccountantApp.Api.Shared.Migrations;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// The only test that touches the real Tickets migration. Everything else in this folder runs against
/// the in-memory provider, which enforces no CHECK constraint, has no partial indexes, cannot execute
/// INSERT ... ON CONFLICT ... RETURNING, and cannot roll a transaction back.
///
/// So without this file the following are ENTIRELY UNVERIFIED: all ten CHECK constraints -- including
/// ck_tickets_assignee, which encodes the AwaitingInformation -> Submitted trap -- the three unique
/// constraints and both 409 paths that depend on them, the two partial indexes,
/// TicketReferenceAllocator (the one piece of raw SQL in the slice, untestable by any other means), and
/// NUMERIC(18,4) semantics for MoneyAmount.
///
/// IT SKIPS WHERE THERE IS NO POSTGRESQL, and on a machine where it skips none of the above has been
/// checked by anything. A skipped test is not a passing test.
/// </summary>
public sealed class TicketsSchemaTests
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
    private const string ExpectedScriptKey =
        "Tickets/Infrastructure/Migrations/20260904_001_CreateTicketsSchema.sql";
    private const string ExpectedReminderScriptKey =
        "Tickets/Infrastructure/Migrations/20260905_001_CreateDueDateReminders.sql";

    [SkippableFact]
    public async Task Migration_constraints_indexes_reference_allocation_and_rollback_work_against_real_postgres()
    {
        Skip.IfNot(await PostgresIsReachable(),
            "No PostgreSQL at localhost:5432. The Tickets schema, its twelve CHECK constraints, its "
            + "three unique constraints, its nine indexes, the concurrency safety of "
            + "TicketReferenceAllocator and NUMERIC(18,4) for MoneyAmount are all unverified.");

        var database = $"accountant_app_tickets_test_{Guid.NewGuid():N}";
        await ExecuteOnAdmin($"CREATE DATABASE \"{database}\"");
        var connectionString = AdminConnectionString.Replace("Database=postgres", $"Database={database}");

        try
        {
            await SqlMigrationRunner.RunAsync(connectionString, AppContext.BaseDirectory);

            // Slice-relative and forward-slashed. A backslash here on Windows means the migration re-runs
            // on Linux and fails on the already-existing table.
            Assert.Equal(ExpectedScriptKey, await QueryScalar<string>(connectionString,
                $"SELECT script_name FROM schema_versions WHERE script_name = '{ExpectedScriptKey}'"));

            // The second migration (plan section 9a.1), tracked under its OWN slice-relative key. Two
            // scripts in one slice is the case the runner's key exists for: a Path.GetFileName key would
            // still be distinct here, but the same 001 sequence number in another slice would not be.
            Assert.Equal(ExpectedReminderScriptKey, await QueryScalar<string>(connectionString,
                "SELECT script_name FROM schema_versions "
                + $"WHERE script_name = '{ExpectedReminderScriptKey}'"));

            await AssertSixTablesAndTheCounter(connectionString);
            await AssertStatusAndPriorityConstraints(connectionString);
            await AssertAssigneeConstraint(connectionString);
            await AssertClosedAtConstraint(connectionString);
            await AssertVersionConstraint(connectionString);
            await AssertUniqueReference(connectionString);
            await AssertRevisionConstraints(connectionString);
            await AssertOneAnswerPerFieldPerRevision(connectionString);
            await AssertOneCarrierPerFieldValue(connectionString);
            await AssertVerificationConstraints(connectionString);
            await AssertMessageConstraints(connectionString);
            await AssertIndexes(connectionString);
            await AssertColumnMappingAndNumericPrecision(connectionString);
            await AssertDueDateReminderKeyReArmsOnANewDueDate(connectionString);
            await AssertConcurrentReferenceAllocationProducesNoDuplicates(connectionString);
            await AssertRolledBackCreationLeavesNoTicketAndNoRevision(connectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnAdmin($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    /// <summary>
    /// Six tables plus the counter from the first script, and ticket_due_date_reminders from the second
    /// (plan section 9a.1).
    ///
    /// AMENDED when the due-date scanner landed. This used to assert
    /// DoesNotContain("ticket_due_date_reminders") -- correct as a statement about the FIRST script, but
    /// asserted against the database AFTER SqlMigrationRunner has applied every script in the slice, so
    /// it necessarily failed the moment the second migration existed. The claim it was making has moved
    /// to TicketsColumnMappingTests.The_reminder_table_is_created_by_the_second_migration_and_not_the_first,
    /// which reads the two scripts and therefore runs on a machine with no PostgreSQL as well.
    /// </summary>
    private static async Task AssertSixTablesAndTheCounter(string connectionString)
    {
        var tables = await QueryStrings(connectionString,
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public'");

        foreach (var expected in new[]
                 {
                     "tickets", "ticket_revisions", "field_values", "field_verifications",
                     "ticket_messages", "ticket_message_documents", "ticket_reference_counters",
                 })
            Assert.Contains(expected, tables);

        Assert.Contains("ticket_due_date_reminders", tables);
    }

    private static async Task AssertStatusAndPriorityConstraints(string connectionString)
    {
        var badStatus = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertTicket("TKT-2026-000001", status: "'Reopened'")));
        Assert.Equal("23514", badStatus.SqlState);
        Assert.Contains("ck_tickets_status", badStatus.Message);

        // Case matters: the code compares ordinally against the PascalCase form, so a 'draft' row would
        // be invisible to every status check in the slice.
        var wrongCase = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertTicket("TKT-2026-000002", status: "'draft'")));
        Assert.Equal("23514", wrongCase.SqlState);

        var badPriority = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertTicket("TKT-2026-000003", priority: "'Urgent'")));
        Assert.Equal("23514", badPriority.SqlState);
        Assert.Contains("ck_tickets_priority", badPriority.Message);
    }

    /// <summary>
    /// ck_tickets_assignee, and the third branch is the whole point. A Submitted ticket MAY carry an
    /// Assignee, because AwaitingInformation -> Submitted retains it -- the correction round. A
    /// constraint written as "Submitted AND assignee IS NULL" looks obviously right and rejects every
    /// correction in the system.
    /// </summary>
    private static async Task AssertAssigneeConstraint(string connectionString)
    {
        // THE CASE PLAN SECTION 11.1 NAMES EXPLICITLY: Submitted WITH an Assignee is accepted.
        await ExecuteOn(connectionString, InsertTicket("TKT-2026-000010",
            status: "'Submitted'", assignee: "gen_random_uuid()"));

        // And Submitted with none, the pickup-queue case.
        await ExecuteOn(connectionString, InsertTicket("TKT-2026-000011", status: "'Submitted'"));

        foreach (var status in new[] { "InReview", "AwaitingInformation", "Answered" })
        {
            var missing = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
                InsertTicket($"TKT-2026-0001{status.Length}2", status: $"'{status}'")));
            Assert.Equal("23514", missing.SqlState);
            Assert.Contains("ck_tickets_assignee", missing.Message);
        }

        // Closed requires one too, and closed_at with it.
        var closedWithoutAssignee = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertTicket("TKT-2026-000013",
                status: "'Closed'", closedAt: "NOW()")));
        Assert.Equal("23514", closedWithoutAssignee.SqlState);

        // Draft and Cancelled must have NONE. A cancelled ticket keeping its Assignee reads as work
        // somebody still owns.
        foreach (var status in new[] { "Draft", "Cancelled" })
        {
            var present = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
                InsertTicket($"TKT-2026-0002{status.Length}0",
                    status: $"'{status}'", assignee: "gen_random_uuid()")));
            Assert.Equal("23514", present.SqlState);
            Assert.Contains("ck_tickets_assignee", present.Message);
        }

        await ExecuteOn(connectionString, InsertTicket("TKT-2026-000014",
            status: "'InReview'", assignee: "gen_random_uuid()"));
    }

    private static async Task AssertClosedAtConstraint(string connectionString)
    {
        var closedWithoutInstant = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertTicket("TKT-2026-000020",
                status: "'Closed'", assignee: "gen_random_uuid()")));
        Assert.Equal("23514", closedWithoutInstant.SqlState);
        Assert.Contains("ck_tickets_closed", closedWithoutInstant.Message);

        // Cancelled is terminal but it is NOT closed. The two mean different things in every report, and
        // a closed_at on a cancelled ticket would make "how many did we close" wrong.
        var cancelledWithInstant = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertTicket("TKT-2026-000021",
                status: "'Cancelled'", closedAt: "NOW()")));
        Assert.Equal("23514", cancelledWithInstant.SqlState);

        await ExecuteOn(connectionString, InsertTicket("TKT-2026-000022",
            status: "'Closed'", assignee: "gen_random_uuid()", closedAt: "NOW()"));
    }

    private static async Task AssertVersionConstraint(string connectionString)
    {
        // Version 0 would make the first client read echo a token the server never issued, and
        // RequireVersion would then accept a request built on nothing.
        var zero = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertTicket("TKT-2026-000030", version: "0")));
        Assert.Equal("23514", zero.SqlState);
        Assert.Contains("ck_tickets_version", zero.Message);
    }

    private static async Task AssertUniqueReference(string connectionString)
    {
        await ExecuteOn(connectionString, InsertTicket("TKT-2026-000040"));

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertTicket("TKT-2026-000040")));
        Assert.Equal("23505", duplicate.SqlState);
        Assert.Contains("uq_tickets_reference", duplicate.Message);
    }

    private static async Task AssertRevisionConstraints(string connectionString)
    {
        var ticketId = await InsertTicketReturningId(connectionString, "TKT-2026-000050");

        await ExecuteOn(connectionString, InsertRevision(ticketId, 1));

        // uq_ticket_revisions_sequence. This is what makes two concurrent corrections impossible to
        // interleave into a duplicate revision 2: one of them gets 23505, which the handler maps to 409
        // rather than letting it surface as a 500.
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertRevision(ticketId, 1)));
        Assert.Equal("23505", duplicate.SqlState);
        Assert.Contains("uq_ticket_revisions_sequence", duplicate.Message);

        var zero = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertRevision(ticketId, 0)));
        Assert.Equal("23514", zero.SqlState);
        Assert.Contains("ck_ticket_revisions_sequence", zero.Message);

        // The same sequence number at a DIFFERENT ticket is fine -- the constraint is per ticket.
        var otherTicketId = await InsertTicketReturningId(connectionString, "TKT-2026-000051");
        await ExecuteOn(connectionString, InsertRevision(otherTicketId, 1));
    }

    /// <summary>
    /// uq_field_values_revision_key: a revision holds ONE answer per field. Without it a correction that
    /// writes a second row for the same key produces two answers and every read picks whichever the
    /// query returns first.
    /// </summary>
    private static async Task AssertOneAnswerPerFieldPerRevision(string connectionString)
    {
        var ticketId = await InsertTicketReturningId(connectionString, "TKT-2026-000060");
        var revisionId = await InsertRevisionReturningId(connectionString, ticketId, 1);

        await ExecuteOn(connectionString,
            $"INSERT INTO field_values (ticket_revision_id, field_key, value_text) "
            + $"VALUES ('{revisionId}', 'vat_number', 'EL123456789')");

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            $"INSERT INTO field_values (ticket_revision_id, field_key, value_text) "
            + $"VALUES ('{revisionId}', 'vat_number', 'EL999999999')"));
        Assert.Equal("23505", duplicate.SqlState);
        Assert.Contains("uq_field_values_revision_key", duplicate.Message);
    }

    /// <summary>
    /// The two constraints from plan section 13 item 4, resolved in favour of adding them.
    ///
    /// ck_field_values_one_carrier is the half of the data-type pairing the database CAN express. It
    /// cannot know a row was meant to be a WholeNumber -- the data type lives on the descriptor, in
    /// another slice -- but "at most one primary carrier is populated" is true of all eleven types, so
    /// it catches the switch that falls through and writes two columns. That failure is invisible until
    /// an Accountant reads a figure that is not what the Customer typed, because every reader picks a
    /// different one of the two.
    ///
    /// The three ACCEPTING cases below matter as much as the rejections. A constraint that also refused
    /// a DateRange, a MultipleChoice or a blank Draft answer would be discovered by a user, not here.
    /// </summary>
    private static async Task AssertOneCarrierPerFieldValue(string connectionString)
    {
        var ticketId = await InsertTicketReturningId(connectionString, "TKT-2026-000065");
        var revisionId = await InsertRevisionReturningId(connectionString, ticketId, 1);

        // Two carriers: the bug the constraint exists for.
        var twoCarriers = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            $"INSERT INTO field_values (ticket_revision_id, field_key, value_text, value_number) "
            + $"VALUES ('{revisionId}', 'two_carriers', '42', 42)"));
        Assert.Equal("23514", twoCarriers.SqlState);
        Assert.Contains("ck_field_values_one_carrier", twoCarriers.Message);

        // A range with an end and no start. value_date_to is excluded from the carrier count, so this
        // is the one ordering mistake that check cannot see -- hence the second constraint.
        var endWithoutStart = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            $"INSERT INTO field_values (ticket_revision_id, field_key, value_date_to) "
            + $"VALUES ('{revisionId}', 'end_only', '2026-03-31')"));
        Assert.Equal("23514", endWithoutStart.SqlState);
        Assert.Contains("ck_field_values_date_range", endWithoutStart.Message);

        // A DateRange populates value_date AND value_date_to, and MUST be accepted.
        await ExecuteOn(connectionString,
            $"INSERT INTO field_values (ticket_revision_id, field_key, value_date, value_date_to) "
            + $"VALUES ('{revisionId}', 'period', '2026-01-01', '2026-03-31')");

        // MultipleChoice serialises to a JSON array in value_text -- one column, non-atomic value.
        await ExecuteOn(connectionString,
            $"INSERT INTO field_values (ticket_revision_id, field_key, value_text) "
            + $"VALUES ('{revisionId}', 'days', '[\"Mon\",\"Tue\"]')");

        // A Draft may hold a blank answer, so all five carriers null is permitted.
        await ExecuteOn(connectionString,
            $"INSERT INTO field_values (ticket_revision_id, field_key) "
            + $"VALUES ('{revisionId}', 'not_reached_yet')");
    }

    private static async Task AssertVerificationConstraints(string connectionString)
    {
        var ticketId = await InsertTicketReturningId(connectionString, "TKT-2026-000070");
        var revisionId = await InsertRevisionReturningId(connectionString, ticketId, 1);
        var valueId = await QueryScalar<Guid>(connectionString,
            $"INSERT INTO field_values (ticket_revision_id, field_key, value_text) "
            + $"VALUES ('{revisionId}', 'payslip_note', 'x') RETURNING id");

        var badOutcome = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            InsertVerification(valueId, "'Pending'")));
        Assert.Equal("23514", badOutcome.SqlState);
        Assert.Contains("ck_field_verifications_outcome", badOutcome.Message);

        // THE CASE PLAN SECTION 11.1 NAMES EXPLICITLY. The reason is shown VERBATIM to the Customer
        // side, so a whitespace-only reason is exactly as useless as a null -- which is why the
        // constraint is length(trim(...)) > 0 and not merely NOT NULL. A plain NOT NULL passes here.
        var whitespaceReason = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertVerification(valueId, "'Rejected'", "'   '")));
        Assert.Equal("23514", whitespaceReason.SqlState);
        Assert.Contains("ck_field_verifications_reason", whitespaceReason.Message);

        var rejectedWithoutReason = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertVerification(valueId, "'Rejected'")));
        Assert.Equal("23514", rejectedWithoutReason.SqlState);

        // Accepted WITH a reason is refused too. A reason on an acceptance renders in the UI where a
        // rejection reason renders, so it reads to the Customer as a rejection.
        var acceptedWithReason = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertVerification(valueId, "'Accepted'", "'looks fine'")));
        Assert.Equal("23514", acceptedWithReason.SqlState);

        await ExecuteOn(connectionString, InsertVerification(valueId, "'Accepted'"));
        await ExecuteOn(connectionString,
            InsertVerification(valueId, "'Rejected'", "'The payslip is illegible.'"));
    }

    private static async Task AssertMessageConstraints(string connectionString)
    {
        var ticketId = await InsertTicketReturningId(connectionString, "TKT-2026-000080");

        var badKind = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            InsertMessage(ticketId, "'Comment'", "gen_random_uuid()")));
        Assert.Equal("23514", badKind.SqlState);
        Assert.Contains("ck_ticket_messages_kind", badKind.Message);

        // A SystemEvent is written by the application, not a person. An author on one would make it look
        // like something the actor typed.
        var systemEventWithAuthor = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertMessage(ticketId, "'SystemEvent'", "gen_random_uuid()")));
        Assert.Equal("23514", systemEventWithAuthor.SqlState);
        Assert.Contains("ck_ticket_messages_author", systemEventWithAuthor.Message);

        var humanMessageWithoutAuthor = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOn(connectionString, InsertMessage(ticketId, "'CustomerMessage'", "NULL")));
        Assert.Equal("23514", humanMessageWithoutAuthor.SqlState);

        await ExecuteOn(connectionString, InsertMessage(ticketId, "'SystemEvent'", "NULL"));
        await ExecuteOn(connectionString, InsertMessage(ticketId, "'InternalNote'", "gen_random_uuid()"));
    }

    /// <summary>
    /// The second migration's one table, and the only thing about it worth a database: the composite
    /// primary key (plan section 9a.3).
    ///
    /// A second marker for the SAME (ticket, due date) is rejected -- that is what makes the scanner
    /// idempotent, and the in-memory provider enforces a primary key but not the FK below it. A marker
    /// for the same ticket at a DIFFERENT due date is accepted, which is what re-arms the reminder when
    /// an Accountant moves a deadline; a key on ticket_id alone would reject it and suppress the reminder
    /// forever.
    /// </summary>
    private static async Task AssertDueDateReminderKeyReArmsOnANewDueDate(string connectionString)
    {
        await ExecuteOn(connectionString, InsertTicket("TKT-2026-009001", status: "'Submitted'"));
        var ticketId = await QueryScalarRequired<Guid>(connectionString,
            "SELECT id FROM tickets WHERE reference = 'TKT-2026-009001'");

        await ExecuteOn(connectionString,
            "INSERT INTO ticket_due_date_reminders (ticket_id, due_date) "
            + $"VALUES ('{ticketId}', DATE '2026-09-15')");

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            "INSERT INTO ticket_due_date_reminders (ticket_id, due_date) "
            + $"VALUES ('{ticketId}', DATE '2026-09-15')"));
        Assert.Equal("23505", duplicate.SqlState);

        // The due date moved: a NEW row, not an update and not a delete (section 1.9).
        await ExecuteOn(connectionString,
            "INSERT INTO ticket_due_date_reminders (ticket_id, due_date) "
            + $"VALUES ('{ticketId}', DATE '2026-10-15')");

        Assert.Equal(2, await QueryScalarRequired<long>(connectionString,
            $"SELECT COUNT(*) FROM ticket_due_date_reminders WHERE ticket_id = '{ticketId}'"));

        // The intra-slice foreign key. A marker for a ticket that does not exist is a bug in the scanner,
        // and it is the kind that only shows up as an orphan row nobody reads.
        var orphan = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOn(connectionString,
            "INSERT INTO ticket_due_date_reminders (ticket_id, due_date) "
            + $"VALUES ('{Guid.NewGuid()}', DATE '2026-09-15')"));
        Assert.Equal("23503", orphan.SqlState);
    }

    private static async Task AssertIndexes(string connectionString)
    {
        var indexes = await QueryStrings(connectionString,
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public'");

        foreach (var expected in new[]
                 {
                     "idx_tickets_pickup", "idx_tickets_assignee_open", "idx_tickets_customer_activity",
                     "idx_tickets_creator", "idx_tickets_subject", "idx_ticket_revisions_ticket",
                     "idx_field_values_revision", "idx_field_verifications_value",
                     "idx_ticket_messages_ticket",
                 })
            Assert.Contains(expected, indexes);

        // A partial index that silently became total still answers every query correctly. It shows up
        // only as a table larger and slower than it should be, years later. Two of the nine are partial;
        // the other seven are total on purpose.
        var partial = await QueryStrings(connectionString,
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND indexdef LIKE '%WHERE%'");
        Assert.Contains("idx_tickets_pickup", partial);
        Assert.Contains("idx_tickets_assignee_open", partial);

        // idx_tickets_pickup's predicate must include the assignee IS NULL half: a partial index whose
        // predicate is narrower than the query's is unusable, and this is the hottest query the Office
        // runs.
        var pickupDefinition = await QueryScalar<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'idx_tickets_pickup'");
        Assert.Contains("assignee_user_account_id IS NULL", pickupDefinition!);
        Assert.Contains("'Submitted'", pickupDefinition!);
    }

    /// <summary>
    /// Every property must map. snake_case is NOT automatic in this codebase; each column name is
    /// declared explicitly and a missed HasColumnName does not fail at startup -- it fails on the first
    /// query that touches the column, as a 42703 from deep inside EF.
    ///
    /// This also covers NUMERIC(18,4): 0.10 must round-trip EXACTLY. A float column accepts the value,
    /// answers every query, and quietly returns 0.100000001490116 -- in an accounting application.
    /// </summary>
    private static async Task AssertColumnMappingAndNumericPrecision(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        var ticketId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var valueId = Guid.NewGuid();
        var recorded = new DateTimeOffset(2026, 9, 2, 11, 45, 0, TimeSpan.FromHours(3));

        await using (var db = new TicketsDbContext(options))
        {
            db.Tickets.Add(new Ticket
            {
                Id = ticketId,
                Reference = "TKT-2026-000100",
                CustomerId = Guid.NewGuid(),
                TicketTypeId = Guid.NewGuid(),
                TicketTypeVersionId = Guid.NewGuid(),
                CreatorUserAccountId = Guid.NewGuid(),
                SubjectEmployeeId = Guid.NewGuid(),
                Status = TicketStatus.AwaitingInformation,
                AssigneeUserAccountId = Guid.NewGuid(),
                Priority = TicketPriority.High,
                DueDate = new DateOnly(2026, 10, 31),
                Title = "Mapping test",
                CurrentRevisionId = revisionId,
                Version = 3,
                CreatedAt = recorded,
                LastActivityAt = recorded,
            });

            db.TicketRevisions.Add(new TicketRevision
            {
                Id = revisionId,
                TicketId = ticketId,
                SequenceNumber = 1,
                SubmittedByUserAccountId = Guid.NewGuid(),
                SubmittedAt = recorded,
                Note = "First submission",
            });

            db.FieldValues.Add(new FieldValue
            {
                Id = valueId,
                TicketRevisionId = revisionId,
                FieldKey = "gross_amount",
                ValueText = "note",
                ValueNumber = 0.10m,
                ValueDate = new DateOnly(2026, 4, 1),
                ValueDateTo = new DateOnly(2026, 4, 30),
                ValueBoolean = true,
                ValueDocumentId = Guid.NewGuid(),
                IsCarriedForward = true,
                CreatedAt = recorded,
            });

            db.FieldVerifications.Add(new FieldVerification
            {
                Id = Guid.NewGuid(),
                FieldValueId = valueId,
                Outcome = VerificationOutcome.Rejected,
                RejectionReason = "Illegible",
                VerifiedByUserAccountId = Guid.NewGuid(),
                VerifiedAt = recorded,
            });

            var messageId = Guid.NewGuid();
            db.TicketMessages.Add(new TicketMessage
            {
                Id = messageId,
                TicketId = ticketId,
                AuthorUserAccountId = null,
                Kind = TicketMessageKind.SystemEvent,
                Body = "Status changed to Awaiting Information",
                CreatedAt = recorded,
            });
            db.TicketMessageDocuments.Add(new TicketMessageDocument
            {
                TicketMessageId = messageId,
                DocumentId = Guid.NewGuid(),
            });

            await db.SaveChangesAsync();
        }

        await using (var db = new TicketsDbContext(options))
        {
            var ticket = await db.Tickets.SingleAsync(item => item.Id == ticketId);

            // DATE round-trips as a DateOnly with no timezone shift. A TIMESTAMPTZ here would turn a
            // statutory deadline into the previous day for half the world.
            Assert.Equal(new DateOnly(2026, 10, 31), ticket.DueDate);
            Assert.Equal(3, ticket.Version);
            Assert.Equal(revisionId, ticket.CurrentRevisionId);

            // TIMESTAMPTZ normalises the offset, so compare in UTC.
            Assert.Equal(recorded.UtcDateTime, ticket.CreatedAt.UtcDateTime);
            Assert.Null(ticket.ClosedAt);

            var value = await db.FieldValues.SingleAsync(item => item.Id == valueId);

            // THE ONE THAT MATTERS. Exactly 0.10, not 0.099999... -- NUMERIC, not float.
            Assert.Equal(0.10m, value.ValueNumber);
            Assert.Equal(new DateOnly(2026, 4, 1), value.ValueDate);
            Assert.Equal(new DateOnly(2026, 4, 30), value.ValueDateTo);
            Assert.True(value.ValueBoolean);
            Assert.True(value.IsCarriedForward);
        }

        // Status is TEXT, not an integer. An int would make ck_tickets_status impossible to express and a
        // table dump unreadable.
        Assert.Equal("AwaitingInformation", await QueryScalar<string>(connectionString,
            $"SELECT status FROM tickets WHERE id = '{ticketId}'"));

        // And the scale is really 4. A NUMERIC with no scale would accept 0.10 and this assertion, while
        // storing tax figures at whatever precision the client happened to send.
        Assert.Equal(4, await QueryScalar<int>(connectionString,
            "SELECT numeric_scale FROM information_schema.columns "
            + "WHERE table_name = 'field_values' AND column_name = 'value_number'"));
    }

    /// <summary>
    /// SUCCESS CRITERION 4, and the only way to test it: 50 concurrent allocations, 50 distinct
    /// references, no exception. The in-memory provider cannot execute the statement at all, so on a
    /// machine without PostgreSQL the reference allocator has never been run.
    ///
    /// Each task gets its OWN connection. Sharing one would serialise them at the client and the test
    /// would pass against a read-then-increment implementation, which is the bug it exists to catch.
    /// </summary>
    private static async Task AssertConcurrentReferenceAllocationProducesNoDuplicates(
        string connectionString)
    {
        const int concurrency = 50;
        const int year = 2031; // A year of its own, so the earlier fixtures cannot affect the count.

        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        var references = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
        {
            await using var db = new TicketsDbContext(options);
            return await new TicketReferenceAllocator(db).AllocateAsync(year, CancellationToken.None);
        }));

        Assert.Equal(concurrency, references.Distinct(StringComparer.Ordinal).Count());

        // Six digits, zero-padded. Five or seven would sort differently as text and would not match a
        // client that formats the reference back.
        Assert.Contains($"TKT-{year}-000001", references);
        Assert.All(references, reference => Assert.Matches(@"^TKT-2031-\d{6}$", reference));

        Assert.Equal(concurrency, await QueryScalar<int>(connectionString,
            $"SELECT last_sequence FROM ticket_reference_counters WHERE year = {year}"));
    }

    /// <summary>
    /// PLAN SECTION 11.3 TEST 6. The assertion QUERIES THE DATABASE after the scope was disposed without
    /// a commit, and it checks BOTH tickets and ticket_revisions -- a response status passes either way,
    /// and a creation that left a ticket with no revision is the failure this catches.
    ///
    /// It also pins what the reference counter does on a rollback, which is where the plan was wrong and
    /// has since been corrected. Section 1.7 rule 5 used to say a rolled-back creation "does not release
    /// the number -- a gap in the sequence", and the acceptance criterion derived from it asserted the
    /// gap. That is false for a counter held in a TABLE: nextval() on a real SEQUENCE is deliberately
    /// non-transactional and would leave a gap, but an UPDATE to a row is undone by ROLLBACK like any
    /// other write, so the number IS handed to the next caller. Rule 5 and section 12 constraint 5 now
    /// say so.
    ///
    /// Nothing is broken by that: "a reference is never reused" is about references given to a PERSISTED
    /// ticket, and a rolled-back creation never had one. The final assertion below is what makes the
    /// corrected claim a fact rather than an argument -- it is the one assertion here that would have
    /// failed under the old wording.
    /// </summary>
    private static async Task AssertRolledBackCreationLeavesNoTicketAndNoRevision(string connectionString)
    {
        const int year = 2032;
        var reference = string.Empty;

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();

            await using var db = new TicketsDbContext(
                new DbContextOptionsBuilder<TicketsDbContext>().UseNpgsql(connection).Options);
            await using var transaction = await db.Database.BeginTransactionAsync();

            reference = await new TicketReferenceAllocator(db).AllocateAsync(year, CancellationToken.None);

            var ticketId = Guid.NewGuid();
            db.Tickets.Add(new Ticket
            {
                Id = ticketId,
                Reference = reference,
                CustomerId = Guid.NewGuid(),
                TicketTypeId = Guid.NewGuid(),
                TicketTypeVersionId = Guid.NewGuid(),
                CreatorUserAccountId = Guid.NewGuid(),
                SubjectEmployeeId = Guid.NewGuid(),
                Status = TicketStatus.Draft,
                Priority = TicketPriority.Normal,
                Title = "Rolled back",
                Version = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                LastActivityAt = DateTimeOffset.UtcNow,
            });
            db.TicketRevisions.Add(new TicketRevision
            {
                Id = Guid.NewGuid(),
                TicketId = ticketId,
                SequenceNumber = 1,
                SubmittedByUserAccountId = Guid.NewGuid(),
                SubmittedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // No commit. Disposal rolls back, which is exactly what RequestTransaction does on a failed
            // request.
        }

        Assert.Equal(0, await QueryScalar<long>(connectionString,
            $"SELECT COUNT(*) FROM tickets WHERE reference = '{reference}'"));
        Assert.Equal(0, await QueryScalar<long>(connectionString,
            "SELECT COUNT(*) FROM ticket_revisions r "
            + "WHERE NOT EXISTS (SELECT 1 FROM tickets t WHERE t.id = r.ticket_id)"));

        // The counter row went with it -- see the note above.
        Assert.Equal(0, await QueryScalar<long>(connectionString,
            $"SELECT COUNT(*) FROM ticket_reference_counters WHERE year = {year}"));
    }

    // --- SQL helpers ---

    private static string InsertTicket(
        string reference,
        string status = "'Draft'",
        string priority = "'Normal'",
        string assignee = "NULL",
        string closedAt = "NULL",
        string version = "1") =>
        "INSERT INTO tickets (reference, customer_id, ticket_type_id, ticket_type_version_id, "
        + "creator_user_account_id, subject_employee_id, status, assignee_user_account_id, priority, "
        + "title, version, closed_at) VALUES "
        + $"('{reference}', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), "
        + $"gen_random_uuid(), gen_random_uuid(), {status}, {assignee}, {priority}, "
        + $"'A ticket', {version}, {closedAt})";

    private static string InsertRevision(Guid ticketId, int sequenceNumber) =>
        "INSERT INTO ticket_revisions (ticket_id, sequence_number, submitted_by_user_account_id) "
        + $"VALUES ('{ticketId}', {sequenceNumber}, gen_random_uuid())";

    private static string InsertVerification(
        Guid fieldValueId, string outcome, string rejectionReason = "NULL") =>
        "INSERT INTO field_verifications (field_value_id, outcome, rejection_reason, "
        + $"verified_by_user_account_id) VALUES ('{fieldValueId}', {outcome}, {rejectionReason}, "
        + "gen_random_uuid())";

    private static string InsertMessage(Guid ticketId, string kind, string author) =>
        "INSERT INTO ticket_messages (ticket_id, author_user_account_id, kind, body) "
        + $"VALUES ('{ticketId}', {author}, {kind}, 'A message')";

    private static Task<Guid> InsertTicketReturningId(string connectionString, string reference) =>
        QueryScalarRequired<Guid>(connectionString, InsertTicket(reference) + " RETURNING id");

    private static Task<Guid> InsertRevisionReturningId(
        string connectionString, Guid ticketId, int sequenceNumber) =>
        QueryScalarRequired<Guid>(
            connectionString, InsertRevision(ticketId, sequenceNumber) + " RETURNING id");

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

    private static async Task<T> QueryScalarRequired<T>(string connectionString, string sql)
    {
        var value = await QueryScalar<T>(connectionString, sql);
        Assert.NotNull(value);
        return value!;
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
