-- Posting only into an open period that covers the journal date (17.5).
SET ROLE kite_app;
SET kite.all_branches = 'on';

-- Closed period.
SELECT kt.expect_error($$INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind)
  VALUES ('30000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
          'JV-T04-1', '2025-12-15', 'e0000000-0000-0000-0000-000000000002', 'test', '30000000-0000-0000-0000-0000000000aa', 'sale')$$, 'KT003');

-- Date outside the referenced period.
SELECT kt.expect_error($$INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind)
  VALUES ('30000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
          'JV-T04-2', '2026-02-15', 'e0000000-0000-0000-0000-000000000001', 'test', '30000000-0000-0000-0000-0000000000bb', 'sale')$$, 'KT003');

-- Soft-closed period without the accountant's flag.
SELECT kt.expect_error($$INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind)
  VALUES ('30000000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
          'JV-T04-3', '2026-02-15', 'e0000000-0000-0000-0000-000000000003', 'test', '30000000-0000-0000-0000-0000000000cc', 'sale')$$, 'KT003');

-- Soft-closed period with the flag set in that transaction: accepted.
BEGIN;
SET LOCAL kite.allow_soft_closed = 'on';
INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind)
VALUES ('30000000-0000-0000-0000-000000000004', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
        'JV-T04-4', '2026-02-15', 'e0000000-0000-0000-0000-000000000003', 'test', '30000000-0000-0000-0000-0000000000dd', 'adjustment');
INSERT INTO acct.journal_line (id, journal_id, line_no, account_id, debit, credit) VALUES
  ('31000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000004', 1, 'f0000000-0000-0000-0000-000000001100', 1.000, 0),
  ('31000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000004', 2, 'f0000000-0000-0000-0000-000000004100', 0, 1.000);
COMMIT;
SELECT kt.assert(EXISTS (SELECT 1 FROM acct.journal WHERE id = '30000000-0000-0000-0000-000000000004'), 'soft-closed posting with flag committed');
RESET ROLE;
