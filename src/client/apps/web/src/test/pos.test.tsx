import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { createMockApi } from '../mock';

const OWNER_PW = 'Muscat-Ruwi-2026!';

beforeEach(() => { window.print = () => undefined; });   // browser printing (no printer set for the till)

/** Registers mock terminal T01 as this browser's till, as the device setup screen does. */
async function tillApi() {
  const api = createMockApi({ configured: true });
  await api.login('owner', OWNER_PW);
  const t01 = (await api.terminals()).find((x) => x.code === 'T01')!;
  await api.logout();
  localStorage.setItem('kite.terminal', JSON.stringify({ terminalId: t01.id, deviceKey: 'k'.repeat(43), code: 'T01', branchId: t01.branchId, printerId: null }));
  return api;
}

async function pinSignIn(user: ReturnType<typeof userEvent.setup>, username: string, pin: string) {
  await user.type(await screen.findByLabelText('Username'), username);
  await user.type(screen.getByLabelText('PIN'), pin);
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
}

async function openShift(user: ReturnType<typeof userEvent.setup>) {
  expect(await screen.findByRole('heading', { name: 'Open shift' })).toBeInTheDocument();
  await user.type(screen.getByLabelText('Pieces of 20'), '1');
  await user.type(screen.getByLabelText('Pieces of 5'), '2');
  expect(screen.getByText('30.000')).toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: 'Open shift' }));
}

describe('catalog', () => {
  it('creates a product with a price and barcode and finds it by barcode', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ configured: true });
    await api.login('owner', OWNER_PW);
    render(<App api={api} initialPath="/app/catalog" lang="en" />);
    await user.click(await screen.findByRole('button', { name: 'New product' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText(/Name \(English\)/), 'Car holder');
    await user.type(within(dialog).getByLabelText(/Name \(Arabic\)/), 'حامل سيارة');
    await user.type(within(dialog).getByLabelText(/^SKU/), 'hold-car');
    await user.type(within(dialog).getByLabelText(/^Barcode/), '6291000000999');
    await user.type(within(dialog).getByLabelText(/^Price/), '3.750');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('button', { name: 'Car holder' }, { timeout: 4000 })).toBeInTheDocument();
    expect(screen.getByText('HOLD-CAR')).toBeInTheDocument();
    expect(screen.getByText('3.750')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Search'), '6291000000999');
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'USB-C cable 1m' })).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Car holder' })).toBeInTheDocument();
    });
  });
});

describe('touch POS', () => {
  it('signs in with a PIN, opens a shift, scans, takes cash with rounding and shows the receipt', async () => {
    const user = userEvent.setup();
    render(<App api={await tillApi()} initialPath="/pos" lang="en" />);
    await pinSignIn(user, 'cashier', '2468');
    await openShift(user);

    const scan = await screen.findByLabelText('Scan a barcode or type a name');
    await user.type(scan, '6291000000011{Enter}');          // USB-C cable, 2.500
    await user.type(scan, '6291000000011{Enter}');          // the same item again: quantity 2
    await user.click(screen.getByRole('button', { name: /Charger 20W/ }));
    const cart = screen.getByRole('region', { name: 'Cart' });
    expect(await within(cart).findByDisplayValue('2')).toBeInTheDocument();
    expect(within(cart).getByText('11.750')).toBeInTheDocument();   // 5.000 + 6.750

    await user.click(screen.getByRole('button', { name: /^Pay 11\.750/ }));
    const pay = await screen.findByRole('dialog', { name: 'Payment' });
    await user.type(within(pay).getByLabelText('Cash given'), '20');
    expect(within(pay).getByText('8.250')).toBeInTheDocument();
    await user.click(within(pay).getByRole('button', { name: 'Complete sale' }));

    const receipt = await screen.findByRole('dialog', { name: /^INV-B01-/ });
    expect(within(receipt).getByLabelText(/^Receipt INV-B01-/)).toHaveTextContent('Simplified tax invoice');
    expect(within(receipt).getByLabelText(/^Receipt INV-B01-/)).toHaveTextContent('فاتورة ضريبية مبسطة');
    expect(within(receipt).getByLabelText(/^Receipt INV-B01-/)).toHaveTextContent('8.250');
    await user.click(within(receipt).getByRole('button', { name: 'New sale' }));
    expect(await screen.findByText('Scan or tap an item to start a sale.')).toBeInTheDocument();
  });

  it('asks a manager to approve a discount above the cashier limit with a PIN', async () => {
    const user = userEvent.setup();
    render(<App api={await tillApi()} initialPath="/pos" lang="en" />);
    await pinSignIn(user, 'cashier', '2468');
    await openShift(user);
    await user.click(await screen.findByRole('button', { name: /Battery \(spare part\)/ }));   // 12.000
    const cart = screen.getByRole('region', { name: 'Cart' });
    await user.type(within(cart).getByLabelText('Discount'), '2.000');                        // above 5% of 12.000
    expect(within(cart).getByText('Needs a manager')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /^Pay 10\.000/ }));
    const pay = await screen.findByRole('dialog', { name: 'Payment' });
    await user.click(within(pay).getByRole('button', { name: 'Complete sale' }));

    const approval = await screen.findByRole('dialog', { name: 'Manager approval' });
    expect(within(approval).getByText('Discount above the limit')).toBeInTheDocument();
    await user.type(within(approval).getByLabelText('Manager username'), 'manager');
    await user.type(within(approval).getByLabelText('PIN'), '0000');
    await user.click(within(approval).getByRole('button', { name: 'Approve' }));
    expect(await within(approval).findByRole('alert')).toHaveTextContent('POS-008');
    await user.clear(within(approval).getByLabelText('PIN'));
    await user.type(within(approval).getByLabelText('PIN'), '1357');
    await user.click(within(approval).getByRole('button', { name: 'Approve' }));
    expect(await screen.findByRole('dialog', { name: /^INV-B01-/ })).toBeInTheDocument();
  });

  it('parks a cart and recalls it, and closes the shift blind with a manager accepting the difference', async () => {
    const user = userEvent.setup();
    render(<App api={await tillApi()} initialPath="/pos" lang="en" />);
    await pinSignIn(user, 'cashier', '2468');
    await openShift(user);
    await user.click(await screen.findByRole('button', { name: /Phone case/ }));
    await user.click(screen.getByRole('button', { name: 'Park' }));
    expect(await screen.findByText('Cart parked.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Parked' }));
    const parked = await screen.findByRole('dialog', { name: 'Parked carts' });
    await user.click(await within(parked).findByRole('button', { name: /No name/ }));
    expect(await screen.findByRole('button', { name: /^Pay 3\.250/ })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /^Pay 3\.250/ }));
    await user.click(within(await screen.findByRole('dialog', { name: 'Payment' })).getByRole('button', { name: 'Complete sale' }));
    await user.click(within(await screen.findByRole('dialog', { name: /^INV-B01-/ })).getByRole('button', { name: 'New sale' }));

    // Expected: 30.000 float + 3.250 cash = 33.250. Count 30.000: 3.250 short, above the 1.000 allowed.
    await user.click(screen.getByRole('button', { name: 'Close shift' }));
    const close = await screen.findByRole('dialog', { name: 'Close shift' });
    expect(within(close).queryByText('33.250')).not.toBeInTheDocument();     // blind
    await user.type(within(close).getByLabelText('Pieces of 20'), '1');
    await user.type(within(close).getByLabelText('Pieces of 10'), '1');
    await user.click(within(close).getByRole('button', { name: 'Close shift' }));
    expect(await within(close).findByRole('alert')).toHaveTextContent('POS-014');
    await user.type(within(close).getByLabelText(/The count does not match/), 'Gave wrong change');
    await user.click(within(close).getByRole('button', { name: 'Close shift' }));
    const approval = await screen.findByRole('dialog', { name: 'Manager approval' });
    await user.type(within(approval).getByLabelText('Manager username'), 'manager');
    await user.type(within(approval).getByLabelText('PIN'), '1357');
    await user.click(within(approval).getByRole('button', { name: 'Approve' }));
    expect(await screen.findByRole('heading', { name: /^Shift SH-B01-.* closed$/ })).toBeInTheDocument();
    expect(screen.getByText('-3.250')).toBeInTheDocument();
  });

  it('runs in Arabic, right to left', async () => {
    const user = userEvent.setup();
    render(<App api={await tillApi()} initialPath="/pos" lang="ar" />);
    await user.type(await screen.findByLabelText('اسم المستخدم'), 'cashier');
    await user.type(screen.getByLabelText('الرمز السري'), '2468');
    await user.click(screen.getByRole('button', { name: 'دخول' }));
    expect(await screen.findByRole('heading', { name: 'فتح وردية' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'فتح وردية' }));
    expect(await screen.findByLabelText('امسح الباركود أو اكتب الاسم')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /كابل USB-C متر/ })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe('rtl');
  });
});
