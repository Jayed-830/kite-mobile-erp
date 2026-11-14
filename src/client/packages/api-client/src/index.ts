// Typed client for the KITE server API (blueprint 22). Phase 2 endpoints are written by hand;
// from Phase 3 the types are generated from the server's OpenAPI document.

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
export interface Module { code: string; nameEn: string; nameAr: string; alwaysOn: boolean; enabled: boolean; dependsOn: string[] }
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

/** Everything the web app calls. The real client and the in-memory mock both implement it. */
export interface KiteApi {
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
  requestTerminal(t: { branchId: string; code: string; name: string; kind: 'windows' | 'browser' }): Promise<{ id: string }>;
  approveTerminal(id: string, emergencyEnabled: boolean): Promise<Terminal>;
  revokeTerminal(id: string): Promise<void>;
  taxCodes(): Promise<TaxCode[]>;
  reviewTaxCode(id: string): Promise<void>;
  accounts(): Promise<Account[]>;
  audit(before?: number): Promise<AuditRow[]>;
  verifyAudit(): Promise<AuditVerify>;
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
    if (r.status === 204) return undefined as T;
    const text = await r.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  const get = <T>(p: string) => request<T>('GET', p);
  const post = <T>(p: string, b?: unknown) => request<T>('POST', p, b ?? {});
  const put = <T>(p: string, b: unknown) => request<T>('PUT', p, b);

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
  });
  return api;
}

/** True when the user holds the permission (the UI hides what the server would refuse anyway). */
export const can = (me: Me | null | undefined, permission: string) => !!me?.permissions.includes(permission);
