import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { createMockApi } from '../mock';

const OWNER_PW = 'Muscat-Ruwi-2026!';

beforeEach(() => { window.print = () => undefined; });

async function tillApi() {
  const api = createMockApi({ configured: true });
  await api.login('owner', OWNER_PW);
  const t01 = (await api.terminals()).find((x) => x.code === 'T01')!;
  await api.logout();
  localStorage.setItem('kite.terminal', JSON.stringify({ terminalId: t01.id, deviceKey: 'k'.repeat(43), code: 'T01', branchId: t01.branchId, printerId: null }));
  return api;
}

async function tillReady(user: ReturnType<typeof userEvent.setup>, username: string, pin: string) {
  await user.type(await screen.findByLabelText('Username'), username);
  await user.type(screen.getByLabelText('PIN'), pin);
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  expect(await screen.findByRole('heading', { name: 'Open shift' })).toBeInTheDocument();
  await user.type(screen.getByLabelText('Pieces of 20'), '1');
  await user.click(screen.getByRole('button', { name: 'Open shift' }));
  return screen.findByLabelText('Scan a barcode or type a name');
}

describe('phones at the till (Phase 5)', () => {
  it('sells a phone by scanning its IMEI, at its own price, and prints the IMEI on the receipt', async () => {
    const user = userEvent.setup();
    render(<App api={await tillApi()} initialPath="/pos" lang="en" />);
    const scan = await tillReady(user, 'cashier', '2468');

    await user.type(scan, '352099001761482{Enter}');   // one digit off: the check digit catches it before the server is asked
    expect(await screen.findByText(/not a valid IMEI/)).toBeInTheDocument();

    await user.type(scan, '352099001761481{Enter}');
    const cart = screen.getByRole('region', { name: 'Cart' });
    expect(await within(cart).findByText('352099001761481')).toBeInTheDocument();
    expect(within(cart).getByText('New')).toBeInTheDocument();
    expect(within(cart).getAllByText('220.000').length).toBeGreaterThan(0);   // the line and the total
    await user.type(scan, '352099001761481{Enter}');   // the same phone again: still one line
    await waitFor(() => expect(within(cart).getAllByText('352099001761481')).toHaveLength(1));

    await user.click(screen.getByRole('button', { name: /^Pay 220\.000/ }));
    const pay = await screen.findByRole('dialog', { name: 'Payment' });
    await user.type(within(pay).getByLabelText('Cash given'), '220');
    await user.click(within(pay).getByRole('button', { name: 'Complete sale' }));
    const receipt = await screen.findByRole('dialog', { name: /^INV-B01-/ });
    expect(within(receipt).getByText('IMEI 352099001761481')).toBeInTheDocument();
  });

  it('shows the units of a phone to pick from, and asks a manager before a damaged one goes in the cart', async () => {
    const user = userEvent.setup();
    render(<App api={await tillApi()} initialPath="/pos" lang="en" />);
    await tillReady(user, 'cashier', '2468');

    await user.click(await screen.findByRole('button', { name: /iPhone 13 128GB/ }));
    const picker = await screen.findByRole('dialog', { name: /Choose the device/ });
    expect(await within(picker).findByText('354670112345691')).toBeInTheDocument();      // the refurbished grade-A unit
    expect(within(picker).getByText('Refurbished · A')).toBeInTheDocument();
    expect(within(picker).getByText('187.000')).toBeInTheDocument();                       // 85% of 220.000
    await user.click(within(picker).getByText('354670112345691'));
    const cart = screen.getByRole('region', { name: 'Cart' });
    expect(await within(cart).findByText(/iPhone 13 128GB · Refurbished A/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Galaxy A55 128GB/ }));
    const p2 = await screen.findByRole('dialog', { name: /Choose the device/ });
    await user.click(await within(p2).findByText('354670112345709'));
    const approve = await screen.findByRole('dialog', { name: 'Manager approval' });
    expect(within(approve).getByText('Sell a damaged device')).toBeInTheDocument();
    await user.type(within(approve).getByLabelText('Manager username'), 'manager');
    await user.type(within(approve).getByLabelText('PIN'), '1357');
    await user.click(within(approve).getByRole('button', { name: 'Approve' }));
    expect(await within(cart).findByText('Damaged: tell the customer')).toBeInTheDocument();
  });
});

describe('devices in the back office (Phase 5)', () => {
  it('inspects a used phone, refurbishes it with labour and completes it as refurbished', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ configured: true });
    await api.login('owner', OWNER_PW);
    render(<App api={api} initialPath="/app/devices" lang="en" />);
    await user.click(await screen.findByRole('tab', { name: 'Inspections' }));
    await user.click(await screen.findByRole('link', { name: 'Inspect' }));

    expect(await screen.findByRole('heading', { name: /Inspect iPhone 13 128GB/ })).toBeInTheDocument();
    for (const group of screen.getAllByRole('group')) await user.click(within(group).getByRole('button', { name: 'Pass' }));
    await user.type(screen.getByLabelText(/^%/), '71');            // battery under 80%
    expect(await screen.findByText(/The checklist suggests: Refurbish/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save the inspection' }));

    expect(await screen.findByRole('heading', { name: /Refurbishment RF-B01-/ })).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Labour' }));
    await user.type(screen.getByLabelText('Minutes'), '30');
    expect(screen.getByText('Adds 1.500 to the device')).toBeInTheDocument();   // 30 minutes at 3.000 an hour
    await user.click(screen.getByRole('button', { name: 'Add' }));
    expect(await screen.findByText('96.500')).toBeInTheDocument();             // 95.000 + 1.500
    await user.click(screen.getByRole('button', { name: 'Complete' }));
    const done = await screen.findByRole('dialog', { name: 'Finish the refurbishment' });
    await user.selectOptions(within(done).getByLabelText('Grade'), within(done).getByRole('option', { name: /^B ·/ }));
    await user.click(within(done).getByRole('button', { name: 'Complete' }));
    expect(await screen.findByText('Completed')).toBeInTheDocument();
  });

  it('receives phones by scanning their IMEIs on opening stock', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ configured: true });
    await api.login('owner', OWNER_PW);
    render(<App api={api} initialPath="/app/inventory" lang="en" />);
    await user.click(await screen.findByRole('tab', { name: 'Adjust stock' }));
    await user.selectOptions(screen.getByLabelText('Document'), 'opening');
    await user.type(screen.getByLabelText('Add an item'), 'Galaxy');
    await user.click(await screen.findByRole('button', { name: /Galaxy A55 128GB/ }));
    await user.type(screen.getByLabelText('Unit cost'), '118.000');
    const scanner = screen.getByLabelText('Scan the IMEIs');
    await user.type(scanner, '354670112345717{Enter}');
    await user.type(scanner, '354670112345725{Enter}');
    await user.type(scanner, '354670112345725{Enter}');   // scanned twice
    expect(await screen.findByText(/Scanned twice/)).toBeInTheDocument();
    expect(screen.getByText('2 scanned')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Post' }));
    await waitFor(async () => expect((await api.devices({ q: '3546701123457' })).filter((d) => d.status === 'in_stock').length).toBeGreaterThanOrEqual(3));
  });
});
