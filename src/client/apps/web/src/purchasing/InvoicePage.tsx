import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { can, type InvoiceDetail, type InvoiceLine, type InvoiceLineInput } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { match, toBaisa } from '@kite/pos';
import { Alert, Button, Card, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { businessToday, ErrorBox, formatDate, useApi, useAuth, workBranchId } from '../core';
import { fmt, parseAmount, qty } from '../money';
import { BranchSelect, InvoiceBadge, MatchBadge, newId, SupplierSelect } from './bits';

interface Pick { on: boolean; qty: string; price: string }
interface ExpenseLine { accountId: string; branchId: string; description: string; amount: string }

/** Entering a supplier's invoice (16.2): the goods, landed costs and outside work it covers, matched against receipts and orders as it is typed. */
export function InvoiceEntryPage() {
  const { id } = useParams();
  const api = useApi();
  const nav = useNavigate();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const existing = useQuery({ queryKey: ['purchaseInvoice', id], enabled: !!id, queryFn: () => api.purchaseInvoice(id!) });
  const [supplierId, setSupplierId] = useState('');
  const [branchId, setBranchId] = useState(workBranchId(me));
  const [number, setNumber] = useState('');
  const [date, setDate] = useState(businessToday());
  const [due, setDue] = useState('');
  const [picks, setPicks] = useState<Record<string, Pick>>({});
  const [expenses, setExpenses] = useState<ExpenseLine[]>([]);
  const settings = useQuery({ queryKey: ['purchSettings'], queryFn: () => api.purchSettings() });
  const open = useQuery({ queryKey: ['openForSupplier', supplierId], enabled: !!supplierId, queryFn: () => api.openForSupplier(supplierId) });
  const accounts = useQuery({ queryKey: ['expenseAccounts'], queryFn: () => api.expenseAccounts(), enabled: can(me, 'EXP.VIEW') || can(me, 'PURCH.INVOICE') });
  useEffect(() => {
    const d = existing.data;
    if (!d) return;
    setSupplierId(d.invoice.supplierId); setBranchId(d.invoice.branchId); setNumber(d.invoice.supplierInvoiceNo); setDate(d.invoice.invoiceDate); setDue(d.invoice.dueDate);
    const p: Record<string, Pick> = {};
    for (const l of d.lines) {
      const key = l.receiptLineId ?? l.receiptChargeId ?? l.outsideId;
      if (key) p[key] = { on: true, qty: String(l.qty), price: String(l.unitPrice) };
    }
    setPicks(p);
    setExpenses(d.lines.filter((l) => l.kind === 'expense').map((l) => ({ accountId: l.accountId ?? '', branchId: l.expenseBranchId ?? d.invoice.branchId, description: l.description, amount: String(l.netAmount) })));
  }, [existing.data]);
  const policy = settings.data?.policy;
  const set = (key: string, patch: Partial<Pick>, base: Pick) => setPicks({ ...picks, [key]: { ...base, ...(picks[key] ?? {}), ...patch } });
  const lines: (InvoiceLineInput & { preview: string })[] = useMemo(() => {
    const o = open.data;
    if (!o) return [];
    const out: (InvoiceLineInput & { preview: string })[] = [];
    for (const r of o.receiptLines) {
      const p = picks[r.id];
      const q = parseAmount(p?.qty ?? '');
      const price = parseAmount(p?.price ?? '');
      if (!p?.on || !q || price == null) continue;
      const m = policy ? match(toBaisa(q), toBaisa(price), toBaisa(r.openQty), toBaisa(r.unitPrice), r.orderPrice == null ? null : toBaisa(r.orderPrice), policy.priceTolerancePct, toBaisa(policy.priceToleranceAmount)).status : 'ok';
      out.push({ kind: 'receipt', receiptLineId: r.id, qty: q, unitPrice: price, preview: m });
    }
    for (const c of o.charges) {
      const p = picks[c.id];
      const price = parseAmount(p?.price ?? '');
      if (!p?.on || price == null) continue;
      const m = policy ? match(1000n, toBaisa(price), 1000n, toBaisa(c.openValue), null, policy.priceTolerancePct, toBaisa(policy.priceToleranceAmount)).status : 'ok';
      out.push({ kind: 'charge', receiptChargeId: c.id, qty: 1, unitPrice: price, preview: m });
    }
    for (const w of o.outsideWork) {
      const p = picks[w.outsideId];
      const price = parseAmount(p?.price ?? '');
      if (!p?.on || price == null) continue;
      const m = policy ? match(1000n, toBaisa(price), 1000n, toBaisa(w.openValue), null, policy.priceTolerancePct, toBaisa(policy.priceToleranceAmount)).status : 'ok';
      out.push({ kind: 'outside_work', outsideType: w.outsideType, outsideId: w.outsideId, qty: 1, unitPrice: price, preview: m });
    }
    for (const e of expenses) {
      const a = parseAmount(e.amount);
      if (!e.accountId || !a) continue;
      out.push({ kind: 'expense', accountId: e.accountId, expenseBranchId: e.branchId, description: e.description || null, qty: 1, unitPrice: a, preview: 'none' });
    }
    return out;
  }, [open.data, picks, expenses, policy]);
  const save = useMutation({
    mutationFn: () => {
      const body = { id: id ?? newId(), branchId, supplierId, supplierInvoiceNo: number, invoiceDate: date, dueDate: due || null, lines: lines.map(({ preview: _, ...l }) => l), version: existing.data?.invoice.version };
      return id ? api.updateInvoice(id, body) : api.enterInvoice(body);
    },
    onSuccess: (r) => nav(`/app/purchasing/invoices/${r.invoice.id}`),
  });
  if (id && existing.isLoading) return <Spinner label={t('app.loading')} />;
  const o = open.data;
  const base = (price: number, q?: number): Pick => ({ on: false, qty: q == null ? '1' : String(q), price: String(price) });
  return (
    <div className="stack">
      <PageHeader title={id ? t('purch.editInvoice', { number: existing.data?.invoice.number ?? '' }) : t('purch.enterInvoice')}>
        <Link className="btn" to="/app/purchasing?tab=invoices">{t('app.back')}</Link>
        <Button variant="primary" disabled={!supplierId || !number.trim() || lines.length === 0} busy={save.isPending} onClick={() => save.mutate()}>{t('purch.checkAndSave')}</Button>
      </PageHeader>
      <Card>
        <div className="grid-2">
          {id ? <TextField label={t('purch.supplier')} value={existing.data ? pick(existing.data.invoice.supplierNameEn, existing.data.invoice.supplierNameAr) : ''} readOnly />
            : <SupplierSelect value={supplierId} onChange={(v) => { setSupplierId(v); setPicks({}); }} />}
          <BranchSelect value={branchId} onChange={setBranchId} label={t('purch.bookedAt')} />
          <TextField label={t('purch.supplierInvoiceNo')} value={number} onChange={(e) => setNumber(e.target.value)} dir="ltr" />
          <TextField label={t('purch.invoiceDate')} type="date" value={date} onChange={(e) => setDate(e.target.value)} dir="ltr" />
          <TextField label={t('purch.dueDate')} type="date" value={due} onChange={(e) => setDue(e.target.value)} dir="ltr" help={t('purch.dueHelp')} />
        </div>
        {policy && <p className="muted">{t('purch.toleranceIs', { pct: String(policy.priceTolerancePct), amount: fmt(policy.priceToleranceAmount) })}</p>}
      </Card>
      <ErrorBox error={save.error ?? open.error} />
      {supplierId && !o && <Spinner label={t('app.loading')} />}
      {o && (
        <>
          <Card>
            <h2>{t('purch.goodsReceived')}</h2>
            <DataTable rows={o.receiptLines} rowKey={(r) => r.id} empty={t('purch.nothingReceived')}
              columns={[
                { key: 'x', header: '', render: (r) => <input type="checkbox" aria-label={`${r.receiptNumber} ${r.description}`} checked={picks[r.id]?.on ?? false}
                  onChange={(e) => set(r.id, { on: e.target.checked }, base(r.orderPrice ?? r.unitPrice, r.openQty))} /> },
                { key: 'r', header: t('purch.receipt'), render: (r) => <span className="ltr">{r.receiptNumber} · {r.branchCode}</span> },
                { key: 'd', header: t('purch.item'), render: (r) => r.description },
                { key: 'o', header: t('purch.openQty'), render: (r) => <span className="num">{qty(r.openQty)}</span> },
                { key: 'p', header: t('purch.orderPrice'), render: (r) => <span className="num">{fmt(r.orderPrice ?? r.unitPrice)}</span> },
                { key: 'q', header: t('purch.qty'), render: (r) => picks[r.id]?.on ? <TextField label={`${t('purch.qty')} ${r.receiptNumber}`} value={picks[r.id]!.qty} onChange={(e) => set(r.id, { qty: e.target.value }, base(r.unitPrice, r.openQty))} inputMode="decimal" dir="ltr" /> : null },
                { key: 'u', header: t('purch.invoicePrice'), render: (r) => picks[r.id]?.on ? <TextField label={`${t('purch.invoicePrice')} ${r.receiptNumber}`} value={picks[r.id]!.price} onChange={(e) => set(r.id, { price: e.target.value }, base(r.unitPrice, r.openQty))} inputMode="decimal" dir="ltr" /> : null },
                { key: 'm', header: t('purch.matchCol'), render: (r) => { const l = lines.find((x) => x.receiptLineId === r.id); return l ? <MatchBadge status={l.preview as 'ok'} /> : null; } },
              ]} />
          </Card>
          {(o.charges.length > 0 || o.outsideWork.length > 0) && (
            <Card>
              <h2>{t('purch.costsTitle')}</h2>
              <DataTable rows={[...o.charges.map((c) => ({ key: c.id, label: `${t(`purch.chargeKinds.${c.kind}`)}: ${c.description}`, ref: `${c.receiptNumber} · ${c.branchCode}`, open: c.openValue })),
                ...o.outsideWork.map((w) => ({ key: w.outsideId, label: `${t('purch.outsideWork')}${w.vendor ? `: ${w.vendor}` : ''}`, ref: `${w.reference ?? ''} · ${w.branchCode}`, open: w.openValue }))]}
                rowKey={(r) => r.key} empty={t('app.none')}
                columns={[
                  { key: 'x', header: '', render: (r) => <input type="checkbox" aria-label={r.label} checked={picks[r.key]?.on ?? false} onChange={(e) => set(r.key, { on: e.target.checked }, base(r.open))} /> },
                  { key: 'l', header: t('purch.description'), render: (r) => r.label },
                  { key: 'r', header: t('purch.reference'), render: (r) => <span className="ltr">{r.ref}</span> },
                  { key: 'o', header: t('purch.expectedAmount'), render: (r) => <span className="num">{fmt(r.open)}</span> },
                  { key: 'a', header: t('purch.invoiceAmount'), render: (r) => picks[r.key]?.on ? <TextField label={`${t('purch.invoiceAmount')} ${r.label}`} value={picks[r.key]!.price} onChange={(e) => set(r.key, { price: e.target.value }, base(r.open))} inputMode="decimal" dir="ltr" /> : null },
                  { key: 'm', header: t('purch.matchCol'), render: (r) => { const l = lines.find((x) => x.receiptChargeId === r.key || x.outsideId === r.key); return l ? <MatchBadge status={l.preview as 'ok'} /> : null; } },
                ]} />
            </Card>
          )}
          <Card>
            <div className="row"><h2 className="grow">{t('purch.expenseLines')}</h2>
              <Button size="sm" onClick={() => setExpenses([...expenses, { accountId: '', branchId, description: '', amount: '' }])}>{t('purch.addExpense')}</Button></div>
            <p className="muted">{t('purch.expenseHelp')}</p>
            {expenses.map((e, i) => (
              <div key={i} className="row line-editor">
                <SelectField label={t('purch.account')} value={e.accountId} onChange={(ev) => setExpenses(expenses.map((x, j) => j === i ? { ...x, accountId: ev.target.value } : x))}>
                  <option value="">{t('purch.chooseAccount')}</option>
                  {(accounts.data ?? []).map((a) => <option key={a.id} value={a.id}>{a.code} · {pick(a.nameEn, a.nameAr)}</option>)}
                </SelectField>
                <BranchSelect value={e.branchId} onChange={(v) => setExpenses(expenses.map((x, j) => j === i ? { ...x, branchId: v } : x))} />
                <TextField label={t('purch.description')} value={e.description} onChange={(ev) => setExpenses(expenses.map((x, j) => j === i ? { ...x, description: ev.target.value } : x))} />
                <TextField label={t('purch.amountBeforeVat')} value={e.amount} onChange={(ev) => setExpenses(expenses.map((x, j) => j === i ? { ...x, amount: ev.target.value } : x))} inputMode="decimal" dir="ltr" />
                <Button size="sm" variant="ghost" onClick={() => setExpenses(expenses.filter((_, j) => j !== i))}>{t('pos.remove')}</Button>
              </div>
            ))}
          </Card>
          {lines.some((l) => l.preview === 'price') && <Alert tone="warning">{t('purch.outsideTolerance')}</Alert>}
          {lines.some((l) => l.preview === 'qty') && <Alert tone="error">{t('purch.moreThanOpen')}</Alert>}
          <p className="muted">{formatDate(date, lang)}</p>
        </>
      )}
    </div>
  );
}

/** A supplier invoice as matched: each line against what was received and ordered, the difference split, then posted (with an override outside tolerance). */
export function InvoicePage() {
  const { id = '' } = useParams();
  const api = useApi();
  const qc = useQueryClient();
  const nav = useNavigate();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const [override, setOverride] = useState(false);
  const [reason, setReason] = useState('');
  const q = useQuery({ queryKey: ['purchaseInvoice', id], queryFn: () => api.purchaseInvoice(id) });
  const refresh = () => { setOverride(false); void qc.invalidateQueries({ queryKey: ['purchaseInvoice', id] }); void qc.invalidateQueries({ queryKey: ['purchaseInvoices'] }); };
  const post = useMutation({ mutationFn: (r: string | null) => api.postInvoice(id, r), onSuccess: refresh });
  const cancel = useMutation({ mutationFn: () => api.cancelInvoice(id), onSuccess: refresh });
  if (q.isLoading) return <Spinner label={t('app.loading')} />;
  if (!q.data) return <ErrorBox error={q.error} />;
  const { invoice: h, lines }: InvoiceDetail = q.data;
  const draft = h.status === 'draft' || h.status === 'matched' || h.status === 'needs_approval';
  const mayOverride = can(me, 'PURCH.INVOICE.OVERRIDE');
  return (
    <div className="stack">
      <PageHeader title={t('purch.invoiceTitle', { number: h.number })}>
        <Link className="btn" to="/app/purchasing?tab=invoices">{t('app.back')}</Link>
        {draft && can(me, 'PURCH.INVOICE') && <Button onClick={() => nav(`/app/purchasing/invoices/${id}/edit`)}>{t('purch.edit')}</Button>}
        {h.status === 'matched' && can(me, 'PURCH.INVOICE') && <Button variant="primary" busy={post.isPending} onClick={() => post.mutate(null)}>{t('purch.postInvoice')}</Button>}
        {h.status === 'needs_approval' && mayOverride && <Button variant="primary" onClick={() => setOverride(true)}>{t('purch.postWithOverride')}</Button>}
        {draft && can(me, 'PURCH.INVOICE') && <Button variant="danger" busy={cancel.isPending} onClick={() => cancel.mutate()}>{t('purch.cancelInvoice')}</Button>}
      </PageHeader>
      <div className="row">
        <InvoiceBadge status={h.status} />
        <span>{pick(h.supplierNameEn, h.supplierNameAr)}</span>
        <span className="ltr">{h.supplierInvoiceNo}</span>
        <span className="muted">{formatDate(h.invoiceDate, lang)} · {t('purch.due')} {formatDate(h.dueDate, lang)} · {h.branchCode}</span>
        {h.journalNumber && <Link className="ltr" to={`/app/accounting/journals/${h.journalId}`}>{h.journalNumber}</Link>}
      </div>
      {h.status === 'needs_approval' && <Alert tone="warning">{mayOverride ? t('purch.needsOverride') : t('purch.needsSomeoneElse')}</Alert>}
      {h.overrideReason && <Alert tone="info">{t('purch.overridden', { by: h.overrideBy ?? '', reason: h.overrideReason })}</Alert>}
      <ErrorBox error={post.error ?? cancel.error} />
      <Card>
        <DataTable<InvoiceLine> rows={lines} rowKey={(l) => l.id} empty={t('app.none')}
          columns={[
            { key: 'd', header: t('purch.description'), render: (l) => <span>{l.description} {l.receiptNumber && <span className="ltr muted">{l.receiptNumber}</span>} {l.accountCode && <span className="ltr muted">{l.accountCode}</span>}</span> },
            { key: 'q', header: t('purch.qty'), render: (l) => <span className="num">{qty(l.qty)}</span> },
            { key: 'p', header: t('purch.invoicePrice'), render: (l) => <span className="num">{fmt(l.unitPrice)}</span> },
            { key: 'o', header: t('purch.orderPrice'), render: (l) => <span className="num">{l.kind === 'receipt' ? fmt(l.orderPrice ?? l.receiptPrice) : ''}</span> },
            { key: 'n', header: t('purch.net'), render: (l) => <span className="num">{fmt(l.netAmount)}</span> },
            { key: 'e', header: t('purch.expectedAmount'), render: (l) => <span className="num">{l.kind === 'expense' ? '' : fmt(l.expectedAmount)}</span> },
            { key: 'm', header: t('purch.matchCol'), render: (l) => <MatchBadge status={l.matchStatus} /> },
            { key: 'v', header: t('purch.toStock'), render: (l) => <span className="num">{l.varianceInventory ? fmt(l.varianceInventory) : ''}</span> },
            { key: 'w', header: t('purch.toPpv'), render: (l) => <span className="num">{l.variancePpv ? fmt(l.variancePpv) : ''}</span> },
          ]} />
        <div className="row figures-row">
          <span>{t('purch.net')} <strong className="num">{fmt(h.netTotal)}</strong></span>
          <span>{t('purch.vat')} <strong className="num">{fmt(h.taxTotal)}</strong></span>
          <span>{t('purch.total')} <strong className="num">{fmt(h.grossTotal)}</strong></span>
          <span>{t('purch.variance')} <strong className="num">{fmt(h.varianceTotal)}</strong></span>
          {h.status === 'posted' && <span>{t('purch.paid')} <strong className="num">{fmt(h.paid)}</strong></span>}
        </div>
      </Card>
      {override && (
        <Modal open title={t('purch.postWithOverride')} onClose={() => setOverride(false)}
          footer={<><Button onClick={() => setOverride(false)}>{t('app.cancel')}</Button><Button variant="primary" disabled={!reason.trim()} busy={post.isPending} onClick={() => post.mutate(reason)}>{t('purch.postInvoice')}</Button></>}>
          <div className="stack">
            <p>{t('purch.overrideHelp')}</p>
            <TextField label={t('purch.reason')} value={reason} onChange={(e) => setReason(e.target.value)} />
            <ErrorBox error={post.error} />
          </div>
        </Modal>
      )}
    </div>
  );
}
