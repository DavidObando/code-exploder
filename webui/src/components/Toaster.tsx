import { useEffect } from 'react';
import { useUi } from '../store/ui';
import type { Toast, ToastKind } from '../store/ui';
import styles from './ui.module.css';

const kindGlyph: Record<ToastKind, string> = { success: '✓', warning: '▲', error: '✕', info: '·' };
const kindColor: Record<ToastKind, string> = {
  success: 'var(--keep)',
  warning: 'var(--suggestion)',
  error: 'var(--error)',
  info: 'var(--accent)',
};

function ToastRow({ toast }: { toast: Toast }) {
  const dismiss = useUi((s) => s.dismissToast);
  useEffect(() => {
    if (toast.sticky) return;
    const ms = toast.kind === 'warning' ? 8000 : 5000;
    const timer = setTimeout(() => dismiss(toast.id), ms);
    return () => clearTimeout(timer);
  }, [toast, dismiss]);

  return (
    <div className={styles.toast} style={{ borderLeftColor: kindColor[toast.kind] }} role="status">
      <span style={{ color: kindColor[toast.kind] }}>{kindGlyph[toast.kind]}</span>
      <div>
        <div className={styles.toastTitle}>{toast.title}</div>
        {toast.detail && <div className={styles.toastDetail}>{toast.detail}</div>}
      </div>
      <button
        onClick={() => dismiss(toast.id)}
        aria-label="Dismiss"
        style={{ marginLeft: 'auto', background: 'none', border: 'none', color: 'var(--text-muted)', cursor: 'pointer' }}
      >
        ✕
      </button>
    </div>
  );
}

export function Toaster() {
  const toasts = useUi((s) => s.toasts);
  return (
    <div className={styles.toaster}>
      {toasts.map((t) => (
        <ToastRow key={t.id} toast={t} />
      ))}
    </div>
  );
}
