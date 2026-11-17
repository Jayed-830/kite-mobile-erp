# KITE MOBILE ERP

© Nextdab. Proprietary software, all rights reserved.

Offline-first, multi-branch ERP and POS for mobile phone sales and repair shops. Oman first: OMR with 3 decimals, English and Arabic (right-to-left), Windows desktop and browser.

**Status:** Phase 11 (purchasing and accounting: purchase orders with approval limits by role and branch, sent with a PDF; goods received against the order with IMEIs and landed costs spread by value or quantity; goods received not invoiced kept as a ledger; supplier invoices matched three ways with tolerances, an override with a reason, the price difference split between stock on hand and price variance, one invoice booked for several branches; returns and debit notes; supplier payments oldest first or as head office names them; expenses and card settlements; the chart of accounts, periods soft- and hard-closed per branch with late documents moved to the next open month, manual journals with a second approver and reversal, year-end; trial balance, ledger, profit and loss, balance sheet and cash flow per branch or consolidated; the VAT return worked out from the documents, filed from closed months and settled branch by branch; every subledger checked against its control account every day) is done. See [`docs/phase-11-report.md`](docs/phase-11-report.md), the earlier [`docs/phase-10-report.md`](docs/phase-10-report.md), [`docs/phase-9-report.md`](docs/phase-9-report.md), [`docs/phase-8-report.md`](docs/phase-8-report.md), [`docs/phase-7-report.md`](docs/phase-7-report.md), [`docs/phase-6-report.md`](docs/phase-6-report.md), [`docs/phase-5-report.md`](docs/phase-5-report.md), [`docs/phase-4-report.md`](docs/phase-4-report.md), [`docs/phase-3-report.md`](docs/phase-3-report.md) and [`docs/phase-2-report.md`](docs/phase-2-report.md), and [`docs/architecture-decisions.md`](docs/architecture-decisions.md) for the decisions behind the code. The Phase 1 blueprint is the design reference.

## Repository layout

```text
db/
  bootstrap/00_bootstrap.sql   one-time database setup by a superuser (roles, extensions)
  migrations/V0001..V0014      forward-only SQL migrations (run by the server as kite_migrator)
  tests/                       SQL tests: immutable ledgers, balanced journals, RLS, audit chain, outbox, revocation, licences, stock, sales, printers, devices
src/server/
  Kite.Platform                IDs (UUIDv7), hybrid logical clock, money, modules, permissions, errors
  Kite.Tax                     tax engine (inclusive/exclusive VAT, margin scheme) and Oman template
  Kite.Accounting              chart-of-accounts template, journal builder, fiscal calendar, posting engine and rules
  Kite.Sales                   POS arithmetic: line pricing, discounts, VAT, cash rounding, tenders, returns, QR
  Kite.Devices                 IMEI check digit, labour cost, grade prices, the inspection checklist rule
  Kite.Identity                Argon2id, TOTP, tokens, lockout, secret encryption, DPAPI key files, business CA,
                               NEXTDAB PROTOCOL 1 licences and commands, backup encryption
  Kite.Persistence             migrator, unit of work with branch scope, audit and outbox writers
  Kite.Host                    ASP.NET Core server: setup, sign-in, users, roles, terminals, settings, audit,
                               branches and servers (mTLS sync port), licence and Nextdab check-in, backup agent,
                               catalog, inventory, POS (shifts, approvals, sales, returns), receipts and printing,
                               emergency intake, devices (units, inspections, refurbishment, reports)
src/client/
  packages/ui                  design system (tokens, components, RTL, light/dark)
  packages/i18n                English and Arabic strings, number formatting
  packages/api-client          typed client for the server API (checked against the server's routes by a test)
  packages/pos                 POS, device and trade-in arithmetic (same vectors as Kite.Sales and Kite.Devices), the receipt picture
  apps/web                     the web app (served by the KITE server)
  apps/desktop                 Windows desktop app (Electron): receipt printers, cash drawer, emergency store
tests/
  Kite.UnitTests               unit and property-based tests
  Kite.IntegrationTests        end-to-end tests against a real PostgreSQL
  vectors/                     NEXTDAB PROTOCOL 1 signing vectors (one made by Nextdab Admin 5.14 itself), POS,
                               device, trade-in, repair, commission, credit and purchase arithmetic vectors (from independent Python implementations), PIN hash vectors
e2e/                           browser walk-throughs of real servers (Phase 2; Phase 3 with two servers and a Nextdab stub;
                               Phase 4 the till and back office; Phase 5 phones from receiving to profit; Phase 6 IMEIs
                               across head office and a branch, with head office stopped and restarted; Phase 7
                               trade-ins, store credit and a margin-scheme resale; Phase 8 repairs from intake to
                               collection with a WhatsApp approval; Phase 9 parts waited for, commissions, a
                               correction at the till and payouts; Phase 10 credit accounts, sales on account in
                               instalments, approvals, payments, holds, statements, the ledger check and suppliers;
                               Phase 11 an order, receipt, three-way match, debit note, payment, expense, manual
                               journal, the VAT return filed and the statements)
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

**Licences.** KITE accepts only licences and commands signed with the KITE MOBILE ERP key made in Nextdab Admin 5.14. Its public key is compiled in as `LicenceKeys.EmbeddedHex` (`src/server/Kite.Host/Infrastructure/Phase3Options.cs`). For development only, `ASPNETCORE_ENVIRONMENT=Development` with `Kite__Licensing__DevPublicKey=<hex>` uses a test key (see `e2e/nextdab-stub.mjs`).

For UI work without a server: `cd src/client && npm run dev:mock` (owner `owner` / `Muscat-Ruwi-2026!`, MFA code `123456`; cashier PIN 2468 and manager PIN 1357 at `/pos`; `VITE_MOCK=setup` starts at the setup wizard with code `ABCD2345`). The mock is for design review only.

## Tests

| What | Command |
| --- | --- |
| Database (migrations + SQL rules) | `PGUSER=postgres db/tests/run.sh` |
| Server unit tests | `dotnet test tests/Kite.UnitTests` |
| Server integration tests | `KITE_TEST_PG="Host=localhost;Username=postgres;Password=..." dotnet test tests/Kite.IntegrationTests` |
| Web app | `cd src/client && npm test` |
| Windows app | `cd src/client/apps/desktop && npm ci && npm test` (`npm run bundle-web` puts the POS inside it) |
| Load test, 20 terminals (Phase 4 exit gate) | `KITE_LOAD_SALES=40 dotnet test tests/Kite.IntegrationTests --filter Phase4LoadTests` with `KITE_TEST_PG` set |
| End-to-end, two servers (Phase 3) | see the `e2e-phase3` CI job (`e2e/phase3.mjs` + `e2e/nextdab-stub.mjs`) |
| End-to-end, till and back office (Phase 4) | see the `e2e-phase4` CI job (`e2e/phase4.mjs` + `e2e/nextdab-stub.mjs`) |
| Device costs, 10 workers (Phase 5 exit gate) | `KITE_P5_WORKERS=10 KITE_P5_PHONES=12 dotnet test tests/Kite.IntegrationTests --filter Phase5CostFlowTests` with `KITE_TEST_PG` set |
| End-to-end, phones (Phase 5) | see the `e2e-phase5` CI job (`e2e/phase5.mjs` + `e2e/nextdab-stub.mjs`) |
| IMEI uniqueness, 20 tills and 10 branch servers (Phase 6 exit gate) | `KITE_P6_TILLS=20 KITE_P6_BRANCHES=10 KITE_P6_ROUNDS=8 KITE_P6_UNITS=8 dotnet test tests/Kite.IntegrationTests --filter Phase6ExitGateTests` with `KITE_TEST_PG` set |
| End-to-end, IMEIs across two servers (Phase 6) | see the `e2e-phase6` CI job (`e2e/phase6.mjs` starts both servers itself) |
| Trade-in settlements, 20 tills (Phase 7 exit gate) | `KITE_P7_TILLS=20 KITE_P7_ROUNDS=10 dotnet test tests/Kite.IntegrationTests --filter Phase7ExitGateTests` with `KITE_TEST_PG` set |
| End-to-end, trade-ins (Phase 7) | see the `e2e-phase7` CI job (`e2e/phase7.mjs` + `e2e/nextdab-stub.mjs`; it reads the accounts with `psql`) |
| Repair workflow at 4 branches at once (Phase 8 exit gate) | `KITE_P8_BRANCHES=4 KITE_P8_WORKERS=3 KITE_P8_ROUNDS=4 dotnet test tests/Kite.IntegrationTests --filter Phase8ExitGateTests` with `KITE_TEST_PG` set |
| End-to-end, repairs and WhatsApp (Phase 8) | see the `e2e-phase8` CI job (`e2e/phase8.mjs` + `e2e/nextdab-stub.mjs`; it reads the accounts with `psql`) |
| Parts and commissions at 4 branches at once (Phase 9 exit gate) | `KITE_P9_BRANCHES=4 KITE_P9_TILLS=3 KITE_P9_JOBS=6 dotnet test tests/Kite.IntegrationTests --filter Phase9ExitGateTests` with `KITE_TEST_PG` set |
| End-to-end, parts and commissions (Phase 9) | see the `e2e-phase9` CI job (`e2e/phase9.mjs` + `e2e/nextdab-stub.mjs`; it reads the accounts with `psql`) |
| Credit online and offline, 12 tills and 4 branch servers (Phase 10 exit gate) | `KITE_P10_TILLS=12 KITE_P10_BRANCHES=4 KITE_P10_CUSTOMERS=10 KITE_P10_ATTEMPTS=10 dotnet test tests/Kite.IntegrationTests --filter Phase10ExitGateTests` with `KITE_TEST_PG` set |
| End-to-end, customer credit and suppliers (Phase 10) | see the `e2e-phase10` CI job (`e2e/phase10.mjs` + `e2e/nextdab-stub.mjs`; it reads the accounts with `psql`) |
| Purchasing races, a full day at three branches and the quarter's VAT return (Phase 11 exit gate) | `KITE_P11_WORKERS=16 KITE_P11_SALES=10 dotnet test tests/Kite.IntegrationTests --filter Phase11ExitGateTests` with `KITE_TEST_PG` set |
| End-to-end, purchasing and accounting (Phase 11) | see the `e2e-phase11` CI job (`e2e/phase11.mjs` + `e2e/nextdab-stub.mjs`; it reads the accounts with `psql`) |
| Proofs of concept | see `poc/*/` and the `pocs-*` CI jobs |

Every push runs all of these on GitHub Actions (Linux and Windows, PostgreSQL 16 and 17).
