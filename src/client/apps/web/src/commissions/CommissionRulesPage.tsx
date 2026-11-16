import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type CommissionBasisKind, type CommissionRule, type CommissionRuleInput, type CommissionSettings } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { businessToday, ErrorBox, formatDate, useApi, useAuth } from '../core';
import { fmt, parseAmount } from '../money';

const today = () => businessToday();

/** Commission rules, versioned (15.1), and the settings: when commissions are generated, who approves them, rework clawback. */
export function CommissionRulesPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, lang, pick } = useI18n();
  const rules = useQuery({ queryKey: ['commissionRules'], queryFn: () => api.commissionRules() });
  const [editing, setEditing] = useState<{ rule: CommissionRule | null } | null>(null);
  const [ending, setEnding] = useState<CommissionRule | null>(null);
  const [endOn, setEndOn] = useState(today());
  const [error, setError] = useState<unknown>(null);
  const manage = can(me, 'COMM.RULES');
  const scope = (r: CommissionRule) => [r.technicianName ? pick(r.technicianName, r.technicianNameAr ?? r.technicianName) : null, r.branchCode, pick(r.categoryEn ?? '', r.categoryAr ?? '') || null].filter(Boolean).join(' · ') || t('comm.anyone');
  return (
    <div className="stack">
      <PageHeader title={t('comm.rules')}>
        {manage && <Button variant="primary" onClick={() => { setError(null); setEditing({ rule: null }); }}>{t('comm.newRule')}</Button>}
      </PageHeader>
      <p className="muted">{t('comm.rulesIntro')} {t('comm.versionsIntro')}</p>
      <ErrorBox error={error ?? rules.error} />
      {rules.isLoading ? <Spinner label={t('app.loading')} /> : (
        <Card>
          <DataTable rows={rules.data ?? []} rowKey={(r) => r.id} empty={t('comm.none')}
            columns={[
              { key: 'n', header: t('comm.rule'), render: (r) => <span>{pick(r.nameEn, r.nameAr)} <span className="muted">v{r.version}</span> {r.latest ? <Badge tone="primary">{t('comm.latest')}</Badge> : <Badge>{t('comm.older')}</Badge>}</span> },
              { key: 'b', header: t('comm.basis'), render: (r) => <span>{t(`comm.bases.${r.basis}`)} <span className="num">{r.basis === 'fixed' ? fmt(r.amount ?? 0) : `${r.rate}%`}</span>
                {r.minAmount != null && <span className="muted"> · ≥ {fmt(r.minAmount)}</span>}{r.maxAmount != null && <span className="muted"> · ≤ {fmt(r.maxAmount)}</span>}</span> },
              { key: 's', header: t('comm.scope'), render: (r) => scope(r) },
              { key: 'd', header: t('comm.effectiveFrom'), render: (r) => <span className="muted">{formatDate(r.effectiveFrom, lang)}{r.effectiveTo ? ` – ${formatDate(r.effectiveTo, lang)}` : ''}</span> },
              { key: 'st', header: t('app.status'), render: (r) => <span><Badge tone={r.status === 'active' ? 'success' : 'default'}>{t(`comm.${r.status}`)}</Badge> <span className="muted">{t('comm.used', { n: r.used })}</span></span> },
              { key: 'x', header: '', render: (r) => manage && r.latest ? (
                <span className="row">
                  <Button size="sm" onClick={() => { setError(null); setEditing({ rule: r }); }}>{t('comm.newVersion')}</Button>
                  {r.status === 'active' && <Button size="sm" variant="ghost" onClick={() => { setEndOn(today()); setEnding(r); }}>{t('comm.end')}</Button>}
                </span>) : null },
            ]} />
        </Card>
      )}
      <SettingsCard />
      {editing && <RuleModal rule={editing.rule} onClose={() => setEditing(null)} onDone={() => { setEditing(null); void qc.invalidateQueries({ queryKey: ['commissionRules'] }); }} />}
      <Modal open={ending != null} title={`${t('comm.end')} · ${ending ? pick(ending.nameEn, ending.nameAr) : ''}`} onClose={() => setEnding(null)}
        footer={<Button variant="danger" onClick={() => api.endCommissionRule(ending!.ruleKey, endOn).then(() => { setEnding(null); void qc.invalidateQueries({ queryKey: ['commissionRules'] }); }).catch(setError)}>{t('comm.end')}</Button>}>
        <TextField label={t('comm.endOn')} type="date" className="ltr" value={endOn} onChange={(e) => setEndOn(e.target.value)} />
      </Modal>
    </div>
  );
}

function RuleModal({ rule, onClose, onDone }: { rule: CommissionRule | null; onClose: () => void; onDone: () => void }) {
  const api = useApi();
  const { t, pick } = useI18n();
  const techs = useQuery({ queryKey: ['repairTechnicians'], queryFn: () => api.repairTechnicians() });
  const setup = useQuery({ queryKey: ['repairSetup'], queryFn: () => api.repairSetup() });
  const [nameEn, setNameEn] = useState(rule?.nameEn ?? '');
  const [nameAr, setNameAr] = useState(rule?.nameAr ?? '');
  const [basis, setBasis] = useState<CommissionBasisKind>(rule?.basis ?? 'labour');
  const [figure, setFigure] = useState(rule ? String(rule.basis === 'fixed' ? rule.amount ?? '' : rule.rate ?? '') : '');
  const [min, setMin] = useState(rule?.minAmount != null ? String(rule.minAmount) : '');
  const [max, setMax] = useState(rule?.maxAmount != null ? String(rule.maxAmount) : '');
  const [technicianId, setTechnicianId] = useState(rule?.technicianId ?? '');
  const [categoryId, setCategoryId] = useState(rule?.categoryId ?? '');
  const [from, setFrom] = useState(today());
  const [error, setError] = useState<unknown>(null);
  const save = () => {
    const input: CommissionRuleInput = {
      nameEn: nameEn.trim(), nameAr: nameAr.trim(), basis, amount: basis === 'fixed' ? parseAmount(figure) : null, rate: basis === 'fixed' ? null : Number(figure),
      minAmount: min ? parseAmount(min) : null, maxAmount: max ? parseAmount(max) : null, technicianId: technicianId || null, branchId: rule?.branchId ?? null,
      categoryId: categoryId || null, effectiveFrom: from,
    };
    (rule ? api.newCommissionRuleVersion(rule.ruleKey, input) : api.createCommissionRule(input)).then(onDone).catch(setError);
  };
  return (
    <Modal open title={rule ? `${t('comm.newVersion')} · ${pick(rule.nameEn, rule.nameAr)}` : t('comm.newRule')} onClose={onClose}
      footer={<Button variant="primary" disabled={!nameEn.trim() || !nameAr.trim() || figure === ''} onClick={save}>{t('comm.save')}</Button>}>
      <div className="stack">
        <ErrorBox error={error} />
        <TextField label={t('comm.ruleName')} value={nameEn} onChange={(e) => setNameEn(e.target.value)} />
        <TextField label={t('comm.ruleNameAr')} value={nameAr} onChange={(e) => setNameAr(e.target.value)} dir="rtl" />
        <SelectField label={t('comm.basis')} value={basis} onChange={(e) => setBasis(e.target.value as CommissionBasisKind)}>
          {(['labour', 'profit', 'fixed'] as const).map((b) => <option key={b} value={b}>{t(`comm.bases.${b}`)}</option>)}
        </SelectField>
        <TextField label={basis === 'fixed' ? t('comm.fixed') : t('comm.rate')} inputMode="decimal" className="ltr num" value={figure} onChange={(e) => setFigure(e.target.value)} />
        <div className="row">
          <TextField label={t('comm.min')} inputMode="decimal" className="ltr num" value={min} onChange={(e) => setMin(e.target.value)} />
          <TextField label={t('comm.max')} inputMode="decimal" className="ltr num" value={max} onChange={(e) => setMax(e.target.value)} />
        </div>
        <SelectField label={t('comm.technician')} value={technicianId} onChange={(e) => setTechnicianId(e.target.value)}>
          <option value="">{t('comm.anyTechnician')}</option>
          {(techs.data ?? []).map((u) => <option key={u.id} value={u.id}>{pick(u.nameEn, u.nameAr ?? u.nameEn)}</option>)}
        </SelectField>
        <SelectField label={t('comm.categoryScope')} value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
          <option value="">{t('comm.anyCategory')}</option>
          {(setup.data?.categories ?? []).map((c) => <option key={c.id} value={c.id}>{pick(c.nameEn, c.nameAr)}</option>)}
        </SelectField>
        <TextField label={t('comm.effectiveFrom')} type="date" className="ltr" value={from} onChange={(e) => setFrom(e.target.value)} />
      </div>
    </Modal>
  );
}

function SettingsCard() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t } = useI18n();
  const q = useQuery({ queryKey: ['commissionSettings'], queryFn: () => api.commissionSettings() });
  const [s, setS] = useState<CommissionSettings | null>(null);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { if (q.data) setS(q.data); }, [q.data]);
  if (!s) return <ErrorBox error={q.error} />;
  const manage = can(me, 'COMM.RULES');
  return (
    <Card>
      <div className="stack">
        <strong>{t('comm.settings')}</strong>
        <ErrorBox error={error} />
        {saved && <Alert tone="success">{t('comm.saved')}</Alert>}
        <div className="row">
          <SelectField label={t('comm.generateOn')} value={s.generateOn} disabled={!manage} onChange={(e) => setS({ ...s, generateOn: e.target.value as CommissionSettings['generateOn'] })}>
            {(['delivered', 'paid'] as const).map((x) => <option key={x} value={x}>{t(`comm.generateOns.${x}`)}</option>)}
          </SelectField>
          <SelectField label={t('comm.approvalBy')} value={s.approvalBy} disabled={!manage} onChange={(e) => setS({ ...s, approvalBy: e.target.value as CommissionSettings['approvalBy'] })}>
            {(['branch', 'head_office'] as const).map((x) => <option key={x} value={x}>{t(`comm.approvalBys.${x}`)}</option>)}
          </SelectField>
        </div>
        <p className="muted">{t('comm.clawbackIntro')}</p>
        <div className="row">
          <TextField label={t('comm.clawbackDays')} inputMode="numeric" className="ltr num" disabled={!manage} value={String(s.clawbackDays)} onChange={(e) => setS({ ...s, clawbackDays: Number(e.target.value) || 0 })} />
          <TextField label={t('comm.clawbackPercent')} inputMode="decimal" className="ltr num" disabled={!manage} value={String(s.clawbackPercent)} onChange={(e) => setS({ ...s, clawbackPercent: Number(e.target.value) || 0 })} />
        </div>
        {manage && <div><Button variant="primary" onClick={() => { setSaved(false); api.updateCommissionSettings(s).then(() => { setSaved(true); void qc.invalidateQueries({ queryKey: ['commissionSettings'] }); }).catch(setError); }}>{t('app.save')}</Button></div>}
      </div>
    </Card>
  );
}
