-- The application role cannot rewrite history or change protected catalogues (18.5).
SET ROLE kite_app;
SET kite.all_branches = 'on';

-- A valid journal to try to tamper with.
BEGIN;
INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind)
VALUES ('10000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
        'JV-T01-1', '2026-01-10', 'e0000000-0000-0000-0000-000000000001', 'test', '10000000-0000-0000-0000-0000000000aa', 'sale');
INSERT INTO acct.journal_line (id, journal_id, line_no, account_id, branch_id, debit, credit) VALUES
  ('11000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001', 1, 'f0000000-0000-0000-0000-000000001100', 'a0000000-0000-0000-0000-00000000000a', 10.500, 0),
  ('11000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000001', 2, 'f0000000-0000-0000-0000-000000004100', 'a0000000-0000-0000-0000-00000000000a', 0, 10.000),
  ('11000000-0000-0000-0000-000000000003', '10000000-0000-0000-0000-000000000001', 3, 'f0000000-0000-0000-0000-000000002200', 'a0000000-0000-0000-0000-00000000000a', 0, 0.500);
COMMIT;

SELECT kt.expect_error($$UPDATE acct.journal SET description = 'x' WHERE id = '10000000-0000-0000-0000-000000000001'$$, '42501');
SELECT kt.expect_error($$DELETE FROM acct.journal_line WHERE journal_id = '10000000-0000-0000-0000-000000000001'$$, '42501');
SELECT kt.expect_error($$TRUNCATE acct.journal_line$$, '42501');
SELECT kt.expect_error($$DELETE FROM audit.audit_log$$, '42501');
SELECT kt.expect_error($$INSERT INTO iam.permission VALUES ('X.Y', 'core', 'x', 'x')$$, '42501');
SELECT kt.expect_error($$DELETE FROM core.branch WHERE code = 'B02'$$, '42501');
SELECT kt.expect_error($$DELETE FROM iam."user"$$, '42501');
SELECT kt.expect_error($$UPDATE sync.outbox SET payload = '{}'$$, '42501');
SELECT kt.expect_error($$CREATE TABLE core.sneaky (id int)$$, '42501');
SELECT kt.expect_error($$CREATE TABLE public.sneaky (id int)$$, '42501');

-- What the app may do: read and append.
SELECT kt.assert((SELECT count(*) FROM acct.journal_line WHERE journal_id = '10000000-0000-0000-0000-000000000001') = 3, 'app can read journal lines');
RESET ROLE;
