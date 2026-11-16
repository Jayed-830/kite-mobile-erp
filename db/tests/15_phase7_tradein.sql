-- Phase 7 (V0016): an open trade-in is worked on and an accepted one is fixed; acceptance needs consent; settlements
-- never add up to more than the agreed value, settle the trade-in exactly when they reach it, and a void takes one
-- back; store credit never goes below zero; a sold unit comes back as a trade-in at the agreed value as its new cost;
-- the margin purchase price is the latest acquisition's; and what the application role may do.
\set ON_ERROR_STOP 1
SET client_min_messages = warning;
SET kite.all_branches = 'on';

INSERT INTO crm.customer (id, business_id, number, name_en, phone_e164) VALUES
  ('ac700000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'C-007001', 'Khalid Al Rashdi', '+96891234567');

-- ---------------------------------------------------------------- the margin scheme approval needs its reference and date
SELECT kt.expect_error($$INSERT INTO tax.margin_scheme (business_id, approved) VALUES ('b0000000-0000-0000-0000-000000000001', true)$$, '23514');
INSERT INTO tax.margin_scheme (business_id, approved, reference, approved_on) VALUES ('b0000000-0000-0000-0000-000000000001', true, 'OTA/PM/2026/0042', '2026-01-01');

-- ---------------------------------------------------------------- an open trade-in, its deductions, and consent
INSERT INTO tradein.tradein (id, business_id, branch_id, number, customer_id, variant_id, imei1, created_by) VALUES
  ('ad700000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'TI-B01-2026-000001',
   'ac700000-0000-0000-0000-000000000001', 'a3000000-0000-0000-0000-000000000002', '353918050360003', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$INSERT INTO tradein.tradein (id, business_id, branch_id, number, customer_id, variant_id, imei1, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'TI-B01-2026-000002',
   'ac700000-0000-0000-0000-000000000001', 'a3000000-0000-0000-0000-000000000002', '353918050360004', 'a5000000-0000-0000-0000-000000000001')$$, '23514');   -- not an IMEI
INSERT INTO tradein.tradein_issue (id, business_id, branch_id, tradein_id, code, name_en, name_ar, amount) VALUES
  ('ae700000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ad700000-0000-0000-0000-000000000001',
   'SCREEN', 'Cracked screen', 'شاشة متشققة', 25.000);
DELETE FROM tradein.tradein_issue WHERE id = 'ae700000-0000-0000-0000-000000000001';                                   -- replaced while open
INSERT INTO tradein.tradein_issue (id, business_id, branch_id, tradein_id, code, name_en, name_ar, amount) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ad700000-0000-0000-0000-000000000001',
   'BATTERY', 'Battery below 80%', 'البطارية أقل من 80%', 10.000);
UPDATE tradein.tradein SET guide_value = 110, deductions_total = 10, offered_value = 100, agreed_value = 100 WHERE id = 'ad700000-0000-0000-0000-000000000001';

-- Accepting needs the device unit and the customer's consent; on-screen consent needs the name, paper the form's reference.
SELECT kt.expect_error($$UPDATE tradein.tradein SET status = 'accepted', accepted_at = now() WHERE id = 'ad700000-0000-0000-0000-000000000001'$$, '23514');
SELECT kt.expect_error($$UPDATE tradein.tradein SET consent_method = 'on_screen', consent_name = ' ', consent_at = now() WHERE id = 'ad700000-0000-0000-0000-000000000001'$$, '23514');
SELECT kt.expect_error($$UPDATE tradein.tradein SET consent_method = 'paper', consent_at = now() WHERE id = 'ad700000-0000-0000-0000-000000000001'$$, '23514');

BEGIN;
INSERT INTO device.device_unit (id, business_id, branch_id, warehouse_id, variant_id, status, condition, acquisition_cost, source, source_doc_type, source_doc_id)
VALUES ('a9000000-0000-0000-0000-000000000071', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
        'a3000000-0000-0000-0000-000000000002', 'under_inspection', 'used', 100.000, 'trade_in', 'tradein', 'ad700000-0000-0000-0000-000000000001');
SELECT kt.assert((kt.umove('a9000000-0000-0000-0000-000000000071', 1, 'tradein_acquisition')).value = 100.000, 'in at the agreed value');
INSERT INTO device.acquisition (id, business_id, branch_id, device_unit_id, source, source_doc_type, source_doc_id, cost, margin_scheme)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a9000000-0000-0000-0000-000000000071',
        'trade_in', 'tradein', 'ad700000-0000-0000-0000-000000000001', 100.000, true);
UPDATE tradein.tradein SET status = 'accepted', device_unit_id = 'a9000000-0000-0000-0000-000000000071', consent_method = 'on_screen', consent_name = 'Khalid Al Rashdi',
       consent_at = now(), accepted_at = now(), margin_scheme = true, self_invoice_number = 'SI-B01-2026-000001'
 WHERE id = 'ad700000-0000-0000-0000-000000000001';
COMMIT;
SELECT kt.assert(device.margin_purchase_price('a9000000-0000-0000-0000-000000000071') = 100.000, 'bought under the margin scheme at 100.000');

-- Accepted: the device, value and consent are fixed; deductions too; a trade-in is never deleted.
SELECT kt.expect_error($$UPDATE tradein.tradein SET agreed_value = 120 WHERE id = 'ad700000-0000-0000-0000-000000000001'$$, 'KT030');
SELECT kt.expect_error($$UPDATE tradein.tradein SET status = 'open' WHERE id = 'ad700000-0000-0000-0000-000000000001'$$, 'KT030');
SELECT kt.expect_error($$UPDATE tradein.tradein SET status = 'rejected', reject_reason = 'x', closed_at = now() WHERE id = 'ad700000-0000-0000-0000-000000000001'$$, 'KT030');
SELECT kt.expect_error($$INSERT INTO tradein.tradein_issue (id, business_id, branch_id, tradein_id, code, name_en, name_ar, amount) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ad700000-0000-0000-0000-000000000001', 'BODY', 'Dent', 'انبعاج', 5)$$, 'KT030');
SELECT kt.expect_error($$DELETE FROM tradein.tradein WHERE id = 'ad700000-0000-0000-0000-000000000001'$$, 'KT030');
SELECT kt.expect_error($$UPDATE tradein.tradein SET margin_scheme = true, self_invoice_number = NULL WHERE id = 'ad700000-0000-0000-0000-000000000001'$$, 'KT030');

-- ---------------------------------------------------------------- settlements: cash 30, store credit 50, then the sale 20
INSERT INTO sales.cash_event (id, business_id, branch_id, shift_id, kind, amount, reason, created_by, tradein_id) VALUES
  ('af700000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a6000000-0000-0000-0000-000000000001',
   'tradein_payout', 30.000, 'Trade-in TI-B01-2026-000001', 'a5000000-0000-0000-0000-000000000001', 'ad700000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$INSERT INTO sales.cash_event (id, business_id, branch_id, shift_id, kind, amount, reason, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a6000000-0000-0000-0000-000000000001',
   'tradein_payout', 5, 'no trade-in', 'a5000000-0000-0000-0000-000000000001')$$, '23514');                                     -- a payout names its trade-in
INSERT INTO tradein.settlement (id, business_id, branch_id, tradein_id, kind, amount, shift_id, cash_event_id, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ad700000-0000-0000-0000-000000000001', 'cash', 30.000,
   'a6000000-0000-0000-0000-000000000001', 'af700000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001');
INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, amount, source_doc_type, source_doc_id, created_by) VALUES
  ('aa700000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ac700000-0000-0000-0000-000000000001',
   'store_credit', 50.000, 'tradein', 'ad700000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001');
INSERT INTO tradein.settlement (id, business_id, branch_id, tradein_id, kind, amount, credit_entry_id, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ad700000-0000-0000-0000-000000000001', 'store_credit', 50.000,
   'aa700000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.assert((SELECT status = 'accepted' AND settled_at IS NULL FROM tradein.tradein WHERE id = 'ad700000-0000-0000-0000-000000000001'), '80 of 100: still accepted');
SELECT kt.assert(tradein.outstanding('ad700000-0000-0000-0000-000000000001') = 20.000, '20 still to settle');

-- More than the agreed value is refused.
SELECT kt.expect_error($$INSERT INTO tradein.settlement (id, business_id, branch_id, tradein_id, kind, amount, credit_entry_id, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ad700000-0000-0000-0000-000000000001', 'store_credit', 20.001,
   'aa700000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001')$$, 'KT031');
SELECT kt.expect_error($$INSERT INTO tradein.settlement (id, business_id, branch_id, tradein_id, kind, amount, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ad700000-0000-0000-0000-000000000001', 'cash', 5,
   'a5000000-0000-0000-0000-000000000001')$$, '23514');                                                                         -- cash names its payout

-- The last 20 pay part of a sale: settled. A void takes it back: accepted again.
SELECT kt.sale('aa700000-0000-0000-0000-000000000011', 'INV-B01-2026-007001');
INSERT INTO sales.payment (id, business_id, branch_id, sale_id, shift_id, method, amount, tradein_id) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'aa700000-0000-0000-0000-000000000011',
   'a6000000-0000-0000-0000-000000000001', 'trade_in', 20.000, 'ad700000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$INSERT INTO sales.payment (id, business_id, branch_id, sale_id, shift_id, method, amount) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'aa700000-0000-0000-0000-000000000011',
   'a6000000-0000-0000-0000-000000000001', 'trade_in', 1)$$, '23514');                                                            -- names its trade-in
SELECT kt.expect_error($$INSERT INTO sales.payment (id, business_id, branch_id, sale_id, shift_id, method, amount) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'aa700000-0000-0000-0000-000000000011',
   'a6000000-0000-0000-0000-000000000001', 'store_credit', 1)$$, '23514');                                                        -- names its ledger line
INSERT INTO tradein.settlement (id, business_id, branch_id, tradein_id, kind, amount, sale_id, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ad700000-0000-0000-0000-000000000001', 'sale', 20.000,
   'aa700000-0000-0000-0000-000000000011', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.assert((SELECT status = 'settled' AND settled_at IS NOT NULL FROM tradein.tradein WHERE id = 'ad700000-0000-0000-0000-000000000001'), 'settled at 100');
SELECT kt.assert(NOT EXISTS (SELECT 1 FROM tradein.unsettled()), 'nothing left in trade-in clearing');
INSERT INTO tradein.settlement (id, business_id, branch_id, tradein_id, kind, amount, sale_id, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ad700000-0000-0000-0000-000000000001', 'sale', -20.000,
   'aa700000-0000-0000-0000-000000000011', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.assert((SELECT status = 'accepted' AND settled_at IS NULL FROM tradein.tradein WHERE id = 'ad700000-0000-0000-0000-000000000001'), 'the void makes it accepted again');
SELECT kt.assert((SELECT outstanding = 20.000 FROM tradein.unsettled() WHERE number = 'TI-B01-2026-000001'), '20 back in clearing');
SELECT kt.expect_error($$INSERT INTO tradein.settlement (id, business_id, branch_id, tradein_id, kind, amount, sale_id, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ad700000-0000-0000-0000-000000000001', 'sale', -80.001,
   'aa700000-0000-0000-0000-000000000011', 'a5000000-0000-0000-0000-000000000001')$$, 'KT031');                                   -- never below nothing
SELECT kt.expect_error($$UPDATE tradein.settlement SET amount = 1$$, 'KT001');
SELECT kt.expect_error($$DELETE FROM tradein.settlement$$, 'KT001');

-- ---------------------------------------------------------------- store credit never goes below zero
SELECT kt.expect_error($$INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, amount, source_doc_type, source_doc_id, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', 'ac700000-0000-0000-0000-000000000001',
   'store_credit', -50.001, 'sale', gen_random_uuid(), 'a5000000-0000-0000-0000-000000000001')$$, 'KT032');
INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, amount, source_doc_type, source_doc_id, created_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', 'ac700000-0000-0000-0000-000000000001',
   'store_credit', -45.000, 'sale', gen_random_uuid(), 'a5000000-0000-0000-0000-000000000001');                               -- used at another branch
SELECT kt.assert(crm.store_credit_balance('ac700000-0000-0000-0000-000000000001') = 5.000, '5.000 left');
SELECT kt.expect_error($$UPDATE crm.credit_ledger SET amount = 100$$, 'KT001');
SELECT kt.expect_error($$DELETE FROM crm.credit_ledger$$, 'KT001');

-- ---------------------------------------------------------------- a sold unit comes back as a trade-in at another branch
BEGIN;
SELECT kt.unit('a9000000-0000-0000-0000-000000000072', 'in_stock', 180.000);
SELECT kt.umove('a9000000-0000-0000-0000-000000000072', 1, 'opening');
COMMIT;
BEGIN;
SELECT kt.sell_unit('aa700000-0000-0000-0000-000000000021', 'ab700000-0000-0000-0000-000000000021', 'a9000000-0000-0000-0000-000000000072',
                    'simplified_invoice', NULL, 'INV-B01-2026-007002');
SELECT kt.ev('a9000000-0000-0000-0000-000000000072', 'sold', 'sold', NULL, NULL, 'aa700000-0000-0000-0000-000000000021');
COMMIT;

SELECT kt.expect_error($$SELECT device.reacquire('a9000000-0000-0000-0000-000000000071', 'a0000000-0000-0000-0000-00000000000b', 'a1000000-0000-0000-0000-00000000000b',
                         90, 'used', 'tradein', gen_random_uuid(), NULL, NULL)$$, 'KT020');                                    -- not sold: under inspection
SELECT kt.expect_error($$SELECT device.reacquire('a9000000-0000-0000-0000-000000000072', 'a0000000-0000-0000-0000-00000000000b', 'a1000000-0000-0000-0000-00000000000b',
                         90, 'brand_new', 'tradein', gen_random_uuid(), NULL, NULL)$$, 'KT020');                               -- a traded-in phone is used
SELECT kt.expect_error($$SELECT device.reacquire('a9000000-0000-0000-0000-000000000072', 'a0000000-0000-0000-0000-00000000000b', 'a1000000-0000-0000-0000-00000000000a',
                         90, 'used', 'tradein', gen_random_uuid(), NULL, NULL)$$, 'KT020');                                    -- the warehouse is that branch's
SELECT kt.expect_commit_error($$SELECT device.reacquire('a9000000-0000-0000-0000-000000000072', 'a0000000-0000-0000-0000-00000000000b', 'a1000000-0000-0000-0000-00000000000b',
                         90, 'used', 'tradein', gen_random_uuid(), NULL, NULL)$$, 'KT021');                                    -- not without its stock movement
BEGIN;
SELECT device.reacquire('a9000000-0000-0000-0000-000000000072', 'a0000000-0000-0000-0000-00000000000b', 'a1000000-0000-0000-0000-00000000000b',
                        90.000, 'used', 'tradein', 'ad700000-0000-0000-0000-000000000002', '{"number":"TI-B02-2026-000001"}', NULL);
INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, device_unit_id, source_doc_type, source_doc_id)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', 'a1000000-0000-0000-0000-00000000000b',
        'a3000000-0000-0000-0000-000000000002', 'tradein_acquisition', 1, 'a9000000-0000-0000-0000-000000000072', 'tradein', 'ad700000-0000-0000-0000-000000000002');
COMMIT;
SELECT kt.assert((SELECT status = 'under_inspection' AND branch_id = 'a0000000-0000-0000-0000-00000000000b' AND condition = 'used' AND accumulated_cost = 90.000
                         AND acquisition_cost = 180.000 AND sold_sale_id IS NULL
                    FROM device.device_unit WHERE id = 'a9000000-0000-0000-0000-000000000072'), 'back at Sohar, under inspection, at 90.000');
SELECT kt.assert((SELECT cost_delta = -90.000 AND from_status = 'sold' FROM device.device_event WHERE device_unit_id = 'a9000000-0000-0000-0000-000000000072'
                   AND event_type = 'traded_in'), 'the history explains the new cost');
SELECT kt.assert(NOT EXISTS (SELECT 1 FROM device.check_units() WHERE device_unit_id IN ('a9000000-0000-0000-0000-000000000071','a9000000-0000-0000-0000-000000000072')),
                 'units, history and ledger agree');
SELECT kt.assert(device.margin_purchase_price('a9000000-0000-0000-0000-000000000072') IS NULL, 'no margin acquisition yet');
INSERT INTO device.acquisition (id, business_id, branch_id, device_unit_id, source, source_doc_type, source_doc_id, cost, margin_scheme)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', 'a9000000-0000-0000-0000-000000000072',
        'buy_back', 'tradein', 'ad700000-0000-0000-0000-000000000002', 90.000, false);
SELECT kt.assert(device.margin_purchase_price('a9000000-0000-0000-0000-000000000072') IS NULL, 'bought from a VAT-registered seller: taxed in full');

-- ---------------------------------------------------------------- margin sale lines keep their purchase price
SELECT kt.expect_error($$INSERT INTO sales.sale_line (id, sale_id, line_no, variant_id, product_type, description_en, description_ar, qty, unit_price, list_price,
                               net_amount, tax_code_id, tax_rate, tax_amount, gross_amount, tax_scheme)
  VALUES (gen_random_uuid(), 'aa700000-0000-0000-0000-000000000011', 9, 'a3000000-0000-0000-0000-000000000002', 'device', 'x', 'x', 1, 150, 150, 147.619,
          '70000000-0000-0000-0000-000000000001', 5, 2.381, 150, 'margin')$$, '23514');

-- ---------------------------------------------------------------- the application role
RESET kite.all_branches;
SET ROLE kite_app;
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000a}';
SELECT kt.expect_error($$UPDATE tradein.settlement SET amount = 1$$, '42501');
SELECT kt.expect_error($$DELETE FROM tradein.tradein$$, '42501');
SELECT kt.expect_error($$UPDATE crm.credit_ledger SET amount = 1$$, '42501');
SELECT kt.expect_error($$DELETE FROM device.acquisition$$, '42501');
SELECT kt.assert((SELECT count(*) = 1 FROM tradein.tradein), 'sees its branch''s trade-ins');
SELECT kt.assert(crm.store_credit_balance('ac700000-0000-0000-0000-000000000001') = 5.000, 'the balance counts every branch');
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000b}';
SELECT kt.assert((SELECT count(*) = 0 FROM tradein.tradein), 'another branch sees none of them');
SELECT kt.expect_error($$SELECT device.reacquire('a9000000-0000-0000-0000-000000000071', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
                         1, 'used', 'tradein', gen_random_uuid(), NULL, NULL)$$, '42501');                                      -- not its branch
RESET ROLE;
RESET kite.branch_ids;

SELECT 'phase 7 trade-in tests passed' AS result;
