# KITE MOBILE ERP

© Nextdab. Proprietary software, all rights reserved.

Offline-first, multi-branch ERP and POS for mobile phone sales and repair shops. Oman first: OMR with 3 decimals, English and Arabic (right-to-left), Windows desktop and browser.

**Status:** Phase 3 (main server and branch server foundation: business CA, branch registration, heartbeat, licensing, backups) is done. See [`docs/phase-3-report.md`](docs/phase-3-report.md), the earlier [`docs/phase-2-report.md`](docs/phase-2-report.md), and [`docs/architecture-decisions.md`](docs/architecture-decisions.md) for the decisions behind the code. The Phase 1 blueprint is the design reference.

## Repository layout

```text
db/
  bootstrap/00_bootstrap.sql   one-time database setup by a superuser (roles, extensions)
  migrations/V0001..V0011      forward-only SQL migrations (run by the server as kite_migrator)
  tests/                       SQL tests: immutable ledgers, balanced journals, RLS, audit chain, outbox, revocation, licences
src/server/
  Kite.Platform                IDs (UUIDv7), hybrid logical clock, money, modules, permissions, errors
  Kite.Tax                     tax engine (inclusive/exclusive VAT, margin scheme) and Oman template
  Kite.Accounting              chart-of-accounts template, journal builder, fiscal calendar
  Kite.Identity                Argon2id, TOTP, tokens, lockout, secret encryption, DPAPI key files, business CA,
                               NEXTDAB PROTOCOL 1 licences and commands, backup encryption
  Kite.Persistence             migrator, unit of work with branch scope, audit and outbox writers
  Kite.Host                    ASP.NET Core server: setup, sign-in, users, roles, terminals, settings, audit,
                               branches and servers (mTLS sync port), licence and Nextdab check-in, backup agent
src/client/
  packages/ui                  design system (tokens, components, RTL, light/dark)
  packages/i18n                English and Arabic strings, number formatting
  packages/api-client          typed client for the server API
  apps/web                     the web app (served by the KITE server)
  apps/desktop                 Windows desktop app (Electron shell)
tests/
  Kite.UnitTests               unit and property-based tests
  Kite.IntegrationTests        end-to-end tests against a real PostgreSQL
  vectors/                     NEXTDAB PROTOCOL 1 signing vectors (one made by Nextdab Admin 5.14 itself)
e2e/                           browser walk-throughs of real servers (Phase 2; Phase 3 with two servers and a Nextdab stub)
poc/                           Phase 2 proofs of concept
scripts/                       build helpers
```

## Run it on a development PC

Prerequisites: .NET 10 SDK, Node.js 22, PostgreSQL 16 or later.

1. **Database (once).** As a PostgreSQL superuser:

   ```bash
   createdb kite
   psql -d kite -f db/bootstrap/00_bootstrap.sql
   psql -d kite -c "ALTER ROLE kite_migrator PASSWORD 'choose-one'; ALTER ROLE kite_app PASSWORD 'choose-another'; ALTER ROLE kite_backup PASSWORD 'choose-a-third';"
   ```

2. **Web app.** `scripts/build-web.sh` (or `scripts/build-web.ps1` on Windows) builds the web app into the server's `wwwroot`.

3. **Server.** Give the connection strings through environment variables (never commit passwords):

   ```bash
   export ConnectionStrings__App="Host=localhost;Database=kite;Username=kite_app;Password=choose-another"
   export ConnectionStrings__Migrator="Host=localhost;Database=kite;Username=kite_migrator;Password=choose-one"
   export ConnectionStrings__Backup="Host=localhost;Database=kite;Username=kite_backup;Password=choose-a-third"   # optional: backups
   export ASPNETCORE_URLS="http://localhost:5080"
   dotnet run --project src/server/Kite.Host
   ```

   Branch servers connect to the main server's TLS sync port (`Kite__Sync__Port`, default 5443; extra names or addresses in `Kite__Sync__Hosts`). Backups need the PostgreSQL tools on the PATH or in `Kite__Backup__PgBin`, and WAL archiving for base backups.

   The server applies the migrations, then prints an 8-character **setup code** (also saved in `data/setup-code.txt`).

4. Open <http://localhost:5080>, choose a new business (or a branch server of an existing one), enter the setup code and complete the wizard. Then sign in with the owner account.

**Licences.** KITE accepts only licences and commands signed with the KITE MOBILE ERP key made in Nextdab Admin 5.14. Its public key goes into `LicenceKeys.EmbeddedHex` (`src/server/Kite.Host/Infrastructure/Phase3Options.cs`); until then the Licence page says no licence can be accepted. For development only, `ASPNETCORE_ENVIRONMENT=Development` with `Kite__Licensing__DevPublicKey=<hex>` uses a test key (see `e2e/nextdab-stub.mjs`).

For UI work without a server: `cd src/client && npm run dev:mock` (owner `owner` / `Muscat-Ruwi-2026!`, MFA code `123456`; `VITE_MOCK=setup` starts at the setup wizard with code `ABCD2345`). The mock is for design review only.

## Tests

| What | Command |
| --- | --- |
| Database (migrations + SQL rules) | `PGUSER=postgres db/tests/run.sh` |
| Server unit tests | `dotnet test tests/Kite.UnitTests` |
| Server integration tests | `KITE_TEST_PG="Host=localhost;Username=postgres;Password=..." dotnet test tests/Kite.IntegrationTests` |
| Web app | `cd src/client && npm test` |
| End-to-end, two servers (Phase 3) | see the `e2e-phase3` CI job (`e2e/phase3.mjs` + `e2e/nextdab-stub.mjs`) |
| Proofs of concept | see `poc/*/` and the `pocs-*` CI jobs |

Every push runs all of these on GitHub Actions (Linux and Windows, PostgreSQL 16 and 17).
