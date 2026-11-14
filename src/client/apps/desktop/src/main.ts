// KITE Desktop (Electron main process), Phase 2 shell: it shows the KITE web app served by the
// shop's own KITE server over the LAN. The hardware bridge (printers, drawer, scanners) and the
// encrypted emergency store are added in Phase 4; signed installers and auto-update in Phase 14.
import { app, BrowserWindow, ipcMain, Menu, shell } from 'electron';
import fs from 'node:fs';
import path from 'node:path';

interface DesktopConfig { serverUrl?: string }

const configPath = () => path.join(app.getPath('userData'), 'kite-desktop.json');

function readConfig(): DesktopConfig {
  try {
    return JSON.parse(fs.readFileSync(configPath(), 'utf8')) as DesktopConfig;
  } catch {
    return {};
  }
}

function writeConfig(cfg: DesktopConfig) {
  fs.mkdirSync(path.dirname(configPath()), { recursive: true });
  fs.writeFileSync(configPath(), JSON.stringify(cfg, null, 2));
}

/** Accepts http(s) addresses only and normalises them to an origin, e.g. https://kite.local:5080 */
export function normaliseServerUrl(input: string): string | null {
  try {
    const u = new URL(input.trim().includes('://') ? input.trim() : `https://${input.trim()}`);
    if (u.protocol !== 'https:' && u.protocol !== 'http:') return null;
    return u.origin;
  } catch {
    return null;
  }
}

// A separate profile folder can be chosen for testing or for two installs on one PC.
if (process.env.KITE_DESKTOP_PROFILE) app.setPath('userData', process.env.KITE_DESKTOP_PROFILE);

let win: BrowserWindow | null = null;

function loadApp() {
  if (!win) return;
  const { serverUrl } = readConfig();
  if (serverUrl) void win.loadURL(`${serverUrl}/app`);
  else void win.loadFile(path.join(__dirname, '..', 'static', 'connect.html'));
}

function createWindow() {
  win = new BrowserWindow({
    width: 1366,
    height: 860,
    minWidth: 1024,
    minHeight: 700,
    title: 'KITE MOBILE ERP',
    backgroundColor: '#f6f7fb',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
      webSecurity: true,
    },
  });

  // Stay on the shop's KITE server; anything else opens in the system browser.
  win.webContents.on('will-navigate', (event, url) => {
    const allowed = readConfig().serverUrl;
    if (url.startsWith('file://')) return;
    if (!allowed || new URL(url).origin !== allowed) {
      event.preventDefault();
      if (/^https?:/.test(url)) void shell.openExternal(url);
    }
  });
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/.test(url)) void shell.openExternal(url);
    return { action: 'deny' };
  });
  win.webContents.session.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));

  loadApp();
}

ipcMain.handle('kite:get-server-url', () => readConfig().serverUrl ?? null);
ipcMain.handle('kite:set-server-url', (_e, input: string) => {
  const url = normaliseServerUrl(input);
  if (!url) return { ok: false };
  writeConfig({ ...readConfig(), serverUrl: url });
  loadApp();
  return { ok: true, url };
});
ipcMain.handle('kite:reset-server', () => {
  writeConfig({ ...readConfig(), serverUrl: undefined });
  loadApp();
});

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (win) {
      if (win.isMinimized()) win.restore();
      win.focus();
    }
  });
  app.whenReady().then(() => {
    Menu.setApplicationMenu(Menu.buildFromTemplate([
      { label: 'KITE', submenu: [
        { label: 'Change server…', click: () => win?.webContents.send('kite:show-connect') },
        { role: 'reload' },
        { role: 'togglefullscreen' },
        { type: 'separator' },
        { role: 'quit' },
      ] },
      ...(app.isPackaged ? [] : [{ label: 'Developer', submenu: [{ role: 'toggleDevTools' as const }] }]),
    ]));
    createWindow();
  });
  app.on('window-all-closed', () => app.quit());
}
