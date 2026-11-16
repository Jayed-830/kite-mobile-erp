// Phase 6 exit gate in a real browser against two real KITE servers - head office (role main) and the Sohar branch
// server - each on its own PostgreSQL database, plus the Nextdab stub. This script starts both servers itself so it can
// stop head office in the middle and start it again:
//   1. head office receives a phone; the branch tries to receive the same IMEI and head office's online check names B01
//   2. head office is stopped; the branch receives that IMEI anyway (typed by hand) and a clean one: both wait for head office
//   3. head office starts again; the branch's phones are sent: the clean one is confirmed, the other opens an IMEI conflict
//      and both phones are put on hold (head office's own directly, the branch's by an instruction with the heartbeat)
//   4. the branch corrects the mistyped IMEI where the phone is; the conflict closes by itself and both phones are released
//   5. head office blocks an IMEI; the list reaches the branch, which refuses to receive it
// Every state is checked through the API of the server concerned, not only on screen. Screenshots go to SHOTS_DIR.
//
//   KITE_HOST_DIR   src/server/Kite.Host (the built server and its web app)       MAIN_PG / BRANCH_PG  "Host=..;Port=..;Database=.."
//   STUB_URL        the Nextdab stub (http://127.0.0.1:8799)                        STUB_KEY_FILE        the stub's public key
//   WORK_DIR        data folders and server logs                                    SHOTS_DIR            screenshots
import { chromium } from 'playwright-core';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const HOST_DIR = path.resolve(process.env.KITE_HOST_DIR ?? '../src/server/Kite.Host');
const WORK = path.resolve(process.env.WORK_DIR ?? '/tmp/e2e6');
const STUB = process.env.STUB_URL ?? 'http://127.0.0.1:8799';
const STUB_KEY = fs.readFileSync(process.env.STUB_KEY_FILE ?? path.join(WORK, 'stub-key.txt'), 'utf8').trim();
const MAIN_PG = process.env.MAIN_PG ?? 'Host=localhost;Port=5432;Database=kite_main';
const BRANCH_PG = process.env.BRANCH_PG ?? 'Host=localhost;Port=5432;Database=kite_branch';
const shots = process.env.SHOTS_DIR ?? path.join(WORK, 'shots');
const MAIN = 'http://localhost:5080';
const BRANCH = 'http://localhost:5081';
const OWNER_PW = 'Muscat-Ruwi-2026!';
fs.mkdirSync(shots, { recursive: true });

// IMEIs with a correct check digit.
const HO_PHONE = '352099001761481';   // received at head office (B01)
const REAL = '354670112345733';       // the Sohar phone's real IMEI; staff typed HO_PHONE by mistake while head office was down
const CLEAN = '354670112345741';      // a second phone Sohar receives while head office is down
const STOLEN = '351756051523993';     // put on the blocked list at head office

const step = (s) => console.log(`- ${s}`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const check = (ok, what) => { if (!ok) throw new Error(what); console.log(`  ok: ${what}`); };

// ---- the two servers ---------------------------------------------------------------------------------------------
const servers = {};
function start(name) {
  const main = name === 'main';
  const env = {
    ...process.env, ASPNETCORE_ENVIRONMENT: 'Development',   // only so the stub's test licence key is accepted
    ASPNETCORE_URLS: main ? MAIN : BRANCH,
    ConnectionStrings__App: `${main ? MAIN_PG : BRANCH_PG};Username=kite_app;Password=e2e-app`,
    ConnectionStrings__Migrator: `${main ? MAIN_PG : BRANCH_PG};Username=kite_migrator;Password=e2e-migrator`,
    Kite__DataDir: path.join(WORK, `${name}-data`), Kite__Licensing__DevPublicKey: STUB_KEY,
    ...(main ? { Kite__Sync__Port: '5443', Kite__Sync__Hosts: 'localhost', Kite__Licensing__ControlUrl: STUB }
      : { Kite__Sync__Port: '0', Kite__Sync__HeartbeatSeconds: '3', Kite__Licensing__CheckinEnabled: 'false', Kite__WhatsApp__WebhookPort: '0' }),
  };
  const log = fs.openSync(path.join(WORK, `${name}.log`), 'a');
  const p = spawn('dotnet', ['bin/Release/net10.0/Kite.Host.dll'], { cwd: HOST_DIR, env, stdio: ['ignore', log, log] });
  servers[name] = p;
  return p;
}
async function ready(base, ms = 120000) {
  const end = Date.now() + ms;
  while (Date.now() < end) { try { if ((await fetch(base + '/health/ready')).ok) return; } catch { /* starting */ } await sleep(1000); }
  throw new Error(`${base} did not become ready`);
}
async function stop(name) {
  const p = servers[name];
  if (!p || p.exitCode !== null) return;
  const gone = new Promise((r) => p.once('exit', r));
  p.kill('SIGTERM');
  await Promise.race([gone, sleep(20000)]);
  if (p.exitCode === null) { p.kill('SIGKILL'); await gone; }
}

async function api(base, method, p, body, token) {
  const r = await fetch(base + p, { method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  if (!r.ok) throw new Error(`${method} ${base}${p}: ${r.status} ${text}`);
  return text ? JSON.parse(text) : null;
}
const token = async (base) => (await api(base, 'POST', '/api/v1/auth/login', { username: 'owner', password: OWNER_PW })).accessToken;
const unitOf = async (base, imei, t) => {
  const { id } = await api(base, 'GET', `/api/v1/devices/lookup/${imei}`, undefined, t);
  return api(base, 'GET', `/api/v1/devices/${id}`, undefined, t);
};
/** Waits until the check passes; the servers talk on their own schedule (heartbeat every 3 s). */
async function until(what, checkFn, ms = 90000) {
  const end = Date.now() + ms;
  for (;;) {
    try { if (await checkFn()) { console.log(`  ok: ${what}`); return; } } catch { /* not yet */ }
    if (Date.now() > end) throw new Error(`timed out waiting for: ${what}`);
    await sleep(1000);
  }
}

// ---- the browser -------------------------------------------------------------------------------------------------
const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH ?? '/usr/bin/google-chrome', args: ['--no-sandbox'] });
const office = await (await browser.newContext({ viewport: { width: 1366, height: 900 } })).newPage();
const sohar = await (await browser.newContext({ viewport: { width: 1366, height: 900 } })).newPage();
const pageErrors = [];
for (const p of [office, sohar]) p.on('pageerror', (e) => pageErrors.push(e.message));
let n = 0;
const shot = async (p, name, opts = {}) => { n += 1; const f = path.join(shots, `${String(n).padStart(2, '0')}-${name}.jpg`); await p.waitForTimeout(400); await p.screenshot({ path: f, type: 'jpeg', quality: 74, fullPage: opts.full ?? false }); console.log(`  screenshot ${f}`); };
const signIn = async (p, base) => {
  await p.goto(base + '/login');
  await p.locator('input[autocomplete="username"]').fill('owner');
  await p.locator('input[autocomplete="current-password"]').fill(OWNER_PW);
  await p.locator('form button[type="submit"]').click();
  await p.getByRole('heading', { name: /^(Welcome, |مرحبًا، )/ }).waitFor({ timeout: 30000 });
};
const nav = (p, name) => p.getByRole('link', { name, exact: true }).click();
/** Inventory > Adjust stock: one iPhone line, IMEIs typed into the scanner; waits for each server check to come back. */
async function startReceiving(p, imeis, typed = []) {
  await nav(p, 'Inventory');
  await p.getByRole('tab', { name: 'Adjust stock' }).click();
  await p.getByLabel('Document').selectOption('opening');
  await p.getByLabel('Add an item').fill('IP13');
  await p.locator('.picker-item', { hasText: 'iPhone 13 128GB' }).click();
  await p.getByLabel('Unit cost').fill('180.000');
  const scanner = p.getByLabel('Scan the IMEIs');
  // A scanner types a whole code at once; a person types digit by digit (the app tells them apart and records which).
  for (const imei of imeis) { if (typed.includes(imei)) await scanner.pressSequentially(imei, { delay: 90 }); else await scanner.fill(imei); await scanner.press('Enter'); await sleep(200); }
  await p.getByText(`${imeis.length} scanned`).waitFor();
  for (let i = 0; i < 60 && (await p.locator('[data-testid="scanned-unit"]', { hasText: '…' }).count()) > 0; i++) await sleep(250);
}
async function post(p) {
  await p.getByRole('button', { name: 'Post' }).click();
  await p.getByRole('tab', { name: 'Stock on hand' }).waitFor();
}

try {
  fs.mkdirSync(WORK, { recursive: true });
  step('both servers start; head office is set up with a licence (3 branches), the device module and an iPhone item');
  start('main'); start('branch');
  await ready(MAIN); await ready(BRANCH);
  const code = (name) => fs.readFileSync(path.join(WORK, `${name}-data`, 'setup-code.txt'), 'utf8').trim();
  await api(MAIN, 'POST', '/api/v1/setup', {
    setupCode: code('main'), serverRole: 'main',
    business: { legalNameEn: 'Al Noor Mobiles LLC', legalNameAr: 'شركة النور للهواتف', tradeNameEn: 'Al Noor Mobiles', tradeNameAr: 'النور للهواتف', vatNumber: 'OM1100223344', fiscalYearStartMonth: 1, pricesIncludeTax: true },
    headOffice: { code: 'B01', nameEn: 'Muscat - Ruwi', nameAr: 'مسقط - روي' },
    owner: { username: 'owner', displayNameEn: 'Salim Al Harthy', displayNameAr: 'سالم الحارثي', password: OWNER_PW, preferredLanguage: 'en' },
  });
  let ho = await token(MAIN);
  const mid = (await api(MAIN, 'GET', '/api/v1/licence', undefined, ho)).installationId;
  const { file } = await (await fetch(STUB + '/stub/licence', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ mid, customer: 'Al Noor Mobiles LLC', kind: 'subscription', validUntil: new Date(Date.now() + 365 * 864e5).toISOString().slice(0, 10), branches: 3, terminals: 10, graceDays: 30 }) })).json();
  await api(MAIN, 'POST', '/api/v1/licence/import', { fileText: file }, ho);
  await api(MAIN, 'PUT', '/api/v1/settings/modules', { changes: { device: true } }, ho);
  const product = async (base, t) => {
    const vat = (await api(base, 'GET', '/api/v1/tax/codes', undefined, t)).find((c) => c.code === 'VAT-STD').id;
    await api(base, 'POST', '/api/v1/catalog/products', { nameEn: 'iPhone 13 128GB', nameAr: 'آيفون 13 سعة 128 جيجا', productType: 'device', tracking: 'imei', taxCodeId: vat, defaultWarrantyDays: 365, variants: [{ sku: 'IP13-128', barcodes: ['6291000000103'], price: 220, minPrice: 200 }] }, t);
  };
  await product(MAIN, ho);

  step('branch B02 (Sohar) is added at head office and its server joins with a one-time token');
  const b02 = await api(MAIN, 'POST', '/api/v1/branches', { code: 'B02', nameEn: 'Sohar', nameAr: 'صحار' }, ho);
  const reg = await api(MAIN, 'POST', `/api/v1/branches/${b02.id}/registration-tokens`, undefined, ho);
  const inspect = await api(BRANCH, 'POST', '/api/v1/setup/join/inspect', { mainUrl: 'https://localhost:5443', setupCode: code('branch') });
  check(inspect.fingerprintCode === reg.fingerprintCode, `the branch sees head office's security code ${reg.fingerprintCode}`);
  await api(BRANCH, 'POST', '/api/v1/setup/join', { mainUrl: 'https://localhost:5443', setupCode: code('branch'), token: reg.token, confirmedFingerprint: inspect.fingerprint, serverName: 'SOHAR-PC' });
  let br = await token(BRANCH);
  await product(BRANCH, br);   // the catalog is copied to branches by the full sync of Phase 12; here the item is made on both
  await until('the branch reaches head office', async () => (await api(BRANCH, 'GET', '/api/v1/devices/central-status', undefined, br)).headOfficeReachable === true);

  step('1. head office receives an iPhone; it is confirmed at once');
  await signIn(office, MAIN);
  await startReceiving(office, [HO_PHONE]);
  await post(office);
  await until('head office phone is in stock and confirmed', async () => { const d = await unitOf(MAIN, HO_PHONE, ho); return d.unit.status === 'in_stock' && d.central?.state === 'verified'; });
  await nav(office, 'Devices');
  await office.getByText(HO_PHONE).first().waitFor();
  await shot(office, 'head-office-device-list');

  step('   Sohar tries to receive the same IMEI: head office is asked online and names where the phone is');
  await signIn(sohar, BRANCH);
  await nav(sohar, 'Devices');
  await sohar.getByTestId('central-banner').getByText(/Head office is reachable/).waitFor({ timeout: 30000 });
  await startReceiving(sohar, [HO_PHONE]);
  const online = sohar.locator('[data-testid="scanned-unit"]', { hasText: HO_PHONE });
  await online.getByText(`IMEI ${HO_PHONE} is already registered: in stock at B01 (iPhone 13 128GB).`).waitFor({ timeout: 20000 });
  check(await sohar.getByRole('button', { name: 'Post' }).isDisabled(), 'Sohar cannot post a phone head office already has');
  await shot(sohar, 'branch-online-check-names-B01');
  let refused = '';
  try { await api(BRANCH, 'POST', '/api/v1/inventory/adjustments', { branchId: b02.id, kind: 'opening', note: 'api', lines: [{ variantId: (await api(BRANCH, 'GET', `/api/v1/pos/items?q=IP13&branchId=${b02.id}`, undefined, br))[0].variantId, qty: 1, unitCost: 180, reasonCode: 'opening', units: [{ imei1: HO_PHONE, condition: 'brand_new', status: 'in_stock' }] }] }, br); }
  catch (e) { refused = String(e.message); }
  check(refused.includes('409') && refused.includes('DEV-007') && refused.includes('B01'), `the server refuses it too (DEV-007, naming B01), not only the screen [${refused.slice(0, 300)}]`);

  step('2. head office is stopped; Sohar receives the phone anyway (IMEI typed by hand) and a second one');
  await stop('main');
  await until('the branch notices head office is gone', async () => (await api(BRANCH, 'GET', '/api/v1/devices/central-status', undefined, br)).headOfficeReachable === false, 60000);
  await nav(sohar, 'Devices');
  await sohar.reload();
  await sohar.getByTestId('central-banner').getByText(/Head office cannot be reached now/).waitFor({ timeout: 20000 });
  await shot(sohar, 'branch-head-office-unreachable');
  await startReceiving(sohar, [HO_PHONE, CLEAN], [HO_PHONE]);
  check((await sohar.getByText('Head office not reachable: checked in this branch only').count()) === 2, 'each scan says it was checked in this branch only');
  await shot(sohar, 'branch-receives-offline');
  await post(sohar);
  await sohar.getByText('2 devices were received while head office could not be reached. They are confirmed as soon as head office answers.').waitFor();
  await shot(sohar, 'branch-offline-receipt-pending-notice');
  for (const imei of [HO_PHONE, CLEAN]) check((await unitOf(BRANCH, imei, br)).central?.state === 'pending', `${imei} waits for head office at Sohar`);
  await nav(sohar, 'Devices');
  await sohar.locator('.badge', { hasText: 'Head office check pending' }).first().waitFor();
  await shot(sohar, 'branch-devices-pending');

  step('3. head office starts again: the clean phone is confirmed; the duplicate opens an IMEI conflict and both phones are held');
  start('main');
  await ready(MAIN);
  ho = await token(MAIN);
  await until('Sohar\'s clean phone is confirmed', async () => (await unitOf(BRANCH, CLEAN, br)).central?.state === 'verified', 90000);
  await until('Sohar\'s duplicate is held for the conflict', async () => { const d = await unitOf(BRANCH, HO_PHONE, br); return d.unit.status === 'quarantined' && d.holds?.some((h) => h.reason === 'imei_conflict' && !h.releasedAt); }, 90000);
  const conflicts = await api(MAIN, 'GET', '/api/v1/devices/conflicts?status=open', undefined, ho);
  check(conflicts.length === 1 && conflicts[0].value === HO_PHONE, 'head office has one open IMEI conflict, for that IMEI');
  check(conflicts[0].sides.map((s) => s.branchCode).sort().join(',') === 'B01,B02', 'its two sides are B01 and B02');
  await until('head office\'s own phone is on hold too', async () => (await unitOf(MAIN, HO_PHONE, ho)).unit.status === 'quarantined');
  await until('head office hears back that Sohar\'s phone is on hold', async () =>
    (await api(MAIN, 'GET', '/api/v1/devices/conflicts?status=open', undefined, ho))[0].sides.every((x) => x.unitStatus === 'quarantined'), 60000);
  await nav(office, 'Devices');
  await office.reload();
  await office.getByRole('tab', { name: 'IMEI conflicts' }).click();
  await office.getByText('B02', { exact: true }).first().waitFor();
  await office.getByText(HO_PHONE, { exact: true }).first().waitFor();
  await shot(office, 'head-office-imei-conflict', { full: true });

  await nav(sohar, 'Devices');
  await sohar.reload();
  await sohar.getByTestId('central-banner').getByText('1 devices are on hold because of an IMEI conflict.').waitFor({ timeout: 20000 });
  await sohar.getByRole('link', { name: HO_PHONE }).first().click();
  await sohar.getByTestId('holds').getByText('IMEI conflict').waitFor();
  await shot(sohar, 'branch-device-held-for-conflict');
  await sohar.getByRole('button', { name: 'العربية' }).click();
  await sohar.getByTestId('holds').getByText('تعارض في IMEI').waitFor();
  await shot(sohar, 'branch-device-held-for-conflict-ar');
  await sohar.getByRole('button', { name: 'English' }).click();

  step('4. Sohar corrects the mistyped IMEI where the phone is; the conflict closes by itself and both phones are released');
  await sohar.getByRole('button', { name: 'Correct the IMEI' }).click();
  const dlg = sohar.getByRole('dialog', { name: 'Correct the IMEI' });
  await dlg.getByLabel('Correct IMEI').fill(REAL);
  await dlg.getByLabel('Why is it corrected?').fill('Typed the IMEI by hand while head office was down; this is the box label');
  await shot(sohar, 'branch-correct-the-imei');
  await dlg.getByRole('button', { name: 'Correct the IMEI' }).click();
  await dlg.getByText('The IMEI is corrected.').waitFor({ timeout: 20000 });
  await dlg.getByRole('button', { name: 'Close', exact: true }).click();
  await until('the conflict is resolved at head office', async () => (await api(MAIN, 'GET', '/api/v1/devices/conflicts?status=open', undefined, ho)).length === 0, 60000);
  await until('head office\'s phone is back in stock', async () => { const d = await unitOf(MAIN, HO_PHONE, ho); return d.unit.status === 'in_stock' && d.central?.state === 'verified'; });
  await until('Sohar\'s phone is released and confirmed under its real IMEI', async () => { const d = await unitOf(BRANCH, REAL, br); return d.unit.status === 'in_stock' && d.central?.state === 'verified'; }, 60000);
  const resolved = (await api(MAIN, 'GET', '/api/v1/devices/conflicts?status=resolved', undefined, ho))[0];
  check(resolved.sides.find((s) => s.branchCode === 'B02')?.outcome === 'corrected' && resolved.sides.find((s) => s.branchCode === 'B01')?.outcome === 'kept', 'the record says B02 corrected its IMEI and B01 kept the phone');
  const soharUnit = await unitOf(BRANCH, REAL, br);
  check(soharUnit.identifiers.some((x) => x.value === HO_PHONE && x.status === 'retired'), 'the mistyped IMEI stays in Sohar\'s history as replaced');
  check(soharUnit.identifiers.some((x) => x.value === HO_PHONE && x.entry === 'manual'), 'it is recorded as typed by hand, not scanned');
  await sohar.reload();
  await sohar.getByTestId('identifiers').getByText(REAL).waitFor();
  await shot(sohar, 'branch-device-released-after-correction', { full: true });
  await office.reload();
  await office.getByRole('tab', { name: 'IMEI conflicts' }).click();
  await office.getByLabel('Status').selectOption('resolved');
  await office.getByText('IMEI corrected').first().waitFor();
  await shot(office, 'head-office-conflict-resolved', { full: true });
  await office.getByRole('tab', { name: 'Device stock' }).click();
  await office.getByLabel(/Scan or type an IMEI or serial number/).fill(HO_PHONE);
  await office.getByRole('button', { name: 'Look up' }).click();
  await office.getByTestId('lookup-result').waitFor();
  await shot(office, 'head-office-imei-lookup', { full: true });

  step('5. head office blocks a stolen IMEI; the list reaches Sohar, which refuses to receive it');
  await office.getByRole('tab', { name: 'Blocked IMEIs' }).click();
  await office.getByRole('button', { name: 'Add to the blocked list' }).click();
  const add = office.getByRole('dialog', { name: 'Add to the blocked list' });
  await add.getByLabel('IMEI').fill(STOLEN);
  await add.getByLabel('Reason').fill('Reported stolen');
  await add.getByLabel('Police or report reference').fill('ROP 22/118');
  await add.getByRole('button', { name: 'Add to the blocked list' }).click();
  await office.getByRole('cell', { name: STOLEN }).waitFor();
  await shot(office, 'head-office-blocked-list');
  await until('the blocked IMEI reached Sohar with the heartbeat', async () => (await api(BRANCH, 'GET', '/api/v1/devices/blocked', undefined, br)).some((b) => b.value === STOLEN && b.status === 'active'), 60000);
  await startReceiving(sohar, [STOLEN]);
  await sohar.locator('[data-testid="scanned-unit"]', { hasText: STOLEN }).getByText(`IMEI ${STOLEN} is on the blocked list: Reported stolen (ROP 22/118).`).waitFor({ timeout: 20000 });
  check(await sohar.getByRole('button', { name: 'Post' }).isDisabled(), 'Sohar cannot receive a blocked IMEI');
  await shot(sohar, 'branch-refuses-blocked-imei');

  const status = await api(BRANCH, 'GET', '/api/v1/devices/central-status', undefined, br);
  check(status.pending === 0 && status.unitsInConflict === 0, 'Sohar: nothing waits for head office and nothing is held for a conflict');
  if (pageErrors.length) throw new Error(`browser errors: ${pageErrors.join(' | ')}`);
  console.log('E2E PHASE 6: ALL STEPS PASSED');
} catch (e) {
  await shot(office, 'FAILED-office').catch(() => undefined);
  await shot(sohar, 'FAILED-sohar').catch(() => undefined);
  console.error(`E2E PHASE 6 FAILED: ${e instanceof Error ? e.message : e}`);
  process.exitCode = 1;
} finally {
  await browser.close().catch(() => undefined);
  await stop('branch'); await stop('main');
}
