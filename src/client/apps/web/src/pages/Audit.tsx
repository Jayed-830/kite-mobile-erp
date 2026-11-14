import { useMutation, useQuery } from '@tanstack/react-query';
import { can } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Button, DataTable, PageHeader, Spinner } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi, useAuth } from '../core';

export function AuditPage() {
  const api = useApi();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const rows = useQuery({ queryKey: ['audit'], queryFn: () => api.audit() });
  const verify = useMutation({ mutationFn: () => api.verifyAudit() });
  if (rows.isLoading) return <Spinner label={t('app.loading')} />;
  return (
    <div className="stack">
      <PageHeader title={t('audit.title')}>
        {can(me, 'AUDIT.VERIFY') && <Button busy={verify.isPending} onClick={() => verify.mutate()}>{t('audit.verify')}</Button>}
      </PageHeader>
      {verify.data && (verify.data.ok
        ? <Alert tone="success">{t('audit.intact', { count: verify.data.rowsChecked })}</Alert>
        : <Alert tone="error">{t('audit.broken', { seq: verify.data.firstBrokenSeq ?? '?' })}</Alert>)}
      <ErrorBox error={rows.error ?? verify.error} />
      <DataTable
        rows={rows.data ?? []}
        rowKey={(r) => String(r.seq)}
        empty={t('app.none')}
        columns={[
          { key: 'q', header: '#', render: (r) => <span className="ltr muted">{r.seq}</span>, width: '64px' },
          { key: 't', header: t('audit.time'), render: (r) => formatDateTime(r.occurredAt, lang) },
          { key: 'u', header: t('audit.user'), render: (r) => <span className="ltr">{r.username ?? '—'}</span> },
          { key: 'a', header: t('audit.action'), render: (r) => <span className="ltr">{r.action}</span> },
          { key: 'e', header: t('audit.entity'), render: (r) => <span className="ltr muted">{r.entityType ?? ''}</span> },
          { key: 'r', header: t('audit.reason'), render: (r) => r.reason ?? '' },
        ]}
      />
    </div>
  );
}
