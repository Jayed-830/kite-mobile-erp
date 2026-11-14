import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import type { Module, SetupRequest } from '@kite/api';
import { useI18n, dictionaries } from '@kite/i18n';
import { Alert, Button, Checkbox, SelectField, Steps, Switch, TextField } from '@kite/ui';
import { ErrorBox, useApi } from '../core';
import { AuthFrame } from './Login';

// Mirrors the server's module catalogue for the setup screen (the server re-validates).
const SETUP_MODULES: Pick<Module, 'code' | 'nameEn' | 'nameAr' | 'dependsOn'>[] = [
  { code: 'device', nameEn: 'Serialized devices and IMEI', nameAr: 'الأجهزة المتسلسلة وIMEI', dependsOn: [] },
  { code: 'repair', nameEn: 'Repairs', nameAr: 'الصيانة', dependsOn: [] },
  { code: 'purch', nameEn: 'Purchasing and suppliers', nameAr: 'المشتريات والموردون', dependsOn: [] },
  { code: 'exp', nameEn: 'Expenses', nameAr: 'المصروفات', dependsOn: [] },
  { code: 'acct_ui', nameEn: 'Accounting screens', nameAr: 'شاشات المحاسبة', dependsOn: [] },
  { code: 'refurb', nameEn: 'Condition and refurbishment', nameAr: 'الحالة والتجديد', dependsOn: ['device'] },
  { code: 'tradein', nameEn: 'Trade-in', nameAr: 'الاستبدال', dependsOn: ['device', 'refurb'] },
  { code: 'comm', nameEn: 'Technician commissions', nameAr: 'عمولات الفنيين', dependsOn: ['repair'] },
  { code: 'credit', nameEn: 'Customer credit', nameAr: 'ائتمان العملاء', dependsOn: [] },
  { code: 'notif', nameEn: 'WhatsApp notifications', nameAr: 'إشعارات واتساب', dependsOn: [] },
];

const DEFAULT_ON = new Set(['device', 'repair', 'purch', 'exp', 'acct_ui']);

/** Setup wizard (blueprint 24.3, 26.2): business, head office, owner, modules, review. */
export function SetupPage() {
  const api = useApi();
  const navigate = useNavigate();
  const { t, pick, lang } = useI18n();
  const [mode, setMode] = useState<'choose' | 'new' | 'join'>('choose');
  const [step, setStep] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [confirm, setConfirm] = useState('');
  const [modules, setModules] = useState<Record<string, boolean>>(() => Object.fromEntries(SETUP_MODULES.map((m) => [m.code, DEFAULT_ON.has(m.code)])));
  const [form, setForm] = useState<SetupRequest>({
    setupCode: '',
    business: { legalNameEn: '', legalNameAr: '', tradeNameEn: '', tradeNameAr: '', crNumber: '', vatNumber: '', phone: '', email: '', fiscalYearStartMonth: 1, pricesIncludeTax: true },
    headOffice: { code: 'B01', nameEn: '', nameAr: '' },
    owner: { username: 'owner', displayNameEn: '', displayNameAr: '', password: '', preferredLanguage: lang },
    serverRole: 'combined',
  });

  useEffect(() => {
    api.setupStatus().then((s) => { if (s.configured) navigate('/login', { replace: true }); }).catch(() => undefined);
  }, [api, navigate]);

  const steps = [t('setup.stepBusiness'), t('setup.stepBranch'), t('setup.stepOwner'), t('setup.stepModules'), t('setup.stepReview')];
  const b = form.business;
  const setB = (patch: Partial<SetupRequest['business']>) => setForm((f) => ({ ...f, business: { ...f.business, ...patch } }));
  const setH = (patch: Partial<SetupRequest['headOffice']>) => setForm((f) => ({ ...f, headOffice: { ...f.headOffice, ...patch } }));
  const setO = (patch: Partial<SetupRequest['owner']>) => setForm((f) => ({ ...f, owner: { ...f.owner, ...patch } }));

  const passwordProblem = form.owner.password.length > 0 && form.owner.password.length < 10 ? t('setup.passwordHelp')
    : confirm.length > 0 && confirm !== form.owner.password ? t('setup.passwordMismatch') : null;

  const valid = [
    form.setupCode.trim().length === 8 && b.legalNameEn.trim() !== '' && b.legalNameAr.trim() !== '',
    /^[A-Za-z0-9-]{2,12}$/.test(form.headOffice.code) && form.headOffice.nameEn.trim() !== '' && form.headOffice.nameAr.trim() !== '',
    /^[a-zA-Z0-9._-]{3,40}$/.test(form.owner.username) && form.owner.displayNameEn.trim() !== '' && form.owner.password.length >= 10 && confirm === form.owner.password,
    true,
    true,
  ];

  const toggle = (code: string, on: boolean) => {
    setModules((m) => {
      const next = { ...m, [code]: on };
      // Switching a module on switches on what it needs; switching off switches off what needs it.
      for (const mod of SETUP_MODULES) {
        if (on && mod.code === code) mod.dependsOn.forEach((d) => (next[d] = true));
        if (!on && mod.dependsOn.includes(code)) next[mod.code] = false;
      }
      return next;
    });
  };

  async function finish() {
    setBusy(true);
    setError(null);
    try {
      const clean = (s?: string) => (s && s.trim() ? s.trim() : undefined);
      await api.setup({
        ...form,
        setupCode: form.setupCode.trim().toUpperCase(),
        business: { ...b, tradeNameEn: clean(b.tradeNameEn), tradeNameAr: clean(b.tradeNameAr), crNumber: clean(b.crNumber), vatNumber: clean(b.vatNumber), phone: clean(b.phone), email: clean(b.email) },
        headOffice: { ...form.headOffice, code: form.headOffice.code.toUpperCase() },
        modules,
      });
      navigate('/login', { replace: true, state: { setupDone: true } });
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  if (mode === 'choose') {
    return (
      <AuthFrame title={t('setup.title')} wide>
        <div className="wide-card stack" style={{ width: '100%' }}>
          <p className="muted" style={{ margin: 0 }}>{t('setup.intro')}</p>
          <h2 style={{ margin: 0 }}>{t('setup.choose')}</h2>
          <div className="grid-2">
            <button type="button" className="card" style={{ textAlign: 'start', cursor: 'pointer' }} onClick={() => setMode('new')}>
              <strong>{t('setup.newBusiness')}</strong><p className="muted" style={{ marginBlockEnd: 0 }}>{t('setup.newBusinessHelp')}</p>
            </button>
            <button type="button" className="card" style={{ textAlign: 'start', cursor: 'pointer' }} onClick={() => setMode('join')}>
              <strong>{t('setup.joinBranch')}</strong><p className="muted" style={{ marginBlockEnd: 0 }}>{t('setup.joinBranchHelp')}</p>
            </button>
          </div>
        </div>
      </AuthFrame>
    );
  }
  if (mode === 'join') return <JoinBranch onBack={() => setMode('choose')} />;

  return (
    <AuthFrame title={t('setup.title')} wide>
      <div className="wide-card" style={{ width: '100%' }}>
        <p className="muted">{t('setup.intro')}</p>
        <Steps steps={steps} current={step} />
        {step === 0 && (
          <div className="stack">
            <TextField label={t('setup.code')} help={t('setup.codeHelp')} value={form.setupCode} onChange={(e) => setForm({ ...form, setupCode: e.target.value })} maxLength={8} dir="ltr" required autoFocus />
            <div className="grid-2">
              <TextField label={t('setup.legalNameEn')} value={b.legalNameEn} onChange={(e) => setB({ legalNameEn: e.target.value })} dir="ltr" required />
              <TextField label={t('setup.legalNameAr')} value={b.legalNameAr} onChange={(e) => setB({ legalNameAr: e.target.value })} dir="rtl" required />
              <TextField label={t('setup.tradeNameEn')} value={b.tradeNameEn} onChange={(e) => setB({ tradeNameEn: e.target.value })} dir="ltr" />
              <TextField label={t('setup.tradeNameAr')} value={b.tradeNameAr} onChange={(e) => setB({ tradeNameAr: e.target.value })} dir="rtl" />
              <TextField label={t('setup.crNumber')} value={b.crNumber} onChange={(e) => setB({ crNumber: e.target.value })} dir="ltr" />
              <TextField label={t('setup.vatNumber')} value={b.vatNumber} onChange={(e) => setB({ vatNumber: e.target.value })} dir="ltr" />
              <TextField label={t('setup.phone')} value={b.phone} onChange={(e) => setB({ phone: e.target.value })} dir="ltr" type="tel" />
              <TextField label={t('setup.email')} value={b.email} onChange={(e) => setB({ email: e.target.value })} dir="ltr" type="email" />
              <SelectField label={t('setup.fiscalStart')} value={b.fiscalYearStartMonth} onChange={(e) => setB({ fiscalYearStartMonth: Number(e.target.value) })}>
                {dictionaries[lang].months.map((m, i) => <option key={m} value={i + 1}>{m}</option>)}
              </SelectField>
            </div>
            <Checkbox label={t('setup.pricesIncludeTax')} checked={b.pricesIncludeTax} onChange={(e) => setB({ pricesIncludeTax: e.target.checked })} />
          </div>
        )}
        {step === 1 && (
          <div className="stack">
            <div className="grid-2">
              <TextField label={t('setup.branchCode')} help={t('setup.branchCodeHelp')} value={form.headOffice.code} onChange={(e) => setH({ code: e.target.value.toUpperCase() })} maxLength={12} dir="ltr" required />
              <span />
              <TextField label={t('setup.branchNameEn')} value={form.headOffice.nameEn} onChange={(e) => setH({ nameEn: e.target.value })} dir="ltr" required />
              <TextField label={t('setup.branchNameAr')} value={form.headOffice.nameAr} onChange={(e) => setH({ nameAr: e.target.value })} dir="rtl" required />
            </div>
            <SelectField label={t('setup.serverRole')} value={form.serverRole} onChange={(e) => setForm({ ...form, serverRole: e.target.value as 'combined' | 'main' })}>
              <option value="combined">{t('setup.roleCombined')}</option>
              <option value="main">{t('setup.roleMain')}</option>
            </SelectField>
          </div>
        )}
        {step === 2 && (
          <div className="grid-2">
            <TextField label={t('setup.ownerUsername')} value={form.owner.username} onChange={(e) => setO({ username: e.target.value.toLowerCase() })} dir="ltr" autoComplete="username" required />
            <SelectField label={t('users.language')} value={form.owner.preferredLanguage} onChange={(e) => setO({ preferredLanguage: e.target.value as 'en' | 'ar' })}>
              <option value="en">English</option>
              <option value="ar">العربية</option>
            </SelectField>
            <TextField label={t('setup.ownerName')} value={form.owner.displayNameEn} onChange={(e) => setO({ displayNameEn: e.target.value })} dir="ltr" required />
            <TextField label={t('setup.ownerNameAr')} value={form.owner.displayNameAr} onChange={(e) => setO({ displayNameAr: e.target.value })} dir="rtl" />
            <TextField label={t('setup.password')} type="password" value={form.owner.password} onChange={(e) => setO({ password: e.target.value })} autoComplete="new-password" help={t('setup.passwordHelp')} required />
            <TextField label={t('setup.passwordConfirm')} type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="new-password" error={passwordProblem} required />
          </div>
        )}
        {step === 3 && (
          <div className="stack">
            <p className="muted">{t('setup.modulesIntro')}</p>
            {SETUP_MODULES.map((m) => (
              <div key={m.code} className="row">
                <Switch label={pick(m.nameEn, m.nameAr)} checked={!!modules[m.code]} onChange={(v) => toggle(m.code, v)} />
                <span>{pick(m.nameEn, m.nameAr)}</span>
                {m.dependsOn.length > 0 && <span className="muted" style={{ fontSize: 13 }}>{t('modules.needs', { list: m.dependsOn.map((d) => { const x = SETUP_MODULES.find((s) => s.code === d)!; return pick(x.nameEn, x.nameAr); }).join(lang === 'ar' ? '، ' : ', ') })}</span>}
              </div>
            ))}
          </div>
        )}
        {step === 4 && (
          <div className="stack">
            <p className="muted">{t('setup.reviewIntro')}</p>
            <dl className="grid-2" style={{ margin: 0 }}>
              <div><dt className="muted">{t('setup.legalNameEn')}</dt><dd style={{ margin: 0 }}>{b.legalNameEn}</dd></div>
              <div><dt className="muted">{t('setup.legalNameAr')}</dt><dd style={{ margin: 0 }} dir="rtl">{b.legalNameAr}</dd></div>
              <div><dt className="muted">{t('setup.vatNumber')}</dt><dd style={{ margin: 0 }} className="ltr">{b.vatNumber || '—'}</dd></div>
              <div><dt className="muted">{t('setup.branchCode')}</dt><dd style={{ margin: 0 }}><span className="ltr">{form.headOffice.code}</span> · {pick(form.headOffice.nameEn, form.headOffice.nameAr)}</dd></div>
              <div><dt className="muted">{t('setup.serverRole')}</dt><dd style={{ margin: 0 }}>{form.serverRole === 'main' ? t('setup.roleMain') : t('setup.roleCombined')}</dd></div>
              <div><dt className="muted">{t('setup.ownerUsername')}</dt><dd style={{ margin: 0 }} className="ltr">{form.owner.username}</dd></div>
            </dl>
            <Alert tone="info">{t('setup.taxNote')}</Alert>
          </div>
        )}
        <div style={{ marginBlockStart: 16 }} className="stack">
          <ErrorBox error={error} />
          <div className="row">
            <Button onClick={() => (step > 0 ? setStep(step - 1) : setMode('choose'))}>{t('app.back')}</Button>
            <span className="spacer" />
            {step < steps.length - 1
              ? <Button variant="primary" disabled={!valid[step]} onClick={() => setStep(step + 1)}>{t('app.next')}</Button>
              : <Button variant="primary" busy={busy} onClick={() => void finish()}>{t('setup.finish')}</Button>}
          </div>
        </div>
      </div>
    </AuthFrame>
  );
}


/** Branch server setup (blueprint 18.1): connect to head office, compare the security code, then join with the one-time token. */
function JoinBranch({ onBack }: { onBack: () => void }) {
  const api = useApi();
  const navigate = useNavigate();
  const { t } = useI18n();
  const [f, setF] = useState({ setupCode: '', mainUrl: 'https://', token: '', serverName: '' });
  const [found, setFound] = useState<{ businessName: string; fingerprintCode: string; fingerprint: string } | null>(null);
  const [match, setMatch] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const run = async (work: () => Promise<void>) => { setBusy(true); setError(null); try { await work(); } catch (e) { setError(e); } finally { setBusy(false); } };
  const inspect = () => run(async () => { setFound(await api.joinInspect(f.mainUrl.trim(), f.setupCode.trim().toUpperCase())); setMatch(false); });
  const join = () => run(async () => {
    await api.join({ mainUrl: f.mainUrl.trim(), setupCode: f.setupCode.trim().toUpperCase(), token: f.token.trim(), confirmedFingerprint: found!.fingerprint, serverName: f.serverName.trim() || undefined });
    navigate('/login', { replace: true, state: { joined: true } });
  });
  return (
    <AuthFrame title={t('setup.joinTitle')} wide>
      <div className="wide-card stack" style={{ width: '100%' }}>
        <div className="grid-2">
          <TextField label={t('setup.code')} help={t('setup.codeHelp')} value={f.setupCode} onChange={(e) => { setF({ ...f, setupCode: e.target.value }); setFound(null); }} maxLength={8} dir="ltr" required autoFocus />
          <TextField label={t('setup.mainUrl')} help={t('setup.mainUrlHelp')} value={f.mainUrl} onChange={(e) => { setF({ ...f, mainUrl: e.target.value }); setFound(null); }} dir="ltr" required />
        </div>
        {!found && (
          <div className="row">
            <Button onClick={onBack}>{t('app.back')}</Button>
            <span className="spacer" />
            <Button variant="primary" busy={busy} disabled={f.setupCode.trim().length !== 8 || !/^https:\/\/.+/.test(f.mainUrl.trim())} onClick={() => void inspect()}>{t('setup.connect')}</Button>
          </div>
        )}
        {found && (
          <div className="stack">
            <h2 style={{ margin: 0 }}>{t('setup.compareTitle')}</h2>
            <div><div className="muted">{t('setup.business')}</div><div><strong>{found.businessName}</strong></div></div>
            <div className="ltr" style={{ fontSize: 28, fontWeight: 700, letterSpacing: 3 }} data-testid="join-code">{found.fingerprintCode}</div>
            <Alert tone="warning">{t('setup.compareHelp')}</Alert>
            <Checkbox label={t('setup.codesMatch')} checked={match} onChange={(e) => setMatch(e.target.checked)} />
            {match && (
              <div className="grid-2">
                <TextField label={t('setup.token')} help={t('setup.tokenHelp')} value={f.token} onChange={(e) => setF({ ...f, token: e.target.value })} dir="ltr" required />
                <TextField label={t('setup.serverName')} value={f.serverName} onChange={(e) => setF({ ...f, serverName: e.target.value })} dir="ltr" />
              </div>
            )}
            <div className="row">
              <Button onClick={() => setFound(null)}>{t('app.back')}</Button>
              <span className="spacer" />
              <Button variant="primary" busy={busy} disabled={!match || f.token.trim().length < 10} onClick={() => void join()}>{t('setup.join')}</Button>
            </div>
          </div>
        )}
        <ErrorBox error={error} />
      </div>
    </AuthFrame>
  );
}
