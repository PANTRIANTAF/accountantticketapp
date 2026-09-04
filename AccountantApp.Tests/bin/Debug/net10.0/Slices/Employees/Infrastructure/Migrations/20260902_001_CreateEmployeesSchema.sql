-- Employees slice schema. Plan section 1. One table.
--
-- No foreign keys leave this table. customers and user_accounts belong to other slices, and a
-- cross-slice FK makes the two schemas one schema -- Identity's migration cannot then change
-- without breaking this slice's inserts.

CREATE TABLE employees (
    id                        UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The tenant boundary and the ICustomerScoped property. Immutable after creation: there is no
    -- move-an-Employee operation, because user_accounts.customer_id is supplied by this slice at
    -- invitation time and is only safe to store there BECAUSE this value can never go stale.
    customer_id               UUID NOT NULL,

    given_name                VARCHAR(100) NOT NULL,
    family_name               VARCHAR(100) NOT NULL,

    -- The login identifier IF and WHEN they are invited, and until then just a note on a record.
    -- NULL for an accountless Employee with no address on file. Deliberately NOT globally unique:
    -- see uq_employees_customer_email below.
    work_email                VARCHAR(320) NULL,
    normalized_work_email     VARCHAR(320) NULL,

    -- The account, once one exists. NULL for an accountless Employee.
    user_account_id           UUID NULL,

    -- Personal identifying numbers the Office needs. VARCHAR, never numeric: leading zeros are
    -- significant, formats vary, and nothing arithmetic is ever done with them.
    tax_identification_number VARCHAR(50) NULL,
    social_security_number    VARCHAR(50) NULL,

    job_title                 VARCHAR(200) NULL,

    -- DATE, not TIMESTAMPTZ. Employment starts on a day, not at an instant, and a
    -- timezone-shifted timestamp turns a start date into the previous day for half the world.
    employment_start_date     DATE NOT NULL,
    employment_end_date       DATE NULL,

    -- 'Active' | 'Departed'. Departure is reversible only as a correction, by /reinstate, which clears
    -- employment_end_date and departed_at again. A genuine re-hire is a new row.
    status                    VARCHAR(20) NOT NULL DEFAULT 'Active',

    contact_phone             VARCHAR(50) NULL,

    created_at                TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at                TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- The audit-relevant instant, separate from the DATE the employment ended. Both exist because
    -- they answer different questions: "when was this recorded" and "when did they stop working".
    departed_at               TIMESTAMPTZ NULL,

    -- An employment end date is set exactly when the person has departed, and never before.
    --
    -- This also constrains status to the two legal values on its own -- any third value fails both
    -- branches -- so there is deliberately no separate ck_employees_status. Adding one would be a
    -- second constraint expressing a rule this one already holds, and the two could then disagree.
    CONSTRAINT ck_employees_departure CHECK (
        (status = 'Active'   AND departed_at IS NULL AND employment_end_date IS NULL)
        OR
        (status = 'Departed' AND departed_at IS NOT NULL)
    ),

    -- An end date, when present, cannot precede the start date.
    CONSTRAINT ck_employees_dates CHECK (
        employment_end_date IS NULL OR employment_end_date >= employment_start_date
    ),

    -- The two email columns are populated together or not at all. A row with a work_email and no
    -- normalized_work_email is invisible to every lookup, which reads as "no such Employee".
    CONSTRAINT ck_employees_email_pair CHECK (
        (work_email IS NULL AND normalized_work_email IS NULL)
        OR
        (work_email IS NOT NULL AND normalized_work_email IS NOT NULL)
    )
);

-- The list endpoint, in its sort order, scoped to a Customer. Covers the common query.
CREATE INDEX idx_employees_customer_name
    ON employees (customer_id, family_name, given_name, id);

-- Two Employees at ONE Customer must not share a work email. Partial, because NULL is common and
-- NULLs are not comparable anyway -- the WHERE makes the intent explicit.
--
-- NOT global. An accountless Employee's work_email is a note, not a credential, and two Customers
-- may each have an Employee with a shared family address on file. Uniqueness across the system is
-- enforced by Identity at invitation time, when the address becomes a login identifier. A global
-- index here would make registering an Employee fail because an UNRELATED Customer has that
-- address on file, and the error could not say why without leaking another Customer's data.
CREATE UNIQUE INDEX uq_employees_customer_email
    ON employees (customer_id, normalized_work_email)
    WHERE normalized_work_email IS NOT NULL;

-- One account belongs to at most one Employee. Two Employee rows pointing at one account means
-- two Customer scopes for one session, and whichever one a query finds first wins.
CREATE UNIQUE INDEX uq_employees_user_account
    ON employees (user_account_id)
    WHERE user_account_id IS NOT NULL;

-- The at-least-one-Active-CustomerAdmin guard and the "who can be a Ticket Subject" query both
-- filter on Active within a Customer.
CREATE INDEX idx_employees_customer_active
    ON employees (customer_id)
    WHERE status = 'Active';

-- Case-insensitive substring search on the three columns /api/employees/list searches. The handler
-- uses ILIKE '%term%', which no b-tree index can serve, so these are trigram indexes -- the same
-- approach the Customers slice takes for legal_name.
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE INDEX idx_employees_name_trgm
    ON employees USING gin (given_name gin_trgm_ops, family_name gin_trgm_ops);
CREATE INDEX idx_employees_email_trgm
    ON employees USING gin (normalized_work_email gin_trgm_ops);

-- No DELETE, no deleted_at, no soft-delete flag: matrix section 4 grants deleting an Employee
-- record to nobody, and Departed is not a delete. A departed Employee stays in this table, stays
-- visible to their Customer Admin forever, and stays the Subject of every Ticket they ever were.
