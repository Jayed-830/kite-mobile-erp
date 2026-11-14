-- KITE MOBILE ERP - V0011: Phase 3 foundation (blueprint 3, 4, 18.1, 19, 24.5).
--   pki   business certificate authority, issued certificates, revocation lists
--   core  branch registration tokens, server identity and heartbeat columns
--   lic   the Nextdab-signed licence, Nextdab control state, received commands and messages
--   ops   backup and verification runs

DO $$
DECLARE s text;
BEGIN
  FOREACH s IN ARRAY ARRAY['pki','lic','ops'] LOOP
    EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I', s);
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO kite_app, kite_ro', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO kite_app', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT ON TABLES TO kite_ro', s);
  END LOOP;
END
$$;

-- ---------------------------------------------------------------- server identity
-- installation_id: the Machine ID Nextdab sees (16 hex in 4 groups), made at setup.
-- sync_url: on a branch server, the head office address it registered with.
ALTER TABLE core.server
  ADD COLUMN installation_id text UNIQUE CHECK (installation_id ~ '^[0-9A-F]{4}(-[0-9A-F]{4}){3}$'),
  ADD COLUMN sync_url        text,
  ADD COLUMN heartbeat       jsonb,
  ADD COLUMN last_ip         text;

-- ---------------------------------------------------------------- PKI
CREATE TABLE pki.authority (
  id                 uuid PRIMARY KEY,
  business_id        uuid NOT NULL REFERENCES core.business(id),
  subject            text NOT NULL,
  cert_pem           text NOT NULL,
  fingerprint_sha256 text NOT NULL UNIQUE CHECK (fingerprint_sha256 ~ '^[0-9a-f]{64}$'),
  not_before         timestamptz NOT NULL,
  not_after          timestamptz NOT NULL,
  key_file           text,                                  -- protected private key in the data folder; NULL on branch servers
  status             text NOT NULL DEFAULT 'active' CHECK (status IN ('active','retired')),
  created_at         timestamptz NOT NULL DEFAULT now(),
  created_by         uuid,
  CHECK (not_after > not_before)
);
CREATE UNIQUE INDEX authority_one_active ON pki.authority (business_id) WHERE status = 'active';

CREATE TABLE pki.certificate (
  serial             text PRIMARY KEY CHECK (serial ~ '^[0-9a-f]{16,64}$'),
  authority_id       uuid NOT NULL REFERENCES pki.authority(id),
  kind               text NOT NULL CHECK (kind IN ('server_tls','branch_server')),
  server_id          uuid REFERENCES core.server(id),
  branch_id          uuid REFERENCES core.branch(id),
  subject            text NOT NULL,
  cert_pem           text NOT NULL,
  fingerprint_sha256 text NOT NULL UNIQUE CHECK (fingerprint_sha256 ~ '^[0-9a-f]{64}$'),
  not_before         timestamptz NOT NULL,
  not_after          timestamptz NOT NULL,
  issued_at          timestamptz NOT NULL DEFAULT now(),
  issued_by          uuid,
  revoked_at         timestamptz,
  revoked_reason     text CHECK (revoked_reason IN ('branch_deactivated','server_replaced','key_compromise','superseded')),
  revoked_by         uuid,
  CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL)),
  CHECK (kind <> 'branch_server' OR (branch_id IS NOT NULL AND server_id IS NOT NULL))
);
CREATE INDEX certificate_server ON pki.certificate (server_id);

-- Certificates are permanent records: only an unrevoked certificate may be revoked, once.
CREATE OR REPLACE FUNCTION pki.certificate_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'certificates are never deleted' USING ERRCODE = 'KT006';
  END IF;
  IF OLD.revoked_at IS NOT NULL THEN
    RAISE EXCEPTION 'certificate % is revoked; a revocation is permanent', OLD.serial USING ERRCODE = 'KT006';
  END IF;
  IF (NEW.serial, NEW.authority_id, NEW.kind, NEW.server_id, NEW.branch_id, NEW.subject, NEW.cert_pem,
      NEW.fingerprint_sha256, NEW.not_before, NEW.not_after, NEW.issued_at, NEW.issued_by)
     IS DISTINCT FROM
     (OLD.serial, OLD.authority_id, OLD.kind, OLD.server_id, OLD.branch_id, OLD.subject, OLD.cert_pem,
      OLD.fingerprint_sha256, OLD.not_before, OLD.not_after, OLD.issued_at, OLD.issued_by) THEN
    RAISE EXCEPTION 'an issued certificate cannot change' USING ERRCODE = 'KT006';
  END IF;
  RETURN NEW;
END
$$;
CREATE TRIGGER certificate_guard BEFORE UPDATE OR DELETE ON pki.certificate
  FOR EACH ROW EXECUTE FUNCTION pki.certificate_guard();

CREATE TABLE pki.crl (
  number        bigint PRIMARY KEY CHECK (number > 0),
  authority_id  uuid NOT NULL REFERENCES pki.authority(id),
  issued_at     timestamptz NOT NULL,
  next_update   timestamptz NOT NULL,
  revoked_count integer NOT NULL CHECK (revoked_count >= 0),
  crl_pem       text NOT NULL
);

-- ---------------------------------------------------------------- branch registration
CREATE TABLE core.registration_token (
  id                uuid PRIMARY KEY,
  business_id       uuid NOT NULL REFERENCES core.business(id),
  branch_id         uuid NOT NULL REFERENCES core.branch(id),
  token_hash        text NOT NULL UNIQUE CHECK (token_hash ~ '^[0-9a-f]{64}$'),
  expires_at        timestamptz NOT NULL,
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        uuid,
  used_at           timestamptz,
  used_by_server_id uuid REFERENCES core.server(id),
  cancelled_at      timestamptz,
  cancelled_by      uuid,
  CHECK (expires_at > created_at AND expires_at <= created_at + interval '24 hours'),
  CHECK ((used_at IS NULL) = (used_by_server_id IS NULL)),
  CHECK (used_at IS NULL OR cancelled_at IS NULL)
);
CREATE INDEX registration_token_branch ON core.registration_token (branch_id);

-- ---------------------------------------------------------------- licensing (blueprint 24.5)
CREATE TABLE lic.licence (
  id                text PRIMARY KEY CHECK (id ~ '^[A-Za-z0-9._-]{6,80}$'),
  installation_id   text NOT NULL,
  kind              text NOT NULL CHECK (kind IN ('trial','subscription')),
  customer          text NOT NULL,
  valid_until       date NOT NULL,
  grace_days        integer NOT NULL CHECK (grace_days BETWEEN 0 AND 120),
  branches_allowed  integer NOT NULL CHECK (branches_allowed BETWEEN 1 AND 500),
  terminals_allowed integer NOT NULL CHECK (terminals_allowed BETWEEN 1 AND 5000),
  modules           text[] NOT NULL,
  issued_at         timestamptz NOT NULL,
  file_text         text NOT NULL,
  source            text NOT NULL CHECK (source IN ('import','nextdab','head_office')),
  imported_at       timestamptz NOT NULL DEFAULT now(),
  imported_by       uuid,
  is_current        boolean NOT NULL DEFAULT false
);
CREATE UNIQUE INDEX licence_one_current ON lic.licence ((true)) WHERE is_current;

-- What Nextdab Admin last told this installation (only main/combined servers talk to Nextdab;
-- branch servers receive the same signed licence and control commands from head office).
CREATE TABLE lic.control_state (
  id                  boolean PRIMARY KEY DEFAULT true CHECK (id),
  checkin_key         text NOT NULL CHECK (length(checkin_key) BETWEEN 20 AND 200),
  suspended           boolean NOT NULL DEFAULT false,
  suspend_message     text,
  suspend_message_ar  text,
  suspend_seq         bigint NOT NULL DEFAULT 0,
  suspend_cmd         jsonb,
  modules_off         text[] NOT NULL DEFAULT '{}',
  modules_seq         bigint NOT NULL DEFAULT 0,
  modules_cmd         jsonb,
  last_checkin_at     timestamptz,
  last_checkin_ok_at  timestamptz,
  last_error          text,
  next_checkin_at     timestamptz,
  updated_at          timestamptz NOT NULL DEFAULT now()
);

-- Every signed command looked at, once (protocol: "carry out each one at most once").
CREATE TABLE lic.command (
  id          text PRIMARY KEY CHECK (length(id) BETWEEN 3 AND 80),
  type        text NOT NULL,
  seq         bigint,
  issued_at   timestamptz,
  received_at timestamptz NOT NULL DEFAULT now(),
  body        jsonb NOT NULL,
  ok          boolean NOT NULL,
  msg         text,
  acked_at    timestamptz
);

CREATE TABLE lic.message (
  id          text PRIMARY KEY REFERENCES lic.command(id),
  title       text,
  text        text,
  title_ar    text,
  text_ar     text,
  level       text NOT NULL CHECK (level IN ('info','important')),
  audience    text NOT NULL CHECK (audience IN ('all','admin')),
  until       date,
  received_at timestamptz NOT NULL DEFAULT now(),
  cleared_at  timestamptz
);

-- ---------------------------------------------------------------- backups (blueprint 19)
CREATE TABLE ops.backup_run (
  id            uuid PRIMARY KEY,
  kind          text NOT NULL CHECK (kind IN ('logical','base','verify','offsite')),
  status        text NOT NULL CHECK (status IN ('running','succeeded','failed')),
  trigger       text NOT NULL CHECK (trigger IN ('schedule','manual')),
  started_at    timestamptz NOT NULL,
  finished_at   timestamptz,
  file_name     text,
  size_bytes    bigint CHECK (size_bytes >= 0),
  sha256        text CHECK (sha256 ~ '^[0-9a-f]{64}$'),
  source_run_id uuid REFERENCES ops.backup_run(id),
  checks        jsonb,
  error         text,
  triggered_by  uuid,
  CHECK (status = 'running' OR finished_at IS NOT NULL),
  CHECK (finished_at IS NULL OR finished_at >= started_at)
);
CREATE INDEX backup_run_recent ON ops.backup_run (kind, started_at DESC);

-- ---------------------------------------------------------------- grants
GRANT USAGE ON SCHEMA pki, lic, ops TO kite_backup;
REVOKE UPDATE, DELETE, TRUNCATE ON pki.certificate, pki.crl, pki.authority,
  core.registration_token, lic.licence, lic.command, lic.message, ops.backup_run FROM kite_app;
GRANT UPDATE (revoked_at, revoked_reason, revoked_by) ON pki.certificate TO kite_app;
GRANT UPDATE (status) ON pki.authority TO kite_app;
GRANT UPDATE (used_at, used_by_server_id, cancelled_at, cancelled_by) ON core.registration_token TO kite_app;
GRANT UPDATE (is_current) ON lic.licence TO kite_app;
GRANT UPDATE (acked_at) ON lic.command TO kite_app;
GRANT UPDATE (cleared_at) ON lic.message TO kite_app;
GRANT UPDATE (status, finished_at, file_name, size_bytes, sha256, checks, error) ON ops.backup_run TO kite_app;
REVOKE DELETE, TRUNCATE ON lic.control_state FROM kite_app;

-- Registration tokens are branch-owned: the same row-level rules as other branch tables.
ALTER TABLE core.registration_token ENABLE ROW LEVEL SECURITY;
CREATE POLICY branch_read   ON core.registration_token FOR SELECT USING (core.branch_visible(branch_id));
CREATE POLICY branch_insert ON core.registration_token FOR INSERT WITH CHECK (core.branch_writable(branch_id));
CREATE POLICY branch_update ON core.registration_token FOR UPDATE USING (core.branch_writable(branch_id)) WITH CHECK (core.branch_writable(branch_id));

-- The licence limits terminals for the whole business; a branch manager's own scope cannot see the
-- other branches' terminals, so the count is taken by a definer function.
CREATE FUNCTION core.active_terminal_count() RETURNS bigint
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, core AS $$
  SELECT count(*) FROM core.terminal WHERE status = 'active'
$$;
REVOKE EXECUTE ON FUNCTION core.active_terminal_count() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.active_terminal_count() TO kite_app;

-- ---------------------------------------------------------------- permissions
INSERT INTO iam.permission (code, module_code, description_en, description_ar) VALUES
  ('SERVERS.MANAGE',  'core', 'Register, replace and revoke branch servers',      'تسجيل خوادم الفروع واستبدالها وإلغاؤها'),
  ('LICENCE.MANAGE',  'core', 'Import the licence and see the Nextdab connection', 'استيراد الترخيص وعرض الاتصال بنكستداب'),
  ('BACKUP.VIEW',     'core', 'See backup and verification status',               'عرض حالة النسخ الاحتياطي والتحقق'),
  ('BACKUP.MANAGE',   'core', 'Run backups, change backup settings, see the recovery key', 'تشغيل النسخ الاحتياطي وتغيير إعداداته وعرض مفتاح الاسترداد');

-- Existing owner roles hold every permission, including these new ones.
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, p.code
  FROM iam.role r
 CROSS JOIN (VALUES ('SERVERS.MANAGE'), ('LICENCE.MANAGE'), ('BACKUP.VIEW'), ('BACKUP.MANAGE')) AS p(code)
 WHERE r.code = 'owner'
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.id, 'BACKUP.VIEW' FROM iam.role r WHERE r.code = 'branch_manager'
ON CONFLICT DO NOTHING;
