// Pure streaming-buffer logic for QaToken deltas — unit-tested without the hub.
//
// QaToken events are coalesced text deltas with a per-message `seq`. They are
// live-only (absent from GetEventsSince); the message row's ~1/s flushed content
// is the recovery story when a gap is detected.

export interface StreamBuffer {
  text: string;
  lastSeq: number;
  /** A seq gap was seen — the owner should refetch the thread and resync. */
  needsResync: boolean;
}

/**
 * Applies one token. Rules:
 * - first token: starts the buffer (seq !== 1 means we joined late → resync)
 * - seq === lastSeq + 1: clean append
 * - seq <= lastSeq: duplicate/out-of-order — ignored
 * - seq > lastSeq + 1: missed a flush — still append (keeps the stream moving)
 *   but flag needsResync so the flushed row content can heal the hole.
 */
export function applyToken(
  buffer: StreamBuffer | undefined,
  seq: number,
  text: string,
): StreamBuffer {
  if (!buffer) {
    return { text, lastSeq: seq, needsResync: seq !== 1 };
  }
  if (seq <= buffer.lastSeq) return buffer;
  if (seq === buffer.lastSeq + 1) {
    return { text: buffer.text + text, lastSeq: seq, needsResync: buffer.needsResync };
  }
  return { text: buffer.text + text, lastSeq: seq, needsResync: true };
}

/**
 * Resyncs from the row's flushed content after a gap. The flushed row may lag
 * tokens we already appended, so the longer of the two wins; the completed
 * message (via query invalidation) is the final authority either way.
 */
export function resyncFromFlushed(buffer: StreamBuffer, flushedContent: string): StreamBuffer {
  return {
    text: flushedContent.length >= buffer.text.length ? flushedContent : buffer.text,
    lastSeq: buffer.lastSeq,
    needsResync: false,
  };
}
