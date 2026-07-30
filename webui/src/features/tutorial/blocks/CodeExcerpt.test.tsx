import { render, screen, cleanup } from '@testing-library/react';
import { CodeExcerpt, classifyDiffLine } from './CodeExcerpt';
import type { CodeBlockData } from '../../../api/types';

const github = { owner: 'vercel', repo: 'next.js', commitSha: 'abc1234def' };

const diffBlock: CodeBlockData = {
  path: 'server/router.ts',
  startLine: 42,
  endLine: 46,
  language: 'Diff',
  content:
    '@@ -40,6 +42,8 @@ export function route()\n' +
    ' const url = parse(req);\n' +
    '-return legacyMatch(url);\n' +
    '+const table = buildTable();\n' +
    '+return table.match(url);\n',
  captionMd: null,
};

afterEach(cleanup);

describe('classifyDiffLine', () => {
  it('classifies hunk headers, additions, removals, and context', () => {
    expect(classifyDiffLine('@@ -1,2 +3,4 @@')).toBe('hunk');
    expect(classifyDiffLine('+added')).toBe('added');
    expect(classifyDiffLine('-removed')).toBe('removed');
    expect(classifyDiffLine(' context')).toBe('context');
    expect(classifyDiffLine('')).toBe('context');
  });
});

describe('CodeExcerpt Diff variant', () => {
  it('tints added/removed lines and mutes the @@ header', () => {
    const { container } = render(<CodeExcerpt data={diffBlock} github={github} />);
    const rows = Array.from(container.querySelectorAll('[data-diff]'));
    expect(rows.map((r) => r.getAttribute('data-diff'))).toEqual([
      'hunk',
      'context',
      'removed',
      'added',
      'added',
    ]);
    expect(screen.getByText(/-return legacyMatch\(url\);/)).toHaveAttribute(
      'data-diff',
      'removed',
    );
    expect(screen.getByText(/\+return table\.match\(url\);/)).toHaveAttribute(
      'data-diff',
      'added',
    );
    expect(screen.getByText(/@@ -40,6 \+42,8 @@/)).toHaveAttribute('data-diff', 'hunk');
  });

  it('numbers all hunk lines from startLine and keeps the GitHub link', () => {
    render(<CodeExcerpt data={diffBlock} github={github} />);
    expect(screen.getByText('42')).toBeInTheDocument(); // @@ header row
    expect(screen.getByText('46')).toBeInTheDocument(); // last added row
    expect(screen.getByRole('link', { name: 'View on GitHub ↗' })).toHaveAttribute(
      'href',
      'https://github.com/vercel/next.js/blob/abc1234def/server/router.ts#L42-L46',
    );
  });

  it('applies no diff classification to ordinary code blocks', () => {
    const { container } = render(
      <CodeExcerpt
        data={{ ...diffBlock, language: 'typescript', content: '+not a diff\n-really\n' }}
        github={github}
      />,
    );
    expect(container.querySelector('[data-diff]')).toBeNull();
  });
});
