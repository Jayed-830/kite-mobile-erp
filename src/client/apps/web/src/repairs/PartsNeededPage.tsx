import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { can } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Button, Card, DataTable, PageHeader, Spinner } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi, useAuth } from '../core';
import { qty } from '../money';
import { RepairStatusBadge } from './bits';

/** What the workshop is waiting for (13.4): open parts requests, oldest first, with the stock on hand. */
export function PartsNeededPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, lang, pick } = useI18n();
  const [done, setDone] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const q = useQuery({ queryKey: ['partsNeeded'], queryFn: () => api.partsNeeded() });
  const fill = () => api.fillPartsRequests().then((r) => { setDone(t('parts.filled', { n: r.moved })); void qc.invalidateQueries({ queryKey: ['partsNeeded'] }); void qc.invalidateQueries({ queryKey: ['repairs'] }); }).catch(setError);
  return (
    <div className="stack">
      <PageHeader title={t('parts.neededTitle')}>
        {can(me, 'REPAIR.WORK') && <Button variant="primary" onClick={() => { setDone(null); setError(null); void fill(); }}>{t('parts.fill')}</Button>}
      </PageHeader>
      <p className="muted">{t('parts.neededIntro')}</p>
      <ErrorBox error={error ?? q.error} />
      {done && <Alert tone="success">{done}</Alert>}
      {q.isLoading ? <Spinner label={t('app.loading')} /> : (
        <Card>
          <DataTable rows={q.data ?? []} rowKey={(r) => `${r.jobId}:${r.variantId}`} empty={t('parts.noneNeeded')}
            columns={[
              { key: 'j', header: t('parts.job'), render: (r) => <span><Link to={`/app/repairs/${r.jobId}`} className="ltr">{r.jobNumber}</Link> <RepairStatusBadge status={r.jobStatus} /></span> },
              { key: 'b', header: t('parts.branch'), render: (r) => <span className="ltr">{r.branchCode}</span> },
              { key: 'p', header: t('parts.part'), render: (r) => <span>{pick(r.nameEn, r.nameAr)} <span className="ltr muted">{r.sku}</span></span> },
              { key: 'o', header: t('parts.outstanding'), render: (r) => <strong className="num">{qty(r.outstanding)}</strong> },
              { key: 'h', header: t('parts.onHand'), render: (r) => <span className="num">{qty(r.onHand)}</span> },
              { key: 'r', header: t('parts.reserved'), render: (r) => <span className="num">{qty(r.reserved)}</span> },
              { key: 's', header: t('parts.since'), render: (r) => r.openSince ? <span className="muted">{formatDateTime(r.openSince, lang)}</span> : '' },
            ]} />
        </Card>
      )}
    </div>
  );
}
