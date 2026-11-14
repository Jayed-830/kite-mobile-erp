import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type Business } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, Checkbox, DataTable, PageHeader, Spinner, Switch, TextField } from '@kite/ui';
import { ErrorBox, formatDate, formatDateTime, useApi, useAuth } from '../core';

export function BusinessPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t } = useI18n();
  const biz = useQuery({ queryKey: ['business'], queryFn: () => api.business() });
  const [form, setForm] = useState<Business | null>(null);
  const editable = can(me, 'SETUP.BUSINESS.EDIT');
  useEffect(() => { if (biz.data) setForm(biz.data); }, [biz.data]);
  const save = useMutation({
    mutationFn: (b: Business) => api.updateBusiness({
      legalNameEn: b.legalNameEn, legalNameAr: b.legalNameAr, tradeNameEn: b.tradeNameEn, tradeNameAr: b.tradeNameAr, crNumber: b.crNumber,
      vatNumber: b.vatNumber, addressEn: b.addressEn, addressAr: b.addressAr, phone: b.phone, email: b.email, pricesIncludeTax: b.pricesIncludeTax, version: b.version,
    }),
    onSuccess: (b) => { qc.setQueryData(['business'], b); setForm(b); },
  });
  if (!form) return <Spinner label={t('app.loading')} />;
  const f = (k: keyof Business) => ({
    value: (form[k] as string | null) ?? '',
    onChange: (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value }),
    disabled: !editable,
  });
  return (
    <div className="stack">
      <PageHeader title={t('business.title')} />
      <Card>
        <div className="stack">
          <div className="grid-2">
            <TextField label={t('setup.legalNameEn')} {...f('legalNameEn')} dir="ltr" />
            <TextField label={t('setup.legalNameAr')} {...f('legalNameAr')} dir="rtl" />
            <TextField label={t('setup.tradeNameEn')} {...f('tradeNameEn')} dir="ltr" />
            <TextField label={t('setup.tradeNameAr')} {...f('tradeNameAr')} dir="rtl" />
            <TextField label={t('setup.crNumber')} {...f('crNumber')} dir="ltr" />
            <TextField label={t('setup.vatNumber')} {...f('vatNumber')} dir="ltr" />
            <TextField label={t('setup.phone')} {...f('phone')} dir="ltr" />
            <TextField label={t('setup.email')} {...f('email')} dir="ltr" />
            <TextField label={t('business.currency')} value={`${form.baseCurrency} (${form.currencyDecimals})`} disabled dir="ltr" />
            <TextField label={t('business.timeZone')} value={form.timeZone} disabled dir="ltr" />
          </div>
          <Checkbox label={t('setup.pricesIncludeTax')} checked={form.pricesIncludeTax} disabled={!editable} onChange={(e) => setForm({ ...form, pricesIncludeTax: e.target.checked })} />
          <ErrorBox error={save.error} />
          {save.isSuccess && <Alert tone="success">{t('app.saved')}</Alert>}
          {editable && <div><Button variant="primary" busy={save.isPending} onClick={() => save.mutate(form)}>{t('app.save')}</Button></div>}
        </div>
      </Card>
    </div>
  );
}

export function ModulesPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const mods = useQuery({ queryKey: ['modules'], queryFn: () => api.modules() });
  const update = useMutation({ mutationFn: (c: Record<string, boolean>) => api.updateModules(c), onSuccess: (m) => qc.setQueryData(['modules'], m) });
  const editable = can(me, 'SETTINGS.MODULES.EDIT');
  if (!mods.data) return <Spinner label={t('app.loading')} />;
  const nameOf = (code: string) => { const m = mods.data.find((x) => x.code === code); return m ? pick(m.nameEn, m.nameAr) : code; };
  return (
    <div className="stack">
      <PageHeader title={t('modules.title')} />
      <ErrorBox error={update.error} />
      <Card>
        <div className="stack">
          {mods.data.map((m) => (
            <div key={m.code} className="row">
              <Switch label={pick(m.nameEn, m.nameAr)} checked={m.enabled} disabled={m.alwaysOn || !editable || update.isPending} onChange={(v) => update.mutate({ [m.code]: v })} />
              <strong>{pick(m.nameEn, m.nameAr)}</strong>
              {m.alwaysOn && <Badge>{t('modules.alwaysOn')}</Badge>}
              {m.dependsOn.length > 0 && <span className="muted" style={{ fontSize: 13 }}>{t('modules.needs', { list: m.dependsOn.map(nameOf).join(lang === 'ar' ? '، ' : ', ') })}</span>}
            </div>
          ))}
        </div>
      </Card>
    </div>
  );
}

export function TaxPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const codes = useQuery({ queryKey: ['tax'], queryFn: () => api.taxCodes() });
  const review = useMutation({ mutationFn: (id: string) => api.reviewTaxCode(id), onSuccess: () => qc.invalidateQueries({ queryKey: ['tax'] }) });
  if (codes.isLoading) return <Spinner label={t('app.loading')} />;
  return (
    <div className="stack">
      <PageHeader title={t('tax.title')} />
      <Alert tone="info">{t('setup.taxNote')}</Alert>
      <ErrorBox error={codes.error ?? review.error} />
      <DataTable
        rows={codes.data ?? []}
        rowKey={(c) => c.id}
        empty={t('app.none')}
        columns={[
          { key: 'c', header: t('tax.code'), render: (c) => <span className="ltr">{c.code}</span> },
          { key: 'n', header: t('accounts.name'), render: (c) => pick(c.nameEn, c.nameAr) },
          { key: 'k', header: t('tax.kind'), render: (c) => t(`tax.kind_${c.kind}`) },
          { key: 'r', header: t('tax.rate'), render: (c) => c.rates.map((r) => <div key={r.effectiveFrom}><span className="ltr">{r.ratePercent}%</span> <span className="muted">{t('tax.from')} {formatDate(r.effectiveFrom, lang)}</span></div>) },
          { key: 's', header: t('app.status'), render: (c) => <Badge tone={c.status === 'active' ? 'success' : 'default'}>{c.status === 'active' ? t('app.active') : t('app.inactive')}</Badge> },
          { key: 'v', header: t('tax.review'), render: (c) => c.needsAccountantReview
            ? (can(me, 'TAX.REVIEW') ? <Button size="sm" onClick={() => review.mutate(c.id)}>{t('tax.confirm')}</Button> : <Badge tone="warning">{t('tax.waiting')}</Badge>)
            : <span><Badge tone="success">{t('tax.reviewed')}</Badge> <span className="muted" style={{ fontSize: 12 }}>{formatDateTime(c.reviewedAt, lang)}</span></span> },
        ]}
      />
    </div>
  );
}

export function AccountsPage() {
  const api = useApi();
  const { t, pick } = useI18n();
  const accounts = useQuery({ queryKey: ['accounts'], queryFn: () => api.accounts() });
  if (accounts.isLoading) return <Spinner label={t('app.loading')} />;
  return (
    <div className="stack">
      <PageHeader title={t('accounts.title')} />
      <ErrorBox error={accounts.error} />
      {accounts.data && (
        <DataTable
          rows={accounts.data}
          rowKey={(a) => a.id}
          empty={t('app.none')}
          columns={[
            { key: 'c', header: t('accounts.code'), render: (a) => <span className="ltr" style={{ fontWeight: a.isPostable ? 400 : 700 }}>{a.code}</span> },
            { key: 'n', header: t('accounts.name'), render: (a) => <span style={{ fontWeight: a.isPostable ? 400 : 700, paddingInlineStart: a.parentId ? 16 : 0 }}>{pick(a.nameEn, a.nameAr)}</span> },
            { key: 't', header: t('accounts.type'), render: (a) => a.type },
            { key: 'k', header: t('accounts.control'), render: (a) => (a.isControl ? <Badge tone="primary">{t('app.yes')}</Badge> : '') },
          ]}
        />
      )}
    </div>
  );
}
