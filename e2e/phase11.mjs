// Phase 11 exit gate in a real browser against a real KITE server and PostgreSQL: a purchasing officer raises an order that is
// above the branch manager's approval limit; the officer approves and sends it; the goods come in with one IMEI per phone and a
// freight charge from a carrier spread over the lines; one phone is sold; the supplier's invoice has a phone price outside the
// tolerance, which the officer cannot post and the owner posts with a reason (the difference splits between the phones still on
// hand and price variance); the carrier's invoice clears the freight; two cases go back on a debit note; the supplier is paid;
// an accountant records an expense and a manual journal above the threshold that the owner approves; the quarter's months are
// closed at every branch and its VAT return filed; the trial balance and balance sheet balance and the daily ledger check agrees.
// Screens in English and Arabic; accounts checked against arithmetic done here. Screenshots to SHOTS_DIR.
//
//   KITE_URL (http://localhost:5080)  SETUP_CODE_FILE  STUB_URL (http://127.0.0.1:8799)  KITE_DB (kite; PGHOST etc. for psql)
import { chromium } from 'playwright-core';
import { execFileSync } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const BASE = process.env.KITE_URL ?? 'http://localhost:5080';
const STUB = process.env.STUB_URL ?? 'http://127.0.0.1:8799';
const DB = process.env.KITE_DB ?? 'kite';
const shots = process.env.SHOTS_DIR ?? 'shots-phase11';
const OWNER_PW = 'Muscat-Ruwi-2026!';
fs.mkdirSync(shots, { recursive: true });

const bz = (x) => Math.round(Number(x) * 1000);
const rial = (b) => (b / 1000).toFixed(3);
const sql = (q) => execFileSync('psql', ['-d', DB, '-At', '-c', q], { encoding: 'utf8' }).trim();
const gl = (code) => bz(sql(`SELECT coalesce(sum(l.debit - l.credit), 0) FROM acct.journal_line l JOIN acct.account a ON a.id = l.account_id WHERE a.code = '${code}'`));
const imei = () => {
  const d = '35' + Array.from({ length: 12 }, () => Math.floor(Math.random() * 10)).join('');
  let s = 0;
  for (let i = 0; i < 14; i++) { let x = Number(d[i]); if (i % 2 === 1) { x *= 2; if (x > 9) x -= 9; } s += x; }
  return d + ((10 - (s % 10)) % 10);
};

const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH ?? '/usr/bin/google-chrome', args: ['--no-sandbox'] });
let office = await (await browser.newContext({ viewport: { width: 1366, height: 900 }, acceptDownloads: true })).newPage();
let boss = await (await browser.newContext({ viewport: { width: 1366, height: 900 } })).newPage();
const pageErrors = [];
for (const p of [office, boss]) p.on('pageerror', (e) => pageErrors.push(e.message));
let n = 0;
const shot = async (p, name, opts = {}) => { n += 1; const f = path.join(shots, `${String(n).padStart(2, '0')}-${name}.jpg`); await p.waitForTimeout(400); await p.screenshot({ path: f, type: 'jpeg', quality: 74, fullPage: opts.full ?? false }); console.log(`  screenshot ${f}`); };
const step = (s) => console.log(`- ${s}`);
const check = (ok, what) => { if (!ok) throw new Error(what); console.log(`  ok: ${what}`); };

async function api(method, p, body, token, raw) {
  const r = await fetch(BASE + p, { method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  if (raw) return r;
  const text = await r.text();
  if (!r.ok) throw new Error(`${method} ${p}: ${r.status} ${text}`);
  return text ? JSON.parse(text) : null;
}
const token = async (username, password, extra = {}) => (await api('POST', '/api/v1/auth/login', { username, password, ...extra })).accessToken;
const signIn = async (p, username, password) => {
  await p.goto(BASE + '/login');
  await p.locator('input[autocomplete="username"]').fill(username);
  await p.locator('input[autocomplete="current-password"]').fill(password);
  await p.locator('form button[type="submit"]').click();
  await p.getByRole('heading', { name: /^(Welcome, |مرحبًا، )/ }).waitFor({ timeout: 30000 });
};
const nav = (p, name) => p.locator('nav.sidebar').getByRole('link', { name, exact: true }).click();
const dialog = (p, name) => p.getByRole('dialog', { name });

try {
  step('the server is set up (API), a licence is imported from the Nextdab stub, purchasing, expenses, accounting screens and devices are on; a second branch; a purchasing officer, a branch manager and an accountant');
  await api('POST', '/api/v1/setup', {
    setupCode: fs.readFileSync(process.env.SETUP_CODE_FILE, 'utf8').trim(), serverRole: 'combined',
    business: { legalNameEn: 'Al Noor Mobiles LLC', legalNameAr: 'شركة النور للهواتف', tradeNameEn: 'Al Noor Mobiles', tradeNameAr: 'النور للهواتف', vatNumber: 'OM1100223344', crNumber: '1234567', fiscalYearStartMonth: 1, pricesIncludeTax: true, phone: '+968 2400 0000' },
    headOffice: { code: 'B01', nameEn: 'Muscat - Ruwi', nameAr: 'مسقط - روي' },
    owner: { username: 'owner', displayNameEn: 'Salim Al Harthy', displayNameAr: 'سالم الحارثي', password: OWNER_PW, preferredLanguage: 'en' },
  });
  let owner = await token('owner', OWNER_PW);
  const mid = (await api('GET', '/api/v1/licence', undefined, owner)).installationId;
  const { file } = await (await fetch(STUB + '/stub/licence', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ mid, customer: 'Al Noor Mobiles LLC', kind: 'subscription', validUntil: new Date(Date.now() + 365 * 864e5).toISOString().slice(0, 10), branches: 5, terminals: 20, graceDays: 30 }) })).json();
  await api('POST', '/api/v1/licence/import', { fileText: file }, owner);
  await api('PUT', '/api/v1/settings/modules', { changes: { purch: true, exp: true, acct_ui: true, device: true } }, owner);
  const b02 = (await api('POST', '/api/v1/branches', { code: 'B02', nameEn: 'Sohar', nameAr: 'صحار' }, owner)).id;
  const roles = await api('GET', '/api/v1/roles', undefined, owner);
  const roleId = (code) => roles.find((r) => r.code === code).id;
  const b01 = (await api('GET', '/api/v1/branches', undefined, owner)).find((b) => b.code === 'B01').id;
  for (const [username, role, name, nameAr, all] of [['karim', 'purchasing_officer', 'Karim Al Siyabi', 'كريم السيابي', false], ['huda', 'branch_manager', 'Huda Al Balushi', 'هدى البلوشية', false],
    ['maha', 'accountant', 'Maha Al Zadjali', 'مها الزدجالية', true]]) {
    await api('POST', '/api/v1/users', { username, displayNameEn: name, displayNameAr: nameAr, temporaryPassword: 'Temp-Pass-2026', preferredLanguage: 'en', defaultBranchId: b01, roles: [{ roleId: roleId(role), branchId: all ? null : b01 }] }, owner);
    await api('POST', '/api/v1/auth/password', { currentPassword: 'Temp-Pass-2026', newPassword: 'Real-Pass-2026' }, await token(username, 'Temp-Pass-2026'));
  }
  owner = await token('owner', OWNER_PW);
  const vat = (await api('GET', '/api/v1/tax/codes', undefined, owner)).find((c) => c.code === 'VAT-STD').id;
  const product = async (nameEn, nameAr, sku, barcode, price, productType, tracking) =>
    (await api('POST', '/api/v1/catalog/products', { nameEn, nameAr, productType, tracking, taxCodeId: vat, defaultWarrantyDays: 30, variants: [{ sku, barcodes: [barcode], price }] }, owner)).variants[0].variant.id;
  await product('Phone case', 'غطاء هاتف', 'CASE-01', '6291000002001', 3.5, 'accessory', 'none');
  const galaxy = await product('Galaxy A55 128GB', 'جالاكسي A55 سعة 128', 'GA55-128', '8806095000551', 199, 'device', 'imei');
  const gulf = (await api('POST', '/api/v1/suppliers', { nameEn: 'Gulf Mobile Parts LLC', nameAr: 'شركة الخليج لقطع الهواتف', vatNumber: 'OM1100445566', termsDays: 30 }, owner)).id;
  const carrier = (await api('POST', '/api/v1/suppliers', { nameEn: 'Muscat Freight Co', nameAr: 'مسقط للشحن', termsDays: 15 }, owner)).id;
  const snap = Object.fromEntries(['1100', '1200', '1400', '1420', '1500', '2100', '2110', '2200', '2210', '3100', '5300', '6100'].map((c) => [c, gl(c)]));
  const moved = (code) => gl(code) - (snap[code] ?? 0);

  // ---------------------------------------------------------------- the order
  step('the purchasing officer raises an order: 20 cases at 1.100 and 4 phones at 150.000 (653.100 with VAT)');
  await signIn(office, 'karim', 'Real-Pass-2026');
  await nav(office, 'Purchasing');
  await office.getByRole('heading', { name: 'Purchasing' }).waitFor();
  await office.getByRole('button', { name: 'New order' }).click();
  let d = dialog(office, 'New order');
  await d.getByRole('option', { name: /Gulf Mobile Parts LLC/ }).waitFor({ state: 'attached' });
  await d.getByLabel('Supplier').selectOption(gulf);
  for (const [q, name, qty, price] of [['Phone case', /Phone case/, '20', '1.1'], ['Galaxy', /Galaxy A55/, '4', '150']]) {
    await d.getByLabel('Add an item').fill(q);
    await d.getByRole('button', { name }).first().click();
    const row = d.locator('.line-editor', { hasText: name });
    await row.waitFor();
    await row.getByLabel('Quantity').fill(qty);
    await row.getByLabel('Price before VAT').fill(price);
  }
  await d.getByText('653.100').waitFor();
  await shot(office, 'order-new');
  await d.getByRole('button', { name: 'Save draft' }).click();
  await office.getByRole('heading', { name: /^Order PO-B01-/ }).waitFor();
  const poUrl = office.url();
  const po = poUrl.split('/').pop();

  step('the branch manager may approve up to 500.000: the order waits for someone with a higher limit');
  await signIn(boss, 'huda', 'Real-Pass-2026');
  await boss.goto(poUrl);
  await boss.getByText('You may approve up to 500.000; this order is 653.100. Someone with a higher limit approves it.').waitFor();
  check(await boss.getByRole('button', { name: 'Approve' }).count() === 0, 'no Approve button for the branch manager');
  await shot(boss, 'order-over-limit');
  const huda = await token('huda', 'Real-Pass-2026');
  const refused = await api('POST', `/api/v1/purchasing/orders/${po}/approve`, undefined, huda, true);
  check(refused.status === 403 && (await refused.json()).code === 'PUR-006', 'the server refuses her approval too (PUR-006)');

  step('the officer (limit 2,000.000) approves and sends it; the PDF for the supplier');
  await office.getByRole('button', { name: 'Approve' }).click();
  await office.locator('.badge', { hasText: /^Approved$/ }).first().waitFor();
  await office.getByRole('button', { name: 'Mark as sent' }).click();
  await office.locator('.badge', { hasText: /^Sent$/ }).first().waitFor();
  const pdf = await api('GET', `/api/v1/purchasing/orders/${po}/pdf`, undefined, await token('karim', 'Real-Pass-2026'), true);
  check(pdf.headers.get('content-type') === 'application/pdf' && (await pdf.arrayBuffer()).byteLength > 1000, 'the order PDF is made');

  step('the goods come in: 20 cases and the 4 phones scanned one by one; 12.000 freight from the carrier spread by value');
  await office.getByRole('button', { name: 'Receive goods' }).click();
  d = dialog(office, /^Receive against PO-B01-/);
  await d.locator('.line-editor').first().getByLabel('Quantity').fill('20');
  const imeis = [imei(), imei(), imei(), imei()];
  const scanner = d.getByLabel('Scan the IMEIs');
  for (const x of imeis) { await scanner.fill(x); await scanner.press('Enter'); await office.waitForTimeout(200); }
  await d.getByText('4 scanned').waitFor();
  await d.getByRole('button', { name: 'Add a landed cost' }).click();
  const charge = d.locator('.line-editor').last();
  await charge.getByLabel('Description').fill('Truck from Jebel Ali');
  await charge.getByLabel('Amount').fill('12');
  await charge.getByLabel('Charged by').selectOption(carrier);
  await shot(office, 'receive');
  await d.getByRole('button', { name: 'Post' }).click();
  await office.getByText(/^Goods received: GR-B01-/).waitFor();
  await office.locator('.badge', { hasText: /^Received$/ }).first().waitFor();
  check(moved('2110') === -bz(634) && moved('1400') + moved('1420') === bz(634), 'stock and goods received not invoiced each moved 634.000 (622 goods + 12 freight)');
  check(sql(`SELECT string_agg(unit_cost::text, ',' ORDER BY unit_cost) FROM purch.receipt_unit`) === '152.894,152.894,152.894,152.894', 'each phone costs 150 + 2.894 of the freight');
  await office.getByRole('link', { name: /^GR-B01-/ }).click();
  await office.getByRole('heading', { name: /^Goods received GR-B01-/ }).waitFor();
  await shot(office, 'receipt', { full: true });

  step('one phone is sold at the till before the invoice comes');
  const key = randomBytes(32).toString('base64url');
  const term = (await api('POST', '/api/v1/terminals', { branchId: b01, code: 'T01', name: 'Front counter', kind: 'browser', deviceKey: key }, owner)).id;
  await api('POST', `/api/v1/terminals/${term}/approve`, { emergencyEnabled: false }, owner);
  const pos = await token('owner', OWNER_PW, { terminalId: term, deviceKey: key });
  await api('POST', '/api/v1/pos/shifts', { openingFloat: 20 }, pos);
  const unit = (await api('GET', `/api/v1/devices/lookup/${imeis[0]}`, undefined, owner)).id;
  await api('POST', `/api/v1/sales/${randomUUID()}/complete`, { lines: [{ variantId: galaxy, qty: 1, deviceUnitId: unit, unitPrice: 199 }], payments: [{ method: 'cash', amount: 199, tendered: 200 }] }, pos);

  // ---------------------------------------------------------------- the invoices
  step('the supplier invoices the phones at 156.000: outside the 2% tolerance; the officer cannot post it');
  await nav(office, 'Purchasing');
  await office.getByRole('button', { name: 'Enter an invoice' }).click();
  await office.getByRole('heading', { name: 'Enter an invoice' }).waitFor();
  await office.getByRole('option', { name: /Gulf Mobile Parts LLC/ }).waitFor({ state: 'attached' });
  await office.getByLabel('Supplier', { exact: true }).selectOption(gulf);
  await office.getByLabel('Supplier’s invoice no.').fill('GMP-5521');
  await office.getByRole('checkbox', { name: /Phone case/ }).check();
  await office.getByRole('checkbox', { name: /Galaxy A55/ }).check();
  await office.getByLabel(/^Invoice price GR-B01-/).nth(1).fill('156');
  await office.getByText('Price differs').waitFor();
  await shot(office, 'invoice-entry', { full: true });
  await office.getByRole('button', { name: 'Check the match and save' }).click();
  await office.getByRole('heading', { name: /^Supplier invoice PI-B01-/ }).waitFor();
  await office.getByText('A price is outside tolerance: someone with PURCH.INVOICE.OVERRIDE posts it.').waitFor();
  const invUrl = office.url();
  await shot(office, 'invoice-needs-override', { full: true });

  step('the owner posts it with a reason: 18.000 to the three phones on hand, 6.000 for the one sold to price variance');
  await boss.context().close();
  boss = await (await browser.newContext({ viewport: { width: 1366, height: 900 } })).newPage();
  boss.on('pageerror', (e) => pageErrors.push(e.message));
  await signIn(boss, 'owner', OWNER_PW);
  await boss.goto(invUrl);
  await boss.getByRole('button', { name: 'Post outside tolerance' }).click();
  d = dialog(boss, 'Post outside tolerance');
  await d.getByLabel('Reason').fill('Price increase from November, confirmed by email');
  await d.getByRole('button', { name: 'Post' }).click();
  await boss.getByText(/^Posted outside tolerance by Salim Al Harthy/).waitFor();
  await shot(boss, 'invoice-posted', { full: true });
  check(moved('5300') === bz(6), 'purchase price variance took 6.000');
  check(sql(`SELECT string_agg(accumulated_cost::text, ',' ORDER BY accumulated_cost) FROM device.device_unit WHERE status = 'in_stock'`) === '158.894,158.894,158.894', 'the three phones on hand now cost 158.894');

  step('the carrier invoices its freight at 12.500 (within the 1.000 line tolerance); two cases go back on a debit note');
  await office.goto(BASE + '/app/purchasing/invoices/new');
  await office.getByRole('option', { name: /Muscat Freight Co/ }).waitFor({ state: 'attached' });
  await office.getByLabel('Supplier', { exact: true }).selectOption(carrier);
  await office.getByLabel('Supplier’s invoice no.').fill('MF-0193');
  await office.getByRole('checkbox', { name: /Freight: Truck from Jebel Ali/ }).check();
  await office.getByLabel(/^Invoiced Freight/).fill('12.5');
  await office.getByRole('button', { name: 'Check the match and save' }).click();
  await office.getByRole('heading', { name: /^Supplier invoice PI-B01-/ }).waitFor();
  await office.getByRole('button', { name: 'Post', exact: true }).click();
  await office.locator('.badge', { hasText: /^Posted$/ }).first().waitFor();
  check(moved('2110') === 0, 'goods received not invoiced is cleared');
  await nav(office, 'Purchasing');
  await office.getByRole('button', { name: 'Return goods' }).click();
  d = dialog(office, 'Return goods');
  await d.getByRole('option', { name: /Gulf Mobile Parts LLC/ }).waitFor({ state: 'attached' });
  await d.getByLabel('Supplier').selectOption(gulf);
  await d.getByRole('option', { name: /^GR-B01-/ }).waitFor({ state: 'attached' });
  await d.getByLabel('Receipt').selectOption({ index: 1 });
  await d.getByLabel('Reason').fill('Cracked in the box');
  await d.getByLabel('Quantity').fill('2');
  await d.getByRole('button', { name: 'Post' }).click();
  await office.getByText(/^Goods returned: DN-B01-/).waitFor();
  await office.getByRole('tab', { name: 'Returns' }).click();
  await office.getByRole('cell', { name: '2.310' }).waitFor();
  await shot(office, 'returns');

  step('the owner pays the supplier 500.000 by transfer, oldest invoice first');
  await boss.goto(BASE + '/app/purchasing?tab=payments');
  await boss.getByRole('button', { name: 'Pay a supplier' }).click();
  d = dialog(boss, 'Pay a supplier');
  await d.getByRole('option', { name: /Gulf Mobile Parts LLC/ }).waitFor({ state: 'attached' });
  await d.getByLabel('Supplier').selectOption(gulf);
  await d.getByLabel('Amount').fill('500');
  await d.getByLabel('Reference').fill('TRF-88310');
  await d.getByRole('button', { name: 'Pay' }).click();
  await boss.getByText(/^Payment PV-B01-.* recorded\.$/).waitFor();
  await shot(boss, 'payments');
  // 646.000 + 32.300 VAT, less the 2.310 debit note, less 500.000; the carrier 12.500 + 0.625.
  check(moved('2100') === -(bz(678.3) - bz(2.31) - bz(500) + bz(13.125)), `payables are ${rial(-moved('2100'))}`);
  check(sql(`SELECT sum(amount) FROM purch.supplier_ledger WHERE supplier_id = '${gulf}'`) === '175.990', 'the supplier is owed 175.990');

  // ---------------------------------------------------------------- the accountant
  step('the accountant records Sohar’s rent (300.000 + VAT by transfer) and a 1,500.000 capital journal that the owner approves');
  await office.context().close();
  office = await (await browser.newContext({ viewport: { width: 1366, height: 900 } })).newPage();
  office.on('pageerror', (e) => pageErrors.push(e.message));
  await signIn(office, 'maha', 'Real-Pass-2026');
  await nav(office, 'Expenses');
  await office.getByRole('button', { name: 'Record an expense' }).click();
  d = dialog(office, 'Record an expense');
  await d.getByLabel('Branch').selectOption(b02);
  await d.getByRole('option', { name: /6100/ }).waitFor({ state: 'attached' });
  await d.getByLabel('Account').selectOption({ label: '6100 · Rent' });
  await d.getByLabel('Description').fill('Shop rent, November');
  await d.getByLabel('Paid to').fill('Sohar Real Estate');
  await d.getByLabel('Amount before VAT').fill('300');
  await d.getByLabel('Paid by').selectOption('bank_transfer');
  await d.getByLabel('Reference').fill('TRF-88311');
  await d.getByText('315.000').waitFor();
  await d.getByRole('button', { name: 'Record' }).click();
  await office.getByText(/^Expense EX-B02-/).waitFor();
  await shot(office, 'expenses');
  await nav(office, 'Accounting');
  await office.getByRole('tab', { name: 'Manual journals' }).click();
  await office.getByRole('button', { name: 'New manual journal' }).click();
  d = dialog(office, 'New manual journal');
  await d.getByLabel('Description').fill('Owner’s additional capital');
  const rows = d.locator('.line-editor');
  await rows.nth(0).getByLabel('Account').selectOption({ label: '1200 · Bank' });
  await rows.nth(0).getByLabel('Debit').fill('1500');
  await rows.nth(1).getByLabel('Account').selectOption({ label: '3100 · Capital' });
  await rows.nth(1).getByLabel('Credit').fill('1500');
  await d.getByRole('button', { name: 'Save draft' }).click();
  await office.getByRole('heading', { name: /^Manual journal MJ-B01-/ }).waitFor();
  await office.getByRole('button', { name: 'Submit' }).click();
  await office.getByText('Waiting for someone else to approve it.').waitFor();
  check(await office.getByRole('button', { name: 'Approve' }).count() === 0, 'she cannot approve her own journal');
  const mjUrl = office.url();
  await boss.goto(mjUrl);
  await boss.getByRole('button', { name: 'Approve' }).click();
  await boss.locator('.badge', { hasText: /^Posted$/ }).first().waitFor();
  await shot(boss, 'manual-journal', { full: true });
  check(moved('3100') === -bz(1500), 'capital rose 1,500.000');

  // ---------------------------------------------------------------- the quarter
  step('the quarter’s months are soft-closed at every branch; its VAT return agrees with the VAT accounts and is filed');
  const month = new Date(Date.now() + 4 * 3600e3).getUTCMonth() + 1;
  const first = Math.floor((month - 1) / 3) * 3 + 1;
  await office.goto(BASE + '/app/accounting?tab=periods');
  for (const m of [first, first + 1, first + 2]) {
    await office.getByLabel(`All branches, period ${m}`).selectOption('soft_closed');
    d = dialog(office, new RegExp(`^Period ${m} · All branches`));
    await d.getByLabel('Reason').fill('Quarter end');
    await d.getByRole('button', { name: 'Confirm' }).click();
    await d.waitFor({ state: 'detached' });
  }
  await shot(office, 'periods', { full: true });
  await nav(office, 'VAT returns');
  await office.getByRole('button', { name: 'Confirm the boxes' }).click();
  await office.getByRole('button', { name: 'Confirm the boxes' }).waitFor({ state: 'detached' });
  await office.getByLabel('Period').selectOption({ index: 1 });
  await office.getByRole('button', { name: 'Prepare the return' }).click();
  await office.getByRole('heading', { name: /^VAT return VAT-/ }).waitFor();
  check(await office.getByText('The VAT from the documents differs from the VAT accounts').count() === 0, 'the VAT report agrees with the VAT accounts at every branch');
  await shot(office, 'vat-return', { full: true });
  await office.getByRole('button', { name: 'File the return' }).click();
  d = dialog(office, 'File the return');
  await d.getByLabel('Tax Authority reference').fill('OTA-2026-Q4-0042');
  await d.getByRole('button', { name: 'Confirm' }).click();
  await office.locator('.badge', { hasText: /^Filed$/ }).first().waitFor();
  const output = bz(sql(`SELECT output_vat FROM tax.vat_return`)); const input = bz(sql(`SELECT input_vat FROM tax.vat_return`));
  check(input === bz(32.3) + bz(0.625) - bz(0.11) + bz(15), `input VAT ${rial(input)} = invoices 32.300 + 0.625 less the debit note 0.110 plus the rent 15.000`);
  check(moved('2210') === -(output - input), 'VAT payable holds the net of the return');
  await shot(office, 'vat-filed');

  step('statements balance; the daily ledger check agrees');
  await nav(office, 'Statements');
  await office.locator('.badge', { hasText: /^Balanced$/ }).first().waitFor();
  await shot(office, 'trial-balance', { full: true });
  await office.getByRole('tab', { name: 'Balance sheet' }).click();
  await office.getByText('Total assets').waitFor();
  await office.locator('.badge', { hasText: /^Balanced$/ }).first().waitFor();
  await shot(office, 'balance-sheet', { full: true });
  await office.getByRole('tab', { name: 'Profit and loss' }).click();
  await office.getByText('Net profit').waitFor();
  await shot(office, 'profit-and-loss', { full: true });
  await nav(office, 'Accounting');
  await office.getByRole('tab', { name: 'Ledger check' }).click();
  await office.getByRole('button', { name: 'Run the check now' }).click();
  await office.locator('.badge', { hasText: /^Everything agrees$/ }).first().waitFor();
  await shot(office, 'ledger-check', { full: true });

  step('purchasing and statements in Arabic');
  await office.getByRole('button', { name: 'العربية' }).click();
  await office.goto(invUrl);
  await office.getByRole('heading', { name: /^فاتورة المورد PI-B01-/ }).waitFor();
  await shot(office, 'invoice-ar', { full: true });
  await office.goto(BASE + '/app/accounting/statements?r=bs');
  await office.locator('.badge', { hasText: /^متوازن$/ }).first().waitFor();
  await shot(office, 'balance-sheet-ar', { full: true });
  await office.getByRole('button', { name: 'English' }).click();

  // ---------------------------------------------------------------- accounts
  step('accounts against arithmetic done here');
  check(moved('2110') === 0, 'goods received not invoiced: nothing open');
  check(moved('1500') === 0, 'VAT input moved into VAT payable when the return was filed');
  check(moved('6100') === bz(300), 'rent 300.000');
  check(sql(`SELECT count(*) FROM acct.subledger_check WHERE run_id = (SELECT run_id FROM acct.subledger_check ORDER BY checked_at DESC LIMIT 1) AND difference <> 0`) === '0', 'the ledger check finds no difference');
  check(Number(sql(`SELECT count(DISTINCT ledger) FROM acct.subledger_check WHERE run_id = (SELECT run_id FROM acct.subledger_check ORDER BY checked_at DESC LIMIT 1)`)) >= 6, 'the check covers the new ledgers');
  check(sql(`SELECT ok FROM audit.verify_chain()`) === 't', 'the audit chain verifies');

  if (pageErrors.length) throw new Error('page errors: ' + pageErrors.join(' | '));
  console.log('PHASE 11 WALK-THROUGH PASSED');
} catch (e) {
  console.error('FAILED:', e);
  await shot(office, 'failure-office', { full: true }).catch(() => undefined);
  await shot(boss, 'failure-boss', { full: true }).catch(() => undefined);
  for (const p of [office, boss]) console.error('  page alerts:', (await p.locator('.alert').allTextContents().catch(() => [])).join(' | '));
  process.exitCode = 1;
} finally {
  await browser.close();
}
