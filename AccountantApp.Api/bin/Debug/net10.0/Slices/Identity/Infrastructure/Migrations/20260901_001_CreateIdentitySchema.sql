-- Identity slice schema. Plan section 1.
-- user_accounts first: user_account_tokens references it.

CREATE TABLE user_accounts (
    id                        UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The login identifier, stored twice on purpose. Uniqueness and every lookup use the
    -- normalized column; login_email keeps the address as the person typed it, because some mail
    -- systems do treat the local part as case-sensitive and mangling it is not free.
    login_email               VARCHAR(320) NOT NULL,
    normalized_login_email    VARCHAR(320) NOT NULL,

    -- NULL while the account is Invited. Not "" and not a placeholder hash: a placeholder is a
    -- password somebody could eventually guess.
    password_hash             VARCHAR(500) NULL,

    display_name              VARCHAR(200) NOT NULL,

    -- One of the four UserRole values, as text. Not a PostgreSQL enum: a new role must not need DDL.
    role                      VARCHAR(20)  NOT NULL,

    -- The Employee this account belongs to, and that Employee's Customer. Both NULL for the two
    -- Accountant roles. Neither is a foreign key: they point into another slice, and a cross-slice
    -- FK makes the two schemas one schema.
    employee_id               UUID NULL,
    customer_id               UUID NULL,

    -- 'Invited' | 'Active' | 'Suspended'
    status                    VARCHAR(20)  NOT NULL DEFAULT 'Invited',

    must_change_password      BOOLEAN      NOT NULL DEFAULT FALSE,
    email_confirmed_at        TIMESTAMPTZ  NULL,

    failed_login_count        INTEGER      NOT NULL DEFAULT 0,
    lockout_expires_at        TIMESTAMPTZ  NULL,

    created_at                TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_login_at             TIMESTAMPTZ  NULL,
    last_password_change_at   TIMESTAMPTZ  NULL,

    CONSTRAINT uq_user_accounts_normalized_email UNIQUE (normalized_login_email),

    -- An Accountant has no Employee and no Customer; a Customer-side account has both. The
    -- database enforces this because a session minted from a CustomerAdmin row with a NULL
    -- customer_id is unusable, and the failure surfaces one request later as a bare 401.
    CONSTRAINT ck_user_accounts_scope CHECK (
        (role IN ('AccountantAdmin', 'AccountantUser')
             AND employee_id IS NULL AND customer_id IS NULL)
        OR
        (role IN ('CustomerAdmin', 'Employee')
             AND employee_id IS NOT NULL AND customer_id IS NOT NULL)
    ),

    CONSTRAINT ck_user_accounts_status CHECK (status IN ('Invited', 'Active', 'Suspended'))
);

CREATE TABLE user_account_tokens (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Same slice, so a real foreign key is correct here.
    user_account_id  UUID NOT NULL REFERENCES user_accounts(id),

    -- 'Invitation' | 'PasswordReset'
    purpose          VARCHAR(30) NOT NULL,

    -- SHA-256 of the raw token, lowercase hex. The raw token is NEVER stored: a person with read
    -- access to this table cannot mint a session or take over an account.
    token_hash       CHAR(64)    NOT NULL,

    -- Absolute, computed at issue time. Invitations 7 days, password resets 1 hour.
    expires_at       TIMESTAMPTZ NOT NULL,

    -- NULL means unused. Set on redemption; the row is never deleted, so "this token was used, at
    -- this time" stays answerable.
    consumed_at      TIMESTAMPTZ NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_user_account_tokens_purpose CHECK (purpose IN ('Invitation', 'PasswordReset'))
);

-- The login lookup is the hottest query in the application. The UNIQUE constraint on
-- normalized_login_email already provides its index; there is deliberately no second index here.

-- Listing Accountants (matrix section 2 permits both Accountant roles to read this list).
-- Partial, because the two Accountant roles are a handful of rows in a table that grows with every
-- Employee of every Customer.
CREATE INDEX idx_user_accounts_accountants ON user_accounts (display_name, id)
    WHERE role IN ('AccountantAdmin', 'AccountantUser');

-- The at-least-one-Active-Admin guard counts this on every suspend and demote.
CREATE INDEX idx_user_accounts_active_admins ON user_accounts (id)
    WHERE role = 'AccountantAdmin' AND status = 'Active';

-- Employees asks "does this Employee have an account?" through IIdentityApi. UNIQUE because one
-- Employee has at most one UserAccount: two accounts means two sessions with the same scope and
-- different roles, and the second is invisible in every UI.
CREATE UNIQUE INDEX uq_user_accounts_employee ON user_accounts (employee_id)
    WHERE employee_id IS NOT NULL;

-- Token redemption looks up BY HASH, never by user. Unique so a hash collision -- or, far more
-- likely, a bug that reuses a token -- fails loudly at insert instead of silently authorizing.
CREATE UNIQUE INDEX uq_user_account_tokens_hash ON user_account_tokens (token_hash);

-- Invalidating a user's outstanding tokens of one purpose. Partial: consumed rows accumulate
-- forever and must not be scanned.
CREATE INDEX idx_user_account_tokens_outstanding
    ON user_account_tokens (user_account_id, purpose)
    WHERE consumed_at IS NULL;
