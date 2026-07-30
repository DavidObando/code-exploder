import { create } from 'zustand';

export type Theme = 'dark' | 'light';

export type ToastKind = 'success' | 'warning' | 'error' | 'info';

export interface Toast {
  id: number;
  kind: ToastKind;
  title: string;
  detail?: string;
  /** Errors are sticky: they never auto-dismiss. */
  sticky: boolean;
}

interface UiState {
  theme: Theme;
  toasts: Toast[];
  setTheme: (theme: Theme) => void;
  toast: (kind: ToastKind, title: string, detail?: string) => void;
  dismissToast: (id: number) => void;
}

const THEME_KEY = 'code-exploder-theme';

function applyTheme(theme: Theme) {
  document.documentElement.dataset.theme = theme;
  localStorage.setItem(THEME_KEY, theme);
}

const initialTheme = (localStorage.getItem(THEME_KEY) as Theme | null) ?? 'dark';
applyTheme(initialTheme);

let nextToastId = 1;

export const useUi = create<UiState>((set) => ({
  theme: initialTheme,
  toasts: [],
  setTheme: (theme) => {
    applyTheme(theme);
    set({ theme });
  },
  toast: (kind, title, detail) =>
    set((state) => {
      const toast: Toast = { id: nextToastId++, kind, title, detail, sticky: kind === 'error' };
      // Max 3 on screen: drop the oldest non-sticky first.
      const toasts = [...state.toasts, toast];
      while (toasts.length > 3) {
        const idx = toasts.findIndex((t) => !t.sticky);
        if (idx < 0) break;
        toasts.splice(idx, 1);
      }
      return { toasts };
    }),
  dismissToast: (id) => set((state) => ({ toasts: state.toasts.filter((t) => t.id !== id) })),
}));
