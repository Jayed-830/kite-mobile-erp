-- Phase 6 (V0015): identifiers retired, never changed; only active identifiers are unique; correcting an identifier
-- while on hold keeps where the unit goes back to; a receipt reversed only from hold and only with the stock out;
-- holds with reasons; the central registry's one active row per identifier; head office's instructions are
-- append-only; the blocked list; and what the application role may do.
\set ON_ERROR_STOP 1
SET client_min_messages = warning;
SET kite.all_branches = 'on';

-- ---------------------------------------------------------------- a unit with a mistyped IMEI
BEGIN;
SELECT kt.unit('a9000000-0000-0000-0000-000000000061', 'in_stock', 150.000);
SELECT kt.umove('a9000000-0000-0000-0000-000000000061', 1, 'opening');
INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key) VALUES
  ('aa600000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000061', 'imei', 1, '356938035643809', gen_random_uuid());
COMMIT;

-- Retiring is the only change, it needs a reason, and nothing is deleted.
SELECT kt.expect_error($$UPDATE device.imei_registry SET value = '490154203237518' WHERE id = 'aa600000-0000-0000-0000-000000000001'$$, 'KT001');
SELECT kt.expect_error($$DELETE FROM device.imei_registry WHERE id = 'aa600000-0000-0000-0000-000000000001'$$, 'KT001');
SELECT kt.expect_error($$SELECT device.retire_identifier('aa600000-0000-0000-0000-000000000001', ' ', NULL)$$, 'KT020');
SELECT kt.expect_error($$UPDATE device.imei_registry SET status = 'retired' WHERE id = 'aa600000-0000-0000-0000-000000000001'$$, '23514');   -- retired needs its time

-- On hold, the identifier is corrected: the unit keeps the status it goes back to.
SELECT kt.ev('a9000000-0000-0000-0000-000000000061', 'held', 'quarantined');
SELECT device.retire_identifier('aa600000-0000-0000-0000-000000000001', 'typing error: last digits swapped', NULL);
INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key) VALUES
  ('aa600000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000061', 'imei', 1, '490154203237518', gen_random_uuid());
SELECT kt.ev('a9000000-0000-0000-0000-000000000061', 'identifier_corrected', NULL);
SELECT kt.assert((SELECT status = 'quarantined' AND held_from_status = 'in_stock' FROM device.device_unit WHERE id = 'a9000000-0000-0000-0000-000000000061'),
                 'still on hold, still going back to in stock');
SELECT kt.expect_error($$UPDATE device.imei_registry SET status = 'active', retired_at = NULL WHERE id = 'aa600000-0000-0000-0000-000000000001'$$, 'KT001');
SELECT kt.ev('a9000000-0000-0000-0000-000000000061', 'released', NULL);
SELECT kt.assert((SELECT status = 'in_stock' FROM device.device_unit WHERE id = 'a9000000-0000-0000-0000-000000000061'), 'released to in stock');

-- The retired IMEI is free for the phone it really belongs to; the active one is not.
BEGIN;
SELECT kt.unit('a9000000-0000-0000-0000-000000000062', 'in_stock', 140.000);
SELECT kt.umove('a9000000-0000-0000-0000-000000000062', 1, 'opening');
INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000062', 'imei', 1, '356938035643809', gen_random_uuid());
COMMIT;
SELECT kt.expect_error($$INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000062', 'imei', 2, '490154203237518', gen_random_uuid())$$, '23505');
SELECT kt.expect_error($$INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a9000000-0000-0000-0000-000000000062', 'imei', 1, '353918050360003', gen_random_uuid())$$, '23505');   -- slot 1 taken
SELECT kt.assert((SELECT count(*) = 2 FROM device.imei_registry WHERE value IN ('356938035643809','490154203237518') AND status = 'active'),
                 'one active row per IMEI');

-- ---------------------------------------------------------------- reversing a receipt: only from hold, only with the stock out
SELECT kt.expect_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000062', 'receipt_reversed', 'reversed')$$, 'KT020');   -- in stock: not without a hold
SELECT kt.ev('a9000000-0000-0000-0000-000000000062', 'held', 'quarantined');
SELECT kt.expect_commit_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000062', 'receipt_reversed', 'reversed')$$, 'KT021');   -- still in the ledger
BEGIN;
SELECT kt.ev('a9000000-0000-0000-0000-000000000062', 'receipt_reversed', 'reversed');
SELECT kt.umove('a9000000-0000-0000-0000-000000000062', -1, 'adjustment');
COMMIT;
SELECT kt.assert((SELECT status = 'reversed' AND held_from_status IS NULL FROM device.device_unit WHERE id = 'a9000000-0000-0000-0000-000000000062'), 'reversed');
SELECT kt.expect_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000062', 'held', 'quarantined')$$, 'KT020');            -- a reversed unit is gone
SELECT kt.expect_error($$SELECT kt.ev('a9000000-0000-0000-0000-000000000062', 'identifier_corrected', NULL)$$, 'KT020');
SELECT kt.assert(NOT EXISTS (SELECT 1 FROM device.check_units() WHERE device_unit_id IN ('a9000000-0000-0000-0000-000000000061','a9000000-0000-0000-0000-000000000062')),
                 'units and ledger agree');

-- ---------------------------------------------------------------- holds with reasons
INSERT INTO device.unit_hold (id, business_id, branch_id, device_unit_id, reason, note) VALUES
  ('ab600000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a9000000-0000-0000-0000-000000000061', 'manager', 'check the box');
SELECT kt.expect_error($$INSERT INTO device.unit_hold (id, business_id, branch_id, device_unit_id, reason) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a9000000-0000-0000-0000-000000000061', 'manager')$$, '23505');   -- one open per reason
SELECT kt.expect_error($$INSERT INTO device.unit_hold (id, business_id, branch_id, device_unit_id, reason) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a9000000-0000-0000-0000-000000000061', 'imei_conflict')$$, '23514');   -- a conflict hold names the conflict
INSERT INTO device.unit_hold (id, business_id, branch_id, device_unit_id, reason, conflict_id) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a9000000-0000-0000-0000-000000000061', 'imei_conflict', gen_random_uuid()),
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a9000000-0000-0000-0000-000000000061', 'imei_conflict', gen_random_uuid());   -- two conflicts
UPDATE device.unit_hold SET released_at = now() WHERE id = 'ab600000-0000-0000-0000-000000000001';
INSERT INTO device.unit_hold (id, business_id, branch_id, device_unit_id, reason) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a9000000-0000-0000-0000-000000000061', 'manager');   -- a new hold after release

-- ---------------------------------------------------------------- the central registry: one active row per identifier
-- c0...02 is the Sohar branch server (10_phase3.sql).
INSERT INTO device.central_registry (id, business_id, kind, value, server_id, branch_id, device_unit_id, unit_status, condition, status) VALUES
  ('ac600000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'imei', '359881030314355', 'c0000000-0000-0000-0000-000000000001',
   'a0000000-0000-0000-0000-00000000000a', gen_random_uuid(), 'in_stock', 'brand_new', 'active');
SELECT kt.expect_error($$INSERT INTO device.central_registry (id, business_id, kind, value, server_id, branch_id, device_unit_id, unit_status, condition, status) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'imei', '359881030314355', 'c0000000-0000-0000-0000-000000000002',
   'a0000000-0000-0000-0000-00000000000b', gen_random_uuid(), 'in_stock', 'brand_new', 'active')$$, '23505');            -- a second unit cannot hold it
SELECT kt.expect_error($$INSERT INTO device.central_registry (id, business_id, kind, value, server_id, branch_id, device_unit_id, unit_status, condition, status) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'imei', '359881030314356', 'c0000000-0000-0000-0000-000000000002',
   'a0000000-0000-0000-0000-00000000000b', gen_random_uuid(), 'in_stock', 'brand_new', 'active')$$, '23514');            -- check digit
SELECT kt.expect_error($$INSERT INTO device.central_registry (id, business_id, kind, value, server_id, branch_id, device_unit_id, unit_status, condition, status) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'imei', '359881030314355', 'c0000000-0000-0000-0000-000000000002',
   'a0000000-0000-0000-0000-00000000000b', gen_random_uuid(), 'in_stock', 'brand_new', 'conflict')$$, '23514');          -- a conflict row names its conflict
INSERT INTO device.imei_conflict (id, business_id, kind, value) VALUES
  ('ad600000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'imei', '359881030314355');
INSERT INTO device.central_registry (id, business_id, kind, value, server_id, branch_id, device_unit_id, unit_status, condition, status, conflict_id) VALUES
  ('ac600000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'imei', '359881030314355', 'c0000000-0000-0000-0000-000000000002',
   'a0000000-0000-0000-0000-00000000000b', gen_random_uuid(), 'in_stock', 'used', 'conflict', 'ad600000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$INSERT INTO device.imei_conflict (id, business_id, kind, value) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'imei', '359881030314355')$$, '23505');                    -- one open conflict per identifier
-- Serial numbers are unique per brand name.
INSERT INTO device.central_registry (id, business_id, kind, value, brand_name, server_id, branch_id, device_unit_id, unit_status, condition) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'serial', 'C02XK1ABJG5H', 'apple', 'c0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', gen_random_uuid(), 'in_stock', 'brand_new'),
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'serial', 'C02XK1ABJG5H', 'lenovo', 'c0000000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-00000000000b', gen_random_uuid(), 'in_stock', 'brand_new');
SELECT kt.expect_error($$INSERT INTO device.central_registry (id, business_id, kind, value, brand_name, server_id, branch_id, device_unit_id, unit_status, condition) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'serial', 'C02XK1ABJG5H', 'apple', 'c0000000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-00000000000b', gen_random_uuid(), 'in_stock', 'brand_new')$$, '23505');

-- Head office's instructions are append-only, in order.
INSERT INTO device.central_instruction (business_id, server_id, kind, device_unit_id, conflict_id, value) VALUES
  ('b0000000-0000-0000-0000-000000000001', 'c0000000-0000-0000-0000-000000000002', 'hold', gen_random_uuid(), 'ad600000-0000-0000-0000-000000000001', '359881030314355');
SELECT kt.expect_error($$UPDATE device.central_instruction SET kind = 'release'$$, 'KT001');
SELECT kt.expect_error($$DELETE FROM device.central_instruction$$, 'KT001');

-- ---------------------------------------------------------------- the blocked list
INSERT INTO device.blocked_imei (id, business_id, kind, value, reason, seq) VALUES
  ('ae600000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'imei', '351756051523993', 'Reported stolen', nextval('device.blocked_seq'));
SELECT kt.expect_error($$INSERT INTO device.blocked_imei (id, business_id, kind, value, reason, seq) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'imei', '351756051523993', 'again', nextval('device.blocked_seq'))$$, '23505');
SELECT kt.expect_error($$INSERT INTO device.blocked_imei (id, business_id, kind, value, reason, seq) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'imei', '351756051523990', 'bad digit', nextval('device.blocked_seq'))$$, '23514');
SELECT kt.expect_error($$INSERT INTO device.blocked_imei (id, business_id, kind, value, reason, seq) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'serial', 'X1', ' ', nextval('device.blocked_seq'))$$, '23514');   -- a reason is required
SELECT kt.expect_error($$UPDATE device.blocked_imei SET status = 'removed' WHERE id = 'ae600000-0000-0000-0000-000000000001'$$, '23514');   -- removed needs its time
UPDATE device.blocked_imei SET status = 'removed', removed_at = now(), removed_reason = 'found by the owner', seq = nextval('device.blocked_seq')
 WHERE id = 'ae600000-0000-0000-0000-000000000001';
INSERT INTO device.blocked_imei (id, business_id, kind, value, reason, seq) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'imei', '351756051523993', 'Reported stolen again', nextval('device.blocked_seq'));

-- ---------------------------------------------------------------- model hints, serial formats, counts
SELECT kt.expect_error($$INSERT INTO device.tac_model (business_id, tac, brand, model) VALUES ('b0000000-0000-0000-0000-000000000001', '3569380', 'Apple', 'iPhone')$$, '23514');
INSERT INTO device.tac_model (business_id, tac, brand, model) VALUES ('b0000000-0000-0000-0000-000000000001', '35693803', 'Apple', 'iPhone 13');
UPDATE catalog.brand SET serial_pattern = '^[A-Z0-9]{12}$' WHERE false;
INSERT INTO device.unit_count (id, business_id, branch_id, number, started_by) VALUES
  ('af600000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'DC-B01-2026-000001', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$INSERT INTO device.unit_count (id, business_id, branch_id, number, started_by) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'DC-B01-2026-000002', 'a5000000-0000-0000-0000-000000000001')$$, '23505');   -- one open count of all devices
INSERT INTO device.unit_count_scan (id, count_id, business_id, branch_id, kind, value, device_unit_id, result, scanned_by) VALUES
  (gen_random_uuid(), 'af600000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'imei', '490154203237518',
   'a9000000-0000-0000-0000-000000000061', 'here', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$INSERT INTO device.unit_count_scan (id, count_id, business_id, branch_id, kind, value, result, scanned_by) VALUES
  (gen_random_uuid(), 'af600000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'imei', '490154203237518',
   'here', 'a5000000-0000-0000-0000-000000000001')$$, '23505');                                                            -- scanned once per count
SELECT kt.expect_error($$UPDATE device.unit_count SET status = 'posted' WHERE id = 'af600000-0000-0000-0000-000000000001'$$, '23514');   -- posted keeps its result

-- ---------------------------------------------------------------- the application role
RESET kite.all_branches;
SET ROLE kite_app;
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000a}';
SELECT kt.expect_error($$UPDATE device.imei_registry SET status = 'retired', retired_at = now()$$, '42501');   -- only through retire_identifier
SELECT kt.expect_error($$DELETE FROM device.central_registry$$, '42501');
SELECT kt.expect_error($$DELETE FROM device.blocked_imei$$, '42501');
SELECT kt.expect_error($$DELETE FROM device.unit_hold$$, '42501');
SELECT kt.assert((SELECT count(*) >= 3 FROM device.unit_hold WHERE device_unit_id = 'a9000000-0000-0000-0000-000000000061'), 'sees its branch''s holds');
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000b}';
SELECT kt.assert((SELECT count(*) = 0 FROM device.unit_hold), 'another branch sees none of them');
SELECT kt.expect_error($$SELECT device.retire_identifier('aa600000-0000-0000-0000-000000000002', 'not mine', NULL)$$, '42501');
RESET ROLE;
RESET kite.branch_ids;

SELECT 'phase 6 IMEI tests passed' AS result;
