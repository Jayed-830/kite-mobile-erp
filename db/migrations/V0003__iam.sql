-- V0003: identity and access (blueprint 18.2, 18.3).

CREATE TABLE iam.permission (
  code            text PRIMARY KEY CHECK (code ~ '^[A-Z][A-Z0-9_]*(\.[A-Z0-9_]+)+$'),
  module_code     text NOT NULL,
  description_en  text NOT NULL,
  description_ar  text NOT NULL
);

CREATE TABLE iam.role (
  id               uuid PRIMARY KEY,
  business_id      uuid NOT NULL REFERENCES core.business(id),
  code             text NOT NULL CHECK (code ~ '^[a-z][a-z0-9_]{1,40}$'),
  name_en          text NOT NULL,
  name_ar          text NOT NULL,
  is_system        boolean NOT NULL DEFAULT false,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid,
  updated_at       timestamptz NOT NULL DEFAULT now(),
  updated_by       uuid,
  version          integer NOT NULL DEFAULT 1,
  UNIQUE (business_id, code)
);
CREATE TRIGGER role_touch BEFORE UPDATE ON iam.role FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE iam.role_permission (
  role_id         uuid NOT NULL REFERENCES iam.role(id) ON DELETE CASCADE,
  permission_code text NOT NULL REFERENCES iam.permission(code),
  PRIMARY KEY (role_id, permission_code)
);

CREATE TABLE iam."user" (
  id                   uuid PRIMARY KEY,
  business_id          uuid NOT NULL REFERENCES core.business(id),
  username             text NOT NULL CHECK (username ~ '^[a-z0-9._-]{3,40}$'),
  display_name_en      text NOT NULL,
  display_name_ar      text,
  email                text,
  phone                text,
  password_hash        text NOT NULL,                 -- Argon2id, PHC string format
  password_changed_at  timestamptz NOT NULL DEFAULT now(),
  must_change_password boolean NOT NULL DEFAULT false,
  pin_hash             text,                          -- Argon2id; quick switch on trusted terminals only
  totp_secret_enc      bytea,                         -- AES-256-GCM, key protected outside the database
  totp_enabled         boolean NOT NULL DEFAULT false,
  status               text NOT NULL DEFAULT 'active' CHECK (status IN ('active','disabled')),
  failed_login_count   integer NOT NULL DEFAULT 0 CHECK (failed_login_count >= 0),
  locked_until         timestamptz,
  default_branch_id    uuid REFERENCES core.branch(id),
  preferred_language   text NOT NULL DEFAULT 'en' CHECK (preferred_language IN ('en','ar')),
  last_login_at        timestamptz,
  created_at           timestamptz NOT NULL DEFAULT now(),
  created_by           uuid,
  updated_at           timestamptz NOT NULL DEFAULT now(),
  updated_by           uuid,
  version              integer NOT NULL DEFAULT 1,
  origin_server_id     uuid,
  UNIQUE (business_id, username),
  CHECK (NOT totp_enabled OR totp_secret_enc IS NOT NULL)
);
CREATE TRIGGER user_touch BEFORE UPDATE ON iam."user" FOR EACH ROW EXECUTE FUNCTION core.touch_row();

-- Roles granted per branch. branch_id NULL means "all branches" (owner, head-office roles).
CREATE TABLE iam.user_branch_role (
  id          uuid PRIMARY KEY,
  user_id     uuid NOT NULL REFERENCES iam."user"(id) ON DELETE CASCADE,
  branch_id   uuid REFERENCES core.branch(id),
  role_id     uuid NOT NULL REFERENCES iam.role(id),
  created_at  timestamptz NOT NULL DEFAULT now(),
  created_by  uuid,
  CONSTRAINT user_branch_role_unique UNIQUE NULLS NOT DISTINCT (user_id, branch_id, role_id)
);

-- Opaque session tokens: only SHA-256 hashes are stored, so a database copy cannot be
-- used to sign in. Access tokens are short-lived; refresh tokens rotate on every use.
CREATE TABLE iam.session (
  id                   uuid PRIMARY KEY,
  user_id              uuid NOT NULL REFERENCES iam."user"(id) ON DELETE CASCADE,
  terminal_id          uuid REFERENCES core.terminal(id),
  access_hash          bytea NOT NULL UNIQUE CHECK (length(access_hash) = 32),
  refresh_hash         bytea NOT NULL UNIQUE CHECK (length(refresh_hash) = 32),
  access_expires_at    timestamptz NOT NULL,
  refresh_expires_at   timestamptz NOT NULL,
  mfa_satisfied        boolean NOT NULL DEFAULT false,
  auth_method          text NOT NULL CHECK (auth_method IN ('password','pin')),
  ip                   inet,
  user_agent           text,
  created_at           timestamptz NOT NULL DEFAULT now(),
  last_seen_at         timestamptz NOT NULL DEFAULT now(),
  revoked_at           timestamptz,
  revoked_reason       text,
  CHECK (refresh_expires_at >= access_expires_at)
);
CREATE INDEX session_user_active ON iam.session (user_id) WHERE revoked_at IS NULL;
