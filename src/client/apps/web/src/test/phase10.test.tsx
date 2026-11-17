import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { KiteApi } from '@kite/api';
import { App } from '../App';
import { createMockApi } from '../mock';

const OWNER_PW = 'Muscat-Ruwi-2026!';
beforeEach(() => { window.print = () => undefined; window.open = () => null; URL.createObjectURL = () => 'blob:x'; URL.revokeObjectURL = () => undefined; });

async function owner() { const api = createMockApi({ configured: true }); await api.login('owner', OWNER_PW); return api; }
const gulf = async (api: KiteApi) => (await api.customers('Gulf'))[0]!;
const maryam = async (api: KiteApi) => (await api.customers('Maryam'))[0]!;

describe('customer credit (Phase 10)', () => {
  it('lists accounts with what is owed and opens one for a customer with branch allowances', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/credit" lang="en" />);
    expect(await screen.findByRole('heading', { name: 'Customer credit' })).toBeInTheDocument();
    const row = (await screen.findByRole('link', { name: 'Gulf Trading LLC' })).closest('tr')!;
    expect(row).toHaveTextContent('500.000');   // limit
    expect(row).toHaveTextContent('250.000');   // 180 + 120 less a 50 payment
    expect(row).toHaveTextContent('Active');

    await user.click(screen.getByRole('button', { name: 'Open an account' }));
    const dlg = await screen.findByRole('dialog', { name: 'Open a credit account' });
    await user.type(within(dlg).getByLabelText('Find the customer'), 'Maryam');
    await user.click(await within(dlg).findByRole('button', { name: /Maryam Al Hinai/ }));
    await user.type(within(dlg).getByLabelText('Limit'), '200');
    await user.type(within(dlg).getByLabelText(/^B01/), '150');
    await user.type(within(dlg).getByLabelText(/^B02/), '80');
    expect(within(dlg).getByText('Allowances 230.000 of 200.000')).toHaveClass('neg');
    await user.click(within(dlg).getByRole('button', { name: 'Save' }));
    expect(await within(dlg).findByText(/Branch allowances add up to 230.000, more than the limit of 200.000/)).toBeInTheDocument();
    await user.clear(within(dlg).getByLabelText(/^B02/)); await user.type(within(dlg).getByLabelText(/^B02/), '50');
    await user.click(within(dlg).getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('link', { name: 'Maryam Al Hinai' })).toBeInTheDocument();
    expect((await api.creditAccount((await maryam(api)).id)).allowances.find((a) => a.code === 'B01')!.amount).toBe(150);
  });

  it('shows an account: open instalments, a transfer received at the office, a hold and its release, and the statement', async () => {
    const user = userEvent.setup();
    const api = await owner();
    const c = await gulf(api);
    render(<App api={api} initialPath={`/app/credit/${c.id}`} lang="en" />);
    expect(await screen.findByRole('heading', { name: 'Gulf Trading LLC' })).toBeInTheDocument();
    expect(screen.getByText('Open instalments')).toBeInTheDocument();
    const owed = screen.getAllByText('Owed')[0]!.closest('.fig')!;
    expect(owed).toHaveTextContent('250.000');

    await user.click(screen.getByRole('button', { name: 'Receive a payment' }));
    const pay = await screen.findByRole('dialog', { name: 'Payment from Gulf Trading LLC' });
    await user.type(within(pay).getByLabelText('Amount'), '130');
    await user.type(within(pay).getByLabelText('Transfer reference'), 'TRF-77');
    await user.click(within(pay).getByRole('button', { name: 'Receive a payment' }));
    expect(await screen.findByText(/^Payment RC\S+: 130.000. Owed now 120.000.$/)).toBeInTheDocument();
    // The oldest instalment (from B01, overdue) is settled first.
    expect((await api.creditAccount(c.id)).openItems.map((i) => i.open)).toEqual([120]);

    await user.click(screen.getByRole('button', { name: 'Put on hold' }));
    const hold = await screen.findByRole('dialog', { name: 'Put Gulf Trading LLC on hold' });
    await user.type(within(hold).getByLabelText('Reason'), 'Cheque returned');
    await user.click(within(hold).getByRole('button', { name: 'Put on hold' }));
    expect(await screen.findByText('On hold: Cheque returned')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Release the hold' }));
    const rel = await screen.findByRole('dialog', { name: 'Release Gulf Trading LLC' });
    await user.type(within(rel).getByLabelText('Reason'), 'Paid by transfer');
    await user.click(within(rel).getByRole('button', { name: 'Release the hold' }));
    await waitFor(() => expect(screen.queryByText('On hold: Cheque returned')).not.toBeInTheDocument());

    // The statement: opening + lines = closing; a PDF opens.
    const st = screen.getByText(/Statement: Gulf Trading LLC/).closest('section')!;
    await user.clear(within(st).getByLabelText('From'));
    await user.type(within(st).getByLabelText('From'), '2020-01-01');
    await waitFor(() => expect(within(st).getByText('Closing balance').closest('tr')!).toHaveTextContent('120.000'));
    await user.click(within(st).getByRole('button', { name: 'Download PDF' }));
  });

  it('sells on account at the till within the available credit, in instalments, and takes a credit payment on the drawer', async () => {
    const user = userEvent.setup();
    const api = await owner();
    const t01 = (await api.terminals()).find((x) => x.code === 'T01')!;
    const c = await gulf(api);
    await api.logout();
    localStorage.setItem('kite.terminal', JSON.stringify({ terminalId: t01.id, deviceKey: 'k'.repeat(43), code: 'T01', branchId: t01.branchId, printerId: null }));
    render(<App api={api} initialPath="/pos" lang="en" />);
    await user.type(await screen.findByLabelText('Username'), 'manager');
    await user.type(screen.getByLabelText('PIN'), '1357');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByRole('heading', { name: 'Open shift' });
    await user.type(screen.getByLabelText('Pieces of 50'), '2');
    await user.click(screen.getByRole('button', { name: 'Open shift' }));
    const scan = await screen.findByLabelText('Scan a barcode or type a name');

    await user.type(scan, '6291000000011{Enter}');      // USB-C cable, 2.500
    await user.click(screen.getByRole('button', { name: 'Customer' }));
    const cd = await screen.findByRole('dialog', { name: 'Customer' });
    await user.type(within(cd).getByLabelText('Search'), 'Gulf');
    await user.click(await within(cd).findByRole('button', { name: /Gulf Trading LLC/ }));
    await user.click(screen.getByRole('button', { name: /^Pay/ }));
    const pay = await screen.findByRole('dialog', { name: 'Payment' });
    expect(await within(pay).findByText(/Available 250.000/)).toBeInTheDocument();
    await user.click(within(pay).getByRole('button', { name: 'Use available' }));
    expect(within(pay).getByLabelText('On account')).toHaveValue('2.500');
    await user.selectOptions(within(pay).getByLabelText('Instalments'), '2');
    await user.click(within(pay).getByRole('button', { name: 'Complete sale' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Payment' })).not.toBeInTheDocument());
    // Two instalments of 1.250, 30 days apart; the till's view shows the new charge.
    const open = (await api.creditAccount(c.id)).openItems.filter((i) => i.amount === 1.25);
    expect(open.map((i) => `${i.seq}/${i.of}`)).toEqual(['1/2', '2/2']);
    expect((await api.creditAvailable(c.id)).exposure).toBe(252.5);

    await user.click(screen.getByRole('button', { name: 'Credit payment' }));
    const cp = await screen.findByRole('dialog', { name: 'Payment on a credit account' });
    await user.type(within(cp).getByLabelText('Find the customer'), 'Gulf');
    await user.click(await within(cp).findByRole('button', { name: /Gulf Trading LLC/ }));
    expect(await within(cp).findByText(/Owed 252.500/)).toBeInTheDocument();
    await user.type(within(cp).getByLabelText('Amount'), '52.5');
    await user.click(within(cp).getByRole('button', { name: 'Receive a payment' }));
    expect(await screen.findByText(/^Payment RC\S+ taken: 52.500. Owed 200.000$/)).toBeInTheDocument();
    const shift = await api.currentShift();
    expect(shift.figures).toMatchObject({ onAccountSales: 2.5, creditReceipts: 52.5 });
  });

  it('keeps the credit policy at head office and shows the ageing report in Arabic', async () => {
    const user = userEvent.setup();
    const api = await owner();
    const { unmount } = render(<App api={api} initialPath="/app/settings/credit" lang="en" />);
    const cap = await screen.findByLabelText('Offline cap per sale');
    expect(cap).toHaveValue('100');
    await user.clear(cap); await user.type(cap, '75');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    expect((await api.creditPolicy()).offlineSaleCap).toBe(75);
    unmount();

    render(<App api={api} initialPath="/app/credit" lang="ar" />);
    expect(await screen.findByRole('heading', { name: 'ائتمان العملاء' })).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'أعمار الديون' }));
    expect(await screen.findByRole('columnheader', { name: '١–٣٠ يومًا' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'الخليج للتجارة' }).closest('tr')!).toHaveTextContent('250.000');
  });
});

describe('suppliers (Phase 10)', () => {
  it('lists suppliers with the provisional one first, approves it, adds one, and merges a duplicate', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/suppliers" lang="en" />);
    expect(await screen.findByRole('heading', { name: 'Suppliers' })).toBeInTheDocument();
    const prov = (await screen.findByRole('link', { name: 'Sohar Screens Trading' })).closest('tr')!;
    expect(prov).toHaveTextContent('Waiting for head office');
    await user.click(within(prov).getByRole('button', { name: 'Approve' }));
    expect(await screen.findByText('SP-B02-2026-000001 approved.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Add a supplier' }));
    const add = await screen.findByRole('dialog', { name: 'New supplier' });
    await user.type(within(add).getByLabelText('Name (English)'), 'Gulf Mobile Parts');
    await user.type(within(add).getByLabelText('Name (Arabic)'), 'قطع الخليج');
    await user.click(within(add).getByRole('button', { name: 'Save' }));
    const dup = await screen.findByRole('link', { name: 'Gulf Mobile Parts' });
    await user.click(dup);
    expect(await screen.findByRole('heading', { name: 'Gulf Mobile Parts' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Merge into another' }));
    const merge = await screen.findByRole('dialog', { name: 'Merge Gulf Mobile Parts' });
    await user.selectOptions(within(merge).getByLabelText('Merge into'), await within(merge).findByRole('option', { name: /S-00001/ }));
    await user.click(within(merge).getByRole('button', { name: 'Merge into another' }));
    expect(await screen.findByText('Merged into S-00001.')).toBeInTheDocument();
    expect((await api.suppliers({ status: 'merged' })).map((s) => s.nameEn)).toEqual(['Gulf Mobile Parts']);
  });
});
