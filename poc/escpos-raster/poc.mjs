// PoC for blueprint 20.1: many thermal printers lack a reliable Arabic code page, so KITE renders
// the whole receipt (English + Arabic) as an image and sends it with the ESC/POS raster command
// GS v 0. This script renders an 80 mm receipt (576 dots wide), converts it to 1-bit, encodes the
// printer bytes and checks the encoding. Printing on a real printer is a Phase 14 bench test.
import { createCanvas, GlobalFonts } from '@napi-rs/canvas';
import QRCode from 'qrcode';
import fs from 'node:fs';
import path from 'node:path';

const WIDTH = 576;               // 72 mm printable at 203 dpi
const out = path.resolve('output');
fs.mkdirSync(out, { recursive: true });

// Fonts with Arabic glyphs: Tahoma / Segoe UI on Windows, DejaVu Sans on Linux; or pass a file.
const fontFile = process.argv[2];
if (fontFile) GlobalFonts.registerFromPath(fontFile, 'ReceiptFont');
const FONT = fontFile ? 'ReceiptFont' : 'Tahoma, "Segoe UI", "DejaVu Sans", sans-serif';

const receipt = {
  nameEn: 'Al Noor Mobiles LLC', nameAr: 'شركة النور للهواتف', branch: 'Muscat - Ruwi  |  مسقط - روي',
  vat: 'OM1100223344', number: 'INV-B01-2026-000123', date: '14/11/2026 21:45',
  lines: [
    { en: 'iPhone 15 128GB Black', ar: 'آيفون 15 سعة 128 جيجابايت أسود', imei: '356938035643809', qty: 1, amount: '299.000' },
    { en: 'Screen protector', ar: 'واقي شاشة', qty: 2, amount: '6.000' },
  ],
  total: '305.000', vatAmount: '14.524', paid: 'Cash  نقدًا', currency: 'OMR  ر.ع.',
};

const canvas = createCanvas(WIDTH, 1100);
const ctx = canvas.getContext('2d');
ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, WIDTH, canvas.height);
ctx.fillStyle = '#000';
let y = 16;
const line = (text, { size = 24, align = 'center', dir = 'ltr', bold = false } = {}) => {
  ctx.font = `${bold ? 'bold ' : ''}${size}px ${FONT}`;
  ctx.direction = dir;
  ctx.textAlign = align;
  ctx.textBaseline = 'top';
  const x = align === 'center' ? WIDTH / 2 : align === 'right' || (align === 'start' && dir === 'rtl') ? WIDTH - 8 : 8;
  ctx.fillText(text, x, y);
  y += Math.round(size * 1.35);
};
const pair = (left, right, size = 22) => {
  ctx.font = `${size}px ${FONT}`; ctx.textBaseline = 'top';
  ctx.direction = 'ltr'; ctx.textAlign = 'left'; ctx.fillText(left, 8, y);
  ctx.direction = 'rtl'; ctx.textAlign = 'right'; ctx.fillText(right, WIDTH - 8, y);
  y += Math.round(size * 1.35);
};
const rule = () => { ctx.fillRect(8, y + 4, WIDTH - 16, 2); y += 14; };

line(receipt.nameEn, { size: 28, bold: true });
line(receipt.nameAr, { size: 30, bold: true, dir: 'rtl' });
line(receipt.branch, { size: 20 });
line(`VAT No. ${receipt.vat}`, { size: 20 });
pair('Simplified tax invoice', 'فاتورة ضريبية مبسطة');
rule();
pair(receipt.number, receipt.date, 20);
rule();
for (const l of receipt.lines) {
  line(l.en, { size: 22, align: 'left' });
  line(l.ar, { size: 24, align: 'right', dir: 'rtl' });
  if (l.imei) line(`IMEI ${l.imei}`, { size: 18, align: 'left' });
  pair(`${l.qty} x`, l.amount, 22);
  y += 4;
}
rule();
pair('VAT 5% included', `${receipt.vatAmount}  ضريبة 5%`);
pair('TOTAL', `${receipt.total}  الإجمالي`, 28);
pair(receipt.currency, receipt.paid, 20);
rule();

// QR code (the Fawtara QR payload is produced by the server in Phase 4; demo text here).
const qr = QRCode.create(`KITE|${receipt.vat}|${receipt.number}|${receipt.total}|${receipt.vatAmount}`, { errorCorrectionLevel: 'M' });
const cell = 5, size = qr.modules.size * cell, qx = (WIDTH - size) / 2;
for (let r = 0; r < qr.modules.size; r++)
  for (let c = 0; c < qr.modules.size; c++)
    if (qr.modules.get(r, c)) ctx.fillRect(qx + c * cell, y + r * cell, cell, cell);
y += size + 12;
line('Thank you  |  شكرًا لزيارتكم', { size: 22 });
const height = y + 16;

// Crop to content, threshold to 1-bit.
const img = ctx.getImageData(0, 0, WIDTH, height);
const bytesPerRow = WIDTH / 8;
const raster = Buffer.alloc(bytesPerRow * height);
let black = 0;
for (let row = 0; row < height; row++)
  for (let col = 0; col < WIDTH; col++) {
    const i = (row * WIDTH + col) * 4;
    const lum = 0.299 * img.data[i] + 0.587 * img.data[i + 1] + 0.114 * img.data[i + 2];
    if (lum < 128) { raster[row * bytesPerRow + (col >> 3)] |= 0x80 >> (col & 7); black++; }
  }

// ESC @ (init) + GS v 0 (raster bit image) + feed + partial cut.
const header = Buffer.from([0x1b, 0x40, 0x1d, 0x76, 0x30, 0x00, bytesPerRow & 0xff, bytesPerRow >> 8, height & 0xff, height >> 8]);
const tail = Buffer.from([0x1b, 0x64, 0x04, 0x1d, 0x56, 0x42, 0x00]);
const bin = Buffer.concat([header, raster, tail]);
fs.writeFileSync(path.join(out, 'receipt.bin'), bin);

// Preview of exactly what the printer receives (decoded back from the 1-bit data).
const preview = createCanvas(WIDTH, height);
const p = preview.getContext('2d');
const pd = p.createImageData(WIDTH, height);
for (let row = 0; row < height; row++)
  for (let col = 0; col < WIDTH; col++) {
    const on = raster[row * bytesPerRow + (col >> 3)] & (0x80 >> (col & 7));
    const i = (row * WIDTH + col) * 4;
    pd.data[i] = pd.data[i + 1] = pd.data[i + 2] = on ? 0 : 255; pd.data[i + 3] = 255;
  }
p.putImageData(pd, 0, 0);
fs.writeFileSync(path.join(out, 'receipt-printer-preview.png'), preview.toBuffer('image/png'));

// Checks.
const checks = [
  [bin.subarray(2, 5).equals(Buffer.from([0x1d, 0x76, 0x30])), 'GS v 0 raster command present'],
  [bin.readUInt16LE(6) === 72 && bin.readUInt16LE(8) === height, `dimensions encoded: 72 bytes x ${height} rows`],
  [bin.length === header.length + bytesPerRow * height + tail.length, 'payload length matches dimensions'],
  [black > 5000 && black < WIDTH * height * 0.5, `ink coverage plausible (${(100 * black / (WIDTH * height)).toFixed(1)}%)`],
];
let failed = 0;
for (const [ok, what] of checks) { console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) failed++; }
console.log(failed ? `ESC/POS PoC: ${failed} FAILED` : `ESC/POS PoC: ALL CHECKS PASSED (${bin.length} bytes)`);
process.exit(failed ? 1 : 0);
