CREATE TABLE customers (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    legal_name VARCHAR(300) NOT NULL,
    trading_name VARCHAR(300) NULL,
    tax_number VARCHAR(50) NOT NULL,
    tax_office VARCHAR(200) NULL,
    address_line1 VARCHAR(200) NOT NULL,
    address_line2 VARCHAR(200) NULL,
    address_city VARCHAR(100) NOT NULL,
    address_postal_code VARCHAR(20) NOT NULL,
    address_country VARCHAR(100) NOT NULL,
    contact_email VARCHAR(320) NOT NULL,
    contact_phone VARCHAR(40) NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Active',
    onboarded_on DATE NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_customers_tax_number UNIQUE (tax_number)
);

CREATE INDEX idx_customers_legal_name ON customers (legal_name, id);
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE INDEX idx_customers_name_trgm ON customers USING gin (legal_name gin_trgm_ops);