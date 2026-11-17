import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type CreditPolicy } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Button, Card, PageHeader, Spinner, TextField } from '@kite/ui';
import { ErrorBox, useApi, useAuth } from '../core';

const fields: [keyof CreditPolicy, string, 'int' | 'money'][] = [
  ['defaultTermsDays', 'credit.policy.defaultTerms', 'int'], ['blockOverdueDays', 'credit.policy.blockOverdue', 'int'], ['holdOverdueDays', 'credit.policy.holdOverdue', 'int'],
  ['offlineSaleCap', 'credit.policy.offlineCap', 'money'], ['overrideCap', 'credit.policy.overrideCap', 'money'], ['maxInstalments', 'credit.policy.maxInstalments', 'int'],
  ['onlineTimeoutMs', 'credit.policy.timeout', 'int'], ['authorisationMinutes', 'credit.policy.reservation', 'int'], ['remindBeforeDays', 'credit.policy.remindBefore', 'int'],
  ['remindOverdueEvery', 'credit.policy.remindEvery', 'int'],
];

/** The credit policy (14.2 to 14.5), set at head office for every branch. */
export function CreditSettingsPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t } = useI18n();
  const q = useQuery({ queryKey: ['creditPolicy'], queryFn: () => api.creditPolicy() });
  const [form, setForm] = useState<Record<string, string>>({});
  const [overcommit, setOvercommit] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (!q.data) return;
    setForm(Object.fromEntries(fields.map(([k]) => [k, String(q.data[k])])));
    setOvercommit(q.data.allowOvercommit);
  }, [q.data]);
  if (q.isLoading) return <Spinner label={t('app.loading')} />;
  const editable = can(me, 'CREDIT.MANAGE') && me?.serverRole !== 'branch';
  const valid = fields.every(([k, , kind]) => (kind === 'int' ? /^\d+$/ : /^\d+(\.\d{1,3})?$/).test((form[k] ?? '').trim()));
  const save = () => api.updateCreditPolicy({ ...q.data!, ...Object.fromEntries(fields.map(([k]) => [k, Number(form[k])])), allowOvercommit: overcommit } as CreditPolicy)
    .then(() => { setMsg(t('credit.saved')); void qc.invalidateQueries({ queryKey: ['creditPolicy'] }); }).catch(setError);
  return (
    <div className="stack">
      <PageHeader title={t('credit.settingsTitle')}>{editable && <Button variant="primary" disabled={!valid} onClick={save}>{t('credit.save')}</Button>}</PageHeader>
      <p className="muted">{t('credit.policy.intro')}</p>
      {!editable && <Alert tone="info">{t('credit.headOfficeOnly')}</Alert>}
      {msg && <Alert tone="success">{msg}</Alert>}
      <ErrorBox error={error ?? q.error} />
      <Card>
        <div className="grid-2">
          {fields.map(([k, label]) => <TextField key={k} label={t(label)} value={form[k] ?? ''} onChange={(e) => setForm({ ...form, [k]: e.target.value })} inputMode="decimal" dir="ltr" disabled={!editable} />)}
        </div>
        <label className="check"><input type="checkbox" checked={overcommit} disabled={!editable} onChange={(e) => setOvercommit(e.target.checked)} /> {t('credit.policy.overcommit')}</label>
      </Card>
    </div>
  );
}
