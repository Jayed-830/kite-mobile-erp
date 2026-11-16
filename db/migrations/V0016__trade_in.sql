-- V0016: Phase 7 - trade-ins and the profit margin mechanism for used goods (blueprint 12.3 - 12.5, 14.6).
--
-- A trade-in is a purchase from a customer. While it is open, staff record the device, inspect it and value it
-- against the price guide; a manager approves values above the staff limit or too far above the guide; the
-- customer consents. Accepting it, in one transaction, creates (or reconnects) the device unit waiting for
-- inspection, moves it into stock at the agreed value and posts Dr used-device stock / Cr trade-in clearing.
-- Settling it takes the clearing back out: applied to a sale (a tender), left as store credit, or paid out in cash
-- from a drawer. The settlements of a trade-in never add up to more than its agreed value, and a trade-in is
-- settled exactly when they add up to it, so the clearing account always equals what is still to be settled.
--
-- Under the profit margin mechanism (Tax Authority approval recorded), a device bought from a customer who is not
-- VAT-registered is bought with a profit-margin self-invoice, and its sale is taxed on the margin:
-- tax = (price - purchase price) x rate / (100 + rate), never below zero.

CREATE SCHEMA IF NOT EXISTS tradein;
GRANT USAGE ON SCHEMA tradein TO kite_app, kite_ro;
ALTER DEFAULT PRIVILEGES IN SCHEMA tradein GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO kite_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA tradein GRANT SELECT ON TABLES TO kite_ro;
ALTER DEFAULT PRIVILEGES IN SCHEMA tradein GRANT EXECUTE ON FUNCTIONS TO kite_app;

-- ================================================================ the margin scheme approval (12.3)
-- One row per business: recorded by the accountant with the Tax Authority's reference and date. While approved,
-- the VAT-MARGIN code is active; withdrawing it switches the code off for sales from then on.
CREATE TABLE tax.margin_scheme (
  business_id    uuid PRIMARY KEY REFERENCES core.business(id),
  approved       boolean NOT NULL DEFAULT false,
  reference      text,
  approved_on    date,
  note           text,
  updated_at     timestamptz NOT NULL DEFAULT now(),
  updated_by     uuid,
  version        integer NOT NULL DEFAULT 1,
  CHECK (NOT approved OR (length(trim(coalesce(reference, ''))) > 0 AND approved_on IS NOT NULL))
);
CREATE TRIGGER margin_scheme_touch BEFORE UPDATE ON tax.margin_scheme FOR EACH ROW EXECUTE FUNCTION core.touch_row();
REVOKE DELETE, TRUNCATE ON tax.margin_scheme FROM kite_app;

-- ================================================================ settings, price guide, deductions
CREATE TABLE tradein.settings (
  business_id         uuid PRIMARY KEY REFERENCES core.business(id),
  staff_limit         numeric(18,3) NOT NULL DEFAULT 50 CHECK (staff_limit >= 0),         -- above this a manager approves
  over_guide_percent  numeric(6,2) NOT NULL DEFAULT 10 CHECK (over_guide_percent BETWEEN 0 AND 100),
  require_civil_id    boolean NOT NULL DEFAULT true,
  declaration_en      text NOT NULL,
  declaration_ar      text NOT NULL,
  updated_at          timestamptz NOT NULL DEFAULT now(),
  updated_by          uuid,
  version             integer NOT NULL DEFAULT 1
);
CREATE TRIGGER tradein_settings_touch BEFORE UPDATE ON tradein.settings FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- Price guide (12.4): model and storage (the variant) and grade give the base value.
CREATE TABLE tradein.price_guide (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  variant_id   uuid NOT NULL REFERENCES catalog.variant(id),
  grade_id     uuid NOT NULL REFERENCES device.grade(id),
  base_value   numeric(18,3) NOT NULL CHECK (base_value >= 0),
  status       text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   uuid,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid,
  version      integer NOT NULL DEFAULT 1,
  CONSTRAINT price_guide_one UNIQUE (business_id, variant_id, grade_id)
);
CREATE TRIGGER price_guide_touch BEFORE UPDATE ON tradein.price_guide FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- Deductions for problems found: a fixed amount, or a percentage of the guide value. A checklist item code links a
-- failed check to the deduction the wizard proposes.
CREATE TABLE tradein.deduction (
  id              uuid PRIMARY KEY,
  business_id     uuid NOT NULL REFERENCES core.business(id),
  code            text NOT NULL CHECK (code ~ '^[A-Z0-9_-]{2,20}$'),
  name_en         text NOT NULL CHECK (length(trim(name_en)) > 0),
  name_ar         text NOT NULL CHECK (length(trim(name_ar)) > 0),
  kind            text NOT NULL CHECK (kind IN ('amount','percent')),
  value           numeric(18,3) NOT NULL CHECK (value >= 0),
  checklist_code  text,
  sort            smallint NOT NULL DEFAULT 0,
  status          text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      uuid,
  updated_at      timestamptz NOT NULL DEFAULT now(),
  updated_by      uuid,
  version         integer NOT NULL DEFAULT 1,
  CONSTRAINT deduction_code UNIQUE (business_id, code),
  CHECK (kind <> 'percent' OR value <= 100)
);
CREATE TRIGGER deduction_touch BEFORE UPDATE ON tradein.deduction FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- ================================================================ trade-ins
CREATE TABLE tradein.tradein (
  id                   uuid PRIMARY KEY,
  business_id          uuid NOT NULL REFERENCES core.business(id),
  branch_id            uuid NOT NULL REFERENCES core.branch(id),
  number               text NOT NULL,
  status               text NOT NULL DEFAULT 'open' CHECK (status IN ('open','accepted','settled','rejected','cancelled')),
  customer_id          uuid NOT NULL REFERENCES crm.customer(id),
  civil_id_enc         bytea,                                     -- encrypted with this server's key (18.4)
  civil_id_last4       text CHECK (civil_id_last4 IS NULL OR civil_id_last4 ~ '^[0-9A-Z]{1,4}$'),
  seller_vat_number    text,                                      -- a VAT-registered seller: no margin scheme
  variant_id           uuid NOT NULL REFERENCES catalog.variant(id),
  imei1                text CHECK (imei1 IS NULL OR device.imei_valid(imei1)),
  imei2                text CHECK (imei2 IS NULL OR device.imei_valid(imei2)),
  serial               text CHECK (serial IS NULL OR serial ~ '^[A-Z0-9][A-Z0-9-]{3,39}$'),
  entry                text NOT NULL DEFAULT 'scan' CHECK (entry IN ('scan','manual')),
  storage              text,
  colour               text,
  grade_id             uuid REFERENCES device.grade(id),
  condition            text NOT NULL DEFAULT 'used' CHECK (condition IN ('used','damaged')),
  inspection           jsonb NOT NULL DEFAULT '{}',
  battery_health       smallint CHECK (battery_health IS NULL OR battery_health BETWEEN 0 AND 100),
  cloud_lock_removed   boolean NOT NULL DEFAULT false,
  guide_value          numeric(18,3) CHECK (guide_value IS NULL OR guide_value >= 0),
  deductions_total     numeric(18,3) NOT NULL DEFAULT 0 CHECK (deductions_total >= 0),
  offered_value        numeric(18,3) CHECK (offered_value IS NULL OR offered_value >= 0),
  agreed_value         numeric(18,3) CHECK (agreed_value IS NULL OR agreed_value >= 0),
  approval_id          uuid REFERENCES iam.approval(id),
  consent_method       text CHECK (consent_method IS NULL OR consent_method IN ('on_screen','paper')),
  consent_name         text,
  consent_reference    text,
  consent_text         text,
  consent_at           timestamptz,
  sale_id              uuid,                                      -- the cart (sale UUID) it is to be applied to
  terminal_id          uuid REFERENCES core.terminal(id),
  device_unit_id       uuid REFERENCES device.device_unit(id),
  reconnected          boolean NOT NULL DEFAULT false,            -- the business sold this device before (11.1)
  margin_scheme        boolean NOT NULL DEFAULT false,            -- bought under the profit margin mechanism
  self_invoice_number  text,
  note                 text,
  reject_reason        text,
  accepted_at          timestamptz,
  accepted_by          uuid,
  settled_at           timestamptz,
  closed_at            timestamptz,                               -- rejected or cancelled
  closed_by            uuid,
  created_at           timestamptz NOT NULL DEFAULT now(),
  created_by           uuid NOT NULL,
  updated_at           timestamptz NOT NULL DEFAULT now(),
  updated_by           uuid,
  version              integer NOT NULL DEFAULT 1,
  origin_server_id     uuid,
  UNIQUE (business_id, number),
  CHECK (imei1 IS NOT NULL OR serial IS NOT NULL),
  CHECK (status NOT IN ('accepted','settled') OR (device_unit_id IS NOT NULL AND agreed_value IS NOT NULL AND consent_at IS NOT NULL
                                                 AND accepted_at IS NOT NULL)),
  CHECK (consent_at IS NULL OR (consent_method = 'on_screen' AND length(trim(coalesce(consent_name, ''))) > 0)
                             OR (consent_method = 'paper' AND length(trim(coalesce(consent_reference, ''))) > 0)),
  CHECK (status <> 'rejected' OR length(trim(coalesce(reject_reason, ''))) > 0),
  CHECK ((status IN ('rejected','cancelled')) = (closed_at IS NOT NULL)),
  CHECK ((status = 'settled') = (settled_at IS NOT NULL)),
  CHECK (margin_scheme = (self_invoice_number IS NOT NULL)),
  CHECK (NOT margin_scheme OR seller_vat_number IS NULL)
);
CREATE INDEX tradein_branch_status ON tradein.tradein (branch_id, status, created_at);
CREATE INDEX tradein_customer ON tradein.tradein (customer_id);
CREATE INDEX tradein_sale ON tradein.tradein (sale_id) WHERE sale_id IS NOT NULL;

-- An open trade-in is worked on; an accepted one is history: only its settlement state (and the cart it is to be
-- applied to) moves. A rejected or cancelled one never changes again.
CREATE FUNCTION tradein.tradein_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE moving text[] := ARRAY['status','settled_at','sale_id','terminal_id','updated_at','updated_by','version'];
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'KITE: a trade-in is never deleted; cancel or reject it' USING ERRCODE = 'KT030';
  END IF;
  IF OLD.status IN ('rejected','cancelled') THEN
    RAISE EXCEPTION 'KITE: trade-in % is % and cannot change', OLD.number, OLD.status USING ERRCODE = 'KT030';
  END IF;
  IF OLD.status = 'open' THEN
    IF NEW.status NOT IN ('open','accepted','rejected','cancelled') THEN
      RAISE EXCEPTION 'KITE: an open trade-in is accepted, rejected or cancelled, not %', NEW.status USING ERRCODE = 'KT030';
    END IF;
    RETURN NEW;
  END IF;
  -- accepted or settled
  IF NEW.status NOT IN ('accepted','settled') THEN
    RAISE EXCEPTION 'KITE: trade-in % was accepted; it can only be settled', OLD.number USING ERRCODE = 'KT030';
  END IF;
  IF (SELECT jsonb_object_agg(k, v) FROM jsonb_each(to_jsonb(NEW)) AS e(k, v) WHERE NOT k = ANY(moving))
     IS DISTINCT FROM (SELECT jsonb_object_agg(k, v) FROM jsonb_each(to_jsonb(OLD)) AS e(k, v) WHERE NOT k = ANY(moving)) THEN
    RAISE EXCEPTION 'KITE: trade-in % was accepted; its device, value and consent cannot change', OLD.number USING ERRCODE = 'KT030';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER tradein_guard BEFORE UPDATE OR DELETE ON tradein.tradein FOR EACH ROW EXECUTE FUNCTION tradein.tradein_guard();
CREATE TRIGGER tradein_touch BEFORE UPDATE ON tradein.tradein FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- The deductions applied to a trade-in: replaced while it is open, fixed once it is accepted.
CREATE TABLE tradein.tradein_issue (
  id            uuid PRIMARY KEY,
  business_id   uuid NOT NULL REFERENCES core.business(id),
  branch_id     uuid NOT NULL REFERENCES core.branch(id),
  tradein_id    uuid NOT NULL REFERENCES tradein.tradein(id),
  deduction_id  uuid REFERENCES tradein.deduction(id),
  code          text NOT NULL,
  name_en       text NOT NULL,
  name_ar       text NOT NULL,
  amount        numeric(18,3) NOT NULL CHECK (amount >= 0),
  UNIQUE (tradein_id, code)
);
CREATE FUNCTION tradein.issue_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE st text;
BEGIN
  SELECT status INTO st FROM tradein.tradein WHERE id = coalesce(NEW.tradein_id, OLD.tradein_id);
  IF st IS DISTINCT FROM 'open' THEN
    RAISE EXCEPTION 'KITE: the deductions of a trade-in change only while it is open' USING ERRCODE = 'KT030';
  END IF;
  RETURN coalesce(NEW, OLD);
END $$;
CREATE TRIGGER tradein_issue_guard BEFORE INSERT OR UPDATE OR DELETE ON tradein.tradein_issue FOR EACH ROW EXECUTE FUNCTION tradein.issue_guard();

-- ================================================================ settlements (12.5)
CREATE TABLE tradein.settlement (
  id              uuid PRIMARY KEY,
  business_id     uuid NOT NULL REFERENCES core.business(id),
  branch_id       uuid NOT NULL REFERENCES core.branch(id),
  tradein_id      uuid NOT NULL REFERENCES tradein.tradein(id),
  kind            text NOT NULL CHECK (kind IN ('sale','store_credit','cash')),
  amount          numeric(18,3) NOT NULL CHECK (amount <> 0),    -- negative: a sale's trade-in payment taken back by a void
  sale_id         uuid REFERENCES sales.sale(id),
  shift_id        uuid REFERENCES sales.shift(id),
  cash_event_id   uuid,
  credit_entry_id uuid,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      uuid NOT NULL,
  CHECK ((kind = 'sale') = (sale_id IS NOT NULL)),
  CHECK (kind = 'sale' OR amount > 0),
  CHECK ((kind = 'cash') = (cash_event_id IS NOT NULL AND shift_id IS NOT NULL)),
  CHECK ((kind = 'store_credit') = (credit_entry_id IS NOT NULL))
);
CREATE INDEX settlement_tradein ON tradein.settlement (tradein_id);
CREATE TRIGGER settlement_immutable BEFORE UPDATE OR DELETE ON tradein.settlement FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

-- After each settlement row: the trade-in's settlements stay between zero and its agreed value, and the trade-in is
-- settled exactly when they reach it (a void that takes a sale's payment back makes it accepted again).
CREATE FUNCTION tradein.settlement_applied() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE t tradein.tradein%ROWTYPE; total numeric;
BEGIN
  SELECT * INTO t FROM tradein.tradein WHERE id = NEW.tradein_id FOR UPDATE;
  IF t.status NOT IN ('accepted','settled') THEN
    RAISE EXCEPTION 'KITE: trade-in % is %; only an accepted trade-in is settled', t.number, t.status USING ERRCODE = 'KT030';
  END IF;
  IF NEW.branch_id <> t.branch_id AND NEW.kind <> 'sale' THEN
    RAISE EXCEPTION 'KITE: trade-in % is settled at the branch that bought the device', t.number USING ERRCODE = 'KT030';
  END IF;
  SELECT coalesce(sum(amount), 0) INTO total FROM tradein.settlement WHERE tradein_id = t.id;
  IF total > t.agreed_value THEN
    RAISE EXCEPTION 'KITE: trade-in % would be settled for % but is worth %', t.number, total, t.agreed_value USING ERRCODE = 'KT031';
  END IF;
  IF total < 0 THEN
    RAISE EXCEPTION 'KITE: trade-in % would be settled for less than nothing', t.number USING ERRCODE = 'KT031';
  END IF;
  UPDATE tradein.tradein
     SET status = CASE WHEN total = t.agreed_value THEN 'settled' ELSE 'accepted' END,
         settled_at = CASE WHEN total = t.agreed_value THEN coalesce(t.settled_at, now()) END,
         updated_by = NEW.created_by
   WHERE id = t.id;
  RETURN NULL;
END $$;
CREATE TRIGGER settlement_applied AFTER INSERT ON tradein.settlement FOR EACH ROW EXECUTE FUNCTION tradein.settlement_applied();

-- What is still to be settled, per trade-in: the trade-in clearing account (2320) must equal its total.
CREATE FUNCTION tradein.outstanding(p_tradein uuid) RETURNS numeric
LANGUAGE sql STABLE AS $$
  SELECT t.agreed_value - coalesce((SELECT sum(s.amount) FROM tradein.settlement s WHERE s.tradein_id = t.id), 0)
    FROM tradein.tradein t WHERE t.id = p_tradein AND t.status IN ('accepted','settled')
$$;

-- ================================================================ store credit (14.1, pulled forward from Phase 10)
-- The customer's credit ledger: append-only lines with a balance type. Phase 7 uses store credit (we owe the
-- customer); receivables and deposits come with Phase 10. A balance is a sum, never a stored total, and store
-- credit never goes below zero.
CREATE TABLE crm.credit_ledger (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  customer_id      uuid NOT NULL REFERENCES crm.customer(id),
  balance_type     text NOT NULL CHECK (balance_type IN ('store_credit')),
  amount           numeric(18,3) NOT NULL CHECK (amount <> 0),   -- store credit: + issued (we owe more), - used
  source_doc_type  text NOT NULL,
  source_doc_id    uuid NOT NULL,
  note             text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid NOT NULL,
  origin_server_id uuid
);
CREATE INDEX credit_ledger_customer ON crm.credit_ledger (customer_id, balance_type);
CREATE INDEX credit_ledger_source ON crm.credit_ledger (source_doc_type, source_doc_id);
CREATE TRIGGER credit_ledger_immutable BEFORE UPDATE OR DELETE ON crm.credit_ledger FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
CREATE TRIGGER credit_ledger_no_truncate BEFORE TRUNCATE ON crm.credit_ledger FOR EACH STATEMENT EXECUTE FUNCTION core.forbid_change();

-- The customer's store credit across every branch on this server (row-level security would hide other branches).
CREATE FUNCTION crm.store_credit_balance(p_customer uuid) RETURNS numeric
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
  SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE customer_id = p_customer AND balance_type = 'store_credit'
$$;

CREATE FUNCTION crm.credit_ledger_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE bal numeric;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('kite:store_credit:' || NEW.customer_id::text, 0));
  IF NEW.amount < 0 THEN
    SELECT coalesce(sum(amount), 0) INTO bal FROM crm.credit_ledger WHERE customer_id = NEW.customer_id AND balance_type = NEW.balance_type;
    IF bal + NEW.amount < 0 THEN
      RAISE EXCEPTION 'KITE: the customer has % store credit; % cannot be used', bal, -NEW.amount USING ERRCODE = 'KT032';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER credit_ledger_check BEFORE INSERT ON crm.credit_ledger FOR EACH ROW EXECUTE FUNCTION crm.credit_ledger_check();

-- ================================================================ tenders and the drawer
ALTER TABLE sales.payment DROP CONSTRAINT payment_method_check;
ALTER TABLE sales.payment ADD CONSTRAINT payment_method_check CHECK (method IN ('cash','card','bank_transfer','trade_in','store_credit'));
ALTER TABLE sales.payment ADD COLUMN tradein_id uuid REFERENCES tradein.tradein(id);
ALTER TABLE sales.payment ADD COLUMN credit_entry_id uuid REFERENCES crm.credit_ledger(id);
ALTER TABLE sales.payment ADD CONSTRAINT payment_tradein CHECK ((method = 'trade_in') = (tradein_id IS NOT NULL));
ALTER TABLE sales.payment ADD CONSTRAINT payment_store_credit CHECK ((method = 'store_credit') = (credit_entry_id IS NOT NULL));

-- A cash buy-back leaves the drawer like a pay-out, against the trade-in.
ALTER TABLE sales.cash_event DROP CONSTRAINT cash_event_kind_check;
ALTER TABLE sales.cash_event ADD CONSTRAINT cash_event_kind_check CHECK (kind IN ('pay_in','pay_out','drop','no_sale','tradein_payout'));
ALTER TABLE sales.cash_event ADD COLUMN tradein_id uuid REFERENCES tradein.tradein(id);
ALTER TABLE sales.cash_event ADD CONSTRAINT cash_event_tradein CHECK ((kind = 'tradein_payout') = (tradein_id IS NOT NULL));
ALTER TABLE tradein.settlement ADD CONSTRAINT settlement_cash_event FOREIGN KEY (cash_event_id) REFERENCES sales.cash_event(id);
ALTER TABLE tradein.settlement ADD CONSTRAINT settlement_credit_entry FOREIGN KEY (credit_entry_id) REFERENCES crm.credit_ledger(id);

-- ================================================================ margin-scheme sale lines (12.3)
-- A line sold under the margin scheme keeps the purchase price it was taxed against; its tax code is VAT-MARGIN.
ALTER TABLE sales.sale_line ADD COLUMN tax_scheme text NOT NULL DEFAULT 'standard' CHECK (tax_scheme IN ('standard','margin'));
ALTER TABLE sales.sale_line ADD COLUMN margin_purchase_price numeric(18,3);
ALTER TABLE sales.sale_line ADD CONSTRAINT sale_line_margin CHECK ((tax_scheme = 'margin') = (margin_purchase_price IS NOT NULL));

-- ================================================================ device acquisitions
-- How a device came into the business each time, when it matters later: trade-ins and buy-backs (the purchase price
-- the margin scheme taxes against). The latest row is the current acquisition.
CREATE TABLE device.acquisition (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  device_unit_id   uuid NOT NULL REFERENCES device.device_unit(id),
  source           text NOT NULL CHECK (source IN ('trade_in','buy_back')),
  source_doc_type  text NOT NULL,
  source_doc_id    uuid NOT NULL,
  cost             numeric(18,3) NOT NULL CHECK (cost >= 0),
  margin_scheme    boolean NOT NULL,
  acquired_at      timestamptz NOT NULL DEFAULT now(),
  created_by       uuid
);
CREATE INDEX acquisition_unit ON device.acquisition (device_unit_id, acquired_at DESC);
CREATE TRIGGER acquisition_immutable BEFORE UPDATE OR DELETE ON device.acquisition FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

ALTER TABLE inv.stock_movement DROP CONSTRAINT stock_movement_movement_type_check;
ALTER TABLE inv.stock_movement ADD CONSTRAINT stock_movement_movement_type_check CHECK (movement_type IN
  ('opening','adjustment','count','sale','return','emergency_sale','purchase_receipt','supplier_return','transfer_out','transfer_in',
   'unit_cost','refurb_issue','refurb_return','tradein_acquisition'));

-- A device the business sold comes back as a trade-in and continues the same unit (11.1, 11.3).
ALTER TABLE device.device_event DROP CONSTRAINT device_event_event_type_check;
ALTER TABLE device.device_event ADD CONSTRAINT device_event_event_type_check CHECK (event_type IN
  ('received','inspected','cost_added','refurb_completed','sold','sale_voided','returned','written_off','held','released','price_set',
   'supplier_returned','transferred_out','transferred_in','identifier_corrected','receipt_reversed','traded_in'));
ALTER TABLE device.transition DISABLE TRIGGER device_transition_immutable;
INSERT INTO device.transition (from_status, to_status, event_type) VALUES ('sold','under_inspection','traded_in');
ALTER TABLE device.transition ENABLE TRIGGER device_transition_immutable;

-- Reconnects a sold unit as a trade-in at the branch that buys it: under inspection, at the agreed value as its new
-- cost. The event's cost change keeps "cost = acquisition + the costs in its history" true; the caller then moves
-- it into stock at that cost. Returns the event's id.
CREATE FUNCTION device.reacquire(p_unit uuid, p_branch uuid, p_warehouse uuid, p_cost numeric, p_condition text,
                                 p_doc_type text, p_doc_id uuid, p_details jsonb, p_actor uuid)
RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  u        device.device_unit%ROWTYPE;
  next_seq integer;
  ev       uuid := gen_random_uuid();
BEGIN
  SELECT * INTO u FROM device.device_unit WHERE id = p_unit FOR UPDATE;
  IF NOT FOUND OR NOT core.branch_writable(p_branch) THEN
    RAISE EXCEPTION 'KITE: device unit % cannot be bought back at that branch', p_unit USING ERRCODE = '42501';
  END IF;
  IF u.status <> 'sold' THEN
    RAISE EXCEPTION 'KITE: a % device cannot be traded in', replace(u.status, '_', ' ') USING ERRCODE = 'KT020';
  END IF;
  IF p_condition NOT IN ('used','damaged') THEN
    RAISE EXCEPTION 'KITE: a traded-in device is used or damaged, not %', p_condition USING ERRCODE = 'KT020';
  END IF;
  IF p_cost IS NULL OR p_cost < 0 OR p_cost <> round(p_cost, 3) THEN
    RAISE EXCEPTION 'KITE: the agreed value is zero or more, in baisa' USING ERRCODE = 'KT020';
  END IF;
  PERFORM 1 FROM core.warehouse w WHERE w.id = p_warehouse AND w.branch_id = p_branch;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'KITE: warehouse % is not in branch %', p_warehouse, p_branch USING ERRCODE = 'KT020';
  END IF;

  UPDATE device.device_unit
     SET status = 'under_inspection', held_from_status = NULL, branch_id = p_branch, warehouse_id = p_warehouse,
         condition = p_condition, grade_id = NULL, selling_price = NULL, warranty_days = NULL, sold_sale_id = NULL, sold_at = NULL,
         accumulated_cost = p_cost, updated_by = p_actor
   WHERE id = p_unit;

  SELECT coalesce(max(seq), 0) + 1 INTO next_seq FROM device.device_event WHERE device_unit_id = p_unit;
  INSERT INTO device.device_event (id, business_id, branch_id, device_unit_id, seq, event_type, from_status, to_status, from_condition,
                                   to_condition, grade_id, cost_delta, selling_price, source_doc_type, source_doc_id, details, actor_id)
  VALUES (ev, u.business_id, p_branch, p_unit, next_seq, 'traded_in', 'sold', 'under_inspection', u.condition, p_condition, NULL,
          p_cost - u.accumulated_cost, NULL, p_doc_type, p_doc_id, p_details, p_actor);
  RETURN ev;
END $$;

-- The purchase price a unit's sale is taxed against under the margin scheme, or null when it is taxed in full:
-- its latest acquisition was a trade-in or buy-back under the scheme.
CREATE FUNCTION device.margin_purchase_price(p_unit uuid) RETURNS numeric
LANGUAGE sql STABLE AS $$
  SELECT CASE WHEN a.margin_scheme THEN a.cost END
    FROM device.acquisition a WHERE a.device_unit_id = p_unit ORDER BY a.acquired_at DESC, a.id DESC LIMIT 1
$$;

-- At head office: a sold device that comes back in elsewhere supersedes its old record instead of opening a conflict.
ALTER TABLE device.central_registry ADD COLUMN retired_reason text;

-- ================================================================ checks for the books
-- Trade-ins with value still to be settled: their total is what trade-in clearing (2320) holds.
CREATE FUNCTION tradein.unsettled()
RETURNS TABLE (tradein_id uuid, number text, branch_id uuid, outstanding numeric)
LANGUAGE sql STABLE AS $$
  SELECT t.id, t.number, t.branch_id, t.agreed_value - coalesce(sum(s.amount), 0)
    FROM tradein.tradein t LEFT JOIN tradein.settlement s ON s.tradein_id = t.id
   WHERE t.status IN ('accepted','settled')
   GROUP BY t.id
  HAVING t.agreed_value - coalesce(sum(s.amount), 0) <> 0
$$;

-- ================================================================ grants, row-level security
REVOKE DELETE, TRUNCATE ON tradein.tradein, tradein.settlement, tradein.price_guide, tradein.deduction, tradein.settings FROM kite_app;
REVOKE UPDATE, DELETE, TRUNCATE ON tradein.settlement, crm.credit_ledger, device.acquisition FROM kite_app;
GRANT SELECT, INSERT ON crm.credit_ledger, device.acquisition, tax.margin_scheme TO kite_app;
GRANT UPDATE ON tax.margin_scheme TO kite_app;
GRANT SELECT ON crm.credit_ledger, device.acquisition, tax.margin_scheme TO kite_ro;
REVOKE EXECUTE ON FUNCTION device.reacquire(uuid, uuid, uuid, numeric, text, text, uuid, jsonb, uuid), crm.store_credit_balance(uuid),
                           tradein.settlement_applied(), crm.credit_ledger_check() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION device.reacquire(uuid, uuid, uuid, numeric, text, text, uuid, jsonb, uuid), crm.store_credit_balance(uuid) TO kite_app;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['tradein.tradein','tradein.tradein_issue','tradein.settlement','crm.credit_ledger','device.acquisition'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY branch_read ON %s FOR SELECT USING (core.branch_visible(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_insert ON %s FOR INSERT WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_update ON %s FOR UPDATE USING (core.branch_visible(branch_id)) WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_delete ON %s FOR DELETE USING (core.branch_writable(branch_id))', t);
  END LOOP;
END
$$;

-- ================================================================ defaults for existing businesses
INSERT INTO tax.margin_scheme (business_id) SELECT id FROM core.business ON CONFLICT DO NOTHING;
INSERT INTO tradein.settings (business_id, declaration_en, declaration_ar)
SELECT id,
       'I confirm that I own this device and may sell it; that it is not stolen, lost, under finance or locked; that I have removed my accounts and data from it; and that I sell it to the business for the agreed value.',
       'أقر بأنني مالك هذا الجهاز ويحق لي بيعه، وأنه غير مسروق أو مفقود أو مرهون أو مقفل، وأنني أزلت حساباتي وبياناتي منه، وأنني أبيعه للمنشأة بالقيمة المتفق عليها.'
  FROM core.business ON CONFLICT DO NOTHING;

-- Trade-in, self-invoice and store-credit numbers for every existing branch and fiscal year that already has series.
INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year)
SELECT gen_random_uuid(), s.business_id, s.branch_id, d.doc_type, d.prefix || '-' || br.code || '-' || s.fiscal_year || '-', s.fiscal_year
  FROM (SELECT DISTINCT business_id, branch_id, fiscal_year FROM core.number_series WHERE branch_id IS NOT NULL AND terminal_id IS NULL) s
  JOIN core.branch br ON br.id = s.branch_id
 CROSS JOIN (VALUES ('trade_in','TI'), ('margin_self_invoice','SI'), ('store_credit','SC')) AS d(doc_type, prefix)
ON CONFLICT DO NOTHING;

-- ================================================================ permissions
INSERT INTO iam.permission (code, module_code, description_en, description_ar) VALUES
  ('TRADEIN.CREATE',       'tradein', 'Take devices in as trade-ins: record, inspect, value and accept within the staff limit', 'استلام الأجهزة كاستبدال: تسجيلها وفحصها وتقييمها وقبولها ضمن حد الموظف'),
  ('TRADEIN.APPROVE',      'tradein', 'Approve trade-in values above the staff limit or the price guide',                     'اعتماد قيم الاستبدال التي تتجاوز حد الموظف أو دليل الأسعار'),
  ('TRADEIN.STORE_CREDIT', 'tradein', 'Settle a trade-in as the customer''s store credit',                                     'تسوية الاستبدال كرصيد للعميل لدى المتجر'),
  ('TRADEIN.PAYOUT',       'tradein', 'Pay a trade-in out in cash from the drawer (buy-back)',                                 'دفع قيمة الاستبدال نقدًا من الدرج (شراء)'),
  ('TRADEIN.VIEW',         'tradein', 'View trade-ins, their documents and reports',                                           'عرض عمليات الاستبدال ومستنداتها وتقاريرها'),
  ('TRADEIN.SETTINGS',     'tradein', 'Manage the trade-in price guide, deductions, limits and declaration',                   'إدارة دليل أسعار الاستبدال والخصومات والحدود والإقرار');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (SELECT code FROM iam.permission WHERE code LIKE 'TRADEIN.%') p
 WHERE r.code IN ('owner','branch_manager')
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('TRADEIN.CREATE'),('TRADEIN.VIEW')) AS p(code)
 WHERE r.code IN ('cashier','senior_cashier')
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, 'TRADEIN.VIEW' FROM iam.role r WHERE r.code = 'accountant'
ON CONFLICT DO NOTHING;
