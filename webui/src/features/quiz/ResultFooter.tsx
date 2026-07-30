import type { QuizAttempt } from '../../api/types';
import ui from '../../components/ui.module.css';
import styles from './quiz.module.css';

export const PASS_PCT = 75;

/** Score, unlimited retake, reread-on-miss. Never an exam: no gating anywhere. */
export function ResultFooter({
  attempt,
  onRetake,
  onReread,
}: {
  attempt: QuizAttempt;
  onRetake: () => void;
  onReread?: () => void;
}) {
  const gradable = attempt.perQuestion.filter((q) => !q.excluded);
  const correct = gradable.filter((q) => q.correct === true).length;
  const anyMiss = attempt.perQuestion.some((q) => q.correct === false);
  const passed = attempt.scorePct !== null && attempt.scorePct >= PASS_PCT;

  return (
    <div className={styles.footer}>
      {attempt.scorePct !== null ? (
        <span className={styles.score}>
          {correct}/{gradable.length} — {Math.round(attempt.scorePct)}%
        </span>
      ) : (
        <span className={styles.pendingNote}>Score pending…</span>
      )}
      {passed && <span className={styles.completedNote}>Section completed ✓</span>}
      {anyMiss && onReread && (
        <button className={styles.rereadLink} onClick={onReread}>
          Reread this section ↑
        </button>
      )}
      <div className={styles.footerRight}>
        <button className={ui.button} onClick={onRetake}>
          Retake quiz
        </button>
      </div>
    </div>
  );
}
