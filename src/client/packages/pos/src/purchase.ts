// Purchasing arithmetic (blueprint 16.2, 16.3), the same rules as the server's Kite.Sales.PurchaseMath and an independent
// Python implementation (tests/vectors/purchase-math-vectors.json keeps all three equal). Amounts and quantities in baisa
// (three decimals as bigint), rounding half away from zero; prices before VAT.
import { abs, mulDiv, rateUnits, sum, type Baisa } from './money';

export type MatchStatus = 'ok' | 'price' | 'qty';
export interface MatchResult { status: MatchStatus; reference: Baisa; priceDifference: Baisa; lineDifference: Baisa }

/** Quantity × price, to the baisa (both in thousandths). */
export const purchaseNet = (qty: Baisa, unitPrice: Baisa): Baisa => mulDiv(qty, unitPrice, 1000n);

/** VAT on an amount before VAT at a percentage such as 5 or "5.0000". */
export const purchaseTax = (net: Baisa, ratePercent: string | number): Baisa => mulDiv(net, rateUnits(ratePercent), 1_000_000n);

/** An amount spread over weights by cumulative rounding, so the shares add up to it exactly; evenly when every weight is zero. */
export function spread(amount: Baisa, weights: Baisa[]): Baisa[] {
  if (weights.length === 0) return [];
  if (weights.some((w) => w < 0n)) throw new RangeError('Weights are zero or more.');
  let total = sum(weights);
  const w = total === 0n ? weights.map(() => 1n) : weights;
  if (total === 0n) total = BigInt(w.length);
  let cum = 0n;
  let before = 0n;
  return w.map((x) => {
    cum += x;
    const upto = mulDiv(amount, cum, total);
    const share = upto - before;
    before = upto;
    return share;
  });
}

/** Landed costs added to receipt lines: each charge spread by the lines' value or quantity. */
export function spreadCharges(lines: { value: Baisa; qty: Baisa }[], charges: { amount: Baisa; basis: 'value' | 'qty' }[]): Baisa[] {
  const shares = lines.map(() => 0n);
  for (const c of charges) {
    const s = spread(c.amount, lines.map((l) => (c.basis === 'qty' ? l.qty : l.value)));
    s.forEach((x, k) => { shares[k] = shares[k]! + x; });
  }
  return shares;
}

/**
 * Three-way match of an invoice line: no more than is still open on the receipt, and a unit price within the percentage of the
 * order's price (the receipt's without an order), or a whole-line difference within the amount.
 */
export function match(invoiceQty: Baisa, invoicePrice: Baisa, openQty: Baisa, receiptPrice: Baisa, orderPrice: Baisa | null,
  tolerancePct: string | number, toleranceAmount: Baisa): MatchResult {
  const reference = orderPrice ?? receiptPrice;
  const priceDifference = invoicePrice - reference;
  const lineDifference = purchaseNet(invoiceQty, invoicePrice) - purchaseNet(invoiceQty, reference);
  if (invoiceQty > openQty) return { status: 'qty', reference, priceDifference, lineDifference };
  const withinPct = abs(priceDifference) <= mulDiv(reference, rateUnits(tolerancePct), 1_000_000n);
  const withinAmount = abs(lineDifference) <= toleranceAmount;
  return { status: withinPct || withinAmount ? 'ok' : 'price', reference, priceDifference, lineDifference };
}

/** What an invoice clears from goods received not invoiced: all that is open when it takes all that is open, else its share. */
export const clearing = (invoiceQty: Baisa, openQty: Baisa, openValue: Baisa): Baisa =>
  openQty <= 0n ? 0n : invoiceQty >= openQty ? openValue : mulDiv(openValue, invoiceQty, openQty);

/** A price difference split: the share still on hand to the stock's cost, the rest to purchase price variance. */
export function splitVariance(variance: Baisa, invoiceQty: Baisa, onHandQty: Baisa): { inventory: Baisa; ppv: Baisa } {
  if (invoiceQty <= 0n) return { inventory: 0n, ppv: variance };
  const share = onHandQty < 0n ? 0n : onHandQty > invoiceQty ? invoiceQty : onHandQty;
  const inventory = mulDiv(variance, share, invoiceQty);
  return { inventory, ppv: variance - inventory };
}

/** An amount split evenly over n parts, the spare baisa on the first parts (a negative amount keeps its sign). */
export function splitEven(amount: Baisa, n: number): Baisa[] {
  if (n < 1) return [];
  const a = abs(amount);
  const sign = amount < 0n ? -1n : 1n;
  const each = a / BigInt(n);
  const spare = a % BigInt(n);
  return Array.from({ length: n }, (_, k) => sign * (each + (BigInt(k) < spare ? 1n : 0n)));
}
