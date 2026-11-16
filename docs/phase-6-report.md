# Phase 6 report: IMEI and serial number management

KITE MOBILE ERP · Nextdab · version 0.6.0 · November 2026

## Summary

Phase 6 makes an IMEI mean one phone across the whole business, not just on one server. Head office now keeps a central IMEI register. A branch server asks head office before it receives a phone. If head office cannot be reached, the branch still receives the phone, and head office confirms it later.

When head office finds the same IMEI on two phones, both phones go on hold. The fix happens where a phone is: correct an IMEI that was typed wrong, or reverse a receipt that should not have happened. The conflict then closes by itself.

The blocked list is kept at head office and copied to every branch. Phones can now be counted by IMEI, and emergency mode sells phones again.

The exit gate passed on GitHub Actions. CI run `43860239618` on commit `7410cbe` has all sixteen jobs green. That includes the Phase 6 exit-gate test and the new two-server browser walk-through, in which head office is stopped and started again.

## Exit gate (blueprint section 25, row 6)

"Uniqueness and verification points pass concurrency tests."

The exit-gate test (`Phase6ExitGateTests`, CI job `phase6-exit-gate`) has two parts.

**1. One server, 20 receivers and 20 tills at once.** Twenty receipts that share IMEIs are posted at the same moment, then twenty tills try to sell the same phone. Every IMEI ends with exactly one device, and every receipt is all or nothing. Exactly one till sells the phone; the other nineteen are told why.

**2. Head office and 10 branch servers at once, over mutual TLS.** For 8 rounds, each branch server registers 8 phones from a shared pool of 153 IMEIs, as it would after working offline. Meanwhile:

- head office receives some of the same IMEIs, and corrects or reverses its own;
- branches correct and reverse theirs while the conflicts are open.

Each branch applies head office's answers and heartbeat instructions exactly as a branch server does. At the end, the test checks every server against its own record of who claims each IMEI.

| Requirement | Status | Evidence (CI run `43853705579`, commit `b9706b6`) |
| --- | --- | --- |
| Uniqueness on one server | Done | 20 receivers and 20 tills raced. One device per IMEI, every receipt all or nothing, one sale. |
| Uniqueness across servers | Done | 592 branch devices and 9 head office devices for 492 IMEIs. Every IMEI has exactly one active holder at head office. |
| Conflicts found and settled | Done | 141 conflicts opened and 107 settled: 85 corrections and 62 reversals at branches, 4 corrections and 1 reversal at head office. A conflict is open exactly where two devices still claim one IMEI; 34 were still waiting for staff at the end. |
| Verification points (holds) | Done | 442 instructions were sent. Every device on every server is on hold exactly for the open conflicts it is in. Each device's instructions are a hold, then at most one release. |
| Late messages | Done | About 220 older instructions arrived after newer news and were correctly ignored (ADR 37). |
| Problems found by the test | 0 | 4.4 seconds for the multi-server part. |

The same test passes locally at 4 branch servers × 4 rounds × 6 phones and at the CI size.

**Two-server browser walk-through** (`e2e/phase6.mjs`, CI job `e2e-phase6`). It uses two real servers, head office and the Sohar branch, each with its own PostgreSQL database, plus the Nextdab stub and Chrome. The script starts both servers itself, so it can stop head office in the middle. Every state is checked through the API of the server concerned, not only on screen. The screenshots are attached to the report message.

1. **Setup.** Head office is set up with a licence (3 branches) and the device module. Branch B02 (Sohar) joins with a one-time token after comparing head office's security code.
2. **Head office receives an iPhone.** Head office confirms it at once.
3. **Sohar tries to receive the same IMEI.** Head office is asked online, and the scan shows "IMEI 352099001761481 is already registered: in stock at B01 (iPhone 13 128GB)". "Post" stays disabled. Posting through the API is refused too, with `DEV-007` naming B01.
4. **Head office is stopped.** Sohar's banner says head office cannot be reached.
5. **Sohar receives two phones offline:**
   - the same IMEI, typed by hand digit by digit;
   - a clean phone.

   Each scan says "checked in this branch only". After posting, a notice says 2 phones wait for head office, and the list marks both "Head office check pending".
6. **Head office starts again.**
   - The clean phone is confirmed.
   - The duplicate opens one IMEI conflict, with sides B01 and B02.
   - Both phones go on hold: head office's directly, Sohar's by an instruction with the heartbeat.
   - Head office then hears back that Sohar's phone is on hold.
   - The device page is shown in English and Arabic.
7. **Sohar corrects the IMEI** to the one on the box label, giving a reason.
   - The conflict closes by itself, and head office's phone is back in stock.
   - Sohar's phone is released and confirmed under its real IMEI.
   - Head office records "B02 corrected, B01 kept".
   - The mistyped IMEI stays in Sohar's history as replaced and typed by hand.
8. **The look-up.** Head office looks up the IMEI across the business.
9. **The blocked list.** Head office blocks a stolen IMEI (reason "Reported stolen", reference ROP 22/118). The list reaches Sohar with the heartbeat. Sohar's scanner then refuses it, with the reason and the reference.
10. **The end state.** At Sohar, nothing waits for head office and nothing is held for a conflict.

## What was built

**Database** (`V0015`, SQL test `14_phase6_imei.sql`)

- **Identifiers are retired, never changed (ADR 39):**
  - IMEIs and serial numbers are unique among active rows.
  - `device.retire_identifier` keeps the old one, with the reason.
  - A trigger refuses any other change.
- **Device statuses:**
  - New status "Receipt reversed".
  - New events "IMEI corrected" and "Receipt reversed", with their allowed moves.
  - A device that is put on hold twice keeps the status it came from.
- **Holds with reasons (ADR 38):** `device.unit_hold` covers a manager's hold, an IMEI conflict and the blocked list. Phase 5 holds were converted to manager holds.
- **At every server:**
  - `device.central_check`: pending, confirmed or conflict, with a per-device stamp.
  - `device.central_link`: the last instruction applied.
- **At head office:**
  - `device.central_registry`: one active row per identifier.
  - `device.imei_conflict` and its sides.
  - `device.central_instruction`: immutable, gap-free per server.
  - `device.blocked_imei`.
  - `device.tac_model`.
- **Serial formats:** a pattern per brand.
- **Counts by IMEI:** `device.unit_count` and its scans, numbered DC-.
- **Emergency phone sales:** `sales.emergency_settlement`, for emergency phone sales settled by a manager.
- **The receipt reversal posting rule (ADR 40).**
- **Existing data:** devices already in a combined or head office server are registered and confirmed; devices already at a branch wait for head office.
- **Permissions:** four new permissions, `DEVICE.IMEI.CORRECT`, `DEVICE.CONFLICTS`, `DEVICE.BLOCKLIST` and `DEVICE.COUNT`.

**Server**

- **Head office register and sync** (`/sync/v1/imei/check`, `/register` and `/lookup`, mutual TLS) (ADR 34):
  - an advisory lock per identifier, taken in sorted order;
  - conflicts recorded, not refused;
  - holds and releases worked out from who still claims each identifier.
- **Branch client:**
  - It asks head office online before receiving and before correcting, with a 4-second limit (ADR 35).
  - It sends phones after a receipt and with every heartbeat until head office has answered or the phone changes.
  - It applies answers and instructions with stamps, so a late message never undoes a newer one (ADR 37).
  - The heartbeat carries the instruction and blocked-list sequences both ways.
- **Receiving checks** (on every server):
  - the IMEI check digit;
  - the serial format;
  - the blocked list;
  - this business's own devices;
  - head office online.

  Typed IMEIs are audited, and a device received offline waits for head office.
- **Device identity endpoints:**
  - check scans;
  - correct an identifier;
  - reverse a receipt;
  - conflicts (all of them at head office, a branch's own held devices on a branch);
  - the blocked list (changed at head office only);
  - TAC import and look-up;
  - serial formats;
  - the business-wide history look-up;
  - the link status with head office.
- **Counts by IMEI.** Each scan says:
  - counted;
  - another model;
  - belongs to another branch;
  - not in stock in the books;
  - not a device of this business.

  Finishing a count can write off the missing phones.
- **The till:**
  - Scanning a phone checks it is the model chosen (`DEV-016`).
  - The strict option keeps pending phones off sale (`DEV-015`).
- **Emergency mode (ADR 41):**
  - The snapshot includes the phones in stock.
  - An upload applies a phone that is still in stock, or keeps the sale as a conflict.
  - A manager settles it with a substitute phone, or dismisses it with a note.
  - If the shift has closed, a shift-variance correction is posted.
- **Error codes `DEV-014` to `DEV-019`**, with English and Arabic messages.

**Web app** (English and Arabic)

- **Devices page:**
  - A head office banner: reachable or not, phones waiting, phones held for a conflict, open conflicts.
  - A "Head office check" filter and badges.
  - New tabs: IMEI conflicts (including emergency phone sales to settle), Blocked IMEIs and Count devices.
  - An IMEI look-up box.
- **Device page:**
  - Holds, with an explanation in the user's language.
  - Every IMEI the phone has had, scanned or typed.
  - "Correct the IMEI", which checks the new IMEI live.
  - "Reverse the receipt".
- **Device settings:**
  - Keep devices off sale until head office confirms.
  - The model table (TAC) CSV import.
  - Serial number formats per brand.
- **Receiving:**
  - Every scanned IMEI is checked with the server as it is scanned: blocked, already in this business, already elsewhere (from head office), or checked here only.
  - A model hint is shown when it differs from the item chosen.
  - "Post" is disabled while a scan has a problem.
- **The till:** the phone picker has an IMEI scan field and refuses a phone of another model. Pending phones have a badge.
- **Emergency till:** it sells phones from the snapshot at their own price, prints the IMEI and never sells one phone twice.

**Windows app** (version 0.6.0): emergency phone sales, as above.

## Tests

| Suite | Count | Where |
| --- | --- | --- |
| SQL | 15 files (1 new) on PostgreSQL 16 and 17 | `db/tests/14_phase6_imei.sql`: retire rules, receipt reversal, holds, central uniqueness, immutable instructions, blocked list, TAC, counts, the app role's rights |
| Server unit | 116 | unchanged count; the posting rule list now has 10 rules |
| Server integration | 48 (9 new) | `Phase6Tests.cs` (7: receiving checks, conflict and correction, receipt reversal at head office and at a branch, the blocked list through the heartbeat, model match at the till and the strict option, counts, settling an emergency phone sale) and `Phase6ExitGateTests.cs` (2) |
| Exit gate | 20 tills; 10 branch servers × 8 rounds × 8 phones | CI job `phase6-exit-gate` |
| Web | 55 (9 new) | `phase6.test.tsx`: conflict corrected and closed; receipt reversed; receiving checks and refusal; blocked list holds and releases; the look-up; a count; settings and model hints; the till refusing another model; emergency phone sales |
| End-to-end Phase 6 | 1 walk-through with two servers, 15 screenshots | `e2e/phase6.mjs`, CI job `e2e-phase6` |

## Problems found and fixed during Phase 6 testing

- **A head office check was refused by the database's own rule.** A query passed `@status` and `@Status`, which the database driver treats as one name, so one overwrote the other. The parameters were renamed. Found by the integration tests; Phase 5 had the same kind of mistake.
- **A phone of head office's own was not released when its conflict closed.** Only branch phones were released. Found by the integration tests.
- **Settling an emergency sale was refused (403).** It tried to lock the immutable emergency record for update. It now takes an advisory lock. Found by the integration tests.
- **A late "no conflict" answer could release a phone that had to stay held.** Found while designing the exit-gate model, before any code shipped. This led to ADR 37 (stamps and ordered instructions). The test now proves it under load.
- **A branch that had just joined could not create items or sell.** It had no default price list, POS policy or device settings until it was restarted. Found by the two-server walk-through. The join now sets them up, as setup does on head office.
- **On a branch server, the web app worked in the user's default branch (head office)** instead of the server's own branch. Found by the walk-through. `/auth/me` now says which branch the server serves.
- **The branch and head office worded the same refusal differently.** Found by the walk-through; both now say "IMEI … is already registered".
- **A settled conflict showed a phone's old status.** It showed "On hold" for a side that had already been fixed. The screenshots showed it. A settled conflict now shows only what happened on each side.

## Decisions and departures from the blueprint

Recorded in [`architecture-decisions.md`](architecture-decisions.md):

- **ADR 34:** head office keeps the IMEI register; a second claim is recorded, not refused.
- **ADR 35:** a branch asks head office before receiving. Offline, it receives and the phone waits for head office; an option keeps such phones off sale.
- **ADR 36:** a duplicate holds every phone concerned, is fixed where the phone is, and closes by itself. There is no "resolve" button at head office.
- **ADR 37:** answers carry a stamp and instructions an order, so a late message never undoes a newer one.
- **ADR 38:** holds have reasons; a phone stays on hold while any hold is open.
- **ADR 39:** identifiers are retired, never changed.
- **ADR 40:** a receipt is reversed only for a phone held by an IMEI conflict.
- **ADR 41:** emergency mode sells phones, and a phone sold elsewhere meanwhile is kept for a manager. This supersedes ADR 33.
- **ADR 42:** the blocked list lives at head office and reaches branches with the heartbeat.
- **ADR 43:** model hints warn; serial formats refuse.

## Things for Nextdab

1. **Nextdab Admin needs no change for Phase 6.** The device module already exists in the licence. Nothing new goes to Nextdab at check-in: no IMEIs and no customer or device data.
2. **The draft release "dev-package-cache"** on GitHub should still be deleted by hand, if not done yet.

## Known limits

1. **The catalog is not yet copied to branch servers.** In the walk-through, the iPhone item was made on both servers. The full sync of items, prices and stock is Phase 12.
2. **A phone sold while it was waiting for head office can turn out to be a duplicate.** The conflict then shows that side as sold. KITE records it but cannot unsell the phone. Shops that want to avoid this can switch on "Keep devices off sale until head office confirms the IMEI".
3. **A conflict nobody fixes stays open,** and both phones stay off sale. It shows on the banner, the device list and the conflicts tab of every server concerned.
4. **The model table (TAC) is imported by hand** from a CSV file. There is no subscription feed.
5. **Notes written by the server are in English.** This covers a hold's note, a conflict's resolution and the blocked-list reason as entered. The Arabic interface explains each hold in Arabic, then shows the note.
6. **The page header says "All branches" for the owner on a branch server.** The work itself happens in the server's own branch. This will be tidied with branch switching in Phase 12.
7. **Transfers between branches (Phase 12), trade-ins (Phase 7) and repair intake (Phase 8)** will use the same register and checks when they are built.

## Next: Phase 7

Blueprint section 25, row 7: trade-in management, including the margin-scheme tax code for second-hand phones. The exit gate is: all settlement types post correct journals.
