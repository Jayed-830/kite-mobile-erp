import { createContext, createElement, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { en, type Dictionary } from './en';
import { ar } from './ar';

export type Lang = 'en' | 'ar';
export const dictionaries: Record<Lang, Dictionary> = { en, ar };
export { en, ar, type Dictionary };

/** Looks up a dotted key ("login.title") and fills {placeholders}. Falls back to English, then the key. */
export function translate(lang: Lang, key: string, vars?: Record<string, string | number>): string {
  const pick = (d: Dictionary): unknown => key.split('.').reduce<unknown>((o, k) => (o && typeof o === 'object' ? (o as Record<string, unknown>)[k] : undefined), d);
  let text = pick(dictionaries[lang]);
  if (typeof text !== 'string') text = pick(en);
  if (typeof text !== 'string') return key;
  return vars ? text.replace(/\{(\w+)\}/g, (_, v: string) => String(vars[v] ?? `{${v}}`)) : text;
}

/** Every leaf key of a dictionary, e.g. "app.save". Used by the parity test. */
export function leafKeys(obj: unknown, prefix = ''): string[] {
  if (Array.isArray(obj) || typeof obj !== 'object' || obj === null) return [prefix];
  return Object.entries(obj).flatMap(([k, v]) => leafKeys(v, prefix ? `${prefix}.${k}` : k));
}

/**
 * Formats an amount held as the API's decimal string ("12.500") with exactly `decimals` places,
 * without converting through floating point (blueprint 22.2).
 */
export function formatAmount(value: string, decimals = 3, lang: Lang = 'en', digits: 'western' | 'eastern' = 'western'): string {
  const m = /^(-?)(\d+)(?:\.(\d+))?$/.exec(value.trim());
  if (!m) throw new Error(`Not a decimal amount: ${value}`);
  const [, sign, int, frac = ''] = m;
  if (frac.length > decimals) throw new Error(`Amount ${value} has more than ${decimals} decimals`);
  const grouped = (int ?? '0').replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  const text = `${sign}${grouped}${decimals > 0 ? '.' + frac.padEnd(decimals, '0') : ''}`;
  const out = lang === 'ar' && digits === 'eastern' ? text.replace(/\d/g, (d) => '٠١٢٣٤٥٦٧٨٩'[Number(d)] ?? d).replace(/,/g, '٬').replace('.', '٫') : text;
  return out;
}

/** Currency label for OMR in the current language. */
export const currencyLabel = (code: string, lang: Lang) => (code === 'OMR' && lang === 'ar' ? 'ر.ع.' : code);

interface I18nValue {
  lang: Lang;
  dir: 'ltr' | 'rtl';
  setLang: (l: Lang) => void;
  t: (key: string, vars?: Record<string, string | number>) => string;
  /** Picks the English or Arabic field of a record, e.g. pick(branch.nameEn, branch.nameAr). */
  pick: (en: string | null | undefined, ar: string | null | undefined) => string;
}

const I18nContext = createContext<I18nValue | null>(null);
const STORAGE_KEY = 'kite.lang';

function readStoredLang(): Lang {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    return v === 'ar' ? 'ar' : 'en';
  } catch {
    return 'en';
  }
}

export function I18nProvider({ children, initial }: { children: ReactNode; initial?: Lang }) {
  const [lang, setLangState] = useState<Lang>(initial ?? readStoredLang());
  const dir = lang === 'ar' ? 'rtl' : 'ltr';

  useEffect(() => {
    document.documentElement.lang = lang;
    document.documentElement.dir = dir;
  }, [lang, dir]);

  const setLang = useCallback((l: Lang) => {
    setLangState(l);
    try {
      localStorage.setItem(STORAGE_KEY, l);
    } catch {
      /* storage unavailable: keep the choice in memory */
    }
  }, []);

  const value = useMemo<I18nValue>(
    () => ({
      lang,
      dir,
      setLang,
      t: (key, vars) => translate(lang, key, vars),
      pick: (e, a) => (lang === 'ar' && a ? a : e ?? a ?? ''),
    }),
    [lang, dir, setLang],
  );
  return createElement(I18nContext.Provider, { value }, children);
}

export function useI18n(): I18nValue {
  const v = useContext(I18nContext);
  if (!v) throw new Error('useI18n must be used inside I18nProvider');
  return v;
}
