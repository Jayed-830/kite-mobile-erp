// Phase 5 exit gate in a real browser against a real KITE server and PostgreSQL: phones received by scanning their
// IMEIs, a used phone inspected with the checklist, refurbished with a part, labour and outside work (one cost taken
// back), completed with a grade, then sold by IMEI at the till next to a new phone, the receipt with the IMEIs, a
// return that sends the phone to inspection, the device history with its profit in English and Arabic, and the books.
// Every figure the screens show is checked against arithmetic done here, not against what the server says.
// Screenshots go to SHOTS_DIR.
//
//   KITE_URL (http://localhost:5080)  SETUP_CODE_FILE  STUB_URL (http://127.0.0.1:8799)
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import path from 'node:path';

const BASE = process.env.KITE_URL ?? 'http://localhost:5080';
const STUB = process.env.STUB_URL ?? 'http://127.0.0.1:8799';
const shots = process.env.SHOTS_DIR ?? 'shots-phase5';
const OWNER_PW = 'Muscat-Ruwi-2026!';
fs.mkdirSync(shots, { recursive: true });

// IMEIs with a correct check digit (the same ones the shared test vectors use).
const NEW_1 = '352099001761481';
const NEW_2 = '354670112345675';
const USED = '354670112345683';
const BAD = '352099001761482';   // NEW_1 with the last digit off by one

// Money in baisa (thousandths of a rial) so the expected figures here are exact.
const bz = (x) => Math.round(Number(x) * 1000);
const rial = (b) => (b / 1000).toFixed(3);
const halfAway = (num, den) => Math.sign(num) * Math.floor((Math.abs(num) * 2 + den) / (2 * den));

const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH ?? '/usr/bin/google-chrome', args: ['--no-sandbox'] });
const office = await (await browser.newContext({ viewport: { width: 1366, height: 900 } })).newPage();
const till = await (await browser.newContext({ viewport: { width: 1366, height: 860 } })).newPage();
const pageErrors = [];
for (const p of [office, till]) p.on('pageerror', (e) => pageErrors.push(e.message));
let n = 0;
const shot = async (p, name, opts = {}) => { n += 1; const f = path.join(shots, `${String(n).padStart(2, '0')}-${name}.jpg`); await p.waitForTimeout(400); await p.screenshot({ path: f, type: 'jpeg', quality: 74, fullPage: opts.full ?? false }); console.log(`  screenshot ${f}`); };
const step = (s) => console.log(`- ${s}`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const check = (ok, what) => { if (!ok) throw new Error(what); console.log(`  ok: ${what}`); };

async function api(method, p, body, token) {
  const r = await fetch(BASE + p, { method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  if (!r.ok) throw new Error(`${method} ${p}: ${r.status} ${text}`);
  return text ? JSON.parse(text) : null;
}
const token = async (username, password) => (await api('POST', '/api/v1/auth/login', { username, password })).accessToken;
const signIn = async (p, username, password) => {
  await p.goto(BASE + '/login');
  await p.locator('input[autocomplete="username"]').fill(username);
  await p.locator('input[autocomplete="current-password"]').fill(password);
  await p.locator('form button[type="submit"]').click();
  await p.getByRole('heading', { name: /^(Welcome, |مرحبًا، )/ }).waitFor({ timeout: 30000 });
};
const nav = (p, name) => p.getByRole('link', { name, exact: true }).click();
const dialog = (p, name) => p.getByRole('dialog', { name });
const detail = async (id, owner) => api('GET', `/api/v1/devices/${id}`, undefined, owner);
const unitByImei = async (imei, owner) => {
  const { id } = await api('GET', `/api/v1/devices/lookup/${imei}`, undefined, owner);
  const d = await detail(id, owner);
  return { id, status: d.unit.status, condition: d.unit.condition, cost: d.cost.total, detail: d };
};

try {
  step('the server is set up (API), a licence is imported from the Nextdab stub, users get PINs, refurbishment is switched on');
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
  await api('PUT', '/api/v1/settings/modules', { changes: { device: true, refurb: true } }, owner);
  const roles = await api('GET', '/api/v1/roles', undefined, owner);
  const roleId = (code) => roles.find((r) => r.code === code).id;
  const branchId = (await api('GET', '/api/v1/branches', undefined, owner))[0].id;
  for (const [username, role, name, nameAr, pin] of [['aisha', 'cashier', 'Aisha Al Balushi', 'عائشة البلوشية', '2468'], ['huda', 'branch_manager', 'Huda Al Hinai', 'هدى الهنائية', '1357']]) {
    await api('POST', '/api/v1/users', { username, displayNameEn: name, displayNameAr: nameAr, temporaryPassword: 'Temp-Pass-2026', preferredLanguage: 'en', defaultBranchId: branchId, roles: [{ roleId: roleId(role), branchId }] }, owner);
    const t = await token(username, 'Temp-Pass-2026');
    await api('POST', '/api/v1/auth/password', { currentPassword: 'Temp-Pass-2026', newPassword: 'Real-Pass-2026' }, t);
    await api('POST', '/api/v1/auth/pin', { pin, currentPassword: 'Real-Pass-2026' }, await token(username, 'Real-Pass-2026'));
  }
  owner = await token('owner', OWNER_PW);
  const vat = (await api('GET', '/api/v1/tax/codes', undefined, owner)).find((c) => c.code === 'VAT-STD').id;
  const product = async (nameEn, nameAr, productType, tracking, sku, barcode, price, minPrice, warranty) =>
    (await api('POST', '/api/v1/catalog/products', { nameEn, nameAr, productType, tracking, taxCodeId: vat, defaultWarrantyDays: warranty, variants: [{ sku, barcodes: [barcode], price, minPrice }] }, owner)).variants[0].variant.id;
  await product('iPhone 13 128GB', 'آيفون 13 سعة 128 جيجا', 'device', 'imei', 'IP13-128', '6291000000103', 220, 200, 365);
  const parts = {};
  for (const [nameEn, nameAr, sku, barcode, qty, cost] of [['Screen for iPhone 13', 'شاشة آيفون 13', 'SCR-IP13', '6291000000110', 4, 28.0], ['Battery for iPhone 13', 'بطارية آيفون 13', 'BAT-IP13', '6291000000127', 6, 9.5]]) {
    const v = await product(nameEn, nameAr, 'spare_part', 'none', sku, barcode, cost * 2, null, 90);
    await api('POST', '/api/v1/inventory/adjustments', { branchId, kind: 'opening', note: 'opening stock', lines: [{ variantId: v, qty, unitCost: cost, reasonCode: 'opening' }] }, owner);
    parts[sku] = { variantId: v, qty, cost: bz(cost) };
  }
  await api('PUT', '/api/v1/pos/policy', { branchId: null, negativeStock: 'block', returnWindowDays: 14, cashRefundCap: 500, varianceThreshold: 1, blindClose: true, roundingLevel: 'line', cashRounding: 0.005, denominations: null, receiptFooterEn: 'Thank you for shopping with us', receiptFooterAr: 'شكراً لتسوقكم معنا', version: 1 }, owner);

  step('device settings: the labour rate is set to 4.000 an hour; the grades are A 85%, B 70%, C 55% of the new price');
  await signIn(office, 'owner', OWNER_PW);
  await nav(office, 'Device settings');
  const rateField = office.getByLabel('Labour rate per hour');
  await rateField.fill('4.000');
  await shot(office, 'device-settings');
  await office.getByRole('button', { name: 'Save' }).first().click();
  for (let i = 0; i < 40 && (await api('GET', '/api/v1/devices/settings', undefined, owner)).labourRatePerHour !== 4; i++) await sleep(250);
  const settings = await api('GET', '/api/v1/devices/settings', undefined, owner);
  check(settings.labourRatePerHour === 4, 'the labour rate is saved as 4.000');
  const RATE = bz(settings.labourRatePerHour);
  const STEP = bz(settings.suggestedPriceStep);

  step('opening stock: two new iPhones received by scanning their IMEIs (one scanned twice), ready to sell');
  await nav(office, 'Inventory');
  const receive = async (imeis, cost, condition, statusLabel, name, twice) => {
    await office.getByRole('tab', { name: 'Adjust stock' }).click();
    await office.getByLabel('Document').selectOption('opening');
    await office.getByLabel('Add an item').fill('IP13');
    await office.locator('.picker-item', { hasText: 'iPhone 13 128GB' }).click();
    await office.getByLabel('Unit cost').fill(cost);
    await office.getByLabel('Comes in as').selectOption({ label: condition });
    await office.getByLabel('Status', { exact: true }).selectOption({ label: statusLabel });
    const scanner = office.getByLabel('Scan the IMEIs');
    for (const imei of imeis) { await scanner.fill(imei); await scanner.press('Enter'); await sleep(150); }
    if (twice) { await scanner.fill(imeis[0]); await scanner.press('Enter'); await office.getByText(/Scanned twice/).waitFor(); }
    await office.getByText(`${imeis.length} scanned`).waitFor();
    await shot(office, name);
    await office.getByRole('button', { name: 'Post' }).click();
    await office.getByRole('tab', { name: 'Stock on hand' }).waitFor();
    for (let i = 0; i < 40; i++) { try { await unitByImei(imeis.at(-1), owner); break; } catch { await sleep(250); } }
  };
  await receive([NEW_1, NEW_2], '180.000', 'New', 'Ready to sell', 'receive-new-phones-by-imei', true);

  step('a used iPhone bought from a walk-in seller comes in at 95.000 and waits for inspection');
  await receive([USED], '95.000', 'Used', 'Waiting for inspection', 'receive-used-phone', false);
  await office.getByText('IP13-128').first().waitFor();
  await shot(office, 'stock-on-hand-with-phones');

  step('Devices: the stock list, then the inspection queue');
  await nav(office, 'Devices');
  await office.getByText(USED).first().waitFor();
  await shot(office, 'devices-stock');
  await office.getByRole('tab', { name: 'Inspections' }).click();
  await office.getByRole('link', { name: 'Inspect' }).first().click();

  step('inspection: the screen fails and the battery is at 71% (under 80%), so the checklist suggests refurbishing');
  await office.getByRole('heading', { name: /Inspect iPhone 13 128GB/ }).waitFor();
  for (const g of await office.getByRole('group').all()) await g.getByRole('button', { name: 'Pass' }).click();
  await office.getByRole('group', { name: /^Screen/ }).getByRole('button', { name: 'Fail' }).click();
  await office.getByLabel(/^%/).fill('71');
  await office.getByText(/The checklist suggests: Refurbish — Failed: Screen.*; Battery health/).waitFor();
  await office.getByLabel('Note', { exact: true }).first().fill('Small scratch on the back');
  await office.getByLabel('Note', { exact: true }).last().fill('Battery worn; screen has a dead line');
  await shot(office, 'inspection-checklist-suggestion', { full: true });
  await office.getByRole('button', { name: 'Save the inspection' }).click();
  await office.getByRole('heading', { name: /^Refurbishment RF-B01-/ }).waitFor({ timeout: 20000 });
  const rfNo = (await office.getByRole('heading', { name: /^Refurbishment RF-B01-/ }).innerText()).replace('Refurbishment ', '').trim();
  let used = await unitByImei(USED, owner);
  check(used.status === 'refurbishing' && used.condition === 'used', `the used phone is being refurbished on ${rfNo}`);

  step('refurbishment: a screen and a battery from stock, 45 minutes of labour, outside work; the battery is taken back');
  const addPart = async (sku, label) => {
    await office.getByRole('tab', { name: 'Part from stock' }).click();
    await office.getByLabel('Part', { exact: true }).fill(sku);
    await office.locator('.picker-item', { hasText: label }).click();
    await office.getByLabel('Quantity').fill('1');
    await office.getByRole('button', { name: 'Add', exact: true }).click();
    await office.locator('td', { hasText: label }).first().waitFor();
  };
  await addPart('SCR-IP13', 'Screen for iPhone 13');
  await addPart('BAT-IP13', 'Battery for iPhone 13');
  await office.getByRole('tab', { name: 'Labour' }).click();
  await office.getByLabel('Minutes').fill('45');
  const labour = halfAway(45 * RATE, 60);
  await office.getByText(`Adds ${rial(labour)} to the device`).waitFor();
  await office.getByRole('button', { name: 'Add', exact: true }).click();
  await office.locator('td', { hasText: '45 min' }).waitFor();
  await office.getByRole('tab', { name: 'Outside work' }).click();
  await office.getByLabel('Done by').fill('Muscat Board Repair');
  await office.getByLabel('Invoice or reference').fill('MBR-118');
  await office.getByLabel('Amount', { exact: true }).fill('12.000');
  await office.getByRole('button', { name: 'Add', exact: true }).click();
  await office.locator('td', { hasText: 'Muscat Board Repair' }).waitFor();
  await office.locator('tr', { hasText: 'Battery for iPhone 13' }).getByRole('button', { name: 'Take back' }).click();
  const rev = dialog(office, 'Take back');
  await rev.getByLabel('Why is this cost taken back?').fill('The old battery was fine after a recalibration');
  await rev.getByRole('button', { name: 'Take back' }).click();
  await office.getByText('Taken back', { exact: true }).waitFor();
  const expectedCost = bz(95) + parts['SCR-IP13'].cost + labour + bz(12);
  await office.getByText(rial(expectedCost), { exact: true }).first().waitFor();
  await shot(office, 'refurbishment-costs', { full: true });
  used = await unitByImei(USED, owner);
  check(bz(used.cost) === expectedCost, `the phone's cost is 95.000 + screen ${rial(parts['SCR-IP13'].cost)} + labour ${rial(labour)} + outside 12.000 = ${rial(expectedCost)}`);
  const stockOf = async (sku) => (await api('GET', `/api/v1/inventory/balances?q=${sku}`, undefined, owner))[0].qty;
  check(await stockOf('SCR-IP13') === parts['SCR-IP13'].qty - 1, 'one screen left stock');
  check(await stockOf('BAT-IP13') === parts['BAT-IP13'].qty, 'the battery taken back is back in stock');

  step('complete: the phone comes back as refurbished, grade A, with a 90-day warranty');
  await office.getByRole('button', { name: 'Complete', exact: true }).click();
  const done = dialog(office, 'Finish the refurbishment');
  await done.getByLabel('Grade').selectOption({ label: (await done.getByRole('option', { name: /^A ·/ }).innerText()).trim() });
  await done.getByLabel('Work done').fill('Screen replaced, board repaired outside, tested');
  await shot(office, 'refurbishment-complete-dialog');
  await done.getByRole('button', { name: 'Complete', exact: true }).click();
  await office.getByText('Completed', { exact: true }).first().waitFor();
  used = await unitByImei(USED, owner);
  const usedDetail = used.detail;
  const newPrice = bz(220);
  const gradeA = 85;
  const suggested = halfAway(newPrice * gradeA, 100 * STEP) * STEP;
  check(used.status === 'in_stock' && used.condition === 'refurbished', 'the phone is back in stock as refurbished');
  check(bz(usedDetail.price.listPrice) === suggested, `it sells at 85% of 220.000 rounded to ${rial(STEP)}: ${rial(suggested)}`);
  await office.getByRole('link', { name: USED }).first().click();
  await office.getByRole('heading', { name: 'iPhone 13 128GB' }).waitFor();
  await shot(office, 'device-detail-after-refurbishment', { full: true });

  step('till: registered as T01, the cashier signs in with her PIN and opens the shift');
  await till.goto(BASE + '/pos');
  await till.getByLabel('Username').fill('owner');
  await till.getByLabel('Password').fill(OWNER_PW);
  await till.getByRole('button', { name: 'Sign in' }).click();
  await till.getByLabel('Code').fill('T01');
  await till.getByLabel('Name').fill('Front counter');
  await till.getByRole('button', { name: 'Register this device' }).click();
  await till.getByRole('heading', { name: 'Till T01' }).waitFor();
  await till.getByLabel('Username').fill('aisha');
  await till.getByLabel('PIN').fill('2468');
  await till.getByRole('button', { name: 'Sign in' }).click();
  await till.getByRole('heading', { name: 'Open shift' }).waitFor();
  await till.getByLabel('Pieces of 10', { exact: true }).fill('5');
  await till.getByRole('button', { name: 'Open shift' }).click();

  step('sell: a mistyped IMEI is caught by its check digit; the new phone is scanned; the refurbished one is picked from the list');
  const scan = till.getByLabel('Scan a barcode or type a name');
  await scan.waitFor();
  await scan.fill(BAD); await scan.press('Enter');
  await till.getByText(/not a valid IMEI/).waitFor();
  await shot(till, 'pos-invalid-imei-caught');
  await scan.fill(NEW_1); await scan.press('Enter');
  const cart = till.getByRole('region', { name: 'Cart' });
  await cart.getByText(NEW_1).waitFor();
  await till.getByRole('button', { name: /iPhone 13 128GB/ }).first().click();
  const picker = till.getByRole('dialog', { name: /^Choose the device/ });
  await picker.getByText(USED).waitFor();
  await picker.getByText('Refurbished · A').waitFor();
  await picker.getByText(rial(suggested)).first().waitFor();
  check(!(await picker.getByText(NEW_1).count()), 'the phone already in this cart is not offered again');
  await shot(till, 'pos-unit-picker');
  await picker.getByText(USED).click();
  await cart.getByText(/iPhone 13 128GB · Refurbished A/).waitFor();
  const total = newPrice + suggested;
  await shot(till, 'pos-cart-two-phones');
  await till.getByRole('button', { name: new RegExp(`^Pay ${rial(total).replace('.', '\\.')}`) }).click();
  const pay = dialog(till, 'Payment');
  await pay.getByLabel('Cash given').fill(String(Math.ceil(total / 1000 / 10) * 10));
  await pay.getByRole('button', { name: 'Complete sale' }).click();
  const receipt = till.getByRole('dialog', { name: /^INV-B01-/ });
  await receipt.waitFor({ timeout: 20000 });
  const inv = (await receipt.getByRole('heading').first().innerText()).trim();
  await receipt.getByText(`IMEI ${NEW_1}`).waitFor();
  await receipt.getByText(`IMEI ${USED}`).waitFor();
  await shot(till, 'pos-receipt-with-imeis', { full: true });
  await receipt.getByRole('button', { name: 'New sale' }).click();
  check((await unitByImei(NEW_1, owner)).status === 'sold' && (await unitByImei(USED, owner)).status === 'sold', `both phones are sold on ${inv}`);
  const sale = await api('GET', `/api/v1/sales/by-number/${inv}`, undefined, owner);
  check(sale.lines.filter((l) => l.identifier).map((l) => l.identifier).sort().join() === [NEW_1, USED].sort().join(), 'the sale lines carry the two IMEIs');
  const costOfSale = async (imei) => bz((await unitByImei(imei, owner)).detail.sales[0].cost);
  check(await costOfSale(USED) === expectedCost, `cost of sale for the refurbished phone is its own cost ${rial(expectedCost)}`);
  check(await costOfSale(NEW_1) === bz(180), 'cost of sale for the new phone is 180.000');

  step('return: the customer brings the new phone back; the cashier scans it and it goes to inspection, not back on sale');
  await till.getByRole('button', { name: 'Return', exact: true }).click();
  const ret = dialog(till, 'Return items');
  await ret.getByLabel('Receipt number').fill(inv);
  await ret.getByRole('button', { name: 'Search' }).click();
  await ret.locator('.line-editor', { hasText: NEW_1 }).getByLabel('Scan the returned device').fill(NEW_1);
  await ret.getByLabel('Reason').fill('Changed his mind');
  await shot(till, 'pos-return-scan-device');
  await ret.getByRole('button', { name: /^Refund/ }).click();
  await till.getByRole('dialog', { name: /^CN-B01-/ }).waitFor({ timeout: 20000 });
  await shot(till, 'pos-credit-note');
  await till.getByRole('dialog', { name: /^CN-B01-/ }).getByRole('button', { name: 'New sale' }).click();
  const back = await unitByImei(NEW_1, owner);
  check(back.status === 'returned_inspection' && bz(back.cost) === bz(180), 'the returned phone waits for inspection at its cost of 180.000');

  step('back office: the device history with its profit, the inspection queue and the reports');
  const soldDetail = (await unitByImei(USED, owner)).detail;
  const net = halfAway(suggested * 100, 105);   // prices include 5% VAT
  const profit = net - expectedCost;
  check(soldDetail.sales.length === 1 && bz(soldDetail.sales[0].profit) === profit, `profit on the refurbished phone = ${rial(net)} net − ${rial(expectedCost)} cost = ${rial(profit)}`);
  await office.goto(`${BASE}/app/devices/${used.id}`);
  await office.getByRole('heading', { name: 'Sales of this device' }).waitFor();
  await office.getByText(rial(profit)).first().waitFor();
  await shot(office, 'device-history-with-profit', { full: true });
  await nav(office, 'Devices');
  await office.getByRole('tab', { name: 'Inspections' }).click();
  await office.getByText(NEW_1).first().waitFor();
  await shot(office, 'inspection-queue-returned-phone');
  await office.getByRole('tab', { name: 'Reports' }).click();
  await office.getByText('Profit per device sold').waitFor();
  await office.getByText(USED).first().waitFor();
  await office.getByText('Not assigned').waitFor();
  check(await office.getByText(NEW_1).count() === 0, 'the profit report leaves out the phone the customer returned');
  await shot(office, 'devices-reports', { full: true });

  step('Arabic: the device page right to left');
  await office.getByRole('button', { name: 'العربية' }).click();
  await office.goto(`${BASE}/app/devices/${used.id}`);
  await office.getByRole('heading', { name: 'آيفون 13 سعة 128 جيجا' }).waitFor();
  await office.getByText('مجدد').first().waitFor();
  await shot(office, 'device-detail-arabic', { full: true });
  await office.getByRole('button', { name: 'English' }).click();

  step('the books agree: every phone in stock is on the ledger at its own cost, stock = ledger = accounts');
  await nav(office, 'Inventory');
  await office.getByRole('button', { name: 'Check the books' }).click();
  await office.getByText('Stock, ledger and accounts agree.').waitFor({ timeout: 20000 });
  await shot(office, 'inventory-check-ok');
  const books = await api('GET', '/api/v1/inventory/check', undefined, owner);
  check(books.ok && (books.deviceMismatches ?? []).length === 0, 'consistency check OK with no device mismatches');
  const stock = await api('GET', '/api/v1/devices?status=in_stock', undefined, owner);
  check(stock.length === 1 && stock[0].imei1 === NEW_2, 'one phone is in stock for sale: the second new iPhone');

  if (pageErrors.length) throw new Error('page errors: ' + pageErrors.join(' | '));
  console.log('PHASE 5 WALK-THROUGH PASSED');
} catch (e) {
  console.error('FAILED:', e);
  await shot(till, 'failure-till').catch(() => undefined);
  await shot(office, 'failure-office').catch(() => undefined);
  process.exitCode = 1;
} finally {
  await browser.close();
}
