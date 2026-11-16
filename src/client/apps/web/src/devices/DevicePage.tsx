import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { can, type DeviceDetail } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Button, Card, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi, useAuth } from '../core';
import { fmt, parseAmount } from '../money';
import { ConditionBadge, Ident, StatusBadge } from './bits';
import { CentralBadge, DeviceIdentityCards, IdentityDialog } from './identity';

/** One device's page (11.6): identity, how its cost was built up, its price, and its whole history with the profit of each sale. */
export function DevicePage() {
  const { id = '' } = useParams();
  const api = useApi();
  const { t, pick, lang } = useI18n();
  const { me } = useAuth();
  const navigate = useNavigate();
  const d = useQuery({ queryKey: ['device', id], queryFn: () => api.device(id) });
  const [dialog, setDialog] = useState<'price' | 'hold' | 'release' | 'writeOff' | 'correct' | 'reverse' | null>(null);
  if (d.isLoading) return <Spinner label={t('app.loading')} />;
  if (!d.data) return <ErrorBox error={d.error} />;
  const { unit: u, cost, price } = d.data;
  const openOrder = d.data.refurbishments.find((r) => r.status === 'open');
  const openHolds = (d.data.holds ?? []).filter((h) => !h.releasedAt);
  const conflictHold = openHolds.some((h) => h.reason === 'imei_conflict');
  const managerHold = openHolds.some((h) => h.reason === 'manager') || (u.status === 'quarantined' && openHolds.length === 0);
  return (
    <div className="stack">
      <PageHeader title={pick(u.descriptionEn, u.descriptionAr)}>
        <Link className="btn btn-ghost" to="/app/devices">{t('devices.back')}</Link>
      </PageHeader>
      <div className="row wrap">
        <StatusBadge status={u.status} /> <ConditionBadge condition={u.condition} grade={u.gradeCode} /> <CentralBadge state={d.data.central?.state} />
        <span>{t('devices.imei')}: <Ident value={u.imei1} /></span>
        {u.imei2 && <span>{t('devices.imei2')}: <Ident value={u.imei2} /></span>}
        {u.serial && <span>{t('devices.serial')}: <Ident value={u.serial} /></span>}
        <span className="muted">{t('devices.received')}: {formatDateTime(u.receivedAt, lang)} · <span className="ltr">{u.branchCode}</span></span>
      </div>
      <div className="row wrap">
        {(u.status === 'under_inspection' || u.status === 'returned_inspection') && can(me, 'DEVICE.INSPECT') &&
          <Button variant="primary" onClick={() => navigate(`/app/devices/${u.id}/inspect`)}>{t('devices.inspect')}</Button>}
        {openOrder && <Button variant="primary" onClick={() => navigate(`/app/refurbishments/${openOrder.id}`)}>{t('devices.openRefurb')}</Button>}
        {can(me, 'DEVICE.PRICE') && !['sold', 'written_off', 'supplier_returned'].includes(u.status) && <Button onClick={() => setDialog('price')}>{t('devices.setPrice')}</Button>}
        {can(me, 'DEVICE.HOLD') && u.status === 'quarantined' && managerHold && <Button onClick={() => setDialog('release')}>{t('devices.release')}</Button>}
        {can(me, 'DEVICE.IMEI.CORRECT') && !['written_off', 'supplier_returned', 'reversed', 'in_transit'].includes(u.status) && <Button onClick={() => setDialog('correct')}>{t('imei.identifiers.correct')}</Button>}
        {can(me, 'DEVICE.WRITE_OFF') && conflictHold && <Button variant="danger" onClick={() => setDialog('reverse')}>{t('imei.identifiers.reverse')}</Button>}
        {can(me, 'DEVICE.HOLD') && !['quarantined', 'sold', 'written_off', 'supplier_returned', 'in_transit'].includes(u.status) && <Button onClick={() => setDialog('hold')}>{t('devices.hold')}</Button>}
        {can(me, 'DEVICE.WRITE_OFF') && (u.status === 'in_stock' || u.status === 'for_parts') && <Button variant="danger" onClick={() => setDialog('writeOff')}>{t('devices.writeOff')}</Button>}
      </div>
      <div className="grid-2">
        <Card>
          <h3>{t('devices.costTitle')}</h3>
          <dl className="figures">
            <dt>{t('devices.acquisition')}</dt><dd className="num">{fmt(cost.acquisition)}</dd>
            <dt>{t('devices.parts')}</dt><dd className="num">{fmt(cost.parts)}</dd>
            <dt>{t('devices.labour')}</dt><dd className="num">{fmt(cost.labour)}</dd>
            <dt>{t('devices.external')}</dt><dd className="num">{fmt(cost.external)}</dd>
            <dt className="strong">{t('devices.total')}</dt><dd className="num strong">{fmt(cost.total)}</dd>
          </dl>
        </Card>
        <Card>
          <h3>{t('devices.priceTitle')}</h3>
          <dl className="figures">
            <dt className="strong">{t('devices.listPrice')}</dt><dd className="num strong">{price.listPrice == null ? t('devices.noPrice') : fmt(price.listPrice)}</dd>
            <dt>{t('devices.ownPrice')}</dt><dd className="num">{fmt(u.sellingPrice)}</dd>
            <dt>{t('devices.suggested')}</dt><dd className="num">{fmt(price.suggested)}{price.gradePercent != null && <span className="muted"> ({price.gradePercent}%)</span>}</dd>
            <dt>{t('devices.newPrice')}</dt><dd className="num">{fmt(price.newPrice)}</dd>
            <dt>{t('devices.warrantyDays')}</dt><dd className="num">{u.warrantyDays ?? u.defaultWarrantyDays}</dd>
          </dl>
        </Card>
      </div>
      <DeviceIdentityCards detail={d.data} />
      <h3>{t('devices.history')}</h3>
      <ol className="timeline">
        {d.data.events.map((e) => (
          <li key={e.seq}>
            <strong>{t(`devices.events.${e.eventType}`)}</strong>
            {e.fromStatus !== e.toStatus && <> · <StatusBadge status={e.toStatus} /></>}
            {e.fromCondition && e.fromCondition !== e.toCondition && <> · <ConditionBadge condition={e.toCondition} grade={e.gradeCode} /></>}
            {e.costDelta !== 0 && <> · <span className={`num${e.costDelta < 0 ? ' neg' : ''}`}>{e.costDelta > 0 ? '+' : ''}{fmt(e.costDelta)}</span></>}
            {e.docNumber && <> · <span className="ltr">{e.docNumber}</span></>}
            <span className="muted"> · {formatDateTime(e.occurredAt, lang)}{e.actorName ? ` · ${e.actorName}` : ''}</span>
          </li>
        ))}
      </ol>
      {d.data.sales.length > 0 && (<>
        <h3>{t('devices.sales')}</h3>
        <DataTable rows={d.data.sales} rowKey={(s) => s.saleId} empty={t('devices.none')}
          columns={[
            { key: 'n', header: t('devices.document'), render: (s) => <span className="ltr">{s.number}</span> },
            { key: 'w', header: t('devices.when'), render: (s) => formatDateTime(s.completedAt, lang) },
            { key: 'v', header: t('devices.net'), render: (s) => <span className="num">{fmt(s.net)}</span> },
            { key: 'k', header: t('devices.cost'), render: (s) => <span className="num">{fmt(s.cost)}</span> },
            { key: 'p', header: t('devices.profit'), render: (s) => s.profit == null ? '—' : <span className={`num${s.profit < 0 ? ' neg' : ''}`}>{fmt(s.profit)}</span> },
          ]} />
      </>)}
      {d.data.inspections.length > 0 && (<>
        <h3>{t('devices.inspections')}</h3>
        <DataTable rows={d.data.inspections} rowKey={(i) => i.id} empty={t('devices.none')}
          columns={[
            { key: 'n', header: t('devices.number'), render: (i) => <span className="ltr">{i.number}</span> },
            { key: 'o', header: t('devices.decision'), render: (i) => t(`devices.outcomes.${i.outcome}`) },
            { key: 'c', header: t('devices.condition'), render: (i) => <ConditionBadge condition={i.resultCondition} grade={i.gradeCode} /> },
            { key: 'b', header: '%', render: (i) => <span className="num">{i.batteryHealth ?? '—'}</span> },
            { key: 'x', header: t('devices.note'), render: (i) => i.note ?? '' },
            { key: 'w', header: t('devices.when'), render: (i) => `${formatDateTime(i.inspectedAt, lang)} · ${i.inspectedBy}` },
          ]} />
      </>)}
      {d.data.refurbishments.length > 0 && (<>
        <h3>{t('devices.refurbishments')}</h3>
        <DataTable rows={d.data.refurbishments} rowKey={(r) => r.id} empty={t('devices.none')}
          columns={[
            { key: 'n', header: t('devices.number'), render: (r) => <Link className="link-button ltr" to={`/app/refurbishments/${r.id}`}>{r.number}</Link> },
            { key: 's', header: t('devices.status'), render: (r) => t(`devices.orderStatus.${r.status}`) },
            { key: 'p', header: t('devices.problem'), render: (r) => r.problem ?? '' },
            { key: 'k', header: t('devices.costs'), render: (r) => <span className="num">{fmt(r.total)}</span> },
          ]} />
      </>)}
      {(dialog === 'correct' || dialog === 'reverse') && <IdentityDialog kind={dialog} detail={d.data} onClose={() => setDialog(null)} />}
      {dialog && dialog !== 'correct' && dialog !== 'reverse' && <DeviceDialog kind={dialog} detail={d.data} onClose={() => setDialog(null)} />}
    </div>
  );
}

function DeviceDialog({ kind, detail, onClose }: { kind: 'price' | 'hold' | 'release' | 'writeOff'; detail: DeviceDetail; onClose: () => void }) {
  const api = useApi();
  const qc = useQueryClient();
  const { t } = useI18n();
  const u = detail.unit;
  const [price, setPrice] = useState(u.sellingPrice == null ? '' : String(u.sellingPrice));
  const [warranty, setWarranty] = useState(String(u.warrantyDays ?? u.defaultWarrantyDays));
  const [reason, setReason] = useState('');
  const [code, setCode] = useState<'damaged' | 'lost' | 'other'>('damaged');
  const save = useMutation({
    mutationFn: async () => {
      if (kind === 'price') return api.setDevicePrice(u.id, { sellingPrice: price.trim() ? parseAmount(price) : null, warrantyDays: Number(warranty) || 0, version: u.version });
      if (kind === 'hold') return api.holdDevice(u.id, reason);
      if (kind === 'release') return api.releaseDevice(u.id, reason);
      return api.writeOffDevice(u.id, { reason, reasonCode: code });
    },
    onSuccess: async () => { await qc.invalidateQueries({ queryKey: ['device', u.id] }); await qc.invalidateQueries({ queryKey: ['devices'] }); onClose(); },
  });
  const title = kind === 'price' ? t('devices.setPrice') : t(`devices.${kind}`);
  return (
    <Modal open title={title} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant={kind === 'writeOff' ? 'danger' : 'primary'} busy={save.isPending} onClick={() => save.mutate()}>{title}</Button></>}>
      <div className="stack">
        {kind === 'price' ? (<>
          <TextField label={t('devices.ownPrice')} help={t('devices.priceHelp')} value={price} onChange={(e) => setPrice(e.target.value)} inputMode="decimal" dir="ltr" />
          <TextField label={t('devices.warrantyDays')} value={warranty} onChange={(e) => setWarranty(e.target.value.replace(/\D/g, ''))} inputMode="numeric" dir="ltr" />
        </>) : (<>
          {kind === 'writeOff' && <Alert tone="warning">{t('devices.writeOffHelp')}</Alert>}
          {kind === 'writeOff' && (
            <SelectField label={t('devices.reason')} value={code} onChange={(e) => setCode(e.target.value as 'damaged' | 'lost' | 'other')}>
              {(['damaged', 'lost', 'other'] as const).map((r) => <option key={r} value={r}>{t(`inventory.reasons.${r}`)}</option>)}
            </SelectField>
          )}
          <TextField label={t('devices.note')} value={reason} onChange={(e) => setReason(e.target.value)} />
        </>)}
        <ErrorBox error={save.error} />
      </div>
    </Modal>
  );
}
