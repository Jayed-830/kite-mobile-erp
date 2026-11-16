// In-memory Phase 5 device units for tests, `npm run dev:mock` and design review: units with IMEIs, conditions and
// grades, inspections with the shared checklist rules, refurbishment orders with parts, labour and outside work, and
// the till's lookups and leases. The real server checks everything again and is the source of truth.
import { ApiError, type BlockedImei, type CentralState, type Checklist, type ChecklistItemDef, type CountResult, type DeviceApi, type DeviceCondition, type DeviceCountDetail,
  type DeviceDetail, type DeviceEvent, type DeviceHold, type DeviceIdentityApi, type DeviceRow, type DeviceSettings, type DeviceStatus, type Grade, type ImeiConflict, type PosUnit,
  type RefurbCost, type RefurbOrderRow, type SerialFormat, type UnitInput, type User } from '@kite/api';
import { allowedConditions, classifyCode, evaluateInspection, labourCost, toBaisa, toNumber, unitListPrice } from '@kite/pos';

const problem = (status: number, code: string, title: string) => new ApiError({ status, code, title });
const id = () => crypto.randomUUID();
const now = () => new Date().toISOString();
const delay = <T>(v: T): Promise<T> => new Promise((r) => setTimeout(() => r(v), 15));
const num = (b: bigint) => toNumber(b);

export interface MockUnit {
  id: string; variantId: string; status: DeviceStatus; heldFrom: DeviceStatus | null; condition: DeviceCondition; gradeId: string | null; acquisition: bigint; cost: bigint;
  sellingPrice: number | null; warrantyDays: number | null; imei1: string | null; imei2: string | null; serial: string | null; receivedAt: string; soldSaleId: string | null;
  events: DeviceEvent[]; version: number;
  holds: DeviceHold[]; central: CentralState; retired: { kind: 'imei' | 'serial'; slot: number; value: string; reason: string; at: string }[];
  /** Bought under the profit margin scheme (Phase 7): the price it was bought for. */
  marginCost?: bigint | null;
}

export interface DeviceHooks {
  need: (perm?: string) => User;
  can: (u: User, perm: string) => boolean;
  item: (variantId: string) => { sku: string; descriptionEn: string; descriptionAr: string; productType: string; tracking: string; listPrice: number | null; minPrice: number | null; warrantyDays: number; categoryId: string | null };
  partMove: (variantId: string, qty: bigint) => bigint;          // issues (negative) or returns (positive) a spare part; returns the signed value
  partStock: (variantId: string) => bigint;
  next: (kind: string, prefix: string) => string;
  reservedBy: () => { terminalCode: string } | null;
  /** Phase 7: whether the profit margin scheme is approved, and the VAT-MARGIN code. */
  marginOn?: () => boolean;
  marginCodeId?: () => string;
}

const COND_EN: Record<DeviceCondition, string> = { brand_new: 'New', used: 'Used', refurbished: 'Refurbished', damaged: 'Damaged', for_parts: 'For parts' };
const COND_AR: Record<DeviceCondition, string> = { brand_new: 'جديد', used: 'مستعمل', refurbished: 'مجدد', damaged: 'به ضرر', for_parts: 'للقطع' };
const IN_STOCK: DeviceStatus[] = ['in_stock', 'under_inspection', 'returned_inspection', 'refurbishing', 'for_parts', 'supplier_return'];
const TRANSITIONS: Record<string, [DeviceStatus, DeviceStatus][]> = {
  inspected: (['under_inspection', 'returned_inspection'] as DeviceStatus[]).flatMap((f) => (['in_stock', 'refurbishing', 'for_parts', 'supplier_return'] as DeviceStatus[]).map((to) => [f, to] as [DeviceStatus, DeviceStatus])),
  cost_added: [['refurbishing', 'refurbishing']], refurb_completed: [['refurbishing', 'in_stock']], sold: [['in_stock', 'sold']], sale_voided: [['sold', 'in_stock']],
  returned: [['sold', 'returned_inspection']], written_off: [['in_stock', 'written_off'], ['for_parts', 'written_off']],
  price_set: (['in_stock', 'under_inspection', 'returned_inspection', 'refurbishing'] as DeviceStatus[]).map((s) => [s, s] as [DeviceStatus, DeviceStatus]),
  held: IN_STOCK.map((s) => [s, 'quarantined'] as [DeviceStatus, DeviceStatus]), released: IN_STOCK.map((s) => ['quarantined', s] as [DeviceStatus, DeviceStatus]),
  identifier_corrected: ([...IN_STOCK, 'quarantined', 'sold'] as DeviceStatus[]).map((s) => [s, s] as [DeviceStatus, DeviceStatus]),
  receipt_reversed: [['quarantined', 'reversed']],
  traded_in: [['sold', 'under_inspection']],
};

export const DEFAULT_CHECKLIST: ChecklistItemDef[] = [
  { code: 'screen', labelEn: 'Screen: no cracks, dead pixels or burn-in', labelAr: 'الشاشة: بلا تشققات أو بكسلات معطلة أو احتراق', kind: 'pass_fail', required: true, minPercent: null, onFail: 'refurbish' },
  { code: 'touch', labelEn: 'Touch works across the whole screen', labelAr: 'اللمس يعمل في كامل الشاشة', kind: 'pass_fail', required: true, minPercent: null, onFail: 'refurbish' },
  { code: 'body', labelEn: 'Body and frame: no dents or cracks', labelAr: 'الهيكل والإطار: بلا انبعاجات أو تشققات', kind: 'pass_fail', required: true, minPercent: null, onFail: 'damaged' },
  { code: 'buttons', labelEn: 'Buttons: power, volume, silent switch', labelAr: 'الأزرار: التشغيل والصوت ومفتاح الصامت', kind: 'pass_fail', required: true, minPercent: null, onFail: 'refurbish' },
  { code: 'cameras', labelEn: 'Front and rear cameras and flash', labelAr: 'الكاميرات الأمامية والخلفية والفلاش', kind: 'pass_fail', required: true, minPercent: null, onFail: 'refurbish' },
  { code: 'audio', labelEn: 'Speakers, earpiece and microphones', labelAr: 'مكبرات الصوت وسماعة المكالمات والميكروفونات', kind: 'pass_fail', required: true, minPercent: null, onFail: 'refurbish' },
  { code: 'charging', labelEn: 'Charging port and charging', labelAr: 'منفذ الشحن والشحن', kind: 'pass_fail', required: true, minPercent: null, onFail: 'refurbish' },
  { code: 'network', labelEn: 'SIM, mobile network, Wi-Fi and Bluetooth', labelAr: 'الشريحة والشبكة والواي فاي والبلوتوث', kind: 'pass_fail', required: true, minPercent: null, onFail: 'refurbish' },
  { code: 'biometrics', labelEn: 'Face ID or fingerprint', labelAr: 'بصمة الوجه أو الإصبع', kind: 'pass_fail', required: true, minPercent: null, onFail: 'damaged' },
  { code: 'water', labelEn: 'Water-damage indicator not triggered', labelAr: 'مؤشر التلف بالماء غير مُفعَّل', kind: 'pass_fail', required: true, minPercent: null, onFail: 'for_parts' },
  { code: 'battery', labelEn: 'Battery health (%)', labelAr: 'صحة البطارية (%)', kind: 'percent', required: true, minPercent: 80, onFail: 'refurbish' },
  { code: 'cloud_lock', labelEn: 'Cloud account and activation lock removed in front of staff', labelAr: 'حساب السحابة وقفل التفعيل مُزالان أمام الموظف', kind: 'pass_fail', required: true, minPercent: null, onFail: 'for_parts' },
  { code: 'notes', labelEn: 'Cosmetic notes', labelAr: 'ملاحظات المظهر', kind: 'text', required: false, minPercent: null, onFail: 'refurbish' },
];

export function createMockDevices(h: DeviceHooks) {
  const units: MockUnit[] = [];
  const grades: Grade[] = [
    { id: id(), categoryId: null, categoryName: null, code: 'A', nameEn: 'Like new', nameAr: 'كالجديد', descriptionEn: null, descriptionAr: null, pricePercent: 85, sort: 0, status: 'active', version: 1 },
    { id: id(), categoryId: null, categoryName: null, code: 'B', nameEn: 'Good', nameAr: 'جيد', descriptionEn: null, descriptionAr: null, pricePercent: 70, sort: 1, status: 'active', version: 1 },
    { id: id(), categoryId: null, categoryName: null, code: 'C', nameEn: 'Fair', nameAr: 'مقبول', descriptionEn: null, descriptionAr: null, pricePercent: 55, sort: 2, status: 'active', version: 1 },
  ];
  const checklist: Checklist = { id: id(), categoryId: null, categoryName: null, nameEn: 'Phone inspection', nameAr: 'فحص الهاتف', status: 'active', version: 1, items: structuredClone(DEFAULT_CHECKLIST) };
  let settings: DeviceSettings = { labourRatePerHour: 3, reservationMinutes: 15, parkedReservationMinutes: 240, suggestedPriceStep: 0.1, version: 1 };
  const inspections: DeviceDetail['inspections'][number][] & { unitId?: string }[] = [];
  const inspectionUnit = new Map<string, string>();
  const orders: (RefurbOrderRow & { gradeId: string | null })[] = [];
  const costs: (RefurbCost & { orderId: string; partVariantId: string | null })[] = [];
  const leases = new Map<string, { cartId: string; until: number; terminalCode: string }>();
  const unitSales = new Map<string, DeviceDetail['sales']>();

  const unit = (uid: string) => { const u = units.find((x) => x.id === uid); if (!u) throw problem(404, 'DEV-003', 'That device is not in your branches.'); return u; };
  const grade = (gid: string | null) => grades.find((g) => g.id === gid) ?? null;
  const ident = (u: MockUnit) => u.imei1 ?? u.serial ?? u.id;
  const event = (u: MockUnit, type: string, to: DeviceStatus, cond: DeviceCondition, extra: Partial<DeviceEvent> = {}) => {
    const allowed = type === 'received' || (TRANSITIONS[type] ?? []).some(([f, t]) => f === u.status && t === to);
    if (!allowed) throw problem(409, 'DEV-006', `A ${u.status.replace(/_/g, ' ')} device cannot be ${type.replace(/_/g, ' ')}.`);
    u.events.push({ seq: u.events.length + 1, eventType: type, fromStatus: type === 'received' ? null : u.status, toStatus: to, fromCondition: type === 'received' ? null : u.condition, toCondition: cond,
      gradeCode: grade(u.gradeId)?.code ?? null, costDelta: 0, sellingPrice: u.sellingPrice, docType: null, docId: null, docNumber: null, details: null, occurredAt: now(), actorName: 'Mock', ...extra });
    u.heldFrom = to !== 'quarantined' ? null : u.status === 'quarantined' ? u.heldFrom : u.status;
    u.status = to; u.condition = cond; u.version++;
  };
  const row = (u: MockUnit): DeviceRow => {
    const i = h.item(u.variantId); const g = grade(u.gradeId);
    return { id: u.id, branchCode: 'B01', variantId: u.variantId, sku: i.sku, descriptionEn: i.descriptionEn, descriptionAr: i.descriptionAr, productType: i.productType, status: u.status,
      heldFromStatus: u.heldFrom, condition: u.condition, gradeCode: g?.code ?? null, gradeNameEn: g?.nameEn ?? null, gradeNameAr: g?.nameAr ?? null, acquisitionCost: num(u.acquisition),
      accumulatedCost: num(u.cost), sellingPrice: u.sellingPrice, imei1: u.imei1, imei2: u.imei2, serial: u.serial, receivedAt: u.receivedAt,
      ageDays: Math.floor((Date.now() - Date.parse(u.receivedAt)) / 864e5), version: u.version, centralState: u.central };
  };
  const listPrice = (u: MockUnit) => {
    const i = h.item(u.variantId);
    const p = unitListPrice(u.sellingPrice, u.condition, i.listPrice, grade(u.gradeId)?.pricePercent ?? null, settings.suggestedPriceStep);
    return p == null ? null : Number(p);
  };
  const posUnit = (u: MockUnit): PosUnit => {
    const i = h.item(u.variantId); const g = grade(u.gradeId);
    const suffix = u.condition === 'brand_new' ? ['', ''] : [` · ${COND_EN[u.condition]}${g ? ` ${g.code}` : ''}`, ` · ${COND_AR[u.condition]}${g ? ` ${g.code}` : ''}`];
    const lease = leases.get(u.id);
    return { unitId: u.id, variantId: u.variantId, sku: i.sku, productType: i.productType, tracking: i.tracking, descriptionEn: i.descriptionEn + suffix[0], descriptionAr: i.descriptionAr + suffix[1],
      taxRate: 5, listPrice: listPrice(u), minPrice: u.condition === 'brand_new' ? i.minPrice : null, condition: u.condition, gradeCode: g?.code ?? null, imei1: u.imei1, imei2: u.imei2, serial: u.serial,
      warrantyDays: u.warrantyDays ?? i.warrantyDays, reservedUntil: lease && lease.until > Date.now() ? new Date(lease.until).toISOString() : null, centralState: u.central,
      ...(u.marginCost != null && h.marginOn?.() ? { marginCost: num(u.marginCost), taxCodeId: h.marginCodeId?.() ?? null } : {}) };
  };
  const sellable = (u: MockUnit) => {
    if (u.status !== 'in_stock') throw problem(409, 'DEV-004', `${ident(u)} is ${u.status.replace(/_/g, ' ')}, so it cannot be sold.`);
    if (u.central === 'pending' && settings.strictCentralCheck) throw problem(409, 'DEV-015', `${ident(u)} is waiting for head office to confirm its IMEI. It can be sold once confirmed.`);
  };
  const find = (code: string) => {
    const c = classifyCode(code);
    if (c.kind === 'invalid_imei') throw problem(400, 'DEV-001', c.value.length === 15 ? `${c.value} is not a valid IMEI: the check digit is wrong. Scan it again.` : `${c.value} is not an IMEI: an IMEI has 15 digits.`);
    if (c.kind === 'unknown') throw problem(400, 'DEV-003', 'Scan or type an IMEI (15 digits) or a serial number.');
    return units.find((u) => u.imei1 === c.value || u.imei2 === c.value || u.serial === c.value) ?? null;
  };

  // ---- Phase 6: holds with reasons, IMEI conflicts, the blocked list, model hints, serial formats, counts
  const blocked: BlockedImei[] = [];
  const conflicts: ImeiConflict[] = [];
  const tac = new Map<string, { brand: string; model: string }>();
  const formats: SerialFormat[] = [{ id: id(), nameEn: 'Apple', nameAr: 'أبل', serialPattern: null }, { id: id(), nameEn: 'Samsung', nameAr: 'سامسونج', serialPattern: null }];
  const counts: (DeviceCountDetail['count'] & { scans: DeviceCountDetail['scans'] })[] = [];
  const open = (u: MockUnit) => u.holds.filter((x) => !x.releasedAt);
  const hold = (u: MockUnit, reason: DeviceHold['reason'], conflictId: string | null, blockedId: string | null, note: string) => {
    if (open(u).some((x) => x.reason === reason && x.conflictId === conflictId && x.blockedId === blockedId)) return;
    if (reason === 'manager' && open(u).some((x) => x.reason === 'manager')) throw problem(409, 'DEV-018', 'A manager already put this device on hold.');
    if (u.status !== 'quarantined' && !IN_STOCK.includes(u.status)) throw problem(409, 'DEV-006', `A ${u.status.replace(/_/g, ' ')} device cannot be put on hold.`);
    u.holds.push({ id: id(), reason, conflictId, blockedId, note, heldAt: now(), releasedAt: null, releaseNote: null });
    if (reason === 'imei_conflict') u.central = 'conflict';
    if (u.status !== 'quarantined') event(u, 'held', 'quarantined', u.condition, { details: JSON.stringify({ reason, note }) });
  };
  const release = (u: MockUnit, reason: DeviceHold['reason'], ref: string | null, note: string) => {
    for (const x of open(u).filter((x) => x.reason === reason && (!ref || x.conflictId === ref || x.blockedId === ref))) { x.releasedAt = now(); x.releaseNote = note; }
    if (reason === 'imei_conflict' && !open(u).some((x) => x.reason === 'imei_conflict')) u.central = 'verified';
    if (u.status === 'quarantined' && open(u).length === 0) event(u, 'released', u.heldFrom ?? 'in_stock', u.condition, { details: JSON.stringify({ reason, note }) });
  };
  /** A conflict found at head office with another branch's device (for design review and tests of the screens). */
  const seedConflict = (uid: string, otherBranch: string) => {
    const u = unit(uid); const i = h.item(u.variantId);
    const c: ImeiConflict = { id: id(), kind: 'imei', value: u.imei1, status: 'open', openedAt: now(), resolvedAt: null, resolution: null, note: null, sides: [
      { unitId: u.id, branchCode: 'B01', branchNameEn: 'Muscat - Ruwi', branchNameAr: 'مسقط - روي', descriptionEn: i.descriptionEn, descriptionAr: i.descriptionAr, unitStatus: 'in_stock', receivedAt: u.receivedAt, outcome: null, outcomeAt: null, local: true, rowStatus: 'active' },
      { unitId: id(), branchCode: otherBranch, branchNameEn: 'Sohar', branchNameAr: 'صحار', descriptionEn: i.descriptionEn, descriptionAr: i.descriptionAr, unitStatus: 'in_stock', receivedAt: now(), outcome: null, outcomeAt: null, local: false, rowStatus: 'conflict' },
    ] };
    conflicts.push(c);
    hold(u, 'imei_conflict', c.id, null, `IMEI ${u.imei1} is also registered: in stock at ${otherBranch} (${i.descriptionEn}).`);
    return c;
  };
  const settle = (u: MockUnit, outcome: 'corrected' | 'reversed', releaseUnit = true) => {
    for (const c of conflicts.filter((c) => c.status === 'open' && c.sides.some((x) => x.unitId === u.id))) {
      c.status = 'resolved'; c.resolvedAt = now(); c.resolution = `Only the device at ${c.sides.find((x) => x.unitId !== u.id)?.branchCode ?? 'B01'} claims it now.`;
      for (const x of c.sides) { x.outcome = x.unitId === u.id ? outcome : 'kept'; x.outcomeAt = now(); }
      if (releaseUnit) release(u, 'imei_conflict', c.id, 'The IMEI conflict is resolved.');
    }
  };

  const receive = (variantId: string, input: UnitInput, fallbackCost: number | null | undefined) => {
    const i = h.item(variantId);
    const imei1 = input.imei1 ? classifyCode(input.imei1) : null;
    if (i.tracking === 'imei' && (!imei1 || imei1.kind !== 'imei')) throw problem(400, imei1 ? 'DEV-001' : 'DEV-002', imei1 ? `${imei1.value} is not a valid IMEI.` : "Scan the device's IMEI.");
    if (imei1 && units.some((u) => u.imei1 === imei1.value || u.imei2 === imei1.value)) throw problem(409, 'DEV-007', `IMEI ${imei1.value} already belongs to a device at B01.`);
    if (imei1 && blocked.some((b) => b.status === 'active' && b.value === imei1.value)) throw problem(409, 'DEV-014', `IMEI ${imei1.value} is on the blocked list. Do not take this device in.`);
    const condition = input.condition ?? 'brand_new';
    const status = input.status ?? (condition === 'brand_new' ? 'in_stock' : 'under_inspection');
    const cost = toBaisa(input.unitCost ?? fallbackCost ?? 0);
    const u: MockUnit = { id: id(), variantId, status, heldFrom: null, condition, gradeId: input.gradeId ?? null, acquisition: cost, cost, sellingPrice: input.sellingPrice ?? null,
      warrantyDays: input.warrantyDays ?? null, imei1: imei1?.value ?? null, imei2: input.imei2 ? classifyCode(input.imei2).value : null, serial: input.serial ? classifyCode(input.serial).value : null,
      receivedAt: now(), soldSaleId: null, events: [], version: 1, holds: [], central: 'verified', retired: [] };
    event(u, 'received', status, condition);
    units.push(u);
    return u;
  };

  const api: DeviceApi = {
    async devices(f) {
      h.need('DEVICE.VIEW');
      const statuses = f.status?.split(',');
      const q = f.q?.trim().toLowerCase();
      return delay(units.filter((u) => (!statuses || statuses.includes(u.status)) && (!f.condition || u.condition === f.condition) && (!f.variantId || u.variantId === f.variantId) && (!f.central || u.central === f.central)
        && (!q || [u.imei1, u.imei2, u.serial, h.item(u.variantId).sku, h.item(u.variantId).descriptionEn].some((x) => x?.toLowerCase().includes(q)))).map(row).reverse());
    },
    async deviceLookup(code) { h.need('DEVICE.VIEW'); const u = find(code); if (!u) throw problem(404, 'DEV-003', `No device with ${code} in your branches.`); return delay({ id: u.id }); },
    async device(uid) {
      h.need('DEVICE.VIEW');
      const u = unit(uid); const i = h.item(u.variantId); const g = grade(u.gradeId);
      const mine = costs.filter((c) => orders.find((o) => o.id === c.orderId)?.unitId === uid);
      const sumOf = (k: string) => num(mine.filter((c) => c.kind === k).reduce((a, c) => a + toBaisa(c.amount), 0n));
      return delay({
        unit: { id: u.id, branchId: 'b', branchCode: 'B01', variantId: u.variantId, sku: i.sku, descriptionEn: i.descriptionEn, descriptionAr: i.descriptionAr, productType: i.productType, tracking: i.tracking,
          categoryId: i.categoryId, status: u.status, heldFromStatus: u.heldFrom, condition: u.condition, gradeId: u.gradeId, gradeCode: g?.code ?? null, gradeNameEn: g?.nameEn ?? null, gradeNameAr: g?.nameAr ?? null,
          imei1: u.imei1, imei2: u.imei2, serial: u.serial, receivedAt: u.receivedAt, sellingPrice: u.sellingPrice, warrantyDays: u.warrantyDays, defaultWarrantyDays: i.warrantyDays, version: u.version },
        cost: { acquisition: num(u.acquisition), parts: sumOf('part'), labour: sumOf('labour'), external: sumOf('external'), total: num(u.cost) },
        price: { newPrice: i.listPrice, gradePercent: g?.pricePercent ?? null, suggested: unitListPrice(null, 'used', i.listPrice, g?.pricePercent ?? null, settings.suggestedPriceStep) == null ? null : Number(unitListPrice(null, 'used', i.listPrice, g?.pricePercent ?? null, settings.suggestedPriceStep)), listPrice: listPrice(u) },
        events: structuredClone(u.events), inspections: inspections.filter((x) => inspectionUnit.get(x.id) === uid), refurbishments: orders.filter((o) => o.unitId === uid).map((o) => ({ id: o.id, number: o.number, status: o.status, problem: o.problem, workDone: o.workDone, openedAt: o.openedAt, completedAt: o.completedAt, technicianName: o.technicianName, total: o.costTotal })),
        sales: unitSales.get(uid) ?? [],
        holds: structuredClone(u.holds).reverse(),
        identifiers: [
          ...(u.imei1 ? [{ kind: 'imei' as const, slot: 1, value: u.imei1, entry: 'scan' as const, status: 'active' as const, createdAt: u.receivedAt, retiredAt: null, retiredReason: null }] : []),
          ...(u.imei2 ? [{ kind: 'imei' as const, slot: 2, value: u.imei2, entry: 'scan' as const, status: 'active' as const, createdAt: u.receivedAt, retiredAt: null, retiredReason: null }] : []),
          ...(u.serial ? [{ kind: 'serial' as const, slot: 1, value: u.serial, entry: 'scan' as const, status: 'active' as const, createdAt: u.receivedAt, retiredAt: null, retiredReason: null }] : []),
          ...u.retired.map((r) => ({ kind: r.kind, slot: r.slot, value: r.value, entry: 'scan' as const, status: 'retired' as const, createdAt: u.receivedAt, retiredAt: r.at, retiredReason: r.reason })),
        ],
        central: { state: u.central, checkedAt: u.receivedAt, attempts: 0, lastError: null },
      });
    },
    async setDevicePrice(uid, p) { h.need('DEVICE.PRICE'); const u = unit(uid); if (u.version !== p.version) throw problem(412, 'GEN-412', 'Someone else changed this device. Reload and try again.'); u.sellingPrice = p.sellingPrice; if (p.warrantyDays != null) u.warrantyDays = p.warrantyDays; event(u, 'price_set', u.status, u.condition); },
    async holdDevice(uid, reason) { h.need('DEVICE.HOLD'); hold(unit(uid), 'manager', null, null, reason); },
    async releaseDevice(uid, reason) {
      h.need('DEVICE.HOLD'); const u = unit(uid);
      if (open(u).some((x) => x.reason !== 'manager')) throw problem(409, 'DEV-018', 'This device stays on hold because the same IMEI is registered to another device (an IMEI conflict at head office). It is released when that is settled.');
      release(u, 'manager', null, reason);
    },
    async writeOffDevice(uid) { h.need('DEVICE.WRITE_OFF'); const u = unit(uid); const c = num(u.cost); event(u, 'written_off', 'written_off', u.condition); return delay({ id: id(), number: h.next('adj', 'ADJ'), cost: c }); },
    async deviceSettings() { h.need(); return delay({ ...settings }); },
    async saveDeviceSettings(s) { h.need('DEVICE.SETTINGS'); if (s.version !== settings.version) throw problem(412, 'GEN-412', 'Someone else changed the device settings.'); settings = { ...s, version: settings.version + 1 }; },
    async grades() { h.need(); return delay(grades.map((g) => ({ ...g }))); },
    async createGrade(g) { h.need('DEVICE.SETTINGS'); const x: Grade = { id: id(), categoryId: g.categoryId ?? null, categoryName: null, code: g.code.toUpperCase(), nameEn: g.nameEn, nameAr: g.nameAr, descriptionEn: g.descriptionEn ?? null, descriptionAr: g.descriptionAr ?? null, pricePercent: g.pricePercent, sort: g.sort ?? grades.length, status: 'active', version: 1 }; grades.push(x); return delay({ id: x.id }); },
    async updateGrade(gid, g) { h.need('DEVICE.SETTINGS'); const x = grades.find((y) => y.id === gid)!; if (x.version !== g.version) throw problem(412, 'GEN-412', 'That grade was changed.'); Object.assign(x, { code: g.code.toUpperCase(), nameEn: g.nameEn, nameAr: g.nameAr, pricePercent: g.pricePercent, status: g.status ?? x.status, version: x.version + 1 }); },
    async checklists() { h.need(); return delay([structuredClone(checklist)]); },
    async deviceChecklist() { h.need('DEVICE.VIEW'); return delay({ checklistId: checklist.id, items: checklist.items.map((i) => ({ ...i })), grades: grades.filter((g) => g.status === 'active').map((g) => ({ id: g.id, code: g.code, nameEn: g.nameEn, nameAr: g.nameAr, pricePercent: g.pricePercent })) }); },
    async createChecklist(c) { h.need('DEVICE.SETTINGS'); return delay({ id: c.categoryId ?? id() }); },
    async updateChecklist(_cid, c) { h.need('DEVICE.SETTINGS'); if (c.version !== checklist.version) throw problem(412, 'GEN-412', 'That checklist was changed.'); Object.assign(checklist, { nameEn: c.nameEn, nameAr: c.nameAr, items: structuredClone(c.items), version: checklist.version + 1 }); },
    async posUnitByCode(code, variantId) {
      h.need('POS.SELL');
      const chosen = variantId ? find(code) : null;
      if (chosen && variantId && chosen.variantId !== variantId) throw problem(409, 'DEV-016', `${ident(chosen)} is a ${h.item(chosen.variantId).descriptionEn}, not the ${h.item(variantId).descriptionEn} you chose. Check the device in your hand.`);
      const c = classifyCode(code);
      const u = find(code);
      if (!u) throw problem(404, 'DEV-003', `No device with ${c.kind === 'imei' ? 'IMEI' : 'serial number'} ${c.value} in this business.`);
      sellable(u);
      return delay(posUnit(u));
    },
    async posUnits(variantId) { h.need('POS.SELL'); return delay(units.filter((u) => u.variantId === variantId && u.status === 'in_stock').map(posUnit)); },
    async reserveUnit(uid, cartId) {
      const u = unit(uid); sellable(u);
      const lease = leases.get(uid);
      if (lease && lease.cartId !== cartId && lease.until > Date.now()) throw problem(409, 'DEV-005', `${ident(u)} is in a cart at till ${lease.terminalCode}.`);
      const until = Date.now() + settings.reservationMinutes * 60_000;
      leases.set(uid, { cartId, until, terminalCode: h.reservedBy()?.terminalCode ?? 'T01' });
      return delay({ unitId: uid, reservedUntil: new Date(until).toISOString() });
    },
    async releaseUnit(uid, cartId) { if (leases.get(uid)?.cartId === cartId) leases.delete(uid); },
    async inspectionQueue() {
      h.need('DEVICE.INSPECT');
      return delay(units.filter((u) => u.status === 'under_inspection' || u.status === 'returned_inspection').map((u) => {
        const i = h.item(u.variantId);
        return { id: u.id, branchCode: 'B01', sku: i.sku, descriptionEn: i.descriptionEn, descriptionAr: i.descriptionAr, status: u.status, condition: u.condition, imei1: u.imei1, serial: u.serial, accumulatedCost: num(u.cost), receivedAt: u.receivedAt };
      }));
    },
    async inspect(uid, req) {
      const me = h.need('DEVICE.INSPECT');
      const u = unit(uid);
      if (u.status !== 'under_inspection' && u.status !== 'returned_inspection') throw problem(409, 'DEV-006', `${ident(u)} is not waiting for inspection.`);
      const r = evaluateInspection(checklist.items, req.answers, u.condition);
      if (!r.complete) throw problem(400, 'DEV-011', `Answer every checklist item first: ${checklist.items.filter((i) => r.missing.includes(i.code)).map((i) => i.labelEn).join('; ')}.`);
      if (req.outcome !== r.outcome && !req.note?.trim()) throw problem(400, 'DEV-011', `The checklist suggests ${r.outcome.replace('_', ' ')}; say why you decided otherwise.`);
      if (!allowedConditions(req.outcome, u.condition).includes(req.condition)) throw problem(400, 'VAL-001', 'That condition does not fit the decision.');
      if (req.sellingPrice != null && !h.can(me, 'DEVICE.PRICE')) throw problem(403, 'AUTH-403', "Setting a device's price needs the DEVICE.PRICE permission.");
      const number = h.next('ins', 'INS');
      const inspId = id();
      const to: DeviceStatus = req.outcome === 'restock' ? 'in_stock' : req.outcome === 'refurbish' ? 'refurbishing' : req.outcome === 'for_parts' ? 'for_parts' : 'supplier_return';
      u.gradeId = req.gradeId ?? null;
      if (req.sellingPrice != null || req.condition !== u.condition) u.sellingPrice = req.sellingPrice ?? null;
      event(u, 'inspected', to, req.condition, { docType: 'device_inspection', docId: inspId, docNumber: number });
      inspections.push({ id: inspId, number, reason: 'intake', outcome: req.outcome, suggestedOutcome: r.outcome, resultCondition: req.condition, gradeCode: grade(u.gradeId)?.code ?? null, batteryHealth: r.battery, note: req.note ?? null, results: JSON.stringify(req.answers), inspectedAt: now(), inspectedBy: me.displayNameEn });
      inspectionUnit.set(inspId, uid);
      let orderId: string | null = null; let orderNumber: string | null = null;
      if (req.outcome === 'refurbish') {
        orderId = id(); orderNumber = h.next('rf', 'RF');
        const i = h.item(u.variantId);
        orders.push({ id: orderId, number: orderNumber, status: 'open', branchId: 'b', unitId: uid, inspectionId: inspId, technicianId: null, technicianName: null,
          problem: checklist.items.filter((x) => r.failed.includes(x.code)).map((x) => x.labelEn).join('; ') || (req.note ?? null), workDone: null, openedAt: now(), completedAt: null,
          resultCondition: null, gradeCode: null, gradeId: null, sellingPrice: null, warrantyDays: null, version: 1, costTotal: 0, unitStatus: u.status, unitCondition: u.condition, unitCost: num(u.cost),
          sku: i.sku, identifier: ident(u), descriptionEn: i.descriptionEn, descriptionAr: i.descriptionAr });
      }
      return delay({ id: inspId, number, outcome: req.outcome, suggestedOutcome: r.outcome, failed: r.failed, refurbOrderId: orderId, refurbOrderNumber: orderNumber });
    },
    async refurbishments(f) {
      h.need('DEVICE.VIEW');
      return delay(orders.filter((o) => !f?.status || o.status === f.status).map((o) => { const u = unit(o.unitId); return { ...o, unitStatus: u.status, unitCondition: u.condition, unitCost: num(u.cost) }; }).reverse());
    },
    async refurbishment(oid) {
      h.need('DEVICE.VIEW');
      const o = orders.find((x) => x.id === oid); if (!o) throw problem(404, 'GEN-404', 'Refurbishment order not found.');
      const u = unit(o.unitId);
      return delay({ order: { ...o, unitStatus: u.status, unitCondition: u.condition, unitCost: num(u.cost) }, costs: costs.filter((c) => c.orderId === oid).map((c) => ({ ...c, reversed: costs.some((x) => x.reversalOf === c.id) })) });
    },
    async updateRefurbishment(oid, r) { h.need('DEVICE.REFURBISH'); const o = orders.find((x) => x.id === oid)!; if (o.version !== r.version) throw problem(412, 'GEN-412', 'Someone else changed this order.'); Object.assign(o, { technicianId: r.technicianId, problem: r.problem, workDone: r.workDone, version: o.version + 1 }); },
    async addRefurbCost(oid, c) {
      const me = h.need('DEVICE.REFURBISH');
      const o = orders.find((x) => x.id === oid)!;
      if (o.status !== 'open') throw problem(409, 'DEV-010', `Refurbishment ${o.number} is completed.`);
      const u = unit(o.unitId);
      let amount: bigint; const row: Partial<RefurbCost> & { partVariantId: string | null } = { partVariantId: null };
      if (c.kind === 'part') {
        if (!c.variantId || !c.qty || c.qty <= 0) throw problem(400, 'VAL-001', 'Choose the part and its quantity.');
        if (h.partStock(c.variantId) < toBaisa(c.qty)) throw problem(409, 'INV-001', 'Not enough of that part in stock.');
        amount = -h.partMove(c.variantId, -toBaisa(c.qty));
        const pi = h.item(c.variantId);
        Object.assign(row, { qty: c.qty, unitCost: num(amount) / c.qty, partVariantId: c.variantId, partSku: pi.sku, partDescriptionEn: pi.descriptionEn, partDescriptionAr: pi.descriptionAr });
      } else if (c.kind === 'labour') {
        if (!c.minutes || c.minutes <= 0) throw problem(400, 'VAL-001', 'Labour is 1 to 6000 minutes.');
        amount = toBaisa(labourCost(c.minutes, settings.labourRatePerHour));
        Object.assign(row, { minutes: c.minutes, rate: settings.labourRatePerHour, technicianName: me.displayNameEn });
      } else {
        if (!c.vendor?.trim()) throw problem(400, 'VAL-001', 'Name who did the work.');
        if (!c.amount || c.amount <= 0) throw problem(400, 'VAL-001', 'The amount is above zero, in baisa.');
        amount = toBaisa(c.amount);
        Object.assign(row, { vendor: c.vendor, reference: c.reference ?? null });
      }
      u.cost += amount;
      event(u, 'cost_added', u.status, u.condition, { costDelta: num(amount), docType: 'refurb_order', docId: oid, docNumber: o.number });
      const cost = { id: id(), kind: c.kind, amount: num(amount), qty: null, unitCost: null, minutes: null, rate: null, vendor: null, reference: null, note: c.note ?? null, reversalOf: null, reversed: false,
        partSku: null, partDescriptionEn: null, partDescriptionAr: null, technicianName: null, createdBy: me.displayNameEn, createdAt: now(), orderId: oid, ...row } as RefurbCost & { orderId: string; partVariantId: string | null };
      costs.push(cost);
      o.costTotal = num(toBaisa(o.costTotal) + amount);
      return delay({ id: cost.id, amount: num(amount), unitCost: num(u.cost) });
    },
    async reverseRefurbCost(oid, costId, reason) {
      const me = h.need('DEVICE.REFURBISH');
      const o = orders.find((x) => x.id === oid)!;
      if (o.status !== 'open') throw problem(409, 'DEV-010', `Refurbishment ${o.number} is completed.`);
      const c = costs.find((x) => x.id === costId)!;
      if (costs.some((x) => x.reversalOf === costId)) throw problem(409, 'GEN-409', 'That cost was already taken back.');
      if (!reason.trim()) throw problem(400, 'VAL-001', 'Say why the cost is taken back.');
      const u = unit(o.unitId);
      const amount = toBaisa(c.amount);
      if (c.kind === 'part' && c.partVariantId) h.partMove(c.partVariantId, toBaisa(c.qty ?? 0));
      u.cost -= amount;
      event(u, 'cost_added', u.status, u.condition, { costDelta: -c.amount, docType: 'refurb_order', docId: oid, docNumber: o.number });
      costs.push({ ...c, id: id(), amount: -c.amount, reversalOf: costId, note: reason, createdBy: me.displayNameEn, createdAt: now() });
      o.costTotal = num(toBaisa(o.costTotal) - amount);
      return delay({ id: id(), amount: -c.amount, unitCost: num(u.cost) });
    },
    async completeRefurbishment(oid, c) {
      h.need('DEVICE.REFURBISH');
      const o = orders.find((x) => x.id === oid)!;
      const u = unit(o.unitId);
      u.gradeId = c.gradeId; if (c.sellingPrice != null) u.sellingPrice = c.sellingPrice; if (c.warrantyDays != null) u.warrantyDays = c.warrantyDays;
      event(u, 'refurb_completed', 'in_stock', c.condition, { docType: 'refurb_order', docId: oid, docNumber: o.number });
      Object.assign(o, { status: 'completed', completedAt: now(), resultCondition: c.condition, gradeId: c.gradeId, gradeCode: grade(c.gradeId)?.code ?? null, sellingPrice: c.sellingPrice ?? null, workDone: c.workDone ?? o.workDone, version: o.version + 1 });
      return delay({ id: oid, number: o.number, unitId: u.id, cost: num(u.cost) });
    },
    async deviceStockReport() {
      h.need('DEVICE.VIEW');
      const groups = new Map<string, { status: DeviceStatus; condition: DeviceCondition; gradeCode: string | null; units: number; cost: bigint; age: number }>();
      for (const u of units.filter((x) => IN_STOCK.includes(x.status))) {
        const k = `${u.status}|${u.condition}|${grade(u.gradeId)?.code ?? ''}`;
        const g = groups.get(k) ?? { status: u.status, condition: u.condition, gradeCode: grade(u.gradeId)?.code ?? null, units: 0, cost: 0n, age: 0 };
        g.units++; g.cost += u.cost; groups.set(k, g);
      }
      return delay([...groups.values()].map((g) => ({ status: g.status, condition: g.condition, gradeCode: g.gradeCode, units: g.units, cost: num(g.cost), averageAgeDays: 0, age0to30: g.units, age31to90: 0, ageOver90: 0 })));
    },
    async deviceProfitReport() {
      h.need('DEVICE.VIEW');
      return delay([...unitSales].flatMap(([uid, ss]) => ss.filter((s, i) => s.docType !== 'credit_note' && !ss.slice(i + 1).some((x) => x.docType === 'credit_note')).map((s) => ({ number: s.number, saleDate: s.completedAt.slice(0, 10), unitId: uid, descriptionEn: h.item(unit(uid).variantId).descriptionEn, identifier: ident(unit(uid)), net: s.net, cost: s.cost, profit: s.profit ?? 0, returned: 0 }))));
    },
    async refurbishmentReport() {
      h.need('DEVICE.VIEW');
      const sumOf = (k: string) => num(costs.filter((c) => c.kind === k).reduce((a, c) => a + toBaisa(c.amount), 0n));
      return delay(orders.length ? [{ technician: null, orders: orders.length, completed: orders.filter((o) => o.status === 'completed').length, parts: sumOf('part'), labour: sumOf('labour'), external: sumOf('external'), total: num(costs.reduce((a, c) => a + toBaisa(c.amount), 0n)) }] : []);
    },
  };

  const countDetail = (cid: string): DeviceCountDetail => {
    const c = counts.find((x) => x.id === cid);
    if (!c) throw problem(404, 'GEN-404', 'That count is not in your branches.');
    const expected = units.filter((u) => IN_STOCK.includes(u.status === 'quarantined' ? (u.heldFrom ?? 'in_stock') : u.status) && (!c.variantId || u.variantId === c.variantId))
      .map((u) => ({ id: u.id, descriptionEn: h.item(u.variantId).descriptionEn, descriptionAr: h.item(u.variantId).descriptionAr, status: u.status, heldFromStatus: u.heldFrom, condition: u.condition, cost: num(u.cost), identifier: u.imei1 ?? u.serial }));
    const here = new Set(c.scans.filter((x) => x.result === 'here').map((x) => x.unitId));
    const { scans, ...count } = c;
    return { count, expected, scans: [...scans].reverse(), found: expected.filter((e) => here.has(e.id)).length, missing: expected.filter((e) => !here.has(e.id)), extra: scans.filter((x) => x.result !== 'here') };
  };

  const identity: DeviceIdentityApi = {
    async checkIdentifiers(c) {
      h.need('DEVICE.VIEW');
      const item = c.variantId ? h.item(c.variantId) : null;
      return delay(c.codes.map((code) => {
        const k = classifyCode(code);
        const r = { code, kind: k.kind, value: k.value, problem: null as { code: string; message: string } | null, hint: null as string | null, hintMismatch: false, centralChecked: true };
        if (k.kind === 'invalid_imei') r.problem = { code: 'DEV-001', message: k.value.length === 15 ? `${k.value} is not a valid IMEI: the check digit is wrong.` : `${k.value} is not an IMEI: an IMEI has 15 digits.` };
        else if (k.kind === 'unknown') r.problem = { code: 'DEV-003', message: 'Scan an IMEI (15 digits) or a serial number.' };
        else if (blocked.some((b) => b.status === 'active' && b.value === k.value)) r.problem = { code: 'DEV-014', message: `${k.value} is on the blocked list: ${blocked.find((b) => b.value === k.value)!.reason}.` };
        else {
          const holder = units.find((u) => u.id !== c.exceptUnitId && (u.imei1 === k.value || u.imei2 === k.value || u.serial === k.value));
          if (holder) r.problem = { code: 'DEV-007', message: `${k.value} already belongs to a device ${holder.status.replace(/_/g, ' ')} at B01 (${h.item(holder.variantId).descriptionEn}).` };
          const t = k.kind === 'imei' ? tac.get(k.value.slice(0, 8)) : undefined;
          if (!holder && t) {
            r.hintMismatch = !!item && !item.descriptionEn.toLowerCase().includes(t.brand.toLowerCase());
            r.hint = r.hintMismatch ? `This IMEI looks like a ${t.brand} ${t.model}, not ${item!.descriptionEn}.` : `Looks like a ${t.brand} ${t.model}.`;
          }
        }
        return r;
      }));
    },
    async correctIdentifiers(uid, c) {
      h.need('DEVICE.IMEI.CORRECT');
      const u = unit(uid);
      const next = c.imei1 ? classifyCode(c.imei1) : null;
      if (!c.reason?.trim()) throw problem(400, 'VAL-001', 'Say why the identifier is corrected.');
      if (next && next.kind !== 'imei') throw problem(400, 'DEV-001', `${next.value} is not a valid IMEI.`);
      if (next && next.value === u.imei1) throw problem(400, 'VAL-001', 'Nothing changed: give the correct identifier.');
      if (next && units.some((x) => x.id !== uid && (x.imei1 === next.value || x.imei2 === next.value))) throw problem(409, 'DEV-007', `IMEI ${next.value} already belongs to another device.`);
      if (next && u.imei1) u.retired.push({ kind: 'imei', slot: 1, value: u.imei1, reason: `corrected: ${c.reason}`, at: now() });
      if (next) u.imei1 = next.value;
      event(u, 'identifier_corrected', u.status, u.condition, { details: JSON.stringify({ reason: c.reason }) });
      settle(u, 'corrected');
      return delay({ unitId: uid, imei1: u.imei1, imei2: u.imei2, serial: u.serial });
    },
    async reverseReceipt(uid, reason) {
      h.need('DEVICE.WRITE_OFF');
      const u = unit(uid);
      if (!reason.trim()) throw problem(400, 'VAL-001', 'Say why the receipt is reversed.');
      if (u.status !== 'quarantined' || !open(u).some((x) => x.reason === 'imei_conflict')) throw problem(409, 'DEV-018', 'A receipt is reversed only for a device on hold because head office found its IMEI on another device. Write it off instead.');
      for (const x of open(u)) { x.releasedAt = now(); x.releaseNote = 'receipt reversed'; }
      settle(u, 'reversed', false);
      event(u, 'receipt_reversed', 'reversed', u.condition, { details: JSON.stringify({ reason }) });
      if (u.imei1) u.retired.push({ kind: 'imei', slot: 1, value: u.imei1, reason: `receipt reversed: ${reason}`, at: now() });
      u.imei1 = null;
      return delay({ id: id(), number: h.next('adj', 'ADJ'), cost: num(u.cost) });
    },
    async deviceConflicts(status) { h.need('DEVICE.CONFLICTS'); return delay(structuredClone(conflicts.filter((c) => !status || c.status === status)).reverse()); },
    async blockedImeis(f) { h.need('DEVICE.VIEW'); return delay(blocked.filter((b) => (!f?.status || b.status === f.status) && (!f?.q || b.value.includes(f.q) || b.reason.toLowerCase().includes(f.q.toLowerCase()))).map((b) => ({ ...b, devicesHere: units.filter((u) => u.imei1 === b.value || u.imei2 === b.value).length }))); },
    async addBlockedImei(b) {
      const me = h.need('DEVICE.BLOCKLIST');
      const k = classifyCode(b.value);
      if (k.kind === 'invalid_imei' || k.kind === 'unknown') throw problem(400, 'DEV-001', `${k.value} is not a valid IMEI.`);
      if (!b.reason.trim()) throw problem(400, 'VAL-001', 'Give the reason (for example: reported stolen).');
      if (blocked.some((x) => x.status === 'active' && x.value === k.value)) throw problem(409, 'GEN-409', 'That IMEI is already on the blocked list.');
      const row: BlockedImei = { id: id(), kind: k.kind === 'serial' ? 'serial' : 'imei', value: k.value, reason: b.reason.trim(), reference: b.reference ?? null, status: 'active', addedAt: now(), addedBy: me.displayNameEn, removedAt: null, removedReason: null, devicesHere: 0 };
      blocked.push(row);
      for (const u of units.filter((u) => u.imei1 === k.value || u.imei2 === k.value || u.serial === k.value)) { try { hold(u, 'blocked_imei', null, row.id, `On the blocked list: ${row.reason}`); } catch { /* sold or gone: nothing to hold */ } }
      return delay({ id: row.id, kind: row.kind, value: row.value });
    },
    async removeBlockedImei(bid, reason) {
      h.need('DEVICE.BLOCKLIST');
      const row = blocked.find((b) => b.id === bid);
      if (!row || row.status !== 'active') throw problem(409, 'GEN-409', 'It was already taken off the list.');
      Object.assign(row, { status: 'removed', removedAt: now(), removedReason: reason });
      for (const u of units) if (u.holds.some((x) => x.blockedId === bid && !x.releasedAt)) release(u, 'blocked_imei', bid, 'Removed from the blocked list.');
    },
    async tacSummary() { h.need('DEVICE.VIEW'); return delay({ count: tac.size, lastImportedAt: tac.size ? now() : null }); },
    async tacLookup(code) { h.need('DEVICE.VIEW'); const t = tac.get(code.slice(0, 8)); if (!t) throw problem(404, 'GEN-404', 'That model is not in the imported model table.'); return delay({ tac: code.slice(0, 8), ...t }); },
    async importTac(csv) {
      h.need('DEVICE.SETTINGS');
      let imported = 0; let skipped = 0;
      for (const line of csv.split('\n')) {
        const cells = line.split(/[,;\t]/).map((c) => c.trim());
        if (cells.length < 3 || !/^\d{8}$/.test(cells[0]!) || !cells[1] || !cells[2]) { if (line.trim()) skipped++; continue; }
        tac.set(cells[0]!, { brand: cells[1]!, model: cells.slice(2).join(' ') }); imported++;
      }
      if (!imported) throw problem(400, 'VAL-001', 'No line had an 8-digit TAC, a brand and a model.');
      return delay({ imported, skipped });
    },
    async serialFormats() { h.need('DEVICE.VIEW'); return delay(formats.map((f) => ({ ...f }))); },
    async setSerialFormat(brandId, pattern) {
      h.need('DEVICE.SETTINGS');
      if (pattern) { try { new RegExp(pattern); } catch { throw problem(400, 'VAL-001', 'That is not a valid pattern (a regular expression such as ^[A-Z0-9]{12}$).'); } }
      formats.find((f) => f.id === brandId)!.serialPattern = pattern || null;
    },
    async historyLookup(code) {
      h.need('DEVICE.VIEW');
      const k = classifyCode(code);
      if (k.kind === 'invalid_imei' || k.kind === 'unknown') throw problem(400, 'DEV-001', `${k.value} is not a valid IMEI or serial number.`);
      const local = units.filter((u) => u.imei1 === k.value || u.imei2 === k.value || u.serial === k.value || u.retired.some((r) => r.value === k.value)).map((u) => ({
        id: u.id, branchCode: 'B01', descriptionEn: h.item(u.variantId).descriptionEn, descriptionAr: h.item(u.variantId).descriptionAr, status: u.status, condition: u.condition, receivedAt: u.receivedAt,
        soldOn: u.soldSaleId ? (unitSales.get(u.id)?.at(-1)?.number ?? null) : null, soldAt: null, warrantyUntil: null, identifierStatus: (u.retired.some((r) => r.value === k.value) ? 'retired' : 'active') as 'active' | 'retired', visible: true,
      }));
      const b = blocked.find((x) => x.status === 'active' && x.value === k.value);
      const t = k.kind === 'imei' ? tac.get(k.value.slice(0, 8)) : undefined;
      const remote = conflicts.flatMap((c) => c.value === k.value ? c.sides.filter((x) => !x.local) : []);
      return delay({ kind: k.kind as 'imei' | 'serial', value: k.value, local,
        headOffice: { holders: [...local.map((l) => ({ branchCode: l.branchCode, branchNameEn: 'Muscat - Ruwi', branchNameAr: 'مسقط - روي', unitId: l.id, unitStatus: l.status, condition: l.condition, descriptionEn: l.descriptionEn, descriptionAr: l.descriptionAr, receivedAt: l.receivedAt, rowStatus: 'active' as const })),
                                ...remote.map((x) => ({ branchCode: x.branchCode, branchNameEn: x.branchNameEn, branchNameAr: x.branchNameAr, unitId: x.unitId, unitStatus: x.unitStatus, condition: 'brand_new' as DeviceCondition, descriptionEn: x.descriptionEn, descriptionAr: x.descriptionAr, receivedAt: x.receivedAt, rowStatus: 'conflict' as const }))],
                      blockedReason: b?.reason ?? null, openConflicts: conflicts.filter((c) => c.status === 'open' && c.value === k.value).map((c) => c.id) },
        headOfficeReachable: true, blocked: b?.reason ?? null, model: t ? `${t.brand} ${t.model}` : null });
    },
    async centralStatus() {
      h.need('DEVICE.VIEW');
      return delay({ role: 'combined', isHeadOffice: true, headOfficeReachable: true, lastContactAt: null, pending: units.filter((u) => u.central === 'pending').length,
        unitsInConflict: units.filter((u) => u.holds.some((x) => x.reason === 'imei_conflict' && !x.releasedAt)).length, openConflicts: conflicts.filter((c) => c.status === 'open').length,
        blocked: blocked.filter((b) => b.status === 'active').length, lastPushAt: null, lastPushOkAt: null, lastPushError: null, strict: !!settings.strictCentralCheck });
    },
    async deviceCounts() {
      h.need('DEVICE.COUNT');
      return delay(counts.map((c) => ({ id: c.id, number: c.number, status: c.status, branchCode: 'B01', branchId: c.branchId, variantId: c.variantId, variantDescription: c.variantId ? h.item(c.variantId).descriptionEn : null,
        startedAt: c.startedAt, startedBy: 'Mock', postedAt: c.postedAt, scans: c.scans.length, result: c.result ? JSON.stringify(c.result) : null })).reverse());
    },
    async startDeviceCount(c) {
      h.need('DEVICE.COUNT');
      if (counts.some((x) => x.status === 'open' && x.variantId === (c.variantId ?? null))) throw problem(409, 'DEV-019', 'A count of these devices is already open in this branch. Finish or cancel it first.');
      const x = { id: id(), number: h.next('dc', 'DC'), status: 'open' as const, branchId: c.branchId, branchCode: 'B01', variantId: c.variantId ?? null, startedAt: now(), postedAt: null, note: c.note ?? null, result: null, scans: [] };
      counts.push(x);
      return delay({ id: x.id, number: x.number });
    },
    async deviceCount(cid) { h.need('DEVICE.COUNT'); return delay(countDetail(cid)); },
    async scanDeviceCount(cid, sc) {
      h.need('DEVICE.COUNT');
      const c = counts.find((x) => x.id === cid)!;
      if (c.status !== 'open') throw problem(409, 'DEV-019', `Count ${c.number} is ${c.status}.`);
      const k = classifyCode(sc.code);
      if (k.kind === 'invalid_imei') throw problem(400, 'DEV-001', `${k.value} is not a valid IMEI: the check digit is wrong.`);
      if (c.scans.some((x) => x.value === k.value)) throw problem(409, 'DEV-019', 'That device is already scanned in this count.');
      const u = units.find((x) => x.imei1 === k.value || x.imei2 === k.value || x.serial === k.value);
      const inStock = u && IN_STOCK.includes(u.status === 'quarantined' ? (u.heldFrom ?? 'in_stock') : u.status);
      const result: CountResult = !u ? 'unknown' : !inStock ? 'not_in_stock' : c.variantId && u.variantId !== c.variantId ? 'other_model' : 'here';
      const message = !u ? 'Not a device of this business: receive it as found stock if it is yours.' : result === 'not_in_stock' ? `${h.item(u.variantId).descriptionEn} is ${u.status.replace(/_/g, ' ')} in the books.`
        : result === 'other_model' ? `A ${h.item(u.variantId).descriptionEn}, not the model being counted.` : h.item(u.variantId).descriptionEn;
      const scan = { id: id(), kind: (k.kind === 'serial' ? 'serial' : 'imei') as 'imei' | 'serial', value: k.value, entry: sc.entry ?? 'scan', result, unitId: u?.id ?? null, scannedAt: now(), scannedBy: 'Mock', descriptionEn: u ? h.item(u.variantId).descriptionEn : null };
      c.scans.push(scan);
      return delay({ id: scan.id, kind: scan.kind, value: scan.value, result, message, unitId: scan.unitId });
    },
    async removeCountScan(cid, sid) { h.need('DEVICE.COUNT'); const c = counts.find((x) => x.id === cid)!; c.scans = c.scans.filter((x) => x.id !== sid); },
    async postDeviceCount(cid, p) {
      h.need('DEVICE.COUNT');
      if (p.writeOffMissing && !p.reason?.trim()) throw problem(400, 'VAL-001', 'Give the reason for writing off the missing devices.');
      const d = countDetail(cid);
      const writtenOff: unknown[] = []; const notWrittenOff: unknown[] = [];
      if (p.writeOffMissing) for (const m of d.missing) { const u = unit(m.id); if (u.status === 'in_stock' || u.status === 'for_parts') { event(u, 'written_off', 'written_off', u.condition); writtenOff.push({ unitId: u.id }); } else notWrittenOff.push({ unitId: u.id, status: u.status }); }
      const result = { expected: d.expected.length, found: d.found, missing: d.missing, extra: d.extra, writtenOff, notWrittenOff };
      Object.assign(counts.find((x) => x.id === cid)!, { status: 'posted', postedAt: now(), result });
      return delay(result);
    },
    async cancelDeviceCount(cid) { h.need('DEVICE.COUNT'); counts.find((x) => x.id === cid)!.status = 'cancelled'; },
  };

  return {
    api,
    identity,
    seedConflict,
    seedBlocked: (value: string, reason: string, reference: string | null) =>
      blocked.push({ id: id(), kind: 'imei', value, reason, reference, status: 'active', addedAt: now(), addedBy: 'Salim Al Harthy', removedAt: null, removedReason: null, devicesHere: 0 }),
    setCentral: (uid: string, state: CentralState) => { unit(uid).central = state; },
    receive,
    inStock: (variantId: string) => units.filter((u) => u.variantId === variantId && u.status === 'in_stock').length,
    /** The cart line's item as one unit sells (own price, condition in the description). */
    priced: (uid: string) => { const u = unit(uid); sellable(u); const p = posUnit(u); return { unit: u, item: p }; },
    leaseHolder: (uid: string) => { const l = leases.get(uid); return l && l.until > Date.now() ? l.cartId : null; },
    sold: (uid: string, saleId: string, number: string, net: bigint, gross: bigint, tax: bigint) => {
      const u = unit(uid); const c = u.cost;
      event(u, 'sold', 'sold', u.condition, { docType: 'sale', docId: saleId, docNumber: number });
      u.soldSaleId = saleId; leases.delete(uid);
      unitSales.set(uid, [...(unitSales.get(uid) ?? []), { saleId, number, docType: 'simplified_invoice', creditReason: null, completedAt: now(), net: num(net), tax: num(tax), gross: num(gross), cost: num(c), profit: num(net - c), customerName: null }]);
      return c;
    },
    returned: (uid: string, saleId: string, number: string, code: string | null | undefined, voided: boolean) => {
      const u = unit(uid);
      if (!voided) {
        if (!code?.trim()) throw problem(400, 'DEV-009', "Scan the returned device's IMEI or serial number.");
        const c = classifyCode(code).value;
        if (c !== u.imei1 && c !== u.imei2 && c !== u.serial) throw problem(400, 'DEV-009', `${c} is not the device sold on this line.`);
      }
      event(u, voided ? 'sale_voided' : 'returned', voided ? 'in_stock' : 'returned_inspection', u.condition, { docType: 'sale', docId: saleId, docNumber: number });
      u.soldSaleId = null;
    },
    identifier: (uid: string) => { const u = units.find((x) => x.id === uid); return u ? (u.imei1 ? `IMEI ${u.imei1}` : u.serial ? `S/N ${u.serial}` : null) : null; },
    condition: (uid: string) => unit(uid).condition,
    gradeId: (code: string) => grades.find((g) => g.code === code)?.id ?? null,
    grades: () => grades.filter((g) => g.status === 'active'),
    checklistItems: () => checklist.items,
    /** Phase 7: whether a device may be taken in, and the unit the business sold if it comes back (blocked → DEV-014; in stock → TRD-004). */
    tradeInCheck: (code: string) => {
      const c = classifyCode(code);
      if (c.kind === 'invalid_imei') throw problem(400, 'DEV-001', `${c.value} is not a valid IMEI. Scan it again.`);
      if (blocked.some((b) => b.status === 'active' && b.value === c.value)) throw problem(409, 'DEV-014', `IMEI ${c.value} is on the blocked list. Do not take this device in.`);
      const u = units.find((x) => x.imei1 === c.value || x.imei2 === c.value || x.serial === c.value) ?? null;
      if (u && u.status !== 'sold') throw problem(409, 'TRD-004', `${c.value} belongs to a device the business still has (${u.status.replace(/_/g, ' ')}): it cannot be traded in.`);
      return u;
    },
    /** Takes a traded-in device into stock under inspection: back into its own record when the business sold it, else a new one. */
    acquire: (a: { variantId: string; imei: string | null; serial: string | null; cost: bigint; condition: DeviceCondition; gradeId: string | null; reconnectId: string | null; margin: boolean }) => {
      if (a.reconnectId) {
        const u = unit(a.reconnectId);
        const delta = a.cost - u.cost;
        event(u, 'traded_in', 'under_inspection', a.condition, { costDelta: num(delta) });
        u.cost = a.cost; u.gradeId = a.gradeId; u.sellingPrice = null; u.soldSaleId = null; u.marginCost = a.margin ? a.cost : null;
        return u.id;
      }
      const u = receive(a.variantId, { imei1: a.imei, serial: a.serial, unitCost: num(a.cost), condition: a.condition, status: 'under_inspection', gradeId: a.gradeId }, num(a.cost));
      u.marginCost = a.margin ? a.cost : null;
      return u.id;
    },
    costOf: (uid: string) => unit(uid).cost,
  };
}
