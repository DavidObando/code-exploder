import { useEffect } from 'react';
import { Link, Outlet, useOutletContext, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api, ApiError } from '../../api/client';
import type { ExperienceToc, SessionSummary } from '../../api/types';
import { liveEvents } from '../../services/liveEvents';
import ui from '../../components/ui.module.css';
import { SectionNav } from './SectionNav';
import { PacingBar } from './PacingBar';
import styles from './tutorial.module.css';

export interface TutorialContext {
  toc: ExperienceToc;
  session: SessionSummary;
}

export function useTutorialContext(): TutorialContext {
  return useOutletContext<TutorialContext>();
}

export function TutorialLayout() {
  // Params merge across nested routes, so :sectionSlug is visible here.
  const { id = '', sectionSlug } = useParams<{ id: string; sectionSlug?: string }>();

  const session = useQuery({
    queryKey: ['session', id],
    queryFn: () => api.getSession(id),
    enabled: id !== '',
  });

  const experience = useQuery({
    queryKey: ['experience', id],
    queryFn: () => api.getExperience(id),
    enabled: id !== '',
    // 404 means "no experience yet" — a state, not a transient failure.
    retry: (count, err) => !(err instanceof ApiError && err.status === 404) && count < 2,
  });

  // SectionReady / lifecycle events for this session stream in and invalidate
  // ['experience', id]; generating rows in the TOC pop to ready live.
  useEffect(() => {
    if (!id) return;
    return liveEvents.subscribe(id);
  }, [id]);

  if (session.isPending || experience.isPending) {
    return <p className={styles.placeholder}>Loading tutorial…</p>;
  }

  const notReady =
    experience.isError && experience.error instanceof ApiError && experience.error.status === 404;
  if (notReady) {
    return (
      <div className={styles.paneMessage}>
        <h1 className={styles.paneTitle}>The tutorial isn't ready yet</h1>
        <p>The expert is still analyzing this codebase. Watch it work on the progress view.</p>
        <Link className={ui.buttonPrimary} to={`/sessions/${id}/progress`}>
          View analysis progress
        </Link>
      </div>
    );
  }

  if (session.isError || experience.isError || !session.data || !experience.data) {
    return (
      <div className={styles.paneMessage}>
        <h1 className={styles.paneTitle}>Tutorial unavailable</h1>
        <p>This session could not be loaded. It may have been deleted.</p>
        <Link className={ui.button} to="/">
          Back to Home
        </Link>
      </div>
    );
  }

  const context: TutorialContext = { toc: experience.data, session: session.data };

  return (
    <div className={styles.layout}>
      <SectionNav
        toc={experience.data}
        sessionId={id}
        sessionTitle={session.data.title}
        currentSlug={sectionSlug ?? null}
      />
      <div className={styles.content}>
        <div className={styles.reading}>
          <Outlet context={context} />
        </div>
        {sectionSlug && <PacingBar toc={experience.data} sessionId={id} currentSlug={sectionSlug} />}
      </div>
    </div>
  );
}
