import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { createMockApi } from '../mock';
import { createEmergencyApi, type Snapshot } from '../pos/emergencyApi';

const OWNER_PW = 'Muscat-Ruwi-2026!';
// Seeded in the mock: a Galaxy whose IMEI head office also found at Sohar (B02), and one IMEI on the blocked list.
const CONFLICT = '356938035643809';
const BLOCKED = '351756051523993';

beforeEach(() => { window.print = () => undefined; });

async function owner() {
  const api = createMockApi({ configured: true });
  await api.login('owner', OWNER_PW);
  return api;
}

describe('IMEI conflicts (Phase 6)', () => {
  it('lists the conflict, and correcting the mistyped IMEI where the device is closes it and releases the device', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/devices" lang="en" />);
    expect(await screen.findByTestId('central-banner')).toHaveTextContent('1 devices are on hold because of an IMEI conflict.');

    await user.click(screen.getByRole('tab', { name: 'IMEI conflicts' }));
    const value = await screen.findByText(CONFLICT, { selector: '.mono' });
    const table = value.closest('table')!;
    expect(within(table).getByText('B02')).toBeInTheDocument();
    await user.click(within(table).getByRole('link', { name: 'Open the device' }));

    expect(await screen.findByTestId('holds')).toHaveTextContent('IMEI conflict');
    expect(screen.getAllByText('IMEI conflict', { selector: '.badge' }).length).toBe(2);   // what head office said, and the hold
    expect(screen.queryByRole('button', { name: 'Release' })).not.toBeInTheDocument();   // a conflict hold is not released by hand

    await user.click(screen.getByRole('button', { name: 'Correct the IMEI' }));
    const dialog = await screen.findByRole('dialog', { name: 'Correct the IMEI' });
    await user.type(within(dialog).getByLabelText('Correct IMEI'), '356938035643817');
    await user.type(within(dialog).getByLabelText('Why is it corrected?'), 'Typed one digit wrong at receiving');
    await user.click(within(dialog).getByRole('button', { name: 'Correct the IMEI' }));
    expect(await within(dialog).findByText('The IMEI is corrected.')).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Close' }));

    await waitFor(() => expect(screen.getByTestId('identifiers')).toHaveTextContent('356938035643817'));
    expect(screen.getByTestId('identifiers')).toHaveTextContent(`${CONFLICT} Replaced`);
    expect((await api.devices({ q: '356938035643817' }))[0]).toMatchObject({ status: 'in_stock', centralState: 'verified' });
    const conflicts = await api.deviceConflicts();
    expect(conflicts[0]).toMatchObject({ status: 'resolved' });
    expect(conflicts[0]!.sides.map((s) => s.outcome).sort()).toEqual(['corrected', 'kept']);
  });

  it('reverses the receipt of a device that was never really here', async () => {
    const api = await owner();
    const [unit] = await api.devices({ q: CONFLICT });
    const r = await api.reverseReceipt(unit!.id, 'Received twice by mistake');
    expect(r.number).toMatch(/^ADJ-/);
    const d = await api.device(unit!.id);
    expect(d.unit.status).toBe('reversed');
    expect(d.identifiers?.find((x) => x.value === CONFLICT)?.status).toBe('retired');
    expect((await api.deviceConflicts('open')).length).toBe(0);
    // An ordinary device is written off instead.
    const [other] = await api.devices({ q: '352099001761481' });
    await expect(api.reverseReceipt(other!.id, 'x')).rejects.toMatchObject({ code: 'DEV-018' });
  });
});

describe('receiving and the blocked list (Phase 6)', () => {
  it('checks each scanned IMEI with the server and refuses to post a blocked one', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/inventory" lang="en" />);
    await user.click(await screen.findByRole('tab', { name: 'Adjust stock' }));
    await user.type(screen.getByLabelText('Add an item'), 'Galaxy');
    await user.click(await screen.findByRole('button', { name: /Galaxy A55 128GB/ }));
    await user.type(screen.getByLabelText('Unit cost'), '118.000');
    const scanner = screen.getByLabelText('Scan the IMEIs');
    await user.type(scanner, '354670112345733{Enter}');
    await user.type(scanner, `${BLOCKED}{Enter}`);
    await user.type(scanner, '352099001761481{Enter}');   // already a device of this business
    await waitFor(() => expect(screen.getByText(/is on the blocked list: Reported stolen/)).toBeInTheDocument());
    expect(await screen.findByText(/already belongs to a device in stock at B01/)).toBeInTheDocument();
    expect(screen.getByText('Remove the devices with a problem before saving.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Post' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: `Remove ${BLOCKED}` }));
    await user.click(screen.getByRole('button', { name: 'Remove 352099001761481' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Post' })).toBeEnabled());
  });

  it('adds an IMEI to the blocked list, which puts the device in stock on hold, and taking it off releases it', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/devices" lang="en" />);
    await user.click(await screen.findByRole('tab', { name: 'Blocked IMEIs' }));
    expect(await screen.findByText(BLOCKED)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Add to the blocked list' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add to the blocked list' });
    await user.type(within(dialog).getByLabelText('IMEI'), '354670112345675');
    await user.type(within(dialog).getByLabelText('Reason'), 'Reported stolen');
    await user.click(within(dialog).getByRole('button', { name: 'Add to the blocked list' }));
    expect(await screen.findByText('354670112345675')).toBeInTheDocument();
    const [unit] = await api.devices({ q: '354670112345675' });
    expect(unit!.status).toBe('quarantined');
    expect((await api.device(unit!.id)).holds?.[0]).toMatchObject({ reason: 'blocked_imei', releasedAt: null });

    const row = screen.getByText('354670112345675').closest('tr')!;
    await user.click(within(row).getByRole('button', { name: 'Take off the list' }));
    const off = await screen.findByRole('dialog', { name: 'Take off the list' });
    await user.type(within(off).getByLabelText('Why is it taken off the list?'), 'Found by the owner');
    await user.click(within(off).getByRole('button', { name: 'Take off the list' }));
    await waitFor(async () => expect((await api.devices({ q: '354670112345675' }))[0]!.status).toBe('in_stock'));
  });

  it('looks up an IMEI across the business: here and at head office', async () => {
    const user = userEvent.setup();
    render(<App api={await owner()} initialPath="/app/devices" lang="en" />);
    await user.type(await screen.findByLabelText(/Scan or type an IMEI or serial number/), `${CONFLICT}{Enter}`);
    const result = await screen.findByTestId('lookup-result');
    expect(within(result).getByText('In this branch')).toBeInTheDocument();
    expect(within(result).getByText('Registered at head office')).toBeInTheDocument();
    expect(within(result).getByText('In conflict')).toBeInTheDocument();
    expect(within(result).getByText('Open IMEI conflicts: 1')).toBeInTheDocument();
  });
});

describe('counting devices by IMEI (Phase 6)', () => {
  it('scans the shelf, says what each scan is, and finishes with the missing devices', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/devices" lang="en" />);
    await user.click(await screen.findByRole('tab', { name: 'Count devices' }));
    await user.click(await screen.findByRole('button', { name: 'Start a count' }));
    const scan = await screen.findByLabelText('Scan each device');
    await user.type(scan, '352099001761481{Enter}');
    expect(await screen.findByTestId('count-last')).toHaveTextContent('Counted');
    await user.type(scan, '354670112345675{Enter}');
    await waitFor(() => expect(screen.getAllByText('Counted', { selector: '.badge' }).length).toBe(2));
    await user.type(scan, '354670112345741{Enter}');
    await waitFor(() => expect(screen.getByTestId('count-last')).toHaveTextContent('Not a device of this business'));
    await user.type(scan, '352099001761481{Enter}');
    expect(await screen.findByText(/already scanned in this count/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Finish the count' }));
    const done = await screen.findByRole('dialog', { name: 'Finish the count' });
    await user.click(within(done).getByRole('button', { name: 'Finish the count' }));
    expect(await screen.findByText(/Count DC-.* finished: 2 of \d+ found\./)).toBeInTheDocument();
  });
});

describe('settings (Phase 6)', () => {
  it('imports the model table, which then hints the model while receiving, and saves the strict option', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/settings/devices" lang="en" />);
    const strict = await screen.findByRole('checkbox', { name: /Keep devices off sale until head office confirms the IMEI/ });
    await user.click(strict);
    await user.click(screen.getAllByRole('button', { name: 'Save' })[0]!);
    await waitFor(async () => expect((await api.deviceSettings()).strictCentralCheck).toBe(true));

    const file = new File(['35467011,Samsung,Galaxy A55\nbad line\n35209900,Apple,iPhone 13\n'], 'tac.csv', { type: 'text/csv' });
    await user.upload(screen.getByLabelText('CSV file'), file);
    await user.click(screen.getByRole('button', { name: 'Import the file' }));
    expect(await screen.findByText('2 models imported, 1 lines skipped.')).toBeInTheDocument();
    const [check] = await api.checkIdentifiers({ codes: ['352099001761515'], variantId: (await api.devices({ q: '354670112345709' }))[0]!.variantId });
    expect(check).toMatchObject({ hintMismatch: true, hint: expect.stringContaining('Apple iPhone 13') });
  });
});

describe('the till with head office checks (Phase 6)', () => {
  it('refuses a phone of another model scanned in the picker', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ configured: true });
    await api.login('owner', OWNER_PW);
    const t01 = (await api.terminals()).find((x) => x.code === 'T01')!;
    await api.logout();
    localStorage.setItem('kite.terminal', JSON.stringify({ terminalId: t01.id, deviceKey: 'k'.repeat(43), code: 'T01', branchId: t01.branchId, printerId: null }));
    render(<App api={api} initialPath="/pos" lang="en" />);
    await user.type(await screen.findByLabelText('Username'), 'cashier');
    await user.type(screen.getByLabelText('PIN'), '2468');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await user.type(await screen.findByLabelText('Pieces of 20'), '1');
    await user.click(screen.getByRole('button', { name: 'Open shift' }));
    await screen.findByLabelText('Scan a barcode or type a name');

    await user.click(await screen.findByRole('button', { name: /Galaxy A55 128GB/ }));
    const picker = await screen.findByRole('dialog', { name: /Choose the device/ });
    await user.type(within(picker).getByLabelText('Scan the device’s IMEI'), '352099001761481{Enter}');   // an iPhone
    expect(await within(picker).findByText(/is a iPhone 13 128GB, not the Galaxy A55 128GB you chose/)).toBeInTheDocument();
  });
});

describe('emergency mode with phones (Phase 6)', () => {
  const unit = (unitId: string, imei1: string, variantId = 'v-a55') => ({
    unitId, variantId, sku: 'A55-128', productType: 'device', tracking: 'imei', descriptionEn: 'Galaxy A55 128GB', descriptionAr: 'جالاكسي A55', taxRate: 5, listPrice: 139, minPrice: 120,
    condition: 'brand_new' as const, gradeCode: null, imei1, imei2: null, serial: null, warrantyDays: 365, reservedUntil: null,
  });
  const snapshot: Snapshot = {
    generatedAt: '2026-11-16T05:00:00Z', businessDate: '2026-11-16', numberPrefix: 'EM-B01-T05-',
    policy: { id: 'p', branchId: null, negativeStock: 'block', returnWindowDays: 14, cashRefundCap: 100, varianceThreshold: 1, blindClose: true, roundingLevel: 'line', cashRounding: 0.005,
      denominations: [50, 20, 10, 5, 1, 0.5, 0.1, 0.05, 0.025, 0.01, 0.005], receiptFooterEn: 'Thank you', receiptFooterAr: 'شكراً', version: 1 },
    items: [{ variantId: 'v-a55', sku: 'A55-128', productType: 'device', tracking: 'imei', descriptionEn: 'Galaxy A55 128GB', descriptionAr: 'جالاكسي A55', taxCodeId: 'tax-std',
      taxKind: 'standard', taxRate: 5, listPrice: 145, minPrice: null, warrantyDays: 365, active: true, stock: 2, barcode: null },
    { variantId: 'v-ip13', sku: 'IP13-128', productType: 'device', tracking: 'imei', descriptionEn: 'iPhone 13 128GB', descriptionAr: 'آيفون 13', taxCodeId: 'tax-std',
      taxKind: 'standard', taxRate: 5, listPrice: 220, minPrice: null, warrantyDays: 365, active: true, stock: 0, barcode: null }],
    barcodes: [],
    users: [{ id: 'u-cash', username: 'cashier', displayNameEn: 'Ahmed', displayNameAr: 'أحمد', permissions: ['POS.SELL', 'POS.SHIFT.OPEN'], discountLimitPercent: 5, discountLimitAmount: 2 }],
    business: { legalNameEn: 'Al Noor Mobiles LLC', legalNameAr: 'شركة النور للهواتف', tradeNameEn: null, tradeNameAr: null, vatNumber: 'OM1100223344', crNumber: null, pricesIncludeTax: true, addressEn: null, addressAr: null, phone: null },
    terminal: { id: 't5', code: 'T05', branchId: 'b1', branchCode: 'B01', branchNameEn: 'Muscat - Ruwi', branchNameAr: 'مسقط - روي' },
    units: [unit('u1', '354670112345733'), unit('u2', '354670112345741')],
  };

  it('sells a phone from the snapshot at its own price, keeps its unit on the record, and will not sell it twice', async () => {
    const records: { seq: number; id: string; kind: string; recordedAt: string; payload: Record<string, unknown> }[] = [];
    let shift: unknown = null; let sale = 0;
    const bridge = {
      snapshot: async () => snapshot,
      status: async () => ({ shift }),
      append: async (kind: string, payload: Record<string, unknown>) => {
        const body = { ...payload };
        if (kind === 'sale') body.number = `EM-B01-T05-${String(++sale).padStart(6, '0')}`;
        if (kind === 'shift_open') { body.number = 'EMS-B01-T05-000001'; shift = body; }
        const r = { seq: records.length + 1, id: crypto.randomUUID(), kind, recordedAt: new Date().toISOString(), payload: body };
        records.push(r); return r;
      },
      records: async () => [...records].reverse(),
      verifyPin: async (u: string, p: string) => (u === 'cashier' && p === '2468' ? snapshot.users[0]! : null),
      end: async () => ({ ok: true }),
    };
    const api = createEmergencyApi(bridge);
    await api.pinLoginAt('cashier', '2468', { terminalId: 't5', deviceKey: '' });
    await api.openShift({ openingFloat: 10 });

    expect((await api.posUnits('v-a55')).map((u) => u.imei1)).toEqual(['354670112345733', '354670112345741']);
    await expect(api.posUnitByCode('354670112345733', 'v-ip13')).rejects.toMatchObject({ code: 'DEV-016' });
    const u = await api.posUnitByCode('354670112345733', 'v-a55');
    const q = await api.quote({ lines: [{ variantId: 'v-a55', qty: 1, deviceUnitId: u.unitId }] });
    expect(q.totals.gross).toBe(139);   // the phone's own price, not the item's 145
    await expect(api.quote({ lines: [{ variantId: 'v-a55', qty: 1 }] })).rejects.toMatchObject({ code: 'DEV-002' });

    const done = await api.completeSale('s1', { lines: [{ variantId: 'v-a55', qty: 1, deviceUnitId: u.unitId }], payments: [{ method: 'cash', amount: 139 }], docType: 'simplified_invoice' });
    expect(done.number).toBe('EM-B01-T05-000001');
    expect(records.at(-1)!.payload.lines).toEqual([expect.objectContaining({ deviceUnitId: 'u1', identifier: '354670112345733', gross: 139, warrantyDays: 365 })]);
    expect((await api.receipt('s1')).lines[0]).toMatchObject({ serial: '354670112345733' });

    expect((await api.posUnits('v-a55')).map((x) => x.imei1)).toEqual(['354670112345741']);
    await expect(api.posUnitByCode('354670112345733')).rejects.toMatchObject({ code: 'DEV-004' });
    await expect(api.completeSale('s2', { lines: [{ variantId: 'v-a55', qty: 1, deviceUnitId: 'u1' }], payments: [{ method: 'cash', amount: 139 }], docType: 'simplified_invoice' }))
      .rejects.toMatchObject({ code: 'DEV-004' });
  });
});
