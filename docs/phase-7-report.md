# Phase 7 report: trade-in management

KITE MOBILE ERP · Nextdab · version 0.7.0 · November 2026

## Summary

Phase 7 lets a shop buy phones from its customers. At the till, the cashier presses F6 and the trade-in wizard walks through six steps:

1. the customer who is selling;
2. the phone, with its IMEI checked against the blocked list and head office;
3. the inspection, which suggests deductions;
4. the value, from the price guide, with a manager's approval when it is above the cashier's limit;
5. the customer's consent;
6. how the value is settled.

The phone goes into used stock at once, waiting for inspection. The value is settled in any of the blueprint's four ways:

- applied to a sale, with the customer paying the rest;
- applied to a sale, with the excess left as store credit or paid in cash;
- store credit only;
- a cash buy-back.

Store credit is a new ledger that customers spend at the till.

Once the business records the Tax Authority's approval, phones bought from the public are resold under the profit margin scheme: VAT is charged on the margin only and is not shown on the receipt.

The exit gate passed on GitHub Actions. CI run `43875843484` on commit `1c05dd6` has all eighteen jobs green. That includes the new Phase 7 exit gate and the Phase 7 browser walk-through.

## Exit gate (blueprint section 25, row 7)

"All settlement types post correct journals." The test table adds: "Journals and VAT match the expected tables" for all four settlement types and a margin-scheme sale.

**The exit-gate test** (`Phase7ExitGateTests`, CI job `phase7-exit-gate`) runs 20 tills at the same time, 10 rounds each, on one server and PostgreSQL. In each round a till:

- takes a phone in at a random value;
- settles it one of the four ways;
- sometimes voids the sale that used the credit (the credit comes back);
- sometimes lets a customer spend store credit (four customers are shared by all tills, so tills compete for the same balances).

Then 40 traded-in phones are inspected and resold under the margin scheme at once. A model in the test records what every successful operation should post. Afterwards the test compares the model with the accounts, every trade-in, every customer's balance and every drawer. It also runs two races: 20 tills spending one trade-in's credit at the same moment, and 20 tills spending one customer's store credit at the same moment.

| Account (CI run `43875843484`, commit `1c05dd6`) | Expected by the model | Moved in the books |
| --- | ---: | ---: |
| 1410 Used device inventory | 13,947.789 | 13,947.789 |
| 2320 Trade-in clearing | −1,182.964 | −1,182.964 |
| 2310 Store credit | −2,271.365 | −2,271.365 |
| 1100 Cash | 2,015.283 | 2,015.283 |
| 2200 Output VAT (standard and margin) | −595.647 | −595.647 |

| Requirement | Status | Evidence (same run) |
| --- | --- | --- |
| Every settlement type posts the right journals | Done | 202 trade-ins accepted and 183 fully settled, with 162 sales, 18 voids and 57 store-credit sales: all five accounts above match to the baisa. |
| Clearing equals what is owed | Done | Account 2320 equals the sum of what accepted trade-ins still owe; each of the 202 trade-ins shows the balance and status the model expects. |
| Store credit never overspent | Done | Account 2310 equals everyone's store credit; no customer is below zero. Of 20 tills spending 40.000 each from a 100.000 balance at once, exactly 2 succeeded and 18 were told `TRD-006`. |
| One trade-in's credit never overspent | Done | Of 20 tills spending 60.000 each from one trade-in of 100.000 at once, exactly 1 succeeded and 19 were told `TRD-005`. |
| Margin-scheme VAT | Done | 40 resales: each line is taxed on (price − its own purchase price), and its purchase price matches the phone's latest acquisition. |
| Drawers | Done | Each of the 20 tills' expected cash equals the model's, with buy-backs taken out. |
| Stock and books agree | Done | The consistency check is clean. |

The commit before it (`7921f88`, run `43870089807`) passed the same gate with different figures, because the tills interleave differently each run.

**Browser walk-through** (`e2e/phase7.mjs`, CI job `e2e-phase7`). It runs a real server, PostgreSQL, the Nextdab stub and Chrome, with a cashier at the till and the owner in the back office. Every figure is worked out in the script, and the accounts are read straight from the database. The screenshots are attached to the report message.

1. **Margin scheme.** The owner records the Tax Authority's approval (reference TA/MS/2026/0142) in Tax codes. VAT-MARGIN switches on.
2. **Price guide.** The owner sets 90.000 for a grade-A Galaxy A55. The five default deductions are shown with the checklist items that suggest them.
3. **Trade-in 1: applied to a sale, the excess left as store credit.**
   - The cashier puts a 2.500 cable in the cart and presses F6.
   - A new customer, Khalid, sells his Galaxy. His IMEI is checked with head office and the blocked list.
   - The screen fails the inspection, so the 30% screen deduction is suggested: the offer is 90.000 − 27.000 = 63.000.
   - 63.000 is above the cashier's limit of 50.000. The manager approves it with her PIN.
   - Khalid types his name to confirm the declaration. The trade-in is accepted and the self-invoice SI-B01-2026-000001 is issued.
   - At payment, the trade-in pays the whole 2.500. The 60.500 left over goes to Khalid's store credit, approved by the manager's PIN.
4. **Trade-in 2: cash buy-back.** Khalid sells a second Galaxy for 50.000, confirmed on a paper form. The manager approves paying it out of the drawer.
5. **Store credit spent.** Khalid buys the new Galaxy (160.000) with his 60.500 store credit and 99.500 cash.
6. **Margin-scheme resale.** The first traded-in phone is inspected and put on sale at 130.000. It sells under the margin scheme:
   - VAT = (130.000 − 63.000) × 5/105 = 3.190;
   - the receipt shows no VAT amount, a "Margin scheme items" total and the margin-scheme note in English and Arabic.
7. **Back office.** The trade-ins list, trade-in 1 with its two settlements (2.500 on the sale and 60.500 as store credit), the civil ID shown to the owner (and audited), the value-against-resale report, Khalid's store credit, and the trade-in page in Arabic.
8. **The books.**

| Account | Moved | Expected |
| --- | ---: | ---: |
| 1410 Used device inventory | 50.000 | 63.000 + 50.000 in, 63.000 out at its cost |
| 2320 Trade-in clearing | 0.000 | everything settled |
| 2310 Store credit | 0.000 | 60.500 raised and spent |
| 1100 Cash | 179.500 | 99.500 + 130.000 − 50.000 |
| 2200 Output VAT | −10.928 | 0.119 + 7.619 standard, 3.190 margin |

The shift's expected cash is 100.000 + 99.500 + 130.000 − 50.000, and the consistency check is clean.

## What was built

**Database** (`V0016`, SQL test `15_phase7_tradein.sql`)

- **Trade-ins (ADR 44):**
  - `tradein.tradein`: the document, its lifecycle and its checks. For example, an accepted trade-in has a device, a value and consent; a margin purchase has a self-invoice and no seller VAT number.
  - A trigger freezes an accepted trade-in.
  - `tradein.tradein_issue`: the deductions applied.
  - `tradein.settlement`: immutable. A trigger keeps the settled total between zero and the agreed value and sets the status (ADR 45).
- **Valuation (ADR 48):** `tradein.settings` (staff limit, percentage over the guide, civil ID required, the declaration in English and Arabic), `tradein.price_guide` and `tradein.deduction`.
- **Store credit (ADR 46):** `crm.credit_ledger`, which never goes below zero, and `crm.store_credit_balance`.
- **Margin scheme (ADR 47):**
  - `tax.margin_scheme`: the approval, with its reference and date.
  - `device.acquisition`: how and at what price a device was bought.
  - `device.margin_purchase_price`.
  - Sale lines record the tax scheme and the margin purchase price.
- **Devices (ADR 49):** `device.reacquire` (sold → under inspection, at the new cost, with event "traded in"); head office retires a sold holder as "superseded".
- **Payments:** the new methods "trade-in" and "store credit", and a new cash event "trade-in payout".
- **Numbers and permissions:**
  - Number series TI- (trade-in), SI- (self-invoice) and SC- (store credit).
  - Six permissions, `TRADEIN.CREATE`, `APPROVE`, `STORE_CREDIT`, `PAYOUT`, `VIEW` and `SETTINGS`, added to the role templates.

**Server**

- **Trade-in API:**
  - create (IMEI checked against the blocked list, this business's devices and head office online);
  - update (device, inspection, grade, deductions, agreed value);
  - accept (approval, consent, cloud lock, civil ID, then the phone into stock, the acquisition, the self-invoice and the journal, all in one transaction);
  - settle (store credit, or cash at a till with an open shift);
  - reject and cancel;
  - show the civil ID (audited);
  - the open trade-ins of a customer;
  - the document: a bilingual A4 PDF, either the self-invoice or the purchase receipt.
- **Settings:** limits and declaration, the price guide, deductions, the value-against-resale report, a customer's store credit, and the margin scheme approval (which switches VAT-MARGIN).
- **Posting rules:** `tradein.accept`, `tradein.store_credit` and `tradein.payout`. The tenders "trade-in" (2320) and "store credit" (2310) are added to the sale rules; rules stored by an earlier version are upgraded.
- **Arithmetic:** `Kite.Sales.TradeInMath` (valuation, approval, margin VAT, how much a sale takes), with vectors written by an independent Python implementation: 166 cases, also checked in TypeScript.
- **At the till:**
  - Trade-in and store-credit payments are checked under lock (the same customer, within what is left, within the balance).
  - A void gives the credit back.
  - Margin-scheme lines are priced on the margin; the receipt and the A4 invoice show no VAT for them; the QR code carries only standard VAT.
  - Shift figures show trade-in credit, store credit and trade-in payouts. Expected cash takes payouts out.
- **Emergency mode (ADR 52):** credit payments are refused; margin-scheme phones sell from the snapshot.
- **One lock order (ADR 53),** with retries on deadlock.
- **Error codes `TRD-001` to `TRD-008`,** with English and Arabic messages.

**Web app** (English and Arabic)

- **The till:**
  - "Trade-in (F6)", the six-step wizard.
  - At payment, the customer's open trade-ins and store credit balance. A trade-in taken in for the cart is applied at once.
  - After a sale, whatever is left on a trade-in is settled as store credit or cash, or left for later.
  - Margin-scheme phones show their total without VAT.
  - X and Z reports show the new figures.
- **Back office:**
  - Trade-ins: a list with status and search, the value-against-resale report, and each trade-in with its seller, device, values, consent, settlements, document, the civil ID for managers, reject and cancel.
  - Trade-in settings: limits, declaration, the price guide grid, deductions.
  - The margin scheme approval on the Tax codes page.
  - Store credit on the Customers page.
- **`@kite/pos`:** the trade-in arithmetic, the margin pricing and the new payment methods.

**Windows app** (version 0.7.0): margin-scheme phones in emergency mode, as above.

## Tests

| Suite | Count | Where |
| --- | --- | --- |
| SQL | 16 files (1 new) on PostgreSQL 16 and 17 | `db/tests/15_phase7_tradein.sql`: margin approval rule, open and frozen trade-ins, consent, acceptance with unit, movement and acquisition, settlements and over-settling (`KT031`), store credit below zero (`KT032`), re-acquiring a sold phone, margin price, the app role's rights |
| Server unit | 127 (11 new) | `Phase7Tests.cs`: the shared vectors, a property test, margin pricing, payment checks, the posting rules and their upgrade |
| Server integration | 52 (4 new) | `Phase7Tests.cs` (3: the four settlement types with their journals and a margin resale; reconnecting a sold phone and every refusal, approval, consent, void and seller-VAT rule; valuation and the margin scheme following its approval) and `Phase7ExitGateTests.cs` (1) |
| Exit gate | 20 tills × 10 rounds, plus two races | CI job `phase7-exit-gate` |
| Web | 61 (6 new) | `tradein.test.ts` (the vectors in TypeScript, margin pricing, credit payments) and `phase7.test.tsx` (the wizard to store credit; the wizard to a sale with the rest settled by a manager's PIN; the margin approval, trade-ins and settings pages) |
| End-to-end Phase 7 | 1 walk-through, 24 screenshots | `e2e/phase7.mjs`, CI job `e2e-phase7` |

## Problems found and fixed during Phase 7 testing

- **Twenty tills deadlocked.** Accepting a trade-in as store credit took the journal-number lock and then waited for the customer's credit lock; settling another trade-in did the opposite. Found by the first exit-gate run at 20 tills. Every writer now takes its locks in one order (ADR 53), and accepting and settling retry. The same run then had no deadlock at all.
- **Catalog look-ups failed after the margin field was added.** The database library could not build a priced item once it had one more field. Found by the integration tests; the query now supplies the field.
- **The manager approval dialog stayed open** after approving from the trade-in wizard. Found by the walk-through. The till now closes the dialog itself once the manager approves. This also covers voids.
- **A sale's details did not include the margin purchase price.** Found by the walk-through's arithmetic check.
- **The API contract test could not read a combined route** for reject and cancel. They are now two plain routes.
- **The walk-through found that 63.000 needed a manager.** This was the rule working as designed: the cashier's limit is 50.000. The script was written expecting no approval. It now shows the approval at the value step.

## Decisions and departures from the blueprint

Recorded in [`architecture-decisions.md`](architecture-decisions.md):

- **ADR 44:** a trade-in is a document; accepting it puts the phone in used stock in the same step.
- **ADR 45:** the four settlement types are three kinds of settlement against one clearing account (2320).
- **ADR 46:** store credit is pulled forward from Phase 10 as a ledger that never goes below zero.
- **ADR 47:** the margin scheme follows the Tax Authority's approval. The price is VAT-inclusive even when the business prices exclude VAT, and its VAT is never shown.
- **ADR 48:** the value is the price guide less deductions; above the limit a manager approves.
- **ADR 49:** a phone the business sold comes back to its own record; head office supersedes the sold record instead of opening a conflict.
- **ADR 50:** the civil ID is encrypted on the server that took it, and only managers see it.
- **ADR 51:** consent is recorded with the declaration that was shown.
- **ADR 52:** trade-ins and credit payments need the server; emergency mode refuses them.
- **ADR 53:** one lock order for trade-ins and store credit.

## Things for Nextdab

1. **The business's accountant should confirm the wording** before shops use the margin scheme:
   - the note printed on margin-scheme receipts;
   - the self-invoice;
   - the default seller's declaration.

   Each is in English and Arabic and can be changed in one place.
2. **Nextdab Admin needs no change for Phase 7.** Trade-ins use the module code `tradein`, which has been in KITE's module list since the start, so licences name it the same way as before. Nothing new goes to Nextdab at check-in: no customers, civil IDs or devices.
3. **The draft release "dev-package-cache"** on GitHub should still be deleted by hand, if not done yet.

## Known limits

1. **Store credit has no expiry,** no manual adjustment and no statement yet. They come with customer credit in Phase 10.
2. **The VAT return boxes for margin-scheme sales** come with the VAT return in Phase 11. The sale lines already record the scheme and the purchase price.
3. **Civil IDs are encrypted per server.** Head office cannot read a branch's civil IDs until data sync in Phase 12.
4. **Cash is paid out only at a till with an open shift.** The back office can settle what is left as store credit only.
5. **The trade-in document is an A4 PDF.** There is no thermal print of it yet.
6. **A return refunds a sale paid with trade-in credit in cash, to the card, by transfer or as store credit,** never back to the trade-in. A void of the same shift does give the credit back.
7. **The value-against-resale report shows the first resale** of each traded-in phone.
8. **Notes written by the server are in English,** as in Phase 6.

## Next: Phase 8

Blueprint section 25, row 8: repair and servicing management (customer devices, the WhatsApp gateway on the main server). The exit gate is: every status transition, estimate approval, deposit and delivery tested.
