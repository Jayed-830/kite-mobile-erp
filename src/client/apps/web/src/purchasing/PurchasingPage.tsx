import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import {
  can, type ChargeInput, type DeviceCondition, type OrderDetail, type OrderRow, type PaymentRow, type PayMethod, type PosItem, type ReceiptRow, type ReturnRow,
  type InvoiceRow, type SupplierOpenItem,
} from '@kite/api';
import { useI18n } from '@kite/i18n';
import { fromBaisa, purchaseNet, purchaseTax, toBaisa } from '@kite/pos';
import { Alert, Button, Card, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { businessToday, ErrorBox, formatDate, useApi, useAuth, workBranchId } from '../core';
import { fmt, fmtB, parseAmount, qty } from '../money';
import { ItemPicker } from '../pages/Inventory';
import { ReceiveOptions, toUnitInputs, UnitScanner, type ScannedUnit } from '../devices/bits';
import { BranchSelect, InvoiceBadge, newId, OrderBadge, SupplierSelect } from './bits';

type Tab = 'orders' | 'receipts' | 'invoices' | 'returns' | 'payments';
const TABS: Tab[] = ['orders', 'receipts', 'invoices', 'returns', 'payments'];

/** Purchasing (blueprint 16.2): orders, goods received, supplier invoices, returns and payments. */
export function PurchasingPage() {
  const api = useApi();
  const qc = useQueryClient();
  const nav = useNavigate();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const [params, setParams] = useSearchParams();
  const tab = (TABS.includes(params.get('tab') as Tab) ? params.get('tab') : 'orders') as Tab;
  const supplierId = params.get('supplier') ?? '';
  const [status, setStatus] = useState('');
  const [q, setQ] = useState('');
  const [dialog, setDialog] = useState<'order' | 'receive' | 'return' | 'pay' | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const filter = { supplierId: supplierId || undefined };
  const orders = useQuery({ queryKey: ['orders', status, supplierId, q], enabled: tab === 'orders', queryFn: () => api.orders({ ...filter, status: status || undefined, q: q.trim() || undefined }) });
  const receipts = useQuery({ queryKey: ['receipts', supplierId, q], enabled: tab === 'receipts', queryFn: () => api.receipts({ ...filter, q: q.trim() || undefined }) });
  const invoices = useQuery({ queryKey: ['purchaseInvoices', status, supplierId, q], enabled: tab === 'invoices', queryFn: () => api.purchaseInvoices({ ...filter, status: status || undefined, q: q.trim() || undefined }) });
  const returns = useQuery({ queryKey: ['supplierReturns', supplierId], enabled: tab === 'returns', queryFn: () => api.supplierReturns(filter) });
  const payments = useQuery({ queryKey: ['supplierPayments', supplierId], enabled: tab === 'payments', queryFn: () => api.supplierPayments(filter) });
  const done = (m: string) => {
    setDialog(null);
    setMsg(m);
    for (const k of ['orders', 'receipts', 'purchaseInvoices', 'supplierReturns', 'supplierPayments', 'suppliers']) void qc.invalidateQueries({ queryKey: [k] });
  };
  const go = (k: Tab) => { setStatus(''); setParams((p) => { p.set('tab', k); return p; }); };
  const name = (r: { supplierNameEn: string; supplierNameAr: string }) => pick(r.supplierNameEn, r.supplierNameAr);
  return (
    <div className="stack">
      <PageHeader title={t('purch.title')}>
        {can(me, 'PURCH.ORDER') && <Button variant="primary" onClick={() => setDialog('order')}>{t('purch.newOrder')}</Button>}
        {can(me, 'PURCH.RECEIVE') && <Button onClick={() => setDialog('receive')}>{t('purch.receiveGoods')}</Button>}
        {can(me, 'PURCH.INVOICE') && <Button onClick={() => nav('/app/purchasing/invoices/new')}>{t('purch.enterInvoice')}</Button>}
        {can(me, 'PURCH.RETURN') && <Button onClick={() => setDialog('return')}>{t('purch.returnGoods')}</Button>}
        {can(me, 'PURCH.PAY') && <Button onClick={() => setDialog('pay')}>{t('purch.paySupplier')}</Button>}
      </PageHeader>
      <div className="tabs" role="tablist">
        {TABS.map((k) => <button key={k} role="tab" aria-selected={tab === k} className={`tab${tab === k ? ' active' : ''}`} onClick={() => go(k)}>{t(`purch.tabs.${k}`)}</button>)}
      </div>
      {msg && <Alert tone="success">{msg}</Alert>}
      <div className="row">
        {(tab === 'orders' || tab === 'receipts' || tab === 'invoices') && <TextField label={t('purch.search')} value={q} onChange={(e) => setQ(e.target.value)} />}
        {tab === 'orders' && (
          <SelectField label={t('app.status')} value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">{t('purch.all')}</option>
            <option value="open">{t('purch.openOrders')}</option>
            {(['draft', 'approved', 'sent', 'partly_received', 'received', 'closed', 'cancelled'] as const).map((s) => <option key={s} value={s}>{t(`purch.orderStatus.${s}`)}</option>)}
          </SelectField>
        )}
        {tab === 'invoices' && (
          <SelectField label={t('app.status')} value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">{t('purch.all')}</option>
            <option value="open">{t('purch.notPosted')}</option>
            {(['posted', 'cancelled'] as const).map((s) => <option key={s} value={s}>{t(`purch.invoiceStatus.${s}`)}</option>)}
          </SelectField>
        )}
        <SupplierSelect value={supplierId} onChange={(id) => setParams((p) => { if (id) p.set('supplier', id); else p.delete('supplier'); return p; })} label={t('purch.filterSupplier')} />
      </div>
      <ErrorBox error={orders.error ?? receipts.error ?? invoices.error ?? returns.error ?? payments.error} />
      <Card>
        {tab === 'orders' && (orders.isLoading ? <Spinner label={t('app.loading')} /> : (
          <DataTable<OrderRow> rows={orders.data ?? []} rowKey={(r) => r.id} empty={t('purch.noOrders')}
            columns={[
              { key: 'n', header: t('purch.number'), render: (r) => <Link className="ltr" to={`/app/purchasing/orders/${r.id}`}>{r.number}</Link> },
              { key: 'd', header: t('purch.date'), render: (r) => formatDate(r.orderDate, lang) },
              { key: 's', header: t('purch.supplier'), render: name },
              { key: 'b', header: t('purch.deliverTo'), render: (r) => <span className="ltr">{r.deliverTo ?? r.branchCode}</span> },
              { key: 't', header: t('purch.total'), render: (r) => <span className="num">{fmt(r.grossTotal)}</span> },
              { key: 'st', header: t('app.status'), render: (r) => <OrderBadge status={r.status} /> },
            ]} />
        ))}
        {tab === 'receipts' && (receipts.isLoading ? <Spinner label={t('app.loading')} /> : (
          <DataTable<ReceiptRow> rows={receipts.data ?? []} rowKey={(r) => r.id} empty={t('purch.noReceipts')}
            columns={[
              { key: 'n', header: t('purch.number'), render: (r) => <Link className="ltr" to={`/app/purchasing/receipts/${r.id}`}>{r.number}</Link> },
              { key: 'd', header: t('purch.date'), render: (r) => formatDate(r.receiptDate, lang) },
              { key: 'b', header: t('purch.branch'), render: (r) => <span className="ltr">{r.branchCode}</span> },
              { key: 's', header: t('purch.supplier'), render: name },
              { key: 'o', header: t('purch.order'), render: (r) => <span className="ltr muted">{r.poNumber ?? '—'}</span> },
              { key: 'g', header: t('purch.goodsValue'), render: (r) => <span className="num">{fmt(r.goodsValue)}</span> },
              { key: 'c', header: t('purch.landedCosts'), render: (r) => <span className="num">{r.chargesValue ? fmt(r.chargesValue) : ''}</span> },
              { key: 'v', header: t('purch.notInvoiced'), render: (r) => <span className="num">{r.openValue ? fmt(r.openValue) : '—'}</span> },
            ]} />
        ))}
        {tab === 'invoices' && (invoices.isLoading ? <Spinner label={t('app.loading')} /> : (
          <DataTable<InvoiceRow> rows={invoices.data ?? []} rowKey={(r) => r.id} empty={t('purch.noInvoices')}
            columns={[
              { key: 'n', header: t('purch.number'), render: (r) => <Link className="ltr" to={`/app/purchasing/invoices/${r.id}`}>{r.number}</Link> },
              { key: 'x', header: t('purch.supplierInvoiceNo'), render: (r) => <span className="ltr">{r.supplierInvoiceNo}</span> },
              { key: 'd', header: t('purch.date'), render: (r) => formatDate(r.invoiceDate, lang) },
              { key: 'u', header: t('purch.due'), render: (r) => formatDate(r.dueDate, lang) },
              { key: 's', header: t('purch.supplier'), render: name },
              { key: 't', header: t('purch.total'), render: (r) => <span className="num">{fmt(r.grossTotal)}</span> },
              { key: 'p', header: t('purch.paid'), render: (r) => <span className="num">{r.paid ? fmt(r.paid) : ''}</span> },
              { key: 'v', header: t('purch.variance'), render: (r) => <span className={`num${r.varianceTotal < 0 ? ' neg' : ''}`}>{r.varianceTotal ? fmt(r.varianceTotal) : ''}</span> },
              { key: 'st', header: t('app.status'), render: (r) => <InvoiceBadge status={r.status} /> },
            ]} />
        ))}
        {tab === 'returns' && (returns.isLoading ? <Spinner label={t('app.loading')} /> : (
          <DataTable<ReturnRow> rows={returns.data ?? []} rowKey={(r) => r.id} empty={t('purch.noReturns')}
            columns={[
              { key: 'n', header: t('purch.number'), render: (r) => <Link className="ltr" to={`/app/purchasing/returns/${r.id}`}>{r.number}</Link> },
              { key: 'd', header: t('purch.date'), render: (r) => formatDate(r.returnDate, lang) },
              { key: 'b', header: t('purch.branch'), render: (r) => <span className="ltr">{r.branchCode}</span> },
              { key: 's', header: t('purch.supplier'), render: name },
              { key: 'r', header: t('purch.reason'), render: (r) => r.reason },
              { key: 'v', header: t('purch.debitNote'), render: (r) => <span className="num">{r.taxTotal ? fmt(r.netTotal + r.taxTotal) : t('purch.fromGrni')}</span> },
            ]} />
        ))}
        {tab === 'payments' && (payments.isLoading ? <Spinner label={t('app.loading')} /> : (
          <DataTable<PaymentRow> rows={payments.data ?? []} rowKey={(r) => r.id} empty={t('purch.noPayments')}
            columns={[
              { key: 'n', header: t('purch.number'), render: (r) => <Link className="ltr" to={`/app/purchasing/payments/${r.id}`}>{r.number}</Link> },
              { key: 'd', header: t('purch.date'), render: (r) => formatDate(r.paymentDate, lang) },
              { key: 'b', header: t('purch.branch'), render: (r) => <span className="ltr">{r.branchCode}</span> },
              { key: 's', header: t('purch.supplier'), render: name },
              { key: 'm', header: t('purch.method'), render: (r) => <span>{t(`purch.methods.${r.method}`)} <span className="ltr muted">{r.reference ?? ''}</span></span> },
              { key: 'a', header: t('purch.amount'), render: (r) => <strong className="num">{fmt(r.amount)}</strong> },
            ]} />
        ))}
      </Card>
      {dialog === 'order' && <OrderForm onClose={() => setDialog(null)} onDone={(id) => { done(t('purch.orderSaved')); nav(`/app/purchasing/orders/${id}`); }} />}
      {dialog === 'receive' && <ReceiveForm onClose={() => setDialog(null)} onDone={(n) => done(t('purch.received', { number: n }))} />}
      {dialog === 'return' && <ReturnForm onClose={() => setDialog(null)} onDone={(n) => done(t('purch.returned', { number: n }))} />}
      {dialog === 'pay' && <PaymentForm supplierId={supplierId} onClose={() => setDialog(null)} onDone={(n) => done(t('purch.paidMsg', { number: n }))} />}
    </div>
  );
}

// ------------------------------------------------------------------ orders
interface DraftLine { item: PosItem; qty: string; price: string; deliver: string; last: number | null }

/** A new purchase order, or a draft changed: lines with the last price paid, delivery to any branch. */
export function OrderForm({ order, onClose, onDone }: { order?: OrderDetail; onClose: () => void; onDone: (id: string) => void }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const home = workBranchId(me);
  const [supplierId, setSupplierId] = useState(order?.order.supplierId ?? '');
  const [branchId, setBranchId] = useState(order?.order.branchId ?? home);
  const [expected, setExpected] = useState(order?.order.expectedDate ?? '');
  const [note, setNote] = useState(order?.order.note ?? '');
  const [lines, setLines] = useState<DraftLine[]>(() => (order?.lines ?? []).map((l) => ({
    item: { variantId: l.variantId, sku: l.sku, descriptionEn: l.description, descriptionAr: l.description, tracking: l.tracking, productType: l.productType, taxRate: l.taxRate } as PosItem,
    qty: String(l.qty), price: String(l.unitPrice), deliver: l.deliverBranchId, last: null,
  })));
  const set = (i: number, patch: Partial<DraftLine>) => setLines(lines.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  const add = async (item: PosItem) => {
    if (lines.some((l) => l.item.variantId === item.variantId)) return;
    const h = await api.priceHistory(item.variantId).catch(() => null);
    setLines((ls) => [...ls, { item, qty: '1', price: h?.last ? String(h.last.unitPrice) : '', deliver: branchId, last: h?.last?.unitPrice ?? null }]);
  };
  const totals = useMemo(() => {
    let net = 0n;
    let tax = 0n;
    for (const l of lines) {
      const q = parseAmount(l.qty);
      const p = parseAmount(l.price);
      if (q == null || p == null) continue;
      const n = purchaseNet(toBaisa(q), toBaisa(p));
      net += n;
      tax += purchaseTax(n, l.item.taxRate ?? 0);
    }
    return { net, tax };
  }, [lines]);
  const valid = supplierId && lines.length > 0 && lines.every((l) => parseAmount(l.qty) && parseAmount(l.price) != null);
  const save = useMutation({
    mutationFn: () => {
      const body = {
        id: order?.order.id, branchId, supplierId, expectedDate: expected || null, note: note || null, version: order?.order.version,
        lines: lines.map((l) => ({ variantId: l.item.variantId, qty: parseAmount(l.qty)!, unitPrice: parseAmount(l.price)!, deliverBranchId: l.deliver || branchId })),
      };
      return order ? api.updateOrder(order.order.id, body) : api.createOrder({ ...body, id: newId() });
    },
    onSuccess: (r) => onDone(r.id),
  });
  return (
    <Modal open title={order ? t('purch.editOrder', { number: order.order.number }) : t('purch.newOrder')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={!valid} busy={save.isPending} onClick={() => save.mutate()}>{t('purch.saveDraft')}</Button></>}>
      <div className="stack">
        <div className="grid-2">
          <SupplierSelect value={supplierId} onChange={setSupplierId} />
          <BranchSelect value={branchId} onChange={setBranchId} label={t('purch.orderingBranch')} />
          <TextField label={t('purch.expected')} type="date" value={expected} onChange={(e) => setExpected(e.target.value)} dir="ltr" />
          <TextField label={t('purch.note')} value={note} onChange={(e) => setNote(e.target.value)} />
        </div>
        <ItemPicker label={t('purch.addItem')} onPick={(i) => void add(i)} />
        {lines.map((l, i) => (
          <div key={l.item.variantId} className="row line-editor">
            <strong className="grow">{pick(l.item.descriptionEn, l.item.descriptionAr)} <span className="ltr muted">{l.item.sku}</span>
              {l.last != null && <small className="muted"> · {t('purch.lastPaid', { price: fmt(l.last) })}</small>}</strong>
            <TextField label={t('purch.qty')} value={l.qty} onChange={(e) => set(i, { qty: e.target.value })} inputMode="decimal" dir="ltr" />
            <TextField label={t('purch.unitPrice')} value={l.price} onChange={(e) => set(i, { price: e.target.value })} inputMode="decimal" dir="ltr" />
            <BranchSelect value={l.deliver} onChange={(v) => set(i, { deliver: v })} label={t('purch.deliverTo')} />
            <Button size="sm" variant="ghost" onClick={() => setLines(lines.filter((_, j) => j !== i))}>{t('pos.remove')}</Button>
          </div>
        ))}
        <div className="row figures-row">
          <span>{t('purch.net')} <strong className="num">{fmtB(totals.net)}</strong></span>
          <span>{t('purch.vat')} <strong className="num">{fmtB(totals.tax)}</strong></span>
          <span>{t('purch.total')} <strong className="num">{fmtB(totals.net + totals.tax)}</strong></span>
        </div>
        <ErrorBox error={save.error} />
      </div>
    </Modal>
  );
}

// ------------------------------------------------------------------ goods receipts
interface RecvLine { key: string; item: PosItem; poLineId: string | null; open: number | null; qty: string; price: string; units: ScannedUnit[]; condition: DeviceCondition; status: 'in_stock' | 'under_inspection' }

/** Goods received (16.2, 16.3): against an order (its price, never more than is still to come) or directly; one scan per phone; landed costs. */
export function ReceiveForm({ order, onClose, onDone }: { order?: OrderDetail; onClose: () => void; onDone: (number: string) => void }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const home = workBranchId(me);
  const [branchId, setBranchId] = useState(() => order?.lines.find((l) => l.received < l.qty)?.deliverBranchId ?? home);
  const [supplierId, setSupplierId] = useState(order?.order.supplierId ?? '');
  const [deliveryNote, setDeliveryNote] = useState('');
  const [date, setDate] = useState(businessToday());
  const [charges, setCharges] = useState<(ChargeInput & { amountText: string })[]>([]);
  const fromOrder = (b: string): RecvLine[] => (order?.lines ?? []).filter((l) => l.deliverBranchId === b && l.received < l.qty).map((l) => ({
    key: l.id, poLineId: l.id, open: Math.round((l.qty - l.received) * 1000) / 1000, qty: '', price: String(l.unitPrice), units: [], condition: 'brand_new', status: 'in_stock',
    item: { variantId: l.variantId, sku: l.sku, descriptionEn: l.description, descriptionAr: l.description, tracking: l.tracking, productType: l.productType } as PosItem,
  }));
  const [lines, setLines] = useState<RecvLine[]>(() => fromOrder(branchId));
  const set = (i: number, patch: Partial<RecvLine>) => setLines(lines.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  const tracked = (l: RecvLine) => l.item.tracking !== 'none';
  const lineQty = (l: RecvLine) => (tracked(l) ? l.units.length : parseAmount(l.qty) ?? 0);
  const take = lines.filter((l) => lineQty(l) > 0);
  const valid = supplierId && take.length > 0 && take.every((l) => parseAmount(l.price) != null && (l.open == null || lineQty(l) <= l.open))
    && !take.some((l) => l.units.some((u) => u.problem)) && charges.every((c) => parseAmount(c.amountText) && c.description.trim());
  const save = useMutation({
    mutationFn: () => api.receive({
      id: newId(), branchId, supplierId, poId: order?.order.id ?? null, receiptDate: date, deliveryNote: deliveryNote || null,
      lines: take.map((l) => ({
        qty: lineQty(l), poLineId: l.poLineId, variantId: l.poLineId ? null : l.item.variantId, unitPrice: l.poLineId ? null : parseAmount(l.price),
        units: tracked(l) ? toUnitInputs(l.units, l.condition, l.status, null).map((u) => ({ imei1: u.imei1, serial: u.serial, condition: u.condition, entry: u.entry })) : null,
      })),
      charges: charges.map((c) => ({ kind: c.kind, description: c.description, amount: parseAmount(c.amountText)!, basis: c.basis, supplierId: c.supplierId || null })),
    }),
    onSuccess: (r) => onDone(r.receipt.number),
  });
  return (
    <Modal open title={order ? t('purch.receiveAgainst', { number: order.order.number }) : t('purch.receiveGoods')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={!valid} busy={save.isPending} onClick={() => save.mutate()}>{t('purch.post')}</Button></>}>
      <div className="stack">
        <div className="grid-2">
          {order ? <TextField label={t('purch.supplier')} value={pick(order.order.supplierNameEn, order.order.supplierNameAr)} readOnly /> : <SupplierSelect value={supplierId} onChange={setSupplierId} />}
          <BranchSelect value={branchId} onChange={(b) => { setBranchId(b); if (order) setLines(fromOrder(b)); }} label={t('purch.receivingBranch')} />
          <TextField label={t('purch.deliveryNote')} value={deliveryNote} onChange={(e) => setDeliveryNote(e.target.value)} dir="auto" />
          <TextField label={t('purch.date')} type="date" value={date} onChange={(e) => setDate(e.target.value)} dir="ltr" />
        </div>
        {order && lines.length === 0 && <Alert tone="info">{t('purch.nothingToCome')}</Alert>}
        {!order && <ItemPicker label={t('purch.addItem')} onPick={(item) => setLines((ls) => ls.some((x) => x.item.variantId === item.variantId) ? ls
          : [...ls, { key: item.variantId, item, poLineId: null, open: null, qty: '', price: '', units: [], condition: 'brand_new', status: 'in_stock' }])} />}
        {lines.map((l, i) => (
          <div key={l.key} className="card stack line-editor">
            <div className="row">
              <strong className="grow">{pick(l.item.descriptionEn, l.item.descriptionAr)} <span className="ltr muted">{l.item.sku}</span>
                {l.open != null && <small className="muted"> · {t('purch.toCome', { qty: qty(l.open) })}</small>}</strong>
              {!tracked(l) && <TextField label={t('purch.qty')} value={l.qty} onChange={(e) => set(i, { qty: e.target.value })} inputMode="decimal" dir="ltr"
                error={l.open != null && (parseAmount(l.qty) ?? 0) > l.open ? t('purch.moreThanOrdered') : undefined} />}
              <TextField label={t('purch.unitPrice')} value={l.price} onChange={(e) => set(i, { price: e.target.value })} inputMode="decimal" dir="ltr" readOnly={!!l.poLineId} />
              {!order && <Button size="sm" variant="ghost" onClick={() => setLines(lines.filter((_, j) => j !== i))}>{t('pos.remove')}</Button>}
            </div>
            {tracked(l) && (
              <>
                <ReceiveOptions condition={l.condition} status={l.status} onChange={(condition, status) => set(i, { condition, status })} />
                <UnitScanner units={l.units} tracking={l.item.tracking} variantId={l.item.variantId} onChange={(units) => set(i, { units })} />
                {l.open != null && l.units.length > l.open && <Alert tone="error">{t('purch.moreThanOrdered')}</Alert>}
              </>
            )}
          </div>
        ))}
        <div className="stack">
          <div className="row"><strong className="grow">{t('purch.landedCosts')}</strong>
            <Button size="sm" onClick={() => setCharges([...charges, { kind: 'freight', description: '', amount: 0, amountText: '', basis: 'value', supplierId: null }])}>{t('purch.addCharge')}</Button></div>
          <p className="muted">{t('purch.landedHelp')}</p>
          {charges.map((c, i) => (
            <div key={i} className="row line-editor">
              <SelectField label={t('purch.chargeKind')} value={c.kind} onChange={(e) => setCharges(charges.map((x, j) => j === i ? { ...x, kind: e.target.value as ChargeInput['kind'] } : x))}>
                {(['freight', 'customs', 'insurance', 'other'] as const).map((k) => <option key={k} value={k}>{t(`purch.chargeKinds.${k}`)}</option>)}
              </SelectField>
              <TextField label={t('purch.description')} value={c.description} onChange={(e) => setCharges(charges.map((x, j) => j === i ? { ...x, description: e.target.value } : x))} />
              <TextField label={t('purch.amount')} value={c.amountText} onChange={(e) => setCharges(charges.map((x, j) => j === i ? { ...x, amountText: e.target.value } : x))} inputMode="decimal" dir="ltr" />
              <SelectField label={t('purch.spreadBy')} value={c.basis} onChange={(e) => setCharges(charges.map((x, j) => j === i ? { ...x, basis: e.target.value as 'value' | 'qty' } : x))}>
                <option value="value">{t('purch.byValue')}</option><option value="qty">{t('purch.byQty')}</option>
              </SelectField>
              <SupplierSelect value={c.supplierId ?? ''} onChange={(v) => setCharges(charges.map((x, j) => j === i ? { ...x, supplierId: v || null } : x))} label={t('purch.chargedBy')} />
              <Button size="sm" variant="ghost" onClick={() => setCharges(charges.filter((_, j) => j !== i))}>{t('pos.remove')}</Button>
            </div>
          ))}
        </div>
        <ErrorBox error={save.error} />
      </div>
    </Modal>
  );
}

// ------------------------------------------------------------------ returns
/** Goods back to the supplier (16.2): out of goods received not invoiced while not invoiced, else on a debit note with its VAT. */
export function ReturnForm({ onClose, onDone }: { onClose: () => void; onDone: (number: string) => void }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const [branchId, setBranchId] = useState(workBranchId(me));
  const [supplierId, setSupplierId] = useState('');
  const [receiptId, setReceiptId] = useState('');
  const [reason, setReason] = useState('');
  const [take, setTake] = useState<Record<string, { qty: string; units: string[] }>>({});
  const receipts = useQuery({ queryKey: ['receipts', supplierId, branchId], enabled: !!supplierId, queryFn: () => api.receipts({ supplierId, branchId }) });
  const receipt = useQuery({ queryKey: ['goodsReceipt', receiptId], enabled: !!receiptId, queryFn: () => api.goodsReceipt(receiptId) });
  const lines = (receipt.data?.lines ?? []).map((l) => ({ ...l, returnable: Math.round((l.qty - l.returned) * 1000) / 1000 })).filter((l) => l.returnable > 0);
  const chosen = lines.flatMap((l) => {
    const x = take[l.id];
    if (!x) return [];
    const q = l.units.length ? x.units.length : parseAmount(x.qty) ?? 0;
    return q > 0 ? [{ receiptLineId: l.id, qty: q, deviceUnitIds: l.units.length ? x.units : null }] : [];
  });
  const save = useMutation({
    mutationFn: () => api.returnToSupplier({ id: newId(), branchId, supplierId, reason, lines: chosen }),
    onSuccess: (r) => onDone(r.supplierReturn.number),
  });
  return (
    <Modal open title={t('purch.returnGoods')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={!chosen.length || !reason.trim()} busy={save.isPending} onClick={() => save.mutate()}>{t('purch.post')}</Button></>}>
      <div className="stack">
        <div className="grid-2">
          <SupplierSelect value={supplierId} onChange={(v) => { setSupplierId(v); setReceiptId(''); }} />
          <BranchSelect value={branchId} onChange={(v) => { setBranchId(v); setReceiptId(''); }} />
          <SelectField label={t('purch.receipt')} value={receiptId} onChange={(e) => { setReceiptId(e.target.value); setTake({}); }}>
            <option value="">{t('purch.chooseReceipt')}</option>
            {(receipts.data ?? []).map((r) => <option key={r.id} value={r.id}>{r.number} · {r.receiptDate}</option>)}
          </SelectField>
          <TextField label={t('purch.reason')} value={reason} onChange={(e) => setReason(e.target.value)} />
        </div>
        {lines.map((l) => (
          <div key={l.id} className="row line-editor">
            <strong className="grow">{l.description} <span className="ltr muted">{l.sku}</span> <small className="muted">{t('purch.canReturn', { qty: qty(l.returnable) })}</small></strong>
            {l.units.length ? (
              <div className="stack">
                {l.units.filter((u) => u.status === 'in_stock' || u.status === 'supplier_return').map((u) => (
                  <label key={u.id} className="row"><input type="checkbox" checked={take[l.id]?.units.includes(u.id) ?? false}
                    onChange={(e) => setTake({ ...take, [l.id]: { qty: '', units: e.target.checked ? [...(take[l.id]?.units ?? []), u.id] : (take[l.id]?.units ?? []).filter((x) => x !== u.id) } })} />
                    <span className="ltr">{u.imei ?? u.id.slice(0, 8)}</span></label>
                ))}
              </div>
            ) : <TextField label={t('purch.qty')} value={take[l.id]?.qty ?? ''} onChange={(e) => setTake({ ...take, [l.id]: { qty: e.target.value, units: [] } })} inputMode="decimal" dir="ltr" />}
          </div>
        ))}
        {receipt.data && lines.length === 0 && <Alert tone="info">{t('purch.nothingToReturn')}</Alert>}
        <p className="muted">{t('purch.returnHelp')}</p>
        <ErrorBox error={save.error ?? receipts.error} />
        {receipt.data && <span className="muted">{pick(receipt.data.receipt.supplierNameEn, receipt.data.receipt.supplierNameAr)}</span>}
      </div>
    </Modal>
  );
}

// ------------------------------------------------------------------ payments
/** Paying a supplier (16.1): oldest invoices first at the paying branch, or chosen invoices (head office may pay any branch's). */
export function PaymentForm({ supplierId: initial, onClose, onDone }: { supplierId?: string; onClose: () => void; onDone: (number: string) => void }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const [supplierId, setSupplierId] = useState(initial ?? '');
  const [branchId, setBranchId] = useState(workBranchId(me));
  const [method, setMethod] = useState<PayMethod>('bank_transfer');
  const [amount, setAmount] = useState('');
  const [reference, setReference] = useState('');
  const [date, setDate] = useState(businessToday());
  const [choose, setChoose] = useState(false);
  const [alloc, setAlloc] = useState<Record<string, string>>({});
  const items = useQuery({ queryKey: ['supplierOpenItems', supplierId], enabled: !!supplierId, queryFn: () => api.supplierOpenItems(supplierId) });
  const allocations = Object.entries(alloc).map(([entryId, v]) => ({ entryId, amount: parseAmount(v) ?? 0 })).filter((a) => a.amount > 0);
  const allocated = allocations.reduce((s, a) => s + toBaisa(a.amount), 0n);
  const amt = parseAmount(amount);
  const valid = supplierId && amt && amt > 0 && (method === 'cash' || reference.trim()) && (!choose || allocated <= toBaisa(amt));
  const save = useMutation({
    mutationFn: () => api.paySupplier({ id: newId(), branchId, supplierId, method, amount: amt!, reference: reference || null, paymentDate: date, allocations: choose ? allocations : null }),
    onSuccess: (r) => onDone(r.payment.number),
  });
  return (
    <Modal open title={t('purch.paySupplier')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={!valid} busy={save.isPending} onClick={() => save.mutate()}>{t('purch.pay')}</Button></>}>
      <div className="stack">
        <div className="grid-2">
          <SupplierSelect value={supplierId} onChange={setSupplierId} payable />
          <BranchSelect value={branchId} onChange={setBranchId} label={t('purch.payingBranch')} />
          <SelectField label={t('purch.method')} value={method} onChange={(e) => setMethod(e.target.value as PayMethod)}>
            {(['bank_transfer', 'cheque', 'cash'] as const).map((m) => <option key={m} value={m}>{t(`purch.methods.${m}`)}</option>)}
          </SelectField>
          <TextField label={t('purch.amount')} value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" dir="ltr" />
          {method !== 'cash' && <TextField label={t('purch.reference')} value={reference} onChange={(e) => setReference(e.target.value)} dir="ltr" />}
          <TextField label={t('purch.date')} type="date" value={date} onChange={(e) => setDate(e.target.value)} dir="ltr" />
        </div>
        <label className="row"><input type="checkbox" checked={choose} onChange={(e) => setChoose(e.target.checked)} /> {t('purch.chooseInvoices')}</label>
        {!choose && <p className="muted">{t('purch.oldestFirst')}</p>}
        {choose && (
          <DataTable<SupplierOpenItem> rows={items.data ?? []} rowKey={(r) => r.id} empty={t('purch.nothingOpen')}
            columns={[
              { key: 'n', header: t('purch.number'), render: (r) => <span className="ltr">{r.number}{r.supplierInvoiceNo ? ` · ${r.supplierInvoiceNo}` : ''}</span> },
              { key: 'b', header: t('purch.branch'), render: (r) => <span className="ltr">{r.branchCode}</span> },
              { key: 'd', header: t('purch.due'), render: (r) => formatDate(r.dueDate, lang) },
              { key: 'o', header: t('purch.open'), render: (r) => <span className="num">{fmt(r.open)}</span> },
              { key: 'a', header: t('purch.payNow'), render: (r) => <TextField label={`${t('purch.payNow')} ${r.number}`} value={alloc[r.id] ?? ''} onChange={(e) => setAlloc({ ...alloc, [r.id]: e.target.value })} inputMode="decimal" dir="ltr" /> },
            ]} />
        )}
        {choose && <div className="row figures-row"><span>{t('purch.allocated')} <strong className={`num${amt && allocated > toBaisa(amt) ? ' neg' : ''}`}>{fromBaisa(allocated)}</strong></span>
          {amt != null && allocated < toBaisa(amt) && <span className="muted">{t('purch.advance', { amount: fromBaisa(toBaisa(amt) - allocated) })}</span>}</div>}
        <ErrorBox error={save.error ?? items.error} />
      </div>
    </Modal>
  );
}
