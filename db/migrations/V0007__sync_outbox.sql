-- V0007: transactional outbox (blueprint 7.1, 7.2). Written in the same transaction as the
-- business change. origin_seq is a gap-free counter per server; payloads never change.

CREATE TABLE sync.outbox (
  event_id           uuid PRIMARY KEY,
  origin_seq         bigint NOT NULL UNIQUE,
  event_type         text NOT NULL CHECK (event_type ~ '^[a-z][a-z0-9_]*\.[a-z][a-z0-9_.]*$'),
  schema_version     integer NOT NULL CHECK (schema_version > 0),
  aggregate_type     text NOT NULL,
  aggregate_id       uuid NOT NULL,
  aggregate_version  integer NOT NULL CHECK (aggregate_version > 0),
  business_id        uuid NOT NULL,
  origin_branch_id   uuid,
  origin_server_id   uuid NOT NULL,
  origin_device_id   uuid,
  actor_user_id      uuid,
  occurred_at        timestamptz NOT NULL,
  recorded_at        timestamptz NOT NULL DEFAULT now(),
  causation_id       uuid,
  correlation_id     uuid,
  payload            jsonb NOT NULL,
  payload_sha256     bytea NOT NULL CHECK (length(payload_sha256) = 32),
  status             text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','acked','archived')),
  attempts           integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
  last_attempt_at    timestamptz,
  last_error         text,
  acked_at           timestamptz,
  CHECK ((status = 'pending') = (acked_at IS NULL))
);
CREATE INDEX outbox_pending ON sync.outbox (origin_seq) WHERE status = 'pending';

CREATE OR REPLACE FUNCTION sync.outbox_before_insert() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, sync AS $$
DECLARE v_last bigint;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('kite.sync.outbox.seq', 0));
  SELECT max(origin_seq) INTO v_last FROM sync.outbox;
  NEW.origin_seq := coalesce(v_last, 0) + 1;
  NEW.payload_sha256 := sha256(convert_to(NEW.payload::text, 'UTF8'));
  NEW.status := 'pending';
  NEW.attempts := 0;
  NEW.acked_at := NULL;
  NEW.recorded_at := now();
  RETURN NEW;
END
$$;
CREATE TRIGGER outbox_assign BEFORE INSERT ON sync.outbox
  FOR EACH ROW EXECUTE FUNCTION sync.outbox_before_insert();

-- Only delivery bookkeeping may change; the event itself is immutable, and an acknowledged
-- event never goes back to pending.
CREATE OR REPLACE FUNCTION sync.outbox_before_update() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.event_id, NEW.origin_seq, NEW.event_type, NEW.schema_version, NEW.aggregate_type,
      NEW.aggregate_id, NEW.aggregate_version, NEW.business_id, NEW.origin_branch_id,
      NEW.origin_server_id, NEW.origin_device_id, NEW.actor_user_id, NEW.occurred_at,
      NEW.recorded_at, NEW.causation_id, NEW.correlation_id, NEW.payload, NEW.payload_sha256)
     IS DISTINCT FROM
     (OLD.event_id, OLD.origin_seq, OLD.event_type, OLD.schema_version, OLD.aggregate_type,
      OLD.aggregate_id, OLD.aggregate_version, OLD.business_id, OLD.origin_branch_id,
      OLD.origin_server_id, OLD.origin_device_id, OLD.actor_user_id, OLD.occurred_at,
      OLD.recorded_at, OLD.causation_id, OLD.correlation_id, OLD.payload, OLD.payload_sha256) THEN
    RAISE EXCEPTION 'KITE: outbox event % is immutable; only delivery status may change', OLD.event_id
      USING ERRCODE = 'KT004';
  END IF;
  IF OLD.status <> 'pending' AND NEW.status = 'pending' THEN
    RAISE EXCEPTION 'KITE: acknowledged outbox event % cannot return to pending', OLD.event_id
      USING ERRCODE = 'KT004';
  END IF;
  RETURN NEW;
END
$$;
CREATE TRIGGER outbox_protect BEFORE UPDATE ON sync.outbox
  FOR EACH ROW EXECUTE FUNCTION sync.outbox_before_update();

-- Never delete an unsynchronised event (blueprint 0.2 rule 1). Archival of acknowledged,
-- backed-up rows is a Phase 12 job running as the owner.
CREATE OR REPLACE FUNCTION sync.outbox_before_delete() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status <> 'archived' THEN
    RAISE EXCEPTION 'KITE: outbox event % (status %) cannot be deleted', OLD.event_id, OLD.status
      USING ERRCODE = 'KT001';
  END IF;
  RETURN OLD;
END
$$;
CREATE TRIGGER outbox_no_delete BEFORE DELETE ON sync.outbox
  FOR EACH ROW EXECUTE FUNCTION sync.outbox_before_delete();
CREATE TRIGGER outbox_no_truncate BEFORE TRUNCATE ON sync.outbox
  FOR EACH STATEMENT EXECUTE FUNCTION core.forbid_change();
