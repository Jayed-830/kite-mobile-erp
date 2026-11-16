// In-memory stand-in for the KITE server, used by tests, `npm run dev:mock` and design review.
// It mirrors the server's main rules (permissions, module dependencies, password policy) but is
// NOT the source of truth: the real server enforces everything again.
import { createMockPos } from './mockPos';
import { ApiError, type Account, type AuditRow, type BackupRun, type Branch, type BranchAdmin, type Business, type KiteApi, type LicenceDetails, type LicenceState, type Me, type Module, type NextdabMessage, type Permission, type RegistrationTokenRow, type Role, type ServerInfo, type TaxCode, type Terminal, type Tokens, type User } from '@kite/api';

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
  ['TAX.MANAGE', 'tax', 'Manage tax codes and the margin scheme approval', 'إدارة الرموز الضريبية وموافقة نظام هامش الربح'],
  ['POS.SELL', 'sales', 'Make sales at the POS', 'إجراء المبيعات في نقطة البيع'],
  ['BRANCHES.MANAGE', 'core', 'Add and change branches', 'إضافة الفروع وتعديلها'],
  ['SERVERS.MANAGE', 'core', 'Register and revoke branch servers', 'تسجيل خوادم الفروع وإلغاؤها'],
  ['LICENCE.MANAGE', 'core', 'View and import the licence', 'عرض الترخيص واستيراده'],
  ['BACKUP.VIEW', 'core', 'View backups', 'عرض النسخ الاحتياطية'],
  ['BACKUP.MANAGE', 'core', 'Run backups and show the recovery key', 'تشغيل النسخ الاحتياطي وعرض مفتاح الاسترداد'],
  ['CATALOG.VIEW', 'catalog', 'View products and prices', 'عرض المنتجات والأسعار'],
  ['CATALOG.MANAGE', 'catalog', 'Create and edit products, barcodes and categories', 'إنشاء المنتجات والباركود والفئات وتعديلها'],
  ['CATALOG.PRICE.MANAGE', 'catalog', 'Set prices and branch price overrides', 'تحديد الأسعار وأسعار الفروع'],
  ['INV.VIEW', 'inv', 'View stock levels and movements', 'عرض أرصدة المخزون وحركاته'],
  ['INV.ADJUST', 'inv', 'Post stock adjustments and opening stock', 'ترحيل تسويات المخزون والأرصدة الافتتاحية'],
  ['INV.COUNT', 'inv', 'Run and post stock counts', 'إجراء جرد المخزون وترحيله'],
  ['CUSTOMERS.VIEW', 'crm', 'View customers', 'عرض العملاء'],
  ['CUSTOMERS.MANAGE', 'crm', 'Create and edit customers', 'إنشاء العملاء وتعديلهم'],
  ['POS.SHIFT.OPEN', 'sales', 'Open and close own shifts', 'فتح الورديات الخاصة وإغلاقها'],
  ['POS.CASH.PAY_OUT', 'sales', 'Pay cash out of the drawer', 'صرف نقد من الدرج'],
  ['POS.REPORTS.VIEW', 'sales', 'View sales, shifts and X/Z reports of the branch', 'عرض المبيعات والورديات وتقارير X وZ للفرع'],
  ['POS.POLICY.MANAGE', 'sales', 'Change POS policy (returns, refunds, rounding)', 'تغيير سياسة نقطة البيع'],
  ['APPROVALS.DECIDE', 'iam', 'Decide remote approval requests', 'البت في طلبات الموافقة عن بعد'],
  ['POS.DISCOUNT.ABOVE_LIMIT', 'sales', 'Give discounts above the cashier limit', 'منح خصومات أعلى من حد أمين الصندوق'],
  ['POS.PRICE.OVERRIDE', 'sales', 'Change prices and sell under the minimum price', 'تغيير الأسعار والبيع بأقل من الحد الأدنى'],
  ['POS.SALE.VOID', 'sales', 'Void a sale', 'إلغاء عملية بيع'],
  ['POS.REFUND', 'sales', 'Refund above the cap or after the return period', 'الاسترداد فوق الحد أو بعد مدة الإرجاع'],
  ['POS.DRAWER.NO_SALE', 'sales', 'Open the drawer without a sale', 'فتح الدرج بدون بيع'],
  ['POS.RECEIPT.REPRINT', 'sales', 'Reprint receipts', 'إعادة طباعة الإيصالات'],
  ['POS.SHIFT.ACCEPT_VARIANCE', 'sales', 'Accept a cash difference at shift close', 'قبول فرق النقد عند إغلاق الوردية'],
  ['DEVICE.VIEW', 'device', 'View device units, their history, costs and profit', 'عرض الأجهزة وسجلها وتكاليفها وأرباحها'],
  ['DEVICE.INSPECT', 'refurb', 'Inspect devices and decide restock, refurbish or parts', 'فحص الأجهزة وتقرير إعادتها للمخزون أو تجديدها أو تخصيصها للقطع'],
  ['DEVICE.REFURBISH', 'refurb', 'Work refurbishment orders: parts, labour, outside work', 'تنفيذ أوامر التجديد: القطع والأجور والأعمال الخارجية'],
  ['DEVICE.PRICE', 'device', 'Set the selling price of a device unit', 'تحديد سعر بيع الجهاز'],
  ['DEVICE.SELL_DAMAGED', 'device', 'Sell a damaged device (with disclosure on the receipt)', 'بيع جهاز به ضرر (مع الإفصاح في الإيصال)'],
  ['DEVICE.WRITE_OFF', 'device', 'Write off device units', 'شطب الأجهزة'],
  ['DEVICE.HOLD', 'device', 'Put device units on hold and release them', 'إيقاف الأجهزة مؤقتًا والإفراج عنها'],
  ['DEVICE.SETTINGS', 'refurb', 'Manage grades, inspection checklists and the labour rate', 'إدارة الدرجات وقوائم الفحص وسعر الأجور'],
  ['DEVICE.IMEI.CORRECT', 'device', 'Correct a wrongly scanned or typed IMEI (the old one is kept)', 'تصحيح رقم IMEI مُسح أو كُتب خطأً (يُحفظ القديم)'],
  ['DEVICE.CONFLICTS', 'device', 'See IMEI conflicts and settle emergency phone sales', 'عرض تعارضات IMEI وتسوية مبيعات الهواتف في وضع الطوارئ'],
  ['DEVICE.BLOCKLIST', 'device', 'Add and remove IMEIs on the blocked list (head office)', 'إضافة أرقام IMEI إلى قائمة الحظر وإزالتها (المكتب الرئيسي)'],
  ['DEVICE.COUNT', 'device', 'Count devices by IMEI', 'جرد الأجهزة برقم IMEI'],
  ['TRADEIN.CREATE', 'tradein', 'Take devices in as trade-ins: record, inspect, value and accept within the staff limit', 'استلام الأجهزة كاستبدال: تسجيلها وفحصها وتقييمها وقبولها ضمن حد الموظف'],
  ['TRADEIN.APPROVE', 'tradein', 'Approve trade-in values above the staff limit or the price guide', 'اعتماد قيم الاستبدال التي تتجاوز حد الموظف أو دليل الأسعار'],
  ['TRADEIN.STORE_CREDIT', 'tradein', "Settle a trade-in as the customer's store credit", 'تسوية الاستبدال كرصيد للعميل لدى المتجر'],
  ['TRADEIN.PAYOUT', 'tradein', 'Pay a trade-in out in cash from the drawer (buy-back)', 'دفع قيمة الاستبدال نقدًا من الدرج (شراء)'],
  ['TRADEIN.VIEW', 'tradein', 'View trade-ins, their documents and reports', 'عرض عمليات الاستبدال ومستنداتها وتقاريرها'],
  ['TRADEIN.SETTINGS', 'tradein', 'Manage the price guide, deductions, limits and the declaration', 'إدارة دليل الأسعار والخصومات والحدود والإقرار'],
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

export interface MockOptions { configured: boolean; licence?: LicenceState; licenceModules?: string[]; backupsConfigured?: boolean; messages?: NextdabMessage[] }

export function createMockApi(opts: MockOptions): KiteApi {
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
    { id: id(), code: 'branch_manager', nameEn: 'Branch manager', nameAr: 'مدير الفرع', isSystem: true, version: 1, permissions: ['SETTINGS.VIEW', 'TERMINALS.APPROVE', 'USERS.VIEW', 'AUDIT.VIEW', 'POS.SELL',
      'POS.DISCOUNT.ABOVE_LIMIT', 'POS.PRICE.OVERRIDE', 'POS.SALE.VOID', 'POS.REFUND', 'POS.DRAWER.NO_SALE', 'POS.RECEIPT.REPRINT', 'POS.SHIFT.ACCEPT_VARIANCE', 'POS.SHIFT.OPEN',
      'POS.CASH.PAY_OUT', 'POS.REPORTS.VIEW', 'APPROVALS.DECIDE', 'CATALOG.VIEW', 'CATALOG.MANAGE', 'CATALOG.PRICE.MANAGE', 'INV.VIEW', 'INV.ADJUST', 'INV.COUNT', 'CUSTOMERS.VIEW', 'CUSTOMERS.MANAGE',
      'DEVICE.VIEW', 'DEVICE.INSPECT', 'DEVICE.REFURBISH', 'DEVICE.PRICE', 'DEVICE.SELL_DAMAGED', 'DEVICE.WRITE_OFF', 'DEVICE.HOLD', 'DEVICE.SETTINGS',
      'DEVICE.IMEI.CORRECT', 'DEVICE.CONFLICTS', 'DEVICE.BLOCKLIST', 'DEVICE.COUNT',
      'TRADEIN.CREATE', 'TRADEIN.APPROVE', 'TRADEIN.STORE_CREDIT', 'TRADEIN.PAYOUT', 'TRADEIN.VIEW', 'TRADEIN.SETTINGS'] },
    { id: id(), code: 'cashier', nameEn: 'Cashier', nameAr: 'أمين صندوق', isSystem: true, version: 1, permissions: ['POS.SELL', 'POS.SHIFT.OPEN', 'POS.RECEIPT.REPRINT', 'CATALOG.VIEW', 'CUSTOMERS.VIEW', 'CUSTOMERS.MANAGE', 'TRADEIN.CREATE', 'TRADEIN.VIEW'] },
    { id: id(), code: 'accountant', nameEn: 'Accountant', nameAr: 'المحاسب', isSystem: true, version: 1, permissions: ['SETTINGS.VIEW', 'AUDIT.VIEW', 'AUDIT.VERIFY', 'TAX.MANAGE', 'TAX.REVIEW', 'TRADEIN.VIEW'] },
  ];
  const passwords = new Map<string, string>([['owner', 'Muscat-Ruwi-2026!'], ['manager', 'Manager-Pass-2026'], ['cashier', 'Cashier-Pass-2026']]);
  const pins = new Map<string, string>([['manager', '1357'], ['cashier', '2468']]);
  const users: User[] = [{
    id: id(), username: 'owner', displayNameEn: 'Salim Al Harthy', displayNameAr: 'سالم الحارثي', email: null, phone: null, status: 'active',
    locked: false, totpEnabled: false, hasPin: false, preferredLanguage: 'en', defaultBranchId: branch.id, lastLoginAt: new Date().toISOString(),
    version: 1, roles: [{ roleId: roles[0]!.id, roleCode: 'owner', roleNameEn: 'Owner', roleNameAr: 'المالك', branchId: null }],
  }, ...(['manager', 'cashier'] as const).map((username): User => ({
    id: id(), username, displayNameEn: username === 'manager' ? 'Huda Al Balushi' : 'Ahmed Al Rawahi', displayNameAr: username === 'manager' ? 'هدى البلوشية' : 'أحمد الرواحي', email: null, phone: null,
    status: 'active', locked: false, totpEnabled: false, hasPin: true, preferredLanguage: 'en', defaultBranchId: branch.id, lastLoginAt: null, version: 1,
    roles: [{ roleId: roles[username === 'manager' ? 1 : 2]!.id, roleCode: username === 'manager' ? 'branch_manager' : 'cashier', roleNameEn: '', roleNameAr: '', branchId: branch.id }],
  }))];
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
  // ---- Phase 3 state: branches, servers, licence, backups ----
  const branchVersions = new Map<string, number>(branches.map((b) => [b.id, 1]));
  const branchExtra = new Map<string, Pick<BranchAdmin, 'addressEn' | 'addressAr' | 'phone'>>();
  const now = Date.now();
  const iso = (ms: number) => new Date(ms).toISOString();
  const servers: ServerInfo[] = [
    { id: id(), branchId: branch.id, branchCode: 'B01', role: 'main', name: 'HO-SERVER', isSelf: true, status: 'active', appVersion: '0.3.0', lastSeenAt: iso(now), lastIp: null, installationId: '1A2B-3C4D-5E6F-7A8B', certificateSerial: null, certificateExpires: null, online: true, heartbeat: null },
    { id: id(), branchId: branches[1]!.id, branchCode: 'B02', role: 'branch', name: 'SOHAR-PC', isSelf: false, status: 'active', appVersion: '0.3.0', lastSeenAt: iso(now - 20_000), lastIp: '192.168.10.21', installationId: '9F8E-7D6C-5B4A-3921', certificateSerial: '7c1f', certificateExpires: iso(now + 700 * 864e5), online: true, heartbeat: { pending: 0, lastSeq: 1840 } },
  ];
  const regTokens: RegistrationTokenRow[] = [];
  const licState: LicenceState = opts.licence ?? 'active';
  const validUntil = licState === 'expiring' ? iso(now + 9 * 864e5) : licState === 'grace' || licState === 'expired' ? iso(now - (licState === 'grace' ? 5 : 40) * 864e5) : iso(now + 300 * 864e5);
  let licence: LicenceDetails = {
    state: licState,
    terms: licState === 'none' ? null : { id: 'kme-1794697889215-06d0f3', installationId: '1A2B-3C4D-5E6F-7A8B', kind: 'subscription', customer: 'Al Noor Mobiles LLC', validUntil: validUntil.slice(0, 10), graceDays: 30, branchesAllowed: 3, terminalsAllowed: 10, modules: opts.licenceModules ?? MODULES.map((m) => m.code), issuedAt: iso(now - 60 * 864e5) },
    daysLeft: licState === 'none' ? null : Math.round((Date.parse(validUntil) - now) / 864e5), endsOn: licState === 'grace' ? iso(Date.parse(validUntil) + 30 * 864e5).slice(0, 10) : null,
    branchesAllowed: licState === 'none' ? 1 : 3, terminalsAllowed: licState === 'none' ? 1 : 10, canTransact: !['none', 'expired', 'suspended'].includes(licState),
    suspendMessage: licState === 'suspended' ? 'Please contact Nextdab about the renewal.' : null, suspendMessageAr: licState === 'suspended' ? 'يرجى التواصل مع نكستداب بخصوص التجديد.' : null,
    branchesUsed: 2, terminalsUsed: 1, installationId: '1A2B-3C4D-5E6F-7A8B', role: 'main', keyConfigured: true,
    connection: { lastCheckinAt: iso(now - 3600_000), lastCheckinOkAt: iso(now - 3600_000), lastError: null, nextCheckinAt: iso(now + 5 * 3600_000), modulesOff: [] },
    history: licState === 'none' ? [] : [{ id: 'kme-1794697889215-06d0f3', kind: 'subscription', customer: 'Al Noor Mobiles LLC', validUntil: validUntil.slice(0, 10), source: 'nextdab', importedAt: iso(now - 60 * 864e5), isCurrent: true }],
  };
  const backupsConfigured = opts.backupsConfigured ?? true;
  const runs: BackupRun[] = backupsConfigured ? [
    { id: id(), kind: 'verify', status: 'succeeded', trigger: 'schedule', startedAt: iso(now - 2 * 864e5), finishedAt: iso(now - 2 * 864e5 + 95_000), fileName: null, sizeBytes: null, sha256: null, checks: { restored: true, journals_balanced: true, outbox_gap_free: true, audit_chain_intact: true, audit_rows_checked: 412 }, error: null },
    { id: id(), kind: 'logical', status: 'succeeded', trigger: 'schedule', startedAt: iso(now - 7 * 3600_000), finishedAt: iso(now - 7 * 3600_000 + 41_000), fileName: 'kite-20261115-0200.kbak', sizeBytes: 18_400_000, sha256: 'ab12', checks: null, error: null },
  ] : [];
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
  const pos = createMockPos({
    need, current: () => current, setCurrent: (u) => { current = u; mfaPending = false; }, permsOf, users, passwords, pins, terminals, branchId: branch.id,
    taxCodeId: taxCodes[0]!.id, business: () => business, tokens,
    setMarginCode: (active) => { taxCodes.find((c) => c.kind === 'margin')!.status = active ? 'active' : 'inactive'; },
  });

  const api: KiteApi = {
    ...pos,
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
    async setPin(pin) { const u = need(); if (!/^\d{4,8}$/.test(pin)) throw problem(400, 'AUTH-006', 'PIN must be 4 to 8 digits.'); u.hasPin = true; pins.set(u.username, pin); },
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
    async modules() {
      need('SETTINGS.VIEW');
      const lic = licence.terms?.modules;
      return delay(modules.map((m) => ({ ...m, locked: licence.connection?.modulesOff.includes(m.code) ? 'nextdab' as const : lic && !m.alwaysOn && !lic.includes(m.code) ? 'licence' as const : null })));
    },
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
    // ---- Phase 3 ----
    async joinInspect(mainUrl, setupCode) {
      if (configured) throw problem(409, 'SET-001', 'This server is already set up.');
      if (setupCode !== 'ABCD2345') throw problem(403, 'SET-002', 'The setup code is not correct.');
      if (!mainUrl.startsWith('https://')) throw problem(400, 'SRV-006', 'Head office could not be reached at that address.');
      return delay({ businessName: 'Al Noor Mobiles LLC', fingerprintCode: '4821-0937-5520-1764-3086', fingerprint: 'f'.repeat(64) });
    },
    async join(req) {
      if (req.token !== 'kite-reg-token-0123456789') throw problem(403, 'SRV-002', 'The registration token is not valid (wrong, used, cancelled or expired).');
      configured = true;
      return delay({ branchId: branches[1]!.id, businessName: 'Al Noor Mobiles LLC' });
    },
    async servers() {
      need('SETTINGS.VIEW');
      return delay({
        self: { serverId: servers[0]!.id, role: 'main', installationId: '1A2B-3C4D-5E6F-7A8B', version: '0.3.0', ca: { fingerprintCode: '4821-0937-5520-1764-3086', hosts: ['192.168.10.5', 'ho-server'] } },
        link: null, servers: servers.map((x) => ({ ...x })), tokens: regTokens.map((x) => ({ ...x })),
        branches: branches.map((b) => ({ ...b, status: b.status as 'active' | 'inactive', addressEn: null, addressAr: null, phone: null, ...branchExtra.get(b.id), version: branchVersions.get(b.id) ?? 1 })), syncPort: 5443,
      });
    },
    async createBranch(b) {
      need('BRANCHES.MANAGE');
      if (branches.filter((x) => x.status === 'active').length >= licence.branchesAllowed) throw problem(403, 'LIC-004', `The licence allows ${licence.branchesAllowed} branches. Ask Nextdab to extend it.`);
      if (!/^[A-Z0-9-]{2,12}$/.test(b.code)) throw problem(400, 'VAL-001', 'Branch code must be 2 to 12 letters, digits or hyphens.');
      if (branches.some((x) => x.code === b.code)) throw problem(409, 'GEN-409', `Branch code ${b.code} is already used.`);
      const nb: Branch = { id: id(), code: b.code, nameEn: b.nameEn, nameAr: b.nameAr, isHeadOffice: false, status: 'active' };
      branches.push(nb); branchVersions.set(nb.id, 1); branchExtra.set(nb.id, { addressEn: b.addressEn ?? null, addressAr: b.addressAr ?? null, phone: b.phone ?? null });
      licence = { ...licence, branchesUsed: licence.branchesUsed + 1 };
      log('branch.created', 'branch');
      return delay({ id: nb.id, code: nb.code });
    },
    async updateBranch(bid, b) {
      need('BRANCHES.MANAGE');
      const x = branches.find((y) => y.id === bid)!;
      if ((branchVersions.get(bid) ?? 1) !== b.version) throw problem(412, 'GEN-412', 'Someone else changed this branch. Reload and try again.');
      Object.assign(x, { nameEn: b.nameEn, nameAr: b.nameAr, status: b.status });
      branchVersions.set(bid, b.version + 1);
      if (b.status === 'inactive') servers.filter((s) => s.branchId === bid && s.role === 'branch').forEach((s) => { s.status = 'revoked'; s.online = false; });
      log('branch.updated', 'branch');
    },
    async createRegistrationToken(bid) {
      need('SERVERS.MANAGE');
      const b = branches.find((y) => y.id === bid)!;
      const tok: RegistrationTokenRow = { id: id(), branchId: bid, branchCode: b.code, expiresAt: iso(Date.now() + 864e5), createdAt: iso(Date.now()), usedAt: null, cancelledAt: null };
      regTokens.unshift(tok);
      log('server.registration_token_created', 'branch');
      return delay({ id: tok.id, token: 'kite-reg-token-0123456789', expiresAt: tok.expiresAt, fingerprintCode: '4821-0937-5520-1764-3086', hosts: ['192.168.10.5', 'ho-server'], port: 5443 });
    },
    async cancelRegistrationToken(tid) { need('SERVERS.MANAGE'); regTokens.find((x) => x.id === tid)!.cancelledAt = iso(Date.now()); },
    async revokeServer(sid) { need('SERVERS.MANAGE'); const x = servers.find((y) => y.id === sid)!; x.status = 'revoked'; x.online = false; log('server.revoked', 'server'); },
    async caRecoveryCopy(password, passphrase) {
      const u = need('SERVERS.MANAGE');
      if (passwords.get(u.username) !== password) throw problem(403, 'AUTH-001', 'Your password is not correct.');
      if (passphrase.length < 12) throw problem(400, 'VAL-001', 'The passphrase must have at least 12 characters.');
      return delay('-----BEGIN ENCRYPTED PRIVATE KEY-----\nMOCK\n-----END ENCRYPTED PRIVATE KEY-----\n');
    },
    async licenceStatus() {
      need();
      const admin = permsOf(current!).includes('LICENCE.MANAGE');
      return delay({ state: licence.state, daysLeft: licence.daysLeft, endsOn: licence.endsOn, validUntil: licence.terms?.validUntil ?? null, kind: licence.terms?.kind ?? null, canTransact: licence.canTransact,
        suspendMessage: licence.suspendMessage, suspendMessageAr: licence.suspendMessageAr, messages: (opts.messages ?? []).filter((m) => m.audience === 'all' || admin) });
    },
    async licence() { need('LICENCE.MANAGE'); return delay(structuredClone(licence)); },
    async importLicence(fileText) {
      need('LICENCE.MANAGE');
      let file: { type?: string; licence?: { id: string; mid: string; kind: 'trial' | 'subscription'; customer: string; valid_until: string; branches: number; terminals: number; modules: string[]; grace_days: number }; sig?: string };
      try { file = JSON.parse(fileText) as typeof file; } catch { throw problem(400, 'LIC-010', 'This is not a KITE MOBILE ERP licence file.'); }
      if (file.type !== 'KITE MOBILE ERP licence file' || !file.licence || !file.sig) throw problem(400, 'LIC-010', 'This is not a KITE MOBILE ERP licence file.');
      if (file.sig !== 'mock-valid-signature') throw problem(400, 'LIC-011', 'The licence signature is not valid. Use the file exactly as Nextdab sent it.');
      const l = file.licence;
      if (l.mid !== licence.installationId) throw problem(400, 'LIC-013', `This licence is for installation ${l.mid}; this installation is ${licence.installationId}.`);
      licence = { ...licence, state: 'active', canTransact: true, branchesAllowed: l.branches, terminalsAllowed: l.terminals, daysLeft: Math.round((Date.parse(l.valid_until) - Date.now()) / 864e5),
        terms: { id: l.id, installationId: l.mid, kind: l.kind, customer: l.customer, validUntil: l.valid_until, graceDays: l.grace_days, branchesAllowed: l.branches, terminalsAllowed: l.terminals, modules: l.modules, issuedAt: iso(Date.now()) },
        history: [{ id: l.id, kind: l.kind, customer: l.customer, validUntil: l.valid_until, source: 'import', importedAt: iso(Date.now()), isCurrent: true }, ...licence.history.map((h) => ({ ...h, isCurrent: false }))] };
      log('licence.imported', 'licence');
      return delay({ id: l.id, kind: l.kind, validUntil: l.valid_until });
    },
    async licenceCheckin() { need('LICENCE.MANAGE'); },
    async backups() {
      need('BACKUP.VIEW');
      const lastOk = (k: string) => runs.find((r) => r.kind === k && r.status === 'succeeded')?.finishedAt ?? null;
      return delay({
        configured: backupsConfigured, enabled: true, running: null, folder: backupsConfigured ? 'C:\\ProgramData\\KITE\\backups' : null, offsiteFolder: null,
        schedule: { logicalAt: '02:00', baseAt: '03:00', baseBackups: true, verifyDay: 'Sunday', verifyAt: '04:00' },
        lastLogicalOk: lastOk('logical'), lastBaseOk: lastOk('base'), lastVerifyOk: lastOk('verify'), lastOffsiteOk: null, alert: backupsConfigured && !lastOk('logical'), alertAfterHours: 26,
        wal: backupsConfigured ? { mode: 'on', archived: 312, lastArchivedAt: iso(Date.now() - 60_000), failed: 0, lastFailedAt: null } : null,
        keyProtection: 'dpapi-machine', recoveryKeyShownAt: null, runs: runs.map((r) => ({ ...r })),
      });
    },
    async runBackup(kind) {
      need('BACKUP.MANAGE');
      if (!backupsConfigured) throw problem(409, 'BAK-001', 'Backups are not configured: ConnectionStrings:Backup (the kite_backup login) is missing.');
      runs.unshift({ id: id(), kind, status: 'succeeded', trigger: 'manual', startedAt: iso(Date.now()), finishedAt: iso(Date.now() + 1000), fileName: kind === 'verify' ? null : 'kite-manual.kbak', sizeBytes: kind === 'verify' ? null : 18_500_000, sha256: null,
        checks: kind === 'verify' ? { restored: true, journals_balanced: true, outbox_gap_free: true, audit_chain_intact: true, audit_rows_checked: 413 } : null, error: null });
    },
    async backupRecoveryKey(password) {
      const u = need('BACKUP.MANAGE');
      if (passwords.get(u.username) !== password) throw problem(403, 'AUTH-001', 'Your password is not correct.');
      return delay({ recoveryKey: 'K4TE-7QXM-2PLA-90ZD-WW3R-HN6B-QF2C-UE8S-1MJK-RT5V-YA7G-PD4E-XB9N', keyId: '3f9a1c2b' });
    },
  };
  return api;
}
