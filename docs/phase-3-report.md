# Phase 3 report: main server and branch server foundation

KITE MOBILE ERP · Nextdab · version 0.3.0 · November 2026

## Summary

Phase 3 makes KITE a multi-server system. A head office server now runs its own certificate authority for the business. Branch servers register with a one-time token and send heartbeats over mutual TLS, and head office can revoke them. KITE checks in with Nextdab Admin and enforces a signed licence, offline as well. A backup agent takes encrypted backups and proves them by restoring them.

Nextdab Admin 5.14 was built for this. It adds KITE MOBILE ERP as a third product next to Kite RMS and Kite REMS, with its own licence key made on the Nextdab PC. It is in the shared copy on `F:\nextdab-admin`.

Every exit-gate item passed on GitHub Actions against real servers: two KITE servers, two PostgreSQL clusters and a real browser. CI run `43657742616` on commit `fa2b332` is green in all ten jobs.

## Exit gate (blueprint section 25, row 3)

| Requirement | Status | Evidence |
| --- | --- | --- |
| Branch registers with token | Done | End-to-end job: head office makes a token. The branch server joins from its own setup screen in Arabic after the security codes match, and the owner signs in on the branch with the account copied from head office. Head office then shows the branch server online from its heartbeats. Integration test `A_branch_server_registers_with_a_one_time_token_heartbeats_over_mutual_TLS_and_can_be_revoked`. |
| Certificates and revocation tested | Done | End-to-end: head office revokes the branch server (reason "key compromise"), and the branch shows "Head office revoked this server" at its next heartbeat. The database shows the certificate revoked for good. Integration tests check refusal with no certificate, with a certificate from another CA, and after revocation or branch deactivation. SQL test `10_phase3.sql` shows a revocation cannot be undone. |
| Backups verified by restore | Done | End-to-end: a full backup, a base backup (`pg_basebackup` + `pg_verifybackup`) and a restore check are run from the Backups page. The restore check restores into a scratch database and finds journals balanced, the outbox gap-free and the audit chain intact. The workflow then decrypts the newest backup with the recovery key shown in the browser and restores it into a clean database with `pg_restore`: audit chain OK, 19 audit rows, 2 branches, 2 licences. A wrong recovery key is refused. |
| A licence issued in Nextdab Admin activates and is enforced offline | Done | Nextdab Admin 5.14 was run locally with a scratch key. It made a licence file and check-in commands, and a KITE unit test verifies and reads them (`tests/vectors/nextdab-admin-5.14-vector.json`). In the end-to-end job, a stub signs licences the same way (it re-signs the Nextdab vector byte for byte before it starts). The job checks the 1-branch limit before a licence, the import, a switch-off and switch-on with a message over check-in, and then, with Nextdab offline, an ended licence that blocks new registrations while backups keep working. |

## What was built

**Database** (`V0011`, SQL test `10_phase3.sql`)

- `pki.authority`, `pki.certificate` (revocation is permanent, enforced by trigger) and `pki.crl`.
- `core.registration_token`: stored as SHA-256 only, under row-level security.
- `lic.licence`, `lic.control_state`, `lic.command` and `lic.message`.
- `ops.backup_run`.
- Four new permissions: `BRANCHES.MANAGE`, `SERVERS.MANAGE`, `LICENCE.MANAGE` and `BACKUP.VIEW/MANAGE`.
- The `kite_backup` role gets BYPASSRLS, REPLICATION, CREATEDB and pg_monitor.

**Server**

- **Business CA and mutual TLS (ADR 13)**
  - ECDSA P-256 CA. Branch certificates last two years, with the subject written by KITE.
  - A separate TLS sync port (5443) checks client certificates against the CA, the database and revocation on every request.
  - Recovery copy of the CA key, encrypted with a passphrase.
- **Branch registration and heartbeat**
  - One-time token valid 24 hours, and a security code compared on both screens.
  - The branch sends a signing request and receives a certificate plus a snapshot of master data (users, roles, branches, settings). TOTP secrets are re-encrypted with the branch's own key.
  - Heartbeat every 30 seconds. The answer carries the licence file when the branch's copy differs, the switch-off and modules state, and the revocation list.
  - Deactivating a branch revokes its server.
- **Licensing (ADRs 16, 17)**
  - `.kmelic` files and commands signed with NEXTDAB PROTOCOL 1, sent through `/v1/kme/checkin`.
  - Grace period and warnings; new transactions stop after the grace period.
  - Switch-off and module locks from Nextdab. Nextdab messages appear in the banner.
  - Unlicensed allowance: 1 branch, 1 terminal, no transactions.
- **Backups (ADR 19)**
  - Logical (`pg_dump -Fc`) and base backups.
  - AES-256-GCM `KITEBAK1` files and a recovery key.
  - Retention: daily, weekly, monthly and yearly.
  - Weekly restore check.
  - `Kite.Host backup-decrypt` to open a backup on any PC.
- **Key files under DPAPI on Windows (ADR 14)** and **permissions per branch (ADR 15)**: both closed from the Phase 2 known limits.

**Web app** (English and Arabic)

- **Branches and servers:** add branch, register server (token, address and security code), revoke with a reason, deactivate, open tokens, recovery copy.
- **Licence:** status, limits, installation ID, file import, connection to Nextdab, history. A banner over every page covers: ending soon (from 14 days before), grace, ended, switched off with the Nextdab message, and messages.
- **Backups:** last runs, schedule, archiving, key protection, run now, recovery key after the password.
- **Setup** first asks "new business or branch server". The branch path compares the security code before the token can be entered.
- **Modules** not in the licence, or switched off by Nextdab, are marked and cannot be switched on.

**Nextdab Admin 5.14** (on `F:\nextdab-admin`, change log updated)

- Product switch with a third button, KITE MOBILE ERP, which has its own pages:
  - Overview, Customers and Licences.
  - A customer page with Summary, Licences, Control, Messages, Details & notes and History.
  - Settings, which shows the public key.
- `lib/kme.js` holds the key, made at the owner's first sign-in and sealed with the owner password.
- `/v1/kme/checkin` on the same address and tunnel.
- The bundle builder now includes `lib/kme.js`, and "Close the key" also closes the KITE MOBILE ERP key.
- Tested locally with throwaway keys and a throwaway password in a scratch copy:
  - Kite RMS and Kite REMS check-ins.
  - The KITE MOBILE ERP check-in, a copied-server refusal, a licence, switch-off and parts off, a message and acknowledgements.
  - Password change and restart.
  - The bundled server, run the way the .exe runs it.
- `lib/lic.js` and `lib/rems.js` are unchanged.

## Tests

| Suite | Count | Where |
| --- | --- | --- |
| SQL | 11 files on PostgreSQL 16 and 17 | `db/tests` |
| Server unit | 78 | `tests/Kite.UnitTests` (incl. both signing vectors) |
| Server integration | 24 (9 new) | `tests/Kite.IntegrationTests/Phase3Tests.cs` |
| Web | 24 (9 new) | `src/client` |
| End-to-end Phase 2 | 1 walk-through | `e2e/real-server.mjs` (setup now starts with the new-business choice) |
| End-to-end Phase 3 | 1 walk-through, 11 steps + manual restore | `e2e/phase3.mjs`, `e2e/nextdab-stub.mjs`, CI job `e2e-phase3` |

## Problems found and fixed during Phase 3 testing

- **Licence rows could not be read** (Dapper cannot match a PostgreSQL `text[]` to a `string[]` constructor parameter). The three affected row types now use properties.
- **A backup asked for while another was running was refused.** The end-to-end run showed the restore check never starting. Requests now wait in the agent's queue.
- **Every refused request was logged as an unhandled error** (sign-in needed, no permission, licence). These are now written as answers, without error-level log entries.
- **The Nextdab Admin installer would have missed the new code.** Its one-file bundle listed only `lic.js` and `rems.js`. `build-bundle.js` now includes `kme.js`, and the bundle was run and tested.
- **"Close the key" in Nextdab Admin left the KITE MOBILE ERP key open.** It now closes it.

## Decisions and departures from the blueprint

Recorded in [`architecture-decisions.md`](architecture-decisions.md), ADRs 13 to 19:

- Business CA and mutual TLS on a separate sync port.
- DPAPI key files.
- Permissions per branch.
- The licence key made inside Nextdab Admin.
- The check-in path and the unlicensed allowance.
- The API client stays hand-written until Phase 4.
- Backups with the PostgreSQL tools, proven by restoring them.

## One step for Nextdab

KITE accepts only licences signed with the KITE MOBILE ERP key, and that key exists only on the Nextdab PC.

1. Build and install Nextdab Admin 5.14 with `BUILD-INSTALLER.bat`.
2. Sign in once as Jayed with the owner password.
3. Copy the public key from **KITE MOBILE ERP › Settings**. It is also saved in `data\KITE-MOBILE-ERP-public-key.txt`.
4. Send the public key to the developers. It goes into `LicenceKeys.EmbeddedHex`, in one commit.

Until then, the Licence page says no licence can be accepted. CI uses a test key in Development mode only.

## Known limits (carried forward)

1. Server messages are in English. The Arabic interface translates them by error code, as in Phase 2.
2. The API client is hand-written. Generating it from OpenAPI is planned for Phase 4 (ADR 18).
3. The web/API port is plain HTTP on the shop LAN. HTTPS for browsers comes with the installer (Phase 14). Branch traffic already uses mutual TLS.
4. Base backups need WAL archiving configured in PostgreSQL. The installer will do this (Phase 14). Without archiving, logical backups and the restore check still work.
5. Integration and end-to-end tests run on Linux. DPAPI is exercised only on the Windows job's unit tests. Real Windows shop PCs come with the installer phase.
6. Sync of business data between branches (beyond master data at join, the licence and control state) is Phase 5 work, as the blueprint plans.

## Next: Phase 4

Blueprint section 25, row 4: the first business modules. It will also generate the API client from OpenAPI (ADR 18), and embed the real KITE MOBILE ERP public key once Nextdab sends it.
