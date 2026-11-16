#!/usr/bin/env python3
"""Writes tests/vectors/tradein-math-vectors.json from an independent Python implementation of the Phase 7 trade-in
rules (blueprint 12.3, 12.4, 12.5): valuation against the price guide, when a manager must approve, VAT on a line under
the profit margin mechanism, and how much of a trade-in a sale takes. Kite.Sales.TradeInMath (C#) and @kite/pos
tradein.ts (TypeScript) must both reproduce every vector. Run: python3 tests/vectors/generators/tradein_vectors.py"""
import json, random
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path

D = Decimal
BAISA = D("0.001")

def money(x):
    """Half away from zero to the baisa (amounts here are never negative when rounded)."""
    sign = -1 if x < 0 else 1
    return sign * abs(x).quantize(BAISA, rounding=ROUND_HALF_UP)

def s(x): return None if x is None else format(x, "f") if x == x.quantize(BAISA) else format(money(x), "f")

def value(guide, deductions):
    amounts = []
    for code, kind, v in deductions:
        if kind == "amount": amounts.append(v)
        else:
            if guide is None: return None            # a percentage needs a guide value: refused
            amounts.append(money(guide * v / D(100)))
    total = sum(amounts, D(0))
    offered = None if guide is None else max(D(0), guide - total)
    return {"amounts": [s(a) for a in amounts], "total": s(total), "offered": s(offered)}

def needs_approval(agreed, offered, staff_limit, over_pct):
    if offered is None: return True
    if agreed > staff_limit: return True
    return agreed > money(offered * (D(100) + over_pct) / D(100))

def margin_line(gross, purchase, rate):
    margin = gross - purchase
    tax = money(margin * rate / (D(100) + rate)) if margin > 0 else D(0)
    return {"net": s(gross - tax), "tax": s(tax), "gross": s(gross), "margin": s(margin)}

def apply(outstanding, due):
    applied = min(outstanding, due)
    return {"applied": s(applied), "left": s(outstanding - applied)}

def amt(rng, lo, hi): return D(rng.randint(int(lo * 1000), int(hi * 1000))) / D(1000)

rng = random.Random(70716)
out = {"comment": "Shared trade-in vectors written by an independent Python Decimal implementation; Kite.Sales.TradeInMath (C#) and @kite/pos tradein.ts (TypeScript) must both reproduce them.",
       "valuation": [], "approval": [], "margin": [], "apply": []}

# ---- valuation: fixed cases, then random ones
fixed = [
    (D("120.000"), [("SCREEN", "amount", D("25.000")), ("BATTERY", "percent", D("10"))]),
    (D("99.999"), [("BODY", "percent", D("12.5"))]),                       # 12.499875 -> 12.500
    (D("10.000"), [("SCREEN", "amount", D("25.000"))]),                    # never below zero
    (None, [("SCREEN", "amount", D("5.000"))]),                            # no guide: no offer
    (None, [("BATTERY", "percent", D("10"))]),                             # refused
    (D("0.000"), []),
    (D("333.333"), [("A", "percent", D("33.333")), ("B", "percent", D("0.001")), ("C", "amount", D("0.001"))]),
]
for _ in range(40):
    guide = None if rng.random() < 0.15 else amt(rng, 0, 600)
    ds = []
    for k in range(rng.randint(0, 4)):
        if rng.random() < 0.5 and guide is not None: ds.append((f"P{k}", "percent", D(rng.randint(0, 10000)) / D(100)))
        else: ds.append((f"A{k}", "amount", amt(rng, 0, 80)))
    fixed.append((guide, ds))
for guide, ds in fixed:
    out["valuation"].append({"guide": s(guide), "deductions": [{"code": c, "kind": k, "value": s(v) if k == "amount" else format(v, "f")} for c, k, v in ds],
                             "expect": value(guide, ds)})

# ---- approval
cases = [(D("100.000"), D("100.000"), D("50.000"), D("10")), (D("40.000"), D("40.000"), D("50.000"), D("10")), (D("44.000"), D("40.000"), D("50.000"), D("10")),
         (D("44.001"), D("40.000"), D("50.000"), D("10")), (D("10.000"), None, D("50.000"), D("10")), (D("50.000"), D("49.999"), D("50.000"), D("0")),
         (D("0.000"), D("0.000"), D("0.000"), D("0")), (D("33.334"), D("30.303"), D("100.000"), D("10"))]
for _ in range(40):
    offered = None if rng.random() < 0.1 else amt(rng, 0, 300)
    agreed = amt(rng, 0, 350) if offered is None else max(D(0), money(offered * D(rng.randint(80, 125)) / D(100)))
    cases.append((agreed, offered, D(rng.choice([0, 25, 50, 100, 250])), D(rng.choice([0, 5, 10, 15, 20]))))
for agreed, offered, limit, pct in cases:
    out["approval"].append({"agreed": s(agreed), "offered": s(offered), "staffLimit": s(limit), "overGuidePercent": format(pct, "f"),
                            "expect": needs_approval(agreed, offered, limit, pct)})

# ---- margin VAT
cases = [(D("150.000"), D("100.000"), D("5")), (D("100.000"), D("100.000"), D("5")), (D("90.000"), D("100.000"), D("5")),
         (D("187.000"), D("95.000"), D("5")), (D("100.010"), D("100.000"), D("5")), (D("0.000"), D("0.000"), D("5")), (D("250.000"), D("120.000"), D("0"))]
for _ in range(60):
    purchase = amt(rng, 0, 400)
    cases.append((max(D(0), purchase + amt(rng, -50, 150)), purchase, D("5")))
for gross, purchase, rate in cases:
    out["margin"].append({"gross": s(gross), "purchase": s(purchase), "rate": format(rate, "f"), "expect": margin_line(gross, purchase, rate)})

# ---- applying a trade-in to a sale
for outstanding, due in [(D("120.000"), D("219.000")), (D("120.000"), D("80.000")), (D("50.000"), D("50.000")), (D("0.000"), D("10.000"))]:
    out["apply"].append({"outstanding": s(outstanding), "due": s(due), "expect": apply(outstanding, due)})

path = Path(__file__).resolve().parents[1] / "tradein-math-vectors.json"
path.write_text(json.dumps(out, indent=1, ensure_ascii=False) + "\n")
print(f"wrote {path}: {len(out['valuation'])} valuations, {len(out['approval'])} approvals, {len(out['margin'])} margin lines, {len(out['apply'])} applications")
