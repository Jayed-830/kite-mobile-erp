import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type DeductionInput, type DeductionRow } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, Checkbox, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDate, useApi, useAuth } from '../core';
import { fmt, parseAmount } from '../money';

/** Trade-in settings (12.4): staff limits and the seller's declaration, the price guide per model and grade, and the deductions. */
export function TradeInSettingsPage() {
  const { t } = useI18n();
  return (
    <div className="stack">
      <PageHeader title={t('tradein.settings.title')} />
      <LimitsCard />
      <GuideCard />
      <DeductionsCard />
    </div>
  );
}

function LimitsCard() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t } = useI18n();
  const s = useQuery({ queryKey: ['tradeInSettings'], queryFn: () => api.tradeInSettings() });
  const [v, setV] = useState({ staffLimit: '', overGuidePercent: '', requireCivilId: true, declarationEn: '', declarationAr: '' });
  useEffect(() => { if (s.data) setV({ staffLimit: s.data.staffLimit.toFixed(3), overGuidePercent: String(s.data.overGuidePercent), requireCivilId: s.data.requireCivilId, declarationEn: s.data.declarationEn, declarationAr: s.data.declarationAr }); }, [s.data]);
  const save = useMutation({
    mutationFn: () => api.updateTradeInSettings({ staffLimit: parseAmount(v.staffLimit) ?? 0, overGuidePercent: Number(v.overGuidePercent), requireCivilId: v.requireCivilId,
      declarationEn: v.declarationEn, declarationAr: v.declarationAr, version: s.data!.version }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['tradeInSettings'] }),
  });
  const editable = can(me, 'TRADEIN.SETTINGS');
  if (s.isLoading) return <Spinner label={t('app.loading')} />;
  return (
    <Card><div className="stack">
      <h3>{t('tradein.settings.limits')}</h3>
      <div className="grid-2">
        <TextField label={t('tradein.settings.staffLimit')} value={v.staffLimit} onChange={(e) => setV({ ...v, staffLimit: e.target.value })} inputMode="decimal" dir="ltr" disabled={!editable} />
        <TextField label={t('tradein.settings.overGuide')} value={v.overGuidePercent} onChange={(e) => setV({ ...v, overGuidePercent: e.target.value.replace(/[^\d.]/g, '') })} inputMode="decimal" dir="ltr" disabled={!editable} />
      </div>
      <Checkbox label={t('tradein.settings.requireCivilId')} checked={v.requireCivilId} onChange={(e) => setV({ ...v, requireCivilId: e.target.checked })} disabled={!editable} />
      <h3>{t('tradein.settings.declaration')}</h3>
      <div className="field"><label htmlFor="decl-en">{t('tradein.settings.declarationEn')}</label>
        <textarea id="decl-en" className="input" rows={3} value={v.declarationEn} onChange={(e) => setV({ ...v, declarationEn: e.target.value })} disabled={!editable} /></div>
      <div className="field"><label htmlFor="decl-ar">{t('tradein.settings.declarationAr')}</label>
        <textarea id="decl-ar" className="input" rows={3} dir="rtl" value={v.declarationAr} onChange={(e) => setV({ ...v, declarationAr: e.target.value })} disabled={!editable} /></div>
      <ErrorBox error={s.error ?? save.error} />
      {save.isSuccess && <Alert tone="success">{t('tradein.settings.saved')}</Alert>}
      {editable && <div className="row"><Button variant="primary" busy={save.isPending} onClick={() => save.mutate()}>{t('app.save')}</Button></div>}
    </div></Card>
  );
}

function GuideCard() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const phones = useQuery({ queryKey: ['products', 'device'], queryFn: () => api.products(undefined, 'device') });
  const grades = useQuery({ queryKey: ['grades'], queryFn: () => api.grades() });
  const guide = useQuery({ queryKey: ['priceGuide'], queryFn: () => api.priceGuide() });
  const [edits, setEdits] = useState<Record<string, string>>({});
  const variants = useMemo(() => (phones.data ?? []).filter((p) => p.product.productType === 'device' && p.product.tracking !== 'none')
    .flatMap((p) => p.variants.map((v) => ({ id: v.variant.id, sku: v.variant.sku, name: pick(p.product.nameEn, p.product.nameAr) }))), [phones.data, pick]);
  const active = (grades.data ?? []).filter((g) => g.status === 'active' && !g.categoryId);
  const key = (v: string, g: string) => `${v}|${g}`;
  const current = (v: string, g: string) => guide.data?.find((x) => x.variantId === v && x.gradeId === g)?.baseValue ?? null;
  const save = useMutation({
    mutationFn: () => api.savePriceGuide(Object.entries(edits).map(([k, val]) => { const [variantId, gradeId] = k.split('|'); return { variantId: variantId!, gradeId: gradeId!, baseValue: val.trim() ? parseAmount(val) : null }; })),
    onSuccess: async () => { setEdits({}); await qc.invalidateQueries({ queryKey: ['priceGuide'] }); },
  });
  const editable = can(me, 'TRADEIN.SETTINGS');
  const bad = Object.values(edits).some((x) => x.trim() && parseAmount(x) == null);
  return (
    <Card><div className="stack">
      <h3>{t('tradein.settings.guide')}</h3>
      <p className="muted">{t('tradein.settings.guideHelp')}</p>
      <ErrorBox error={phones.error ?? grades.error ?? guide.error ?? save.error} />
      <div className="table-wrap">
        <table className="table">
          <thead><tr><th>{t('tradein.settings.guideModel')}</th>{active.map((g) => <th key={g.id}>{g.code} · {pick(g.nameEn, g.nameAr)}</th>)}</tr></thead>
          <tbody>
            {variants.map((v) => (
              <tr key={v.id}>
                <td>{v.name} <span className="ltr muted">{v.sku}</span></td>
                {active.map((g) => (
                  <td key={g.id}>
                    <input className="input num" aria-label={`${v.sku} ${g.code}`} dir="ltr" inputMode="decimal" disabled={!editable} placeholder="—"
                      value={edits[key(v.id, g.id)] ?? (current(v.id, g.id) == null ? '' : current(v.id, g.id)!.toFixed(3))}
                      onChange={(e) => setEdits({ ...edits, [key(v.id, g.id)]: e.target.value })} />
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {editable && <div className="row"><Button variant="primary" disabled={Object.keys(edits).length === 0 || bad} busy={save.isPending} onClick={() => save.mutate()}>{t('tradein.settings.saveGuide')}</Button></div>}
    </div></Card>
  );
}

function DeductionsCard() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const rows = useQuery({ queryKey: ['deductions'], queryFn: () => api.deductions() });
  const checklists = useQuery({ queryKey: ['checklists'], queryFn: () => api.checklists() });
  const [edit, setEdit] = useState<(DeductionInput & { id?: string }) | null>(null);
  const save = useMutation({
    mutationFn: (d: DeductionInput & { id?: string }) => (d.id ? api.updateDeduction(d.id, d) : api.createDeduction(d).then(() => undefined)),
    onSuccess: async () => { setEdit(null); await qc.invalidateQueries({ queryKey: ['deductions'] }); },
  });
  const items = checklists.data?.[0]?.items ?? [];
  const toInput = (d: DeductionRow): DeductionInput & { id: string } => ({ id: d.id, code: d.code, nameEn: d.nameEn, nameAr: d.nameAr, kind: d.kind, value: d.value, checklistCode: d.checklistCode, sort: d.sort, status: d.status, version: d.version });
  const editable = can(me, 'TRADEIN.SETTINGS');
  return (
    <Card>
      <div className="row"><h3 className="grow">{t('tradein.settings.deductionsTitle')}</h3>
        {editable && <Button onClick={() => { save.reset(); setEdit({ code: '', nameEn: '', nameAr: '', kind: 'amount', value: 0 }); }}>{t('tradein.settings.add')}</Button>}</div>
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(d) => d.id} empty={t('app.none')}
        columns={[
          { key: 'c', header: t('tradein.settings.code'), render: (d) => <span className="ltr">{d.code}</span> },
          { key: 'n', header: t('accounts.name'), render: (d) => pick(d.nameEn, d.nameAr) },
          { key: 'v', header: t('tradein.settings.value'), render: (d) => <span className="num">{d.kind === 'percent' ? `${d.value}%` : fmt(d.value)}</span> },
          { key: 'k', header: t('tradein.settings.checklist'), render: (d) => { const i = items.find((x) => x.code === d.checklistCode); return i ? pick(i.labelEn, i.labelAr) : <span className="muted">{t('tradein.settings.none')}</span>; } },
          { key: 's', header: t('app.status'), render: (d) => <Badge tone={d.status === 'active' ? 'success' : 'default'}>{t(`app.${d.status}`)}</Badge> },
          { key: 'a', header: '', render: (d) => editable && <Button size="sm" onClick={() => { save.reset(); setEdit(toInput(d)); }}>{t('app.edit')}</Button> },
        ]} />
      {edit && (
        <Modal open title={edit.id ? t('app.edit') : t('tradein.settings.add')} onClose={() => setEdit(null)}
          footer={<><Button onClick={() => setEdit(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={save.isPending} onClick={() => save.mutate(edit)}>{t('app.save')}</Button></>}>
          <div className="grid-2">
            <TextField label={t('tradein.settings.code')} value={edit.code} onChange={(e) => setEdit({ ...edit, code: e.target.value.toUpperCase() })} dir="ltr" maxLength={20} />
            <SelectField label={t('tradein.settings.kind')} value={edit.kind} onChange={(e) => setEdit({ ...edit, kind: e.target.value as 'amount' | 'percent' })}>
              <option value="amount">{t('tradein.settings.kinds.amount')}</option><option value="percent">{t('tradein.settings.kinds.percent')}</option>
            </SelectField>
          </div>
          <div className="grid-2">
            <TextField label={t('tradein.settings.nameEn')} value={edit.nameEn} onChange={(e) => setEdit({ ...edit, nameEn: e.target.value })} />
            <TextField label={t('tradein.settings.nameAr')} value={edit.nameAr} onChange={(e) => setEdit({ ...edit, nameAr: e.target.value })} dir="rtl" />
          </div>
          <div className="grid-2">
            <TextField label={t('tradein.settings.value')} value={String(edit.value)} onChange={(e) => setEdit({ ...edit, value: Number(e.target.value.replace(/[^\d.]/g, '')) || 0 })} inputMode="decimal" dir="ltr" />
            <SelectField label={t('tradein.settings.checklist')} value={edit.checklistCode ?? ''} onChange={(e) => setEdit({ ...edit, checklistCode: e.target.value || null })}>
              <option value="">{t('tradein.settings.none')}</option>
              {items.map((i) => <option key={i.code} value={i.code}>{pick(i.labelEn, i.labelAr)}</option>)}
            </SelectField>
          </div>
          {edit.id && <Checkbox label={t('tradein.settings.active')} checked={edit.status !== 'inactive'} onChange={(e) => setEdit({ ...edit, status: e.target.checked ? 'active' : 'inactive' })} />}
          <ErrorBox error={save.error} />
        </Modal>
      )}
    </Card>
  );
}

/** The Tax Authority's approval of the profit margin scheme (12.3): VAT-MARGIN is active only while it is recorded. */
export function MarginSchemeCard() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const m = useQuery({ queryKey: ['marginScheme'], queryFn: () => api.marginScheme() });
  const [v, setV] = useState({ approved: false, reference: '', approvedOn: '', note: '' });
  useEffect(() => { if (m.data) setV({ approved: m.data.approved, reference: m.data.reference ?? '', approvedOn: m.data.approvedOn ?? '', note: m.data.note ?? '' }); }, [m.data]);
  const save = useMutation({
    mutationFn: (approved: boolean) => api.updateMarginScheme({ approved, reference: v.reference || null, approvedOn: v.approvedOn || null, note: v.note || null, version: m.data!.version }),
    onSuccess: async () => { await qc.invalidateQueries({ queryKey: ['marginScheme'] }); await qc.invalidateQueries({ queryKey: ['tax'] }); },
  });
  if (!m.data) return <ErrorBox error={m.error} />;
  const editable = can(me, 'TAX.MANAGE');
  return (
    <Card><div className="stack">
      <h3>{t('tradein.margin.title')}</h3>
      <p className="muted">{t('tradein.margin.help')}</p>
      <p>{t('tradein.margin.status', { status: t(`tradein.margin.statuses.${m.data.taxCodeStatus}`) })}{m.data.approved && m.data.approvedOn && <> · {m.data.reference} · {formatDate(m.data.approvedOn, lang)}</>}</p>
      {editable && (
        <>
          <div className="grid-2">
            <TextField label={t('tradein.margin.reference')} value={v.reference} onChange={(e) => setV({ ...v, reference: e.target.value })} dir="ltr" />
            <TextField label={t('tradein.margin.approvedOn')} type="date" value={v.approvedOn} onChange={(e) => setV({ ...v, approvedOn: e.target.value })} dir="ltr" />
          </div>
          <TextField label={t('tradein.margin.note')} value={v.note} onChange={(e) => setV({ ...v, note: e.target.value })} />
          <ErrorBox error={save.error} />
          {save.isSuccess && <Alert tone="success">{t('tradein.margin.saved')}</Alert>}
          <div className="row">
            <Button variant="primary" busy={save.isPending} disabled={!v.reference.trim() || !v.approvedOn} onClick={() => save.mutate(true)}>{t('tradein.margin.approved')}</Button>
            {m.data.approved && <Button variant="danger" busy={save.isPending} onClick={() => save.mutate(false)}>{t('tradein.margin.withdraw')}</Button>}
          </div>
        </>
      )}
    </div></Card>
  );
}

/** A customer's store credit: balance and every movement (14.6). */
export function StoreCreditButton({ customerId }: { customerId: string }) {
  const api = useApi();
  const { t, lang } = useI18n();
  const [open, setOpen] = useState(false);
  const sc = useQuery({ queryKey: ['storeCredit', customerId], queryFn: () => api.storeCredit(customerId), enabled: open });
  return (
    <>
      <Button size="sm" onClick={() => setOpen(true)}>{t('tradein.storeCredit.view')}</Button>
      {open && (
        <Modal open title={t('tradein.storeCredit.title')} onClose={() => setOpen(false)} footer={<Button onClick={() => setOpen(false)}>{t('app.close')}</Button>}>
          <ErrorBox error={sc.error} />
          {sc.data && (
            <>
              <div className="pay-total"><span>{t('tradein.storeCredit.balance')}</span><strong className="num">{fmt(sc.data.balance)}</strong></div>
              {sc.data.lines.length === 0 ? <p className="muted">{t('tradein.storeCredit.none')}</p> : (
                <div className="figures">
                  {sc.data.lines.map((l) => <div key={l.id} className="fig"><span>{l.note ?? l.documentNumber} <span className="muted">{formatDate(l.createdAt, lang)}</span></span><span className={`num${l.amount < 0 ? ' neg' : ''}`}>{fmt(l.amount)}</span></div>)}
                </div>
              )}
            </>
          )}
        </Modal>
      )}
    </>
  );
}
