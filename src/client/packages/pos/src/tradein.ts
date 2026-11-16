// Trade-in arithmetic, the same rules as the server's Kite.Sales.TradeInMath and an independent Python implementation
// (tests/vectors/tradein-math-vectors.json keeps all three equal). Amounts in baisa; rounding half away from zero.
import { mulDiv, rateUnits, sum, toBaisa, type Baisa } from './money';
import { PosError } from './errors';

export interface Deduction { code: string; kind: 'amount' | 'percent' | string; value: string | number }
export interface Valuation { guide: Baisa | null; amounts: Baisa[]; total: Baisa; offered: Baisa | null }

/** Percentages as hundredths of a basis point, like tax rates: 10 → 100000n. */
const pct = (v: string | number) => rateUnits(typeof v === 'number' ? v : v);

/**
 * Values a trade-in: a percentage deduction is that share of the guide value, rounded to the baisa; the offer is the guide
 * value less every deduction, never below zero. Without a guide value there is no offer, and only fixed deductions.
 */
export function valueTradeIn(guide: string | number | null, deductions: Deduction[]): Valuation {
  const g = guide == null ? null : toBaisa(guide);
  if (g != null && g < 0n) throw new PosError('VAL-001', 'The guide value is zero or more.');
  const amounts = deductions.map((d) => {
    if (d.kind === 'amount') {
      const a = toBaisa(d.value);
      if (a < 0n) throw new PosError('VAL-001', `Deduction ${d.code} cannot be negative.`);
      return a;
    }
    if (d.kind !== 'percent') throw new PosError('VAL-001', 'A deduction is an amount or a percent.');
    const p = pct(d.value);
    if (p < 0n || p > 1_000_000n) throw new PosError('VAL-001', `Deduction ${d.code} is 0 to 100%.`);
    if (g == null) throw new PosError('TRD-008', `Deduction ${d.code} is a percentage of the guide value, and there is no guide value.`);
    return mulDiv(g, p, 1_000_000n);
  });
  const total = sum(amounts);
  return { guide: g, amounts, total, offered: g == null ? null : (g - total > 0n ? g - total : 0n) };
}

/** Whether staff without TRADEIN.APPROVE need a manager: no offer to compare with, above the staff limit, or above offer + X%. */
export function needsApproval(agreed: Baisa, offered: Baisa | null, staffLimit: string | number, overGuidePercent: string | number): boolean {
  if (agreed < 0n) throw new PosError('VAL-001', 'The agreed value is zero or more.');
  if (offered == null) return true;
  if (agreed > toBaisa(staffLimit)) return true;
  return agreed > mulDiv(offered, 1_000_000n + pct(overGuidePercent), 1_000_000n);
}

/** VAT inside a margin-scheme line: (price − purchase price) × r / (100 + r), never below zero; the price is what the customer pays. */
export function marginLine(gross: Baisa, purchase: Baisa, rate: number | string): { net: Baisa; tax: Baisa; gross: Baisa; margin: Baisa } {
  if (gross < 0n || purchase < 0n) throw new PosError('VAL-001', 'Margin amounts cannot be negative.');
  const r = rateUnits(rate);
  const margin = gross - purchase;
  const tax = margin > 0n ? mulDiv(margin, r, 1_000_000n + r) : 0n;
  return { net: gross - tax, tax, gross, margin };
}

/** How much of a trade-in's balance a sale takes (never more than the sale's balance), and what stays. */
export function applyTradeIn(outstanding: Baisa, due: Baisa): { applied: Baisa; left: Baisa } {
  if (outstanding < 0n || due < 0n) throw new PosError('VAL-001', 'Amounts cannot be negative.');
  const applied = outstanding < due ? outstanding : due;
  return { applied, left: outstanding - applied };
}

/** The last four letters or digits of a civil ID, for showing it masked. */
export function last4(civilId: string): string {
  const clean = civilId.replace(/[^0-9A-Za-z]/g, '').toUpperCase();
  return clean.length <= 4 ? clean : clean.slice(-4);
}
