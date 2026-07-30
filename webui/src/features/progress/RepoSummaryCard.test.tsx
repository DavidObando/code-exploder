import { render, screen, cleanup } from '@testing-library/react';
import { RepoSummaryCard, languageSegments } from './RepoSummaryCard';
import type { RepoSummary } from '../../api/types';

const summary: RepoSummary = {
  commitSha: '9b1d2f4a77c0e6d5b3a1f8e2c4d6a8b0c2e4f6a8',
  description: 'The React Framework for the Web',
  fileCount: 4210,
  analyzedFileCount: 3980,
  excludedFileCount: 230,
  chunkCount: 15234,
  totalBytes: 88_000_000,
  languages: [
    { name: 'TypeScript', files: 2900, bytes: 52_000_000, percent: 61.2 },
    { name: 'Rust', files: 400, bytes: 14_000_000, percent: 16.4 },
    { name: 'JavaScript', files: 350, bytes: 9_000_000, percent: 10.5 },
    { name: 'CSS', files: 120, bytes: 3_000_000, percent: 4.1 },
    { name: 'MDX', files: 90, bytes: 2_500_000, percent: 3.2 },
    { name: 'Shell', files: 60, bytes: 1_500_000, percent: 2.1 },
    { name: 'Python', files: 30, bytes: 900_000, percent: 1.5 },
    { name: 'Dockerfile', files: 10, bytes: 300_000, percent: 1.0 },
  ],
  buildSystems: ['pnpm', 'turbo', 'cargo'],
  ciConfigs: ['build_and_test.yml', 'code_freeze.yml'],
  entryPoints: ['packages/next/src/cli/next-start.ts'],
  components: [
    { name: 'packages/next', fileCount: 2100, topFiles: ['packages/next/src/server/next.ts'] },
    { name: 'packages/next-swc', fileCount: 420, topFiles: [] },
    { name: 'docs', fileCount: 380, topFiles: [] },
  ],
  topChurnFiles: [
    { path: 'packages/next/src/server/base-server.ts', commits: 412 },
    { path: 'packages/next/package.json', commits: 388 },
  ],
  commitCount: 28453,
  contributorCount: 3521,
};

afterEach(cleanup);

describe('languageSegments', () => {
  it('keeps the top 6 languages and folds the rest into Other', () => {
    const segments = languageSegments(summary.languages);
    expect(segments).toHaveLength(7);
    expect(segments[0].name).toBe('TypeScript');
    expect(segments[5].name).toBe('Shell');
    expect(segments[6].name).toBe('Other');
    expect(segments[6].percent).toBeCloseTo(2.5);
  });

  it('omits Other when there are 6 or fewer languages', () => {
    const segments = languageSegments(summary.languages.slice(0, 3));
    expect(segments.map((s) => s.name)).toEqual(['TypeScript', 'Rust', 'JavaScript']);
  });
});

describe('RepoSummaryCard', () => {
  it('renders the short SHA and description', () => {
    render(<RepoSummaryCard summary={summary} />);
    expect(screen.getByText('9b1d2f4')).toBeInTheDocument();
    expect(screen.getByText('The React Framework for the Web')).toBeInTheDocument();
  });

  it('renders one bar segment per top language plus Other, sized by percent', () => {
    render(<RepoSummaryCard summary={summary} />);
    const segments = screen.getAllByTestId('lang-segment');
    expect(segments).toHaveLength(7);
    expect(segments[0]).toHaveStyle({ width: '61.2%' });
    expect(screen.getByText('TypeScript')).toBeInTheDocument();
    expect(screen.getByText('Other')).toBeInTheDocument();
    expect(screen.getByText('61.2%')).toBeInTheDocument();
  });

  it('renders the stat row values', () => {
    render(<RepoSummaryCard summary={summary} />);
    expect(screen.getByText('3,980')).toBeInTheDocument(); // files analyzed
    expect(screen.getByText('230')).toBeInTheDocument(); // excluded
    expect(screen.getByText('15,234')).toBeInTheDocument(); // chunks
    expect(screen.getByText('28,453')).toBeInTheDocument(); // commits
    expect(screen.getByText('3,521')).toBeInTheDocument(); // contributors
  });

  it('renders build systems and CI configs as chips', () => {
    render(<RepoSummaryCard summary={summary} />);
    for (const chip of ['pnpm', 'turbo', 'cargo', 'build_and_test.yml', 'code_freeze.yml']) {
      expect(screen.getByText(chip)).toBeInTheDocument();
    }
  });

  it('renders components with file counts and top churn files with commit counts', () => {
    render(<RepoSummaryCard summary={summary} />);
    expect(screen.getByText('packages/next')).toBeInTheDocument();
    expect(screen.getByText('2,100 files')).toBeInTheDocument();
    expect(screen.getByText('packages/next/src/server/base-server.ts')).toBeInTheDocument();
    expect(screen.getByText('412 commits')).toBeInTheDocument();
  });
});
