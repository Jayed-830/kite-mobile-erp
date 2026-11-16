// Phase 7 API: trade-ins at the till and in the back office, the price guide and deductions, store credit, the margin scheme approval.
import type { ChecklistItemDef } from './devices';

export type TradeInStatus = 'open' | 'accepted' | 'settled' | 'rejected' | 'cancelled';
export type SettlementKind = 'sale' | 'store_credit' | 'cash';

export interface TradeIn {
  id: string; number: string; status: TradeInStatus; branchId: string; branchCode: string; customerId: string; customerName: string; customerNameAr: string | null;
  customerPhone: string | null; civilId: string | null; sellerVatNumber: string | null; variantId: string; sku: string; descriptionEn: string; descriptionAr: string;
  imei1: string | null; imei2: string | null; serial: string | null; entry: 'scan' | 'manual'; storage: string | null; colour: string | null;
  gradeId: string | null; gradeCode: string | null; gradeNameEn: string | null; gradeNameAr: string | null; condition: 'used' | 'damaged';
  inspection: Record<string, string | null>; batteryHealth: number | null; cloudLockRemoved: boolean; guideValue: number | null; deductionsTotal: number;
  offeredValue: number | null; agreedValue: number | null; approvalId: string | null; consentMethod: 'on_screen' | 'paper' | null; consentName: string | null;
  consentReference: string | null; consentAt: string | null; saleId: string | null; deviceUnitId: string | null; reconnected: boolean; marginScheme: boolean;
  selfInvoiceNumber: string | null; note: string | null; rejectReason: string | null; acceptedAt: string | null; acceptedByName: string | null; settledAt: string | null;
  closedAt: string | null; createdAt: string; createdByName: string; version: number; outstanding: number | null; needsApproval: boolean | null;
}
export interface TradeInIssue { code: string; nameEn: string; nameAr: string; amount: number }
export interface TradeInSettlement { id: string; kind: SettlementKind; amount: number; saleNumber: string | null; createdAt: string; createdBy: string }
export interface TradeInGrade { id: string; code: string; nameEn: string; nameAr: string; pricePercent: number; guideValue: number | null }
export interface TradeInDeductionDef { id: string; code: string; nameEn: string; nameAr: string; kind: 'amount' | 'percent'; value: number; checklistCode: string | null }
export interface TradeInForm {
  checklistId: string | null; items: ChecklistItemDef[]; grades: TradeInGrade[]; deductions: TradeInDeductionDef[]; staffLimit: number; overGuidePercent: number;
  requireCivilId: boolean; declarationEn: string; declarationAr: string; canApprove: boolean;
}
export interface TradeInDetail { tradeIn: TradeIn; issues: TradeInIssue[]; settlements: TradeInSettlement[]; form: TradeInForm | null }
export interface TradeInListRow {
  id: string; number: string; status: TradeInStatus; branchCode: string; customerName: string; customerPhone: string | null; descriptionEn: string; descriptionAr: string;
  identifier: string | null; offeredValue: number | null; agreedValue: number | null; outstanding: number | null; marginScheme: boolean; reconnected: boolean;
  createdAt: string; acceptedAt: string | null; createdBy: string;
}
export interface OpenTradeIn { id: string; number: string; customerId: string; customerName: string; descriptionEn: string; descriptionAr: string; identifier: string | null; agreedValue: number; outstanding: number; saleId: string | null }

export interface TradeInDeviceInput { variantId: string; imei1?: string | null; imei2?: string | null; serial?: string | null; entry?: 'scan' | 'manual'; storage?: string | null; colour?: string | null }
export interface TradeInCreate { customerId: string; device: TradeInDeviceInput; civilId?: string | null; sellerVatNumber?: string | null; saleId?: string | null; note?: string | null }
export interface TradeInUpdate {
  version: number; device?: TradeInDeviceInput; gradeId?: string | null; condition?: 'used' | 'damaged'; inspection?: Record<string, string | null>; batteryHealth?: number | null;
  cloudLockRemoved?: boolean; deductions?: string[]; agreedValue?: number | null; civilId?: string | null; sellerVatNumber?: string | null; note?: string | null; saleId?: string | null;
}
export interface TradeInSettle { kind: 'store_credit' | 'cash'; amount?: number | null; approvalId?: string | null }
export interface TradeInAccept {
  version: number; agreedValue: number; consent: { method: 'on_screen' | 'paper'; name?: string | null; reference?: string | null }; approvalId?: string | null;
  settle?: TradeInSettle | null; saleId?: string | null;
}
export interface TradeInSettings {
  staffLimit: number; overGuidePercent: number; requireCivilId: boolean; declarationEn: string; declarationAr: string; version: number;
  margin: { approved: boolean; reference: string | null; approvedOn: string | null };
}
export interface PriceGuideRow { id: string; variantId: string; sku: string; descriptionEn: string; descriptionAr: string; gradeId: string; gradeCode: string; baseValue: number; status: string; updatedAt: string }
export interface DeductionRow extends TradeInDeductionDef { sort: number; status: 'active' | 'inactive'; version: number }
export interface DeductionInput { code: string; nameEn: string; nameAr: string; kind: 'amount' | 'percent'; value: number; checklistCode?: string | null; sort?: number; status?: 'active' | 'inactive'; version?: number }
export interface TradeInReportRow {
  number: string; acceptedAt: string; branchCode: string; descriptionEn: string; identifier: string | null; agreedValue: number; marginScheme: boolean; unitStatus: string;
  costAdded: number; saleNumber: string | null; resaleNet: number | null; resaleTax: number | null; profit: number | null;
}
export interface MarginScheme { approved: boolean; reference: string | null; approvedOn: string | null; note: string | null; version: number; taxCode: string; taxCodeStatus: 'active' | 'inactive' }
export interface StoreCredit {
  customerId: string; balance: number;
  lines: { id: string; amount: number; sourceDocType: string; sourceDocId: string; note: string | null; createdAt: string; branchCode: string; documentNumber: string | null }[];
}

export interface TradeInApi {
  tradeIns(f?: { status?: TradeInStatus | 'unsettled'; q?: string; customerId?: string; branchId?: string }): Promise<TradeInListRow[]>;
  tradeIn(id: string): Promise<TradeInDetail>;
  createTradeIn(t: TradeInCreate): Promise<{ detail: TradeInDetail; headOfficeChecked: boolean }>;
  updateTradeIn(id: string, t: TradeInUpdate): Promise<TradeInDetail>;
  acceptTradeIn(id: string, a: TradeInAccept): Promise<TradeInDetail>;
  settleTradeIn(id: string, s: TradeInSettle): Promise<TradeInDetail>;
  rejectTradeIn(id: string, reason: string): Promise<TradeInDetail>;
  cancelTradeIn(id: string): Promise<TradeInDetail>;
  revealCivilId(id: string): Promise<{ civilId: string }>;
  openTradeIns(f?: { customerId?: string | null; saleId?: string | null }): Promise<OpenTradeIn[]>;
  tradeInDocument(id: string): Promise<Blob>;
  tradeInSettings(): Promise<TradeInSettings>;
  updateTradeInSettings(s: Omit<TradeInSettings, 'margin'>): Promise<void>;
  priceGuide(variantId?: string): Promise<PriceGuideRow[]>;
  savePriceGuide(rows: { variantId: string; gradeId: string; baseValue: number | null }[]): Promise<void>;
  deductions(): Promise<DeductionRow[]>;
  createDeduction(d: DeductionInput): Promise<{ id: string }>;
  updateDeduction(id: string, d: DeductionInput): Promise<void>;
  tradeInReport(): Promise<TradeInReportRow[]>;
  storeCredit(customerId: string): Promise<StoreCredit>;
  marginScheme(): Promise<MarginScheme>;
  updateMarginScheme(m: { approved: boolean; reference: string | null; approvedOn: string | null; note: string | null; version: number }): Promise<void>;
}

type Http = { get: <T>(p: string) => Promise<T>; post: <T>(p: string, b?: unknown) => Promise<T>; put: <T>(p: string, b: unknown) => Promise<T>; blob: (p: string) => Promise<Blob> };

const qs = (o: Record<string, string | number | boolean | undefined | null>) => {
  const p = Object.entries(o).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`);
  return p.length ? `?${p.join('&')}` : '';
};

export function tradeInMethods(h: Http): TradeInApi {
  const { get, post, put, blob } = h;
  return {
    tradeIns: (f) => get(`/api/v1/tradeins${qs(f ?? {})}`),
    tradeIn: (id) => get(`/api/v1/tradeins/${id}`),
    createTradeIn: (t) => post('/api/v1/tradeins', t),
    updateTradeIn: (id, t) => put(`/api/v1/tradeins/${id}`, t),
    acceptTradeIn: (id, a) => post(`/api/v1/tradeins/${id}/accept`, a),
    settleTradeIn: (id, s) => post(`/api/v1/tradeins/${id}/settle`, s),
    rejectTradeIn: (id, reason) => post(`/api/v1/tradeins/${id}/reject`, { reason }),
    cancelTradeIn: (id) => post(`/api/v1/tradeins/${id}/cancel`, {}),
    revealCivilId: (id) => post(`/api/v1/tradeins/${id}/civil-id`),
    openTradeIns: (f) => get(`/api/v1/tradeins/open${qs(f ?? {})}`),
    tradeInDocument: (id) => blob(`/api/v1/tradeins/${id}/document`),
    tradeInSettings: () => get('/api/v1/tradein/settings'),
    updateTradeInSettings: (s) => put('/api/v1/tradein/settings', s),
    priceGuide: (variantId) => get(`/api/v1/tradein/guide${qs({ variantId })}`),
    savePriceGuide: (rows) => put('/api/v1/tradein/guide', { rows }),
    deductions: () => get('/api/v1/tradein/deductions'),
    createDeduction: (d) => post('/api/v1/tradein/deductions', d),
    updateDeduction: (id, d) => put(`/api/v1/tradein/deductions/${id}`, d),
    tradeInReport: () => get('/api/v1/tradein/report'),
    storeCredit: (customerId) => get(`/api/v1/customers/${customerId}/store-credit`),
    marginScheme: () => get('/api/v1/tax/margin-scheme'),
    updateMarginScheme: (m) => put('/api/v1/tax/margin-scheme', m),
  };
}
