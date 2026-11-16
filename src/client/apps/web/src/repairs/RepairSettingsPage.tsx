import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type RepairSettings } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Button, Card, Checkbox, DataTable, PageHeader, SelectField, Spinner, Switch, TextField } from '@kite/ui';
import { ErrorBox, useApi, useAuth } from '../core';
import { fmt } from '../money';

const NOTIFY = ['received', 'waiting_approval', 'ready'] as const;

/** Repair settings (13.1-13.6): defaults, the deposit VAT point, terms, kinds of repair and fault codes. */
export function RepairSettingsPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const q = useQuery({ queryKey: ['repairSetup'], queryFn: () => api.repairSetup() });
  const [s, setS] = useState<RepairSettings | null>(null);
  const [days, setDays] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const [cat, setCat] = useState({ code: '', nameEn: '', nameAr: '', turnaroundHours: '24' });
  const [fault, setFault] = useState({ code: '', nameEn: '', nameAr: '', categoryId: '' });
  useEffect(() => { if (q.data) { setS(q.data.settings); setDays(q.data.settings.uncollectedDays.join(', ')); } }, [q.data]);
  if (!q.data || !s) return q.error ? <ErrorBox error={q.error} /> : <Spinner label={t('app.loading')} />;
  const edit = can(me, 'REPAIR.SETTINGS');
  const refresh = () => qc.invalidateQueries({ queryKey: ['repairSetup'] });
  const num = (k: keyof RepairSettings) => (e: React.ChangeEvent<HTMLInputElement>) => setS({ ...s, [k]: Number(e.target.value) });
  const save = () => {
    setError(null); setSaved(false);
    api.updateRepairSettings({ ...s, uncollectedDays: days.split(/[ ,]+/).filter(Boolean).map(Number) }).then(() => { setSaved(true); void refresh(); }).catch(setError);
  };
  return (
    <div className="stack">
      <PageHeader title={t('repair.settings.title')} />
      <ErrorBox error={error} />
      {saved && <Alert tone="success">{t('repair.settings.saved')}</Alert>}
      <Card>
        <div className="stack">
          <strong>{t('repair.settings.general')}</strong>
          <div className="grid-2">
            <TextField label={t('repair.settings.preAuthorised')} className="ltr num" inputMode="decimal" value={String(s.preAuthorised)} onChange={num('preAuthorised')} disabled={!edit} />
            <TextField label={t('repair.settings.warrantyDays')} className="ltr num" inputMode="numeric" value={String(s.warrantyDays)} onChange={num('warrantyDays')} disabled={!edit} />
            <TextField label={t('repair.settings.passcodeKeepDays')} className="ltr num" inputMode="numeric" value={String(s.passcodeKeepDays)} onChange={num('passcodeKeepDays')} disabled={!edit} />
            <TextField label={t('repair.settings.uncollectedDays')} className="ltr" value={days} onChange={(e) => setDays(e.target.value)} disabled={!edit} />
            <TextField label={t('repair.settings.diagnosisFee')} className="ltr num" inputMode="decimal" value={String(s.diagnosisFee)} onChange={num('diagnosisFee')} disabled={!edit} />
            <TextField label={t('repair.settings.hourlyRate')} className="ltr num" inputMode="decimal" value={String(s.hourlyRate)} onChange={num('hourlyRate')} disabled={!edit} />
          </div>
          <Switch label={t('repair.settings.depositVat')} checked={s.depositVatPoint} onChange={(v) => setS({ ...s, depositVatPoint: v })} disabled={!edit || !can(me, 'TAX.MANAGE')} />
          <p className="muted">{t('repair.settings.depositVatHelp')}</p>
          <fieldset className="chips"><legend>{t('repair.settings.notifyStatuses')}</legend>
            {NOTIFY.map((k) => <Checkbox key={k} label={t(`repair.statuses.${k}`)} checked={s.notifyStatuses.includes(k)} disabled={!edit} onChange={(e) => setS({ ...s, notifyStatuses: e.target.checked ? [...s.notifyStatuses, k] : s.notifyStatuses.filter((x) => x !== k) })} />)}
          </fieldset>
          <div className="field"><label htmlFor="terms-en">{t('repair.settings.termsEn')}</label><textarea id="terms-en" className="input" rows={4} value={s.termsEn} onChange={(e) => setS({ ...s, termsEn: e.target.value })} disabled={!edit} /></div>
          <div className="field"><label htmlFor="terms-ar">{t('repair.settings.termsAr')}</label><textarea id="terms-ar" className="input" rows={4} dir="rtl" value={s.termsAr} onChange={(e) => setS({ ...s, termsAr: e.target.value })} disabled={!edit} /></div>
          {edit && <div><Button variant="primary" onClick={save}>{t('app.save')}</Button></div>}
        </div>
      </Card>
      <Card>
        <div className="stack">
          <strong>{t('repair.settings.categories')}</strong>
          <DataTable rows={q.data.categories} rowKey={(c) => c.id} empty="—"
            columns={[
              { key: 'c', header: t('repair.settings.code'), render: (c) => <span className="ltr mono">{c.code}</span> },
              { key: 'n', header: t('repair.settings.nameEn'), render: (c) => pick(c.nameEn, c.nameAr) },
              { key: 't', header: t('repair.settings.turnaround'), render: (c) => <span className="num">{c.turnaroundHours}</span> },
              { key: 'w', header: t('repair.settings.warrantyDays'), render: (c) => <span className="num">{c.warrantyDays ?? s.warrantyDays}</span> },
              { key: 'p', header: t('repair.settings.preAuthorised'), render: (c) => <span className="num">{fmt(c.preAuthorised ?? s.preAuthorised)}</span> },
              { key: 's', header: t('app.status'), render: (c) => edit ? <Switch label={t('app.active')} checked={c.status === 'active'} onChange={(v) => api.updateRepairCategory(c.id, { ...c, status: v ? 'active' : 'inactive' }).then(refresh).catch(setError)} /> : t(`app.${c.status}`) },
            ]} />
          {edit && (
            <div className="row">
              <TextField label={t('repair.settings.code')} className="ltr" value={cat.code} onChange={(e) => setCat({ ...cat, code: e.target.value })} />
              <TextField label={t('repair.settings.nameEn')} value={cat.nameEn} onChange={(e) => setCat({ ...cat, nameEn: e.target.value })} />
              <TextField label={t('repair.settings.nameAr')} dir="rtl" value={cat.nameAr} onChange={(e) => setCat({ ...cat, nameAr: e.target.value })} />
              <TextField label={t('repair.settings.turnaround')} className="ltr num" value={cat.turnaroundHours} onChange={(e) => setCat({ ...cat, turnaroundHours: e.target.value })} />
              <Button onClick={() => api.createRepairCategory({ code: cat.code, nameEn: cat.nameEn, nameAr: cat.nameAr, turnaroundHours: Number(cat.turnaroundHours), warrantyDays: null, preAuthorised: null })
                .then(() => { setCat({ code: '', nameEn: '', nameAr: '', turnaroundHours: '24' }); void refresh(); }).catch(setError)}>{t('repair.settings.add')}</Button>
            </div>
          )}
        </div>
      </Card>
      <Card>
        <div className="stack">
          <strong>{t('repair.settings.faultCodes')}</strong>
          <DataTable rows={q.data.faultCodes} rowKey={(f) => f.id} empty="—"
            columns={[
              { key: 'c', header: t('repair.settings.code'), render: (f) => <span className="ltr mono">{f.code}</span> },
              { key: 'n', header: t('repair.settings.nameEn'), render: (f) => pick(f.nameEn, f.nameAr) },
              { key: 'k', header: t('repair.settings.category'), render: (f) => { const c = q.data.categories.find((x) => x.id === f.categoryId); return c ? pick(c.nameEn, c.nameAr) : t('repair.settings.anyCategory'); } },
              { key: 's', header: t('app.status'), render: (f) => edit ? <Switch label={t('app.active')} checked={f.status === 'active'} onChange={(v) => api.updateFaultCode(f.id, { ...f, status: v ? 'active' : 'inactive' }).then(refresh).catch(setError)} /> : t(`app.${f.status}`) },
            ]} />
          {edit && (
            <div className="row">
              <TextField label={t('repair.settings.code')} className="ltr" value={fault.code} onChange={(e) => setFault({ ...fault, code: e.target.value })} />
              <TextField label={t('repair.settings.nameEn')} value={fault.nameEn} onChange={(e) => setFault({ ...fault, nameEn: e.target.value })} />
              <TextField label={t('repair.settings.nameAr')} dir="rtl" value={fault.nameAr} onChange={(e) => setFault({ ...fault, nameAr: e.target.value })} />
              <SelectField label={t('repair.settings.category')} value={fault.categoryId} onChange={(e) => setFault({ ...fault, categoryId: e.target.value })}>
                <option value="">{t('repair.settings.anyCategory')}</option>
                {q.data.categories.map((c) => <option key={c.id} value={c.id}>{pick(c.nameEn, c.nameAr)}</option>)}
              </SelectField>
              <Button onClick={() => api.createFaultCode({ code: fault.code, nameEn: fault.nameEn, nameAr: fault.nameAr, categoryId: fault.categoryId || null })
                .then(() => { setFault({ code: '', nameEn: '', nameAr: '', categoryId: '' }); void refresh(); }).catch(setError)}>{t('repair.settings.add')}</Button>
            </div>
          )}
        </div>
      </Card>
    </div>
  );
}
