import { useEffect, useState, type FormEvent } from 'react';
import { Navigate, Link, useLocation } from 'react-router-dom';
import { useI18n } from '@kite/i18n';
import { Alert, Button, Card, TextField } from '@kite/ui';
import { ErrorBox, useApi, useAuth } from '../core';

export function AuthFrame({ title, children, wide }: { title: string; children: React.ReactNode; wide?: boolean }) {
  const { t, lang, setLang } = useI18n();
  return (
    <div className="center-page">
      <Card className={wide ? 'wide-card' : 'auth-card'}>
        <div className="stack">
          <div className="row">
            <div className="brand-mark" aria-hidden="true">K</div>
            <div><strong>{t('app.name')}</strong><div className="muted" style={{ fontSize: 13 }}>{t('app.by')}</div></div>
            <span className="spacer" />
            <Button size="sm" variant="ghost" onClick={() => setLang(lang === 'en' ? 'ar' : 'en')}>{lang === 'en' ? 'العربية' : 'English'}</Button>
          </div>
          <h1>{title}</h1>
          {children}
        </div>
      </Card>
    </div>
  );
}

export function LoginPage() {
  const { stage, signIn, verifyMfa, changePassword } = useAuth();
  const api = useApi();
  const { t } = useI18n();
  const location = useLocation();
  const [configured, setConfigured] = useState<boolean | null>(null);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const justSetUp = (location.state as { setupDone?: boolean } | null)?.setupDone;
  const justJoined = (location.state as { joined?: boolean } | null)?.joined;

  useEffect(() => {
    api.setupStatus().then((s) => setConfigured(s.configured)).catch(() => setConfigured(true));
  }, [api]);

  if (stage === 'signedIn') return <Navigate to="/app" replace />;

  const run = (fn: () => Promise<void>) => async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try { await fn(); } catch (err) { setError(err); } finally { setBusy(false); }
  };

  if (configured === false) {
    return (
      <AuthFrame title={t('login.title')}>
        <Alert tone="warning">{t('login.notSetUp')}</Alert>
        <Link className="btn btn-primary" to="/setup">{t('login.goToSetup')}</Link>
      </AuthFrame>
    );
  }

  if (stage === 'mfa') {
    return (
      <AuthFrame title={t('login.mfaTitle')}>
        <form className="stack" onSubmit={run(() => verifyMfa(code.trim()))}>
          <p className="muted">{t('login.mfaHelp')}</p>
          <TextField label={t('login.mfaCode')} value={code} onChange={(e) => setCode(e.target.value)} inputMode="numeric" autoComplete="one-time-code" maxLength={6} required autoFocus />
          <ErrorBox error={error} />
          <Button variant="primary" type="submit" busy={busy}>{t('login.verify')}</Button>
        </form>
      </AuthFrame>
    );
  }

  if (stage === 'changePassword') {
    return (
      <AuthFrame title={t('login.changeTitle')}>
        <form className="stack" onSubmit={run(() => changePassword(password, newPassword))}>
          <p className="muted">{t('login.changeHelp')}</p>
          <TextField label={t('login.currentPassword')} type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required />
          <TextField label={t('login.newPassword')} type="password" value={newPassword} onChange={(e) => setNewPassword(e.target.value)} autoComplete="new-password" help={t('setup.passwordHelp')} required />
          <ErrorBox error={error} />
          <Button variant="primary" type="submit" busy={busy}>{t('login.changeSubmit')}</Button>
        </form>
      </AuthFrame>
    );
  }

  return (
    <AuthFrame title={t('login.title')}>
      {justSetUp && <Alert tone="success">{t('setup.done')}</Alert>}
      {justJoined && <Alert tone="success">{t('setup.joined')}</Alert>}
      <form className="stack" onSubmit={run(() => signIn(username.trim(), password))}>
        <TextField label={t('login.username')} value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" required autoFocus dir="ltr" />
        <TextField label={t('login.password')} type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required />
        <ErrorBox error={error} />
        <Button variant="primary" type="submit" busy={busy || stage === 'loading'}>{t('login.submit')}</Button>
      </form>
    </AuthFrame>
  );
}
