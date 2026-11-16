-- Phase 4: stock ledger and weighted average, serialized and service refusals, returns limited to what was sold,
-- sale and payment rules, prices without overlaps, shifts, card settlements, approvals and branch isolation.
\set ON_ERROR_STOP 1
SET client_min_messages = warning;

-- ---------------------------------------------------------------- fixture
INSERT INTO core.warehouse (id, business_id, branch_id, code, name_en, name_ar, kind) VALUES
  ('a1000000-0000-0000-0000-00000000000a', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'SHOP', 'Shop', 'المحل', 'shop'),
  ('a1000000-0000-0000-0000-00000000000b', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', 'SHOP', 'Shop', 'المحل', 'shop');
INSERT INTO catalog.product (id, business_id, name_en, name_ar, product_type, tracking, tax_code_id) VALUES
  ('a2000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'USB-C cable', 'كابل', 'accessory', 'none', '70000000-0000-0000-0000-000000000001'),
  ('a2000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'iPhone 15', 'آيفون 15', 'device', 'imei', '70000000-0000-0000-0000-000000000001'),
  ('a2000000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000001', 'Screen fitting', 'تركيب شاشة', 'service', 'none', '70000000-0000-0000-0000-000000000001');
INSERT INTO catalog.variant (id, business_id, product_id, sku) VALUES
  ('a3000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a2000000-0000-0000-0000-000000000001', 'CBL-C-1M'),
  ('a3000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'a2000000-0000-0000-0000-000000000002', 'IP15-128-BLK'),
  ('a3000000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000001', 'a2000000-0000-0000-0000-000000000003', 'SVC-SCREEN');

SELECT kt.assert(NOT EXISTS (SELECT 1 FROM inv.check_balances()), 'no stock yet: nothing to reconcile');

CREATE FUNCTION kt.move(p_qty numeric, p_cost numeric, p_type text DEFAULT 'adjustment', p_variant uuid DEFAULT 'a3000000-0000-0000-0000-000000000001',
                        p_branch uuid DEFAULT 'a0000000-0000-0000-0000-00000000000a', p_wh uuid DEFAULT 'a1000000-0000-0000-0000-00000000000a')
RETURNS inv.stock_movement LANGUAGE sql AS $$
  INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, unit_cost, source_doc_type, source_doc_id)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', p_branch, p_wh, p_variant, p_type, p_qty, p_cost, 'test', gen_random_uuid())
  RETURNING *
$$;

-- ---------------------------------------------------------------- weighted average (16.3)
SELECT kt.assert((kt.move(10, 1.200, 'opening')).value = 12.000, 'receipt value = qty x cost');
SELECT kt.assert((kt.move(5, 1.500)).value = 7.500, 'second receipt');
SELECT kt.assert((SELECT qty = 15 AND value = 19.500 AND avg_cost = 1.300000 FROM inv.stock_balance
                   WHERE variant_id = 'a3000000-0000-0000-0000-000000000001'), 'average cost 19.5 / 15 = 1.300');
SELECT kt.assert((kt.move(-4, NULL, 'sale')).value = -5.200, 'a sale takes its share of the valuation');
SELECT kt.assert((kt.move(-3, NULL, 'sale')).unit_cost = 1.300000, 'outgoing unit cost is the average');
-- Eight left worth 10.400: sold in uneven pieces, rounding never leaves value behind once qty is zero.
SELECT count(*) FROM (SELECT kt.move(-1, NULL, 'sale') UNION ALL SELECT kt.move(-1, NULL, 'sale') UNION ALL SELECT kt.move(-6, NULL, 'sale')) x;
SELECT kt.assert((SELECT qty = 0 AND value = 0 FROM inv.stock_balance WHERE variant_id = 'a3000000-0000-0000-0000-000000000001'),
                 'quantity and value return to exactly zero together');
-- Uneven costs: 3 at 0.333 then sell one at a time.
SELECT (kt.move(3, 0.333)).value;
SELECT (kt.move(-1, NULL, 'sale')).value;
SELECT (kt.move(-1, NULL, 'sale')).value;
SELECT (kt.move(-1, NULL, 'sale')).value;
SELECT kt.assert((SELECT qty = 0 AND value = 0 FROM inv.stock_balance WHERE variant_id = 'a3000000-0000-0000-0000-000000000001'),
                 'odd thirds still close to zero value');
SELECT kt.assert((SELECT sum(value) FROM inv.stock_movement WHERE variant_id = 'a3000000-0000-0000-0000-000000000001') = 0,
                 'ledger value sums to zero');

-- Going negative (emergency recovery, 6.4): allowed by the ledger, valued at the last average.
SELECT (kt.move(2, 2.000)).value;
SELECT kt.assert((kt.move(-3, NULL, 'emergency_sale')).value = -6.000, 'negative stock: rest valued at the last average');
SELECT kt.assert((SELECT qty = -1 AND value = -2.000 FROM inv.stock_balance WHERE variant_id = 'a3000000-0000-0000-0000-000000000001'), 'balance -1 / -2.000');
SELECT (kt.move(1, 2.000)).value;
SELECT kt.assert((SELECT qty = 0 AND value = 0 FROM inv.stock_balance WHERE variant_id = 'a3000000-0000-0000-0000-000000000001'), 'back to zero');
SELECT kt.assert(NOT EXISTS (SELECT 1 FROM inv.check_balances()), 'every balance equals its ledger');

-- ---------------------------------------------------------------- refusals
SELECT kt.expect_error($$SELECT kt.move(1, 100, 'opening', 'a3000000-0000-0000-0000-000000000002')$$, 'KT010');   -- serialized: Phase 5
SELECT kt.expect_error($$SELECT kt.move(1, 1, 'opening', 'a3000000-0000-0000-0000-000000000003')$$, 'KT010');     -- services are not stocked
SELECT kt.expect_error($$SELECT kt.move(1, 1, 'opening', 'a3000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000b')$$, 'KT010'); -- warehouse of another branch
SELECT kt.expect_error($$UPDATE inv.stock_movement SET qty = 99$$, 'KT001');
SELECT kt.expect_error($$DELETE FROM inv.stock_movement$$, 'KT001');

SET ROLE kite_app;
SET kite.all_branches = 'on';
SELECT kt.expect_error($$UPDATE inv.stock_balance SET qty = 1000$$, '42501');           -- balances change only through the ledger
SELECT kt.expect_error($$INSERT INTO inv.stock_balance (business_id, branch_id, warehouse_id, variant_id)
                         VALUES ('b0000000-0000-0000-0000-000000000001','a0000000-0000-0000-0000-00000000000a','a1000000-0000-0000-0000-00000000000a','a3000000-0000-0000-0000-000000000003')$$, '42501');
SELECT kt.assert((kt.move(1, 1.000)).qty_after = 1, 'kite_app can post movements; the trigger keeps the balance');
RESET ROLE;
RESET kite.all_branches;

-- ---------------------------------------------------------------- prices never overlap (9.4)
INSERT INTO catalog.price_list (id, business_id, code, name_en, name_ar, is_default)
VALUES ('a4000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'RETAIL', 'Retail', 'التجزئة', true);
INSERT INTO catalog.price (id, price_list_id, variant_id, price, min_price, effective_from, effective_to)
VALUES (gen_random_uuid(), 'a4000000-0000-0000-0000-000000000001', 'a3000000-0000-0000-0000-000000000001', 3.500, 3.000, '2026-01-01', '2026-03-01');
SELECT kt.expect_error($$INSERT INTO catalog.price (id, price_list_id, variant_id, price, effective_from)
  VALUES (gen_random_uuid(), 'a4000000-0000-0000-0000-000000000001', 'a3000000-0000-0000-0000-000000000001', 4, '2026-02-15')$$, '23P01');
INSERT INTO catalog.price (id, price_list_id, variant_id, price, effective_from)
VALUES (gen_random_uuid(), 'a4000000-0000-0000-0000-000000000001', 'a3000000-0000-0000-0000-000000000001', 4.000, '2026-03-01');
SELECT kt.expect_error($$INSERT INTO catalog.price (id, price_list_id, variant_id, price, min_price, effective_from)
  VALUES (gen_random_uuid(), 'a4000000-0000-0000-0000-000000000001', 'a3000000-0000-0000-0000-000000000003', 4, 5, '2026-01-01')$$, '23514');
SELECT kt.expect_error($$INSERT INTO catalog.barcode (id, business_id, variant_id, code) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a3000000-0000-0000-0000-000000000001', '6291041500213'),
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a3000000-0000-0000-0000-000000000003', '6291041500213')$$, '23505');

-- ---------------------------------------------------------------- shifts and cash (10.4)
INSERT INTO iam."user" (id, business_id, username, display_name_en, password_hash) VALUES
  ('a5000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'cashier1', 'Cashier', 'x'),
  ('a5000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'manager1', 'Manager', 'x');
INSERT INTO sales.shift (id, business_id, branch_id, terminal_id, cashier_id, number, opening_float) VALUES
  ('a6000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
   'd0000000-0000-0000-0000-00000000000a', 'a5000000-0000-0000-0000-000000000001', 'SH-B01-2026-000001', 20.000);
SELECT kt.expect_error($$INSERT INTO sales.shift (id, business_id, branch_id, terminal_id, cashier_id, number, opening_float) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
   'd0000000-0000-0000-0000-00000000000a', 'a5000000-0000-0000-0000-000000000002', 'SH-B01-2026-000002', 0)$$, '23505');   -- one open shift per terminal
SELECT kt.expect_error($$INSERT INTO sales.cash_event (id, business_id, branch_id, shift_id, kind, amount, reason, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a6000000-0000-0000-0000-000000000001', 'pay_out', 5, ' ', 'a5000000-0000-0000-0000-000000000001')$$, '23514');   -- a reason is required
INSERT INTO sales.cash_event (id, business_id, branch_id, shift_id, kind, amount, reason, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a6000000-0000-0000-0000-000000000001', 'pay_out', 2.500, 'Water for the shop', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$INSERT INTO sales.card_settlement (id, business_id, branch_id, shift_id, card_terminal, batch_total, recorded_total, difference, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a6000000-0000-0000-0000-000000000001', 'BM-01', 50, 45, 5, 'a5000000-0000-0000-0000-000000000001')$$, '23514');   -- a difference needs a reason

-- ---------------------------------------------------------------- sales and returns (10.2, 10.3)
CREATE FUNCTION kt.sale(p_id uuid, p_number text, p_doc text DEFAULT 'simplified_invoice', p_original uuid DEFAULT NULL, p_reason text DEFAULT NULL)
RETURNS void LANGUAGE sql AS $$
  INSERT INTO sales.sale (id, business_id, branch_id, warehouse_id, terminal_id, shift_id, cashier_id, number, doc_type, credit_reason,
                          original_sale_id, sale_date, prices_include_tax, net_total, tax_total, gross_total, einvoice_uuid, qr_payload)
  VALUES (p_id, 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
          'd0000000-0000-0000-0000-00000000000a', 'a6000000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001',
          p_number, p_doc, p_reason, p_original, '2026-01-10', true, 6.667, 0.333, 7.000, gen_random_uuid(), 'qr')
$$;
CREATE FUNCTION kt.line(p_sale uuid, p_line uuid, p_no int, p_qty numeric, p_returned uuid DEFAULT NULL)
RETURNS void LANGUAGE sql AS $$
  INSERT INTO sales.sale_line (id, sale_id, line_no, variant_id, product_type, description_en, description_ar, qty, unit_price, list_price,
                               net_amount, tax_code_id, tax_rate, tax_amount, gross_amount, returned_line_id)
  VALUES (p_line, p_sale, p_no, 'a3000000-0000-0000-0000-000000000001', 'accessory', 'USB-C cable', 'كابل', p_qty, 3.500, 3.500,
          6.667, '70000000-0000-0000-0000-000000000001', 5, 0.333, 7.000, p_returned)
$$;
SELECT kt.sale('a7000000-0000-0000-0000-000000000001', 'INV-B01-2026-000001');
SELECT kt.line('a7000000-0000-0000-0000-000000000001', 'a8000000-0000-0000-0000-000000000001', 1, 2);

SELECT kt.expect_error($$SELECT kt.sale(gen_random_uuid(), 'INV-B01-2026-000002', 'tax_invoice')$$, '23514');             -- full tax invoice needs a customer
SELECT kt.expect_error($$SELECT kt.sale(gen_random_uuid(), 'CN-B01-2026-000001', 'credit_note')$$, '23514');            -- credit note needs its original
SELECT kt.expect_error($$UPDATE sales.sale SET gross_total = 0$$, 'KT001');
SELECT kt.expect_error($$DELETE FROM sales.sale_line$$, 'KT001');

-- Return 1 of 2, then 1 more, then a third is refused.
SELECT kt.sale('a7000000-0000-0000-0000-000000000002', 'CN-B01-2026-000001', 'credit_note', 'a7000000-0000-0000-0000-000000000001', 'return');
SELECT kt.line('a7000000-0000-0000-0000-000000000002', gen_random_uuid(), 1, 1, 'a8000000-0000-0000-0000-000000000001');
SELECT kt.sale('a7000000-0000-0000-0000-000000000003', 'CN-B01-2026-000002', 'credit_note', 'a7000000-0000-0000-0000-000000000001', 'return');
SELECT kt.line('a7000000-0000-0000-0000-000000000003', gen_random_uuid(), 1, 1, 'a8000000-0000-0000-0000-000000000001');
SELECT kt.sale('a7000000-0000-0000-0000-000000000004', 'CN-B01-2026-000003', 'credit_note', 'a7000000-0000-0000-0000-000000000001', 'return');
SELECT kt.expect_error($$SELECT kt.line('a7000000-0000-0000-0000-000000000004', gen_random_uuid(), 1, 1, 'a8000000-0000-0000-0000-000000000001')$$, 'KT011');
SELECT kt.expect_error($$SELECT kt.line('a7000000-0000-0000-0000-000000000004', gen_random_uuid(), 1, 1, 'a7000000-0000-0000-0000-000000000099')$$, 'KT011');

-- Payments: card needs terminal and approval code; change must be tendered minus amount.
SELECT kt.expect_error($$INSERT INTO sales.payment (id, business_id, branch_id, sale_id, shift_id, method, amount) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a7000000-0000-0000-0000-000000000001',
   'a6000000-0000-0000-0000-000000000001', 'card', 7)$$, '23514');
SELECT kt.expect_error($$INSERT INTO sales.payment (id, business_id, branch_id, sale_id, shift_id, method, amount, tendered, change_given) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a7000000-0000-0000-0000-000000000001',
   'a6000000-0000-0000-0000-000000000001', 'cash', 7, 10, 2)$$, '23514');
INSERT INTO sales.payment (id, business_id, branch_id, sale_id, shift_id, method, amount, tendered, change_given) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a7000000-0000-0000-0000-000000000001',
   'a6000000-0000-0000-0000-000000000001', 'cash', 7, 10, 3);

-- A closed shift is final.
UPDATE sales.shift SET status = 'closed', closed_at = now(), expected_cash = 24.500, counted_cash = 24.500, variance = 0
 WHERE id = 'a6000000-0000-0000-0000-000000000001';
SELECT kt.expect_error($$UPDATE sales.shift SET counted_cash = 30 WHERE id = 'a6000000-0000-0000-0000-000000000001'$$, 'KT001');
SELECT kt.expect_error($$UPDATE sales.shift SET status = 'open', closed_at = NULL WHERE id = 'a6000000-0000-0000-0000-000000000001'$$, 'KT001');

-- ---------------------------------------------------------------- approvals
SELECT kt.expect_error($$INSERT INTO iam.approval (id, business_id, branch_id, action_code, method, status, requester_id, approver_id, decided_at, expires_at) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'POS.DISCOUNT.ABOVE_LIMIT', 'pin', 'approved',
   'a5000000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001', now(), now() + interval '5 minutes')$$, '23514');   -- nobody approves their own request

-- ---------------------------------------------------------------- branch isolation (18.3)
SET ROLE kite_app;
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000b}';
SELECT kt.assert((SELECT count(*) FROM sales.sale) = 0, 'branch B sees no sales of branch A');
SELECT kt.assert((SELECT count(*) FROM sales.sale_line) = 0, 'nor their lines');
SELECT kt.assert((SELECT count(*) FROM inv.stock_movement) = 0, 'nor their stock movements');
SELECT kt.assert((SELECT count(*) FROM inv.stock_balance) = 0, 'nor their balances');
SELECT kt.expect_error($$SELECT kt.move(1, 1)$$, '42501');     -- cannot write branch A stock
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000a}';
SELECT kt.assert((SELECT count(*) FROM sales.sale) = 4, 'branch A sees its four documents');
RESET ROLE;
RESET kite.branch_ids;

SELECT kt.assert((SELECT count(*) FROM iam.permission WHERE code LIKE 'CATALOG.%' OR code LIKE 'INV.%') = 6, 'catalog and inventory permissions seeded');
SELECT 'phase 4 database tests passed' AS result;
