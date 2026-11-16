// Phase 9 exit gate in a real browser against a real KITE server and PostgreSQL: the owner writes commission rules for two
// technicians (10% of labour, 20% of repair profit) and sets rework clawback; one job takes the only battery in stock, so a
// second job waits for parts (the till cannot sell the reserved battery) and moves on by itself when one arrives; a serial-
// tracked board is fitted by its serial; outside work is recorded at cost; the second job is shared 40/60; both are collected at
// the till and their commissions are generated once; the first invoice is corrected at the till (credited in full, cash back,
// re-invoiced with less labour) and its commission reversed and generated again; the commissions are approved and paid (bank
// and payroll); a warranty claim takes back half the commission. Every figure is checked against arithmetic done here, the
// accounts against the expected table, and the commission screens are shown in English and Arabic. Screenshots to SHOTS_DIR.
//
//   KITE_URL (http://localhost:5080)  SETUP_CODE_FILE  STUB_URL (http://127.0.0.1:8799)  KITE_DB (kite; PGHOST etc. for psql)
import { chromium } from 'playwright-core';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const BASE = process.env.KITE_URL ?? 'http://localhost:5080';
const STUB = process.env.STUB_URL ?? 'http://127.0.0.1:8799';
const DB = process.env.KITE_DB ?? 'kite';
const shots = process.env.SHOTS_DIR ?? 'shots-phase9';
const OWNER_PW = 'Muscat-Ruwi-2026!';
const PNG = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==';
const SERIAL = 'LB778899';
fs.mkdirSync(shots, { recursive: true });

const bz = (x) => Math.round(Number(x) * 1000);
const rial = (b) => (b / 1000).toFixed(3);
const halfAway = (num, den) => Math.sign(num) * Math.floor((Math.abs(num) * 2 + den) / (2 * den));
const vatIn = (gross) => halfAway(gross * 5, 105);           // VAT inside a VAT-inclusive price at 5%
const net = (gross) => gross - vatIn(gross);
const pct = (amount, percent) => halfAway(amount * Math.round(percent * 10000), 1_000_000);   // amount × percent / 100, to the baisa
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

async function api(method, p, body, token, expectStatus) {
  const r = await fetch(BASE + p, { method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  if (expectStatus) return { status: r.status, body: text ? JSON.parse(text) : null };
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
const draw = async (p, name, scope = p) => {
  const c = scope.getByRole('img', { name });
  await c.scrollIntoViewIfNeeded();
  const b = await c.boundingBox();
  await p.mouse.move(b.x + 20, b.y + 50); await p.mouse.down();
  await p.mouse.move(b.x + 110, b.y + 100, { steps: 10 }); await p.mouse.move(b.x + 200, b.y + 40, { steps: 10 }); await p.mouse.up();
};

try {
  step('the server is set up (API), a licence is imported from the Nextdab stub, repairs and commissions are switched on; two technicians and a branch manager');
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
  await api('PUT', '/api/v1/settings/modules', { changes: { device: true, repair: true, comm: true } }, owner);
  const roles = await api('GET', '/api/v1/roles', undefined, owner);
  const roleId = (code) => roles.find((r) => r.code === code).id;
  const branchId = (await api('GET', '/api/v1/branches', undefined, owner))[0].id;
  const ids = {};
  for (const [username, role, name, nameAr, pin] of [['tariq', 'technician', 'Tariq Al Amri', 'طارق العامري', '8642'], ['noor', 'technician', 'Noor Al Kindi', 'نور الكندي', '9753'], ['huda', 'branch_manager', 'Huda Al Balushi', 'هدى البلوشية', '1357']]) {
    ids[username] = (await api('POST', '/api/v1/users', { username, displayNameEn: name, displayNameAr: nameAr, temporaryPassword: 'Temp-Pass-2026', preferredLanguage: 'en', defaultBranchId: branchId, roles: [{ roleId: roleId(role), branchId }] }, owner)).id;
    const t = await token(username, 'Temp-Pass-2026');
    await api('POST', '/api/v1/auth/password', { currentPassword: 'Temp-Pass-2026', newPassword: 'Real-Pass-2026' }, t);
    await api('POST', '/api/v1/auth/pin', { pin, currentPassword: 'Real-Pass-2026' }, await token(username, 'Real-Pass-2026'));
  }
  owner = await token('owner', OWNER_PW);
  const vat = (await api('GET', '/api/v1/tax/codes', undefined, owner)).find((c) => c.code === 'VAT-STD').id;
  const product = async (nameEn, nameAr, sku, barcode, price, tracking = 'none') => (await api('POST', '/api/v1/catalog/products', { nameEn, nameAr, productType: 'spare_part', tracking, taxCodeId: vat, defaultWarrantyDays: 90, variants: [{ sku, barcodes: [barcode], price }] }, owner)).variants[0].variant.id;
  const battery = await product('Battery iPhone 12', 'بطارية آيفون 12', 'BAT-IP12', '6291000000401', 21);
  const board = await product('Logic board iPhone 12', 'لوحة آيفون 12', 'LB-IP12', '6291000000418', 60, 'serial');
  await api('POST', '/api/v1/inventory/adjustments', { branchId, kind: 'opening', note: 'opening stock', lines: [
    { variantId: battery, qty: 1, unitCost: 8, reasonCode: 'opening' },
    { variantId: board, qty: 1, unitCost: 30, reasonCode: 'opening', units: [{ serial: SERIAL, unitCost: 30 }] },
  ] }, owner);
  const setup = await api('GET', '/api/v1/repair-setup', undefined, owner);
  const labour = setup.services.find((s) => s.sku === 'REPAIR-LABOUR').id;
  const category = setup.categories.find((c) => c.code === 'BATTERY').id;
  const CODES = ['1100', '1200', '1430', '1460', '2110', '2400', '2410', '4200', '4210', '4900', '2200', '5200', '5210', '6300'];
  const glStart = Object.fromEntries(CODES.map((c) => [c, gl(c)]));
  const moved = (code) => gl(code) - glStart[code];

  const intake = async (name, imei, tech) => {
    const customer = (await api('POST', '/api/v1/customers', { nameEn: name, nameAr: 'عميل ' + name, kind: 'individual', phone: '9' + String(Math.floor(1000000 + Math.random() * 8999999)) }, owner)).id;
    const job = (await api('POST', '/api/v1/repairs', {
      customerId: customer, categoryId: category, device: { imei1: imei, brand: 'Apple', model: 'iPhone 12' }, problem: 'Battery drains by noon', faultCodes: [], condition: { screen: 'ok' },
      accessories: [], passcodeMode: 'none', termsName: name, termsSignature: PNG, lang: 'en', photos: [], branchId, technicianId: tech,
    }, owner)).job;
    for (const to of ['inspection_pending', 'under_diagnosis']) await api('POST', `/api/v1/repairs/${job.id}/status`, { to }, owner);
    return job;
  };
  const estimate = async (job, lines) => {
    const d = await api('POST', `/api/v1/repairs/${job.id}/estimates`, { lines }, owner);
    return api('POST', `/api/v1/repairs/${job.id}/estimates/${d.estimates[0].id}/approve`, { method: 'phone', approverName: 'Customer', phone: '+96899112233', then: 'in_progress' }, owner);
  };

  // ---------------------------------------------------------------- rules and settings in the browser
  step('commission rules: Tariq 10% of labour, Noor 20% of repair profit (at least 0.500); rework within 30 days takes back 50%');
  await signIn(office, 'owner', OWNER_PW);
  await nav(office, 'Commission rules');
  for (const [nameEn, nameAr, basis, rate, tech, min] of [['Tariq: 10% of labour', 'طارق: 10% من الأجور', '% of labour', '10', 'Tariq Al Amri', ''], ['Noor: 20% of repair profit', 'نور: 20% من ربح الصيانة', '% of repair profit', '20', 'Noor Al Kindi', '0.5']]) {
    await office.getByRole('button', { name: 'New rule' }).click();
    const d = dialog(office, 'New rule');
    await d.getByLabel('Name (English)').fill(nameEn);
    await d.getByLabel('Name (Arabic)').fill(nameAr);
    await d.getByLabel('Basis').selectOption({ label: basis });
    await d.getByLabel('Rate %').fill(rate);
    if (min) await d.getByLabel('Minimum per job').fill(min);
    await d.getByLabel('Technician').selectOption({ label: tech });
    await d.getByLabel('From').fill(new Date(Date.now() - 864e5).toISOString().slice(0, 10));
    await d.getByRole('button', { name: 'Save rule' }).click();
    await d.waitFor({ state: 'hidden' });
    await office.getByText(nameEn).waitFor();
  }
  await office.getByLabel('Rework window (days, 0 = none)').fill('30');
  await office.getByLabel('Taken back on rework (%)').fill('50');
  await office.getByRole('button', { name: 'Save', exact: true }).click();
  await office.getByText('Saved.').waitFor();
  await shot(office, 'commission-rules-en', { full: true });
  const cs = await api('GET', '/api/v1/commissions/settings', undefined, owner);
  check(cs.clawbackDays === 30 && bz(cs.clawbackPercent) === bz(50) && cs.approvalBy === 'branch', 'settings saved: clawback 30 days, 50%, approved at the branch');
  const rules = await api('GET', '/api/v1/commissions/rules', undefined, owner);
  check(rules.length === 2 && rules.every((r) => r.rank === 2 && r.version === 1), 'two technician rules, version 1');

  // ---------------------------------------------------------------- parts held and waited for
  step('job A (Tariq) takes the only battery and the board; job B (Noor) needs a battery too and waits for it');
  const A = await intake('Khalid Al Rawahi', '356789012345672', ids.tariq);
  const dA = await estimate(A, [{ kind: 'labour', variantId: labour, qty: 1, unitPrice: 15 }, { kind: 'part', variantId: battery, qty: 1, unitPrice: 21 }, { kind: 'part', variantId: board, qty: 1, unitPrice: 60 }]);
  check(dA.job.status === 'in_progress' && dA.parts.every((p) => bz(p.reserved) === bz(1) && bz(p.requested) === 0), 'job A in progress, its battery and board held in stock');
  const B = await intake('Maryam Al Hinai', '356789012345680', ids.noor);
  const dB = await estimate(B, [{ kind: 'labour', variantId: labour, qty: 1, unitPrice: 10 }, { kind: 'part', variantId: battery, qty: 1, unitPrice: 21 }]);
  check(dB.job.status === 'waiting_parts' && bz(dB.parts[0].requested) === bz(1), 'job B waits for parts: one battery requested');
  await office.goto(BASE + `/app/repairs/${B.id}`);
  await office.getByRole('tab', { name: 'Parts and labour' }).click();
  await office.getByText('This job is waiting for 1 parts. It moves on by itself when they arrive.').waitFor();
  await shot(office, 'job-b-waiting-for-parts', { full: true });
  await nav(office, 'Parts needed');
  await office.getByRole('link', { name: B.number }).waitFor();
  await shot(office, 'parts-needed', { full: true });

  step('the till cannot sell the battery held for job A');
  await till.goto(BASE + '/pos');
  await till.getByLabel('Username').fill('owner');
  await till.getByLabel('Password').fill(OWNER_PW);
  await till.getByRole('button', { name: 'Sign in' }).click();
  await till.getByLabel('Code').fill('T01');
  await till.getByLabel('Name').fill('Front counter');
  await till.getByRole('button', { name: 'Register this device' }).click();
  await till.getByRole('heading', { name: 'Till T01' }).waitFor();
  await till.getByLabel('Username').fill('huda');
  await till.getByLabel('PIN').fill('1357');
  await till.getByRole('button', { name: 'Sign in' }).click();
  await till.getByRole('heading', { name: 'Open shift' }).waitFor();
  await till.getByLabel('Pieces of 10', { exact: true }).fill('10');
  await till.getByRole('button', { name: 'Open shift' }).click();
  const scan = till.getByLabel('Scan a barcode or type a name');
  await scan.fill('6291000000401'); await scan.press('Enter');
  await till.getByRole('button', { name: /^Pay / }).click();
  let pay = dialog(till, 'Payment');
  await pay.getByLabel('Cash given').fill('21');
  await pay.getByRole('button', { name: 'Complete sale' }).click();
  await till.getByText(/reserved for or awaited by repair jobs/).first().waitFor();
  await shot(till, 'till-refuses-reserved-battery');
  await pay.getByRole('button', { name: 'Cancel' }).click().catch(() => undefined);
  await till.getByRole('button', { name: 'Clear' }).click().catch(() => undefined);

  step('a battery arrives: given out at once to job B, which moves to in progress by itself');
  await api('POST', '/api/v1/inventory/adjustments', { branchId, kind: 'opening', note: 'arrived', lines: [{ variantId: battery, qty: 1, unitCost: 8, reasonCode: 'opening' }] }, owner);
  await office.getByRole('button', { name: 'Give out arrived stock now' }).click();
  await office.getByText('1 jobs got their parts and moved on.').waitFor();
  check((await api('GET', `/api/v1/repairs/${B.id}`, undefined, owner)).job.status === 'in_progress', 'job B is in progress');

  // ---------------------------------------------------------------- the work
  step('job A: the battery fitted, the board scanned by its serial; job B: outside work and shared 40/60');
  await api('POST', `/api/v1/repairs/${A.id}/parts`, { variantId: battery, qty: 1 }, owner);
  await office.goto(BASE + `/app/repairs/${A.id}`);
  await office.getByRole('tab', { name: 'Parts and labour' }).click();
  await office.getByLabel('Part', { exact: true }).fill('Logic board');
  await office.getByRole('button', { name: /Logic board iPhone 12/ }).click();
  await office.getByLabel('Scan the part’s serial number').fill(SERIAL);
  await office.getByRole('button', { name: 'Fit part' }).click();
  await office.getByText(SERIAL).first().waitFor();
  await shot(office, 'job-a-serial-board-fitted', { full: true });
  const wipA = bz((await api('GET', `/api/v1/repairs/${A.id}`, undefined, owner)).money.wip);
  check(wipA === bz(8) + bz(30), `job A work in progress ${rial(wipA)}: battery 8.000 and the board’s own cost 30.000`);

  await office.goto(BASE + `/app/repairs/${B.id}`);
  await office.getByRole('button', { name: 'Change shares' }).click();
  const techSelects = office.getByLabel('Technician');
  await techSelects.nth(1).selectOption({ label: 'Tariq Al Amri' });
  await techSelects.nth(2).selectOption({ label: 'Noor Al Kindi' });
  await office.getByLabel('Share %').nth(0).fill('40');
  await office.getByLabel('Share %').nth(1).fill('60');
  await office.getByRole('button', { name: 'Save', exact: true }).click();
  await office.getByText('Noor Al Kindi · 60%').waitFor();
  await api('POST', `/api/v1/repairs/${B.id}/parts`, { variantId: battery, qty: 1 }, owner);
  await office.getByRole('tab', { name: 'Parts and labour' }).click();
  await office.getByLabel('Done by').fill('Ruwi Board Lab');
  await office.getByLabel('Cost', { exact: true }).fill('4.500');
  await office.getByLabel('Their reference').fill('RBL-2026-118');
  await office.getByRole('button', { name: 'Add outside work' }).click();
  await office.getByText('Ruwi Board Lab').waitFor();
  await shot(office, 'job-b-outside-work-and-shares', { full: true });
  check(moved('2110') === -bz(4.5) && bz((await api('GET', `/api/v1/repairs/${B.id}`, undefined, owner)).money.wip) === bz(8) + bz(4.5), 'outside work 4.500 into work in progress, owed to the supplier');
  for (const job of [A, B]) for (const to of ['testing', 'completed', 'ready']) await api('POST', `/api/v1/repairs/${job.id}/status`, { to }, owner);

  // ---------------------------------------------------------------- collection and generation
  const collect = async (job, gross) => {
    await till.getByRole('button', { name: 'Repairs', exact: true }).click();
    const c = dialog(till, 'Repairs at the counter');
    await c.getByLabel(/Scan the job card/).fill(job.number);
    await c.getByRole('button', { name: new RegExp(job.number) }).click();
    await c.getByRole('button', { name: 'Collect' }).click();
    await c.getByLabel('Who is collecting').selectOption('verified');
    await c.getByLabel('Name of the person collecting').fill('The customer');
    await draw(till, 'Signature', c);
    await c.getByRole('button', { name: new RegExp(`^Pay ${rial(gross).replace('.', '\\.')}`) }).click();
    pay = dialog(till, 'Payment');
    await pay.getByLabel('Cash given').fill(rial(gross));
    await pay.getByRole('button', { name: 'Complete sale' }).click();
    await till.getByText(new RegExp(`Job ${job.number} delivered\\. Invoice INV-B01-`)).waitFor({ timeout: 20000 });
  };
  step('both devices collected at the till: A 96.000, B 31.000; their commissions are generated once');
  await collect(A, bz(96));
  await collect(B, bz(31));
  await shot(till, 'till-collected');
  const accrualsOf = async (job) => api('GET', `/api/v1/commissions?jobId=${job.id}`, undefined, owner);
  const LA = net(bz(15)), LB = net(bz(10)), PB = net(bz(21));
  const commA1 = pct(LA, 10);
  const commBT = pct(pct(LB, 10), 40);
  const profitB = LB + PB - bz(8) - bz(4.5);
  const commBN = pct(pct(profitB, 20), 60);
  let a = await accrualsOf(A), b = await accrualsOf(B);
  check(a.length === 1 && bz(a[0].amount) === commA1, `job A: Tariq 10% of ${rial(LA)} labour = ${rial(commA1)}`);
  check(b.length === 2 && bz(b.find((x) => x.technicianId === ids.tariq).amount) === commBT && bz(b.find((x) => x.technicianId === ids.noor).amount) === commBN,
    `job B shared: Tariq 40% of 10% of ${rial(LB)} = ${rial(commBT)}; Noor 60% of 20% of profit ${rial(profitB)} = ${rial(commBN)}`);
  for (let i = 0; i < 3; i++) await api('POST', '/api/v1/commissions/generate', {}, owner);
  check((await accrualsOf(A)).length === 1 && (await accrualsOf(B)).length === 2, 'the generator run three more times writes nothing new');
  check(bz(sql(`SELECT repair.outside_cost('${B.id}')`)) === bz(4.5) && moved('5210') === bz(4.5), 'outside work cleared to its own cost at delivery (5210)');

  // ---------------------------------------------------------------- correction at the till
  step('job A’s invoice corrected at the till: labour 15.000 → 12.000; 96.000 back in cash and 93.000 paid again');
  await till.getByRole('button', { name: 'Repairs', exact: true }).click();
  let c = dialog(till, 'Repairs at the counter');
  await c.getByLabel('Correct the invoice').check();
  await c.getByLabel(/Delivered job/).fill(A.number);
  await c.getByRole('button', { name: new RegExp(A.number) }).click();
  await c.getByText('Invoice that stands now').waitFor();
  await c.getByLabel('Why is it corrected?').fill('Labour charged at the wrong rate');
  await c.getByLabel('Correct price').first().fill('12');
  await shot(till, 'till-correct-invoice');
  await c.getByRole('button', { name: /^Take payment 93\.000/ }).click();
  pay = dialog(till, 'Payment');
  await pay.getByLabel('Cash given').fill('93');
  await pay.getByRole('button', { name: 'Complete sale' }).click();
  await till.getByText(/Credit note CN-B01-\S+ issued; corrected invoice INV-B01-\S+\./).waitFor({ timeout: 20000 });
  await shot(till, 'till-corrected');
  const commA2 = pct(net(bz(12)), 10);
  a = await accrualsOf(A);
  check(a.some((x) => x.kind === 'reversal' && bz(x.amount) === -commA1) && a.some((x) => x.kind === 'repair' && x.kindVersion === 2 && bz(x.amount) === commA2),
    `job A: ${rial(commA1)} reversed, ${rial(commA2)} generated from the corrected invoice`);
  const dA2 = await api('GET', `/api/v1/repairs/${A.id}`, undefined, owner);
  check(dA2.corrections.length === 1 && bz(dA2.invoice.header.grossTotal) === bz(93) && bz(dA2.corrections[0].credited) === bz(96), 'the job keeps its record: 96.000 credited, the corrected invoice of 93.000 stands');

  // ---------------------------------------------------------------- approval and payout
  step('the commissions approved in the back office and paid: Tariq by bank, Noor through payroll');
  await nav(office, 'Commissions');
  await office.getByRole('tab', { name: 'To approve' }).waitFor();
  await office.getByRole('link', { name: B.number }).first().waitFor();
  const boxes = office.locator('main').getByRole('checkbox');
  check(await boxes.count() === 3, 'three commissions to approve: job A’s corrected one and job B’s two shares');
  for (let i = 0; i < 3; i++) await boxes.nth(i).check();
  await shot(office, 'commissions-to-approve', { full: true });
  await office.getByRole('button', { name: /^Approve selected/ }).click();
  await office.getByText(/approved\.$/).waitFor();
  await office.getByRole('tab', { name: 'To pay' }).click();
  const payouts = {};
  for (const [name, method] of [['Tariq Al Amri', 'Bank transfer'], ['Noor Al Kindi', 'Through payroll']]) {
    await office.getByRole('row', { name: new RegExp(name) }).getByRole('button', { name: 'Pay' }).click();
    const d = dialog(office, `Pay ${name}`);
    await d.getByLabel('Paid by').selectOption({ label: method });
    await d.getByRole('button', { name: /^Pay / }).click();
    await office.getByText(/^Payout COM-B01-\S+: /).waitFor();
    payouts[name] = await office.getByText(/^Payout COM-B01-\S+: /).innerText();
  }
  const tariqDue = commA2 + commBT, noorDue = commBN;
  check(payouts['Tariq Al Amri'].endsWith(`${rial(tariqDue)}.`) && payouts['Noor Al Kindi'].endsWith(`${rial(noorDue)}.`), `paid Tariq ${rial(tariqDue)}, Noor ${rial(noorDue)}`);
  await office.getByRole('tab', { name: 'Payouts' }).click();
  await shot(office, 'commission-payouts', { full: true });

  // ---------------------------------------------------------------- rework clawback
  step('a warranty claim on job B within 30 days takes back half of each technician’s commission');
  await api('POST', `/api/v1/repairs/${B.id}/warranty-claim`, { problem: 'Drains again', passcodeMode: 'none', termsName: 'Maryam Al Hinai', termsSignature: PNG }, owner);
  b = await accrualsOf(B);
  const backT = -pct(commBT, 50), backN = -pct(commBN, 50);
  check(bz(b.find((x) => x.kind === 'clawback' && x.technicianId === ids.tariq).amount) === backT && bz(b.find((x) => x.kind === 'clawback' && x.technicianId === ids.noor).amount) === backN,
    `clawback: Tariq ${rial(backT)}, Noor ${rial(backN)}`);

  step('Tariq’s statement and the reports in Arabic');
  await office.getByRole('tab', { name: 'Statement' }).click();
  await office.getByLabel('Technician').selectOption({ label: 'Tariq Al Amri' });
  await office.getByText('Payable at the end').waitFor();
  const closing = office.getByText('Payable at the end').locator('xpath=..');
  await closing.getByText(rial(backT)).waitFor();
  check(true, `Tariq’s statement: payable at the end ${rial(backT)} (the clawback waits for his next payout)`);
  await office.getByRole('button', { name: 'العربية' }).click();
  await office.getByRole('heading', { name: 'العمولات' }).waitFor();
  await office.getByText('المستحق في النهاية').waitFor();
  await shot(office, 'commission-statement-ar', { full: true });
  await office.getByRole('tab', { name: 'الأداء' }).click();
  await shot(office, 'technician-performance-ar', { full: true });
  await nav(office, 'قواعد العمولة');
  await office.getByText('طارق: 10% من الأجور').waitFor();
  await shot(office, 'commission-rules-ar', { full: true });
  await office.getByRole('button', { name: 'English' }).click();

  // ---------------------------------------------------------------- accounts
  step('accounts against arithmetic done here');
  const accrued = commA1 - commA1 + commA2 + commBT + commBN + backT + backN;
  const paid = tariqDue + noorDue;
  const SALE_A1 = bz(96), SALE_A2 = bz(93), SALE_B = bz(31);
  const expected = {
    '6300': accrued,                                      // commission expense: what was accrued, reversals and clawbacks included
    '2400': -(accrued - paid),                            // commissions payable: accrued less paid (the clawbacks wait for the next payout)
    '1200': -tariqDue,                                    // Tariq paid by bank
    '2410': -noorDue,                                     // Noor through payroll clearing
    '5210': bz(4.5), '2110': -bz(4.5),                    // outside work: its own cost, owed to the supplier
    '5200': bz(8) * 2 + bz(30),                           // two batteries and the board
    '1460': 0,                                            // nothing left in progress
    '1430': -(bz(8) * 2 + bz(30)) + bz(8),                // the opening stock is before the snapshot; one battery arrived after it
    '1100': SALE_A1 + SALE_B - SALE_A1 + SALE_A2,         // the drawer: both collections, the refund and the corrected invoice
    '4900': net(bz(15)) + net(bz(21)) + net(bz(60)),      // the credited invoice A to sales returns
    '4200': -(net(bz(15)) + net(bz(10)) + net(bz(12))),   // labour on invoices A, B and corrected A
    '4210': -(net(bz(21)) + net(bz(60)) + net(bz(21)) + net(bz(21)) + net(bz(60))),
  };
  for (const [code, value] of Object.entries(expected)) check(moved(code) === value, `account ${code} moved ${rial(moved(code))}, expected ${rial(value)}`);
  check(sql(`SELECT count(*) FROM (SELECT job_id, technician_id, kind, kind_version FROM comm.accrual WHERE kind <> 'reversal' GROUP BY 1,2,3,4 HAVING count(*) > 1) x`) === '0', 'no commission was generated twice');
  check(sql(`SELECT ok FROM audit.verify_chain()`) === 't', 'the audit chain verifies');
  const books = await api('GET', '/api/v1/inventory/check', undefined, owner);
  check(books.ok, 'stock, ledger and accounts agree');

  if (pageErrors.length) throw new Error('page errors: ' + pageErrors.join(' | '));
  console.log('PHASE 9 WALK-THROUGH PASSED');
} catch (e) {
  console.error('FAILED:', e);
  await shot(till, 'failure-till', { full: true }).catch(() => undefined);
  await shot(office, 'failure-office', { full: true }).catch(() => undefined);
  for (const p of [office, till]) console.error('  page alerts:', (await p.locator('.alert').allTextContents().catch(() => [])).join(' | '));
  process.exitCode = 1;
} finally {
  await browser.close();
}
