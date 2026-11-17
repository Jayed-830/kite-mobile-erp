// Phase 11: purchasing (blueprint 16), accounting (17) and expenses. Amounts are OMR numbers with three decimals.

export type OrderStatus = 'draft' | 'approved' | 'sent' | 'partly_received' | 'received' | 'closed' | 'cancelled';
export type InvoiceStatus = 'draft' | 'matched' | 'needs_approval' | 'posted' | 'cancelled';
export type MatchStatus = 'ok' | 'price' | 'qty' | 'none';
export type InvoiceLineKind = 'receipt' | 'charge' | 'outside_work' | 'expense';
export type PayMethod = 'cash' | 'bank_transfer' | 'cheque';

export interface PurchPolicy { priceTolerancePct: number; priceToleranceAmount: number; overReceiptPct: number; supplierList: 'all' | 'approved'; version: number }
export interface ApprovalLimit { id?: string; roleId: string; roleCode?: string; roleNameEn?: string; roleNameAr?: string; branchId: string | null; branchCode?: string | null; maxAmount: number | null }
export interface PurchSettings { policy: PurchPolicy; limits: ApprovalLimit[]; roles: { id: string; code: string; nameEn: string; nameAr: string }[] }
export interface BranchSupplier { id: string; number: string; nameEn: string; nameAr: string; status: string; approved: boolean }

export interface OrderRow {
  id: string; number: string; status: OrderStatus; orderDate: string; expectedDate: string | null; grossTotal: number; branchCode: string;
  supplierId: string; supplierNameEn: string; supplierNameAr: string; lines: number; deliverTo: string | null;
}
export interface OrderLineInput { variantId: string; qty: number; unitPrice: number; taxCodeId?: string | null; deliverBranchId?: string | null; repairJobId?: string | null }
export interface OrderInput { id?: string; branchId: string; supplierId: string; lines: OrderLineInput[]; orderDate?: string | null; expectedDate?: string | null; note?: string | null; version?: number | null }
export interface OrderLine {
  id: string; lineNo: number; variantId: string; sku: string; description: string; tracking: string; productType: string; deliverBranchId: string; deliverBranchCode: string;
  qty: number; unitPrice: number; taxCodeId: string; taxRate: number; netAmount: number; taxAmount: number; repairJobId: string | null; repairJobNumber: string | null;
  received: number; invoiced: number;
}
export interface OrderDetail {
  order: {
    id: string; number: string; status: OrderStatus; orderDate: string; expectedDate: string | null; netTotal: number; taxTotal: number; grossTotal: number; note: string | null;
    version: number; branchId: string; branchCode: string; branchNameEn: string; branchNameAr: string; supplierId: string; supplierNumber: string; supplierNameEn: string;
    supplierNameAr: string; supplierStatus: string; createdBy: string | null; approvedBy: string | null; approvedAt: string | null; sentAt: string | null; closedAt: string | null;
    closeReason: string | null; createdAt: string;
  };
  lines: OrderLine[];
  receipts: { id: string; number: string; receiptDate: string; branchCode: string; goodsValue: number; chargesValue: number }[];
  approval: { canApprove: boolean; limit: number | null };
}
export interface PriceHistory {
  last: { unitPrice: number; date: string; supplierNameEn: string; supplierNameAr: string; branchCode: string; number: string } | null;
  average: number | null;
  bySupplier: { supplierId: string; supplierNameEn: string; supplierNameAr: string; unitPrice: number; date: string }[];
}

export interface UnitScan { imei1?: string | null; imei2?: string | null; serial?: string | null; condition?: string | null; sellingPrice?: number | null; entry?: 'scan' | 'manual' }
export interface ReceiptLineInput { qty: number; poLineId?: string | null; variantId?: string | null; unitPrice?: number | null; taxCodeId?: string | null; units?: UnitScan[] | null; repairJobId?: string | null }
export interface ChargeInput { kind: 'freight' | 'customs' | 'insurance' | 'other'; description: string; amount: number; basis: 'value' | 'qty'; supplierId?: string | null }
export interface ReceiptInput {
  id: string; branchId: string; supplierId: string; lines: ReceiptLineInput[]; poId?: string | null; charges?: ChargeInput[]; warehouseId?: string | null; receiptDate?: string | null;
  deliveryNote?: string | null; note?: string | null;
}
export interface ReceiptRow {
  id: string; number: string; receiptDate: string; branchCode: string; supplierNameEn: string; supplierNameAr: string; poNumber: string | null; goodsValue: number;
  chargesValue: number; deliveryNote: string | null; openValue: number;
}
export interface ReceiptDetail {
  receipt: {
    id: string; number: string; receiptDate: string; branchId: string; branchCode: string; supplierId: string; supplierNameEn: string; supplierNameAr: string; poId: string | null;
    poNumber: string | null; deliveryNote: string | null; note: string | null; goodsValue: number; chargesValue: number; receivedBy: string | null; createdAt: string; journalNumber: string | null;
  };
  lines: {
    id: string; lineNo: number; variantId: string; sku: string; description: string; productType: string; qty: number; unitPrice: number; goodsValue: number; chargeShare: number;
    stockValue: number; poLineId: string | null; invoiced: number; returned: number; openValue: number; units: { id: string; imei: string | null; cost: number; status: string }[];
  }[];
  charges: { id: string; lineNo: number; kind: string; description: string; amount: number; basis: string; supplierId: string; supplierNameEn: string; openValue: number }[];
}

export interface InvoiceLineInput {
  kind: InvoiceLineKind; qty: number; unitPrice: number; receiptLineId?: string | null; receiptChargeId?: string | null; outsideType?: 'refurb_cost' | 'repair_line' | null;
  outsideId?: string | null; accountId?: string | null; expenseBranchId?: string | null; description?: string | null; taxCodeId?: string | null;
}
export interface InvoiceInput {
  id: string; branchId: string; supplierId: string; supplierInvoiceNo: string; invoiceDate: string; lines: InvoiceLineInput[]; dueDate?: string | null; note?: string | null; version?: number | null;
}
export interface InvoiceRow {
  id: string; number: string; status: InvoiceStatus; supplierInvoiceNo: string; invoiceDate: string; dueDate: string; branchCode: string; supplierNameEn: string; supplierNameAr: string;
  grossTotal: number; varianceTotal: number; paid: number;
}
export interface InvoiceLine {
  id: string; lineNo: number; kind: InvoiceLineKind; description: string; qty: number; unitPrice: number; netAmount: number; taxCodeId: string; taxRate: number; taxAmount: number;
  expectedAmount: number; orderPrice: number | null; matchStatus: MatchStatus; variance: number; varianceInventory: number; variancePpv: number; receiptLineId: string | null;
  receiptChargeId: string | null; outsideType: string | null; outsideId: string | null; accountId: string | null; accountCode: string | null; expenseBranchId: string | null;
  receiptNumber: string | null; receiptPrice: number | null; branchCode: string | null;
}
export interface InvoiceDetail {
  invoice: {
    id: string; number: string; status: InvoiceStatus; supplierInvoiceNo: string; invoiceDate: string; dueDate: string; branchId: string; branchCode: string; supplierId: string;
    supplierNumber: string; supplierNameEn: string; supplierNameAr: string; netTotal: number; taxTotal: number; grossTotal: number; varianceTotal: number; note: string | null;
    version: number; overrideReason: string | null; overrideBy: string | null; postedAt: string | null; postedBy: string | null; journalNumber: string | null; journalId: string | null; paid: number;
  };
  lines: InvoiceLine[];
}
export interface OpenForSupplier {
  receiptLines: { id: string; receiptId: string; receiptNumber: string; receiptDate: string; branchCode: string; description: string; qty: number; openQty: number; unitPrice: number;
    orderPrice: number | null; openValue: number; taxCodeId: string; tracking: string }[];
  charges: { id: string; receiptNumber: string; receiptDate: string; branchCode: string; kind: string; description: string; amount: number; openValue: number }[];
  outsideWork: OutsideWork[];
}
export interface OutsideWork { outsideType: 'refurb_cost' | 'repair_line'; outsideId: string; branchCode: string; vendor: string | null; openValue: number; date: string; reference: string | null }

export interface SupplierReturnInput { id: string; branchId: string; supplierId: string; reason: string; lines: { receiptLineId: string; qty: number; deviceUnitIds?: string[] | null }[]; returnDate?: string | null }
export interface ReturnRow { id: string; number: string; returnDate: string; branchCode: string; supplierNameEn: string; supplierNameAr: string; reason: string; netTotal: number; taxTotal: number; stockValue: number }
export interface ReturnDetail {
  supplierReturn: { id: string; number: string; returnDate: string; reason: string; netTotal: number; taxTotal: number; stockValue: number; branchCode: string; supplierId: string;
    supplierNameEn: string; supplierNameAr: string; journalNumber: string | null; createdAt: string };
  lines: { lineNo: number; qty: number; stockValue: number; invoiced: boolean; netAmount: number; taxAmount: number; description: string; receiptNumber: string; imei: string | null }[];
}
export interface SupplierOpenItem { id: string; number: string; kind: string; docDate: string; dueDate: string; branchId: string; branchCode: string; amount: number; open: number; supplierInvoiceNo: string | null }
export interface SupplierPaymentInput {
  id: string; branchId: string; supplierId: string; method: PayMethod; amount: number; reference?: string | null; paymentDate?: string | null; note?: string | null;
  allocations?: { entryId: string; amount: number }[] | null;
}
export interface PaymentRow { id: string; number: string; paymentDate: string; branchCode: string; supplierNameEn: string; supplierNameAr: string; method: PayMethod; reference: string | null; amount: number }
export interface PaymentDetail {
  payment: { id: string; number: string; paymentDate: string; method: PayMethod; reference: string | null; amount: number; note: string | null; branchCode: string; supplierId: string;
    supplierNameEn: string; supplierNameAr: string; journalNumber: string | null; createdAt: string };
  allocations: { number: string; kind: string; branchCode: string; amount: number; dueDate: string | null }[];
}

// ------------------------------------------------------------------ accounting (17)
export type AccountKind = 'asset' | 'liability' | 'equity' | 'revenue' | 'expense';
export type CashFlowClass = 'cash' | 'operating' | 'investing' | 'financing' | 'none';
export type PeriodStatus = 'open' | 'soft_closed' | 'closed';
export interface ChartAccount {
  id: string; code: string; nameEn: string; nameAr: string; type: AccountKind; parentId: string | null; parentCode: string | null; isPostable: boolean; isControl: boolean;
  status: 'active' | 'inactive'; cashFlowClass: CashFlowClass; version: number; balance: number; used: boolean;
}
export interface AccountInput { code: string; nameEn: string; nameAr: string; type: AccountKind; parentId?: string | null; isPostable?: boolean; isControl?: boolean; cashFlowClass?: CashFlowClass | null }
export interface AccountChange { nameEn: string; nameAr: string; parentId: string | null; isControl: boolean; cashFlowClass: CashFlowClass; status: 'active' | 'inactive'; version: number }
export interface AcctBranch { id: string; code: string; nameEn: string; nameAr: string; isHeadOffice: boolean; branchServerSeenAt: string | null }
export interface PeriodsView {
  fiscalYear: number; years: number[]; branches: AcctBranch[]; yearClosed: boolean;
  periods: { id: string; periodNo: number; startsOn: string; endsOn: string; businessStatus: PeriodStatus; branches: string; journals: number; vatFiled: boolean }[];
}
export interface AcctPolicy { manualJournalApprovalAbove: number; vatPeriodMonths: 1 | 3; vatFirstMonth: number; version: number }
export interface JournalRow { id: string; number: string; journalDate: string; postingKind: string; description: string; branchCode: string | null; sourceDocType: string | null; total: number; isReversal: boolean }
export interface JournalDetail {
  journal: { id: string; number: string; journalDate: string; postingKind: string; description: string; sourceDocType: string | null; sourceDocId: string | null; branchCode: string | null;
    fiscalYear: number; periodNo: number; postedAt: string; postedBy: string | null; reversalOfId: string | null; reversalOfNumber: string | null; reversedById: string | null; reversedByNumber: string | null };
  lines: { lineNo: number; accountCode: string; accountNameEn: string; accountNameAr: string; branchCode: string | null; debit: number; credit: number; partyType: string | null; partyName: string | null; memo: string | null }[];
}
export type ManualStatus = 'draft' | 'pending_approval' | 'posted' | 'rejected' | 'cancelled';
export interface ManualLineInput { accountId: string; branchId: string; debit: number; credit: number; memo?: string | null }
export interface ManualJournalInput { id: string; branchId: string; journalDate: string; description: string; lines: ManualLineInput[]; intoSoftClosed?: boolean; version?: number | null }
export interface ManualRow { id: string; number: string; journalDate: string; description: string; status: ManualStatus; total: number; branchCode: string; createdBy: string | null; reversed: boolean }
export interface ManualDetail {
  journal: { id: string; number: string; journalDate: string; description: string; status: ManualStatus; total: number; intoSoftClosed: boolean; version: number; branchId: string;
    branchCode: string; createdBy: string | null; createdById: string; approvedBy: string | null; approvedAt: string | null; rejectedReason: string | null; journalId: string | null;
    journalNumber: string | null; reversalJournalId: string | null; reversalNumber: string | null; reversedReason: string | null; createdAt: string; approvalAbove: number };
  lines: { lineNo: number; accountId: string; accountCode: string; accountNameEn: string; accountNameAr: string; isControl: boolean; branchId: string; branchCode: string; debit: number; credit: number; memo: string | null }[];
}
export interface BranchAsOf { branchId: string; code: string; nameEn: string; nameAr: string; asOf: string; onThisServer: boolean }
export interface TrialBalance {
  from: string; to: string; branchId: string | null; earlierResult: number; balanced: boolean; asOf: BranchAsOf[];
  rows: { id: string; code: string; nameEn: string; nameAr: string; type: AccountKind; isControl: boolean; opening: number; debit: number; credit: number; closing: number }[];
  totals: { openingDebit: number; openingCredit: number; debit: number; credit: number; closingDebit: number; closingCredit: number };
}
export interface LedgerView {
  account: { id: string; code: string; nameEn: string; nameAr: string; type: AccountKind }; from: string; to: string; opening: number; closing: number;
  lines: { journalId: string; number: string; date: string; description: string; postingKind: string; sourceDocType: string | null; sourceDocId: string | null; branchCode: string | null;
    debit: number; credit: number; memo: string | null; partyName: string | null; balance: number }[];
}
export interface StatementRow { code: string; nameEn: string; nameAr: string; amount: number }
export interface ProfitAndLoss {
  from: string; to: string; branchId: string | null; revenue: StatementRow[]; costOfSales: StatementRow[]; expenses: StatementRow[]; totalRevenue: number; totalCostOfSales: number;
  grossProfit: number; totalExpenses: number; netProfit: number; asOf: BranchAsOf[];
}
export interface BalanceSheet {
  asOf: string; branchId: string | null; assets: StatementRow[]; liabilities: StatementRow[]; equity: StatementRow[]; yearResult: number; earlierResult: number;
  elimination: { dueFromBranches: number; dueToBranches: number; eliminated: number; difference: number };
  totalAssets: number; totalLiabilities: number; totalEquity: number; balanced: boolean; branches: BranchAsOf[];
}
export interface CashFlow {
  from: string; to: string; branchId: string | null; netProfit: number; operatingChanges: StatementRow[]; operating: number; investingItems: StatementRow[]; investing: number;
  financingItems: StatementRow[]; financing: number; netChange: number; openingCash: number; closingCash: number; cashMovement: number; difference: number; asOf: BranchAsOf[];
}
export interface VatFigure { value: number; vat: number }
export interface VatBox { id?: string; code: string; sort: number; nameEn: string; nameAr: string; source: 'output' | 'input' | 'manual' | 'formula'; needsReview: boolean; reviewedBy?: string | null; reviewedAt?: string | null; formula?: string | null }
export interface VatReturnRow { id: string; number: string; periodFrom: string; periodTo: string; status: 'draft' | 'filed'; outputVat: number; inputVat: number; netVat: number; filedAt: string | null; paidAt: string | null }
export interface VatReturnDetail {
  vatReturn: { id: string; number: string; periodFrom: string; periodTo: string; status: 'draft' | 'filed'; boxes: Record<string, VatFigure>; adjustments: Record<string, VatFigure>;
    outputVat: number; inputVat: number; netVat: number; refundRequested: boolean; filedAt: string | null; filedBy: string | null; filingReference: string | null;
    settlementNumber: string | null; paymentNumber: string | null; paidAt: string | null; version: number };
  boxes: VatBox[];
  check: { branchCode: string; output: number; outputAccount: number; input: number; inputAccount: number }[];
  agrees: boolean;
  bySource: { direction: 'output' | 'input'; taxKind: string; net: number; vat: number }[];
}
export interface CardSettlements {
  clearing: { branchId: string; branchCode: string; nameEn: string; nameAr: string; balance: number }[];
  settlements: { id: string; number: string; settlementDate: string; branchCode: string; grossAmount: number; fees: number; netAmount: number; reference: string }[];
}
export type LedgerName = 'customers' | 'suppliers' | 'inventory' | 'grni' | 'vat_output' | 'vat_input' | 'store_credit' | 'deposits' | 'repair_wip' | 'interbranch' | 'trial_balance';
export interface Reconciliation {
  runId: string | null; clean?: boolean;
  rows: { ledger: LedgerName; branchId: string | null; branchCode: string | null; subledger: number; control: number; difference: number; asOf: string; checkedAt: string }[];
}
export interface ExpenseInput {
  id: string; branchId: string; accountId: string; description: string; netAmount: number; taxCodeId: string; paidBy: PayMethod; expenseDate?: string | null; supplierId?: string | null;
  payee?: string | null; reference?: string | null;
}
export interface ExpenseRow {
  id: string; number: string; expenseDate: string; branchCode: string; accountCode: string; accountNameEn: string; accountNameAr: string; description: string; payee: string | null;
  netAmount: number; taxAmount: number; grossAmount: number; paidBy: PayMethod;
}
export interface ExpenseAccount { id: string; code: string; nameEn: string; nameAr: string; type: AccountKind }

type Query = Record<string, string | number | boolean | null | undefined>;

export interface PurchasingApi {
  purchSettings(): Promise<PurchSettings>;
  updatePurchPolicy(p: PurchPolicy): Promise<void>;
  updateApprovalLimits(limits: { roleId: string; branchId: string | null; maxAmount: number | null }[]): Promise<void>;
  branchSuppliers(branchId: string): Promise<BranchSupplier[]>;
  setBranchSuppliers(branchId: string, supplierIds: string[]): Promise<void>;
  orders(f?: { status?: string; supplierId?: string; branchId?: string; q?: string }): Promise<OrderRow[]>;
  order(id: string): Promise<OrderDetail>;
  createOrder(o: OrderInput): Promise<{ id: string; number: string }>;
  updateOrder(id: string, o: OrderInput): Promise<{ id: string; number: string }>;
  approveOrder(id: string): Promise<OrderDetail>;
  sendOrder(id: string): Promise<OrderDetail>;
  cancelOrder(id: string, reason: string): Promise<OrderDetail>;
  closeOrder(id: string, reason: string): Promise<OrderDetail>;
  orderPdf(id: string): Promise<Blob>;
  priceHistory(variantId: string): Promise<PriceHistory>;
  receipts(f?: { supplierId?: string; branchId?: string; q?: string }): Promise<ReceiptRow[]>;
  goodsReceipt(id: string): Promise<ReceiptDetail>;
  receive(r: ReceiptInput): Promise<ReceiptDetail>;
  openForSupplier(supplierId: string): Promise<OpenForSupplier>;
  supplierOpenItems(supplierId: string): Promise<SupplierOpenItem[]>;
  purchaseInvoices(f?: { status?: string; supplierId?: string; q?: string }): Promise<InvoiceRow[]>;
  purchaseInvoice(id: string): Promise<InvoiceDetail>;
  enterInvoice(i: InvoiceInput): Promise<InvoiceDetail>;
  updateInvoice(id: string, i: InvoiceInput): Promise<InvoiceDetail>;
  postInvoice(id: string, overrideReason?: string | null): Promise<InvoiceDetail>;
  cancelInvoice(id: string): Promise<void>;
  supplierReturns(f?: { supplierId?: string }): Promise<ReturnRow[]>;
  supplierReturn(id: string): Promise<ReturnDetail>;
  returnToSupplier(r: SupplierReturnInput): Promise<ReturnDetail>;
  supplierPayments(f?: { supplierId?: string }): Promise<PaymentRow[]>;
  supplierPayment(id: string): Promise<PaymentDetail>;
  paySupplier(p: SupplierPaymentInput): Promise<PaymentDetail>;
  chart(): Promise<ChartAccount[]>;
  createAccount(a: AccountInput): Promise<{ id: string; code: string }>;
  updateAccount(id: string, a: AccountChange): Promise<void>;
  acctPolicy(): Promise<AcctPolicy>;
  updateAcctPolicy(p: AcctPolicy): Promise<void>;
  periods(fiscalYear?: number): Promise<PeriodsView>;
  setPeriodStatus(id: string, status: PeriodStatus, reason: string, branchId?: string | null): Promise<{ changed: number }>;
  yearEnd(fiscalYear: number): Promise<{ year: number; profit: number; journalId: string | null }>;
  journals(f?: { from?: string; to?: string; branchId?: string; kind?: string; q?: string }): Promise<JournalRow[]>;
  journal(id: string): Promise<JournalDetail>;
  manualJournals(f?: { status?: string }): Promise<ManualRow[]>;
  manualJournal(id: string): Promise<ManualDetail>;
  createManualJournal(j: ManualJournalInput): Promise<ManualDetail>;
  updateManualJournal(id: string, j: ManualJournalInput): Promise<ManualDetail>;
  submitManualJournal(id: string): Promise<ManualDetail>;
  approveManualJournal(id: string): Promise<ManualDetail>;
  rejectManualJournal(id: string, reason: string): Promise<ManualDetail>;
  reverseManualJournal(id: string, reason: string, date?: string | null): Promise<ManualDetail>;
  trialBalance(f: { from?: string; to?: string; branchId?: string }): Promise<TrialBalance>;
  generalLedger(accountId: string, f: { from?: string; to?: string; branchId?: string }): Promise<LedgerView>;
  profitAndLoss(f: { from?: string; to?: string; branchId?: string }): Promise<ProfitAndLoss>;
  balanceSheet(f: { asOf?: string; branchId?: string }): Promise<BalanceSheet>;
  cashFlow(f: { from?: string; to?: string; branchId?: string }): Promise<CashFlow>;
  vatPeriods(): Promise<{ months: number; periods: { from: string; to: string }[] }>;
  vatBoxes(): Promise<VatBox[]>;
  reviewVatBoxes(): Promise<void>;
  vatReturns(): Promise<VatReturnRow[]>;
  prepareVatReturn(periodFrom: string, periodTo: string): Promise<VatReturnDetail>;
  vatReturn(id: string): Promise<VatReturnDetail>;
  adjustVatReturn(id: string, boxes: Record<string, VatFigure>, refundRequested: boolean, version: number): Promise<VatReturnDetail>;
  fileVatReturn(id: string, filingReference?: string | null): Promise<VatReturnDetail>;
  payVatReturn(id: string, reference: string, date?: string | null): Promise<VatReturnDetail>;
  cardSettlements(): Promise<CardSettlements>;
  settleCards(s: { id: string; branchId: string; grossAmount: number; fees: number; reference: string; settlementDate?: string | null }): Promise<{ id: string; number: string }>;
  reconciliation(): Promise<Reconciliation>;
  runReconciliation(): Promise<Reconciliation>;
  expenses(f?: { from?: string; to?: string; branchId?: string }): Promise<ExpenseRow[]>;
  expenseAccounts(): Promise<ExpenseAccount[]>;
  recordExpense(e: ExpenseInput): Promise<ExpenseRow & { journalNumber: string | null }>;
}

interface Http {
  get: (path: string) => Promise<any>; // eslint-disable-line @typescript-eslint/no-explicit-any
  post: (path: string, body?: unknown) => Promise<any>; // eslint-disable-line @typescript-eslint/no-explicit-any
  put: (path: string, body?: unknown) => Promise<any>; // eslint-disable-line @typescript-eslint/no-explicit-any
  blob: (path: string) => Promise<Blob>;
}

const qs = (f: Query) => {
  const p = Object.entries(f).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`);
  return p.length ? `?${p.join('&')}` : '';
};

export function purchasingMethods(h: Http): PurchasingApi {
  const { get, post, put, blob } = h;
  const p = '/api/v1/purchasing';
  const a = '/api/v1/accounting';
  return {
    purchSettings: () => get(`${p}/policy`),
    updatePurchPolicy: (x) => put(`${p}/policy`, x),
    updateApprovalLimits: (limits) => put(`${p}/approval-limits`, limits),
    branchSuppliers: (b) => get(`${p}/branches/${b}/suppliers`),
    setBranchSuppliers: (b, supplierIds) => put(`${p}/branches/${b}/suppliers`, { supplierIds }),
    orders: (f) => get(`${p}/orders${qs(f ?? {})}`),
    order: (id) => get(`${p}/orders/${id}`),
    createOrder: (o) => post(`${p}/orders`, o),
    updateOrder: (id, o) => put(`${p}/orders/${id}`, o),
    approveOrder: (id) => post(`${p}/orders/${id}/approve`),
    sendOrder: (id) => post(`${p}/orders/${id}/send`),
    cancelOrder: (id, reason) => post(`${p}/orders/${id}/cancel`, { reason }),
    closeOrder: (id, reason) => post(`${p}/orders/${id}/close`, { reason }),
    orderPdf: (id) => blob(`${p}/orders/${id}/pdf`),
    priceHistory: (v) => get(`${p}/price-history/${v}`),
    receipts: (f) => get(`${p}/receipts${qs(f ?? {})}`),
    goodsReceipt: (id) => get(`${p}/receipts/${id}`),
    receive: (r) => post(`${p}/receipts`, r),
    openForSupplier: (s) => get(`${p}/suppliers/${s}/open`),
    supplierOpenItems: (s) => get(`${p}/suppliers/${s}/open-items`),
    purchaseInvoices: (f) => get(`${p}/invoices${qs(f ?? {})}`),
    purchaseInvoice: (id) => get(`${p}/invoices/${id}`),
    enterInvoice: (i) => post(`${p}/invoices`, i),
    updateInvoice: (id, i) => put(`${p}/invoices/${id}`, i),
    postInvoice: (id, overrideReason) => post(`${p}/invoices/${id}/post`, { overrideReason: overrideReason ?? null }),
    cancelInvoice: (id) => post(`${p}/invoices/${id}/cancel`),
    supplierReturns: (f) => get(`${p}/returns${qs(f ?? {})}`),
    supplierReturn: (id) => get(`${p}/returns/${id}`),
    returnToSupplier: (r) => post(`${p}/returns`, r),
    supplierPayments: (f) => get(`${p}/payments${qs(f ?? {})}`),
    supplierPayment: (id) => get(`${p}/payments/${id}`),
    paySupplier: (x) => post(`${p}/payments`, x),
    chart: () => get(`${a}/chart`),
    createAccount: (x) => post(`${a}/accounts`, x),
    updateAccount: (id, x) => put(`${a}/accounts/${id}`, x),
    acctPolicy: () => get(`${a}/policy`),
    updateAcctPolicy: (x) => put(`${a}/policy`, x),
    periods: (y) => get(`${a}/periods${qs({ fiscalYear: y })}`),
    setPeriodStatus: (id, status, reason, branchId) => post(`${a}/periods/${id}/status`, { status, reason, branchId: branchId ?? null }),
    yearEnd: (fiscalYear) => post(`${a}/year-end`, { fiscalYear }),
    journals: (f) => get(`${a}/journals${qs(f ?? {})}`),
    journal: (id) => get(`${a}/journals/${id}`),
    manualJournals: (f) => get(`${a}/manual-journals${qs(f ?? {})}`),
    manualJournal: (id) => get(`${a}/manual-journals/${id}`),
    createManualJournal: (j) => post(`${a}/manual-journals`, j),
    updateManualJournal: (id, j) => put(`${a}/manual-journals/${id}`, j),
    submitManualJournal: (id) => post(`${a}/manual-journals/${id}/submit`),
    approveManualJournal: (id) => post(`${a}/manual-journals/${id}/approve`),
    rejectManualJournal: (id, reason) => post(`${a}/manual-journals/${id}/reject`, { reason }),
    reverseManualJournal: (id, reason, date) => post(`${a}/manual-journals/${id}/reverse`, { reason, date: date ?? null }),
    trialBalance: (f) => get(`${a}/reports/trial-balance${qs(f)}`),
    generalLedger: (id, f) => get(`${a}/reports/ledger/${id}${qs(f)}`),
    profitAndLoss: (f) => get(`${a}/reports/profit-and-loss${qs(f)}`),
    balanceSheet: (f) => get(`${a}/reports/balance-sheet${qs(f)}`),
    cashFlow: (f) => get(`${a}/reports/cash-flow${qs(f)}`),
    vatPeriods: () => get(`${a}/vat/periods`),
    vatBoxes: () => get(`${a}/vat/boxes`),
    reviewVatBoxes: () => post(`${a}/vat/boxes/review`),
    vatReturns: () => get(`${a}/vat/returns`),
    prepareVatReturn: (periodFrom, periodTo) => post(`${a}/vat/returns`, { periodFrom, periodTo }),
    vatReturn: (id) => get(`${a}/vat/returns/${id}`),
    adjustVatReturn: (id, boxes, refundRequested, version) => put(`${a}/vat/returns/${id}/adjustments`, { boxes, refundRequested, version }),
    fileVatReturn: (id, filingReference) => post(`${a}/vat/returns/${id}/file`, { filingReference: filingReference ?? null }),
    payVatReturn: (id, reference, date) => post(`${a}/vat/returns/${id}/pay`, { reference, date: date ?? null }),
    cardSettlements: () => get(`${a}/card-settlements`),
    settleCards: (s) => post(`${a}/card-settlements`, s),
    reconciliation: () => get(`${a}/reconciliation`),
    runReconciliation: () => post(`${a}/reconciliation/run`),
    expenses: (f) => get(`/api/v1/expenses${qs(f ?? {})}`),
    expenseAccounts: () => get('/api/v1/expenses/accounts'),
    recordExpense: (e) => post('/api/v1/expenses', e),
  };
}
