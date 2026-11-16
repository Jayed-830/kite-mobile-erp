// In-memory Phase 7 trade-ins for tests, `npm run dev:mock` and design review: the till wizard's steps, the four ways a
// trade-in is settled, store credit, the price guide and deductions, and the margin scheme approval. The arithmetic is
// @kite/pos (the same vectors as the server); the real server checks everything again and is the source of truth.
import { ApiError, type ApprovalAction, type Customer, type DeductionRow, type MarginScheme, type OpenTradeIn, type PriceGuideRow, type TradeIn, type TradeInApi,
  type TradeInDetail, type TradeInSettings, type TradeInSettlement, type User } from '@kite/api';
import { classifyCode, fromBaisa, last4, needsApproval, toBaisa, toNumber, valueTradeIn, type Tender } from '@kite/pos';

const problem = (status: number, code: string, title: string) => new ApiError({ status, code, title });
const id = () => crypto.randomUUID();
const now = () => new Date().toISOString();
const delay = <T>(v: T): Promise<T> => new Promise((r) => setTimeout(() => r(v), 15));
const n = (b: bigint) => toNumber(b);

type DeviceMock = {
  tradeInCheck: (code: string) => { id: string; variantId: string } | null;
  acquire: (a: { variantId: string; imei: string | null; serial: string | null; cost: bigint; condition: 'used' | 'damaged'; gradeId: string | null; reconnectId: string | null; margin: boolean }) => string;
  grades: () => { id: string; code: string; nameEn: string; nameAr: string; pricePercent: number }[];
  checklistItems: () => import('@kite/api').ChecklistItemDef[];
  costOf: (uid: string) => bigint;
};

export interface TradeInMockHooks {
  need: (perm?: string) => User;
  can: (u: User, perm: string) => boolean;
  next: (kind: string, prefix: string) => string;
  branchId: string;
  /** The till: the signed-in user and terminal, else POS-004. */
  terminal: () => { u: User; t: { id: string; code: string } };
  /** The open shift of this terminal (POS-001 when none). */
  shift: () => { id: string };
  drawerCash: (shiftId: string) => bigint;
  payOut: (shiftId: string, amount: bigint, reason: string) => void;
  customers: () => Customer[];
  item: (variantId: string) => { sku: string; descriptionEn: string; descriptionAr: string; productType: string; tracking: string };
  dev: DeviceMock;
  useApproval: (approvalId: string | null | undefined, action: ApprovalAction, requesterId: string) => void;
  marginCodeStatus: (active: boolean) => void;
}

interface Row extends TradeIn { civilIdPlain: string | null; issues: { code: string; nameEn: string; nameAr: string; amount: number }[]; settlements: TradeInSettlement[] }

export function createMockTradeIn(h: TradeInMockHooks) {
  const rows: Row[] = [];
  const credit: { id: string; customerId: string; amount: bigint; sourceDocType: string; sourceDocId: string; note: string; createdAt: string; documentNumber: string | null }[] = [];
  let settings: TradeInSettings = {
    staffLimit: 50, overGuidePercent: 10, requireCivilId: true, version: 1,
    declarationEn: 'I confirm that I own this device and may sell it; that it is not stolen, lost, under finance or locked; that I have removed my accounts and data from it; and that I sell it to the business for the agreed value.',
    declarationAr: 'أقر بأنني مالك هذا الجهاز ويحق لي بيعه، وأنه غير مسروق أو مفقود أو مرهون أو مقفل، وأنني أزلت حساباتي وبياناتي منه، وأنني أبيعه للمنشأة بالقيمة المتفق عليها.',
    margin: { approved: false, reference: null, approvedOn: null },
  };
  let margin: MarginScheme = { approved: false, reference: null, approvedOn: null, note: null, version: 1, taxCode: 'VAT-MARGIN', taxCodeStatus: 'inactive' };
  const guide: PriceGuideRow[] = [];
  const deductions: DeductionRow[] = [
    ['SCREEN', 'Cracked or faulty screen', 'شاشة مكسورة أو معطلة', 'percent', 30, 'screen'], ['BATTERY', 'Battery below 80%', 'بطارية أقل من 80%', 'amount', 10, 'battery'],
    ['BODY', 'Dents or cracks on the body', 'انبعاجات أو تشققات في الهيكل', 'percent', 10, 'body'], ['CAMERA', 'Camera fault', 'عطل في الكاميرا', 'percent', 15, 'cameras'],
    ['NO_BOX', 'No box or charger', 'بدون علبة أو شاحن', 'amount', 3, null],
  ].map(([code, nameEn, nameAr, kind, value, checklistCode], i) => ({ id: id(), code: code as string, nameEn: nameEn as string, nameAr: nameAr as string, kind: kind as 'amount' | 'percent',
    value: value as number, checklistCode: checklistCode as string | null, sort: i, status: 'active' as const, version: 1 }));

  const marginOn = () => margin.approved && margin.approvedOn != null && margin.approvedOn <= now().slice(0, 10);
  const row = (tid: string) => { const r = rows.find((x) => x.id === tid); if (!r) throw problem(404, 'GEN-404', 'That trade-in was not found in your branches.'); return r; };
  const outstanding = (r: Row) => toBaisa(r.agreedValue ?? 0) - r.settlements.reduce((a, s) => a + toBaisa(s.amount), 0n);
  const balance = (cid: string) => credit.filter((c) => c.customerId === cid).reduce((a, c) => a + c.amount, 0n);
  const guideValue = (variantId: string, gradeId: string | null) => (gradeId ? guide.find((g) => g.variantId === variantId && g.gradeId === gradeId && g.status === 'active')?.baseValue ?? null : null);
  const detail = (r: Row, u: User): TradeInDetail => {
    const canApprove = h.can(u, 'TRADEIN.APPROVE');
    const left = r.status === 'accepted' || r.status === 'settled' ? n(outstanding(r)) : null;
    const tradeIn: TradeIn = { ...r, outstanding: left, needsApproval: r.agreedValue != null && r.status === 'open' ? !canApprove && needsApproval(toBaisa(r.agreedValue), r.offeredValue == null ? null : toBaisa(r.offeredValue), settings.staffLimit, settings.overGuidePercent) : null };
    for (const k of ['civilIdPlain', 'issues', 'settlements'] as const) delete (tradeIn as Partial<Row>)[k];
    return {
      tradeIn, issues: r.issues.map((x) => ({ ...x })), settlements: r.settlements.map((x) => ({ ...x })),
      form: r.status !== 'open' ? null : {
        checklistId: 'mock', items: h.dev.checklistItems(), grades: h.dev.grades().map((g) => ({ ...g, guideValue: guideValue(r.variantId, g.id) })),
        deductions: deductions.filter((d) => d.status === 'active').map(({ sort: _s, status: _st, version: _v, ...d }) => d), staffLimit: settings.staffLimit,
        overGuidePercent: settings.overGuidePercent, requireCivilId: settings.requireCivilId, declarationEn: settings.declarationEn, declarationAr: settings.declarationAr, canApprove,
      },
    };
  };
  const settle = (r: Row, u: User, kind: 'store_credit' | 'cash', amount: bigint | null, approvalId: string | null | undefined) => {
    if (r.status !== 'accepted') throw problem(409, 'TRD-001', `Trade-in ${r.number} is ${r.status}: nothing is left to settle.`);
    const left = outstanding(r);
    const value = amount ?? left;
    if (value <= 0n) throw problem(400, 'VAL-001', 'The amount is above zero.');
    if (value > left) throw problem(409, 'TRD-005', `Only ${fromBaisa(left)} is left to settle on trade-in ${r.number}.`);
    const perm = kind === 'store_credit' ? 'TRADEIN.STORE_CREDIT' : 'TRADEIN.PAYOUT';
    if (!h.can(u, perm)) {
      if (!approvalId) throw problem(403, 'AUTH-403', kind === 'store_credit' ? 'Leaving a trade-in as store credit needs a manager\'s approval.' : 'Paying a trade-in out in cash needs a manager\'s approval.');
      h.useApproval(approvalId, perm, u.id);
    }
    if (kind === 'cash') {
      const s = h.shift();
      if (value > h.drawerCash(s.id)) throw problem(400, 'VAL-001', `The drawer holds ${fromBaisa(h.drawerCash(s.id))}; it cannot pay out ${fromBaisa(value)}.`);
      h.payOut(s.id, value, `Trade-in ${r.number} paid out`);
    } else {
      credit.push({ id: id(), customerId: r.customerId, amount: value, sourceDocType: 'tradein', sourceDocId: r.id, note: `Trade-in ${r.number}`, createdAt: now(), documentNumber: r.number });
    }
    r.settlements.push({ id: id(), kind, amount: n(value), saleNumber: null, createdAt: now(), createdBy: u.displayNameEn });
    if (outstanding(r) === 0n) { r.status = 'settled'; r.settledAt = now(); }
    r.version++;
  };

  const api: TradeInApi = {
    async tradeIns(f) {
      h.need('TRADEIN.VIEW');
      const q = f?.q?.trim().toLowerCase();
      return delay(rows.filter((r) => (!f?.status || (f.status === 'unsettled' ? r.status === 'accepted' : r.status === f.status)) && (!f?.customerId || r.customerId === f.customerId)
        && (!q || r.number.toLowerCase().includes(q) || r.customerName.toLowerCase().includes(q) || r.imei1 === q || (r.customerPhone ?? '').includes(q)))
        .map((r) => ({ id: r.id, number: r.number, status: r.status, branchCode: 'B01', customerName: r.customerName, customerPhone: r.customerPhone, descriptionEn: r.descriptionEn, descriptionAr: r.descriptionAr,
          identifier: r.imei1 ?? r.serial, offeredValue: r.offeredValue, agreedValue: r.agreedValue, outstanding: r.status === 'accepted' || r.status === 'settled' ? n(outstanding(r)) : null,
          marginScheme: r.marginScheme, reconnected: r.reconnected, createdAt: r.createdAt, acceptedAt: r.acceptedAt, createdBy: r.createdByName })).reverse());
    },
    async tradeIn(tid) { const u = h.need(); return delay(detail(row(tid), u)); },
    async createTradeIn(req) {
      const { u } = h.terminal();
      if (!h.can(u, 'TRADEIN.CREATE')) throw problem(403, 'AUTH-403', 'You do not have permission TRADEIN.CREATE.');
      const c = h.customers().find((x) => x.id === req.customerId);
      if (!c) throw problem(400, 'POS-016', 'Choose the customer selling the device (name and phone).');
      if (!c.phoneE164) throw problem(400, 'POS-016', 'A trade-in needs the customer\'s phone number. Add it to the customer.');
      const it = h.item(req.device.variantId);
      if (it.productType !== 'device' || it.tracking === 'none') throw problem(400, 'VAL-001', 'Trade-ins are devices with an IMEI or serial number; choose the phone\'s model.');
      const code = req.device.imei1 ?? req.device.serial;
      if (!code) throw problem(400, 'DEV-002', 'Scan the device\'s IMEI.');
      const kind = classifyCode(code);
      if (it.tracking === 'imei' && kind.kind !== 'imei') throw problem(400, kind.kind === 'invalid_imei' ? 'DEV-001' : 'DEV-002', `${kind.value} is not a valid IMEI. Scan it again.`);
      const back = h.dev.tradeInCheck(kind.value);
      const civil = req.civilId?.replace(/[^0-9A-Za-z]/g, '').toUpperCase() || null;
      if (civil && (civil.length < 6 || civil.length > 20)) throw problem(400, 'TRD-007', 'A civil ID is 6 to 20 letters or digits.');
      const variantId = back?.variantId ?? req.device.variantId;
      const i2 = h.item(variantId);
      const r: Row = {
        id: id(), number: h.next('ti', 'TI'), status: 'open', branchId: h.branchId, branchCode: 'B01', customerId: c.id, customerName: c.nameEn, customerNameAr: c.nameAr, customerPhone: c.phoneE164,
        civilId: civil ? `•••• ${last4(civil)}` : null, civilIdPlain: civil, sellerVatNumber: req.sellerVatNumber?.trim() || null, variantId, sku: i2.sku, descriptionEn: i2.descriptionEn, descriptionAr: i2.descriptionAr,
        imei1: kind.kind === 'imei' ? kind.value : null, imei2: null, serial: kind.kind === 'serial' ? kind.value : null, entry: req.device.entry ?? 'scan', storage: req.device.storage ?? null, colour: req.device.colour ?? null,
        gradeId: null, gradeCode: null, gradeNameEn: null, gradeNameAr: null, condition: 'used', inspection: {}, batteryHealth: null, cloudLockRemoved: false, guideValue: null, deductionsTotal: 0,
        offeredValue: null, agreedValue: null, approvalId: null, consentMethod: null, consentName: null, consentReference: null, consentAt: null, saleId: req.saleId ?? null, deviceUnitId: back?.id ?? null,
        reconnected: false, marginScheme: false, selfInvoiceNumber: null, note: req.note ?? null, rejectReason: null, acceptedAt: null, acceptedByName: null, settledAt: null, closedAt: null,
        createdAt: now(), createdByName: u.displayNameEn, version: 1, outstanding: null, needsApproval: null, issues: [], settlements: [],
      };
      rows.push(r);
      return delay({ detail: detail(r, u), headOfficeChecked: true });
    },
    async updateTradeIn(tid, req) {
      const { u } = h.terminal();
      const r = row(tid);
      if (r.status !== 'open') throw problem(409, 'TRD-001', `Trade-in ${r.number} is ${r.status}; it can no longer change.`);
      if (r.version !== req.version) throw problem(412, 'GEN-412', 'Someone else changed this trade-in. Reload it.');
      if (req.gradeId !== undefined) { const g = h.dev.grades().find((x) => x.id === req.gradeId) ?? null; r.gradeId = g?.id ?? null; r.gradeCode = g?.code ?? null; r.gradeNameEn = g?.nameEn ?? null; r.gradeNameAr = g?.nameAr ?? null; }
      if (req.condition) r.condition = req.condition;
      if (req.inspection) r.inspection = { ...req.inspection };
      if (req.batteryHealth != null) r.batteryHealth = req.batteryHealth;
      if (req.cloudLockRemoved != null) r.cloudLockRemoved = req.cloudLockRemoved;
      if (req.civilId != null) { const civil = req.civilId.replace(/[^0-9A-Za-z]/g, '').toUpperCase() || null; r.civilIdPlain = civil; r.civilId = civil ? `•••• ${last4(civil)}` : null; }
      if (req.sellerVatNumber !== undefined) r.sellerVatNumber = req.sellerVatNumber?.trim() || null;
      if (req.saleId) r.saleId = req.saleId;
      const codes = req.deductions ?? r.issues.map((x) => x.code);
      const defs = codes.map((c) => { const d = deductions.find((x) => x.code === c && x.status === 'active'); if (!d) throw problem(400, 'VAL-001', `Unknown deduction ${c}.`); return d; }).sort((a, b) => (a.code < b.code ? -1 : 1));
      const g = guideValue(r.variantId, r.gradeId);
      let v;
      try { v = valueTradeIn(g, defs.map((d) => ({ code: d.code, kind: d.kind, value: d.value }))); }
      catch (e) { const pe = e as { code?: string; message: string }; throw problem(400, pe.code ?? 'VAL-001', pe.message); }
      r.issues = defs.map((d, i) => ({ code: d.code, nameEn: d.nameEn, nameAr: d.nameAr, amount: n(v.amounts[i]!) }));
      r.guideValue = g; r.deductionsTotal = n(v.total); r.offeredValue = v.offered == null ? null : n(v.offered);
      if (req.agreedValue !== undefined && req.agreedValue !== r.agreedValue) { r.agreedValue = req.agreedValue; r.approvalId = null; }
      r.version++;
      return delay(detail(r, u));
    },
    async acceptTradeIn(tid, req) {
      const { u } = h.terminal();
      const r = row(tid);
      if (r.status !== 'open') throw problem(409, 'TRD-001', `Trade-in ${r.number} is ${r.status}.`);
      if (r.version !== req.version) throw problem(412, 'GEN-412', 'Someone else changed this trade-in. Reload it.');
      if (!r.cloudLockRemoved) throw problem(409, 'TRD-004', 'Confirm that the customer removed their cloud account and activation lock in front of you.');
      if (settings.requireCivilId && !r.civilIdPlain) throw problem(400, 'TRD-007', 'Record the customer\'s civil ID: the business is buying a device from them.');
      const agreed = toBaisa(req.agreedValue);
      if (!h.can(u, 'TRADEIN.APPROVE') && needsApproval(agreed, r.offeredValue == null ? null : toBaisa(r.offeredValue), settings.staffLimit, settings.overGuidePercent)) {
        if (!req.approvalId) throw problem(403, 'TRD-002', r.offeredValue == null ? 'There is no guide value for this model and grade, so a manager approves the value.' : `${fromBaisa(agreed)} is above your limit or the guide offer: a manager approves it.`);
        h.useApproval(req.approvalId, 'TRADEIN.APPROVE', u.id);
        r.approvalId = req.approvalId;
      }
      const c = req.consent;
      if (!c || (c.method === 'on_screen' && !c.name?.trim()) || (c.method === 'paper' && !c.reference?.trim())) throw problem(400, 'TRD-003', c?.method === 'paper' ? 'Give the reference of the signed paper form.' : 'The customer types their name to confirm on screen.');
      if (req.settle?.kind === 'cash' && !h.can(u, 'TRADEIN.PAYOUT') && !req.settle.approvalId) throw problem(403, 'AUTH-403', 'Paying a trade-in out in cash needs a manager\'s approval.');
      if (req.settle?.kind === 'store_credit' && !h.can(u, 'TRADEIN.STORE_CREDIT') && !req.settle.approvalId) throw problem(403, 'AUTH-403', 'Leaving a trade-in as store credit needs a manager\'s approval.');
      const back = h.dev.tradeInCheck(r.imei1 ?? r.serial!);
      const isMargin = marginOn() && !r.sellerVatNumber && !h.customers().find((x) => x.id === r.customerId)?.vatNumber;
      r.deviceUnitId = h.dev.acquire({ variantId: r.variantId, imei: r.imei1, serial: r.serial, cost: agreed, condition: r.condition, gradeId: r.gradeId, reconnectId: back?.id ?? null, margin: isMargin });
      Object.assign(r, { status: 'accepted', agreedValue: n(agreed), reconnected: !!back, consentMethod: c.method, consentName: c.name?.trim() || null, consentReference: c.reference?.trim() || null,
        consentAt: now(), marginScheme: isMargin, selfInvoiceNumber: isMargin ? h.next('si', 'SI') : null, acceptedAt: now(), acceptedByName: u.displayNameEn, saleId: req.saleId ?? r.saleId });
      r.version++;
      if (req.settle) settle(r, u, req.settle.kind, req.settle.amount == null ? null : toBaisa(req.settle.amount), req.settle.approvalId);
      return delay(detail(r, u));
    },
    async settleTradeIn(tid, req) {
      const u = h.need();
      const r = row(tid);
      settle(r, u, req.kind, req.amount == null ? null : toBaisa(req.amount), req.approvalId);
      return delay(detail(r, u));
    },
    async rejectTradeIn(tid, reason) {
      const u = h.need('TRADEIN.CREATE');
      const r = row(tid);
      if (r.status !== 'open') throw problem(409, 'TRD-001', `Trade-in ${r.number} is ${r.status}.`);
      if (!reason.trim()) throw problem(400, 'VAL-001', 'Say why the trade-in is rejected.');
      Object.assign(r, { status: 'rejected', rejectReason: reason.trim(), closedAt: now(), version: r.version + 1 });
      return delay(detail(r, u));
    },
    async cancelTradeIn(tid) {
      const u = h.need('TRADEIN.CREATE');
      const r = row(tid);
      if (r.status !== 'open') throw problem(409, 'TRD-001', `Trade-in ${r.number} is ${r.status}.`);
      Object.assign(r, { status: 'cancelled', closedAt: now(), version: r.version + 1 });
      return delay(detail(r, u));
    },
    async revealCivilId(tid) { h.need('TRADEIN.APPROVE'); const r = row(tid); if (!r.civilIdPlain) throw problem(404, 'GEN-404', 'No civil ID was recorded on this trade-in.'); return delay({ civilId: r.civilIdPlain }); },
    async openTradeIns(f) {
      h.terminal();
      const list: OpenTradeIn[] = rows.filter((r) => r.status === 'accepted' && (!f?.customerId || r.customerId === f.customerId) && (!f?.saleId || r.saleId === f.saleId))
        .map((r) => ({ id: r.id, number: r.number, customerId: r.customerId, customerName: r.customerName, descriptionEn: r.descriptionEn, descriptionAr: r.descriptionAr, identifier: r.imei1 ?? r.serial,
          agreedValue: r.agreedValue ?? 0, outstanding: n(outstanding(r)), saleId: r.saleId }));
      return delay(list.reverse());
    },
    async tradeInDocument() { h.need(); return delay(new Blob(['%PDF-1.7 mock trade-in'], { type: 'application/pdf' })); },
    async tradeInSettings() { h.need(); return delay({ ...settings, margin: { approved: margin.approved, reference: margin.reference, approvedOn: margin.approvedOn } }); },
    async updateTradeInSettings(s) {
      h.need('TRADEIN.SETTINGS');
      if (s.version !== settings.version) throw problem(412, 'GEN-412', 'Someone else changed the trade-in settings. Reload them.');
      if (!s.declarationEn.trim() || !s.declarationAr.trim()) throw problem(400, 'VAL-001', 'The declaration is needed in English and Arabic.');
      settings = { ...settings, ...s, version: settings.version + 1 };
    },
    async priceGuide(variantId) { h.need(); return delay(guide.filter((g) => g.status === 'active' && (!variantId || g.variantId === variantId)).map((g) => ({ ...g }))); },
    async savePriceGuide(list) {
      h.need('TRADEIN.SETTINGS');
      for (const x of list) {
        const it = h.item(x.variantId); const g = h.dev.grades().find((y) => y.id === x.gradeId)!;
        const existing = guide.find((y) => y.variantId === x.variantId && y.gradeId === x.gradeId);
        if (x.baseValue == null) { if (existing) existing.status = 'inactive'; continue; }
        if (existing) Object.assign(existing, { baseValue: x.baseValue, status: 'active', updatedAt: now() });
        else guide.push({ id: id(), variantId: x.variantId, sku: it.sku, descriptionEn: it.descriptionEn, descriptionAr: it.descriptionAr, gradeId: x.gradeId, gradeCode: g.code, baseValue: x.baseValue, status: 'active', updatedAt: now() });
      }
    },
    async deductions() { h.need(); return delay(deductions.map((d) => ({ ...d }))); },
    async createDeduction(d) {
      h.need('TRADEIN.SETTINGS');
      if (!/^[A-Z0-9_-]{2,20}$/.test(d.code.trim().toUpperCase())) throw problem(400, 'VAL-001', 'A code is 2 to 20 letters, digits, dashes or underscores.');
      if (deductions.some((x) => x.code === d.code.trim().toUpperCase())) throw problem(409, 'GEN-409', 'That code is already in use.');
      const x: DeductionRow = { id: id(), code: d.code.trim().toUpperCase(), nameEn: d.nameEn, nameAr: d.nameAr, kind: d.kind, value: d.value, checklistCode: d.checklistCode ?? null, sort: d.sort ?? deductions.length, status: 'active', version: 1 };
      deductions.push(x);
      return delay({ id: x.id });
    },
    async updateDeduction(did, d) {
      h.need('TRADEIN.SETTINGS');
      const x = deductions.find((y) => y.id === did)!;
      if (d.version !== x.version) throw problem(412, 'GEN-412', 'That deduction was changed by someone else. Reload it.');
      Object.assign(x, { ...d, code: d.code.trim().toUpperCase(), checklistCode: d.checklistCode ?? null, version: x.version + 1 });
    },
    async tradeInReport() {
      h.need('TRADEIN.VIEW');
      return delay(rows.filter((r) => r.status === 'accepted' || r.status === 'settled').map((r) => ({
        number: r.number, acceptedAt: r.acceptedAt!, branchCode: 'B01', descriptionEn: r.descriptionEn, identifier: r.imei1 ?? r.serial, agreedValue: r.agreedValue ?? 0, marginScheme: r.marginScheme,
        unitStatus: 'under_inspection', costAdded: r.deviceUnitId ? n(h.dev.costOf(r.deviceUnitId) - toBaisa(r.agreedValue ?? 0)) : 0, saleNumber: null, resaleNet: null, resaleTax: null, profit: null,
      })));
    },
    async storeCredit(cid) {
      h.need('CUSTOMERS.VIEW');
      return delay({ customerId: cid, balance: n(balance(cid)), lines: credit.filter((c) => c.customerId === cid).map((c) => ({ id: c.id, amount: n(c.amount), sourceDocType: c.sourceDocType, sourceDocId: c.sourceDocId,
        note: c.note, createdAt: c.createdAt, branchCode: 'B01', documentNumber: c.documentNumber })).reverse() });
    },
    async marginScheme() { h.need(); return delay({ ...margin }); },
    async updateMarginScheme(m) {
      h.need('TAX.MANAGE');
      if (m.version !== margin.version) throw problem(412, 'GEN-412', 'Someone else changed the margin scheme record. Reload it.');
      if (m.approved && (!m.reference?.trim() || !m.approvedOn)) throw problem(400, 'VAL-001', 'Give the Tax Authority\'s approval reference and date.');
      margin = { ...margin, approved: m.approved, reference: m.reference?.trim() || null, approvedOn: m.approvedOn, note: m.note, version: margin.version + 1, taxCodeStatus: m.approved ? 'active' : 'inactive' };
      h.marginCodeStatus(m.approved);
    },
  };

  return {
    api,
    marginOn,
    /** Before a sale is written: trade-in credit is this customer's and within what is left; store credit needs the customer and the balance. */
    checkPayments(tenders: Tender[], customerId: string | null, refund: boolean) {
      let sc = 0n;
      for (const p of tenders) {
        if (p.method === 'store_credit') { if (!customerId) throw problem(400, 'POS-016', 'Store credit belongs to a customer: choose the customer first.'); sc += p.amount; }
        if (p.method !== 'trade_in') continue;
        const r = row(p.tradeInId!);
        if (r.status !== 'accepted') throw problem(409, 'TRD-001', `Trade-in ${r.number} is ${r.status}: it has no credit left.`);
        if (r.customerId !== customerId) throw problem(409, 'TRD-005', `Trade-in ${r.number} is ${r.customerName}'s: choose that customer for the sale.`);
        if (p.amount > outstanding(r)) throw problem(409, 'TRD-005', `Only ${fromBaisa(outstanding(r))} is left on trade-in ${r.number}.`);
      }
      if (!refund && sc > 0n && sc > balance(customerId!)) throw problem(409, 'TRD-006', `The customer has ${fromBaisa(balance(customerId!))} of store credit.`);
    },
    /** After the sale (or its credit note) is written: settles trade-in credit and moves store credit. */
    applyPayments(tenders: Tender[], customerId: string | null, saleId: string, number: string, refund: boolean, by: string) {
      for (const p of tenders) {
        if (p.method === 'store_credit')
          credit.push({ id: id(), customerId: customerId!, amount: refund ? p.amount : -p.amount, sourceDocType: 'sale', sourceDocId: saleId, note: `${refund ? 'Credit note' : 'Sale'} ${number}`, createdAt: now(), documentNumber: number });
        if (p.method !== 'trade_in') continue;
        const r = row(p.tradeInId!);
        r.settlements.push({ id: id(), kind: 'sale', amount: n(refund ? -p.amount : p.amount), saleNumber: number, createdAt: now(), createdBy: by });
        r.status = outstanding(r) === 0n ? 'settled' : 'accepted';
        r.settledAt = r.status === 'settled' ? now() : null;
        if (!refund) r.saleId = saleId;
        r.version++;
      }
    },
  };
}
