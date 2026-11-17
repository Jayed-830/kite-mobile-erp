-- Phase 11 (V0020): a period closed at one branch stops that branch's journals and lines, not the other branch's; an order's
-- lines change only while it is a draft and its status moves only forward; receipts never pass the order (plus the allowance);
-- goods received not invoiced is a ledger that outside work feeds by itself and that is never cleared past what was received;
-- invoices are booked once per supplier number, never invoice more than was received less returned, and are frozen once posted;
-- allocations stay within one branch and never pass either row; manual journal lines change only in a draft and a second
-- approver is someone else; a stock cost is corrected only on stock on hand; a filed VAT return is frozen; the new ledgers are
-- never changed, and the application cannot update what it may only add.
\set ON_ERROR_STOP 1
SET client_min_messages = warning;
SET kite.all_branches = 'on';

INSERT INTO purch.policy (business_id) VALUES ('b0000000-0000-0000-0000-000000000001') ON CONFLICT DO NOTHING;
INSERT INTO acct.account (id, business_id, code, name_en, name_ar, type) VALUES
  ('f0000000-0000-0000-0000-000000002110', 'b0000000-0000-0000-0000-000000000001', '2110', 'GRNI', 'بضاعة مستلمة غير مفوترة', 'liability')
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------- periods closed branch by branch (17.5)
CREATE FUNCTION kt.jv(p_branch uuid, p_line_branch uuid DEFAULT NULL, p_period uuid DEFAULT 'e0000000-0000-0000-0000-000000000001',
                      p_date date DEFAULT DATE '2026-01-20') RETURNS void
LANGUAGE plpgsql AS $$
DECLARE j uuid := gen_random_uuid();
BEGIN
  INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind)
  VALUES (j, 'b0000000-0000-0000-0000-000000000001', p_branch, 'JV-T-' || left(j::text, 8), p_date, p_period, 'test', j, 'test.p11');
  INSERT INTO acct.journal_line (id, journal_id, line_no, account_id, branch_id, debit) VALUES
    (gen_random_uuid(), j, 1, 'f0000000-0000-0000-0000-000000001100', p_branch, 10);
  INSERT INTO acct.journal_line (id, journal_id, line_no, account_id, branch_id, credit) VALUES
    (gen_random_uuid(), j, 2, 'f0000000-0000-0000-0000-000000004100', coalesce(p_line_branch, p_branch), 10);
END $$;
-- Sohar's January is soft-closed by its own row; Muscat's is not.
INSERT INTO acct.fiscal_period (id, business_id, branch_id, fiscal_year, period_no, starts_on, ends_on, status) VALUES
  ('e1100000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', 2026, 1, '2026-01-01', '2026-01-31', 'soft_closed');
SELECT kt.assert(acct.period_status('e0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b') = 'soft_closed', 'Sohar''s January is soft-closed');
SELECT kt.assert(acct.period_status('e0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a') = 'open', 'Muscat''s January is open');
SELECT kt.assert(acct.period_status('e0000000-0000-0000-0000-000000000003', 'a0000000-0000-0000-0000-00000000000a') = 'soft_closed', 'the business-wide status applies to every branch');
SELECT kt.jv('a0000000-0000-0000-0000-00000000000a');
SELECT kt.expect_error($$SELECT kt.jv('a0000000-0000-0000-0000-00000000000b')$$, 'KT003');
-- A Muscat journal with a line at Sohar is stopped by the line.
SELECT kt.expect_error($$SELECT kt.jv('a0000000-0000-0000-0000-00000000000a', 'a0000000-0000-0000-0000-00000000000b')$$, 'KT003');
-- The accountant may post into a soft-closed period.
BEGIN;
SET LOCAL kite.allow_soft_closed = 'on';
SELECT kt.jv('a0000000-0000-0000-0000-00000000000b');
COMMIT;
-- Hard-closed: nobody posts.
UPDATE acct.fiscal_period SET status = 'closed' WHERE id = 'e1100000-0000-0000-0000-000000000001';
BEGIN;
SET LOCAL kite.allow_soft_closed = 'on';
SELECT kt.expect_error($$SELECT kt.jv('a0000000-0000-0000-0000-00000000000b')$$, 'KT003');
COMMIT;
SELECT kt.expect_error($$UPDATE acct.fiscal_period SET ends_on = DATE '2026-01-30' WHERE id = 'e1100000-0000-0000-0000-000000000001'$$, 'KT003');
SELECT kt.expect_error($$INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'JV-X', DATE '2026-01-20',
          'e1100000-0000-0000-0000-000000000001', 'test', gen_random_uuid(), 'test.branch_period')$$, 'KT003');   -- journals take the business-wide period
UPDATE acct.fiscal_period SET status = 'open' WHERE id = 'e1100000-0000-0000-0000-000000000001';

-- ---------------------------------------------------------------- purchase orders (16.2)
INSERT INTO purch.purchase_order (id, business_id, branch_id, number, supplier_id, order_date, created_by) VALUES
  ('b1100000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'PO-T-1',
   'af100000-0000-0000-0000-000000000001', DATE '2026-01-10', 'a5000000-0000-0000-0000-000000000001');
INSERT INTO purch.po_line (id, po_id, line_no, variant_id, description_en, deliver_branch_id, qty, unit_price, tax_code_id, tax_rate, net_amount, tax_amount) VALUES
  ('b1200000-0000-0000-0000-000000000001', 'b1100000-0000-0000-0000-000000000001', 1, 'a3000000-0000-0000-0000-000000000001', 'USB-C cable',
   'a0000000-0000-0000-0000-00000000000a', 10, 1.000, '70000000-0000-0000-0000-000000000001', 5, 10.000, 0.500),
  ('b1200000-0000-0000-0000-000000000002', 'b1100000-0000-0000-0000-000000000001', 2, 'a3000000-0000-0000-0000-000000000001', 'USB-C cable (Sohar)',
   'a0000000-0000-0000-0000-00000000000b', 4, 1.000, '70000000-0000-0000-0000-000000000001', 5, 4.000, 0.200);
SELECT kt.expect_error($$UPDATE purch.purchase_order SET status = 'approved' WHERE id = 'b1100000-0000-0000-0000-000000000001'$$, '23514');   -- approved names who
SELECT kt.expect_error($$UPDATE purch.purchase_order SET status = 'received', approved_by = 'a5000000-0000-0000-0000-000000000002', approved_at = now()
  WHERE id = 'b1100000-0000-0000-0000-000000000001'$$, 'KT081');                                                                                     -- not straight to received
UPDATE purch.purchase_order SET status = 'approved', approved_by = 'a5000000-0000-0000-0000-000000000002', approved_at = now(),
       net_total = 14, tax_total = 0.7, gross_total = 14.7 WHERE id = 'b1100000-0000-0000-0000-000000000001';
SELECT kt.expect_error($$UPDATE purch.po_line SET qty = 20 WHERE id = 'b1200000-0000-0000-0000-000000000001'$$, 'KT081');                       -- frozen once approved
SELECT kt.expect_error($$UPDATE purch.purchase_order SET gross_total = 99, net_total = 99, tax_total = 0 WHERE id = 'b1100000-0000-0000-0000-000000000001'$$, 'KT081');
SELECT kt.expect_error($$UPDATE purch.purchase_order SET status = 'draft' WHERE id = 'b1100000-0000-0000-0000-000000000001'$$, 'KT081');
UPDATE purch.purchase_order SET status = 'sent', sent_at = now() WHERE id = 'b1100000-0000-0000-0000-000000000001';

-- ---------------------------------------------------------------- goods receipts never pass the order (16.2)
CREATE FUNCTION kt.grn(p_id uuid, p_line uuid, p_po_line uuid, p_qty numeric, p_price numeric DEFAULT 1.000,
                       p_branch uuid DEFAULT 'a0000000-0000-0000-0000-00000000000a', p_wh uuid DEFAULT 'a1000000-0000-0000-0000-00000000000a') RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO purch.goods_receipt (id, business_id, branch_id, warehouse_id, number, supplier_id, po_id, receipt_date, goods_value, created_by)
  VALUES (p_id, 'b0000000-0000-0000-0000-000000000001', p_branch, p_wh, 'GR-T-' || right(p_id::text, 12), 'af100000-0000-0000-0000-000000000001',
          'b1100000-0000-0000-0000-000000000001', DATE '2026-01-12', round(p_qty * p_price, 3), 'a5000000-0000-0000-0000-000000000001')
  ON CONFLICT DO NOTHING;
  INSERT INTO purch.receipt_line (id, receipt_id, line_no, po_line_id, variant_id, product_type, qty, unit_price, tax_code_id, goods_value, stock_value)
  VALUES (p_line, p_id, (SELECT coalesce(max(line_no), 0) + 1 FROM purch.receipt_line WHERE receipt_id = p_id), p_po_line,
          'a3000000-0000-0000-0000-000000000001', 'accessory', p_qty, p_price, '70000000-0000-0000-0000-000000000001', round(p_qty * p_price, 3), round(p_qty * p_price, 3));
END $$;
SELECT kt.grn('b1300000-0000-0000-0000-000000000001', 'b1400000-0000-0000-0000-000000000001', 'b1200000-0000-0000-0000-000000000001', 6);
SELECT kt.grn('b1300000-0000-0000-0000-000000000002', 'b1400000-0000-0000-0000-000000000002', 'b1200000-0000-0000-0000-000000000001', 4);
SELECT kt.expect_commit_error($$SELECT kt.grn('b1300000-0000-0000-0000-000000000003', 'b1400000-0000-0000-0000-000000000003', 'b1200000-0000-0000-0000-000000000001', 1)$$, 'KT082');
-- With a 25% allowance a little more may come.
UPDATE purch.policy SET over_receipt_pct = 25 WHERE business_id = 'b0000000-0000-0000-0000-000000000001';
SELECT kt.grn('b1300000-0000-0000-0000-000000000004', 'b1400000-0000-0000-0000-000000000004', 'b1200000-0000-0000-0000-000000000002', 5, 1.000,
              'a0000000-0000-0000-0000-00000000000b', 'a1000000-0000-0000-0000-00000000000b');
UPDATE purch.policy SET over_receipt_pct = 0 WHERE business_id = 'b0000000-0000-0000-0000-000000000001';

-- ---------------------------------------------------------------- goods received not invoiced as a ledger
CREATE FUNCTION kt.grni(p_source uuid, p_kind text, p_amount numeric, p_type text DEFAULT 'receipt_line') RETURNS void
LANGUAGE sql AS $$
  INSERT INTO purch.grni_entry (id, business_id, branch_id, supplier_id, source_type, source_id, kind, amount, doc_type, doc_id)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'af100000-0000-0000-0000-000000000001',
          p_type, p_source, p_kind, p_amount, 'test', gen_random_uuid())
$$;
SELECT kt.grni('b1400000-0000-0000-0000-000000000001', 'accrued', 6);
SELECT kt.grni('b1400000-0000-0000-0000-000000000001', 'invoiced', -4);
SELECT kt.expect_commit_error($$SELECT kt.grni('b1400000-0000-0000-0000-000000000001', 'invoiced', -2.001)$$, 'KT082');   -- past what was received
SELECT kt.expect_error($$SELECT kt.grni('b1400000-0000-0000-0000-000000000001', 'accrued', -1)$$, '23514');               -- an accrual is positive
SELECT kt.expect_error($$UPDATE purch.grni_entry SET amount = 1$$, 'KT001');
-- Outside work on a repair accrues by itself, and its reversal takes it back.
CREATE TEMP TABLE w11 AS SELECT kt.rjob_at('in_progress') AS j;
INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, cost, vendor, reference, created_by)
VALUES ('b1500000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j FROM w11),
        'external', 12.500, 'Board Doctor LLC', 'BD-2001', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.assert((SELECT sum(amount) FROM purch.grni_entry WHERE source_type = 'repair_line' AND source_id = 'b1500000-0000-0000-0000-000000000001') = 12.500,
                 'outside work accrues to goods received not invoiced');
INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, cost, vendor, return_of_id, created_by)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', (SELECT j FROM w11), 'external', -12.500,
        'Board Doctor LLC', 'b1500000-0000-0000-0000-000000000001', 'a5000000-0000-0000-0000-000000000001');
SELECT kt.assert((SELECT sum(amount) FROM purch.grni_entry WHERE source_type = 'repair_line' AND source_id = 'b1500000-0000-0000-0000-000000000001') = 0,
                 'its reversal takes it back');

-- ---------------------------------------------------------------- supplier invoices (16.2)
CREATE FUNCTION kt.inv(p_id uuid, p_no text) RETURNS void
LANGUAGE sql AS $$
  INSERT INTO purch.purchase_invoice (id, business_id, branch_id, number, supplier_id, supplier_invoice_no, invoice_date, due_date, created_by)
  VALUES (p_id, 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'PI-T-' || right(p_id::text, 12),
          'af100000-0000-0000-0000-000000000001', p_no, DATE '2026-01-15', DATE '2026-02-14', 'a5000000-0000-0000-0000-000000000001')
$$;
CREATE FUNCTION kt.inv_line(p_invoice uuid, p_no int, p_receipt_line uuid, p_qty numeric) RETURNS void
LANGUAGE sql AS $$
  INSERT INTO purch.invoice_line (id, invoice_id, line_no, kind, receipt_line_id, description, qty, unit_price, net_amount, tax_code_id, tax_rate, tax_amount,
                                  expected_amount, order_price, match_status)
  VALUES (gen_random_uuid(), p_invoice, p_no, 'receipt', p_receipt_line, 'USB-C cable', p_qty, 1.000, p_qty, '70000000-0000-0000-0000-000000000001', 5,
          round(p_qty * 0.05, 3), p_qty, 1.000, 'ok')
$$;
CREATE FUNCTION kt.post_inv(p_id uuid) RETURNS void
LANGUAGE sql AS $$
  UPDATE purch.purchase_invoice SET status = 'posted', journal_id = (SELECT id FROM acct.journal LIMIT 1), posted_at = now() WHERE id = p_id
$$;
SELECT kt.inv('b1600000-0000-0000-0000-000000000001', 'INV-778');
SELECT kt.expect_error($$SELECT kt.inv('b1600000-0000-0000-0000-000000000009', ' inv-778 ')$$, '23505');            -- the same supplier number again
SELECT kt.inv_line('b1600000-0000-0000-0000-000000000001', 1, 'b1400000-0000-0000-0000-000000000001', 6);
SELECT kt.post_inv('b1600000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$UPDATE purch.purchase_invoice SET note = 'x' WHERE id = 'b1600000-0000-0000-0000-000000000001'$$, 'KT083');
SELECT kt.expect_error($$UPDATE purch.invoice_line SET qty = 1 WHERE invoice_id = 'b1600000-0000-0000-0000-000000000001'$$, 'KT083');
-- A second invoice for the same receipt line cannot be posted: all 6 are invoiced.
SELECT kt.inv('b1600000-0000-0000-0000-000000000002', 'INV-779');
SELECT kt.inv_line('b1600000-0000-0000-0000-000000000002', 1, 'b1400000-0000-0000-0000-000000000001', 1);
SELECT kt.expect_commit_error($$SELECT kt.post_inv('b1600000-0000-0000-0000-000000000002')$$, 'KT082');
DELETE FROM purch.invoice_line WHERE invoice_id = 'b1600000-0000-0000-0000-000000000002';
UPDATE purch.purchase_invoice SET status = 'cancelled' WHERE id = 'b1600000-0000-0000-0000-000000000002';
SELECT kt.inv('b1600000-0000-0000-0000-000000000003', 'INV-779');                                                  -- a cancelled number may be booked again

-- ---------------------------------------------------------------- supplier returns: before the invoice out of what is open, after it on a debit note
INSERT INTO purch.supplier_return (id, business_id, branch_id, warehouse_id, number, supplier_id, return_date, reason, net_total, tax_total, stock_value, created_by)
VALUES ('b1700000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
        'DN-T-1', 'af100000-0000-0000-0000-000000000001', DATE '2026-01-16', 'Faulty', 1, 0.05, 1, 'a5000000-0000-0000-0000-000000000001');
SELECT kt.expect_commit_error($$INSERT INTO purch.supplier_return_line (id, return_id, line_no, receipt_line_id, qty, stock_value, invoiced, net_amount, tax_amount)
  VALUES (gen_random_uuid(), 'b1700000-0000-0000-0000-000000000001', 1, 'b1400000-0000-0000-0000-000000000001', 1, 1, false, 1, 0)$$, 'KT082');     -- all 6 invoiced: nothing open
SELECT kt.expect_commit_error($$INSERT INTO purch.supplier_return_line (id, return_id, line_no, receipt_line_id, qty, stock_value, invoiced, net_amount, tax_amount)
  VALUES (gen_random_uuid(), 'b1700000-0000-0000-0000-000000000001', 1, 'b1400000-0000-0000-0000-000000000001', 7, 7, true, 7, 0.35)$$, 'KT082');   -- more than received
INSERT INTO purch.supplier_return_line (id, return_id, line_no, receipt_line_id, qty, stock_value, invoiced, net_amount, tax_amount)
VALUES (gen_random_uuid(), 'b1700000-0000-0000-0000-000000000001', 1, 'b1400000-0000-0000-0000-000000000001', 1, 1, true, 1, 0.05);                 -- a debit note
INSERT INTO purch.supplier_return_line (id, return_id, line_no, receipt_line_id, qty, stock_value, invoiced, net_amount, tax_amount)
VALUES (gen_random_uuid(), 'b1700000-0000-0000-0000-000000000001', 2, 'b1400000-0000-0000-0000-000000000002', 4, 4, false, 4, 0);
SELECT kt.inv('b1600000-0000-0000-0000-000000000004', 'INV-780');
SELECT kt.inv_line('b1600000-0000-0000-0000-000000000004', 1, 'b1400000-0000-0000-0000-000000000002', 1);
SELECT kt.expect_commit_error($$SELECT kt.post_inv('b1600000-0000-0000-0000-000000000004')$$, 'KT082');                                             -- the 4 went back before an invoice
SELECT kt.expect_error($$INSERT INTO purch.supplier_return_line (id, return_id, line_no, receipt_line_id, qty, stock_value, invoiced, net_amount, tax_amount)
  VALUES (gen_random_uuid(), 'b1700000-0000-0000-0000-000000000001', 3, 'b1400000-0000-0000-0000-000000000004', 1, 1, false, 1, 0.05)$$, '23514');  -- no VAT before the invoice

-- ---------------------------------------------------------------- allocations (16.2)
CREATE FUNCTION kt.sl(p_id uuid, p_kind text, p_amount numeric, p_branch uuid DEFAULT 'a0000000-0000-0000-0000-00000000000a') RETURNS void
LANGUAGE sql AS $$
  INSERT INTO purch.supplier_ledger (id, business_id, branch_id, supplier_id, kind, amount, doc_date, due_date, number, source_doc_type, source_doc_id, created_by)
  VALUES (p_id, 'b0000000-0000-0000-0000-000000000001', p_branch, 'af100000-0000-0000-0000-000000000001', p_kind, p_amount, DATE '2026-01-15',
          DATE '2026-02-14', 'L-' || left(p_id::text, 8), 'test', p_id, 'a5000000-0000-0000-0000-000000000001')
$$;
CREATE FUNCTION kt.salloc(p_debit uuid, p_credit uuid, p_amount numeric) RETURNS void
LANGUAGE sql AS $$
  INSERT INTO purch.supplier_allocation (id, business_id, debit_entry_id, credit_entry_id, amount)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', p_debit, p_credit, p_amount)
$$;
SELECT kt.sl('b1800000-0000-0000-0000-000000000001', 'invoice', 63);
SELECT kt.sl('b1800000-0000-0000-0000-000000000002', 'payment', -50);
SELECT kt.sl('b1800000-0000-0000-0000-000000000003', 'invoice', 20, 'a0000000-0000-0000-0000-00000000000b');
SELECT kt.salloc('b1800000-0000-0000-0000-000000000001', 'b1800000-0000-0000-0000-000000000002', 40);
SELECT kt.expect_commit_error($$SELECT kt.salloc('b1800000-0000-0000-0000-000000000001', 'b1800000-0000-0000-0000-000000000002', 11)$$, 'KT084');  -- 10 left on the payment
SELECT kt.expect_commit_error($$SELECT kt.salloc('b1800000-0000-0000-0000-000000000003', 'b1800000-0000-0000-0000-000000000002', 5)$$, 'KT084');   -- another branch's invoice
SELECT kt.expect_commit_error($$SELECT kt.salloc('b1800000-0000-0000-0000-000000000002', 'b1800000-0000-0000-0000-000000000001', 5)$$, 'KT084');   -- the wrong way round
SELECT kt.expect_error($$UPDATE purch.supplier_allocation SET amount = 1$$, 'KT001');

-- ---------------------------------------------------------------- manual journals (17.6)
INSERT INTO acct.manual_journal (id, business_id, branch_id, number, journal_date, description, created_by) VALUES
  ('b1900000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'MJ-T-1', DATE '2026-01-20',
   'Accrued rent', 'a5000000-0000-0000-0000-000000000001');
INSERT INTO acct.manual_journal_line (id, manual_journal_id, line_no, account_id, branch_id, debit) VALUES
  (gen_random_uuid(), 'b1900000-0000-0000-0000-000000000001', 1, 'f0000000-0000-0000-0000-000000001100', 'a0000000-0000-0000-0000-00000000000a', 5);
SELECT kt.expect_error($$UPDATE acct.manual_journal SET approved_by = created_by WHERE id = 'b1900000-0000-0000-0000-000000000001'$$, '23514');   -- the second approver is someone else
UPDATE acct.manual_journal SET status = 'pending_approval' WHERE id = 'b1900000-0000-0000-0000-000000000001';
SELECT kt.expect_error($$INSERT INTO acct.manual_journal_line (id, manual_journal_id, line_no, account_id, branch_id, credit) VALUES
  (gen_random_uuid(), 'b1900000-0000-0000-0000-000000000001', 2, 'f0000000-0000-0000-0000-000000004100', 'a0000000-0000-0000-0000-00000000000a', 5)$$, 'KT080');
SELECT kt.expect_error($$UPDATE acct.manual_journal SET status = 'posted' WHERE id = 'b1900000-0000-0000-0000-000000000001'$$, '23514');           -- posted means a journal

-- ---------------------------------------------------------------- a cost corrected on stock on hand (16.3)
SELECT kt.assert((kt.move(5, 2.000, 'opening', 'a3000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', 'a1000000-0000-0000-0000-00000000000b')).value = 10, 'Sohar has stock');
CREATE TEMP TABLE before11 AS SELECT qty, value FROM inv.stock_balance WHERE branch_id = 'a0000000-0000-0000-0000-00000000000b' AND variant_id = 'a3000000-0000-0000-0000-000000000001';
INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, value, source_doc_type, source_doc_id)
VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', 'a1000000-0000-0000-0000-00000000000b',
        'a3000000-0000-0000-0000-000000000001', 'cost_adjustment', 0, 1.500, 'purchase_invoice', gen_random_uuid());
SELECT kt.assert((SELECT b.qty = x.qty AND b.value = x.value + 1.500 AND b.avg_cost = round(b.value / b.qty, 6)
                    FROM inv.stock_balance b, before11 x WHERE b.branch_id = 'a0000000-0000-0000-0000-00000000000b' AND b.variant_id = 'a3000000-0000-0000-0000-000000000001'),
                 'the correction moves the value and the average, not the quantity');
SELECT kt.expect_error($$INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, value, source_doc_type, source_doc_id)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', 'a1000000-0000-0000-0000-00000000000b',
          'a3000000-0000-0000-0000-000000000001', 'cost_adjustment', 1, 1, 'test', gen_random_uuid())$$, '23514');                 -- quantity 0 only
SELECT kt.expect_error($$INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, value, source_doc_type, source_doc_id)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', 'a1000000-0000-0000-0000-00000000000b',
          'a3000000-0000-0000-0000-000000000001', 'cost_adjustment', 0, -9999, 'test', gen_random_uuid())$$, 'KT010');            -- never below zero

-- ---------------------------------------------------------------- a filed VAT return is frozen (17.4)
INSERT INTO tax.vat_return (id, business_id, number, period_from, period_to, boxes, output_vat, input_vat, net_vat, created_by)
VALUES ('b2000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'VR-T-1', DATE '2026-01-01', DATE '2026-03-31', '{}'::jsonb, 10, 4, 6,
        'a5000000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$UPDATE tax.vat_return SET status = 'filed' WHERE id = 'b2000000-0000-0000-0000-000000000001'$$, '23514');                   -- filing names who and posts
UPDATE tax.vat_return SET status = 'filed', filed_by = 'a5000000-0000-0000-0000-000000000001', filed_at = now(), settlement_journal_id = (SELECT id FROM acct.journal LIMIT 1)
 WHERE id = 'b2000000-0000-0000-0000-000000000001';
SELECT kt.expect_error($$UPDATE tax.vat_return SET net_vat = 1 WHERE id = 'b2000000-0000-0000-0000-000000000001'$$, 'KT085');
SELECT kt.expect_error($$INSERT INTO tax.vat_return (id, business_id, number, period_from, period_to, boxes, output_vat, input_vat, net_vat, created_by)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'VR-T-2', DATE '2026-01-01', DATE '2026-03-31', '{}'::jsonb, 0, 0, 0,
          'a5000000-0000-0000-0000-000000000001')$$, '23505');                                                                                       -- one return per period

-- ---------------------------------------------------------------- the application only adds to the new ledgers
SET ROLE kite_app;
SET kite.all_branches = 'on';
SELECT kt.expect_error($$UPDATE purch.goods_receipt SET note = 'x'$$, '42501');
SELECT kt.expect_error($$UPDATE purch.supplier_payment SET amount = 1$$, '42501');
SELECT kt.expect_error($$DELETE FROM purch.purchase_invoice$$, '42501');
SELECT kt.expect_error($$UPDATE acct.fiscal_period SET ends_on = DATE '2026-01-30'$$, '42501');                                                     -- only the status moves
RESET ROLE;
-- A receipt, return or payment gets its journal linked once and nothing else changes (column grant plus purch.journal_once).
SELECT kt.expect_error($$UPDATE purch.goods_receipt SET journal_id = (SELECT id FROM acct.journal LIMIT 1), goods_value = goods_value + 1$$, 'KT082');
UPDATE purch.goods_receipt SET journal_id = (SELECT id FROM acct.journal LIMIT 1) WHERE journal_id IS NULL;
SELECT kt.expect_error($$UPDATE purch.goods_receipt SET journal_id = (SELECT id FROM acct.journal ORDER BY id DESC LIMIT 1)$$, 'KT082');
-- A nil VAT return files without a settlement journal; a draft has none.
INSERT INTO tax.vat_return (id, business_id, number, period_from, period_to, boxes, output_vat, input_vat, net_vat, created_by)
VALUES ('b2000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'VR-T-3', DATE '2026-04-01', DATE '2026-06-30', '{}'::jsonb, 0, 0, 0,
        'a5000000-0000-0000-0000-000000000001');
SELECT kt.expect_error($$UPDATE tax.vat_return SET settlement_journal_id = (SELECT id FROM acct.journal LIMIT 1) WHERE id = 'b2000000-0000-0000-0000-000000000002'$$, '23514');
UPDATE tax.vat_return SET status = 'filed', filed_by = 'a5000000-0000-0000-0000-000000000001', filed_at = now() WHERE id = 'b2000000-0000-0000-0000-000000000002';
SELECT kt.assert((SELECT status = 'filed' AND settlement_journal_id IS NULL FROM tax.vat_return WHERE id = 'b2000000-0000-0000-0000-000000000002'), 'a nil return is filed');
