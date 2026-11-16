-- V0018: Phase 9 - spare parts for repairs (blueprint 13.4) and technician commissions (blueprint 15).
--
-- Parts. When a job's estimate is approved its parts are reserved in the branch's stock. A reservation is one row per
-- job, warehouse and part, holding the quantity still needed; the stock balance carries the total reserved, kept by
-- trigger, so a reservation can never exceed the stock on hand and the till can see what it must not sell. What cannot
-- be reserved becomes a parts request; when stock arrives the oldest requests are filled first and a job waiting for
-- parts moves on. Serial-tracked parts are device units: fitted to a job they leave stock with status 'fitted', and
-- come back at their own cost. Outside work (a specialist's repair) goes into the job's work in progress at cost,
-- owed to the supplier until the invoice arrives (2110), and is cleared at delivery like the parts.
--
-- Commissions. Versioned rules (fixed, % of labour, % of repair profit) with scopes, minimum, maximum and dates; the
-- most specific active rule wins. A job's technicians share it (shares total 100%). An accrual is written once per
-- job, technician, kind and version - the unique key makes a second one impossible however often the generator runs -
-- with the rule version, the basis amounts and a calculation trace, and its journal in the same transaction. An
-- accrual is never edited: it is approved, paid in exactly one payout line, or reversed by an opposite accrual. A
-- repair invoice corrected at the till (credit note, and a corrected invoice) reverses the job's accruals and
-- generates them again under the next version.

-- ================================================================ accounts added in Phase 9 (blueprint 17.2)
INSERT INTO acct.account (id, business_id, code, name_en, name_ar, type, parent_id, is_postable, is_control)
SELECT gen_random_uuid(), b.id, a.code, a.name_en, a.name_ar, a.type, p.id, true, a.is_control
  FROM core.business b
 CROSS JOIN (VALUES ('5210', 'Cost: outside repair work', 'التكلفة: أعمال صيانة خارجية', 'expense', '5000', false),
                    ('2410', 'Payroll clearing', 'تسوية الرواتب', 'liability', '2000', true)) AS a(code, name_en, name_ar, type, parent, is_control)
  LEFT JOIN acct.account p ON p.business_id = b.id AND p.code = a.parent
 WHERE NOT EXISTS (SELECT 1 FROM acct.account x WHERE x.business_id = b.id AND x.code = a.code);

-- ================================================================ reservations (13.4)
ALTER TABLE inv.stock_balance ADD COLUMN reserved numeric(14,3) NOT NULL DEFAULT 0 CHECK (reserved >= 0);

CREATE TABLE inv.reservation (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  warehouse_id     uuid NOT NULL REFERENCES core.warehouse(id),
  variant_id       uuid NOT NULL REFERENCES catalog.variant(id),
  source_doc_type  text NOT NULL CHECK (source_doc_type IN ('repair_job')),
  source_doc_id    uuid NOT NULL,
  qty              numeric(14,3) NOT NULL CHECK (qty >= 0),       -- still held; 0 = nothing held
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  updated_by       uuid,
  UNIQUE (source_doc_type, source_doc_id, warehouse_id, variant_id)
);
CREATE INDEX reservation_balance ON inv.reservation (branch_id, warehouse_id, variant_id) WHERE qty > 0;

-- The balance's reserved total follows every reservation change; a reservation never takes more than is on hand.
CREATE FUNCTION inv.reservation_apply() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE delta numeric; b inv.stock_balance%ROWTYPE; p_type text;
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'KITE: a reservation is released by setting it to zero, not deleted' USING ERRCODE = 'KT050';
  END IF;
  IF TG_OP = 'UPDATE' AND (NEW.branch_id, NEW.warehouse_id, NEW.variant_id, NEW.source_doc_type, NEW.source_doc_id)
                          IS DISTINCT FROM (OLD.branch_id, OLD.warehouse_id, OLD.variant_id, OLD.source_doc_type, OLD.source_doc_id) THEN
    RAISE EXCEPTION 'KITE: a reservation keeps its item and document' USING ERRCODE = 'KT050';
  END IF;
  SELECT p.product_type INTO p_type FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id WHERE v.id = NEW.variant_id;
  IF p_type IN ('service','device') THEN
    RAISE EXCEPTION 'KITE: only parts, accessories and consumables are reserved for repairs' USING ERRCODE = 'KT050';
  END IF;
  delta := NEW.qty - CASE WHEN TG_OP = 'UPDATE' THEN OLD.qty ELSE 0 END;
  IF delta = 0 THEN RETURN NEW; END IF;
  INSERT INTO inv.stock_balance (business_id, branch_id, warehouse_id, variant_id)
  VALUES (NEW.business_id, NEW.branch_id, NEW.warehouse_id, NEW.variant_id)
  ON CONFLICT (branch_id, warehouse_id, variant_id) DO NOTHING;
  UPDATE inv.stock_balance SET reserved = reserved + delta
   WHERE branch_id = NEW.branch_id AND warehouse_id = NEW.warehouse_id AND variant_id = NEW.variant_id
  RETURNING * INTO b;
  IF delta > 0 AND b.reserved > b.qty THEN
    RAISE EXCEPTION 'KITE: only % of this part is on hand and % is already reserved', b.qty, b.reserved - delta USING ERRCODE = 'KT050';
  END IF;
  NEW.updated_at := now();
  RETURN NEW;
END $$;
CREATE TRIGGER reservation_apply BEFORE INSERT OR UPDATE OR DELETE ON inv.reservation FOR EACH ROW EXECUTE FUNCTION inv.reservation_apply();

-- What a branch can still use of an item: on hand less reserved (never below zero).
CREATE FUNCTION inv.available(p_warehouse uuid, p_variant uuid) RETURNS numeric
LANGUAGE sql STABLE AS $$
  SELECT greatest(coalesce((SELECT qty - reserved FROM inv.stock_balance WHERE warehouse_id = p_warehouse AND variant_id = p_variant), 0), 0)
$$;

-- ================================================================ parts requests (13.4)
-- What a job needs and the branch did not have when the estimate was approved; one row per job, warehouse and part,
-- holding what is still outstanding. open_since orders the queue: the oldest request is filled first.
CREATE TABLE repair.part_request (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  job_id           uuid NOT NULL REFERENCES repair.repair_job(id),
  warehouse_id     uuid NOT NULL REFERENCES core.warehouse(id),
  variant_id       uuid NOT NULL REFERENCES catalog.variant(id),
  qty              numeric(14,3) NOT NULL CHECK (qty >= 0),        -- still outstanding
  requested_total  numeric(14,3) NOT NULL DEFAULT 0 CHECK (requested_total >= 0),
  filled_total     numeric(14,3) NOT NULL DEFAULT 0 CHECK (filled_total >= 0),
  open_since       timestamptz,
  link_doc_type    text,                                            -- purchase order or transfer request (Phases 11 and 12)
  link_doc_id      uuid,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  UNIQUE (job_id, warehouse_id, variant_id),
  CHECK ((qty > 0) = (open_since IS NOT NULL))
);
CREATE INDEX part_request_queue ON repair.part_request (branch_id, variant_id, open_since) WHERE qty > 0;
CREATE TRIGGER part_request_no_delete BEFORE DELETE ON repair.part_request FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

-- ================================================================ serial-tracked parts (13.4)
ALTER TABLE device.device_unit DROP CONSTRAINT device_unit_status_check;
ALTER TABLE device.device_unit ADD CONSTRAINT device_unit_status_check CHECK (status IN
  ('in_stock','under_inspection','returned_inspection','refurbishing','for_parts','supplier_return','sold','in_transit','quarantined',
   'supplier_returned','written_off','reversed','fitted'));
ALTER TABLE device.device_event DROP CONSTRAINT device_event_event_type_check;
ALTER TABLE device.device_event ADD CONSTRAINT device_event_event_type_check CHECK (event_type IN
  ('received','inspected','cost_added','refurb_completed','sold','sale_voided','returned','written_off','held','released','price_set',
   'supplier_returned','transferred_out','transferred_in','identifier_corrected','receipt_reversed','traded_in','fitted','unfitted'));
ALTER TABLE device.transition DISABLE TRIGGER device_transition_immutable;
INSERT INTO device.transition (from_status, to_status, event_type) VALUES
  ('in_stock','fitted','fitted'),           -- fitted to a customer's device in a repair
  ('fitted','in_stock','unfitted'),         -- taken out again unused, back at its cost
  ('fitted','written_off','written_off');   -- damaged while fitting
ALTER TABLE device.transition ENABLE TRIGGER device_transition_immutable;

-- ================================================================ repair lines: serial parts and outside work
ALTER TABLE repair.repair_line ADD COLUMN device_unit_id uuid REFERENCES device.device_unit(id);
ALTER TABLE repair.repair_line ADD COLUMN serial text;
ALTER TABLE repair.repair_line ADD COLUMN vendor text;
ALTER TABLE repair.repair_line ADD COLUMN reference text;
ALTER TABLE repair.repair_line DROP CONSTRAINT repair_line_kind_check;
ALTER TABLE repair.repair_line ADD CONSTRAINT repair_line_kind_check CHECK (kind IN ('part','labour','external'));
ALTER TABLE repair.repair_line ADD CONSTRAINT repair_line_serial CHECK (device_unit_id IS NULL OR (kind = 'part' AND abs(qty) = 1 AND serial IS NOT NULL));
ALTER TABLE repair.repair_line ADD CONSTRAINT repair_line_external CHECK (kind <> 'external' OR
  (variant_id IS NULL AND qty IS NULL AND minutes IS NULL AND stock_movement_id IS NULL AND cost <> 0 AND vendor IS NOT NULL
   AND (cost > 0) = (return_of_id IS NULL)));
CREATE UNIQUE INDEX repair_line_one_reversal ON repair.repair_line (return_of_id) WHERE kind = 'external';

CREATE OR REPLACE FUNCTION repair.repair_line_check() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE st text; num text; fitted record; back numeric;
BEGIN
  SELECT status, number INTO st, num FROM repair.repair_job WHERE id = NEW.job_id FOR SHARE;
  IF st = 'delivered' THEN
    RAISE EXCEPTION 'KITE: job % was delivered; its work cannot change', num USING ERRCODE = 'KT041';
  END IF;
  IF NEW.kind = 'labour' AND st NOT IN ('under_diagnosis','in_progress','testing') THEN
    RAISE EXCEPTION 'KITE: labour is recorded while the job is diagnosed, repaired or tested, not while it is %', replace(st, '_', ' ') USING ERRCODE = 'KT041';
  END IF;
  IF NEW.kind = 'part' AND NEW.qty > 0 AND st NOT IN ('in_progress','testing') THEN
    RAISE EXCEPTION 'KITE: parts are fitted while the repair is in progress or being tested, not while it is %', replace(st, '_', ' ') USING ERRCODE = 'KT041';
  END IF;
  IF NEW.kind = 'external' AND NEW.cost > 0 AND st NOT IN ('under_diagnosis','waiting_parts','in_progress','testing') THEN
    RAISE EXCEPTION 'KITE: outside work is recorded while the job is diagnosed or repaired, not while it is %', replace(st, '_', ' ') USING ERRCODE = 'KT041';
  END IF;
  IF NEW.kind = 'external' AND NEW.cost < 0 THEN
    SELECT * INTO fitted FROM repair.repair_line WHERE id = NEW.return_of_id;
    IF NOT FOUND OR fitted.job_id <> NEW.job_id OR fitted.kind <> 'external' OR fitted.cost <= 0 OR fitted.cost <> -NEW.cost THEN
      RAISE EXCEPTION 'KITE: outside work is reversed in full, against its own line on the same job' USING ERRCODE = 'KT041';
    END IF;
  END IF;
  IF NEW.kind = 'part' AND NEW.qty < 0 THEN
    SELECT * INTO fitted FROM repair.repair_line WHERE id = NEW.return_of_id;
    IF NOT FOUND OR fitted.job_id <> NEW.job_id OR fitted.kind <> 'part' OR fitted.qty <= 0 OR fitted.variant_id <> NEW.variant_id
       OR fitted.device_unit_id IS DISTINCT FROM NEW.device_unit_id THEN
      RAISE EXCEPTION 'KITE: a returned part is returned against the part fitted on the same job' USING ERRCODE = 'KT041';
    END IF;
    SELECT coalesce(-sum(qty), 0) INTO back FROM repair.repair_line WHERE return_of_id = NEW.return_of_id;
    IF back - NEW.qty > fitted.qty THEN
      RAISE EXCEPTION 'KITE: more would be returned than was fitted' USING ERRCODE = 'KT041';
    END IF;
  END IF;
  RETURN NEW;
END $$;

ALTER TABLE repair.wip_clearing DROP CONSTRAINT wip_clearing_kind_check;
ALTER TABLE repair.wip_clearing ADD CONSTRAINT wip_clearing_kind_check CHECK (kind IN ('cost_of_repair','outside_work','warranty','written_off'));

-- Work in progress: parts at cost and outside work, less what has been cleared.
CREATE OR REPLACE FUNCTION repair.wip(p_job uuid) RETURNS numeric
LANGUAGE sql STABLE AS $$
  SELECT coalesce((SELECT sum(cost) FROM repair.repair_line WHERE job_id = p_job AND kind IN ('part','external')), 0)
       - coalesce((SELECT sum(amount) FROM repair.wip_clearing WHERE job_id = p_job), 0)
$$;
-- The cost of the parts used on a job (fitted less returned, written-off parts included) and of its outside work.
CREATE FUNCTION repair.parts_cost(p_job uuid) RETURNS numeric
LANGUAGE sql STABLE AS $$ SELECT coalesce(sum(cost), 0) FROM repair.repair_line WHERE job_id = p_job AND kind = 'part' $$;
CREATE FUNCTION repair.outside_cost(p_job uuid) RETURNS numeric
LANGUAGE sql STABLE AS $$ SELECT coalesce(sum(cost), 0) FROM repair.repair_line WHERE job_id = p_job AND kind = 'external' $$;

-- A repair invoice corrected at the till is credited with its own reason.
ALTER TABLE sales.sale DROP CONSTRAINT sale_credit_reason_check;
ALTER TABLE sales.sale ADD CONSTRAINT sale_credit_reason_check CHECK (credit_reason IN ('return','void','exchange','correction'));

-- What a repair invoice line was on the estimate: labour, a part or another charge (the commission basis).
ALTER TABLE sales.sale_line ADD COLUMN repair_kind text CHECK (repair_kind IN ('labour','part','extra'));

-- ================================================================ correcting a repair invoice at the till
-- The delivered job keeps its record; a correction credits its current invoice in full and may issue a corrected one.
CREATE TABLE repair.invoice_correction (
  id                 uuid PRIMARY KEY,
  business_id        uuid NOT NULL REFERENCES core.business(id),
  branch_id          uuid NOT NULL REFERENCES core.branch(id),
  job_id             uuid NOT NULL REFERENCES repair.repair_job(id),
  credited_sale_id   uuid NOT NULL UNIQUE REFERENCES sales.sale(id),
  credit_note_id     uuid NOT NULL UNIQUE REFERENCES sales.sale(id),
  corrected_sale_id  uuid UNIQUE REFERENCES sales.sale(id),
  reason             text NOT NULL CHECK (length(trim(reason)) >= 3),
  created_at         timestamptz NOT NULL DEFAULT now(),
  created_by         uuid NOT NULL
);
CREATE INDEX invoice_correction_job ON repair.invoice_correction (job_id, created_at);
CREATE TRIGGER invoice_correction_immutable BEFORE UPDATE OR DELETE ON repair.invoice_correction FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

-- The invoice that stands for a job now: the delivery invoice, or the last corrected one; null if credited and not re-issued.
CREATE FUNCTION repair.current_invoice(p_job uuid) RETURNS uuid
LANGUAGE sql STABLE AS $$
  WITH RECURSIVE chain(sale_id) AS (
    SELECT delivery_sale_id FROM repair.repair_job WHERE id = p_job AND delivery_sale_id IS NOT NULL
    UNION ALL
    SELECT c.corrected_sale_id FROM repair.invoice_correction c JOIN chain ON c.credited_sale_id = chain.sale_id WHERE c.corrected_sale_id IS NOT NULL
  )
  SELECT sale_id FROM chain c WHERE NOT EXISTS (SELECT 1 FROM repair.invoice_correction x WHERE x.credited_sale_id = c.sale_id) LIMIT 1
$$;

-- ================================================================ commissions (15)
CREATE SCHEMA IF NOT EXISTS comm;
GRANT USAGE ON SCHEMA comm TO kite_app, kite_ro;
ALTER DEFAULT PRIVILEGES IN SCHEMA comm GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO kite_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA comm GRANT SELECT ON TABLES TO kite_ro;
ALTER DEFAULT PRIVILEGES IN SCHEMA comm GRANT EXECUTE ON FUNCTIONS TO kite_app, kite_ro;

CREATE TABLE comm.settings (
  business_id       uuid PRIMARY KEY REFERENCES core.business(id),
  generate_on       text NOT NULL DEFAULT 'delivered' CHECK (generate_on IN ('delivered','paid')),
  approval_by       text NOT NULL DEFAULT 'branch' CHECK (approval_by IN ('branch','head_office')),
  clawback_days     integer NOT NULL DEFAULT 0 CHECK (clawback_days BETWEEN 0 AND 365),     -- 0: no rework clawback
  clawback_percent  numeric(7,4) NOT NULL DEFAULT 100 CHECK (clawback_percent > 0 AND clawback_percent <= 100),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  updated_by        uuid,
  version           integer NOT NULL DEFAULT 1
);
INSERT INTO comm.settings (business_id) SELECT id FROM core.business ON CONFLICT DO NOTHING;

-- A rule version. Editing a rule writes the next version and ends the previous one the day before.
CREATE TABLE comm.rule (
  id              uuid PRIMARY KEY,
  business_id     uuid NOT NULL REFERENCES core.business(id),
  rule_key        uuid NOT NULL,
  version         integer NOT NULL CHECK (version > 0),
  name_en         text NOT NULL CHECK (length(trim(name_en)) > 0),
  name_ar         text NOT NULL CHECK (length(trim(name_ar)) > 0),
  basis           text NOT NULL CHECK (basis IN ('fixed','labour','profit')),
  amount          numeric(18,3) CHECK (amount IS NULL OR amount >= 0),
  rate            numeric(9,4) CHECK (rate IS NULL OR (rate >= 0 AND rate <= 100)),
  min_amount      numeric(18,3) CHECK (min_amount IS NULL OR min_amount >= 0),
  max_amount      numeric(18,3) CHECK (max_amount IS NULL OR max_amount >= 0),
  technician_id   uuid REFERENCES iam."user"(id),
  branch_id       uuid REFERENCES core.branch(id),
  category_id     uuid REFERENCES repair.category(id),
  effective_from  date NOT NULL,
  effective_to    date,
  status          text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      uuid,
  UNIQUE (rule_key, version),
  CHECK ((basis = 'fixed') = (amount IS NOT NULL)),
  CHECK ((basis = 'fixed') = (rate IS NULL)),
  CHECK (min_amount IS NULL OR max_amount IS NULL OR min_amount <= max_amount),
  CHECK (effective_to IS NULL OR effective_to >= effective_from),
  CHECK (technician_id IS NULL OR branch_id IS NULL)          -- the scopes of 15.1: a technician's rule applies in every branch
);

-- How specific a scope is (15.1): technician + category, technician, branch + category, category, branch, business default.
CREATE FUNCTION comm.scope_rank(p_technician uuid, p_branch uuid, p_category uuid) RETURNS integer
LANGUAGE sql IMMUTABLE AS $$
  SELECT CASE WHEN p_technician IS NOT NULL AND p_category IS NOT NULL THEN 1
              WHEN p_technician IS NOT NULL THEN 2
              WHEN p_branch IS NOT NULL AND p_category IS NOT NULL THEN 3
              WHEN p_category IS NOT NULL THEN 4
              WHEN p_branch IS NOT NULL THEN 5
              ELSE 6 END
$$;

-- Versions are kept as written; only the end date and the status change. Two active rules of the same scope never
-- overlap in time, so the most specific rule on a date is always one rule.
CREATE FUNCTION comm.rule_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'KITE: a commission rule is never deleted; end it or make it inactive' USING ERRCODE = 'KT060';
  END IF;
  IF TG_OP = 'UPDATE' AND (to_jsonb(NEW) - 'effective_to' - 'status') IS DISTINCT FROM (to_jsonb(OLD) - 'effective_to' - 'status') THEN
    RAISE EXCEPTION 'KITE: a rule version is kept as written; a change is a new version' USING ERRCODE = 'KT060';
  END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('comm.rule:' || NEW.business_id::text, 0));
  IF NEW.status = 'active' AND EXISTS (
       SELECT 1 FROM comm.rule r
        WHERE r.business_id = NEW.business_id AND r.id <> NEW.id AND r.status = 'active'
          AND r.technician_id IS NOT DISTINCT FROM NEW.technician_id AND r.branch_id IS NOT DISTINCT FROM NEW.branch_id
          AND r.category_id IS NOT DISTINCT FROM NEW.category_id
          AND daterange(r.effective_from, r.effective_to, '[]') && daterange(NEW.effective_from, NEW.effective_to, '[]')) THEN
    RAISE EXCEPTION 'KITE: another active rule with the same scope covers some of these dates' USING ERRCODE = 'KT060';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER rule_guard BEFORE INSERT OR UPDATE OR DELETE ON comm.rule FOR EACH ROW EXECUTE FUNCTION comm.rule_guard();

-- The rule for a technician's share of a job on a date: the most specific active rule in force.
CREATE FUNCTION comm.resolve_rule(p_business uuid, p_technician uuid, p_branch uuid, p_category uuid, p_on date) RETURNS uuid
LANGUAGE sql STABLE AS $$
  SELECT r.id FROM comm.rule r
   WHERE r.business_id = p_business AND r.status = 'active'
     AND r.effective_from <= p_on AND (r.effective_to IS NULL OR r.effective_to >= p_on)
     AND (r.technician_id IS NULL OR r.technician_id = p_technician)
     AND (r.branch_id IS NULL OR r.branch_id = p_branch)
     AND (r.category_id IS NULL OR r.category_id = p_category)
   ORDER BY comm.scope_rank(r.technician_id, r.branch_id, r.category_id), r.effective_from DESC
   LIMIT 1
$$;

-- Who shares a job's commission (15.2); without rows the job's technician takes all of it.
CREATE TABLE comm.job_share (
  job_id          uuid NOT NULL REFERENCES repair.repair_job(id),
  technician_id   uuid NOT NULL REFERENCES iam."user"(id),
  business_id     uuid NOT NULL REFERENCES core.business(id),
  branch_id       uuid NOT NULL REFERENCES core.branch(id),
  share           numeric(7,4) NOT NULL CHECK (share > 0 AND share <= 100),
  set_by          uuid NOT NULL,
  set_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (job_id, technician_id)
);

CREATE TABLE comm.accrual (
  id              uuid PRIMARY KEY,
  business_id     uuid NOT NULL REFERENCES core.business(id),
  branch_id       uuid NOT NULL REFERENCES core.branch(id),
  job_id          uuid NOT NULL REFERENCES repair.repair_job(id),
  technician_id   uuid NOT NULL REFERENCES iam."user"(id),
  kind            text NOT NULL CHECK (kind IN ('repair','clawback','reversal')),
  kind_version    integer NOT NULL DEFAULT 1 CHECK (kind_version > 0),
  amount          numeric(18,3) NOT NULL,
  share           numeric(7,4),
  rule_id         uuid REFERENCES comm.rule(id),
  rule_key        uuid,
  rule_version    integer,
  basis           text CHECK (basis IN ('fixed','labour','profit')),
  basis_amounts   jsonb NOT NULL DEFAULT '{}',
  trace           jsonb NOT NULL,
  sale_id         uuid REFERENCES sales.sale(id),         -- the invoice a repair accrual was generated from
  claim_job_id    uuid REFERENCES repair.repair_job(id),  -- the warranty claim a clawback answers
  reverses_id     uuid UNIQUE REFERENCES comm.accrual(id),
  accrual_date    date NOT NULL,
  status          text NOT NULL DEFAULT 'accrued' CHECK (status IN ('accrued','approved','paid','reversed')),
  journal_id      uuid,
  approved_by     uuid,
  approved_at     timestamptz,
  payout_id       uuid,
  reversed_by     uuid,
  reversed_at     timestamptz,
  reason          text,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      uuid,
  CHECK ((kind = 'reversal') = (reverses_id IS NOT NULL)),
  CHECK (kind <> 'repair' OR (amount >= 0 AND sale_id IS NOT NULL AND rule_id IS NOT NULL AND share IS NOT NULL)),
  CHECK (kind <> 'clawback' OR (amount <= 0 AND claim_job_id IS NOT NULL)),
  CHECK ((status = 'paid') = (payout_id IS NOT NULL)),
  CHECK (status <> 'approved' OR approved_by IS NOT NULL)
);
-- The guarantee of 15.2: one accrual per job, technician, kind and version, however often the generator runs.
CREATE UNIQUE INDEX accrual_once ON comm.accrual (job_id, technician_id, kind, kind_version) WHERE kind <> 'reversal';
CREATE INDEX accrual_technician ON comm.accrual (technician_id, accrual_date);
CREATE INDEX accrual_status ON comm.accrual (branch_id, status);

-- An accrual is never edited (15.3): only its status moves forward, and a reversal pairs it with its opposite.
CREATE FUNCTION comm.accrual_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE moving text[] := ARRAY['status','approved_by','approved_at','payout_id','reversed_by','reversed_at','reason'];
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'KITE: a commission accrual is never deleted; it is reversed' USING ERRCODE = 'KT061';
  END IF;
  IF (SELECT jsonb_object_agg(k, v) FROM jsonb_each(to_jsonb(NEW)) e(k, v) WHERE NOT k = ANY(moving))
     IS DISTINCT FROM (SELECT jsonb_object_agg(k, v) FROM jsonb_each(to_jsonb(OLD)) e(k, v) WHERE NOT k = ANY(moving)) THEN
    RAISE EXCEPTION 'KITE: a commission accrual cannot be edited; reverse it' USING ERRCODE = 'KT061';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'accrued' AND NEW.status IN ('approved','reversed')) OR
       (OLD.status = 'approved' AND NEW.status IN ('paid','reversed'))) THEN
    RAISE EXCEPTION 'KITE: a commission that is % cannot become %', OLD.status, NEW.status USING ERRCODE = 'KT061';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER accrual_guard BEFORE UPDATE OR DELETE ON comm.accrual FOR EACH ROW EXECUTE FUNCTION comm.accrual_guard();

-- A reversal is the exact opposite of what it reverses, for the same job and technician.
CREATE FUNCTION comm.accrual_check() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE o comm.accrual%ROWTYPE;
BEGIN
  IF NEW.kind = 'reversal' THEN
    SELECT * INTO o FROM comm.accrual WHERE id = NEW.reverses_id FOR UPDATE;
    IF NOT FOUND OR o.kind = 'reversal' OR o.job_id <> NEW.job_id OR o.technician_id <> NEW.technician_id OR NEW.amount <> -o.amount THEN
      RAISE EXCEPTION 'KITE: a reversal is the exact opposite of a commission on the same job and technician' USING ERRCODE = 'KT061';
    END IF;
    IF o.status = 'reversed' THEN
      RAISE EXCEPTION 'KITE: that commission was already reversed' USING ERRCODE = 'KT061';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER accrual_check BEFORE INSERT ON comm.accrual FOR EACH ROW EXECUTE FUNCTION comm.accrual_check();

-- A payout (15.4): approved accruals of one technician at one branch; each accrual in exactly one payout line.
CREATE TABLE comm.payout (
  id              uuid PRIMARY KEY,
  business_id     uuid NOT NULL REFERENCES core.business(id),
  branch_id       uuid NOT NULL REFERENCES core.branch(id),
  number          text NOT NULL UNIQUE,
  technician_id   uuid NOT NULL REFERENCES iam."user"(id),
  method          text NOT NULL CHECK (method IN ('cash','bank','payroll')),
  total           numeric(18,3) NOT NULL CHECK (total > 0),
  reference       text,
  note            text,
  payout_date     date NOT NULL,
  journal_id      uuid,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      uuid NOT NULL
);
CREATE TABLE comm.payout_line (
  id              uuid PRIMARY KEY,
  payout_id       uuid NOT NULL REFERENCES comm.payout(id),
  accrual_id      uuid NOT NULL UNIQUE REFERENCES comm.accrual(id),
  amount          numeric(18,3) NOT NULL
);
CREATE TRIGGER payout_immutable BEFORE UPDATE OR DELETE ON comm.payout FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
CREATE TRIGGER payout_line_immutable BEFORE UPDATE OR DELETE ON comm.payout_line FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

-- A payout line takes an approved accrual of the payout's technician and branch, at its amount, and marks it paid.
CREATE FUNCTION comm.payout_line_apply() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE a comm.accrual%ROWTYPE; p comm.payout%ROWTYPE;
BEGIN
  SELECT * INTO p FROM comm.payout WHERE id = NEW.payout_id;
  SELECT * INTO a FROM comm.accrual WHERE id = NEW.accrual_id FOR UPDATE;
  IF a.status <> 'approved' THEN
    RAISE EXCEPTION 'KITE: only an approved commission is paid (this one is %)', a.status USING ERRCODE = 'KT061';
  END IF;
  IF a.technician_id <> p.technician_id OR a.branch_id <> p.branch_id OR a.amount <> NEW.amount THEN
    RAISE EXCEPTION 'KITE: a payout line is a commission of the payout''s technician and branch, at its amount' USING ERRCODE = 'KT061';
  END IF;
  UPDATE comm.accrual SET status = 'paid', payout_id = NEW.payout_id WHERE id = a.id;
  RETURN NEW;
END $$;
CREATE TRIGGER payout_line_apply BEFORE INSERT ON comm.payout_line FOR EACH ROW EXECUTE FUNCTION comm.payout_line_apply();

-- At commit a payout's total is the sum of its lines.
CREATE FUNCTION comm.payout_total_check() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF (SELECT total FROM comm.payout WHERE id = NEW.payout_id) <> (SELECT coalesce(sum(amount), 0) FROM comm.payout_line WHERE payout_id = NEW.payout_id) THEN
    RAISE EXCEPTION 'KITE: a payout''s total is the sum of its lines' USING ERRCODE = 'KT061';
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER payout_total AFTER INSERT ON comm.payout_line DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION comm.payout_total_check();

-- ================================================================ checks for the books
-- Commissions payable by the accruals and payouts, against the ledger (there should be no difference).
CREATE FUNCTION comm.payable_by_documents(p_business uuid) RETURNS numeric
LANGUAGE sql STABLE AS $$
  SELECT coalesce((SELECT sum(amount) FROM comm.accrual WHERE business_id = p_business), 0)
       - coalesce((SELECT sum(total) FROM comm.payout WHERE business_id = p_business), 0)
$$;

-- ================================================================ grants, row-level security
REVOKE DELETE, TRUNCATE ON inv.reservation, repair.part_request, repair.invoice_correction, comm.rule, comm.accrual, comm.payout, comm.payout_line FROM kite_app;
REVOKE UPDATE ON repair.invoice_correction, comm.payout, comm.payout_line FROM kite_app;
GRANT SELECT, INSERT, UPDATE ON inv.reservation, repair.part_request TO kite_app;
GRANT SELECT ON inv.reservation, repair.part_request, repair.invoice_correction TO kite_ro;
GRANT SELECT, INSERT ON repair.invoice_correction TO kite_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON comm.job_share TO kite_app;
GRANT EXECUTE ON FUNCTION inv.available(uuid, uuid), repair.parts_cost(uuid), repair.outside_cost(uuid), repair.current_invoice(uuid) TO kite_app, kite_ro;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['inv.reservation','repair.part_request','repair.invoice_correction','comm.job_share','comm.accrual','comm.payout'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY branch_read ON %s FOR SELECT USING (core.branch_visible(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_insert ON %s FOR INSERT WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_update ON %s FOR UPDATE USING (core.branch_visible(branch_id)) WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_delete ON %s FOR DELETE USING (core.branch_writable(branch_id))', t);
  END LOOP;
END
$$;
-- Payout lines follow their payout.
ALTER TABLE comm.payout_line ENABLE ROW LEVEL SECURITY;
CREATE POLICY payout_line_read ON comm.payout_line FOR SELECT USING (EXISTS (SELECT 1 FROM comm.payout p WHERE p.id = payout_id));
CREATE POLICY payout_line_write ON comm.payout_line FOR INSERT WITH CHECK (EXISTS (SELECT 1 FROM comm.payout p WHERE p.id = payout_id AND core.branch_writable(p.branch_id)));

-- ================================================================ number series and permissions
INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year)
SELECT gen_random_uuid(), s.business_id, s.branch_id, 'comm_payout', 'COM-' || br.code || '-' || s.fiscal_year || '-', s.fiscal_year
  FROM (SELECT DISTINCT business_id, branch_id, fiscal_year FROM core.number_series WHERE branch_id IS NOT NULL AND terminal_id IS NULL) s
  JOIN core.branch br ON br.id = s.branch_id
ON CONFLICT DO NOTHING;

INSERT INTO iam.permission (code, module_code, description_en, description_ar) VALUES
  ('REPAIR.INVOICE.CORRECT', 'repair', 'Correct a delivered repair invoice at the till: credit note, refund and corrected invoice', 'تصحيح فاتورة صيانة مسلّمة عند نقطة البيع: إشعار دائن واسترداد وفاتورة مصححة'),
  ('COMM.VIEW',    'comm', 'View commissions, statements and technician reports',                     'عرض العمولات والكشوف وتقارير الفنيين'),
  ('COMM.OWN',     'comm', 'See your own commissions',                                                 'عرض عمولاتك'),
  ('COMM.RULES',   'comm', 'Manage commission rules and settings',                                     'إدارة قواعد العمولات وإعداداتها'),
  ('COMM.APPROVE', 'comm', 'Approve commissions for payout, and reverse them with a reason',          'اعتماد العمولات للصرف وعكسها مع ذكر السبب'),
  ('COMM.PAY',     'comm', 'Pay approved commissions out',                                             'صرف العمولات المعتمدة')
ON CONFLICT DO NOTHING;

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (SELECT code FROM iam.permission WHERE code LIKE 'COMM.%' OR code = 'REPAIR.INVOICE.CORRECT') p
 WHERE r.code = 'owner'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('COMM.VIEW'),('COMM.OWN'),('COMM.APPROVE'),('REPAIR.INVOICE.CORRECT')) AS p(code)
 WHERE r.code = 'branch_manager'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('COMM.VIEW'),('COMM.PAY')) AS p(code)
 WHERE r.code = 'accountant'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, 'COMM.OWN' FROM iam.role r WHERE r.code = 'technician'
ON CONFLICT DO NOTHING;
