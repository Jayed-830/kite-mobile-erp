-- Test fixture and helpers (runs as superuser). Fixed UUIDs keep tests readable.
--   business  b0000000-...-01
--   branch A  a0000000-...-0a   branch B  a0000000-...-0b

CREATE SCHEMA kt;
GRANT USAGE ON SCHEMA kt TO kite_app, kite_migrator;

-- Fails the test file with a clear message.
CREATE FUNCTION kt.assert(p_ok boolean, p_msg text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF p_ok IS NOT TRUE THEN RAISE EXCEPTION 'ASSERTION FAILED: %', p_msg USING ERRCODE = 'KTF01'; END IF;
END $$;

-- Runs one statement and requires it to fail with the given SQLSTATE.
CREATE FUNCTION kt.expect_error(p_sql text, p_state text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  BEGIN
    EXECUTE p_sql;
  EXCEPTION WHEN OTHERS THEN
    IF SQLSTATE = p_state THEN RETURN; END IF;
    RAISE EXCEPTION 'expected SQLSTATE % but got % (%) for: %', p_state, SQLSTATE, SQLERRM, p_sql USING ERRCODE = 'KTF01';
  END;
  RAISE EXCEPTION 'expected SQLSTATE % but the statement succeeded: %', p_state, p_sql USING ERRCODE = 'KTF01';
END $$;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA kt TO kite_app, kite_migrator;

INSERT INTO core.business (id, legal_name_en, legal_name_ar, vat_number)
VALUES ('b0000000-0000-0000-0000-000000000001', 'Test Mobiles LLC', 'شركة اختبار الهواتف', 'OM1100000000');

INSERT INTO core.branch (id, business_id, code, name_en, name_ar, is_head_office) VALUES
  ('a0000000-0000-0000-0000-00000000000a', 'b0000000-0000-0000-0000-000000000001', 'B01', 'Muscat', 'مسقط', true),
  ('a0000000-0000-0000-0000-00000000000b', 'b0000000-0000-0000-0000-000000000001', 'B02', 'Sohar',  'صحار', false);

INSERT INTO core.server (id, business_id, branch_id, role, name, is_self)
VALUES ('c0000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001',
        'a0000000-0000-0000-0000-00000000000a', 'combined', 'HQ server', true);

INSERT INTO core.terminal (id, business_id, branch_id, code, name, kind) VALUES
  ('d0000000-0000-0000-0000-00000000000a', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'T01', 'Till 1', 'windows'),
  ('d0000000-0000-0000-0000-00000000000b', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', 'T01', 'Till 1', 'windows');

-- Fiscal periods: business-wide Jan 2026 open, Dec 2025 closed, Feb 2026 soft-closed.
INSERT INTO acct.fiscal_period (id, business_id, branch_id, fiscal_year, period_no, starts_on, ends_on, status) VALUES
  ('e0000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', NULL, 2026, 1, '2026-01-01', '2026-01-31', 'open'),
  ('e0000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', NULL, 2025, 12, '2025-12-01', '2025-12-31', 'closed'),
  ('e0000000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000001', NULL, 2026, 2, '2026-02-01', '2026-02-28', 'soft_closed');

INSERT INTO acct.account (id, business_id, code, name_en, name_ar, type) VALUES
  ('f0000000-0000-0000-0000-000000001100', 'b0000000-0000-0000-0000-000000000001', '1100', 'Cash', 'النقدية', 'asset'),
  ('f0000000-0000-0000-0000-000000004100', 'b0000000-0000-0000-0000-000000000001', '4100', 'Device sales', 'مبيعات الأجهزة', 'revenue'),
  ('f0000000-0000-0000-0000-000000002200', 'b0000000-0000-0000-0000-000000000001', '2200', 'VAT output', 'ضريبة المخرجات', 'liability');

INSERT INTO tax.tax_code (id, business_id, code, name_en, name_ar, kind)
VALUES ('70000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'VAT-STD', 'Standard VAT', 'ضريبة القيمة المضافة الأساسية', 'standard');
INSERT INTO tax.tax_rate (id, tax_code_id, rate_percent, effective_from)
VALUES ('71000000-0000-0000-0000-000000000001', '70000000-0000-0000-0000-000000000001', 5.0, '2021-04-16');

INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year)
VALUES ('90000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001',
        'a0000000-0000-0000-0000-00000000000a', 'journal', 'JV-B01-2026-', 2026);
