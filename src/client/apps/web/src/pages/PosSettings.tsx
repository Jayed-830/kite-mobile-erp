import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type PosPolicy, type PrinterInput } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, Checkbox, DataTable, Modal, PageHeader, SelectField, TextField } from '@kite/ui';
import { ErrorBox, useApi, useAuth, workBranchId } from '../core';

/** POS policy (returns, refunds, rounding, blind close) and the shop's network printers. */
export function PosSettingsPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t } = useI18n();
  const policy = useQuery({ queryKey: ['posPolicy'], queryFn: () => api.posPolicy() });
  const [p, setP] = useState<PosPolicy | null>(null);
  useEffect(() => { if (policy.data) setP(policy.data); }, [policy.data]);
  const save = useMutation({ mutationFn: (x: PosPolicy) => api.savePosPolicy({ ...x, version: x.version || null }), onSuccess: (x) => { setP(x); void qc.invalidateQueries({ queryKey: ['posPolicy'] }); } });
  const manage = can(me, 'POS.POLICY.MANAGE');
  return (
    <div className="stack">
      <PageHeader title={t('posSettings.title')} />
      <ErrorBox error={policy.error} />
      {p && (
        <Card className="stack">
          <h2>{t('posSettings.policy')}</h2>
          <div className="grid-2">
            <TextField label={t('posSettings.returnDays')} value={String(p.returnWindowDays)} onChange={(e) => setP({ ...p, returnWindowDays: Number(e.target.value.replace(/\D/g, '') || 0) })} inputMode="numeric" dir="ltr" disabled={!manage} />
            <TextField label={t('posSettings.refundCap')} value={String(p.cashRefundCap)} onChange={(e) => setP({ ...p, cashRefundCap: Number(e.target.value) || 0 })} inputMode="decimal" dir="ltr" disabled={!manage} />
            <TextField label={t('posSettings.variance')} value={String(p.varianceThreshold)} onChange={(e) => setP({ ...p, varianceThreshold: Number(e.target.value) || 0 })} inputMode="decimal" dir="ltr" disabled={!manage} />
            <SelectField label={t('posSettings.cashRounding')} value={String(p.cashRounding)} onChange={(e) => setP({ ...p, cashRounding: Number(e.target.value) })} disabled={!manage}>
              {[0, 0.005, 0.01, 0.05, 0.1].map((x) => <option key={x} value={String(x)}>{x === 0 ? t('posSettings.noRounding') : x.toFixed(3)}</option>)}
            </SelectField>
            <SelectField label={t('posSettings.negativeStock')} value={p.negativeStock} onChange={(e) => setP({ ...p, negativeStock: e.target.value as 'block' | 'allow' })} disabled={!manage}>
              <option value="block">{t('posSettings.block')}</option><option value="allow">{t('posSettings.allow')}</option>
            </SelectField>
            <SelectField label={t('posSettings.rounding')} value={p.roundingLevel} onChange={(e) => setP({ ...p, roundingLevel: e.target.value as 'line' | 'invoice' })} disabled={!manage}>
              <option value="line">{t('posSettings.perLine')}</option><option value="invoice">{t('posSettings.perInvoice')}</option>
            </SelectField>
          </div>
          <Checkbox label={t('posSettings.blind')} checked={p.blindClose} onChange={(e) => setP({ ...p, blindClose: e.target.checked })} disabled={!manage} />
          <div className="grid-2">
            <TextField label={t('posSettings.footerEn')} value={p.receiptFooterEn ?? ''} onChange={(e) => setP({ ...p, receiptFooterEn: e.target.value })} disabled={!manage} />
            <TextField label={t('posSettings.footerAr')} value={p.receiptFooterAr ?? ''} onChange={(e) => setP({ ...p, receiptFooterAr: e.target.value })} dir="rtl" disabled={!manage} />
          </div>
          {save.isSuccess && <Alert tone="success">{t('app.saved')}</Alert>}
          <ErrorBox error={save.error} />
          {manage && <div className="row"><Button variant="primary" busy={save.isPending} onClick={() => save.mutate(p)}>{t('app.save')}</Button></div>}
        </Card>
      )}
      <Printers />
    </div>
  );
}

function Printers() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t } = useI18n();
  const printers = useQuery({ queryKey: ['printers'], queryFn: () => api.printers() });
  const [form, setForm] = useState<PrinterInput | null>(null);
  const save = useMutation({ mutationFn: (f: PrinterInput) => api.createPrinter(f), onSuccess: async () => { setForm(null); await qc.invalidateQueries({ queryKey: ['printers'] }); } });
  const test = useMutation({ mutationFn: (id: string) => api.printJob(id, { kind: 'test' }), onSettled: () => qc.invalidateQueries({ queryKey: ['printers'] }) });
  const branchId = workBranchId(me);
  return (
    <Card className="stack">
      <div className="row"><h2>{t('posSettings.printers')}</h2><span className="spacer" />
        {can(me, 'TERMINALS.APPROVE') && <Button onClick={() => { save.reset(); setForm({ branchId, name: '', host: '', port: 9100, paperWidthMm: 80, hasDrawer: true }); }}>{t('posSettings.addPrinter')}</Button>}
      </div>
      <p className="muted">{t('posSettings.printersHelp')}</p>
      <ErrorBox error={printers.error ?? test.error} />
      {test.isSuccess && <Alert tone="success">{t('posSettings.testSent')}</Alert>}
      <DataTable rows={printers.data ?? []} rowKey={(p) => p.id} empty={t('app.none')}
        columns={[
          { key: 'n', header: t('posSettings.printerName'), render: (p) => p.name },
          { key: 'h', header: t('posSettings.address'), render: (p) => <span className="ltr">{p.host}:{p.port}</span> },
          { key: 'w', header: t('posSettings.paper'), render: (p) => `${p.paperWidthMm} mm` },
          { key: 'r', header: t('posSettings.lastResult'), render: (p) => p.lastResult ? <Badge tone={p.lastResult === 'ok' ? 'success' : 'danger'}>{p.lastResult}</Badge> : '—' },
          { key: 'a', header: t('app.actions'), render: (p) => <Button size="sm" busy={test.isPending} onClick={() => test.mutate(p.id)}>{t('posSettings.test')}</Button> },
        ]} />
      {form && (
        <Modal open title={t('posSettings.addPrinter')} onClose={() => setForm(null)}
          footer={<><Button onClick={() => setForm(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={save.isPending} onClick={() => save.mutate(form)}>{t('app.save')}</Button></>}>
          <TextField label={t('posSettings.printerName')} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          <div className="grid-2">
            <TextField label={t('posSettings.host')} value={form.host} onChange={(e) => setForm({ ...form, host: e.target.value })} dir="ltr" placeholder="192.168.1.50" />
            <TextField label={t('posSettings.port')} value={String(form.port ?? 9100)} onChange={(e) => setForm({ ...form, port: Number(e.target.value.replace(/\D/g, '') || 9100) })} dir="ltr" />
          </div>
          <SelectField label={t('posSettings.paper')} value={String(form.paperWidthMm ?? 80)} onChange={(e) => setForm({ ...form, paperWidthMm: Number(e.target.value) as 58 | 80 })}>
            <option value="80">80 mm</option><option value="58">58 mm</option>
          </SelectField>
          <Checkbox label={t('posSettings.drawer')} checked={form.hasDrawer ?? true} onChange={(e) => setForm({ ...form, hasDrawer: e.target.checked })} />
          <ErrorBox error={save.error} />
        </Modal>
      )}
    </Card>
  );
}
