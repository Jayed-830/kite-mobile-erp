#!/usr/bin/env python3
"""Writes tests/vectors/device-math-vectors.json from an independent Python implementation of the Phase 5 device
rules (blueprint 11.2, 12.1, 12.2, 12.4). Kite.Devices.DeviceMath (C#) and @kite/pos devices.ts (TypeScript) must
both reproduce every vector. Run: python3 tests/vectors/generators/device_vectors.py"""
import json, random, re
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path

D = Decimal
BAISA = D("0.001")

def luhn_ok(s):
    if not re.fullmatch(r"[0-9]{15}", s): return False
    total = 0
    for i, ch in enumerate(s):          # from the left: every second digit (positions 2, 4, ... 14) doubled
        d = int(ch)
        if i % 2 == 1:
            d *= 2
            if d > 9: d -= 9
        total += d
    return total % 10 == 0

def check_digit(s14):
    for c in "0123456789":
        if luhn_ok(s14 + c): return c
    raise AssertionError

def classify(raw):
    s = (raw or "").strip().replace(" ", "").upper()
    if s and s.isdigit():
        if len(s) == 15: return ("imei" if luhn_ok(s) else "invalid_imei", s)
        if len(s) in (14, 16): return ("invalid_imei", s)
    return ("serial", s) if re.fullmatch(r"[A-Z0-9][A-Z0-9-]{3,39}", s) else ("unknown", s)

def money(x):  # half away from zero for the positive amounts used here
    return x.quantize(BAISA, rounding=ROUND_HALF_UP)

def labour(minutes, rate): return money(D(minutes) * rate / D(60))

def suggested(new_price, percent, step):
    if new_price is None or percent is None or step <= 0: return None
    return money((new_price * percent / D(100) / step).quantize(D(1), rounding=ROUND_HALF_UP) * step)

def unit_list(selling, condition, new_price, percent, step):
    if selling is not None: return selling
    return new_price if condition == "brand_new" else suggested(new_price, percent, step)

CHECKLIST = [
    ("screen", "pass_fail", True, None, "refurbish"), ("touch", "pass_fail", True, None, "refurbish"),
    ("body", "pass_fail", True, None, "damaged"), ("buttons", "pass_fail", True, None, "refurbish"),
    ("cameras", "pass_fail", True, None, "refurbish"), ("audio", "pass_fail", True, None, "refurbish"),
    ("charging", "pass_fail", True, None, "refurbish"), ("network", "pass_fail", True, None, "refurbish"),
    ("biometrics", "pass_fail", True, None, "damaged"), ("water", "pass_fail", True, None, "for_parts"),
    ("battery", "percent", True, 80, "refurbish"), ("cloud_lock", "pass_fail", True, None, "for_parts"),
    ("notes", "text", False, None, "refurbish"),
]

def evaluate(answers, current):
    missing, failed, battery, worst = [], [], None, 0
    for code, kind, required, minimum, on_fail in CHECKLIST:
        v = answers.get(code)
        v = None if v is None or str(v).strip() == "" else str(v).strip()
        passed = None
        if kind == "pass_fail":
            if v is None:
                if required: missing.append(code)
            else:
                passed = v.lower() == "pass"
        elif kind == "percent":
            if v is None:
                if required: missing.append(code)
            else:
                pct = int(v); passed = pct >= minimum
                if code == "battery": battery = pct
        if passed is False:
            failed.append(code)
            worst = max(worst, {"for_parts": 3, "refurbish": 2}.get(on_fail, 1))
    outcome, condition = {3: ("for_parts", "for_parts"), 2: ("refurbish", "used"), 1: ("restock", "damaged")}.get(
        worst, ("restock", "used" if current == "for_parts" else current))
    return {"complete": not missing, "missing": missing, "failed": failed, "outcome": outcome, "condition": condition, "battery": battery}

def s(x): return None if x is None else str(x)

rng = random.Random(20261116)
out = {"comment": "Phase 5 device vectors written by an independent Python implementation (tests/vectors/generators/device_vectors.py); "
                  "Kite.Devices.DeviceMath (C#) and @kite/pos devices.ts (TypeScript) must both reproduce them.",
       "checklist": [{"code": c, "kind": k, "required": r, "minPercent": m, "onFail": f} for c, k, r, m, f in CHECKLIST]}

imeis = []
for base in ["35209900176148", "49015420323751", "35467011234567", "86012345678901"]:
    good = base + check_digit(base)
    imeis.append({"value": good, "valid": True})
    imeis.append({"value": good[:14] + str((int(good[14]) + 1) % 10), "valid": False})
for _ in range(8):
    b = "35" + "".join(rng.choice("0123456789") for _ in range(12)); g = b + check_digit(b)
    imeis.append({"value": g, "valid": True})
imeis += [{"value": "3520990017614", "valid": False}, {"value": "35209900176148A", "valid": False}, {"value": "", "valid": False}]
out["imei"] = imeis

raws = ["352099001761481", " 3520 9900 1761 481 ", "352099001761482", "35209900176148", "3520990017614810", "f2lx9kq1", "SN-001", "ab", "C39X 4K2L",
        "DX3VR0ZQJCLF", "!!", "12345678901234567890"]
out["classify"] = [{"raw": r, "kind": classify(r)[0], "value": classify(r)[1]} for r in raws]

labour_cases = [(45, "4.000"), (1, "3.000"), (7, "2.500"), (13, "3.333"), (60, "0"), (90, "7.125"), (25, "1.111"), (6000, "9.999")]
out["labour"] = [{"minutes": m, "rate": r, "amount": str(labour(m, D(r)))} for m, r in labour_cases]

sug = [("220.000", "85", "0.500"), ("220.000", "85", "0.100"), ("199.900", "70", "0.100"), ("349.000", "55", "1.000"), ("10.333", "85", "0.005"),
       ("0.105", "50", "0.010"), (None, "85", "0.100"), ("300.000", None, "0.100"), ("123.456", "62.5", "0.050")]
out["suggested"] = [{"newPrice": n, "percent": p, "step": st, "price": s(suggested(None if n is None else D(n), None if p is None else D(p), D(st)))} for n, p, st in sug]

ul = [("130.000", "used", "300.000", "70", "0.100"), (None, "brand_new", "300.000", None, "0.100"), (None, "used", "300.000", "70", "0.100"),
      (None, "used", "300.000", None, "0.100"), (None, "refurbished", "219.000", "85", "0.500"), (None, "brand_new", None, None, "0.100"),
      ("0.000", "damaged", "80.000", "55", "0.100")]
out["unitListPrice"] = [{"sellingPrice": a, "condition": c, "newPrice": n, "percent": p, "step": st,
                         "price": s(unit_list(None if a is None else D(a), c, None if n is None else D(n), None if p is None else D(p), D(st)))} for a, c, n, p, st in ul]

def all_pass(b=92):
    d = {c: "pass" for c, k, *_ in CHECKLIST if k == "pass_fail"}; d["battery"] = str(b); return d
ev = []
ev.append((all_pass(), "used")); ev.append((all_pass(), "brand_new")); ev.append((all_pass(), "refurbished"))
a = all_pass(71); ev.append((a, "used"))
a = all_pass(); a["body"] = "fail"; ev.append((a, "used"))
a = all_pass(); a["water"] = "fail"; a["screen"] = "fail"; ev.append((a, "used"))
a = all_pass(); a["cloud_lock"] = "fail"; ev.append((a, "brand_new"))
a = all_pass(); del a["touch"]; ev.append((a, "used"))
a = all_pass(); del a["battery"]; a["biometrics"] = "FAIL"; ev.append((a, "used"))
a = all_pass(80); a["notes"] = "small scratch on the back"; ev.append((a, "damaged"))
a = all_pass(79); a["body"] = "fail"; ev.append((a, "used"))
for _ in range(10):
    a = {}
    for c, k, *_ in CHECKLIST:
        if k == "pass_fail":
            r = rng.random()
            if r < 0.85: a[c] = "pass"
            elif r < 0.97: a[c] = "fail"
        elif k == "percent" and rng.random() < 0.95:
            a[c] = str(rng.randint(55, 100))
    ev.append((a, rng.choice(["used", "brand_new", "refurbished", "damaged"])))
out["evaluate"] = [{"answers": a, "current": cur, "expect": evaluate(a, cur)} for a, cur in ev]

path = Path(__file__).resolve().parents[1] / "device-math-vectors.json"
path.write_text(json.dumps(out, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"wrote {path}: {len(out['imei'])} IMEIs, {len(out['classify'])} codes, {len(out['labour'])} labour, {len(out['suggested'])} suggested, "
      f"{len(out['unitListPrice'])} unit prices, {len(out['evaluate'])} inspections")
