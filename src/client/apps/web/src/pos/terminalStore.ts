// This device as a POS terminal: its ID and its device key (a random secret; the server keeps only its SHA-256
// fingerprint, 18.1). Browser terminals keep it in localStorage; the Windows app keeps it in its encrypted store.
export interface StoredTerminal { terminalId: string; deviceKey: string; code: string; branchId: string; printerId: string | null }

const KEY = 'kite.terminal';

export function loadTerminal(): StoredTerminal | null {
  try { const v = localStorage.getItem(KEY); return v ? (JSON.parse(v) as StoredTerminal) : null; } catch { return null; }
}
export function saveTerminal(t: StoredTerminal | null) {
  try { if (t) localStorage.setItem(KEY, JSON.stringify(t)); else localStorage.removeItem(KEY); } catch { /* private mode: the terminal is forgotten */ }
}
export function newDeviceKey(): string {
  const b = new Uint8Array(32);
  crypto.getRandomValues(b);
  let s = '';
  for (const x of b) s += String.fromCharCode(x);
  return btoa(s).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
}
