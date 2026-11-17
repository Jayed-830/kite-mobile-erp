import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { can, type CreditDetail, type CustomerStatement } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, DataTable, Modal, PageHeader, SelectField, Spinner, TextField } from '@kite/ui';
import { businessToday, ErrorBox, formatDate, formatDateTime, useApi, useAuth } from '../core';
import { fmt, parseAmount } from '../money';
import { statusTone } from './CreditPage';

type Dialog = 'pay' | 'hold' | 'release' | 'edit' | 'writeOff' | 'opening' | { allocate: string } | null;

/** One customer's credit account (14): figures, open instalments, payments, holds, statement and head office's actions. */
export function CreditAccountPage() {
  const { customerId = '' } = useParams();
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, lang, pick } = useI18n();
  const [dialog, setDialog] = useState<Dialog>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const q = useQuery({ queryKey: ['creditAccount', customerId], queryFn: () => api.creditAccount(customerId) });
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['creditAccount', customerId] }); void qc.invalidateQueries({ queryKey: ['creditAccounts'] }); void qc.invalidateQueries({ queryKey: ['statement', customerId] }); };
  if (q.isLoading) return <Spinner label={t('app.loading')} />;
  if (!q.data) return <ErrorBox error={q.error} />;
  const d = q.data;
  const a = d.account;
  const name = pick(a.customerName, a.customerNameAr ?? a.customerName);
  const manage = can(me, 'CREDIT.MANAGE') && d.figures.headOffice;
  const done = (m: string) => { setDialog(null); setMsg(m); refresh(); };
  return (
    <div className="stack">
      <PageHeader title={name}>
        <Link className="btn" to="/app/credit">{t('app.back')}</Link>
        {can(me, 'CREDIT.RECEIVE') && <Button variant="primary" onClick={() => setDialog('pay')}>{t('credit.receive')}</Button>}
        {manage && a.status === 'active' && <Button onClick={() => setDialog('hold')}>{t('credit.hold')}</Button>}
        {manage && a.status === 'hold' && <Button onClick={() => setDialog('release')}>{t('credit.release')}</Button>}
        {manage && <Button onClick={() => setDialog('edit')}>{t('credit.edit')}</Button>}
        {manage && <Button onClick={() => setDialog('opening')}>{t('credit.openingBalance')}</Button>}
        {can(me, 'CREDIT.WRITE_OFF') && d.figures.headOffice && d.openItems.length > 0 && <Button variant="danger" onClick={() => setDialog('writeOff')}>{t('credit.writeOff')}</Button>}
      </PageHeader>
      <div className="row">
        <span className="ltr muted">{a.customerNumber}</span>
        <Badge tone={statusTone(a.status)}>{t(`credit.statuses.${a.status}`)}</Badge>
        {a.holdKind && <span className="muted">{t(`credit.holdKinds.${a.holdKind}`)}</span>}
        <span className="muted">{t('credit.termsDays', { n: a.termsDays })}</span>
      </div>
      {a.status === 'hold' && <Alert tone="error">{t('credit.heldBecause', { reason: a.holdReason ?? '' })}</Alert>}
      {d.figures.blocked && <Alert tone="warning">{t('credit.figures.blocked', { n: d.policy.blockOverdueDays })}</Alert>}
      {!d.figures.headOffice && <Alert tone="info">{t('credit.branchCopy', { time: formatDateTime(d.figures.asOf, lang) })}</Alert>}
      {msg && <Alert tone="success">{msg}</Alert>}
      <div className="row figures-row">
        {([['limit', a.creditLimit], ['exposure', d.figures.exposure], ['available', d.figures.available], ['onAccount', d.figures.onAccount]] as const).map(([k, v]) => (
          <div key={k} className="fig stat"><span className="muted">{t(`credit.figures.${k}`)}</span><strong className={`num${k === 'exposure' && v > a.creditLimit ? ' neg' : ''}`}>{fmt(v)}</strong></div>
        ))}
        <div className="fig stat"><span className="muted">{t('credit.figures.overdue')}</span><strong className={`num${d.figures.overdueDays > 0 ? ' neg' : ''}`}>{d.figures.overdueDays}</strong></div>
      </div>

      <Card>
        <h3>{t('credit.openItems')}</h3>
        <DataTable rows={d.openItems} rowKey={(i) => i.id} empty={t('credit.noOpen')}
          columns={[
            { key: 'n', header: t('credit.document'), render: (i) => <span className="ltr">{i.number}</span> },
            { key: 'b', header: t('credit.branch'), render: (i) => <span className="ltr">{i.branchCode}</span> },
            { key: 's', header: t('credit.instalment'), render: (i) => t('credit.of', { seq: i.seq, of: i.of }) },
            { key: 'd', header: t('credit.due'), render: (i) => <span className={i.overdueDays > 0 ? 'neg' : 'muted'}>{formatDate(i.dueDate, lang)}{i.overdueDays > 0 ? ` · ${i.overdueDays}` : ''}</span> },
            { key: 'a', header: t('credit.amount'), render: (i) => <span className="num">{fmt(i.amount)}</span> },
            { key: 'o', header: t('credit.openAmount'), render: (i) => <strong className="num">{fmt(i.open)}</strong> },
          ]} />
      </Card>

      {d.credits.length > 0 && (
        <Card>
          <h3>{t('credit.credits')}</h3>
          <DataTable rows={d.credits} rowKey={(c) => c.id} empty=""
            columns={[
              { key: 'n', header: t('credit.document'), render: (c) => <span className="ltr">{c.number}</span> },
              { key: 'k', header: t('credit.type'), render: (c) => t(`credit.kinds.${c.kind}`) },
              { key: 'b', header: t('credit.branch'), render: (c) => <span className="ltr">{c.branchCode}</span> },
              { key: 'a', header: t('credit.amount'), render: (c) => <span className="num">{fmt(c.amount)}</span> },
              { key: 'l', header: t('credit.leftOver'), render: (c) => <strong className="num">{fmt(c.leftOver)}</strong> },
              { key: 'x', header: '', render: (c) => manage && d.openItems.length > 0 ? <Button size="sm" onClick={() => setDialog({ allocate: c.id })}>{t('credit.allocate')}</Button> : null },
            ]} />
        </Card>
      )}

      <StatementCard customerId={customerId} name={name} />

      <Card>
        <h3>{t('credit.allowances')}</h3>
        <DataTable rows={d.allowances} rowKey={(w) => w.branchId} empty=""
          columns={[
            { key: 'b', header: t('credit.branch'), render: (w) => <span><span className="ltr">{w.code}</span> · {pick(w.nameEn, w.nameAr)}</span> },
            { key: 'a', header: t('credit.amount'), render: (w) => <span className="num">{fmt(w.amount)}</span> },
          ]} />
      </Card>

      {(d.holds.length > 0 || d.breaches.length > 0) && (
        <Card>
          <h3>{t('credit.holds')}</h3>
          <ul className="plain">
            {d.breaches.map((b, i) => <li key={`b${i}`} className="neg">{formatDateTime(b.detectedAt, lang)} · {t('credit.breach', { exposure: fmt(b.exposure), limit: fmt(b.creditLimit), entries: b.entries, branch: b.branchCode ?? '' })}</li>)}
            {d.holds.map((h, i) => (
              <li key={i}><Badge tone={h.action === 'hold' ? 'danger' : 'success'}>{h.action === 'hold' ? t('credit.hold') : t('credit.release')}</Badge> {t(`credit.holdKinds.${h.kind}`)} · <span dir="auto">{h.reason}</span>
                <span className="muted"> · {formatDateTime(h.createdAt, lang)}{h.byName ? ` · ${pick(h.byName, h.byNameAr ?? h.byName)}` : ''}</span></li>
            ))}
          </ul>
        </Card>
      )}

      {dialog === 'pay' && <PaymentDialog detail={d} name={name} onClose={() => setDialog(null)} onDone={done} />}
      {(dialog === 'hold' || dialog === 'release') && <HoldDialog customerId={customerId} name={name} release={dialog === 'release'} onClose={() => setDialog(null)} onDone={() => done(t('credit.saved'))} />}
      {dialog === 'edit' && <EditDialog detail={d} onClose={() => setDialog(null)} onDone={() => done(t('credit.saved'))} />}
      {dialog === 'writeOff' && <WriteOffDialog detail={d} name={name} onClose={() => setDialog(null)} onDone={done} />}
      {dialog === 'opening' && <OpeningDialog customerId={customerId} name={name} onClose={() => setDialog(null)} onDone={done} />}
      {dialog && typeof dialog === 'object' && <AllocateDialog detail={d} creditId={dialog.allocate} onClose={() => setDialog(null)} onDone={() => done(t('credit.saved'))} />}
    </div>
  );
}

/** A payment at the office: a transfer names the branch it was received at (cash and card are taken at the till). */
function PaymentDialog({ detail, name, onClose, onDone }: { detail: CreditDetail; name: string; onClose: () => void; onDone: (m: string) => void }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const branches = (me?.branches ?? []).filter((b) => b.status === 'active');
  const [branchId, setBranchId] = useState(me?.serverBranchId ?? branches[0]?.id ?? '');
  const [amount, setAmount] = useState('');
  const [reference, setReference] = useState('');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const value = parseAmount(amount);
  const submit = async () => {
    setBusy(true); setError(null);
    try {
      const r = await api.receiveCustomerPayment({ id: crypto.randomUUID(), customerId: detail.account.customerId, method: 'bank_transfer', amount: value!, branchId, reference: reference.trim(), note: note.trim() || null });
      onDone(t('credit.received', { number: r.number, amount: fmt(r.amount), balance: fmt(r.balance) }));
    } catch (e) { setError(e); } finally { setBusy(false); }
  };
  return (
    <Modal open title={t('credit.receiveTitle', { name })} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" busy={busy} disabled={!value || value <= 0 || !reference.trim() || !branchId} onClick={submit}>{t('credit.receive')}</Button></>}>
      <div className="stack">
        <p className="muted">{t('credit.till.owedNow', { amount: fmt(detail.figures.balance) })} · {t('credit.oldestFirst')}</p>
        <div className="grid-2">
          <SelectField label={t('credit.method')} value="bank_transfer" onChange={() => undefined}><option value="bank_transfer">{t('credit.methods.bank_transfer')}</option></SelectField>
          <SelectField label={t('credit.branch')} value={branchId} onChange={(e) => setBranchId(e.target.value)}>
            {branches.map((b) => <option key={b.id} value={b.id}>{b.code} · {pick(b.nameEn, b.nameAr)}</option>)}
          </SelectField>
          <TextField label={t('credit.amount')} value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" dir="ltr" placeholder="0.000" autoFocus />
          <TextField label={t('credit.reference')} value={reference} onChange={(e) => setReference(e.target.value)} dir="ltr" />
        </div>
        <TextField label={t('credit.note')} value={note} onChange={(e) => setNote(e.target.value)} dir="auto" />
        <ErrorBox error={error} />
      </div>
    </Modal>
  );
}

function HoldDialog({ customerId, name, release, onClose, onDone }: { customerId: string; name: string; release: boolean; onClose: () => void; onDone: () => void }) {
  const api = useApi();
  const { t } = useI18n();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<unknown>(null);
  const go = () => (release ? api.releaseCredit(customerId, reason.trim()) : api.holdCredit(customerId, reason.trim())).then(onDone).catch(setError);
  return (
    <Modal open title={t(release ? 'credit.releaseTitle' : 'credit.holdTitle', { name })} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant={release ? 'primary' : 'danger'} disabled={!reason.trim()} onClick={go}>{t(release ? 'credit.release' : 'credit.hold')}</Button></>}>
      <div className="stack"><TextField label={t('credit.reason')} value={reason} onChange={(e) => setReason(e.target.value)} dir="auto" autoFocus /><ErrorBox error={error} /></div>
    </Modal>
  );
}

function EditDialog({ detail, onClose, onDone }: { detail: CreditDetail; onClose: () => void; onDone: () => void }) {
  const api = useApi();
  const { t, pick } = useI18n();
  const a = detail.account;
  const [limit, setLimit] = useState(String(a.creditLimit));
  const [terms, setTerms] = useState(String(a.termsDays));
  const [cap, setCap] = useState(a.offlineSaleCap == null ? '' : String(a.offlineSaleCap));
  const [note, setNote] = useState(a.note ?? '');
  const [closed, setClosed] = useState(a.status === 'closed');
  const [allow, setAllow] = useState<Record<string, string>>(Object.fromEntries(detail.allowances.map((w) => [w.branchId, w.amount ? String(w.amount) : ''])));
  const [error, setError] = useState<unknown>(null);
  const total = Object.values(allow).reduce((s, v) => s + (parseAmount(v) ?? 0), 0);
  const save = () => api.updateCreditAccount(a.customerId, {
    customerId: a.customerId, creditLimit: parseAmount(limit) ?? 0, termsDays: Number(terms), offlineSaleCap: cap.trim() ? parseAmount(cap) : null, note: note.trim() || null,
    status: a.status === 'hold' ? null : closed ? 'closed' : 'active', version: a.version,
    allowances: detail.allowances.map((w) => ({ branchId: w.branchId, amount: parseAmount(allow[w.branchId] ?? '') ?? 0 })),
  }).then(onDone).catch(setError);
  return (
    <Modal open title={t('credit.edit')} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={parseAmount(limit) == null || !/^\d{1,3}$/.test(terms)} onClick={save}>{t('credit.save')}</Button></>}>
      <div className="stack">
        <div className="grid-2">
          <TextField label={t('credit.limit')} value={limit} onChange={(e) => setLimit(e.target.value)} inputMode="decimal" dir="ltr" />
          <TextField label={t('credit.terms')} value={terms} onChange={(e) => setTerms(e.target.value.replace(/\D/g, ''))} inputMode="numeric" dir="ltr" />
          <TextField label={t('credit.offlineCap')} help={t('credit.offlineCapHelp')} value={cap} onChange={(e) => setCap(e.target.value)} inputMode="decimal" dir="ltr" />
          <TextField label={t('credit.note')} value={note} onChange={(e) => setNote(e.target.value)} dir="auto" />
        </div>
        <fieldset><legend>{t('credit.allowances')}</legend>
          <div className="grid-2">{detail.allowances.map((w) => <TextField key={w.branchId} label={`${w.code} · ${pick(w.nameEn, w.nameAr)}`} value={allow[w.branchId] ?? ''} onChange={(e) => setAllow({ ...allow, [w.branchId]: e.target.value })} inputMode="decimal" dir="ltr" placeholder="0.000" />)}</div>
          <p className={!detail.policy.allowOvercommit && total > (parseAmount(limit) ?? 0) ? 'neg' : 'muted'}>{t('credit.allowanceTotal', { total: fmt(total), limit: fmt(parseAmount(limit) ?? 0) })}</p>
        </fieldset>
        {a.status !== 'hold' && <label className="check"><input type="checkbox" checked={closed} onChange={(e) => setClosed(e.target.checked)} /> {t('credit.closeAccount')}</label>}
        <ErrorBox error={error} />
      </div>
    </Modal>
  );
}

function WriteOffDialog({ detail, name, onClose, onDone }: { detail: CreditDetail; name: string; onClose: () => void; onDone: (m: string) => void }) {
  const api = useApi();
  const { t } = useI18n();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<unknown>(null);
  const go = () => api.writeOffCredit(detail.account.customerId, { reason: reason.trim() }).then((r) => onDone(t('credit.writtenOff', { total: fmt(r.total), number: r.number }))).catch(setError);
  return (
    <Modal open title={t('credit.writeOffTitle', { name })} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="danger" disabled={!reason.trim()} onClick={go}>{t('credit.writeOff')} {fmt(detail.figures.openItems)}</Button></>}>
      <div className="stack"><Alert tone="warning">{t('credit.writeOffHelp')}</Alert><TextField label={t('credit.reason')} value={reason} onChange={(e) => setReason(e.target.value)} dir="auto" autoFocus /><ErrorBox error={error} /></div>
    </Modal>
  );
}

function OpeningDialog({ customerId, name, onClose, onDone }: { customerId: string; name: string; onClose: () => void; onDone: (m: string) => void }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const branches = (me?.branches ?? []).filter((b) => b.status === 'active');
  const [branchId, setBranchId] = useState(branches[0]?.id ?? '');
  const [amount, setAmount] = useState('');
  const [date, setDate] = useState(businessToday());
  const [error, setError] = useState<unknown>(null);
  const value = Number(amount);
  const ok = /^-?\d+(\.\d{1,3})?$/.test(amount.trim()) && value !== 0;
  const go = () => api.customerOpening(customerId, { branchId, amount: value, date }).then((r) => onDone(`${t('credit.openingBalance')} ${r.number}`)).catch(setError);
  return (
    <Modal open title={t('credit.openingTitle', { name })} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={!ok} onClick={go}>{t('credit.save')}</Button></>}>
      <div className="stack">
        <p className="muted">{t('credit.openingHelp')}</p>
        <div className="grid-2">
          <SelectField label={t('credit.branch')} value={branchId} onChange={(e) => setBranchId(e.target.value)}>{branches.map((b) => <option key={b.id} value={b.id}>{b.code} · {pick(b.nameEn, b.nameAr)}</option>)}</SelectField>
          <TextField label={t('credit.amount')} value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" dir="ltr" placeholder="0.000" />
          <TextField label={t('credit.date')} type="date" value={date} onChange={(e) => setDate(e.target.value)} dir="ltr" />
        </div>
        <ErrorBox error={error} />
      </div>
    </Modal>
  );
}

function AllocateDialog({ detail, creditId, onClose, onDone }: { detail: CreditDetail; creditId: string; onClose: () => void; onDone: () => void }) {
  const api = useApi();
  const { t, lang } = useI18n();
  const credit = detail.credits.find((c) => c.id === creditId)!;
  const [amounts, setAmounts] = useState<Record<string, string>>({});
  const [error, setError] = useState<unknown>(null);
  const total = Object.values(amounts).reduce((s, v) => s + (parseAmount(v) ?? 0), 0);
  const list = Object.entries(amounts).map(([itemId, v]) => ({ itemId, amount: parseAmount(v) ?? 0 })).filter((x) => x.amount > 0);
  const valid = list.length > 0 && total <= credit.leftOver + 1e-9 && list.every((x) => x.amount <= (detail.openItems.find((i) => i.id === x.itemId)?.open ?? 0) + 1e-9);
  return (
    <Modal open title={t('credit.allocateTitle', { number: credit.number })} onClose={onClose}
      footer={<><Button onClick={onClose}>{t('app.cancel')}</Button><Button variant="primary" disabled={!valid} onClick={() => api.allocateCredit(detail.account.customerId, creditId, list).then(onDone).catch(setError)}>{t('credit.allocate')}</Button></>}>
      <div className="stack">
        <p className="muted">{t('credit.allocateHelp')} {t('credit.leftOver')}: <span className="num">{fmt(credit.leftOver - total)}</span></p>
        {detail.openItems.map((i) => (
          <div key={i.id} className="grid-2">
            <span><span className="ltr">{i.number}</span> · {t('credit.of', { seq: i.seq, of: i.of })} · {formatDate(i.dueDate, lang)} · <strong className="num">{fmt(i.open)}</strong></span>
            <TextField label={t('credit.amount')} value={amounts[i.id] ?? ''} onChange={(e) => setAmounts({ ...amounts, [i.id]: e.target.value })} inputMode="decimal" dir="ltr" placeholder="0.000" />
          </div>
        ))}
        <ErrorBox error={error} />
      </div>
    </Modal>
  );
}

/** The statement for any period, across every branch or for one (14.5): on screen, as a PDF, or printed. */
export function StatementCard({ customerId, name }: { customerId: string; name: string }) {
  const api = useApi();
  const { me } = useAuth();
  const { t, lang, pick } = useI18n();
  const [from, setFrom] = useState(`${businessToday().slice(0, 8)}01`);
  const [to, setTo] = useState(businessToday());
  const [branchId, setBranchId] = useState('');
  const [error, setError] = useState<unknown>(null);
  const q = useQuery({ queryKey: ['statement', customerId, from, to, branchId], queryFn: () => api.customerStatement(customerId, { from, to, branchId: branchId || null }) });
  const pdf = () => api.customerStatementPdf(customerId, { from, to, branchId: branchId || null })
    .then((b) => { const url = URL.createObjectURL(b); window.open(url, '_blank'); setTimeout(() => URL.revokeObjectURL(url), 60_000); }).catch(setError);
  const s: CustomerStatement | undefined = q.data;
  return (
    <Card className="print-area">
      <div className="statement-head">
        <h3>{t('credit.statementTitle', { name })}</h3>
        <div className="row no-print">
          <TextField label={t('credit.from')} type="date" value={from} onChange={(e) => setFrom(e.target.value)} dir="ltr" />
          <TextField label={t('credit.to')} type="date" value={to} onChange={(e) => setTo(e.target.value)} dir="ltr" />
          <SelectField label={t('credit.branch')} value={branchId} onChange={(e) => setBranchId(e.target.value)}>
            <option value="">{t('credit.allBranches')}</option>
            {(me?.branches ?? []).map((b) => <option key={b.id} value={b.id}>{b.code} · {pick(b.nameEn, b.nameAr)}</option>)}
          </SelectField>
          <Button onClick={pdf}>{t('credit.pdf')}</Button>
          <Button onClick={() => window.print()}>{t('credit.print')}</Button>
        </div>
      </div>
      <ErrorBox error={error ?? q.error} />
      {s && (
        <div className="table-wrap"><table className="table">
          <thead><tr><th scope="col">{t('credit.date')}</th><th scope="col">{t('credit.document')}</th><th scope="col">{t('credit.type')}</th><th scope="col">{t('credit.branch')}</th><th scope="col" className="num">{t('credit.debit')}</th><th scope="col" className="num">{t('credit.credit')}</th><th scope="col" className="num">{t('credit.balance')}</th></tr></thead>
          <tbody>
            <tr><td colSpan={6} className="muted">{t('credit.opening')}</td><td className="num">{fmt(s.opening)}</td></tr>
            {s.lines.map((l, i) => (
              <tr key={i}><td className="muted">{formatDate(l.date, lang)}</td><td><span className="ltr">{l.number}</span></td><td>{t(`credit.kinds.${l.kind}`)}</td><td><span className="ltr">{l.branchCode}</span></td>
                <td className="num">{l.debit ? fmt(l.debit) : ''}</td><td className="num">{l.credit ? fmt(l.credit) : ''}</td><td className="num">{fmt(l.balance)}</td></tr>
            ))}
          </tbody>
          <tfoot><tr><th colSpan={4}>{t('credit.closing')}</th><th className="num">{fmt(s.debits)}</th><th className="num">{fmt(s.credits)}</th><th className="num">{fmt(s.closing)}</th></tr></tfoot>
        </table></div>
      )}
      {s?.aging && (
        <div className="row figures-row">
          {(['current', 'd1To30', 'd31To60', 'd61To90', 'd90Plus'] as const).map((k, i) => (
            <div key={k} className="fig stat"><span className="muted">{t(`credit.aging.${['current', 'd1_30', 'd31_60', 'd61_90', 'd90plus'][i]}`)}</span><strong className="num">{fmt(s.aging![k])}</strong></div>
          ))}
        </div>
      )}
    </Card>
  );
}
