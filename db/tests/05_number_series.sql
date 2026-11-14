-- Document numbers are sequential and a rolled-back document returns its number (9.4 rule 7).
SET ROLE kite_app;
SET kite.all_branches = 'on';
DO $$
DECLARE n1 text; n2 text; n3 text;
BEGIN
  n1 := core.next_document_number('b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', NULL, 'journal', 2026);
  BEGIN
    n2 := core.next_document_number('b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', NULL, 'journal', 2026);
    RAISE EXCEPTION 'simulated failure after taking a number';
  EXCEPTION WHEN raise_exception THEN NULL;
  END;
  n3 := core.next_document_number('b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', NULL, 'journal', 2026);
  PERFORM kt.assert(n1 = 'JV-B01-2026-000001', 'first number is JV-B01-2026-000001, got ' || n1);
  PERFORM kt.assert(n3 = 'JV-B01-2026-000002', 'number returned after rollback, got ' || n3);
END $$;

-- Unknown series fails clearly.
SELECT kt.expect_error($$SELECT core.next_document_number('b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', NULL, 'sale', 2026)$$, 'P0002');

-- A branch user may not draw numbers from another branch's series (RLS hides the row).
SET kite.all_branches = '';
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000b}';
SELECT kt.expect_error($$SELECT core.next_document_number('b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', NULL, 'journal', 2026)$$, 'P0002');
RESET ROLE;
