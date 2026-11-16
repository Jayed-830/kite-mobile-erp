import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { DeliverResult, PosPolicy, RepairBoardRow, TenderInput } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { toBaisa } from '@kite/pos';
import { Alert, Button, Modal, SelectField, TextField } from '@kite/ui';
import { ErrorBox, useApi } from '../core';
import { fmt, parseAmount } from '../money';
import { RepairStatusBadge, SignaturePad } from '../repairs/bits';
import { PayDialog } from './dialogs';
import { openPdf } from './TradeInWizard';

/**
 * Repairs at the till (13.4, 13.5): find a job by its card's QR, number or the customer's phone; take a deposit into the drawer,
 * or hand the device back: the job card or a check of the customer, their signature, the repair invoice with the deposit applied,
 * and the rest paid by any tender (or the deposit's excess back).
 */
export function RepairCounter({ policy, onClose, onDone }: { policy: PosPolicy; onClose: () => void; onDone: (message: string, cash: boolean) => void }) {
  const api = useApi();
  const { t, pick } = useI18n();
  const [q, setQ] = useState('');
  const [job, setJob] = useState<RepairBoardRow | null>(null);
  const [mode, setMode] = useState<'deposit' | 'collect' | null>(null);
  const [scannedQr, setScannedQr] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const found = useQuery({ queryKey: ['tillRepairs', q], enabled: q.trim().length >= 2, queryFn: () => api.repairs({ q: q.trim().startsWith('KITE-R|') ? q.trim().split('|')[1] : q.trim(), open: true }) });
  const pickJob = (r: RepairBoardRow) => { setJob(r); setMode(null); setError(null); setScannedQr(q.trim().startsWith('KITE-R|') ? q.trim() : null); };

  return (
    <Modal open title={t('repair.till.title')} onClose={onClose}>
      <div className="stack">
        <ErrorBox error={error} />
        {!job ? (
          <>
            <TextField label={t('repair.till.find')} value={q} onChange={(e) => setQ(e.target.value)} className="ltr" autoFocus
              onKeyDown={(e) => { if (e.key === 'Enter' && found.data?.length === 1) pickJob(found.data[0]!); }} />
            <ErrorBox error={found.error} />
            <div className="picker-list">
              {(found.data ?? []).map((r) => (
                <button key={r.id} type="button" className="picker-item" onClick={() => pickJob(r)}>
                  <strong className="ltr">{r.number}</strong> {r.customerName} · {r.device} <RepairStatusBadge status={r.status} />
                </button>
              ))}
            </div>
          </>
        ) : (
          <>
            <div className="row">
              <strong className="ltr">{job.number}</strong> <span>{job.customerName}</span> <span className="muted">{job.device} · {pick(job.categoryEn, job.categoryAr)}</span>
              <RepairStatusBadge status={job.status} /><span className="spacer" />
              <Button size="sm" variant="ghost" onClick={() => { setJob(null); setMode(null); }}>{t('app.back')}</Button>
            </div>
            <div className="row"><span>{t('repair.money.balance')}: <strong className="num">{fmt(job.deposit)}</strong></span></div>
            {mode == null && (
              <div className="row">
                <Button onClick={() => setMode('deposit')} disabled={job.outcome != null}>{t('repair.till.deposit')}</Button>
                <Button variant="primary" onClick={() => setMode('collect')} disabled={job.status !== 'ready'}>{t('repair.till.collect')}</Button>
                {job.status !== 'ready' && <span className="muted">{t('repair.till.notReady')}</span>}
              </div>
            )}
            {mode === 'deposit' && <Deposit jobId={job.id} onDone={(m, cash) => onDone(m, cash)} />}
            {mode === 'collect' && <Collect jobId={job.id} policy={policy} qr={scannedQr} onDone={(r, cash) => onDone(t('repair.till.delivered', { number: r.number, sale: r.saleNumber ?? '—' }), cash)} />}
          </>
        )}
      </div>
    </Modal>
  );
}

function Deposit({ jobId, onDone }: { jobId: string; onDone: (message: string, cash: boolean) => void }) {
  const api = useApi();
  const { t } = useI18n();
  const [amount, setAmount] = useState('');
  const [method, setMethod] = useState<'cash' | 'card' | 'bank_transfer'>('cash');
  const [tendered, setTendered] = useState('');
  const [ref, setRef] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const take = async () => {
    const a = parseAmount(amount);
    if (a == null) return;
    setBusy(true); setError(null);
    try {
      const r = await api.takeDeposit(jobId, { amount: a, method, tendered: method === 'cash' ? parseAmount(tendered) : null, approvalCode: method === 'card' ? ref || null : null, reference: method === 'bank_transfer' ? ref || null : null });
      void openPdf(api.repairDocument(jobId, { deposit: r.id }));
      onDone(t('repair.till.taken', { number: r.number, balance: fmt(r.balance) }), method === 'cash');
    } catch (e) { setError(e); } finally { setBusy(false); }
  };
  return (
    <div className="stack">
      <ErrorBox error={error} />
      <TextField label={t('repair.till.amount')} inputMode="decimal" className="ltr num" value={amount} onChange={(e) => setAmount(e.target.value)} autoFocus />
      <SelectField label={t('repair.till.method')} value={method} onChange={(e) => setMethod(e.target.value as typeof method)}>
        {(['cash', 'card', 'bank_transfer'] as const).map((m) => <option key={m} value={m}>{t(`repair.till.methods.${m}`)}</option>)}
      </SelectField>
      {method === 'cash' ? <TextField label={t('repair.till.tendered')} inputMode="decimal" className="ltr num" value={tendered} onChange={(e) => setTendered(e.target.value)} placeholder={amount} />
        : <TextField label={method === 'card' ? t('pos.approvalCode') : t('pos.reference')} className="ltr" value={ref} onChange={(e) => setRef(e.target.value)} />}
      <div><Button variant="primary" busy={busy} disabled={parseAmount(amount) == null} onClick={() => void take()}>{t('repair.till.deposit')}</Button></div>
    </div>
  );
}

function Collect({ jobId, policy, qr, onDone }: { jobId: string; policy: PosPolicy; qr: string | null; onDone: (r: DeliverResult, cash: boolean) => void }) {
  const api = useApi();
  const { t, pick } = useI18n();
  const preview = useQuery({ queryKey: ['deliveryPreview', jobId], queryFn: () => api.deliveryPreview(jobId) });
  const [check, setCheck] = useState<'qr' | 'verified'>(qr ? 'qr' : 'verified');
  const [qrCode, setQrCode] = useState(qr ?? '');
  const [name, setName] = useState('');
  const [sig, setSig] = useState<string | null>(null);
  const [excessTo, setExcessTo] = useState<'cash' | 'store_credit'>('cash');
  const [paying, setPaying] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const p = preview.data;
  if (!p) return <ErrorBox error={preview.error} />;
  const deliver = async (payments: TenderInput[]) => {
    setBusy(true); setError(null);
    try {
      const r = await api.deliver(jobId, { check, qrCode: check === 'qr' ? qrCode.trim() : null, collectedBy: name.trim(), signature: sig ?? '', payments, excessTo: p.excess > 0 ? excessTo : null, expectedDue: p.due });
      void openPdf(api.repairDocument(jobId, 'delivery-receipt'));
      onDone(r, payments.some((x) => x.method === 'cash') || (p.excess > 0 && excessTo === 'cash'));
    } catch (e) { setError(e); setPaying(false); } finally { setBusy(false); }
  };
  const ready = name.trim().length >= 2 && !!sig && (check === 'verified' || qrCode.trim().length > 0);
  return (
    <div className="stack">
      <ErrorBox error={error} />
      <ul>
        {p.charges.map((c) => <li key={c.id}>{pick(c.descriptionEn, c.descriptionAr)} <span className="num">{fmt(c.grossAmount)}</span></li>)}
      </ul>
      <div className="figures-row">
        <div className="fig"><span>{t('repair.money.charges')}</span><strong className="num">{fmt(p.gross)}</strong></div>
        <div className="fig"><span>{t('repair.money.balance')}</span><strong className="num">{fmt(p.deposit)}</strong></div>
        <div className="fig"><span>{t('repair.money.due')}</span><strong className="num">{fmt(p.due)}</strong></div>
        {p.excess > 0 && <div className="fig"><span>{t('repair.money.excess')}</span><strong className="num">{fmt(p.excess)}</strong></div>}
      </div>
      <SelectField label={t('repair.till.check')} value={check} onChange={(e) => setCheck(e.target.value as 'qr' | 'verified')}>
        <option value="qr">{t('repair.till.checks.qr')}</option><option value="verified">{t('repair.till.checks.verified')}</option>
      </SelectField>
      {check === 'qr' && <TextField label={t('repair.till.qr')} className="ltr mono" value={qrCode} onChange={(e) => setQrCode(e.target.value)} />}
      <TextField label={t('repair.till.collectedBy')} value={name} onChange={(e) => setName(e.target.value)} dir="auto" />
      <SignaturePad label={t('repair.till.sign')} onChange={setSig} />
      {p.excess > 0 && (
        <SelectField label={t('repair.till.excessTo')} value={excessTo} onChange={(e) => setExcessTo(e.target.value as 'cash' | 'store_credit')}>
          <option value="cash">{t('repair.till.excessTos.cash')}</option><option value="store_credit">{t('repair.till.excessTos.store_credit')}</option>
        </SelectField>
      )}
      {p.excess > 0 && <Alert tone="info">{t('repair.money.excess')}: {fmt(p.excess)}</Alert>}
      <div>
        {p.due > 0
          ? <Button variant="primary" disabled={!ready} onClick={() => setPaying(true)}>{t('repair.till.pay')} {fmt(p.due)}</Button>
          : <Button variant="primary" disabled={!ready} busy={busy} onClick={() => void deliver([])}>{t('repair.till.collect')}</Button>}
      </div>
      {paying && <PayDialog gross={toBaisa(p.due)} policy={policy} busy={busy} error={error} onClose={() => setPaying(false)} onPay={(x) => void deliver(x)} />}
    </div>
  );
}
