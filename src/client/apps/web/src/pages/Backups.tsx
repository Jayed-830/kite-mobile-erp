import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type BackupRun } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, DataTable, Modal, PageHeader, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi, useAuth } from '../core';

const size = (n: number | null) => (n === null ? '—' : n > 1 << 30 ? `${(n / (1 << 30)).toFixed(2)} GB` : n > 1 << 20 ? `${(n / (1 << 20)).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);

/** Backups (blueprint 19): status, the last runs with their restore checks, run now, and the printed recovery key. */
export function BackupsPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const st = useQuery({ queryKey: ['backups'], queryFn: () => api.backups(), refetchInterval: (q) => (q.state.data?.running ? 5000 : 60_000) });
  const run = useMutation({ mutationFn: (kind: 'logical' | 'base' | 'verify') => api.runBackup(kind), onSuccess: () => setTimeout(() => void qc.invalidateQueries({ queryKey: ['backups'] }), 1500) });
  const [askKey, setAskKey] = useState<string | null>(null);
  const key = useMutation({ mutationFn: (password: string) => api.backupRecoveryKey(password), onSuccess: () => { setAskKey(null); void qc.invalidateQueries({ queryKey: ['backups'] }); } });
  const manage = can(me, 'BACKUP.MANAGE');

  if (!st.data) return st.error ? <ErrorBox error={st.error} /> : <Spinner label={t('app.loading')} />;
  const d = st.data;
  const when = (iso: string | null) => (iso ? formatDateTime(iso, lang) : t('servers.never'));
  const checksText = (r: BackupRun) => {
    if (r.kind !== 'verify' || !r.checks) return '';
    const c = r.checks as Record<string, unknown>;
    const ok = c.journals_balanced === true && c.outbox_gap_free === true && c.audit_chain_intact === true;
    return ok ? `${t('backups.checksOk')} (${String(c.audit_rows_checked ?? '')})` : JSON.stringify(c);
  };

  return (
    <div className="stack">
      <PageHeader title={t('backups.title')}>
        {manage && d.configured && (
          <>
            <Button variant="primary" busy={run.isPending && run.variables === 'logical'} disabled={!!d.running} onClick={() => run.mutate('logical')}>{t('backups.runNow')}</Button>
            {d.schedule.baseBackups && <Button disabled={!!d.running} onClick={() => run.mutate('base')}>{t('backups.runBase')}</Button>}
            <Button disabled={!!d.running} onClick={() => run.mutate('verify')}>{t('backups.verifyNow')}</Button>
          </>
        )}
      </PageHeader>
      {!d.configured && <Alert tone="warning">{t('backups.notConfigured')}</Alert>}
      {d.configured && (d.alert ? <Alert tone="error">{t('backups.alert', { hours: d.alertAfterHours })}</Alert> : <Alert tone="success">{t('backups.ok')}</Alert>)}
      {d.running && <Alert tone="info">{t('backups.running', { kind: t(`backups.kinds.${d.running}`) })}</Alert>}
      {run.isSuccess && <Alert tone="info">{t('backups.requested')}</Alert>}
      <ErrorBox error={run.error} />

      <div className="grid-2">
        <Card>
          <dl className="grid-2" style={{ margin: 0 }}>
            <div><dt className="muted">{t('backups.lastLogical')}</dt><dd style={{ margin: 0 }}>{when(d.lastLogicalOk)}</dd></div>
            <div><dt className="muted">{t('backups.lastVerify')}</dt><dd style={{ margin: 0 }}>{when(d.lastVerifyOk)}</dd></div>
            {d.schedule.baseBackups && <div><dt className="muted">{t('backups.lastBase')}</dt><dd style={{ margin: 0 }}>{when(d.lastBaseOk)}</dd></div>}
            {d.offsiteFolder && <div><dt className="muted">{t('backups.lastOffsite')}</dt><dd style={{ margin: 0 }}>{when(d.lastOffsiteOk)}</dd></div>}
          </dl>
        </Card>
        <Card>
          <dl className="stack" style={{ margin: 0 }}>
            <div><dt className="muted">{t('backups.folder')}</dt><dd style={{ margin: 0 }} className="ltr">{d.folder ?? '—'}</dd></div>
            {d.offsiteFolder && <div><dt className="muted">{t('backups.offsiteFolder')}</dt><dd style={{ margin: 0 }} className="ltr">{d.offsiteFolder}</dd></div>}
            <div><dt className="muted">{t('backups.schedule')}</dt><dd style={{ margin: 0 }}>{t('backups.scheduleText', { logical: d.schedule.logicalAt, base: d.schedule.baseAt, day: d.schedule.verifyDay, verify: d.schedule.verifyAt })}</dd></div>
            {d.wal && <div><dt className="muted">{t('backups.wal')}</dt><dd style={{ margin: 0 }}>{t('backups.walText', { mode: d.wal.mode, archived: d.wal.archived, failed: d.wal.failed })}</dd></div>}
            <div><dt className="muted">{t('backups.keyProtection')}</dt><dd style={{ margin: 0 }}>{t(`backups.protections.${d.keyProtection}`)}</dd></div>
          </dl>
        </Card>
      </div>

      {manage && (
        <Card>
          <div className="stack">
            <h2 style={{ margin: 0 }}>{t('backups.recoveryKey')}</h2>
            <p className="muted" style={{ margin: 0 }}>{t('backups.recoveryHelp')}</p>
            {key.data ? (
              <div className="stack">
                <div className="ltr" style={{ fontSize: 20, fontWeight: 700, letterSpacing: 1, wordBreak: 'break-word' }} data-testid="recovery-key">{key.data.recoveryKey}</div>
                <div className="muted">{t('backups.keyId')}: <span className="ltr">{key.data.keyId}</span></div>
                <div><Button onClick={() => window.print()}>{t('backups.print')}</Button></div>
              </div>
            ) : (
              <div className="row">
                <Button onClick={() => { key.reset(); setAskKey(''); }}>{t('backups.showKey')}</Button>
                {d.recoveryKeyShownAt && <span className="muted">{t('backups.shownAt', { time: formatDateTime(d.recoveryKeyShownAt, lang) })}</span>}
              </div>
            )}
          </div>
        </Card>
      )}

      <h2>{t('backups.runs')}</h2>
      <DataTable
        rows={d.runs}
        rowKey={(r) => r.id}
        empty={t('app.none')}
        columns={[
          { key: 'k', header: t('backups.kind'), render: (r) => t(`backups.kinds.${r.kind}`) },
          { key: 's', header: t('app.status'), render: (r) => <Badge tone={r.status === 'succeeded' ? 'success' : r.status === 'failed' ? 'danger' : 'warning'}>{t(`backups.statuses.${r.status}`)}</Badge> },
          { key: 'b', header: t('backups.started'), render: (r) => formatDateTime(r.startedAt, lang) },
          { key: 'f', header: t('backups.finished'), render: (r) => (r.finishedAt ? formatDateTime(r.finishedAt, lang) : '—') },
          { key: 'z', header: t('backups.size'), render: (r) => <span className="ltr">{size(r.sizeBytes)}</span> },
          { key: 'c', header: t('backups.checks'), render: (r) => (r.error ? <span className="ltr" style={{ color: 'var(--danger, #b42318)' }}>{r.error}</span> : checksText(r)) },
        ]}
      />

      {askKey !== null && (
        <Modal open title={t('backups.recoveryKey')} onClose={() => setAskKey(null)}
          footer={<><Button onClick={() => setAskKey(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={key.isPending} disabled={!askKey} onClick={() => key.mutate(askKey)}>{t('backups.showKey')}</Button></>}>
          <TextField label={t('servers.yourPassword')} type="password" value={askKey} onChange={(e) => setAskKey(e.target.value)} autoComplete="current-password" autoFocus />
          <ErrorBox error={key.error} />
        </Modal>
      )}
    </div>
  );
}
