export interface ParsedGitHubUrl {
  kind: 'repo' | 'pr';
  owner: string;
  repo: string;
  prNumber: number | null;
}

const NAME = /^[A-Za-z0-9_.-]+$/;

/**
 * Client-side parse of github.com/{owner}/{repo} and .../pull/{n} URLs.
 * Accepts an omitted scheme and a `.git` suffix; anything else returns null.
 */
export function parseGitHubUrl(input: string): ParsedGitHubUrl | null {
  const trimmed = input.trim();
  if (!trimmed) return null;

  const candidate = /^https?:\/\//i.test(trimmed) ? trimmed : `https://${trimmed}`;
  let url: URL;
  try {
    url = new URL(candidate);
  } catch {
    return null;
  }

  const host = url.hostname.toLowerCase();
  if (host !== 'github.com' && host !== 'www.github.com') return null;

  const segments = url.pathname.split('/').filter(Boolean);
  if (segments.length < 2) return null;

  const owner = segments[0];
  const repo = segments[1].replace(/\.git$/i, '');
  if (!NAME.test(owner) || !NAME.test(repo)) return null;

  if (segments.length === 2) {
    return { kind: 'repo', owner, repo, prNumber: null };
  }

  // Allow trailing segments after the PR number (e.g. /files, /commits).
  if (segments[2] === 'pull' && segments.length >= 4) {
    const n = Number(segments[3]);
    if (Number.isInteger(n) && n > 0) {
      return { kind: 'pr', owner, repo, prNumber: n };
    }
  }

  return null;
}
