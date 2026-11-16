import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { REPAIR_STATUSES, REPAIR_TRANSITIONS, type KiteApi } from '@kite/api';
import { App } from '../App';
import { createMockApi } from '../mock';

const OWNER_PW = 'Muscat-Ruwi-2026!';
const PNG = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==';

// jsdom has no canvas: a pen that records strokes and returns a PNG data URL, as a browser would.
beforeAll(() => {
  HTMLCanvasElement.prototype.getContext = (() => ({ beginPath() {}, moveTo() {}, lineTo() {}, stroke() {}, clearRect() {}, lineWidth: 1, lineCap: 'round', strokeStyle: '#000' })) as unknown as HTMLCanvasElement['getContext'];
  HTMLCanvasElement.prototype.toDataURL = () => PNG;
});
beforeEach(() => { window.print = () => undefined; window.open = () => null; URL.createObjectURL = () => 'blob:x'; URL.revokeObjectURL = () => undefined; });

function sign(canvas: HTMLElement) {
  fireEvent.pointerDown(canvas, { clientX: 5, clientY: 5, pointerId: 1 });
  fireEvent.pointerMove(canvas, { clientX: 40, clientY: 30, pointerId: 1 });
  fireEvent.pointerUp(canvas, { pointerId: 1 });
}
async function owner() { const api = createMockApi({ configured: true }); await api.login('owner', OWNER_PW); return api; }
const jobBy = async (api: KiteApi, model: string) => (await api.repairs({ q: '' })).find((r) => r.device.includes(model))!;

describe('the workshop (Phase 8)', () => {
  it('shows the jobs by stage and books a device in with the customer’s signature', async () => {
    const user = userEvent.setup();
    const api = await owner();
    render(<App api={api} initialPath="/app/repairs" lang="en" />);
    const diagnosis = await screen.findByRole('region', { name: 'Diagnosis' });
    expect(await within(diagnosis).findByText(/Apple iPhone 13/)).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Ready to collect' })).getByText(/Samsung Galaxy A54/)).toBeInTheDocument();

    await user.click(screen.getByRole('link', { name: 'New repair' }));
    expect(await screen.findByRole('heading', { name: 'Book a device in' })).toBeInTheDocument();
    await user.type(screen.getByLabelText('Find the customer (name or phone)'), 'Maryam');
    await user.click(await screen.findByRole('option', { name: /Maryam Al Hinai/ }));
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(await screen.findByRole('radio', { name: 'Another device' }));
    await user.type(screen.getByLabelText('Scan or type the IMEI or serial number'), '356789012345671');
    await user.type(screen.getByLabelText('Brand'), 'Apple');
    await user.type(screen.getByLabelText(/^Model/), 'iPhone 15');
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.selectOptions(await screen.findByLabelText(/^Kind of repair/), 'Screen');
    await user.type(screen.getByLabelText('What the customer says is wrong'), 'Black screen after a drop.');
    await user.click(screen.getByLabelText('Cracked glass'));
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(within(await screen.findByRole('radiogroup', { name: 'Screen' })).getByRole('radio', { name: 'Fault' }));
    await user.click(screen.getByLabelText('Charger'));
    await user.selectOptions(screen.getByLabelText('Access to the device'), 'Passcode given');
    await user.type(screen.getByLabelText('Passcode or pattern'), '2580');
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(await screen.findByLabelText('Send updates by WhatsApp'));
    // Booking in without the signature is refused.
    await user.click(screen.getByRole('button', { name: 'Book in' }));
    expect(await screen.findByText('The customer signs to accept the terms.')).toBeInTheDocument();
    sign(screen.getByRole('img', { name: 'Customer signs here' }));
    await user.click(screen.getByRole('button', { name: 'Book in' }));
    expect(await screen.findByText(/^Job R-B01-\d{4}-\d+ booked in\.$/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Open the job' }));
    expect(await screen.findByRole('heading', { name: /^Repair job R-B01-/ })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Show passcode' }));
    expect(await screen.findByText('Passcode: 2580')).toBeInTheDocument();
  });

  it('offers only the allowed moves, writes an estimate and moves on once it is approved by phone', async () => {
    const user = userEvent.setup();
    const api = await owner();
    const job = await jobBy(api, 'iPhone 13');
    render(<App api={api} initialPath={`/app/repairs/${job.id}`} lang="en" />);
    await screen.findByRole('heading', { name: `Repair job ${job.number}` });
    // Under diagnosis: waiting for approval, in progress (blocked until an estimate is approved) and cannot be repaired. Nothing else.
    const moves = screen.getAllByRole('button', { name: /^Move to:/ }).map((b) => b.textContent);
    expect(moves).toEqual(['Move to: Waiting for approval', 'Move to: In progress', 'Move to: Cannot be repaired']);
    expect(screen.getByRole('button', { name: 'Move to: In progress' })).toBeDisabled();

    await user.click(screen.getByRole('tab', { name: 'Estimates' }));
    await user.click(await screen.findByRole('button', { name: 'Write estimate' }));
    await user.click(screen.getByRole('button', { name: 'Add labour' }));
    await user.clear(screen.getByLabelText('Minutes'));
    await user.type(screen.getByLabelText('Minutes'), '45');
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByText('Version 1')).toBeInTheDocument();
    expect(screen.getByText('Total:', { exact: false })).toHaveTextContent('9.000');   // 45 minutes at 12.000 an hour

    await user.click(screen.getByRole('button', { name: 'Approve' }));
    const dlg = await screen.findByRole('dialog', { name: /^Approve · Version 1/ });
    await user.selectOptions(within(dlg).getByLabelText('How the customer approved'), 'By phone');
    await user.type(within(dlg).getByLabelText('Number called'), '{selectall}+96899112233');
    await user.click(within(dlg).getByRole('button', { name: 'Approve' }));
    expect(await screen.findByText(/Approved .* \(By phone\)/)).toBeInTheDocument();
    expect(screen.getAllByText('In progress').length).toBeGreaterThan(0);
    const d = await api.repair(job.id);
    expect(d.job.status).toBe('in_progress');
    expect(d.estimates[0]!.approvalMethod).toBe('phone');
  });

  it('refuses every move the blueprint does not allow, and lets the customer approve with WhatsApp’s button', async () => {
    const api = await owner();
    const job = await jobBy(api, 'iPhone 13');
    for (const to of REPAIR_STATUSES.filter((s) => !REPAIR_TRANSITIONS.under_diagnosis.includes(s) && s !== 'under_diagnosis'))
      await expect(api.moveRepair(job.id, to)).rejects.toMatchObject({ code: to === 'delivered' ? 'REP-004' : 'REP-001' });
    const services = (await api.repairSetup()).services;
    const est = await api.writeEstimate(job.id, [{ kind: 'extra', variantId: services.find((s) => s.sku === 'REPAIR-EXTRA')!.id, unitPrice: 42, descriptionEn: 'Screen assembly' }]);
    await api.moveRepair(job.id, 'waiting_approval');

    const user = userEvent.setup();
    render(<App api={api} initialPath="/app/settings/whatsapp" lang="en" />);
    await user.click(await screen.findByRole('tab', { name: 'Test chat' }));
    await user.click(await screen.findByRole('button', { name: /\+96899112233/ }));
    expect(await screen.findByText(/the estimate for job R-B01-.* is 42\.000 OMR/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Customer taps “Approve”' }));
    await waitFor(async () => expect((await api.repair(job.id)).estimates[0]).toMatchObject({ id: est.estimates[0]!.id, status: 'approved', approvalMethod: 'whatsapp' }));
    // The customer replies; staff can answer within 24 hours. STOP ends the updates.
    await user.type(screen.getByLabelText('Customer’s reply'), 'Thank you');
    await user.click(screen.getByRole('button', { name: 'Customer replies' }));
    await waitFor(async () => expect((await api.repairMessages(job.id)).canReply).toBe(true));
    await user.click(screen.getByRole('button', { name: 'Customer sends STOP' }));
    await waitFor(async () => expect((await api.repair(job.id)).job.customerOptIn).toBe(false));
  });
});

describe('repairs in Arabic (Phase 8)', () => {
  it('shows the workshop board right to left with Arabic stages and statuses', async () => {
    const api = await owner();
    render(<App api={api} initialPath="/app/repairs" lang="ar" />);
    const lane = await screen.findByRole('region', { name: 'التشخيص' });
    expect(await within(lane).findByText('قيد التشخيص')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'جاهز للاستلام' })).toBeInTheDocument();
    expect(document.documentElement.getAttribute('dir')).toBe('rtl');
  });
});

describe('repairs at the till (Phase 8)', () => {
  async function tillApi() {
    const api = await owner();
    const t01 = (await api.terminals()).find((x) => x.code === 'T01')!;
    await api.logout();
    localStorage.setItem('kite.terminal', JSON.stringify({ terminalId: t01.id, deviceKey: 'k'.repeat(43), code: 'T01', branchId: t01.branchId, printerId: null }));
    return api;
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

  it('takes a deposit into the drawer, then hands a repaired device back against its card with the deposit applied', async () => {
    const user = userEvent.setup();
    const api = await tillApi();
    render(<App api={api} initialPath="/pos" lang="en" />);
    await openShift(user);

    // A deposit on the job in the workshop.
    await user.click(screen.getByRole('button', { name: 'Repairs' }));
    let dlg = await screen.findByRole('dialog', { name: 'Repairs at the counter' });
    await user.type(within(dlg).getByLabelText(/Scan the job card/), 'Maryam');
    await user.click(await within(dlg).findByRole('button', { name: /iPhone 13/ }));
    await user.click(within(dlg).getByRole('button', { name: 'Take deposit' }));
    await user.type(within(dlg).getByLabelText('Amount'), '10');
    await user.click(within(dlg).getByRole('button', { name: 'Take deposit' }));
    expect(await screen.findByText(/Deposit DEP\S+ taken\. Held: 10\.000\./)).toBeInTheDocument();

    // The ready Galaxy: 15.000 to pay (30 minutes' labour and a 9.000 battery), collected against the card's QR.
    const ready = (await api.repairs({ q: 'Maryam' })).find((r) => r.status === 'ready')!;
    const qr = (await api.repair(ready.id)).job.qr;
    await user.click(screen.getByRole('button', { name: 'Repairs' }));
    dlg = await screen.findByRole('dialog', { name: 'Repairs at the counter' });
    await user.type(within(dlg).getByLabelText(/Scan the job card/), qr);
    await user.click(await within(dlg).findByRole('button', { name: new RegExp(ready.number) }));
    await user.click(within(dlg).getByRole('button', { name: 'Collect' }));
    expect((await within(dlg).findByText('Still to pay')).parentElement).toHaveTextContent('15.000');
    expect(within(dlg).getByLabelText('Who is collecting')).toHaveValue('qr');
    await user.type(within(dlg).getByLabelText('Name of the person collecting'), 'Maryam Al Hinai');
    sign(within(dlg).getByRole('img', { name: 'Signature' }));
    await user.click(within(dlg).getByRole('button', { name: /^Pay 15\.000/ }));
    const pay = await screen.findByRole('dialog', { name: 'Payment' });
    await user.type(within(pay).getByLabelText('Cash given'), '20');
    await user.click(within(pay).getByRole('button', { name: 'Complete sale' }));
    expect(await screen.findByText(new RegExp(`Job ${ready.number} delivered\\. Invoice INV`))).toBeInTheDocument();

    const after = await api.repair(ready.id);
    expect(after.job).toMatchObject({ status: 'delivered', collectionCheck: 'qr', collectedBy: 'Maryam Al Hinai' });
    expect(after.job.warrantyUntil).not.toBeNull();
    const shift = await api.currentShift();
    expect(shift.figures).toMatchObject({ repairDeposits: 10, cashPayments: 15 });
    expect(shift.figures!.expectedCash).toBe(100 + 10 + 15);
  });
});
