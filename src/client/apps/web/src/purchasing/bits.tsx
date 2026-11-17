import { useQuery } from '@tanstack/react-query';
import type { InvoiceStatus, MatchStatus, OrderStatus } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Badge, SelectField } from '@kite/ui';
import { useApi, useAuth } from '../core';

export const newId = () => crypto.randomUUID();

export const orderTone = (s: OrderStatus) =>
  (s === 'draft' ? 'default' : s === 'approved' || s === 'sent' ? 'primary' : s === 'partly_received' ? 'warning' : s === 'cancelled' ? 'danger' : 'success') as
    'default' | 'primary' | 'warning' | 'danger' | 'success';
export const invoiceTone = (s: InvoiceStatus) =>
  (s === 'posted' ? 'success' : s === 'needs_approval' ? 'warning' : s === 'cancelled' ? 'danger' : s === 'matched' ? 'primary' : 'default') as
    'default' | 'primary' | 'warning' | 'danger' | 'success';

export function OrderBadge({ status }: { status: OrderStatus }) {
  const { t } = useI18n();
  return <Badge tone={orderTone(status)}>{t(`purch.orderStatus.${status}`)}</Badge>;
}

export function InvoiceBadge({ status }: { status: InvoiceStatus }) {
  const { t } = useI18n();
  return <Badge tone={invoiceTone(status)}>{t(`purch.invoiceStatus.${status}`)}</Badge>;
}

export function MatchBadge({ status }: { status: MatchStatus }) {
  const { t } = useI18n();
  const tone = status === 'ok' ? 'success' : status === 'none' ? 'default' : 'danger';
  return <Badge tone={tone}>{t(`purch.match.${status}`)}</Badge>;
}

/** Suppliers documents can be raised for: active ones and those a branch added (not merged or inactive). */
export function SupplierSelect({ value, onChange, label, payable }: { value: string; onChange: (id: string) => void; label?: string; payable?: boolean }) {
  const api = useApi();
  const { t, pick } = useI18n();
  const list = useQuery({ queryKey: ['suppliers', '', ''], queryFn: () => api.suppliers({}) });
  const usable = (list.data ?? []).filter((s) => s.status === 'active' || (!payable && s.status === 'provisional'));
  return (
    <SelectField label={label ?? t('purch.supplier')} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">{t('purch.chooseSupplier')}</option>
      {usable.map((s) => <option key={s.id} value={s.id}>{pick(s.nameEn, s.nameAr)} · {s.number}</option>)}
    </SelectField>
  );
}

/** The branches the user works in (all of them for head office users). */
export function BranchSelect({ value, onChange, label, any }: { value: string; onChange: (id: string) => void; label?: string; any?: string }) {
  const { me } = useAuth();
  const { t, pick } = useI18n();
  return (
    <SelectField label={label ?? t('purch.branch')} value={value} onChange={(e) => onChange(e.target.value)}>
      {any !== undefined && <option value="">{any}</option>}
      {(me?.branches ?? []).map((b) => <option key={b.id} value={b.id}>{b.code} · {pick(b.nameEn, b.nameAr)}</option>)}
    </SelectField>
  );
}

/** Downloads a blob the server made (a purchase order PDF). */
export async function download(blob: Promise<Blob>, name: string) {
  const url = URL.createObjectURL(await blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}
