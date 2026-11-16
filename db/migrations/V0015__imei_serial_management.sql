-- V0015: Phase 6 - IMEI and serial number management (blueprint 11): the central registry at head office with its
-- conflicts and the instructions it sends back to branch servers, each unit's head-office check state, holds with
-- their reasons, correcting a mistyped identifier, reversing a receipt made in error, the business's blocked-IMEI
-- list, model hints from the IMEI (TAC), serial number formats per brand, and counting devices by IMEI.
--
-- Uniqueness, as 11.4 requires: an IMEI belongs to one device unit for ever. Locally a partial unique index on
-- active identifiers guarantees it per server. Business-wide, head office inserts every identifier into
-- device.central_registry under a partial unique index on active rows: the first unit holds the identifier, a later
-- one received offline is stored as a conflict, both units go on hold, and the conflict closes by itself once only
-- one unit still claims the identifier (one was corrected or its receipt reversed).

-- ================================================================ identifiers: active or retired
-- A mistyped identifier is retired, never changed or deleted; the corrected one is a new row. Retiring also frees
-- the identifiers of a unit whose receipt was reversed. Only active rows are unique.
ALTER TABLE device.imei_registry
  ADD COLUMN status         text NOT NULL DEFAULT 'active' CHECK (status IN ('active','retired')),
  ADD COLUMN retired_at     timestamptz,
  ADD COLUMN retired_by     uuid,
  ADD COLUMN retired_reason text;
ALTER TABLE device.imei_registry ADD CONSTRAINT imei_registry_retired_when CHECK ((status = 'retired') = (retired_at IS NOT NULL));
ALTER TABLE device.imei_registry DROP CONSTRAINT imei_registry_device_unit_id_kind_slot_key;
CREATE UNIQUE INDEX imei_registry_slot ON device.imei_registry (device_unit_id, kind, slot) WHERE status = 'active';
DROP INDEX device.imei_registry_imei;
DROP INDEX device.imei_registry_serial;
CREATE UNIQUE INDEX imei_registry_imei ON device.imei_registry (business_id, value) WHERE kind IN ('imei','eid') AND status = 'active';
CREATE UNIQUE INDEX imei_registry_serial ON device.imei_registry (business_id, brand_key, value) WHERE kind = 'serial' AND status = 'active';

DROP TRIGGER device_imei_registry_immutable ON device.imei_registry;
CREATE FUNCTION device.imei_registry_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'KITE: identifiers are never deleted; a wrong one is retired' USING ERRCODE = 'KT001';
  END IF;
  IF OLD.status <> 'active' OR NEW.status <> 'retired'
     OR (to_jsonb(NEW) - 'status' - 'retired_at' - 'retired_by' - 'retired_reason') <> (to_jsonb(OLD) - 'status' - 'retired_at' - 'retired_by' - 'retired_reason') THEN
    RAISE EXCEPTION 'KITE: an identifier can only be retired' USING ERRCODE = 'KT001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER imei_registry_guard BEFORE UPDATE OR DELETE ON device.imei_registry FOR EACH ROW EXECUTE FUNCTION device.imei_registry_guard();

-- Retires one identifier of a unit the caller may write (kite_app has no UPDATE on the registry).
CREATE FUNCTION device.retire_identifier(p_id uuid, p_reason text, p_actor uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE branch uuid;
BEGIN
  SELECT u.branch_id INTO branch FROM device.imei_registry r JOIN device.device_unit u ON u.id = r.device_unit_id WHERE r.id = p_id;
  IF NOT FOUND OR NOT core.branch_writable(branch) THEN
    RAISE EXCEPTION 'KITE: identifier % is outside your branches', p_id USING ERRCODE = '42501';
  END IF;
  IF length(trim(coalesce(p_reason, ''))) = 0 THEN
    RAISE EXCEPTION 'KITE: retiring an identifier needs a reason' USING ERRCODE = 'KT020';
  END IF;
  UPDATE device.imei_registry SET status = 'retired', retired_at = now(), retired_by = p_actor, retired_reason = p_reason
   WHERE id = p_id AND status = 'active';
END $$;

-- ================================================================ unit lifecycle additions
-- 'reversed': the receipt was made in error (a duplicate of a unit held elsewhere); the unit never existed in stock.
ALTER TABLE device.device_unit DROP CONSTRAINT device_unit_status_check;
ALTER TABLE device.device_unit ADD CONSTRAINT device_unit_status_check CHECK (status IN
  ('in_stock','under_inspection','returned_inspection','refurbishing','for_parts','supplier_return','sold','in_transit','quarantined',
   'supplier_returned','written_off','reversed'));
ALTER TABLE device.device_event DROP CONSTRAINT device_event_event_type_check;
ALTER TABLE device.device_event ADD CONSTRAINT device_event_event_type_check CHECK (event_type IN
  ('received','inspected','cost_added','refurb_completed','sold','sale_voided','returned','written_off','held','released','price_set',
   'supplier_returned','transferred_out','transferred_in','identifier_corrected','receipt_reversed'));

ALTER TABLE device.transition DISABLE TRIGGER device_transition_immutable;
INSERT INTO device.transition (from_status, to_status, event_type) VALUES ('quarantined','reversed','receipt_reversed');
-- A mistyped identifier can be corrected while the unit is anywhere in the business, on hold, or sold with it.
INSERT INTO device.transition (from_status, to_status, event_type)
SELECT s, s, 'identifier_corrected'
  FROM unnest(ARRAY['in_stock','under_inspection','returned_inspection','refurbishing','for_parts','supplier_return','quarantined','sold']) s;
ALTER TABLE device.transition ENABLE TRIGGER device_transition_immutable;

-- apply_event again: an event that keeps a quarantined unit quarantined keeps the status it will be released to.
CREATE OR REPLACE FUNCTION device.apply_event(p_event_id uuid, p_unit uuid, p_event text, p_to_status text, p_to_condition text, p_grade_id uuid,
                                              p_selling_price numeric, p_warranty_days integer, p_doc_type text, p_doc_id uuid, p_details jsonb,
                                              p_actor uuid, p_cost_delta numeric DEFAULT 0)
RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  u        device.device_unit%ROWTYPE;
  target   text;
  cond     text;
  grade    uuid;
  price    numeric;
  warranty integer;
  next_seq integer;
BEGIN
  SELECT * INTO u FROM device.device_unit WHERE id = p_unit FOR UPDATE;
  IF NOT FOUND OR NOT core.branch_writable(u.branch_id) THEN
    RAISE EXCEPTION 'KITE: device unit % is outside your branches', p_unit USING ERRCODE = '42501';
  END IF;
  target := CASE WHEN p_event = 'released' THEN u.held_from_status ELSE coalesce(p_to_status, u.status) END;
  IF NOT EXISTS (SELECT 1 FROM device.transition t WHERE t.from_status = u.status AND t.to_status = target AND t.event_type = p_event) THEN
    RAISE EXCEPTION 'KITE: a % device cannot be %', replace(u.status, '_', ' '), replace(p_event, '_', ' ') || CASE WHEN target <> u.status THEN ' (to ' || replace(target, '_', ' ') || ')' ELSE '' END
      USING ERRCODE = 'KT020';
  END IF;
  IF p_event IN ('inspected','refurb_completed') THEN
    cond := coalesce(p_to_condition, u.condition); grade := p_grade_id; price := p_selling_price; warranty := coalesce(p_warranty_days, u.warranty_days);
  ELSIF p_event = 'price_set' THEN
    cond := u.condition; grade := u.grade_id; price := p_selling_price; warranty := coalesce(p_warranty_days, u.warranty_days);
  ELSE
    IF p_to_condition IS NOT NULL AND p_to_condition <> u.condition THEN
      RAISE EXCEPTION 'KITE: % does not change a unit''s condition', p_event USING ERRCODE = 'KT020';
    END IF;
    cond := u.condition; grade := u.grade_id; price := u.selling_price; warranty := u.warranty_days;
  END IF;
  IF p_event = 'refurb_completed' AND cond NOT IN ('refurbished','damaged') THEN
    RAISE EXCEPTION 'KITE: a refurbished unit comes back refurbished or damaged, not %', cond USING ERRCODE = 'KT020';
  END IF;
  IF p_event = 'inspected' AND cond = 'brand_new' AND u.condition <> 'brand_new' THEN
    RAISE EXCEPTION 'KITE: a used unit cannot become brand new' USING ERRCODE = 'KT020';
  END IF;
  IF p_event = 'inspected' AND cond = 'refurbished' AND u.condition <> 'refurbished' THEN
    RAISE EXCEPTION 'KITE: a unit becomes refurbished through a refurbishment order, not an inspection' USING ERRCODE = 'KT020';
  END IF;
  IF p_event = 'inspected' AND (target = 'for_parts') <> (cond = 'for_parts') THEN
    RAISE EXCEPTION 'KITE: a unit set aside for parts has the condition for parts' USING ERRCODE = 'KT020';
  END IF;
  IF p_event = 'inspected' AND target = 'refurbishing' AND cond NOT IN ('used','damaged') THEN
    RAISE EXCEPTION 'KITE: a unit goes to refurbishment as used or damaged' USING ERRCODE = 'KT020';
  END IF;
  IF grade IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM device.grade g JOIN catalog.variant v ON v.id = u.variant_id JOIN catalog.product p ON p.id = v.product_id
        WHERE g.id = grade AND g.business_id = u.business_id AND (g.category_id IS NULL OR g.category_id = p.category_id)) THEN
    RAISE EXCEPTION 'KITE: that grade is not for this product''s category' USING ERRCODE = 'KT020';
  END IF;
  IF p_event IN ('sold') AND p_doc_id IS NULL THEN
    RAISE EXCEPTION 'KITE: a sale event needs the sale' USING ERRCODE = 'KT020';
  END IF;

  UPDATE device.device_unit
     SET status = target,
         held_from_status = CASE WHEN target <> 'quarantined' THEN NULL
                                 WHEN u.status = 'quarantined' THEN u.held_from_status
                                 ELSE u.status END,
         condition = cond, grade_id = grade, selling_price = price, warranty_days = warranty,
         sold_sale_id = CASE WHEN target = 'sold' THEN coalesce(CASE WHEN u.status = 'sold' THEN u.sold_sale_id END, p_doc_id) END,
         sold_at = CASE WHEN target = 'sold' THEN coalesce(CASE WHEN u.status = 'sold' THEN u.sold_at END, now()) END,
         updated_by = p_actor
   WHERE id = p_unit;

  SELECT coalesce(max(seq), 0) + 1 INTO next_seq FROM device.device_event WHERE device_unit_id = p_unit;
  INSERT INTO device.device_event (id, business_id, branch_id, device_unit_id, seq, event_type, from_status, to_status, from_condition,
                                   to_condition, grade_id, cost_delta, selling_price, source_doc_type, source_doc_id, details, actor_id)
  VALUES (p_event_id, u.business_id, u.branch_id, p_unit, next_seq, p_event, u.status, target, u.condition, cond, grade,
          coalesce(p_cost_delta, 0), price, p_doc_type, p_doc_id, p_details, p_actor);
  RETURN p_event_id;
END $$;

-- ================================================================ holds and their reasons
-- A unit is on hold (quarantined) while it has at least one open hold: a manager's, an IMEI conflict at head office,
-- or a hit on the blocked-IMEI list. It is released only when the last one closes.
CREATE TABLE device.unit_hold (
  id              uuid PRIMARY KEY,
  business_id     uuid NOT NULL REFERENCES core.business(id),
  branch_id       uuid NOT NULL REFERENCES core.branch(id),
  device_unit_id  uuid NOT NULL REFERENCES device.device_unit(id),
  reason          text NOT NULL CHECK (reason IN ('manager','imei_conflict','blocked_imei')),
  conflict_id     uuid,                                     -- the conflict at head office (it lives there, so no reference)
  blocked_id      uuid,                                     -- the blocked-list entry
  note            text,
  held_at         timestamptz NOT NULL DEFAULT now(),
  held_by         uuid,
  released_at     timestamptz,
  released_by     uuid,
  release_note    text,
  CHECK ((reason = 'imei_conflict') = (conflict_id IS NOT NULL)),
  CHECK ((reason = 'blocked_imei') = (blocked_id IS NOT NULL))
);
CREATE UNIQUE INDEX unit_hold_open ON device.unit_hold (device_unit_id, reason, coalesce(conflict_id, blocked_id, '00000000-0000-0000-0000-000000000000'::uuid))
  WHERE released_at IS NULL;
CREATE INDEX unit_hold_unit ON device.unit_hold (device_unit_id);

-- Units already on hold were put there by a manager (Phase 5).
INSERT INTO device.unit_hold (id, business_id, branch_id, device_unit_id, reason, note, held_at, held_by)
SELECT gen_random_uuid(), u.business_id, u.branch_id, u.id, 'manager',
       (SELECT e.details ->> 'reason' FROM device.device_event e WHERE e.device_unit_id = u.id AND e.event_type = 'held' ORDER BY e.seq DESC LIMIT 1),
       coalesce((SELECT e.occurred_at FROM device.device_event e WHERE e.device_unit_id = u.id AND e.event_type = 'held' ORDER BY e.seq DESC LIMIT 1), now()),
       (SELECT e.actor_id FROM device.device_event e WHERE e.device_unit_id = u.id AND e.event_type = 'held' ORDER BY e.seq DESC LIMIT 1)
  FROM device.device_unit u WHERE u.status = 'quarantined';

-- ================================================================ head-office check state of each unit (11.4)
-- verified: head office holds the identifiers for this unit; pending: received while head office was unreachable,
-- not yet confirmed; conflict: head office found the identifier on another unit. pushed_seq is the unit's last
-- history event head office has been told about (a branch server pushes status changes too). stamp orders what head
-- office says about the unit: an answer or instruction older than the last one applied is ignored.
CREATE TABLE device.central_check (
  device_unit_id  uuid PRIMARY KEY REFERENCES device.device_unit(id),
  business_id     uuid NOT NULL REFERENCES core.business(id),
  branch_id       uuid NOT NULL REFERENCES core.branch(id),
  state           text NOT NULL CHECK (state IN ('pending','verified','conflict')),
  pushed_seq      integer NOT NULL DEFAULT 0,
  stamp           bigint NOT NULL DEFAULT 0,                -- head office's instruction number its last word on this unit reflects
  checked_at      timestamptz,
  attempts        integer NOT NULL DEFAULT 0,
  last_error      text,
  updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX central_check_pending ON device.central_check (state) WHERE state <> 'verified';

-- What a branch server last received from head office.
CREATE TABLE device.central_link (
  business_id       uuid PRIMARY KEY REFERENCES core.business(id),
  instruction_seq   bigint NOT NULL DEFAULT 0,
  last_push_at      timestamptz,
  last_push_ok_at   timestamptz,
  last_push_error   text,
  updated_at        timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE device.settings ADD COLUMN strict_central_check boolean NOT NULL DEFAULT false;

-- ================================================================ the central registry (head office)
CREATE TABLE device.imei_conflict (
  id            uuid PRIMARY KEY,
  business_id   uuid NOT NULL REFERENCES core.business(id),
  kind          text NOT NULL CHECK (kind IN ('imei','serial','eid')),
  value         text NOT NULL,
  brand_name    text NOT NULL DEFAULT '',
  status        text NOT NULL DEFAULT 'open' CHECK (status IN ('open','resolved')),
  opened_at     timestamptz NOT NULL DEFAULT now(),
  resolved_at   timestamptz,
  resolution    text,
  CHECK ((status = 'resolved') = (resolved_at IS NOT NULL))
);
CREATE UNIQUE INDEX imei_conflict_one_open ON device.imei_conflict (business_id, kind, brand_name, value) WHERE status = 'open';

-- Every identifier the business has handled, on any server (11.1): the unit, its server and branch, what it is
-- and where it stands. device_unit_id is the unit's id on its own server (UUIDv7, unique business-wide).
CREATE TABLE device.central_registry (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  kind             text NOT NULL CHECK (kind IN ('imei','serial','eid')),
  slot             smallint NOT NULL DEFAULT 1 CHECK (slot IN (1, 2)),
  value            text NOT NULL,
  brand_name       text NOT NULL DEFAULT '',                -- serial numbers are unique per brand (by name: catalogs are per server until Phase 12)
  server_id        uuid NOT NULL REFERENCES core.server(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  device_unit_id   uuid NOT NULL,
  description_en   text NOT NULL DEFAULT '',
  description_ar   text NOT NULL DEFAULT '',
  unit_status      text NOT NULL,
  condition        text NOT NULL,
  received_at      timestamptz,
  status           text NOT NULL DEFAULT 'active' CHECK (status IN ('active','conflict','retired')),
  conflict_id      uuid REFERENCES device.imei_conflict(id),
  registered_at    timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  retired_at       timestamptz,
  CHECK (kind <> 'imei' OR device.imei_valid(value)),
  CHECK (status <> 'conflict' OR conflict_id IS NOT NULL),
  CHECK ((status = 'retired') = (retired_at IS NOT NULL))
);
CREATE UNIQUE INDEX central_registry_active_imei ON device.central_registry (business_id, value) WHERE status = 'active' AND kind IN ('imei','eid');
CREATE UNIQUE INDEX central_registry_active_serial ON device.central_registry (business_id, brand_name, value) WHERE status = 'active' AND kind = 'serial';
CREATE UNIQUE INDEX central_registry_slot ON device.central_registry (server_id, device_unit_id, kind, slot) WHERE status <> 'retired';
CREATE INDEX central_registry_value ON device.central_registry (value);
CREATE INDEX central_registry_unit ON device.central_registry (device_unit_id);

CREATE TABLE device.imei_conflict_side (
  id               uuid PRIMARY KEY,
  conflict_id      uuid NOT NULL REFERENCES device.imei_conflict(id),
  registry_id      uuid NOT NULL REFERENCES device.central_registry(id),
  server_id        uuid NOT NULL REFERENCES core.server(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  device_unit_id   uuid NOT NULL,
  description_en   text NOT NULL DEFAULT '',
  description_ar   text NOT NULL DEFAULT '',
  unit_status      text NOT NULL,
  received_at      timestamptz,
  joined_at        timestamptz NOT NULL DEFAULT now(),
  outcome          text CHECK (outcome IS NULL OR outcome IN ('kept','corrected','reversed')),
  outcome_at       timestamptz,
  UNIQUE (conflict_id, registry_id)
);

-- What head office tells a branch server to do to one of its units (hold or release for a conflict), in order.
CREATE TABLE device.central_instruction (
  seq              bigserial PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  server_id        uuid NOT NULL REFERENCES core.server(id),
  kind             text NOT NULL CHECK (kind IN ('hold','release')),
  device_unit_id   uuid NOT NULL,
  conflict_id      uuid NOT NULL REFERENCES device.imei_conflict(id),
  value            text NOT NULL,
  note             text,
  created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX central_instruction_server ON device.central_instruction (server_id, seq);

-- ================================================================ blocked IMEIs (11.5), kept by the business at head office
CREATE SEQUENCE device.blocked_seq;
GRANT USAGE, SELECT ON SEQUENCE device.blocked_seq TO kite_app;
CREATE TABLE device.blocked_imei (
  id              uuid PRIMARY KEY,
  business_id     uuid NOT NULL REFERENCES core.business(id),
  kind            text NOT NULL CHECK (kind IN ('imei','serial')),
  value           text NOT NULL,
  reason          text NOT NULL CHECK (length(trim(reason)) > 0),
  reference       text,                                     -- a police report, the owner's name, ...
  status          text NOT NULL DEFAULT 'active' CHECK (status IN ('active','removed')),
  added_at        timestamptz NOT NULL DEFAULT now(),
  added_by        uuid,
  added_by_name   text,
  removed_at      timestamptz,
  removed_by      uuid,
  removed_reason  text,
  seq             bigint NOT NULL,                          -- head office's change number: a branch copies rows above its last one
  CHECK (kind <> 'imei' OR device.imei_valid(value)),
  CHECK ((status = 'removed') = (removed_at IS NOT NULL))
);
CREATE UNIQUE INDEX blocked_imei_active ON device.blocked_imei (business_id, kind, value) WHERE status = 'active';
CREATE INDEX blocked_imei_seq ON device.blocked_imei (seq);

-- ================================================================ model hints (11.2): the first 8 digits of an IMEI
CREATE TABLE device.tac_model (
  business_id  uuid NOT NULL REFERENCES core.business(id),
  tac          text NOT NULL CHECK (tac ~ '^[0-9]{8}$'),
  brand        text NOT NULL CHECK (length(trim(brand)) > 0),
  model        text NOT NULL CHECK (length(trim(model)) > 0),
  imported_at  timestamptz NOT NULL DEFAULT now(),
  imported_by  uuid,
  PRIMARY KEY (business_id, tac)
);

-- Serial number format per brand (11.2), a .NET regular expression checked by the server.
ALTER TABLE catalog.brand ADD COLUMN serial_pattern text CHECK (serial_pattern IS NULL OR length(serial_pattern) BETWEEN 1 AND 200);

-- ================================================================ counting devices by IMEI
CREATE TABLE device.unit_count (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  branch_id         uuid NOT NULL REFERENCES core.branch(id),
  number            text NOT NULL,
  variant_id        uuid REFERENCES catalog.variant(id),     -- one model, or every device in the branch
  status            text NOT NULL DEFAULT 'open' CHECK (status IN ('open','posted','cancelled')),
  note              text,
  started_at        timestamptz NOT NULL DEFAULT now(),
  started_by        uuid NOT NULL REFERENCES iam."user"(id),
  posted_at         timestamptz,
  posted_by         uuid REFERENCES iam."user"(id),
  result            jsonb,                                  -- what the count found, kept when it is posted
  updated_at        timestamptz NOT NULL DEFAULT now(),
  version           integer NOT NULL DEFAULT 1,
  origin_server_id  uuid,
  UNIQUE (business_id, number),
  CHECK ((status = 'posted') = (posted_at IS NOT NULL AND result IS NOT NULL))
);
CREATE UNIQUE INDEX unit_count_one_open ON device.unit_count (branch_id, coalesce(variant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WHERE status = 'open';
CREATE TRIGGER unit_count_touch BEFORE UPDATE ON device.unit_count FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE device.unit_count_scan (
  id               uuid PRIMARY KEY,
  count_id         uuid NOT NULL REFERENCES device.unit_count(id),
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  kind             text NOT NULL CHECK (kind IN ('imei','serial')),
  value            text NOT NULL,
  entry            text NOT NULL DEFAULT 'scan' CHECK (entry IN ('scan','manual')),
  device_unit_id   uuid REFERENCES device.device_unit(id),
  result           text NOT NULL CHECK (result IN ('here','other_model','elsewhere','not_in_stock','unknown')),
  scanned_at       timestamptz NOT NULL DEFAULT now(),
  scanned_by       uuid NOT NULL REFERENCES iam."user"(id),
  UNIQUE (count_id, value)
);

-- ================================================================ emergency sales of phones settled by a manager (6.4)
-- An emergency sale whose phone was no longer in stock is kept as a conflict record (records are immutable); the manager's
-- settlement is its own row: applied with the phone the customer took (the sale is written then), or dismissed with a reason.
CREATE TABLE sales.emergency_settlement (
  record_id    uuid PRIMARY KEY REFERENCES sales.emergency_record(id),
  business_id  uuid NOT NULL REFERENCES core.business(id),
  branch_id    uuid NOT NULL REFERENCES core.branch(id),
  outcome      text NOT NULL CHECK (outcome IN ('applied','dismissed')),
  sale_id      uuid REFERENCES sales.sale(id),
  note         text,
  settled_at   timestamptz NOT NULL DEFAULT now(),
  settled_by   uuid NOT NULL REFERENCES iam."user"(id),
  CHECK ((outcome = 'applied') = (sale_id IS NOT NULL)),
  CHECK (outcome <> 'dismissed' OR length(trim(coalesce(note, ''))) > 0)
);
CREATE TRIGGER emergency_settlement_immutable BEFORE UPDATE OR DELETE ON sales.emergency_settlement FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
REVOKE UPDATE, DELETE, TRUNCATE ON sales.emergency_settlement FROM kite_app;
ALTER TABLE sales.emergency_settlement ENABLE ROW LEVEL SECURITY;
CREATE POLICY branch_read ON sales.emergency_settlement FOR SELECT USING (core.branch_visible(branch_id));
CREATE POLICY branch_insert ON sales.emergency_settlement FOR INSERT WITH CHECK (core.branch_writable(branch_id));

-- ================================================================ grants, row-level security
REVOKE DELETE, TRUNCATE ON device.central_registry, device.imei_conflict, device.imei_conflict_side, device.central_instruction,
                           device.blocked_imei, device.unit_hold, device.unit_count FROM kite_app;
REVOKE UPDATE, DELETE, TRUNCATE ON device.central_instruction FROM kite_app;
CREATE TRIGGER central_instruction_immutable BEFORE UPDATE OR DELETE ON device.central_instruction FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
GRANT USAGE, SELECT ON SEQUENCE device.central_instruction_seq_seq TO kite_app;
REVOKE EXECUTE ON FUNCTION device.retire_identifier(uuid, text, uuid), device.imei_registry_guard() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION device.retire_identifier(uuid, text, uuid) TO kite_app;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['device.unit_hold','device.central_check','device.unit_count','device.unit_count_scan'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY branch_read ON %s FOR SELECT USING (core.branch_visible(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_insert ON %s FOR INSERT WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_update ON %s FOR UPDATE USING (core.branch_visible(branch_id)) WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_delete ON %s FOR DELETE USING (core.branch_writable(branch_id))', t);
  END LOOP;
END
$$;

-- ================================================================ existing units: register them, or mark them for the first push
DO $$
DECLARE self_role text; self_id uuid;
BEGIN
  SELECT s.role, s.id INTO self_role, self_id FROM core.server s WHERE s.is_self;
  IF self_role IN ('combined','main') THEN
    INSERT INTO device.central_registry (id, business_id, kind, slot, value, brand_name, server_id, branch_id, device_unit_id, description_en,
                                         description_ar, unit_status, condition, received_at, status)
    SELECT gen_random_uuid(), r.business_id, r.kind, r.slot, r.value,
           CASE WHEN r.kind = 'serial' THEN coalesce(lower(trim(br.name_en)), 'product:' || p.id::text) ELSE '' END,
           self_id, u.branch_id, u.id, p.name_en, p.name_ar, u.status, u.condition, u.received_at, 'active'
      FROM device.imei_registry r JOIN device.device_unit u ON u.id = r.device_unit_id
      JOIN catalog.variant v ON v.id = u.variant_id JOIN catalog.product p ON p.id = v.product_id
      LEFT JOIN catalog.brand br ON br.id = p.brand_id
     WHERE r.status = 'active';
    INSERT INTO device.central_check (device_unit_id, business_id, branch_id, state, pushed_seq, checked_at)
    SELECT u.id, u.business_id, u.branch_id, 'verified', (SELECT max(e.seq) FROM device.device_event e WHERE e.device_unit_id = u.id), now()
      FROM device.device_unit u;
  ELSIF self_role = 'branch' THEN
    INSERT INTO device.central_check (device_unit_id, business_id, branch_id, state, pushed_seq)
    SELECT u.id, u.business_id, u.branch_id, 'pending', 0 FROM device.device_unit u;
  END IF;
END
$$;

-- ================================================================ number series, permissions
INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year)
SELECT gen_random_uuid(), s.business_id, s.branch_id, 'device_count', 'DC-' || br.code || '-' || s.fiscal_year || '-', s.fiscal_year
  FROM (SELECT DISTINCT business_id, branch_id, fiscal_year FROM core.number_series WHERE branch_id IS NOT NULL AND terminal_id IS NULL) s
  JOIN core.branch br ON br.id = s.branch_id
ON CONFLICT DO NOTHING;

INSERT INTO iam.permission (code, module_code, description_en, description_ar) VALUES
  ('DEVICE.IMEI.CORRECT', 'device', 'Correct a mistyped IMEI or serial number of a device',          'تصحيح رقم IMEI أو رقم تسلسلي أُدخل خطأً'),
  ('DEVICE.CONFLICTS',    'device', 'See IMEI conflicts and settle emergency sales of devices',      'عرض تعارضات IMEI وتسوية مبيعات الأجهزة في وضع الطوارئ'),
  ('DEVICE.BLOCKLIST',    'device', 'Keep the business''s list of blocked IMEIs',                     'إدارة قائمة أرقام IMEI المحظورة'),
  ('DEVICE.COUNT',        'device', 'Count devices by scanning their IMEIs',                         'جرد الأجهزة بمسح أرقام IMEI');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r
  CROSS JOIN (VALUES ('DEVICE.IMEI.CORRECT'),('DEVICE.CONFLICTS'),('DEVICE.BLOCKLIST'),('DEVICE.COUNT')) AS p(code)
 WHERE r.code IN ('owner','branch_manager')
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('DEVICE.COUNT'),('DEVICE.IMEI.CORRECT'),('DEVICE.VIEW')) AS p(code)
 WHERE r.code = 'inventory_controller'
ON CONFLICT DO NOTHING;
