import type { SidebarsConfig } from '@docusaurus/plugin-content-docs';

const sidebars: SidebarsConfig = {
  docs: [
    'intro',
    {
      type: 'category',
      label: 'Getting started',
      items: ['getting-started/quick-start', 'getting-started/sample'],
    },
    {
      type: 'category',
      label: 'Guides',
      items: [
        'guides/handlers',
        'guides/context-and-testing',
        'guides/fan-out',
        'guides/expansion',
        'guides/configuration',
        'guides/programmatic-control',
      ],
    },
    {
      type: 'category',
      label: 'Architecture',
      items: [
        'architecture/overview',
        'architecture/data-model',
        'architecture/fan-out',
        'architecture/scheduler',
        'architecture/claim',
        'architecture/execution',
        'architecture/settlement-and-recovery',
        'architecture/stats-and-completion',
      ],
    },
    {
      type: 'category',
      label: 'Operations',
      items: [
        'operations/dashboard',
        'operations/observability',
        'operations/production',
        'operations/known-limits',
      ],
    },
    'contributing',
  ],
};

export default sidebars;
