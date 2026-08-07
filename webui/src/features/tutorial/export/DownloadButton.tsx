import type { ExperienceToc, SessionSummary } from '../../../api/types';
import ui from '../../../components/ui.module.css';
import { useDownloadStatic } from './useDownloadStatic';

/**
 * Downloads the reading tour as a single self-contained HTML file for offline
 * reading. Disabled until at least one section is ready.
 */
export function DownloadButton({ toc, session }: { toc: ExperienceToc; session: SessionSummary }) {
  const { download, isExporting } = useDownloadStatic(toc, session);
  const hasReady = toc.sections.some((s) => s.status === 'ready');

  return (
    <button
      className={ui.buttonGhost}
      onClick={download}
      disabled={isExporting || !hasReady}
      title="Download a self-contained HTML copy you can read offline"
    >
      {isExporting ? 'Preparing…' : '⤓ Download'}
    </button>
  );
}
