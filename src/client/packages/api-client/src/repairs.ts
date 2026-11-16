// Phase 8 API: repair jobs (intake, the workshop board, estimates, parts and labour, deposits, collection, messages), repair
// settings, and WhatsApp on the main server (settings, templates, the test chat).
import type { TenderInput } from './pos';

export const REPAIR_STATUSES = [
  'received', 'inspection_pending', 'under_diagnosis', 'waiting_approval', 'waiting_parts', 'in_progress', 'testing',
  'completed', 'ready', 'delivered', 'cancelled', 'unrepairable',
] as const;
export type RepairStatus = (typeof REPAIR_STATUSES)[number];

/** Blueprint 13.2: the only moves a job can make (the server and the database refuse any other). */
export const REPAIR_TRANSITIONS: Record<RepairStatus, RepairStatus[]> = {
  received: ['inspection_pending', 'cancelled'],
  inspection_pending: ['under_diagnosis', 'cancelled'],
  under_diagnosis: ['waiting_approval', 'in_progress', 'unrepairable'],
  waiting_approval: ['waiting_parts', 'in_progress', 'cancelled'],
  waiting_parts: ['in_progress', 'cancelled'],
  in_progress: ['testing', 'waiting_approval', 'waiting_parts', 'unrepairable'],
  testing: ['completed', 'in_progress'],
  completed: ['ready'],
  ready: ['delivered'],
  delivered: [],
  cancelled: ['ready'],
  unrepairable: ['ready'],
};
/** Moving into these needs the current estimate approved. */
export const NEEDS_APPROVED_ESTIMATE: RepairStatus[] = ['in_progress', 'waiting_parts', 'testing', 'completed'];
export const ACCESSORY_CODES = ['sim_tray', 'sim_card', 'memory_card', 'case', 'charger', 'box'] as const;
export const CONDITION_CODES = ['screen', 'body', 'buttons', 'cameras', 'liquid'] as const;
export type ConditionValue = 'ok' | 'fault' | 'not_checked';
export type PasscodeMode = 'none' | 'customer_present' | 'provided';
export type RepairOutcome = 'repaired' | 'cancelled' | 'unrepairable' | null;

export interface RepairBoardRow {
  id: string; number: string; status: RepairStatus; outcome: RepairOutcome; branchCode: string; branchId: string; customerName: string; customerPhone: string | null;
  device: string; identifier: string | null; categoryEn: string; categoryAr: string; technicianId: string | null; technicianName: string | null; promisedAt: string;
  overdue: boolean; warrantyClaim: boolean; createdAt: string; deliveredAt: string | null; estimate: number | null; estimateStatus: string | null; deposit: number;
  statusAt: string | null; unreadMessages: number; remindedDays: number; version: number;
}

export interface RepairJob {
  id: string; number: string; status: RepairStatus; outcome: RepairOutcome; branchId: string; branchCode: string; customerId: string; customerName: string;
  customerNameAr: string | null; customerPhone: string | null; customerOptIn: boolean; customerDeviceId: string; imei1: string | null; imei2: string | null;
  serial: string | null; brand: string | null; model: string; colour: string | null; deviceUnitId: string | null; categoryId: string; categoryCode: string;
  categoryNameEn: string; categoryNameAr: string; technicianId: string | null; technicianName: string | null; warrantyOfId: string | null; warrantyOfNumber: string | null;
  soldSaleId: string | null; soldSaleNumber: string | null; problem: string; faultCodes: string[]; condition: Partial<Record<string, ConditionValue>>;
  conditionNotes: string | null; accessories: { items: string[]; other: string | null }; passcodeMode: PasscodeMode; passcodeHeld: boolean; passcodePurgedAt: string | null;
  preAuthorised: number; ceiling: number; warrantyDays: number; promisedAt: string; overdue: boolean; termsName: string; termsAcceptedAt: string; notifyWhatsapp: boolean;
  lang: 'en' | 'ar'; deliverySaleId: string | null; deliverySaleNumber: string | null; deliveredAt: string | null; deliveredByName: string | null; collectedBy: string | null;
  collectionCheck: 'qr' | 'verified' | null; warrantyUntil: string | null; remindedDays: number; createdAt: string; createdByName: string; version: number; qr: string;
  next: RepairStatus[];
}
export interface RepairEstimateLine {
  id: string; lineNo: number; kind: 'labour' | 'part' | 'extra'; variantId: string; productType: string; descriptionEn: string; descriptionAr: string;
  qty: number; minutes: number | null; unitPrice: number; taxRate: number; netAmount: number; taxAmount: number; grossAmount: number;
}
export type EstimateStatus = 'draft' | 'sent' | 'approved' | 'declined' | 'superseded';
export interface RepairEstimate {
  id: string; estimateNo: number; status: EstimateStatus; netTotal: number; taxTotal: number; total: number; note: string | null; sentAt: string | null;
  approvalMethod: 'signature' | 'phone' | 'whatsapp' | 'pre_authorised' | 'warranty' | null; approvedAt: string | null; approvedBy: string | null; approverName: string | null;
  approvalRef: string | null; declineReason: string | null; declinedAt: string | null; createdAt: string; createdBy: string; version: number; lines: RepairEstimateLine[];
}
export interface RepairWorkLine {
  id: string; kind: 'part' | 'labour'; variantId: string | null; sku: string | null; nameEn: string | null; nameAr: string | null; qty: number | null; cost: number | null;
  returnOfId: string | null; minutes: number | null; technicianName: string | null; note: string | null; createdAt: string; createdBy: string; qtyLeft: number | null;
  costLeft: number | null; writtenOff: boolean;
}
export interface RepairDeposit {
  id: string; number: string; kind: 'deposit' | 'refund' | 'applied'; amount: number; tax: number; method: string | null; reason: string | null;
  saleNumber: string | null; createdAt: string; createdBy: string;
}
export interface RepairHistoryRow { from: RepairStatus | null; to: RepairStatus; note: string | null; at: string; userName: string | null }
export interface RepairDetail {
  job: RepairJob; estimates: RepairEstimate[]; work: RepairWorkLine[]; clearing: { kind: string; amount: number; reason: string | null; createdAt: string }[];
  deposits: RepairDeposit[]; history: RepairHistoryRow[]; assignments: { at: string; fromName: string | null; toName: string | null; byName: string | null }[];
  photos: { id: string; stage: string; contentType: string; size: number; sha256: string; caption: string | null; createdAt: string }[];
  claims: { id: string; number: string; status: RepairStatus }[];
  money: { deposit: number; depositTax: number; wip: number; charges: RepairEstimateLine[]; chargesTotal: number; due: number; excess: number; diagnosisFee: number; hourlyRate: number; depositVatPoint: boolean };
}
export interface RepairLookup {
  kind: string; value: string;
  devices: { id: string; customerId: string; customerName: string; customerPhone: string | null; imei1: string | null; imei2: string | null; serial: string | null; brand: string | null; model: string; colour: string | null }[];
  unit: { id: string; descriptionEn: string; descriptionAr: string; status: string } | null;
  sold: { saleId: string; number: string; soldAt: string; branchCode: string; warrantyUntil: string | null; inWarranty: boolean; customerId: string | null; customerName: string | null } | null;
  jobs: { id: string; number: string; status: RepairStatus; outcome: RepairOutcome; createdAt: string; deliveredAt: string | null; warrantyUntil: string | null; inWarranty: boolean }[];
}
export interface CustomerDevice { id: string; imei1: string | null; imei2: string | null; serial: string | null; brand: string | null; model: string; colour: string | null; deviceUnitId: string | null; jobs: number; openJob: string | null }
export interface PhotoInput { stage: string; dataUrl: string; caption?: string | null }
export interface RepairIntake {
  customerId: string; categoryId: string; problem: string; passcodeMode: PasscodeMode; termsName: string; termsSignature: string;
  customerDeviceId?: string | null; device?: { imei1?: string | null; imei2?: string | null; serial?: string | null; brand?: string | null; model?: string | null; colour?: string | null } | null;
  faultCodes?: string[]; condition?: Record<string, ConditionValue>; conditionNotes?: string | null; accessories?: string[]; accessoriesOther?: string | null;
  passcode?: string | null; preAuthorised?: number | null; promisedAt?: string | null; notifyWhatsapp?: boolean; lang?: 'en' | 'ar'; photos?: PhotoInput[];
  technicianId?: string | null; id?: string;
}
export type WarrantyClaimInput = Omit<RepairIntake, 'customerId' | 'categoryId' | 'customerDeviceId' | 'device' | 'preAuthorised' | 'promisedAt' | 'lang' | 'technicianId' | 'id'>;
export interface EstimateLineInput { kind: 'labour' | 'part' | 'extra'; variantId: string; qty?: number; unitPrice?: number | null; minutes?: number | null; descriptionEn?: string | null; descriptionAr?: string | null }
export interface EstimateApprove { method: 'signature' | 'phone' | 'pre_authorised'; approverName?: string | null; phone?: string | null; signature?: string | null; then?: RepairStatus | null }
export interface DepositInput { amount: number; method: 'cash' | 'card' | 'bank_transfer'; tendered?: number | null; cardTerminal?: string | null; approvalCode?: string | null; reference?: string | null }
export interface DepositResult { id: string; number: string; amount: number; tax: number; change: number; balance: number }
export interface DeliveryPreview {
  number: string; status: RepairStatus; outcome: RepairOutcome; charges: RepairEstimateLine[]; gross: number; deposit: number; applied: number; due: number; excess: number;
  wip: number; qr: string; diagnosisFee: number; customerId: string; customerName: string;
}
export interface DeliverInput {
  check: 'qr' | 'verified'; collectedBy: string; signature: string; qrCode?: string | null; diagnosisFee?: number | null; payments?: TenderInput[];
  excessTo?: 'cash' | 'store_credit' | null; expectedDue?: number | null; note?: string | null;
}
export interface DeliverResult {
  id: string; number: string; saleId: string | null; saleNumber: string | null; gross: number; applied: number; due: number; excess: number; refundId: string | null;
  refundNumber: string | null; change: number; cashRounding: number; warrantyUntil: string | null;
}
export interface RepairMessage {
  id: string; direction: 'out' | 'in'; template?: string | null; params?: string[] | null; kind?: string; text: string | null; status?: string; statusReason?: string | null;
  statusAt?: string | null; handled?: string | null; at: string; byName?: string | null;
}
export interface RepairMessages { messages: RepairMessage[]; canReply: boolean; replyUntil: string | null; optIn: boolean; notify: boolean }
export interface UncollectedRow { id: string; number: string; branchCode: string; customerName: string; customerPhone: string | null; device: string; outcome: RepairOutcome; readySince: string; days: number; remindedDays: number; deposit: number }

export interface RepairSettings {
  preAuthorised: number; warrantyDays: number; passcodeKeepDays: number; uncollectedDays: number[]; diagnosisFee: number; hourlyRate: number; depositVatPoint: boolean;
  notifyStatuses: string[]; termsEn: string; termsAr: string; version: number;
}
export interface RepairCategory { id: string; code: string; nameEn: string; nameAr: string; turnaroundHours: number; warrantyDays: number | null; preAuthorised: number | null; sort: number; status: 'active' | 'inactive'; version: number }
export interface FaultCode { id: string; categoryId: string | null; code: string; nameEn: string; nameAr: string; sort: number; status: 'active' | 'inactive'; version: number }
export interface RepairSetup {
  settings: RepairSettings; categories: RepairCategory[]; faultCodes: FaultCode[];
  services: { id: string; sku: string; nameEn: string; nameAr: string; productType: string }[]; accessories: string[]; conditionChecks: string[];
}
export type RepairCategoryInput = Omit<RepairCategory, 'id' | 'version' | 'sort' | 'status'> & { sort?: number; status?: 'active' | 'inactive'; version?: number };
export type FaultCodeInput = Omit<FaultCode, 'id' | 'version' | 'sort' | 'status'> & { sort?: number; status?: 'active' | 'inactive'; version?: number };

export interface WhatsAppSettings {
  gateway: boolean; enabled: boolean | null; provider: 'meta' | 'test' | null; phoneNumberId: string | null; wabaId: string | null; displayPhone: string | null;
  hasToken: boolean; hasSecret: boolean; verifyToken: string | null; dailyCap: number | null; staleHours: number | null; webhookSeenAt: string | null; version: number | null;
  webhookUrl: string | null;
  webAddress: { host: string | null; active: boolean; offReason: string | null; tunnel: string; error: string | null; since: string | null; listening: boolean; port: number; problem: string | null };
  counts: { status: string; n: number }[]; sentToday: number; relay: { lastOk: string | null; error: string | null };
}
export interface WhatsAppSettingsInput {
  enabled: boolean; provider: 'meta' | 'test'; phoneNumberId: string | null; wabaId: string | null; displayPhone: string | null; accessToken?: string | null; appSecret?: string | null;
  dailyCap: number; staleHours: number; version: number;
}
export interface WhatsAppTemplate { id: string; code: string; lang: 'en' | 'ar'; metaName: string; metaLang: string; body: string; params: string[]; buttons: string[]; version: number }
export interface TestChat {
  phones: { phone: string; last: string }[];
  messages: { id: string; direction: 'out' | 'in'; wamid: string | null; body: string; buttons: string[] | null; at: string; status: string | null }[];
}
export type TestChatAction = 'delivered' | 'read' | 'failed' | 'button' | 'text' | 'stop' | 'start';

export interface RepairApi {
  repairs(f?: { status?: RepairStatus; technicianId?: string; branchId?: string; q?: string; open?: boolean }): Promise<RepairBoardRow[]>;
  repair(id: string): Promise<RepairDetail>;
  repairLookup(code: string): Promise<RepairLookup>;
  createRepair(r: RepairIntake): Promise<RepairDetail>;
  warrantyClaim(id: string, r: WarrantyClaimInput): Promise<RepairDetail>;
  moveRepair(id: string, to: RepairStatus, note?: string | null, version?: number): Promise<RepairDetail>;
  assignRepair(id: string, technicianId: string | null): Promise<RepairDetail>;
  repairTechnicians(branchId?: string): Promise<{ id: string; nameEn: string; nameAr: string | null }[]>;
  revealPasscode(id: string): Promise<{ passcode: string }>;
  addRepairPhoto(id: string, p: PhotoInput): Promise<{ id: string }>;
  repairPhoto(id: string, photoId: string): Promise<Blob>;
  repairMessages(id: string): Promise<RepairMessages>;
  sendRepairMessage(id: string, text: string): Promise<RepairMessages>;
  uncollected(): Promise<UncollectedRow[]>;
  writeEstimate(id: string, lines: EstimateLineInput[], note?: string | null): Promise<RepairDetail>;
  updateEstimate(id: string, estimateId: string, lines: EstimateLineInput[], note?: string | null): Promise<RepairDetail>;
  sendEstimate(id: string, estimateId: string): Promise<RepairDetail>;
  approveEstimate(id: string, estimateId: string, a: EstimateApprove): Promise<RepairDetail>;
  declineEstimate(id: string, estimateId: string, reason: string): Promise<RepairDetail>;
  fitPart(id: string, p: { variantId: string; qty: number; note?: string | null }): Promise<RepairDetail>;
  returnPart(id: string, lineId: string, qty: number, note?: string | null): Promise<RepairDetail>;
  writeOffPart(id: string, lineId: string, reason: string): Promise<RepairDetail>;
  logLabour(id: string, l: { minutes: number; technicianId?: string | null; note?: string | null }): Promise<RepairDetail>;
  takeDeposit(id: string, d: DepositInput): Promise<DepositResult>;
  refundDeposit(id: string, d: { amount: number; method: 'cash' | 'card' | 'bank_transfer' | 'store_credit'; reason: string; cardTerminal?: string | null; reference?: string | null }): Promise<unknown>;
  deliveryPreview(id: string, diagnosisFee?: number | null): Promise<DeliveryPreview>;
  deliver(id: string, d: DeliverInput): Promise<DeliverResult>;
  repairDocument(id: string, doc: 'job-card' | 'tag' | 'delivery-receipt' | { deposit: string }): Promise<Blob>;
  customerDevices(customerId: string): Promise<CustomerDevice[]>;
  repairSetup(): Promise<RepairSetup>;
  updateRepairSettings(s: RepairSettings): Promise<void>;
  createRepairCategory(c: RepairCategoryInput): Promise<{ id: string }>;
  updateRepairCategory(id: string, c: RepairCategoryInput): Promise<void>;
  createFaultCode(c: FaultCodeInput): Promise<{ id: string }>;
  updateFaultCode(id: string, c: FaultCodeInput): Promise<void>;
  whatsAppSettings(): Promise<WhatsAppSettings>;
  updateWhatsAppSettings(s: WhatsAppSettingsInput): Promise<void>;
  connectWhatsApp(): Promise<{ steps: { step: string; ok: boolean; detail: string | null }[]; display: string | null }>;
  whatsAppTemplates(): Promise<WhatsAppTemplate[]>;
  updateWhatsAppTemplate(id: string, t: { metaName: string; metaLang: string; body: string; buttons: string[]; version: number }): Promise<{ id: string }>;
  testChat(phone?: string | null): Promise<TestChat>;
  testChatAction(a: { phone: string; action: TestChatAction; text?: string | null; button?: number | null; wamid?: string | null }): Promise<unknown>;
}

type Http = { get: <T>(p: string) => Promise<T>; post: <T>(p: string, b?: unknown) => Promise<T>; put: <T>(p: string, b: unknown) => Promise<T>; blob: (p: string) => Promise<Blob> };
const qs = (o: Record<string, string | number | boolean | undefined | null>) => {
  const p = Object.entries(o).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`);
  return p.length ? `?${p.join('&')}` : '';
};

export function repairMethods(h: Http): RepairApi {
  const { get, post, put, blob } = h;
  const r = (id: string) => `/api/v1/repairs/${id}`;
  return {
    repairs: (f) => get(`/api/v1/repairs${qs(f ?? {})}`),
    repair: (id) => get(r(id)),
    repairLookup: (code) => get(`/api/v1/repairs/lookup${qs({ code })}`),
    createRepair: (x) => post('/api/v1/repairs', x),
    warrantyClaim: (id, x) => post(`${r(id)}/warranty-claim`, x),
    moveRepair: (id, to, note, version) => post(`${r(id)}/status`, { to, note: note ?? null, version: version ?? null }),
    assignRepair: (id, technicianId) => post(`${r(id)}/assign`, { technicianId }),
    repairTechnicians: (branchId) => get(`/api/v1/repairs/technicians${qs({ branchId })}`),
    revealPasscode: (id) => post(`${r(id)}/passcode`),
    addRepairPhoto: (id, p) => post(`${r(id)}/photos`, p),
    repairPhoto: (id, photoId) => blob(`${r(id)}/photos/${photoId}`),
    repairMessages: (id) => get(`${r(id)}/messages`),
    sendRepairMessage: (id, text) => post(`${r(id)}/messages`, { text }),
    uncollected: () => get('/api/v1/repairs/uncollected'),
    writeEstimate: (id, lines, note) => post(`${r(id)}/estimates`, { lines, note: note ?? null }),
    updateEstimate: (id, eid, lines, note) => put(`${r(id)}/estimates/${eid}`, { lines, note: note ?? null }),
    sendEstimate: (id, eid) => post(`${r(id)}/estimates/${eid}/send`),
    approveEstimate: (id, eid, a) => post(`${r(id)}/estimates/${eid}/approve`, a),
    declineEstimate: (id, eid, reason) => post(`${r(id)}/estimates/${eid}/decline`, { reason }),
    fitPart: (id, p) => post(`${r(id)}/parts`, p),
    returnPart: (id, lineId, qty, note) => post(`${r(id)}/parts/${lineId}/return`, { qty, note: note ?? null }),
    writeOffPart: (id, lineId, reason) => post(`${r(id)}/parts/${lineId}/write-off`, { reason }),
    logLabour: (id, l) => post(`${r(id)}/labour`, l),
    takeDeposit: (id, d) => post(`${r(id)}/deposits`, d),
    refundDeposit: (id, d) => post(`${r(id)}/deposits/refund`, d),
    deliveryPreview: (id, fee) => get(`${r(id)}/delivery${qs({ diagnosisFee: fee })}`),
    deliver: (id, d) => post(`${r(id)}/deliver`, d),
    repairDocument: (id, doc) => blob(typeof doc === 'string' ? `${r(id)}/${doc}` : `${r(id)}/deposits/${doc.deposit}/receipt`),
    customerDevices: (customerId) => get(`/api/v1/customers/${customerId}/devices`),
    repairSetup: () => get('/api/v1/repair-setup'),
    updateRepairSettings: (s) => put('/api/v1/repair-setup/settings', s),
    createRepairCategory: (c) => post('/api/v1/repair-setup/categories', c),
    updateRepairCategory: (id, c) => put(`/api/v1/repair-setup/categories/${id}`, c),
    createFaultCode: (c) => post('/api/v1/repair-setup/fault-codes', c),
    updateFaultCode: (id, c) => put(`/api/v1/repair-setup/fault-codes/${id}`, c),
    whatsAppSettings: () => get('/api/v1/whatsapp/settings'),
    updateWhatsAppSettings: (s) => put('/api/v1/whatsapp/settings', s),
    connectWhatsApp: () => post('/api/v1/whatsapp/connect'),
    whatsAppTemplates: () => get('/api/v1/whatsapp/templates'),
    updateWhatsAppTemplate: (id, t) => put(`/api/v1/whatsapp/templates/${id}`, t),
    testChat: (phone) => get(`/api/v1/whatsapp/test-chat${qs({ phone })}`),
    testChatAction: (a) => post('/api/v1/whatsapp/test-chat', a),
  };
}
