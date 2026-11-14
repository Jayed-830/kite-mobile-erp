import { createContext, useContext, useEffect, useId, useRef, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes } from 'react';
import './tokens.css';

export type Theme = 'light' | 'dark';

const ThemeContext = createContext<{ theme: Theme; setTheme: (t: Theme) => void } | null>(null);

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<Theme>(() => {
    try {
      const v = localStorage.getItem('kite.theme');
      if (v === 'dark' || v === 'light') return v;
    } catch { /* ignore */ }
    return typeof matchMedia === 'function' && matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  });
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);
  const setTheme = (t: Theme) => {
    setThemeState(t);
    try { localStorage.setItem('kite.theme', t); } catch { /* ignore */ }
  };
  return <ThemeContext.Provider value={{ theme, setTheme }}>{children}</ThemeContext.Provider>;
}

export function useTheme() {
  const v = useContext(ThemeContext);
  if (!v) throw new Error('useTheme must be used inside ThemeProvider');
  return v;
}

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'danger' | 'ghost' | 'default'; size?: 'sm' | 'md'; busy?: boolean };

export function Button({ variant = 'default', size = 'md', busy, className, children, disabled, type = 'button', ...rest }: ButtonProps) {
  const cls = ['btn', variant !== 'default' && `btn-${variant}`, size === 'sm' && 'btn-sm', className].filter(Boolean).join(' ');
  return (
    <button type={type} className={cls} disabled={disabled || busy} aria-busy={busy || undefined} {...rest}>
      {busy && <span className="spinner" aria-hidden="true" style={{ width: 16, height: 16, borderWidth: 2 }} />}
      {children}
    </button>
  );
}

interface FieldShell { label: string; help?: string; error?: string | null }

export function TextField({ label, help, error, className, ...rest }: FieldShell & InputHTMLAttributes<HTMLInputElement>) {
  const id = useId();
  return (
    <div className={['field', className].filter(Boolean).join(' ')}>
      <label htmlFor={id}>{label}{rest.required && <span aria-hidden="true"> *</span>}</label>
      <input id={id} className="input" aria-invalid={error ? true : undefined} aria-describedby={help || error ? `${id}-d` : undefined} {...rest} />
      {(error || help) && <span id={`${id}-d`} className={error ? 'error' : 'help'}>{error || help}</span>}
    </div>
  );
}

export function SelectField({ label, help, error, children, ...rest }: FieldShell & SelectHTMLAttributes<HTMLSelectElement> & { children: ReactNode }) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <select id={id} className="select" aria-invalid={error ? true : undefined} {...rest}>{children}</select>
      {(error || help) && <span className={error ? 'error' : 'help'}>{error || help}</span>}
    </div>
  );
}

export function Checkbox({ label, ...rest }: { label: ReactNode } & InputHTMLAttributes<HTMLInputElement>) {
  return (
    <label className="check">
      <input type="checkbox" {...rest} />
      <span>{label}</span>
    </label>
  );
}

export function Switch({ checked, onChange, disabled, label }: { checked: boolean; onChange: (v: boolean) => void; disabled?: boolean; label: string }) {
  return (
    <label className="switch">
      <input type="checkbox" role="switch" aria-label={label} checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
      <span className="slider" />
    </label>
  );
}

export function Card({ children, className }: { children: ReactNode; className?: string }) {
  return <section className={['card', className].filter(Boolean).join(' ')}>{children}</section>;
}

export function PageHeader({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="page-header">
      <h1>{title}</h1>
      <span className="spacer" />
      {children}
    </div>
  );
}

export function Badge({ tone = 'default', children }: { tone?: 'default' | 'success' | 'warning' | 'danger' | 'primary'; children: ReactNode }) {
  return <span className={`badge${tone === 'default' ? '' : ` badge-${tone}`}`}>{children}</span>;
}

/** Error/notice box. Errors show the server's code so staff can quote it to support (blueprint 21.4). */
export function Alert({ tone = 'info', children, code }: { tone?: 'info' | 'success' | 'warning' | 'error'; children: ReactNode; code?: string }) {
  return (
    <div className={`alert alert-${tone}`} role={tone === 'error' ? 'alert' : 'status'}>
      {children}
      {code && <> <span className="code ltr">({code})</span></>}
    </div>
  );
}

export function Spinner({ label }: { label: string }) {
  return (
    <div className="row" role="status">
      <span className="spinner" aria-hidden="true" />
      <span className="muted">{label}</span>
    </div>
  );
}

export interface Column<T> { key: string; header: string; render: (row: T) => ReactNode; width?: string }

export function DataTable<T>({ rows, columns, rowKey, empty }: { rows: T[]; columns: Column<T>[]; rowKey: (r: T) => string; empty: string }) {
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>{columns.map((c) => <th key={c.key} style={c.width ? { width: c.width } : undefined} scope="col">{c.header}</th>)}</tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr><td colSpan={columns.length} className="muted">{empty}</td></tr>
          ) : (
            rows.map((r) => <tr key={rowKey(r)}>{columns.map((c) => <td key={c.key}>{c.render(r)}</td>)}</tr>)
          )}
        </tbody>
      </table>
    </div>
  );
}

/** Accessible modal built on the native dialog element. */
export function Modal({ open, title, onClose, children, footer }: { open: boolean; title: string; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const d = ref.current;
    if (!d) return;
    if (open && !d.open) {
      if (typeof d.showModal === 'function') d.showModal();
      else d.setAttribute('open', '');
    }
    if (!open && d.open) {
      if (typeof d.close === 'function') d.close();
      else d.removeAttribute('open');
    }
  }, [open]);
  if (!open) return null;
  return (
    <dialog ref={ref} className="modal" aria-label={title} onCancel={(e) => { e.preventDefault(); onClose(); }}>
      <div className="modal-head"><h2>{title}</h2><span className="spacer" /><Button variant="ghost" size="sm" onClick={onClose} aria-label="close">✕</Button></div>
      <div className="modal-body">{children}</div>
      {footer && <div className="modal-foot">{footer}</div>}
    </dialog>
  );
}

export function Steps({ steps, current }: { steps: string[]; current: number }) {
  return (
    <ol className="steps" aria-label="progress" style={{ listStyle: 'none', padding: 0 }}>
      {steps.map((s, i) => (
        <li key={s} className={`step${i === current ? ' current' : i < current ? ' done' : ''}`} aria-current={i === current ? 'step' : undefined}>
          {i + 1}. {s}
        </li>
      ))}
    </ol>
  );
}
