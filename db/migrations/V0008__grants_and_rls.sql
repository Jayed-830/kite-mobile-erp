-- V0008: least-privilege grants and row-level security (blueprint 18.3, 18.5).

-- 1. Ledgers: the application may only INSERT and SELECT.
REVOKE UPDATE, DELETE, TRUNCATE ON acct.journal, acct.journal_line, audit.audit_log FROM kite_app;

-- 2. Outbox: insert, read, and update delivery bookkeeping columns only.
REVOKE UPDATE, DELETE, TRUNCATE ON sync.outbox FROM kite_app;
GRANT UPDATE (status, attempts, last_attempt_at, last_error, acked_at) ON sync.outbox TO kite_app;

-- 3. Master data is deactivated, never deleted (9.2).
REVOKE DELETE, TRUNCATE ON
  core.business, core.branch, core.server, core.terminal, core.warehouse, core.number_series,
  core.setting, core.module_setting,
  iam."user", iam.role, iam.permission,
  tax.tax_code, tax.tax_rate,
  acct.account, acct.fiscal_period, acct.posting_rule
FROM kite_app;

-- 4. The permission catalogue is owned by migrations.
REVOKE INSERT, UPDATE ON iam.permission FROM kite_app;

-- 5. Functions are executable by PUBLIC by default in PostgreSQL; tighten the sensitive ones.
REVOKE EXECUTE ON FUNCTION audit.verify_chain() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION audit.verify_chain() TO kite_app;
REVOKE EXECUTE ON FUNCTION core.next_document_number(uuid, uuid, uuid, text, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.next_document_number(uuid, uuid, uuid, text, integer) TO kite_app;

-- 6. Row-level security on branch-owned tables. The owner (kite_migrator) is not forced,
--    so migrations and SECURITY DEFINER functions see everything; kite_app and kite_ro
--    see only the branches set in the transaction (core.branch_visible).
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['core.terminal','core.warehouse','core.number_series',
                           'acct.fiscal_period','acct.journal','acct.journal_line'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY branch_read ON %s FOR SELECT USING (core.branch_visible(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_insert ON %s FOR INSERT WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_update ON %s FOR UPDATE USING (core.branch_visible(branch_id)) WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_delete ON %s FOR DELETE USING (core.branch_writable(branch_id))', t);
  END LOOP;
END
$$;

-- Audit rows: anyone may append (an audit write must never be blocked by scope);
-- reading is limited to the branches in scope.
ALTER TABLE audit.audit_log ENABLE ROW LEVEL SECURITY;
CREATE POLICY audit_read ON audit.audit_log FOR SELECT USING (core.branch_visible(branch_id));
CREATE POLICY audit_append ON audit.audit_log FOR INSERT WITH CHECK (true);
