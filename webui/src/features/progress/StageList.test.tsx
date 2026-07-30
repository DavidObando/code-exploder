import { render, screen, cleanup } from '@testing-library/react';
import { StageList } from './StageList';
import type { StageInfo } from '../../api/types';

const stages: StageInfo[] = [
  { key: 'clone', label: 'Fetch & clone', state: 'done', percent: 100, detail: null },
  { key: 'index', label: 'Index', state: 'active', percent: 45, detail: '812 files indexed' },
  { key: 'map', label: 'Map structure', state: 'pending', percent: null, detail: null },
  { key: 'sections', label: 'Write sections', state: 'pending', percent: null, detail: null },
  { key: 'finalize', label: 'Finalize', state: 'pending', percent: null, detail: null },
];

afterEach(cleanup);

describe('StageList', () => {
  it('renders every stage label in order', () => {
    render(<StageList stages={stages} />);
    const items = screen.getAllByRole('listitem');
    expect(items).toHaveLength(5);
    expect(items[0]).toHaveTextContent('Fetch & clone');
    expect(items[1]).toHaveTextContent('Index');
    expect(items[4]).toHaveTextContent('Finalize');
  });

  it('shows a progress bar with percent only on the active stage', () => {
    render(<StageList stages={stages} />);
    const bars = screen.getAllByRole('progressbar');
    expect(bars).toHaveLength(1);
    expect(bars[0]).toHaveAttribute('aria-valuenow', '45');
    expect(screen.getByText('45%')).toBeInTheDocument();
    expect(screen.getByText('812 files indexed')).toBeInTheDocument();
  });

  it('marks done, pending, and failed states with distinct glyphs', () => {
    const withFailure: StageInfo[] = [
      stages[0],
      { ...stages[1], state: 'failed', percent: null },
      ...stages.slice(2),
    ];
    render(<StageList stages={withFailure} />);
    expect(screen.getByText('✓')).toBeInTheDocument();
    expect(screen.getByText('✗')).toBeInTheDocument();
    expect(screen.getAllByText('○')).toHaveLength(3);
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it('prefers a live AnalysisProgress overlay over the snapshot percent', () => {
    render(<StageList stages={stages} live={{ index: { percent: 72, detail: 'embedding chunks' } }} />);
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '72');
    expect(screen.getByText('72%')).toBeInTheDocument();
    expect(screen.getByText('embedding chunks')).toBeInTheDocument();
    expect(screen.queryByText('45%')).not.toBeInTheDocument();
  });
});
