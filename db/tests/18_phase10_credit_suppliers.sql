-- Phase 10 (V0019): a charge is exactly its instalments; allocations never take an item or a credit past its amount and
-- stay within one customer; a customer's receivable, open items and oldest unpaid due date are sums; head office's
-- exposure counts live authorisations only; branch allowances add up to no more than the limit unless the owner allows an
-- overcommit; sales and payments move an account's stream number but not its version; the decision, item, allocation and
-- receipt rows are never changed; a merged supplier stays merged, is merged only into an active one and takes no new
-- lines of its own; a supplier's balance follows its merges; "on account" payments name their ledger row.
\set ON_ERROR_STOP 1
SET client_min_messages = warning;
SET kite.all_branches = 'on';

-- ---------------------------------------------------------------- fixture: two customers with accounts
INSERT INTO crm.credit_policy (business_id) VALUES ('b0000000-0000-0000-0000-000000000001') ON CONFLICT DO NOTHING;
INSERT INTO crm.customer (id, business_id, number, name_en, phone_e164) VALUES
  ('ac100000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'C-010001', 'Salim Al Habsi', '+96891000001'),
  ('ac100000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'C-010002', 'Aisha Al Lawati', '+96891000002');
INSERT INTO crm.credit_account (id, business_id, customer_id, credit_limit, terms_days, approved_by) VALUES
  ('ad100000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'ac100000-0000-0000-0000-000000000001', 500, 30, 'a5000000-0000-0000-0000-000000000002'),
  ('ad100000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'ac100000-0000-0000-0000-000000000002', 200, 30, 'a5000000-0000-0000-0000-000000000002');

CREATE FUNCTION kt.entry(p_id uuid, p_kind text, p_amount numeric, p_customer uuid DEFAULT 'ac100000-0000-0000-0000-000000000001',
                         p_branch uuid DEFAULT 'a0000000-0000-0000-0000-00000000000a') RETURNS void
LANGUAGE sql AS $$
  INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, kind, amount, number, doc_date, source_doc_type, source_doc_id, created_by)
  VALUES (p_id, 'b0000000-0000-0000-0000-000000000001', p_branch, p_customer, 'receivable', p_kind, p_amount, 'T-' || left(p_id::text, 8),
          DATE '2026-01-10', 'test', p_id, 'a5000000-0000-0000-0000-000000000001')
$$;
CREATE FUNCTION kt.item(p_entry uuid, p_seq int, p_due date, p_amount numeric, p_customer uuid DEFAULT 'ac100000-0000-0000-0000-000000000001',
                        p_branch uuid DEFAULT 'a0000000-0000-0000-0000-00000000000a') RETURNS uuid
LANGUAGE sql AS $$
  INSERT INTO crm.receivable_item (id, business_id, branch_id, customer_id, entry_id, seq, due_date, amount)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', p_branch, p_customer, p_entry, p_seq, p_due, p_amount) RETURNING id
$$;
CREATE FUNCTION kt.alloc(p_item uuid, p_credit uuid, p_amount numeric, p_customer uuid DEFAULT 'ac100000-0000-0000-0000-000000000001') RETURNS void
LANGUAGE sql AS $$
  INSERT INTO crm.receivable_allocation (id, business_id, customer_id, item_id, credit_entry_id, amount)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', p_customer, p_item, p_credit, p_amount)
$$;

-- ---------------------------------------------------------------- a charge is exactly its instalments (14.4)
BEGIN;
SELECT kt.entry('ae100000-0000-0000-0000-000000000001', 'charge', 300);
SELECT kt.item('ae100000-0000-0000-0000-000000000001', 1, DATE '2026-02-09', 150);
SELECT kt.item('ae100000-0000-0000-0000-000000000001', 2, DATE '2026-03-11', 150);
COMMIT;
SELECT kt.expect_commit_error($$SELECT kt.entry('ae100000-0000-0000-0000-000000000009', 'charge', 100);
  SELECT kt.item('ae100000-0000-0000-0000-000000000009', 1, DATE '2026-02-09', 60)$$, 'KT073');
SELECT kt.expect_error($$SELECT kt.entry('ae100000-0000-0000-0000-000000000008', 'charge', -5)$$, '23514');            -- a charge is positive
SELECT kt.expect_error($$SELECT kt.entry('ae100000-0000-0000-0000-000000000008', 'payment', 5)$$, '23514');            -- a payment is negative
SELECT kt.expect_error($$SELECT kt.item('ae100000-0000-0000-0000-000000000001', 3, DATE '2026-04-10', 1, 'ac100000-0000-0000-0000-000000000002')$$, 'KT073');   -- another customer's
SELECT kt.expect_error($$UPDATE crm.receivable_item SET amount = 1$$, 'KT001');

-- ---------------------------------------------------------------- payments settle open items, never past either amount
SELECT kt.entry('ae100000-0000-0000-0000-000000000002', 'payment', -200);
CREATE TEMP TABLE it AS SELECT id, seq FROM crm.receivable_item WHERE entry_id = 'ae100000-0000-0000-0000-000000000001';
SELECT kt.alloc((SELECT id FROM it WHERE seq = 1), 'ae100000-0000-0000-0000-000000000002', 150);
SELECT kt.expect_error($$SELECT kt.alloc((SELECT id FROM it WHERE seq = 1), 'ae100000-0000-0000-0000-000000000002', 1)$$, 'KT070');   -- item paid
SELECT kt.expect_error($$SELECT kt.alloc((SELECT id FROM it WHERE seq = 2), 'ae100000-0000-0000-0000-000000000002', 51)$$, 'KT070');  -- only 50 left on the payment
SELECT kt.expect_error($$SELECT kt.alloc((SELECT id FROM it WHERE seq = 2), 'ae100000-0000-0000-0000-000000000001', 10)$$, 'KT070');  -- a charge settles nothing
SELECT kt.alloc((SELECT id FROM it WHERE seq = 2), 'ae100000-0000-0000-0000-000000000002', 30);
SELECT kt.expect_error($$UPDATE crm.receivable_allocation SET amount = 1$$, 'KT001');

SELECT kt.assert((s).balance = 100 AND (s).open_items = 120 AND (s).unallocated = 20 AND (s).oldest_due = DATE '2026-03-11',
                 'balance 300 - 200; 120 still open on the second instalment; 20 of the payment on account')
  FROM (SELECT crm.receivable_status('ac100000-0000-0000-0000-000000000001') AS s) x;

-- A payment at the other branch moves the receivable there by two transfer rows that net to nothing.
SELECT kt.entry('ae100000-0000-0000-0000-000000000003', 'transfer', 40, p_branch => 'a0000000-0000-0000-0000-00000000000b');
SELECT kt.entry('ae100000-0000-0000-0000-000000000004', 'transfer', -40);
SELECT kt.assert((s).balance = 100 AND (s).unallocated = 20, 'transfers change neither the balance nor what is on account')
  FROM (SELECT crm.receivable_status('ac100000-0000-0000-0000-000000000001') AS s) x;
SELECT kt.expect_error($$SELECT kt.alloc((SELECT id FROM it WHERE seq = 2), 'ae100000-0000-0000-0000-000000000004', 1)$$, 'KT070');  -- a transfer settles nothing

-- ---------------------------------------------------------------- exposure, stream numbers, versions (14.2)
SELECT kt.assert(crm.credit_exposure('ac100000-0000-0000-0000-000000000001') = 100, 'exposure is the balance');
INSERT INTO crm.credit_authorisation (id, business_id, customer_id, branch_id, server_id, sale_id, amount, available, expires_at) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'ac100000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b',
   gen_random_uuid(), gen_random_uuid(), 25, 400, now() + interval '15 minutes'),
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'ac100000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b',
   gen_random_uuid(), gen_random_uuid(), 70, 400, now() - interval '1 minute');
SELECT kt.assert(crm.credit_exposure('ac100000-0000-0000-0000-000000000001') = 125, 'a live authorisation counts; an expired one does not');
UPDATE crm.credit_authorisation SET consumed_at = now() WHERE amount = 25;
SELECT kt.assert(crm.credit_exposure('ac100000-0000-0000-0000-000000000001') = 100, 'a consumed authorisation no longer counts');

CREATE TEMP TABLE before AS SELECT version, sync_seq FROM crm.credit_account WHERE id = 'ad100000-0000-0000-0000-000000000001';
SELECT kt.entry('ae100000-0000-0000-0000-000000000005', 'payment', -10);
SELECT kt.assert(a.version = b.version AND a.sync_seq > b.sync_seq, 'a payment moves the stream number, not the version')
  FROM crm.credit_account a, before b WHERE a.id = 'ad100000-0000-0000-0000-000000000001';
UPDATE crm.credit_account SET credit_limit = 600 WHERE id = 'ad100000-0000-0000-0000-000000000001';
SELECT kt.assert(a.version = b.version + 1, 'a new limit is a new version') FROM crm.credit_account a, before b WHERE a.id = 'ad100000-0000-0000-0000-000000000001';
-- A branch server applying head office's copy keeps head office's numbers.
BEGIN;
SET LOCAL kite.credit_replica = 'on';
UPDATE crm.credit_account SET version = 42, sync_seq = 9000, exposure = 77 WHERE id = 'ad100000-0000-0000-0000-000000000002';
COMMIT;
SELECT kt.assert(version = 42 AND sync_seq = 9000, 'replica keeps head office numbers') FROM crm.credit_account WHERE id = 'ad100000-0000-0000-0000-000000000002';
SELECT kt.expect_error($$UPDATE crm.credit_account SET status = 'hold' WHERE id = 'ad100000-0000-0000-0000-000000000002'$$, '23514');   -- a hold has a kind and reason

-- ---------------------------------------------------------------- allowances (14.1)
INSERT INTO crm.credit_allowance (id, business_id, account_id, branch_id, amount) VALUES
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'ad100000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-00000000000a', 120),
  (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'ad100000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-00000000000b', 80);
SELECT kt.expect_commit_error($$UPDATE crm.credit_allowance SET amount = 81 WHERE branch_id = 'a0000000-0000-0000-0000-00000000000b' AND account_id = 'ad100000-0000-0000-0000-000000000002'$$, 'KT071');
SELECT kt.expect_commit_error($$UPDATE crm.credit_account SET credit_limit = 150 WHERE id = 'ad100000-0000-0000-0000-000000000002'$$, 'KT071');
UPDATE crm.credit_policy SET allow_overcommit = true;
UPDATE crm.credit_allowance SET amount = 150 WHERE branch_id = 'a0000000-0000-0000-0000-00000000000b' AND account_id = 'ad100000-0000-0000-0000-000000000002';
SELECT kt.assert((SELECT sum(amount) FROM crm.credit_allowance WHERE account_id = 'ad100000-0000-0000-0000-000000000002') = 270, 'the owner allowed an overcommit');
UPDATE crm.credit_policy SET allow_overcommit = false;

-- ---------------------------------------------------------------- decisions and receipts are records
INSERT INTO crm.credit_decision (id, business_id, branch_id, customer_id, sale_id, entry_id, mode, amount, available, credit_limit, exposure, created_by)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ac100000-0000-0000-0000-000000000001',
        gen_random_uuid(), 'ae100000-0000-0000-0000-000000000001', 'local', 300, 500, 500, 0, 'a5000000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$INSERT INTO crm.credit_decision (id, business_id, branch_id, customer_id, sale_id, entry_id, mode, amount, available, credit_limit, exposure, over_by, created_by)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ac100000-0000-0000-0000-000000000001',
          gen_random_uuid(), 'ae100000-0000-0000-0000-000000000001', 'offline', 50, 10, 500, 0, 40, 'a5000000-0000-0000-0000-000000000001')$$, '23514');   -- over the limit needs an approver
SELECT kt.expect_error($$UPDATE crm.credit_decision SET amount = 1$$, 'KT001');
SELECT kt.expect_error($$INSERT INTO crm.customer_receipt (id, business_id, branch_id, customer_id, number, method, amount, entry_id, created_by)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ac100000-0000-0000-0000-000000000001',
          'RC-T-1', 'cash', 10, 'ae100000-0000-0000-0000-000000000005', 'a5000000-0000-0000-0000-000000000001')$$, '23514');   -- cash goes on a drawer

-- ---------------------------------------------------------------- suppliers (16.1)
INSERT INTO purch.supplier (id, business_id, number, name_en, name_ar, status) VALUES
  ('af100000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'S-00001', 'Gulf Parts', 'قطع الخليج', 'active'),
  ('af100000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'SP-B02-2026-000001', 'Gulf Parts Co', 'قطع الخليج', 'provisional'),
  ('af100000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000001', 'SP-B02-2026-000002', 'Sohar Screens', 'شاشات صحار', 'provisional');
CREATE FUNCTION kt.sline(p_supplier uuid, p_amount numeric, p_branch uuid DEFAULT 'a0000000-0000-0000-0000-00000000000b', p_origin uuid DEFAULT NULL) RETURNS void
LANGUAGE sql AS $$
  INSERT INTO purch.supplier_ledger (id, business_id, branch_id, supplier_id, kind, amount, doc_date, number, source_doc_type, source_doc_id, created_by, origin_server_id)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', p_branch, p_supplier, 'opening', p_amount, DATE '2026-01-01', 'OB-T',
          'opening_balance', gen_random_uuid(), 'a5000000-0000-0000-0000-000000000001', p_origin)
$$;
SELECT kt.sline('af100000-0000-0000-0000-000000000001', 100, 'a0000000-0000-0000-0000-00000000000a');
SELECT kt.sline('af100000-0000-0000-0000-000000000002', 30);
SELECT kt.expect_error($$UPDATE purch.supplier SET status = 'merged', merged_into_id = 'af100000-0000-0000-0000-000000000003' WHERE id = 'af100000-0000-0000-0000-000000000002'$$, 'KT072');   -- into a provisional one
UPDATE purch.supplier SET status = 'merged', merged_into_id = 'af100000-0000-0000-0000-000000000001' WHERE id = 'af100000-0000-0000-0000-000000000002';
SELECT kt.assert(purch.supplier_balance('af100000-0000-0000-0000-000000000001') = 130, 'the balance follows the merge');
SELECT kt.expect_error($$UPDATE purch.supplier SET status = 'active', merged_into_id = NULL WHERE id = 'af100000-0000-0000-0000-000000000002'$$, 'KT072');
SELECT kt.expect_error($$SELECT kt.sline('af100000-0000-0000-0000-000000000002', 5, p_origin => 'c0000000-0000-0000-0000-000000000001')$$, 'KT072');  -- new lines go to the survivor
SELECT kt.sline('af100000-0000-0000-0000-000000000002', 5, p_origin => gen_random_uuid());     -- a branch's earlier line still arrives
UPDATE purch.supplier SET status = 'active' WHERE id = 'af100000-0000-0000-0000-000000000003';
SELECT kt.expect_error($$UPDATE purch.supplier SET status = 'provisional' WHERE id = 'af100000-0000-0000-0000-000000000003'$$, 'KT072');
SELECT kt.expect_error($$UPDATE purch.supplier_ledger SET amount = 1$$, 'KT001');

-- ---------------------------------------------------------------- "on account" payments name their ledger row
SELECT kt.assert(EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'payment_store_credit'
                          AND pg_get_constraintdef(oid) LIKE '%credit%credit_entry_id%'), 'an on-account payment carries its ledger row');

-- ---------------------------------------------------------------- the application role
SET ROLE kite_app;
SET kite.all_branches = 'on';
SELECT kt.expect_error($$DELETE FROM crm.credit_account$$, '42501');
SELECT kt.expect_error($$DELETE FROM purch.supplier$$, '42501');
SELECT kt.expect_error($$UPDATE crm.credit_decision SET mode = 'local'$$, '42501');
SELECT kt.expect_error($$UPDATE crm.customer_receipt SET amount = 1$$, '42501');
RESET ROLE;
