// Phase 6 screens: the link with head office, IMEI conflicts, the blocked list, the business-wide IMEI look-up,
// counting devices by IMEI, and on a device's page its holds and identifiers with "Correct the IMEI" and "Reverse the receipt".
import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { can, type CentralState, type CountResult, type DeviceDetail, type EmergencyRecordRow } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { classifyCode } from '@kite/pos';
import { Alert, Badge, Button, Card, Checkbox, DataTable, Modal, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi, useAuth } from '../core';
import { fmt } from '../money';
import { ConditionBadge, Ident, StatusBadge } from './bits';

const CENTRAL_TONE: Record<CentralState, 'success' | 'warning' | 'danger'> = { verified: 'success', pending: 'warning', conflict: 'danger' };

export function CentralBadge({ state }: { state: CentralState | null | undefined }) {
  const { t } = useI18n();
  if (!state) return null;
  return <Badge tone={CENTRAL_TONE[state]}>{t(`imei.central.${state}`)}</Badge>;
}

/** Above the device list: is head office reachable, how many devices wait for it, how many are held by a conflict. */
export function CentralBanner() {
  const api = useApi();
  const { t, lang } = useI18n();
  const s = useQuery({ queryKey: ['centralStatus'], queryFn: () => api.centralStatus(), refetchInterval: 30_000 });
  if (!s.data) return null;
  const d = s.data;
  const lines: string[] = [];
  if (!d.isHeadOffice) lines.push(d.headOfficeReachable ? t('imei.central.reachable', { when: d.lastContactAt ? formatDateTime(d.lastContactAt, lang) : t('imei.central.never') }) : t('imei.central.unreachable'));
  if (d.pending > 0) lines.push(t('imei.central.pendingCount', { count: String(d.pending) }));
  if (d.unitsInConflict > 0) lines.push(t('imei.central.conflictCount', { count: String(d.unitsInConflict) }));
  if (d.isHeadOffice && (d.openConflicts ?? 0) > 0) lines.push(t('imei.central.openConflicts', { count: String(d.openConflicts) }));
  if (d.strict && d.pending > 0) lines.push(t('imei.central.strict'));
  if (!d.isHeadOffice && d.lastPushError) lines.push(t('imei.central.pushError', { error: d.lastPushError }));
  const tone = d.unitsInConflict > 0 || (d.openConflicts ?? 0) > 0 ? 'warning' : !d.isHeadOffice && !d.headOfficeReachable ? 'warning' : 'info';
  if (d.isHeadOffice && lines.length === 0) return <p className="muted" data-testid="central-banner">{t('imei.central.headOffice')}</p>;
  return <div data-testid="central-banner"><Alert tone={tone}>{lines.map((l) => <div key={l}>{l}</div>)}</Alert></div>;
}

/** The business-wide look-up (6.8): this branch's history of an IMEI and, from head office, where else it is. */
export function ImeiLookup() {
  const api = useApi();
  const { t, pick, lang } = useI18n();
  const [code, setCode] = useState('');
  const [asked, setAsked] = useState<string | null>(null);
  const r = useQuery({ queryKey: ['historyLookup', asked], queryFn: () => api.historyLookup(asked!), enabled: !!asked, retry: false });
  const d = r.data;
  return (
    <Card>
      <h3>{t('imei.lookup.title')}</h3>
      <form className="row" onSubmit={(e) => { e.preventDefault(); if (code.trim()) setAsked(classifyCode(code).value || code.trim()); }}>
        <TextField label={t('imei.lookup.hint')} value={code} dir="ltr" autoComplete="off" onChange={(e) => setCode(e.target.value)} />
        <Button type="submit" variant="primary" busy={r.isFetching}>{t('imei.lookup.go')}</Button>
      </form>
      <ErrorBox error={r.error} />
      {d && (
        <div className="stack" data-testid="lookup-result">
          <div className="row wrap"><Ident value={d.value} />{d.model && <span className="muted">{t('imei.lookup.model', { model: d.model })}</span>}</div>
          {d.blocked && <Alert tone="error">{t('imei.lookup.blocked', { reason: d.blocked })}</Alert>}
          {!d.headOfficeReachable && <Alert tone="warning">{t('imei.lookup.unreachable')}</Alert>}
          {d.local.length === 0 && (d.headOffice?.holders.length ?? 0) === 0 && <p className="muted">{t('imei.lookup.none')}</p>}
          {d.local.length > 0 && (<>
            <h4>{t('imei.lookup.here')}</h4>
            <DataTable rows={d.local} rowKey={(x) => x.id} empty=""
              columns={[
                { key: 'd', header: t('devices.item'), render: (x) => x.visible ? <Link className="link-button" to={`/app/devices/${x.id}`}>{pick(x.descriptionEn, x.descriptionAr)}</Link> : pick(x.descriptionEn, x.descriptionAr) },
                { key: 'b', header: t('devices.branch'), render: (x) => <span className="ltr">{x.branchCode}</span> },
                { key: 's', header: t('devices.status'), render: (x) => <><StatusBadge status={x.status} /> {x.identifierStatus === 'retired' && <Badge>{t('imei.lookup.replaced')}</Badge>}</> },
                { key: 'c', header: t('devices.condition'), render: (x) => <ConditionBadge condition={x.condition} /> },
                { key: 'r', header: t('devices.received'), render: (x) => formatDateTime(x.receivedAt, lang) },
                { key: 'o', header: t('devices.sales'), render: (x) => x.soldOn ? <span className="ltr">{t('imei.lookup.soldOn', { number: x.soldOn })}</span> : '—' },
              ]} />
          </>)}
          {d.headOffice && d.headOffice.holders.length > 0 && (<>
            <h4>{t('imei.lookup.headOffice')}</h4>
            {d.headOffice.openConflicts.length > 0 && <Alert tone="warning">{t('imei.lookup.openConflicts', { count: String(d.headOffice.openConflicts.length) })}</Alert>}
            <DataTable rows={d.headOffice.holders} rowKey={(x) => x.unitId} empty=""
              columns={[
                { key: 'b', header: t('devices.branch'), render: (x) => <span><span className="ltr">{x.branchCode}</span> · {pick(x.branchNameEn, x.branchNameAr)}</span> },
                { key: 'd', header: t('devices.item'), render: (x) => pick(x.descriptionEn, x.descriptionAr) },
                { key: 's', header: t('devices.status'), render: (x) => <><StatusBadge status={x.unitStatus} /> {x.rowStatus === 'conflict' && <Badge tone="danger">{t('imei.lookup.conflictRow')}</Badge>}</> },
                { key: 'r', header: t('devices.received'), render: (x) => formatDateTime(x.receivedAt, lang) },
              ]} />
          </>)}
        </div>
      )}
    </Card>
  );
}

/** IMEI conflicts: at head office every conflict across the branches; on a branch the devices held here. Plus emergency phone sales to settle. */
export function ConflictsTab() {
  const api = useApi();
  const { t, pick, lang } = useI18n();
  const { me } = useAuth();
  const status = useQuery({ queryKey: ['centralStatus'], queryFn: () => api.centralStatus() });
  const [filter, setFilter] = useState<'open' | 'resolved' | ''>('open');
  const rows = useQuery({ queryKey: ['deviceConflicts', filter], queryFn: () => api.deviceConflicts(filter || undefined) });
  return (
    <div className="stack">
      <p className="muted">{status.data?.isHeadOffice === false ? t('imei.conflicts.branchHelp') : t('imei.conflicts.help')}</p>
      <div className="row">
        <SelectField label={t('devices.status')} value={filter} onChange={(e) => setFilter(e.target.value as 'open' | 'resolved' | '')}>
          <option value="open">{t('imei.conflicts.open')}</option>
          <option value="resolved">{t('imei.conflicts.resolved')}</option>
          <option value="">{t('imei.conflicts.all')}</option>
        </SelectField>
      </div>
      <ErrorBox error={rows.error} />
      {rows.isLoading ? <Spinner label={t('app.loading')} /> : (
        <DataTable rows={rows.data ?? []} rowKey={(c) => c.id} empty={t('imei.conflicts.empty')}
          columns={[
            { key: 'v', header: t('imei.conflicts.value'), render: (c) => <Ident value={c.value} /> },
            { key: 's', header: t('imei.conflicts.sides'), render: (c) => (
              <ul className="plain">
                {c.sides.map((x) => (
                  <li key={x.unitId}>
                    <span className="ltr">{x.branchCode}</span> · {pick(x.descriptionEn, x.descriptionAr)} · <StatusBadge status={x.unitStatus} />
                    {x.outcome && <> · <Badge tone={x.outcome === 'kept' ? 'success' : 'default'}>{t(`imei.conflicts.outcomes.${x.outcome}`)}</Badge></>}
                    {x.local && <> · <Link className="link-button" to={`/app/devices/${x.unitId}`}>{t('imei.conflicts.openDevice')}</Link></>}
                  </li>
                ))}
              </ul>) },
            { key: 'o', header: t('imei.conflicts.opened'), render: (c) => formatDateTime(c.openedAt, lang) },
            { key: 'r', header: t('imei.conflicts.resolution'), render: (c) => c.status === 'open' ? <Badge tone="danger">{t('imei.conflicts.open')}</Badge>
              : <span>{c.resolution}{c.resolvedAt ? <span className="muted"> · {formatDateTime(c.resolvedAt, lang)}</span> : null}</span> },
          ]} />
      )}
      {can(me, 'POS.REPORTS.VIEW') && <EmergencyToSettle />}
    </div>
  );
}

function EmergencyToSettle() {
  const api = useApi();
  const { t, lang } = useI18n();
  const { me } = useAuth();
  const rows = useQuery({ queryKey: ['emergencyRecords', 'conflict', 'devices'], queryFn: () => api.emergencyRecords('conflict', true) });
  const [open, setOpen] = useState<EmergencyRecordRow | null>(null);
  return (
    <div className="stack">
      <h3>{t('imei.emergency.title')}</h3>
      <p className="muted">{t('imei.emergency.help')}</p>
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(r) => r.id} empty={t('imei.emergency.empty')}
        columns={[
          { key: 'n', header: t('imei.emergency.sale'), render: (r) => <span className="ltr">{r.number ?? `${r.terminalCode} #${r.seq}`}</span> },
          { key: 'w', header: t('salesReports.recorded'), render: (r) => formatDateTime(r.recordedAt, lang) },
          { key: 'g', header: t('imei.emergency.total'), render: (r) => <span className="num">{fmt(r.gross ?? 0)}</span> },
          { key: 'd', header: t('salesReports.detail'), render: (r) => r.detail ?? '—' },
          { key: 'x', header: '', render: (r) => r.settledOutcome
            ? <Badge tone={r.settledOutcome === 'applied' ? 'success' : 'default'}>{t(`imei.emergency.outcomes.${r.settledOutcome}`, { number: r.settledSaleNumber ?? '' })}</Badge>
            : can(me, 'DEVICE.CONFLICTS') ? <Button size="sm" variant="primary" onClick={() => setOpen(r)}>{t('imei.emergency.settle')}</Button> : null },
        ]} />
      {open && <SettleDialog record={open} onClose={() => setOpen(null)} />}
    </div>
  );
}

type DeviceLine = { line: number; variantId: string; descriptionEn: string; descriptionAr: string; deviceUnitId: string; identifier: string | null };

function SettleDialog({ record, onClose }: { record: EmergencyRecordRow; onClose: () => void }) {
  const api = useApi();
  const qc = useQueryClient();
  const { t, pick } = useI18n();
  const lines = useMemo<DeviceLine[]>(() => { try { return JSON.parse(record.deviceLines ?? '[]') as DeviceLine[]; } catch { return []; } }, [record.deviceLines]);
  const [subs, setSubs] = useState<Record<number, string>>({});
  const [note, setNote] = useState('');
  const [problem, setProblem] = useState<string | null>(null);
  const settle = useMutation({
    mutationFn: (action: 'apply' | 'dismiss') => {
      if (action === 'dismiss' && !note.trim()) { setProblem(t('imei.emergency.noteRequired')); return Promise.reject(new Error('note')); }
      return api.settleEmergency(record.id, { action, note: note.trim() || null, substitutions: Object.entries(subs).filter(([, v]) => v).map(([k, v]) => ({ line: Number(k), deviceUnitId: v })) });
    },
    onSuccess: async () => { await qc.invalidateQueries({ queryKey: ['emergencyRecords'] }); await qc.invalidateQueries({ queryKey: ['devices'] }); onClose(); },
  });
  return (
    <Modal open title={`${t('imei.emergency.settle')}: ${record.number ?? ''}`} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button>
        <Button busy={settle.isPending} onClick={() => settle.mutate('dismiss')}>{t('imei.emergency.dismiss')}</Button>
        <Button variant="primary" busy={settle.isPending} onClick={() => settle.mutate('apply')}>{t('imei.emergency.apply')}</Button></>}>
      <div className="stack">
        {record.detail && <Alert tone="warning">{record.detail}</Alert>}
        {lines.map((l) => <SubstituteLine key={l.line} line={l} label={pick(l.descriptionEn, l.descriptionAr)} value={subs[l.line] ?? ''} onChange={(v) => setSubs({ ...subs, [l.line]: v })} />)}
        <TextField label={t('imei.emergency.note')} value={note} onChange={(e) => { setNote(e.target.value); setProblem(null); }} error={problem} />
        {!(settle.error instanceof Error && settle.error.message === 'note') && <ErrorBox error={settle.error} />}
      </div>
    </Modal>
  );
}

function SubstituteLine({ line, label, value, onChange }: { line: DeviceLine; label: string; value: string; onChange: (v: string) => void }) {
  const api = useApi();
  const { t } = useI18n();
  const units = useQuery({ queryKey: ['devices', 'substitute', line.variantId], queryFn: () => api.devices({ variantId: line.variantId, status: 'in_stock' }) });
  return (
    <div className="stack">
      <div>{t('imei.emergency.phone')}: {label} · <Ident value={line.identifier} /></div>
      <SelectField label={t('imei.emergency.substitute')} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">{t('imei.emergency.pick')}</option>
        {units.data?.map((u) => <option key={u.id} value={u.id}>{u.imei1 ?? u.serial} · {t(`devices.conditions.${u.condition}`)}</option>)}
      </SelectField>
    </div>
  );
}

/** The blocked list: kept at head office, copied to branches with the heartbeat. */
export function BlockedTab() {
  const api = useApi();
  const qc = useQueryClient();
  const { t, lang } = useI18n();
  const { me } = useAuth();
  const status = useQuery({ queryKey: ['centralStatus'], queryFn: () => api.centralStatus() });
  const [q, setQ] = useState('');
  const [state, setState] = useState<'active' | 'removed' | ''>('active');
  const rows = useQuery({ queryKey: ['blockedImeis', q, state], queryFn: () => api.blockedImeis({ q: q || undefined, status: state || undefined }) });
  const [adding, setAdding] = useState(false);
  const [removing, setRemoving] = useState<string | null>(null);
  const [value, setValue] = useState(''); const [reason, setReason] = useState(''); const [reference, setReference] = useState('');
  const editable = status.data?.isHeadOffice !== false && can(me, 'DEVICE.BLOCKLIST');
  const done = async () => { await qc.invalidateQueries({ queryKey: ['blockedImeis'] }); await qc.invalidateQueries({ queryKey: ['centralStatus'] }); await qc.invalidateQueries({ queryKey: ['devices'] }); };
  const add = useMutation({ mutationFn: () => api.addBlockedImei({ value, reason, reference: reference || null }), onSuccess: async () => { await done(); setAdding(false); setValue(''); setReason(''); setReference(''); } });
  const remove = useMutation({ mutationFn: () => api.removeBlockedImei(removing!, reason), onSuccess: async () => { await done(); setRemoving(null); setReason(''); } });
  return (
    <div className="stack">
      <p className="muted">{editable ? t('imei.blocked.help') : t('imei.blocked.branchHelp')}</p>
      <div className="row">
        <TextField label={t('app.search')} placeholder={t('imei.blocked.search')} value={q} onChange={(e) => setQ(e.target.value)} dir="auto" />
        <SelectField label={t('devices.status')} value={state} onChange={(e) => setState(e.target.value as 'active' | 'removed' | '')}>
          <option value="active">{t('imei.blocked.statuses.active')}</option>
          <option value="removed">{t('imei.blocked.statuses.removed')}</option>
          <option value="">{t('imei.conflicts.all')}</option>
        </SelectField>
        {editable && <Button variant="primary" onClick={() => setAdding(true)}>{t('imei.blocked.add')}</Button>}
      </div>
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(b) => b.id} empty={t('imei.blocked.empty')}
        columns={[
          { key: 'v', header: t('imei.blocked.value'), render: (b) => <Ident value={b.value} /> },
          { key: 'r', header: t('imei.blocked.reason'), render: (b) => <span>{b.reason}{b.reference && <span className="muted"> · <span className="ltr">{b.reference}</span></span>}</span> },
          { key: 'a', header: t('imei.blocked.addedAt'), render: (b) => `${formatDateTime(b.addedAt, lang)}${b.addedBy ? ` · ${b.addedBy}` : ''}` },
          { key: 'h', header: t('imei.blocked.devicesHere'), render: (b) => <span className="num">{b.devicesHere}</span> },
          { key: 's', header: t('devices.status'), render: (b) => <Badge tone={b.status === 'active' ? 'danger' : 'default'}>{t(`imei.blocked.statuses.${b.status}`)}</Badge> },
          { key: 'x', header: '', render: (b) => editable && b.status === 'active' ? <Button size="sm" onClick={() => setRemoving(b.id)}>{t('imei.blocked.remove')}</Button> : (b.removedReason ?? '') },
        ]} />
      {adding && (
        <Modal open title={t('imei.blocked.add')} onClose={() => setAdding(false)}
          footer={<><Button onClick={() => setAdding(false)}>{t('app.cancel')}</Button><Button variant="danger" busy={add.isPending} onClick={() => add.mutate()}>{t('imei.blocked.add')}</Button></>}>
          <div className="stack">
            <TextField label={t('imei.blocked.value')} value={value} dir="ltr" autoComplete="off" onChange={(e) => setValue(e.target.value)} />
            <TextField label={t('imei.blocked.reason')} placeholder={t('imei.blocked.reasonHint')} value={reason} onChange={(e) => setReason(e.target.value)} />
            <TextField label={t('imei.blocked.reference')} value={reference} dir="auto" onChange={(e) => setReference(e.target.value)} />
            <ErrorBox error={add.error} />
          </div>
        </Modal>
      )}
      {removing && (
        <Modal open title={t('imei.blocked.remove')} onClose={() => setRemoving(null)}
          footer={<><Button onClick={() => setRemoving(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={remove.isPending} onClick={() => remove.mutate()}>{t('imei.blocked.remove')}</Button></>}>
          <div className="stack">
            <TextField label={t('imei.blocked.removeReason')} value={reason} onChange={(e) => setReason(e.target.value)} />
            <ErrorBox error={remove.error} />
          </div>
        </Modal>
      )}
    </div>
  );
}

/** Counting devices by IMEI (6.9): scan every device; the server says for each scan whether it belongs here. */
export function CountsTab() {
  const api = useApi();
  const qc = useQueryClient();
  const { t, lang } = useI18n();
  const { me } = useAuth();
  const branchId = me?.user.defaultBranchId ?? me?.branches[0]?.id ?? '';
  const list = useQuery({ queryKey: ['deviceCounts'], queryFn: () => api.deviceCounts() });
  const stock = useQuery({ queryKey: ['devices', 'models'], queryFn: () => api.devices({ status: 'in_stock,under_inspection,returned_inspection,refurbishing,for_parts,quarantined' }) });
  const models = useMemo(() => {
    const m = new Map<string, { en: string; ar: string }>();
    for (const d of stock.data ?? []) m.set(d.variantId, { en: d.descriptionEn, ar: d.descriptionAr });
    return [...m.entries()];
  }, [stock.data]);
  const [model, setModel] = useState('');
  const [open, setOpen] = useState<string | null>(null);
  const start = useMutation({ mutationFn: () => api.startDeviceCount({ branchId, variantId: model || null }), onSuccess: async (c) => { await qc.invalidateQueries({ queryKey: ['deviceCounts'] }); setOpen(c.id); } });
  const { pick } = useI18n();
  if (open) return <CountView id={open} onBack={() => setOpen(null)} />;
  return (
    <div className="stack">
      <div className="row">
        <SelectField label={t('imei.counts.model')} value={model} onChange={(e) => setModel(e.target.value)}>
          <option value="">{t('imei.counts.allModels')}</option>
          {models.map(([id, n]) => <option key={id} value={id}>{pick(n.en, n.ar)}</option>)}
        </SelectField>
        <Button variant="primary" busy={start.isPending} onClick={() => start.mutate()}>{t('imei.counts.start')}</Button>
      </div>
      <ErrorBox error={start.error ?? list.error} />
      <DataTable rows={list.data ?? []} rowKey={(c) => c.id} empty={t('imei.counts.empty')}
        columns={[
          { key: 'n', header: t('imei.counts.number'), render: (c) => <button className="link-button ltr" onClick={() => setOpen(c.id)}>{c.number}</button> },
          { key: 'm', header: t('imei.counts.model'), render: (c) => c.variantDescription ?? t('imei.counts.allModels') },
          { key: 's', header: t('devices.status'), render: (c) => <Badge tone={c.status === 'open' ? 'warning' : c.status === 'posted' ? 'success' : 'default'}>{t(`imei.counts.statuses.${c.status}`)}</Badge> },
          { key: 'c', header: t('imei.counts.scans'), render: (c) => <span className="num">{c.scans}</span> },
          { key: 'w', header: t('imei.counts.started'), render: (c) => `${formatDateTime(c.startedAt, lang)} · ${c.startedBy}` },
        ]} />
    </div>
  );
}

const RESULT_TONE: Record<CountResult, 'success' | 'warning' | 'danger' | 'default'> = { here: 'success', other_model: 'warning', elsewhere: 'warning', not_in_stock: 'danger', unknown: 'danger' };

function CountView({ id, onBack }: { id: string; onBack: () => void }) {
  const api = useApi();
  const qc = useQueryClient();
  const { t, pick } = useI18n();
  const d = useQuery({ queryKey: ['deviceCount', id], queryFn: () => api.deviceCount(id) });
  const [code, setCode] = useState('');
  const [last, setLast] = useState<{ value: string; result: CountResult; message: string } | null>(null);
  const [finishing, setFinishing] = useState(false);
  const [writeOff, setWriteOff] = useState(false);
  const [reason, setReason] = useState('');
  const [posted, setPosted] = useState<string | null>(null);
  const refresh = () => qc.invalidateQueries({ queryKey: ['deviceCount', id] });
  const scan = useMutation({ mutationFn: (c: string) => api.scanDeviceCount(id, { code: c }), onSuccess: async (r) => { setLast(r); setCode(''); await refresh(); } });
  const unscan = useMutation({ mutationFn: (scanId: string) => api.removeCountScan(id, scanId), onSuccess: refresh });
  const post = useMutation({
    mutationFn: () => api.postDeviceCount(id, { writeOffMissing: writeOff, reason: writeOff ? reason : null }),
    onSuccess: async (r) => { setPosted(t('imei.counts.posted', { number: d.data?.count.number ?? '', found: String(r.found), expected: String(r.expected) })); setFinishing(false); await refresh(); await qc.invalidateQueries({ queryKey: ['deviceCounts'] }); },
  });
  const cancel = useMutation({ mutationFn: () => api.cancelDeviceCount(id), onSuccess: async () => { await qc.invalidateQueries({ queryKey: ['deviceCounts'] }); onBack(); } });
  if (d.isLoading) return <Spinner label={t('app.loading')} />;
  if (!d.data) return <ErrorBox error={d.error} />;
  const c = d.data;
  const isOpen = c.count.status === 'open';
  return (
    <div className="stack">
      <div className="row wrap">
        <Button variant="ghost" onClick={onBack}>{t('imei.counts.back')}</Button>
        <h3 className="ltr">{t('imei.counts.title', { number: c.count.number })}</h3>
        <Badge tone={isOpen ? 'warning' : 'success'}>{t(`imei.counts.statuses.${c.count.status}`)}</Badge>
        <span className="spacer" />
        {isOpen && <Button onClick={() => cancel.mutate()} busy={cancel.isPending}>{t('imei.counts.cancel')}</Button>}
        {isOpen && <Button variant="primary" onClick={() => setFinishing(true)}>{t('imei.counts.post')}</Button>}
      </div>
      {posted && <Alert tone="success">{posted}</Alert>}
      <div className="row wrap figures-row">
        <span>{t('imei.counts.expected')}: <strong className="num">{c.expected.length}</strong></span>
        <span>{t('imei.counts.found')}: <strong className="num">{c.found}</strong></span>
        <span>{t('imei.counts.missing')}: <strong className="num">{c.missing.length}</strong></span>
        <span>{t('imei.counts.extra')}: <strong className="num">{c.extra.length}</strong></span>
      </div>
      {isOpen && (
        <form onSubmit={(e) => { e.preventDefault(); if (code.trim()) scan.mutate(code.trim()); }}>
          <TextField label={t('imei.counts.scan')} help={t('imei.counts.scanHelp')} value={code} dir="ltr" autoComplete="off" autoFocus onChange={(e) => setCode(e.target.value)} />
        </form>
      )}
      <ErrorBox error={scan.error ?? unscan.error ?? cancel.error} />
      {last && <div data-testid="count-last"><Alert tone={last.result === 'here' ? 'success' : 'warning'}><Ident value={last.value} /> · <strong>{t(`imei.counts.results.${last.result}`)}</strong> · {last.message}</Alert></div>}
      <div className="grid-2">
        <Card>
          <h4>{t('imei.counts.missing')}</h4>
          <DataTable rows={c.missing} rowKey={(m) => m.id} empty={t('devices.none')}
            columns={[
              { key: 'i', header: t('devices.identifier'), render: (m) => <Link className="link-button" to={`/app/devices/${m.id}`}><Ident value={m.identifier} /></Link> },
              { key: 'd', header: t('devices.item'), render: (m) => pick(m.descriptionEn, m.descriptionAr) },
              { key: 's', header: t('devices.status'), render: (m) => <StatusBadge status={m.status} /> },
              { key: 'k', header: t('devices.cost'), render: (m) => <span className="num">{fmt(m.cost)}</span> },
            ]} />
        </Card>
        <Card>
          <h4>{t('imei.counts.scans')}</h4>
          <DataTable rows={c.scans} rowKey={(s) => s.id} empty={t('devices.none')}
            columns={[
              { key: 'v', header: t('devices.identifier'), render: (s) => <Ident value={s.value} /> },
              { key: 'r', header: t('devices.status'), render: (s) => <Badge tone={RESULT_TONE[s.result]}>{t(`imei.counts.results.${s.result}`)}</Badge> },
              { key: 'd', header: t('devices.item'), render: (s) => s.descriptionEn ?? '—' },
              { key: 'x', header: '', render: (s) => isOpen ? <button className="link-button" onClick={() => unscan.mutate(s.id)}>{t('imei.counts.removeScan')}</button> : null },
            ]} />
        </Card>
      </div>
      {finishing && (
        <Modal open title={t('imei.counts.post')} onClose={() => setFinishing(false)}
          footer={<><Button onClick={() => setFinishing(false)}>{t('app.cancel')}</Button><Button variant="primary" busy={post.isPending} onClick={() => post.mutate()}>{t('imei.counts.post')}</Button></>}>
          <div className="stack">
            <p>{t('imei.counts.postHelp')}</p>
            <p>{t('imei.counts.missing')}: <strong className="num">{c.missing.length}</strong></p>
            {c.missing.length > 0 && <Checkbox label={t('imei.counts.writeOffMissing')} checked={writeOff} onChange={(e) => setWriteOff(e.target.checked)} />}
            {writeOff && <TextField label={t('imei.counts.reason')} value={reason} onChange={(e) => setReason(e.target.value)} />}
            <ErrorBox error={post.error} />
          </div>
        </Modal>
      )}
    </div>
  );
}

/** On a device's page: why it is held, what head office said, and every IMEI it has had. */
export function DeviceIdentityCards({ detail }: { detail: DeviceDetail }) {
  const { t, lang } = useI18n();
  const holds = detail.holds ?? [];
  const ids = detail.identifiers ?? [];
  return (
    <div className="grid-2">
      <Card>
        <h3>{t('imei.identifiers.title')}</h3>
        <ul className="plain" data-testid="identifiers">
          {ids.map((x) => (
            <li key={`${x.kind}-${x.slot}-${x.value}-${x.status}`}>
              {t(`imei.identifiers.kinds.${x.kind === 'serial' ? 'serial' : 'imei'}`)}{x.slot > 1 ? ` ${x.slot}` : ''}: <Ident value={x.value} />{' '}
              <Badge tone={x.status === 'active' ? 'success' : 'default'}>{t(`imei.identifiers.${x.status}`)}</Badge>{' '}
              <span className="muted">{t(`imei.identifiers.entries.${x.entry}`)}{x.retiredAt ? ` · ${formatDateTime(x.retiredAt, lang)}` : ''}{x.retiredReason ? ` · ${x.retiredReason}` : ''}</span>
            </li>
          ))}
        </ul>
        {detail.central?.lastError && <p className="muted">{t('imei.central.pushError', { error: detail.central.lastError })}</p>}
      </Card>
      <Card>
        <h3>{t('imei.holds.title')}</h3>
        {holds.length === 0 ? <p className="muted">{t('devices.none')}</p> : (
          <ul className="plain" data-testid="holds">
            {holds.map((h) => (
              <li key={h.id}>
                <Badge tone={h.releasedAt ? 'default' : 'danger'}>{t(`imei.holds.reasons.${h.reason}`)}</Badge>{' '}
                {h.note}
                <span className="muted"> · {t('imei.holds.heldOn', { when: formatDateTime(h.heldAt, lang) })}{h.releasedAt ? ` · ${t('imei.holds.released', { when: formatDateTime(h.releasedAt, lang) })}` : ''}{h.releaseNote ? ` · ${h.releaseNote}` : ''}</span>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

/** "Correct the IMEI" and "Reverse the receipt": the two ways an IMEI conflict is settled where the device is. */
export function IdentityDialog({ kind, detail, onClose }: { kind: 'correct' | 'reverse'; detail: DeviceDetail; onClose: () => void }) {
  const api = useApi();
  const qc = useQueryClient();
  const { t } = useI18n();
  const u = detail.unit;
  const [imei1, setImei1] = useState(''); const [serial, setSerial] = useState(''); const [reason, setReason] = useState('');
  const [done, setDone] = useState<string | null>(null);
  const check = useQuery({
    queryKey: ['identifierCheck', imei1], enabled: kind === 'correct' && classifyCode(imei1).kind === 'imei',
    queryFn: () => api.checkIdentifiers({ codes: [imei1], variantId: u.variantId, exceptUnitId: u.id }),
  });
  const save = useMutation({
    mutationFn: async () => {
      if (kind === 'correct') { await api.correctIdentifiers(u.id, { imei1: imei1.trim() || null, serial: serial.trim() || null, reason, entry: 'manual' }); return t('imei.identifiers.corrected'); }
      const r = await api.reverseReceipt(u.id, reason); return t('imei.identifiers.reversedOn', { number: r.number });
    },
    onSuccess: async (msg) => {
      setDone(msg);
      for (const k of [['device', u.id], ['devices'], ['deviceConflicts'], ['centralStatus']]) await qc.invalidateQueries({ queryKey: k });
    },
  });
  const title = kind === 'correct' ? t('imei.identifiers.correct') : t('imei.identifiers.reverse');
  const c = check.data?.[0];
  return (
    <Modal open title={title} onClose={onClose}
      footer={done ? <Button variant="primary" onClick={onClose}>{t('app.close')}</Button>
        : <><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant={kind === 'reverse' ? 'danger' : 'primary'} busy={save.isPending} onClick={() => save.mutate()}>{title}</Button></>}>
      <div className="stack">
        {done ? <Alert tone="success">{done}</Alert> : (<>
          <Alert tone={kind === 'reverse' ? 'warning' : 'info'}>{kind === 'correct' ? t('imei.identifiers.correctHelp') : t('imei.identifiers.reverseHelp')}</Alert>
          {kind === 'correct' && (u.imei1 != null || u.tracking === 'imei') && (<>
            <TextField label={t('imei.identifiers.newImei')} value={imei1} dir="ltr" autoComplete="off" onChange={(e) => setImei1(e.target.value)}
              error={c?.problem?.message ?? (imei1.trim().length >= 14 && classifyCode(imei1).kind !== 'imei' ? t('devices.invalidImei') : null)} help={c?.hint ?? undefined} />
          </>)}
          {kind === 'correct' && u.imei1 == null && u.tracking !== 'imei' && <TextField label={t('imei.identifiers.newSerial')} value={serial} dir="ltr" autoComplete="off" onChange={(e) => setSerial(e.target.value)} />}
          <TextField label={kind === 'correct' ? t('imei.identifiers.reason') : t('imei.identifiers.reverseReason')} value={reason} onChange={(e) => setReason(e.target.value)} />
          <ErrorBox error={save.error} />
        </>)}
      </div>
    </Modal>
  );
}
