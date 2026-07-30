import type { QuizAnswer, QuizAttempt, QuizQuestion } from '../../api/types';
import { MarkdownBlock } from '../tutorial/blocks/MarkdownBlock';
import styles from './quiz.module.css';

export type PerQuestionResult = QuizAttempt['perQuestion'][number];

export function countWords(text: string): number {
  return text.trim().split(/\s+/).filter(Boolean).length;
}

const BOOLEAN_CHOICES = [
  { key: 'true', text: 'True' },
  { key: 'false', text: 'False' },
];

function choicesFor(question: QuizQuestion) {
  if (question.type === 'boolean') return BOOLEAN_CHOICES;
  if (question.type === 'short') return [];
  return question.choices;
}

/** Form mode: radios / checkboxes / true-false / bounded textarea. */
export function QuestionForm({
  question,
  answer,
  onChange,
}: {
  question: QuizQuestion;
  answer: QuizAnswer | undefined;
  onChange: (answer: QuizAnswer) => void;
}) {
  const selected = new Set(answer?.choiceKeys ?? []);

  if (question.type === 'short') {
    const text = answer?.text ?? '';
    const words = countWords(text);
    const over = words > question.maxWords;
    return (
      <div className={styles.question}>
        <p className={styles.prompt}>{question.prompt}</p>
        <span className={styles.hint}>In your own words — optional</span>
        <textarea
          className={styles.shortInput}
          value={text}
          onChange={(e) => onChange({ questionId: question.id, text: e.target.value })}
          aria-label={question.prompt}
        />
        <span className={over ? styles.wordCountOver : styles.wordCount}>
          {words} / {question.maxWords} words
          {over && ' — consider trimming'}
        </span>
      </div>
    );
  }

  const multi = question.type === 'multi';
  const toggle = (key: string) => {
    if (multi) {
      const next = new Set(selected);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      onChange({ questionId: question.id, choiceKeys: [...next] });
    } else {
      onChange({ questionId: question.id, choiceKeys: [key] });
    }
  };

  return (
    <fieldset className={styles.question} style={{ border: 'none', margin: 0 }}>
      <legend className={styles.prompt}>{question.prompt}</legend>
      {multi && <span className={styles.hint}>Select all that apply</span>}
      <ul className={styles.choices}>
        {choicesFor(question).map((choice) => (
          <li key={choice.key}>
            <label className={selected.has(choice.key) ? styles.choiceSelected : styles.choice}>
              <input
                type={multi ? 'checkbox' : 'radio'}
                name={question.id}
                value={choice.key}
                checked={selected.has(choice.key)}
                onChange={() => toggle(choice.key)}
              />
              {choice.text}
            </label>
          </li>
        ))}
      </ul>
    </fieldset>
  );
}

/**
 * Result mode. Answer keys never reach the client, so the learner's own answer
 * (when known locally) is echoed and feedbackMd carries the correction.
 */
export function QuestionResult({
  question,
  result,
  answer,
  grading,
}: {
  question: QuizQuestion;
  result: PerQuestionResult | undefined;
  answer: QuizAnswer | undefined;
  grading: boolean;
}) {
  const pending = grading && result?.correct === null && !result.excluded;
  const excluded = result?.excluded ?? false;

  const rowClass = excluded
    ? styles.question
    : pending
      ? styles.questionPending
      : result?.correct === true
        ? styles.questionCorrect
        : result?.correct === false
          ? styles.questionIncorrect
          : styles.question;

  const glyph = excluded
    ? '·'
    : pending
      ? '◐'
      : result?.correct === true
        ? '✓'
        : result?.correct === false
          ? '✕'
          : '·';

  const glyphColor = excluded
    ? 'var(--text-muted)'
    : pending
      ? 'var(--suggestion)'
      : result?.correct === true
        ? 'var(--keep)'
        : result?.correct === false
          ? 'var(--error)'
          : 'var(--text-muted)';

  const echo =
    question.type === 'short'
      ? answer?.text?.trim() || null
      : (answer?.choiceKeys ?? [])
          .map((key) => choicesFor(question).find((c) => c.key === key)?.text ?? key)
          .join(', ') || null;

  return (
    <div className={rowClass} data-result={excluded ? 'excluded' : pending ? 'pending' : result?.correct === true ? 'correct' : result?.correct === false ? 'incorrect' : 'unknown'}>
      <p className={styles.prompt}>
        <span className={styles.resultGlyph} style={{ color: glyphColor }} aria-hidden="true">
          {glyph}
        </span>
        {question.prompt}
      </p>
      {echo && <span className={styles.answerEcho}>Your answer: {echo}</span>}
      {pending && <span className={styles.pendingNote}>The expert is reading your answer…</span>}
      {excluded && (
        <span className={styles.excludedNote}>Couldn't be graded — not counted.</span>
      )}
      {result?.feedbackMd && (
        <div className={styles.feedback}>
          <MarkdownBlock md={result.feedbackMd} />
        </div>
      )}
    </div>
  );
}
