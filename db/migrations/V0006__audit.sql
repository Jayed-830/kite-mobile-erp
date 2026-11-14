-- V0006: hash-chained audit log (blueprint 18.6).
-- The chain is built inside the database, so every insert is linked no matter which
-- code path wrote it. seq is gap-free per server: it is assigned under a transaction
-- advisory lock, and a rolled-back insert releases its number.

CREATE TABLE audit.audit_log (
  id               uuid PRIMARY KEY,
  seq              bigint NOT NULL UNIQUE,
  occurred_at      timestamptz NOT NULL DEFAULT now(),
  business_id      uuid,
  branch_id        uuid,
  user_id          uuid,
  terminal_id      uuid,
  action           text NOT NULL CHECK (action ~ '^[a-z][a-z0-9_.]{2,80}$'),
  entity_type      text,
  entity_id        uuid,
  before           jsonb,
  after            jsonb,
  reason           text,
  ip               inet,
  origin_server_id uuid,
  prev_hash        bytea NOT NULL CHECK (length(prev_hash) = 32),
  hash             bytea NOT NULL CHECK (length(hash) = 32)
);
CREATE INDEX audit_log_entity ON audit.audit_log (entity_type, entity_id);
CREATE INDEX audit_log_user ON audit.audit_log (user_id, occurred_at);

-- Canonical text that is hashed. jsonb::text is normalised by PostgreSQL, and the time is
-- printed in UTC, so the result does not depend on session settings.
CREATE OR REPLACE FUNCTION audit.row_digest(
  p_seq bigint, p_id uuid, p_occurred_at timestamptz, p_user_id uuid, p_branch_id uuid,
  p_action text, p_entity_type text, p_entity_id uuid, p_before jsonb, p_after jsonb,
  p_reason text, p_prev_hash bytea)
RETURNS bytea
LANGUAGE sql IMMUTABLE AS $$
  SELECT sha256(convert_to(concat_ws('|',
    p_seq::text,
    p_id::text,
    to_char(p_occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
    coalesce(p_user_id::text, ''),
    coalesce(p_branch_id::text, ''),
    p_action,
    coalesce(p_entity_type, ''),
    coalesce(p_entity_id::text, ''),
    coalesce(p_before::text, ''),
    coalesce(p_after::text, ''),
    coalesce(p_reason, ''),
    encode(p_prev_hash, 'hex')), 'UTF8'))
$$;

-- SECURITY DEFINER: the trigger must see the true last row even when the inserting
-- session is limited by row-level security to some branches.
CREATE OR REPLACE FUNCTION audit.chain_row() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, audit, core AS $$
DECLARE
  v_last_seq  bigint;
  v_last_hash bytea;
BEGIN
  -- Serialise chain appends for this database.
  PERFORM pg_advisory_xact_lock(hashtextextended('kite.audit.chain', 0));
  SELECT seq, hash INTO v_last_seq, v_last_hash FROM audit.audit_log ORDER BY seq DESC LIMIT 1;
  NEW.seq := coalesce(v_last_seq, 0) + 1;
  NEW.prev_hash := coalesce(v_last_hash, '\x0000000000000000000000000000000000000000000000000000000000000000'::bytea);
  NEW.occurred_at := coalesce(NEW.occurred_at, now());
  NEW.hash := audit.row_digest(NEW.seq, NEW.id, NEW.occurred_at, NEW.user_id, NEW.branch_id,
                               NEW.action, NEW.entity_type, NEW.entity_id, NEW.before, NEW.after,
                               NEW.reason, NEW.prev_hash);
  RETURN NEW;
END
$$;
CREATE TRIGGER audit_chain BEFORE INSERT ON audit.audit_log
  FOR EACH ROW EXECUTE FUNCTION audit.chain_row();
CREATE TRIGGER audit_immutable BEFORE UPDATE OR DELETE ON audit.audit_log
  FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
CREATE TRIGGER audit_no_truncate BEFORE TRUNCATE ON audit.audit_log
  FOR EACH STATEMENT EXECUTE FUNCTION core.forbid_change();

-- Verifies the whole chain. Returns one row: ok, rows checked, first broken seq (if any).
-- SECURITY DEFINER so it always checks every row regardless of the caller's branch scope;
-- it returns only a verdict, never row contents. EXECUTE is granted to kite_app only.
CREATE OR REPLACE FUNCTION audit.verify_chain(
  OUT ok boolean, OUT rows_checked bigint, OUT first_broken_seq bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, audit, core AS $$
DECLARE
  r audit.audit_log%ROWTYPE;
  v_prev bytea := '\x0000000000000000000000000000000000000000000000000000000000000000'::bytea;
  v_expected_seq bigint := 1;
BEGIN
  ok := true; rows_checked := 0; first_broken_seq := NULL;
  FOR r IN SELECT * FROM audit.audit_log ORDER BY seq LOOP
    rows_checked := rows_checked + 1;
    IF r.seq <> v_expected_seq
       OR r.prev_hash <> v_prev
       OR r.hash <> audit.row_digest(r.seq, r.id, r.occurred_at, r.user_id, r.branch_id, r.action,
                                     r.entity_type, r.entity_id, r.before, r.after, r.reason, r.prev_hash) THEN
      ok := false; first_broken_seq := r.seq;
      RETURN;
    END IF;
    v_prev := r.hash;
    v_expected_seq := v_expected_seq + 1;
  END LOOP;
END
$$;
