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

describe('shared terminals', () => {
  it('drops the previous user\'s cached data when another user signs in', async () => {
    const api = createMockApi({ configured: true });
    await api.login('owner', OWNER_PW);
    const cashier = (await api.roles()).find((r) => r.code === 'cashier')!;
    await api.createUser({ username: 'cashier1', displayNameEn: 'Aisha', temporaryPassword: 'Temp-Seeb-2026!', preferredLanguage: 'en', roles: [{ roleId: cashier.id, branchId: null }] });
    await api.logout();

    const user = userEvent.setup();
    render(<App api={api} initialPath="/login" lang="en" />);
    await signIn(user);
    expect(await screen.findByText(/5 tax code\(s\) waiting/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    await user.type(await screen.findByLabelText(/Username/), 'cashier1');
    await user.type(screen.getByLabelText(/^Password/), 'Temp-Seeb-2026!');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('heading', { name: 'Welcome, Aisha' })).toBeInTheDocument();
    expect(screen.queryByText(/tax code/)).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Users' })).not.toBeInTheDocument();
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

  it('shows server errors in Arabic, keeping the code for support', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: true })} initialPath="/login" lang="ar" />);
    await user.type(await screen.findByLabelText(/اسم المستخدم/), 'owner');
    await user.type(screen.getByLabelText(/^كلمة المرور/), 'wrong-password');
    await user.click(screen.getByRole('button', { name: 'دخول' }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('اسم المستخدم أو كلمة المرور غير صحيحة.');
    expect(alert).toHaveTextContent('AUTH-001');
    expect(alert).not.toHaveTextContent('not correct');
  });
});

describe('setup wizard', () => {
  it('validates each step, links module dependencies and reports a wrong setup code', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: false })} initialPath="/setup" lang="en" />);
    await user.click(await screen.findByRole('button', { name: /^A new business/ }));
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

describe('branches and servers (Phase 3)', () => {
  it('adds a branch and shows the one-time token with the security code to compare', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: true })} initialPath="/login" lang="en" />);
    await signIn(user);
    await user.click(await screen.findByRole('link', { name: 'Branches and servers' }));
    expect(await screen.findByTestId('ca-code')).toHaveTextContent('4821-0937-5520-1764-3086');
    expect(screen.getByText('SOHAR-PC')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Add branch' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText(/^Code/), 'b03');
    await user.type(within(dialog).getByLabelText(/Branch name \(English\)/), 'Nizwa');
    await user.type(within(dialog).getByLabelText(/Branch name \(Arabic\)/), 'نزوى');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    const row = (await screen.findByText('B03')).closest('tr')!;
    expect(within(row).getByText('No server yet')).toBeInTheDocument();

    await user.click(within(row).getByRole('button', { name: 'Register server' }));
    const tokenDialog = await screen.findByRole('dialog', { name: 'Register the server of branch B03' });
    expect(within(tokenDialog).getByTestId('registration-token')).toHaveTextContent('kite-reg-token-0123456789');
    expect(tokenDialog).toHaveTextContent('4821-0937-5520-1764-3086');
    expect(tokenDialog).toHaveTextContent('https://192.168.10.5:5443');
    await user.click(within(tokenDialog).getByRole('button', { name: 'Close' }));
    expect(await screen.findByText('Open registration tokens')).toBeInTheDocument();
  });

  it('revokes a branch server after asking for the reason', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: true })} initialPath="/login" lang="en" />);
    await signIn(user);
    await user.click(await screen.findByRole('link', { name: 'Branches and servers' }));
    const row = (await screen.findByText('SOHAR-PC')).closest('tr')!;
    await user.click(within(row).getByRole('button', { name: 'Revoke server' }));
    const dialog = await screen.findByRole('dialog');
    await user.selectOptions(within(dialog).getByLabelText('Reason'), 'key_compromise');
    await user.click(within(dialog).getByRole('button', { name: 'Revoke server' }));
    await waitFor(() => expect(within(row).getByText('Revoked')).toBeInTheDocument());
    expect(within(row).getByRole('button', { name: 'Register server' })).toBeInTheDocument();
  });
});

describe('licence (Phase 3)', () => {
  it('shows the warning banner before the end and the licence page with limits', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: true, licence: 'expiring' })} initialPath="/login" lang="en" />);
    await signIn(user);
    expect(await screen.findByTestId('licence-banner')).toHaveTextContent(/The licence ends on .* \(9 day\(s\) left\)/);
    await user.click(screen.getByRole('link', { name: 'Open the licence page.' }));
    expect(await screen.findByTestId('installation-id')).toHaveTextContent('1A2B-3C4D-5E6F-7A8B');
    expect(screen.getByText('2 of 3')).toBeInTheDocument();
    expect(screen.getByText('Ending soon')).toBeInTheDocument();
  });

  it('imports a licence file and refuses a changed one with its code', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: true, licence: 'none' })} initialPath="/login" lang="en" />);
    await signIn(user);
    expect(await screen.findByTestId('licence-banner')).toHaveTextContent('KITE has no licence yet');
    await user.click(screen.getByRole('link', { name: 'Licence' }));
    const lic = { id: 'kme-1', mid: '1A2B-3C4D-5E6F-7A8B', kind: 'subscription', customer: 'Al Noor Mobiles LLC', valid_until: '2027-11-13', branches: 3, terminals: 10, modules: ['core'], grace_days: 30 };
    const box = await screen.findByLabelText('Or paste the licence text');
    await user.click(box);
    await user.paste(JSON.stringify({ type: 'KITE MOBILE ERP licence file', version: 1, licence: lic, sig: 'tampered' }));
    await user.click(screen.getByRole('button', { name: 'Import licence file' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('LIC-011');

    await user.clear(box);
    await user.click(box);
    await user.paste(JSON.stringify({ type: 'KITE MOBILE ERP licence file', version: 1, licence: lic, sig: 'mock-valid-signature' }));
    await user.click(screen.getByRole('button', { name: 'Import licence file' }));
    expect(await screen.findByText(/Licence imported: Subscription until/)).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByTestId('licence-banner')).not.toBeInTheDocument());
  });

  it('shows the Nextdab switch-off message in Arabic', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: true, licence: 'suspended' })} initialPath="/login" lang="ar" />);
    await user.type(await screen.findByLabelText(/اسم المستخدم/), 'owner');
    await user.type(screen.getByLabelText(/^كلمة المرور/), OWNER_PW);
    await user.click(screen.getByRole('button', { name: 'دخول' }));
    // The owner's own language is English: the app follows it after sign-in, then the user switches to Arabic.
    expect(await screen.findByTestId('licence-banner')).toHaveTextContent('KITE is switched off by Nextdab. Please contact Nextdab about the renewal.');
    await user.click(screen.getByRole('button', { name: 'العربية' }));
    expect(await screen.findByTestId('licence-banner')).toHaveTextContent('أوقفت نكستداب كايت. يرجى التواصل مع نكستداب بخصوص التجديد.');
  });

  it('marks modules that are not in the licence and does not let them be switched on', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: true, licenceModules: ['core', 'catalog', 'inv', 'sales', 'tax', 'device', 'repair', 'purch', 'acct_ui'] })} initialPath="/login" lang="en" />);
    await signIn(user);
    await user.click(await screen.findByRole('link', { name: 'Modules' }));
    expect(await screen.findByRole('switch', { name: 'WhatsApp notifications' })).toBeDisabled();
    expect(screen.getAllByText('Not in the licence').length).toBeGreaterThan(0);
  });
});

describe('backups (Phase 3)', () => {
  it('shows the last verified restore and the recovery key after the password', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: true })} initialPath="/login" lang="en" />);
    await signIn(user);
    await user.click(await screen.findByRole('link', { name: 'Backups' }));
    expect(await screen.findByText('Backups are running.')).toBeInTheDocument();
    expect(screen.getByText(/Journals balanced, sync sequence gap-free, audit chain intact/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Show recovery key' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Your password'), 'wrong-password');
    await user.click(within(dialog).getByRole('button', { name: 'Show recovery key' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('AUTH-001');
    await user.clear(within(dialog).getByLabelText('Your password'));
    await user.type(within(dialog).getByLabelText('Your password'), OWNER_PW);
    await user.click(within(dialog).getByRole('button', { name: 'Show recovery key' }));
    expect(await screen.findByTestId('recovery-key')).toHaveTextContent('K4TE-7QXM');
  });

  it('says plainly when backups are not set up', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: true, backupsConfigured: false })} initialPath="/login" lang="en" />);
    await signIn(user);
    await user.click(await screen.findByRole('link', { name: 'Backups' }));
    expect(await screen.findByText(/Backups are not set up on this server yet/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Back up now' })).not.toBeInTheDocument();
  });
});

describe('join as a branch server (Phase 3)', () => {
  it('compares the security code before the token can be entered', async () => {
    const user = userEvent.setup();
    render(<App api={createMockApi({ configured: false })} initialPath="/setup" lang="en" />);
    await user.click(await screen.findByRole('button', { name: /^A branch server of an existing business/ }));
    await user.type(screen.getByLabelText(/Setup code/), 'ABCD2345');
    await user.clear(screen.getByLabelText(/Head office address/));
    await user.type(screen.getByLabelText(/Head office address/), 'https://192.168.10.5:5443');
    await user.click(screen.getByRole('button', { name: 'Connect' }));
    expect(await screen.findByTestId('join-code')).toHaveTextContent('4821-0937-5520-1764-3086');
    expect(screen.getByText('Al Noor Mobiles LLC')).toBeInTheDocument();
    expect(screen.queryByLabelText(/Registration token/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Join' })).toBeDisabled();
    await user.click(screen.getByLabelText('The codes are the same'));
    await user.type(screen.getByLabelText(/Registration token/), 'kite-reg-token-0123456789');
    await user.click(screen.getByRole('button', { name: 'Join' }));
    expect(await screen.findByText('This server is now the branch server. Sign in with your usual account.')).toBeInTheDocument();
  });
});
