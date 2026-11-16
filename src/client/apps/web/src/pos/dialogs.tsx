import { useEffect, useMemo, useState } from 'react';
import { ApiError, type ApprovalAction, type Customer, type PosPolicy, type SaleDetail, type ShiftFigures, type TenderInput } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Badge, Button, Checkbox, Modal, SelectField, TextField } from '@kite/ui';
import { apportionReturn, cashDue, countTotal, fromBaisa, roundCash, toBaisa, type Baisa } from '@kite/pos';
import { ErrorBox, useApi } from '../core';
import { CustomerFields } from '../pages/Customers';
import { fmt, fmtB, qty } from '../money';

const b2n = (b: Baisa) => Number(fromBaisa(b));

// ------------------------------------------------------------------ manager approval (PIN on this terminal, or remote)
export function ApprovalDialog({ action, reason, onApproved, onClose }: { action: ApprovalAction; reason?: string; onApproved: (id: string) => void; onClose: () => void }) {
  const api = useApi();
  const { t } = useI18n();
  const [mode, setMode] = useState<'pin' | 'remote'>('pin');
  const [username, setUsername] = useState('');
  const [pin, setPin] = useState('');
  const [why, setWhy] = useState(reason ?? '');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [waiting, setWaiting] = useState<string | null>(null);
  useEffect(() => {
    if (!waiting) return;
    const timer = setInterval(async () => {
      try {
        const s = await api.approvalStatus(waiting);
        if (s.status === 'approved') { clearInterval(timer); onApproved(waiting); }
        else if (s.status !== 'pending') { clearInterval(timer); setWaiting(null); setError(new ApiError({ status: 409, code: 'POS-008', title: t(`pos.approvalStatus.${s.status}`) })); }
      } catch (e) { setError(e); }
    }, 1500);
    return () => clearInterval(timer);
  }, [waiting, api, onApproved, t]);
  const submit = async () => {
    setBusy(true); setError(null);
    try {
      if (mode === 'pin') onApproved((await api.pinApproval({ action, approverUsername: username, pin, reason: why || undefined })).id);
      else setWaiting((await api.requestApproval({ action, reason: why || undefined })).id);
    } catch (e) { setError(e); } finally { setBusy(false); }
  };
  return (
    <Modal open title={t('pos.approvalTitle')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button>{!waiting && <Button variant="primary" busy={busy} onClick={submit}>{mode === 'pin' ? t('pos.approve') : t('pos.askManager')}</Button>}</>}>
      <p><Badge tone="warning">{t(`approvals.actions.${action.replace(/\./g, '_')}`)}</Badge></p>
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={mode === 'pin'} className={`tab${mode === 'pin' ? ' active' : ''}`} onClick={() => setMode('pin')}>{t('pos.managerPin')}</button>
        <button role="tab" aria-selected={mode === 'remote'} className={`tab${mode === 'remote' ? ' active' : ''}`} onClick={() => setMode('remote')}>{t('pos.remote')}</button>
      </div>
      <TextField label={t('approvals.reason')} value={why} onChange={(e) => setWhy(e.target.value)} />
      {mode === 'pin' ? (
        <div className="grid-2">
          <TextField label={t('pos.managerUsername')} value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" dir="ltr" />
          <TextField label={t('pos.pin')} type="password" inputMode="numeric" value={pin} onChange={(e) => setPin(e.target.value.replace(/\D/g, ''))} autoComplete="off" dir="ltr" />
        </div>
      ) : waiting ? <p className="muted" role="status">{t('pos.waitingManager')}</p> : <p className="muted">{t('pos.remoteHelp')}</p>}
      <ErrorBox error={error} />
    </Modal>
  );
}

// ------------------------------------------------------------------ payment
export function PayDialog({ gross, policy, onPay, onClose, busy, error }: {
  gross: Baisa; policy: PosPolicy; busy: boolean; error: unknown; onClose: () => void; onPay: (tenders: TenderInput[]) => void;
}) {
  const { t } = useI18n();
  const [card, setCard] = useState('');
  const [cardTerminal, setCardTerminal] = useState('BANK-1');
  const [approvalCode, setApprovalCode] = useState('');
  const [transfer, setTransfer] = useState('');
  const [reference, setReference] = useState('');
  const [tendered, setTendered] = useState('');
  const amount = (s: string) => (/^\d+(\.\d{1,3})?$/.test(s.trim()) ? toBaisa(s.trim()) : 0n);
  const nonCash = amount(card) + amount(transfer);
  const { due } = cashDue(gross, nonCash > gross ? gross : nonCash, policy.cashRounding);
  const given = tendered.trim() ? amount(tendered) : due;
  const change = given - due;
  const valid = nonCash <= gross && (due === 0n || given >= due) && (amount(card) === 0n || (cardTerminal.trim() && approvalCode.trim())) && (amount(transfer) === 0n || reference.trim());
  const pay = () => {
    const tenders: TenderInput[] = [];
    if (amount(card) > 0n) tenders.push({ method: 'card', amount: b2n(amount(card)), cardTerminal: cardTerminal.trim(), approvalCode: approvalCode.trim() });
    if (amount(transfer) > 0n) tenders.push({ method: 'bank_transfer', amount: b2n(amount(transfer)), reference: reference.trim() });
    if (due > 0n || tenders.length === 0) tenders.push({ method: 'cash', amount: b2n(due), tendered: b2n(given) });
    onPay(due === 0n && tenders.length > 1 ? tenders.filter((x) => x.method !== 'cash') : tenders.filter((x) => x.amount > 0 || tenders.length === 1));
  };
  const quick = [due, ...[1000n, 5000n, 10000n, 20000n, 50000n].map((n) => (due <= 0n ? 0n : ((due + n - 1n) / n) * n))].filter((v, i, a) => v > 0n && a.indexOf(v) === i).slice(0, 5);
  return (
    <Modal open title={t('pos.payTitle')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" busy={busy} disabled={!valid} onClick={pay}>{t('pos.complete')}</Button></>}>
      <div className="pay-total"><span>{t('pos.total')}</span><strong className="num">{fmtB(gross)}</strong></div>
      <div className="grid-2">
        <TextField label={t('pos.card')} value={card} onChange={(e) => setCard(e.target.value)} inputMode="decimal" dir="ltr" placeholder="0.000" />
        <Button onClick={() => setCard(fromBaisa(gross))}>{t('pos.allOnCard')}</Button>
      </div>
      {amount(card) > 0n && (
        <div className="grid-2">
          <TextField label={t('pos.bankTerminal')} value={cardTerminal} onChange={(e) => setCardTerminal(e.target.value.toUpperCase())} dir="ltr" />
          <TextField label={t('pos.approvalCode')} value={approvalCode} onChange={(e) => setApprovalCode(e.target.value)} dir="ltr" help={t('pos.approvalCodeHelp')} />
        </div>
      )}
      <div className="grid-2">
        <TextField label={t('pos.transfer')} value={transfer} onChange={(e) => setTransfer(e.target.value)} inputMode="decimal" dir="ltr" placeholder="0.000" />
        {amount(transfer) > 0n && <TextField label={t('pos.reference')} value={reference} onChange={(e) => setReference(e.target.value)} dir="ltr" />}
      </div>
      <div className="pay-cash">
        <div className="pay-total"><span>{t('pos.cashDue')}</span><strong className="num">{fmtB(due)}</strong></div>
        {due > 0n && (
          <>
            <TextField label={t('pos.tendered')} value={tendered} onChange={(e) => setTendered(e.target.value)} inputMode="decimal" dir="ltr" placeholder={fromBaisa(due)} autoFocus />
            <div className="row">{quick.map((v) => <Button key={String(v)} size="sm" onClick={() => setTendered(fromBaisa(v))}>{fmtB(v)}</Button>)}</div>
            <div className="pay-total change"><span>{t('pos.change')}</span><strong className="num">{fmtB(change > 0n ? change : 0n)}</strong></div>
          </>
        )}
        {nonCash > gross && <p className="neg">{t('pos.tooMuch')}</p>}
        {roundCash(gross - nonCash, policy.cashRounding) !== gross - nonCash && due > 0n && <p className="muted">{t('pos.roundingNote', { step: String(policy.cashRounding) })}</p>}
      </div>
      <ErrorBox error={error} />
    </Modal>
  );
}

// ------------------------------------------------------------------ customer
export function CustomerDialog({ onPick, onClose }: { onPick: (c: Customer) => void; onClose: () => void }) {
  const api = useApi();
  const { t } = useI18n();
  const [q, setQ] = useState('');
  const [list, setList] = useState<Customer[]>([]);
  const [creating, setCreating] = useState<{ nameEn: string; kind: 'individual' | 'company'; phone?: string; vatNumber?: string } | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (q.trim().length < 2) { setList([]); return; }
    const h = setTimeout(() => { api.customers(q).then(setList).catch(setError); }, 200);
    return () => clearTimeout(h);
  }, [q, api]);
  const create = async () => { try { onPick(await api.createCustomer(creating!)); } catch (e) { setError(e); } };
  return (
    <Modal open title={t('pos.customer')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button>{creating ? <Button variant="primary" onClick={create}>{t('app.save')}</Button> : <Button onClick={() => setCreating({ nameEn: q, kind: 'individual' })}>{t('customers.new')}</Button>}</>}>
      {creating ? <CustomerFields value={creating} onChange={setCreating} /> : (
        <>
          <TextField label={t('app.search')} value={q} onChange={(e) => setQ(e.target.value)} placeholder={t('customers.searchHint')} autoFocus />
          <div className="picker-list">{list.map((c) => <button key={c.id} className="picker-item" onClick={() => onPick(c)}><span>{c.nameEn}</span><span className="ltr muted">{c.phoneE164 ?? ''}</span><span className="ltr muted">{c.vatNumber ?? ''}</span></button>)}</div>
        </>
      )}
      <ErrorBox error={error} />
    </Modal>
  );
}

// ------------------------------------------------------------------ cash in / out
export function CashDialog({ onDone, onClose, onApproval }: { onDone: (kind: string) => void; onClose: () => void; onApproval: (action: ApprovalAction, retry: (approvalId: string) => void) => void }) {
  const api = useApi();
  const { t } = useI18n();
  const [kind, setKind] = useState<'pay_in' | 'pay_out' | 'drop' | 'no_sale'>('pay_in');
  const [amount, setAmount] = useState('');
  const [reason, setReason] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const send = async (approvalId?: string) => {
    setBusy(true); setError(null);
    try {
      await api.cashEvent({ kind, amount: kind === 'no_sale' ? 0 : Number(amount || 0), reason, approvalId });
      onDone(kind);
    } catch (e) {
      if (e instanceof ApiError && e.code === 'AUTH-403' && (kind === 'pay_out' || kind === 'no_sale')) onApproval(kind === 'pay_out' ? 'POS.CASH.PAY_OUT' : 'POS.DRAWER.NO_SALE', (id) => void send(id));
      else setError(e);
    } finally { setBusy(false); }
  };
  return (
    <Modal open title={t('pos.cashTitle')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" busy={busy} onClick={() => void send()}>{t('app.save')}</Button></>}>
      <SelectField label={t('pos.cashKind')} value={kind} onChange={(e) => setKind(e.target.value as typeof kind)}>
        {(['pay_in', 'pay_out', 'drop', 'no_sale'] as const).map((k) => <option key={k} value={k}>{t(`pos.cashKinds.${k}`)}</option>)}
      </SelectField>
      {kind !== 'no_sale' && <TextField label={t('pos.amount')} value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" dir="ltr" />}
      <TextField label={t('approvals.reason')} value={reason} onChange={(e) => setReason(e.target.value)} required />
      <ErrorBox error={error} />
    </Modal>
  );
}

// ------------------------------------------------------------------ returns
export function ReturnDialog({ policy, onDone, onClose, onApproval }: {
  policy: PosPolicy; onDone: (creditId: string) => void; onClose: () => void; onApproval: (action: ApprovalAction, retry: (approvalId: string) => void) => void;
}) {
  const api = useApi();
  const { t, pick } = useI18n();
  const [number, setNumber] = useState('');
  const [sale, setSale] = useState<SaleDetail | null>(null);
  const [picked, setPicked] = useState<Record<string, { qty: string; restock: boolean }>>({});
  const [method, setMethod] = useState<'cash' | 'card'>('cash');
  const [cardTerminal, setCardTerminal] = useState('BANK-1');
  const [reason, setReason] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const find = async () => { setError(null); try { const s = await api.saleByNumber(number); setSale(s); setPicked({}); } catch (e) { setError(e); } };
  const refundGross = useMemo(() => {
    if (!sale) return 0n;
    let total = 0n;
    for (const l of sale.lines) {
      const p = picked[l.id];
      if (!p || !/^\d+(\.\d{1,3})?$/.test(p.qty) || toBaisa(p.qty) <= 0n) continue;
      try {
        total += apportionReturn({ qty: toBaisa(l.qty), net: toBaisa(l.net), tax: toBaisa(l.tax), gross: toBaisa(l.gross), discount: toBaisa(l.discount),
          returnedQty: toBaisa(l.returnedQty), returnedNet: toBaisa(l.returnedNet ?? 0), returnedTax: toBaisa(l.returnedTax ?? 0), returnedGross: toBaisa(l.returnedGross ?? 0), returnedDiscount: toBaisa(l.returnedDiscount ?? 0) }, toBaisa(p.qty)).gross;
      } catch { /* shown by the server on submit */ }
    }
    return total;
  }, [sale, picked]);
  const refund = method === 'cash' ? roundCash(refundGross, policy.cashRounding) : refundGross;
  const submit = async (approvalId?: string) => {
    if (!sale) return;
    setBusy(true); setError(null);
    const id = crypto.randomUUID();
    try {
      const lines = sale.lines.filter((l) => picked[l.id] && Number(picked[l.id]!.qty) > 0).map((l) => ({ lineId: l.id, qty: Number(picked[l.id]!.qty), restock: picked[l.id]!.restock }));
      const r = await api.returnSale(sale.sale.id, { id, lines, reason, approvalId, refunds: refund === 0n ? [] : [method === 'cash' ? { method: 'cash', amount: b2n(refund) } : { method: 'card', amount: b2n(refund), cardTerminal }] });
      onDone(r.id);
    } catch (e) {
      if (e instanceof ApiError && (e.code === 'POS-009' || e.code === 'POS-012')) onApproval('POS.REFUND', (aid) => void submit(aid));
      else setError(e);
    } finally { setBusy(false); }
  };
  return (
    <Modal open title={t('pos.returnTitle')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button>{sale && <Button variant="primary" busy={busy} disabled={refundGross === 0n || !reason.trim()} onClick={() => void submit()}>{t('pos.refund')} {fmtB(refund)}</Button>}</>}>
      <div className="row">
        <TextField label={t('pos.receiptNumber')} value={number} onChange={(e) => setNumber(e.target.value)} dir="ltr" onKeyDown={(e) => { if (e.key === 'Enter') void find(); }} autoFocus />
        <Button onClick={() => void find()}>{t('app.search')}</Button>
      </div>
      {sale && (
        <>
          {sale.lines.map((l) => (
            <div key={l.id} className="row line-editor">
              <span className="grow">{pick(l.descriptionEn, l.descriptionAr)} <span className="muted">· {qty(l.qty)} × {fmt(l.unitPrice)}</span></span>
              {l.returnable > 0 ? (
                <>
                  <TextField label={t('pos.returnQty', { max: qty(l.returnable) })} value={picked[l.id]?.qty ?? ''} inputMode="decimal" dir="ltr"
                    onChange={(e) => setPicked({ ...picked, [l.id]: { qty: e.target.value, restock: picked[l.id]?.restock ?? l.productType !== 'service' } })} />
                  {l.productType !== 'service' && <Checkbox label={t('pos.restock')} checked={picked[l.id]?.restock ?? true} onChange={(e) => setPicked({ ...picked, [l.id]: { qty: picked[l.id]?.qty ?? '', restock: e.target.checked } })} />}
                </>
              ) : <Badge>{t('pos.allReturned')}</Badge>}
            </div>
          ))}
          <div className="grid-2">
            <SelectField label={t('pos.refundMethod')} value={method} onChange={(e) => setMethod(e.target.value as 'cash' | 'card')}>
              <option value="cash">{t('pos.cash')}</option><option value="card">{t('pos.card')}</option>
            </SelectField>
            {method === 'card' && <TextField label={t('pos.bankTerminal')} value={cardTerminal} onChange={(e) => setCardTerminal(e.target.value.toUpperCase())} dir="ltr" />}
          </div>
          <TextField label={t('approvals.reason')} value={reason} onChange={(e) => setReason(e.target.value)} required />
        </>
      )}
      <ErrorBox error={error} />
    </Modal>
  );
}

// ------------------------------------------------------------------ shift open and close
export function DenominationCount({ denominations, value, onChange }: { denominations: number[]; value: Record<string, number>; onChange: (v: Record<string, number>) => void }) {
  const { t } = useI18n();
  let total = 0n;
  try { total = countTotal(value, denominations); } catch { total = 0n; }
  return (
    <div className="stack">
      <div className="denoms">
        {denominations.map((d) => (
          <label key={d} className="denom">
            <span className="ltr">{d >= 1 ? d.toFixed(0) : d.toFixed(3)}</span>
            <input className="input" inputMode="numeric" dir="ltr" aria-label={`${t('pos.pieces')} ${d}`} value={value[String(d)] ?? ''}
              onChange={(e) => { const v = e.target.value.replace(/\D/g, ''); const next = { ...value }; if (v) next[String(d)] = Number(v); else delete next[String(d)]; onChange(next); }} />
          </label>
        ))}
      </div>
      <div className="pay-total"><span>{t('pos.countedTotal')}</span><strong className="num">{fmtB(total)}</strong></div>
    </div>
  );
}

export function CloseShiftDialog({ policy, figures, onClosed, onClose, onApproval }: {
  policy: PosPolicy; figures: ShiftFigures | undefined; onClosed: (r: { figures: ShiftFigures; counted: number; variance: number; number: string }) => void; onClose: () => void;
  onApproval: (action: ApprovalAction, retry: (approvalId: string) => void) => void;
}) {
  const api = useApi();
  const { t } = useI18n();
  const [count, setCount] = useState<Record<string, number>>({});
  const terminals = figures?.cardByTerminal.filter((c) => c.total !== 0).map((c) => c.cardTerminal) ?? [];
  const [batches, setBatches] = useState<Record<string, { total: string; reason: string }>>({});
  const [reason, setReason] = useState('');
  const [needsReason, setNeedsReason] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const submit = async (approvalId?: string) => {
    setBusy(true); setError(null);
    try {
      const r = await api.closeShift({ closingCount: count, varianceReason: reason || null, approvalId,
        cardSettlements: terminals.map((c) => ({ cardTerminal: c, batchTotal: Number(batches[c]?.total || 0), reason: batches[c]?.reason || null })) });
      onClosed(r);
    } catch (e) {
      if (e instanceof ApiError && e.code === 'POS-014') {
        if (!reason.trim()) { setNeedsReason(true); setError(e); }
        else onApproval('POS.SHIFT.ACCEPT_VARIANCE', (aid) => void submit(aid));
      } else setError(e);
    } finally { setBusy(false); }
  };
  return (
    <Modal open title={t('pos.closeTitle')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" busy={busy} onClick={() => void submit()}>{t('pos.closeShift')}</Button></>}>
      <p className="muted">{policy.blindClose ? t('pos.blindHelp') : t('pos.countHelp')}</p>
      <DenominationCount denominations={policy.denominations} value={count} onChange={setCount} />
      {terminals.length > 0 && <h3>{t('pos.batches')}</h3>}
      {terminals.map((c) => (
        <div key={c} className="grid-2">
          <TextField label={t('pos.batchTotal', { terminal: c })} value={batches[c]?.total ?? ''} onChange={(e) => setBatches({ ...batches, [c]: { total: e.target.value, reason: batches[c]?.reason ?? '' } })} inputMode="decimal" dir="ltr" />
          <TextField label={t('pos.batchReason')} value={batches[c]?.reason ?? ''} onChange={(e) => setBatches({ ...batches, [c]: { total: batches[c]?.total ?? '', reason: e.target.value } })} />
        </div>
      ))}
      {needsReason && <TextField label={t('pos.varianceReason')} value={reason} onChange={(e) => setReason(e.target.value)} autoFocus />}
      <ErrorBox error={error} />
    </Modal>
  );
}
