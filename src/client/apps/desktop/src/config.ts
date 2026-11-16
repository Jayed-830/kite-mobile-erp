// Settings of this Windows till, kept in the user's profile (kite-desktop.json). Secrets are not kept here.
import fs from 'node:fs';
import path from 'node:path';

export type PrinterMode = 'none' | 'windows' | 'network';
export interface PrinterConfig { mode: PrinterMode; name?: string; host?: string; port?: number; paperWidthMm?: 58 | 80; drawer?: boolean }
export interface DesktopConfig { serverUrl?: string; printer?: PrinterConfig }

export class ConfigFile {
  constructor(private readonly file: string) {}
  read(): DesktopConfig {
    try { return JSON.parse(fs.readFileSync(this.file, 'utf8')) as DesktopConfig; } catch { return {}; }
  }
  write(cfg: DesktopConfig) {
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    fs.writeFileSync(this.file, JSON.stringify(cfg, null, 2));
  }
  update(patch: Partial<DesktopConfig>): DesktopConfig {
    const next = { ...this.read(), ...patch };
    this.write(next);
    return next;
  }
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

/** Checks printer settings from the settings page before they are saved. */
export function checkPrinter(p: PrinterConfig): string | null {
  if (!['none', 'windows', 'network'].includes(p.mode)) return 'Choose how the receipt printer is connected.';
  if (p.mode === 'windows' && !p.name?.trim()) return 'Choose the Windows printer.';
  if (p.mode === 'network' && !/^[A-Za-z0-9.-]{1,253}$/.test(p.host ?? '')) return 'Enter the printer address, for example 192.168.1.50.';
  if (p.port !== undefined && (!Number.isInteger(p.port) || p.port < 1 || p.port > 65535)) return 'The port is 1 to 65535 (usually 9100).';
  if (p.paperWidthMm !== undefined && p.paperWidthMm !== 58 && p.paperWidthMm !== 80) return 'Paper is 58 or 80 mm.';
  return null;
}
