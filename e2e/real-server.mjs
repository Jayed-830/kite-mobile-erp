// Walks through a running KITE server in a real browser, in Arabic and English:
// setup wizard -> owner sign-in -> create a cashier -> cashier's forced password change and limited menu
// -> owner turns on two-step verification -> two-step sign-in (wrong code, then right code) -> audit chain check.
// Screenshots go to SHOTS_DIR. Exits non-zero on the first failed step.
//
//   KITE_URL            server address (default http://localhost:5080)
//   KITE_SETUP_CODE     setup code, or KITE_SETUP_CODE_FILE pointing at the server's setup-code.txt
//   CHROME_PATH         browser to drive (default /usr/bin/google-chrome)
//   KITE_FIXED_TOTP     only for the design mock, which accepts a fixed code
//   KITE_EXPECT_REAL=1  require behaviour only the real server has (forced password change)
import { chromium } from 'playwright-core';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const base = process.env.KITE_URL ?? 'http://localhost:5080';
const shots = process.env.SHOTS_DIR ?? 'shots';
const setupCode = (process.env.KITE_SETUP_CODE ?? fs.readFileSync(process.env.KITE_SETUP_CODE_FILE ?? 'setup-code.txt', 'utf8')).trim();
const real = process.env.KITE_EXPECT_REAL === '1';
const OWNER_PW = 'Muscat-Ruwi-2026!';
const CASHIER_TEMP = 'Temp-Seeb-2026!';
const CASHIER_PW = 'Seeb-Souq-2026!';
fs.mkdirSync(shots, { recursive: true });

// RFC 6238 TOTP (SHA-1, 6 digits, 30 s), the same as an authenticator app.
const base32 = (s) => {
  const abc = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; let bits = ''; const out = [];
  for (const c of s.replace(/[\s=]/g, '').toUpperCase()) { const v = abc.indexOf(c); if (v < 0) throw new Error(`bad base32 ${c}`); bits += v.toString(2).padStart(5, '0'); }
  for (let i = 0; i + 8 <= bits.length; i += 8) out.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(out);
};
const stepNow = () => Math.floor(Date.now() / 1000 / 30);
const totp = (key, step = stepNow()) => {
  if (process.env.KITE_FIXED_TOTP) return process.env.KITE_FIXED_TOTP;
  const msg = Buffer.alloc(8); msg.writeBigUInt64BE(BigInt(step));
  const h = crypto.createHmac('sha1', base32(key)).update(msg).digest(); const o = h[h.length - 1] & 15;
  return String((h.readUInt32BE(o) & 0x7fffffff) % 1_000_000).padStart(6, '0');
};

const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH ?? '/usr/bin/google-chrome', args: ['--no-sandbox'] });
const page = await (await browser.newContext({ viewport: { width: 1366, height: 820 } })).newPage();
const pageErrors = []; page.on('pageerror', (e) => pageErrors.push(e.message));
let n = 0;
const shot = async (name) => { n += 1; const f = path.join(shots, `${String(n).padStart(2, '0')}-${name}.jpg`); await page.waitForTimeout(250); await page.screenshot({ path: f, type: 'jpeg', quality: 72 }); console.log(`  screenshot ${f}`); };
const step = (s) => console.log(`- ${s}`);
const expectVisible = (text, timeout = 15000) => page.getByText(text, { exact: false }).first().waitFor({ state: 'visible', timeout });
const lang = async (to) => { await page.getByRole('button', { name: to === 'ar' ? 'العربية' : 'English' }).click(); await page.waitForFunction((l) => document.documentElement.lang === l, to); };
const signIn = async (username, password) => {
  await page.locator('input[autocomplete="username"]').fill(username);
  await page.locator('input[autocomplete="current-password"]').fill(password);
  await page.locator('form button[type="submit"]').click();
};
const welcome = () => page.getByRole('heading', { name: /^(Welcome, |مرحبًا، )/ }).waitFor({ timeout: 20000 });
const currentLang = () => page.evaluate(() => document.documentElement.lang);
const signOut = async () => { await page.getByRole('button', { name: /^(Sign out|تسجيل الخروج)$/ }).click(); await page.locator('input[autocomplete="username"]').waitFor(); };

try {
  step('first visit to a new server offers the setup wizard');
  await page.goto(base + '/');
  await lang('ar');
  await page.getByRole('link', { name: 'ابدأ الإعداد' }).click();
  await page.waitForURL(/\/setup$/, { timeout: 20000 });
  await expectVisible('إعداد منشأتك');

  step('setup wizard in Arabic');
  await page.getByLabel(/^رمز الإعداد/).fill(setupCode.toLowerCase()); // the code is case-insensitive
  await page.getByLabel(/^الاسم القانوني \(بالإنجليزية\)/).fill('Al Noor Mobiles LLC');
  await page.getByLabel(/^الاسم القانوني \(بالعربية\)/).fill('شركة النور للهواتف');
  await page.getByLabel(/^رقم التسجيل في ضريبة القيمة المضافة/).fill('OM1100223344');
  await shot('setup-business-ar');
  await page.getByRole('button', { name: 'التالي' }).click();
  await page.getByLabel(/^اسم الفرع \(بالإنجليزية\)/).fill('Muscat - Ruwi');
  await page.getByLabel(/^اسم الفرع \(بالعربية\)/).fill('مسقط - روي');
  await page.getByRole('button', { name: 'التالي' }).click();
  await page.getByLabel(/^اسمك \(بالإنجليزية\)/).fill('Salim Al Harthy');
  await page.getByLabel(/^اسمك \(بالعربية\)/).fill('سالم الحارثي');
  await page.getByLabel(/^كلمة المرور/).first().fill(OWNER_PW);
  await page.getByLabel(/^تأكيد كلمة المرور/).fill(OWNER_PW);
  await page.getByRole('button', { name: 'التالي' }).click();
  await page.getByRole('button', { name: 'التالي' }).click(); // default modules
  await shot('setup-review-ar');
  await page.getByRole('button', { name: 'إنشاء المنشأة' }).click();
  await page.waitForURL(/\/login/, { timeout: 30000 });

  step('owner signs in; the app opens in the language chosen during setup (Arabic)');
  await signIn('owner', OWNER_PW);
  await welcome();
  if (real) {
    if ((await currentLang()) !== 'ar') throw new Error('the owner chose Arabic during setup but the app opened in English');
    await expectVisible('مرحبًا، سالم الحارثي');
  } else if ((await currentLang()) !== 'ar') await lang('ar');
  await shot('dashboard-ar');

  step('owner creates a cashier (English)');
  await lang('en');
  await page.getByRole('link', { name: 'Roles' }).click();
  await expectVisible('Cashier');
  await shot('roles-en');
  await page.getByRole('link', { name: 'Users' }).click();
  await page.getByRole('button', { name: 'Add user' }).click();
  const dlg = page.getByRole('dialog');
  await dlg.getByLabel(/^Username/).fill('cashier1');
  await dlg.getByLabel(/^Name( \*)?$/).fill('Aisha Al Balushi');
  await dlg.getByLabel(/^Temporary password/).fill(CASHIER_TEMP);
  await dlg.getByRole('button', { name: 'Add role' }).click();
  await dlg.getByLabel(/^Roles/).selectOption({ label: 'Cashier' });
  await shot('add-cashier-en');
  await dlg.getByRole('button', { name: 'Save' }).click();
  await dlg.waitFor({ state: 'hidden' });
  await expectVisible('cashier1');
  await shot('users-en');
  await signOut();

  step('cashier must change the temporary password and sees only what the role allows');
  await signIn('cashier1', CASHIER_TEMP);
  const forced = await page.getByText('Choose a new password').first().waitFor({ timeout: 8000 }).then(() => true, () => false);
  if (real && !forced) throw new Error('the real server did not ask the cashier to change the temporary password');
  if (forced) {
    await shot('cashier-change-password-en');
    await page.getByLabel(/^Current password/).fill(CASHIER_TEMP);
    await page.getByLabel(/^New password/).fill(CASHIER_PW);
    await page.getByRole('button', { name: 'Change password' }).click();
  }
  await welcome();
  if ((await currentLang()) !== 'en') await lang('en');
  if (real) await expectVisible('Welcome, Aisha Al Balushi');
  for (const hidden of ['Users', 'Roles', 'Audit log', 'Modules']) {
    if (await page.getByRole('link', { name: hidden, exact: true }).count()) throw new Error(`a cashier can see "${hidden}"`);
  }
  await shot('cashier-dashboard-en');
  await signOut();

  step('owner turns on two-step verification');
  await signIn('owner', OWNER_PW);
  await welcome();
  if ((await currentLang()) !== 'en') await lang('en');
  await page.getByRole('link', { name: 'My account' }).click();
  await page.getByRole('button', { name: 'Turn on' }).click();
  await expectVisible('Scan this code');
  const key = (await page.locator('code, .ltr').filter({ hasText: /^[A-Z2-7]{16,}$/ }).first().textContent())?.trim() ?? '';
  if (!/^[A-Z2-7]{16,}$/.test(key)) throw new Error(`could not read the setup key (got "${key}")`);
  await shot('2fa-setup-en');
  const enabledAt = stepNow();
  await page.locator('input[inputmode="numeric"]').last().fill(totp(key, enabledAt));
  await page.getByRole('button', { name: 'Confirm and turn on' }).click();
  await expectVisible('Turn off');
  await signOut();

  step('two-step sign-in in Arabic: wrong code refused, right code accepted');
  await lang('ar');
  await signIn('owner', OWNER_PW);
  await expectVisible('التحقق بخطوتين');
  const good = totp(key);
  await page.locator('input[autocomplete="one-time-code"]').fill(good === '000000' ? '111111' : '000000');
  await page.locator('form button[type="submit"]').click();
  await expectVisible('الرمز غير صحيح');
  await shot('2fa-wrong-code-ar');
  if (!process.env.KITE_FIXED_TOTP) while (stepNow() <= enabledAt) await page.waitForTimeout(1000); // a code is accepted only once
  await page.locator('input[autocomplete="one-time-code"]').fill(totp(key));
  await page.locator('form button[type="submit"]').click();
  await welcome();
  if ((await currentLang()) !== 'ar') await lang('ar');

  step('audit chain verifies and shows every action (Arabic)');
  await page.getByRole('link', { name: 'سجل التدقيق' }).click();
  await page.getByRole('button', { name: 'التحقق من السلسلة' }).click();
  await expectVisible('سلسلة سجل التدقيق سليمة');
  for (const action of ['setup.completed', 'auth.totp_enabled', 'auth.login_failed', 'auth.mfa_passed', 'user.created']) {
    if (real && !(await page.getByText(action, { exact: true }).count())) throw new Error(`audit log has no "${action}" entry`);
  }
  await shot('audit-verified-ar');

  if (pageErrors.length) throw new Error(`browser errors: ${pageErrors.join(' | ')}`);
  console.log('E2E: ALL STEPS PASSED');
} catch (e) {
  await shot('FAILED').catch(() => undefined);
  console.error(`E2E FAILED: ${e instanceof Error ? e.message : e}`);
  process.exitCode = 1;
} finally {
  await browser.close();
}
