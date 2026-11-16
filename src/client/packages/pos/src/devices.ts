// Device rules (blueprint 11.2, 12.1, 12.2, 12.4), the same as the server's Kite.Devices.DeviceMath; both reproduce
// tests/vectors/device-math-vectors.json, written by an independent Python implementation. The server decides again;
// this copy lets the till and the inspection screen answer instantly.
import { fromBaisa, mulDiv, toBaisa } from './money';

export type Condition = 'brand_new' | 'used' | 'refurbished' | 'damaged' | 'for_parts';
export type Outcome = 'restock' | 'refurbish' | 'for_parts' | 'supplier_return';
export interface ChecklistItem { code: string; labelEn?: string; labelAr?: string; kind: 'pass_fail' | 'percent' | 'text'; required: boolean; minPercent: number | null; onFail: 'refurbish' | 'damaged' | 'for_parts' }
export interface InspectionResult { complete: boolean; missing: string[]; failed: string[]; outcome: Outcome; condition: Condition; battery: number | null }
export interface ScannedCode { kind: 'imei' | 'invalid_imei' | 'serial' | 'unknown'; value: string }

/** 15 digits, the last one the Luhn check digit of the first 14. */
export function imeiValid(s: string | null | undefined): boolean {
  if (!s || !/^[0-9]{15}$/.test(s)) return false;
  let sum = 0;
  for (let i = 0; i < 15; i++) {
    let d = s.charCodeAt(i) - 48;
    if (i % 2 === 1) { d *= 2; if (d > 9) d -= 9; }
    sum += d;
  }
  return sum % 10 === 0;
}

/** 15 digits are an IMEI (check digit verified); 14 or 16 digits a mistyped IMEI; else a serial number of 4-40 characters. */
export function classifyCode(raw: string | null | undefined): ScannedCode {
  const s = (raw ?? '').trim().replace(/ /g, '').toUpperCase();
  if (s.length > 0 && /^[0-9]+$/.test(s)) {
    if (s.length === 15) return { kind: imeiValid(s) ? 'imei' : 'invalid_imei', value: s };
    if (s.length === 14 || s.length === 16) return { kind: 'invalid_imei', value: s };
  }
  return /^[A-Z0-9][A-Z0-9-]{3,39}$/.test(s) ? { kind: 'serial', value: s } : { kind: 'unknown', value: s };
}

/** Internal labour: minutes at the hourly rate, rounded to the baisa, half away from zero. */
export function labourCost(minutes: number, ratePerHour: string | number): string {
  return fromBaisa(mulDiv(BigInt(minutes) * toBaisa(ratePerHour), 1n, 60n));
}

/** The grade's suggested price: the new price × the grade's percentage, to the nearest step. */
export function suggestedPrice(newPrice: string | number | null, gradePercent: string | number | null, step: string | number): string | null {
  if (newPrice == null || gradePercent == null) return null;
  const s = toBaisa(step);
  if (s <= 0n) return null;
  // percent may carry two decimals: work in hundredths of a percent
  const pct = BigInt(Math.round(Number(gradePercent) * 100));
  const raw = toBaisa(newPrice) * pct;                // baisa × hundredths of a percent
  const steps = mulDiv(raw, 1n, 10_000n * s);         // round(price × pct / 100 / step)
  return fromBaisa(steps * s);
}

/** The price a unit sells at unless changed: its own; else, new, the item's price; else the grade's suggestion. */
export function unitListPrice(sellingPrice: string | number | null, condition: string, newPrice: string | number | null, gradePercent: string | number | null, step: string | number): string | null {
  if (sellingPrice != null) return fromBaisa(toBaisa(sellingPrice));
  return condition === 'brand_new' ? (newPrice == null ? null : fromBaisa(toBaisa(newPrice))) : suggestedPrice(newPrice, gradePercent, step);
}

/** The conditions an inspection may give a unit, for an outcome, from its current condition. */
export function allowedConditions(outcome: Outcome, current: string): Condition[] {
  const keep = (c: Condition): Condition[] => (current === c ? [c] : []);
  switch (outcome) {
    case 'restock': case 'supplier_return': return [...keep('brand_new'), ...keep('refurbished'), 'used', 'damaged'];
    case 'refurbish': return ['used', 'damaged'];
    case 'for_parts': return ['for_parts'];
    default: return [];
  }
}

/** Reads a checklist; the suggestion follows the worst failure: for parts, then refurbish (as used), then damaged. */
export function evaluateInspection(items: ChecklistItem[], answers: Record<string, string | null | undefined>, current: string): InspectionResult {
  const missing: string[] = [];
  const failed: string[] = [];
  let battery: number | null = null;
  let worst = 0;
  for (const item of items) {
    const raw = answers[item.code];
    const v = raw == null || String(raw).trim() === '' ? null : String(raw).trim();
    let passed: boolean | null = null;
    if (item.kind === 'pass_fail') {
      if (v == null) { if (item.required) missing.push(item.code); }
      else passed = v.toLowerCase() === 'pass';
    } else if (item.kind === 'percent') {
      if (v == null) { if (item.required) missing.push(item.code); }
      else {
        const pct = Number.parseInt(v, 10);
        passed = pct >= (item.minPercent ?? 0);
        if (item.code === 'battery') battery = pct;
      }
    }
    if (passed === false) {
      failed.push(item.code);
      worst = Math.max(worst, item.onFail === 'for_parts' ? 3 : item.onFail === 'refurbish' ? 2 : 1);
    }
  }
  const [outcome, condition]: [Outcome, Condition] =
    worst === 3 ? ['for_parts', 'for_parts'] : worst === 2 ? ['refurbish', 'used'] : worst === 1 ? ['restock', 'damaged']
      : ['restock', (current === 'for_parts' ? 'used' : current) as Condition];
  return { complete: missing.length === 0, missing, failed, outcome, condition, battery };
}
