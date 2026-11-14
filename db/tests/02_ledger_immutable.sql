-- Even the owner role cannot change or remove ledger rows: the triggers stop it (KT001).
SET ROLE kite_migrator;
SELECT kt.expect_error($$UPDATE acct.journal SET description = 'edited' WHERE id = '10000000-0000-0000-0000-000000000001'$$, 'KT001');
SELECT kt.expect_error($$UPDATE acct.journal_line SET debit = 99 WHERE id = '11000000-0000-0000-0000-000000000001'$$, 'KT001');
SELECT kt.expect_error($$DELETE FROM acct.journal_line WHERE id = '11000000-0000-0000-0000-000000000003'$$, 'KT001');
SELECT kt.expect_error($$DELETE FROM acct.journal WHERE id = '10000000-0000-0000-0000-000000000001'$$, 'KT001');
SELECT kt.expect_error($$TRUNCATE acct.journal_line$$, 'KT001');
SELECT kt.expect_error($$TRUNCATE audit.audit_log$$, 'KT001');
SELECT kt.expect_error($$TRUNCATE sync.outbox$$, 'KT001');
RESET ROLE;
