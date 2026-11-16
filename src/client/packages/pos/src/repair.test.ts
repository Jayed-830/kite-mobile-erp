import { readFileSync } from 'node:fs';
import { allocateCost, depositTax, fromBaisa, labourPrice, returnCost, splitDelivery, taxShare, toBaisa } from './index';

// The same vectors the server's unit tests read, written by an independent Python implementation.
const v = JSON.parse(readFileSync('../../tests/vectors/repair-math-vectors.json', 'utf8')) as {
  labour: { minutes: number; rate: string; expect: string | null }[];
  depositTax: { amount: string; rate: string; expect: string }[];
  taxShare: { held: string; balance: string; part: string; expect: string | null }[];
  split: { gross: string; balance: string; tax: string; expect: { applied: string; due: string; excess: string; appliedTax: string; excessTax: string } }[];
  allocate: { cost: string; weights: string[]; expect: string[] }[];
  returnCost: { fittedQty: string; fittedCost: string; returnedQty: string; returnedCost: string; qty: string; expect: string | null }[];
};
const b = toBaisa;
const f = (s: string) => fromBaisa(b(s));

describe('repair vectors (same as the server and the Python implementation)', () => {
  it('prices labour and finds the VAT inside a deposit', () => {
    for (const c of v.labour) {
      if (c.expect == null) { expect(() => labourPrice(c.minutes, c.rate)).toThrow(); continue; }
      expect(fromBaisa(labourPrice(c.minutes, c.rate))).toBe(f(c.expect));
    }
    for (const c of v.depositTax) expect(fromBaisa(depositTax(b(c.amount), c.rate))).toBe(f(c.expect));
    expect(v.labour.length + v.depositTax.length).toBeGreaterThan(20);
  });

  it('splits a collection between the deposit, what is due and the excess back', () => {
    for (const c of v.taxShare) {
      if (c.expect == null) { expect(() => taxShare(b(c.held), b(c.balance), b(c.part))).toThrow(); continue; }
      expect(fromBaisa(taxShare(b(c.held), b(c.balance), b(c.part)))).toBe(f(c.expect));
    }
    for (const c of v.split) {
      const s = splitDelivery(b(c.gross), b(c.balance), b(c.tax));
      expect([s.applied, s.due, s.excess, s.appliedTax, s.excessTax].map(fromBaisa))
        .toEqual([c.expect.applied, c.expect.due, c.expect.excess, c.expect.appliedTax, c.expect.excessTax].map(f));
      expect(s.applied + s.due).toBe(b(c.gross));
      expect(s.appliedTax + s.excessTax).toBe(b(c.tax));
    }
  });

  it('spreads costs and returns parts at the cost they went on at', () => {
    for (const c of v.allocate) {
      const r = allocateCost(b(c.cost), c.weights.map(b));
      expect(r.map(fromBaisa)).toEqual(c.expect.map(f));
      expect(r.reduce((x, y) => x + y, 0n)).toBe(b(c.cost));
    }
    for (const c of v.returnCost) {
      const args = [c.fittedQty, c.fittedCost, c.returnedQty, c.returnedCost, c.qty].map(b) as [bigint, bigint, bigint, bigint, bigint];
      if (c.expect == null) { expect(() => returnCost(...args)).toThrow(); continue; }
      expect(fromBaisa(returnCost(...args))).toBe(f(c.expect));
    }
  });
});
