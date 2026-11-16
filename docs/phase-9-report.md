# Phase 9 report: spare parts and technician commissions

KITE MOBILE ERP · Nextdab · version 0.9.0 · November 2026

## Summary

Phase 9 makes the workshop's parts and the technicians' commissions exact.

1. **Parts held for jobs.** When an estimate is approved, its parts are held in the branch's stock for the job. What the branch does not have becomes a parts request, and the job waits for parts. Arriving stock goes to the oldest request first; the job then moves to in progress by itself. The till cannot sell parts that are held for jobs or that jobs are waiting for. The **Parts needed** page lists what the workshop is waiting for.
2. **Serial-tracked parts.** A part such as a logic board can be tracked by serial number. It is estimated and charged by quantity and fitted by scanning its serial, at that unit's own cost; returned, the same unit goes back to stock.
3. **Outside work.** Work done by another workshop is recorded at cost into the job's work in progress, owed to the supplier (2110), and goes to its own cost account (5210) at delivery.
4. **Commission rules.** A rule pays a fixed amount, a percentage of labour or a percentage of repair profit (labour and parts charged, without VAT, less parts cost and outside work), with a minimum and maximum per job, from a date. The most specific rule in force on the invoice date pays: a technician on a kind of repair, then a technician, a branch and kind, a kind, a branch, everyone. A rule is never edited; a change is its next version.
5. **Shared jobs.** When technicians share a job, their shares must make 100%; each is paid by their own rule.
6. **Generation.** Commissions are generated at delivery (or when the invoice is paid, if the business chooses), in the same transaction as the invoice, with the figures they were worked out from and a journal (Dr 6300 / Cr 2400). A second commission for the same job, technician and invoice is impossible, however often the generator runs.
7. **Approval and payout.** Commissions are approved by the branch manager or only at head office, as the business sets, and paid by cash, bank transfer or through payroll (2410), one payout line for each commission. A commission is reversed by its exact opposite; a paid one's reversal comes off the next payout. A warranty claim within the rework window takes back the business's chosen share of the original technician's commission, once.
8. **Correcting a repair invoice at the till** (the owner's choice for this phase). A manager credits the delivered invoice in full; the money goes back from the drawer or to store credit, and a corrected invoice can be issued and paid there. The job keeps its record, and its commissions are reversed and generated again from the corrected invoice.
9. **Reports.** A technician's statement (payable at the start, each commission, each payout, payable at the end), technician performance (jobs, revenue, turnaround, warranty claims, commission) and rule usage.

The exit gate passed on GitHub Actions. CI run `43982367957` on commit `fe1ad95` has all twenty-two jobs green, including the new Phase 9 exit gate and the Phase 9 browser walk-through (`e2e/phase9.mjs`, 33 checks against a real server, PostgreSQL and Chrome).

## Exit gate (blueprint section 25, row 9)

"Parts costing and commissions exact; duplicate generation impossible." The test table adds: "Each basis, shared jobs, credit-noted job, generator run twice — correct amounts; reversal posted; one accrual only."

**In the database** (`db/tests/17_phase9_parts_commissions.sql`): reservations never exceed what is on hand and are never deleted; raising a reservation through an upsert counts once; requests and their order; serial parts fitted, returned and written off; outside work reversed in full, once; a reversal is the exact opposite of its commission; a commission is never edited and moves only accrued → approved → paid, or to reversed; one payout line per commission, at its amount, adding up to the payout; two active rules of the same scope never overlap.

**The exit-gate test** (`Phase9ExitGateTests`, CI job `phase9-exit-gate`) runs 4 branches at the same time, 3 tills each, 6 repairs per till, with a scarce part at one unit cost:

- the workers approve twice as many repairs as there are parts: exactly half hold a part and half wait with a request;
- stock arrives while four fillers give it out and every till tries to sell the part: every waiting job gets its part and moves on exactly once, and the tills sell only the spare ones;
- jobs are fitted, some with outside work, a quarter shared 50/50 between two technicians, delivered and paid;
- a third technician's rule is written after delivery and the generator runs ten times at once: one commission for each of that technician's jobs;
- a third of the invoices are corrected at the till (half re-invoiced with less labour and paid in cash, half credited to store credit), and one correction is sent from every till of its branch at once: it happens once;
- a serial part is fitted by two workers at once: one succeeds;
- commissions are approved and each technician's payout is sent from every till at once and once more through payroll: one payout each.

On CI the gate ran 72 repairs in 14 seconds: 36 waited for parts and were filled, 12 parts were sold at the tills, 24 invoices were corrected (12 re-invoiced, 12 credited to store credit), 3 corrections were raced from every till, the generator ran 10 times at once, 12 payouts were raced and 2 serial fits were raced.

Every job's standing commission matches the arithmetic in the test, each correction's reversal is the exact opposite of what stood, no commission exists twice and no commission is in two payout lines. The accounts match the test's model:

| Account | What the model counts | Expected | Moved (CI) |
|---|---|---:|---:|
| 1430 Spare parts | arrivals, less what the tills sold, less what was fitted | −288.000 | −288.000 |
| 1460 Work in progress | only the board still on an open job | 30.000 | 30.000 |
| 5200 Cost of repair parts | parts fitted and delivered, and parts sold over the counter | 672.000 | 672.000 |
| 5210 Cost of outside work | outside work at cost | 94.500 | 94.500 |
| 2110 Payables | outside work owed to the supplier | −94.500 | −94.500 |
| 6300 Commission expense | the commissions that stand | 128.456 | 128.456 |
| 2400 Commissions payable | what stands less what was paid | 0.000 | 0.000 |
| 1100 Cash | sales, deliveries, corrections and cash payouts | 3,051.686 | 3,051.686 |

**The browser walk-through** (`e2e/phase9.mjs`, CI job `e2e-phase9`) writes two rules in the browser (Tariq 10% of labour, Noor 20% of repair profit with a 0.500 minimum) and sets a 30-day, 50% rework clawback. Job A takes the only battery and the serial board; job B needs a battery too and waits for it. The till refuses to sell the held battery. One arrives, the back office gives it out, and job B moves on. The board is fitted by scanning its serial; job B gets 4.500 of outside work and is shared 40/60. Both devices are collected at the till (96.000 and 31.000):

| Commission | Arithmetic | Amount |
|---|---|---|
| A, Tariq | 10% of labour 14.286 (15.000 with VAT) | 1.429 |
| B, Tariq | 40% of 10% of labour 9.524 | 0.381 |
| B, Noor | 60% of 20% of profit 17.024 (9.524 + 20.000 − 8.000 − 4.500) | 2.043 |

The generator, run three more times, writes nothing. Job A's invoice is corrected at the till (labour 15.000 → 12.000; 96.000 back in cash, 93.000 paid): 1.429 is reversed and 1.143 generated. The three commissions are approved and paid (Tariq 1.524 by bank, Noor 2.043 through payroll). A warranty claim on job B takes back 0.191 and 1.022. Tariq's statement then shows −0.191 payable (the clawback waits for his next payout). The accounts:

| Account | Moved | Why |
|---|---|---|
| 1100 Cash | +124.000 | 96 + 31 collected, 96 back, 93 for the corrected invoice |
| 1200 Bank | −1.524 | Tariq's payout |
| 2410 Payroll clearing | −2.043 | Noor's payout |
| 6300 Commission expense | +2.354 | 1.143 + 0.381 + 2.043 − 0.191 − 1.022 |
| 2400 Commissions payable | +1.213 | 3.567 paid, 2.354 accrued: the clawbacks are owed back |
| 1430 Spare parts | −38.000 | two batteries and the board fitted, one battery arrived |
| 5200 Cost of repair parts | +46.000 | two batteries 8.000 and the board 30.000 |
| 5210 Cost of outside work | +4.500 | |
| 2110 Payables | −4.500 | |
| 1460 Work in progress | 0.000 | |
| 4900 Sales returns | +91.429 | invoice A credited (without VAT) |
| 4200 Service income | −35.239 | labour on A, B and the corrected A |
| 4210 Repair parts income | −174.286 | parts on A, B and the corrected A |

No commission exists twice; the audit chain verifies; stock, ledger and accounts agree. Screenshots of every step, in English and Arabic, are in the CI artifact `e2e-phase9-screenshots`.

## What was built

**Database** (`V0018__parts_commissions.sql`): reservations and the balance's reserved quantity; parts requests; serial parts (the unit's `fitted` state); outside work lines and their clearing; invoice corrections and the chain that decides which invoice stands; the commission schema (settings, versioned rules and the rule resolver, job shares, accruals, payouts and their lines, with the triggers that keep them exact); accounts 2410 payroll clearing and 5210 cost of outside work; the posting rules `repair.outside_work`, `comm.accrual` and `comm.payout`; the permissions `REPAIR.INVOICE.CORRECT`, `COMM.VIEW`, `COMM.OWN`, `COMM.RULES`, `COMM.APPROVE`, `COMM.PAY`.

**Server:**

- parts: reconciliation of what each job holds and waits for, the background filler, the parts-needed list, serial fitting, outside work and its reversal;
- commissions: generation in the invoice's transaction, a safe re-run generator, reversal, clawback, approval, payouts, the settings, rules and shares APIs, and the statement, performance and rule-usage reports;
- the correction of a delivered repair invoice at the till;
- the job detail now shows the invoice that stands and its corrections.

**Lock order and deadlocks.** The full test suite showed a dozen deadlocks a run under load (all retried, none lost), most of them involving the audit log's and the outbox's server-wide locks. Audit and outbox rows are now written just before each transaction commits (ADR 61); the correction and the collection's deposit refund take their document numbers before any journal; parts are reconciled before a fitting's journal. The suite now runs with no deadlocks at all.

**Web app and till:**

- the job page: parts held and waited for, serial fitting, outside work, who shares the commission, the job's commissions, the invoice that stands and its corrections;
- Parts needed; Commissions (to approve, to pay, all, payouts, statement, performance, rule usage); My commissions for technicians; Commission rules and settings;
- at the till, Repairs at the counter → Correct the invoice, for managers.

All in English and Arabic. The web app's commission arithmetic (`@kite/pos commission.ts`) uses the same vectors as the server.

**Nextdab Admin** is unchanged: Phase 9 adds no switchable part, licence kind or check-in field (the commissions module was already in the licence's module list).

## Tests

| Suite | Result |
|---|---|
| Database (PostgreSQL 16 and 17) | 18 of 18 files |
| Server unit tests | 138 passed (commission vectors, worked examples and refusals, the new posting rules, upgrades from earlier phases) |
| Server integration tests | 69 passed (4 for Phase 9: parts, serial parts and outside work, commissions, and the exit gate) |
| Web and till (Vitest) | 78 passed in 14 files; 9 for Phase 9 (7 screen tests, one in Arabic, and 2 for the commission arithmetic) |
| Commission arithmetic vectors | 122 cases from an independent Python implementation, shared by server and web app; CI regenerates them and fails on any difference |
| Browser walk-through | 33 checks passed (`e2e/phase9.mjs`) |

## Problems found and fixed during Phase 9 testing

1. **A reservation raised through an upsert counted twice.** PostgreSQL fires a BEFORE INSERT trigger for the row an `INSERT … ON CONFLICT DO UPDATE` proposes, and then the UPDATE trigger. The balance now follows reservations in an AFTER trigger; a SQL test covers it.
2. **The application could not lock stock balances** (it may only read them). Locking goes through a function that runs as the owner, for a warehouse of the branch given.
3. **The payout rule's account map was empty when the rules were built** (static fields initialise in order). Caught by the unit tests before any payout ran.
4. **Deadlocks under load** around the audit and outbox locks and document numbers (see Lock order above).
5. **A part waited for by a job could be sold at the till** before the filler gave it out. The till now respects both held and waited-for parts.
6. **The commission report's days were counted in UTC.** They are counted in the business's time zone, and the screens' default periods use the business day.
7. **Serial parts could not be put on an estimate.** They can now, by whole units.
8. Smaller fixes: the module check's array comparison, a date parameter, the shares' audit entry, the trace returned as text, technicians' names in Arabic on the commission screens.

## Decisions and departures from the blueprint

- **ADR 61:** audit and outbox rows are written just before the commit.
- **ADR 62:** the latest approved estimate decides what a job holds in stock; stock goes to the oldest request; the till cannot sell what is held or waited for.
- **ADR 63:** serial-tracked spare parts are estimated by quantity and fitted by serial.
- **ADR 64:** a commission is written once and never changed; rules are versioned.
- **ADR 65:** repair profit uses the job's own costs.
- **ADR 66:** a delivered repair invoice is corrected at the till by a manager (the owner's decision).
- **ADR 67:** payouts are cash, bank or payroll, and payroll is a clearing account.

The blueprint's link from a parts request to a purchase order or a transfer is in the data (each request has a place for it) and comes with purchasing (Phase 11) and transfers (Phase 12).

## Things for Nextdab

1. Nothing to install in Nextdab Admin for this phase.
2. Commissions are a module: switch it on in Modules (it needs Repairs), then write the rules in Commission rules. Technicians see their own commissions under My commissions.
3. Shops that pay commissions with salaries should pay through payroll and clear account 2410 when salaries are paid.

## Known limits

1. Parts requests are not yet linked to purchase orders or transfers (Phases 11 and 12).
2. Correcting a repair invoice needs an open shift at a till of the job's branch.
3. Outside work recorded after delivery does not change a commission already generated.
4. Customer credit (on account) cannot pay a repair invoice yet (Phase 10).
5. Deposits, collections and corrections need the server: emergency mode cannot take them.

## Next: Phase 10

Customer credit and supplier management: "Online and offline credit rules pass; statements reconcile."
