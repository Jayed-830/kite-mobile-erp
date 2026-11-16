-- Phase 4 (V0013): emergency shifts beside a normal shift, the balance lock for counts, network printers and
-- resolve-only stock exceptions.
\set ON_ERROR_STOP 1
SET client_min_messages = warning;

INSERT INTO core.terminal (id, business_id, branch_id, code, name, kind) VALUES
  ('d0000000-0000-0000-0000-0000000000e1', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'T09', 'Till 9', 'windows');
INSERT INTO sales.shift (id, business_id, branch_id, terminal_id, cashier_id, number, opening_float) VALUES
  ('a6000000-0000-0000-0000-0000000000e1', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
   'd0000000-0000-0000-0000-0000000000e1', 'a5000000-0000-0000-0000-000000000001', 'SH-B01-2026-000901', 10);
SELECT kt.expect_error($$INSERT INTO sales.shift (id, business_id, branch_id, terminal_id, cashier_id, number, opening_float) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
   'd0000000-0000-0000-0000-0000000000e1', 'a5000000-0000-0000-0000-000000000001', 'SH-B01-2026-000902', 0)$$, '23505');   -- still one normal shift
INSERT INTO sales.shift (id, business_id, branch_id, terminal_id, cashier_id, number, opening_float, is_emergency) VALUES
  ('a6000000-0000-0000-0000-0000000000e2', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
   'd0000000-0000-0000-0000-0000000000e1', 'a5000000-0000-0000-0000-000000000001', 'EMS-T09-000001', 5, true);
SELECT kt.assert((SELECT count(*) FROM sales.shift WHERE terminal_id = 'd0000000-0000-0000-0000-0000000000e1' AND status = 'open') = 2,
                 'an uploaded emergency shift may sit beside the open normal shift');

-- The count lock: kite_app reads and locks a balance of its own branch only.
SELECT kt.move(7, 1, 'opening');
SET ROLE kite_app;
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000a}';
BEGIN;
SELECT kt.assert(inv.lock_balance('a1000000-0000-0000-0000-00000000000a', 'a3000000-0000-0000-0000-000000000001') >= 7, 'locks and returns the quantity');
SELECT kt.assert(inv.lock_balance('a1000000-0000-0000-0000-00000000000a', gen_random_uuid()) = 0, 'no balance yet means zero');
COMMIT;
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000b}';
SELECT kt.expect_error($$SELECT inv.lock_balance('a1000000-0000-0000-0000-00000000000a', 'a3000000-0000-0000-0000-000000000001')$$, '42501');
RESET ROLE;
RESET kite.branch_ids;

-- Printers: a host on the shop network, never deleted by the application.
INSERT INTO sales.printer (id, business_id, branch_id, name, host) VALUES
  ('ab000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'Front', '192.168.1.50');
SELECT kt.assert((SELECT port = 9100 AND paper_width_mm = 80 FROM sales.printer WHERE id = 'ab000000-0000-0000-0000-000000000001'), 'defaults: raw port 9100, 80 mm');
SELECT kt.expect_error($$INSERT INTO sales.printer (id, business_id, branch_id, name, host) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'Bad', 'http://x/')$$, '23514');
SELECT kt.expect_error($$INSERT INTO sales.printer (id, business_id, branch_id, name, host, paper_width_mm) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'Wide', '10.0.0.9', 110)$$, '23514');
SET ROLE kite_app;
SET kite.all_branches = 'on';
SELECT kt.expect_error($$DELETE FROM sales.printer$$, '42501');
RESET ROLE;
RESET kite.all_branches;

-- Stock exceptions are resolved once and never changed afterwards.
INSERT INTO inv.stock_exception (id, business_id, branch_id, warehouse_id, variant_id, kind, qty_after, source_doc_type, source_doc_id) VALUES
  ('ac000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
   'a1000000-0000-0000-0000-00000000000a', 'a3000000-0000-0000-0000-000000000001', 'negative_stock', -1, 'sale', gen_random_uuid());
UPDATE inv.stock_exception SET status = 'resolved', resolved_at = now(), resolution_note = 'counted' WHERE id = 'ac000000-0000-0000-0000-000000000001';
SELECT kt.expect_error($$UPDATE inv.stock_exception SET resolution_note = 'changed' WHERE id = 'ac000000-0000-0000-0000-000000000001'$$, 'KT001');

SELECT 'phase 4 printer and emergency tests passed' AS result;
