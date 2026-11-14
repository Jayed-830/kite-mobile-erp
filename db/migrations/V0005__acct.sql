-- V0005: accounting foundation (blueprint 17). Journals are append-only ledgers.

CREATE TABLE acct.account (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  code         text NOT NULL CHECK (code ~ '^[0-9]{3,10}$'),
  name_en      text NOT NULL,
  name_ar      text NOT NULL,
  type         text NOT NULL CHECK (type IN ('asset','liability','equity','revenue','expense')),
  parent_id    uuid REFERENCES acct.account(id),
  is_postable  boolean NOT NULL DEFAULT true,
  is_control   boolean NOT NULL DEFAULT false,   -- receivables, payables, inventory, VAT (17.6)
  status       text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   uuid,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid,
  version      integer NOT NULL DEFAULT 1,
  UNIQUE (business_id, code)
);
CREATE TRIGGER account_touch BEFORE UPDATE ON acct.account FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE acct.fiscal_period (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  branch_id    uuid REFERENCES core.branch(id),     -- NULL = business-wide period
  fiscal_year  integer NOT NULL,
  period_no    smallint NOT NULL CHECK (period_no BETWEEN 1 AND 13),
  starts_on    date NOT NULL,
  ends_on      date NOT NULL,
  status       text NOT NULL DEFAULT 'open' CHECK (status IN ('open','soft_closed','closed')),
  created_at   timestamptz NOT NULL DEFAULT now(),
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid,
  version      integer NOT NULL DEFAULT 1,
  CHECK (ends_on >= starts_on),
  CONSTRAINT fiscal_period_unique UNIQUE NULLS NOT DISTINCT (business_id, branch_id, fiscal_year, period_no)
);
CREATE TRIGGER fiscal_period_touch BEFORE UPDATE ON acct.fiscal_period FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- Posting rules are versioned data synced from the main server (17.1).
CREATE TABLE acct.posting_rule (
  id              uuid PRIMARY KEY,
  business_id     uuid NOT NULL REFERENCES core.business(id),
  event_type      text NOT NULL,
  rule_version    integer NOT NULL CHECK (rule_version > 0),
  effective_from  date NOT NULL,
  definition      jsonb NOT NULL,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      uuid,
  UNIQUE (business_id, event_type, rule_version)
);

CREATE TABLE acct.journal (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  branch_id         uuid REFERENCES core.branch(id),   -- NULL only for head-office journals
  number            text NOT NULL,
  journal_date      date NOT NULL,
  fiscal_period_id  uuid NOT NULL REFERENCES acct.fiscal_period(id),
  source_doc_type   text NOT NULL,
  source_doc_id     uuid NOT NULL,
  posting_kind      text NOT NULL,
  description       text,
  reversal_of_id    uuid UNIQUE REFERENCES acct.journal(id),   -- a journal is reversed at most once
  posted_at         timestamptz NOT NULL DEFAULT now(),
  posted_by         uuid,
  origin_server_id  uuid,
  origin_device_id  uuid,
  -- A document can never post the same kind of journal twice (9.4 rule 3).
  CONSTRAINT journal_no_double_posting UNIQUE (source_doc_type, source_doc_id, posting_kind),
  UNIQUE (business_id, number)
);

CREATE TABLE acct.journal_line (
  id           uuid PRIMARY KEY,
  journal_id   uuid NOT NULL REFERENCES acct.journal(id),
  line_no      smallint NOT NULL CHECK (line_no > 0),
  account_id   uuid NOT NULL REFERENCES acct.account(id),
  branch_id    uuid REFERENCES core.branch(id),
  debit        numeric(18,3) NOT NULL DEFAULT 0 CHECK (debit >= 0),
  credit       numeric(18,3) NOT NULL DEFAULT 0 CHECK (credit >= 0),
  party_type   text CHECK (party_type IN ('customer','supplier','employee')),
  party_id     uuid,
  tax_code_id  uuid REFERENCES tax.tax_code(id),
  memo         text,
  CHECK (debit = 0 OR credit = 0),
  CHECK (debit > 0 OR credit > 0),
  CHECK ((party_type IS NULL) = (party_id IS NULL)),
  UNIQUE (journal_id, line_no)
);
CREATE INDEX journal_line_account ON acct.journal_line (account_id, branch_id);
CREATE INDEX journal_line_party ON acct.journal_line (party_type, party_id) WHERE party_id IS NOT NULL;

-- Ledgers are append-only: block UPDATE and DELETE for everyone, owner included.
CREATE TRIGGER journal_immutable BEFORE UPDATE OR DELETE ON acct.journal
  FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
CREATE TRIGGER journal_line_immutable BEFORE UPDATE OR DELETE ON acct.journal_line
  FOR EACH ROW EXECUTE FUNCTION core.forbid_change();
-- TRUNCATE bypasses row triggers, so block it separately.
CREATE TRIGGER journal_no_truncate BEFORE TRUNCATE ON acct.journal
  FOR EACH STATEMENT EXECUTE FUNCTION core.forbid_change();
CREATE TRIGGER journal_line_no_truncate BEFORE TRUNCATE ON acct.journal_line
  FOR EACH STATEMENT EXECUTE FUNCTION core.forbid_change();

-- Posting is allowed only into an open period that covers the journal date. Soft-closed
-- periods accept postings only when the accountant's transaction sets kite.allow_soft_closed.
CREATE OR REPLACE FUNCTION acct.check_period() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE p acct.fiscal_period%ROWTYPE;
BEGIN
  SELECT * INTO p FROM acct.fiscal_period WHERE id = NEW.fiscal_period_id;
  IF NEW.journal_date < p.starts_on OR NEW.journal_date > p.ends_on THEN
    RAISE EXCEPTION 'KITE: journal date % is outside fiscal period %-%', NEW.journal_date, p.fiscal_year, p.period_no
      USING ERRCODE = 'KT003';
  END IF;
  IF p.status = 'closed'
     OR (p.status = 'soft_closed' AND coalesce(current_setting('kite.allow_soft_closed', true), '') <> 'on') THEN
    RAISE EXCEPTION 'KITE: fiscal period %-% is %', p.fiscal_year, p.period_no, p.status
      USING ERRCODE = 'KT003';
  END IF;
  RETURN NEW;
END
$$;
CREATE TRIGGER journal_period_check BEFORE INSERT ON acct.journal
  FOR EACH ROW EXECUTE FUNCTION acct.check_period();

-- Balanced-journal check runs at COMMIT (deferred), after all lines are inserted (9.4 rule 2).
CREATE OR REPLACE FUNCTION acct.check_journal_balanced() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
  v_journal uuid;
  v_lines   integer;
  v_debit   numeric;
  v_credit  numeric;
BEGIN
  -- Separate branches: each NEW field reference is only prepared for its own table.
  IF TG_TABLE_NAME = 'journal' THEN
    v_journal := NEW.id;
  ELSE
    v_journal := NEW.journal_id;
  END IF;
  SELECT count(*), coalesce(sum(debit),0), coalesce(sum(credit),0)
    INTO v_lines, v_debit, v_credit
    FROM acct.journal_line WHERE journal_id = v_journal;
  IF v_lines < 2 THEN
    RAISE EXCEPTION 'KITE: journal % has % line(s); at least two are required', v_journal, v_lines
      USING ERRCODE = 'KT002';
  END IF;
  IF v_debit <> v_credit THEN
    RAISE EXCEPTION 'KITE: journal % does not balance (debit %, credit %)', v_journal, v_debit, v_credit
      USING ERRCODE = 'KT002';
  END IF;
  RETURN NULL;
END
$$;
CREATE CONSTRAINT TRIGGER journal_balanced AFTER INSERT ON acct.journal
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION acct.check_journal_balanced();
CREATE CONSTRAINT TRIGGER journal_line_balanced AFTER INSERT ON acct.journal_line
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION acct.check_journal_balanced();

-- A reversal must mirror its original exactly (same accounts, debits and credits swapped).
CREATE OR REPLACE FUNCTION acct.check_reversal_mirrors() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE v_diff integer;
BEGIN
  IF NEW.reversal_of_id IS NULL THEN RETURN NULL; END IF;
  SELECT count(*) INTO v_diff FROM (
    (SELECT account_id, debit AS d, credit AS c FROM acct.journal_line WHERE journal_id = NEW.reversal_of_id
     EXCEPT ALL
     SELECT account_id, credit, debit FROM acct.journal_line WHERE journal_id = NEW.id)
    UNION ALL
    (SELECT account_id, credit, debit FROM acct.journal_line WHERE journal_id = NEW.id
     EXCEPT ALL
     SELECT account_id, debit, credit FROM acct.journal_line WHERE journal_id = NEW.reversal_of_id)
  ) x;
  IF v_diff > 0 THEN
    RAISE EXCEPTION 'KITE: reversal journal % does not mirror journal %', NEW.id, NEW.reversal_of_id
      USING ERRCODE = 'KT002';
  END IF;
  RETURN NULL;
END
$$;
CREATE CONSTRAINT TRIGGER journal_reversal_mirrors AFTER INSERT ON acct.journal
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION acct.check_reversal_mirrors();
