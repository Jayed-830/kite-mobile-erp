// In-memory Phase 5 device units for tests, `npm run dev:mock` and design review: units with IMEIs, conditions and
// grades, inspections with the shared checklist rules, refurbishment orders with parts, labour and outside work, and
// the till's lookups and leases. The real server checks everything again and is the source of truth.
import { ApiError, type Checklist, type ChecklistItemDef, type DeviceApi, type DeviceCondition, type DeviceDetail, type DeviceEvent, type DeviceRow, type DeviceSettings,
  type DeviceStatus, type Grade, type PosUnit, type RefurbCost, type RefurbOrderRow, type UnitInput, type User } from '@kite/api';
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
}

export interface DeviceHooks {
  need: (perm?: string) => User;
  can: (u: User, perm: string) => boolean;
  item: (variantId: string) => { sku: string; descriptionEn: string; descriptionAr: string; productType: string; tracking: string; listPrice: number | null; minPrice: number | null; warrantyDays: number; categoryId: string | null };
  partMove: (variantId: string, qty: bigint) => bigint;          // issues (negative) or returns (positive) a spare part; returns the signed value
  partStock: (variantId: string) => bigint;
  next: (kind: string, prefix: string) => string;
  reservedBy: () => { terminalCode: string } | null;
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
    u.heldFrom = to === 'quarantined' ? u.status : null;
    u.status = to; u.condition = cond; u.version++;
  };
  const row = (u: MockUnit): DeviceRow => {
    const i = h.item(u.variantId); const g = grade(u.gradeId);
    return { id: u.id, branchCode: 'B01', variantId: u.variantId, sku: i.sku, descriptionEn: i.descriptionEn, descriptionAr: i.descriptionAr, productType: i.productType, status: u.status,
      heldFromStatus: u.heldFrom, condition: u.condition, gradeCode: g?.code ?? null, gradeNameEn: g?.nameEn ?? null, gradeNameAr: g?.nameAr ?? null, acquisitionCost: num(u.acquisition),
      accumulatedCost: num(u.cost), sellingPrice: u.sellingPrice, imei1: u.imei1, imei2: u.imei2, serial: u.serial, receivedAt: u.receivedAt,
      ageDays: Math.floor((Date.now() - Date.parse(u.receivedAt)) / 864e5), version: u.version };
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
      warrantyDays: u.warrantyDays ?? i.warrantyDays, reservedUntil: lease && lease.until > Date.now() ? new Date(lease.until).toISOString() : null };
  };
  const sellable = (u: MockUnit) => {
    if (u.status !== 'in_stock') throw problem(409, 'DEV-004', `${ident(u)} is ${u.status.replace(/_/g, ' ')}, so it cannot be sold.`);
  };
  const find = (code: string) => {
    const c = classifyCode(code);
    if (c.kind === 'invalid_imei') throw problem(400, 'DEV-001', c.value.length === 15 ? `${c.value} is not a valid IMEI: the check digit is wrong. Scan it again.` : `${c.value} is not an IMEI: an IMEI has 15 digits.`);
    if (c.kind === 'unknown') throw problem(400, 'DEV-003', 'Scan or type an IMEI (15 digits) or a serial number.');
    return units.find((u) => u.imei1 === c.value || u.imei2 === c.value || u.serial === c.value) ?? null;
  };

  const receive = (variantId: string, input: UnitInput, fallbackCost: number | null | undefined) => {
    const i = h.item(variantId);
    const imei1 = input.imei1 ? classifyCode(input.imei1) : null;
    if (i.tracking === 'imei' && (!imei1 || imei1.kind !== 'imei')) throw problem(400, imei1 ? 'DEV-001' : 'DEV-002', imei1 ? `${imei1.value} is not a valid IMEI.` : "Scan the device's IMEI.");
    if (imei1 && units.some((u) => u.imei1 === imei1.value || u.imei2 === imei1.value)) throw problem(409, 'DEV-007', 'That IMEI already belongs to another device.');
    const condition = input.condition ?? 'brand_new';
    const status = input.status ?? (condition === 'brand_new' ? 'in_stock' : 'under_inspection');
    const cost = toBaisa(input.unitCost ?? fallbackCost ?? 0);
    const u: MockUnit = { id: id(), variantId, status, heldFrom: null, condition, gradeId: input.gradeId ?? null, acquisition: cost, cost, sellingPrice: input.sellingPrice ?? null,
      warrantyDays: input.warrantyDays ?? null, imei1: imei1?.value ?? null, imei2: input.imei2 ? classifyCode(input.imei2).value : null, serial: input.serial ? classifyCode(input.serial).value : null,
      receivedAt: now(), soldSaleId: null, events: [], version: 1 };
    event(u, 'received', status, condition);
    units.push(u);
    return u;
  };

  const api: DeviceApi = {
    async devices(f) {
      h.need('DEVICE.VIEW');
      const statuses = f.status?.split(',');
      const q = f.q?.trim().toLowerCase();
      return delay(units.filter((u) => (!statuses || statuses.includes(u.status)) && (!f.condition || u.condition === f.condition) && (!f.variantId || u.variantId === f.variantId)
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
      });
    },
    async setDevicePrice(uid, p) { h.need('DEVICE.PRICE'); const u = unit(uid); if (u.version !== p.version) throw problem(412, 'GEN-412', 'Someone else changed this device. Reload and try again.'); u.sellingPrice = p.sellingPrice; if (p.warrantyDays != null) u.warrantyDays = p.warrantyDays; event(u, 'price_set', u.status, u.condition); },
    async holdDevice(uid) { h.need('DEVICE.HOLD'); const u = unit(uid); event(u, 'held', 'quarantined', u.condition); },
    async releaseDevice(uid) { h.need('DEVICE.HOLD'); const u = unit(uid); event(u, 'released', u.heldFrom ?? 'in_stock', u.condition); },
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
    async posUnitByCode(code) {
      h.need('POS.SELL');
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
      return delay([...unitSales].flatMap(([uid, ss]) => ss.filter((s) => s.docType !== 'credit_note').map((s) => ({ number: s.number, saleDate: s.completedAt.slice(0, 10), unitId: uid, descriptionEn: h.item(unit(uid).variantId).descriptionEn, identifier: ident(unit(uid)), net: s.net, cost: s.cost, profit: s.profit ?? 0, returned: 0 }))));
    },
    async refurbishmentReport() {
      h.need('DEVICE.VIEW');
      const sumOf = (k: string) => num(costs.filter((c) => c.kind === k).reduce((a, c) => a + toBaisa(c.amount), 0n));
      return delay(orders.length ? [{ technician: '-', orders: orders.length, completed: orders.filter((o) => o.status === 'completed').length, parts: sumOf('part'), labour: sumOf('labour'), external: sumOf('external'), total: num(costs.reduce((a, c) => a + toBaisa(c.amount), 0n)) }] : []);
    },
  };

  return {
    api,
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
  };
}
