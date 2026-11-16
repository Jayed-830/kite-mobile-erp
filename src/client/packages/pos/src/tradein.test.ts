import { readFileSync } from 'node:fs';
import { applyTradeIn, checkTenders, fromBaisa, last4, marginLine, needsApproval, priceLine, toBaisa, valueTradeIn, type Item } from './index';

// The same vectors the server's unit tests read, written by an independent Python implementation.
const v = JSON.parse(readFileSync('../../tests/vectors/tradein-math-vectors.json', 'utf8')) as {
  valuation: { guide: string | null; deductions: { code: string; kind: string; value: string }[]; expect: { amounts: string[]; total: string; offered: string | null } | null }[];
  approval: { agreed: string; offered: string | null; staffLimit: string; overGuidePercent: string; expect: boolean }[];
  margin: { gross: string; purchase: string; rate: string; expect: { net: string; tax: string; gross: string; margin: string } }[];
  apply: { outstanding: string; due: string; expect: { applied: string; left: string } }[];
};
const f = (s: string) => fromBaisa(toBaisa(s));

describe('trade-in vectors (same as the server and the Python implementation)', () => {
  it('values every trade-in, and refuses percentages without a guide value', () => {
    for (const c of v.valuation) {
      if (c.expect == null) { expect(() => valueTradeIn(c.guide, c.deductions)).toThrow(expect.objectContaining({ code: 'TRD-008' })); continue; }
      const r = valueTradeIn(c.guide, c.deductions);
      expect(r.amounts.map(fromBaisa)).toEqual(c.expect.amounts.map(f));
      expect(fromBaisa(r.total)).toBe(f(c.expect.total));
      expect(r.offered == null ? null : fromBaisa(r.offered)).toBe(c.expect.offered == null ? null : f(c.expect.offered));
    }
    expect(v.valuation.length).toBeGreaterThan(40);
  });

  it('decides approvals, margin VAT and how much a sale takes exactly like the server', () => {
    for (const c of v.approval) expect(needsApproval(toBaisa(c.agreed), c.offered == null ? null : toBaisa(c.offered), c.staffLimit, c.overGuidePercent)).toBe(c.expect);
    for (const c of v.margin) {
      const m = marginLine(toBaisa(c.gross), toBaisa(c.purchase), c.rate);
      expect([m.net, m.tax, m.gross, m.margin].map(fromBaisa)).toEqual([c.expect.net, c.expect.tax, c.expect.gross, c.expect.margin].map(f));
    }
    for (const c of v.apply) {
      const a = applyTradeIn(toBaisa(c.outstanding), toBaisa(c.due));
      expect([fromBaisa(a.applied), fromBaisa(a.left)]).toEqual([f(c.expect.applied), f(c.expect.left)]);
    }
    expect(last4('12 345 678')).toBe('5678');
  });

  it('prices a margin device on its margin whether or not prices include VAT, and checks credit payments', () => {
    const item: Item = { variantId: 'v', sku: 'P', productType: 'device', tracking: 'imei', descriptionEn: 'x', descriptionAr: 'x', taxCodeId: 'm', taxKind: 'margin',
      taxRate: '5', listPrice: '130.000', minPrice: null, warrantyDays: 0, active: true, marginCost: '90.000' };
    for (const inclusive of [true, false]) {
      const l = priceLine(1, { variantId: 'v', qty: 1, deviceUnitId: 'u' }, item, inclusive, { percent: 0, amount: 0, unlimited: true });
      expect([l.net, l.tax, l.gross].map(fromBaisa)).toEqual(['128.095', '1.905', '130.000']);
    }
    expect(() => priceLine(1, { variantId: 'v', qty: 1, deviceUnitId: 'u' }, { ...item, marginCost: null }, true, { percent: 0, amount: 0, unlimited: true })).toThrow();
    expect(checkTenders([{ method: 'trade_in', amount: 90_000n, tradeInId: 't' }, { method: 'cash', amount: 40_000n }], 130_000n, 0)).toEqual({ rounding: 0n, paid: 130_000n });
    expect(() => checkTenders([{ method: 'trade_in', amount: 90_000n }], 90_000n, 0)).toThrow();
    expect(() => checkTenders([{ method: 'trade_in', amount: 50_000n, tradeInId: 't' }, { method: 'trade_in', amount: 40_000n, tradeInId: 't' }], 90_000n, 0)).toThrow();
    expect(() => checkTenders([{ method: 'trade_in', amount: 10_000n, tradeInId: 't' }], 10_000n, 0, true)).toThrow(expect.objectContaining({ code: 'POS-011' }));
    expect(() => checkTenders([{ method: 'store_credit', amount: 140_000n }, { method: 'cash', amount: 0n }], 130_000n, 0)).toThrow();
  });
});
