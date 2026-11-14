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
