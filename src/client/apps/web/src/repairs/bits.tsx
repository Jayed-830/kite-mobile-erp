import { useEffect, useRef, useState } from 'react';
import type { Customer, RepairStatus } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Badge, Button, TextField } from '@kite/ui';
import { ErrorBox, useApi } from '../core';

const TONE: Record<RepairStatus, 'default' | 'success' | 'warning' | 'danger' | 'primary'> = {
  received: 'default', inspection_pending: 'default', under_diagnosis: 'primary', waiting_approval: 'warning', waiting_parts: 'warning', in_progress: 'primary',
  testing: 'primary', completed: 'success', ready: 'success', delivered: 'default', cancelled: 'danger', unrepairable: 'danger',
};

export function RepairStatusBadge({ status }: { status: RepairStatus }) {
  const { t } = useI18n();
  return <Badge tone={TONE[status]}>{t(`repair.statuses.${status}`)}</Badge>;
}

/**
 * A signature drawn with a finger, pen or mouse, kept as a PNG data URL (the server accepts PNG, JPEG or WebP and stores its
 * SHA-256). Empty until something is drawn.
 */
export function SignaturePad({ label, onChange }: { label: string; onChange: (dataUrl: string | null) => void }) {
  const { t } = useI18n();
  const ref = useRef<HTMLCanvasElement>(null);
  const drawing = useRef(false);
  const [drawn, setDrawn] = useState(false);
  const ctx = () => ref.current?.getContext('2d') ?? null;
  useEffect(() => {
    const c = ref.current;
    if (!c) return;
    c.width = c.clientWidth || 480;
    c.height = c.clientHeight || 160;
    const g = ctx();
    if (g) { g.lineWidth = 2.5; g.lineCap = 'round'; g.strokeStyle = '#111'; }
  }, []);
  const at = (e: React.PointerEvent<HTMLCanvasElement>) => { const r = e.currentTarget.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top] as const; };
  const down = (e: React.PointerEvent<HTMLCanvasElement>) => { drawing.current = true; const g = ctx(); const [x, y] = at(e); g?.beginPath(); g?.moveTo(x, y); e.currentTarget.setPointerCapture?.(e.pointerId); };
  const moveTo = (e: React.PointerEvent<HTMLCanvasElement>) => { if (!drawing.current) return; const g = ctx(); const [x, y] = at(e); g?.lineTo(x, y); g?.stroke(); setDrawn(true); };
  const up = () => {
    if (!drawing.current) return;
    drawing.current = false;
    const url = ref.current?.toDataURL('image/png');
    if (url && url.startsWith('data:image/png')) { setDrawn(true); onChange(url); }
  };
  const clear = () => { const c = ref.current; ctx()?.clearRect(0, 0, c?.width ?? 0, c?.height ?? 0); setDrawn(false); onChange(null); };
  return (
    <div className="field">
      <span className="label">{label}</span>
      <canvas ref={ref} className="sig-pad" aria-label={label} role="img" onPointerDown={down} onPointerMove={moveTo} onPointerUp={up} onPointerLeave={up} />
      {drawn && <div><Button size="sm" variant="ghost" onClick={clear}>{t('repair.intake.clear')}</Button></div>}
    </div>
  );
}

/** Reads a chosen photo as a data URL (JPEG/PNG/WebP up to 3 MB; the server checks again). */
export function PhotoButton({ label, onPhoto }: { label: string; onPhoto: (dataUrl: string, name: string) => void }) {
  return (
    <label className="btn btn-sm">
      {label}
      <input type="file" accept="image/png,image/jpeg,image/webp" capture="environment" hidden onChange={(e) => {
        const f = e.target.files?.[0];
        if (!f) return;
        const rd = new FileReader();
        rd.onload = () => onPhoto(String(rd.result), f.name);
        rd.readAsDataURL(f);
        e.target.value = '';
      }} />
    </label>
  );
}

/** Find a customer by name or phone, or add one with a mobile number. */
export function CustomerPicker({ value, onChange }: { value: Customer | null; onChange: (c: Customer | null) => void }) {
  const api = useApi();
  const { t } = useI18n();
  const [q, setQ] = useState('');
  const [found, setFound] = useState<Customer[]>([]);
  const [adding, setAdding] = useState(false);
  const [name, setName] = useState('');
  const [phone, setPhone] = useState('');
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (q.trim().length < 2) { setFound([]); return; }
    const h = setTimeout(() => { api.customers(q).then(setFound).catch(setError); }, 200);
    return () => clearTimeout(h);
  }, [q, api]);
  if (value) return (
    <div className="row">
      <strong>{value.nameEn}</strong> <span className="ltr muted">{value.phoneE164 ?? ''}</span>
      <Button size="sm" variant="ghost" onClick={() => onChange(null)}>{t('app.edit')}</Button>
    </div>
  );
  return (
    <div className="stack">
      <ErrorBox error={error} />
      <TextField label={t('tradein.customer.search')} value={q} onChange={(e) => setQ(e.target.value)} dir="auto" autoFocus />
      {found.length > 0 && (
        <div className="picker-list" role="listbox">
          {found.map((c) => <button key={c.id} type="button" className="picker-item" role="option" aria-selected={false} onClick={() => onChange(c)}>{c.nameEn} <span className="ltr muted">{c.phoneE164 ?? ''}</span></button>)}
        </div>
      )}
      {!adding ? <div><Button size="sm" onClick={() => setAdding(true)}>{t('tradein.customer.new')}</Button></div> : (
        <div className="row">
          <TextField label={t('repair.intake.customerName')} value={name} onChange={(e) => setName(e.target.value)} dir="auto" />
          <TextField label={t('repair.intake.customerPhone')} value={phone} onChange={(e) => setPhone(e.target.value)} className="ltr" inputMode="tel" />
          <Button variant="primary" onClick={() => api.createCustomer({ nameEn: name.trim(), phone: phone.trim() || null, kind: 'individual' }).then((c) => { setAdding(false); onChange(c); }).catch(setError)}>{t('app.save')}</Button>
        </div>
      )}
    </div>
  );
}
