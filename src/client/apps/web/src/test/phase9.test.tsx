import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { KiteApi } from '@kite/api';
import { App } from '../App';
import { createMockApi } from '../mock';

const OWNER_PW = 'Muscat-Ruwi-2026!';
const PNG = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==';

beforeAll(() => {
  HTMLCanvasElement.prototype.getContext = (() => ({ beginPath() {}, moveTo() {}, lineTo() {}, stroke() {}, clearRect() {}, lineWidth: 1, lineCap: 'round', strokeStyle: '#000' })) as unknown as HTMLCanvasElement['getContext'];
  HTMLCanvasElement.prototype.toDataURL = () => PNG;
});
beforeEach(() => { window.print = () => undefined; window.open = () => null; URL.createObjectURL = () => 'blob:x'; URL.revokeObjectURL = () => undefined; });

async function owner() { const api = createMockApi({ configured: true }); await api.login('owner', OWNER_PW); return api; }
const jobBy = async (api: KiteApi, model: string) => (await api.repairs({ q: '' })).find((r) => r.device.includes(model))!;
const batteryId = async (api: KiteApi) => (await api.products('BAT-SPARE'))[0]!.variants[0]!.variant.id;
const ownerId = async (api: KiteApi) => (await api.users()).find((u) => u.username === 'owner')!.id;
const managerId = async (api: KiteApi) => (await api.users()).find((u) => u.username === 'manager')!.id;

/** The iPhone in diagnosis: an estimate of 30 minutes' labour and some batteries, approved by phone and sent to the workshop. */
async function approved(api: KiteApi, batteries: number) {
  const job = await jobBy(api, 'iPhone 13');
  const d = await api.writeEstimate(job.id, [{ kind: 'labour', variantId: 'svc-labour', minutes: 30 }, { kind: 'part', variantId: await batteryId(api), qty: batteries, unitPrice: 12 }]);
  return api.approveEstimate(job.id, d.estimates[0]!.id, { method: 'phone', approverName: 'Maryam Al Hinai', phone: '+96899112233', then: 'in_progress' });
}

describe('parts on repair jobs (Phase 9)', () => {
  it('a job short of parts waits for them, is listed as needing parts, and moves on when they arrive', async () => {
    const user = userEvent.setup();
    const api = await owner();
    const d = await approved(api, 11);   // 10 in stock
    expect(d.job.status).toBe('waiting_parts');
    expect(d.parts![0]).toMatchObject({ needed: 11, reserved: 10, requested: 1 });

    render(<App api={api} initialPath="/app/repairs/parts" lang="en" />);
    expect(await screen.findByRole('heading', { name: 'Parts needed' })).toBeInTheDocument();
    const row = (await screen.findByRole('link', { name: d.job.number })).closest('tr')!;
    expect(row).toHaveTextContent('Battery (spare part)');
    expect(row).toHaveTextContent('Waiting for parts');
    await user.click(screen.getByRole('button', { name: 'Give out arrived stock now' }));
    expect(await screen.findByText('0 jobs got their parts and moved on.')).toBeInTheDocument();

    await api.postAdjustment({ branchId: (await api.repair(d.job.id)).job.branchId, kind: 'opening', note: 'arrived', lines: [{ variantId: await batteryId(api), qty: 1, unitCost: 6.5, reasonCode: 'opening' }] });
    await user.click(screen.getByRole('button', { name: 'Give out arrived stock now' }));
    expect(await screen.findByText('1 jobs got their parts and moved on.')).toBeInTheDocument();
    expect(await screen.findByText('No job is waiting for parts.')).toBeInTheDocument();
    expect((await api.repair(d.job.id)).job.status).toBe('in_progress');
  });

  it('shows the job’s parts, fits them, and records outside work that is reversed once', async () => {
    const user = userEvent.setup();
    const api = await owner();
    const d = await approved(api, 2);
    render(<App api={api} initialPath={`/app/repairs/${d.job.id}`} lang="en" />);
    await screen.findByRole('heading', { name: `Repair job ${d.job.number}` });
    await user.click(screen.getByRole('tab', { name: 'Parts and labour' }));
    expect(await screen.findByText('Parts for this job')).toBeInTheDocument();
    const row = screen.getByText('Battery (spare part)').closest('tr')!;
    // Needed 2, fitted 0, held in stock 2, waiting for 0.
    expect(within(row).getAllByRole('cell').slice(1, 5).map((c) => c.textContent)).toEqual(['2', '0', '2', '0']);

    await user.type(screen.getByLabelText('Done by'), 'Ruwi Board Lab');
    await user.type(screen.getByLabelText('Cost'), '7.250');
    await user.type(screen.getByLabelText('Their reference'), 'RBL-78');
    await user.click(screen.getByRole('button', { name: 'Add outside work' }));
    const line = (await screen.findByText('Ruwi Board Lab')).closest('tr')!;
    expect(line).toHaveTextContent('7.250');
    await user.click(within(line).getByRole('button', { name: 'Reverse' }));
    const dlg = await screen.findByRole('dialog', { name: 'Reverse outside work' });
    await user.type(within(dlg).getByLabelText('Why is it reversed?'), 'Entered on the wrong job');
    await user.click(within(dlg).getByRole('button', { name: 'Reverse' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    const after = await api.repair(d.job.id);
    expect(after.work.filter((w) => w.kind === 'external').map((w) => w.cost)).toEqual([7.25, -7.25]);
    expect(after.money.wip).toBe(0);
  });

  it('shares a job’s commission only in shares that make 100%', async () => {
    const user = userEvent.setup();
    const api = await owner();
    const job = await jobBy(api, 'iPhone 13');
    const [me, manager] = [await ownerId(api), await managerId(api)];
    render(<App api={api} initialPath={`/app/repairs/${job.id}`} lang="en" />);
    await user.click(await screen.findByRole('button', { name: 'Change shares' }));
    const techs = screen.getAllByLabelText('Technician').slice(1);   // the first is the job's technician
    await user.selectOptions(techs[0]!, me);
    await user.selectOptions(techs[1]!, manager);
    const shares = screen.getAllByLabelText('Share %');
    await user.clear(shares[0]!); await user.type(shares[0]!, '59');
    await user.clear(shares[1]!); await user.type(shares[1]!, '40');
    expect(screen.getByText('Total: 99%')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    await user.clear(shares[0]!); await user.type(shares[0]!, '60');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText(/· 60%/)).toBeInTheDocument();
    expect((await api.jobShares(job.id)).map((s) => s.share)).toEqual([60, 40]);
  });
});

describe('commissions (Phase 9)', () => {
  it('writes a rule for one technician (two rules for everyone at once are refused)', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/settings/commissions" lang="en" />);
    await user.click(await screen.findByRole('button', { name: 'New rule' }));
    const dlg = await screen.findByRole('dialog', { name: 'New rule' });
    await user.type(within(dlg).getByLabelText('Name (English)'), 'Owner labour');
    await user.type(within(dlg).getByLabelText('Name (Arabic)'), 'أجور المالك');
    await user.type(within(dlg).getByLabelText('Rate %'), '12.5');
    await user.click(within(dlg).getByRole('button', { name: 'Save rule' }));
    // The seeded workshop rule is for everyone and in force: another for everyone at the same time is refused.
    expect(await within(dlg).findByText(/Another rule for the same technicians/)).toBeInTheDocument();
    await user.selectOptions(within(dlg).getByLabelText('Technician'), await ownerId(api));
    await user.click(within(dlg).getByRole('button', { name: 'Save rule' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect((await screen.findByText('Owner labour')).closest('tr')!).toHaveTextContent('Salim Al Harthy');
    const rules = await api.commissionRules();
    expect(rules.find((r) => r.nameEn === 'Owner labour')).toMatchObject({ basis: 'labour', rate: 12.5, rank: 2, version: 1 });
  });

  it('generates the commission at delivery, then approves and pays it from the back office', async () => {
    const user = userEvent.setup();
    const api = await owner();
    const manager = await managerId(api);
    await api.createCommissionRule({ nameEn: 'Manager labour', nameAr: 'أجور المديرة', basis: 'labour', rate: 10, technicianId: manager, effectiveFrom: '2026-01-01' });
    const ready = (await api.repairs({ q: '' })).find((r) => r.status === 'ready')!;
    await api.assignRepair(ready.id, manager).catch(() => undefined);
    // The Galaxy is handed back at the till by the manager: 15.000, of which labour 6.000 (5.714 without VAT) → 0.571.
    const t01 = (await api.terminals()).find((x) => x.code === 'T01')!;
    await api.pinLoginAt('manager', '1357', { terminalId: t01.id, deviceKey: 'k'.repeat(43) });
    await api.openShift({ openingFloat: 100 });
    await api.deliver(ready.id, { check: 'qr', qrCode: (await api.repair(ready.id)).job.qr, collectedBy: 'Maryam Al Hinai', signature: PNG, payments: [{ method: 'cash', amount: 15 }] });
    await api.login('owner', OWNER_PW);

    render(<App api={api} initialPath="/app/commissions" lang="en" />);
    expect(await screen.findByRole('heading', { name: 'Commissions' })).toBeInTheDocument();
    const row = (await screen.findByRole('link', { name: ready.number })).closest('tr')!;
    expect(row).toHaveTextContent('0.571');
    expect(row).toHaveTextContent('Huda Al Balushi');
    await user.click(within(row).getByRole('checkbox'));
    await user.click(screen.getByRole('button', { name: 'Approve selected (1)' }));
    expect(await screen.findByText('1 approved.')).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'To pay' }));
    const payable = (await screen.findByText('Huda Al Balushi')).closest('tr')!;
    await user.click(within(payable).getByRole('button', { name: 'Pay' }));
    const dlg = await screen.findByRole('dialog', { name: 'Pay Huda Al Balushi' });
    await user.selectOptions(within(dlg).getByLabelText('Paid by'), 'Through payroll');
    await user.click(await within(dlg).findByRole('button', { name: 'Pay 0.571' }));
    expect(await screen.findByText(/^Payout COM-B01-\d{4}-\d+: 0\.571\.$/)).toBeInTheDocument();
    expect((await api.commissions({ jobId: ready.id }))[0]).toMatchObject({ status: 'paid' });

    await user.click(screen.getByRole('tab', { name: 'Statement' }));
    await screen.findByRole('option', { name: 'Huda Al Balushi' });
    await user.selectOptions(screen.getByLabelText('Technician'), manager);
    expect((await screen.findByText('Payable at the end')).parentElement).toHaveTextContent('0.000');
    await user.click(screen.getByRole('tab', { name: 'Rule usage' }));
    expect((await screen.findByText('Manager labour')).closest('tr')!).toHaveTextContent('0.571');
  });
});

describe('correcting a repair invoice at the till (Phase 9)', () => {
  async function tillApi() {
    const api = await owner();
    const t01 = (await api.terminals()).find((x) => x.code === 'T01')!;
    await api.createCommissionRule({ nameEn: 'Manager labour', nameAr: 'أجور المديرة', basis: 'labour', rate: 10, technicianId: await managerId(api), effectiveFrom: '2026-01-01' });
    const ready = (await api.repairs({ q: '' })).find((r) => r.status === 'ready')!;
    await api.assignRepair(ready.id, await managerId(api)).catch(() => undefined);
    await api.logout();
    localStorage.setItem('kite.terminal', JSON.stringify({ terminalId: t01.id, deviceKey: 'k'.repeat(43), code: 'T01', branchId: t01.branchId, printerId: null }));
    return { api, ready };
  }
  async function openShift(user: ReturnType<typeof userEvent.setup>) {
    await user.type(await screen.findByLabelText('Username'), 'manager');
    await user.type(screen.getByLabelText('PIN'), '1357');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByRole('heading', { name: 'Open shift' });
    await user.type(screen.getByLabelText('Pieces of 50'), '2');
    await user.click(screen.getByRole('button', { name: 'Open shift' }));
    await screen.findByLabelText('Scan a barcode or type a name');
  }

  it('credits the delivered invoice, refunds the drawer, re-invoices at the right price and regenerates the commission', async () => {
    const user = userEvent.setup();
    const { api, ready } = await tillApi();
    render(<App api={api} initialPath="/pos" lang="en" />);
    await openShift(user);
    // Deliver the ready Galaxy (15.000: 30 minutes' labour and a 9.000 battery) through the API, as the till would.
    const qr = (await api.repair(ready.id)).job.qr;
    await api.deliver(ready.id, { check: 'qr', qrCode: qr, collectedBy: 'Maryam Al Hinai', signature: PNG, payments: [{ method: 'cash', amount: 15 }] });
    const first = await api.commissions({ jobId: ready.id });
    expect(first.map((a) => a.amount)).toEqual([0.571]);   // 10% of 6.000 + VAT → 5.714 net

    await user.click(screen.getByRole('button', { name: 'Repairs' }));
    const dlg = await screen.findByRole('dialog', { name: 'Repairs at the counter' });
    await user.click(within(dlg).getByLabelText('Correct the invoice'));
    await user.type(within(dlg).getByLabelText(/Delivered job/), 'Maryam');
    await user.click(await within(dlg).findByRole('button', { name: new RegExp(ready.number) }));
    expect(await within(dlg).findByText('Invoice that stands now')).toBeInTheDocument();
    await user.type(within(dlg).getByLabelText('Why is it corrected?'), 'Labour charged twice');
    const prices = within(dlg).getAllByLabelText('Correct price');
    await user.clear(prices[0]!); await user.type(prices[0]!, '3');
    await user.click(within(dlg).getByRole('button', { name: /^Take payment 12\.000/ }));
    const pay = await screen.findByRole('dialog', { name: 'Payment' });
    await user.type(within(pay).getByLabelText('Cash given'), '12');
    await user.click(within(pay).getByRole('button', { name: 'Complete sale' }));
    expect(await screen.findByText(/^Credit note CN\S+ issued; corrected invoice INV\S+\.$/)).toBeInTheDocument();

    const after = await api.repair(ready.id);
    expect(after.corrections).toHaveLength(1);
    expect(after.invoice!.header.grossTotal).toBe(12);
    const accruals = await api.commissions({ jobId: ready.id });
    expect(accruals.find((a) => a.kind === 'reversal')!.amount).toBe(-0.571);
    expect(accruals.find((a) => a.kind === 'repair' && a.kindVersion === 2)!.amount).toBe(0.286);   // 10% of 2.857
    // The drawer: 15.000 in at delivery, 15.000 back, 12.000 in for the corrected invoice.
    expect((await api.currentShift()).figures!.expectedCash).toBe(100 + 15 - 15 + 12);
  });
});

describe('commissions in Arabic (Phase 9)', () => {
  it('lays the commission rules out right to left', async () => {
    const api = await owner();
    render(<App api={api} initialPath="/app/settings/commissions" lang="ar" />);
    expect(await screen.findByRole('heading', { name: 'قواعد العمولة' })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe('rtl');
    expect(await screen.findByText('الورشة: 10% من الأجور')).toBeInTheDocument();
    expect(screen.getByText('إعدادات العمولة')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'قاعدة جديدة' }));
    expect(await screen.findByRole('dialog', { name: 'قاعدة جديدة' })).toBeInTheDocument();
  });
});
