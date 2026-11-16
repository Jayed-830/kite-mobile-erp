import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type Customer, type CustomerInput } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Badge, Button, DataTable, Modal, PageHeader, SelectField, TextField } from '@kite/ui';
import { ErrorBox, useApi, useAuth } from '../core';
import { StoreCreditButton } from '../tradein/TradeInSettingsPage';

/** Customers (Phase 4 basics; credit accounts arrive with Phase 10). */
export function CustomersPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t } = useI18n();
  const [q, setQ] = useState('');
  const [edit, setEdit] = useState<(CustomerInput & { id?: string }) | null>(null);
  const rows = useQuery({ queryKey: ['customers', q], queryFn: () => api.customers(q || undefined) });
  const save = useMutation({
    mutationFn: (c: CustomerInput & { id?: string }) => (c.id ? api.updateCustomer(c.id, c) : api.createCustomer(c)),
    onSuccess: async () => { setEdit(null); await qc.invalidateQueries({ queryKey: ['customers'] }); },
  });
  const toInput = (c: Customer): CustomerInput & { id: string } => ({ id: c.id, nameEn: c.nameEn, nameAr: c.nameAr, kind: c.kind, phone: c.phoneE164, email: c.email, vatNumber: c.vatNumber, crNumber: c.crNumber, addressEn: c.addressEn, addressAr: c.addressAr, status: c.status, version: c.version });
  return (
    <div className="stack">
      <PageHeader title={t('customers.title')}>
        {can(me, 'CUSTOMERS.MANAGE') && <Button variant="primary" onClick={() => { save.reset(); setEdit({ nameEn: '', kind: 'individual' }); }}>{t('customers.new')}</Button>}
      </PageHeader>
      <TextField label={t('app.search')} value={q} onChange={(e) => setQ(e.target.value)} placeholder={t('customers.searchHint')} />
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(c) => c.id} empty={t('app.none')}
        columns={[
          { key: 'no', header: t('customers.number'), render: (c) => <span className="ltr">{c.number}</span> },
          { key: 'n', header: t('customers.name'), render: (c) => <span>{c.nameEn}{c.nameAr ? <span className="muted"> · {c.nameAr}</span> : null}</span> },
          { key: 'p', header: t('customers.phone'), render: (c) => <span className="ltr">{c.phoneE164 ?? '—'}</span> },
          { key: 'v', header: t('customers.vat'), render: (c) => <span className="ltr">{c.vatNumber ?? '—'}</span> },
          { key: 'k', header: t('customers.kind'), render: (c) => t(`customers.kinds.${c.kind}`) },
          { key: 's', header: t('app.status'), render: (c) => <Badge tone={c.status === 'active' ? 'success' : 'default'}>{t(`app.${c.status}`)}</Badge> },
          { key: 'a', header: t('app.actions'), render: (c) => <span className="row">{can(me, 'CUSTOMERS.MANAGE') && <Button size="sm" onClick={() => { save.reset(); setEdit(toInput(c)); }}>{t('app.edit')}</Button>}
            <StoreCreditButton customerId={c.id} /></span> },
        ]} />
      {edit && (
        <Modal open title={edit.id ? t('customers.edit') : t('customers.new')} onClose={() => setEdit(null)}
          footer={<><Button onClick={() => setEdit(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={save.isPending} onClick={() => save.mutate(edit)}>{t('app.save')}</Button></>}>
          <CustomerFields value={edit} onChange={setEdit} />
          <ErrorBox error={save.error} />
        </Modal>
      )}
    </div>
  );
}

export function CustomerFields<T extends CustomerInput>({ value, onChange }: { value: T; onChange: (v: T) => void }) {
  const { t } = useI18n();
  return (
    <>
      <div className="grid-2">
        <TextField label={t('customers.nameEn')} value={value.nameEn} onChange={(e) => onChange({ ...value, nameEn: e.target.value })} required />
        <TextField label={t('customers.nameAr')} value={value.nameAr ?? ''} onChange={(e) => onChange({ ...value, nameAr: e.target.value })} dir="rtl" />
      </div>
      <div className="grid-2">
        <TextField label={t('customers.phone')} value={value.phone ?? ''} onChange={(e) => onChange({ ...value, phone: e.target.value })} dir="ltr" inputMode="tel" help={t('customers.phoneHelp')} />
        <SelectField label={t('customers.kind')} value={value.kind ?? 'individual'} onChange={(e) => onChange({ ...value, kind: e.target.value as 'individual' | 'company' })}>
          <option value="individual">{t('customers.kinds.individual')}</option><option value="company">{t('customers.kinds.company')}</option>
        </SelectField>
      </div>
      <div className="grid-2">
        <TextField label={t('customers.vat')} value={value.vatNumber ?? ''} onChange={(e) => onChange({ ...value, vatNumber: e.target.value })} dir="ltr" />
        <TextField label={t('customers.email')} value={value.email ?? ''} onChange={(e) => onChange({ ...value, email: e.target.value })} dir="ltr" type="email" />
      </div>
    </>
  );
}
