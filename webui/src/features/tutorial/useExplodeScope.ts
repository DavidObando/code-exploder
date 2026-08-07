import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '../../api/client';
import { useTutorial } from '../../store/tutorial';
import { useUi } from '../../store/ui';

/**
 * Starts (or, for a failed dive, restarts) a deep dive into a scope. Marks the
 * componentId pending in this tab so live DeepDive* events know their toasts
 * belong to us — eager and other-tab dives stay quiet.
 */
export function useExplodeScope(sessionId: string) {
  const queryClient = useQueryClient();
  const toast = useUi((s) => s.toast);

  return useMutation({
    mutationFn: (componentId: string) => api.explodeScope(sessionId, componentId),
    onMutate: (componentId) => {
      useTutorial.getState().markExplosionPending(componentId);
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['experience', sessionId] });
      void queryClient.invalidateQueries({ queryKey: ['scopes', sessionId] });
    },
    onError: (err, componentId) => {
      useTutorial.getState().clearExplosionPending(componentId);
      if (err instanceof ApiError && err.status === 409) {
        toast('info', 'Deep dive busy', err.message);
        // A stale TOC likely hid the in-flight dive — heal it.
        void queryClient.invalidateQueries({ queryKey: ['experience', sessionId] });
        void queryClient.invalidateQueries({ queryKey: ['scopes', sessionId] });
      } else {
        toast(
          'error',
          'Could not start the deep dive',
          err instanceof ApiError ? err.message : 'Unexpected error',
        );
      }
    },
  });
}
