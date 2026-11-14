-- Structural rules: one business, no overlapping tax rates, unique module switches,
-- emergency mode only on Windows terminals.
SET ROLE kite_app;
SET kite.all_branches = 'on';

SELECT kt.expect_error($$INSERT INTO core.business (id, legal_name_en, legal_name_ar) VALUES ('b0000000-0000-0000-0000-000000000002', 'Second', 'ثانية')$$, '23505');

SELECT kt.expect_error($$INSERT INTO tax.tax_rate (id, tax_code_id, rate_percent, effective_from)
  VALUES ('71000000-0000-0000-0000-000000000002', '70000000-0000-0000-0000-000000000001', 7.0, '2027-01-01')$$, '23P01');
-- Closing the current rate and starting a new one is the correct way to change a rate.
SET ROLE kite_migrator;  -- rate edits are an owner-approved operation in this test
UPDATE tax.tax_rate SET effective_to = '2027-01-01' WHERE id = '71000000-0000-0000-0000-000000000001';
SET ROLE kite_app;
INSERT INTO tax.tax_rate (id, tax_code_id, rate_percent, effective_from)
VALUES ('71000000-0000-0000-0000-000000000003', '70000000-0000-0000-0000-000000000001', 7.0, '2027-01-01');
SELECT kt.assert(tax.rate_on('70000000-0000-0000-0000-000000000001', '2026-06-30') = 5.0, 'rate 5% in 2026');
SELECT kt.assert(tax.rate_on('70000000-0000-0000-0000-000000000001', '2027-01-01') = 7.0, 'rate 7% from 2027 (test data only)');

INSERT INTO core.module_setting (id, business_id, branch_id, module_code, enabled)
VALUES ('80000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', NULL, 'repair', true);
SELECT kt.expect_error($$INSERT INTO core.module_setting (id, business_id, branch_id, module_code, enabled)
  VALUES ('80000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', NULL, 'repair', false)$$, '23505');

SELECT kt.expect_error($$INSERT INTO core.terminal (id, business_id, branch_id, code, name, kind, emergency_enabled)
  VALUES ('d0000000-0000-0000-0000-0000000000c1', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'W9', 'Web', 'browser', true)$$, '23514');

-- Updating a master row bumps its version (optimistic concurrency).
UPDATE core.module_setting SET enabled = false WHERE id = '80000000-0000-0000-0000-000000000001';
SELECT kt.assert((SELECT version = 2 FROM core.module_setting WHERE id = '80000000-0000-0000-0000-000000000001'), 'version bumped to 2');
RESET ROLE;
