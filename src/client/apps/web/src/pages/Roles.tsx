import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { can, type Role } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, Checkbox, Modal, PageHeader, Spinner, TextField } from '@kite/ui';
import { ErrorBox, useApi, useAuth } from '../core';

interface Draft { id?: string; code: string; nameEn: string; nameAr: string; permissions: Set<string>; version: number }

export function RolesPage() {
  const api = useApi();
  const qc = useQueryClient();
  const { me } = useAuth();
  const { t, pick } = useI18n();
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => api.roles() });
  const perms = useQuery({ queryKey: ['permissions'], queryFn: () => api.permissions() });
  const [draft, setDraft] = useState<Draft | null>(null);
  const manage = can(me, 'ROLES.MANAGE');

  const byModule = useMemo(() => {
    const m = new Map<string, NonNullable<typeof perms.data>>();
    for (const p of perms.data ?? []) m.set(p.moduleCode, [...(m.get(p.moduleCode) ?? []), p]);
    return [...m.entries()];
  }, [perms.data]);

  const save = useMutation({
    mutationFn: (d: Draft) => d.id
      ? api.updateRole(d.id, { nameEn: d.nameEn, nameAr: d.nameAr, permissions: [...d.permissions], version: d.version })
      : api.createRole({ code: d.code, nameEn: d.nameEn, nameAr: d.nameAr, permissions: [...d.permissions] }),
    onSuccess: async () => { setDraft(null); await qc.invalidateQueries({ queryKey: ['roles'] }); },
  });

  const edit = (r: Role) => { save.reset(); setDraft({ id: r.id, code: r.code, nameEn: r.nameEn, nameAr: r.nameAr, permissions: new Set(r.permissions), version: r.version }); };

  if (roles.isLoading) return <Spinner label={t('app.loading')} />;

  return (
    <div className="stack">
      <PageHeader title={t('roles.title')}>
        {manage && <Button variant="primary" onClick={() => { save.reset(); setDraft({ code: '', nameEn: '', nameAr: '', permissions: new Set(), version: 1 }); }}>{t('roles.add')}</Button>}
      </PageHeader>
      <div className="grid-2">
        {roles.data?.map((r) => (
          <Card key={r.id}>
            <div className="stack">
              <div className="row">
                <h2>{pick(r.nameEn, r.nameAr)}</h2>
                {r.isSystem && <Badge>{t('roles.system')}</Badge>}
                <span className="spacer" />
                {manage && r.code !== 'owner' && <Button size="sm" onClick={() => edit(r)}>{t('app.edit')}</Button>}
              </div>
              <span className="muted">{r.permissions.length} {t('roles.permissions')}</span>
              {r.code === 'owner' && <span className="muted" style={{ fontSize: 13 }}>{t('roles.ownerLocked')}</span>}
            </div>
          </Card>
        ))}
      </div>

      {draft && (
        <Modal open title={draft.id ? pick(draft.nameEn, draft.nameAr) : t('roles.add')} onClose={() => setDraft(null)}
          footer={<><Button onClick={() => setDraft(null)}>{t('app.cancel')}</Button><Button variant="primary" busy={save.isPending} onClick={() => save.mutate(draft)}>{t('app.save')}</Button></>}>
          {!draft.id && <TextField label={t('roles.code')} value={draft.code} onChange={(e) => setDraft({ ...draft, code: e.target.value.toLowerCase() })} dir="ltr" required />}
          <div className="grid-2">
            <TextField label={t('roles.nameEn')} value={draft.nameEn} onChange={(e) => setDraft({ ...draft, nameEn: e.target.value })} required />
            <TextField label={t('roles.nameAr')} value={draft.nameAr} onChange={(e) => setDraft({ ...draft, nameAr: e.target.value })} dir="rtl" required />
          </div>
          <h3>{t('roles.permissions')}</h3>
          {!manage && <Alert tone="warning">{t('roles.ownerLocked')}</Alert>}
          {byModule.map(([module, list]) => (
            <fieldset key={module} style={{ border: '1px solid var(--border)', borderRadius: 8, padding: 12 }}>
              <legend className="muted" style={{ paddingInline: 6 }}>{module}</legend>
              {list.map((p) => (
                <Checkbox key={p.code}
                  label={<span>{pick(p.descriptionEn, p.descriptionAr)} <span className="muted ltr" style={{ fontSize: 12 }}>{p.code}</span></span>}
                  checked={draft.permissions.has(p.code)}
                  disabled={!me?.permissions.includes(p.code)}
                  onChange={(e) => { const s = new Set(draft.permissions); if (e.target.checked) s.add(p.code); else s.delete(p.code); setDraft({ ...draft, permissions: s }); }} />
              ))}
            </fieldset>
          ))}
          <ErrorBox error={save.error} />
        </Modal>
      )}
    </div>
  );
}
