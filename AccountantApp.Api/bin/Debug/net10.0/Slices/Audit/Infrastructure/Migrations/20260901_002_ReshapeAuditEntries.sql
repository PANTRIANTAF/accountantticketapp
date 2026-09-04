-- Brings audit_entries from the shape 20260828_001 created (id, action, target_id, actor, details,
-- occurred_at) to the shape AuditRecord and AuditRecordConfiguration actually map, and adds the
-- outcome CHECK.
--
-- Why this is a second script rather than an edit to 001: 001 is already pushed to origin, and
-- SqlMigrationRunner records only script_name in schema_versions -- no checksum. So a database that
-- started the app on the earlier commit has 001 recorded against a six-column table and would never
-- re-run it. Editing 001 in place fixes only databases that have never run it, and leaves every
-- other one permanently wrong with no error to say so. Every statement below is guarded, so this
-- converges from either starting point: a fresh database that just ran 001, or a stale one.
--
-- The columns are added, backfilled, then stripped of their temporary defaults, because the target
-- shape has actor_user_id, actor_role, target_kind and outcome as NOT NULL with no default -- the
-- application always supplies them, and a default would silently mask a missing value.

ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS actor_user_id VARCHAR(100) NOT NULL DEFAULT '';
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS actor_role    VARCHAR(30)  NOT NULL DEFAULT '';
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS customer_id   UUID         NULL;
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS target_kind   VARCHAR(50)  NOT NULL DEFAULT 'None';
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS outcome       VARCHAR(20)  NOT NULL DEFAULT 'Success';
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS before_value  JSONB        NULL;
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS after_value   JSONB        NULL;
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS source_ip     VARCHAR(45)  NOT NULL DEFAULT '';
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS user_agent    VARCHAR(512) NOT NULL DEFAULT '';

-- Carry the old free-text actor across before dropping it. Truncated to the new width; the old
-- column was VARCHAR(255). 'details' has no counterpart in the new shape -- before_value and
-- after_value are structured -- so it is dropped rather than mapped.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'audit_entries' AND column_name = 'actor') THEN
        UPDATE audit_entries
           SET actor_user_id = LEFT(actor, 100)
         WHERE actor_user_id = '' AND actor <> '';
    END IF;
END $$;

ALTER TABLE audit_entries DROP COLUMN IF EXISTS actor;
ALTER TABLE audit_entries DROP COLUMN IF EXISTS details;

-- target_id narrows from VARCHAR(255) to VARCHAR(100), so anything longer is truncated first;
-- a bare ALTER TYPE would fail with 22001 on a stale row.
UPDATE audit_entries SET target_id = LEFT(target_id, 100) WHERE LENGTH(target_id) > 100;
ALTER TABLE audit_entries ALTER COLUMN target_id TYPE VARCHAR(100);

ALTER TABLE audit_entries ALTER COLUMN actor_user_id DROP DEFAULT;
ALTER TABLE audit_entries ALTER COLUMN actor_role    DROP DEFAULT;
ALTER TABLE audit_entries ALTER COLUMN target_kind   DROP DEFAULT;
ALTER TABLE audit_entries ALTER COLUMN outcome       DROP DEFAULT;

CREATE INDEX IF NOT EXISTS idx_audit_entries_occurred_at ON audit_entries (occurred_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS idx_audit_entries_actor       ON audit_entries (actor_user_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_audit_entries_target      ON audit_entries (target_kind, target_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_audit_entries_customer    ON audit_entries (customer_id, occurred_at DESC)
    WHERE customer_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_audit_entries_action      ON audit_entries (action, occurred_at DESC);

-- outcome is one of exactly three values (AuditOutcome). AuditApi rejects anything else before the
-- insert; this constraint is the backstop, and it matters more here than on an ordinary table
-- because audit_entries is append-only. A row with outcome 'success' or 'Denied ' can never be
-- corrected by an UPDATE -- there is no UPDATE path -- so it would stay invisible to the audit
-- reader's outcome filter for the lifetime of the deployment.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_audit_entries_outcome') THEN
        ALTER TABLE audit_entries
            ADD CONSTRAINT ck_audit_entries_outcome CHECK (outcome IN ('Success', 'Denied', 'Failure'));
    END IF;
END $$;

COMMENT ON TABLE audit_entries IS
    'Append-only. No UPDATE or DELETE path exists in the application. See 01-DomainModel.md section 8.';
