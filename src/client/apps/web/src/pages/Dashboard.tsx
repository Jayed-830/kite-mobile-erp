import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { can } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Badge, Card, PageHeader } from '@kite/ui';
import { useApi, useAuth } from '../core';

export function DashboardPage() {
  const api = useApi();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const tax = useQuery({ queryKey: ['tax'], queryFn: () => api.taxCodes(), enabled: can(me, 'SETTINGS.VIEW') });
  const terminals = useQuery({ queryKey: ['terminals'], queryFn: () => api.terminals(), enabled: can(me, 'TERMINALS.APPROVE') });
  if (!me) return null;
  const pendingTax = tax.data?.filter((c) => c.needsAccountantReview).length ?? 0;
  const pendingTerminals = terminals.data?.filter((x) => x.status === 'pending').length ?? 0;

  return (
    <div className="stack">
      <PageHeader title={t('dashboard.welcome', { name: pick(me.user.displayNameEn, me.user.displayNameAr) })} />
      <Card>
        <div className="stack">
          <h2>{t('dashboard.checklist')}</h2>
          {tax.data && (
            <div className="row">
              <Badge tone={pendingTax ? 'warning' : 'success'}>{pendingTax ? '!' : '✓'}</Badge>
              <Link to="/app/settings/tax">{pendingTax ? t('dashboard.taxReview', { count: pendingTax }) : t('dashboard.taxReviewed')}</Link>
            </div>
          )}
          <div className="row">
            <Badge tone={me.user.totpEnabled ? 'success' : 'warning'}>{me.user.totpEnabled ? '✓' : '!'}</Badge>
            <Link to="/app/account">{me.user.totpEnabled ? t('dashboard.twoFactorOn') : t('dashboard.twoFactorOff')}</Link>
          </div>
          {terminals.data && pendingTerminals > 0 && (
            <div className="row">
              <Badge tone="warning">!</Badge>
              <Link to="/app/terminals">{t('dashboard.terminalsPending', { count: pendingTerminals })}</Link>
            </div>
          )}
        </div>
      </Card>
    </div>
  );
}
