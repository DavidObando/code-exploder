import { useEffect } from 'react';
import { Outlet } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { liveEvents } from '../../services/liveEvents';
import { Toaster } from '../../components/Toaster';
import { NavRail } from './NavRail';
import { StatusBar } from './StatusBar';
import styles from './shell.module.css';

export function Shell() {
  const queryClient = useQueryClient();
  useEffect(() => liveEvents.attach(queryClient), [queryClient]);

  // Shared ['sessions'] query: feeds the rail badge here and the Home left pane.
  // Kept fresh by user-group lifecycle events (invalidation), not polling.
  const sessions = useQuery({
    queryKey: ['sessions'],
    queryFn: api.listSessions,
    retry: 1,
  });

  const activeCount =
    sessions.data?.filter((s) => s.status === 'queued' || s.status === 'analyzing').length ?? 0;

  return (
    <div className={styles.frame}>
      <NavRail activeCount={activeCount} />
      <main className={styles.main}>
        <Outlet />
      </main>
      <StatusBar />
      <Toaster />
    </div>
  );
}
