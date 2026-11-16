// Phase 4 API: catalog, customers, inventory, POS (shifts, sales, returns, approvals), receipts, printers, emergency.
export interface EmergencyRecordRow {
  id: string; terminalCode: string; branchCode: string; seq: number; kind: string; recordedAt: string; receivedAt: string; result: string; detail: string | null;
  number?: string | null; gross?: number; deviceLines?: string | null; settledOutcome?: 'applied' | 'dismissed' | null; settledAt?: string | null; settledBy?: string | null;
  settledNote?: string | null; settledSaleNumber?: string | null;
}
// Amounts arrive as JSON numbers with at most three decimals; the POS package turns them into exact baisa.
import type { ReceiptModel } from '@kite/pos';

export type ProductType = 'device' | 'accessory' | 'spare_part' | 'service' | 'consumable';
export interface Brand { id: string; nameEn: string; nameAr: string; status: string; version: number }
export interface Category { id: string; parentId: string | null; nameEn: string; nameAr: string; status: string; version: number }
export interface ProductRow {
  id: string; brandId: string | null; brandName: string | null; categoryId: string | null; categoryName: string | null; nameEn: string; nameAr: string;
  productType: ProductType; tracking: 'none' | 'imei' | 'serial'; taxCodeId: string; taxCode: string; defaultWarrantyDays: number; status: 'active' | 'inactive'; version: number;
}
export interface VariantRow {
  id: string; productId: string; sku: string; colourEn: string | null; colourAr: string | null; storageGb: number | null; ramGb: number | null;
  status: 'active' | 'inactive'; version: number; price: number | null; minPrice: number | null; priceFrom: string | null;
}
export interface ProductView { product: ProductRow; variants: { variant: VariantRow; barcodes: { id: string; code: string }[] }[] }
export interface VariantInput { sku: string; colourEn?: string | null; colourAr?: string | null; storageGb?: number | null; ramGb?: number | null; barcodes?: string[]; price?: number | null; minPrice?: number | null }
export interface ProductInput {
  nameEn: string; nameAr: string; productType: ProductType; taxCodeId: string; tracking?: 'none' | 'imei' | 'serial'; brandId?: string | null; categoryId?: string | null;
  defaultWarrantyDays?: number; variants?: VariantInput[]; status?: 'active' | 'inactive'; version?: number;
}
export interface PriceRow { id: string; priceList: string; price: number; minPrice: number | null; effectiveFrom: string; effectiveTo: string | null }
export interface PosItem {
  variantId: string; sku: string; productType: ProductType; tracking: 'none' | 'imei' | 'serial'; descriptionEn: string; descriptionAr: string; taxCodeId: string;
  taxKind: string; taxRate: number; listPrice: number | null; minPrice: number | null; warrantyDays: number; active: boolean; stock: number; barcode: string | null;
  /** Only on a device unit bought under the profit margin scheme (Phase 7). */
  marginCost?: number | null;
}
export interface Customer {
  id: string; number: string; kind: 'individual' | 'company'; nameEn: string; nameAr: string | null; phoneE164: string | null; email: string | null;
  vatNumber: string | null; crNumber: string | null; addressEn: string | null; addressAr: string | null; status: 'active' | 'inactive'; version: number;
}
export interface CustomerInput {
  nameEn: string; nameAr?: string | null; kind?: 'individual' | 'company'; phone?: string | null; email?: string | null; vatNumber?: string | null; crNumber?: string | null;
  addressEn?: string | null; addressAr?: string | null; status?: 'active' | 'inactive'; version?: number;
}
export interface Balance {
  variantId: string; sku: string; descriptionEn: string; descriptionAr: string; productType: ProductType; branchId: string; branchCode: string; warehouseId: string;
  warehouseCode: string; qty: number; value: number; avgCost: number; reorderLevel: number | null; lastMovementAt: string | null;
}
export interface Movement {
  id: string; movementAt: string; movementType: string; branchCode: string; warehouseCode: string; qty: number; unitCost: number | null; value: number; qtyAfter: number;
  valueAfter: number; sourceDocType: string; sourceDocId: string; sourceNumber: string | null; reason: string | null;
}
export type ReasonCode = 'opening' | 'damaged' | 'lost' | 'found' | 'expired' | 'correction' | 'other';
export interface AdjustmentInput { branchId: string; kind: 'opening' | 'adjustment'; note?: string; date?: string; warehouseId?: string; lines: { variantId: string; qty: number; unitCost?: number | null; reasonCode: ReasonCode; units?: import('./devices').UnitInput[] }[] }
export interface AdjustmentRow { id: string; number: string; kind: string; date: string; branchCode: string; note: string | null; postedAt: string; postedBy: string; lines: number; value: number }
export interface CountRow { id: string; number: string; status: 'open' | 'posted' | 'cancelled'; branchCode: string; warehouseCode: string; startedAt: string; postedAt: string | null; lines: number }
export interface CountDetail { id: string; number: string; status: string; branchId: string; lines: { variantId: string; sku: string; descriptionEn: string; countedQty: number; systemQty: number; difference: number }[] }
export interface StockException { id: string; kind: string; status: string; branchCode: string; sku: string; descriptionEn: string; qtyAfter: number; sourceDocType: string; sourceNumber: string | null; createdAt: string; resolutionNote: string | null }
export interface ConsistencyCheck { ok: boolean; balanceMismatches: unknown[]; valuationMismatches: unknown[]; unbalancedJournals: unknown[]; salesMismatches: unknown[]; deviceMismatches?: unknown[] }

export interface PosPolicy {
  id: string; branchId: string | null; negativeStock: 'block' | 'allow'; returnWindowDays: number; cashRefundCap: number; varianceThreshold: number; blindClose: boolean;
  roundingLevel: 'line' | 'invoice'; cashRounding: number; denominations: number[]; receiptFooterEn: string | null; receiptFooterAr: string | null; version: number;
}
export interface Shift {
  id: string; branchId: string; terminalId: string; cashierId: string; number: string; status: 'open' | 'closed'; isEmergency: boolean; openedAt: string; openingFloat: number;
  closedAt: string | null; expectedCash: number | null; countedCash: number | null; variance: number | null; varianceReason: string | null; varianceAcceptedBy: string | null; version: number;
}
export interface ShiftFigures {
  salesCount: number; salesGross: number; salesNet: number; salesTax: number; salesDiscount: number; creditNotesCount: number; creditNotesGross: number; cashRounding: number;
  cashPayments: number; cardPayments: number; transferPayments: number; payIns: number; payOuts: number; drops: number; noSales: number; openingFloat: number; expectedCash: number;
  cardByTerminal: { cardTerminal: string; total: number }[];
  /** Phase 7: trade-in credit and store credit taken as payments, and trade-ins paid out of the drawer. */
  tradeInCredit?: number; storeCreditPayments?: number; tradeInPayouts?: number;
}
export interface ShiftReport { shift: Shift; report: 'X' | 'Z'; figures: ShiftFigures; cardSettlements: { cardTerminal: string; batchTotal: number; recordedTotal: number; difference: number; reason: string | null }[]; cashEvents: { kind: string; amount: number; reason: string; createdAt: string }[] }
export interface CartLineInput { variantId: string; qty: number; unitPrice?: number | null; discount?: number | null; approvalId?: string | null; deviceUnitId?: string | null; damagedApprovalId?: string | null }
export interface TenderInput { method: 'cash' | 'card' | 'bank_transfer' | 'trade_in' | 'store_credit'; amount: number; tendered?: number | null; cardTerminal?: string | null; approvalCode?: string | null; reference?: string | null; tradeInId?: string | null }
export interface SaleInput { lines: CartLineInput[]; payments?: TenderInput[]; docType?: 'simplified_invoice' | 'tax_invoice'; customerId?: string | null; expectedTotal?: number; note?: string; approvalId?: string | null }
export interface Quote {
  lines: { lineNo: number; variantId: string; sku: string; descriptionEn: string; descriptionAr: string; qty: number; listPrice: number; unitPrice: number; discount: number; net: number; tax: number; gross: number; taxRate: number; needsPriceOverride: boolean; needsDiscountApproval: boolean }[];
  totals: { net: number; discount: number; tax: number; gross: number }; cashDue: number;
}
export interface SaleSummary { id: string; number: string; docType: string; gross: number; cashRounding: number; paid: number; change: number; stockExceptions: number }
export interface SaleHeader {
  id: string; branchId: string; number: string; docType: string; creditReason: string | null; originalSaleId: string | null; originalNumber: string | null; saleDate: string; completedAt: string;
  netTotal: number; discountTotal: number; taxTotal: number; grossTotal: number; cashRounding: number; isEmergency: boolean; note: string | null; customerId: string | null;
  customerName: string | null; customerVat: string | null; cashierName: string; terminalCode: string; shiftId: string; qrPayload: string; einvoiceUuid: string;
}
export interface SaleLine {
  id: string; variantId: string; productType: ProductType; descriptionEn: string; descriptionAr: string; qty: number; unitPrice: number; listPrice: number; discount: number;
  net: number; taxRate: number; tax: number; gross: number; returnedQty: number; returnable: number;
  returnedNet?: number; returnedTax?: number; returnedGross?: number; returnedDiscount?: number; deviceUnitId?: string | null; identifier?: string | null;
  /** Sold under the profit margin scheme: the purchase price its VAT was worked out against (Phase 7). */
  marginPurchasePrice?: number | null;
}
export interface SaleDetail { sale: SaleHeader; lines: SaleLine[]; payments: { method: string; amount: number; tendered: number | null; changeGiven: number | null; cardTerminal: string | null }[]; creditNotes: { id: string; number: string; creditReason: string; grossTotal: number }[] }
export interface SaleListRow { id: string; number: string; docType: string; creditReason: string | null; completedAt: string; grossTotal: number; cashRounding: number; isEmergency: boolean; customerName: string | null; cashierName: string; terminalCode: string; originalNumber: string | null }
export interface ReturnInput { id: string; lines: { lineId: string; qty: number; restock: boolean; code?: string | null }[]; refunds: TenderInput[]; reason: string; approvalId?: string | null }
export interface ParkedCart { id: string; label: string | null; parkedAt: string; parkedBy: string; terminalCode: string; cart: string }
export type ApprovalAction = 'POS.DISCOUNT.ABOVE_LIMIT' | 'POS.PRICE.OVERRIDE' | 'POS.SALE.VOID' | 'POS.REFUND' | 'POS.CASH.PAY_OUT' | 'POS.DRAWER.NO_SALE' | 'POS.SHIFT.ACCEPT_VARIANCE' | 'DEVICE.SELL_DAMAGED'
  | 'TRADEIN.APPROVE' | 'TRADEIN.STORE_CREDIT' | 'TRADEIN.PAYOUT';
export interface ApprovalRow { id: string; branchId: string; branchCode: string; actionCode: ApprovalAction; method: string; status: string; reason: string | null; context: string; requesterName: string; terminalCode: string | null; createdAt: string; expiresAt: string }
export interface Printer { id: string; branchId: string; name: string; host: string; port: number; paperWidthMm: number; hasDrawer: boolean; status: 'active' | 'inactive'; lastResult: string | null; lastUsedAt: string | null; version: number }
export interface PrinterInput { branchId: string; name: string; host: string; port?: number; paperWidthMm?: 58 | 80; hasDrawer?: boolean; status?: 'active' | 'inactive'; version?: number }
export interface TerminalCredentials { terminalId: string; deviceKey: string }
export interface EmergencyRecord { id: string; seq: number; kind: 'shift_open' | 'sale' | 'cash_event' | 'shift_close'; recordedAt: string; payload: unknown; monotonicMs?: number }
export interface EmergencyResult { id: string; seq: number; result: 'applied' | 'conflict'; detail: string | null; duplicate: boolean }

export interface PosApi {
  brands(): Promise<Brand[]>;
  createBrand(b: { nameEn: string; nameAr: string }): Promise<Brand>;
  categories(): Promise<Category[]>;
  createCategory(c: { nameEn: string; nameAr: string; parentId?: string | null }): Promise<Category>;
  products(q?: string, type?: string): Promise<ProductView[]>;
  product(id: string): Promise<ProductView>;
  createProduct(p: ProductInput): Promise<ProductView>;
  updateProduct(id: string, p: ProductInput): Promise<ProductView>;
  addVariant(productId: string, v: VariantInput): Promise<{ id: string }>;
  addBarcode(variantId: string, code: string): Promise<{ id: string; code: string }>;
  removeBarcode(id: string): Promise<void>;
  setPrice(variantId: string, p: { price: number; minPrice?: number | null; effectiveFrom?: string | null }): Promise<void>;
  prices(variantId: string): Promise<PriceRow[]>;
  setBranchVariant(variantId: string, branchId: string, b: { overridePrice: number | null; overrideMinPrice: number | null; reorderLevel: number | null; binLocation: string | null }): Promise<void>;
  posItems(q?: string, branchId?: string, limit?: number): Promise<PosItem[]>;
  posItemByBarcode(code: string, branchId?: string): Promise<PosItem>;
  customers(q?: string): Promise<Customer[]>;
  createCustomer(c: CustomerInput): Promise<Customer>;
  updateCustomer(id: string, c: CustomerInput): Promise<Customer>;
  balances(f: { branchId?: string; q?: string; lowOnly?: boolean }): Promise<Balance[]>;
  movements(variantId: string, branchId?: string): Promise<Movement[]>;
  postAdjustment(a: AdjustmentInput): Promise<{ id: string; number: string; units?: number; centralPending?: number }>;
  adjustments(branchId?: string): Promise<AdjustmentRow[]>;
  startCount(c: { branchId: string; note?: string }): Promise<{ id: string; number: string }>;
  counts(branchId?: string): Promise<CountRow[]>;
  count(id: string): Promise<CountDetail>;
  setCountLines(id: string, lines: { variantId: string; countedQty: number }[]): Promise<void>;
  postCount(id: string): Promise<{ lines: number; changed: number }>;
  cancelCount(id: string): Promise<void>;
  stockExceptions(status?: string): Promise<StockException[]>;
  resolveException(id: string, note: string): Promise<void>;
  inventoryCheck(): Promise<ConsistencyCheck>;
  posPolicy(branchId?: string): Promise<PosPolicy>;
  savePosPolicy(p: Omit<PosPolicy, 'id' | 'version' | 'denominations'> & { denominations?: number[] | null; version?: number | null }): Promise<PosPolicy>;
  openShift(o: { openingFloat?: number; openingCount?: Record<string, number> }): Promise<Shift>;
  currentShift(): Promise<{ shift: Shift | null; figures?: ShiftFigures; blind?: boolean }>;
  shifts(f?: { status?: string; branchId?: string }): Promise<Shift[]>;
  shift(id: string): Promise<ShiftReport>;
  cashEvent(e: { kind: 'pay_in' | 'pay_out' | 'drop' | 'no_sale'; amount: number; reason: string; approvalId?: string | null }): Promise<{ id: string }>;
  closeShift(c: { closingCount?: Record<string, number>; countedCash?: number; cardSettlements?: { cardTerminal: string; batchTotal: number; reason?: string | null }[]; varianceReason?: string | null; approvalId?: string | null }): Promise<{ id: string; number: string; figures: ShiftFigures; counted: number; variance: number }>;
  quote(s: SaleInput): Promise<Quote>;
  completeSale(id: string, s: SaleInput): Promise<SaleSummary>;
  returnSale(originalId: string, r: ReturnInput): Promise<SaleSummary>;
  voidSale(originalId: string, v: { id: string; reason: string; approvalId?: string | null }): Promise<SaleSummary>;
  sales(f: { shiftId?: string; branchId?: string; q?: string; date?: string }): Promise<SaleListRow[]>;
  sale(id: string): Promise<SaleDetail>;
  saleByNumber(number: string): Promise<SaleDetail>;
  park(c: { id: string; label?: string | null; cart: unknown; deviceUnitIds?: string[] }): Promise<void>;
  parked(): Promise<ParkedCart[]>;
  recall(id: string): Promise<unknown>;
  receipt(saleId: string, reprint?: boolean): Promise<ReceiptModel>;
  invoicePdf(saleId: string): Promise<Blob>;
  pinApproval(a: { action: ApprovalAction; approverUsername: string; pin: string; reason?: string; context?: unknown }): Promise<{ id: string; status: string; approverName: string; expiresAt: string }>;
  requestApproval(a: { action: ApprovalAction; reason?: string; context?: unknown }): Promise<{ id: string; status: string; expiresAt: string }>;
  approvalStatus(id: string): Promise<{ id: string; actionCode: string; status: string; reason: string | null; expiresAt: string }>;
  approvals(status?: string): Promise<ApprovalRow[]>;
  decideApproval(id: string, decision: 'approve' | 'reject', reason?: string): Promise<void>;
  printers(branchId?: string): Promise<Printer[]>;
  createPrinter(p: PrinterInput): Promise<Printer>;
  updatePrinter(id: string, p: PrinterInput): Promise<Printer>;
  printJob(printerId: string, job: { kind: 'receipt' | 'raw' | 'drawer' | 'test'; data?: string; saleId?: string }): Promise<{ printed: boolean }>;
  setTerminalPrinter(terminalId: string, printerId: string | null): Promise<void>;
  bindDeviceKey(terminalId: string, deviceKey: string): Promise<void>;
  loginAt(username: string, password: string, terminal: TerminalCredentials): Promise<import('./index').Tokens>;
  pinLoginAt(username: string, pin: string, terminal: TerminalCredentials): Promise<import('./index').Tokens>;
  emergencySnapshot(): Promise<unknown>;
  uploadEmergency(records: EmergencyRecord[]): Promise<{ results: EmergencyResult[] }>;
  emergencyRecords(result?: string, devices?: boolean): Promise<EmergencyRecordRow[]>;
  settleEmergency(id: string, s: { action: 'apply' | 'dismiss'; substitutions?: { line: number; deviceUnitId: string }[]; note?: string | null }): Promise<{ recordId: string; outcome: string; saleId: string | null }>;
}

type Http = {
  get: <T>(p: string) => Promise<T>;
  post: <T>(p: string, b?: unknown) => Promise<T>;
  put: <T>(p: string, b: unknown) => Promise<T>;
  del: <T>(p: string) => Promise<T>;
  blob: (p: string) => Promise<Blob>;
  setToken: (t: string) => void;
};

const qs = (o: Record<string, string | number | boolean | undefined | null>) => {
  const p = Object.entries(o).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`);
  return p.length ? `?${p.join('&')}` : '';
};

export function posMethods(h: Http): PosApi {
  const { get, post, put, del } = h;
  return {
    brands: () => get('/api/v1/catalog/brands'),
    createBrand: (b) => post('/api/v1/catalog/brands', b),
    categories: () => get('/api/v1/catalog/categories'),
    createCategory: (c) => post('/api/v1/catalog/categories', c),
    products: (q, type) => get(`/api/v1/catalog/products${qs({ q, type })}`),
    product: (id) => get(`/api/v1/catalog/products/${id}`),
    createProduct: (p) => post('/api/v1/catalog/products', p),
    updateProduct: (id, p) => put(`/api/v1/catalog/products/${id}`, p),
    addVariant: (productId, v) => post(`/api/v1/catalog/products/${productId}/variants`, v),
    addBarcode: (variantId, code) => post(`/api/v1/catalog/variants/${variantId}/barcodes`, { code }),
    removeBarcode: (id) => del(`/api/v1/catalog/barcodes/${id}`),
    setPrice: (variantId, p) => post(`/api/v1/catalog/variants/${variantId}/prices`, p),
    prices: (variantId) => get(`/api/v1/catalog/variants/${variantId}/prices`),
    setBranchVariant: (variantId, branchId, b) => put(`/api/v1/catalog/variants/${variantId}/branches/${branchId}`, b),
    posItems: (q, branchId, limit) => get(`/api/v1/pos/items${qs({ q, branchId, limit })}`),
    posItemByBarcode: (code, branchId) => get(`/api/v1/pos/items/barcode/${encodeURIComponent(code)}${qs({ branchId })}`),
    customers: (q) => get(`/api/v1/customers${qs({ q })}`),
    createCustomer: (c) => post('/api/v1/customers', c),
    updateCustomer: (id, c) => put(`/api/v1/customers/${id}`, c),
    balances: (f) => get(`/api/v1/inventory/balances${qs(f)}`),
    movements: (variantId, branchId) => get(`/api/v1/inventory/movements${qs({ variantId, branchId })}`),
    postAdjustment: (a) => post('/api/v1/inventory/adjustments', a),
    adjustments: (branchId) => get(`/api/v1/inventory/adjustments${qs({ branchId })}`),
    startCount: (c) => post('/api/v1/inventory/counts', c),
    counts: (branchId) => get(`/api/v1/inventory/counts${qs({ branchId })}`),
    count: (id) => get(`/api/v1/inventory/counts/${id}`),
    setCountLines: (id, lines) => put(`/api/v1/inventory/counts/${id}/lines`, lines),
    postCount: (id) => post(`/api/v1/inventory/counts/${id}/post`),
    cancelCount: (id) => post(`/api/v1/inventory/counts/${id}/cancel`),
    stockExceptions: (status) => get(`/api/v1/inventory/exceptions${qs({ status })}`),
    resolveException: (id, note) => post(`/api/v1/inventory/exceptions/${id}/resolve`, { note }),
    inventoryCheck: () => get('/api/v1/inventory/check'),
    posPolicy: (branchId) => get(`/api/v1/pos/policy${qs({ branchId })}`),
    savePosPolicy: (p) => put('/api/v1/pos/policy', p),
    openShift: (o) => post('/api/v1/pos/shifts', o),
    currentShift: () => get('/api/v1/pos/shifts/current'),
    shifts: (f) => get(`/api/v1/pos/shifts${qs(f ?? {})}`),
    shift: (id) => get(`/api/v1/pos/shifts/${id}`),
    cashEvent: (e) => post('/api/v1/pos/shifts/current/cash-events', e),
    closeShift: (c) => post('/api/v1/pos/shifts/current/close', c),
    quote: (s) => post('/api/v1/sales/quote', s),
    completeSale: (id, s) => post(`/api/v1/sales/${id}/complete`, s),
    returnSale: (originalId, r) => post(`/api/v1/sales/${originalId}/return`, r),
    voidSale: (originalId, v) => post(`/api/v1/sales/${originalId}/void`, v),
    sales: (f) => get(`/api/v1/sales${qs(f)}`),
    sale: (id) => get(`/api/v1/sales/${id}`),
    saleByNumber: (number) => get(`/api/v1/sales/by-number/${encodeURIComponent(number)}`),
    park: (c) => post('/api/v1/sales/parked', c),
    parked: () => get('/api/v1/sales/parked'),
    recall: (id) => post(`/api/v1/sales/parked/${id}/recall`),
    receipt: (saleId, reprint) => get(`/api/v1/sales/${saleId}/receipt${qs({ reprint })}`),
    invoicePdf: (saleId) => h.blob(`/api/v1/sales/${saleId}/invoice.pdf`),
    pinApproval: (a) => post('/api/v1/approvals/pin', a),
    requestApproval: (a) => post('/api/v1/approvals', a),
    approvalStatus: (id) => get(`/api/v1/approvals/${id}`),
    approvals: (status) => get(`/api/v1/approvals${qs({ status })}`),
    decideApproval: (id, decision, reason) => post(`/api/v1/approvals/${id}/${decision}`, { reason }),
    printers: (branchId) => get(`/api/v1/printers${qs({ branchId })}`),
    createPrinter: (p) => post('/api/v1/printers', p),
    updatePrinter: (id, p) => put(`/api/v1/printers/${id}`, p),
    printJob: (printerId, job) => post(`/api/v1/printers/${printerId}/jobs`, job),
    setTerminalPrinter: (terminalId, printerId) => put(`/api/v1/terminals/${terminalId}/printer`, { printerId }),
    bindDeviceKey: (terminalId, deviceKey) => post(`/api/v1/terminals/${terminalId}/device-key`, { deviceKey }),
    async loginAt(username, password, terminal) {
      const t = await post<import('./index').Tokens>('/api/v1/auth/login', { username, password, ...terminal });
      h.setToken(t.accessToken);
      return t;
    },
    async pinLoginAt(username, pin, terminal) {
      const t = await post<import('./index').Tokens>('/api/v1/auth/pin-login', { username, pin, ...terminal });
      h.setToken(t.accessToken);
      return t;
    },
    emergencySnapshot: () => get('/api/v1/pos/emergency/snapshot'),
    uploadEmergency: (records) => post('/api/v1/pos/emergency/records', { records }),
    emergencyRecords: (result, devices) => get(`/api/v1/pos/emergency/records${qs({ result, devices })}`),
    settleEmergency: (id, s) => post(`/api/v1/pos/emergency/records/${id}/settle`, s),
  };
}
