-- Phase 9 (V0018): reservations never exceed the stock on hand and the balance keeps their total; parts requests hold
-- what is outstanding; a serial-tracked part is fitted and unfitted as a device unit; outside work goes into work in
-- progress and is reversed only in full; the invoice that stands for a job follows its corrections; commission rules keep
-- their versions, never overlap within a scope, and resolve to the most specific; an accrual is written once per job,
-- technician, kind and version, is never edited, is reversed only by its exact opposite, and is paid in exactly one
-- payout line; a payout's total is its lines.
\set ON_ERROR_STOP 1
SET client_min_messages = warning;
SET kite.all_branches = 'on';

-- ---------------------------------------------------------------- fixture: a battery, 3 on hand at 20.000
INSERT INTO catalog.product (id, business_id, name_en, name_ar, product_type, tracking, tax_code_id) VALUES
  ('a2900000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'Battery', 'بطارية', 'spare_part', 'none', '70000000-0000-0000-0000-000000000001'),
  ('a2900000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'OLED display', 'شاشة OLED', 'spare_part', 'serial', '70000000-0000-0000-0000-000000000001');
INSERT INTO catalog.variant (id, business_id, product_id, sku) VALUES
  ('a3900000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a2900000-0000-0000-0000-000000000001', 'SP-BAT'),
  ('a3900000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'a2900000-0000-0000-0000-000000000002', 'SP-OLED');
SELECT kt.move(3, 20.000, 'opening', 'a3900000-0000-0000-0000-000000000001');
CREATE TEMP TABLE v AS SELECT kt.rjob() AS j1, kt.rjob() AS j2, kt.rjob() AS j3;

CREATE FUNCTION kt.reserve(p_job uuid, p_qty numeric, p_variant uuid DEFAULT 'a3900000-0000-0000-0000-000000000001') RETURNS void
LANGUAGE sql AS $$
  INSERT INTO inv.reservation (id, business_id, branch_id, warehouse_id, variant_id, source_doc_type, source_doc_id, qty)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
          p_variant, 'repair_job', p_job, p_qty)
  ON CONFLICT (source_doc_type, source_doc_id, warehouse_id, variant_id) DO UPDATE SET qty = EXCLUDED.qty
$$;
CREATE FUNCTION kt.reserved() RETURNS numeric LANGUAGE sql AS $$
  SELECT reserved FROM inv.stock_balance WHERE warehouse_id = 'a1000000-0000-0000-0000-00000000000a' AND variant_id = 'a3900000-0000-0000-0000-000000000001'
$$;

-- ---------------------------------------------------------------- reservations (13.4)
SELECT kt.reserve((SELECT j1 FROM v), 2);
SELECT kt.reserve((SELECT j2 FROM v), 1);
SELECT kt.assert(kt.reserved() = 3, 'the balance holds the reserved total');
SELECT kt.assert(inv.available('a1000000-0000-0000-0000-00000000000a', 'a3900000-0000-0000-0000-000000000001') = 0, 'nothing left to reserve or sell');
SELECT kt.expect_error($$SELECT kt.reserve((SELECT j3 FROM v), 1)$$, 'KT050');                   -- more than is on hand
SELECT kt.reserve((SELECT j1 FROM v), 0);                                                         -- released
SELECT kt.assert(kt.reserved() = 1 AND inv.available('a1000000-0000-0000-0000-00000000000a', 'a3900000-0000-0000-0000-000000000001') = 2, 'a release frees stock');
SELECT kt.reserve((SELECT j3 FROM v), 2);
SELECT kt.assert(kt.reserved() = 3, 'a third job takes what was freed');
SELECT kt.expect_error($$DELETE FROM inv.reservation WHERE source_doc_id = (SELECT j3 FROM v)$$, 'KT050');
SELECT kt.expect_error($$UPDATE inv.reservation SET variant_id = 'a3800000-0000-0000-0000-000000000001' WHERE source_doc_id = (SELECT j3 FROM v)$$, 'KT050');
SELECT kt.expect_error($$SELECT kt.reserve((SELECT j3 FROM v), 1, 'a3800000-0000-0000-0000-000000000002')$$, 'KT050');   -- a service is not reserved
SELECT kt.expect_error($$SELECT kt.reserve((SELECT j3 FROM v), -1)$$, '23514');
-- Stock arriving does not change what is reserved; a sale can take the balance below reserved (the till checks first).
SELECT kt.move(2, 22.000, 'adjustment', 'a3900000-0000-0000-0000-000000000001');
SELECT kt.assert(kt.reserved() = 3 AND inv.available('a1000000-0000-0000-0000-00000000000a', 'a3900000-0000-0000-0000-000000000001') = 2, 'arrivals add to what is available');
SELECT kt.reserve((SELECT j2 FROM v), 0);
SELECT kt.reserve((SELECT j3 FROM v), 0);

-- ---------------------------------------------------------------- parts requests
INSERT INTO repair.part_request (id, business_id, branch_id, job_id, warehouse_id, variant_id, qty, requested_total, open_since)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j1 FROM v),
        'a1000000-0000-0000-0000-00000000000a', 'a3900000-0000-0000-0000-000000000001', 2, 2, now());
SELECT kt.expect_error($$UPDATE repair.part_request SET qty = 0 WHERE job_id = (SELECT j1 FROM v)$$, '23514');   -- filled: no longer open
UPDATE repair.part_request SET qty = 0, open_since = NULL, filled_total = 2 WHERE job_id = (SELECT j1 FROM v);
SELECT kt.expect_error($$INSERT INTO repair.part_request (id, business_id, branch_id, job_id, warehouse_id, variant_id, qty)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j1 FROM v),
          'a1000000-0000-0000-0000-00000000000a', 'a3900000-0000-0000-0000-000000000001', 0)$$, '23505');                -- one row per job and part
SELECT kt.expect_error($$DELETE FROM repair.part_request WHERE job_id = (SELECT j1 FROM v)$$, 'KT001');

-- ---------------------------------------------------------------- a serial-tracked part fitted and taken out again
BEGIN;
INSERT INTO device.device_unit (id, business_id, branch_id, warehouse_id, variant_id, status, condition, acquisition_cost, source, source_doc_type, source_doc_id)
VALUES ('a4900000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
        'a3900000-0000-0000-0000-000000000002', 'in_stock', 'brand_new', 35.000, 'opening', 'test', gen_random_uuid());
INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, value, device_unit_id, source_doc_type, source_doc_id)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
        'a3900000-0000-0000-0000-000000000002', 'opening', 1, 35.000, 'a4900000-0000-0000-0000-000000000001', 'test', gen_random_uuid());
COMMIT;
BEGIN;
INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, device_unit_id, source_doc_type, source_doc_id)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
        'a3900000-0000-0000-0000-000000000002', 'repair_issue', -1, 'a4900000-0000-0000-0000-000000000001', 'repair_job', (SELECT j1 FROM v));
SELECT device.apply_event(gen_random_uuid(), 'a4900000-0000-0000-0000-000000000001', 'fitted', 'fitted', NULL, NULL, NULL, NULL, 'repair_job', (SELECT j1 FROM v), NULL, NULL, 0);
COMMIT;
SELECT kt.assert((SELECT status FROM device.device_unit WHERE id = 'a4900000-0000-0000-0000-000000000001') = 'fitted', 'a fitted part is out of stock as a unit');
SELECT kt.expect_error($$SELECT device.apply_event(gen_random_uuid(), 'a4900000-0000-0000-0000-000000000001', 'sold', 'sold', NULL, NULL, NULL, NULL, 'sale', gen_random_uuid(), NULL, NULL, 0)$$, 'KT020');
BEGIN;
INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, device_unit_id, source_doc_type, source_doc_id)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
        'a3900000-0000-0000-0000-000000000002', 'repair_return', 1, 'a4900000-0000-0000-0000-000000000001', 'repair_job', (SELECT j1 FROM v));
SELECT device.apply_event(gen_random_uuid(), 'a4900000-0000-0000-0000-000000000001', 'unfitted', 'in_stock', NULL, NULL, NULL, NULL, 'repair_job', (SELECT j1 FROM v), NULL, NULL, 0);
COMMIT;
SELECT kt.assert((SELECT status FROM device.device_unit WHERE id = 'a4900000-0000-0000-0000-000000000001') = 'in_stock'
             AND (SELECT qty FROM inv.stock_balance WHERE variant_id = 'a3900000-0000-0000-0000-000000000002') = 1, 'taken out again, it is back in stock at its cost');
-- A fitted unit must leave the ledger in the same transaction.
SELECT kt.expect_commit_error($$SELECT device.apply_event(gen_random_uuid(), 'a4900000-0000-0000-0000-000000000001', 'fitted', 'fitted', NULL, NULL, NULL, NULL, 'repair_job', gen_random_uuid(), NULL, NULL, 0)$$, 'KT021');

-- ---------------------------------------------------------------- outside work in work in progress
CREATE TEMP TABLE w AS SELECT kt.rjob_at('in_progress') AS j;
INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, cost, vendor, reference, created_by)
VALUES ('a9900000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j FROM w),
        'external', 15.000, 'Board Doctor LLC', 'BD-1182', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.assert(repair.wip((SELECT j FROM w)) = 15.000 AND repair.outside_cost((SELECT j FROM w)) = 15.000, 'outside work is in work in progress');
SELECT kt.expect_error($$INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, cost, vendor, return_of_id, created_by)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j FROM w), 'external', -5.000, 'Board Doctor LLC',
          'a9900000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001')$$, 'KT041');                  -- reversed in full only
SELECT kt.expect_error($$INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, cost, created_by)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j FROM w), 'external', 5.000,
          'a5000000-0000-0000-0000-000000000001')$$, '23514');                                                          -- names who did it
INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, cost, vendor, return_of_id, created_by)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j FROM w), 'external', -15.000,
        'Board Doctor LLC', 'a9900000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, cost, vendor, return_of_id, created_by)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j FROM w), 'external', -15.000, 'Board Doctor LLC',
          'a9900000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001')$$, '23505');                  -- once
SELECT kt.assert(repair.wip((SELECT j FROM w)) = 0, 'a reversed outside job leaves nothing in progress');

-- ---------------------------------------------------------------- the invoice that stands for a job
SELECT kt.sale('a7900000-0000-0000-0000-000000000001', 'INV-B01-2026-090001');
SELECT kt.sale('a7900000-0000-0000-0000-000000000002', 'CN-B01-2026-090001', 'credit_note', 'a7900000-0000-0000-0000-000000000001', 'correction');
SELECT kt.sale('a7900000-0000-0000-0000-000000000003', 'INV-B01-2026-090002');
SELECT kt.sale('a7900000-0000-0000-0000-000000000004', 'CN-B01-2026-090002', 'credit_note', 'a7900000-0000-0000-0000-000000000003', 'correction');
CREATE TEMP TABLE d AS SELECT kt.rjob_at('ready') AS j;
UPDATE repair.repair_job SET delivery_sale_id = 'a7900000-0000-0000-0000-000000000001' WHERE id = (SELECT j FROM d);
SELECT kt.rmove((SELECT j FROM d), 'delivered');
SELECT kt.assert(repair.current_invoice((SELECT j FROM d)) = 'a7900000-0000-0000-0000-000000000001', 'the delivery invoice stands');
INSERT INTO repair.invoice_correction (id, business_id, branch_id, job_id, credited_sale_id, credit_note_id, corrected_sale_id, reason, created_by)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j FROM d), 'a7900000-0000-0000-0000-000000000001',
        'a7900000-0000-0000-0000-000000000002', 'a7900000-0000-0000-0000-000000000003', 'Screen price was wrong', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.assert(repair.current_invoice((SELECT j FROM d)) = 'a7900000-0000-0000-0000-000000000003', 'the corrected invoice stands');
SELECT kt.expect_error($$INSERT INTO repair.invoice_correction (id, business_id, branch_id, job_id, credited_sale_id, credit_note_id, reason, created_by)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j FROM d), 'a7900000-0000-0000-0000-000000000001',
          'a7900000-0000-0000-0000-000000000004', 'Again', 'a5000000-0000-0000-0000-000000000001')$$, '23505');       -- an invoice is credited once
INSERT INTO repair.invoice_correction (id, business_id, branch_id, job_id, credited_sale_id, credit_note_id, reason, created_by)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j FROM d), 'a7900000-0000-0000-0000-000000000003',
        'a7900000-0000-0000-0000-000000000004', 'Customer refunded', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.assert(repair.current_invoice((SELECT j FROM d)) IS NULL, 'credited and not re-issued: no invoice stands');

-- ---------------------------------------------------------------- commission rules (15.1)
INSERT INTO iam."user" (id, business_id, username, display_name_en, password_hash)
VALUES ('a5900000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'tech1', 'Tariq', 'x'),
       ('a5900000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'tech2', 'Nasser', 'x');
CREATE FUNCTION kt.rule(p_key uuid, p_version int, p_basis text, p_value numeric, p_tech uuid, p_branch uuid, p_cat uuid, p_from date, p_to date DEFAULT NULL)
RETURNS uuid LANGUAGE sql AS $$
  INSERT INTO comm.rule (id, business_id, rule_key, version, name_en, name_ar, basis, amount, rate, technician_id, branch_id, category_id, effective_from, effective_to)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', p_key, p_version, 'Rule', 'قاعدة', p_basis,
          CASE WHEN p_basis = 'fixed' THEN p_value END, CASE WHEN p_basis <> 'fixed' THEN p_value END, p_tech, p_branch, p_cat, p_from, p_to)
  RETURNING id
$$;
CREATE TEMP TABLE r AS SELECT
  kt.rule('c0900000-0000-0000-0000-000000000001', 1, 'labour', 10, NULL, NULL, NULL, '2026-01-01') AS def,
  kt.rule('c0900000-0000-0000-0000-000000000002', 1, 'fixed', 3, 'a5900000-0000-0000-0000-000000000001', NULL, NULL, '2026-01-01') AS tech,
  kt.rule('c0900000-0000-0000-0000-000000000003', 1, 'profit', 15, 'a5900000-0000-0000-0000-000000000001', NULL, 'ab800000-0000-0000-0000-000000000001', '2026-01-01') AS techcat,
  kt.rule('c0900000-0000-0000-0000-000000000004', 1, 'labour', 12, NULL, 'a0000000-0000-0000-0000-00000000000a', NULL, '2026-01-01') AS branch;
SELECT kt.assert(comm.resolve_rule('b0000000-0000-0000-0000-000000000001', 'a5900000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
                                   'ab800000-0000-0000-0000-000000000001', '2026-03-01') = (SELECT techcat FROM r), 'technician + category wins');
SELECT kt.assert(comm.resolve_rule('b0000000-0000-0000-0000-000000000001', 'a5900000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
                                   NULL, '2026-03-01') = (SELECT tech FROM r), 'then the technician');
SELECT kt.assert(comm.resolve_rule('b0000000-0000-0000-0000-000000000001', 'a5900000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-00000000000a',
                                   'ab800000-0000-0000-0000-000000000001', '2026-03-01') = (SELECT branch FROM r), 'then the branch');
SELECT kt.assert(comm.resolve_rule('b0000000-0000-0000-0000-000000000001', 'a5900000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-00000000000b',
                                   NULL, '2026-03-01') = (SELECT def FROM r), 'then the business default');
SELECT kt.assert(comm.resolve_rule('b0000000-0000-0000-0000-000000000001', 'a5900000-0000-0000-0000-000000000002', NULL, NULL, '2025-12-31') IS NULL, 'nothing before the rules begin');
SELECT kt.expect_error($$SELECT kt.rule(gen_random_uuid(), 1, 'labour', 8, NULL, NULL, NULL, '2026-06-01')$$, 'KT060');           -- same scope, overlapping
SELECT kt.expect_error($$UPDATE comm.rule SET rate = 11 WHERE id = (SELECT def FROM r)$$, 'KT060');                                -- versions are kept as written
SELECT kt.expect_error($$DELETE FROM comm.rule WHERE id = (SELECT def FROM r)$$, 'KT060');
SELECT kt.expect_error($$SELECT kt.rule(gen_random_uuid(), 1, 'fixed', 2, 'a5900000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-00000000000a', NULL, '2026-01-01')$$, '23514');
SELECT kt.expect_error($$SELECT kt.rule(gen_random_uuid(), 1, 'fixed', NULL, NULL, NULL, 'ab800000-0000-0000-0000-000000000001', '2026-01-01')$$, '23514');
-- A new version: the old one ends the day before.
UPDATE comm.rule SET effective_to = '2026-05-31' WHERE id = (SELECT def FROM r);
CREATE TEMP TABLE r2 AS SELECT kt.rule('c0900000-0000-0000-0000-000000000001', 2, 'labour', 11, NULL, NULL, NULL, '2026-06-01') AS def2;
SELECT kt.assert(comm.resolve_rule('b0000000-0000-0000-0000-000000000001', 'a5900000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-00000000000b', NULL, '2026-05-31') = (SELECT def FROM r)
             AND comm.resolve_rule('b0000000-0000-0000-0000-000000000001', 'a5900000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-00000000000b', NULL, '2026-06-01') = (SELECT def2 FROM r2),
             'each version on its own dates');
SELECT kt.expect_error($$INSERT INTO comm.rule (id, business_id, rule_key, version, name_en, name_ar, basis, rate, effective_from, status)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'c0900000-0000-0000-0000-000000000001', 2, 'Rule', 'قاعدة', 'labour', 9, '2027-01-01', 'inactive')$$, '23505');   -- one row per version

-- ---------------------------------------------------------------- accruals (15.2-15.4)
CREATE FUNCTION kt.accrue(p_kind text, p_amount numeric, p_version int DEFAULT 1, p_tech uuid DEFAULT 'a5900000-0000-0000-0000-000000000001',
                          p_reverses uuid DEFAULT NULL, p_status text DEFAULT 'accrued') RETURNS uuid
LANGUAGE sql AS $$
  INSERT INTO comm.accrual (id, business_id, branch_id, job_id, technician_id, kind, kind_version, amount, share, rule_id, basis, trace, sale_id, claim_job_id,
                            reverses_id, accrual_date, status)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j FROM d), p_tech, p_kind, p_version, p_amount,
          CASE WHEN p_kind = 'repair' THEN 100 END, CASE WHEN p_kind = 'repair' THEN (SELECT techcat FROM r) END, CASE WHEN p_kind = 'repair' THEN 'profit' END,
          '{}', CASE WHEN p_kind = 'repair' THEN 'a7900000-0000-0000-0000-000000000003'::uuid END, CASE WHEN p_kind = 'clawback' THEN (SELECT j FROM w) END,
          p_reverses, '2026-03-01', p_status)
  RETURNING id
$$;
CREATE TEMP TABLE a AS SELECT kt.accrue('repair', 4.800) AS a1;
SELECT kt.expect_error($$SELECT kt.accrue('repair', 4.800)$$, '23505');                         -- the generator ran twice: one accrual only
SELECT kt.expect_error($$SELECT kt.accrue('repair', -1)$$, '23514');                            -- a repair commission is not negative
SELECT kt.expect_error($$SELECT kt.accrue('clawback', 1)$$, '23514');                           -- a clawback is
SELECT kt.expect_error($$UPDATE comm.accrual SET amount = 5 WHERE id = (SELECT a1 FROM a)$$, 'KT061');
SELECT kt.expect_error($$UPDATE comm.accrual SET status = 'paid', payout_id = gen_random_uuid() WHERE id = (SELECT a1 FROM a)$$, 'KT061');   -- approved first
SELECT kt.expect_error($$DELETE FROM comm.accrual WHERE id = (SELECT a1 FROM a)$$, 'KT061');
SELECT kt.expect_error($$SELECT kt.accrue('reversal', -4.000, 1, 'a5900000-0000-0000-0000-000000000001', (SELECT a1 FROM a))$$, 'KT061');   -- not the exact opposite
SELECT kt.expect_error($$SELECT kt.accrue('reversal', -4.800, 1, 'a5900000-0000-0000-0000-000000000002', (SELECT a1 FROM a))$$, 'KT061');   -- another technician
-- Reversed before payout: the pair closes.
CREATE TEMP TABLE ra AS SELECT kt.accrue('reversal', -4.800, 1, 'a5900000-0000-0000-0000-000000000001', (SELECT a1 FROM a), 'reversed') AS r1;
UPDATE comm.accrual SET status = 'reversed', reversed_by = 'a5000000-0000-0000-0000-000000000001', reversed_at = now(), reason = 'Invoice corrected' WHERE id = (SELECT a1 FROM a);
SELECT kt.expect_error($$SELECT kt.accrue('reversal', -4.800, 1, 'a5900000-0000-0000-0000-000000000001', (SELECT a1 FROM a))$$, 'KT061');   -- reversed once
SELECT kt.expect_error($$UPDATE comm.accrual SET status = 'approved', approved_by = 'a5000000-0000-0000-0000-000000000001' WHERE id = (SELECT a1 FROM a)$$, 'KT061');
-- The corrected invoice's accrual is the next version.
INSERT INTO a SELECT kt.accrue('repair', 4.200, 2);

-- ---------------------------------------------------------------- payouts
CREATE FUNCTION kt.payout(p_id uuid, p_number text, p_total numeric) RETURNS void LANGUAGE sql AS $$
  INSERT INTO comm.payout (id, business_id, branch_id, number, technician_id, method, total, payout_date, created_by)
  VALUES (p_id, 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', p_number, 'a5900000-0000-0000-0000-000000000001', 'cash', p_total,
          '2026-03-31', 'a5000000-0000-0000-0000-000000000001')
$$;
CREATE TEMP TABLE a2 AS SELECT id AS v2 FROM comm.accrual WHERE kind = 'repair' AND kind_version = 2;
SELECT kt.expect_error($$SELECT kt.payout('a6900000-0000-0000-0000-000000000001', 'COM-T-1', 4.200);
  INSERT INTO comm.payout_line (id, payout_id, accrual_id, amount) VALUES (gen_random_uuid(), 'a6900000-0000-0000-0000-000000000001', (SELECT v2 FROM a2), 4.200)$$, 'KT061');   -- approved first
UPDATE comm.accrual SET status = 'approved', approved_by = 'a5000000-0000-0000-0000-000000000001', approved_at = now() WHERE id = (SELECT v2 FROM a2);
SELECT kt.expect_commit_error($$SELECT kt.payout('a6900000-0000-0000-0000-000000000002', 'COM-T-2', 5.000);
  INSERT INTO comm.payout_line (id, payout_id, accrual_id, amount) VALUES (gen_random_uuid(), 'a6900000-0000-0000-0000-000000000002', (SELECT v2 FROM a2), 4.200)$$, 'KT061');   -- total is its lines
SELECT kt.expect_error($$SELECT kt.payout('a6900000-0000-0000-0000-000000000003', 'COM-T-3', 4.200);
  INSERT INTO comm.payout_line (id, payout_id, accrual_id, amount) VALUES (gen_random_uuid(), 'a6900000-0000-0000-0000-000000000003', (SELECT v2 FROM a2), 4.000)$$, 'KT061');   -- at its amount
BEGIN;
SELECT kt.payout('a6900000-0000-0000-0000-000000000004', 'COM-T-4', 4.200);
INSERT INTO comm.payout_line (id, payout_id, accrual_id, amount) VALUES (gen_random_uuid(), 'a6900000-0000-0000-0000-000000000004', (SELECT v2 FROM a2), 4.200);
COMMIT;
SELECT kt.assert((SELECT status FROM comm.accrual WHERE id = (SELECT v2 FROM a2)) = 'paid', 'paid in its payout');
SELECT kt.expect_error($$SELECT kt.payout('a6900000-0000-0000-0000-000000000005', 'COM-T-5', 4.200);
  INSERT INTO comm.payout_line (id, payout_id, accrual_id, amount) VALUES (gen_random_uuid(), 'a6900000-0000-0000-0000-000000000005', (SELECT v2 FROM a2), 4.200)$$, 'KT061');   -- paid already: one payout line only
SELECT kt.expect_error($$UPDATE comm.payout SET total = 1 WHERE number = 'COM-T-4'$$, 'KT001');
-- A paid commission is reversed by an opposite accrual that is itself approved and paid (a deduction from the next payout).
SELECT kt.accrue('reversal', -4.200, 2, 'a5900000-0000-0000-0000-000000000001', (SELECT v2 FROM a2));
SELECT kt.assert((SELECT status FROM comm.accrual WHERE id = (SELECT v2 FROM a2)) = 'paid'
             AND (SELECT status FROM comm.accrual WHERE reverses_id = (SELECT v2 FROM a2)) = 'accrued', 'a paid commission stays paid; its reversal waits for the next payout');
SELECT kt.assert(comm.payable_by_documents('b0000000-0000-0000-0000-000000000001') = -4.200, 'payable by documents: accruals less payouts');

-- ---------------------------------------------------------------- the application role
SET ROLE kite_app;
SET kite.all_branches = 'on';
SELECT kt.expect_error($$DELETE FROM comm.payout_line$$, '42501');
SELECT kt.expect_error($$UPDATE repair.invoice_correction SET reason = 'x'$$, '42501');
SELECT kt.expect_error($$DELETE FROM inv.reservation$$, '42501');
RESET ROLE;
