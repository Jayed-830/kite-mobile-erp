# Architecture decision records

Each record states the decision, why, and what it costs. Records 1 to 7 implement the Phase 1 blueprint (section 1.1). Records 8 to 12 are Phase 2 choices, including where the code departs from the blueprint and why. Records 13 to 19 are Phase 3 choices.

## ADR 1: One server code base, three roles

**Decision.** One ASP.NET Core build runs as `combined` (single shop), `main` (head office) or `branch`. The role is stored in `core.server` (the row with `is_self`).
**Why.** A single-branch shop grows into multi-branch by adding branch servers, with no data migration (blueprint 3.4).
**Cost.** Role-specific behaviour must be switched at runtime; covered from Phase 3.

## ADR 2: Branch is the system of record for its operations

**Decision.** Every operational record carries `business_id`, owning `branch_id` and origin columns; only the owner server changes it.
**Why.** Branches must trade with no head office (blueprint 0.2 rule 3).

## ADR 3: Ledgers are append-only, enforced by the database

**Decision.** `kite_app` has no UPDATE/DELETE grant on `acct.journal`, `acct.journal_line` and `audit.audit_log`; triggers reject UPDATE, DELETE and TRUNCATE for every role, the owner included. Journals balance at commit (deferred constraint trigger), cannot post twice (unique key), cannot post into closed periods, and a reversal must mirror its original.
**Why.** Financial history cannot be rewritten by a bug or a direct database edit (blueprint 9.4).
**Evidence.** `db/tests/01`–`04`.

## ADR 4: Transactional outbox with a gap-free sequence

**Decision.** `sync.outbox` is written in the same transaction as the business change. `origin_seq` is assigned in a trigger under an advisory lock, so a rolled-back event leaves no gap; the payload and its SHA-256 never change; an unacknowledged event cannot be deleted.
**Why.** The sync protocol (blueprint 7) relies on contiguous sequences and immutable events.
**Evidence.** `db/tests/07_outbox.sql`, integration test "every write produced a sync event".

## ADR 5: UUIDv7 identifiers and a hybrid logical clock

**Decision.** IDs from `Guid.CreateVersion7()`; descriptive-field ordering by a hybrid logical clock (`HlcTimestamp`).
**Why.** IDs are created offline without collision; field-level last-writer-wins stays deterministic despite clock drift (blueprint 8.1).

## ADR 6: Exact money

**Decision.** `numeric(18,3)` in PostgreSQL, `decimal` in C#, decimal strings ("12.500") in the API. A unit test fails the build if `float` or `double` appears in server code.
**Why.** Blueprint 1.1 decision 7.

## ADR 7: Row-level security by branch

**Decision.** Each transaction sets `kite.all_branches` or `kite.branch_ids`; policies on branch-owned tables allow reads and writes only inside that scope, and the default with no scope is "see nothing". Audit appends are always allowed; audit reads are scoped.
**Why.** A branch user cannot read another branch's restricted records even if application code has a bug (blueprint 18.3).
**Evidence.** `db/tests/08_rls.sql`.

## ADR 8: SQL-first migrations run by the server (departure from blueprint 9.6)

**Decision.** Migrations are plain SQL files in `db/migrations`, embedded into `Kite.Persistence` and applied by the server at startup as `kite_migrator`, with checksums recorded in `meta.schema_info`. An applied migration whose text changed is refused.
**Why.** Ledger rules, grants, triggers and RLS are SQL anyway; the same files are tested directly by `db/tests/run.sh` on PostgreSQL 16 and 17. EF Core migrations would have wrapped raw SQL with little benefit.
**Cost.** No model-diff generation; schema changes are written by hand and reviewed.

## ADR 9: Npgsql + Dapper for data access in Phase 2 (departure from blueprint 20.1)

**Decision.** Phase 2 uses Npgsql with Dapper and explicit SQL. EF Core can be introduced per module later if it pays for itself.
**Why.** Every Phase 2 write touches RLS settings, audit and outbox in one transaction; explicit SQL keeps that visible and fast.

## ADR 10: Opaque session tokens instead of OpenIddict (departure from blueprint 20.1)

**Decision.** Sign-in issues a random access token (15 minutes) and a rotating refresh token (12 hours, httpOnly cookie). Only SHA-256 hashes are stored in `iam.session`, so sessions can be revoked instantly (logout, password change, user disabled, terminal revoked). Passwords and PINs use Argon2id; TOTP follows RFC 6238; TOTP secrets are AES-256-GCM encrypted with a key file in the server's data folder.
**Why.** No external identity server is needed offline, and revocation is immediate. OpenIddict can be added if third-party OAuth clients are ever needed.
**Known limit (closed in Phase 3).** Permissions were the union of a user's roles across branches; ADR 15 makes them per branch. The key files are wrapped with DPAPI from Phase 3 (ADR 14).

## ADR 11: Plain CSS design tokens instead of Tailwind and Radix in Phase 2 (departure from blueprint 20.1)

**Decision.** The design system uses CSS custom properties and logical properties (`margin-inline-start`, `inset-inline-start`), native `<dialog>`, and no web fonts or CDNs.
**Why.** Full RTL mirroring and light/dark themes with no build plugins; the app must load on a shop LAN with no internet. Headless primitives can be added when complex widgets (comboboxes, date pickers) arrive in Phase 4.

## ADR 12: Build and test on GitHub Actions

**Decision.** Every push builds and tests on GitHub's machines: SQL tests on PostgreSQL 16 and 17, server on Linux (with integration tests against PostgreSQL 17) and Windows, web app, desktop shell, and the proofs of concept on Linux and Windows.
**Why.** The workspace where the code is written cannot download NuGet packages, and the owner's PCs should not be the only test machines.

## ADR 13: A business certificate authority and mutual TLS on a separate sync port

**Decision.** At setup the main (or combined) server creates the business CA: ECDSA P-256, 20 years, made with .NET's own `CertificateRequest`, no external PKI. Branch servers get two-year certificates whose subject KITE writes itself (`CN=kite-server:{id}`, `OU=kite-branch:{id}`, `O=kite-business:{id}`), whatever the request asked for. Branch traffic (registration, heartbeat) uses its own TLS port (default 5443) with client certificates; the web and API port stays plain HTTP on the shop LAN behind the Windows firewall until the installer adds HTTPS (Phase 14). Every request on the sync port is checked against the CA, the certificate's database record and its revocation, not only the TLS handshake; a revoked certificate (`pki.certificate`, permanent by trigger) fails at the next heartbeat, and the CRL is sent to branches in the heartbeat answer. Joining uses a one-time token (24 hours, stored as SHA-256 only) and a fingerprint code of the CA (20 digits from its SHA-256, in five groups of four), shown on both screens so the person joining compares them; the branch pins that fingerprint.
**Why.** Branches must trust head office and each other offline, with no public CA and no internet (blueprint 18.1, 18.4). A separate port keeps client-certificate handling away from browsers.
**Cost.** The CA private key is the business's root of trust: it is DPAPI-protected on the main server, and a passphrase-encrypted recovery copy can be exported (password asked again, audited). Losing both means re-registering every branch.

## ADR 14: Key files protected with Windows DPAPI in machine scope

**Decision.** Key files in the server's data folder (`secrets.key` for TOTP secrets, `business-ca.key`, `sync-tls.key`, `branch-client.key`, `backup.key`) are wrapped with DPAPI `LocalMachine` scope plus a per-purpose entropy on Windows. On Linux and macOS (development and CI) they are written with owner-only permissions and the Backups page says "File permissions" so this is never mistaken for production protection. Phase 2 raw key files, and files restored from an unprotected store, are wrapped on first read.
**Why.** A copied data folder (or a stolen backup of it) is useless on another PC; this closes the Phase 2 known limit without a separate secrets service.
**Cost.** Moving a server to a new PC needs the recovery copies (CA) and the printed backup recovery key, by design.

## ADR 15: Permissions evaluated per branch

**Decision.** Sign-in issues two kinds of permission claims: `kite:perm` for roles granted business-wide (branch empty) and `kite:bperm` = `{branch}|{permission}` for roles granted for one branch. `Has` checks business-wide only; `HasIn(permission, branch)` and `Demand(permission, branch)` check business-wide or that branch. Endpoints that act on one branch (registration tokens, revoking its server) demand the permission in that branch.
**Why.** A manager of Sohar must not manage Muscat (blueprint 18.3).
**Cost.** Each branch-scoped endpoint must name its branch; an endpoint that forgets falls back to business-wide permissions, which is the safe direction.

## ADR 16: The KITE MOBILE ERP licence key is made inside Nextdab Admin

**Decision.** Nextdab Admin 5.14 creates the KITE MOBILE ERP Ed25519 key pair on the Nextdab PC the first time the owner signs in, seals the private key with the owner password (as for Kite RMS and Kite REMS) and shows only the public key. That public key is compiled into KITE (`LicenceKeys.EmbeddedHex`). Until it is filled in, no licence can be imported (the Licence page says so); a Development build may use a test key from configuration (`Kite:Licensing:DevPublicKey`), which is how CI runs.
**Why.** The private key never exists anywhere but the Nextdab PC: not in this repository, not in a chat, not in CI. Licences and commands use NEXTDAB PROTOCOL 1 (canonical JSON, Ed25519 over `NEXTDAB1|`), the same as Kite REMS, proven by two test vectors: one re-signed byte for byte, one made by Nextdab Admin 5.14 itself.
**Cost.** One manual step: the owner sends the public key once after installing Nextdab Admin 5.14, and a KITE build with it is made.
**Done on 16 November 2026.** Nextdab sent the key (`baea0666…f5fd4d9`) and it is compiled in. A release build always uses it; `LicenceKeyTests` checks that, and that a licence signed with any other key is refused.

## ADR 17: Check-in at /v1/kme/checkin, and what an unlicensed server may do

**Decision.** Only the main or combined server checks in, at `https://control.nextdab.com/v1/kme/checkin` (the same address and tunnel as Kite RMS and Kite REMS, a new path so the existing check-ins are untouched). Branch servers never call Nextdab; they receive the licence, switch-off and modules state from head office in the heartbeat. A server with no licence allows 1 branch and 1 terminal and no new transactions, so setup and configuration can be done before the licence arrives. After the licence's end date it warns (from 14 days before), then a grace period (default 30 days) keeps trading; after that new transactions stop while sign-in, data, reports, backups and sync keep working. Switch-off by Nextdab stops everything except sign-in, licence, backups, setup, servers, health and sync.
**Why.** Blueprint 24.5; the shop must never lose its data or backups because of a licence.
**Cost.** Licence enforcement is checked on every guarded action against a 30-second cached status.

## ADR 18: The API client stays hand-written until Phase 4

**Decision.** The typed API client (`@kite/api`) is still written by hand for the Phase 3 endpoints; generating it from the server's OpenAPI document moves to Phase 4.
**Why.** Phase 3 added 18 endpoints; minimal-API OpenAPI generation needs response types on every endpoint to be useful, and Phase 4 starts the business modules (the Phase 2 report lists stock, sales and repairs), which grows the API several times over. Doing it once then, with a CI check that the generated client is current, costs less than doing it twice.
**Cost.** Until then a server change can drift from the client unnoticed by the compiler; the web tests and the end-to-end runs against real servers catch it.
**Revised in Phase 4.** Still hand-written. The POS endpoints carry many request shapes whose rules (money as strings of baisa, approvals, idempotency keys) are clearer typed by hand, and the generated client would still need wrappers. Drift is caught instead by `ApiContractTests`: it reads every path the client calls and fails if the server has no route for that method and path. Generation is revisited when a third-party integrator needs the OpenAPI document (Phase 12).

## ADR 19: Backups with the PostgreSQL tools, encrypted, and proven by restoring them

**Decision.** The backup agent runs inside the server: nightly `pg_dump -Fc` (logical) and `pg_basebackup` checked with `pg_verifybackup` (base, for point-in-time recovery with WAL archiving), each encrypted with AES-256-GCM in 1 MiB chunks (`KITEBAK1` format, the last chunk flagged so truncation is detected) under a backup key whose printed recovery key is shown once after the password. Retention: daily 30 days, weekly 12, monthly 12, yearly 10 years; base backups 7 days. Every week the newest backup is restored into a scratch database and checked: journals balanced, outbox sequence gap-free, audit chain intact. `Kite.Host backup-decrypt` opens a backup on any PC with the recovery key.
**Why.** A backup that has never been restored is a hope, not a backup (blueprint 19).
**Cost.** The `kite_backup` role needs BYPASSRLS, REPLICATION and CREATEDB; it has no write grant on KITE's tables.

## ADR 20: Posting rules are data, and every business document posts in the same transaction

**Decision.** How a sale, credit note, shift variance, cash pay-out or stock adjustment becomes a journal is a row in `acct.posting_rule` (event, line, account code, debit or credit, amount key), seeded with seven default rules and changeable per business without a release. `PostingEngine.Build` turns the facts of a document into balanced lines and refuses an unbalanced result; the journal is written in the same database transaction as the document, its stock movements and its audit row. A sale posts two journals (revenue, VAT, tenders and cash rounding; then cost of sales against inventory at moving average). A document dated in a closed period posts to the first open one.
**Why.** Blueprint 11: accountants change mappings, and a ledger that can disagree with sales for even a moment is wrong.
**Cost.** A bad rule change could misroute postings; rules can only be edited with `ACCT.POSTING_RULES` and every change is audited.

## ADR 21: One lock order for sales, returns, voids and counts

**Decision.** Every writer takes locks in the same order: the audit chain lock before the outbox lock, stock rows in variant order (`inv.lock_balance`, a security-definer function, so the app role needs no UPDATE grant to lock), and the document number series last. Sales, returns and voids retry on deadlock (40P01) or serialisation failure (40001). Completing a sale is idempotent: the client's sale key takes an advisory lock and a repeated request returns the first result.
**Why.** Twenty tills selling the same items must not deadlock or oversell. The load test (20 terminals, 662 sales, 126 returns, 76 voids in CI) found the audit/outbox inversion before it reached a shop.
**Cost.** New writers must follow the order; the load test is the guard.

## ADR 22: QuestPDF for the A4 invoice, under its Community licence

**Decision.** A4 tax invoices are drawn with QuestPDF (Arabic shaping proven in the Phase 1 proof of concept) with the QR code from QRCoder.
**Why.** It renders Arabic correctly without a browser on the server.
**Cost.** The QuestPDF Community licence is free only while the developer (Nextdab) has annual gross revenue under USD 1,000,000 and is not publicly traded; end customers never need a licence. Nextdab must confirm it qualifies, or buy the Professional licence. Receipts on 58/80 mm printers do not use QuestPDF.
**Confirmed on 16 November 2026.** Nextdab confirmed the Community licence. If Nextdab's annual gross revenue reaches USD 1,000,000, a Professional licence is needed and this line changes.

## ADR 23: Receipts are pictures sent as ESC/POS raster; the server relays only to private addresses

**Decision.** The receipt is drawn on a canvas in the client (both languages, any font) and sent as ESC/POS raster (`GS v 0`) with the drawer kick (`ESC p`). The Windows app prints to network printers over TCP 9100 and to USB printers through the Windows spooler in RAW mode. A browser till prints through the server's relay, which connects only to private (RFC 1918, link-local, loopback) addresses unless `Kite:Printing:AllowPublicAddresses` is set, or falls back to the browser's print dialog with an 80 mm page. Opening the drawer needs `POS.DRAWER.OPEN` and the user's own open shift, and is counted on the Z report.
**Why.** Text-mode ESC/POS cannot print Arabic reliably across printer brands; a picture always can. The relay must not become a way to reach the internet from the server.
**Cost.** Raster receipts are slower on very old printers (about one second on 80 mm).

## ADR 24: Emergency mode in the Windows app: an encrypted local store, a server snapshot, ordered upload

**Decision.** When the server cannot be reached, a Windows till that the business has enabled keeps selling from a snapshot (items, prices, stock, users with their Argon2id PIN hashes, permissions and limits) refreshed while online. Sales go to a SQLCipher database whose key is protected by Windows DPAPI through Electron safeStorage, numbered from the terminal's own series (`EM-{branch}-{terminal}-NNNNNN`). On reconnection records upload in order, each applied once (unique terminal and sequence) in its own transaction; a record the server cannot accept is kept as a conflict for a manager, and stock that went negative is recorded as a stock exception instead of being refused.
**Why.** Blueprint 6: a shop must not stop selling when the server PC is off, and nothing sold offline may be lost or counted twice.
**Cost.** PIN hashes leave the server (encrypted at rest, and a PIN alone signs in only at that till); emergency sales cannot check stock held by other tills.

## ADR 25: A till is bound to its device with a key fingerprint

**Decision.** When a till is registered the client makes a 32-byte random device key and keeps it; the server stores only its SHA-256 fingerprint on the terminal. Signing in at a terminal must present the key (`POS-017` otherwise). A manager can rebind a replaced PC with `POST /terminals/{id}/device-key`.
**Why.** A terminal id alone is guessable; without the binding any browser could claim to be a till and use its shift and drawer.
**Cost.** Clearing the browser's storage unbinds a browser till until a manager rebinds it.

## ADR 26: Discount limits are the smaller of a percentage and an amount

**Decision.** Each role has a discount limit as a percentage of the line and an amount in OMR; a cashier may give up to the smaller of the two without approval (cashier template 5% or 2.000, senior cashier 10% or 10.000). `POS.DISCOUNT.ABOVE_LIMIT` removes the limit. A price change or a price below the minimum always needs `POS.PRICE.OVERRIDE`. Approval is by a manager's PIN at the till or remotely from the Approvals page, and each approval is used once, for the cart it was given for.
**Why.** A percentage alone gives away too much on a phone; an amount alone blocks a fair discount on a cable.
**Cost.** Shops must set both numbers per role.


## ADR 27: A phone is a device unit, and its status changes only through one database function

**Decision.** Every phone (or other serialized item) is a row in `device.device_unit` with its IMEI 1, IMEI 2 or serial number in `device.imei_registry` (unique per business; a serial is unique per brand). Its status changes only through `device.apply_event`, a security-definer function that checks the move against `device.transition` (blueprint 11.3), writes the event to the unit's history with a gap-free sequence and updates the unit. The app role has no UPDATE right on units. Selling, voiding, returning, inspecting, refurbishing, holding, pricing and writing off all go through it.
**Why.** A phone is worth hundreds of rials; its history must be complete and no code path may move it to a status the business rules forbid. One function is one place to check.
**Cost.** Every new device action (transfers in Phase 12, supplier returns in Phase 11) must add its moves to the transitions table.

## ADR 28: A phone's cost changes only through the stock ledger, and status and ledger must agree at commit

**Decision.** A unit's `accumulated_cost` is changed only by the stock ledger trigger: a `unit_cost` movement (quantity 0, a value) adds parts, labour or outside work; serialized movements are always ±1 at the unit's own cost. Deferred constraint triggers check at commit that a unit in an in-stock status has ledger quantity 1 at exactly its cost, and any other unit has 0 (`KT021`, shown as `DEV-013`). Cost of sale is the unit's own cost, not the item's moving average.
**Why.** Blueprint 25 row 5: conditions, inspections and refurbishment costs flow to unit cost. If cost could be written directly, the ledger, the inventory accounts and the device page could disagree.
**Cost.** Writers must post a movement for every cost change; a mistake fails the whole transaction instead of saving a wrong figure.

## ADR 29: Used, refurbished and damaged phones post to their own accounts, and an inspection reclassifies

**Decision.** A phone that is not brand new uses the posting class `device_used` (inventory 1410, sales 4110, cost 5110); new phones use 1400, 4100 and 5100. When an inspection changes a returned new phone to used, refurbished or damaged, a reclassification journal moves its cost from 1400 to 1410 in the same transaction.
**Why.** Accountants and the VAT margin scheme (Phase 7) need new and second-hand stock, sales and margins apart.
**Cost.** Two more posting rules (`refurb.cost`, `device.reclass`). Default rules seeded in Phase 4 are upgraded only if the business has not changed them.

## ADR 30: A phone in a cart is a lease, not a lock

**Decision.** Putting a phone in a cart writes a lease row (`device.reservation`: unit, cart, expiry; 15 minutes, 240 when the sale is parked). Another till cannot take it until the lease expires; an expired lease is simply taken over. Completing the sale checks the lease and deletes it. The till's unit list hides phones in its own cart and greys out phones held by others.
**Why.** Two cashiers must never sell the same phone, but a till that crashes must not hold a phone forever. The concurrency test (6 tills scanning one phone at once) sells it once.
**Cost.** A cashier who leaves a cart open longer than the lease can lose the phone to another till; the sale is then refused with `DEV-005` and nothing is sold twice.

## ADR 31: The checklist suggests, the inspector decides, and a different decision needs a note

**Decision.** Each category has an inspection checklist (pass/fail, percent with a lowest value, or a note) and each item says what a failure means: refurbish, damaged or for parts. The suggestion follows the worst failure (for parts, then refurbish, then damaged; all pass means back to stock). The same rule runs in C#, in TypeScript on the inspection page and in an independent Python program, checked against shared vectors. The inspector can choose another outcome, but must write why.
**Why.** Consistent grading across technicians and branches, without taking the decision away from the person holding the phone.
**Cost.** Shops must keep their checklists sensible; the default has 13 items.

## ADR 32: Labour is absorbed into stock and outside work waits for the supplier's invoice

**Decision.** Refurbishment labour is minutes × the hourly rate, rounded to the baisa half away from zero, and posts Dr inventory, Cr 6210 "Labour absorbed into stock". Parts come out of spare-parts stock at moving average (Dr inventory, Cr 1430). Outside work posts Dr inventory, Cr 2110 "Goods received not invoiced" until supplier invoices arrive in Phase 11. A cost taken back posts the exact reverse, and a part taken back returns to stock.
**Why.** Blueprint 12.4: the phone's cost must include all work on it, and the books must show where that money came from.
**Cost.** 2110 grows until Phase 11 matches it to invoices; accountants should review it monthly.

## ADR 33: Emergency mode does not sell phones until Phase 6

**Superseded by ADR 41 on 16 November 2026 (Phase 6).**

**Decision.** The Windows app's offline snapshot leaves out serialized items, and an offline upload with a phone line is refused (`DEV-002`). Phones are sold only while the server is reachable.
**Why.** Selling a phone offline needs a central IMEI register to stop two tills selling the same unit while apart; that is Phase 6 (IMEI and serial number management).
**Cost.** A shop with its server down can still sell accessories, parts and services, but not phones, until Phase 6.

## ADR 34: Head office keeps the IMEI register, and a second claim is recorded, not refused

**Decision.** Head office (role main or combined) keeps `device.central_registry`: every IMEI and serial number of every device in every branch, with the branch, the server, the device's status and a partial unique index so that only one row per identifier is active. A branch server sends its devices to head office (`/sync/v1/imei/register`, mutual TLS) after a receipt and with every heartbeat until head office has answered. When a device claims an identifier another device already holds, head office stores it as a `conflict` row and opens an IMEI conflict (`device.imei_conflict` and its sides); it never refuses the device. Requests touching the same identifier take an advisory lock per identifier, always in sorted order.
**Why.** Branch servers work offline (blueprint 6). A branch that received a phone while head office was down cannot be told "no" later: the phone is on its shelf. Head office's job is to notice and get it fixed, not to pretend it did not happen.
**Cost.** Head office stores a row per identifier per device ever received. A business with many branches and years of trade-ins will hold millions of rows; the indexes are on the value only.

## ADR 35: A branch asks head office before receiving; offline it receives and the device waits for head office

**Decision.** A branch server asks head office online (4-second limit) before it receives devices, corrects an identifier or checks a scan. If head office already has the identifier on another device, the receipt is refused with `DEV-007`, naming the branch that has it. If head office cannot be reached, the branch checks its own devices and the blocked list, receives the device, and marks it "head office check pending" (`device.central_check`). By default a pending device can be sold. The setting "Keep devices off sale until head office confirms the IMEI" (`device.settings.strict_central_check`) keeps it off sale (`DEV-015`) until head office answers; emergency snapshots leave such devices out.
**Why.** Most shops would rather sell a phone than leave a customer waiting because a cable to head office is down; some want the opposite. The online check catches almost all duplicates while everything is up; the pending state makes the rest visible.
**Cost.** A device sold while pending can later turn out to be a duplicate. The conflict then shows one side as sold; the business settles it (Phase 6 records it; it cannot unsell the phone).

## ADR 36: A duplicate found later holds every device concerned, is fixed where the device is, and closes by itself

**Decision.** When head office opens an IMEI conflict, every device claiming the identifier goes on hold: head office's own devices directly, a branch's devices through an instruction (`device.central_instruction`, a gap-free sequence per server) delivered with the next heartbeat. There is no "resolve" button at head office. The conflict is fixed where a device is: correct a mistyped identifier, or reverse the receipt of a device that was never really there (ADR 40). When only one device still claims the identifier, head office closes the conflict, records each side's outcome (kept, corrected, reversed) and releases the devices (a release instruction for branches). Instructions are immutable; a branch remembers the last sequence it applied.
**Why.** Only the person holding the phone can tell which IMEI is right. A head office button would let someone close a conflict without either phone being checked.
**Cost.** A conflict whose sides nobody fixes stays open and keeps both devices off sale. The devices list, the banner and the conflicts tab show it on every server concerned.

## ADR 37: Answers carry a stamp and instructions an order, so a late message never undoes a newer one

**Decision.** Head office takes one lock (`kite:central_instruction`) while it writes an instruction or works out an answer, so every answer carries a stamp equal to the highest instruction sequence it has seen. A branch keeps, per device, the stamp of the last thing it applied. It applies an answer only if the answer's stamp is at least the device's stamp, and an instruction only if its sequence is greater. The same rule is used by the server code and the exit-gate model.
**Why.** An answer to a push and a heartbeat's instructions travel separately. Without ordering, an old answer saying "no conflict" could arrive after a hold instruction and release a device that must stay held. The exit-gate run ignored about 220 older instructions without a single wrong hold.
**Cost.** The lock serialises instruction writing at head office; at 10 branch servers it added no visible delay, but it is a single point to watch if the business grows to hundreds of branches.

## ADR 38: Holds have reasons, and a device stays on hold while any hold is open

**Decision.** `device.unit_hold` records why a device is on hold: a manager's hold, an IMEI conflict or the blocked list, each with its note. A device is "On hold" while any hold is open and returns to its earlier status when the last one is released. A manager releases only a manager's hold (`DEV-018` otherwise); conflict and blocked-list holds are released only by settling the conflict or taking the IMEI off the list. Holds from Phase 5 were converted to manager holds.
**Why.** One "on hold" flag would let a manager release a phone that head office is holding for a duplicate IMEI.
**Cost.** One more table to read on every hold and release.

## ADR 39: Identifiers are retired, never changed

**Decision.** An IMEI or serial number row is never updated or deleted. Correcting it retires the old row (`device.retire_identifier`, with the reason and who did it) and adds a new one; reversing a receipt retires the device's identifiers. A trigger refuses any other change. Uniqueness applies to active rows only. Typed identifiers are recorded as typed (entry "manual") and audited.
**Why.** "Which IMEI did this phone have when it was sold?" must always have an answer, and a correction must not hide a mistake or a fraud.
**Cost.** Look-ups must say active or retired; the history look-up shows both.

## ADR 40: A receipt is reversed only for a device held by an IMEI conflict

**Decision.** "Reverse the receipt" takes a device out of stock at its cost with posting rule `device.receipt_reversal`: Dr 3900 opening balance equity for opening stock, Dr 6700 stock write-offs for "found" stock; Cr the device's inventory account. The device's status becomes "Receipt reversed" and its identifiers are retired. It is allowed only for a device on hold because of an IMEI conflict; any other device is written off instead.
**Why.** A phone received twice, or on the wrong branch, was never stock; writing it off would report a loss that did not happen.
**Cost.** A second reversal path next to write-offs. The permission is the write-off permission.

## ADR 41: Emergency mode sells phones; a phone sold elsewhere meanwhile is kept for a manager to settle

**Decision.** The Windows app's snapshot now includes the phones in stock at that branch, with their own price and warranty (pending devices are left out when the strict option is on). In emergency mode the till scans or picks a phone from the snapshot, never sells the same one twice on that till, and records the device on the sale. When the sale is uploaded, the phone is applied if it is still in stock; if it was sold or moved meanwhile, the sale is kept as a conflict. A manager (`DEVICE.CONFLICTS`) settles it from Devices › IMEI conflicts: record the sale with the phone the customer actually took (any phone of the same model in stock), or close it with a note (`sales.emergency_settlement`, immutable). If the emergency shift was already closed, the cash counted as over at close is taken back with a shift-variance journal.
**Why.** A phone shop whose server is down still needs to sell phones. Two tills selling the same phone while apart is rare and visible; refusing all phone sales is not.
**Cost.** Until the manager settles it, the sale is not in the books and the cash sits in the shift's variance. Supersedes ADR 33.

## ADR 42: The blocked list lives at head office and reaches branches with the heartbeat

**Decision.** Head office keeps `device.blocked_imei` (reason, police or report reference, who added it, who removed it and why; a sequence for changes). Branch servers receive changes with their heartbeat and keep a copy. A blocked identifier is refused at receiving (`DEV-014`) on every server, and a device already in stock with that identifier is put on hold; taking it off the list releases the hold. Only head office can add or remove (`DEVICE.BLOCKLIST`).
**Why.** A stolen phone reported in Muscat must be refused in Sohar the same day, even if Sohar's server is offline at the moment it is added.
**Cost.** A branch that is offline when an IMEI is added learns it at its next heartbeat; until then only head office's online check stops it.

## ADR 43: Model hints warn, serial formats refuse

**Decision.** An imported model table (TAC: the first 8 digits of an IMEI, brand, model) gives a hint while scanning ("Looks like a Samsung Galaxy A55") and warns when it does not match the item chosen; it never blocks. A serial number pattern per brand (a regular expression, checked with a 100 ms limit) refuses a serial number that does not match (`DEV-017`). At the till, scanning a phone of another model than the one chosen is refused (`DEV-016`).
**Why.** TAC tables are incomplete and change; a missing or wrong row must not stop a sale. A serial format is set by the shop for its own brands and is exact.
**Cost.** The TAC table is imported by hand from a CSV file; there is no subscription feed.

