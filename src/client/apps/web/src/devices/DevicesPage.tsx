import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { can, type DeviceCondition, type DeviceStatus } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { DataTable, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDate, useApi, useAuth } from '../core';
import { fmt } from '../money';
import { ConditionBadge, Ident, StatusBadge } from './bits';

type Tab = 'stock' | 'inspections' | 'refurbishment' | 'reports';
const STATUSES: DeviceStatus[] = ['in_stock', 'under_inspection', 'returned_inspection', 'refurbishing', 'for_parts', 'quarantined', 'sold', 'written_off'];
const CONDITIONS: DeviceCondition[] = ['brand_new', 'used', 'refurbished', 'damaged', 'for_parts'];

/** Devices (blueprint 11, 12): every unit with its IMEI, status, condition, grade and own cost; inspections and refurbishment. */
export function DevicesPage() {
  const { t } = useI18n();
  const { me } = useAuth();
  const [tab, setTab] = useState<Tab>('stock');
  const tabs: [Tab, boolean][] = [['stock', true], ['inspections', can(me, 'DEVICE.INSPECT')], ['refurbishment', true], ['reports', true]];
  return (
    <div className="stack">
      <PageHeader title={t('devices.title')} />
      <div className="tabs" role="tablist">
        {tabs.filter((x) => x[1]).map(([k]) => <button key={k} role="tab" aria-selected={tab === k} className={`tab${tab === k ? ' active' : ''}`} onClick={() => setTab(k)}>{t(`devices.tabs.${k}`)}</button>)}
      </div>
      {tab === 'stock' && <StockTab />}
      {tab === 'inspections' && <InspectionsTab />}
      {tab === 'refurbishment' && <RefurbTab />}
      {tab === 'reports' && <ReportsTab />}
    </div>
  );
}

function StockTab() {
  const api = useApi();
  const { t, pick } = useI18n();
  const [q, setQ] = useState('');
  const [status, setStatus] = useState('in_stock,under_inspection,returned_inspection,refurbishing,for_parts,quarantined');
  const [condition, setCondition] = useState('');
  const rows = useQuery({ queryKey: ['devices', q, status, condition], queryFn: () => api.devices({ q: q || undefined, status: status || undefined, condition: condition || undefined }) });
  return (
    <div className="stack">
      <div className="row">
        <TextField label={t('app.search')} placeholder={t('devices.searchHint')} value={q} onChange={(e) => setQ(e.target.value)} dir="auto" />
        <SelectField label={t('devices.status')} value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="in_stock,under_inspection,returned_inspection,refurbishing,for_parts,quarantined">{t('devices.inShop')}</option>
          <option value="">{t('devices.allStatuses')}</option>
          {STATUSES.map((s) => <option key={s} value={s}>{t(`devices.statuses.${s}`)}</option>)}
        </SelectField>
        <SelectField label={t('devices.condition')} value={condition} onChange={(e) => setCondition(e.target.value)}>
          <option value="">{t('devices.allConditions')}</option>
          {CONDITIONS.map((c) => <option key={c} value={c}>{t(`devices.conditions.${c}`)}</option>)}
        </SelectField>
      </div>
      <ErrorBox error={rows.error} />
      {rows.isLoading ? <Spinner label={t('app.loading')} /> : (
        <DataTable rows={rows.data ?? []} rowKey={(r) => r.id} empty={t('devices.empty')}
          columns={[
            { key: 'i', header: t('devices.identifier'), render: (r) => <Link className="link-button" to={`/app/devices/${r.id}`}><Ident value={r.imei1 ?? r.serial} /></Link> },
            { key: 'd', header: t('devices.item'), render: (r) => <span>{pick(r.descriptionEn, r.descriptionAr)} <span className="ltr muted">{r.sku}</span></span> },
            { key: 's', header: t('devices.status'), render: (r) => <StatusBadge status={r.status} /> },
            { key: 'c', header: t('devices.condition'), render: (r) => <ConditionBadge condition={r.condition} grade={r.gradeCode} /> },
            { key: 'k', header: t('devices.cost'), render: (r) => <span className="num">{fmt(r.accumulatedCost)}</span> },
            { key: 'p', header: t('devices.price'), render: (r) => <span className="num">{fmt(r.sellingPrice)}</span> },
            { key: 'a', header: t('devices.age'), render: (r) => <span className="num">{r.ageDays}</span> },
          ]} />
      )}
    </div>
  );
}

function InspectionsTab() {
  const api = useApi();
  const { t, pick, lang } = useI18n();
  const rows = useQuery({ queryKey: ['inspectionQueue'], queryFn: () => api.inspectionQueue() });
  return (
    <div className="stack">
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(r) => r.id} empty={t('devices.queueEmpty')}
        columns={[
          { key: 'i', header: t('devices.identifier'), render: (r) => <Link className="link-button" to={`/app/devices/${r.id}/inspect`}><Ident value={r.imei1 ?? r.serial} /></Link> },
          { key: 'd', header: t('devices.item'), render: (r) => pick(r.descriptionEn, r.descriptionAr) },
          { key: 's', header: t('devices.status'), render: (r) => <StatusBadge status={r.status} /> },
          { key: 'c', header: t('devices.condition'), render: (r) => <ConditionBadge condition={r.condition} /> },
          { key: 'k', header: t('devices.cost'), render: (r) => <span className="num">{fmt(r.accumulatedCost)}</span> },
          { key: 'w', header: t('devices.waitingSince'), render: (r) => formatDate(r.receivedAt, lang) },
          { key: 'x', header: '', render: (r) => <Link className="btn btn-primary btn-sm" to={`/app/devices/${r.id}/inspect`}>{t('devices.inspect')}</Link> },
        ]} />
      {rows.isLoading && <Spinner label={t('app.loading')} />}
    </div>
  );
}

function RefurbTab() {
  const api = useApi();
  const { t, pick, lang } = useI18n();
  const [status, setStatus] = useState('open');
  const rows = useQuery({ queryKey: ['refurbishments', status], queryFn: () => api.refurbishments({ status: status || undefined }) });
  return (
    <div className="stack">
      <div className="row">
        <SelectField label={t('devices.status')} value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="open">{t('devices.orderStatus.open')}</option>
          <option value="completed">{t('devices.orderStatus.completed')}</option>
          <option value="">{t('devices.allStatuses')}</option>
        </SelectField>
      </div>
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(r) => r.id} empty={t('devices.noOrders')}
        columns={[
          { key: 'n', header: t('devices.number'), render: (r) => <Link className="link-button ltr" to={`/app/refurbishments/${r.id}`}>{r.number}</Link> },
          { key: 'i', header: t('devices.identifier'), render: (r) => <Ident value={r.identifier} /> },
          { key: 'd', header: t('devices.item'), render: (r) => pick(r.descriptionEn, r.descriptionAr) },
          { key: 't', header: t('devices.technician'), render: (r) => r.technicianName ?? <span className="muted">{t('devices.unassigned')}</span> },
          { key: 'k', header: t('devices.costSoFar'), render: (r) => <span className="num">{fmt(r.unitCost)}</span> },
          { key: 's', header: t('devices.status'), render: (r) => t(`devices.orderStatus.${r.status}`) },
          { key: 'o', header: t('devices.opened'), render: (r) => formatDate(r.openedAt, lang) },
        ]} />
    </div>
  );
}

function ReportsTab() {
  const api = useApi();
  const { t } = useI18n();
  const stock = useQuery({ queryKey: ['deviceStockReport'], queryFn: () => api.deviceStockReport() });
  const profit = useQuery({ queryKey: ['deviceProfitReport'], queryFn: () => api.deviceProfitReport() });
  const refurb = useQuery({ queryKey: ['refurbishmentReport'], queryFn: () => api.refurbishmentReport() });
  return (
    <div className="stack">
      <ErrorBox error={stock.error ?? profit.error ?? refurb.error} />
      <h3>{t('devices.stockReport')}</h3>
      <DataTable rows={stock.data ?? []} rowKey={(r) => `${r.status}-${r.condition}-${r.gradeCode}`} empty={t('devices.empty')}
        columns={[
          { key: 's', header: t('devices.status'), render: (r) => <StatusBadge status={r.status} /> },
          { key: 'c', header: t('devices.condition'), render: (r) => <ConditionBadge condition={r.condition} grade={r.gradeCode} /> },
          { key: 'u', header: t('devices.units'), render: (r) => <span className="num">{r.units}</span> },
          { key: 'k', header: t('devices.cost'), render: (r) => <span className="num">{fmt(r.cost)}</span> },
          { key: 'a', header: t('devices.averageAge'), render: (r) => <span className="num">{r.averageAgeDays}</span> },
        ]} />
      <h3>{t('devices.profitReport')}</h3>
      <DataTable rows={profit.data ?? []} rowKey={(r) => `${r.number}-${r.unitId}`} empty={t('devices.none')}
        columns={[
          { key: 'n', header: t('devices.document'), render: (r) => <span className="ltr">{r.number}</span> },
          { key: 'i', header: t('devices.identifier'), render: (r) => <Ident value={r.identifier} /> },
          { key: 'd', header: t('devices.item'), render: (r) => r.descriptionEn },
          { key: 'v', header: t('devices.net'), render: (r) => <span className="num">{fmt(r.net)}</span> },
          { key: 'k', header: t('devices.cost'), render: (r) => <span className="num">{fmt(r.cost)}</span> },
          { key: 'p', header: t('devices.profit'), render: (r) => <span className={`num${r.profit < 0 ? ' neg' : ''}`}>{fmt(r.profit)}</span> },
        ]} />
      <h3>{t('devices.refurbReport')}</h3>
      <DataTable rows={refurb.data ?? []} rowKey={(r) => r.technician ?? ''} empty={t('devices.none')}
        columns={[
          { key: 't', header: t('devices.technician'), render: (r) => r.technician ?? <span className="muted">{t('devices.unassigned')}</span> },
          { key: 'o', header: t('devices.orders'), render: (r) => <span className="num">{r.orders}</span> },
          { key: 'c', header: t('devices.completedOrders'), render: (r) => <span className="num">{r.completed}</span> },
          { key: 'p', header: t('devices.parts'), render: (r) => <span className="num">{fmt(r.parts)}</span> },
          { key: 'l', header: t('devices.labour'), render: (r) => <span className="num">{fmt(r.labour)}</span> },
          { key: 'e', header: t('devices.external'), render: (r) => <span className="num">{fmt(r.external)}</span> },
          { key: 'x', header: t('devices.total'), render: (r) => <span className="num">{fmt(r.total)}</span> },
        ]} />
    </div>
  );
}
