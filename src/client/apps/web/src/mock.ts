// In-memory stand-in for the KITE server, used by tests, `npm run dev:mock` and design review.
// It mirrors the server's main rules (permissions, module dependencies, password policy) but is
// NOT the source of truth: the real server enforces everything again.
import { ApiError, type Account, type AuditRow, type Branch, type Business, type KiteApi, type Me, type Module, type Permission, type Role, type TaxCode, type Terminal, type Tokens, type User } from '@kite/api';

const PERMISSIONS: Permission[] = [
  ['SETUP.BUSINESS.EDIT', 'core', 'Edit business profile and tax registration', 'تعديل بيانات المنشأة والتسجيل الضريبي'],
  ['SETTINGS.VIEW', 'core', 'View system settings', 'عرض إعدادات النظام'],
  ['SETTINGS.MODULES.EDIT', 'core', 'Switch modules on or off', 'تفعيل الوحدات أو إيقافها'],
  ['TERMINALS.APPROVE', 'core', 'Approve and revoke terminals', 'اعتماد الأجهزة الطرفية وإلغاؤها'],
  ['USERS.VIEW', 'iam', 'View users', 'عرض المستخدمين'],
  ['USERS.MANAGE', 'iam', 'Create, edit and disable users', 'إنشاء المستخدمين وتعديلهم وإيقافهم'],
  ['ROLES.MANAGE', 'iam', 'Create and edit roles and permissions', 'إنشاء الأدوار والصلاحيات وتعديلها'],
  ['AUDIT.VIEW', 'audit', 'View the audit log', 'عرض سجل التدقيق'],
  ['AUDIT.VERIFY', 'audit', 'Run the audit chain verification', 'تشغيل التحقق من سلسلة سجل التدقيق'],
  ['TAX.REVIEW', 'tax', 'Confirm tax codes as reviewed by the accountant', 'تأكيد مراجعة المحاسب للرموز الضريبية'],
  ['POS.SELL', 'sales', 'Make sales at the POS', 'إجراء المبيعات في نقطة البيع'],
].map(([code, moduleCode, descriptionEn, descriptionAr]) => ({ code: code!, moduleCode: moduleCode!, descriptionEn: descriptionEn!, descriptionAr: descriptionAr! }));

const MODULES: Module[] = [
  ['core', 'Platform', 'المنصة', true, true, []],
  ['catalog', 'Catalog', 'دليل المنتجات', true, true, ['core']],
  ['inv', 'Inventory', 'المخزون', true, true, ['catalog']],
  ['device', 'Serialized devices and IMEI', 'الأجهزة المتسلسلة وIMEI', false, true, ['inv']],
  ['refurb', 'Condition and refurbishment', 'الحالة والتجديد', false, false, ['device']],
  ['sales', 'POS and sales', 'نقاط البيع والمبيعات', true, true, ['catalog', 'inv', 'tax']],
  ['tradein', 'Trade-in', 'الاستبدال', false, false, ['device', 'refurb', 'sales']],
  ['repair', 'Repairs', 'الصيانة', false, true, ['inv']],
  ['comm', 'Technician commissions', 'عمولات الفنيين', false, false, ['repair']],
  ['credit', 'Customer credit', 'ائتمان العملاء', false, false, ['sales']],
  ['purch', 'Purchasing and suppliers', 'المشتريات والموردون', false, true, ['inv']],
  ['tax', 'Tax', 'الضرائب', true, true, ['core']],
  ['acct_ui', 'Accounting screens', 'شاشات المحاسبة', false, true, ['core']],
  ['notif', 'WhatsApp notifications', 'إشعارات واتساب', false, false, ['core']],
].map(([code, nameEn, nameAr, alwaysOn, enabled, dependsOn]) => ({ code, nameEn, nameAr, alwaysOn, enabled, dependsOn }) as Module);

const problem = (status: number, code: string, title: string) => new ApiError({ status, code, title });
const id = () => crypto.randomUUID();

export function createMockApi(opts: { configured: boolean }): KiteApi {
  let configured = opts.configured;
  const branch: Branch = { id: id(), code: 'B01', nameEn: 'Muscat - Ruwi', nameAr: 'مسقط - روي', isHeadOffice: true, status: 'active' };
  const branches: Branch[] = [branch, { id: id(), code: 'B02', nameEn: 'Sohar', nameAr: 'صحار', isHeadOffice: false, status: 'active' }];
  let business: Business = {
    id: id(), legalNameEn: 'Al Noor Mobiles LLC', legalNameAr: 'شركة النور للهواتف', tradeNameEn: 'Al Noor Mobiles', tradeNameAr: 'النور للهواتف',
    crNumber: '1234567', vatNumber: 'OM1100223344', countryCode: 'OM', baseCurrency: 'OMR', currencyDecimals: 3, timeZone: 'Asia/Muscat',
    fiscalYearStartMonth: 1, pricesIncludeTax: true, addressEn: null, addressAr: null, phone: '+968 2400 0000', email: null, version: 1,
  };
  const roles: Role[] = [
    { id: id(), code: 'owner', nameEn: 'Owner', nameAr: 'المالك', isSystem: true, version: 1, permissions: PERMISSIONS.map((p) => p.code) },
    { id: id(), code: 'branch_manager', nameEn: 'Branch manager', nameAr: 'مدير الفرع', isSystem: true, version: 1, permissions: ['SETTINGS.VIEW', 'TERMINALS.APPROVE', 'USERS.VIEW', 'AUDIT.VIEW', 'POS.SELL'] },
    { id: id(), code: 'cashier', nameEn: 'Cashier', nameAr: 'أمين صندوق', isSystem: true, version: 1, permissions: ['POS.SELL'] },
    { id: id(), code: 'accountant', nameEn: 'Accountant', nameAr: 'المحاسب', isSystem: true, version: 1, permissions: ['SETTINGS.VIEW', 'AUDIT.VIEW', 'AUDIT.VERIFY', 'TAX.REVIEW'] },
  ];
  const passwords = new Map<string, string>([['owner', 'Muscat-Ruwi-2026!']]);
  const users: User[] = [{
    id: id(), username: 'owner', displayNameEn: 'Salim Al Harthy', displayNameAr: 'سالم الحارثي', email: null, phone: null, status: 'active',
    locked: false, totpEnabled: false, hasPin: false, preferredLanguage: 'en', defaultBranchId: branch.id, lastLoginAt: new Date().toISOString(),
    version: 1, roles: [{ roleId: roles[0]!.id, roleCode: 'owner', roleNameEn: 'Owner', roleNameAr: 'المالك', branchId: null }],
  }];
  const modules = MODULES.map((m) => ({ ...m }));
  const terminals: Terminal[] = [
    { id: id(), branchId: branch.id, code: 'T01', name: 'Front counter', kind: 'windows', status: 'active', emergencyEnabled: true, requestedAt: new Date(Date.now() - 86400000).toISOString(), approvedAt: new Date().toISOString(), version: 2 },
    { id: id(), branchId: branch.id, code: 'T02', name: 'Repair desk', kind: 'browser', status: 'pending', emergencyEnabled: false, requestedAt: new Date().toISOString(), approvedAt: null, version: 1 },
  ];
  const taxCodes: TaxCode[] = [
    ['VAT-STD', 'Standard rate VAT', 'ضريبة القيمة المضافة بالنسبة الأساسية', 'standard', 'active', 5],
    ['VAT-ZERO', 'Zero-rated', 'خاضع لنسبة الصفر', 'zero', 'active', 0],
    ['VAT-EXEMPT', 'Exempt', 'معفى', 'exempt', 'active', 0],
    ['OUT-SCOPE', 'Out of scope', 'خارج نطاق الضريبة', 'out_of_scope', 'active', 0],
    ['VAT-MARGIN', 'VAT on profit margin (used goods)', 'ضريبة على هامش الربح (سلع مستعملة)', 'margin', 'inactive', 5],
  ].map(([code, nameEn, nameAr, kind, status, rate]) => ({
    id: id(), code: code as string, nameEn: nameEn as string, nameAr: nameAr as string, kind: kind as string, status: status as string,
    needsAccountantReview: true, reviewedAt: null, rates: [{ ratePercent: rate as number, effectiveFrom: '2021-04-16', effectiveTo: null }],
  }));
  const accounts: Account[] = [
    ['1000', 'Assets', 'الأصول', 'asset', false, false], ['1100', 'Cash on hand', 'النقدية في الصندوق', 'asset', true, false],
    ['1300', 'Accounts receivable', 'الذمم المدينة', 'asset', true, true], ['1400', 'Inventory: new devices', 'المخزون: أجهزة جديدة', 'asset', true, true],
    ['2000', 'Liabilities', 'الالتزامات', 'liability', false, false], ['2200', 'VAT output', 'ضريبة المخرجات', 'liability', true, true],
    ['4000', 'Revenue', 'الإيرادات', 'revenue', false, false], ['4100', 'Sales: new devices', 'المبيعات: أجهزة جديدة', 'revenue', true, false],
  ].map(([code, nameEn, nameAr, type, isPostable, isControl]) => ({ id: id(), code, nameEn, nameAr, type, parentId: isPostable ? 'p' : null, isPostable, isControl, status: 'active' }) as Account);
  const audit: AuditRow[] = [];
  let current: User | null = null;
  let mfaPending = false;

  const log = (action: string, entityType: string | null = null, reason: string | null = null) =>
    audit.unshift({ seq: audit.length + 1, occurredAt: new Date().toISOString(), userId: current?.id ?? null, username: current?.username ?? null, branchId: null, action, entityType, entityId: null, reason });
  log('setup.completed', 'business');

  const permsOf = (u: User) => [...new Set(u.roles.flatMap((r) => roles.find((x) => x.id === r.roleId)?.permissions ?? []))].sort();
  const need = (perm?: string) => {
    if (!current) throw problem(401, 'AUTH-401', 'Sign in required.');
    if (mfaPending) throw problem(401, 'AUTH-004', 'Enter the code from your authenticator app to continue.');
    if (perm && !permsOf(current).includes(perm)) throw problem(403, 'AUTH-403', `You do not have permission ${perm}.`);
    return current;
  };
  const tokens = (u: User): Tokens => ({ accessToken: 'mock', accessExpiresAt: new Date(Date.now() + 9e5).toISOString(), refreshToken: 'mock', refreshExpiresAt: new Date(Date.now() + 4.32e7).toISOString(), mfaRequired: u.totpEnabled, mustChangePassword: false });
  const checkPassword = (pw: string, username: string) => {
    if (pw.length < 10) throw problem(400, 'AUTH-006', 'Password must be at least 10 characters.');
    if (pw.toLowerCase().includes(username.toLowerCase())) throw problem(400, 'AUTH-006', 'Password must not contain the username.');
  };
  const delay = <T>(v: T): Promise<T> => new Promise((r) => setTimeout(() => r(v), 30));

  const api: KiteApi = {
    async setupStatus() { return delay({ configured, role: configured ? 'combined' : null }); },
    async setup(req) {
      if (configured) throw problem(409, 'SET-001', 'This server is already set up.');
      if (req.setupCode !== 'ABCD2345') throw problem(403, 'SET-002', 'The setup code is not correct. It is shown on the server screen and in its data folder.');
      checkPassword(req.owner.password, req.owner.username);
      business = { ...business, legalNameEn: req.business.legalNameEn, legalNameAr: req.business.legalNameAr };
      passwords.set(req.owner.username, req.owner.password);
      users[0] = { ...users[0]!, username: req.owner.username, displayNameEn: req.owner.displayNameEn };
      configured = true;
      return delay({ businessId: business.id, branchId: branch.id, ownerUserId: users[0]!.id });
    },
    async login(username, password) {
      const u = users.find((x) => x.username === username.toLowerCase());
      if (!u || passwords.get(u.username) !== password) throw problem(401, 'AUTH-001', 'Username or password is not correct.');
      current = u;
      mfaPending = u.totpEnabled;
      log('auth.login', 'session');
      return delay(tokens(u));
    },
    async mfa(code) {
      if (code !== '123456') throw problem(401, 'AUTH-005', 'That code is not correct. Check the time on your phone and try again.');
      mfaPending = false;
    },
    async me() {
      if (!current) throw problem(401, 'AUTH-401', 'Sign in required.');
      const all = current.roles.some((r) => r.branchId === null);
      const me: Me = {
        user: { id: current.id, username: current.username, displayNameEn: current.displayNameEn, displayNameAr: current.displayNameAr, preferredLanguage: current.preferredLanguage, totpEnabled: current.totpEnabled, defaultBranchId: current.defaultBranchId },
        business: { legalNameEn: business.legalNameEn, legalNameAr: business.legalNameAr, currency: 'OMR', decimals: 3, pricesIncludeTax: business.pricesIncludeTax },
        permissions: permsOf(current), allBranches: all, branches: all ? branches : branches.filter((b) => current!.roles.some((r) => r.branchId === b.id)),
        mfaPending, mustChangePassword: false, serverRole: 'combined',
      };
      return delay(me);
    },
    async logout() { current = null; },
    async changePassword(cur, next) {
      const u = need();
      if (passwords.get(u.username) !== cur) throw problem(400, 'AUTH-001', 'Current password is not correct.');
      checkPassword(next, u.username);
      passwords.set(u.username, next);
    },
    async setPin(pin) { const u = need(); if (!/^\d{4,8}$/.test(pin)) throw problem(400, 'AUTH-006', 'PIN must be 4 to 8 digits.'); u.hasPin = true; },
    async startTotp() { need(); return delay({ secret: 'JBSWY3DPEHPK3PXP', otpauthUri: 'otpauth://totp/KITE%20MOBILE%20ERP:owner?secret=JBSWY3DPEHPK3PXP&issuer=KITE%20MOBILE%20ERP' }); },
    async enableTotp(code) { const u = need(); if (code !== '123456') throw problem(400, 'AUTH-005', 'That code is not correct.'); u.totpEnabled = true; },
    async disableTotp() { const u = need(); u.totpEnabled = false; },
    async users() { need('USERS.VIEW'); return delay(users.map((u) => ({ ...u }))); },
    async createUser(c) {
      need('USERS.MANAGE');
      if (users.some((u) => u.username === c.username)) throw problem(409, 'GEN-409', 'That value is already in use.');
      if (c.roles.length === 0) throw problem(400, 'VAL-001', 'Give the user at least one role.');
      checkPassword(c.temporaryPassword, c.username);
      const u: User = { id: id(), username: c.username, displayNameEn: c.displayNameEn, displayNameAr: c.displayNameAr ?? null, email: null, phone: null, status: 'active', locked: false, totpEnabled: false, hasPin: false, preferredLanguage: c.preferredLanguage, defaultBranchId: null, lastLoginAt: null, version: 1,
        roles: c.roles.map((g) => { const r = roles.find((x) => x.id === g.roleId)!; return { roleId: r.id, roleCode: r.code, roleNameEn: r.nameEn, roleNameAr: r.nameAr, branchId: g.branchId }; }) };
      users.push(u);
      passwords.set(u.username, c.temporaryPassword);
      log('user.created', 'user');
      return delay(u);
    },
    async updateUser(uid, c) {
      need('USERS.MANAGE');
      const u = users.find((x) => x.id === uid)!;
      if (u.version !== c.version) throw problem(412, 'GEN-412', 'Someone else changed this user. Reload and try again.');
      Object.assign(u, { displayNameEn: c.displayNameEn, displayNameAr: c.displayNameAr ?? null, status: c.status, preferredLanguage: c.preferredLanguage, version: u.version + 1,
        roles: c.roles.map((g) => { const r = roles.find((x) => x.id === g.roleId)!; return { roleId: r.id, roleCode: r.code, roleNameEn: r.nameEn, roleNameAr: r.nameAr, branchId: g.branchId }; }) });
      log('user.updated', 'user');
      return delay({ ...u });
    },
    async unlockUser(uid) { need('USERS.MANAGE'); users.find((x) => x.id === uid)!.locked = false; },
    async resetPassword(uid, pw) { need('USERS.MANAGE'); const u = users.find((x) => x.id === uid)!; checkPassword(pw, u.username); passwords.set(u.username, pw); },
    async roles() { need('USERS.VIEW'); return delay(roles.map((r) => ({ ...r }))); },
    async permissions() { need('USERS.VIEW'); return delay(PERMISSIONS); },
    async createRole(r) { need('ROLES.MANAGE'); const role: Role = { id: id(), isSystem: false, version: 1, ...r }; roles.push(role); return delay(role); },
    async updateRole(rid, r) {
      need('ROLES.MANAGE');
      const role = roles.find((x) => x.id === rid)!;
      if (role.code === 'owner') throw problem(409, 'IAM-001', 'The owner role always has every permission and cannot be edited.');
      Object.assign(role, { ...r, version: role.version + 1 });
      return delay({ ...role });
    },
    async branches() { need(); return delay(branches); },
    async business() { need('SETTINGS.VIEW'); return delay({ ...business }); },
    async updateBusiness(b) {
      need('SETUP.BUSINESS.EDIT');
      if (b.version !== business.version) throw problem(412, 'GEN-412', 'Someone else changed the business details. Reload and try again.');
      business = { ...business, ...b, version: business.version + 1 };
      log('business.updated', 'business');
      return delay({ ...business });
    },
    async modules() { need('SETTINGS.VIEW'); return delay(modules.map((m) => ({ ...m }))); },
    async updateModules(changes) {
      need('SETTINGS.MODULES.EDIT');
      const next = new Map(modules.map((m) => [m.code, m.enabled]));
      for (const [code, on] of Object.entries(changes)) {
        const m = modules.find((x) => x.code === code)!;
        if (m.alwaysOn && !on) throw problem(400, 'MOD-002', `${m.nameEn} is always on and cannot be switched off.`);
        next.set(code, on);
      }
      for (const m of modules) {
        if (!next.get(m.code)) continue;
        const missing = m.dependsOn.filter((d) => !next.get(d));
        if (missing.length) throw problem(400, 'MOD-001', `${m.nameEn} needs ${missing.map((d) => modules.find((x) => x.code === d)!.nameEn).join(', ')} switched on first.`);
      }
      for (const m of modules) m.enabled = !!next.get(m.code);
      log('settings.modules_changed', 'business');
      return delay(modules.map((m) => ({ ...m })));
    },
    async terminals() { need('TERMINALS.APPROVE'); return delay(terminals.map((x) => ({ ...x }))); },
    async requestTerminal(r) { need(); const term: Terminal = { id: id(), ...r, status: 'pending', emergencyEnabled: false, requestedAt: new Date().toISOString(), approvedAt: null, version: 1 }; terminals.push(term); return delay({ id: term.id }); },
    async approveTerminal(tid, emergency) { need('TERMINALS.APPROVE'); const x = terminals.find((y) => y.id === tid)!; Object.assign(x, { status: 'active', emergencyEnabled: emergency && x.kind === 'windows', approvedAt: new Date().toISOString() }); log('terminal.approved', 'terminal'); return delay({ ...x }); },
    async revokeTerminal(tid) { need('TERMINALS.APPROVE'); terminals.find((y) => y.id === tid)!.status = 'revoked'; },
    async taxCodes() { need('SETTINGS.VIEW'); return delay(taxCodes.map((c) => ({ ...c }))); },
    async reviewTaxCode(cid) { need('TAX.REVIEW'); const c = taxCodes.find((x) => x.id === cid)!; c.needsAccountantReview = false; c.reviewedAt = new Date().toISOString(); log('tax.code_reviewed', 'tax_code', 'Confirmed by accountant'); },
    async accounts() {
      need('SETTINGS.VIEW');
      if (!modules.find((m) => m.code === 'acct_ui')!.enabled) throw problem(403, 'MOD-403', 'The Accounting screens module is switched off.');
      return delay(accounts);
    },
    async audit() { need('AUDIT.VIEW'); return delay(audit.slice(0, 50)); },
    async verifyAudit() { need('AUDIT.VERIFY'); log('audit.verified', null, 'chain intact'); return delay({ ok: true, rowsChecked: audit.length, firstBrokenSeq: null }); },
  };
  return api;
}
