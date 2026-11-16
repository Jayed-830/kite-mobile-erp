-- V0017: Phase 8 - repair and servicing (blueprint 13), customer devices, repair deposits, the WhatsApp gateway (5.3)
-- and the main server's internet address for the WhatsApp webhook.
--
-- A repair job has a branch job number, one of 12 statuses and only the transitions of 13.2; every status change writes
-- a history row (here, by trigger, so none is ever missed). Work that costs the customer more than they pre-authorised
-- waits for an approved estimate: the database refuses Repair in progress, Waiting for parts, Testing and Completed
-- unless the job's current estimate is approved. Parts fitted move at cost from spare-parts stock into repair
-- work-in-progress (1460); a job is delivered only when its work-in-progress is cleared (cost of the repair, warranty
-- expense, back to stock or written off). Deposits are the customer's money held against the job (2300) and never go
-- below zero; at delivery the repair invoice - a normal tax invoice - applies them.
--
-- The WhatsApp gateway: every server queues messages in its own outbox; only the main server talks to the provider
-- (Meta Cloud API, or the test chat). Delivery statuses, button replies (Approve / Call me), texts and STOP come back
-- to the server whose message they answer, as an ordered stream of events.

CREATE SCHEMA IF NOT EXISTS repair;
CREATE SCHEMA IF NOT EXISTS notif;
GRANT USAGE ON SCHEMA repair, notif TO kite_app, kite_ro;
ALTER DEFAULT PRIVILEGES IN SCHEMA repair GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO kite_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA repair GRANT SELECT ON TABLES TO kite_ro;
ALTER DEFAULT PRIVILEGES IN SCHEMA repair GRANT EXECUTE ON FUNCTIONS TO kite_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA repair GRANT USAGE, SELECT ON SEQUENCES TO kite_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA notif GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO kite_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA notif GRANT SELECT ON TABLES TO kite_ro;
ALTER DEFAULT PRIVILEGES IN SCHEMA notif GRANT EXECUTE ON FUNCTIONS TO kite_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA notif GRANT USAGE, SELECT ON SEQUENCES TO kite_app;

-- ================================================================ customer devices (13.1)
-- A device a customer brings in, by IMEI or serial. When the business sold it, it points at that device unit, so intake
-- shows the sale and its warranty. Customers are business-wide, and so are their devices.
CREATE TABLE device.customer_device (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  customer_id       uuid NOT NULL REFERENCES crm.customer(id),
  imei1             text CHECK (imei1 IS NULL OR imei1 ~ '^[0-9]{15}$'),
  imei2             text CHECK (imei2 IS NULL OR imei2 ~ '^[0-9]{15}$'),
  serial            text CHECK (serial IS NULL OR length(trim(serial)) BETWEEN 3 AND 40),
  brand             text,
  model             text NOT NULL CHECK (length(trim(model)) > 0),
  colour            text,
  device_unit_id    uuid REFERENCES device.device_unit(id),
  status            text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  note              text,
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        uuid,
  updated_at        timestamptz NOT NULL DEFAULT now(),
  updated_by        uuid,
  version           integer NOT NULL DEFAULT 1,
  origin_server_id  uuid,
  CHECK (imei1 IS NOT NULL OR serial IS NOT NULL)
);
CREATE UNIQUE INDEX customer_device_imei ON device.customer_device (customer_id, imei1) WHERE imei1 IS NOT NULL;
CREATE UNIQUE INDEX customer_device_serial ON device.customer_device (customer_id, upper(serial)) WHERE imei1 IS NULL AND serial IS NOT NULL;
CREATE INDEX customer_device_imei_any ON device.customer_device (imei1) WHERE imei1 IS NOT NULL;
CREATE INDEX customer_device_serial_any ON device.customer_device (upper(serial)) WHERE serial IS NOT NULL;
CREATE TRIGGER customer_device_touch BEFORE UPDATE ON device.customer_device FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- ================================================================ settings, categories, fault codes
CREATE TABLE repair.settings (
  business_id          uuid PRIMARY KEY REFERENCES core.business(id),
  pre_authorised       numeric(18,3) NOT NULL DEFAULT 5 CHECK (pre_authorised >= 0),       -- default the customer allows without asking
  warranty_days        integer NOT NULL DEFAULT 90 CHECK (warranty_days BETWEEN 0 AND 1095),
  passcode_keep_days   integer NOT NULL DEFAULT 30 CHECK (passcode_keep_days BETWEEN 1 AND 365),
  uncollected_days     integer[] NOT NULL DEFAULT '{30,60,90}' CHECK (cardinality(uncollected_days) BETWEEN 0 AND 6),
  diagnosis_fee        numeric(18,3) NOT NULL DEFAULT 0 CHECK (diagnosis_fee >= 0),
  hourly_rate          numeric(18,3) NOT NULL DEFAULT 10 CHECK (hourly_rate >= 0),         -- labour charged by the hour
  deposit_vat_point    boolean NOT NULL DEFAULT false,                                      -- the accountant's switch (13.7)
  notify_statuses      text[] NOT NULL DEFAULT '{received,waiting_approval,ready}',
  terms_en             text NOT NULL,
  terms_ar             text NOT NULL,
  updated_at           timestamptz NOT NULL DEFAULT now(),
  updated_by           uuid,
  version              integer NOT NULL DEFAULT 1,
  CHECK (notify_statuses <@ ARRAY['received','waiting_approval','ready']::text[])
);
CREATE TRIGGER repair_settings_touch BEFORE UPDATE ON repair.settings FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE repair.category (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  code              text NOT NULL CHECK (code ~ '^[A-Z0-9_]{2,20}$'),
  name_en           text NOT NULL CHECK (length(trim(name_en)) > 0),
  name_ar           text NOT NULL CHECK (length(trim(name_ar)) > 0),
  turnaround_hours  integer NOT NULL DEFAULT 48 CHECK (turnaround_hours BETWEEN 1 AND 2160),
  warranty_days     integer CHECK (warranty_days IS NULL OR warranty_days BETWEEN 0 AND 1095),
  pre_authorised    numeric(18,3) CHECK (pre_authorised IS NULL OR pre_authorised >= 0),
  sort              smallint NOT NULL DEFAULT 0,
  status            text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  version           integer NOT NULL DEFAULT 1,
  UNIQUE (business_id, code)
);
CREATE TRIGGER repair_category_touch BEFORE UPDATE ON repair.category FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE repair.fault_code (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  category_id  uuid REFERENCES repair.category(id),        -- null: offered for every category
  code         text NOT NULL CHECK (code ~ '^[A-Z0-9_]{2,24}$'),
  name_en      text NOT NULL CHECK (length(trim(name_en)) > 0),
  name_ar      text NOT NULL CHECK (length(trim(name_ar)) > 0),
  sort         smallint NOT NULL DEFAULT 0,
  status       text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  updated_at   timestamptz NOT NULL DEFAULT now(),
  version      integer NOT NULL DEFAULT 1,
  UNIQUE (business_id, code)
);
CREATE TRIGGER repair_fault_touch BEFORE UPDATE ON repair.fault_code FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- ================================================================ the lifecycle (13.2)
CREATE TABLE repair.transition (
  from_status  text NOT NULL,
  to_status    text NOT NULL,
  PRIMARY KEY (from_status, to_status)
);
INSERT INTO repair.transition (from_status, to_status) VALUES
  ('received','inspection_pending'), ('received','cancelled'),
  ('inspection_pending','under_diagnosis'), ('inspection_pending','cancelled'),
  ('under_diagnosis','waiting_approval'), ('under_diagnosis','in_progress'), ('under_diagnosis','unrepairable'),
  ('waiting_approval','waiting_parts'), ('waiting_approval','in_progress'), ('waiting_approval','cancelled'),
  ('waiting_parts','in_progress'), ('waiting_parts','cancelled'),
  ('in_progress','testing'), ('in_progress','waiting_approval'), ('in_progress','waiting_parts'), ('in_progress','unrepairable'),
  ('testing','completed'), ('testing','in_progress'),
  ('completed','ready'),
  ('ready','delivered'),
  ('cancelled','ready'),
  ('unrepairable','ready');
CREATE TRIGGER repair_transition_immutable BEFORE INSERT OR UPDATE OR DELETE ON repair.transition FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

CREATE TABLE repair.repair_job (
  id                   uuid PRIMARY KEY,
  business_id          uuid NOT NULL REFERENCES core.business(id),
  branch_id            uuid NOT NULL REFERENCES core.branch(id),
  number               text NOT NULL,
  status               text NOT NULL DEFAULT 'received' CHECK (status IN ('received','inspection_pending','under_diagnosis','waiting_approval',
                         'waiting_parts','in_progress','testing','completed','ready','delivered','cancelled','unrepairable')),
  outcome              text CHECK (outcome IN ('repaired','cancelled','unrepairable')),
  customer_id          uuid NOT NULL REFERENCES crm.customer(id),
  customer_device_id   uuid NOT NULL REFERENCES device.customer_device(id),
  category_id          uuid NOT NULL REFERENCES repair.category(id),
  technician_id        uuid REFERENCES iam."user"(id),
  warranty_of_id       uuid REFERENCES repair.repair_job(id),    -- a warranty claim on this delivered job (13.6)
  sold_sale_id         uuid REFERENCES sales.sale(id),           -- the sale of the device, when the business sold it
  problem              text NOT NULL CHECK (length(trim(problem)) > 0),
  fault_codes          text[] NOT NULL DEFAULT '{}',
  condition            jsonb NOT NULL DEFAULT '{}',
  condition_notes      text,
  accessories          jsonb NOT NULL DEFAULT '{}',
  passcode_mode        text NOT NULL CHECK (passcode_mode IN ('none','customer_present','provided')),
  passcode_enc         bytea,                                    -- encrypted with this server's key; never printed, never synced
  passcode_purged_at   timestamptz,
  pre_authorised       numeric(18,3) NOT NULL DEFAULT 0 CHECK (pre_authorised >= 0),
  warranty_days        integer NOT NULL CHECK (warranty_days BETWEEN 0 AND 1095),
  promised_at          timestamptz NOT NULL,
  terms_name           text NOT NULL CHECK (length(trim(terms_name)) > 0),
  terms_accepted_at    timestamptz NOT NULL,
  notify_whatsapp      boolean NOT NULL DEFAULT false,
  lang                 text NOT NULL DEFAULT 'ar' CHECK (lang IN ('en','ar')),
  delivery_sale_id     uuid REFERENCES sales.sale(id),
  delivered_at         timestamptz,
  delivered_by         uuid,
  collected_by         text,
  collection_check     text CHECK (collection_check IN ('qr','verified')),
  warranty_until       date,
  reminded_days        integer NOT NULL DEFAULT 0,
  created_at           timestamptz NOT NULL DEFAULT now(),
  created_by           uuid NOT NULL,
  updated_at           timestamptz NOT NULL DEFAULT now(),
  updated_by           uuid,
  version              integer NOT NULL DEFAULT 1,
  origin_server_id     uuid,
  UNIQUE (business_id, number),
  CHECK (passcode_mode = 'provided' OR passcode_enc IS NULL),
  CHECK (passcode_enc IS NULL OR passcode_purged_at IS NULL),
  CHECK ((status IN ('completed','ready','delivered','cancelled','unrepairable')) = (outcome IS NOT NULL)),
  CHECK (status <> 'completed' OR outcome = 'repaired'),
  CHECK (status <> 'cancelled' OR outcome = 'cancelled'),
  CHECK (status <> 'unrepairable' OR outcome = 'unrepairable'),
  CHECK ((status = 'delivered') = (delivered_at IS NOT NULL)),
  CHECK (status <> 'delivered' OR (collection_check IS NOT NULL AND length(trim(coalesce(collected_by, ''))) > 0)),
  CHECK (warranty_until IS NULL OR status = 'delivered'),
  CHECK (warranty_of_id IS NULL OR warranty_of_id <> id)
);
CREATE INDEX repair_job_board ON repair.repair_job (branch_id, status, promised_at);
CREATE INDEX repair_job_customer ON repair.repair_job (customer_id);
CREATE INDEX repair_job_device ON repair.repair_job (customer_device_id);
CREATE INDEX repair_job_technician ON repair.repair_job (technician_id) WHERE technician_id IS NOT NULL;
CREATE INDEX repair_job_warranty_of ON repair.repair_job (warranty_of_id) WHERE warranty_of_id IS NOT NULL;

-- ================================================================ estimates (13.3)
CREATE TABLE repair.estimate (
  id                  uuid PRIMARY KEY,
  business_id         uuid NOT NULL REFERENCES core.business(id),
  branch_id           uuid NOT NULL REFERENCES core.branch(id),
  job_id              uuid NOT NULL REFERENCES repair.repair_job(id),
  estimate_no         integer NOT NULL CHECK (estimate_no >= 1),
  status              text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','sent','approved','declined','superseded')),
  net_total           numeric(18,3) NOT NULL DEFAULT 0,
  tax_total           numeric(18,3) NOT NULL DEFAULT 0,
  total               numeric(18,3) NOT NULL DEFAULT 0 CHECK (total >= 0),
  note                text,
  sent_at             timestamptz,
  approval_method     text CHECK (approval_method IN ('signature','phone','whatsapp','pre_authorised','warranty')),
  approved_at         timestamptz,
  approved_by         uuid,                     -- the member of staff who recorded it (null: the customer's WhatsApp reply)
  approver_name       text,                     -- the customer, or the person spoken to on the phone
  approval_ref        text,                     -- the phone number called, or the WhatsApp message ID
  decline_reason      text,
  declined_at         timestamptz,
  created_at          timestamptz NOT NULL DEFAULT now(),
  created_by          uuid NOT NULL,
  updated_at          timestamptz NOT NULL DEFAULT now(),
  updated_by          uuid,
  version             integer NOT NULL DEFAULT 1,
  UNIQUE (job_id, estimate_no),
  CHECK (total = net_total + tax_total),
  CHECK ((status = 'approved') = (approved_at IS NOT NULL AND approval_method IS NOT NULL)),
  CHECK ((status = 'declined') = (declined_at IS NOT NULL)),
  CHECK (approval_method IS DISTINCT FROM 'signature' OR length(trim(coalesce(approver_name, ''))) > 0),
  CHECK (approval_method IS DISTINCT FROM 'phone' OR (length(trim(coalesce(approver_name, ''))) > 0 AND length(trim(coalesce(approval_ref, ''))) > 0)),
  CHECK (approval_method IS DISTINCT FROM 'whatsapp' OR length(trim(coalesce(approval_ref, ''))) > 0)
);
CREATE INDEX estimate_job ON repair.estimate (job_id, estimate_no DESC);

CREATE TABLE repair.estimate_line (
  id            uuid PRIMARY KEY,
  estimate_id   uuid NOT NULL REFERENCES repair.estimate(id),
  line_no       smallint NOT NULL CHECK (line_no >= 1),
  kind          text NOT NULL CHECK (kind IN ('labour','part','extra')),
  variant_id    uuid NOT NULL REFERENCES catalog.variant(id),
  description_en text NOT NULL,
  description_ar text NOT NULL,
  qty           numeric(14,3) NOT NULL CHECK (qty > 0),
  minutes       integer CHECK (minutes IS NULL OR minutes BETWEEN 1 AND 6000),   -- labour charged by the hour
  unit_price    numeric(18,3) NOT NULL CHECK (unit_price >= 0),
  tax_code_id   uuid NOT NULL REFERENCES tax.tax_code(id),
  tax_rate      numeric(6,3) NOT NULL,
  net_amount    numeric(18,3) NOT NULL,
  tax_amount    numeric(18,3) NOT NULL,
  gross_amount  numeric(18,3) NOT NULL CHECK (gross_amount >= 0),
  UNIQUE (estimate_id, line_no),
  CHECK (gross_amount = net_amount + tax_amount),
  CHECK (minutes IS NULL OR kind = 'labour')
);

-- The current estimate of a job: its highest version.
CREATE FUNCTION repair.current_estimate(p_job uuid) RETURNS repair.estimate
LANGUAGE sql STABLE AS $$
  SELECT * FROM repair.estimate WHERE job_id = p_job ORDER BY estimate_no DESC LIMIT 1
$$;

-- What staff may approve without asking the customer again: the larger of what the customer pre-authorised and the
-- largest total the customer already approved for this job (13.3).
CREATE FUNCTION repair.authorised_ceiling(p_job uuid, p_except uuid DEFAULT NULL) RETURNS numeric
LANGUAGE sql STABLE AS $$
  SELECT greatest(j.pre_authorised,
                  coalesce((SELECT max(e.total) FROM repair.estimate e
                             WHERE e.job_id = j.id AND e.status = 'approved' AND e.approval_method IN ('signature','phone','whatsapp')
                               AND e.id IS DISTINCT FROM p_except), 0))
    FROM repair.repair_job j WHERE j.id = p_job
$$;

CREATE FUNCTION repair.estimate_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE j repair.repair_job%ROWTYPE; top integer; sums record; ceiling numeric;
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'KITE: an estimate is never deleted; write a new version' USING ERRCODE = 'KT041';
  END IF;
  SELECT * INTO j FROM repair.repair_job WHERE id = NEW.job_id;
  IF TG_OP = 'INSERT' THEN
    IF j.status NOT IN ('received','inspection_pending','under_diagnosis','waiting_approval','waiting_parts','in_progress','testing') THEN
      RAISE EXCEPTION 'KITE: job % is %; it cannot have a new estimate', j.number, replace(j.status, '_', ' ') USING ERRCODE = 'KT041';
    END IF;
    SELECT coalesce(max(estimate_no), 0) INTO top FROM repair.estimate WHERE job_id = NEW.job_id;
    IF NEW.estimate_no <> top + 1 THEN
      RAISE EXCEPTION 'KITE: the next estimate of job % is version %', j.number, top + 1 USING ERRCODE = 'KT041';
    END IF;
    IF NEW.status <> 'draft' THEN
      RAISE EXCEPTION 'KITE: an estimate starts as a draft' USING ERRCODE = 'KT041';
    END IF;
    RETURN NEW;
  END IF;
  -- UPDATE
  IF OLD.status IN ('approved','declined','superseded') THEN
    RAISE EXCEPTION 'KITE: version % of the estimate for job % is % and cannot change', OLD.estimate_no, j.number, OLD.status USING ERRCODE = 'KT041';
  END IF;
  IF NEW.job_id <> OLD.job_id OR NEW.estimate_no <> OLD.estimate_no OR NEW.branch_id <> OLD.branch_id THEN
    RAISE EXCEPTION 'KITE: an estimate stays with its job and version' USING ERRCODE = 'KT041';
  END IF;
  IF OLD.status = 'sent' AND NEW.status = 'draft' THEN
    RAISE EXCEPTION 'KITE: a sent estimate is changed by writing a new version' USING ERRCODE = 'KT041';
  END IF;
  IF OLD.status <> 'draft' AND (NEW.total, NEW.net_total, NEW.tax_total) IS DISTINCT FROM (OLD.total, OLD.net_total, OLD.tax_total) THEN
    RAISE EXCEPTION 'KITE: the total of a sent estimate cannot change; write a new version' USING ERRCODE = 'KT041';
  END IF;
  IF OLD.status = 'draft' AND NEW.status <> 'draft' AND NEW.status <> 'superseded' THEN
    SELECT coalesce(sum(net_amount), 0) AS net, coalesce(sum(tax_amount), 0) AS tax, coalesce(sum(gross_amount), 0) AS gross, count(*) AS n
      INTO sums FROM repair.estimate_line WHERE estimate_id = NEW.id;
    IF (sums.net, sums.tax, sums.gross) IS DISTINCT FROM (NEW.net_total, NEW.tax_total, NEW.total) THEN
      RAISE EXCEPTION 'KITE: the estimate''s totals must be the sum of its lines' USING ERRCODE = 'KT041';
    END IF;
    IF sums.n = 0 AND NEW.approval_method IS DISTINCT FROM 'warranty' THEN
      RAISE EXCEPTION 'KITE: an estimate needs at least one line' USING ERRCODE = 'KT041';
    END IF;
  END IF;
  IF NEW.status IN ('approved','declined') AND OLD.status NOT IN ('approved','declined') THEN
    IF EXISTS (SELECT 1 FROM repair.estimate WHERE job_id = NEW.job_id AND estimate_no > NEW.estimate_no) THEN
      RAISE EXCEPTION 'KITE: only the current version of an estimate is approved or declined' USING ERRCODE = 'KT041';
    END IF;
  END IF;
  IF NEW.status = 'approved' AND OLD.status <> 'approved' THEN
    IF NEW.approval_method = 'pre_authorised' THEN
      ceiling := repair.authorised_ceiling(NEW.job_id, NEW.id);
      IF NEW.total > ceiling THEN
        RAISE EXCEPTION 'KITE: % is more than the customer authorised (%); the customer approves it', NEW.total, ceiling USING ERRCODE = 'KT041';
      END IF;
    ELSIF NEW.approval_method = 'warranty' THEN
      IF j.warranty_of_id IS NULL OR NEW.total <> 0 THEN
        RAISE EXCEPTION 'KITE: only a warranty claim has a zero-charge warranty estimate' USING ERRCODE = 'KT041';
      END IF;
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER estimate_guard BEFORE INSERT OR UPDATE OR DELETE ON repair.estimate FOR EACH ROW EXECUTE FUNCTION repair.estimate_guard();
CREATE TRIGGER estimate_touch BEFORE UPDATE ON repair.estimate FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- A new version supersedes the drafts and sent versions before it (an approved or declined version stays as history).
CREATE FUNCTION repair.estimate_supersede() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  UPDATE repair.estimate SET status = 'superseded', updated_by = NEW.created_by
   WHERE job_id = NEW.job_id AND id <> NEW.id AND status IN ('draft','sent');
  RETURN NULL;
END $$;
CREATE TRIGGER estimate_supersede AFTER INSERT ON repair.estimate FOR EACH ROW EXECUTE FUNCTION repair.estimate_supersede();

CREATE FUNCTION repair.estimate_line_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE st text;
BEGIN
  SELECT status INTO st FROM repair.estimate WHERE id = coalesce(NEW.estimate_id, OLD.estimate_id);
  IF st IS DISTINCT FROM 'draft' THEN
    RAISE EXCEPTION 'KITE: the lines of an estimate change only while it is a draft' USING ERRCODE = 'KT041';
  END IF;
  IF TG_OP = 'UPDATE' AND NEW.estimate_id <> OLD.estimate_id THEN
    RAISE EXCEPTION 'KITE: a line stays on its estimate' USING ERRCODE = 'KT041';
  END IF;
  RETURN coalesce(NEW, OLD);
END $$;
CREATE TRIGGER estimate_line_guard BEFORE INSERT OR UPDATE OR DELETE ON repair.estimate_line FOR EACH ROW EXECUTE FUNCTION repair.estimate_line_guard();

-- ================================================================ parts and labour on the job (13.4)
-- The work log: parts fitted (+qty, at cost, into work-in-progress) and returned (-qty, back to stock at what they went
-- out at), and labour time. Charges come from the approved estimate; this is what the work cost.
CREATE TABLE repair.repair_line (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  branch_id         uuid NOT NULL REFERENCES core.branch(id),
  job_id            uuid NOT NULL REFERENCES repair.repair_job(id),
  kind              text NOT NULL CHECK (kind IN ('part','labour')),
  variant_id        uuid REFERENCES catalog.variant(id),
  product_type      text,
  qty               numeric(14,3),
  cost              numeric(18,3) NOT NULL DEFAULT 0,          -- part: + fitted, - returned (signed, as the stock moved)
  stock_movement_id uuid REFERENCES inv.stock_movement(id),
  return_of_id      uuid REFERENCES repair.repair_line(id),
  minutes           integer CHECK (minutes IS NULL OR minutes BETWEEN 1 AND 6000),
  technician_id     uuid REFERENCES iam."user"(id),
  journal_id        uuid,
  note              text,
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        uuid NOT NULL,
  origin_server_id  uuid,
  CHECK (kind <> 'part' OR (variant_id IS NOT NULL AND qty IS NOT NULL AND qty <> 0 AND stock_movement_id IS NOT NULL AND product_type IS NOT NULL)),
  CHECK (kind <> 'part' OR (qty > 0) = (return_of_id IS NULL)),
  CHECK (kind <> 'part' OR (qty > 0) = (cost >= 0)),
  CHECK (kind <> 'labour' OR (minutes IS NOT NULL AND variant_id IS NULL AND cost = 0 AND return_of_id IS NULL))
);
CREATE INDEX repair_line_job ON repair.repair_line (job_id);
CREATE TRIGGER repair_line_immutable BEFORE UPDATE OR DELETE ON repair.repair_line FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

CREATE FUNCTION repair.repair_line_check() RETURNS trigger
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
  IF NEW.kind = 'part' AND NEW.qty < 0 THEN
    SELECT * INTO fitted FROM repair.repair_line WHERE id = NEW.return_of_id;
    IF NOT FOUND OR fitted.job_id <> NEW.job_id OR fitted.kind <> 'part' OR fitted.qty <= 0 OR fitted.variant_id <> NEW.variant_id THEN
      RAISE EXCEPTION 'KITE: a returned part is returned against the part fitted on the same job' USING ERRCODE = 'KT041';
    END IF;
    SELECT coalesce(-sum(qty), 0) INTO back FROM repair.repair_line WHERE return_of_id = NEW.return_of_id;
    IF back - NEW.qty > fitted.qty THEN
      RAISE EXCEPTION 'KITE: more would be returned than was fitted' USING ERRCODE = 'KT041';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER repair_line_check BEFORE INSERT ON repair.repair_line FOR EACH ROW EXECUTE FUNCTION repair.repair_line_check();

-- How work-in-progress leaves a job: as the cost of the repair at delivery, as warranty expense (a warranty claim), or
-- written off with a reason (a cancelled job's part that cannot go back to stock).
CREATE TABLE repair.wip_clearing (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  branch_id         uuid NOT NULL REFERENCES core.branch(id),
  job_id            uuid NOT NULL REFERENCES repair.repair_job(id),
  kind              text NOT NULL CHECK (kind IN ('cost_of_repair','warranty','written_off')),
  amount            numeric(18,3) NOT NULL CHECK (amount > 0),
  line_id           uuid REFERENCES repair.repair_line(id),     -- the fitted part written off
  reason            text,
  journal_id        uuid,
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        uuid NOT NULL,
  CHECK (kind <> 'written_off' OR (length(trim(coalesce(reason, ''))) > 0 AND line_id IS NOT NULL))
);
CREATE INDEX wip_clearing_job ON repair.wip_clearing (job_id);
CREATE TRIGGER wip_clearing_immutable BEFORE UPDATE OR DELETE ON repair.wip_clearing FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

-- Work-in-progress still on a job (what 1460 holds for it).
CREATE FUNCTION repair.wip(p_job uuid) RETURNS numeric
LANGUAGE sql STABLE AS $$
  SELECT coalesce((SELECT sum(cost) FROM repair.repair_line WHERE job_id = p_job AND kind = 'part'), 0)
       - coalesce((SELECT sum(amount) FROM repair.wip_clearing WHERE job_id = p_job), 0)
$$;

CREATE FUNCTION repair.wip_clearing_check() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  PERFORM 1 FROM repair.repair_job WHERE id = NEW.job_id FOR UPDATE;
  IF repair.wip(NEW.job_id) < 0 THEN
    RAISE EXCEPTION 'KITE: more would leave work-in-progress than the job holds' USING ERRCODE = 'KT041';
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER wip_clearing_check AFTER INSERT ON repair.wip_clearing FOR EACH ROW EXECUTE FUNCTION repair.wip_clearing_check();

-- ================================================================ photos and signatures (13.1, 13.5)
-- Each stored with its SHA-256, which the database checks against the bytes.
CREATE TABLE repair.photo (
  id            uuid PRIMARY KEY,
  business_id   uuid NOT NULL REFERENCES core.business(id),
  branch_id     uuid NOT NULL REFERENCES core.branch(id),
  job_id        uuid NOT NULL REFERENCES repair.repair_job(id),
  stage         text NOT NULL CHECK (stage IN ('intake','diagnosis','delivery','terms_signature','approval_signature','collection_signature')),
  content_type  text NOT NULL CHECK (content_type IN ('image/jpeg','image/png','image/webp')),
  size_bytes    integer NOT NULL CHECK (size_bytes BETWEEN 1 AND 3145728),
  sha256        text NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
  content       bytea NOT NULL,
  caption       text,
  estimate_id   uuid REFERENCES repair.estimate(id),
  created_at    timestamptz NOT NULL DEFAULT now(),
  created_by    uuid NOT NULL,
  CHECK (octet_length(content) = size_bytes),
  CHECK (sha256 = encode(sha256(content), 'hex'))
);
CREATE INDEX photo_job ON repair.photo (job_id, stage);
CREATE TRIGGER photo_immutable BEFORE UPDATE OR DELETE ON repair.photo FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

-- ================================================================ the job's guard and history
CREATE FUNCTION repair.job_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
  after_delivery text[] := ARRAY['passcode_enc','passcode_purged_at','updated_at','updated_by','version'];
  est repair.estimate%ROWTYPE;
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'KITE: a repair job is never deleted; cancel it' USING ERRCODE = 'KT040';
  END IF;
  IF (NEW.number, NEW.branch_id, NEW.business_id, NEW.customer_id, NEW.customer_device_id, NEW.created_by)
     IS DISTINCT FROM (OLD.number, OLD.branch_id, OLD.business_id, OLD.customer_id, OLD.customer_device_id, OLD.created_by)
     OR NEW.warranty_of_id IS DISTINCT FROM OLD.warranty_of_id THEN
    RAISE EXCEPTION 'KITE: a job keeps its number, branch, customer and device' USING ERRCODE = 'KT041';
  END IF;
  IF OLD.status = 'delivered' THEN
    IF (SELECT jsonb_object_agg(k, v) FROM jsonb_each(to_jsonb(NEW)) AS e(k, v) WHERE NOT k = ANY(after_delivery))
       IS DISTINCT FROM (SELECT jsonb_object_agg(k, v) FROM jsonb_each(to_jsonb(OLD)) AS e(k, v) WHERE NOT k = ANY(after_delivery)) THEN
      RAISE EXCEPTION 'KITE: job % was delivered and cannot change; a warranty claim opens a new job', OLD.number USING ERRCODE = 'KT040';
    END IF;
    RETURN NEW;
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status
     AND NOT EXISTS (SELECT 1 FROM repair.transition WHERE from_status = OLD.status AND to_status = NEW.status) THEN
    RAISE EXCEPTION 'KITE: a job that is % cannot move to %', replace(OLD.status, '_', ' '), replace(NEW.status, '_', ' ') USING ERRCODE = 'KT040';
  END IF;
  IF OLD.outcome IS NOT NULL AND NEW.outcome IS DISTINCT FROM OLD.outcome THEN
    RAISE EXCEPTION 'KITE: job % already ended as %', OLD.number, OLD.outcome USING ERRCODE = 'KT041';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status THEN
    -- Work goes ahead only on an approved estimate (13.3).
    IF NEW.status IN ('in_progress','waiting_parts','testing','completed') THEN
      est := repair.current_estimate(NEW.id);
      IF est.id IS NULL OR est.status <> 'approved' THEN
        RAISE EXCEPTION 'KITE: job % has no approved estimate, so the work cannot go ahead', NEW.number USING ERRCODE = 'KT041';
      END IF;
    END IF;
    IF NEW.status = 'delivered' AND repair.wip(NEW.id) <> 0 THEN
      RAISE EXCEPTION 'KITE: job % still holds parts in work-in-progress; return them or write them off first', NEW.number USING ERRCODE = 'KT041';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER job_guard BEFORE UPDATE OR DELETE ON repair.repair_job FOR EACH ROW EXECUTE FUNCTION repair.job_guard();
CREATE TRIGGER job_touch BEFORE UPDATE ON repair.repair_job FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE repair.status_history (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  business_id   uuid NOT NULL,
  branch_id     uuid NOT NULL REFERENCES core.branch(id),
  job_id        uuid NOT NULL REFERENCES repair.repair_job(id),
  from_status   text,
  to_status     text NOT NULL,
  note          text,
  user_id       uuid,
  at            timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX status_history_job ON repair.status_history (job_id, id);
CREATE TRIGGER status_history_immutable BEFORE UPDATE OR DELETE ON repair.status_history FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

-- Every status a job takes is written here by the database itself; the note comes from the transaction
-- (SET LOCAL kite.repair_note), the user from updated_by (created_by for the first row).
CREATE FUNCTION repair.job_history() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'received' THEN
      RAISE EXCEPTION 'KITE: a job starts as received' USING ERRCODE = 'KT040';
    END IF;
    INSERT INTO repair.status_history (business_id, branch_id, job_id, from_status, to_status, note, user_id)
    VALUES (NEW.business_id, NEW.branch_id, NEW.id, NULL, NEW.status, nullif(current_setting('kite.repair_note', true), ''), NEW.created_by);
  ELSIF NEW.status IS DISTINCT FROM OLD.status THEN
    INSERT INTO repair.status_history (business_id, branch_id, job_id, from_status, to_status, note, user_id)
    VALUES (NEW.business_id, NEW.branch_id, NEW.id, OLD.status, NEW.status, nullif(current_setting('kite.repair_note', true), ''), NEW.updated_by);
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER job_history AFTER INSERT OR UPDATE OF status ON repair.repair_job FOR EACH ROW EXECUTE FUNCTION repair.job_history();

-- Reassignments of the technician are logged (13.6).
CREATE TABLE repair.assignment (
  id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  branch_id      uuid NOT NULL REFERENCES core.branch(id),
  job_id         uuid NOT NULL REFERENCES repair.repair_job(id),
  from_user_id   uuid,
  to_user_id     uuid,
  by_user_id     uuid,
  at             timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TRIGGER assignment_immutable BEFORE UPDATE OR DELETE ON repair.assignment FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
CREATE FUNCTION repair.job_assignment() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
  IF NEW.technician_id IS DISTINCT FROM OLD.technician_id THEN
    INSERT INTO repair.assignment (branch_id, job_id, from_user_id, to_user_id, by_user_id)
    VALUES (NEW.branch_id, NEW.id, OLD.technician_id, NEW.technician_id, NEW.updated_by);
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER job_assignment AFTER UPDATE OF technician_id ON repair.repair_job FOR EACH ROW EXECUTE FUNCTION repair.job_assignment();

-- Who looked at a provided passcode, and when (13.1: every view logged).
CREATE TABLE repair.credential_view (
  id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  branch_id  uuid NOT NULL REFERENCES core.branch(id),
  job_id     uuid NOT NULL REFERENCES repair.repair_job(id),
  user_id    uuid NOT NULL,
  at         timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TRIGGER credential_view_immutable BEFORE UPDATE OR DELETE ON repair.credential_view FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

-- ================================================================ deposits (13.1, 13.5, 14.1)
-- The customer ledger gets the deposit balance type: a deposit is held against one job; + taken, - applied or refunded.
ALTER TABLE crm.credit_ledger DROP CONSTRAINT credit_ledger_balance_type_check;
ALTER TABLE crm.credit_ledger ADD CONSTRAINT credit_ledger_balance_type_check CHECK (balance_type IN ('store_credit','deposit'));
ALTER TABLE crm.credit_ledger ADD COLUMN repair_job_id uuid REFERENCES repair.repair_job(id);
ALTER TABLE crm.credit_ledger ADD CONSTRAINT credit_ledger_deposit_job CHECK ((balance_type = 'deposit') = (repair_job_id IS NOT NULL));
CREATE INDEX credit_ledger_job ON crm.credit_ledger (repair_job_id) WHERE repair_job_id IS NOT NULL;

-- Store credit never goes below zero for the customer; a deposit never goes below zero for its job.
CREATE OR REPLACE FUNCTION crm.credit_ledger_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE bal numeric;
BEGIN
  IF NEW.balance_type = 'deposit' THEN
    PERFORM pg_advisory_xact_lock(hashtextextended('kite:deposit:' || NEW.repair_job_id::text, 0));
    IF NEW.amount < 0 THEN
      SELECT coalesce(sum(amount), 0) INTO bal FROM crm.credit_ledger WHERE repair_job_id = NEW.repair_job_id AND balance_type = 'deposit';
      IF bal + NEW.amount < 0 THEN
        RAISE EXCEPTION 'KITE: the job holds % in deposits; % cannot be used', bal, -NEW.amount USING ERRCODE = 'KT042';
      END IF;
    END IF;
    RETURN NEW;
  END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('kite:store_credit:' || NEW.customer_id::text, 0));
  IF NEW.amount < 0 THEN
    SELECT coalesce(sum(amount), 0) INTO bal FROM crm.credit_ledger WHERE customer_id = NEW.customer_id AND balance_type = NEW.balance_type;
    IF bal + NEW.amount < 0 THEN
      RAISE EXCEPTION 'KITE: the customer has % store credit; % cannot be used', bal, -NEW.amount USING ERRCODE = 'KT032';
    END IF;
  END IF;
  RETURN NEW;
END $$;

-- The job's deposit receipts and refunds (numbered), and the amounts applied to its invoice.
CREATE TABLE repair.deposit (
  id                     uuid PRIMARY KEY,
  business_id            uuid NOT NULL REFERENCES core.business(id),
  branch_id              uuid NOT NULL REFERENCES core.branch(id),
  job_id                 uuid NOT NULL REFERENCES repair.repair_job(id),
  number                 text,
  kind                   text NOT NULL CHECK (kind IN ('deposit','refund','applied')),
  amount                 numeric(18,3) NOT NULL CHECK (amount > 0),
  tax                    numeric(18,3) NOT NULL DEFAULT 0 CHECK (tax >= 0 AND tax <= amount),   -- VAT in it when deposits are a VAT point
  method                 text CHECK (method IN ('cash','card','bank_transfer','store_credit')),
  shift_id               uuid REFERENCES sales.shift(id),
  terminal_id            uuid REFERENCES core.terminal(id),
  sale_id                uuid REFERENCES sales.sale(id),
  credit_entry_id        uuid NOT NULL REFERENCES crm.credit_ledger(id),
  store_credit_entry_id  uuid REFERENCES crm.credit_ledger(id),
  reason                 text,
  journal_id             uuid,
  created_at             timestamptz NOT NULL DEFAULT now(),
  created_by             uuid NOT NULL,
  origin_server_id       uuid,
  UNIQUE (business_id, number),
  CHECK ((kind = 'applied') = (number IS NULL)),
  CHECK ((kind = 'applied') = (sale_id IS NOT NULL)),
  CHECK (kind = 'applied' OR method IS NOT NULL),
  CHECK (kind <> 'applied' OR method IS NULL),
  CHECK (kind <> 'deposit' OR method <> 'store_credit'),
  CHECK ((method IN ('cash','card','bank_transfer')) = (shift_id IS NOT NULL)),
  CHECK ((method = 'store_credit') = (store_credit_entry_id IS NOT NULL)),
  CHECK (kind <> 'refund' OR length(trim(coalesce(reason, ''))) > 0)
);
CREATE INDEX deposit_job ON repair.deposit (job_id);
CREATE TRIGGER deposit_immutable BEFORE UPDATE OR DELETE ON repair.deposit FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

CREATE FUNCTION repair.deposit_balance(p_job uuid) RETURNS numeric
LANGUAGE sql STABLE AS $$
  SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE repair_job_id = p_job AND balance_type = 'deposit'
$$;

-- The VAT still held in a job's deposits (deposits as a VAT point): taken back when they are applied or refunded.
CREATE FUNCTION repair.deposit_tax(p_job uuid) RETURNS numeric
LANGUAGE sql STABLE AS $$
  SELECT coalesce(sum(CASE WHEN kind = 'deposit' THEN tax ELSE -tax END), 0) FROM repair.deposit WHERE job_id = p_job
$$;

-- Deposits are tenders on the drawer: a deposit payment has no sale, it has the deposit receipt.
ALTER TABLE sales.payment ALTER COLUMN sale_id DROP NOT NULL;
ALTER TABLE sales.payment ADD COLUMN repair_deposit_id uuid REFERENCES repair.deposit(id);
ALTER TABLE sales.payment ADD CONSTRAINT payment_document CHECK ((sale_id IS NULL) <> (repair_deposit_id IS NULL));
ALTER TABLE sales.payment DROP CONSTRAINT payment_method_check;
ALTER TABLE sales.payment ADD CONSTRAINT payment_method_check CHECK (method IN ('cash','card','bank_transfer','trade_in','store_credit','deposit'));
ALTER TABLE sales.payment DROP CONSTRAINT payment_store_credit;
ALTER TABLE sales.payment ADD CONSTRAINT payment_store_credit CHECK ((method IN ('store_credit','deposit')) = (credit_entry_id IS NOT NULL));
ALTER TABLE sales.payment ADD CONSTRAINT payment_deposit_sale CHECK (method <> 'deposit' OR sale_id IS NOT NULL);
ALTER TABLE sales.payment ADD CONSTRAINT payment_receipt_tenders CHECK (repair_deposit_id IS NULL OR method IN ('cash','card','bank_transfer'));
CREATE INDEX payment_repair_deposit ON sales.payment (repair_deposit_id) WHERE repair_deposit_id IS NOT NULL;

-- The repair invoice is a normal tax invoice that knows its job.
ALTER TABLE sales.sale ADD COLUMN repair_job_id uuid REFERENCES repair.repair_job(id);
CREATE INDEX sale_repair_job ON sales.sale (repair_job_id) WHERE repair_job_id IS NOT NULL;

ALTER TABLE inv.stock_movement DROP CONSTRAINT stock_movement_movement_type_check;
ALTER TABLE inv.stock_movement ADD CONSTRAINT stock_movement_movement_type_check CHECK (movement_type IN
  ('opening','adjustment','count','sale','return','emergency_sale','purchase_receipt','supplier_return','transfer_out','transfer_in',
   'unit_cost','refurb_issue','refurb_return','tradein_acquisition','repair_issue','repair_return'));

-- ================================================================ WhatsApp (5.3): consent and the outbox on every server
ALTER TABLE crm.customer ADD COLUMN whatsapp_opt_in boolean NOT NULL DEFAULT false;
ALTER TABLE crm.customer ADD COLUMN whatsapp_opt_in_at timestamptz;
ALTER TABLE crm.customer ADD COLUMN lang text NOT NULL DEFAULT 'ar' CHECK (lang IN ('en','ar'));
ALTER TABLE crm.customer ADD CONSTRAINT customer_whatsapp CHECK (NOT whatsapp_opt_in OR (phone_e164 IS NOT NULL AND whatsapp_opt_in_at IS NOT NULL));

-- Every change of a customer's WhatsApp consent, append-only.
CREATE TABLE notif.consent (
  id            uuid PRIMARY KEY,
  business_id   uuid NOT NULL REFERENCES core.business(id),
  branch_id     uuid NOT NULL REFERENCES core.branch(id),
  customer_id   uuid NOT NULL REFERENCES crm.customer(id),
  phone_e164    text NOT NULL,
  action        text NOT NULL CHECK (action IN ('opt_in','opt_out')),
  source        text NOT NULL CHECK (source IN ('intake','customer_reply','staff')),
  note          text,
  at            timestamptz NOT NULL DEFAULT now(),
  by_user_id    uuid
);
CREATE INDEX consent_customer ON notif.consent (customer_id, at);
CREATE TRIGGER consent_immutable BEFORE UPDATE OR DELETE ON notif.consent FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

-- What this server wants sent. Branch servers hand their messages to the main server; the main server's own go
-- straight to its gateway queue. Passcodes and civil IDs are never in a message (the server builds them from templates).
CREATE TABLE notif.outbound_message (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  customer_id      uuid REFERENCES crm.customer(id),
  phone_e164       text NOT NULL CHECK (phone_e164 ~ '^\+[1-9][0-9]{6,14}$'),
  lang             text NOT NULL CHECK (lang IN ('en','ar')),
  template         text CHECK (template IN ('repair_received','repair_estimate','repair_ready','repair_uncollected')),
  params           jsonb NOT NULL DEFAULT '[]',
  body_text        text CHECK (body_text IS NULL OR length(body_text) BETWEEN 1 AND 1000),
  source_doc_type  text,
  source_doc_id    uuid,
  status           text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','relayed','sent','delivered','read','failed','dropped','blocked')),
  status_reason    text,
  status_at        timestamptz,
  wamid            text,
  attempts         integer NOT NULL DEFAULT 0,
  opt_in_at        timestamptz,                                  -- the consent it is sent under (null for replies in the 24-hour window)
  expires_at       timestamptz NOT NULL,
  relayed_at       timestamptz,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid,
  origin_server_id uuid,
  CHECK ((template IS NULL) <> (body_text IS NULL))
);
CREATE INDEX outbound_queue ON notif.outbound_message (created_at) WHERE status = 'queued';
CREATE INDEX outbound_source ON notif.outbound_message (source_doc_type, source_doc_id);
CREATE INDEX outbound_phone ON notif.outbound_message (phone_e164, created_at DESC);

-- Messages from customers that reached this server (their replies to our messages): texts, button taps, STOP.
CREATE TABLE notif.inbound_message (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  customer_id      uuid REFERENCES crm.customer(id),
  phone_e164       text NOT NULL,
  kind             text NOT NULL CHECK (kind IN ('text','button','stop','start')),
  body             text,
  reply_to_id      uuid REFERENCES notif.outbound_message(id),
  wamid            text NOT NULL,
  received_at      timestamptz NOT NULL,
  handled          text,                                         -- what it did: approved estimate, opted out, ...
  created_at       timestamptz NOT NULL DEFAULT now(),
  UNIQUE (wamid)
);
CREATE INDEX inbound_phone ON notif.inbound_message (phone_e164, received_at DESC);
CREATE INDEX inbound_reply ON notif.inbound_message (reply_to_id) WHERE reply_to_id IS NOT NULL;

-- Where this server is in the main server's event stream.
CREATE TABLE notif.relay_state (
  id              boolean PRIMARY KEY DEFAULT true CHECK (id),
  events_seq      bigint NOT NULL DEFAULT 0,
  last_push_at    timestamptz,
  last_pull_at    timestamptz,
  last_ok_at      timestamptz,
  last_error      text
);
INSERT INTO notif.relay_state (id) VALUES (true);

-- ================================================================ the gateway (main server only)
CREATE TABLE notif.settings (
  business_id       uuid PRIMARY KEY REFERENCES core.business(id),
  enabled           boolean NOT NULL DEFAULT false,
  provider          text NOT NULL DEFAULT 'test' CHECK (provider IN ('meta','test')),
  phone_number_id   text CHECK (phone_number_id IS NULL OR phone_number_id ~ '^[0-9]{5,30}$'),
  waba_id           text CHECK (waba_id IS NULL OR waba_id ~ '^[0-9]{5,30}$'),
  display_phone     text,
  access_token_enc  bytea,
  app_secret_enc    bytea,
  verify_token      text CHECK (verify_token IS NULL OR length(verify_token) BETWEEN 16 AND 100),
  daily_cap         integer NOT NULL DEFAULT 250 CHECK (daily_cap BETWEEN 1 AND 100000),
  stale_hours       integer NOT NULL DEFAULT 24 CHECK (stale_hours BETWEEN 1 AND 168),
  webhook_seen_at   timestamptz,
  updated_at        timestamptz NOT NULL DEFAULT now(),
  updated_by        uuid,
  version           integer NOT NULL DEFAULT 1,
  CHECK (provider <> 'meta' OR NOT enabled OR (phone_number_id IS NOT NULL AND access_token_enc IS NOT NULL AND app_secret_enc IS NOT NULL
                                               AND verify_token IS NOT NULL))
);
CREATE TRIGGER notif_settings_touch BEFORE UPDATE ON notif.settings FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- The message templates (approved in WhatsApp Manager under these names); the body is shown in KITE as a preview.
CREATE TABLE notif.template (
  id            uuid PRIMARY KEY,
  business_id   uuid NOT NULL REFERENCES core.business(id),
  code          text NOT NULL CHECK (code IN ('repair_received','repair_estimate','repair_ready','repair_uncollected')),
  lang          text NOT NULL CHECK (lang IN ('en','ar')),
  meta_name     text NOT NULL CHECK (meta_name ~ '^[a-z0-9_]{1,250}$'),
  meta_lang     text NOT NULL,
  body          text NOT NULL,
  params        text[] NOT NULL,
  buttons       text[] NOT NULL DEFAULT '{}',
  updated_at    timestamptz NOT NULL DEFAULT now(),
  updated_by    uuid,
  version       integer NOT NULL DEFAULT 1,
  UNIQUE (business_id, code, lang)
);
CREATE TRIGGER notif_template_touch BEFORE UPDATE ON notif.template FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE notif.gateway_message (
  id                uuid PRIMARY KEY,                           -- the outbound message's id on the server that queued it
  origin_server_id  uuid NOT NULL,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  branch_id         uuid NOT NULL,
  phone_e164        text NOT NULL,
  lang              text NOT NULL,
  template          text,
  params            jsonb NOT NULL DEFAULT '[]',
  body_text         text,
  opt_in_at         timestamptz,
  expires_at        timestamptz NOT NULL,
  status            text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','sent','delivered','read','failed','dropped','blocked')),
  status_reason     text,
  wamid             text UNIQUE,
  attempts          integer NOT NULL DEFAULT 0,
  next_attempt_at   timestamptz NOT NULL DEFAULT now(),
  received_at       timestamptz NOT NULL DEFAULT now(),
  sent_at           timestamptz,
  status_at         timestamptz,
  CHECK ((template IS NULL) <> (body_text IS NULL))
);
CREATE INDEX gateway_queue ON notif.gateway_message (next_attempt_at) WHERE status = 'queued';
CREATE INDEX gateway_sent_day ON notif.gateway_message (business_id, sent_at) WHERE sent_at IS NOT NULL;
CREATE INDEX gateway_phone ON notif.gateway_message (phone_e164, sent_at DESC);

-- What happened to messages, and what customers sent, for the server it belongs to (ordered by seq).
CREATE TABLE notif.gateway_event (
  seq               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  origin_server_id  uuid NOT NULL,
  message_id        uuid,                                       -- the message it is about, or the one a reply answers
  kind              text NOT NULL CHECK (kind IN ('status','text','button','stop','start')),
  status            text,
  reason            text,
  phone_e164        text,
  body              text,
  wamid             text,
  at                timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX gateway_event_server ON notif.gateway_event (origin_server_id, seq);

-- Webhook deliveries already handled (Meta retries; each status and message is handled once).
CREATE TABLE notif.webhook_seen (
  key          text PRIMARY KEY,
  at           timestamptz NOT NULL DEFAULT now()
);

-- Numbers that sent STOP: nothing is sent to them until a newer consent arrives with a message.
CREATE TABLE notif.suppression (
  phone_e164   text PRIMARY KEY,
  since        timestamptz NOT NULL DEFAULT now(),
  source       text NOT NULL
);

-- The test chat (provider "test"): what a phone would have shown, for demos and tests.
CREATE TABLE notif.test_chat (
  id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  phone_e164   text NOT NULL,
  direction    text NOT NULL CHECK (direction IN ('out','in')),
  wamid        text NOT NULL,
  body         text NOT NULL,
  buttons      text[] NOT NULL DEFAULT '{}',
  at           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX test_chat_phone ON notif.test_chat (phone_e164, id);

-- ================================================================ the internet address (Nextdab "web" command)
-- The main server's https://<name>.nextdab.com, made by Nextdab Admin; through it only the WhatsApp webhook is reachable.
-- The tunnel token is kept encrypted, only here.
CREATE TABLE lic.web_state (
  id              boolean PRIMARY KEY DEFAULT true CHECK (id),
  host            text,
  token_enc       bytea,
  port            integer CHECK (port IS NULL OR port BETWEEN 1 AND 65535),
  tool_url        text,
  tool_sha256     text CHECK (tool_sha256 IS NULL OR tool_sha256 ~ '^[0-9a-f]{64}$'),
  tool_size       bigint,
  active          boolean NOT NULL DEFAULT false,
  off_reason      text,
  command_id      text,
  received_at     timestamptz,
  updated_at      timestamptz NOT NULL DEFAULT now(),
  CHECK (NOT active OR (host IS NOT NULL AND token_enc IS NOT NULL AND tool_url IS NOT NULL AND tool_sha256 IS NOT NULL))
);

-- ================================================================ checks for the books
-- Jobs whose deposit receipts and the deposit ledger disagree (there should be none).
CREATE FUNCTION repair.deposit_mismatches()
RETURNS TABLE (job_id uuid, ledger numeric, documents numeric)
LANGUAGE sql STABLE AS $$
  SELECT j.id, repair.deposit_balance(j.id),
         coalesce((SELECT sum(CASE WHEN d.kind = 'deposit' THEN d.amount ELSE -d.amount END) FROM repair.deposit d WHERE d.job_id = j.id), 0)
    FROM repair.repair_job j
   WHERE repair.deposit_balance(j.id) <> coalesce((SELECT sum(CASE WHEN d.kind = 'deposit' THEN d.amount ELSE -d.amount END) FROM repair.deposit d WHERE d.job_id = j.id), 0)
$$;

-- ================================================================ grants, row-level security
REVOKE DELETE, TRUNCATE ON repair.repair_job, repair.estimate, repair.repair_line, repair.wip_clearing, repair.photo, repair.deposit,
                          repair.status_history, repair.assignment, repair.credential_view, notif.consent, device.customer_device,
                          repair.category, repair.fault_code, repair.settings FROM kite_app;
REVOKE UPDATE ON repair.repair_line, repair.wip_clearing, repair.photo, repair.deposit, repair.status_history, repair.assignment,
                 repair.credential_view, notif.consent, repair.transition FROM kite_app;
GRANT SELECT, INSERT, UPDATE ON device.customer_device, lic.web_state TO kite_app;
GRANT SELECT ON device.customer_device, lic.web_state TO kite_ro;
REVOKE EXECUTE ON FUNCTION repair.job_history(), repair.job_assignment() FROM PUBLIC;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['repair.repair_job','repair.estimate','repair.repair_line','repair.wip_clearing','repair.photo','repair.deposit',
                           'repair.status_history','repair.assignment','repair.credential_view','notif.consent','notif.outbound_message',
                           'notif.inbound_message'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY branch_read ON %s FOR SELECT USING (core.branch_visible(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_insert ON %s FOR INSERT WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_update ON %s FOR UPDATE USING (core.branch_visible(branch_id)) WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_delete ON %s FOR DELETE USING (core.branch_writable(branch_id))', t);
  END LOOP;
END
$$;
-- Estimate lines follow their estimate.
ALTER TABLE repair.estimate_line ENABLE ROW LEVEL SECURITY;
CREATE POLICY estimate_line_read ON repair.estimate_line FOR SELECT USING (EXISTS (SELECT 1 FROM repair.estimate e WHERE e.id = estimate_id));
CREATE POLICY estimate_line_write ON repair.estimate_line FOR ALL USING (EXISTS (SELECT 1 FROM repair.estimate e WHERE e.id = estimate_id))
  WITH CHECK (EXISTS (SELECT 1 FROM repair.estimate e WHERE e.id = estimate_id AND core.branch_writable(e.branch_id)));

-- ================================================================ number series and permissions
-- Deposit receipt numbers (DEP-B01-2026-000001) for every branch and fiscal year that already has series; job numbers
-- (R-) were created with the business.
INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year)
SELECT gen_random_uuid(), s.business_id, s.branch_id, d.doc_type, d.prefix || '-' || br.code || '-' || s.fiscal_year || '-', s.fiscal_year
  FROM (SELECT DISTINCT business_id, branch_id, fiscal_year FROM core.number_series WHERE branch_id IS NOT NULL AND terminal_id IS NULL) s
  JOIN core.branch br ON br.id = s.branch_id
 CROSS JOIN (VALUES ('repair_job','R'), ('repair_deposit','DEP')) AS d(doc_type, prefix)
ON CONFLICT DO NOTHING;

INSERT INTO iam.permission (code, module_code, description_en, description_ar) VALUES
  ('REPAIR.VIEW',     'repair', 'View repair jobs, the workshop board and repair reports',                         'عرض أوامر الصيانة ولوحة الورشة وتقارير الصيانة'),
  ('REPAIR.INTAKE',   'repair', 'Receive devices for repair, take deposits, record approvals and deliver repairs',  'استلام الأجهزة للصيانة وأخذ العربون وتسجيل الموافقات وتسليم الأجهزة'),
  ('REPAIR.WORK',     'repair', 'Diagnose and repair: statuses, estimates, parts and labour',                       'التشخيص والإصلاح: الحالات والتقديرات والقطع والعمالة'),
  ('REPAIR.ASSIGN',   'repair', 'Assign and reassign technicians to jobs',                                          'تعيين الفنيين على أوامر الصيانة وإعادة تعيينهم'),
  ('REPAIR.CANCEL',   'repair', 'Cancel jobs, write off parts and refund deposits',                                 'إلغاء أوامر الصيانة وشطب القطع ورد العربون'),
  ('REPAIR.SETTINGS', 'repair', 'Manage repair categories, fault codes, terms and repair settings',                  'إدارة فئات الصيانة ورموز الأعطال والشروط وإعدادات الصيانة'),
  ('NOTIF.SETTINGS',  'notif',  'Manage the WhatsApp connection, templates and limits',                             'إدارة ربط واتساب والقوالب والحدود'),
  ('NOTIF.REPLY',     'notif',  'Read customers'' WhatsApp replies and answer them within 24 hours',                'قراءة ردود العملاء على واتساب والرد عليها خلال 24 ساعة')
ON CONFLICT DO NOTHING;

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (SELECT code FROM iam.permission WHERE code LIKE 'REPAIR.%' OR code LIKE 'NOTIF.%') p
 WHERE r.code IN ('owner','branch_manager')
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('REPAIR.VIEW'),('REPAIR.INTAKE'),('NOTIF.REPLY')) AS p(code)
 WHERE r.code IN ('cashier','senior_cashier')
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('REPAIR.VIEW'),('REPAIR.WORK')) AS p(code)
 WHERE r.code = 'technician'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, 'REPAIR.VIEW' FROM iam.role r WHERE r.code = 'accountant'
ON CONFLICT DO NOTHING;
