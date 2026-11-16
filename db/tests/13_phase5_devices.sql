-- Phase 5 (V0014): IMEI check digits, one identity per IMEI, the unit lifecycle, specific cost in the stock ledger,
-- cost added only through the ledger, the unit and the ledger agreeing at commit, returns of the same unit,
-- inspections and refurbishment records, append-only history and branch isolation.
\set ON_ERROR_STOP 1
SET client_min_messages = warning;

-- Like kt.expect_error, for rules checked at commit: the deferred checks are run before the block ends.
CREATE FUNCTION kt.expect_commit_error(p_sql text, p_state text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  BEGIN
    EXECUTE p_sql;
    SET CONSTRAINTS ALL IMMEDIATE;
  EXCEPTION WHEN OTHERS THEN
    SET CONSTRAINTS ALL DEFERRED;
    IF SQLSTATE = p_state THEN RETURN; END IF;
    RAISE EXCEPTION 'expected SQLSTATE % but got % (%) for: %', p_state, SQLSTATE, SQLERRM, p_sql USING ERRCODE = 'KTF01';
  END;
  RAISE EXCEPTION 'expected SQLSTATE % but the statement succeeded: %', p_state, p_sql USING ERRCODE = 'KTF01';
END $$;

-- ---------------------------------------------------------------- IMEI check digit (11.2)
SELECT kt.assert(device.imei_valid('352099001761481'), 'a valid IMEI');
SELECT kt.assert(NOT device.imei_valid('352099001761482'), 'wrong check digit');
SELECT kt.assert(NOT device.imei_valid('35209900176148'), '14 digits refused');
SELECT kt.assert(NOT device.imei_valid('3520990017614810'), '16 digits refused');
SELECT kt.assert(NOT device.imei_valid('35209900176148A'), 'digits only');

-- ---------------------------------------------------------------- fixture: a used-phone category, a grade, a spare part
INSERT INTO catalog.category (id, business_id, name_en, name_ar) VALUES
  ('a7000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'Phones', 'هواتف'),
  ('a7000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'Tablets', 'أجهزة لوحية');
UPDATE catalog.product SET category_id = 'a7000000-0000-0000-0000-000000000001' WHERE id = 'a2000000-0000-0000-0000-000000000002';
INSERT INTO device.grade (id, business_id, category_id, code, name_en, name_ar, price_percent) VALUES
  ('a8000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', NULL, 'A', 'Like new', 'كالجديد', 85),
  ('a8000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'a7000000-0000-0000-0000-000000000002', 'T1', 'Tablet grade', 'درجة الأجهزة اللوحية', 60);

CREATE FUNCTION kt.unit(p_id uuid, p_status text, p_cost numeric, p_condition text DEFAULT 'brand_new') RETURNS void LANGUAGE sql AS $$
  INSERT INTO device.device_unit (id, business_id, branch_id, warehouse_id, variant_id, status, condition, acquisition_cost, source, source_doc_type, source_doc_id)
  VALUES (p_id, 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
          'a3000000-0000-0000-0000-000000000002', p_status, p_condition, p_cost, 'opening', 'test', gen_random_uuid());
$$;
CREATE FUNCTION kt.umove(p_unit uuid, p_qty numeric, p_type text, p_value numeric DEFAULT NULL) RETURNS inv.stock_movement LANGUAGE sql AS $$
  INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, value, device_unit_id, source_doc_type, source_doc_id)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
          'a3000000-0000-0000-0000-000000000002', p_type, p_qty, p_value, p_unit, 'test', gen_random_uuid())
  RETURNING *
$$;
CREATE FUNCTION kt.ev(p_unit uuid, p_event text, p_to text, p_condition text DEFAULT NULL, p_grade uuid DEFAULT NULL, p_doc uuid DEFAULT NULL,
                      p_cost numeric DEFAULT 0) RETURNS uuid LANGUAGE sql AS $$
  SELECT device.apply_event(gen_random_uuid(), p_unit, p_event, p_to, p_condition, p_grade, NULL, NULL, 'test', p_doc, NULL, NULL, p_cost)
$$;

SET kite.all_branches = 'on';   -- the superuser acts for every branch; the application role is tested at the end

-- ---------------------------------------------------------------- receiving: the unit and its first movement together
BEGIN;
SELECT kt.unit('a9000000-0000-0000-0000-000000000001', 'in_stock', 210.000);
SELECT kt.assert((kt.umove('a9000000-0000-0000-0000-000000000001', 1, 'opening')).value = 210.000, 'a unit comes in at its acquisition cost');
INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000001', 'imei', 1, '352099001761481', 'a2000000-0000-0000-0000-000000000002'),
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000001', 'imei', 2, '354670112345675', 'a2000000-0000-0000-0000-000000000002');
COMMIT;
SELECT kt.assert((SELECT accumulated_cost = 210.000 FROM device.device_unit WHERE id = 'a9000000-0000-0000-0000-000000000001'), 'cost = acquisition');
SELECT kt.assert((SELECT count(*) = 1 AND min(event_type) = 'received' AND min(seq) = 1 FROM device.device_event
                   WHERE device_unit_id = 'a9000000-0000-0000-0000-000000000001'), 'the first event is written by the database');
SELECT kt.assert((SELECT qty = 1 AND value = 210.000 FROM inv.stock_balance WHERE variant_id = 'a3000000-0000-0000-0000-000000000002'), 'balance holds one unit at its cost');

-- A unit without its movement (or the other way round) does not commit.
SELECT kt.expect_commit_error($$SELECT kt.unit('a9000000-0000-0000-0000-0000000000ff', 'in_stock', 10)$$, 'KT021');
SELECT kt.expect_commit_error($$SELECT kt.umove('a9000000-0000-0000-0000-000000000001', 1, 'adjustment')$$, 'KT021');   -- two in the ledger
SELECT kt.expect_error($$SELECT kt.unit(gen_random_uuid(), 'sold', 10)$$, 'KT020');             -- a new unit is in stock or under inspection
SELECT kt.expect_error($$SELECT kt.unit(gen_random_uuid(), 'in_stock', 10, 'for_parts')$$, 'KT020');
-- Serialized items move as units, one at a time; non-serialized items carry no unit.
SELECT kt.expect_error($$SELECT kt.move(1, 100, 'opening', 'a3000000-0000-0000-0000-000000000002')$$, 'KT010');
SELECT kt.expect_error($$SELECT kt.umove('a9000000-0000-0000-0000-000000000001', 2, 'adjustment')$$, 'KT010');
SELECT kt.expect_error($$INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, unit_cost, device_unit_id, source_doc_type, source_doc_id)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
          'a3000000-0000-0000-0000-000000000001', 'adjustment', 1, 1, 'a9000000-0000-0000-0000-000000000001', 'test', gen_random_uuid())$$, 'KT010');

-- ---------------------------------------------------------------- identifiers: one unit per IMEI for ever
SELECT kt.expect_error($$INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000001', 'imei', 1, '352099001761482', gen_random_uuid())$$, '23514');
BEGIN;
SELECT kt.unit('a9000000-0000-0000-0000-000000000002', 'under_inspection', 95.000, 'used');
SELECT kt.umove('a9000000-0000-0000-0000-000000000002', 1, 'opening');
COMMIT;
SELECT kt.expect_error($$INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000002', 'imei', 1, '352099001761481', gen_random_uuid())$$, '23505');
SELECT kt.expect_error($$INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000002', 'serial', 1, 'f2lx 9', gen_random_uuid())$$, '23514');
INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000002', 'imei', 1, '354670112345683', 'a2000000-0000-0000-0000-000000000002'),
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000002', 'serial', 1, 'F2LX9KQ1', 'a2000000-0000-0000-0000-000000000002');
SELECT kt.expect_error($$UPDATE device.imei_registry SET value = '354670112345691' WHERE value = '354670112345683'$$, 'KT001');

-- ---------------------------------------------------------------- the lifecycle (11.3)
SELECT kt.expect_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'sold', 'sold', NULL, NULL, gen_random_uuid())$$, 'KT020');   -- not sellable under inspection
SELECT kt.expect_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'inspected', 'in_stock', 'brand_new')$$, 'KT020');           -- used never becomes new
SELECT kt.expect_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'inspected', 'for_parts', 'used')$$, 'KT020');               -- for parts means condition for parts
SELECT kt.expect_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'inspected', 'refurbishing', 'used', 'a8000000-0000-0000-0000-000000000002')$$, 'KT020');   -- tablet grade on a phone
SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'inspected', 'refurbishing', 'used');
-- Cost goes in only through the ledger, while refurbishing.
SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'cost_added', NULL, NULL, NULL, NULL, 12.500);
SELECT kt.assert((kt.umove('a9000000-0000-0000-0000-000000000002', 0, 'unit_cost', 12.500)).qty_after = 2, 'a cost movement changes value, not quantity');
SELECT kt.assert((SELECT accumulated_cost = 107.500 FROM device.device_unit WHERE id = 'a9000000-0000-0000-0000-000000000002'), 'cost added to the unit by the ledger');
SELECT kt.expect_error($$SELECT kt.umove('a9000000-0000-0000-0000-000000000002', 0, 'unit_cost', NULL)$$, 'KT010');
SELECT kt.expect_error($$SELECT kt.umove('a9000000-0000-0000-0000-000000000002', 0, 'unit_cost', -500)$$, 'KT010');   -- never below zero
SELECT kt.expect_error($$SELECT kt.umove('a9000000-0000-0000-0000-000000000002', 1, 'unit_cost', 5)$$, '23514');      -- a cost movement has quantity 0
SELECT kt.expect_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'refurb_completed', 'in_stock', 'used')$$, 'KT020');   -- comes back refurbished or damaged
SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'refurb_completed', 'in_stock', 'refurbished', 'a8000000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'cost_added', NULL, NULL, NULL, NULL, 1)$$, 'KT020');   -- no cost outside refurbishment
SELECT kt.assert((SELECT status = 'in_stock' AND condition = 'refurbished' AND grade_id = 'a8000000-0000-0000-0000-000000000001'
                   FROM device.device_unit WHERE id = 'a9000000-0000-0000-0000-000000000002'), 'refurbished, grade A, back in stock');
SELECT kt.assert(NOT EXISTS (SELECT 1 FROM device.check_units()), 'cost = acquisition + history; ledger agrees');

-- ---------------------------------------------------------------- selling, voiding and returning the same unit
CREATE FUNCTION kt.sell_unit(p_sale uuid, p_line uuid, p_unit uuid, p_doc text, p_returned uuid, p_number text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE c numeric;
BEGIN
  INSERT INTO sales.sale (id, business_id, branch_id, warehouse_id, terminal_id, shift_id, cashier_id, number, doc_type, sale_date, completed_at,
                          prices_include_tax, net_total, discount_total, tax_total, gross_total, einvoice_uuid, qr_payload, original_sale_id, credit_reason)
  VALUES (p_sale, 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
          'd0000000-0000-0000-0000-00000000000a', 'a6000000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001', p_number, p_doc, current_date, now(), true,
          200, 0, 10, 210, gen_random_uuid(), 'qr', CASE WHEN p_doc = 'credit_note' THEN (SELECT sale_id FROM sales.sale_line WHERE id = p_returned) END,
          CASE WHEN p_doc = 'credit_note' THEN 'return' END);
  c := abs((kt.umove(p_unit, CASE WHEN p_doc = 'credit_note' THEN 1 ELSE -1 END, CASE WHEN p_doc = 'credit_note' THEN 'return' ELSE 'sale' END)).value);
  INSERT INTO sales.sale_line (id, sale_id, line_no, variant_id, product_type, description_en, description_ar, qty, unit_price, list_price, net_amount,
                               tax_code_id, tax_rate, tax_amount, gross_amount, cost_value, device_unit_id, returned_line_id)
  VALUES (p_line, p_sale, 1, 'a3000000-0000-0000-0000-000000000002', 'device', 'iPhone 15', 'آيفون 15', 1, 210, 210, 200,
          '70000000-0000-0000-0000-000000000001', 5, 10, 210, c, p_unit, p_returned);
END $$;

BEGIN;
SELECT kt.sell_unit('aa000000-0000-0000-0000-000000000001', 'ab100000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000002',
                    'simplified_invoice', NULL, 'INV-B01-2026-009001');
SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'sold', 'sold', NULL, NULL, 'aa000000-0000-0000-0000-000000000001');
COMMIT;
SELECT kt.assert((SELECT cost_value = 107.500 AND profit = 92.500 FROM sales.sale_line WHERE id = 'ab100000-0000-0000-0000-000000000001'),
                 'cost of sale = the unit''s own cost; profit = net - cost, stored on the line');
SELECT kt.assert((SELECT status = 'sold' AND sold_sale_id = 'aa000000-0000-0000-0000-000000000001' FROM device.device_unit WHERE id = 'a9000000-0000-0000-0000-000000000002'), 'sold');
-- Selling it again fails, in the ledger and in the lifecycle.
SELECT kt.expect_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'sold', 'sold', NULL, NULL, gen_random_uuid())$$, 'KT020');
-- A return must name the unit that was sold on that line.
SELECT kt.expect_error($$SELECT kt.sell_unit('aa000000-0000-0000-0000-000000000002', 'ab100000-0000-0000-0000-000000000002', 'a9000000-0000-0000-0000-000000000001',
                    'credit_note', 'ab100000-0000-0000-0000-000000000001', 'CN-B01-2026-009001')$$, 'KT011');
BEGIN;
SELECT kt.sell_unit('aa000000-0000-0000-0000-000000000003', 'ab100000-0000-0000-0000-000000000003', 'a9000000-0000-0000-0000-000000000002',
                    'credit_note', 'ab100000-0000-0000-0000-000000000001', 'CN-B01-2026-009002');
SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'returned', 'returned_inspection', NULL, NULL, 'aa000000-0000-0000-0000-000000000003');
COMMIT;
SELECT kt.assert((SELECT status = 'returned_inspection' AND accumulated_cost = 107.500 FROM device.device_unit WHERE id = 'a9000000-0000-0000-0000-000000000002'),
                 'a returned unit waits for inspection at the same cost');
-- Writing the status without the movement does not commit.
SELECT kt.expect_commit_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'inspected', 'in_stock', 'refurbished') UNION ALL
                         SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'sold', 'sold', NULL, NULL, gen_random_uuid())$$, 'KT021');
SELECT kt.assert((SELECT status = 'returned_inspection' FROM device.device_unit WHERE id = 'a9000000-0000-0000-0000-000000000002'), 'nothing of it stayed');

-- ---------------------------------------------------------------- hold and release; write-off
SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'held', 'quarantined');
SELECT kt.assert((SELECT status = 'quarantined' AND held_from_status = 'returned_inspection' FROM device.device_unit WHERE id = 'a9000000-0000-0000-0000-000000000002'), 'held');
SELECT kt.expect_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'inspected', 'in_stock', 'used')$$, 'KT020');
SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'released', NULL);
SELECT kt.assert((SELECT status = 'returned_inspection' AND held_from_status IS NULL FROM device.device_unit WHERE id = 'a9000000-0000-0000-0000-000000000002'), 'released to where it was');
SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'inspected', 'for_parts', 'for_parts');
BEGIN;
SELECT kt.umove('a9000000-0000-0000-0000-000000000002', -1, 'adjustment');
SELECT kt.ev('a9000000-0000-0000-0000-000000000002', 'written_off', 'written_off');
COMMIT;
SELECT kt.assert((SELECT seq FROM device.device_event WHERE device_unit_id = 'a9000000-0000-0000-0000-000000000002' ORDER BY seq DESC LIMIT 1) = 10,
                 'every step is in the history, numbered');
SELECT kt.assert(NOT EXISTS (SELECT 1 FROM device.check_units()), 'every unit agrees with its history and the ledger');
SELECT kt.assert(NOT EXISTS (SELECT 1 FROM inv.check_balances()), 'every balance equals its ledger');

-- ---------------------------------------------------------------- inspections and refurbishment records
INSERT INTO device.inspection (id, business_id, branch_id, device_unit_id, number, reason, results, suggested_outcome, outcome, result_condition, event_id, inspected_by)
SELECT gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a9000000-0000-0000-0000-000000000002',
       'INS-B01-2026-000001', 'return', '[]', 'for_parts', 'for_parts', 'for_parts', e.id, 'a5000000-0000-0000-0000-000000000001'
  FROM device.device_event e WHERE e.device_unit_id = 'a9000000-0000-0000-0000-000000000002' AND e.event_type = 'inspected' ORDER BY seq DESC LIMIT 1;
SELECT kt.expect_error($$INSERT INTO device.inspection (id, business_id, branch_id, device_unit_id, number, reason, results, suggested_outcome, outcome, result_condition, event_id, inspected_by)
SELECT gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a9000000-0000-0000-0000-000000000002',
       'INS-B01-2026-000002', 'return', '[]', 'for_parts', 'restock', 'used', e.id, 'a5000000-0000-0000-0000-000000000001'
  FROM device.device_event e WHERE e.device_unit_id = 'a9000000-0000-0000-0000-000000000002' LIMIT 1$$, '23514');   -- overriding needs a note
SELECT kt.expect_error($$UPDATE device.inspection SET note = 'x'$$, 'KT001');
INSERT INTO refurb.refurb_order (id, business_id, branch_id, number, device_unit_id, opened_by) VALUES
  ('ad000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'RF-B01-2026-000001',
   'a9000000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$INSERT INTO refurb.refurb_order (id, business_id, branch_id, number, device_unit_id, opened_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'RF-B01-2026-000002',
   'a9000000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001')$$, '23505');   -- one open order per unit
SELECT kt.expect_error($$UPDATE refurb.refurb_order SET status = 'completed' WHERE id = 'ad000000-0000-0000-0000-000000000001'$$, '23514');
UPDATE refurb.refurb_order SET status = 'completed', completed_at = now(), completed_by = 'a5000000-0000-0000-0000-000000000001', result_condition = 'refurbished'
 WHERE id = 'ad000000-0000-0000-0000-000000000001';
SELECT kt.expect_error($$UPDATE refurb.refurb_order SET work_done = 'more' WHERE id = 'ad000000-0000-0000-0000-000000000001'$$, 'KT001');

-- ---------------------------------------------------------------- the application role
RESET kite.all_branches;
SET ROLE kite_app;
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000a}';
SELECT kt.expect_error($$UPDATE device.device_unit SET accumulated_cost = 1$$, '42501');           -- the cost is the ledger's
SELECT kt.expect_error($$UPDATE device.device_unit SET status = 'in_stock'$$, '42501');            -- status only through apply_event
SELECT kt.expect_error($$INSERT INTO device.device_event (id, business_id, branch_id, device_unit_id, seq, event_type, to_status, to_condition)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a9000000-0000-0000-0000-000000000001', 99, 'sold', 'sold', 'used')$$, '42501');
SELECT kt.expect_error($$DELETE FROM device.device_unit$$, '42501');
SELECT kt.assert((SELECT count(*) FROM device.device_unit) >= 2, 'reads its branch''s units');
SELECT kt.ev('a9000000-0000-0000-0000-000000000001', 'price_set', NULL);
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000b}';
SELECT kt.assert((SELECT count(*) FROM device.device_unit) = 0, 'another branch sees none of them');
SELECT kt.assert((SELECT count(*) FROM device.imei_registry) = 0, 'nor their IMEIs');
SELECT kt.expect_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000001', 'held', 'quarantined')$$, '42501');
RESET ROLE;
RESET kite.branch_ids;

SELECT 'phase 5 device tests passed' AS result;
