import { NavLink, Outlet } from 'react-router-dom';
import { can } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Button, useTheme } from '@kite/ui';
import { useAuth } from './core';
import { LicenceBanner } from './pages/Licence';

/** Back-office shell (blueprint 21.1): sidebar by module, top bar with language, theme and user. */
export function Shell() {
  const { me, signOut } = useAuth();
  const { t, lang, setLang, pick } = useI18n();
  const { theme, setTheme } = useTheme();
  if (!me) return null;

  const links: { to: string; label: string; show: boolean; section?: string }[] = [
    { to: '/app', label: t('nav.dashboard'), show: true },
    { to: '/pos', label: t('nav.pos'), show: can(me, 'POS.SELL'), section: t('nav.sales') },
    { to: '/app/sales', label: t('nav.salesReports'), show: can(me, 'POS.REPORTS.VIEW') },
    { to: '/app/approvals', label: t('nav.approvals'), show: can(me, 'APPROVALS.DECIDE') },
    { to: '/app/customers', label: t('nav.customers'), show: can(me, 'CUSTOMERS.VIEW') },
    { to: '/app/credit', label: t('nav.credit'), show: can(me, 'CREDIT.VIEW') },
    { to: '/app/catalog', label: t('nav.catalog'), show: can(me, 'CATALOG.VIEW'), section: t('nav.stock') },
    { to: '/app/inventory', label: t('nav.inventory'), show: can(me, 'INV.VIEW') },
    { to: '/app/devices', label: t('nav.devices'), show: can(me, 'DEVICE.VIEW') },
    { to: '/app/tradeins', label: t('nav.tradeins'), show: can(me, 'TRADEIN.VIEW') },
    { to: '/app/repairs', label: t('nav.repairs'), show: can(me, 'REPAIR.VIEW') },
    { to: '/app/repairs/parts', label: t('nav.partsNeeded'), show: can(me, 'REPAIR.VIEW') },
    { to: '/app/commissions', label: t('nav.commissions'), show: can(me, 'COMM.VIEW') },
    { to: '/app/commissions/mine', label: t('nav.myCommissions'), show: can(me, 'COMM.OWN') && !can(me, 'COMM.VIEW') },
    { to: '/app/suppliers', label: t('nav.suppliers'), show: can(me, 'SUPPLIER.VIEW') },
    { to: '/app/users', label: t('nav.users'), show: can(me, 'USERS.VIEW'), section: t('nav.administration') },
    { to: '/app/roles', label: t('nav.roles'), show: can(me, 'USERS.VIEW') },
    { to: '/app/terminals', label: t('nav.terminals'), show: can(me, 'TERMINALS.APPROVE') },
    { to: '/app/audit', label: t('nav.audit'), show: can(me, 'AUDIT.VIEW') },
    { to: '/app/servers', label: t('nav.servers'), show: can(me, 'SETTINGS.VIEW') },
    { to: '/app/licence', label: t('nav.licence'), show: can(me, 'LICENCE.MANAGE') },
    { to: '/app/backups', label: t('nav.backups'), show: can(me, 'BACKUP.VIEW') },
    { to: '/app/settings/business', label: t('nav.business'), show: can(me, 'SETTINGS.VIEW'), section: t('nav.settings') },
    { to: '/app/settings/modules', label: t('nav.modules'), show: can(me, 'SETTINGS.VIEW') },
    { to: '/app/settings/tax', label: t('nav.tax'), show: can(me, 'SETTINGS.VIEW') },
    { to: '/app/settings/accounts', label: t('nav.accounts'), show: can(me, 'SETTINGS.VIEW') },
    { to: '/app/settings/pos', label: t('nav.posSettings'), show: can(me, 'SETTINGS.VIEW') || can(me, 'POS.POLICY.MANAGE') },
    { to: '/app/settings/devices', label: t('nav.deviceSettings'), show: can(me, 'DEVICE.SETTINGS') },
    { to: '/app/settings/tradein', label: t('nav.tradeinSettings'), show: can(me, 'TRADEIN.SETTINGS') },
    { to: '/app/settings/repairs', label: t('nav.repairSettings'), show: can(me, 'REPAIR.SETTINGS') },
    { to: '/app/settings/whatsapp', label: t('nav.whatsapp'), show: can(me, 'NOTIF.SETTINGS') },
    { to: '/app/settings/commissions', label: t('nav.commissionRules'), show: can(me, 'COMM.RULES') },
    { to: '/app/settings/credit', label: t('nav.creditSettings'), show: can(me, 'CREDIT.MANAGE') },
    { to: '/app/account', label: t('nav.account'), show: true, section: '' },
  ];

  return (
    <div className="shell">
      <nav className="sidebar" aria-label={t('app.name')}>
        <div className="brand">
          <div className="brand-mark" aria-hidden="true">K</div>
          <div><strong>KITE</strong><small>{t('app.by')}</small></div>
        </div>
        {links.filter((l) => l.show).map((l) => (
          <div key={l.to}>
            {l.section !== undefined && l.section !== '' && <div className="nav-section">{l.section}</div>}
            <NavLink to={l.to} end={l.to === '/app'} className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}>{l.label}</NavLink>
          </div>
        ))}
      </nav>
      <div className="main">
        <header className="topbar">
          <strong>{pick(me.business.legalNameEn, me.business.legalNameAr)}</strong>
          <span className="muted">{me.allBranches ? t('app.allBranches') : me.branches.map((b) => pick(b.nameEn, b.nameAr)).join(lang === 'ar' ? '، ' : ', ')}</span>
          <span className="spacer" />
          <Button size="sm" variant="ghost" onClick={() => setLang(lang === 'en' ? 'ar' : 'en')}>
            {lang === 'en' ? 'العربية' : 'English'}
          </Button>
          <Button size="sm" variant="ghost" onClick={() => setTheme(theme === 'light' ? 'dark' : 'light')}>
            {theme === 'light' ? t('app.dark') : t('app.light')}
          </Button>
          <span>{pick(me.user.displayNameEn, me.user.displayNameAr)}</span>
          <Button size="sm" onClick={() => void signOut()}>{t('app.signOut')}</Button>
        </header>
        <main className="content">
          <LicenceBanner />
          <Outlet />
        </main>
      </div>
    </div>
  );
}
