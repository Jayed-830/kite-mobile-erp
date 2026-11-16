import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, can, type ApprovalAction, type Customer, type PosItem, type ShiftFigures, type TenderInput } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Modal, SelectField, Spinner, TextField, useTheme } from '@kite/ui';
import { fromBaisa, PosError, priceLine, totals, UNLIMITED, type Item, type Priced, type ReceiptModel } from '@kite/pos';
import { ErrorBox, useApi, useAuth } from '../core';
import { fmt, fmtB, qty } from '../money';
import { FiguresView } from '../pages/Sales';
import { ApprovalDialog, CashDialog, CloseShiftDialog, CustomerDialog, DenominationCount, PayDialog, ReturnDialog } from './dialogs';
import { openDrawer, printReceipt, printRoute } from './printing';
import { ReceiptView } from './ReceiptView';
import { loadTerminal, newDeviceKey, saveTerminal, type StoredTerminal } from './terminalStore';

/** The touch POS (10.1-10.5): this device as a terminal, sign-in, shift, selling, payments, receipts, returns, cash and close. */
export function PosApp() {
  const { stage, me } = useAuth();
  const [terminal, setTerminal] = useState<StoredTerminal | null>(() => loadTerminal());
  const { t } = useI18n();
  if (stage === 'loading') return <div className="center-page"><Spinner label={t('app.loading')} /></div>;
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
  const { me, stage, signIn } = useAuth();
  const { t, pick } = useI18n();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [form, setForm] = useState({ code: '', name: '', branchId: '' });
  const [pending, setPending] = useState<StoredTerminal | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const branches = useQuery({ queryKey: ['branches'], queryFn: () => api.branches(), enabled: stage === 'signedIn' });
  useEffect(() => { if (branches.data?.[0] && !form.branchId) setForm((f) => ({ ...f, branchId: me?.user.defaultBranchId ?? branches.data[0]!.id })); }, [branches.data, form.branchId, me]);

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
  if (pending) {
    return (
      <PosFrame title={t('pos.waitingApproval')}>
        <p>{t('pos.waitingApprovalHelp', { code: pending.code })}</p>
        <Button variant="primary" onClick={() => onRegistered(pending)}>{t('pos.approvedContinue')}</Button>
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
      <Button variant="primary" busy={busy} onClick={async () => {
        setBusy(true); setError(null);
        try {
          const deviceKey = newDeviceKey();
          const r = await api.requestTerminal({ branchId: form.branchId, code: form.code, name: form.name || form.code, kind: 'browser', deviceKey });
          const x: StoredTerminal = { terminalId: r.id, deviceKey, code: form.code, branchId: form.branchId, printerId: null };
          if (can(me, 'TERMINALS.APPROVE')) { await api.approveTerminal(r.id, false); onRegistered(x); } else setPending(x);
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
interface CartRow { key: string; item: PosItem; qty: string; discount: string; unitPrice: string | null }
type Dialog = null | 'pay' | 'customer' | 'cash' | 'return' | 'close' | 'parked' | 'x' | 'printer' | 'void';
interface ApprovalAsk { action: ApprovalAction; retry: (approvalId: string) => void }

const asItem = (i: PosItem): Item => ({ ...i, taxRate: i.taxRate, listPrice: i.listPrice, minPrice: i.minPrice });

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
  const searchRef = useRef<HTMLInputElement>(null);
  const route = printRoute(terminal.printerId);
  const pricesIncludeTax = me?.business.pricesIncludeTax ?? true;
  const limit = can(me, 'POS.DISCOUNT.ABOVE_LIMIT') ? UNLIMITED : { percent: 5, amount: 2, unlimited: false };

  const priced = useMemo(() => {
    const out: (Priced | PosError)[] = [];
    cart.forEach((r, i) => {
      try { out.push(priceLine(i + 1, { variantId: r.item.variantId, qty: r.qty || '0', discount: r.discount || '0', unitPrice: r.unitPrice }, asItem(r.item), pricesIncludeTax, limit)); }
      catch (e) { out.push(e instanceof PosError ? e : new PosError('VAL-001', String(e))); }
    });
    return out;
  }, [cart, pricesIncludeTax, limit]);
  const okLines = priced.filter((p): p is Priced => !(p instanceof PosError));
  const sum = totals(okLines);
  const cartValid = cart.length > 0 && okLines.length === cart.length;

  const add = useCallback((item: PosItem, fromScan = false) => {
    setError(null);
    setCart((c) => {
      const i = c.findIndex((r) => r.item.variantId === item.variantId && r.unitPrice === null && !r.discount);
      if (i >= 0) return c.map((r, j) => (j === i ? { ...r, qty: String(Number(r.qty) + 1) } : r));
      return [...c, { key: crypto.randomUUID(), item, qty: '1', discount: '', unitPrice: null }];
    });
    if (!fromScan) { setSearch(''); setHits(null); }
    searchRef.current?.focus();
  }, []);

  // A scanner types the code and Enter, often faster than the server answers: the box is cleared at once so the next
  // scan starts clean, and each code is looked up on its own.
  const scan = async () => {
    const q = search.trim();
    if (!q) return;
    setSearch(''); setHits(null); setError(null);
    try { add(await api.posItemByBarcode(q), true); }
    catch (e) {
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

  const complete = async (tenders: TenderInput[], approvalId?: string) => {
    setPaying(true); setPayError(null);
    try {
      const res = await api.completeSale(cartId, {
        lines: cart.map((r) => ({ variantId: r.item.variantId, qty: Number(r.qty), discount: r.discount ? Number(r.discount) : null, unitPrice: r.unitPrice === null ? null : Number(r.unitPrice) })),
        payments: tenders, docType: taxInvoice ? 'tax_invoice' : 'simplified_invoice', customerId: customer?.id ?? null, expectedTotal: Number(fromBaisa(sum.gross)), approvalId,
      });
      setDialog(null);
      const r = await api.receipt(res.id);
      const cash = tenders.some((x) => x.method === 'cash');
      setReceipt({ r, cash });
      const err = await printReceipt(api, r, route, cash);
      if (err) setNotice(t('pos.printFailed', { error: err }));
      void qc.invalidateQueries({ queryKey: ['currentShift'] });
      void qc.invalidateQueries({ queryKey: ['posQuick'] });
    } catch (e) {
      if (e instanceof ApiError && (e.code === 'POS-006' || e.code === 'POS-007')) {
        ask(e.code === 'POS-006' ? 'POS.PRICE.OVERRIDE' : 'POS.DISCOUNT.ABOVE_LIMIT', (aid) => { setApproval(null); void complete(tenders, aid); });
      } else setPayError(e);
    } finally { setPaying(false); }
  };

  const park = async () => {
    try { await api.park({ id: cartId, label: customer?.nameEn ?? null, cart: { rows: cart, customer, taxInvoice } }); newSale(); setNotice(t('pos.parked')); }
    catch (e) { setError(e); }
  };

  if (shift.isLoading || policy.isLoading) return <div className="center-page"><Spinner label={t('app.loading')} /></div>;
  if (zReport) {
    return (
      <PosFrame title={t('pos.shiftClosed', { number: zReport.number })}>
        <FiguresView f={zReport.figures} />
        <div className="fig strong"><span>{t('salesReports.counted')}</span><span className="num">{fmt(zReport.counted)}</span></div>
        <div className="fig strong"><span>{t('salesReports.variance')}</span><span className="num">{fmt(zReport.variance)}</span></div>
        <Button variant="primary" onClick={() => void signOut()}>{t('app.signOut')}</Button>
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
        <span className="spacer" />
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
          {(hits ?? quick.data ?? []).map((i) => (
            <button key={i.variantId} className="tile" onClick={() => add(i)} disabled={!i.active || i.tracking !== 'none'}>
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
                  {!bad && (p.needsDiscountApproval || p.needsPriceOverride) && <Badge tone="warning">{t('pos.needsApproval')}</Badge>}
                  {bad && <span className="neg">{p.message}</span>}
                </div>
                <div className="qty-step">
                  <button aria-label={t('pos.less')} onClick={() => setCart(cart.map((x, j) => j === i ? { ...x, qty: String(Math.max(1, Number(x.qty) - 1)) } : x))}>−</button>
                  <input className="input" aria-label={t('inventory.qty')} value={r.qty} inputMode="decimal" dir="ltr" onChange={(e) => setCart(cart.map((x, j) => j === i ? { ...x, qty: e.target.value } : x))} />
                  <button aria-label={t('pos.more')} onClick={() => setCart(cart.map((x, j) => j === i ? { ...x, qty: String(Number(x.qty) + 1) } : x))}>+</button>
                </div>
                <input className="input disc" aria-label={t('pos.discount')} placeholder={t('pos.discount')} value={r.discount} inputMode="decimal" dir="ltr" onChange={(e) => setCart(cart.map((x, j) => j === i ? { ...x, discount: e.target.value } : x))} />
                <span className="num line-total">{bad ? '—' : fmtB(p.gross)}</span>
                <button className="link-button" aria-label={t('pos.remove')} onClick={() => setCart(cart.filter((_, j) => j !== i))}>×</button>
              </div>
            );
          })}
        </div>
        <div className="totals">
          <div className="fig"><span>{t('pos.subtotal')}</span><span className="num">{fmtB(sum.net)}</span></div>
          <div className="fig"><span>{t('pos.vat')}</span><span className="num">{fmtB(sum.tax)}</span></div>
          {sum.discount > 0n && <div className="fig"><span>{t('pos.discounts')}</span><span className="num">{fmtB(sum.discount)}</span></div>}
          <div className="fig grand"><span>{t('pos.total')}</span><span className="num">{fmtB(sum.gross)}</span></div>
        </div>
        <div className="pos-actions">
          <Button onClick={() => void park()} disabled={cart.length === 0}>{t('pos.park')}</Button>
          <Button onClick={newSale} disabled={cart.length === 0}>{t('pos.clear')}</Button>
          <Button variant="primary" className="pay-btn" disabled={!cartValid} onClick={() => { setPayError(null); setDialog('pay'); }}>{t('pos.pay')} {fmtB(sum.gross)}</Button>
        </div>
      </section>

      {dialog === 'pay' && policy.data && <PayDialog gross={sum.gross} policy={policy.data} busy={paying} error={payError} onClose={() => setDialog(null)} onPay={(x) => void complete(x)} />}
      {dialog === 'customer' && <CustomerDialog onClose={() => setDialog(null)} onPick={(c) => { setCustomer(c); setDialog(null); }} />}
      {dialog === 'cash' && <CashDialog onClose={() => setDialog(null)} onApproval={(a, retry) => ask(a, (id) => { setApproval(null); retry(id); })}
        onDone={(kind) => { setDialog(null); setNotice(t(`pos.cashDone.${kind}`)); if (kind !== 'drop') void openDrawer(api, route); void qc.invalidateQueries({ queryKey: ['currentShift'] }); }} />}
      {dialog === 'return' && policy.data && <ReturnDialog policy={policy.data} onClose={() => setDialog(null)} onApproval={(a, retry) => ask(a, (id) => { setApproval(null); retry(id); })}
        onDone={async (id) => { setDialog(null); const r = await api.receipt(id); setReceipt({ r, cash: r.payments.some((p) => p.method === 'cash') }); void printReceipt(api, r, route, r.payments.some((p) => p.method === 'cash')); void qc.invalidateQueries({ queryKey: ['currentShift'] }); }} />}
      {dialog === 'close' && policy.data && <CloseShiftDialog policy={policy.data} figures={shift.data.figures} onClose={() => setDialog(null)} onApproval={(a, retry) => ask(a, (id) => { setApproval(null); retry(id); })}
        onClosed={(r) => { setDialog(null); setZReport(r); }} />}
      {dialog === 'parked' && <ParkedDialog onClose={() => setDialog(null)} onRecall={(c) => { setDialog(null); setCartId(c.id); setCart(c.rows); setCustomer(c.customer); setTaxInvoice(c.taxInvoice); }} />}
      {dialog === 'x' && <XReport id={s.id} onClose={() => setDialog(null)} />}
      {dialog === 'printer' && <PrinterDialog current={terminal.printerId} onClose={() => setDialog(null)} onPick={(p) => { onPrinter(p); setDialog(null); }} />}
      {approval && <ApprovalDialog action={approval.action} onClose={() => setApproval(null)} onApproved={(id) => approval.retry(id)} />}
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

function ParkedDialog({ onClose, onRecall }: { onClose: () => void; onRecall: (c: { id: string; rows: CartRow[]; customer: Customer | null; taxInvoice: boolean }) => void }) {
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

