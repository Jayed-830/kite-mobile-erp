import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { PurchPolicy } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Button, Card, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, useApi, useAuth, workBranchId } from '../core';
import { BranchSelect } from './bits';

/** Purchasing settings (16.2): match tolerances, receiving over the order, approval limits by role and branch, branch supplier lists. */
export function PurchSettingsPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const q = useQuery({ queryKey: ['purchSettings'], queryFn: () => api.purchSettings() });
  const [p, setP] = useState<PurchPolicy | null>(null);
  const [limits, setLimits] = useState<Record<string, string>>({});
  const [branch, setBranch] = useState(workBranchId(me));
  const [msg, setMsg] = useState<string | null>(null);
  const sup = useQuery({ queryKey: ['branchSuppliers', branch], queryFn: () => api.branchSuppliers(branch), enabled: !!branch });
  const [chosen, setChosen] = useState<string[]>([]);
  useEffect(() => { if (q.data) { setP(q.data.policy); setLimits(Object.fromEntries(q.data.limits.filter((l) => !l.branchId).map((l) => [l.roleId, l.maxAmount == null ? '' : String(l.maxAmount)]))); } }, [q.data]);
  useEffect(() => { if (sup.data) setChosen(sup.data.filter((s) => s.approved).map((s) => s.id)); }, [sup.data]);
  const done = (m: string) => { setMsg(m); void qc.invalidateQueries({ queryKey: ['purchSettings'] }); };
  const savePolicy = useMutation({ mutationFn: () => api.updatePurchPolicy(p!), onSuccess: () => done(t('purch.settingsSaved')) });
  const saveLimits = useMutation({
    mutationFn: () => api.updateApprovalLimits([
      ...Object.entries(limits).map(([roleId, v]) => ({ roleId, branchId: null, maxAmount: v.trim() === '' ? null : Number(v) })),
      ...(q.data?.limits ?? []).filter((l) => l.branchId).map((l) => ({ roleId: l.roleId, branchId: l.branchId, maxAmount: l.maxAmount })),
    ]),
    onSuccess: () => done(t('purch.settingsSaved')),
  });
  const saveList = useMutation({ mutationFn: () => api.setBranchSuppliers(branch, chosen), onSuccess: () => done(t('purch.settingsSaved')) });
  if (q.isLoading || !p) return <Spinner label={t('app.loading')} />;
  const num = (v: string) => Number(v.replace(/[^\d.]/g, '') || 0);
  return (
    <div className="stack">
      <PageHeader title={t('purch.settingsTitle')} />
      {msg && <Alert tone="success">{msg}</Alert>}
      <ErrorBox error={q.error ?? savePolicy.error ?? saveLimits.error ?? saveList.error} />
      <Card>
        <h2>{t('purch.matching')}</h2>
        <div className="grid-2">
          <TextField label={t('purch.tolerancePct')} value={String(p.priceTolerancePct)} onChange={(e) => setP({ ...p, priceTolerancePct: num(e.target.value) })} inputMode="decimal" dir="ltr" />
          <TextField label={t('purch.toleranceAmount')} value={String(p.priceToleranceAmount)} onChange={(e) => setP({ ...p, priceToleranceAmount: num(e.target.value) })} inputMode="decimal" dir="ltr" />
          <TextField label={t('purch.overReceipt')} value={String(p.overReceiptPct)} onChange={(e) => setP({ ...p, overReceiptPct: num(e.target.value) })} inputMode="decimal" dir="ltr" help={t('purch.overReceiptHelp')} />
          <SelectField label={t('purch.supplierList')} value={p.supplierList} onChange={(e) => setP({ ...p, supplierList: e.target.value as 'all' | 'approved' })}>
            <option value="all">{t('purch.listAll')}</option><option value="approved">{t('purch.listApproved')}</option>
          </SelectField>
        </div>
        <p className="muted">{t('purch.matchingHelp')}</p>
        <div className="row"><Button variant="primary" busy={savePolicy.isPending} onClick={() => savePolicy.mutate()}>{t('app.save')}</Button></div>
      </Card>
      <Card>
        <h2>{t('purch.approvalLimits')}</h2>
        <p className="muted">{t('purch.limitsHelp')}</p>
        <div className="grid-2">
          {(q.data?.roles ?? []).filter((r) => limits[r.id] !== undefined).map((r) => (
            <TextField key={r.id} label={pick(r.nameEn, r.nameAr)} value={limits[r.id] ?? ''} onChange={(e) => setLimits({ ...limits, [r.id]: e.target.value })} inputMode="decimal" dir="ltr" placeholder={t('purch.noLimit')} />
          ))}
        </div>
        <SelectField label={t('purch.addRole')} value="" onChange={(e) => e.target.value && setLimits({ ...limits, [e.target.value]: '0' })}>
          <option value="">—</option>
          {(q.data?.roles ?? []).filter((r) => limits[r.id] === undefined).map((r) => <option key={r.id} value={r.id}>{pick(r.nameEn, r.nameAr)}</option>)}
        </SelectField>
        <div className="row"><Button variant="primary" busy={saveLimits.isPending} onClick={() => saveLimits.mutate()}>{t('app.save')}</Button></div>
      </Card>
      <Card>
        <h2>{t('purch.branchLists')}</h2>
        <p className="muted">{p.supplierList === 'approved' ? t('purch.branchListsOn') : t('purch.branchListsOff')}</p>
        <BranchSelect value={branch} onChange={setBranch} />
        <div className="stack">
          {(sup.data ?? []).map((s) => (
            <label key={s.id} className="row"><input type="checkbox" checked={chosen.includes(s.id)} onChange={(e) => setChosen(e.target.checked ? [...chosen, s.id] : chosen.filter((x) => x !== s.id))} />
              {pick(s.nameEn, s.nameAr)} <span className="ltr muted">{s.number}</span></label>
          ))}
        </div>
        <div className="row"><Button variant="primary" busy={saveList.isPending} onClick={() => saveList.mutate()}>{t('app.save')}</Button></div>
      </Card>
    </div>
  );
}
