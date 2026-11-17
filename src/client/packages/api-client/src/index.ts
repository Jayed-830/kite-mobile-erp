// Typed client for the KITE server API (blueprint 22), written by hand. ADR 18 (revised in Phase 4): the server's
// minimal-API endpoints return untyped results, so a generated client would type the requests only. Instead a server
// test checks that every path this client calls exists on the server (ApiContractTests).
import { posMethods, type PosApi } from './pos';
import { deviceMethods, type DeviceApi } from './devices';
import { deviceIdentityMethods, type DeviceIdentityApi } from './deviceIdentity';
import { tradeInMethods, type TradeInApi } from './tradein';
import { repairMethods, type RepairApi } from './repairs';
import { creditMethods, type CreditApi } from './credit';
import { purchasingMethods, type PurchasingApi } from './purchasing';
export * from './pos';
export * from './devices';
export * from './deviceIdentity';
export * from './tradein';
export * from './repairs';
export * from './credit';
export * from './purchasing';

export interface Problem {
  title: string;
  status: number;
  code: string;
  field?: string | null;
  traceId?: string;
}

/** Error carrying the server's stable error code (e.g. AUTH-001) for messages and support. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly field?: string | null;
  readonly traceId?: string;

  constructor(p: Problem) {
    super(p.title);
    this.name = 'ApiError';
    this.status = p.status;
    this.code = p.code;
    this.field = p.field;
    this.traceId = p.traceId;
  }
}

export interface Tokens {
  accessToken: string;
  accessExpiresAt: string;
  refreshToken: string;
  refreshExpiresAt: string;
  mfaRequired: boolean;
  mustChangePassword: boolean;
}

export interface Branch { id: string; code: string; nameEn: string; nameAr: string; isHeadOffice: boolean; status: string }

export interface Me {
  user: { id: string; username: string; displayNameEn: string; displayNameAr: string | null; preferredLanguage: 'en' | 'ar'; totpEnabled: boolean; defaultBranchId: string | null };
  business: { legalNameEn: string; legalNameAr: string; currency: string; decimals: number; pricesIncludeTax: boolean };
  permissions: string[];
  allBranches: boolean;
  branches: Branch[];
  mfaPending: boolean;
  mustChangePassword: boolean;
  serverRole: string;
  /** On a branch server: the branch it serves (receiving, counts and terminals happen there). */
  serverBranchId?: string | null;
}

export interface SetupRequest {
  setupCode: string;
  business: {
    legalNameEn: string; legalNameAr: string; tradeNameEn?: string; tradeNameAr?: string; crNumber?: string; vatNumber?: string;
    addressEn?: string; addressAr?: string; phone?: string; email?: string; fiscalYearStartMonth: number; pricesIncludeTax: boolean;
  };
  headOffice: { code: string; nameEn: string; nameAr: string };
  owner: { username: string; displayNameEn: string; displayNameAr?: string; password: string; preferredLanguage: 'en' | 'ar' };
  serverRole: 'combined' | 'main';
  modules?: Record<string, boolean>;
}

export interface RoleGrant { roleId: string; branchId: string | null }
export interface UserRole { roleId: string; roleCode: string; roleNameEn: string; roleNameAr: string; branchId: string | null }
export interface User {
  id: string; username: string; displayNameEn: string; displayNameAr: string | null; email: string | null; phone: string | null;
  status: 'active' | 'disabled'; locked: boolean; totpEnabled: boolean; hasPin: boolean; preferredLanguage: 'en' | 'ar';
  defaultBranchId: string | null; lastLoginAt: string | null; version: number; roles: UserRole[];
}
export interface CreateUser {
  username: string; displayNameEn: string; displayNameAr?: string | null; email?: string | null; phone?: string | null;
  temporaryPassword: string; preferredLanguage: 'en' | 'ar'; defaultBranchId?: string | null; roles: RoleGrant[];
}
export interface UpdateUser {
  displayNameEn: string; displayNameAr?: string | null; email?: string | null; phone?: string | null; preferredLanguage: 'en' | 'ar';
  defaultBranchId?: string | null; status: 'active' | 'disabled'; roles: RoleGrant[]; version: number;
}
export interface Role { id: string; code: string; nameEn: string; nameAr: string; isSystem: boolean; version: number; permissions: string[] }
export interface Permission { code: string; moduleCode: string; descriptionEn: string; descriptionAr: string }
export interface Business {
  id: string; legalNameEn: string; legalNameAr: string; tradeNameEn: string | null; tradeNameAr: string | null; crNumber: string | null;
  vatNumber: string | null; countryCode: string; baseCurrency: string; currencyDecimals: number; timeZone: string; fiscalYearStartMonth: number;
  pricesIncludeTax: boolean; addressEn: string | null; addressAr: string | null; phone: string | null; email: string | null; version: number;
}
export type UpdateBusiness = Omit<Business, 'id' | 'countryCode' | 'baseCurrency' | 'currencyDecimals' | 'timeZone' | 'fiscalYearStartMonth'>;
export interface Module { code: string; nameEn: string; nameAr: string; alwaysOn: boolean; enabled: boolean; dependsOn: string[]; locked?: 'nextdab' | 'licence' | null }
export interface Terminal {
  id: string; branchId: string; code: string; name: string; kind: 'windows' | 'browser'; status: 'pending' | 'active' | 'revoked';
  emergencyEnabled: boolean; requestedAt: string; approvedAt: string | null; version: number;
}
export interface TaxCode {
  id: string; code: string; nameEn: string; nameAr: string; kind: string; status: string; needsAccountantReview: boolean;
  reviewedAt: string | null; rates: { ratePercent: number; effectiveFrom: string; effectiveTo: string | null }[];
}
export interface Account { id: string; code: string; nameEn: string; nameAr: string; type: string; parentId: string | null; isPostable: boolean; isControl: boolean; status: string }
export interface AuditRow { seq: number; occurredAt: string; userId: string | null; username: string | null; branchId: string | null; action: string; entityType: string | null; entityId: string | null; reason: string | null }
export interface AuditVerify { ok: boolean; rowsChecked: number; firstBrokenSeq: number | null }

// ---- Phase 3: branches and servers, licence, backups ----
export interface BranchAdmin { id: string; code: string; nameEn: string; nameAr: string; addressEn: string | null; addressAr: string | null; phone: string | null; isHeadOffice: boolean; status: 'active' | 'inactive'; version: number }
export interface ServerInfo {
  id: string; branchId: string | null; branchCode: string | null; role: 'combined' | 'main' | 'branch'; name: string; isSelf: boolean; status: 'active' | 'revoked' | string;
  appVersion: string | null; lastSeenAt: string | null; lastIp: string | null; installationId: string | null; certificateSerial: string | null; certificateExpires: string | null;
  online: boolean; heartbeat: { pending?: number; lastSeq?: number; backupAt?: string | null; backupStatus?: string | null } | null;
}
export interface RegistrationTokenRow { id: string; branchId: string; branchCode: string; expiresAt: string; createdAt: string; usedAt: string | null; cancelledAt: string | null }
export interface ServersOverview {
  self: { serverId: string; role: 'combined' | 'main' | 'branch'; installationId: string | null; version: string; ca: { fingerprintCode: string; hosts: string[] } | null };
  link: { mainUrl: string | null; lastOkAt: string | null; lastError: string | null; revoked: boolean } | null;
  servers: ServerInfo[]; tokens: RegistrationTokenRow[]; branches: BranchAdmin[]; syncPort: number;
}
export interface NewBranch { code: string; nameEn: string; nameAr: string; addressEn?: string | null; addressAr?: string | null; phone?: string | null }
export interface RegistrationToken { id: string; token: string; expiresAt: string; fingerprintCode: string; hosts: string[]; port: number }
export type LicenceState = 'none' | 'active' | 'expiring' | 'grace' | 'expired' | 'suspended';
export interface NextdabMessage { id: string; title: string | null; text: string | null; titleAr: string | null; textAr: string | null; level: 'info' | 'important'; audience: 'all' | 'admin'; until: string | null; receivedAt: string }
export interface LicenceBanner {
  state: LicenceState; daysLeft: number | null; endsOn: string | null; validUntil: string | null; kind: 'trial' | 'subscription' | null;
  canTransact: boolean; suspendMessage: string | null; suspendMessageAr: string | null; messages: NextdabMessage[];
}
export interface LicenceTerms { id: string; installationId: string; kind: 'trial' | 'subscription'; customer: string; validUntil: string; graceDays: number; branchesAllowed: number; terminalsAllowed: number; modules: string[]; issuedAt: string }
export interface LicenceDetails {
  state: LicenceState; terms: LicenceTerms | null; daysLeft: number | null; endsOn: string | null; branchesAllowed: number; terminalsAllowed: number; canTransact: boolean;
  suspendMessage: string | null; suspendMessageAr: string | null; branchesUsed: number; terminalsUsed: number; installationId: string; role: string; keyConfigured: boolean;
  connection: { lastCheckinAt: string | null; lastCheckinOkAt: string | null; lastError: string | null; nextCheckinAt: string | null; modulesOff: string[] } | null;
  history: { id: string; kind: string; customer: string; validUntil: string; source: string; importedAt: string; isCurrent: boolean }[];
}
export interface BackupRun {
  id: string; kind: 'logical' | 'base' | 'verify' | 'offsite' | string; status: 'running' | 'succeeded' | 'failed' | string; trigger: string; startedAt: string; finishedAt: string | null;
  fileName: string | null; sizeBytes: number | null; sha256: string | null; checks: Record<string, unknown> | null; error: string | null;
}
export interface BackupStatus {
  configured: boolean; enabled: boolean; running: string | null; folder: string | null; offsiteFolder: string | null;
  schedule: { logicalAt: string; baseAt: string; baseBackups: boolean; verifyDay: string; verifyAt: string };
  lastLogicalOk: string | null; lastBaseOk: string | null; lastVerifyOk: string | null; lastOffsiteOk: string | null; alert: boolean; alertAfterHours: number;
  wal: { mode: string; archived: number; lastArchivedAt: string | null; failed: number; lastFailedAt: string | null } | null;
  keyProtection: string; recoveryKeyShownAt: string | null; runs: BackupRun[];
}
export interface JoinRequest { mainUrl: string; setupCode: string; token: string; confirmedFingerprint: string; serverName?: string }

/** Everything the web app calls. The real client and the in-memory mock both implement it. */
export interface KiteApi extends PosApi, DeviceApi, DeviceIdentityApi, TradeInApi, RepairApi, CreditApi, PurchasingApi {
  setupStatus(): Promise<{ configured: boolean; role: string | null }>;
  setup(req: SetupRequest): Promise<{ businessId: string; branchId: string; ownerUserId: string }>;
  login(username: string, password: string): Promise<Tokens>;
  mfa(code: string): Promise<void>;
  me(): Promise<Me>;
  logout(): Promise<void>;
  changePassword(currentPassword: string, newPassword: string): Promise<void>;
  setPin(pin: string, currentPassword: string): Promise<void>;
  startTotp(): Promise<{ secret: string; otpauthUri: string }>;
  enableTotp(code: string): Promise<void>;
  disableTotp(currentPassword: string): Promise<void>;
  users(): Promise<User[]>;
  createUser(u: CreateUser): Promise<User>;
  updateUser(id: string, u: UpdateUser): Promise<User>;
  unlockUser(id: string): Promise<void>;
  resetPassword(id: string, temporaryPassword: string): Promise<void>;
  roles(): Promise<Role[]>;
  permissions(): Promise<Permission[]>;
  createRole(r: { code: string; nameEn: string; nameAr: string; permissions: string[] }): Promise<Role>;
  updateRole(id: string, r: { nameEn: string; nameAr: string; permissions: string[]; version: number }): Promise<Role>;
  branches(): Promise<Branch[]>;
  business(): Promise<Business>;
  updateBusiness(b: UpdateBusiness): Promise<Business>;
  modules(): Promise<Module[]>;
  updateModules(changes: Record<string, boolean>): Promise<Module[]>;
  terminals(): Promise<Terminal[]>;
  requestTerminal(t: { branchId: string; code: string; name: string; kind: 'windows' | 'browser'; deviceKey?: string }): Promise<{ id: string }>;
  approveTerminal(id: string, emergencyEnabled: boolean): Promise<Terminal>;
  revokeTerminal(id: string): Promise<void>;
  taxCodes(): Promise<TaxCode[]>;
  reviewTaxCode(id: string): Promise<void>;
  accounts(): Promise<Account[]>;
  audit(before?: number): Promise<AuditRow[]>;
  verifyAudit(): Promise<AuditVerify>;
  // Phase 3
  joinInspect(mainUrl: string, setupCode: string): Promise<{ businessName: string; fingerprintCode: string; fingerprint: string }>;
  join(req: JoinRequest): Promise<{ branchId: string; businessName: string }>;
  servers(): Promise<ServersOverview>;
  createBranch(b: NewBranch): Promise<{ id: string; code: string }>;
  updateBranch(id: string, b: Omit<BranchAdmin, 'id' | 'code' | 'isHeadOffice'>): Promise<void>;
  createRegistrationToken(branchId: string): Promise<RegistrationToken>;
  cancelRegistrationToken(id: string): Promise<void>;
  revokeServer(id: string, reason: 'server_replaced' | 'key_compromise'): Promise<void>;
  caRecoveryCopy(password: string, passphrase: string): Promise<string>;
  licenceStatus(): Promise<LicenceBanner>;
  licence(): Promise<LicenceDetails>;
  importLicence(fileText: string): Promise<{ id: string; kind: string; validUntil: string }>;
  licenceCheckin(): Promise<void>;
  backups(): Promise<BackupStatus>;
  runBackup(kind: 'logical' | 'base' | 'verify'): Promise<void>;
  backupRecoveryKey(password: string): Promise<{ recoveryKey: string; keyId: string }>;
  /** Called when the session ends (refresh failed); the app returns to sign-in. */
  onSessionEnded?: () => void;
}

/**
 * HTTP client. The access token lives in memory only; the refresh token is an httpOnly cookie
 * set by the server, so a page script cannot read it (blueprint 18.2).
 */
export function createHttpApi(baseUrl = ''): KiteApi {
  let accessToken: string | null = null;
  let refreshing: Promise<boolean> | null = null;

  const api: KiteApi = {} as KiteApi;

  async function raw(method: string, path: string, body?: unknown): Promise<Response> {
    const headers: Record<string, string> = { Accept: 'application/json' };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
    try {
      return await fetch(baseUrl + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), credentials: 'include' });
    } catch {
      throw new ApiError({ title: 'Cannot reach the KITE server.', status: 0, code: 'NET-000' });
    }
  }

  async function refresh(): Promise<boolean> {
    refreshing ??= (async () => {
      const r = await raw('POST', '/api/v1/auth/refresh', {});
      if (!r.ok) return false;
      const t = (await r.json()) as Tokens;
      accessToken = t.accessToken;
      return true;
    })().finally(() => {
      refreshing = null;
    });
    return refreshing;
  }

  async function request<T>(method: string, path: string, body?: unknown, retry = true): Promise<T> {
    const r = await raw(method, path, body);
    if (r.status === 401 && retry && !path.startsWith('/api/v1/auth/login') && !path.startsWith('/api/v1/auth/refresh')) {
      const problem = await r.clone().json().catch(() => null) as Problem | null;
      if (!problem || problem.code === 'AUTH-401') {
        if (await refresh()) return request<T>(method, path, body, false);
        accessToken = null;
        api.onSessionEnded?.();
      }
    }
    if (!r.ok) {
      const p = (await r.json().catch(() => null)) as Problem | null;
      throw new ApiError(p ?? { title: r.statusText || 'Request failed', status: r.status, code: `HTTP-${r.status}` });
    }
    if (r.status === 204 || r.status === 202) return undefined as T;
    const text = await r.text();
    if (!(r.headers.get('Content-Type') ?? 'application/json').includes('json')) return text as T;
    return (text ? JSON.parse(text) : undefined) as T;
  }

  const get = <T>(p: string) => request<T>('GET', p);
  const post = <T>(p: string, b?: unknown) => request<T>('POST', p, b ?? {});
  const put = <T>(p: string, b: unknown) => request<T>('PUT', p, b);
  const del = <T>(p: string) => request<T>('DELETE', p);
  async function blob(p: string): Promise<Blob> {
    let r = await raw('GET', p);
    if (r.status === 401 && await refresh()) r = await raw('GET', p);
    if (!r.ok) {
      const pr = (await r.json().catch(() => null)) as Problem | null;
      throw new ApiError(pr ?? { title: r.statusText || 'Request failed', status: r.status, code: `HTTP-${r.status}` });
    }
    return r.blob();
  }

  Object.assign(api, {
    setupStatus: () => get('/api/v1/setup/status'),
    setup: (req: SetupRequest) => post('/api/v1/setup', req),
    async login(username: string, password: string) {
      const t = await post<Tokens>('/api/v1/auth/login', { username, password });
      accessToken = t.accessToken;
      return t;
    },
    mfa: (code: string) => post('/api/v1/auth/mfa', { code }),
    me: () => get('/api/v1/auth/me'),
    async logout() {
      try { await post('/api/v1/auth/logout'); } finally { accessToken = null; }
    },
    changePassword: (currentPassword: string, newPassword: string) => post('/api/v1/auth/password', { currentPassword, newPassword }),
    setPin: (pin: string, currentPassword: string) => post('/api/v1/auth/pin', { pin, currentPassword }),
    startTotp: () => post('/api/v1/auth/2fa/setup'),
    enableTotp: (code: string) => post('/api/v1/auth/2fa/enable', { code }),
    disableTotp: (currentPassword: string) => post('/api/v1/auth/2fa/disable', { currentPassword }),
    users: () => get('/api/v1/users'),
    createUser: (u: CreateUser) => post('/api/v1/users', u),
    updateUser: (id: string, u: UpdateUser) => put(`/api/v1/users/${id}`, u),
    unlockUser: (id: string) => post(`/api/v1/users/${id}/unlock`),
    resetPassword: (id: string, temporaryPassword: string) => post(`/api/v1/users/${id}/reset-password`, { temporaryPassword }),
    roles: () => get('/api/v1/roles'),
    permissions: () => get('/api/v1/permissions'),
    createRole: (r: unknown) => post('/api/v1/roles', r),
    updateRole: (id: string, r: unknown) => put(`/api/v1/roles/${id}`, r),
    branches: () => get('/api/v1/branches'),
    business: () => get('/api/v1/settings/business'),
    updateBusiness: (b: UpdateBusiness) => put('/api/v1/settings/business', b),
    modules: () => get('/api/v1/settings/modules'),
    updateModules: (changes: Record<string, boolean>) => put('/api/v1/settings/modules', { changes }),
    terminals: () => get('/api/v1/terminals'),
    requestTerminal: (t: unknown) => post('/api/v1/terminals', t),
    approveTerminal: (id: string, emergencyEnabled: boolean) => post(`/api/v1/terminals/${id}/approve`, { emergencyEnabled }),
    revokeTerminal: (id: string) => post(`/api/v1/terminals/${id}/revoke`),
    taxCodes: () => get('/api/v1/tax/codes'),
    reviewTaxCode: (id: string) => post(`/api/v1/tax/codes/${id}/review`),
    accounts: () => get('/api/v1/accounting/accounts'),
    audit: (before?: number) => get(`/api/v1/audit?limit=50${before ? `&before=${before}` : ''}`),
    verifyAudit: () => post('/api/v1/audit/verify'),
    joinInspect: (mainUrl: string, setupCode: string) => post('/api/v1/setup/join/inspect', { mainUrl, setupCode }),
    join: (req: JoinRequest) => post('/api/v1/setup/join', req),
    servers: () => get('/api/v1/servers'),
    createBranch: (b: NewBranch) => post('/api/v1/branches', b),
    updateBranch: (id: string, b: unknown) => put(`/api/v1/branches/${id}`, b),
    createRegistrationToken: (branchId: string) => post(`/api/v1/branches/${branchId}/registration-tokens`),
    cancelRegistrationToken: (id: string) => post(`/api/v1/registration-tokens/${id}/cancel`),
    revokeServer: (id: string, reason: string) => post(`/api/v1/servers/${id}/revoke`, { reason }),
    caRecoveryCopy: (password: string, passphrase: string) => post('/api/v1/pki/recovery-copy', { password, passphrase }),
    licenceStatus: () => get('/api/v1/licence/status'),
    licence: () => get('/api/v1/licence'),
    importLicence: (fileText: string) => post('/api/v1/licence/import', { fileText }),
    licenceCheckin: () => post('/api/v1/licence/checkin'),
    backups: () => get('/api/v1/backups'),
    runBackup: (kind: string) => post('/api/v1/backups/run', { kind }),
    backupRecoveryKey: (password: string) => post('/api/v1/backups/recovery-key', { password }),
    ...posMethods({ get, post, put, del, blob, setToken: (t: string) => { accessToken = t; } }),
    ...deviceMethods({ get, post, put, del }),
    ...deviceIdentityMethods({ get, post, put, del }),
    ...tradeInMethods({ get, post, put, blob }),
    ...repairMethods({ get, post, put, blob }),
    ...creditMethods({ get, post, put, blob }),
    ...purchasingMethods({ get, post, put, blob }),
  });
  return api;
}

/** True when the user holds the permission (the UI hides what the server would refuse anyway). */
export const can = (me: Me | null | undefined, permission: string) => !!me?.permissions.includes(permission);
