// Money in baisa (1/1000 OMR) as bigint: exact, never floating point (blueprint 26.3 rule 1). Quantities use the
// same three decimals. Rounding is half away from zero, as on the server.

export type Baisa = bigint;
export const ZERO = 0n;

/** "12.345" | 12.345 → 12345n. More than three decimals is an error (the server would refuse it too). */
export function toBaisa(v: string | number | bigint): Baisa {
  if (typeof v === 'bigint') return v;
  const s = typeof v === 'number' ? v.toFixed(6) : v.trim();
  const m = /^(-)?(\d+)(?:\.(\d+))?$/.exec(s);
  if (!m) throw new Error(`Not an amount: ${v}`);
  const frac = (m[3] ?? '').replace(/0+$/, '');
  if (frac.length > 3) throw new Error(`More than three decimals: ${v}`);
  const b = BigInt(m[2]!) * 1000n + BigInt((frac + '000').slice(0, 3));
  return m[1] ? -b : b;
}

/** 12345n → "12.345" (always three decimals). */
export function fromBaisa(b: Baisa): string {
  const neg = b < 0n;
  const a = neg ? -b : b;
  return `${neg ? '-' : ''}${a / 1000n}.${(a % 1000n).toString().padStart(3, '0')}`;
}

/** The number for JSON bodies: exact for every amount below 9 billion OMR. */
export const toNumber = (b: Baisa): number => Number(fromBaisa(b));

/** round(a × num / den), half away from zero. */
export function mulDiv(a: bigint, num: bigint, den: bigint): bigint {
  if (den === 0n) throw new Error('division by zero');
  const p = a * num;
  const neg = (p < 0n) !== (den < 0n);
  const ap = p < 0n ? -p : p;
  const ad = den < 0n ? -den : den;
  const q = ap / ad;
  const r = ap % ad;
  const rounded = r * 2n >= ad ? q + 1n : q;
  return neg ? -rounded : rounded;
}

export const abs = (b: bigint) => (b < 0n ? -b : b);
export const min = (a: bigint, b: bigint) => (a < b ? a : b);
export const sum = (xs: bigint[]) => xs.reduce((a, b) => a + b, 0n);

/** A tax rate such as 5 or "5.0000" as hundredths of a basis point (×10000), so 5% → 50000n. */
export function rateUnits(rate: string | number): bigint {
  const s = typeof rate === 'number' ? rate.toFixed(4) : rate;
  const [i, f = ''] = s.split('.');
  return BigInt(i!) * 10000n + BigInt((f + '0000').slice(0, 4));
}
