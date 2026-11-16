# Phase 8 report: repair and servicing management

KITE MOBILE ERP · Nextdab · version 0.8.0 · November 2026

## Summary

Phase 8 lets a shop repair its customers' phones and keep them informed on WhatsApp.

1. **Booking in.** At the counter (or in the back office) the intake wizard records the customer, the device (with what the shop knows of it: sold here and under warranty, earlier jobs), the problem and fault codes, the condition and what was left with it, how the device can be opened (a passcode is kept encrypted and every view is logged), photos, the customer's WhatsApp consent, and the repair terms signed on screen. It prints an A5 job card with a QR code and a device tag. Job numbers look like `R-B01-2026-000001`.
2. **The workshop.** Jobs move through the blueprint's 12 statuses and only its transitions. The board shows them by stage, overdue jobs first.
3. **Estimates.** Versioned estimates (labour at the hourly rate, parts, other charges, VAT) are approved by the customer's signature, by phone, by tapping Approve on WhatsApp, or automatically when they are within the amount the customer pre-authorised. Work cannot start until the current estimate is approved; extra work needs a new version approved again.
4. **Parts and labour.** Parts fitted move from stock into work in progress (1460) at their cost; they can be returned to stock or written off. Labour is logged by technician.
5. **Deposits.** Deposits are taken at the till into the drawer, held as customer deposits (2300) with their own number and receipt, and refunded when needed. Whether a deposit is a VAT point is the business's switch, off by default.
6. **Collection.** At the till the customer shows the job card (its QR) or the cashier checks them, they sign, and the repair invoice applies the deposit and takes the rest by any tender; any excess goes back in cash or as store credit. Work in progress is cleared to cost of repairs (5200), warranty (6600) or write-off (6700), the warranty starts and the passcode is deleted. A cancelled or unrepairable job pays the diagnosis fee.
7. **Uncollected devices.** Reminders go at 30, 60 and 90 days after the device is ready.
8. **WhatsApp.** The main server is the gateway: every server queues messages, the gateway sends them through Meta's WhatsApp Cloud API (or a test chat that never leaves the server), with consent, STOP, the 24-hour reply window, a daily cap and stale messages dropped. Meta reaches the gateway through an internet address that Nextdab makes for the main server.

The exit gate passed on GitHub Actions. CI run `43960518185` on commit `71e4f87` has all twenty-one jobs green. That includes the new Phase 8 exit gate and the Phase 8 browser walk-through (`e2e/phase8.mjs`, 36 checks against a real server, PostgreSQL and Chrome).

## Exit gate (blueprint section 25, row 8)

"Every status transition, estimate approval, deposit and delivery tested." The test table adds: "Every allowed transition; every disallowed one — allowed pass, disallowed rejected; parts and deposits post correctly."

**In the database** (`db/tests/16_phase8_repair.sql`): all 132 ordered pairs of the 12 statuses are tried. The 22 the blueprint allows pass; the 110 others are refused. The test also checks that work statuses need an approved estimate, that the pre-authorised and warranty approvals are limited as designed, that a delivered job cannot change, and that deposits never go below zero.

**The exit-gate test** (`Phase8ExitGateTests`, CI job `phase8-exit-gate`) runs 4 branches at the same time, 3 workers each, 4 rounds. At each branch it first tries all 132 pairs through the API. Then the workers run repairs end to end:

- estimates approved by signature, by phone, by the customer's WhatsApp Approve, and within the pre-authorised amount; extra work approved again;
- waiting for parts; deposits in cash and by card; parts fitted, returned and written off;
- deliveries paid in cash and by card; cancellations with a diagnosis fee; unrepairable devices with the deposit given back; warranty claims;
- disallowed transitions tried on the way, each refused.

A model in the test records what each step should post. Afterwards every account, the deposit and stock ledgers, every job's history and every drawer match the model. Two races run at the end: many tills collecting one device, and many refunding one deposit. Each succeeds exactly once.

**The browser walk-through** (`e2e/phase8.mjs`, CI job `e2e-phase8`) books an iPhone in with the wizard, takes a 20.000 deposit at the till, refuses eight moves out of "received" and work before an approved estimate, sends estimate version 1 (57.000) through the gateway, approves it with the Approve button in the test chat, fits the screen and logs labour, and collects the phone against its job card's QR: 37.000 paid in cash. A second job is declined, cancelled and collected: the 3.000 diagnosis fee comes out of a 5.000 deposit and 2.000 goes back. The accounts then match a table worked out in the script:

| Account | Moved | Why |
|---|---|---|
| 1100 Cash | +60.000 | deposits 20 + 5, the rest of job 1 37, 2 back |
| 1430 Spare parts | −30.000 | the screen left stock |
| 1460 Work in progress | 0.000 | the screen in, then cleared at delivery |
| 5200 Cost of repairs | +30.000 | the screen's cost at delivery |
| 2300 Customer deposits | 0.000 | 25 taken, 23 applied, 2 refunded |
| 4200 Service income | −14.286 | labour 12.000 and the fee 3.000, less VAT |
| 4210 Repair parts income | −42.857 | the screen 45.000, less VAT |
| 2200 Output VAT | −2.857 | 0.571 + 2.143 + 0.143 |

The drawer expects 160.000 (100.000 float + 60.000); the audit chain verifies; stock, ledger and accounts agree.

## What was built

**Database** (`V0017__repairs_whatsapp.sql`): the repair schema (settings, categories, fault codes, customer devices, jobs, status transitions and history, assignments, estimates and their lines, work lines, work-in-progress clearing, deposits, photos and signatures with their SHA-256, passcode views), the notification schema (settings, templates, outbound and inbound messages, the gateway's messages and events, suppression, the test chat, webhook de-duplication) and the posting rules `repair.deposit`, `repair.deposit_refund`, `repair.parts` and `repair.cost`.

**Server:**

- the repair API: board, look-up, intake, warranty claims, status, assignment, passcode, photos, messages, estimates, parts, labour, deposits, delivery preview and collection, uncollected devices, settings, and PDFs (job card with QR, tag, deposit receipt, delivery receipt);
- the WhatsApp gateway (provider adapter for Meta Cloud API and the test chat, sender, webhook with signature check, relay between servers) and housekeeping (reminders, passcode deletion);
- the `web` command from Nextdab: the main server's internet address, with the tunnel token kept encrypted and cloudflared checked before it runs; a loopback-only webhook port and a guard so only the webhook answers through the tunnel. If that port is in use, the server starts without the webhook and reports it.

**Web app and till:**

- Repairs: the workshop board, devices not collected, every job;
- the intake wizard;
- the job page: next statuses, technician, passcode, estimates, parts and labour, money, messages within the 24-hour window, history, warranty claims;
- repair settings, and WhatsApp (provider and its secrets, which can be written but not read back, the internet address and webhook state, templates, test chat);
- at the till, a Repairs button: find a job by its card, number or phone, take a deposit, or collect.

All in English and Arabic. The till's repair arithmetic (`@kite/pos` `repair.ts`) uses the same vectors as the server.

**Nextdab Admin 5.19** (in the shared copy on F:): the customer page's Internet address tab makes the `kme-` tunnel and DNS record and sends the signed `web` command; it also serves the Linux cloudflared.

## Tests

| Suite | Result |
|---|---|
| Database (PostgreSQL 16 and 17) | 17 of 17 files, including all 132 status pairs |
| Server unit tests | 134 passed (repair vectors, posting rules, upgrades from Phase 7) |
| Server integration tests | 65 passed (12 for Phase 8, the exit gate and a busy webhook port among them) |
| Web and till (Vitest) | 69 passed in 12 files; 8 for Phase 8 (5 screen tests, one in Arabic, and 3 for the till's repair arithmetic) |
| Repair arithmetic vectors | 286 cases, written by an independent Python implementation, shared by server and till |
| Browser walk-through | 36 checks passed (`e2e/phase8.mjs`) |
| Nextdab Admin 5.19 | 19 checks: KITE MOBILE ERP address made, signed, acknowledged and turned off; Kite RMS, Kite REMS and MOBILE STORE ERP unchanged |

## Problems found and fixed during Phase 8 testing

1. **A collection preview ignored the diagnosis fee the cashier typed.** The preview now takes it as a parameter.
2. **The WhatsApp approval raced the estimate's sending under load in the exit gate.** The gate now waits for delivery, and the test chat's Approve picks the latest message that has buttons.
3. **Two servers on one computer could not both start** (CI's two-server walk-throughs failed): both opened the webhook port. A server now opens it only if it is free; if not, it starts without the webhook and says why in WhatsApp settings and in the Nextdab report.
4. **The Phase 3 walk-through refused the new `web` field in the report to Nextdab.** It now allows it and checks it never contains a token.
5. **The intake wizard did not say which branch receives the device** for a user who works at several. It sends the user's working branch.
6. **The part search on the job page found nothing for the owner** (who sees all branches). It now searches the job's branch.
7. **The till's quick tiles showed the repair charge services.** They are charged only through a job and are now left out.
8. **The board's sixth lane was cut off in Arabic.** The lanes now wrap instead of scrolling sideways.
9. Smaller fixes: an invalid regular expression in setup, a JSON property clash in the deposit response, two test counts.

## Decisions and departures from the blueprint

- **ADR 54:** repair writers lock the job row first, then the shift, credit, the deposit lock, numbers, journals, audit and outbox.
- **ADR 55:** a deposit is drawer money held for the customer (2300) and is applied to the invoice as a tender at collection.
- **ADR 56:** whether a deposit is a VAT point is the business's setting, off by default, changed only with the tax permission.
- **ADR 57:** the workflow is enforced by the database, with clearer checks in front (REP-005 explains what to do).
- **ADR 58:** WhatsApp goes out only from the main server's gateway; branches relay through the sync port.
- **ADR 59:** the main server's internet address comes from Nextdab, and only the webhook answers through it.
- **ADR 60:** a repair invoice is not voided or returned at the till; a failed repair is a warranty claim.

Left for Phase 9 by plan: reserving parts when an estimate is approved, serial-tracked parts, and technician commissions (with their reversal when a repair invoice is credit-noted).

## Things for Nextdab

1. **Install Nextdab Admin 5.19** (the zip is in F:\nextdab-admin). Customers' main servers need KITE 0.8.0 to receive an address.
2. **For each customer who wants WhatsApp:** on their customer page, Internet address tab, make the address. The customer then enters their Meta phone number ID, access token and app secret in KITE (WhatsApp settings) and presses Connect to Meta. Their message templates must be approved in Meta's WhatsApp Manager with the names shown on KITE's templates page.
3. Until then a shop can use repairs fully without WhatsApp, and try the messages with the test chat.

## Known limits

1. Parts are not reserved when an estimate is approved; they leave stock when fitted (Phase 9).
2. Customer credit (on account) cannot pay a repair invoice yet; store credit and the usual tenders can (Phase 10).
3. Deposits and collections need the server: emergency mode cannot take them.
4. A billing mistake on a delivered repair invoice cannot be corrected at the till (ADR 60); the back-office credit note comes with Phase 9.
5. WhatsApp templates must be approved by Meta outside KITE; KITE only stores their names and text.
6. Photos and signatures are kept in the database, up to 3 MB each, so they are in every backup.

## Next: Phase 9

Spare parts and technician commissions: "Parts costing and commissions exact; duplicate generation impossible."
