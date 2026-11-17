// In-memory Phase 10 customer credit and suppliers for tests, `npm run dev:mock` and design review: accounts with limits,
// terms and branch allowances; sales on account with a manager's approval up to the override cap; instalments; payments
// settling the oldest instalment first (with transfers when another branch's invoice is paid); holds, write-offs, statements,
// ageing and the reports; suppliers with provisional ones approved or merged. The arithmetic is @kite/pos (the same vectors as
// the server); the real server and database check everything again and are the source of truth.
import {
  ApiError, type Branch, type CreditAccount, type CreditAccountRow, type CreditApi, type CreditDetail, type CreditPolicy, type CustomerStatement, type Customer,
  type Supplier, type SupplierRow, type User,
} from '@kite/api';
import { aging, allocateOldestFirst, availableOnline, decideCredit, instalments, toBaisa, toNumber } from '@kite/pos';

const problem = (status: number, code: string, title: string) => new ApiError({ status, code, title });
const uid = () => crypto.randomUUID();
const now = () => new Date().toISOString();
const delay = <T>(v: T): Promise<T> => new Promise((r) => setTimeout(() => r(v), 15));
const today = () => new Intl.DateTimeFormat('sv-SE', { timeZone: 'Asia/Muscat' }).format(new Date());
const addDays = (d: string, n: number) => new Date(Date.parse(`${d}T00:00:00Z`) + n * 864e5).toISOString().slice(0, 10);
const days = (from: string, to: string) => Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 864e5);
const r3 = (x: number) => Math.round(x * 1000) / 1000;

interface Entry { id: string; branchId: string; customerId: string; kind: string; amount: number; number: string; docDate: string; note: string | null; createdAt: string; saleId: string | null }
interface Item { id: string; entryId: string; branchId: string; customerId: string; seq: number; of: number; dueDate: string; amount: number }
interface Alloc { itemId: string; creditId: string; amount: number }
interface Account extends Omit<CreditAccount, 'customerNumber' | 'customerName' | 'customerNameAr'> { allowances: Map<string, number> }
interface SupplierLine { id: string; branchId: string; supplierId: string; kind: string; amount: number; docDate: string; dueDate: string | null; number: string; note: string | null }

export interface CreditMockHooks {
  need: (perm?: string) => User;
  can: (perm: string) => boolean;
  next: (kind: string, prefix: string) => string;
  customers: () => Customer[];
  branches: () => Branch[];
  branchId: string;
  useApproval: (id: string, action: 'CREDIT.OVERRIDE', requester: string) => User | undefined;
  /** A payment taken at the till goes on the drawer. */
  drawer: (method: string, amount: number) => void;
}

export interface CreditSaleCheck { customerId: string; amount: number; overBy: number; approvedBy: string | null; plan: { instalments: number; intervalDays?: number | null } | null }

export function createMockCredit(h: CreditMockHooks) {
  const policy: CreditPolicy = { defaultTermsDays: 30, blockOverdueDays: 30, holdOverdueDays: 60, offlineSaleCap: 100, overrideCap: 50, allowOvercommit: false,
    onlineTimeoutMs: 2000, authorisationMinutes: 15, maxInstalments: 12, remindBeforeDays: 3, remindOverdueEvery: 7, version: 1 };
  const accounts = new Map<string, Account>();
  const entries: Entry[] = [];
  const items: Item[] = [];
  const allocs: Alloc[] = [];
  const holdsOf = new Map<string, CreditDetail['holds']>();
  const decisions: { id: string; customerId: string; amount: number; overBy: number; approvedBy: string | null; number: string; branchId: string; createdAt: string; available: number; limit: number; exposure: number }[] = [];
  const suppliers: Supplier[] = [];
  const supplierLines: SupplierLine[] = [];
  const branchCode = (bid: string) => h.branches().find((b) => b.id === bid)?.code ?? 'B01';
  const customer = (cid: string) => h.customers().find((c) => c.id === cid);

  const balance = (cid: string) => r3(entries.filter((e) => e.customerId === cid).reduce((a, e) => a + e.amount, 0));
  const openOf = (it: Item) => r3(it.amount - allocs.filter((a) => a.itemId === it.id).reduce((s, a) => s + a.amount, 0));
  const leftOf = (e: Entry) => r3(-e.amount - allocs.filter((a) => a.creditId === e.id).reduce((s, a) => s + a.amount, 0));
  const credits = (cid: string) => entries.filter((e) => e.customerId === cid && e.amount < 0 && e.kind !== 'transfer');
  const oldestDue = (cid: string) => items.filter((i) => i.customerId === cid && openOf(i) > 0).map((i) => i.dueDate).sort()[0] ?? null;
  const overdueDays = (cid: string) => { const o = oldestDue(cid); return o && o < today() ? days(o, today()) : 0; };
  const acc = (cid: string) => accounts.get(cid) ?? (() => { throw problem(404, 'GEN-404', 'That customer has no credit account.'); })();

  const view = (a: Account): CreditAccount => {
    const c = customer(a.customerId);
    return { ...a, exposure: balance(a.customerId), oldestDue: oldestDue(a.customerId), customerNumber: c?.number ?? '', customerName: c?.nameEn ?? '', customerNameAr: c?.nameAr ?? null } as CreditAccount & { allowances?: unknown };
  };

  /** Oldest due first; another branch's invoice paid here moves the receivable by two transfer rows. */
  const allocate = (cid: string, preferSale: string | null = null, manual: { creditId: string; itemId: string; amount: number }[] = []) => {
    for (const m of manual) allocs.push({ itemId: m.itemId, creditId: m.creditId, amount: m.amount });
    for (const c of credits(cid).sort((a, b) => (a.docDate + a.createdAt).localeCompare(b.docDate + b.createdAt))) {
      let left = toBaisa(leftOf(c));
      if (left <= 0n) continue;
      const open = items.filter((i) => i.customerId === cid && openOf(i) > 0).sort((a, b) => a.dueDate.localeCompare(b.dueDate));
      const preferred = preferSale ? open.filter((i) => entries.find((e) => e.id === i.entryId)?.saleId === preferSale) : [];
      for (const group of [preferred, open.filter((i) => !preferred.includes(i))]) {
        const r = allocateOldestFirst(group.map((i) => ({ id: i.id, due: i.dueDate, open: toBaisa(openOf(i)) })), left);
        for (const x of r.allocations) {
          const it = items.find((i) => i.id === x.id)!;
          const amount = toNumber(x.amount);
          allocs.push({ itemId: it.id, creditId: c.id, amount });
          if (it.branchId !== c.branchId) {
            entries.push({ id: uid(), branchId: c.branchId, customerId: cid, kind: 'transfer', amount, number: c.number, docDate: today(), note: "Payment here settled another branch's invoice", createdAt: now(), saleId: null });
            entries.push({ id: uid(), branchId: it.branchId, customerId: cid, kind: 'transfer', amount: -amount, number: c.number, docDate: today(), note: 'Invoice settled by a payment at another branch', createdAt: now(), saleId: null });
          }
        }
        left = r.left;
      }
    }
  };

  const hold = (a: Account, kind: 'manual' | 'overdue' | 'exposure', reason: string, by: User) => {
    a.status = 'hold'; a.holdKind = kind; a.holdReason = reason; a.heldAt = now(); a.version++;
    (holdsOf.get(a.customerId) ?? holdsOf.set(a.customerId, []).get(a.customerId)!).unshift({ action: 'hold', kind, reason, exposure: balance(a.customerId), creditLimit: a.creditLimit, createdAt: now(), byName: by.displayNameEn, byNameAr: by.displayNameAr });
  };
  const release = (a: Account, reason: string, by: User) => {
    (holdsOf.get(a.customerId) ?? holdsOf.set(a.customerId, []).get(a.customerId)!).unshift({ action: 'release', kind: a.holdKind ?? 'manual', reason, exposure: balance(a.customerId), creditLimit: a.creditLimit, createdAt: now(), byName: by.displayNameEn, byNameAr: by.displayNameAr });
    a.status = 'active'; a.holdKind = null; a.holdReason = null; a.heldAt = null; a.version++;
  };

  // ---------------------------------------------------------------- seed (design review)
  const seed = () => {
    const cs = h.customers();
    const gulf = cs.find((c) => c.number === 'C-000001');
    if (!gulf || accounts.size > 0) return;
    const b2 = h.branches()[1]?.id ?? h.branchId;
    accounts.set(gulf.id, { id: uid(), customerId: gulf.id, creditLimit: 500, termsDays: 30, status: 'active', holdKind: null, holdReason: null, heldAt: null, offlineSaleCap: null,
      note: 'Corporate account', exposure: 0, oldestDue: null, exposureAsOf: null, version: 1, allowances: new Map([[h.branchId, 300], [b2, 200]]) });
    const e1 = { id: uid(), branchId: h.branchId, customerId: gulf.id, kind: 'charge', amount: 180, number: 'INV-B01-2026-000101', docDate: addDays(today(), -45), note: 'Sale INV-B01-2026-000101', createdAt: now(), saleId: uid() };
    const e2 = { id: uid(), branchId: b2, customerId: gulf.id, kind: 'charge', amount: 120, number: 'INV-B02-2026-000044', docDate: addDays(today(), -10), note: 'Sale INV-B02-2026-000044', createdAt: now(), saleId: uid() };
    entries.push(e1, e2);
    items.push({ id: uid(), entryId: e1.id, branchId: e1.branchId, customerId: gulf.id, seq: 1, of: 1, dueDate: addDays(e1.docDate, 30), amount: 180 });
    items.push({ id: uid(), entryId: e2.id, branchId: e2.branchId, customerId: gulf.id, seq: 1, of: 1, dueDate: addDays(e2.docDate, 30), amount: 120 });
    const p = { id: uid(), branchId: h.branchId, customerId: gulf.id, kind: 'payment', amount: -50, number: 'RC-B01-2026-000012', docDate: addDays(today(), -5), note: 'Payment RC-B01-2026-000012 (cash)', createdAt: now(), saleId: null };
    entries.push(p);
    allocate(gulf.id);
    suppliers.push({ id: uid(), number: 'S-00001', nameEn: 'Gulf Mobile Parts LLC', nameAr: 'شركة الخليج لقطع الهواتف', vatNumber: 'OM1100445566', crNumber: '1199887', phone: '+96824556677', email: null, address: 'Ruwi, Muscat', contactName: 'Anil', termsDays: 45, status: 'active', mergedIntoId: null, originBranchId: h.branchId, originServerId: null, version: 1 });
    suppliers.push({ id: uid(), number: 'SP-B02-2026-000001', nameEn: 'Sohar Screens Trading', nameAr: 'صحار للشاشات', vatNumber: null, crNumber: null, phone: null, email: null, address: null, contactName: null, termsDays: 30, status: 'provisional', mergedIntoId: null, originBranchId: b2, originServerId: null, version: 1 });
    supplierLines.push({ id: uid(), branchId: h.branchId, supplierId: suppliers[0]!.id, kind: 'opening', amount: 640, docDate: addDays(today(), -20), dueDate: addDays(today(), -20), number: 'OB-B01-2026-000001', note: null });
  };

  // ---------------------------------------------------------------- the till's side
  /** The decision on a sale on account (14.3): checked before the sale is written. */
  const checkSale = (cid: string | null | undefined, amount: number, approvalId: string | null | undefined, plan: CreditSaleCheck['plan'], u: User): CreditSaleCheck => {
    seed();
    if (!cid) throw problem(400, 'POS-016', 'A sale on account is charged to a customer: choose the customer.');
    if (!h.can('POS.CREDIT_SALE')) throw problem(403, 'AUTH-403', 'You do not have permission POS.CREDIT_SALE.');
    const a = accounts.get(cid);
    if (!a) throw problem(409, 'CRD-001', 'This customer has no credit account. Head office opens one with a limit.');
    if (plan && (plan.instalments < 1 || plan.instalments > policy.maxInstalments)) throw problem(400, 'VAL-001', `A credit sale is paid in 1 to ${policy.maxInstalments} instalments.`);
    const available = toNumber(availableOnline(toBaisa(a.creditLimit), toBaisa(balance(cid)), 0n));
    const self = h.can('CREDIT.OVERRIDE');
    const d = decideCredit(toBaisa(amount), toBaisa(available), toBaisa(policy.overrideCap), overdueDays(cid), policy.blockOverdueDays, a.status, !!approvalId || self);
    const over = toNumber(d.overBy);
    if (d.outcome === 'hold') throw problem(409, 'CRD-002', `The customer's credit account is on hold: ${a.holdReason}. No new credit until head office releases it.`);
    if (d.outcome === 'closed') throw problem(409, 'CRD-002', "The customer's credit account is closed.");
    if (d.outcome === 'over_cap') throw problem(409, 'CRD-003', `Available credit is ${available.toFixed(3)}. This is ${over.toFixed(3)} over, more than a manager may approve (${policy.overrideCap.toFixed(3)}).`);
    if (d.outcome === 'needs_approval') {
      const why = [over > 0 ? `it is ${over.toFixed(3)} over the available credit of ${available.toFixed(3)}` : null, overdueDays(cid) > 0 ? `an invoice is ${overdueDays(cid)} days overdue` : null].filter(Boolean);
      throw problem(403, 'CRD-004', `This credit sale needs a manager's approval: ${why.join(' and ')}.`);
    }
    let approvedBy: string | null = null;
    if (d.outcome === 'approved') approvedBy = approvalId ? (h.useApproval(approvalId, 'CREDIT.OVERRIDE', u.id)?.displayNameEn ?? u.displayNameEn) : u.displayNameEn;
    return { customerId: cid, amount, overBy: over, approvedBy, plan };
  };

  const charge = (c: CreditSaleCheck, saleId: string, number: string, branchId: string) => {
    const a = acc(c.customerId);
    const e: Entry = { id: uid(), branchId, customerId: c.customerId, kind: 'charge', amount: c.amount, number, docDate: today(), note: `Sale ${number}`, createdAt: now(), saleId };
    const before = balance(c.customerId);
    entries.push(e);
    const plan = instalments(toBaisa(c.amount), c.plan?.instalments ?? 1, today(), a.termsDays, c.plan?.intervalDays ?? 30);
    for (const i of plan) items.push({ id: uid(), entryId: e.id, branchId, customerId: c.customerId, seq: i.seq, of: plan.length, dueDate: i.due, amount: toNumber(i.amount) });
    decisions.push({ id: uid(), customerId: c.customerId, amount: c.amount, overBy: c.overBy, approvedBy: c.approvedBy, number, branchId, createdAt: now(), available: r3(a.creditLimit - before), limit: a.creditLimit, exposure: before });
    allocate(c.customerId);
  };

  const refunded = new Map<string, number>();
  const creditNote = (cid: string, amount: number, saleId: string, originalSaleId: string, number: string, branchId: string) => {
    refunded.set(originalSaleId, r3((refunded.get(originalSaleId) ?? 0) + amount));
    entries.push({ id: uid(), branchId, customerId: cid, kind: 'credit_note', amount: -amount, number, docDate: today(), note: `Credit note ${number}`, createdAt: now(), saleId });
    allocate(cid, originalSaleId);
  };

  const detail = (cid: string): CreditDetail => {
    const a = acc(cid);
    const open = items.filter((i) => i.customerId === cid && openOf(i) > 0).sort((x, y) => x.dueDate.localeCompare(y.dueDate));
    const unalloc = credits(cid).filter((e) => leftOf(e) > 0);
    const exposure = balance(cid);
    return {
      account: view(a), policy,
      figures: { exposure, balance: exposure, openItems: r3(open.reduce((s, i) => s + openOf(i), 0)), onAccount: r3(unalloc.reduce((s, e) => s + leftOf(e), 0)), oldestDue: oldestDue(cid),
        overdueDays: overdueDays(cid), available: Math.max(0, r3(a.creditLimit - exposure)), blocked: overdueDays(cid) > policy.blockOverdueDays, headOffice: true, asOf: null },
      allowances: h.branches().filter((b) => b.status === 'active').map((b) => ({ branchId: b.id, code: b.code, nameEn: b.nameEn, nameAr: b.nameAr, amount: a.allowances.get(b.id) ?? 0 })),
      openItems: open.map((i) => ({ id: i.id, number: entries.find((e) => e.id === i.entryId)!.number, branchCode: branchCode(i.branchId), seq: i.seq, of: i.of, dueDate: i.dueDate, amount: i.amount, open: openOf(i), overdueDays: Math.max(0, days(i.dueDate, today())) })),
      credits: unalloc.map((e) => ({ id: e.id, number: e.number, kind: e.kind, branchCode: branchCode(e.branchId), docDate: e.docDate, amount: -e.amount, leftOver: leftOf(e) })),
      ledger: entries.filter((e) => e.customerId === cid).slice().reverse().map((e) => ({ id: e.id, docDate: e.docDate, number: e.number, kind: e.kind, branchCode: branchCode(e.branchId), amount: e.amount, note: e.note, createdAt: e.createdAt })),
      holds: holdsOf.get(cid) ?? [], breaches: [],
    };
  };

  const statement = (cid: string, f?: { from?: string; to?: string; branchId?: string | null }): CustomerStatement => {
    const c = customer(cid)!;
    const to = f?.to ?? today();
    const from = f?.from ?? `${to.slice(0, 8)}01`;
    const rows = entries.filter((e) => e.customerId === cid && (f?.branchId ? e.branchId === f.branchId : e.kind !== 'transfer'));
    const opening = r3(rows.filter((e) => e.docDate < from).reduce((s, e) => s + e.amount, 0));
    let bal = opening;
    const lines = rows.filter((e) => e.docDate >= from && e.docDate <= to).sort((a, b) => a.docDate.localeCompare(b.docDate))
      .map((e) => { bal = r3(bal + e.amount); return { date: e.docDate, number: e.number, kind: e.kind, branchCode: branchCode(e.branchId), note: e.note, debit: e.amount > 0 ? e.amount : 0, credit: e.amount < 0 ? -e.amount : 0, balance: bal }; });
    const ag = aging(items.filter((i) => i.customerId === cid && openOf(i) > 0).map((i) => ({ due: i.dueDate, open: toBaisa(openOf(i)) })), toBaisa(credits(cid).reduce((s, e) => s + Math.max(0, leftOf(e)), 0)), today());
    return { customer: { id: c.id, number: c.number, nameEn: c.nameEn, nameAr: c.nameAr, phone: c.phoneE164, vatNumber: c.vatNumber }, from, to, branchId: f?.branchId ?? null,
      branchCode: f?.branchId ? branchCode(f.branchId) : null, opening, lines, debits: r3(lines.reduce((s, l) => s + l.debit, 0)), credits: r3(lines.reduce((s, l) => s + l.credit, 0)), closing: bal,
      aging: f?.branchId ? null : { current: toNumber(ag.current), d1To30: toNumber(ag.d1_30), d31To60: toNumber(ag.d31_60), d61To90: toNumber(ag.d61_90), d90Plus: toNumber(ag.d90plus), onAccount: toNumber(ag.onAccount), total: toNumber(ag.total) },
      creditLimit: accounts.get(cid)?.creditLimit ?? null, generatedAt: now(), headOffice: true };
  };

  const supplierBalance = (sid: string) => r3(supplierLines.filter((l) => l.supplierId === sid || suppliers.find((s) => s.id === l.supplierId)?.mergedIntoId === sid).reduce((s, l) => s + l.amount, 0));

  const api: CreditApi = {
    async creditPolicy() { h.need('CREDIT.VIEW'); return delay({ ...policy }); },
    async updateCreditPolicy(p) {
      h.need('CREDIT.MANAGE');
      if (p.version !== policy.version) throw problem(412, 'GEN-412', 'Someone else changed the credit policy. Reload and try again.');
      Object.assign(policy, p, { version: policy.version + 1 });
    },
    async creditAccounts(f) {
      h.need('CREDIT.VIEW'); seed();
      const k = f?.q?.toLowerCase();
      const rows: CreditAccountRow[] = [...accounts.values()].map((a) => {
        const c = customer(a.customerId)!;
        const exposure = balance(a.customerId);
        return { id: a.id, customerId: a.customerId, customerNumber: c.number, nameEn: c.nameEn, nameAr: c.nameAr, phone: c.phoneE164, creditLimit: a.creditLimit, termsDays: a.termsDays,
          status: a.status, holdKind: a.holdKind, holdReason: a.holdReason, exposure, oldestDue: oldestDue(a.customerId), asOf: null, version: a.version,
          utilisation: a.creditLimit > 0 ? Math.round(exposure / a.creditLimit * 1000) / 10 : null, available: Math.max(0, r3(a.creditLimit - exposure)), overdueDays: overdueDays(a.customerId) };
      }).filter((r) => (!k || r.nameEn.toLowerCase().includes(k) || r.customerNumber.toLowerCase().includes(k)) && (!f?.status || r.status === f.status));
      return delay(rows.sort((a, b) => a.nameEn.localeCompare(b.nameEn)));
    },
    async creditAccount(cid) { h.need('CREDIT.VIEW'); seed(); return delay(detail(cid)); },
    async openCreditAccount(x) {
      h.need('CREDIT.MANAGE');
      if (accounts.has(x.customerId)) throw problem(409, 'GEN-409', 'The customer already has a credit account.');
      const sum = (x.allowances ?? []).reduce((s, a) => s + a.amount, 0);
      if (!policy.allowOvercommit && sum > x.creditLimit) throw problem(409, 'CRD-006', `Branch allowances add up to ${sum.toFixed(3)}, more than the limit of ${x.creditLimit.toFixed(3)}.`);
      const id = uid();
      accounts.set(x.customerId, { id, customerId: x.customerId, creditLimit: x.creditLimit, termsDays: x.termsDays ?? policy.defaultTermsDays, status: 'active', holdKind: null, holdReason: null, heldAt: null,
        offlineSaleCap: x.offlineSaleCap ?? null, note: x.note ?? null, exposure: 0, oldestDue: null, exposureAsOf: null, version: 1, allowances: new Map((x.allowances ?? []).map((a) => [a.branchId, a.amount])) });
      return delay({ id });
    },
    async updateCreditAccount(cid, x) {
      h.need('CREDIT.MANAGE');
      const a = acc(cid);
      if (x.version !== a.version) throw problem(412, 'GEN-412', 'Someone else changed this account. Reload and try again.');
      const sum = (x.allowances ?? []).reduce((s, v) => s + v.amount, 0);
      if (!policy.allowOvercommit && sum > x.creditLimit) throw problem(409, 'CRD-006', `Branch allowances add up to ${sum.toFixed(3)}, more than the limit of ${x.creditLimit.toFixed(3)}.`);
      a.creditLimit = x.creditLimit; a.termsDays = x.termsDays ?? a.termsDays; a.offlineSaleCap = x.offlineSaleCap ?? null; a.note = x.note ?? null;
      if (x.status && a.status !== 'hold') a.status = x.status;
      if (x.allowances) a.allowances = new Map(x.allowances.map((v) => [v.branchId, v.amount]));
      a.version++;
    },
    async holdCredit(cid, reason) { const u = h.need('CREDIT.MANAGE'); const a = acc(cid); if (a.status !== 'active') throw problem(409, 'CRD-002', `The account is ${a.status}.`); hold(a, 'manual', reason, u); },
    async releaseCredit(cid, reason) { const u = h.need('CREDIT.MANAGE'); const a = acc(cid); if (a.status !== 'hold') throw problem(409, 'CRD-002', 'The account is not on hold.'); release(a, reason, u); },
    async creditAvailable(cid) {
      h.need(); seed();
      const a = accounts.get(cid);
      if (!a) return delay({ hasAccount: false });
      const exposure = balance(cid);
      return delay({ hasAccount: true, status: a.status, holdReason: a.holdReason, limit: a.creditLimit, exposure, available: Math.max(0, r3(a.creditLimit - exposure)),
        overdueDays: overdueDays(cid), blocked: overdueDays(cid) > policy.blockOverdueDays, blockOverdueDays: policy.blockOverdueDays, overrideCap: policy.overrideCap,
        termsDays: a.termsDays, maxInstalments: policy.maxInstalments, offlineSaleCap: a.offlineSaleCap ?? policy.offlineSaleCap, mode: 'head_office', asOf: null });
    },
    async receiveCustomerPayment(p) {
      h.need('CREDIT.RECEIVE'); seed();
      const a = acc(p.customerId);
      if (!(p.amount > 0)) throw problem(400, 'VAL-001', 'The amount is above zero, with at most 3 decimals.');
      if (p.method === 'card' && (!p.cardTerminal || !p.approvalCode)) throw problem(400, 'VAL-001', 'A card payment needs the bank terminal and the approval code from its slip.');
      if (p.method === 'bank_transfer' && !p.reference) throw problem(400, 'VAL-001', 'A bank transfer needs its reference.');
      const branchId = p.branchId ?? h.branchId;
      const number = h.next('rc', 'RC');
      const e: Entry = { id: uid(), branchId, customerId: p.customerId, kind: 'payment', amount: -p.amount, number, docDate: today(), note: `Payment ${number} (${p.method.replace('_', ' ')})`, createdAt: now(), saleId: null };
      entries.push(e);
      if (!p.branchId) h.drawer(p.method, p.amount);
      allocate(p.customerId, null, (p.allocations ?? []).map((x) => ({ creditId: e.id, itemId: x.itemId, amount: x.amount })));
      if (a.status === 'hold' && a.holdKind !== 'manual' && balance(p.customerId) <= a.creditLimit && overdueDays(p.customerId) <= policy.holdOverdueDays) release(a, 'Back within the limit', h.need());
      return delay({ id: e.id, number, method: p.method, amount: p.amount, customerId: p.customerId, branchCode: branchCode(branchId), receivedAt: now(), balance: balance(p.customerId),
        allocated: r3(allocs.filter((x) => x.creditId === e.id).reduce((s, x) => s + x.amount, 0)) });
    },
    async allocateCredit(cid, creditId, list) {
      h.need('CREDIT.MANAGE');
      allocate(cid, null, list.map((x) => ({ creditId, itemId: x.itemId, amount: x.amount })));
      return delay({ allocations: list.length });
    },
    async writeOffCredit(cid, w) {
      h.need('CREDIT.WRITE_OFF');
      if (!w.reason?.trim()) throw problem(400, 'VAL-001', 'Give the reason for the write-off.');
      const open = items.filter((i) => i.customerId === cid && openOf(i) > 0 && (!w.itemIds?.length || w.itemIds.includes(i.id)));
      const total = r3(open.reduce((s, i) => s + openOf(i), 0));
      if (total <= 0) throw problem(409, 'CRD-005', 'Nothing is open to write off.');
      const number = h.next('wo', 'WO');
      for (const i of open) {
        const e: Entry = { id: uid(), branchId: i.branchId, customerId: cid, kind: 'write_off', amount: -openOf(i), number, docDate: today(), note: `Written off: ${w.reason}`, createdAt: now(), saleId: null };
        entries.push(e);
        allocs.push({ itemId: i.id, creditId: e.id, amount: -e.amount });
      }
      return delay({ id: uid(), number, total });
    },
    async customerOpening(cid, o) {
      h.need('CREDIT.MANAGE');
      const a = acc(cid);
      const number = h.next('ob', 'OB');
      const e: Entry = { id: uid(), branchId: o.branchId, customerId: cid, kind: 'opening', amount: o.amount, number, docDate: o.date ?? today(), note: o.note ?? 'Opening balance', createdAt: now(), saleId: null };
      entries.push(e);
      if (o.amount > 0) items.push({ id: uid(), entryId: e.id, branchId: o.branchId, customerId: cid, seq: 1, of: 1, dueDate: addDays(e.docDate, a.termsDays), amount: o.amount });
      allocate(cid);
      return delay({ id: e.id, number });
    },
    async customerStatement(cid, f) { h.need('CREDIT.VIEW'); seed(); return delay(statement(cid, f)); },
    async customerStatementPdf() { h.need('CREDIT.VIEW'); return delay(new Blob(['%PDF-1.4 mock statement'], { type: 'application/pdf' })); },
    async creditAging() {
      h.need('CREDIT.VIEW'); seed();
      return delay([...accounts.values()].map((a) => {
        const c = customer(a.customerId)!;
        const ag = aging(items.filter((i) => i.customerId === a.customerId && openOf(i) > 0).map((i) => ({ due: i.dueDate, open: toBaisa(openOf(i)) })), toBaisa(credits(a.customerId).reduce((s, e) => s + Math.max(0, leftOf(e)), 0)), today());
        return { customerId: a.customerId, customerNumber: c.number, nameEn: c.nameEn, nameAr: c.nameAr, status: a.status, creditLimit: a.creditLimit, current: toNumber(ag.current),
          d1_30: toNumber(ag.d1_30), d31_60: toNumber(ag.d31_60), d61_90: toNumber(ag.d61_90), d90plus: toNumber(ag.d90plus), onAccount: toNumber(ag.onAccount), total: balance(a.customerId) };
      }));
    },
    async creditExposure() {
      h.need('CREDIT.VIEW'); seed();
      return delay(h.branches().filter((b) => b.status === 'active').map((b) => ({ branchId: b.id, code: b.code, nameEn: b.nameEn, nameAr: b.nameAr,
        balance: r3(entries.filter((e) => e.branchId === b.id).reduce((s, e) => s + e.amount, 0)),
        overdue: r3(items.filter((i) => i.branchId === b.id && i.dueDate < today()).reduce((s, i) => s + openOf(i), 0)),
        customers: new Set(entries.filter((e) => e.branchId === b.id).map((e) => e.customerId)).size,
        chargedThisMonth: r3(entries.filter((e) => e.branchId === b.id && e.kind === 'charge' && e.docDate >= `${today().slice(0, 8)}01`).reduce((s, e) => s + e.amount, 0)),
        receivedThisMonth: r3(-entries.filter((e) => e.branchId === b.id && e.kind === 'payment' && e.docDate >= `${today().slice(0, 8)}01`).reduce((s, e) => s + e.amount, 0)),
        allowances: r3([...accounts.values()].reduce((s, a) => s + (a.allowances.get(b.id) ?? 0), 0)) })));
    },
    async creditOverrides() {
      h.need('CREDIT.VIEW');
      return delay({ decisions: decisions.filter((d) => d.approvedBy).map((d) => {
        const c = customer(d.customerId)!;
        return { id: d.id, createdAt: d.createdAt, mode: 'local' as const, amount: d.amount, available: d.available, overBy: d.overBy, overdueDays: 0, creditLimit: d.limit, exposure: d.exposure, allowance: null,
          branchCode: branchCode(d.branchId), customerNumber: c.number, nameEn: c.nameEn, nameAr: c.nameAr, invoiceNumber: d.number, approvedBy: d.approvedBy, approvedByAr: null };
      }), breaches: [] });
    },
    async ledgerChecks() {
      h.need('CREDIT.VIEW'); seed();
      return delay(h.branches().map((b) => ({ ledger: 'customers' as const, branchId: b.id, branchCode: b.code, asOf: today(), subledger: r3(entries.filter((e) => e.branchId === b.id).reduce((s, e) => s + e.amount, 0)),
        control: r3(entries.filter((e) => e.branchId === b.id).reduce((s, e) => s + e.amount, 0)), difference: 0, checkedAt: now() })));
    },
    async runLedgerCheck() { h.need('CREDIT.VIEW'); return delay({ runId: uid(), asOf: today(), rows: [] }); },
    async suppliers(f) {
      h.need('SUPPLIER.VIEW'); seed();
      const k = f?.q?.toLowerCase();
      const rows: SupplierRow[] = suppliers.filter((s) => (!k || s.nameEn.toLowerCase().includes(k) || s.number.toLowerCase().includes(k)) && (!f?.status || s.status === f.status))
        .map((s) => ({ id: s.id, number: s.number, nameEn: s.nameEn, nameAr: s.nameAr, vatNumber: s.vatNumber, phone: s.phone, termsDays: s.termsDays, status: s.status, mergedIntoId: s.mergedIntoId,
          mergedIntoNumber: suppliers.find((x) => x.id === s.mergedIntoId)?.number ?? null, originBranchCode: s.originBranchId ? branchCode(s.originBranchId) : null, version: s.version,
          balance: supplierBalance(s.id), createdAt: now() }));
      return delay(rows.sort((a, b) => Number(b.status === 'provisional') - Number(a.status === 'provisional') || a.nameEn.localeCompare(b.nameEn)));
    },
    async supplier(sid) {
      h.need('SUPPLIER.VIEW'); seed();
      const s = suppliers.find((x) => x.id === sid) ?? (() => { throw problem(404, 'GEN-404', 'That supplier was not found.'); })();
      const lines = supplierLines.filter((l) => l.supplierId === sid || suppliers.find((x) => x.id === l.supplierId)?.mergedIntoId === sid);
      return delay({ supplier: s, headOffice: true, balance: supplierBalance(sid),
        byBranch: h.branches().map((b) => ({ branchId: b.id, code: b.code, nameEn: b.nameEn, nameAr: b.nameAr, balance: r3(lines.filter((l) => l.branchId === b.id).reduce((a, l) => a + l.amount, 0)) })).filter((x) => x.balance !== 0),
        merged: suppliers.filter((x) => x.mergedIntoId === sid).map((x) => ({ id: x.id, number: x.number, nameEn: x.nameEn, nameAr: x.nameAr })),
        ledger: lines.map((l) => ({ id: l.id, docDate: l.docDate, dueDate: l.dueDate, number: l.number, kind: l.kind, branchCode: branchCode(l.branchId), amount: l.amount, note: l.note })) });
    },
    async createSupplier(x) {
      h.need('SUPPLIER.MANAGE');
      if (!x.nameEn?.trim()) throw problem(400, 'VAL-001', 'Name in English is required.');
      if (!x.nameAr?.trim()) throw problem(400, 'VAL-001', 'Name in Arabic is required.');
      const s: Supplier = { id: uid(), number: h.next('supplier', 'S'), nameEn: x.nameEn.trim(), nameAr: x.nameAr.trim(), vatNumber: x.vatNumber ?? null, crNumber: x.crNumber ?? null,
        phone: x.phone ?? null, email: x.email ?? null, address: x.address ?? null, contactName: x.contactName ?? null, termsDays: x.termsDays ?? 30, status: 'active', mergedIntoId: null,
        originBranchId: h.branchId, originServerId: null, version: 1 };
      suppliers.push(s);
      return delay({ id: s.id, number: s.number, status: s.status });
    },
    async updateSupplier(sid, x) {
      h.need('SUPPLIER.MANAGE');
      const s = suppliers.find((v) => v.id === sid)!;
      if (x.version !== s.version) throw problem(412, 'GEN-412', 'Someone else changed this supplier. Reload and try again.');
      Object.assign(s, { nameEn: x.nameEn, nameAr: x.nameAr, vatNumber: x.vatNumber ?? null, crNumber: x.crNumber ?? null, phone: x.phone ?? null, email: x.email ?? null,
        address: x.address ?? null, contactName: x.contactName ?? null, termsDays: x.termsDays ?? s.termsDays, version: s.version + 1 });
    },
    async approveSupplier(sid) {
      h.need('SUPPLIER.APPROVE');
      const s = suppliers.find((v) => v.id === sid)!;
      if (s.status !== 'provisional') throw problem(409, 'SUP-001', `${s.number} is ${s.status}, not waiting for approval.`);
      s.status = 'active'; s.version++;
    },
    async mergeSupplier(sid, intoId) {
      h.need('SUPPLIER.APPROVE');
      const s = suppliers.find((v) => v.id === sid)!;
      const into = suppliers.find((v) => v.id === intoId);
      if (!into || into.status !== 'active' || into.id === sid) throw problem(409, 'SUP-001', 'Merge only into another active supplier.');
      s.status = 'merged'; s.mergedIntoId = intoId; s.version++;
    },
    async supplierOpening(sid, o) {
      h.need('SUPPLIER.MANAGE');
      const number = h.next('ob', 'OB');
      supplierLines.push({ id: uid(), branchId: o.branchId, supplierId: sid, kind: 'opening', amount: o.amount, docDate: o.date ?? today(), dueDate: o.date ?? today(), number, note: o.note ?? null });
      return delay({ id: uid(), number });
    },
    async supplierStatement(sid, f) {
      h.need('SUPPLIER.VIEW');
      const s = suppliers.find((v) => v.id === sid)!;
      const to = f?.to ?? today();
      const from = f?.from ?? `${to.slice(0, 8)}01`;
      const rows = supplierLines.filter((l) => (l.supplierId === sid || suppliers.find((x) => x.id === l.supplierId)?.mergedIntoId === sid) && (!f?.branchId || l.branchId === f.branchId));
      const opening = r3(rows.filter((l) => l.docDate < from).reduce((a, l) => a + l.amount, 0));
      let bal = opening;
      const lines = rows.filter((l) => l.docDate >= from && l.docDate <= to).map((l) => { bal = r3(bal + l.amount); return { date: l.docDate, number: l.number, kind: l.kind, branchCode: branchCode(l.branchId), note: l.note, debit: l.amount < 0 ? -l.amount : 0, credit: l.amount > 0 ? l.amount : 0, balance: bal }; });
      return delay({ supplier: s, from, to, branchId: f?.branchId ?? null, opening, lines, closing: bal });
    },
    async supplierAging() {
      h.need('SUPPLIER.VIEW'); seed();
      return delay(suppliers.filter((s) => s.status !== 'merged').map((s) => {
        const lines = supplierLines.filter((l) => l.supplierId === s.id || suppliers.find((x) => x.id === l.supplierId)?.mergedIntoId === s.id);
        const pos = lines.filter((l) => l.amount > 0).map((l) => ({ id: l.id, due: l.dueDate ?? l.docDate, open: toBaisa(l.amount) }));
        const paid = toBaisa(-lines.filter((l) => l.amount < 0).reduce((a, l) => a + l.amount, 0));
        const r = allocateOldestFirst(pos, paid);
        const ag = aging(pos.map((p) => ({ due: p.due, open: p.open - (r.allocations.find((x) => x.id === p.id)?.amount ?? 0n) })), r.left, today());
        return { supplierId: s.id, number: s.number, nameEn: s.nameEn, nameAr: s.nameAr, current: toNumber(ag.current), d1_30: toNumber(ag.d1_30), d31_60: toNumber(ag.d31_60),
          d61_90: toNumber(ag.d61_90), d90plus: toNumber(ag.d90plus), advance: toNumber(ag.onAccount), total: toNumber(ag.total) };
      }).filter((x) => x.total !== 0 || x.advance !== 0));
    },
  };
  /** What a sale charged on account, less what credit notes already took back. */
  const refundable = (saleId: string) => r3(entries.filter((e) => e.saleId === saleId && e.kind === 'charge').reduce((s, e) => s + e.amount, 0) - (refunded.get(saleId) ?? 0));
  return { api, checkSale, charge, creditNote, refundable, hasAccount: (cid: string) => { seed(); return accounts.has(cid); } };
}
