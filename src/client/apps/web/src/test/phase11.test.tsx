import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { KiteApi } from '@kite/api';
import { App } from '../App';
import { createMockApi } from '../mock';

const OWNER_PW = 'Muscat-Ruwi-2026!';
beforeEach(() => { URL.createObjectURL = () => 'blob:x'; URL.revokeObjectURL = () => undefined; });

async function owner() { const api = createMockApi({ configured: true }); await api.login('owner', OWNER_PW); return api; }
const gulf = async (api: KiteApi) => (await api.suppliers({})).find((s) => s.nameEn.startsWith('Gulf Mobile'))!;
const item = async (api: KiteApi, q: string) => (await api.posItems(q))[0]!;
const ho = async (api: KiteApi) => (await api.me()).branches[0]!.id;

describe('purchasing (Phase 11)', () => {
  it('raises an order with the last price, approves it, receives part of it and closes the rest', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/purchasing" lang="en" />);
    expect(await screen.findByRole('heading', { name: 'Purchasing' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'New order' }));
    const dlg = await screen.findByRole('dialog', { name: 'New order' });
    await within(dlg).findByRole('option', { name: /Gulf Mobile Parts/ });
    await user.selectOptions(within(dlg).getByLabelText('Supplier'), (await gulf(api)).id);
    await user.type(within(dlg).getByLabelText('Add an item'), 'Charger');
    await user.click(await within(dlg).findByRole('button', { name: /Charger 20W/ }));
    const qty = await within(dlg).findByLabelText('Quantity');
    await user.clear(qty); await user.type(qty, '10');
    await user.type(within(dlg).getByLabelText('Price before VAT'), '3.2');
    expect(within(dlg).getByText('33.600')).toBeInTheDocument();   // 32.000 + 5% VAT
    await user.click(within(dlg).getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByRole('heading', { name: /Order PO-B01-/ })).toBeInTheDocument();
    expect(screen.getByText('Draft')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Approve' }));
    expect(await screen.findByText('Approved')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Receive goods' }));
    const rcv = await screen.findByRole('dialog', { name: /Receive against PO-B01-/ });
    expect(within(rcv).getByText(/10 still to come/)).toBeInTheDocument();
    await user.type(within(rcv).getByLabelText('Quantity'), '12');
    expect(within(rcv).getByText('More than is still to come on the order.')).toBeInTheDocument();
    expect(within(rcv).getByRole('button', { name: 'Post' })).toBeDisabled();
    await user.clear(within(rcv).getByLabelText('Quantity')); await user.type(within(rcv).getByLabelText('Quantity'), '6');
    await user.click(within(rcv).getByRole('button', { name: 'Post' }));
    expect(await screen.findByText(/Goods received: GR-B01-/)).toBeInTheDocument();
    expect(await screen.findByText('Partly received')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Close the order' }));
    const close = await screen.findByRole('dialog', { name: 'Close the order' });
    await user.type(within(close).getByLabelText('Reason'), 'Supplier out of stock');
    await user.click(within(close).getByRole('button', { name: 'Confirm' }));
    expect(await screen.findByText('Closed: Supplier out of stock')).toBeInTheDocument();
  });

  it('matches an invoice three ways: within tolerance it posts, outside it needs an override with a reason', async () => {
    const user = userEvent.setup();
    const api = await owner();
    const s = await gulf(api);
    const cable = await item(api, 'USB-C');
    const guard = await item(api, 'Screen guard');
    const b1 = await ho(api);
    await api.receive({ id: crypto.randomUUID(), branchId: b1, supplierId: s.id, lines: [{ qty: 10, variantId: cable.variantId, unitPrice: 1 }, { qty: 5, variantId: guard.variantId, unitPrice: 2 }] });
    render(<App api={api} initialPath="/app/purchasing/invoices/new" lang="en" />);
    expect(await screen.findByRole('heading', { name: 'Enter an invoice' })).toBeInTheDocument();
    await screen.findByRole('option', { name: /Gulf Mobile Parts/ });
    await user.selectOptions(screen.getByLabelText('Supplier'), s.id);
    await user.type(screen.getByLabelText('Supplier’s invoice no.'), 'GMP-7781');
    await user.click(await screen.findByRole('checkbox', { name: /USB-C cable/ }));
    await user.click(screen.getByRole('checkbox', { name: /Screen guard/ }));
    const prices = screen.getAllByLabelText(/^Invoice price GR-B01-/);
    expect(prices[0]).toHaveValue('1');
    await user.clear(prices[1]!); await user.type(prices[1]!, '2.3');   // 15% over the receipt's price
    expect(await screen.findByText('Price differs')).toBeInTheDocument();
    expect(screen.getByText(/Some prices differ from the order/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Check the match and save' }));
    expect(await screen.findByRole('heading', { name: /Supplier invoice PI-B01-/ })).toBeInTheDocument();
    expect(screen.getByText('Outside tolerance')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Post outside tolerance' }));
    const dlg = await screen.findByRole('dialog', { name: 'Post outside tolerance' });
    await user.type(within(dlg).getByLabelText('Reason'), 'New price list from March');
    await user.click(within(dlg).getByRole('button', { name: 'Post' }));
    expect(await screen.findByText('Posted outside tolerance by Salim Al Harthy: New price list from March')).toBeInTheDocument();
    expect(screen.getByText('Posted')).toBeInTheDocument();
    // 10 + 11.5 before VAT, 1.075 VAT; the guards' 1.500 difference all to the stock still on hand.
    expect(screen.getByText('22.575')).toBeInTheDocument();
    expect(screen.getAllByText('1.500').length).toBeGreaterThanOrEqual(2);   // the line's difference to stock and the invoice's difference
    // The same number again is refused.
    const again = await api.enterInvoice({ id: crypto.randomUUID(), branchId: b1, supplierId: s.id, supplierInvoiceNo: ' gmp-7781 ', invoiceDate: '2026-11-17', lines: [] }).catch((e: unknown) => e);
    expect(again).toMatchObject({ code: 'PUR-008' });
  });

  it('pays a supplier choosing the invoices, the rest staying as an advance', async () => {
    const user = userEvent.setup();
    const api = await owner();
    const s = await gulf(api);
    render(<App api={api} initialPath={`/app/purchasing?tab=payments&supplier=${s.id}`} lang="en" />);
    await user.click(await screen.findByRole('button', { name: 'Pay a supplier' }));
    const dlg = await screen.findByRole('dialog', { name: 'Pay a supplier' });
    await within(dlg).findByRole('option', { name: /Gulf Mobile Parts/ });
    await user.type(within(dlg).getByLabelText('Amount'), '700');
    expect(within(dlg).getByRole('button', { name: 'Pay' })).toBeDisabled();   // a transfer needs its reference
    await user.type(within(dlg).getByLabelText('Reference'), 'TRF-2201');
    await user.click(within(dlg).getByRole('checkbox', { name: 'Choose the invoices it pays' }));
    await user.type(await within(dlg).findByLabelText(/^Pay now OB-/), '640');
    expect(within(dlg).getByText('60.000 stays with the supplier as an advance.')).toBeInTheDocument();
    await user.click(within(dlg).getByRole('button', { name: 'Pay' }));
    expect(await screen.findByText(/Payment PV-B01-.* recorded/)).toBeInTheDocument();
    expect((await api.supplierOpenItems(s.id)).length).toBe(0);
  });

  it('shows purchasing in Arabic, right to left', async () => {
    const api = await owner();
    render(<App api={api} initialPath="/app/purchasing" lang="ar" />);
    expect(await screen.findByRole('heading', { name: 'المشتريات' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'فواتير الموردين' })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe('rtl');
  });
});

describe('accounting (Phase 11)', () => {
  it('posts a manual journal within the threshold and leaves one above it for a second person', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/accounting?tab=manual" lang="en" />);
    await user.click(await screen.findByRole('button', { name: 'New manual journal' }));
    const dlg = await screen.findByRole('dialog', { name: 'New manual journal' });
    await user.type(within(dlg).getByLabelText('Description'), 'Rent for November');
    const accounts = within(dlg).getAllByLabelText('Account');
    const chart = await api.chart();
    await user.selectOptions(accounts[0]!, chart.find((a) => a.code === '6100')!.id);
    await user.selectOptions(accounts[1]!, chart.find((a) => a.code === '1200')!.id);
    await user.type(within(dlg).getAllByLabelText('Debit')[0]!, '1500');
    expect(within(dlg).getByText('Off by 1,500.000')).toBeInTheDocument();
    await user.type(within(dlg).getAllByLabelText('Credit')[1]!, '1500');
    await user.click(within(dlg).getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByRole('heading', { name: /Manual journal MJ-B01-/ })).toBeInTheDocument();
    expect(screen.getByText(/once submitted it waits for someone else/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Submit' }));
    expect(await screen.findByText('Waiting for someone else to approve it.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
  });

  it('files a quarter’s VAT return once its months are closed; a late document moves to the next open period', async () => {
    const user = userEvent.setup();
    const api = await owner();
    const b1 = await ho(api);
    const general = (await api.chart()).find((a) => a.code === '6900')!;
    const taxes = await api.taxCodes();
    const year = new Date().getFullYear();
    await api.recordExpense({ id: crypto.randomUUID(), branchId: b1, accountId: general.id, description: 'Cleaning', netAmount: 40, taxCodeId: taxes[0]!.id, paidBy: 'cash', expenseDate: `${year}-02-10` });
    const v = await api.prepareVatReturn(`${year}-01-01`, `${year}-03-31`);
    render(<App api={api} initialPath={`/app/accounting/vat/${v.vatReturn.id}`} lang="en" />);
    expect(await screen.findByRole('heading', { name: /VAT return VAT-/ })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'File the return' }));
    const dlg = await screen.findByRole('dialog', { name: 'File the return' });
    await user.click(within(dlg).getByRole('button', { name: 'Confirm' }));
    expect(await within(dlg).findByText(/confirms the VAT return boxes|before the first return is filed/)).toBeInTheDocument();
    await api.reviewVatBoxes();
    await user.click(within(dlg).getByRole('button', { name: 'Confirm' }));
    expect(await within(dlg).findByText(/still open at B01/)).toBeInTheDocument();
    const periods = await api.periods();
    for (const p of periods.periods.slice(0, 3)) await api.setPeriodStatus(p.id, 'soft_closed', 'Quarter closed');
    await user.click(within(dlg).getByRole('button', { name: 'Confirm' }));
    expect(await screen.findByText('Filed')).toBeInTheDocument();
    // A late expense for February now posts in April.
    await api.recordExpense({ id: crypto.randomUUID(), branchId: b1, accountId: general.id, description: 'Late bill', netAmount: 10, taxCodeId: taxes[0]!.id, paidBy: 'cash', expenseDate: `${year}-02-20` });
    expect((await api.journals({ q: 'Late bill' }))[0]!.journalDate).toBe(`${year}-04-01`);
  });

  it('shows the trial balance and balance sheet balanced, and the ledger check agreeing', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/accounting/statements" lang="en" />);
    expect(await screen.findByRole('heading', { name: 'Statements' })).toBeInTheDocument();
    expect(await screen.findByText('Balanced')).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Balance sheet' }));
    await waitFor(() => expect(screen.getByText('Total assets')).toBeInTheDocument());
    expect(screen.getByText('Balanced')).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Profit and loss' }));
    expect(await screen.findByText('Net profit')).toBeInTheDocument();
    render(<App api={api} initialPath="/app/accounting?tab=check" lang="en" />);
    expect(await screen.findByText('Everything agrees')).toBeInTheDocument();
  });

  it('records an expense with its VAT', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/expenses" lang="en" />);
    await user.click(await screen.findByRole('button', { name: 'Record an expense' }));
    const dlg = await screen.findByRole('dialog', { name: 'Record an expense' });
    await user.selectOptions(within(dlg).getByLabelText('Account'), (await api.expenseAccounts()).find((a) => a.code === '6100')!.id);
    await user.type(within(dlg).getByLabelText('Description'), 'Shop rent');
    await user.type(within(dlg).getByLabelText('Amount before VAT'), '200');
    expect(within(dlg).getByText('210.000')).toBeInTheDocument();
    await user.click(within(dlg).getByRole('button', { name: 'Record' }));
    expect(await screen.findByText(/Expense EX-B01-.* recorded/)).toBeInTheDocument();
    expect(await screen.findByText('Shop rent')).toBeInTheDocument();
  });
});
