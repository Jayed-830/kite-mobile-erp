import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type ExpenseRow, type PayMethod } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { purchaseNet, purchaseTax, toBaisa } from '@kite/pos';
import { Alert, Button, Card, DataTable, Modal, PageHeader, SelectField, TextField } from '@kite/ui';
import { businessToday, ErrorBox, formatDate, useApi, useAuth, workBranchId } from '../core';
import { fmt, fmtB, parseAmount } from '../money';
import { BranchSelect, newId } from '../purchasing/bits';

/** Expenses paid in cash or by bank (17.3): rent, utilities, small purchases, with their VAT input. */
export function ExpensesPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const today = businessToday();
  const [from, setFrom] = useState(`${today.slice(0, 7)}-01`);
  const [to, setTo] = useState(today);
  const [branchId, setBranchId] = useState('');
  const [adding, setAdding] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const list = useQuery({ queryKey: ['expenses', from, to, branchId], queryFn: () => api.expenses({ from, to, branchId: branchId || undefined }) });
  const total = (list.data ?? []).reduce((s, e) => s + toBaisa(e.grossAmount), 0n);
  return (
    <div className="stack">
      <PageHeader title={t('exp.title')}>{can(me, 'EXP.CREATE') && <Button variant="primary" onClick={() => setAdding(true)}>{t('exp.add')}</Button>}</PageHeader>
      {msg && <Alert tone="success">{msg}</Alert>}
      <div className="row">
        <TextField label={t('acct.from')} type="date" value={from} onChange={(e) => setFrom(e.target.value)} dir="ltr" />
        <TextField label={t('acct.to')} type="date" value={to} onChange={(e) => setTo(e.target.value)} dir="ltr" />
        <BranchSelect value={branchId} onChange={setBranchId} any={t('acct.allBranches')} />
      </div>
      <ErrorBox error={list.error} />
      <Card>
        <DataTable<ExpenseRow> rows={list.data ?? []} rowKey={(e) => e.id} empty={t('exp.none')}
          columns={[
            { key: 'n', header: t('acct.number'), render: (e) => <span className="ltr">{e.number}</span> },
            { key: 'd', header: t('acct.date'), render: (e) => formatDate(e.expenseDate, lang) },
            { key: 'b', header: t('acct.branch'), render: (e) => <span className="ltr">{e.branchCode}</span> },
            { key: 'a', header: t('acct.account'), render: (e) => <span><span className="ltr muted">{e.accountCode}</span> {pick(e.accountNameEn, e.accountNameAr)}</span> },
            { key: 's', header: t('acct.description'), render: (e) => <span>{e.description}{e.payee ? <span className="muted"> · {e.payee}</span> : null}</span> },
            { key: 'p', header: t('exp.paidBy'), render: (e) => t(`purch.methods.${e.paidBy}`) },
            { key: 'v', header: t('purch.vat'), render: (e) => <span className="num">{e.taxAmount ? fmt(e.taxAmount) : ''}</span> },
            { key: 'g', header: t('purch.total'), render: (e) => <strong className="num">{fmt(e.grossAmount)}</strong> },
          ]} />
        <div className="row figures-row"><span>{t('exp.totalShown')} <strong className="num">{fmtB(total)}</strong></span></div>
      </Card>
      {adding && <ExpenseForm onClose={() => setAdding(false)} onDone={(m) => { setAdding(false); setMsg(m); void qc.invalidateQueries({ queryKey: ['expenses'] }); }} />}
    </div>
  );
}

function ExpenseForm({ onClose, onDone }: { onClose: () => void; onDone: (m: string) => void }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const accounts = useQuery({ queryKey: ['expenseAccounts'], queryFn: () => api.expenseAccounts() });
  const taxes = useQuery({ queryKey: ['taxCodes'], queryFn: () => api.taxCodes() });
  const [branchId, setBranchId] = useState(workBranchId(me));
  const [accountId, setAccountId] = useState('');
  const [description, setDescription] = useState('');
  const [payee, setPayee] = useState('');
  const [amount, setAmount] = useState('');
  const [taxCodeId, setTaxCodeId] = useState('');
  const [paidBy, setPaidBy] = useState<PayMethod>('cash');
  const [reference, setReference] = useState('');
  const [date, setDate] = useState(businessToday());
  const codes = (taxes.data ?? []).filter((c) => c.status === 'active' && c.kind !== 'margin');
  const tax = taxCodeId || codes.find((c) => c.code === 'VAT-STD')?.id || '';
  const rate = codes.find((c) => c.id === tax)?.rates.at(-1)?.ratePercent ?? 0;
  const net = parseAmount(amount);
  const vat = net == null ? 0n : purchaseTax(purchaseNet(1000n, toBaisa(net)), rate);
  const save = useMutation({
    mutationFn: () => api.recordExpense({ id: newId(), branchId, accountId, description, netAmount: net!, taxCodeId: tax, paidBy, expenseDate: date, payee: payee || null, reference: reference || null }),
    onSuccess: (r) => onDone(t('exp.recorded', { number: r.number })),
  });
  return (
    <Modal open title={t('exp.add')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={!accountId || !description.trim() || !net || !tax || (paidBy !== 'cash' && !reference.trim())} busy={save.isPending} onClick={() => save.mutate()}>{t('exp.record')}</Button></>}>
      <div className="stack">
        <div className="grid-2">
          <BranchSelect value={branchId} onChange={setBranchId} />
          <TextField label={t('acct.date')} type="date" value={date} onChange={(e) => setDate(e.target.value)} dir="ltr" />
          <SelectField label={t('acct.account')} value={accountId} onChange={(e) => setAccountId(e.target.value)}>
            <option value="">{t('acct.chooseAccount')}</option>
            {(accounts.data ?? []).map((a) => <option key={a.id} value={a.id}>{a.code} · {pick(a.nameEn, a.nameAr)}</option>)}
          </SelectField>
          <TextField label={t('acct.description')} value={description} onChange={(e) => setDescription(e.target.value)} />
          <TextField label={t('exp.payee')} value={payee} onChange={(e) => setPayee(e.target.value)} />
          <TextField label={t('purch.amountBeforeVat')} value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" dir="ltr" />
          <SelectField label={t('exp.taxCode')} value={tax} onChange={(e) => setTaxCodeId(e.target.value)}>
            {codes.map((c) => <option key={c.id} value={c.id}>{c.code} · {pick(c.nameEn, c.nameAr)}</option>)}
          </SelectField>
          <SelectField label={t('exp.paidBy')} value={paidBy} onChange={(e) => setPaidBy(e.target.value as PayMethod)}>
            {(['cash', 'bank_transfer', 'cheque'] as const).map((m) => <option key={m} value={m}>{t(`purch.methods.${m}`)}</option>)}
          </SelectField>
          {paidBy !== 'cash' && <TextField label={t('purch.reference')} value={reference} onChange={(e) => setReference(e.target.value)} dir="ltr" />}
        </div>
        <div className="row figures-row"><span>{t('purch.vat')} <strong className="num">{fmtB(vat)}</strong></span>
          <span>{t('purch.total')} <strong className="num">{fmtB((net == null ? 0n : toBaisa(net)) + vat)}</strong></span></div>
        <ErrorBox error={save.error} />
      </div>
    </Modal>
  );
}
