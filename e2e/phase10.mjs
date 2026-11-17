// Phase 10 exit gate in a real browser against a real KITE server and PostgreSQL: head office sets the credit policy and opens
// an account with a limit, terms and offline allowances for two branches; at the till a senior cashier sells on account in three
// instalments, goes over the available credit with a manager's PIN (within the override cap) and is refused past the cap; a
// customer payment is taken on the drawer; at the office a transfer received at the other branch settles the oldest instalment
// and books the interbranch journal; the account is held and released; the statement is shown and downloaded; the ageing,
// branch exposure and approvals reports show the sales; a supplier is added with an opening balance; the screens are shown in
// English and Arabic; every account is checked against arithmetic done here and the daily ledger check finds no difference.
// Screenshots to SHOTS_DIR.
//
//   KITE_URL (http://localhost:5080)  SETUP_CODE_FILE  STUB_URL (http://127.0.0.1:8799)  KITE_DB (kite; PGHOST etc. for psql)
import { chromium } from 'playwright-core';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const BASE = process.env.KITE_URL ?? 'http://localhost:5080';
const STUB = process.env.STUB_URL ?? 'http://127.0.0.1:8799';
const DB = process.env.KITE_DB ?? 'kite';
const shots = process.env.SHOTS_DIR ?? 'shots-phase10';
const OWNER_PW = 'Muscat-Ruwi-2026!';
fs.mkdirSync(shots, { recursive: true });

const bz = (x) => Math.round(Number(x) * 1000);
const rial = (b) => (b / 1000).toFixed(3);
const sql = (q) => execFileSync('psql', ['-d', DB, '-At', '-c', q], { encoding: 'utf8' }).trim();
const gl = (code) => bz(sql(`SELECT coalesce(sum(l.debit - l.credit), 0) FROM acct.journal_line l JOIN acct.account a ON a.id = l.account_id WHERE a.code = '${code}'`));

const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH ?? '/usr/bin/google-chrome', args: ['--no-sandbox'] });
const office = await (await browser.newContext({ viewport: { width: 1366, height: 900 } })).newPage();
const till = await (await browser.newContext({ viewport: { width: 1366, height: 900 } })).newPage();
const pageErrors = [];
for (const p of [office, till]) p.on('pageerror', (e) => pageErrors.push(e.message));
for (const c of [office.context(), till.context()]) c.on('page', (popup) => { if (popup !== office && popup !== till) void popup.close().catch(() => undefined); });
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
const token = async (username, password) => (await api('POST', '/api/v1/auth/login', { username, password })).accessToken;
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
  step('the server is set up (API), a licence is imported from the Nextdab stub, customer credit and suppliers are switched on; a second branch; a manager and a senior cashier');
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
  await api('PUT', '/api/v1/settings/modules', { changes: { credit: true, purch: true } }, owner);
  const b02 = (await api('POST', '/api/v1/branches', { code: 'B02', nameEn: 'Sohar', nameAr: 'صحار' }, owner)).id;
  const roles = await api('GET', '/api/v1/roles', undefined, owner);
  const roleId = (code) => roles.find((r) => r.code === code).id;
  const branchId = (await api('GET', '/api/v1/branches', undefined, owner)).find((b) => b.code === 'B01').id;
  for (const [username, role, name, nameAr, pin] of [['huda', 'branch_manager', 'Huda Al Balushi', 'هدى البلوشية', '1357'], ['aisha', 'senior_cashier', 'Aisha Al Lawati', 'عائشة اللواتية', '2468']]) {
    await api('POST', '/api/v1/users', { username, displayNameEn: name, displayNameAr: nameAr, temporaryPassword: 'Temp-Pass-2026', preferredLanguage: 'en', defaultBranchId: branchId, roles: [{ roleId: roleId(role), branchId }] }, owner);
    const t = await token(username, 'Temp-Pass-2026');
    await api('POST', '/api/v1/auth/password', { currentPassword: 'Temp-Pass-2026', newPassword: 'Real-Pass-2026' }, t);
    await api('POST', '/api/v1/auth/pin', { pin, currentPassword: 'Real-Pass-2026' }, await token(username, 'Real-Pass-2026'));
  }
  owner = await token('owner', OWNER_PW);
  const vat = (await api('GET', '/api/v1/tax/codes', undefined, owner)).find((c) => c.code === 'VAT-STD').id;
  const product = async (nameEn, nameAr, sku, barcode, price) => (await api('POST', '/api/v1/catalog/products', { nameEn, nameAr, productType: 'accessory', tracking: 'none', taxCodeId: vat, defaultWarrantyDays: 30, variants: [{ sku, barcodes: [barcode], price }] }, owner)).variants[0].variant.id;
  const watch = await product('Smart watch', 'ساعة ذكية', 'SW-01', '6291000001001', 120);
  const tablet = await product('Tablet 10 inch', 'جهاز لوحي 10 بوصة', 'TB-10', '6291000001018', 200);
  await api('POST', '/api/v1/inventory/adjustments', { branchId, kind: 'opening', note: 'opening stock', lines: [
    { variantId: watch, qty: 5, unitCost: 70, reasonCode: 'opening' }, { variantId: tablet, qty: 5, unitCost: 130, reasonCode: 'opening' }] }, owner);
  const customer = (await api('POST', '/api/v1/customers', { nameEn: 'Khalid Al Rashdi Trading', nameAr: 'مؤسسة خالد الراشدي التجارية', kind: 'company', phone: '+96891234567', vatNumber: 'OM1199001122' }, owner)).id;
  const snap = Object.fromEntries(['1100', '1200', '1300', '1310', '2100', '2200', '2500', '3900', '4120'].map((c) => [c, gl(c)]));
  const moved = (code) => gl(code) - snap[code];

  // ---------------------------------------------------------------- policy and account (head office)
  step('head office sets the credit policy and opens an account: 300 limit, 30 days, allowances 180 at Ruwi and 120 at Sohar');
  await signIn(office, 'owner', OWNER_PW);
  await nav(office, 'Credit settings');
  await office.getByLabel('Offline cap per sale').fill('80');
  await office.getByRole('button', { name: 'Save' }).click();
  await office.getByText('Saved.').waitFor();
  check(Number((await api('GET', '/api/v1/credit/policy', undefined, owner)).offlineSaleCap) === 80, 'the offline cap per sale is 80');
  await shot(office, 'credit-settings', { full: true });
  await nav(office, 'Customer credit');
  await office.getByRole('button', { name: 'Open an account' }).click();
  let d = dialog(office, 'Open a credit account');
  await d.getByLabel('Find the customer').fill('Khalid');
  await d.getByRole('button', { name: /Khalid Al Rashdi Trading/ }).click();
  await d.getByLabel('Limit').fill('300');
  await d.getByLabel(/^B01/).fill('180');
  await d.getByLabel(/^B02/).fill('120');
  await shot(office, 'open-account');
  await d.getByRole('button', { name: 'Save' }).click();
  await office.getByRole('link', { name: 'Khalid Al Rashdi Trading' }).waitFor();
  const acc = await api('GET', `/api/v1/credit/accounts/${customer}`, undefined, owner);
  check(acc.account.creditLimit === 300 && acc.allowances.find((a) => a.code === 'B02').amount === 120, 'account opened with its allowances');

  // ---------------------------------------------------------------- the till
  step('the senior cashier sells a smart watch on account in three instalments');
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
  const scan = till.getByLabel('Scan a barcode or type a name');
  const chooseCustomer = async () => {
    await till.getByRole('button', { name: 'Customer', exact: true }).click();
    const c = dialog(till, 'Customer');
    await c.getByLabel('Search').fill('Khalid');
    await c.getByRole('button', { name: /Khalid Al Rashdi Trading/ }).click();
  };
  await scan.fill('6291000001001'); await scan.press('Enter');
  await chooseCustomer();
  await till.getByRole('button', { name: /^Pay/ }).click();
  let pay = dialog(till, 'Payment');
  await pay.getByText('Available 300.000').waitFor();
  await pay.getByRole('button', { name: 'Use available' }).click();
  await pay.getByLabel('Instalments').selectOption('3');
  await shot(till, 'pay-on-account-instalments');
  await pay.getByRole('button', { name: 'Complete sale' }).click();
  await till.locator('.receipt-frame').getByText('On account', { exact: true }).waitFor({ timeout: 15000 });
  await shot(till, 'receipt-on-account');
  const items1 = (await api('GET', `/api/v1/credit/accounts/${customer}`, undefined, owner)).openItems;
  check(items1.length === 3 && items1.map((i) => i.amount).join(',') === '40,40,40', 'three instalments of 40.000');
  await till.getByRole('button', { name: 'New sale' }).click();

  step('a tablet on account is 20 over the 180 left: the manager approves with her PIN; 60 more is past the 50 cap and refused');
  await scan.fill('6291000001018'); await scan.press('Enter');
  await chooseCustomer();
  await till.getByRole('button', { name: /^Pay/ }).click();
  pay = dialog(till, 'Payment');
  await pay.getByText('Available 180.000').waitFor();
  await pay.getByLabel('On account', { exact: true }).fill('200');
  await pay.getByText('More than the available credit: a manager must approve.').waitFor();
  await pay.getByRole('button', { name: 'Complete sale' }).click();
  const ap = dialog(till, 'Manager approval');
  await ap.getByText('Credit above the available limit').waitFor();
  await ap.getByLabel('Manager username').fill('huda');
  await ap.getByLabel('PIN').fill('1357');
  await shot(till, 'credit-override-approval');
  await ap.getByRole('button', { name: 'Approve' }).click();
  await till.locator('.receipt-frame').getByText('On account', { exact: true }).waitFor({ timeout: 15000 });
  check(sql(`SELECT over_by FROM crm.credit_decision d JOIN iam."user" u ON u.id = d.approved_by WHERE u.username = 'huda'`) === '20.000', 'the decision records 20 over, approved by Huda');
  await till.getByRole('button', { name: 'New sale' }).click();
  await scan.fill('6291000001001'); await scan.press('Enter');
  await chooseCustomer();
  await till.getByRole('button', { name: /^Pay/ }).click();
  pay = dialog(till, 'Payment');
  await pay.getByText('Available 0.000').waitFor();
  await pay.getByLabel('On account', { exact: true }).fill('120');
  await pay.getByRole('button', { name: 'Complete sale' }).click();
  const capError = pay.getByText(/This is 120\.000 over, more than a manager may approve \(50\.000\)/);
  await capError.waitFor();
  await capError.scrollIntoViewIfNeeded();
  await shot(till, 'credit-past-the-cap');
  check(sql(`SELECT count(*) FROM crm.credit_ledger WHERE kind = 'charge'`) === '2', 'nothing was charged past the cap');
  await pay.getByRole('button', { name: 'Cancel' }).click();
  await till.getByRole('button', { name: 'Clear' }).click().catch(() => undefined);

  step('a customer payment of 100 in cash on the drawer settles the oldest instalments');
  await till.getByRole('button', { name: 'Credit payment' }).click();
  const cp = dialog(till, 'Payment on a credit account');
  await cp.getByLabel('Find the customer').fill('Khalid');
  await cp.getByRole('button', { name: /Khalid Al Rashdi Trading/ }).click();
  await cp.getByText('Owed 320.000').waitFor();
  await cp.getByLabel('Amount').fill('100');
  await shot(till, 'credit-payment');
  await cp.getByRole('button', { name: 'Receive a payment' }).click();
  await till.getByText(/Payment RC-B01-\S+ taken: 100\.000\. Owed 220\.000/).waitFor();
  check(sql(`SELECT coalesce(sum(amount), 0) FROM sales.payment WHERE customer_receipt_id IS NOT NULL`) === '100.000', 'the payment is on the drawer');

  // ---------------------------------------------------------------- the office
  step('at the office: a transfer of 50 received at Sohar settles Ruwi’s next instalment through the interbranch accounts');
  await office.goto(BASE + `/app/credit/${customer}`);
  await office.getByRole('heading', { name: 'Khalid Al Rashdi Trading', exact: true }).waitFor();
  await office.getByRole('button', { name: 'Receive a payment' }).click();
  d = dialog(office, 'Payment from Khalid Al Rashdi Trading');
  await d.getByLabel('Branch').selectOption({ label: 'B02 · Sohar' });
  await d.getByLabel('Amount').fill('50');
  await d.getByLabel('Transfer reference').fill('TRF-2026-118');
  await d.getByRole('button', { name: 'Receive a payment' }).click();
  await office.getByText(/Payment RC-B02-\S+: 50\.000\. Owed now 170\.000\./).waitFor();
  check(sql(`SELECT count(*) FROM acct.journal WHERE posting_kind = 'credit.interbranch'`) === '1', 'one interbranch journal');
  await shot(office, 'credit-account', { full: true });

  step('a manual hold stops credit at once; released, the statement and its PDF');
  await office.getByRole('button', { name: 'Put on hold' }).click();
  d = dialog(office, 'Put Khalid Al Rashdi Trading on hold');
  await d.getByLabel('Reason').fill('Cheque returned by the bank');
  await d.getByRole('button', { name: 'Put on hold' }).click();
  await office.getByText('On hold: Cheque returned by the bank').waitFor();
  await shot(office, 'account-on-hold');
  await scan.fill('6291000001001'); await scan.press('Enter');
  await chooseCustomer();
  await till.getByRole('button', { name: /^Pay/ }).click();
  pay = dialog(till, 'Payment');
  await pay.getByText('Credit on hold: Cheque returned by the bank').waitFor();
  check(await pay.getByLabel('On account', { exact: true }).count() === 0 || await pay.getByLabel('On account', { exact: true }).isDisabled(), 'the till offers no credit while the account is on hold');
  await shot(till, 'till-account-on-hold');
  await pay.getByRole('button', { name: 'Cancel' }).click();
  await till.getByRole('button', { name: 'Clear' }).click().catch(() => undefined);
  await office.getByRole('button', { name: 'Release the hold' }).click();
  d = dialog(office, 'Release Khalid Al Rashdi Trading');
  await d.getByLabel('Reason').fill('Paid by transfer');
  await d.getByRole('button', { name: 'Release the hold' }).click();
  await office.getByText('On hold: Cheque returned by the bank').waitFor({ state: 'detached' });
  const statement = await api('GET', `/api/v1/credit/customers/${customer}/statement`, undefined, owner);
  check(bz(statement.closing) === bz(170) && bz(statement.opening) + bz(statement.debits) - bz(statement.credits) === bz(statement.closing), 'statement: opening + lines = closing = 170.000');
  const pdf = await api('GET', `/api/v1/credit/customers/${customer}/statement.pdf`, undefined, owner, true);
  check(pdf.headers.get('content-type') === 'application/pdf' && (await pdf.arrayBuffer()).byteLength > 1500, 'the statement PDF downloads');
  await office.getByText(/Statement: Khalid Al Rashdi Trading/).scrollIntoViewIfNeeded();
  await shot(office, 'statement', { full: true });

  step('reports: ageing, exposure by branch, approvals and offline sales');
  await nav(office, 'Customer credit');
  await office.getByRole('tab', { name: 'Ageing' }).click();
  await office.getByRole('columnheader', { name: '1–30 days' }).waitFor();
  await shot(office, 'credit-ageing');
  await office.getByRole('tab', { name: 'By branch' }).click();
  await office.getByRole('columnheader', { name: 'Owed at the branch' }).waitFor();
  await shot(office, 'credit-by-branch');
  await office.getByRole('tab', { name: 'Approvals and breaches' }).click();
  await office.getByText('Huda Al Balushi').waitFor();
  await shot(office, 'credit-approvals');

  // ---------------------------------------------------------------- suppliers
  step('a supplier is added at head office with an opening balance of 400 owed at Ruwi');
  await nav(office, 'Suppliers');
  await office.getByRole('button', { name: 'Add a supplier' }).click();
  d = dialog(office, 'New supplier');
  await d.getByLabel('Name (English)').fill('Gulf Mobile Parts LLC');
  await d.getByLabel('Name (Arabic)').fill('شركة الخليج لقطع الهواتف');
  await d.getByLabel('VAT number').fill('OM1100445566');
  await d.getByLabel('Payment terms (days)').fill('45');
  await d.getByRole('button', { name: 'Save' }).click();
  await office.getByRole('link', { name: 'Gulf Mobile Parts LLC' }).click();
  await office.getByRole('heading', { name: 'Gulf Mobile Parts LLC', exact: true }).waitFor();
  await office.getByRole('button', { name: 'Opening balance' }).click();
  d = dialog(office, 'Opening balance with Gulf Mobile Parts LLC');
  await d.getByLabel('Amount').fill('400');
  await d.getByRole('button', { name: 'Save' }).click();
  await office.getByText(/^Opening balance OB-B01-/).waitFor();
  await shot(office, 'supplier', { full: true });

  step('the daily ledger check, run now: customers against 1300 and suppliers against 2100, branch by branch');
  await nav(office, 'Customer credit');
  await office.getByRole('tab', { name: 'Ledger check' }).click();
  await office.getByRole('button', { name: 'Check now' }).click();
  await office.getByText('Everything agrees.').waitFor();
  await office.getByRole('cell', { name: '400.000' }).first().waitFor();
  await shot(office, 'ledger-check');

  step('the credit and supplier screens in Arabic');
  await office.getByRole('button', { name: 'العربية' }).click();
  await office.goto(BASE + `/app/credit/${customer}`);
  await office.getByRole('heading', { name: 'مؤسسة خالد الراشدي التجارية', exact: true }).waitFor();
  await shot(office, 'credit-account-ar', { full: true });
  await office.goto(BASE + '/app/suppliers');
  await office.getByRole('heading', { name: 'الموردون' }).waitFor();
  await shot(office, 'suppliers-ar');
  await office.getByRole('button', { name: 'English' }).click();

  // ---------------------------------------------------------------- accounts
  step('accounts against arithmetic done here');
  const sold = bz(120) + bz(200);
  const vatOf = (g) => Math.round(g * 5 / 105);
  const expected = {
    '1300': sold - bz(100) - bz(50),                       // receivables: two sales on account less two payments
    '1100': bz(100),                                       // the cash payment on the drawer
    '1200': bz(50),                                        // the transfer received at Sohar
    '1310': bz(50), '2500': -bz(50),                       // Ruwi is owed by Sohar; Sohar owes Ruwi
    '2200': -(vatOf(bz(120)) + vatOf(bz(200))),            // VAT on both sales
    '4120': -(bz(120) - vatOf(bz(120)) + bz(200) - vatOf(bz(200))),
    '2100': -bz(400), '3900': bz(400),                     // the supplier's opening balance
  };
  for (const [code, value] of Object.entries(expected)) check(moved(code) === value, `account ${code} moved ${rial(moved(code))}, expected ${rial(value)}`);
  check(sql(`SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE balance_type = 'receivable'`) === '170.000', 'the customer ledger is 170.000');
  check(sql(`SELECT string_agg(code || '=' || sum::text, ',' ORDER BY code) FROM (SELECT b.code, sum(l.amount) FROM crm.credit_ledger l JOIN core.branch b ON b.id = l.branch_id WHERE l.balance_type = 'receivable' GROUP BY b.code) x`) === 'B01=170.000,B02=0.000', 'Ruwi is owed 170.000; Sohar passed its 50.000 on');
  check(sql(`SELECT count(*) FROM acct.subledger_check WHERE run_id = (SELECT run_id FROM acct.subledger_check ORDER BY checked_at DESC LIMIT 1) AND difference <> 0`) === '0', 'the ledger check finds no difference');
  check(sql(`SELECT ok FROM audit.verify_chain()`) === 't', 'the audit chain verifies');
  void b02;

  if (pageErrors.length) throw new Error('page errors: ' + pageErrors.join(' | '));
  console.log('PHASE 10 WALK-THROUGH PASSED');
} catch (e) {
  console.error('FAILED:', e);
  await shot(till, 'failure-till', { full: true }).catch(() => undefined);
  await shot(office, 'failure-office', { full: true }).catch(() => undefined);
  for (const p of [office, till]) console.error('  page alerts:', (await p.locator('.alert').allTextContents().catch(() => [])).join(' | '));
  process.exitCode = 1;
} finally {
  await browser.close();
}
