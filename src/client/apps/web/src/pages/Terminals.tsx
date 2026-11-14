import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useI18n } from '@kite/i18n';
import { Badge, Button, Checkbox, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi } from '../core';

export function TerminalsPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { t, pick, lang } = useI18n();
  const terminals = useQuery({ queryKey: ['terminals'], queryFn: () => api.terminals() });
  const branches = useQuery({ queryKey: ['branches'], queryFn: () => api.branches() });
  const [form, setForm] = useState<{ branchId: string; code: string; name: string; kind: 'windows' | 'browser' } | null>(null);
  const [approve, setApprove] = useState<{ id: string; emergency: boolean; windows: boolean } | null>(null);
  const refresh = () => qc.invalidateQueries({ queryKey: ['terminals'] });

  const request = useMutation({ mutationFn: (f: NonNullable<typeof form>) => api.requestTerminal(f), onSuccess: async () => { setForm(null); await refresh(); } });
  const doApprove = useMutation({ mutationFn: (a: NonNullable<typeof approve>) => api.approveTerminal(a.id, a.emergency), onSuccess: async () => { setApprove(null); await refresh(); } });
  const revoke = useMutation({ mutationFn: (id: string) => api.revokeTerminal(id), onSuccess: refresh });

  if (terminals.isLoading) return <Spinner label={t('app.loading')} />;
  const branchName = (id: string) => { const b = branches.data?.find((x) => x.id === id); return b ? pick(b.nameEn, b.nameAr) : ''; };
  const tone = (s: string) => (s === 'active' ? 'success' : s === 'pending' ? 'warning' : 'default') as 'success' | 'warning' | 'default';

  return (
    <div className="stack">
      <PageHeader title={t('terminals.title')}>
        <Button variant="primary" onClick={() => { request.reset(); setForm({ branchId: branches.data?.[0]?.id ?? '', code: '', name: '', kind: 'windows' }); }}>{t('terminals.request')}</Button>
      </PageHeader>
      <ErrorBox error={terminals.error ?? revoke.error} />
      <DataTable
        rows={terminals.data ?? []}
        rowKey={(x) => x.id}
        empty={t('app.none')}
        columns={[
          { key: 'c', header: t('terminals.code'), render: (x) => <span className="ltr">{x.code}</span> },
          { key: 'n', header: t('terminals.name'), render: (x) => x.name },
          { key: 'b', header: t('users.branch'), render: (x) => branchName(x.branchId) },
          { key: 'k', header: t('terminals.kind'), render: (x) => (x.kind === 'windows' ? t('terminals.windows') : t('terminals.browser')) },
          { key: 's', header: t('app.status'), render: (x) => <span className="row"><Badge tone={tone(x.status)}>{t(`app.${x.status}`)}</Badge>{x.emergencyEnabled && <Badge tone="primary">{t('terminals.emergency')}</Badge>}</span> },
          { key: 'r', header: t('terminals.requested'), render: (x) => formatDateTime(x.requestedAt, lang) },
          { key: 'a', header: t('app.actions'), render: (x) => <span className="row">
            {x.status === 'pending' && <Button size="sm" variant="primary" onClick={() => { doApprove.reset(); setApprove({ id: x.id, emergency: false, windows: x.kind === 'windows' }); }}>{t('terminals.approve')}</Button>}
            {x.status !== 'revoked' && <Button size="sm" variant="danger" onClick={() => revoke.mutate(x.id)}>{t('terminals.revoke')}</Button>}
          </span> },
        ]}
      />
      {form && (
        <Modal open title={t('terminals.request')} onClose={() => setForm(null)}
          footer={<><Button onClick={() => setForm(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={request.isPending} onClick={() => request.mutate(form)}>{t('app.save')}</Button></>}>
          <SelectField label={t('users.branch')} value={form.branchId} onChange={(e) => setForm({ ...form, branchId: e.target.value })}>
            {branches.data?.map((b) => <option key={b.id} value={b.id}>{pick(b.nameEn, b.nameAr)}</option>)}
          </SelectField>
          <div className="grid-2">
            <TextField label={t('terminals.code')} value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value.toUpperCase() })} maxLength={12} dir="ltr" required />
            <TextField label={t('terminals.name')} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          </div>
          <SelectField label={t('terminals.kind')} value={form.kind} onChange={(e) => setForm({ ...form, kind: e.target.value as 'windows' | 'browser' })}>
            <option value="windows">{t('terminals.windows')}</option><option value="browser">{t('terminals.browser')}</option>
          </SelectField>
          <ErrorBox error={request.error} />
        </Modal>
      )}
      {approve && (
        <Modal open title={t('terminals.approve')} onClose={() => setApprove(null)}
          footer={<><Button onClick={() => setApprove(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={doApprove.isPending} onClick={() => doApprove.mutate(approve)}>{t('terminals.approve')}</Button></>}>
          {approve.windows && <Checkbox label={t('terminals.emergency')} checked={approve.emergency} onChange={(e) => setApprove({ ...approve, emergency: e.target.checked })} />}
          <ErrorBox error={doApprove.error} />
        </Modal>
      )}
    </div>
  );
}
