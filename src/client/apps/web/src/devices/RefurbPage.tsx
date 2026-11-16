import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import type { PosItem, RefurbCost } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { labourCost } from '@kite/pos';
import { Badge, Button, Card, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi } from '../core';
import { fmt, parseAmount } from '../money';
import { ItemPicker } from '../pages/Inventory';
import { ConditionBadge, Ident, StatusBadge } from './bits';

type Kind = 'part' | 'labour' | 'external';

/**
 * A refurbishment order (12.6): every part, minute of labour and piece of outside work is added to this one device's
 * cost the moment it is recorded (12.2). A mistake is taken back while the order is open; completion puts the device
 * back in stock, refurbished, with its grade and price.
 */
export function RefurbPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const qc = useQueryClient();
  const { t, pick, lang } = useI18n();
  const order = useQuery({ queryKey: ['refurbishment', id], queryFn: () => api.refurbishment(id) });
  const settings = useQuery({ queryKey: ['deviceSettings'], queryFn: () => api.deviceSettings() });
  const [kind, setKind] = useState<Kind>('part');
  const [part, setPart] = useState<PosItem | null>(null);
  const [qty, setQty] = useState('1');
  const [minutes, setMinutes] = useState('');
  const [vendor, setVendor] = useState('');
  const [reference, setReference] = useState('');
  const [amount, setAmount] = useState('');
  const [reversing, setReversing] = useState<RefurbCost | null>(null);
  const [completing, setCompleting] = useState(false);
  const refresh = async () => { await qc.invalidateQueries({ queryKey: ['refurbishment', id] }); await qc.invalidateQueries({ queryKey: ['refurbishments'] }); };
  const add = useMutation({
    mutationFn: () => api.addRefurbCost(id, kind === 'part' ? { kind, variantId: part?.variantId, qty: Number(qty) }
      : kind === 'labour' ? { kind, minutes: Number(minutes) } : { kind, vendor, reference: reference || null, amount: parseAmount(amount) ?? 0 }),
    onSuccess: async () => { setPart(null); setQty('1'); setMinutes(''); setVendor(''); setReference(''); setAmount(''); await refresh(); },
  });
  if (order.isLoading) return <Spinner label={t('app.loading')} />;
  if (!order.data) return <ErrorBox error={order.error} />;
  const o = order.data.order;
  const open = o.status === 'open';
  const rate = settings.data?.labourRatePerHour ?? 0;
  const labour = minutes && Number(minutes) > 0 ? labourCost(Number(minutes), rate) : null;
  return (
    <div className="stack">
      <PageHeader title={t('devices.refurbTitle', { number: o.number })}>
        <Link className="btn btn-ghost" to={`/app/devices/${o.unitId}`}><Ident value={o.identifier} /></Link>
      </PageHeader>
      <div className="row wrap">
        <strong>{pick(o.descriptionEn, o.descriptionAr)}</strong><StatusBadge status={o.unitStatus} /><ConditionBadge condition={o.unitCondition} grade={o.gradeCode} />
        <Badge tone={open ? 'warning' : 'success'}>{t(`devices.orderStatus.${o.status}`)}</Badge>
        <span>{t('devices.costSoFar')}: <strong className="num">{fmt(o.unitCost)}</strong></span>
      </div>
      {o.problem && <p><strong>{t('devices.problem')}:</strong> {o.problem}</p>}
      <h3>{t('devices.costs')}</h3>
      <DataTable rows={order.data.costs} rowKey={(c) => c.id} empty={t('devices.noCosts')}
        columns={[
          { key: 'k', header: t('devices.item'), render: (c) => <span>{t(`devices.kinds.${c.kind}`)}{c.reversalOf ? ` · ${t('devices.reversal', { note: c.note ?? '' })}` : ''}</span> },
          { key: 'd', header: '', render: (c) => c.kind === 'part' ? <span>{pick(c.partDescriptionEn ?? '', c.partDescriptionAr ?? '')} × <span className="num">{c.qty}</span></span>
            : c.kind === 'labour' ? <span className="ltr">{c.minutes} min{c.technicianName ? ` · ${c.technicianName}` : ''}</span> : <span>{c.vendor}{c.reference ? <span className="ltr muted"> {c.reference}</span> : null}</span> },
          { key: 'a', header: t('devices.amount'), render: (c) => <span className={`num${c.amount < 0 ? ' neg' : ''}`}>{fmt(c.amount)}</span> },
          { key: 'w', header: t('devices.when'), render: (c) => formatDateTime(c.createdAt, lang) },
          { key: 'x', header: '', render: (c) => c.reversed ? <Badge>{t('devices.reversed')}</Badge> : open && !c.reversalOf ? <Button size="sm" variant="ghost" onClick={() => setReversing(c)}>{t('devices.reverse')}</Button> : null },
        ]} />
      {open && (
        <Card>
          <h3>{t('devices.addCost')}</h3>
          <div className="tabs" role="tablist">
            {(['part', 'labour', 'external'] as Kind[]).map((k) => <button key={k} role="tab" aria-selected={kind === k} className={`tab${kind === k ? ' active' : ''}`} onClick={() => setKind(k)}>{t(`devices.kinds.${k}`)}</button>)}
          </div>
          {kind === 'part' && (
            <div className="stack">
              {part ? <div className="row"><strong className="grow">{pick(part.descriptionEn, part.descriptionAr)} <span className="ltr muted">{part.sku}</span></strong><Button size="sm" variant="ghost" onClick={() => setPart(null)}>{t('devices.remove')}</Button></div>
                : <ItemPicker label={t('devices.part')} onPick={(i) => setPart(i)} />}
              <TextField label={t('devices.qty')} value={qty} onChange={(e) => setQty(e.target.value)} inputMode="decimal" dir="ltr" />
            </div>
          )}
          {kind === 'labour' && (
            <div className="stack">
              <TextField label={t('devices.minutes')} help={t('devices.ratePerHour', { rate: fmt(rate) })} value={minutes} onChange={(e) => setMinutes(e.target.value.replace(/\D/g, ''))} inputMode="numeric" dir="ltr" />
              {labour && <span className="muted">{t('devices.adds', { amount: fmt(Number(labour)) })}</span>}
            </div>
          )}
          {kind === 'external' && (
            <div className="grid-2">
              <TextField label={t('devices.vendor')} value={vendor} onChange={(e) => setVendor(e.target.value)} />
              <TextField label={t('devices.reference')} value={reference} onChange={(e) => setReference(e.target.value)} dir="ltr" />
              <TextField label={t('devices.amount')} value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" dir="ltr" />
            </div>
          )}
          <ErrorBox error={add.error} />
          <div className="row">
            <Button variant="primary" busy={add.isPending} disabled={(kind === 'part' && !part) || (kind === 'labour' && !labour) || (kind === 'external' && (!vendor.trim() || parseAmount(amount) == null))}
              onClick={() => add.mutate()}>{t('devices.add')}</Button>
            <span className="spacer" />
            <Button onClick={() => setCompleting(true)}>{t('devices.complete')}</Button>
          </div>
        </Card>
      )}
      {reversing && <ReverseDialog orderId={id} cost={reversing} onClose={async () => { setReversing(null); await refresh(); }} />}
      {completing && <CompleteDialog orderId={id} unitId={o.unitId} version={o.version} onClose={async () => { setCompleting(false); await refresh(); }} />}
    </div>
  );
}

function ReverseDialog({ orderId, cost, onClose }: { orderId: string; cost: RefurbCost; onClose: () => void }) {
  const api = useApi();
  const { t } = useI18n();
  const [reason, setReason] = useState('');
  const go = useMutation({ mutationFn: () => api.reverseRefurbCost(orderId, cost.id, reason), onSuccess: onClose });
  return (
    <Modal open title={t('devices.reverse')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="danger" disabled={!reason.trim()} busy={go.isPending} onClick={() => go.mutate()}>{t('devices.reverse')}</Button></>}>
      <div className="stack">
        <p>{t(`devices.kinds.${cost.kind}`)} · <span className="num">{fmt(cost.amount)}</span></p>
        <TextField label={t('devices.reverseReason')} value={reason} onChange={(e) => setReason(e.target.value)} />
        <ErrorBox error={go.error} />
      </div>
    </Modal>
  );
}

function CompleteDialog({ orderId, unitId, version, onClose }: { orderId: string; unitId: string; version: number; onClose: () => void }) {
  const api = useApi();
  const qc = useQueryClient();
  const { t, pick } = useI18n();
  const list = useQuery({ queryKey: ['deviceChecklist', unitId], queryFn: () => api.deviceChecklist(unitId) });
  const [condition, setCondition] = useState<'refurbished' | 'damaged'>('refurbished');
  const [gradeId, setGradeId] = useState('');
  const [workDone, setWorkDone] = useState('');
  const [warranty, setWarranty] = useState('90');
  const go = useMutation({
    mutationFn: () => api.completeRefurbishment(orderId, { condition, gradeId: gradeId || null, warrantyDays: Number(warranty) || 0, workDone: workDone || null, version }),
    onSuccess: async () => { await qc.invalidateQueries({ queryKey: ['device', unitId] }); onClose(); },
  });
  return (
    <Modal open title={t('devices.completeTitle')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" busy={go.isPending} onClick={() => go.mutate()}>{t('devices.complete')}</Button></>}>
      <div className="stack">
        <SelectField label={t('devices.comeBackAs')} value={condition} onChange={(e) => setCondition(e.target.value as 'refurbished' | 'damaged')}>
          <option value="refurbished">{t('devices.conditions.refurbished')}</option>
          <option value="damaged">{t('devices.conditions.damaged')}</option>
        </SelectField>
        <SelectField label={t('devices.grade')} value={gradeId} onChange={(e) => setGradeId(e.target.value)}>
          <option value="">{t('devices.noGrade')}</option>
          {(list.data?.grades ?? []).map((g) => <option key={g.id} value={g.id}>{g.code} · {pick(g.nameEn, g.nameAr)} ({g.pricePercent}%)</option>)}
        </SelectField>
        <TextField label={t('devices.warrantyDays')} value={warranty} onChange={(e) => setWarranty(e.target.value.replace(/\D/g, ''))} inputMode="numeric" dir="ltr" />
        <TextField label={t('devices.workDone')} value={workDone} onChange={(e) => setWorkDone(e.target.value)} />
        <ErrorBox error={go.error} />
      </div>
    </Modal>
  );
}
