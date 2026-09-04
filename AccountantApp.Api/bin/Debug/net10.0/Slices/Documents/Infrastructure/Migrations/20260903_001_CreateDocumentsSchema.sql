-- Documents slice schema. Plan section 1. TWO tables, and the separation is deliberate.
--
-- The metadata and the bytes are split because PostgreSQL would TOAST a BYTEA into a side table
-- anyway, and because a single table makes `SELECT * FROM documents` -- which is what EF's default
-- entity materialisation issues -- read every byte of every document just to render ten file names.
-- With the bytes in their own table that mistake is not available. Plan section 1.1.
--
-- No foreign keys LEAVE this slice: customer_id and ticket_id belong to Customers and Tickets, and a
-- cross-slice FK would make two schemas one schema. The one FK below is intra-slice and therefore
-- correct.

CREATE TABLE documents (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The tenant boundary (ICustomerScoped) and the ticket it belongs to.
    -- No foreign keys: both belong to other slices.
    --
    -- customer_id is denormalised from the ticket ON PURPOSE and cannot go stale: a ticket belongs to
    -- the Customer it was opened for and there is no operation that moves one. Plan section 1.2.
    --
    -- The ticket's STATUS is deliberately NOT copied here. It is mutable, it governs whether a
    -- Customer-side actor may still delete their own upload, and a copy would be wrong the moment an
    -- Accountant picks the ticket up. Tickets evaluates status live. Do not "complete" the
    -- denormalisation.
    customer_id       UUID NOT NULL,
    ticket_id         UUID NOT NULL,

    -- 'CustomerUpload' | 'AccountantResponse'. Derived by Tickets from the uploader's ROLE, never
    -- client-supplied: a Customer who could set this would mark their own upload as an Accountant
    -- response and change what the ticket appears to say.
    origin            VARCHAR(30) NOT NULL,

    -- As supplied by the client, sanitised. NEVER used as a filesystem path.
    original_file_name VARCHAR(255) NOT NULL,

    -- The type this slice DETERMINED from the leading bytes -- not the declared header.
    content_type      VARCHAR(100) NOT NULL,
    size_bytes        BIGINT NOT NULL,

    -- SHA-256 of the content, hex. For integrity and duplicate reporting only -- see below.
    content_hash      CHAR(64) NOT NULL,

    uploaded_by_user_account_id UUID NOT NULL,
    uploaded_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- The one soft delete in the system. 01-DomainModel.md section 9.2.
    deleted_at        TIMESTAMPTZ NULL,
    deleted_by_user_account_id UUID NULL,

    CONSTRAINT ck_documents_origin CHECK (origin IN ('CustomerUpload', 'AccountantResponse')),

    -- The two soft-delete columns are set together or not at all. A row with a
    -- deleted_at and no deleter cannot answer "who deleted it", which 01-DomainModel.md section 6
    -- requires.
    CONSTRAINT ck_documents_deletion CHECK (
        (deleted_at IS NULL     AND deleted_by_user_account_id IS NULL)
        OR
        (deleted_at IS NOT NULL AND deleted_by_user_account_id IS NOT NULL)
    ),

    -- 26214400 is 25 * 1024 * 1024, written as the literal on purpose: 25 * 1000 * 1000 is a
    -- DIFFERENT number from the one the proxy will be configured with, and uploads between the two
    -- limits then fail at the proxy with an error the application never sees.
    --
    -- Belt and braces with UploadValidation.MaxUploadSizeBytes. An app-side cap changed in one of two
    -- places leaves the other wrong, and the database is the one that cannot be bypassed.
    CONSTRAINT ck_documents_size CHECK (size_bytes > 0 AND size_bytes <= 26214400)
);

CREATE TABLE document_contents (
    -- The only foreign key in this schema, and it is intra-slice: it guarantees no orphaned bytes and
    -- no bytes without metadata.
    --
    -- There is deliberately NO ON DELETE CASCADE. Nothing is ever deleted (01-DomainModel.md section
    -- 9.2), and a cascade clause is harmless but advertises an operation that must not exist.
    document_id UUID PRIMARY KEY REFERENCES documents(id),
    content     BYTEA NOT NULL
);

-- The one query that matters: a ticket's live documents, in upload order.
-- Partial, matching the global query filter, so the filter is free.
--
-- The WHERE mirrors HasQueryFilter(d => d.DeletedAt == null) EXACTLY. If the two ever disagree the
-- index silently stops being usable for the slice's main query, so change them together or not at all.
CREATE INDEX idx_documents_ticket
    ON documents (ticket_id, uploaded_at)
    WHERE deleted_at IS NULL;

-- Defence in depth for the scope filter, and the only cross-Customer query shape.
CREATE INDEX idx_documents_customer
    ON documents (customer_id)
    WHERE deleted_at IS NULL;

-- "Has this exact file already been put on this ticket?" -- NOT unique, and it must never become
-- unique. The same PDF legitimately appears on two tickets at two Customers; deduplicating would mean
-- one row's bytes serving two documents, and soft-deleting one of them would then either break the
-- other or do nothing. Plan section 1.3.
CREATE INDEX idx_documents_ticket_hash
    ON documents (ticket_id, content_hash);

-- No DELETE statement anywhere, in this script or in any handler. A soft delete is not a delete: the
-- row stays, THE BYTES STAY, and the flag hides it. There is no undelete, no hard delete, and no purge
-- job -- 01-DomainModel.md section 9.2 forbids background work that removes data.
