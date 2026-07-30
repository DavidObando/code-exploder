import { create } from 'zustand';
import { applyToken, resyncFromFlushed } from '../features/chat/streamBuffer';
import type { StreamBuffer } from '../features/chat/streamBuffer';

// Ephemeral chat UI: panel visibility and per-message streaming buffers.
// Thread/message rows live in TanStack Query; buffers are live-render only and
// are cleared on QaMessageCompleted.

interface ChatState {
  panelOpen: boolean;
  setPanelOpen: (open: boolean) => void;
  buffers: Record<string, StreamBuffer>;
  ingestToken: (messageId: string, seq: number, text: string) => void;
  resyncBuffer: (messageId: string, flushedContent: string) => void;
  clearBuffer: (messageId: string) => void;
}

export const useChat = create<ChatState>((set) => ({
  panelOpen: false,
  setPanelOpen: (open) => set({ panelOpen: open }),
  buffers: {},
  ingestToken: (messageId, seq, text) =>
    set((s) => ({
      buffers: { ...s.buffers, [messageId]: applyToken(s.buffers[messageId], seq, text) },
    })),
  resyncBuffer: (messageId, flushedContent) =>
    set((s) => {
      const buffer = s.buffers[messageId];
      if (!buffer) return s;
      return { buffers: { ...s.buffers, [messageId]: resyncFromFlushed(buffer, flushedContent) } };
    }),
  clearBuffer: (messageId) =>
    set((s) => {
      if (!(messageId in s.buffers)) return s;
      const buffers = { ...s.buffers };
      delete buffers[messageId];
      return { buffers };
    }),
}));
