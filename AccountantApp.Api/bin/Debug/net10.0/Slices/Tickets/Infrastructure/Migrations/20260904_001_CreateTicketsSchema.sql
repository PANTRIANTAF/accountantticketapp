-- Tickets slice schema. Plan section 1 and section 10. Six tables plus the reference counter.
--
-- Hand-written, like every other slice's. `dotnet ef migrations add` is never used here: it would
-- generate PascalCase columns, a __EFMigrationsHistory table, and a C# model snapshot that fights
-- the one-DbContext-per-slice rule and the snake_case mapping this application maps by hand.
--
-- The due-date scanner's idempotency table (ticket_due_date_reminders) is DELIBERATELY ABSENT --
-- plan section 9a.1 puts it in a second, later migration (20260905_001_CreateDueDateReminders.sql).
-- Migrations are append-only, so a second file is the normal way to add a table. Do not fold it in.
--
-- Foreign keys: every FK below stays inside this slice. Nothing points at customers,
-- user_accounts, employees, ticket_type_versions or documents -- those belong to other slices, and
-- a cross-slice FK makes two schemas one schema.
--
-- No rollback script. No DELETE, no deleted_at, no soft-delete flag on any table here: matrix
-- section 7 grants deleting a ticket to nobody, and 01-DomainModel.md section 9.2 makes Document
-- the only entity in the system with a soft delete. `Cancelled` is a status, not a removal -- a
-- cancelled ticket stays readable and keeps its revisions, messages and documents.

CREATE TABLE tickets (
    id                       UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Human-readable, unique, never reused, never changed. Format TKT-{year}-{000000}.
    -- Allocated by TicketReferenceAllocator (section 1.7), including for a Draft.
    reference                VARCHAR(20) NOT NULL,

    -- The tenant boundary (ICustomerScoped). Immutable. No FK: another slice.
    customer_id              UUID NOT NULL,

    -- Type AND the specific version. Both immutable. No FK: another slice.
    -- The version id is stored, not just the type id, so a later version of the type does not
    -- change what an existing ticket asked for.
    ticket_type_id           UUID NOT NULL,
    ticket_type_version_id   UUID NOT NULL,

    -- The UserAccount that created it. Immutable.
    creator_user_account_id  UUID NOT NULL,

    -- The Employee the ticket is about. Immutable. No FK: another slice.
    subject_employee_id      UUID NOT NULL,

    -- 'Draft'|'Submitted'|'InReview'|'AwaitingInformation'|'Answered'|'Closed'|'Cancelled'
    status                   VARCHAR(30) NOT NULL DEFAULT 'Draft',

    -- The Accountant responsible. NULL in Draft/Cancelled, REQUIRED in
    -- InReview/AwaitingInformation/Answered/Closed, optional in Submitted -- see
    -- ck_tickets_assignee.
    assignee_user_account_id UUID NULL,

    -- 'Normal' | 'High'. Accountant-only.
    priority                 VARCHAR(10) NOT NULL DEFAULT 'Normal',

    -- DATE, not TIMESTAMPTZ: a statutory deadline falls on a day. A timezone-shifted timestamp
    -- turns a due date into the previous day for half the world.
    due_date                 DATE NULL,

    -- Derived from the Type name plus the Subject, at creation, so lists read well. Never supplied
    -- by a client, and not recomputed if the Employee or the Type is later renamed (section 12
    -- constraint 4, section 13 item 7).
    title                    VARCHAR(300) NOT NULL,

    -- The current revision. Nullable ONLY between the two inserts, and with NO foreign key:
    -- tickets and ticket_revisions reference each other, so the ticket row is inserted first, then
    -- revision 1, then the ticket is updated. An FK here would make that impossible. See 1.3.
    current_revision_id      UUID NULL,

    -- 01-DomainModel.md section 9.1. Set at creation only, immutable thereafter. Self-referential
    -- and therefore intra-slice, which is why this one column DOES get an FK.
    preceded_by_ticket_id    UUID NULL REFERENCES tickets(id),

    -- 01-DomainModel.md section 9.7. Hand-incremented on EVERY write to this row, by
    -- TicketConcurrency.Touch. NOT xmin: an opaque provider-specific token does not belong in a
    -- contract the SPA has to round-trip.
    version                  INTEGER NOT NULL DEFAULT 1,

    created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_activity_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    closed_at                TIMESTAMPTZ NULL,

    CONSTRAINT uq_tickets_reference UNIQUE (reference),

    CONSTRAINT ck_tickets_status CHECK (status IN (
        'Draft','Submitted','InReview','AwaitingInformation','Answered','Closed','Cancelled')),

    CONSTRAINT ck_tickets_priority CHECK (priority IN ('Normal','High')),

    -- 01-DomainModel.md section 3: the Assignee is ABSENT in Draft/Submitted/Cancelled and
    -- REQUIRED in InReview/AwaitingInformation/Answered/Closed.
    --
    -- ONE EXCEPTION, and it is the trap in section 5 of the domain model:
    -- AwaitingInformation -> Submitted RETAINS the Assignee, so 'Submitted' may have one.
    --
    -- DO NOT "TIGHTEN" THE THIRD BRANCH. Written as
    --     (status = 'Submitted' AND assignee_user_account_id IS NULL)
    -- it looks obviously right and rejects every correction round in the system.
    CONSTRAINT ck_tickets_assignee CHECK (
        (status IN ('InReview','AwaitingInformation','Answered','Closed')
             AND assignee_user_account_id IS NOT NULL)
        OR
        (status IN ('Draft','Cancelled') AND assignee_user_account_id IS NULL)
        OR
        (status = 'Submitted')          -- may or may not have one. See above.
    ),

    CONSTRAINT ck_tickets_closed CHECK (
        (status = 'Closed' AND closed_at IS NOT NULL)
        OR
        (status <> 'Closed' AND closed_at IS NULL)
    ),

    CONSTRAINT ck_tickets_version CHECK (version >= 1)
);

CREATE TABLE ticket_revisions (
    id                           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_id                    UUID NOT NULL REFERENCES tickets(id),

    -- Starts at 1. Revision 1 is created together with the Ticket.
    sequence_number              INTEGER NOT NULL,

    submitted_by_user_account_id UUID NOT NULL,
    submitted_at                 TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Optional note explaining what changed, written by the submitter.
    note                         VARCHAR(2000) NULL,

    -- What makes two concurrent corrections impossible to interleave into a duplicate revision 2.
    -- One of them gets 23505 and the handler maps it to 409 -- never a 500.
    CONSTRAINT uq_ticket_revisions_sequence UNIQUE (ticket_id, sequence_number),
    CONSTRAINT ck_ticket_revisions_sequence CHECK (sequence_number >= 1)
);

-- Append-only: a revision, once written, is never modified and never deleted. To see what an
-- Employee originally claimed you read revision 1. No version column here or on any other table in
-- this script -- section 9.7 puts optimistic concurrency on the tickets row alone, and an
-- append-only table has nothing to conflict on.

CREATE TABLE field_values (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_revision_id  UUID NOT NULL REFERENCES ticket_revisions(id),

    -- The FieldDescriptor key it answers. A string, not an FK: descriptors are another slice's
    -- rows, and the key is what survives a version change.
    field_key           VARCHAR(100) NOT NULL,

    -- Typed columns rather than one TEXT, because 01-DomainModel.md section 3 requires the value be
    -- stored "in a form that preserves the declared data type". One TEXT column defers the question
    -- to every reader and the readers disagree -- one parses "1.500" as fifteen hundred, another as
    -- one and a half.
    value_text          TEXT NULL,

    -- NUMERIC(18,4), never float/double/real. MoneyAmount is money, a binary float cannot represent
    -- 0.10, and this is an accounting application: a rounding artefact in a tax figure is the worst
    -- class of bug this codebase can produce.
    value_number        NUMERIC(18,4) NULL,

    value_date          DATE NULL,
    value_date_to       DATE NULL,          -- DateRange only. No CHECK: see below.
    value_boolean       BOOLEAN NULL,
    value_document_id   UUID NULL,          -- FileUpload only. No FK: another slice.

    -- Whether this value was carried forward unchanged from the previous revision, or newly entered
    -- in this one. Not cosmetic: it is what tells the Accountant which fields need attention.
    is_carried_forward  BOOLEAN NOT NULL DEFAULT FALSE,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- A revision holds ONE answer per field. Without this a correction that writes a second row for
    -- the same key produces two answers and every read picks whichever the query returns first.
    CONSTRAINT uq_field_values_revision_key UNIQUE (ticket_revision_id, field_key),

    -- Plan section 13 item 4, RESOLVED: yes, add it.
    --
    -- This table has no data_type column -- the type lives on the descriptor, in another slice -- so
    -- the database cannot check that a WholeNumber landed in value_number rather than value_text.
    -- That much is genuinely unenforceable here. But it CAN enforce the weaker invariant that holds
    -- for all eleven types regardless of which one a row is: at most ONE of the five primary
    -- carriers is populated. Verified against every type:
    --
    --   SingleLineText, MultiLineText, SingleChoice, MultipleChoice -> value_text
    --      (MultipleChoice holds a JSON array of chosen values; it is the one non-atomic value, and
    --       it is still one column)
    --   WholeNumber, DecimalNumber, MoneyAmount                     -> value_number
    --   Date                                                        -> value_date
    --   DateRange                                        -> value_date + value_date_to
    --   YesNo                                                       -> value_boolean
    --   FileUpload                                                  -> value_document_id
    --
    -- value_date_to is deliberately NOT counted: it is the companion of value_date, not a carrier of
    -- its own, so a DateRange populates one carrier plus its end. See ck_field_values_date_range.
    --
    -- What this catches is half the bug class and the more damaging half: a switch that falls
    -- through and writes two columns, or a mapper that populates the carrier for the value it was
    -- handed AND the one for the type it expected. Both produce a row with two answers where every
    -- reader picks a different one, and neither is visible until an Accountant reads a figure that
    -- does not match what the Customer typed. All five null is permitted -- a Draft may hold a blank
    -- answer for a field the user has not reached yet, and section 6.4 does not require one.
    CONSTRAINT ck_field_values_one_carrier CHECK (
        (CASE WHEN value_text        IS NOT NULL THEN 1 ELSE 0 END
       + CASE WHEN value_number      IS NOT NULL THEN 1 ELSE 0 END
       + CASE WHEN value_date        IS NOT NULL THEN 1 ELSE 0 END
       + CASE WHEN value_boolean     IS NOT NULL THEN 1 ELSE 0 END
       + CASE WHEN value_document_id IS NOT NULL THEN 1 ELSE 0 END) <= 1
    ),

    -- A range with an end and no start is not a range. Cheap, and it closes the one ordering mistake
    -- the carrier check above cannot see, because value_date_to is excluded from that count.
    CONSTRAINT ck_field_values_date_range CHECK (
        value_date_to IS NULL OR value_date IS NOT NULL
    )
);

-- There is deliberately NO CHECK tying the populated column to the field's data type, and none
-- asserting that exactly one value column is non-null: this table does not know the data type,
-- which lives in TicketTypes. FieldValueValidation is the only guard, and plan section 13 item 4
-- raises whether that is good enough. DateRange's "to >= from" is in the same position: there is no
-- CHECK for it (section 1.4 rule 3), so the validator is the only place it is enforced.

CREATE TABLE field_verifications (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Attaches to a FieldValue in a SPECIFIC revision, so the verification history of a corrected
    -- field is fully preserved. 01-DomainModel.md section 3.
    field_value_id      UUID NOT NULL REFERENCES field_values(id),

    -- 'Accepted' | 'Rejected'
    outcome             VARCHAR(20) NOT NULL,

    -- Required when rejected. Shown VERBATIM to the Customer side.
    rejection_reason    VARCHAR(2000) NULL,

    verified_by_user_account_id UUID NOT NULL,
    verified_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_field_verifications_outcome CHECK (outcome IN ('Accepted','Rejected')),

    -- A rejected field with no reason is useless to the Customer side, and the reason is "shown
    -- verbatim", so an empty or whitespace-only string is as bad as a null -- which is why this is
    -- length(trim(...)) > 0 and not merely NOT NULL.
    CONSTRAINT ck_field_verifications_reason CHECK (
        (outcome = 'Rejected' AND rejection_reason IS NOT NULL AND length(trim(rejection_reason)) > 0)
        OR
        (outcome = 'Accepted' AND rejection_reason IS NULL)
    )
);

-- Append-only. A re-verification APPENDS a new row; the latest by verified_at (tie-broken by id) is
-- current. Never UPDATE an existing row -- the verification history is the point.

CREATE TABLE ticket_messages (
    id                     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_id              UUID NOT NULL REFERENCES tickets(id),

    -- NULL for SystemEvent: written by the application, not a person.
    author_user_account_id UUID NULL,

    -- 'CustomerMessage'|'AccountantResponse'|'InternalNote'|'SystemEvent'
    -- Derived from the caller's ROLE, never from the request body.
    kind                   VARCHAR(30) NOT NULL,

    body                   TEXT NOT NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_ticket_messages_kind CHECK (kind IN (
        'CustomerMessage','AccountantResponse','InternalNote','SystemEvent')),

    -- A SystemEvent has no human author; everything else must have one.
    CONSTRAINT ck_ticket_messages_author CHECK (
        (kind = 'SystemEvent' AND author_user_account_id IS NULL)
        OR
        (kind <> 'SystemEvent' AND author_user_account_id IS NOT NULL)
    )
);

-- Append-only: messages are not edited or deleted. No edited_at, no deleted_at, no update handler.

-- 01-DomainModel.md section 3: a TicketMessage has "Attached Documents".
--
-- This table exists because a document is attached to a TICKET in the Documents schema (its
-- ticket_id column) but to a MESSAGE in the conversation. Both are true: the document belongs to the
-- ticket for authorization and to a message for rendering. documents.ticket_id remains the
-- authorization anchor, and section 0.3 step 5 still checks it.
CREATE TABLE ticket_message_documents (
    ticket_message_id   UUID NOT NULL REFERENCES ticket_messages(id),
    document_id         UUID NOT NULL,      -- No FK: another slice.
    PRIMARY KEY (ticket_message_id, document_id)
);

-- The ticket reference sequence restarts each year, which rules out a plain PostgreSQL SEQUENCE.
-- A counter table plus one atomic upsert (see TicketReferenceAllocator) is what makes concurrent
-- allocation safe. The year comes from the application clock and is passed in -- calling NOW() here
-- and DateTime.Now in the C# string produces a mismatched reference on New Year's Eve.
CREATE TABLE ticket_reference_counters (
    year           INTEGER PRIMARY KEY,
    last_sequence  INTEGER NOT NULL
);

-- Indexes ------------------------------------------------------------------------------------

-- The pickup queue, condition 1: Submitted with NO assignee. 01-DomainModel.md section 9.8.
-- The predicate must match the handler's WHERE exactly, INCLUDING the assignee IS NULL: a partial
-- index whose predicate is narrower than the query is unusable, and this is the hottest query the
-- Office runs.
CREATE INDEX idx_tickets_pickup
    ON tickets (last_activity_at)
    WHERE status = 'Submitted' AND assignee_user_account_id IS NULL;

-- The pickup queue, condition 2, and "assigned to me": by assignee, open statuses only.
CREATE INDEX idx_tickets_assignee_open
    ON tickets (assignee_user_account_id, last_activity_at)
    WHERE status IN ('Submitted','InReview','AwaitingInformation','Answered');

-- Every Customer-side list, in the default sort order (last_activity_at DESC, id DESC). The id
-- tiebreaker is mandatory: two tickets touched in one transaction share a last_activity_at to the
-- microsecond, and an unstable sort makes paging skip and repeat rows.
CREATE INDEX idx_tickets_customer_activity
    ON tickets (customer_id, last_activity_at DESC, id DESC);

-- An Employee's own tickets: Creator or Subject. Two indexes, because it is an OR -- PostgreSQL can
-- combine them with a bitmap OR. If the plan is bad, rewrite the query as a UNION of two indexed
-- halves rather than adding a composite index, which cannot serve an OR. Measure before optimising.
CREATE INDEX idx_tickets_creator  ON tickets (creator_user_account_id, last_activity_at DESC);
CREATE INDEX idx_tickets_subject  ON tickets (subject_employee_id, last_activity_at DESC);

-- Lookup by the human-readable reference (the search box) is already covered by
-- uq_tickets_reference; there is deliberately no second index for it.

CREATE INDEX idx_ticket_revisions_ticket   ON ticket_revisions (ticket_id, sequence_number);
CREATE INDEX idx_field_values_revision     ON field_values (ticket_revision_id);
CREATE INDEX idx_field_verifications_value  ON field_verifications (field_value_id, verified_at);
CREATE INDEX idx_ticket_messages_ticket    ON ticket_messages (ticket_id, created_at);
