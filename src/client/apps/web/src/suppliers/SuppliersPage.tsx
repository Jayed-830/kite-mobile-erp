import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { can, type Supplier, type SupplierAgingRow, type SupplierInput, type SupplierRow, type SupplierStatus } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { businessToday, ErrorBox, formatDate, useApi, useAuth } from '../core';
import { fmt } from '../money';

const tone = (s: SupplierStatus) => (s === 'active' ? 'success' : s === 'provisional' ? 'warning' : 'default') as 'success' | 'warning' | 'default';

/** Suppliers (16.1): head office keeps one list; branches add provisional ones offline, approved or merged here. */
export function SuppliersPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const [tab, setTab] = useState<'list' | 'aging'>('list');
  const [q, setQ] = useState('');
  const [status, setStatus] = useState<SupplierStatus | ''>('');
  const [adding, setAdding] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const list = useQuery({ queryKey: ['suppliers', q, status], queryFn: () => api.suppliers({ q: q.trim() || undefined, status: status || undefined }) });
  const aging = useQuery({ queryKey: ['supplierAging'], enabled: tab === 'aging', queryFn: () => api.supplierAging() });
  const approver = can(me, 'SUPPLIER.APPROVE') && me?.serverRole !== 'branch';
  const refresh = () => void qc.invalidateQueries({ queryKey: ['suppliers'] });
  return (
    <div className="stack">
      <PageHeader title={t('sup.title')}>{can(me, 'SUPPLIER.MANAGE') && <Button variant="primary" onClick={() => setAdding(true)}>{t('sup.add')}</Button>}</PageHeader>
      <div className="tabs" role="tablist">
        {(['list', 'aging'] as const).map((k) => <button key={k} role="tab" aria-selected={tab === k} className={`tab${tab === k ? ' active' : ''}`} onClick={() => setTab(k)}>{t(`sup.tabs.${k}`)}</button>)}
      </div>
      {msg && <Alert tone="success">{msg}</Alert>}
      <ErrorBox error={error ?? list.error ?? aging.error} />
      {tab === 'list' && (
        <>
          <div className="row">
            <TextField label={t('sup.search')} value={q} onChange={(e) => setQ(e.target.value)} />
            <SelectField label={t('app.status')} value={status} onChange={(e) => setStatus(e.target.value as SupplierStatus | '')}>
              <option value="">{t('sup.all')}</option>
              {(['provisional', 'active', 'inactive', 'merged'] as const).map((s) => <option key={s} value={s}>{t(`sup.statuses.${s}`)}</option>)}
            </SelectField>
          </div>
          {list.isLoading ? <Spinner label={t('app.loading')} /> : (
            <Card>
              <DataTable<SupplierRow> rows={list.data ?? []} rowKey={(r) => r.id} empty={t('sup.none')}
                columns={[
                  { key: 'n', header: t('sup.number'), render: (r) => <span className="ltr">{r.number}</span> },
                  { key: 'm', header: t('sup.name'), render: (r) => <Link to={`/app/suppliers/${r.id}`}>{pick(r.nameEn, r.nameAr)}</Link> },
                  { key: 'v', header: t('sup.vat'), render: (r) => <span className="ltr muted">{r.vatNumber ?? ''}</span> },
                  { key: 't', header: t('sup.terms'), render: (r) => <span className="num">{r.termsDays}</span> },
                  { key: 'b', header: t('sup.balance'), render: (r) => <strong className="num">{fmt(r.balance)}</strong> },
                  { key: 's', header: t('app.status'), render: (r) => <span><Badge tone={tone(r.status)}>{t(`sup.statuses.${r.status}`)}</Badge>{r.mergedIntoNumber && <small className="muted ltr"> → {r.mergedIntoNumber}</small>}{r.originBranchCode && r.status === 'provisional' && <small className="muted"> · {r.originBranchCode}</small>}</span> },
                  { key: 'x', header: '', render: (r) => approver && r.status === 'provisional'
                    ? <Button size="sm" onClick={() => api.approveSupplier(r.id).then(() => { setMsg(t('sup.approved', { number: r.number })); refresh(); }).catch(setError)}>{t('sup.approve')}</Button> : null },
                ]} />
            </Card>
          )}
        </>
      )}
      {tab === 'aging' && (
        <Card>
          {aging.isLoading ? <Spinner label={t('app.loading')} /> : (
            <DataTable<SupplierAgingRow> rows={aging.data ?? []} rowKey={(r) => r.supplierId} empty={t('sup.none')}
              columns={[
                { key: 'n', header: t('sup.name'), render: (r) => <Link to={`/app/suppliers/${r.supplierId}`}>{pick(r.nameEn, r.nameAr)}</Link> },
                ...(['current', 'd1_30', 'd31_60', 'd61_90', 'd90plus'] as const).map((k) => ({ key: k, header: t(`credit.aging.${k}`), render: (r: SupplierAgingRow) => <span className="num">{fmt(r[k])}</span> })),
                { key: 'a', header: t('sup.advance'), render: (r) => <span className="num">{r.advance ? fmt(r.advance) : ''}</span> },
                { key: 't', header: t('sup.balance'), render: (r) => <strong className="num">{fmt(r.total)}</strong> },
              ]} />
          )}
        </Card>
      )}
      <p className="muted">{t('sup.phase11')}</p>
      {adding && <SupplierForm onClose={() => setAdding(false)} onDone={(m) => { setAdding(false); setMsg(m); refresh(); }} />}
    </div>
  );
}

function SupplierForm({ supplier, onClose, onDone }: { supplier?: Supplier; onClose: () => void; onDone: (m: string) => void }) {
  const api = useApi();
  const { t } = useI18n();
  const [f, setF] = useState<SupplierInput>({ nameEn: supplier?.nameEn ?? '', nameAr: supplier?.nameAr ?? '', vatNumber: supplier?.vatNumber ?? '', crNumber: supplier?.crNumber ?? '',
    phone: supplier?.phone ?? '', email: supplier?.email ?? '', address: supplier?.address ?? '', contactName: supplier?.contactName ?? '', termsDays: supplier?.termsDays ?? 30 });
  const [error, setError] = useState<unknown>(null);
  const set = (k: keyof SupplierInput) => (e: { target: { value: string } }) => setF({ ...f, [k]: k === 'termsDays' ? Number(e.target.value.replace(/\D/g, '') || 0) : e.target.value });
  const save = () => (supplier ? api.updateSupplier(supplier.id, { ...f, version: supplier.version }).then(() => t('sup.saved'))
    : api.createSupplier(f).then((r) => `${r.number} · ${t(`sup.statuses.${r.status}`)}`)).then(onDone).catch(setError);
  return (
    <Modal open title={supplier ? t('sup.editTitle', { name: supplier.nameEn }) : t('sup.addTitle')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={!f.nameEn.trim() || !f.nameAr.trim()} onClick={save}>{t('sup.save')}</Button></>}>
      <div className="stack">
        <div className="grid-2">
          <TextField label={t('sup.nameEn')} value={f.nameEn} onChange={set('nameEn')} />
          <TextField label={t('sup.nameAr')} value={f.nameAr} onChange={set('nameAr')} dir="rtl" />
          <TextField label={t('sup.vat')} value={f.vatNumber ?? ''} onChange={set('vatNumber')} dir="ltr" />
          <TextField label={t('sup.cr')} value={f.crNumber ?? ''} onChange={set('crNumber')} dir="ltr" />
          <TextField label={t('sup.phone')} value={f.phone ?? ''} onChange={set('phone')} dir="ltr" placeholder="+968" />
          <TextField label={t('sup.email')} value={f.email ?? ''} onChange={set('email')} dir="ltr" />
          <TextField label={t('sup.contact')} value={f.contactName ?? ''} onChange={set('contactName')} />
          <TextField label={t('sup.terms')} value={String(f.termsDays ?? 30)} onChange={set('termsDays')} inputMode="numeric" dir="ltr" />
        </div>
        <TextField label={t('sup.address')} value={f.address ?? ''} onChange={set('address')} dir="auto" />
        <ErrorBox error={error} />
      </div>
    </Modal>
  );
}

/** One supplier (16.1): details, balances by branch, merged suppliers, the ledger and statement, and head office's actions. */
export function SupplierPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, lang, pick } = useI18n();
  const [dialog, setDialog] = useState<'edit' | 'merge' | 'opening' | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const q = useQuery({ queryKey: ['supplier', id], queryFn: () => api.supplier(id) });
  const st = useQuery({ queryKey: ['supplierStatement', id], queryFn: () => api.supplierStatement(id, { from: `${businessToday().slice(0, 4)}-01-01` }) });
  if (q.isLoading) return <Spinner label={t('app.loading')} />;
  if (!q.data) return <ErrorBox error={q.error} />;
  const d = q.data;
  const s = d.supplier;
  const done = (m: string) => { setDialog(null); setMsg(m); void qc.invalidateQueries({ queryKey: ['supplier', id] }); void qc.invalidateQueries({ queryKey: ['supplierStatement', id] }); void qc.invalidateQueries({ queryKey: ['suppliers'] }); };
  const approver = can(me, 'SUPPLIER.APPROVE') && d.headOffice;
  return (
    <div className="stack">
      <PageHeader title={pick(s.nameEn, s.nameAr)}>
        <Link className="btn" to="/app/suppliers">{t('app.back')}</Link>
        {can(me, 'SUPPLIER.MANAGE') && s.status !== 'merged' && <Button onClick={() => setDialog('edit')}>{t('sup.edit')}</Button>}
        {can(me, 'SUPPLIER.MANAGE') && s.status !== 'merged' && <Button onClick={() => setDialog('opening')}>{t('sup.opening')}</Button>}
        {approver && s.status === 'provisional' && <Button variant="primary" onClick={() => api.approveSupplier(s.id).then(() => done(t('sup.approved', { number: s.number })))}>{t('sup.approve')}</Button>}
        {approver && (s.status === 'provisional' || s.status === 'active') && d.merged.length === 0 && <Button onClick={() => setDialog('merge')}>{t('sup.merge')}</Button>}
      </PageHeader>
      <div className="row"><span className="ltr muted">{s.number}</span><Badge tone={tone(s.status)}>{t(`sup.statuses.${s.status}`)}</Badge>{s.vatNumber && <span className="ltr muted">{t('sup.vat')} {s.vatNumber}</span>}{s.phone && <span className="ltr muted">{s.phone}</span>}</div>
      {s.status === 'provisional' && <Alert tone="warning">{t('sup.provisionalHelp')}</Alert>}
      {msg && <Alert tone="success">{msg}</Alert>}
      <div className="row figures-row">
        <div className="fig stat"><span className="muted">{t('sup.balance')}</span><strong className="num">{fmt(d.balance)}</strong></div>
        {d.byBranch.map((b) => <div key={b.branchId} className="fig stat"><span className="muted">{b.code} · {pick(b.nameEn, b.nameAr)}</span><strong className="num">{fmt(b.balance)}</strong></div>)}
      </div>
      {d.merged.length > 0 && <Card><h3>{t('sup.mergedFrom')}</h3><ul className="plain">{d.merged.map((m) => <li key={m.id}><span className="ltr">{m.number}</span> · {pick(m.nameEn, m.nameAr)}</li>)}</ul></Card>}
      <Card>
        <h3>{t('sup.statement')}</h3>
        <ErrorBox error={st.error} />
        {st.data && (
          <div className="table-wrap"><table className="table">
            <thead><tr><th>{t('sup.date')}</th><th>{t('credit.document')}</th><th>{t('credit.type')}</th><th>{t('credit.branch')}</th><th className="num">{t('credit.debit')}</th><th className="num">{t('credit.credit')}</th><th className="num">{t('credit.balance')}</th></tr></thead>
            <tbody>
              <tr><td colSpan={6} className="muted">{t('credit.opening')}</td><td className="num">{fmt(st.data.opening)}</td></tr>
              {st.data.lines.map((l, i) => <tr key={i}><td className="muted">{formatDate(l.date, lang)}</td><td><span className="ltr">{l.number}</span></td><td>{t(`sup.kinds.${l.kind}`)}</td><td><span className="ltr">{l.branchCode}</span></td>
                <td className="num">{l.debit ? fmt(l.debit) : ''}</td><td className="num">{l.credit ? fmt(l.credit) : ''}</td><td className="num">{fmt(l.balance)}</td></tr>)}
            </tbody>
            <tfoot><tr><th colSpan={6}>{t('credit.closing')}</th><th className="num">{fmt(st.data.closing)}</th></tr></tfoot>
          </table></div>
        )}
      </Card>
      <p className="muted">{t('sup.phase11')}</p>
      {dialog === 'edit' && <SupplierForm supplier={s} onClose={() => setDialog(null)} onDone={done} />}
      {dialog === 'merge' && <MergeDialog supplier={s} onClose={() => setDialog(null)} onDone={done} />}
      {dialog === 'opening' && <SupplierOpening supplier={s} onClose={() => setDialog(null)} onDone={done} />}
    </div>
  );
}

function MergeDialog({ supplier, onClose, onDone }: { supplier: Supplier; onClose: () => void; onDone: (m: string) => void }) {
  const api = useApi();
  const { t, pick } = useI18n();
  const [into, setInto] = useState('');
  const [reason, setReason] = useState('');
  const [error, setError] = useState<unknown>(null);
  const list = useQuery({ queryKey: ['suppliers', 'active'], queryFn: () => api.suppliers({ status: 'active' }) });
  const target = (list.data ?? []).find((s) => s.id === into);
  return (
    <Modal open title={t('sup.mergeTitle', { name: supplier.nameEn })} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="danger" disabled={!into} onClick={() => api.mergeSupplier(supplier.id, into, reason.trim() || null).then(() => onDone(t('sup.merged', { number: target?.number ?? '' }))).catch(setError)}>{t('sup.merge')}</Button></>}>
      <div className="stack">
        <p className="muted">{t('sup.mergeHelp')}</p>
        <SelectField label={t('sup.into')} value={into} onChange={(e) => setInto(e.target.value)}>
          <option value="" />
          {(list.data ?? []).filter((s) => s.id !== supplier.id).map((s) => <option key={s.id} value={s.id}>{s.number} · {pick(s.nameEn, s.nameAr)}</option>)}
        </SelectField>
        <TextField label={t('sup.reason')} value={reason} onChange={(e) => setReason(e.target.value)} dir="auto" />
        <ErrorBox error={error} />
      </div>
    </Modal>
  );
}

function SupplierOpening({ supplier, onClose, onDone }: { supplier: Supplier; onClose: () => void; onDone: (m: string) => void }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const branches = (me?.branches ?? []).filter((b) => b.status === 'active');
  const [branchId, setBranchId] = useState(me?.serverBranchId ?? branches[0]?.id ?? '');
  const [amount, setAmount] = useState('');
  const [date, setDate] = useState(businessToday());
  const [error, setError] = useState<unknown>(null);
  const ok = /^-?\d+(\.\d{1,3})?$/.test(amount.trim()) && Number(amount) !== 0;
  return (
    <Modal open title={t('sup.openingTitle', { name: supplier.nameEn })} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={!ok} onClick={() => api.supplierOpening(supplier.id, { branchId, amount: Number(amount), date }).then((r) => onDone(`${t('sup.opening')} ${r.number}`)).catch(setError)}>{t('sup.save')}</Button></>}>
      <div className="stack">
        <p className="muted">{t('sup.openingHelp')}</p>
        <div className="grid-2">
          <SelectField label={t('credit.branch')} value={branchId} onChange={(e) => setBranchId(e.target.value)}>{branches.map((b) => <option key={b.id} value={b.id}>{b.code} · {pick(b.nameEn, b.nameAr)}</option>)}</SelectField>
          <TextField label={t('sup.amount')} value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" dir="ltr" placeholder="0.000" />
          <TextField label={t('sup.date')} type="date" value={date} onChange={(e) => setDate(e.target.value)} dir="ltr" />
        </div>
        <ErrorBox error={error} />
      </div>
    </Modal>
  );
}
