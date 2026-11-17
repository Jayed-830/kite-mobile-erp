// Phase 10: customer credit (blueprint 14) and suppliers (16.1). Amounts are OMR numbers with three decimals.

export type CreditStatus = 'active' | 'hold' | 'closed';
export type HoldKind = 'manual' | 'overdue' | 'exposure';
export interface CreditPolicy {
  defaultTermsDays: number; blockOverdueDays: number; holdOverdueDays: number; offlineSaleCap: number; overrideCap: number; allowOvercommit: boolean;
  onlineTimeoutMs: number; authorisationMinutes: number; maxInstalments: number; remindBeforeDays: number; remindOverdueEvery: number; version: number;
}
export interface CreditAccountRow {
  id: string; customerId: string; customerNumber: string; nameEn: string; nameAr: string | null; phone: string | null; creditLimit: number; termsDays: number;
  status: CreditStatus; holdKind: HoldKind | null; holdReason: string | null; exposure: number; oldestDue: string | null; asOf: string | null; version: number;
  utilisation: number | null; available: number; overdueDays: number;
}
export interface CreditAccount {
  id: string; customerId: string; creditLimit: number; termsDays: number; status: CreditStatus; holdKind: HoldKind | null; holdReason: string | null; heldAt: string | null;
  offlineSaleCap: number | null; note: string | null; exposure: number; oldestDue: string | null; exposureAsOf: string | null; version: number;
  customerNumber: string; customerName: string; customerNameAr: string | null;
}
export interface OpenItem { id: string; number: string; branchCode: string; seq: number; of: number; dueDate: string; amount: number; open: number; overdueDays: number }
export interface CreditOnAccount { id: string; number: string; kind: string; branchCode: string; docDate: string; amount: number; leftOver: number }
export interface CreditLedgerRow { id: string; docDate: string; number: string; kind: string; branchCode: string; amount: number; note: string | null; createdAt: string }
export interface CreditDetail {
  account: CreditAccount; policy: CreditPolicy;
  figures: { exposure: number; balance: number; openItems: number; onAccount: number; oldestDue: string | null; overdueDays: number; available: number; blocked: boolean; headOffice: boolean; asOf: string | null };
  allowances: { branchId: string; code: string; nameEn: string; nameAr: string; amount: number }[];
  openItems: OpenItem[]; credits: CreditOnAccount[]; ledger: CreditLedgerRow[];
  holds: { action: 'hold' | 'release'; kind: HoldKind; reason: string; exposure: number | null; creditLimit: number | null; createdAt: string; byName: string | null; byNameAr: string | null }[];
  breaches: { detectedAt: string; exposure: number; creditLimit: number; entries: number; branchCode: string | null }[];
}
export interface CreditAccountInput { customerId: string; creditLimit: number; termsDays?: number | null; offlineSaleCap?: number | null; note?: string | null; allowances?: { branchId: string; amount: number }[]; status?: 'active' | 'closed' | null; version?: number | null }
export interface CreditAvailable {
  hasAccount: boolean; status?: CreditStatus; holdReason?: string | null; limit?: number; exposure?: number; available?: number; overdueDays?: number; blocked?: boolean;
  blockOverdueDays?: number; overrideCap?: number; termsDays?: number; maxInstalments?: number; offlineSaleCap?: number; mode?: 'head_office' | 'branch_copy'; asOf?: string | null;
}
export interface CustomerPaymentInput { id: string; customerId: string; method: 'cash' | 'card' | 'bank_transfer'; amount: number; branchId?: string | null; cardTerminal?: string | null; approvalCode?: string | null; reference?: string | null; note?: string | null; allocations?: { itemId: string; amount: number }[] }
export interface CustomerReceipt { id: string; number: string; method: string; amount: number; customerId: string; branchCode: string; receivedAt: string; balance: number; allocated: number }
export interface StatementLine { date: string; number: string; kind: string; branchCode: string; note: string | null; debit: number; credit: number; balance: number }
export interface Aging { current: number; d1To30: number; d31To60: number; d61To90: number; d90Plus: number; onAccount: number; total: number }
export interface CustomerStatement {
  customer: { id: string; number: string; nameEn: string; nameAr: string | null; phone: string | null; vatNumber: string | null };
  from: string; to: string; branchId: string | null; branchCode: string | null; opening: number; lines: StatementLine[]; debits: number; credits: number; closing: number;
  aging: Aging | null; creditLimit: number | null; generatedAt: string; headOffice: boolean;
}
export interface AgingRow { customerId: string; customerNumber: string; nameEn: string; nameAr: string | null; status: CreditStatus; creditLimit: number; current: number; d1_30: number; d31_60: number; d61_90: number; d90plus: number; onAccount: number; total: number }
export interface ExposureRow { branchId: string; code: string; nameEn: string; nameAr: string; balance: number; overdue: number; customers: number; chargedThisMonth: number; receivedThisMonth: number; allowances: number }
export interface OverridesReport {
  decisions: { id: string; createdAt: string; mode: 'local' | 'online' | 'offline'; amount: number; available: number; overBy: number; overdueDays: number; creditLimit: number; exposure: number; allowance: number | null; branchCode: string; customerNumber: string; nameEn: string; nameAr: string | null; invoiceNumber: string; approvedBy: string | null; approvedByAr: string | null }[];
  breaches: { id: string; detectedAt: string; exposure: number; creditLimit: number; entries: number; branchCode: string | null; customerNumber: string; nameEn: string; nameAr: string | null }[];
}
export interface LedgerCheckRow { ledger: 'customers' | 'suppliers'; branchId: string | null; branchCode: string | null; asOf: string; subledger: number; control: number; difference: number; checkedAt: string }

export type SupplierStatus = 'provisional' | 'active' | 'inactive' | 'merged';
export interface SupplierRow { id: string; number: string; nameEn: string; nameAr: string; vatNumber: string | null; phone: string | null; termsDays: number; status: SupplierStatus; mergedIntoId: string | null; mergedIntoNumber: string | null; originBranchCode: string | null; version: number; balance: number; createdAt: string }
export interface Supplier { id: string; number: string; nameEn: string; nameAr: string; vatNumber: string | null; crNumber: string | null; phone: string | null; email: string | null; address: string | null; contactName: string | null; termsDays: number; status: SupplierStatus; mergedIntoId: string | null; originBranchId: string | null; originServerId: string | null; version: number }
export interface SupplierDetail {
  supplier: Supplier; headOffice: boolean; balance: number;
  byBranch: { branchId: string; code: string; nameEn: string; nameAr: string; balance: number }[];
  merged: { id: string; number: string; nameEn: string; nameAr: string }[];
  ledger: { id: string; docDate: string; dueDate: string | null; number: string; kind: string; branchCode: string; amount: number; note: string | null }[];
}
export interface SupplierInput { nameEn: string; nameAr: string; vatNumber?: string | null; crNumber?: string | null; phone?: string | null; email?: string | null; address?: string | null; contactName?: string | null; termsDays?: number | null; version?: number | null }
export interface SupplierStatement { supplier: Supplier; from: string; to: string; branchId: string | null; opening: number; lines: { date: string; number: string; kind: string; branchCode: string; note: string | null; debit: number; credit: number; balance: number }[]; closing: number }
export interface SupplierAgingRow { supplierId: string; number: string; nameEn: string; nameAr: string; current: number; d1_30: number; d31_60: number; d61_90: number; d90plus: number; advance: number; total: number }

export interface CreditApi {
  creditPolicy(): Promise<CreditPolicy>;
  updateCreditPolicy(p: CreditPolicy): Promise<void>;
  creditAccounts(f?: { q?: string; status?: CreditStatus }): Promise<CreditAccountRow[]>;
  creditAccount(customerId: string): Promise<CreditDetail>;
  openCreditAccount(a: CreditAccountInput): Promise<{ id: string }>;
  updateCreditAccount(customerId: string, a: CreditAccountInput): Promise<void>;
  holdCredit(customerId: string, reason: string): Promise<void>;
  releaseCredit(customerId: string, reason: string): Promise<void>;
  creditAvailable(customerId: string): Promise<CreditAvailable>;
  receiveCustomerPayment(p: CustomerPaymentInput): Promise<CustomerReceipt>;
  allocateCredit(customerId: string, creditEntryId: string, allocations: { itemId: string; amount: number }[]): Promise<{ allocations: number }>;
  writeOffCredit(customerId: string, w: { reason: string; itemIds?: string[] | null; amount?: number | null }): Promise<{ id: string; number: string; total: number }>;
  customerOpening(customerId: string, o: { branchId: string; amount: number; date?: string | null; note?: string | null }): Promise<{ id: string; number: string }>;
  customerStatement(customerId: string, f?: { from?: string; to?: string; branchId?: string | null }): Promise<CustomerStatement>;
  customerStatementPdf(customerId: string, f?: { from?: string; to?: string; branchId?: string | null }): Promise<Blob>;
  creditAging(): Promise<AgingRow[]>;
  creditExposure(): Promise<ExposureRow[]>;
  creditOverrides(f?: { from?: string; to?: string }): Promise<OverridesReport>;
  ledgerChecks(): Promise<LedgerCheckRow[]>;
  runLedgerCheck(): Promise<{ runId: string; asOf: string; rows: unknown[] }>;
  suppliers(f?: { q?: string; status?: SupplierStatus }): Promise<SupplierRow[]>;
  supplier(id: string): Promise<SupplierDetail>;
  createSupplier(s: SupplierInput): Promise<{ id: string; number: string; status: SupplierStatus }>;
  updateSupplier(id: string, s: SupplierInput): Promise<void>;
  approveSupplier(id: string): Promise<void>;
  mergeSupplier(id: string, intoId: string, reason?: string | null): Promise<void>;
  supplierOpening(id: string, o: { branchId: string; amount: number; date?: string | null; note?: string | null }): Promise<{ id: string; number: string }>;
  supplierStatement(id: string, f?: { from?: string; to?: string; branchId?: string | null }): Promise<SupplierStatement>;
  supplierAging(): Promise<SupplierAgingRow[]>;
}

type Http = { get: <T>(p: string) => Promise<T>; post: <T>(p: string, b?: unknown) => Promise<T>; put: <T>(p: string, b: unknown) => Promise<T>; blob: (p: string) => Promise<Blob> };
const qs = (o: Record<string, string | number | boolean | undefined | null>) => {
  const p = Object.entries(o).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`);
  return p.length ? `?${p.join('&')}` : '';
};

export function creditMethods(h: Http): CreditApi {
  const { get, post, put, blob } = h;
  const c = (id: string) => `/api/v1/credit/customers/${id}`;
  return {
    creditPolicy: () => get('/api/v1/credit/policy'),
    updateCreditPolicy: (p) => put('/api/v1/credit/policy', p),
    creditAccounts: (f) => get(`/api/v1/credit/accounts${qs(f ?? {})}`),
    creditAccount: (id) => get(`/api/v1/credit/accounts/${id}`),
    openCreditAccount: (a) => post('/api/v1/credit/accounts', a),
    updateCreditAccount: (id, a) => put(`/api/v1/credit/accounts/${id}`, a),
    holdCredit: (id, reason) => post(`/api/v1/credit/accounts/${id}/hold`, { reason }),
    releaseCredit: (id, reason) => post(`/api/v1/credit/accounts/${id}/release`, { reason }),
    creditAvailable: (id) => get(`${c(id)}/available`),
    receiveCustomerPayment: (p) => post('/api/v1/credit/payments', p),
    allocateCredit: (id, creditEntryId, allocations) => post(`${c(id)}/allocate`, { creditEntryId, allocations }),
    writeOffCredit: (id, w) => post(`${c(id)}/write-off`, w),
    customerOpening: (id, o) => post(`${c(id)}/opening`, o),
    customerStatement: (id, f) => get(`${c(id)}/statement${qs(f ?? {})}`),
    customerStatementPdf: (id, f) => blob(`${c(id)}/statement.pdf${qs(f ?? {})}`),
    creditAging: () => get('/api/v1/credit/reports/aging'),
    creditExposure: () => get('/api/v1/credit/reports/exposure'),
    creditOverrides: (f) => get(`/api/v1/credit/reports/overrides${qs(f ?? {})}`),
    ledgerChecks: () => get('/api/v1/credit/reconciliation'),
    runLedgerCheck: () => post('/api/v1/credit/reconciliation/run'),
    suppliers: (f) => get(`/api/v1/suppliers${qs(f ?? {})}`),
    supplier: (id) => get(`/api/v1/suppliers/${id}`),
    createSupplier: (s) => post('/api/v1/suppliers', s),
    updateSupplier: (id, s) => put(`/api/v1/suppliers/${id}`, s),
    approveSupplier: (id) => post(`/api/v1/suppliers/${id}/approve`),
    mergeSupplier: (id, intoId, reason) => post(`/api/v1/suppliers/${id}/merge`, { intoId, reason: reason ?? null }),
    supplierOpening: (id, o) => post(`/api/v1/suppliers/${id}/opening`, o),
    supplierStatement: (id, f) => get(`/api/v1/suppliers/${id}/statement${qs(f ?? {})}`),
    supplierAging: () => get('/api/v1/suppliers/reports/aging'),
  };
}
