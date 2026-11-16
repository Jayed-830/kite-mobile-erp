// Commission arithmetic (blueprint 15.1, 15.2), the same rules as the server's Kite.Sales.CommissionMath and an independent
// Python implementation (tests/vectors/commission-math-vectors.json keeps all three equal). Amounts in baisa; rates and
// shares in percent with up to 4 decimals; each step rounded half away from zero to the baisa.
import { mulDiv, rateUnits, type Baisa } from './money';
import { PosError } from './errors';

export type CommissionBasisKind = 'fixed' | 'labour' | 'profit';
/** What a repair earned, excluding VAT, and what it cost. */
export interface CommissionBasis { labour: Baisa; partsRevenue: Baisa; partsCost: Baisa; external: Baisa }
export interface CommissionResult { base: Baisa; raw: Baisa; clamped: Baisa; amount: Baisa }

/** Repair profit: labour and parts revenue, less parts cost and outside work. */
export const repairProfit = (b: CommissionBasis): Baisa => b.labour + b.partsRevenue - b.partsCost - b.external;

/**
 * A fixed amount, or a rate of labour or of repair profit (never of a loss), then the rule's minimum and maximum per job,
 * then the technician's share.
 */
export function commission(basis: CommissionBasisKind, fixedAmount: Baisa | null, ratePercent: string | number | null, min: Baisa | null, max: Baisa | null,
  b: CommissionBasis, sharePercent: string | number): CommissionResult {
  const share = rateUnits(sharePercent);
  if (share <= 0n || share > 1_000_000n) throw new PosError('COM-003', 'A share is above 0% and at most 100%.');
  if (min != null && max != null && min > max) throw new PosError('COM-001', 'The minimum is above the maximum.');
  let base: Baisa;
  let raw: Baisa;
  if (basis === 'fixed') {
    if (fixedAmount == null || fixedAmount < 0n) throw new PosError('COM-001', 'A fixed commission has an amount of zero or more.');
    base = 0n;
    raw = fixedAmount;
  } else if (basis === 'labour' || basis === 'profit') {
    if (ratePercent == null) throw new PosError('COM-001', 'A rate is 0% to 100%.');
    const r = rateUnits(ratePercent);
    if (r < 0n || r > 1_000_000n) throw new PosError('COM-001', 'A rate is 0% to 100%.');
    base = basis === 'labour' ? b.labour : repairProfit(b);
    raw = mulDiv(r, base > 0n ? base : 0n, 1_000_000n);
  } else throw new PosError('COM-001', 'A rule pays a fixed amount, a share of labour, or a share of repair profit.');
  let clamped = raw;
  if (min != null && clamped < min) clamped = min;
  if (max != null && clamped > max) clamped = max;
  return { base, raw, clamped, amount: mulDiv(clamped, share, 1_000_000n) };
}

/** Shares of a job must make exactly 100%. */
export function sharesComplete(shares: (string | number)[]): boolean {
  return shares.length === 0 || shares.reduce<bigint>((a, s) => a + rateUnits(s), 0n) === 1_000_000n;
}
