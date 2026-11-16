import { useEffect, useMemo, useState } from 'react';
import { ApiError, type ApprovalAction, type Customer, type CustomerInput, type PosItem, type TradeInDetail, type TradeInForm } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Checkbox, Modal, SelectField, Steps, TextField } from '@kite/ui';
import { classifyCode, evaluateInspection } from '@kite/pos';
import { ErrorBox, useApi } from '../core';
import { CustomerFields } from '../pages/Customers';
import { fmt, parseAmount } from '../money';

export type SettleChoice = 'sale' | 'store_credit' | 'cash';
const STEPS = ['customer', 'device', 'inspection', 'valuation', 'consent', 'settlement', 'done'] as const;

/** Opens a PDF the server made (trade-in document) in a new tab. */
export async function openPdf(blob: Promise<Blob>) {
  const url = URL.createObjectURL(await blob);
  window.open(url, '_blank');
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

/**
 * The trade-in wizard at the till (12.4): the selling customer, the device (IMEI checked against the blocked list and head
 * office), the inspection with suggested deductions, the value (a manager approves above the limit or the guide), the
 * customer's consent, and how the value is settled: as a payment for this sale, as store credit, or in cash.
 */
export function TradeInWizard({ cartId, cartCustomer, cartHasItems, onClose, onAccepted, ask }: {
  cartId: string; cartCustomer: Customer | null; cartHasItems: boolean; onClose: () => void;
  onAccepted: (d: TradeInDetail, kind: SettleChoice, customer: Customer) => void;
  ask: (action: ApprovalAction, retry: (approvalId: string) => void) => void;
}) {
  const api = useApi();
  const { t, pick } = useI18n();
  const [step, setStep] = useState(0);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  // customer
  const [customer, setCustomer] = useState<Customer | null>(cartCustomer);
  const [q, setQ] = useState('');
  const [found, setFound] = useState<Customer[]>([]);
  const [creating, setCreating] = useState<CustomerInput | null>(null);
  const [civilId, setCivilId] = useState('');
  const [sellerVat, setSellerVat] = useState('');
  // device
  const [modelQ, setModelQ] = useState('');
  const [models, setModels] = useState<PosItem[]>([]);
  const [model, setModel] = useState<PosItem | null>(null);
  const [code, setCode] = useState('');
  const [detail, setDetail] = useState<TradeInDetail | null>(null);
  const [form, setForm] = useState<TradeInForm | null>(null);
  const [headOffice, setHeadOffice] = useState<boolean | null>(null);
  // inspection and value
  const [answers, setAnswers] = useState<Record<string, string | null>>({});
  const [cloudLock, setCloudLock] = useState(false);
  const [gradeId, setGradeId] = useState('');
  const [condition, setCondition] = useState<'used' | 'damaged'>('used');
  const [chosen, setChosen] = useState<string[] | null>(null);
  const [agreed, setAgreed] = useState('');
  const [approvalId, setApprovalId] = useState<string | null>(null);
  // consent and settlement
  const [method, setMethod] = useState<'on_screen' | 'paper'>('on_screen');
  const [consentName, setConsentName] = useState('');
  const [reference, setReference] = useState('');
  const [settle, setSettle] = useState<SettleChoice>(cartHasItems ? 'sale' : 'store_credit');

  useEffect(() => {
    if (q.trim().length < 2) { setFound([]); return; }
    const h = setTimeout(() => { api.customers(q).then(setFound).catch(setError); }, 200);
    return () => clearTimeout(h);
  }, [q, api]);
  useEffect(() => {
    if (modelQ.trim().length < 2) { setModels([]); return; }
    const h = setTimeout(() => { api.posItems(modelQ, undefined, 20).then((xs) => setModels(xs.filter((x) => x.productType === 'device' && x.tracking !== 'none'))).catch(setError); }, 200);
    return () => clearTimeout(h);
  }, [modelQ, api]);

  const ti = detail?.tradeIn;
  const items = form?.items ?? [];
  const inspection = useMemo(() => evaluateInspection(items, answers, 'used'), [items, answers]);
  // A failed checklist item (or a percentage under its minimum) suggests the deduction tied to it.
  const suggested = useMemo(() => new Set((form?.deductions ?? []).filter((d) => d.checklistCode && inspection.failed.includes(d.checklistCode)).map((d) => d.code)), [form, inspection]);
  const deductions = chosen ?? [...suggested];
  const run = async (f: () => Promise<void>) => { setBusy(true); setError(null); try { await f(); } catch (e) { setError(e); } finally { setBusy(false); } };
  const keep = (d: TradeInDetail) => { setDetail(d); if (d.form) setForm(d.form); };

  const next = () => run(async () => {
    switch (STEPS[step]) {
      case 'customer': {
        let c = customer;
        if (creating) { c = await api.createCustomer(creating); setCustomer(c); setCreating(null); }
        if (!c) return;
        if (!c.phoneE164) throw new ApiError({ status: 400, code: 'POS-016', title: t('tradein.customer.noPhone') });
        if (detail) await api.updateTradeIn(detail.tradeIn.id, { version: detail.tradeIn.version, civilId: civilId || null, sellerVatNumber: sellerVat || null }).then(keep);
        setStep(1); return;
      }
      case 'device': {
        if (!model) throw new ApiError({ status: 400, code: 'VAL-001', title: t('tradein.device.pickModel') });
        if (!detail) {
          const k = classifyCode(code);
          const r = await api.createTradeIn({ customerId: customer!.id, device: { variantId: model.variantId, imei1: model.tracking === 'imei' ? k.value : null, serial: model.tracking === 'serial' ? k.value : null, entry: 'scan' },
            civilId: civilId.trim() || null, sellerVatNumber: sellerVat.trim() || null, saleId: cartId });
          keep(r.detail); setHeadOffice(r.headOfficeChecked);
          return;   // stay to show what head office and the blocked list said; Next goes on to the inspection
        }
        setStep(2); return;
      }
      case 'inspection': {
        const battery = items.find((i) => i.kind === 'percent');
        const d = await api.updateTradeIn(ti!.id, { version: ti!.version, gradeId: gradeId || null, condition, inspection: answers, cloudLockRemoved: cloudLock,
          batteryHealth: battery && answers[battery.code] ? Number(answers[battery.code]) : null, deductions });
        keep(d);
        if (!agreed && d.tradeIn.offeredValue != null) setAgreed(String(d.tradeIn.offeredValue));
        setStep(3); return;
      }
      case 'valuation': {
        const v = parseAmount(agreed);
        if (v == null) throw new ApiError({ status: 400, code: 'VAL-001', title: t('tradein.valuation.agreedHelp') });
        keep(await api.updateTradeIn(ti!.id, { version: ti!.version, agreedValue: v }));
        setStep(4); return;
      }
      case 'consent': setStep(5); return;
      case 'settlement': await accept(); return;
    }
  });

  const accept = async (settleApproval?: string) => {
    const kind = settle;
    try {
      const d = await api.acceptTradeIn(ti!.id, {
        version: ti!.version, agreedValue: parseAmount(agreed)!, approvalId,
        consent: { method, name: method === 'on_screen' ? consentName : null, reference: method === 'paper' ? reference : null },
        settle: kind === 'sale' ? null : { kind, approvalId: settleApproval ?? null }, saleId: cartId,
      });
      keep(d); setStep(6);
      onAccepted(d, kind, customer!);
    } catch (e) {
      if (e instanceof ApiError && e.code === 'TRD-002') ask('TRADEIN.APPROVE', (id) => { setApprovalId(id); void run(async () => { await accept(settleApproval); }); });
      else if (e instanceof ApiError && e.code === 'AUTH-403' && kind !== 'sale') ask(kind === 'cash' ? 'TRADEIN.PAYOUT' : 'TRADEIN.STORE_CREDIT', (id) => void run(async () => { await accept(id); }));
      else throw e;
    }
  };

  const canNext = (() => {
    switch (STEPS[step]) {
      case 'customer': return !!(customer || creating?.nameEn.trim()) && (!form?.requireCivilId || civilId.trim().length >= 6 || !!ti?.civilId);
      case 'device': return !!model && (!!detail || code.trim().length > 0);
      case 'inspection': return inspection.complete && cloudLock && !!gradeId;
      case 'valuation': return parseAmount(agreed) != null;
      case 'consent': return method === 'on_screen' ? consentName.trim().length > 1 : reference.trim().length > 0;
      default: return true;
    }
  })();
  const needsApproval = ti?.needsApproval === true && !approvalId;

  return (
    <Modal open title={ti ? `${t('tradein.one')} ${ti.number}` : t('tradein.one')} onClose={onClose}
      footer={step === 6
        ? <><Button onClick={() => void openPdf(api.tradeInDocument(ti!.id))}>{t('tradein.done.document')}</Button><Button variant="primary" onClick={onClose}>{t('app.close')}</Button></>
        : <>
            <Button onClick={onClose}>{t('app.cancel')}</Button>
            {step > 0 && <Button onClick={() => setStep(step - 1)} disabled={busy}>{t('app.back')}</Button>}
            <Button variant="primary" busy={busy} disabled={!canNext} onClick={() => void next()}>{STEPS[step] === 'settlement' ? t('tradein.settlement.accept') : STEPS[step] === 'device' && !detail ? t('tradein.device.check') : t('app.next')}</Button>
          </>}>
      <Steps steps={STEPS.slice(0, 6).map((s) => t(`tradein.steps.${s}`))} current={Math.min(step, 5)} />

      {STEPS[step] === 'customer' && (
        <div className="stack">
          <p className="muted">{t('tradein.customer.intro')}</p>
          {creating ? <CustomerFields value={creating} onChange={setCreating} /> : customer ? (
            <div className="row"><strong>{customer.nameEn}</strong><span className="ltr muted">{customer.phoneE164 ?? ''}</span>
              {!detail && <Button size="sm" variant="ghost" onClick={() => setCustomer(null)}>{t('app.edit')}</Button>}</div>
          ) : (
            <>
              <TextField label={t('tradein.customer.search')} value={q} onChange={(e) => setQ(e.target.value)} autoFocus />
              <div className="picker-list">{found.map((c) => <button key={c.id} className="picker-item" onClick={() => setCustomer(c)}><span>{c.nameEn}</span><span className="ltr muted">{c.phoneE164 ?? ''}</span></button>)}</div>
              <Button size="sm" onClick={() => setCreating({ nameEn: q, kind: 'individual' })}>{t('tradein.customer.new')}</Button>
            </>
          )}
          {customer && !customer.phoneE164 && <Alert tone="warning">{t('tradein.customer.noPhone')}</Alert>}
          <div className="grid-2">
            <TextField label={t('tradein.customer.civilId')} help={t('tradein.customer.civilIdHelp')} value={civilId} onChange={(e) => setCivilId(e.target.value)} dir="ltr" autoComplete="off" placeholder={ti?.civilId ?? ''} />
            <TextField label={t('tradein.customer.sellerVat')} help={t('tradein.customer.sellerVatHelp')} value={sellerVat} onChange={(e) => setSellerVat(e.target.value)} dir="ltr" />
          </div>
        </div>
      )}

      {STEPS[step] === 'device' && (
        <div className="stack">
          {model ? <div className="row"><strong>{pick(model.descriptionEn, model.descriptionAr)}</strong><span className="ltr muted">{model.sku}</span>
            {!detail && <Button size="sm" variant="ghost" onClick={() => setModel(null)}>{t('app.edit')}</Button>}</div> : (
            <>
              <TextField label={t('tradein.device.modelSearch')} value={modelQ} onChange={(e) => setModelQ(e.target.value)} autoFocus />
              <div className="picker-list">{models.map((m) => <button key={m.variantId} className="picker-item" onClick={() => setModel(m)}><span>{pick(m.descriptionEn, m.descriptionAr)}</span><span className="ltr muted">{m.sku}</span></button>)}</div>
            </>
          )}
          {model && <TextField label={model.tracking === 'serial' ? t('tradein.device.serial') : t('tradein.device.imei')} value={detail ? (ti?.imei1 ?? ti?.serial ?? '') : code} disabled={!!detail}
            onChange={(e) => setCode(e.target.value)} dir="ltr" autoComplete="off" inputMode={model.tracking === 'imei' ? 'numeric' : undefined} autoFocus />}
          {detail && <Alert tone={headOffice ? 'success' : 'warning'}>{headOffice ? t('tradein.device.headOffice') : t('tradein.device.headOfficeOffline')}</Alert>}
        </div>
      )}

      {STEPS[step] === 'inspection' && form && (
        <div className="stack">
          <p className="muted">{t('tradein.inspection.intro')}</p>
          <div className="checklist">
            {items.map((i) => (
              <div key={i.code} className="checklist-item">
                <span className="grow">{pick(i.labelEn, i.labelAr)}</span>
                {i.kind === 'pass_fail' && (
                  <div className="segmented" role="group" aria-label={pick(i.labelEn, i.labelAr)}>
                    {(['pass', 'fail'] as const).map((v) => (
                      <button key={v} type="button" aria-pressed={answers[i.code] === v} className={`seg${answers[i.code] === v ? ` active ${v}` : ''}`}
                        onClick={() => { setAnswers({ ...answers, [i.code]: v }); if (i.code === 'cloud_lock') setCloudLock(v === 'pass'); }}>{t(`devices.${v}`)}</button>
                    ))}
                  </div>
                )}
                {i.kind === 'percent' && <TextField label={`%${i.minPercent != null ? ` (≥ ${i.minPercent})` : ''}`} value={answers[i.code] ?? ''} inputMode="numeric" dir="ltr"
                  onChange={(e) => setAnswers({ ...answers, [i.code]: e.target.value.replace(/\D/g, '').slice(0, 3) || null })} />}
                {i.kind === 'text' && <TextField label={t('devices.note')} value={answers[i.code] ?? ''} onChange={(e) => setAnswers({ ...answers, [i.code]: e.target.value || null })} />}
              </div>
            ))}
          </div>
          <Checkbox label={t('tradein.inspection.cloudLock')} checked={cloudLock} onChange={(e) => setCloudLock(e.target.checked)} />
          {!cloudLock && <p className="muted">{t('tradein.inspection.cloudLockHelp')}</p>}
          <div className="grid-2">
            <SelectField label={t('tradein.inspection.grade')} value={gradeId} onChange={(e) => setGradeId(e.target.value)}>
              <option value="">—</option>
              {form.grades.map((g) => <option key={g.id} value={g.id}>{g.code} · {pick(g.nameEn, g.nameAr)} · {g.guideValue != null ? fmt(g.guideValue) : t('tradein.inspection.noGuide')}</option>)}
            </SelectField>
            <SelectField label={t('tradein.inspection.condition')} value={condition} onChange={(e) => setCondition(e.target.value as 'used' | 'damaged')}>
              <option value="used">{t('tradein.inspection.conditions.used')}</option><option value="damaged">{t('tradein.inspection.conditions.damaged')}</option>
            </SelectField>
          </div>
          <h3>{t('tradein.inspection.deductions')}</h3>
          <div className="stack tight">
            {form.deductions.map((d) => (
              <Checkbox key={d.code} checked={deductions.includes(d.code)}
                label={<>{pick(d.nameEn, d.nameAr)} <span className="muted">({d.kind === 'percent' ? `${d.value}%` : fmt(d.value)})</span>{suggested.has(d.code) && <> <Badge tone="warning">{t('tradein.inspection.suggested')}</Badge></>}</>}
                onChange={(e) => setChosen(e.target.checked ? [...deductions, d.code] : deductions.filter((x) => x !== d.code))} />
            ))}
          </div>
        </div>
      )}

      {STEPS[step] === 'valuation' && ti && (
        <div className="stack">
          <div className="figures">
            <div className="fig"><span>{t('tradein.valuation.guide')}</span><span className="num">{fmt(ti.guideValue)}</span></div>
            {detail!.issues.map((x) => <div key={x.code} className="fig"><span>− {pick(x.nameEn, x.nameAr)}</span><span className="num">{fmt(x.amount)}</span></div>)}
            <div className="fig strong"><span>{t('tradein.valuation.offer')}</span><span className="num">{fmt(ti.offeredValue)}</span></div>
          </div>
          <TextField label={t('tradein.valuation.agreed')} help={t('tradein.valuation.agreedHelp')} value={agreed} onChange={(e) => { setAgreed(e.target.value); setApprovalId(null); }} inputMode="decimal" dir="ltr" autoFocus />
          {form && !form.canApprove && (() => {
            const v = parseAmount(agreed);
            const over = v != null && (ti.offeredValue == null || v > form.staffLimit || v > Math.round(ti.offeredValue * (100 + form.overGuidePercent) * 10) / 1000);
            if (!over) return null;
            return approvalId ? <Alert tone="success">{t('tradein.valuation.approved')}</Alert> : (
              <Alert tone="warning">
                {ti.offeredValue == null ? t('tradein.valuation.approvalNoGuide') : t('tradein.valuation.approval', { limit: fmt(form.staffLimit), percent: String(form.overGuidePercent) })}{' '}
                <Button size="sm" onClick={() => ask('TRADEIN.APPROVE', (id) => setApprovalId(id))}>{t('tradein.valuation.getApproval')}</Button>
              </Alert>
            );
          })()}
          {needsApproval && !form && <Alert tone="warning">{t('tradein.valuation.approvalNoGuide')}</Alert>}
        </div>
      )}

      {STEPS[step] === 'consent' && form && (
        <div className="stack">
          <p className="muted">{t('tradein.consent.intro')}</p>
          <div className="declaration"><strong>{t('tradein.consent.declaration')}</strong><p>{form.declarationEn}</p><p dir="rtl">{form.declarationAr}</p></div>
          <SelectField label={t('tradein.consent.method')} value={method} onChange={(e) => setMethod(e.target.value as 'on_screen' | 'paper')}>
            <option value="on_screen">{t('tradein.consent.methods.on_screen')}</option><option value="paper">{t('tradein.consent.methods.paper')}</option>
          </SelectField>
          {method === 'on_screen'
            ? <TextField label={t('tradein.consent.name')} value={consentName} onChange={(e) => setConsentName(e.target.value)} autoFocus />
            : <TextField label={t('tradein.consent.reference')} value={reference} onChange={(e) => setReference(e.target.value)} dir="ltr" autoFocus />}
        </div>
      )}

      {STEPS[step] === 'settlement' && ti && (
        <div className="stack">
          <p className="muted">{t('tradein.settlement.intro')} <strong className="num">{fmt(parseAmount(agreed))}</strong></p>
          {(['sale', 'store_credit', 'cash'] as const).map((k) => (
            <label key={k} className={`choice${settle === k ? ' active' : ''}`}>
              <input type="radio" name="settle" checked={settle === k} onChange={() => setSettle(k)} />
              <span><strong>{t(k === 'sale' ? 'tradein.settlement.sale' : k === 'store_credit' ? 'tradein.settlement.storeCredit' : 'tradein.settlement.cash')}</strong>
                <small className="muted" style={{ display: 'block' }}>{t(k === 'sale' ? (cartHasItems ? 'tradein.settlement.saleHelp' : 'tradein.settlement.saleEmpty') : k === 'store_credit' ? 'tradein.settlement.storeCreditHelp' : 'tradein.settlement.cashHelp')}</small></span>
            </label>
          ))}
        </div>
      )}

      {STEPS[step] === 'done' && ti && (
        <div className="stack">
          <Alert tone="success">{t('tradein.done.title', { number: ti.number })}</Alert>
          <p>{t('tradein.done.unit')}</p>
          {ti.reconnected && <p>{t('tradein.device.reconnect')}</p>}
          {ti.selfInvoiceNumber && <p>{t('tradein.done.selfInvoice', { number: ti.selfInvoiceNumber })}</p>}
          {settle === 'sale' && <p>{t('tradein.done.inCart')}</p>}
          {settle === 'store_credit' && <p>{t('tradein.done.storeCredit', { amount: fmt(ti.agreedValue) })}</p>}
          {settle === 'cash' && <Alert tone="info">{t('tradein.done.cash', { amount: fmt(ti.agreedValue) })}</Alert>}
        </div>
      )}
      <ErrorBox error={error} />
    </Modal>
  );
}
