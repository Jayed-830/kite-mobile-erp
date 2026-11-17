#!/usr/bin/env python3
"""Writes tests/vectors/credit-math-vectors.json from an independent Python implementation of the Phase 10 credit
arithmetic (blueprint 14.2 to 14.5):
- available credit online: limit - head office exposure - the asking branch's charges head office has not seen, never below 0;
- available credit offline: the lower of (branch allowance - charges since the last sync) and (limit - last known exposure -
  charges since the last sync), never more than the offline per-sale cap, never below 0;
- the decision on a credit sale: refused on hold or closed; going over the available credit, or selling while an invoice is
  overdue past the policy, needs an approval; going over by more than the override cap is refused even with one;
- instalments: the total split into equal parts to the baisa, the spare baisa on the first parts; the first due after the
  terms, the rest at the interval;
- allocation of a payment: oldest due date first (ties in the order given), never past an item's open amount;
- aging of open items by days past due: current, 1-30, 31-60, 61-90, over 90, and what is on account.
Kite.Sales.CreditMath (C#) and @kite/pos credit.ts (TypeScript) must both reproduce every vector.
Run: python3 tests/vectors/generators/credit_vectors.py"""
import json, random
from datetime import date, timedelta
from decimal import Decimal
from pathlib import Path

D = Decimal
BAISA = D("0.001")

def s(x): return format(x.quantize(BAISA), "f")

def available_online(limit, exposure, unsynced):
    return max(D(0), limit - exposure - unsynced)

def available_offline(allowance, limit, last_exposure, since_sync, cap):
    a = min(allowance - since_sync, limit - last_exposure - since_sync)
    return max(D(0), min(a, cap))

def decide(amount, available, override_cap, overdue_days, block_days, status, has_approval):
    if status in ("hold", "closed"):
        return {"outcome": status, "overBy": s(D(0))}
    over = max(D(0), amount - available)
    blocked = overdue_days > block_days
    if over > override_cap:
        return {"outcome": "over_cap", "overBy": s(over)}
    if over > 0 or blocked:
        return {"outcome": "approved" if has_approval else "needs_approval", "overBy": s(over)}
    return {"outcome": "ok", "overBy": s(over)}

def instalments(total, count, sale_date, terms, interval):
    baisa = int(total / BAISA)
    base, spare = divmod(baisa, count)
    out = []
    for k in range(count):
        amount = D(base + (1 if k < spare else 0)) * BAISA
        due = sale_date + timedelta(days=terms + k * interval)
        out.append({"seq": k + 1, "due": due.isoformat(), "amount": s(amount)})
    return out

def allocate(items, amount):
    left = amount
    out = []
    for idx, it in sorted(enumerate(items), key=lambda p: (p[1]["due"], p[0])):
        if left <= 0: break
        if it["open"] <= 0: continue
        take = min(left, it["open"])
        out.append({"id": it["id"], "amount": s(take)})
        left -= take
    return {"allocations": out, "left": s(left)}

def aging(items, unallocated, as_of):
    b = {"current": D(0), "d1_30": D(0), "d31_60": D(0), "d61_90": D(0), "d90plus": D(0)}
    for it in items:
        days = (as_of - it["due"]).days
        key = "current" if days <= 0 else "d1_30" if days <= 30 else "d31_60" if days <= 60 else "d61_90" if days <= 90 else "d90plus"
        b[key] += it["open"]
    total = sum(b.values()) - unallocated
    return {k: s(v) for k, v in b.items()} | {"onAccount": s(unallocated), "total": s(total)}

def amt(r, lo=0, hi=500000): return D(r.randint(lo, hi)) * BAISA

def main():
    r = random.Random(20261117)
    online, offline, decisions, plans, allocs, agings = [], [], [], [], [], []
    # The test table's case (25): limit 500, two branches with 300 and 200, 100 known exposure.
    offline.append(dict(allowance=D(300), limit=D(500), lastExposure=D(100), sinceSync=D(0), cap=D(1000)))
    offline.append(dict(allowance=D(200), limit=D(500), lastExposure=D(100), sinceSync=D(150), cap=D(100)))
    for _ in range(80):
        limit = amt(r)
        online.append(dict(limit=limit, exposure=amt(r, -50000, 600000), unsynced=amt(r, 0, 100000) if r.random() < 0.5 else D(0)))
        offline.append(dict(allowance=amt(r, 0, 300000), limit=limit, lastExposure=amt(r, -20000, 500000),
                            sinceSync=amt(r, 0, 200000) if r.random() < 0.6 else D(0), cap=amt(r, 0, 200000)))
    for _ in range(100):
        decisions.append(dict(amount=amt(r, 1, 300000), available=amt(r, 0, 300000), overrideCap=amt(r, 0, 80000),
                              overdueDays=r.choice([0, 0, 0, 5, 29, 30, 31, 45, 61, 120]), blockDays=r.choice([0, 30, 60]),
                              status=r.choice(["active"] * 6 + ["hold", "closed"]), hasApproval=r.random() < 0.5))
    for _ in range(60):
        plans.append(dict(total=amt(r, 1, 900000), count=r.choice([1, 1, 2, 3, 4, 6, 7, 12]),
                          saleDate=date(2026, 1, 1) + timedelta(days=r.randint(0, 700)), terms=r.choice([0, 7, 14, 30, 45, 60]),
                          interval=r.choice([7, 14, 30, 31])))
    for n in range(60):
        k = r.randint(1, 6)
        items = [dict(id=f"i{n}-{j}", due=date(2026, 1, 1) + timedelta(days=r.randint(0, 200)), open=amt(r, 0, 150000)) for j in range(k)]
        if r.random() < 0.3 and k > 1: items[1]["due"] = items[0]["due"]            # ties keep the order given
        allocs.append(dict(items=items, amount=amt(r, 1, 500000)))
    for _ in range(60):
        k = r.randint(0, 8)
        items = [dict(due=date(2026, 1, 1) + timedelta(days=r.randint(0, 300)), open=amt(r, 1, 150000)) for _ in range(k)]
        agings.append(dict(items=items, unallocated=amt(r, 0, 50000) if r.random() < 0.3 else D(0),
                           asOf=date(2026, 1, 1) + timedelta(days=r.randint(0, 400))))

    def ser(v):
        if isinstance(v, D): return s(v)
        if isinstance(v, date): return v.isoformat()
        if isinstance(v, list): return [ser(x) for x in v]
        if isinstance(v, dict): return {k: ser(x) for k, x in v.items()}
        return v
    out = {
        "online": [ser(c) | {"expect": s(available_online(c["limit"], c["exposure"], c["unsynced"]))} for c in online],
        "offline": [ser(c) | {"expect": s(available_offline(c["allowance"], c["limit"], c["lastExposure"], c["sinceSync"], c["cap"]))} for c in offline],
        "decide": [ser(c) | {"expect": decide(c["amount"], c["available"], c["overrideCap"], c["overdueDays"], c["blockDays"], c["status"], c["hasApproval"])} for c in decisions],
        "instalments": [ser(c) | {"expect": instalments(c["total"], c["count"], c["saleDate"], c["terms"], c["interval"])} for c in plans],
        "allocate": [ser(c) | {"expect": allocate(c["items"], c["amount"])} for c in allocs],
        "aging": [ser(c) | {"expect": aging(c["items"], c["unallocated"], c["asOf"])} for c in agings],
    }
    path = Path(__file__).resolve().parents[1] / "credit-math-vectors.json"
    path.write_text(json.dumps(out, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {sum(len(v) for v in out.values())} credit vectors to {path}")

if __name__ == "__main__":
    main()
