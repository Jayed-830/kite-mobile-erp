import { readFileSync } from 'node:fs';
import { commission, fromBaisa, sharesComplete, toBaisa } from './index';

// The same vectors the server's unit tests read, written by an independent Python implementation.
const v = JSON.parse(readFileSync('../../tests/vectors/commission-math-vectors.json', 'utf8')) as {
  commission: { basis: 'fixed' | 'labour' | 'profit'; amount: string | null; rate: string | null; min: string | null; max: string | null; labour: string; partsRevenue: string;
    partsCost: string; external: string; share: string; expect: { base: string; raw: string; clamped: string; amount: string } }[];
};
const b = toBaisa;
const nb = (s: string | null) => (s == null ? null : b(s));
const f = (s: string) => fromBaisa(b(s));

describe('commission vectors (same as the server and the Python implementation)', () => {
  it('reproduces every basis, minimum, maximum and share', () => {
    for (const c of v.commission) {
      const r = commission(c.basis, nb(c.amount), c.rate, nb(c.min), nb(c.max),
        { labour: b(c.labour), partsRevenue: b(c.partsRevenue), partsCost: b(c.partsCost), external: b(c.external) }, c.share);
      expect([fromBaisa(r.base), fromBaisa(r.raw), fromBaisa(r.clamped), fromBaisa(r.amount)]).toEqual([f(c.expect.base), f(c.expect.raw), f(c.expect.clamped), f(c.expect.amount)]);
    }
    expect(v.commission.length).toBeGreaterThanOrEqual(100);
  });

  it('refuses what does not fit, and checks shares make 100%', () => {
    const job = { labour: b('20'), partsRevenue: b('30'), partsCost: b('18'), external: 0n };
    expect(() => commission('fixed', null, null, null, null, job, 100)).toThrow();
    expect(() => commission('labour', null, 101, null, null, job, 100)).toThrow();
    expect(() => commission('profit', null, 5, b('3'), b('2'), job, 100)).toThrow();
    expect(() => commission('fixed', b('1'), null, null, null, job, 0)).toThrow();
    expect(sharesComplete([60, 40])).toBe(true);
    expect(sharesComplete(['33.3333', '33.3333', '33.3334'])).toBe(true);
    expect(sharesComplete([59, 40])).toBe(false);
  });
});
