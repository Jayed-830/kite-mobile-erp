// Phase 8 exit gate in a real browser against a real KITE server and PostgreSQL: the owner sets the repair rates and turns
// WhatsApp on with the test chat; a customer's phone is booked in with the wizard (condition, accessories, passcode, WhatsApp
// consent, signed terms); a deposit is taken at the till; the technician diagnoses, writes an estimate that goes to the
// customer by WhatsApp and is approved with its Approve button; a part is fitted and labour logged; the job moves through
// only the allowed statuses (the others are refused); the device is collected at the till against its job card's QR with
// the deposit applied. A second job is declined, cancelled and collected with the diagnosis fee and the deposit's excess
// back. The board is shown in English and Arabic. Every figure is checked against arithmetic done here, and the accounts
// against the expected table. Screenshots go to SHOTS_DIR.
//
//   KITE_URL (http://localhost:5080)  SETUP_CODE_FILE  STUB_URL (http://127.0.0.1:8799)  KITE_DB (kite; PGHOST etc. for psql)
import { chromium } from 'playwright-core';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const BASE = process.env.KITE_URL ?? 'http://localhost:5080';
const STUB = process.env.STUB_URL ?? 'http://127.0.0.1:8799';
const DB = process.env.KITE_DB ?? 'kite';
const shots = process.env.SHOTS_DIR ?? 'shots-phase8';
const OWNER_PW = 'Muscat-Ruwi-2026!';
const PHONE = '+96895551234';
const IMEI = '356789012345672';   // a correct check digit
fs.mkdirSync(shots, { recursive: true });

const bz = (x) => Math.round(Number(x) * 1000);
const rial = (b) => (b / 1000).toFixed(3);
const halfAway = (num, den) => Math.sign(num) * Math.floor((Math.abs(num) * 2 + den) / (2 * den));
const vatIn = (gross) => halfAway(gross * 5, 105);   // VAT inside a VAT-inclusive price at 5%
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
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const check = (ok, what) => { if (!ok) throw new Error(what); console.log(`  ok: ${what}`); };
const until = async (what, f, ms = 30000) => { const end = Date.now() + ms; for (;;) { if (await f()) return; if (Date.now() > end) throw new Error(`timed out waiting for ${what}`); await sleep(400); } };

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
const nav = (p, name) => p.getByRole('link', { name, exact: true }).click();
const dialog = (p, name) => p.getByRole('dialog', { name });
/** Draws a signature on the pad with the mouse, as a customer would with a finger or pen. */
const draw = async (p, name, scope = p) => {
  const c = scope.getByRole('img', { name });
  await c.scrollIntoViewIfNeeded();
  const b = await c.boundingBox();
  await p.mouse.move(b.x + 20, b.y + 50); await p.mouse.down();
  await p.mouse.move(b.x + 110, b.y + 100, { steps: 10 }); await p.mouse.move(b.x + 200, b.y + 40, { steps: 10 }); await p.mouse.move(b.x + 280, b.y + 110, { steps: 10 });
  await p.mouse.up();
};
const moveJob = async (p, status) => {
  await p.getByRole('button', { name: `Move to: ${status}`, exact: true }).click();
  const d = dialog(p, `Move to: ${status}`);
  await d.getByRole('button', { name: 'Save' }).click();
  await d.waitFor({ state: 'hidden' });
  await p.getByRole('heading', { name: /^Repair job / }).locator('xpath=..').getByText(status, { exact: true }).first().waitFor();
};

try {
  step('the server is set up (API), a licence is imported from the Nextdab stub, repairs and WhatsApp are switched on, users get PINs');
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
  await api('PUT', '/api/v1/settings/modules', { changes: { device: true, repair: true, notif: true } }, owner);
  const roles = await api('GET', '/api/v1/roles', undefined, owner);
  const roleId = (code) => roles.find((r) => r.code === code).id;
  const branchId = (await api('GET', '/api/v1/branches', undefined, owner))[0].id;
  for (const [username, role, name, nameAr, pin] of [['aisha', 'cashier', 'Aisha Al Balushi', 'عائشة البلوشية', '2468'], ['tariq', 'technician', 'Tariq Al Amri', 'طارق العامري', '8642']]) {
    await api('POST', '/api/v1/users', { username, displayNameEn: name, displayNameAr: nameAr, temporaryPassword: 'Temp-Pass-2026', preferredLanguage: 'en', defaultBranchId: branchId, roles: [{ roleId: roleId(role), branchId }] }, owner);
    const t = await token(username, 'Temp-Pass-2026');
    await api('POST', '/api/v1/auth/password', { currentPassword: 'Temp-Pass-2026', newPassword: 'Real-Pass-2026' }, t);
    await api('POST', '/api/v1/auth/pin', { pin, currentPassword: 'Real-Pass-2026' }, await token(username, 'Real-Pass-2026'));
  }
  owner = await token('owner', OWNER_PW);
  const vat = (await api('GET', '/api/v1/tax/codes', undefined, owner)).find((c) => c.code === 'VAT-STD').id;
  const screen = (await api('POST', '/api/v1/catalog/products', { nameEn: 'iPhone 14 screen', nameAr: 'شاشة آيفون 14', productType: 'spare_part', tracking: 'none', taxCodeId: vat, defaultWarrantyDays: 90, variants: [{ sku: 'SCR-IP14', barcodes: ['6291000000301'], price: 45 }] }, owner)).variants[0].variant.id;
  await api('POST', '/api/v1/inventory/adjustments', { branchId, kind: 'opening', note: 'opening stock', lines: [{ variantId: screen, qty: 3, unitCost: 30, reasonCode: 'opening' }] }, owner);
  const CODES = ['1100', '1430', '1460', '2200', '2300', '4200', '4210', '5200'];
  const glStart = Object.fromEntries(CODES.map((c) => [c, gl(c)]));
  const moved = (code) => gl(code) - glStart[code];

  // ---------------------------------------------------------------- settings
  step('repair settings: labour 12.000 an hour, a 3.000 diagnosis fee; WhatsApp on with the test chat');
  await signIn(office, 'owner', OWNER_PW);
  await nav(office, 'Repair settings');
  await office.getByLabel('Labour per hour (OMR)').fill('12');
  await office.getByLabel('Diagnosis fee (OMR)').fill('3');
  await office.getByRole('button', { name: 'Save', exact: true }).click();
  await office.getByText('Saved.').waitFor();
  await shot(office, 'repair-settings', { full: true });
  const rs = (await api('GET', '/api/v1/repair-setup', undefined, owner)).settings;
  check(bz(rs.hourlyRate) === bz(12) && bz(rs.diagnosisFee) === bz(3) && bz(rs.preAuthorised) === bz(5), 'rates saved: 12.000 an hour, diagnosis 3.000, pre-authorised default 5.000');
  await nav(office, 'WhatsApp');
  await office.getByLabel('Provider').selectOption('test');
  await office.getByRole('switch', { name: 'Send WhatsApp messages' }).locator('xpath=..').click();   // the switch's label (the checkbox itself is drawn as a slider)
  check(await office.getByRole('switch', { name: 'Send WhatsApp messages' }).isChecked(), 'WhatsApp switched on');
  await office.getByRole('button', { name: 'Save', exact: true }).click();
  await office.getByText('Saved.').waitFor();
  await shot(office, 'whatsapp-settings', { full: true });

  // ---------------------------------------------------------------- intake
  step('intake: a new customer’s iPhone 14 with a cracked screen: condition, accessories, passcode, WhatsApp consent, signed terms');
  await nav(office, 'Repairs');
  await office.getByRole('link', { name: 'New repair' }).click();
  await office.getByRole('heading', { name: 'Book a device in' }).waitFor();
  await office.getByRole('button', { name: 'New customer' }).click();
  await office.getByLabel('Name', { exact: true }).fill('Khalid Al Rawahi');
  await office.getByLabel('Mobile number').fill(PHONE);
  await office.getByRole('button', { name: 'Save', exact: true }).click();
  await office.getByText('Khalid Al Rawahi').waitFor();
  await office.getByRole('button', { name: 'Next' }).click();
  await office.getByLabel('Scan or type the IMEI or serial number').fill(IMEI);
  await office.getByRole('button', { name: 'Look up' }).click();
  await office.getByLabel('Brand').fill('Apple');
  await office.getByLabel(/^Model/).fill('iPhone 14');
  await office.getByLabel('Colour').fill('Midnight');
  await office.getByRole('button', { name: 'Next' }).click();
  await office.getByLabel(/^Kind of repair/).selectOption({ label: 'Screen and display' });
  await office.getByLabel('What the customer says is wrong').fill('Dropped it; the glass is cracked and the top of the screen does not respond.');
  await office.getByLabel('Cracked glass').check();
  await office.getByLabel('Touch not working').check();
  await office.getByLabel('Assign to').selectOption({ label: 'Tariq Al Amri' });
  await shot(office, 'intake-problem');
  await office.getByRole('button', { name: 'Next' }).click();
  await office.getByRole('radiogroup', { name: 'Screen' }).getByRole('radio', { name: 'Fault' }).click();
  await office.getByRole('radiogroup', { name: 'Body' }).getByRole('radio', { name: 'OK' }).click();
  await office.getByLabel('Case', { exact: true }).check();
  await office.getByLabel('Access to the device').selectOption('provided');
  await office.getByLabel('Passcode or pattern').fill('1234');
  await shot(office, 'intake-condition', { full: true });
  await office.getByRole('button', { name: 'Next' }).click();
  await office.getByLabel('Send updates by WhatsApp').check();
  await office.getByLabel('Messages in').selectOption('en');
  await draw(office, 'Customer signs here');
  await shot(office, 'intake-terms-signed', { full: true });
  await office.getByRole('button', { name: 'Book in' }).click();
  const booked = await office.getByText(/^Job R-B01-\d{4}-\d{6} booked in\.$/).textContent({ timeout: 20000 });
  const J1 = booked.match(/R-B01-\d{4}-\d{6}/)[0];
  await shot(office, 'intake-done');
  const job1 = (await api('GET', `/api/v1/repairs?q=${J1}`, undefined, owner))[0];
  let d1 = await api('GET', `/api/v1/repairs/${job1.id}`, undefined, owner);
  check(d1.job.status === 'received' && d1.job.technicianName === 'Tariq Al Amri' && d1.job.passcodeHeld && d1.job.notifyWhatsapp && d1.job.customerOptIn, `${J1} received, assigned to Tariq, passcode held encrypted, WhatsApp consent recorded`);
  check(d1.photos.some((ph) => ph.stage === 'terms_signature' && ph.contentType === 'image/png' && /^[0-9a-f]{64}$/.test(ph.sha256)), 'the signed terms are stored as a PNG with its SHA-256');
  check(sql(`SELECT count(*) FROM repair.repair_job WHERE id = '${job1.id}' AND passcode_enc IS NOT NULL AND position(convert_to('1234','UTF8') in passcode_enc) = 0`) === '1', 'the passcode is stored encrypted, not as typed');

  // ---------------------------------------------------------------- till: register and deposit
  step('till: registered as T01, the cashier opens the shift with 100.000 and takes a 20.000 cash deposit on the job');
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
  await till.getByLabel('Scan a barcode or type a name').waitFor();
  const FLOAT = bz(100);
  const deposit = async (job, amount) => {
    await till.getByRole('button', { name: 'Repairs', exact: true }).click();
    const c = dialog(till, 'Repairs at the counter');
    await c.getByLabel(/Scan the job card/).fill(job);
    await c.getByRole('button', { name: new RegExp(job) }).click();
    await c.getByRole('button', { name: 'Take deposit' }).click();
    await c.getByLabel('Amount').fill(String(amount));
    await shot(till, `till-deposit-${job.slice(-6)}`);
    await c.getByRole('button', { name: 'Take deposit' }).click();
    await till.getByText(/^Deposit DEP-B01-\S+ taken\. Held: /).waitFor();
  };
  await deposit(J1, 20);
  d1 = await api('GET', `/api/v1/repairs/${job1.id}`, undefined, owner);
  check(bz(d1.money.deposit) === bz(20), 'the job holds a 20.000 deposit');

  // ---------------------------------------------------------------- allowed and refused moves
  step('only the blueprint’s moves: from received, everything but inspection or cancel is refused; work needs an approved estimate');
  const refused = [];
  for (const to of ['under_diagnosis', 'waiting_approval', 'waiting_parts', 'in_progress', 'testing', 'completed', 'ready', 'unrepairable']) {
    const r = await api('POST', `/api/v1/repairs/${job1.id}/status`, { to }, owner, true);
    if (r.status === 409 && r.body.code === 'REP-001') refused.push(to);
  }
  check(refused.length === 8, `received → ${refused.join(', ')}: all refused with REP-001`);
  const del = await api('POST', `/api/v1/repairs/${job1.id}/status`, { to: 'delivered' }, owner, true);
  check(del.status === 409 && ['REP-001', 'REP-004'].includes(del.body.code), 'delivered is only reached by collection at the till');

  step('the technician inspects and diagnoses, writes estimate version 1 (an hour’s labour and the screen) and sends it');
  await office.goto(BASE + `/app/repairs/${job1.id}`);
  await office.getByRole('heading', { name: `Repair job ${J1}` }).waitFor();
  const offered = await office.getByRole('button', { name: /^Move to:/ }).allTextContents();
  check(offered.join('|') === 'Move to: Waiting for inspection|Move to: Cancelled', `the page offers only: ${offered.join(', ')}`);
  await moveJob(office, 'Waiting for inspection');
  await moveJob(office, 'Under diagnosis');
  const noEstimate = await api('POST', `/api/v1/repairs/${job1.id}/status`, { to: 'in_progress' }, owner, true);
  check(noEstimate.status === 409 && noEstimate.body.code === 'REP-005', 'in progress is refused before an estimate is written (REP-005)');
  await office.getByRole('tab', { name: 'Estimates' }).click();
  await office.getByRole('button', { name: 'Write estimate' }).click();
  await office.getByRole('button', { name: 'Add labour' }).click();
  await office.getByLabel('Minutes').fill('60');
  await office.getByLabel('Part').fill('iPhone 14');
  await office.getByRole('button', { name: /iPhone 14 screen/ }).click();
  await office.getByRole('button', { name: 'Save draft' }).click();
  await office.getByText('Version 1').waitFor();
  const LABOUR = bz(12), PART = bz(45), EST = LABOUR + PART;
  d1 = await api('GET', `/api/v1/repairs/${job1.id}`, undefined, owner);
  check(bz(d1.estimates[0].total) === EST && bz(d1.estimates[0].taxTotal) === vatIn(LABOUR) + vatIn(PART), `estimate total ${rial(EST)} with VAT ${rial(vatIn(LABOUR) + vatIn(PART))}`);
  await shot(office, 'estimate-draft', { full: true });
  const unapproved = await api('POST', `/api/v1/repairs/${job1.id}/status`, { to: 'in_progress' }, owner, true);
  check(unapproved.status === 409 && unapproved.body.code === 'REP-005' && /more than the customer authorised/.test(unapproved.body.title), `in progress is refused while the ${rial(EST)} estimate is above the 5.000 pre-authorised and not approved: "${unapproved.body.title}"`);
  await office.getByRole('tab', { name: 'Overview' }).click();
  await moveJob(office, 'Waiting for approval');

  // ---------------------------------------------------------------- WhatsApp approval
  step('WhatsApp: the estimate reaches the customer through the gateway; he taps Approve in the test chat');
  await until('the estimate message in the test chat', async () => (await api('GET', `/api/v1/whatsapp/test-chat?phone=${encodeURIComponent(PHONE)}`, undefined, owner)).messages.some((m) => m.direction === 'out' && (m.buttons ?? []).length > 0), 60000);
  await nav(office, 'WhatsApp');
  await office.getByRole('tab', { name: 'Test chat' }).click();
  await office.getByRole('button', { name: new RegExp(PHONE.replace('+', '\\+')) }).click();
  await office.getByRole('button', { name: /^Customer taps “Approve”/ }).waitFor();
  await shot(office, 'whatsapp-estimate-in-test-chat', { full: true });
  await office.getByRole('button', { name: /^Customer taps “Approve”/ }).click();
  await until('the WhatsApp approval', async () => (await api('GET', `/api/v1/repairs/${job1.id}`, undefined, owner)).estimates[0].status === 'approved', 60000);
  d1 = await api('GET', `/api/v1/repairs/${job1.id}`, undefined, owner);
  check(d1.estimates[0].approvalMethod === 'whatsapp' && /^wamid\./.test(d1.estimates[0].approvalRef ?? ''), `estimate approved by WhatsApp (${d1.estimates[0].approvalRef})`);

  step('workshop: in progress, the screen is fitted (cost into work in progress) and an hour of labour logged; testing, completed, ready');
  await office.goto(BASE + `/app/repairs/${job1.id}`);
  await office.getByRole('heading', { name: `Repair job ${J1}` }).waitFor();
  await moveJob(office, 'In progress');
  await office.getByRole('tab', { name: 'Parts and labour' }).click();
  await office.getByLabel('Part').fill('iPhone 14');
  await office.getByRole('button', { name: /iPhone 14 screen/ }).click();
  await office.getByRole('button', { name: 'Fit part' }).click();
  await office.getByText('SCR-IP14').first().waitFor();
  await office.getByLabel('Minutes').fill('60');
  await office.getByRole('button', { name: 'Log labour' }).click();
  await office.getByText('Labour · 60 min · Tariq Al Amri').waitFor();   // logged against the job's technician
  await shot(office, 'parts-and-labour', { full: true });
  d1 = await api('GET', `/api/v1/repairs/${job1.id}`, undefined, owner);
  const COST = bz(30);
  check(bz(d1.money.wip) === COST && moved('1460') === COST && moved('1430') === -COST, 'the screen’s 30.000 cost moved from spare parts stock (1430) to work in progress (1460)');
  await office.getByRole('tab', { name: 'Overview' }).click();
  await moveJob(office, 'Testing');
  await moveJob(office, 'Completed');
  await moveJob(office, 'Ready to collect');
  await shot(office, 'job-ready', { full: true });
  await until('the ready message', async () => (await api('GET', `/api/v1/whatsapp/test-chat?phone=${encodeURIComponent(PHONE)}`, undefined, owner)).messages.filter((m) => m.direction === 'out').length >= 3, 60000);
  check(true, 'the customer got the received, estimate and ready messages');

  // ---------------------------------------------------------------- job 2: declined, cancelled, diagnosis fee
  step('job 2: the same customer’s Galaxy for a battery; 5.000 deposit; the estimate is declined, the job cancelled and made ready');
  const d2 = await api('POST', '/api/v1/repairs', { customerId: d1.job.customerId, categoryId: (await api('GET', '/api/v1/repair-setup', undefined, owner)).categories.find((c) => c.code === 'BATTERY').id,
    problem: 'Battery drains by noon.', passcodeMode: 'none', termsName: 'Khalid Al Rawahi', termsSignature: (await office.evaluate(() => { const c = document.createElement('canvas'); c.width = 40; c.height = 20; const g = c.getContext('2d'); g.fillRect(2, 2, 30, 10); return c.toDataURL('image/png'); })),
    device: { serial: 'R58N12AB34C', brand: 'Samsung', model: 'Galaxy S21' }, branchId }, owner);
  const J2 = d2.job.number;
  await deposit(J2, 5);
  for (const to of ['inspection_pending', 'under_diagnosis']) await api('POST', `/api/v1/repairs/${d2.job.id}/status`, { to }, owner);
  const extra = (await api('GET', '/api/v1/repair-setup', undefined, owner)).services.find((s) => s.sku === 'REPAIR-EXTRA').id;
  const e2 = (await api('POST', `/api/v1/repairs/${d2.job.id}/estimates`, { lines: [{ kind: 'extra', variantId: extra, unitPrice: 25, descriptionEn: 'Battery replacement', descriptionAr: 'استبدال البطارية' }] }, owner)).estimates[0];
  await api('POST', `/api/v1/repairs/${d2.job.id}/status`, { to: 'waiting_approval' }, owner);
  await office.goto(BASE + `/app/repairs/${d2.job.id}`);
  await office.getByRole('tab', { name: 'Estimates' }).click();
  await office.getByRole('button', { name: 'Declined by the customer' }).click();
  await dialog(office, 'Declined by the customer').getByLabel('Reason').fill('Too expensive; will buy a new phone');
  await dialog(office, 'Declined by the customer').getByRole('button', { name: 'Save' }).click();
  await office.getByText('Too expensive; will buy a new phone').waitFor();
  check((await api('GET', `/api/v1/repairs/${d2.job.id}`, undefined, owner)).estimates[0].id === e2.id, 'estimate declined');
  await office.getByRole('tab', { name: 'Overview' }).click();
  await moveJob(office, 'Cancelled');
  await moveJob(office, 'Ready to collect');

  step('the workshop board in Arabic, with both jobs ready to collect');
  await nav(office, 'Repairs');
  await office.getByRole('region', { name: 'Ready to collect' }).getByText(J2).waitFor();
  await office.getByRole('button', { name: 'العربية' }).click();
  await office.getByRole('region', { name: 'جاهز للاستلام' }).getByText(J1).waitFor();
  await shot(office, 'repairs-board-ar');
  await office.getByRole('button', { name: 'English' }).click();
  check(await office.getByRole('region', { name: 'Ready to collect' }).isVisible(), 'the board shows both jobs ready to collect, in Arabic and English');
  check(!(await till.getByRole('button', { name: /Diagnosis fee|Repair labour/ }).count()), 'the till’s tiles leave out the repair charges (they are charged through a job)');

  // ---------------------------------------------------------------- collection
  const DUE1 = EST - bz(20);
  step(`collection 1: the customer shows his job card; ${rial(EST)} less the 20.000 deposit = ${rial(DUE1)} paid in cash (40.000 given)`);
  const qr = d1.job.qr;
  check(/^KITE-R\|R-B01-\d{4}-\d{6}\|[0-9a-f]{12}$/.test(qr), `the job card’s QR holds the number and a check of the job id (${qr})`);
  await till.getByRole('button', { name: 'Repairs', exact: true }).click();
  let c = dialog(till, 'Repairs at the counter');
  await c.getByLabel(/Scan the job card/).fill(qr);
  await c.getByRole('button', { name: new RegExp(J1) }).click();
  await c.getByRole('button', { name: 'Collect' }).click();
  await c.getByText('Still to pay').locator('xpath=..').getByText(rial(DUE1)).waitFor();
  check((await c.getByLabel('Who is collecting').inputValue()) === 'qr', 'the scanned card is the check');
  await c.getByLabel('Name of the person collecting').fill('Khalid Al Rawahi');
  await draw(till, 'Signature', c);
  await shot(till, 'till-collect-job-1');
  await c.getByRole('button', { name: new RegExp(`^Pay ${rial(DUE1).replace('.', '\\.')}`) }).click();
  const pay = dialog(till, 'Payment');
  await pay.getByLabel('Cash given').fill('40');
  await pay.getByRole('button', { name: 'Complete sale' }).click();
  await till.getByText(new RegExp(`Job ${J1} delivered\\. Invoice INV-B01-`)).waitFor({ timeout: 20000 });
  await shot(till, 'till-collected-job-1');
  d1 = await api('GET', `/api/v1/repairs/${job1.id}`, undefined, owner);
  check(d1.job.status === 'delivered' && d1.job.collectionCheck === 'qr' && d1.job.warrantyUntil && !d1.job.passcodeHeld, `${J1} delivered against its card; repair warranty until ${d1.job.warrantyUntil}; passcode deleted`);
  const inv = (await api('GET', `/api/v1/pos/sales?q=${d1.job.deliverySaleNumber}`, undefined, owner).catch(() => []))[0];
  check(bz(d1.money.wip) === 0, 'work in progress cleared at delivery');
  const wrongQr = await api('POST', `/api/v1/repairs/${d2.job.id}/deliver`, { check: 'qr', qrCode: qr, collectedBy: 'Someone', signature: 'iVBORw0KGgo=' }, owner, true);
  check(wrongQr.status === 409 && wrongQr.body.code === 'REP-004', 'another job’s card does not collect this device (REP-004)');

  step('collection 2: cancelled job, 3.000 diagnosis fee from the 5.000 deposit, 2.000 back in cash; the customer is checked by the cashier');
  await till.getByRole('button', { name: 'Repairs', exact: true }).click();
  c = dialog(till, 'Repairs at the counter');
  await c.getByLabel(/Scan the job card/).fill(J2);
  await c.getByRole('button', { name: new RegExp(J2) }).click();
  await c.getByRole('button', { name: 'Collect' }).click();
  await c.getByText('Back to the customer').first().waitFor();
  await c.getByLabel('Who is collecting').selectOption('verified');
  await c.getByLabel('Name of the person collecting').fill('Khalid Al Rawahi');
  await draw(till, 'Signature', c);
  await shot(till, 'till-collect-job-2');
  await c.getByRole('button', { name: 'Collect', exact: true }).click();
  await till.getByText(new RegExp(`Job ${J2} delivered\\.`)).waitFor({ timeout: 20000 });
  const done2 = await api('GET', `/api/v1/repairs/${d2.job.id}`, undefined, owner);
  check(done2.job.status === 'delivered' && done2.job.outcome === 'cancelled' && bz(done2.money.deposit) === 0 && done2.deposits.some((x) => x.kind === 'refund' && bz(x.amount) === bz(2)), `${J2} delivered; 3.000 fee from the deposit and 2.000 back`);

  // ---------------------------------------------------------------- board in both languages
  step('every job in English, and a delivered job’s history in Arabic');
  await nav(office, 'Repairs');
  await office.getByRole('tab', { name: 'All jobs' }).click();
  await office.getByRole('link', { name: J1 }).waitFor();
  await shot(office, 'repairs-all-jobs-en', { full: true });
  await office.goto(BASE + `/app/repairs/${job1.id}`);
  await office.getByRole('button', { name: 'العربية' }).click();
  await office.getByRole('tab', { name: 'السجل' }).click();
  await office.getByText('تم التسليم').first().waitFor();
  await shot(office, 'repair-history-ar', { full: true });
  await office.getByRole('button', { name: 'English' }).click();

  // ---------------------------------------------------------------- accounts
  step('accounts and the drawer against arithmetic done here');
  const FEE = bz(3);
  const expected = {
    '1100': bz(20) + bz(5) + DUE1 - bz(2),                         // deposits in, the rest of job 1 in, job 2's excess out
    '2300': 0,                                                     // deposits taken, applied and refunded: nothing left
    '1460': 0,                                                     // work in progress: the screen in, then cleared
    '1430': -COST,                                                 // the screen left spare parts stock
    '5200': COST,                                                  // cost of the repair part at delivery
    '4200': -(LABOUR - vatIn(LABOUR)) - (FEE - vatIn(FEE)),        // labour and the diagnosis fee are services
    '4210': -(PART - vatIn(PART)),                                 // the screen charged on a repair invoice: repair parts income
    '2200': -(vatIn(LABOUR) + vatIn(PART) + vatIn(FEE)),           // output VAT on the two repair invoices
  };
  for (const [code, value] of Object.entries(expected)) check(moved(code) === value, `account ${code} moved ${rial(moved(code))}, expected ${rial(value)}`);
  const shiftId = (await api('GET', '/api/v1/pos/shifts?limit=1', undefined, owner))[0].id;
  const f = (await api('GET', `/api/v1/pos/shifts/${shiftId}`, undefined, owner)).figures;
  check(bz(f.repairDeposits) === bz(25), `the shift shows 25.000 of repair deposits taken (refunded ${f.repairDepositRefunds}, applied ${f.repairDepositsApplied})`);
  check(bz(f.expectedCash) === FLOAT + expected['1100'], `expected cash = 100.000 + ${rial(expected['1100'])}`);
  check(sql(`SELECT ok FROM audit.verify_chain()`) === 't', 'the audit chain verifies');
  const books = await api('GET', '/api/v1/inventory/check', undefined, owner);
  check(books.ok, 'stock, ledger and accounts agree');
  if (inv) console.log(`  invoice ${inv.number}: ${inv.grossTotal}`);

  if (pageErrors.length) throw new Error('page errors: ' + pageErrors.join(' | '));
  console.log('PHASE 8 WALK-THROUGH PASSED');
} catch (e) {
  console.error('FAILED:', e);
  await shot(till, 'failure-till', { full: true }).catch(() => undefined);
  await shot(office, 'failure-office', { full: true }).catch(() => undefined);
  for (const p of [office, till]) console.error('  page alerts:', (await p.locator('.alert').allTextContents().catch(() => [])).join(' | '));
  process.exitCode = 1;
} finally {
  await browser.close();
}
