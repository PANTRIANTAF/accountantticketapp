CREATE TABLE ticket_types (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code VARCHAR(100) NOT NULL,
    display_name VARCHAR(255) NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    category VARCHAR(100) NOT NULL,
    allow_employee_to_open BOOLEAN NOT NULL DEFAULT true,
    allow_subject_other_than_creator BOOLEAN NOT NULL DEFAULT true,
    is_active BOOLEAN NOT NULL DEFAULT true,
    version_number INTEGER NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Uniqueness is case-insensitive, matching CreateTicketTypeHandler's duplicate check.
-- A plain UNIQUE on code would accept "payroll" alongside "PAYROLL" while the handler
-- rejects it with 409, and two concurrent requests could both pass the pre-check.
CREATE UNIQUE INDEX idx_ticket_types_code_lower ON ticket_types (LOWER(code));

CREATE INDEX idx_ticket_types_active ON ticket_types(is_active);

CREATE TABLE ticket_type_versions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_type_id UUID NOT NULL REFERENCES ticket_types(id),
    version_number INTEGER NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(ticket_type_id, version_number)
);

CREATE INDEX idx_ticket_type_versions_type_id ON ticket_type_versions(ticket_type_id);

CREATE TABLE field_descriptors (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_type_version_id UUID NOT NULL REFERENCES ticket_type_versions(id),
    key VARCHAR(100) NOT NULL,
    label VARCHAR(255) NOT NULL,
    help_text TEXT NOT NULL DEFAULT '',
    data_type VARCHAR(50) NOT NULL,
    display_order INTEGER NOT NULL,
    group_name VARCHAR(100) NOT NULL DEFAULT '',
    is_required BOOLEAN NOT NULL DEFAULT true,
    is_visible_to_customer BOOLEAN NOT NULL DEFAULT true,
    choice_options TEXT NOT NULL DEFAULT '[]',
    min_length INTEGER,
    max_length INTEGER,
    min_value NUMERIC(18,4),
    max_value NUMERIC(18,4),
    earliest_date DATE,
    latest_date DATE,
    regex_pattern VARCHAR(500) NOT NULL DEFAULT '',
    allowed_file_types VARCHAR(500) NOT NULL DEFAULT '',
    max_file_size_bytes BIGINT,
    conditional_visibility_field_key VARCHAR(100) NOT NULL DEFAULT '',
    conditional_visibility_value VARCHAR(500) NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(ticket_type_version_id, key)
);

CREATE INDEX idx_field_descriptors_version_id ON field_descriptors(ticket_type_version_id);