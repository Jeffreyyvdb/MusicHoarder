<script lang="ts">
  import type { BackgroundPanel } from '#lib/background-work/types.ts';
  import BarsPanel from './BarsPanel.svelte';
  import BreakdownPanel from './BreakdownPanel.svelte';
  import FiguresPanel from './FiguresPanel.svelte';
  import LibraryMapPanel from './LibraryMapPanel.svelte';
  import SeriesPanel from './SeriesPanel.svelte';

  type Props = { panel: BackgroundPanel; now?: number };
  let { panel, now }: Props = $props();

  // A kind with no branch below makes `panel` something other than `never` here, so it fails `bun run check`.
  const unhandled = (panel: never): never => {
    throw new Error(`No view for panel ${JSON.stringify(panel)}`);
  };
</script>

{#if panel.kind === 'breakdown'}
  <BreakdownPanel {panel} />
{:else if panel.kind === 'series'}
  <SeriesPanel {panel} />
{:else if panel.kind === 'library-map'}
  <LibraryMapPanel {panel} />
{:else if panel.kind === 'bars'}
  <BarsPanel {panel} />
{:else if panel.kind === 'figures'}
  <FiguresPanel {panel} {now} />
{:else}
  {unhandled(panel)}
{/if}
