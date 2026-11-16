import { formatAmount } from '@kite/i18n';
import { fromBaisa, toBaisa } from '@kite/pos';

/** Server amounts (JSON numbers with up to three decimals) shown with exactly three decimals, never via floating point maths. */
export const fmt = (v: number | null | undefined): string => (v == null ? '—' : formatAmount(fromBaisa(toBaisa(v))));
export const fmtB = (b: bigint): string => formatAmount(fromBaisa(b));
/** Parses what the user typed ("12.5") into a number for the API; null when empty or not an amount. */
export function parseAmount(s: string): number | null {
  const t = s.trim();
  if (!/^\d+(\.\d{1,3})?$/.test(t)) return null;
  return Number(t);
}
export const qty = (v: number) => (Number.isInteger(v) ? String(v) : v.toFixed(3).replace(/0+$/, ''));
