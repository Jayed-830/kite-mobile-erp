import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { can, type VatFigure, type VatReturnRow } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, Checkbox, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDate, formatDateTime, useApi, useAuth } from '../core';
import { fmt } from '../money';

/** A box amount as typed: adjustments may be negative; anything not a number counts as zero. */
const num = (s: string) => { const x = Number(s.trim() || 0); return Number.isFinite(x) ? Math.round(x * 1000) / 1000 : 0; };

/** VAT returns (17.4): the boxes of the Oman return, worked out from the period's documents, filed once the months are closed. */
export function VatPage() {
  const api = useApi();
  const qc = useQueryClient();
  const nav = useNavigate();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const returns = useQuery({ queryKey: ['vatReturns'], queryFn: () => api.vatReturns() });
  const periods = useQuery({ queryKey: ['vatPeriods'], queryFn: () => api.vatPeriods() });
  const boxes = useQuery({ queryKey: ['vatBoxes'], queryFn: () => api.vatBoxes() });
  const [period, setPeriod] = useState('');
  const prepare = useMutation({
    mutationFn: () => { const [from, to] = period.split('|'); return api.prepareVatReturn(from!, to!); },
    onSuccess: (r) => { void qc.invalidateQueries({ queryKey: ['vatReturns'] }); nav(`/app/accounting/vat/${r.vatReturn.id}`); },
  });
  const review = useMutation({ mutationFn: () => api.reviewVatBoxes(), onSuccess: () => qc.invalidateQueries({ queryKey: ['vatBoxes'] }) });
  const needsReview = (boxes.data ?? []).some((b) => b.needsReview);
  const manage = can(me, 'ACCT.VAT.RETURN');
  return (
    <div className="stack">
      <PageHeader title={t('acct.vatTitle')}><Link className="btn" to="/app/accounting">{t('app.back')}</Link></PageHeader>
      {needsReview && <Alert tone="warning">{t('acct.vatBoxesReview')} {manage && <Button size="sm" busy={review.isPending} onClick={() => review.mutate()}>{t('acct.confirmBoxes')}</Button>}</Alert>}
      <ErrorBox error={returns.error ?? prepare.error ?? review.error} />
      {manage && (
        <Card>
          <div className="row">
            <SelectField label={t('acct.vatPeriod')} value={period} onChange={(e) => setPeriod(e.target.value)}>
              <option value="">{t('acct.choosePeriod')}</option>
              {(periods.data?.periods ?? []).map((p) => <option key={p.from} value={`${p.from}|${p.to}`}>{formatDate(p.from, lang)} – {formatDate(p.to, lang)}</option>)}
            </SelectField>
            <Button variant="primary" disabled={!period} busy={prepare.isPending} onClick={() => prepare.mutate()}>{t('acct.prepareReturn')}</Button>
          </div>
          <p className="muted">{t('acct.vatHelp')}</p>
        </Card>
      )}
      <Card>
        <DataTable<VatReturnRow> rows={returns.data ?? []} rowKey={(r) => r.id} empty={t('acct.noReturns')}
          columns={[
            { key: 'n', header: t('acct.number'), render: (r) => <Link className="ltr" to={`/app/accounting/vat/${r.id}`}>{r.number}</Link> },
            { key: 'p', header: t('acct.vatPeriod'), render: (r) => `${formatDate(r.periodFrom, lang)} – ${formatDate(r.periodTo, lang)}` },
            { key: 'o', header: t('acct.outputVat'), render: (r) => <span className="num">{fmt(r.outputVat)}</span> },
            { key: 'i', header: t('acct.inputVat'), render: (r) => <span className="num">{fmt(r.inputVat)}</span> },
            { key: 'v', header: t('acct.netVat'), render: (r) => <strong className="num">{fmt(r.netVat)}</strong> },
            { key: 's', header: t('app.status'), render: (r) => <span><Badge tone={r.status === 'filed' ? 'success' : 'default'}>{t(`acct.vatStatus.${r.status}`)}</Badge> {r.paidAt && <Badge tone="primary">{t('acct.vatPaid')}</Badge>}</span> },
          ]} />
      </Card>
      <Card>
        <h2>{t('acct.boxes')}</h2>
        <DataTable rows={boxes.data ?? []} rowKey={(b) => b.code} empty={t('app.none')}
          columns={[
            { key: 'c', header: t('acct.box'), render: (b) => <strong className="ltr">{b.code}</strong> },
            { key: 'n', header: t('acct.description'), render: (b) => pick(b.nameEn, b.nameAr) },
            { key: 's', header: t('acct.source'), render: (b) => t(`acct.boxSource.${b.source}`) },
          ]} />
      </Card>
    </div>
  );
}

/** One VAT return: the boxes, the manual boxes entered by the accountant, the check against the VAT accounts, filing and payment. */
export function VatReturnPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const q = useQuery({ queryKey: ['vatReturn', id], queryFn: () => api.vatReturn(id) });
  const [adj, setAdj] = useState<Record<string, { value: string; vat: string }>>({});
  const [refund, setRefund] = useState(false);
  const [dialog, setDialog] = useState<'file' | 'pay' | null>(null);
  const [reference, setReference] = useState('');
  useEffect(() => {
    if (!q.data) return;
    const a = q.data.vatReturn.adjustments;
    setAdj(Object.fromEntries(Object.entries(a).map(([k, v]) => [k, { value: String(v.value), vat: String(v.vat) }])));
    setRefund(q.data.vatReturn.refundRequested);
  }, [q.data]);
  const done = () => { setDialog(null); setReference(''); void qc.invalidateQueries({ queryKey: ['vatReturn', id] }); void qc.invalidateQueries({ queryKey: ['vatReturns'] }); };
  const save = useMutation({
    mutationFn: () => api.adjustVatReturn(id, Object.fromEntries(Object.entries(adj).map(([k, v]) => [k, { value: num(v.value), vat: num(v.vat) } as VatFigure])), refund, q.data!.vatReturn.version),
    onSuccess: (r) => qc.setQueryData(['vatReturn', id], r),
  });
  const file = useMutation({ mutationFn: () => api.fileVatReturn(id, reference || null), onSuccess: done });
  const pay = useMutation({ mutationFn: () => api.payVatReturn(id, reference), onSuccess: done });
  if (q.isLoading) return <Spinner label={t('app.loading')} />;
  if (!q.data) return <ErrorBox error={q.error} />;
  const { vatReturn: v, boxes, check, agrees } = q.data;
  const draft = v.status === 'draft';
  const manage = can(me, 'ACCT.VAT.RETURN');
  return (
    <div className="stack">
      <PageHeader title={t('acct.vatReturnTitle', { number: v.number })}>
        <Link className="btn" to="/app/accounting/vat">{t('app.back')}</Link>
        {draft && manage && <Button busy={save.isPending} onClick={() => save.mutate()}>{t('acct.recalculate')}</Button>}
        {draft && manage && <Button variant="primary" onClick={() => setDialog('file')}>{t('acct.fileReturn')}</Button>}
        {!draft && !v.paymentNumber && manage && <Button variant="primary" onClick={() => setDialog('pay')}>{v.netVat < 0 ? t('acct.recordRefund') : t('acct.recordPayment')}</Button>}
      </PageHeader>
      <div className="row">
        <Badge tone={draft ? 'default' : 'success'}>{t(`acct.vatStatus.${v.status}`)}</Badge>
        <span>{formatDate(v.periodFrom, lang)} – {formatDate(v.periodTo, lang)}</span>
        {v.filedAt && <span className="muted">{t('acct.filedBy', { by: v.filedBy ?? '', at: formatDateTime(v.filedAt, lang) })}</span>}
        {v.filingReference && <span className="ltr muted">{v.filingReference}</span>}
        {v.settlementNumber && <span className="ltr muted">{v.settlementNumber}</span>}
        {v.paymentNumber && <span className="ltr muted">{v.paymentNumber}</span>}
      </div>
      {!agrees && <Alert tone="error">{t('acct.vatDisagrees')}</Alert>}
      <ErrorBox error={save.error ?? file.error ?? pay.error} />
      <Card>
        <div className="table-wrap">
          <table className="table">
            <thead><tr><th scope="col">{t('acct.box')}</th><th scope="col">{t('acct.description')}</th><th scope="col">{t('acct.valueBeforeVat')}</th><th scope="col">{t('acct.vatAmount')}</th></tr></thead>
            <tbody>
              {boxes.map((b) => {
                const f = v.boxes[b.code] ?? { value: 0, vat: 0 };
                const manual = b.source === 'manual' && draft && manage;
                return (
                  <tr key={b.code} className={b.source === 'formula' ? 'strong' : undefined}>
                    <td className="ltr">{b.code}</td><td>{pick(b.nameEn, b.nameAr)}</td>
                    <td className="num">{manual ? <TextField label={`${b.code} ${t('acct.valueBeforeVat')}`} value={adj[b.code]?.value ?? ''} onChange={(e) => setAdj({ ...adj, [b.code]: { value: e.target.value, vat: adj[b.code]?.vat ?? '' } })} inputMode="decimal" dir="ltr" /> : fmt(f.value)}</td>
                    <td className="num">{manual ? <TextField label={`${b.code} ${t('acct.vatAmount')}`} value={adj[b.code]?.vat ?? ''} onChange={(e) => setAdj({ ...adj, [b.code]: { value: adj[b.code]?.value ?? '', vat: e.target.value } })} inputMode="decimal" dir="ltr" /> : fmt(f.vat)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        {draft && manage && <Checkbox label={t('acct.refundRequested')} checked={refund} onChange={(e) => setRefund(e.target.checked)} />}
      </Card>
      <Card>
        <h2>{t('acct.vatCheck')}</h2>
        <DataTable rows={check} rowKey={(c) => c.branchCode} empty={t('app.none')}
          columns={[
            { key: 'b', header: t('acct.branch'), render: (c) => <span className="ltr">{c.branchCode}</span> },
            { key: 'o', header: t('acct.outputFromDocs'), render: (c) => <span className="num">{fmt(c.output)}</span> },
            { key: 'oa', header: t('acct.outputAccount'), render: (c) => <span className={`num${c.output !== c.outputAccount ? ' neg' : ''}`}>{fmt(c.outputAccount)}</span> },
            { key: 'i', header: t('acct.inputFromDocs'), render: (c) => <span className="num">{fmt(c.input)}</span> },
            { key: 'ia', header: t('acct.inputAccount'), render: (c) => <span className={`num${c.input !== c.inputAccount ? ' neg' : ''}`}>{fmt(c.inputAccount)}</span> },
          ]} />
      </Card>
      {dialog && (
        <Modal open title={dialog === 'file' ? t('acct.fileReturn') : v.netVat < 0 ? t('acct.recordRefund') : t('acct.recordPayment')} onClose={() => setDialog(null)}
          footer={<><Button onClick={() => setDialog(null)}>{t('app.cancel')}</Button>
            <Button variant="primary" disabled={dialog === 'pay' && !reference.trim()} busy={file.isPending || pay.isPending} onClick={() => (dialog === 'file' ? file.mutate() : pay.mutate())}>{t('acct.confirm')}</Button></>}>
          <div className="stack">
            <p>{dialog === 'file' ? t('acct.fileHelp', { amount: fmt(v.netVat) }) : t('acct.payHelp', { amount: fmt(Math.abs(v.netVat)) })}</p>
            <TextField label={dialog === 'file' ? t('acct.filingReference') : t('acct.bankReference')} value={reference} onChange={(e) => setReference(e.target.value)} dir="ltr" />
            <ErrorBox error={file.error ?? pay.error} />
          </div>
        </Modal>
      )}
    </div>
  );
}
