// In-memory Phase 11 purchasing and accounting for tests, `npm run dev:mock` and design review: orders approved within each role's
// limit, goods received (non-serialized into stock, phones as device units) with landed costs spread over the lines, supplier
// invoices matched three ways with the difference split between stock on hand and price variance, returns, payments oldest first,
// the chart of accounts, journals, manual journals with a second approver, periods by branch, statements, VAT returns, card
// settlements, expenses and the ledger check. The arithmetic is @kite/pos (the same vectors as the server); the real server and
// database check everything again and are the source of truth.
import {
  ApiError, type AccountKind, type ApprovalLimit, type Branch, type CashFlowClass, type ChartAccount, type InvoiceDetail, type InvoiceLine, type InvoiceLineInput,
  type ManualDetail, type ManualStatus, type OrderDetail, type OrderStatus, type PeriodStatus, type PosItem, type PurchasingApi, type PurchPolicy, type ReceiptDetail,
  type StatementRow, type Supplier, type UnitInput, type User, type VatFigure, type VatReturnDetail,
} from '@kite/api';
import { allocateOldestFirst, clearing, fromBaisa, match, purchaseNet, purchaseTax, splitEven, splitVariance, spreadCharges, toBaisa, toNumber } from '@kite/pos';

const problem = (status: number, code: string, title: string) => new ApiError({ status, code, title });
const uid = () => crypto.randomUUID();
const now = () => new Date().toISOString();
const delay = <T>(v: T): Promise<T> => new Promise((r) => setTimeout(() => r(v), 15));
const today = () => new Intl.DateTimeFormat('sv-SE', { timeZone: 'Asia/Muscat' }).format(new Date());
const addDays = (d: string, n: number) => new Date(Date.parse(`${d}T00:00:00Z`) + n * 864e5).toISOString().slice(0, 10);
const n = (b: bigint) => toNumber(b);
const b = (x: number | null | undefined) => toBaisa(x ?? 0);

interface SupplierLine { id: string; branchId: string; supplierId: string; kind: string; amount: number; docDate: string; dueDate: string | null; number: string; note: string | null }

export interface PurchMockHooks {
  need: (perm?: string) => User;
  can: (perm: string) => boolean;
  next: (kind: string, prefix: string) => string;
  branches: () => Branch[];
  branchId: string;
  suppliers: () => Supplier[];
  supplierLines: SupplierLine[];
  item: (variantId: string) => PosItem;
  stockIn: (variantId: string, qty: bigint, unitCost: bigint) => bigint;
  stockOf: (variantId: string) => bigint;
  receiveUnit: (variantId: string, input: UnitInput, cost: number) => { id: string; status: string };
  roles: () => { id: string; code: string; nameEn: string; nameAr: string }[];
  taxCodeId: string;
}

interface JLine { code: string; branchId: string; debit: bigint; credit: bigint; memo?: string | null; party?: string | null }
interface Journal { id: string; number: string; date: string; kind: string; description: string; branchId: string; lines: JLine[]; reversalOf: string | null; sourceType: string | null; sourceId: string | null; postedAt: string; by: string }

const CHART: [string, string, string, AccountKind, string | null, boolean, boolean, CashFlowClass][] = [
  ['1000', 'Assets', 'الأصول', 'asset', null, false, false, 'none'], ['1100', 'Cash on hand', 'النقدية في الصندوق', 'asset', '1000', true, false, 'cash'],
  ['1150', 'Card clearing', 'تسوية مدفوعات البطاقات', 'asset', '1000', true, false, 'cash'], ['1200', 'Bank', 'البنك', 'asset', '1000', true, false, 'cash'],
  ['1300', 'Accounts receivable', 'الذمم المدينة', 'asset', '1000', true, true, 'operating'], ['1310', 'Interbranch due from', 'مستحق من الفروع', 'asset', '1000', true, true, 'operating'],
  ['1400', 'Inventory: new devices', 'المخزون: أجهزة جديدة', 'asset', '1000', true, true, 'operating'], ['1420', 'Inventory: accessories', 'المخزون: الإكسسوارات', 'asset', '1000', true, true, 'operating'],
  ['1500', 'VAT input', 'ضريبة المدخلات', 'asset', '1000', true, true, 'operating'], ['1600', 'Fixed assets', 'الأصول الثابتة', 'asset', '1000', true, false, 'investing'],
  ['2000', 'Liabilities', 'الالتزامات', 'liability', null, false, false, 'none'], ['2100', 'Supplier payables', 'الذمم الدائنة للموردين', 'liability', '2000', true, true, 'operating'],
  ['2110', 'Goods received not invoiced', 'بضاعة مستلمة غير مفوترة', 'liability', '2000', true, true, 'operating'], ['2200', 'VAT output', 'ضريبة المخرجات', 'liability', '2000', true, true, 'operating'],
  ['2210', 'VAT payable', 'ضريبة القيمة المضافة المستحقة', 'liability', '2000', true, true, 'operating'], ['2500', 'Interbranch due to', 'مستحق للفروع', 'liability', '2000', true, true, 'operating'],
  ['3000', 'Equity', 'حقوق الملكية', 'equity', null, false, false, 'none'], ['3100', 'Capital', 'رأس المال', 'equity', '3000', true, false, 'financing'],
  ['3300', 'Retained earnings', 'الأرباح المحتجزة', 'equity', '3000', true, false, 'financing'],
  ['4000', 'Revenue', 'الإيرادات', 'revenue', null, false, false, 'none'], ['4100', 'Sales: new devices', 'المبيعات: أجهزة جديدة', 'revenue', '4000', true, false, 'none'],
  ['4120', 'Sales: accessories', 'المبيعات: الإكسسوارات', 'revenue', '4000', true, false, 'none'],
  ['5000', 'Cost of sales', 'تكلفة المبيعات', 'expense', null, false, false, 'none'], ['5100', 'Cost: new devices', 'التكلفة: أجهزة جديدة', 'expense', '5000', true, false, 'none'],
  ['5300', 'Purchase price variance', 'فروقات أسعار الشراء', 'expense', '5000', true, false, 'none'],
  ['6000', 'Expenses', 'المصروفات', 'expense', null, false, false, 'none'], ['6100', 'Rent', 'الإيجار', 'expense', '6000', true, false, 'none'],
  ['6800', 'Card fees', 'رسوم البطاقات', 'expense', '6000', true, false, 'none'], ['6900', 'General expenses', 'مصروفات عامة', 'expense', '6000', true, false, 'none'],
];
const INVENTORY: Record<string, string> = { device: '1400', accessory: '1420', consumable: '1420', spare_part: '1420' };
const PAY: Record<string, string> = { cash: '1100', bank_transfer: '1200', cheque: '1200' };
const VAT_BOXES: [string, number, string, string, 'output' | 'input' | 'manual' | 'formula', string | null][] = [
  ['1a', 10, 'Supplies of goods and services taxed at 5%', 'توريدات السلع والخدمات الخاضعة لنسبة 5%', 'output', null],
  ['2a', 70, 'Purchases from the GCC subject to reverse charge', 'المشتريات من دول المجلس الخاضعة للاحتساب العكسي', 'manual', null],
  ['5a', 120, 'Total VAT due (1a + 1f + 2a + 2b + 4a)', 'إجمالي الضريبة المستحقة (1أ + 1و + 2أ + 2ب + 4أ)', 'formula', '1a+2a'],
  ['5b', 130, 'Adjustment of VAT due', 'تعديل الضريبة المستحقة', 'manual', null],
  ['6a', 140, 'Purchases (except import of goods)', 'المشتريات (عدا استيراد السلع)', 'input', null],
  ['6c', 160, 'VAT on acquisition of fixed assets', 'الضريبة على اقتناء الأصول الثابتة', 'input', null],
  ['6d', 170, 'Adjustment of input VAT credit', 'تعديل رصيد ضريبة المدخلات', 'manual', null],
  ['6e', 180, 'Total input VAT credit (6a + 6b + 6c + 6d)', 'إجمالي رصيد ضريبة المدخلات (6أ + 6ب + 6ج + 6د)', 'formula', '6a+6c+6d'],
  ['7', 190, 'Net VAT payable or refundable (5a + 5b - 6e)', 'صافي الضريبة المستحقة أو المستردة (5أ + 5ب - 6هـ)', 'formula', '5a+5b-6e'],
];

export function createMockPurchasing(h: PurchMockHooks) {
  const branchCode = (id: string) => h.branches().find((x) => x.id === id)?.code ?? 'B01';
  const supplier = (id: string) => h.suppliers().find((s) => s.id === id) ?? (() => { throw problem(404, 'GEN-404', 'That supplier was not found.'); })();
  const me = () => h.need();
  let policy: PurchPolicy = { priceTolerancePct: 2, priceToleranceAmount: 1, overReceiptPct: 0, supplierList: 'all', version: 1 };
  let acctPolicy = { manualJournalApprovalAbove: 1000, vatPeriodMonths: 3 as 1 | 3, vatFirstMonth: 1, version: 1 };
  const limits: ApprovalLimit[] = [];
  const branchLists = new Map<string, Set<string>>();
  const accounts: ChartAccount[] = [];
  const journals: Journal[] = [];
  type OrderLine = OrderDetail['lines'][number];
  const orders: (OrderDetail['order'] & { lines: OrderLine[] })[] = [];
  type RLine = ReceiptDetail['lines'][number] & { receiptId: string; branchId: string; supplierId: string; orderPrice: number | null; taxCodeId: string; tracking: string; returnedOpen: number };
  const receipts: (ReceiptDetail['receipt'] & { warehouse: string })[] = [];
  const rlines: RLine[] = [];
  const charges: (ReceiptDetail['charges'][number] & { receiptId: string; branchId: string })[] = [];
  const grni = new Map<string, bigint>();
  const invoices: (InvoiceDetail['invoice'] & { lines: InvoiceLine[]; entryId: string | null })[] = [];
  const allocations: { debit: string; credit: string; amount: number }[] = [];
  const returns: { id: string; number: string; returnDate: string; reason: string; netTotal: number; taxTotal: number; stockValue: number; branchId: string; supplierId: string; journalNumber: string | null; createdAt: string; lines: { lineNo: number; qty: number; stockValue: number; invoiced: boolean; netAmount: number; taxAmount: number; description: string; receiptNumber: string; imei: string | null }[] }[] = [];
  const payments: { id: string; number: string; paymentDate: string; method: 'cash' | 'bank_transfer' | 'cheque'; reference: string | null; amount: number; note: string | null; branchId: string; supplierId: string; journalNumber: string | null; createdAt: string; entries: string[] }[] = [];
  const manuals: (ManualDetail['journal'] & { lines: ManualDetail['lines'] })[] = [];
  const year = Number(today().slice(0, 4));
  const periods = Array.from({ length: 12 }, (_, k) => ({
    id: uid(), periodNo: k + 1, startsOn: `${year}-${String(k + 1).padStart(2, '0')}-01`,
    endsOn: new Date(Date.UTC(year, k + 1, 0)).toISOString().slice(0, 10), status: new Map<string, PeriodStatus>(),
  }));
  let yearClosed = false;
  const vatReturns: VatReturnDetail['vatReturn'][] = [];
  let boxesReviewed = false;
  const cardSettlements: { id: string; number: string; settlementDate: string; branchId: string; grossAmount: number; fees: number; netAmount: number; reference: string }[] = [];
  const expenses: { id: string; number: string; expenseDate: string; branchId: string; accountId: string; description: string; payee: string | null; netAmount: number; taxAmount: number; grossAmount: number; paidBy: 'cash' | 'bank_transfer' | 'cheque'; journalNumber: string | null }[] = [];

  const account = (code: string) => accounts.find((a) => a.code === code)!;
  const periodOf = (date: string) => periods.find((p) => date >= p.startsOn && date <= p.endsOn);
  const statusAt = (p: (typeof periods)[number], branchId: string): PeriodStatus => p.status.get(branchId) ?? 'open';
  /** The journal date: its own when open at every branch the journal touches, else the first open period after (17.5). */
  const postingDate = (date: string, branchIds: string[], soft = false) => {
    let d = date;
    for (let guard = 0; guard < 24; guard++) {
      const p = periodOf(d);
      if (!p) return d;
      if (branchIds.every((x) => statusAt(p, x) === 'open' || (soft && statusAt(p, x) === 'soft_closed'))) return d;
      d = addDays(p.endsOn, 1);
    }
    throw problem(409, 'ACC-001', 'Every period from this date on is closed.');
  };
  const post = (kind: string, branchId: string, date: string, description: string, lines: JLine[], source: [string, string] | null = null, reversalOf: string | null = null, soft = false) => {
    const ls = lines.filter((l) => l.debit !== 0n || l.credit !== 0n).map((l) => (l.debit < 0n || l.credit < 0n ? { ...l, debit: l.credit < 0n ? -l.credit : l.debit < 0n ? 0n : l.debit, credit: l.debit < 0n ? -l.debit : l.credit < 0n ? 0n : l.credit } : l));
    const dr = ls.reduce((s, l) => s + l.debit, 0n);
    const cr = ls.reduce((s, l) => s + l.credit, 0n);
    if (dr !== cr) throw problem(400, 'ACC-002', `The journal does not balance: debit ${fromBaisa(dr)}, credit ${fromBaisa(cr)}.`);
    if (ls.length === 0) return null;
    const j: Journal = { id: uid(), number: h.next('jv', 'JV'), date: postingDate(date, [...new Set([branchId, ...ls.map((l) => l.branchId)])], soft),
      kind, description, branchId, lines: ls, reversalOf, sourceType: source?.[0] ?? null, sourceId: source?.[1] ?? null, postedAt: now(), by: me().displayNameEn };
    journals.push(j);
    return j;
  };
  const dr = (code: string, branchId: string, amount: bigint, party?: string): JLine => ({ code, branchId, debit: amount, credit: 0n, party });
  const cr = (code: string, branchId: string, amount: bigint, party?: string): JLine => ({ code, branchId, debit: 0n, credit: amount, party });
  const balanceOf = (code: string, branchId?: string, to?: string, from?: string) => journals.filter((j) => (!to || j.date <= to) && (!from || j.date >= from))
    .flatMap((j) => j.lines).filter((l) => l.code === code && (!branchId || l.branchId === branchId)).reduce((s, l) => s + l.debit - l.credit, 0n);
  const entryOpen = (e: SupplierLine) => r3(e.amount - allocations.filter((a) => a.debit === e.id).reduce((s, a) => s + a.amount, 0));
  const creditLeft = (e: SupplierLine) => r3(-e.amount - allocations.filter((a) => a.credit === e.id).reduce((s, a) => s + a.amount, 0));
  const r3 = (x: number) => Math.round(x * 1000) / 1000;

  let seeded = false;
  const seed = () => {
    if (seeded) return;
    seeded = true;
    for (const [code, nameEn, nameAr, type, parent, isPostable, isControl, cff] of CHART)
      accounts.push({ id: uid(), code, nameEn, nameAr, type, parentId: null, parentCode: parent, isPostable, isControl, status: 'active', cashFlowClass: cff, version: 1, balance: 0, used: false });
    for (const a of accounts) a.parentId = accounts.find((x) => x.code === a.parentCode)?.id ?? null;
    const roles = h.roles();
    for (const [code, max] of [['owner', null], ['accountant', 5000], ['branch_manager', 500]] as const) {
      const r = roles.find((x) => x.code === code);
      if (r) limits.push({ id: uid(), roleId: r.id, roleCode: code, roleNameEn: r.nameEn, roleNameAr: r.nameAr, branchId: null, branchCode: null, maxAmount: max });
    }
    const ho = h.branchId;
    const d = `${year}-01-05`;
    journals.push({ id: uid(), number: h.next('jv', 'JV'), date: d, kind: 'acct.manual', description: 'Owner’s capital', branchId: ho, lines: [dr('1200', ho, 8000000n), cr('3100', ho, 8000000n)], reversalOf: null, sourceType: null, sourceId: null, postedAt: now(), by: 'Salim Al Harthy' });
    journals.push({ id: uid(), number: h.next('jv', 'JV'), date: today(), kind: 'pos.sale', description: 'Sales of the day', branchId: ho, lines: [dr('1100', ho, 840000n), dr('1150', ho, 210000n), cr('4100', ho, 1000000n), cr('2200', ho, 50000n)], reversalOf: null, sourceType: 'sale', sourceId: null, postedAt: now(), by: 'Salim Al Harthy' });
  };

  const orderDetail = (id: string): OrderDetail => {
    const o = orders.find((x) => x.id === id) ?? (() => { throw problem(404, 'GEN-404', 'That order was not found.'); })();
    const u = me();
    const roleIds = u.roles.map((r) => r.roleId);
    const mine = limits.filter((l) => roleIds.includes(l.roleId));
    const unlimited = mine.some((l) => l.maxAmount == null);
    const max = mine.reduce((m, l) => Math.max(m, l.maxAmount ?? 0), 0);
    const { lines, ...order } = o;
    return structuredClone({
      order, lines: lines.map((l) => ({ ...l, received: r3(rlines.filter((r) => r.poLineId === l.id).reduce((s, r) => s + r.qty, 0)), invoiced: r3(rlines.filter((r) => r.poLineId === l.id).reduce((s, r) => s + r.invoiced, 0)) })),
      receipts: receipts.filter((r) => r.poId === id).map((r) => ({ id: r.id, number: r.number, receiptDate: r.receiptDate, branchCode: r.branchCode, goodsValue: r.goodsValue, chargesValue: r.chargesValue })),
      approval: { canApprove: h.can('PURCH.APPROVE') && mine.length > 0 && (unlimited || max >= o.grossTotal), limit: mine.length === 0 ? 0 : unlimited ? null : max },
    });
  };
  const refreshOrder = (oid: string) => {
    const o = orders.find((x) => x.id === oid)!;
    const got = (l: OrderLine) => rlines.filter((r) => r.poLineId === l.id).reduce((s, r) => s + r.qty, 0);
    if (o.lines.every((l) => got(l) >= l.qty)) o.status = 'received';
    else if (o.lines.some((l) => got(l) > 0)) o.status = 'partly_received';
  };
  const receiptDetail = (id: string): ReceiptDetail => {
    const r = receipts.find((x) => x.id === id) ?? (() => { throw problem(404, 'GEN-404', 'That receipt was not found.'); })();
    const { warehouse: _, ...receipt } = r;
    return structuredClone({
      receipt,
      lines: rlines.filter((l) => l.receiptId === id).map(({ receiptId: _r, branchId: _b, supplierId: _s, orderPrice: _o, taxCodeId: _t, tracking: _k, ...l }) => ({ ...l, openValue: n(grni.get(l.id) ?? 0n) })),
      charges: charges.filter((c) => c.receiptId === id).map(({ receiptId: _r, branchId: _b, ...c }) => ({ ...c, openValue: n(grni.get(c.id) ?? 0n) })),
    });
  };
  const invoiceDetail = (id: string): InvoiceDetail => {
    const i = invoices.find((x) => x.id === id) ?? (() => { throw problem(404, 'GEN-404', 'That invoice was not found.'); })();
    const { lines, entryId, ...invoice } = i;
    const e = h.supplierLines.find((l) => l.id === entryId);
    return structuredClone({ invoice: { ...invoice, paid: e ? r3(e.amount - entryOpen(e)) : 0 }, lines });
  };
  /** Each invoice line matched against what is open now (16.2). */
  const matchLines = (supplierId: string, lines: InvoiceLineInput[]): InvoiceLine[] => lines.map((l, k) => {
    const net = purchaseNet(b(l.qty), b(l.unitPrice));
    const tax = purchaseTax(net, 5);
    const base = { id: uid(), lineNo: k + 1, kind: l.kind, qty: l.qty, unitPrice: l.unitPrice, netAmount: n(net), taxCodeId: l.taxCodeId ?? h.taxCodeId, taxRate: 5, taxAmount: n(tax),
      receiptLineId: l.receiptLineId ?? null, receiptChargeId: l.receiptChargeId ?? null, outsideType: l.outsideType ?? null, outsideId: l.outsideId ?? null, accountId: l.accountId ?? null,
      accountCode: l.accountId ? accounts.find((a) => a.id === l.accountId)?.code ?? null : null, expenseBranchId: l.expenseBranchId ?? null, receiptNumber: null as string | null, receiptPrice: null as number | null, branchCode: null as string | null };
    if (l.kind === 'receipt') {
      const r = rlines.find((x) => x.id === l.receiptLineId);
      if (!r || r.supplierId !== supplierId) throw problem(400, 'VAL-001', 'Those goods came from another supplier.');
      const openQty = r3(r.qty - r.invoiced - r.returnedOpen);
      const m = match(b(l.qty), b(l.unitPrice), b(openQty), b(r.unitPrice), r.orderPrice == null ? null : b(r.orderPrice), policy.priceTolerancePct, b(policy.priceToleranceAmount));
      if (m.status === 'qty') throw problem(409, 'PUR-002', `${r.description}: only ${openQty} of these are received and not invoiced.`);
      const expected = clearing(b(l.qty), b(openQty), grni.get(r.id) ?? 0n);
      const onHand = r.tracking === 'none' ? h.stockOf(r.variantId) : b(r.units.filter((u) => u.status === 'in_stock').length);
      const v = splitVariance(net - expected, b(l.qty), onHand);
      return { ...base, description: r.description, expectedAmount: n(expected), orderPrice: r.orderPrice, matchStatus: m.status, variance: n(net - expected), varianceInventory: n(v.inventory),
        variancePpv: n(v.ppv), receiptNumber: receipts.find((x) => x.id === r.receiptId)?.number ?? null, receiptPrice: r.unitPrice, branchCode: branchCode(r.branchId) };
    }
    if (l.kind === 'charge' || l.kind === 'outside_work') {
      const key = (l.receiptChargeId ?? l.outsideId)!;
      const open = grni.get(key) ?? 0n;
      const m = match(1000n, b(l.unitPrice), open > 0n ? 1000n : 0n, open, null, policy.priceTolerancePct, b(policy.priceToleranceAmount));
      if (m.status === 'qty') throw problem(409, 'PUR-002', 'That cost is already invoiced.');
      const c = charges.find((x) => x.id === key);
      return { ...base, description: l.description ?? (c ? c.description : 'Outside work'), expectedAmount: n(open), orderPrice: null, matchStatus: m.status, variance: n(net - open), varianceInventory: 0, variancePpv: n(net - open), branchCode: c ? branchCode(c.branchId) : null };
    }
    return { ...base, description: l.description ?? accounts.find((a) => a.id === l.accountId)?.nameEn ?? 'Expense', expectedAmount: 0, orderPrice: null, matchStatus: 'none' as const, variance: 0, varianceInventory: 0, variancePpv: 0, branchCode: branchCode(l.expenseBranchId ?? h.branchId) };
  });
  const allocateCredits = (supplierId: string, branchId: string) => {
    const credits = h.supplierLines.filter((l) => l.supplierId === supplierId && l.branchId === branchId && l.amount < 0 && creditLeft(l) > 0);
    for (const c of credits) {
      const items = h.supplierLines.filter((l) => l.supplierId === supplierId && l.branchId === branchId && l.amount > 0 && entryOpen(l) > 0)
        .map((l) => ({ id: l.id, due: l.dueDate ?? l.docDate, open: b(entryOpen(l)) }));
      for (const a of allocateOldestFirst(items, b(creditLeft(c))).allocations) allocations.push({ debit: a.id, credit: c.id, amount: n(a.amount) });
    }
  };
  const ledgerChecks = () => {
    const rows: { ledger: 'suppliers' | 'grni' | 'trial_balance' | 'interbranch'; branchId: string | null; branchCode: string | null; subledger: number; control: number; difference: number; asOf: string; checkedAt: string }[] = [];
    for (const br of h.branches()) {
      const sub = b(h.supplierLines.filter((l) => l.branchId === br.id).reduce((s, l) => s + l.amount, 0));
      const ctl = -balanceOf('2100', br.id);
      rows.push({ ledger: 'suppliers', branchId: br.id, branchCode: br.code, subledger: n(sub), control: n(ctl), difference: n(sub - ctl), asOf: today(), checkedAt: now() });
      const g = [...grni.entries()].filter(([k]) => rlines.some((r) => r.id === k && r.branchId === br.id) || charges.some((c) => c.id === k && c.branchId === br.id)).reduce((s, [, v]) => s + v, 0n);
      const gc = -balanceOf('2110', br.id);
      rows.push({ ledger: 'grni', branchId: br.id, branchCode: br.code, subledger: n(g), control: n(gc), difference: n(g - gc), asOf: today(), checkedAt: now() });
      const lines = journals.flatMap((j) => j.lines).filter((l) => l.branchId === br.id);
      const d = lines.reduce((s, l) => s + l.debit, 0n);
      const c = lines.reduce((s, l) => s + l.credit, 0n);
      rows.push({ ledger: 'trial_balance', branchId: br.id, branchCode: br.code, subledger: n(d), control: n(c), difference: n(d - c), asOf: today(), checkedAt: now() });
    }
    const from = balanceOf('1310');
    const to = -balanceOf('2500');
    rows.push({ ledger: 'interbranch', branchId: null, branchCode: null, subledger: n(from), control: n(to), difference: n(from - to), asOf: today(), checkedAt: now() });
    return rows;
  };
  const balances = (to: string, from?: string, branchId?: string, includeYearEnd = true) => {
    const m = new Map<string, bigint>();
    for (const j of journals) {
      if (j.date > to || (from && j.date < from) || (!includeYearEnd && j.kind === 'acct.year_end')) continue;
      for (const l of j.lines) if (!branchId || l.branchId === branchId) m.set(l.code, (m.get(l.code) ?? 0n) + l.debit - l.credit);
    }
    return [...m.entries()].filter(([, v]) => v !== 0n).sort(([a], [c]) => a.localeCompare(c)).map(([code, v]) => ({ code, v, a: account(code) }));
  };
  const rowsOf = (list: ReturnType<typeof balances>, sign: 1n | -1n): StatementRow[] => list.map((x) => ({ code: x.code, nameEn: x.a.nameEn, nameAr: x.a.nameAr, amount: n(sign * x.v) }));
  const asOf = () => h.branches().map((x) => ({ branchId: x.id, code: x.code, nameEn: x.nameEn, nameAr: x.nameAr, asOf: now(), onThisServer: true }));
  const vatCompute = (from: string, to: string, adj: Record<string, VatFigure>) => {
    const inRange = journals.filter((j) => j.date >= from && j.date <= to && j.kind !== 'vat.settlement');
    const out = inRange.flatMap((j) => j.lines).filter((l) => l.code === '2200').reduce((s, l) => s + l.credit - l.debit, 0n);
    const fixed = inRange.filter((j) => j.lines.some((l) => l.code === '1600')).flatMap((j) => j.lines).filter((l) => l.code === '1500').reduce((s, l) => s + l.debit - l.credit, 0n);
    const inp = inRange.flatMap((j) => j.lines).filter((l) => l.code === '1500').reduce((s, l) => s + l.debit - l.credit, 0n) - fixed;
    const boxes: Record<string, VatFigure> = {};
    const fig = (v: bigint): VatFigure => ({ value: n(v * 20n), vat: n(v) });
    boxes['1a'] = fig(out); boxes['6a'] = fig(inp); boxes['6c'] = fig(fixed);
    for (const k of ['2a', '5b', '6d']) boxes[k] = adj[k] ?? { value: 0, vat: 0 };
    const sumOf = (codes: [number, string][]) => codes.reduce((s, [sign, c]) => ({ value: r3(s.value + sign * (boxes[c]?.value ?? 0)), vat: r3(s.vat + sign * (boxes[c]?.vat ?? 0)) }), { value: 0, vat: 0 });
    boxes['5a'] = sumOf([[1, '1a'], [1, '2a']]); boxes['6e'] = sumOf([[1, '6a'], [1, '6c'], [1, '6d']]); boxes['7'] = sumOf([[1, '5a'], [1, '5b'], [-1, '6e']]);
    return { boxes, out, inp: inp + fixed };
  };
  const vatDetail = (id: string): VatReturnDetail => {
    const v = vatReturns.find((x) => x.id === id) ?? (() => { throw problem(404, 'GEN-404', 'That return was not found.'); })();
    return structuredClone({
      vatReturn: v,
      boxes: VAT_BOXES.map(([code, sort, nameEn, nameAr, source, formula]) => ({ code, sort, nameEn, nameAr, source, formula, needsReview: !boxesReviewed })),
      check: h.branches().map((x) => ({ branchCode: x.code, output: 0, outputAccount: 0, input: 0, inputAccount: 0 })), agrees: true, bySource: [],
    });
  };

  const api: PurchasingApi = {
    // ------------------------------------------------------------ settings
    async purchSettings() { h.need('PURCH.VIEW'); seed(); return delay(structuredClone({ policy, limits, roles: h.roles() })); },
    async updatePurchPolicy(p) { h.need('PURCH.SETTINGS'); if (p.version !== policy.version) throw problem(412, 'GEN-412', 'Someone changed the purchasing settings; open them again.'); policy = { ...p, version: p.version + 1 }; },
    async updateApprovalLimits(list) {
      h.need('PURCH.SETTINGS');
      limits.splice(0, limits.length, ...list.map((l) => { const r = h.roles().find((x) => x.id === l.roleId); return { id: uid(), roleId: l.roleId, roleCode: r?.code, roleNameEn: r?.nameEn, roleNameAr: r?.nameAr, branchId: l.branchId, branchCode: null, maxAmount: l.maxAmount }; }));
    },
    async branchSuppliers(branchId) {
      h.need('PURCH.VIEW');
      const set = branchLists.get(branchId) ?? new Set();
      return delay(h.suppliers().filter((s) => s.status === 'active' || s.status === 'provisional').map((s) => ({ id: s.id, number: s.number, nameEn: s.nameEn, nameAr: s.nameAr, status: s.status, approved: set.has(s.id) })));
    },
    async setBranchSuppliers(branchId, ids) { h.need('PURCH.SETTINGS'); branchLists.set(branchId, new Set(ids)); },
    // ------------------------------------------------------------ orders
    async orders(f) {
      h.need('PURCH.VIEW'); seed();
      const k = f?.q?.trim().toLowerCase();
      return delay(orders.filter((o) => (!f?.status || o.status === f.status || (f.status === 'open' && ['approved', 'sent', 'partly_received'].includes(o.status)))
        && (!f?.supplierId || o.supplierId === f.supplierId) && (!k || o.number.toLowerCase().includes(k) || o.supplierNameEn.toLowerCase().includes(k)))
        .map((o) => ({ id: o.id, number: o.number, status: o.status, orderDate: o.orderDate, expectedDate: o.expectedDate, grossTotal: o.grossTotal, branchCode: o.branchCode, supplierId: o.supplierId,
          supplierNameEn: o.supplierNameEn, supplierNameAr: o.supplierNameAr, lines: o.lines.length, deliverTo: [...new Set(o.lines.map((l) => l.deliverBranchCode))].join(', ') || null })).reverse());
    },
    async order(id) { h.need('PURCH.VIEW'); seed(); return delay(orderDetail(id)); },
    async createOrder(o) {
      const u = h.need('PURCH.ORDER'); seed();
      if (!o.lines.length) throw problem(400, 'VAL-001', 'Add at least one line.');
      const s = supplier(o.supplierId);
      if (s.status === 'merged' || s.status === 'inactive') throw problem(409, 'SUP-001', `${s.nameEn} is ${s.status}.`);
      const lines: OrderLine[] = o.lines.map((l, k) => {
        const i = h.item(l.variantId);
        const net = purchaseNet(b(l.qty), b(l.unitPrice));
        const tax = purchaseTax(net, i.taxRate ?? 5);
        const deliver = l.deliverBranchId ?? o.branchId;
        return { id: uid(), lineNo: k + 1, variantId: l.variantId, sku: i.sku, description: i.descriptionEn, tracking: i.tracking, productType: i.productType, deliverBranchId: deliver,
          deliverBranchCode: branchCode(deliver), qty: l.qty, unitPrice: l.unitPrice, taxCodeId: i.taxCodeId, taxRate: i.taxRate ?? 5, netAmount: n(net), taxAmount: n(tax), repairJobId: null,
          repairJobNumber: null, received: 0, invoiced: 0 };
      });
      const net = lines.reduce((s, l) => s + b(l.netAmount), 0n);
      const tax = lines.reduce((s, l) => s + b(l.taxAmount), 0n);
      const br = h.branches().find((x) => x.id === o.branchId)!;
      const existing = o.id ? orders.find((x) => x.id === o.id) : undefined;
      if (existing) {
        if (existing.status !== 'draft') throw problem(409, 'PUR-001', `Order ${existing.number} is ${existing.status}; only a draft is changed.`);
        Object.assign(existing, { supplierId: s.id, supplierNumber: s.number, supplierNameEn: s.nameEn, supplierNameAr: s.nameAr, lines, netTotal: n(net), taxTotal: n(tax), grossTotal: n(net + tax), expectedDate: o.expectedDate ?? null, note: o.note ?? null, version: existing.version + 1 });
        return delay({ id: existing.id, number: existing.number });
      }
      const oid = o.id ?? uid();
      const number = h.next('po', 'PO');
      orders.push({ id: oid, number, status: 'draft', orderDate: o.orderDate ?? today(), expectedDate: o.expectedDate ?? null, netTotal: n(net), taxTotal: n(tax), grossTotal: n(net + tax),
        note: o.note ?? null, version: 1, branchId: br.id, branchCode: br.code, branchNameEn: br.nameEn, branchNameAr: br.nameAr, supplierId: s.id, supplierNumber: s.number, supplierNameEn: s.nameEn,
        supplierNameAr: s.nameAr, supplierStatus: s.status, createdBy: u.displayNameEn, approvedBy: null, approvedAt: null, sentAt: null, closedAt: null, closeReason: null, createdAt: now(), lines });
      return delay({ id: oid, number });
    },
    async updateOrder(id, o) { return api.createOrder({ ...o, id }); },
    async approveOrder(id) {
      const u = h.need('PURCH.APPROVE');
      const o = orders.find((x) => x.id === id)!;
      if (o.status !== 'draft') throw problem(409, 'PUR-001', `Order ${o.number} is already ${o.status}.`);
      const d = orderDetail(id);
      if (!d.approval.canApprove) throw problem(403, 'PUR-006', `Order ${o.number} is OMR ${fromBaisa(b(o.grossTotal))}; you may approve up to OMR ${fromBaisa(b(d.approval.limit ?? 0))}. Someone with a higher limit approves it.`);
      Object.assign(o, { status: 'approved' as OrderStatus, approvedBy: u.displayNameEn, approvedAt: now() });
      return delay(orderDetail(id));
    },
    async sendOrder(id) { h.need('PURCH.ORDER'); const o = orders.find((x) => x.id === id)!; if (o.status !== 'approved') throw problem(409, 'PUR-001', 'Only an approved order is sent.'); Object.assign(o, { status: 'sent', sentAt: now() }); return delay(orderDetail(id)); },
    async cancelOrder(id, reason) {
      h.need('PURCH.ORDER'); const o = orders.find((x) => x.id === id)!;
      if (!['draft', 'approved', 'sent'].includes(o.status)) throw problem(409, 'PUR-001', 'Goods have come in against it, so it is closed instead.');
      Object.assign(o, { status: 'cancelled', closedAt: now(), closeReason: reason }); return delay(orderDetail(id));
    },
    async closeOrder(id, reason) { h.need('PURCH.ORDER'); const o = orders.find((x) => x.id === id)!; Object.assign(o, { status: 'closed', closedAt: now(), closeReason: reason }); return delay(orderDetail(id)); },
    async orderPdf() { h.need('PURCH.VIEW'); return delay(new Blob(['%PDF-1.7 mock'], { type: 'application/pdf' })); },
    async priceHistory(variantId) {
      h.need('PURCH.VIEW');
      const ls = rlines.filter((l) => l.variantId === variantId);
      const last = ls.at(-1);
      const r = last ? receipts.find((x) => x.id === last.receiptId)! : null;
      return delay({ last: last && r ? { unitPrice: last.unitPrice, date: r.receiptDate, supplierNameEn: r.supplierNameEn, supplierNameAr: r.supplierNameAr, branchCode: r.branchCode, number: r.number } : null,
        average: ls.length ? r3(ls.reduce((s, l) => s + l.goodsValue, 0) / ls.reduce((s, l) => s + l.qty, 0)) : null, bySupplier: [] });
    },
    // ------------------------------------------------------------ receipts
    async receipts(f) {
      h.need('PURCH.VIEW'); seed();
      return delay(receipts.filter((r) => (!f?.supplierId || r.supplierId === f.supplierId) && (!f?.branchId || r.branchId === f.branchId)).map((r) => ({
        id: r.id, number: r.number, receiptDate: r.receiptDate, branchCode: r.branchCode, supplierNameEn: r.supplierNameEn, supplierNameAr: r.supplierNameAr, poNumber: r.poNumber,
        goodsValue: r.goodsValue, chargesValue: r.chargesValue, deliveryNote: r.deliveryNote, openValue: n(rlines.filter((l) => l.receiptId === r.id).reduce((s, l) => s + (grni.get(l.id) ?? 0n), 0n)),
      })).reverse());
    },
    async goodsReceipt(id) { h.need('PURCH.VIEW'); return delay(receiptDetail(id)); },
    async receive(r) {
      const u = h.need('PURCH.RECEIVE'); seed();
      if (receipts.some((x) => x.id === r.id)) return delay(receiptDetail(r.id));
      const s = supplier(r.supplierId);
      const o = r.poId ? orders.find((x) => x.id === r.poId) : undefined;
      if (o && !['approved', 'sent', 'partly_received'].includes(o.status)) throw problem(409, 'PUR-001', `Order ${o.number} is ${o.status}; goods are received against an approved order.`);
      const plan = r.lines.map((l) => {
        const pl = o?.lines.find((x) => x.id === l.poLineId);
        if (pl) {
          const open = r3(pl.qty * (1 + policy.overReceiptPct / 100) - rlines.filter((x) => x.poLineId === pl.id).reduce((a, x) => a + x.qty, 0));
          if (l.qty > open) throw problem(409, 'PUR-002', `Only ${open} of that line are still to come.`);
        }
        const i = h.item(pl?.variantId ?? l.variantId!);
        if (i.tracking !== 'none' && (l.units?.length ?? 0) !== l.qty) throw problem(400, 'DEV-012', `Scan one IMEI or serial number per device: ${l.qty} needed, ${l.units?.length ?? 0} scanned.`);
        const price = pl?.unitPrice ?? l.unitPrice ?? 0;
        return { l, i, pl, price, goods: purchaseNet(b(l.qty), b(price)) };
      });
      const shares = spreadCharges(plan.map((p) => ({ value: p.goods, qty: b(p.l.qty) })), (r.charges ?? []).map((c) => ({ amount: b(c.amount), basis: c.basis })));
      const br = h.branches().find((x) => x.id === r.branchId)!;
      const number = h.next('gr', 'GR');
      const rid = r.id;
      const stockByCode = new Map<string, bigint>();
      plan.forEach((p, k) => {
        const lid = uid();
        const stockValue = p.goods + shares[k]!;
        const units: RLine['units'] = [];
        if (p.i.tracking !== 'none') {
          const extra = splitEven(shares[k]!, p.l.units!.length);
          p.l.units!.forEach((x, j) => { const cost = b(p.price) + extra[j]!; const unit = h.receiveUnit(p.i.variantId, x as UnitInput, n(cost)); units.push({ id: unit.id, imei: x.imei1 ?? x.serial ?? null, cost: n(cost), status: unit.status }); });
        } else h.stockIn(p.i.variantId, b(p.l.qty), p.l.qty ? (stockValue * 1000n) / b(p.l.qty) : 0n);
        rlines.push({ id: lid, lineNo: k + 1, variantId: p.i.variantId, sku: p.i.sku, description: p.i.descriptionEn, productType: p.i.productType, qty: p.l.qty, unitPrice: p.price, goodsValue: n(p.goods),
          chargeShare: n(shares[k]!), stockValue: n(stockValue), poLineId: p.pl?.id ?? null, invoiced: 0, returned: 0, openValue: 0, units, receiptId: rid, branchId: br.id, supplierId: s.id,
          orderPrice: p.pl?.unitPrice ?? null, taxCodeId: p.i.taxCodeId, tracking: p.i.tracking, returnedOpen: 0 });
        grni.set(lid, p.goods);
        const code = INVENTORY[p.i.productType] ?? '1420';
        stockByCode.set(code, (stockByCode.get(code) ?? 0n) + stockValue);
      });
      (r.charges ?? []).forEach((c, k) => {
        const cid = uid();
        const cs = supplier(c.supplierId ?? s.id);
        charges.push({ id: cid, lineNo: k + 1, kind: c.kind, description: c.description, amount: c.amount, basis: c.basis, supplierId: cs.id, supplierNameEn: cs.nameEn, openValue: c.amount, receiptId: rid, branchId: br.id });
        grni.set(cid, b(c.amount));
      });
      const total = [...stockByCode.values()].reduce((a, x) => a + x, 0n);
      const j = post('purch.receipt', br.id, r.receiptDate ?? today(), `Goods receipt ${number}`, [...[...stockByCode].map(([code, v]) => dr(code, br.id, v)), cr('2110', br.id, total)], ['goods_receipt', rid]);
      receipts.push({ id: rid, number, receiptDate: r.receiptDate ?? today(), branchId: br.id, branchCode: br.code, supplierId: s.id, supplierNameEn: s.nameEn, supplierNameAr: s.nameAr, poId: o?.id ?? null,
        poNumber: o?.number ?? null, deliveryNote: r.deliveryNote ?? null, note: r.note ?? null, goodsValue: n(plan.reduce((a, p) => a + p.goods, 0n)), chargesValue: r3((r.charges ?? []).reduce((a, c) => a + c.amount, 0)),
        receivedBy: u.displayNameEn, createdAt: now(), journalNumber: j?.number ?? null, warehouse: 'SHOP' });
      if (o) refreshOrder(o.id);
      return delay(receiptDetail(rid));
    },
    // ------------------------------------------------------------ invoices
    async openForSupplier(supplierId) {
      h.need('PURCH.INVOICE'); seed();
      return delay(structuredClone({
        receiptLines: rlines.filter((l) => l.supplierId === supplierId && r3(l.qty - l.invoiced - l.returnedOpen) > 0).map((l) => {
          const r = receipts.find((x) => x.id === l.receiptId)!;
          return { id: l.id, receiptId: r.id, receiptNumber: r.number, receiptDate: r.receiptDate, branchCode: r.branchCode, description: l.description, qty: l.qty,
            openQty: r3(l.qty - l.invoiced - l.returnedOpen), unitPrice: l.unitPrice, orderPrice: l.orderPrice, openValue: n(grni.get(l.id) ?? 0n), taxCodeId: l.taxCodeId, tracking: l.tracking };
        }),
        charges: charges.filter((c) => c.supplierId === supplierId && (grni.get(c.id) ?? 0n) > 0n).map((c) => {
          const r = receipts.find((x) => x.id === c.receiptId)!;
          return { id: c.id, receiptNumber: r.number, receiptDate: r.receiptDate, branchCode: r.branchCode, kind: c.kind, description: c.description, amount: c.amount, openValue: n(grni.get(c.id) ?? 0n) };
        }),
        outsideWork: [],
      }));
    },
    async supplierOpenItems(supplierId) {
      h.need('PURCH.VIEW');
      return delay(h.supplierLines.filter((l) => l.supplierId === supplierId && l.amount > 0 && entryOpen(l) > 0).sort((x, y) => (x.dueDate ?? x.docDate).localeCompare(y.dueDate ?? y.docDate))
        .map((l) => ({ id: l.id, number: l.number, kind: l.kind, docDate: l.docDate, dueDate: l.dueDate ?? l.docDate, branchId: l.branchId, branchCode: branchCode(l.branchId), amount: l.amount, open: entryOpen(l),
          supplierInvoiceNo: invoices.find((i) => i.entryId === l.id)?.supplierInvoiceNo ?? null })));
    },
    async purchaseInvoices(f) {
      h.need('PURCH.VIEW'); seed();
      return delay(invoices.filter((i) => (!f?.status || i.status === f.status || (f.status === 'open' && ['draft', 'matched', 'needs_approval'].includes(i.status))) && (!f?.supplierId || i.supplierId === f.supplierId))
        .map((i) => { const d = invoiceDetail(i.id).invoice; return { id: d.id, number: d.number, status: d.status, supplierInvoiceNo: d.supplierInvoiceNo, invoiceDate: d.invoiceDate, dueDate: d.dueDate,
          branchCode: d.branchCode, supplierNameEn: d.supplierNameEn, supplierNameAr: d.supplierNameAr, grossTotal: d.grossTotal, varianceTotal: d.varianceTotal, paid: d.paid }; }).reverse());
    },
    async purchaseInvoice(id) { h.need('PURCH.VIEW'); return delay(invoiceDetail(id)); },
    async enterInvoice(i) {
      h.need('PURCH.INVOICE'); seed();
      const existing = invoices.find((x) => x.id === i.id);
      if (existing && existing.status !== 'draft' && existing.status !== 'matched' && existing.status !== 'needs_approval') throw problem(409, 'PUR-003', `Invoice ${existing.number} is ${existing.status}.`);
      const s = supplier(i.supplierId);
      const dup = invoices.find((x) => x.id !== i.id && x.supplierId === s.id && x.status !== 'cancelled' && x.supplierInvoiceNo.trim().toLowerCase() === i.supplierInvoiceNo.trim().toLowerCase());
      if (dup) throw problem(409, 'PUR-008', `${s.nameEn}'s invoice ${i.supplierInvoiceNo.trim()} is already booked as ${dup.number}.`);
      const lines = matchLines(s.id, i.lines);
      const net = lines.reduce((a, l) => a + b(l.netAmount), 0n);
      const tax = lines.reduce((a, l) => a + b(l.taxAmount), 0n);
      const status = lines.every((l) => l.matchStatus === 'ok' || l.matchStatus === 'none') ? 'matched' : 'needs_approval';
      const br = h.branches().find((x) => x.id === i.branchId)!;
      const body = { supplierId: s.id, supplierNumber: s.number, supplierNameEn: s.nameEn, supplierNameAr: s.nameAr, supplierInvoiceNo: i.supplierInvoiceNo.trim(), invoiceDate: i.invoiceDate,
        dueDate: i.dueDate ?? addDays(i.invoiceDate, s.termsDays), status: status as 'matched' | 'needs_approval', netTotal: n(net), taxTotal: n(tax), grossTotal: n(net + tax),
        varianceTotal: n(lines.reduce((a, l) => a + b(l.variance), 0n)), note: i.note ?? null, lines, branchId: br.id, branchCode: br.code };
      if (existing) Object.assign(existing, body, { version: existing.version + 1 });
      else invoices.push({ ...body, id: i.id, number: h.next('pi', 'PI'), version: 1, overrideReason: null, overrideBy: null, postedAt: null, postedBy: null, journalNumber: null, journalId: null, paid: 0, entryId: null });
      return delay(invoiceDetail(i.id));
    },
    async updateInvoice(id, i) { return api.enterInvoice({ ...i, id }); },
    async postInvoice(id, overrideReason) {
      const u = h.need('PURCH.INVOICE');
      const inv = invoices.find((x) => x.id === id)!;
      if (inv.status === 'posted') return delay(invoiceDetail(id));
      if (inv.status === 'cancelled') throw problem(409, 'PUR-003', `Invoice ${inv.number} is cancelled.`);
      const lines = matchLines(inv.supplierId, inv.lines.map((l) => ({ kind: l.kind, qty: l.qty, unitPrice: l.unitPrice, receiptLineId: l.receiptLineId, receiptChargeId: l.receiptChargeId, outsideType: l.outsideType as 'refurb_cost' | null, outsideId: l.outsideId, accountId: l.accountId, expenseBranchId: l.expenseBranchId, description: l.description })));
      const outside = lines.filter((l) => l.matchStatus === 'price');
      if (outside.length) {
        if (!h.can('PURCH.INVOICE.OVERRIDE')) throw problem(409, 'PUR-004', `${outside.length} line(s) differ from the order by more than the tolerance. Someone with PURCH.INVOICE.OVERRIDE posts it, with a reason.`);
        if (!overrideReason?.trim()) throw problem(400, 'VAL-001', 'Say why the invoice is posted outside the tolerances.');
      }
      const jl: JLine[] = [];
      let grniCleared = 0n; let ppv = 0n; let stockVar = 0n;
      for (const l of lines) {
        if (l.kind === 'receipt') {
          const r = rlines.find((x) => x.id === l.receiptLineId)!;
          r.invoiced = r3(r.invoiced + l.qty);
          grni.set(r.id, (grni.get(r.id) ?? 0n) - b(l.expectedAmount));
          if (l.varianceInventory) {
            if (r.tracking === 'none') h.stockIn(r.variantId, 0n, 0n);
            const parts = splitEven(b(l.varianceInventory), r.units.filter((x) => x.status === 'in_stock').length || 1);
            r.units.filter((x) => x.status === 'in_stock').forEach((x, k) => { x.cost = n(b(x.cost) + parts[k]!); });
          }
          grniCleared += b(l.expectedAmount); stockVar += b(l.varianceInventory); ppv += b(l.variancePpv);
        } else if (l.kind === 'charge') { grni.set(l.receiptChargeId!, (grni.get(l.receiptChargeId!) ?? 0n) - b(l.expectedAmount)); grniCleared += b(l.expectedAmount); ppv += b(l.variancePpv); }
        else jl.push(dr(l.accountCode ?? '6900', inv.branchId, b(l.netAmount)));
      }
      const gross = b(inv.netTotal) + b(inv.taxTotal);
      jl.push(dr('2110', inv.branchId, grniCleared), dr('1420', inv.branchId, stockVar), dr('5300', inv.branchId, ppv), dr('1500', inv.branchId, b(inv.taxTotal)), cr('2100', inv.branchId, gross, inv.supplierId));
      const j = post('purch.invoice', inv.branchId, inv.invoiceDate, `Supplier invoice ${inv.number}`, jl, ['purchase_invoice', id]);
      const entryId = uid();
      h.supplierLines.push({ id: entryId, branchId: inv.branchId, supplierId: inv.supplierId, kind: 'invoice', amount: n(gross), docDate: inv.invoiceDate, dueDate: inv.dueDate, number: inv.number, note: `Invoice ${inv.supplierInvoiceNo}` });
      Object.assign(inv, { status: 'posted', lines, postedAt: now(), postedBy: u.displayNameEn, journalNumber: j?.number ?? null, journalId: j?.id ?? null, entryId,
        overrideBy: outside.length ? u.displayNameEn : null, overrideReason: outside.length ? overrideReason!.trim() : null });
      allocateCredits(inv.supplierId, inv.branchId);
      return delay(invoiceDetail(id));
    },
    async cancelInvoice(id) {
      h.need('PURCH.INVOICE');
      const inv = invoices.find((x) => x.id === id)!;
      if (inv.status === 'posted' || inv.status === 'cancelled') throw problem(409, 'PUR-003', `Invoice ${inv.number} is ${inv.status}; a posted invoice is corrected by a return or a debit note.`);
      inv.status = 'cancelled';
    },
    // ------------------------------------------------------------ returns and payments
    async supplierReturns(f) {
      h.need('PURCH.VIEW');
      return delay(returns.filter((r) => !f?.supplierId || r.supplierId === f.supplierId).map((r) => { const s = supplier(r.supplierId); return { id: r.id, number: r.number, returnDate: r.returnDate,
        branchCode: branchCode(r.branchId), supplierNameEn: s.nameEn, supplierNameAr: s.nameAr, reason: r.reason, netTotal: r.netTotal, taxTotal: r.taxTotal, stockValue: r.stockValue }; }).reverse());
    },
    async supplierReturn(id) {
      h.need('PURCH.VIEW');
      const r = returns.find((x) => x.id === id) ?? (() => { throw problem(404, 'GEN-404', 'That return was not found.'); })();
      const s = supplier(r.supplierId);
      const { lines, branchId, ...rest } = r;
      return delay(structuredClone({ supplierReturn: { ...rest, branchCode: branchCode(branchId), supplierNameEn: s.nameEn, supplierNameAr: s.nameAr }, lines }));
    },
    async returnToSupplier(x) {
      h.need('PURCH.RETURN');
      if (!x.reason.trim()) throw problem(400, 'VAL-001', 'Say why the goods go back.');
      const br = h.branches().find((y) => y.id === x.branchId)!;
      const number = h.next('dn', 'DN');
      const id = x.id;
      const lines: (typeof returns)[number]['lines'] = [];
      let back = 0n; let debitNet = 0n; let debitTax = 0n; let stockOut = 0n;
      x.lines.forEach((l, k) => {
        const r = rlines.find((y) => y.id === l.receiptLineId)!;
        if (l.qty > r3(r.qty - r.returned)) throw problem(409, 'PUR-002', `Only ${r3(r.qty - r.returned)} of ${r.description} can still go back.`);
        const openQty = r3(r.qty - r.invoiced - r.returnedOpen);
        const fromOpen = Math.min(l.qty, Math.max(0, openQty));
        const cost = b(r.stockValue) * b(l.qty) / b(r.qty);
        stockOut += cost;
        if (r.tracking === 'none') h.stockIn(r.variantId, -b(l.qty), 0n);
        for (const u of r.units.filter((u2) => l.deviceUnitIds?.includes(u2.id))) u.status = 'supplier_returned';
        if (fromOpen > 0) { const v = clearing(b(fromOpen), b(openQty), grni.get(r.id) ?? 0n); grni.set(r.id, (grni.get(r.id) ?? 0n) - v); back += v; r.returnedOpen = r3(r.returnedOpen + fromOpen); }
        const inv = r3(l.qty - fromOpen);
        let net = 0n; let tax = 0n;
        if (inv > 0) { const il = invoices.flatMap((i) => i.lines).find((y) => y.receiptLineId === r.id); net = purchaseNet(b(inv), b(il?.unitPrice ?? r.unitPrice)); tax = purchaseTax(net, 5); debitNet += net; debitTax += tax; }
        r.returned = r3(r.returned + l.qty);
        lines.push({ lineNo: k + 1, qty: l.qty, stockValue: n(cost), invoiced: inv > 0, netAmount: n(net + (fromOpen > 0 ? back : 0n)), taxAmount: n(tax), description: r.description, receiptNumber: receipts.find((y) => y.id === r.receiptId)!.number, imei: null });
      });
      const ppvV = back + debitNet - stockOut;
      const j = post('purch.return', br.id, x.returnDate ?? today(), `Goods returned to the supplier, ${number}`,
        [dr('2110', br.id, back), dr('2100', br.id, debitNet + debitTax, x.supplierId), cr('1500', br.id, debitTax), cr('1420', br.id, stockOut), cr('5300', br.id, ppvV)], ['supplier_return', id]);
      if (debitNet + debitTax > 0n) {
        h.supplierLines.push({ id: uid(), branchId: br.id, supplierId: x.supplierId, kind: 'debit_note', amount: -n(debitNet + debitTax), docDate: today(), dueDate: null, number, note: x.reason });
        allocateCredits(x.supplierId, br.id);
      }
      returns.push({ id, number, returnDate: x.returnDate ?? today(), reason: x.reason, netTotal: n(back + debitNet), taxTotal: n(debitTax), stockValue: n(stockOut), branchId: br.id, supplierId: x.supplierId, journalNumber: j?.number ?? null, createdAt: now(), lines });
      return api.supplierReturn(id);
    },
    async supplierPayments(f) {
      h.need('PURCH.VIEW');
      return delay(payments.filter((p) => !f?.supplierId || p.supplierId === f.supplierId).map((p) => { const s = supplier(p.supplierId); return { id: p.id, number: p.number, paymentDate: p.paymentDate,
        branchCode: branchCode(p.branchId), supplierNameEn: s.nameEn, supplierNameAr: s.nameAr, method: p.method, reference: p.reference, amount: p.amount }; }).reverse());
    },
    async supplierPayment(id) {
      h.need('PURCH.VIEW');
      const p = payments.find((x) => x.id === id) ?? (() => { throw problem(404, 'GEN-404', 'That payment was not found.'); })();
      const s = supplier(p.supplierId);
      const { entries, branchId, ...rest } = p;
      return delay(structuredClone({
        payment: { ...rest, branchCode: branchCode(branchId), supplierNameEn: s.nameEn, supplierNameAr: s.nameAr },
        allocations: allocations.filter((a) => entries.includes(a.credit)).map((a) => { const d = h.supplierLines.find((l) => l.id === a.debit)!; return { number: d.number, kind: d.kind, branchCode: branchCode(d.branchId), amount: a.amount, dueDate: d.dueDate }; }),
      }));
    },
    async paySupplier(p) {
      h.need('PURCH.PAY');
      if (payments.some((x) => x.id === p.id)) return api.supplierPayment(p.id);
      if (p.method !== 'cash' && !p.reference?.trim()) throw problem(400, 'VAL-001', 'Give the transfer or cheque reference.');
      const s = supplier(p.supplierId);
      if (s.status !== 'active') throw problem(409, 'SUP-001', `${s.nameEn} is ${s.status}.`);
      const open = h.supplierLines.filter((l) => l.supplierId === s.id && l.amount > 0 && entryOpen(l) > 0);
      let allocs: { entry: SupplierLine; amount: number }[];
      if (p.allocations?.length) {
        allocs = p.allocations.map((a) => {
          const e = open.find((x) => x.id === a.entryId);
          if (!e) throw problem(409, 'PUR-005', 'That invoice is not open for this supplier.');
          if (a.amount > entryOpen(e)) throw problem(409, 'PUR-005', `${e.number} has only ${entryOpen(e)} open.`);
          return { entry: e, amount: a.amount };
        });
        if (allocs.reduce((a, x) => a + x.amount, 0) > p.amount) throw problem(409, 'PUR-005', 'The allocations add up to more than the payment.');
      } else {
        const mine = open.filter((l) => l.branchId === p.branchId).map((l) => ({ id: l.id, due: l.dueDate ?? l.docDate, open: b(entryOpen(l)) }));
        allocs = allocateOldestFirst(mine, b(p.amount)).allocations.map((a) => ({ entry: open.find((x) => x.id === a.id)!, amount: n(a.amount) }));
      }
      const br = h.branches().find((x) => x.id === p.branchId)!;
      const number = h.next('pv', 'PV');
      const byBranch = new Map<string, number>();
      for (const a of allocs) byBranch.set(a.entry.branchId, r3((byBranch.get(a.entry.branchId) ?? 0) + a.amount));
      const rest = r3(p.amount - allocs.reduce((a, x) => a + x.amount, 0));
      if (rest > 0) byBranch.set(br.id, r3((byBranch.get(br.id) ?? 0) + rest));
      const entries = new Map<string, string>();
      for (const [bid, amount] of byBranch) {
        const eid = uid(); entries.set(bid, eid);
        h.supplierLines.push({ id: eid, branchId: bid, supplierId: s.id, kind: 'payment', amount: -amount, docDate: p.paymentDate ?? today(), dueDate: null, number, note: `Payment ${number}` });
      }
      for (const a of allocs) allocations.push({ debit: a.entry.id, credit: entries.get(a.entry.branchId)!, amount: a.amount });
      const lines: JLine[] = [cr(PAY[p.method]!, br.id, b(p.amount))];
      for (const [bid, amount] of byBranch) {
        lines.push(dr('2100', bid, b(amount), s.id));
        if (bid !== br.id) lines.push(cr('2500', bid, b(amount)), dr('1310', br.id, b(amount)));
      }
      const j = post('purch.payment', br.id, p.paymentDate ?? today(), `Supplier payment ${number}: ${s.nameEn}`, lines, ['supplier_payment', p.id]);
      payments.push({ id: p.id, number, paymentDate: p.paymentDate ?? today(), method: p.method, reference: p.reference ?? null, amount: p.amount, note: p.note ?? null, branchId: br.id, supplierId: s.id, journalNumber: j?.number ?? null, createdAt: now(), entries: [...entries.values()] });
      return api.supplierPayment(p.id);
    },
    // ------------------------------------------------------------ accounting
    async chart() {
      h.need('ACCT.VIEW'); seed();
      return delay(structuredClone(accounts.map((a) => ({ ...a, balance: n(balanceOf(a.code)), used: journals.some((j) => j.lines.some((l) => l.code === a.code)) }))));
    },
    async createAccount(x) {
      h.need('ACCT.COA.MANAGE'); seed();
      if (accounts.some((a) => a.code === x.code)) throw problem(409, 'GEN-409', 'That account code is already in use.');
      const a: ChartAccount = { id: uid(), code: x.code, nameEn: x.nameEn, nameAr: x.nameAr, type: x.type, parentId: x.parentId ?? null, parentCode: accounts.find((p) => p.id === x.parentId)?.code ?? null,
        isPostable: x.isPostable ?? true, isControl: x.isControl ?? false, status: 'active', cashFlowClass: x.cashFlowClass ?? 'none', version: 1, balance: 0, used: false };
      accounts.push(a); accounts.sort((p, q) => p.code.localeCompare(q.code));
      return delay({ id: a.id, code: a.code });
    },
    async updateAccount(id, x) {
      h.need('ACCT.COA.MANAGE');
      const a = accounts.find((y) => y.id === id)!;
      if (x.status === 'inactive' && balanceOf(a.code) !== 0n) throw problem(409, 'ACC-009', `Account ${a.code} has a balance; move it before switching the account off.`);
      Object.assign(a, { nameEn: x.nameEn, nameAr: x.nameAr, parentId: x.parentId, isControl: x.isControl, cashFlowClass: x.cashFlowClass, status: x.status, version: a.version + 1 });
    },
    async acctPolicy() { h.need('ACCT.VIEW'); return delay({ ...acctPolicy }); },
    async updateAcctPolicy(p) { h.need('ACCT.COA.MANAGE'); acctPolicy = { ...p, version: p.version + 1 }; },
    async periods(fy) {
      h.need('ACCT.VIEW'); seed();
      return delay({
        fiscalYear: fy ?? year, years: [year], yearClosed, branches: h.branches().map((x) => ({ id: x.id, code: x.code, nameEn: x.nameEn, nameAr: x.nameAr, isHeadOffice: x.isHeadOffice, branchServerSeenAt: null })),
        periods: periods.map((p) => ({ id: p.id, periodNo: p.periodNo, startsOn: p.startsOn, endsOn: p.endsOn, businessStatus: 'open' as PeriodStatus,
          branches: JSON.stringify(Object.fromEntries(h.branches().map((x) => [x.id, statusAt(p, x.id)]))), journals: journals.filter((j) => j.date >= p.startsOn && j.date <= p.endsOn).length,
          vatFiled: vatReturns.some((v) => v.status === 'filed' && v.periodFrom <= p.endsOn && v.periodTo >= p.startsOn) })),
      });
    },
    async setPeriodStatus(id, status, reason, branchId) {
      h.need('ACCT.PERIOD.CLOSE');
      if (!reason.trim()) throw problem(400, 'VAL-001', 'Say why.');
      const p = periods.find((x) => x.id === id)!;
      if (status === 'open' && vatReturns.some((v) => v.status === 'filed' && v.periodFrom <= p.endsOn && v.periodTo >= p.startsOn)) throw problem(409, 'ACC-006', 'A filed VAT return covers this period; it stays closed.');
      if (yearClosed && status !== 'closed') throw problem(409, 'ACC-008', `Fiscal year ${year} is closed; its periods stay closed.`);
      const targets = branchId ? [branchId] : h.branches().map((x) => x.id);
      let changed = 0;
      for (const t of targets) if (statusAt(p, t) !== status) { p.status.set(t, status); changed++; }
      return delay({ changed });
    },
    async yearEnd(fy) {
      h.need('ACCT.YEAR_END');
      if (yearClosed) throw problem(409, 'ACC-008', `Fiscal year ${fy} is already closed.`);
      for (const p of periods) for (const x of h.branches()) if (statusAt(p, x.id) === 'open') throw problem(409, 'ACC-008', `Period ${p.periodNo} of ${fy} is still open at ${x.code}; close every period of the year first.`);
      const pl = balances(`${fy}-12-31`, `${fy}-01-01`).filter((x) => x.a.type === 'revenue' || x.a.type === 'expense');
      const profit = -pl.reduce((s, x) => s + x.v, 0n);
      yearClosed = true;
      return delay({ year: fy, profit: n(profit), journalId: null });
    },
    async journals(f) {
      h.need('ACCT.VIEW'); seed();
      const k = f?.q?.trim().toLowerCase();
      return delay(journals.filter((j) => (!f?.from || j.date >= f.from) && (!f?.to || j.date <= f.to) && (!f?.branchId || j.lines.some((l) => l.branchId === f.branchId))
        && (!k || j.number.toLowerCase().includes(k) || j.description.toLowerCase().includes(k)))
        .map((j) => ({ id: j.id, number: j.number, journalDate: j.date, postingKind: j.kind, description: j.description, branchCode: branchCode(j.branchId), sourceDocType: j.sourceType,
          total: n(j.lines.reduce((s, l) => s + l.debit, 0n)), isReversal: !!j.reversalOf })).reverse());
    },
    async journal(id) {
      h.need('ACCT.VIEW');
      const j = journals.find((x) => x.id === id) ?? (() => { throw problem(404, 'GEN-404', 'That journal was not found.'); })();
      const rev = journals.find((x) => x.reversalOf === id);
      return delay({
        journal: { id: j.id, number: j.number, journalDate: j.date, postingKind: j.kind, description: j.description, sourceDocType: j.sourceType, sourceDocId: j.sourceId, branchCode: branchCode(j.branchId),
          fiscalYear: year, periodNo: periodOf(j.date)?.periodNo ?? 1, postedAt: j.postedAt, postedBy: j.by, reversalOfId: j.reversalOf, reversalOfNumber: journals.find((x) => x.id === j.reversalOf)?.number ?? null,
          reversedById: rev?.id ?? null, reversedByNumber: rev?.number ?? null },
        lines: j.lines.map((l, k) => ({ lineNo: k + 1, accountCode: l.code, accountNameEn: account(l.code).nameEn, accountNameAr: account(l.code).nameAr, branchCode: branchCode(l.branchId),
          debit: n(l.debit), credit: n(l.credit), partyType: l.party ? 'supplier' : null, partyName: l.party ? h.suppliers().find((s) => s.id === l.party)?.nameEn ?? null : null, memo: l.memo ?? null })),
      });
    },
    async manualJournals(f) {
      h.need('ACCT.VIEW');
      return delay(manuals.filter((m) => !f?.status || m.status === f.status).map((m) => ({ id: m.id, number: m.number, journalDate: m.journalDate, description: m.description, status: m.status, total: m.total,
        branchCode: m.branchCode, createdBy: m.createdBy, reversed: !!m.reversalJournalId })).reverse());
    },
    async manualJournal(id) {
      h.need('ACCT.VIEW');
      const m = manuals.find((x) => x.id === id) ?? (() => { throw problem(404, 'GEN-404', 'That journal was not found.'); })();
      const { lines, ...journal } = m;
      return delay(structuredClone({ journal, lines }));
    },
    async createManualJournal(x) {
      const u = h.need('ACCT.JOURNAL.MANUAL'); seed();
      const existing = manuals.find((m) => m.id === x.id);
      if (existing && existing.status !== 'draft') throw problem(409, 'ACC-003', `Journal ${existing.number} is ${existing.status.replace('_', ' ')}; only a draft is changed.`);
      const lines = x.lines.map((l, k) => { const a = accounts.find((y) => y.id === l.accountId)!; return { lineNo: k + 1, accountId: a.id, accountCode: a.code, accountNameEn: a.nameEn, accountNameAr: a.nameAr, isControl: a.isControl, branchId: l.branchId, branchCode: branchCode(l.branchId), debit: l.debit, credit: l.credit, memo: l.memo ?? null }; });
      const total = r3(lines.reduce((s, l) => s + l.debit, 0));
      if (existing) { Object.assign(existing, { journalDate: x.journalDate, description: x.description, lines, total, intoSoftClosed: x.intoSoftClosed ?? false, version: existing.version + 1 }); return api.manualJournal(x.id); }
      manuals.push({ id: x.id, number: h.next('mj', 'MJ'), journalDate: x.journalDate, description: x.description, status: 'draft', total, intoSoftClosed: x.intoSoftClosed ?? false,
        version: 1, branchId: x.branchId, branchCode: branchCode(x.branchId), createdBy: u.displayNameEn, createdById: u.id, approvedBy: null, approvedAt: null, rejectedReason: null, journalId: null, journalNumber: null,
        reversalJournalId: null, reversalNumber: null, reversedReason: null, createdAt: now(), approvalAbove: acctPolicy.manualJournalApprovalAbove, lines });
      return api.manualJournal(x.id);
    },
    async updateManualJournal(id, x) { return api.createManualJournal({ ...x, id }); },
    async submitManualJournal(id) {
      h.need('ACCT.JOURNAL.MANUAL');
      const m = manuals.find((x) => x.id === id)!;
      if (m.status !== 'draft') throw problem(409, 'ACC-003', `Journal ${m.number} is ${m.status}.`);
      const d = m.lines.reduce((s, l) => s + b(l.debit), 0n); const c = m.lines.reduce((s, l) => s + b(l.credit), 0n);
      if (d !== c) throw problem(400, 'ACC-002', `The journal does not balance: debit ${fromBaisa(d)}, credit ${fromBaisa(c)}.`);
      const control = m.lines.find((l) => l.isControl);
      if (control && !h.can('ACCT.CONTROL_ACCOUNT.POST')) throw problem(403, 'ACC-004', `Account ${control.accountCode} is a control account; posting to it by hand needs ACCT.CONTROL_ACCOUNT.POST.`);
      if (m.total > acctPolicy.manualJournalApprovalAbove) { m.status = 'pending_approval'; return api.manualJournal(id); }
      const j = post('acct.manual', m.branchId, m.journalDate, `${m.number}: ${m.description}`, m.lines.map((l) => ({ code: l.accountCode, branchId: l.branchId, debit: b(l.debit), credit: b(l.credit), memo: l.memo })), ['manual_journal', id], null, m.intoSoftClosed)!;
      Object.assign(m, { status: 'posted' as ManualStatus, journalId: j.id, journalNumber: j.number });
      return api.manualJournal(id);
    },
    async approveManualJournal(id) {
      const u = h.need('ACCT.JOURNAL.APPROVE');
      const m = manuals.find((x) => x.id === id)!;
      if (m.status !== 'pending_approval') throw problem(409, 'ACC-003', `Journal ${m.number} is ${m.status}.`);
      if (m.createdById === u.id) throw problem(403, 'ACC-005', 'Someone other than the person who entered it approves this journal.');
      const j = post('acct.manual', m.branchId, m.journalDate, `${m.number}: ${m.description}`, m.lines.map((l) => ({ code: l.accountCode, branchId: l.branchId, debit: b(l.debit), credit: b(l.credit), memo: l.memo })), ['manual_journal', id], null, m.intoSoftClosed)!;
      Object.assign(m, { status: 'posted' as ManualStatus, journalId: j.id, journalNumber: j.number, approvedBy: u.displayNameEn, approvedAt: now() });
      return api.manualJournal(id);
    },
    async rejectManualJournal(id, reason) {
      h.need('ACCT.JOURNAL.APPROVE');
      const m = manuals.find((x) => x.id === id)!;
      Object.assign(m, { status: 'rejected' as ManualStatus, rejectedReason: reason });
      return api.manualJournal(id);
    },
    async reverseManualJournal(id, reason) {
      h.need('ACCT.JOURNAL.MANUAL');
      const m = manuals.find((x) => x.id === id)!;
      if (m.status !== 'posted' || m.reversalJournalId) throw problem(409, 'ACC-003', `Journal ${m.number} is not posted or is already reversed.`);
      const orig = journals.find((x) => x.id === m.journalId)!;
      const j = post('acct.manual.reversal', m.branchId, today(), `Reversal of ${m.number}: ${reason}`, orig.lines.map((l) => ({ ...l, debit: l.credit, credit: l.debit })), ['manual_journal', id], orig.id)!;
      Object.assign(m, { reversalJournalId: j.id, reversalNumber: j.number, reversedReason: reason });
      return api.manualJournal(id);
    },
    async trialBalance(f) {
      h.need('ACCT.VIEW'); seed();
      const to = f.to ?? today(); const from = f.from ?? `${year}-01-01`;
      const codes = [...new Set(journals.flatMap((j) => j.lines.map((l) => l.code)))].sort();
      const rows = codes.map((code) => {
        const ls = journals.filter((j) => j.date <= to).flatMap((j) => j.lines.map((l) => ({ ...l, date: j.date }))).filter((l) => l.code === code && (!f.branchId || l.branchId === f.branchId));
        const opening = ls.filter((l) => l.date < from).reduce((s, l) => s + l.debit - l.credit, 0n);
        const debit = ls.filter((l) => l.date >= from).reduce((s, l) => s + l.debit, 0n);
        const credit = ls.filter((l) => l.date >= from).reduce((s, l) => s + l.credit, 0n);
        const a = account(code);
        return { id: a.id, code, nameEn: a.nameEn, nameAr: a.nameAr, type: a.type, isControl: a.isControl, opening: n(opening), debit: n(debit), credit: n(credit), closing: n(opening + debit - credit) };
      }).filter((r) => r.opening || r.debit || r.credit);
      const sum = (k: 'opening' | 'debit' | 'credit' | 'closing', pos: boolean) => r3(rows.reduce((s, r) => s + (k === 'debit' || k === 'credit' ? r[k] : pos ? Math.max(0, r[k]) : Math.max(0, -r[k])), 0));
      const totals = { openingDebit: sum('opening', true), openingCredit: sum('opening', false), debit: sum('debit', true), credit: sum('credit', true), closingDebit: sum('closing', true), closingCredit: sum('closing', false) };
      return delay({ from, to, branchId: f.branchId ?? null, rows, earlierResult: 0, totals, balanced: totals.debit === totals.credit && totals.closingDebit === totals.closingCredit, asOf: asOf() });
    },
    async generalLedger(accountId, f) {
      h.need('ACCT.VIEW');
      const a = accounts.find((x) => x.id === accountId)!;
      const from = f.from ?? `${year}-01-01`; const to = f.to ?? today();
      const opening = balanceOf(a.code, f.branchId, addDays(from, -1));
      let bal = opening;
      const lines = journals.filter((j) => j.date >= from && j.date <= to).flatMap((j) => j.lines.filter((l) => l.code === a.code && (!f.branchId || l.branchId === f.branchId)).map((l) => {
        bal += l.debit - l.credit;
        return { journalId: j.id, number: j.number, date: j.date, description: j.description, postingKind: j.kind, sourceDocType: j.sourceType, sourceDocId: j.sourceId, branchCode: branchCode(l.branchId), debit: n(l.debit), credit: n(l.credit), memo: l.memo ?? null, partyName: null, balance: n(bal) };
      }));
      return delay({ account: { id: a.id, code: a.code, nameEn: a.nameEn, nameAr: a.nameAr, type: a.type }, from, to, opening: n(opening), lines, closing: n(bal) });
    },
    async profitAndLoss(f) {
      h.need('ACCT.VIEW'); seed();
      const list = balances(f.to ?? today(), f.from ?? `${year}-01-01`, f.branchId, false);
      const rev = list.filter((x) => x.a.type === 'revenue'); const cost = list.filter((x) => x.a.type === 'expense' && x.code.startsWith('5')); const ex = list.filter((x) => x.a.type === 'expense' && !x.code.startsWith('5'));
      const tr = -rev.reduce((s, x) => s + x.v, 0n); const tc = cost.reduce((s, x) => s + x.v, 0n); const te = ex.reduce((s, x) => s + x.v, 0n);
      return delay({ from: f.from ?? `${year}-01-01`, to: f.to ?? today(), branchId: f.branchId ?? null, revenue: rowsOf(rev, -1n), costOfSales: rowsOf(cost, 1n), expenses: rowsOf(ex, 1n),
        totalRevenue: n(tr), totalCostOfSales: n(tc), grossProfit: n(tr - tc), totalExpenses: n(te), netProfit: n(tr - tc - te), asOf: asOf() });
    },
    async balanceSheet(f) {
      h.need('ACCT.VIEW'); seed();
      const list = balances(f.asOf ?? today(), undefined, f.branchId);
      const due = f.branchId ? 0n : (() => { const a = balanceOf('1310', undefined, f.asOf); const c = -balanceOf('2500', undefined, f.asOf); return a < c ? a : c; })();
      const assets = rowsOf(list.filter((x) => x.a.type === 'asset'), 1n); const liab = rowsOf(list.filter((x) => x.a.type === 'liability'), -1n); const eq = rowsOf(list.filter((x) => x.a.type === 'equity'), -1n);
      const result = -list.filter((x) => x.a.type === 'revenue' || x.a.type === 'expense').reduce((s, x) => s + x.v, 0n);
      const ta = assets.reduce((s, x) => s + b(x.amount), 0n) - due; const tl = liab.reduce((s, x) => s + b(x.amount), 0n) - due; const te = eq.reduce((s, x) => s + b(x.amount), 0n) + result;
      return delay({ asOf: f.asOf ?? today(), branchId: f.branchId ?? null, assets, liabilities: liab, equity: eq, yearResult: n(result), earlierResult: 0,
        elimination: { dueFromBranches: n(balanceOf('1310')), dueToBranches: n(-balanceOf('2500')), eliminated: n(due), difference: n(balanceOf('1310') + balanceOf('2500')) },
        totalAssets: n(ta), totalLiabilities: n(tl), totalEquity: n(te), balanced: ta === tl + te, branches: asOf() });
    },
    async cashFlow(f) {
      h.need('ACCT.VIEW'); seed();
      const from = f.from ?? `${year}-01-01`; const to = f.to ?? today();
      const list = balances(to, from, f.branchId, false);
      const profit = -list.filter((x) => x.a.type === 'revenue' || x.a.type === 'expense').reduce((s, x) => s + x.v, 0n);
      const bs = list.filter((x) => x.a.type !== 'revenue' && x.a.type !== 'expense');
      const sec = (c: CashFlowClass) => rowsOf(bs.filter((x) => x.a.cashFlowClass === c), -1n);
      const tot = (c: CashFlowClass) => -bs.filter((x) => x.a.cashFlowClass === c).reduce((s, x) => s + x.v, 0n);
      const operating = profit + tot('operating') + tot('none'); const investing = tot('investing'); const financing = tot('financing');
      const cash = bs.filter((x) => x.a.cashFlowClass === 'cash').reduce((s, x) => s + x.v, 0n);
      const opening = ['1100', '1150', '1200'].reduce((s, c) => s + balanceOf(c, f.branchId, addDays(from, -1)), 0n);
      return delay({ from, to, branchId: f.branchId ?? null, netProfit: n(profit), operatingChanges: sec('operating'), operating: n(operating), investingItems: sec('investing'), investing: n(investing),
        financingItems: sec('financing'), financing: n(financing), netChange: n(operating + investing + financing), openingCash: n(opening), closingCash: n(opening + cash), cashMovement: n(cash),
        difference: n(operating + investing + financing - cash), asOf: asOf() });
    },
    async vatPeriods() {
      h.need('ACCT.VIEW');
      const q = Math.floor((Number(today().slice(5, 7)) - 1) / 3);
      return delay({ months: 3, periods: [0, 1, 2, 3].map((k) => { const m = (q - k) * 3; const y = year + Math.floor(m / 12); const mm = ((m % 12) + 12) % 12; const from = `${y}-${String(mm + 1).padStart(2, '0')}-01`; return { from, to: new Date(Date.UTC(y, mm + 3, 0)).toISOString().slice(0, 10) }; }) });
    },
    async vatBoxes() { h.need('ACCT.VIEW'); return delay(VAT_BOXES.map(([code, sort, nameEn, nameAr, source, formula]) => ({ code, sort, nameEn, nameAr, source, formula, needsReview: !boxesReviewed }))); },
    async reviewVatBoxes() { h.need('ACCT.VAT.RETURN'); boxesReviewed = true; },
    async vatReturns() {
      h.need('ACCT.VIEW');
      return delay(vatReturns.map((v) => ({ id: v.id, number: v.number, periodFrom: v.periodFrom, periodTo: v.periodTo, status: v.status, outputVat: v.outputVat, inputVat: v.inputVat, netVat: v.netVat, filedAt: v.filedAt, paidAt: v.paidAt })));
    },
    async prepareVatReturn(from, to) {
      h.need('ACCT.VAT.RETURN'); seed();
      let v = vatReturns.find((x) => x.periodFrom === from);
      const adj = v?.adjustments ?? {};
      const c = vatCompute(from, to, adj);
      if (v && v.status === 'filed') return delay(vatDetail(v.id));
      if (!v) { v = { id: uid(), number: `VAT-${from.slice(0, 7)}`, periodFrom: from, periodTo: to, status: 'draft', boxes: {}, adjustments: {}, outputVat: 0, inputVat: 0, netVat: 0, refundRequested: false, filedAt: null, filedBy: null, filingReference: null, settlementNumber: null, paymentNumber: null, paidAt: null, version: 1 }; vatReturns.unshift(v); }
      Object.assign(v, { boxes: c.boxes, outputVat: c.boxes['5a']!.vat, inputVat: c.boxes['6e']!.vat, netVat: c.boxes['7']!.vat });
      return delay(vatDetail(v.id));
    },
    async vatReturn(id) { h.need('ACCT.VIEW'); return delay(vatDetail(id)); },
    async adjustVatReturn(id, boxes, refundRequested) {
      h.need('ACCT.VAT.RETURN');
      const v = vatReturns.find((x) => x.id === id)!;
      if (v.status === 'filed') throw problem(409, 'ACC-006', 'A filed return is not changed.');
      Object.assign(v, { adjustments: boxes, refundRequested, version: v.version + 1 });
      return api.prepareVatReturn(v.periodFrom, v.periodTo);
    },
    async fileVatReturn(id, reference) {
      const u = h.need('ACCT.VAT.RETURN');
      const v = vatReturns.find((x) => x.id === id)!;
      if (v.status === 'filed') return delay(vatDetail(id));
      if (!boxesReviewed) throw problem(409, 'ACC-006', 'The accountant confirms the VAT return boxes before the first return is filed.');
      for (const p of periods.filter((x) => x.startsOn <= v.periodTo && x.endsOn >= v.periodFrom))
        for (const x of h.branches()) if (statusAt(p, x.id) === 'open') throw problem(409, 'ACC-007', `Period ${p.periodNo} of ${year} is still open at ${x.code}. Close the return's months at every branch (soft close is enough).`);
      const c = vatCompute(v.periodFrom, v.periodTo, v.adjustments);
      const ho = h.branchId;
      const j = post('vat.settlement', ho, today(), `VAT return ${v.number}`, [dr('2200', ho, c.out), cr('1500', ho, c.inp), cr('2210', ho, c.out - c.inp)], ['vat_return', id]);
      Object.assign(v, { status: 'filed', filedAt: now(), filedBy: u.displayNameEn, filingReference: reference ?? null, settlementNumber: j?.number ?? null });
      return delay(vatDetail(id));
    },
    async payVatReturn(id, reference) {
      h.need('ACCT.VAT.RETURN');
      const v = vatReturns.find((x) => x.id === id)!;
      if (v.status !== 'filed') throw problem(409, 'ACC-006', 'File the return first.');
      const ho = h.branchId;
      const j = post('vat.payment', ho, today(), `VAT return ${v.number} paid (${reference})`, [dr('2210', ho, b(v.netVat)), cr('1200', ho, b(v.netVat))], ['vat_payment', id]);
      Object.assign(v, { paymentNumber: j?.number ?? null, paidAt: now() });
      return delay(vatDetail(id));
    },
    async cardSettlements() {
      h.need('ACCT.VIEW'); seed();
      return delay({
        clearing: h.branches().map((x) => ({ branchId: x.id, branchCode: x.code, nameEn: x.nameEn, nameAr: x.nameAr, balance: n(balanceOf('1150', x.id)) })),
        settlements: cardSettlements.map((s) => ({ ...s, branchCode: branchCode(s.branchId) })).reverse(),
      });
    },
    async settleCards(s) {
      h.need('ACCT.CARD_SETTLE'); seed();
      const held = balanceOf('1150', s.branchId);
      if (b(s.grossAmount) > held) throw problem(409, 'ACC-010', `Card clearing at this branch holds ${fromBaisa(held)}; the bank cannot settle ${fromBaisa(b(s.grossAmount))}.`);
      const number = h.next('cs', 'CS');
      post('acct.card_settlement', s.branchId, s.settlementDate ?? today(), `Card settlement ${number}`, [dr('1200', s.branchId, b(s.grossAmount) - b(s.fees)), dr('6800', s.branchId, b(s.fees)), cr('1150', s.branchId, b(s.grossAmount))]);
      cardSettlements.push({ id: s.id, number, settlementDate: s.settlementDate ?? today(), branchId: s.branchId, grossAmount: s.grossAmount, fees: s.fees, netAmount: r3(s.grossAmount - s.fees), reference: s.reference });
      return delay({ id: s.id, number });
    },
    async reconciliation() { h.need('ACCT.VIEW'); seed(); const rows = ledgerChecks(); return delay({ runId: uid(), rows, clean: rows.every((r) => r.difference === 0) }); },
    async runReconciliation() { return api.reconciliation(); },
    async expenses(f) {
      h.need('EXP.VIEW'); seed();
      return delay(expenses.filter((e) => (!f?.from || e.expenseDate >= f.from) && (!f?.to || e.expenseDate <= f.to) && (!f?.branchId || e.branchId === f.branchId))
        .map((e) => { const a = accounts.find((x) => x.id === e.accountId)!; return { ...e, branchCode: branchCode(e.branchId), accountCode: a.code, accountNameEn: a.nameEn, accountNameAr: a.nameAr }; }).reverse());
    },
    async expenseAccounts() {
      h.need(); seed();
      return delay(accounts.filter((a) => a.isPostable && ((a.type === 'expense' && !a.code.startsWith('5')) || a.cashFlowClass === 'investing')).map((a) => ({ id: a.id, code: a.code, nameEn: a.nameEn, nameAr: a.nameAr, type: a.type })));
    },
    async recordExpense(e) {
      h.need('EXP.CREATE'); seed();
      if (!e.description.trim()) throw problem(400, 'VAL-001', 'Describe the expense.');
      const a = accounts.find((x) => x.id === e.accountId)!;
      const tax = purchaseTax(b(e.netAmount), 5);
      const number = h.next('ex', 'EX');
      const j = post('exp.expense', e.branchId, e.expenseDate ?? today(), `Expense ${number}: ${e.description}`, [dr(a.code, e.branchId, b(e.netAmount)), dr('1500', e.branchId, tax), cr(PAY[e.paidBy]!, e.branchId, b(e.netAmount) + tax)], ['expense', e.id]);
      const row = { id: e.id, number, expenseDate: e.expenseDate ?? today(), branchId: e.branchId, accountId: a.id, description: e.description, payee: e.payee ?? null, netAmount: e.netAmount, taxAmount: n(tax), grossAmount: n(b(e.netAmount) + tax), paidBy: e.paidBy, journalNumber: j?.number ?? null };
      expenses.push(row);
      return delay({ ...row, branchCode: branchCode(e.branchId), accountCode: a.code, accountNameEn: a.nameEn, accountNameAr: a.nameAr });
    },
  };
  return { api, seed };
}
