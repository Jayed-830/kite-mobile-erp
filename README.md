# KITE MOBILE ERP

© Nextdab. Proprietary software, all rights reserved.

Offline-first, multi-branch ERP and POS for mobile phone sales and repair shops. Oman first: OMR with 3 decimals, English and Arabic (right-to-left), Windows desktop and browser.

**Status:** Phase 2 (project foundation) is done. See [`docs/phase-2-report.md`](docs/phase-2-report.md) for the report and [`docs/architecture-decisions.md`](docs/architecture-decisions.md) for the decisions behind the code. The Phase 1 blueprint is the design reference.

## Repository layout

```text
db/
  bootstrap/00_bootstrap.sql   one-time database setup by a superuser (roles, extensions)
  migrations/V0001..V0009      forward-only SQL migrations (run by the server as kite_migrator)
  tests/                       SQL tests: immutable ledgers, balanced journals, RLS, audit chain, outbox
src/server/
  Kite.Platform                IDs (UUIDv7), hybrid logical clock, money, modules, permissions, errors
  Kite.Tax                     tax engine (inclusive/exclusive VAT, margin scheme) and Oman template
  Kite.Accounting              chart-of-accounts template, journal builder, fiscal calendar
  Kite.Identity                Argon2id hashing, TOTP, tokens, lockout, secret encryption
  Kite.Persistence             migrator, unit of work with branch scope, audit and outbox writers
  Kite.Host                    ASP.NET Core server: setup, sign-in, users, roles, terminals, settings, audit
src/client/
  packages/ui                  design system (tokens, components, RTL, light/dark)
  packages/i18n                English and Arabic strings, number formatting
  packages/api-client          typed client for the server API
  apps/web                     the web app (served by the KITE server)
  apps/desktop                 Windows desktop app (Electron shell)
tests/
  Kite.UnitTests               unit and property-based tests
  Kite.IntegrationTests        end-to-end tests against a real PostgreSQL
poc/                           Phase 2 proofs of concept
scripts/                       build helpers
```

## Run it on a development PC

Prerequisites: .NET 10 SDK, Node.js 22, PostgreSQL 16 or later.

1. **Database (once).** As a PostgreSQL superuser:

   ```bash
   createdb kite
   psql -d kite -f db/bootstrap/00_bootstrap.sql
   psql -d kite -c "ALTER ROLE kite_migrator PASSWORD 'choose-one'; ALTER ROLE kite_app PASSWORD 'choose-another';"
   ```

2. **Web app.** `scripts/build-web.sh` (or `scripts/build-web.ps1` on Windows) builds the web app into the server's `wwwroot`.

3. **Server.** Give the connection strings through environment variables (never commit passwords):

   ```bash
   export ConnectionStrings__App="Host=localhost;Database=kite;Username=kite_app;Password=choose-another"
   export ConnectionStrings__Migrator="Host=localhost;Database=kite;Username=kite_migrator;Password=choose-one"
   export ASPNETCORE_URLS="http://localhost:5080"
   dotnet run --project src/server/Kite.Host
   ```

   The server applies the migrations, then prints an 8-character **setup code** (also saved in `data/setup-code.txt`).

4. Open <http://localhost:5080>, enter the setup code and complete the setup wizard. Then sign in with the owner account.

For UI work without a server: `cd src/client && npm run dev:mock` (owner `owner` / `Muscat-Ruwi-2026!`, MFA code `123456`; `VITE_MOCK=setup` starts at the setup wizard with code `ABCD2345`). The mock is for design review only.

## Tests

| What | Command |
| --- | --- |
| Database (migrations + SQL rules) | `PGUSER=postgres db/tests/run.sh` |
| Server unit tests | `dotnet test tests/Kite.UnitTests` |
| Server integration tests | `KITE_TEST_PG="Host=localhost;Username=postgres;Password=..." dotnet test tests/Kite.IntegrationTests` |
| Web app | `cd src/client && npm test` |
| Proofs of concept | see `poc/*/` and the `pocs-*` CI jobs |

Every push runs all of these on GitHub Actions (Linux and Windows, PostgreSQL 16 and 17).
