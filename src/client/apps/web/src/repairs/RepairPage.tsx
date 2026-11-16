import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { can, NEEDS_APPROVED_ESTIMATE, type EstimateLineInput, type PosItem, type RepairDetail, type RepairEstimate, type RepairStatus } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDate, formatDateTime, useApi, useAuth } from '../core';
import { fmt, qty } from '../money';
import { Ident } from '../devices/bits';
import { openPdf } from '../pos/TradeInWizard';
import { RepairStatusBadge, SignaturePad } from './bits';

type Tab = 'overview' | 'estimates' | 'work' | 'money' | 'messages' | 'history';

/** One repair job (13.2-13.6): where it stands and what can happen next, its estimates, parts and labour, money, messages and history. */
export function RepairPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, lang, pick } = useI18n();
  const [tab, setTab] = useState<Tab>('overview');
  const [error, setError] = useState<unknown>(null);
  const [moving, setMoving] = useState<RepairStatus | null>(null);
  const [note, setNote] = useState('');
  const [passcode, setPasscode] = useState<string | null>(null);
  const [claim, setClaim] = useState(false);
  const q = useQuery({ queryKey: ['repair', id], queryFn: () => api.repair(id) });
  const techs = useQuery({ queryKey: ['repairTechnicians'], queryFn: () => api.repairTechnicians() });
  const keep = (d: RepairDetail) => { qc.setQueryData(['repair', id], d); void qc.invalidateQueries({ queryKey: ['repairs'] }); };
  const move = useMutation({ mutationFn: () => api.moveRepair(id, moving!, note.trim() || null, q.data?.job.version), onSuccess: (d) => { keep(d); setMoving(null); setNote(''); }, onError: setError });
  if (q.isLoading) return <Spinner label={t('app.loading')} />;
  if (!q.data) return <ErrorBox error={q.error} />;
  const d = q.data;
  const j = d.job;
  const current = d.estimates[0] ?? null;
  const approved = current?.status === 'approved';
  const branchOk = (p: string) => can(me, p);

  return (
    <div className="stack">
      <PageHeader title={`${t('repair.one')} ${j.number}`}>
        <RepairStatusBadge status={j.status} />
        {j.overdue && <Badge tone="danger">{t('repair.overdue')}</Badge>}
        {j.warrantyOfId && <Badge tone="warning">{t('repair.detail.claimOf', { number: j.warrantyOfNumber ?? '' })}</Badge>}
        <span className="spacer" />
        <Button size="sm" onClick={() => void openPdf(api.repairDocument(j.id, 'job-card'))}>{t('repair.detail.jobCard')}</Button>
        <Button size="sm" onClick={() => void openPdf(api.repairDocument(j.id, 'tag'))}>{t('repair.detail.tag')}</Button>
        {j.status === 'delivered' && <Button size="sm" onClick={() => void openPdf(api.repairDocument(j.id, 'delivery-receipt'))}>{t('repair.detail.receipt')}</Button>}
      </PageHeader>
      <ErrorBox error={error} />
      {j.next.length > 0 && j.status !== 'ready' && (branchOk('REPAIR.WORK') || branchOk('REPAIR.CANCEL')) && (
        <Card>
          <div className="row">
            {j.next.filter((s) => s !== 'delivered').map((s) => {
              const blocked = NEEDS_APPROVED_ESTIMATE.includes(s) && !approved && !(current && current.total <= j.ceiling && current.lines.length > 0);
              return <Button key={s} variant={s === 'cancelled' || s === 'unrepairable' ? 'danger' : 'default'} disabled={blocked || (s === 'cancelled' && !branchOk('REPAIR.CANCEL'))}
                title={blocked ? t('repair.needsEstimate') : undefined} onClick={() => { setError(null); setMoving(s); }}>{t('repair.moveTo', { status: t(`repair.statuses.${s}`) })}</Button>;
            })}
          </div>
        </Card>
      )}
      {j.status === 'ready' && <Alert tone="info">{t('repair.money.tillHint')}</Alert>}
      <div className="tabs" role="tablist">
        {(['overview', 'estimates', 'work', 'money', 'messages', 'history'] as const).map((k) => <button key={k} role="tab" aria-selected={tab === k} className={`tab${tab === k ? ' active' : ''}`} onClick={() => setTab(k)}>{t(`repair.detail.tabs.${k}`)}</button>)}
      </div>

      {tab === 'overview' && (
        <div className="grid-2">
          <Card>
            <dl className="kv">
              <dt>{t('repair.detail.customer')}</dt><dd>{j.customerName} <span className="ltr muted">{j.customerPhone ?? ''}</span></dd>
              <dt>{t('repair.detail.device')}</dt><dd>{[j.brand, j.model, j.colour].filter(Boolean).join(' ')} <Ident value={j.imei1 ?? j.serial} /></dd>
              <dt>{t('repair.intake.category')}</dt><dd>{pick(j.categoryNameEn, j.categoryNameAr)}</dd>
              <dt>{t('repair.detail.problem')}</dt><dd dir="auto">{j.problem}</dd>
              {j.faultCodes.length > 0 && <><dt>{t('repair.intake.faultCodes')}</dt><dd className="ltr">{j.faultCodes.join(', ')}</dd></>}
              <dt>{t('repair.promised')}</dt><dd>{formatDateTime(j.promisedAt, lang)}</dd>
              <dt>{t('repair.intake.preAuthorised')}</dt><dd className="num">{fmt(j.preAuthorised)}</dd>
              <dt>{t('repair.detail.received', { date: formatDateTime(j.createdAt, lang), name: j.createdByName })}</dt><dd />
              {j.deliveredAt && <><dt>{t('repair.detail.collectedBy', { name: j.collectedBy ?? '', date: formatDateTime(j.deliveredAt, lang) })}</dt><dd /></>}
              {j.warrantyUntil && <><dt>{t('repair.detail.warrantyUntil', { date: formatDate(j.warrantyUntil, lang) })}</dt><dd /></>}
              {j.soldSaleNumber && <><dt>{t('repair.detail.soldSale', { number: j.soldSaleNumber })}</dt><dd /></>}
            </dl>
          </Card>
          <Card>
            <div className="stack">
              <SelectField label={t('repair.technician')} value={j.technicianId ?? ''} disabled={!branchOk('REPAIR.ASSIGN') || j.status === 'delivered'}
                onChange={(e) => api.assignRepair(j.id, e.target.value || null).then(keep).catch(setError)}>
                <option value="">{t('repair.unassigned')}</option>
                {(techs.data ?? []).map((u) => <option key={u.id} value={u.id}>{pick(u.nameEn, u.nameAr ?? u.nameEn)}</option>)}
              </SelectField>
              <div><strong>{t('repair.intake.condition')}</strong>
                <ul>{Object.entries(j.condition).map(([k, v]) => <li key={k}>{t(`repair.intake.checks.${k}`)}: {t(`repair.intake.values.${v}`)}</li>)}</ul>
                {j.conditionNotes && <p dir="auto">{j.conditionNotes}</p>}
              </div>
              <div><strong>{t('repair.intake.accessories')}</strong>: {[...j.accessories.items.map((a) => t(`repair.intake.items.${a}`)), j.accessories.other].filter(Boolean).join(', ') || t('app.none')}</div>
              <div className="row">
                <span>{t('repair.intake.access')}: {t(`repair.intake.accessModes.${j.passcodeMode}`)}</span>
                {j.passcodeMode === 'provided' && (passcode ? <strong className="ltr mono">{t('repair.detail.passcodeShown', { code: passcode })}</strong>
                  : j.passcodeHeld && branchOk('REPAIR.CREDENTIAL.VIEW') ? <Button size="sm" onClick={() => api.revealPasscode(j.id).then((r) => setPasscode(r.passcode)).catch(setError)}>{t('repair.detail.passcode')}</Button>
                  : <span className="muted">{j.passcodePurgedAt ? t('repair.detail.passcodeGone') : t('repair.detail.noPasscode')}</span>)}
              </div>
              <div className="muted">{t('repair.intake.photos')}: {d.photos.filter((p) => p.stage !== 'terms_signature').length}</div>
              {d.claims.length > 0 && <div>{t('repair.detail.claims')}: {d.claims.map((c) => <Link key={c.id} to={`/app/repairs/${c.id}`} className="ltr"> {c.number}</Link>)}</div>}
              {j.status === 'delivered' && j.outcome === 'repaired' && !j.warrantyOfId && j.warrantyUntil && j.warrantyUntil >= new Date().toISOString().slice(0, 10) && branchOk('REPAIR.INTAKE') &&
                <div><Button onClick={() => setClaim(true)}>{t('repair.detail.claim')}</Button></div>}
            </div>
          </Card>
        </div>
      )}
      {tab === 'estimates' && <Estimates d={d} onDone={keep} onError={setError} />}
      {tab === 'work' && <Work d={d} onDone={keep} onError={setError} />}
      {tab === 'money' && <Money d={d} />}
      {tab === 'messages' && <Messages jobId={j.id} />}
      {tab === 'history' && (
        <div className="grid-2">
          <Card><strong>{t('repair.history.status')}</strong>
            <ol className="timeline">{d.history.map((h, i) => <li key={i}><RepairStatusBadge status={h.to} /> <span className="muted">{formatDateTime(h.at, lang)} · {t('repair.history.by', { name: h.userName ?? '—' })}</span>{h.note && <div dir="auto">{h.note}</div>}</li>)}</ol>
          </Card>
          <Card><strong>{t('repair.history.assignments')}</strong>
            <ol className="timeline">{d.assignments.map((a, i) => <li key={i}>{a.fromName ?? '—'} → {a.toName ?? t('repair.unassigned')} <span className="muted">{formatDateTime(a.at, lang)} · {t('repair.history.by', { name: a.byName ?? '—' })}</span></li>)}</ol>
          </Card>
        </div>
      )}

      <Modal open={moving != null} title={moving ? t('repair.moveTo', { status: t(`repair.statuses.${moving}`) }) : ''} onClose={() => setMoving(null)}
        footer={<><Button onClick={() => setMoving(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={move.isPending} onClick={() => move.mutate()}>{t('app.save')}</Button></>}>
        <ErrorBox error={move.error} />
        <TextField label={t('repair.moveNote')} value={note} onChange={(e) => setNote(e.target.value)} dir="auto" />
      </Modal>
      {claim && <ClaimModal jobId={j.id} onClose={() => setClaim(false)} />}
    </div>
  );
}

function ClaimModal({ jobId, onClose }: { jobId: string; onClose: () => void }) {
  const api = useApi();
  const { t } = useI18n();
  const [problem, setProblem] = useState('');
  const [name, setName] = useState('');
  const [sig, setSig] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [created, setCreated] = useState<RepairDetail | null>(null);
  return (
    <Modal open title={t('repair.detail.claimTitle')} onClose={onClose}
      footer={created ? <Link className="btn btn-primary" to={`/app/repairs/${created.job.id}`} onClick={onClose}>{t('repair.intake.open')}</Link>
        : <Button variant="primary" onClick={() => api.warrantyClaim(jobId, { problem, passcodeMode: 'none', termsName: name, termsSignature: sig ?? '' }).then(setCreated).catch(setError)}>{t('repair.intake.create')}</Button>}>
      <ErrorBox error={error} />
      {created ? <Alert tone="success">{t('repair.intake.created', { number: created.job.number })}</Alert> : (
        <div className="stack">
          <p className="muted">{t('repair.detail.claimHelp')}</p>
          <TextField label={t('repair.intake.problem')} value={problem} onChange={(e) => setProblem(e.target.value)} dir="auto" />
          <TextField label={t('repair.intake.termsName')} value={name} onChange={(e) => setName(e.target.value)} dir="auto" />
          <SignaturePad label={t('repair.intake.sign')} onChange={setSig} />
        </div>
      )}
    </Modal>
  );
}

interface Draft { kind: 'labour' | 'part' | 'extra'; variantId: string; minutes?: number; qty?: number; unitPrice?: number | null; descriptionEn?: string; label: string }

function ItemSearch({ onPick }: { onPick: (i: PosItem) => void }) {
  const api = useApi();
  const { t, pick } = useI18n();
  const [q, setQ] = useState('');
  const [found, setFound] = useState<PosItem[]>([]);
  useEffect(() => {
    if (q.trim().length < 2) { setFound([]); return; }
    const h = setTimeout(() => { api.posItems(q, undefined, 20).then((xs) => setFound(xs.filter((x) => x.productType !== 'device' && x.productType !== 'service'))).catch(() => setFound([])); }, 200);
    return () => clearTimeout(h);
  }, [q, api]);
  return (
    <div className="stack">
      <TextField label={t('repair.work.part')} value={q} onChange={(e) => setQ(e.target.value)} dir="auto" />
      {found.length > 0 && <div className="picker-list">{found.map((i) => <button key={i.variantId} type="button" className="picker-item" onClick={() => { onPick(i); setQ(''); }}>{pick(i.descriptionEn, i.descriptionAr)} <span className="ltr muted">{i.sku}</span> <span className="num">{fmt(i.listPrice)}</span></button>)}</div>}
    </div>
  );
}

function Estimates({ d, onDone, onError }: { d: RepairDetail; onDone: (d: RepairDetail) => void; onError: (e: unknown) => void }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, lang, pick } = useI18n();
  const setup = useQuery({ queryKey: ['repairSetup'], queryFn: () => api.repairSetup() });
  const [lines, setLines] = useState<Draft[] | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [note, setNote] = useState('');
  const [approving, setApproving] = useState<RepairEstimate | null>(null);
  const [declining, setDeclining] = useState<RepairEstimate | null>(null);
  const [reason, setReason] = useState('');
  const j = d.job;
  const svc = (sku: string) => setup.data?.services.find((s) => s.sku === sku)?.id ?? '';
  const current = d.estimates[0] ?? null;
  const canWork = can(me, 'REPAIR.WORK') && !j.outcome && j.status !== 'delivered' && !j.warrantyOfId;
  const start = (e: RepairEstimate | null) => {
    setEditing(e && e.status === 'draft' ? e.id : null);
    setNote(e?.note ?? '');
    setLines(e ? e.lines.map((l) => ({ kind: l.kind, variantId: l.variantId, minutes: l.minutes ?? undefined, qty: l.qty, unitPrice: l.kind === 'labour' ? null : l.unitPrice, descriptionEn: l.descriptionEn, label: pick(l.descriptionEn, l.descriptionAr) })) : []);
  };
  const save = () => {
    const ls: EstimateLineInput[] = (lines ?? []).map((l) => ({ kind: l.kind, variantId: l.variantId, minutes: l.kind === 'labour' ? l.minutes ?? null : null, qty: l.kind === 'labour' ? 1 : l.qty ?? 1, unitPrice: l.unitPrice ?? null, descriptionEn: l.kind === 'extra' ? l.descriptionEn ?? null : null }));
    (editing ? api.updateEstimate(j.id, editing, ls, note || null) : api.writeEstimate(j.id, ls, note || null)).then((r) => { onDone(r); setLines(null); }).catch(onError);
  };
  const set = (i: number, p: Partial<Draft>) => setLines((ls) => (ls ?? []).map((l, k) => (k === i ? { ...l, ...p } : l)));
  return (
    <div className="stack">
      {j.preAuthorised > 0 && <Alert tone="info">{t('repair.estimates.withinCeiling', { amount: fmt(j.ceiling) })}</Alert>}
      {lines ? (
        <Card>
          <div className="stack">
            {lines.map((l, i) => (
              <div key={i} className="row">
                <span className="grow">{l.label}</span>
                {l.kind === 'labour' && <TextField label={t('repair.estimates.minutes')} inputMode="numeric" className="ltr num" value={String(l.minutes ?? '')} onChange={(e) => set(i, { minutes: Number(e.target.value) || undefined })} />}
                {l.kind === 'part' && <TextField label={t('repair.estimates.qty')} inputMode="decimal" className="ltr num" value={String(l.qty ?? 1)} onChange={(e) => set(i, { qty: Number(e.target.value) || undefined })} />}
                {l.kind === 'extra' && <TextField label={t('repair.estimates.item')} value={l.descriptionEn ?? ''} onChange={(e) => set(i, { descriptionEn: e.target.value })} dir="auto" />}
                {l.kind !== 'labour' && <TextField label={t('repair.estimates.price')} inputMode="decimal" className="ltr num" value={l.unitPrice == null ? '' : String(l.unitPrice)} onChange={(e) => set(i, { unitPrice: e.target.value === '' ? null : Number(e.target.value) })} />}
                <Button size="sm" variant="ghost" onClick={() => setLines(lines.filter((_, k) => k !== i))} aria-label={t('app.cancel')}>✕</Button>
              </div>
            ))}
            <div className="row">
              <Button size="sm" onClick={() => setLines([...lines, { kind: 'labour', variantId: svc('REPAIR-LABOUR'), minutes: 30, label: t('repair.estimates.addLabour') }])}>{t('repair.estimates.addLabour')}</Button>
              <Button size="sm" onClick={() => setLines([...lines, { kind: 'extra', variantId: svc('REPAIR-EXTRA'), unitPrice: null, descriptionEn: '', label: t('repair.estimates.addExtra') }])}>{t('repair.estimates.addExtra')}</Button>
            </div>
            <ItemSearch onPick={(i) => setLines([...lines, { kind: 'part', variantId: i.variantId, qty: 1, unitPrice: i.listPrice, label: pick(i.descriptionEn, i.descriptionAr) }])} />
            <TextField label={t('repair.estimates.note')} value={note} onChange={(e) => setNote(e.target.value)} dir="auto" />
            <div className="row"><Button onClick={() => setLines(null)}>{t('app.cancel')}</Button><span className="spacer" /><Button variant="primary" onClick={save} disabled={lines.length === 0}>{t('repair.estimates.save')}</Button></div>
          </div>
        </Card>
      ) : canWork && (
        <div className="row">
          {(!current || current.status === 'declined' || current.status === 'approved') && <Button variant="primary" onClick={() => start(current?.status === 'approved' ? null : current)}>{current ? t('repair.estimates.newVersion') : t('repair.estimates.write')}</Button>}
          {current?.status === 'draft' && <Button onClick={() => start(current)}>{t('app.edit')}</Button>}
        </div>
      )}
      {d.estimates.length === 0 && <p className="muted">{t('repair.estimates.none')}</p>}
      {d.estimates.map((e) => (
        <Card key={e.id}>
          <div className="stack">
            <div className="row">
              <strong>{t('repair.estimates.version', { n: e.estimateNo })}</strong>
              <Badge tone={e.status === 'approved' ? 'success' : e.status === 'declined' ? 'danger' : e.status === 'sent' ? 'warning' : 'default'}>{t(`repair.estimates.statuses.${e.status}`)}</Badge>
              {e.approvedAt && <span className="muted">{t('repair.estimates.approvedBy', { date: formatDateTime(e.approvedAt, lang), method: t(`repair.estimates.methods.${e.approvalMethod}`) })}{e.approverName ? ` · ${e.approverName}` : ''}</span>}
              {e.sentAt && e.status === 'sent' && <span className="muted">{t('repair.estimates.sent', { date: formatDateTime(e.sentAt, lang) })}</span>}
              {e.declineReason && <span className="muted">{e.declineReason}</span>}
              <span className="spacer" />
              {canWork && e === current && (e.status === 'draft' || e.status === 'sent') && (
                <>
                  {e.status === 'draft' && <Button size="sm" onClick={() => api.sendEstimate(j.id, e.id).then(onDone).catch(onError)}>{t('repair.estimates.send')}</Button>}
                  <Button size="sm" variant="primary" onClick={() => setApproving(e)}>{t('repair.estimates.approve')}</Button>
                  <Button size="sm" variant="ghost" onClick={() => { setReason(''); setDeclining(e); }}>{t('repair.estimates.decline')}</Button>
                </>
              )}
            </div>
            <DataTable rows={e.lines} rowKey={(l) => l.id} empty="—"
              columns={[
                { key: 'd', header: t('repair.estimates.item'), render: (l) => <span>{pick(l.descriptionEn, l.descriptionAr)}{l.minutes ? <span className="muted"> · {l.minutes} {t('repair.estimates.minutes')}</span> : null}</span> },
                { key: 'q', header: t('repair.estimates.qty'), render: (l) => <span className="num">{qty(l.qty)}</span> },
                { key: 'p', header: t('repair.estimates.price'), render: (l) => <span className="num">{fmt(l.unitPrice)}</span> },
                { key: 'v', header: t('repair.estimates.vat'), render: (l) => <span className="num">{fmt(l.taxAmount)}</span> },
                { key: 't', header: t('repair.estimates.total'), render: (l) => <span className="num">{fmt(l.grossAmount)}</span> },
              ]} />
            <div className="row"><span className="spacer" /><strong>{t('repair.estimates.total')}: <span className="num">{fmt(e.total)}</span></strong></div>
          </div>
        </Card>
      ))}
      {approving && <ApproveModal d={d} e={approving} onClose={() => setApproving(null)} onDone={(r) => { onDone(r); setApproving(null); }} />}
      <Modal open={declining != null} title={t('repair.estimates.decline')} onClose={() => setDeclining(null)}
        footer={<Button variant="danger" onClick={() => api.declineEstimate(j.id, declining!.id, reason).then((r) => { onDone(r); setDeclining(null); }).catch(onError)}>{t('app.save')}</Button>}>
        <TextField label={t('repair.estimates.declineReason')} value={reason} onChange={(e) => setReason(e.target.value)} dir="auto" />
      </Modal>
    </div>
  );
}

function ApproveModal({ d, e, onClose, onDone }: { d: RepairDetail; e: RepairEstimate; onClose: () => void; onDone: (d: RepairDetail) => void }) {
  const api = useApi();
  const { t } = useI18n();
  const within = e.total <= d.job.ceiling;
  const [method, setMethod] = useState<'signature' | 'phone' | 'pre_authorised'>(within ? 'pre_authorised' : 'signature');
  const [name, setName] = useState(d.job.customerName);
  const [phone, setPhone] = useState(d.job.customerPhone ?? '');
  const [sig, setSig] = useState<string | null>(null);
  const [then, setThen] = useState<RepairStatus | ''>(d.job.next.includes('in_progress') ? 'in_progress' : '');
  const [error, setError] = useState<unknown>(null);
  return (
    <Modal open title={`${t('repair.estimates.approve')} · ${t('repair.estimates.version', { n: e.estimateNo })} · ${fmt(e.total)}`} onClose={onClose}
      footer={<Button variant="primary" onClick={() => api.approveEstimate(d.job.id, e.id, { method, approverName: method === 'pre_authorised' ? null : name, phone: method === 'phone' ? phone : null, signature: method === 'signature' ? sig : null, then: then || null }).then(onDone).catch(setError)}>{t('repair.estimates.approve')}</Button>}>
      <div className="stack">
        <ErrorBox error={error} />
        <SelectField label={t('repair.estimates.method')} value={method} onChange={(x) => setMethod(x.target.value as typeof method)}>
          <option value="signature">{t('repair.estimates.methods.signature')}</option>
          <option value="phone">{t('repair.estimates.methods.phone')}</option>
          {within && <option value="pre_authorised">{t('repair.estimates.methods.pre_authorised')}</option>}
        </SelectField>
        {method !== 'pre_authorised' && <TextField label={t('repair.estimates.approver')} value={name} onChange={(x) => setName(x.target.value)} dir="auto" />}
        {method === 'phone' && <TextField label={t('repair.estimates.phoneCalled')} value={phone} onChange={(x) => setPhone(x.target.value)} className="ltr" />}
        {method === 'signature' && <SignaturePad label={t('repair.intake.sign')} onChange={setSig} />}
        <SelectField label={t('repair.estimates.thenMove')} value={then} onChange={(x) => setThen(x.target.value as RepairStatus | '')}>
          <option value="">—</option>
          {d.job.next.filter((s) => s === 'in_progress' || s === 'waiting_parts').map((s) => <option key={s} value={s}>{t(`repair.statuses.${s}`)}</option>)}
        </SelectField>
      </div>
    </Modal>
  );
}

function Work({ d, onDone, onError }: { d: RepairDetail; onDone: (d: RepairDetail) => void; onError: (e: unknown) => void }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, lang, pick } = useI18n();
  const [part, setPart] = useState<PosItem | null>(null);
  const [partQty, setPartQty] = useState('1');
  const [minutes, setMinutes] = useState('');
  const [writeOff, setWriteOff] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const j = d.job;
  const canWork = can(me, 'REPAIR.WORK') && !j.outcome && j.status !== 'delivered';
  return (
    <div className="stack">
      {canWork && (
        <div className="grid-2">
          <Card><div className="stack">
            <strong>{t('repair.work.fit')}</strong>
            {part ? <div className="row"><span className="grow">{pick(part.descriptionEn, part.descriptionAr)}</span><Button size="sm" variant="ghost" onClick={() => setPart(null)}>✕</Button></div> : <ItemSearch onPick={setPart} />}
            <TextField label={t('repair.work.qty')} inputMode="decimal" className="ltr num" value={partQty} onChange={(e) => setPartQty(e.target.value)} />
            <div><Button variant="primary" disabled={!part} onClick={() => api.fitPart(j.id, { variantId: part!.variantId, qty: Number(partQty) }).then((r) => { onDone(r); setPart(null); setPartQty('1'); }).catch(onError)}>{t('repair.work.fit')}</Button></div>
          </div></Card>
          <Card><div className="stack">
            <strong>{t('repair.work.labour')}</strong>
            <TextField label={t('repair.work.minutes')} inputMode="numeric" className="ltr num" value={minutes} onChange={(e) => setMinutes(e.target.value)} />
            <div><Button variant="primary" disabled={!minutes} onClick={() => api.logLabour(j.id, { minutes: Number(minutes) }).then((r) => { onDone(r); setMinutes(''); }).catch(onError)}>{t('repair.work.labour')}</Button></div>
          </div></Card>
        </div>
      )}
      <DataTable rows={d.work} rowKey={(w) => w.id} empty={t('repair.work.none')}
        columns={[
          { key: 'w', header: t('repair.work.part'), render: (w) => w.kind === 'labour' ? <span>{t('repair.work.labour')} · {w.minutes} {t('repair.work.minutes')} · {w.technicianName}</span> : <span>{pick(w.nameEn ?? '', w.nameAr ?? '')} <span className="ltr muted">{w.sku}</span>{w.writtenOff && <Badge tone="danger">{t('repair.work.writtenOff')}</Badge>}</span> },
          { key: 'q', header: t('repair.work.qty'), render: (w) => w.qty == null ? '' : <span className="num">{qty(w.qty)}</span> },
          { key: 'c', header: t('repair.work.cost'), render: (w) => w.cost == null ? '' : <span className="num">{fmt(w.cost)}</span> },
          { key: 'l', header: t('repair.work.left'), render: (w) => w.qtyLeft == null ? '' : <span className="num">{qty(w.qtyLeft)} · {fmt(w.costLeft)}</span> },
          { key: 'a', header: '', render: (w) => <span className="muted">{formatDateTime(w.createdAt, lang)}</span> },
          { key: 'x', header: '', render: (w) => canWork && w.kind === 'part' && !w.returnOfId && !w.writtenOff && (w.qtyLeft ?? 0) > 0 ? (
            <span className="row">
              <Button size="sm" onClick={() => api.returnPart(j.id, w.id, w.qtyLeft!).then(onDone).catch(onError)}>{t('repair.work.returnPart')}</Button>
              <Button size="sm" variant="ghost" onClick={() => { setReason(''); setWriteOff(w.id); }}>{t('repair.work.writeOff')}</Button>
            </span>) : null },
        ]} />
      <div className="muted">{t('repair.work.wip')}: <span className="num">{fmt(d.money.wip)}</span></div>
      <Modal open={writeOff != null} title={t('repair.work.writeOff')} onClose={() => setWriteOff(null)}
        footer={<Button variant="danger" onClick={() => api.writeOffPart(j.id, writeOff!, reason).then((r) => { onDone(r); setWriteOff(null); }).catch(onError)}>{t('repair.work.writeOff')}</Button>}>
        <TextField label={t('repair.work.writeOffReason')} value={reason} onChange={(e) => setReason(e.target.value)} dir="auto" />
      </Modal>
    </div>
  );
}

function Money({ d }: { d: RepairDetail }) {
  const { t, lang } = useI18n();
  const m = d.money;
  return (
    <div className="stack">
      <Alert tone="info">{t('repair.money.tillHint')}</Alert>
      {m.depositVatPoint && <p className="muted">{t('repair.money.vatInDeposit')}</p>}
      <div className="figures-row">
        <div className="fig"><span>{t('repair.money.balance')}</span><strong className="num">{fmt(m.deposit)}</strong></div>
        {d.job.outcome && <div className="fig"><span>{t('repair.money.charges')}</span><strong className="num">{fmt(m.chargesTotal)}</strong></div>}
        {d.job.outcome && <div className="fig"><span>{t('repair.money.due')}</span><strong className="num">{fmt(m.due)}</strong></div>}
        {d.job.outcome && m.excess > 0 && <div className="fig"><span>{t('repair.money.excess')}</span><strong className="num">{fmt(m.excess)}</strong></div>}
      </div>
      <DataTable rows={d.deposits} rowKey={(x) => x.id} empty={t('repair.money.noDeposits')}
        columns={[
          { key: 'n', header: '#', render: (x) => <span className="ltr">{x.number}</span> },
          { key: 'k', header: t('app.status'), render: (x) => t(`repair.money.kinds.${x.kind}`) },
          { key: 'a', header: t('repair.till.amount'), render: (x) => <span className="num">{fmt(x.amount)}</span> },
          { key: 'm', header: t('repair.till.method'), render: (x) => x.method ? t(`repair.till.methods.${x.method}`) : (x.saleNumber ?? '') },
          { key: 'w', header: '', render: (x) => <span className="muted">{formatDateTime(x.createdAt, lang)} · {x.createdBy}</span> },
        ]} />
    </div>
  );
}

function Messages({ jobId }: { jobId: string }) {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const [text, setText] = useState('');
  const q = useQuery({ queryKey: ['repairMessages', jobId], queryFn: () => api.repairMessages(jobId), refetchInterval: 15_000 });
  const send = useMutation({ mutationFn: () => api.sendRepairMessage(jobId, text), onSuccess: (r) => { qc.setQueryData(['repairMessages', jobId], r); setText(''); } });
  const m = q.data;
  return (
    <div className="stack">
      <ErrorBox error={q.error ?? send.error} />
      {m && !m.optIn && <Alert tone="warning">{t('repair.messages.noConsent')}</Alert>}
      <div className="chat" aria-live="polite">
        {(m?.messages ?? []).length === 0 && <span className="muted">{t('repair.messages.none')}</span>}
        {(m?.messages ?? []).map((x) => (
          <div key={x.id} className={`msg ${x.direction === 'out' ? 'msg-out' : 'msg-in'}`} dir="auto">
            {x.text}
            <div className="msg-meta">{formatDateTime(x.at, lang)}{x.direction === 'out' && x.status ? ` · ${t(`repair.messages.status.${x.status}`)}` : ''}{x.byName ? ` · ${x.byName}` : ''}</div>
          </div>
        ))}
      </div>
      {m && (m.canReply && can(me, 'NOTIF.REPLY') ? (
        <div className="row">
          <TextField label={t('repair.messages.reply')} help={m.replyUntil ? t('repair.messages.window', { time: formatDateTime(m.replyUntil, lang) }) : undefined} value={text} onChange={(e) => setText(e.target.value)} dir="auto" className="grow" />
          <Button variant="primary" disabled={!text.trim()} busy={send.isPending} onClick={() => send.mutate()}>{t('repair.messages.send')}</Button>
        </div>
      ) : <p className="muted">{t('repair.messages.closed')}</p>)}
    </div>
  );
}
