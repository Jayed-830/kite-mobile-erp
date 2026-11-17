import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import {
  can, type AccountKind, type CashFlowClass, type ChartAccount, type JournalRow, type ManualDetail, type ManualLineInput, type ManualRow, type PeriodStatus, type PeriodsView,
} from '@kite/api';
import { useI18n } from '@kite/i18n';
import { toBaisa } from '@kite/pos';
import { Alert, Badge, Button, Card, Checkbox, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { businessToday, ErrorBox, formatDate, formatDateTime, useApi, useAuth, workBranchId } from '../core';
import { fmt, fmtB, parseAmount } from '../money';
import { BranchSelect, newId } from '../purchasing/bits';

type Tab = 'chart' | 'journals' | 'manual' | 'periods' | 'cards' | 'check';
const TABS: Tab[] = ['chart', 'journals', 'manual', 'periods', 'cards', 'check'];
const KINDS: AccountKind[] = ['asset', 'liability', 'equity', 'revenue', 'expense'];
const CLASSES: CashFlowClass[] = ['cash', 'operating', 'investing', 'financing', 'none'];

/** The accountant's desk (blueprint 17): chart of accounts, journals, manual journals, periods by branch, card settlements, the ledger check. */
export function AccountingPage() {
  const { t } = useI18n();
  const { me } = useAuth();
  const [params, setParams] = useSearchParams();
  const tab = (TABS.includes(params.get('tab') as Tab) ? params.get('tab') : 'chart') as Tab;
  return (
    <div className="stack">
      <PageHeader title={t('acct.title')}>
        <Link className="btn" to="/app/accounting/statements">{t('acct.statements')}</Link>
        <Link className="btn" to="/app/accounting/vat">{t('acct.vat')}</Link>
      </PageHeader>
      <div className="tabs" role="tablist">
        {TABS.filter((k) => k !== 'cards' || can(me, 'ACCT.VIEW')).map((k) => (
          <button key={k} role="tab" aria-selected={tab === k} className={`tab${tab === k ? ' active' : ''}`} onClick={() => setParams({ tab: k })}>{t(`acct.tabs.${k}`)}</button>
        ))}
      </div>
      {tab === 'chart' && <ChartTab />}
      {tab === 'journals' && <JournalsTab />}
      {tab === 'manual' && <ManualTab />}
      {tab === 'periods' && <PeriodsTab />}
      {tab === 'cards' && <CardsTab />}
      {tab === 'check' && <CheckTab />}
    </div>
  );
}

// ------------------------------------------------------------------ chart of accounts (17.2)
function ChartTab() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const q = useQuery({ queryKey: ['chart'], queryFn: () => api.chart() });
  const [edit, setEdit] = useState<ChartAccount | 'new' | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const manage = can(me, 'ACCT.COA.MANAGE');
  return (
    <div className="stack">
      {manage && <div className="row"><Button variant="primary" onClick={() => setEdit('new')}>{t('acct.addAccount')}</Button></div>}
      {msg && <Alert tone="success">{msg}</Alert>}
      <ErrorBox error={q.error} />
      <Card>
        {q.isLoading ? <Spinner label={t('app.loading')} /> : (
          <DataTable<ChartAccount> rows={q.data ?? []} rowKey={(a) => a.id} empty={t('app.none')}
            columns={[
              { key: 'c', header: t('acct.code'), render: (a) => <span className={`ltr${a.isPostable ? '' : ' strong'}`}>{a.code}</span> },
              { key: 'n', header: t('acct.name'), render: (a) => <span className={a.isPostable ? '' : 'strong'}>{a.isPostable ? '  ' : ''}{pick(a.nameEn, a.nameAr)}</span> },
              { key: 't', header: t('acct.type'), render: (a) => t(`acct.kinds.${a.type}`) },
              { key: 'f', header: t('acct.cashFlow'), render: (a) => (a.isPostable ? t(`acct.classes.${a.cashFlowClass}`) : '') },
              { key: 'x', header: '', render: (a) => <span>{a.isControl && <Badge tone="primary">{t('acct.control')}</Badge>} {a.status === 'inactive' && <Badge>{t('acct.inactive')}</Badge>}</span> },
              { key: 'b', header: t('acct.balance'), render: (a) => (a.isPostable ? <span className="num">{fmt(a.balance)}</span> : '') },
              { key: 'e', header: '', render: (a) => manage ? <Button size="sm" variant="ghost" onClick={() => setEdit(a)}>{t('acct.edit')}</Button> : null },
            ]} />
        )}
      </Card>
      {edit && <AccountForm account={edit === 'new' ? undefined : edit} accounts={q.data ?? []} onClose={() => setEdit(null)}
        onDone={(m) => { setEdit(null); setMsg(m); void qc.invalidateQueries({ queryKey: ['chart'] }); }} />}
    </div>
  );
}

function AccountForm({ account, accounts, onClose, onDone }: { account?: ChartAccount; accounts: ChartAccount[]; onClose: () => void; onDone: (m: string) => void }) {
  const api = useApi();
  const { t, pick } = useI18n();
  const [code, setCode] = useState(account?.code ?? '');
  const [nameEn, setNameEn] = useState(account?.nameEn ?? '');
  const [nameAr, setNameAr] = useState(account?.nameAr ?? '');
  const [type, setType] = useState<AccountKind>(account?.type ?? 'expense');
  const [parentId, setParentId] = useState(account?.parentId ?? '');
  const [control, setControl] = useState(account?.isControl ?? false);
  const [cff, setCff] = useState<CashFlowClass>(account?.cashFlowClass ?? 'none');
  const [status, setStatus] = useState(account?.status ?? 'active');
  const save = useMutation({
    mutationFn: async () => {
      if (account) await api.updateAccount(account.id, { nameEn, nameAr, parentId: parentId || null, isControl: control, cashFlowClass: cff, status, version: account.version });
      else await api.createAccount({ code, nameEn, nameAr, type, parentId: parentId || null, isControl: control, cashFlowClass: type === 'revenue' || type === 'expense' ? 'none' : cff });
    },
    onSuccess: () => onDone(t('acct.accountSaved', { code })),
  });
  return (
    <Modal open title={account ? t('acct.editAccount', { code: account.code }) : t('acct.addAccount')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={!code.trim() || !nameEn.trim() || !nameAr.trim()} busy={save.isPending} onClick={() => save.mutate()}>{t('app.save')}</Button></>}>
      <div className="stack">
        <div className="grid-2">
          <TextField label={t('acct.code')} value={code} onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))} readOnly={!!account} dir="ltr" inputMode="numeric" />
          <SelectField label={t('acct.type')} value={type} onChange={(e) => setType(e.target.value as AccountKind)} disabled={!!account}>
            {KINDS.map((k) => <option key={k} value={k}>{t(`acct.kinds.${k}`)}</option>)}
          </SelectField>
          <TextField label={t('acct.nameEn')} value={nameEn} onChange={(e) => setNameEn(e.target.value)} />
          <TextField label={t('acct.nameAr')} value={nameAr} onChange={(e) => setNameAr(e.target.value)} dir="rtl" />
          <SelectField label={t('acct.parent')} value={parentId} onChange={(e) => setParentId(e.target.value)}>
            <option value="">—</option>
            {accounts.filter((a) => !a.isPostable && a.type === type).map((a) => <option key={a.id} value={a.id}>{a.code} · {pick(a.nameEn, a.nameAr)}</option>)}
          </SelectField>
          {type !== 'revenue' && type !== 'expense' && (
            <SelectField label={t('acct.cashFlow')} value={cff} onChange={(e) => setCff(e.target.value as CashFlowClass)}>
              {CLASSES.map((c) => <option key={c} value={c}>{t(`acct.classes.${c}`)}</option>)}
            </SelectField>
          )}
          {account && (
            <SelectField label={t('app.status')} value={status} onChange={(e) => setStatus(e.target.value as 'active' | 'inactive')}>
              <option value="active">{t('acct.active')}</option><option value="inactive">{t('acct.inactive')}</option>
            </SelectField>
          )}
        </div>
        <Checkbox label={t('acct.controlHelp')} checked={control} onChange={(e) => setControl(e.target.checked)} />
        <ErrorBox error={save.error} />
      </div>
    </Modal>
  );
}

// ------------------------------------------------------------------ journals (17.1)
function JournalsTab() {
  const api = useApi();
  const { t, lang } = useI18n();
  const [from, setFrom] = useState(`${businessToday().slice(0, 7)}-01`);
  const [to, setTo] = useState(businessToday());
  const [branchId, setBranchId] = useState('');
  const [q, setQ] = useState('');
  const list = useQuery({ queryKey: ['journals', from, to, branchId, q], queryFn: () => api.journals({ from, to, branchId: branchId || undefined, q: q.trim() || undefined }) });
  return (
    <div className="stack">
      <div className="row">
        <TextField label={t('acct.from')} type="date" value={from} onChange={(e) => setFrom(e.target.value)} dir="ltr" />
        <TextField label={t('acct.to')} type="date" value={to} onChange={(e) => setTo(e.target.value)} dir="ltr" />
        <BranchSelect value={branchId} onChange={setBranchId} any={t('acct.allBranches')} />
        <TextField label={t('acct.search')} value={q} onChange={(e) => setQ(e.target.value)} />
      </div>
      <ErrorBox error={list.error} />
      <Card>
        {list.isLoading ? <Spinner label={t('app.loading')} /> : (
          <DataTable<JournalRow> rows={list.data ?? []} rowKey={(j) => j.id} empty={t('app.none')}
            columns={[
              { key: 'n', header: t('acct.number'), render: (j) => <Link className="ltr" to={`/app/accounting/journals/${j.id}`}>{j.number}</Link> },
              { key: 'd', header: t('acct.date'), render: (j) => formatDate(j.journalDate, lang) },
              { key: 'k', header: t('acct.kind'), render: (j) => <span className="ltr muted">{j.postingKind}</span> },
              { key: 's', header: t('acct.description'), render: (j) => <span>{j.isReversal && <Badge tone="warning">{t('acct.reversal')}</Badge>} {j.description}</span> },
              { key: 'b', header: t('acct.branch'), render: (j) => <span className="ltr">{j.branchCode ?? ''}</span> },
              { key: 't', header: t('acct.amount'), render: (j) => <span className="num">{fmt(j.total)}</span> },
            ]} />
        )}
      </Card>
    </div>
  );
}

/** One journal and its lines (17.1), with its reversal both ways. */
export function JournalPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const { t, pick, lang } = useI18n();
  const q = useQuery({ queryKey: ['journal', id], queryFn: () => api.journal(id) });
  if (q.isLoading) return <Spinner label={t('app.loading')} />;
  if (!q.data) return <ErrorBox error={q.error} />;
  const { journal: j, lines } = q.data;
  const dr = lines.reduce((s, l) => s + toBaisa(l.debit), 0n);
  const cr = lines.reduce((s, l) => s + toBaisa(l.credit), 0n);
  return (
    <div className="stack">
      <PageHeader title={t('acct.journalTitle', { number: j.number })}><Link className="btn" to="/app/accounting?tab=journals">{t('app.back')}</Link></PageHeader>
      <div className="row">
        <span>{formatDate(j.journalDate, lang)}</span><span className="ltr muted">{j.postingKind}</span><span className="muted">{t('acct.periodOf', { no: j.periodNo, year: j.fiscalYear })}</span>
        <span>{j.description}</span><span className="muted">{j.postedBy ?? ''} · {formatDateTime(j.postedAt, lang)}</span>
      </div>
      {j.reversalOfId && <Alert tone="info">{t('acct.reverses')} <Link className="ltr" to={`/app/accounting/journals/${j.reversalOfId}`}>{j.reversalOfNumber}</Link></Alert>}
      {j.reversedById && <Alert tone="warning">{t('acct.reversedBy')} <Link className="ltr" to={`/app/accounting/journals/${j.reversedById}`}>{j.reversedByNumber}</Link></Alert>}
      <Card>
        <table className="table">
          <thead><tr><th scope="col">{t('acct.account')}</th><th scope="col">{t('acct.branch')}</th><th scope="col">{t('acct.party')}</th><th scope="col">{t('acct.memo')}</th><th scope="col">{t('acct.debit')}</th><th scope="col">{t('acct.credit')}</th></tr></thead>
          <tbody>
            {lines.map((l) => (
              <tr key={l.lineNo}><td><span className="ltr">{l.accountCode}</span> {pick(l.accountNameEn, l.accountNameAr)}</td><td className="ltr">{l.branchCode ?? ''}</td><td>{l.partyName ?? ''}</td>
                <td>{l.memo ?? ''}</td><td className="num">{l.debit ? fmt(l.debit) : ''}</td><td className="num">{l.credit ? fmt(l.credit) : ''}</td></tr>
            ))}
          </tbody>
          <tfoot><tr><th scope="row" colSpan={4}>{t('acct.totals')}</th><td className="num strong">{fmtB(dr)}</td><td className="num strong">{fmtB(cr)}</td></tr></tfoot>
        </table>
      </Card>
    </div>
  );
}

// ------------------------------------------------------------------ manual journals (17.6)
const manualTone = (s: string) => (s === 'posted' ? 'success' : s === 'pending_approval' ? 'warning' : s === 'rejected' ? 'danger' : 'default') as 'success' | 'warning' | 'danger' | 'default';

function ManualTab() {
  const api = useApi();
  const nav = useNavigate();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const [status, setStatus] = useState('');
  const [adding, setAdding] = useState(false);
  const list = useQuery({ queryKey: ['manualJournals', status], queryFn: () => api.manualJournals({ status: status || undefined }) });
  return (
    <div className="stack">
      <div className="row">
        {can(me, 'ACCT.JOURNAL.MANUAL') && <Button variant="primary" onClick={() => setAdding(true)}>{t('acct.newManual')}</Button>}
        <SelectField label={t('app.status')} value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">{t('acct.all')}</option>
          {(['draft', 'pending_approval', 'posted', 'rejected'] as const).map((s) => <option key={s} value={s}>{t(`acct.manualStatus.${s}`)}</option>)}
        </SelectField>
      </div>
      <ErrorBox error={list.error} />
      <Card>
        <DataTable<ManualRow> rows={list.data ?? []} rowKey={(m) => m.id} empty={t('app.none')}
          columns={[
            { key: 'n', header: t('acct.number'), render: (m) => <Link className="ltr" to={`/app/accounting/manual/${m.id}`}>{m.number}</Link> },
            { key: 'd', header: t('acct.date'), render: (m) => formatDate(m.journalDate, lang) },
            { key: 's', header: t('acct.description'), render: (m) => m.description },
            { key: 'b', header: t('acct.branch'), render: (m) => <span className="ltr">{m.branchCode}</span> },
            { key: 't', header: t('acct.amount'), render: (m) => <span className="num">{fmt(m.total)}</span> },
            { key: 'st', header: t('app.status'), render: (m) => <span><Badge tone={manualTone(m.status)}>{t(`acct.manualStatus.${m.status}`)}</Badge>{m.reversed && <Badge tone="warning">{t('acct.reversed')}</Badge>}</span> },
            { key: 'u', header: t('acct.enteredBy'), render: (m) => m.createdBy ?? '' },
          ]} />
      </Card>
      {adding && <ManualForm onClose={() => setAdding(false)} onDone={(id) => nav(`/app/accounting/manual/${id}`)} />}
    </div>
  );
}

interface MLine { accountId: string; branchId: string; debit: string; credit: string; memo: string }

function ManualForm({ journal, onClose, onDone }: { journal?: ManualDetail; onClose: () => void; onDone: (id: string) => void }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const home = workBranchId(me);
  const chart = useQuery({ queryKey: ['chart'], queryFn: () => api.chart() });
  const [branchId, setBranchId] = useState(journal?.journal.branchId ?? home);
  const [date, setDate] = useState(journal?.journal.journalDate ?? businessToday());
  const [description, setDescription] = useState(journal?.journal.description ?? '');
  const [soft, setSoft] = useState(journal?.journal.intoSoftClosed ?? false);
  const [lines, setLines] = useState<MLine[]>(journal?.lines.map((l) => ({ accountId: l.accountId, branchId: l.branchId, debit: l.debit ? String(l.debit) : '', credit: l.credit ? String(l.credit) : '', memo: l.memo ?? '' }))
    ?? [{ accountId: '', branchId: home, debit: '', credit: '', memo: '' }, { accountId: '', branchId: home, debit: '', credit: '', memo: '' }]);
  const set = (i: number, patch: Partial<MLine>) => setLines(lines.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  const dr = lines.reduce((s, l) => s + toBaisa(parseAmount(l.debit) ?? 0), 0n);
  const cr = lines.reduce((s, l) => s + toBaisa(parseAmount(l.credit) ?? 0), 0n);
  const postable = (chart.data ?? []).filter((a) => a.isPostable && a.status === 'active');
  const body = (): ManualLineInput[] => lines.filter((l) => l.accountId).map((l) => ({ accountId: l.accountId, branchId: l.branchId, debit: parseAmount(l.debit) ?? 0, credit: parseAmount(l.credit) ?? 0, memo: l.memo || null }));
  const save = useMutation({
    mutationFn: () => {
      const j = { id: journal?.journal.id ?? newId(), branchId, journalDate: date, description, lines: body(), intoSoftClosed: soft, version: journal?.journal.version };
      return journal ? api.updateManualJournal(journal.journal.id, j) : api.createManualJournal(j);
    },
    onSuccess: (r) => onDone(r.journal.id),
  });
  return (
    <Modal open title={journal ? t('acct.editManual', { number: journal.journal.number }) : t('acct.newManual')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={!description.trim() || body().length < 2} busy={save.isPending} onClick={() => save.mutate()}>{t('acct.saveDraft')}</Button></>}>
      <div className="stack">
        <div className="grid-2">
          <BranchSelect value={branchId} onChange={setBranchId} />
          <TextField label={t('acct.date')} type="date" value={date} onChange={(e) => setDate(e.target.value)} dir="ltr" />
          <TextField label={t('acct.description')} value={description} onChange={(e) => setDescription(e.target.value)} />
        </div>
        {can(me, 'ACCT.PERIOD.CLOSE') && <Checkbox label={t('acct.intoSoftClosed')} checked={soft} onChange={(e) => setSoft(e.target.checked)} />}
        {lines.map((l, i) => (
          <div key={i} className="row line-editor">
            <SelectField label={t('acct.account')} value={l.accountId} onChange={(e) => set(i, { accountId: e.target.value })}>
              <option value="">{t('acct.chooseAccount')}</option>
              {postable.map((a) => <option key={a.id} value={a.id}>{a.code} · {pick(a.nameEn, a.nameAr)}{a.isControl ? ` (${t('acct.control')})` : ''}</option>)}
            </SelectField>
            <BranchSelect value={l.branchId} onChange={(v) => set(i, { branchId: v })} />
            <TextField label={t('acct.debit')} value={l.debit} onChange={(e) => set(i, { debit: e.target.value, credit: e.target.value ? '' : l.credit })} inputMode="decimal" dir="ltr" />
            <TextField label={t('acct.credit')} value={l.credit} onChange={(e) => set(i, { credit: e.target.value, debit: e.target.value ? '' : l.debit })} inputMode="decimal" dir="ltr" />
            <TextField label={t('acct.memo')} value={l.memo} onChange={(e) => set(i, { memo: e.target.value })} />
            {lines.length > 2 && <Button size="sm" variant="ghost" onClick={() => setLines(lines.filter((_, j) => j !== i))}>{t('pos.remove')}</Button>}
          </div>
        ))}
        <div className="row">
          <Button size="sm" onClick={() => setLines([...lines, { accountId: '', branchId, debit: '', credit: '', memo: '' }])}>{t('acct.addLine')}</Button>
          <span className="grow" />
          <span>{t('acct.debit')} <strong className="num">{fmtB(dr)}</strong></span>
          <span>{t('acct.credit')} <strong className="num">{fmtB(cr)}</strong></span>
          {dr !== cr && <Badge tone="danger">{t('acct.notBalanced', { diff: fmtB(dr - cr) })}</Badge>}
        </div>
        <ErrorBox error={save.error} />
      </div>
    </Modal>
  );
}

/** A manual journal: submitted, posted at once within the threshold or waiting for a second person above it, corrected only by reversal. */
export function ManualJournalPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const [dialog, setDialog] = useState<'edit' | 'reject' | 'reverse' | null>(null);
  const [reason, setReason] = useState('');
  const q = useQuery({ queryKey: ['manualJournal', id], queryFn: () => api.manualJournal(id) });
  const act = useMutation({
    mutationFn: (f: () => Promise<unknown>) => f(),
    onSuccess: () => { setDialog(null); setReason(''); void qc.invalidateQueries({ queryKey: ['manualJournal', id] }); void qc.invalidateQueries({ queryKey: ['manualJournals'] }); },
  });
  if (q.isLoading) return <Spinner label={t('app.loading')} />;
  if (!q.data) return <ErrorBox error={q.error} />;
  const d = q.data;
  const j = d.journal;
  const mine = j.createdById === me?.user.id;
  return (
    <div className="stack">
      <PageHeader title={t('acct.manualTitle', { number: j.number })}>
        <Link className="btn" to="/app/accounting?tab=manual">{t('app.back')}</Link>
        {j.status === 'draft' && can(me, 'ACCT.JOURNAL.MANUAL') && <Button onClick={() => setDialog('edit')}>{t('acct.edit')}</Button>}
        {j.status === 'draft' && can(me, 'ACCT.JOURNAL.MANUAL') && <Button variant="primary" busy={act.isPending} onClick={() => act.mutate(() => api.submitManualJournal(id))}>{t('acct.submit')}</Button>}
        {j.status === 'pending_approval' && can(me, 'ACCT.JOURNAL.APPROVE') && !mine && <Button variant="primary" busy={act.isPending} onClick={() => act.mutate(() => api.approveManualJournal(id))}>{t('acct.approve')}</Button>}
        {j.status === 'pending_approval' && can(me, 'ACCT.JOURNAL.APPROVE') && !mine && <Button variant="danger" onClick={() => setDialog('reject')}>{t('acct.reject')}</Button>}
        {j.status === 'posted' && !j.reversalJournalId && can(me, 'ACCT.JOURNAL.MANUAL') && <Button onClick={() => setDialog('reverse')}>{t('acct.reverse')}</Button>}
      </PageHeader>
      <div className="row">
        <Badge tone={manualTone(j.status)}>{t(`acct.manualStatus.${j.status}`)}</Badge>
        <span>{formatDate(j.journalDate, lang)}</span><span className="ltr">{j.branchCode}</span><span>{j.description}</span>
        {j.journalNumber && <Link className="ltr" to={`/app/accounting/journals/${j.journalId}`}>{j.journalNumber}</Link>}
        {j.reversalNumber && <span className="muted">{t('acct.reversedAs', { number: j.reversalNumber })}</span>}
      </div>
      {j.status === 'draft' && toBaisa(j.total) > toBaisa(j.approvalAbove) && <Alert tone="info">{t('acct.needsSecond', { amount: fmt(j.approvalAbove) })}</Alert>}
      {j.status === 'pending_approval' && <Alert tone="warning">{mine ? t('acct.waitingOther') : t('acct.waitingYou')}</Alert>}
      {j.rejectedReason && <Alert tone="error">{t('acct.rejectedBecause', { reason: j.rejectedReason })}</Alert>}
      {j.reversedReason && <Alert tone="info">{t('acct.reversedBecause', { reason: j.reversedReason })}</Alert>}
      <ErrorBox error={act.error} />
      <Card>
        <DataTable rows={d.lines} rowKey={(l) => String(l.lineNo)} empty={t('app.none')}
          columns={[
            { key: 'a', header: t('acct.account'), render: (l) => <span><span className="ltr">{l.accountCode}</span> {pick(l.accountNameEn, l.accountNameAr)} {l.isControl && <Badge tone="primary">{t('acct.control')}</Badge>}</span> },
            { key: 'b', header: t('acct.branch'), render: (l) => <span className="ltr">{l.branchCode}</span> },
            { key: 'm', header: t('acct.memo'), render: (l) => l.memo ?? '' },
            { key: 'd', header: t('acct.debit'), render: (l) => <span className="num">{l.debit ? fmt(l.debit) : ''}</span> },
            { key: 'c', header: t('acct.credit'), render: (l) => <span className="num">{l.credit ? fmt(l.credit) : ''}</span> },
          ]} />
      </Card>
      <p className="muted">{t('acct.enteredByOn', { by: j.createdBy ?? '', at: formatDateTime(j.createdAt, lang) })}{j.approvedBy ? ` · ${t('acct.approvedBy', { by: j.approvedBy })}` : ''}</p>
      {dialog === 'edit' && <ManualForm journal={d} onClose={() => setDialog(null)} onDone={() => { setDialog(null); void qc.invalidateQueries({ queryKey: ['manualJournal', id] }); }} />}
      {(dialog === 'reject' || dialog === 'reverse') && (
        <Modal open title={dialog === 'reject' ? t('acct.reject') : t('acct.reverse')} onClose={() => setDialog(null)}
          footer={<><Button onClick={() => setDialog(null)}>{t('app.cancel')}</Button>
            <Button variant="primary" disabled={!reason.trim()} busy={act.isPending} onClick={() => act.mutate(() => dialog === 'reject' ? api.rejectManualJournal(id, reason) : api.reverseManualJournal(id, reason))}>{t('acct.confirm')}</Button></>}>
          <div className="stack">
            {dialog === 'reverse' && <p>{t('acct.reverseHelp')}</p>}
            <TextField label={t('acct.reason')} value={reason} onChange={(e) => setReason(e.target.value)} />
            <ErrorBox error={act.error} />
          </div>
        </Modal>
      )}
    </div>
  );
}

// ------------------------------------------------------------------ periods (17.5)
const periodTone = (s: string) => (s === 'open' ? 'success' : s === 'soft_closed' ? 'warning' : 'danger') as 'success' | 'warning' | 'danger';

function PeriodsTab() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const [year, setYear] = useState<number | undefined>(undefined);
  const q = useQuery({ queryKey: ['periods', year], queryFn: () => api.periods(year) });
  const [change, setChange] = useState<{ id: string; label: string; branchId: string | null; status: PeriodStatus } | null>(null);
  const [reason, setReason] = useState('');
  const [msg, setMsg] = useState<string | null>(null);
  const [confirmYear, setConfirmYear] = useState(false);
  useEffect(() => { if (q.data && year === undefined) setYear(q.data.fiscalYear); }, [q.data, year]);
  const refresh = (m: string) => { setChange(null); setReason(''); setConfirmYear(false); setMsg(m); void qc.invalidateQueries({ queryKey: ['periods'] }); };
  const set = useMutation({ mutationFn: () => api.setPeriodStatus(change!.id, change!.status, reason, change!.branchId), onSuccess: (r) => refresh(t('acct.periodChanged', { n: r.changed })) });
  const close = useMutation({ mutationFn: () => api.yearEnd(q.data!.fiscalYear), onSuccess: (r) => refresh(t('acct.yearClosed', { year: r.year, profit: fmt(r.profit) })) });
  if (q.isLoading || !q.data) return q.error ? <ErrorBox error={q.error} /> : <Spinner label={t('app.loading')} />;
  const v: PeriodsView = q.data;
  const statusOf = (json: string, branchId: string): PeriodStatus => ((JSON.parse(json) as Record<string, PeriodStatus>)[branchId] ?? 'open');
  const manage = can(me, 'ACCT.PERIOD.CLOSE');
  return (
    <div className="stack">
      <div className="row">
        <SelectField label={t('acct.fiscalYear')} value={String(v.fiscalYear)} onChange={(e) => setYear(Number(e.target.value))}>
          {v.years.map((y) => <option key={y} value={y}>{y}</option>)}
        </SelectField>
        {v.yearClosed ? <Badge tone="danger">{t('acct.yearIsClosed')}</Badge>
          : can(me, 'ACCT.YEAR_END') && <Button onClick={() => setConfirmYear(true)}>{t('acct.closeYear', { year: v.fiscalYear })}</Button>}
      </div>
      <p className="muted">{t('acct.periodsHelp')}</p>
      {msg && <Alert tone="success">{msg}</Alert>}
      <ErrorBox error={set.error ?? close.error} />
      <Card>
        <div className="table-wrap">
          <table className="table">
            <thead><tr><th scope="col">{t('acct.period')}</th>{v.branches.map((b) => <th key={b.id} scope="col" className="ltr">{b.code}</th>)}<th scope="col">{t('acct.journalsCount')}</th>{manage && <th scope="col" />}</tr></thead>
            <tbody>
              {v.periods.map((p) => (
                <tr key={p.id}>
                  <td><strong>{p.periodNo}</strong> <span className="muted">{formatDate(p.startsOn, lang)} – {formatDate(p.endsOn, lang)}</span> {p.vatFiled && <Badge tone="primary">{t('acct.vatFiled')}</Badge>}</td>
                  {v.branches.map((b) => {
                    const s = statusOf(p.branches, b.id);
                    return (
                      <td key={b.id}>
                        {manage ? (
                          <SelectField label={`${b.code} ${p.periodNo}`} value={s} onChange={(e) => setChange({ id: p.id, branchId: b.id, status: e.target.value as PeriodStatus, label: `${p.periodNo} · ${b.code}` })}>
                            {(['open', 'soft_closed', 'closed'] as const).map((x) => <option key={x} value={x}>{t(`acct.periodStatus.${x}`)}</option>)}
                          </SelectField>
                        ) : <Badge tone={periodTone(s)}>{t(`acct.periodStatus.${s}`)}</Badge>}
                      </td>
                    );
                  })}
                  <td className="num">{p.journals}</td>
                  {manage && (
                    <td><SelectField label={t('acct.allBranchesTo', { no: p.periodNo })} value="" onChange={(e) => e.target.value && setChange({ id: p.id, branchId: null, status: e.target.value as PeriodStatus, label: `${p.periodNo} · ${t('acct.allBranches')}` })}>
                      <option value="">{t('acct.allBranchesShort')}</option>
                      {(['open', 'soft_closed', 'closed'] as const).map((x) => <option key={x} value={x}>{t(`acct.periodStatus.${x}`)}</option>)}
                    </SelectField></td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
      {change && (
        <Modal open title={t('acct.changePeriod', { label: change.label, status: t(`acct.periodStatus.${change.status}`) })} onClose={() => setChange(null)}
          footer={<><Button onClick={() => setChange(null)}>{t('app.cancel')}</Button><Button variant="primary" disabled={!reason.trim()} busy={set.isPending} onClick={() => set.mutate()}>{t('acct.confirm')}</Button></>}>
          <div className="stack"><p>{t(`acct.periodHelp.${change.status}`)}</p><TextField label={t('acct.reason')} value={reason} onChange={(e) => setReason(e.target.value)} /><ErrorBox error={set.error} /></div>
        </Modal>
      )}
      {confirmYear && (
        <Modal open title={t('acct.closeYear', { year: v.fiscalYear })} onClose={() => setConfirmYear(false)}
          footer={<><Button onClick={() => setConfirmYear(false)}>{t('app.cancel')}</Button><Button variant="danger" busy={close.isPending} onClick={() => close.mutate()}>{t('acct.closeYearConfirm')}</Button></>}>
          <div className="stack"><p>{t('acct.closeYearHelp')}</p><ErrorBox error={close.error} /></div>
        </Modal>
      )}
    </div>
  );
}

// ------------------------------------------------------------------ card settlements (17.3)
function CardsTab() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const q = useQuery({ queryKey: ['cardSettlements'], queryFn: () => api.cardSettlements() });
  const [branchId, setBranchId] = useState(workBranchId(me));
  const [gross, setGross] = useState('');
  const [fees, setFees] = useState('');
  const [reference, setReference] = useState('');
  const [msg, setMsg] = useState<string | null>(null);
  const settle = useMutation({
    mutationFn: () => api.settleCards({ id: newId(), branchId, grossAmount: parseAmount(gross)!, fees: parseAmount(fees) ?? 0, reference }),
    onSuccess: (r) => { setMsg(t('acct.settled', { number: r.number })); setGross(''); setFees(''); setReference(''); void qc.invalidateQueries({ queryKey: ['cardSettlements'] }); },
  });
  const net = (parseAmount(gross) ?? 0) - (parseAmount(fees) ?? 0);
  return (
    <div className="stack">
      <Card>
        <h2>{t('acct.cardClearing')}</h2>
        <DataTable rows={q.data?.clearing ?? []} rowKey={(c) => c.branchId} empty={t('app.none')}
          columns={[
            { key: 'b', header: t('acct.branch'), render: (c) => <span><span className="ltr">{c.branchCode}</span> {pick(c.nameEn, c.nameAr)}</span> },
            { key: 'v', header: t('acct.waitingFromBank'), render: (c) => <span className="num">{fmt(c.balance)}</span> },
          ]} />
      </Card>
      {can(me, 'ACCT.CARD_SETTLE') && (
        <Card>
          <h2>{t('acct.recordSettlement')}</h2>
          <div className="grid-2">
            <BranchSelect value={branchId} onChange={setBranchId} />
            <TextField label={t('acct.settledGross')} value={gross} onChange={(e) => setGross(e.target.value)} inputMode="decimal" dir="ltr" />
            <TextField label={t('acct.fees')} value={fees} onChange={(e) => setFees(e.target.value)} inputMode="decimal" dir="ltr" />
            <TextField label={t('acct.bankReference')} value={reference} onChange={(e) => setReference(e.target.value)} dir="ltr" />
          </div>
          <p className="muted">{t('acct.toBank', { amount: fmt(Math.max(0, Math.round(net * 1000) / 1000)) })}</p>
          {msg && <Alert tone="success">{msg}</Alert>}
          <ErrorBox error={settle.error} />
          <div className="row"><Button variant="primary" disabled={!parseAmount(gross) || !reference.trim()} busy={settle.isPending} onClick={() => settle.mutate()}>{t('acct.record')}</Button></div>
        </Card>
      )}
      <Card>
        <DataTable rows={q.data?.settlements ?? []} rowKey={(s) => s.id} empty={t('app.none')}
          columns={[
            { key: 'n', header: t('acct.number'), render: (s) => <span className="ltr">{s.number}</span> },
            { key: 'd', header: t('acct.date'), render: (s) => formatDate(s.settlementDate, lang) },
            { key: 'b', header: t('acct.branch'), render: (s) => <span className="ltr">{s.branchCode}</span> },
            { key: 'g', header: t('acct.settledGross'), render: (s) => <span className="num">{fmt(s.grossAmount)}</span> },
            { key: 'f', header: t('acct.fees'), render: (s) => <span className="num">{fmt(s.fees)}</span> },
            { key: 'r', header: t('acct.bankReference'), render: (s) => <span className="ltr">{s.reference}</span> },
          ]} />
      </Card>
    </div>
  );
}

// ------------------------------------------------------------------ the daily ledger check (17.8)
function CheckTab() {
  const api = useApi();
  const qc = useQueryClient();
  const { t, lang } = useI18n();
  const q = useQuery({ queryKey: ['reconciliation'], queryFn: () => api.reconciliation() });
  const run = useMutation({ mutationFn: () => api.runReconciliation(), onSuccess: (r) => qc.setQueryData(['reconciliation'], r) });
  const rows = q.data?.rows ?? [];
  return (
    <div className="stack">
      <div className="row"><Button variant="primary" busy={run.isPending} onClick={() => run.mutate()}>{t('acct.runCheck')}</Button>
        {rows[0] && <span className="muted">{t('acct.checkedAt', { at: formatDateTime(rows[0].checkedAt, lang) })}</span>}
        {q.data?.clean === true && <Badge tone="success">{t('acct.allAgree')}</Badge>}
        {q.data?.clean === false && <Badge tone="danger">{t('acct.differences')}</Badge>}</div>
      <p className="muted">{t('acct.checkHelp')}</p>
      <ErrorBox error={q.error ?? run.error} />
      <Card>
        <DataTable rows={rows} rowKey={(r) => `${r.ledger}-${r.branchId ?? 'all'}`} empty={t('acct.notRunYet')}
          columns={[
            { key: 'l', header: t('acct.ledger'), render: (r) => t(`acct.ledgers.${r.ledger}`) },
            { key: 'b', header: t('acct.branch'), render: (r) => <span className="ltr">{r.branchCode ?? t('acct.business')}</span> },
            { key: 's', header: t('acct.subledger'), render: (r) => <span className="num">{fmt(r.subledger)}</span> },
            { key: 'c', header: t('acct.controlAccount'), render: (r) => <span className="num">{fmt(r.control)}</span> },
            { key: 'd', header: t('acct.difference'), render: (r) => <span className={`num${r.difference ? ' neg' : ''}`}>{r.difference ? fmt(r.difference) : '✓'}</span> },
          ]} />
      </Card>
    </div>
  );
}
