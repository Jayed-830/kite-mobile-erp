import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { can, type TradeInStatus } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi, useAuth } from '../core';
import { fmt } from '../money';
import { Ident } from '../devices/bits';
import { openPdf } from '../pos/TradeInWizard';

const TONE: Record<TradeInStatus, 'default' | 'success' | 'warning' | 'danger' | 'primary'> = { open: 'primary', accepted: 'warning', settled: 'success', rejected: 'danger', cancelled: 'default' };

export function TradeInStatusBadge({ status }: { status: TradeInStatus }) {
  const { t } = useI18n();
  return <Badge tone={TONE[status]}>{t(`tradein.statuses.${status}`)}</Badge>;
}

/** Trade-ins in the back office (12.4-12.6): every trade-in with what is left to settle, and the value-against-resale report. */
export function TradeInsPage() {
  const { t } = useI18n();
  const [tab, setTab] = useState<'list' | 'report'>('list');
  return (
    <div className="stack">
      <PageHeader title={t('tradein.title')} />
      <div className="tabs" role="tablist">
        {(['list', 'report'] as const).map((k) => <button key={k} role="tab" aria-selected={tab === k} className={`tab${tab === k ? ' active' : ''}`} onClick={() => setTab(k)}>{t(k === 'list' ? 'tradein.listTab' : 'tradein.reportTab')}</button>)}
      </div>
      {tab === 'list' ? <ListTab /> : <ReportTab />}
    </div>
  );
}

function ListTab() {
  const api = useApi();
  const navigate = useNavigate();
  const { t, pick, lang } = useI18n();
  const [q, setQ] = useState('');
  const [status, setStatus] = useState<TradeInStatus | 'unsettled' | ''>('');
  const rows = useQuery({ queryKey: ['tradeIns', q, status], queryFn: () => api.tradeIns({ q: q || undefined, status: status || undefined }) });
  return (
    <div className="stack">
      <div className="row">
        <TextField label={t('app.search')} placeholder={t('tradein.list.search')} value={q} onChange={(e) => setQ(e.target.value)} dir="auto" />
        <SelectField label={t('app.status')} value={status} onChange={(e) => setStatus(e.target.value as TradeInStatus | 'unsettled' | '')}>
          <option value="">{t('tradein.statuses.all')}</option>
          <option value="unsettled">{t('tradein.statuses.unsettled')}</option>
          {(['open', 'accepted', 'settled', 'rejected', 'cancelled'] as const).map((s) => <option key={s} value={s}>{t(`tradein.statuses.${s}`)}</option>)}
        </SelectField>
      </div>
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(r) => r.id} empty={t('tradein.list.empty')}
        columns={[
          { key: 'n', header: t('tradein.number'), render: (r) => <Link to={`/app/tradeins/${r.id}`} className="ltr">{r.number}</Link> },
          { key: 's', header: t('app.status'), render: (r) => <TradeInStatusBadge status={r.status} /> },
          { key: 'c', header: t('tradein.list.customer'), render: (r) => <span>{r.customerName} <span className="ltr muted">{r.customerPhone ?? ''}</span></span> },
          { key: 'd', header: t('tradein.list.device'), render: (r) => <span>{pick(r.descriptionEn, r.descriptionAr)} <Ident value={r.identifier} /></span> },
          { key: 'a', header: t('tradein.list.agreed'), render: (r) => <span className="num">{fmt(r.agreedValue ?? r.offeredValue)}</span> },
          { key: 'l', header: t('tradein.list.left'), render: (r) => <span className="num">{r.outstanding == null ? '—' : fmt(r.outstanding)}</span> },
          { key: 'w', header: t('tradein.list.when'), render: (r) => <span className="muted">{formatDateTime(r.acceptedAt ?? r.createdAt, lang)}</span> },
          { key: 'o', header: '', render: (r) => <Button size="sm" onClick={() => navigate(`/app/tradeins/${r.id}`)}>{t('app.edit')}</Button> },
        ]} />
    </div>
  );
}

function ReportTab() {
  const api = useApi();
  const { t, lang } = useI18n();
  const rows = useQuery({ queryKey: ['tradeInReport'], queryFn: () => api.tradeInReport() });
  return (
    <div className="stack">
      <p className="muted">{t('tradein.report.intro')}</p>
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(r) => r.number} empty={t('tradein.list.empty')}
        columns={[
          { key: 'n', header: t('tradein.number'), render: (r) => <span className="ltr">{r.number}</span> },
          { key: 'w', header: t('tradein.list.when'), render: (r) => <span className="muted">{formatDateTime(r.acceptedAt, lang)}</span> },
          { key: 'd', header: t('tradein.list.device'), render: (r) => <span>{r.descriptionEn} <Ident value={r.identifier} />{r.marginScheme && <> <Badge tone="primary">{t('tradein.detail.margin')}</Badge></>}</span> },
          { key: 'a', header: t('tradein.report.agreed'), render: (r) => <span className="num">{fmt(r.agreedValue)}</span> },
          { key: 'c', header: t('tradein.report.added'), render: (r) => <span className="num">{fmt(r.costAdded)}</span> },
          { key: 's', header: t('tradein.report.sale'), render: (r) => r.saleNumber ? <span className="ltr">{r.saleNumber}</span> : <span className="muted">{t('tradein.report.notSold')}</span> },
          { key: 'r', header: t('tradein.report.resale'), render: (r) => <span className="num">{fmt(r.resaleNet)}</span> },
          { key: 't', header: t('tradein.report.resaleTax'), render: (r) => <span className="num">{fmt(r.resaleTax)}</span> },
          { key: 'p', header: t('tradein.report.profit'), render: (r) => <span className={`num${(r.profit ?? 0) < 0 ? ' neg' : ''}`}>{fmt(r.profit)}</span> },
        ]} />
    </div>
  );
}

/** One trade-in: the seller, the device and its inspection, the values, consent, settlements and what is left. */
export function TradeInPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const d = useQuery({ queryKey: ['tradeIn', id], queryFn: () => api.tradeIn(id) });
  const [civil, setCivil] = useState<string | null>(null);
  const [rejecting, setRejecting] = useState(false);
  const [reason, setReason] = useState('');
  const refresh = () => qc.invalidateQueries({ queryKey: ['tradeIn', id] });
  const reveal = useMutation({ mutationFn: () => api.revealCivilId(id), onSuccess: (r) => setCivil(r.civilId) });
  const settle = useMutation({ mutationFn: () => api.settleTradeIn(id, { kind: 'store_credit' }), onSuccess: refresh });
  const reject = useMutation({ mutationFn: () => api.rejectTradeIn(id, reason), onSuccess: () => { setRejecting(false); void refresh(); } });
  const cancel = useMutation({ mutationFn: () => api.cancelTradeIn(id), onSuccess: refresh });
  const doc = useMutation({ mutationFn: () => openPdf(api.tradeInDocument(id)) });
  if (d.isLoading) return <Spinner label={t('app.loading')} />;
  if (!d.data) return <ErrorBox error={d.error} />;
  const ti = d.data.tradeIn;
  const fig = (label: string, v: number | null, strong = false) => <div className={`fig${strong ? ' strong' : ''}`}><span>{label}</span><span className="num">{fmt(v)}</span></div>;
  return (
    <div className="stack">
      <PageHeader title={`${t('tradein.one')} ${ti.number}`}>
        {(ti.status === 'accepted' || ti.status === 'settled') && <Button busy={doc.isPending} onClick={() => doc.mutate()}>{t('tradein.detail.document')}</Button>}
        {ti.deviceUnitId && can(me, 'DEVICE.VIEW') && <Link className="btn" to={`/app/devices/${ti.deviceUnitId}`}>{t('tradein.detail.unit')}</Link>}
      </PageHeader>
      <div className="row wrap"><TradeInStatusBadge status={ti.status} />{ti.reconnected && <Badge tone="primary">{t('tradein.detail.reconnected')}</Badge>}
        {ti.acceptedAt && <span className="muted">{t('tradein.detail.acceptedBy', { when: formatDateTime(ti.acceptedAt, lang), who: ti.acceptedByName ?? '' })}</span>}</div>
      <ErrorBox error={reveal.error ?? settle.error ?? reject.error ?? cancel.error ?? doc.error} />
      <div className="grid-2">
        <Card>
          <h3>{t('tradein.detail.seller')}</h3>
          <p><strong>{pick(ti.customerName, ti.customerNameAr ?? ti.customerName)}</strong> <span className="ltr muted">{ti.customerPhone ?? ''}</span></p>
          {ti.civilId && <p>{t('tradein.customer.civilId')}: <span className="ltr">{civil ?? ti.civilId}</span>
            {!civil && can(me, 'TRADEIN.APPROVE') && <> <Button size="sm" busy={reveal.isPending} onClick={() => reveal.mutate()}>{t('tradein.detail.reveal')}</Button></>}</p>}
          {civil && <Alert tone="info">{t('tradein.detail.revealed', { value: civil })}</Alert>}
          {ti.sellerVatNumber && <p>{t('tradein.customer.sellerVat')}: <span className="ltr">{ti.sellerVatNumber}</span></p>}
          <h3>{t('tradein.detail.device')}</h3>
          <p>{pick(ti.descriptionEn, ti.descriptionAr)} <Ident value={ti.imei1 ?? ti.serial} /></p>
          <p>{t('tradein.inspection.condition')}: {t(`tradein.inspection.conditions.${ti.condition}`)}{ti.gradeCode && <> · {t('tradein.inspection.grade')} {ti.gradeCode}</>}{ti.batteryHealth != null && <> · {t('tradein.inspection.battery')} {ti.batteryHealth}</>}</p>
          {ti.consentMethod && <><h3>{t('tradein.detail.consent')}</h3><p>{ti.consentMethod === 'paper' ? t('tradein.detail.paperForm', { reference: ti.consentReference ?? '' }) : t('tradein.detail.onScreen', { name: ti.consentName ?? '' })} · <bdi className="muted">{formatDateTime(ti.consentAt, lang)}</bdi></p></>}
        </Card>
        <Card>
          <h3>{t('tradein.detail.values')}</h3>
          <div className="figures">
            {fig(t('tradein.valuation.guide'), ti.guideValue)}
            {d.data.issues.map((x) => <div key={x.code} className="fig"><span>− {pick(x.nameEn, x.nameAr)}</span><span className="num">{fmt(x.amount)}</span></div>)}
            {fig(t('tradein.valuation.offer'), ti.offeredValue)}
            {fig(t('tradein.valuation.agreed'), ti.agreedValue, true)}
          </div>
          <p>{ti.marginScheme ? t('tradein.detail.marginYes', { number: ti.selfInvoiceNumber ?? '' }) : ti.status === 'open' ? null : t('tradein.detail.marginNo')}</p>
          {(ti.status === 'accepted' || ti.status === 'settled') && (
            <>
              <h3>{t('tradein.detail.settlements')}</h3>
              <div className="figures">
                {d.data.settlements.map((s) => <div key={s.id} className="fig"><span>{t(`tradein.kinds.${s.kind}`)}{s.saleNumber && <> <span className="ltr muted">{s.saleNumber}</span></>} <bdi className="muted">{formatDateTime(s.createdAt, lang)}</bdi></span><span className="num">{fmt(s.amount)}</span></div>)}
                {fig(t('tradein.detail.outstanding'), ti.outstanding, true)}
              </div>
              {ti.status === 'accepted' && (ti.outstanding ?? 0) > 0 && (
                <div className="row">
                  {can(me, 'TRADEIN.STORE_CREDIT') && <Button variant="primary" busy={settle.isPending} onClick={() => settle.mutate()}>{t('tradein.detail.settle')}</Button>}
                  <span className="muted">{t('tradein.detail.cashAtTill')}</span>
                </div>
              )}
            </>
          )}
          {ti.status === 'open' && can(me, 'TRADEIN.CREATE') && (
            <div className="row">
              <Button variant="danger" onClick={() => setRejecting(true)}>{t('tradein.detail.reject')}</Button>
              <Button busy={cancel.isPending} onClick={() => cancel.mutate()}>{t('tradein.detail.cancel')}</Button>
            </div>
          )}
          {ti.rejectReason && <Alert tone="warning">{ti.rejectReason}</Alert>}
        </Card>
      </div>
      {rejecting && (
        <Modal open title={t('tradein.detail.reject')} onClose={() => setRejecting(false)}
          footer={<><Button onClick={() => setRejecting(false)}>{t('app.cancel')}</Button><Button variant="danger" disabled={!reason.trim()} busy={reject.isPending} onClick={() => reject.mutate()}>{t('tradein.detail.reject')}</Button></>}>
          <TextField label={t('tradein.detail.rejectReason')} value={reason} onChange={(e) => setReason(e.target.value)} autoFocus />
          <ErrorBox error={reject.error} />
        </Modal>
      )}
    </div>
  );
}
