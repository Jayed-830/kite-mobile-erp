import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import type { ReceiptModel } from '@kite/pos';
import { fmt, qty } from '../money';

const METHOD: Record<string, [string, string]> = { cash: ['Cash', 'نقداً'], card: ['Card', 'بطاقة'], bank_transfer: ['Bank transfer', 'تحويل بنكي'] };

/** The 80 mm receipt as HTML: preview on screen and browser printing (bilingual, QR). The thermal raster uses the same model. */
export function ReceiptView({ r }: { r: ReceiptModel }) {
  const [qr, setQr] = useState<string | null>(null);
  useEffect(() => {
    let live = true;
    QRCode.toDataURL(r.qrPayload, { errorCorrectionLevel: 'M', margin: 1, width: 180 }).then((u) => { if (live) setQr(u); }).catch(() => setQr(null));
    return () => { live = false; };
  }, [r.qrPayload]);
  const credit = r.docType === 'credit_note';
  return (
    <div className={`receipt${r.paperWidthMm === 58 ? ' receipt-58' : ''}`} aria-label={`Receipt ${r.number}`}>
      <div className="r-center r-strong r-big">{r.business.nameEn}</div>
      <div className="r-center r-strong r-big" dir="rtl">{r.business.nameAr}</div>
      <div className="r-center">{r.branch.nameEn} | <span dir="rtl">{r.branch.nameAr}</span></div>
      {r.branch.phone && <div className="r-center ltr">{r.branch.phone}</div>}
      {r.business.vatNumber && <div className="r-center">VAT No. <span className="ltr">{r.business.vatNumber}</span> · الرقم الضريبي</div>}
      <hr />
      <div className="r-pair r-strong"><span>{r.titleEn}{r.isCopy ? ' (COPY)' : ''}</span><span dir="rtl">{r.titleAr}{r.isCopy ? ' (نسخة)' : ''}</span></div>
      {r.isEmergency && <div className="r-center r-strong">EMERGENCY MODE · وضع الطوارئ</div>}
      <div className="r-pair"><span className="ltr">{r.number}</span><span className="ltr">{r.localTime.slice(0, 16).replace('T', ' ')}</span></div>
      {r.originalNumber && <div className="r-pair"><span>For <span className="ltr">{r.originalNumber}</span></span><span dir="rtl">عن الفاتورة</span></div>}
      <div className="r-pair r-small"><span>{r.terminalCode} · {r.cashierName}</span></div>
      {r.customerName && <div className="r-pair"><span>{r.customerName}</span><span className="ltr">{r.customerVat ?? ''}</span></div>}
      <hr />
      {r.lines.map((l, i) => (
        <div key={i} className="r-line">
          <div>{l.descriptionEn}</div>
          <div dir="rtl">{l.descriptionAr}</div>
          <div className="r-pair"><span className="ltr">{qty(l.qty)} × {fmt(l.unitPrice)}{l.discount ? `  −${fmt(l.discount)}` : ''}</span><span className="ltr">{fmt(l.gross)}</span></div>
          {l.serial && <div className="r-small ltr r-serial">{l.serial}</div>}
          {l.warrantyUntil && <div className="r-pair r-small"><span>Warranty until <span className="ltr">{l.warrantyUntil.slice(0, 10)}</span></span><span dir="rtl">الضمان حتى</span></div>}
        </div>
      ))}
      <hr />
      {r.taxBreakdown.map((b) => <div key={b.rate} className="r-pair"><span>VAT {b.rate}%</span><span><span className="ltr">{fmt(b.tax)}</span> ضريبة</span></div>)}
      {r.cashRounding !== 0 && <div className="r-pair"><span>Cash rounding</span><span><span className="ltr">{fmt(r.cashRounding)}</span> تقريب</span></div>}
      <div className="r-pair r-strong r-big"><span>{credit ? 'REFUND' : 'TOTAL'} OMR</span><span><span className="ltr">{fmt(r.total)}</span> {credit ? 'المسترد' : 'الإجمالي'}</span></div>
      {r.payments.map((p, i) => {
        const [en, ar] = METHOD[p.method] ?? [p.method, p.method];
        return (
          <div key={i}>
            <div className="r-pair"><span>{en}{p.cardTerminal ? ` (${p.cardTerminal})` : ''}</span><span><span className="ltr">{fmt(p.amount)}</span> {ar}</span></div>
            {p.change ? <div className="r-pair"><span>Change</span><span><span className="ltr">{fmt(p.change)}</span> الباقي</span></div> : null}
          </div>
        );
      })}
      <hr />
      {qr && <img className="r-qr" src={qr} alt="QR" />}
      {r.footerEn && <div className="r-center">{r.footerEn}</div>}
      {r.footerAr && <div className="r-center" dir="rtl">{r.footerAr}</div>}
    </div>
  );
}
