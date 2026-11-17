# Phase 11 report: purchasing and accounting

KITE MOBILE ERP · Nextdab · version 0.11.0 · November 2026

## Summary

Phase 11 lets the shops buy from their suppliers and gives the accountant the books: periods, statements and the VAT return.

1. **Purchase orders.** A purchasing officer raises an order for a supplier and a branch, at the supplier's last price (the price history is shown). Orders above a user's approval limit wait for someone with a higher limit (limits per role, and per branch where set); the branch manager cannot approve above hers (PUR-006). An approved order is sent with its PDF; it can be cancelled before goods arrive and closed short after.
2. **Receiving.** Goods are received against the order at its price, never more than is still open; phones are scanned one IMEI per unit. Freight, duty and other charges from the supplier or a carrier are spread over the lines by value or quantity into the cost of stock. Each line and charge is owed to the supplier as goods received not invoiced (GRNI).
3. **Supplier invoices and the three-way match.** An invoice is matched line by line against what was received and ordered: within tolerance (2% of the price or 1.000 OMR on the line) it posts; outside, only someone with PURCH.INVOICE.OVERRIDE posts it, with a reason. The price difference goes to the stock still on hand and to purchase price variance for what has been sold. An invoice can also cover charges, outside repair work and expenses. Head office can book one invoice for goods several branches received; each branch clears its own GRNI through interbranch lines. A supplier's invoice number is entered once (PUR-008).
4. **Returns and debit notes.** Goods go back to the supplier off GRNI if not yet invoiced, otherwise on a debit note at the invoiced price with its VAT.
5. **Payments.** A payment settles the paying branch's oldest invoices, or the invoices head office names at any branch; what is left is an advance for the next invoice. Statements and ageing show what is open after payments.
6. **Expenses and card settlements.** Expenses (rent, utilities, wages and so on) with their VAT, paid in cash, from the bank or on credit; the bank's card settlement with its fee against the card clearing account (ACC-010 when it does not agree).
7. **The chart of accounts and manual journals.** The accountant adds and edits accounts (cash-flow class, VAT-relevant, control account). Manual journals are drafted, checked and posted; above the threshold a second person approves (the author cannot, ACC-005); posting to a control account needs its own permission; a posted journal is corrected by its reversal.
8. **Periods.** Each month is open, soft-closed or hard-closed per branch; a document dated in a closed month posts to the first open one and says so. Year-end closes the year into retained earnings at head office.
9. **Statements.** Trial balance, ledger, profit and loss, balance sheet and cash flow, for a branch or consolidated (interbranch eliminated), each saying how recent each branch's data is.
10. **The VAT return.** Worked out from the documents for the quarter, box by box, beside the VAT accounts' own movements; filed when the quarter's months are closed at every branch, which moves VAT output and input into VAT payable branch by branch; then paid or refunded.
11. **The daily ledger check** now covers customers, suppliers, GRNI, inventory, VAT output and input, store credit, deposits, repair WIP, each branch's trial balance and interbranch.

All screens are in English and Arabic.

## Exit gate (blueprint section 25, row 11)

"Full chart of accounts UI, periods, VAT return, reconciliation jobs … Three-way match; trial balance and subledger reconciliation clean." The test table adds: "Accounting reconciliation | Daily reconciliation after all scenarios | Subledgers equal control accounts; trial balance balances; interbranch eliminates."

**In the database** (`db/tests/19_phase11_purchasing_accounting.sql`): a period closed at one branch stops that branch's journals and lines, not another's, and only the accountant posts into a soft-closed one; an order's lines change only in draft and its status moves only forward; receipts never pass the order (plus any allowance); GRNI is never cleared past what was received, and outside repair work feeds it by itself; a supplier's invoice number is booked once, an invoice never covers more than was received less returned, and is frozen once posted; allocations stay within one branch and never pass either side; manual journal lines change only in draft and the second approver is someone else; a stock cost is corrected only on stock on hand; a filed VAT return is frozen, and a nil return files without a settlement journal; a receipt, return or payment links its journal once and nothing else changes (KT082); the application cannot update what it may only add.

**The exit-gate test** (`Phase11ExitGateTests`, CI job `phase11-exit-gate`):

- **Races:** workers at once receive against one order line, post invoices for one receipt line, pay one supplier invoice and return the same goods. Nothing is received beyond the order, invoiced beyond what came in, paid beyond what is owed or returned beyond what was received; GRNI never goes below zero.
- **A full day at three branches:** orders and receipts with landed costs and IMEIs; invoices matched three ways, one outside tolerance posted with an override after a phone was sold (the difference split between stock and price variance); head office booking one invoice for goods two branches received; returns before and after invoicing; head office paying a branch's invoice; expenses; card sales and their settlement; a manual journal; a sale on account at one branch paid at another.
- **The quarter closed:** its months closed at every branch, its VAT return filed with the VAT report agreeing with the VAT accounts branch by branch, a late receipt moving to the next open month; then the ledger check over the whole database: every subledger equals its control account at every branch, each branch's trial balance balances, interbranch receivable equals interbranch payable, the balance sheets balance and the cash flow explains the cash.

CI_RESULTS

**The browser walk-through** (`e2e/phase11.mjs`, CI job `e2e-phase11`), 21 checks:

1. Karim, the purchasing officer, orders 20 cases at 1.100 and 4 phones at 150.000 from Gulf Mobile Parts (653.100 with VAT).
2. Huda, the branch manager (limit 500.000), has no Approve button, and the server refuses her approval too (PUR-006).
3. Karim (limit 2,000.000) approves and sends it; the PDF for the supplier is made.
4. The goods come in: 20 cases and the 4 phones scanned one by one, with 12.000 freight from Muscat Freight spread by value. Stock and GRNI each move 634.000; each phone costs 150.000 + 2.894 freight.
5. One phone is sold at the till (199.000) before the invoice arrives.
6. The supplier invoices the phones at 156.000, outside the 2% tolerance: Karim cannot post it. The owner posts it with a reason: 18.000 goes to the three phones on hand (now 158.894 each) and 6.000 for the one sold to price variance; GRNI is cleared.
7. The carrier invoices 12.500 for the freight (0.500 over, within the 1.000 line tolerance); two cases go back on a debit note of 2.310.
8. The owner pays the supplier 500.000 by transfer: payables 189.115, of which Gulf Mobile Parts 175.990.
9. Maha, the accountant, records Sohar's rent (300.000 + 15.000 VAT by transfer) and a 1,500.000 capital journal above the threshold, which she cannot approve herself; the owner does.
10. The quarter's months are soft-closed at every branch; the VAT return agrees with the VAT accounts and is filed: output 9.476, input 47.815 (32.300 + 0.625 on the invoices, less 0.110 on the debit note, plus 15.000 on the rent), 38.339 to be refunded.
11. The statements balance, and the ledger check finds no difference over the new ledgers.
12. Purchasing and the statements in Arabic.

The accounts moved exactly as worked out in the script:

| Account | Balance | Why |
|---|---:|---|
| 1100 Cash | +199.000 | the phone sold |
| 1200 Bank | +685.000 | capital 1,500.000, less 500.000 to the supplier and 315.000 rent |
| 1400 Inventory: new devices | +476.682 | 3 phones at 158.894 |
| 1420 Inventory: accessories | +20.182 | 18 cases at 1.1212 |
| 1500 VAT input | 0.000 | 47.815 moved to VAT payable on filing |
| 2100 Supplier payables | −189.115 | 678.300 + 13.125 invoiced, less 2.310 and 500.000 |
| 2110 GRNI | 0.000 | everything received has been invoiced or returned |
| 2200 VAT output | 0.000 | 9.476 moved to VAT payable on filing |
| 2210 VAT payable | +38.339 | the refund due |
| 3100 Capital | −1,500.000 | the manual journal |
| 4100 Sales: new devices | −189.524 | the phone without VAT |
| 5100 Cost: new devices | +152.894 | its cost with freight |
| 5300 Purchase price variance | +6.542 | 6.000 on the phone sold, 0.500 on the freight, 0.042 on the cases returned |
| 6100 Rent | +300.000 | Sohar's rent |

The audit chain verifies. Screenshots of every step are in the CI artifact `e2e-phase11-screenshots`.

## What was built

**Database** (`V0020__purchasing_accounting.sql`):

- purchasing: the policy (tolerances, over-receipt, approved suppliers per branch), approval limits per role and branch, purchase orders and their lines, goods receipts with lines, units and charges, GRNI entries, supplier invoices and their lines, supplier returns and debit notes, supplier payments and allocations;
- accounting: the accounting policy (manual journal threshold), periods per branch with soft and hard close, manual journals and their lines, expenses, card settlements, VAT returns with their adjustments, the VAT box definitions with review, the ledger check's new ledgers;
- `tax.vat_lines` (every VAT amount from the documents by journal date) and the guards on orders, receipts, invoices, allocations, periods, manual journals and VAT returns, including `purch.journal_once`;
- number series PO, GR, PI, DN, PV, EX, CS and MJ for every branch;
- the permissions `PURCH.*`, `ACCT.*` and `EXP.*`, given to the purchasing officer, accountant and branch manager roles, and approval limits for them (owner unlimited, accountant 5,000, purchasing officer 2,000, branch manager 500).

**Server:**

- purchasing: orders with approval limits, send, PDF, cancel and close; receiving with IMEIs and landed costs; the three-way match, override and posting; returns and debit notes; payments and allocations; open items, ageing and price history;
- accounting: the chart, periods per branch and year-end, manual journals with a second approver and reversal, trial balance, ledger, profit and loss, balance sheet, cash flow; expenses and card settlements; the VAT return prepared, adjusted, filed and paid;
- the daily ledger check extended to every subledger;
- late documents posted into the first open period.

**Posting rules:** `purch.receipt`, `purch.invoice` (with interbranch parts), `purch.return`, `purch.payment`, `exp.expense`, `acct.card_settlement`, `vat.settlement` and `vat.payment`; manual journals (`acct.manual`) and the year-end close (`acct.year_end`) post their own lines.

**Web app:** Purchasing (orders, receipts, invoices, returns, payments and their pages), Purchasing settings, Accounting (chart, journals, manual journals, periods, card settlements, ledger check), Statements, VAT and the VAT return's page, Expenses.

**Nextdab Admin** is unchanged: Phase 11 adds no licence kind, switchable part or check-in field (the `purch`, `exp` and `acct_ui` modules were already in the licence's module list).

## Tests

| Suite | Result |
|---|---|
| Database (PostgreSQL 16 and 17) | 20 of 20 files |
| Server unit tests | 151 passed (7 for Phase 11: purchase vectors, worked examples, the new posting rules, cash-flow classes) |
| Server integration tests | 80 passed (5 for Phase 11: orders, receipts and the three-way match; returns, debit notes and payments with head office paying a branch; manual journals; periods, late documents and VAT returns; the exit gate) |
| Web (Vitest) | 98 passed in 18 files; 11 for Phase 11 (8 screen tests, one in Arabic, and 3 for the purchase arithmetic) |
| Purchase arithmetic vectors | 501 cases from an independent Python implementation, shared by server and web app; CI regenerates them and fails on any difference |
| Browser walk-through | 21 checks passed (`e2e/phase11.mjs`) |

## Problems found and fixed during Phase 11 testing

1. **Two parameters named `id` and `Id` in one query** collided (Dapper ignores case), so manual journal lines and return lines pointed at the wrong row. They have distinct names.
2. **Expenses and card settlements could not be linked to their journal** after insert, because their rows can never be updated. The journal is posted first and the row inserted with it (ADR 86).
3. **The application could not link a receipt, return or payment to its journal.** It may now update that one column, once (ADR 86).
4. **A nil VAT return needed a journal of zeros.** It is filed without one (ADR 87).
5. **Earlier tests counted every fiscal period and number series**, which now include other years'; they count the current year's.
6. **Supplier ageing showed invoices as open after they were paid.** It uses what is open after allocations.
7. **The dashboard still showed the "Phase 2 foundation" notice.** Removed.
8. Smaller fixes: amounts in table rows repeated their labels; the Accounting menu item stayed highlighted on Statements and VAT; the web app's in-memory demo numbered documents `PO-B01-B01-…`.

## Decisions and departures from the blueprint

- **ADR 78:** purchasing takes its locks after credit and before numbers, in one order.
- **ADR 79:** periods are closed per branch; a late document lands in the first period open at every branch it touches.
- **ADR 80:** goods received not invoiced is its own ledger, per source line.
- **ADR 81:** an invoice's price difference goes to stock for what is on hand and to price variance for the rest.
- **ADR 82:** a supplier invoice is booked at one branch; the goods' branches clear their own GRNI through interbranch lines.
- **ADR 83:** supplier payments settle the oldest open items at the paying branch, unless head office names them.
- **ADR 84:** returns come off GRNI first, then on a debit note at the invoiced price.
- **ADR 85:** the VAT return is worked out from the documents by journal date and settled branch by branch.
- **ADR 86:** documents that must not change are written once and link their journal once.
- **ADR 87:** a nil VAT return is filed with no settlement journal.
- **ADR 88:** the daily ledger check compares each subledger with what its own documents posted.
- **ADR 89:** statements are as of each branch's latest data and consolidate by eliminating interbranch.
- **ADR 90:** the year is closed once, at head office, into retained earnings.
- **ADR 91:** purchase arithmetic is shared and checked against an independent implementation.

Input VAT is reclaimed at the branch that books the invoice, not where the goods went (ADR 82). The VAT return is filed per business as the Tax Authority requires; it is settled in the books branch by branch.
