import * as signalR from '@microsoft/signalr';
import type { QueryClient } from '@tanstack/react-query';
import { useSyncExternalStore } from 'react';
import { useUi } from '../store/ui';
import type { AnalysisFailedData, SectionReadyData, SessionEventEnvelope } from '../api/events';

// One SessionHub connection per app instance. The user's own sessions' lifecycle
// events arrive automatically (user group); pages additionally subscribe per session.
//
// Client policy (docs/02-queue-and-events.md):
//   lifecycle events  -> invalidate TanStack Query keys (server state stays authoritative)
//   stream events     -> direct render via listeners, never durable state
// lastEventId is tracked per session; on (re)connect we call GetEventsSince to catch up.

export type HubStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

type Listener = (evt: SessionEventEnvelope) => void;

const RETRY_MS = 5000;

class LiveEvents {
  private connection: signalR.HubConnection | null = null;
  private startPromise: Promise<void> | null = null;
  private queryClient: QueryClient | null = null;
  private listeners = new Set<Listener>();
  private statusListeners = new Set<() => void>();
  private status: HubStatus = 'disconnected';
  private subscribedSessions = new Set<string>();
  private lastEventId = new Map<string, number>();
  private retryTimer: ReturnType<typeof setTimeout> | null = null;

  /** Called once from the Shell; wires query invalidation and opens the connection. */
  attach(queryClient: QueryClient): () => void {
    this.queryClient = queryClient;
    void this.ensureStarted().catch(() => {});
    return () => {
      if (this.queryClient === queryClient) this.queryClient = null;
    };
  }

  getStatus(): HubStatus {
    return this.status;
  }

  subscribeStatus(cb: () => void): () => void {
    this.statusListeners.add(cb);
    return () => {
      this.statusListeners.delete(cb);
    };
  }

  /** Stream listener (AnalysisProgress/AnalysisNarration render directly). */
  listen(listener: Listener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  /**
   * Join a session group and catch up on missed events. Returns a synchronous
   * unsubscribe so it composes with useEffect.
   */
  subscribe(sessionId: string): () => void {
    this.subscribedSessions.add(sessionId);
    void this.ensureStarted()
      .then(async () => {
        const conn = this.connection;
        if (!conn || !this.subscribedSessions.has(sessionId)) return;
        await conn.invoke('SubscribeSession', sessionId).catch(() => {});
        await this.catchUp(sessionId);
      })
      .catch(() => {});
    return () => {
      if (!this.subscribedSessions.delete(sessionId)) return;
      this.connection?.invoke('UnsubscribeSession', sessionId).catch(() => {});
    };
  }

  /** Snapshots carry lastEventId; recording it keeps GetEventsSince catch-ups small. */
  noteSnapshot(sessionId: string, lastEventId: number) {
    const prev = this.lastEventId.get(sessionId) ?? 0;
    if (lastEventId > prev) this.lastEventId.set(sessionId, lastEventId);
  }

  private setStatus(status: HubStatus) {
    if (this.status === status) return;
    this.status = status;
    for (const cb of this.statusListeners) cb();
  }

  private async ensureStarted(): Promise<void> {
    if (!this.connection) {
      const conn = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/session')
        .withAutomaticReconnect()
        .configureLogging(signalR.LogLevel.Warning)
        .build();

      conn.on('sessionEvent', (evt: SessionEventEnvelope) => this.handleEvent(evt));

      conn.onreconnecting(() => this.setStatus('reconnecting'));
      conn.onreconnected(async () => {
        this.setStatus('connected');
        await this.resubscribeAll();
        // Anything may have happened while offline — refresh the session list.
        this.queryClient?.invalidateQueries({ queryKey: ['sessions'] });
      });
      conn.onclose(() => {
        this.startPromise = null;
        this.setStatus('disconnected');
        this.scheduleRetry();
      });

      this.connection = conn;
    }

    if (!this.startPromise) {
      this.setStatus('connecting');
      this.startPromise = this.connection
        .start()
        .then(() => this.setStatus('connected'))
        .catch((err) => {
          this.startPromise = null;
          this.setStatus('disconnected');
          this.scheduleRetry();
          throw err;
        });
    }
    await this.startPromise;
  }

  private scheduleRetry() {
    if (this.retryTimer) return;
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      void this.ensureStarted()
        .then(async () => {
          await this.resubscribeAll();
          this.queryClient?.invalidateQueries({ queryKey: ['sessions'] });
        })
        .catch(() => {});
    }, RETRY_MS);
  }

  /** Group membership is per-connection; re-join and catch up after any (re)connect. */
  private async resubscribeAll() {
    const conn = this.connection;
    if (!conn) return;
    for (const sessionId of this.subscribedSessions) {
      await conn.invoke('SubscribeSession', sessionId).catch(() => {});
      await this.catchUp(sessionId);
    }
  }

  private async catchUp(sessionId: string) {
    const conn = this.connection;
    if (!conn) return;
    const since = this.lastEventId.get(sessionId) ?? 0;
    try {
      const events = await conn.invoke<SessionEventEnvelope[]>('GetEventsSince', sessionId, since);
      for (const evt of events) {
        // A live event may have raced the catch-up; never process an id twice.
        if (evt.id <= (this.lastEventId.get(evt.sessionId) ?? 0)) continue;
        this.handleEvent(evt);
      }
    } catch {
      // Catch-up is best effort; the next snapshot fetch heals any gap.
    }
  }

  private handleEvent(evt: SessionEventEnvelope) {
    // Ids are monotonic per session and ride in every envelope; dropping stale ids
    // dedupes overlapping group deliveries (session + user group) exactly.
    const prev = this.lastEventId.get(evt.sessionId) ?? 0;
    if (evt.id <= prev) return;
    this.lastEventId.set(evt.sessionId, evt.id);

    const qc = this.queryClient;
    switch (evt.kind) {
      case 'AnalysisStageChanged':
        qc?.invalidateQueries({ queryKey: ['analysis', evt.sessionId] });
        qc?.invalidateQueries({ queryKey: ['sessions'] });
        break;
      case 'SectionReady': {
        const data = evt.data as SectionReadyData;
        useUi.getState().toast('success', 'Section ready', data.title);
        qc?.invalidateQueries({ queryKey: ['analysis', evt.sessionId] });
        qc?.invalidateQueries({ queryKey: ['experience', evt.sessionId] });
        break;
      }
      case 'AnalysisCompleted':
        qc?.invalidateQueries({ queryKey: ['analysis', evt.sessionId] });
        qc?.invalidateQueries({ queryKey: ['experience', evt.sessionId] });
        qc?.invalidateQueries({ queryKey: ['sessions'] });
        break;
      case 'AnalysisFailed': {
        const data = evt.data as AnalysisFailedData;
        useUi.getState().toast('error', 'Analysis failed', data.reason);
        qc?.invalidateQueries({ queryKey: ['analysis', evt.sessionId] });
        qc?.invalidateQueries({ queryKey: ['sessions'] });
        break;
      }
      // AnalysisProgress / AnalysisNarration: stream-only, handled by listeners.
    }

    for (const listener of this.listeners) listener(evt);
  }
}

export const liveEvents = new LiveEvents();

/** Connection status for the StatusBar dot. */
export function useHubStatus(): HubStatus {
  return useSyncExternalStore(
    (cb) => liveEvents.subscribeStatus(cb),
    () => liveEvents.getStatus(),
  );
}
