import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '../../api/client';
import { useUi } from '../../store/ui';
import { parseGitHubUrl } from './parseGitHubUrl';
import ui from '../../components/ui.module.css';
import styles from './home.module.css';

export function NewSessionForm() {
  const [url, setUrl] = useState('');
  const [gitRef, setGitRef] = useState('');
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const toast = useUi((s) => s.toast);

  const parsed = parseGitHubUrl(url);
  const showError = url.trim().length > 0 && !parsed;

  const create = useMutation({
    mutationFn: () =>
      api.createSession({ url: url.trim(), gitRef: gitRef.trim() || undefined }),
    onSuccess: (session) => {
      void queryClient.invalidateQueries({ queryKey: ['sessions'] });
      navigate(`/sessions/${session.id}/progress`);
    },
    onError: (err) => {
      toast('error', 'Could not start analysis', err instanceof ApiError ? err.message : 'Unexpected error');
    },
  });

  return (
    <form
      className={styles.form}
      onSubmit={(e) => {
        e.preventDefault();
        if (parsed && !create.isPending) create.mutate();
      }}
    >
      <div className={styles.urlRow}>
        <input
          className={styles.urlInput}
          type="text"
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          placeholder="https://github.com/owner/repo"
          aria-label="GitHub repository or pull request URL"
          spellCheck={false}
          autoFocus
        />
        {parsed && (
          <span className={ui.chip} data-testid="kind-chip">
            {parsed.kind === 'pr' ? `PR #${parsed.prNumber}` : 'REPO'}
          </span>
        )}
      </div>
      {showError && (
        <p className={styles.parseError} role="alert">
          Enter a GitHub repository URL (github.com/owner/repo) or pull request URL (…/pull/123).
        </p>
      )}
      <div className={styles.refRow}>
        <label htmlFor="git-ref">branch / tag (optional)</label>
        <input
          id="git-ref"
          className={styles.refInput}
          type="text"
          value={gitRef}
          onChange={(e) => setGitRef(e.target.value)}
          placeholder="main"
          spellCheck={false}
          disabled={parsed?.kind === 'pr'}
        />
      </div>
      <div className={styles.actions}>
        <button className={ui.buttonPrimary} type="submit" disabled={!parsed || create.isPending}>
          {create.isPending ? 'Starting…' : 'Start analysis'}
        </button>
      </div>
    </form>
  );
}
