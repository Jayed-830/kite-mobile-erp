import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useI18n } from '@kite/i18n';
import { Button, DataTable, PageHeader } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi } from '../core';

/** Remote approvals (10.2, 18.3): a cashier asks, a manager decides from any screen. */
export function ApprovalsPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { t, lang } = useI18n();
  const rows = useQuery({ queryKey: ['approvals', 'pending'], queryFn: () => api.approvals('pending'), refetchInterval: 3000 });
  const decide = useMutation({ mutationFn: (d: { id: string; decision: 'approve' | 'reject' }) => api.decideApproval(d.id, d.decision), onSuccess: () => qc.invalidateQueries({ queryKey: ['approvals'] }) });
  return (
    <div className="stack">
      <PageHeader title={t('approvals.title')} />
      <p className="muted">{t('approvals.intro')}</p>
      <ErrorBox error={rows.error ?? decide.error} />
      <DataTable rows={rows.data ?? []} rowKey={(a) => a.id} empty={t('approvals.empty')}
        columns={[
          { key: 'a', header: t('approvals.action'), render: (a) => t(`approvals.actions.${a.actionCode.replace(/\./g, '_')}`) },
          { key: 'r', header: t('approvals.requester'), render: (a) => `${a.requesterName} · ${a.terminalCode ?? ''}` },
          { key: 'why', header: t('approvals.reason'), render: (a) => a.reason ?? '—' },
          { key: 'w', header: t('approvals.when'), render: (a) => formatDateTime(a.createdAt, lang) },
          { key: 'd', header: t('app.actions'), render: (a) => <span className="row">
            <Button size="sm" variant="primary" onClick={() => decide.mutate({ id: a.id, decision: 'approve' })}>{t('approvals.approve')}</Button>
            <Button size="sm" variant="danger" onClick={() => decide.mutate({ id: a.id, decision: 'reject' })}>{t('approvals.reject')}</Button>
          </span> },
        ]} />
    </div>
  );
}
