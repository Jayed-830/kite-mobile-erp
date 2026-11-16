#!/usr/bin/env python3
"""Writes tests/vectors/commission-math-vectors.json from an independent Python implementation of the Phase 9 commission
arithmetic (blueprint 15.1, 15.2): a rule's base (a fixed amount, or a rate of the labour charges, or of the repair profit:
labour + parts revenue, excluding VAT, less parts cost and outside work, never below zero), the rule's minimum and maximum
per job, and the technician's share of the result, each rounded half away from zero to the baisa.
Kite.Sales.CommissionMath (C#) and @kite/pos commission.ts (TypeScript) must both reproduce every vector.
Run: python3 tests/vectors/generators/commission_vectors.py"""
import json, random
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path

D = Decimal
BAISA = D("0.001")

def money(x):
    sign = -1 if x < 0 else 1
    return sign * abs(x).quantize(BAISA, rounding=ROUND_HALF_UP)

def s(x): return format(money(x), "f")

def compute(basis, amount, rate, mn, mx, labour, parts_rev, parts_cost, external, share):
    profit = labour + parts_rev - parts_cost - external
    if basis == "fixed":
        base = D(0)
        raw = amount
    else:
        base = labour if basis == "labour" else profit
        raw = money(rate * max(base, D(0)) / D(100))
    clamped = raw
    if mn is not None and clamped < mn: clamped = mn
    if mx is not None and clamped > mx: clamped = mx
    return {"base": s(base), "raw": s(raw), "clamped": s(clamped), "amount": s(money(clamped * share / D(100)))}

def amt(r, lo=0, hi=200000): return D(r.randint(lo, hi)) / 1000

def main():
    r = random.Random(20261117)
    cases = []
    # The blueprint's worked example (15.2): labour 20, parts 30 at cost 18, 15% of profit = 4.800, shared 60/40.
    for share in (D(60), D(40)):
        cases.append(dict(basis="profit", amount=None, rate=D(15), min=None, max=None, labour=D(20), partsRevenue=D(30), partsCost=D(18), external=D(0), share=share))
    for _ in range(120):
        basis = r.choice(["fixed", "labour", "profit"])
        rate = None if basis == "fixed" else D(r.randint(0, 400000)) / 10000          # 0 to 40%, 4 decimals
        fixed = amt(r, 0, 25000) if basis == "fixed" else None
        mn = amt(r, 0, 6000) if r.random() < 0.3 else None
        mx = (mn or D(0)) + amt(r, 0, 20000) if r.random() < 0.3 else None
        share = r.choice([D(100), D(50), D(60), D(40), D("33.3333"), D("66.6667"), D(25), D(r.randint(1, 1000000)) / 10000])
        labour, parts_rev = amt(r), amt(r)
        parts_cost = amt(r, 0, 250000)          # sometimes more than revenue: a loss gives no commission
        external = amt(r, 0, 30000) if r.random() < 0.3 else D(0)
        cases.append(dict(basis=basis, amount=fixed, rate=rate, min=mn, max=mx, labour=labour, partsRevenue=parts_rev, partsCost=parts_cost, external=external, share=share))
    out = []
    for c in cases:
        expect = compute(c["basis"], c["amount"], c["rate"], c["min"], c["max"], c["labour"], c["partsRevenue"], c["partsCost"], c["external"], c["share"])
        out.append({k: (None if v is None else (v if isinstance(v, str) else format(v, "f"))) for k, v in c.items()} | {"expect": expect})
    path = Path(__file__).resolve().parents[1] / "commission-math-vectors.json"
    path.write_text(json.dumps({"commission": out}, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {len(out)} commission vectors to {path}")

if __name__ == "__main__":
    main()
