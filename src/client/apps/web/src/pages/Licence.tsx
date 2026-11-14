import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type LicenceBanner as Banner, type LicenceState } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, DataTable, PageHeader, Spinner } from '@kite/ui';
import { ErrorBox, formatDate, formatDateTime, useApi, useAuth } from '../core';

export const stateTone = (s: LicenceState) =>
  (s === 'active' ? 'success' : s === 'expiring' || s === 'grace' || s === 'none' ? 'warning' : 'danger') as 'success' | 'warning' | 'danger';

/** Licence (blueprint 24.5): status, limits, import of the .kmelic file, the connection to Nextdab and the history. */
export function LicencePage() {
  const api = useApi();
  const qc = useQueryClient();
  const { t, lang, pick } = useI18n();
  const lic = useQuery({ queryKey: ['licence'], queryFn: () => api.licence() });
  const modules = useQuery({ queryKey: ['modules'], queryFn: () => api.modules() });
  const [text, setText] = useState('');
  const [copied, setCopied] = useState(false);
  const refresh = async () => { await qc.invalidateQueries({ queryKey: ['licence'] }); await qc.invalidateQueries({ queryKey: ['licence-status'] }); await qc.invalidateQueries({ queryKey: ['modules'] }); };
  const imp = useMutation({ mutationFn: (fileText: string) => api.importLicence(fileText), onSuccess: async () => { setText(''); await refresh(); } });
  const checkin = useMutation({ mutationFn: () => api.licenceCheckin() });

  if (!lic.data) return lic.error ? <ErrorBox error={lic.error} /> : <Spinner label={t('app.loading')} />;
  const d = lic.data;
  const modName = (code: string) => { const m = modules.data?.find((x) => x.code === code); return m ? pick(m.nameEn, m.nameAr) : code; };
  const readFile = (f: File | undefined) => { if (f) void f.text().then((s) => imp.mutate(s)); };

  return (
    <div className="stack">
      <PageHeader title={t('licence.title')} />
      {!d.keyConfigured && <Alert tone="error">{t('licence.keyMissing')}</Alert>}
      {d.state === 'suspended' && <Alert tone="error">{t('licence.banner.suspended')} {(lang === 'ar' ? d.suspendMessageAr : d.suspendMessage) ?? d.suspendMessage ?? ''}</Alert>}

      <div className="grid-2">
        <Card>
          <div className="stack">
            <div className="row"><h2 style={{ margin: 0 }}>{t('licence.title')}</h2><Badge tone={stateTone(d.state)}>{t(`licence.states.${d.state}`)}</Badge>{d.terms && <Badge>{t(`licence.kinds.${d.terms.kind}`)}</Badge>}</div>
            {d.terms ? (
              <dl className="stack" style={{ margin: 0 }}>
                <div><dt className="muted">{t('licence.customer')}</dt><dd style={{ margin: 0 }}>{d.terms.customer}</dd></div>
                <div><dt className="muted">{t('licence.validUntil')}</dt><dd style={{ margin: 0 }}>{formatDate(d.terms.validUntil, lang)}{d.daysLeft !== null && d.daysLeft >= 0 && <span className="muted"> · {t('licence.daysLeft', { days: d.daysLeft })}</span>}</dd></div>
                {(d.state === 'grace' || d.state === 'expiring') && d.endsOn && <div><dt className="muted">{t('licence.graceUntil', { date: formatDate(d.endsOn, lang) })}</dt></div>}
                <div><dt className="muted">{t('licence.modules')}</dt><dd style={{ margin: 0 }}>{d.terms.modules.map(modName).join(lang === 'ar' ? '، ' : ', ')}</dd></div>
              </dl>
            ) : <p className="muted" style={{ margin: 0 }}>{t('licence.unlicensedNote')}</p>}
            <dl className="grid-2" style={{ margin: 0 }}>
              <div><dt className="muted">{t('licence.branches')}</dt><dd style={{ margin: 0 }}>{t('licence.used', { used: d.branchesUsed, allowed: d.branchesAllowed })}</dd></div>
              <div><dt className="muted">{t('licence.terminals')}</dt><dd style={{ margin: 0 }}>{t('licence.used', { used: d.terminalsUsed, allowed: d.terminalsAllowed })}</dd></div>
            </dl>
            {d.connection && d.connection.modulesOff.length > 0 && <div><span className="muted">{t('licence.switchedOffModules')}: </span>{d.connection.modulesOff.map(modName).join(lang === 'ar' ? '، ' : ', ')}</div>}
          </div>
        </Card>
        <Card>
          <div className="stack">
            <h2 style={{ margin: 0 }}>{t('licence.installationId')}</h2>
            <div className="row">
              <span className="ltr" style={{ fontSize: 22, fontWeight: 700, letterSpacing: 1 }} data-testid="installation-id">{d.installationId}</span>
              <Button size="sm" onClick={() => { void navigator.clipboard?.writeText(d.installationId).then(() => setCopied(true)); }}>{copied ? t('licence.copied') : t('licence.copy')}</Button>
            </div>
            <p className="muted" style={{ margin: 0 }}>{t('licence.installationHelp')}</p>
            {d.connection ? (
              <>
                <h2 style={{ margin: 0 }}>{t('licence.connection')}</h2>
                <dl className="grid-2" style={{ margin: 0 }}>
                  <div><dt className="muted">{t('licence.lastCheckin')}</dt><dd style={{ margin: 0 }}>{d.connection.lastCheckinAt ? formatDateTime(d.connection.lastCheckinAt, lang) : t('servers.never')}</dd></div>
                  <div><dt className="muted">{t('licence.lastOk')}</dt><dd style={{ margin: 0 }}>{d.connection.lastCheckinOkAt ? formatDateTime(d.connection.lastCheckinOkAt, lang) : t('servers.never')}</dd></div>
                  <div><dt className="muted">{t('licence.nextCheckin')}</dt><dd style={{ margin: 0 }}>{d.connection.nextCheckinAt ? formatDateTime(d.connection.nextCheckinAt, lang) : '—'}</dd></div>
                  {d.connection.lastError && <div><dt className="muted">{t('licence.lastError')}</dt><dd style={{ margin: 0 }} className="ltr">{d.connection.lastError}</dd></div>}
                </dl>
                <div className="row"><Button busy={checkin.isPending} onClick={() => checkin.mutate()}>{t('licence.checkinNow')}</Button>{checkin.isSuccess && <span className="muted">{t('licence.checkinAsked')}</span>}</div>
                <ErrorBox error={checkin.error} />
              </>
            ) : <p className="muted" style={{ margin: 0 }}>{t('licence.branchNote')}</p>}
          </div>
        </Card>
      </div>

      {d.role !== 'branch' && (
        <Card>
          <div className="stack">
            <h2 style={{ margin: 0 }}>{t('licence.import')}</h2>
            <p className="muted" style={{ margin: 0 }}>{t('licence.importHelp')}</p>
            <label className="field">
              <span>{t('licence.chooseFile')}</span>
              <input className="input" type="file" accept=".kmelic,application/json,text/plain" onChange={(e) => readFile(e.target.files?.[0])} />
            </label>
            <label className="field">
              <span>{t('licence.pasteText')}</span>
              <textarea className="input ltr" rows={4} value={text} onChange={(e) => setText(e.target.value)} />
            </label>
            <div><Button variant="primary" disabled={!text.trim()} busy={imp.isPending} onClick={() => imp.mutate(text)}>{t('licence.import')}</Button></div>
            <ErrorBox error={imp.error} />
            {imp.data && <Alert tone="success">{t('licence.imported', { kind: t(`licence.kinds.${imp.data.kind}`), date: formatDate(imp.data.validUntil, lang) })}</Alert>}
          </div>
        </Card>
      )}

      <h2>{t('licence.history')}</h2>
      <DataTable
        rows={d.history}
        rowKey={(h) => h.id}
        empty={t('app.none')}
        columns={[
          { key: 'k', header: t('backups.kind'), render: (h) => <span className="row">{t(`licence.kinds.${h.kind}`)}{h.isCurrent && <Badge tone="success">{t('licence.current')}</Badge>}</span> },
          { key: 'c', header: t('licence.customer'), render: (h) => h.customer },
          { key: 'u', header: t('licence.validUntil'), render: (h) => formatDate(h.validUntil, lang) },
          { key: 's', header: t('licence.source'), render: (h) => t(`licence.sources.${h.source}`) },
          { key: 'i', header: t('licence.importedAt'), render: (h) => formatDateTime(h.importedAt, lang) },
        ]}
      />
    </div>
  );
}

/** The licence banner over every page (blueprint 24.5): warnings from 14 days before the end, grace, ended, switched off, Nextdab messages. */
export function LicenceBanner() {
  const api = useApi();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const [hidden, setHidden] = useState<string[]>([]);
  const status = useQuery({ queryKey: ['licence-status'], queryFn: () => api.licenceStatus(), refetchInterval: 5 * 60_000, enabled: !!me });
  const s: Banner | undefined = status.data;
  if (!s) return null;
  const admin = can(me, 'LICENCE.MANAGE');
  const link = admin ? <Link to="/app/licence">{t('licence.banner.adminLink')}</Link> : null;
  const pickAr = (en: string | null, ar: string | null) => (lang === 'ar' ? ar || en : en || ar) ?? '';
  const main =
    s.state === 'suspended' ? { tone: 'error' as const, text: `${t('licence.banner.suspended')} ${pickAr(s.suspendMessage, s.suspendMessageAr)}` } :
    s.state === 'expired' ? { tone: 'error' as const, text: t('licence.banner.expired') } :
    s.state === 'grace' ? { tone: 'warning' as const, text: t('licence.banner.grace', { validUntil: formatDate(s.validUntil, lang), date: formatDate(s.endsOn, lang) }) } :
    s.state === 'expiring' ? { tone: 'warning' as const, text: t('licence.banner.expiring', { date: formatDate(s.validUntil, lang), days: s.daysLeft ?? 0 }) } :
    s.state === 'none' ? { tone: 'warning' as const, text: t('licence.banner.none', { admin: '' }).trim() } : null;
  const messages = s.messages.filter((m) => !hidden.includes(m.id));
  // An empty marker once the status is known, so tests can tell "nothing to warn about" from "not loaded yet".
  if (!main && messages.length === 0) return <span data-testid="licence-ok" hidden />;
  return (
    <div className="stack" style={{ gap: 8, marginBlockEnd: 16 }} data-testid="licence-banner">
      {main && <Alert tone={main.tone}>{main.text} {link}</Alert>}
      {messages.map((m) => (
        <Alert key={m.id} tone={m.level === 'important' ? 'warning' : 'info'}>
          <strong>{pickAr(m.title, m.titleAr)}</strong> {pickAr(m.text, m.textAr)}{' '}
          <Button size="sm" variant="ghost" onClick={() => setHidden([...hidden, m.id])}>{t('licence.banner.dismiss')}</Button>
        </Alert>
      ))}
    </div>
  );
}
