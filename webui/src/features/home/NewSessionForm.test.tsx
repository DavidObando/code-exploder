import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { NewSessionForm } from './NewSessionForm';
import { parseGitHubUrl } from './parseGitHubUrl';

function renderForm() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <NewSessionForm />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(cleanup);

describe('parseGitHubUrl', () => {
  it('parses a repo URL', () => {
    expect(parseGitHubUrl('https://github.com/vercel/next.js')).toEqual({
      kind: 'repo',
      owner: 'vercel',
      repo: 'next.js',
      prNumber: null,
    });
  });

  it('parses a PR URL', () => {
    expect(parseGitHubUrl('https://github.com/ollama/ollama/pull/812')).toEqual({
      kind: 'pr',
      owner: 'ollama',
      repo: 'ollama',
      prNumber: 812,
    });
  });

  it('accepts scheme-less input and .git suffixes', () => {
    expect(parseGitHubUrl('github.com/redis/redis.git')?.repo).toBe('redis');
  });

  it('rejects non-GitHub and malformed URLs', () => {
    expect(parseGitHubUrl('https://gitlab.com/foo/bar')).toBeNull();
    expect(parseGitHubUrl('https://github.com/only-owner')).toBeNull();
    expect(parseGitHubUrl('https://github.com/foo/bar/issues/1')).toBeNull();
    expect(parseGitHubUrl('not a url')).toBeNull();
  });
});

describe('NewSessionForm', () => {
  it('shows a REPO chip and enables submit for a repo URL', () => {
    renderForm();
    fireEvent.change(screen.getByLabelText('GitHub repository or pull request URL'), {
      target: { value: 'https://github.com/vercel/next.js' },
    });
    expect(screen.getByTestId('kind-chip')).toHaveTextContent('REPO');
    expect(screen.getByRole('button', { name: 'Start analysis' })).toBeEnabled();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows a PR chip with the number for a pull request URL', () => {
    renderForm();
    fireEvent.change(screen.getByLabelText('GitHub repository or pull request URL'), {
      target: { value: 'https://github.com/ollama/ollama/pull/812' },
    });
    expect(screen.getByTestId('kind-chip')).toHaveTextContent('PR #812');
    expect(screen.getByRole('button', { name: 'Start analysis' })).toBeEnabled();
  });

  it('shows an inline error and disables submit for an invalid URL', () => {
    renderForm();
    fireEvent.change(screen.getByLabelText('GitHub repository or pull request URL'), {
      target: { value: 'https://gitlab.com/foo/bar' },
    });
    expect(screen.queryByTestId('kind-chip')).not.toBeInTheDocument();
    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Start analysis' })).toBeDisabled();
  });

  it('disables submit while the URL is empty', () => {
    renderForm();
    expect(screen.getByRole('button', { name: 'Start analysis' })).toBeDisabled();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
