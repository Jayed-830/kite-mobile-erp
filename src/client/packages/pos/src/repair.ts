// Repair arithmetic, the same rules as the server's Kite.Sales.RepairMath and an independent Python implementation
// (tests/vectors/repair-math-vectors.json keeps all three equal). Amounts in baisa; rounding half away from zero.
import { mulDiv, rateUnits, sum, toBaisa, type Baisa } from './money';
import { PosError } from './errors';

/** Labour at the hourly rate: rate × minutes / 60, to the baisa. 1 to 6000 minutes. */
export function labourPrice(minutes: number, hourlyRate: string | number): Baisa {
  if (!Number.isInteger(minutes) || minutes < 1 || minutes > 6000) throw new PosError('VAL-001', 'Labour is 1 to 6000 minutes.');
  const rate = toBaisa(hourlyRate);
  if (rate < 0n) throw new PosError('VAL-001', 'The hourly rate cannot be negative.');
  return mulDiv(rate, BigInt(minutes), 60n);
}

/** The VAT inside a deposit when the business treats a deposit as a tax point: amount × rate / (100 + rate). */
export function depositTax(amount: Baisa, ratePercent: string | number): Baisa {
  const r = rateUnits(ratePercent);
  if (amount <= 0n || r <= 0n) return 0n;
  return mulDiv(amount, r, 1_000_000n + r);
}

/** The share of the VAT held in deposits that goes with part of the balance; all of it when the part is the whole balance. */
export function taxShare(heldTax: Baisa, balance: Baisa, part: Baisa): Baisa {
  if (part < 0n || part > balance) throw new PosError('VAL-001', 'More than the deposit held.');
  if (heldTax === 0n || part === 0n) return 0n;
  return part === balance ? heldTax : mulDiv(heldTax, part, balance);
}

export interface DeliverySplit { applied: Baisa; due: Baisa; excess: Baisa; appliedTax: Baisa; excessTax: Baisa }

/** At collection: the deposit pays the invoice first; the rest is due, or the deposit's excess goes back with its share of the VAT. */
export function splitDelivery(invoiceGross: Baisa, depositBalance: Baisa, heldTax: Baisa): DeliverySplit {
  if (invoiceGross < 0n) throw new PosError('VAL-001', 'The invoice cannot be negative.');
  if (depositBalance < 0n) throw new PosError('VAL-001', 'The deposit cannot be negative.');
  const applied = invoiceGross < depositBalance ? invoiceGross : depositBalance;
  const excess = depositBalance - applied;
  const excessTax = taxShare(heldTax, depositBalance, excess);
  return { applied, due: invoiceGross - applied, excess, appliedTax: heldTax - excessTax, excessTax };
}

/** Spreads a cost over lines by weight, to the baisa; the last line takes what rounding leaves, so the parts add up. */
export function allocateCost(cost: Baisa, weights: Baisa[]): Baisa[] {
  if (weights.length === 0) return [];
  const total = sum(weights);
  const out = weights.map(() => 0n);
  if (total <= 0n) { out[out.length - 1] = cost; return out; }
  let left = cost;
  for (let i = 0; i < weights.length - 1; i++) { out[i] = mulDiv(cost, weights[i]!, total); left -= out[i]!; }
  out[out.length - 1] = left;
  return out;
}

/** The cost that comes back with returned parts: pro rata, and the last return takes exactly what is left. Quantities in thousandths. */
export function returnCost(fittedQty: Baisa, fittedCost: Baisa, returnedQty: Baisa, returnedCost: Baisa, qty: Baisa): Baisa {
  if (qty <= 0n || returnedQty + qty > fittedQty) throw new PosError('VAL-001', 'More would come back than was fitted.');
  return returnedQty + qty === fittedQty ? fittedCost - returnedCost : mulDiv(fittedCost, qty, fittedQty);
}
