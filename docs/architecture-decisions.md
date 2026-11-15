# Architecture decision records

Each record states the decision, why, and what it costs. Records 1 to 7 implement the Phase 1 blueprint (section 1.1). Records 8 to 12 are Phase 2 choices, including where the code departs from the blueprint and why. Records 13 to 19 are Phase 3 choices.

## ADR 1: One server code base, three roles

**Decision.** One ASP.NET Core build runs as `combined` (single shop), `main` (head office) or `branch`. The role is stored in `core.server` (the row with `is_self`).
**Why.** A single-branch shop grows into multi-branch by adding branch servers, with no data migration (blueprint 3.4).
**Cost.** Role-specific behaviour must be switched at runtime; covered from Phase 3.

## ADR 2: Branch is the system of record for its operations

**Decision.** Every operational record carries `business_id`, owning `branch_id` and origin columns; only the owner server changes it.
**Why.** Branches must trade with no head office (blueprint 0.2 rule 3).

## ADR 3: Ledgers are append-only, enforced by the database

**Decision.** `kite_app` has no UPDATE/DELETE grant on `acct.journal`, `acct.journal_line` and `audit.audit_log`; triggers reject UPDATE, DELETE and TRUNCATE for every role, the owner included. Journals balance at commit (deferred constraint trigger), cannot post twice (unique key), cannot post into closed periods, and a reversal must mirror its original.
**Why.** Financial history cannot be rewritten by a bug or a direct database edit (blueprint 9.4).
**Evidence.** `db/tests/01`–`04`.

## ADR 4: Transactional outbox with a gap-free sequence

**Decision.** `sync.outbox` is written in the same transaction as the business change. `origin_seq` is assigned in a trigger under an advisory lock, so a rolled-back event leaves no gap; the payload and its SHA-256 never change; an unacknowledged event cannot be deleted.
**Why.** The sync protocol (blueprint 7) relies on contiguous sequences and immutable events.
**Evidence.** `db/tests/07_outbox.sql`, integration test "every write produced a sync event".

## ADR 5: UUIDv7 identifiers and a hybrid logical clock

**Decision.** IDs from `Guid.CreateVersion7()`; descriptive-field ordering by a hybrid logical clock (`HlcTimestamp`).
**Why.** IDs are created offline without collision; field-level last-writer-wins stays deterministic despite clock drift (blueprint 8.1).

## ADR 6: Exact money

**Decision.** `numeric(18,3)` in PostgreSQL, `decimal` in C#, decimal strings ("12.500") in the API. A unit test fails the build if `float` or `double` appears in server code.
**Why.** Blueprint 1.1 decision 7.

## ADR 7: Row-level security by branch

**Decision.** Each transaction sets `kite.all_branches` or `kite.branch_ids`; policies on branch-owned tables allow reads and writes only inside that scope, and the default with no scope is "see nothing". Audit appends are always allowed; audit reads are scoped.
**Why.** A branch user cannot read another branch's restricted records even if application code has a bug (blueprint 18.3).
**Evidence.** `db/tests/08_rls.sql`.

## ADR 8: SQL-first migrations run by the server (departure from blueprint 9.6)

**Decision.** Migrations are plain SQL files in `db/migrations`, embedded into `Kite.Persistence` and applied by the server at startup as `kite_migrator`, with checksums recorded in `meta.schema_info`. An applied migration whose text changed is refused.
**Why.** Ledger rules, grants, triggers and RLS are SQL anyway; the same files are tested directly by `db/tests/run.sh` on PostgreSQL 16 and 17. EF Core migrations would have wrapped raw SQL with little benefit.
**Cost.** No model-diff generation; schema changes are written by hand and reviewed.

## ADR 9: Npgsql + Dapper for data access in Phase 2 (departure from blueprint 20.1)

**Decision.** Phase 2 uses Npgsql with Dapper and explicit SQL. EF Core can be introduced per module later if it pays for itself.
**Why.** Every Phase 2 write touches RLS settings, audit and outbox in one transaction; explicit SQL keeps that visible and fast.

## ADR 10: Opaque session tokens instead of OpenIddict (departure from blueprint 20.1)

**Decision.** Sign-in issues a random access token (15 minutes) and a rotating refresh token (12 hours, httpOnly cookie). Only SHA-256 hashes are stored in `iam.session`, so sessions can be revoked instantly (logout, password change, user disabled, terminal revoked). Passwords and PINs use Argon2id; TOTP follows RFC 6238; TOTP secrets are AES-256-GCM encrypted with a key file in the server's data folder.
**Why.** No external identity server is needed offline, and revocation is immediate. OpenIddict can be added if third-party OAuth clients are ever needed.
**Known limit (closed in Phase 3).** Permissions were the union of a user's roles across branches; ADR 15 makes them per branch. The key files are wrapped with DPAPI from Phase 3 (ADR 14).

## ADR 11: Plain CSS design tokens instead of Tailwind and Radix in Phase 2 (departure from blueprint 20.1)

**Decision.** The design system uses CSS custom properties and logical properties (`margin-inline-start`, `inset-inline-start`), native `<dialog>`, and no web fonts or CDNs.
**Why.** Full RTL mirroring and light/dark themes with no build plugins; the app must load on a shop LAN with no internet. Headless primitives can be added when complex widgets (comboboxes, date pickers) arrive in Phase 4.

## ADR 12: Build and test on GitHub Actions

**Decision.** Every push builds and tests on GitHub's machines: SQL tests on PostgreSQL 16 and 17, server on Linux (with integration tests against PostgreSQL 17) and Windows, web app, desktop shell, and the proofs of concept on Linux and Windows.
**Why.** The workspace where the code is written cannot download NuGet packages, and the owner's PCs should not be the only test machines.

## ADR 13: A business certificate authority and mutual TLS on a separate sync port

**Decision.** At setup the main (or combined) server creates the business CA: ECDSA P-256, 20 years, made with .NET's own `CertificateRequest`, no external PKI. Branch servers get two-year certificates whose subject KITE writes itself (`CN=kite-server:{id}`, `OU=kite-branch:{id}`, `O=kite-business:{id}`), whatever the request asked for. Branch traffic (registration, heartbeat) uses its own TLS port (default 5443) with client certificates; the web and API port stays plain HTTP on the shop LAN behind the Windows firewall until the installer adds HTTPS (Phase 14). Every request on the sync port is checked against the CA, the certificate's database record and its revocation, not only the TLS handshake; a revoked certificate (`pki.certificate`, permanent by trigger) fails at the next heartbeat, and the CRL is sent to branches in the heartbeat answer. Joining uses a one-time token (24 hours, stored as SHA-256 only) and a fingerprint code of the CA (20 digits from its SHA-256, in five groups of four), shown on both screens so the person joining compares them; the branch pins that fingerprint.
**Why.** Branches must trust head office and each other offline, with no public CA and no internet (blueprint 18.1, 18.4). A separate port keeps client-certificate handling away from browsers.
**Cost.** The CA private key is the business's root of trust: it is DPAPI-protected on the main server, and a passphrase-encrypted recovery copy can be exported (password asked again, audited). Losing both means re-registering every branch.

## ADR 14: Key files protected with Windows DPAPI in machine scope

**Decision.** Key files in the server's data folder (`secrets.key` for TOTP secrets, `business-ca.key`, `sync-tls.key`, `branch-client.key`, `backup.key`) are wrapped with DPAPI `LocalMachine` scope plus a per-purpose entropy on Windows. On Linux and macOS (development and CI) they are written with owner-only permissions and the Backups page says "File permissions" so this is never mistaken for production protection. Phase 2 raw key files, and files restored from an unprotected store, are wrapped on first read.
**Why.** A copied data folder (or a stolen backup of it) is useless on another PC; this closes the Phase 2 known limit without a separate secrets service.
**Cost.** Moving a server to a new PC needs the recovery copies (CA) and the printed backup recovery key, by design.

## ADR 15: Permissions evaluated per branch

**Decision.** Sign-in issues two kinds of permission claims: `kite:perm` for roles granted business-wide (branch empty) and `kite:bperm` = `{branch}|{permission}` for roles granted for one branch. `Has` checks business-wide only; `HasIn(permission, branch)` and `Demand(permission, branch)` check business-wide or that branch. Endpoints that act on one branch (registration tokens, revoking its server) demand the permission in that branch.
**Why.** A manager of Sohar must not manage Muscat (blueprint 18.3).
**Cost.** Each branch-scoped endpoint must name its branch; an endpoint that forgets falls back to business-wide permissions, which is the safe direction.

## ADR 16: The KITE MOBILE ERP licence key is made inside Nextdab Admin

**Decision.** Nextdab Admin 5.14 creates the KITE MOBILE ERP Ed25519 key pair on the Nextdab PC the first time the owner signs in, seals the private key with the owner password (as for Kite RMS and Kite REMS) and shows only the public key. That public key is compiled into KITE (`LicenceKeys.EmbeddedHex`). Until it is filled in, no licence can be imported (the Licence page says so); a Development build may use a test key from configuration (`Kite:Licensing:DevPublicKey`), which is how CI runs.
**Why.** The private key never exists anywhere but the Nextdab PC: not in this repository, not in a chat, not in CI. Licences and commands use NEXTDAB PROTOCOL 1 (canonical JSON, Ed25519 over `NEXTDAB1|`), the same as Kite REMS, proven by two test vectors: one re-signed byte for byte, one made by Nextdab Admin 5.14 itself.
**Cost.** One manual step: the owner sends the public key once after installing Nextdab Admin 5.14, and a KITE build with it is made.

## ADR 17: Check-in at /v1/kme/checkin, and what an unlicensed server may do

**Decision.** Only the main or combined server checks in, at `https://control.nextdab.com/v1/kme/checkin` (the same address and tunnel as Kite RMS and Kite REMS, a new path so the existing check-ins are untouched). Branch servers never call Nextdab; they receive the licence, switch-off and modules state from head office in the heartbeat. A server with no licence allows 1 branch and 1 terminal and no new transactions, so setup and configuration can be done before the licence arrives. After the licence's end date it warns (from 14 days before), then a grace period (default 30 days) keeps trading; after that new transactions stop while sign-in, data, reports, backups and sync keep working. Switch-off by Nextdab stops everything except sign-in, licence, backups, setup, servers, health and sync.
**Why.** Blueprint 24.5; the shop must never lose its data or backups because of a licence.
**Cost.** Licence enforcement is checked on every guarded action against a 30-second cached status.

## ADR 18: The API client stays hand-written until Phase 4

**Decision.** The typed API client (`@kite/api`) is still written by hand for the Phase 3 endpoints; generating it from the server's OpenAPI document moves to Phase 4.
**Why.** Phase 3 added 18 endpoints; minimal-API OpenAPI generation needs response types on every endpoint to be useful, and Phase 4 starts the business modules (the Phase 2 report lists stock, sales and repairs), which grows the API several times over. Doing it once then, with a CI check that the generated client is current, costs less than doing it twice.
**Cost.** Until then a server change can drift from the client unnoticed by the compiler; the web tests and the end-to-end runs against real servers catch it.

## ADR 19: Backups with the PostgreSQL tools, encrypted, and proven by restoring them

**Decision.** The backup agent runs inside the server: nightly `pg_dump -Fc` (logical) and `pg_basebackup` checked with `pg_verifybackup` (base, for point-in-time recovery with WAL archiving), each encrypted with AES-256-GCM in 1 MiB chunks (`KITEBAK1` format, the last chunk flagged so truncation is detected) under a backup key whose printed recovery key is shown once after the password. Retention: daily 30 days, weekly 12, monthly 12, yearly 10 years; base backups 7 days. Every week the newest backup is restored into a scratch database and checked: journals balanced, outbox sequence gap-free, audit chain intact. `Kite.Host backup-decrypt` opens a backup on any PC with the recovery key.
**Why.** A backup that has never been restored is a hope, not a backup (blueprint 19).
**Cost.** The `kite_backup` role needs BYPASSRLS, REPLICATION and CREATEDB; it has no write grant on KITE's tables.

