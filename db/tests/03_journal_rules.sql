-- Journals must balance, have two or more lines, post once per document, and reversals
-- must mirror the original (9.4 rules 2-3, 17.6).
SET ROLE kite_app;
SET kite.all_branches = 'on';

-- Unbalanced: caught when the deferred check fires.
DO $$ BEGIN
  BEGIN
    INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind)
    VALUES ('20000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
            'JV-T03-1', '2026-01-11', 'e0000000-0000-0000-0000-000000000001', 'test', '20000000-0000-0000-0000-0000000000aa', 'sale');
    INSERT INTO acct.journal_line (id, journal_id, line_no, account_id, debit, credit) VALUES
      ('21000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', 1, 'f0000000-0000-0000-0000-000000001100', 10.000, 0),
      ('21000000-0000-0000-0000-000000000002', '20000000-0000-0000-0000-000000000001', 2, 'f0000000-0000-0000-0000-000000004100', 0, 9.999);
    SET CONSTRAINTS ALL IMMEDIATE;
    RAISE EXCEPTION 'unbalanced journal was accepted' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT002' THEN NULL;
  END;
END $$;

-- One line only.
DO $$ BEGIN
  BEGIN
    INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind)
    VALUES ('20000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
            'JV-T03-2', '2026-01-11', 'e0000000-0000-0000-0000-000000000001', 'test', '20000000-0000-0000-0000-0000000000bb', 'sale');
    INSERT INTO acct.journal_line (id, journal_id, line_no, account_id, debit, credit) VALUES
      ('21000000-0000-0000-0000-000000000003', '20000000-0000-0000-0000-000000000002', 1, 'f0000000-0000-0000-0000-000000001100', 5.000, 0);
    SET CONSTRAINTS ALL IMMEDIATE;
    RAISE EXCEPTION 'single-line journal was accepted' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT002' THEN NULL;
  END;
END $$;

-- A header with no lines at all.
DO $$ BEGIN
  BEGIN
    INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind)
    VALUES ('20000000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
            'JV-T03-3', '2026-01-11', 'e0000000-0000-0000-0000-000000000001', 'test', '20000000-0000-0000-0000-0000000000cc', 'sale');
    SET CONSTRAINTS ALL IMMEDIATE;
    RAISE EXCEPTION 'empty journal was accepted' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT002' THEN NULL;
  END;
END $$;

-- Line with both debit and credit, or neither, is rejected by check constraints.
SELECT kt.expect_error($$INSERT INTO acct.journal_line (id, journal_id, line_no, account_id, debit, credit)
  VALUES ('21000000-0000-0000-0000-0000000000f1', '10000000-0000-0000-0000-000000000001', 9, 'f0000000-0000-0000-0000-000000001100', 1, 1)$$, '23514');
SELECT kt.expect_error($$INSERT INTO acct.journal_line (id, journal_id, line_no, account_id, debit, credit)
  VALUES ('21000000-0000-0000-0000-0000000000f2', '10000000-0000-0000-0000-000000000001', 9, 'f0000000-0000-0000-0000-000000001100', 0, 0)$$, '23514');

-- Same document, same posting kind: the unique key stops a double posting.
SELECT kt.expect_error($$INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind)
  VALUES ('20000000-0000-0000-0000-000000000004', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
          'JV-T03-4', '2026-01-11', 'e0000000-0000-0000-0000-000000000001', 'test', '10000000-0000-0000-0000-0000000000aa', 'sale')$$, '23505');

-- A correct reversal of journal 1 commits.
BEGIN;
INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind, reversal_of_id)
VALUES ('20000000-0000-0000-0000-000000000005', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
        'JV-T03-5', '2026-01-12', 'e0000000-0000-0000-0000-000000000001', 'test', '10000000-0000-0000-0000-0000000000aa', 'sale.reversal',
        '10000000-0000-0000-0000-000000000001');
INSERT INTO acct.journal_line (id, journal_id, line_no, account_id, debit, credit) VALUES
  ('21000000-0000-0000-0000-000000000011', '20000000-0000-0000-0000-000000000005', 1, 'f0000000-0000-0000-0000-000000001100', 0, 10.500),
  ('21000000-0000-0000-0000-000000000012', '20000000-0000-0000-0000-000000000005', 2, 'f0000000-0000-0000-0000-000000004100', 10.000, 0),
  ('21000000-0000-0000-0000-000000000013', '20000000-0000-0000-0000-000000000005', 3, 'f0000000-0000-0000-0000-000000002200', 0.500, 0);
COMMIT;

-- Reversing the same journal again is impossible (reversal_of_id is unique).
SELECT kt.expect_error($$INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind, reversal_of_id)
  VALUES ('20000000-0000-0000-0000-000000000006', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
          'JV-T03-6', '2026-01-12', 'e0000000-0000-0000-0000-000000000001', 'test', '10000000-0000-0000-0000-0000000000aa', 'sale.reversal2',
          '10000000-0000-0000-0000-000000000001')$$, '23505');

-- A "reversal" that does not mirror the original is rejected at commit.
BEGIN;
INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind)
VALUES ('20000000-0000-0000-0000-000000000007', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
        'JV-T03-7', '2026-01-13', 'e0000000-0000-0000-0000-000000000001', 'test', '20000000-0000-0000-0000-0000000000dd', 'sale');
INSERT INTO acct.journal_line (id, journal_id, line_no, account_id, debit, credit) VALUES
  ('21000000-0000-0000-0000-000000000021', '20000000-0000-0000-0000-000000000007', 1, 'f0000000-0000-0000-0000-000000001100', 4.200, 0),
  ('21000000-0000-0000-0000-000000000022', '20000000-0000-0000-0000-000000000007', 2, 'f0000000-0000-0000-0000-000000004100', 0, 4.200);
COMMIT;
DO $$ BEGIN
  BEGIN
    INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id, posting_kind, reversal_of_id)
    VALUES ('20000000-0000-0000-0000-000000000008', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a',
            'JV-T03-8', '2026-01-13', 'e0000000-0000-0000-0000-000000000001', 'test', '20000000-0000-0000-0000-0000000000dd', 'sale.reversal',
            '20000000-0000-0000-0000-000000000007');
    INSERT INTO acct.journal_line (id, journal_id, line_no, account_id, debit, credit) VALUES
      ('21000000-0000-0000-0000-000000000023', '20000000-0000-0000-0000-000000000008', 1, 'f0000000-0000-0000-0000-000000001100', 0, 4.000),
      ('21000000-0000-0000-0000-000000000024', '20000000-0000-0000-0000-000000000008', 2, 'f0000000-0000-0000-0000-000000004100', 4.000, 0);
    SET CONSTRAINTS ALL IMMEDIATE;
    RAISE EXCEPTION 'non-mirroring reversal was accepted' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT002' THEN NULL;
  END;
END $$;
RESET ROLE;
