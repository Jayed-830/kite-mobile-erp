import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { createMockApi } from '../mock';

const OWNER_PW = 'Muscat-Ruwi-2026!';

async function signIn(user: ReturnType<typeof userEvent.setup>, password = OWNER_PW) {
  await user.type(await screen.findByLabelText(/Username/), 'owner');
  await user.type(screen.getByLabelText(/^Password/), password);
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
}

describe('sign-in', () => {
  it('shows the server error with its code, then signs in to the dashboard', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: true })} initialPath="/login" lang="en" />);
    await signIn(user, 'wrong-password');
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Username or password is not correct.');
    expect(alert).toHaveTextContent('AUTH-001');

    await user.clear(screen.getByLabelText(/^Password/));
    await user.type(screen.getByLabelText(/^Password/), OWNER_PW);
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('heading', { name: /Welcome, Salim Al Harthy/ })).toBeInTheDocument();
    expect(await screen.findByText(/5 tax code\(s\) waiting for your accountant/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Users' })).toBeInTheDocument();
  });

  it('asks for the two-step code when the user has it switched on', async () => {
    const api = createMockApi({ configured: true });
    await api.login('owner', OWNER_PW);
    await api.enableTotp('123456');
    await api.logout();
    const user = userEvent.setup();
    render(<App api={api} initialPath="/login" lang="en" />);
    await signIn(user);
    expect(await screen.findByRole('heading', { name: 'Two-step verification' })).toBeInTheDocument();
    await user.type(screen.getByLabelText(/Code/), '000000');
    await user.click(screen.getByRole('button', { name: 'Verify' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('AUTH-005');
    await user.clear(screen.getByLabelText(/Code/));
    await user.type(screen.getByLabelText(/Code/), '123456');
    await user.click(screen.getByRole('button', { name: 'Verify' }));
    expect(await screen.findByRole('heading', { name: /Welcome/ })).toBeInTheDocument();
  });
});

describe('Arabic and right-to-left', () => {
  it('switches the whole page to Arabic and RTL', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: true })} initialPath="/login" lang="en" />);
    await user.click(await screen.findByRole('button', { name: 'العربية' }));
    expect(document.documentElement.dir).toBe('rtl');
    expect(document.documentElement.lang).toBe('ar');
    expect(screen.getByRole('heading', { name: 'تسجيل الدخول' })).toBeInTheDocument();
    expect(localStorage.getItem('kite.lang')).toBe('ar');
  });
});

describe('setup wizard', () => {
  it('validates each step, links module dependencies and reports a wrong setup code', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: false })} initialPath="/setup" lang="en" />);
    const next = await screen.findByRole('button', { name: 'Next' });
    expect(next).toBeDisabled();

    await user.type(screen.getByLabelText(/Setup code/), 'WRONG123');
    await user.type(screen.getByLabelText(/Legal name \(English\)/), 'Al Noor Mobiles LLC');
    await user.type(screen.getByLabelText(/Legal name \(Arabic\)/), 'شركة النور للهواتف');
    expect(next).toBeEnabled();
    await user.click(next);

    await user.type(screen.getByLabelText(/Branch name \(English\)/), 'Muscat');
    await user.type(screen.getByLabelText(/Branch name \(Arabic\)/), 'مسقط');
    await user.click(screen.getByRole('button', { name: 'Next' }));

    await user.type(screen.getByLabelText(/Your name \(English\)/), 'Salim');
    await user.type(screen.getByLabelText(/^Password/), 'Muscat-Ruwi-2026!');
    await user.type(screen.getByLabelText(/Confirm password/), 'Muscat-Ruwi-2026?');
    expect(screen.getByText('The passwords do not match.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
    await user.clear(screen.getByLabelText(/Confirm password/));
    await user.type(screen.getByLabelText(/Confirm password/), 'Muscat-Ruwi-2026!');
    await user.click(screen.getByRole('button', { name: 'Next' }));

    const tradeIn = screen.getByRole('switch', { name: 'Trade-in' });
    expect(tradeIn).not.toBeChecked();
    await user.click(tradeIn);
    expect(screen.getByRole('switch', { name: 'Condition and refurbishment' })).toBeChecked();
    expect(screen.getByRole('switch', { name: 'Serialized devices and IMEI' })).toBeChecked();
    await user.click(screen.getByRole('switch', { name: 'Serialized devices and IMEI' }));
    expect(screen.getByRole('switch', { name: 'Trade-in' })).not.toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(screen.getByText(/5% standard rate/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Create business' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('SET-002');
  });
});

describe('administration', () => {
  it('shows the module rule from the server and blocks a switched-off module', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ configured: true });
    render(<App api={api} initialPath="/login" lang="en" />);
    await signIn(user);
    await screen.findByRole('heading', { name: /Welcome/ });

    await user.click(screen.getByRole('link', { name: 'Modules' }));
    const refurb = await screen.findByRole('switch', { name: 'Condition and refurbishment' });
    expect(screen.getByRole('switch', { name: 'POS and sales' })).toBeDisabled();
    await user.click(screen.getByRole('switch', { name: 'Trade-in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('MOD-001');
    expect(refurb).not.toBeChecked();

    await user.click(screen.getByRole('switch', { name: 'Accounting screens' }));
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Accounting screens' })).not.toBeChecked());
    await user.click(screen.getByRole('link', { name: 'Chart of accounts' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('MOD-403');
  });

  it('lets the owner add a user with a branch role', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: true })} initialPath="/login" lang="en" />);
    await signIn(user);
    await screen.findByRole('heading', { name: /Welcome/ });
    await user.click(screen.getByRole('link', { name: 'Users' }));
    await user.click(await screen.findByRole('button', { name: 'Add user' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText(/Username/), 'cashier_amal');
    await user.type(within(dialog).getByLabelText(/^Name( \*)?$/), 'Amal');
    await user.type(within(dialog).getByLabelText(/Temporary password/), 'Temp-Pass-2026');
    await user.click(within(dialog).getByRole('button', { name: 'Add role' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('cashier_amal')).toBeInTheDocument();
  });
});
