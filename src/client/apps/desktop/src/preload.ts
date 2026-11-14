// The only bridge between the web app and the desktop. Nothing from Node is exposed directly
// (contextIsolation + sandbox). Phase 4 adds printing, cash drawer and emergency-store calls here.
import { contextBridge, ipcRenderer } from 'electron';

contextBridge.exposeInMainWorld('kiteDesktop', {
  isDesktop: true,
  version: process.env.npm_package_version ?? '0.2.0',
  getServerUrl: (): Promise<string | null> => ipcRenderer.invoke('kite:get-server-url'),
  setServerUrl: (url: string): Promise<{ ok: boolean; url?: string }> => ipcRenderer.invoke('kite:set-server-url', url),
  resetServer: (): Promise<void> => ipcRenderer.invoke('kite:reset-server'),
});
