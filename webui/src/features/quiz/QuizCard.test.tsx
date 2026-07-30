import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Mock } from 'vitest';
import { QuizCard } from './QuizCard';
import { ResultFooter } from './ResultFooter';
import { useTutorial } from '../../store/tutorial';
import type { Quiz, QuizAttempt } from '../../api/types';

vi.mock('../../api/client', () => ({
  ApiError: class MockApiError extends Error {
    constructor(
      public status: number,
      message: string,
    ) {
      super(message);
    }
  },
  api: {
    getQuiz: vi.fn(),
    listQuizAttempts: vi.fn(),
    submitQuizAttempt: vi.fn(),
  },
}));

import { api } from '../../api/client';

const mockApi = api as unknown as {
  getQuiz: Mock;
  listQuizAttempts: Mock;
  submitQuizAttempt: Mock;
};

const quiz: Quiz = {
  id: 'q1',
  sectionId: 'sec1',
  title: 'Check your understanding',
  questions: [
    {
      id: 'qq1',
      ord: 0,
      type: 'single',
      prompt: 'Where do requests enter?',
      choices: [
        { key: 'a', text: 'The edge runtime' },
        { key: 'b', text: 'The database' },
      ],
    },
    {
      id: 'qq2',
      ord: 1,
      type: 'multi',
      prompt: 'Which layers cache?',
      choices: [
        { key: 'x', text: 'Router' },
        { key: 'y', text: 'Store' },
      ],
    },
    { id: 'qq3', ord: 2, type: 'boolean', prompt: 'Middleware runs first.' },
    { id: 'qq4', ord: 3, type: 'short', prompt: 'Explain the cache layer.', maxWords: 5 },
  ],
};

function gradedAttempt(overrides: Partial<QuizAttempt> = {}): QuizAttempt {
  return {
    id: 'att1',
    submittedAt: '2026-07-30T12:00:00Z',
    status: 'graded',
    scorePct: 67,
    perQuestion: [
      { questionId: 'qq1', correct: true, excluded: false, feedbackMd: 'Right — the edge.' },
      { questionId: 'qq2', correct: false, excluded: false, feedbackMd: 'The router never caches.' },
      { questionId: 'qq3', correct: true, excluded: false, feedbackMd: null },
      { questionId: 'qq4', correct: null, excluded: true, feedbackMd: 'Too short to grade.' },
    ],
    ...overrides,
  };
}

function renderCard(onReread?: () => void) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <QuizCard quizId="q1" onReread={onReread} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  useTutorial.setState({ skippedQuizzes: {} });
  mockApi.getQuiz.mockResolvedValue(quiz);
  mockApi.listQuizAttempts.mockResolvedValue([]);
});

afterEach(cleanup);

describe('QuizCard form', () => {
  it('renders every question type', async () => {
    renderCard();
    expect(await screen.findByText('Check your understanding')).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'The edge runtime' })).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Router' })).toBeInTheDocument();
    expect(screen.getByText('Select all that apply')).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'True' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'False' })).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Explain the cache layer.' })).toBeInTheDocument();
    expect(screen.getByText('0 / 5 words')).toBeInTheDocument();
  });

  it('counts words live and soft-warns over maxWords without blocking submit', async () => {
    renderCard();
    const textarea = await screen.findByRole('textbox', { name: 'Explain the cache layer.' });
    fireEvent.change(textarea, {
      target: { value: 'one two three four five six seven' },
    });
    expect(screen.getByText(/7 \/ 5 words — consider trimming/)).toBeInTheDocument();
  });

  it('disables submit until every deterministic question is answered, then POSTs the payload shape', async () => {
    mockApi.submitQuizAttempt.mockResolvedValue(gradedAttempt());
    renderCard();
    const submitButton = await screen.findByRole('button', { name: 'Submit answers' });
    expect(submitButton).toBeDisabled();

    fireEvent.click(screen.getByRole('radio', { name: 'The edge runtime' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Router' }));
    expect(submitButton).toBeDisabled(); // boolean still unanswered; short may stay blank
    fireEvent.click(screen.getByRole('radio', { name: 'True' }));
    expect(submitButton).toBeEnabled();

    fireEvent.click(submitButton);
    await waitFor(() => expect(mockApi.submitQuizAttempt).toHaveBeenCalledTimes(1));
    expect(mockApi.submitQuizAttempt).toHaveBeenCalledWith('q1', {
      answers: [
        { questionId: 'qq1', choiceKeys: ['a'] },
        { questionId: 'qq2', choiceKeys: ['x'] },
        { questionId: 'qq3', choiceKeys: ['true'] },
        { questionId: 'qq4', text: '' },
      ],
    });
  });

  it('Skip quiz collapses the card without any server call', async () => {
    renderCard();
    fireEvent.click(await screen.findByRole('button', { name: 'Skip quiz' }));
    expect(screen.getByText('Quiz hidden.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Submit answers' })).not.toBeInTheDocument();
    expect(mockApi.submitQuizAttempt).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Show quiz' }));
    expect(await screen.findByRole('button', { name: 'Submit answers' })).toBeInTheDocument();
  });
});

describe('QuizCard results', () => {
  it('shows the latest attempt on mount with correct/incorrect/excluded row states', async () => {
    mockApi.listQuizAttempts.mockResolvedValue([gradedAttempt()]);
    renderCard();
    const correctRow = (await screen.findByText('Where do requests enter?')).closest('[data-result]');
    expect(correctRow).toHaveAttribute('data-result', 'correct');
    expect(screen.getByText('Which layers cache?').closest('[data-result]')).toHaveAttribute(
      'data-result',
      'incorrect',
    );
    expect(screen.getByText('Explain the cache layer.').closest('[data-result]')).toHaveAttribute(
      'data-result',
      'excluded',
    );
    expect(screen.getByText("Couldn't be graded — not counted.")).toBeInTheDocument();
    expect(screen.getByText('Right — the edge.')).toBeInTheDocument();
    expect(screen.getByText('The router never caches.')).toBeInTheDocument();
  });

  it('shows the pending-grading state for an ungraded short answer', async () => {
    mockApi.listQuizAttempts.mockResolvedValue([
      gradedAttempt({
        status: 'grading',
        scorePct: null,
        perQuestion: [
          { questionId: 'qq1', correct: true, excluded: false, feedbackMd: null },
          { questionId: 'qq2', correct: true, excluded: false, feedbackMd: null },
          { questionId: 'qq3', correct: true, excluded: false, feedbackMd: null },
          { questionId: 'qq4', correct: null, excluded: false, feedbackMd: null },
        ],
      }),
    ]);
    renderCard();
    expect(await screen.findByText('The expert is reading your answer…')).toBeInTheDocument();
    expect(screen.getByText('Score pending…')).toBeInTheDocument();
  });

  it('retake resets to a fresh, unanswered form', async () => {
    mockApi.listQuizAttempts.mockResolvedValue([gradedAttempt()]);
    renderCard();
    fireEvent.click(await screen.findByRole('button', { name: 'Retake quiz' }));
    const submitButton = screen.getByRole('button', { name: 'Submit answers' });
    expect(submitButton).toBeDisabled();
    expect(screen.getByRole('radio', { name: 'The edge runtime' })).not.toBeChecked();
    expect(screen.getByRole('textbox', { name: 'Explain the cache layer.' })).toHaveValue('');
  });
});

describe('ResultFooter', () => {
  it('shows score as correct/gradable with percent and a reread link on any miss', () => {
    const onReread = vi.fn();
    render(<ResultFooter attempt={gradedAttempt()} onRetake={() => {}} onReread={onReread} />);
    expect(screen.getByText('2/3 — 67%')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Reread this section ↑' }));
    expect(onReread).toHaveBeenCalledTimes(1);
    expect(screen.queryByText('Section completed ✓')).not.toBeInTheDocument();
  });

  it('shows "Section completed" at or above 75%', () => {
    const passing = gradedAttempt({
      scorePct: 80,
      perQuestion: [
        { questionId: 'qq1', correct: true, excluded: false, feedbackMd: null },
        { questionId: 'qq2', correct: true, excluded: false, feedbackMd: null },
        { questionId: 'qq3', correct: true, excluded: false, feedbackMd: null },
        { questionId: 'qq4', correct: false, excluded: false, feedbackMd: null },
      ],
    });
    render(<ResultFooter attempt={passing} onRetake={() => {}} />);
    expect(screen.getByText('3/4 — 80%')).toBeInTheDocument();
    expect(screen.getByText('Section completed ✓')).toBeInTheDocument();
  });
});
