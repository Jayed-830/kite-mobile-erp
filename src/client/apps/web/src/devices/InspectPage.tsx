import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { can, type DeviceCondition, type InspectionOutcome } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { allowedConditions, evaluateInspection } from '@kite/pos';
import { Alert, Button, Card, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, useApi, useAuth } from '../core';
import { fmt, parseAmount } from '../money';
import { ConditionBadge, Ident, StatusBadge } from './bits';

const OUTCOMES: InspectionOutcome[] = ['restock', 'refurbish', 'for_parts', 'supplier_return'];

/**
 * Inspecting a device (12.4): the checklist of its category, answered item by item; the suggestion follows the
 * worst failure, live, by the same rules as the server. Deciding differently needs a note.
 */
export function InspectPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const device = useQuery({ queryKey: ['device', id], queryFn: () => api.device(id) });
  const list = useQuery({ queryKey: ['deviceChecklist', id], queryFn: () => api.deviceChecklist(id) });
  const [answers, setAnswers] = useState<Record<string, string | null>>({});
  const [chosen, setChosen] = useState<InspectionOutcome | null>(null);
  const [condition, setCondition] = useState<DeviceCondition | null>(null);
  const [gradeId, setGradeId] = useState('');
  const [price, setPrice] = useState('');
  const [note, setNote] = useState('');
  const current = device.data?.unit.condition ?? 'used';
  const result = useMemo(() => evaluateInspection(list.data?.items ?? [], answers, current), [list.data, answers, current]);
  const outcome = chosen ?? result.outcome;
  const conditions = allowedConditions(outcome, current);
  const cond = condition && conditions.includes(condition) ? condition : (conditions.includes(result.condition) ? result.condition : conditions[0]!);
  const overriding = outcome !== result.outcome;
  const save = useMutation({
    mutationFn: () => api.inspect(id, {
      answers, outcome, condition: cond, gradeId: gradeId || null, note: note.trim() || null,
      sellingPrice: price.trim() ? parseAmount(price) : null, version: device.data?.unit.version,
    }),
    onSuccess: async (r) => {
      await qc.invalidateQueries({ queryKey: ['device', id] });
      await qc.invalidateQueries({ queryKey: ['inspectionQueue'] });
      navigate(r.refurbOrderId ? `/app/refurbishments/${r.refurbOrderId}` : `/app/devices/${id}`);
    },
  });
  if (device.isLoading || list.isLoading) return <Spinner label={t('app.loading')} />;
  if (!device.data || !list.data) return <ErrorBox error={device.error ?? list.error} />;
  const u = device.data.unit;
  const label = (code: string) => { const i = list.data!.items.find((x) => x.code === code); return i ? pick(i.labelEn, i.labelAr) : code; };
  return (
    <div className="stack">
      <PageHeader title={t('devices.inspectTitle', { item: pick(u.descriptionEn, u.descriptionAr) })}>
        <Link className="btn btn-ghost" to={`/app/devices/${u.id}`}><Ident value={u.imei1 ?? u.serial} /></Link>
      </PageHeader>
      <div className="row wrap"><StatusBadge status={u.status} /><ConditionBadge condition={u.condition} grade={u.gradeCode} /><span>{t('devices.costSoFar')}: <span className="num">{fmt(device.data.cost.total)}</span></span></div>
      <Card>
        <h3>{t('devices.checklist')}</h3>
        <div className="checklist">
          {list.data.items.map((i) => (
            <div key={i.code} className="checklist-item">
              <span className="grow">{pick(i.labelEn, i.labelAr)}</span>
              {i.kind === 'pass_fail' && (
                <div className="segmented" role="group" aria-label={pick(i.labelEn, i.labelAr)}>
                  {(['pass', 'fail'] as const).map((v) => (
                    <button key={v} type="button" aria-pressed={answers[i.code] === v} className={`seg${answers[i.code] === v ? ` active ${v}` : ''}`}
                      onClick={() => setAnswers({ ...answers, [i.code]: v })}>{t(`devices.${v}`)}</button>
                  ))}
                </div>
              )}
              {i.kind === 'percent' && <TextField label={`%${i.minPercent != null ? ` (≥ ${i.minPercent})` : ''}`} value={answers[i.code] ?? ''} inputMode="numeric" dir="ltr"
                onChange={(e) => setAnswers({ ...answers, [i.code]: e.target.value.replace(/\D/g, '').slice(0, 3) || null })} />}
              {i.kind === 'text' && <TextField label={t('devices.note')} value={answers[i.code] ?? ''} onChange={(e) => setAnswers({ ...answers, [i.code]: e.target.value || null })} />}
            </div>
          ))}
        </div>
      </Card>
      {result.complete
        ? <Alert tone={result.outcome === 'restock' ? 'success' : 'warning'}>
            {t('devices.suggestion', { outcome: t(`devices.outcomes.${result.outcome}`) })}
            {result.failed.length > 0 && <> — {t('devices.failedItems', { items: result.failed.map(label).join(lang === 'ar' ? '، ' : '; ') })}</>}
          </Alert>
        : <Alert tone="info">{t('devices.incomplete')}</Alert>}
      <Card>
        <div className="grid-2">
          <SelectField label={t('devices.decision')} value={outcome} onChange={(e) => setChosen(e.target.value as InspectionOutcome)}>
            {OUTCOMES.map((o) => <option key={o} value={o}>{t(`devices.outcomes.${o}`)}</option>)}
          </SelectField>
          <SelectField label={t('devices.resultCondition')} value={cond} onChange={(e) => setCondition(e.target.value as DeviceCondition)}>
            {conditions.map((c) => <option key={c} value={c}>{t(`devices.conditions.${c}`)}</option>)}
          </SelectField>
          {outcome !== 'for_parts' && (
            <SelectField label={t('devices.grade')} value={gradeId} onChange={(e) => setGradeId(e.target.value)}>
              <option value="">{t('devices.noGrade')}</option>
              {list.data.grades.map((g) => <option key={g.id} value={g.id}>{g.code} · {pick(g.nameEn, g.nameAr)} ({g.pricePercent}%)</option>)}
            </SelectField>
          )}
          {outcome === 'restock' && can(me, 'DEVICE.PRICE') && (
            <TextField label={t('devices.ownPrice')} help={t('devices.priceHelp')} value={price} onChange={(e) => setPrice(e.target.value)} inputMode="decimal" dir="ltr" />
          )}
        </div>
        <TextField label={t('devices.note')} value={note} onChange={(e) => setNote(e.target.value)} error={overriding && !note.trim() ? t('devices.noteRequired') : undefined} />
        <ErrorBox error={save.error} />
        <div className="row"><Button variant="primary" disabled={!result.complete || (overriding && !note.trim())} busy={save.isPending} onClick={() => save.mutate()}>{t('devices.save')}</Button></div>
      </Card>
    </div>
  );
}
