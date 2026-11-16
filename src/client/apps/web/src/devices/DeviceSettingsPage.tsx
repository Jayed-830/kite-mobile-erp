import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type ChecklistItemDef, type Grade } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Button, Card, Checkbox, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi, useAuth } from '../core';
import { parseAmount } from '../money';

/** Device settings (12.1, 12.2, 12.4): the labour rate, how long carts hold a device, grades and their price share, the inspection checklist. */
export function DeviceSettingsPage() {
  const { t } = useI18n();
  return (
    <div className="stack">
      <PageHeader title={t('devices.settings.title')} />
      <RatesCard />
      <GradesCard />
      <ChecklistCard />
      <TacCard />
      <SerialFormatsCard />
    </div>
  );
}

function RatesCard() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t } = useI18n();
  const s = useQuery({ queryKey: ['deviceSettings'], queryFn: () => api.deviceSettings() });
  const [rate, setRate] = useState('');
  const [cart, setCart] = useState('');
  const [parked, setParked] = useState('');
  const [step, setStep] = useState('0.1');
  const [strict, setStrict] = useState(false);
  useEffect(() => {
    if (!s.data) return;
    setRate(s.data.labourRatePerHour.toFixed(3)); setCart(String(s.data.reservationMinutes)); setParked(String(s.data.parkedReservationMinutes)); setStep(String(s.data.suggestedPriceStep));
    setStrict(!!s.data.strictCentralCheck);
  }, [s.data]);
  const save = useMutation({
    mutationFn: () => api.saveDeviceSettings({ labourRatePerHour: parseAmount(rate) ?? 0, reservationMinutes: Number(cart), parkedReservationMinutes: Number(parked), suggestedPriceStep: Number(step), strictCentralCheck: strict, version: s.data!.version }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['deviceSettings'] }),
  });
  if (s.isLoading) return <Spinner label={t('app.loading')} />;
  const edit = can(me, 'DEVICE.SETTINGS');
  return (
    <Card>
      <div className="grid-2">
        <TextField label={t('devices.settings.labourRate')} value={rate} onChange={(e) => setRate(e.target.value)} inputMode="decimal" dir="ltr" disabled={!edit} />
        <SelectField label={t('devices.settings.priceStep')} value={step} onChange={(e) => setStep(e.target.value)} disabled={!edit}>
          {['0.005', '0.01', '0.05', '0.1', '0.5', '1'].map((v) => <option key={v} value={v}>{Number(v).toFixed(3)}</option>)}
        </SelectField>
        <TextField label={t('devices.settings.reservationMinutes')} value={cart} onChange={(e) => setCart(e.target.value.replace(/\D/g, ''))} inputMode="numeric" dir="ltr" disabled={!edit} />
        <TextField label={t('devices.settings.parkedMinutes')} value={parked} onChange={(e) => setParked(e.target.value.replace(/\D/g, ''))} inputMode="numeric" dir="ltr" disabled={!edit} />
      </div>
      <Checkbox label={<><strong>{t('imei.settings.strict')}</strong><br /><span className="muted">{t('imei.settings.strictHelp')}</span></>} checked={strict} disabled={!edit} onChange={(e) => setStrict(e.target.checked)} />
      <ErrorBox error={s.error ?? save.error} />
      {save.isSuccess && <Alert tone="success">{t('app.saved')}</Alert>}
      {edit && <div className="row"><Button variant="primary" busy={save.isPending} onClick={() => save.mutate()}>{t('app.save')}</Button></div>}
    </Card>
  );
}

function GradesCard() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t } = useI18n();
  const grades = useQuery({ queryKey: ['grades'], queryFn: () => api.grades() });
  const [draft, setDraft] = useState({ code: '', nameEn: '', nameAr: '', pricePercent: '' });
  const refresh = () => qc.invalidateQueries({ queryKey: ['grades'] });
  const create = useMutation({ mutationFn: () => api.createGrade({ ...draft, pricePercent: Number(draft.pricePercent) }), onSuccess: async () => { setDraft({ code: '', nameEn: '', nameAr: '', pricePercent: '' }); await refresh(); } });
  const update = useMutation({ mutationFn: (g: Grade) => api.updateGrade(g.id, { code: g.code, nameEn: g.nameEn, nameAr: g.nameAr, pricePercent: g.pricePercent, status: g.status, categoryId: g.categoryId, version: g.version }), onSuccess: refresh });
  const edit = can(me, 'DEVICE.SETTINGS');
  return (
    <Card>
      <h3>{t('devices.settings.grades')}</h3>
      <ErrorBox error={grades.error ?? create.error ?? update.error} />
      {(grades.data ?? []).map((g) => <GradeRow key={`${g.id}-${g.version}`} grade={g} disabled={!edit} onSave={(x) => update.mutate(x)} />)}
      {edit && (
        <div className="row line-editor">
          <TextField label={t('devices.settings.code')} value={draft.code} onChange={(e) => setDraft({ ...draft, code: e.target.value.toUpperCase() })} dir="ltr" />
          <TextField label={t('devices.settings.nameEn')} value={draft.nameEn} onChange={(e) => setDraft({ ...draft, nameEn: e.target.value })} />
          <TextField label={t('devices.settings.nameAr')} value={draft.nameAr} onChange={(e) => setDraft({ ...draft, nameAr: e.target.value })} dir="rtl" />
          <TextField label={t('devices.settings.percent')} value={draft.pricePercent} onChange={(e) => setDraft({ ...draft, pricePercent: e.target.value })} inputMode="decimal" dir="ltr" />
          <Button busy={create.isPending} disabled={!draft.code || !draft.nameEn || !draft.nameAr || !draft.pricePercent} onClick={() => create.mutate()}>{t('devices.settings.newGrade')}</Button>
        </div>
      )}
    </Card>
  );
}

function GradeRow({ grade, disabled, onSave }: { grade: Grade; disabled: boolean; onSave: (g: Grade) => void }) {
  const { t } = useI18n();
  const [g, setG] = useState(grade);
  const changed = JSON.stringify(g) !== JSON.stringify(grade);
  return (
    <div className="row line-editor">
      <TextField label={t('devices.settings.code')} value={g.code} onChange={(e) => setG({ ...g, code: e.target.value.toUpperCase() })} dir="ltr" disabled={disabled} />
      <TextField label={t('devices.settings.nameEn')} value={g.nameEn} onChange={(e) => setG({ ...g, nameEn: e.target.value })} disabled={disabled} />
      <TextField label={t('devices.settings.nameAr')} value={g.nameAr} onChange={(e) => setG({ ...g, nameAr: e.target.value })} dir="rtl" disabled={disabled} />
      <TextField label={t('devices.settings.percent')} value={String(g.pricePercent)} onChange={(e) => setG({ ...g, pricePercent: Number(e.target.value) || 0 })} inputMode="decimal" dir="ltr" disabled={disabled} />
      <Checkbox label={t('devices.settings.active')} checked={g.status === 'active'} disabled={disabled} onChange={(e) => setG({ ...g, status: e.target.checked ? 'active' : 'inactive' })} />
      {changed && <Button size="sm" variant="primary" onClick={() => onSave(g)}>{t('app.save')}</Button>}
    </div>
  );
}

function ChecklistCard() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t } = useI18n();
  const lists = useQuery({ queryKey: ['checklists'], queryFn: () => api.checklists() });
  const list = lists.data?.find((c) => c.categoryId === null && c.status === 'active') ?? lists.data?.[0];
  const [items, setItems] = useState<ChecklistItemDef[]>([]);
  useEffect(() => { if (list) setItems(list.items.map((i) => ({ ...i }))); }, [list]);
  const save = useMutation({ mutationFn: () => api.updateChecklist(list!.id, { nameEn: list!.nameEn, nameAr: list!.nameAr, items, version: list!.version }), onSuccess: () => qc.invalidateQueries({ queryKey: ['checklists'] }) });
  const edit = can(me, 'DEVICE.SETTINGS');
  const set = (i: number, patch: Partial<ChecklistItemDef>) => setItems(items.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  if (lists.isLoading) return <Spinner label={t('app.loading')} />;
  return (
    <Card>
      <h3>{t('devices.settings.checklist')}</h3>
      <ErrorBox error={lists.error ?? save.error} />
      {items.map((it, i) => (
        <div key={i} className="row line-editor">
          <TextField label={t('devices.settings.itemCode')} value={it.code} onChange={(e) => set(i, { code: e.target.value.toLowerCase().replace(/[^a-z0-9_]/g, '') })} dir="ltr" disabled={!edit} />
          <TextField label={t('devices.settings.labelEn')} value={it.labelEn} onChange={(e) => set(i, { labelEn: e.target.value })} disabled={!edit} />
          <TextField label={t('devices.settings.labelAr')} value={it.labelAr} onChange={(e) => set(i, { labelAr: e.target.value })} dir="rtl" disabled={!edit} />
          <SelectField label={t('devices.settings.kind')} value={it.kind} disabled={!edit} onChange={(e) => { const kind = e.target.value as ChecklistItemDef['kind']; set(i, { kind, minPercent: kind === 'percent' ? it.minPercent ?? 80 : null, required: kind === 'text' ? false : it.required }); }}>
            {(['pass_fail', 'percent', 'text'] as const).map((k) => <option key={k} value={k}>{t(`devices.settings.kinds.${k}`)}</option>)}
          </SelectField>
          {it.kind === 'percent' && <TextField label={t('devices.settings.minPercent')} value={String(it.minPercent ?? '')} onChange={(e) => set(i, { minPercent: Number(e.target.value.replace(/\D/g, '')) })} inputMode="numeric" dir="ltr" disabled={!edit} />}
          {it.kind !== 'text' && (
            <SelectField label={t('devices.settings.onFail')} value={it.onFail} disabled={!edit} onChange={(e) => set(i, { onFail: e.target.value as ChecklistItemDef['onFail'] })}>
              {(['refurbish', 'damaged', 'for_parts'] as const).map((k) => <option key={k} value={k}>{t(`devices.settings.onFails.${k}`)}</option>)}
            </SelectField>
          )}
          {it.kind !== 'text' && <Checkbox label={t('devices.settings.required')} checked={it.required} disabled={!edit} onChange={(e) => set(i, { required: e.target.checked })} />}
          {edit && <Button size="sm" variant="ghost" onClick={() => setItems(items.filter((_, j) => j !== i))}>{t('devices.remove')}</Button>}
        </div>
      ))}
      {edit && (
        <div className="row">
          <Button onClick={() => setItems([...items, { code: `item_${items.length + 1}`, labelEn: '', labelAr: '', kind: 'pass_fail', required: true, minPercent: null, onFail: 'refurbish' }])}>{t('devices.settings.addItem')}</Button>
          <span className="spacer" />
          <Button variant="primary" busy={save.isPending} onClick={() => save.mutate()}>{t('app.save')}</Button>
        </div>
      )}
      {save.isSuccess && <Alert tone="success">{t('app.saved')}</Alert>}
    </Card>
  );
}

/** The model table (TAC): the first 8 digits of an IMEI name the model, so receiving can warn when a scan does not match the item. */
function TacCard() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const sum = useQuery({ queryKey: ['tacSummary'], queryFn: () => api.tacSummary() });
  const [csv, setCsv] = useState('');
  const run = useMutation({ mutationFn: () => api.importTac(csv), onSuccess: async () => { setCsv(''); await qc.invalidateQueries({ queryKey: ['tacSummary'] }); } });
  const edit = can(me, 'DEVICE.SETTINGS');
  return (
    <Card>
      <h3>{t('imei.settings.tac')}</h3>
      <p className="muted">{t('imei.settings.tacHelp')}</p>
      {sum.data && <p>{t('imei.settings.tacCount', { count: String(sum.data.count) })}{sum.data.lastImportedAt && <span className="muted"> · {t('imei.settings.tacLast', { when: formatDateTime(sum.data.lastImportedAt, lang) })}</span>}</p>}
      {edit && (
        <div className="row wrap">
          <label className="field"><span>{t('imei.settings.tacFile')}</span>
            <input type="file" accept=".csv,text/csv,text/plain" onChange={async (e) => { const f = e.target.files?.[0]; if (f) setCsv(await readText(f)); }} /></label>
          <Button variant="primary" disabled={!csv} busy={run.isPending} onClick={() => run.mutate()}>{t('imei.settings.tacImport')}</Button>
        </div>
      )}
      {run.data && <Alert tone="success">{t('imei.settings.tacImported', { imported: String(run.data.imported), skipped: String(run.data.skipped) })}</Alert>}
      <ErrorBox error={sum.error ?? run.error} />
    </Card>
  );
}

/** Serial number formats per brand: a serial number that does not match the brand's pattern is refused at receiving. */
function SerialFormatsCard() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const rows = useQuery({ queryKey: ['serialFormats'], queryFn: () => api.serialFormats() });
  const [draft, setDraft] = useState<Record<string, string>>({});
  const save = useMutation({ mutationFn: (x: { id: string; pattern: string }) => api.setSerialFormat(x.id, x.pattern.trim() || null), onSuccess: () => qc.invalidateQueries({ queryKey: ['serialFormats'] }) });
  const edit = can(me, 'DEVICE.SETTINGS');
  return (
    <Card>
      <h3>{t('imei.settings.serialFormats')}</h3>
      <p className="muted">{t('imei.settings.serialHelp')}</p>
      <ErrorBox error={rows.error ?? save.error} />
      {(rows.data ?? []).map((b) => (
        <div key={b.id} className="row wrap">
          <TextField label={`${t('imei.settings.pattern')}: ${pick(b.nameEn, b.nameAr)}`} placeholder={t('imei.settings.noPattern')} dir="ltr" disabled={!edit}
            value={draft[b.id] ?? b.serialPattern ?? ''} onChange={(e) => setDraft({ ...draft, [b.id]: e.target.value })} />
          {edit && <Button busy={save.isPending && save.variables?.id === b.id} disabled={draft[b.id] === undefined || draft[b.id] === (b.serialPattern ?? '')}
            onClick={() => save.mutate({ id: b.id, pattern: draft[b.id] ?? '' })}>{t('app.save')}</Button>}
        </div>
      ))}
      {save.isSuccess && <Alert tone="success">{t('imei.settings.saved')}</Alert>}
    </Card>
  );
}

/** Reads a small text file the user picked (FileReader works in every browser the app supports). */
const readText = (f: Blob) => new Promise<string>((resolve, reject) => {
  const r = new FileReader();
  r.onload = () => resolve(String(r.result ?? ''));
  r.onerror = () => reject(r.error ?? new Error('The file could not be read.'));
  r.readAsText(f);
});
