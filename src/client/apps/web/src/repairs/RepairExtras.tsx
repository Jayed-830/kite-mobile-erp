import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { can, type RepairDetail } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { sharesComplete } from '@kite/pos';
import { Alert, Badge, Button, Card, DataTable, Modal, SelectField, TextField } from '@kite/ui';
import { ErrorBox, formatDate, formatDateTime, useApi, useAuth } from '../core';
import { fmt, parseAmount, qty } from '../money';

// Phase 9 parts of the repair job page: the parts the job needs (held in stock or waited for), outside work, who shares the
// commission, the job's commissions, and the invoice that stands with its corrections at the till.

/** What the approved estimate needs of each part: fitted, held in stock for the job, still waited for (13.4). */
export function PartsCard({ d }: { d: RepairDetail }) {
  const { t, lang, pick } = useI18n();
  const parts = d.parts ?? [];
  const waiting = parts.reduce((a, p) => a + p.requested, 0);
  return (
    <Card>
      <div className="stack">
        <div className="row"><strong>{t('parts.title')}</strong><span className="spacer" /><Link to="/app/repairs/parts">{t('parts.neededTitle')}</Link></div>
        {waiting > 0 && <Alert tone="warning">{t('parts.waiting', { n: qty(waiting) })}</Alert>}
        <DataTable rows={parts} rowKey={(p) => p.variantId} empty={t('parts.none')}
          columns={[
            { key: 'n', header: t('parts.part'), render: (p) => <span>{pick(p.nameEn, p.nameAr)} <span className="ltr muted">{p.sku}</span>{p.tracking === 'serial' && <Badge>{t('parts.serial')}</Badge>}</span> },
            { key: 'need', header: t('parts.needed'), render: (p) => <span className="num">{qty(p.needed)}</span> },
            { key: 'fit', header: t('parts.fitted'), render: (p) => <span className="num">{qty(p.fitted)}</span> },
            { key: 'res', header: t('parts.reserved'), render: (p) => <span className="num">{qty(p.reserved)}</span> },
            { key: 'req', header: t('parts.requested'), render: (p) => p.requested > 0 ? <Badge tone="warning"><span className="num">{qty(p.requested)}</span></Badge> : <span className="num muted">0</span> },
            { key: 'since', header: t('parts.since'), render: (p) => p.requestedSince ? <span className="muted">{formatDateTime(p.requestedSince, lang)}</span> : '' },
          ]} />
      </div>
    </Card>
  );
}

/** Outside work at cost into the job's work in progress (15.1); a mistaken entry is reversed in full. */
export function OutsideWorkCard({ d, onDone, onError }: { d: RepairDetail; onDone: (d: RepairDetail) => void; onError: (e: unknown) => void }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const [vendor, setVendor] = useState('');
  const [amount, setAmount] = useState('');
  const [reference, setReference] = useState('');
  const [reversing, setReversing] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const j = d.job;
  const canWork = can(me, 'REPAIR.WORK') && ['under_diagnosis', 'waiting_parts', 'in_progress', 'testing'].includes(j.status);
  const lines = d.work.filter((w) => w.kind === 'external');
  return (
    <Card>
      <div className="stack">
        <strong>{t('outside.title')}</strong>
        <p className="muted">{t('outside.intro')}</p>
        {canWork && (
          <div className="row">
            <TextField label={t('outside.vendor')} value={vendor} onChange={(e) => setVendor(e.target.value)} dir="auto" />
            <TextField label={t('outside.amount')} inputMode="decimal" className="ltr num" value={amount} onChange={(e) => setAmount(e.target.value)} />
            <TextField label={t('outside.reference')} className="ltr" value={reference} onChange={(e) => setReference(e.target.value)} />
            <Button variant="primary" disabled={!vendor.trim() || parseAmount(amount) == null}
              onClick={() => api.addOutsideWork(j.id, { vendor: vendor.trim(), amount: parseAmount(amount)!, reference: reference.trim() || null })
                .then((r) => { onDone(r); setVendor(''); setAmount(''); setReference(''); }).catch(onError)}>{t('outside.add')}</Button>
          </div>
        )}
        {lines.length > 0 && (
          <DataTable rows={lines} rowKey={(w) => w.id} empty="—"
            columns={[
              { key: 'v', header: t('outside.vendor'), render: (w) => <span dir="auto">{w.vendor}{w.reference ? <span className="ltr muted">{` · ${w.reference}`}</span> : null}{w.returnOfId && <Badge>{t('outside.reversed')}</Badge>}</span> },
              { key: 'a', header: t('outside.amount'), render: (w) => <span className="num">{fmt(w.cost ?? 0)}</span> },
              { key: 'n', header: t('outside.note'), render: (w) => <span dir="auto" className="muted">{w.note ?? ''}</span> },
              { key: 'd', header: '', render: (w) => <span className="muted">{formatDateTime(w.createdAt, lang)} · {w.createdBy}</span> },
              { key: 'x', header: '', render: (w) => canWork && !w.returnOfId && !w.reversed && (w.cost ?? 0) > 0
                ? <Button size="sm" variant="ghost" onClick={() => { setReason(''); setReversing(w.id); }}>{t('outside.reverse')}</Button> : w.reversed ? <Badge>{t('outside.reversed')}</Badge> : null },
            ]} />
        )}
      </div>
      <Modal open={reversing != null} title={t('outside.reverseTitle')} onClose={() => setReversing(null)}
        footer={<Button variant="danger" disabled={!reason.trim()} onClick={() => api.reverseOutsideWork(j.id, reversing!, reason.trim()).then((r) => { onDone(r); setReversing(null); }).catch(onError)}>{t('outside.reverse')}</Button>}>
        <TextField label={t('outside.reason')} value={reason} onChange={(e) => setReason(e.target.value)} dir="auto" />
      </Modal>
    </Card>
  );
}

/** Who shares the job's commission (15.2): shares that make 100%, fixed once the commissions are generated. */
export function SharesCard({ d }: { d: RepairDetail }) {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const j = d.job;
  const shares = useQuery({ queryKey: ['jobShares', j.id], queryFn: () => api.jobShares(j.id) });
  const techs = useQuery({ queryKey: ['repairTechnicians'], queryFn: () => api.repairTechnicians() });
  const [edit, setEdit] = useState<{ technicianId: string; share: string }[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const editable = can(me, 'REPAIR.ASSIGN') && j.status !== 'delivered';
  const total = (edit ?? []).reduce((a, s) => a + (Number(s.share) || 0), 0);
  const valid = edit != null && edit.every((s) => s.technicianId && Number(s.share) > 0) && sharesComplete(edit.map((s) => s.share || '0'))
    && new Set(edit.map((s) => s.technicianId)).size === edit.length;
  const save = (list: { technicianId: string; share: number }[]) =>
    api.setJobShares(j.id, list).then(() => { setEdit(null); void qc.invalidateQueries({ queryKey: ['jobShares', j.id] }); }).catch(setError);
  return (
    <div className="stack">
      <div className="row"><strong>{t('shares.title')}</strong><span className="spacer" />
        {editable && !edit && <Button size="sm" onClick={() => { setError(null); setEdit((shares.data ?? []).length ? shares.data!.map((s) => ({ technicianId: s.technicianId, share: String(s.share) }))
          : [{ technicianId: j.technicianId ?? '', share: '50' }, { technicianId: '', share: '50' }]); }}>{t('shares.edit')}</Button>}
      </div>
      <ErrorBox error={error ?? shares.error} />
      {!edit && ((shares.data ?? []).length === 0 ? <span className="muted">{t('shares.none')}</span>
        : <ul>{shares.data!.map((s) => <li key={s.technicianId}>{pick(s.technicianName, s.technicianNameAr ?? s.technicianName)} · {s.share}%</li>)}</ul>)}
      {edit && (
        <div className="stack">
          <p className="muted">{t('shares.intro')}</p>
          {edit.map((s, i) => (
            <div key={i} className="row">
              <SelectField label={t('repair.technician')} value={s.technicianId} onChange={(e) => setEdit(edit.map((x, k) => (k === i ? { ...x, technicianId: e.target.value } : x)))}>
                <option value="">—</option>
                {(techs.data ?? []).map((u) => <option key={u.id} value={u.id}>{pick(u.nameEn, u.nameAr ?? u.nameEn)}</option>)}
              </SelectField>
              <TextField label={t('shares.share')} inputMode="decimal" className="ltr num" value={s.share} onChange={(e) => setEdit(edit.map((x, k) => (k === i ? { ...x, share: e.target.value } : x)))} />
              <Button size="sm" variant="ghost" aria-label={t('app.cancel')} onClick={() => setEdit(edit.filter((_, k) => k !== i))}>✕</Button>
            </div>
          ))}
          <div className="row">
            <Button size="sm" onClick={() => setEdit([...edit, { technicianId: '', share: '' }])}>{t('shares.add')}</Button>
            <span className={valid ? 'muted' : 'error-text'}>{t('shares.total', { n: Math.round(total * 10000) / 10000 })}</span>
            <span className="spacer" />
            <Button size="sm" onClick={() => void save([])}>{t('shares.clear')}</Button>
            <Button size="sm" onClick={() => setEdit(null)}>{t('app.cancel')}</Button>
            <Button size="sm" variant="primary" disabled={!valid} onClick={() => void save(edit.map((x) => ({ technicianId: x.technicianId, share: Number(x.share) })))}>{t('app.save')}</Button>
          </div>
          {!valid && <span className="muted">{t('shares.mustBe100')}</span>}
        </div>
      )}
    </div>
  );
}

/** The commissions on this job, with how each was worked out (15.2). */
export function JobCommissions({ jobId }: { jobId: string }) {
  const api = useApi();
  const { t, lang, pick } = useI18n();
  const q = useQuery({ queryKey: ['commissions', { jobId }], queryFn: () => api.commissions({ jobId }) });
  if (!q.data || q.data.length === 0) return q.error ? <ErrorBox error={q.error} /> : null;
  return (
    <Card>
      <div className="stack">
        <strong>{t('comm.forJob')}</strong>
        <DataTable rows={q.data} rowKey={(a) => a.id} empty={t('comm.none')}
          columns={[
            { key: 't', header: t('comm.technician'), render: (a) => pick(a.technicianName, a.technicianNameAr ?? a.technicianName) },
            { key: 'k', header: t('app.status'), render: (a) => <span>{t(`comm.kinds.${a.kind}`)} <Badge tone={a.status === 'paid' ? 'success' : a.status === 'reversed' ? 'danger' : a.status === 'approved' ? 'primary' : 'default'}>{t(`comm.statuses.${a.status}`)}</Badge></span> },
            { key: 'r', header: t('comm.rule'), render: (a) => a.ruleNameEn ? <span>{pick(a.ruleNameEn, a.ruleNameAr ?? a.ruleNameEn)} <span className="muted">v{a.ruleVersion}</span></span> : (a.reason ?? '') },
            { key: 'b', header: t('comm.basis'), render: (a) => a.basis ? <span className="muted">{t(`comm.bases.${a.basis}`)}{a.share != null && a.share !== 100 ? ` · ${a.share}%` : ''}</span> : '' },
            { key: 'i', header: t('comm.invoice'), render: (a) => <span className="ltr">{a.invoiceNumber ?? ''}</span> },
            { key: 'a', header: t('comm.amount'), render: (a) => <span className="num">{fmt(a.amount)}</span> },
            { key: 'd', header: t('comm.date'), render: (a) => <span className="muted">{formatDate(a.accrualDate, lang)}</span> },
          ]} />
      </div>
    </Card>
  );
}

/** The invoice that stands now and the corrections made at the till (Phase 9). */
export function InvoiceCard({ d }: { d: RepairDetail }) {
  const { t, lang, pick } = useI18n();
  const inv = d.invoice;
  const corrections = d.corrections ?? [];
  if (!inv && corrections.length === 0) return null;
  return (
    <Card>
      <div className="stack">
        {inv ? (
          <>
            <div className="row"><strong>{t('correct.invoice')}</strong> <span className="ltr">{inv.header.number}</span><span className="spacer" /><strong className="num">{fmt(inv.header.grossTotal)}</strong></div>
            <ul>{inv.lines.map((l) => <li key={l.id}>{pick(l.descriptionEn, l.descriptionAr)} · <span className="num">{qty(l.qty)} × {fmt(l.unitPrice)} = {fmt(l.grossAmount)}</span></li>)}</ul>
          </>
        ) : <span className="muted">{t('correct.noInvoice')}</span>}
        {corrections.length > 0 && (
          <div><strong>{t('correct.history')}</strong>
            <ol className="timeline">
              {corrections.map((c) => (
                <li key={c.id}>
                  <span className="ltr">{c.creditedNumber}</span> · {t('correct.creditedBy', { cn: c.creditNoteNumber, credited: fmt(c.credited) })}
                  {c.correctedNumber && <> · {t('correct.reissued', { number: c.correctedNumber, gross: fmt(c.correctedGross ?? 0) })}</>}
                  <div className="muted" dir="auto">{c.reason} · {formatDateTime(c.createdAt, lang)} · {c.createdBy}</div>
                </li>
              ))}
            </ol>
          </div>
        )}
      </div>
    </Card>
  );
}
