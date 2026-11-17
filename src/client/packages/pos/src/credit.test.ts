import { readFileSync } from 'node:fs';
import { aging, allocateOldestFirst, availableOffline, availableOnline, decideCredit, fromBaisa, instalments, toBaisa } from './index';

// The same vectors the server's unit tests read, written by an independent Python implementation.
type Item = { id: string; due: string; open: string };
const v = JSON.parse(readFileSync('../../tests/vectors/credit-math-vectors.json', 'utf8')) as {
  online: { limit: string; exposure: string; unsynced: string; expect: string }[];
  offline: { allowance: string; limit: string; lastExposure: string; sinceSync: string; cap: string; expect: string }[];
  decide: { amount: string; available: string; overrideCap: string; overdueDays: number; blockDays: number; status: string; hasApproval: boolean;
    expect: { outcome: string; overBy: string } }[];
  instalments: { total: string; count: number; saleDate: string; terms: number; interval: number; expect: { seq: number; due: string; amount: string }[] }[];
  allocate: { items: Item[]; amount: string; expect: { allocations: { id: string; amount: string }[]; left: string } }[];
  aging: { items: { due: string; open: string }[]; unallocated: string; asOf: string; expect: Record<string, string> }[];
};
const b = toBaisa;
const f = (s: string) => fromBaisa(b(s));

describe('credit vectors (same as the server and the Python implementation)', () => {
  it('reproduces available credit online and offline', () => {
    for (const c of v.online) expect(fromBaisa(availableOnline(b(c.limit), b(c.exposure), b(c.unsynced)))).toBe(f(c.expect));
    for (const c of v.offline) expect(fromBaisa(availableOffline(b(c.allowance), b(c.limit), b(c.lastExposure), b(c.sinceSync), b(c.cap)))).toBe(f(c.expect));
    expect(v.online.length + v.offline.length).toBeGreaterThanOrEqual(160);
  });

  it('reproduces every decision', () => {
    for (const c of v.decide) {
      const r = decideCredit(b(c.amount), b(c.available), b(c.overrideCap), c.overdueDays, c.blockDays, c.status, c.hasApproval);
      expect([r.outcome, fromBaisa(r.overBy)]).toEqual([c.expect.outcome, f(c.expect.overBy)]);
    }
  });

  it('reproduces instalments, allocation and aging', () => {
    for (const c of v.instalments)
      expect(instalments(b(c.total), c.count, c.saleDate, c.terms, c.interval).map(x => [x.seq, x.due, fromBaisa(x.amount)]))
        .toEqual(c.expect.map(x => [x.seq, x.due, f(x.amount)]));
    for (const c of v.allocate) {
      const r = allocateOldestFirst(c.items.map(i => ({ ...i, open: b(i.open) })), b(c.amount));
      expect(r.allocations.map(a => [a.id, fromBaisa(a.amount)])).toEqual(c.expect.allocations.map(a => [a.id, f(a.amount)]));
      expect(fromBaisa(r.left)).toBe(f(c.expect.left));
    }
    for (const c of v.aging) {
      const r = aging(c.items.map(i => ({ due: i.due, open: b(i.open) })), b(c.unallocated), c.asOf);
      for (const k of ['current', 'd1_30', 'd31_60', 'd61_90', 'd90plus', 'onAccount', 'total'] as const) expect(fromBaisa(r[k])).toBe(f(c.expect[k]!));
    }
    expect(() => instalments(10n, 0, '2026-01-01', 30, 30)).toThrow();
  });
});
