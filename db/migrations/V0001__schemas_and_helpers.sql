-- V0001: schemas, default privileges and shared helper functions.
-- Runs as kite_migrator. Forward-only (blueprint 9.6).
--
-- KITE SQLSTATE codes (class KT):
--   KT001  ledger row is immutable (UPDATE/DELETE attempted)
--   KT002  journal does not balance or has fewer than two lines
--   KT003  posting date is not in an open fiscal period
--   KT004  attempt to change protected outbox columns
--   KT005  row-level branch scope violation on write

CREATE SCHEMA IF NOT EXISTS core;
CREATE SCHEMA IF NOT EXISTS iam;
CREATE SCHEMA IF NOT EXISTS tax;
CREATE SCHEMA IF NOT EXISTS acct;
CREATE SCHEMA IF NOT EXISTS audit;
CREATE SCHEMA IF NOT EXISTS sync;

DO $$
DECLARE s text;
BEGIN
  FOREACH s IN ARRAY ARRAY['core','iam','tax','acct','audit','sync'] LOOP
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO kite_app, kite_ro', s);
    -- Every table created later by kite_migrator in these schemas:
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO kite_app', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT ON TABLES TO kite_ro', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT USAGE, SELECT ON SEQUENCES TO kite_app', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT EXECUTE ON FUNCTIONS TO kite_app', s);
  END LOOP;
END
$$;

-- Rejects UPDATE and DELETE on append-only ledger tables. This is the second barrier;
-- the first is that kite_app has no UPDATE/DELETE grant on those tables.
CREATE OR REPLACE FUNCTION core.forbid_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'KITE: % on %.% is not allowed; ledger rows are immutable, post a reversal instead',
    TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME
    USING ERRCODE = 'KT001';
END
$$;

-- Keeps updated_at and version current on mutable master-data tables.
CREATE OR REPLACE FUNCTION core.touch_row() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  NEW.updated_at := now();
  NEW.version := OLD.version + 1;
  RETURN NEW;
END
$$;

-- Branch scope for row-level security. The application sets, per transaction:
--   SET LOCAL kite.all_branches = 'on'          (owner / system jobs), or
--   SET LOCAL kite.branch_ids   = '{uuid,uuid}'  (branches the user may access).
-- With neither set, no branch-owned row is visible: the safe default.
CREATE OR REPLACE FUNCTION core.branch_visible(p_branch_id uuid) RETURNS boolean
LANGUAGE sql STABLE AS $$
  SELECT CASE
    WHEN coalesce(current_setting('kite.all_branches', true), '') = 'on' THEN true
    WHEN p_branch_id IS NULL THEN true
    WHEN nullif(current_setting('kite.branch_ids', true), '') IS NULL THEN false
    ELSE p_branch_id = ANY (current_setting('kite.branch_ids', true)::uuid[])
  END
$$;

-- Writes are stricter than reads: a row with no branch (business-wide) can be written
-- only in all-branches context.
CREATE OR REPLACE FUNCTION core.branch_writable(p_branch_id uuid) RETURNS boolean
LANGUAGE sql STABLE AS $$
  SELECT CASE
    WHEN coalesce(current_setting('kite.all_branches', true), '') = 'on' THEN true
    WHEN p_branch_id IS NULL THEN false
    WHEN nullif(current_setting('kite.branch_ids', true), '') IS NULL THEN false
    ELSE p_branch_id = ANY (current_setting('kite.branch_ids', true)::uuid[])
  END
$$;
