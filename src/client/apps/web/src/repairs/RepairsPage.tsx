import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { can, REPAIR_STATUSES, type RepairBoardRow, type RepairStatus } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Badge, Button, DataTable, PageHeader, SelectField, TextField } from '@kite/ui';
import { ErrorBox, formatDate, formatDateTime, useApi, useAuth } from '../core';
import { fmt } from '../money';
import { Ident } from '../devices/bits';
import { RepairStatusBadge } from './bits';

const LANES: { key: 'intake' | 'diagnosis' | 'approval' | 'parts' | 'workshop' | 'ready'; statuses: RepairStatus[] }[] = [
  { key: 'intake', statuses: ['received', 'inspection_pending'] },
  { key: 'diagnosis', statuses: ['under_diagnosis'] },
  { key: 'approval', statuses: ['waiting_approval'] },
  { key: 'parts', statuses: ['waiting_parts'] },
  { key: 'workshop', statuses: ['in_progress', 'testing', 'completed'] },
  { key: 'ready', statuses: ['ready', 'cancelled', 'unrepairable'] },
];

/** Repairs (13.1-13.6): the workshop board by stage, devices not collected, and every job. */
export function RepairsPage() {
  const { t } = useI18n();
  const { me } = useAuth();
  const [tab, setTab] = useState<'board' | 'uncollected' | 'all'>('board');
  return (
    <div className="stack">
      <PageHeader title={t('repair.title')}>
        <span className="spacer" />
        {can(me, 'REPAIR.INTAKE') && <Link className="btn btn-primary" to="/app/repairs/new">{t('repair.newJob')}</Link>}
      </PageHeader>
      <div className="tabs" role="tablist">
        {(['board', 'uncollected', 'all'] as const).map((k) => <button key={k} role="tab" aria-selected={tab === k} className={`tab${tab === k ? ' active' : ''}`} onClick={() => setTab(k)}>{t(`repair.${k}`)}</button>)}
      </div>
      {tab === 'board' ? <Board /> : tab === 'uncollected' ? <Uncollected /> : <AllJobs />}
    </div>
  );
}

function JobCard({ r }: { r: RepairBoardRow }) {
  const { t, lang, pick } = useI18n();
  return (
    <Link to={`/app/repairs/${r.id}`} className={`job-card${r.overdue ? ' overdue' : ''}`} data-testid={`job-${r.number}`}>
      <span className="row"><strong className="ltr">{r.number}</strong><span className="spacer" />{r.warrantyClaim && <Badge tone="warning">{t('repair.warrantyClaim')}</Badge>}</span>
      <span>{r.customerName}</span>
      <span className="muted">{r.device} · {pick(r.categoryEn, r.categoryAr)}</span>
      <span className="row"><RepairStatusBadge status={r.status} />{r.overdue && <Badge tone="danger">{t('repair.overdue')}</Badge>}</span>
      <span className="muted">{t('repair.promised')}: {formatDateTime(r.promisedAt, lang)}</span>
      <span className="muted">{r.technicianName ?? t('repair.unassigned')}{r.deposit > 0 ? ` · ${t('repair.deposit')} ${fmt(r.deposit)}` : ''}</span>
      {r.unreadMessages > 0 && <Badge tone="primary">{t('repair.unread', { n: r.unreadMessages })}</Badge>}
    </Link>
  );
}

function Board() {
  const api = useApi();
  const { t, pick } = useI18n();
  const [q, setQ] = useState('');
  const [tech, setTech] = useState('');
  const rows = useQuery({ queryKey: ['repairs', 'board', q, tech], queryFn: () => api.repairs({ open: true, q: q || undefined, technicianId: tech || undefined }), refetchInterval: 30_000 });
  const techs = useQuery({ queryKey: ['repairTechnicians'], queryFn: () => api.repairTechnicians() });
  return (
    <div className="stack">
      <div className="row">
        <TextField label={t('app.search')} placeholder={t('repair.search')} value={q} onChange={(e) => setQ(e.target.value)} dir="auto" />
        <SelectField label={t('repair.technician')} value={tech} onChange={(e) => setTech(e.target.value)}>
          <option value="">{t('repair.anyTechnician')}</option>
          {(techs.data ?? []).map((u) => <option key={u.id} value={u.id}>{pick(u.nameEn, u.nameAr ?? u.nameEn)}</option>)}
        </SelectField>
      </div>
      <ErrorBox error={rows.error} />
      <div className="board">
        {LANES.map((l) => {
          const mine = (rows.data ?? []).filter((r) => l.statuses.includes(r.status));
          return (
            <section key={l.key} className="lane" aria-label={t(`repair.lanes.${l.key}`)}>
              <div className="lane-head"><span>{t(`repair.lanes.${l.key}`)}</span><Badge>{mine.length}</Badge></div>
              {mine.map((r) => <JobCard key={r.id} r={r} />)}
            </section>
          );
        })}
      </div>
    </div>
  );
}

function Uncollected() {
  const api = useApi();
  const { t, lang } = useI18n();
  const rows = useQuery({ queryKey: ['repairs', 'uncollected'], queryFn: () => api.uncollected() });
  return (
    <>
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(r) => r.id} empty={t('repair.noJobs')}
        columns={[
          { key: 'n', header: t('repair.number'), render: (r) => <Link to={`/app/repairs/${r.id}`} className="ltr">{r.number}</Link> },
          { key: 'c', header: t('repair.detail.customer'), render: (r) => <span>{r.customerName} <span className="ltr muted">{r.customerPhone ?? ''}</span></span> },
          { key: 'd', header: t('repair.detail.device'), render: (r) => r.device },
          { key: 's', header: t('repair.statuses.ready'), render: (r) => <span>{formatDate(r.readySince, lang)} · <strong>{t('repair.days', { n: r.days })}</strong></span> },
          { key: 'r', header: '', render: (r) => r.remindedDays > 0 ? <span className="muted">{t('repair.remindedAt', { n: r.remindedDays })}</span> : null },
          { key: 'p', header: t('repair.deposit'), render: (r) => <span className="num">{fmt(r.deposit)}</span> },
        ]} />
    </>
  );
}

function AllJobs() {
  const api = useApi();
  const { t, lang } = useI18n();
  const [q, setQ] = useState('');
  const [status, setStatus] = useState<RepairStatus | ''>('');
  const rows = useQuery({ queryKey: ['repairs', 'all', q, status], queryFn: () => api.repairs({ q: q || undefined, status: status || undefined }) });
  return (
    <div className="stack">
      <div className="row">
        <TextField label={t('app.search')} placeholder={t('repair.search')} value={q} onChange={(e) => setQ(e.target.value)} dir="auto" />
        <SelectField label={t('app.status')} value={status} onChange={(e) => setStatus(e.target.value as RepairStatus | '')}>
          <option value="">{t('repair.all')}</option>
          {REPAIR_STATUSES.map((s) => <option key={s} value={s}>{t(`repair.statuses.${s}`)}</option>)}
        </SelectField>
        <span className="spacer" />
        <Button size="sm" variant="ghost" onClick={() => void rows.refetch()}>{t('app.retry')}</Button>
      </div>
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(r) => r.id} empty={t('repair.noJobs')}
        columns={[
          { key: 'n', header: t('repair.number'), render: (r) => <Link to={`/app/repairs/${r.id}`} className="ltr">{r.number}</Link> },
          { key: 's', header: t('app.status'), render: (r) => <RepairStatusBadge status={r.status} /> },
          { key: 'c', header: t('repair.detail.customer'), render: (r) => r.customerName },
          { key: 'd', header: t('repair.detail.device'), render: (r) => <span>{r.device} <Ident value={r.identifier} /></span> },
          { key: 'e', header: t('repair.estimate'), render: (r) => <span className="num">{fmt(r.estimate)}</span> },
          { key: 'w', header: t('repair.promised'), render: (r) => <span className={r.overdue ? 'neg' : 'muted'}>{formatDateTime(r.promisedAt, lang)}</span> },
        ]} />
    </div>
  );
}
