<script lang="ts">
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import type { BackgroundJobDetail, BackgroundJobSummary } from '#lib/background-work/types.ts';
  import BackgroundJobItem from './BackgroundJobItem.svelte';
  import PanelView from './panels/PanelView.svelte';

  type Props = {
    summary: BackgroundJobSummary | null;
    detail: BackgroundJobDetail | null;
    error?: string | null;
    now?: number;
  };
  let { summary, detail, error = null, now = Date.now() }: Props = $props();

  const top = $derived(detail?.summary ?? summary);
  const trendInSheet = $derived(
    detail !== null && !detail.panels.some((p) => p.kind === 'series' && p.bucket === 'day')
  );
</script>

<div class="flex flex-col gap-4 pt-1 pb-2">
  {#if top}
    <BackgroundJobItem job={top} layout="sheet" {now} {trendInSheet} />
  {/if}

  {#if detail}
    {#each detail.panels as panel, i (`${panel.kind}-${i}`)}
      <PanelView {panel} {now} />
    {/each}
  {:else if error}
    <div
      role="alert"
      class="bg-card text-subheadline text-muted-foreground mx-4 rounded-xl px-4 py-6 text-center md:text-sm"
    >
      {error}
    </div>
  {:else}
    <div class="bg-card mx-4 flex flex-col gap-3 rounded-xl p-4" aria-busy="true">
      <Skeleton class="h-4 w-48" />
      <Skeleton class="h-28 w-full" />
    </div>
  {/if}
</div>
