import type { Block } from '../../../api/types';
import { MarkdownBlock } from './MarkdownBlock';
import { CodeExcerpt } from './CodeExcerpt';
import type { GithubContext } from './CodeExcerpt';
import { Callout } from './Callout';
import { StagedDiagram } from './StagedDiagram';

/**
 * Renders one content block. `firstDiagramId` marks the section's first diagram
 * block, which owns the ?stage deep link and the [ / ] shortcuts.
 */
export function BlockRenderer({
  block,
  github,
  firstDiagramId = null,
}: {
  block: Block;
  github: GithubContext | null;
  firstDiagramId?: string | null;
}) {
  switch (block.type) {
    case 'markdown':
      return <MarkdownBlock md={block.data.md} />;
    case 'code':
      return <CodeExcerpt data={block.data} github={github} />;
    case 'callout':
      return <Callout data={block.data} />;
    case 'diagram':
      return (
        <StagedDiagram
          data={block.data}
          blockId={block.id}
          deepLink={block.id === firstDiagramId}
        />
      );
  }
}
