// Phase 6 API: IMEI and serial number management - checks while scanning, correcting an identifier, reversing a receipt,
// IMEI conflicts, the blocked list, model hints (TAC), serial formats, the business-wide look-up, the link with head
// office, and counting devices by IMEI.
import type { DeviceCondition, DeviceStatus } from './devices';

export interface CodeCheck {
  code: string; kind: 'imei' | 'serial' | 'invalid_imei' | 'unknown'; value: string; problem: { code: string; message: string } | null;
  hint: string | null; hintMismatch?: boolean; centralChecked?: boolean;
}
export interface ConflictSide {
  unitId: string; branchCode: string; branchNameEn: string; branchNameAr: string; descriptionEn: string; descriptionAr: string; unitStatus: DeviceStatus;
  receivedAt: string | null; outcome: 'kept' | 'corrected' | 'reversed' | null; outcomeAt: string | null; local: boolean; rowStatus: string;
}
export interface ImeiConflict {
  id: string; kind: string; value: string | null; status: 'open' | 'resolved'; openedAt: string; resolvedAt: string | null; resolution: string | null; note: string | null;
  sides: ConflictSide[];
}
export interface BlockedImei {
  id: string; kind: 'imei' | 'serial'; value: string; reason: string; reference: string | null; status: 'active' | 'removed'; addedAt: string; addedBy: string | null;
  removedAt: string | null; removedReason: string | null; devicesHere: number;
}
export interface HistoryLookup {
  kind: 'imei' | 'serial'; value: string;
  local: { id: string; branchCode: string; descriptionEn: string; descriptionAr: string; status: DeviceStatus; condition: DeviceCondition; receivedAt: string; soldOn: string | null;
           soldAt: string | null; warrantyUntil: string | null; identifierStatus: 'active' | 'retired'; visible: boolean }[];
  headOffice: { holders: { branchCode: string; branchNameEn: string; branchNameAr: string; unitId: string; unitStatus: DeviceStatus; condition: DeviceCondition; descriptionEn: string;
                           descriptionAr: string; receivedAt: string | null; rowStatus: 'active' | 'conflict' | 'retired' }[]; blockedReason: string | null; openConflicts: string[] } | null;
  headOfficeReachable: boolean; blocked: string | null; model: string | null;
}
export interface CentralStatus {
  role: 'combined' | 'main' | 'branch'; isHeadOffice: boolean; headOfficeReachable: boolean; lastContactAt: string | null; pending: number; unitsInConflict: number;
  openConflicts: number | null; blocked: number; lastPushAt: string | null; lastPushOkAt: string | null; lastPushError: string | null; strict: boolean;
}
export type CountResult = 'here' | 'other_model' | 'elsewhere' | 'not_in_stock' | 'unknown';
export interface DeviceCountRow {
  id: string; number: string; status: 'open' | 'posted' | 'cancelled'; branchCode: string; branchId: string; variantId: string | null; variantDescription: string | null;
  startedAt: string; startedBy: string; postedAt: string | null; scans: number; result: string | null;
}
export interface CountedDevice { id: string; descriptionEn: string; descriptionAr: string; status: DeviceStatus; heldFromStatus: DeviceStatus | null; condition: DeviceCondition; cost: number; identifier: string | null }
export interface CountScan { id: string; kind: 'imei' | 'serial'; value: string; entry: 'scan' | 'manual'; result: CountResult; unitId: string | null; scannedAt: string; scannedBy: string; descriptionEn: string | null }
export interface DeviceCountDetail {
  count: { id: string; number: string; status: 'open' | 'posted' | 'cancelled'; branchId: string; branchCode: string; variantId: string | null; startedAt: string; postedAt: string | null; note: string | null; result: unknown };
  expected: CountedDevice[]; scans: CountScan[]; found: number; missing: CountedDevice[]; extra: CountScan[];
}
export interface SerialFormat { id: string; nameEn: string; nameAr: string; serialPattern: string | null }

export interface DeviceIdentityApi {
  checkIdentifiers(c: { codes: string[]; variantId?: string | null; exceptUnitId?: string | null }): Promise<CodeCheck[]>;
  correctIdentifiers(unitId: string, c: { imei1?: string | null; imei2?: string | null; serial?: string | null; reason: string; entry?: 'scan' | 'manual' }): Promise<{ unitId: string; imei1: string | null; imei2: string | null; serial: string | null }>;
  reverseReceipt(unitId: string, reason: string): Promise<{ id: string; number: string; cost: number }>;
  deviceConflicts(status?: 'open' | 'resolved'): Promise<ImeiConflict[]>;
  blockedImeis(f?: { q?: string; status?: 'active' | 'removed' }): Promise<BlockedImei[]>;
  addBlockedImei(b: { value: string; reason: string; reference?: string | null }): Promise<{ id: string; kind: string; value: string }>;
  removeBlockedImei(id: string, reason: string): Promise<void>;
  tacSummary(): Promise<{ count: number; lastImportedAt: string | null }>;
  tacLookup(code: string): Promise<{ tac: string; brand: string; model: string }>;
  importTac(csv: string): Promise<{ imported: number; skipped: number }>;
  serialFormats(): Promise<SerialFormat[]>;
  setSerialFormat(brandId: string, pattern: string | null): Promise<void>;
  historyLookup(code: string): Promise<HistoryLookup>;
  centralStatus(): Promise<CentralStatus>;
  deviceCounts(branchId?: string): Promise<DeviceCountRow[]>;
  startDeviceCount(c: { branchId: string; variantId?: string | null; note?: string | null }): Promise<{ id: string; number: string }>;
  deviceCount(id: string): Promise<DeviceCountDetail>;
  scanDeviceCount(id: string, s: { code: string; entry?: 'scan' | 'manual' }): Promise<{ id: string; kind: string; value: string; result: CountResult; message: string; unitId: string | null }>;
  removeCountScan(id: string, scanId: string): Promise<void>;
  postDeviceCount(id: string, p: { writeOffMissing: boolean; reason?: string | null }): Promise<{ expected: number; found: number; missing: CountedDevice[]; extra: CountScan[]; writtenOff: unknown[]; notWrittenOff: unknown[] }>;
  cancelDeviceCount(id: string): Promise<void>;
}

type Http = { get: <T>(p: string) => Promise<T>; post: <T>(p: string, b?: unknown) => Promise<T>; put: <T>(p: string, b: unknown) => Promise<T>; del: <T>(p: string) => Promise<T> };

const qs = (o: Record<string, string | number | boolean | undefined | null>) => {
  const p = Object.entries(o).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`);
  return p.length ? `?${p.join('&')}` : '';
};

export function deviceIdentityMethods(h: Http): DeviceIdentityApi {
  const { get, post, put, del } = h;
  return {
    checkIdentifiers: (c) => post('/api/v1/devices/identifiers/check', c),
    correctIdentifiers: (unitId, c) => post(`/api/v1/devices/${unitId}/identifiers`, c),
    reverseReceipt: (unitId, reason) => post(`/api/v1/devices/${unitId}/reverse-receipt`, { reason }),
    deviceConflicts: (status) => get(`/api/v1/devices/conflicts${qs({ status })}`),
    blockedImeis: (f) => get(`/api/v1/devices/blocked${qs(f ?? {})}`),
    addBlockedImei: (b) => post('/api/v1/devices/blocked', b),
    removeBlockedImei: (id, reason) => post(`/api/v1/devices/blocked/${id}/remove`, { reason }),
    tacSummary: () => get('/api/v1/devices/tac'),
    tacLookup: (code) => get(`/api/v1/devices/tac/${encodeURIComponent(code)}`),
    importTac: (csv) => post('/api/v1/devices/tac/import', { csv }),
    serialFormats: () => get('/api/v1/devices/serial-formats'),
    setSerialFormat: (brandId, pattern) => put(`/api/v1/devices/serial-formats/${brandId}`, { pattern }),
    historyLookup: (code) => get(`/api/v1/devices/history-lookup/${encodeURIComponent(code)}`),
    centralStatus: () => get('/api/v1/devices/central-status'),
    deviceCounts: (branchId) => get(`/api/v1/devices/counts${qs({ branchId })}`),
    startDeviceCount: (c) => post('/api/v1/devices/counts', c),
    deviceCount: (id) => get(`/api/v1/devices/counts/${id}`),
    scanDeviceCount: (id, s) => post(`/api/v1/devices/counts/${id}/scans`, s),
    removeCountScan: (id, scanId) => del(`/api/v1/devices/counts/${id}/scans/${scanId}`),
    postDeviceCount: (id, p) => post(`/api/v1/devices/counts/${id}/post`, p),
    cancelDeviceCount: (id) => post(`/api/v1/devices/counts/${id}/cancel`),
  };
}

