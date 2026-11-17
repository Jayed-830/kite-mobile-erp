// The only bridge between the web app and the desktop (contextIsolation + sandbox): printing, the cash drawer and the
// emergency store. Nothing from Node is exposed directly; the main process checks which page is calling.
import { contextBridge, ipcRenderer } from 'electron';

const call = <T>(channel: string, ...args: unknown[]): Promise<T> => ipcRenderer.invoke(channel, ...args) as Promise<T>;

contextBridge.exposeInMainWorld('kiteDesktop', {
  isDesktop: true,
  version: process.env.npm_package_version ?? '0.10.0',
  getServerUrl: () => call<string | null>('kite:get-server-url'),
  setServerUrl: (url: string) => call<{ ok: boolean; url?: string }>('kite:set-server-url', url),
  resetServer: () => call<void>('kite:reset-server'),
  retry: () => call<void>('kite:retry'),
  mode: () => call<'online' | 'emergency' | 'setup'>('kite:mode'),
  print: (bytesBase64: string) => call<{ ok: boolean; error?: string }>('kite:print', bytesBase64),
  openDrawer: () => call<{ ok: boolean; error?: string }>('kite:open-drawer'),
  printerName: () => call<string | null>('kite:printer-name'),
  getPrinter: () => call<unknown>('kite:get-printer'),
  setPrinter: (p: unknown) => call<{ ok: boolean; error?: string }>('kite:set-printer', p),
  listPrinters: () => call<string[]>('kite:list-printers'),
  emergency: {
    status: () => call<unknown>('kite:emergency:status'),
    snapshot: () => call<unknown>('kite:emergency:snapshot'),
    saveSnapshot: (s: unknown) => call<{ ok: boolean; error?: string }>('kite:emergency:save-snapshot', s),
    append: (kind: string, payload: unknown) => call<unknown>('kite:emergency:append', kind, payload),
    pending: () => call<unknown[]>('kite:emergency:pending'),
    records: () => call<unknown[]>('kite:emergency:records'),
    markUploaded: (results: unknown[]) => call<void>('kite:emergency:mark-uploaded', results),
    verifyPin: (username: string, pin: string) => call<unknown>('kite:emergency:verify-pin', username, pin),
    start: (username: string, pin: string) => call<{ ok: boolean; error?: string }>('kite:emergency:start', username, pin),
    end: () => call<{ ok: boolean; error?: string }>('kite:emergency:end'),
  },
});
