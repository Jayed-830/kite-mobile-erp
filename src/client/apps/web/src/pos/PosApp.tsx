import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, can, type ApprovalAction, type Customer, type PosItem, type PosUnit, type ShiftFigures, type TenderInput, type TradeIn } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Modal, SelectField, Spinner, TextField, useTheme } from '@kite/ui';
import { classifyCode, fromBaisa, PosError, priceLine, totals, UNLIMITED, type Item, type Priced, type ReceiptModel } from '@kite/pos';
import { ErrorBox, useApi, useAuth, workBranchId } from '../core';
import { fmt, fmtB, qty } from '../money';
import { FiguresView } from '../pages/Sales';
import { ApprovalDialog, CashDialog, CloseShiftDialog, CreditPaymentDialog, CustomerDialog, DenominationCount, PayDialog, ReturnDialog } from './dialogs';
import { desktop, isEmergencyMode, openDrawer, printReceipt, printRoute } from './printing';
import { ReceiptView } from './ReceiptView';
import { ConditionBadge, Ident } from '../devices/bits';
import { loadTerminal, newDeviceKey, saveTerminal, type StoredTerminal } from './terminalStore';
import { TradeInWizard } from './TradeInWizard';
import { RepairCounter } from './RepairCounter';

/** The touch POS (10.1-10.5): this device as a terminal, sign-in, shift, selling, payments, receipts, returns, cash and close. */
export function PosApp() {
  const { stage, me } = useAuth();
  const emergency = isEmergencyMode();
  const [terminal, setTerminal] = useState<StoredTerminal | null>(() => (emergency ? null : loadTerminal()));
  const snap = useQuery({ queryKey: ['emergencyTerminal'], queryFn: async () => ((await window.kiteDesktop!.emergency!.snapshot()) as { terminal: { id: string; code: string; branchId: string } }).terminal, enabled: emergency });
  const { t } = useI18n();
  if (stage === 'loading' || (emergency && !snap.data)) return <div className="center-page"><Spinner label={t('app.loading')} /></div>;
  if (emergency && !terminal) { const x = snap.data!; setTerminal({ terminalId: x.id, deviceKey: '', code: x.code, branchId: x.branchId, printerId: null }); return null; }
  if (!terminal) return <RegisterTerminal onRegistered={(x) => { saveTerminal(x); setTerminal(x); }} />;
  if (stage !== 'signedIn' || !me) return <TerminalSignIn terminal={terminal} onForget={() => { saveTerminal(null); setTerminal(null); }} />;
  return <Till terminal={terminal} onPrinter={(printerId) => { const x = { ...terminal, printerId }; saveTerminal(x); setTerminal(x); }} />;
}

function PosFrame({ children, title }: { children: React.ReactNode; title: string }) {
  const { t, lang, setLang } = useI18n();
  return (
    <div className="center-page pos-auth">
      <div className="auth-card stack">
        <div className="row"><div className="brand-mark" aria-hidden="true">K</div><div><strong>KITE POS</strong><small className="muted"> · {t('app.by')}</small></div><span className="spacer" />
          <Button size="sm" variant="ghost" onClick={() => setLang(lang === 'en' ? 'ar' : 'en')}>{lang === 'en' ? 'العربية' : 'English'}</Button></div>
        <h1>{title}</h1>
        {children}
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ register this device
function RegisterTerminal({ onRegistered }: { onRegistered: (t: StoredTerminal) => void }) {
  const api = useApi();
  const { me, stage, signIn, signOut } = useAuth();
  const { t, pick } = useI18n();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [form, setForm] = useState({ code: '', name: '', branchId: '' });
  const [pending, setPending] = useState<StoredTerminal | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const branches = useQuery({ queryKey: ['branches'], queryFn: () => api.branches(), enabled: stage === 'signedIn' });
  useEffect(() => { if (branches.data?.[0] && !form.branchId) setForm((f) => ({ ...f, branchId: workBranchId(me) || branches.data[0]!.id })); }, [branches.data, form.branchId, me]);

  if (pending) {
    return (
      <PosFrame title={t('pos.waitingApproval')}>
        <p>{t('pos.waitingApprovalHelp', { code: pending.code })}</p>
        <Button variant="primary" onClick={() => onRegistered(pending)}>{t('pos.approvedContinue')}</Button>
      </PosFrame>
    );
  }
  if (stage !== 'signedIn') {
    return (
      <PosFrame title={t('pos.registerTitle')}>
        <p className="muted">{t('pos.registerIntro')}</p>
        <TextField label={t('login.username')} value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" dir="ltr" />
        <TextField label={t('login.password')} type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" dir="ltr" />
        <ErrorBox error={error} />
        <Button variant="primary" busy={busy} onClick={async () => { setBusy(true); setError(null); try { await signIn(username, password); } catch (e) { setError(e); } finally { setBusy(false); } }}>{t('login.submit')}</Button>
      </PosFrame>
    );
  }
  return (
    <PosFrame title={t('pos.registerTitle')}>
      <p className="muted">{t('pos.registerHelp')}</p>
      <SelectField label={t('users.branch')} value={form.branchId} onChange={(e) => setForm({ ...form, branchId: e.target.value })}>
        {branches.data?.map((b) => <option key={b.id} value={b.id}>{pick(b.nameEn, b.nameAr)}</option>)}
      </SelectField>
      <div className="grid-2">
        <TextField label={t('terminals.code')} value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value.toUpperCase().replace(/[^A-Z0-9-]/g, '') })} maxLength={12} dir="ltr" />
        <TextField label={t('terminals.name')} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
      </div>
      <ErrorBox error={error} />
      <Button variant="primary" busy={busy} disabled={!form.branchId || !form.code.trim()} onClick={async () => {
        setBusy(true); setError(null);
        try {
          const deviceKey = newDeviceKey();
          const r = await api.requestTerminal({ branchId: form.branchId, code: form.code, name: form.name || form.code, kind: 'browser', deviceKey });
          const x: StoredTerminal = { terminalId: r.id, deviceKey, code: form.code, branchId: form.branchId, printerId: null };
          // The manager's own session is not a till session: sign out so a cashier signs in at this terminal.
          if (can(me, 'TERMINALS.APPROVE')) { await api.approveTerminal(r.id, false); await signOut(); onRegistered(x); } else { await signOut(); setPending(x); }
        } catch (e) { setError(e); } finally { setBusy(false); }
      }}>{t('pos.register')}</Button>
    </PosFrame>
  );
}

// ------------------------------------------------------------------ sign in at the terminal
function TerminalSignIn({ terminal, onForget }: { terminal: StoredTerminal; onForget: () => void }) {
  const api = useApi();
  const { reload } = useAuth();
  const qc = useQueryClient();
  const { t } = useI18n();
  const [mode, setMode] = useState<'pin' | 'password'>('pin');
  const [username, setUsername] = useState('');
  const [secret, setSecret] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    setBusy(true); setError(null);
    try {
      const cred = { terminalId: terminal.terminalId, deviceKey: terminal.deviceKey };
      if (mode === 'pin') await api.pinLoginAt(username, secret, cred); else await api.loginAt(username, secret, cred);
      qc.clear();
      await reload();
    } catch (e) { setError(e); setSecret(''); } finally { setBusy(false); }
  };
  return (
    <PosFrame title={t('pos.signInTitle', { code: terminal.code })}>
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={mode === 'pin'} className={`tab${mode === 'pin' ? ' active' : ''}`} onClick={() => setMode('pin')}>{t('pos.pin')}</button>
        <button role="tab" aria-selected={mode === 'password'} className={`tab${mode === 'password' ? ' active' : ''}`} onClick={() => setMode('password')}>{t('login.password')}</button>
      </div>
      <TextField label={t('login.username')} value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" dir="ltr" autoFocus />
      <TextField label={mode === 'pin' ? t('pos.pin') : t('login.password')} type="password" inputMode={mode === 'pin' ? 'numeric' : undefined} value={secret}
        onChange={(e) => setSecret(mode === 'pin' ? e.target.value.replace(/\D/g, '') : e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') void submit(); }} dir="ltr" />
      <ErrorBox error={error} />
      <Button variant="primary" busy={busy} onClick={() => void submit()}>{t('login.submit')}</Button>
      {error instanceof ApiError && (error.code === 'POS-017' || error.code === 'AUTH-007') && <Button variant="ghost" onClick={onForget}>{t('pos.forgetTerminal')}</Button>}
    </PosFrame>
  );
}

// ------------------------------------------------------------------ the till
interface CartRow { key: string; item: PosItem; qty: string; discount: string; unitPrice: string | null; unit?: PosUnit | null; damagedApprovalId?: string | null }
type Dialog = null | 'pay' | 'customer' | 'cash' | 'return' | 'close' | 'parked' | 'x' | 'printer' | 'void' | 'tradein' | 'repair' | 'creditPayment' | { pickUnits: PosItem };
interface ApprovalAsk { action: ApprovalAction; retry: (approvalId: string) => void }

const asItem = (i: PosItem): Item => ({ ...i, taxRate: i.taxRate, listPrice: i.listPrice, minPrice: i.minPrice });
/** A device unit as a cart item: its own description (condition, grade), price and minimum (12.1); a unit bought under the
 * profit margin scheme carries its purchase price so its VAT is worked out on the margin (12.3). */
const unitItem = (u: PosUnit, base?: PosItem): PosItem => ({
  variantId: u.variantId, sku: u.sku, productType: u.productType as PosItem['productType'], tracking: u.tracking as PosItem['tracking'], descriptionEn: u.descriptionEn, descriptionAr: u.descriptionAr,
  taxCodeId: u.taxCodeId ?? base?.taxCodeId ?? '', taxKind: u.marginCost != null ? 'margin' : base?.taxKind ?? 'standard', taxRate: u.taxRate, listPrice: u.listPrice, minPrice: u.minPrice,
  warrantyDays: u.warrantyDays, active: true, stock: 1, barcode: null, marginCost: u.marginCost ?? null,
});

function Till({ terminal, onPrinter }: { terminal: StoredTerminal; onPrinter: (printerId: string | null) => void }) {
  const api = useApi();
  const qc = useQueryClient();
  const { me, signOut } = useAuth();
  const { t, lang, setLang, pick } = useI18n();
  const { theme, setTheme } = useTheme();
  const shift = useQuery({ queryKey: ['currentShift'], queryFn: () => api.currentShift() });
  const policy = useQuery({ queryKey: ['posPolicyTerminal'], queryFn: () => api.posPolicy() });
  const quick = useQuery({ queryKey: ['posQuick'], queryFn: () => api.posItems('', undefined, 24) });
  const [cartId, setCartId] = useState<string>(() => crypto.randomUUID());
  const [cart, setCart] = useState<CartRow[]>([]);
  const [customer, setCustomer] = useState<Customer | null>(null);
  const [taxInvoice, setTaxInvoice] = useState(false);
  const [search, setSearch] = useState('');
  const [hits, setHits] = useState<PosItem[] | null>(null);
  const [dialog, setDialog] = useState<Dialog>(null);
  const [approval, setApproval] = useState<ApprovalAsk | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [paying, setPaying] = useState(false);
  const [payError, setPayError] = useState<unknown>(null);
  const [receipt, setReceipt] = useState<{ r: ReceiptModel; cash: boolean } | null>(null);
  const [zReport, setZReport] = useState<{ figures: ShiftFigures; counted: number; variance: number; number: string } | null>(null);
  const [rest, setRest] = useState<TradeIn[]>([]);
  const searchRef = useRef<HTMLInputElement>(null);
  const route = printRoute(terminal.printerId);
  const emergency = isEmergencyMode();

  // Windows app online: keep the emergency snapshot fresh and send what emergency mode recorded (6.4).
  useEffect(() => {
    const em = desktop()?.emergency;
    if (!em || emergency) return;
    let live = true;
    const sync = async () => {
      try {
        const pending = (await em.pending()) as { seq: number; id: string; kind: string; recordedAt: string; monotonicMs: number; payload: unknown }[];
        if (pending.length) {
          const res = await api.uploadEmergency(pending.map((r) => ({ id: r.id, seq: r.seq, kind: r.kind as 'sale', recordedAt: r.recordedAt, monotonicMs: r.monotonicMs, payload: r.payload })));
          await em.markUploaded(res.results.map((x) => ({ seq: x.seq, result: x.result, detail: x.detail })));
          const conflicts = res.results.filter((x) => x.result === 'conflict').length;
          if (live) setNotice(t('pos.emergencyUploaded', { count: String(res.results.length), conflicts: String(conflicts) }));
          void qc.invalidateQueries({ queryKey: ['currentShift'] });
        }
        await em.saveSnapshot(await api.emergencySnapshot());
      } catch { /* not an emergency terminal, or the server is busy: try again later */ }
    };
    void sync();
    const timer = setInterval(() => void sync(), 10 * 60_000);
    return () => { live = false; clearInterval(timer); };
  }, [api, emergency, qc, t]);
  const pricesIncludeTax = me?.business.pricesIncludeTax ?? true;
  const limit = can(me, 'POS.DISCOUNT.ABOVE_LIMIT') ? UNLIMITED : { percent: 5, amount: 2, unlimited: false };

  const priced = useMemo(() => {
    const out: (Priced | PosError)[] = [];
    cart.forEach((r, i) => {
      try { out.push(priceLine(i + 1, { variantId: r.item.variantId, qty: r.qty || '0', discount: r.discount || '0', unitPrice: r.unitPrice, deviceUnitId: r.unit?.unitId ?? null }, asItem(r.item), pricesIncludeTax, limit)); }
      catch (e) { out.push(e instanceof PosError ? e : new PosError('VAL-001', String(e))); }
    });
    return out;
  }, [cart, pricesIncludeTax, limit]);
  const okLines = priced.filter((p): p is Priced => !(p instanceof PosError));
  const sum = totals(okLines);
  // Margin-scheme lines carry their VAT inside the price: the till, like the receipt, shows only standard-rated VAT (12.3).
  const marginLines = okLines.filter((l) => l.item.taxKind === 'margin');
  const shownTax = sum.tax - marginLines.reduce((a, l) => a + l.tax, 0n);
  const tradeInsOn = can(me, 'TRADEIN.CREATE');
  const repairsOn = can(me, 'REPAIR.INTAKE');
  const cartValid = cart.length > 0 && okLines.length === cart.length;

  const add = useCallback((item: PosItem, fromScan = false) => {
    setError(null);
    if (item.tracking !== 'none') { setDialog({ pickUnits: item }); setSearch(''); setHits(null); return; }   // a phone: choose the very device
    setCart((c) => {
      const i = c.findIndex((r) => r.item.variantId === item.variantId && r.unitPrice === null && !r.discount);
      if (i >= 0) return c.map((r, j) => (j === i ? { ...r, qty: String(Number(r.qty) + 1) } : r));
      return [...c, { key: crypto.randomUUID(), item, qty: '1', discount: '', unitPrice: null }];
    });
    if (!fromScan) { setSearch(''); setHits(null); }
    searchRef.current?.focus();
  }, []);

  // A device (11.5): leased to this cart so no other till can sell it, then added as its own line. A damaged one needs a
  // manager's approval before it goes in the cart (12.1).
  const addUnit = useCallback(async (u: PosUnit, damagedApprovalId?: string) => {
    setError(null);
    if (cart.some((r) => r.unit?.unitId === u.unitId)) return;
    if (u.condition === 'damaged' && !damagedApprovalId && !can(me, 'DEVICE.SELL_DAMAGED')) {
      setApproval({ action: 'DEVICE.SELL_DAMAGED', retry: (aid) => { setApproval(null); void addUnit(u, aid); } });
      return;
    }
    try {
      await api.reserveUnit(u.unitId, cartId);
      setCart((c) => [...c, { key: crypto.randomUUID(), item: unitItem(u), qty: '1', discount: '', unitPrice: null, unit: u, damagedApprovalId: damagedApprovalId ?? null }]);
      setDialog(null);
    } catch (e) { setError(e); }
    searchRef.current?.focus();
  }, [api, cart, cartId, me]);
  const releaseUnits = (rows: CartRow[]) => { for (const r of rows) if (r.unit) void api.releaseUnit(r.unit.unitId, cartId).catch(() => undefined); };

  // A scanner types the code and Enter, often faster than the server answers: the box is cleared at once so the next
  // scan starts clean, and each code is looked up on its own. 15 digits are an IMEI (check digit verified here first).
  const scan = async () => {
    const q = search.trim();
    if (!q) return;
    setSearch(''); setHits(null); setError(null);
    const code = classifyCode(q);
    if (code.kind === 'invalid_imei') {
      setError(new ApiError({ status: 400, code: 'DEV-001', title: code.value.length === 15 ? `${code.value} is not a valid IMEI: the check digit is wrong. Scan it again.` : `${code.value} is not an IMEI: an IMEI has 15 digits.` }));
      return;
    }
    try {
      if (code.kind === 'imei') { await addUnit(await api.posUnitByCode(code.value)); return; }
      add(await api.posItemByBarcode(q), true);
    } catch (e) {
      if (e instanceof ApiError && e.code === 'CAT-002' && code.kind === 'serial') {
        try { await addUnit(await api.posUnitByCode(code.value)); return; } catch (e2) { if (!(e2 instanceof ApiError && e2.code === 'DEV-003')) { setError(e2); return; } }
      }
      if (e instanceof ApiError && e.code === 'CAT-002') { setSearch(q); setHits(await api.posItems(q, undefined, 20)); } else setError(e);
    }
  };
  useEffect(() => {
    const q = search.trim();
    if (q.length < 2) { setHits(null); return; }
    const h = setTimeout(() => { api.posItems(q, undefined, 20).then(setHits).catch(() => undefined); }, 250);
    return () => clearTimeout(h);
  }, [search, api]);

  const newSale = () => { setCart([]); setCustomer(null); setTaxInvoice(false); setCartId(crypto.randomUUID()); setPayError(null); setError(null); setTimeout(() => searchRef.current?.focus(), 0); };
  const ask = (action: ApprovalAction, retry: (approvalId: string) => void) => setApproval({ action, retry });

  const complete = async (tenders: TenderInput[], approvalId?: string, creditApprovalId?: string, creditPlan?: { instalments: number; intervalDays: number } | null) => {
    setPaying(true); setPayError(null);
    try {
      const res = await api.completeSale(cartId, {
        lines: cart.map((r) => ({ variantId: r.item.variantId, qty: Number(r.qty), discount: r.discount ? Number(r.discount) : null, unitPrice: r.unitPrice === null ? null : Number(r.unitPrice),
          deviceUnitId: r.unit?.unitId ?? null, damagedApprovalId: r.damagedApprovalId ?? null })),
        payments: tenders, docType: taxInvoice ? 'tax_invoice' : 'simplified_invoice', customerId: customer?.id ?? null, expectedTotal: Number(fromBaisa(sum.gross)), approvalId,
        creditApprovalId: creditApprovalId ?? null, creditPlan: creditPlan ?? null,
      });
      setDialog(null);
      const r = await api.receipt(res.id);
      const cash = tenders.some((x) => x.method === 'cash');
      setReceipt({ r, cash });
      // Trade-in credit that this sale did not use up is settled now, as store credit or cash (12.5).
      const used = tenders.filter((x) => x.method === 'trade_in' && x.tradeInId).map((x) => x.tradeInId!);
      if (used.length) {
        const left = (await Promise.all(used.map((id) => api.tradeIn(id).catch(() => null)))).filter((d) => d && (d.tradeIn.outstanding ?? 0) > 0).map((d) => d!.tradeIn);
        setRest(left);
      }
      const err = await printReceipt(api, r, route, cash);
      if (err) setNotice(t('pos.printFailed', { error: err }));
      void qc.invalidateQueries({ queryKey: ['currentShift'] });
      void qc.invalidateQueries({ queryKey: ['posQuick'] });
    } catch (e) {
      if (e instanceof ApiError && (e.code === 'POS-006' || e.code === 'POS-007')) {
        ask(e.code === 'POS-006' ? 'POS.PRICE.OVERRIDE' : 'POS.DISCOUNT.ABOVE_LIMIT', (aid) => { setApproval(null); void complete(tenders, aid, creditApprovalId, creditPlan); });
      } else if (e instanceof ApiError && e.code === 'CRD-004') {
        // On account past the available credit, or with an invoice overdue: a manager approves up to the override cap (14.3).
        setPayError(e);
        ask('CREDIT.OVERRIDE', (aid) => { setApproval(null); void complete(tenders, approvalId, aid, creditPlan); });
      } else setPayError(e);
    } finally { setPaying(false); }
  };

  const park = async () => {
    try {
      await api.park({ id: cartId, label: customer?.nameEn ?? null, cart: { rows: cart, customer, taxInvoice }, deviceUnitIds: cart.filter((r) => r.unit).map((r) => r.unit!.unitId) });
      newSale(); setNotice(t('pos.parked'));
    }
    catch (e) { setError(e); }
  };

  // F6 opens the trade-in wizard at the till.
  useEffect(() => {
    if (!tradeInsOn) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'F6' && !dialog) { e.preventDefault(); setDialog('tradein'); } };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [tradeInsOn, dialog]);

  if (shift.isLoading || policy.isLoading) return <div className="center-page"><Spinner label={t('app.loading')} /></div>;
  if (zReport) {
    return (
      <PosFrame title={t('pos.shiftClosed', { number: zReport.number })}>
        <FiguresView f={zReport.figures} />
        <div className="fig strong"><span>{t('salesReports.counted')}</span><span className="num">{fmt(zReport.counted)}</span></div>
        <div className="fig strong"><span>{t('salesReports.variance')}</span><span className="num">{fmt(zReport.variance)}</span></div>
        {emergency
          ? <Button variant="primary" onClick={() => void desktop()?.emergency?.end()}>{t('pos.endEmergency')}</Button>
          : <Button variant="primary" onClick={() => void signOut()}>{t('app.signOut')}</Button>}
      </PosFrame>
    );
  }
  if (!shift.data?.shift) return <OpenShift denominations={policy.data?.denominations ?? []} onOpened={() => void qc.invalidateQueries({ queryKey: ['currentShift'] })} />;
  const s = shift.data.shift;

  return (
    <div className="pos">
      <header className="pos-top">
        <div className="brand-mark" aria-hidden="true">K</div>
        <strong>KITE POS</strong>
        <span className="muted">{pick(me!.branches[0]?.nameEn ?? '', me!.branches[0]?.nameAr ?? '')} · <span className="ltr">{terminal.code}</span> · {pick(me!.user.displayNameEn, me!.user.displayNameAr)}</span>
        <Badge tone="success"><span className="ltr">{s.number}</span></Badge>
        {emergency && <Badge tone="danger">{t('pos.emergencyMode')}</Badge>}
        <span className="spacer" />
        {tradeInsOn && !emergency && <Button size="sm" onClick={() => setDialog('tradein')}>{t('tradein.start')}</Button>}
        {repairsOn && !emergency && <Button size="sm" onClick={() => setDialog('repair')}>{t('repair.till.button')}</Button>}
        {can(me, 'CREDIT.RECEIVE') && !emergency && <Button size="sm" onClick={() => setDialog('creditPayment')}>{t('credit.till.payment')}</Button>}
        <Button size="sm" onClick={() => setDialog('parked')}>{t('pos.recall')}</Button>
        <Button size="sm" onClick={() => setDialog('return')}>{t('pos.return')}</Button>
        <Button size="sm" onClick={() => setDialog('cash')}>{t('pos.cashMenu')}</Button>
        {can(me, 'POS.REPORTS.VIEW') && <Button size="sm" onClick={() => setDialog('x')}>{t('pos.xReport')}</Button>}
        <Button size="sm" onClick={() => setDialog('printer')}>{t('pos.printer')}</Button>
        <Button size="sm" onClick={() => setDialog('close')}>{t('pos.closeShift')}</Button>
        <Button size="sm" variant="ghost" onClick={() => setLang(lang === 'en' ? 'ar' : 'en')}>{lang === 'en' ? 'العربية' : 'English'}</Button>
        <Button size="sm" variant="ghost" onClick={() => setTheme(theme === 'light' ? 'dark' : 'light')}>{theme === 'light' ? t('app.dark') : t('app.light')}</Button>
        <Button size="sm" variant="ghost" onClick={() => void signOut()}>{t('app.signOut')}</Button>
      </header>
      <section className="pos-items" aria-label={t('pos.items')}>
        <input ref={searchRef} className="input pos-search" aria-label={t('pos.scan')} placeholder={t('pos.scan')} value={search} autoFocus
          onChange={(e) => setSearch(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); void scan(); } }} />
        {notice && <Alert tone="info">{notice} <button className="link-button" onClick={() => setNotice(null)}>×</button></Alert>}
        <ErrorBox error={error} />
        <div className="tiles">
          {/* Repair labour, extra charges and the diagnosis fee are charged through a repair job (Repairs), not sold from the till. */}
          {(hits ?? quick.data ?? []).filter((i) => !/^REPAIR-/i.test(i.sku)).map((i) => (
            <button key={i.variantId} className="tile" onClick={() => (i.tracking !== 'none' ? setDialog({ pickUnits: i }) : add(i))} disabled={!i.active}>
              <span className="tile-name">{pick(i.descriptionEn, i.descriptionAr)}</span>
              <span className="tile-meta"><span className="ltr">{i.sku}</span>{i.productType !== 'service' && <span className={i.stock <= 0 ? 'neg' : 'muted'}>{t('pos.inStock', { qty: qty(i.stock) })}</span>}</span>
              <span className="tile-price num">{fmt(i.listPrice)}</span>
            </button>
          ))}
          {hits && hits.length === 0 && <p className="muted">{t('pos.noItems')}</p>}
        </div>
      </section>
      <section className="pos-cart" aria-label={t('pos.cart')}>
        <div className="row">
          <Button size="sm" onClick={() => setDialog('customer')}>{customer ? customer.nameEn : t('pos.addCustomer')}</Button>
          {customer && <Button size="sm" variant="ghost" onClick={() => { setCustomer(null); setTaxInvoice(false); }}>×</Button>}
          <label className="check"><input type="checkbox" checked={taxInvoice} onChange={(e) => { setTaxInvoice(e.target.checked); if (e.target.checked && !customer) setDialog('customer'); }} /> {t('pos.taxInvoice')}</label>
        </div>
        <div className="cart-lines">
          {cart.length === 0 && <p className="muted cart-empty">{t('pos.cartEmpty')}</p>}
          {cart.map((r, i) => {
            const p = priced[i]!;
            const bad = p instanceof PosError;
            return (
              <div key={r.key} className={`cart-line${bad ? ' bad' : ''}`}>
                <div className="cart-desc"><strong>{pick(r.item.descriptionEn, r.item.descriptionAr)}</strong><span className="ltr muted">{r.item.sku}</span>
                  {r.unit && <span className="unit-line"><Ident value={r.unit.imei1 ?? r.unit.serial} /> <ConditionBadge condition={r.unit.condition} grade={r.unit.gradeCode} /></span>}
                  {r.unit?.condition === 'damaged' && <Badge tone="warning">{t('devices.till.damaged')}</Badge>}
                  {!bad && (p.needsDiscountApproval || p.needsPriceOverride) && <Badge tone="warning">{t('pos.needsApproval')}</Badge>}
                  {bad && <span className="neg">{p.message}</span>}
                </div>
                {r.unit ? <span className="qty-step num">1</span> : <div className="qty-step">
                  <button aria-label={t('pos.less')} onClick={() => setCart(cart.map((x, j) => j === i ? { ...x, qty: String(Math.max(1, Number(x.qty) - 1)) } : x))}>−</button>
                  <input className="input" aria-label={t('inventory.qty')} value={r.qty} inputMode="decimal" dir="ltr" onChange={(e) => setCart(cart.map((x, j) => j === i ? { ...x, qty: e.target.value } : x))} />
                  <button aria-label={t('pos.more')} onClick={() => setCart(cart.map((x, j) => j === i ? { ...x, qty: String(Number(x.qty) + 1) } : x))}>+</button>
                </div>}
                <input className="input disc" aria-label={t('pos.discount')} placeholder={t('pos.discount')} value={r.discount} inputMode="decimal" dir="ltr" onChange={(e) => setCart(cart.map((x, j) => j === i ? { ...x, discount: e.target.value } : x))} />
                <span className="num line-total">{bad ? '—' : fmtB(p.gross)}</span>
                <button className="link-button" aria-label={t('pos.remove')} onClick={() => { releaseUnits([r]); setCart(cart.filter((_, j) => j !== i)); }}>×</button>
              </div>
            );
          })}
        </div>
        <div className="totals">
          <div className="fig"><span>{t('pos.subtotal')}</span><span className="num">{fmtB(sum.gross - shownTax)}</span></div>
          <div className="fig"><span>{t('pos.vat')}</span><span className="num">{fmtB(shownTax)}</span></div>
          {marginLines.length > 0 && <div className="fig muted"><span>{t('tradein.detail.margin')}</span><span className="num">{fmtB(marginLines.reduce((a, l) => a + l.gross, 0n))}</span></div>}
          {sum.discount > 0n && <div className="fig"><span>{t('pos.discounts')}</span><span className="num">{fmtB(sum.discount)}</span></div>}
          <div className="fig grand"><span>{t('pos.total')}</span><span className="num">{fmtB(sum.gross)}</span></div>
        </div>
        <div className="pos-actions">
          <Button onClick={() => void park()} disabled={cart.length === 0}>{t('pos.park')}</Button>
          <Button onClick={() => { releaseUnits(cart); newSale(); }} disabled={cart.length === 0}>{t('pos.clear')}</Button>
          <Button variant="primary" className="pay-btn" disabled={!cartValid} onClick={() => { setPayError(null); setDialog('pay'); }}>{t('pos.pay')} {fmtB(sum.gross)}</Button>
        </div>
      </section>

      {dialog === 'pay' && policy.data && <PayDialog gross={sum.gross} policy={policy.data} busy={paying} error={payError} onClose={() => setDialog(null)} onPay={(x, plan) => void complete(x, undefined, undefined, plan)}
        customer={customer} cartId={cartId} tradeIns={tradeInsOn && !emergency} creditSales={can(me, 'POS.CREDIT_SALE') && !emergency} />}
      {dialog === 'creditPayment' && <CreditPaymentDialog onClose={() => setDialog(null)} onDone={(m, cash) => { setDialog(null); setNotice(m); if (cash) void openDrawer(api, route); void qc.invalidateQueries({ queryKey: ['currentShift'] }); }} />}
      {dialog === 'repair' && policy.data && <RepairCounter policy={policy.data} onClose={() => setDialog(null)} onApproval={(a, retry) => ask(a, (id) => { setApproval(null); retry(id); })}
        onDone={(message, cash) => { setDialog(null); setNotice(message); if (cash) void openDrawer(api, route); void qc.invalidateQueries({ queryKey: ['currentShift'] }); }} />}
      {dialog === 'tradein' && <TradeInWizard cartId={cartId} cartCustomer={customer} cartHasItems={cart.length > 0} onClose={() => setDialog(null)} ask={ask}
        onAccepted={(d, kind, c) => {
          if (kind === 'sale') setCustomer(c);
          if (kind === 'cash') void openDrawer(api, route);
          void qc.invalidateQueries({ queryKey: ['currentShift'] });
          setNotice(t('tradein.done.title', { number: d.tradeIn.number }));
        }} />}
      {rest.length > 0 && <RestDialog items={rest} ask={ask} onDone={(cash) => { setRest([]); if (cash) void openDrawer(api, route); void qc.invalidateQueries({ queryKey: ['currentShift'] }); }} />}
      {dialog === 'customer' && <CustomerDialog onClose={() => setDialog(null)} onPick={(c) => { setCustomer(c); setDialog(null); }} />}
      {dialog === 'cash' && <CashDialog onClose={() => setDialog(null)} onApproval={(a, retry) => ask(a, (id) => { setApproval(null); retry(id); })}
        onDone={(kind) => { setDialog(null); setNotice(t(`pos.cashDone.${kind}`)); if (kind !== 'drop') void openDrawer(api, route); void qc.invalidateQueries({ queryKey: ['currentShift'] }); }} />}
      {dialog === 'return' && policy.data && <ReturnDialog policy={policy.data} onClose={() => setDialog(null)} onApproval={(a, retry) => ask(a, (id) => { setApproval(null); retry(id); })}
        onDone={async (id) => { setDialog(null); const r = await api.receipt(id); setReceipt({ r, cash: r.payments.some((p) => p.method === 'cash') }); void printReceipt(api, r, route, r.payments.some((p) => p.method === 'cash')); void qc.invalidateQueries({ queryKey: ['currentShift'] }); }} />}
      {dialog === 'close' && policy.data && <CloseShiftDialog policy={policy.data} figures={shift.data.figures} onClose={() => setDialog(null)} onApproval={(a, retry) => ask(a, (id) => { setApproval(null); retry(id); })}
        onClosed={(r) => { setDialog(null); setZReport(r); }} />}
      {dialog === 'parked' && <ParkedDialog onClose={() => setDialog(null)} onRecall={async (c) => {
        setDialog(null); setCartId(c.id); setCart(c.rows); setCustomer(c.customer); setTaxInvoice(c.taxInvoice);
        for (const r of c.rows) if (r.unit) { try { await api.reserveUnit(r.unit.unitId, c.id); } catch (e) { setError(e); } }
      }} />}
      {dialog && typeof dialog === 'object' && <UnitPicker item={dialog.pickUnits} inCart={cart.filter((r) => r.unit).map((r) => r.unit!.unitId)} onClose={() => setDialog(null)} onPick={(u) => void addUnit(u)} />}
      {dialog === 'x' && <XReport id={s.id} onClose={() => setDialog(null)} />}
      {dialog === 'printer' && <PrinterDialog current={terminal.printerId} onClose={() => setDialog(null)} onPick={(p) => { onPrinter(p); setDialog(null); }} />}
      {approval && <ApprovalDialog action={approval.action} onClose={() => setApproval(null)} onApproved={(id) => { const retry = approval.retry; setApproval(null); retry(id); }} />}
      {receipt && (
        <Modal open title={receipt.r.number} onClose={() => { setReceipt(null); newSale(); }}
          footer={<>
            {receipt.r.docType !== 'credit_note' && <VoidButton saleId={receipt.r.saleId} onApproval={ask} onVoided={async (id) => { const r = await api.receipt(id); setReceipt({ r, cash: false }); void qc.invalidateQueries({ queryKey: ['currentShift'] }); }} />}
            <Button onClick={() => void printReceipt(api, receipt.r, route, false)}>{t('pos.printAgain')}</Button>
            <Button variant="primary" onClick={() => { setReceipt(null); newSale(); }}>{t('pos.newSale')}</Button>
          </>}>
          <div className="receipt-frame print-area"><ReceiptView r={receipt.r} /></div>
        </Modal>
      )}
    </div>
  );
}

/** Trade-in value a sale did not use up: settled at once as store credit or in cash, or left for later (12.5). */
function RestDialog({ items, ask, onDone }: { items: TradeIn[]; ask: (a: ApprovalAction, retry: (id: string) => void) => void; onDone: (cash: boolean) => void }) {
  const api = useApi();
  const { t } = useI18n();
  const [i, setI] = useState(0);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [paidCash, setPaidCash] = useState(false);
  const ti = items[i]!;
  const nextOne = (cash: boolean) => { const c = paidCash || cash; setPaidCash(c); if (i + 1 < items.length) setI(i + 1); else onDone(c); };
  const settle = async (kind: 'store_credit' | 'cash', approvalId?: string) => {
    setBusy(true); setError(null);
    try { await api.settleTradeIn(ti.id, { kind, approvalId }); nextOne(kind === 'cash'); }
    catch (e) { if (e instanceof ApiError && e.code === 'AUTH-403') ask(kind === 'cash' ? 'TRADEIN.PAYOUT' : 'TRADEIN.STORE_CREDIT', (id) => void settle(kind, id)); else setError(e); }
    finally { setBusy(false); }
  };
  return (
    <Modal open title={t('tradein.rest.title')} onClose={() => nextOne(false)}
      footer={<><Button onClick={() => nextOne(false)}>{t('tradein.rest.later')}</Button><Button busy={busy} onClick={() => void settle('cash')}>{t('tradein.rest.cash')}</Button>
        <Button variant="primary" busy={busy} onClick={() => void settle('store_credit')}>{t('tradein.rest.storeCredit')}</Button></>}>
      <p>{t('tradein.rest.help', { number: ti.number, amount: fmt(ti.outstanding) })}</p>
      <ErrorBox error={error} />
    </Modal>
  );
}

function VoidButton({ saleId, onApproval, onVoided }: { saleId: string; onApproval: (a: ApprovalAction, retry: (id: string) => void) => void; onVoided: (creditId: string) => void }) {
  const api = useApi();
  const { t } = useI18n();
  const [ask, setAsk] = useState(false);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<unknown>(null);
  const run = async (approvalId?: string) => {
    setError(null);
    try { const r = await api.voidSale(saleId, { id: crypto.randomUUID(), reason, approvalId }); setAsk(false); onVoided(r.id); }
    catch (e) { if (e instanceof ApiError && e.code === 'AUTH-403') onApproval('POS.SALE.VOID', (id) => void run(id)); else setError(e); }
  };
  if (!ask) return <Button variant="danger" onClick={() => setAsk(true)}>{t('pos.void')}</Button>;
  return (
    <span className="row">
      <TextField label={t('approvals.reason')} value={reason} onChange={(e) => setReason(e.target.value)} autoFocus />
      <Button variant="danger" disabled={!reason.trim()} onClick={() => void run()}>{t('pos.confirmVoid')}</Button>
      <ErrorBox error={error} />
    </span>
  );
}

function OpenShift({ denominations, onOpened }: { denominations: number[]; onOpened: () => void }) {
  const api = useApi();
  const { signOut } = useAuth();
  const { t } = useI18n();
  const [count, setCount] = useState<Record<string, number>>({});
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  return (
    <PosFrame title={t('pos.openShift')}>
      <p className="muted">{t('pos.openShiftHelp')}</p>
      <DenominationCount denominations={denominations} value={count} onChange={setCount} />
      <ErrorBox error={error} />
      <div className="row">
        <Button variant="primary" busy={busy} onClick={async () => { setBusy(true); setError(null); try { await api.openShift({ openingCount: count }); onOpened(); } catch (e) { setError(e); } finally { setBusy(false); } }}>{t('pos.openShift')}</Button>
        <Button variant="ghost" onClick={() => void signOut()}>{t('app.signOut')}</Button>
      </div>
    </PosFrame>
  );
}

/** Tapping a phone shows the units of that item in stock here: each with its IMEI, condition, grade and own price. */
function UnitPicker({ item, inCart, onPick, onClose }: { item: PosItem; inCart: string[]; onPick: (u: PosUnit) => void; onClose: () => void }) {
  const api = useApi();
  const { t, pick } = useI18n();
  const units = useQuery({ queryKey: ['posUnits', item.variantId], queryFn: () => api.posUnits(item.variantId) });
  // Scanning the phone in hand is safer than tapping one from the list: the server refuses a phone of another model (DEV-016).
  const [code, setCode] = useState('');
  const [scanError, setScanError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const scan = async () => {
    if (!code.trim()) return;
    setBusy(true); setScanError(null);
    try { onPick(await api.posUnitByCode(classifyCode(code).value || code.trim(), item.variantId)); } catch (e) { setScanError(e); } finally { setBusy(false); setCode(''); }
  };
  return (
    <Modal open title={`${t('devices.till.pickUnit')}: ${pick(item.descriptionEn, item.descriptionAr)}`} onClose={onClose} footer={<Button onClick={onClose}>{t('app.close')}</Button>}>
      <form onSubmit={(e) => { e.preventDefault(); void scan(); }}>
        <TextField label={t('imei.till.scanDevice')} help={t('imei.till.scanOrPick')} value={code} dir="ltr" autoComplete="off" autoFocus disabled={busy} onChange={(e) => setCode(e.target.value)} />
      </form>
      <ErrorBox error={scanError ?? units.error} />
      {units.isLoading && <Spinner label={t('app.loading')} />}
      {units.data?.length === 0 && <p className="muted">{t('devices.till.noUnits')}</p>}
      <div className="picker-list">
        {units.data?.filter((u) => !inCart.includes(u.unitId)).map((u) => (
          <button key={u.unitId} className="picker-item" disabled={u.reservedUntil != null} onClick={() => onPick(u)}>
            <span><Ident value={u.imei1 ?? u.serial} />{u.centralState === 'pending' && <> <Badge tone="warning">{t('imei.till.pending')}</Badge></>}</span>
            <ConditionBadge condition={u.condition} grade={u.gradeCode} /><span className="num">{fmt(u.listPrice)}</span>
          </button>
        ))}
      </div>
    </Modal>
  );
}

function ParkedDialog({ onClose, onRecall }: { onClose: () => void; onRecall: (c: { id: string; rows: CartRow[]; customer: Customer | null; taxInvoice: boolean }) => void | Promise<void> }) {
  const api = useApi();
  const { t, lang } = useI18n();
  const list = useQuery({ queryKey: ['parked'], queryFn: () => api.parked() });
  const [error, setError] = useState<unknown>(null);
  return (
    <Modal open title={t('pos.parkedTitle')} onClose={onClose} footer={<Button onClick={onClose}>{t('app.close')}</Button>}>
      <ErrorBox error={list.error ?? error} />
      {list.data?.length === 0 && <p className="muted">{t('app.none')}</p>}
      <div className="picker-list">
        {list.data?.map((p) => (
          <button key={p.id} className="picker-item" onClick={async () => {
            try { const c = await api.recall(p.id) as { rows: CartRow[]; customer: Customer | null; taxInvoice: boolean }; onRecall({ id: p.id, ...c }); } catch (e) { setError(e); }
          }}>
            <span>{p.label ?? t('pos.noLabel')}</span><span className="muted">{p.parkedBy} · {p.terminalCode}</span>
            <span className="muted">{new Date(p.parkedAt).toLocaleTimeString(lang === 'ar' ? 'ar-OM-u-nu-latn' : 'en-GB')}</span>
          </button>
        ))}
      </div>
    </Modal>
  );
}

function XReport({ id, onClose }: { id: string; onClose: () => void }) {
  const api = useApi();
  const { t } = useI18n();
  const r = useQuery({ queryKey: ['shift', id], queryFn: () => api.shift(id) });
  return (
    <Modal open title={`X · ${r.data?.shift.number ?? ''}`} onClose={onClose} footer={<Button onClick={onClose}>{t('app.close')}</Button>}>
      <ErrorBox error={r.error} />
      {r.data && <FiguresView f={r.data.figures} />}
    </Modal>
  );
}

function PrinterDialog({ current, onPick, onClose }: { current: string | null; onPick: (id: string | null) => void; onClose: () => void }) {
  const api = useApi();
  const { t } = useI18n();
  const printers = useQuery({ queryKey: ['printers'], queryFn: () => api.printers() });
  const [v, setV] = useState(current ?? '');
  const desktopApp = typeof window !== 'undefined' && !!window.kiteDesktop;
  return (
    <Modal open title={t('pos.printer')} onClose={onClose} footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" onClick={() => onPick(v || null)}>{t('app.save')}</Button></>}>
      {desktopApp ? <p>{t('pos.printerDesktop')}</p> : (
        <SelectField label={t('pos.printer')} value={v} onChange={(e) => setV(e.target.value)}>
          <option value="">{t('pos.browserPrint')}</option>
          {printers.data?.filter((p) => p.status === 'active').map((p) => <option key={p.id} value={p.id}>{p.name} ({p.host})</option>)}
        </SelectField>
      )}
      <ErrorBox error={printers.error} />
    </Modal>
  );
}

