CREATE TABLE notifications (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The UserAccount that receives it. No foreign key: this slice may not depend on Identity.
    recipient_user_id   VARCHAR(100) NOT NULL,

    -- The Ticket it concerns. Nullable: an invitation notification concerns no Ticket.
    -- No foreign key to tickets — Notifications must not depend on Tickets.
    ticket_id           UUID NULL,

    -- Event kind, from the fixed catalogue in ExternalInterfaces/NotificationEvents.cs.
    event_kind          VARCHAR(100) NOT NULL,

    title               VARCHAR(200)  NOT NULL,
    body                VARCHAR(2000) NOT NULL,

    is_read             BOOLEAN     NOT NULL DEFAULT FALSE,
    read_at             TIMESTAMPTZ NULL,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE notification_outbox (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The Notification this email is for. Same slice, so a real foreign key is correct here.
    notification_id     UUID NOT NULL REFERENCES notifications(id),

    -- Resolved at send time, not at enqueue time, so an address change is picked up.
    -- Recorded here once resolved, for diagnostics.
    resolved_email      VARCHAR(320) NULL,

    -- The email body, when it must differ from the notification body because it carries a
    -- secret. NULL means "use the notification's body". Blanked by the drainer on success.
    email_body          VARCHAR(4000) NULL,

    -- Status: 'Pending', 'Sent', 'Failed', 'Abandoned', 'Skipped'
    status              VARCHAR(20) NOT NULL DEFAULT 'Pending',
    attempt_count       INTEGER     NOT NULL DEFAULT 0,
    next_attempt_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_error          VARCHAR(1000) NULL,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    sent_at             TIMESTAMPTZ NULL
);

-- The notification centre: this user's list, newest first. Every read endpoint uses it.
CREATE INDEX idx_notifications_recipient ON notifications (recipient_user_id, created_at DESC, id DESC);

-- The unread badge count. Partial: only unread rows are ever counted, and the unread set
-- stays small while the table grows forever.
CREATE INDEX idx_notifications_unread ON notifications (recipient_user_id)
    WHERE is_read = FALSE;

-- The drainer's only query. Partial on Pending: Sent rows accumulate forever and must not
-- be scanned. This index is what keeps the background loop cheap.
CREATE INDEX idx_outbox_due ON notification_outbox (next_attempt_at)
    WHERE status = 'Pending';

-- List projection in §7.1 requires lookup by notification_id.
CREATE INDEX idx_outbox_notification ON notification_outbox (notification_id);
