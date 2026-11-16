import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type ProductInput, type ProductType, type ProductView } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Badge, Button, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { ErrorBox, formatDate, useApi, useAuth } from '../core';
import { fmt, parseAmount } from '../money';

const TYPES: ProductType[] = ['accessory', 'spare_part', 'consumable', 'service', 'device'];

interface Draft { nameEn: string; nameAr: string; productType: ProductType; taxCodeId: string; warranty: string; sku: string; barcode: string; price: string; minPrice: string }

/** Catalog (9.3): products and variants, barcodes and effective-dated prices. */
export function CatalogPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick, lang } = useI18n();
  const [q, setQ] = useState('');
  const [type, setType] = useState('');
  const [draft, setDraft] = useState<Draft | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const list = useQuery({ queryKey: ['products', q, type], queryFn: () => api.products(q || undefined, type || undefined) });
  const taxCodes = useQuery({ queryKey: ['taxCodes'], queryFn: () => api.taxCodes(), enabled: can(me, 'SETTINGS.VIEW') });
  const create = useMutation({
    mutationFn: (d: Draft) => {
      const body: ProductInput = {
        nameEn: d.nameEn.trim(), nameAr: d.nameAr.trim(), productType: d.productType, taxCodeId: d.taxCodeId, defaultWarrantyDays: Number(d.warranty || 0),
        tracking: d.productType === 'device' ? 'imei' : 'none',
        variants: [{ sku: d.sku.trim(), barcodes: d.barcode.trim() ? [d.barcode.trim()] : [], price: parseAmount(d.price), minPrice: parseAmount(d.minPrice) }],
      };
      return api.createProduct(body);
    },
    onSuccess: async () => { setDraft(null); await qc.invalidateQueries({ queryKey: ['products'] }); },
  });
  const typeName = (x: string) => t(`catalog.types.${x}`);
  const firstPrice = (p: ProductView) => p.variants[0]?.variant.price ?? null;

  return (
    <div className="stack">
      <PageHeader title={t('catalog.title')}>
        {can(me, 'CATALOG.MANAGE') && (
          <Button variant="primary" onClick={() => { create.reset(); setDraft({ nameEn: '', nameAr: '', productType: 'accessory', taxCodeId: taxCodes.data?.find((c) => c.code === 'VAT-STD')?.id ?? '', warranty: '0', sku: '', barcode: '', price: '', minPrice: '' }); }}>
            {t('catalog.new')}
          </Button>
        )}
      </PageHeader>
      <div className="row">
        <TextField label={t('app.search')} value={q} onChange={(e) => setQ(e.target.value)} placeholder={t('catalog.searchHint')} />
        <SelectField label={t('catalog.type')} value={type} onChange={(e) => setType(e.target.value)}>
          <option value="">{t('catalog.allTypes')}</option>
          {TYPES.map((x) => <option key={x} value={x}>{typeName(x)}</option>)}
        </SelectField>
      </div>
      <ErrorBox error={list.error} />
      {list.isLoading ? <Spinner label={t('app.loading')} /> : (
        <DataTable rows={list.data ?? []} rowKey={(p) => p.product.id} empty={t('catalog.empty')}
          columns={[
            { key: 'n', header: t('catalog.name'), render: (p) => <button className="link-button" onClick={() => setOpen(p.product.id)}>{pick(p.product.nameEn, p.product.nameAr)}</button> },
            { key: 't', header: t('catalog.type'), render: (p) => <span className="row">{typeName(p.product.productType)}{p.product.tracking !== 'none' && <Badge tone="warning">{t('catalog.serialized')}</Badge>}</span> },
            { key: 's', header: t('catalog.sku'), render: (p) => <span className="ltr">{p.variants.map((v) => v.variant.sku).join(', ')}</span> },
            { key: 'b', header: t('catalog.barcode'), render: (p) => <span className="ltr muted">{p.variants.flatMap((v) => v.barcodes.map((b) => b.code)).join(', ') || '—'}</span> },
            { key: 'p', header: t('catalog.price'), render: (p) => <span className="num">{fmt(firstPrice(p))}</span> },
            { key: 'st', header: t('app.status'), render: (p) => <Badge tone={p.product.status === 'active' ? 'success' : 'default'}>{t(`app.${p.product.status}`)}</Badge> },
          ]} />
      )}
      {draft && (
        <Modal open title={t('catalog.new')} onClose={() => setDraft(null)}
          footer={<><Button onClick={() => setDraft(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={create.isPending} onClick={() => create.mutate(draft)}>{t('app.save')}</Button></>}>
          <div className="grid-2">
            <TextField label={t('catalog.nameEn')} value={draft.nameEn} onChange={(e) => setDraft({ ...draft, nameEn: e.target.value })} required dir="ltr" />
            <TextField label={t('catalog.nameAr')} value={draft.nameAr} onChange={(e) => setDraft({ ...draft, nameAr: e.target.value })} required dir="rtl" />
          </div>
          <div className="grid-2">
            <SelectField label={t('catalog.type')} value={draft.productType} onChange={(e) => setDraft({ ...draft, productType: e.target.value as ProductType })}>
              {TYPES.map((x) => <option key={x} value={x}>{typeName(x)}</option>)}
            </SelectField>
            <SelectField label={t('catalog.taxCode')} value={draft.taxCodeId} onChange={(e) => setDraft({ ...draft, taxCodeId: e.target.value })}>
              {taxCodes.data?.filter((c) => c.status === 'active').map((c) => <option key={c.id} value={c.id}>{c.code} · {pick(c.nameEn, c.nameAr)}</option>)}
            </SelectField>
          </div>
          {draft.productType === 'device' && <p className="muted">{t('catalog.deviceNote')}</p>}
          <div className="grid-2">
            <TextField label={t('catalog.sku')} value={draft.sku} onChange={(e) => setDraft({ ...draft, sku: e.target.value.toUpperCase() })} required dir="ltr" />
            <TextField label={t('catalog.barcode')} value={draft.barcode} onChange={(e) => setDraft({ ...draft, barcode: e.target.value })} dir="ltr" help={t('catalog.barcodeHelp')} />
          </div>
          <div className="grid-2">
            <TextField label={t('catalog.price')} value={draft.price} onChange={(e) => setDraft({ ...draft, price: e.target.value })} inputMode="decimal" dir="ltr" help={t('catalog.priceHelp')} />
            <TextField label={t('catalog.minPrice')} value={draft.minPrice} onChange={(e) => setDraft({ ...draft, minPrice: e.target.value })} inputMode="decimal" dir="ltr" />
          </div>
          <TextField label={t('catalog.warranty')} value={draft.warranty} onChange={(e) => setDraft({ ...draft, warranty: e.target.value.replace(/\D/g, '') })} inputMode="numeric" dir="ltr" />
          <ErrorBox error={create.error} />
        </Modal>
      )}
      {open && <ProductModal id={open} onClose={() => setOpen(null)} lang={lang} />}
    </div>
  );
}

function ProductModal({ id, onClose, lang }: { id: string; onClose: () => void; lang: 'en' | 'ar' }) {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const p = useQuery({ queryKey: ['product', id], queryFn: () => api.product(id) });
  const [price, setPrice] = useState<{ variantId: string; price: string; minPrice: string; from: string } | null>(null);
  const [barcode, setBarcode] = useState<{ variantId: string; code: string } | null>(null);
  const refresh = async () => { await qc.invalidateQueries({ queryKey: ['product', id] }); await qc.invalidateQueries({ queryKey: ['products'] }); };
  const savePrice = useMutation({ mutationFn: (x: NonNullable<typeof price>) => api.setPrice(x.variantId, { price: parseAmount(x.price) ?? -1, minPrice: parseAmount(x.minPrice), effectiveFrom: x.from || null }), onSuccess: async () => { setPrice(null); await refresh(); } });
  const addBarcode = useMutation({ mutationFn: (x: NonNullable<typeof barcode>) => api.addBarcode(x.variantId, x.code.trim()), onSuccess: async () => { setBarcode(null); await refresh(); } });
  const removeBarcode = useMutation({ mutationFn: (bid: string) => api.removeBarcode(bid), onSuccess: refresh });
  return (
    <Modal open title={p.data ? pick(p.data.product.nameEn, p.data.product.nameAr) : t('app.loading')} onClose={onClose} footer={<Button onClick={onClose}>{t('app.close')}</Button>}>
      {p.isLoading && <Spinner label={t('app.loading')} />}
      <ErrorBox error={p.error ?? removeBarcode.error} />
      {p.data?.variants.map(({ variant: v, barcodes }) => (
        <div key={v.id} className="card stack">
          <div className="row"><strong className="ltr">{v.sku}</strong><span className="spacer" /><span className="num">{fmt(v.price)}</span>{v.minPrice != null && <span className="muted">{t('catalog.min')} {fmt(v.minPrice)}</span>}</div>
          {v.priceFrom && <span className="muted">{t('catalog.priceFrom', { date: formatDate(v.priceFrom, lang) })}</span>}
          <div className="row">{barcodes.map((b) => <span key={b.id} className="badge ltr">{b.code}{can(me, 'CATALOG.MANAGE') && <button className="chip-x" aria-label={t('catalog.removeBarcode')} onClick={() => removeBarcode.mutate(b.id)}>×</button>}</span>)}</div>
          <div className="row">
            {can(me, 'CATALOG.PRICE.MANAGE') && <Button size="sm" onClick={() => { savePrice.reset(); setPrice({ variantId: v.id, price: v.price?.toString() ?? '', minPrice: v.minPrice?.toString() ?? '', from: '' }); }}>{t('catalog.changePrice')}</Button>}
            {can(me, 'CATALOG.MANAGE') && <Button size="sm" onClick={() => { addBarcode.reset(); setBarcode({ variantId: v.id, code: '' }); }}>{t('catalog.addBarcode')}</Button>}
          </div>
          {price?.variantId === v.id && (
            <div className="stack">
              <div className="grid-2">
                <TextField label={t('catalog.price')} value={price.price} onChange={(e) => setPrice({ ...price, price: e.target.value })} inputMode="decimal" dir="ltr" />
                <TextField label={t('catalog.minPrice')} value={price.minPrice} onChange={(e) => setPrice({ ...price, minPrice: e.target.value })} inputMode="decimal" dir="ltr" />
              </div>
              <TextField label={t('catalog.effectiveFrom')} type="date" value={price.from} onChange={(e) => setPrice({ ...price, from: e.target.value })} help={t('catalog.effectiveHelp')} />
              <div className="row"><Button variant="primary" size="sm" busy={savePrice.isPending} onClick={() => savePrice.mutate(price)}>{t('app.save')}</Button><Button size="sm" onClick={() => setPrice(null)}>{t('app.cancel')}</Button></div>
              <ErrorBox error={savePrice.error} />
            </div>
          )}
          {barcode?.variantId === v.id && (
            <div className="row">
              <TextField label={t('catalog.barcode')} value={barcode.code} onChange={(e) => setBarcode({ ...barcode, code: e.target.value })} dir="ltr" autoFocus />
              <Button variant="primary" size="sm" busy={addBarcode.isPending} onClick={() => addBarcode.mutate(barcode)}>{t('app.save')}</Button>
              <ErrorBox error={addBarcode.error} />
            </div>
          )}
        </div>
      ))}
    </Modal>
  );
}
