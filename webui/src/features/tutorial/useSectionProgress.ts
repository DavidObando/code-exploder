import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '../../api/client';
import type { ExperienceToc, SectionUserState } from '../../api/types';
import { useUi } from '../../store/ui';

/**
 * PUT section progress with an optimistic myState patch on the TOC. Server
 * reconciles on settle; ['sessions'] refreshes because completion counts moved.
 */
export function useSectionProgress(sessionId: string) {
  const queryClient = useQueryClient();
  const toast = useUi((s) => s.toast);

  return useMutation({
    mutationFn: ({ sectionId, state }: { sectionId: string; state: SectionUserState }) =>
      api.setSectionProgress(sectionId, state),
    onMutate: async ({ sectionId, state }) => {
      await queryClient.cancelQueries({ queryKey: ['experience', sessionId] });
      const previous = queryClient.getQueryData<ExperienceToc>(['experience', sessionId]);
      if (previous) {
        queryClient.setQueryData<ExperienceToc>(['experience', sessionId], {
          ...previous,
          sections: previous.sections.map((s) =>
            s.id === sectionId ? { ...s, myState: state } : s,
          ),
        });
      }
      return { previous };
    },
    onError: (err, _vars, context) => {
      if (context?.previous) {
        queryClient.setQueryData(['experience', sessionId], context.previous);
      }
      toast(
        'error',
        'Could not save progress',
        err instanceof ApiError ? err.message : 'Unexpected error',
      );
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: ['experience', sessionId] });
      void queryClient.invalidateQueries({ queryKey: ['sessions'] });
    },
  });
}
