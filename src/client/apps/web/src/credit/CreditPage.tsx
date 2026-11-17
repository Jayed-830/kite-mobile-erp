import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { can, type CreditAccountRow, type CreditStatus, type Customer } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDate, formatDateTime, useApi, useAuth } from '../core';
import { fmt, parseAmount } from '../money';

type Tab = 'accounts' | 'aging' | 'exposure' | 'overrides' | 'checks';
export const statusTone = (s: CreditStatus) => (s === 'active' ? 'success' : s === 'hold' ? 'danger' : 'default') as 'success' | 'danger' | 'default';

/** Customer credit (blueprint 14): accounts and the ageing, exposure, approvals and breaches, and ledger-check reports. */
export function CreditPage() {
  const { me } = useAuth();
  const { t } = useI18n();
  const [tab, setTab] = useState<Tab>('accounts');
  const [opening, setOpening] = useState(false);
  const tabs: Tab[] = ['accounts', 'aging', 'exposure', 'overrides', 'checks'];
  return (
    <div className="stack">
      <PageHeader title={t('credit.title')}>
        {can(me, 'CREDIT.MANAGE') && <Link className="btn" to="/app/settings/credit">{t('credit.settingsTitle')}</Link>}
        {can(me, 'CREDIT.MANAGE') && me?.serverRole !== 'branch' && <Button variant="primary" onClick={() => setOpening(true)}>{t('credit.open')}</Button>}
      </PageHeader>
      <div className="tabs" role="tablist">
        {tabs.map((k) => <button key={k} role="tab" aria-selected={tab === k} className={`tab${tab === k ? ' active' : ''}`} onClick={() => setTab(k)}>{t(`credit.tabs.${k}`)}</button>)}
      </div>
      {tab === 'accounts' && <Accounts />}
      {tab === 'aging' && <AgingReport />}
      {tab === 'exposure' && <Exposure />}
      {tab === 'overrides' && <Overrides />}
      {tab === 'checks' && <LedgerChecks />}
      {opening && <OpenAccount onClose={() => setOpening(false)} />}
    </div>
  );
}

function Accounts() {
  const api = useApi();
  const { t, pick } = useI18n();
  const [q, setQ] = useState('');
  const [status, setStatus] = useState<CreditStatus | ''>('');
  const list = useQuery({ queryKey: ['creditAccounts', q, status], queryFn: () => api.creditAccounts({ q: q.trim() || undefined, status: status || undefined }) });
  return (
    <div className="stack">
      <div className="row">
        <TextField label={t('credit.search')} value={q} onChange={(e) => setQ(e.target.value)} />
        <SelectField label={t('credit.status')} value={status} onChange={(e) => setStatus(e.target.value as CreditStatus | '')}>
          <option value="">{t('credit.all')}</option>
          {(['active', 'hold', 'closed'] as const).map((s) => <option key={s} value={s}>{t(`credit.statuses.${s}`)}</option>)}
        </SelectField>
      </div>
      <ErrorBox error={list.error} />
      {list.isLoading ? <Spinner label={t('app.loading')} /> : (
        <Card>
          <DataTable<CreditAccountRow> rows={list.data ?? []} rowKey={(r) => r.id} empty={t('credit.none')}
            columns={[
              { key: 'c', header: t('credit.customer'), render: (r) => <Link to={`/app/credit/${r.customerId}`}>{pick(r.nameEn, r.nameAr ?? r.nameEn)}</Link> },
              { key: 'n', header: t('credit.number'), render: (r) => <span className="ltr muted">{r.customerNumber}</span> },
              { key: 'l', header: t('credit.limit'), render: (r) => <span className="num">{fmt(r.creditLimit)}</span> },
              { key: 'o', header: t('credit.owed'), render: (r) => <strong className="num">{fmt(r.exposure)}</strong> },
              { key: 'a', header: t('credit.available'), render: (r) => <span className="num">{fmt(r.available)}</span> },
              { key: 'u', header: t('credit.utilisation'), render: (r) => r.utilisation == null ? '—' : (
                <span className="meter" title={`${r.utilisation}%`}><span className={`meter-fill${r.utilisation > 100 ? ' over' : r.utilisation > 80 ? ' warn' : ''}`} style={{ width: `${Math.min(100, r.utilisation)}%` }} /><small className="num">{r.utilisation}%</small></span>) },
              { key: 'd', header: t('credit.overdueDays'), render: (r) => r.overdueDays > 0 ? <Badge tone={r.overdueDays > 30 ? 'danger' : 'warning'}>{r.overdueDays}</Badge> : <span className="muted">0</span> },
              { key: 's', header: t('credit.status'), render: (r) => <span><Badge tone={statusTone(r.status)}>{t(`credit.statuses.${r.status}`)}</Badge>{r.holdKind && <small className="muted"> · {t(`credit.holdKinds.${r.holdKind}`)}</small>}</span> },
            ]} />
        </Card>
      )}
    </div>
  );
}

/** Head office opens an account: a limit, terms, the offline cap and what each branch may sell offline. */
function OpenAccount({ onClose }: { onClose: () => void }) {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const [search, setSearch] = useState('');
  const [customer, setCustomer] = useState<Customer | null>(null);
  const [limit, setLimit] = useState('');
  const [terms, setTerms] = useState('30');
  const [cap, setCap] = useState('');
  const [note, setNote] = useState('');
  const [allow, setAllow] = useState<Record<string, string>>({});
  const [error, setError] = useState<unknown>(null);
  const found = useQuery({ queryKey: ['creditCustomerSearch', search], enabled: search.trim().length >= 2, queryFn: () => api.customers(search.trim()) });
  const branches = (me?.branches ?? []).filter((b) => b.status === 'active');
  const total = Object.values(allow).reduce((s, v) => s + (parseAmount(v) ?? 0), 0);
  const valid = customer && parseAmount(limit) != null && /^\d{1,3}$/.test(terms);
  const save = () => api.openCreditAccount({
    customerId: customer!.id, creditLimit: parseAmount(limit)!, termsDays: Number(terms), offlineSaleCap: cap.trim() ? parseAmount(cap) : null, note: note.trim() || null,
    allowances: branches.map((b) => ({ branchId: b.id, amount: parseAmount(allow[b.id] ?? '') ?? 0 })).filter((a) => a.amount > 0),
  }).then(() => { void qc.invalidateQueries({ queryKey: ['creditAccounts'] }); onClose(); }).catch(setError);
  return (
    <Modal open title={t('credit.openTitle')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={!valid} onClick={save}>{t('credit.save')}</Button></>}>
      <div className="stack">
        {customer ? (
          <div className="row"><strong>{pick(customer.nameEn, customer.nameAr ?? customer.nameEn)}</strong> <span className="ltr muted">{customer.number}</span><span className="spacer" /><Button size="sm" variant="ghost" onClick={() => setCustomer(null)}>{t('app.edit')}</Button></div>
        ) : (
          <>
            <TextField label={t('credit.pickCustomer')} value={search} onChange={(e) => setSearch(e.target.value)} autoFocus />
            <div className="pick-list">{(found.data ?? []).slice(0, 8).map((c) => <Button key={c.id} variant="ghost" onClick={() => setCustomer(c)}>{pick(c.nameEn, c.nameAr ?? c.nameEn)} · <span className="ltr">{c.number}</span></Button>)}</div>
          </>
        )}
        <div className="grid-2">
          <TextField label={t('credit.limit')} value={limit} onChange={(e) => setLimit(e.target.value)} inputMode="decimal" dir="ltr" placeholder="0.000" />
          <TextField label={t('credit.terms')} value={terms} onChange={(e) => setTerms(e.target.value.replace(/\D/g, ''))} inputMode="numeric" dir="ltr" />
          <TextField label={t('credit.offlineCap')} help={t('credit.offlineCapHelp')} value={cap} onChange={(e) => setCap(e.target.value)} inputMode="decimal" dir="ltr" />
          <TextField label={t('credit.note')} value={note} onChange={(e) => setNote(e.target.value)} dir="auto" />
        </div>
        <fieldset><legend>{t('credit.allowances')}</legend>
          <p className="muted">{t('credit.allowancesHelp')}</p>
          <div className="grid-2">{branches.map((b) => <TextField key={b.id} label={`${b.code} · ${pick(b.nameEn, b.nameAr)}`} value={allow[b.id] ?? ''} onChange={(e) => setAllow({ ...allow, [b.id]: e.target.value })} inputMode="decimal" dir="ltr" placeholder="0.000" />)}</div>
          <p className={total > (parseAmount(limit) ?? 0) ? 'neg' : 'muted'}>{t('credit.allowanceTotal', { total: fmt(total), limit: fmt(parseAmount(limit) ?? 0) })}</p>
        </fieldset>
        <ErrorBox error={error} />
      </div>
    </Modal>
  );
}

function AgingReport() {
  const api = useApi();
  const { t, pick } = useI18n();
  const q = useQuery({ queryKey: ['creditAging'], queryFn: () => api.creditAging() });
  const rows = q.data ?? [];
  const sum = (k: 'current' | 'd1_30' | 'd31_60' | 'd61_90' | 'd90plus' | 'onAccount' | 'total') => rows.reduce((s, r) => s + r[k], 0);
  const keys = ['current', 'd1_30', 'd31_60', 'd61_90', 'd90plus', 'onAccount', 'total'] as const;
  return (
    <Card>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Spinner label={t('app.loading')} /> : (
        <div className="table-wrap"><table className="table">
          <thead><tr><th scope="col">{t('credit.customer')}</th>{keys.map((k) => <th key={k} scope="col" className="num">{t(`credit.aging.${k}`)}</th>)}</tr></thead>
          <tbody>
            {rows.length === 0 && <tr><td colSpan={8} className="muted">{t('credit.none')}</td></tr>}
            {rows.map((r) => (
              <tr key={r.customerId}>
                <td><Link to={`/app/credit/${r.customerId}`}>{pick(r.nameEn, r.nameAr ?? r.nameEn)}</Link> {r.status === 'hold' && <Badge tone="danger">{t('credit.statuses.hold')}</Badge>}</td>
                {keys.map((k) => <td key={k} className={`num${(k === 'd61_90' || k === 'd90plus') && r[k] > 0 ? ' neg' : ''}`}>{k === 'onAccount' && r[k] > 0 ? `(${fmt(r[k])})` : fmt(r[k])}</td>)}
              </tr>
            ))}
          </tbody>
          {rows.length > 0 && <tfoot><tr><th scope="row">{t('credit.totals')}</th>{keys.map((k) => <td key={k} className="num strong">{fmt(Math.round(sum(k) * 1000) / 1000)}</td>)}</tr></tfoot>}
        </table></div>
      )}
    </Card>
  );
}

function Exposure() {
  const api = useApi();
  const { t, pick } = useI18n();
  const q = useQuery({ queryKey: ['creditExposure'], queryFn: () => api.creditExposure() });
  return (
    <Card>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Spinner label={t('app.loading')} /> : (
        <DataTable rows={q.data ?? []} rowKey={(r) => r.branchId} empty={t('credit.none')}
          columns={[
            { key: 'b', header: t('credit.branch'), render: (r) => <span><span className="ltr">{r.code}</span> · {pick(r.nameEn, r.nameAr)}</span> },
            { key: 'o', header: t('credit.exposure.balance'), render: (r) => <strong className="num">{fmt(r.balance)}</strong> },
            { key: 'd', header: t('credit.exposure.overdue'), render: (r) => <span className={`num${r.overdue > 0 ? ' neg' : ''}`}>{fmt(r.overdue)}</span> },
            { key: 'c', header: t('credit.exposure.customers'), render: (r) => <span className="num">{r.customers}</span> },
            { key: 's', header: t('credit.exposure.charged'), render: (r) => <span className="num">{fmt(r.chargedThisMonth)}</span> },
            { key: 'r', header: t('credit.exposure.received'), render: (r) => <span className="num">{fmt(r.receivedThisMonth)}</span> },
            { key: 'a', header: t('credit.exposure.allowances'), render: (r) => <span className="num">{fmt(r.allowances)}</span> },
          ]} />
      )}
    </Card>
  );
}

function Overrides() {
  const api = useApi();
  const { t, lang, pick } = useI18n();
  const q = useQuery({ queryKey: ['creditOverrides'], queryFn: () => api.creditOverrides() });
  return (
    <div className="stack">
      <ErrorBox error={q.error} />
      <Card><h3>{t('credit.overrides.breaches')}</h3>
        <DataTable rows={q.data?.breaches ?? []} rowKey={(r) => r.id} empty={t('credit.overrides.noneYet')}
          columns={[
            { key: 'w', header: t('credit.overrides.when'), render: (r) => <span className="muted">{formatDateTime(r.detectedAt, lang)}</span> },
            { key: 'c', header: t('credit.customer'), render: (r) => pick(r.nameEn, r.nameAr ?? r.nameEn) },
            { key: 'b', header: t('credit.branch'), render: (r) => <span className="ltr">{r.branchCode ?? ''}</span> },
            { key: 'e', header: t('credit.owed'), render: (r) => <strong className="num neg">{fmt(r.exposure)}</strong> },
            { key: 'l', header: t('credit.limit'), render: (r) => <span className="num">{fmt(r.creditLimit)}</span> },
          ]} />
      </Card>
      <Card><h3>{t('credit.overrides.decisions')}</h3>
        <DataTable rows={q.data?.decisions ?? []} rowKey={(r) => r.id} empty={t('credit.overrides.noneYet')}
          columns={[
            { key: 'w', header: t('credit.overrides.when'), render: (r) => <span className="muted">{formatDateTime(r.createdAt, lang)}</span> },
            { key: 'c', header: t('credit.customer'), render: (r) => pick(r.nameEn, r.nameAr ?? r.nameEn) },
            { key: 'i', header: t('credit.overrides.invoice'), render: (r) => <span className="ltr">{r.invoiceNumber}</span> },
            { key: 'm', header: t('credit.overrides.mode'), render: (r) => <Badge tone={r.mode === 'offline' ? 'warning' : 'default'}>{t(`credit.modes.${r.mode}`)}</Badge> },
            { key: 'a', header: t('credit.amount'), render: (r) => <span className="num">{fmt(r.amount)}</span> },
            { key: 'v', header: t('credit.available'), render: (r) => <span className="num">{fmt(r.available)}</span> },
            { key: 'o', header: t('credit.overrides.over'), render: (r) => r.overBy > 0 ? <strong className="num neg">{fmt(r.overBy)}</strong> : <span className="muted">—</span> },
            { key: 'p', header: t('credit.overrides.approvedBy'), render: (r) => r.approvedBy ? pick(r.approvedBy, r.approvedByAr ?? r.approvedBy) : '' },
          ]} />
      </Card>
    </div>
  );
}

function LedgerChecks() {
  const api = useApi();
  const qc = useQueryClient();
  const { t, lang } = useI18n();
  const [error, setError] = useState<unknown>(null);
  const q = useQuery({ queryKey: ['ledgerChecks'], queryFn: () => api.ledgerChecks() });
  const rows = q.data ?? [];
  const ok = rows.length > 0 && rows.every((r) => r.difference === 0);
  return (
    <Card>
      <div className="stack">
        <p className="muted">{t('credit.checks.intro')}</p>
        <div className="row">
          {rows[0] && <span className="muted">{t('credit.checks.checked', { time: formatDateTime(rows[0].checkedAt, lang) })}</span>}
          <span className="spacer" />
          <Button onClick={() => api.runLedgerCheck().then(() => qc.invalidateQueries({ queryKey: ['ledgerChecks'] })).catch(setError)}>{t('credit.checks.run')}</Button>
        </div>
        {ok && <Alert tone="success">{t('credit.checks.ok')}</Alert>}
        <ErrorBox error={error ?? q.error} />
        <DataTable rows={rows} rowKey={(r) => `${r.ledger}-${r.branchId}`} empty={t('credit.overrides.noneYet')}
          columns={[
            { key: 'l', header: t('credit.checks.ledger'), render: (r) => t(`credit.checks.${r.ledger}`) },
            { key: 'b', header: t('credit.branch'), render: (r) => <span className="ltr">{r.branchCode ?? ''}</span> },
            { key: 'd', header: t('credit.date'), render: (r) => <span className="muted">{formatDate(r.asOf, lang)}</span> },
            { key: 's', header: t('credit.checks.subledger'), render: (r) => <span className="num">{fmt(r.subledger)}</span> },
            { key: 'c', header: t('credit.checks.control'), render: (r) => <span className="num">{fmt(r.control)}</span> },
            { key: 'x', header: t('credit.checks.difference'), render: (r) => <strong className={`num${r.difference !== 0 ? ' neg' : ''}`}>{fmt(r.difference)}</strong> },
          ]} />
      </div>
    </Card>
  );
}
