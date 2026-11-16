// Desktop modules under plain Node (the Electron main process uses the same code): the encrypted emergency store,
// the network printer path, printer settings and the offline PIN check. Run after `npm run build`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { EmergencyStore } = require('../dist/emergencyStore.js');
const { sendToNetworkPrinter, decodeJob, DRAWER_KICK } = require('../dist/printer.js');
const { checkPrinter, normaliseServerUrl } = require('../dist/config.js');
const { verifyPin } = require('../dist/pin.js');

const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'kite-desktop-test-'));
const snapshot = { numberPrefix: 'EM-B01-T05-', generatedAt: '2026-11-16T08:00:00Z', users: [], items: [{ variantId: 'v1', sku: 'CBL', descriptionEn: 'IMEI-MARKER-cable' }] };

test('the emergency store numbers records from the terminal series, in order, encrypted at rest', () => {
  const dir = tmp();
  const file = path.join(dir, 'emergency.db');
  const key = crypto.randomBytes(32).toString('hex');
  let s = EmergencyStore.openWithKey(file, key);
  assert.throws(() => s.append('sale', {}, 1), /snapshot/);
  s.saveSnapshot(snapshot);
  const open = s.append('shift_open', { shiftId: 's1', cashierId: 'u1', openingFloat: 10 }, 10);
  const sale1 = s.append('sale', { saleId: 'a', shiftId: 's1', lines: [], payments: [] }, 20);
  const sale2 = s.append('sale', { saleId: 'b', shiftId: 's1', lines: [], payments: [] }, 30);
  assert.equal(open.payload.number, 'EMS-B01-T05-000001');
  assert.equal(sale1.payload.number, 'EM-B01-T05-000001');
  assert.equal(sale2.payload.number, 'EM-B01-T05-000002');
  assert.deepEqual(s.pending().map((r) => r.seq), [1, 2, 3]);
  assert.equal(s.status().pending, 3);
  assert.equal(s.status().shift.number, 'EMS-B01-T05-000001');
  s.markUploaded([{ seq: 1, result: 'applied', detail: null }, { seq: 2, result: 'conflict', detail: 'does not add up' }]);
  assert.deepEqual(s.pending().map((r) => r.seq), [3]);
  assert.equal(s.status().conflicts, 1);
  s.append('shift_close', { shiftId: 's1', countedCash: 10 }, 40);
  assert.equal(s.status().shift, null);
  s.close();

  // Nothing readable without the key; a wrong key is refused; the right key reads everything.
  const raw = fs.readFileSync(file);
  assert.equal(raw.includes(Buffer.from('IMEI-MARKER')), false);
  assert.equal(raw.includes(Buffer.from('EM-B01-T05')), false);
  assert.throws(() => EmergencyStore.openWithKey(file, crypto.randomBytes(32).toString('hex')));
  s = EmergencyStore.openWithKey(file, key);
  assert.equal(s.records().length, 4);
  assert.equal(s.append('sale', { saleId: 'c' }, 50).payload.number, 'EM-B01-T05-000003');   // the series continues after a restart
  s.close();
});

test('the store key is kept only in protected form', () => {
  const dir = tmp();
  const protector = { protect: (k) => Buffer.from(k.map((b) => b ^ 0x5a)), unprotect: (b) => Buffer.from(b.map((x) => x ^ 0x5a)) };
  const s = EmergencyStore.open(dir, protector);
  s.saveSnapshot(snapshot);
  s.close();
  const blob = fs.readFileSync(path.join(dir, 'emergency.key'));
  assert.equal(blob.length, 32);
  const again = EmergencyStore.open(dir, protector);
  assert.equal(again.status().numberPrefix, 'EM-B01-T05-');
  again.close();
  assert.throws(() => EmergencyStore.open(dir, { protect: (k) => k, unprotect: (b) => b }));
});

test('a snapshot that is not from the server is refused', () => {
  const s = EmergencyStore.openWithKey(path.join(tmp(), 'e.db'), crypto.randomBytes(32).toString('hex'));
  assert.throws(() => s.saveSnapshot({ numberPrefix: '../../evil' }));
  s.close();
});

test('receipt bytes and the drawer kick reach a network printer over raw TCP', async () => {
  const received = [];
  const server = net.createServer((sock) => { const chunks = []; sock.on('data', (c) => chunks.push(c)); sock.on('end', () => received.push(Buffer.concat(chunks))); });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const port = server.address().port;
  const job = decodeJob(Buffer.from([0x1b, 0x40, 0x41, 0x0a]).toString('base64'));
  await sendToNetworkPrinter('127.0.0.1', port, job);
  await sendToNetworkPrinter('127.0.0.1', port, DRAWER_KICK);
  await new Promise((r) => setTimeout(r, 100));
  server.close();
  assert.deepEqual([...received[0]], [0x1b, 0x40, 0x41, 0x0a]);
  assert.deepEqual([...received[1]], [0x1b, 0x70, 0x00, 0x19, 0xfa]);
  await assert.rejects(sendToNetworkPrinter('127.0.0.1', port, job, 500));   // nobody listening any more
  assert.throws(() => decodeJob(''));
});

test('printer settings and server addresses are checked', () => {
  assert.equal(checkPrinter({ mode: 'network', host: '192.168.1.50', port: 9100 }), null);
  assert.match(checkPrinter({ mode: 'network', host: 'http://x' }), /address/);
  assert.match(checkPrinter({ mode: 'windows', name: '' }), /Windows printer/);
  assert.match(checkPrinter({ mode: 'network', host: '10.0.0.5', paperWidthMm: 110 }), /58 or 80/);
  assert.equal(normaliseServerUrl('kite.local:5080'), 'https://kite.local:5080');
  assert.equal(normaliseServerUrl('ftp://x'), null);
});

test('PIN hashes from the server verify offline (shared vectors)', async () => {
  const v = JSON.parse(fs.readFileSync(path.resolve('../../../../tests/vectors/pin-hash-vectors.json'), 'utf8'));
  for (const x of v.vectors) {
    assert.equal(await verifyPin(x.pin, x.hash), true);
    assert.equal(await verifyPin('0000', x.hash), false);
  }
  assert.equal(await verifyPin('12', v.vectors[0].hash), false);
  assert.equal(await verifyPin('2468', null), false);
});

test('a USB printer installed in Windows gets the receipt bytes RAW through the spooler', { skip: !process.env.KITE_TEST_WINDOWS_PRINTER }, async () => {
  const { printRawWindows } = require('../dist/printer.js');
  const file = process.env.KITE_TEST_WINDOWS_PRINTER_FILE;
  fs.rmSync(file, { force: true });
  const bytes = Buffer.concat([Buffer.from([0x1b, 0x40]), Buffer.from('KITE RAW TEST\n'), Buffer.from([0x1d, 0x76, 0x30, 0x00, 0x01, 0x00, 0x01, 0x00, 0xff]), DRAWER_KICK]);
  await printRawWindows(process.env.KITE_TEST_WINDOWS_PRINTER, bytes);
  for (let i = 0; i < 100 && !(fs.existsSync(file) && fs.statSync(file).size >= bytes.length); i++) await new Promise((r) => setTimeout(r, 200));
  assert.deepEqual([...fs.readFileSync(file)], [...bytes]);
});
