import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { createMockApi } from '../mock';

const OWNER_PW = 'Muscat-Ruwi-2026!';
const IMEI_A = '354670112341005';
const IMEI_B = '352899112341010';

beforeEach(() => { window.print = () => undefined; window.open = () => null; });

async function tillApi() {
  const api = createMockApi({ configured: true });
  await api.login('owner', OWNER_PW);
  const t01 = (await api.terminals()).find((x) => x.code === 'T01')!;
  await api.logout();
  localStorage.setItem('kite.terminal', JSON.stringify({ terminalId: t01.id, deviceKey: 'k'.repeat(43), code: 'T01', branchId: t01.branchId, printerId: null }));
  return api;
}

async function signInAndOpen(user: ReturnType<typeof userEvent.setup>, username: string, pin: string) {
  await user.type(await screen.findByLabelText('Username'), username);
  await user.type(screen.getByLabelText('PIN'), pin);
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  expect(await screen.findByRole('heading', { name: 'Open shift' })).toBeInTheDocument();
  await user.type(screen.getByLabelText('Pieces of 50'), '2');
  await user.click(screen.getByRole('button', { name: 'Open shift' }));
}

/** Customer, device and inspection steps of the wizard, up to the value. */
async function wizardToValue(user: ReturnType<typeof userEvent.setup>, imei: string) {
  const w = await screen.findByRole('dialog', { name: 'Trade-in' });
  await user.type(within(w).getByLabelText('Find the customer (name or phone)'), 'Maryam');
  await user.click(await within(w).findByRole('button', { name: /Maryam Al Hinai/ }));
  await user.type(within(w).getByLabelText('Civil ID'), '9104821');
  await user.click(within(w).getByRole('button', { name: 'Next' }));
  await user.type(await within(w).findByLabelText('Search the model (name or SKU)'), 'Galaxy');
  await user.click(await within(w).findByRole('button', { name: /Galaxy A55 128GB/ }));
  await user.type(within(w).getByLabelText(/^IMEI/), imei);
  await user.click(within(w).getByRole('button', { name: 'Check the device' }));
  const d = await screen.findByRole('dialog', { name: /^Trade-in TI-B01-/ });
  expect(within(d).getByText('Checked with head office and the blocked list.')).toBeInTheDocument();
  await user.click(within(d).getByRole('button', { name: 'Next' }));
  for (const b of await within(d).findAllByRole('button', { name: 'Pass' })) await user.click(b);
  await user.type(within(d).getByLabelText(/^%/), '92');
  expect(within(d).getByLabelText(/removed their cloud account/)).toBeChecked();   // the checklist's cloud-lock item ticks it
  await user.selectOptions(within(d).getByLabelText('Grade'), within(d).getByRole('option', { name: /^A · / }));
  return d;
}

describe('trade-ins at the till (Phase 7)', () => {
  it('takes a phone in with F6 and leaves the value as the customer’s store credit', async () => {
    const user = userEvent.setup();
    const api = await tillApi();
    render(<App api={api} initialPath="/pos" lang="en" />);
    await signInAndOpen(user, 'manager', '1357');
    await screen.findByLabelText('Scan a barcode or type a name');
    await user.keyboard('{F6}');
    const d = await wizardToValue(user, IMEI_A);
    await user.click(within(d).getByRole('button', { name: 'Next' }));
    expect(await within(d).findByText('Offer')).toBeInTheDocument();
    await user.type(within(d).getByLabelText('Agreed value'), '70');
    await user.click(within(d).getByRole('button', { name: 'Next' }));
    expect(await within(d).findByText(/I confirm that I own this device/)).toBeInTheDocument();
    await user.type(within(d).getByLabelText('Customer types their full name'), 'Maryam Al Hinai');
    await user.click(within(d).getByRole('button', { name: 'Next' }));
    await user.click(within(d).getByRole('radio', { name: /Store credit/ }));
    await user.click(within(d).getByRole('button', { name: 'Accept the device' }));
    expect(await within(d).findByText(/^Trade-in TI-B01-\d{4}-000001 accepted$/)).toBeInTheDocument();
    expect(within(d).getByText('70.000 added to the customer’s store credit.')).toBeInTheDocument();
    await user.click(within(d).getByRole('button', { name: 'Close' }));
    const maryam = (await api.customers('Maryam'))[0]!;
    expect((await api.storeCredit(maryam.id)).balance).toBe(70);
    const unit = await api.deviceLookup(IMEI_A);
    expect((await api.device(unit.id)).unit.status).toBe('under_inspection');
  });

  it('uses the value as payment for the cart, then settles what is left as store credit with a manager’s PIN', async () => {
    const user = userEvent.setup();
    const api = await tillApi();
    await api.login('owner', OWNER_PW);
    const galaxy = (await api.posItems('Galaxy'))[0]!;
    const gradeA = (await api.grades()).find((g) => g.code === 'A')!;
    await api.savePriceGuide([{ variantId: galaxy.variantId, gradeId: gradeA.id, baseValue: 40 }]);
    await api.logout();
    render(<App api={api} initialPath="/pos" lang="en" />);
    await signInAndOpen(user, 'cashier', '2468');
    const scan = await screen.findByLabelText('Scan a barcode or type a name');
    await user.type(scan, '6291000000011{Enter}');            // USB-C cable, 2.500
    await user.click(screen.getByRole('button', { name: 'Trade-in (F6)' }));
    const d = await wizardToValue(user, IMEI_B);
    await user.click(within(d).getByRole('button', { name: 'Next' }));
    expect(await within(d).findByDisplayValue('40')).toBeInTheDocument();   // the guide offer, within the cashier's limit
    await user.click(within(d).getByRole('button', { name: 'Next' }));
    await user.selectOptions(await within(d).findByLabelText('How does the customer confirm?'), 'paper');
    await user.type(within(d).getByLabelText('Paper form number'), 'TI-FORM-7');
    await user.click(within(d).getByRole('button', { name: 'Next' }));
    expect(within(d).getByRole('radio', { name: /Use it to pay for this sale/ })).toBeChecked();
    await user.click(within(d).getByRole('button', { name: 'Accept the device' }));
    expect(await within(d).findByText('The trade-in value is ready as a payment for this sale.')).toBeInTheDocument();
    await user.click(within(d).getByRole('button', { name: 'Close' }));
    expect(screen.getByRole('button', { name: 'Maryam Al Hinai' })).toBeInTheDocument();   // the cart is now the seller's

    await user.click(screen.getByRole('button', { name: /^Pay 2\.500/ }));
    const pay = await screen.findByRole('dialog', { name: 'Payment' });
    expect(await within(pay).findByDisplayValue('2.500')).toBeInTheDocument();   // the trade-in pays the whole cart
    expect(within(pay).getByText('40.000 left on it')).toBeInTheDocument();
    await user.click(within(pay).getByRole('button', { name: 'Complete sale' }));
    const rest = await screen.findByRole('dialog', { name: 'Value left on the trade-in' });
    expect(within(rest).getByText(/has 37\.500 left after this sale/)).toBeInTheDocument();
    await user.click(within(rest).getByRole('button', { name: 'As store credit' }));
    const approval = await screen.findByRole('dialog', { name: 'Manager approval' });
    expect(within(approval).getByText('Leave a trade-in as store credit')).toBeInTheDocument();
    await user.type(within(approval).getByLabelText('Manager username'), 'manager');
    await user.type(within(approval).getByLabelText('PIN'), '1357');
    await user.click(within(approval).getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Value left on the trade-in' })).not.toBeInTheDocument());
    const receipt = await screen.findByRole('dialog', { name: /^INV-B01-/ });
    expect(within(receipt).getByText('Trade-in')).toBeInTheDocument();
    await api.login('owner', OWNER_PW);
    const maryam = (await api.customers('Maryam'))[0]!;
    expect((await api.storeCredit(maryam.id)).balance).toBe(37.5);
    const [ti] = await api.tradeIns();
    expect(ti!.status).toBe('settled');
  });

  it('records the margin scheme approval and the back office shows the trade-in with its settlements', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ configured: true });
    await api.login('owner', OWNER_PW);
    render(<App api={api} initialPath="/app/settings/tax" lang="en" />);
    expect(await screen.findByText('VAT-MARGIN is switched off.')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Approval reference'), 'TA/MS/2026/0142');
    await user.type(screen.getByLabelText('Approved on'), '2026-10-01');
    await user.click(screen.getByRole('button', { name: 'The Tax Authority approved the profit margin scheme' }));
    expect(await screen.findByText(/VAT-MARGIN is active\./)).toBeInTheDocument();
    expect((await api.taxCodes()).find((c) => c.code === 'VAT-MARGIN')!.status).toBe('active');

    await user.click(screen.getByRole('link', { name: 'Trade-ins' }));
    expect(await screen.findByText('No trade-ins.')).toBeInTheDocument();
    await user.click(screen.getByRole('link', { name: 'Trade-in settings' }));
    expect(await screen.findByRole('heading', { name: 'Trade-in settings' })).toBeInTheDocument();
    expect(await screen.findByText('Cracked or faulty screen')).toBeInTheDocument();
    await user.type(screen.getByLabelText('IP13-128 A'), '150');
    await user.click(screen.getByRole('button', { name: 'Save the guide' }));
    await waitFor(async () => expect((await api.priceGuide()).map((g) => g.baseValue)).toEqual([150]));
  });
});
