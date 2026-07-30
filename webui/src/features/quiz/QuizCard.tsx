import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '../../api/client';
import type { QuizAnswer, QuizAttempt, QuizAttemptRequest, QuizQuestion } from '../../api/types';
import { useTutorial } from '../../store/tutorial';
import { useUi } from '../../store/ui';
import ui from '../../components/ui.module.css';
import { QuestionForm, QuestionResult } from './QuestionRow';
import { ResultFooter } from './ResultFooter';
import styles from './quiz.module.css';

function isAnswered(question: QuizQuestion, answer: QuizAnswer | undefined): boolean {
  if (question.type === 'short') return true; // may be left blank → excluded server-side
  return (answer?.choiceKeys?.length ?? 0) > 0;
}

export function buildAttemptPayload(
  questions: QuizQuestion[],
  answers: Record<string, QuizAnswer>,
): QuizAttemptRequest {
  return {
    answers: [...questions]
      .sort((a, b) => a.ord - b.ord)
      .map((q) =>
        q.type === 'short'
          ? { questionId: q.id, text: answers[q.id]?.text ?? '' }
          : { questionId: q.id, choiceKeys: answers[q.id]?.choiceKeys ?? [] },
      ),
  };
}

/**
 * Inline comprehension checkpoint at the end of a ready section (docs/05
 * §Quizzes) — optional, unlimited retakes, never gates navigation.
 */
export function QuizCard({ quizId, onReread }: { quizId: string; onReread?: () => void }) {
  const queryClient = useQueryClient();
  const toast = useUi((s) => s.toast);
  const skipped = useTutorial((s) => !!s.skippedQuizzes[quizId]);
  const setQuizSkipped = useTutorial((s) => s.setQuizSkipped);

  const quiz = useQuery({
    queryKey: ['quiz', quizId],
    queryFn: () => api.getQuiz(quizId),
  });

  // Newest first; polls as a backup while a short answer is being graded (the
  // QuizGraded hub event is the primary refresh path).
  const attempts = useQuery({
    queryKey: ['attempts', quizId],
    queryFn: () => api.listQuizAttempts(quizId),
    refetchInterval: (query) => (query.state.data?.[0]?.status === 'grading' ? 4000 : false),
  });

  const [answers, setAnswers] = useState<Record<string, QuizAnswer>>({});
  const [retaking, setRetaking] = useState(false);
  // Answers echoed in results — only known for attempts submitted this mount.
  const [submittedAnswers, setSubmittedAnswers] = useState<Record<string, QuizAnswer>>({});

  const submit = useMutation({
    mutationFn: (body: QuizAttemptRequest) => api.submitQuizAttempt(quizId, body),
    onSuccess: (attempt, body) => {
      queryClient.setQueryData<QuizAttempt[]>(['attempts', quizId], (old) => [
        attempt,
        ...(old ?? []),
      ]);
      setSubmittedAnswers(Object.fromEntries(body.answers.map((a) => [a.questionId, a])));
      setRetaking(false);
      setAnswers({});
    },
    onError: (err) =>
      toast(
        'error',
        'Could not submit quiz',
        err instanceof ApiError ? err.message : 'Unexpected error',
      ),
  });

  if (skipped) {
    return (
      <div className={styles.skippedRow}>
        Quiz hidden.
        <button className={styles.skipLink} onClick={() => setQuizSkipped(quizId, false)}>
          Show quiz
        </button>
      </div>
    );
  }

  if (quiz.isPending || attempts.isPending) return null;
  if (quiz.isError || !quiz.data) return null;

  const questions = [...quiz.data.questions].sort((a, b) => a.ord - b.ord);
  const latest = attempts.data?.[0] ?? null;
  const showResults = latest !== null && !retaking;

  return (
    <section className={styles.card} aria-label="Section quiz">
      <div className={styles.header}>
        <h2 className={styles.title}>Check your understanding</h2>
        <span className={styles.optional}>optional</span>
        {!showResults && (
          <button className={styles.skipLink} onClick={() => setQuizSkipped(quizId, true)}>
            Skip quiz
          </button>
        )}
      </div>

      {showResults ? (
        <>
          {questions.map((q) => (
            <QuestionResult
              key={q.id}
              question={q}
              result={latest.perQuestion.find((p) => p.questionId === q.id)}
              answer={submittedAnswers[q.id]}
              grading={latest.status === 'grading'}
            />
          ))}
          <ResultFooter
            attempt={latest}
            onRetake={() => {
              setAnswers({});
              setRetaking(true);
            }}
            onReread={onReread}
          />
        </>
      ) : (
        <>
          {questions.map((q) => (
            <QuestionForm
              key={q.id}
              question={q}
              answer={answers[q.id]}
              onChange={(a) => setAnswers((prev) => ({ ...prev, [q.id]: a }))}
            />
          ))}
          <div className={styles.actions}>
            <button
              className={ui.buttonPrimary}
              disabled={
                submit.isPending || !questions.every((q) => isAnswered(q, answers[q.id]))
              }
              onClick={() => submit.mutate(buildAttemptPayload(questions, answers))}
            >
              {submit.isPending ? 'Submitting…' : 'Submit answers'}
            </button>
          </div>
        </>
      )}
    </section>
  );
}
