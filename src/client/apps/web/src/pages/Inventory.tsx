import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type PosItem, type ReasonCode } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Checkbox, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi, useAuth } from '../core';
import { fmt, parseAmount, qty } from '../money';

type Tab = 'stock' | 'adjust' | 'counts' | 'exceptions';
const REASONS: ReasonCode[] = ['damaged', 'lost', 'found', 'expired', 'correction', 'other'];

/** Inventory (9.3, 16.3): stock on hand at weighted-average cost, adjustments and opening stock, counts, exceptions. */
export function InventoryPage() {
  const { t } = useI18n();
  const { me } = useAuth();
  const [tab, setTab] = useState<Tab>('stock');
  const tabs: [Tab, string, boolean][] = [
    ['stock', t('inventory.stock'), true], ['adjust', t('inventory.adjust'), can(me, 'INV.ADJUST')],
    ['counts', t('inventory.counts'), can(me, 'INV.COUNT')], ['exceptions', t('inventory.exceptions'), true],
  ];
  return (
    <div className="stack">
      <PageHeader title={t('inventory.title')} />
      <div className="tabs" role="tablist">
        {tabs.filter((x) => x[2]).map(([k, label]) => <button key={k} role="tab" aria-selected={tab === k} className={`tab${tab === k ? ' active' : ''}`} onClick={() => setTab(k)}>{label}</button>)}
      </div>
      {tab === 'stock' && <StockTab />}
      {tab === 'adjust' && <AdjustTab onDone={() => setTab('stock')} />}
      {tab === 'counts' && <CountsTab />}
      {tab === 'exceptions' && <ExceptionsTab />}
    </div>
  );
}

function StockTab() {
  const api = useApi();
  const { t, pick, lang } = useI18n();
  const [q, setQ] = useState('');
  const [low, setLow] = useState(false);
  const rows = useQuery({ queryKey: ['balances', q, low], queryFn: () => api.balances({ q: q || undefined, lowOnly: low }) });
  const check = useMutation({ mutationFn: () => api.inventoryCheck() });
  return (
    <div className="stack">
      <div className="row">
        <TextField label={t('app.search')} value={q} onChange={(e) => setQ(e.target.value)} />
        <Checkbox label={t('inventory.lowOnly')} checked={low} onChange={(e) => setLow(e.target.checked)} />
        <span className="spacer" />
        <Button busy={check.isPending} onClick={() => check.mutate()}>{t('inventory.check')}</Button>
      </div>
      {check.data && (check.data.ok ? <Alert tone="success">{t('inventory.checkOk')}</Alert> : <Alert tone="error">{t('inventory.checkFailed')}</Alert>)}
      <ErrorBox error={rows.error ?? check.error} />
      {rows.isLoading ? <Spinner label={t('app.loading')} /> : (
        <DataTable rows={rows.data ?? []} rowKey={(r) => `${r.warehouseId}-${r.variantId}`} empty={t('inventory.empty')}
          columns={[
            { key: 'i', header: t('catalog.name'), render: (r) => pick(r.descriptionEn, r.descriptionAr) },
            { key: 's', header: t('catalog.sku'), render: (r) => <span className="ltr">{r.sku}</span> },
            { key: 'b', header: t('users.branch'), render: (r) => <span className="ltr">{r.branchCode} · {r.warehouseCode}</span> },
            { key: 'q', header: t('inventory.qty'), render: (r) => <span className={`num${r.qty < 0 ? ' neg' : ''}`}>{qty(r.qty)}</span> },
            { key: 'a', header: t('inventory.avgCost'), render: (r) => <span className="num">{fmt(Math.round(r.avgCost * 1000) / 1000)}</span> },
            { key: 'v', header: t('inventory.value'), render: (r) => <span className="num">{fmt(r.value)}</span> },
            { key: 'l', header: t('inventory.lastMovement'), render: (r) => formatDateTime(r.lastMovementAt, lang) },
          ]} />
      )}
    </div>
  );
}

/** Picks an item by barcode, SKU or name (the same lookup as the POS). */
export function ItemPicker({ onPick, label }: { onPick: (i: PosItem) => void; label: string }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const [q, setQ] = useState('');
  const branchId = me?.user.defaultBranchId ?? me?.branches[0]?.id;
  const hits = useQuery({ queryKey: ['posItems', q, branchId], queryFn: () => api.posItems(q, branchId, 8), enabled: q.trim().length >= 2 });
  return (
    <div className="stack picker">
      <TextField label={label} value={q} onChange={(e) => setQ(e.target.value)} placeholder={t('catalog.searchHint')} />
      {hits.data && q.trim().length >= 2 && (
        <div className="picker-list">
          {hits.data.map((i) => <button key={i.variantId} className="picker-item" onClick={() => { onPick(i); setQ(''); }}><span>{pick(i.descriptionEn, i.descriptionAr)}</span><span className="ltr muted">{i.sku}</span><span className="num">{qty(i.stock)}</span></button>)}
          {hits.data.length === 0 && <span className="muted">{t('app.none')}</span>}
        </div>
      )}
    </div>
  );
}

function AdjustTab({ onDone }: { onDone: () => void }) {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const [kind, setKind] = useState<'opening' | 'adjustment'>('adjustment');
  const [note, setNote] = useState('');
  const [lines, setLines] = useState<{ item: PosItem; qty: string; cost: string; reason: ReasonCode }[]>([]);
  const branchId = me?.user.defaultBranchId ?? me?.branches[0]?.id ?? '';
  const post = useMutation({
    mutationFn: () => api.postAdjustment({
      branchId, kind, note,
      lines: lines.map((l) => ({ variantId: l.item.variantId, qty: Number(l.qty), unitCost: kind === 'opening' || Number(l.qty) > 0 ? parseAmount(l.cost) : null, reasonCode: kind === 'opening' ? 'opening' : l.reason })),
    }),
    onSuccess: async () => { setLines([]); setNote(''); await qc.invalidateQueries({ queryKey: ['balances'] }); onDone(); },
  });
  return (
    <div className="card stack">
      <div className="grid-2">
        <SelectField label={t('inventory.kind')} value={kind} onChange={(e) => setKind(e.target.value as 'opening' | 'adjustment')}>
          <option value="adjustment">{t('inventory.kindAdjustment')}</option>
          <option value="opening">{t('inventory.kindOpening')}</option>
        </SelectField>
        <TextField label={t('inventory.note')} value={note} onChange={(e) => setNote(e.target.value)} />
      </div>
      <p className="muted">{kind === 'opening' ? t('inventory.openingHelp') : t('inventory.adjustHelp')}</p>
      <ItemPicker label={t('inventory.addItem')} onPick={(item) => setLines((ls) => ls.some((l) => l.item.variantId === item.variantId) ? ls : [...ls, { item, qty: '', cost: '', reason: 'correction' }])} />
      {lines.map((l, i) => (
        <div key={l.item.variantId} className="row line-editor">
          <strong className="grow">{pick(l.item.descriptionEn, l.item.descriptionAr)} <span className="ltr muted">{l.item.sku}</span></strong>
          <TextField label={t('inventory.qty')} value={l.qty} onChange={(e) => setLines(lines.map((x, j) => j === i ? { ...x, qty: e.target.value } : x))} inputMode="decimal" dir="ltr" help={kind === 'adjustment' ? t('inventory.qtyHelp') : undefined} />
          {(kind === 'opening' || Number(l.qty) > 0) && <TextField label={t('inventory.unitCost')} value={l.cost} onChange={(e) => setLines(lines.map((x, j) => j === i ? { ...x, cost: e.target.value } : x))} inputMode="decimal" dir="ltr" />}
          {kind === 'adjustment' && (
            <SelectField label={t('inventory.reason')} value={l.reason} onChange={(e) => setLines(lines.map((x, j) => j === i ? { ...x, reason: e.target.value as ReasonCode } : x))}>
              {REASONS.map((r) => <option key={r} value={r}>{t(`inventory.reasons.${r}`)}</option>)}
            </SelectField>
          )}
          <Button size="sm" variant="ghost" onClick={() => setLines(lines.filter((_, j) => j !== i))}>{t('pos.remove')}</Button>
        </div>
      ))}
      <ErrorBox error={post.error} />
      <div className="row"><Button variant="primary" disabled={lines.length === 0} busy={post.isPending} onClick={() => post.mutate()}>{t('inventory.post')}</Button></div>
    </div>
  );
}

function CountsTab() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const branchId = me?.user.defaultBranchId ?? me?.branches[0]?.id ?? '';
  const counts = useQuery({ queryKey: ['counts'], queryFn: () => api.counts() });
  const [open, setOpen] = useState<string | null>(null);
  const start = useMutation({ mutationFn: () => api.startCount({ branchId }), onSuccess: async (c) => { await qc.invalidateQueries({ queryKey: ['counts'] }); setOpen(c.id); } });
  return (
    <div className="stack">
      <div className="row"><Button variant="primary" busy={start.isPending} onClick={() => start.mutate()}>{t('inventory.startCount')}</Button></div>
      <ErrorBox error={counts.error ?? start.error} />
      <DataTable rows={counts.data ?? []} rowKey={(c) => c.id} empty={t('app.none')}
        columns={[
          { key: 'n', header: t('inventory.number'), render: (c) => <button className="link-button ltr" onClick={() => setOpen(c.id)}>{c.number}</button> },
          { key: 's', header: t('app.status'), render: (c) => <Badge tone={c.status === 'posted' ? 'success' : c.status === 'open' ? 'warning' : 'default'}>{t(`inventory.countStatus.${c.status}`)}</Badge> },
          { key: 'l', header: t('inventory.lines'), render: (c) => c.lines },
          { key: 'd', header: t('inventory.started'), render: (c) => formatDateTime(c.startedAt, lang) },
        ]} />
      {open && <CountModal id={open} onClose={() => setOpen(null)} />}
    </div>
  );
}

function CountModal({ id, onClose }: { id: string; onClose: () => void }) {
  const api = useApi();
  const qc = useQueryClient();
  const { t } = useI18n();
  const c = useQuery({ queryKey: ['count', id], queryFn: () => api.count(id) });
  const [counted, setCounted] = useState<Record<string, string>>({});
  const refresh = async () => { await qc.invalidateQueries({ queryKey: ['count', id] }); await qc.invalidateQueries({ queryKey: ['counts'] }); await qc.invalidateQueries({ queryKey: ['balances'] }); };
  const save = useMutation({ mutationFn: (lines: { variantId: string; countedQty: number }[]) => api.setCountLines(id, lines), onSuccess: refresh });
  const post = useMutation({ mutationFn: () => api.postCount(id), onSuccess: async () => { await refresh(); onClose(); } });
  const open = c.data?.status === 'open';
  return (
    <Modal open title={`${t('inventory.count')} ${c.data?.number ?? ''}`} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.close')}</Button>{open && <Button variant="primary" busy={post.isPending} onClick={() => post.mutate()}>{t('inventory.postCount')}</Button>}</>}>
      {open && <ItemPicker label={t('inventory.addItem')} onPick={(i) => save.mutate([{ variantId: i.variantId, countedQty: 0 }])} />}
      <ErrorBox error={c.error ?? save.error ?? post.error} />
      {c.data?.lines.map((l) => (
        <div key={l.variantId} className="row line-editor">
          <span className="grow">{l.descriptionEn} <span className="ltr muted">{l.sku}</span></span>
          <span className="muted">{t('inventory.system')} {qty(l.systemQty)}</span>
          {open ? (
            <TextField label={t('inventory.counted')} value={counted[l.variantId] ?? String(l.countedQty)} inputMode="decimal" dir="ltr"
              onChange={(e) => setCounted({ ...counted, [l.variantId]: e.target.value })}
              onBlur={() => counted[l.variantId] != null && save.mutate([{ variantId: l.variantId, countedQty: Number(counted[l.variantId]) }])} />
          ) : <span>{t('inventory.counted')} {qty(l.countedQty)}</span>}
          <Badge tone={l.difference === 0 ? 'success' : 'warning'}>{l.difference > 0 ? '+' : ''}{qty(l.difference)}</Badge>
        </div>
      ))}
    </Modal>
  );
}

function ExceptionsTab() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, lang } = useI18n();
  const rows = useQuery({ queryKey: ['stockExceptions'], queryFn: () => api.stockExceptions('open') });
  const [resolve, setResolve] = useState<{ id: string; note: string } | null>(null);
  const done = useMutation({ mutationFn: (r: { id: string; note: string }) => api.resolveException(r.id, r.note), onSuccess: async () => { setResolve(null); await qc.invalidateQueries({ queryKey: ['stockExceptions'] }); } });
  return (
    <div className="stack">
      <p className="muted">{t('inventory.exceptionsHelp')}</p>
      <ErrorBox error={rows.error} />
      <DataTable rows={rows.data ?? []} rowKey={(r) => r.id} empty={t('inventory.noExceptions')}
        columns={[
          { key: 'i', header: t('catalog.name'), render: (r) => <span>{r.descriptionEn} <span className="ltr muted">{r.sku}</span></span> },
          { key: 'q', header: t('inventory.qtyAfter'), render: (r) => <span className="num neg">{qty(r.qtyAfter)}</span> },
          { key: 'd', header: t('inventory.document'), render: (r) => <span className="ltr">{r.sourceNumber ?? r.sourceDocType}</span> },
          { key: 'w', header: t('inventory.when'), render: (r) => formatDateTime(r.createdAt, lang) },
          { key: 'a', header: t('app.actions'), render: (r) => can(me, 'INV.ADJUST') && <Button size="sm" onClick={() => setResolve({ id: r.id, note: '' })}>{t('inventory.resolve')}</Button> },
        ]} />
      {resolve && (
        <Modal open title={t('inventory.resolve')} onClose={() => setResolve(null)}
          footer={<><Button onClick={() => setResolve(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={done.isPending} onClick={() => done.mutate(resolve)}>{t('app.save')}</Button></>}>
          <TextField label={t('inventory.resolution')} value={resolve.note} onChange={(e) => setResolve({ ...resolve, note: e.target.value })} />
          <ErrorBox error={done.error} />
        </Modal>
      )}
    </div>
  );
}
