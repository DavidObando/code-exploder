import styles from './ui.module.css';

export function ConfirmDialog({
  title,
  body,
  confirmLabel = 'Delete',
  onConfirm,
  onCancel,
}: {
  title: string;
  body: string;
  confirmLabel?: string;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <div className={styles.modalScrim} onClick={onCancel}>
      <div
        className={styles.modal}
        role="alertdialog"
        aria-modal="true"
        aria-label={title}
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className={styles.modalTitle}>{title}</h2>
        <p style={{ margin: 0, color: 'var(--text-secondary)' }}>{body}</p>
        <div className={styles.modalActions}>
          <button className={styles.buttonGhost} onClick={onCancel} autoFocus>
            Cancel
          </button>
          <button className={styles.buttonDanger} onClick={onConfirm}>
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
