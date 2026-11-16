import { useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from 'react-router-dom';
import { ACCESSORY_CODES, ApiError, CONDITION_CODES, type ConditionValue, type Customer, type CustomerDevice, type PasscodeMode, type PhotoInput, type RepairDetail, type RepairLookup } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Button, Card, Checkbox, PageHeader, SelectField, Steps, TextField } from '@kite/ui';
import { ErrorBox, formatDate, useApi, workBranchId, useAuth } from '../core';
import { openPdf } from '../pos/TradeInWizard';
import { CustomerPicker, PhotoButton, SignaturePad } from './bits';

const STEPS = ['customer', 'device', 'problem', 'condition', 'terms', 'done'] as const;
const localInput = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);

/** Booking a device in (13.1): the customer, the device (with what we know of it), the problem, its condition and what came with it, access, and the signed terms. */
export function IntakePage() {
  const api = useApi();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const navigate = useNavigate();
  const setup = useQuery({ queryKey: ['repairSetup'], queryFn: () => api.repairSetup() });
  const techs = useQuery({ queryKey: ['repairTechnicians'], queryFn: () => api.repairTechnicians(workBranchId(me)) });
  const [step, setStep] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [customer, setCustomer] = useState<Customer | null>(null);
  const [devices, setDevices] = useState<CustomerDevice[]>([]);
  const [deviceId, setDeviceId] = useState<string | null>(null);
  const [code, setCode] = useState('');
  const [lookup, setLookup] = useState<RepairLookup | null>(null);
  const [brand, setBrand] = useState('');
  const [model, setModel] = useState('');
  const [colour, setColour] = useState('');
  const [categoryId, setCategoryId] = useState('');
  const [problem, setProblem] = useState('');
  const [faults, setFaults] = useState<string[]>([]);
  const [promised, setPromised] = useState('');
  const [preAuth, setPreAuth] = useState('');
  const [condition, setCondition] = useState<Record<string, ConditionValue>>({});
  const [conditionNotes, setConditionNotes] = useState('');
  const [accessories, setAccessories] = useState<string[]>([]);
  const [accessoriesOther, setAccessoriesOther] = useState('');
  const [mode, setMode] = useState<PasscodeMode>('none');
  const [passcode, setPasscode] = useState('');
  const [photos, setPhotos] = useState<PhotoInput[]>([]);
  const [tech, setTech] = useState('');
  const [notify, setNotify] = useState(false);
  const [msgLang, setMsgLang] = useState<'en' | 'ar'>(lang);
  const [termsName, setTermsName] = useState('');
  const [signature, setSignature] = useState<string | null>(null);
  const [done, setDone] = useState<RepairDetail | null>(null);

  const cats = (setup.data?.categories ?? []).filter((c) => c.status === 'active');
  const category = cats.find((c) => c.id === categoryId);
  const faultOptions = (setup.data?.faultCodes ?? []).filter((f) => f.status === 'active' && (!f.categoryId || f.categoryId === categoryId));
  useEffect(() => { if (category && !promised) setPromised(localInput(new Date(Date.now() + category.turnaroundHours * 36e5))); }, [category, promised]);
  useEffect(() => { if (customer) { setTermsName(customer.nameEn); api.customerDevices(customer.id).then(setDevices).catch(setError); } }, [customer, api]);
  const terms = setup.data ? pick(setup.data.settings.termsEn, setup.data.settings.termsAr) : '';
  const known = useMemo(() => devices.find((d) => d.id === deviceId) ?? null, [devices, deviceId]);

  const run = async (f: () => Promise<void>) => { setBusy(true); setError(null); try { await f(); } catch (e) { setError(e); } finally { setBusy(false); } };
  const fail = (title: string) => { throw new ApiError({ status: 400, code: 'VAL-001', title }); };
  const next = () => run(async () => {
    switch (STEPS[step]) {
      case 'customer': if (!customer) fail(t('repair.intake.customerIntro')); break;
      case 'device':
        if (!deviceId && !code.trim()) fail(t('repair.intake.scan'));
        if (!deviceId && !model.trim()) fail(t('repair.intake.model'));
        break;
      case 'problem':
        if (!categoryId) fail(t('repair.intake.category'));
        if (problem.trim().length < 3) fail(t('repair.intake.problem'));
        break;
      case 'condition':
        if (mode === 'provided' && !passcode) fail(t('repair.intake.passcode'));
        break;
      case 'terms': {
        if (!signature || termsName.trim().length < 2) fail(t('repair.intake.signFirst'));
        const k = code.trim();
        const isImei = /^\d{15}$/.test(k);
        const r = await api.createRepair({
          customerId: customer!.id, categoryId, problem: problem.trim(), passcodeMode: mode, passcode: mode === 'provided' ? passcode : null, termsName: termsName.trim(), termsSignature: signature!,
          customerDeviceId: deviceId, device: deviceId ? null : { imei1: isImei ? k : null, serial: isImei ? null : k || null, brand: brand.trim() || null, model: model.trim(), colour: colour.trim() || null },
          faultCodes: faults, condition, conditionNotes: conditionNotes.trim() || null, accessories, accessoriesOther: accessoriesOther.trim() || null,
          preAuthorised: preAuth === '' ? null : Number(preAuth), promisedAt: promised ? new Date(promised).toISOString() : null, notifyWhatsapp: notify, lang: msgLang, photos,
          technicianId: tech || null, branchId: workBranchId(me) || null,
        });
        setDone(r);
        break;
      }
    }
    setStep((s) => s + 1);
  });
  const doLookup = () => run(async () => {
    const r = await api.repairLookup(code.trim());
    setLookup(r);
    const d = r.devices[0];
    if (d) { setBrand(d.brand ?? ''); setModel(d.model); setColour(d.colour ?? ''); }
    else if (r.unit) setModel(r.unit.descriptionEn);
  });

  return (
    <div className="stack">
      <PageHeader title={t('repair.intake.title')} />
      <Steps steps={STEPS.map((s) => t(`repair.intake.steps.${s}`))} current={step} />
      <Card>
        <div className="stack">
          <ErrorBox error={error} />
          {STEPS[step] === 'customer' && (<><p>{t('repair.intake.customerIntro')}</p><CustomerPicker value={customer} onChange={setCustomer} /></>)}

          {STEPS[step] === 'device' && (
            <>
              {devices.length > 0 && (
                <div className="stack">
                  <strong>{t('repair.intake.yourDevices')}</strong>
                  {devices.map((d) => (
                    <label key={d.id} className="choice">
                      <input type="radio" name="device" checked={deviceId === d.id} onChange={() => setDeviceId(d.id)} />
                      <span>{[d.brand, d.model, d.colour].filter(Boolean).join(' ')} <span className="ltr mono">{d.imei1 ?? d.serial}</span></span>
                      {d.openJob && <Alert tone="warning">{t('repair.intake.openJob', { number: d.openJob })}</Alert>}
                    </label>
                  ))}
                  <label className="choice"><input type="radio" name="device" checked={deviceId === null} onChange={() => setDeviceId(null)} /><span>{t('repair.intake.newDevice')}</span></label>
                </div>
              )}
              {!deviceId && (
                <>
                  <div className="row">
                    <TextField label={t('repair.intake.scan')} value={code} onChange={(e) => setCode(e.target.value)} className="ltr mono" autoFocus onKeyDown={(e) => { if (e.key === 'Enter' && code.trim()) doLookup(); }} />
                    <Button onClick={doLookup} disabled={!code.trim()} busy={busy}>{t('repair.intake.lookup')}</Button>
                  </div>
                  {lookup?.sold && <Alert tone="info">{t('repair.intake.soldHere', { date: formatDate(lookup.sold.soldAt, lang), number: lookup.sold.number })} {lookup.sold.warrantyUntil && t(lookup.sold.inWarranty ? 'repair.intake.inWarranty' : 'repair.intake.outOfWarranty', { date: formatDate(lookup.sold.warrantyUntil, lang) })}</Alert>}
                  {lookup && lookup.jobs.length > 0 && <Alert tone="info">{t('repair.intake.earlierJobs')}: {lookup.jobs.map((j) => <Link key={j.id} to={`/app/repairs/${j.id}`} className="ltr"> {j.number}</Link>)}</Alert>}
                  <div className="grid-2">
                    <TextField label={t('repair.intake.brand')} value={brand} onChange={(e) => setBrand(e.target.value)} dir="auto" />
                    <TextField label={t('repair.intake.model')} value={model} onChange={(e) => setModel(e.target.value)} dir="auto" required />
                    <TextField label={t('repair.intake.colour')} value={colour} onChange={(e) => setColour(e.target.value)} dir="auto" />
                  </div>
                </>
              )}
              {known?.openJob && <Alert tone="warning">{t('repair.intake.openJob', { number: known.openJob })}</Alert>}
            </>
          )}

          {STEPS[step] === 'problem' && (
            <>
              <SelectField label={t('repair.intake.category')} value={categoryId} onChange={(e) => { setCategoryId(e.target.value); setPromised(''); }} required>
                <option value="" />
                {cats.map((c) => <option key={c.id} value={c.id}>{pick(c.nameEn, c.nameAr)}</option>)}
              </SelectField>
              <div className="field"><label htmlFor="problem">{t('repair.intake.problem')}</label><textarea id="problem" className="input" rows={3} value={problem} onChange={(e) => setProblem(e.target.value)} dir="auto" /></div>
              {faultOptions.length > 0 && (
                <fieldset className="chips"><legend>{t('repair.intake.faultCodes')}</legend>
                  {faultOptions.map((f) => <Checkbox key={f.id} label={pick(f.nameEn, f.nameAr)} checked={faults.includes(f.code)} onChange={(e) => setFaults(e.target.checked ? [...faults, f.code] : faults.filter((x) => x !== f.code))} />)}
                </fieldset>
              )}
              <div className="grid-2">
                <TextField label={t('repair.intake.promisedAt')} type="datetime-local" value={promised} onChange={(e) => setPromised(e.target.value)} />
                <TextField label={t('repair.intake.preAuthorised')} help={t('repair.intake.preAuthorisedHelp')} inputMode="decimal" className="ltr num"
                  placeholder={String(category?.preAuthorised ?? setup.data?.settings.preAuthorised ?? 0)} value={preAuth} onChange={(e) => setPreAuth(e.target.value)} />
                <SelectField label={t('repair.intake.technician')} value={tech} onChange={(e) => setTech(e.target.value)}>
                  <option value="">{t('repair.unassigned')}</option>
                  {(techs.data ?? []).map((u) => <option key={u.id} value={u.id}>{pick(u.nameEn, u.nameAr ?? u.nameEn)}</option>)}
                </SelectField>
              </div>
            </>
          )}

          {STEPS[step] === 'condition' && (
            <>
              <fieldset className="checklist"><legend>{t('repair.intake.condition')}</legend>
                {CONDITION_CODES.map((c) => (
                  <div key={c} className="checklist-item">
                    <span className="grow">{t(`repair.intake.checks.${c}`)}</span>
                    <div className="segmented" role="radiogroup" aria-label={t(`repair.intake.checks.${c}`)}>
                      {(['ok', 'fault', 'not_checked'] as const).map((v) => <button key={v} type="button" role="radio" aria-checked={(condition[c] ?? 'not_checked') === v} className={(condition[c] ?? 'not_checked') === v ? 'active' : ''} onClick={() => setCondition({ ...condition, [c]: v })}>{t(`repair.intake.values.${v}`)}</button>)}
                    </div>
                  </div>
                ))}
              </fieldset>
              <TextField label={t('repair.intake.conditionNotes')} value={conditionNotes} onChange={(e) => setConditionNotes(e.target.value)} dir="auto" />
              <fieldset className="chips"><legend>{t('repair.intake.accessories')}</legend>
                {ACCESSORY_CODES.map((a) => <Checkbox key={a} label={t(`repair.intake.items.${a}`)} checked={accessories.includes(a)} onChange={(e) => setAccessories(e.target.checked ? [...accessories, a] : accessories.filter((x) => x !== a))} />)}
              </fieldset>
              <TextField label={t('repair.intake.accessoriesOther')} value={accessoriesOther} onChange={(e) => setAccessoriesOther(e.target.value)} dir="auto" />
              <SelectField label={t('repair.intake.access')} value={mode} onChange={(e) => setMode(e.target.value as PasscodeMode)}>
                {(['none', 'customer_present', 'provided'] as const).map((m) => <option key={m} value={m}>{t(`repair.intake.accessModes.${m}`)}</option>)}
              </SelectField>
              {mode === 'provided' && <TextField label={t('repair.intake.passcode')} help={t('repair.intake.passcodeHelp')} value={passcode} onChange={(e) => setPasscode(e.target.value)} className="ltr mono" autoComplete="off" />}
              <div className="row"><strong>{t('repair.intake.photos')}</strong> <span className="muted">{photos.length}</span><PhotoButton label={t('repair.intake.addPhoto')} onPhoto={(d, name) => setPhotos([...photos, { stage: 'intake', dataUrl: d, caption: name }])} /></div>
            </>
          )}

          {STEPS[step] === 'terms' && (
            <>
              <Checkbox label={t('repair.intake.whatsapp')} checked={notify} onChange={(e) => setNotify(e.target.checked)} disabled={!customer?.phoneE164} />
              <p className="muted">{t('repair.intake.whatsappHelp')}</p>
              {notify && (
                <SelectField label={t('repair.intake.language')} value={msgLang} onChange={(e) => setMsgLang(e.target.value as 'en' | 'ar')}>
                  <option value="ar">العربية</option><option value="en">English</option>
                </SelectField>
              )}
              <strong>{t('repair.intake.termsTitle')}</strong>
              <div className="declaration" dir="auto">{terms}</div>
              <TextField label={t('repair.intake.termsName')} value={termsName} onChange={(e) => setTermsName(e.target.value)} dir="auto" />
              <SignaturePad label={t('repair.intake.sign')} onChange={setSignature} />
            </>
          )}

          {STEPS[step] === 'done' && done && (
            <>
              <Alert tone="success">{t('repair.intake.created', { number: done.job.number })}</Alert>
              <p className="muted">{t('repair.intake.depositHint')}</p>
              <div className="row">
                <Button onClick={() => void openPdf(api.repairDocument(done.job.id, 'job-card'))}>{t('repair.intake.printCard')}</Button>
                <Button onClick={() => void openPdf(api.repairDocument(done.job.id, 'tag'))}>{t('repair.intake.printTag')}</Button>
                <Button variant="primary" onClick={() => navigate(`/app/repairs/${done.job.id}`)}>{t('repair.intake.open')}</Button>
              </div>
            </>
          )}

          {STEPS[step] !== 'done' && (
            <div className="row">
              {step > 0 && <Button onClick={() => setStep(step - 1)} disabled={busy}>{t('app.back')}</Button>}
              <span className="spacer" />
              <Button variant="primary" onClick={next} busy={busy}>{STEPS[step] === 'terms' ? t('repair.intake.create') : t('app.next')}</Button>
            </div>
          )}
        </div>
      </Card>
    </div>
  );
}
