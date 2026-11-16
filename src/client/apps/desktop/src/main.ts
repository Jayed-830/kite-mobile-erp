// KITE Desktop (Electron main process). It shows the KITE web app served by the shop's own KITE server over the LAN
// and adds what a browser cannot do (Phase 4): receipt printing to USB (Windows spooler, RAW) or network printers,
// the cash drawer kick, and emergency mode, an encrypted local store with the POS bundled in the app, used while the
// server cannot be reached. Signed installers and auto-update come in Phase 14.
import { app, BrowserWindow, ipcMain, Menu, safeStorage, shell, type IpcMainInvokeEvent } from 'electron';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { performance } from 'node:perf_hooks';
import { checkPrinter, ConfigFile, normaliseServerUrl, type PrinterConfig } from './config';
import { EmergencyStore, type KeyProtector, type RecordKind } from './emergencyStore';
import { verifyPin } from './pin';
import { decodeJob, DRAWER_KICK, listWindowsPrinters, printBytes } from './printer';

export { normaliseServerUrl };

// A separate profile folder can be chosen for testing or for two installs on one PC.
if (process.env.KITE_DESKTOP_PROFILE) app.setPath('userData', process.env.KITE_DESKTOP_PROFILE);

const config = new ConfigFile(path.join(app.getPath('userData'), 'kite-desktop.json'));
const bundledApp = () => path.join(__dirname, '..', 'static', 'app', 'index.html');
let win: BrowserWindow | null = null;
let mode: 'online' | 'emergency' | 'setup' = 'setup';
let store: EmergencyStore | null = null;

/** DPAPI (current Windows user) through Electron safeStorage. */
const dpapi: KeyProtector = {
  protect: (key) => {
    if (!safeStorage.isEncryptionAvailable()) throw new Error('Windows data protection is not available on this PC.');
    return safeStorage.encryptString(key.toString('hex'));
  },
  unprotect: (blob) => Buffer.from(safeStorage.decryptString(blob), 'hex'),
};

function emergencyStore(): EmergencyStore {
  store ??= EmergencyStore.open(path.join(app.getPath('userData'), 'emergency'), dpapi);
  return store;
}

function loadOnline() {
  if (!win) return;
  const { serverUrl } = config.read();
  if (serverUrl) { mode = 'online'; void win.loadURL(`${serverUrl}/pos`); }
  else { mode = 'setup'; void win.loadFile(path.join(__dirname, '..', 'static', 'connect.html')); }
}

function loadOffline(reason: string) {
  if (!win) return;
  mode = 'setup';
  void win.loadFile(path.join(__dirname, '..', 'static', 'offline.html'), { query: { reason } });
}

/** IPC is answered only for our own pages: the shop's KITE server, or the pages bundled with the app. */
function trusted(e: IpcMainInvokeEvent): boolean {
  const url = e.senderFrame?.url ?? '';
  if (url.startsWith('file://')) {
    try { return path.resolve(fileURLToPath(url)).startsWith(path.resolve(__dirname, '..', 'static') + path.sep); } catch { return false; }
  }
  const server = config.read().serverUrl;
  try { return !!server && new URL(url).origin === server; } catch { return false; }
}

function handle<T>(channel: string, fn: (e: IpcMainInvokeEvent, ...args: never[]) => T | Promise<T>) {
  ipcMain.handle(channel, async (e, ...args) => {
    if (!trusted(e)) throw new Error('Not allowed from this page.');
    return fn(e, ...(args as never[]));
  });
}

const ok = async (f: () => Promise<unknown>) => { try { await f(); return { ok: true }; } catch (err) { return { ok: false, error: err instanceof Error ? err.message : String(err) }; } };

function createWindow() {
  win = new BrowserWindow({
    width: 1366, height: 860, minWidth: 1024, minHeight: 700, title: 'KITE MOBILE ERP', backgroundColor: '#f6f7fb',
    webPreferences: { preload: path.join(__dirname, 'preload.js'), contextIsolation: true, sandbox: true, nodeIntegration: false, webSecurity: true },
  });
  // Stay on the shop's KITE server or the bundled pages; anything else opens in the system browser.
  win.webContents.on('will-navigate', (event, url) => {
    const allowed = config.read().serverUrl;
    if (url.startsWith('file://')) return;
    if (!allowed || new URL(url).origin !== allowed) {
      event.preventDefault();
      if (/^https?:/.test(url)) void shell.openExternal(url);
    }
  });
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/.test(url) || url.startsWith('blob:')) void shell.openExternal(url);
    return { action: 'deny' };
  });
  win.webContents.session.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));
  // The server cannot be reached: offer to retry or to start emergency mode.
  win.webContents.on('did-fail-load', (_e, code, description, url, isMainFrame) => {
    if (isMainFrame && mode === 'online' && code !== -3) loadOffline(`${description} (${url})`);
  });
  loadOnline();
}

// ------------------------------------------------------------------ server address
handle('kite:get-server-url', () => config.read().serverUrl ?? null);
handle('kite:set-server-url', (_e, input: string) => {
  const url = normaliseServerUrl(input);
  if (!url) return { ok: false };
  config.update({ serverUrl: url });
  loadOnline();
  return { ok: true, url };
});
handle('kite:reset-server', () => { config.update({ serverUrl: undefined }); loadOnline(); });
handle('kite:retry', () => loadOnline());

// ------------------------------------------------------------------ printing and the drawer
handle('kite:print', (_e, base64: string) => ok(() => printBytes(config.read().printer, decodeJob(base64))));
handle('kite:open-drawer', () => ok(async () => {
  const p = config.read().printer;
  if (p && p.drawer === false) throw new Error('No cash drawer is connected to this till.');
  await printBytes(p, DRAWER_KICK);
}));
handle('kite:printer-name', () => { const p = config.read().printer; return !p || p.mode === 'none' ? null : p.mode === 'network' ? `${p.host}:${p.port ?? 9100}` : p.name ?? null; });
handle('kite:get-printer', () => config.read().printer ?? { mode: 'none' });
handle('kite:set-printer', (_e, p: PrinterConfig) => {
  const error = checkPrinter(p);
  if (error) return { ok: false, error };
  config.update({ printer: { mode: p.mode, name: p.name?.trim(), host: p.host?.trim(), port: p.port ?? 9100, paperWidthMm: p.paperWidthMm ?? 80, drawer: p.drawer ?? true } });
  return { ok: true };
});
handle('kite:list-printers', () => listWindowsPrinters().catch(() => []));

// ------------------------------------------------------------------ emergency mode
interface SnapshotUser { id: string; username: string; displayNameEn: string; displayNameAr: string | null; pinHash: string | null; permissions: string[] }
const snapshotUsers = (): SnapshotUser[] => ((emergencyStore().snapshot() as { users?: SnapshotUser[] } | null)?.users ?? []);

handle('kite:mode', () => mode);
handle('kite:emergency:status', () => emergencyStore().status());
handle('kite:emergency:snapshot', () => emergencyStore().snapshot());
handle('kite:emergency:save-snapshot', (_e, s: { numberPrefix?: string; generatedAt?: string }) => ok(async () => emergencyStore().saveSnapshot(s)));
handle('kite:emergency:append', (_e, kind: RecordKind, payload: Record<string, unknown>) => {
  if (mode !== 'emergency') throw new Error('Records are kept only in emergency mode.');
  return emergencyStore().append(kind, payload, performance.now());
});
handle('kite:emergency:pending', () => emergencyStore().pending());
handle('kite:emergency:records', () => emergencyStore().records());
handle('kite:emergency:mark-uploaded', (_e, results: { seq: number; result: 'applied' | 'conflict'; detail: string | null }[]) => emergencyStore().markUploaded(results));
handle('kite:emergency:verify-pin', async (_e, username: string, pin: string) => {
  const u = snapshotUsers().find((x) => x.username === String(username).trim().toLowerCase());
  if (!u || !(await verifyPin(String(pin), u.pinHash))) return null;
  return { id: u.id, username: u.username, displayNameEn: u.displayNameEn, displayNameAr: u.displayNameAr, permissions: u.permissions };
});
// Starting emergency mode needs a user with POS.EMERGENCY.ACTIVATE and their PIN, checked against the snapshot.
handle('kite:emergency:start', async (_e, username: string, pin: string) => {
  const s = emergencyStore().status();
  if (!s.snapshotAt) return { ok: false, error: 'This till has no snapshot from the server yet, so it cannot sell in emergency mode.' };
  const u = snapshotUsers().find((x) => x.username === String(username).trim().toLowerCase());
  if (!u || !(await verifyPin(String(pin), u.pinHash))) return { ok: false, error: 'Username or PIN is not correct.' };
  if (!u.permissions.includes('POS.EMERGENCY.ACTIVATE')) return { ok: false, error: 'This user may not start emergency mode.' };
  mode = 'emergency';
  void win?.loadFile(bundledApp(), { query: { emergency: '1' } });
  return { ok: true };
});
handle('kite:emergency:end', () => {
  if (emergencyStore().status().shift) return { ok: false, error: 'Close the emergency shift first.' };
  loadOnline();
  return { ok: true };
});

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => { if (win) { if (win.isMinimized()) win.restore(); win.focus(); } });
  app.whenReady().then(() => {
    Menu.setApplicationMenu(Menu.buildFromTemplate([
      { label: 'KITE', submenu: [
        { label: 'Change server…', click: () => { mode = 'setup'; void win?.loadFile(path.join(__dirname, '..', 'static', 'connect.html')); } },
        { label: 'Printer settings…', click: () => { mode = 'setup'; void win?.loadFile(path.join(__dirname, '..', 'static', 'printer.html')); } },
        { label: 'Back to KITE', click: () => loadOnline() },
        { role: 'reload' },
        { role: 'togglefullscreen' },
        { type: 'separator' },
        { role: 'quit' },
      ] },
      ...(app.isPackaged ? [] : [{ label: 'Developer', submenu: [{ role: 'toggleDevTools' as const }] }]),
    ]));
    createWindow();
  });
  app.on('window-all-closed', () => { store?.close(); app.quit(); });
}
