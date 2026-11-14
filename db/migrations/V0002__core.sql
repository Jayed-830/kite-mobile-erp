-- V0002: platform tables (blueprint 9.3 "Platform and identity").
-- Mutable master data carries created/updated/version/origin columns; ledgers come later.

CREATE TABLE core.business (
  id                     uuid PRIMARY KEY,
  legal_name_en          text NOT NULL CHECK (length(trim(legal_name_en)) > 0),
  legal_name_ar          text NOT NULL CHECK (length(trim(legal_name_ar)) > 0),
  trade_name_en          text,
  trade_name_ar          text,
  cr_number              text,
  vat_number             text,
  country_code           char(2) NOT NULL DEFAULT 'OM',
  base_currency          char(3) NOT NULL DEFAULT 'OMR',
  currency_decimals      smallint NOT NULL DEFAULT 3 CHECK (currency_decimals BETWEEN 0 AND 4),
  time_zone              text NOT NULL DEFAULT 'Asia/Muscat',
  fiscal_year_start_month smallint NOT NULL DEFAULT 1 CHECK (fiscal_year_start_month BETWEEN 1 AND 12),
  prices_include_tax     boolean NOT NULL DEFAULT true,
  address_en             text,
  address_ar             text,
  phone                  text,
  email                  text,
  created_at             timestamptz NOT NULL DEFAULT now(),
  created_by             uuid,
  updated_at             timestamptz NOT NULL DEFAULT now(),
  updated_by             uuid,
  version                integer NOT NULL DEFAULT 1,
  origin_server_id       uuid
);
-- One business per installation in the first release (blueprint 0.4); the business_id
-- column on every table keeps multi-company possible later without a redesign.
CREATE UNIQUE INDEX business_single_row ON core.business ((true));
CREATE TRIGGER business_touch BEFORE UPDATE ON core.business FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE core.branch (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  code             text NOT NULL CHECK (code ~ '^[A-Z0-9-]{2,12}$'),
  name_en          text NOT NULL,
  name_ar          text NOT NULL,
  address_en       text,
  address_ar       text,
  phone            text,
  is_head_office   boolean NOT NULL DEFAULT false,
  status           text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid,
  updated_at       timestamptz NOT NULL DEFAULT now(),
  updated_by       uuid,
  version          integer NOT NULL DEFAULT 1,
  origin_server_id uuid,
  UNIQUE (business_id, code)
);
CREATE UNIQUE INDEX branch_one_head_office ON core.branch (business_id) WHERE is_head_office;
CREATE TRIGGER branch_touch BEFORE UPDATE ON core.branch FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- Servers known to this database. Exactly one row is this installation (is_self).
CREATE TABLE core.server (
  id                uuid PRIMARY KEY,
  business_id       uuid REFERENCES core.business(id),
  branch_id         uuid REFERENCES core.branch(id),
  role              text NOT NULL CHECK (role IN ('combined','main','branch')),
  name              text NOT NULL,
  is_self           boolean NOT NULL DEFAULT false,
  cert_fingerprint  text UNIQUE,
  app_version       text,
  schema_version    text,
  status            text NOT NULL DEFAULT 'active' CHECK (status IN ('pending','active','revoked')),
  last_seen_at      timestamptz,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  version           integer NOT NULL DEFAULT 1,
  CHECK (role = 'main' OR branch_id IS NOT NULL OR is_self)
);
CREATE UNIQUE INDEX server_one_self ON core.server ((true)) WHERE is_self;
CREATE TRIGGER server_touch BEFORE UPDATE ON core.server FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE core.terminal (
  id                     uuid PRIMARY KEY,
  business_id            uuid NOT NULL REFERENCES core.business(id),
  branch_id              uuid NOT NULL REFERENCES core.branch(id),
  code                   text NOT NULL CHECK (code ~ '^[A-Z0-9-]{1,12}$'),
  name                   text NOT NULL,
  kind                   text NOT NULL CHECK (kind IN ('windows','browser')),
  device_key_fingerprint text UNIQUE,
  status                 text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','active','revoked')),
  emergency_enabled      boolean NOT NULL DEFAULT false,
  requested_at           timestamptz NOT NULL DEFAULT now(),
  approved_at            timestamptz,
  approved_by            uuid,
  created_at             timestamptz NOT NULL DEFAULT now(),
  created_by             uuid,
  updated_at             timestamptz NOT NULL DEFAULT now(),
  updated_by             uuid,
  version                integer NOT NULL DEFAULT 1,
  origin_server_id       uuid,
  UNIQUE (branch_id, code),
  CHECK (kind = 'windows' OR NOT emergency_enabled),           -- emergency mode is Windows-only (6.1)
  CHECK (status <> 'active' OR approved_at IS NOT NULL)
);
CREATE TRIGGER terminal_touch BEFORE UPDATE ON core.terminal FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE core.warehouse (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  branch_id        uuid NOT NULL REFERENCES core.branch(id),
  code             text NOT NULL CHECK (code ~ '^[A-Z0-9-]{1,12}$'),
  name_en          text NOT NULL,
  name_ar          text NOT NULL,
  kind             text NOT NULL CHECK (kind IN ('shop','backstore','repair_parts','transit')),
  status           text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid,
  updated_at       timestamptz NOT NULL DEFAULT now(),
  updated_by       uuid,
  version          integer NOT NULL DEFAULT 1,
  origin_server_id uuid,
  UNIQUE (branch_id, code)
);
CREATE TRIGGER warehouse_touch BEFORE UPDATE ON core.warehouse FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- Human document numbers. Branch series for normal documents; terminal series for
-- emergency mode (6.2). Gap-free per server because the row is locked inside the
-- document's own transaction (9.4 rule 7).
CREATE TABLE core.number_series (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  branch_id    uuid REFERENCES core.branch(id),
  terminal_id  uuid REFERENCES core.terminal(id),
  doc_type     text NOT NULL CHECK (doc_type ~ '^[a-z_]{2,40}$'),
  prefix       text NOT NULL,
  fiscal_year  integer NOT NULL CHECK (fiscal_year BETWEEN 2000 AND 2200),
  next_value   bigint NOT NULL DEFAULT 1 CHECK (next_value > 0),
  padding      smallint NOT NULL DEFAULT 6 CHECK (padding BETWEEN 1 AND 12),
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   uuid,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  version      integer NOT NULL DEFAULT 1,
  CONSTRAINT number_series_scope_unique UNIQUE NULLS NOT DISTINCT (business_id, branch_id, terminal_id, doc_type, fiscal_year)
);
CREATE TRIGGER number_series_touch BEFORE UPDATE ON core.number_series FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- Returns the next number, e.g. 'INV-B01-2026-000123'. Must be called inside the
-- transaction that creates the document; a rollback returns the number.
CREATE OR REPLACE FUNCTION core.next_document_number(
  p_business_id uuid, p_branch_id uuid, p_terminal_id uuid, p_doc_type text, p_fiscal_year integer)
RETURNS text
LANGUAGE plpgsql AS $$
DECLARE
  r core.number_series%ROWTYPE;
BEGIN
  SELECT * INTO r FROM core.number_series
   WHERE business_id = p_business_id
     AND branch_id IS NOT DISTINCT FROM p_branch_id
     AND terminal_id IS NOT DISTINCT FROM p_terminal_id
     AND doc_type = p_doc_type
     AND fiscal_year = p_fiscal_year
   FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'KITE: no number series for doc type % in year %', p_doc_type, p_fiscal_year
      USING ERRCODE = 'P0002';
  END IF;
  UPDATE core.number_series SET next_value = r.next_value + 1 WHERE id = r.id;
  RETURN r.prefix || lpad(r.next_value::text, r.padding, '0');
END
$$;

-- Generic scoped settings (business, branch or terminal), effective-dated.
CREATE TABLE core.setting (
  id             uuid PRIMARY KEY,
  business_id    uuid NOT NULL REFERENCES core.business(id),
  scope          text NOT NULL CHECK (scope IN ('business','branch','terminal')),
  scope_id       uuid NOT NULL,
  key            text NOT NULL CHECK (key ~ '^[a-z0-9_.]{2,80}$'),
  value          jsonb NOT NULL,
  effective_from timestamptz NOT NULL DEFAULT now(),
  created_at     timestamptz NOT NULL DEFAULT now(),
  created_by     uuid,
  UNIQUE (scope, scope_id, key, effective_from)
);

-- Module switches (blueprint 2.4). A branch row overrides the business row.
CREATE TABLE core.module_setting (
  id           uuid PRIMARY KEY,
  business_id  uuid NOT NULL REFERENCES core.business(id),
  branch_id    uuid REFERENCES core.branch(id),
  module_code  text NOT NULL CHECK (module_code ~ '^[a-z_]{2,30}$'),
  enabled      boolean NOT NULL,
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   uuid,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid,
  version      integer NOT NULL DEFAULT 1,
  CONSTRAINT module_setting_scope_unique UNIQUE NULLS NOT DISTINCT (business_id, branch_id, module_code)
);
CREATE TRIGGER module_setting_touch BEFORE UPDATE ON core.module_setting FOR EACH ROW EXECUTE FUNCTION core.touch_row();
