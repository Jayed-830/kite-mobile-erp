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
import { TradeInPage, TradeInsPage } from './tradein/TradeInsPage';
import { TradeInSettingsPage } from './tradein/TradeInSettingsPage';
import { RepairsPage } from './repairs/RepairsPage';
import { IntakePage } from './repairs/IntakePage';
import { RepairPage } from './repairs/RepairPage';
import { RepairSettingsPage } from './repairs/RepairSettingsPage';
import { WhatsAppPage } from './repairs/WhatsAppPage';
import { PartsNeededPage } from './repairs/PartsNeededPage';
import { CommissionsPage, MyCommissionsPage } from './commissions/CommissionsPage';
import { CommissionRulesPage } from './commissions/CommissionRulesPage';
import { CreditPage } from './credit/CreditPage';
import { CreditAccountPage } from './credit/CreditAccountPage';
import { CreditSettingsPage } from './credit/CreditSettingsPage';
import { SupplierPage, SuppliersPage } from './suppliers/SuppliersPage';
import { PurchasingPage } from './purchasing/PurchasingPage';
import { OrderPage, PaymentPage, ReceiptPage, ReturnPage } from './purchasing/OrderPage';
import { InvoiceEntryPage, InvoicePage } from './purchasing/InvoicePage';
import { PurchSettingsPage } from './purchasing/PurchSettingsPage';
import { AccountingPage, JournalPage, ManualJournalPage } from './accounting/AccountingPage';
import { StatementsPage } from './accounting/StatementsPage';
import { VatPage, VatReturnPage } from './accounting/VatPage';
import { ExpensesPage } from './expenses/ExpensesPage';

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
        <Route path="tradeins" element={<TradeInsPage />} />
        <Route path="tradeins/:id" element={<TradeInPage />} />
        <Route path="settings/tradein" element={<TradeInSettingsPage />} />
        <Route path="repairs" element={<RepairsPage />} />
        <Route path="repairs/new" element={<IntakePage />} />
        <Route path="repairs/parts" element={<PartsNeededPage />} />
        <Route path="repairs/:id" element={<RepairPage />} />
        <Route path="settings/repairs" element={<RepairSettingsPage />} />
        <Route path="settings/whatsapp" element={<WhatsAppPage />} />
        <Route path="commissions" element={<CommissionsPage />} />
        <Route path="commissions/mine" element={<MyCommissionsPage />} />
        <Route path="settings/commissions" element={<CommissionRulesPage />} />
        <Route path="credit" element={<CreditPage />} />
        <Route path="credit/:customerId" element={<CreditAccountPage />} />
        <Route path="settings/credit" element={<CreditSettingsPage />} />
        <Route path="suppliers" element={<SuppliersPage />} />
        <Route path="suppliers/:id" element={<SupplierPage />} />
        <Route path="purchasing" element={<PurchasingPage />} />
        <Route path="purchasing/orders/:id" element={<OrderPage />} />
        <Route path="purchasing/receipts/:id" element={<ReceiptPage />} />
        <Route path="purchasing/invoices/new" element={<InvoiceEntryPage />} />
        <Route path="purchasing/invoices/:id" element={<InvoicePage />} />
        <Route path="purchasing/invoices/:id/edit" element={<InvoiceEntryPage />} />
        <Route path="purchasing/returns/:id" element={<ReturnPage />} />
        <Route path="purchasing/payments/:id" element={<PaymentPage />} />
        <Route path="settings/purchasing" element={<PurchSettingsPage />} />
        <Route path="accounting" element={<AccountingPage />} />
        <Route path="accounting/journals/:id" element={<JournalPage />} />
        <Route path="accounting/manual/:id" element={<ManualJournalPage />} />
        <Route path="accounting/statements" element={<StatementsPage />} />
        <Route path="accounting/vat" element={<VatPage />} />
        <Route path="accounting/vat/:id" element={<VatReturnPage />} />
        <Route path="expenses" element={<ExpensesPage />} />
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
