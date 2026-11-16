-- V0014: Phase 5 - device units (one identity per physical device for its whole life in the business), IMEI and
-- serial identifiers, the status lifecycle, conditions and grades, inspection checklists and inspections,
-- refurbishment orders and their costs, reservations at the till, and serialized stock in the stock ledger at
-- specific cost (blueprint 11, 12, 16.3).
--
-- A unit's cost changes only through the stock ledger: acquisition cost when it is received, then one 'unit_cost'
-- movement per cost added (parts, labour, outside work). Its status changes only through device.apply_event, which
-- allows only the transitions of 11.3 and writes the unit's history in the same transaction. At commit a unit that
-- is in stock holds exactly one unit in the ledger at exactly its accumulated cost, and a unit that is not holds none.

CREATE SCHEMA IF NOT EXISTS device;
CREATE SCHEMA IF NOT EXISTS refurb;

DO $$
DECLARE s text;
BEGIN
  FOREACH s IN ARRAY ARRAY['device','refurb'] LOOP
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO kite_app, kite_ro', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO kite_app', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT ON TABLES TO kite_ro', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT EXECUTE ON FUNCTIONS TO kite_app', s);
  END LOOP;
END
$$;

-- ================================================================ identifiers
-- An IMEI is 15 digits whose last digit is the Luhn check digit (11.2); 14- or 16-digit entries are refused.
CREATE FUNCTION device.imei_valid(p text) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE STRICT AS $$
DECLARE s int := 0; d int; i int;
BEGIN
  IF p !~ '^[0-9]{15}$' THEN RETURN false; END IF;
  FOR i IN 1..15 LOOP
    d := ascii(substr(p, i, 1)) - 48;
    IF i % 2 = 0 THEN d := d * 2; IF d > 9 THEN d := d - 9; END IF; END IF;
    s := s + d;
  END LOOP;
  RETURN s % 10 = 0;
END $$;

-- Statuses that are physically in a warehouse (the stock ledger holds the unit).
CREATE FUNCTION device.in_stock_status(p_status text, p_held_from text) RETURNS boolean
LANGUAGE sql IMMUTABLE AS $$
  SELECT CASE WHEN p_status = 'quarantined' THEN coalesce(p_held_from, '') ELSE p_status END
         IN ('in_stock','under_inspection','returned_inspection','refurbishing','for_parts','supplier_return')
$$;

-- ================================================================ settings, grades, checklists (business-wide)
CREATE TABLE device.settings (
  business_id                  uuid PRIMARY KEY REFERENCES core.business(id),
  labour_rate_per_hour         numeric(18,3) NOT NULL DEFAULT 3.000 CHECK (labour_rate_per_hour >= 0),
  reservation_minutes          integer NOT NULL DEFAULT 15 CHECK (reservation_minutes BETWEEN 1 AND 240),
  parked_reservation_minutes   integer NOT NULL DEFAULT 240 CHECK (parked_reservation_minutes BETWEEN 1 AND 10080),
  suggested_price_step         numeric(6,3) NOT NULL DEFAULT 0.100 CHECK (suggested_price_step > 0),
  updated_at                   timestamptz NOT NULL DEFAULT now(),
  updated_by                   uuid,
  version                      integer NOT NULL DEFAULT 1
);
CREATE TRIGGER settings_touch BEFORE UPDATE ON device.settings FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- Grades (12.1): configurable per category (null = every category); the grade suggests the selling price as a
-- percentage of the variant's price when new.
CREATE TABLE device.grade (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  category_id      uuid REFERENCES catalog.category(id),
  code             text NOT NULL CHECK (code ~ '^[A-Z0-9][A-Z0-9+-]{0,7}$'),
  name_en          text NOT NULL CHECK (length(trim(name_en)) > 0),
  name_ar          text NOT NULL CHECK (length(trim(name_ar)) > 0),
  description_en   text,
  description_ar   text,
  price_percent    numeric(5,2) NOT NULL CHECK (price_percent > 0 AND price_percent <= 100),
  sort             smallint NOT NULL DEFAULT 0,
  status           text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid,
  updated_at       timestamptz NOT NULL DEFAULT now(),
  updated_by       uuid,
  version          integer NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX grade_code_unique ON device.grade (business_id, coalesce(category_id, '00000000-0000-0000-0000-000000000000'::uuid), code);
CREATE TRIGGER grade_touch BEFORE UPDATE ON device.grade FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- Inspection checklists (12.4): one active checklist per category, null = the default. Items are changed freely:
-- every inspection keeps its own copy of the items it was done with.
CREATE TABLE device.checklist (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  category_id  uuid REFERENCES catalog.category(id),
  name_en      text NOT NULL CHECK (length(trim(name_en)) > 0),
  name_ar      text NOT NULL CHECK (length(trim(name_ar)) > 0),
  status       text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   uuid,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid,
  version      integer NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX checklist_one_active ON device.checklist (business_id, coalesce(category_id, '00000000-0000-0000-0000-000000000000'::uuid))
  WHERE status = 'active';
CREATE TRIGGER checklist_touch BEFORE UPDATE ON device.checklist FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE device.checklist_item (
  id            uuid PRIMARY KEY,
  checklist_id  uuid NOT NULL REFERENCES device.checklist(id),
  sort          smallint NOT NULL DEFAULT 0,
  code          text NOT NULL CHECK (code ~ '^[a-z][a-z0-9_]{0,31}$'),
  label_en      text NOT NULL CHECK (length(trim(label_en)) > 0),
  label_ar      text NOT NULL CHECK (length(trim(label_ar)) > 0),
  kind          text NOT NULL CHECK (kind IN ('pass_fail','percent','text')),
  required      boolean NOT NULL DEFAULT true,
  min_percent   smallint CHECK (min_percent IS NULL OR min_percent BETWEEN 0 AND 100),
  on_fail       text NOT NULL DEFAULT 'refurbish' CHECK (on_fail IN ('refurbish','damaged','for_parts')),
  status        text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  UNIQUE (checklist_id, code),
  CHECK (kind <> 'percent' OR min_percent IS NOT NULL),
  CHECK (kind <> 'text' OR NOT required)
);

-- ================================================================ device units
CREATE TABLE device.device_unit (
  id                 uuid PRIMARY KEY,
  business_id        uuid NOT NULL REFERENCES core.business(id),
  branch_id          uuid NOT NULL REFERENCES core.branch(id),
  warehouse_id       uuid NOT NULL REFERENCES core.warehouse(id),
  variant_id         uuid NOT NULL REFERENCES catalog.variant(id),
  status             text NOT NULL CHECK (status IN ('in_stock','under_inspection','returned_inspection','refurbishing','for_parts',
                                                     'supplier_return','sold','in_transit','quarantined','supplier_returned','written_off')),
  held_from_status   text,                                  -- the status a quarantined unit returns to when released
  condition          text NOT NULL CHECK (condition IN ('brand_new','used','refurbished','damaged','for_parts')),
  grade_id           uuid REFERENCES device.grade(id),
  acquisition_cost   numeric(18,3) NOT NULL CHECK (acquisition_cost >= 0),
  accumulated_cost   numeric(18,3) NOT NULL CHECK (accumulated_cost >= 0),   -- acquisition + every cost added; set by the stock ledger only
  selling_price      numeric(18,3) CHECK (selling_price IS NULL OR selling_price >= 0),
  warranty_days      integer CHECK (warranty_days IS NULL OR warranty_days >= 0),
  source             text NOT NULL CHECK (source IN ('opening','adjustment','purchase','trade_in','buy_back')),
  source_doc_type    text NOT NULL,
  source_doc_id      uuid NOT NULL,
  received_at        timestamptz NOT NULL DEFAULT now(),
  sold_sale_id       uuid,
  sold_at            timestamptz,
  note               text,
  created_at         timestamptz NOT NULL DEFAULT now(),
  created_by         uuid,
  updated_at         timestamptz NOT NULL DEFAULT now(),
  updated_by         uuid,
  version            integer NOT NULL DEFAULT 1,
  origin_server_id   uuid,
  CHECK ((status = 'quarantined') = (held_from_status IS NOT NULL)),
  CHECK (held_from_status IS NULL OR held_from_status NOT IN ('quarantined','sold','written_off','supplier_returned')),
  CHECK (status <> 'for_parts' OR condition = 'for_parts'),
  CHECK (status <> 'in_stock' OR condition <> 'for_parts'),
  CHECK ((status = 'sold') = (sold_sale_id IS NOT NULL))
);
CREATE INDEX device_unit_stock ON device.device_unit (branch_id, variant_id, status);
CREATE INDEX device_unit_status ON device.device_unit (status, received_at);
CREATE TRIGGER device_unit_touch BEFORE UPDATE ON device.device_unit FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- IMEI 1 and 2, serial number, eSIM EID (11.1). An IMEI or EID belongs to one unit for ever: a phone that comes
-- back reconnects to its unit. Serial numbers are unique per brand.
CREATE TABLE device.imei_registry (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  device_unit_id   uuid NOT NULL REFERENCES device.device_unit(id),
  kind             text NOT NULL CHECK (kind IN ('imei','serial','eid')),
  slot             smallint NOT NULL DEFAULT 1 CHECK (slot IN (1, 2)),
  value            text NOT NULL,
  brand_key        uuid NOT NULL,                           -- the product's brand, or the product when it has none
  entry            text NOT NULL DEFAULT 'scan' CHECK (entry IN ('scan','manual')),
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid,
  UNIQUE (device_unit_id, kind, slot),
  CHECK (kind <> 'imei' OR device.imei_valid(value)),
  CHECK (kind <> 'serial' OR (value ~ '^[A-Z0-9][A-Z0-9-]{3,39}$' AND slot = 1)),
  CHECK (kind <> 'eid' OR (value ~ '^[0-9]{32}$' AND slot = 1))
);
CREATE UNIQUE INDEX imei_registry_imei ON device.imei_registry (business_id, value) WHERE kind IN ('imei','eid');
CREATE UNIQUE INDEX imei_registry_serial ON device.imei_registry (business_id, brand_key, value) WHERE kind = 'serial';
CREATE INDEX imei_registry_value ON device.imei_registry (value);

-- The unit's history (11.6): append-only, numbered per unit.
CREATE TABLE device.device_event (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  device_unit_id   uuid NOT NULL REFERENCES device.device_unit(id),
  seq              integer NOT NULL CHECK (seq > 0),
  event_type       text NOT NULL CHECK (event_type IN ('received','inspected','cost_added','refurb_completed','sold','sale_voided','returned',
                                                       'written_off','held','released','price_set','supplier_returned','transferred_out','transferred_in')),
  from_status      text,
  to_status        text NOT NULL,
  from_condition   text,
  to_condition     text NOT NULL,
  grade_id         uuid REFERENCES device.grade(id),
  cost_delta       numeric(18,3) NOT NULL DEFAULT 0,
  selling_price    numeric(18,3),
  source_doc_type  text,
  source_doc_id    uuid,
  details          jsonb,
  occurred_at      timestamptz NOT NULL DEFAULT now(),
  actor_id         uuid,
  UNIQUE (device_unit_id, seq)
);
CREATE INDEX device_event_doc ON device.device_event (source_doc_type, source_doc_id);

-- The allowed transitions (11.3). A row whose from_status equals to_status is an event that does not move the unit.
CREATE TABLE device.transition (
  from_status  text NOT NULL,
  to_status    text NOT NULL,
  event_type   text NOT NULL,
  PRIMARY KEY (from_status, to_status, event_type)
);
INSERT INTO device.transition (from_status, to_status, event_type) VALUES
  ('under_inspection','in_stock','inspected'), ('under_inspection','refurbishing','inspected'),
  ('under_inspection','for_parts','inspected'), ('under_inspection','supplier_return','inspected'),
  ('returned_inspection','in_stock','inspected'), ('returned_inspection','refurbishing','inspected'),
  ('returned_inspection','for_parts','inspected'), ('returned_inspection','supplier_return','inspected'),
  ('refurbishing','refurbishing','cost_added'),
  ('refurbishing','in_stock','refurb_completed'),
  ('in_stock','sold','sold'),
  ('sold','in_stock','sale_voided'),
  ('sold','returned_inspection','returned'),
  ('in_stock','written_off','written_off'), ('for_parts','written_off','written_off'),
  ('in_stock','supplier_returned','supplier_returned'), ('supplier_return','supplier_returned','supplier_returned'),
  ('in_stock','in_transit','transferred_out'), ('in_transit','in_stock','transferred_in'),
  ('in_stock','in_stock','price_set'), ('under_inspection','under_inspection','price_set'),
  ('returned_inspection','returned_inspection','price_set'), ('refurbishing','refurbishing','price_set');
-- Any unit still in the business can be put on hold; release returns it to where it was.
INSERT INTO device.transition (from_status, to_status, event_type)
SELECT s, 'quarantined', 'held' FROM unnest(ARRAY['in_stock','under_inspection','returned_inspection','refurbishing','for_parts','supplier_return']) s;
INSERT INTO device.transition (from_status, to_status, event_type)
SELECT 'quarantined', s, 'released' FROM unnest(ARRAY['in_stock','under_inspection','returned_inspection','refurbishing','for_parts','supplier_return']) s;

-- A new unit enters as received (in stock, or waiting for inspection), at its acquisition cost; its first event is written here.
CREATE FUNCTION device.unit_inserted() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
  IF TG_OP = 'INSERT' AND TG_WHEN = 'BEFORE' THEN
    IF NEW.status NOT IN ('in_stock','under_inspection') THEN
      RAISE EXCEPTION 'KITE: a new device unit is in stock or under inspection, not %', NEW.status USING ERRCODE = 'KT020';
    END IF;
    IF NEW.condition = 'for_parts' THEN
      RAISE EXCEPTION 'KITE: a unit is set aside for parts by an inspection' USING ERRCODE = 'KT020';
    END IF;
    NEW.accumulated_cost := NEW.acquisition_cost;
    NEW.held_from_status := NULL;
    NEW.sold_sale_id := NULL;
    NEW.sold_at := NULL;
    RETURN NEW;
  END IF;
  INSERT INTO device.device_event (id, business_id, branch_id, device_unit_id, seq, event_type, from_status, to_status, from_condition,
                                   to_condition, grade_id, cost_delta, selling_price, source_doc_type, source_doc_id, occurred_at, actor_id)
  VALUES (gen_random_uuid(), NEW.business_id, NEW.branch_id, NEW.id, 1, 'received', NULL, NEW.status, NULL, NEW.condition, NEW.grade_id, 0,
          NEW.selling_price, NEW.source_doc_type, NEW.source_doc_id, NEW.received_at, NEW.created_by);
  RETURN NULL;
END $$;
CREATE TRIGGER device_unit_before_insert BEFORE INSERT ON device.device_unit FOR EACH ROW EXECUTE FUNCTION device.unit_inserted();
CREATE TRIGGER device_unit_after_insert AFTER INSERT ON device.device_unit FOR EACH ROW EXECUTE FUNCTION device.unit_inserted();

-- Every status, condition, grade or price change of a unit goes through here (kite_app cannot update units): the
-- transition must be in device.transition, the caller must be able to write the unit's branch, and the event is
-- written in the same transaction. Returns the event's id.
CREATE FUNCTION device.apply_event(p_event_id uuid, p_unit uuid, p_event text, p_to_status text, p_to_condition text, p_grade_id uuid,
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
  -- Condition, grade and price are decided only by receiving, inspection, refurbishment and price setting.
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
         held_from_status = CASE WHEN target = 'quarantined' THEN u.status END,
         condition = cond, grade_id = grade, selling_price = price, warranty_days = warranty,
         sold_sale_id = CASE WHEN target = 'sold' THEN p_doc_id END,
         sold_at = CASE WHEN target = 'sold' THEN now() END,
         updated_by = p_actor
   WHERE id = p_unit;

  SELECT coalesce(max(seq), 0) + 1 INTO next_seq FROM device.device_event WHERE device_unit_id = p_unit;
  INSERT INTO device.device_event (id, business_id, branch_id, device_unit_id, seq, event_type, from_status, to_status, from_condition,
                                   to_condition, grade_id, cost_delta, selling_price, source_doc_type, source_doc_id, details, actor_id)
  VALUES (p_event_id, u.business_id, u.branch_id, p_unit, next_seq, p_event, u.status, target, u.condition, cond, grade,
          coalesce(p_cost_delta, 0), price, p_doc_type, p_doc_id, p_details, p_actor);
  RETURN p_event_id;
END $$;

-- ================================================================ serialized stock in the ledger
ALTER TABLE inv.stock_movement ADD COLUMN device_unit_id uuid REFERENCES device.device_unit(id);
ALTER TABLE inv.stock_movement DROP CONSTRAINT stock_movement_movement_type_check;
ALTER TABLE inv.stock_movement ADD CONSTRAINT stock_movement_movement_type_check CHECK (movement_type IN
  ('opening','adjustment','count','sale','return','emergency_sale','purchase_receipt','supplier_return','transfer_out','transfer_in',
   'unit_cost','refurb_issue','refurb_return'));
ALTER TABLE inv.stock_movement DROP CONSTRAINT stock_movement_qty_check;
ALTER TABLE inv.stock_movement ADD CONSTRAINT stock_movement_qty_check CHECK ((qty <> 0) <> (movement_type = 'unit_cost'));
CREATE INDEX stock_movement_unit ON inv.stock_movement (device_unit_id) WHERE device_unit_id IS NOT NULL;

-- The ledger trigger, now with specific identification for serialized items (16.3): a unit moves in or out at its
-- accumulated cost, and a cost added to a unit in stock is a 'unit_cost' movement of quantity 0 that raises both
-- the unit's cost and the balance value. Non-serialized items keep the weighted average of Phase 4.
CREATE OR REPLACE FUNCTION inv.apply_movement() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  b        inv.stock_balance%ROWTYPE;
  u        device.device_unit%ROWTYPE;
  p_type   text;
  p_track  text;
  out_qty  numeric;
BEGIN
  SELECT p.product_type, p.tracking INTO p_type, p_track
    FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id WHERE v.id = NEW.variant_id;
  IF p_type = 'service' THEN
    RAISE EXCEPTION 'KITE: services are not stocked' USING ERRCODE = 'KT010';
  END IF;
  PERFORM 1 FROM core.warehouse w WHERE w.id = NEW.warehouse_id AND w.branch_id = NEW.branch_id;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'KITE: warehouse % is not in branch %', NEW.warehouse_id, NEW.branch_id USING ERRCODE = 'KT010';
  END IF;

  IF p_track IS DISTINCT FROM 'none' THEN
    IF NEW.device_unit_id IS NULL THEN
      RAISE EXCEPTION 'KITE: % is serialized; it moves one device unit at a time', NEW.variant_id USING ERRCODE = 'KT010';
    END IF;
    SELECT * INTO u FROM device.device_unit WHERE id = NEW.device_unit_id FOR UPDATE;
    IF NOT FOUND OR u.variant_id <> NEW.variant_id OR u.branch_id <> NEW.branch_id OR u.warehouse_id <> NEW.warehouse_id THEN
      RAISE EXCEPTION 'KITE: device unit % is not that item in that warehouse', NEW.device_unit_id USING ERRCODE = 'KT010';
    END IF;
  ELSIF NEW.device_unit_id IS NOT NULL OR NEW.movement_type = 'unit_cost' THEN
    RAISE EXCEPTION 'KITE: % is not serialized', NEW.variant_id USING ERRCODE = 'KT010';
  END IF;

  INSERT INTO inv.stock_balance (business_id, branch_id, warehouse_id, variant_id)
  VALUES (NEW.business_id, NEW.branch_id, NEW.warehouse_id, NEW.variant_id)
  ON CONFLICT (branch_id, warehouse_id, variant_id) DO NOTHING;
  SELECT * INTO b FROM inv.stock_balance
   WHERE branch_id = NEW.branch_id AND warehouse_id = NEW.warehouse_id AND variant_id = NEW.variant_id
   FOR UPDATE;

  IF p_track IS DISTINCT FROM 'none' THEN
    IF NEW.movement_type = 'unit_cost' THEN
      IF NEW.value IS NULL OR NEW.value = 0 OR NEW.value <> round(NEW.value, 3) THEN
        RAISE EXCEPTION 'KITE: a unit cost movement carries a non-zero value' USING ERRCODE = 'KT010';
      END IF;
      IF u.accumulated_cost + NEW.value < 0 THEN
        RAISE EXCEPTION 'KITE: the cost of unit % cannot go below zero', u.id USING ERRCODE = 'KT010';
      END IF;
      UPDATE device.device_unit SET accumulated_cost = u.accumulated_cost + NEW.value WHERE id = u.id;
      NEW.unit_cost := NULL;
    ELSIF NEW.qty = 1 THEN
      NEW.unit_cost := u.accumulated_cost;
      NEW.value := u.accumulated_cost;
    ELSIF NEW.qty = -1 THEN
      NEW.unit_cost := u.accumulated_cost;
      NEW.value := -u.accumulated_cost;
    ELSE
      RAISE EXCEPTION 'KITE: a device unit moves one at a time' USING ERRCODE = 'KT010';
    END IF;
  ELSIF NEW.qty > 0 THEN
    IF NEW.value IS NOT NULL THEN                        -- an exact value given: a part coming back at what it went out at
      IF NEW.value < 0 OR NEW.value <> round(NEW.value, 3) THEN
        RAISE EXCEPTION 'KITE: an incoming value is zero or more, in baisa' USING ERRCODE = 'KT010';
      END IF;
      NEW.unit_cost := round(NEW.value / NEW.qty, 6);
    ELSE
      IF NEW.unit_cost IS NULL THEN
        NEW.unit_cost := b.avg_cost;                     -- e.g. a count gain at the current average
      END IF;
      NEW.value := round(NEW.qty * NEW.unit_cost, 3);
    END IF;
  ELSE
    out_qty := -NEW.qty;
    IF b.qty >= out_qty THEN
      NEW.value := -round(b.value * out_qty / b.qty, 3);  -- its exact share of the valuation
    ELSIF b.qty > 0 THEN
      NEW.value := -(b.value + round((out_qty - b.qty) * b.avg_cost, 3));   -- goes negative (6.4): rest at the last average
    ELSE
      NEW.value := -round(out_qty * b.avg_cost, 3);
    END IF;
    NEW.unit_cost := CASE WHEN out_qty = 0 THEN 0 ELSE round(-NEW.value / out_qty, 6) END;
  END IF;

  NEW.qty_after := b.qty + NEW.qty;
  NEW.value_after := b.value + NEW.value;
  UPDATE inv.stock_balance
     SET qty = NEW.qty_after,
         value = NEW.value_after,
         avg_cost = CASE WHEN NEW.qty_after > 0 THEN round(NEW.value_after / NEW.qty_after, 6) ELSE b.avg_cost END,
         movement_count = b.movement_count + 1,
         last_movement_at = NEW.movement_at
   WHERE branch_id = NEW.branch_id AND warehouse_id = NEW.warehouse_id AND variant_id = NEW.variant_id;
  RETURN NEW;
END
$$;

-- At commit, a unit and the ledger agree: in stock means exactly one unit in the ledger at exactly its accumulated
-- cost; anywhere else (sold, written off, returned to the supplier) means none. Deferred, so a document can write
-- the movement and the status change in either order.
CREATE FUNCTION device.check_unit_stock() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  uid uuid;
  u   device.device_unit%ROWTYPE;
  q   numeric;
  v   numeric;
  w   uuid;
BEGIN
  IF TG_TABLE_NAME = 'device_unit' THEN
    uid := (to_jsonb(NEW) ->> 'id')::uuid;
  ELSE
    uid := (to_jsonb(NEW) ->> 'device_unit_id')::uuid;
  END IF;
  SELECT * INTO u FROM device.device_unit WHERE id = uid;
  SELECT coalesce(sum(m.qty), 0), coalesce(sum(m.value), 0) INTO q, v FROM inv.stock_movement m WHERE m.device_unit_id = uid;
  IF device.in_stock_status(u.status, u.held_from_status) THEN
    IF q <> 1 OR v <> u.accumulated_cost THEN
      RAISE EXCEPTION 'KITE: device unit % is % but the stock ledger holds % at %, not 1 at %', uid, u.status, q, v, u.accumulated_cost
        USING ERRCODE = 'KT021';
    END IF;
    SELECT m.warehouse_id INTO w FROM inv.stock_movement m WHERE m.device_unit_id = uid AND m.qty > 0 ORDER BY m.movement_at DESC, m.id DESC LIMIT 1;
    IF w IS DISTINCT FROM u.warehouse_id THEN
      RAISE EXCEPTION 'KITE: device unit % is not in the warehouse the ledger has it in', uid USING ERRCODE = 'KT021';
    END IF;
  ELSIF u.status <> 'in_transit' AND (q <> 0 OR v <> 0) THEN
    RAISE EXCEPTION 'KITE: device unit % is % but the stock ledger still holds % at %', uid, u.status, q, v USING ERRCODE = 'KT021';
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER device_unit_stock_agrees AFTER INSERT OR UPDATE ON device.device_unit
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION device.check_unit_stock();
CREATE CONSTRAINT TRIGGER stock_movement_unit_agrees AFTER INSERT ON inv.stock_movement
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.device_unit_id IS NOT NULL) EXECUTE FUNCTION device.check_unit_stock();

-- Units whose cost, history or ledger disagree (23.2 for Phase 5). Invoker rights: row-level security applies.
CREATE FUNCTION device.check_units()
RETURNS TABLE (device_unit_id uuid, problem text, expected numeric, actual numeric)
LANGUAGE sql STABLE AS $$
  WITH led AS (SELECT m.device_unit_id AS id, sum(m.qty) AS qty, sum(m.value) AS value
                 FROM inv.stock_movement m WHERE m.device_unit_id IS NOT NULL GROUP BY 1),
       ev AS (SELECT e.device_unit_id AS id, sum(e.cost_delta) AS added, count(*) FILTER (WHERE e.event_type = 'received') AS received
                FROM device.device_event e GROUP BY 1)
  SELECT u.id, 'cost is not acquisition plus the costs in its history', u.acquisition_cost + coalesce(ev.added, 0), u.accumulated_cost
    FROM device.device_unit u LEFT JOIN ev ON ev.id = u.id
   WHERE u.accumulated_cost <> u.acquisition_cost + coalesce(ev.added, 0)
  UNION ALL
  SELECT u.id, 'stock ledger quantity does not match the status',
         CASE WHEN device.in_stock_status(u.status, u.held_from_status) THEN 1 ELSE 0 END, coalesce(led.qty, 0)
    FROM device.device_unit u LEFT JOIN led ON led.id = u.id
   WHERE u.status <> 'in_transit' AND coalesce(led.qty, 0) <> CASE WHEN device.in_stock_status(u.status, u.held_from_status) THEN 1 ELSE 0 END
  UNION ALL
  SELECT u.id, 'stock ledger value does not match the unit''s cost',
         CASE WHEN device.in_stock_status(u.status, u.held_from_status) THEN u.accumulated_cost ELSE 0 END, coalesce(led.value, 0)
    FROM device.device_unit u LEFT JOIN led ON led.id = u.id
   WHERE u.status <> 'in_transit' AND coalesce(led.value, 0) <> CASE WHEN device.in_stock_status(u.status, u.held_from_status) THEN u.accumulated_cost ELSE 0 END
  UNION ALL
  SELECT u.id, 'history has no single received event', 1, coalesce(ev.received, 0)
    FROM device.device_unit u LEFT JOIN ev ON ev.id = u.id WHERE coalesce(ev.received, 0) <> 1
$$;

-- ================================================================ reservations at the till (10.2, 11.3)
-- A unit in a cart or a parked sale is leased to that cart until the lease expires; an expired lease is simply
-- taken over, so a till that crashed never strands a phone. "Reserved" is the lease, not a stored status.
CREATE TABLE device.reservation (
  device_unit_id  uuid PRIMARY KEY REFERENCES device.device_unit(id),
  business_id     uuid NOT NULL REFERENCES core.business(id),
  branch_id       uuid NOT NULL REFERENCES core.branch(id),
  cart_id         uuid NOT NULL,                            -- the cart's sale UUID
  terminal_id     uuid NOT NULL REFERENCES core.terminal(id),
  user_id         uuid NOT NULL REFERENCES iam."user"(id),
  reserved_at     timestamptz NOT NULL DEFAULT now(),
  expires_at      timestamptz NOT NULL,
  CHECK (expires_at > reserved_at)
);
CREATE INDEX reservation_cart ON device.reservation (cart_id);

-- ================================================================ inspections (12.4, 11.3)
CREATE TABLE device.inspection (
  id                 uuid PRIMARY KEY,
  business_id        uuid NOT NULL REFERENCES core.business(id),
  branch_id          uuid NOT NULL REFERENCES core.branch(id),
  device_unit_id     uuid NOT NULL REFERENCES device.device_unit(id),
  number             text NOT NULL,
  reason             text NOT NULL CHECK (reason IN ('intake','return')),
  checklist_id       uuid REFERENCES device.checklist(id),
  results            jsonb NOT NULL CHECK (jsonb_typeof(results) = 'array'),   -- the items as they were, with the answers
  battery_health     smallint CHECK (battery_health IS NULL OR battery_health BETWEEN 0 AND 100),
  suggested_outcome  text NOT NULL CHECK (suggested_outcome IN ('restock','refurbish','for_parts','supplier_return')),
  outcome            text NOT NULL CHECK (outcome IN ('restock','refurbish','for_parts','supplier_return')),
  result_condition   text NOT NULL CHECK (result_condition IN ('brand_new','used','refurbished','damaged','for_parts')),
  grade_id           uuid REFERENCES device.grade(id),
  selling_price      numeric(18,3) CHECK (selling_price IS NULL OR selling_price >= 0),
  note               text,
  event_id           uuid NOT NULL REFERENCES device.device_event(id),
  inspected_by       uuid NOT NULL REFERENCES iam."user"(id),
  inspected_at       timestamptz NOT NULL DEFAULT now(),
  origin_server_id   uuid,
  UNIQUE (business_id, number),
  CHECK (outcome = suggested_outcome OR length(trim(coalesce(note, ''))) > 0)   -- overriding the checklist needs a reason
);
CREATE INDEX inspection_unit ON device.inspection (device_unit_id);

-- ================================================================ refurbishment (12.2, 12.6)
CREATE TABLE refurb.refurb_order (
  id                 uuid PRIMARY KEY,
  business_id        uuid NOT NULL REFERENCES core.business(id),
  branch_id          uuid NOT NULL REFERENCES core.branch(id),
  number             text NOT NULL,
  device_unit_id     uuid NOT NULL REFERENCES device.device_unit(id),
  inspection_id      uuid REFERENCES device.inspection(id),
  technician_id      uuid REFERENCES iam."user"(id),
  status             text NOT NULL DEFAULT 'open' CHECK (status IN ('open','completed')),
  problem            text,
  work_done          text,
  opened_at          timestamptz NOT NULL DEFAULT now(),
  opened_by          uuid NOT NULL,
  completed_at       timestamptz,
  completed_by       uuid,
  result_condition   text CHECK (result_condition IN ('refurbished','damaged')),
  grade_id           uuid REFERENCES device.grade(id),
  selling_price      numeric(18,3) CHECK (selling_price IS NULL OR selling_price >= 0),
  warranty_days      integer CHECK (warranty_days IS NULL OR warranty_days >= 0),
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  updated_by         uuid,
  version            integer NOT NULL DEFAULT 1,
  origin_server_id   uuid,
  UNIQUE (business_id, number),
  CHECK ((status = 'completed') = (completed_at IS NOT NULL AND completed_by IS NOT NULL AND result_condition IS NOT NULL))
);
CREATE UNIQUE INDEX refurb_order_one_open ON refurb.refurb_order (device_unit_id) WHERE status = 'open';
CREATE TRIGGER refurb_order_touch BEFORE UPDATE ON refurb.refurb_order FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- A completed order is never changed again.
CREATE FUNCTION refurb.order_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = 'completed' THEN
    RAISE EXCEPTION 'KITE: refurbishment % is completed and cannot be changed', OLD.number USING ERRCODE = 'KT001';
  END IF;
  IF NEW.device_unit_id <> OLD.device_unit_id OR NEW.number <> OLD.number OR NEW.branch_id <> OLD.branch_id THEN
    RAISE EXCEPTION 'KITE: a refurbishment order keeps its unit and number' USING ERRCODE = 'KT001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER refurb_order_guard BEFORE UPDATE ON refurb.refurb_order FOR EACH ROW EXECUTE FUNCTION refurb.order_guard();

-- Every cost added to the unit (12.2): parts at their stock cost, labour at the rate, outside work at its invoice.
-- A mistake is taken back by a reversal row while the order is open; rows are never changed.
CREATE TABLE refurb.refurb_cost (
  id                 uuid PRIMARY KEY,
  business_id        uuid NOT NULL REFERENCES core.business(id),
  branch_id          uuid NOT NULL REFERENCES core.branch(id),
  refurb_order_id    uuid NOT NULL REFERENCES refurb.refurb_order(id),
  device_unit_id     uuid NOT NULL REFERENCES device.device_unit(id),
  kind               text NOT NULL CHECK (kind IN ('part','labour','external')),
  part_variant_id    uuid REFERENCES catalog.variant(id),
  qty                numeric(14,3),
  unit_cost          numeric(18,6),
  labour_minutes     integer,
  labour_rate        numeric(18,3),
  technician_id      uuid REFERENCES iam."user"(id),
  vendor             text,
  reference          text,
  amount             numeric(18,3) NOT NULL CHECK (amount <> 0),
  reversal_of_id     uuid UNIQUE REFERENCES refurb.refurb_cost(id),
  part_movement_id   uuid REFERENCES inv.stock_movement(id),
  unit_movement_id   uuid NOT NULL REFERENCES inv.stock_movement(id),
  event_id           uuid NOT NULL REFERENCES device.device_event(id),
  journal_id         uuid REFERENCES acct.journal(id),
  note               text,
  created_at         timestamptz NOT NULL DEFAULT now(),
  created_by         uuid NOT NULL,
  origin_server_id   uuid,
  CHECK (kind <> 'part' OR (part_variant_id IS NOT NULL AND qty IS NOT NULL AND qty <> 0 AND part_movement_id IS NOT NULL)),
  CHECK (kind <> 'labour' OR (labour_minutes IS NOT NULL AND labour_minutes <> 0 AND labour_rate IS NOT NULL)),
  CHECK (kind <> 'external' OR length(trim(coalesce(vendor, ''))) > 0),
  CHECK ((reversal_of_id IS NULL) = (amount > 0))
);
CREATE INDEX refurb_cost_order ON refurb.refurb_cost (refurb_order_id);

-- ================================================================ sales and adjustments carry the unit
ALTER TABLE sales.sale_line ADD COLUMN device_unit_id uuid REFERENCES device.device_unit(id);
ALTER TABLE sales.sale_line ADD COLUMN profit numeric(18,3) GENERATED ALWAYS AS (net_amount - cost_value) STORED;
CREATE INDEX sale_line_unit ON sales.sale_line (device_unit_id) WHERE device_unit_id IS NOT NULL;
ALTER TABLE sales.sale_line ADD CONSTRAINT sale_line_unit_qty CHECK (device_unit_id IS NULL OR qty = 1);
ALTER TABLE inv.adjustment_line ADD COLUMN device_unit_id uuid REFERENCES device.device_unit(id);
ALTER TABLE inv.adjustment_line ADD CONSTRAINT adjustment_line_unit_qty CHECK (device_unit_id IS NULL OR qty IN (1, -1));

-- A returned device line names the same unit as the line it returns (11.5).
CREATE OR REPLACE FUNCTION sales.check_return_qty() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE sold numeric; returned numeric; orig_variant uuid; orig_unit uuid;
BEGIN
  IF NEW.returned_line_id IS NULL THEN RETURN NEW; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(NEW.returned_line_id::text, 0));
  SELECT l.qty, l.variant_id, l.device_unit_id INTO sold, orig_variant, orig_unit
    FROM sales.sale_line l JOIN sales.sale s ON s.id = l.sale_id
   WHERE l.id = NEW.returned_line_id AND s.doc_type <> 'credit_note';
  IF NOT FOUND THEN
    RAISE EXCEPTION 'KITE: returned line % is not a sold line', NEW.returned_line_id USING ERRCODE = 'KT011';
  END IF;
  IF orig_variant <> NEW.variant_id THEN
    RAISE EXCEPTION 'KITE: a return line must be the same item as the line it returns' USING ERRCODE = 'KT011';
  END IF;
  IF orig_unit IS DISTINCT FROM NEW.device_unit_id THEN
    RAISE EXCEPTION 'KITE: a returned device must be the unit that was sold on that line' USING ERRCODE = 'KT011';
  END IF;
  SELECT coalesce(sum(l.qty), 0) INTO returned FROM sales.sale_line l WHERE l.returned_line_id = NEW.returned_line_id;
  IF returned + NEW.qty > sold THEN
    RAISE EXCEPTION 'KITE: line % sold %, already returned %, cannot return % more', NEW.returned_line_id, sold, returned, NEW.qty
      USING ERRCODE = 'KT011';
  END IF;
  RETURN NEW;
END $$;

-- ================================================================ immutability, grants, row-level security
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['device.imei_registry','device.device_event','device.inspection','refurb.refurb_cost','device.transition'] LOOP
    EXECUTE format('CREATE TRIGGER %s_immutable BEFORE UPDATE OR DELETE ON %s FOR EACH ROW EXECUTE FUNCTION core.forbid_change()',
                   replace(t, '.', '_'), t);
    EXECUTE format('REVOKE UPDATE, DELETE, TRUNCATE ON %s FROM kite_app', t);
  END LOOP;
END
$$;
REVOKE INSERT ON device.device_event, device.transition FROM kite_app;   -- events are written by device.apply_event and the insert trigger
REVOKE UPDATE, DELETE, TRUNCATE ON device.device_unit FROM kite_app;
REVOKE DELETE, TRUNCATE ON device.grade, device.checklist, device.checklist_item, device.settings, refurb.refurb_order FROM kite_app;
REVOKE EXECUTE ON FUNCTION device.unit_inserted(), device.check_unit_stock() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION device.apply_event(uuid, uuid, text, text, text, uuid, numeric, integer, text, uuid, jsonb, uuid, numeric) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION device.apply_event(uuid, uuid, text, text, text, uuid, numeric, integer, text, uuid, jsonb, uuid, numeric) TO kite_app;
GRANT EXECUTE ON FUNCTION device.check_units(), device.imei_valid(text), device.in_stock_status(text, text) TO kite_app, kite_ro;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['device.device_unit','device.device_event','device.reservation','device.inspection',
                           'refurb.refurb_order','refurb.refurb_cost'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY branch_read ON %s FOR SELECT USING (core.branch_visible(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_insert ON %s FOR INSERT WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_update ON %s FOR UPDATE USING (core.branch_visible(branch_id)) WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_delete ON %s FOR DELETE USING (core.branch_writable(branch_id))', t);
  END LOOP;
END
$$;
-- Identifiers follow their unit's branch.
ALTER TABLE device.imei_registry ENABLE ROW LEVEL SECURITY;
CREATE POLICY branch_read ON device.imei_registry FOR SELECT USING (EXISTS (SELECT 1 FROM device.device_unit u WHERE u.id = device_unit_id));
CREATE POLICY branch_insert ON device.imei_registry FOR INSERT
  WITH CHECK (EXISTS (SELECT 1 FROM device.device_unit u WHERE u.id = device_unit_id AND core.branch_writable(u.branch_id)));

-- ================================================================ accounts, permissions, posting rules for existing data
-- Labour absorbed into a device's cost is credited to a contra-expense account (12.5).
INSERT INTO acct.account (id, business_id, code, name_en, name_ar, type, parent_id, is_postable, is_control)
SELECT gen_random_uuid(), b.id, '6210', 'Labour absorbed into stock', 'أجور محمّلة على المخزون', 'expense',
       (SELECT a.id FROM acct.account a WHERE a.business_id = b.id AND a.code = '6000'), true, false
  FROM core.business b
 WHERE NOT EXISTS (SELECT 1 FROM acct.account a WHERE a.business_id = b.id AND a.code = '6210');

-- Inspection and refurbishment series for every existing branch and fiscal year that already has series.
INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year)
SELECT gen_random_uuid(), s.business_id, s.branch_id, d.doc_type, d.prefix || '-' || br.code || '-' || s.fiscal_year || '-', s.fiscal_year
  FROM (SELECT DISTINCT business_id, branch_id, fiscal_year FROM core.number_series WHERE branch_id IS NOT NULL AND terminal_id IS NULL) s
  JOIN core.branch br ON br.id = s.branch_id
 CROSS JOIN (VALUES ('device_inspection','INS'), ('refurb_order','RF')) AS d(doc_type, prefix)
ON CONFLICT DO NOTHING;

INSERT INTO iam.permission (code, module_code, description_en, description_ar) VALUES
  ('DEVICE.VIEW',         'device', 'View device units, their history, costs and profit',       'عرض الأجهزة وسجلها وتكاليفها وأرباحها'),
  ('DEVICE.INSPECT',      'refurb', 'Inspect devices and decide restock, refurbish or parts',   'فحص الأجهزة وتقرير إعادتها للمخزون أو تجديدها أو تخصيصها للقطع'),
  ('DEVICE.REFURBISH',    'refurb', 'Work refurbishment orders: parts, labour, outside work',   'تنفيذ أوامر التجديد: القطع والأجور والأعمال الخارجية'),
  ('DEVICE.PRICE',        'device', 'Set the selling price of a device unit',                   'تحديد سعر بيع الجهاز'),
  ('DEVICE.SELL_DAMAGED', 'device', 'Sell a damaged device (with disclosure on the receipt)',   'بيع جهاز به ضرر (مع الإفصاح في الإيصال)'),
  ('DEVICE.WRITE_OFF',    'device', 'Write off device units',                                   'شطب الأجهزة'),
  ('DEVICE.HOLD',         'device', 'Put device units on hold and release them',                'إيقاف الأجهزة مؤقتًا والإفراج عنها'),
  ('DEVICE.SETTINGS',     'refurb', 'Manage grades, inspection checklists and the labour rate', 'إدارة الدرجات وقوائم الفحص وسعر الأجور');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r
  CROSS JOIN (SELECT code FROM iam.permission WHERE code LIKE 'DEVICE.%' AND code <> 'DEVICE.IMEI.MANUAL_ENTRY') p
 WHERE r.code IN ('owner','branch_manager')
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r
  CROSS JOIN (VALUES ('DEVICE.VIEW'),('DEVICE.INSPECT'),('DEVICE.REFURBISH'),('CATALOG.VIEW'),('INV.VIEW')) AS p(code)
 WHERE r.code = 'technician'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('DEVICE.VIEW'),('DEVICE.INSPECT')) AS p(code)
 WHERE r.code = 'inventory_controller'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, 'DEVICE.VIEW' FROM iam.role r WHERE r.code IN ('senior_cashier','accountant')
ON CONFLICT DO NOTHING;
