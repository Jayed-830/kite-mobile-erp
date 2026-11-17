-- V0020: purchasing and accounting (blueprint 16.1-16.3, 17). Purchase orders, goods receipts, supplier invoices matched three
-- ways, supplier returns and payments; goods received not invoiced as its own ledger; periods closed branch by branch; manual
-- journals with a second approver; expense vouchers and card settlements; the VAT return from configurable boxes; the daily check
-- of every subledger against its control account.

-- ================================================================ chart of accounts (17.2)
ALTER TABLE acct.account ADD COLUMN cash_flow_class text NOT NULL DEFAULT 'none'
  CHECK (cash_flow_class IN ('cash','operating','investing','financing','none'));
UPDATE acct.account SET cash_flow_class = CASE
    WHEN code IN ('1100','1150','1200') THEN 'cash'
    WHEN NOT is_postable THEN 'none'
    WHEN type IN ('asset','liability') THEN 'operating'
    WHEN type = 'equity' THEN 'financing'
    ELSE 'none' END;

INSERT INTO acct.account (id, business_id, code, name_en, name_ar, type, parent_id, is_postable, is_control, cash_flow_class)
SELECT gen_random_uuid(), b.id, a.code, a.name_en, a.name_ar, a.type, p.id, true, a.is_control, a.cff
  FROM core.business b
 CROSS JOIN (VALUES ('1600', 'Fixed assets', 'الأصول الثابتة', 'asset', '1000', false, 'investing'),
                    ('5300', 'Purchase price variance', 'فروقات أسعار الشراء', 'expense', '5000', false, 'none')) AS a(code, name_en, name_ar, type, parent, is_control, cff)
  LEFT JOIN acct.account p ON p.business_id = b.id AND p.code = a.parent
ON CONFLICT (business_id, code) DO NOTHING;

-- ================================================================ periods closed branch by branch (17.5)
-- The business-wide period (branch_id NULL) is the period journals reference. A branch's own row, created when the branch's
-- period is first closed, holds that branch's status; the stricter of the two applies. Period 13 is the year-end closing period.
CREATE FUNCTION acct.period_status(p_period uuid, p_branch uuid) RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
  SELECT CASE WHEN 'closed' IN (p.status, coalesce(b.status, 'open')) THEN 'closed'
              WHEN 'soft_closed' IN (p.status, coalesce(b.status, 'open')) THEN 'soft_closed'
              ELSE 'open' END
    FROM acct.fiscal_period p
    LEFT JOIN acct.fiscal_period b ON p_branch IS NOT NULL AND b.business_id = p.business_id AND b.branch_id = p_branch
                                  AND b.fiscal_year = p.fiscal_year AND b.period_no = p.period_no
   WHERE p.id = p_period
$$;

CREATE OR REPLACE FUNCTION acct.check_period() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE p acct.fiscal_period%ROWTYPE; st text;
BEGIN
  SELECT * INTO p FROM acct.fiscal_period WHERE id = NEW.fiscal_period_id;
  IF p.branch_id IS NOT NULL THEN
    RAISE EXCEPTION 'KITE: a journal references the business-wide period, not a branch''s' USING ERRCODE = 'KT003';
  END IF;
  IF NEW.journal_date < p.starts_on OR NEW.journal_date > p.ends_on THEN
    RAISE EXCEPTION 'KITE: journal date % is outside fiscal period %-%', NEW.journal_date, p.fiscal_year, p.period_no
      USING ERRCODE = 'KT003';
  END IF;
  st := acct.period_status(p.id, NEW.branch_id);
  IF st = 'closed' OR (st = 'soft_closed' AND coalesce(current_setting('kite.allow_soft_closed', true), '') <> 'on') THEN
    RAISE EXCEPTION 'KITE: fiscal period %-% is % at this branch', p.fiscal_year, p.period_no, replace(st, '_', ' ')
      USING ERRCODE = 'KT003';
  END IF;
  RETURN NEW;
END
$$;

-- Every line's branch is checked too: one journal can have lines at several branches (interbranch settlements).
CREATE FUNCTION acct.check_line_period() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE pid uuid; st text; y integer; n smallint;
BEGIN
  IF NEW.branch_id IS NULL THEN RETURN NEW; END IF;
  SELECT j.fiscal_period_id, p.fiscal_year, p.period_no INTO pid, y, n
    FROM acct.journal j JOIN acct.fiscal_period p ON p.id = j.fiscal_period_id WHERE j.id = NEW.journal_id;
  st := acct.period_status(pid, NEW.branch_id);
  IF st = 'closed' OR (st = 'soft_closed' AND coalesce(current_setting('kite.allow_soft_closed', true), '') <> 'on') THEN
    RAISE EXCEPTION 'KITE: fiscal period %-% is % at the branch of line %', y, n, replace(st, '_', ' '), NEW.line_no
      USING ERRCODE = 'KT003';
  END IF;
  RETURN NEW;
END
$$;
CREATE TRIGGER journal_line_period_check BEFORE INSERT ON acct.journal_line FOR EACH ROW EXECUTE FUNCTION acct.check_line_period();

-- A period's status only moves through the steps (the application records who and why in the audit chain).
CREATE FUNCTION acct.fiscal_period_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.business_id <> NEW.business_id OR OLD.branch_id IS DISTINCT FROM NEW.branch_id OR OLD.fiscal_year <> NEW.fiscal_year
     OR OLD.period_no <> NEW.period_no OR OLD.starts_on <> NEW.starts_on OR OLD.ends_on <> NEW.ends_on THEN
    RAISE EXCEPTION 'KITE: a fiscal period''s dates and place do not change' USING ERRCODE = 'KT003';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER fiscal_period_guard BEFORE UPDATE ON acct.fiscal_period FOR EACH ROW EXECUTE FUNCTION acct.fiscal_period_guard();

-- ================================================================ accounting policy, manual journals (17.6)
CREATE TABLE acct.policy (
  business_id                    uuid PRIMARY KEY REFERENCES core.business(id),
  manual_journal_approval_above  numeric(18,3) NOT NULL DEFAULT 1000 CHECK (manual_journal_approval_above >= 0),
  vat_period_months              smallint NOT NULL DEFAULT 3 CHECK (vat_period_months IN (1, 3)),
  vat_first_month                smallint NOT NULL DEFAULT 1 CHECK (vat_first_month BETWEEN 1 AND 12),
  version                        integer NOT NULL DEFAULT 1,
  updated_at                     timestamptz NOT NULL DEFAULT now(),
  updated_by                     uuid
);
CREATE TRIGGER acct_policy_touch BEFORE UPDATE ON acct.policy FOR EACH ROW EXECUTE FUNCTION core.touch_row();
INSERT INTO acct.policy (business_id) SELECT id FROM core.business ON CONFLICT DO NOTHING;

CREATE TABLE acct.manual_journal (
  id                 uuid PRIMARY KEY,
  business_id        uuid NOT NULL REFERENCES core.business(id),
  branch_id          uuid NOT NULL REFERENCES core.branch(id),
  number             text NOT NULL,
  journal_date       date NOT NULL,
  description        text NOT NULL CHECK (length(trim(description)) > 0),
  status             text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','pending_approval','posted','rejected','cancelled')),
  total              numeric(18,3) NOT NULL DEFAULT 0 CHECK (total >= 0),
  into_soft_closed   boolean NOT NULL DEFAULT false,
  created_by         uuid NOT NULL,
  submitted_at       timestamptz,
  approved_by        uuid,
  approved_at        timestamptz,
  rejected_reason    text,
  journal_id         uuid REFERENCES acct.journal(id),
  reversal_journal_id uuid REFERENCES acct.journal(id),
  reversed_by        uuid,
  reversed_reason    text,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  updated_by         uuid,
  version            integer NOT NULL DEFAULT 1,
  origin_server_id   uuid,
  UNIQUE (business_id, number),
  CHECK (approved_by IS NULL OR approved_by <> created_by),
  CHECK ((status = 'posted') = (journal_id IS NOT NULL)),
  CHECK (status <> 'rejected' OR length(trim(coalesce(rejected_reason, ''))) > 0),
  CHECK ((reversal_journal_id IS NULL) OR (status = 'posted' AND length(trim(coalesce(reversed_reason, ''))) > 0))
);
CREATE TRIGGER manual_journal_touch BEFORE UPDATE ON acct.manual_journal FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE acct.manual_journal_line (
  id                 uuid PRIMARY KEY,
  manual_journal_id  uuid NOT NULL REFERENCES acct.manual_journal(id),
  line_no            smallint NOT NULL CHECK (line_no > 0),
  account_id         uuid NOT NULL REFERENCES acct.account(id),
  branch_id          uuid NOT NULL REFERENCES core.branch(id),
  debit              numeric(18,3) NOT NULL DEFAULT 0 CHECK (debit >= 0),
  credit             numeric(18,3) NOT NULL DEFAULT 0 CHECK (credit >= 0),
  memo               text,
  CHECK (debit = 0 OR credit = 0),
  CHECK (debit > 0 OR credit > 0),
  UNIQUE (manual_journal_id, line_no)
);
-- Lines change only while their journal is a draft.
CREATE FUNCTION acct.manual_line_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE st text;
BEGIN
  SELECT status INTO st FROM acct.manual_journal WHERE id = coalesce(NEW.manual_journal_id, OLD.manual_journal_id);
  IF st <> 'draft' THEN
    RAISE EXCEPTION 'KITE: a manual journal''s lines change only while it is a draft' USING ERRCODE = 'KT080';
  END IF;
  RETURN coalesce(NEW, OLD);
END $$;
CREATE TRIGGER manual_journal_line_guard BEFORE INSERT OR UPDATE OR DELETE ON acct.manual_journal_line
  FOR EACH ROW EXECUTE FUNCTION acct.manual_line_guard();

-- ================================================================ purchasing policy (16.1)
CREATE TABLE purch.policy (
  business_id             uuid PRIMARY KEY REFERENCES core.business(id),
  price_tolerance_pct     numeric(6,3) NOT NULL DEFAULT 2 CHECK (price_tolerance_pct BETWEEN 0 AND 100),
  price_tolerance_amount  numeric(18,3) NOT NULL DEFAULT 1 CHECK (price_tolerance_amount >= 0),
  over_receipt_pct        numeric(6,3) NOT NULL DEFAULT 0 CHECK (over_receipt_pct BETWEEN 0 AND 100),
  supplier_list           text NOT NULL DEFAULT 'all' CHECK (supplier_list IN ('all','approved')),
  version                 integer NOT NULL DEFAULT 1,
  updated_at              timestamptz NOT NULL DEFAULT now(),
  updated_by              uuid
);
CREATE TRIGGER purch_policy_touch BEFORE UPDATE ON purch.policy FOR EACH ROW EXECUTE FUNCTION core.touch_row();
INSERT INTO purch.policy (business_id) SELECT id FROM core.business ON CONFLICT DO NOTHING;

-- How much a role may approve on one order (NULL = no limit), for one branch or every branch.
CREATE TABLE purch.approval_limit (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  role_id      uuid NOT NULL REFERENCES iam.role(id),
  branch_id    uuid REFERENCES core.branch(id),
  max_amount   numeric(18,3) CHECK (max_amount IS NULL OR max_amount >= 0),
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid,
  CONSTRAINT approval_limit_scope UNIQUE NULLS NOT DISTINCT (business_id, role_id, branch_id)
);
INSERT INTO purch.approval_limit (id, business_id, role_id, branch_id, max_amount)
SELECT gen_random_uuid(), r.business_id, r.id, NULL, l.max_amount
  FROM iam.role r JOIN (VALUES ('owner', NULL::numeric), ('accountant', 5000), ('purchasing_officer', 2000), ('branch_manager', 500)) AS l(code, max_amount)
    ON l.code = r.code
ON CONFLICT DO NOTHING;

-- The approved supplier list of a branch (used when the policy says 'approved').
CREATE TABLE purch.branch_supplier (
  business_id  uuid NOT NULL REFERENCES core.business(id),
  branch_id    uuid NOT NULL REFERENCES core.branch(id),
  supplier_id  uuid NOT NULL REFERENCES purch.supplier(id),
  added_by     uuid,
  added_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (branch_id, supplier_id)
);

-- ================================================================ purchase orders (16.2)
CREATE TABLE purch.purchase_order (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),      -- the branch that orders
  number           text NOT NULL,
  supplier_id      uuid NOT NULL REFERENCES purch.supplier(id),
  status           text NOT NULL DEFAULT 'draft'
                     CHECK (status IN ('draft','approved','sent','partly_received','received','closed','cancelled')),
  order_date       date NOT NULL,
  expected_date    date,
  net_total        numeric(18,3) NOT NULL DEFAULT 0 CHECK (net_total >= 0),
  tax_total        numeric(18,3) NOT NULL DEFAULT 0 CHECK (tax_total >= 0),
  gross_total      numeric(18,3) NOT NULL DEFAULT 0 CHECK (gross_total >= 0),
  note             text,
  created_by       uuid NOT NULL,
  approved_by      uuid,
  approved_at      timestamptz,
  sent_at          timestamptz,
  sent_by          uuid,
  closed_at        timestamptz,
  closed_by        uuid,
  close_reason     text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  updated_by       uuid,
  version          integer NOT NULL DEFAULT 1,
  origin_server_id uuid,
  UNIQUE (business_id, number),
  CHECK (gross_total = net_total + tax_total),
  CHECK (status IN ('draft','cancelled') OR approved_by IS NOT NULL),
  CHECK (status <> 'draft' OR approved_by IS NULL),
  CHECK (status NOT IN ('closed','cancelled') OR closed_at IS NOT NULL)
);
CREATE INDEX purchase_order_supplier ON purch.purchase_order (supplier_id, order_date);
CREATE TRIGGER purchase_order_touch BEFORE UPDATE ON purch.purchase_order FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE FUNCTION purch.po_status_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = NEW.status THEN
    IF OLD.status <> 'draft' AND (OLD.supplier_id <> NEW.supplier_id OR OLD.net_total <> NEW.net_total OR OLD.tax_total <> NEW.tax_total) THEN
      RAISE EXCEPTION 'KITE: an approved order is not changed; cancel it or order again' USING ERRCODE = 'KT081';
    END IF;
    RETURN NEW;
  END IF;
  IF NOT (OLD.status, NEW.status) IN (('draft','approved'), ('draft','cancelled'), ('approved','sent'), ('approved','cancelled'), ('sent','cancelled'),
       ('approved','partly_received'), ('approved','received'), ('sent','partly_received'), ('sent','received'), ('partly_received','received'),
       ('partly_received','closed'), ('received','closed'), ('approved','closed'), ('sent','closed')) THEN
    RAISE EXCEPTION 'KITE: an order cannot go from % to %', replace(OLD.status, '_', ' '), replace(NEW.status, '_', ' ') USING ERRCODE = 'KT081';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER purchase_order_status BEFORE UPDATE ON purch.purchase_order FOR EACH ROW EXECUTE FUNCTION purch.po_status_guard();

CREATE TABLE purch.po_line (
  id                 uuid PRIMARY KEY,
  po_id              uuid NOT NULL REFERENCES purch.purchase_order(id),
  line_no            smallint NOT NULL CHECK (line_no > 0),
  variant_id         uuid NOT NULL REFERENCES catalog.variant(id),
  description_en     text NOT NULL,
  deliver_branch_id  uuid NOT NULL REFERENCES core.branch(id),
  qty                numeric(14,3) NOT NULL CHECK (qty > 0),
  unit_price         numeric(18,3) NOT NULL CHECK (unit_price >= 0),     -- before VAT
  tax_code_id        uuid NOT NULL REFERENCES tax.tax_code(id),
  tax_rate           numeric(7,4) NOT NULL CHECK (tax_rate >= 0),
  net_amount         numeric(18,3) NOT NULL CHECK (net_amount >= 0),
  tax_amount         numeric(18,3) NOT NULL CHECK (tax_amount >= 0),
  repair_job_id      uuid REFERENCES repair.repair_job(id),
  UNIQUE (po_id, line_no)
);
CREATE INDEX po_line_branch ON purch.po_line (deliver_branch_id);
CREATE INDEX po_line_repair ON purch.po_line (repair_job_id) WHERE repair_job_id IS NOT NULL;
CREATE FUNCTION purch.po_line_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE st text;
BEGIN
  SELECT status INTO st FROM purch.purchase_order WHERE id = coalesce(NEW.po_id, OLD.po_id);
  IF st <> 'draft' THEN
    RAISE EXCEPTION 'KITE: an order''s lines change only while it is a draft' USING ERRCODE = 'KT081';
  END IF;
  RETURN coalesce(NEW, OLD);
END $$;
CREATE TRIGGER po_line_guard BEFORE INSERT OR UPDATE OR DELETE ON purch.po_line FOR EACH ROW EXECUTE FUNCTION purch.po_line_guard();

-- ================================================================ goods receipts (16.2)
CREATE TABLE purch.goods_receipt (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  warehouse_id     uuid NOT NULL REFERENCES core.warehouse(id),
  number           text NOT NULL,
  supplier_id      uuid NOT NULL REFERENCES purch.supplier(id),
  po_id            uuid REFERENCES purch.purchase_order(id),
  receipt_date     date NOT NULL,
  delivery_note    text,
  note             text,
  goods_value      numeric(18,3) NOT NULL CHECK (goods_value >= 0),
  charges_value    numeric(18,3) NOT NULL DEFAULT 0 CHECK (charges_value >= 0),
  journal_id       uuid REFERENCES acct.journal(id),
  created_by       uuid NOT NULL,
  created_at       timestamptz NOT NULL DEFAULT now(),
  origin_server_id uuid,
  UNIQUE (business_id, number)
);
CREATE INDEX goods_receipt_po ON purch.goods_receipt (po_id) WHERE po_id IS NOT NULL;
CREATE INDEX goods_receipt_supplier ON purch.goods_receipt (supplier_id, receipt_date);

CREATE TABLE purch.receipt_line (
  id              uuid PRIMARY KEY,
  receipt_id      uuid NOT NULL REFERENCES purch.goods_receipt(id),
  line_no         smallint NOT NULL CHECK (line_no > 0),
  po_line_id      uuid REFERENCES purch.po_line(id),
  variant_id      uuid NOT NULL REFERENCES catalog.variant(id),
  product_type    text NOT NULL,
  qty             numeric(14,3) NOT NULL CHECK (qty > 0),
  unit_price      numeric(18,3) NOT NULL CHECK (unit_price >= 0),          -- before VAT, as ordered (or agreed)
  tax_code_id     uuid NOT NULL REFERENCES tax.tax_code(id),
  goods_value     numeric(18,3) NOT NULL CHECK (goods_value >= 0),         -- qty x price: what goods received not invoiced holds
  charge_share    numeric(18,3) NOT NULL DEFAULT 0 CHECK (charge_share >= 0),
  stock_value     numeric(18,3) NOT NULL CHECK (stock_value >= 0),         -- goods value + landed cost: what the stock holds
  repair_job_id   uuid REFERENCES repair.repair_job(id),
  UNIQUE (receipt_id, line_no),
  CHECK (stock_value = goods_value + charge_share)
);
CREATE INDEX receipt_line_po_line ON purch.receipt_line (po_line_id) WHERE po_line_id IS NOT NULL;
CREATE INDEX receipt_line_variant ON purch.receipt_line (variant_id);

-- The phones received on a line, each with its own cost (specific identification, 16.3).
CREATE TABLE purch.receipt_unit (
  device_unit_id   uuid PRIMARY KEY REFERENCES device.device_unit(id),
  receipt_line_id  uuid NOT NULL REFERENCES purch.receipt_line(id),
  unit_cost        numeric(18,3) NOT NULL CHECK (unit_cost >= 0)
);
CREATE INDEX receipt_unit_line ON purch.receipt_unit (receipt_line_id);

-- Freight, customs and other landed costs, spread over the lines by value or quantity; each owed to the supplier who will invoice it.
CREATE TABLE purch.receipt_charge (
  id            uuid PRIMARY KEY,
  receipt_id    uuid NOT NULL REFERENCES purch.goods_receipt(id),
  line_no       smallint NOT NULL CHECK (line_no > 0),
  kind          text NOT NULL CHECK (kind IN ('freight','customs','insurance','other')),
  description   text NOT NULL,
  supplier_id   uuid NOT NULL REFERENCES purch.supplier(id),
  amount        numeric(18,3) NOT NULL CHECK (amount > 0),
  basis         text NOT NULL CHECK (basis IN ('value','qty')),
  UNIQUE (receipt_id, line_no)
);

-- ================================================================ goods received not invoiced (2110) as a ledger
-- Every amount that sits in 2110 and why: receipts and landed costs (+), outside work for refurbishment and repairs (+, its
-- reversal -), cleared by supplier invoices (-) and by returns before the invoice (-). The daily check compares it with 2110.
CREATE TABLE purch.grni_entry (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  supplier_id      uuid REFERENCES purch.supplier(id),
  source_type      text NOT NULL CHECK (source_type IN ('receipt_line','receipt_charge','refurb_cost','repair_line')),
  source_id        uuid NOT NULL,
  kind             text NOT NULL CHECK (kind IN ('accrued','reversed','invoiced','returned')),
  amount           numeric(18,3) NOT NULL CHECK (amount <> 0),
  doc_type         text NOT NULL,
  doc_id           uuid NOT NULL,
  vendor           text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  origin_server_id uuid,
  CHECK ((kind = 'accrued') = (amount > 0))
);
CREATE INDEX grni_entry_source ON purch.grni_entry (source_type, source_id);
CREATE INDEX grni_entry_branch ON purch.grni_entry (branch_id);
CREATE TRIGGER grni_entry_immutable BEFORE UPDATE OR DELETE ON purch.grni_entry FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
CREATE TRIGGER grni_entry_no_truncate BEFORE TRUNCATE ON purch.grni_entry FOR EACH STATEMENT EXECUTE FUNCTION core.forbid_change();

-- What is still open on a source: never below zero (a source is cleared at most once in full).
CREATE FUNCTION purch.grni_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE open numeric;
BEGIN
  SELECT coalesce(sum(amount), 0) INTO open FROM purch.grni_entry WHERE source_type = NEW.source_type AND source_id = NEW.source_id;
  IF open < 0 THEN
    RAISE EXCEPTION 'KITE: more is cleared from % % than was received', replace(NEW.source_type, '_', ' '), NEW.source_id USING ERRCODE = 'KT082';
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER grni_entry_not_negative AFTER INSERT ON purch.grni_entry
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION purch.grni_check();

-- Outside work recorded for a refurbishment or a repair accrues to 2110 until its supplier's invoice clears it.
CREATE FUNCTION purch.grni_from_outside_work() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
  IF TG_TABLE_NAME = 'refurb_cost' THEN
    IF NEW.kind <> 'external' THEN RETURN NULL; END IF;
    INSERT INTO purch.grni_entry (id, business_id, branch_id, source_type, source_id, kind, amount, doc_type, doc_id, vendor, created_at, origin_server_id)
    VALUES (gen_random_uuid(), NEW.business_id, NEW.branch_id, 'refurb_cost', coalesce(NEW.reversal_of_id, NEW.id),
            CASE WHEN NEW.amount > 0 THEN 'accrued' ELSE 'reversed' END, NEW.amount, 'refurb_cost', NEW.id, NEW.vendor, NEW.created_at, NEW.origin_server_id);
  ELSE
    IF NEW.kind <> 'external' THEN RETURN NULL; END IF;
    INSERT INTO purch.grni_entry (id, business_id, branch_id, source_type, source_id, kind, amount, doc_type, doc_id, vendor, created_at, origin_server_id)
    VALUES (gen_random_uuid(), NEW.business_id, NEW.branch_id, 'repair_line', coalesce(NEW.return_of_id, NEW.id),
            CASE WHEN NEW.cost > 0 THEN 'accrued' ELSE 'reversed' END, NEW.cost, 'repair_line', NEW.id, NEW.vendor, NEW.created_at, NEW.origin_server_id);
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER refurb_cost_grni AFTER INSERT ON refurb.refurb_cost FOR EACH ROW EXECUTE FUNCTION purch.grni_from_outside_work();
CREATE TRIGGER repair_line_grni AFTER INSERT ON repair.repair_line FOR EACH ROW EXECUTE FUNCTION purch.grni_from_outside_work();

INSERT INTO purch.grni_entry (id, business_id, branch_id, source_type, source_id, kind, amount, doc_type, doc_id, vendor, created_at, origin_server_id)
SELECT gen_random_uuid(), business_id, branch_id, 'refurb_cost', coalesce(reversal_of_id, id), CASE WHEN amount > 0 THEN 'accrued' ELSE 'reversed' END,
       amount, 'refurb_cost', id, vendor, created_at, origin_server_id
  FROM refurb.refurb_cost WHERE kind = 'external';
INSERT INTO purch.grni_entry (id, business_id, branch_id, source_type, source_id, kind, amount, doc_type, doc_id, vendor, created_at, origin_server_id)
SELECT gen_random_uuid(), business_id, branch_id, 'repair_line', coalesce(return_of_id, id), CASE WHEN cost > 0 THEN 'accrued' ELSE 'reversed' END,
       cost, 'repair_line', id, vendor, created_at, origin_server_id
  FROM repair.repair_line WHERE kind = 'external';

-- ================================================================ supplier invoices, matched three ways (16.2)
CREATE TABLE purch.purchase_invoice (
  id                   uuid PRIMARY KEY,
  business_id          uuid NOT NULL REFERENCES core.business(id),
  branch_id            uuid NOT NULL REFERENCES core.branch(id),     -- where it is booked and owed
  number               text NOT NULL,
  supplier_id          uuid NOT NULL REFERENCES purch.supplier(id),
  supplier_invoice_no  text NOT NULL CHECK (length(trim(supplier_invoice_no)) > 0),
  invoice_date         date NOT NULL,
  due_date             date NOT NULL,
  status               text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','matched','needs_approval','posted','cancelled')),
  net_total            numeric(18,3) NOT NULL DEFAULT 0 CHECK (net_total >= 0),
  tax_total            numeric(18,3) NOT NULL DEFAULT 0 CHECK (tax_total >= 0),
  gross_total          numeric(18,3) NOT NULL DEFAULT 0 CHECK (gross_total >= 0),
  variance_total       numeric(18,3) NOT NULL DEFAULT 0,
  note                 text,
  override_by          uuid,
  override_reason      text,
  journal_id           uuid REFERENCES acct.journal(id),
  ledger_entry_id      uuid REFERENCES purch.supplier_ledger(id),
  created_by           uuid NOT NULL,
  created_at           timestamptz NOT NULL DEFAULT now(),
  posted_at            timestamptz,
  posted_by            uuid,
  updated_at           timestamptz NOT NULL DEFAULT now(),
  updated_by           uuid,
  version              integer NOT NULL DEFAULT 1,
  origin_server_id     uuid,
  UNIQUE (business_id, number),
  CHECK (gross_total = net_total + tax_total),
  CHECK (due_date >= invoice_date),
  CHECK ((status = 'posted') = (journal_id IS NOT NULL AND posted_at IS NOT NULL)),
  CHECK (override_by IS NULL OR length(trim(coalesce(override_reason, ''))) > 0)
);
-- A supplier's invoice number is booked once.
CREATE UNIQUE INDEX purchase_invoice_supplier_no ON purch.purchase_invoice (supplier_id, lower(trim(supplier_invoice_no))) WHERE status <> 'cancelled';
CREATE INDEX purchase_invoice_supplier ON purch.purchase_invoice (supplier_id, invoice_date);
CREATE TRIGGER purchase_invoice_touch BEFORE UPDATE ON purch.purchase_invoice FOR EACH ROW EXECUTE FUNCTION core.touch_row();
CREATE FUNCTION purch.invoice_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status IN ('posted','cancelled') THEN
    RAISE EXCEPTION 'KITE: a % invoice is not changed', OLD.status USING ERRCODE = 'KT083';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER purchase_invoice_guard BEFORE UPDATE ON purch.purchase_invoice FOR EACH ROW EXECUTE FUNCTION purch.invoice_guard();

CREATE TABLE purch.invoice_line (
  id                  uuid PRIMARY KEY,
  invoice_id          uuid NOT NULL REFERENCES purch.purchase_invoice(id),
  line_no             smallint NOT NULL CHECK (line_no > 0),
  kind                text NOT NULL CHECK (kind IN ('receipt','charge','outside_work','expense')),
  receipt_line_id     uuid REFERENCES purch.receipt_line(id),
  receipt_charge_id   uuid REFERENCES purch.receipt_charge(id),
  outside_type        text CHECK (outside_type IN ('refurb_cost','repair_line')),
  outside_id          uuid,
  account_id          uuid REFERENCES acct.account(id),
  expense_branch_id   uuid REFERENCES core.branch(id),
  description         text NOT NULL,
  qty                 numeric(14,3) NOT NULL CHECK (qty > 0),
  unit_price          numeric(18,3) NOT NULL CHECK (unit_price >= 0),     -- before VAT
  net_amount          numeric(18,3) NOT NULL CHECK (net_amount >= 0),
  tax_code_id         uuid NOT NULL REFERENCES tax.tax_code(id),
  tax_rate            numeric(7,4) NOT NULL CHECK (tax_rate >= 0),
  tax_amount          numeric(18,3) NOT NULL CHECK (tax_amount >= 0),
  expected_amount     numeric(18,3) NOT NULL DEFAULT 0 CHECK (expected_amount >= 0),   -- what it clears from 2110
  order_price         numeric(18,3),                                                     -- the order's price, for the three-way match
  match_status        text NOT NULL CHECK (match_status IN ('ok','price','qty','none')),
  variance            numeric(18,3) NOT NULL DEFAULT 0,
  variance_inventory  numeric(18,3) NOT NULL DEFAULT 0,
  variance_ppv        numeric(18,3) NOT NULL DEFAULT 0,
  UNIQUE (invoice_id, line_no),
  CHECK ((kind = 'receipt') = (receipt_line_id IS NOT NULL)),
  CHECK ((kind = 'charge') = (receipt_charge_id IS NOT NULL)),
  CHECK ((kind = 'outside_work') = (outside_id IS NOT NULL AND outside_type IS NOT NULL)),
  CHECK ((kind = 'expense') = (account_id IS NOT NULL AND expense_branch_id IS NOT NULL)),
  CHECK (kind = 'expense' OR match_status <> 'none'),
  CHECK (variance = variance_inventory + variance_ppv)
);
CREATE INDEX invoice_line_receipt ON purch.invoice_line (receipt_line_id) WHERE receipt_line_id IS NOT NULL;
CREATE INDEX invoice_line_charge ON purch.invoice_line (receipt_charge_id) WHERE receipt_charge_id IS NOT NULL;
CREATE FUNCTION purch.invoice_line_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE st text;
BEGIN
  SELECT status INTO st FROM purch.purchase_invoice WHERE id = coalesce(NEW.invoice_id, OLD.invoice_id);
  IF st IN ('posted','cancelled') THEN
    RAISE EXCEPTION 'KITE: a % invoice''s lines are not changed', st USING ERRCODE = 'KT083';
  END IF;
  RETURN coalesce(NEW, OLD);
END $$;
CREATE TRIGGER invoice_line_guard BEFORE INSERT OR UPDATE OR DELETE ON purch.invoice_line FOR EACH ROW EXECUTE FUNCTION purch.invoice_line_guard();

-- Invoiced quantities on receipt lines never exceed what was received less what went back before the invoice (at commit).
CREATE FUNCTION purch.receipt_qty_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE rl uuid; received numeric; invoiced numeric; returned numeric; all_returned numeric;
BEGIN
  rl := NEW.receipt_line_id;
  IF rl IS NULL THEN RETURN NULL; END IF;
  SELECT qty INTO received FROM purch.receipt_line WHERE id = rl;
  SELECT coalesce(sum(il.qty), 0) INTO invoiced FROM purch.invoice_line il JOIN purch.purchase_invoice i ON i.id = il.invoice_id
   WHERE il.receipt_line_id = rl AND i.status = 'posted';
  SELECT coalesce(sum(x.qty) FILTER (WHERE NOT x.invoiced), 0), coalesce(sum(x.qty), 0) INTO returned, all_returned
    FROM purch.supplier_return_line x WHERE x.receipt_line_id = rl;
  -- Goods returned before their invoice are no longer invoiceable; goods returned after it go back on a debit note.
  IF invoiced + returned > received OR all_returned > received THEN
    RAISE EXCEPTION 'KITE: receipt line % has % received but % invoiced and % returned', rl, received, invoiced, all_returned USING ERRCODE = 'KT082';
  END IF;
  RETURN NULL;
END $$;

-- ================================================================ supplier returns: debit notes (16.2)
CREATE TABLE purch.supplier_return (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  warehouse_id     uuid NOT NULL REFERENCES core.warehouse(id),
  number           text NOT NULL,
  supplier_id      uuid NOT NULL REFERENCES purch.supplier(id),
  return_date      date NOT NULL,
  reason           text NOT NULL CHECK (length(trim(reason)) > 0),
  net_total        numeric(18,3) NOT NULL CHECK (net_total >= 0),     -- what the supplier owes back (or no longer owed), before VAT
  tax_total        numeric(18,3) NOT NULL CHECK (tax_total >= 0),
  stock_value      numeric(18,3) NOT NULL CHECK (stock_value >= 0),
  journal_id       uuid REFERENCES acct.journal(id),
  ledger_entry_id  uuid REFERENCES purch.supplier_ledger(id),
  created_by       uuid NOT NULL,
  created_at       timestamptz NOT NULL DEFAULT now(),
  origin_server_id uuid,
  UNIQUE (business_id, number)
);
CREATE TABLE purch.supplier_return_line (
  id               uuid PRIMARY KEY,
  return_id        uuid NOT NULL REFERENCES purch.supplier_return(id),
  line_no          smallint NOT NULL CHECK (line_no > 0),
  receipt_line_id  uuid NOT NULL REFERENCES purch.receipt_line(id),
  device_unit_id   uuid REFERENCES device.device_unit(id),
  qty              numeric(14,3) NOT NULL CHECK (qty > 0),
  stock_value      numeric(18,3) NOT NULL CHECK (stock_value >= 0),     -- the cost that left the stock
  invoiced         boolean NOT NULL,                                     -- a debit note against the invoice, or out of 2110
  net_amount       numeric(18,3) NOT NULL CHECK (net_amount >= 0),
  tax_amount       numeric(18,3) NOT NULL CHECK (tax_amount >= 0),
  UNIQUE (return_id, line_no),
  CHECK (device_unit_id IS NULL OR qty = 1),
  CHECK (invoiced OR tax_amount = 0)
);
CREATE INDEX supplier_return_line_receipt ON purch.supplier_return_line (receipt_line_id);
CREATE CONSTRAINT TRIGGER invoice_line_qty AFTER INSERT OR UPDATE ON purch.invoice_line
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION purch.receipt_qty_check();
CREATE CONSTRAINT TRIGGER supplier_return_line_qty AFTER INSERT ON purch.supplier_return_line
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION purch.receipt_qty_check();
-- Posting an invoice re-checks its lines (the status changes, not the lines).
CREATE FUNCTION purch.invoice_post_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE r record; received numeric; invoiced numeric; returned numeric;
BEGIN
  IF NEW.status <> 'posted' OR OLD.status = 'posted' THEN RETURN NULL; END IF;
  FOR r IN SELECT DISTINCT receipt_line_id FROM purch.invoice_line WHERE invoice_id = NEW.id AND receipt_line_id IS NOT NULL LOOP
    SELECT qty INTO received FROM purch.receipt_line WHERE id = r.receipt_line_id;
    SELECT coalesce(sum(il.qty), 0) INTO invoiced FROM purch.invoice_line il JOIN purch.purchase_invoice i ON i.id = il.invoice_id
     WHERE il.receipt_line_id = r.receipt_line_id AND i.status = 'posted';
    SELECT coalesce(sum(x.qty), 0) INTO returned FROM purch.supplier_return_line x WHERE x.receipt_line_id = r.receipt_line_id AND NOT x.invoiced;
    IF invoiced + returned > received THEN
      RAISE EXCEPTION 'KITE: receipt line % has % received but % invoiced and % returned', r.receipt_line_id, received, invoiced, returned USING ERRCODE = 'KT082';
    END IF;
  END LOOP;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER purchase_invoice_posted_qty AFTER UPDATE ON purch.purchase_invoice
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION purch.invoice_post_check();

-- Received quantities on order lines never exceed the order plus the policy's over-receipt allowance (at commit).
CREATE FUNCTION purch.po_receipt_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE ordered numeric; received numeric; allow numeric;
BEGIN
  IF NEW.po_line_id IS NULL THEN RETURN NULL; END IF;
  SELECT l.qty, coalesce(pp.over_receipt_pct, 0) INTO ordered, allow
    FROM purch.po_line l JOIN purch.purchase_order o ON o.id = l.po_id LEFT JOIN purch.policy pp ON pp.business_id = o.business_id
   WHERE l.id = NEW.po_line_id;
  SELECT coalesce(sum(qty), 0) INTO received FROM purch.receipt_line WHERE po_line_id = NEW.po_line_id;
  IF received > ordered * (1 + allow / 100) THEN
    RAISE EXCEPTION 'KITE: order line % has % ordered but % received', NEW.po_line_id, ordered, received USING ERRCODE = 'KT082';
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER receipt_line_po_qty AFTER INSERT ON purch.receipt_line
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION purch.po_receipt_check();

-- ================================================================ supplier payments and allocations (16.1, 16.2)
CREATE TABLE purch.supplier_payment (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),       -- the branch that pays
  number           text NOT NULL,
  supplier_id      uuid NOT NULL REFERENCES purch.supplier(id),
  payment_date     date NOT NULL,
  method           text NOT NULL CHECK (method IN ('cash','bank_transfer','cheque')),
  reference        text,
  amount           numeric(18,3) NOT NULL CHECK (amount > 0),
  note             text,
  journal_id       uuid REFERENCES acct.journal(id),
  created_by       uuid NOT NULL,
  created_at       timestamptz NOT NULL DEFAULT now(),
  origin_server_id uuid,
  UNIQUE (business_id, number),
  CHECK (method = 'cash' OR length(trim(coalesce(reference, ''))) > 0)
);

-- A payment or a debit note (a negative ledger row) settles invoices and openings (positive rows), oldest due first.
CREATE TABLE purch.supplier_allocation (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  debit_entry_id   uuid NOT NULL REFERENCES purch.supplier_ledger(id),   -- the invoice or opening settled
  credit_entry_id  uuid NOT NULL REFERENCES purch.supplier_ledger(id),   -- the payment or debit note settling it
  amount           numeric(18,3) NOT NULL CHECK (amount > 0),
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid
);
CREATE INDEX supplier_allocation_debit ON purch.supplier_allocation (debit_entry_id);
CREATE INDEX supplier_allocation_credit ON purch.supplier_allocation (credit_entry_id);
CREATE TRIGGER supplier_allocation_immutable BEFORE UPDATE OR DELETE ON purch.supplier_allocation FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
CREATE FUNCTION purch.supplier_allocation_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE d purch.supplier_ledger%ROWTYPE; c purch.supplier_ledger%ROWTYPE; used numeric;
BEGIN
  SELECT * INTO d FROM purch.supplier_ledger WHERE id = NEW.debit_entry_id;
  SELECT * INTO c FROM purch.supplier_ledger WHERE id = NEW.credit_entry_id;
  IF d.amount <= 0 OR d.kind NOT IN ('invoice','opening','adjustment') OR c.amount >= 0 OR c.kind NOT IN ('payment','debit_note','adjustment') THEN
    RAISE EXCEPTION 'KITE: an allocation settles an invoice with a payment or a debit note' USING ERRCODE = 'KT084';
  END IF;
  IF d.branch_id <> c.branch_id THEN
    RAISE EXCEPTION 'KITE: an allocation is between rows of one branch' USING ERRCODE = 'KT084';
  END IF;
  SELECT coalesce(sum(amount), 0) INTO used FROM purch.supplier_allocation WHERE debit_entry_id = d.id;
  IF used > d.amount THEN
    RAISE EXCEPTION 'KITE: % is settled for more than it is', d.number USING ERRCODE = 'KT084';
  END IF;
  SELECT coalesce(sum(amount), 0) INTO used FROM purch.supplier_allocation WHERE credit_entry_id = c.id;
  IF used > -c.amount THEN
    RAISE EXCEPTION 'KITE: % settles more than it holds', c.number USING ERRCODE = 'KT084';
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER supplier_allocation_check AFTER INSERT ON purch.supplier_allocation
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION purch.supplier_allocation_check();

ALTER TABLE purch.supplier_ledger ADD COLUMN payment_id uuid REFERENCES purch.supplier_payment(id);

-- ================================================================ expenses and card settlements (17.3)
CREATE SCHEMA IF NOT EXISTS exp;
GRANT USAGE ON SCHEMA exp TO kite_app, kite_ro;
CREATE TABLE exp.expense (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  number           text NOT NULL,
  expense_date     date NOT NULL,
  account_id       uuid NOT NULL REFERENCES acct.account(id),
  description      text NOT NULL CHECK (length(trim(description)) > 0),
  supplier_id      uuid REFERENCES purch.supplier(id),
  payee            text,
  reference        text,
  net_amount       numeric(18,3) NOT NULL CHECK (net_amount > 0),
  tax_code_id      uuid NOT NULL REFERENCES tax.tax_code(id),
  tax_rate         numeric(7,4) NOT NULL CHECK (tax_rate >= 0),
  tax_amount       numeric(18,3) NOT NULL CHECK (tax_amount >= 0),
  gross_amount     numeric(18,3) NOT NULL,
  paid_by          text NOT NULL CHECK (paid_by IN ('cash','bank_transfer','cheque')),
  journal_id       uuid REFERENCES acct.journal(id),
  created_by       uuid NOT NULL,
  created_at       timestamptz NOT NULL DEFAULT now(),
  origin_server_id uuid,
  UNIQUE (business_id, number),
  CHECK (gross_amount = net_amount + tax_amount)
);
CREATE TRIGGER expense_immutable BEFORE UPDATE OR DELETE ON exp.expense FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

CREATE TABLE acct.card_settlement (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  number           text NOT NULL,
  settlement_date  date NOT NULL,
  gross_amount     numeric(18,3) NOT NULL CHECK (gross_amount > 0),     -- cleared from card clearing (1150)
  fees             numeric(18,3) NOT NULL CHECK (fees >= 0),
  net_amount       numeric(18,3) NOT NULL CHECK (net_amount >= 0),
  reference        text NOT NULL CHECK (length(trim(reference)) > 0),
  journal_id       uuid REFERENCES acct.journal(id),
  created_by       uuid NOT NULL,
  created_at       timestamptz NOT NULL DEFAULT now(),
  origin_server_id uuid,
  UNIQUE (business_id, number),
  CHECK (gross_amount = net_amount + fees)
);
CREATE TRIGGER card_settlement_immutable BEFORE UPDATE OR DELETE ON acct.card_settlement FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

-- ================================================================ VAT return (17.4)
-- The boxes of the return form, as data: a change to the official form is a change here. Seeded from the Tax Authority's
-- form, flagged for the accountant to confirm, like the tax codes.
CREATE TABLE tax.vat_box (
  id                       uuid PRIMARY KEY,
  business_id              uuid NOT NULL REFERENCES core.business(id),
  code                     text NOT NULL CHECK (code ~ '^[0-9]{1,2}[a-z]?$'),
  sort                     smallint NOT NULL,
  name_en                  text NOT NULL,
  name_ar                  text NOT NULL,
  source                   text NOT NULL CHECK (source IN ('output','input','manual','formula')),
  tax_kinds                text[],                      -- output/input: which tax code kinds
  account_class            text CHECK (account_class IN ('fixed_assets','other')),   -- input: purchases of fixed assets, or the rest
  formula                  text,                        -- formula: '1a+1f+2a+2b+4a', or a box's VAT with :vat
  needs_accountant_review  boolean NOT NULL DEFAULT true,
  reviewed_by              uuid,
  reviewed_at              timestamptz,
  version                  integer NOT NULL DEFAULT 1,
  updated_at               timestamptz NOT NULL DEFAULT now(),
  UNIQUE (business_id, code),
  CHECK ((source IN ('output','input')) = (tax_kinds IS NOT NULL)),
  CHECK ((source = 'formula') = (formula IS NOT NULL))
);
CREATE TRIGGER vat_box_touch BEFORE UPDATE ON tax.vat_box FOR EACH ROW EXECUTE FUNCTION core.touch_row();
INSERT INTO tax.vat_box (id, business_id, code, sort, name_en, name_ar, source, tax_kinds, account_class, formula)
SELECT gen_random_uuid(), b.id, v.code, v.sort, v.name_en, v.name_ar, v.source, v.kinds, v.cls, v.formula
  FROM core.business b CROSS JOIN (VALUES
  ('1a', 10, 'Supplies of goods and services taxed at 5%',                 'توريدات السلع والخدمات الخاضعة لنسبة 5%',         'output', ARRAY['standard'], NULL, NULL),
  ('1b', 20, 'Supplies of goods and services taxed at 0%',                 'توريدات السلع والخدمات الخاضعة لنسبة 0%',         'output', ARRAY['zero'], NULL, NULL),
  ('1c', 30, 'Supplies of goods and services exempt from tax',             'توريدات السلع والخدمات المعفاة',                  'output', ARRAY['exempt'], NULL, NULL),
  ('1d', 40, 'Supplies of goods, tax levy shifted to the recipient in the GCC', 'توريدات السلع مع نقل عبء الضريبة إلى المتلقي في دول المجلس', 'manual', NULL, NULL, NULL),
  ('1e', 50, 'Supplies of services, tax levy shifted to the recipient in the GCC', 'توريدات الخدمات مع نقل عبء الضريبة إلى المتلقي في دول المجلس', 'manual', NULL, NULL, NULL),
  ('1f', 60, 'Supplies of goods under the profit margin scheme',           'توريدات السلع وفق نظام هامش الربح',               'output', ARRAY['margin'], NULL, NULL),
  ('2a', 70, 'Purchases from the GCC subject to reverse charge',           'المشتريات من دول المجلس الخاضعة للاحتساب العكسي',  'manual', NULL, NULL, NULL),
  ('2b', 80, 'Purchases from outside the GCC subject to reverse charge',   'المشتريات من خارج دول المجلس الخاضعة للاحتساب العكسي', 'manual', NULL, NULL, NULL),
  ('3',  90, 'Supplies to countries outside Oman',                         'التوريدات إلى دول خارج السلطنة',                  'manual', NULL, NULL, NULL),
  ('4a', 100, 'Import of goods (postponed payment)',                       'استيراد السلع (الدفع المؤجل)',                     'manual', NULL, NULL, NULL),
  ('4b', 110, 'Total goods imported',                                      'إجمالي السلع المستوردة',                           'manual', NULL, NULL, NULL),
  ('5a', 120, 'Total VAT due (1a + 1f + 2a + 2b + 4a)',                    'إجمالي الضريبة المستحقة (1أ + 1و + 2أ + 2ب + 4أ)', 'formula', NULL, NULL, '1a+1f+2a+2b+4a'),
  ('5b', 130, 'Adjustment of VAT due',                                     'تعديل الضريبة المستحقة',                           'manual', NULL, NULL, NULL),
  ('6a', 140, 'Purchases (except import of goods)',                        'المشتريات (عدا استيراد السلع)',                    'input', ARRAY['standard'], 'other', NULL),
  ('6b', 150, 'Import of goods',                                           'استيراد السلع',                                    'manual', NULL, NULL, NULL),
  ('6c', 160, 'VAT on acquisition of fixed assets',                        'الضريبة على اقتناء الأصول الثابتة',                'input', ARRAY['standard'], 'fixed_assets', NULL),
  ('6d', 170, 'Adjustment of input VAT credit',                            'تعديل رصيد ضريبة المدخلات',                        'manual', NULL, NULL, NULL),
  ('6e', 180, 'Total input VAT credit (6a + 6b + 6c + 6d)',                'إجمالي رصيد ضريبة المدخلات (6أ + 6ب + 6ج + 6د)',   'formula', NULL, NULL, '6a+6b+6c+6d'),
  ('7',  190, 'Net VAT payable or refundable (5a + 5b - 6e)',              'صافي الضريبة المستحقة أو المستردة (5أ + 5ب - 6هـ)', 'formula', NULL, NULL, '5a+5b-6e')
  ) AS v(code, sort, name_en, name_ar, source, kinds, cls, formula)
ON CONFLICT DO NOTHING;

CREATE TABLE tax.vat_return (
  id                     uuid PRIMARY KEY,
  business_id            uuid NOT NULL REFERENCES core.business(id),
  number                 text NOT NULL,
  period_from            date NOT NULL,
  period_to              date NOT NULL,
  status                 text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','filed')),
  boxes                  jsonb NOT NULL,          -- {"1a": {"value": 0, "vat": 0}, ...}
  adjustments            jsonb NOT NULL DEFAULT '{}'::jsonb,
  output_vat             numeric(18,3) NOT NULL,
  input_vat              numeric(18,3) NOT NULL,
  net_vat                numeric(18,3) NOT NULL,
  refund_requested       boolean NOT NULL DEFAULT false,
  filed_by               uuid,
  filed_at               timestamptz,
  filing_reference       text,
  settlement_journal_id  uuid REFERENCES acct.journal(id),
  paid_journal_id        uuid REFERENCES acct.journal(id),
  paid_at                timestamptz,
  created_by             uuid NOT NULL,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  updated_by             uuid,
  version                integer NOT NULL DEFAULT 1,
  UNIQUE (business_id, number),
  CHECK (period_to >= period_from),
  CHECK ((status = 'filed') = (filed_at IS NOT NULL AND filed_by IS NOT NULL)),   -- a nil return files without a settlement journal
  CHECK (status = 'filed' OR (settlement_journal_id IS NULL AND paid_journal_id IS NULL))
);
-- One return per period; a filed return is changed only to record its payment.
CREATE UNIQUE INDEX vat_return_period ON tax.vat_return (business_id, period_from);
CREATE TRIGGER vat_return_touch BEFORE UPDATE ON tax.vat_return FOR EACH ROW EXECUTE FUNCTION core.touch_row();
CREATE FUNCTION tax.vat_return_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = 'filed' AND (NEW.status <> 'filed' OR NEW.boxes <> OLD.boxes OR NEW.net_vat <> OLD.net_vat OR NEW.period_from <> OLD.period_from
     OR NEW.period_to <> OLD.period_to OR NEW.settlement_journal_id IS DISTINCT FROM OLD.settlement_journal_id
     OR (OLD.paid_journal_id IS NOT NULL AND NEW.paid_journal_id IS DISTINCT FROM OLD.paid_journal_id)) THEN
    RAISE EXCEPTION 'KITE: a filed VAT return is not changed' USING ERRCODE = 'KT085';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER vat_return_guard BEFORE UPDATE ON tax.vat_return FOR EACH ROW EXECUTE FUNCTION tax.vat_return_guard();

-- Every VAT amount the business declared, by the date of the journal that booked it (a late document falls in the period it was
-- posted to), branch by branch: sales and credit notes, VAT on deposits (when deposits are a VAT point) and its reversal when a
-- deposit is applied or refunded, purchase invoices, debit notes and expenses. Output is VAT owed, input VAT reclaimable.
CREATE FUNCTION tax.vat_lines(p_business uuid, p_from date, p_to date)
RETURNS TABLE (branch_id uuid, journal_date date, direction text, tax_kind text, account_class text, net numeric, vat numeric, source_type text, source_id uuid)
LANGUAGE sql STABLE AS $$
  SELECT s.branch_id, j.journal_date, 'output', tc.kind, NULL::text,
         sum(CASE WHEN s.doc_type = 'credit_note' THEN -l.net_amount ELSE l.net_amount END),
         sum(CASE WHEN s.doc_type = 'credit_note' THEN -l.tax_amount ELSE l.tax_amount END), 'sale', s.id
    FROM sales.sale s JOIN sales.sale_line l ON l.sale_id = s.id JOIN tax.tax_code tc ON tc.id = l.tax_code_id
    JOIN acct.journal j ON j.source_doc_type = 'sale' AND j.source_doc_id = s.id AND j.posting_kind IN ('pos.sale','pos.credit_note')
   WHERE s.business_id = p_business AND j.journal_date BETWEEN p_from AND p_to
   GROUP BY s.branch_id, j.journal_date, tc.kind, s.id
  UNION ALL
  SELECT d.branch_id, j.journal_date, 'output', 'standard', NULL,
         CASE WHEN d.kind = 'deposit' THEN d.amount - d.tax ELSE -(d.amount - d.tax) END,
         CASE WHEN d.kind = 'deposit' THEN d.tax ELSE -d.tax END, 'repair_deposit', d.id
    FROM repair.deposit d
    JOIN acct.journal j ON (d.kind <> 'applied' AND j.source_doc_type = 'repair_deposit' AND j.source_doc_id = d.id)
                        OR (d.kind = 'applied' AND j.source_doc_type = 'sale' AND j.source_doc_id = d.sale_id AND j.posting_kind = 'pos.sale')
   WHERE d.business_id = p_business AND d.tax > 0 AND j.journal_date BETWEEN p_from AND p_to
  UNION ALL
  SELECT i.branch_id, j.journal_date, 'input', tc.kind,
         CASE WHEN a.cash_flow_class = 'investing' THEN 'fixed_assets' ELSE 'other' END,
         l.net_amount, l.tax_amount, 'purchase_invoice', i.id
    FROM purch.purchase_invoice i JOIN purch.invoice_line l ON l.invoice_id = i.id JOIN tax.tax_code tc ON tc.id = l.tax_code_id
    LEFT JOIN acct.account a ON a.id = l.account_id
    JOIN acct.journal j ON j.source_doc_type = 'purchase_invoice' AND j.source_doc_id = i.id AND j.posting_kind = 'purch.invoice'
   WHERE i.business_id = p_business AND i.status = 'posted' AND j.journal_date BETWEEN p_from AND p_to
  UNION ALL
  SELECT r.branch_id, j.journal_date, 'input', tc.kind, 'other', -l.net_amount, -l.tax_amount, 'supplier_return', r.id
    FROM purch.supplier_return r JOIN purch.supplier_return_line l ON l.return_id = r.id
    JOIN purch.receipt_line rl ON rl.id = l.receipt_line_id JOIN tax.tax_code tc ON tc.id = rl.tax_code_id
    JOIN acct.journal j ON j.source_doc_type = 'supplier_return' AND j.source_doc_id = r.id AND j.posting_kind = 'purch.return'
   WHERE r.business_id = p_business AND l.invoiced AND j.journal_date BETWEEN p_from AND p_to
  UNION ALL
  SELECT e.branch_id, j.journal_date, 'input', tc.kind, CASE WHEN a.cash_flow_class = 'investing' THEN 'fixed_assets' ELSE 'other' END,
         e.net_amount, e.tax_amount, 'expense', e.id
    FROM exp.expense e JOIN tax.tax_code tc ON tc.id = e.tax_code_id JOIN acct.account a ON a.id = e.account_id
    JOIN acct.journal j ON j.source_doc_type = 'expense' AND j.source_doc_id = e.id AND j.posting_kind = 'exp.expense'
   WHERE e.business_id = p_business AND j.journal_date BETWEEN p_from AND p_to
$$;

-- ================================================================ stock: a cost corrected on items still in stock (16.3)
-- A supplier's invoice that differs from the receipt corrects the cost of what is still on hand: for a phone, a 'unit_cost'
-- movement as in Phase 5; for other items, a 'cost_adjustment' of quantity 0 that moves the balance value (and so the average).
ALTER TABLE inv.stock_movement DROP CONSTRAINT stock_movement_movement_type_check;
ALTER TABLE inv.stock_movement ADD CONSTRAINT stock_movement_movement_type_check CHECK (movement_type IN
  ('opening','adjustment','count','sale','return','emergency_sale','purchase_receipt','supplier_return','transfer_out','transfer_in',
   'unit_cost','refurb_issue','refurb_return','tradein_acquisition','repair_issue','repair_return','cost_adjustment'));
ALTER TABLE inv.stock_movement DROP CONSTRAINT stock_movement_qty_check;
ALTER TABLE inv.stock_movement ADD CONSTRAINT stock_movement_qty_check CHECK ((qty <> 0) <> (movement_type IN ('unit_cost','cost_adjustment')));

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
    IF NEW.movement_type = 'cost_adjustment' THEN
      RAISE EXCEPTION 'KITE: a serialized unit''s cost changes by a unit cost movement' USING ERRCODE = 'KT010';
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
  ELSIF NEW.movement_type = 'cost_adjustment' THEN
    IF NEW.value IS NULL OR NEW.value = 0 OR NEW.value <> round(NEW.value, 3) THEN
      RAISE EXCEPTION 'KITE: a cost adjustment carries a non-zero value' USING ERRCODE = 'KT010';
    END IF;
    IF b.qty <= 0 OR b.value + NEW.value < 0 THEN
      RAISE EXCEPTION 'KITE: a cost is corrected only on stock on hand, and never below zero' USING ERRCODE = 'KT010';
    END IF;
    NEW.unit_cost := NULL;
  ELSIF NEW.qty > 0 THEN
    IF NEW.value IS NOT NULL THEN                        -- an exact value given: a receipt with its landed cost, a part coming back
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

-- A phone still in the business takes a cost correction from its supplier's invoice ('cost_corrected'; costs are otherwise added only
-- while refurbishing).
ALTER TABLE device.device_event DROP CONSTRAINT device_event_event_type_check;
ALTER TABLE device.device_event ADD CONSTRAINT device_event_event_type_check CHECK (event_type IN
  ('received','inspected','cost_added','refurb_completed','sold','sale_voided','returned','written_off','held','released','price_set',
   'supplier_returned','transferred_out','transferred_in','identifier_corrected','receipt_reversed','traded_in','fitted','unfitted','cost_corrected'));
ALTER TABLE device.transition DISABLE TRIGGER device_transition_immutable;
INSERT INTO device.transition (from_status, to_status, event_type)
SELECT s, s, 'cost_corrected' FROM unnest(ARRAY['in_stock','under_inspection','returned_inspection','refurbishing','for_parts','supplier_return','quarantined']) s
ON CONFLICT DO NOTHING;
ALTER TABLE device.transition ENABLE TRIGGER device_transition_immutable;

-- ================================================================ the daily check of the books (17.6)
ALTER TABLE acct.subledger_check DROP CONSTRAINT subledger_check_ledger_check;
ALTER TABLE acct.subledger_check ADD CONSTRAINT subledger_check_ledger_check CHECK (ledger IN
  ('customers','suppliers','inventory','grni','vat_output','vat_input','store_credit','deposits','repair_wip','interbranch','trial_balance'));

-- ================================================================ number series and permissions
INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year)
SELECT gen_random_uuid(), s.business_id, s.branch_id, d.doc_type, d.prefix || '-' || br.code || '-' || s.fiscal_year || '-', s.fiscal_year
  FROM (SELECT DISTINCT business_id, branch_id, fiscal_year FROM core.number_series WHERE branch_id IS NOT NULL AND terminal_id IS NULL) s
  JOIN core.branch br ON br.id = s.branch_id
 CROSS JOIN (VALUES ('purchase_order','PO'), ('goods_receipt','GR'), ('purchase_invoice','PI'), ('supplier_return','DN'),
                    ('supplier_payment','PV'), ('expense','EX'), ('card_settlement','CS'), ('manual_journal','MJ')) AS d(doc_type, prefix)
ON CONFLICT DO NOTHING;

INSERT INTO iam.permission (code, module_code, description_en, description_ar) VALUES
  ('PURCH.VIEW',             'purch',   'View purchase orders, receipts, invoices, returns and payments',      'عرض أوامر الشراء والاستلامات والفواتير والمرتجعات والمدفوعات'),
  ('PURCH.ORDER',            'purch',   'Create and send purchase orders',                                      'إنشاء أوامر الشراء وإرسالها'),
  ('PURCH.APPROVE',          'purch',   'Approve purchase orders, up to the role''s limit',                    'اعتماد أوامر الشراء ضمن حد الدور'),
  ('PURCH.RECEIVE',          'purch',   'Receive goods from suppliers',                                         'استلام البضائع من الموردين'),
  ('PURCH.INVOICE',          'purch',   'Enter and post supplier invoices',                                     'إدخال فواتير الموردين وترحيلها'),
  ('PURCH.INVOICE.OVERRIDE', 'purch',   'Post a supplier invoice outside the match tolerances',                 'ترحيل فاتورة مورد خارج حدود المطابقة'),
  ('PURCH.RETURN',           'purch',   'Return goods to suppliers',                                            'إرجاع البضائع إلى الموردين'),
  ('PURCH.PAY',              'purch',   'Pay suppliers',                                                        'الدفع للموردين'),
  ('PURCH.SETTINGS',         'purch',   'Set the purchasing policy, approval limits and branch supplier lists', 'ضبط سياسة الشراء وحدود الاعتماد وقوائم موردي الفروع'),
  ('ACCT.VIEW',              'acct',    'View the chart of accounts, journals and financial statements',        'عرض دليل الحسابات والقيود والقوائم المالية'),
  ('ACCT.JOURNAL.APPROVE',   'acct',    'Approve manual journals above the threshold (second approver)',       'اعتماد القيود اليدوية فوق الحد (معتمد ثانٍ)'),
  ('ACCT.VAT.RETURN',        'acct',    'Prepare and file VAT returns',                                         'إعداد إقرارات ضريبة القيمة المضافة وتقديمها'),
  ('ACCT.YEAR_END',          'acct',    'Close the fiscal year to retained earnings',                          'إقفال السنة المالية في الأرباح المحتجزة'),
  ('ACCT.CARD_SETTLE',       'acct',    'Record card settlements from the bank',                                'تسجيل تسويات البطاقات من البنك'),
  ('EXP.VIEW',               'exp',     'View expenses',                                                        'عرض المصروفات'),
  ('EXP.CREATE',             'exp',     'Record expenses paid in cash or by bank',                              'تسجيل المصروفات المدفوعة نقدًا أو عبر البنك')
ON CONFLICT DO NOTHING;

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (SELECT code FROM iam.permission WHERE code LIKE 'PURCH.%' OR code LIKE 'ACCT.%' OR code LIKE 'EXP.%') p
 WHERE r.code = 'owner'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('PURCH.VIEW'),('PURCH.INVOICE'),('PURCH.INVOICE.OVERRIDE'),('PURCH.PAY'),('PURCH.SETTINGS'),('PURCH.APPROVE'),
  ('ACCT.VIEW'),('ACCT.JOURNAL.APPROVE'),('ACCT.VAT.RETURN'),('ACCT.YEAR_END'),('ACCT.CARD_SETTLE'),('ACCT.CONTROL_ACCOUNT.POST'),('EXP.VIEW'),('EXP.CREATE')) AS p(code)
 WHERE r.code = 'accountant'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('PURCH.VIEW'),('PURCH.ORDER'),('PURCH.APPROVE'),('PURCH.RECEIVE'),('PURCH.INVOICE'),('PURCH.RETURN'),
  ('CATALOG.VIEW'),('INV.VIEW')) AS p(code)
 WHERE r.code = 'purchasing_officer'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('PURCH.VIEW'),('PURCH.ORDER'),('PURCH.APPROVE'),('PURCH.RECEIVE'),('PURCH.RETURN'),('EXP.VIEW'),('EXP.CREATE')) AS p(code)
 WHERE r.code = 'branch_manager'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('PURCH.VIEW'),('PURCH.RECEIVE')) AS p(code)
 WHERE r.code = 'inventory_controller'
ON CONFLICT DO NOTHING;

-- ================================================================ grants, row-level security
REVOKE DELETE, TRUNCATE ON acct.policy, acct.manual_journal, purch.policy, purch.purchase_order, purch.goods_receipt,
  purch.receipt_line, purch.receipt_unit, purch.receipt_charge, purch.grni_entry, purch.purchase_invoice, purch.supplier_return,
  purch.supplier_return_line, purch.supplier_payment, purch.supplier_allocation, exp.expense, acct.card_settlement, tax.vat_box, tax.vat_return FROM kite_app;
REVOKE UPDATE ON purch.goods_receipt, purch.receipt_line, purch.receipt_unit, purch.receipt_charge, purch.grni_entry, purch.supplier_return,
  purch.supplier_return_line, purch.supplier_payment, purch.supplier_allocation, exp.expense, acct.card_settlement FROM kite_app;
GRANT SELECT, INSERT, UPDATE ON acct.policy, acct.manual_journal, purch.policy, purch.purchase_order, purch.purchase_invoice,
  tax.vat_box, tax.vat_return TO kite_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON acct.manual_journal_line, purch.po_line, purch.invoice_line, purch.branch_supplier, purch.approval_limit TO kite_app;
GRANT SELECT, INSERT ON purch.goods_receipt, purch.receipt_line, purch.receipt_unit, purch.receipt_charge, purch.grni_entry, purch.supplier_return,
  purch.supplier_return_line, purch.supplier_payment, purch.supplier_allocation, exp.expense, acct.card_settlement TO kite_app;
-- Receipts, returns and payments are written once; their journal is linked once, after it is posted.
GRANT UPDATE (journal_id) ON purch.goods_receipt, purch.supplier_return, purch.supplier_payment TO kite_app;
CREATE FUNCTION purch.journal_once() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.journal_id IS NOT NULL OR (to_jsonb(NEW) - 'journal_id') <> (to_jsonb(OLD) - 'journal_id') THEN
    RAISE EXCEPTION 'KITE: % is written once', TG_TABLE_NAME USING ERRCODE = 'KT082';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER goods_receipt_once BEFORE UPDATE ON purch.goods_receipt FOR EACH ROW EXECUTE FUNCTION purch.journal_once();
CREATE TRIGGER supplier_return_once BEFORE UPDATE ON purch.supplier_return FOR EACH ROW EXECUTE FUNCTION purch.journal_once();
CREATE TRIGGER supplier_payment_once BEFORE UPDATE ON purch.supplier_payment FOR EACH ROW EXECUTE FUNCTION purch.journal_once();
REVOKE UPDATE ON acct.fiscal_period FROM kite_app;
GRANT UPDATE (status, updated_at, updated_by, version) ON acct.fiscal_period TO kite_app;
GRANT SELECT ON acct.policy, acct.manual_journal, acct.manual_journal_line, purch.policy, purch.approval_limit, purch.branch_supplier, purch.purchase_order,
  purch.po_line, purch.goods_receipt, purch.receipt_line, purch.receipt_unit, purch.receipt_charge, purch.grni_entry, purch.purchase_invoice, purch.invoice_line,
  purch.supplier_return, purch.supplier_return_line, purch.supplier_payment, purch.supplier_allocation, exp.expense, acct.card_settlement, tax.vat_box,
  tax.vat_return TO kite_ro;
GRANT EXECUTE ON FUNCTION acct.period_status(uuid, uuid), tax.vat_lines(uuid, date, date) TO kite_app, kite_ro;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['acct.manual_journal','purch.goods_receipt','purch.grni_entry','purch.purchase_invoice','purch.supplier_return',
                           'purch.supplier_payment','exp.expense','acct.card_settlement'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY branch_read ON %s FOR SELECT USING (core.branch_visible(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_insert ON %s FOR INSERT WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_update ON %s FOR UPDATE USING (core.branch_visible(branch_id)) WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_delete ON %s FOR DELETE USING (core.branch_writable(branch_id))', t);
  END LOOP;
END
$$;
-- An order is seen by the branch that placed it and by every branch it delivers to.
ALTER TABLE purch.purchase_order ENABLE ROW LEVEL SECURITY;
CREATE POLICY branch_read ON purch.purchase_order FOR SELECT USING (core.branch_visible(branch_id)
  OR EXISTS (SELECT 1 FROM purch.po_line l WHERE l.po_id = purchase_order.id AND core.branch_visible(l.deliver_branch_id)));
CREATE POLICY branch_insert ON purch.purchase_order FOR INSERT WITH CHECK (core.branch_writable(branch_id));
CREATE POLICY branch_update ON purch.purchase_order FOR UPDATE USING (core.branch_visible(branch_id)
  OR EXISTS (SELECT 1 FROM purch.po_line l WHERE l.po_id = purchase_order.id AND core.branch_visible(l.deliver_branch_id)))
  WITH CHECK (true);
