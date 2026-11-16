// In-memory Phase 9 technician commissions for tests, `npm run dev:mock` and design review: versioned rules with the most
// specific one winning, generation once per job, technician and invoice version, reversal and clawback, approval at the
// branch or head office, payouts with one line per accrual, and the reports. The arithmetic is @kite/pos (the same vectors
// as the server); the real server and database check everything again and are the source of truth.
import {
  ApiError, type AccrualStatus, type CommissionAccrual, type CommissionPayout, type CommissionRule, type CommissionRuleInput, type CommissionSettings, type RepairApi, type User,
} from '@kite/api';
import { commission, toBaisa, toNumber } from '@kite/pos';

const problem = (status: number, code: string, title: string) => new ApiError({ status, code, title });
const id = () => crypto.randomUUID();
const now = () => new Date().toISOString();
const today = () => now().slice(0, 10);
const delay = <T>(v: T): Promise<T> => new Promise((r) => setTimeout(() => r(v), 15));
const b = (x: number) => toBaisa(x);

export type CommissionApi = Pick<RepairApi, 'commissionSettings' | 'updateCommissionSettings' | 'commissionRules' | 'createCommissionRule' | 'newCommissionRuleVersion'
  | 'endCommissionRule' | 'commissions' | 'myCommissions' | 'approveCommissions' | 'reverseCommission' | 'generateCommissions' | 'commissionsPayable' | 'payCommissions'
  | 'commissionPayouts' | 'commissionStatement' | 'technicianPerformance' | 'commissionRuleUsage'>;

/** What the commission engine needs to know about a repair job. */
export interface CommissionJob {
  id: string; number: string; branchId: string; branchCode: string; technicianId: string | null; categoryId: string; status: string; outcome: string | null;
  warranty: boolean; warrantyOfId: string | null; saleId: string | null; saleNumber: string | null; invoiceVersion: number; shares: { technicianId: string; share: number }[];
  basis: { labour: number; partsRevenue: number; partsCost: number; external: number }; createdAt: string; deliveredAt: string | null;
}

export interface CommissionMockHooks {
  need: (perm?: string) => User;
  can: (perm: string) => boolean;
  next: (kind: string, prefix: string) => string;
  users: () => User[];
  jobs: () => CommissionJob[];
  categoryName: (categoryId: string) => { en: string; ar: string } | null;
}

/** blueprint 15.1: technician and category, technician, branch and category, category, branch, everyone. */
export const scopeRank = (r: { technicianId: string | null; branchId: string | null; categoryId: string | null }) =>
  r.technicianId && r.categoryId ? 1 : r.technicianId ? 2 : r.branchId && r.categoryId ? 3 : r.categoryId ? 4 : r.branchId ? 5 : 6;

export function createMockCommissions(h: CommissionMockHooks) {
  let settings: CommissionSettings = { generateOn: 'delivered', approvalBy: 'branch', clawbackDays: 0, clawbackPercent: 100, version: 1 };
  const rules: CommissionRule[] = [];
  const accruals: CommissionAccrual[] = [];
  const payouts: (CommissionPayout & { accrualIds: string[] })[] = [];
  const userName = (uid: string | null) => h.users().find((u) => u.id === uid)?.displayNameEn ?? null;

  const writeRule = (r: CommissionRuleInput, key: string, version: number): CommissionRule => {
    if (!r.nameEn?.trim() || !r.nameAr?.trim()) throw problem(400, 'VAL-001', 'Name the rule in English and Arabic.');
    if (r.technicianId && r.branchId) throw problem(400, 'COM-001', "A technician's rule applies in every branch: choose a technician or a branch, not both.");
    // The arithmetic checks the figures the same way it will use them.
    try {
      commission(r.basis, r.basis === 'fixed' && r.amount != null ? b(r.amount) : null, r.basis === 'fixed' ? null : (r.rate ?? null),
        r.minAmount != null ? b(r.minAmount) : null, r.maxAmount != null ? b(r.maxAmount) : null, { labour: 0n, partsRevenue: 0n, partsCost: 0n, external: 0n }, 100);
    } catch (e) { throw problem(400, 'COM-001', (e as Error).message); }
    const scope = { technicianId: r.technicianId ?? null, branchId: r.branchId ?? null, categoryId: r.categoryId ?? null };
    const to = r.effectiveTo ?? null;
    const clash = rules.find((x) => x.status === 'active' && x.ruleKey !== key && x.technicianId === scope.technicianId && x.branchId === scope.branchId && x.categoryId === scope.categoryId
      && x.effectiveFrom <= (to ?? '9999-12-31') && (x.effectiveTo ?? '9999-12-31') >= r.effectiveFrom);
    if (clash) throw problem(409, 'COM-001', `Another rule for the same technicians (${clash.nameEn}) is in force then. End it first, or write its next version.`);
    const cat = scope.categoryId ? h.categoryName(scope.categoryId) : null;
    const row: CommissionRule = {
      id: id(), ruleKey: key, version, nameEn: r.nameEn.trim(), nameAr: r.nameAr.trim(), basis: r.basis, amount: r.basis === 'fixed' ? (r.amount ?? null) : null,
      rate: r.basis === 'fixed' ? null : (r.rate ?? null), minAmount: r.minAmount ?? null, maxAmount: r.maxAmount ?? null, ...scope, technicianName: userName(scope.technicianId),
      branchCode: scope.branchId ? 'B01' : null, categoryEn: cat?.en ?? null, categoryAr: cat?.ar ?? null, effectiveFrom: r.effectiveFrom, effectiveTo: to, status: 'active',
      rank: scopeRank(scope), latest: true, used: 0, createdAt: now(),
    };
    rules.filter((x) => x.ruleKey === key).forEach((x) => { x.latest = false; });
    rules.push(row);
    return row;
  };

  const resolve = (j: CommissionJob, tech: string, on: string) => rules
    .filter((r) => r.status === 'active' && r.effectiveFrom <= on && (!r.effectiveTo || r.effectiveTo >= on) && (!r.technicianId || r.technicianId === tech)
      && (!r.branchId || r.branchId === j.branchId) && (!r.categoryId || r.categoryId === j.categoryId))
    .sort((x, y) => x.rank - y.rank || y.effectiveFrom.localeCompare(x.effectiveFrom))[0] ?? null;

  const standing = (a: CommissionAccrual) => a.status !== 'reversed' && !accruals.some((r) => r.reversesId === a.id);

  /** Generates a delivered repair's commissions for its current invoice; returns how many were written (0 if they exist). */
  const generate = (j: CommissionJob, actor: User | null): number => {
    if (j.status !== 'delivered' || j.outcome !== 'repaired' || j.warranty || !j.saleId) return 0;
    const shares = j.shares.length ? j.shares : j.technicianId ? [{ technicianId: j.technicianId, share: 100 }] : [];
    let written = 0;
    for (const s of shares) {
      if (accruals.some((a) => a.jobId === j.id && a.technicianId === s.technicianId && a.kind === 'repair' && a.kindVersion === j.invoiceVersion)) continue;
      const rule = resolve(j, s.technicianId, today());
      if (!rule) continue;
      const basis = { labour: b(j.basis.labour), partsRevenue: b(j.basis.partsRevenue), partsCost: b(j.basis.partsCost), external: b(j.basis.external) };
      const r = commission(rule.basis, rule.amount != null ? b(rule.amount) : null, rule.rate, rule.minAmount != null ? b(rule.minAmount) : null,
        rule.maxAmount != null ? b(rule.maxAmount) : null, basis, s.share);
      const profit = j.basis.labour + j.basis.partsRevenue - j.basis.partsCost - j.basis.external;
      accruals.push({
        id: id(), jobId: j.id, jobNumber: j.number, branchId: j.branchId, branchCode: j.branchCode, technicianId: s.technicianId, technicianName: userName(s.technicianId) ?? '',
        kind: 'repair', kindVersion: j.invoiceVersion, amount: toNumber(r.amount), share: s.share, basis: rule.basis, basisAmounts: { ...j.basis, profit: Number(profit.toFixed(3)) },
        trace: { job: j.number, invoice: j.saleNumber, invoiceVersion: j.invoiceVersion, rule: { nameEn: rule.nameEn, version: rule.version, basis: rule.basis, rate: rule.rate, amount: rule.amount },
          base: toNumber(r.base), raw: toNumber(r.raw), clamped: toNumber(r.clamped), share: s.share, amount: toNumber(r.amount) },
        ruleKey: rule.ruleKey, ruleVersion: rule.version, ruleNameEn: rule.nameEn, ruleNameAr: rule.nameAr, invoiceNumber: j.saleNumber, accrualDate: today(), status: 'accrued',
        reversesId: null, reason: null, approvedBy: null, approvedAt: null, payoutNumber: null, createdAt: now(),
      });
      rule.used += 1;
      written++;
    }
    void actor;
    return written;
  };

  const reverse = (a: CommissionAccrual, reason: string, actor: User) => {
    if (!reason.trim()) throw problem(400, 'VAL-001', 'Give the reason for the reversal.');
    if (a.kind === 'reversal') throw problem(409, 'COM-002', 'A reversal is not reversed again.');
    if (!standing(a)) throw problem(409, 'COM-002', 'That commission was already reversed.');
    const paid = a.status === 'paid';
    const rev: CommissionAccrual = { ...a, id: id(), kind: 'reversal', amount: -a.amount, reversesId: a.id, reason: reason.trim(), accrualDate: today(), createdAt: now(),
      status: paid ? 'approved' : 'reversed', approvedBy: paid ? actor.displayNameEn : null, approvedAt: paid ? now() : null, payoutNumber: null,
      trace: { reverses: a.id, amount: a.amount, wasPaid: paid, reason: reason.trim() } };
    accruals.push(rev);
    if (!paid) Object.assign(a, { status: 'reversed' as AccrualStatus, reason: reason.trim() });
    return rev;
  };

  /** The invoice was credit-noted: its standing commissions are reversed. */
  const reverseInvoice = (jobId: string, saleNumber: string | null, reason: string, actor: User) => {
    accruals.filter((a) => a.jobId === jobId && a.kind === 'repair' && a.invoiceNumber === saleNumber && standing(a)).forEach((a) => reverse(a, reason, actor));
  };

  /** A warranty claim soon after delivery takes back part of the original technician's commission, once. */
  const clawback = (original: CommissionJob, claimNumber: string) => {
    if (!settings.clawbackDays || !original.deliveredAt || Date.now() > Date.parse(original.deliveredAt) + settings.clawbackDays * 864e5) return;
    const top = Math.max(0, ...accruals.filter((a) => a.jobId === original.id && a.kind === 'repair').map((a) => a.kindVersion));
    for (const a of accruals.filter((x) => x.jobId === original.id && x.kind === 'repair' && x.kindVersion === top && standing(x))) {
      if (accruals.some((x) => x.jobId === original.id && x.technicianId === a.technicianId && x.kind === 'clawback')) continue;
      const amount = -toNumber((b(a.amount) * BigInt(Math.round(settings.clawbackPercent * 10000)) + 500000n) / 1000000n);
      if (amount === 0) continue;
      accruals.push({ ...a, id: id(), kind: 'clawback', kindVersion: 1, amount, share: null, basis: null, ruleKey: null, ruleVersion: null, ruleNameEn: null, ruleNameAr: null,
        invoiceNumber: null, status: 'accrued', reversesId: null, reason: `Warranty claim ${claimNumber}`, approvedBy: null, approvedAt: null, payoutNumber: null, accrualDate: today(),
        createdAt: now(), trace: { original: original.number, claim: claimNumber, of: a.id, percent: settings.clawbackPercent } });
    }
  };

  const filterRows = (f: { status?: AccrualStatus; technicianId?: string; branchId?: string; jobId?: string; from?: string; to?: string } | undefined) =>
    accruals.filter((a) => (!f?.status || a.status === f.status) && (!f?.technicianId || a.technicianId === f.technicianId) && (!f?.branchId || a.branchId === f.branchId)
      && (!f?.jobId || a.jobId === f.jobId) && (!f?.from || a.accrualDate >= f.from) && (!f?.to || a.accrualDate <= f.to))
      .sort((x, y) => y.accrualDate.localeCompare(x.accrualDate) || x.jobNumber.localeCompare(y.jobNumber) || x.createdAt.localeCompare(y.createdAt));

  const api: CommissionApi = {
    async commissionSettings() { h.need('COMM.VIEW'); return delay({ ...settings }); },
    async updateCommissionSettings(s) {
      h.need('COMM.RULES');
      if (s.version !== settings.version) throw problem(412, 'GEN-412', 'Someone else changed the commission settings. Reload and try again.');
      if (s.clawbackDays < 0 || s.clawbackDays > 365) throw problem(400, 'VAL-001', 'The rework window is 0 to 365 days (0: no clawback).');
      if (!(s.clawbackPercent > 0 && s.clawbackPercent <= 100)) throw problem(400, 'VAL-001', 'The clawback is above 0% and at most 100%.');
      settings = { ...s, version: settings.version + 1 };
      return delay(undefined);
    },
    async commissionRules() {
      h.need('COMM.VIEW');
      return delay(structuredClone(rules).sort((x, y) => Number(y.latest) - Number(x.latest) || x.rank - y.rank || x.nameEn.localeCompare(y.nameEn) || y.version - x.version));
    },
    async createCommissionRule(r) { h.need('COMM.RULES'); const row = writeRule(r, id(), 1); return delay({ id: row.id, ruleKey: row.ruleKey, version: row.version }); },
    async newCommissionRuleVersion(key, r) {
      h.need('COMM.RULES');
      const latest = rules.filter((x) => x.ruleKey === key).sort((x, y) => y.version - x.version)[0];
      if (!latest) throw problem(404, 'GEN-404', 'That rule was not found.');
      if (latest.status === 'active') {
        if (latest.effectiveFrom >= r.effectiveFrom) latest.status = 'inactive';
        else if (!latest.effectiveTo || latest.effectiveTo >= r.effectiveFrom)
          latest.effectiveTo = new Date(Date.parse(r.effectiveFrom) - 864e5).toISOString().slice(0, 10);
      }
      const row = writeRule(r, key, latest.version + 1);
      return delay({ id: row.id, ruleKey: key, version: row.version });
    },
    async endCommissionRule(key, effectiveTo) {
      h.need('COMM.RULES');
      const latest = rules.filter((x) => x.ruleKey === key).sort((x, y) => y.version - x.version)[0];
      if (!latest) throw problem(404, 'GEN-404', 'That rule was not found.');
      if (effectiveTo < latest.effectiveFrom) latest.status = 'inactive'; else latest.effectiveTo = effectiveTo;
      return delay(undefined);
    },
    async commissions(f) { h.need('COMM.VIEW'); return delay(structuredClone(filterRows(f))); },
    async myCommissions(f) { const u = h.need('COMM.OWN'); return delay(structuredClone(filterRows({ ...f, technicianId: u.id }))); },
    async approveCommissions(ids) {
      const u = h.need('COMM.APPROVE');
      // The mock has one branch and no branch-scoped roles: head office stands for whoever may also set the rules.
      if (settings.approvalBy === 'head_office' && !h.can('COMM.RULES')) throw problem(403, 'AUTH-403', 'Commissions are approved at head office.');
      let n = 0;
      for (const aid of [...new Set(ids)]) {
        const a = accruals.find((x) => x.id === aid);
        if (!a) throw problem(404, 'GEN-404', 'A commission was not found in your branches.');
        if (a.status !== 'accrued') throw problem(409, 'COM-002', `Only an accrued commission is approved; one is ${a.status}.`);
        Object.assign(a, { status: 'approved' as AccrualStatus, approvedBy: u.displayNameEn, approvedAt: now() });
        n++;
      }
      return delay({ approved: n });
    },
    async reverseCommission(aid, reason) {
      const u = h.need('COMM.APPROVE');
      const a = accruals.find((x) => x.id === aid);
      if (!a) throw problem(404, 'GEN-404', 'That commission was not found in your branches.');
      return delay({ reversalId: reverse(a, reason, u).id });
    },
    async generateCommissions(jobId) {
      const u = h.need('COMM.APPROVE');
      return delay({ written: h.jobs().filter((j) => !jobId || j.id === jobId).reduce((n, j) => n + generate(j, u), 0) });
    },
    async commissionsPayable(f) {
      h.need('COMM.VIEW');
      const groups = new Map<string, CommissionAccrual[]>();
      for (const a of accruals.filter((x) => x.status === 'approved' && (!f?.branchId || x.branchId === f.branchId) && (!f?.technicianId || x.technicianId === f.technicianId)))
        groups.set(`${a.branchId}|${a.technicianId}`, [...(groups.get(`${a.branchId}|${a.technicianId}`) ?? []), a]);
      return delay([...groups.values()].map((xs) => ({ branchId: xs[0]!.branchId, branchCode: xs[0]!.branchCode, technicianId: xs[0]!.technicianId, technicianName: xs[0]!.technicianName,
        count: xs.length, total: toNumber(xs.reduce((s, a) => s + b(a.amount), 0n)), oldest: xs.map((a) => a.accrualDate).sort()[0]! })));
    },
    async payCommissions(p) {
      const u = h.need('COMM.PAY');
      if (!['cash', 'bank', 'payroll'].includes(p.method)) throw problem(400, 'VAL-001', 'A payout is in cash, to the bank, or through payroll.');
      if (p.accrualIds.length === 0) throw problem(400, 'VAL-001', 'Choose the commissions to pay.');
      const rows = [...new Set(p.accrualIds)].map((aid) => {
        const a = accruals.find((x) => x.id === aid);
        if (!a) throw problem(404, 'GEN-404', 'A commission was not found in your branches.');
        if (a.status !== 'approved') throw problem(409, 'COM-002', `Only approved commissions are paid; one is ${a.status}.`);
        if (a.technicianId !== p.technicianId || a.branchId !== p.branchId) throw problem(400, 'COM-004', 'A payout is for one technician’s commissions at one branch.');
        return a;
      });
      const total = rows.reduce((s, a) => s + b(a.amount), 0n);
      if (total <= 0n) throw problem(400, 'COM-004', 'These commissions come to zero or less; a payout is above zero (deductions wait for the next one).');
      const number = h.next('comm', 'COM');
      const pid = id();
      payouts.push({ id: pid, number, branchId: p.branchId, branchCode: rows[0]!.branchCode, technicianId: p.technicianId, technicianName: rows[0]!.technicianName, method: p.method,
        total: toNumber(total), reference: p.reference ?? null, note: p.note ?? null, payoutDate: today(), createdBy: u.displayNameEn, lines: rows.length, accrualIds: rows.map((a) => a.id) });
      rows.forEach((a) => Object.assign(a, { status: 'paid' as AccrualStatus, payoutNumber: number }));
      return delay({ id: pid, number, total: toNumber(total) });
    },
    async commissionPayouts(technicianId) {
      h.need('COMM.VIEW');
      return delay(structuredClone(payouts.filter((x) => !technicianId || x.technicianId === technicianId)).map(({ accrualIds: _a, ...x }) => x).reverse());
    },
    async commissionStatement(technicianId, from, to) {
      h.need('COMM.VIEW');
      const sumA = (pred: (a: CommissionAccrual) => boolean) => accruals.filter((a) => a.technicianId === technicianId && pred(a)).reduce((s, a) => s + b(a.amount), 0n);
      const sumP = (pred: (p: CommissionPayout) => boolean) => payouts.filter((p) => p.technicianId === technicianId && pred(p)).reduce((s, p) => s + b(p.total), 0n);
      return delay({
        technician: userName(technicianId) ?? '', from, to,
        opening: toNumber(sumA((a) => a.accrualDate < from) - sumP((p) => p.payoutDate < from)),
        accruals: structuredClone(accruals.filter((a) => a.technicianId === technicianId && a.accrualDate >= from && a.accrualDate <= to)),
        payouts: payouts.filter((p) => p.technicianId === technicianId && p.payoutDate >= from && p.payoutDate <= to)
          .map((p) => ({ id: p.id, number: p.number, method: p.method, total: p.total, payoutDate: p.payoutDate, branchCode: p.branchCode })),
        closing: toNumber(sumA((a) => a.accrualDate <= to) - sumP((p) => p.payoutDate <= to)),
      });
    },
    async technicianPerformance(from, to) {
      h.need('COMM.VIEW');
      const jobs = h.jobs().filter((j) => j.status === 'delivered' && j.outcome === 'repaired' && j.technicianId && j.deliveredAt && j.deliveredAt.slice(0, 10) >= from && j.deliveredAt.slice(0, 10) <= to);
      const techs = [...new Set(jobs.map((j) => j.technicianId!))];
      return delay(techs.map((t) => {
        const mine = jobs.filter((j) => j.technicianId === t);
        const hours = mine.map((j) => (Date.parse(j.deliveredAt!) - Date.parse(j.createdAt)) / 36e5);
        return {
          technicianId: t, technicianName: userName(t) ?? '', jobs: mine.filter((j) => !j.warranty).length,
          revenue: Number(mine.reduce((s, j) => s + j.basis.labour + j.basis.partsRevenue, 0).toFixed(3)),
          avgTurnaroundHours: hours.length ? Math.round((hours.reduce((s, x) => s + x, 0) / hours.length) * 10) / 10 : null,
          rework: h.jobs().filter((c) => c.warrantyOfId && h.jobs().find((o) => o.id === c.warrantyOfId)?.technicianId === t).length,
          commission: toNumber(accruals.filter((a) => a.technicianId === t && a.accrualDate >= from && a.accrualDate <= to).reduce((s, a) => s + b(a.amount), 0n)),
        };
      }));
    },
    async commissionRuleUsage(from, to) {
      h.need('COMM.VIEW');
      return delay(rules.filter((r) => r.used > 0).map((r) => {
        const mine = accruals.filter((a) => a.ruleKey === r.ruleKey && a.ruleVersion === r.version && a.kind === 'repair' && (!from || a.accrualDate >= from) && (!to || a.accrualDate <= to));
        return { ruleKey: r.ruleKey, version: r.version, nameEn: r.nameEn, nameAr: r.nameAr, basis: r.basis, rate: r.rate, amount: r.amount, accruals: mine.length,
          total: toNumber(mine.reduce((s, a) => s + b(a.amount), 0n)), first: mine.map((a) => a.accrualDate).sort()[0] ?? '', last: mine.map((a) => a.accrualDate).sort().at(-1) ?? '' };
      }).filter((r) => r.accruals > 0));
    },
  };

  // A rule for everyone and one for screens, for design review.
  const seed = () => {
    writeRule({ nameEn: 'Workshop: 10% of labour', nameAr: 'الورشة: 10% من الأجور', basis: 'labour', rate: 10, effectiveFrom: '2026-01-01' }, id(), 1);
  };
  return { api, generate, reverseInvoice, clawback, seed, settings: () => settings };
}
