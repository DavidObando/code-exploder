import { Navigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import styles from './tutorial.module.css';

/** /sessions/:id — status-based redirect: ready|partial → learn, else progress. */
export function SessionRedirect() {
  const { id = '' } = useParams<{ id: string }>();
  const session = useQuery({
    queryKey: ['session', id],
    queryFn: () => api.getSession(id),
    enabled: id !== '',
  });

  if (session.isPending) {
    return <p className={styles.placeholder}>Loading session…</p>;
  }
  if (session.isError || !session.data) {
    return <Navigate to={`/sessions/${id}/progress`} replace />;
  }

  const { status } = session.data;
  const target = status === 'ready' || status === 'partial' ? 'learn' : 'progress';
  return <Navigate to={`/sessions/${id}/${target}`} replace />;
}
