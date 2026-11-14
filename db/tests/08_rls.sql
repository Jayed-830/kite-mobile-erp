-- Row-level security: a session sees and writes only its branches (18.3).
SET ROLE kite_app;

-- No scope set: nothing branch-owned is visible.
SELECT kt.assert((SELECT count(*) FROM core.terminal) = 0, 'no scope: no terminals visible');
SELECT kt.assert((SELECT count(*) FROM acct.journal) = 0, 'no scope: no journals visible');

-- Branch A scope.
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000a}';
SELECT kt.assert((SELECT count(*) FROM core.terminal) = 1, 'branch A scope: 1 terminal');
SELECT kt.assert((SELECT bool_and(branch_id = 'a0000000-0000-0000-0000-00000000000a') FROM core.terminal), 'only branch A terminal visible');

-- Writing into branch B from branch A scope is refused.
SELECT kt.expect_error($$INSERT INTO core.warehouse (id, business_id, branch_id, code, name_en, name_ar, kind)
  VALUES ('60000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', 'W1', 'Store', 'مخزن', 'shop')$$, '42501');
-- Moving a branch A row to branch B is refused too.
SELECT kt.expect_error($$UPDATE core.terminal SET branch_id = 'a0000000-0000-0000-0000-00000000000b' WHERE code = 'T01'$$, '42501');
-- Business-wide rows (no branch) cannot be written from branch scope.
SELECT kt.expect_error($$INSERT INTO acct.fiscal_period (id, business_id, branch_id, fiscal_year, period_no, starts_on, ends_on)
  VALUES ('60000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', NULL, 2026, 3, '2026-03-01', '2026-03-31')$$, '42501');
-- Writing into the own branch works.
INSERT INTO core.warehouse (id, business_id, branch_id, code, name_en, name_ar, kind)
VALUES ('60000000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'W1', 'Shop floor', 'صالة العرض', 'shop');

-- Both branches.
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000a,a0000000-0000-0000-0000-00000000000b}';
SELECT kt.assert((SELECT count(*) FROM core.terminal) = 2, 'two-branch scope: 2 terminals');

-- All-branches (owner / system job).
RESET kite.branch_ids;
SET kite.all_branches = 'on';
SELECT kt.assert((SELECT count(*) FROM core.terminal) = 2, 'all branches: 2 terminals');
RESET ROLE;
