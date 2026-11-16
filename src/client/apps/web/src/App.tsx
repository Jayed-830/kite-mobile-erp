import { useState } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, MemoryRouter, Navigate, Route, Routes } from 'react-router-dom';
import { ApiError, type KiteApi } from '@kite/api';
import { I18nProvider, type Lang } from '@kite/i18n';
import { Spinner, ThemeProvider } from '@kite/ui';
import { ApiProvider, AuthProvider, useAuth } from './core';
import { Shell } from './Shell';
import { LoginPage } from './pages/Login';
import { SetupPage } from './pages/Setup';
import { DashboardPage } from './pages/Dashboard';
import { UsersPage } from './pages/Users';
import { RolesPage } from './pages/Roles';
import { TerminalsPage } from './pages/Terminals';
import { BusinessPage, ModulesPage, TaxPage, AccountsPage } from './pages/Settings';
import { AuditPage } from './pages/Audit';
import { ServersPage } from './pages/Servers';
import { LicencePage } from './pages/Licence';
import { BackupsPage } from './pages/Backups';
import { AccountPage } from './pages/Account';
import { CatalogPage } from './pages/Catalog';
import { InventoryPage } from './pages/Inventory';
import { CustomersPage } from './pages/Customers';
import { ApprovalsPage } from './pages/Approvals';
import { SalesPage } from './pages/Sales';
import { PosSettingsPage } from './pages/PosSettings';
import { PosApp } from './pos/PosApp';
import { DevicesPage } from './devices/DevicesPage';
import { DevicePage } from './devices/DevicePage';
import { InspectPage } from './devices/InspectPage';
import { RefurbPage } from './devices/RefurbPage';
import { DeviceSettingsPage } from './devices/DeviceSettingsPage';
import { useI18n } from '@kite/i18n';

function Guarded() {
  const { stage } = useAuth();
  const { t } = useI18n();
  if (stage === 'loading') return <div className="center-page"><Spinner label={t('app.loading')} /></div>;
  if (stage !== 'signedIn') return <Navigate to="/login" replace />;
  return <Shell />;
}

function AppRoutes() {
  return (
    <Routes>
      <Route path="/setup" element={<SetupPage />} />
      <Route path="/login" element={<LoginPage />} />
      <Route path="/pos" element={<PosApp />} />
      <Route path="/app" element={<Guarded />}>
        <Route index element={<DashboardPage />} />
        <Route path="users" element={<UsersPage />} />
        <Route path="roles" element={<RolesPage />} />
        <Route path="terminals" element={<TerminalsPage />} />
        <Route path="settings/business" element={<BusinessPage />} />
        <Route path="settings/modules" element={<ModulesPage />} />
        <Route path="settings/tax" element={<TaxPage />} />
        <Route path="settings/accounts" element={<AccountsPage />} />
        <Route path="audit" element={<AuditPage />} />
        <Route path="servers" element={<ServersPage />} />
        <Route path="licence" element={<LicencePage />} />
        <Route path="backups" element={<BackupsPage />} />
        <Route path="account" element={<AccountPage />} />
        <Route path="catalog" element={<CatalogPage />} />
        <Route path="inventory" element={<InventoryPage />} />
        <Route path="devices" element={<DevicesPage />} />
        <Route path="devices/:id" element={<DevicePage />} />
        <Route path="devices/:id/inspect" element={<InspectPage />} />
        <Route path="refurbishments/:id" element={<RefurbPage />} />
        <Route path="settings/devices" element={<DeviceSettingsPage />} />
        <Route path="customers" element={<CustomersPage />} />
        <Route path="approvals" element={<ApprovalsPage />} />
        <Route path="sales" element={<SalesPage />} />
        <Route path="settings/pos" element={<PosSettingsPage />} />
      </Route>
      <Route path="*" element={<Navigate to="/app" replace />} />
    </Routes>
  );
}

export function App({ api, initialPath, lang }: { api: KiteApi; initialPath?: string; lang?: Lang }) {
  const [queryClient] = useState(() => new QueryClient({
    defaultOptions: {
      // A 4xx answer (permission, module switched off, validation) will not change on retry.
      queries: { retry: (count, err) => !(err instanceof ApiError && err.status >= 400 && err.status < 500) && count < 1, refetchOnWindowFocus: false },
    },
  }));
  const body = (
    <ApiProvider api={api}>
      <AuthProvider>
        <AppRoutes />
      </AuthProvider>
    </ApiProvider>
  );
  return (
    <ThemeProvider>
      <I18nProvider initial={lang}>
        <QueryClientProvider client={queryClient}>
          {initialPath ? <MemoryRouter initialEntries={[initialPath]}>{body}</MemoryRouter> : <BrowserRouter>{body}</BrowserRouter>}
        </QueryClientProvider>
      </I18nProvider>
    </ThemeProvider>
  );
}
