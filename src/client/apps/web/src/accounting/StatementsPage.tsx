import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import type { BranchAsOf, StatementRow } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Card, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { businessToday, ErrorBox, formatDateTime, useApi } from '../core';
import { fmt } from '../money';
import { BranchSelect } from '../purchasing/bits';

type Report = 'trial' | 'pl' | 'bs' | 'cf' | 'ledger';
const REPORTS: Report[] = ['trial', 'pl', 'bs', 'cf', 'ledger'];

/** Financial statements (17.7): by branch or consolidated, each branch's data shown with how recent it is. */
export function StatementsPage() {
  const api = useApi();
  const { t, pick } = useI18n();
  const [params, setParams] = useSearchParams();
  const report = (REPORTS.includes(params.get('r') as Report) ? params.get('r') : 'trial') as Report;
  const today = businessToday();
  const [from, setFrom] = useState(`${today.slice(0, 4)}-01-01`);
  const [to, setTo] = useState(today);
  const [branchId, setBranchId] = useState('');
  const [accountId, setAccountId] = useState(params.get('account') ?? '');
  const f = { from, to, branchId: branchId || undefined };
  const chart = useQuery({ queryKey: ['chart'], queryFn: () => api.chart(), enabled: report === 'ledger' });
  const tb = useQuery({ queryKey: ['tb', f], enabled: report === 'trial', queryFn: () => api.trialBalance(f) });
  const pl = useQuery({ queryKey: ['pl', f], enabled: report === 'pl', queryFn: () => api.profitAndLoss(f) });
  const bs = useQuery({ queryKey: ['bs', to, branchId], enabled: report === 'bs', queryFn: () => api.balanceSheet({ asOf: to, branchId: branchId || undefined }) });
  const cf = useQuery({ queryKey: ['cf', f], enabled: report === 'cf', queryFn: () => api.cashFlow(f) });
  const gl = useQuery({ queryKey: ['gl', accountId, f], enabled: report === 'ledger' && !!accountId, queryFn: () => api.generalLedger(accountId, f) });
  const loading = [tb, pl, bs, cf, gl].some((x) => x.isLoading && x.fetchStatus !== 'idle');
  const asOf = tb.data?.asOf ?? pl.data?.asOf ?? bs.data?.branches ?? cf.data?.asOf;
  return (
    <div className="stack">
      <PageHeader title={t('acct.statements')}><Link className="btn" to="/app/accounting">{t('app.back')}</Link></PageHeader>
      <div className="tabs" role="tablist">
        {REPORTS.map((k) => <button key={k} role="tab" aria-selected={report === k} className={`tab${report === k ? ' active' : ''}`} onClick={() => setParams({ r: k })}>{t(`acct.reports.${k}`)}</button>)}
      </div>
      <div className="row">
        {report !== 'bs' && <TextField label={t('acct.from')} type="date" value={from} onChange={(e) => setFrom(e.target.value)} dir="ltr" />}
        <TextField label={report === 'bs' ? t('acct.asOf') : t('acct.to')} type="date" value={to} onChange={(e) => setTo(e.target.value)} dir="ltr" />
        <BranchSelect value={branchId} onChange={setBranchId} any={t('acct.consolidated')} />
        {report === 'ledger' && (
          <SelectField label={t('acct.account')} value={accountId} onChange={(e) => setAccountId(e.target.value)}>
            <option value="">{t('acct.chooseAccount')}</option>
            {(chart.data ?? []).filter((a) => a.isPostable).map((a) => <option key={a.id} value={a.id}>{a.code} · {pick(a.nameEn, a.nameAr)}</option>)}
          </SelectField>
        )}
      </div>
      <ErrorBox error={tb.error ?? pl.error ?? bs.error ?? cf.error ?? gl.error} />
      {loading && <Spinner label={t('app.loading')} />}
      {asOf && <AsOf rows={asOf} />}
      {report === 'trial' && tb.data && (
        <Card>
          {!tb.data.balanced && <Alert tone="error">{t('acct.tbNotBalanced')}</Alert>}
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th scope="col">{t('acct.account')}</th><th scope="col">{t('acct.opening')}</th><th scope="col">{t('acct.debit')}</th><th scope="col">{t('acct.credit')}</th><th scope="col">{t('acct.closing')}</th></tr></thead>
              <tbody>
                {tb.data.rows.map((r) => (
                  <tr key={r.id}><td><Link className="ltr" to={`/app/accounting/statements?r=ledger&account=${r.id}`}>{r.code}</Link> {pick(r.nameEn, r.nameAr)}</td>
                    <td className="num">{fmt(r.opening)}</td><td className="num">{fmt(r.debit)}</td><td className="num">{fmt(r.credit)}</td><td className="num">{fmt(r.closing)}</td></tr>
                ))}
                {tb.data.earlierResult !== 0 && <tr><td>{t('acct.earlierResult')}</td><td className="num">{fmt(tb.data.earlierResult)}</td><td /><td /><td className="num">{fmt(tb.data.earlierResult)}</td></tr>}
              </tbody>
              <tfoot>
                <tr><th scope="row">{t('acct.totalDebit')}</th><td className="num strong">{fmt(tb.data.totals.openingDebit)}</td><td className="num strong">{fmt(tb.data.totals.debit)}</td><td /><td className="num strong">{fmt(tb.data.totals.closingDebit)}</td></tr>
                <tr><th scope="row">{t('acct.totalCredit')}</th><td className="num strong">{fmt(tb.data.totals.openingCredit)}</td><td /><td className="num strong">{fmt(tb.data.totals.credit)}</td><td className="num strong">{fmt(tb.data.totals.closingCredit)}</td></tr>
              </tfoot>
            </table>
          </div>
          {tb.data.balanced && <Badge tone="success">{t('acct.balanced')}</Badge>}
        </Card>
      )}
      {report === 'pl' && pl.data && (
        <Card>
          <Section title={t('acct.revenue')} rows={pl.data.revenue} total={pl.data.totalRevenue} />
          <Section title={t('acct.costOfSales')} rows={pl.data.costOfSales} total={pl.data.totalCostOfSales} />
          <Line label={t('acct.grossProfit')} value={pl.data.grossProfit} strong />
          <Section title={t('acct.expenses')} rows={pl.data.expenses} total={pl.data.totalExpenses} />
          <Line label={t('acct.netProfit')} value={pl.data.netProfit} strong />
        </Card>
      )}
      {report === 'bs' && bs.data && (
        <Card>
          {!bs.data.balanced && <Alert tone="error">{t('acct.bsNotBalanced')}</Alert>}
          <Section title={t('acct.assets')} rows={bs.data.assets} />
          {bs.data.elimination.eliminated !== 0 && <Line label={t('acct.eliminated')} value={-bs.data.elimination.eliminated} />}
          <Line label={t('acct.totalAssets')} value={bs.data.totalAssets} strong />
          <Section title={t('acct.liabilities')} rows={bs.data.liabilities} />
          {bs.data.elimination.eliminated !== 0 && <Line label={t('acct.eliminated')} value={-bs.data.elimination.eliminated} />}
          <Line label={t('acct.totalLiabilities')} value={bs.data.totalLiabilities} strong />
          <Section title={t('acct.equity')} rows={bs.data.equity} />
          <Line label={t('acct.yearResult')} value={bs.data.yearResult} />
          {bs.data.earlierResult !== 0 && <Line label={t('acct.earlierResult')} value={bs.data.earlierResult} />}
          <Line label={t('acct.totalEquity')} value={bs.data.totalEquity} strong />
          {branchId === '' && bs.data.elimination.difference !== 0 && <Alert tone="warning">{t('acct.interbranchDifference', { amount: fmt(bs.data.elimination.difference) })}</Alert>}
          {bs.data.balanced && <Badge tone="success">{t('acct.balanced')}</Badge>}
        </Card>
      )}
      {report === 'cf' && cf.data && (
        <Card>
          <Line label={t('acct.netProfit')} value={cf.data.netProfit} />
          <Section title={t('acct.operatingChanges')} rows={cf.data.operatingChanges} />
          <Line label={t('acct.operating')} value={cf.data.operating} strong />
          <Section title={t('acct.investing')} rows={cf.data.investingItems} total={cf.data.investing} />
          <Section title={t('acct.financing')} rows={cf.data.financingItems} total={cf.data.financing} />
          <Line label={t('acct.netChange')} value={cf.data.netChange} strong />
          <Line label={t('acct.openingCash')} value={cf.data.openingCash} />
          <Line label={t('acct.closingCash')} value={cf.data.closingCash} strong />
          {cf.data.difference !== 0 && <Alert tone="error">{t('acct.cfDifference', { amount: fmt(cf.data.difference) })}</Alert>}
        </Card>
      )}
      {report === 'ledger' && gl.data && (
        <Card>
          <h2><span className="ltr">{gl.data.account.code}</span> {pick(gl.data.account.nameEn, gl.data.account.nameAr)}</h2>
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th scope="col">{t('acct.date')}</th><th scope="col">{t('acct.number')}</th><th scope="col">{t('acct.description')}</th><th scope="col">{t('acct.branch')}</th><th scope="col">{t('acct.debit')}</th><th scope="col">{t('acct.credit')}</th><th scope="col">{t('acct.balance')}</th></tr></thead>
              <tbody>
                <tr><td colSpan={6}>{t('acct.opening')}</td><td className="num">{fmt(gl.data.opening)}</td></tr>
                {gl.data.lines.map((l, i) => (
                  <tr key={i}><td>{l.date}</td><td><Link className="ltr" to={`/app/accounting/journals/${l.journalId}`}>{l.number}</Link></td><td>{l.description}{l.partyName ? ` · ${l.partyName}` : ''}</td>
                    <td className="ltr">{l.branchCode ?? ''}</td><td className="num">{l.debit ? fmt(l.debit) : ''}</td><td className="num">{l.credit ? fmt(l.credit) : ''}</td><td className="num">{fmt(l.balance)}</td></tr>
                ))}
              </tbody>
              <tfoot><tr><th scope="row" colSpan={6}>{t('acct.closing')}</th><td className="num strong">{fmt(gl.data.closing)}</td></tr></tfoot>
            </table>
          </div>
        </Card>
      )}
    </div>
  );
}

function Section({ title, rows, total }: { title: string; rows: StatementRow[]; total?: number }) {
  const { pick } = useI18n();
  return (
    <div className="stack statement-section">
      <h3>{title}</h3>
      {rows.map((r) => <div key={r.code} className="row statement-line"><span className="grow"><span className="ltr muted">{r.code}</span> {pick(r.nameEn, r.nameAr)}</span><span className="num">{fmt(r.amount)}</span></div>)}
      {total !== undefined && <div className="row statement-line"><strong className="grow">{title}</strong><strong className="num">{fmt(total)}</strong></div>}
    </div>
  );
}

function Line({ label, value, strong }: { label: string; value: number; strong?: boolean }) {
  return <div className="row statement-line">{strong ? <strong className="grow">{label}</strong> : <span className="grow">{label}</span>}<span className={`num${strong ? ' strong' : ''}${value < 0 ? ' neg' : ''}`}>{fmt(value)}</span></div>;
}

/** Each branch's figures and how recent they are: this server's own branches now, a branch server's as of its last contact. */
function AsOf({ rows }: { rows: BranchAsOf[] }) {
  const { t, lang } = useI18n();
  return (
    <p className="muted">{t('acct.dataAsOf')} {rows.map((r) => `${r.code} ${r.onThisServer ? t('acct.now') : formatDateTime(r.asOf, lang)}`).join(' · ')}</p>
  );
}
