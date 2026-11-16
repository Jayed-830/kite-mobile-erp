import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { can, type AccrualStatus, type CommissionAccrual, type PayableRow } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { businessToday, ErrorBox, formatDate, useApi, useAuth } from '../core';
import { fmt } from '../money';

type Tab = 'accrued' | 'payable' | 'all' | 'payouts' | 'statement' | 'performance' | 'usage';
const today = () => businessToday();
const monthStart = () => `${today().slice(0, 8)}01`;

const tone = (s: AccrualStatus) => (s === 'paid' ? 'success' : s === 'reversed' ? 'danger' : s === 'approved' ? 'primary' : 'default') as 'success' | 'danger' | 'primary' | 'default';

/** Technician commissions (blueprint 15): approve, pay, reverse, and the statement, performance and rule-usage reports. */
export function CommissionsPage() {
  const { me } = useAuth();
  const { t } = useI18n();
  const [tab, setTab] = useState<Tab>('accrued');
  if (!can(me, 'COMM.VIEW')) return <MyCommissionsPage />;
  const tabs: Tab[] = ['accrued', 'payable', 'all', 'payouts', 'statement', 'performance', 'usage'];
  return (
    <div className="stack">
      <PageHeader title={t('comm.title')}>
        {can(me, 'COMM.RULES') && <Link className="btn" to="/app/settings/commissions">{t('comm.rules')}</Link>}
      </PageHeader>
      <div className="tabs" role="tablist">
        {tabs.map((k) => <button key={k} role="tab" aria-selected={tab === k} className={`tab${tab === k ? ' active' : ''}`} onClick={() => setTab(k)}>{t(`comm.tabs.${k}`)}</button>)}
      </div>
      {tab === 'accrued' && <AccrualList status="accrued" selectable />}
      {tab === 'payable' && <Payable />}
      {tab === 'all' && <AccrualList />}
      {tab === 'payouts' && <Payouts />}
      {tab === 'statement' && <Statement />}
      {tab === 'performance' && <Performance />}
      {tab === 'usage' && <Usage />}
    </div>
  );
}

/** A technician's own commissions (COMM.OWN). */
export function MyCommissionsPage() {
  const api = useApi();
  const { t } = useI18n();
  const q = useQuery({ queryKey: ['myCommissions'], queryFn: () => api.myCommissions() });
  const total = (q.data ?? []).filter((a) => a.status !== 'paid').reduce((s, a) => s + a.amount, 0);
  return (
    <div className="stack">
      <PageHeader title={t('comm.mine')} />
      <ErrorBox error={q.error} />
      <div className="figures-row"><div className="fig"><span>{t('comm.closing')}</span><strong className="num ltr">{fmt(Math.round(total * 1000) / 1000)}</strong></div></div>
      {q.isLoading ? <Spinner label={t('app.loading')} /> : <Card><AccrualTable rows={q.data ?? []} /></Card>}
    </div>
  );
}

function AccrualTable({ rows, selected, onToggle, onReverse }: { rows: CommissionAccrual[]; selected?: Set<string>; onToggle?: (id: string) => void; onReverse?: (a: CommissionAccrual) => void }) {
  const { t, lang, pick } = useI18n();
  const [open, setOpen] = useState<string | null>(null);
  return (
    <DataTable rows={rows} rowKey={(a) => a.id} empty={t('comm.none')}
      columns={[
        ...(selected && onToggle ? [{ key: 'sel', header: '', render: (a: CommissionAccrual) => <input type="checkbox" aria-label={a.jobNumber} checked={selected.has(a.id)} onChange={() => onToggle(a.id)} /> }] : []),
        { key: 'd', header: t('comm.date'), render: (a) => <span className="muted">{formatDate(a.accrualDate, lang)}</span> },
        { key: 'j', header: t('comm.job'), render: (a) => <Link to={`/app/repairs/${a.jobId}`} className="ltr">{a.jobNumber}</Link> },
        { key: 't', header: t('comm.technician'), render: (a) => pick(a.technicianName, a.technicianNameAr ?? a.technicianName) },
        { key: 'k', header: t('app.status'), render: (a) => <span>{t(`comm.kinds.${a.kind}`)}{a.kindVersion > 1 ? ` · v${a.kindVersion}` : ''} <Badge tone={tone(a.status)}>{t(`comm.statuses.${a.status}`)}</Badge></span> },
        { key: 'r', header: t('comm.rule'), render: (a) => a.ruleNameEn ? <span>{pick(a.ruleNameEn, a.ruleNameAr ?? a.ruleNameEn)} <span className="muted">v{a.ruleVersion}</span></span> : <span className="muted" dir="auto">{a.reason ?? ''}</span> },
        { key: 'i', header: t('comm.invoice'), render: (a) => <span className="ltr">{a.invoiceNumber ?? a.payoutNumber ?? ''}</span> },
        { key: 'a', header: t('comm.amount'), render: (a) => <strong className="num">{fmt(a.amount)}</strong> },
        { key: 'x', header: '', render: (a) => (
          <span className="row">
            {a.kind === 'repair' && <Button size="sm" variant="ghost" onClick={() => setOpen(open === a.id ? null : a.id)}>{t('comm.trace')}</Button>}
            {onReverse && a.kind !== 'reversal' && a.status !== 'reversed' && <Button size="sm" variant="ghost" onClick={() => onReverse(a)}>{t('comm.reverse')}</Button>}
            {open === a.id && (
              <span className="muted">
                {t('comm.labour')} {fmt(a.basisAmounts.labour ?? 0)} · {t('comm.partsRevenue')} {fmt(a.basisAmounts.partsRevenue ?? 0)} · {t('comm.partsCost')} {fmt(a.basisAmounts.partsCost ?? 0)}
                {' · '}{t('comm.external')} {fmt(a.basisAmounts.external ?? 0)} · {t('comm.profit')} {fmt(a.basisAmounts.profit ?? 0)}{a.share != null ? ` · ${t('comm.share')} ${a.share}%` : ''}
              </span>
            )}
          </span>) },
      ]} />
  );
}

function AccrualList({ status, selectable }: { status?: AccrualStatus; selectable?: boolean }) {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const [filter, setFilter] = useState<AccrualStatus | ''>(status ?? '');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [reversing, setReversing] = useState<CommissionAccrual | null>(null);
  const [reason, setReason] = useState('');
  const [msg, setMsg] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const q = useQuery({ queryKey: ['commissions', { status: filter }], queryFn: () => api.commissions(filter ? { status: filter } : undefined) });
  const refresh = () => { setSelected(new Set()); void qc.invalidateQueries({ queryKey: ['commissions'] }); void qc.invalidateQueries({ queryKey: ['payable'] }); };
  const toggle = (id: string) => setSelected((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const approver = can(me, 'COMM.APPROVE');
  return (
    <div className="stack">
      <ErrorBox error={error ?? q.error} />
      {msg && <Alert tone="success">{msg}</Alert>}
      <div className="row">
        {!status && (
          <SelectField label={t('app.status')} value={filter} onChange={(e) => setFilter(e.target.value as AccrualStatus | '')}>
            <option value="">{t('comm.tabs.all')}</option>
            {(['accrued', 'approved', 'paid', 'reversed'] as const).map((s) => <option key={s} value={s}>{t(`comm.statuses.${s}`)}</option>)}
          </SelectField>
        )}
        <span className="spacer" />
        {approver && <Button onClick={() => api.generateCommissions().then((r) => { setMsg(t('comm.generated', { n: r.written })); refresh(); }).catch(setError)}>{t('comm.generate')}</Button>}
        {selectable && approver && (
          <Button variant="primary" disabled={selected.size === 0}
            onClick={() => api.approveCommissions([...selected]).then((r) => { setMsg(t('comm.approved', { n: r.approved })); refresh(); }).catch(setError)}>
            {t('comm.approveSelected', { n: selected.size })}
          </Button>
        )}
      </div>
      {q.isLoading ? <Spinner label={t('app.loading')} /> : (
        <Card><AccrualTable rows={q.data ?? []} selected={selectable && approver ? selected : undefined} onToggle={selectable && approver ? toggle : undefined}
          onReverse={approver ? (a) => { setReason(''); setReversing(a); } : undefined} /></Card>
      )}
      <Modal open={reversing != null} title={t('comm.reverseTitle')} onClose={() => setReversing(null)}
        footer={<Button variant="danger" disabled={!reason.trim()} onClick={() => api.reverseCommission(reversing!.id, reason.trim()).then(() => { setReversing(null); refresh(); }).catch(setError)}>{t('comm.reverse')}</Button>}>
        <div className="stack">
          {reversing?.status === 'paid' && <Alert tone="warning">{t('comm.reversedPaid')}</Alert>}
          <div>{reversing?.jobNumber} · {reversing ? pick(reversing.technicianName, reversing.technicianNameAr ?? reversing.technicianName) : ''} · <span className="num">{fmt(reversing?.amount ?? 0)}</span></div>
          <TextField label={t('comm.reverseReason')} value={reason} onChange={(e) => setReason(e.target.value)} dir="auto" />
        </div>
      </Modal>
    </div>
  );
}

function Payable() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const q = useQuery({ queryKey: ['payable'], queryFn: () => api.commissionsPayable() });
  const [paying, setPaying] = useState<PayableRow | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  return (
    <div className="stack">
      <ErrorBox error={q.error} />
      {msg && <Alert tone="success">{msg}</Alert>}
      <Card>
        <DataTable rows={q.data ?? []} rowKey={(r) => `${r.branchId}:${r.technicianId}`} empty={t('comm.none')}
          columns={[
            { key: 'b', header: t('parts.branch'), render: (r) => <span className="ltr">{r.branchCode}</span> },
            { key: 't', header: t('comm.technician'), render: (r) => pick(r.technicianName, r.technicianNameAr ?? r.technicianName) },
            { key: 'c', header: t('comm.count'), render: (r) => <span className="num">{r.count}</span> },
            { key: 'o', header: t('comm.oldest'), render: (r) => <span className="muted">{r.oldest}</span> },
            { key: 'a', header: t('comm.total'), render: (r) => <strong className="num">{fmt(r.total)}</strong> },
            { key: 'x', header: '', render: (r) => can(me, 'COMM.PAY') && r.total > 0 ? <Button size="sm" variant="primary" onClick={() => { setMsg(null); setPaying(r); }}>{t('comm.pay')}</Button> : null },
          ]} />
      </Card>
      {paying && <PayModal row={paying} onClose={() => setPaying(null)} onDone={(m) => { setPaying(null); setMsg(m); void qc.invalidateQueries({ queryKey: ['payable'] }); void qc.invalidateQueries({ queryKey: ['commissions'] }); void qc.invalidateQueries({ queryKey: ['payouts'] }); }} />}
    </div>
  );
}

function PayModal({ row, onClose, onDone }: { row: PayableRow; onClose: () => void; onDone: (message: string) => void }) {
  const api = useApi();
  const { t, pick } = useI18n();
  const q = useQuery({ queryKey: ['commissions', { status: 'approved', technicianId: row.technicianId, branchId: row.branchId }],
    queryFn: () => api.commissions({ status: 'approved', technicianId: row.technicianId, branchId: row.branchId }) });
  const [method, setMethod] = useState<'cash' | 'bank' | 'payroll'>('bank');
  const [reference, setReference] = useState('');
  const [error, setError] = useState<unknown>(null);
  const ids = (q.data ?? []).map((a) => a.id);
  return (
    <Modal open title={t('comm.payTitle', { name: pick(row.technicianName, row.technicianNameAr ?? row.technicianName) })} onClose={onClose}
      footer={<Button variant="primary" disabled={ids.length === 0}
        onClick={() => api.payCommissions({ branchId: row.branchId, technicianId: row.technicianId, accrualIds: ids, method, reference: reference.trim() || null })
          .then((r) => onDone(t('comm.paid', { number: r.number, total: fmt(r.total) }))).catch(setError)}>{t('comm.pay')} {fmt(row.total)}</Button>}>
      <div className="stack">
        <ErrorBox error={error ?? q.error} />
        <div>{t('comm.count')}: <span className="num">{ids.length}</span> · {t('comm.total')}: <strong className="num">{fmt(row.total)}</strong></div>
        <SelectField label={t('comm.payMethod')} value={method} onChange={(e) => setMethod(e.target.value as typeof method)}>
          {(['cash', 'bank', 'payroll'] as const).map((m) => <option key={m} value={m}>{t(`comm.methods.${m}`)}</option>)}
        </SelectField>
        <TextField label={t('comm.payReference')} value={reference} onChange={(e) => setReference(e.target.value)} className="ltr" />
      </div>
    </Modal>
  );
}

function Payouts() {
  const api = useApi();
  const { t, lang, pick } = useI18n();
  const q = useQuery({ queryKey: ['payouts'], queryFn: () => api.commissionPayouts() });
  return (
    <Card>
      <ErrorBox error={q.error} />
      <DataTable rows={q.data ?? []} rowKey={(p) => p.id} empty={t('comm.none')}
        columns={[
          { key: 'n', header: t('comm.number'), render: (p) => <span className="ltr">{p.number}</span> },
          { key: 'd', header: t('comm.date'), render: (p) => formatDate(p.payoutDate, lang) },
          { key: 't', header: t('comm.technician'), render: (p) => pick(p.technicianName, p.technicianNameAr ?? p.technicianName) },
          { key: 'm', header: t('comm.payMethod'), render: (p) => t(`comm.methods.${p.method}`) },
          { key: 'l', header: t('comm.lines'), render: (p) => <span className="num">{p.lines}</span> },
          { key: 'a', header: t('comm.total'), render: (p) => <strong className="num">{fmt(p.total)}</strong> },
          { key: 'b', header: t('comm.by'), render: (p) => <span className="muted">{p.createdBy}{p.reference ? ` · ${p.reference}` : ''}</span> },
        ]} />
    </Card>
  );
}

function Period({ from, to, onChange }: { from: string; to: string; onChange: (from: string, to: string) => void }) {
  const { t } = useI18n();
  return (
    <>
      <TextField label={t('comm.from')} type="date" className="ltr" value={from} onChange={(e) => onChange(e.target.value, to)} />
      <TextField label={t('comm.to')} type="date" className="ltr" value={to} onChange={(e) => onChange(from, e.target.value)} />
    </>
  );
}

function Statement() {
  const api = useApi();
  const { t, pick } = useI18n();
  const techs = useQuery({ queryKey: ['repairTechnicians'], queryFn: () => api.repairTechnicians() });
  const [tech, setTech] = useState('');
  const [[from, to], setPeriod] = useState([monthStart(), today()]);
  const q = useQuery({ queryKey: ['statement', tech, from, to], enabled: !!tech, queryFn: () => api.commissionStatement(tech, from, to) });
  return (
    <div className="stack">
      <div className="row">
        <SelectField label={t('comm.technician')} value={tech} onChange={(e) => setTech(e.target.value)}>
          <option value="">—</option>
          {(techs.data ?? []).map((u) => <option key={u.id} value={u.id}>{pick(u.nameEn, u.nameAr ?? u.nameEn)}</option>)}
        </SelectField>
        <Period from={from} to={to} onChange={(f, x) => setPeriod([f, x])} />
      </div>
      <ErrorBox error={q.error} />
      {q.data && (
        <>
          <div className="figures-row">
            <div className="fig"><span>{t('comm.opening')}</span><strong className="num ltr">{fmt(q.data.opening)}</strong></div>
            <div className="fig"><span>{t('comm.payouts')}</span><strong className="num ltr">{fmt(q.data.payouts.reduce((s, p) => s + p.total, 0))}</strong></div>
            <div className="fig"><span>{t('comm.closing')}</span><strong className="num ltr">{fmt(q.data.closing)}</strong></div>
          </div>
          <Card><AccrualTable rows={q.data.accruals} /></Card>
        </>
      )}
    </div>
  );
}

function Performance() {
  const api = useApi();
  const { t, pick } = useI18n();
  const [[from, to], setPeriod] = useState([monthStart(), today()]);
  const q = useQuery({ queryKey: ['performance', from, to], queryFn: () => api.technicianPerformance(from, to) });
  return (
    <div className="stack">
      <div className="row"><Period from={from} to={to} onChange={(f, x) => setPeriod([f, x])} /></div>
      <ErrorBox error={q.error} />
      <Card>
        <DataTable rows={q.data ?? []} rowKey={(r) => r.technicianId} empty={t('comm.none')}
          columns={[
            { key: 't', header: t('comm.technician'), render: (r) => pick(r.technicianName, r.technicianNameAr ?? r.technicianName) },
            { key: 'j', header: t('comm.jobs'), render: (r) => <span className="num">{r.jobs}</span> },
            { key: 'r', header: t('comm.revenue'), render: (r) => <span className="num">{fmt(r.revenue)}</span> },
            { key: 'h', header: t('comm.turnaround'), render: (r) => <span className="num">{r.avgTurnaroundHours ?? '—'}</span> },
            { key: 'w', header: t('comm.rework'), render: (r) => <span className="num">{r.rework}</span> },
            { key: 'c', header: t('comm.earned'), render: (r) => <strong className="num">{fmt(r.commission)}</strong> },
          ]} />
      </Card>
    </div>
  );
}

function Usage() {
  const api = useApi();
  const { t, pick } = useI18n();
  const q = useQuery({ queryKey: ['ruleUsage'], queryFn: () => api.commissionRuleUsage() });
  return (
    <Card>
      <ErrorBox error={q.error} />
      <DataTable rows={q.data ?? []} rowKey={(r) => `${r.ruleKey}:${r.version}`} empty={t('comm.none')}
        columns={[
          { key: 'n', header: t('comm.rule'), render: (r) => <span>{pick(r.nameEn, r.nameAr)} <span className="muted">v{r.version}</span></span> },
          { key: 'b', header: t('comm.basis'), render: (r) => <span>{t(`comm.bases.${r.basis}`)} <span className="num">{r.basis === 'fixed' ? fmt(r.amount ?? 0) : `${r.rate}%`}</span></span> },
          { key: 'c', header: t('comm.accruals'), render: (r) => <span className="num">{r.accruals}</span> },
          { key: 'a', header: t('comm.total'), render: (r) => <strong className="num">{fmt(r.total)}</strong> },
          { key: 'f', header: t('comm.first'), render: (r) => <span className="muted">{r.first}</span> },
          { key: 'l', header: t('comm.last'), render: (r) => <span className="muted">{r.last}</span> },
        ]} />
    </Card>
  );
}
