// POS arithmetic, the same rules as the server's Kite.Sales.PosMath (shared test vectors keep the two equal). The
// server prices every sale again; this copy lets the terminal show totals instantly and sell in emergency mode.
import { abs, mulDiv, rateUnits, sum, toBaisa, type Baisa } from './money';
import { marginLine } from './tradein';

export interface Item {
  variantId: string; sku: string; productType: string; tracking: string; descriptionEn: string; descriptionAr: string;
  taxCodeId: string; taxKind: string; taxRate: number | string; listPrice: number | string | null; minPrice: number | string | null; warrantyDays: number; active: boolean;
  /** A device bought under the profit margin scheme sells under it: the price it was bought for (12.3). */
  marginCost?: number | string | null;
}
export interface CartLine { variantId: string; qty: string | number; unitPrice?: string | number | null; discount?: string | number | null; approvalId?: string | null; deviceUnitId?: string | null }
export interface Limit { percent: number | string; amount: number | string; unlimited: boolean }
export interface Priced {
  lineNo: number; item: Item; qty: Baisa; listPrice: Baisa; unitPrice: Baisa; discount: Baisa; net: Baisa; tax: Baisa; gross: Baisa;
  needsPriceOverride: boolean; needsDiscountApproval: boolean;
}
export { PosError } from './errors';
import { PosError } from './errors';

export const UNLIMITED: Limit = { percent: 100, amount: 0, unlimited: true };

/** Tax for a line: extracted from tax-inclusive prices (gross × r / (100 + r)) or added (net × r / 100), per line. */
export function lineTax(lineAmount: Baisa, rate: number | string, inclusive: boolean): { net: Baisa; tax: Baisa; gross: Baisa } {
  const r = rateUnits(rate);
  if (inclusive) {
    const tax = mulDiv(lineAmount, r, 1_000_000n + r);
    return { net: lineAmount - tax, tax, gross: lineAmount };
  }
  const tax = mulDiv(lineAmount, r, 1_000_000n);
  return { net: lineAmount, tax, gross: lineAmount + tax };
}

export function allowedDiscount(lineAmount: Baisa, limit: Limit): Baisa {
  if (limit.unlimited) return lineAmount;
  const pct = mulDiv(lineAmount, rateUnits(limit.percent), 1_000_000n);
  const amt = toBaisa(limit.amount);
  return pct < amt ? pct : amt;
}

export function priceLine(lineNo: number, line: CartLine, item: Item, inclusive: boolean, limit: Limit): Priced {
  if (!item.active) throw new PosError('CAT-004', `${item.sku} is inactive.`);
  if (item.tracking !== 'none' && !line.deviceUnitId) throw new PosError('DEV-002', `${item.sku} is sold by IMEI or serial number: scan the device.`);
  if (line.deviceUnitId && toBaisa(line.qty) !== 1000n) throw new PosError('VAL-001', 'A device is sold one at a time.');
  if (item.taxKind === 'margin' && item.marginCost == null) throw new PosError('VAL-001', `${item.sku}: the margin scheme taxes one device against the price it was bought for; choose the device.`);
  if (item.listPrice == null && line.unitPrice == null) throw new PosError('CAT-001', `${item.sku} has no price.`);
  const qty = toBaisa(line.qty);
  if (qty <= 0n) throw new PosError('VAL-001', 'Quantity must be above zero.');
  const discount = toBaisa(line.discount ?? 0);
  if (discount < 0n) throw new PosError('VAL-001', 'Discount cannot be negative.');
  const list = toBaisa(item.listPrice ?? line.unitPrice!);
  const unit = line.unitPrice != null ? toBaisa(line.unitPrice) : list;
  const lineAmount = mulDiv(unit, qty, 1000n);
  if (discount > lineAmount) throw new PosError('VAL-001', 'The discount is larger than the line.');
  // Under the margin scheme the price is what the customer pays whether or not prices include tax: the VAT is inside the margin.
  const { net, tax, gross } = item.taxKind === 'margin'
    ? marginLine(lineAmount - discount, toBaisa(item.marginCost!), item.taxRate)
    : lineTax(lineAmount - discount, item.taxRate, inclusive);
  const priceChanged = item.listPrice == null || unit !== list;
  // effective unit = (lineAmount - discount) / qty < min  ⇔  (lineAmount - discount) × 1000 < min × qty
  const belowMin = item.minPrice != null && (lineAmount - discount) * 1000n < toBaisa(item.minPrice) * qty;
  return { lineNo, item, qty, listPrice: list, unitPrice: unit, discount, net, tax, gross,
    needsPriceOverride: priceChanged || belowMin, needsDiscountApproval: discount > allowedDiscount(lineAmount, limit) };
}

export function totals(lines: Priced[]) {
  return { net: sum(lines.map((l) => l.net)), discount: sum(lines.map((l) => l.discount)), tax: sum(lines.map((l) => l.tax)), gross: sum(lines.map((l) => l.gross)) };
}

/** Rounds cash to the policy step (0 = none), half away from zero. */
export function roundCash(amount: Baisa, step: number | string): Baisa {
  const s = toBaisa(step);
  if (s <= 0n) return amount;
  return mulDiv(amount, 1n, s) * s;
}

export type TenderMethod = 'cash' | 'card' | 'bank_transfer' | 'trade_in' | 'store_credit';
export interface Tender { method: TenderMethod; amount: Baisa; tendered?: Baisa | null; cardTerminal?: string | null; approvalCode?: string | null; reference?: string | null; tradeInId?: string | null }

/** Cash due after card/transfer, with the rounding it carries. */
export function cashDue(gross: Baisa, nonCash: Baisa, step: number | string): { due: Baisa; rounding: Baisa } {
  const raw = gross - nonCash;
  const due = roundCash(raw, step);
  return { due, rounding: due - raw };
}

/** Same checks as the server's CheckTenders; returns the cash rounding and what is paid in total. */
export function checkTenders(tenders: Tender[], gross: Baisa, step: number | string, refund = false): { rounding: Baisa; paid: Baisa } {
  if (tenders.length === 0 && roundCash(gross, step) === 0n) return { rounding: -gross, paid: 0n };
  if (tenders.length === 0) throw new PosError('POS-005', 'Add a payment.');
  if (tenders.filter((t) => t.method === 'cash').length > 1) throw new PosError('POS-005', 'Use one cash payment.');
  const seen = new Set<string>();
  for (const t of tenders) {
    if (t.method === 'trade_in' && refund) throw new PosError('POS-011', 'A refund cannot go back as trade-in credit.');
    if (t.method === 'trade_in' && !t.tradeInId) throw new PosError('VAL-001', 'Trade-in credit names its trade-in.');
    if (t.method !== 'trade_in' && t.tradeInId) throw new PosError('VAL-001', 'Only trade-in credit names a trade-in.');
    if (t.tradeInId) { if (seen.has(t.tradeInId)) throw new PosError('VAL-001', 'The same trade-in is applied twice.'); seen.add(t.tradeInId); }
    if (t.amount <= 0n) throw new PosError('VAL-001', 'Each payment must be above zero.');
    if (t.method === 'card' && !refund && (!t.cardTerminal?.trim() || !t.approvalCode?.trim())) throw new PosError('VAL-001', 'A card payment needs the bank terminal and approval code.');
    if (t.method === 'bank_transfer' && !t.reference?.trim()) throw new PosError('VAL-001', 'A bank transfer needs its reference.');
    if (t.tendered != null && (t.method !== 'cash' || t.tendered < t.amount)) throw new PosError('VAL-001', 'Cash tendered must cover the cash amount.');
  }
  const nonCash = sum(tenders.filter((t) => t.method !== 'cash').map((t) => t.amount));
  if (nonCash > gross) throw new PosError('POS-005', 'Payments other than cash are more than the total.');
  const cash = tenders.find((t) => t.method === 'cash');
  if (!cash) {
    if (nonCash !== gross) throw new PosError('POS-005', 'Payments do not match the total.');
    return { rounding: 0n, paid: gross };
  }
  const { due, rounding } = cashDue(gross, nonCash, step);
  if (cash.amount !== due) throw new PosError('POS-005', 'Cash does not match the amount due.');
  return { rounding, paid: gross + rounding };
}

export interface Returnable { qty: Baisa; net: Baisa; tax: Baisa; gross: Baisa; discount: Baisa; returnedQty: Baisa; returnedNet: Baisa; returnedTax: Baisa; returnedGross: Baisa; returnedDiscount: Baisa }

/** A return's proportional share; the last return takes whatever is left. */
export function apportionReturn(o: Returnable, qty: Baisa): { net: Baisa; tax: Baisa; gross: Baisa; discount: Baisa } {
  const left = o.qty - o.returnedQty;
  if (qty <= 0n) throw new PosError('VAL-001', 'Return quantity must be above zero.');
  if (qty > left) throw new PosError('POS-010', 'More would be returned than was sold.');
  if (qty === left) return { net: o.net - o.returnedNet, tax: o.tax - o.returnedTax, gross: o.gross - o.returnedGross, discount: o.discount - o.returnedDiscount };
  const share = (v: Baisa) => mulDiv(v, qty, o.qty);
  const tax = share(o.tax);
  const gross = share(o.gross);
  return { net: gross - tax, tax, gross, discount: share(o.discount) };
}

/** Totals a denomination count against the policy's denominations. */
export function countTotal(count: Record<string, number>, denominations: (number | string)[]): Baisa {
  const allowed = new Set(denominations.map((d) => toBaisa(d)));
  let total = 0n;
  for (const [k, pieces] of Object.entries(count)) {
    const d = toBaisa(k);
    if (!allowed.has(d)) throw new PosError('VAL-001', `${k} is not a denomination in the POS policy.`);
    if (!Number.isInteger(pieces) || pieces < 0) throw new PosError('VAL-001', 'Pieces must be a whole number, zero or more.');
    total += d * BigInt(pieces);
  }
  return total;
}

/** TLV QR payload (base64), the same layout as the server's (tags 1-6). */
export function qrPayload(seller: string, vatNumber: string | null, at: Date, total: Baisa, vat: Baisa, uuid: string, fmt: (b: Baisa) => string): string {
  const enc = new TextEncoder();
  const parts: number[] = [];
  const tag = (t: number, v: string) => { let b = enc.encode(v); if (b.length > 255) b = b.slice(0, 255); parts.push(t, b.length, ...b); };
  tag(1, seller); tag(2, vatNumber ?? ''); tag(3, at.toISOString().replace(/\.\d{3}Z$/, 'Z')); tag(4, fmt(total)); tag(5, fmt(vat)); tag(6, uuid);
  let s = '';
  for (const b of parts) s += String.fromCharCode(b);
  return btoa(s);
}

export const isZero = (b: Baisa) => abs(b) === 0n;
