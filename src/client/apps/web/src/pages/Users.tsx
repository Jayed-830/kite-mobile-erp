import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type RoleGrant, type User } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Badge, Button, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi, useAuth } from '../core';

interface Draft {
  id?: string;
  username: string;
  displayNameEn: string;
  displayNameAr: string;
  preferredLanguage: 'en' | 'ar';
  status: 'active' | 'disabled';
  temporaryPassword: string;
  roles: RoleGrant[];
  version: number;
}

const empty: Draft = { username: '', displayNameEn: '', displayNameAr: '', preferredLanguage: 'en', status: 'active', temporaryPassword: '', roles: [], version: 1 };

export function UsersPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const users = useQuery({ queryKey: ['users'], queryFn: () => api.users() });
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => api.roles() });
  const branches = useQuery({ queryKey: ['branches'], queryFn: () => api.branches() });
  const [draft, setDraft] = useState<Draft | null>(null);
  const [reset, setReset] = useState<{ user: User; password: string } | null>(null);
  const manage = can(me, 'USERS.MANAGE');

  const save = useMutation({
    mutationFn: async (d: Draft) => d.id
      ? api.updateUser(d.id, { displayNameEn: d.displayNameEn, displayNameAr: d.displayNameAr || null, preferredLanguage: d.preferredLanguage, status: d.status, roles: d.roles, version: d.version })
      : api.createUser({ username: d.username, displayNameEn: d.displayNameEn, displayNameAr: d.displayNameAr || null, preferredLanguage: d.preferredLanguage, temporaryPassword: d.temporaryPassword, roles: d.roles }),
    onSuccess: async () => { setDraft(null); await qc.invalidateQueries({ queryKey: ['users'] }); },
  });
  const unlock = useMutation({ mutationFn: (id: string) => api.unlockUser(id), onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }) });
  const resetPw = useMutation({ mutationFn: (r: { id: string; pw: string }) => api.resetPassword(r.id, r.pw), onSuccess: () => setReset(null) });

  const roleName = (id: string) => { const r = roles.data?.find((x) => x.id === id); return r ? pick(r.nameEn, r.nameAr) : '?'; };
  const branchName = (id: string | null) => { if (!id) return t('app.allBranches'); const b = branches.data?.find((x) => x.id === id); return b ? pick(b.nameEn, b.nameAr) : '?'; };

  if (users.isLoading) return <Spinner label={t('app.loading')} />;

  return (
    <div className="stack">
      <PageHeader title={t('users.title')}>
        {manage && <Button variant="primary" onClick={() => { save.reset(); setDraft({ ...empty, roles: [] }); }}>{t('users.add')}</Button>}
      </PageHeader>
      <ErrorBox error={users.error} />
      <DataTable
        rows={users.data ?? []}
        rowKey={(u) => u.id}
        empty={t('app.none')}
        columns={[
          { key: 'u', header: t('users.username'), render: (u) => <span className="ltr">{u.username}</span> },
          { key: 'n', header: t('users.name'), render: (u) => pick(u.displayNameEn, u.displayNameAr) },
          { key: 'r', header: t('users.roles'), render: (u) => u.roles.map((r) => `${pick(r.roleNameEn, r.roleNameAr)} · ${branchName(r.branchId)}`).join(' | ') },
          { key: 's', header: t('app.status'), render: (u) => <span className="row">
            <Badge tone={u.status === 'active' ? 'success' : 'default'}>{u.status === 'active' ? t('app.active') : t('app.disabled')}</Badge>
            {u.locked && <Badge tone="danger">{t('users.locked')}</Badge>}
            {u.totpEnabled && <Badge tone="primary">{t('users.twoFactor')}</Badge>}
          </span> },
          { key: 'l', header: t('users.lastLogin'), render: (u) => formatDateTime(u.lastLoginAt, lang) },
          { key: 'a', header: t('app.actions'), render: (u) => manage && <span className="row">
            <Button size="sm" onClick={() => { save.reset(); setDraft({ id: u.id, username: u.username, displayNameEn: u.displayNameEn, displayNameAr: u.displayNameAr ?? '', preferredLanguage: u.preferredLanguage, status: u.status, temporaryPassword: '', roles: u.roles.map((r) => ({ roleId: r.roleId, branchId: r.branchId })), version: u.version }); }}>{t('app.edit')}</Button>
            {u.locked && <Button size="sm" onClick={() => unlock.mutate(u.id)}>{t('users.unlock')}</Button>}
            <Button size="sm" variant="ghost" onClick={() => { resetPw.reset(); setReset({ user: u, password: '' }); }}>{t('users.resetPassword')}</Button>
          </span> },
        ]}
      />

      {draft && (
        <Modal open title={draft.id ? draft.username : t('users.add')} onClose={() => setDraft(null)}
          footer={<><Button onClick={() => setDraft(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={save.isPending} onClick={() => save.mutate(draft)}>{t('app.save')}</Button></>}>
          {!draft.id && <TextField label={t('users.username')} value={draft.username} onChange={(e) => setDraft({ ...draft, username: e.target.value.toLowerCase() })} dir="ltr" required />}
          <div className="grid-2">
            <TextField label={t('users.name')} value={draft.displayNameEn} onChange={(e) => setDraft({ ...draft, displayNameEn: e.target.value })} required />
            <TextField label={t('users.nameAr')} value={draft.displayNameAr} onChange={(e) => setDraft({ ...draft, displayNameAr: e.target.value })} dir="rtl" />
            <SelectField label={t('users.language')} value={draft.preferredLanguage} onChange={(e) => setDraft({ ...draft, preferredLanguage: e.target.value as 'en' | 'ar' })}>
              <option value="en">English</option><option value="ar">العربية</option>
            </SelectField>
            {draft.id && (
              <SelectField label={t('app.status')} value={draft.status} onChange={(e) => setDraft({ ...draft, status: e.target.value as 'active' | 'disabled' })}>
                <option value="active">{t('app.active')}</option><option value="disabled">{t('app.disabled')}</option>
              </SelectField>
            )}
          </div>
          {!draft.id && <TextField label={t('users.temporaryPassword')} help={t('users.temporaryHelp')} type="password" value={draft.temporaryPassword} onChange={(e) => setDraft({ ...draft, temporaryPassword: e.target.value })} autoComplete="new-password" required />}
          <h3>{t('users.roles')}</h3>
          {draft.roles.map((g, i) => (
            <div key={i} className="row">
              <div style={{ flex: 1 }}>
                <SelectField label={t('users.roles')} value={g.roleId} onChange={(e) => setDraft({ ...draft, roles: draft.roles.map((x, j) => (j === i ? { ...x, roleId: e.target.value } : x)) })}>
                  {roles.data?.map((r) => <option key={r.id} value={r.id}>{pick(r.nameEn, r.nameAr)}</option>)}
                </SelectField>
              </div>
              <div style={{ flex: 1 }}>
                <SelectField label={t('users.branch')} value={g.branchId ?? ''} onChange={(e) => setDraft({ ...draft, roles: draft.roles.map((x, j) => (j === i ? { ...x, branchId: e.target.value || null } : x)) })}>
                  {me?.allBranches && <option value="">{t('app.allBranches')}</option>}
                  {branches.data?.map((b) => <option key={b.id} value={b.id}>{pick(b.nameEn, b.nameAr)}</option>)}
                </SelectField>
              </div>
              <Button size="sm" variant="ghost" aria-label={t('app.close')} onClick={() => setDraft({ ...draft, roles: draft.roles.filter((_, j) => j !== i) })} style={{ marginBlockStart: 22 }}>✕</Button>
            </div>
          ))}
          <div><Button size="sm" onClick={() => {
            const firstRole = roles.data?.find((r) => r.code !== 'owner') ?? roles.data?.[0];
            if (firstRole) setDraft({ ...draft, roles: [...draft.roles, { roleId: firstRole.id, branchId: me?.allBranches ? null : (branches.data?.[0]?.id ?? null) }] });
          }}>{t('users.addRole')}</Button></div>
          <span className="muted" style={{ fontSize: 13 }}>{draft.roles.map((r) => `${roleName(r.roleId)} · ${branchName(r.branchId)}`).join(' | ')}</span>
          <ErrorBox error={save.error} />
        </Modal>
      )}

      {reset && (
        <Modal open title={`${t('users.resetPassword')}: ${reset.user.username}`} onClose={() => setReset(null)}
          footer={<><Button onClick={() => setReset(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={resetPw.isPending} onClick={() => resetPw.mutate({ id: reset.user.id, pw: reset.password })}>{t('app.save')}</Button></>}>
          <TextField label={t('users.temporaryPassword')} help={t('users.temporaryHelp')} type="password" value={reset.password} onChange={(e) => setReset({ ...reset, password: e.target.value })} autoComplete="new-password" />
          <ErrorBox error={resetPw.error} />
        </Modal>
      )}
    </div>
  );
}
