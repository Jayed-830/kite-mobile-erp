import { useEffect, useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import QRCode from 'qrcode';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, PageHeader, TextField } from '@kite/ui';
import { ErrorBox, useApi, useAuth } from '../core';

export function AccountPage() {
  const api = useApi();
  const { me, reload } = useAuth();
  const { t } = useI18n();
  const [pw, setPw] = useState({ current: '', next: '' });
  const [pin, setPin] = useState({ pin: '', current: '' });
  const [totp, setTotp] = useState<{ secret: string; uri: string; qr: string } | null>(null);
  const [code, setCode] = useState('');
  const [disablePw, setDisablePw] = useState('');

  const changePw = useMutation({ mutationFn: () => api.changePassword(pw.current, pw.next), onSuccess: () => setPw({ current: '', next: '' }) });
  const savePin = useMutation({ mutationFn: () => api.setPin(pin.pin, pin.current), onSuccess: () => setPin({ pin: '', current: '' }) });
  const start = useMutation({
    mutationFn: () => api.startTotp(),
    onSuccess: async (r) => setTotp({ secret: r.secret, uri: r.otpauthUri, qr: await QRCode.toDataURL(r.otpauthUri, { margin: 1, width: 192 }) }),
  });
  const enable = useMutation({ mutationFn: () => api.enableTotp(code.trim()), onSuccess: async () => { setTotp(null); setCode(''); await reload(); } });
  const disable = useMutation({ mutationFn: () => api.disableTotp(disablePw), onSuccess: async () => { setDisablePw(''); await reload(); } });
  useEffect(() => () => setTotp(null), []);
  if (!me) return null;

  return (
    <div className="stack">
      <PageHeader title={t('account.title')} />
      <div className="grid-2">
        <Card>
          <form className="stack" onSubmit={(e) => { e.preventDefault(); changePw.mutate(); }}>
            <h2>{t('account.changePassword')}</h2>
            <TextField label={t('login.currentPassword')} type="password" value={pw.current} onChange={(e) => setPw({ ...pw, current: e.target.value })} autoComplete="current-password" required />
            <TextField label={t('login.newPassword')} type="password" value={pw.next} onChange={(e) => setPw({ ...pw, next: e.target.value })} autoComplete="new-password" help={t('setup.passwordHelp')} required />
            <ErrorBox error={changePw.error} />
            {changePw.isSuccess && <Alert tone="success">{t('app.saved')}</Alert>}
            <div><Button type="submit" variant="primary" busy={changePw.isPending}>{t('login.changeSubmit')}</Button></div>
          </form>
        </Card>
        <Card>
          <form className="stack" onSubmit={(e) => { e.preventDefault(); savePin.mutate(); }}>
            <h2>{t('account.pinTitle')}</h2>
            <p className="muted">{t('account.pinHelp')}</p>
            <TextField label={t('account.pin')} type="password" inputMode="numeric" maxLength={8} value={pin.pin} onChange={(e) => setPin({ ...pin, pin: e.target.value.replace(/\D/g, '') })} required />
            <TextField label={t('login.currentPassword')} type="password" value={pin.current} onChange={(e) => setPin({ ...pin, current: e.target.value })} autoComplete="current-password" required />
            <ErrorBox error={savePin.error} />
            {savePin.isSuccess && <Alert tone="success">{t('app.saved')}</Alert>}
            <div><Button type="submit" busy={savePin.isPending}>{t('account.setPin')}</Button></div>
          </form>
        </Card>
        <Card>
          <div className="stack">
            <div className="row"><h2>{t('account.twoFactorTitle')}</h2><Badge tone={me.user.totpEnabled ? 'success' : 'warning'}>{me.user.totpEnabled ? t('account.twoFactorOn') : t('account.twoFactorOff')}</Badge></div>
            {!me.user.totpEnabled && !totp && <div><Button variant="primary" busy={start.isPending} onClick={() => start.mutate()}>{t('account.twoFactorStart')}</Button></div>}
            {totp && (
              <>
                <p className="muted">{t('account.twoFactorScan')}</p>
                <img src={totp.qr} alt="QR" width={192} height={192} style={{ background: '#fff', borderRadius: 8 }} />
                <div><span className="muted">{t('account.twoFactorKey')}: </span><code className="ltr" style={{ wordBreak: 'break-all' }}>{totp.secret}</code></div>
                <TextField label={t('login.mfaCode')} value={code} onChange={(e) => setCode(e.target.value)} inputMode="numeric" maxLength={6} />
                <div><Button variant="primary" busy={enable.isPending} onClick={() => enable.mutate()}>{t('account.twoFactorEnable')}</Button></div>
              </>
            )}
            {me.user.totpEnabled && (
              <>
                <TextField label={t('login.currentPassword')} type="password" value={disablePw} onChange={(e) => setDisablePw(e.target.value)} autoComplete="current-password" />
                <div><Button variant="danger" busy={disable.isPending} onClick={() => disable.mutate()}>{t('account.twoFactorDisable')}</Button></div>
              </>
            )}
            <ErrorBox error={start.error ?? enable.error ?? disable.error} />
          </div>
        </Card>
      </div>
    </div>
  );
}
