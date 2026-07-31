import { applyReveal, buildDiagramMap, computeReveal, supportsReveal } from './stagedDiagramUtils';
import type { DiagramData, DiagramStage } from '../../../api/types';

const stages: DiagramStage[] = [
  { title: 'Entry', narrationMd: 'a', reveal: { nodes: ['ui'], edges: [] } },
  { title: 'Routing', narrationMd: 'b', reveal: { nodes: ['router'], edges: [0] } },
  { title: 'Middleware', narrationMd: 'c', reveal: { nodes: ['mw'], edges: [1] } },
  { title: 'Storage', narrationMd: 'd', reveal: { nodes: ['cache', 'db'], edges: [2, 3] } },
];

describe('computeReveal', () => {
  it('stage 0 reveals only the first stage set', () => {
    const r = computeReveal(stages, 0);
    expect([...r.nodes]).toEqual(['ui']);
    expect(r.edges.size).toBe(0);
  });

  it('stage k reveals the union of stages 0..k', () => {
    const r = computeReveal(stages, 2);
    expect([...r.nodes].sort()).toEqual(['mw', 'router', 'ui']);
    expect([...r.edges].sort()).toEqual([0, 1]);
  });

  it('the last stage reveals everything', () => {
    const r = computeReveal(stages, 3);
    expect(r.nodes.size).toBe(5);
    expect(r.edges.size).toBe(4);
  });

  it('clamps an out-of-range stage to the last stage', () => {
    const r = computeReveal(stages, 99);
    expect(r.nodes.size).toBe(5);
  });
});

function flowchartSvg(): SVGSVGElement {
  const host = document.createElement('div');
  host.innerHTML = `
    <svg>
      <g class="edgePaths">
        <path id="e0"></path>
        <path id="e1"></path>
      </g>
      <g class="edgeLabels"><g class="edgeLabel"></g><g class="edgeLabel"></g></g>
      <g class="nodes">
        <g class="node" id="flowchart-ui-1"></g>
        <g class="node" id="flowchart-router-3"></g>
      </g>
    </svg>`;
  return host.querySelector('svg') as SVGSVGElement;
}

const flowchartData = (s: DiagramStage[]): DiagramData => ({
  diagramKind: 'flowchart',
  title: 't',
  mermaid: '',
  stages: s,
});

describe('buildDiagramMap / applyReveal', () => {
  const twoStages: DiagramStage[] = [
    { title: '1', narrationMd: '', reveal: { nodes: ['ui'], edges: [] } },
    { title: '2', narrationMd: '', reveal: { nodes: ['router'], edges: [0, 1] } },
  ];

  it('maps flowchart node ids and edge order, and reports complete', () => {
    const map = buildDiagramMap(flowchartSvg(), flowchartData(twoStages));
    expect(map.complete).toBe(true);
    expect([...map.nodes.keys()].sort()).toEqual(['router', 'ui']);
    expect(map.edges).toHaveLength(2);
  });

  it('reports incomplete when a referenced node is missing (fallback trigger)', () => {
    const missing: DiagramStage[] = [
      { title: '1', narrationMd: '', reveal: { nodes: ['ghost-node'], edges: [] } },
    ];
    const map = buildDiagramMap(flowchartSvg(), flowchartData(missing));
    expect(map.complete).toBe(false);
  });

  it('ghosts exactly the not-yet-revealed elements, and clears on revealAll', () => {
    const svg = flowchartSvg();
    const map = buildDiagramMap(svg, flowchartData(twoStages));

    applyReveal(map, computeReveal(twoStages, 0), false);
    expect(svg.querySelector('#flowchart-ui-1')!.classList.contains('ghost')).toBe(false);
    expect(svg.querySelector('#flowchart-router-3')!.classList.contains('ghost')).toBe(true);
    expect(svg.querySelector('#e0')!.classList.contains('ghost')).toBe(true);

    applyReveal(map, computeReveal(twoStages, 1), false);
    expect(svg.querySelector('#flowchart-router-3')!.classList.contains('ghost')).toBe(false);
    expect(svg.querySelector('#e0')!.classList.contains('ghost')).toBe(false);

    applyReveal(map, computeReveal(twoStages, 0), true);
    expect(svg.querySelectorAll('.ghost')).toHaveLength(0);
  });
});

describe('timeline diagrams (M9)', () => {
  it('supportsReveal is true only for flowchart and sequence', () => {
    expect(supportsReveal('flowchart')).toBe(true);
    expect(supportsReveal('sequence')).toBe(true);
    expect(supportsReveal('timeline')).toBe(false);
  });

  it('buildDiagramMap skips element mapping for timelines: empty, complete map', () => {
    const svg = flowchartSvg(); // whatever the DOM shape, timelines never map it
    const data: DiagramData = {
      diagramKind: 'timeline',
      title: 'History',
      mermaid: 'timeline\n 2019 : born',
      stages: [
        { title: 'Era 1', narrationMd: 'a', reveal: { nodes: [], edges: [] } },
        { title: 'Era 2', narrationMd: 'b', reveal: { nodes: [], edges: [] } },
      ],
    };
    const map = buildDiagramMap(svg, data);
    expect(map.complete).toBe(true);
    expect(map.nodes.size).toBe(0);
    expect(map.edges).toHaveLength(0);
    // Applying any reveal to an empty map ghosts nothing.
    applyReveal(map, computeReveal(data.stages, 0), false);
    expect(svg.querySelectorAll('.ghost')).toHaveLength(0);
  });
});
