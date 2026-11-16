// The hardware bridge for receipts (10.5): the web app renders the receipt as ESC/POS raster bytes; this module sends
// them to the printer. A network printer gets raw TCP (port 9100). A USB printer installed in Windows gets the bytes
// through the Windows spooler in RAW mode (OpenPrinter / StartDocPrinter / WritePrinter), so the cash drawer kick
// (ESC p) reaches the printer exactly as sent.
import { execFile } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import type { PrinterConfig } from './config';

export const DRAWER_KICK = Buffer.from([0x1b, 0x70, 0x00, 0x19, 0xfa]);
export const MAX_JOB_BYTES = 4 * 1024 * 1024;

export function sendToNetworkPrinter(host: string, port: number, data: Buffer, timeoutMs = 5000): Promise<void> {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host, port });
    const fail = (e: Error) => { socket.destroy(); reject(e); };
    socket.setTimeout(timeoutMs, () => fail(new Error(`The printer at ${host}:${port} did not answer.`)));
    socket.once('error', fail);
    socket.once('connect', () => socket.end(data, () => resolve()));
  });
}

const RAW_PRINT_PS1 = `
param([Parameter(Mandatory=$true)][string]$Printer, [Parameter(Mandatory=$true)][string]$File)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class KiteRawPrinter {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public class DOCINFO { public string pDocName; public string pOutputFile; public string pDataType; }
  [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool OpenPrinter(string name, out IntPtr h, IntPtr d);
  [DllImport("winspool.drv", SetLastError = true)] public static extern bool ClosePrinter(IntPtr h);
  [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] public static extern int StartDocPrinter(IntPtr h, int level, DOCINFO di);
  [DllImport("winspool.drv", SetLastError = true)] public static extern bool EndDocPrinter(IntPtr h);
  [DllImport("winspool.drv", SetLastError = true)] public static extern bool StartPagePrinter(IntPtr h);
  [DllImport("winspool.drv", SetLastError = true)] public static extern bool EndPagePrinter(IntPtr h);
  [DllImport("winspool.drv", SetLastError = true)] public static extern bool WritePrinter(IntPtr h, byte[] b, int n, out int written);
  public static void Send(string printer, byte[] bytes) {
    IntPtr h; if (!OpenPrinter(printer, out h, IntPtr.Zero)) throw new Exception("Cannot open printer " + printer + " (" + Marshal.GetLastWin32Error() + ")");
    try {
      var di = new DOCINFO { pDocName = "KITE receipt", pDataType = "RAW" };
      if (StartDocPrinter(h, 1, di) == 0) throw new Exception("StartDocPrinter failed (" + Marshal.GetLastWin32Error() + ")");
      try { StartPagePrinter(h); int w; if (!WritePrinter(h, bytes, bytes.Length, out w) || w != bytes.Length) throw new Exception("WritePrinter failed"); EndPagePrinter(h); }
      finally { EndDocPrinter(h); }
    } finally { ClosePrinter(h); }
  }
}
"@
[KiteRawPrinter]::Send($Printer, [System.IO.File]::ReadAllBytes($File))
`;

function powershell(args: string[], timeoutMs = 30000): Promise<string> {
  return new Promise((resolve, reject) => {
    execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', ...args], { timeout: timeoutMs, windowsHide: true }, (err, stdout, stderr) => {
      if (err) reject(new Error((stderr || err.message).trim())); else resolve(stdout);
    });
  });
}

/** Sends raw bytes to an installed Windows printer through the spooler. */
export async function printRawWindows(printer: string, data: Buffer, tempDir = os.tmpdir()): Promise<void> {
  if (process.platform !== 'win32') throw new Error('Windows printers are available on Windows only.');
  const dir = fs.mkdtempSync(path.join(tempDir, 'kite-print-'));
  try {
    const script = path.join(dir, 'raw-print.ps1');
    const job = path.join(dir, 'receipt.bin');
    fs.writeFileSync(script, RAW_PRINT_PS1);
    fs.writeFileSync(job, data);
    await powershell(['-File', script, '-Printer', printer, '-File', job]);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

export async function listWindowsPrinters(): Promise<string[]> {
  if (process.platform !== 'win32') return [];
  const out = await powershell(['-Command', 'Get-Printer | Select-Object -ExpandProperty Name']);
  return out.split(/\r?\n/).map((s) => s.trim()).filter(Boolean);
}

/** Decodes a print job from the renderer and refuses anything that is not a sensible receipt. */
export function decodeJob(base64: string): Buffer {
  if (typeof base64 !== 'string' || base64.length === 0 || base64.length > (MAX_JOB_BYTES * 4) / 3 + 4) throw new Error('The print job is empty or too large.');
  const b = Buffer.from(base64, 'base64');
  if (b.length === 0) throw new Error('The print job is not valid.');
  return b;
}

export async function printBytes(cfg: PrinterConfig | undefined, data: Buffer): Promise<void> {
  if (!cfg || cfg.mode === 'none') throw new Error('No receipt printer is set up on this till (KITE menu → Printer settings).');
  if (cfg.mode === 'network') return sendToNetworkPrinter(cfg.host!, cfg.port ?? 9100, data);
  return printRawWindows(cfg.name!, data);
}
