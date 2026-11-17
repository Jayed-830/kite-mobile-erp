-- Phase 10: customer credit (blueprint 14) and suppliers (16.1).
--
-- Credit accounts carry a limit, terms and a status; only head office changes them. Each branch has an offline allowance.
-- Receivables are rows of the customer ledger (crm.credit_ledger, balance type 'receivable'): charges with their
-- instalments (open items), payments, credit notes, write-offs, opening balances and interbranch transfers. A balance is a
-- sum; an open item is its amount less what is allocated to it. Head office records the amounts branch servers reserve
-- online (authorisations), what branch servers push after working offline, breaches of the limit found on sync, and holds.
-- Suppliers are business-wide records kept by head office; an offline branch may add a provisional one. Each branch writes
-- its own supplier ledger lines.

-- Head office's copies on a branch server keep head office's versions (the server sets kite.credit_replica while applying them).
CREATE FUNCTION crm.touch_unless_replica() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF coalesce(current_setting('kite.credit_replica', true), '') = 'on' THEN RETURN NEW; END IF;
  NEW.updated_at := now();
  NEW.version := OLD.version + 1;
  RETURN NEW;
END $$;

-- ================================================================ policy
CREATE TABLE crm.credit_policy (
  business_id              uuid PRIMARY KEY REFERENCES core.business(id),
  default_terms_days       integer NOT NULL DEFAULT 30 CHECK (default_terms_days BETWEEN 0 AND 365),
  block_overdue_days       integer NOT NULL DEFAULT 30 CHECK (block_overdue_days BETWEEN 0 AND 3650),
  hold_overdue_days        integer NOT NULL DEFAULT 60 CHECK (hold_overdue_days BETWEEN 0 AND 3650),
  offline_sale_cap         numeric(18,3) NOT NULL DEFAULT 100 CHECK (offline_sale_cap >= 0),
  override_cap             numeric(18,3) NOT NULL DEFAULT 50 CHECK (override_cap >= 0),
  allow_overcommit         boolean NOT NULL DEFAULT false,
  online_timeout_ms        integer NOT NULL DEFAULT 2000 CHECK (online_timeout_ms BETWEEN 500 AND 10000),
  authorisation_minutes    integer NOT NULL DEFAULT 15 CHECK (authorisation_minutes BETWEEN 1 AND 1440),
  max_instalments          integer NOT NULL DEFAULT 12 CHECK (max_instalments BETWEEN 1 AND 60),
  remind_before_days       integer NOT NULL DEFAULT 3 CHECK (remind_before_days BETWEEN 0 AND 60),   -- 0: no reminder before the due date
  remind_overdue_every     integer NOT NULL DEFAULT 7 CHECK (remind_overdue_every BETWEEN 0 AND 60), -- 0: no overdue reminders
  updated_at               timestamptz NOT NULL DEFAULT now(),
  updated_by               uuid,
  version                  integer NOT NULL DEFAULT 1
);
CREATE TRIGGER credit_policy_touch BEFORE UPDATE ON crm.credit_policy FOR EACH ROW EXECUTE FUNCTION crm.touch_unless_replica();
INSERT INTO crm.credit_policy (business_id) SELECT id FROM core.business ON CONFLICT DO NOTHING;

-- Every change head office makes to an account (or its exposure) gets the next number, so branches ask for "changes since n".
CREATE SEQUENCE crm.credit_sync_seq;

-- ================================================================ accounts and allowances (14.1)
CREATE TABLE crm.credit_account (
  id                 uuid PRIMARY KEY,
  business_id        uuid NOT NULL REFERENCES core.business(id),
  customer_id        uuid NOT NULL UNIQUE REFERENCES crm.customer(id),
  credit_limit       numeric(18,3) NOT NULL CHECK (credit_limit >= 0),
  terms_days         integer NOT NULL CHECK (terms_days BETWEEN 0 AND 365),
  status             text NOT NULL DEFAULT 'active' CHECK (status IN ('active','hold','closed')),
  hold_kind          text CHECK (hold_kind IN ('manual','overdue','exposure')),
  hold_reason        text,
  held_at            timestamptz,
  held_by            uuid,
  offline_sale_cap   numeric(18,3) CHECK (offline_sale_cap >= 0),              -- null: the policy's cap
  note               text,
  approved_by        uuid NOT NULL,                                             -- who set the current limit
  -- What head office last reported (on a branch server: the last known figures; on head office: refreshed on each change).
  exposure           numeric(18,3) NOT NULL DEFAULT 0,
  oldest_due         date,
  exposure_as_of     timestamptz,
  sync_seq           bigint NOT NULL DEFAULT 0,
  version            integer NOT NULL DEFAULT 1,
  created_at         timestamptz NOT NULL DEFAULT now(),
  created_by         uuid,
  updated_at         timestamptz NOT NULL DEFAULT now(),
  updated_by         uuid,
  origin_server_id   uuid,
  CHECK ((status = 'hold') = (hold_kind IS NOT NULL)),
  CHECK (hold_kind IS NULL OR length(trim(coalesce(hold_reason, ''))) > 0)
);
CREATE INDEX credit_account_seq ON crm.credit_account (sync_seq);

CREATE TABLE crm.credit_allowance (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  account_id   uuid NOT NULL REFERENCES crm.credit_account(id),
  branch_id    uuid NOT NULL REFERENCES core.branch(id),
  amount       numeric(18,3) NOT NULL CHECK (amount >= 0),
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid,
  version      integer NOT NULL DEFAULT 1,
  UNIQUE (account_id, branch_id)
);
CREATE TRIGGER credit_allowance_touch BEFORE UPDATE ON crm.credit_allowance FOR EACH ROW EXECUTE FUNCTION crm.touch_unless_replica();

-- An account change takes the next stream number; only a change of its terms moves its version (sales and payments move
-- only the stream number). A branch applying head office's copy keeps head office's numbers.
CREATE FUNCTION crm.credit_account_touch() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
  IF coalesce(current_setting('kite.credit_replica', true), '') = 'on' THEN RETURN NEW; END IF;
  NEW.sync_seq := nextval('crm.credit_sync_seq');
  IF TG_OP = 'UPDATE' AND (NEW.credit_limit, NEW.terms_days, NEW.status, NEW.hold_kind, NEW.hold_reason, NEW.offline_sale_cap, NEW.note)
       IS DISTINCT FROM (OLD.credit_limit, OLD.terms_days, OLD.status, OLD.hold_kind, OLD.hold_reason, OLD.offline_sale_cap, OLD.note) THEN
    NEW.updated_at := now();
    NEW.version := OLD.version + 1;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER credit_account_touch BEFORE INSERT OR UPDATE ON crm.credit_account FOR EACH ROW EXECUTE FUNCTION crm.credit_account_touch();

CREATE FUNCTION crm.credit_allowance_bump() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
  IF coalesce(current_setting('kite.credit_replica', true), '') <> 'on' THEN
    UPDATE crm.credit_account SET sync_seq = sync_seq WHERE id = NEW.account_id;   -- the account's stream number moves with its allowances
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER credit_allowance_bump AFTER INSERT OR UPDATE ON crm.credit_allowance FOR EACH ROW EXECUTE FUNCTION crm.credit_allowance_bump();

-- Allowances add up to no more than the limit unless the owner allows a controlled overcommit (14.1).
CREATE FUNCTION crm.credit_allowance_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE acc uuid; lim numeric; total numeric; over boolean;
BEGIN
  IF coalesce(current_setting('kite.credit_replica', true), '') = 'on' THEN RETURN NULL; END IF;
  IF TG_TABLE_NAME = 'credit_account' THEN acc := NEW.id; ELSE acc := NEW.account_id; END IF;
  SELECT a.credit_limit, p.allow_overcommit INTO lim, over
    FROM crm.credit_account a LEFT JOIN crm.credit_policy p ON p.business_id = a.business_id WHERE a.id = acc;
  SELECT coalesce(sum(amount), 0) INTO total FROM crm.credit_allowance WHERE account_id = acc;
  IF total > lim AND NOT coalesce(over, false) THEN
    RAISE EXCEPTION 'KITE: branch allowances add up to %, more than the limit of %', total, lim USING ERRCODE = 'KT071';
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER credit_allowance_check AFTER INSERT OR UPDATE ON crm.credit_allowance
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION crm.credit_allowance_check();
CREATE CONSTRAINT TRIGGER credit_account_allowances AFTER UPDATE OF credit_limit ON crm.credit_account
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION crm.credit_allowance_check();

-- ================================================================ receivables in the customer ledger (14.1, 14.4)
ALTER TABLE crm.credit_ledger DROP CONSTRAINT credit_ledger_balance_type_check;
ALTER TABLE crm.credit_ledger ADD CONSTRAINT credit_ledger_balance_type_check CHECK (balance_type IN ('store_credit','deposit','receivable'));
ALTER TABLE crm.credit_ledger ADD COLUMN kind text CHECK (kind IN ('charge','payment','credit_note','write_off','opening','transfer'));
ALTER TABLE crm.credit_ledger ADD COLUMN number text;
ALTER TABLE crm.credit_ledger ADD COLUMN doc_date date;
ALTER TABLE crm.credit_ledger ADD CONSTRAINT credit_ledger_receivable_kind CHECK ((balance_type = 'receivable') = (kind IS NOT NULL));
ALTER TABLE crm.credit_ledger ADD CONSTRAINT credit_ledger_receivable_sign CHECK (
  kind IS NULL OR kind IN ('opening','transfer') OR (kind = 'charge' AND amount > 0) OR (kind IN ('payment','credit_note','write_off') AND amount < 0));
ALTER TABLE crm.credit_ledger ADD CONSTRAINT credit_ledger_receivable_doc CHECK (kind IS NULL OR (number IS NOT NULL AND doc_date IS NOT NULL));
CREATE INDEX credit_ledger_receivable ON crm.credit_ledger (customer_id, doc_date) WHERE balance_type = 'receivable';

-- The open items of a charge (or of a positive opening balance): one per instalment, due on its date (14.4).
CREATE TABLE crm.receivable_item (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  customer_id      uuid NOT NULL REFERENCES crm.customer(id),
  entry_id         uuid NOT NULL REFERENCES crm.credit_ledger(id),
  seq              smallint NOT NULL CHECK (seq BETWEEN 1 AND 60),
  due_date         date NOT NULL,
  amount           numeric(18,3) NOT NULL CHECK (amount > 0),
  origin_server_id uuid,
  UNIQUE (entry_id, seq)
);
CREATE INDEX receivable_item_customer ON crm.receivable_item (customer_id, due_date);
CREATE TRIGGER receivable_item_immutable BEFORE UPDATE OR DELETE ON crm.receivable_item FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
CREATE TRIGGER receivable_item_no_truncate BEFORE TRUNCATE ON crm.receivable_item FOR EACH STATEMENT EXECUTE FUNCTION core.forbid_change();

-- A payment, credit note, write-off or negative opening balance settling (part of) an open item.
CREATE TABLE crm.receivable_allocation (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  customer_id      uuid NOT NULL REFERENCES crm.customer(id),
  item_id          uuid NOT NULL REFERENCES crm.receivable_item(id),
  credit_entry_id  uuid NOT NULL REFERENCES crm.credit_ledger(id),
  amount           numeric(18,3) NOT NULL CHECK (amount > 0),
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid,
  origin_server_id uuid
);
CREATE INDEX receivable_allocation_item ON crm.receivable_allocation (item_id);
CREATE INDEX receivable_allocation_credit ON crm.receivable_allocation (credit_entry_id);
CREATE INDEX receivable_allocation_customer ON crm.receivable_allocation (customer_id);
CREATE TRIGGER receivable_allocation_immutable BEFORE UPDATE OR DELETE ON crm.receivable_allocation FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
CREATE TRIGGER receivable_allocation_no_truncate BEFORE TRUNCATE ON crm.receivable_allocation FOR EACH STATEMENT EXECUTE FUNCTION core.forbid_change();

-- Receivable rows take the customer's credit lock (store credit and deposits keep theirs).
CREATE OR REPLACE FUNCTION crm.credit_ledger_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE bal numeric;
BEGIN
  IF NEW.balance_type = 'receivable' THEN
    PERFORM pg_advisory_xact_lock(hashtextextended('kite:credit:' || NEW.customer_id::text, 0));
    UPDATE crm.credit_account SET sync_seq = sync_seq WHERE customer_id = NEW.customer_id;   -- moves the stream number
    RETURN NEW;
  END IF;
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

-- A charge (or positive opening balance) is exactly its instalments; checked at commit, when all of them are written.
CREATE FUNCTION crm.receivable_items_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE total numeric; n integer;
BEGIN
  IF NEW.balance_type <> 'receivable' THEN RETURN NULL; END IF;
  SELECT coalesce(sum(amount), 0), count(*) INTO total, n FROM crm.receivable_item WHERE entry_id = NEW.id;
  IF (NEW.kind = 'charge' OR (NEW.kind = 'opening' AND NEW.amount > 0)) AND total <> NEW.amount THEN
    RAISE EXCEPTION 'KITE: % % has instalments of %', NEW.kind, NEW.amount, total USING ERRCODE = 'KT073';
  END IF;
  IF NOT (NEW.kind = 'charge' OR (NEW.kind = 'opening' AND NEW.amount > 0)) AND n > 0 THEN
    RAISE EXCEPTION 'KITE: only a charge or a positive opening balance has instalments' USING ERRCODE = 'KT073';
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER credit_ledger_items AFTER INSERT ON crm.credit_ledger
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION crm.receivable_items_check();

CREATE FUNCTION crm.receivable_item_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE e record;
BEGIN
  SELECT customer_id, branch_id, kind, amount INTO e FROM crm.credit_ledger WHERE id = NEW.entry_id;
  IF e.customer_id IS DISTINCT FROM NEW.customer_id OR e.branch_id IS DISTINCT FROM NEW.branch_id
     OR NOT (e.kind = 'charge' OR (e.kind = 'opening' AND e.amount > 0)) THEN
    RAISE EXCEPTION 'KITE: an instalment belongs to a charge of the same customer and branch' USING ERRCODE = 'KT073';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER receivable_item_check BEFORE INSERT ON crm.receivable_item FOR EACH ROW EXECUTE FUNCTION crm.receivable_item_check();

-- An allocation never takes an item, or a credit, past its amount; both are the same customer's (14.4).
CREATE FUNCTION crm.receivable_allocation_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE item_amount numeric; item_customer uuid; credit record; used numeric;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('kite:credit:' || NEW.customer_id::text, 0));
  SELECT amount, customer_id INTO item_amount, item_customer FROM crm.receivable_item WHERE id = NEW.item_id;
  SELECT customer_id, balance_type, kind, amount INTO credit FROM crm.credit_ledger WHERE id = NEW.credit_entry_id;
  IF item_customer IS DISTINCT FROM NEW.customer_id OR credit.customer_id IS DISTINCT FROM NEW.customer_id
     OR credit.balance_type IS DISTINCT FROM 'receivable' OR credit.kind = 'transfer' OR credit.amount >= 0 THEN
    RAISE EXCEPTION 'KITE: a payment or credit settles only the same customer''s open items' USING ERRCODE = 'KT070';
  END IF;
  SELECT coalesce(sum(amount), 0) INTO used FROM crm.receivable_allocation WHERE item_id = NEW.item_id;
  IF used + NEW.amount > item_amount THEN
    RAISE EXCEPTION 'KITE: the item has % open; % cannot be allocated to it', item_amount - used, NEW.amount USING ERRCODE = 'KT070';
  END IF;
  SELECT coalesce(sum(amount), 0) INTO used FROM crm.receivable_allocation WHERE credit_entry_id = NEW.credit_entry_id;
  IF used + NEW.amount > -credit.amount THEN
    RAISE EXCEPTION 'KITE: the credit has % unallocated; % cannot be allocated', -credit.amount - used, NEW.amount USING ERRCODE = 'KT070';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER receivable_allocation_check BEFORE INSERT ON crm.receivable_allocation FOR EACH ROW EXECUTE FUNCTION crm.receivable_allocation_check();

-- The customer's receivable across every branch on this server, its open items and the oldest unpaid due date
-- (row-level security would hide other branches; the till needs all of them).
CREATE FUNCTION crm.receivable_status(p_customer uuid, OUT balance numeric, OUT open_items numeric, OUT unallocated numeric, OUT oldest_due date)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
  WITH items AS (
    SELECT i.due_date, i.amount - coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.item_id = i.id), 0) AS open
      FROM crm.receivable_item i WHERE i.customer_id = p_customer
  ), credits AS (
    SELECT -l.amount - coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.credit_entry_id = l.id), 0) AS left_over
      FROM crm.credit_ledger l WHERE l.customer_id = p_customer AND l.balance_type = 'receivable' AND l.amount < 0 AND l.kind <> 'transfer'
  )
  SELECT (SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE customer_id = p_customer AND balance_type = 'receivable'),
         (SELECT coalesce(sum(open), 0) FROM items),
         (SELECT coalesce(sum(left_over), 0) FROM credits),
         (SELECT min(due_date) FROM items WHERE open > 0)
$$;

-- ================================================================ decisions, authorisations, breaches, holds (14.2, 14.3, 14.5)
-- Every credit sale's decision: how it was checked, what was available and who approved going over.
CREATE TABLE crm.credit_decision (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  customer_id      uuid NOT NULL REFERENCES crm.customer(id),
  sale_id          uuid NOT NULL,
  entry_id         uuid NOT NULL REFERENCES crm.credit_ledger(id),
  mode             text NOT NULL CHECK (mode IN ('local','online','offline')),
  amount           numeric(18,3) NOT NULL CHECK (amount > 0),
  available        numeric(18,3) NOT NULL,
  credit_limit     numeric(18,3) NOT NULL,
  exposure         numeric(18,3) NOT NULL,
  allowance        numeric(18,3),
  over_by          numeric(18,3) NOT NULL DEFAULT 0 CHECK (over_by >= 0),
  overdue_days     integer NOT NULL DEFAULT 0,
  approval_id      uuid,
  approved_by      uuid,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid NOT NULL,
  origin_server_id uuid,
  UNIQUE (sale_id),
  CHECK (over_by = 0 OR approved_by IS NOT NULL)
);
CREATE INDEX credit_decision_customer ON crm.credit_decision (customer_id, created_at);
CREATE TRIGGER credit_decision_immutable BEFORE UPDATE OR DELETE ON crm.credit_decision FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

-- An amount a branch server reserved online for a sale it is writing (head office only). It counts in the exposure until the
-- branch pushes the sale's charge (consumed), says the sale did not go through (released), or it expires.
CREATE TABLE crm.credit_authorisation (
  id            uuid PRIMARY KEY,
  business_id   uuid NOT NULL REFERENCES core.business(id),
  customer_id   uuid NOT NULL REFERENCES crm.customer(id),
  branch_id     uuid NOT NULL REFERENCES core.branch(id),
  server_id     uuid NOT NULL,
  sale_id       uuid NOT NULL,
  amount        numeric(18,3) NOT NULL CHECK (amount > 0),
  available     numeric(18,3) NOT NULL,
  over_by       numeric(18,3) NOT NULL DEFAULT 0,
  created_at    timestamptz NOT NULL DEFAULT now(),
  expires_at    timestamptz NOT NULL,
  consumed_at   timestamptz,
  released_at   timestamptz,
  UNIQUE (server_id, sale_id)
);
CREATE INDEX credit_authorisation_active ON crm.credit_authorisation (customer_id) WHERE consumed_at IS NULL AND released_at IS NULL;

-- Exposure as head office counts it: the receivable balance and what branch servers reserved and have not pushed yet.
CREATE FUNCTION crm.credit_exposure(p_customer uuid) RETURNS numeric
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
  SELECT (SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE customer_id = p_customer AND balance_type = 'receivable')
       + (SELECT coalesce(sum(amount), 0) FROM crm.credit_authorisation
           WHERE customer_id = p_customer AND consumed_at IS NULL AND released_at IS NULL AND expires_at > now())
$$;

-- Found on sync: the exposure past the limit once a branch server's offline charges arrived (14.5).
CREATE TABLE crm.credit_breach (
  id              uuid PRIMARY KEY,
  business_id     uuid NOT NULL REFERENCES core.business(id),
  account_id      uuid NOT NULL REFERENCES crm.credit_account(id),
  customer_id     uuid NOT NULL REFERENCES crm.customer(id),
  exposure        numeric(18,3) NOT NULL,
  credit_limit    numeric(18,3) NOT NULL,
  from_server_id  uuid,
  branch_id       uuid REFERENCES core.branch(id),
  entries         integer NOT NULL DEFAULT 0,
  detected_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX credit_breach_account ON crm.credit_breach (account_id, detected_at);
CREATE TRIGGER credit_breach_immutable BEFORE UPDATE OR DELETE ON crm.credit_breach FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

CREATE TABLE crm.credit_hold_event (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  account_id       uuid NOT NULL REFERENCES crm.credit_account(id),
  action           text NOT NULL CHECK (action IN ('hold','release')),
  kind             text NOT NULL CHECK (kind IN ('manual','overdue','exposure')),
  reason           text NOT NULL CHECK (length(trim(reason)) > 0),
  exposure         numeric(18,3),
  credit_limit     numeric(18,3),
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid,
  origin_server_id uuid
);
CREATE INDEX credit_hold_event_account ON crm.credit_hold_event (account_id, created_at);
CREATE TRIGGER credit_hold_event_immutable BEFORE UPDATE OR DELETE ON crm.credit_hold_event FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

-- Customer receipts: payments taken at any branch (a till payment also goes on the drawer through sales.payment).
CREATE TABLE crm.customer_receipt (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  customer_id      uuid NOT NULL REFERENCES crm.customer(id),
  number           text NOT NULL,
  method           text NOT NULL CHECK (method IN ('cash','card','bank_transfer')),
  amount           numeric(18,3) NOT NULL CHECK (amount > 0),
  shift_id         uuid REFERENCES sales.shift(id),
  reference        text,
  note             text,
  entry_id         uuid NOT NULL REFERENCES crm.credit_ledger(id),
  received_at      timestamptz NOT NULL DEFAULT now(),
  created_by       uuid NOT NULL,
  origin_server_id uuid,
  UNIQUE (business_id, number),
  CHECK (method = 'bank_transfer' OR shift_id IS NOT NULL)
);
CREATE INDEX customer_receipt_customer ON crm.customer_receipt (customer_id, received_at);
CREATE TRIGGER customer_receipt_immutable BEFORE UPDATE OR DELETE ON crm.customer_receipt FOR EACH ROW EXECUTE FUNCTION core.forbid_change();

ALTER TABLE sales.payment ADD COLUMN customer_receipt_id uuid REFERENCES crm.customer_receipt(id);
ALTER TABLE sales.payment DROP CONSTRAINT payment_document;
ALTER TABLE sales.payment ADD CONSTRAINT payment_document CHECK (num_nonnulls(sale_id, repair_deposit_id, customer_receipt_id) = 1);
ALTER TABLE sales.payment ADD CONSTRAINT payment_customer_receipt_tenders CHECK (customer_receipt_id IS NULL OR method IN ('cash','card','bank_transfer'));
CREATE INDEX payment_customer_receipt ON sales.payment (customer_receipt_id) WHERE customer_receipt_id IS NOT NULL;
-- "On account": the sale is charged to the customer's credit account (14.3); a credit note can take it back off.
ALTER TABLE sales.payment DROP CONSTRAINT payment_method_check;
ALTER TABLE sales.payment ADD CONSTRAINT payment_method_check CHECK (method IN ('cash','card','bank_transfer','trade_in','store_credit','deposit','credit'));
ALTER TABLE sales.payment DROP CONSTRAINT payment_store_credit;
ALTER TABLE sales.payment ADD CONSTRAINT payment_store_credit CHECK ((method IN ('store_credit','deposit','credit')) = (credit_entry_id IS NOT NULL));

-- WhatsApp reminders sent for an open item (14.5): once before it is due, then every few days while it is overdue.
CREATE TABLE crm.credit_reminder (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  customer_id  uuid NOT NULL REFERENCES crm.customer(id),
  item_id      uuid NOT NULL REFERENCES crm.receivable_item(id),
  kind         text NOT NULL CHECK (kind IN ('before_due','overdue')),
  days         integer NOT NULL,
  message_id   uuid,
  created_at   timestamptz NOT NULL DEFAULT now(),
  UNIQUE (item_id, kind, days)
);

-- ================================================================ a branch server's link state for credit and suppliers
CREATE TABLE crm.credit_link (
  business_id        uuid PRIMARY KEY REFERENCES core.business(id),
  credit_seq         bigint NOT NULL DEFAULT 0,      -- head office's stream number last applied
  supplier_seq       bigint NOT NULL DEFAULT 0,
  last_snapshot_at   timestamptz,
  last_push_at       timestamptz,
  last_push_ok_at    timestamptz,
  last_push_error    text,
  updated_at         timestamptz NOT NULL DEFAULT now()
);
-- Ledger rows head office has confirmed (a branch server pushes the receivable rows it wrote until they are here).
CREATE TABLE crm.credit_entry_ack (
  entry_id   uuid PRIMARY KEY REFERENCES crm.credit_ledger(id),
  acked_at   timestamptz NOT NULL DEFAULT now()
);

-- ================================================================ checks for the books (17)
CREATE TABLE acct.subledger_check (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  ledger       text NOT NULL CHECK (ledger IN ('customers','suppliers')),
  branch_id    uuid REFERENCES core.branch(id),
  as_of        date NOT NULL,
  subledger    numeric(18,3) NOT NULL,
  control      numeric(18,3) NOT NULL,
  difference   numeric(18,3) NOT NULL,
  checked_at   timestamptz NOT NULL DEFAULT now(),
  run_id       uuid NOT NULL
);
CREATE INDEX subledger_check_run ON acct.subledger_check (checked_at DESC);

-- ================================================================ suppliers (16.1)
CREATE SCHEMA IF NOT EXISTS purch;
GRANT USAGE ON SCHEMA purch TO kite_app, kite_ro;

CREATE SEQUENCE purch.supplier_sync_seq;

CREATE TABLE purch.supplier (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  number            text NOT NULL,
  name_en           text NOT NULL CHECK (length(trim(name_en)) > 0),
  name_ar           text NOT NULL CHECK (length(trim(name_ar)) > 0),
  vat_number        text,
  cr_number         text,
  phone_e164        text CHECK (phone_e164 IS NULL OR phone_e164 ~ '^\+[1-9][0-9]{6,14}$'),
  email             text,
  address           text,
  contact_name      text,
  terms_days        integer NOT NULL DEFAULT 30 CHECK (terms_days BETWEEN 0 AND 365),
  status            text NOT NULL DEFAULT 'active' CHECK (status IN ('provisional','active','inactive','merged')),
  merged_into_id    uuid REFERENCES purch.supplier(id),
  origin_branch_id  uuid REFERENCES core.branch(id),
  approved_at       timestamptz,
  approved_by       uuid,
  sync_seq          bigint NOT NULL DEFAULT 0,
  version           integer NOT NULL DEFAULT 1,
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        uuid,
  updated_at        timestamptz NOT NULL DEFAULT now(),
  updated_by        uuid,
  origin_server_id  uuid,
  UNIQUE (business_id, number),
  CHECK ((status = 'merged') = (merged_into_id IS NOT NULL)),
  CHECK (merged_into_id IS DISTINCT FROM id)
);
CREATE INDEX supplier_seq ON purch.supplier (sync_seq);
CREATE INDEX supplier_name ON purch.supplier (lower(name_en));
CREATE TRIGGER supplier_touch BEFORE UPDATE ON purch.supplier FOR EACH ROW EXECUTE FUNCTION crm.touch_unless_replica();

CREATE FUNCTION purch.supplier_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE target text;
BEGIN
  IF coalesce(current_setting('kite.credit_replica', true), '') <> 'on' THEN
    NEW.sync_seq := nextval('purch.supplier_sync_seq');
  END IF;
  IF NEW.merged_into_id IS NOT NULL THEN
    SELECT status INTO target FROM purch.supplier WHERE id = NEW.merged_into_id;
    IF target IS DISTINCT FROM 'active' THEN
      RAISE EXCEPTION 'KITE: a supplier is merged only into an active supplier' USING ERRCODE = 'KT072';
    END IF;
  END IF;
  IF TG_OP = 'UPDATE' AND OLD.status = 'merged' AND (NEW.status <> 'merged' OR NEW.merged_into_id <> OLD.merged_into_id) THEN
    RAISE EXCEPTION 'KITE: a merged supplier stays merged' USING ERRCODE = 'KT072';
  END IF;
  IF TG_OP = 'UPDATE' AND NEW.status = 'provisional' AND OLD.status <> 'provisional' THEN
    RAISE EXCEPTION 'KITE: an approved supplier is not provisional again' USING ERRCODE = 'KT072';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER supplier_check BEFORE INSERT OR UPDATE ON purch.supplier FOR EACH ROW EXECUTE FUNCTION purch.supplier_check();

-- Each branch's own lines with a supplier: + the business owes more, - less. Phase 10 writes opening balances; purchase
-- invoices, payments and debit notes come with purchasing (Phase 11).
CREATE TABLE purch.supplier_ledger (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  supplier_id      uuid NOT NULL REFERENCES purch.supplier(id),
  kind             text NOT NULL CHECK (kind IN ('opening','invoice','payment','debit_note','adjustment')),
  amount           numeric(18,3) NOT NULL CHECK (amount <> 0),
  doc_date         date NOT NULL,
  due_date         date,
  number           text NOT NULL,
  source_doc_type  text NOT NULL,
  source_doc_id    uuid NOT NULL,
  note             text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid NOT NULL,
  origin_server_id uuid
);
CREATE INDEX supplier_ledger_supplier ON purch.supplier_ledger (supplier_id, doc_date);
CREATE TRIGGER supplier_ledger_immutable BEFORE UPDATE OR DELETE ON purch.supplier_ledger FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
CREATE TRIGGER supplier_ledger_no_truncate BEFORE TRUNCATE ON purch.supplier_ledger FOR EACH STATEMENT EXECUTE FUNCTION core.forbid_change();

CREATE FUNCTION purch.supplier_ledger_check() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE st text;
BEGIN
  SELECT status INTO st FROM purch.supplier WHERE id = NEW.supplier_id;
  -- A branch's line made before head office merged the supplier still arrives from the branch; new lines go to the survivor.
  IF st = 'merged' AND NEW.origin_server_id IS NOT DISTINCT FROM (SELECT id FROM core.server WHERE is_self) THEN
    RAISE EXCEPTION 'KITE: the supplier was merged; use the supplier it was merged into' USING ERRCODE = 'KT072';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER supplier_ledger_check BEFORE INSERT ON purch.supplier_ledger FOR EACH ROW EXECUTE FUNCTION purch.supplier_ledger_check();

CREATE TABLE purch.supplier_entry_ack (
  entry_id   uuid PRIMARY KEY REFERENCES purch.supplier_ledger(id),
  acked_at   timestamptz NOT NULL DEFAULT now()
);
-- Provisional suppliers a branch server added, and the version head office has.
CREATE TABLE purch.supplier_push (
  supplier_id  uuid PRIMARY KEY REFERENCES purch.supplier(id),
  version      integer NOT NULL,
  acked_at     timestamptz NOT NULL DEFAULT now()
);

-- A supplier's balance across every branch on this server, following merges.
CREATE FUNCTION purch.supplier_balance(p_supplier uuid) RETURNS numeric
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
  SELECT coalesce(sum(l.amount), 0) FROM purch.supplier_ledger l
   WHERE l.supplier_id = p_supplier OR l.supplier_id IN (SELECT id FROM purch.supplier WHERE merged_into_id = p_supplier)
$$;

-- ================================================================ WhatsApp templates for credit reminders
ALTER TABLE notif.outbound_message DROP CONSTRAINT outbound_message_template_check;
ALTER TABLE notif.outbound_message ADD CONSTRAINT outbound_message_template_check
  CHECK (template IN ('repair_received','repair_estimate','repair_ready','repair_uncollected','credit_due','credit_overdue'));
ALTER TABLE notif.template DROP CONSTRAINT template_code_check;
ALTER TABLE notif.template ADD CONSTRAINT template_code_check
  CHECK (code IN ('repair_received','repair_estimate','repair_ready','repair_uncollected','credit_due','credit_overdue'));

-- ================================================================ number series and permissions
INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year)
SELECT gen_random_uuid(), s.business_id, s.branch_id, d.doc_type, d.prefix || '-' || br.code || '-' || s.fiscal_year || '-', s.fiscal_year
  FROM (SELECT DISTINCT business_id, branch_id, fiscal_year FROM core.number_series WHERE branch_id IS NOT NULL AND terminal_id IS NULL) s
  JOIN core.branch br ON br.id = s.branch_id
 CROSS JOIN (VALUES ('credit_write_off','WO'), ('opening_balance','OB'), ('supplier_provisional','SP')) AS d(doc_type, prefix)
ON CONFLICT DO NOTHING;
-- Suppliers are numbered business-wide by head office.
INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year, padding)
SELECT gen_random_uuid(), b.id, NULL, 'supplier', 'S-', 2000, 5 FROM core.business b
ON CONFLICT DO NOTHING;

INSERT INTO iam.permission (code, module_code, description_en, description_ar) VALUES
  ('CREDIT.VIEW',     'credit', 'View credit accounts, statements and credit reports',                    'عرض حسابات الائتمان والكشوف وتقارير الائتمان'),
  ('CREDIT.MANAGE',   'credit', 'Set credit limits, terms, branch allowances and holds (head office)',    'تحديد حدود الائتمان والمدد ومخصصات الفروع والإيقاف (المكتب الرئيسي)'),
  ('CREDIT.RECEIVE',  'credit', 'Take payments from credit customers',                                    'استلام دفعات عملاء الائتمان'),
  ('SUPPLIER.VIEW',   'purch',  'View suppliers, their balances and statements',                          'عرض الموردين وأرصدتهم وكشوفهم'),
  ('SUPPLIER.MANAGE', 'purch',  'Add and edit suppliers and their opening balances',                      'إضافة الموردين وتعديلهم وأرصدتهم الافتتاحية'),
  ('SUPPLIER.APPROVE','purch',  'Approve or merge suppliers added by branches (head office)',             'اعتماد الموردين المضافين من الفروع أو دمجهم (المكتب الرئيسي)')
ON CONFLICT DO NOTHING;

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (SELECT code FROM iam.permission WHERE code LIKE 'CREDIT.%' OR code LIKE 'SUPPLIER.%') p
 WHERE r.code = 'owner'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('CREDIT.VIEW'),('CREDIT.RECEIVE'),('SUPPLIER.VIEW'),('SUPPLIER.MANAGE')) AS p(code)
 WHERE r.code = 'branch_manager'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('CREDIT.RECEIVE')) AS p(code)
 WHERE r.code IN ('senior_cashier','cashier')
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('CREDIT.VIEW'),('CREDIT.MANAGE'),('CREDIT.RECEIVE'),('SUPPLIER.VIEW'),('SUPPLIER.MANAGE'),('SUPPLIER.APPROVE')) AS p(code)
 WHERE r.code = 'accountant'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code FROM iam.role r CROSS JOIN (VALUES ('SUPPLIER.VIEW'),('SUPPLIER.MANAGE')) AS p(code)
 WHERE r.code = 'purchasing_officer'
ON CONFLICT DO NOTHING;

-- ================================================================ grants, row-level security
REVOKE DELETE, TRUNCATE ON crm.credit_account, crm.credit_allowance, crm.receivable_item, crm.receivable_allocation, crm.credit_decision,
  crm.credit_breach, crm.credit_hold_event, crm.customer_receipt, crm.credit_reminder, purch.supplier, purch.supplier_ledger FROM kite_app;
REVOKE UPDATE ON crm.receivable_item, crm.receivable_allocation, crm.credit_decision, crm.credit_breach, crm.credit_hold_event, crm.customer_receipt,
  crm.credit_reminder, crm.credit_entry_ack, acct.subledger_check, purch.supplier_ledger, purch.supplier_entry_ack FROM kite_app;
GRANT SELECT, INSERT, UPDATE ON crm.credit_policy, crm.credit_account, crm.credit_allowance, crm.credit_authorisation, crm.credit_link, purch.supplier TO kite_app;
GRANT SELECT, INSERT ON crm.receivable_item, crm.receivable_allocation, crm.credit_decision, crm.credit_breach, crm.credit_hold_event,
  crm.customer_receipt, crm.credit_reminder, crm.credit_entry_ack, acct.subledger_check, purch.supplier_ledger, purch.supplier_entry_ack TO kite_app;
GRANT SELECT, INSERT, UPDATE ON purch.supplier_push TO kite_app;
GRANT SELECT ON crm.credit_policy, crm.credit_account, crm.credit_allowance, crm.receivable_item, crm.receivable_allocation, crm.credit_decision,
  crm.credit_authorisation, crm.credit_breach, crm.credit_hold_event, crm.customer_receipt, crm.credit_reminder, crm.credit_link, crm.credit_entry_ack,
  acct.subledger_check, purch.supplier, purch.supplier_ledger, purch.supplier_entry_ack, purch.supplier_push TO kite_ro;
GRANT USAGE ON SEQUENCE crm.credit_sync_seq, purch.supplier_sync_seq TO kite_app;
GRANT EXECUTE ON FUNCTION crm.receivable_status(uuid), crm.credit_exposure(uuid), purch.supplier_balance(uuid) TO kite_app, kite_ro;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['crm.receivable_item','crm.credit_decision','crm.customer_receipt','purch.supplier_ledger'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY branch_read ON %s FOR SELECT USING (core.branch_visible(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_insert ON %s FOR INSERT WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_update ON %s FOR UPDATE USING (core.branch_visible(branch_id)) WITH CHECK (core.branch_writable(branch_id))', t);
    EXECUTE format('CREATE POLICY branch_delete ON %s FOR DELETE USING (core.branch_writable(branch_id))', t);
  END LOOP;
END
$$;
