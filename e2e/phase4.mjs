// Phase 4 exit gate in a real browser against a real KITE server and PostgreSQL: catalog and opening stock in the
// back office, a browser till registered with a device key, PIN sign-in, a shift with a denomination count, scanning,
// a discount approved by a manager's PIN, a price change approved remotely from the manager's screen, split tenders
// with cash rounding, the bilingual receipt with its QR, Arabic, a return, cash in and out, the blind close with the
// bank batch, the Z report, the A4 invoice PDF and the consistency check. Screenshots go to SHOTS_DIR.
//
//   KITE_URL (http://localhost:5080)  SETUP_CODE_FILE  STUB_URL (http://127.0.0.1:8799)
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import path from 'node:path';

const BASE = process.env.KITE_URL ?? 'http://localhost:5080';
const STUB = process.env.STUB_URL ?? 'http://127.0.0.1:8799';
const shots = process.env.SHOTS_DIR ?? 'shots-phase4';
const OWNER_PW = 'Muscat-Ruwi-2026!';
fs.mkdirSync(shots, { recursive: true });

const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH ?? '/usr/bin/google-chrome', args: ['--no-sandbox'] });
const office = await (await browser.newContext({ viewport: { width: 1366, height: 860 } })).newPage();
const tillCtx = await browser.newContext({ viewport: { width: 1366, height: 860 } });
const till = await tillCtx.newPage();
const managerPage = await (await browser.newContext({ viewport: { width: 1280, height: 800 } })).newPage();
const pageErrors = [];
for (const p of [office, till, managerPage]) p.on('pageerror', (e) => pageErrors.push(e.message));
let n = 0;
const shot = async (p, name, opts = {}) => { n += 1; const f = path.join(shots, `${String(n).padStart(2, '0')}-${name}.jpg`); await p.waitForTimeout(350); await p.screenshot({ path: f, type: 'jpeg', quality: 74, fullPage: opts.full ?? false }); console.log(`  screenshot ${f}`); };
const step = (s) => console.log(`- ${s}`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

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

try {
  step('the server is set up (API), a licence is imported from the Nextdab stub, users get PINs');
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
  const more = [
    ['USB-C cable 1m', 'كابل USB-C متر', 'accessory', 'CBL-C-1M', '6291000000011', 2.333, null, 40, 0.9],
    ['Charger 20W', 'شاحن 20 واط', 'accessory', 'CHG-20W', '6291000000035', 6.75, null, 15, 3.2],
    ['Phone case', 'غطاء هاتف', 'accessory', 'CASE-STD', '6291000000042', 3.25, 3.0, 30, 1.1],
    ['Battery replacement part', 'بطارية (قطعة غيار)', 'spare_part', 'BAT-SPARE', '6291000000066', 12.0, 10.0, 8, 6.5],
    ['Phone setup service', 'خدمة إعداد الهاتف', 'service', 'SVC-SETUP', '6291000000073', 2.0, null, 0, 0],
  ];
  for (const [nameEn, nameAr, productType, sku, barcode, price, minPrice, qty, cost] of more) {
    const p = await api('POST', '/api/v1/catalog/products', { nameEn, nameAr, productType, taxCodeId: vat, defaultWarrantyDays: productType === 'service' ? 0 : 30, variants: [{ sku, barcodes: [barcode], price, minPrice }] }, owner);
    if (qty) await api('POST', '/api/v1/inventory/adjustments', { branchId, kind: 'opening', note: 'opening stock', lines: [{ variantId: p.variants[0].variant.id, qty, unitCost: cost, reasonCode: 'opening' }] }, owner);
  }
  await api('PUT', '/api/v1/pos/policy', { branchId: null, negativeStock: 'block', returnWindowDays: 14, cashRefundCap: 100, varianceThreshold: 1, blindClose: true, roundingLevel: 'line', cashRounding: 0.005, denominations: null, receiptFooterEn: 'Thank you for shopping with us', receiptFooterAr: 'شكراً لتسوقكم معنا', version: 1 }, owner);

  step('back office: the owner adds a product with a barcode and price, and its opening stock');
  await signIn(office, 'owner', OWNER_PW);
  await nav(office, 'Products');
  await office.getByRole('button', { name: 'New product' }).click();
  const np = dialog(office, 'New product');
  await np.getByLabel('Name (English)').fill('Screen guard (glass)');
  await np.getByLabel('Name (Arabic)').fill('واقي شاشة زجاجي');
  await np.getByLabel('SKU').fill('GRD-GLASS');
  await np.getByLabel('Barcode').fill('6291000000028');
  await np.getByLabel('Price', { exact: true }).fill('4.000');
  await np.getByLabel('Minimum price').fill('3.500');
  await shot(office, 'catalog-new-product');
  await np.getByRole('button', { name: 'Save' }).click();
  await office.getByRole('button', { name: 'Screen guard (glass)' }).waitFor();
  await shot(office, 'catalog-list');
  await nav(office, 'Inventory');
  await office.getByRole('tab', { name: 'Adjust stock' }).click();
  await office.getByLabel('Document').selectOption('opening');
  await office.getByLabel('Add an item').fill('GRD');
  await office.locator('.picker-item', { hasText: 'Screen guard' }).click();
  await office.getByLabel('Quantity').fill('25');
  await office.getByLabel('Unit cost').fill('1.500');
  await shot(office, 'inventory-opening-stock');
  await office.getByRole('button', { name: 'Post' }).click();
  await office.getByRole('tab', { name: 'Stock on hand' }).waitFor();
  await office.getByText('GRD-GLASS').waitFor();
  await shot(office, 'inventory-stock-on-hand');

  step('till: this browser is registered as terminal T01 with a device key; the cashier signs in with her PIN');
  await till.goto(BASE + '/pos');
  await till.getByLabel('Username').fill('owner');
  await till.getByLabel('Password').fill(OWNER_PW);
  await till.getByRole('button', { name: 'Sign in' }).click();
  await till.getByLabel('Code').fill('T01');
  await till.getByLabel('Name').fill('Front counter');
  await shot(till, 'pos-register-device');
  await till.getByRole('button', { name: 'Register this device' }).click();
  await till.getByRole('heading', { name: 'Till T01' }).waitFor();
  const stored = await till.evaluate(() => JSON.parse(localStorage.getItem('kite.terminal')));
  if (!stored?.deviceKey || stored.deviceKey.length < 43) throw new Error('the till did not keep its device key');
  const blocked = await fetch(BASE + '/api/v1/auth/pin-login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username: 'aisha', pin: '2468', terminalId: stored.terminalId }) });
  if (blocked.status !== 403 || !(await blocked.text()).includes('POS-017')) throw new Error('sign-in without the device key was not refused');
  await till.getByLabel('Username').fill('aisha');
  await till.getByLabel('PIN').fill('2468');
  await till.getByRole('button', { name: 'Sign in' }).click();
  await till.getByRole('heading', { name: 'Open shift' }).waitFor();
  await till.getByLabel('Pieces of 10', { exact: true }).fill('2');
  await till.getByLabel('Pieces of 5', { exact: true }).fill('2');
  await till.getByLabel('Pieces of 1', { exact: true }).fill('5');
  await shot(till, 'pos-open-shift-count');
  await till.getByRole('button', { name: 'Open shift' }).click();

  step('sell: scan three cables, tap a charger, give a discount above the cashier limit, approved with the manager PIN');
  const scan = till.getByLabel('Scan a barcode or type a name');
  await scan.waitFor();
  for (let i = 0; i < 3; i++) { await scan.fill('6291000000011'); await scan.press('Enter'); await sleep(250); }
  await till.getByRole('button', { name: /Charger 20W/ }).click();
  await till.locator('.cart-line', { hasText: 'Charger 20W' }).getByLabel('Discount').fill('1.000');
  await till.getByText('Needs a manager').waitFor();
  await shot(till, 'pos-cart-needs-approval');
  await till.getByRole('button', { name: /^Pay / }).click();
  const pay = dialog(till, 'Payment');
  await pay.getByLabel('Card', { exact: true }).fill('5.000');
  await pay.getByLabel('Approval code').fill('483920');
  await pay.getByLabel('Cash given').fill('10');
  await shot(till, 'pos-payment-split');
  await pay.getByRole('button', { name: 'Complete sale' }).click();
  const appr = dialog(till, 'Manager approval');
  await appr.getByLabel('Manager username').fill('huda');
  await appr.getByLabel('PIN').fill('1357');
  await shot(till, 'pos-manager-pin-approval');
  await appr.getByRole('button', { name: 'Approve' }).click();
  const receipt1 = till.getByRole('dialog', { name: /^INV-B01-/ });
  await receipt1.waitFor({ timeout: 20000 });
  const inv1 = (await receipt1.getByRole('heading').first().innerText()).trim();
  await till.locator('.r-qr').waitFor();
  await shot(till, 'pos-receipt-bilingual-qr');
  await receipt1.getByRole('button', { name: 'New sale' }).click();

  step('a price under the minimum is approved remotely from the manager\'s screen');
  await signIn(managerPage, 'huda', 'Real-Pass-2026');
  await scan.fill('6291000000042'); await scan.press('Enter');
  await till.locator('.cart-line', { hasText: 'Phone case' }).getByLabel('Discount').fill('0.500');
  await till.getByRole('button', { name: /^Pay / }).click();
  await dialog(till, 'Payment').getByRole('button', { name: 'Complete sale' }).click();
  const appr2 = dialog(till, 'Manager approval');
  await appr2.getByRole('tab', { name: 'Ask remotely' }).click();
  await appr2.getByLabel('Reason').fill('Match the shop next door');
  await appr2.getByRole('button', { name: 'Ask a manager' }).click();
  await appr2.getByText('Waiting for the manager…').waitFor();
  await nav(managerPage, 'Approvals');
  await managerPage.getByText('Price change or below minimum').waitFor({ timeout: 15000 });
  await shot(managerPage, 'approvals-remote-request');
  await managerPage.getByRole('button', { name: 'Approve' }).first().click();
  await till.getByRole('dialog', { name: /^INV-B01-/ }).waitFor({ timeout: 20000 });
  await till.getByRole('dialog', { name: /^INV-B01-/ }).getByRole('button', { name: 'New sale' }).click();

  step('Arabic: the till in Arabic, right to left, selling a service and a part');
  await till.getByRole('button', { name: 'العربية' }).click();
  await till.getByRole('button', { name: /خدمة إعداد الهاتف/ }).click();
  await till.getByRole('button', { name: /بطارية/ }).click();
  await shot(till, 'pos-arabic-rtl');
  await till.getByRole('button', { name: /^دفع / }).click();
  await dialog(till, 'الدفع').getByRole('button', { name: 'إتمام البيع' }).click();
  await till.getByRole('dialog', { name: /^INV-B01-/ }).waitFor({ timeout: 20000 });
  await shot(till, 'pos-arabic-receipt');
  await till.getByRole('dialog', { name: /^INV-B01-/ }).getByRole('button', { name: 'بيع جديد' }).click();
  await till.getByRole('button', { name: 'English' }).click();

  step('a return of one cable from the first sale, refunded in cash and put back in stock');
  await till.getByRole('button', { name: 'Return', exact: true }).click();
  const ret = dialog(till, 'Return items');
  await ret.getByLabel('Receipt number').fill(inv1);
  await ret.getByRole('button', { name: 'Search' }).click();
  await ret.getByLabel(/Return \(up to 3\)/).fill('1');
  await ret.getByLabel('Reason').fill('Wrong length');
  await shot(till, 'pos-return');
  await ret.getByRole('button', { name: /^Refund/ }).click();
  await till.getByRole('dialog', { name: /^CN-B01-/ }).waitFor({ timeout: 20000 });
  await shot(till, 'pos-credit-note-receipt');
  await till.getByRole('dialog', { name: /^CN-B01-/ }).getByRole('button', { name: 'New sale' }).click();

  step('cash in and out, then the blind close with the bank batch total');
  await till.getByRole('button', { name: 'Cash in/out' }).click();
  const cash = dialog(till, 'Cash in or out');
  await cash.getByLabel('What').selectOption('pay_in');
  await cash.getByLabel('Amount').fill('5.000');
  await cash.getByLabel('Reason').fill('Coins from the safe');
  await cash.getByRole('button', { name: 'Save' }).click();
  await till.getByText('Cash paid in.').waitFor();
  // What the drawer should hold, from the manager's report (the cashier does not see it: blind close).
  const managerToken = await token('huda', 'Real-Pass-2026');
  const shifts = await api('GET', '/api/v1/pos/shifts?status=open', undefined, managerToken);
  const x = await api('GET', `/api/v1/pos/shifts/${shifts[0].id}`, undefined, managerToken);
  const expected = x.figures.expectedCash;
  await till.getByRole('button', { name: 'Close shift' }).click();
  const close = dialog(till, 'Close shift');
  // Count exactly what is expected, by denomination.
  let left = Math.round(expected * 1000);
  for (const d of [50, 20, 10, 5, 1, 0.5, 0.1, 0.05, 0.025, 0.01, 0.005]) {
    const u = Math.round(d * 1000);
    const pieces = Math.floor(left / u);
    if (pieces > 0) { await close.getByLabel(`Pieces of ${d}`, { exact: true }).fill(String(pieces)); left -= pieces * u; }
  }
  await close.getByLabel(/Batch total printed by BANK-1/).fill('5.000');
  await shot(till, 'pos-close-blind-count');
  await close.getByRole('button', { name: 'Close shift' }).click();
  await till.getByRole('heading', { name: /^Shift SH-B01-.* closed$/ }).waitFor({ timeout: 20000 });
  await shot(till, 'pos-z-report', { full: true });

  step('back office: the Z report, the receipt preview and the A4 tax invoice');
  await nav(office, 'Sales and shifts');
  await office.getByRole('button', { name: shifts[0].number }).click();
  await dialog(office, `Z · ${shifts[0].number}`).waitFor();
  await shot(office, 'reports-z');
  await dialog(office, `Z · ${shifts[0].number}`).getByRole('button', { name: 'Close', exact: true }).click();
  await office.getByRole('tab', { name: 'Documents' }).click();
  await office.getByRole('button', { name: inv1 }).click();
  await office.locator('.receipt').waitFor();
  await shot(office, 'reports-sale-receipt');
  const saleId = (await api('GET', `/api/v1/sales/by-number/${inv1}`, undefined, owner)).sale.id;
  const pdf = await fetch(`${BASE}/api/v1/sales/${saleId}/invoice.pdf`, { headers: { Authorization: `Bearer ${owner}` } });
  const bytes = Buffer.from(await pdf.arrayBuffer());
  if (!pdf.ok || bytes.subarray(0, 4).toString() !== '%PDF') throw new Error('the A4 invoice is not a PDF');
  fs.writeFileSync(path.join(shots, `${inv1}.pdf`), bytes);
  console.log(`  A4 invoice ${inv1}.pdf, ${bytes.length} bytes`);

  step('the books agree: stock = ledger = inventory accounts, every journal balances, every sale is paid');
  await dialog(office, inv1).getByRole('button', { name: 'Close', exact: true }).click();
  await nav(office, 'Inventory');
  await office.getByRole('button', { name: 'Check the books' }).click();
  await office.getByText('Stock, ledger and accounts agree.').waitFor({ timeout: 20000 });
  await shot(office, 'inventory-check-ok');
  const check = await api('GET', '/api/v1/inventory/check', undefined, owner);
  if (!check.ok) throw new Error('consistency check failed: ' + JSON.stringify(check));
  const cable = (await api('GET', '/api/v1/inventory/balances?q=CBL-C-1M', undefined, owner))[0];
  if (cable.qty !== 38) throw new Error(`cable stock should be 40 - 3 + 1 = 38, got ${cable.qty}`);

  if (pageErrors.length) throw new Error('page errors: ' + pageErrors.join(' | '));
  console.log('PHASE 4 WALK-THROUGH PASSED');
} catch (e) {
  console.error('FAILED:', e);
  await shot(till, 'failure-till').catch(() => undefined);
  await shot(office, 'failure-office').catch(() => undefined);
  await shot(managerPage, 'failure-manager').catch(() => undefined);
  process.exitCode = 1;
} finally {
  await browser.close();
}
