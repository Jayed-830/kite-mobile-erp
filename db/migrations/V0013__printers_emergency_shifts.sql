-- V0013: Phase 4 - network printers for the print relay, emergency shifts beside a normal shift, and the
-- stock-exception follow-up (blueprint 6.4, 10.5).

-- An emergency shift is recorded on the terminal while the server cannot be reached; it is uploaded later and may
-- overlap a normal shift that was open on the same terminal when the connection dropped.
DROP INDEX sales.shift_one_open_per_terminal;
CREATE UNIQUE INDEX shift_one_open_per_terminal ON sales.shift (terminal_id) WHERE status = 'open' AND NOT is_emergency;

-- Receipt printers reached over the network (raw TCP, usually port 9100). Browser terminals print through the
-- server; the Windows app prints directly to a Windows printer or the same network address.
CREATE TABLE sales.printer (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  name             text NOT NULL CHECK (length(trim(name)) > 0),
  host             text NOT NULL CHECK (host ~ '^[A-Za-z0-9.-]{1,253}$'),
  port             integer NOT NULL DEFAULT 9100 CHECK (port BETWEEN 1 AND 65535),
  paper_width_mm   smallint NOT NULL DEFAULT 80 CHECK (paper_width_mm IN (58, 80)),
  has_drawer       boolean NOT NULL DEFAULT true,
  status           text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  last_result      text,
  last_used_at     timestamptz,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid,
  updated_at       timestamptz NOT NULL DEFAULT now(),
  updated_by       uuid,
  version          integer NOT NULL DEFAULT 1,
  UNIQUE (branch_id, name)
);
CREATE TRIGGER printer_touch BEFORE UPDATE ON sales.printer FOR EACH ROW EXECUTE FUNCTION core.touch_row();
ALTER TABLE sales.printer ENABLE ROW LEVEL SECURITY;
CREATE POLICY branch_read ON sales.printer FOR SELECT USING (core.branch_visible(branch_id));
CREATE POLICY branch_insert ON sales.printer FOR INSERT WITH CHECK (core.branch_writable(branch_id));
CREATE POLICY branch_update ON sales.printer FOR UPDATE USING (core.branch_visible(branch_id)) WITH CHECK (core.branch_writable(branch_id));
REVOKE DELETE, TRUNCATE ON sales.printer FROM kite_app;

-- A terminal may name its default printer.
ALTER TABLE core.terminal ADD COLUMN printer_id uuid REFERENCES sales.printer(id);

-- Stock exceptions can only be resolved, never removed.
CREATE OR REPLACE FUNCTION inv.stock_exception_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = 'resolved' OR NEW.variant_id <> OLD.variant_id OR NEW.qty_after <> OLD.qty_after
     OR NEW.source_doc_id <> OLD.source_doc_id THEN
    RAISE EXCEPTION 'KITE: a stock exception can only be resolved once' USING ERRCODE = 'KT001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER stock_exception_resolve_only BEFORE UPDATE ON inv.stock_exception FOR EACH ROW EXECUTE FUNCTION inv.stock_exception_guard();

-- A stock count reads the system quantity and writes the difference in one step: the balance row is locked so that a
-- sale at the same moment cannot slip in between. kite_app cannot lock the projection itself (no UPDATE grant).
CREATE FUNCTION inv.lock_balance(p_warehouse_id uuid, p_variant_id uuid) RETURNS numeric
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE q numeric;
BEGIN
  IF NOT EXISTS (SELECT 1 FROM core.warehouse w WHERE w.id = p_warehouse_id AND core.branch_writable(w.branch_id)) THEN
    RAISE EXCEPTION 'KITE: warehouse % is outside your branches', p_warehouse_id USING ERRCODE = '42501';
  END IF;
  SELECT qty INTO q FROM inv.stock_balance WHERE warehouse_id = p_warehouse_id AND variant_id = p_variant_id FOR UPDATE;
  RETURN coalesce(q, 0);
END $$;
REVOKE EXECUTE ON FUNCTION inv.lock_balance(uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION inv.lock_balance(uuid, uuid) TO kite_app;
