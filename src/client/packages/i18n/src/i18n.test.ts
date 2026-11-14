import { readFileSync } from 'node:fs';
import { ar, en, formatAmount, leafKeys, translate } from './index';

describe('dictionaries', () => {
  it('English and Arabic have exactly the same keys', () => {
    expect(leafKeys(ar).sort()).toEqual(leafKeys(en).sort());
  });

  it('no Arabic string is empty or still in English', () => {
    const arabicLetters = /[؀-ۿ]/;
    const flat = (o: unknown, p = ''): [string, string][] =>
      typeof o === 'string' ? [[p, o]] : Object.entries(o as object).flatMap(([k, v]) => flat(v, p ? `${p}.${k}` : k));
    const missing = flat(ar).filter(([, v]) => !v.trim() || !arabicLetters.test(v));
    expect(missing).toEqual([]);
  });

  it('fills placeholders and falls back to the key when missing', () => {
    expect(translate('en', 'dashboard.welcome', { name: 'Salim' })).toBe('Welcome, Salim');
    expect(translate('ar', 'dashboard.welcome', { name: 'سالم' })).toBe('مرحبًا، سالم');
    expect(translate('en', 'no.such.key')).toBe('no.such.key');
  });
});

describe('server error codes', () => {
  it('every code the server can return has an English and an Arabic message', () => {
    const source: string = readFileSync('../server/Kite.Platform/Errors.cs', 'utf8') /* Vitest runs from src/client */;
    const codes = [...source.matchAll(/=\s*"([A-Z]{2,4}-\d{3})"/g)].map((m) => m[1] ?? "");
    expect(codes.length).toBeGreaterThan(20);
    const missing = codes.filter((c) => translate('ar', `errors.codes.${c}`) === `errors.codes.${c}` || !(c in ar.errors.codes));
    expect(missing).toEqual([]);
  });
});

describe('formatAmount', () => {
  it('shows OMR with exactly three decimals and grouping', () => {
    expect(formatAmount('12.5')).toBe('12.500');
    expect(formatAmount('1234567.125')).toBe('1,234,567.125');
    expect(formatAmount('-0.005')).toBe('-0.005');
  });

  it('never rounds: more decimals than allowed is an error', () => {
    expect(() => formatAmount('1.0005')).toThrow();
    expect(() => formatAmount('abc')).toThrow();
  });

  it('can show Eastern Arabic digits', () => {
    expect(formatAmount('1250.5', 3, 'ar', 'eastern')).toBe('١٬٢٥٠٫٥٠٠');
  });
});
