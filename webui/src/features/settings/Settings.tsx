import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { useUi } from '../../store/ui';
import { usePageTitle } from '../../lib/usePageTitle';
import ui from '../../components/ui.module.css';
import styles from './settings.module.css';

export function Settings() {
  usePageTitle('Settings — Code Exploder');
  const { theme, setTheme } = useUi();

  const system = useQuery({
    queryKey: ['system'],
    queryFn: api.getSystemStatus,
    refetchInterval: 5000,
    retry: false,
  });

  const me = useQuery({
    queryKey: ['me'],
    queryFn: api.getMe,
    staleTime: 5 * 60 * 1000,
    retry: 1,
  });

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Settings</h1>

      <section className={ui.card}>
        <h2 className={styles.cardTitle}>System status</h2>
        {system.isError && <p className={styles.muted}>Backend unreachable.</p>}
        {system.data && (
          <div className={styles.chipRow}>
            <span
              className={ui.chip}
              style={{ color: system.data.db ? 'var(--keep)' : 'var(--error)' }}
            >
              database {system.data.db ? 'ok' : 'down'}
            </span>
            <span className={ui.chip}>{system.data.queue.depth} queued</span>
            <span className={ui.chip}>{system.data.queue.activeJobs} active job{system.data.queue.activeJobs === 1 ? '' : 's'}</span>
          </div>
        )}
        {system.isPending && <p className={styles.muted}>Checking…</p>}
      </section>

      <section className={ui.card}>
        <h2 className={styles.cardTitle}>Appearance</h2>
        <button
          className={ui.button}
          onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}
        >
          Switch to {theme === 'dark' ? 'light' : 'dark'} theme
        </button>
      </section>

      <section className={ui.card}>
        <h2 className={styles.cardTitle}>You</h2>
        <p style={{ margin: 0 }}>
          {me.data ? `${me.data.name} · development bypass` : 'Signing in…'}
        </p>
      </section>
    </div>
  );
}
