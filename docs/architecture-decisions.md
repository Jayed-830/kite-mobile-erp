# Architecture decision records

Each record states the decision, why, and what it costs. Records 1 to 7 implement the Phase 1 blueprint (section 1.1). Records 8 to 12 are Phase 2 choices, including where the code departs from the blueprint and why.

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
**Known limit.** Permissions are the union of a user's roles across branches. A user with different roles in different branches gets per-branch evaluation in Phase 3, when requests carry a branch context. The Windows DPAPI wrapping of the key file is done by the Phase 3 installer.

## ADR 11: Plain CSS design tokens instead of Tailwind and Radix in Phase 2 (departure from blueprint 20.1)

**Decision.** The design system uses CSS custom properties and logical properties (`margin-inline-start`, `inset-inline-start`), native `<dialog>`, and no web fonts or CDNs.
**Why.** Full RTL mirroring and light/dark themes with no build plugins; the app must load on a shop LAN with no internet. Headless primitives can be added when complex widgets (comboboxes, date pickers) arrive in Phase 4.

## ADR 12: Build and test on GitHub Actions

**Decision.** Every push builds and tests on GitHub's machines: SQL tests on PostgreSQL 16 and 17, server on Linux (with integration tests against PostgreSQL 17) and Windows, web app, desktop shell, and the proofs of concept on Linux and Windows.
**Why.** The workspace where the code is written cannot download NuGet packages, and the owner's PCs should not be the only test machines.
