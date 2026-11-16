import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError, type KiteApi, type Me } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert } from '@kite/ui';

const ApiContext = createContext<KiteApi | null>(null);

export function ApiProvider({ api, children }: { api: KiteApi; children: ReactNode }) {
  return <ApiContext.Provider value={api}>{children}</ApiContext.Provider>;
}

export function useApi(): KiteApi {
  const v = useContext(ApiContext);
  if (!v) throw new Error('useApi must be used inside ApiProvider');
  return v;
}

/** Codes whose server message carries details (which field, which rule) that the generic translation does not. */
const DETAILED = new Set(['VAL-001', 'GEN-409', 'AUTH-006', 'MOD-001']);

/**
 * Shows an API error in the user's language with the server's code for support.
 * English shows the server's own message. Arabic shows the translation for the code;
 * for codes with field-level detail, the server's English detail follows on a second line
 * (server messages are English-only in Phase 2).
 */
export function ErrorBox({ error }: { error: unknown }) {
  const { t, lang } = useI18n();
  if (!error) return null;
  if (error instanceof ApiError) {
    if (error.code === 'NET-000') return <Alert tone="error" code={error.code}>{t('errors.network')}</Alert>;
    const key = `errors.codes.${error.code}`;
    const translated = t(key);
    const known = translated !== key;
    if (lang === 'en' || !known) return <Alert tone="error" code={error.code}>{error.message || (known ? translated : t('errors.unknown'))}</Alert>;
    return (
      <Alert tone="error" code={error.code}>
        {translated}
        {DETAILED.has(error.code) && error.message && error.message !== t(key) && (
          <span className="ltr muted" style={{ display: 'block', fontSize: 13, marginBlockStart: 4 }} title={t('errors.detail')}>{error.message}</span>
        )}
      </Alert>
    );
  }
  return <Alert tone="error">{t('errors.unknown')}</Alert>;
}

type Stage = 'loading' | 'signedOut' | 'mfa' | 'changePassword' | 'signedIn';

interface AuthValue {
  stage: Stage;
  me: Me | null;
  signIn: (username: string, password: string) => Promise<void>;
  verifyMfa: (code: string) => Promise<void>;
  changePassword: (current: string, next: string) => Promise<void>;
  signOut: () => Promise<void>;
  reload: () => Promise<void>;
}

const AuthContext = createContext<AuthValue | null>(null);

/**
 * Sign-in state machine: password, then two-step code if the user has it on, then a forced
 * password change if a manager set the password (blueprint 18.2).
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const { setLang } = useI18n();
  const [stage, setStage] = useState<Stage>('loading');
  const [me, setMe] = useState<Me | null>(null);

  // Cached server data belongs to the user who loaded it. Shop terminals are shared, so the cache is
  // dropped whenever a session ends or a different user signs in.
  const signedOut = useCallback(() => {
    queryClient.clear();
    setMe(null);
    setStage('signedOut');
  }, [queryClient]);

  const load = useCallback(async () => {
    try {
      const m = await api.me();
      setMe(m);
      setStage(m.mfaPending ? 'mfa' : m.mustChangePassword ? 'changePassword' : 'signedIn');
    } catch {
      signedOut();
    }
  }, [api, signedOut]);

  useEffect(() => {
    api.onSessionEnded = signedOut;
    void load();
  }, [api, load, signedOut]);

  const value = useMemo<AuthValue>(() => ({
    stage,
    me,
    async signIn(username, password) {
      const tokens = await api.login(username, password);
      queryClient.clear();
      const m = await api.me();
      setMe(m);
      if (!tokens.mfaRequired && !tokens.mustChangePassword) setLang(m.user.preferredLanguage);
      setStage(tokens.mfaRequired ? 'mfa' : tokens.mustChangePassword ? 'changePassword' : 'signedIn');
    },
    async verifyMfa(code) {
      await api.mfa(code);
      await load();
    },
    async changePassword(current, next) {
      await api.changePassword(current, next);
      await load();
    },
    async signOut() {
      try { await api.logout(); } catch { /* already signed out */ }
      signedOut();
    },
    reload: load,
  }), [api, stage, me, load, setLang, queryClient, signedOut]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthValue {
  const v = useContext(AuthContext);
  if (!v) throw new Error('useAuth must be used inside AuthProvider');
  return v;
}

export function formatDateTime(iso: string | null | undefined, lang: 'en' | 'ar'): string {
  if (!iso) return '—';
  const d = new Date(iso.endsWith('Z') || iso.includes('+') ? iso : `${iso}Z`);
  return new Intl.DateTimeFormat(lang === 'ar' ? 'ar-OM-u-nu-latn' : 'en-GB', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Muscat' }).format(d);
}

export function formatDate(iso: string | null | undefined, lang: 'en' | 'ar'): string {
  if (!iso) return '—';
  return new Intl.DateTimeFormat(lang === 'ar' ? 'ar-OM-u-nu-latn' : 'en-GB', { dateStyle: 'medium', timeZone: 'UTC' }).format(new Date(iso.slice(0, 10)));
}

/** The branch the signed-in user works in here: a branch server's own branch, else the user's default, else the first they can see. */
export const workBranchId = (me: Me | null | undefined): string => me?.serverBranchId ?? me?.user.defaultBranchId ?? me?.branches[0]?.id ?? '';

/** Today in the business's time zone (Oman), as YYYY-MM-DD: business days (commission dates, invoices) are counted there. */
export function businessToday(offsetDays = 0): string {
  return new Intl.DateTimeFormat('sv-SE', { timeZone: 'Asia/Muscat' }).format(new Date(Date.now() + offsetDays * 864e5));
}
