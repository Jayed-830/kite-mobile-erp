import { readFileSync } from 'node:fs';
import { apportionReturn, checkTenders, countTotal, encodeRaster, fromBaisa, mulDiv, priceLine, qrPayload, roundCash, toBaisa, type Item } from './index';

// Vitest runs from src/client; the vectors are shared with the server's unit tests.
const vectors = JSON.parse(readFileSync('../../tests/vectors/pos-math-vectors.json', 'utf8')) as {
  lines: { listPrice: string; minPrice: string | null; qty: string; unitPrice: string | null; discount: string; taxRate: string; inclusive: boolean;
    limit: { percent: string; amount: string; unlimited: boolean }; expect: { net: string; tax: string; gross: string; needsPriceOverride: boolean; needsDiscountApproval: boolean } }[];
  roundCash: { amount: string; step: string; expect: string }[];
  apportion: { line: { qty: string; net: string; tax: string; gross: string; discount: string }; returns: string[]; expect: { net: string; tax: string; gross: string; discount: string }[] }[];
};

const item = (listPrice: string | null, minPrice: string | null, taxRate: string): Item => ({
  variantId: 'v', sku: 'SKU', productType: 'accessory', tracking: 'none', descriptionEn: 'x', descriptionAr: 'x', taxCodeId: 't', taxKind: 'standard',
  taxRate, listPrice, minPrice, warrantyDays: 0, active: true,
});

describe('money', () => {
  it('parses and formats baisa exactly and rounds half away from zero', () => {
    expect(toBaisa('12.345')).toBe(12345n);
    expect(toBaisa('-0.5')).toBe(-500n);
    expect(fromBaisa(-5n)).toBe('-0.005');
    expect(() => toBaisa('1.2345')).toThrow();
    expect(mulDiv(5n, 1n, 2n)).toBe(3n);
    expect(mulDiv(-5n, 1n, 2n)).toBe(-3n);
    expect(mulDiv(4n, 1n, 3n)).toBe(1n);
  });
});

describe('shared POS vectors (same as the server)', () => {
  it('prices every line exactly like the server', () => {
    for (const v of vectors.lines) {
      const l = priceLine(1, { variantId: 'v', qty: v.qty, unitPrice: v.unitPrice, discount: v.discount }, item(v.listPrice, v.minPrice, v.taxRate), v.inclusive, v.limit);
      expect([fromBaisa(l.net), fromBaisa(l.tax), fromBaisa(l.gross)]).toEqual([fromBaisa(toBaisa(v.expect.net)), fromBaisa(toBaisa(v.expect.tax)), fromBaisa(toBaisa(v.expect.gross))]);
      expect(l.needsPriceOverride).toBe(v.expect.needsPriceOverride);
      expect(l.needsDiscountApproval).toBe(v.expect.needsDiscountApproval);
    }
    expect(vectors.lines.length).toBeGreaterThanOrEqual(15);
  });

  it('rounds cash and shares returns exactly like the server', () => {
    for (const v of vectors.roundCash) expect(fromBaisa(roundCash(toBaisa(v.amount), v.step))).toBe(fromBaisa(toBaisa(v.expect)));
    for (const v of vectors.apportion) {
      const o = { qty: toBaisa(v.line.qty), net: toBaisa(v.line.net), tax: toBaisa(v.line.tax), gross: toBaisa(v.line.gross), discount: toBaisa(v.line.discount),
        returnedQty: 0n, returnedNet: 0n, returnedTax: 0n, returnedGross: 0n, returnedDiscount: 0n };
      v.returns.forEach((q, i) => {
        const a = apportionReturn(o, toBaisa(q));
        expect([a.net, a.tax, a.gross, a.discount].map(fromBaisa)).toEqual([v.expect[i]!.net, v.expect[i]!.tax, v.expect[i]!.gross, v.expect[i]!.discount].map((x) => fromBaisa(toBaisa(x))));
        o.returnedQty += toBaisa(q); o.returnedNet += a.net; o.returnedTax += a.tax; o.returnedGross += a.gross; o.returnedDiscount += a.discount;
      });
      expect(o.returnedGross).toBe(o.gross);
    }
  });
});

describe('tenders and counts', () => {
  it('checks payments with cash rounding', () => {
    expect(checkTenders([{ method: 'card', amount: 3500n, cardTerminal: 'B', approvalCode: '1' }, { method: 'cash', amount: 3500n, tendered: 5000n }], 6999n, '0.005'))
      .toEqual({ rounding: 1n, paid: 7000n });
    expect(() => checkTenders([{ method: 'cash', amount: 6999n }], 6999n, '0.005')).toThrow();
    expect(() => checkTenders([{ method: 'card', amount: 6999n, cardTerminal: 'B' }], 6999n, '0.005')).toThrow();
    expect(checkTenders([], 2n, '0.005')).toEqual({ rounding: -2n, paid: 0n });
  });

  it('totals a drawer count', () => {
    expect(fromBaisa(countTotal({ '20': 1, '1': 4, '0.5': 1, '0.1': 1, '0.05': 1, '0.01': 1, '0.005': 1 }, [50, 20, 10, 5, 1, 0.5, 0.1, 0.05, 0.025, 0.01, 0.005]))).toBe('24.665');
    expect(() => countTotal({ '2': 1 }, [1, 5])).toThrow();
  });
});

describe('receipt bytes', () => {
  it('encodes a 1-bit raster with GS v 0, the drawer kick and a cut', () => {
    const w = 16, h = 2;
    const data = new Uint8ClampedArray(w * h * 4).fill(255);
    for (let c = 0; c < 8; c++) data.set([0, 0, 0, 255], c * 4);   // first 8 dots of row 0 black
    const bytes = encodeRaster({ data }, w, h, { drawer: true });
    expect([...bytes.slice(0, 10)]).toEqual([0x1b, 0x40, 0x1d, 0x76, 0x30, 0x00, 2, 0, 2, 0]);
    expect([...bytes.slice(10, 14)]).toEqual([0xff, 0x00, 0x00, 0x00]);
    expect([...bytes.slice(14, 19)]).toEqual([0x1b, 0x70, 0x00, 0x19, 0xfa]);
    expect([...bytes.slice(-3)]).toEqual([0x56, 0x42, 0x00]);
  });

  it('builds the same TLV QR layout as the server', () => {
    const qr = qrPayload('Al Noor', 'OM1', new Date('2026-11-16T05:30:00Z'), 6999n, 333n, 'u-1', fromBaisa);
    const raw = atob(qr);
    expect(raw.charCodeAt(0)).toBe(1);
    expect(raw.slice(2, 2 + raw.charCodeAt(1))).toBe('Al Noor');
    expect(raw).toContain('2026-11-16T05:30:00Z');
    expect(raw).toContain('6.999');
  });
});
