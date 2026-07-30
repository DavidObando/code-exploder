import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { useHubStatus } from '../../services/liveEvents';
import type { HubStatus } from '../../services/liveEvents';
import { useUi } from '../../store/ui';
import styles from './shell.module.css';

const dotColor: Record<HubStatus, string> = {
  connected: 'var(--keep)',
  connecting: 'var(--suggestion)',
  reconnecting: 'var(--suggestion)',
  disconnected: 'var(--error)',
};

const dotText: Record<HubStatus, string> = {
  connected: 'Live',
  connecting: 'Connecting…',
  reconnecting: 'Reconnecting…',
  disconnected: 'Offline',
};

/** Bottom status bar: hub connection dot, queue depth, identity, theme toggle. */
export function StatusBar() {
  const { theme, setTheme } = useUi();
  const hubStatus = useHubStatus();

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

  const depth = system.data?.queue.depth;

  return (
    <footer className={styles.status}>
      <span>
        <span
          className={styles.dot}
          style={{ background: dotColor[hubStatus] }}
          aria-hidden="true"
        />{' '}
        {dotText[hubStatus]}
      </span>
      {depth !== undefined && (
        <span>
          {depth} job{depth === 1 ? '' : 's'} queued
        </span>
      )}
      <div className={styles.statusRight}>
        {me.data && <span>{me.data.name}</span>}
        <button
          className={styles.themeButton}
          onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}
        >
          {theme === 'dark' ? '☀ Light' : '☾ Dark'}
        </button>
      </div>
    </footer>
  );
}
