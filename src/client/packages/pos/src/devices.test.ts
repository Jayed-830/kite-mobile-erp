import { readFileSync } from 'node:fs';
import { allowedConditions, classifyCode, evaluateInspection, imeiValid, labourCost, suggestedPrice, unitListPrice, type ChecklistItem } from './index';

// The same vectors as the server's DeviceVectorTests, written by tests/vectors/generators/device_vectors.py.
const v = JSON.parse(readFileSync('../../tests/vectors/device-math-vectors.json', 'utf8')) as {
  checklist: ChecklistItem[];
  imei: { value: string; valid: boolean }[];
  classify: { raw: string; kind: string; value: string }[];
  labour: { minutes: number; rate: string; amount: string }[];
  suggested: { newPrice: string | null; percent: string | null; step: string; price: string | null }[];
  unitListPrice: { sellingPrice: string | null; condition: string; newPrice: string | null; percent: string | null; step: string; price: string | null }[];
  evaluate: { answers: Record<string, string>; current: string; expect: { complete: boolean; missing: string[]; failed: string[]; outcome: string; condition: string; battery: number | null } }[];
};
const norm = (x: string | null) => (x == null ? null : Number(x).toFixed(3));

describe('shared device vectors (same as the server)', () => {
  it('checks IMEI digits and recognises codes', () => {
    for (const c of v.imei) expect([c.value, imeiValid(c.value)]).toEqual([c.value, c.valid]);
    for (const c of v.classify) expect(classifyCode(c.raw)).toEqual({ kind: c.kind, value: c.value });
  });
  it('prices labour, grades and units', () => {
    for (const c of v.labour) expect(labourCost(c.minutes, c.rate)).toBe(norm(c.amount));
    for (const c of v.suggested) expect(suggestedPrice(c.newPrice, c.percent, c.step)).toBe(norm(c.price));
    for (const c of v.unitListPrice) expect(unitListPrice(c.sellingPrice, c.condition, c.newPrice, c.percent, c.step)).toBe(norm(c.price));
  });
  it('reads checklists', () => {
    for (const c of v.evaluate) {
      const r = evaluateInspection(v.checklist, c.answers, c.current);
      expect(r).toEqual({ ...c.expect });
    }
    expect(v.evaluate.length).toBeGreaterThanOrEqual(20);
  });
  it('allows only the conditions a decision can give', () => {
    expect(allowedConditions('restock', 'used')).toEqual(['used', 'damaged']);
    expect(allowedConditions('restock', 'brand_new')).toEqual(['brand_new', 'used', 'damaged']);
    expect(allowedConditions('for_parts', 'used')).toEqual(['for_parts']);
  });
});
