import { applyToken, resyncFromFlushed } from './streamBuffer';

describe('applyToken', () => {
  it('starts a buffer cleanly on the first token (seq 1)', () => {
    const b = applyToken(undefined, 1, 'Hello');
    expect(b).toEqual({ text: 'Hello', lastSeq: 1, needsResync: false });
  });

  it('flags a late join (first token with seq > 1) for resync', () => {
    const b = applyToken(undefined, 5, 'world');
    expect(b.needsResync).toBe(true);
    expect(b.lastSeq).toBe(5);
  });

  it('appends contiguous tokens', () => {
    let b = applyToken(undefined, 1, 'Hello');
    b = applyToken(b, 2, ', ');
    b = applyToken(b, 3, 'world');
    expect(b).toEqual({ text: 'Hello, world', lastSeq: 3, needsResync: false });
  });

  it('ignores duplicate and out-of-order tokens', () => {
    let b = applyToken(undefined, 1, 'A');
    b = applyToken(b, 2, 'B');
    const before = b;
    b = applyToken(b, 2, 'B');
    expect(b).toBe(before);
    b = applyToken(b, 1, 'A');
    expect(b).toBe(before);
  });

  it('detects a seq gap: keeps appending but flags needsResync', () => {
    let b = applyToken(undefined, 1, 'A');
    b = applyToken(b, 4, 'D'); // missed 2 and 3
    expect(b.text).toBe('AD');
    expect(b.lastSeq).toBe(4);
    expect(b.needsResync).toBe(true);
  });

  it('keeps the resync flag across later clean appends until resolved', () => {
    let b = applyToken(undefined, 1, 'A');
    b = applyToken(b, 4, 'D');
    b = applyToken(b, 5, 'E');
    expect(b.needsResync).toBe(true);
    expect(b.text).toBe('ADE');
  });
});

describe('resyncFromFlushed', () => {
  it('replaces the buffer with longer flushed row content and clears the flag', () => {
    const gappy = applyToken(applyToken(undefined, 1, 'A'), 4, 'D');
    const healed = resyncFromFlushed(gappy, 'ABCD');
    expect(healed).toEqual({ text: 'ABCD', lastSeq: 4, needsResync: false });
  });

  it('keeps the buffer when it is ahead of the flushed content', () => {
    const buffer = { text: 'ABCDEF', lastSeq: 6, needsResync: true };
    const healed = resyncFromFlushed(buffer, 'ABC');
    expect(healed.text).toBe('ABCDEF');
    expect(healed.needsResync).toBe(false);
  });
});
