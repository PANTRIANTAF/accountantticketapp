-- The due-date scanner's idempotency table. Plan section 9a.1 and section 9a.3.
--
-- THIS IS A SECOND, LATER MIGRATION, and that is the point of it. 20260904_001_CreateTicketsSchema.sql
-- stays exactly as specified -- six tables plus the reference counter -- and success criterion 1 stays
-- true as written. Migrations here are append-only (section 10), so adding a table in a new file is the
-- normal way to do this, not a workaround for a script that was already shipped. Do NOT fold this into
-- the first script.
--
-- The runner (Shared/Migrations/SqlMigrationRunner.cs) picks this up with no registration anywhere: it
-- enumerates *.sql under Slices/**/Infrastructure/Migrations, orders by the YYYYMMDD_### prefix, and
-- records each script under its slice-relative path with forward slashes -- here
-- "Tickets/Infrastructure/Migrations/20260905_001_CreateDueDateReminders.sql". 20260905 sorts after
-- 20260904, so tickets exists before this references it. The .csproj already copies the whole glob
-- (Slices\**\Infrastructure\Migrations\*.sql, PreserveNewest), so no build action is added for this
-- file either.
--
-- No rollback script. No DELETE and no soft-delete flag: section 1.9, and section 9a.3 specifically --
-- a re-armed reminder is a NEW row for the new due date, never a removal of the old one.

CREATE TABLE ticket_due_date_reminders (
    -- Intra-slice FK, so it gets one. This is the only column here that points anywhere.
    ticket_id   UUID NOT NULL REFERENCES tickets(id),

    -- The due date the reminder was sent FOR. DATE, matching tickets.due_date exactly: the scanner
    -- compares the two, and a TIMESTAMPTZ here would compare a moment against a calendar day.
    due_date    DATE NOT NULL,

    -- When it went out. An instant, unlike due_date, and used for diagnosis only -- no decision the
    -- scanner makes reads it.
    sent_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- THE IDEMPOTENCY GUARANTEE, and the reason this table exists at all.
    --
    -- Keyed on (ticket_id, due_date), not on ticket_id alone. A one-row-per-ticket key -- or the
    -- reminded BOOLEAN / last_reminded_at column somebody will propose putting on tickets -- suppresses
    -- the reminder forever after the first send, so an Accountant who pushes a due date out by a month
    -- is never reminded of the new date. Including the due date in the key means changing
    -- tickets.due_date RE-ARMS the reminder with no reset step and no second code path.
    --
    -- It is also the reason a duplicate send is impossible rather than merely unlikely: the scanner
    -- INSERTs the marker and only then raises the notification, both inside ONE transaction, so a
    -- second pass racing the first blocks on this key, gets 23505 when the first commits, and rolls
    -- back its own notification with it. That is a stronger guarantee than the OutboxDrainer has, and
    -- it is why this constraint is load-bearing rather than decoration. Section 9a.2 rule 11 still
    -- says single-replica, and the scanner still says so in its own doc comment -- but the reason is
    -- wasted work and a shared advisory-lock-free scan, not a duplicated reminder. Flagged there.
    CONSTRAINT pk_ticket_due_date_reminders PRIMARY KEY (ticket_id, due_date)
);

-- No further index. The scanner's only read is "which of these ticket ids already have a marker",
-- which the primary key's leading column serves; and there is no query by sent_at, by date range, or
-- by anything else, because nothing on a request path reads this table.
