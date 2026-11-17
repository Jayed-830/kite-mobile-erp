# Phase 10 report: customer credit and suppliers

KITE MOBILE ERP · Nextdab · version 0.10.0 · November 2026

## Summary

Phase 10 lets the shops sell on account, safely at every branch, and keeps one list of suppliers.

1. **Credit accounts.** Head office opens an account for a customer: a limit, payment terms (30 days by default), an optional cap per offline sale, a note, and an allowance for each branch (what that branch may sell on credit while it cannot reach head office; together no more than the limit unless the policy allows it). An account can be put on hold with a reason, released, closed and reopened.
2. **Sales on account at the till.** The cashier picks the customer, sees the limit, what is owed and what is available, and charges all or part of the sale on account, in up to 12 instalments a chosen number of days apart. Over the available credit a manager approves with their PIN, up to the override cap (50.000 by default); past the cap, on hold or closed, the till refuses. The receipt shows the line "On account / على الحساب". Returns go back on account, never more than the sale charged there.
3. **Repairs on account.** A repair can be collected on account at the till (the rest after the deposit), with the same rules and approvals. A delivered repair invoice corrected at the till goes back on account first, and the corrected invoice can be charged on account again, checked against the credit coming back.
4. **Online and offline.** Head office decides every credit sale under one lock per customer. A branch server asks head office first (it has 2 seconds to answer and reserves the amount); when head office cannot be reached, the branch decides by itself within its allowance, the per-sale cap and what it last heard. When the branch's sales reach head office, head office finds any customer now over the limit, records the breach and puts the account on hold until the cause clears.
5. **Payments.** A customer pays at the till (on the drawer) or at the office, in cash, by card or by transfer, at any branch. Payments settle the oldest instalments first (or the ones chosen). A payment received at one branch for a sale made at another moves the receivable between the branches with an interbranch journal.
6. **Credit control.** New credit needs a manager when an instalment is more than 30 days overdue; after 60 days head office holds the account by itself and lifts the hold when it is paid. Customers who agreed to WhatsApp get a reminder before an instalment is due and then every 7 days while it is overdue. Debts can be written off to bad debts; opening balances bring in what customers owed before KITE.
7. **Statements and reports.** A statement for any period and branch (on screen, printed or as a PDF), ageing (not due, 1–30, 31–60, 61–90, over 90 days, in advance), exposure by branch, credit approvals and offline sales, and breaches.
8. **Suppliers.** One list for the business, numbered by head office (S-00001). A branch that is offline can add a supplier provisionally; head office approves it or merges it into one it already has. Opening balances owed to suppliers are recorded per branch. Purchase orders, receipts, supplier invoices and payments come with purchasing (Phase 11).
9. **The ledgers are checked against the books every day.** Each branch's customer ledger against receivables (1300) and supplier ledger against payables (2100); a difference is reported, never corrected.

All screens are in English and Arabic.

The exit gate passed on GitHub Actions. CI run `CI_RUN` on commit `CI_COMMIT` has all twenty-four jobs green, including the new Phase 10 exit gate and the Phase 10 browser walk-through (`e2e/phase10.mjs`, 23 checks against a real server, PostgreSQL and Chrome).

## Exit gate (blueprint section 25, row 10)

"Online and offline credit rules pass; statements reconcile." The test table adds: "Customer credit | Online limit; two branches offline both selling on credit | Allowances respected offline; breach detected and hold applied on sync."

**In the database** (`db/tests/18_phase10_credit_suppliers.sql`): ledger rows, instalments and allocations are never changed; instalments add up to their charge; an allocation never settles more than an instalment or a payment holds, and a transfer never acts as a payment; branch allowances add up to no more than the limit (unless the policy allows it), also when the limit is lowered; a decision over the limit always names who approved it; a sale is decided once; a merged supplier stays merged and only into an active one; the application cannot update what it may only insert.

**The exit-gate test** (`Phase10ExitGateTests`, CI job `phase10-exit-gate`), with KITE_P10_TILLS tills at head office, KITE_P10_BRANCHES branch servers over mutual TLS and KITE_P10_CUSTOMERS customers:

- **Online:** the tills (cashiers without the override) and the branch servers asking head office race, KITE_P10_ATTEMPTS attempts each per customer, to sell on account to the same customers. No customer ever owes more than the limit; every sale written has its charge, instalments and decision; the branch servers' reservations become charges when pushed.
- **Offline:** each branch server sells on its own copy within its allowance, the per-sale cap and the exposure it last heard. Pushed together, head office finds every customer whose exposure passed the limit: the breach is recorded, the account goes on hold, the hold reaches every branch with its heartbeat and head office refuses online; a customer still within the limit stays active. Payments that bring a customer back under the limit lift the automatic hold.
- **Statements reconcile:** each statement's closing balance is the opening balance plus its lines and equals the customer ledger; head office's ledger equals receivables (1300) customer by customer and branch by branch; the daily ledger check finds no difference.

GATE_RESULTS

**The browser walk-through** (`e2e/phase10.mjs`, CI job `e2e-phase10`):

1. Head office sets the offline cap per sale to 80 and opens an account for Khalid Al Rashdi Trading: limit 300, 30 days, allowances 180 at Ruwi and 120 at Sohar.
2. At the till a senior cashier sells a smart watch (120.000) on account in three instalments of 40.000 (receipt: "On account").
3. A tablet (200.000) is 20.000 over the 180.000 left: Huda, the branch manager, approves with her PIN; the decision records 20.000 over, approved by her.
4. Another watch on account is 120.000 over with nothing left, more than a manager may approve (50.000): refused (CRD-003), nothing charged.
5. A payment of 100.000 in cash at the till goes on the drawer and settles the oldest instalments.
6. At the office a transfer of 50.000 received at Sohar settles Ruwi's next instalment: one interbranch journal.
7. A hold ("Cheque returned by the bank") stops credit at once — the till shows it and offers no credit — and is released.
8. The statement (opening + lines = closing = 170.000) and its PDF; ageing, exposure by branch, and approvals (Huda's approval listed).
9. A supplier, Gulf Mobile Parts LLC (S-00001), with an opening balance of 400.000 owed at Ruwi.
10. The ledger check: customers at Ruwi 170.000 and Sohar 0.000, suppliers at Ruwi 400.000, all against the books: everything agrees.
11. The account and suppliers screens in Arabic.

The accounts moved exactly as worked out in the script:

| Account | Moved | Why |
|---|---:|---|
| 1300 Receivables | +170.000 | 120 + 200 on account, less 100 and 50 paid |
| 1310 Due from other branches | +50.000 | Ruwi is owed by Sohar |
| 2500 Due to other branches | −50.000 | Sohar holds Ruwi's 50.000 |
| 1100 Cash | +100.000 | the payment on the drawer |
| 1200 Bank | +50.000 | the transfer received at Sohar |
| 2200 VAT | −15.238 | 5.714 + 9.524 on the two sales |
| 4120 Sales | −304.762 | the two sales without VAT |
| 2100 Payables | −400.000 | the supplier's opening balance |
| 3900 Opening balances | +400.000 | its other side |

The customer ledger is 170.000 (Ruwi 170.000, Sohar 0.000), the ledger check finds no difference and the audit chain verifies. Screenshots of every step are in the CI artifact `e2e-phase10-screenshots`.

## What was built

**Database** (`V0019__credit_suppliers.sql`):

- the credit policy (one per business), credit accounts with their holds and sync sequence, branch allowances;
- receivables in the customer ledger (charge, payment, credit note, write-off, opening balance, transfer), instalments, allocations, credit decisions, head office's reservations for branch servers, breaches, hold events, customer receipts, reminders, the sync state with head office;
- the supplier schema: suppliers (approved, provisional, merged), the supplier ledger per branch, its sync state;
- the daily ledger check's results;
- the functions `crm.receivable_status`, `crm.credit_exposure` and `purch.supplier_balance`;
- the number series for write-offs (WO), opening balances (OB) and provisional suppliers (SP), and the business's supplier numbers (S-);
- the permissions `CREDIT.VIEW`, `CREDIT.MANAGE`, `CREDIT.RECEIVE`, `CREDIT.OVERRIDE`, `SUPPLIER.VIEW`, `SUPPLIER.MANAGE`, `SUPPLIER.APPROVE`; the WhatsApp templates `credit_due` and `credit_overdue`.

**Server:**

- the credit check at the till, head office's evaluation and reservations, the branch server's offline rule;
- charges with instalments, credit notes on account, payments, allocation, write-offs, opening balances;
- interbranch settlement journals posted in one transaction (`Posting.PostManyAsync`);
- the branch link: accounts and suppliers with the heartbeat, push of sales, payments and suppliers, breaches and automatic holds;
- statements and their PDF, ageing, exposure, approvals and breaches;
- the hourly worker (overdue holds, reminders, the daily ledger check);
- the supplier endpoints (list, add, edit, approve, merge, opening balance, statement, ageing);
- repair collection and correction on account.

**Posting rules:** `credit.payment`, `credit.write_off`, `credit.interbranch_out` and `credit.interbranch_in` (the two sides, in one journal `credit.interbranch`), `credit.opening` and `supplier.opening`; the on-account tender posts to 1300 with the customer as the party.

**Web app and till:**

- Customer credit: accounts, an account's page (instalments, payments waiting, ledger, holds, breaches, statement), ageing, by branch, approvals and breaches, ledger check;
- Credit settings; Suppliers and a supplier's page (approve, merge, opening balance, statement);
- at the till: On account in the payment, instalments, the manager's approval, the hold shown, Credit payment on the drawer, On account on the receipt, and on account at the repair counter.

The shift's figures add sales on account and credit payments taken.

**Nextdab Admin** is unchanged: Phase 10 adds no licence kind, switchable part or check-in field (the `credit` and `purch` modules were already in the licence's module list).

## Tests

| Suite | Result |
|---|---|
| Database (PostgreSQL 16 and 17) | 19 of 19 files |
| Server unit tests | 144 passed (credit vectors, the new posting rules, upgrades from earlier phases) |
| Server integration tests | 75 passed (6 for Phase 10: sales on account, instalments and payments across branches, branch servers online and offline, suppliers, repairs on account, and the exit gate) |
| Web and till (Vitest) | 87 passed in 16 files; 9 for Phase 10 (6 screen tests, one in Arabic, and 3 for the credit arithmetic) |
| Credit arithmetic vectors | 442 cases from an independent Python implementation, shared by server and web app; CI regenerates them and fails on any difference |
| Browser walk-through | 23 checks passed (`e2e/phase10.mjs`) |

## Problems found and fixed during Phase 10 testing

1. **The application could update rows it may only add** through PostgreSQL's default privileges. `UPDATE` is now revoked on every ledger, instalment, allocation and decision table; a SQL test covers it.
2. **The general row trigger raised a credit account's version** when a branch wrote head office's copy or the ledger changed its figures, which made the next edit at head office fail as a conflict. Credit accounts have their own trigger: the version moves only when terms change.
3. **A transfer between branches could have been counted as a payment** when allocating. Transfers are excluded from what can settle an instalment.
4. **The manager's approval for credit could not be given at the till**: `CREDIT.OVERRIDE` was missing from the actions a manager approves with a PIN.
5. **The payments endpoint had no access rule** (the API contract test caught it).
6. **Dates came back with a time** (`2026-12-17T00:00:00`) on an account's page; they are sent as dates.
7. **Collecting a repair with a payment on account failed with an internal error.** Repair collection and correction now take it, with the same checks as a sale (see Summary, item 3).
8. **The Phase 7 exit gate summed every credit-ledger row**, which now includes receivables; it reads store credit only.
9. **The till's Register button could send an empty branch** before the branches had loaded (the Phase 9 walk-through failed on it once on CI). The button waits for a branch.
10. Smaller fixes: the receipt showed the method's code ("credit") instead of "On account"; statement cells drawn as inline blocks broke the table's lines; the ageing table's headers.

## Decisions and departures from the blueprint

- **ADR 68:** head office decides credit online, under one lock per customer; the lock's place in the order.
- **ADR 69:** offline, a branch sells on credit within its allowance, a cap per sale and what it last knew.
- **ADR 70:** a breach is found on sync and holds the account until its cause clears.
- **ADR 71:** receivables are the customer ledger, in instalments, settled oldest first at head office.
- **ADR 72:** a payment received at another branch is settled between branches in one journal.
- **ADR 73:** branch servers keep a copy of the credit accounts, sent with the heartbeat.
- **ADR 74:** suppliers are one list for the business; a branch may add one for head office to approve or merge.
- **ADR 75:** the customer and supplier ledgers are checked against the books every day.
- **ADR 76:** overdue instalments need a manager, then hold the account, with WhatsApp reminders.
- **ADR 77:** credit arithmetic is shared and checked against an independent implementation.

The supplier ledger holds opening balances only in this phase: supplier documents and three-way matching are Phase 11.

## Things for Nextdab

1. Nothing to install in Nextdab Admin for this phase.
2. Customer credit and Suppliers are modules: switch them on in Modules, then set Credit settings (terms, overdue days, offline cap, override cap) at head office before opening accounts.
3. Branch managers can approve credit over the limit with their PIN; a senior cashier sells on account but cannot go over. Only head office opens accounts, changes limits and holds.

## Known limits

1. Purchase orders, receipts, supplier invoices and supplier payments come with purchasing (Phase 11).
2. The interbranch accounts (1310, 2500) are settled by the accountant; there is no settlement document between branches yet.
3. Emergency mode at the till cannot sell on account (it has no credit data).
4. A branch's copy of an account is as old as its last heartbeat; two branches offline at the same time can together pass the limit, which is found and held on sync (by design, ADR 69 and 70).
5. Payments taken on a branch server are allocated when head office receives them.
6. Store credit stays per server, as in Phase 7.

## Next: Phase 11

Purchasing and accounting: "Three-way match; trial balance and subledger reconciliation clean" — purchase orders, goods receipts and supplier invoices matched, supplier payments, the full chart of accounts screen, periods and the VAT return.
