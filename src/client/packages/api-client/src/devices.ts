// Phase 5 API: device units (identity, cost, history), inspections, refurbishment, grades, checklists, device
// settings, the till's unit lookups and leases, and the device reports.
export type DeviceStatus = 'in_stock' | 'under_inspection' | 'returned_inspection' | 'refurbishing' | 'for_parts' | 'supplier_return' | 'sold' | 'in_transit'
  | 'quarantined' | 'supplier_returned' | 'written_off' | 'reversed';
/** Where a unit stands with head office (Phase 6): verified, received while head office was away, or in an IMEI conflict. */
export type CentralState = 'verified' | 'pending' | 'conflict';
export type DeviceCondition = 'brand_new' | 'used' | 'refurbished' | 'damaged' | 'for_parts';
export type InspectionOutcome = 'restock' | 'refurbish' | 'for_parts' | 'supplier_return';

export interface DeviceRow {
  id: string; branchCode: string; variantId: string; sku: string; descriptionEn: string; descriptionAr: string; productType: string; status: DeviceStatus;
  heldFromStatus: DeviceStatus | null; condition: DeviceCondition; gradeCode: string | null; gradeNameEn: string | null; gradeNameAr: string | null;
  acquisitionCost: number; accumulatedCost: number; sellingPrice: number | null; imei1: string | null; imei2: string | null; serial: string | null; receivedAt: string; ageDays: number; version: number;
  centralState?: CentralState | null;
}
export interface DeviceEvent {
  seq: number; eventType: string; fromStatus: DeviceStatus | null; toStatus: DeviceStatus; fromCondition: DeviceCondition | null; toCondition: DeviceCondition; gradeCode: string | null;
  costDelta: number; sellingPrice: number | null; docType: string | null; docId: string | null; docNumber: string | null; details: string | null; occurredAt: string; actorName: string | null;
}
export interface DeviceDetail {
  unit: {
    id: string; branchId: string; branchCode: string; variantId: string; sku: string; descriptionEn: string; descriptionAr: string; productType: string; tracking: string;
    categoryId: string | null; status: DeviceStatus; heldFromStatus: DeviceStatus | null; condition: DeviceCondition; gradeId: string | null; gradeCode: string | null;
    gradeNameEn: string | null; gradeNameAr: string | null; imei1: string | null; imei2: string | null; serial: string | null; receivedAt: string; sellingPrice: number | null;
    warrantyDays: number | null; defaultWarrantyDays: number; version: number;
  };
  cost: { acquisition: number; parts: number; labour: number; external: number; total: number };
  price: { newPrice: number | null; gradePercent: number | null; suggested: number | null; listPrice: number | null };
  events: DeviceEvent[];
  inspections: { id: string; number: string; reason: string; outcome: InspectionOutcome; suggestedOutcome: InspectionOutcome; resultCondition: DeviceCondition; gradeCode: string | null; batteryHealth: number | null; note: string | null; results: string; inspectedAt: string; inspectedBy: string }[];
  refurbishments: { id: string; number: string; status: 'open' | 'completed'; problem: string | null; workDone: string | null; openedAt: string; completedAt: string | null; technicianName: string | null; total: number }[];
  sales: { saleId: string; number: string; docType: string; creditReason: string | null; completedAt: string; net: number; tax: number; gross: number; cost: number; profit: number | null; customerName: string | null }[];
  holds?: DeviceHold[];
  identifiers?: { kind: 'imei' | 'serial' | 'eid'; slot: number; value: string; entry: 'scan' | 'manual'; status: 'active' | 'retired'; createdAt: string; retiredAt: string | null; retiredReason: string | null }[];
  central?: { state: CentralState; checkedAt: string | null; attempts: number; lastError: string | null };
}
export interface DeviceHold { id: string; reason: 'manager' | 'imei_conflict' | 'blocked_imei'; conflictId: string | null; blockedId: string | null; note: string | null; heldAt: string; releasedAt: string | null; releaseNote: string | null }
export interface UnitInput {
  imei1?: string | null; imei2?: string | null; serial?: string | null; unitCost?: number | null; condition?: DeviceCondition; status?: 'in_stock' | 'under_inspection';
  gradeId?: string | null; sellingPrice?: number | null; warrantyDays?: number | null; entry?: 'scan' | 'manual';
}
export interface Grade {
  id: string; categoryId: string | null; categoryName: string | null; code: string; nameEn: string; nameAr: string; descriptionEn: string | null; descriptionAr: string | null;
  pricePercent: number; sort: number; status: 'active' | 'inactive'; version: number;
}
export interface GradeInput { code: string; nameEn: string; nameAr: string; pricePercent: number; categoryId?: string | null; descriptionEn?: string | null; descriptionAr?: string | null; sort?: number; status?: 'active' | 'inactive'; version?: number }
export interface ChecklistItemDef { code: string; labelEn: string; labelAr: string; kind: 'pass_fail' | 'percent' | 'text'; required: boolean; minPercent: number | null; onFail: 'refurbish' | 'damaged' | 'for_parts' }
export interface Checklist { id: string; categoryId: string | null; categoryName: string | null; nameEn: string; nameAr: string; status: string; version: number; items: ChecklistItemDef[] }
export interface ChecklistInput { nameEn: string; nameAr: string; items: ChecklistItemDef[]; categoryId?: string | null; version?: number }
export interface DeviceSettings { labourRatePerHour: number; reservationMinutes: number; parkedReservationMinutes: number; suggestedPriceStep: number; version: number; strictCentralCheck?: boolean }
export interface PosUnit {
  unitId: string; variantId: string; sku: string; productType: string; tracking: string; descriptionEn: string; descriptionAr: string; taxRate: number; listPrice: number | null;
  minPrice: number | null; condition: DeviceCondition; gradeCode: string | null; imei1: string | null; imei2: string | null; serial: string | null; warrantyDays: number; reservedUntil: string | null;
  centralState?: CentralState | null;
}
export interface InspectionQueueRow { id: string; branchCode: string; sku: string; descriptionEn: string; descriptionAr: string; status: DeviceStatus; condition: DeviceCondition; imei1: string | null; serial: string | null; accumulatedCost: number; receivedAt: string }
export interface InspectionInput { answers: Record<string, string | null>; outcome: InspectionOutcome; condition: DeviceCondition; gradeId?: string | null; sellingPrice?: number | null; note?: string | null; technicianId?: string | null; version?: number }
export interface InspectionResult { id: string; number: string; outcome: InspectionOutcome; suggestedOutcome: InspectionOutcome; failed: string[]; refurbOrderId: string | null; refurbOrderNumber: string | null }
export interface RefurbOrderRow {
  id: string; number: string; status: 'open' | 'completed'; branchId: string; unitId: string; inspectionId: string | null; technicianId: string | null; technicianName: string | null;
  problem: string | null; workDone: string | null; openedAt: string; completedAt: string | null; resultCondition: DeviceCondition | null; gradeCode: string | null; sellingPrice: number | null;
  warrantyDays: number | null; version: number; costTotal: number; unitStatus: DeviceStatus; unitCondition: DeviceCondition; unitCost: number; sku: string; identifier: string | null;
  descriptionEn: string; descriptionAr: string;
}
export interface RefurbCost {
  id: string; kind: 'part' | 'labour' | 'external'; amount: number; qty: number | null; unitCost: number | null; minutes: number | null; rate: number | null; vendor: string | null;
  reference: string | null; note: string | null; reversalOf: string | null; reversed: boolean; partSku: string | null; partDescriptionEn: string | null; partDescriptionAr: string | null;
  technicianName: string | null; createdBy: string; createdAt: string;
}
export interface RefurbCostInput { kind: 'part' | 'labour' | 'external'; variantId?: string; qty?: number; minutes?: number; technicianId?: string | null; vendor?: string; reference?: string | null; amount?: number; note?: string | null }

export interface DeviceApi {
  devices(f: { status?: string; condition?: string; branchId?: string; variantId?: string; q?: string; central?: CentralState; limit?: number }): Promise<DeviceRow[]>;
  deviceLookup(code: string): Promise<{ id: string }>;
  device(id: string): Promise<DeviceDetail>;
  setDevicePrice(id: string, p: { sellingPrice: number | null; warrantyDays?: number | null; version: number }): Promise<void>;
  holdDevice(id: string, reason: string): Promise<void>;
  releaseDevice(id: string, reason: string): Promise<void>;
  writeOffDevice(id: string, w: { reason: string; reasonCode?: 'damaged' | 'lost' | 'other' }): Promise<{ id: string; number: string; cost: number }>;
  deviceSettings(): Promise<DeviceSettings>;
  saveDeviceSettings(s: DeviceSettings): Promise<void>;
  grades(): Promise<Grade[]>;
  createGrade(g: GradeInput): Promise<{ id: string }>;
  updateGrade(id: string, g: GradeInput): Promise<void>;
  checklists(): Promise<Checklist[]>;
  deviceChecklist(unitId: string): Promise<{ checklistId: string | null; items: ChecklistItemDef[]; grades: { id: string; code: string; nameEn: string; nameAr: string; pricePercent: number }[] }>;
  createChecklist(c: ChecklistInput): Promise<{ id: string }>;
  updateChecklist(id: string, c: ChecklistInput): Promise<void>;
  posUnitByCode(code: string, variantId?: string): Promise<PosUnit>;
  posUnits(variantId: string): Promise<PosUnit[]>;
  reserveUnit(id: string, cartId: string): Promise<{ unitId: string; reservedUntil: string }>;
  releaseUnit(id: string, cartId: string): Promise<void>;
  inspectionQueue(branchId?: string): Promise<InspectionQueueRow[]>;
  inspect(unitId: string, i: InspectionInput): Promise<InspectionResult>;
  refurbishments(f?: { status?: string; technicianId?: string; branchId?: string }): Promise<RefurbOrderRow[]>;
  refurbishment(id: string): Promise<{ order: RefurbOrderRow; costs: RefurbCost[] }>;
  updateRefurbishment(id: string, r: { technicianId: string | null; problem: string | null; workDone: string | null; version: number }): Promise<void>;
  addRefurbCost(id: string, c: RefurbCostInput): Promise<{ id: string; amount: number; unitCost: number }>;
  reverseRefurbCost(id: string, costId: string, reason: string): Promise<{ id: string; amount: number; unitCost: number }>;
  completeRefurbishment(id: string, c: { condition: 'refurbished' | 'damaged'; gradeId: string | null; sellingPrice?: number | null; warrantyDays?: number | null; workDone?: string | null; version?: number }): Promise<{ id: string; number: string; unitId: string; cost: number }>;
  deviceStockReport(branchId?: string): Promise<{ status: DeviceStatus; condition: DeviceCondition; gradeCode: string | null; units: number; cost: number; averageAgeDays: number; age0to30: number; age31to90: number; ageOver90: number }[]>;
  deviceProfitReport(f?: { from?: string; to?: string; branchId?: string }): Promise<{ number: string; saleDate: string; unitId: string; descriptionEn: string; identifier: string | null; net: number; cost: number; profit: number; returned: number }[]>;
  refurbishmentReport(f?: { from?: string; to?: string; branchId?: string }): Promise<{ technician: string | null; orders: number; completed: number; parts: number; labour: number; external: number; total: number }[]>;
}

type Http = { get: <T>(p: string) => Promise<T>; post: <T>(p: string, b?: unknown) => Promise<T>; put: <T>(p: string, b: unknown) => Promise<T>; del: <T>(p: string) => Promise<T> };

const qs = (o: Record<string, string | number | boolean | undefined | null>) => {
  const p = Object.entries(o).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`);
  return p.length ? `?${p.join('&')}` : '';
};

export function deviceMethods(h: Http): DeviceApi {
  const { get, post, put, del } = h;
  return {
    devices: (f) => get(`/api/v1/devices${qs(f)}`),
    deviceLookup: (code) => get(`/api/v1/devices/lookup/${encodeURIComponent(code)}`),
    device: (id) => get(`/api/v1/devices/${id}`),
    setDevicePrice: (id, p) => post(`/api/v1/devices/${id}/price`, p),
    holdDevice: (id, reason) => post(`/api/v1/devices/${id}/hold`, { reason }),
    releaseDevice: (id, reason) => post(`/api/v1/devices/${id}/release`, { reason }),
    writeOffDevice: (id, w) => post(`/api/v1/devices/${id}/write-off`, w),
    deviceSettings: () => get('/api/v1/devices/settings'),
    saveDeviceSettings: (s) => put('/api/v1/devices/settings', s),
    grades: () => get('/api/v1/devices/grades'),
    createGrade: (g) => post('/api/v1/devices/grades', g),
    updateGrade: (id, g) => put(`/api/v1/devices/grades/${id}`, g),
    checklists: () => get('/api/v1/devices/checklists'),
    deviceChecklist: (unitId) => get(`/api/v1/devices/${unitId}/checklist`),
    createChecklist: (c) => post('/api/v1/devices/checklists', c),
    updateChecklist: (id, c) => put(`/api/v1/devices/checklists/${id}`, c),
    posUnitByCode: (code, variantId) => get(`/api/v1/pos/units/code/${encodeURIComponent(code)}${qs({ variantId })}`),
    posUnits: (variantId) => get(`/api/v1/pos/units${qs({ variantId })}`),
    reserveUnit: (id, cartId) => post(`/api/v1/pos/units/${id}/reserve`, { cartId }),
    releaseUnit: (id, cartId) => del(`/api/v1/pos/units/${id}/reserve${qs({ cartId })}`),
    inspectionQueue: (branchId) => get(`/api/v1/inspections/queue${qs({ branchId })}`),
    inspect: (unitId, i) => post(`/api/v1/devices/${unitId}/inspections`, i),
    refurbishments: (f) => get(`/api/v1/refurbishments${qs(f ?? {})}`),
    refurbishment: (id) => get(`/api/v1/refurbishments/${id}`),
    updateRefurbishment: (id, r) => put(`/api/v1/refurbishments/${id}`, r),
    addRefurbCost: (id, c) => post(`/api/v1/refurbishments/${id}/costs`, c),
    reverseRefurbCost: (id, costId, reason) => post(`/api/v1/refurbishments/${id}/costs/${costId}/reverse`, { reason }),
    completeRefurbishment: (id, c) => post(`/api/v1/refurbishments/${id}/complete`, c),
    deviceStockReport: (branchId) => get(`/api/v1/devices/reports/stock${qs({ branchId })}`),
    deviceProfitReport: (f) => get(`/api/v1/devices/reports/profit${qs(f ?? {})}`),
    refurbishmentReport: (f) => get(`/api/v1/devices/reports/refurbishment${qs(f ?? {})}`),
  };
}
