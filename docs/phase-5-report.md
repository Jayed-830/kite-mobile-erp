# Phase 5 report: new, used and refurbished devices

KITE MOBILE ERP · Nextdab · version 0.5.0 · November 2026

## Summary

Phase 5 teaches KITE that every phone is different. Each phone is now its own record with its IMEI (or serial number), status, condition, grade, cost and history. Used phones are inspected against a checklist that suggests what to do. Refurbishment adds parts, labour and outside work to that one phone's cost, and the journals follow. At the till the cashier scans the IMEI, and the sale takes that phone's own price and cost, so every phone sold shows its own profit.

Pulled forward from Phase 6, as planned: **device unit identity**. That covers the unit record, IMEIs unique per business, and the rule that a phone's status changes only through one database function.

The exit gate passed on GitHub Actions. In CI run `43840215384` on commit `fe5d4d9`, all fourteen jobs are green, including the new Phase 5 exit-gate test and the new browser walk-through.

## Exit gate (blueprint section 25, row 5)

"Conditions, inspections, refurbishment costs flow to unit cost."

The exit-gate test (`Phase5CostFlowTests`, CI job `phase5-exit-gate`) runs 10 workers at once against one server and PostgreSQL 17, each taking 12 phones through random lives:

- received new or used;
- inspected, either following the checklist or overriding it with a reason;
- refurbished with parts, labour and outside work, with some costs taken back;
- sold, voided, returned and inspected again;
- set aside for parts and written off.

The test keeps its own record of every phone, then checks the server against it. That record has each phone's cost, status, condition and history, plus what each step must do to each account.

| Requirement | Status | Evidence (CI run `43840215384`) |
| --- | --- | --- |
| Conditions | Done | 120 phones received, new and used. 21 new phones were changed to used by an inspection, and each moved its cost from new-device to used-device inventory (1400 → 1410) in the same transaction. Every phone's condition and grade equal the test's record. |
| Inspections | Done | 133 inspections: 78 back to stock, 48 to refurbish, 7 for parts. 8 overrode the checklist, each with a note. The checklist's suggestion is the same in C#, TypeScript and an independent Python program (21 shared cases). |
| Refurbishment costs | Done | 48 refurbishments with 39 parts, 38 labour entries and 53 outside jobs; 8 costs were taken back. 688.320 OMR of parts left spare-parts stock (1430), labour 185.100 OMR was absorbed (6210), and outside work 1,095.485 OMR waits for invoices (2110). Each figure equals the test's record. |
| Flow to unit cost | Done | Every phone's cost equals what it was bought for plus parts, labour and outside work, as its history page adds it up. Each sale's cost of sale is that phone's own cost: 199 sales, 29 voids and 77 returns, and every line's profit equals net minus cost. All 11 accounts (inventory, revenue, cost, returns, labour, clearing and write-offs) equal the test's record to the baisa. The consistency check finds no phone whose status and ledger disagree. |

The run took 10.2 seconds. Each worker's random choices come from a fixed seed, so every CI run repeats the same plan and a failure can be replayed. Locally, 10 workers × 10 phones (100 phones, 156 sales, 60 returns, 22 voids, 39 refurbishments, 23 write-offs) also matched on every figure.

**Browser walk-through** (`e2e/phase5.mjs`, CI job `e2e-phase5`). It uses a real server, PostgreSQL and Chrome. The screenshots are attached to the report message. The script works out every figure itself and checks the screen and the server against it.

1. The owner sets the labour rate to 4.000 an hour in Device settings.
2. Two new iPhones are received at 180.000 by scanning their IMEIs. Scanning one twice is flagged.
3. A used iPhone comes in at 95.000 and waits for inspection.
4. The inspection fails the screen and finds the battery at 71% (the lowest allowed is 80%). The checklist suggests "Refurbish", and saving opens refurbishment RF-B01-2026-000001.
5. The technician adds:
   - a screen from stock (28.000);
   - a battery from stock;
   - 45 minutes of labour (3.000);
   - outside board repair (12.000).
6. The battery is taken back, and it returns to stock. The phone's cost is 95.000 + 28.000 + 3.000 + 12.000 = 138.000.
7. The phone is completed as refurbished, grade A. It sells at 85% of 220.000 = 187.000.
8. At the till, an IMEI with one wrong digit is caught by its check digit before the server is asked.
9. The new iPhone is scanned, and the refurbished one is picked from the list of that model's phones.
10. The receipt shows both IMEIs and warranty dates.
11. Cost of sale is 180.000 for the new phone and 138.000 for the refurbished one.
12. The new phone is returned by scanning it. It goes to "Returned, waiting for inspection", not back on sale.
13. The refurbished phone's page shows its whole history and its profit: 178.095 net − 138.000 = 40.095. The page is also shown in Arabic. The profit report lists only this phone, not the one that came back.
14. "Check the books" is OK. The stock ledger holds the two iPhones at 360.000 (180.000 each), 3 screens and all 6 batteries.

## What was built

**Database** (`V0014`, SQL test `13_phase5_devices.sql`)

- **Device units:** IMEI 1, IMEI 2, EID or serial in a register that is unique per business (serial numbers per brand). Also status, condition, grade, own price and warranty, and a history with a gap-free sequence per phone.
- **One door for status (ADR 27):** `device.apply_event` checks every move against the transitions table. The app role cannot update a unit directly.
- **Cost only through the ledger (ADR 28):**
  - A phone moves in and out of stock at its own cost, one at a time.
  - Refurbishment adds cost with a `unit_cost` movement.
  - Checks at commit refuse any transaction that would leave a phone's status and the ledger disagreeing (`DEV-013`).
- **Inspections, refurbishment orders and costs:**
  - One open order per phone.
  - A completed order cannot be changed.
  - A cost taken back is a reversal row, never an edit.
- **Grades and checklists:** per category, with defaults: grades A 85%, B 70% and C 55%, and a 13-item checklist.
- **Lease rows for phones in a cart (ADR 30).** The new tables have row-level security, and posted rows cannot be changed.

**Server**

- **Devices:**
  - A list with filters, and look-up by IMEI or serial.
  - A detail page with the cost breakdown, price, history, inspections, refurbishments and sales with profit.
  - Price, hold, release and write-off actions.
- **Inspections:** the queue, the checklist rule (ADR 31), reclassification journals (ADR 29), and opening the refurbishment order. The order's problem is the inspector's note, or else the failed checks in both languages.
- **Refurbishment:** parts from stock, labour at the hourly rate, outside work, taking a cost back, and completing with condition, grade and warranty. Each cost posts its own journal (ADR 32).
- **Receiving:** opening and "found" stock documents take a list of IMEIs or serials, each with its cost, condition and status. Typing an IMEI by hand instead of scanning it needs `DEVICE.IMEI.MANUAL_ENTRY`. Stock counts refuse phones until Phase 6.
- **The till:**
  - Scanning an IMEI finds the phone.
  - Tapping a phone lists that model's phones in stock, with condition, grade and price.
  - Putting a phone in a cart leases it.
  - A damaged phone needs a manager's approval (`DEVICE.SELL_DAMAGED`).
  - Sale, void and return carry the phone, and a returned phone must be the same IMEI that was sold.
- **Reports:** devices in stock by condition and grade with average age, profit per phone sold, and refurbishment cost by technician.
- **Posting rules:** `refurb.cost` and `device.reclass`. The Phase 4 defaults are upgraded only if the business has not changed them.
- **8 new permissions** (`DEVICE.*`) and **error codes `DEV-001` to `DEV-013`**, with English and Arabic messages.

**Web app** (English and Arabic)

- **Devices:** device stock, inspection queue, refurbishment orders and reports.
- **Device page:** cost, price, history, sales with profit, inspections and refurbishments.
- **Inspection page:** Pass/Fail buttons, a live suggestion, and a note required to override.
- **Refurbishment page:** Part, Labour and Outside work tabs. "Adds 3.000 to the device" is shown before saving.
- **Device settings:** labour rate, lease times, price rounding, grades and the checklist editor.
- **Inventory:** tracked items show an IMEI scanner with "Comes in as" and "Status".
- **Till:**
  - The check digit is checked locally.
  - The phone list, and the IMEI and condition on each cart line.
  - "Damaged: tell the customer".
  - A parked sale keeps its phones.
  - Returns ask the cashier to scan the returned phone.
- **Receipts** (screen, 58/80 mm picture and A4 PDF) print the IMEI and the warranty end date.

**Windows app** (version 0.5.0): the receipt picture with IMEIs. Emergency mode does not sell phones (ADR 33).

## Tests

| Suite | Count | Where |
| --- | --- | --- |
| SQL | 14 files (1 new) on PostgreSQL 16 and 17 | `db/tests/13_phase5_devices.sql`: check digits, status and ledger agreement at commit, identifiers, lifecycle, cost through the ledger, sell and return the same phone, hold, write-off, inspections, refurbishment orders, the app role's rights |
| Server unit | 116 (10 new) | `tests/Kite.UnitTests/Phase5Tests.cs`: shared vectors and rule properties |
| Server integration | 38 (6 new) | `Phase5Tests.cs` (receiving and refusals, costs into one phone and the accounts, selling and returning a phone, damaged and write-off, 6 tills racing for one phone, phones in emergency mode) and `Phase5CostFlowTests.cs` |
| Exit gate | 10 workers × 12 phones | CI job `phase5-exit-gate` |
| Web | 46 (8 new) | `src/client`: device rules against the vectors; the till by IMEI, the phone list and damaged approval, inspection to refurbishment, receiving by IMEI |
| End-to-end Phase 5 | 1 walk-through, 14 steps, 20 screenshots | `e2e/phase5.mjs`, CI job `e2e-phase5` |

**One rule, three implementations.** The device arithmetic is checked three ways: C# (`Kite.Devices`), TypeScript (`@kite/pos`) and an independent Python program. The checks cover the IMEI check digit, recognising a code as an IMEI or serial, labour cost, grade prices, the unit's list price and the checklist suggestion. All three agree on `tests/vectors/device-math-vectors.json`. CI regenerates the vectors from the Python program and fails if they change.

## Problems found and fixed during Phase 5 testing

- **Receiving phones was refused.** Two query parameters, `@id` and `@Id`, were the same name to the database driver, so the unit was not found. Found by the integration tests.
- **The second refurbishment cost on an order was refused.** The guard against posting a document twice saw the order, not the cost. Each cost now posts as its own document.
- **New branches had no number series for inspections and refurbishment orders.** Found by the server tests and added for new and existing branches.
- **A mistyped IMEI at the till showed a different message from the server's.** Found by a web test; both now say the same thing.
- **The web CI job failed on the two server commits** until the web commit added English and Arabic messages for `DEV-001` to `DEV-013`. The rule that every server code has both messages caught it.
- **The refurbishment's "problem" was just the English checklist label.** The walk-through screenshots showed it. It is now the inspector's note, or the failed checks in both languages.
- **The profit report counted a returned phone.** A phone the customer brought back still showed the profit that its credit note took back. The report now lists only phones the customer kept; voids are left out too. The walk-through screenshots showed it, and a server test and the walk-through now check it.
- **A refurbishment with no technician showed "-".** It now says "Not assigned".
- **The version number was behind.** The server still said 0.3.0 during Phase 4 (the report said 0.4.0). The server and the Windows app are now both 0.5.0.

## Decisions and departures from the blueprint

Recorded in [`architecture-decisions.md`](architecture-decisions.md):

- **ADR 27:** a phone is a device unit; status changes only through `device.apply_event`.
- **ADR 28:** cost changes only through the stock ledger; status and ledger must agree at commit.
- **ADR 29:** used, refurbished and damaged phones post to their own accounts; inspections reclassify.
- **ADR 30:** a phone in a cart is a lease, not a lock.
- **ADR 31:** the checklist suggests, the inspector decides, and a different decision needs a note.
- **ADR 32:** labour is absorbed into stock; outside work waits for the supplier's invoice.
- **ADR 33:** emergency mode does not sell phones until Phase 6.

## Things for Nextdab

1. **The KITE MOBILE ERP public key: received and compiled in** on 16 November 2026. Release builds now accept licences signed by Nextdab Admin 5.14 with the KITE MOBILE ERP key, and refuse any other.
2. **QuestPDF licence: confirmed** on 16 November 2026. Nextdab uses the Community licence (annual gross revenue under USD 1,000,000).
3. **The draft release "dev-package-cache"** on GitHub should be deleted by hand. This session is not allowed to delete releases.

Nextdab Admin needs no change for Phase 5. The licence modules "device" and "refurb" already exist.

## Known limits

1. **Phones are not sold in emergency mode** (ADR 33). Accessories, parts and services still are.
2. **The IMEI register is per business on one server.** These come in Phase 6:
   - a central register across head office and branch servers;
   - TAC (model) look-up;
   - blocked and stolen IMEI lists;
   - handling duplicates.
3. **Stock counts skip phones.** Counting by IMEI comes in Phase 6.
4. **Second-hand phones are sold with standard VAT on the full price.** The VAT margin scheme and trade-ins at the till come in Phase 7. Until then, a phone bought from a walk-in seller is recorded on an opening or "found" stock document.
5. **Outside work credits 2110** until supplier invoices arrive in Phase 11. Supplier returns (Phase 11) and branch transfers (Phase 12) have their statuses in the database but no screens yet.
6. **Customer repairs** (the customer's own phone) are Phase 8. Refurbishment here is for the shop's own stock.
7. **People's names in history lists show in English** in the Arabic interface. This applies to every screen since Phase 2, not only devices.
8. **Server messages are in English.** The Arabic interface translates them by error code, as before.

## Next: Phase 6

Blueprint section 25, row 6: IMEI and serial number management, with the central registry API. The exit gate is: uniqueness and verification points pass concurrency tests.
