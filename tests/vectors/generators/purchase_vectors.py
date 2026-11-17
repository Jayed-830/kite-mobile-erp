#!/usr/bin/env python3
"""Writes tests/vectors/purchase-math-vectors.json from an independent Python implementation of the Phase 11 purchasing
arithmetic (blueprint 16.2, 16.3):
- a line: quantity x price before VAT to the baisa, and its VAT at the rate, half away from zero;
- spreading an amount over weights by cumulative rounding, so the shares add up exactly (evenly when every weight is zero);
- landed costs: each charge spread over the receipt lines by their value or by their quantity;
- the three-way match of an invoice line: not more than the receipt still has open, and a unit price within the percentage
  tolerance of the order's price (the receipt's without an order), or a whole-line difference within the amount tolerance;
- what an invoice clears from goods received not invoiced: all that is open when it invoices all that is open, else its share;
- a price difference split between the stock still on hand and purchase price variance;
- an amount split evenly over n parts to the baisa, the spare baisa on the first parts.
Kite.Sales.PurchaseMath (C#) and @kite/pos purchase.ts (TypeScript) must both reproduce every vector.
Run: python3 tests/vectors/generators/purchase_vectors.py"""
import json, random
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path

D = Decimal
BAISA = D("0.001")

def r3(x): return x.quantize(BAISA, rounding=ROUND_HALF_UP)
def s(x): return format(r3(x), "f")

def line_net(qty, price): return r3(qty * price)
def line_tax(net, rate): return r3(net * rate / 100)

def spread(amount, weights):
    if not weights: return []
    total = sum(weights)
    w = weights if total != 0 else [D(1)] * len(weights)
    if total == 0: total = D(len(weights))
    out, cum, before = [], D(0), D(0)
    for x in w:
        cum += x
        upto = r3(amount * cum / total)
        out.append(upto - before)
        before = upto
    return out

def spread_charges(lines, charges):
    shares = [D(0)] * len(lines)
    for amount, basis in charges:
        sh = spread(amount, [q if basis == "qty" else v for v, q in lines])
        shares = [a + b for a, b in zip(shares, sh)]
    return shares

def match(inv_qty, inv_price, open_qty, receipt_price, order_price, pct, amount_tol):
    ref = order_price if order_price is not None else receipt_price
    diff = inv_price - ref
    line_diff = line_net(inv_qty, inv_price) - line_net(inv_qty, ref)
    if inv_qty > open_qty: status = "qty"
    else:
        ok = abs(diff) <= r3(ref * pct / 100) or abs(line_diff) <= amount_tol
        status = "ok" if ok else "price"
    return status, ref, diff, line_diff

def clearing(inv_qty, open_qty, open_value):
    if open_qty <= 0: return D(0)
    if inv_qty >= open_qty: return open_value
    return r3(open_value * inv_qty / open_qty)

def split_variance(variance, inv_qty, on_hand):
    if inv_qty <= 0: return D(0), variance
    share = min(max(on_hand, D(0)), inv_qty)
    inv = r3(variance * share / inv_qty)
    return inv, variance - inv

def split_even(amount, n):
    if n < 1: return []
    b = int((abs(amount) * 1000).to_integral_value(rounding=ROUND_HALF_UP))
    sign = -1 if amount < 0 else 1
    each, spare = divmod(b, n)
    return [D(sign * (each + (1 if k < spare else 0))) / 1000 for k in range(n)]

def money(rng, lo, hi): return D(rng.randint(int(lo * 1000), int(hi * 1000))) / 1000
def qty(rng): return D(rng.choice([1, 2, 3, 5, 7, 10, 12, 24, 50, 100])) if rng.random() < 0.8 else D(rng.randint(1, 9999)) / 1000 * 10

def main():
    rng = random.Random(20261117)
    out = {"lines": [], "spread": [], "spreadCharges": [], "match": [], "clearing": [], "splitVariance": [], "splitEven": []}
    for _ in range(80):
        q, p, rate = qty(rng), money(rng, 0, 400), D(rng.choice([0, 5, 5, 5, 10]))
        net = line_net(q, p)
        out["lines"].append({"qty": format(q, "f"), "unitPrice": s(p), "rate": format(rate, "f"), "net": s(net), "tax": s(line_tax(net, rate))})
    for _ in range(80):
        n = rng.randint(1, 7)
        weights = [D(0) if rng.random() < 0.1 else money(rng, 0, 500) for _ in range(n)]
        if rng.random() < 0.05: weights = [D(0)] * n
        amt = money(rng, 0, 300) * (-1 if rng.random() < 0.1 else 1)
        out["spread"].append({"amount": s(amt), "weights": [s(w) for w in weights], "shares": [s(x) for x in spread(amt, weights)]})
    for _ in range(60):
        lines = [(money(rng, 0, 900), D(rng.randint(1, 40))) for _ in range(rng.randint(1, 6))]
        charges = [(money(rng, 0.001, 120), rng.choice(["value", "qty"])) for _ in range(rng.randint(1, 3))]
        out["spreadCharges"].append({"lines": [{"value": s(v), "qty": format(q, "f")} for v, q in lines],
                                     "charges": [{"amount": s(a), "basis": b} for a, b in charges],
                                     "shares": [s(x) for x in spread_charges(lines, charges)]})
    for _ in range(120):
        ref = money(rng, 0.5, 300)
        order = None if rng.random() < 0.2 else ref
        receipt = ref if order is not None else money(rng, 0.5, 300)
        base = order if order is not None else receipt
        kind = rng.random()
        if kind < 0.4: price = base
        elif kind < 0.7: price = r3(base * (1 + D(rng.randint(-300, 300)) / 10000))
        else: price = r3(base * (1 + D(rng.randint(-2000, 2000)) / 10000))
        open_q = D(rng.randint(1, 50))
        inv_q = open_q if rng.random() < 0.6 else D(rng.randint(1, 60))
        pct, tol = D(rng.choice([0, 1, 2, 5])), D(rng.choice(["0", "0.500", "1", "5"]))
        st, rf, diff, ld = match(inv_q, price, open_q, receipt, order, pct, tol)
        out["match"].append({"invoiceQty": format(inv_q, "f"), "invoicePrice": s(price), "openQty": format(open_q, "f"), "receiptPrice": s(receipt),
                             "orderPrice": None if order is None else s(order), "tolerancePct": format(pct, "f"), "toleranceAmount": s(tol),
                             "status": st, "reference": s(rf), "priceDifference": s(diff), "lineDifference": s(ld)})
    for _ in range(60):
        open_q = D(rng.randint(1, 40)); open_v = money(rng, 0, 4000)
        inv_q = open_q if rng.random() < 0.4 else D(rng.randint(1, int(open_q)))
        out["clearing"].append({"invoiceQty": format(inv_q, "f"), "openQty": format(open_q, "f"), "openValue": s(open_v), "cleared": s(clearing(inv_q, open_q, open_v))})
    out["clearing"].append({"invoiceQty": "1", "openQty": "0", "openValue": "0.000", "cleared": "0.000"})
    for _ in range(60):
        var = money(rng, -80, 80); inv_q = D(rng.randint(1, 30)); on_hand = D(rng.randint(-5, 40))
        a, b = split_variance(var, inv_q, on_hand)
        out["splitVariance"].append({"variance": s(var), "invoiceQty": format(inv_q, "f"), "onHandQty": format(on_hand, "f"), "inventory": s(a), "ppv": s(b)})
    for _ in range(40):
        amt = money(rng, -50, 50); n = rng.randint(1, 9)
        out["splitEven"].append({"amount": s(amt), "n": n, "parts": [s(x) for x in split_even(amt, n)]})
    path = Path(__file__).resolve().parents[1] / "purchase-math-vectors.json"
    path.write_text(json.dumps(out, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {path} ({sum(len(v) for v in out.values())} vectors)")

if __name__ == "__main__":
    main()
