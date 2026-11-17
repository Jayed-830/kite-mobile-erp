# Architecture decision records

Each record states the decision, why, and what it costs. Records 1 to 7 implement the Phase 1 blueprint (section 1.1). Records 8 to 12 are Phase 2 choices, including where the code departs from the blueprint and why. Records 13 to 19 are Phase 3 choices. Records 54 to 60 are Phase 8 choices (repairs and WhatsApp).

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


## ADR 44: A trade-in is a document; accepting it puts the phone in used stock in the same step

**Decision.** `tradein.tradein` is a numbered document (TI-) that moves open → accepted → settled, or open → rejected / cancelled. While open, the device, inspection, grade, deductions and agreed value can change; once accepted, a trigger freezes everything except its status and its link to a sale. Accepting does in one transaction: checks the IMEI again (blocked list, this business's devices, head office online), records the consent, brings the phone into stock under inspection at the agreed value, records how it was acquired (`device.acquisition`: trade-in or buy-back, cost, margin scheme), and posts Dr used-device inventory / Cr 2320 trade-in clearing.
**Why.** The business owns the phone from the moment the customer hands it over, whatever happens to the value afterwards. Splitting "take the phone" and "pay for it" lets a cashier take the phone now and settle the value in any of the four ways.
**Cost.** An open trade-in can be left behind; it holds no stock and no money, and the list shows it as open until it is rejected or cancelled.

## ADR 45: The four settlement types are three kinds of settlement against one clearing account

**Decision.** A trade-in is settled by any mix of three kinds of `tradein.settlement`: as a payment on a sale (`sale`), as the customer's store credit (`store_credit`, Dr 2320 / Cr 2310) or in cash from the drawer (`cash`, Dr 2320 / Cr 1100, a `tradein_payout` cash event). The blueprint's four types are: (1) applied to a sale, the customer pays the rest; (2) applied to a sale, the excess left as store credit or paid in cash; (3) store credit only; (4) cash buy-back. A trigger keeps the settled total between zero and the agreed value and sets the status. A void of a sale paid with trade-in credit writes a negative settlement, so the value can be used again. Account 2320 always equals what accepted trade-ins still owe.
**Why.** One clearing account and one rule cover every mix the shop meets (half on a sale, the rest as credit), and the exit gate can check the whole thing with one equation.
**Cost.** A trade-in credit cannot be refunded back to the trade-in after a return; a return refunds in cash, to the card, by transfer or as store credit.

## ADR 46: Store credit is pulled forward from Phase 10 as a signed ledger that never goes below zero

**Decision.** `crm.credit_ledger` (balance type `store_credit`) holds signed lines: + when a trade-in is left as credit or a refund goes to credit, − when a sale uses it. A trigger takes an advisory lock per customer and refuses a line that would take the balance below zero (`TRD-006`). Lines are never changed. Account 2310 is the liability. Store credit is a payment method at the till for a named customer, at any branch of the server.
**Why.** Trade-ins need store credit now; building it as the ledger Phase 10 will extend avoids a second migration of balances.
**Cost.** No expiry, no manual adjustment screen and no statement until Phase 10.

## ADR 47: The profit margin scheme follows the Tax Authority's approval, and its VAT is never shown

**Decision.** `tax.margin_scheme` records the approval (reference and date). VAT-MARGIN is active only while an approval is recorded; recording or withdrawing it switches the code. A trade-in accepted while the approval is in force, from a seller with no VAT number, is a margin purchase: it gets a self-invoice number (SI-) and the device remembers its purchase price. When such a device is sold, its line is taxed on the margin only: VAT = (price − purchase price) × rate / (100 + rate), never below zero, and the price is VAT-inclusive even for a business whose prices exclude VAT. The receipt and the A4 invoice show no VAT amount for that line, a "Margin scheme items" total and a note in English and Arabic; the QR code carries only the standard-rated VAT. The arithmetic is in C#, TypeScript and an independent Python implementation, checked against shared vectors (166 cases).
**Why.** In Oman the margin scheme for second-hand goods is used only with the Tax Authority's approval, and a margin-scheme invoice does not show the VAT amount (our reading of the rules). Tying the code to the recorded approval stops margin pricing before the business may use it.
**Cost.** The receipt and self-invoice wording are our reading of the rules and must be confirmed by the business's accountant. The VAT return boxes for margin sales come with the VAT return (Phase 11).

## ADR 48: Valuation is a price guide less deductions; above the limit a manager approves

**Decision.** `tradein.price_guide` gives the value of a model in each grade; `tradein.deduction` is a fixed amount or a percentage of the guide, and may be tied to a checklist item so that a failed item suggests it. The offer is the guide less every deduction, never below zero. A cashier without `TRADEIN.APPROVE` may agree up to the staff limit (default 50.000) and up to a percentage over the offer (default 10%); above either, or when there is no guide value, a manager approves with a PIN or remotely, and the approval is used once.
**Why.** Values must be consistent between staff and branches, and large or generous values need a second person.
**Cost.** The guide is kept by hand per model and grade; there is no price feed.

## ADR 49: A phone the business sold comes back to its own record

**Decision.** When a traded-in IMEI belongs to a device this business sold, accepting it reconnects that device (`device.reacquire`: sold → under inspection, at the new cost, with event "traded in") instead of creating a second device. At head office, a sold device whose IMEI is claimed again is retired as "superseded", not reported as a conflict. A device still in stock, on hold or under repair is refused (`TRD-004`).
**Why.** One IMEI is one phone (ADR 34); its history — sold, traded back, refurbished, sold again — stays on one page.
**Cost.** The device's earlier cost is replaced by the trade-in value; the earlier sale keeps its own cost of sale.

## ADR 50: The civil ID is encrypted on the server that took it, and only managers see it

**Decision.** The seller's civil ID is stored encrypted with the server's own key (`ISecretProtector`), with the last four characters kept in clear for display. A manager (`TRADEIN.APPROVE`) can show the full number; every view is audited. The business can switch off the requirement.
**Why.** A purchase from the public must be traceable to the seller, but a civil ID is personal data that most staff never need.
**Cost.** Head office cannot read a branch's civil IDs until data sync (Phase 12) re-encrypts them for head office.

## ADR 51: Consent is recorded with the declaration that was shown

**Decision.** The customer confirms the seller's declaration (kept in English and Arabic in the settings) either by typing their name on screen or on a signed paper form whose number is recorded. The declaration in force is copied onto the trade-in and printed on its document.
**Why.** If the device later turns out to be stolen, the business must show exactly what the seller declared.
**Cost.** None beyond storage.

## ADR 52: Trade-ins and credit payments need the server; emergency mode refuses them

**Decision.** The trade-in wizard is not offered in emergency mode, and an emergency sale paid with trade-in credit or store credit is refused at the till and again when uploaded. Margin-scheme phones still sell offline: the snapshot carries each phone's purchase price and the VAT-MARGIN code, and the upload checks the line against the phone actually sold.
**Why.** A trade-in checks the IMEI with head office and a credit balance must be locked while it is spent; an offline till can do neither safely.
**Cost.** During an outage the shop can sell, but not take phones in or let customers spend credit.

## ADR 53: One lock order for trade-ins and store credit

**Decision.** Every transaction that touches trade-in credit or store credit takes its locks in one order: the shift (when cash moves), the trade-ins by id, the customer's store credit, then document numbers and journals. Accepting and settling retry on a deadlock, as sales do.
**Why.** The first exit-gate run at 20 tills deadlocked: accepting a trade-in as store credit held the journal-number lock and waited for the customer's credit lock, while a settlement held the credit lock and waited for the journal number. With the fixed order the same run had no deadlock at all.
**Cost.** Locks are held a little longer in the busiest transactions.

## ADR 54: Repair writers lock the job row first

**Decision.** Every write to a repair job takes its locks in one order: the job row (`SELECT … FOR UPDATE`), then the shift (when the drawer moves), trade-ins, store credit, the job's deposit lock (`kite:deposit:<job>`, an advisory lock taken by the credit ledger's trigger), document numbers, journals, audit and the outbox. Writers run inside `PosEndpoints.WithRetryAsync`.
**Why.** A job can be touched from several places at once: two tills taking a deposit or collecting it, the workshop moving it on, and a WhatsApp approval arriving through the gateway. Locking the job first makes them queue, so only one collection and one refund of the same money can succeed (the Phase 8 exit gate races both).
**Cost.** Writes to one busy job wait for each other; writes to different jobs do not.

## ADR 55: A deposit is drawer money held for the customer, applied to the invoice at collection

**Decision.** A deposit is taken at the till with its own number (`DEP-…`) and receipt, and posts `repair.deposit`: debit the tender, credit 2300 customer deposits. It counts in the shift's drawer figures. At collection the repair invoice (a tax invoice, issued once) applies the deposit as a `deposit` tender (debit 2300) and takes the rest by any tender; the excess goes back in cash or as store credit (2310). Refunds post `repair.deposit_refund`.
**Why.** Until the work is done and accepted the deposit is the customer's money, not revenue; one invoice at delivery carries the real charges (approved estimate, or the diagnosis fee for a cancelled or unrepairable job).
**Cost.** Deposits and collections need the server and an open shift: emergency mode cannot take them.

## ADR 56: Whether a deposit is a VAT point is the business's setting, off by default

**Decision.** `repair.settings.deposit_vat_point` (default off) decides whether VAT is charged when a deposit is taken (`RepairMath.DepositTax`: amount × rate / (100 + rate)). Only a user with `TAX.MANAGE` can change it. When on, the VAT held moves with the deposit: its share goes back with a refund or an excess (`TaxShare`), and the applied share reduces the invoice's VAT due (`debit 2200 deposit_tax`).
**Why.** Whether an advance payment creates a tax point depends on how the business takes deposits; that is a decision for the business and its tax adviser, not for the software.
**Cost.** One more switch for accountants; the arithmetic is shared by the server, the till and an independent Python implementation through `tests/vectors/repair-math-vectors.json`.

## ADR 57: The repair workflow is enforced by the database, with clearer checks in front of it

**Decision.** The database holds the blueprint's transitions (`repair.transition`) and refuses any other (KT040 → REP-001); entering in progress, waiting for parts, testing or completed needs the current estimate approved (KT041 → REP-002); a job's deposits never go below zero (KT042 → REP-003); a delivered job cannot change except to delete its passcode; status history and assignments are written by triggers. The server checks the same things first so that the message says what to do (REP-005: "write the estimate first", "more than the customer authorised"). The pre-authorised ceiling is the larger of the job's pre-authorised amount and the highest estimate the customer approved by signature, phone or WhatsApp.
**Why.** The exit gate requires every disallowed move to be rejected, whoever makes it; the database is the one place no path can skip.
**Cost.** Two layers to keep in step; the SQL tests cover all 132 status pairs (22 allowed, 110 refused).

## ADR 58: WhatsApp goes out from the main server's gateway only

**Decision.** Every server writes messages to its own outbox. The main server's gateway receives them (branches relay over `/sync/v1/notif/push` with their client certificates) and sends them through a provider adapter: Meta's WhatsApp Cloud API, or the test chat that never leaves the server. The gateway sends a template only with the customer's consent, holds back customers who sent STOP, sends typed text only within 24 hours of the customer's last message, drops messages older than the stale limit and stops at the daily cap. Delivery events and replies go back to the server that queued the message, in order. A tap on Approve approves the estimate (method `whatsapp`, reference the message id) only if it is still the current version. Messages never carry passcodes or civil IDs.
**Why.** One WhatsApp number and one Meta webhook per business, and branches that work while head office is away.
**Cost.** Branch messages wait for the relay; the main server needs an internet address for Meta's webhook (ADR 59).

## ADR 59: The main server's internet address comes from Nextdab, and only the webhook answers through it

**Decision.** Nextdab Admin makes a Cloudflare tunnel `kme-<name>` and the address `https://<name>.nextdab.com` for the customer's main server, and sends it in a signed `web` command at check-in: the address, the tunnel token, and where to get cloudflared with its SHA-256 and size. KITE keeps the token encrypted (`lic.web_state`), stores the command with the token replaced, verifies the download before running it with `TUNNEL_TOKEN`, and reports the address and tunnel state (never the token). The tunnel reaches only a loopback port (8791); on that port, or for requests carrying Cloudflare's headers, only `/wa/webhook` and `/wa/ping` answer. If the port is taken the server starts without the webhook and says so.
**Why.** Meta must reach the webhook, but the rest of KITE stays inside the shop's network; the customer does not have to run a tunnel themselves.
**Cost.** Needs Nextdab Admin 5.19 and KITE 0.8.0 or newer on the main server; branch servers never get an address.

## ADR 60: A repair invoice is not voided or returned at the till

**Decision.** The till refuses to void, return or credit-sell a repair invoice (REP-002). A repair that fails goes back to the workshop as a warranty claim on the delivered job: at no charge, approved as `warranty`, its parts' cost posted to warranty expense (6600) at delivery.
**Why.** The invoice applied the customer's deposit, cleared the job's work in progress and started its warranty; undoing it at the till would leave the job, its deposit and its accounts disagreeing.
**Cost.** A genuine billing mistake on a repair invoice needs its own path. Phase 9 adds it at the till, for a manager (ADR 66), and it reverses the technician's commission.

## ADR 61: Audit and outbox rows are written just before the commit

**Decision.** `DbTx.AuditAsync` and `DbTx.PublishAsync` collect their rows; `KiteDb.InTransactionAsync` writes them, audit entries first and then outbox events, after the work and just before it commits. A transaction that fails writes neither. Two counters that relied on the audit lock to serialise (the module-switch event's version and the CRL number) take their own advisory locks.
**Why.** The audit chain and the outbox each take one server-wide advisory lock that is held until commit. Taken in the middle of a transaction, every lock taken after them could deadlock with another transaction that held that lock and was waiting to audit; the full test suite showed a dozen such deadlocks a run under load (retried, but not acceptable at the exit gate). Taken last, a transaction that holds them never waits for anything else: the suite now runs with none.
**Cost.** Code cannot read its own audit or outbox rows inside the transaction that wrote them (none did). The order of rows within a transaction is unchanged.

## ADR 62: The latest approved estimate decides what a job holds in stock

**Decision.** The parts of a job's latest approved estimate, less what is fitted, are reserved in the branch's stock (`inv.reservation`, one row per job and part; the balance's `reserved` column follows it in an AFTER trigger). What the branch lacks is a parts request, ordered by when it was opened. Stock is given out oldest request first: a job reserving now cannot take what an older request is waiting for. A job sent to repair while short waits for parts, and moves to in progress by itself when its parts are given out (a background filler every 10 seconds, or the "give out now" button). The till cannot sell what is reserved or waited for. Reservations go when the job ends; a new estimate version waiting for the customer does not release them. Balances are locked through `inv.lock_part_balance` (the application only reads balances), job first and then balances in part order (ADR 54), and before the journal of a fitting takes its number.
**Why.** The blueprint's "parts reserved on approval" without a race: two jobs and a till cannot all count the same last part.
**Cost.** A part waited for by a job cannot be sold over the counter even if the job is far away in the queue; staff see why in the message.

## ADR 63: Serial-tracked spare parts are estimated by quantity and fitted by serial

**Decision.** A spare part may be tracked by serial number. Estimates and invoices carry it by quantity (whole units); fitting scans the serial, which moves that unit out of stock at its own cost and marks it `fitted`; returning it brings the same unit back (`unfitted`), and writing it off ends it. The job's work lines keep the serial.
**Why.** A logic board or a camera module has its own serial and cost; the customer's invoice does not need to show it, the job and the stock do.
**Cost.** A serial part is fitted one unit at a time.

## ADR 64: A commission is written once and never changed

**Decision.** Commissions are generated in the same transaction as the repair invoice (or when it is paid, if the business chooses), per technician share, from the most specific rule in force on the invoice date (technician and kind of repair, technician, branch and kind, kind, branch, everyone). Each accrual carries its rule version, the figures it was worked out from and a trace, and its journal (Dr 6300 / Cr 2400). The unique key on job, technician, kind and invoice version makes a second accrual impossible, however often or concurrently the generator runs. An accrual is approved (at the branch or only at head office), paid in exactly one payout line, or reversed by its exact opposite; a paid accrual's reversal becomes an approved deduction from the next payout. Rules are versioned, never edited: two active rules of the same scope never overlap in time.
**Why.** "Duplicate generation impossible" (row 9 of the exit gate), and a history that explains every amount to the technician.
**Cost.** Correcting a commission means reversing it and generating a new version, not editing it.

## ADR 65: Repair profit uses the job's own costs

**Decision.** The profit basis is labour and parts charged on the invoice, without VAT and after discounts, less the parts cost in the job's work in progress and outside work at cost, never below zero (a minimum still applies). The arithmetic (fixed amount, rate of labour or profit, minimum and maximum per job, then the share, each rounded half away from zero to the baisa) is shared by the server (`CommissionMath`), the web app (`@kite/pos commission.ts`) and an independent Python implementation through `tests/vectors/commission-math-vectors.json` (122 vectors; CI regenerates them and fails on any difference).
**Why.** The commission should follow what the job really earned, including what another workshop charged for it.
**Cost.** Outside work recorded after delivery does not change a commission already generated.

## ADR 66: A delivered repair invoice is corrected at the till by a manager

**Decision.** `POST /api/v1/repairs/{id}/correct` (permission `REPAIR.INVOICE.CORRECT`, branch managers and owners) credits the invoice that stands in full (credit reason `correction`; nothing goes back to stock: the parts are in the customer's device), refunds from the drawer or to store credit, and may issue a corrected invoice paid at the same till. The job keeps its record (`repair.invoice_correction`; the chain decides which invoice stands). Commissions on the credited invoice are reversed and generated again from the corrected one. The request is idempotent by its id. Its locks follow ADR 54: job, the job's commissions, shift, store credit, both document numbers, then journals.
**Why.** The owner chose the till: the customer is usually there, the money moves through the drawer, and the till already counts it.
**Cost.** Needs an open shift; head office cannot correct an invoice without a till.

## ADR 67: Commission payouts are cash, bank or payroll, and payroll is a clearing account

**Decision.** A payout pays approved commissions of one technician at one branch (a document `COM-B01-…`, one line per accrual, the total above zero). It posts Dr 2400 / Cr 1100 cash, 1200 bank or 2410 payroll clearing. Racing payouts for the same accruals lock them in order: one succeeds, the others are refused (COM-002).
**Why.** Many shops pay commissions with salaries; the payroll system then clears 2410.
**Cost.** KITE does not run payroll; 2410 must be cleared by the accountant when salaries are paid.


## ADR 68: Head office decides credit online, under one lock per customer

**Decision.** A customer's credit account (limit, terms, status, offline cap, branch allowances) belongs to head office (the main or combined server). A sale on account is decided under the customer's credit lock (`kite:credit:<customer>`): available = limit − exposure, where exposure is the receivable balance plus live authorisations. Over the available credit a manager approves with their PIN (`CREDIT.OVERRIDE`, as for prices and discounts; a cashier who holds the permission approves for themselves) up to the policy's override cap (default 50.000); past the cap, on hold or closed, the sale is refused. Every sale on account stores its decision (available, limit, exposure, how much over and who approved). A branch server asks head office first (`/sync/v1/credit/authorise`, answer within the policy's 2 seconds): head office reserves the amount for 15 minutes and counts the branch's charges it has not seen yet. The lock comes after trade-ins and store credit and before any document number: job → shift → trade-ins → store credit → **credit** → deposit → document numbers → stock → journals (ADR 54, 61).
**Why.** "Online limit" (the Phase 10 exit gate): twelve tills racing for one limit must not sell more than it, which needs one place that decides and one lock.
**Cost.** A branch server online waits up to 2 seconds for head office on each sale on account.

## ADR 69: Offline, a branch sells on credit within its allowance, a cap per sale and what it last knew

**Decision.** When head office does not answer, a branch server decides by itself: available = the lower of (its allowance − its charges since the last sync) and (limit − the exposure it last heard − its charges since the last sync), never below zero and never above the cap per sale (the account's, or the policy's). The same approval and cap rules apply. Head office sets the allowances; together they may not exceed the limit unless the policy allows it (KT071 in the database).
**Why.** The blueprint's test: "two branches offline both selling on credit — allowances respected offline". A branch cannot know what another branch is selling, so each spends only what was set aside for it.
**Cost.** Two offline branches can still, together, go over the limit when exposure grew after their last sync; that is found on sync (ADR 70).

## ADR 70: A breach is found on sync and holds the account until its cause clears

**Decision.** Branch servers push what they sold on account (`/sync/v1/credit/push`, entries with their instalments and decisions, idempotent by entry). Head office takes them into the customer ledger, allocates, and when exposure is above the limit records a breach (which branch, how much, what was owed against what limit) and puts the account on hold (`hold_kind = exposure`). An automatic hold (exposure or overdue) lifts itself when its cause clears; a hold put by hand stays until someone releases it. Holds and releases are kept with their reason and who made them.
**Why.** "Breach detected and hold applied on sync" (the blueprint's test table).
**Cost.** Between the offline sales and the next sync, the customer may owe more than the limit.

## ADR 71: Receivables are the customer ledger, in instalments, settled oldest first at head office

**Decision.** `crm.credit_ledger` gains the balance type `receivable`, with kinds charge, payment, credit note, write-off, opening balance and transfer; rows are never changed. A charge is split into instalments (`crm.receivable_item`; up to the policy's 12, the spare baisa on the first ones, each due its terms or interval after the last; the database checks they add up to the charge). Payments, credit notes and write-offs are allocated by head office only, oldest due first (manual allocations first; a credit note settles its own sale first), in `crm.receivable_allocation`. A sale returned or a repair invoice corrected goes back on account up to what it charged there (CRD-009). The customer's account (1300) in the books always equals the customer ledger, branch by branch, with the customer as the journal line's party.
**Why.** Statements, ageing and reminders need what is due when; one place allocating avoids two branches settling the same instalment.
**Cost.** On a branch server, its own payments wait for head office to be allocated.

## ADR 72: A payment received at another branch is settled between branches in one journal

**Decision.** When a payment at one branch settles an instalment charged at another, two transfer rows move the receivable (+ at the receiving branch, − at the charging branch) and one journal `credit.interbranch` posts both sides: the receiving branch Dr 1300 / Cr 2500 (it owes the other branch), the charging branch Dr 1310 / Cr 1300 (the other branch owes it). The journal is numbered at the acting branch (`Posting.PostManyAsync`), so one transaction takes only one branch's journal series.
**Why.** Each branch's receivables, and its own books, stay true: the branch that sold is still owed, now by the branch that holds the money.
**Cost.** The interbranch accounts (1310, 2500) are settled between branches by the accountant.

## ADR 73: Branch servers keep a copy of the credit accounts, sent with the heartbeat

**Decision.** The heartbeat carries the credit policy, the accounts changed since the branch's last sequence (with that branch's allowance, the exposure and the oldest due date) and the suppliers changed since its last sequence. The branch writes them in replica mode (`SET LOCAL kite.credit_replica = 'on'`), which lets it write rows that otherwise only head office may change, without bumping their versions. Customers arrive with their accounts; a customer number already used at the branch gets the suffix `-HO`. Store credit stays per server, as in Phase 7.
**Why.** Offline decisions (ADR 69) need the limit, the allowance and the last known exposure at the branch.
**Cost.** A branch's copy is as old as its last heartbeat; the till says so ("Figures from the last sync with head office").

## ADR 74: Suppliers are one list for the business; a branch may add one for head office to approve or merge

**Decision.** `purch.supplier` is business-wide. Head office numbers suppliers `S-00001`; a branch server offline adds a provisional one (`SP-B02-2026-000001`) that head office approves (it gets an `S-` number) or merges into one it already has (KT072: only into an active supplier; merged stays merged). The supplier ledger is per branch; Phase 10 writes opening balances only (Dr 3900 / Cr 2100, the supplier the journal line's party); purchase orders, receipts, invoices and payments come with purchasing (Phase 11).
**Why.** Duplicates are the usual problem with supplier lists; merging keeps both histories and one supplier to pay.
**Cost.** A provisional supplier cannot be paid until head office approves it.

## ADR 75: The customer and supplier ledgers are checked against the books every day

**Decision.** Every day (and on demand: Customer credit → Ledger check → Check now) each branch's own customer ledger is compared with account 1300 (debit − credit) and its supplier ledger with 2100 (credit − debit), and the result is kept (`acct.subledger_check`). A difference is reported, never corrected.
**Why.** "Statements reconcile" (row 10 of the exit gate), every day and not just in a test.
**Cost.** None found; a difference needs an accountant to explain it.

## ADR 76: Overdue instalments need a manager, then hold the account, with reminders by WhatsApp

**Decision.** When an instalment is more than the policy's days overdue (30), new credit needs a manager's approval; after more days (60) head office holds the account automatically (`hold_kind = overdue`) and lifts the hold when the overdue is paid. Customers who agreed to WhatsApp get a reminder a few days before an instalment is due (3) and then every few days while it is overdue (7), through the Phase 8 outbox with the templates `credit_due` and `credit_overdue`.
**Why.** Credit control without someone checking every account by hand.
**Cost.** The hourly worker runs on head office only.

## ADR 77: Credit arithmetic is shared and checked against an independent implementation

**Decision.** Available online and offline, the decision, instalments, oldest-first allocation and ageing are in `CreditMath` (server) and `@kite/pos credit.ts` (web app), and both are tested with `tests/vectors/credit-math-vectors.json`, written by an independent Python implementation (`tests/vectors/generators/credit_vectors.py`); CI regenerates the vectors and fails on any difference.
**Why.** The till shows what is available and splits instalments before the server decides; both must agree to the baisa.
**Cost.** A change to a rule is made in three places.

## ADR 78: Purchasing takes its locks after credit and before numbers, in one order

**Decision.** The order of advisory locks (ADR 54, 61, 68) gains three levels between the deposit and document numbers: the purchase order (`kite:po:<id>`), then the goods receipts and outside work an invoice or return touches (`kite:grn:<id>`, `kite:grni:<type>:<id>`, taken sorted), then the supplier (`kite:supplier:<id>`). Receiving locks the order; invoicing and returning lock the receipts and then the supplier; paying locks the supplier. Each then reads what is still open inside the lock and refuses what would go past it (PUR-002 more than is open, PUR-005 more than is owed).
**Why.** Two people receiving the last units of one order line, or invoicing and returning the same receipt line, must not both succeed. One order everywhere means no deadlocks.
**Cost.** Work on one supplier is serialised at one server; it is short (a few rows and one journal).

## ADR 79: Periods are closed per branch, and a late document lands in the first period open at every branch it touches

**Decision.** `acct.fiscal_period` keeps a row per branch and month (12 months and a closing period 13). A period is open, soft-closed (only users with ACCT.PERIOD.CLOSE may post into it, by setting `kite.allow_soft_closed` for that one insert) or hard-closed. The accountant closes one branch or all branches at once. A document dated in a closed period (a receipt keyed late, an invoice that arrives after the month is closed) keeps its document date and is journalled on the first day of the first period open at every branch its journal touches; the journal says so. A period a filed VAT return covers cannot be reopened (ACC-006), and nothing in a closed year can (ACC-008).
**Why.** Branches close at different speeds; a late invoice is normal and must not be refused or reopen a month that has been reported.
**Cost.** A document's date and its journal's date can differ; reports by journal date and documents by document date can disagree for a late document, and the journal line explains it.

## ADR 80: Goods received not invoiced is its own ledger, per source line

**Decision.** Every receipt line, receipt charge and outside-work line (repair work sent out, Phase 9) accrues to GRNI (2110) when received, as an `accrued` entry in `purch.grni_entry` at the receiving branch; an invoice clears it with an `invoiced` entry (all that is open when it invoices all that is open, its share otherwise), and a return before invoicing reverses it with a `returned` entry. The open GRNI of a line is the sum of its entries; zero entries are not written.
**Why.** The three-way match needs to know what each receipt line is still owed for, and the ledger check (ADR 88) needs a subledger for 2110.
**Cost.** One more table written in the same transaction as the receipt, invoice or return.

## ADR 81: An invoice's price difference goes to stock for what is still on hand and to purchase price variance for the rest

**Decision.** An invoice line is matched against its receipt line at the order's price (or the receipt's): within tolerance when the unit price is within the policy's percentage (2%) or the line within its amount (1.000 OMR), else `price`; more than is open is `qty` and refused (PUR-002). Posting a line outside tolerance needs PURCH.INVOICE.OVERRIDE and a reason. The difference between the invoice line and what it clears is split by `PurchaseMath.SplitVariance`: the share for units still in stock (serialised units in stock, or the branch's stock balance) goes to inventory (the units' cost is corrected, or a `cost_adjustment` stock movement for quantity items), the rest to purchase price variance (5300).
**Why.** Stock should carry what the goods really cost; what has already been sold cannot be re-costed, so its share is an expense of the period.
**Cost.** The split depends on what is on hand when the invoice is posted, so the same invoice posted a day later can split differently.

## ADR 82: A supplier invoice is booked at one branch; the goods' branches clear their own GRNI through interbranch lines

**Decision.** The invoice, its VAT, the supplier ledger entry and the payable (2100) are at the booking branch. Each receiving branch's part of the journal clears its own GRNI and takes its own variance and expenses, and is owed by the booking branch through due to branches (2500) and due from branches (1310), in one journal posted with `PostManyAsync`. Each branch's part balances on its own.
**Why.** Head office often receives the invoice for goods delivered to a branch; each branch's books stay complete and the interbranch accounts eliminate when consolidated (ADR 89).
**Cost.** Input VAT is reclaimed at the booking branch, not where the goods went.

## ADR 83: Supplier payments settle the oldest open items at the paying branch, unless head office names them

**Decision.** A payment is recorded in the supplier ledger at the paying branch. Without allocations it settles that branch's open invoices oldest first; head office may name invoices of any branch, and each other branch's share is settled between branches in the same journal (as for customers, ADR 72). What is left is an advance, which settles the next invoice posted for that supplier at that branch (as does a debit note). Paying more than is owed in named allocations is refused (PUR-005); a provisional supplier cannot be paid (ADR 74).
**Why.** Branches pay their own suppliers; head office often pays for everyone.
**Cost.** An advance waits at its branch; head office moves it by naming the invoices.

## ADR 84: Goods returned to a supplier come off GRNI first, then on a debit note at the invoiced price

**Decision.** A return takes back units of a receipt line. What has not been invoiced reverses its GRNI; what has been invoiced is raised on a debit note (`DN-`) at the invoice's price with its VAT, which reduces the supplier's balance and input VAT. Stock goes out at its carrying cost; any difference between that and what is credited (GRNI back plus the debit note's net) is purchase price variance. Serialised units are named by IMEI.
**Why.** The supplier credits what it invoiced; stock leaves at what it cost us; the difference is a real gain or loss.
**Cost.** A return of units bought at different prices on one line uses the line's average.

## ADR 85: The VAT return is worked out from the documents by the date of their journals, and settled branch by branch

**Decision.** `tax.vat_lines` gathers every VAT amount from the documents (sales and credit notes, VAT on deposits, purchase invoices, debit notes, expenses) by the date of the journal that booked it, so a late document falls in the period it was posted to, and each box adds them up by tax kind and class; boxes 5b and 6d are adjustments the accountant enters. A return is prepared for a quarter (`VAT-2026-10`), and filed only when its months are closed at every branch (ACC-007) and the box definitions marked for review have been confirmed by an accountant. Filing posts a settlement journal (`vat.settlement`), dated the day of filing (or the first period open after it, ADR 79), with a part per branch moving VAT output (2200) and VAT input (1500) into VAT payable (2210); adjustments go to 2210 against general expenses (6900) for the accountant to reclassify. Paying posts VAT payable against the bank. The return page also shows the VAT accounts' own movements beside the boxes ("VAT report vs VAT accounts"), which must agree.
**Why.** The Oman return is per business, but the books are per branch; working from documents shows where each figure came from, and checking against the accounts catches a posting rule that is wrong.
**Cost.** A posting made directly to a VAT account by a manual journal is not on the return; the check shows it as a difference.

## ADR 86: Documents that must not change are written once, and link their journal once

**Decision.** Expenses and card settlements post their journal first and are inserted with its id (their rows cannot be updated). Goods receipts, supplier returns and supplier payments are inserted first (their stock and ledger rows refer to them) and get their journal id once: the app role may update that one column (column grant), and the trigger `purch.journal_once` refuses a second link or any other change (KT082).
**Why.** The audit trail must show a document and its journal as they were posted.
**Cost.** Two patterns for one rule, because of the order in which rows refer to each other.

## ADR 87: A nil VAT return is filed with no settlement journal

**Decision.** A return whose output and input are both zero is filed with no settlement journal; a return can have a settlement or payment journal only when filed.
**Why.** A small branch with no sales in a quarter still files; a journal of zeros helps no one.
**Cost.** None found.

## ADR 88: The daily ledger check compares each subledger with what its own documents posted to the control account

**Decision.** The Phase 10 check (ADR 75) now covers, per branch: customers (1300), suppliers (2100), GRNI (2110), inventory (stock movement value against 1400–1430), VAT output and VAT input, store credit, deposits, repair WIP (the VAT accounts excluding the settlement journals), and the trial balance (debits equal credits); and once for the business, interbranch (1310 equal to 2500). Each control side counts only journal lines posted by the documents of that subledger at that server ("own origin"), so a manual journal into a control account shows as a difference to be explained rather than being hidden. Accounting → Ledger check shows the last run and runs it on demand.
**Why.** Row 11 of the exit gate: subledgers equal control accounts, the trial balance balances and interbranch eliminates, every day.
**Cost.** Each new subledger needs its pair added to the check.

## ADR 89: Statements are as of the latest data each branch has sent, and consolidate by eliminating interbranch

**Decision.** The trial balance, ledger, profit and loss, balance sheet and cash flow can be run for one branch or all. Consolidated, 1310 and 2500 are eliminated against each other and any difference shows. Each statement says "Data as of" per branch: now for branches this server keeps, the last contact for a branch server whose journals arrive with sync. Earlier years' result not yet closed shows on its own line; the cash flow (indirect method, by each account's cash-flow class) shows any difference from the cash accounts' movement, which must be zero.
**Why.** Head office runs statements while branch servers may be offline; the reader must know how old each branch's figures are.
**Cost.** A consolidated statement can mix branches' figures of different ages.

## ADR 90: The year is closed once, at head office, into retained earnings

**Decision.** Year-end (ACCT.YEAR_END) needs all twelve periods closed at every branch; it posts one journal at head office taking, branch by branch, every revenue and expense balance into retained earnings (3300) in period 13, dated the last day of the year (`acct.year_end`), and then closes all periods of the year for good. Statements leave the year-end journal out unless asked.
**Why.** Profit and loss starts again at zero each year; nothing more may land in a closed year.
**Cost.** A correction found after year-end is posted in the new year.

## ADR 91: Purchase arithmetic is shared and checked against an independent implementation

**Decision.** Line amounts, spreading charges by value or quantity, matching with tolerance, GRNI clearing and the variance split are in `PurchaseMath` (server) and `@kite/pos purchase.ts` (web app), both tested with `tests/vectors/purchase-math-vectors.json` written by `tests/vectors/generators/purchase_vectors.py`; CI regenerates the vectors and fails on any difference.
**Why.** The receiving and invoice screens show the match before the server decides; both must agree to the baisa.
**Cost.** A change to a rule is made in three places.
