import { useRef, useState } from 'react';
import { useApi } from '../core';
import type { DeviceCondition, DeviceStatus, UnitInput } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { classifyCode } from '@kite/pos';
import { Badge, Button, SelectField, TextField } from '@kite/ui';

const CONDITION_TONE: Record<DeviceCondition, 'success' | 'primary' | 'warning' | 'danger' | 'default'> = {
  brand_new: 'success', refurbished: 'primary', used: 'default', damaged: 'warning', for_parts: 'danger',
};
const STATUS_TONE: Partial<Record<DeviceStatus, 'success' | 'primary' | 'warning' | 'danger' | 'default'>> = {
  in_stock: 'success', under_inspection: 'warning', returned_inspection: 'warning', refurbishing: 'primary', quarantined: 'danger', written_off: 'danger', for_parts: 'danger',
};

export function ConditionBadge({ condition, grade }: { condition: DeviceCondition; grade?: string | null }) {
  const { t } = useI18n();
  return <Badge tone={CONDITION_TONE[condition]}>{t(`devices.conditions.${condition}`)}{grade ? ` · ${grade}` : ''}</Badge>;
}

export function StatusBadge({ status }: { status: DeviceStatus }) {
  const { t } = useI18n();
  return <Badge tone={STATUS_TONE[status] ?? 'default'}>{t(`devices.statuses.${status}`)}</Badge>;
}

/** IMEIs and serial numbers stay left-to-right inside Arabic text (blueprint 18). */
export const Ident = ({ value }: { value: string | null | undefined }) => <span className="ltr mono">{value ?? '—'}</span>;

/** Keyboard-wedge scanners type a whole code in well under a second; typing by hand is slower (11.2). */
function useEntryTiming() {
  const first = useRef<number | null>(null);
  return {
    onKey: () => { if (first.current == null) first.current = performance.now(); },
    take: (length: number): 'scan' | 'manual' => {
      const started = first.current;
      first.current = null;
      if (started == null || length === 0) return 'manual';
      return (performance.now() - started) / length < 35 ? 'scan' : 'manual';
    },
  };
}

export interface ScannedUnit { code: string; kind: 'imei' | 'serial'; entry: 'scan' | 'manual'; problem?: string | null; hint?: string | null; hintMismatch?: boolean; localOnly?: boolean; checking?: boolean }

/** Receiving devices (11.3, 6.1): one scan per device; the IMEI's check digit is verified as it is scanned, then the server checks
 *  the blocked list, this business's devices and (online) head office, and gives a model hint from the TAC table. */
export function UnitScanner({ units, onChange, tracking, variantId }: { units: ScannedUnit[]; onChange: (u: ScannedUnit[]) => void; tracking: string; variantId?: string }) {
  const { t } = useI18n();
  const api = useApi();
  const latest = useRef(units);
  latest.current = units;
  const [value, setValue] = useState('');
  const [problem, setProblem] = useState<string | null>(null);
  const timing = useEntryTiming();
  const add = () => {
    const c = classifyCode(value);
    const entry = timing.take(value.trim().length);
    if (c.kind === 'invalid_imei' || c.kind === 'unknown' || (tracking === 'imei' && c.kind !== 'imei')) { setProblem(`${c.value || value}: ${t('devices.invalidImei')}`); return; }
    if (units.some((u) => u.code === c.value)) { setProblem(`${c.value}: ${t('devices.scannedTwice')}`); setValue(''); return; }
    const added: ScannedUnit = { code: c.value, kind: c.kind as 'imei' | 'serial', entry, checking: true };
    onChange([...units, added]);
    setProblem(null);
    setValue('');
    void api.checkIdentifiers({ codes: [c.value], variantId: variantId ?? null }).then(([r]) => r, () => null).then((r) => {
      const patch: Partial<ScannedUnit> = r ? { checking: false, problem: r.problem?.message ?? null, hint: r.hint, hintMismatch: !!r.hintMismatch, localOnly: r.centralChecked === false } : { checking: false };
      onChange(latest.current.map((u) => u.code === c.value ? { ...u, ...patch } : u));
    });
  };
  return (
    <div className="stack unit-scanner">
      <TextField label={t('devices.scanImeis')} help={t('devices.scanHelp')} value={value} dir="ltr" autoComplete="off" error={problem ?? undefined}
        onChange={(e) => setValue(e.target.value)} onKeyDown={(e) => { timing.onKey(); if (e.key === 'Enter') { e.preventDefault(); add(); } }} />
      <div className="chips">
        {units.map((u) => (
          <span key={u.code} className={`chip${u.problem ? ' chip-error' : ''}`} title={u.problem ?? u.hint ?? undefined} data-testid="scanned-unit">
            <Ident value={u.code} />{u.entry === 'manual' && <Badge tone="warning">✎</Badge>}
            {u.checking && <span className="muted">…</span>}
            {u.problem && <Badge tone="danger">{u.problem}</Badge>}
            {!u.problem && u.hint && <Badge tone={u.hintMismatch ? 'warning' : 'default'}>{u.hint}</Badge>}
            {!u.problem && u.localOnly && <Badge tone="warning">{t('imei.check.localOnly')}</Badge>}
            <button className="link-button" aria-label={`${t('devices.remove')} ${u.code}`} onClick={() => onChange(units.filter((x) => x.code !== u.code))}>×</button></span>
        ))}
      </div>
      <span className="muted">{t('devices.scanned', { count: String(units.length) })}</span>
    </div>
  );
}

/** How received devices come in: condition and whether they still need an inspection. */
export function ReceiveOptions({ condition, status, onChange }: { condition: DeviceCondition; status: 'in_stock' | 'under_inspection'; onChange: (c: DeviceCondition, s: 'in_stock' | 'under_inspection') => void }) {
  const { t } = useI18n();
  return (
    <div className="row">
      <SelectField label={t('devices.comesInAs')} value={condition} onChange={(e) => { const c = e.target.value as DeviceCondition; onChange(c, c === 'brand_new' ? 'in_stock' : 'under_inspection'); }}>
        {(['brand_new', 'used', 'refurbished', 'damaged'] as DeviceCondition[]).map((c) => <option key={c} value={c}>{t(`devices.conditions.${c}`)}</option>)}
      </SelectField>
      <SelectField label={t('devices.status')} value={status} onChange={(e) => onChange(condition, e.target.value as 'in_stock' | 'under_inspection')}>
        <option value="in_stock">{t('devices.readyToSell')}</option>
        <option value="under_inspection">{t('devices.needsInspection')}</option>
      </SelectField>
    </div>
  );
}

export const toUnitInputs = (units: ScannedUnit[], condition: DeviceCondition, status: 'in_stock' | 'under_inspection', unitCost: number | null): UnitInput[] =>
  units.map((u) => ({ ...(u.kind === 'imei' ? { imei1: u.code } : { serial: u.code }), condition, status, unitCost, entry: u.entry }));

export function RemoveButton({ onClick }: { onClick: () => void }) {
  const { t } = useI18n();
  return <Button size="sm" variant="ghost" onClick={onClick}>{t('devices.remove')}</Button>;
}
