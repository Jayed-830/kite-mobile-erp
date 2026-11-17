// Customer credit arithmetic (blueprint 14.2 to 14.5), the same rules as the server's Kite.Sales.CreditMath and an
// independent Python implementation (tests/vectors/credit-math-vectors.json keeps all three equal). Amounts in baisa.
import type { Baisa } from './money';

export type CreditOutcome = 'ok' | 'needs_approval' | 'approved' | 'over_cap' | 'hold' | 'closed';
export interface CreditDecision { outcome: CreditOutcome; overBy: Baisa }
export interface InstalmentLine { seq: number; due: string; amount: Baisa }
export interface Aging { current: Baisa; d1_30: Baisa; d31_60: Baisa; d61_90: Baisa; d90plus: Baisa; onAccount: Baisa; total: Baisa }

const max = (a: Baisa, b: Baisa) => (a > b ? a : b);
const min = (a: Baisa, b: Baisa) => (a < b ? a : b);

/** Online: the limit less head office's exposure and the branch's charges head office has not seen, never below zero. */
export const availableOnline = (limit: Baisa, exposure: Baisa, unsynced: Baisa): Baisa => max(0n, limit - exposure - unsynced);

/** Offline: the lower of the allowance and the limit less the last known exposure, each less charges since the last sync; capped per sale. */
export const availableOffline = (allowance: Baisa, limit: Baisa, lastExposure: Baisa, sinceSync: Baisa, cap: Baisa): Baisa =>
  max(0n, min(min(allowance - sinceSync, limit - lastExposure - sinceSync), cap));

/** No credit on hold or closed; over the available credit or overdue past the policy needs an approval; over the cap is refused. */
export function decideCredit(amount: Baisa, available: Baisa, overrideCap: Baisa, overdueDays: number, blockDays: number, status: string, hasApproval: boolean): CreditDecision {
  if (status === 'hold' || status === 'closed') return { outcome: status, overBy: 0n };
  const overBy = max(0n, amount - available);
  if (overBy > overrideCap) return { outcome: 'over_cap', overBy };
  if (overBy > 0n || overdueDays > blockDays) return { outcome: hasApproval ? 'approved' : 'needs_approval', overBy };
  return { outcome: 'ok', overBy };
}

const day = 86_400_000;
const toDay = (iso: string) => Date.UTC(+iso.slice(0, 4), +iso.slice(5, 7) - 1, +iso.slice(8, 10));
const iso = (t: number) => new Date(t).toISOString().slice(0, 10);

/** Equal instalments to the baisa, the spare baisa on the first; the first due after the terms, then at the interval. */
export function instalments(total: Baisa, count: number, saleDate: string, termsDays: number, intervalDays: number): InstalmentLine[] {
  if (!Number.isInteger(count) || count < 1) throw new RangeError('At least one instalment.');
  const n = BigInt(count);
  const each = total / n;
  const spare = total % n;
  const start = toDay(saleDate);
  return Array.from({ length: count }, (_, k) => ({
    seq: k + 1, due: iso(start + (termsDays + k * intervalDays) * day), amount: each + (BigInt(k) < spare ? 1n : 0n),
  }));
}

/** Oldest due date first (ties in the order given), never past an item's open amount. */
export function allocateOldestFirst<T extends { id: string; due: string; open: Baisa }>(items: T[], amount: Baisa): { allocations: { id: string; amount: Baisa }[]; left: Baisa } {
  let left = amount;
  const allocations: { id: string; amount: Baisa }[] = [];
  for (const it of items.map((x, i) => ({ x, i })).sort((a, b) => (a.x.due < b.x.due ? -1 : a.x.due > b.x.due ? 1 : a.i - b.i)).map(p => p.x)) {
    if (left <= 0n) break;
    if (it.open <= 0n) continue;
    const take = min(left, it.open);
    allocations.push({ id: it.id, amount: take });
    left -= take;
  }
  return { allocations, left };
}

/** Open items by days past due, what is on account, and the balance. */
export function aging(items: { due: string; open: Baisa }[], unallocated: Baisa, asOf: string): Aging {
  const b: Aging = { current: 0n, d1_30: 0n, d31_60: 0n, d61_90: 0n, d90plus: 0n, onAccount: unallocated, total: 0n };
  const at = toDay(asOf);
  for (const it of items) {
    const days = Math.round((at - toDay(it.due)) / day);
    if (days <= 0) b.current += it.open;
    else if (days <= 30) b.d1_30 += it.open;
    else if (days <= 60) b.d31_60 += it.open;
    else if (days <= 90) b.d61_90 += it.open;
    else b.d90plus += it.open;
  }
  b.total = b.current + b.d1_30 + b.d31_60 + b.d61_90 + b.d90plus - unallocated;
  return b;
}
