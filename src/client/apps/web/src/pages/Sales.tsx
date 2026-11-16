import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { can, type ShiftFigures } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Badge, Button, DataTable, Modal, PageHeader, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi, useAuth } from '../core';
import { fmt, qty } from '../money';
import { ReceiptView } from '../pos/ReceiptView';

type Tab = 'shifts' | 'sales' | 'emergency';

/** Sales and shift reports (10.4): X/Z reports, documents with receipts and A4 invoices, emergency uploads. */
export function SalesPage() {
  const { t } = useI18n();
  const [tab, setTab] = useState<Tab>('shifts');
  return (
    <div className="stack">
      <PageHeader title={t('salesReports.title')} />
      <div className="tabs" role="tablist">
        {(['shifts', 'sales', 'emergency'] as Tab[]).map((k) => <button key={k} role="tab" aria-selected={tab === k} className={`tab${tab === k ? ' active' : ''}`} onClick={() => setTab(k)}>{t(`salesReports.tabs.${k}`)}</button>)}
      </div>
      {tab === 'shifts' && <ShiftsTab />}
      {tab === 'sales' && <SalesTab />}
      {tab === 'emergency' && <EmergencyTab />}
    </div>
  );
}

export function FiguresView({ f }: { f: ShiftFigures }) {
  const { t } = useI18n();
  const row = (label: string, v: number, strong = false) => <div className={`fig${strong ? ' strong' : ''}`}><span>{label}</span><span className="num">{fmt(v)}</span></div>;
  return (
    <div className="figures">
      <div className="fig"><span>{t('salesReports.salesCount')}</span><span className="num">{f.salesCount}</span></div>
      {row(t('salesReports.salesGross'), f.salesGross)}
      <div className="fig"><span>{t('salesReports.creditNotes')}</span><span className="num">{f.creditNotesCount} · {fmt(f.creditNotesGross)}</span></div>
      {row(t('salesReports.net'), f.salesNet)}
      {row(t('salesReports.vat'), f.salesTax)}
      {row(t('salesReports.discounts'), f.salesDiscount)}
      {row(t('salesReports.rounding'), f.cashRounding)}
      <hr />
      {row(t('salesReports.opening'), f.openingFloat)}
      {row(t('salesReports.cash'), f.cashPayments)}
      {row(t('salesReports.payIns'), f.payIns)}
      {row(t('salesReports.payOuts'), f.payOuts)}
      {row(t('salesReports.drops'), f.drops)}
      {!!f.tradeInPayouts && row(t('tradein.figures.tradeInPayouts'), f.tradeInPayouts)}
      {row(t('salesReports.expected'), f.expectedCash, true)}
      <hr />
      {row(t('salesReports.card'), f.cardPayments)}
      {f.cardByTerminal.map((c) => <div key={c.cardTerminal} className="fig sub"><span className="ltr">{c.cardTerminal || '—'}</span><span className="num">{fmt(c.total)}</span></div>)}
      {row(t('salesReports.transfer'), f.transferPayments)}
      {!!f.tradeInCredit && row(t('tradein.figures.tradeInCredit'), f.tradeInCredit)}
      {!!f.storeCreditPayments && row(t('tradein.figures.storeCreditPayments'), f.storeCreditPayments)}
      <div className="fig"><span>{t('salesReports.noSales')}</span><span className="num">{f.noSales}</span></div>
    </div>
  );
}

function ShiftsTab() {
  const api = useApi();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const shifts = useQuery({ queryKey: ['shifts'], queryFn: () => api.shifts(), enabled: can(me, 'POS.REPORTS.VIEW') });
  const [open, setOpen] = useState<string | null>(null);
  const report = useQuery({ queryKey: ['shift', open], queryFn: () => api.shift(open!), enabled: !!open });
  return (
    <div className="stack">
      <ErrorBox error={shifts.error} />
      <DataTable rows={shifts.data ?? []} rowKey={(s) => s.id} empty={t('app.none')}
        columns={[
          { key: 'n', header: t('salesReports.shift'), render: (s) => <button className="link-button ltr" onClick={() => setOpen(s.id)}>{s.number}</button> },
          { key: 's', header: t('app.status'), render: (s) => <span className="row"><Badge tone={s.status === 'open' ? 'warning' : 'success'}>{t(`salesReports.status.${s.status}`)}</Badge>{s.isEmergency && <Badge tone="danger">{t('pos.emergency')}</Badge>}</span> },
          { key: 'o', header: t('salesReports.opened'), render: (s) => formatDateTime(s.openedAt, lang) },
          { key: 'c', header: t('salesReports.closed'), render: (s) => formatDateTime(s.closedAt, lang) },
          { key: 'v', header: t('salesReports.variance'), render: (s) => s.variance == null ? '—' : <span className={`num${s.variance < 0 ? ' neg' : ''}`}>{fmt(s.variance)}</span> },
        ]} />
      {open && (
        <Modal open title={report.data ? `${report.data.report} · ${report.data.shift.number}` : t('app.loading')} onClose={() => setOpen(null)} footer={<Button onClick={() => setOpen(null)}>{t('app.close')}</Button>}>
          {report.isLoading && <Spinner label={t('app.loading')} />}
          <ErrorBox error={report.error} />
          {report.data && (
            <>
              <FiguresView f={report.data.figures} />
              {report.data.shift.countedCash != null && <div className="fig strong"><span>{t('salesReports.counted')}</span><span className="num">{fmt(report.data.shift.countedCash)}</span></div>}
              {report.data.shift.variance != null && <div className="fig strong"><span>{t('salesReports.variance')}</span><span className="num">{fmt(report.data.shift.variance)}</span></div>}
              {report.data.cardSettlements.map((c) => <div key={c.cardTerminal} className="fig"><span>{t('salesReports.batch')} <span className="ltr">{c.cardTerminal}</span></span><span className="num">{fmt(c.batchTotal)} ({fmt(c.difference)})</span></div>)}
            </>
          )}
        </Modal>
      )}
    </div>
  );
}

function SalesTab() {
  const api = useApi();
  const { me } = useAuth();
  const { t, lang, pick } = useI18n();
  const [q, setQ] = useState('');
  const rows = useQuery({ queryKey: ['salesList', q], queryFn: () => api.sales({ q: q || undefined }), enabled: can(me, 'POS.REPORTS.VIEW') });
  const [open, setOpen] = useState<string | null>(null);
  const detail = useQuery({ queryKey: ['sale', open], queryFn: () => api.sale(open!), enabled: !!open });
  const receipt = useQuery({ queryKey: ['receipt', open], queryFn: () => api.receipt(open!), enabled: !!open });
  const pdf = useMutation({
    mutationFn: async (id: string) => { const b = await api.invoicePdf(id); const url = URL.createObjectURL(b); window.open(url, '_blank'); setTimeout(() => URL.revokeObjectURL(url), 60_000); },
  });
  return (
    <div className="stack">
      <TextField label={t('salesReports.findNumber')} value={q} onChange={(e) => setQ(e.target.value)} dir="ltr" />
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(s) => s.id} empty={t('app.none')}
        columns={[
          { key: 'n', header: t('salesReports.number'), render: (s) => <button className="link-button ltr" onClick={() => setOpen(s.id)}>{s.number}</button> },
          { key: 'd', header: t('salesReports.docType'), render: (s) => <span className="row">{t(`salesReports.docTypes.${s.docType}`)}{s.creditReason && <Badge>{t(`salesReports.reasons.${s.creditReason}`)}</Badge>}{s.isEmergency && <Badge tone="danger">{t('pos.emergency')}</Badge>}</span> },
          { key: 'w', header: t('salesReports.when'), render: (s) => formatDateTime(s.completedAt, lang) },
          { key: 'c', header: t('salesReports.cashier'), render: (s) => `${s.cashierName} · ${s.terminalCode}` },
          { key: 'g', header: t('salesReports.total'), render: (s) => <span className="num">{fmt(s.grossTotal)}</span> },
        ]} />
      {open && (
        <Modal open title={detail.data?.sale.number ?? t('app.loading')} onClose={() => setOpen(null)}
          footer={<><Button busy={pdf.isPending} onClick={() => pdf.mutate(open)}>{t('salesReports.a4')}</Button><Button onClick={() => setOpen(null)}>{t('app.close')}</Button></>}>
          <ErrorBox error={detail.error ?? receipt.error ?? pdf.error} />
          {detail.data && (
            <DataTable rows={detail.data.lines} rowKey={(l) => l.id} empty=""
              columns={[
                { key: 'i', header: t('catalog.name'), render: (l) => pick(l.descriptionEn, l.descriptionAr) },
                { key: 'q', header: t('inventory.qty'), render: (l) => <span className="num">{qty(l.qty)}{l.returnedQty ? <span className="muted"> (−{qty(l.returnedQty)})</span> : null}</span> },
                { key: 'g', header: t('salesReports.total'), render: (l) => <span className="num">{fmt(l.gross)}</span> },
              ]} />
          )}
          {receipt.data && <div className="receipt-frame"><ReceiptView r={receipt.data} /></div>}
        </Modal>
      )}
    </div>
  );
}

function EmergencyTab() {
  const api = useApi();
  const { t, lang } = useI18n();
  const rows = useQuery({ queryKey: ['emergencyRecords'], queryFn: () => api.emergencyRecords() });
  return (
    <div className="stack">
      <p className="muted">{t('salesReports.emergencyHelp')}</p>
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(r) => r.id} empty={t('app.none')}
        columns={[
          { key: 't', header: t('terminals.code'), render: (r) => <span className="ltr">{r.branchCode} · {r.terminalCode} #{r.seq}</span> },
          { key: 'k', header: t('salesReports.kind'), render: (r) => t(`salesReports.kinds.${r.kind}`) },
          { key: 'w', header: t('salesReports.recorded'), render: (r) => formatDateTime(r.recordedAt, lang) },
          { key: 'r', header: t('salesReports.result'), render: (r) => <Badge tone={r.result === 'applied' ? 'success' : 'danger'}>{t(`salesReports.results.${r.result}`)}</Badge> },
          { key: 'd', header: t('salesReports.detail'), render: (r) => r.detail ?? '—' },
        ]} />
    </div>
  );
}
