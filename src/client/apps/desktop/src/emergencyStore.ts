// Emergency mode store (6.2, 6.4): an encrypted SQLite file (SQLCipher) in WAL mode with synchronous=FULL, so a
// recorded sale survives a power cut. It keeps the last catalog snapshot from the server and every record made while
// the server could not be reached, numbered from this terminal's own series, until the server confirms them.
// The 32-byte key is protected by Windows DPAPI (Electron safeStorage) and never written in clear.
import Database from 'better-sqlite3-multiple-ciphers';
import { randomBytes, randomUUID } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

export type RecordKind = 'shift_open' | 'sale' | 'cash_event' | 'shift_close';
export interface StoredRecord { seq: number; id: string; kind: RecordKind; recordedAt: string; monotonicMs: number; payload: unknown; uploadedAt: string | null; result: string | null; detail: string | null }
export interface KeyProtector { protect(key: Buffer): Buffer; unprotect(blob: Buffer): Buffer }
export interface StoreStatus { snapshotAt: string | null; pending: number; conflicts: number; lastUploadAt: string | null; shift: unknown | null; numberPrefix: string | null }

const KINDS: RecordKind[] = ['shift_open', 'sale', 'cash_event', 'shift_close'];

export class EmergencyStore {
  private readonly db: Database.Database;

  private constructor(db: Database.Database) {
    this.db = db;
    db.pragma('journal_mode = WAL');
    db.pragma('synchronous = FULL');
    db.exec(`
      CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS counter (name TEXT PRIMARY KEY, next INTEGER NOT NULL);
      CREATE TABLE IF NOT EXISTS record (
        seq INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL, recorded_at TEXT NOT NULL, monotonic_ms INTEGER NOT NULL,
        payload TEXT NOT NULL, uploaded_at TEXT, result TEXT, detail TEXT);
    `);
  }

  /** Opens (or creates) the store. The key file holds the DPAPI-protected key next to the database. */
  static open(dir: string, protector: KeyProtector): EmergencyStore {
    fs.mkdirSync(dir, { recursive: true });
    const keyFile = path.join(dir, 'emergency.key');
    let key: Buffer;
    if (fs.existsSync(keyFile)) key = protector.unprotect(fs.readFileSync(keyFile));
    else {
      key = randomBytes(32);
      fs.writeFileSync(keyFile, protector.protect(key), { mode: 0o600 });
    }
    return EmergencyStore.openWithKey(path.join(dir, 'emergency.db'), key.toString('hex'));
  }

  static openWithKey(file: string, hexKey: string): EmergencyStore {
    if (!/^[0-9a-f]{64}$/.test(hexKey)) throw new Error('The emergency store key is not valid.');
    const db = new Database(file);
    db.pragma(`cipher='sqlcipher'`);
    db.pragma('legacy=4');
    db.pragma(`key='${hexKey}'`);
    db.prepare('SELECT count(*) FROM sqlite_master').get();   // fails here with a wrong key
    return new EmergencyStore(db);
  }

  close() { this.db.close(); }

  private get(k: string): string | null { return (this.db.prepare('SELECT v FROM kv WHERE k = ?').get(k) as { v: string } | undefined)?.v ?? null; }
  private set(k: string, v: string | null) {
    if (v === null) this.db.prepare('DELETE FROM kv WHERE k = ?').run(k);
    else this.db.prepare('INSERT INTO kv (k, v) VALUES (?, ?) ON CONFLICT(k) DO UPDATE SET v = excluded.v').run(k, v);
  }

  /** Keeps the latest snapshot from the server (catalog, prices, stock, policy, users with PIN hashes). */
  saveSnapshot(snapshot: { numberPrefix?: string; generatedAt?: string }) {
    if (!snapshot || typeof snapshot !== 'object' || typeof snapshot.numberPrefix !== 'string' || !/^EM-[A-Z0-9-]+-$/.test(snapshot.numberPrefix))
      throw new Error('That is not an emergency snapshot from the KITE server.');
    this.db.transaction(() => {
      this.set('snapshot', JSON.stringify(snapshot));
      this.set('snapshotAt', snapshot.generatedAt ?? new Date().toISOString());
      this.set('numberPrefix', snapshot.numberPrefix!);
    })();
  }

  snapshot(): unknown | null { const s = this.get('snapshot'); return s ? JSON.parse(s) : null; }

  status(): StoreStatus {
    const n = (sql: string) => (this.db.prepare(sql).get() as { n: number }).n;
    return {
      snapshotAt: this.get('snapshotAt'), pending: n('SELECT count(*) AS n FROM record WHERE uploaded_at IS NULL'),
      conflicts: n("SELECT count(*) AS n FROM record WHERE result = 'conflict'"), lastUploadAt: this.get('lastUploadAt'),
      shift: this.get('shift') ? JSON.parse(this.get('shift')!) : null, numberPrefix: this.get('numberPrefix'),
    };
  }

  private next(name: string): number {
    const row = this.db.prepare('SELECT next FROM counter WHERE name = ?').get(name) as { next: number } | undefined;
    const v = row?.next ?? 1;
    this.db.prepare('INSERT INTO counter (name, next) VALUES (?, ?) ON CONFLICT(name) DO UPDATE SET next = excluded.next').run(name, v + 1);
    return v;
  }

  /**
   * Appends a record in one transaction: the next terminal sequence and, for a sale or a shift, the next number of this
   * terminal's emergency series (EM-B01-T01-000001, EMS-B01-T01-000001). Returns the stored record.
   */
  append(kind: RecordKind, payload: Record<string, unknown>, monotonicMs: number): StoredRecord {
    if (!KINDS.includes(kind)) throw new Error(`Unknown record kind ${kind}.`);
    const prefix = this.get('numberPrefix');
    if (!prefix) throw new Error('Emergency mode needs a snapshot from the server first.');
    return this.db.transaction(() => {
      const seq = this.next('seq');
      const body = { ...payload };
      if (kind === 'sale') body.number = `${prefix}${String(this.next('sale')).padStart(6, '0')}`;
      if (kind === 'shift_open') body.number = `EMS-${prefix.slice(3)}${String(this.next('shift')).padStart(6, '0')}`;
      const rec: StoredRecord = { seq, id: randomUUID(), kind, recordedAt: new Date().toISOString(), monotonicMs, payload: body, uploadedAt: null, result: null, detail: null };
      this.db.prepare('INSERT INTO record (seq, id, kind, recorded_at, monotonic_ms, payload) VALUES (?, ?, ?, ?, ?, ?)')
        .run(rec.seq, rec.id, kind, rec.recordedAt, Math.round(monotonicMs), JSON.stringify(body));
      if (kind === 'shift_open') this.set('shift', JSON.stringify(body));
      if (kind === 'shift_close') this.set('shift', null);
      return rec;
    })();
  }

  private static row(r: { seq: number; id: string; kind: RecordKind; recorded_at: string; monotonic_ms: number; payload: string; uploaded_at: string | null; result: string | null; detail: string | null }): StoredRecord {
    return { seq: r.seq, id: r.id, kind: r.kind, recordedAt: r.recorded_at, monotonicMs: r.monotonic_ms, payload: JSON.parse(r.payload), uploadedAt: r.uploaded_at, result: r.result, detail: r.detail };
  }

  /** Records not yet confirmed by the server, oldest first (the order they must be applied in). */
  pending(limit = 1000): StoredRecord[] {
    return (this.db.prepare('SELECT * FROM record WHERE uploaded_at IS NULL ORDER BY seq LIMIT ?').all(limit) as Parameters<typeof EmergencyStore.row>[0][]).map(EmergencyStore.row);
  }

  records(limit = 200): StoredRecord[] {
    return (this.db.prepare('SELECT * FROM record ORDER BY seq DESC LIMIT ?').all(limit) as Parameters<typeof EmergencyStore.row>[0][]).map(EmergencyStore.row);
  }

  /** Marks what the server answered; a conflict is kept, marked, never deleted. */
  markUploaded(results: { seq: number; result: 'applied' | 'conflict'; detail: string | null }[]) {
    const now = new Date().toISOString();
    const upd = this.db.prepare('UPDATE record SET uploaded_at = ?, result = ?, detail = ? WHERE seq = ? AND uploaded_at IS NULL');
    this.db.transaction(() => {
      for (const r of results) upd.run(now, r.result, r.detail, r.seq);
      this.set('lastUploadAt', now);
    })();
  }
}
