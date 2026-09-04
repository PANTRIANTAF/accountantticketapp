-- customers.status is one of exactly two values (Customers.Core.CustomerStatus), but the
-- CREATE TABLE in 20260830_001 left it as a bare VARCHAR(20) with a default. Every write path
-- goes through a handler that validates, so this constraint is not the primary defence -- it is
-- what makes a bug in a future handler, or a hand-run UPDATE during support, fail loudly instead
-- of leaving a row that no status filter ever matches and no reader can explain.
--
-- A separate script rather than an edit to 001: 001 is recorded in schema_versions on every
-- environment where it has already run, so editing it changes nothing there and silently produces
-- two databases with different schemas. Migrations are append-only.
ALTER TABLE customers
    ADD CONSTRAINT ck_customers_status CHECK (status IN ('Active', 'Suspended'));
