// Emergency mode (6.4) inside the Windows app: the same POS screens run from the app's bundled copy and talk to this
// local API instead of the server. Sales are priced with the shared POS arithmetic against the last snapshot, kept in
// the app's encrypted store with this terminal's own numbers, and uploaded in order when the server is back.
import { ApiError, type KiteApi, type Me, type PosItem, type PosPolicy, type SaleInput, type Shift, type ShiftFigures, type TenderInput, type Tokens } from '@kite/api';
import { checkTenders, countTotal, fromBaisa, PosError, priceLine, qrPayload, toBaisa, toNumber, totals, UNLIMITED, type Item, type ReceiptModel, type Tender } from '@kite/pos';

export interface SnapshotUser { id: string; username: string; displayNameEn: string; displayNameAr: string | null; permissions: string[]; discountLimitPercent?: number; discountLimitAmount?: number }
export interface Snapshot {
  generatedAt: string; businessDate: string; numberPrefix: string; policy: PosPolicy; items: PosItem[]; barcodes: { code: string; variantId: string }[]; users: SnapshotUser[];
  business: { legalNameEn: string; legalNameAr: string; tradeNameEn: string | null; tradeNameAr: string | null; vatNumber: string | null; crNumber: string | null; pricesIncludeTax: boolean; addressEn: string | null; addressAr: string | null; phone: string | null };
  terminal: { id: string; code: string; branchId: string; branchCode: string; branchNameEn: string; branchNameAr: string };
}
interface StoredRecord { seq: number; id: string; kind: string; recordedAt: string; payload: Record<string, unknown> }
export interface EmergencyBridge {
  snapshot(): Promise<unknown>;
  status(): Promise<unknown>;
  append(kind: string, payload: unknown): Promise<unknown>;
  records(): Promise<unknown[]>;
  verifyPin(username: string, pin: string): Promise<unknown>;
  end(): Promise<{ ok: boolean; error?: string }>;
}

const offline = (title: string) => new ApiError({ status: 503, code: 'POS-018', title });
const id = () => crypto.randomUUID();
const n = (b: bigint) => toNumber(b);
const wrap = <T>(f: () => T): T => { try { return f(); } catch (e) { if (e instanceof PosError) throw new ApiError({ status: 400, code: e.code, title: e.message }); throw e; } };

export function createEmergencyApi(bridge: EmergencyBridge): KiteApi {
  let snap: Snapshot | null = null;
  let user: SnapshotUser | null = null;
  const approvals = new Map<string, { action: string; used: boolean }>();
  const parked = new Map<string, { label: string | null; cart: unknown; at: string }>();
  const load = async () => (snap ??= (await bridge.snapshot()) as Snapshot);
  const need = (perm?: string) => {
    if (!user) throw new ApiError({ status: 401, code: 'AUTH-401', title: 'Sign in required.' });
    if (perm && !user.permissions.includes(perm)) throw new ApiError({ status: 403, code: 'AUTH-403', title: `You do not have permission ${perm}.` });
    return user;
  };
  const records = async () => ((await bridge.records()) as StoredRecord[]).slice().reverse();
  const openShift = async () => ((await bridge.status()) as { shift: (Record<string, unknown> & { shiftId: string; number: string; cashierId: string; openingFloat: number }) | null }).shift;
  const shiftRow = (s: { shiftId: string; number: string; cashierId: string; openingFloat: number }, at: string): Shift => ({
    id: s.shiftId, branchId: snap!.terminal.branchId, terminalId: snap!.terminal.id, cashierId: s.cashierId, number: s.number, status: 'open', isEmergency: true,
    openedAt: at, openingFloat: s.openingFloat, closedAt: null, expectedCash: null, countedCash: null, variance: null, varianceReason: null, varianceAcceptedBy: null, version: 1,
  });
  const figures = async (shiftId: string, opening: number): Promise<ShiftFigures> => {
    const recs = (await records()).filter((r) => r.payload.shiftId === shiftId);
    const sales = recs.filter((r) => r.kind === 'sale').map((r) => r.payload as { lines: { net: number; tax: number; gross: number; discount: number }[]; payments: TenderInput[]; cashRounding: number });
    const pays = sales.flatMap((s) => s.payments);
    const sumOf = (m: string) => pays.filter((p) => p.method === m).reduce((a, p) => a + toBaisa(p.amount), 0n);
    const ev = (k: string) => recs.filter((r) => r.kind === 'cash_event' && r.payload.kind === k).reduce((a, r) => a + toBaisa(r.payload.amount as number), 0n);
    const lines = sales.flatMap((s) => s.lines);
    const L = (k: 'net' | 'tax' | 'gross' | 'discount') => lines.reduce((a, l) => a + toBaisa(l[k]), 0n);
    const cards = new Map<string, bigint>();
    for (const p of pays.filter((p) => p.method === 'card')) cards.set(p.cardTerminal ?? '', (cards.get(p.cardTerminal ?? '') ?? 0n) + toBaisa(p.amount));
    const cash = sumOf('cash');
    return {
      salesCount: sales.length, salesGross: n(L('gross')), salesNet: n(L('net')), salesTax: n(L('tax')), salesDiscount: n(L('discount')), creditNotesCount: 0, creditNotesGross: 0,
      cashRounding: n(sales.reduce((a, s) => a + toBaisa(s.cashRounding), 0n)), cashPayments: n(cash), cardPayments: n(sumOf('card')), transferPayments: n(sumOf('bank_transfer')),
      payIns: n(ev('pay_in')), payOuts: n(ev('pay_out')), drops: n(ev('drop')), noSales: recs.filter((r) => r.kind === 'cash_event' && r.payload.kind === 'no_sale').length,
      openingFloat: opening, expectedCash: n(toBaisa(opening) + cash + ev('pay_in') - ev('pay_out') - ev('drop')), cardByTerminal: [...cards].map(([cardTerminal, total]) => ({ cardTerminal, total: n(total) })),
    };
  };
  const useApproval = (aid: string | null | undefined, action: string) => {
    const a = aid ? approvals.get(aid) : undefined;
    if (!a || a.used || a.action !== action) throw new ApiError({ status: 400, code: 'POS-008', title: 'That approval is not valid or was used already.' });
    a.used = true;
  };
  const itemOf = (vid: string): Item => {
    const i = snap!.items.find((x) => x.variantId === vid);
    if (!i) throw new ApiError({ status: 400, code: 'CAT-002', title: 'An item in the cart is not in this till\'s catalog copy.' });
    return i;
  };
  const tokens = (): Tokens => ({ accessToken: 'emergency', accessExpiresAt: new Date(Date.now() + 864e5).toISOString(), refreshToken: '', refreshExpiresAt: new Date(Date.now() + 864e5).toISOString(), mfaRequired: false, mustChangePassword: false });

  const impl: Partial<KiteApi> = {
    async me() {
      const s = await load();
      const u = need();
      const me: Me = {
        user: { id: u.id, username: u.username, displayNameEn: u.displayNameEn, displayNameAr: u.displayNameAr, preferredLanguage: 'en', totpEnabled: false, defaultBranchId: s.terminal.branchId },
        business: { legalNameEn: s.business.legalNameEn, legalNameAr: s.business.legalNameAr, currency: 'OMR', decimals: 3, pricesIncludeTax: s.business.pricesIncludeTax },
        permissions: u.permissions, allBranches: false, branches: [{ id: s.terminal.branchId, code: s.terminal.branchCode, nameEn: s.terminal.branchNameEn, nameAr: s.terminal.branchNameAr, isHeadOffice: false, status: 'active' }],
        mfaPending: false, mustChangePassword: false, serverRole: 'emergency',
      };
      return me;
    },
    async login() { throw offline('In emergency mode, sign in with your PIN.'); },
    async loginAt() { throw offline('In emergency mode, sign in with your PIN.'); },
    async pinLoginAt(username, pin) {
      await load();
      const u = (await bridge.verifyPin(username, pin)) as SnapshotUser | null;
      if (!u) throw new ApiError({ status: 401, code: 'AUTH-001', title: 'Username or PIN is not correct.' });
      user = { ...u, ...snap!.users.find((x) => x.id === u.id) };
      if (!user.permissions.includes('POS.SELL')) { user = null; throw new ApiError({ status: 403, code: 'AUTH-403', title: 'You may not sell in this branch.' }); }
      return tokens();
    },
    async logout() { user = null; },
    async posPolicy() { return (await load()).policy; },
    async posItems(q, _b, limit) {
      const s = await load();
      const k = q?.trim().toLowerCase();
      return s.items.filter((i) => !k || i.sku.toLowerCase().startsWith(k) || i.descriptionEn.toLowerCase().includes(k) || i.descriptionAr.includes(q!.trim()) || s.barcodes.some((b) => b.code === q?.trim() && b.variantId === i.variantId)).slice(0, limit ?? 30);
    },
    async posItemByBarcode(code) {
      const s = await load();
      const c = code.trim();
      const vid = s.barcodes.find((b) => b.code === c)?.variantId ?? s.items.find((i) => i.sku.toUpperCase() === c.toUpperCase())?.variantId;
      const i = s.items.find((x) => x.variantId === vid);
      if (!i) throw new ApiError({ status: 404, code: 'CAT-002', title: `No item has barcode or SKU ${code}.` });
      return i;
    },
    async currentShift() {
      await load();
      need();
      const s = await openShift();
      if (!s) return { shift: null };
      const opened = (await records()).find((r) => r.kind === 'shift_open' && r.payload.shiftId === s.shiftId)?.recordedAt ?? new Date().toISOString();
      const f = await figures(s.shiftId, s.openingFloat);
      return { shift: shiftRow(s, opened), figures: { ...f, expectedCash: 0, cashPayments: 0 }, blind: true };
    },
    async openShift(o) {
      const s = await load();
      const u = need('POS.SHIFT.OPEN');
      if (await openShift()) throw new ApiError({ status: 409, code: 'POS-002', title: 'This till already has an open emergency shift.' });
      const opening = o.openingCount ? n(wrap(() => countTotal(o.openingCount!, s.policy.denominations))) : o.openingFloat ?? 0;
      const rec = (await bridge.append('shift_open', { shiftId: id(), cashierId: u.id, openingFloat: opening })) as StoredRecord;
      return shiftRow(rec.payload as never, rec.recordedAt);
    },
    async quote(q: SaleInput) {
      const s = await load(); const u = need();
      const limit = u.permissions.includes('POS.DISCOUNT.ABOVE_LIMIT') ? UNLIMITED : { percent: u.discountLimitPercent ?? 0, amount: u.discountLimitAmount ?? 0, unlimited: false };
      const lines = wrap(() => q.lines.map((l, i) => priceLine(i + 1, l, itemOf(l.variantId), s.business.pricesIncludeTax, limit)));
      const t = totals(lines);
      return { lines: lines.map((l) => ({ lineNo: l.lineNo, variantId: l.item.variantId, sku: l.item.sku, descriptionEn: l.item.descriptionEn, descriptionAr: l.item.descriptionAr, qty: n(l.qty), listPrice: n(l.listPrice), unitPrice: n(l.unitPrice), discount: n(l.discount), net: n(l.net), tax: n(l.tax), gross: n(l.gross), taxRate: Number(l.item.taxRate), needsPriceOverride: l.needsPriceOverride, needsDiscountApproval: l.needsDiscountApproval })),
        totals: { net: n(t.net), discount: n(t.discount), tax: n(t.tax), gross: n(t.gross) }, cashDue: n(t.gross) };
    },
    async completeSale(saleId, req) {
      const s = await load(); const u = need('POS.SELL');
      const shift = await openShift();
      if (!shift) throw new ApiError({ status: 409, code: 'POS-001', title: 'Open a shift on this till first.' });
      const limit = u.permissions.includes('POS.DISCOUNT.ABOVE_LIMIT') ? UNLIMITED : { percent: u.discountLimitPercent ?? 0, amount: u.discountLimitAmount ?? 0, unlimited: false };
      const lines = wrap(() => req.lines.map((l, i) => priceLine(i + 1, l, itemOf(l.variantId), s.business.pricesIncludeTax, limit)));
      const used = new Set<string>();
      for (const l of lines) {
        const action = l.needsPriceOverride && !u.permissions.includes('POS.PRICE.OVERRIDE') ? 'POS.PRICE.OVERRIDE' : l.needsDiscountApproval ? 'POS.DISCOUNT.ABOVE_LIMIT' : null;
        if (!action) continue;
        const aid = req.lines[l.lineNo - 1]!.approvalId ?? req.approvalId;
        if (!aid) throw new ApiError({ status: 403, code: action === 'POS.PRICE.OVERRIDE' ? 'POS-006' : 'POS-007', title: 'A manager must approve this line.' });
        if (!used.has(aid)) { useApproval(aid, action); used.add(aid); }
      }
      const t = totals(lines);
      const tenders: Tender[] = (req.payments ?? []).map((p) => ({ ...p, amount: toBaisa(p.amount), tendered: p.tendered == null ? null : toBaisa(p.tendered) }));
      const paid = wrap(() => checkTenders(tenders, t.gross, s.policy.cashRounding));
      const rec = (await bridge.append('sale', {
        saleId, shiftId: shift.shiftId, cashierId: u.id, cashRounding: n(paid.rounding),
        lines: lines.map((l) => ({ variantId: l.item.variantId, qty: n(l.qty), unitPrice: n(l.unitPrice), listPrice: n(l.listPrice), discount: n(l.discount), net: n(l.net), tax: n(l.tax), gross: n(l.gross),
          taxRate: Number(l.item.taxRate), taxCodeId: l.item.taxCodeId, productType: l.item.productType, descriptionEn: l.item.descriptionEn, descriptionAr: l.item.descriptionAr, warrantyDays: l.item.warrantyDays })),
        payments: (req.payments ?? []).map((p) => ({ method: p.method, amount: p.amount, tendered: p.tendered ?? null, cardTerminal: p.cardTerminal ?? null, approvalCode: p.approvalCode ?? null, reference: p.reference ?? null })),
      })) as StoredRecord;
      const change = tenders.reduce((a, p) => a + (p.tendered == null ? 0n : p.tendered - p.amount), 0n);
      return { id: saleId, number: rec.payload.number as string, docType: 'simplified_invoice', gross: n(t.gross), cashRounding: n(paid.rounding), paid: n(paid.paid), change: n(change), stockExceptions: 0 };
    },
    async receipt(saleId, reprint) {
      const s = await load();
      const rec = (await records()).find((r) => r.kind === 'sale' && r.payload.saleId === saleId);
      if (!rec) throw new ApiError({ status: 404, code: 'GEN-404', title: 'That sale was not found on this till.' });
      const p = rec.payload as { number: string; cashRounding: number; cashierId: string; lines: { descriptionEn: string; descriptionAr: string; qty: number; unitPrice: number; discount: number; net: number; tax: number; gross: number; taxRate: number }[]; payments: { method: string; amount: number; tendered: number | null; cardTerminal: string | null }[] };
      const net = p.lines.reduce((a, l) => a + toBaisa(l.net), 0n), tax = p.lines.reduce((a, l) => a + toBaisa(l.tax), 0n), gross = p.lines.reduce((a, l) => a + toBaisa(l.gross), 0n);
      const rates = [...new Set(p.lines.map((l) => l.taxRate))];
      const cashier = s.users.find((x) => x.id === p.cashierId);
      const r: ReceiptModel = {
        saleId, number: p.number, docType: 'simplified_invoice', titleEn: 'Simplified tax invoice', titleAr: 'فاتورة ضريبية مبسطة', originalNumber: null, creditReason: null,
        localTime: new Date(rec.recordedAt).toLocaleString('sv-SE', { timeZone: 'Asia/Muscat' }).replace(' ', 'T'), terminalCode: s.terminal.code, cashierName: cashier?.displayNameEn ?? '',
        customerName: null, customerVat: null,
        business: { nameEn: s.business.tradeNameEn ?? s.business.legalNameEn, nameAr: s.business.tradeNameAr ?? s.business.legalNameAr, vatNumber: s.business.vatNumber, crNumber: s.business.crNumber, addressEn: s.business.addressEn, addressAr: s.business.addressAr, phone: s.business.phone },
        branch: { nameEn: s.terminal.branchNameEn, nameAr: s.terminal.branchNameAr, vatNumber: null, crNumber: null, addressEn: null, addressAr: null, phone: null },
        lines: p.lines.map((l) => ({ descriptionEn: l.descriptionEn, descriptionAr: l.descriptionAr, qty: l.qty, unitPrice: l.unitPrice, discount: l.discount, net: l.net, tax: l.tax, gross: l.gross, taxRate: l.taxRate })),
        net: n(net), discount: n(p.lines.reduce((a, l) => a + toBaisa(l.discount), 0n)), tax: n(tax), gross: n(gross), cashRounding: p.cashRounding, total: n(gross + toBaisa(p.cashRounding)),
        taxBreakdown: rates.map((rate) => ({ rate, net: n(p.lines.filter((l) => l.taxRate === rate).reduce((a, l) => a + toBaisa(l.net), 0n)), tax: n(p.lines.filter((l) => l.taxRate === rate).reduce((a, l) => a + toBaisa(l.tax), 0n)) })),
        payments: p.payments.map((x) => ({ method: x.method, amount: x.amount, tendered: x.tendered, change: x.tendered == null ? null : n(toBaisa(x.tendered) - toBaisa(x.amount)), cardTerminal: x.cardTerminal })),
        // The server makes the official QR when the sale is uploaded; this one carries the same fields for the customer now.
        qrPayload: qrPayload(s.business.tradeNameEn ?? s.business.legalNameEn, s.business.vatNumber, new Date(rec.recordedAt), gross, tax, saleId, fromBaisa),
        footerEn: s.policy.receiptFooterEn, footerAr: s.policy.receiptFooterAr, isCopy: !!reprint, isEmergency: true, paperWidthMm: 80,
      };
      return r;
    },
    async pinApproval(a) {
      const s = await load(); const u = need();
      const approver = (await bridge.verifyPin(a.approverUsername, a.pin)) as SnapshotUser | null;
      if (!approver) throw new ApiError({ status: 403, code: 'POS-008', title: 'The PIN is not correct.' });
      if (approver.id === u.id) throw new ApiError({ status: 403, code: 'POS-008', title: 'You cannot approve your own request.' });
      const full = s.users.find((x) => x.id === approver.id) ?? approver;
      if (!full.permissions.includes(a.action)) throw new ApiError({ status: 403, code: 'POS-008', title: `${full.displayNameEn} does not hold ${a.action}.` });
      const aid = id();
      approvals.set(aid, { action: a.action, used: false });
      return { id: aid, status: 'approved', approverName: full.displayNameEn, expiresAt: new Date(Date.now() + 300_000).toISOString() };
    },
    async requestApproval() { throw offline('Remote approvals need the server. Ask the manager to type their PIN on this till.'); },
    async cashEvent(e) {
      const u = need();
      const shift = await openShift();
      if (!shift) throw new ApiError({ status: 409, code: 'POS-001', title: 'Open a shift on this till first.' });
      const needed = e.kind === 'pay_out' ? 'POS.CASH.PAY_OUT' : e.kind === 'no_sale' ? 'POS.DRAWER.NO_SALE' : 'POS.SHIFT.OPEN';
      if (!u.permissions.includes(needed)) {
        if (!e.approvalId || needed === 'POS.SHIFT.OPEN') throw new ApiError({ status: 403, code: 'AUTH-403', title: `${needed} is needed. Ask a manager to approve.` });
        useApproval(e.approvalId, needed);
      }
      if (!e.reason?.trim()) throw new ApiError({ status: 400, code: 'VAL-001', title: 'Give a reason.' });
      const eventId = id();
      await bridge.append('cash_event', { eventId, shiftId: shift.shiftId, kind: e.kind, amount: e.kind === 'no_sale' ? 0 : e.amount, reason: e.reason.trim(), createdBy: u.id });
      return { id: eventId };
    },
    async shift() {
      const shift = await openShift();
      if (!shift) throw new ApiError({ status: 404, code: 'GEN-404', title: 'No open emergency shift.' });
      return { shift: shiftRow(shift, new Date().toISOString()), report: 'X', figures: await figures(shift.shiftId, shift.openingFloat), cardSettlements: [], cashEvents: [] };
    },
    async closeShift(c) {
      const s = await load(); const u = need();
      const shift = await openShift();
      if (!shift) throw new ApiError({ status: 409, code: 'POS-001', title: 'No open emergency shift.' });
      const f = await figures(shift.shiftId, shift.openingFloat);
      const counted = c.closingCount ? wrap(() => countTotal(c.closingCount!, s.policy.denominations)) : toBaisa(c.countedCash ?? 0);
      const variance = counted - toBaisa(f.expectedCash);
      if ((variance < 0n ? -variance : variance) > toBaisa(s.policy.varianceThreshold)) {
        if (!c.varianceReason?.trim()) throw new ApiError({ status: 409, code: 'POS-014', title: 'The count does not match. Give a reason and ask a manager to accept it.' });
        if (!u.permissions.includes('POS.SHIFT.ACCEPT_VARIANCE')) {
          if (!c.approvalId) throw new ApiError({ status: 409, code: 'POS-014', title: 'A manager must accept the difference.' });
          useApproval(c.approvalId, 'POS.SHIFT.ACCEPT_VARIANCE');
        }
      }
      await bridge.append('shift_close', { shiftId: shift.shiftId, closedBy: u.id, countedCash: n(counted), varianceReason: c.varianceReason ?? null,
        cardSettlements: (c.cardSettlements ?? []).map((x) => ({ cardTerminal: x.cardTerminal, batchTotal: x.batchTotal, reason: x.reason ?? null })) });
      return { id: shift.shiftId, number: shift.number, figures: f, counted: n(counted), variance: n(variance) };
    },
    async park(c) { need(); parked.set(c.id, { label: c.label ?? null, cart: c.cart, at: new Date().toISOString() }); },
    async parked() { need(); return [...parked].map(([pid, p]) => ({ id: pid, label: p.label, parkedAt: p.at, parkedBy: user?.displayNameEn ?? '', terminalCode: snap?.terminal.code ?? '', cart: JSON.stringify(p.cart) })); },
    async recall(pid) { need(); const p = parked.get(pid); if (!p) throw new ApiError({ status: 404, code: 'GEN-404', title: 'That parked cart was already recalled.' }); parked.delete(pid); return p.cart; },
    async printers() { return []; },
    async customers() { return []; },
  };
  return new Proxy(impl as KiteApi, {
    get(target, key: string) {
      if (key in target) return (target as unknown as Record<string, unknown>)[key];
      if (key === 'onSessionEnded' || key === 'then') return undefined;
      return () => Promise.reject(offline('This needs the KITE server. It is not available in emergency mode.'));
    },
    set(target, key: string, value) { (target as unknown as Record<string, unknown>)[key] = value; return true; },
  });
}
