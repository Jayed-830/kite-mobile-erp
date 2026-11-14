# Phase 2 report: project foundation

KITE MOBILE ERP · Nextdab · version 0.2.0 · November 2026

## Summary

Phase 2 delivers the foundation every later module builds on: the database with its protection rules, the server platform, sign-in and permissions, the setup wizard, the bilingual web app, the desktop shell, and four proofs of concept for the risky parts. There are no sales, stock or repair screens yet. Those start in Phase 4, as the blueprint planned.

All work is in the repository `Jayed-830/kite-mobile-erp`. Each push is built and tested automatically on GitHub Actions, on Linux and Windows, with PostgreSQL 16 and 17.

## Definition of done (blueprint section 26)

| Requirement | Status | Evidence |
| --- | --- | --- |
| All CI tests green | Done | GitHub Actions: database (PG 16 and 17), server on Linux and Windows, integration tests, web app, desktop shell, proofs of concept, end-to-end |
| Database builds from migrations | Done | `db/tests/run.sh` builds a fresh database from `V0001`–`V0010` as `kite_migrator`; the server does the same at startup |
| Tests prove the app role cannot update or delete ledgers | Done | `db/tests/01_grants.sql`, `02_ledger_immutable.sql`: `kite_app` UPDATE/DELETE/TRUNCATE on journals, journal lines and the audit log fail; triggers block even the owner role |
| Setup, two-step sign-in, roles and audit chain shown in English and Arabic | Done | End-to-end CI job drives a real server in Chrome (screenshots in the job output); 15 integration tests on the API |
| Proof-of-concept write-up | Done | Section "Proofs of concept" below |

## What was built

**Database** (10 SQL migrations, 10 SQL test files)

- Four roles: `kite_migrator` owns the schema, `kite_app` runs the application, `kite_ro` reads reports, `kite_backup` takes backups. Passwords are set at install time and are never stored in files.
- Ledgers are append-only: journals, journal lines and the audit log reject UPDATE, DELETE and TRUNCATE. A journal must balance before commit, cannot be posted twice or into a closed period, and a reversal must exactly mirror its original.
- The audit log is a hash chain, and `audit.verify_chain()` finds the first broken entry.
- The sync outbox gives each server a gap-free event sequence. Payloads are immutable and carry a SHA-256 hash.
- Row-level security by branch. With no branch scope set, nothing is visible.
- Number series are gap-free and per branch: JV, INV, CN, R, TI, PO, GR, RC.
- Tax codes and rates use effective dates that cannot overlap.

**Server platform** (ASP.NET Core, .NET 10)

- Platform services: UUIDv7 IDs, a hybrid logical clock, and an OMR money type with 3 decimals and exact allocation. Module switches enforce dependencies; a switched-off module answers 403.
- Every endpoint must declare its access rule; a test fails the build if one does not.
- Error responses use RFC 9457 with a stable code (for example `AUTH-005`), which the web app translates.
- Oman tax template:
  - VAT 5% from 16 April 2021, plus zero-rated, exempt and out-of-scope codes.
  - The margin scheme is off until the Tax Authority approves it.
  - All codes are marked "needs accountant review".
- A chart-of-accounts template of about 52 accounts, a journal builder, and a fiscal calendar.

**Sign-in and permissions**

- Passwords and PINs are hashed with Argon2id. Passwords need at least 10 characters and must not contain the username. A PIN is 4–8 digits and works only on an approved terminal.
- Lockout: after 5 failures the account locks for 15 minutes, doubling each time up to 24 hours. A manager can unlock it.
- Sessions:
  - Access tokens last 15 minutes; refresh tokens last 12 hours and rotate on each use.
  - Only hashes of the tokens are stored, so a session can be revoked immediately.
  - A password change, a disabled user or a revoked terminal ends sessions.
- Two-step verification uses TOTP (RFC 6238):
  - The secret is encrypted with AES-256-GCM.
  - Each code is accepted only once (`V0010`).
  - While the second step is pending, every other request is blocked.
- Roles and permissions:
  - Eight role templates and 31 permissions; roles can be granted per branch.
  - The owner role is locked, and the last owner cannot be removed.
  - A user can grant only permissions they hold themselves.
- A new user must change their temporary password at first sign-in.
- Terminal registration needs a manager's approval.

**Setup wizard.** A one-time setup code is printed by the server and also saved to its data folder. The wizard has five steps: business, head office, owner, modules and review. One database transaction creates everything: business, branch, warehouse, owner, roles, number series, tax codes, chart of accounts and fiscal year.

**Web app** (React, TypeScript)

- Pages: sign-in with two-step verification, users, roles, terminals, business details, modules, tax codes, chart of accounts, audit log (with chain verification) and my account.
- English and Arabic with full right-to-left layout, plus light and dark themes. The app works on a shop network with no internet: it loads no web fonts and uses no CDNs.

**Desktop app** (Electron 39). The first run asks for the server address and checks it, then opens the web app from that server. The shell is locked down:

- context isolation and sandbox are on, and Node.js is not available to pages
- the app cannot navigate away from the server's origin
- it exposes a small preload bridge with five functions

A headless test confirmed that the connect page loads and rejects an invalid address, the address is saved, the sign-in page opens, and pages cannot reach Node.js.

## Proofs of concept

| PoC | Result | What was checked | Not yet proven |
| --- | --- | --- | --- |
| **Arabic PDF invoice** (QuestPDF) | Passed on CI | A 33 KB PDF with fonts embedded as subsets and Unicode mapping. Arabic letters join correctly, run right-to-left, and sit beside English. The page was rendered to an image and reviewed. | Fawtara layout and QR code (Phase 9). Final fonts: DejaVu Sans is used now, and a licensed Arabic font is to be chosen. |
| **Arabic receipt for 80 mm thermal printers** (ESC/POS raster) | Passed (locally and on CI) | Arabic text is rendered to a 576-dot-wide image, then encoded as a `GS v 0` raster command (57 KB). The test checked dimensions, payload length and ink coverage, and a preview image of the receipt was reviewed. | **Not printed on a real printer.** Needs a test on the owner's printer models (Phase 7). |
| **Encrypted emergency store** (SQLCipher) | Passed on Linux and Windows | WAL and synchronous=FULL. The store cannot be read without the key or with a wrong key. All 250 committed records are read back. A duplicate terminal sequence is refused. No IMEI appears in plain text in the files on disk. | Key storage in Windows DPAPI (Phase 3). |
| **PostgreSQL as a Windows service** | Passed on CI (Windows Server) | `initdb`, then service registration, start, bootstrap and all migrations (23 tables, 31 permissions). The service survives a restart and is then removed. | The real installer (Phase 14). Running on the shop's own Windows 10/11 PCs. |

## Problems found and fixed during Phase 2 testing

- **A cashier could see data cached from the previous user's session** on the same browser (the owner's tax checklist). The web app now clears all cached data when a session ends or another user signs in. A web test and the end-to-end test guard this.
- **A two-step code could be used twice** within its 30-second window. Each code is now accepted only once (`V0010`), and an integration test checks this.
- **Arabic screens showed server errors in English.** Error codes are now translated, and a test fails if the server has an error code with no Arabic text.

## Decisions and departures from the blueprint

Recorded in [`architecture-decisions.md`](architecture-decisions.md):

- ADR 8: plain SQL migrations instead of EF Core migrations.
- ADR 9: Npgsql + Dapper instead of EF Core.
- ADR 10: the server's own session tokens instead of OpenIddict.
- ADR 11: plain CSS design tokens instead of Tailwind/Radix.
- ADR 12: build and test on GitHub Actions.

## Known limits (carried into Phase 3)

1. Server error messages are in English. The Arabic interface translates them by error code. Field-level details, such as which rule a password broke, appear in English under the Arabic message.
2. Permissions are the union of a user's roles across branches. Per-branch evaluation comes with the branch context in Phase 3.
3. The encryption key file sits in the server's data folder. Wrapping it with Windows DPAPI is the installer's job.
4. The web app's API client is written by hand. It will be generated from OpenAPI in Phase 3.
5. The desktop installer (NSIS) is configured but not built or code-signed. It needs a code-signing certificate.
6. Integration tests run on Linux. Windows runs the unit tests, and the PostgreSQL service PoC runs all migrations on Windows.

## Nextdab Admin

Nothing in Nextdab Admin changed in Phase 2. The KITE MOBILE ERP licence check-in (to `https://control.nextdab.com`) and the matching Nextdab Admin update are scheduled for Phase 3, following the shared-copy procedure on `F:\nextdab-admin`.

## Next: Phase 3

The full Phase 3 scope is in blueprint section 26. From Phase 2 it inherits the KITE MOBILE ERP licence check-in and the matching Nextdab Admin update, the Windows DPAPI protection of the key file, generating the API client from OpenAPI, and per-branch permission checks (known limits 2 to 4 above).
