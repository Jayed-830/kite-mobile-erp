// PoC for blueprint 6.2: the emergency POS store is an encrypted SQLite file (SQLCipher) in WAL
// mode with synchronous=FULL. Proves: (1) data is unreadable without the key, (2) a wrong key is
// rejected, (3) the right key reads every committed record, (4) no plaintext is left on disk.
import Database from 'better-sqlite3-multiple-ciphers';
import { randomBytes, randomUUID } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const out = path.resolve('output');
fs.rmSync(out, { recursive: true, force: true });
fs.mkdirSync(out, { recursive: true });
const file = path.join(out, 'emergency.db');
const key = randomBytes(32).toString('hex');   // on Windows the key is protected with DPAPI (Phase 4)
const marker = 'IMEI-356938035643809-MARKER';
const results = [];
const check = (ok, what) => { results.push({ ok, what }); console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}`); };

function open(k) {
  const db = new Database(file);
  db.pragma(`cipher='sqlcipher'`);
  db.pragma(`legacy=4`);
  if (k !== undefined) db.pragma(`key='${k}'`);
  return db;
}

// 1. Create and write.
let db = open(key);
db.pragma('journal_mode = WAL');
db.pragma('synchronous = FULL');
db.exec(`CREATE TABLE emergency_sale (id TEXT PRIMARY KEY, terminal_seq INTEGER NOT NULL UNIQUE, number TEXT NOT NULL UNIQUE,
         occurred_at TEXT NOT NULL, monotonic_ms INTEGER NOT NULL, payload TEXT NOT NULL, handed_over_at TEXT)`);
const insert = db.prepare('INSERT INTO emergency_sale VALUES (?, ?, ?, ?, ?, ?, NULL)');
const tx = db.transaction((n) => {
  for (let i = 1; i <= n; i++) insert.run(randomUUID(), i, `E-B01-T05-${String(i).padStart(6, '0')}`, new Date().toISOString(), Math.round(performance.now()), JSON.stringify({ imei: marker, total: '199.000' }));
});
tx(250);
check(db.pragma('journal_mode', { simple: true }) === 'wal', 'WAL journal mode is on');
check(db.pragma('synchronous', { simple: true }) === 2, 'synchronous=FULL is on');
// A duplicate terminal sequence (double submission) is refused by the store itself.
let dupRefused = false;
try { insert.run(randomUUID(), 1, 'E-B01-T05-999999', new Date().toISOString(), 0, '{}'); } catch { dupRefused = true; }
check(dupRefused, 'duplicate terminal sequence is refused');
db.pragma('wal_checkpoint(TRUNCATE)');
db.close();

// 2. No key.
let noKeyFailed = false;
try { const d = open(undefined); d.prepare('SELECT count(*) FROM emergency_sale').get(); d.close(); } catch { noKeyFailed = true; }
check(noKeyFailed, 'cannot read without the key');

// 3. Wrong key.
let wrongKeyFailed = false;
try { const d = open(randomBytes(32).toString('hex')); d.prepare('SELECT count(*) FROM emergency_sale').get(); d.close(); } catch { wrongKeyFailed = true; }
check(wrongKeyFailed, 'a wrong key is rejected');

// 4. Right key reads everything.
db = open(key);
const count = db.prepare('SELECT count(*) AS n FROM emergency_sale').get().n;
check(count === 250, `right key reads all 250 committed records (got ${count})`);
db.close();

// 5. No plaintext on disk (database and any WAL file).
const leaked = fs.readdirSync(out).filter((f) => f.startsWith('emergency.db')).some((f) => fs.readFileSync(path.join(out, f)).includes(Buffer.from(marker)));
check(!leaked, 'no plaintext IMEI found in the files on disk');

fs.writeFileSync(path.join(out, 'result.json'), JSON.stringify({ results, sqlite: new Database(':memory:').prepare('select sqlite_version() v').get().v }, null, 2));
const failed = results.filter((r) => !r.ok).length;
console.log(failed === 0 ? 'SQLCipher PoC: ALL CHECKS PASSED' : `SQLCipher PoC: ${failed} FAILED`);
process.exit(failed === 0 ? 0 : 1);
