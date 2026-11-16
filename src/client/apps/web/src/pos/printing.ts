// Receipt printing (10.5): the Windows app prints the raster bytes to its USB/LAN printer and kicks the drawer; a
// browser terminal sends the same bytes to the server's print relay when a network printer is set for this
// terminal, and otherwise uses the browser's print dialog with the HTML receipt.
import type { KiteApi } from '@kite/api';
import { DRAWER_KICK, receiptBytes, toBase64, type CanvasLike, type ReceiptModel } from '@kite/pos';

export interface DesktopEmergency {
  status(): Promise<unknown>;
  snapshot(): Promise<unknown>;
  saveSnapshot(s: unknown): Promise<{ ok: boolean; error?: string }>;
  append(kind: string, payload: unknown): Promise<unknown>;
  pending(): Promise<unknown[]>;
  records(): Promise<unknown[]>;
  markUploaded(results: unknown[]): Promise<void>;
  verifyPin(username: string, pin: string): Promise<unknown>;
  start(username: string, pin: string): Promise<{ ok: boolean; error?: string }>;
  end(): Promise<{ ok: boolean; error?: string }>;
}
export interface DesktopBridge {
  print(bytesBase64: string): Promise<{ ok: boolean; error?: string }>;
  openDrawer(): Promise<{ ok: boolean; error?: string }>;
  printerName(): Promise<string | null>;
  emergency?: DesktopEmergency;
}
declare global { interface Window { kiteDesktop?: DesktopBridge } }

/** True when the Windows app runs its bundled POS because the server cannot be reached. */
export const isEmergencyMode = () => typeof window !== 'undefined' && !!window.kiteDesktop?.emergency && new URLSearchParams(window.location.search).get('emergency') === '1';

export const desktop = (): DesktopBridge | null => (typeof window !== 'undefined' && window.kiteDesktop ? window.kiteDesktop : null);

const makeCanvas = (w: number, h: number): CanvasLike => {
  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  return c as unknown as CanvasLike;
};

export type PrintRoute = { kind: 'desktop' } | { kind: 'relay'; printerId: string } | { kind: 'browser' };

export function printRoute(printerId: string | null): PrintRoute {
  if (desktop()) return { kind: 'desktop' };
  if (printerId) return { kind: 'relay', printerId };
  return { kind: 'browser' };
}

/** Prints a receipt; `drawer` adds the drawer kick (cash sales). Returns an error message or null. */
export async function printReceipt(api: KiteApi, r: ReceiptModel, route: PrintRoute, drawer: boolean): Promise<string | null> {
  try {
    if (route.kind === 'browser') {
      window.print();
      return null;
    }
    const bytes = receiptBytes(r, makeCanvas, { drawer });
    if (route.kind === 'desktop') {
      const res = await desktop()!.print(toBase64(bytes));
      return res.ok ? null : res.error ?? 'print failed';
    }
    await api.printJob(route.printerId, { kind: 'receipt', data: toBase64(bytes), saleId: r.saleId });
    return null;
  } catch (e) {
    return e instanceof Error ? e.message : String(e);
  }
}

export async function openDrawer(api: KiteApi, route: PrintRoute): Promise<string | null> {
  try {
    if (route.kind === 'desktop') { const r = await desktop()!.openDrawer(); return r.ok ? null : r.error ?? 'drawer failed'; }
    if (route.kind === 'relay') { await api.printJob(route.printerId, { kind: 'drawer' }); return null; }
    return null;
  } catch (e) {
    return e instanceof Error ? e.message : String(e);
  }
}

export const drawerKickBase64 = () => toBase64(new Uint8Array(DRAWER_KICK));
