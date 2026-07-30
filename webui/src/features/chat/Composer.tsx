import ui from '../../components/ui.module.css';
import styles from './chat.module.css';

/** Enter sends, Shift+Enter newline. Disabled with a note while a generation runs. */
export function Composer({
  value,
  onChange,
  onSend,
  disabled,
  disabledNote,
  includeContext,
  onToggleContext,
  contextAvailable,
}: {
  value: string;
  onChange: (value: string) => void;
  onSend: () => void;
  disabled: boolean;
  disabledNote?: string;
  includeContext: boolean;
  onToggleContext: (include: boolean) => void;
  contextAvailable: boolean;
}) {
  return (
    <div className={styles.composer}>
      {disabled && disabledNote && <div className={styles.inFlightNote}>{disabledNote}</div>}
      <textarea
        className={styles.composerInput}
        aria-label="Message the expert"
        placeholder="Ask about this codebase…"
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            if (!disabled && value.trim()) onSend();
          }
        }}
      />
      <div className={styles.composerRow}>
        <label className={styles.contextToggle}>
          <input
            type="checkbox"
            checked={includeContext && contextAvailable}
            disabled={!contextAvailable}
            onChange={(e) => onToggleContext(e.target.checked)}
          />
          Include current section as context
        </label>
        <button
          className={`${ui.buttonPrimary} ${styles.sendButton}`}
          onClick={onSend}
          disabled={disabled || !value.trim()}
        >
          Send
        </button>
      </div>
    </div>
  );
}
