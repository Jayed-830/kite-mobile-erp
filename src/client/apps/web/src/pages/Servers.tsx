import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type BranchAdmin, type NewBranch, type RegistrationToken, type ServerInfo } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDate, formatDateTime, useApi, useAuth } from '../core';

/** Saves text as a file in the browser (recovery copy). */
function downloadText(name: string, text: string, type = 'application/x-pem-file') {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Branches and servers (blueprint 18.1, 26.4): add branches, register branch servers with a one-time token, revoke them. */
export function ServersPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const data = useQuery({ queryKey: ['servers'], queryFn: () => api.servers(), refetchInterval: 30_000 });
  const refresh = () => qc.invalidateQueries({ queryKey: ['servers'] });
  const [addForm, setAddForm] = useState<NewBranch | null>(null);
  const [token, setToken] = useState<{ code: string; value: RegistrationToken } | null>(null);
  const [revoke, setRevoke] = useState<{ server: ServerInfo; reason: 'server_replaced' | 'key_compromise' } | null>(null);
  const [recovery, setRecovery] = useState<{ password: string; passphrase: string } | null>(null);
  const manageBranches = can(me, 'BRANCHES.MANAGE');
  const manageServers = can(me, 'SERVERS.MANAGE');

  const add = useMutation({ mutationFn: (b: NewBranch) => api.createBranch(b), onSuccess: async () => { setAddForm(null); await refresh(); await qc.invalidateQueries({ queryKey: ['branches'] }); } });
  const makeToken = useMutation({ mutationFn: (b: BranchAdmin) => api.createRegistrationToken(b.id).then((value) => ({ code: b.code, value })), onSuccess: async (x) => { setToken(x); await refresh(); } });
  const cancelToken = useMutation({ mutationFn: (id: string) => api.cancelRegistrationToken(id), onSuccess: refresh });
  const doRevoke = useMutation({ mutationFn: (r: NonNullable<typeof revoke>) => api.revokeServer(r.server.id, r.reason), onSuccess: async () => { setRevoke(null); await refresh(); } });
  const setStatus = useMutation({
    mutationFn: (b: BranchAdmin) => api.updateBranch(b.id, { nameEn: b.nameEn, nameAr: b.nameAr, addressEn: b.addressEn, addressAr: b.addressAr, phone: b.phone, status: b.status === 'active' ? 'inactive' : 'active', version: b.version }),
    onSuccess: refresh,
  });
  const exportCopy = useMutation({
    mutationFn: (r: NonNullable<typeof recovery>) => api.caRecoveryCopy(r.password, r.passphrase),
    onSuccess: (pem) => { downloadText('KITE-business-certificate-recovery.pem', pem); setRecovery(null); },
  });

  if (data.isLoading || !data.data) return data.error ? <ErrorBox error={data.error} /> : <Spinner label={t('app.loading')} />;
  const d = data.data;
  const isHeadOffice = d.self.role !== 'branch';
  const serverOf = (b: BranchAdmin) => d.servers.filter((s) => s.branchId === b.id && s.role === 'branch').sort((x, y) => (x.status === 'active' ? -1 : 1) - (y.status === 'active' ? -1 : 1))[0];
  const openTokens = d.tokens.filter((x) => !x.usedAt && !x.cancelledAt && new Date(x.expiresAt) > new Date());
  const roleName = (r: string) => t(`servers.roles.${r}`);

  return (
    <div className="stack">
      <PageHeader title={t('servers.title')}>
        {isHeadOffice && manageBranches && <Button variant="primary" onClick={() => { add.reset(); setAddForm({ code: '', nameEn: '', nameAr: '', addressEn: '', addressAr: '', phone: '' }); }}>{t('servers.addBranch')}</Button>}
      </PageHeader>
      <ErrorBox error={makeToken.error ?? cancelToken.error ?? setStatus.error} />

      <div className="grid-2">
        <Card>
          <div className="stack">
            <h2>{t('servers.thisServer')}</h2>
            <dl className="stack" style={{ margin: 0 }}>
              <div><dt className="muted">{t('servers.role')}</dt><dd style={{ margin: 0 }}>{roleName(d.self.role)}</dd></div>
              <div><dt className="muted">{t('servers.installationId')}</dt><dd style={{ margin: 0 }} className="ltr">{d.self.installationId ?? '—'}</dd></div>
              <div><dt className="muted">{t('servers.version')}</dt><dd style={{ margin: 0 }} className="ltr">{d.self.version}</dd></div>
            </dl>
          </div>
        </Card>
        {d.self.ca && (
          <Card>
            <div className="stack">
              <h2>{t('servers.caTitle')}</h2>
              <div><div className="muted">{t('servers.caCode')}</div><div className="ltr" style={{ fontSize: 24, fontWeight: 700, letterSpacing: 2 }} data-testid="ca-code">{d.self.ca.fingerprintCode}</div></div>
              <p className="muted" style={{ margin: 0 }}>{t('servers.caHelp')}</p>
              <div><div className="muted">{t('servers.addresses')}</div><div className="ltr">{d.self.ca.hosts.map((h) => `https://${h}:${d.syncPort}`).join('  ·  ')}</div></div>
              {manageServers && <div><Button onClick={() => { exportCopy.reset(); setRecovery({ password: '', passphrase: '' }); }}>{t('servers.recoveryCopy')}</Button></div>}
            </div>
          </Card>
        )}
        {d.link && (
          <Card>
            <div className="stack">
              <h2>{t('servers.linkTitle')}</h2>
              {d.link.revoked && <Alert tone="error">{t('servers.revokedBanner')}</Alert>}
              <dl className="stack" style={{ margin: 0 }}>
                <div><dt className="muted">{t('servers.mainUrl')}</dt><dd style={{ margin: 0 }} className="ltr">{d.link.mainUrl ?? '—'}</dd></div>
                <div><dt className="muted">{t('servers.lastOk')}</dt><dd style={{ margin: 0 }}>{d.link.lastOkAt ? formatDateTime(d.link.lastOkAt, lang) : t('servers.never')}</dd></div>
                {d.link.lastError && <div><dt className="muted">{t('servers.lastError')}</dt><dd style={{ margin: 0 }} className="ltr">{d.link.lastError}</dd></div>}
              </dl>
            </div>
          </Card>
        )}
      </div>

      <h2>{t('servers.branches')}</h2>
      <DataTable
        rows={d.branches}
        rowKey={(b) => b.id}
        empty={t('app.none')}
        columns={[
          { key: 'c', header: t('servers.code'), render: (b) => <span className="ltr">{b.code}</span> },
          { key: 'n', header: t('servers.name'), render: (b) => <span>{pick(b.nameEn, b.nameAr)} {b.isHeadOffice && <Badge tone="primary">{t('servers.headOffice')}</Badge>}</span> },
          { key: 's', header: t('app.status'), render: (b) => <Badge tone={b.status === 'active' ? 'success' : 'default'}>{t(`app.${b.status}`)}</Badge> },
          { key: 'v', header: t('servers.server'), render: (b) => {
            if (b.isHeadOffice) return <span className="muted">{t('servers.servedHere')}</span>;
            const s = serverOf(b);
            if (!s) return <span className="muted">{t('servers.noServer')}</span>;
            return (
              <div className="stack" style={{ gap: 2 }}>
                <span className="row">
                  <strong>{s.name}</strong>
                  {s.status === 'revoked' ? <Badge tone="danger">{t('app.revoked')}</Badge> : <Badge tone={s.online ? 'success' : 'warning'}>{s.online ? t('servers.online') : t('servers.offline')}</Badge>}
                </span>
                <span className="muted" style={{ fontSize: 13 }}>
                  {s.lastSeenAt && t('servers.lastSeen', { time: formatDateTime(s.lastSeenAt, lang) })}
                  {s.appVersion && <> · <span className="ltr">{s.appVersion}</span></>}
                  {s.certificateExpires && s.status === 'active' && <> · {t('servers.certUntil', { date: formatDate(s.certificateExpires, lang) })}</>}
                  {(s.heartbeat?.pending ?? 0) > 0 && <> · {t('servers.pendingEvents', { count: s.heartbeat!.pending! })}</>}
                </span>
              </div>
            );
          } },
          { key: 'a', header: t('app.actions'), render: (b) => {
            if (!isHeadOffice || b.isHeadOffice) return null;
            const s = serverOf(b);
            const live = s && s.status === 'active';
            return (
              <span className="row">
                {manageServers && b.status === 'active' && !live && <Button size="sm" variant="primary" busy={makeToken.isPending && makeToken.variables?.id === b.id} onClick={() => makeToken.mutate(b)}>{t('servers.register')}</Button>}
                {manageServers && live && <Button size="sm" variant="danger" onClick={() => { doRevoke.reset(); setRevoke({ server: s, reason: 'server_replaced' }); }}>{t('servers.revoke')}</Button>}
                {manageBranches && <Button size="sm" onClick={() => { if (b.status === 'inactive' || window.confirm(t('servers.deactivateConfirm', { code: b.code }))) setStatus.mutate(b); }}>{b.status === 'active' ? t('servers.deactivate') : t('servers.activate')}</Button>}
              </span>
            );
          } },
        ]}
      />

      {openTokens.length > 0 && (
        <>
          <h2>{t('servers.openTokens')}</h2>
          <DataTable
            rows={openTokens}
            rowKey={(x) => x.id}
            empty={t('app.none')}
            columns={[
              { key: 'b', header: t('servers.code'), render: (x) => <span className="ltr">{x.branchCode}</span> },
              { key: 'e', header: t('licence.validUntil'), render: (x) => formatDateTime(x.expiresAt, lang) },
              { key: 'a', header: t('app.actions'), render: (x) => manageServers && <Button size="sm" onClick={() => cancelToken.mutate(x.id)}>{t('servers.cancelToken')}</Button> },
            ]}
          />
        </>
      )}

      {addForm && (
        <Modal open title={t('servers.addBranch')} onClose={() => setAddForm(null)}
          footer={<><Button onClick={() => setAddForm(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={add.isPending} onClick={() => add.mutate({ ...addForm, code: addForm.code.toUpperCase() })}>{t('app.save')}</Button></>}>
          <div className="grid-2">
            <TextField label={t('servers.code')} help={t('setup.branchCodeHelp')} value={addForm.code} onChange={(e) => setAddForm({ ...addForm, code: e.target.value.toUpperCase() })} maxLength={12} dir="ltr" required />
            <TextField label={t('servers.phone')} value={addForm.phone ?? ''} onChange={(e) => setAddForm({ ...addForm, phone: e.target.value })} dir="ltr" type="tel" />
            <TextField label={t('setup.branchNameEn')} value={addForm.nameEn} onChange={(e) => setAddForm({ ...addForm, nameEn: e.target.value })} dir="ltr" required />
            <TextField label={t('setup.branchNameAr')} value={addForm.nameAr} onChange={(e) => setAddForm({ ...addForm, nameAr: e.target.value })} dir="rtl" required />
            <TextField label={t('servers.addressEn')} value={addForm.addressEn ?? ''} onChange={(e) => setAddForm({ ...addForm, addressEn: e.target.value })} dir="ltr" />
            <TextField label={t('servers.addressAr')} value={addForm.addressAr ?? ''} onChange={(e) => setAddForm({ ...addForm, addressAr: e.target.value })} dir="rtl" />
          </div>
          <ErrorBox error={add.error} />
        </Modal>
      )}

      {token && (
        <Modal open title={t('servers.tokenTitle', { code: token.code })} onClose={() => setToken(null)} footer={<Button variant="primary" onClick={() => setToken(null)}>{t('app.close')}</Button>}>
          <div className="stack">
            <p style={{ margin: 0 }}>{t('servers.tokenHelp')}</p>
            <div><div className="muted">{t('setup.mainUrl')}</div><div className="ltr">{token.value.hosts.map((h) => `https://${h}:${token.value.port}`).join('  ·  ')}</div></div>
            <div><div className="muted">{t('servers.tokenValue')}</div><div className="ltr" style={{ fontSize: 18, fontWeight: 700, wordBreak: 'break-all' }} data-testid="registration-token">{token.value.token}</div></div>
            <div className="muted">{t('servers.tokenExpires', { time: formatDateTime(token.value.expiresAt, lang) })}</div>
            <Alert tone="warning">{t('servers.tokenCompare')} <strong className="ltr" style={{ fontSize: 20, letterSpacing: 2 }}>{token.value.fingerprintCode}</strong></Alert>
          </div>
        </Modal>
      )}

      {revoke && (
        <Modal open title={t('servers.revokeTitle', { code: revoke.server.branchCode ?? '' })} onClose={() => setRevoke(null)}
          footer={<><Button onClick={() => setRevoke(null)}>{t('app.cancel')}</Button><Button variant="danger" busy={doRevoke.isPending} onClick={() => doRevoke.mutate(revoke)}>{t('servers.revoke')}</Button></>}>
          <p style={{ marginBlockStart: 0 }}>{t('servers.revokeHelp')}</p>
          <SelectField label={t('servers.reason')} value={revoke.reason} onChange={(e) => setRevoke({ ...revoke, reason: e.target.value as 'server_replaced' | 'key_compromise' })}>
            <option value="server_replaced">{t('servers.reasonReplaced')}</option>
            <option value="key_compromise">{t('servers.reasonCompromise')}</option>
          </SelectField>
          <ErrorBox error={doRevoke.error} />
        </Modal>
      )}

      {recovery && (
        <Modal open title={t('servers.recoveryCopy')} onClose={() => setRecovery(null)}
          footer={<><Button onClick={() => setRecovery(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={exportCopy.isPending} disabled={recovery.passphrase.length < 12 || !recovery.password} onClick={() => exportCopy.mutate(recovery)}>{t('servers.download')}</Button></>}>
          <p style={{ marginBlockStart: 0 }}>{t('servers.recoveryHelp')}</p>
          <TextField label={t('servers.passphrase')} type="password" value={recovery.passphrase} onChange={(e) => setRecovery({ ...recovery, passphrase: e.target.value })} autoComplete="new-password" />
          <TextField label={t('servers.yourPassword')} type="password" value={recovery.password} onChange={(e) => setRecovery({ ...recovery, password: e.target.value })} autoComplete="current-password" />
          <ErrorBox error={exportCopy.error} />
        </Modal>
      )}
    </div>
  );
}
