import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { type TestChatAction, type WhatsAppSettingsInput, type WhatsAppTemplate } from '@kite/api';
import { useI18n } from '@kite/i18n';
import { Alert, Badge, Button, Card, PageHeader, SelectField, Spinner, Switch, TextField } from '@kite/ui';
import { ErrorBox, formatDateTime, useApi } from '../core';

/** WhatsApp on the main server (Phase 8 gateway): the provider and its secrets, the Nextdab internet address, templates and the test chat. */
export function WhatsAppPage() {
  const { t } = useI18n();
  const [tab, setTab] = useState<'settings' | 'templates' | 'testChat'>('settings');
  return (
    <div className="stack">
      <PageHeader title={t('wa.title')} />
      <div className="tabs" role="tablist">
        {(['settings', 'templates', 'testChat'] as const).map((k) => <button key={k} role="tab" aria-selected={tab === k} className={`tab${tab === k ? ' active' : ''}`} onClick={() => setTab(k)}>{t(`wa.${k}`)}</button>)}
      </div>
      {tab === 'settings' ? <Settings /> : tab === 'templates' ? <Templates /> : <TestChatTab />}
    </div>
  );
}

function Settings() {
  const api = useApi();
  const qc = useQueryClient();
  const { t, lang } = useI18n();
  const q = useQuery({ queryKey: ['waSettings'], queryFn: () => api.whatsAppSettings() });
  const [f, setF] = useState<WhatsAppSettingsInput | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const [steps, setSteps] = useState<{ step: string; ok: boolean; detail: string | null }[] | null>(null);
  useEffect(() => {
    const s = q.data;
    if (s) setF({ enabled: !!s.enabled, provider: s.provider ?? 'meta', phoneNumberId: s.phoneNumberId, wabaId: s.wabaId, displayPhone: s.displayPhone, accessToken: '', appSecret: '', dailyCap: s.dailyCap ?? 1000, staleHours: s.staleHours ?? 24, version: s.version ?? 0 });
  }, [q.data]);
  if (!q.data || !f) return q.error ? <ErrorBox error={q.error} /> : <Spinner label={t('app.loading')} />;
  const s = q.data;
  if (!s.gateway) return <Alert tone="info">{t('wa.branchNote')}</Alert>;
  const w = s.webAddress;
  const save = () => { setError(null); setSaved(false); api.updateWhatsAppSettings({ ...f, accessToken: f.accessToken || null, appSecret: f.appSecret || null }).then(() => { setSaved(true); void qc.invalidateQueries({ queryKey: ['waSettings'] }); }).catch(setError); };
  return (
    <div className="grid-2">
      <Card>
        <div className="stack">
          <ErrorBox error={error} />
          {saved && <Alert tone="success">{t('wa.saved')}</Alert>}
          <Switch label={t('wa.enabled')} checked={f.enabled} onChange={(v) => setF({ ...f, enabled: v })} />
          <SelectField label={t('wa.provider')} value={f.provider} onChange={(e) => setF({ ...f, provider: e.target.value as 'meta' | 'test' })}>
            <option value="meta">{t('wa.providers.meta')}</option><option value="test">{t('wa.providers.test')}</option>
          </SelectField>
          {f.provider === 'meta' && (
            <>
              <TextField label={t('wa.phoneNumberId')} className="ltr" value={f.phoneNumberId ?? ''} onChange={(e) => setF({ ...f, phoneNumberId: e.target.value || null })} />
              <TextField label={t('wa.wabaId')} className="ltr" value={f.wabaId ?? ''} onChange={(e) => setF({ ...f, wabaId: e.target.value || null })} />
              <TextField label={t('wa.accessToken')} type="password" autoComplete="off" className="ltr" placeholder={s.hasToken ? t('wa.kept') : ''} value={f.accessToken ?? ''} onChange={(e) => setF({ ...f, accessToken: e.target.value })} />
              <TextField label={t('wa.appSecret')} type="password" autoComplete="off" className="ltr" placeholder={s.hasSecret ? t('wa.kept') : ''} value={f.appSecret ?? ''} onChange={(e) => setF({ ...f, appSecret: e.target.value })} />
            </>
          )}
          <TextField label={t('wa.displayPhone')} className="ltr" value={f.displayPhone ?? ''} onChange={(e) => setF({ ...f, displayPhone: e.target.value || null })} />
          <div className="grid-2">
            <TextField label={t('wa.dailyCap')} className="ltr num" inputMode="numeric" value={String(f.dailyCap)} onChange={(e) => setF({ ...f, dailyCap: Number(e.target.value) })} />
            <TextField label={t('wa.staleHours')} className="ltr num" inputMode="numeric" value={String(f.staleHours)} onChange={(e) => setF({ ...f, staleHours: Number(e.target.value) })} />
          </div>
          <div><Button variant="primary" onClick={save}>{t('wa.save')}</Button></div>
        </div>
      </Card>
      <Card>
        <div className="stack">
          <div className="row"><strong>{t('wa.tunnel')}</strong><Badge tone={w.tunnel === 'connected' ? 'success' : w.tunnel === 'failed' ? 'danger' : 'default'}>{t(`wa.tunnels.${w.tunnel}`) || w.tunnel}</Badge></div>
          {w.host ? <span className="ltr mono">https://{w.host}</span> : <Alert tone="info">{t('wa.noAddress')}</Alert>}
          {w.error && <Alert tone="warning">{w.error}</Alert>}
          {!w.listening && <Alert tone="warning">{t('wa.notListening')}{w.problem ? `: ${w.problem}` : ''}</Alert>}
          <dl className="kv">
            <dt>{t('wa.webhookUrl')}</dt><dd className="ltr mono">{s.webhookUrl ?? '—'}</dd>
            <dt>{t('wa.verifyToken')}</dt><dd className="ltr mono">{s.verifyToken ?? '—'}</dd>
            <dt>{t('wa.lastWebhook')}</dt><dd>{formatDateTime(s.webhookSeenAt, lang) || '—'}</dd>
            <dt>{t('wa.sentToday')}</dt><dd className="num">{s.sentToday}</dd>
            <dt>{t('wa.relay')}</dt><dd>{formatDateTime(s.relay.lastOk, lang) || '—'}{s.relay.error ? ` · ${s.relay.error}` : ''}</dd>
          </dl>
          {s.provider === 'meta' && <div><Button onClick={() => api.connectWhatsApp().then((r) => setSteps(r.steps)).catch(setError)}>{t('wa.connect')}</Button></div>}
          {steps && <ul>{steps.map((x) => <li key={x.step}><Badge tone={x.ok ? 'success' : 'danger'}>{t(`wa.connectSteps.${x.step}`)}</Badge> <span className="muted">{x.detail}</span></li>)}</ul>}
        </div>
      </Card>
    </div>
  );
}

function Templates() {
  const api = useApi();
  const qc = useQueryClient();
  const { t } = useI18n();
  const q = useQuery({ queryKey: ['waTemplates'], queryFn: () => api.whatsAppTemplates() });
  const [error, setError] = useState<unknown>(null);
  if (!q.data) return q.error ? <ErrorBox error={q.error} /> : <Spinner label={t('app.loading')} />;
  return (
    <div className="stack">
      <ErrorBox error={error} />
      {q.data.map((tp) => <TemplateCard key={tp.id} tp={tp} onSave={(x) => api.updateWhatsAppTemplate(tp.id, x).then(() => qc.invalidateQueries({ queryKey: ['waTemplates'] })).catch(setError)} />)}
    </div>
  );
}

function TemplateCard({ tp, onSave }: { tp: WhatsAppTemplate; onSave: (x: { metaName: string; metaLang: string; body: string; buttons: string[]; version: number }) => void }) {
  const { t } = useI18n();
  const [metaName, setMetaName] = useState(tp.metaName);
  const [body, setBody] = useState(tp.body);
  return (
    <Card>
      <div className="stack">
        <div className="row"><strong className="ltr mono">{tp.code}</strong><Badge>{tp.lang}</Badge><span className="muted">{t('wa.template.params')}: {tp.params.join(', ')}</span>{tp.buttons.length > 0 && <span className="muted">{t('wa.template.buttons')}: {tp.buttons.join(' · ')}</span>}</div>
        <TextField label={t('wa.template.metaName')} className="ltr" value={metaName} onChange={(e) => setMetaName(e.target.value)} />
        <div className="field"><label htmlFor={`b-${tp.id}`}>{t('wa.template.body')}</label><textarea id={`b-${tp.id}`} className="input" rows={2} dir={tp.lang === 'ar' ? 'rtl' : 'ltr'} value={body} onChange={(e) => setBody(e.target.value)} /></div>
        <div><Button size="sm" onClick={() => onSave({ metaName, metaLang: tp.metaLang, body, buttons: tp.buttons, version: tp.version })}>{t('wa.save')}</Button></div>
      </div>
    </Card>
  );
}

function TestChatTab() {
  const api = useApi();
  const qc = useQueryClient();
  const { t, lang } = useI18n();
  const [phone, setPhone] = useState<string | null>(null);
  const [typed, setTyped] = useState('');
  const [reply, setReply] = useState('');
  const [error, setError] = useState<unknown>(null);
  const q = useQuery({ queryKey: ['waTestChat', phone], queryFn: () => api.testChat(phone), refetchInterval: 5000 });
  const act = (action: TestChatAction, extra: { text?: string; button?: number } = {}) => {
    setError(null);
    api.testChatAction({ phone: phone!, action, ...extra }).then(() => { setReply(''); void qc.invalidateQueries({ queryKey: ['waTestChat'] }); void qc.invalidateQueries({ queryKey: ['repair'] }); }).catch(setError);
  };
  const lastButtons = [...(q.data?.messages ?? [])].reverse().find((m) => m.direction === 'out' && m.buttons?.length)?.buttons ?? [];
  return (
    <div className="grid-2">
      <Card>
        <div className="stack">
          <div className="row">
            <TextField label={t('wa.chat.phone')} className="ltr" value={typed} onChange={(e) => setTyped(e.target.value)} />
            <Button onClick={() => setPhone(typed.trim() || null)}>{t('wa.chat.open')}</Button>
          </div>
          {(q.data?.phones ?? []).map((p) => <button key={p.phone} type="button" className={`picker-item${p.phone === phone ? ' active' : ''}`} onClick={() => { setPhone(p.phone); setTyped(p.phone); }}><span className="ltr">{p.phone}</span> <span className="muted">{formatDateTime(p.last, lang)}</span></button>)}
        </div>
      </Card>
      <Card>
        <div className="stack">
          <ErrorBox error={error} />
          <div className="chat">
            {(q.data?.messages ?? []).length === 0 && <span className="muted">{t('wa.chat.none')}</span>}
            {(q.data?.messages ?? []).map((m) => (
              <div key={m.id} className={`msg ${m.direction === 'out' ? 'msg-in' : 'msg-out'}`} dir="auto">
                {m.body}
                {m.buttons && <div className="row">{m.buttons.map((b) => <Badge key={b}>{b}</Badge>)}</div>}
                <div className="msg-meta">{formatDateTime(m.at, lang)}{m.status ? ` · ${m.status}` : ''}</div>
              </div>
            ))}
          </div>
          {phone && (
            <>
              <div className="row">
                {(['delivered', 'read', 'failed'] as const).map((a) => <Button key={a} size="sm" onClick={() => act(a)}>{t(`wa.chat.actions.${a}`)}</Button>)}
                {lastButtons.map((b, i) => <Button key={b} size="sm" variant="primary" onClick={() => act('button', { button: i })}>{t('wa.chat.tap', { label: b })}</Button>)}
              </div>
              <div className="row">
                <TextField label={t('wa.chat.reply')} value={reply} onChange={(e) => setReply(e.target.value)} dir="auto" className="grow" />
                <Button onClick={() => act('text', { text: reply })} disabled={!reply.trim()}>{t('wa.chat.actions.text')}</Button>
                <Button size="sm" variant="ghost" onClick={() => act('stop')}>{t('wa.chat.actions.stop')}</Button>
                <Button size="sm" variant="ghost" onClick={() => act('start')}>{t('wa.chat.actions.start')}</Button>
              </div>
            </>
          )}
        </div>
      </Card>
    </div>
  );
}
