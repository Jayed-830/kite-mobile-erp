#!/usr/bin/env python3
"""Writes tests/vectors/repair-math-vectors.json from an independent Python implementation of the Phase 8 repair
arithmetic (blueprint 13.3, 13.5, 13.7): labour charged by the hour, the VAT inside a deposit when deposits are a VAT point,
the share of that VAT going with part of the deposit, how a deposit is split at delivery (applied, still due, given back),
spreading a job's parts cost over its invoice's part lines, and the cost a part returned from a job takes back.
Kite.Sales.RepairMath (C#) and @kite/pos repair.ts (TypeScript) must both reproduce every vector.
Run: python3 tests/vectors/generators/repair_vectors.py"""
import json, random
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path

D = Decimal
BAISA = D("0.001")

def money(x):
    sign = -1 if x < 0 else 1
    return sign * abs(x).quantize(BAISA, rounding=ROUND_HALF_UP)

def s(x): return format(money(x), "f")

def labour(minutes, rate): return money(rate * minutes / D(60))

def deposit_tax(amount, rate): return D(0) if amount <= 0 or rate <= 0 else money(amount * rate / (D(100) + rate))

def tax_share(held, balance, part):
    if held == 0 or part == 0: return D(0)
    return held if part == balance else money(held * part / balance)

def split(gross, balance, tax):
    applied = min(gross, balance)
    excess = balance - applied
    excess_tax = tax_share(tax, balance, excess)
    return {"applied": s(applied), "due": s(gross - applied), "excess": s(excess), "appliedTax": s(tax - excess_tax), "excessTax": s(excess_tax)}

def allocate(cost, weights):
    if not weights: return []
    total = sum(weights, D(0))
    out = [D(0)] * len(weights)
    if total <= 0:
        out[-1] = cost
        return [s(x) for x in out]
    left = cost
    for i in range(len(weights) - 1):
        out[i] = money(cost * weights[i] / total)
        left -= out[i]
    out[-1] = left
    return [s(x) for x in out]

def return_cost(fq, fc, rq, rc, q):
    return fc - rc if rq + q == fq else money(fc * q / fq)

def amt(r, lo=0, hi=200000):
    return D(r.randint(lo, hi)) / D(1000)

def main():
    r = random.Random(20261116)
    out = {"labour": [], "depositTax": [], "taxShare": [], "split": [], "allocate": [], "returnCost": []}
    for minutes, rate in [(1, D("10")), (30, D("10")), (45, D("12.5")), (7, D("8.333")), (90, D("0")), (6000, D("99.999")), (61, D("7.777"))]:
        out["labour"].append({"minutes": minutes, "rate": s(rate), "expect": s(labour(minutes, rate))})
    for _ in range(40):
        minutes, rate = r.randint(1, 600), amt(r, 0, 40000)
        out["labour"].append({"minutes": minutes, "rate": s(rate), "expect": s(labour(minutes, rate))})
    for a, rate in [(D("21"), D("5")), (D("10"), D("5")), (D("0.001"), D("5")), (D("0"), D("5")), (D("10"), D("0")), (D("0.021"), D("5"))]:
        out["depositTax"].append({"amount": s(a), "rate": s(rate), "expect": s(deposit_tax(a, rate))})
    for _ in range(40):
        a, rate = amt(r, 1, 300000), D(r.choice(["5", "5", "0", "10", "15"]))
        out["depositTax"].append({"amount": s(a), "rate": s(rate), "expect": s(deposit_tax(a, rate))})
    for _ in range(50):
        balance = amt(r, 1, 300000)
        held = deposit_tax(balance, D(5))
        part = balance if r.random() < 0.2 else D(0) if r.random() < 0.1 else money(balance * D(r.randint(1, 999)) / D(1000))
        out["taxShare"].append({"held": s(held), "balance": s(balance), "part": s(part), "expect": s(tax_share(held, balance, part))})
    for _ in range(60):
        gross = D(0) if r.random() < 0.1 else amt(r, 0, 300000)
        balance = D(0) if r.random() < 0.2 else amt(r, 0, 300000)
        tax = deposit_tax(balance, D(5)) if r.random() < 0.5 else D(0)
        out["split"].append({"gross": s(gross), "balance": s(balance), "tax": s(tax), "expect": split(gross, balance, tax)})
    out["split"].append({"gross": "36.000", "balance": "10.000", "tax": "0.000", "expect": split(D("36"), D("10"), D("0"))})
    out["split"].append({"gross": "3.000", "balance": "20.000", "tax": "0.952", "expect": split(D("3"), D("20"), D("0.952"))})
    for _ in range(40):
        n = r.randint(1, 5)
        weights = [amt(r, 0, 80000) for _ in range(n)]
        cost = amt(r, 1, 90000)
        out["allocate"].append({"cost": s(cost), "weights": [s(w) for w in weights], "expect": allocate(cost, weights)})
    out["allocate"].append({"cost": "12.000", "weights": ["0.000", "0.000"], "expect": allocate(D("12"), [D(0), D(0)])})
    for _ in range(40):
        fq = D(r.randint(1, 12))
        fc = amt(r, 1, 120000)
        rq = D(r.randint(0, int(fq) - 1))
        rc = money(fc * rq / fq)
        q = D(r.randint(1, int(fq - rq)))
        out["returnCost"].append({"fittedQty": s(fq), "fittedCost": s(fc), "returnedQty": s(rq), "returnedCost": s(rc), "qty": s(q),
                                  "expect": s(return_cost(fq, fc, rq, rc, q))})
    path = Path(__file__).resolve().parents[1] / "repair-math-vectors.json"
    path.write_text(json.dumps(out, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {path} ({sum(len(v) for v in out.values())} vectors)")

if __name__ == "__main__":
    main()
