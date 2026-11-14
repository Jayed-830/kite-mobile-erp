// A stand-in for Nextdab's control server in the end-to-end run (NEXTDAB PROTOCOL 1, part 7: KITE MOBILE ERP).
// It is NOT Nextdab Admin and is never shipped: Nextdab Admin lives only on the Nextdab PC. It signs licences and
// commands exactly as Nextdab Admin's lib/kme.js does (canonical JSON with sorted keys, Ed25519 over "NEXTDAB1|" + JSON),
// and proves that on start: it re-signs the licence of tests/vectors/nextdab-signing-vector.json with that vector's
// test seed and must get the same signature byte for byte (Ed25519 signatures are deterministic), and it verifies the
// licence and commands that Nextdab Admin 5.14 itself made (tests/vectors/nextdab-admin-5.14-vector.json).
//
//   STUB_PORT      (default 8799)
//   STUB_KEY_FILE  where the public key (hex) is written for KITE's Kite:Licensing:DevPublicKey
//
// Test API (no auth, localhost only):
//   POST /stub/licence {mid, customer, kind, validUntil, branches, terminals, modules, graceDays}  -> {file, licence}
//   POST /stub/command {mid, type, data}   queue a signed command for the next check-in
//   GET  /stub/state                       -> {checkins, lastReport, acks, queued}
//   POST /stub/stop                        the stub exits (Nextdab offline)
// KITE's check-in:  POST /v1/kme/checkin {product, mid, key, report, acks} -> {ok, next, server_time, commands}
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const PRODUCT = 'kite-mobile-erp';
const ALWAYS = ['core', 'iam', 'catalog', 'inv', 'sales', 'tax', 'rpt'];
const MODULE_KEYS = ['core', 'iam', 'catalog', 'inv', 'device', 'refurb', 'sales', 'tradein', 'repair', 'comm', 'credit', 'purch', 'exp', 'tax', 'einv', 'acct_ui', 'rpt', 'notif', 'sync'];

export function canon(v) {
  if (v === null || typeof v !== 'object') return JSON.stringify(v === undefined ? null : v);
  if (Array.isArray(v)) return '[' + v.map(canon).join(',') + ']';
  return '{' + Object.keys(v).filter((k) => v[k] !== undefined).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0)).map((k) => JSON.stringify(k) + ':' + canon(v[k])).join(',') + '}';
}
const msg = (o) => Buffer.concat([Buffer.from('NEXTDAB1|'), Buffer.from(canon(o), 'utf8')]);
const privKey = (seed) => crypto.createPrivateKey({ key: Buffer.concat([Buffer.from('302e020100300506032b657004220420', 'hex'), seed]), format: 'der', type: 'pkcs8' });
const pubOf = (seed) => crypto.createPublicKey(privKey(seed)).export({ format: 'der', type: 'spki' }).subarray(-32);
const pubKeyObj = (hex) => crypto.createPublicKey({ key: Buffer.concat([Buffer.from('302a300506032b6570032100', 'hex'), Buffer.from(hex, 'hex')]), format: 'der', type: 'spki' });
export const sign = (seed, o) => crypto.sign(null, msg(o), privKey(seed)).toString('hex');
const verify = (hex, o, sig) => crypto.verify(null, msg(o), pubKeyObj(hex), Buffer.from(sig, 'hex'));
const iso = (t) => new Date(t).toISOString().slice(0, 19) + 'Z';

// ---- self-check against the vectors before serving anything ----
const v1 = JSON.parse(fs.readFileSync(path.join(here, '..', 'tests', 'vectors', 'nextdab-signing-vector.json'), 'utf8'));
const f1 = JSON.parse(v1.licence_file);
if (canon(f1.licence) !== v1.canonical_licence) throw new Error('stub canonical JSON differs from the vector');
if (sign(Buffer.from(v1.seed_hex, 'hex'), f1.licence) !== f1.sig) throw new Error('stub signature differs from the vector (not the Nextdab signing method)');
const v2 = JSON.parse(fs.readFileSync(path.join(here, '..', 'tests', 'vectors', 'nextdab-admin-5.14-vector.json'), 'utf8'));
const f2 = JSON.parse(v2.licence_file);
if (!verify(v2.public_key_hex, f2.licence, f2.sig)) throw new Error('Nextdab Admin 5.14 licence does not verify with the stub method');
for (const c of v2.commands) { const { sig, ...rest } = c; if (!verify(v2.public_key_hex, rest, sig)) throw new Error(`Nextdab Admin 5.14 command ${c.type} does not verify`); }
console.log('stub: signing method matches Nextdab Admin (vector signature identical, 5.14 licence + commands verify)');

const seed = crypto.randomBytes(32);
const pub = pubOf(seed).toString('hex');
if (process.env.STUB_KEY_FILE) fs.writeFileSync(process.env.STUB_KEY_FILE, pub);
console.log(`stub: test public key ${pub}`);

const state = { checkins: 0, lastReport: null, acks: [], queued: [], keys: {} };

function makeLicence(o) {
  const mods = [...new Set([...ALWAYS, ...(o.modules ?? MODULE_KEYS)])];
  const licence = {
    id: `kme-${Date.now()}-${crypto.randomBytes(3).toString('hex')}`, product: PRODUCT, mid: o.mid, customer: o.customer ?? 'Al Noor Mobiles LLC',
    kind: o.kind ?? 'subscription', modules: MODULE_KEYS.filter((m) => mods.includes(m)), branches: o.branches ?? 3, terminals: o.terminals ?? 5,
    issued: iso(Date.now()), valid_until: o.validUntil, grace_days: o.graceDays ?? 30,
  };
  const file = { type: 'KITE MOBILE ERP licence file', version: 1, licence, sig: sign(seed, licence), how_to_import: 'KITE MOBILE ERP > Licence > Import licence file.' };
  return { licence, file: JSON.stringify(file, null, 2) + '\n' };
}
function makeCmd(mid, type, data) {
  const now = Date.now();
  const c = { v: 1, id: `${type.slice(0, 3)}-${now}-${crypto.randomBytes(3).toString('hex')}`, product: PRODUCT, mid, type, data: data ?? {}, seq: now, issued: iso(now) };
  c.sig = sign(seed, c);
  return c;
}

const body = (req) => new Promise((ok, bad) => { let s = ''; req.on('data', (d) => (s += d)); req.on('end', () => { try { ok(s ? JSON.parse(s) : {}); } catch (e) { bad(e); } }); });
const send = (res, code, o) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(o)); };

http.createServer(async (req, res) => {
  try {
    if (req.method === 'POST' && req.url === '/v1/kme/checkin') {
      const b = await body(req);
      if (b.product !== PRODUCT) return send(res, 400, { error: 'product' });
      if (state.keys[b.mid] && state.keys[b.mid] !== b.key) return send(res, 403, { error: 'another installation uses this ID' });
      state.keys[b.mid] = b.key;
      state.checkins += 1;
      state.lastReport = b.report;
      for (const a of b.acks ?? []) { state.acks.push(a); state.queued = state.queued.filter((c) => c.id !== a.id); }
      return send(res, 200, { ok: true, next: 30, server_time: iso(Date.now()), commands: state.queued.filter((c) => c.mid === b.mid) });
    }
    if (req.method === 'POST' && req.url === '/stub/licence') return send(res, 200, makeLicence(await body(req)));
    if (req.method === 'POST' && req.url === '/stub/command') { const b = await body(req); const c = makeCmd(b.mid, b.type, b.data); state.queued.push(c); return send(res, 200, c); }
    if (req.method === 'GET' && req.url === '/stub/state') return send(res, 200, state);
    if (req.method === 'POST' && req.url === '/stub/stop') { send(res, 200, { ok: true }); setTimeout(() => process.exit(0), 100); return; }   // Nextdab goes offline
    send(res, 404, { error: 'not found' });
  } catch (e) { send(res, 500, { error: String(e) }); }
}).listen(Number(process.env.STUB_PORT ?? 8799), '127.0.0.1', () => console.log(`stub: listening on ${process.env.STUB_PORT ?? 8799}`));
