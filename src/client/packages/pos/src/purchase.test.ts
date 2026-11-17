import { readFileSync } from 'node:fs';
import { clearing, fromBaisa, purchaseNet, purchaseTax, match, splitEven, splitVariance, spread, spreadCharges, toBaisa } from './index';

// The same vectors the server's unit tests read, written by an independent Python implementation.
const v = JSON.parse(readFileSync('../../tests/vectors/purchase-math-vectors.json', 'utf8')) as {
  lines: { qty: string; unitPrice: string; rate: string; net: string; tax: string }[];
  spread: { amount: string; weights: string[]; shares: string[] }[];
  spreadCharges: { lines: { value: string; qty: string }[]; charges: { amount: string; basis: 'value' | 'qty' }[]; shares: string[] }[];
  match: { invoiceQty: string; invoicePrice: string; openQty: string; receiptPrice: string; orderPrice?: string | null; tolerancePct: string; toleranceAmount: string;
    status: string; reference: string; priceDifference: string; lineDifference: string }[];
  clearing: { invoiceQty: string; openQty: string; openValue: string; cleared: string }[];
  splitVariance: { variance: string; invoiceQty: string; onHandQty: string; inventory: string; ppv: string }[];
  splitEven: { amount: string; n: number; parts: string[] }[];
};
const b = toBaisa;
const f = (s: string) => fromBaisa(b(s));

describe('purchase vectors (same as the server and the Python implementation)', () => {
  it('reproduces line amounts, spreads and landed costs', () => {
    for (const c of v.lines) {
      const net = purchaseNet(b(c.qty), b(c.unitPrice));
      expect([fromBaisa(net), fromBaisa(purchaseTax(net, c.rate))]).toEqual([f(c.net), f(c.tax)]);
    }
    for (const c of v.spread) expect(spread(b(c.amount), c.weights.map(b)).map(fromBaisa)).toEqual(c.shares.map(f));
    for (const c of v.spreadCharges)
      expect(spreadCharges(c.lines.map((l) => ({ value: b(l.value), qty: b(l.qty) })), c.charges.map((x) => ({ amount: b(x.amount), basis: x.basis }))).map(fromBaisa))
        .toEqual(c.shares.map(f));
    for (const c of v.splitEven) expect(splitEven(b(c.amount), c.n).map(fromBaisa)).toEqual(c.parts.map(f));
    expect(v.lines.length + v.spread.length + v.spreadCharges.length + v.splitEven.length).toBeGreaterThanOrEqual(250);
  });

  it('reproduces the three-way match, clearing and the variance split', () => {
    for (const c of v.match) {
      const r = match(b(c.invoiceQty), b(c.invoicePrice), b(c.openQty), b(c.receiptPrice), c.orderPrice ? b(c.orderPrice) : null, c.tolerancePct, b(c.toleranceAmount));
      expect([r.status, fromBaisa(r.reference), fromBaisa(r.priceDifference), fromBaisa(r.lineDifference)])
        .toEqual([c.status, f(c.reference), f(c.priceDifference), f(c.lineDifference)]);
    }
    for (const c of v.clearing) expect(fromBaisa(clearing(b(c.invoiceQty), b(c.openQty), b(c.openValue)))).toBe(f(c.cleared));
    for (const c of v.splitVariance) {
      const r = splitVariance(b(c.variance), b(c.invoiceQty), b(c.onHandQty));
      expect([fromBaisa(r.inventory), fromBaisa(r.ppv)]).toEqual([f(c.inventory), f(c.ppv)]);
    }
  });

  it('worked examples', () => {
    expect(match(b('2'), b('101.5'), b('2'), b('100'), b('100'), 2, 0n).status).toBe('ok');
    expect(match(b('2'), b('103'), b('2'), b('100'), b('100'), 2, b('1')).status).toBe('price');
    expect(match(b('3'), b('100'), b('2'), b('100'), b('100'), 2, b('1')).status).toBe('qty');
    expect(fromBaisa(clearing(b('2'), b('3'), b('100')))).toBe('66.667');
    expect(splitVariance(b('10'), b('10'), b('4'))).toEqual({ inventory: 4000n, ppv: 6000n });
    expect(splitEven(b('10'), 3).map(fromBaisa)).toEqual(['3.334', '3.333', '3.333']);
  });
});
