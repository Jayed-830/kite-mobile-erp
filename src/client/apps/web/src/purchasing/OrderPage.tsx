import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { can, type OrderLine, type PaymentDetail, type ReceiptDetail, type ReturnDetail } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, DataTable, Modal, PageHeader, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDate, formatDateTime, useApi, useAuth } from '../core';
import { fmt, qty } from '../money';
import { download, OrderBadge } from './bits';
import { OrderForm, ReceiveForm } from './PurchasingPage';

/** One purchase order (16.2): its lines with what came in and was invoiced, approval within the user's limit, sending, receiving, closing. */
export function OrderPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const [dialog, setDialog] = useState<'edit' | 'receive' | 'cancel' | 'close' | null>(null);
  const [reason, setReason] = useState('');
  const [msg, setMsg] = useState<string | null>(null);
  const q = useQuery({ queryKey: ['order', id], queryFn: () => api.order(id) });
  const refresh = (m?: string) => { setDialog(null); setReason(''); if (m) setMsg(m); void qc.invalidateQueries({ queryKey: ['order', id] }); void qc.invalidateQueries({ queryKey: ['orders'] }); };
  const act = useMutation({ mutationFn: (f: () => Promise<unknown>) => f(), onSuccess: () => refresh(t('purch.done')) });
  if (q.isLoading) return <Spinner label={t('app.loading')} />;
  if (!q.data) return <ErrorBox error={q.error} />;
  const d = q.data;
  const o = d.order;
  const open = d.lines.some((l) => l.received < l.qty);
  return (
    <div className="stack">
      <PageHeader title={t('purch.orderTitle', { number: o.number })}>
        <Link className="btn" to="/app/purchasing?tab=orders">{t('app.back')}</Link>
        {o.status === 'draft' && can(me, 'PURCH.ORDER') && <Button onClick={() => setDialog('edit')}>{t('purch.edit')}</Button>}
        {o.status === 'draft' && d.approval.canApprove && <Button variant="primary" busy={act.isPending} onClick={() => act.mutate(() => api.approveOrder(id))}>{t('purch.approve')}</Button>}
        {o.status === 'approved' && can(me, 'PURCH.ORDER') && <Button variant="primary" busy={act.isPending} onClick={() => act.mutate(() => api.sendOrder(id))}>{t('purch.markSent')}</Button>}
        {o.status !== 'draft' && o.status !== 'cancelled' && <Button onClick={() => void download(api.orderPdf(id), `${o.number}.pdf`)}>{t('purch.pdf')}</Button>}
        {['approved', 'sent', 'partly_received'].includes(o.status) && open && can(me, 'PURCH.RECEIVE') && <Button variant="primary" onClick={() => setDialog('receive')}>{t('purch.receiveGoods')}</Button>}
        {['draft', 'approved', 'sent'].includes(o.status) && can(me, 'PURCH.ORDER') && <Button variant="danger" onClick={() => setDialog('cancel')}>{t('purch.cancelOrder')}</Button>}
        {['partly_received', 'received'].includes(o.status) && can(me, 'PURCH.ORDER') && <Button onClick={() => setDialog('close')}>{t('purch.closeOrder')}</Button>}
      </PageHeader>
      <div className="row">
        <OrderBadge status={o.status} />
        <span>{pick(o.supplierNameEn, o.supplierNameAr)} <span className="ltr muted">{o.supplierNumber}</span></span>
        <span className="muted">{t('purch.orderedBy', { branch: o.branchCode })} · {formatDate(o.orderDate, lang)}</span>
        {o.expectedDate && <span className="muted">{t('purch.expected')} {formatDate(o.expectedDate, lang)}</span>}
      </div>
      {o.status === 'draft' && !d.approval.canApprove && <Alert tone="warning">{d.approval.limit == null ? t('purch.cannotApprove') : t('purch.overLimit', { limit: fmt(d.approval.limit), total: fmt(o.grossTotal) })}</Alert>}
      {o.closeReason && <Alert tone="info">{t('purch.closedBecause', { reason: o.closeReason })}</Alert>}
      {msg && <Alert tone="success">{msg}</Alert>}
      <ErrorBox error={act.error} />
      <Card>
        <DataTable<OrderLine> rows={d.lines} rowKey={(l) => l.id} empty={t('app.none')}
          columns={[
            { key: 'i', header: t('purch.item'), render: (l) => <span>{l.description} <span className="ltr muted">{l.sku}</span>{l.repairJobNumber && <small className="muted"> · {l.repairJobNumber}</small>}</span> },
            { key: 'b', header: t('purch.deliverTo'), render: (l) => <span className="ltr">{l.deliverBranchCode}</span> },
            { key: 'q', header: t('purch.ordered'), render: (l) => <span className="num">{qty(l.qty)}</span> },
            { key: 'r', header: t('purch.receivedQty'), render: (l) => <span className="num">{qty(l.received)}</span> },
            { key: 'v', header: t('purch.invoicedQty'), render: (l) => <span className="num">{qty(l.invoiced)}</span> },
            { key: 'p', header: t('purch.unitPrice'), render: (l) => <span className="num">{fmt(l.unitPrice)}</span> },
            { key: 'n', header: t('purch.net'), render: (l) => <span className="num">{fmt(l.netAmount)}</span> },
          ]} />
        <div className="row figures-row">
          <span>{t('purch.net')} <strong className="num">{fmt(o.netTotal)}</strong></span>
          <span>{t('purch.vat')} <strong className="num">{fmt(o.taxTotal)}</strong></span>
          <span>{t('purch.total')} <strong className="num">{fmt(o.grossTotal)}</strong></span>
        </div>
      </Card>
      <Card>
        <h2>{t('purch.receiptsTitle')}</h2>
        <DataTable rows={d.receipts} rowKey={(r) => r.id} empty={t('purch.noReceipts')}
          columns={[
            { key: 'n', header: t('purch.number'), render: (r) => <Link className="ltr" to={`/app/purchasing/receipts/${r.id}`}>{r.number}</Link> },
            { key: 'd', header: t('purch.date'), render: (r) => formatDate(r.receiptDate, lang) },
            { key: 'b', header: t('purch.branch'), render: (r) => <span className="ltr">{r.branchCode}</span> },
            { key: 'g', header: t('purch.goodsValue'), render: (r) => <span className="num">{fmt(r.goodsValue)}</span> },
          ]} />
      </Card>
      <p className="muted">{t('purch.history', { by: o.createdBy ?? '—', at: formatDateTime(o.createdAt, lang) })}{o.approvedBy ? ` · ${t('purch.approvedBy', { by: o.approvedBy })}` : ''}</p>
      {dialog === 'edit' && <OrderForm order={d} onClose={() => setDialog(null)} onDone={() => refresh(t('purch.orderSaved'))} />}
      {dialog === 'receive' && <ReceiveForm order={d} onClose={() => setDialog(null)} onDone={(n) => refresh(t('purch.received', { number: n }))} />}
      {(dialog === 'cancel' || dialog === 'close') && (
        <Modal open title={dialog === 'cancel' ? t('purch.cancelOrder') : t('purch.closeOrder')} onClose={() => setDialog(null)}
          footer={<><Button onClick={() => setDialog(null)}>{t('app.cancel')}</Button>
            <Button variant="primary" disabled={!reason.trim()} busy={act.isPending} onClick={() => act.mutate(() => dialog === 'cancel' ? api.cancelOrder(id, reason) : api.closeOrder(id, reason))}>{t('purch.confirm')}</Button></>}>
          <div className="stack">
            <p>{dialog === 'cancel' ? t('purch.cancelHelp') : t('purch.closeHelp')}</p>
            <TextField label={t('purch.reason')} value={reason} onChange={(e) => setReason(e.target.value)} />
            <ErrorBox error={act.error} />
          </div>
        </Modal>
      )}
    </div>
  );
}

/** A goods receipt: what came in, at what cost with its landed costs, the phones by IMEI, and what is still to be invoiced. */
export function ReceiptPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const { t, pick, lang } = useI18n();
  const q = useQuery({ queryKey: ['goodsReceipt', id], queryFn: () => api.goodsReceipt(id) });
  if (q.isLoading) return <Spinner label={t('app.loading')} />;
  if (!q.data) return <ErrorBox error={q.error} />;
  const { receipt: r, lines, charges }: ReceiptDetail = q.data;
  return (
    <div className="stack">
      <PageHeader title={t('purch.receiptTitle', { number: r.number })}><Link className="btn" to="/app/purchasing?tab=receipts">{t('app.back')}</Link></PageHeader>
      <div className="row">
        <span>{pick(r.supplierNameEn, r.supplierNameAr)}</span><span className="ltr muted">{r.branchCode}</span><span className="muted">{formatDate(r.receiptDate, lang)}</span>
        {r.poNumber && <Link className="ltr" to={`/app/purchasing/orders/${r.poId}`}>{r.poNumber}</Link>}
        {r.deliveryNote && <span className="muted">{t('purch.deliveryNote')}: <span className="ltr">{r.deliveryNote}</span></span>}
        {r.journalNumber && <span className="ltr muted">{r.journalNumber}</span>}
      </div>
      <Card>
        <DataTable rows={lines} rowKey={(l) => l.id} empty={t('app.none')}
          columns={[
            { key: 'i', header: t('purch.item'), render: (l) => <div><span>{l.description} <span className="ltr muted">{l.sku}</span></span>
              {l.units.length > 0 && <div className="muted ltr">{l.units.map((u) => `${u.imei ?? '?'} (${fmt(u.cost)})`).join(' · ')}</div>}</div> },
            { key: 'q', header: t('purch.qty'), render: (l) => <span className="num">{qty(l.qty)}</span> },
            { key: 'p', header: t('purch.unitPrice'), render: (l) => <span className="num">{fmt(l.unitPrice)}</span> },
            { key: 'c', header: t('purch.landedShare'), render: (l) => <span className="num">{l.chargeShare ? fmt(l.chargeShare) : ''}</span> },
            { key: 's', header: t('purch.stockValue'), render: (l) => <span className="num">{fmt(l.stockValue)}</span> },
            { key: 'v', header: t('purch.invoicedQty'), render: (l) => <span className="num">{qty(l.invoiced)}</span> },
            { key: 'r', header: t('purch.returnedQty'), render: (l) => <span className="num">{l.returned ? qty(l.returned) : ''}</span> },
            { key: 'o', header: t('purch.notInvoiced'), render: (l) => <span className="num">{fmt(l.openValue)}</span> },
          ]} />
      </Card>
      {charges.length > 0 && (
        <Card>
          <h2>{t('purch.landedCosts')}</h2>
          <DataTable rows={charges} rowKey={(c) => c.id} empty={t('app.none')}
            columns={[
              { key: 'k', header: t('purch.chargeKind'), render: (c) => t(`purch.chargeKinds.${c.kind}`) },
              { key: 'd', header: t('purch.description'), render: (c) => c.description },
              { key: 's', header: t('purch.chargedBy'), render: (c) => c.supplierNameEn },
              { key: 'b', header: t('purch.spreadBy'), render: (c) => (c.basis === 'qty' ? t('purch.byQty') : t('purch.byValue')) },
              { key: 'a', header: t('purch.amount'), render: (c) => <span className="num">{fmt(c.amount)}</span> },
              { key: 'o', header: t('purch.notInvoiced'), render: (c) => <span className="num">{fmt(c.openValue)}</span> },
            ]} />
        </Card>
      )}
    </div>
  );
}

/** A return to the supplier: from goods received not invoiced, or a debit note with its VAT. */
export function ReturnPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const { t, pick, lang } = useI18n();
  const q = useQuery({ queryKey: ['supplierReturn', id], queryFn: () => api.supplierReturn(id) });
  if (q.isLoading) return <Spinner label={t('app.loading')} />;
  if (!q.data) return <ErrorBox error={q.error} />;
  const { supplierReturn: r, lines }: ReturnDetail = q.data;
  return (
    <div className="stack">
      <PageHeader title={t('purch.returnTitle', { number: r.number })}><Link className="btn" to="/app/purchasing?tab=returns">{t('app.back')}</Link></PageHeader>
      <div className="row"><span>{pick(r.supplierNameEn, r.supplierNameAr)}</span><span className="ltr muted">{r.branchCode}</span><span className="muted">{formatDate(r.returnDate, lang)}</span><span>{r.reason}</span></div>
      <Card>
        <DataTable rows={lines} rowKey={(l) => String(l.lineNo)} empty={t('app.none')}
          columns={[
            { key: 'i', header: t('purch.item'), render: (l) => <span>{l.description} {l.imei && <span className="ltr muted">{l.imei}</span>}</span> },
            { key: 'r', header: t('purch.receipt'), render: (l) => <span className="ltr">{l.receiptNumber}</span> },
            { key: 'q', header: t('purch.qty'), render: (l) => <span className="num">{qty(l.qty)}</span> },
            { key: 'k', header: t('purch.how'), render: (l) => <Badge tone={l.invoiced ? 'warning' : 'default'}>{l.invoiced ? t('purch.debitNote') : t('purch.fromGrni')}</Badge> },
            { key: 's', header: t('purch.stockValue'), render: (l) => <span className="num">{fmt(l.stockValue)}</span> },
            { key: 'n', header: t('purch.net'), render: (l) => <span className="num">{fmt(l.netAmount)}</span> },
            { key: 'v', header: t('purch.vat'), render: (l) => <span className="num">{l.taxAmount ? fmt(l.taxAmount) : ''}</span> },
          ]} />
        <div className="row figures-row">
          <span>{t('purch.net')} <strong className="num">{fmt(r.netTotal)}</strong></span>
          <span>{t('purch.vat')} <strong className="num">{fmt(r.taxTotal)}</strong></span>
          {r.journalNumber && <span className="ltr muted">{r.journalNumber}</span>}
        </div>
      </Card>
    </div>
  );
}

/** A supplier payment and the invoices it settled, at whichever branch they were booked. */
export function PaymentPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const { t, pick, lang } = useI18n();
  const q = useQuery({ queryKey: ['supplierPayment', id], queryFn: () => api.supplierPayment(id) });
  if (q.isLoading) return <Spinner label={t('app.loading')} />;
  if (!q.data) return <ErrorBox error={q.error} />;
  const { payment: p, allocations }: PaymentDetail = q.data;
  const left = Math.round((p.amount - allocations.reduce((s, a) => s + a.amount, 0)) * 1000) / 1000;
  return (
    <div className="stack">
      <PageHeader title={t('purch.paymentTitle', { number: p.number })}><Link className="btn" to="/app/purchasing?tab=payments">{t('app.back')}</Link></PageHeader>
      <div className="row"><span>{pick(p.supplierNameEn, p.supplierNameAr)}</span><span className="ltr muted">{p.branchCode}</span><span className="muted">{formatDate(p.paymentDate, lang)}</span>
        <span>{t(`purch.methods.${p.method}`)} <span className="ltr">{p.reference ?? ''}</span></span><strong className="num">{fmt(p.amount)}</strong></div>
      <Card>
        <DataTable rows={allocations} rowKey={(a) => a.number + a.branchCode} empty={t('purch.allAdvance')}
          columns={[
            { key: 'n', header: t('purch.number'), render: (a) => <span className="ltr">{a.number}</span> },
            { key: 'b', header: t('purch.branch'), render: (a) => <span className="ltr">{a.branchCode}</span> },
            { key: 'd', header: t('purch.due'), render: (a) => formatDate(a.dueDate, lang) },
            { key: 'a', header: t('purch.amount'), render: (a) => <span className="num">{fmt(a.amount)}</span> },
          ]} />
        {left > 0 && <p className="muted">{t('purch.advance', { amount: fmt(left) })}</p>}
      </Card>
    </div>
  );
}
