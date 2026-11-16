-- V0012: Phase 4 - catalog, customers, inventory ledger, shifts, sales and returns, approvals, POS policy,
-- emergency intake and the default posting rules (blueprint 9.3, 9.4, 10, 16.3, 17).
--
-- Serialized products (IMEI or serial tracking) can be defined in the catalog but cannot move in stock until
-- device units arrive in Phase 5; the stock ledger refuses them with KT010.

CREATE SCHEMA IF NOT EXISTS catalog;
CREATE SCHEMA IF NOT EXISTS crm;
CREATE SCHEMA IF NOT EXISTS inv;
CREATE SCHEMA IF NOT EXISTS sales;

DO $$
DECLARE s text;
BEGIN
  FOREACH s IN ARRAY ARRAY['catalog','crm','inv','sales'] LOOP
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO kite_app, kite_ro', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO kite_app', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT ON TABLES TO kite_ro', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT EXECUTE ON FUNCTIONS TO kite_app', s);
  END LOOP;
END
$$;

-- ================================================================ catalog
CREATE TABLE catalog.brand (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  name_en      text NOT NULL CHECK (length(trim(name_en)) > 0),
  name_ar      text NOT NULL CHECK (length(trim(name_ar)) > 0),
  status       text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   uuid,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid,
  version      integer NOT NULL DEFAULT 1,
  origin_server_id uuid
);
CREATE UNIQUE INDEX brand_name_unique ON catalog.brand (business_id, lower(name_en));
CREATE TRIGGER brand_touch BEFORE UPDATE ON catalog.brand FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE catalog.category (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  parent_id    uuid REFERENCES catalog.category(id),
  name_en      text NOT NULL CHECK (length(trim(name_en)) > 0),
  name_ar      text NOT NULL CHECK (length(trim(name_ar)) > 0),
  status       text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   uuid,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid,
  version      integer NOT NULL DEFAULT 1,
  origin_server_id uuid,
  CHECK (parent_id IS NULL OR parent_id <> id)
);
CREATE UNIQUE INDEX category_name_unique ON catalog.category (business_id, coalesce(parent_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(name_en));
CREATE TRIGGER category_touch BEFORE UPDATE ON catalog.category FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE catalog.product (
  id                     uuid PRIMARY KEY,
  business_id            uuid NOT NULL REFERENCES core.business(id),
  brand_id               uuid REFERENCES catalog.brand(id),
  category_id            uuid REFERENCES catalog.category(id),
  name_en                text NOT NULL CHECK (length(trim(name_en)) > 0),
  name_ar                text NOT NULL CHECK (length(trim(name_ar)) > 0),
  product_type           text NOT NULL CHECK (product_type IN ('device','accessory','spare_part','service','consumable')),
  tracking               text NOT NULL DEFAULT 'none' CHECK (tracking IN ('none','imei','serial')),
  tax_code_id            uuid NOT NULL REFERENCES tax.tax_code(id),
  default_warranty_days  integer NOT NULL DEFAULT 0 CHECK (default_warranty_days BETWEEN 0 AND 3650),
  status                 text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at             timestamptz NOT NULL DEFAULT now(),
  created_by             uuid,
  updated_at             timestamptz NOT NULL DEFAULT now(),
  updated_by             uuid,
  version                integer NOT NULL DEFAULT 1,
  origin_server_id       uuid,
  CHECK (product_type <> 'service' OR tracking = 'none'),
  CHECK (tracking <> 'imei' OR product_type = 'device')
);
CREATE TRIGGER product_touch BEFORE UPDATE ON catalog.product FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE catalog.variant (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  product_id   uuid NOT NULL REFERENCES catalog.product(id),
  sku          text NOT NULL CHECK (sku ~ '^[A-Za-z0-9._/-]{1,40}$'),
  colour_en    text,
  colour_ar    text,
  storage_gb   integer CHECK (storage_gb IS NULL OR storage_gb > 0),
  ram_gb       integer CHECK (ram_gb IS NULL OR ram_gb > 0),
  status       text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   uuid,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid,
  version      integer NOT NULL DEFAULT 1,
  origin_server_id uuid
);
CREATE UNIQUE INDEX variant_sku_unique ON catalog.variant (business_id, upper(sku));
CREATE INDEX variant_product ON catalog.variant (product_id);
CREATE TRIGGER variant_touch BEFORE UPDATE ON catalog.variant FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE catalog.barcode (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  variant_id   uuid NOT NULL REFERENCES catalog.variant(id),
  code         text NOT NULL CHECK (code ~ '^[A-Za-z0-9._/-]{3,64}$'),
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   uuid,
  origin_server_id uuid,
  UNIQUE (business_id, code)
);
CREATE INDEX barcode_variant ON catalog.barcode (variant_id);

CREATE TABLE catalog.price_list (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  code         text NOT NULL CHECK (code ~ '^[A-Z0-9-]{2,20}$'),
  name_en      text NOT NULL,
  name_ar      text NOT NULL,
  is_default   boolean NOT NULL DEFAULT false,
  status       text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   uuid,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid,
  version      integer NOT NULL DEFAULT 1,
  UNIQUE (business_id, code)
);
CREATE UNIQUE INDEX price_list_one_default ON catalog.price_list (business_id) WHERE is_default;
CREATE TRIGGER price_list_touch BEFORE UPDATE ON catalog.price_list FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- Prices are effective-dated and never overlap for one variant on one list (9.4). A new price closes the old one.
CREATE TABLE catalog.price (
  id              uuid PRIMARY KEY,
  price_list_id   uuid NOT NULL REFERENCES catalog.price_list(id),
  variant_id      uuid NOT NULL REFERENCES catalog.variant(id),
  price           numeric(18,3) NOT NULL CHECK (price >= 0),
  min_price       numeric(18,3) CHECK (min_price IS NULL OR (min_price >= 0)),
  effective_from  date NOT NULL,
  effective_to    date,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      uuid,
  updated_at      timestamptz NOT NULL DEFAULT now(),
  updated_by      uuid,
  version         integer NOT NULL DEFAULT 1,
  CHECK (effective_to IS NULL OR effective_to > effective_from),
  CHECK (min_price IS NULL OR min_price <= price),
  CONSTRAINT price_no_overlap EXCLUDE USING gist (
    price_list_id WITH =, variant_id WITH =,
    daterange(effective_from, coalesce(effective_to, 'infinity'::date), '[)') WITH &&
  )
);
CREATE TRIGGER price_touch BEFORE UPDATE ON catalog.price FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE catalog.branch_variant (
  id                  uuid PRIMARY KEY,
  business_id         uuid NOT NULL REFERENCES core.business(id),
  branch_id           uuid NOT NULL REFERENCES core.branch(id),
  variant_id          uuid NOT NULL REFERENCES catalog.variant(id),
  override_price      numeric(18,3) CHECK (override_price IS NULL OR override_price >= 0),
  override_min_price  numeric(18,3) CHECK (override_min_price IS NULL OR override_min_price >= 0),
  reorder_level       numeric(14,3) CHECK (reorder_level IS NULL OR reorder_level >= 0),
  bin_location        text,
  created_at          timestamptz NOT NULL DEFAULT now(),
  created_by          uuid,
  updated_at          timestamptz NOT NULL DEFAULT now(),
  updated_by          uuid,
  version             integer NOT NULL DEFAULT 1,
  UNIQUE (branch_id, variant_id),
  CHECK (override_min_price IS NULL OR override_price IS NULL OR override_min_price <= override_price)
);
CREATE TRIGGER branch_variant_touch BEFORE UPDATE ON catalog.branch_variant FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- ================================================================ customers (credit arrives in Phase 10)
CREATE TABLE crm.customer (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  number            text NOT NULL,
  kind              text NOT NULL DEFAULT 'individual' CHECK (kind IN ('individual','company')),
  name_en           text NOT NULL CHECK (length(trim(name_en)) > 0),
  name_ar           text,
  phone_e164        text CHECK (phone_e164 IS NULL OR phone_e164 ~ '^\+[1-9][0-9]{6,14}$'),
  email             text CHECK (email IS NULL OR email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'),
  vat_number        text,
  cr_number         text,
  address_en        text,
  address_ar        text,
  origin_branch_id  uuid REFERENCES core.branch(id),
  merged_into_id    uuid REFERENCES crm.customer(id),
  status            text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive','merged')),
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        uuid,
  updated_at        timestamptz NOT NULL DEFAULT now(),
  updated_by        uuid,
  version           integer NOT NULL DEFAULT 1,
  origin_server_id  uuid,
  UNIQUE (business_id, number),
  CHECK ((status = 'merged') = (merged_into_id IS NOT NULL))
);
CREATE INDEX customer_phone ON crm.customer (phone_e164) WHERE phone_e164 IS NOT NULL;
CREATE INDEX customer_name ON crm.customer (lower(name_en));
CREATE TRIGGER customer_touch BEFORE UPDATE ON crm.customer FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- ================================================================ inventory
-- Balance projection, one row per branch, warehouse and variant. Rebuildable from the ledger; changed only by
-- the ledger trigger below (kite_app has no write grant on it).
CREATE TABLE inv.stock_balance (
  business_id       uuid NOT NULL REFERENCES core.business(id),
  branch_id         uuid NOT NULL REFERENCES core.branch(id),
  warehouse_id      uuid NOT NULL REFERENCES core.warehouse(id),
  variant_id        uuid NOT NULL REFERENCES catalog.variant(id),
  qty               numeric(14,3) NOT NULL DEFAULT 0,
  value             numeric(18,3) NOT NULL DEFAULT 0,      -- sum of movement values: the inventory valuation
  avg_cost          numeric(18,6) NOT NULL DEFAULT 0,      -- value / qty while qty > 0, else the last average
  movement_count    bigint NOT NULL DEFAULT 0,
  last_movement_at  timestamptz,
  PRIMARY KEY (branch_id, warehouse_id, variant_id)
);

-- The stock ledger (9.3, 9.4): append-only, signed quantities, cost per movement. Weighted average per branch
-- warehouse for non-serialized items (16.3): an outgoing movement takes its share of the balance value, so the
-- valuation always equals the sum of movement values and returns to exactly zero with the quantity.
CREATE TABLE inv.stock_movement (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  branch_id         uuid NOT NULL REFERENCES core.branch(id),
  warehouse_id      uuid NOT NULL REFERENCES core.warehouse(id),
  variant_id        uuid NOT NULL REFERENCES catalog.variant(id),
  movement_type     text NOT NULL CHECK (movement_type IN ('opening','adjustment','count','sale','return','emergency_sale',
                                                            'purchase_receipt','supplier_return','transfer_out','transfer_in')),
  qty               numeric(14,3) NOT NULL CHECK (qty <> 0),
  unit_cost         numeric(18,6),                         -- given for incoming movements; set by the trigger for outgoing
  value             numeric(18,3),                         -- signed; set by the trigger
  qty_after         numeric(14,3),
  value_after       numeric(18,3),
  source_doc_type   text NOT NULL,
  source_doc_id     uuid NOT NULL,
  source_line_id    uuid,
  reversal_of_id    uuid UNIQUE REFERENCES inv.stock_movement(id),
  reason            text,
  movement_at       timestamptz NOT NULL DEFAULT now(),
  created_by        uuid,
  origin_server_id  uuid,
  origin_device_id  uuid,
  CHECK (unit_cost IS NULL OR unit_cost >= 0)
);
CREATE INDEX stock_movement_variant ON inv.stock_movement (branch_id, variant_id, movement_at);
CREATE INDEX stock_movement_source ON inv.stock_movement (source_doc_type, source_doc_id);
CREATE UNIQUE INDEX stock_movement_one_per_source_line ON inv.stock_movement (source_doc_type, source_line_id, movement_type)
  WHERE source_line_id IS NOT NULL AND reversal_of_id IS NULL;

CREATE OR REPLACE FUNCTION inv.apply_movement() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  b        inv.stock_balance%ROWTYPE;
  p_type   text;
  p_track  text;
  out_qty  numeric;
BEGIN
  SELECT p.product_type, p.tracking INTO p_type, p_track
    FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id WHERE v.id = NEW.variant_id;
  IF p_track IS DISTINCT FROM 'none' THEN
    RAISE EXCEPTION 'KITE: % is serialized; stock movements of serialized products need device units (Phase 5)', NEW.variant_id
      USING ERRCODE = 'KT010';
  END IF;
  IF p_type = 'service' THEN
    RAISE EXCEPTION 'KITE: services are not stocked' USING ERRCODE = 'KT010';
  END IF;
  PERFORM 1 FROM core.warehouse w WHERE w.id = NEW.warehouse_id AND w.branch_id = NEW.branch_id;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'KITE: warehouse % is not in branch %', NEW.warehouse_id, NEW.branch_id USING ERRCODE = 'KT010';
  END IF;

  INSERT INTO inv.stock_balance (business_id, branch_id, warehouse_id, variant_id)
  VALUES (NEW.business_id, NEW.branch_id, NEW.warehouse_id, NEW.variant_id)
  ON CONFLICT (branch_id, warehouse_id, variant_id) DO NOTHING;
  SELECT * INTO b FROM inv.stock_balance
   WHERE branch_id = NEW.branch_id AND warehouse_id = NEW.warehouse_id AND variant_id = NEW.variant_id
   FOR UPDATE;

  IF NEW.qty > 0 THEN
    IF NEW.unit_cost IS NULL THEN
      NEW.unit_cost := b.avg_cost;                       -- e.g. a count gain at the current average
    END IF;
    NEW.value := round(NEW.qty * NEW.unit_cost, 3);
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
CREATE TRIGGER stock_movement_apply BEFORE INSERT ON inv.stock_movement FOR EACH ROW EXECUTE FUNCTION inv.apply_movement();
CREATE TRIGGER stock_movement_immutable BEFORE UPDATE OR DELETE ON inv.stock_movement FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
CREATE TRIGGER stock_movement_no_truncate BEFORE TRUNCATE ON inv.stock_movement FOR EACH STATEMENT EXECUTE FUNCTION core.forbid_change();

-- Every balance equals its ledger (23.2 "Inventory consistency"). Returns the rows that do not.
CREATE OR REPLACE FUNCTION inv.check_balances()
RETURNS TABLE (branch_id uuid, warehouse_id uuid, variant_id uuid, balance_qty numeric, ledger_qty numeric, balance_value numeric, ledger_value numeric)
LANGUAGE sql STABLE AS $$     -- invoker rights: row-level security limits it to the caller's branches
  WITH l AS (
    SELECT m.branch_id, m.warehouse_id, m.variant_id, sum(m.qty) AS qty, sum(m.value) AS value
      FROM inv.stock_movement m GROUP BY 1, 2, 3
  )
  SELECT coalesce(b.branch_id, l.branch_id), coalesce(b.warehouse_id, l.warehouse_id), coalesce(b.variant_id, l.variant_id),
         b.qty, l.qty, b.value, l.value
    FROM inv.stock_balance b
    FULL JOIN l ON l.branch_id = b.branch_id AND l.warehouse_id = b.warehouse_id AND l.variant_id = b.variant_id
   WHERE coalesce(b.qty, 0) <> coalesce(l.qty, 0) OR coalesce(b.value, 0) <> coalesce(l.value, 0)
$$;

-- Stock adjustments and opening stock: posted documents, never edited.
CREATE TABLE inv.adjustment (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  branch_id         uuid NOT NULL REFERENCES core.branch(id),
  warehouse_id      uuid NOT NULL REFERENCES core.warehouse(id),
  number            text NOT NULL,
  kind              text NOT NULL CHECK (kind IN ('opening','adjustment')),
  adjustment_date   date NOT NULL,
  note              text,
  posted_at         timestamptz NOT NULL DEFAULT now(),
  posted_by         uuid NOT NULL,
  origin_server_id  uuid,
  UNIQUE (business_id, number)
);
CREATE TABLE inv.adjustment_line (
  id             uuid PRIMARY KEY,
  adjustment_id  uuid NOT NULL REFERENCES inv.adjustment(id),
  line_no        smallint NOT NULL CHECK (line_no > 0),
  variant_id     uuid NOT NULL REFERENCES catalog.variant(id),
  qty            numeric(14,3) NOT NULL CHECK (qty <> 0),
  unit_cost      numeric(18,6) CHECK (unit_cost IS NULL OR unit_cost >= 0),
  reason_code    text NOT NULL CHECK (reason_code IN ('opening','damaged','lost','found','expired','correction','other')),
  UNIQUE (adjustment_id, line_no)
);

-- Stock counts: lines are counted while the count is open; posting writes the differences to the ledger.
CREATE TABLE inv.stock_count (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  branch_id         uuid NOT NULL REFERENCES core.branch(id),
  warehouse_id      uuid NOT NULL REFERENCES core.warehouse(id),
  number            text NOT NULL,
  status            text NOT NULL DEFAULT 'open' CHECK (status IN ('open','posted','cancelled')),
  note              text,
  started_at        timestamptz NOT NULL DEFAULT now(),
  started_by        uuid NOT NULL,
  posted_at         timestamptz,
  posted_by         uuid,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  version           integer NOT NULL DEFAULT 1,
  origin_server_id  uuid,
  UNIQUE (business_id, number),
  CHECK ((status = 'posted') = (posted_at IS NOT NULL))
);
CREATE TRIGGER stock_count_touch BEFORE UPDATE ON inv.stock_count FOR EACH ROW EXECUTE FUNCTION core.touch_row();
CREATE TABLE inv.stock_count_line (
  id              uuid PRIMARY KEY,
  stock_count_id  uuid NOT NULL REFERENCES inv.stock_count(id),
  variant_id      uuid NOT NULL REFERENCES catalog.variant(id),
  counted_qty     numeric(14,3) NOT NULL CHECK (counted_qty >= 0),
  system_qty      numeric(14,3),                -- set when the count is posted
  difference      numeric(14,3),
  counted_by      uuid,
  counted_at      timestamptz NOT NULL DEFAULT now(),
  UNIQUE (stock_count_id, variant_id)
);

-- Exceptions the shop must look at, e.g. stock driven negative by an emergency sale recovered later (6.4).
CREATE TABLE inv.stock_exception (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  branch_id         uuid NOT NULL REFERENCES core.branch(id),
  warehouse_id      uuid NOT NULL REFERENCES core.warehouse(id),
  variant_id        uuid NOT NULL REFERENCES catalog.variant(id),
  kind              text NOT NULL CHECK (kind IN ('negative_stock')),
  qty_after         numeric(14,3) NOT NULL,
  source_doc_type   text NOT NULL,
  source_doc_id     uuid NOT NULL,
  status            text NOT NULL DEFAULT 'open' CHECK (status IN ('open','resolved')),
  resolved_at       timestamptz,
  resolved_by       uuid,
  resolution_note   text,
  created_at        timestamptz NOT NULL DEFAULT now()
);

-- ================================================================ POS policy (defaults the owner can change)
CREATE TABLE sales.pos_policy (
  id                          uuid PRIMARY KEY,
  business_id                 uuid NOT NULL REFERENCES core.business(id),
  branch_id                   uuid REFERENCES core.branch(id),         -- NULL = business default
  negative_stock              text NOT NULL DEFAULT 'block' CHECK (negative_stock IN ('block','allow')),
  return_window_days          integer NOT NULL DEFAULT 14 CHECK (return_window_days BETWEEN 0 AND 3650),
  cash_refund_cap             numeric(18,3) NOT NULL DEFAULT 100.000 CHECK (cash_refund_cap >= 0),
  variance_threshold          numeric(18,3) NOT NULL DEFAULT 1.000 CHECK (variance_threshold >= 0),
  blind_close                 boolean NOT NULL DEFAULT true,
  rounding_level              text NOT NULL DEFAULT 'line' CHECK (rounding_level IN ('line','invoice')),
  cash_rounding               numeric(6,3) NOT NULL DEFAULT 0 CHECK (cash_rounding IN (0, 0.005, 0.010, 0.050, 0.100)),
  denominations               numeric(18,3)[] NOT NULL DEFAULT '{50,20,10,5,1,0.5,0.1,0.05,0.025,0.01,0.005}',
  receipt_footer_en           text,
  receipt_footer_ar           text,
  created_at                  timestamptz NOT NULL DEFAULT now(),
  created_by                  uuid,
  updated_at                  timestamptz NOT NULL DEFAULT now(),
  updated_by                  uuid,
  version                     integer NOT NULL DEFAULT 1,
  CONSTRAINT pos_policy_scope_unique UNIQUE NULLS NOT DISTINCT (business_id, branch_id)
);
CREATE TRIGGER pos_policy_touch BEFORE UPDATE ON sales.pos_policy FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- Cashier discount limits per role (10.2); holders of POS.DISCOUNT.ABOVE_LIMIT have none.
ALTER TABLE iam.role
  ADD COLUMN discount_limit_percent numeric(5,2) NOT NULL DEFAULT 0 CHECK (discount_limit_percent BETWEEN 0 AND 100),
  ADD COLUMN discount_limit_amount  numeric(18,3) NOT NULL DEFAULT 0 CHECK (discount_limit_amount >= 0);
UPDATE iam.role SET discount_limit_percent = 5,  discount_limit_amount = 2.000  WHERE code = 'cashier';
UPDATE iam.role SET discount_limit_percent = 10, discount_limit_amount = 10.000 WHERE code = 'senior_cashier';

-- ================================================================ manager approvals (10.2, 18.3)
CREATE TABLE iam.approval (
  id             uuid PRIMARY KEY,
  business_id    uuid NOT NULL REFERENCES core.business(id),
  branch_id      uuid NOT NULL REFERENCES core.branch(id),
  terminal_id    uuid REFERENCES core.terminal(id),
  action_code    text NOT NULL REFERENCES iam.permission(code),
  method         text NOT NULL CHECK (method IN ('pin','password','remote')),
  status         text NOT NULL CHECK (status IN ('pending','approved','rejected','used','expired')),
  requester_id   uuid NOT NULL REFERENCES iam."user"(id),
  approver_id    uuid REFERENCES iam."user"(id),
  context        jsonb NOT NULL DEFAULT '{}',
  reason         text,
  decided_at     timestamptz,
  expires_at     timestamptz NOT NULL,
  used_at        timestamptz,
  used_by_doc_type text,
  used_by_doc_id uuid,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  version        integer NOT NULL DEFAULT 1,
  CHECK (status IN ('pending','expired') OR approver_id IS NOT NULL),
  CHECK (approver_id IS NULL OR approver_id <> requester_id),
  CHECK ((status = 'used') = (used_at IS NOT NULL))
);
CREATE INDEX approval_pending ON iam.approval (branch_id, created_at) WHERE status = 'pending';
CREATE TRIGGER approval_touch BEFORE UPDATE ON iam.approval FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- ================================================================ shifts and cash (10.4)
CREATE TABLE sales.shift (
  id                     uuid PRIMARY KEY,
  business_id            uuid NOT NULL REFERENCES core.business(id),
  branch_id              uuid NOT NULL REFERENCES core.branch(id),
  terminal_id            uuid NOT NULL REFERENCES core.terminal(id),
  cashier_id             uuid NOT NULL REFERENCES iam."user"(id),
  number                 text NOT NULL,
  status                 text NOT NULL DEFAULT 'open' CHECK (status IN ('open','closed')),
  is_emergency           boolean NOT NULL DEFAULT false,
  opened_at              timestamptz NOT NULL DEFAULT now(),
  opening_float          numeric(18,3) NOT NULL CHECK (opening_float >= 0),
  opening_count          jsonb NOT NULL DEFAULT '{}',          -- denomination -> pieces
  closed_at              timestamptz,
  closed_by              uuid REFERENCES iam."user"(id),
  expected_cash          numeric(18,3),
  counted_cash           numeric(18,3),
  closing_count          jsonb,
  variance               numeric(18,3),
  variance_reason        text,
  variance_accepted_by   uuid REFERENCES iam."user"(id),
  variance_accepted_at   timestamptz,
  totals                 jsonb,                                -- the Z report figures, frozen at close
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  version                integer NOT NULL DEFAULT 1,
  origin_server_id       uuid,
  UNIQUE (business_id, number),
  CHECK ((status = 'closed') = (closed_at IS NOT NULL)),
  CHECK (status = 'open' OR (expected_cash IS NOT NULL AND counted_cash IS NOT NULL AND variance IS NOT NULL))
);
CREATE UNIQUE INDEX shift_one_open_per_terminal ON sales.shift (terminal_id) WHERE status = 'open';
CREATE TRIGGER shift_touch BEFORE UPDATE ON sales.shift FOR EACH ROW EXECUTE FUNCTION core.touch_row();
-- A closed shift is final.
CREATE OR REPLACE FUNCTION sales.shift_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = 'closed' AND (NEW.status <> 'closed' OR NEW.counted_cash IS DISTINCT FROM OLD.counted_cash
       OR NEW.expected_cash IS DISTINCT FROM OLD.expected_cash OR NEW.variance IS DISTINCT FROM OLD.variance
       OR NEW.totals IS DISTINCT FROM OLD.totals) THEN
    RAISE EXCEPTION 'KITE: shift % is closed', OLD.number USING ERRCODE = 'KT001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER shift_closed_final BEFORE UPDATE ON sales.shift FOR EACH ROW EXECUTE FUNCTION sales.shift_guard();

CREATE TABLE sales.cash_event (
  id            uuid PRIMARY KEY,
  business_id   uuid NOT NULL REFERENCES core.business(id),
  branch_id     uuid NOT NULL REFERENCES core.branch(id),
  shift_id      uuid NOT NULL REFERENCES sales.shift(id),
  kind          text NOT NULL CHECK (kind IN ('pay_in','pay_out','drop','no_sale')),
  amount        numeric(18,3) NOT NULL CHECK (amount >= 0),
  reason        text NOT NULL CHECK (length(trim(reason)) > 0),
  approval_id   uuid REFERENCES iam.approval(id),
  created_at    timestamptz NOT NULL DEFAULT now(),
  created_by    uuid NOT NULL,
  CHECK ((kind = 'no_sale') = (amount = 0))
);
CREATE INDEX cash_event_shift ON sales.cash_event (shift_id);

-- Standalone bank terminals (owner decision, 10.4): the batch total each terminal printed at close.
CREATE TABLE sales.card_settlement (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  shift_id         uuid NOT NULL REFERENCES sales.shift(id),
  card_terminal    text NOT NULL CHECK (length(trim(card_terminal)) > 0),
  batch_total      numeric(18,3) NOT NULL,
  recorded_total   numeric(18,3) NOT NULL,
  difference       numeric(18,3) NOT NULL,
  reason           text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid NOT NULL,
  UNIQUE (shift_id, card_terminal),
  CHECK (difference = batch_total - recorded_total),
  CHECK (difference = 0 OR length(trim(coalesce(reason, ''))) > 0)
);

-- ================================================================ sales, returns and payments (10.2, 10.3)
-- A sale row exists only once it is completed: carts live on the terminal (or parked below) until then, so an
-- abandoned cart never takes a number. Credit notes (returns, voids) are sale rows of their own kind.
CREATE TABLE sales.sale (
  id                  uuid PRIMARY KEY,                 -- created by the terminal when the cart opens (idempotency key)
  business_id         uuid NOT NULL REFERENCES core.business(id),
  branch_id           uuid NOT NULL REFERENCES core.branch(id),
  warehouse_id        uuid NOT NULL REFERENCES core.warehouse(id),
  terminal_id         uuid NOT NULL REFERENCES core.terminal(id),
  shift_id            uuid NOT NULL REFERENCES sales.shift(id),
  cashier_id          uuid NOT NULL REFERENCES iam."user"(id),
  customer_id         uuid REFERENCES crm.customer(id),
  number              text NOT NULL,
  doc_type            text NOT NULL CHECK (doc_type IN ('simplified_invoice','tax_invoice','credit_note')),
  credit_reason       text CHECK (credit_reason IN ('return','void','exchange')),
  original_sale_id    uuid REFERENCES sales.sale(id),
  exchange_group_id   uuid,
  sale_date           date NOT NULL,
  completed_at        timestamptz NOT NULL DEFAULT now(),
  currency            char(3) NOT NULL DEFAULT 'OMR',
  prices_include_tax  boolean NOT NULL,
  net_total           numeric(18,3) NOT NULL,           -- before tax, after discounts
  discount_total      numeric(18,3) NOT NULL DEFAULT 0 CHECK (discount_total >= 0),
  tax_total           numeric(18,3) NOT NULL,
  gross_total         numeric(18,3) NOT NULL,           -- what the customer pays (or gets back), before cash rounding
  cash_rounding       numeric(18,3) NOT NULL DEFAULT 0,
  is_emergency        boolean NOT NULL DEFAULT false,
  emergency_number    text,
  einvoice_uuid       uuid NOT NULL UNIQUE,
  qr_payload          text NOT NULL,
  note                text,
  origin_server_id    uuid,
  origin_device_id    uuid,
  UNIQUE (business_id, number),
  CHECK ((doc_type = 'credit_note') = (credit_reason IS NOT NULL)),
  CHECK ((doc_type = 'credit_note') = (original_sale_id IS NOT NULL)),
  CHECK (NOT is_emergency OR number = emergency_number),
  CHECK (doc_type <> 'tax_invoice' OR customer_id IS NOT NULL),
  CHECK (net_total >= 0 AND tax_total >= 0 AND gross_total >= 0),
  CHECK (gross_total = net_total + tax_total),
  CHECK (is_emergency = (emergency_number IS NOT NULL))
);
CREATE INDEX sale_shift ON sales.sale (shift_id);
CREATE INDEX sale_original ON sales.sale (original_sale_id) WHERE original_sale_id IS NOT NULL;
CREATE INDEX sale_date ON sales.sale (branch_id, sale_date);
CREATE INDEX sale_customer ON sales.sale (customer_id) WHERE customer_id IS NOT NULL;

CREATE TABLE sales.sale_line (
  id                uuid PRIMARY KEY,
  sale_id           uuid NOT NULL REFERENCES sales.sale(id),
  line_no           smallint NOT NULL CHECK (line_no > 0),
  variant_id        uuid NOT NULL REFERENCES catalog.variant(id),
  product_type      text NOT NULL CHECK (product_type IN ('device','accessory','spare_part','service','consumable')),
  description_en    text NOT NULL,
  description_ar    text NOT NULL,
  qty               numeric(14,3) NOT NULL CHECK (qty > 0),
  unit_price        numeric(18,3) NOT NULL CHECK (unit_price >= 0),   -- as priced: tax-inclusive when the sale is
  list_price        numeric(18,3) NOT NULL CHECK (list_price >= 0),
  discount_amount   numeric(18,3) NOT NULL DEFAULT 0 CHECK (discount_amount >= 0),
  net_amount        numeric(18,3) NOT NULL CHECK (net_amount >= 0),    -- after discount, before tax
  tax_code_id       uuid NOT NULL REFERENCES tax.tax_code(id),
  tax_rate          numeric(7,4) NOT NULL CHECK (tax_rate >= 0),
  tax_amount        numeric(18,3) NOT NULL CHECK (tax_amount >= 0),
  gross_amount      numeric(18,3) NOT NULL CHECK (gross_amount >= 0),
  cost_value        numeric(18,3) NOT NULL DEFAULT 0,                  -- from the stock movement (0 for services)
  approval_id       uuid REFERENCES iam.approval(id),
  returned_line_id  uuid REFERENCES sales.sale_line(id),               -- on credit-note lines
  restock           boolean,                                           -- on credit-note lines: back to sellable stock?
  warranty_until    date,
  UNIQUE (sale_id, line_no),
  CHECK (gross_amount = net_amount + tax_amount)
);
CREATE INDEX sale_line_returned ON sales.sale_line (returned_line_id) WHERE returned_line_id IS NOT NULL;

-- A line can be returned at most as often as it was sold, across every credit note, even with two terminals at once.
CREATE OR REPLACE FUNCTION sales.check_return_qty() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE sold numeric; returned numeric; orig_variant uuid;
BEGIN
  IF NEW.returned_line_id IS NULL THEN RETURN NEW; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(NEW.returned_line_id::text, 0));
  SELECT l.qty, l.variant_id INTO sold, orig_variant
    FROM sales.sale_line l JOIN sales.sale s ON s.id = l.sale_id
   WHERE l.id = NEW.returned_line_id AND s.doc_type <> 'credit_note';
  IF NOT FOUND THEN
    RAISE EXCEPTION 'KITE: returned line % is not a sold line', NEW.returned_line_id USING ERRCODE = 'KT011';
  END IF;
  IF orig_variant <> NEW.variant_id THEN
    RAISE EXCEPTION 'KITE: a return must be of the item sold' USING ERRCODE = 'KT011';
  END IF;
  SELECT coalesce(sum(qty), 0) INTO returned FROM sales.sale_line WHERE returned_line_id = NEW.returned_line_id;
  IF returned + NEW.qty > sold THEN
    RAISE EXCEPTION 'KITE: % of % already returned; % more is too many', returned, sold, NEW.qty USING ERRCODE = 'KT011';
  END IF;
  RETURN NEW;
END
$$;
CREATE TRIGGER sale_line_return_qty BEFORE INSERT ON sales.sale_line FOR EACH ROW EXECUTE FUNCTION sales.check_return_qty();

CREATE TABLE sales.payment (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  branch_id         uuid NOT NULL REFERENCES core.branch(id),
  sale_id           uuid NOT NULL REFERENCES sales.sale(id),
  shift_id          uuid NOT NULL REFERENCES sales.shift(id),
  method            text NOT NULL CHECK (method IN ('cash','card','bank_transfer')),
  amount            numeric(18,3) NOT NULL CHECK (amount <> 0),        -- negative = refund
  tendered          numeric(18,3),
  change_given      numeric(18,3),
  card_terminal     text,
  approval_code     text,
  reference         text,
  created_at        timestamptz NOT NULL DEFAULT now(),
  CHECK (method <> 'card' OR amount < 0 OR (card_terminal IS NOT NULL AND approval_code IS NOT NULL)),
  CHECK (method <> 'bank_transfer' OR reference IS NOT NULL),
  CHECK (method = 'cash' OR (tendered IS NULL AND change_given IS NULL)),
  CHECK (tendered IS NULL OR (amount > 0 AND change_given = tendered - amount AND change_given >= 0))
);
CREATE INDEX payment_shift ON sales.payment (shift_id, method);
CREATE INDEX payment_sale ON sales.payment (sale_id);

-- Parked carts (10.3): saved on the server, recalled at any terminal of the branch; recalling deletes the row.
CREATE TABLE sales.parked_cart (
  id            uuid PRIMARY KEY,                 -- the cart's sale UUID
  business_id   uuid NOT NULL REFERENCES core.business(id),
  branch_id     uuid NOT NULL REFERENCES core.branch(id),
  terminal_id   uuid NOT NULL REFERENCES core.terminal(id),
  parked_by     uuid NOT NULL REFERENCES iam."user"(id),
  label         text,
  cart          jsonb NOT NULL,
  parked_at     timestamptz NOT NULL DEFAULT now()
);

-- Emergency records received from terminals (6.4): one row per record UUID, so a re-upload is acknowledged
-- without being applied twice.
CREATE TABLE sales.emergency_record (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  terminal_id      uuid NOT NULL REFERENCES core.terminal(id),
  terminal_seq     bigint NOT NULL CHECK (terminal_seq > 0),
  kind             text NOT NULL CHECK (kind IN ('shift_open','sale','cash_event','shift_close')),
  recorded_at      timestamptz NOT NULL,
  monotonic_ms     bigint,
  payload          jsonb NOT NULL,
  received_at      timestamptz NOT NULL DEFAULT now(),
  result           text NOT NULL CHECK (result IN ('applied','conflict')),
  result_detail    text,
  UNIQUE (terminal_id, terminal_seq)
);

-- ================================================================ immutability and grants
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['inv.adjustment','inv.adjustment_line','sales.cash_event','sales.card_settlement',
                           'sales.sale','sales.sale_line','sales.payment','sales.emergency_record'] LOOP
    EXECUTE format('CREATE TRIGGER %s_immutable BEFORE UPDATE OR DELETE ON %s FOR EACH ROW EXECUTE FUNCTION core.forbid_change()',
                   replace(t, '.', '_'), t);
    EXECUTE format('REVOKE UPDATE, DELETE, TRUNCATE ON %s FROM kite_app', t);
  END LOOP;
END
$$;
REVOKE UPDATE, DELETE, TRUNCATE ON inv.stock_movement FROM kite_app;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON inv.stock_balance FROM kite_app;
REVOKE DELETE, TRUNCATE ON catalog.brand, catalog.category, catalog.product, catalog.variant, catalog.price_list,
  catalog.price, crm.customer, sales.shift, sales.pos_policy, iam.approval, inv.stock_count, inv.stock_exception FROM kite_app;
REVOKE EXECUTE ON FUNCTION inv.apply_movement() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION inv.check_balances() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION inv.check_balances() TO kite_app, kite_ro;
REVOKE EXECUTE ON FUNCTION sales.check_return_qty() FROM PUBLIC;

-- Row-level security on branch-owned tables (18.3).
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['catalog.branch_variant','inv.stock_balance','inv.stock_movement','inv.adjustment','inv.stock_count',
                           'inv.stock_exception','sales.shift','sales.cash_event','sales.card_settlement','sales.sale',
                           'sales.payment','sales.parked_cart','sales.emergency_record','iam.approval'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY branch_read ON %s FOR SELECT USING (core.branch_visible(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_insert ON %s FOR INSERT WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_update ON %s FOR UPDATE USING (core.branch_visible(branch_id)) WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_delete ON %s FOR DELETE USING (core.branch_writable(branch_id))', t);
  END LOOP;
END
$$;
-- Lines follow their document's branch.
ALTER TABLE sales.sale_line ENABLE ROW LEVEL SECURITY;
CREATE POLICY branch_read ON sales.sale_line FOR SELECT USING (EXISTS (SELECT 1 FROM sales.sale s WHERE s.id = sale_id));
CREATE POLICY branch_insert ON sales.sale_line FOR INSERT WITH CHECK (EXISTS (SELECT 1 FROM sales.sale s WHERE s.id = sale_id AND core.branch_writable(s.branch_id)));
ALTER TABLE inv.adjustment_line ENABLE ROW LEVEL SECURITY;
CREATE POLICY branch_read ON inv.adjustment_line FOR SELECT USING (EXISTS (SELECT 1 FROM inv.adjustment a WHERE a.id = adjustment_id));
CREATE POLICY branch_insert ON inv.adjustment_line FOR INSERT WITH CHECK (EXISTS (SELECT 1 FROM inv.adjustment a WHERE a.id = adjustment_id AND core.branch_writable(a.branch_id)));
ALTER TABLE inv.stock_count_line ENABLE ROW LEVEL SECURITY;
CREATE POLICY branch_all ON inv.stock_count_line FOR ALL
  USING (EXISTS (SELECT 1 FROM inv.stock_count c WHERE c.id = stock_count_id))
  WITH CHECK (EXISTS (SELECT 1 FROM inv.stock_count c WHERE c.id = stock_count_id AND core.branch_writable(c.branch_id)));

-- ================================================================ accounts, number series, policies for existing data
-- Opening stock is credited to an opening-balance equity account (17.2 template, editable).
INSERT INTO acct.account (id, business_id, code, name_en, name_ar, type, parent_id, is_postable, is_control)
SELECT gen_random_uuid(), b.id, '3900', 'Opening balance equity', 'حقوق ملكية الأرصدة الافتتاحية', 'equity',
       (SELECT a.id FROM acct.account a WHERE a.business_id = b.id AND a.code = '3000'), true, false
  FROM core.business b
 WHERE NOT EXISTS (SELECT 1 FROM acct.account a WHERE a.business_id = b.id AND a.code = '3900');

-- New document series for every existing branch and fiscal year that already has series.
INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year)
SELECT gen_random_uuid(), s.business_id, s.branch_id, d.doc_type, d.prefix || '-' || br.code || '-' || s.fiscal_year || '-', s.fiscal_year
  FROM (SELECT DISTINCT business_id, branch_id, fiscal_year FROM core.number_series WHERE branch_id IS NOT NULL AND terminal_id IS NULL) s
  JOIN core.branch br ON br.id = s.branch_id
 CROSS JOIN (VALUES ('shift','SH'), ('stock_adjustment','ADJ'), ('stock_count','CNT')) AS d(doc_type, prefix)
ON CONFLICT DO NOTHING;
-- Customers are numbered business-wide.
INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year, padding)
SELECT gen_random_uuid(), b.id, NULL, 'customer', 'C-', 2000, 6 FROM core.business b
ON CONFLICT DO NOTHING;

INSERT INTO sales.pos_policy (id, business_id) SELECT gen_random_uuid(), b.id FROM core.business b ON CONFLICT DO NOTHING;
INSERT INTO catalog.price_list (id, business_id, code, name_en, name_ar, is_default)
SELECT gen_random_uuid(), b.id, 'RETAIL', 'Retail prices', 'أسعار التجزئة', true FROM core.business b ON CONFLICT DO NOTHING;

-- ================================================================ permissions
INSERT INTO iam.permission (code, module_code, description_en, description_ar) VALUES
  ('CATALOG.VIEW',          'catalog', 'View products and prices',                         'عرض المنتجات والأسعار'),
  ('CATALOG.MANAGE',        'catalog', 'Create and edit products, barcodes and categories', 'إنشاء المنتجات والباركود والفئات وتعديلها'),
  ('CATALOG.PRICE.MANAGE',  'catalog', 'Set prices and branch price overrides',             'تحديد الأسعار وأسعار الفروع'),
  ('INV.VIEW',              'inv',     'View stock levels and movements',                   'عرض أرصدة المخزون وحركاته'),
  ('INV.ADJUST',            'inv',     'Post stock adjustments and opening stock',          'ترحيل تسويات المخزون والأرصدة الافتتاحية'),
  ('INV.COUNT',             'inv',     'Run and post stock counts',                         'إجراء جرد المخزون وترحيله'),
  ('CUSTOMERS.VIEW',        'crm',     'View customers',                                    'عرض العملاء'),
  ('CUSTOMERS.MANAGE',      'crm',     'Create and edit customers',                         'إنشاء العملاء وتعديلهم'),
  ('POS.SHIFT.OPEN',        'sales',   'Open and close own shifts',                         'فتح الورديات الخاصة وإغلاقها'),
  ('POS.CASH.PAY_OUT',      'sales',   'Pay cash out of the drawer',                        'صرف نقد من الدرج'),
  ('POS.REPORTS.VIEW',      'sales',   'View sales, shifts and X/Z reports of the branch',  'عرض المبيعات والورديات وتقارير X وZ للفرع'),
  ('POS.POLICY.MANAGE',     'sales',   'Change POS policy (returns, refunds, rounding)',    'تغيير سياسة نقطة البيع (المرتجعات والاسترداد والتقريب)'),
  ('APPROVALS.DECIDE',      'iam',     'Decide remote approval requests',                   'البت في طلبات الموافقة عن بعد');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r
  CROSS JOIN (SELECT code FROM iam.permission WHERE code IN ('CATALOG.VIEW','CATALOG.MANAGE','CATALOG.PRICE.MANAGE','INV.VIEW','INV.ADJUST',
              'INV.COUNT','CUSTOMERS.VIEW','CUSTOMERS.MANAGE','POS.SHIFT.OPEN','POS.CASH.PAY_OUT','POS.REPORTS.VIEW','POS.POLICY.MANAGE','APPROVALS.DECIDE')) p
 WHERE r.code = 'owner'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r
  CROSS JOIN (VALUES ('CATALOG.VIEW'),('CATALOG.MANAGE'),('CATALOG.PRICE.MANAGE'),('INV.VIEW'),('INV.ADJUST'),('INV.COUNT'),('CUSTOMERS.VIEW'),
              ('CUSTOMERS.MANAGE'),('POS.SHIFT.OPEN'),('POS.CASH.PAY_OUT'),('POS.REPORTS.VIEW'),('APPROVALS.DECIDE')) AS p(code)
 WHERE r.code = 'branch_manager'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r
  CROSS JOIN (VALUES ('CATALOG.VIEW'),('INV.VIEW'),('CUSTOMERS.VIEW'),('CUSTOMERS.MANAGE'),('POS.SHIFT.OPEN'),('POS.CASH.PAY_OUT')) AS p(code)
 WHERE r.code = 'senior_cashier'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r
  CROSS JOIN (VALUES ('CATALOG.VIEW'),('CUSTOMERS.VIEW'),('CUSTOMERS.MANAGE'),('POS.SHIFT.OPEN')) AS p(code)
 WHERE r.code = 'cashier'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r
  CROSS JOIN (VALUES ('CATALOG.VIEW'),('CATALOG.MANAGE'),('INV.VIEW'),('INV.ADJUST'),('INV.COUNT')) AS p(code)
 WHERE r.code = 'inventory_controller'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r
  CROSS JOIN (VALUES ('CATALOG.VIEW'),('INV.VIEW'),('CUSTOMERS.VIEW'),('POS.REPORTS.VIEW')) AS p(code)
 WHERE r.code = 'accountant'
ON CONFLICT DO NOTHING;
