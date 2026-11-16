# Phase 4 report: POS and inventory management

KITE MOBILE ERP · Nextdab · version 0.4.0 · November 2026

## Summary

Phase 4 gives KITE its first business modules. It adds a product catalog with barcodes and prices, a stock ledger with adjustments and counts, and a touch till that runs in a browser or in the Windows app. The till handles shifts, manager approvals, split payments, returns, voids, parked sales and bilingual receipts with a QR code. Every sale and return posts its journals in the same database transaction as the stock and the document.

Three things were pulled forward from later phases:

- **The posting engine core with default rules.** Rules are data, in `acct.posting_rule`.
- **The printing bridge.** Receipts go to 58/80 mm printers as ESC/POS pictures, with the cash drawer.
- **The emergency store.** The Windows app keeps selling when the server is off and uploads afterwards.

The exit gate passed on GitHub Actions. In CI run `43796687062` on commit `3d56f08`, all twelve jobs are green, including the 20-terminal load test and the new browser walk-through of the till.

## Exit gate (blueprint section 25, row 4)

"Sales, returns, shifts, stock ledger and journals balanced under 20-terminal load."

The load test runs 20 tills at once against one server and PostgreSQL 17, each with its own cashier and shift. Each till sells 40 times: random baskets from 12 items with deliberately low stock, random discounts within the cashier's limit, and split cash and card payments. Some sales are returned in part, some are voided, and every shift closes with a counted drawer. The test keeps its own ledger of everything it did and then checks the server against it.

| Requirement | Status | Evidence (CI run `43796687062`) |
| --- | --- | --- |
| 20 terminals at once | Done | 20 terminals, 656 sales, 121 returns, 80 voids in 13.6 s (48.3 sales per second). Completing a sale took 173 ms at the median, 966 ms at p95 and 2,168 ms at worst. |
| Sales | Done | Every sale is in the server with its lines, payments and two journals. Invoice numbers are gap-free per branch. Cash moved was 6,121.310 OMR, net revenue 14,141.283 OMR and VAT 596.746 OMR, each equal to the test's own ledger. |
| Returns | Done | 121 partial and full returns. Each was refunded to the baisa (the last return of a line takes the remainder) and its stock went back. A return above what was sold is refused. |
| Shifts | Done | All 20 shifts closed with zero variance. Each Z report's sales, returns, cash taken, card total and expected drawer equal the test's ledger for that till. |
| Stock ledger | Done | 144 sales were correctly refused for lack of stock, and none oversold. Every balance equals the sum of its movements, and the stock value equals the inventory accounts in the general ledger. |
| Journals balanced | Done | Every journal balances. GL movements on cash, revenue, VAT, cash rounding, inventory and cost of sales equal the test's ledger. The consistency check (`GET /inventory/check`) is OK. |

The same test with 12 sales per till also passes on every push in the integration tests. Locally with 12 sales per till: 221 sales, 37 returns, 17 voids, 19 refusals, all balanced.

**Browser walk-through** (`e2e/phase4.mjs`, CI job `e2e-phase4`). This uses a real server, PostgreSQL and Chrome. The screenshots are attached to the report message. The walk-through covers:

1. The owner adds a product and its opening stock in the back office.
2. A browser is registered as till T01. Signing in at T01 without its device key is refused.
3. The cashier signs in with her PIN and opens a shift with a counted float.
4. She scans three cables and taps a charger. A discount above her limit is approved with the manager's PIN.
5. She takes a split card and cash payment, with cash rounding, and the bilingual receipt with its QR code is shown.
6. A price below the minimum is approved by the manager from another screen.
7. A sale is made with the till in Arabic.
8. One cable is returned by receipt number.
9. Cash is paid in.
10. The shift closes blind, with the bank batch total entered, and the Z report shows 0.000 difference.
11. The back office shows the Z report, the receipt and the A4 tax invoice PDF.
12. "Check the books" is OK, and cable stock is 40 − 3 + 1 = 38.

After the walk-through the database holds 4 documents and 13 journals, and the audit chain verifies.

## What was built

**Database** (`V0012`, `V0013`, SQL tests `11_phase4.sql`, `12_phase4_printers.sql`)

- **Catalog:** brands, categories, products, variants (SKU), barcodes and price lists with dated prices and branch prices. Serialized items are refused until Phase 5–6 (`CAT-003`).
- **Stock:** `inv.stock_balance` and `inv.stock_movement` at moving average cost, adjustments and counts, and stock exceptions (emergency sales only).
- **Sales:** sales and credit notes, lines, payments, parked sales, shifts with cash events and denomination counts, approvals, the POS policy per branch, printers, and terminal printer and device key.
- **Rules in the database:** journals must balance, movements and posted documents cannot be changed, and one open shift per terminal.
- Row-level security on every new table.

**Server**

- **Catalog and customers:** CRUD with audit. Phone numbers are normalised to +968.
- **Inventory:** balances, movements, opening and adjustment documents, counts that post their differences, exceptions, and the consistency check.
- **POS:**
  - The policy covers negative stock, the return window, the cash refund cap, the variance threshold, blind close, rounding, denominations and the receipt footer.
  - Shifts: open with a count, X and Z reports, cash in, cash out and to the safe, close with card settlement totals, and manager close.
  - Approvals: by PIN at the till with lockout, or remote from the Approvals page.
  - Sales: quote, complete (idempotent), return, void, park and recall, and list.
- **POS arithmetic** (`Kite.Sales`, and the same rules in `@kite/pos`):
  - Line pricing and discount limits.
  - VAT inclusive or exclusive, rounded per line or per invoice.
  - Cash rounding to the policy step, half away from zero.
  - Tenders and change.
  - Proportional returns.
  - The QR as TLV (seller, VAT number, time, total, VAT, UUID).
- **Posting engine (ADR 20):** seven default rules. Each sale posts revenue, VAT, tenders and rounding, then cost of sales against inventory. Credit notes, shift variance, cash pay-out and stock adjustments also post. A document dated in a closed period posts to the first open one.
- **Receipts:** the receipt as data for the client, reprints (permission and audit), and the A4 tax invoice PDF with QuestPDF and QRCoder (ADR 22).
- **Printing (ADR 23):** printers per branch, assigned to terminals. A print relay sends raw ESC/POS to network printers on private addresses only. The drawer opens only for the user's own open shift.
- **Emergency intake (ADR 24):** a snapshot for the Windows app, and an ordered, once-only upload of offline shifts, sales and cash events. Conflicts are kept for a manager.
- **Device binding (ADR 25):** a terminal's device key fingerprint is checked at every till sign-in.

**Web app** (English and Arabic)

- **Back office:** Products, Inventory (stock on hand, movements, adjust stock, counts, exceptions, "Check the books"), Customers, Approvals, Sales and shifts (documents, receipts, Z reports, A4 invoice), and POS settings with printers.
- **Touch till at `/pos`:**
  - Registering a terminal, PIN sign-in, and opening a shift with a denomination count.
  - Scanning (fast scans are never lost) and item tiles.
  - The cart with discounts and approval badges.
  - Customers and tax invoices.
  - Payment with cash, card with approval code and bank transfer.
  - Receipt, reprint, void, park and recall, and returns by receipt number.
  - Cash in/out, the X report, the blind close and the Z report.
  - Printer settings. Light and dark themes, right to left in Arabic.

**Windows app** (`apps/desktop`, version 0.4.0)

- It loads the POS from the server, or the bundled copy in emergency mode.
- **Receipt printers:** network printers over TCP 9100, and USB printers through the Windows spooler in RAW mode. Receipts are drawn as pictures, so Arabic always prints. The cash drawer kick is supported.
- **Emergency mode:**
  - The local store is SQLCipher, with its key protected by Windows DPAPI.
  - Numbers come from the terminal's own series (`EM-B01-T01-000001`).
  - The PIN is checked offline against the server's Argon2id hashes.
  - It uploads in order when the server is back.
- The IPC calls accept only the KITE server's origin. An offline page and a printer setup page are included.

## Tests

| Suite | Count | Where |
| --- | --- | --- |
| SQL | 13 files (2 new) on PostgreSQL 16 and 17 | `db/tests` |
| Server unit | 106 (28 new) | `tests/Kite.UnitTests/Phase4Tests.cs`: posting engine, POS arithmetic (with property tests), shared vectors, PIN hash compatibility |
| Server integration | 32 (8 new) | `Phase4Tests.cs` (catalog, stock and counts, a full shift, customers and tax invoices, the print relay against a TCP printer, emergency upload), `Phase4LoadTests.cs`, `ApiContractTests.cs` |
| Load test | 20 terminals × 40 sales | CI job `load-test` |
| Web | 38 (14 new) | `src/client`: POS arithmetic and receipt bytes, till, back office, emergency API |
| Windows app | 7 | `apps/desktop/test`: encrypted store, key protection, snapshot check, network printer, settings, offline PIN, USB RAW through a real Windows printer (CI) |
| End-to-end Phase 4 | 1 walk-through, 12 steps, 20 screenshots and an A4 PDF | `e2e/phase4.mjs`, CI job `e2e-phase4` |

**One rule, three implementations.** The POS arithmetic is checked three ways: C# on the server, TypeScript in the till, and an independent Python Decimal program. All three agree on the shared vectors in `tests/vectors/pos-math-vectors.json` (16 lines, 11 cash roundings, 4 return splits). The PIN hashes made by the server verify in the Windows app offline (`pin-hash-vectors.json`).

**Other checks:**

- `ApiContractTests` fails if the web client calls a path the server does not have.
- The proofs of concept (QuestPDF Arabic, ESC/POS raster, SQLCipher) still run on every push.

## Problems found and fixed during Phase 4 testing

- **Two tills could deadlock.** Some writers took the audit lock before the outbox lock, and others the other way round. Every writer now takes them in one order, and sales retry on a deadlock (ADR 21). The load test is the guard.
- **Posting a count was refused** (`42501`). Locking a stock row needs UPDATE rights that the app role must not have. A security-definer function, `inv.lock_balance`, now takes the lock.
- **A fully discounted sale could not be completed.** It still asked for a tender. A property test found this with a total of 0. A sale with nothing to pay now needs no tender.
- **Fast scanning lost scans.** The search box was cleared after the lookup returned, which wiped the next scan. It is now cleared when Enter is pressed.
- **A manager who registered a till stayed signed in on it.** They are now signed out, so the cashier signs in. The browser walk-through found this.
- **New error codes had no message in the web app.** The web CI job caught it, and English and Arabic messages were added.

## Decisions and departures from the blueprint

Recorded in [`architecture-decisions.md`](architecture-decisions.md):

- **ADR 18 revised:** the API client stays hand-written, with a contract test instead.
- **ADR 20:** posting rules as data, in the same transaction.
- **ADR 21:** one lock order.
- **ADR 22:** QuestPDF under its Community licence.
- **ADR 23:** receipts as ESC/POS pictures, relayed to private addresses only.
- **ADR 24:** the emergency store, snapshot and ordered upload.
- **ADR 25:** device binding.
- **ADR 26:** discount limit = the smaller of a percentage and an amount.

## Two things for Nextdab

1. **The KITE MOBILE ERP public key** (as in the Phase 3 report). Sign in once to Nextdab Admin 5.14 and copy the key from **KITE MOBILE ERP › Settings**, or from `data\KITE-MOBILE-ERP-public-key.txt`. It goes into `LicenceKeys.EmbeddedHex` in one commit. Until then, real licences cannot be accepted, and CI uses a test key in Development mode only.
2. **QuestPDF licence.** The A4 invoice uses QuestPDF. Its Community licence is free while Nextdab's annual gross revenue is under USD 1,000,000 (and Nextdab is not publicly traded). Shops that use KITE need no licence of their own. Please confirm Nextdab qualifies, or a Professional licence is needed.

## Known limits (carried forward)

1. **Phones by IMEI or serial number** are not sold yet (`CAT-003`). Device units come in Phase 5 and IMEI management in Phase 6.
2. **USB printing** was tested through the Windows spooler with a Windows printer that writes to a file. Network printing was tested against a TCP listener. A real thermal printer and drawer should be tried at the first pilot shop.
3. **Card payments.** Card machines are standalone bank terminals. The cashier enters the approval code and, at close, the batch total; there is no direct link to the card machine.
4. **An exchange** is a return plus a new sale (two documents). There is no single exchange screen.
5. **The QR** follows the blueprint (TLV tags 1–6). The exact Fawtara (Oman e-invoicing) format is confirmed in Phase 9.
6. **Emergency mode** is in the Windows app only; browser tills need the server. While offline, a till cannot see stock sold by other tills. Stock that goes negative this way is recorded as an exception for a manager.
7. **Packaging the Windows app** (installer, code signing, native modules for each Windows build) is Phase 14.
8. **Server messages are in English.** The Arabic interface translates them by error code, as before.

## Next: Phase 5

Blueprint section 25, row 5: new, used and refurbished device management, with device unit identity pulled forward (size M). The exit gate is: conditions, inspections and refurbishment costs flow to unit cost.
