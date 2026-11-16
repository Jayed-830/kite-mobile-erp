// In-memory Phase 8 repairs and WhatsApp for tests, `npm run dev:mock` and design review: intake, the 12 statuses and only the
// blueprint's transitions, versioned estimates and their approval, parts and labour on the job, deposits in the drawer,
// collection with the repair invoice, messages and the WhatsApp test chat. Phase 9: parts held and waited for, serial parts,
// outside work, technician shares, commissions (mockCommissions.ts) and correcting a delivered invoice at the till. The arithmetic is @kite/pos (the same vectors as
// the server); the real server and database check everything again and are the source of truth.
import {
  ApiError, NEEDS_APPROVED_ESTIMATE, REPAIR_TRANSITIONS, type Customer, type DeliverInput, type EstimateLineInput, type FaultCode, type RepairApi, type RepairCategory,
  type RepairDetail, type RepairEstimate, type RepairEstimateLine, type RepairIntake, type RepairJob, type RepairMessage, type RepairSettings, type RepairStatus,
  type RepairWorkLine, type TestChat, type User, type WhatsAppTemplate, type RepairPartRow, type RepairCorrectionRow, type PartsNeededRow,
} from '@kite/api';
import { createMockCommissions, type CommissionJob } from './mockCommissions';
import { depositTax, fromBaisa, labourPrice, returnCost, sharesComplete, splitDelivery, taxShare, toBaisa, toNumber, type Tender } from '@kite/pos';

const problem = (status: number, code: string, title: string) => new ApiError({ status, code, title });
const id = () => crypto.randomUUID();
const now = () => new Date().toISOString();
const delay = <T>(v: T): Promise<T> => new Promise((r) => setTimeout(() => r(v), 15));
const n = (b: bigint) => toNumber(b);
const b = (x: number | string) => toBaisa(x);
const words = (s: string) => s.replace(/_/g, ' ');

export interface RepairMockHooks {
  need: (perm?: string) => User;
  next: (kind: string, prefix: string) => string;
  branchId: string;
  branchCode: string;
  users: () => User[];
  customers: () => Customer[];
  /** A catalogue item: its names, type and price (tax-inclusive). */
  item: (variantId: string) => { sku: string; descriptionEn: string; descriptionAr: string; productType: string; listPrice: number | null; tracking?: string } | null;
  /** Phase 9: what is on hand of a part (for what jobs can hold), whether the user holds a permission, and a full credit note at the till. */
  partStock?: (variantId: string) => bigint;
  can?: (perm: string) => boolean;
  creditNote?: (originalSaleId: string, reason: string, tenders: Tender[]) => { id: string; number: string };
  /** Moves stock; returns the value moved (negative when stock goes out). */
  partMove: (variantId: string, qty: bigint, unitCost?: bigint) => bigint;
  /** The till: the open shift of this terminal (POS-001/POS-004 when none). */
  shift: () => { id: string };
  /** Money into or out of the drawer for a deposit or its refund. */
  drawer: (shiftId: string, kind: 'repair_deposit' | 'repair_refund' | 'repair_applied', method: string, amount: bigint) => void;
  /** The repair invoice at collection, through the till's sale writer. */
  invoice: (customerId: string, lines: RepairEstimateLine[], tenders: Tender[]) => { id: string; number: string };
}

interface Out { id: string; template: string | null; params: string[] | null; text: string | null; status: string; statusAt: string | null; at: string; byName: string | null; buttons: string[]; estimateId: string | null; customerId: string; phone: string }
interface In { id: string; kind: string; text: string | null; handled: string | null; at: string; customerId: string | null; phone: string }
interface Row {
  job: RepairJob; estimates: RepairEstimate[]; work: RepairWorkLine[]; clearing: RepairDetail['clearing']; deposits: (RepairDetail['deposits'][number] & { method: string | null })[];
  history: RepairDetail['history']; assignments: RepairDetail['assignments']; photos: RepairDetail['photos']; passcode: string | null; readyAt: string | null;
  shares: { technicianId: string; share: number }[]; invoices: { id: string; number: string; lines: RepairEstimateLine[] }[]; corrections: RepairCorrectionRow[];
}

const SERVICES = [
  { id: 'svc-labour', sku: 'REPAIR-LABOUR', nameEn: 'Repair labour', nameAr: 'أجور الإصلاح', productType: 'service' },
  { id: 'svc-extra', sku: 'REPAIR-EXTRA', nameEn: 'Repair service', nameAr: 'خدمة إصلاح', productType: 'service' },
  { id: 'svc-diag', sku: 'REPAIR-DIAG', nameEn: 'Diagnosis fee', nameAr: 'رسوم التشخيص', productType: 'service' },
];
const RATE = 5;

export function createMockRepairs(h: RepairMockHooks) {
  const rows = new Map<string, Row>();
  const outbox: Out[] = [];
  const inbox: In[] = [];
  const optedOut = new Set<string>();
  let settings: RepairSettings = {
    preAuthorised: 0, warrantyDays: 90, passcodeKeepDays: 30, uncollectedDays: [30, 60, 90], diagnosisFee: 3, hourlyRate: 12, depositVatPoint: false,
    notifyStatuses: ['received', 'waiting_approval', 'ready'], termsEn: 'We check the device, tell you the price before any work beyond what you allowed, and keep it for 90 days after it is ready.',
    termsAr: 'نفحص الجهاز ونبلغك بالسعر قبل أي عمل يتجاوز ما وافقت عليه، ونحتفظ به 90 يومًا بعد جاهزيته.', version: 1,
  };
  const categories: RepairCategory[] = [
    { id: id(), code: 'SCREEN', nameEn: 'Screen', nameAr: 'الشاشة', turnaroundHours: 24, warrantyDays: null, preAuthorised: null, sort: 1, status: 'active', version: 1 },
    { id: id(), code: 'BATTERY', nameEn: 'Battery', nameAr: 'البطارية', turnaroundHours: 4, warrantyDays: 180, preAuthorised: null, sort: 2, status: 'active', version: 1 },
    { id: id(), code: 'BOARD', nameEn: 'Board and charging', nameAr: 'اللوحة والشحن', turnaroundHours: 72, warrantyDays: null, preAuthorised: null, sort: 3, status: 'active', version: 1 },
  ];
  const faultCodes: FaultCode[] = [
    { id: id(), categoryId: categories[0]!.id, code: 'CRACK', nameEn: 'Cracked glass', nameAr: 'زجاج مكسور', sort: 1, status: 'active', version: 1 },
    { id: id(), categoryId: categories[0]!.id, code: 'TOUCH', nameEn: 'Touch not working', nameAr: 'اللمس لا يعمل', sort: 2, status: 'active', version: 1 },
    { id: id(), categoryId: categories[1]!.id, code: 'DRAIN', nameEn: 'Drains fast', nameAr: 'تفرغ بسرعة', sort: 3, status: 'active', version: 1 },
    { id: id(), categoryId: null, code: 'NOPOWER', nameEn: 'Does not switch on', nameAr: 'لا يعمل', sort: 4, status: 'active', version: 1 },
  ];
  let wa = { enabled: true, provider: 'test' as 'meta' | 'test', phoneNumberId: null as string | null, wabaId: null as string | null, displayPhone: '+968 9000 0000' as string | null,
    hasToken: false, hasSecret: false, verifyToken: 'kme-verify-3f9a', dailyCap: 1000, staleHours: 24, version: 1 };
  const templates: WhatsAppTemplate[] = (['received', 'estimate', 'ready', 'uncollected'] as const).flatMap((k) => (['en', 'ar'] as const).map((lang) => ({
    id: id(), code: `kme_repair_${k}`, lang, metaName: `kme_repair_${k}_${lang}`, metaLang: lang, version: 1,
    params: k === 'estimate' ? ['name', 'job', 'amount'] : ['name', 'job'],
    buttons: k === 'estimate' ? (lang === 'en' ? ['Approve', 'Call me'] : ['موافق', 'اتصلوا بي']) : [],
    body: lang === 'en'
      ? { received: 'Hello {{1}}, we received your device. Job {{2}}.', estimate: 'Hello {{1}}, the estimate for job {{2}} is {{3}} OMR. Tap Approve to go ahead.', ready: 'Hello {{1}}, job {{2}} is ready to collect.', uncollected: 'Hello {{1}}, job {{2}} is still waiting for you.' }[k]
      : { received: 'مرحبًا {{1}}، استلمنا جهازك. رقم الأمر {{2}}.', estimate: 'مرحبًا {{1}}، تقدير الأمر {{2}} هو {{3}} ر.ع. اضغط موافق للمتابعة.', ready: 'مرحبًا {{1}}، الأمر {{2}} جاهز للاستلام.', uncollected: 'مرحبًا {{1}}، الأمر {{2}} ما زال بانتظارك.' }[k],
  })));

  const itemOf = (vid: string) => {
    const s = SERVICES.find((x) => x.id === vid);
    return s ? { sku: s.sku, descriptionEn: s.nameEn, descriptionAr: s.nameAr, productType: 'service', listPrice: null as number | null } : h.item(vid);
  };
  const rowOf = (jid: string) => { const r = rows.get(jid); if (!r) throw problem(404, 'REP-007', 'That repair job was not found in your branches.'); return r; };
  const current = (r: Row) => r.estimates[0] ?? null;
  const ceiling = (r: Row) => r.estimates.filter((e) => e.status === 'approved' && ['signature', 'phone', 'whatsapp'].includes(e.approvalMethod ?? ''))
    .reduce((m, e) => Math.max(m, e.total), r.job.preAuthorised);
  const wip = (r: Row) => r.work.filter((w) => w.kind === 'part' || w.kind === 'external').reduce((a, w) => a + b(w.cost ?? 0), 0n) - r.clearing.reduce((a, c) => a + b(c.amount), 0n);
  // Phase 9 (13.4): what each job's latest approved estimate needs; free stock goes to the oldest jobs first.
  const latestApproved = (r: Row) => r.estimates.find((e) => e.status === 'approved') ?? null;
  const remaining = (r: Row) => {
    const out = new Map<string, { needed: number; fitted: number }>();
    for (const l of latestApproved(r)?.lines ?? []) if (l.kind === 'part') out.set(l.variantId, { needed: (out.get(l.variantId)?.needed ?? 0) + l.qty, fitted: 0 });
    for (const w of r.work) if (w.kind === 'part' && w.variantId) out.set(w.variantId, { needed: out.get(w.variantId)?.needed ?? 0, fitted: (out.get(w.variantId)?.fitted ?? 0) + (w.qty ?? 0) });
    return out;
  };
  const partsOf = (r: Row): RepairPartRow[] => {
    const ended = !!r.job.outcome || r.job.status === 'delivered';
    const open = [...rows.values()].filter((x) => !x.job.outcome && x.job.status !== 'delivered').sort((x, y) => x.job.createdAt.localeCompare(y.job.createdAt));
    return [...remaining(r)].map(([vid, q]) => {
      const it = itemOf(vid);
      const left = ended ? 0 : Math.max(0, q.needed - q.fitted);
      let free = Number(fromBaisa(h.partStock?.(vid) ?? 1_000_000_000n));
      for (const o of open) { if (o === r) break; const oq = remaining(o).get(vid); if (oq) free -= Math.max(0, oq.needed - oq.fitted); }
      const reserved = Math.max(0, Math.min(left, free));
      return { variantId: vid, sku: it?.sku ?? '', nameEn: it?.descriptionEn ?? '', nameAr: it?.descriptionAr ?? '', tracking: (it?.tracking ?? 'none') as RepairPartRow['tracking'],
        needed: q.needed, fitted: q.fitted, reserved, requested: left - reserved, requestedSince: left - reserved > 0 ? r.job.createdAt : null };
    });
  };
  const short = (r: Row) => partsOf(r).reduce((a, p) => a + p.requested, 0);
  /** The invoice that stands: the latest one, unless a correction credited it. */
  const standing = (r: Row) => { const inv = r.invoices.at(-1); return inv && !r.corrections.some((c) => c.creditedNumber === inv.number) ? inv : null; };
  const commJob = (r: Row): CommissionJob => {
    const inv = standing(r);
    const net = (k: string) => (inv?.lines ?? []).filter((l) => l.kind === k).reduce((a, l) => a + l.netAmount, 0);
    return { id: r.job.id, number: r.job.number, branchId: r.job.branchId, branchCode: r.job.branchCode, technicianId: r.job.technicianId, categoryId: r.job.categoryId,
      status: r.job.status, outcome: r.job.outcome, warranty: !!r.job.warrantyOfId, warrantyOfId: r.job.warrantyOfId, saleId: inv?.id ?? null, saleNumber: inv?.number ?? null,
      invoiceVersion: 1 + r.corrections.filter((c) => c.correctedNumber).length, shares: r.shares,
      basis: { labour: net('labour'), partsRevenue: net('part'), partsCost: n(r.work.filter((w) => w.kind === 'part').reduce((a, w) => a + b(w.cost ?? 0), 0n)),
        external: n(r.work.filter((w) => w.kind === 'external').reduce((a, w) => a + b(w.cost ?? 0), 0n)) },
      createdAt: r.job.createdAt, deliveredAt: r.job.deliveredAt };
  };
  const comm = createMockCommissions({ need: h.need, can: (p) => h.can?.(p) ?? true, next: h.next, users: h.users, jobs: () => [...rows.values()].map(commJob),
    categoryName: (cid) => { const c = categories.find((x) => x.id === cid); return c ? { en: c.nameEn, ar: c.nameAr } : null; } });
  const balance = (r: Row) => r.deposits.reduce((a, d) => a + (d.kind === 'deposit' ? b(d.amount) : -b(d.amount)), 0n);
  const heldTax = (r: Row) => r.deposits.reduce((a, d) => a + (d.kind === 'deposit' ? b(d.tax) : -b(d.tax)), 0n);
  const nameOf = (u: User) => u.displayNameEn;

  const notify = (r: Row, kind: 'received' | 'estimate' | 'ready' | 'uncollected', params: string[], estimateId: string | null = null) => {
    const j = r.job;
    if (!j.notifyWhatsapp || !j.customerOptIn || !j.customerPhone || optedOut.has(j.customerPhone)) return;
    const t = templates.find((x) => x.code === `kme_repair_${kind}` && x.lang === j.lang)!;
    outbox.push({ id: id(), template: t.code, params, text: null, status: wa.enabled ? 'sent' : 'queued', statusAt: wa.enabled ? now() : null, at: now(), byName: null,
      buttons: t.buttons, estimateId, customerId: j.customerId, phone: j.customerPhone });
  };
  const render = (o: Out) => {
    if (o.text) return o.text;
    const t = templates.find((x) => x.code === o.template)!;
    return t.body.replace(/\{\{(\d)\}\}/g, (_, i: string) => o.params?.[Number(i) - 1] ?? '');
  };

  const move = (r: Row, to: RepairStatus, u: User, note: string | null) => {
    const j = r.job;
    if (!REPAIR_TRANSITIONS[j.status].includes(to)) throw problem(409, 'REP-001', `A job that is ${words(j.status)} cannot move to ${words(to)}.`);
    if (NEEDS_APPROVED_ESTIMATE.includes(to)) {
      const e = current(r);
      if (!e) throw problem(409, 'REP-005', 'Write the estimate first: the work goes ahead on an approved estimate.');
      if (e.status !== 'approved' && e.total <= ceiling(r) && e.lines.length > 0) approve(r, e, 'pre_authorised', null, null, u);
      if (e.status !== 'approved') throw problem(409, 'REP-005', `The estimate (${e.total.toFixed(3)}) is more than the customer authorised (${ceiling(r).toFixed(3)}). Send it and wait for the customer's approval.`);
      if (!current(r) || current(r)!.status !== 'approved') throw problem(409, 'REP-002', 'Approve the current estimate first.');
    }
    if (to === 'waiting_approval') {
      const e = current(r);
      if (!e || e.lines.length === 0) throw problem(409, 'REP-005', 'Write the estimate before asking the customer.');
      if (e.status === 'draft') { e.status = 'sent'; e.sentAt = now(); }
      notify(r, 'estimate', [j.customerName.split(' ')[0]!, j.number, fromBaisa(b(e.total))], e.id);
    }
    r.history.push({ from: j.status, to, note, at: now(), userName: nameOf(u) });
    j.status = to;
    j.outcome = to === 'completed' ? 'repaired' : to === 'cancelled' ? 'cancelled' : to === 'unrepairable' ? 'unrepairable' : j.outcome;
    j.next = REPAIR_TRANSITIONS[to];
    j.version += 1;
    if (to === 'ready') { r.readyAt = now(); if (settings.notifyStatuses.includes('ready')) notify(r, 'ready', [j.customerName.split(' ')[0]!, j.number]); }
    // Work cannot start without its parts: the job waits for them (13.4).
    if (to === 'in_progress' && short(r) > 0) move(r, 'waiting_parts', u, `Waiting for parts: ${short(r)} not in stock`);
  };
  const approve = (r: Row, e: RepairEstimate, method: NonNullable<RepairEstimate['approvalMethod']>, approver: string | null, ref: string | null, u: User | null) => {
    if (method === 'pre_authorised' && e.total > ceiling(r)) throw problem(409, 'REP-005', `The estimate is above the pre-authorised ${ceiling(r).toFixed(3)}.`);
    Object.assign(e, { status: 'approved', approvalMethod: method, approvedAt: now(), approvedBy: u ? nameOf(u) : null, approverName: approver, approvalRef: ref, version: e.version + 1 });
  };
  const priceLines = (lines: EstimateLineInput[]): RepairEstimateLine[] => {
    if (lines.length === 0) throw problem(409, 'REP-005', 'The estimate has no lines yet.');
    return lines.map((l, i) => {
      const it = itemOf(l.variantId);
      if (!it) throw problem(400, 'VAL-001', 'Unknown item.');
      const qty = l.kind === 'labour' ? 1 : (l.qty ?? 1);
      const unit = l.kind === 'labour' ? (l.unitPrice != null ? b(l.unitPrice) : labourPrice(l.minutes ?? 0, settings.hourlyRate)) : l.unitPrice != null ? b(l.unitPrice) : b(it.listPrice ?? 0);
      const gross = (unit * b(qty)) / 1000n;
      const tax = depositTax(gross, RATE);
      return { id: id(), lineNo: i + 1, kind: l.kind, variantId: l.variantId, productType: it.productType, descriptionEn: l.descriptionEn || it.descriptionEn,
        descriptionAr: l.descriptionAr || it.descriptionAr, qty, minutes: l.kind === 'labour' ? (l.minutes ?? null) : null, unitPrice: n(unit), taxRate: RATE,
        netAmount: n(gross - tax), taxAmount: n(tax), grossAmount: n(gross) };
    });
  };
  const totals = (lines: RepairEstimateLine[]) => ({ netTotal: lines.reduce((a, l) => a + l.netAmount, 0), taxTotal: lines.reduce((a, l) => a + l.taxAmount, 0), total: lines.reduce((a, l) => a + l.grossAmount, 0) });
  const charges = (r: Row, fee: number): RepairEstimateLine[] => {
    if (r.job.warrantyOfId) return [];
    if (r.job.outcome === 'repaired') return current(r)?.status === 'approved' ? current(r)!.lines : [];
    return fee > 0 ? priceLines([{ kind: 'extra', variantId: 'svc-diag', unitPrice: fee }]) : [];
  };
  const detail = (r: Row): RepairDetail => {
    const fee = settings.diagnosisFee;
    const ch = r.job.outcome ? charges(r, fee) : [];
    const total = b(ch.reduce((a, l) => a + l.grossAmount, 0));
    const dep = balance(r);
    return structuredClone({
      job: { ...r.job, ceiling: ceiling(r), overdue: Date.parse(r.job.promisedAt) < Date.now() && !['ready', 'delivered', 'cancelled', 'unrepairable', 'completed'].includes(r.job.status) },
      estimates: r.estimates, work: r.work, clearing: r.clearing, deposits: r.deposits, history: r.history, assignments: r.assignments, photos: r.photos,
      claims: [...rows.values()].filter((x) => x.job.warrantyOfId === r.job.id).map((x) => ({ id: x.job.id, number: x.job.number, status: x.job.status })),
      money: { deposit: n(dep), depositTax: n(heldTax(r)), wip: n(wip(r)), charges: ch, chargesTotal: n(total), due: n(total > dep ? total - dep : 0n), excess: n(dep > total ? dep - total : 0n),
        diagnosisFee: fee, hourlyRate: settings.hourlyRate, depositVatPoint: settings.depositVatPoint },
      parts: partsOf(r), corrections: r.corrections,
      invoice: (() => {
        const inv = standing(r);
        if (!inv) return null;
        const sum = (k: 'netAmount' | 'taxAmount' | 'grossAmount') => inv.lines.reduce((a, l) => a + l[k], 0);
        return { header: { id: inv.id, number: inv.number, saleDate: now().slice(0, 10), grossTotal: sum('grossAmount'), taxTotal: sum('taxAmount'), netTotal: sum('netAmount'), cashRounding: 0 },
          lines: inv.lines.map((l) => ({ id: l.id, kind: l.kind, variantId: l.variantId, descriptionEn: l.descriptionEn, descriptionAr: l.descriptionAr, qty: l.qty, unitPrice: l.unitPrice,
            netAmount: l.netAmount, taxAmount: l.taxAmount, grossAmount: l.grossAmount })) };
      })(),
    });
  };
  const messagesOf = (r: Row) => {
    const j = r.job;
    const out: RepairMessage[] = outbox.filter((o) => o.customerId === j.customerId).map((o) => ({ id: o.id, direction: 'out', template: o.template, params: o.params, text: render(o), status: o.status, statusReason: null, statusAt: o.statusAt, at: o.at, byName: o.byName }));
    const inn: RepairMessage[] = inbox.filter((m) => m.customerId === j.customerId || m.phone === j.customerPhone).map((m) => ({ id: m.id, direction: 'in', kind: m.kind, text: m.text, handled: m.handled, at: m.at }));
    const last = inn.filter((m) => m.kind !== 'stop').map((m) => Date.parse(m.at)).reduce((a, x) => Math.max(a, x), 0);
    return { messages: [...out, ...inn].sort((x, y) => x.at.localeCompare(y.at)), canReply: last > Date.now() - 864e5, replyUntil: last ? new Date(last + 864e5).toISOString() : null, optIn: j.customerOptIn, notify: j.notifyWhatsapp };
  };

  const intake = (req: RepairIntake, u: User, warrantyOf: Row | null): Row => {
    const cust = h.customers().find((c) => c.id === req.customerId);
    if (!cust) throw problem(400, 'VAL-001', 'Choose the customer.');
    const cat = categories.find((c) => c.id === req.categoryId);
    if (!cat) throw problem(400, 'VAL-001', 'Choose the kind of repair.');
    if ((req.problem ?? '').trim().length < 3) throw problem(400, 'VAL-001', 'Describe the problem.');
    if ((req.termsName ?? '').trim().length < 2 || !req.termsSignature) throw problem(400, 'VAL-001', 'The customer signs to accept the terms.');
    if (req.passcodeMode === 'provided' && !req.passcode) throw problem(400, 'VAL-001', 'Type the passcode, or choose another option.');
    if (req.notifyWhatsapp && !cust.phoneE164) throw problem(400, 'WA-003', 'WhatsApp updates need the customer’s mobile number: add it to the customer first.');
    const device = warrantyOf ? { ...warrantyOf.job } : null;
    const d = req.device ?? {};
    if (!device && !req.customerDeviceId && !(d.imei1 || d.serial)) throw problem(400, 'VAL-001', 'Scan the IMEI or serial number.');
    const known = !device && req.customerDeviceId ? [...rows.values()].find((x) => x.job.customerDeviceId === req.customerDeviceId)?.job : null;
    const at = now();
    const job: RepairJob = {
      id: req.id ?? id(), number: h.next('repair', 'R'), status: 'received', outcome: null, branchId: h.branchId, branchCode: h.branchCode,
      customerId: cust.id, customerName: cust.nameEn, customerNameAr: cust.nameAr, customerPhone: cust.phoneE164, customerOptIn: !!req.notifyWhatsapp,
      customerDeviceId: device?.customerDeviceId ?? req.customerDeviceId ?? id(), imei1: device?.imei1 ?? known?.imei1 ?? d.imei1 ?? null, imei2: device?.imei2 ?? known?.imei2 ?? d.imei2 ?? null,
      serial: device?.serial ?? known?.serial ?? d.serial ?? null, brand: device?.brand ?? known?.brand ?? d.brand ?? null, model: device?.model ?? known?.model ?? d.model ?? 'Phone',
      colour: device?.colour ?? known?.colour ?? d.colour ?? null, deviceUnitId: null, categoryId: cat.id, categoryCode: cat.code, categoryNameEn: cat.nameEn, categoryNameAr: cat.nameAr,
      technicianId: req.technicianId ?? null, technicianName: h.users().find((x) => x.id === req.technicianId)?.displayNameEn ?? null, warrantyOfId: warrantyOf?.job.id ?? null,
      warrantyOfNumber: warrantyOf?.job.number ?? null, soldSaleId: null, soldSaleNumber: null, problem: req.problem.trim(), faultCodes: req.faultCodes ?? [], condition: req.condition ?? {},
      conditionNotes: req.conditionNotes ?? null, accessories: { items: req.accessories ?? [], other: req.accessoriesOther ?? null }, passcodeMode: req.passcodeMode,
      passcodeHeld: req.passcodeMode === 'provided', passcodePurgedAt: null, preAuthorised: warrantyOf ? 0 : (req.preAuthorised ?? cat.preAuthorised ?? settings.preAuthorised), ceiling: 0,
      warrantyDays: cat.warrantyDays ?? settings.warrantyDays, promisedAt: req.promisedAt ?? new Date(Date.now() + cat.turnaroundHours * 36e5).toISOString(), overdue: false,
      termsName: req.termsName.trim(), termsAcceptedAt: at, notifyWhatsapp: !!req.notifyWhatsapp, lang: req.lang ?? 'ar', deliverySaleId: null, deliverySaleNumber: null, deliveredAt: null,
      deliveredByName: null, collectedBy: null, collectionCheck: null, warrantyUntil: null, remindedDays: 0, createdAt: at, createdByName: nameOf(u), version: 1, qr: '', next: REPAIR_TRANSITIONS.received,
    };
    job.qr = `KITE-R|${job.number}|${job.id.replace(/-/g, '').slice(0, 12)}`;
    const r: Row = { job, estimates: [], work: [], clearing: [], deposits: [], history: [{ from: null, to: 'received', note: warrantyOf ? `Warranty claim on ${warrantyOf.job.number}` : null, at, userName: nameOf(u) }],
      assignments: job.technicianId ? [{ at, fromName: null, toName: job.technicianName, byName: nameOf(u) }] : [], photos: [], passcode: req.passcode ?? null, readyAt: null,
      shares: [], invoices: [], corrections: [] };
    r.photos.push({ id: id(), stage: 'terms_signature', contentType: 'image/png', size: req.termsSignature.length, sha256: '', caption: `Terms accepted by ${job.termsName}`, createdAt: at });
    for (const p of req.photos ?? []) r.photos.push({ id: id(), stage: p.stage, contentType: 'image/jpeg', size: p.dataUrl.length, sha256: '', caption: p.caption ?? null, createdAt: at });
    if (warrantyOf) r.estimates.unshift({ id: id(), estimateNo: 1, status: 'approved', netTotal: 0, taxTotal: 0, total: 0, note: null, sentAt: null, approvalMethod: 'warranty', approvedAt: at,
      approvedBy: nameOf(u), approverName: null, approvalRef: null, declineReason: null, declinedAt: null, createdAt: at, createdBy: nameOf(u), version: 1, lines: [] });
    rows.set(job.id, r);
    if (settings.notifyStatuses.includes('received')) notify(r, 'received', [cust.nameEn.split(' ')[0]!, job.number]);
    return r;
  };

  // A job in the workshop and one ready to collect, for design review.
  const seed = () => {
    const u = h.users()[0]!;
    const cust = h.customers().find((c) => c.kind === 'individual') ?? h.customers()[0];
    if (!cust) return;
    const sig = 'data:image/png;base64,iVBORw0KGgo=';
    const a = intake({ customerId: cust.id, categoryId: categories[0]!.id, problem: 'Screen cracked after a fall; touch works.', passcodeMode: 'customer_present', termsName: cust.nameEn,
      termsSignature: sig, device: { imei1: '359876543210985', brand: 'Apple', model: 'iPhone 13', colour: 'Blue' }, faultCodes: ['CRACK'], condition: { screen: 'fault', body: 'ok' },
      accessories: ['case'], notifyWhatsapp: true, lang: 'en' }, u, null);
    move(a, 'inspection_pending', u, null); move(a, 'under_diagnosis', u, null);
    const c = intake({ customerId: cust.id, categoryId: categories[1]!.id, problem: 'Battery drains by noon.', passcodeMode: 'none', termsName: cust.nameEn, termsSignature: sig,
      device: { serial: 'R58N123ABC', brand: 'Samsung', model: 'Galaxy A54' }, faultCodes: ['DRAIN'], preAuthorised: 20 }, u, null);
    move(c, 'inspection_pending', u, null); move(c, 'under_diagnosis', u, null);
    const e = priceLines([{ kind: 'labour', variantId: 'svc-labour', minutes: 30 }, { kind: 'extra', variantId: 'svc-extra', unitPrice: 9, descriptionEn: 'Battery (fitted)', descriptionAr: 'بطارية (مركبة)' }]);
    c.estimates.unshift({ id: id(), estimateNo: 1, status: 'draft', ...totals(e), note: null, sentAt: null, approvalMethod: null, approvedAt: null, approvedBy: null, approverName: null,
      approvalRef: null, declineReason: null, declinedAt: null, createdAt: now(), createdBy: nameOf(u), version: 1, lines: e });
    move(c, 'in_progress', u, null); move(c, 'testing', u, null); move(c, 'completed', u, null); move(c, 'ready', u, null);
  };

  const api: RepairApi = {
    async repairs(f) {
      h.need('REPAIR.VIEW');
      const q = f?.q?.trim().toLowerCase();
      const list = [...rows.values()].filter(({ job: j }) => (!f?.status || j.status === f.status) && (!f?.technicianId || j.technicianId === f.technicianId) && (!f?.open || j.status !== 'delivered')
        && (!q || j.number.toLowerCase().includes(q) || j.customerName.toLowerCase().includes(q) || (j.customerPhone ?? '').includes(q) || j.imei1 === q || (j.serial ?? '').toLowerCase() === q));
      return delay(list.map((r) => {
        const j = r.job; const d = detail(r).job; const e = current(r);
        return { id: j.id, number: j.number, status: j.status, outcome: j.outcome, branchCode: j.branchCode, branchId: j.branchId, customerName: j.customerName, customerPhone: j.customerPhone,
          device: [j.brand, j.model].filter(Boolean).join(' '), identifier: j.imei1 ?? j.serial, categoryEn: j.categoryNameEn, categoryAr: j.categoryNameAr, technicianId: j.technicianId,
          technicianName: j.technicianName, promisedAt: j.promisedAt, overdue: d.overdue, warrantyClaim: !!j.warrantyOfId, createdAt: j.createdAt, deliveredAt: j.deliveredAt,
          estimate: e?.total ?? null, estimateStatus: e?.status ?? null, deposit: n(balance(r)), statusAt: r.history.at(-1)?.at ?? null,
          unreadMessages: inbox.filter((m) => m.customerId === j.customerId && m.handled !== 'seen').length, remindedDays: j.remindedDays, version: j.version };
      }).sort((x, y) => Number(x.status === 'delivered') - Number(y.status === 'delivered') || Number(y.overdue) - Number(x.overdue) || x.promisedAt.localeCompare(y.promisedAt)));
    },
    async repair(jid) { h.need('REPAIR.VIEW'); return delay(detail(rowOf(jid))); },
    async repairLookup(code) {
      h.need('REPAIR.VIEW');
      const c = code.trim();
      const hits = [...rows.values()].filter((r) => r.job.imei1 === c || (r.job.serial ?? '').toUpperCase() === c.toUpperCase());
      const devices = [...new Map(hits.map((r) => [r.job.customerDeviceId, { id: r.job.customerDeviceId, customerId: r.job.customerId, customerName: r.job.customerName, customerPhone: r.job.customerPhone,
        imei1: r.job.imei1, imei2: r.job.imei2, serial: r.job.serial, brand: r.job.brand, model: r.job.model, colour: r.job.colour }])).values()];
      const jobs = hits.map((r) => ({ id: r.job.id, number: r.job.number, status: r.job.status, outcome: r.job.outcome, createdAt: r.job.createdAt, deliveredAt: r.job.deliveredAt,
        warrantyUntil: r.job.warrantyUntil, inWarranty: !!r.job.warrantyUntil && r.job.warrantyUntil >= now().slice(0, 10) }));
      return delay({ kind: /^\d{15}$/.test(c) ? 'imei' : 'serial', value: c, devices, unit: null, sold: null, jobs });
    },
    async createRepair(req) { const u = h.need('REPAIR.INTAKE'); return delay(detail(intake(req, u, null))); },
    async warrantyClaim(jid, req) {
      const u = h.need('REPAIR.INTAKE');
      const o = rowOf(jid);
      if (o.job.status !== 'delivered' || o.job.outcome !== 'repaired') throw problem(409, 'REP-002', 'A warranty claim is made on a repaired job that was delivered.');
      if (!o.job.warrantyUntil || o.job.warrantyUntil < now().slice(0, 10)) throw problem(409, 'REP-002', 'The repair warranty has ended.');
      const claim = intake({ ...req, customerId: o.job.customerId, categoryId: o.job.categoryId }, u, o);
      comm.clawback(commJob(o), claim.job.number);
      return delay(detail(claim));
    },
    async moveRepair(jid, to, note) {
      const u = h.need(to === 'cancelled' ? 'REPAIR.CANCEL' : 'REPAIR.WORK');
      if (to === 'delivered') throw problem(409, 'REP-004', 'A device is delivered with the collection step: the customer’s check, signature and payment.');
      const r = rowOf(jid); move(r, to, u, note ?? null); return delay(detail(r));
    },
    async assignRepair(jid, tid) {
      const u = h.need('REPAIR.ASSIGN');
      const r = rowOf(jid);
      if (r.job.status === 'delivered') throw problem(409, 'REP-002', 'A delivered job keeps its technician.');
      const to = h.users().find((x) => x.id === tid);
      r.assignments.push({ at: now(), fromName: r.job.technicianName, toName: to?.displayNameEn ?? null, byName: nameOf(u) });
      Object.assign(r.job, { technicianId: tid, technicianName: to?.displayNameEn ?? null, version: r.job.version + 1 });
      return delay(detail(r));
    },
    async repairTechnicians() { h.need('REPAIR.VIEW'); return delay(h.users().map((u) => ({ id: u.id, nameEn: u.displayNameEn, nameAr: u.displayNameAr }))); },
    async revealPasscode(jid) {
      h.need('REPAIR.CREDENTIAL.VIEW');
      const r = rowOf(jid);
      if (!r.passcode) throw problem(404, 'REP-006', r.job.passcodePurgedAt ? 'The passcode was deleted.' : 'No passcode was given for this job.');
      return delay({ passcode: r.passcode });
    },
    async addRepairPhoto(jid, p) { h.need('REPAIR.WORK'); const r = rowOf(jid); const pid = id(); r.photos.push({ id: pid, stage: p.stage, contentType: 'image/jpeg', size: p.dataUrl.length, sha256: '', caption: p.caption ?? null, createdAt: now() }); return delay({ id: pid }); },
    async repairPhoto() { return delay(new Blob([], { type: 'image/png' })); },
    async repairMessages(jid) { h.need('REPAIR.VIEW'); const r = rowOf(jid); inbox.filter((m) => m.customerId === r.job.customerId).forEach((m) => { m.handled = 'seen'; }); return delay(messagesOf(r)); },
    async sendRepairMessage(jid, text) {
      const u = h.need('NOTIF.REPLY');
      const r = rowOf(jid);
      if (!messagesOf(r).canReply) throw problem(409, 'WA-002', 'WhatsApp allows a typed reply only within 24 hours of the customer’s last message. Call the customer instead.');
      outbox.push({ id: id(), template: null, params: null, text: text.trim(), status: 'sent', statusAt: now(), at: now(), byName: nameOf(u), buttons: [], estimateId: null, customerId: r.job.customerId, phone: r.job.customerPhone! });
      return delay(messagesOf(r));
    },
    async uncollected() {
      h.need('REPAIR.VIEW');
      return delay([...rows.values()].filter((r) => r.job.status === 'ready').map((r) => ({ id: r.job.id, number: r.job.number, branchCode: r.job.branchCode, customerName: r.job.customerName,
        customerPhone: r.job.customerPhone, device: r.job.model, outcome: r.job.outcome, readySince: r.readyAt ?? now(), days: Math.floor((Date.now() - Date.parse(r.readyAt ?? now())) / 864e5),
        remindedDays: r.job.remindedDays, deposit: n(balance(r)) })));
    },
    async writeEstimate(jid, lines, note) {
      const u = h.need('REPAIR.WORK');
      const r = rowOf(jid);
      if (r.job.outcome || r.job.status === 'delivered') throw problem(409, 'REP-002', `Job ${r.job.number} has ended.`);
      if (r.job.warrantyOfId) throw problem(409, 'REP-005', 'A warranty claim is at no charge; it has no estimate to write.');
      const priced = priceLines(lines);
      const prev = current(r);
      if (prev && prev.status !== 'declined' && prev.status !== 'approved') prev.status = 'superseded';
      r.estimates.unshift({ id: id(), estimateNo: (prev?.estimateNo ?? 0) + 1, status: 'draft', ...totals(priced), note: note ?? null, sentAt: null, approvalMethod: null, approvedAt: null,
        approvedBy: null, approverName: null, approvalRef: null, declineReason: null, declinedAt: null, createdAt: now(), createdBy: nameOf(u), version: 1, lines: priced });
      return delay(detail(r));
    },
    async updateEstimate(jid, eid, lines, note) {
      h.need('REPAIR.WORK');
      const r = rowOf(jid); const e = r.estimates.find((x) => x.id === eid);
      if (!e || e !== current(r) || e.status !== 'draft') throw problem(409, 'REP-005', 'Only a draft estimate is changed; write a new version.');
      const priced = priceLines(lines);
      Object.assign(e, { ...totals(priced), lines: priced, note: note ?? null, version: e.version + 1 });
      return delay(detail(r));
    },
    async sendEstimate(jid, eid) {
      h.need('REPAIR.WORK');
      const r = rowOf(jid); const e = r.estimates.find((x) => x.id === eid);
      if (!e || e !== current(r) || e.status === 'approved' || e.status === 'declined') throw problem(409, 'REP-005', 'Only the current estimate is sent.');
      e.status = 'sent'; e.sentAt = now();
      notify(r, 'estimate', [r.job.customerName.split(' ')[0]!, r.job.number, fromBaisa(b(e.total))], e.id);
      return delay(detail(r));
    },
    async approveEstimate(jid, eid, a) {
      const u = h.need('REPAIR.WORK');
      const r = rowOf(jid); const e = r.estimates.find((x) => x.id === eid);
      if (!e || e !== current(r) || e.status === 'approved' || e.status === 'declined') throw problem(409, 'REP-005', 'The current estimate is already approved: write a new version for the extra work.');
      if (a.method === 'signature') { if (!a.approverName?.trim() || !a.signature) throw problem(400, 'VAL-001', 'The customer types their name and signs.'); approve(r, e, 'signature', a.approverName.trim(), null, u); }
      else if (a.method === 'phone') { if (!a.approverName?.trim()) throw problem(400, 'VAL-001', 'Type who approved.'); if (!a.phone?.trim()) throw problem(400, 'VAL-001', 'Type the number you called.'); approve(r, e, 'phone', a.approverName.trim(), a.phone.trim(), u); }
      else if (a.method === 'pre_authorised') approve(r, e, 'pre_authorised', null, null, u);
      else throw problem(400, 'VAL-001', 'Approval is by signature, by phone, or within the pre-authorised amount. WhatsApp approvals arrive by themselves.');
      if (a.then) move(r, a.then, u, `Estimate version ${e.estimateNo} approved (${a.method})`);
      return delay(detail(r));
    },
    async declineEstimate(jid, eid, reason) {
      h.need('REPAIR.WORK');
      const r = rowOf(jid); const e = r.estimates.find((x) => x.id === eid);
      if (!e || e !== current(r) || e.status === 'approved' || e.status === 'declined') throw problem(409, 'REP-005', 'Only an estimate waiting for the customer is declined.');
      if (reason.trim().length < 2) throw problem(400, 'VAL-001', 'Give the reason.');
      Object.assign(e, { status: 'declined', declineReason: reason.trim(), declinedAt: now() });
      return delay(detail(r));
    },
    async fitPart(jid, p) {
      const u = h.need('REPAIR.WORK');
      const r = rowOf(jid);
      if (r.job.outcome || r.job.status === 'delivered') throw problem(409, 'REP-002', `Job ${r.job.number} has ended; no parts go on it.`);
      if (!(p.qty > 0)) throw problem(400, 'VAL-001', 'The quantity is more than zero.');
      const it = h.item(p.variantId);
      if (!it) throw problem(400, 'VAL-001', 'Unknown part.');
      const cost = -h.partMove(p.variantId, -b(p.qty));
      r.work.push({ id: id(), kind: 'part', variantId: p.variantId, sku: it.sku, nameEn: it.descriptionEn, nameAr: it.descriptionAr, qty: p.qty, cost: n(cost), returnOfId: null, minutes: null,
        technicianName: null, note: p.note ?? null, createdAt: now(), createdBy: nameOf(u), qtyLeft: p.qty, costLeft: n(cost), writtenOff: false });
      return delay(detail(r));
    },
    async returnPart(jid, lineId, qty, note) {
      const u = h.need('REPAIR.WORK');
      const r = rowOf(jid); const l = r.work.find((x) => x.id === lineId && x.kind === 'part' && !x.returnOfId);
      if (!l || l.writtenOff) throw problem(409, 'REP-002', 'Nothing of that part is left on the job.');
      const back = r.work.filter((x) => x.returnOfId === l.id);
      const cost = returnCost(b(l.qty!), b(l.cost!), b(back.reduce((a, x) => a - x.qty!, 0)), b(back.reduce((a, x) => a - x.cost!, 0)), b(qty));
      h.partMove(l.variantId!, b(qty), (cost * 1000n) / b(qty));
      r.work.push({ ...l, id: id(), qty: -qty, cost: -n(cost), returnOfId: l.id, note: note ?? null, createdAt: now(), createdBy: nameOf(u), qtyLeft: null, costLeft: null });
      l.qtyLeft = (l.qtyLeft ?? 0) - qty; l.costLeft = n(b(l.costLeft ?? 0) - cost);
      return delay(detail(r));
    },
    async writeOffPart(jid, lineId, reason) {
      h.need('REPAIR.WORK');
      const r = rowOf(jid); const l = r.work.find((x) => x.id === lineId);
      if (!l || l.writtenOff || !(l.qtyLeft && l.qtyLeft > 0)) throw problem(409, 'REP-002', 'Nothing of that part is left on the job.');
      r.clearing.push({ kind: 'written_off', amount: l.costLeft ?? 0, reason, createdAt: now() });
      l.writtenOff = true; l.costLeft = 0;
      return delay(detail(r));
    },
    async logLabour(jid, l) {
      const u = h.need('REPAIR.WORK');
      const r = rowOf(jid);
      if (!Number.isInteger(l.minutes) || l.minutes < 1 || l.minutes > 6000) throw problem(400, 'VAL-001', 'Labour is 1 to 6000 minutes.');
      r.work.push({ id: id(), kind: 'labour', variantId: null, sku: null, nameEn: null, nameAr: null, qty: null, cost: null, returnOfId: null, minutes: l.minutes,
        technicianName: h.users().find((x) => x.id === l.technicianId)?.displayNameEn ?? nameOf(u), note: l.note ?? null, createdAt: now(), createdBy: nameOf(u), qtyLeft: null, costLeft: null, writtenOff: false });
      return delay(detail(r));
    },
    async takeDeposit(jid, d) {
      const u = h.need('REPAIR.INTAKE');
      const r = rowOf(jid);
      if (r.job.outcome || r.job.status === 'delivered') throw problem(409, 'REP-002', `Job ${r.job.number} has ended; no deposit is taken on it.`);
      const s = h.shift();
      const amount = b(d.amount);
      if (amount <= 0n) throw problem(400, 'VAL-001', 'The deposit is more than zero.');
      if (d.method === 'cash' && d.tendered != null && b(d.tendered) < amount) throw problem(400, 'POS-006', 'The cash given is less than the deposit.');
      const tax = settings.depositVatPoint ? depositTax(amount, RATE) : 0n;
      const number = h.next('deposit', 'DEP');
      const did = id();
      r.deposits.push({ id: did, number, kind: 'deposit', amount: n(amount), tax: n(tax), method: d.method, reason: null, saleNumber: null, createdAt: now(), createdBy: nameOf(u) });
      h.drawer(s.id, 'repair_deposit', d.method, amount);
      return delay({ id: did, number, amount: n(amount), tax: n(tax), change: d.method === 'cash' && d.tendered != null ? n(b(d.tendered) - amount) : 0, balance: n(balance(r)) });
    },
    async refundDeposit(jid, d) {
      const u = h.need('REPAIR.CANCEL');
      const r = rowOf(jid);
      const amount = b(d.amount);
      if (amount <= 0n || amount > balance(r)) throw problem(409, 'REP-003', `The job holds ${fromBaisa(balance(r))} in deposits.`);
      if (d.reason.trim().length < 2) throw problem(400, 'VAL-001', 'Give the reason.');
      const tax = taxShare(heldTax(r), balance(r), amount);
      if (d.method !== 'store_credit') h.drawer(h.shift().id, 'repair_refund', d.method, amount);
      r.deposits.push({ id: id(), number: h.next('deposit', 'DEP'), kind: 'refund', amount: n(amount), tax: n(tax), method: d.method, reason: d.reason.trim(), saleNumber: null, createdAt: now(), createdBy: nameOf(u) });
      return delay({ balance: n(balance(r)) });
    },
    async deliveryPreview(jid, fee) {
      h.need('REPAIR.VIEW');
      const r = rowOf(jid);
      const ch = charges(r, fee ?? settings.diagnosisFee);
      const gross = b(ch.reduce((a, l) => a + l.grossAmount, 0));
      const s = splitDelivery(gross, balance(r), heldTax(r));
      return delay({ number: r.job.number, status: r.job.status, outcome: r.job.outcome, charges: ch, gross: n(gross), deposit: n(balance(r)), applied: n(s.applied), due: n(s.due), excess: n(s.excess),
        wip: n(wip(r)), qr: r.job.qr, diagnosisFee: settings.diagnosisFee, customerId: r.job.customerId, customerName: r.job.customerName });
    },
    async deliver(jid, d: DeliverInput) {
      const u = h.need('REPAIR.INTAKE');
      const r = rowOf(jid); const j = r.job;
      if (j.status !== 'ready') throw problem(409, 'REP-002', `Job ${j.number} is ${words(j.status)}; only a ready job is collected.`);
      if (d.check === 'qr' && (d.qrCode ?? '').trim() !== j.qr) throw problem(409, 'REP-004', 'That QR code is not this job’s card. Scan the customer’s job card, or check the customer instead.');
      if (d.check !== 'qr' && d.check !== 'verified') throw problem(400, 'VAL-001', 'Collection needs the job card’s QR code or a check of the customer.');
      if ((d.collectedBy ?? '').trim().length < 2) throw problem(400, 'VAL-001', 'Type the name of the person collecting.');
      if (!d.signature) throw problem(400, 'VAL-001', 'The person collecting signs for the device.');
      const ch = charges(r, d.diagnosisFee ?? settings.diagnosisFee);
      const gross = b(ch.reduce((a, l) => a + l.grossAmount, 0));
      const s = splitDelivery(gross, balance(r), heldTax(r));
      if (d.expectedDue != null && b(d.expectedDue) !== s.due) throw problem(409, 'POS-010', `The amount due is now ${fromBaisa(s.due)}, not ${d.expectedDue.toFixed(3)}. Check and try again.`);
      const pays: Tender[] = (d.payments ?? []).map((p) => ({ ...p, amount: b(p.amount), tendered: p.tendered == null ? null : b(p.tendered) }));
      const paid = pays.reduce((a, p) => a + p.amount, 0n);
      if (paid !== s.due) throw problem(400, 'POS-006', `Pay ${fromBaisa(s.due)} to collect.`);
      let sale: { id: string; number: string } | null = null;
      if (gross > 0n) {
        const tenders = [...pays, ...(s.applied > 0n ? [{ method: 'deposit', amount: s.applied } as unknown as Tender] : [])];
        sale = h.invoice(j.customerId, ch, tenders);
        r.invoices.push({ id: sale.id, number: sale.number, lines: ch });
        r.deposits.push({ id: id(), number: h.next('deposit', 'DEP'), kind: 'applied', amount: n(s.applied), tax: n(s.appliedTax), method: null, reason: null, saleNumber: sale.number, createdAt: now(), createdBy: nameOf(u) });
        if (s.applied > 0n) h.drawer(h.shift().id, 'repair_applied', 'deposit', s.applied);
      }
      let refundNumber: string | null = null;
      if (s.excess > 0n) {
        if (d.excessTo !== 'store_credit') h.drawer(h.shift().id, 'repair_refund', 'cash', s.excess);
        refundNumber = h.next('deposit', 'DEP');
        r.deposits.push({ id: id(), number: refundNumber, kind: 'refund', amount: n(s.excess), tax: n(s.excessTax), method: d.excessTo ?? 'cash', reason: 'Excess at collection', saleNumber: null, createdAt: now(), createdBy: nameOf(u) });
      }
      const w = wip(r);
      const outside = r.work.filter((x) => x.kind === 'external').reduce((a, x) => a + b(x.cost ?? 0), 0n);
      if (w !== 0n && outside !== 0n && j.outcome === 'repaired' && !j.warrantyOfId) {
        r.clearing.push({ kind: 'cost_of_repair', amount: n(w - outside), reason: null, createdAt: now() });
        r.clearing.push({ kind: 'outside_work', amount: n(outside), reason: null, createdAt: now() });
      } else if (w !== 0n) r.clearing.push({ kind: j.warrantyOfId ? 'warranty' : j.outcome === 'repaired' ? 'cost_of_repair' : 'written_off', amount: n(w), reason: null, createdAt: now() });
      const until = j.outcome === 'repaired' && !j.warrantyOfId ? new Date(Date.now() + j.warrantyDays * 864e5).toISOString().slice(0, 10) : null;
      r.history.push({ from: 'ready', to: 'delivered', note: d.note ?? null, at: now(), userName: nameOf(u) });
      Object.assign(j, { status: 'delivered', next: [], deliveredAt: now(), deliveredByName: nameOf(u), collectedBy: d.collectedBy.trim(), collectionCheck: d.check, deliverySaleId: sale?.id ?? null,
        deliverySaleNumber: sale?.number ?? null, warrantyUntil: until, passcodeHeld: false, passcodePurgedAt: r.passcode ? now() : null, version: j.version + 1 });
      r.passcode = null;
      comm.generate(commJob(r), u);
      const change = pays.reduce((a, p) => a + (p.tendered != null ? p.tendered - p.amount : 0n), 0n);
      return delay({ id: j.id, number: j.number, saleId: sale?.id ?? null, saleNumber: sale?.number ?? null, gross: n(gross), applied: n(s.applied), due: n(s.due), excess: n(s.excess),
        refundId: null, refundNumber, change: n(change), cashRounding: 0, warrantyUntil: until });
    },
    async repairDocument() { return delay(new Blob(['%PDF-1.7 mock'], { type: 'application/pdf' })); },
    async customerDevices(cid) {
      h.need('REPAIR.VIEW');
      const mine = [...rows.values()].filter((r) => r.job.customerId === cid);
      return delay([...new Map(mine.map((r) => [r.job.customerDeviceId, { id: r.job.customerDeviceId, imei1: r.job.imei1, imei2: r.job.imei2, serial: r.job.serial, brand: r.job.brand, model: r.job.model,
        colour: r.job.colour, deviceUnitId: null, jobs: mine.filter((x) => x.job.customerDeviceId === r.job.customerDeviceId).length,
        openJob: mine.find((x) => x.job.customerDeviceId === r.job.customerDeviceId && x.job.status !== 'delivered')?.job.number ?? null }])).values()]);
    },
    async repairSetup() {
      h.need('REPAIR.VIEW');
      return delay(structuredClone({ settings, categories, faultCodes, services: SERVICES, accessories: ['sim_tray', 'sim_card', 'memory_card', 'case', 'charger', 'box'], conditionChecks: ['screen', 'body', 'buttons', 'cameras', 'liquid'] }));
    },
    async updateRepairSettings(s) {
      h.need('REPAIR.SETTINGS');
      if (s.version !== settings.version) throw problem(409, 'CON-001', 'Someone else changed the settings. Reload and try again.');
      if (s.depositVatPoint !== settings.depositVatPoint) h.need('TAX.MANAGE');
      settings = { ...s, version: settings.version + 1 };
      return delay(undefined);
    },
    async createRepairCategory(c) {
      h.need('REPAIR.SETTINGS');
      if (categories.some((x) => x.code === c.code.toUpperCase())) throw problem(409, 'VAL-002', 'That code is used already.');
      const nid = id(); categories.push({ id: nid, ...c, code: c.code.toUpperCase(), sort: c.sort ?? categories.length + 1, status: c.status ?? 'active', version: 1 }); return delay({ id: nid });
    },
    async updateRepairCategory(cid, c) { h.need('REPAIR.SETTINGS'); const x = categories.find((y) => y.id === cid)!; Object.assign(x, { ...c, version: x.version + 1 }); return delay(undefined); },
    async createFaultCode(c) {
      h.need('REPAIR.SETTINGS');
      const nid = id(); faultCodes.push({ id: nid, ...c, code: c.code.toUpperCase(), sort: c.sort ?? faultCodes.length + 1, status: c.status ?? 'active', version: 1 }); return delay({ id: nid });
    },
    async updateFaultCode(fid, c) { h.need('REPAIR.SETTINGS'); const x = faultCodes.find((y) => y.id === fid)!; Object.assign(x, { ...c, version: x.version + 1 }); return delay(undefined); },
    async whatsAppSettings() {
      h.need('NOTIF.SETTINGS');
      return delay({ gateway: true, ...wa, webhookSeenAt: null, webhookUrl: null,
        webAddress: { host: null, active: false, offReason: null, tunnel: 'off', error: null, since: null, listening: true, port: 8791, problem: null },
        counts: [...new Set(outbox.map((o) => o.status))].map((status) => ({ status, n: outbox.filter((o) => o.status === status).length })), sentToday: outbox.length, relay: { lastOk: now(), error: null } });
    },
    async updateWhatsAppSettings(s) {
      h.need('NOTIF.SETTINGS');
      if (s.version !== wa.version) throw problem(409, 'CON-001', 'Someone else changed the settings. Reload and try again.');
      if (s.enabled && s.provider === 'meta' && (!s.phoneNumberId || (!wa.hasToken && !s.accessToken) || (!wa.hasSecret && !s.appSecret)))
        throw problem(409, 'WA-001', 'To switch Meta on, give the phone number ID, the access token and the app secret.');
      wa = { ...wa, enabled: s.enabled, provider: s.provider, phoneNumberId: s.phoneNumberId, wabaId: s.wabaId, displayPhone: s.displayPhone, dailyCap: s.dailyCap, staleHours: s.staleHours,
        hasToken: wa.hasToken || !!s.accessToken, hasSecret: wa.hasSecret || !!s.appSecret, version: wa.version + 1 };
      return delay(undefined);
    },
    async connectWhatsApp() {
      h.need('NOTIF.SETTINGS');
      if (!wa.phoneNumberId || !wa.hasToken) throw problem(409, 'WA-001', 'Save the Meta phone number ID and access token first.');
      return delay({ steps: [{ step: 'token', ok: false, detail: 'The design-review server does not reach Meta.' }], display: null });
    },
    async whatsAppTemplates() { h.need('NOTIF.SETTINGS'); return delay(structuredClone(templates)); },
    async updateWhatsAppTemplate(tid, t) {
      h.need('NOTIF.SETTINGS');
      const x = templates.find((y) => y.id === tid)!;
      if (t.version !== x.version) throw problem(409, 'CON-001', 'Someone else changed this template. Reload and try again.');
      Object.assign(x, { metaName: t.metaName, metaLang: t.metaLang, body: t.body, buttons: t.buttons, version: x.version + 1 });
      return delay({ id: tid });
    },
    async testChat(phone) {
      h.need('NOTIF.SETTINGS');
      const phones = [...new Set([...outbox.map((o) => o.phone), ...inbox.map((m) => m.phone)])].map((p) => ({ phone: p, last: [...outbox.filter((o) => o.phone === p).map((o) => o.at), ...inbox.filter((m) => m.phone === p).map((m) => m.at)].sort().at(-1)! }));
      const messages: TestChat['messages'] = !phone ? [] : [
        ...outbox.filter((o) => o.phone === phone).map((o) => ({ id: o.id, direction: 'out' as const, wamid: `wamid.${o.id.slice(0, 8)}`, body: render(o), buttons: o.buttons.length ? o.buttons : null, at: o.at, status: o.status })),
        ...inbox.filter((m) => m.phone === phone).map((m) => ({ id: m.id, direction: 'in' as const, wamid: null, body: m.text ?? '', buttons: null, at: m.at, status: null })),
      ].sort((x, y) => x.at.localeCompare(y.at));
      return delay({ phones, messages });
    },
    async testChatAction(a) {
      h.need('NOTIF.SETTINGS');
      if (wa.provider !== 'test') throw problem(409, 'WA-001', 'The test chat works while the provider is the test chat.');
      const cust = h.customers().find((c) => c.phoneE164 === a.phone) ?? null;
      const last = [...outbox].reverse().find((o) => o.phone === a.phone && (a.action !== 'button' || o.buttons.length > 0));
      if (['delivered', 'read', 'failed'].includes(a.action)) { if (last) { last.status = a.action; last.statusAt = now(); } }
      else if (a.action === 'button') {
        if (!last) throw problem(409, 'WA-001', 'There is no message with buttons to tap.');
        inbox.push({ id: id(), kind: 'button', text: last.buttons[a.button ?? 0] ?? '', handled: null, at: now(), customerId: cust?.id ?? null, phone: a.phone });
        const r = [...rows.values()].find((x) => x.estimates.some((e) => e.id === last.estimateId));
        const e = r && current(r);
        if ((a.button ?? 0) === 0 && r && e && e.id === last.estimateId && e.status === 'sent') approve(r, e, 'whatsapp', r.job.customerName, `wamid.${last.id.slice(0, 8)}`, null);
      } else {
        const text = a.action === 'stop' ? 'STOP' : a.action === 'start' ? 'START' : (a.text ?? '').trim();
        inbox.push({ id: id(), kind: a.action, text, handled: null, at: now(), customerId: cust?.id ?? null, phone: a.phone });
        if (a.action === 'stop') { optedOut.add(a.phone); rows.forEach((r) => { if (r.job.customerPhone === a.phone) r.job.customerOptIn = false; }); }
        if (a.action === 'start') optedOut.delete(a.phone);
      }
      return delay({ ok: true });
    },
    // ---------------------------------------------------------------- Phase 9
    async fitSerialPart(jid, p) {
      const u = h.need('REPAIR.WORK');
      const r = rowOf(jid);
      if (!p.serial.trim()) throw problem(400, 'VAL-001', 'Scan the serial number of the part.');
      const it = h.item(p.variantId);
      if (!it) throw problem(400, 'VAL-001', 'Unknown part.');
      if ([...rows.values()].some((x) => x.work.some((w) => w.serial === p.serial.trim().toUpperCase() && (w.qtyLeft ?? 0) > 0)))
        throw problem(409, 'DEV-004', `That ${it.descriptionEn} is fitted, not in stock here.`);
      const cost = -h.partMove(p.variantId, -1000n);
      r.work.push({ id: id(), kind: 'part', variantId: p.variantId, sku: it.sku, nameEn: it.descriptionEn, nameAr: it.descriptionAr, qty: 1, cost: n(cost), returnOfId: null, minutes: null,
        technicianName: null, note: p.note ?? null, createdAt: now(), createdBy: nameOf(u), qtyLeft: 1, costLeft: n(cost), writtenOff: false, serial: p.serial.trim().toUpperCase() });
      return delay(detail(r));
    },
    async addOutsideWork(jid, w) {
      const u = h.need('REPAIR.WORK');
      const r = rowOf(jid);
      if (!w.vendor.trim()) throw problem(400, 'VAL-001', 'Name who did the work.');
      if (!(w.amount > 0)) throw problem(400, 'VAL-001', 'The amount is above zero, in baisa.');
      if (!['under_diagnosis', 'waiting_parts', 'in_progress', 'testing'].includes(r.job.status)) throw problem(409, 'REP-002', `Outside work is recorded while the job is diagnosed or repaired; it is ${words(r.job.status)}.`);
      r.work.push({ id: id(), kind: 'external', variantId: null, sku: null, nameEn: null, nameAr: null, qty: null, cost: w.amount, returnOfId: null, minutes: null, technicianName: r.job.technicianName,
        note: w.note ?? null, createdAt: now(), createdBy: nameOf(u), qtyLeft: null, costLeft: null, writtenOff: false, vendor: w.vendor.trim(), reference: w.reference ?? null, reversed: false });
      return delay(detail(r));
    },
    async reverseOutsideWork(jid, lineId, reason) {
      const u = h.need('REPAIR.WORK');
      const r = rowOf(jid); const l = r.work.find((x) => x.id === lineId && x.kind === 'external' && (x.cost ?? 0) > 0);
      if (!l) throw problem(404, 'GEN-404', 'That outside work is not on this job.');
      if (l.reversed) throw problem(409, 'REP-002', 'That outside work was already reversed or written off.');
      if (!reason.trim()) throw problem(400, 'VAL-001', 'Say why it is reversed.');
      l.reversed = true;
      r.work.push({ ...l, id: id(), cost: -(l.cost ?? 0), returnOfId: l.id, note: reason.trim(), createdAt: now(), createdBy: nameOf(u), reversed: false });
      return delay(detail(r));
    },
    async partsNeeded() {
      h.need('REPAIR.VIEW');
      const out: PartsNeededRow[] = [];
      for (const r of [...rows.values()].sort((x, y) => x.job.createdAt.localeCompare(y.job.createdAt)))
        for (const p of partsOf(r).filter((x) => x.requested > 0))
          out.push({ jobId: r.job.id, jobNumber: r.job.number, jobStatus: r.job.status, branchCode: r.job.branchCode, branchId: r.job.branchId, variantId: p.variantId, sku: p.sku,
            nameEn: p.nameEn, nameAr: p.nameAr, outstanding: p.requested, requested: p.requested, filled: 0, openSince: p.requestedSince, onHand: Number(fromBaisa(h.partStock?.(p.variantId) ?? 0n)),
            reserved: 0, linkDocType: null, linkDocId: null });
      return delay(out);
    },
    async fillPartsRequests() {
      const u = h.need('REPAIR.WORK');
      let moved = 0;
      for (const r of [...rows.values()].sort((x, y) => x.job.createdAt.localeCompare(y.job.createdAt)))
        if (r.job.status === 'waiting_parts' && short(r) === 0) { move(r, 'in_progress', u, 'Parts arrived'); moved++; }
      return delay({ moved });
    },
    async jobShares(jid) {
      h.need('REPAIR.VIEW');
      return delay(rowOf(jid).shares.map((s) => ({ ...s, technicianName: h.users().find((x) => x.id === s.technicianId)?.displayNameEn ?? '' })));
    },
    async setJobShares(jid, shares) {
      h.need('REPAIR.ASSIGN');
      const r = rowOf(jid);
      if (new Set(shares.map((s) => s.technicianId)).size !== shares.length) throw problem(400, 'COM-003', 'A technician appears twice.');
      if (shares.some((s) => !(s.share > 0 && s.share <= 100)) || !sharesComplete(shares.map((s) => s.share))) throw problem(400, 'COM-003', 'The shares must make 100%.');
      if (comm.settings() && r.job.status === 'delivered' && r.invoices.length > 0) throw problem(409, 'COM-002', 'The job’s commissions were generated; correct the invoice to change who shares them.');
      r.shares = shares.map((s) => ({ ...s }));
      return delay(undefined);
    },
    async correctRepairInvoice(jid, c) {
      const u = h.need('REPAIR.INVOICE.CORRECT');
      const r = rowOf(jid); const j = r.job;
      const done = r.corrections.find((x) => x.id === c.id);
      if (done) return delay({ jobId: jid, creditNoteId: c.id, creditNoteNumber: done.creditNoteNumber, credited: done.credited, correctedId: null, correctedNumber: done.correctedNumber, correctedGross: done.correctedGross });
      if (j.status !== 'delivered') throw problem(409, 'REP-008', 'Only a delivered job’s invoice is corrected; before delivery, change the estimate.');
      if ((c.reason ?? '').trim().length < 3) throw problem(400, 'VAL-001', 'Give the reason for the correction.');
      const inv = standing(r);
      if (!inv) throw problem(409, 'REP-008', `Job ${j.number} has no invoice standing: it was credited already.`);
      const gross = inv.lines.reduce((a, l) => a + b(l.grossAmount), 0n);
      const refunds: Tender[] = c.refunds.map((p) => ({ ...p, amount: b(p.amount), tendered: null }));
      if (refunds.reduce((a, p) => a + p.amount, 0n) !== gross) throw problem(400, 'POS-006', `Refund ${fromBaisa(gross)}: the whole invoice is credited.`);
      const cn = h.creditNote?.(inv.id, c.reason.trim(), refunds) ?? { id: c.id, number: h.next('cn', 'CN') };
      comm.reverseInvoice(jid, inv.number, `Invoice ${inv.number} credited: ${c.reason.trim()}`, u);
      let corrected: { id: string; number: string } | null = null;
      let correctedGross: number | null = null;
      if (c.lines && c.lines.length > 0) {
        const lines = priceLines(c.lines);
        const pays: Tender[] = (c.payments ?? []).map((p) => ({ ...p, amount: b(p.amount), tendered: p.tendered == null ? null : b(p.tendered) }));
        const g = lines.reduce((a, l) => a + b(l.grossAmount), 0n);
        if (pays.reduce((a, p) => a + p.amount, 0n) !== g) throw problem(400, 'POS-006', `Pay ${fromBaisa(g)} for the corrected invoice.`);
        corrected = h.invoice(j.customerId, lines, pays);
        correctedGross = n(g);
        r.invoices.push({ id: corrected.id, number: corrected.number, lines });
      }
      r.corrections.push({ id: c.id, creditedNumber: inv.number, creditNoteNumber: cn.number, credited: n(gross), correctedNumber: corrected?.number ?? null, correctedGross,
        reason: c.reason.trim(), createdAt: now(), createdBy: nameOf(u) });
      if (corrected) comm.generate(commJob(r), u);
      return delay({ jobId: jid, creditNoteId: cn.id, creditNoteNumber: cn.number, credited: n(gross), correctedId: corrected?.id ?? null, correctedNumber: corrected?.number ?? null, correctedGross });
    },
    ...comm.api,
  };
  return { api, seed: () => { seed(); comm.seed(); } };
}
