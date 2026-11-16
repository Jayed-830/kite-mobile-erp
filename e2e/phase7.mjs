// Phase 7 exit gate in a real browser against a real KITE server and PostgreSQL: the accountant records the margin scheme
// approval, the owner sets the trade-in price guide, and at the till a cashier takes phones in with the trade-in wizard
// (F6): one applied to a sale with the excess left as store credit (a manager approves by PIN), one bought back for cash.
// The customer spends the store credit, a traded-in phone is inspected and resold under the profit margin scheme (no VAT
// shown on the receipt), and the back office shows the trade-ins, their settlements, the report and the store credit in
// English and Arabic. Every figure is checked against arithmetic done here, and the accounts against the expected table.
// Screenshots go to SHOTS_DIR.
//
//   KITE_URL (http://localhost:5080)  SETUP_CODE_FILE  STUB_URL (http://127.0.0.1:8799)  KITE_DB (kite; PGHOST etc. for psql)
import { chromium } from 'playwright-core';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const BASE = process.env.KITE_URL ?? 'http://localhost:5080';
const STUB = process.env.STUB_URL ?? 'http://127.0.0.1:8799';
const DB = process.env.KITE_DB ?? 'kite';
const shots = process.env.SHOTS_DIR ?? 'shots-phase7';
const OWNER_PW = 'Muscat-Ruwi-2026!';
fs.mkdirSync(shots, { recursive: true });

// IMEIs with a correct check digit.
const NEW_1 = '352099001761481';
const TRADE_1 = '354670112341005';
const TRADE_2 = '352899112341010';

const bz = (x) => Math.round(Number(x) * 1000);
const rial = (b) => (b / 1000).toFixed(3);
const halfAway = (num, den) => Math.sign(num) * Math.floor((Math.abs(num) * 2 + den) / (2 * den));
const sql = (q) => execFileSync('psql', ['-d', DB, '-At', '-c', q], { encoding: 'utf8' }).trim();
const gl = (code) => bz(sql(`SELECT coalesce(sum(l.debit - l.credit), 0) FROM acct.journal_line l JOIN acct.account a ON a.id = l.account_id WHERE a.code = '${code}'`));

const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH ?? '/usr/bin/google-chrome', args: ['--no-sandbox'] });
const office = await (await browser.newContext({ viewport: { width: 1366, height: 900 } })).newPage();
const till = await (await browser.newContext({ viewport: { width: 1366, height: 900 } })).newPage();
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
const approveWithPin = async (p, label) => {
  const a = dialog(p, 'Manager approval');
  await a.waitFor();
  await a.getByText(label).waitFor();
  await a.getByLabel('Manager username').fill('huda');
  await a.getByLabel('PIN').fill('1357');
  await shot(p, `manager-approval-${label.toLowerCase().replace(/[^a-z]+/g, '-')}`);
  await a.getByRole('button', { name: 'Approve' }).click();
};

try {
  step('the server is set up (API), a licence is imported from the Nextdab stub, trade-ins are switched on, users get PINs');
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
  await api('PUT', '/api/v1/settings/modules', { changes: { device: true, refurb: true, tradein: true } }, owner);
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
  const product = async (nameEn, nameAr, productType, tracking, sku, barcode, price, warranty) =>
    (await api('POST', '/api/v1/catalog/products', { nameEn, nameAr, productType, tracking, taxCodeId: vat, defaultWarrantyDays: warranty, variants: [{ sku, barcodes: [barcode], price }] }, owner)).variants[0].variant.id;
  const galaxy = await product('Galaxy A55 128GB', 'جالاكسي A55 سعة 128 جيجا', 'device', 'imei', 'SM-A55-128', '6291000000201', 160, 365);
  const cable = await product('USB-C cable 1m', 'كابل USB-C متر', 'accessory', 'none', 'CBL-C-1M', '6291000000011', 2.5, 30);
  await api('POST', '/api/v1/inventory/adjustments', { branchId, kind: 'opening', note: 'opening stock', lines: [
    { variantId: cable, qty: 20, unitCost: 0.9, reasonCode: 'opening' },
    { variantId: galaxy, qty: 1, unitCost: 120, reasonCode: 'opening', units: [{ imei1: NEW_1, unitCost: 120, condition: 'brand_new', status: 'in_stock' }] },
  ] }, owner);
  await api('PUT', '/api/v1/pos/policy', { branchId: null, negativeStock: 'block', returnWindowDays: 14, cashRefundCap: 500, varianceThreshold: 1, blindClose: true, roundingLevel: 'line', cashRounding: 0, denominations: null, receiptFooterEn: 'Thank you for shopping with us', receiptFooterAr: 'شكراً لتسوقكم معنا', version: 1 }, owner);
  const glStart = Object.fromEntries(['1410', '2320', '2310', '1100', '2200'].map((c) => [c, gl(c)]));

  step('tax settings: the accountant records the Tax Authority\'s margin scheme approval; VAT-MARGIN switches on');
  await signIn(office, 'owner', OWNER_PW);
  await nav(office, 'Tax codes');
  await office.getByText('VAT-MARGIN is switched off.').waitFor();
  await office.getByLabel('Approval reference').fill('TA/MS/2026/0142');
  await office.getByLabel('Approved on').fill(new Date(Date.now() - 30 * 864e5).toISOString().slice(0, 10));
  await office.getByRole('button', { name: 'The Tax Authority approved the profit margin scheme' }).click();
  await office.getByText(/VAT-MARGIN is active\./).waitFor();
  await shot(office, 'tax-margin-scheme-approved', { full: true });
  check((await api('GET', '/api/v1/tax/codes', undefined, owner)).find((c) => c.code === 'VAT-MARGIN').status === 'active', 'VAT-MARGIN is active after the approval is recorded');

  step('trade-in settings: the guide pays 90.000 for a grade-A Galaxy; the deductions and the declaration are the defaults');
  await nav(office, 'Trade-in settings');
  await office.getByText('Cracked or faulty screen').waitFor();
  await office.getByLabel('SM-A55-128 A').fill('90');
  await office.getByRole('button', { name: 'Save the guide' }).click();
  for (let i = 0; i < 40 && !(await api('GET', '/api/v1/tradein/guide', undefined, owner)).length; i++) await sleep(250);
  await shot(office, 'tradein-settings-guide-and-deductions', { full: true });
  const GUIDE = bz((await api('GET', '/api/v1/tradein/guide', undefined, owner))[0].baseValue);
  check(GUIDE === bz(90), 'the guide value is saved: 90.000');
  const deductions = await api('GET', '/api/v1/tradein/deductions', undefined, owner);
  const screenPct = deductions.find((d) => d.code === 'SCREEN').value;

  step('till: registered as T01, the cashier signs in with her PIN and opens the shift with 100.000');
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
  await till.getByLabel('Pieces of 10', { exact: true }).fill('10');
  await till.getByRole('button', { name: 'Open shift' }).click();
  const scan = till.getByLabel('Scan a barcode or type a name');
  await scan.waitFor();
  const FLOAT = bz(100);

  // ---------------------------------------------------------------- trade-in 1: applied to a sale, the excess as store credit
  step('trade-in 1: a cable goes in the cart, then F6 opens the wizard; a new customer sells his Galaxy');
  await scan.fill('6291000000011'); await scan.press('Enter');
  const cart = till.getByRole('region', { name: 'Cart' });
  await cart.getByText('USB-C cable 1m').waitFor();
  await till.keyboard.press('F6');
  let w = dialog(till, 'Trade-in');
  await w.getByText('Who is selling the device?').waitFor();
  await w.getByRole('button', { name: 'New customer' }).click();
  await w.getByLabel('Name (English)').fill('Khalid Al Rashdi');
  await w.getByLabel('Phone').fill('9123 4567');
  await w.getByLabel('Civil ID').fill('12345678');
  await shot(till, 'wizard-1-customer');
  await w.getByRole('button', { name: 'Next' }).click();
  await w.getByLabel('Search the model (name or SKU)').fill('Galaxy');
  await w.locator('.picker-item', { hasText: 'Galaxy A55 128GB' }).click();
  await w.getByLabel(/^IMEI/).fill(TRADE_1);
  await w.getByRole('button', { name: 'Check the device' }).click();
  w = dialog(till, /^Trade-in TI-B01-/);
  await w.getByText('Checked with head office and the blocked list.').waitFor({ timeout: 20000 });
  await shot(till, 'wizard-2-device-checked');
  await w.getByRole('button', { name: 'Next' }).click();

  step('inspection: the screen fails, so the screen deduction is suggested; the customer removed the cloud lock; grade A');
  await w.getByRole('group').first().waitFor();
  for (const g of await w.getByRole('group').all()) await g.getByRole('button', { name: 'Pass' }).click();
  await w.getByRole('group', { name: /^Screen/ }).getByRole('button', { name: 'Fail' }).click();
  await w.getByLabel(/^%/).fill('88');
  await w.getByLabel('Grade').selectOption({ label: (await w.getByRole('option', { name: /^A · / }).innerText()).trim() });
  await w.getByText('suggested by the inspection').waitFor();
  check(await w.getByLabel(/removed their cloud account/).isChecked(), 'the cloud-lock confirmation is ticked from the checklist');
  await shot(till, 'wizard-3-inspection-with-deduction', { full: true });
  await w.getByRole('button', { name: 'Next' }).click();
  const screenDeduction = halfAway(GUIDE * screenPct, 100);
  const offer1 = GUIDE - screenDeduction;
  await w.getByLabel('Agreed value').waitFor();
  check(bz(await w.getByLabel('Agreed value').inputValue()) === offer1, `offer = guide 90.000 − screen ${screenPct}% (${rial(screenDeduction)}) = ${rial(offer1)}`);
  await w.getByText(/This value needs a manager’s approval: it is above your limit of 50\.000/).waitFor();
  await shot(till, 'wizard-4-valuation-needs-approval');
  await w.getByRole('button', { name: 'Get approval' }).click();
  await approveWithPin(till, 'Trade-in value above the limit or the guide');
  await w.getByText('Approved by a manager.').waitFor();
  check(true, `${rial(offer1)} is above the cashier's limit of 50.000: the manager approved it by PIN`);
  await w.getByRole('button', { name: 'Next' }).click();
  await w.getByText(/I confirm that I own this device/).waitFor();
  await w.getByLabel('Customer types their full name').fill('Khalid Al Rashdi');
  await shot(till, 'wizard-5-consent');
  await w.getByRole('button', { name: 'Next' }).click();
  check(await w.getByRole('radio', { name: /Use it to pay for this sale/ }).isChecked(), 'with a cart, the value goes to this sale by default');
  await shot(till, 'wizard-6-settlement');
  await w.getByRole('button', { name: 'Accept the device' }).click();
  await w.getByText('The trade-in value is ready as a payment for this sale.').waitFor({ timeout: 20000 });
  const ti1No = (await w.getByText(/^Trade-in TI-B01-\d{4}-\d+ accepted$/).innerText()).match(/TI-B01-\d{4}-\d+/)[0];
  const selfInvoice = (await w.getByText(/^Profit margin self-invoice/).innerText()).match(/SI-B01-\S+/)[0];
  await shot(till, 'wizard-7-accepted');
  await w.getByRole('button', { name: 'Close', exact: true }).click();
  const tis = await api('GET', '/api/v1/tradeins', undefined, owner);
  const ti1 = tis.find((x) => x.number === ti1No);
  check(ti1.status === 'accepted' && ti1.marginScheme && bz(ti1.agreedValue) === offer1, `${ti1No} accepted at ${rial(offer1)}, bought under the margin scheme (${selfInvoice})`);
  check(gl('1410') - glStart['1410'] === offer1 && gl('2320') - glStart['2320'] === -offer1, 'accepting posts used stock against trade-in clearing');

  step('pay: the trade-in pays the whole cable; 60.500 is left and the manager approves leaving it as store credit');
  await till.getByRole('button', { name: /^Pay 2\.500/ }).click();
  let pay = dialog(till, 'Payment');
  await pay.getByText(`${rial(offer1)} left on it`).waitFor();
  check(bz(await pay.getByLabel(new RegExp(ti1No)).inputValue()) === bz(2.5), 'the trade-in is applied up to the total');
  await shot(till, 'pay-with-trade-in-credit');
  await pay.getByRole('button', { name: 'Complete sale' }).click();
  const rest = dialog(till, 'Value left on the trade-in');
  const left1 = offer1 - bz(2.5);
  await rest.getByText(new RegExp(`has ${rial(left1).replace('.', '\\.')} left after this sale`)).waitFor({ timeout: 20000 });
  await shot(till, 'settle-the-rest');
  await rest.getByRole('button', { name: 'As store credit' }).click();
  await approveWithPin(till, 'Leave a trade-in as store credit');
  let receipt = till.getByRole('dialog', { name: /^INV-B01-/ });
  await receipt.waitFor({ timeout: 20000 });
  await receipt.getByText('Trade-in', { exact: true }).waitFor();
  await shot(till, 'receipt-paid-by-trade-in', { full: true });
  await receipt.getByRole('button', { name: 'New sale' }).click();
  const khalid = (await api('GET', '/api/v1/customers?q=Khalid', undefined, owner))[0];
  check(bz((await api('GET', `/api/v1/customers/${khalid.id}/store-credit`, undefined, owner)).balance) === left1, `Khalid's store credit is ${rial(left1)}`);

  // ---------------------------------------------------------------- trade-in 2: cash buy-back
  step('trade-in 2: Khalid sells a second Galaxy for cash; paying out of the drawer needs the manager');
  await till.getByRole('button', { name: 'Trade-in (F6)' }).click();
  w = dialog(till, 'Trade-in');
  await w.getByLabel('Find the customer (name or phone)').fill('Khalid');
  await w.locator('.picker-item', { hasText: 'Khalid Al Rashdi' }).click();
  await w.getByLabel('Civil ID').fill('12345678');
  await w.getByRole('button', { name: 'Next' }).click();
  await w.getByLabel('Search the model (name or SKU)').fill('Galaxy');
  await w.locator('.picker-item', { hasText: 'Galaxy A55 128GB' }).click();
  await w.getByLabel(/^IMEI/).fill(TRADE_2);
  await w.getByRole('button', { name: 'Check the device' }).click();
  w = dialog(till, /^Trade-in TI-B01-/);
  await w.getByText('Checked with head office and the blocked list.').waitFor({ timeout: 20000 });
  await w.getByRole('button', { name: 'Next' }).click();
  await w.getByRole('group').first().waitFor();
  for (const g of await w.getByRole('group').all()) await g.getByRole('button', { name: 'Pass' }).click();
  await w.getByLabel(/^%/).fill('91');
  await w.getByLabel('Grade').selectOption({ label: (await w.getByRole('option', { name: /^A · / }).innerText()).trim() });
  await w.getByRole('button', { name: 'Next' }).click();
  await w.getByLabel('Agreed value').fill('50');
  await w.getByRole('button', { name: 'Next' }).click();
  await w.getByLabel('How does the customer confirm?').selectOption('paper');
  await w.getByLabel('Paper form number').fill('TI-FORM-0002');
  await w.getByRole('button', { name: 'Next' }).click();
  await w.getByRole('radio', { name: /Pay cash from the drawer/ }).check();
  await w.getByRole('button', { name: 'Accept the device' }).click();
  await approveWithPin(till, 'Pay a trade-in out in cash');
  await w.getByText('Pay 50.000 to the customer from the drawer.').waitFor({ timeout: 20000 });
  await shot(till, 'wizard-cash-buy-back-done');
  await w.getByRole('button', { name: 'Close', exact: true }).click();
  const buyBack = bz(50);

  // ---------------------------------------------------------------- store credit spent
  step('Khalid buys the new Galaxy with his store credit and cash');
  await scan.fill(NEW_1); await scan.press('Enter');
  await cart.getByText(NEW_1).waitFor();
  await till.getByRole('button', { name: 'Customer', exact: true }).click();
  const cd = dialog(till, 'Customer');
  await cd.getByLabel('Search').fill('Khalid');
  await cd.locator('.picker-item', { hasText: 'Khalid Al Rashdi' }).click();
  await till.getByRole('button', { name: /^Pay 160\.000/ }).click();
  pay = dialog(till, 'Payment');
  await pay.getByText(`Balance ${rial(left1)}`).waitFor();
  await pay.getByRole('button', { name: 'Use all' }).click();
  const cashForPhone = bz(160) - left1;
  await pay.getByText(rial(cashForPhone)).first().waitFor();
  await shot(till, 'pay-with-store-credit');
  await pay.getByRole('button', { name: 'Complete sale' }).click();
  receipt = till.getByRole('dialog', { name: /^INV-B01-/ });
  await receipt.waitFor({ timeout: 20000 });
  await receipt.getByText('Store credit', { exact: true }).waitFor();
  await receipt.getByRole('button', { name: 'New sale' }).click();
  check(bz((await api('GET', `/api/v1/customers/${khalid.id}/store-credit`, undefined, owner)).balance) === 0, 'the store credit is spent');

  // ---------------------------------------------------------------- margin-scheme resale
  step('back office: the first traded-in phone is inspected and put on sale at 130.000');
  const unit1 = (await api('GET', `/api/v1/tradeins/${ti1.id}`, undefined, owner)).tradeIn.deviceUnitId;
  await office.goto(`${BASE}/app/devices/${unit1}/inspect`);
  await office.getByRole('heading', { name: /Inspect Galaxy A55 128GB/ }).waitFor();
  for (const g of await office.getByRole('group').all()) await g.getByRole('button', { name: 'Pass' }).click();
  await office.getByLabel(/^%/).fill('88');
  await office.getByLabel('Grade').selectOption({ label: (await office.getByRole('option', { name: /^A · / }).innerText()).trim() });
  await office.getByLabel('Own price').fill('130');
  await office.getByRole('button', { name: 'Save the inspection' }).click();
  await office.getByRole('heading', { name: 'Galaxy A55 128GB', exact: true }).waitFor({ timeout: 20000 });
  for (let i = 0; i < 40 && (await api('GET', `/api/v1/devices/${unit1}`, undefined, owner)).unit.status !== 'in_stock'; i++) await sleep(250);

  step('till: the traded-in phone sells under the profit margin scheme: VAT only on 130.000 − its purchase price, and none shown');
  await scan.fill(TRADE_1); await scan.press('Enter');
  await cart.getByText(TRADE_1).waitFor();
  const marginTax = halfAway((bz(130) - offer1) * 5, 105);
  await till.getByText('Profit margin scheme').waitFor();
  await shot(till, 'cart-margin-scheme-phone');
  await till.getByRole('button', { name: /^Pay 130\.000/ }).click();
  pay = dialog(till, 'Payment');
  await pay.getByLabel('Cash given').fill('130');
  await pay.getByRole('button', { name: 'Complete sale' }).click();
  receipt = till.getByRole('dialog', { name: /^INV-B01-/ });
  await receipt.waitFor({ timeout: 20000 });
  await receipt.getByText(/sold under the profit margin scheme: VAT is included in the price/).waitFor();
  await shot(till, 'receipt-margin-scheme', { full: true });
  const marginInv = (await receipt.getByRole('heading').first().innerText()).trim();
  check(await receipt.getByText(/^VAT 5%/).count() === 0, 'the margin receipt shows no VAT amount');
  await receipt.getByRole('button', { name: 'New sale' }).click();
  const marginSale = await api('GET', `/api/v1/sales/by-number/${marginInv}`, undefined, owner);
  const ml = marginSale.lines[0];
  check(bz(ml.marginPurchasePrice) === offer1 && bz(ml.tax) === marginTax && bz(ml.net) === bz(130) - marginTax,
    `${marginInv}: VAT = (130.000 − ${rial(offer1)}) × 5/105 = ${rial(marginTax)}, net ${rial(bz(130) - marginTax)}`);

  // ---------------------------------------------------------------- back office
  step('back office: the trade-ins, the first one with its settlements, the value-against-resale report');
  await nav(office, 'Trade-ins');
  await office.getByText(ti1No).waitFor();
  await shot(office, 'tradeins-list');
  await office.getByRole('link', { name: ti1No }).click();
  await office.getByRole('heading', { name: `Trade-in ${ti1No}` }).waitFor();
  await office.getByText(new RegExp(`Bought under the profit margin scheme \\(self-invoice ${selfInvoice}`)).waitFor();
  await shot(office, 'tradein-detail', { full: true });
  await office.getByRole('button', { name: 'Show the civil ID' }).click();
  await office.getByText(/Civil ID 12345678 \(this view is recorded\)/).waitFor();
  await nav(office, 'Trade-ins');
  await office.getByRole('tab', { name: 'Value against resale' }).click();
  await office.getByText(marginInv).waitFor();
  await shot(office, 'tradein-report', { full: true });
  await nav(office, 'Customers');
  await office.locator('tr', { hasText: 'Khalid Al Rashdi' }).getByRole('button', { name: 'Store credit' }).click();
  await dialog(office, 'Store credit').getByText(/Trade-in TI-B01-/).waitFor();
  await shot(office, 'customer-store-credit');
  await dialog(office, 'Store credit').getByRole('button', { name: 'Close', exact: true }).click();

  step('Arabic: the trade-in page right to left');
  await office.getByRole('button', { name: 'العربية' }).click();
  await office.goto(`${BASE}/app/tradeins/${ti1.id}`);
  await office.getByRole('heading', { name: `استبدال ${ti1No}` }).waitFor();
  await office.getByText('مسوّى').first().waitFor();
  await shot(office, 'tradein-detail-arabic', { full: true });
  await office.getByRole('button', { name: 'English' }).click();

  step('the accounts against the expected table, the drawer, and the books agree');
  const moved = (c) => gl(c) - glStart[c];
  const stdVat = halfAway(bz(2.5) * 5, 105) + halfAway(bz(160) * 5, 105);
  const expected = {
    '1410': offer1 + buyBack - offer1,                       // two phones in at their agreed values, the first sold at its cost
    '2320': 0,                                               // clearing: everything accepted is settled
    '2310': 0,                                               // store credit raised (60.500) and spent
    '1100': -buyBack + cashForPhone + bz(130),               // cash: buy-back out, the phone sales in
    '2200': -(stdVat + marginTax),                           // output VAT: two standard sales and the margin
  };
  for (const [code, value] of Object.entries(expected)) check(moved(code) === value, `account ${code} moved ${rial(moved(code))}, expected ${rial(value)}`);
  const figures = (await api('GET', '/api/v1/pos/shifts?limit=1', undefined, owner));
  const shiftId = figures[0].id;
  const f = (await api('GET', `/api/v1/pos/shifts/${shiftId}`, undefined, owner)).figures;
  check(bz(f.tradeInPayouts) === buyBack && bz(f.tradeInCredit) === bz(2.5) && bz(f.storeCreditPayments) === left1, 'the shift shows trade-in credit, store credit and the buy-back');
  check(bz(f.expectedCash) === FLOAT + cashForPhone + bz(130) - buyBack, `expected cash = 100.000 + ${rial(cashForPhone)} + 130.000 − 50.000`);
  await nav(office, 'Inventory');
  await office.getByRole('button', { name: 'Check the books' }).click();
  await office.getByText('Stock, ledger and accounts agree.').waitFor({ timeout: 20000 });
  const books = await api('GET', '/api/v1/inventory/check', undefined, owner);
  check(books.ok, 'consistency check OK');

  if (pageErrors.length) throw new Error('page errors: ' + pageErrors.join(' | '));
  console.log('PHASE 7 WALK-THROUGH PASSED');
} catch (e) {
  console.error('FAILED:', e);
  await shot(till, 'failure-till').catch(() => undefined);
  await shot(office, 'failure-office').catch(() => undefined);
  process.exitCode = 1;
} finally {
  await browser.close();
}
