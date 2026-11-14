-- KITE MOBILE ERP - database bootstrap
-- Run ONCE per server by a PostgreSQL superuser, connected to the KITE database,
-- before any migration. The installer (Phase 14) runs this; CI and tests run it too.
--
-- Roles (blueprint section 18.5):
--   kite_migrator  owns every schema and table; used only for migrations and upgrades
--   kite_app       the application; DML only; no UPDATE/DELETE on ledgers; subject to RLS
--   kite_ro        read-only reporting
--   kite_backup    backups (pg_dump / base backup)
-- Passwords are set by the installer with ALTER ROLE ... PASSWORD; never stored in files.

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kite_migrator') THEN
    CREATE ROLE kite_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kite_app') THEN
    CREATE ROLE kite_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kite_ro') THEN
    CREATE ROLE kite_ro LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kite_backup') THEN
    CREATE ROLE kite_backup LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
  END IF;
END
$$;

-- pg_read_all_data exists from PostgreSQL 14 and lets the backup role dump everything.
GRANT pg_read_all_data TO kite_backup;

-- Extensions need elevated rights, so they are created here rather than in migrations.
CREATE EXTENSION IF NOT EXISTS btree_gist;  -- exclusion constraints on effective-dated rows
CREATE EXTENSION IF NOT EXISTS pg_trgm;     -- fuzzy search on EN/AR names (from Phase 4)

-- The migrator creates schemas and objects inside this database.
DO $$
BEGIN
  EXECUTE format('GRANT CONNECT, CREATE, TEMPORARY ON DATABASE %I TO kite_migrator', current_database());
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO kite_app, kite_ro, kite_backup', current_database());
END
$$;

-- Nobody but the migrator may create objects in public.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
