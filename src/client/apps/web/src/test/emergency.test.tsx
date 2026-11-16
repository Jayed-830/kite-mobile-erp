import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { createEmergencyApi, type Snapshot } from '../pos/emergencyApi';

/** The Windows app's encrypted store, in memory: the same numbering as EmergencyStore.append. */
function fakeStore(snapshot: Snapshot) {
  const records: { seq: number; id: string; kind: string; recordedAt: string; payload: Record<string, unknown> }[] = [];
  let shift: unknown = null; let sale = 0; let shifts = 0;
  const printed: string[] = [];
  const bridge = {
    snapshot: async () => snapshot,
    status: async () => ({ shift, pending: records.length }),
    append: async (kind: string, payload: Record<string, unknown>) => {
      const body = { ...payload };
      if (kind === 'sale') body.number = `${snapshot.numberPrefix}${String(++sale).padStart(6, '0')}`;
      if (kind === 'shift_open') { body.number = `EMS-${snapshot.numberPrefix.slice(3)}${String(++shifts).padStart(6, '0')}`; shift = body; }
      if (kind === 'shift_close') shift = null;
      const r = { seq: records.length + 1, id: crypto.randomUUID(), kind, recordedAt: new Date().toISOString(), payload: body };
      records.push(r);
      return r;
    },
    records: async () => [...records].reverse(),
    pending: async () => records,
    saveSnapshot: async () => ({ ok: true }),
    markUploaded: async () => undefined,
    verifyPin: async (username: string, pin: string) => {
      const pins: Record<string, string> = { cashier: '2468', manager: '1357' };
      const u = snapshot.users.find((x) => x.username === username);
      return u && pins[username] === pin ? u : null;
    },
    start: async () => ({ ok: true }),
    end: async () => ({ ok: true }),
  };
  return { bridge, records, printed };
}

const snapshot: Snapshot = {
  generatedAt: '2026-11-16T05:00:00Z', businessDate: '2026-11-16', numberPrefix: 'EM-B01-T05-',
  policy: { id: 'p', branchId: null, negativeStock: 'block', returnWindowDays: 14, cashRefundCap: 100, varianceThreshold: 1, blindClose: true, roundingLevel: 'line', cashRounding: 0.005,
    denominations: [50, 20, 10, 5, 1, 0.5, 0.1, 0.05, 0.025, 0.01, 0.005], receiptFooterEn: 'Thank you', receiptFooterAr: 'شكراً', version: 1 },
  items: [{ variantId: 'v-cable', sku: 'CBL-C-1M', productType: 'accessory', tracking: 'none', descriptionEn: 'USB-C cable 1m', descriptionAr: 'كابل USB-C متر', taxCodeId: 'tax-std',
    taxKind: 'standard', taxRate: 5, listPrice: 2.333, minPrice: null, warrantyDays: 30, active: true, stock: 4, barcode: '6291000000011' }],
  barcodes: [{ code: '6291000000011', variantId: 'v-cable' }],
  users: [
    { id: 'u-cash', username: 'cashier', displayNameEn: 'Ahmed', displayNameAr: 'أحمد', permissions: ['POS.SELL', 'POS.SHIFT.OPEN'], discountLimitPercent: 5, discountLimitAmount: 2 },
    { id: 'u-mgr', username: 'manager', displayNameEn: 'Huda', displayNameAr: 'هدى', permissions: ['POS.SELL', 'POS.SHIFT.OPEN', 'POS.SHIFT.ACCEPT_VARIANCE', 'POS.EMERGENCY.ACTIVATE'] },
  ],
  business: { legalNameEn: 'Al Noor Mobiles LLC', legalNameAr: 'شركة النور للهواتف', tradeNameEn: null, tradeNameAr: null, vatNumber: 'OM1100223344', crNumber: null, pricesIncludeTax: true, addressEn: null, addressAr: null, phone: null },
  terminal: { id: 't5', code: 'T05', branchId: 'b1', branchCode: 'B01', branchNameEn: 'Muscat - Ruwi', branchNameAr: 'مسقط - روي' },
};

describe('emergency mode (Windows app, server unreachable)', () => {
  afterEach(() => { delete window.kiteDesktop; window.history.replaceState({}, '', '/'); });

  it('sells from the snapshot, numbers from the terminal series and keeps records the server can apply', async () => {
    const { bridge, records } = fakeStore(snapshot);
    const printed: string[] = [];
    window.kiteDesktop = { emergency: bridge, print: async (b) => { printed.push(b); return { ok: true }; }, openDrawer: async () => ({ ok: true }), printerName: async () => 'EPSON TM-T20' };
    window.history.replaceState({}, '', '/?emergency=1');
    const user = userEvent.setup();
    render(<App api={createEmergencyApi(bridge)} initialPath="/pos" lang="en" />);

    await user.type(await screen.findByLabelText('Username'), 'cashier');
    await user.type(screen.getByLabelText('PIN'), '2468');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await user.type(await screen.findByLabelText('Pieces of 10'), '1');
    await user.click(screen.getByRole('button', { name: 'Open shift' }));
    expect(await screen.findByText('Emergency mode')).toBeInTheDocument();

    const scan = screen.getByLabelText('Scan a barcode or type a name');
    await user.type(scan, '6291000000011{Enter}');
    await user.type(scan, '6291000000011{Enter}');
    await user.type(scan, '6291000000011{Enter}');
    expect(await screen.findByRole('button', { name: /^Pay 6\.999/ })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /^Pay 6\.999/ }));
    const pay = await screen.findByRole('dialog', { name: 'Payment' });
    expect(within(pay).getAllByText('7.000').length).toBeGreaterThan(0);   // cash rounded to 0.005
    await user.click(within(pay).getByRole('button', { name: 'Complete sale' }));

    const receipt = await screen.findByRole('dialog', { name: 'EM-B01-T05-000001' });
    expect(within(receipt).getByLabelText('Receipt EM-B01-T05-000001')).toHaveTextContent('EMERGENCY MODE');
    expect(printed.length).toBe(0);   // jsdom has no canvas; the print route failed and is reported, not hidden
    expect(records.map((r) => r.kind)).toEqual(['shift_open', 'sale']);
    const sale = records[1]!.payload as { number: string; cashierId: string; shiftId: string; cashRounding: number; lines: { variantId: string; qty: number; net: number; tax: number; gross: number; taxCodeId: string; productType: string }[]; payments: { method: string; amount: number }[] };
    expect(sale.number).toBe('EM-B01-T05-000001');
    expect(sale.shiftId).toBe(records[0]!.payload.shiftId);
    expect(sale.lines).toEqual([expect.objectContaining({ variantId: 'v-cable', qty: 3, net: 6.666, tax: 0.333, gross: 6.999, taxCodeId: 'tax-std', productType: 'accessory' })]);
    expect(sale.payments).toEqual([expect.objectContaining({ method: 'cash', amount: 7 })]);
    expect(sale.cashRounding).toBe(0.001);
  });

  it('refuses work that needs the server, with a clear message', async () => {
    const { bridge } = fakeStore(snapshot);
    const api = createEmergencyApi(bridge);
    await api.pinLoginAt('cashier', '2468', { terminalId: 't5', deviceKey: '' });
    await expect(api.saleByNumber('INV-1')).rejects.toMatchObject({ code: 'POS-018' });
    await expect(api.loginAt('cashier', 'x', { terminalId: 't5', deviceKey: '' })).rejects.toMatchObject({ code: 'POS-018' });
    await expect(api.pinLoginAt('cashier', '0000', { terminalId: 't5', deviceKey: '' })).rejects.toMatchObject({ code: 'AUTH-001' });
  });
});
