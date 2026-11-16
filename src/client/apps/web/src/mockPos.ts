// In-memory Phase 4 server for tests, `npm run dev:mock` and design review: catalog, stock, customers, shifts, sales,
// returns, approvals, printers. Prices and totals use the same arithmetic as the real server (@kite/pos), but the real
// server is the source of truth and checks everything again.
import { ApiError, type ApprovalAction, type ApprovalRow, type Balance, type Customer, type PosApi, type PosItem, type PosPolicy, type Printer, type ProductView,
  type SaleDetail, type SaleLine, type SaleListRow, type Shift, type ShiftFigures, type TenderInput, type Terminal, type Tokens, type User } from '@kite/api';
import { apportionReturn, checkTenders, countTotal, fromBaisa, PosError, priceLine, qrPayload, toBaisa, toNumber, totals, UNLIMITED, type Item, type ReceiptModel, type Tender } from '@kite/pos';

export interface MockPosContext {
  need: (perm?: string) => User;
  current: () => User | null;
  setCurrent: (u: User) => void;
  permsOf: (u: User) => string[];
  users: User[];
  passwords: Map<string, string>;
  pins: Map<string, string>;
  terminals: Terminal[];
  branchId: string;
  taxCodeId: string;
  business: () => { legalNameEn: string; legalNameAr: string; vatNumber: string | null; crNumber: string | null; pricesIncludeTax: boolean };
  tokens: (u: User) => Tokens;
}

const problem = (status: number, code: string, title: string) => new ApiError({ status, code, title });
const id = () => crypto.randomUUID();
const now = () => new Date().toISOString();
const delay = <T>(v: T): Promise<T> => new Promise((r) => setTimeout(() => r(v), 15));
const n = (b: bigint) => toNumber(b);

interface StockRow { qty: bigint; value: bigint }
interface MockSale { detail: SaleDetail; shiftId: string; terminalId: string; payments: { method: string; amount: bigint; cardTerminal: string | null; tendered: bigint | null; change: bigint | null }[] }

export function createMockPos(ctx: MockPosContext): PosApi {
  const products: ProductView[] = [];
  const stock = new Map<string, StockRow>();
  const customers: Customer[] = [];
  const shifts: (Shift & { cashierName: string })[] = [];
  const sales: MockSale[] = [];
  const cashEvents: { shiftId: string; kind: string; amount: bigint; reason: string; createdAt: string }[] = [];
  const approvals: (ApprovalRow & { requesterId: string; approverId: string | null })[] = [];
  const parked: { id: string; label: string | null; parkedAt: string; parkedBy: string; terminalCode: string; cart: string }[] = [];
  const printers: Printer[] = [];
  const deviceKeys = new Map<string, string>();
  const counters = new Map<string, number>();
  let currentTerminal: Terminal | null = null;
  let policy: PosPolicy = {
    id: id(), branchId: null, negativeStock: 'block', returnWindowDays: 14, cashRefundCap: 100, varianceThreshold: 1, blindClose: true, roundingLevel: 'line',
    cashRounding: 0.005, denominations: [50, 20, 10, 5, 1, 0.5, 0.1, 0.05, 0.025, 0.01, 0.005], receiptFooterEn: 'Thank you for shopping with us', receiptFooterAr: 'شكراً لتسوقكم معنا', version: 1,
  };

  const next = (kind: string, prefix: string) => {
    const v = (counters.get(kind) ?? 0) + 1;
    counters.set(kind, v);
    return `${prefix}-B01-${new Date().getFullYear()}-${String(v).padStart(6, '0')}`;
  };
  const seed = (nameEn: string, nameAr: string, type: ProductView['product']['productType'], sku: string, barcode: string, price: number, qty: number, cost: number, min: number | null = null) => {
    const pid = id(); const vid = id();
    products.push({
      product: { id: pid, brandId: null, brandName: null, categoryId: null, categoryName: null, nameEn, nameAr, productType: type, tracking: 'none', taxCodeId: ctx.taxCodeId, taxCode: 'VAT-STD', defaultWarrantyDays: type === 'service' ? 0 : 30, status: 'active', version: 1 },
      variants: [{ variant: { id: vid, productId: pid, sku, colourEn: null, colourAr: null, storageGb: null, ramGb: null, status: 'active', version: 1, price, minPrice: min, priceFrom: '2026-01-01' }, barcodes: [{ id: id(), code: barcode }] }],
    });
    if (type !== 'service') stock.set(vid, { qty: toBaisa(qty), value: toBaisa(qty) * toBaisa(cost) / 1000n });
  };
  seed('USB-C cable 1m', 'كابل USB-C متر', 'accessory', 'CBL-C-1M', '6291000000011', 2.5, 40, 0.9);
  seed('Screen guard', 'واقي شاشة', 'accessory', 'GRD-GLASS', '6291000000028', 4, 25, 1.5, 3.5);
  seed('Charger 20W', 'شاحن 20 واط', 'accessory', 'CHG-20W', '6291000000035', 6.75, 18, 3.2);
  seed('Phone case', 'غطاء هاتف', 'accessory', 'CASE-STD', '6291000000042', 3.25, 30, 1.1);
  seed('Earphones', 'سماعات', 'accessory', 'EAR-BASIC', '6291000000059', 5.5, 2, 2.8);
  seed('Battery (spare part)', 'بطارية (قطعة غيار)', 'spare_part', 'BAT-SPARE', '6291000000066', 12, 10, 6.5);
  seed('Phone setup service', 'خدمة إعداد الهاتف', 'service', 'SVC-SETUP', '6291000000073', 2, 0, 0);
  customers.push({ id: id(), number: 'C-000001', kind: 'company', nameEn: 'Gulf Trading LLC', nameAr: 'الخليج للتجارة', phoneE164: '+96891234567', email: null, vatNumber: 'OM1199887766', crNumber: null, addressEn: null, addressAr: null, status: 'active', version: 1 });

  const variantOf = (vid: string) => {
    for (const p of products) for (const v of p.variants) if (v.variant.id === vid) return { p, v };
    return null;
  };
  const itemOf = (vid: string): Item & { stock: number; barcode: string | null } => {
    const f = variantOf(vid);
    if (!f) throw problem(400, 'CAT-002', 'An item in the cart is not in the catalog.');
    return {
      variantId: vid, sku: f.v.variant.sku, productType: f.p.product.productType, tracking: f.p.product.tracking, descriptionEn: f.p.product.nameEn, descriptionAr: f.p.product.nameAr,
      taxCodeId: f.p.product.taxCodeId, taxKind: 'standard', taxRate: 5, listPrice: f.v.variant.price, minPrice: f.v.variant.minPrice, warrantyDays: f.p.product.defaultWarrantyDays,
      active: f.p.product.status === 'active' && f.v.variant.status === 'active', stock: n(stock.get(vid)?.qty ?? 0n), barcode: f.v.barcodes[0]?.code ?? null,
    };
  };
  const posItem = (vid: string): PosItem => { const i = itemOf(vid); return { ...i, productType: i.productType as PosItem['productType'], tracking: i.tracking as PosItem['tracking'], taxRate: 5, listPrice: i.listPrice as number | null, minPrice: i.minPrice as number | null }; };
  const wrap = <T>(f: () => T): T => { try { return f(); } catch (e) { if (e instanceof PosError) throw problem(e.code === 'POS-010' ? 409 : 400, e.code, e.message); throw e; } };
  const terminal = () => {
    const u = ctx.need();
    if (!currentTerminal) throw problem(403, 'POS-004', 'Sign in on a POS terminal to do this.');
    return { u, t: currentTerminal };
  };
  const openShiftOf = (tid: string) => shifts.find((s) => s.terminalId === tid && s.status === 'open' && !s.isEmergency);
  const myShift = () => {
    const { u, t } = terminal();
    const s = openShiftOf(t.id);
    if (!s) throw problem(409, 'POS-001', 'Open a shift on this terminal first.');
    if (s.cashierId !== u.id) throw problem(403, 'POS-003', "This terminal's shift belongs to another cashier.");
    return { u, t, s };
  };
  const figures = (s: Shift): ShiftFigures => {
    const mine = sales.filter((x) => x.shiftId === s.id);
    const pays = mine.flatMap((x) => x.payments);
    const sumOf = (m: string) => pays.filter((p) => p.method === m).reduce((a, p) => a + p.amount, 0n);
    const ev = (k: string) => cashEvents.filter((e) => e.shiftId === s.id && e.kind === k).reduce((a, e) => a + e.amount, 0n);
    const sold = mine.filter((x) => x.detail.sale.docType !== 'credit_note');
    const credit = mine.filter((x) => x.detail.sale.docType === 'credit_note');
    const cash = sumOf('cash');
    const cards = new Map<string, bigint>();
    for (const p of pays.filter((p) => p.method === 'card')) cards.set(p.cardTerminal ?? '', (cards.get(p.cardTerminal ?? '') ?? 0n) + p.amount);
    const g = (xs: MockSale[], k: 'grossTotal' | 'netTotal' | 'taxTotal' | 'discountTotal' | 'cashRounding') => xs.reduce((a, x) => a + toBaisa(x.detail.sale[k]), 0n);
    return {
      salesCount: sold.length, salesGross: n(g(sold, 'grossTotal')), salesNet: n(g(sold, 'netTotal') - g(credit, 'netTotal')), salesTax: n(g(sold, 'taxTotal') - g(credit, 'taxTotal')),
      salesDiscount: n(g(sold, 'discountTotal')), creditNotesCount: credit.length, creditNotesGross: n(g(credit, 'grossTotal')), cashRounding: n(g(sold, 'cashRounding') - g(credit, 'cashRounding')),
      cashPayments: n(cash), cardPayments: n(sumOf('card')), transferPayments: n(sumOf('bank_transfer')), payIns: n(ev('pay_in')), payOuts: n(ev('pay_out')), drops: n(ev('drop')),
      noSales: cashEvents.filter((e) => e.shiftId === s.id && e.kind === 'no_sale').length, openingFloat: s.openingFloat,
      expectedCash: n(toBaisa(s.openingFloat) + cash + ev('pay_in') - ev('pay_out') - ev('drop')), cardByTerminal: [...cards].map(([cardTerminal, total]) => ({ cardTerminal, total: n(total) })),
    };
  };
  const useApproval = (aid: string | null | undefined, action: ApprovalAction, requester: string) => {
    const a = approvals.find((x) => x.id === aid);
    if (!a || a.actionCode !== action || a.requesterId !== requester || a.status !== 'approved' || Date.parse(a.expiresAt) < Date.now())
      throw problem(400, 'POS-008', 'That approval is not valid, was used already or has expired.');
    a.status = 'used';
  };
  const can = (u: User, p: string) => ctx.permsOf(u).includes(p);
  const move = (vid: string, qty: bigint, unitCost?: bigint): bigint => {
    const row = stock.get(vid) ?? { qty: 0n, value: 0n };
    let value: bigint;
    if (qty > 0n) value = (qty * (unitCost ?? (row.qty > 0n ? (row.value * 1000n) / row.qty : 0n))) / 1000n;
    else value = row.qty >= -qty && row.qty > 0n ? -((row.value * -qty) / row.qty) : -((-qty * (row.qty > 0n ? (row.value * 1000n) / row.qty : 0n)) / 1000n);
    stock.set(vid, { qty: row.qty + qty, value: row.value + value });
    return value;
  };
  const write = (detail: Omit<SaleDetail['sale'], 'qrPayload' | 'einvoiceUuid'>, lines: SaleLine[], tenders: Tender[], refund: boolean, shiftId: string, t: Terminal): SaleDetail => {
    const uuid = id();
    const b = ctx.business();
    const sale = { ...detail, qrPayload: qrPayload(b.legalNameEn, b.vatNumber, new Date(), toBaisa(detail.grossTotal), toBaisa(detail.taxTotal), uuid, fromBaisa), einvoiceUuid: uuid };
    const full: SaleDetail = { sale, lines, payments: tenders.map((p) => ({ method: p.method, amount: n(refund ? -p.amount : p.amount), tendered: p.tendered == null ? null : n(p.tendered), changeGiven: p.tendered == null ? null : n(p.tendered - p.amount), cardTerminal: p.cardTerminal ?? null })), creditNotes: [] };
    sales.push({ detail: full, shiftId, terminalId: t.id, payments: tenders.map((p) => ({ method: p.method, amount: refund ? -p.amount : p.amount, cardTerminal: p.cardTerminal ?? null, tendered: p.tendered ?? null, change: p.tendered == null ? null : p.tendered - p.amount })) });
    return full;
  };
  const toTenders = (xs: TenderInput[] | undefined): Tender[] => (xs ?? []).map((p) => ({ ...p, amount: toBaisa(p.amount), tendered: p.tendered == null ? null : toBaisa(p.tendered) }));
  const summary = (d: SaleDetail, paid: bigint, change: bigint) => ({ id: d.sale.id, number: d.sale.number, docType: d.sale.docType, gross: d.sale.grossTotal, cashRounding: d.sale.cashRounding, paid: n(paid), change: n(change), stockExceptions: 0 });
  const priceCart = (u: User, lines: { variantId: string; qty: number; unitPrice?: number | null; discount?: number | null }[]) => wrap(() => {
    if (lines.length === 0) throw new PosError('VAL-001', 'The cart is empty.');
    const limit = can(u, 'POS.DISCOUNT.ABOVE_LIMIT') ? UNLIMITED : { percent: 5, amount: 2, unlimited: false };
    return lines.map((l, i) => priceLine(i + 1, l, itemOf(l.variantId), ctx.business().pricesIncludeTax, limit));
  });

  return {
    async brands() { ctx.need('CATALOG.VIEW'); return delay([]); },
    async createBrand(b) { ctx.need('CATALOG.MANAGE'); return delay({ id: id(), ...b, status: 'active', version: 1 }); },
    async categories() { ctx.need('CATALOG.VIEW'); return delay([]); },
    async createCategory(c) { ctx.need('CATALOG.MANAGE'); return delay({ id: id(), parentId: c.parentId ?? null, nameEn: c.nameEn, nameAr: c.nameAr, status: 'active', version: 1 }); },
    async products(q) {
      ctx.need('CATALOG.VIEW');
      const k = q?.trim().toLowerCase();
      return delay(products.filter((p) => !k || p.product.nameEn.toLowerCase().includes(k) || p.product.nameAr.includes(k) || p.variants.some((v) => v.variant.sku.toLowerCase().includes(k) || v.barcodes.some((b) => b.code === q))).map((p) => structuredClone(p)));
    },
    async product(pid) { ctx.need('CATALOG.VIEW'); const p = products.find((x) => x.product.id === pid); if (!p) throw problem(404, 'GEN-404', 'Product not found.'); return delay(structuredClone(p)); },
    async createProduct(p) {
      ctx.need('CATALOG.MANAGE');
      if (!p.nameEn.trim() || !p.nameAr.trim()) throw problem(400, 'VAL-001', 'English and Arabic names are required.');
      if (!p.variants?.length) throw problem(400, 'VAL-001', 'Add at least one variant (SKU).');
      for (const v of p.variants) {
        if (products.some((x) => x.variants.some((y) => y.variant.sku.toUpperCase() === v.sku.toUpperCase()))) throw problem(409, 'GEN-409', 'That SKU is already in use.');
        for (const b of v.barcodes ?? []) if (products.some((x) => x.variants.some((y) => y.barcodes.some((z) => z.code === b)))) throw problem(409, 'GEN-409', 'That barcode is already on another item.');
      }
      const pid = id();
      const view: ProductView = {
        product: { id: pid, brandId: p.brandId ?? null, brandName: null, categoryId: p.categoryId ?? null, categoryName: null, nameEn: p.nameEn, nameAr: p.nameAr, productType: p.productType, tracking: p.tracking ?? (p.productType === 'device' ? 'imei' : 'none'), taxCodeId: p.taxCodeId, taxCode: 'VAT-STD', defaultWarrantyDays: p.defaultWarrantyDays ?? 0, status: 'active', version: 1 },
        variants: p.variants.map((v) => ({ variant: { id: id(), productId: pid, sku: v.sku, colourEn: v.colourEn ?? null, colourAr: v.colourAr ?? null, storageGb: v.storageGb ?? null, ramGb: v.ramGb ?? null, status: 'active', version: 1, price: v.price ?? null, minPrice: v.minPrice ?? null, priceFrom: v.price == null ? null : now().slice(0, 10) }, barcodes: (v.barcodes ?? []).map((code) => ({ id: id(), code })) })),
      };
      products.push(view);
      return delay(structuredClone(view));
    },
    async updateProduct(pid, p) {
      ctx.need('CATALOG.MANAGE');
      const x = products.find((y) => y.product.id === pid);
      if (!x) throw problem(404, 'GEN-404', 'Product not found.');
      if (p.version !== x.product.version) throw problem(412, 'GEN-412', 'Someone else changed this product.');
      Object.assign(x.product, { nameEn: p.nameEn, nameAr: p.nameAr, productType: p.productType, taxCodeId: p.taxCodeId, defaultWarrantyDays: p.defaultWarrantyDays ?? x.product.defaultWarrantyDays, status: p.status ?? x.product.status, version: x.product.version + 1 });
      return delay(structuredClone(x));
    },
    async addVariant(pid, v) {
      ctx.need('CATALOG.MANAGE');
      const x = products.find((y) => y.product.id === pid)!;
      const vid = id();
      x.variants.push({ variant: { id: vid, productId: pid, sku: v.sku, colourEn: v.colourEn ?? null, colourAr: v.colourAr ?? null, storageGb: v.storageGb ?? null, ramGb: v.ramGb ?? null, status: 'active', version: 1, price: v.price ?? null, minPrice: v.minPrice ?? null, priceFrom: null }, barcodes: (v.barcodes ?? []).map((code) => ({ id: id(), code })) });
      return delay({ id: vid });
    },
    async addBarcode(vid, code) {
      ctx.need('CATALOG.MANAGE');
      if (products.some((x) => x.variants.some((y) => y.barcodes.some((z) => z.code === code)))) throw problem(409, 'GEN-409', 'That barcode is already on another item.');
      const b = { id: id(), code };
      variantOf(vid)!.v.barcodes.push(b);
      return delay(b);
    },
    async removeBarcode(bid) { ctx.need('CATALOG.MANAGE'); for (const p of products) for (const v of p.variants) v.barcodes = v.barcodes.filter((b) => b.id !== bid); },
    async setPrice(vid, p) { ctx.need('CATALOG.PRICE.MANAGE'); const v = variantOf(vid)!.v.variant; Object.assign(v, { price: p.price, minPrice: p.minPrice ?? null, priceFrom: p.effectiveFrom ?? now().slice(0, 10) }); },
    async prices(vid) { ctx.need('CATALOG.VIEW'); const v = variantOf(vid)!.v.variant; return delay(v.price == null ? [] : [{ id: id(), priceList: 'RETAIL', price: v.price, minPrice: v.minPrice, effectiveFrom: v.priceFrom ?? '2026-01-01', effectiveTo: null }]); },
    async setBranchVariant() { ctx.need('CATALOG.PRICE.MANAGE'); },
    async posItems(q, _branchId, limit) {
      ctx.need();
      const k = q?.trim().toLowerCase();
      const all = products.flatMap((p) => p.variants.map((v) => v.variant.id)).map(posItem);
      const hits = all.filter((i) => !k || i.barcode === q?.trim() || i.sku.toLowerCase().startsWith(k) || i.descriptionEn.toLowerCase().includes(k) || i.descriptionAr.includes(q!.trim()));
      return delay(hits.slice(0, limit ?? 30));
    },
    async posItemByBarcode(code) {
      ctx.need();
      const hit = products.flatMap((p) => p.variants).find((v) => v.barcodes.some((b) => b.code === code.trim()) || v.variant.sku.toUpperCase() === code.trim().toUpperCase());
      if (!hit) throw problem(404, 'CAT-002', `No item has barcode or SKU ${code}.`);
      return delay(posItem(hit.variant.id));
    },
    async customers(q) { ctx.need('CUSTOMERS.VIEW'); const k = q?.trim().toLowerCase(); return delay(customers.filter((c) => !k || c.nameEn.toLowerCase().includes(k) || (c.phoneE164 ?? '').includes(k.replace(/\D/g, '') || '§') || c.number.toLowerCase().includes(k))); },
    async createCustomer(c) {
      ctx.need('CUSTOMERS.MANAGE');
      if (!c.nameEn.trim()) throw problem(400, 'VAL-001', 'Name is required.');
      const digits = (c.phone ?? '').replace(/\D/g, '');
      const cust: Customer = { id: id(), number: `C-${String(customers.length + 1).padStart(6, '0')}`, kind: c.kind ?? 'individual', nameEn: c.nameEn, nameAr: c.nameAr ?? null, phoneE164: digits ? (digits.length === 8 ? `+968${digits}` : `+${digits}`) : null, email: c.email ?? null, vatNumber: c.vatNumber ?? null, crNumber: c.crNumber ?? null, addressEn: c.addressEn ?? null, addressAr: c.addressAr ?? null, status: 'active', version: 1 };
      customers.push(cust);
      return delay({ ...cust });
    },
    async updateCustomer(cid, c) { ctx.need('CUSTOMERS.MANAGE'); const x = customers.find((y) => y.id === cid)!; Object.assign(x, { nameEn: c.nameEn, nameAr: c.nameAr ?? null, vatNumber: c.vatNumber ?? null, status: c.status ?? x.status, version: x.version + 1 }); return delay({ ...x }); },
    async balances(f) {
      ctx.need('INV.VIEW');
      const k = f.q?.trim().toLowerCase();
      const rows: Balance[] = [...stock].map(([vid, s]) => { const i = itemOf(vid); return { variantId: vid, sku: i.sku, descriptionEn: i.descriptionEn, descriptionAr: i.descriptionAr, productType: i.productType as Balance['productType'], branchId: ctx.branchId, branchCode: 'B01', warehouseId: 'w', warehouseCode: 'SHOP', qty: n(s.qty), value: n(s.value), avgCost: s.qty > 0n ? Number(s.value) / Number(s.qty) : 0, reorderLevel: null, lastMovementAt: now() }; });
      return delay(rows.filter((r) => (!k || r.descriptionEn.toLowerCase().includes(k) || r.sku.toLowerCase().includes(k)) && (!f.lowOnly || r.qty <= 3)));
    },
    async movements() { ctx.need('INV.VIEW'); return delay([]); },
    async postAdjustment(a) {
      ctx.need('INV.ADJUST');
      if (!a.lines.length) throw problem(400, 'VAL-001', 'Add at least one line.');
      for (const l of a.lines) move(l.variantId, toBaisa(l.qty), l.unitCost == null ? undefined : toBaisa(l.unitCost));
      return delay({ id: id(), number: next('adj', 'ADJ') });
    },
    async adjustments() { ctx.need('INV.VIEW'); return delay([]); },
    async startCount() { ctx.need('INV.COUNT'); return delay({ id: id(), number: next('cnt', 'CNT') }); },
    async counts() { ctx.need('INV.VIEW'); return delay([]); },
    async count(cid) { ctx.need('INV.VIEW'); return delay({ id: cid, number: 'CNT', status: 'open', branchId: ctx.branchId, lines: [] }); },
    async setCountLines() { ctx.need('INV.COUNT'); },
    async postCount() { ctx.need('INV.COUNT'); return delay({ lines: 0, changed: 0 }); },
    async cancelCount() { ctx.need('INV.COUNT'); },
    async stockExceptions() { ctx.need('INV.VIEW'); return delay([]); },
    async resolveException() { ctx.need('INV.ADJUST'); },
    async inventoryCheck() { ctx.need('INV.VIEW'); return delay({ ok: true, balanceMismatches: [], valuationMismatches: [], unbalancedJournals: [], salesMismatches: [] }); },
    async posPolicy() { ctx.need(); return delay({ ...policy }); },
    async savePosPolicy(p) { ctx.need('POS.POLICY.MANAGE'); policy = { ...policy, ...p, denominations: p.denominations ?? policy.denominations, version: policy.version + 1 }; return delay({ ...policy }); },
    async openShift(o) {
      const { u, t } = terminal();
      if (!can(u, 'POS.SHIFT.OPEN')) throw problem(403, 'AUTH-403', 'You do not have permission POS.SHIFT.OPEN.');
      if (openShiftOf(t.id)) throw problem(409, 'POS-002', 'This terminal already has an open shift.');
      const opening = o.openingCount ? n(wrap(() => countTotal(o.openingCount!, policy.denominations))) : o.openingFloat ?? 0;
      const s = { id: id(), branchId: t.branchId, terminalId: t.id, cashierId: u.id, cashierName: u.displayNameEn, number: next('shift', 'SH'), status: 'open' as const, isEmergency: false, openedAt: now(), openingFloat: opening, closedAt: null, expectedCash: null, countedCash: null, variance: null, varianceReason: null, varianceAcceptedBy: null, version: 1 };
      shifts.push(s);
      return delay({ ...s });
    },
    async currentShift() {
      const { u, t } = terminal();
      const s = openShiftOf(t.id);
      if (!s) return delay({ shift: null });
      const f = figures(s);
      const blind = policy.blindClose && !can(u, 'POS.REPORTS.VIEW');
      return delay({ shift: { ...s }, figures: blind ? { ...f, expectedCash: 0, cashPayments: 0 } : f, blind });
    },
    async shifts() { ctx.need('POS.REPORTS.VIEW'); return delay(shifts.map((s) => ({ ...s })).reverse()); },
    async shift(sid) {
      ctx.need();
      const s = shifts.find((x) => x.id === sid);
      if (!s) throw problem(404, 'GEN-404', 'Shift not found.');
      return delay({ shift: { ...s }, report: s.status === 'closed' ? 'Z' as const : 'X' as const, figures: figures(s), cardSettlements: [], cashEvents: cashEvents.filter((e) => e.shiftId === sid).map((e) => ({ kind: e.kind, amount: n(e.amount), reason: e.reason, createdAt: e.createdAt })) });
    },
    async cashEvent(e) {
      const { u, s } = myShift();
      const needed = e.kind === 'pay_out' ? 'POS.CASH.PAY_OUT' : e.kind === 'no_sale' ? 'POS.DRAWER.NO_SALE' : 'POS.SHIFT.OPEN';
      if (!can(u, needed)) {
        if (!e.approvalId || needed === 'POS.SHIFT.OPEN') throw problem(403, 'AUTH-403', `${needed} is needed. Ask a manager to approve.`);
        useApproval(e.approvalId, needed as ApprovalAction, u.id);
      }
      if (!e.reason.trim()) throw problem(400, 'VAL-001', 'Give a reason.');
      const amount = e.kind === 'no_sale' ? 0n : toBaisa(e.amount);
      if ((e.kind === 'pay_out' || e.kind === 'drop') && amount > toBaisa(figures(s).expectedCash)) throw problem(400, 'VAL-001', 'The drawer does not hold that much cash.');
      cashEvents.push({ shiftId: s.id, kind: e.kind, amount, reason: e.reason, createdAt: now() });
      return delay({ id: id() });
    },
    async closeShift(c) {
      const { u, s } = myShift();
      const f = figures(s);
      const counted = c.closingCount ? wrap(() => countTotal(c.closingCount!, policy.denominations)) : toBaisa(c.countedCash ?? 0);
      const variance = counted - toBaisa(f.expectedCash);
      if ((variance < 0n ? -variance : variance) > toBaisa(policy.varianceThreshold)) {
        if (!c.varianceReason?.trim()) throw problem(409, 'POS-014', 'The count does not match the expected cash. Recount, or give a reason and ask a manager to accept it.');
        if (!can(u, 'POS.SHIFT.ACCEPT_VARIANCE')) {
          if (!c.approvalId) throw problem(409, 'POS-014', 'The count does not match the expected cash. A manager must accept the difference.');
          useApproval(c.approvalId, 'POS.SHIFT.ACCEPT_VARIANCE', u.id);
        }
      }
      const missing = f.cardByTerminal.filter((x) => x.total !== 0 && !(c.cardSettlements ?? []).some((y) => y.cardTerminal.toUpperCase() === x.cardTerminal.toUpperCase()));
      if (missing.length) throw problem(400, 'VAL-001', `Enter the batch total printed by bank terminal ${missing.map((m) => m.cardTerminal).join(', ')}.`);
      Object.assign(s, { status: 'closed', closedAt: now(), expectedCash: f.expectedCash, countedCash: n(counted), variance: n(variance), varianceReason: c.varianceReason ?? null });
      return delay({ id: s.id, number: s.number, figures: f, counted: n(counted), variance: n(variance) });
    },
    async quote(q) {
      const { u } = terminal();
      const lines = priceCart(u, q.lines);
      const tt = totals(lines);
      return delay({
        lines: lines.map((l) => ({ lineNo: l.lineNo, variantId: l.item.variantId, sku: l.item.sku, descriptionEn: l.item.descriptionEn, descriptionAr: l.item.descriptionAr, qty: n(l.qty), listPrice: n(l.listPrice), unitPrice: n(l.unitPrice), discount: n(l.discount), net: n(l.net), tax: n(l.tax), gross: n(l.gross), taxRate: 5, needsPriceOverride: l.needsPriceOverride, needsDiscountApproval: l.needsDiscountApproval })),
        totals: { net: n(tt.net), discount: n(tt.discount), tax: n(tt.tax), gross: n(tt.gross) }, cashDue: n(tt.gross),
      });
    },
    async completeSale(saleId, req) {
      const existing = sales.find((x) => x.detail.sale.id === saleId);
      if (existing) return delay(summary(existing.detail, existing.payments.reduce((a, p) => a + p.amount, 0n), existing.payments.reduce((a, p) => a + (p.change ?? 0n), 0n)));
      const { u, t, s } = myShift();
      const lines = priceCart(u, req.lines);
      const used = new Set<string>();
      for (const l of lines) {
        const action = l.needsPriceOverride && !can(u, 'POS.PRICE.OVERRIDE') ? 'POS.PRICE.OVERRIDE' : l.needsDiscountApproval ? 'POS.DISCOUNT.ABOVE_LIMIT' : null;
        if (!action) continue;
        const aid = req.lines[l.lineNo - 1]!.approvalId ?? req.approvalId;
        if (!aid) throw problem(403, action === 'POS.PRICE.OVERRIDE' ? 'POS-006' : 'POS-007', action === 'POS.PRICE.OVERRIDE' ? `${l.item.descriptionEn}: the price needs a manager's approval.` : `${l.item.descriptionEn}: the discount is above your limit and needs a manager's approval.`);
        if (!used.has(aid)) { useApproval(aid, action, u.id); used.add(aid); }
      }
      if (req.docType === 'tax_invoice' && !req.customerId) throw problem(400, 'POS-016', 'A tax invoice needs a customer.');
      const tt = totals(lines);
      if (req.expectedTotal != null && toBaisa(req.expectedTotal) !== tt.gross) throw problem(409, 'POS-015', 'Prices changed. Check the cart and try again.');
      const tenders = toTenders(req.payments);
      const paid = wrap(() => checkTenders(tenders, tt.gross, policy.cashRounding));
      for (const l of lines) if (l.item.productType !== 'service' && (stock.get(l.item.variantId)?.qty ?? 0n) < l.qty && policy.negativeStock === 'block') throw problem(409, 'INV-001', `Not enough stock of ${l.item.descriptionEn}.`);
      for (const l of lines) if (l.item.productType !== 'service') move(l.item.variantId, -l.qty);
      const cust = customers.find((c) => c.id === req.customerId);
      const d = write({ id: saleId, branchId: t.branchId, number: next('inv', 'INV'), docType: req.docType ?? 'simplified_invoice', creditReason: null, originalSaleId: null, originalNumber: null, saleDate: now().slice(0, 10), completedAt: now(), netTotal: n(tt.net), discountTotal: n(tt.discount), taxTotal: n(tt.tax), grossTotal: n(tt.gross), cashRounding: n(paid.rounding), isEmergency: false, note: req.note ?? null, customerId: cust?.id ?? null, customerName: cust?.nameEn ?? null, customerVat: cust?.vatNumber ?? null, cashierName: u.displayNameEn, terminalCode: t.code, shiftId: s.id },
        lines.map((l) => ({ id: id(), variantId: l.item.variantId, productType: l.item.productType as SaleLine['productType'], descriptionEn: l.item.descriptionEn, descriptionAr: l.item.descriptionAr, qty: n(l.qty), unitPrice: n(l.unitPrice), listPrice: n(l.listPrice), discount: n(l.discount), net: n(l.net), taxRate: 5, tax: n(l.tax), gross: n(l.gross), returnedQty: 0, returnable: n(l.qty) })),
        tenders, false, s.id, t);
      return delay(summary(d, paid.paid, tenders.reduce((a, p) => a + (p.tendered == null ? 0n : p.tendered - p.amount), 0n)));
    },
    async returnSale(originalId, r) {
      const { u, t, s } = myShift();
      const o = sales.find((x) => x.detail.sale.id === originalId);
      if (!o) throw problem(404, 'GEN-404', 'That sale was not found.');
      if (!r.reason.trim()) throw problem(400, 'VAL-001', 'Give the reason for the return.');
      const lines: SaleLine[] = r.lines.map((x) => {
        const l = o.detail.lines.find((y) => y.id === x.lineId)!;
        const a = wrap(() => apportionReturn({ qty: toBaisa(l.qty), net: toBaisa(l.net), tax: toBaisa(l.tax), gross: toBaisa(l.gross), discount: toBaisa(l.discount), returnedQty: toBaisa(l.returnedQty), returnedNet: 0n, returnedTax: 0n, returnedGross: 0n, returnedDiscount: 0n }, toBaisa(x.qty)));
        l.returnedQty += x.qty; l.returnable -= x.qty;
        if (x.restock && l.productType !== 'service') move(l.variantId, toBaisa(x.qty));
        return { ...l, id: id(), qty: x.qty, net: n(a.net), tax: n(a.tax), gross: n(a.gross), discount: n(a.discount), returnedQty: 0, returnable: 0 };
      });
      const gross = lines.reduce((a, l) => a + toBaisa(l.gross), 0n);
      const tenders = toTenders(r.refunds);
      const paid = wrap(() => checkTenders(tenders, gross, policy.cashRefundCap ? policy.cashRounding : 0, true));
      const cash = tenders.filter((x) => x.method === 'cash').reduce((a, x) => a + x.amount, 0n);
      if (cash > toBaisa(policy.cashRefundCap) && !can(u, 'POS.REFUND')) { if (!r.approvalId) throw problem(403, 'POS-012', 'This return needs a manager\'s approval.'); useApproval(r.approvalId, 'POS.REFUND', u.id); }
      const tax = lines.reduce((a, l) => a + toBaisa(l.tax), 0n);
      const d = write({ ...o.detail.sale, id: r.id, number: next('cn', 'CN'), docType: 'credit_note', creditReason: 'return', originalSaleId: originalId, originalNumber: o.detail.sale.number, completedAt: now(), netTotal: n(gross - tax), discountTotal: 0, taxTotal: n(tax), grossTotal: n(gross), cashRounding: n(paid.rounding), note: r.reason, cashierName: u.displayNameEn, terminalCode: t.code, shiftId: s.id }, lines, tenders, true, s.id, t);
      o.detail.creditNotes.push({ id: d.sale.id, number: d.sale.number, creditReason: 'return', grossTotal: d.sale.grossTotal });
      return delay(summary(d, paid.paid, 0n));
    },
    async voidSale(originalId, v) {
      const { u, t, s } = myShift();
      const o = sales.find((x) => x.detail.sale.id === originalId);
      if (!o) throw problem(404, 'GEN-404', 'That sale was not found.');
      if (o.shiftId !== s.id || o.detail.lines.some((l) => l.returnedQty > 0)) throw problem(409, 'POS-013', 'This sale can no longer be voided; use a return.');
      if (!can(u, 'POS.SALE.VOID')) { if (!v.approvalId) throw problem(403, 'AUTH-403', "Voiding a sale needs a manager's approval."); useApproval(v.approvalId, 'POS.SALE.VOID', u.id); }
      for (const l of o.detail.lines) { l.returnedQty = l.qty; l.returnable = 0; if (l.productType !== 'service') move(l.variantId, toBaisa(l.qty)); }
      const tenders: Tender[] = o.payments.map((p) => ({ method: p.method as Tender['method'], amount: p.amount, cardTerminal: p.cardTerminal }));
      const d = write({ ...o.detail.sale, id: v.id, number: next('cn', 'CN'), docType: 'credit_note', creditReason: 'void', originalSaleId: originalId, originalNumber: o.detail.sale.number, completedAt: now(), note: v.reason, cashierName: u.displayNameEn, terminalCode: t.code, shiftId: s.id }, o.detail.lines.map((l) => ({ ...l, id: id() })), tenders, true, s.id, t);
      o.detail.creditNotes.push({ id: d.sale.id, number: d.sale.number, creditReason: 'void', grossTotal: d.sale.grossTotal });
      return delay(summary(d, tenders.reduce((a, p) => a + p.amount, 0n), 0n));
    },
    async sales(f) {
      ctx.need();
      const rows: SaleListRow[] = sales.filter((x) => (!f.shiftId || x.shiftId === f.shiftId) && (!f.q || x.detail.sale.number.toLowerCase().includes(f.q.toLowerCase()))).map((x) => ({ id: x.detail.sale.id, number: x.detail.sale.number, docType: x.detail.sale.docType, creditReason: x.detail.sale.creditReason, completedAt: x.detail.sale.completedAt, grossTotal: x.detail.sale.grossTotal, cashRounding: x.detail.sale.cashRounding, isEmergency: false, customerName: x.detail.sale.customerName, cashierName: x.detail.sale.cashierName, terminalCode: x.detail.sale.terminalCode, originalNumber: x.detail.sale.originalNumber }));
      return delay(rows.reverse());
    },
    async sale(sid) { ctx.need(); const x = sales.find((y) => y.detail.sale.id === sid); if (!x) throw problem(404, 'GEN-404', 'That sale was not found.'); return delay(structuredClone(x.detail)); },
    async saleByNumber(number) { ctx.need(); const x = sales.find((y) => y.detail.sale.number.toUpperCase() === number.trim().toUpperCase()); if (!x) throw problem(404, 'GEN-404', `No sale ${number} in your branches.`); return delay(structuredClone(x.detail)); },
    async park(c) { const { u, t } = terminal(); parked.push({ id: c.id, label: c.label ?? null, parkedAt: now(), parkedBy: u.displayNameEn, terminalCode: t.code, cart: JSON.stringify(c.cart) }); },
    async parked() { terminal(); return delay(parked.map((p) => ({ ...p }))); },
    async recall(pid) { terminal(); const i = parked.findIndex((p) => p.id === pid); if (i < 0) throw problem(404, 'GEN-404', 'That parked cart was already recalled.'); const [p] = parked.splice(i, 1); return delay(JSON.parse(p!.cart) as unknown); },
    async receipt(sid, reprint) {
      ctx.need();
      const x = sales.find((y) => y.detail.sale.id === sid);
      if (!x) throw problem(404, 'GEN-404', 'That sale was not found.');
      const b = ctx.business(); const s = x.detail.sale;
      const [titleEn, titleAr] = s.docType === 'credit_note' ? ['Credit note', 'إشعار دائن'] : s.docType === 'tax_invoice' ? ['Tax invoice', 'فاتورة ضريبية'] : ['Simplified tax invoice', 'فاتورة ضريبية مبسطة'];
      const r: ReceiptModel = {
        saleId: s.id, number: s.number, docType: s.docType, titleEn, titleAr, originalNumber: s.originalNumber, creditReason: s.creditReason, localTime: s.completedAt, terminalCode: s.terminalCode, cashierName: s.cashierName,
        customerName: s.customerName, customerVat: s.customerVat, business: { nameEn: b.legalNameEn, nameAr: b.legalNameAr, vatNumber: b.vatNumber, crNumber: b.crNumber, addressEn: null, addressAr: null, phone: '+968 2400 0000' },
        branch: { nameEn: 'Muscat - Ruwi', nameAr: 'مسقط - روي', vatNumber: null, crNumber: null, addressEn: null, addressAr: null, phone: null },
        lines: x.detail.lines.map((l) => ({ descriptionEn: l.descriptionEn, descriptionAr: l.descriptionAr, qty: l.qty, unitPrice: l.unitPrice, discount: l.discount, net: l.net, tax: l.tax, gross: l.gross, taxRate: l.taxRate })),
        net: s.netTotal, discount: s.discountTotal, tax: s.taxTotal, gross: s.grossTotal, cashRounding: s.cashRounding, total: n(toBaisa(s.grossTotal) + toBaisa(s.cashRounding)),
        taxBreakdown: [{ rate: 5, net: s.netTotal, tax: s.taxTotal }], payments: x.detail.payments.map((p) => ({ method: p.method, amount: Math.abs(p.amount), tendered: p.tendered, change: p.changeGiven, cardTerminal: p.cardTerminal })),
        qrPayload: s.qrPayload, footerEn: policy.receiptFooterEn, footerAr: policy.receiptFooterAr, isCopy: !!reprint, isEmergency: false, paperWidthMm: 80,
      };
      return delay(r);
    },
    async invoicePdf() { ctx.need(); return delay(new Blob(['%PDF-1.7 mock'], { type: 'application/pdf' })); },
    async pinApproval(a) {
      const { u, t } = terminal();
      const approver = ctx.users.find((x) => x.username === a.approverUsername.trim().toLowerCase());
      if (!approver || ctx.pins.get(approver.username) == null) throw problem(403, 'POS-008', 'That manager cannot approve with a PIN.');
      if (approver.id === u.id) throw problem(403, 'POS-008', 'You cannot approve your own request.');
      if (ctx.pins.get(approver.username) !== a.pin) throw problem(403, 'POS-008', 'The PIN is not correct.');
      if (!can(approver, a.action)) throw problem(403, 'POS-008', `${approver.displayNameEn} does not hold ${a.action} in this branch.`);
      const row = { id: id(), branchId: t.branchId, branchCode: 'B01', actionCode: a.action, method: 'pin', status: 'approved', reason: a.reason ?? null, context: '{}', requesterName: u.displayNameEn, terminalCode: t.code, createdAt: now(), expiresAt: new Date(Date.now() + 300_000).toISOString(), requesterId: u.id, approverId: approver.id };
      approvals.push(row);
      return delay({ id: row.id, status: 'approved', approverName: approver.displayNameEn, expiresAt: row.expiresAt });
    },
    async requestApproval(a) {
      const { u, t } = terminal();
      const row = { id: id(), branchId: t.branchId, branchCode: 'B01', actionCode: a.action, method: 'remote', status: 'pending', reason: a.reason ?? null, context: '{}', requesterName: u.displayNameEn, terminalCode: t.code, createdAt: now(), expiresAt: new Date(Date.now() + 600_000).toISOString(), requesterId: u.id, approverId: null };
      approvals.push(row);
      return delay({ id: row.id, status: 'pending', expiresAt: row.expiresAt });
    },
    async approvalStatus(aid) { ctx.need(); const a = approvals.find((x) => x.id === aid); if (!a) throw problem(404, 'GEN-404', 'Approval not found.'); return delay({ id: a.id, actionCode: a.actionCode, status: a.status, reason: a.reason, expiresAt: a.expiresAt }); },
    async approvals(status) { ctx.need('APPROVALS.DECIDE'); return delay(approvals.filter((a) => !status || a.status === status).map(({ requesterId: _r, approverId: _a, ...rest }) => rest)); },
    async decideApproval(aid, decision) {
      const u = ctx.need('APPROVALS.DECIDE');
      const a = approvals.find((x) => x.id === aid)!;
      if (a.requesterId === u.id) throw problem(403, 'POS-008', 'You cannot decide your own request.');
      if (a.status !== 'pending') throw problem(409, 'POS-008', 'That request is no longer waiting.');
      a.status = decision === 'approve' ? 'approved' : 'rejected';
      a.approverId = u.id;
      a.expiresAt = new Date(Date.now() + 300_000).toISOString();
    },
    async printers() { ctx.need(); return delay(printers.map((p) => ({ ...p }))); },
    async createPrinter(p) { ctx.need('TERMINALS.APPROVE'); const x: Printer = { id: id(), branchId: p.branchId, name: p.name, host: p.host, port: p.port ?? 9100, paperWidthMm: p.paperWidthMm ?? 80, hasDrawer: p.hasDrawer ?? true, status: 'active', lastResult: null, lastUsedAt: null, version: 1 }; printers.push(x); return delay({ ...x }); },
    async updatePrinter(pid, p) { ctx.need('TERMINALS.APPROVE'); const x = printers.find((y) => y.id === pid)!; Object.assign(x, p, { version: x.version + 1 }); return delay({ ...x }); },
    async printJob(pid) { ctx.need(); const x = printers.find((y) => y.id === pid); if (!x) throw problem(404, 'GEN-404', 'Printer not found.'); x.lastResult = 'ok'; x.lastUsedAt = now(); return delay({ printed: true }); },
    async setTerminalPrinter() { ctx.need('TERMINALS.APPROVE'); },
    async bindDeviceKey(tid, key) { ctx.need('TERMINALS.APPROVE'); deviceKeys.set(tid, key); },
    async loginAt(username, password, cred) {
      const t = ctx.terminals.find((x) => x.id === cred.terminalId);
      if (!t || t.status !== 'active') throw problem(403, 'AUTH-007', 'This terminal is not approved. Ask a manager to approve it.');
      if (deviceKeys.has(t.id) && deviceKeys.get(t.id) !== cred.deviceKey) throw problem(403, 'POS-017', 'This device is not the registered terminal.');
      const u = ctx.users.find((x) => x.username === username.trim().toLowerCase());
      if (!u || ctx.passwords.get(u.username) !== password) throw problem(401, 'AUTH-001', 'Username or password is not correct.');
      ctx.setCurrent(u); currentTerminal = t;
      return delay(ctx.tokens(u));
    },
    async pinLoginAt(username, pin, cred) {
      const t = ctx.terminals.find((x) => x.id === cred.terminalId);
      if (!t || t.status !== 'active') throw problem(403, 'AUTH-007', 'This terminal is not approved. Ask a manager to approve it.');
      const u = ctx.users.find((x) => x.username === username.trim().toLowerCase());
      if (!u || ctx.pins.get(u.username) !== pin) throw problem(401, 'AUTH-001', 'Username or PIN is not correct.');
      ctx.setCurrent(u); currentTerminal = t;
      return delay(ctx.tokens(u));
    },
    async emergencySnapshot() { terminal(); return delay({ items: products.flatMap((p) => p.variants.map((v) => posItem(v.variant.id))), policy, users: [] }); },
    async uploadEmergency(records) { terminal(); return delay({ results: records.map((r) => ({ id: r.id, seq: r.seq, result: 'applied' as const, detail: null, duplicate: false })) }); },
    async emergencyRecords() { ctx.need('POS.REPORTS.VIEW'); return delay([]); },
  };
}

/** The terminal this browser is registered as (kept in localStorage, like the Windows app keeps it in its store). */
export const DEMO_TERMINAL_HINT = 'T01';
