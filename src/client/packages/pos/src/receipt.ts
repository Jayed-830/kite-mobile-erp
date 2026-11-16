// 80/58 mm thermal receipts: the receipt is drawn as an image (English and Arabic shaped by the canvas text engine)
// and sent with the ESC/POS raster command GS v 0, because many thermal printers lack a reliable Arabic code page
// (blueprint 20.1, proven by the Phase 2 PoC). The same bytes go to a USB printer through the Windows app or to a
// network printer through the server's print relay.
import QRCode from 'qrcode';

export interface ReceiptParty { nameEn: string; nameAr: string; vatNumber: string | null; crNumber: string | null; addressEn: string | null; addressAr: string | null; phone: string | null }
/** A margin-scheme line (12.3) shows no VAT: `tax` is 0 and `net` is the price paid. */
export interface ReceiptLine { descriptionEn: string; descriptionAr: string; qty: number; unitPrice: number; discount: number; net: number; tax: number; gross: number; taxRate: number; serial?: string | null; warrantyUntil?: string | null; margin?: boolean }
export interface ReceiptModel {
  saleId: string; number: string; docType: string; titleEn: string; titleAr: string; originalNumber: string | null; creditReason: string | null;
  localTime: string; terminalCode: string; cashierName: string; customerName: string | null; customerVat: string | null;
  business: ReceiptParty; branch: ReceiptParty; lines: ReceiptLine[]; net: number; discount: number; tax: number; gross: number; cashRounding: number; total: number;
  taxBreakdown: { rate: number; net: number; tax: number }[]; payments: { method: string; amount: number; tendered: number | null; change: number | null; cardTerminal: string | null }[];
  qrPayload: string; footerEn: string | null; footerAr: string | null; isCopy: boolean; isEmergency: boolean; paperWidthMm: number;
  /** Margin-scheme items: their total, and the wording printed in place of their VAT. */
  marginGross?: number; marginNoteEn?: string | null; marginNoteAr?: string | null;
}

export interface Ctx2D {
  fillStyle: string | CanvasGradient | CanvasPattern; font: string; textAlign: CanvasTextAlign; textBaseline: CanvasTextBaseline; direction: CanvasDirection;
  fillRect(x: number, y: number, w: number, h: number): void; fillText(t: string, x: number, y: number, maxWidth?: number): void;
  measureText(t: string): { width: number }; getImageData(x: number, y: number, w: number, h: number): { data: Uint8ClampedArray; width: number; height: number };
}
export interface CanvasLike { width: number; height: number; getContext(kind: '2d'): Ctx2D | null }

const FONT = 'Tahoma, "Segoe UI", "Noto Sans Arabic", "DejaVu Sans", sans-serif';
export const money = (v: number) => v.toLocaleString('en-US', { minimumFractionDigits: 3, maximumFractionDigits: 3 });
const qtyText = (v: number) => (Number.isInteger(v) ? String(v) : v.toFixed(3).replace(/0+$/, ''));
export const METHOD: Record<string, [string, string]> = {
  cash: ['Cash', 'نقداً'], card: ['Card', 'بطاقة'], bank_transfer: ['Bank transfer', 'تحويل بنكي'], trade_in: ['Trade-in', 'جهاز مستبدل'], store_credit: ['Store credit', 'رصيد المتجر'],
};

/** Printable width in dots at 203 dpi: 576 for 80 mm paper, 384 for 58 mm. */
export const dotsFor = (paperMm: number) => (paperMm === 58 ? 384 : 576);

/** Draws the receipt; returns the used height. `make` creates a canvas (DOM, OffscreenCanvas or @napi-rs/canvas). */
export function drawReceipt(r: ReceiptModel, make: (w: number, h: number) => CanvasLike): { canvas: CanvasLike; height: number } {
  const W = dotsFor(r.paperWidthMm);
  const canvas = make(W, 3000 + r.lines.length * 120);
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('No 2D canvas available');
  ctx.fillStyle = '#fff';
  ctx.fillRect(0, 0, W, canvas.height);
  ctx.fillStyle = '#000';
  ctx.textBaseline = 'top';
  const s = W / 576;
  const px = (n: number) => Math.round(n * s);
  let y = px(12);
  const text = (t: string, size: number, align: 'center' | 'left' | 'right', rtl = false, bold = false) => {
    ctx.font = `${bold ? 'bold ' : ''}${px(size)}px ${FONT}`;
    ctx.direction = rtl ? 'rtl' : 'ltr';
    ctx.textAlign = align;
    ctx.fillText(t, align === 'center' ? W / 2 : align === 'right' ? W - px(8) : px(8), y, W - px(16));
    y += Math.round(px(size) * 1.32);
  };
  const pair = (left: string, right: string, size = 22, bold = false) => {
    ctx.font = `${bold ? 'bold ' : ''}${px(size)}px ${FONT}`;
    ctx.direction = 'ltr'; ctx.textAlign = 'left'; ctx.fillText(left, px(8), y);
    ctx.direction = /[؀-ۿ]/.test(right) ? 'rtl' : 'ltr'; ctx.textAlign = 'right'; ctx.fillText(right, W - px(8), y);
    y += Math.round(px(size) * 1.32);
  };
  const rule = () => { ctx.fillRect(px(8), y + px(4), W - px(16), Math.max(1, px(2))); y += px(14); };

  text(r.business.nameEn, 28, 'center', false, true);
  text(r.business.nameAr, 30, 'center', true, true);
  text(`${r.branch.nameEn}  |  ${r.branch.nameAr}`, 20, 'center');
  if (r.branch.phone) text(r.branch.phone, 20, 'center');
  if (r.business.vatNumber) text(`VAT No. ${r.business.vatNumber}  الرقم الضريبي`, 20, 'center');
  rule();
  pair(r.titleEn + (r.isCopy ? ' (COPY)' : ''), r.titleAr + (r.isCopy ? ' (نسخة)' : ''), 24, true);
  if (r.isEmergency) text('EMERGENCY MODE  |  وضع الطوارئ', 20, 'center', false, true);
  pair(r.number, r.localTime.slice(0, 16).replace('T', ' '), 20);
  if (r.originalNumber) pair(`For ${r.originalNumber}`, 'عن الفاتورة', 20);
  pair(`${r.terminalCode} · ${r.cashierName}`, '', 18);
  if (r.customerName) pair(r.customerName, r.customerVat ?? '', 20);
  rule();
  for (const l of r.lines) {
    text(l.descriptionEn, 22, 'left');
    text(l.descriptionAr, 24, 'right', true);
    pair(`${qtyText(l.qty)} x ${money(l.unitPrice)}${l.discount ? `  -${money(l.discount)}` : ''}`, money(l.gross), 22);
    if (l.serial) text(l.serial, 18, 'left');
    if (l.margin) pair('Margin scheme', 'نظام الهامش', 18);
    if (l.warrantyUntil) pair(`Warranty until ${l.warrantyUntil.slice(0, 10)}`, 'الضمان', 18);
    y += px(4);
  }
  rule();
  for (const b of r.taxBreakdown) pair(`VAT ${qtyText(b.rate)}%`, `${money(b.tax)}  ضريبة ${qtyText(b.rate)}%`, 20);
  if (r.marginGross) pair('Margin scheme items', `${money(r.marginGross)}  سلع الهامش`, 20);
  if (r.cashRounding) pair('Cash rounding', `${money(r.cashRounding)}  تقريب`, 20);
  pair(r.docType === 'credit_note' ? 'REFUND' : 'TOTAL', `${money(r.total)}  ${r.docType === 'credit_note' ? 'المسترد' : 'الإجمالي'}`, 30, true);
  text('OMR  ر.ع.', 18, 'center');
  for (const p of r.payments) {
    const [en, ar] = METHOD[p.method] ?? [p.method, p.method];
    pair(`${en}${p.cardTerminal ? ` (${p.cardTerminal})` : ''}`, `${money(p.amount)}  ${ar}`, 20);
    if (p.change) pair('Change', `${money(p.change)}  الباقي`, 20);
  }
  if (r.marginNoteEn) text(r.marginNoteEn, 16, 'left');
  if (r.marginNoteAr) text(r.marginNoteAr, 18, 'right', true);
  rule();
  const qr = QRCode.create(r.qrPayload, { errorCorrectionLevel: 'M' });
  const cell = Math.max(2, Math.floor((W * 0.55) / qr.modules.size));
  const size = qr.modules.size * cell;
  const qx = Math.floor((W - size) / 2);
  for (let row = 0; row < qr.modules.size; row++)
    for (let col = 0; col < qr.modules.size; col++)
      if (qr.modules.get(row, col)) ctx.fillRect(qx + col * cell, y + row * cell, cell, cell);
  y += size + px(12);
  if (r.footerEn) text(r.footerEn, 22, 'center');
  if (r.footerAr) text(r.footerAr, 24, 'center', true);
  return { canvas, height: y + px(16) };
}

/** 1-bit raster in ESC/POS: ESC @, GS v 0 with the image, feed, partial cut; optional drawer kick before cutting. */
export function encodeRaster(pixels: { data: Uint8ClampedArray | Uint8Array }, width: number, height: number, opts: { drawer?: boolean } = {}): Uint8Array {
  const bytesPerRow = Math.ceil(width / 8);
  const raster = new Uint8Array(bytesPerRow * height);
  for (let row = 0; row < height; row++)
    for (let col = 0; col < width; col++) {
      const i = (row * width + col) * 4;
      const lum = 0.299 * pixels.data[i]! + 0.587 * pixels.data[i + 1]! + 0.114 * pixels.data[i + 2]!;
      if (lum < 128 && pixels.data[i + 3]! > 0) raster[row * bytesPerRow + (col >> 3)]! |= 0x80 >> (col & 7);
    }
  const header = [0x1b, 0x40, 0x1d, 0x76, 0x30, 0x00, bytesPerRow & 0xff, bytesPerRow >> 8, height & 0xff, (height >> 8) & 0xff];
  const tail = [...(opts.drawer ? DRAWER_KICK : []), 0x1b, 0x64, 0x04, 0x1d, 0x56, 0x42, 0x00];
  const out = new Uint8Array(header.length + raster.length + tail.length);
  out.set(header, 0);
  out.set(raster, header.length);
  out.set(tail, header.length + raster.length);
  return out;
}

/** ESC p 0: pulse drawer pin 2 (50 ms on, 500 ms off). */
export const DRAWER_KICK = [0x1b, 0x70, 0x00, 0x19, 0xfa];

/** Receipt model → printer bytes. */
export function receiptBytes(r: ReceiptModel, make: (w: number, h: number) => CanvasLike, opts: { drawer?: boolean } = {}): Uint8Array {
  const { canvas, height } = drawReceipt(r, make);
  const img = canvas.getContext('2d')!.getImageData(0, 0, canvas.width, height);
  return encodeRaster(img, canvas.width, height, opts);
}

export function toBase64(bytes: Uint8Array): string {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}
