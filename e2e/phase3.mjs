// Phase 3 exit gate in a real browser against two real KITE servers (head office + branch) and the Nextdab stub:
//   licence: unlicensed limits -> licence file imported -> Nextdab switch-off and back over check-in -> ended licence
//            enforced with Nextdab offline
//   branch:  registration token -> branch joins (security code compared) -> heartbeats -> revoked by head office
//   backups: full backup, base backup, restore check, recovery key (the workflow then restores the file by hand)
// Screenshots go to SHOTS_DIR. Exits non-zero on the first failed step.
//
//   MAIN_URL / BRANCH_URL            the two servers (http://localhost:5080 / 5081)
//   MAIN_SETUP_CODE_FILE / BRANCH_SETUP_CODE_FILE
//   STUB_URL                         the Nextdab stub (http://127.0.0.1:8799)
//   RECOVERY_KEY_FILE                where the recovery key shown in the browser is saved for the manual restore
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const MAIN = process.env.MAIN_URL ?? 'http://localhost:5080';
const BRANCH = process.env.BRANCH_URL ?? 'http://localhost:5081';
const STUB = process.env.STUB_URL ?? 'http://127.0.0.1:8799';
const shots = process.env.SHOTS_DIR ?? 'shots-phase3';
const OWNER_PW = 'Muscat-Ruwi-2026!';
const code = (f) => fs.readFileSync(f, 'utf8').trim();
fs.mkdirSync(shots, { recursive: true });

const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH ?? '/usr/bin/google-chrome', args: ['--no-sandbox'] });
const ctxMain = await browser.newContext({ viewport: { width: 1366, height: 860 } });
const ctxBranch = await browser.newContext({ viewport: { width: 1366, height: 860 } });
const main = await ctxMain.newPage();
const branch = await ctxBranch.newPage();
const pageErrors = [];
for (const p of [main, branch]) p.on('pageerror', (e) => pageErrors.push(e.message));
let n = 0;
const shot = async (p, name) => { n += 1; const f = path.join(shots, `${String(n).padStart(2, '0')}-${name}.jpg`); await p.waitForTimeout(300); await p.screenshot({ path: f, type: 'jpeg', quality: 72, fullPage: true }); console.log(`  screenshot ${f}`); };
const step = (s) => console.log(`- ${s}`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const stub = async (p, body) => { const r = await fetch(STUB + p, body === undefined ? undefined : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }); if (!r.ok) throw new Error(`stub ${p}: ${r.status}`); return r.json(); };
const day = (offsetDays) => new Date(Date.now() + offsetDays * 864e5).toISOString().slice(0, 10);
const signIn = async (p, base) => {
  await p.goto(base + '/login');
  await p.locator('input[autocomplete="username"]').fill('owner');
  await p.locator('input[autocomplete="current-password"]').fill(OWNER_PW);
  await p.locator('form button[type="submit"]').click();
  await p.getByRole('heading', { name: /^(Welcome, |مرحبًا، )/ }).waitFor({ timeout: 30000 });
};
const nav = (p, name) => p.getByRole('link', { name, exact: true }).click();
/** Reloads until the check passes (the servers update on their own schedule). */
async function until(p, what, check, timeoutMs = 90000) {
  const end = Date.now() + timeoutMs;
  for (;;) {
    if (await check().catch(() => false)) return;
    if (Date.now() > end) throw new Error(`timed out waiting for: ${what}`);
    await sleep(2500);
    await p.reload();
  }
}
const bannerText = async (p) => ((await p.getByTestId('licence-banner').count()) ? await p.getByTestId('licence-banner').innerText() : '');
async function importLicence(p, licenceOpts) {
  const { file, licence } = await stub('/stub/licence', licenceOpts);
  const f = path.join(os.tmpdir(), `${licence.id}.kmelic`);
  fs.writeFileSync(f, file);
  await nav(p, 'Licence');
  await p.getByLabel('Licence file').setInputFiles(f);
  await p.getByText(/^Licence imported:/).waitFor({ timeout: 20000 });
  return licence;
}

try {
  step('head office server is set up (API) and the owner signs in');
  const setup = await fetch(MAIN + '/api/v1/setup', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      setupCode: code(process.env.MAIN_SETUP_CODE_FILE), serverRole: 'main',
      business: { legalNameEn: 'Al Noor Mobiles LLC', legalNameAr: 'شركة النور للهواتف', vatNumber: 'OM1100223344', fiscalYearStartMonth: 1, pricesIncludeTax: true },
      headOffice: { code: 'B01', nameEn: 'Muscat - Ruwi', nameAr: 'مسقط - روي' },
      owner: { username: 'owner', displayNameEn: 'Salim Al Harthy', displayNameAr: 'سالم الحارثي', password: OWNER_PW, preferredLanguage: 'en' },
    }),
  });
  if (!setup.ok) throw new Error(`setup failed: ${setup.status} ${await setup.text()}`);
  await signIn(main, MAIN);

  step('without a licence: banner, and only one branch is allowed');
  if (!(await bannerText(main)).includes('KITE has no licence yet')) throw new Error('no "no licence" banner');
  await nav(main, 'Branches and servers');
  await main.getByRole('button', { name: 'Add branch' }).click();
  let dlg = main.getByRole('dialog');
  await dlg.getByLabel(/^Code/).fill('B02');
  await dlg.getByLabel(/Branch name \(English\)/).fill('Sohar');
  await dlg.getByLabel(/Branch name \(Arabic\)/).fill('صحار');
  await dlg.getByRole('button', { name: 'Save' }).click();
  await dlg.getByText('LIC-004').waitFor({ timeout: 15000 });
  await shot(main, 'unlicensed-branch-limit');
  await dlg.getByRole('button', { name: 'Cancel' }).click();

  step('a licence file (Nextdab signing method) is imported: 3 branches, 5 terminals, one year');
  await nav(main, 'Licence');
  const mid = (await main.getByTestId('installation-id').innerText()).trim();
  await importLicence(main, { mid, kind: 'subscription', validUntil: day(365), branches: 3, terminals: 5, graceDays: 30 });
  await until(main, 'licence banner gone', async () => (await bannerText(main)) === '');
  await main.getByText('Licensed', { exact: true }).first().waitFor();
  await shot(main, 'licence-imported');

  step('Nextdab switches KITE off over the check-in, with a message, then on again');
  await stub('/stub/command', { mid, type: 'message', data: { title: 'Renewal', text: 'Your renewal is due next month.', title_ar: 'التجديد', text_ar: 'موعد التجديد الشهر القادم.', level: 'info', to: 'all' } });
  await stub('/stub/command', { mid, type: 'suspend', data: { message: 'Please contact Nextdab.', message_ar: 'يرجى التواصل مع نكستداب.' } });
  await main.getByRole('button', { name: 'Check in now' }).click();
  await until(main, 'switched-off banner', async () => (await bannerText(main)).includes('KITE is switched off by Nextdab. Please contact Nextdab.'));
  if (!(await bannerText(main)).includes('Your renewal is due next month.')) throw new Error('Nextdab message not shown');
  await shot(main, 'switched-off-by-nextdab');
  const st1 = await stub('/stub/state');
  if (!st1.acks.some((a) => a.type === 'suspend' && a.ok)) throw new Error('suspend was not acknowledged');
  const rep = JSON.stringify(st1.lastReport);
  if (!st1.lastReport?.license || st1.lastReport.product !== 'kite-mobile-erp') throw new Error(`unexpected report ${rep.slice(0, 300)}`);
  for (const bad of ['password', 'imei', 'customer_phone', 'argon2']) if (rep.toLowerCase().includes(bad)) throw new Error(`report contains ${bad}`);
  await stub('/stub/command', { mid, type: 'resume', data: {} });
  await main.getByRole('button', { name: 'Check in now' }).click();
  await until(main, 'switched-off banner gone', async () => !(await bannerText(main)).includes('switched off'));

  step('branch B02 is added and a one-time registration token is made');
  await nav(main, 'Branches and servers');
  await main.getByRole('button', { name: 'Add branch' }).click();
  dlg = main.getByRole('dialog');
  await dlg.getByLabel(/^Code/).fill('B02');
  await dlg.getByLabel(/Branch name \(English\)/).fill('Sohar');
  await dlg.getByLabel(/Branch name \(Arabic\)/).fill('صحار');
  await dlg.getByRole('button', { name: 'Save' }).click();
  await main.getByRole('row', { name: /B02/ }).getByRole('button', { name: 'Register server' }).click();
  const tokenEl = main.getByTestId('registration-token');
  await tokenEl.waitFor({ timeout: 20000 });
  const token = (await tokenEl.innerText()).trim();
  const caCode = (await main.getByRole('dialog').locator('strong.ltr').innerText()).trim();
  await shot(main, 'registration-token');
  await main.getByRole('dialog').getByRole('button', { name: 'Close' }).last().click();

  step('the branch server joins in Arabic after comparing the security code');
  await branch.goto(BRANCH + '/login');
  await branch.getByRole('button', { name: 'العربية' }).click();
  await branch.goto(BRANCH + '/setup');
  await branch.getByRole('button', { name: /^خادم فرع لمنشأة موجودة/ }).click();
  await branch.getByLabel(/^رمز الإعداد/).fill(code(process.env.BRANCH_SETUP_CODE_FILE));
  await branch.getByLabel(/^عنوان المكتب الرئيسي/).fill('https://localhost:5443');
  await branch.getByRole('button', { name: 'اتصال' }).click();
  const joinCode = (await branch.getByTestId('join-code').innerText({ timeout: 30000 })).trim();
  if (joinCode !== caCode) throw new Error(`security codes differ: branch shows ${joinCode}, head office ${caCode}`);
  await shot(branch, 'branch-compare-code-ar');
  await branch.getByLabel('الرمزان متطابقان').check();
  await branch.getByLabel(/^رمز التسجيل/).fill(token);
  await branch.getByLabel(/^اسم هذا الخادم/).fill('SOHAR-PC');
  await branch.getByRole('button', { name: 'انضمام' }).click();
  await branch.getByText('أصبح هذا الخادم خادم الفرع').waitFor({ timeout: 60000 });
  await shot(branch, 'branch-joined-ar');

  step('the owner signs in on the branch (users came from head office) and sees the head office link');
  await signIn(branch, BRANCH);
  if ((await branch.evaluate(() => document.documentElement.lang)) !== 'en') await branch.getByRole('button', { name: 'English' }).click();
  await nav(branch, 'Branches and servers');
  await until(branch, 'branch heartbeat ok', async () => (await branch.getByText('Last successful contact').count()) > 0 && !(await branch.getByText('Never').count()));
  await shot(branch, 'branch-link-to-head-office');

  step('head office shows the branch server online');
  await nav(main, 'Branches and servers');
  await until(main, 'branch server online', async () => (await main.getByRole('row', { name: /SOHAR-PC/ }).getByText('Online').count()) > 0);
  await shot(main, 'head-office-sees-branch-online');

  step('backups: full backup, base backup and a restore check, then the recovery key');
  await nav(main, 'Backups');
  await main.getByText('Back up now').waitFor();
  const runAndWait = async (button, kind) => {
    await main.getByRole('button', { name: button }).click();
    await until(main, `${kind} succeeded`, async () => (await main.getByRole('row', { name: new RegExp(`^${kind}.*Succeeded`) }).count()) > 0, 240000);
  };
  await runAndWait('Back up now', 'Full backup');
  await runAndWait('Base backup now', 'Base backup');
  await runAndWait('Check a restore now', 'Restore check');
  if (!(await main.getByText(/Journals balanced, sync sequence gap-free, audit chain intact/).count())) throw new Error('restore check has no passed checks');
  await main.getByRole('button', { name: 'Show recovery key' }).click();
  await main.getByRole('dialog').getByLabel('Your password').fill(OWNER_PW);
  await main.getByRole('dialog').getByRole('button', { name: 'Show recovery key' }).click();
  const key = (await main.getByTestId('recovery-key').innerText({ timeout: 15000 })).trim();
  if (process.env.RECOVERY_KEY_FILE) fs.writeFileSync(process.env.RECOVERY_KEY_FILE, key);
  await shot(main, 'backups-verified');

  step('head office revokes the branch server; the branch learns it at its next heartbeat');
  await nav(main, 'Branches and servers');
  await main.getByRole('row', { name: /SOHAR-PC/ }).getByRole('button', { name: 'Revoke server' }).click();
  await main.getByRole('dialog').getByLabel('Reason').selectOption('key_compromise');
  await main.getByRole('dialog').getByRole('button', { name: 'Revoke server' }).click();
  await until(main, 'server revoked', async () => (await main.getByRole('row', { name: /SOHAR-PC/ }).getByText('Revoked').count()) > 0);
  await shot(main, 'branch-server-revoked');
  await until(branch, 'branch sees revocation', async () => (await branch.getByText('Head office revoked this server.').count()) > 0);
  await shot(branch, 'branch-told-it-is-revoked');

  step('Nextdab goes offline; an ended licence is still enforced: no new registrations, backups keep working');
  await fetch(STUB + '/stub/stop', { method: 'POST' }).catch(() => undefined);
  await importLicence(main, { mid, kind: 'subscription', validUntil: day(-40), branches: 3, terminals: 5, graceDays: 30 });
  await until(main, 'ended banner', async () => (await bannerText(main)).includes('The licence has ended.'));
  await nav(main, 'Branches and servers');
  await main.getByRole('row', { name: /SOHAR-PC/ }).getByRole('button', { name: 'Register server' }).click();
  await main.getByText('LIC-002').waitFor({ timeout: 15000 });
  await shot(main, 'ended-licence-blocks-new-registration');
  await nav(main, 'Backups');
  const before = await main.getByRole('row', { name: /^Full backup.*Succeeded/ }).count();
  await main.getByRole('button', { name: 'Back up now' }).click();
  await until(main, 'backup after the licence ended', async () => (await main.getByRole('row', { name: /^Full backup.*Succeeded/ }).count()) > before, 240000);
  await shot(main, 'backup-still-works-after-licence-ended');

  if (pageErrors.length) throw new Error(`browser errors: ${pageErrors.join(' | ')}`);
  console.log('E2E PHASE 3: ALL STEPS PASSED');
} catch (e) {
  await shot(main, 'FAILED-main').catch(() => undefined);
  await shot(branch, 'FAILED-branch').catch(() => undefined);
  console.error(`E2E PHASE 3 FAILED: ${e instanceof Error ? e.message : e}`);
  process.exitCode = 1;
} finally {
  await browser.close();
}
